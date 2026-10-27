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
#   6. 声音(E-089、E-090):同一局实时运行默认静音(-batchmode),但整条链路照跑;日志里的 DNF_AUDIO 计数要有提示、开始放过、读成功过剪辑、没有读失败。
#   7. 整条流程(E-093、E-094):副本选择界面(原版世界地图画法)离屏两帧(0.7、2.0 秒)与命令行 flow-check --from select 逐字节对照;
#      选角界面(E-096)0、0.7、1.4 秒三帧与命令行 charselect-shot 逐字节对照;
#      加载画面(E-095)进城与进图两帧、城镇图册里的三块区域(赛丽亚房间、艾尔文防线、地下城入口区域)各一帧,分别与命令行 flow-check --shots-at、town-shot 逐字节对照;
#      再让流程自动机在播放器里实时走完 选角 → 登录加载 → 赛丽亚房间 → 走出口 → 艾尔文防线 → 走出口 → 地下城入口区域 → 走出口 → 选择 → 副本 → 结算 → 再次挑战 → 副本 → 结算 →
#      选择其它地下城 → 选择 → 副本 → 结算 → 返回城镇(时间步放大 4 倍),检查十六次阶段切换、三局副本与声音计数;
#      场景音乐(E-097):日志里第一首 DNF_MUSIC 是选角音乐 M_CHARACTER_SELECT、背景音乐至少开始七次、三个结算界面各有一行 DNF_RESULT_SOUND(M_RESULT)。
#   8. 触屏操作层(M13,docs/mobile.md,本项目自己的设计):-dnftouchscript 按时间表替手指操作,走真实的触屏路径(时间表 → TouchControls → 与键盘同一组输入 → 会话);
#      城镇里把摇杆推到最外圈向左再抬起,与键盘 -dnfautowalk 同一个终点;房间里用 stick/tap 复刻技能时间表,A、S、D 各放出一次且命中过怪物。
#   9. 死亡复活、失败回城与 Esc 菜单(E-115):一击必杀的怪物伤害(测试旋钮)下的三段实机运行,查 DNF_REVIVE、DNF_FLOW、DNF_MENU 行;
#      金币(E-116):整条流程日志里最后一行 DNF_GOLD 掉出与捡起一样多,带伤害的复活那一趟里钱包(purse)不为 0。
#      物品(E-117):日志里若有 DNF_ITEM 行,最后一行掉出与捡起一样多、没有因背包满而留下的。
#     背包与装备栏(E-118):副本里探针放一件武器、开背包、试穿、关上,日志里 DNF_BAG 各行齐全,主角的武器外观已换成装备栏里的那件。
#     角色存档(M26):第一趟打副本、穿一件武器,退出时存;第二趟读同一个存档,日志里读到的与存下的一样,主角外观已是那件武器,读完没有新写。
#     NPC 商店(E-119):城镇里探针打开第一家有商品的店(游戏暂停)、买一件再卖回去、关上(游戏恢复),日志里 DNF_SHOP 各行齐全,进城时记到摆着开商店的 NPC。
#     消耗品(M28,E-120):副本里探针放一种回血药与一种回蓝药、压低主角的生命魔法,像按键 1、1、2 那样连用(同一种药连用第二次被冷却拒绝),等药效发完,日志里 DNF_POTION 各行齐全。
#     中文文字(M29,E-121):流程里装系统自带的带汉字字体与原版文字表;背包与商店的窗口画过汉字(日志 DNF_FONT ready 与 DNF_FONT stats glyphs>0,只记字体文件名与个数)。对拍与离屏出图不装字体,画面与 CPU 参照一致。
#     任务(M31,E-123):城镇里探针找第一个头顶带感叹号的 NPC、打开任务对话(游戏暂停)、接一个任务、关上、按 L 开任务日志、放弃选中的任务、再关上(游戏恢复);
#       日志里 DNF_QUEST 各行齐全(只有个数与结果名,没有任务名与编号),进城时记到头顶带标记的 NPC;窗口画过汉字。
#     副本结算(M32,E-124):整条流程三局副本各有一行 DNF_CLEAR(评分字母与帧一致、评分数据读到、没有等级进度时不发经验);单独一趟带等级进度打通副本,
#       日志里 DNF_CLEAR 的经验已发给主角(granted=True、exp>0)。日志里只有字母与数字。
#     副本难度(M33,E-125):带等级进度、自动机从选择界面把难度升到通关记录允许的最高一档(-dnfraisedifficulty),三局的难度依次是 0、1、1,
#       难度 0 通关写下记录(DNF_CLEAR 的 opened=1),再次挑战沿用难度。
#     可破坏物件(M34,E-126):自动机带 -dnfsmash,清房、捡完金币之后顺手把房间里的可破坏物件打翻;副本不写死,由 dnf breakable-check --pick 按本机数据挑;
#       日志里 DNF_OBJECT 只有个数:进过的房间里有物件、挥中过、打翻过、执行过 [item] 动作。
# 运行演示:
#   macOS:  open unity/DnfUnity/Builds/macos/DnfTownDemo.app --args -dnfdir "<含 Script.pvf 的目录>"
#           (不带参数也行:窗口里有输入框,路径会保存在 PlayerPrefs 里)
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

platform="${1:?用法: $0 <macos|windows|android>}"
UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity}"
[ -x "$UNITY" ] || { echo "找不到 Unity 编辑器: $UNITY" >&2; exit 2; }

# 离屏对拍的 CPU 参照是命令行 dnf:先按当前源码重新构建一遍,免得拿旧二进制对新播放器(源码改过但没重建时会出现整屏不同)
if [ "$platform" = "macos" ]; then
  dotnet build src/Dnf.Cli -c Release -nologo -v q >/dev/null
fi

case "$platform" in
  macos)   method="DnfUnity.Editor.BuildTownDemo.BuildMacOS";   target="StandaloneOSX" ;;
  windows) method="DnfUnity.Editor.BuildTownDemo.BuildWindows"; target="StandaloneWindows64" ;;
  android) method="DnfUnity.Editor.BuildTownDemo.BuildAndroid"; target="Android" ;;
  *) echo "未知平台 $platform" >&2; exit 2 ;;
esac

mkdir -p re out
log="$PWD/re/unity-build-town-$platform.log"
if [ "${DNF_SKIP_BUILD:-}" = "1" ] && [ "$platform" = "macos" ] && [ -d "$PWD/unity/DnfUnity/Builds/macos/DnfTownDemo.app" ]; then
  # 调试用:只改了本脚本里的校验、没动 packages/ 时,沿用上次构建出来的播放器重跑校验(动过 packages/ 之后不要用)
  echo "跳过 Unity 构建(DNF_SKIP_BUILD=1),沿用已有的播放器"
else
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
    # 最后停在哪儿由门体的碰撞形状决定(证据 E-072),所以不写死数字,和命令行 combat-check 同一份时间表的结果比:位置、朝向、镜头都要一致。
    # 怪物会还手(E-086):这类按时刻比事件的回归检查一律带 -dnfnodamage(命令行 --no-damage),让主角打不到、事件的时刻不被受击硬直改变;
    # 怪物还手与主角受击另有专门的检查(见下面"怪物还手")。
    room_walk_log="$PWD/re/unity-run-town-room-walk-macos.log"
    rm -f "$room_walk_log"
    set +e
    "$app" -batchmode -logFile "$room_walk_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 7 -dnfnodamage -dnfautowalk 0.5:left,5:down,6.5:none
    rc=$?
    set -e
    echo "播放器退出码(房间里自动行走): $rc"
    grep -E 'DNF_TOWN_DEMO' "$room_walk_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    cli_hero="$(dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play 0.5:left,5:down,6.5:none --seconds 7 --no-damage \
      | sed -n 's/^结束:主角 (\([0-9.]*,[0-9.]*\)),.*/\1/p')"
    [ -n "$cli_hero" ] || { echo "命令行 combat-check 没有给出主角的最终位置" >&2; exit 1; }
    grep -q -E "DNF_TOWN_DEMO ran .* hero=\(${cli_hero}\) facing=left motion=Rest cameraX=0 .*room=true" "$room_walk_log" \
      || { echo "房间里自动行走后主角不在命令行算出的位置 (${cli_hero}) 朝左、站立、镜头 0:$room_walk_log" >&2; exit 1; }
    case "$cli_hero" in *,348.0) ;; *) echo "主角应走到通行带的下沿(y = 348.0),命令行给出 ${cli_hero}" >&2; exit 1 ;; esac

    # 副本房间里的战斗(证据 E-069):时间表是 "0.2 秒起向左上走、1.0 秒起只向左走、2.3 秒停下、3.6/4.3/5.0/5.7 秒各按一下攻击键"。
    # 主角走进怪物堆里再出手:怪物要先发现主角、过一会儿才起步、每隔约一秒才校正一次方向(走位大脑,E-114),所以出手时刻比旧占位 AI 晚;
    # 改了怪物的走位之后时刻要重选(命令行 combat-check --play 同一份时间表,看命中的时刻)。
    # 播放器带 -dnfplayat 时按固定 1/60 秒步长确定地模拟到指定时刻,再离屏渲染一帧;命令行 combat-check 用同一份时间表与同一个模拟类,
    # 在同一时刻写出 PNG。3.9 秒:第一只怪物刚受击(受击帧、血条、伤害数字、连击提示);4.6 秒:一只在死亡、其余受击。
    fight_play="0.2:left+up,1.0:left,2.3:none,3.6:attack,4.3:attack,5.0:attack,5.7:attack"
    for at in 3.9 4.6; do
      tag="room-fight-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-room-fight-$at.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfnodamage -dnfautowalk "$fight_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$fight_play" --seconds "$at" --no-damage \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-fight" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-fight-$(printf '%.2f' "$at").png" --max-diff 0
    done
    # 实时运行同一份时间表 7 秒:帧率不固定,所以不比像素,只检查按键确实进了战斗(至少命中一次)、没有异常
    fight_log="$PWD/re/unity-run-town-room-fight-macos.log"
    rm -f "$fight_log"
    set +e
    "$app" -batchmode -logFile "$fight_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 7 -dnfnodamage -dnfautowalk "$fight_play"
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
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfnodamage -dnfautowalk "$action_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$action_play" --seconds "$at" --no-damage \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-action" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-action-$(printf '%.2f' "$at").png" --max-diff 0
    done

    # 技能 A S D(证据 E-076):时间表是 "0.2 秒起向左上走、1.0 秒起只向左走、2.3 秒停下、3.3 秒按 A(鬼斩)、5.0 秒按 S(上挑)、6.0 秒按 D(后跳)"
    # (主角站进怪物堆里放技能;怪物走位按 E-114 之后重选了路线)。
    # 3.5 秒:鬼斩在 3.47 秒命中,主角与被打中的怪物正处在命中停顿里(E-077);3.55 秒:紧接着的一帧,斩击光影两层叠在前面;
    # 3.7 秒:光影收尾;5.2 秒:上挑(连招第三段的挥击);6.2 秒:后跳腾空。
    # 播放器与命令行 combat-check 同一份时间表、同一个模拟类,逐字节对照;再实时跑一遍,检查三个技能都放出来了。
    skill_play="0.2:left+up,1.0:left,2.3:none,3.3:a,5.0:s,6.0:d,7.0:none"
    for at in 3.5 3.55 3.7 5.2 6.2; do
      tag="room-skill-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-room-skill-$at.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfnodamage -dnfautowalk "$skill_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$skill_play" --seconds "$at" --no-damage \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-skill" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-skill-$(printf '%.2f' "$at").png" --max-diff 0
    done
    skill_log="$PWD/re/unity-run-town-room-skill-macos.log"
    rm -f "$skill_log"
    set +e
    "$app" -batchmode -logFile "$skill_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 8 -dnfnodamage -dnfautowalk "$skill_play"
    rc=$?
    set -e
    echo "播放器退出码(房间里实时放技能): $rc"
    grep -E 'DNF_TOWN_DEMO' "$skill_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$skill_log"; then
      echo "实时放技能的日志里有异常:$skill_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 hits=[1-9][0-9]* .* casts=3 ' "$skill_log" \
      || { echo "实时放技能里 A、S、D 没有各放出一次、或没有命中过怪物(应有 casts=3 与 hits>=1):$skill_log" >&2; exit 1; }

    # 触屏操作层(M13,docs/mobile.md):-dnftouchscript 按时间表替手指操作,走的是真实的触屏路径(时间表 → TouchControls → 与键盘同一组输入 → 会话)。
    # 城镇里把摇杆推到最外圈向左(奔跑)再抬起:与上面的 -dnfautowalk 同一个终点(可走区域左边界、朝左、站立、镜头 0);
    # 房间里用 stick/tap 复刻上面的技能时间表:A、S、D 各放出一次且命中过怪物。
    touch_walk_log="$PWD/re/unity-run-town-touch-walk-macos.log"
    rm -f "$touch_walk_log"
    set +e
    "$app" -batchmode -logFile "$touch_walk_log" -dnfdir "$DNF_DIR" -dnfquitafter 4 -dnftouchscript 0.5:stick=left+run,3:stick=none
    rc=$?
    set -e
    echo "播放器退出码(触屏摇杆向左奔跑): $rc"
    grep -E 'DNF_TOUCH|DNF_TOWN_DEMO' "$touch_walk_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$touch_walk_log"; then
      echo "触屏行走的日志里有异常:$touch_walk_log" >&2
      exit 1
    fi
    grep -q 'DNF_TOUCH on ' "$touch_walk_log" || { echo "触屏操作层没有打开:$touch_walk_log" >&2; exit 1; }
    grep -q 'DNF_TOWN_DEMO ran .* facing=left motion=Rest cameraX=0 leftEdge=True' "$touch_walk_log" \
      || { echo "触屏摇杆向左奔跑后主角不在预期的状态(应停在可走区域左边界上、朝左、站立、镜头 0):$touch_walk_log" >&2; exit 1; }
    touch_skill_play="0.2:stick=left+up,1.0:stick=left,2.3:stick=none,3.3:tap=a,5.0:tap=s,6.0:tap=d,7.0:stick=none"
    touch_skill_log="$PWD/re/unity-run-town-touch-skill-macos.log"
    rm -f "$touch_skill_log"
    set +e
    "$app" -batchmode -logFile "$touch_skill_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 8 -dnfnodamage -dnftouchscript "$touch_skill_play"
    rc=$?
    set -e
    echo "播放器退出码(房间里触屏放技能): $rc"
    grep -E 'DNF_TOUCH|DNF_TOWN_DEMO' "$touch_skill_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$touch_skill_log"; then
      echo "触屏放技能的日志里有异常:$touch_skill_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 hits=[1-9][0-9]* .* casts=3 ' "$touch_skill_log" \
      || { echo "触屏放技能里 A、S、D 没有各放出一次、或没有命中过怪物(应有 casts=3 与 hits>=1):$touch_skill_log" >&2; exit 1; }

    # 死亡(证据 E-084):时间表是 "0.2 秒起向左上走、1.0 秒起只向左走、2.3 秒停下、3.6/4.3/5.0/5.7 秒各按一下攻击"(与上面的战斗同一条路线,走位见 E-114)。
    # 白闪与淡出从血量归零算起,腾空的尸体也是白的。
    # 4.6 秒:第一只被打死的怪物纯白、还在飞;4.75 秒:它淡出到一半;5.3 秒:后面被打死的两只纯白;5.5 秒:它们躺在地上淡出。
    # 剪影是缓存下来的白色副本帧(SpriteFrame.Silhouette),淡出走 Normal 混合的整体不透明度;播放器与命令行 combat-check 同一份时间表,逐字节对照。
    death_play="0.2:left+up,1.0:left,2.3:none,3.6:attack,4.3:attack,5.0:attack,5.7:attack"
    for at in 4.6 4.75 5.3 5.5; do
      tag="room-death-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-room-death-$at.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfnodamage -dnfautowalk "$death_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$death_play" --seconds "$at" --no-damage \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-death" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-death-$(printf '%.2f' "$at").png" --max-diff 0
    done

    # 怪物还手(证据 E-086):时间表是 "0.2 秒起向左走、3.5 秒停下",主角不出手,站在房间左端等怪物走过来打他。
    # 怪物按走位大脑(E-114)发现主角、起步、走到身边才出招,第一下比旧占位 AI 晚得多:
    # 9.15 秒:第一下刚打中(受击姿势、左下角生命条掉了一截、头顶红色伤害数字);9.65 秒:受击姿势已过去、数字已经飘上去一段。
    # 播放器与命令行 combat-check 同一份时间表、同一个模拟类,逐字节对照(怪物和主角的受击框、出招、伤害、硬直与击退全在里面);
    # 再实时跑 12 秒,检查日志里主角确实被打中过(heroHits >= 1)且没有异常。时刻跟着怪物走过来的节奏,改了怪物行为要重选。
    hurt_play="0.2:left,3.5:none"
    for at in 9.15 9.65; do
      tag="room-hurt-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-room-hurt-$at.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfmap map/lorien/s2.map -dnfautowalk "$hurt_play" -dnfplayat "$at"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play "$hurt_play" --seconds "$at" \
        --shots-at "$at" --png "$PWD/out/town-cpu-room-hurt" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-hurt-$(printf '%.2f' "$at").png" --max-diff 0
    done
    hurt_log="$PWD/re/unity-run-town-room-hurt-macos.log"
    rm -f "$hurt_log"
    set +e
    "$app" -batchmode -logFile "$hurt_log" -dnfdir "$DNF_DIR" -dnfmap map/lorien/s2.map -dnfquitafter 12 -dnfautowalk "$hurt_play"
    rc=$?
    set -e
    echo "播放器退出码(房间里实时挨打): $rc"
    grep -E 'DNF_TOWN_DEMO' "$hurt_log" || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$hurt_log"; then
      echo "实时挨打的日志里有异常:$hurt_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 .* heroHits=[1-9][0-9]* heroHp=[0-9]+/[0-9]+' "$hurt_log" \
      || { echo "实时挨打里主角没有被打中过(应有 heroHits>=1):$hurt_log" >&2; exit 1; }

    # 整座副本(证据 E-070):洛兰的迷宫,自动机(DungeonBot)打副本。
    # 播放器带 -dnfdungeon -dnfexplore(或 -dnfbot)-dnfplayat 时按固定 1/60 秒步长确定地打到指定时刻,再离屏渲染一帧;
    # 命令行 dungeon-check --bot(--explore)是同一局、同一时刻写 PNG,逐字节对照。
    # 逛遍所有房间的路线:6.8 秒(第一间房刚清空,出口门洞淡出打开到一半,小地图)、9.83 秒(走进门洞,黑幕淡出到一半)、10.0 秒(黑幕全黑,已换进下一间房)、
    # 10.2 秒(黑幕淡入到一半)、21.0 秒(已来回换过房间)、48.0 秒(刚进首领房)、59.7 秒(首领房清空:CLEAR 横幅与完整小地图);
    # 直奔首领的最短路线:14.0 秒(刚进首领房,黑幕淡入快结束)、14.6 秒(首领房的第一帧清晰画面)。
    # 这些时刻跟着出门的节奏走(证据 E-072:淡出 250 毫秒、全黑 130 毫秒、淡入 250 毫秒);节奏改了要重选时刻。
    # (怪物与自动机的行为每改一次,各帧实际落在哪个阶段就会漂一点,上面写的画面是最初选时刻时的样子;对拍要求的是命令行与播放器在同一时刻逐字节相同,这一点不受漂移影响。
    # 但路线必须是"逛遍六个房间再打首领"、全程不早于这些时刻结束:太早通关命令行就写不出那一时刻的帧。)
    # 按时刻对拍的这几帧带 -dnfnodamage(命令行 --no-damage):怪物还手会让清房的时刻后移,选好的时刻就不再对应原来的画面;
    # 下面实时跑的一整局是带伤害的,检查主角活到了最后(生命随人带进每个房间)。
    dungeon=dungeon/act1/lorien.dgn
    # 结算画面(证据 E-091):直奔首领的路线在 27.93 秒清空首领房、30.93 秒出结算;规格最后一段是"清空之后再让时间走多少秒"(命令行 --after-clear、播放器 -dnfafterclear):
    # 31.2 秒(黑幕淡入刚开始,两层黑幕各 alpha 约 30)、33.0 秒(营火生起、德利拉站在旁边、黑幕接近全浓)、36.0 秒(黑幕全浓、营火循环到别的帧)。
    for spec in "explore:6.8" "explore:9.83" "explore:10.0" "explore:10.2" "explore:21.0" "explore:48.0" "explore:59.7" "bot:14.0" "bot:14.6" "bot:31.2:9" "bot:33.0:9" "bot:36.0:9"; do
      IFS=: read -r mode at after <<< "$spec"
      tag="dungeon-$mode-$at"
      ms="$(python3 -c "print(int(float('$at')*1000.0))")"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-dungeon-$mode-$(printf '%.2f' "$at").png"
      cli_args=(--dungeon "$dungeon" --bot --no-damage --seconds "$at" --shots-at "$at" --png "$PWD/out/town-cpu-dungeon-$mode")
      [ "$mode" = "explore" ] && cli_args+=(--explore)
      player_extra=()
      if [ -n "${after:-}" ]; then
        cli_args+=(--after-clear "$after")
        player_extra=(-dnfafterclear "$after")
      fi
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" \
        -dnftime "$ms" -dnfdungeon "$dungeon" "-dnf$mode" -dnfnodamage -dnfplayat "$at" ${player_extra[@]+"${player_extra[@]}"}
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
    grep -q -E 'DNF_TOWN_DEMO ran .* dungeon=.* heroHits=[0-9]+ heroHp=[1-9][0-9]*/[0-9]+' "$dungeon_log" \
      || { echo "实时打副本结束时主角应还活着(heroHp 不为 0,日志里要有 heroHits 与 heroHp):$dungeon_log" >&2; exit 1; }
    # 脚本怪物(M36,证据 E-128):状态日志带着房间里由 .ai 脚本驱动的怪物个数与已发现主角的个数(scripted=、aware=;个数取决于这座副本,可以是 0),
    # 脚本怪物的行为本身由命令行全库扫描与单元测试查,这里只确认播放器里这条路径编过、日志字段在
    grep -q -E 'DNF_TOWN_DEMO ran .* monsters=[0-9]+/[0-9]+ scripted=[0-9]+ aware=[0-9]+ hits=' "$dungeon_log" \
      || { echo "实时打副本的状态日志里应有 scripted= 与 aware= 字段:$dungeon_log" >&2; exit 1; }
    # 首领清空 3 秒后进结算:营火生起(camp=层数/NPC 数,至少一层火),没有人选菜单所以 choice=None(证据 E-091)
    grep -q -E 'DNF_TOWN_DEMO ran .* settlement=True camp=[1-9][0-9]*/[0-9]+ choice=None' "$dungeon_log" \
      || { echo "实时打副本没有进到结算(应有 settlement=True、营火至少一层、choice=None):$dungeon_log" >&2; exit 1; }
    # 声音(证据 E-089、E-090):带 -batchmode 的运行默认静音,但整条链路照跑(提示 → 调度 → 读剪辑 → 放),所以能查计数器:
    # 收到过提示、真的开始放过、读成功过剪辑、没有读失败的文件;不对具体数字下结论(帧率不固定,放的次数会变)
    grep -q -E 'DNF_AUDIO ready ' "$dungeon_log" \
      || { echo "实时打副本没有打开声音(日志里应有 DNF_AUDIO ready):$dungeon_log" >&2; exit 1; }
    audio_line="$(grep -E 'DNF_AUDIO cues=' "$dungeon_log" | tail -1)"
    echo "$audio_line"
    echo "$audio_line" | grep -q -E 'cues=[1-9][0-9]* .* started=[1-9][0-9]* .* loaded=[1-9][0-9]* failed=0 ' \
      || { echo "声音的计数不对(应有 cues>=1、started>=1、loaded>=1、failed=0):$dungeon_log" >&2; exit 1; }

    # 整条流程(证据 E-093、E-094)。副本选择界面是原版的世界地图界面(底图、标记、卡片、横幅、按钮都是图包里的图,卡片缩放由自己的双线性算,所以 CPU 与 GPU 一致):
    # 离屏渲染 0.7 / 2.0 秒两个时刻(地图名横幅停留中、卡片全部出现)与命令行 flow-check --from select 逐字节对照。
    # 两个时刻都选在不透明度恒定的区间里(横幅停留 0.3 到 1.0 秒、卡片 1.6 秒之后到按键提示横幅 3 秒之前),不在淡入淡出的边界上:
    # CPU 按 1/60 秒步长走到的那一帧(--shots-at 0 实际是第一步之后的 16 毫秒,横幅已经亮起一点)与播放器直接走到的时刻差几毫秒也不影响像素
    rm -f out/town-demo-flow-select-*.png out/town-cpu-flow-select-*.png
    dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --from select --choices town --seconds 3 --shots-at 0.7,2.0 --png "$PWD/out/town-cpu-flow-select" >/dev/null || [ $? -eq 2 ]
    for pair in 700:0.70 2000:2.00; do
      shot_ms="${pair%%:*}"
      cpu_sec="${pair##*:}"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-flow-select-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-flow-select-$shot_ms.png" \
        -dnftime "$shot_ms" -dnfflow -dnfflowfrom select
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染副本选择界面 t=${shot_ms}ms): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-flow-select-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      python3 tools/py/png_diff.py "out/town-demo-flow-select-$shot_ms.png" "out/town-cpu-flow-select-$cpu_sec.png" --max-diff 0
    done
    # 选角界面(证据 E-096):原版的背景、窗口边框、标题、旗、底座、选中框、滚动条、按钮、鬼剑士头像都是图包里的图(边框与滚动条、按钮是几十个小图块合成的大图),
    # 光束是 .ani 动画;离屏渲染 0、0.7、1.4 秒三个时刻(-dnfflow -dnfflowfrom char,界面时间一次走到这一刻)与命令行 charselect-shot 逐字节对照
    rm -f out/town-demo-charselect-*.png out/town-cpu-charselect-*.png
    for shot_ms in 0 700 1400; do
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-charselect-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-charselect-$shot_ms.png" \
        -dnftime "$shot_ms" -dnfflow -dnfflowfrom char
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染选角界面 t=${shot_ms}ms): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-charselect-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll charselect-shot --time "$shot_ms" --png "$PWD/out/town-cpu-charselect-$shot_ms.png" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-charselect-$shot_ms.png" "out/town-cpu-charselect-$shot_ms.png" --max-diff 0
    done
    # 加载画面(证据 E-095):流程从头开始就停在加载画面,所以 -dnfflow(-dnfflowfrom town 进城、dungeon 进图)离屏渲染 0.5 秒的一帧就是加载画面,
    # 与命令行 flow-check --shots-at 0.5 逐字节对照(过场图、整屏边条、标题三层,画面是静止的)
    rm -f out/town-demo-splash-*.png out/town-cpu-splash-*.png
    for from in town dungeon; do
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --from "$from" --seconds 1 --shots-at 0.5 --png "$PWD/out/town-cpu-splash-$from" >/dev/null || [ $? -eq 2 ]
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-splash-$from-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-splash-$from.png" \
        -dnftime 500 -dnfflow -dnfflowfrom "$from"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染加载画面 $from): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-splash-$from-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      python3 tools/py/png_diff.py "out/town-demo-splash-$from.png" "out/town-cpu-splash-$from-0.50.png" --max-diff 0
    done
    # 城镇图册里的区域(证据 E-095):按登录落脚点出生的主角 + 房间式合成(背景、瓦片、图层、NPC、主角同一次深度排序),
    # 离屏渲染一帧(-dnftownarea 城镇/区域)与命令行 town-shot 逐字节对照:赛丽亚房间、艾尔文防线、地下城入口区域
    for area in 1/1 1/0 1/2; do
      tag="town-area-${area/\//-}"
      rm -f "out/town-demo-$tag.png" "out/town-cpu-$tag.png"
      set +e
      "$app" -batchmode -logFile "$PWD/re/unity-run-town-$tag-macos.log" -dnfdir "$DNF_DIR" -dnfshot "$PWD/out/town-demo-$tag.png" -dnftime 545 -dnftownarea "$area"
      rc=$?
      set -e
      echo "播放器退出码(离屏渲染 $tag): $rc"
      grep -E 'DNF_TOWN_DEMO' "$PWD/re/unity-run-town-$tag-macos.log" || true
      [ "$rc" -eq 0 ] || exit "$rc"
      dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll town-shot --area "$area" --time 545 --png "$PWD/out/town-cpu-$tag.png" >/dev/null
      python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-$tag.png" --max-diff 0
    done
    # 实时运行:流程自动机走完 选角(停一会儿确认第一个角色,E-096)→ 登录加载 → 赛丽亚房间 → 走出口(加载)→ 艾尔文防线 → 走出口 → 地下城入口区域 →
    # 走出口(世界地图)→ 选择 → 加载 → 副本 → 结算 → 再次挑战(加载)→ 副本 → 结算 → 选择其它地下城 → 选择 → 加载 → 副本 → 结算 → 返回城镇(加载);
    # 时间步放大 4 倍,走完自动退出(-dnfquitafter 只是上限)。要有十六次阶段切换(第一次是进入选角界面)、三局副本、三次菜单选择,自动机说走完了,最后停在城镇
    flow_log="$PWD/re/unity-run-town-flow-macos.log"
    rm -f "$flow_log"
    set +e
    "$app" -batchmode -logFile "$flow_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom char -dnfbot -dnfnodamage -dnfdungeon "$dungeon" -dnfflowchoices retry,select,town \
      -dnftimescale 4 -dnfquitafter 150
    rc=$?
    set -e
    echo "播放器退出码(实时走完整条流程): $rc"
    grep -E 'DNF_FLOW|DNF_TOWN_DEMO ran' "$flow_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$flow_log"; then
      echo "整条流程的日志里有异常:$flow_log" >&2
      exit 1
    fi
    [ "$(grep -c -E 'DNF_FLOW serial=' "$flow_log")" -eq 16 ] \
      || { echo "整条流程应有十六次阶段切换(DNF_FLOW 行):$flow_log" >&2; exit 1; }
    grep -q -E 'DNF_FLOW serial=1 from=[A-Za-z]+ to=CharacterSelect reason="start"' "$flow_log" \
      || { echo "整条流程应从选角界面开始(第一行 DNF_FLOW 应是进入 CharacterSelect):$flow_log" >&2; exit 1; }
    grep -q -E 'DNF_FLOW serial=2 from=CharacterSelect to=Loading reason="login"' "$flow_log" \
      || { echo "选角之后应登录进城(第二行 DNF_FLOW 应是 CharacterSelect -> Loading,原因 login):$flow_log" >&2; exit 1; }
    grep -q -E 'DNF_TOWN_DEMO ran .* flow=Town serial=16 runs=3 settlements=3 botDone=True choicesMade=3' "$flow_log" \
      || { echo "整条流程没有走完(应停在城镇、三局副本、三次菜单选择、自动机说走完了):$flow_log" >&2; exit 1; }
    flow_audio="$(grep -E 'DNF_AUDIO cues=' "$flow_log" | tail -1)"
    echo "$flow_audio"
    echo "$flow_audio" | grep -q -E 'cues=[1-9][0-9]* .* started=[1-9][0-9]* .* loaded=[1-9][0-9]* failed=0 ' \
      || { echo "整条流程的声音计数不对(应有 cues>=1、started>=1、loaded>=1、failed=0):$flow_log" >&2; exit 1; }
    # 场景音乐(E-097):第一首是选角音乐;城镇区域与副本房间各自换音乐(至少七次);三个结算界面各放一声 M_RESULT;日志行数与计数器一致
    grep -E 'DNF_MUSIC id=|DNF_RESULT_SOUND' "$flow_log" | cut -c1-120 || true
    first_music="$(grep -E 'DNF_MUSIC id=' "$flow_log" | head -1)"
    echo "$first_music" | grep -q -E 'DNF_MUSIC id=M_CHARACTER_SELECT ' \
      || { echo "第一首背景音乐应是选角音乐 M_CHARACTER_SELECT:$flow_log" >&2; exit 1; }
    music_lines="$(grep -c -E 'DNF_MUSIC id=' "$flow_log")"
    [ "$music_lines" -ge 7 ] \
      || { echo "整条流程里背景音乐应至少开始七次(选角、城镇区域、三局副本),实际 $music_lines:$flow_log" >&2; exit 1; }
    [ "$(grep -c -E 'DNF_RESULT_SOUND id=M_RESULT ' "$flow_log")" -eq 3 ] \
      || { echo "三个结算界面应各放一声 M_RESULT(DNF_RESULT_SOUND 行):$flow_log" >&2; exit 1; }
    echo "$flow_audio" | grep -q -E "musicStarts=$music_lines resultSounds=3 " \
      || { echo "声音计数里的 musicStarts/resultSounds 与日志行数不一致:$flow_log" >&2; exit 1; }
    # 金币(证据 E-116):整条流程的日志里有 DNF_GOLD 行,最后一行掉出的与捡起的一样多且不为 0(自动机把地上的金币都捡完了);
    # 这一趟带 -dnfnodamage(没有等级进度),钱包恒为 0,钱包在下面的死亡复活那一趟里查
    grep -E 'DNF_GOLD dropped=' "$flow_log" | cut -c1-120 || true
    grep -E 'DNF_GOLD dropped=' "$flow_log" | tail -1 | grep -q -E 'DNF_GOLD dropped=([1-9][0-9]*) picked=\1 ' \
      || { echo "整条流程里自动机应捡完掉在地上的金币(最后一行 DNF_GOLD 的 dropped 与 picked 相等且不为 0):$flow_log" >&2; exit 1; }
    # 物品(证据 E-117):怪物的物品概率很低,一趟里可能一件也没掉,所以只在有 DNF_ITEM 行时要求最后一行掉出的与捡起的一样多、没有因背包满留在地上的
    grep -E 'DNF_ITEM dropped=' "$flow_log" | cut -c1-140 || true
    if grep -q -E 'DNF_ITEM dropped=' "$flow_log"; then
      grep -E 'DNF_ITEM dropped=' "$flow_log" | tail -1 | grep -q -E 'DNF_ITEM dropped=([0-9]+) picked=\1 full=0 ' \
        || { echo "整条流程里自动机应捡完掉在地上的物品(最后一行 DNF_ITEM 的 dropped 与 picked 相等、full=0):$flow_log" >&2; exit 1; }
    fi
    # 副本结算(M32,证据 E-124):三局副本各结算一次(DNF_CLEAR 三行),评分字母与图的帧一致(SSS 0、SS 1、S 2、A 3、B 4、C 5、D 6、E 7、F 8),评分数据读到(ranked=True);
    # 这一趟带 -dnfnodamage(没有等级进度),经验不发(granted=False exp=0 bonus=0)
    grep -E 'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=' "$flow_log" | cut -c1-200 || true
    [ "$(grep -c -E 'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=' "$flow_log")" -eq 3 ] \
      || { echo "整条流程三局副本应各有一行 DNF_CLEAR:$flow_log" >&2; exit 1; }
    [ "$(grep -c -E 'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=[A-Z]+ frame=[0-8] ranked=True .* kills=[1-9][0-9]* .* granted=False exp=0 bonus=0 levels=0' "$flow_log")" -eq 3 ] \
      || { echo "三行 DNF_CLEAR 都应读到评分数据(ranked=True)、打死过怪物、没有等级进度时不发经验(granted=False exp=0 bonus=0):$flow_log" >&2; exit 1; }
    python3 tools/py/check_clear_log.py "$flow_log" 3 \
      || { echo "DNF_CLEAR 的评分字母与帧对不上:$flow_log" >&2; exit 1; }
    # 失败分支(证据 E-115):怪物伤害放大到一击必杀、复活币 0 枚,无人值守地打副本——主角死后没有币可用,躺平 10 秒的计时走完,副本失败回城镇,
    # 不算结算(回城先经过加载画面,所以从副本出去的第一行是 to=Loading);一并看 DNF_REVIVE 行(死了一次、没复活)与流程计数(failures=1、settlements=0)
    fail_log="$PWD/re/unity-run-town-fail-macos.log"
    rm -f "$fail_log"
    set +e
    "$app" -batchmode -logFile "$fail_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" \
      -dnfmonsterdamage 100000 -dnfcoins 0 -dnftimescale 4 -dnfquitafter 30
    rc=$?
    set -e
    echo "播放器退出码(失败回城): $rc"
    grep -E 'DNF_FLOW|DNF_REVIVE' "$fail_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$fail_log"; then
      echo "失败回城的日志里有异常:$fail_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_REVIVE deaths=1 revives=0 coins=0' "$fail_log" \
      || { echo "没有币的主角应死一次、没有复活(DNF_REVIVE deaths=1 revives=0 coins=0):$fail_log" >&2; exit 1; }
    grep -q -E 'DNF_FLOW serial=[0-9]+ from=Dungeon to=(Loading|Town) reason="dungeon fail" runs=1 settlements=0 failures=1 giveups=0' "$fail_log" \
      || { echo "失败计时走完应回城镇、不算结算(reason=dungeon fail、settlements=0、failures=1):$fail_log" >&2; exit 1; }
    # 复活分支:同样一击必杀、币给足,无人值守的自动机在死亡窗口里按攻击键用币,复活之后继续打,不应失败回城
    revive_log="$PWD/re/unity-run-town-revive-macos.log"
    rm -f "$revive_log"
    set +e
    "$app" -batchmode -logFile "$revive_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" \
      -dnfmonsterdamage 100000 -dnfcoins 99 -dnftimescale 4 -dnfquitafter 25
    rc=$?
    set -e
    echo "播放器退出码(死亡复活): $rc"
    grep -E 'DNF_FLOW|DNF_REVIVE|DNF_GOLD' "$revive_log" | cut -c1-260 | head -16 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$revive_log"; then
      echo "死亡复活的日志里有异常:$revive_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_REVIVE deaths=[1-9][0-9]* revives=[1-9][0-9]* coins=' "$revive_log" \
      || { echo "币给足的主角死后应用币复活(DNF_REVIVE 行里 deaths 与 revives 都不为 0):$revive_log" >&2; exit 1; }
    if grep -q -E 'reason="dungeon fail"' "$revive_log"; then
      echo "币给足时不应失败回城:$revive_log" >&2
      exit 1
    fi
    # 金币(证据 E-116):这一趟带伤害、有等级进度,捡到的金币进钱包(purse 不为 0)
    grep -q -E 'DNF_GOLD dropped=[1-9][0-9]* picked=[1-9][0-9]* earned=[1-9][0-9]* purse=[1-9][0-9]*' "$revive_log" \
      || { echo "死亡复活那一趟里主角应捡到金币、钱包里有钱(DNF_GOLD 行的 picked、earned、purse 都不为 0):$revive_log" >&2; exit 1; }
    # 副本结算与通关经验(M32,证据 E-124):带等级进度(20 级)、怪物照常还手,自动机打通整座副本;首领清空的那一刻结算一次并把通关经验发给主角,
    # 日志里 DNF_CLEAR 的评分数据读到(ranked=True)、经验发了(granted=True、exp>0),评分字母与帧一致;没有异常
    clear_log="$PWD/re/unity-run-town-clear-macos.log"
    rm -f "$clear_log"
    set +e
    "$app" -batchmode -logFile "$clear_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" -dnflevel 20 \
      -dnftimescale 4 -dnfquitafter 25
    rc=$?
    set -e
    echo "播放器退出码(副本结算与通关经验): $rc"
    grep -E 'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=' "$clear_log" | cut -c1-200 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$clear_log"; then
      echo "副本结算的日志里有异常:$clear_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=[A-Z]+ frame=[0-8] ranked=True .* kills=[1-9][0-9]* .* granted=True exp=[1-9][0-9]* bonus=[0-9]+ levels=[0-9]+' "$clear_log" \
      || { echo "带等级进度打通副本应结算出评分并发了通关经验(DNF_CLEAR … ranked=True … granted=True exp>0):$clear_log" >&2; exit 1; }
    python3 tools/py/check_clear_log.py "$clear_log" 1 \
      || { echo "DNF_CLEAR 的评分字母与帧对不上:$clear_log" >&2; exit 1; }
    # 副本难度(M33,证据 E-125):带等级进度(20 级),自动机从副本开局;打通后选"选择其它地下城"回到选择界面,自动机把难度调到通关记录允许的最高一档
    # (-dnfraisedifficulty)再进;再"再次挑战"沿用难度。三局的难度依次是 0、1、1;第一局(难度 0)通关写下记录,日志里 opened=1;评分字母与帧一致;没有异常
    difficulty_log="$PWD/re/unity-run-town-difficulty-macos.log"
    rm -f "$difficulty_log"
    set +e
    "$app" -batchmode -logFile "$difficulty_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" -dnflevel 20 -dnfraisedifficulty \
      -dnfflowchoices select,retry,town -dnftimescale 4 -dnfquitafter 120
    rc=$?
    set -e
    echo "播放器退出码(副本难度): $rc"
    grep -E 'DNF_CLEAR difficulty=' "$difficulty_log" | cut -c1-120 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$difficulty_log"; then
      echo "副本难度的日志里有异常:$difficulty_log" >&2
      exit 1
    fi
    [ "$(grep -E 'DNF_CLEAR difficulty=' "$difficulty_log" | sed -E 's/DNF_CLEAR difficulty=([0-9]+) .*/\1/' | tr '\n' ' ')" = "0 1 1 " ] \
      || { echo "三局副本的难度应依次是 0、1、1(自动机从选择界面升到记录允许的最高一档,再次挑战沿用):$difficulty_log" >&2; exit 1; }
    grep -q -E 'DNF_CLEAR difficulty=0 opened=1 ' "$difficulty_log" \
      || { echo "难度 0 通关应写下通关记录并开放难度 1(DNF_CLEAR difficulty=0 opened=1):$difficulty_log" >&2; exit 1; }
    python3 tools/py/check_clear_log.py "$difficulty_log" 3 2 \
      || { echo "难度日志里的 DNF_CLEAR 对不上:$difficulty_log" >&2; exit 1; }
    # Esc 系统菜单:无伤害打副本,阶段开始 3 秒后探针像按 Esc 一样打开菜单(游戏暂停)、再过一秒选第 2 项"放弃回城";
    # 流程应回城镇,算一次放弃(giveups=1),不算结算也不算失败
    menu_log="$PWD/re/unity-run-town-menu-macos.log"
    rm -f "$menu_log"
    set +e
    "$app" -batchmode -logFile "$menu_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfnodamage -dnfdungeon "$dungeon" \
      -dnfmenuat 3 -dnfmenuchoose 2 -dnftimescale 4 -dnfquitafter 20
    rc=$?
    set -e
    echo "播放器退出码(系统菜单放弃回城): $rc"
    grep -E 'DNF_FLOW|DNF_MENU' "$menu_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$menu_log"; then
      echo "系统菜单的日志里有异常:$menu_log" >&2
      exit 1
    fi
    for pat in 'DNF_MENU probe open items=3' 'DNF_MENU open stage=Dungeon' 'DNF_MENU probe choose 2' 'DNF_MENU closed'; do
      grep -q -F "$pat" "$menu_log" || { echo "系统菜单的日志里缺 \"$pat\":$menu_log" >&2; exit 1; }
    done
    grep -q -E 'DNF_FLOW serial=[0-9]+ from=Dungeon to=(Loading|Town) reason="menu return" runs=1 settlements=0 failures=0 giveups=1' "$menu_log" \
      || { echo "菜单放弃回城应回城镇、算一次放弃(reason=menu return、settlements=0、failures=0、giveups=1):$menu_log" >&2; exit 1; }
    # 背包窗口与装备栏(证据 E-118):副本里阶段开始 3 秒后探针先往背包里放一件剑魂穿得上的武器,像按 I 键一样打开背包(游戏暂停)、再过一秒按回车试穿、再过一秒关上,
    # 再过一秒看战斗会话是否已把主角的武器外观换成装备栏里的那件;等级给 30 级好让背包里放得进武器
    bag_log="$PWD/re/unity-run-town-bag-macos.log"
    rm -f "$bag_log"
    set +e
    "$app" -batchmode -logFile "$bag_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" -dnflevel 30 \
      -dnfbagat 3 -dnftimescale 4 -dnfquitafter 25
    rc=$?
    set -e
    echo "播放器退出码(背包与装备栏): $rc"
    grep -E 'DNF_BAG' "$bag_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$bag_log"; then
      echo "背包与装备栏的日志里有异常:$bag_log" >&2
      exit 1
    fi
    for pat in 'DNF_BAG probe open granted=True' 'DNF_BAG open stage=Dungeon paused=True' 'DNF_BAG probe equip ok=True' 'DNF_BAG probe closed equipped=1' 'DNF_BAG closed paused=False' 'DNF_BAG probe look matches=True weapon=True'; do
      grep -q -F "$pat" "$bag_log" || { echo "背包与装备栏的日志里缺 \"$pat\":$bag_log" >&2; exit 1; }
    done
    # 汉字(M29,证据 E-121):真的在玩的流程里装了系统里带汉字的字体,背包窗口画过汉字(栅格化过的字形数大于 0);日志里只记字体文件名与个数,不记文字
    grep -E 'DNF_FONT' "$bag_log" | cut -c1-200 || true
    grep -q -E 'DNF_FONT ready file=' "$bag_log" \
      || { echo "流程里应当装上带汉字的系统字体(DNF_FONT ready):$bag_log" >&2; exit 1; }
    grep -q -E 'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]' "$bag_log" \
      || { echo "背包窗口应当画过汉字(DNF_FONT stats glyphs>0):$bag_log" >&2; exit 1; }
    # NPC 商店(证据 E-119,窗口与键位是本项目自己的占位设计):城镇里阶段开始 3 秒后探针先给钱包放一笔钱,像走到商店 NPC 旁按空格一样打开第一家有商品的店(游戏暂停),
    # 再过一秒无人值守地买一件、卖回去,再过一秒按 Esc 的路关上(游戏恢复),再过一秒收尾;进城时日志里记这座城镇摆着几个 NPC、其中几个开商店
    shop_log="$PWD/re/unity-run-town-shop-macos.log"
    rm -f "$shop_log"
    set +e
    "$app" -batchmode -logFile "$shop_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom town -dnflevel 30 \
      -dnfshopat 3 -dnftimescale 4 -dnfquitafter 10
    rc=$?
    set -e
    echo "播放器退出码(NPC 商店): $rc"
    grep -E 'DNF_SHOP' "$shop_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$shop_log"; then
      echo "NPC 商店的日志里有异常:$shop_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_SHOP town serial=[0-9]+ npcs=[0-9]+ shopnpcs=[1-9][0-9]*' "$shop_log" \
      || { echo "NPC 商店的日志里进城时应记到摆着开商店的 NPC(DNF_SHOP town … shopnpcs>0):$shop_log" >&2; exit 1; }
    grep -q -E 'DNF_SHOP probe open shop=[0-9]+ items=[1-9][0-9]* tabs=' "$shop_log" \
      || { echo "NPC 商店的探针应打开一家有商品的店(DNF_SHOP probe open … items>0):$shop_log" >&2; exit 1; }
    grep -q -E 'DNF_SHOP open shop=[0-9]+ items=[1-9][0-9]* paused=True' "$shop_log" \
      || { echo "开商店时游戏应暂停(DNF_SHOP open … paused=True):$shop_log" >&2; exit 1; }
    for pat in 'DNF_SHOP probe trade bought=True sold=True' 'DNF_SHOP probe closed open=False' 'DNF_SHOP closed paused=False' 'DNF_SHOP probe done'; do
      grep -q -F "$pat" "$shop_log" || { echo "NPC 商店的日志里缺 \"$pat\":$shop_log" >&2; exit 1; }
    done
    grep -q -E 'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]' "$shop_log" \
      || { echo "商店窗口应当画过汉字(DNF_FONT stats glyphs>0):$shop_log" >&2; exit 1; }
    # 城镇底栏与头顶名牌(M30,证据 E-122):有等级进度的城镇里底栏画过(DNF_HUD town drawn),装了字体与文字表时主角名牌与至少一个 NPC 名字画过(DNF_PLATE town);日志里只有个数
    grep -E 'DNF_HUD|DNF_PLATE' "$shop_log" | cut -c1-160 || true
    grep -q -E 'DNF_HUD town drawn serial=[0-9]+ level=[0-9]+ sprites=[1-9][0-9]*' "$shop_log" \
      || { echo "有等级进度的城镇里应画过底栏(DNF_HUD town drawn … sprites>0):$shop_log" >&2; exit 1; }
    grep -q -E 'DNF_PLATE town serial=[0-9]+ npcs=[1-9][0-9]* hero=1' "$shop_log" \
      || { echo "城镇里应画过主角名牌与至少一个 NPC 名字(DNF_PLATE town … npcs>0 hero=1):$shop_log" >&2; exit 1; }
    # 任务系统(M31,证据 E-123,窗口与键位是本项目自己的占位设计):城镇里阶段开始 3 秒后探针找第一个头顶带感叹号的 NPC,像走到它身旁按空格一样打开任务对话(游戏暂停),
    # 再过一秒按回车接第一个可接的任务,再过一秒按 Esc 的路关上(游戏恢复),再过一秒按 L 打开任务日志,再过一秒连按两次删除键放弃选中的任务,再按 L 关上,再过一秒收尾;
    # 主角给 40 级好让起始城镇里有 NPC 可接任务;进城时日志里记这座城镇头顶带标记的 NPC 有几个;日志里只有个数与结果名,没有任务名与编号
    quest_log="$PWD/re/unity-run-town-quest-macos.log"
    rm -f "$quest_log"
    set +e
    "$app" -batchmode -logFile "$quest_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom town -dnflevel 40 \
      -dnfquestat 3 -dnftimescale 4 -dnfquitafter 25
    rc=$?
    set -e
    echo "播放器退出码(任务系统): $rc"
    grep -E 'DNF_QUEST' "$quest_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed|DNF_QUEST problem' "$quest_log"; then
      echo "任务系统的日志里有异常:$quest_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_QUEST ready listed=[1-9][0-9]*' "$quest_log" \
      || { echo "任务系统应在进城前准备好(DNF_QUEST ready … listed>0):$quest_log" >&2; exit 1; }
    grep -q -E 'DNF_QUEST town serial=[0-9]+ marks=[1-9][0-9]* available=[1-9][0-9]*' "$quest_log" \
      || { echo "城镇里应有头顶带可接任务标记的 NPC(DNF_QUEST town … available>0):$quest_log" >&2; exit 1; }
    grep -q -E 'DNF_QUEST marks drawn serial=[0-9]+ count=[1-9][0-9]*' "$quest_log" \
      || { echo "头顶标记应画过(DNF_QUEST marks drawn … count>0):$quest_log" >&2; exit 1; }
    grep -q -E 'DNF_QUEST probe open rows=[1-9][0-9]* ' "$quest_log" \
      || { echo "探针应打开一个有任务的 NPC 对话(DNF_QUEST probe open … rows>0):$quest_log" >&2; exit 1; }
    grep -q -E 'DNF_QUEST window open mode=Npc rows=[1-9][0-9]* paused=True' "$quest_log" \
      || { echo "开任务窗口时游戏应暂停(DNF_QUEST window open mode=Npc … paused=True):$quest_log" >&2; exit 1; }
    grep -q -E 'DNF_QUEST probe accept acted=True result=Ok accepted=1 active=[1-9]' "$quest_log" \
      || { echo "探针应接下一个任务(DNF_QUEST probe accept acted=True result=Ok accepted=1):$quest_log" >&2; exit 1; }
    for pat in 'DNF_QUEST probe closed open=False menu=False' 'DNF_QUEST probe journal opened=True mode=Journal' 'DNF_QUEST probe abandon first=False second=True result=Ok abandoned=1 active=0' 'DNF_QUEST probe closed open=False' 'DNF_QUEST window closed paused=False' 'DNF_QUEST probe done accepted=1 abandoned=1'; do
      grep -q -F "$pat" "$quest_log" || { echo "任务系统的日志里缺 \"$pat\":$quest_log" >&2; exit 1; }
    done
    grep -q -E 'DNF_QUEST toast shown=[1-9][0-9]* ' "$quest_log" \
      || { echo "接任务与放弃都应出过提示条(DNF_QUEST toast shown>0):$quest_log" >&2; exit 1; }
    grep -q -E 'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]' "$quest_log" \
      || { echo "任务窗口应当画过汉字(DNF_FONT stats glyphs>0):$quest_log" >&2; exit 1; }
    # 技能系统(M35,证据 E-127,窗口与键位是本项目自己的占位设计):城镇里阶段开始 3 秒后探针像按 K 一样打开技能窗口(游戏暂停),过一秒在四个分页里找第一个能学的技能、选中、学一级,
    # 再过一秒退一级,再过一秒把第一个学会的主动技能放进快捷栏最后一格,再过一秒连按两次重置(第一下只预备、第二下才执行),再过一秒按 K 关上,再过一秒收尾;
    # 主角给 40 级好让四个分页都有技能可学;日志里只有个数、点数与结果名,没有技能名与编号
    skill_win_log="$PWD/re/unity-run-town-skillwin-macos.log"
    rm -f "$skill_win_log"
    set +e
    "$app" -batchmode -logFile "$skill_win_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom town -dnflevel 40 \
      -dnfskillat 3 -dnftimescale 4 -dnfquitafter 25
    rc=$?
    set -e
    echo "播放器退出码(技能窗口): $rc"
    grep -E 'DNF_SKILL' "$skill_win_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed|DNF_SKILL probe failed' "$skill_win_log"; then
      echo "技能窗口的日志里有异常或探针开不了窗口:$skill_win_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_SKILL probe open page=[0-3] rows=[0-9]+ sp=[1-9][0-9]* ap=[0-9]+ learned=[1-9][0-9]* level=40' "$skill_win_log" \
      || { echo "探针应打开技能窗口(DNF_SKILL probe open … sp>0 learned>0 level=40):$skill_win_log" >&2; exit 1; }
    grep -q -E 'DNF_SKILL window open page=[0-3] rows=[0-9]+ paused=True' "$skill_win_log" \
      || { echo "开技能窗口时游戏应暂停(DNF_SKILL window open … paused=True):$skill_win_log" >&2; exit 1; }
    grep -q -E 'DNF_SKILL probe learn found=True pages=[0-9]+/[0-9]+/[0-9]+/[0-9]+ learned=True result=Ok ' "$skill_win_log" \
      || { echo "探针应学成一个技能(DNF_SKILL probe learn found=True … learned=True result=Ok):$skill_win_log" >&2; exit 1; }
    grep -q -E 'DNF_SKILL probe refund refunded=True result=Ok ' "$skill_win_log" \
      || { echo "探针应退成一级(DNF_SKILL probe refund refunded=True result=Ok):$skill_win_log" >&2; exit 1; }
    for pat in 'DNF_SKILL probe assign ok=True' 'DNF_SKILL probe reset first=False second=True resets=1' 'DNF_SKILL probe closed open=False' 'DNF_SKILL probe done learns=1 refunds=1 resets=1 assigns=1'; do
      grep -q -F "$pat" "$skill_win_log" || { echo "技能窗口的日志里缺 \"$pat\":$skill_win_log" >&2; exit 1; }
    done
    grep -q -E 'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]' "$skill_win_log" \
      || { echo "技能窗口应当画过汉字(DNF_FONT stats glyphs>0):$skill_win_log" >&2; exit 1; }
    # 技能系统(M35)· 自动机学技能:-dnflearn 在进副本前先让主角按自动机策略把技能点与觉醒点花光(快捷栏随之排满六格),自动机再照常打副本;
    # 看点:学满技能后的快捷栏建槽、副本里的底栏与放技能路径都没有出异常,自动机照样清房到结算
    skill_learn_log="$PWD/re/unity-run-town-skilllearn-macos.log"
    rm -f "$skill_learn_log"
    set +e
    "$app" -batchmode -logFile "$skill_learn_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" -dnflevel 40 -dnflearn \
      -dnftimescale 4 -dnfquitafter 40
    rc=$?
    set -e
    echo "播放器退出码(自动机学技能): $rc"
    grep -E 'DNF_SKILL|DNF_FLOW' "$skill_learn_log" | cut -c1-200 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$skill_learn_log"; then
      echo "自动机学技能的日志里有异常:$skill_learn_log" >&2
      exit 1
    fi
    grep -q -E 'DNF_SKILL planner levels=[1-9][0-9]* sp=[1-9][0-9]*>[0-9]+ ap=[0-9]+>[0-9]+ learnedCount=[1-9][0-9]*' "$skill_learn_log" \
      || { echo "自动机策略应花掉技能点(DNF_SKILL planner levels>0):$skill_learn_log" >&2; exit 1; }
    # 消耗品(M28,证据 E-120):副本里阶段开始 3 秒后探针往背包放一种回血药与一种回蓝药(快捷栏自动放进前两格)、把主角的生命压到四成、魔法压到三成,
    # 过一秒像按键 1、1、2 那样连着用(同一种药连用第二次应被冷却拒绝),等药效发完(时间步放大 4 倍),再过一秒再用一次;日志里只有数与结果名,没有物品名与编号
    potion_log="$PWD/re/unity-run-town-potion-macos.log"
    rm -f "$potion_log"
    set +e
    "$app" -batchmode -logFile "$potion_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" -dnflevel 30 \
      -dnfpotionat 3 -dnftimescale 4 -dnfquitafter 40
    rc=$?
    set -e
    echo "播放器退出码(消耗品): $rc"
    grep -E 'DNF_POTION' "$potion_log" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$potion_log"; then
      echo "消耗品的日志里有异常:$potion_log" >&2
      exit 1
    fi
    for pat in 'DNF_POTION probe grant kinds=2 hurt=True' 'DNF_POTION probe use first=Ok again=Cooling second=Ok' 'DNF_POTION probe settled recovering=False' 'DNF_POTION probe done last='; do
      grep -q -F "$pat" "$potion_log" || { echo "消耗品的日志里缺 \"$pat\":$potion_log" >&2; exit 1; }
    done
    grep -q -E 'DNF_POTION probe use first=Ok again=Cooling second=Ok .* used=2 quick=2 cooling=[12]$' "$potion_log" \
      || { echo "连用三次后应是用成两件、快捷栏占两格、一到两种药在冷却(used=2 quick=2 cooling=1或2):$potion_log" >&2; exit 1; }
    grep -q -E 'DNF_POTION probe settled recovering=False .* pending=0/0 ' "$potion_log" \
      || { echo "药效应已发完(pending=0/0):$potion_log" >&2; exit 1; }
    # 地上名牌(M30,证据 E-122):这一趟装了字体与文字表,自动机打的副本里掉出的金币要画过名牌(DNF_LABEL drawn>0)。
    # "选中"的名牌只在主角停在物品旁、而且这一步之后正好出了一帧图时才画得出来——自动机下一步就按攻击键捡走,4 倍时间步下常常来不及出图,
    # 所以实机不要求 selected>0(选中与拾取的规则由单元测试和 EditMode 出图查;物品被捡起本身就说明选中过,见上面的 DNF_ITEM 检查)
    grep -E 'DNF_LABEL' "$potion_log" | cut -c1-120 || true
    grep -q -E 'DNF_LABEL drawn=[1-9][0-9]* selected=[0-9]+' "$potion_log" \
      || { echo "副本里掉在地上的金币应画过名牌(DNF_LABEL drawn>0):$potion_log" >&2; exit 1; }
    # 可破坏物件(M34,证据 E-126):自动机带 -dnfsmash,清房、捡完金币之后顺手把房间里的可破坏物件打翻(掉出的东西由同一套逻辑捡起);
    # 副本不写死具体是哪一座:dnf breakable-check --pick 按本机数据挑第一座格数少、首领格以外几个房间摆着物件的副本;结算选择只留"返回城镇",一局打完流程就走完。
    # 日志里 DNF_OBJECT 只有个数(进过的房间里有几个物件、挥中、打翻、执行过几次 [item] 动作、因此掉出几件),要求都大于 0
    smash_dungeon="$(dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll breakable-check --pick 2>/dev/null || true)"
    if [ -z "$smash_dungeon" ]; then
      echo "没有挑到适合打物件检查的副本(数据里没有可破坏物件?),跳过这一步"
    else
      smash_log="$PWD/re/unity-run-town-smash-macos.log"
      rm -f "$smash_log"
      set +e
      "$app" -batchmode -logFile "$smash_log" -dnfdir "$DNF_DIR" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfsmash -dnfnodamage -dnfdungeon "$smash_dungeon" \
        -dnfflowchoices town -dnftimescale 4 -dnfquitafter 150
      rc=$?
      set -e
      echo "播放器退出码(打物件): $rc"
      grep -E 'DNF_OBJECT' "$smash_log" | tail -3 | cut -c1-200 || true
      [ "$rc" -eq 0 ] || exit "$rc"
      if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$smash_log"; then
        echo "打物件的日志里有异常:$smash_log" >&2
        exit 1
      fi
      grep -q -E 'DNF_OBJECT breakables=[1-9][0-9]* hit=[1-9][0-9]* destroyed=[1-9][0-9]* itemactions=[1-9][0-9]* ' "$smash_log" \
        || { echo "自动机应当打中并打翻过物件、执行过 [item] 动作(DNF_OBJECT breakables/hit/destroyed/itemactions 都大于 0):$smash_log" >&2; exit 1; }
    fi
    # 角色存档(M26,本项目自己的设计):第一趟无人值守打副本、探针往背包放一件武器并穿上,退出时存档;第二趟读同一个存档进城镇,
    # 日志里读到的等级、经验、金币、背包与装备栏和第一趟最后存下的一模一样,主角的外观已是穿着的那件武器,而且读完不用再写
    save_file="$PWD/out/m26-hero.sav"
    rm -f "$save_file" "$save_file.tmp" "$save_file.bad" "$save_file.dropped"
    save_log1="$PWD/re/unity-run-town-save1-macos.log"
    save_log2="$PWD/re/unity-run-town-save2-macos.log"
    rm -f "$save_log1" "$save_log2"
    set +e
    "$app" -batchmode -logFile "$save_log1" -dnfdir "$DNF_DIR" -dnfsave "$save_file" -dnfflow -dnfflowfrom dungeon -dnfbot -dnfdungeon "$dungeon" -dnflevel 30 \
      -dnfbagat 3 -dnftimescale 4 -dnfquitafter 25
    rc=$?
    set -e
    echo "播放器退出码(存档第一趟): $rc"
    grep -E 'DNF_SAVE' "$save_log1" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$save_log1"; then
      echo "存档第一趟的日志里有异常:$save_log1" >&2
      exit 1
    fi
    grep -q -F 'DNF_SAVE open file=m26-hero.sav found=False' "$save_log1" \
      || { echo "第一趟开始时不应有存档(DNF_SAVE open … found=False):$save_log1" >&2; exit 1; }
    [ -s "$save_file" ] || { echo "第一趟结束后应有存档文件:$save_file" >&2; exit 1; }
    # 通关记录(M33,证据 E-125):第一趟打通了副本,存档里有通关记录行(dclear 副本编号 值);第二趟读回去以后不用重写(下面"读完不再写"一并覆盖了这一行)
    grep -q -E '^dclear [0-9]+ [1-4]$' "$save_file" \
      || { echo "第一趟打通了副本,存档里应有通关记录行(dclear 副本编号 值):$save_file" >&2; exit 1; }
    saved_fields="$(grep -E 'DNF_SAVE saved' "$save_log1" | tail -1 | grep -o -E 'level=[0-9]+ exp=[0-9]+ gold=[0-9]+ bag=[0-9]+ equipped=[0-9]+' || true)"
    case "$saved_fields" in
      *' equipped=1') ;;
      *) echo "第一趟最后存下的应穿着 1 件装备(DNF_SAVE saved … equipped=1),实际:$saved_fields" >&2; exit 1 ;;
    esac
    set +e
    "$app" -batchmode -logFile "$save_log2" -dnfdir "$DNF_DIR" -dnfsave "$save_file" -dnfflow -dnfflowfrom town -dnftimescale 4 -dnfquitafter 6
    rc=$?
    set -e
    echo "播放器退出码(存档第二趟): $rc"
    grep -E 'DNF_SAVE' "$save_log2" | cut -c1-260 || true
    [ "$rc" -eq 0 ] || exit "$rc"
    if grep -q -E 'Exception|DNF_TOWN_DEMO failed' "$save_log2"; then
      echo "存档第二趟的日志里有异常:$save_log2" >&2
      exit 1
    fi
    loaded_fields="$(grep -E 'DNF_SAVE loaded' "$save_log2" | tail -1 | grep -o -E 'level=[0-9]+ exp=[0-9]+ gold=[0-9]+ bag=[0-9]+ equipped=[0-9]+' || true)"
    [ -n "$loaded_fields" ] && [ "$loaded_fields" = "$saved_fields" ] \
      || { echo "第二趟读到的应与第一趟最后存下的一样:存下 \"$saved_fields\"、读到 \"$loaded_fields\":$save_log2" >&2; exit 1; }
    grep -q -E 'DNF_SAVE loaded .* dropped=0' "$save_log2" || { echo "读存档时不应丢东西(dropped=0):$save_log2" >&2; exit 1; }
    grep -q -F 'DNF_SAVE look matches=True weapon=True' "$save_log2" \
      || { echo "读了存档进城镇,主角应已换上穿着的武器(DNF_SAVE look matches=True weapon=True):$save_log2" >&2; exit 1; }
    if grep -q -E 'DNF_SAVE saved' "$save_log2"; then
      echo "读完存档什么都没变,不应再写(DNF_SAVE saved):$save_log2" >&2
      exit 1
    fi
  else
    echo "没有设置 DNF_DIR:跳过离屏渲染校验"
  fi
fi
