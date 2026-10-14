// 检查调用进程(Cursor 终端的子进程)是否拥有"屏幕录制"与"辅助功能"权限。
import Foundation
import CoreGraphics
import ApplicationServices

print("screen_capture_access=\(CGPreflightScreenCaptureAccess())")
print("accessibility_trusted=\(AXIsProcessTrusted())")
