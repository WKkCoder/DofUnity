#!/usr/bin/env bash
# 构建基线录制小工具 DnfBaselineTool.app(ad-hoc 签名),输出到 re/baseline/(已 gitignore)。
# 注意:重新构建会改变代码签名,系统里给它的屏幕录制/辅助功能授权可能失效,需要重新勾选。
# 因此只在源码变更后才重建。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/tools/baseline/DnfBaselineTool.swift"
APP="$ROOT/re/baseline/DnfBaselineTool.app"
BIN="$APP/Contents/MacOS/DnfBaselineTool"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>local.dnfunity.baselinetool</string>
  <key>CFBundleName</key><string>DnfBaselineTool</string>
  <key>CFBundleDisplayName</key><string>DnfBaselineTool</string>
  <key>CFBundleExecutable</key><string>DnfBaselineTool</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSUIElement</key><true/>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSScreenCaptureUsageDescription</key><string>只截取 wine / DNF / CrossOver 窗口,用于记录原版客户端的对照基线。</string>
</dict>
</plist>
PLIST

swiftc -O -swift-version 5 -o "$BIN" "$SRC" \
  -framework AppKit -framework ApplicationServices -framework CoreGraphics

codesign --force --sign - --identifier local.dnfunity.baselinetool "$APP"
echo "已构建: $APP"
codesign -dv "$APP" 2>&1 | sed -n '1,6p'
