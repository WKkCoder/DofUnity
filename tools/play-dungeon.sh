#!/usr/bin/env bash
# 试玩整座洛兰副本(证据 E-070):用 tools/unity-build-town.sh macos 构建好的播放器,运行时读取你自备的数据。
# 用法: tools/play-dungeon.sh            自己玩:方向键走(左或右双击=奔跑)、X 键普攻(奔跑中 X=奔跑攻击)、C 键跳跃(空中 X=跳跃攻击);
#                                       A 鬼斩、S 上挑、D 后跳(各有冷却,屏幕上有冷却文字;E-076)。
#                                       怪物清完出口打开,顶着出口的门洞走进去换房间;Esc 退出。Z 不绑定;空格是暂停(原版里它们是技能槽,绑什么没确认)
#       tools/play-dungeon.sh explore    自动机逛遍所有房间再打首领(看一遍整条路线)
#       tools/play-dungeon.sh bot        自动机直奔首领
# 伤害是占位值,命中停顿按客户端代码做了(E-077),没有耗蓝,怪物不会伤害你,没有结算界面。数据目录取 DNF_DIR(local.env 或环境变量)。
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
case "${1:-}" in
  "") ;;
  bot) extra=(-dnfbot) ;;
  explore) extra=(-dnfexplore) ;;
  *) echo "用法: $0 [bot|explore]" >&2; exit 2 ;;
esac

open -n "$app" --args -dnfdir "$DNF_DIR" -dnfdungeon dungeon/act1/lorien.dgn ${extra[@]+"${extra[@]}"}
