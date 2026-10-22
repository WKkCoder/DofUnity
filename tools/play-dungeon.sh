#!/usr/bin/env bash
# 试玩整座洛兰副本(证据 E-070):用 tools/unity-build-town.sh macos 构建好的播放器,运行时读取你自备的数据。
# 用法: tools/play-dungeon.sh            自己玩:方向键走(左或右双击=奔跑)、X 键普攻(奔跑中 X=奔跑攻击)、C 键跳跃(空中 X=跳跃攻击);
#                                       A 鬼斩、S 上挑、D 后跳(各有冷却,屏幕上有冷却文字;E-076)。
#                                       怪物清完出口打开,顶着出口的门洞走进去换房间;Esc 退出。Z 不绑定;空格是暂停(原版里它们是技能槽,绑什么没确认)
#       tools/play-dungeon.sh explore    自动机逛遍所有房间再打首领(看一遍整条路线)
#       tools/play-dungeon.sh bot        自动机直奔首领
#       tools/play-dungeon.sh nodamage   (可以和上面的模式连着写,例如 explore nodamage)怪物打不到你、没有血条:想只看打怪的时候用
#       tools/play-dungeon.sh nosound    (同样可以连着写)不要声音。默认是有声音的(E-089、E-090):挥剑与命中、角色语音、地图音乐与环境声,数据取自你自备的 audio.xml、SoundPacks、Music
#       tools/play-dungeon.sh level=30   (同样可以连着写)主角从 30 级开始(1 到 85,默认 1 级);打怪得经验、攒够升级(E-111),底栏有经验条;
#                                       怪物的基础经验表不在客户端数据里:没有 monster_reward_ref.csv(数据目录里,每行 等级,经验)时用占位表,不是原版数值
#       tools/play-dungeon.sh noexp      (同样可以连着写)打怪不给经验(没有经验条)
#       tools/play-dungeon.sh coins=N    (同样可以连着写)复活币起始 N 枚(0 到 99,默认 5,占位:没有背包);主角倒下后 10 秒内按 X 用一枚币原地复活(E-115),币用完或 10 秒走完副本失败
#       tools/play-dungeon.sh monsterdamage=N   (同样可以连着写)怪物伤害乘上 N(测试旋钮,不是原版规则):想快点看到倒下与复活时用,例如 coins=1 monsterdamage=50
#       tools/play-dungeon.sh flow       整条流程(E-093、E-096):从选角界面出发(方向键选、回车或空格开始游戏、Esc 或点"结束游戏"退出,也可以点格子与按钮),登录加载后进城镇,
#                                       走进地下城入口区域的世界地图出口打开副本选择(上下选、回车或空格确认、Esc 返回城镇),进副本,首领清空后在结算菜单里
#                                       选再次挑战(1)/选择其它地下城(2)/返回城镇(3),也可以点菜单行,循环往复(可以连着写 bot、explore、nodamage、nosound;bot 会按 retry,select,town 自动走一遍)
#       tools/play-dungeon.sh flowtown   同 flow,但跳过选角界面,直接从城镇出发(M9d 之前的起点)
#                                       flow 模式下 Esc 打开系统菜单(游戏暂停;上下键选、回车或数字键确定:继续 / 放弃回城或回选角 / 退出游戏;版式是占位设计)
# 伤害按客户端的公式算(E-110;没有面板数据时用占位值),命中停顿按客户端代码做了(E-077);怪物会还手(E-086,左下角有生命条与魔法条,主角死了躺平、屏幕上方出 10 秒复活倒计时,E-115),
# 放技能耗蓝(E-087)、有原版底栏(E-088)、首领清空后有结算场景与菜单(E-091;非 flow 模式下选了菜单只记日志)。数据目录取 DNF_DIR(local.env 或环境变量)。
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

app="unity/DnfUnity/Builds/macos/DnfTownDemo.app"
[ -d "$app" ] || { echo "还没有构建播放器:先运行 tools/unity-build-town.sh macos" >&2; exit 2; }
if [ -z "${DNF_DIR:-}" ] || [ ! -f "$DNF_DIR/Script.pvf" ]; then
  echo "没有找到数据目录:请在 local.env 里设置 DNF_DIR(含 Script.pvf 与 ImagePacks2 的目录)" >&2
  exit 2
fi

extra=()
for arg in "$@"; do
  case "$arg" in
    bot) extra+=(-dnfbot) ;;
    explore) extra+=(-dnfexplore) ;;
    nodamage) extra+=(-dnfnodamage) ;;
    nosound) extra+=(-dnfnosound) ;;
    noexp) extra+=(-dnfnoexp) ;;
    level=*) extra+=(-dnflevel "${arg#level=}") ;;
    coins=*) extra+=(-dnfcoins "${arg#coins=}") ;;
    monsterdamage=*) extra+=(-dnfmonsterdamage "${arg#monsterdamage=}") ;;
    flow) extra+=(-dnfflow -dnfflowfrom char) ;;
    flowtown) extra+=(-dnfflow -dnfflowfrom town) ;;
    *) echo "用法: $0 [flow|flowtown] [bot|explore] [nodamage] [nosound] [noexp] [level=N] [coins=N] [monsterdamage=N]" >&2; exit 2 ;;
  esac
done

open -n "$app" --args -dnfdir "$DNF_DIR" -dnfdungeon dungeon/act1/lorien.dgn ${extra[@]+"${extra[@]}"}
