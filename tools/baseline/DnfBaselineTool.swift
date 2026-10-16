// DnfBaselineTool:基线录制用的最小权限小工具(原创代码)。
//
// 只做几件事:列出窗口、给窗口截图(含连拍)、把窗口所属应用切到前台、向指定窗口所属进程发送受限的键盘/鼠标事件。
// 最小权限约束(写死在代码里):
//   - 窗口所属进程名必须包含 wine / dnf / crossover 之一,否则拒绝操作;因此它碰不到你的浏览器、聊天窗口或 Cursor;
//   - 键盘脚本只允许方向键、Esc、<wait>、<hold:方向键:毫秒>,以及 --allow-plain 列出的字母(默认只有 x 与 c);
//     回车必须显式加 --allow-return;其余一律拒绝;
//   - 默认事件只投递给目标进程(postToPid),不会落到别的应用;--hid 走系统事件通道,只在目标应用已是前台时才发。
//
// 权限归属:作为独立应用经 `open` 启动,系统授权("屏幕录制"、"辅助功能")记在本工具名下,
// 而不是 Cursor 名下。构建与调用见 tools/baseline/build-tool.sh 与 tools/baseline/tool.sh。
//
// 2026-10-16 改动:`front` 改用辅助功能接口(macOS 15 对后台调用方的 NSRunningApplication.activate 一概不放行,证据 E-040);
// 加 `burst`(同一进程里定时输入 + 窗口连拍,用 ScreenCaptureKit,CGWindowListCreateImage 在 macOS 15 已移除);
// key / click 可加 --front 与 --shot,一次调用完成"切前台、输入、等一等、截图"。
import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import ImageIO
import ScreenCaptureKit
import UniformTypeIdentifiers

// MARK: - 约束与工具函数

let allowedOwnerFragments = ["wine", "dnf", "crossover"]

func isAllowedOwner(_ owner: String) -> Bool {
    let lower = owner.lowercased()
    return allowedOwnerFragments.contains { lower.contains($0) }
}

func out(_ s: String) {
    FileHandle.standardOutput.write(Data((s + "\n").utf8))
}

func fail(_ s: String) -> Never {
    FileHandle.standardError.write(Data((s + "\n").utf8))
    out("RC=1")
    exit(1)
}

func nowEpoch() -> Double {
    return Date().timeIntervalSince1970
}

struct WinInfo {
    let id: CGWindowID
    let pid: pid_t
    let owner: String
    let title: String
    let bounds: CGRect
    let layer: Int
    let onScreen: Bool
}

func listWindows() -> [WinInfo] {
    guard let raw = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] else {
        return []
    }
    var result: [WinInfo] = []
    for d in raw {
        guard let id = d[kCGWindowNumber as String] as? UInt32,
              let pid = d[kCGWindowOwnerPID as String] as? Int32,
              let owner = d[kCGWindowOwnerName as String] as? String,
              let bd = d[kCGWindowBounds as String] as? [String: Any],
              let rect = CGRect(dictionaryRepresentation: bd as CFDictionary)
        else { continue }
        let title = d[kCGWindowName as String] as? String ?? ""
        let layer = d[kCGWindowLayer as String] as? Int ?? 0
        let onScreen = d[kCGWindowIsOnscreen as String] as? Bool ?? false
        result.append(WinInfo(id: id, pid: pid, owner: owner, title: title, bounds: rect, layer: layer, onScreen: onScreen))
    }
    return result
}

/// 解析 `--name value` 与 `--flag` 形式的参数。
func parseArgs(_ argv: [String]) -> (positional: [String], opts: [String: String]) {
    var positional: [String] = []
    var opts: [String: String] = [:]
    var i = 0
    while i < argv.count {
        let a = argv[i]
        if a.hasPrefix("--") {
            let name = String(a.dropFirst(2))
            if i + 1 < argv.count, !argv[i + 1].hasPrefix("--") {
                opts[name] = argv[i + 1]
                i += 2
            } else {
                opts[name] = "1"
                i += 1
            }
        } else {
            positional.append(a)
            i += 1
        }
    }
    return (positional, opts)
}

/// 按 --id 或 --owner(取面积最大的、屏幕上的、layer 0 的窗口)选窗口,并强制检查最小权限约束。
func selectWindow(_ opts: [String: String]) -> WinInfo {
    let wins = listWindows()
    if let idText = opts["id"], let id = UInt32(idText) {
        guard let w = wins.first(where: { $0.id == id }) else { fail("找不到窗口 id=\(id)") }
        guard isAllowedOwner(w.owner) else { fail("拒绝:窗口所属进程 \"\(w.owner)\" 不在允许范围内") }
        return w
    }
    guard let frag = opts["owner"] else { fail("需要 --id 或 --owner") }
    guard isAllowedOwner(frag) || allowedOwnerFragments.contains(where: { frag.lowercased().contains($0) }) else {
        fail("拒绝:--owner \"\(frag)\" 不在允许范围内(只允许包含 wine/dnf/crossover)")
    }
    let titleFrag = opts["title"]
    let candidates = wins.filter {
        $0.owner.lowercased().contains(frag.lowercased())
            && isAllowedOwner($0.owner)
            && $0.layer == 0
            && $0.onScreen
            && $0.bounds.width >= 100 && $0.bounds.height >= 100
            && (titleFrag == nil || $0.title.contains(titleFrag!))
    }
    guard let best = candidates.max(by: { $0.bounds.width * $0.bounds.height < $1.bounds.width * $1.bounds.height }) else {
        fail("没有符合条件的窗口(owner 含 \"\(frag)\")")
    }
    return best
}

// MARK: - 前台

/// 运行一个系统自带的只读命令行工具,返回它的标准输出。
func runTool(_ path: String, _ args: [String]) -> String? {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = Pipe()
    do { try p.run() } catch { return nil }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    return String(data: data, encoding: .utf8)
}

/// 屏幕上最上层的"正常窗口"(layer 0、尺寸不太小)所属的进程。
func topNormalWindowPid() -> pid_t {
    guard let raw = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else {
        return -1
    }
    for d in raw { // 从前到后
        guard (d[kCGWindowLayer as String] as? Int ?? -1) == 0,
              let pid = d[kCGWindowOwnerPID as String] as? Int32,
              let bd = d[kCGWindowBounds as String] as? [String: Any],
              let r = CGRect(dictionaryRepresentation: bd as CFDictionary),
              r.width >= 100, r.height >= 100 else { continue }
        return pid
    }
    return -1
}

/// 当前前台应用的进程号:问 LaunchServices(`lsappinfo`,值总是新的)。
/// 不用 NSWorkspace.frontmostApplication:本工具的主线程不跑 AppKit 事件循环,那个值可能是旧的(E-064)。
func frontmostPid() -> pid_t {
    if let asn = runTool("/usr/bin/lsappinfo", ["front"])?.trimmingCharacters(in: .whitespacesAndNewlines),
       asn.hasPrefix("ASN:"),
       let info = runTool("/usr/bin/lsappinfo", ["info", "-only", "pid", asn]),
       let eq = info.firstIndex(of: "="),
       let pid = Int32(info[info.index(after: eq)...].trimmingCharacters(in: .whitespacesAndNewlines)) {
        return pid
    }
    return topNormalWindowPid()
}

/// 屏幕上盖住某一点的最上层窗口(从前到后第一个包含该点的、屏幕上的窗口)。
func topWindowAt(_ p: CGPoint) -> WinInfo? {
    guard let raw = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] else { return nil }
    for d in raw {
        guard let id = d[kCGWindowNumber as String] as? UInt32,
              let pid = d[kCGWindowOwnerPID as String] as? Int32,
              let owner = d[kCGWindowOwnerName as String] as? String,
              let bd = d[kCGWindowBounds as String] as? [String: Any],
              let rect = CGRect(dictionaryRepresentation: bd as CFDictionary),
              rect.contains(p) else { continue }
        let alpha = d[kCGWindowAlpha as String] as? Double ?? 1.0
        if alpha < 0.05 { continue }
        let layer = d[kCGWindowLayer as String] as? Int ?? 0
        let title = d[kCGWindowName as String] as? String ?? ""
        // 程序坞有一个铺满整个屏幕的透明窗口(layer 20),只有贴边的一圈(图标条所在处)真会接住点击
        if layer == 20, owner == "Dock" || owner == "程序坞" {
            let edge: CGFloat = 110
            let nearEdge = p.y > rect.maxY - edge || p.x < rect.minX + edge || p.x > rect.maxX - edge
            if !nearEdge { continue }
        }
        return WinInfo(id: id, pid: pid, owner: owner, title: title, bounds: rect, layer: layer, onScreen: true)
    }
    return nil
}

func axPoint(_ element: AXUIElement, _ attribute: String) -> CGPoint? {
    var ref: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, attribute as CFString, &ref) == .success,
          let value = ref, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
    var p = CGPoint.zero
    return AXValueGetValue(value as! AXValue, .cgPoint, &p) ? p : nil
}

/// 把目标窗口所属的应用切到前台:先用辅助功能接口设"最前"并抬起那个窗口,再补一次 activate;
/// 然后轮询最多 3 秒确认。返回是否真的成为前台应用。
/// 用户用 --front-point X,Y 指定的"切前台用的点击点"(窗口内点数,左上角为原点)。
/// 没有标题栏的窗口(游戏窗口是无边框的)必须由调用方看过当前画面后指定一个空白点;有标题栏的窗口默认点标题栏。
var frontClickPoint: CGPoint?

/// 这个窗口有没有标题栏:看辅助功能里对应窗口是否有关闭按钮。
func hasTitleBar(_ w: WinInfo) -> Bool {
    let app = AXUIElementCreateApplication(w.pid)
    var ref: CFTypeRef?
    guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &ref) == .success,
          let windows = ref as? [AXUIElement] else { return false }
    for win in windows {
        if let pos = axPoint(win, kAXPositionAttribute),
           abs(pos.x - w.bounds.origin.x) < 3, abs(pos.y - w.bounds.origin.y) < 3 {
            var btn: CFTypeRef?
            return AXUIElementCopyAttributeValue(win, kAXCloseButtonAttribute as CFString, &btn) == .success
        }
    }
    return false
}

func postHidClick(at p: CGPoint) {
    func fire(_ type: CGEventType) {
        let src = CGEventSource(stateID: .hidSystemState)
        let ev = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: .left)
        ev?.setIntegerValueField(.mouseEventClickState, value: 1)
        ev?.post(tap: .cghidEventTap)
    }
    fire(.mouseMoved)
    usleep(120_000)
    fire(.leftMouseDown)
    usleep(60_000)
    fire(.leftMouseUp)
}

/// 第三招:对目标窗口做一次真实点击来激活它(和用户自己点一下窗口等效)。
/// 约束:
///   - 点击点只来自 --front-point(调用方看过画面选的空白点),或有标题栏的窗口的标题栏(顶部 12 点处);
///     没有标题栏又没给 --front-point 就拒绝,绝不在游戏画面里乱点;
///   - 点之前确认屏幕上盖住该点的最上层窗口就是目标窗口本身,被别的窗口盖住就换点或拒绝,绝不会点到别的应用。
func focusByClick(_ w: WinInfo) -> Bool {
    var candidates: [CGPoint] = []
    if let fp = frontClickPoint {
        candidates = [CGPoint(x: w.bounds.origin.x + fp.x, y: w.bounds.origin.y + fp.y)]
    } else if hasTitleBar(w) {
        for fraction in [0.55, 0.7, 0.4, 0.85] as [CGFloat] {
            candidates.append(CGPoint(x: w.bounds.origin.x + w.bounds.width * fraction, y: w.bounds.origin.y + 12))
        }
    } else {
        FileHandle.standardError.write(Data("窗口没有标题栏,需要 --front-point X,Y 指定一个空白点来切前台\n".utf8))
        return false
    }
    for p in candidates {
        guard w.bounds.contains(p), let top = topWindowAt(p), top.id == w.id else { continue }
        postHidClick(at: p)
        return true
    }
    return false
}

/// 把目标窗口所属的应用切到前台,返回是否真的成为前台应用。依次尝试:
/// 1) 辅助功能接口设"最前"并抬起窗口(2026-10-16 实测:对 Wine 进程返回成功但并不生效,见 E-064);
/// 2) 标题栏真实点击(focusByTitleBarClick),成功后轮询确认。
func bringToFront(_ w: WinInfo) -> Bool {
    if frontmostPid() == w.pid { return true }
    let app = AXUIElementCreateApplication(w.pid)
    let r = AXUIElementSetAttributeValue(app, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
    if r != .success {
        FileHandle.standardError.write(Data("AX 设最前返回 \(r.rawValue)\n".utf8))
    }
    var ref: CFTypeRef?
    if AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &ref) == .success,
       let windows = ref as? [AXUIElement] {
        for win in windows {
            if let pos = axPoint(win, kAXPositionAttribute),
               abs(pos.x - w.bounds.origin.x) < 3, abs(pos.y - w.bounds.origin.y) < 3 {
                _ = AXUIElementPerformAction(win, kAXRaiseAction as CFString)
                _ = AXUIElementSetAttributeValue(win, kAXMainAttribute as CFString, kCFBooleanTrue)
                break
            }
        }
    }
    for _ in 0..<8 {
        if frontmostPid() == w.pid { return true }
        usleep(100_000)
    }
    guard focusByClick(w) else {
        FileHandle.standardError.write(Data("没有可用的点击点(被别的窗口盖住或没有标题栏),不点击\n".utf8))
        return false
    }
    for _ in 0..<20 {
        if frontmostPid() == w.pid { return true }
        usleep(100_000)
    }
    return frontmostPid() == w.pid
}

// MARK: - 键盘

/// US ANSI 键盘的虚拟键码。值为 (keycode, 是否需要 Shift)。
func keyInfo(for ch: Character) -> (CGKeyCode, Bool)? {
    let letters: [Character: CGKeyCode] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9,
        "b": 11, "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17,
        "o": 31, "u": 32, "i": 34, "p": 35, "l": 37, "j": 38, "k": 40, "n": 45, "m": 46,
    ]
    let digits: [Character: CGKeyCode] = [
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "9": 25, "7": 26, "8": 28, "0": 29,
    ]
    let shiftedDigits: [Character: Character] = [
        "!": "1", "@": "2", "#": "3", "$": "4", "%": "5", "^": "6", "&": "7", "*": "8", "(": "9", ")": "0",
    ]
    let punct: [Character: CGKeyCode] = [
        "=": 24, "-": 27, "]": 30, "[": 33, "'": 39, ";": 41, "\\": 42, ",": 43, "/": 44, ".": 47, "`": 50,
    ]
    let shiftedPunct: [Character: Character] = [
        "+": "=", "_": "-", "}": "]", "{": "[", "\"": "'", ":": ";", "|": "\\", "<": ",", "?": "/", ">": ".", "~": "`",
    ]
    if let c = letters[ch] { return (c, false) }
    if ch.isUppercase, let lower = ch.lowercased().first, let c = letters[lower] { return (c, true) }
    if let c = digits[ch] { return (c, false) }
    if let base = shiftedDigits[ch], let c = digits[base] { return (c, true) }
    if let c = punct[ch] { return (c, false) }
    if let base = shiftedPunct[ch], let c = punct[base] { return (c, true) }
    if ch == " " { return (49, false) }
    return nil
}

let namedKeys: [String: CGKeyCode] = [
    "return": 36, "enter": 36, "tab": 48, "space": 49, "delete": 51, "backspace": 51, "esc": 53, "escape": 53,
    "left": 123, "right": 124, "down": 125, "up": 126,
    "f1": 122, "f2": 120, "f3": 99, "f4": 118, "f5": 96, "f6": 97, "f7": 98, "f8": 100,
    "f9": 101, "f10": 109, "f11": 103, "f12": 111, "f13": 105, "f14": 107, "f15": 113,
    "home": 115, "end": 119, "pageup": 116, "pagedown": 121,
]

/// 键盘脚本的限制(写死在小工具里,脚本层的检查只是第一道):
/// 默认只允许 x、c(普攻、跳跃)、方向键、esc、wait、hold;--allow-plain 追加字母;--allow-return 允许回车。
struct KeyPolicy {
    var plain: Set<Character> = ["x", "c"]
    var allowReturn = false
    static let maxHoldMs = 6000
    static let maxWaitMs = 60000
    static let arrows: Set<String> = ["left", "right", "up", "down"]
}

func checkKeyScript(_ script: String, policy: KeyPolicy) {
    var i = script.startIndex
    while i < script.endIndex {
        let ch = script[i]
        if ch == "<" {
            guard let close = script[i...].firstIndex(of: ">") else { fail("键盘脚本里的 < 没有配对的 >") }
            let token = String(script[script.index(after: i)..<close])
            i = script.index(after: close)
            let parts = token.split(separator: ":").map(String.init)
            guard let name = parts.first?.lowercased() else { fail("空的 <>") }
            switch name {
            case "wait":
                guard parts.count == 2, let ms = Int(parts[1]), ms >= 0, ms <= KeyPolicy.maxWaitMs else {
                    fail("<wait:毫秒> 的毫秒要是不超过 \(KeyPolicy.maxWaitMs) 的整数: <\(token)>")
                }
            case "hold":
                guard parts.count == 3, KeyPolicy.arrows.contains(parts[1].lowercased()),
                      let ms = Int(parts[2]), ms >= 0, ms <= KeyPolicy.maxHoldMs else {
                    fail("<hold:方向键:毫秒> 只允许方向键且不超过 \(KeyPolicy.maxHoldMs) 毫秒: <\(token)>")
                }
            case "left", "right", "up", "down", "esc", "escape":
                guard parts.count == 1 else { fail("按键 <\(token)> 不带参数") }
            case "return", "enter":
                guard policy.allowReturn, parts.count == 1 else { fail("回车需要显式加 --allow-return") }
            default:
                fail("不允许的按键 <\(token)>")
            }
        } else {
            guard policy.plain.contains(ch) else { fail("不允许的字符 \"\(ch)\"(允许:\(String(policy.plain.sorted())))") }
            i = script.index(after: i)
        }
    }
}

func keyPolicy(from opts: [String: String]) -> KeyPolicy {
    var p = KeyPolicy()
    if let extra = opts["allow-plain"] {
        for c in extra { p.plain.insert(c) }
    }
    p.allowReturn = opts["allow-return"] != nil
    return p
}

/// 事件投递方式:默认只投给目标进程;hid = 走系统事件通道(落到前台应用),只在目标应用是前台时才发。
struct Delivery {
    let pid: pid_t
    let hid: Bool
}

/// 前台检查的缓存(每次 lsappinfo 要起两个子进程,连发事件时不必每个事件都问)。
var lastFrontCheck: (pid: pid_t, at: Double) = (-1, 0)

func frontmostPidCached(maxAge: Double = 0.25) -> pid_t {
    let now = nowEpoch()
    if now - lastFrontCheck.at > maxAge {
        lastFrontCheck = (frontmostPid(), now)
    }
    return lastFrontCheck.pid
}

func post(_ event: CGEvent?, _ d: Delivery) {
    guard let event = event else { return }
    if d.hid {
        let front = frontmostPidCached()
        guard front == d.pid else { fail("拒绝:--hid 要求目标应用在前台,当前前台 pid=\(front)") }
        event.post(tap: .cghidEventTap)
    } else {
        event.postToPid(d.pid)
    }
}

func keyEvent(_ code: CGKeyCode, down: Bool, shift: Bool, _ d: Delivery) {
    let src = CGEventSource(stateID: .hidSystemState)
    let ev = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: down)
    ev?.flags = shift ? .maskShift : []
    post(ev, d)
}

func tap(_ code: CGKeyCode, shift: Bool, _ d: Delivery, holdMs: UInt32) {
    if shift { keyEvent(56, down: true, shift: true, d); usleep(15_000) }
    keyEvent(code, down: true, shift: shift, d)
    usleep(holdMs * 1000)
    keyEvent(code, down: false, shift: shift, d)
    if shift { usleep(15_000); keyEvent(56, down: false, shift: false, d) }
}

/// 迷你语言:普通字符直接输入;<tab> <return> <esc> <f13> 等用尖括号;<wait:300> 暂停 300 毫秒;
/// <hold:left:500> 按住某个键 500 毫秒。字面量 "<" 写成 <lt>。
func typeScript(_ script: String, _ d: Delivery, gapMs: UInt32, holdMs: UInt32) {
    var i = script.startIndex
    while i < script.endIndex {
        let ch = script[i]
        if ch == "<", let close = script[i...].firstIndex(of: ">") {
            let token = String(script[script.index(after: i)..<close])
            i = script.index(after: close)
            let parts = token.split(separator: ":").map(String.init)
            switch parts[0].lowercased() {
            case "wait":
                usleep((UInt32(parts.count > 1 ? parts[1] : "200") ?? 200) * 1000)
            case "hold":
                if parts.count >= 3, let code = namedKeys[parts[1].lowercased()], let ms = UInt32(parts[2]) {
                    keyEvent(code, down: true, shift: false, d)
                    usleep(ms * 1000)
                    keyEvent(code, down: false, shift: false, d)
                }
            case "lt":
                if let (code, shift) = keyInfo(for: "<") { tap(code, shift: shift, d, holdMs: holdMs) }
            default:
                if let code = namedKeys[parts[0].lowercased()] {
                    tap(code, shift: false, d, holdMs: holdMs)
                } else {
                    fail("未知按键 <\(token)>")
                }
            }
        } else {
            guard let (code, shift) = keyInfo(for: ch) else { fail("不支持的字符 \"\(ch)\"") }
            tap(code, shift: shift, d, holdMs: holdMs)
            i = script.index(after: i)
        }
        usleep(gapMs * 1000)
    }
}

// MARK: - 鼠标

func mouseEvent(_ type: CGEventType, at p: CGPoint, clickState: Int64, _ d: Delivery) {
    let src = CGEventSource(stateID: .hidSystemState)
    let ev = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: .left)
    ev?.setIntegerValueField(.mouseEventClickState, value: clickState)
    post(ev, d)
}

// MARK: - 截图

/// 经 ScreenCaptureKit 截一个窗口(CGWindowListCreateImage 在 macOS 15 已移除)。
final class WindowCapturer {
    private let filter: SCContentFilter
    private let config: SCStreamConfiguration
    let pixelWidth: Int
    let pixelHeight: Int

    init?(window: WinInfo) {
        var content: SCShareableContent?
        let sem = DispatchSemaphore(value: 0)
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { c, e in
            content = c
            if let e = e { FileHandle.standardError.write(Data("SCShareableContent 失败: \(e)\n".utf8)) }
            sem.signal()
        }
        if sem.wait(timeout: .now() + 8) == .timedOut { return nil }
        guard let scw = content?.windows.first(where: { $0.windowID == window.id }) else { return nil }
        let f = SCContentFilter(desktopIndependentWindow: scw)
        let scale = max(1.0, Double(f.pointPixelScale))
        let cfg = SCStreamConfiguration()
        cfg.width = Int((scw.frame.width * scale).rounded())
        cfg.height = Int((scw.frame.height * scale).rounded())
        cfg.showsCursor = false
        cfg.pixelFormat = kCVPixelFormatType_32BGRA
        filter = f
        config = cfg
        pixelWidth = cfg.width
        pixelHeight = cfg.height
    }

    func capture() -> CGImage? {
        var image: CGImage?
        let sem = DispatchSemaphore(value: 0)
        SCScreenshotManager.captureImage(contentFilter: filter, configuration: config) { img, e in
            image = img
            if img == nil, let e = e { FileHandle.standardError.write(Data("captureImage 失败: \(e)\n".utf8)) }
            sem.signal()
        }
        if sem.wait(timeout: .now() + 5) == .timedOut { return nil }
        return image
    }
}

func writePNG(_ image: CGImage, to path: String) -> Bool {
    let url = URL(fileURLWithPath: path) as CFURL
    guard let dest = CGImageDestinationCreateWithURL(url, UTType.png.identifier as CFString, 1, nil) else { return false }
    CGImageDestinationAddImage(dest, image, nil)
    return CGImageDestinationFinalize(dest)
}

/// 截一张窗口图存成 PNG:先用 ScreenCaptureKit,失败再退回 screencapture。
func shotPNG(_ w: WinInfo, path: String) -> String {
    if let cap = WindowCapturer(window: w), let img = cap.capture(), writePNG(img, to: path) {
        return "shot id=\(w.id) owner=\"\(w.owner)\" pixels=\(img.width)x\(img.height) file=\(path) via=sck"
    }
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    p.arguments = ["-x", "-o", "-l", String(w.id), path]
    do { try p.run() } catch { fail("无法运行 screencapture: \(error)") }
    p.waitUntilExit()
    guard p.terminationStatus == 0, FileManager.default.fileExists(atPath: path) else {
        fail("截图失败(screencapture 状态 \(p.terminationStatus));多半是还没有屏幕录制权限")
    }
    return "shot id=\(w.id) owner=\"\(w.owner)\" bounds=\(Int(w.bounds.width))x\(Int(w.bounds.height)) file=\(path) via=screencapture"
}

// MARK: - 主流程

func printUsage() -> Never {
    fail("""
    用法:
      status [--prompt]                              查看(或触发请求)屏幕录制与辅助功能权限
      windows [--owner 片段]                          列出窗口
      front (--id N | --owner 片段 [--title 片段]) [--front-point X,Y]
                                                     把该窗口所属应用切到前台,确认后返回。先试辅助功能接口,
                                                     不行就对窗口做一次真实点击:有标题栏点标题栏;无标题栏的
                                                     (游戏窗口)必须用 --front-point 指一个看过画面的空白点
      shot (--id N | --owner 片段 [--title 片段]) --out 文件.png [--front] [--front-point X,Y]
      key  (--id N | --owner 片段) --keys "脚本" [--gap 40] [--hold 40] [--front] [--front-point X,Y] [--hid]
           [--allow-plain 字母们] [--allow-return] [--shot 文件.png] [--shot-delay 毫秒]
      click (--id N | --owner 片段) --x X --y Y [--double] [--move-only] [--front] [--front-point X,Y] [--hid]
           [--shot 文件.png] [--shot-delay 毫秒]                (坐标为窗口内点数,左上角为原点)
      burst (--id N | --owner 片段 [--title 片段]) --out-dir 目录 [--prefix 名] [--count 20] [--interval 100]
           [--keys 脚本 [--keys-delay 毫秒] [--allow-plain 字母们]] [--index index.tsv] [--front]
           在同一个进程里定时发键并连拍,把每帧的起止时刻(epoch 秒)写进 index
    """)
}

func afterAction(_ w: WinInfo, _ opts: [String: String]) {
    guard let path = opts["shot"] else { return }
    let delay = UInt32(opts["shot-delay"] ?? "300") ?? 300
    usleep(delay * 1000)
    out(shotPNG(w, path: path))
}

func runCommand() {
    let rawArgs = Array(CommandLine.arguments.dropFirst())
    guard let command = rawArgs.first else { printUsage() }
    let (_, opts) = parseArgs(Array(rawArgs.dropFirst()))
    if let fp = opts["front-point"] {
        let parts = fp.split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
        guard parts.count == 2 else { fail("--front-point 要写成 X,Y(窗口内点数)") }
        frontClickPoint = CGPoint(x: parts[0], y: parts[1])
    }

    switch command {
    case "status":
        out("screen_capture_access=\(CGPreflightScreenCaptureAccess())")
        out("accessibility_trusted=\(AXIsProcessTrusted())")
        if opts["prompt"] != nil {
            _ = CGRequestScreenCaptureAccess()
            let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
            out("prompt_requested=1")
        }

    case "windows":
        let frag = opts["owner"]?.lowercased()
        for w in listWindows() {
            if let frag = frag {
                if !w.owner.lowercased().contains(frag) { continue }
            } else if !isAllowedOwner(w.owner) {
                continue
            }
            out("id=\(w.id) pid=\(w.pid) owner=\"\(w.owner)\" title=\"\(w.title)\" x=\(Int(w.bounds.origin.x)) y=\(Int(w.bounds.origin.y)) w=\(Int(w.bounds.width)) h=\(Int(w.bounds.height)) layer=\(w.layer) onscreen=\(w.onScreen ? 1 : 0)")
        }
        out("frontmost_pid=\(frontmostPid())")

    case "shot":
        guard let path = opts["out"] else { fail("需要 --out") }
        let w = selectWindow(opts)
        if opts["front"] != nil, !bringToFront(w) { fail("没能把 pid=\(w.pid) 切到前台") }
        out(shotPNG(w, path: path))

    case "front":
        guard AXIsProcessTrusted() else { fail("还没有辅助功能权限,无法切前台") }
        let w = selectWindow(opts)
        if bringToFront(w) {
            out("front ok pid=\(w.pid) owner=\"\(w.owner)\" frontmost_pid=\(frontmostPid())")
        } else {
            fail("没能把 pid=\(w.pid) owner=\"\(w.owner)\" 切到前台(当前前台 pid=\(frontmostPid()))")
        }

    case "key":
        guard let keys = opts["keys"] else { fail("需要 --keys") }
        guard AXIsProcessTrusted() else { fail("还没有辅助功能权限,无法发送键盘事件") }
        let policy = keyPolicy(from: opts)
        checkKeyScript(keys, policy: policy)
        let w = selectWindow(opts)
        if opts["front"] != nil, !bringToFront(w) { fail("没能把 pid=\(w.pid) 切到前台") }
        let gap = UInt32(opts["gap"] ?? "40") ?? 40
        let hold = UInt32(opts["hold"] ?? "40") ?? 40
        typeScript(keys, Delivery(pid: w.pid, hid: opts["hid"] != nil), gapMs: gap, holdMs: hold)
        out("key sent to pid=\(w.pid) owner=\"\(w.owner)\"")
        afterAction(w, opts)

    case "click":
        guard AXIsProcessTrusted() else { fail("还没有辅助功能权限,无法发送鼠标事件") }
        guard let xs = opts["x"], let ys = opts["y"], let x = Double(xs), let y = Double(ys) else { fail("需要 --x --y") }
        let w = selectWindow(opts)
        guard x >= 0, y >= 0, x < w.bounds.width, y < w.bounds.height else {
            fail("点击坐标 (\(Int(x)), \(Int(y))) 不在窗口 \(Int(w.bounds.width))x\(Int(w.bounds.height)) 之内")
        }
        if opts["front"] != nil, !bringToFront(w) { fail("没能把 pid=\(w.pid) 切到前台") }
        let d = Delivery(pid: w.pid, hid: opts["hid"] != nil)
        let p = CGPoint(x: w.bounds.origin.x + x, y: w.bounds.origin.y + y)
        if d.hid {
            // HID 鼠标事件落到屏幕上该点最上层的窗口:必须就是目标窗口本身
            let top = topWindowAt(p)
            guard let top = top, top.id == w.id else {
                let desc = top.map { "id=\($0.id) pid=\($0.pid) owner=\"\($0.owner)\" layer=\($0.layer) bounds=\(Int($0.bounds.origin.x)),\(Int($0.bounds.origin.y)),\(Int($0.bounds.width))x\(Int($0.bounds.height))" } ?? "无"
                fail("拒绝:屏幕点 (\(Int(p.x)), \(Int(p.y))) 上最上层的窗口不是目标窗口(目标 id=\(w.id));最上层是 \(desc);不发 HID 点击")
            }
        }
        mouseEvent(.mouseMoved, at: p, clickState: 0, d)
        usleep(60_000)
        if opts["move-only"] == nil {
            let clicks = opts["double"] != nil ? 2 : 1
            for n in 1...clicks {
                mouseEvent(.leftMouseDown, at: p, clickState: Int64(n), d)
                usleep(50_000)
                mouseEvent(.leftMouseUp, at: p, clickState: Int64(n), d)
                usleep(80_000)
            }
        }
        out("click at window(\(Int(x)),\(Int(y))) -> screen(\(Int(p.x)),\(Int(p.y))) pid=\(w.pid) hid=\(d.hid)")
        afterAction(w, opts)

    case "burst":
        guard let dir = opts["out-dir"] else { fail("需要 --out-dir") }
        let w = selectWindow(opts)
        let count = min(max(Int(opts["count"] ?? "20") ?? 20, 1), 300)
        let intervalMs = max(Int(opts["interval"] ?? "100") ?? 100, 20)
        let prefix = opts["prefix"] ?? "f"
        guard prefix.range(of: "^[A-Za-z0-9_.-]+$", options: .regularExpression) != nil else { fail("--prefix 只能含字母数字下划线点减号") }
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        var keyThread: Thread?
        if let keys = opts["keys"] {
            guard AXIsProcessTrusted() else { fail("还没有辅助功能权限,无法发送键盘事件") }
            checkKeyScript(keys, policy: keyPolicy(from: opts))
        }
        if opts["front"] != nil, !bringToFront(w) { fail("没能把 pid=\(w.pid) 切到前台") }
        guard let cap = WindowCapturer(window: w) else { fail("无法建立窗口截取(多半是还没有屏幕录制权限)") }
        // 预热一次(第一次调用常常慢),不保存
        _ = cap.capture()

        let delayMs = UInt32(opts["keys-delay"] ?? "0") ?? 0
        var keyStart = 0.0
        var keyEnd = 0.0
        if let keys = opts["keys"] {
            let d = Delivery(pid: w.pid, hid: opts["hid"] != nil)
            let t = Thread {
                usleep(delayMs * 1000)
                keyStart = nowEpoch()
                typeScript(keys, d, gapMs: 40, holdMs: 40)
                keyEnd = nowEpoch()
            }
            keyThread = t
        }

        var frames: [(Double, Double, CGImage)] = []
        let startMono = DispatchTime.now()
        let startEpoch = nowEpoch()
        keyThread?.start()
        for i in 0..<count {
            let target = startMono + .milliseconds(i * intervalMs)
            let now = DispatchTime.now()
            if target > now {
                usleep(UInt32((target.uptimeNanoseconds - now.uptimeNanoseconds) / 1000))
            }
            let t0 = nowEpoch()
            guard let img = cap.capture() else { fail("第 \(i) 帧截取失败") }
            frames.append((t0, nowEpoch(), img))
        }
        while keyThread?.isExecuting == true { usleep(20_000) }

        var lines: [String] = []
        var maxGap = 0.0
        for (i, f) in frames.enumerated() {
            let name = "\(prefix)-\(String(format: "%03d", i))"
            let path = (dir as NSString).appendingPathComponent(name + ".png")
            guard writePNG(f.2, to: path) else { fail("写 PNG 失败: \(path)") }
            lines.append(String(format: "%.3f\t%.3f\tburst\t%@\tframe=%d size=%dx%d", f.0, f.1, name, i, f.2.width, f.2.height))
            if i > 0 { maxGap = max(maxGap, f.0 - frames[i - 1].0) }
        }
        if keyStart > 0 {
            lines.append(String(format: "%.3f\t%.3f\tbgkey\t%@\tburst-keys", keyStart, keyEnd, opts["keys"] ?? ""))
        }
        if let indexPath = opts["index"] {
            let text = lines.joined(separator: "\n") + "\n"
            if let h = FileHandle(forWritingAtPath: indexPath) {
                h.seekToEndOfFile()
                h.write(Data(text.utf8))
                try? h.close()
            } else {
                try? text.write(toFile: indexPath, atomically: true, encoding: .utf8)
            }
        }
        let total = (frames.last?.0 ?? startEpoch) - startEpoch
        out(String(format: "burst frames=%d span=%.3fs avg_interval=%.0fms max_gap=%.0fms size=%dx%d dir=%@",
                   frames.count, total, frames.count > 1 ? total / Double(frames.count - 1) * 1000 : 0, maxGap * 1000,
                   cap.pixelWidth, cap.pixelHeight, dir))

    default:
        fail("未知子命令 \(command)")
    }
    out("RC=0")
    exit(0)
}

// 命令在后台队列里跑,主线程留给事件循环(ScreenCaptureKit 的回调有可能要用主队列)。
DispatchQueue.global(qos: .userInitiated).async { runCommand() }
dispatchMain()
