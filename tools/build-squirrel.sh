#!/usr/bin/env bash
# 把 Squirrel 3.2(native/squirrel,MIT 许可)与最小宿主封装(native/dnfsq)编成各平台的原生插件。
#
# 用法:
#   tools/build-squirrel.sh macos      通用二进制(arm64 + x86_64) -> packages/com.dnfunity.squirrel/Plugins/macOS/libdnfsq.dylib
#   tools/build-squirrel.sh android    NDK:arm64-v8a、x86_64   -> packages/com.dnfunity.squirrel/Plugins/Android/<abi>/libdnfsq.so
#   tools/build-squirrel.sh windows    需要 mingw-w64 交叉编译器 -> packages/com.dnfunity.squirrel/Plugins/Windows/x86_64/dnfsq.dll
#   tools/build-squirrel.sh all        依次构建能构建的平台
#
# iOS 不需要预编译:Unity 会把 Plugins/iOS 下的 .cpp 源码加进 Xcode 工程(见 docs/roadmap.md,需要完整 Xcode 才能验证)。
#
# 注意:整数一律是 64 位(SQInteger = long long,各平台一致),浮点是 32 位;因此 Android 只构建 64 位 ABI。
set -euo pipefail
cd "$(dirname "$0")/.."

PKG="packages/com.dnfunity.squirrel/Plugins"
SRCS=(native/squirrel/squirrel/*.cpp native/squirrel/sqstdlib/*.cpp native/dnfsq/dnfsq.cpp)
INCS=(-Inative/squirrel/include -Inative/squirrel/squirrel)
COMMON=(-std=c++11 -O2 -fno-exceptions -fno-rtti -fno-strict-aliasing -D_SQ64 -w)

build_macos() {
  mkdir -p "$PKG/macOS"
  clang++ -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 -dynamiclib \
    -install_name @rpath/libdnfsq.dylib "${COMMON[@]}" "${INCS[@]}" "${SRCS[@]}" \
    -o "$PKG/macOS/libdnfsq.dylib"
  codesign -s - --force "$PKG/macOS/libdnfsq.dylib" >/dev/null
  echo "macOS: $PKG/macOS/libdnfsq.dylib"
}

find_ndk() {
  if [ -n "${ANDROID_NDK_HOME:-}" ] && [ -d "$ANDROID_NDK_HOME" ]; then echo "$ANDROID_NDK_HOME"; return; fi
  local d
  for d in /Applications/Unity/Hub/Editor/*/PlaybackEngines/AndroidPlayer/NDK; do
    if [ -d "$d/toolchains/llvm/prebuilt" ]; then echo "$d"; return; fi
  done
  echo "找不到 Android NDK(设置 ANDROID_NDK_HOME,或安装 Unity 的 Android 模块)" >&2
  return 1
}

build_android() {
  local ndk bin abi triple
  ndk="$(find_ndk)"
  bin="$(echo "$ndk"/toolchains/llvm/prebuilt/*/bin)"
  for abi in arm64-v8a x86_64; do
    case "$abi" in
      arm64-v8a) triple=aarch64-linux-android24 ;;
      x86_64) triple=x86_64-linux-android24 ;;
    esac
    mkdir -p "$PKG/Android/$abi"
    "$bin/${triple}-clang++" -shared -fPIC -static-libstdc++ "${COMMON[@]}" "${INCS[@]}" "${SRCS[@]}" \
      -Wl,-z,max-page-size=16384 -o "$PKG/Android/$abi/libdnfsq.so"
    "$bin/llvm-strip" --strip-unneeded "$PKG/Android/$abi/libdnfsq.so"
    echo "Android $abi: $PKG/Android/$abi/libdnfsq.so"
  done
}

build_windows() {
  local cxx=x86_64-w64-mingw32-g++
  if ! command -v "$cxx" >/dev/null 2>&1; then
    echo "没有找到 $cxx;安装 mingw-w64(brew install mingw-w64)后重试" >&2
    return 1
  fi
  mkdir -p "$PKG/Windows/x86_64"
  "$cxx" -shared -static-libgcc -static-libstdc++ "${COMMON[@]}" "${INCS[@]}" "${SRCS[@]}" \
    -o "$PKG/Windows/x86_64/dnfsq.dll"
  x86_64-w64-mingw32-strip --strip-unneeded "$PKG/Windows/x86_64/dnfsq.dll"
  echo "Windows: $PKG/Windows/x86_64/dnfsq.dll"
}

case "${1:-}" in
  macos) build_macos ;;
  android) build_android ;;
  windows) build_windows ;;
  all) build_macos; build_android; build_windows || echo "(Windows 跳过)" ;;
  *) sed -n 2,14p "$0"; exit 2 ;;
esac
