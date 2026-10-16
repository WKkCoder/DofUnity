#!/usr/bin/env bash
# 构建"城镇演示"播放器:运行时读取你自备的 Script.pvf 与 ImagePacks2,实时播放网关城镇(地图图层、叠加光效、NPC)。
# 用法: tools/unity-build-town.sh <macos|windows|android>
# 日志:re/unity-build-town-<平台>.log;macOS 的离屏渲染校验日志 re/unity-run-town-macos.log(都被 .gitignore 排除)
# macOS 构建后,如果 DNF_DIR 有效(local.env 或环境变量),会带图形设备无窗口地跑几次播放器:
#   1. 用 -dnfshot 把 t=545 毫秒的一帧渲染成 PNG,再与 CPU 合成器(dnf render-town --scene --png)逐像素比较,要求逐字节相同;
#   1b. 同样的对照,但带主角:朝右站立、朝左行走各一帧(-dnfhero 与 --hero 同一写法,证据 E-065);
#   2. 用 -dnfquitafter 4 实时运行 4 秒,检查日志里没有异常、帧数正常;
#   3. 用 -dnfautowalk 0.5:left,3:none 替主角按键:向左走到撞上可走区域的左边界(镜头 x = 0),松开后站立,检查日志里的最终状态。
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
    # 带主角的两帧:朝右站立(镜头 96)与朝左行走(镜头 0);Unity 不给镜头 x 时按"主角居中再夹住"取,所以 CPU 一侧显式给出同样的镜头
    for spec in "615,234,right,rest,800:96:hero-rest" "400,250,left,move,300:0:hero-walk"; do
      hero="${spec%%:*}"; rest="${spec#*:}"; cam="${rest%%:*}"; tag="${rest#*:}"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-$tag.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" -dnftime 545 -dnfhero "$hero"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --scene --png "$PWD/out/town-cpu-$tag.png" --time 545 --cam-x "$cam" --hero "$hero" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-$tag.png" --max-diff 0
    done
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
    # 主角自动向左走:0.5 秒按下,约 1.8 秒撞到可走区域的左边界,3 秒松开;4 秒时应停在左边界上(leftEdge=True)、镜头 x = 0、朝左、站立
    walk_log="$PWD/re/unity-run-town-autowalk-macos.log"
    rm -f "$walk_log"
    set +e
    "$app" -batchmode -logFile "$walk_log" -dnfdir "$DNF_DIR" -dnfquitafter 4 -dnfautowalk 0.5:left,3:none
    rc=$?
    set -e
    echo "播放器退出码(主角自动向左走): $rc"
    grep -E 'DNF_TOWN_DEMO' "$walk_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    grep -q 'DNF_TOWN_DEMO ran .* facing=left motion=Rest cameraX=0 leftEdge=True' "$walk_log" \
      || { echo "自动行走后主角不在预期的状态(应停在可走区域左边界上、朝左、站立、镜头 0):$walk_log" >&2; exit 1; }

    # 副本房间(证据 E-067、E-068):洛兰第一、第二个房间。离屏渲染一帧与 CPU 合成逐字节对照(出口按演示摆法,房间里的障碍物都在)
    #   s2 带主角:出生点 (876,342) 朝右站立(战斗待机动作),镜头 320;s3 不带主角:镜头 320
    for spec in "map/lorien/s2.map:876,342,right,rest,0:320:room-s2-hero" "map/lorien/s3.map::320:room-s3"; do
      rmap="${spec%%:*}"; rest="${spec#*:}"; hero="${rest%%:*}"; rest="${rest#*:}"; cam="${rest%%:*}"; tag="${rest#*:}"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-$tag.png"
      app_args=(-batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" -dnftime 545 -dnfmap "$rmap")
      cpu_args=(render-town --scene --room --map "$rmap" --demo-gates --png "$PWD/out/town-cpu-$tag.png" --time 545 --cam-x "$cam")
      if [ -n "$hero" ]; then
        app_args+=(-dnfhero "$hero,$cam")
        cpu_args+=(--hero "$hero" --hero-wait)
      fi
      set +e
      "$app" "${app_args[@]}"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll "${cpu_args[@]}" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-$tag.png" --max-diff 0
    done
    # 房间里实时运行并自动走:向左走到房间左边(x 到 0,镜头 0),再向下走到通行带的下沿(y 停在 348 之前,取整显示 348.0)
    room_walk_log="$PWD/re/unity-run-town-room-walk-macos.log"
    rm -f "$room_walk_log"
    set +e
    "$app" -batchmode -logFile "$room_walk_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 7 -dnfautowalk 0.5:left,5:down,6.5:none
    rc=$?
    set -e
    echo "播放器退出码(房间里自动行走): $rc"
    grep -E 'DNF_TOWN_DEMO' "$room_walk_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    grep -q 'DNF_TOWN_DEMO ran .* hero=(0\.0,348\.0) facing=left motion=Rest cameraX=0 .*room=true' "$room_walk_log" \
      || { echo "房间里自动行走后主角不在预期的状态(应在 (0, 348) 取整显示、朝左、站立、镜头 0):$room_walk_log" >&2; exit 1; }
  else
    echo "没有设置 DNF_DIR:跳过离屏渲染校验"
  fi
fi
