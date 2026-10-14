#!/usr/bin/env bash
# 用 Unity 批处理模式为一个平台构建最小冒烟播放器,桌面平台(macOS)再把播放器跑一遍。
# 用法: tools/unity-build-smoke.sh <macos|windows|android>
# 日志:re/unity-build-<平台>.log;播放器日志:re/unity-run-macos.log、re/unity-run-macos-gpu.log(都被 .gitignore 排除)
# 环境变量: UNITY_GRAPHICS=1 让 macOS 播放器在无图形设备的那遍之后,再带图形设备跑一遍(需要 GPU 与图形会话);
#           DNF_DIR 有效时,带图形设备的那遍还会渲染真实城镇并与 CPU 合成器对照。
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

platform="${1:?用法: $0 <macos|windows|android>}"
UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity}"
[ -x "$UNITY" ] || { echo "找不到 Unity 编辑器: $UNITY" >&2; exit 2; }

case "$platform" in
  macos)   method="DnfUnity.Editor.BuildSmoke.BuildMacOS";   target="StandaloneOSX" ;;
  windows) method="DnfUnity.Editor.BuildSmoke.BuildWindows"; target="StandaloneWindows64" ;;
  android) method="DnfUnity.Editor.BuildSmoke.BuildAndroid"; target="Android" ;;
  *) echo "未知平台 $platform" >&2; exit 2 ;;
esac

mkdir -p re
log="$PWD/re/unity-build-$platform.log"
set +e
"$UNITY" -batchmode -nographics -quit -projectPath "$PWD/unity/DnfUnity" \
  -buildTarget "$target" -executeMethod "$method" -logFile "$log"
code=$?
set -e
echo "Unity 退出码: $code(日志 $log)"
grep -n 'DNF_BUILD' "$log" | tail -5 || true
if [ "$code" -ne 0 ]; then
  grep -n -E 'error CS|Error building Player|BuildFailedException|Exception:' "$log" | head -15 >&2 || true
  exit 1
fi

if [ "$platform" = "macos" ]; then
  # 可执行文件名等于 productName,不写死,取 Contents/MacOS 下唯一的那个
  app="$(ls -d "$PWD"/unity/DnfUnity/Builds/macos/DnfSmoke.app/Contents/MacOS/* | head -1)"
  run_log="$PWD/re/unity-run-macos.log"
  rm -f "$run_log"
  set +e
  "$app" -batchmode -nographics -logFile "$run_log"
  rc=$?
  set -e
  echo "播放器退出码(无图形设备): $rc"
  grep -E 'DNF_SMOKE' "$run_log" || true
  if [ "$rc" -ne 0 ]; then
    exit "$rc"
  fi
  if [ "${UNITY_GRAPHICS:-0}" = "1" ]; then
    # 带图形设备再跑一遍:GPU 与 CPU 合成器的对照;设置了 DNF_DIR 时还会渲染真实城镇
    gpu_log="$PWD/re/unity-run-macos-gpu.log"
    rm -f "$gpu_log"
    set +e
    "$app" -batchmode -logFile "$gpu_log"
    rc=$?
    set -e
    echo "播放器退出码(带图形设备): $rc"
    grep -E 'DNF_SMOKE' "$gpu_log" || true
  fi
  exit "$rc"
fi
