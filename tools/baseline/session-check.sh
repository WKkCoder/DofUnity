#!/usr/bin/env bash
# 检查当前 macOS 登录会话是否处于可操作状态。
# 屏幕锁定时:游戏窗口被遮挡,Wine 会降低渲染频率,截图得到的是过期画面,发键盘事件也没有意义。
# 所以任何 GUI 自动化(front、key、click、shot)之前都先跑这个脚本;它返回非 0 就停手,等用户回来。
#
# 用法: tools/baseline/session-check.sh        (正常输出一行状态;锁定时退出码 1)
set -euo pipefail
swift -e '
import CoreGraphics
import AppKit
let dict = (CGSessionCopyCurrentDictionary() as? [String: Any]) ?? [:]
let locked = (dict["CGSSessionScreenIsLocked"] as? Bool) ?? ((dict["CGSSessionScreenIsLocked"] as? Int) == 1)
let front = NSWorkspace.shared.frontmostApplication?.localizedName ?? "?"
print("screen_locked=\(locked) frontmost=\(front)")
exit(locked ? 1 : 0)
' 2>/dev/null
