// DnfBaselineTool:基线录制用的最小权限小工具(原创代码)。
//
// 只做三件事:列出窗口、给窗口截图、向指定窗口所属进程发送键盘/鼠标事件。
// 最小权限约束(写死在代码里):窗口所属进程名必须包含 wine / dnf / crossover 之一,
// 否则拒绝操作;因此它碰不到你的浏览器、聊天窗口或 Cursor。
//
// 权限归属:作为独立应用经 `open` 启动,系统授权("屏幕录制"、"辅助功能")记在本工具名下,
// 而不是 Cursor 名下。构建与调用见 tools/baseline/build-tool.sh 与 tools/baseline/tool.sh。
import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

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

func post(_ event: CGEvent?, pid: pid_t) {
    guard let event = event else { return }
    event.postToPid(pid)
}

func keyEvent(_ code: CGKeyCode, down: Bool, shift: Bool, pid: pid_t) {
    let src = CGEventSource(stateID: .hidSystemState)
    let ev = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: down)
    ev?.flags = shift ? .maskShift : []
    post(ev, pid: pid)
}

func tap(_ code: CGKeyCode, shift: Bool, pid: pid_t, holdMs: UInt32) {
    if shift { keyEvent(56, down: true, shift: true, pid: pid); usleep(15_000) }
    keyEvent(code, down: true, shift: shift, pid: pid)
    usleep(holdMs * 1000)
    keyEvent(code, down: false, shift: shift, pid: pid)
    if shift { usleep(15_000); keyEvent(56, down: false, shift: false, pid: pid) }
}

/// 迷你语言:普通字符直接输入;<tab> <return> <esc> <f13> 等用尖括号;<wait:300> 暂停 300 毫秒;
/// <hold:left:500> 按住某个键 500 毫秒。字面量 "<" 写成 <lt>。
func typeScript(_ script: String, pid: pid_t, gapMs: UInt32, holdMs: UInt32) {
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
                    keyEvent(code, down: true, shift: false, pid: pid)
                    usleep(ms * 1000)
                    keyEvent(code, down: false, shift: false, pid: pid)
                }
            case "lt":
                if let (code, shift) = keyInfo(for: "<") { tap(code, shift: shift, pid: pid, holdMs: holdMs) }
            default:
                if let code = namedKeys[parts[0].lowercased()] {
                    tap(code, shift: false, pid: pid, holdMs: holdMs)
                } else {
                    fail("未知按键 <\(token)>")
                }
            }
        } else {
            guard let (code, shift) = keyInfo(for: ch) else { fail("不支持的字符 \"\(ch)\"") }
            tap(code, shift: shift, pid: pid, holdMs: holdMs)
            i = script.index(after: i)
        }
        usleep(gapMs * 1000)
    }
}

// MARK: - 鼠标

func mouseEvent(_ type: CGEventType, at p: CGPoint, clickState: Int64, pid: pid_t) {
    let src = CGEventSource(stateID: .hidSystemState)
    let ev = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: .left)
    ev?.setIntegerValueField(.mouseEventClickState, value: clickState)
    post(ev, pid: pid)
}

// MARK: - 主流程

let rawArgs = Array(CommandLine.arguments.dropFirst())
guard let command = rawArgs.first else {
    fail("""
    用法:
      status [--prompt]                              查看(或触发请求)屏幕录制与辅助功能权限
      windows [--owner 片段]                          列出窗口
      shot (--id N | --owner 片段 [--title 片段]) --out 文件.png
      key  (--id N | --owner 片段) --keys "脚本" [--gap 40] [--hold 40]
      click (--id N | --owner 片段) --x X --y Y [--double] [--move-only]   (坐标为窗口内点数,左上角为原点)
      front (--id N | --owner 片段)                   把该窗口所属应用切到前台
    """)
}
let (_, opts) = parseArgs(Array(rawArgs.dropFirst()))

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

case "shot":
    guard let path = opts["out"] else { fail("需要 --out") }
    let w = selectWindow(opts)
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    p.arguments = ["-x", "-o", "-l", String(w.id), path]
    do { try p.run() } catch { fail("无法运行 screencapture: \(error)") }
    p.waitUntilExit()
    guard p.terminationStatus == 0, FileManager.default.fileExists(atPath: path) else {
        fail("screencapture 失败(状态 \(p.terminationStatus));多半是还没有屏幕录制权限")
    }
    out("shot id=\(w.id) owner=\"\(w.owner)\" bounds=\(Int(w.bounds.width))x\(Int(w.bounds.height)) file=\(path)")

case "key":
    guard let keys = opts["keys"] else { fail("需要 --keys") }
    guard AXIsProcessTrusted() else { fail("还没有辅助功能权限,无法发送键盘事件") }
    let w = selectWindow(opts)
    let gap = UInt32(opts["gap"] ?? "40") ?? 40
    let hold = UInt32(opts["hold"] ?? "40") ?? 40
    typeScript(keys, pid: w.pid, gapMs: gap, holdMs: hold)
    out("key sent to pid=\(w.pid) owner=\"\(w.owner)\"")

case "click":
    guard AXIsProcessTrusted() else { fail("还没有辅助功能权限,无法发送鼠标事件") }
    guard let xs = opts["x"], let ys = opts["y"], let x = Double(xs), let y = Double(ys) else { fail("需要 --x --y") }
    let w = selectWindow(opts)
    let p = CGPoint(x: w.bounds.origin.x + x, y: w.bounds.origin.y + y)
    mouseEvent(.mouseMoved, at: p, clickState: 0, pid: w.pid)
    usleep(60_000)
    if opts["move-only"] == nil {
        let clicks = opts["double"] != nil ? 2 : 1
        for n in 1...clicks {
            mouseEvent(.leftMouseDown, at: p, clickState: Int64(n), pid: w.pid)
            usleep(50_000)
            mouseEvent(.leftMouseUp, at: p, clickState: Int64(n), pid: w.pid)
            usleep(80_000)
        }
    }
    out("click at window(\(Int(x)),\(Int(y))) -> screen(\(Int(p.x)),\(Int(p.y))) pid=\(w.pid)")

case "front":
    let w = selectWindow(opts)
    if let app = NSRunningApplication(processIdentifier: w.pid) {
        app.activate()
        out("activated pid=\(w.pid) owner=\"\(w.owner)\"")
    } else {
        fail("找不到进程 pid=\(w.pid)")
    }

default:
    fail("未知子命令 \(command)")
}
out("RC=0")
