#!/usr/bin/env bash
# 构建基线录制小工具 DnfBaselineTool.app,输出到 re/baseline/(已 gitignore)。
#
# 签名:
#   - 若 re/baseline/signing/ 下有本地签名身份(tools/baseline/make-signing-identity.sh 建的),用它签名;
#     系统里的屏幕录制/辅助功能授权记的是"标识符 + 证书",重新构建不会让授权失效。
#   - 否则退回 ad-hoc 签名(授权记的是代码哈希,每次重建都要重新勾选)。
# 签名时需要把专用钥匙串临时加进当前用户的钥匙串搜索列表(否则 codesign 找不到私钥),
# 签完立刻恢复原列表(用 trap 保证出错也会恢复)。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/tools/baseline/DnfBaselineTool.swift"
APP="$ROOT/re/baseline/DnfBaselineTool.app"
BIN="$APP/Contents/MacOS/DnfBaselineTool"
SIGN_DIR="$ROOT/re/baseline/signing"
KC="$SIGN_DIR/dnfbaselinetool.keychain-db"
IDENT="local.dnfunity.baselinetool"

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
  -framework AppKit -framework ApplicationServices -framework CoreGraphics \
  -framework ScreenCaptureKit -framework ImageIO -framework UniformTypeIdentifiers

if [[ -f "$KC" && -f "$SIGN_DIR/keychain.pass" ]]; then
  HASH="$(security find-identity -p codesigning "$KC" | awk '/DnfBaselineTool Local Signing/ {print $2; exit}')"
  [[ -n "$HASH" ]] || { echo "专用钥匙串里找不到签名身份,先运行 tools/baseline/make-signing-identity.sh" >&2; exit 2; }
  ORIG=()
  while IFS= read -r line; do
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line//\"/}"
    [[ -n "$line" ]] && ORIG+=("$line")
  done < <(security list-keychains -d user)
  [[ ${#ORIG[@]} -gt 0 ]] || { echo "读不到当前钥匙串搜索列表,停止" >&2; exit 2; }
  restore_keychains() { security list-keychains -d user -s "${ORIG[@]}"; }
  trap restore_keychains EXIT
  security unlock-keychain -p "$(cat "$SIGN_DIR/keychain.pass")" "$KC"
  security list-keychains -d user -s "$KC" "${ORIG[@]}"
  codesign --force --sign "$HASH" --identifier "$IDENT" "$APP"
  echo "签名: 本地身份 $HASH"
else
  codesign --force --sign - --identifier "$IDENT" "$APP"
  echo "签名: ad-hoc(没有本地签名身份)"
fi

echo "已构建: $APP"
codesign -dv "$APP" 2>&1 | sed -n '1,6p'
codesign -dr - "$APP" 2>&1 | tail -1
