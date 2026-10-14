#!/usr/bin/env bash
# 构建"城镇演示"播放器:运行时读取你自备的 Script.pvf 与 ImagePacks2,实时播放网关城镇(地图图层、叠加光效、NPC)。
# 用法: tools/unity-build-town.sh <macos|windows|android>
# 日志:re/unity-build-town-<平台>.log;macOS 的离屏渲染校验日志 re/unity-run-town-macos.log(都被 .gitignore 排除)
# macOS 构建后,如果 DNF_DIR 有效(local.env 或环境变量),会带图形设备无窗口地跑两次播放器:
#   1. 用 -dnfshot 把 t=545 毫秒的一帧渲染成 PNG,再与 CPU 合成器(dnf render-town --scene --png)逐像素比较,要求逐字节相同;
#   2. 用 -dnfquitafter 4 实时运行 4 秒,检查日志里没有异常、帧数正常。
# 运行演示:
#   macOS:  open unity/DnfUnity/Builds/macos/DnfTownDemo.app --args -dnfdir "<含 Script.pvf 的目录>"
#           (不带参数也行:窗口里有输入框,路径会保存在 PlayerPrefs 里)
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

platform="${1:?用法: $0 <macos|windows|android>}"
UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity}"
[ -x "$UNITY" ] || { echo "找不到 Unity 编辑器: $UNITY" >&2; exit 2; }

case "$platform" in
  macos)   method="DnfUnity.Editor.BuildTownDemo.BuildMacOS";   target="StandaloneOSX" ;;
  windows) method="DnfUnity.Editor.BuildTownDemo.BuildWindows"; target="StandaloneWindows64" ;;
  android) method="DnfUnity.Editor.BuildTownDemo.BuildAndroid"; target="Android" ;;
  *) echo "未知平台 $platform" >&2; exit 2 ;;
esac

mkdir -p re out
log="$PWD/re/unity-build-town-$platform.log"
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
  app="$(ls -d "$PWD"/unity/DnfUnity/Builds/macos/DnfTownDemo.app/Contents/MacOS/* | head -1)"
  echo "已构建: $PWD/unity/DnfUnity/Builds/macos/DnfTownDemo.app"
  if [ -n "${DNF_DIR:-}" ] && [ -f "$DNF_DIR/Script.pvf" ]; then
    run_log="$PWD/re/unity-run-town-macos.log"
    rm -f "$run_log" out/town-demo-545.png out/town-cpu-545.png
    set +e
    "$app" -batchmode -logFile "$run_log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-545.png" -dnftime 545
    rc=$?
    set -e
    echo "播放器退出码(离屏渲染一帧): $rc"
    grep -E 'DNF_TOWN_DEMO' "$run_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --scene --png "$PWD/out/town-cpu-545.png" --time 545 >/dev/null
    python3 tools/py/png_diff.py out/town-demo-545.png out/town-cpu-545.png --max-diff 0
    # 再实时运行 4 秒(无窗口),检查实时循环没有异常、帧数正常
    live_log="$PWD/re/unity-run-town-live-macos.log"
    rm -f "$live_log"
    set +e
    "$app" -batchmode -logFile "$live_log" -dnfdir "$DNF_DIR" -dnfquitafter 4
    rc=$?
    set -e
    echo "播放器退出码(实时运行 4 秒): $rc"
    grep -E 'DNF_TOWN_DEMO' "$live_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$live_log"; then
      echo "实时运行的日志里有异常:$live_log" >&2
      exit 1
    fi
    grep -q 'DNF_TOWN_DEMO ran ' "$live_log" || { echo "实时运行没有留下 DNF_TOWN_DEMO ran 行" >&2; exit 1; }
  else
    echo "没有设置 DNF_DIR:跳过离屏渲染校验"
  fi
fi
