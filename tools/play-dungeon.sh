#!/usr/bin/env bash
# 试玩整座洛兰副本(证据 E-070):用 tools/unity-build-town.sh macos 构建好的播放器,运行时读取你自备的数据。
# 用法: tools/play-dungeon.sh            自己玩:方向键走(左或右双击=奔跑)、X 键普攻(奔跑中 X=奔跑攻击)、C 键跳跃(空中 X=跳跃攻击);
#                                       A 鬼斩、S 上挑、D 后跳(各有冷却,屏幕上有冷却文字;E-076)。
#                                       怪物清完出口打开,顶着出口的门洞走进去换房间;Esc 退出。Z 不绑定;空格是暂停(原版里它们是技能槽,绑什么没确认)
#       tools/play-dungeon.sh explore    自动机逛遍所有房间再打首领(看一遍整条路线)
#       tools/play-dungeon.sh bot        自动机直奔首领
#       tools/play-dungeon.sh nodamage   (可以和上面的模式连着写,例如 explore nodamage)怪物打不到你、没有血条:想只看打怪的时候用
#       tools/play-dungeon.sh nosound    (同样可以连着写)不要声音。默认是有声音的(E-089、E-090):挥剑与命中、角色语音、地图音乐与环境声,数据取自你自备的 audio.xml、SoundPacks、Music
#       tools/play-dungeon.sh flow       整条流程(E-093):从城镇出发,回车打开副本选择(上下选、回车或空格确认、Esc 返回城镇),进副本,首领清空后在结算菜单里
#                                       选再次挑战(1)/选择其它地下城(2)/返回城镇(3),也可以点菜单行,循环往复(可以连着写 bot、explore、nodamage、nosound;bot 会按 retry,select,town 自动走一遍)
# 伤害是占位值,命中停顿按客户端代码做了(E-077);怪物会还手(E-086,左下角有生命条与魔法条,主角死了只有 DEAD 字样,没有复活),
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
    flow) extra+=(-dnfflow) ;;
    *) echo "用法: $0 [flow] [bot|explore] [nodamage] [nosound]" >&2; exit 2 ;;
  esac
done

open -n "$app" --args -dnfdir "$DNF_DIR" -dnfdungeon dungeon/act1/lorien.dgn ${extra[@]+"${extra[@]}"}
