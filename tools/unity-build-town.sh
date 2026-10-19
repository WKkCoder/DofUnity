#!/usr/bin/env bash
# 构建"城镇演示"播放器:运行时读取你自备的 Script.pvf 与 ImagePacks2,实时播放网关城镇(地图图层、叠加光效、NPC)。
# 用法: tools/unity-build-town.sh <macos|windows|android>
# 日志:re/unity-build-town-<平台>.log;macOS 的离屏渲染校验日志 re/unity-run-town-macos.log(都被 .gitignore 排除)
# macOS 构建后,如果 DNF_DIR 有效(local.env 或环境变量),会带图形设备无窗口地跑几次播放器:
#   1. 用 -dnfshot 把 t=545 毫秒的一帧渲染成 PNG,再与 CPU 合成器(dnf render-town --scene --png)逐像素比较,要求逐字节相同;
#   1b. 同样的对照,但带主角:朝右站立、朝左行走各一帧(-dnfhero 与 --hero 同一写法,证据 E-065);
#   2. 用 -dnfquitafter 4 实时运行 4 秒,检查日志里没有异常、帧数正常;
#   3. 用 -dnfautowalk 0.5:left,3:none 替主角按键:向左走到撞上可走区域的左边界(镜头 x = 0),松开后站立,检查日志里的最终状态。
#   4. 副本房间(E-067、E-068):离屏一帧逐字节对照,实时自动行走;战斗(E-069):按同一份时间表确定地模拟到指定时刻,
#      离屏渲染的一帧(怪物受击/死亡、血条、伤害数字、连击)与命令行 combat-check 逐字节对照;再实时跑一遍挥击。
#   5. 整座副本(E-070):自动机按固定步长打到指定时刻(逛遍所有房间的 6.8/21.0/48.0/59.7 秒与直奔首领的 14.0 秒),离屏渲染的一帧(出口淡出、换房间、小地图、CLEAR)
#      与命令行 dungeon-check --bot 逐字节对照;再实时让自动机逛遍并打完整座副本,检查走完(进过 6 个房间、首领清空)。
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
    # 房间里实时运行并自动走:向左走到房间左边(被左边那面墙的门体挡住,镜头 0),再向下走到通行带的下沿(y 取整显示 348.0)。
    # 最后停在哪儿由门体的碰撞形状决定(证据 E-072),所以不写死数字,和命令行 combat-check 同一份时间表的结果比:位置、朝向、站立、镜头都要一致。
    room_walk_log="$PWD/re/unity-run-town-room-walk-macos.log"
    rm -f "$room_walk_log"
    set +e
    "$app" -batchmode -logFile "$room_walk_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 7 -dnfautowalk 0.5:left,5:down,6.5:none
    rc=$?
    set -e
    echo "播放器退出码(房间里自动行走): $rc"
    grep -E 'DNF_TOWN_DEMO' "$room_walk_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    cli_hero="$(dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play 0.5:left,5:down,6.5:none --seconds 7 \
      | sed -n 's/^结束:主角 (\([0-9.]*,[0-9.]*\)),.*/\1/p')"
    [ -n "$cli_hero" ] || { echo "命令行 combat-check 没有给出主角的最终位置" >&2; exit 1; }
    grep -q "DNF_TOWN_DEMO ran .* hero=(${cli_hero}) facing=left motion=Rest cameraX=0 .*room=true" "$room_walk_log" \
      || { echo "房间里自动行走后主角不在命令行算出的位置 (${cli_hero}) 朝左、站立、镜头 0:$room_walk_log" >&2; exit 1; }
    case "$cli_hero" in *,348.0) ;; *) echo "主角应走到通行带的下沿(y = 348.0),命令行给出 ${cli_hero}" >&2; exit 1 ;; esac

    # 副本房间里的战斗(证据 E-069):时间表是 "0.2 秒起向左走、2.5 秒停下、2.6/3.3/4.0 秒各按一下攻击键"。
    # 播放器带 -dnfplayat 时按固定 1/60 秒步长确定地模拟到指定时刻,再离屏渲染一帧;命令行 combat-check 用同一份时间表与同一个模拟类,
    # 在同一时刻写出 PNG。3.6 秒:第一只怪物刚受击(受击帧、血条、伤害数字、连击提示);4.3 秒:一只在死亡、一只受击。
    fight_play="0.2:left,2.5:none,2.6:attack,3.3:attack,4.0:attack"
    for at in 3.6 4.3; do
      tag="room-fight-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-room-fight-$at.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfautowalk "$fight_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$fight_play" --seconds "$at" \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-fight" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-fight-$(printf '%.2f' "$at").png" --max-diff 0
    done
    # 实时运行同一份时间表 6 秒:帧率不固定,所以不比像素,只检查按键确实进了战斗(至少命中一次)、没有异常
    fight_log="$PWD/re/unity-run-town-room-fight-macos.log"
    rm -f "$fight_log"
    set +e
    "$app" -batchmode -logFile "$fight_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 6 -dnfautowalk "$fight_play"
    rc=$?
    set -e
    echo "播放器退出码(房间里实时战斗): $rc"
    grep -E 'DNF_TOWN_DEMO' "$fight_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$fight_log"; then
      echo "实时战斗的日志里有异常:$fight_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 hits=[1-9][0-9]*' "$fight_log" \
      || { echo "实时战斗里没有命中过怪物(应有 hits>=1):$fight_log" >&2; exit 1; }

    # 奔跑、奔跑攻击、跳跃与跳跃攻击(证据 E-072):时间表是 "0.2 秒向左走、0.3 秒松开、0.4 秒再按向左(双击 = 奔跑)、0.9 秒奔跑中按攻击(奔跑攻击)、
    # 1.6 秒松开、2.0 秒跳跃、2.3 秒空中按攻击(跳跃攻击)"。0.7 秒:奔跑中;1.2 秒:奔跑攻击挥击中;2.2 秒:跳跃上升(精灵整体抬高);
    # 2.5 秒:空中攻击挥击中;2.9 秒:快落地。播放器与命令行 combat-check 同一份时间表、同一个模拟类,逐字节对照。
    action_play="0.2:left,0.3:none,0.4:left,0.9:left+attack,1.6:none,2.0:jump,2.3:attack,3.2:none"
    for at in 0.7 1.2 2.2 2.5 2.9; do
      tag="room-action-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-room-action-$at.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfautowalk "$action_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$action_play" --seconds "$at" \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-action" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-action-$(printf '%.2f' "$at").png" --max-diff 0
    done

    # 整座副本(证据 E-070):洛兰的迷宫,自动机(DungeonBot)打副本。
    # 播放器带 -dnfdungeon -dnfexplore(或 -dnfbot)-dnfplayat 时按固定 1/60 秒步长确定地打到指定时刻,再离屏渲染一帧;
    # 命令行 dungeon-check --bot(--explore)是同一局、同一时刻写 PNG,逐字节对照。
    # 逛遍所有房间的路线:6.8 秒(第一间房刚清空,出口门洞淡出打开到一半,小地图)、9.83 秒(走进门洞,黑幕淡出到一半)、10.0 秒(黑幕全黑,已换进下一间房)、
    # 10.2 秒(黑幕淡入到一半)、21.0 秒(已来回换过房间)、48.0 秒(刚进首领房)、59.7 秒(首领房清空:CLEAR 横幅与完整小地图);
    # 直奔首领的最短路线:14.0 秒(刚进首领房,黑幕淡入快结束)、14.6 秒(首领房的第一帧清晰画面)。
    # 这些时刻跟着出门的节奏走(证据 E-072:淡出 250 毫秒、全黑 130 毫秒、淡入 250 毫秒);节奏改了要重选时刻。
    dungeon=dungeon/act1/lorien.dgn
    for spec in "explore:6.8" "explore:9.83" "explore:10.0" "explore:10.2" "explore:21.0" "explore:48.0" "explore:59.7" "bot:14.0" "bot:14.6"; do
      mode="${spec%%:*}"; at="${spec#*:}"
      tag="dungeon-$mode-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-dungeon-$mode-$(printf '%.2f' "$at").png"
      cli_args=(--dungeon "$dungeon" --bot --seconds "$at" --shots-at "$at" --png "$PWD/out/town-cpu-dungeon-$mode")
      [ "$mode" = "explore" ] && cli_args+=(--explore)
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfdungeon "$dungeon" "-dnf$mode" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      # 退出码 2 = 到点时副本还没走完(这里是预期的:只要到点的那一帧)
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll dungeon-check "${cli_args[@]}" >/dev/null || [ $? -eq 2 ]
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-dungeon-$mode-$(printf '%.2f' "$at").png" --max-diff 0
    done
    # 实时运行:自动机逛遍整座副本再打首领。帧率不固定,所以不比像素,只检查走完了(进过 6 个房间、全清、首领清空)、没有异常
    dungeon_log="$PWD/re/unity-run-town-dungeon-explore-macos.log"
    rm -f "$dungeon_log"
    set +e
    "$app" -batchmode -logFile "$dungeon_log" -dnfdir "$DNF_DIR" -dnfdungeon "$dungeon" -dnfexplore -dnfquitafter 90
    rc=$?
    set -e
    echo "播放器退出码(实时自动打副本): $rc"
    grep -E 'DNF_TOWN_DEMO' "$dungeon_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$dungeon_log"; then
      echo "实时打副本的日志里有异常:$dungeon_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_TOWN_DEMO ran .* dungeon=.* entered=6 cleared=6 .* bossCleared=True' "$dungeon_log" \
      || { echo "实时打副本没有走完(应进过 6 个房间、清空 6 个、首领清空):$dungeon_log" >&2; exit 1; }
  else
    echo "没有设置 DNF_DIR:跳过离屏渲染校验"
  fi
fi
