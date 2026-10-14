#!/usr/bin/env bash
# 在 CrossOver 瓶子 "DNF" 里启动原版启动器 dnf-launcher.exe,用于录制对照基线。
#
# 与 /Applications/DNF-CX.app 的区别:
#   - 不改 system.reg(不做任何试用期/许可相关处理),
#   - 不启动 DNFBossKey / DNFScale 辅助应用(基线需要原生窗口尺寸),
#   - 日志写到 re/baseline/runs/<时间戳>/(已 gitignore),不写用户的 ~/Library/Logs/DNF。
# 环境变量沿用 DNF-CX:插件注入关闭(DNF_PLUGIN_ENABLED=0),服务器 IP 走 ijl15.dll 的 gethostbyname 重定向。
# 用法: tools/baseline/launch-dnf.sh        (前台运行,直到启动器退出;一般用后台方式调用)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck disable=SC1091
source "$ROOT/local.env"
: "${DNF_DIR:?local.env 里缺少 DNF_DIR}"

# 只取服务器 IP,不把账号密码导出到游戏进程的环境里
GAME_SERVER_IP="$(sed -n 's/^DNF_SERVER_IP=//p' "$ROOT/local.secrets.env" | tr -d "'\"")"
: "${GAME_SERVER_IP:?local.secrets.env 里缺少 DNF_SERVER_IP}"

CROSSOVER="/Applications/CrossOver.app/Contents/SharedSupport/CrossOver"
[[ -x "$CROSSOVER/bin/wine" ]] || { echo "未找到 CrossOver: $CROSSOVER" >&2; exit 2; }
pgrep -x wineserver >/dev/null 2>&1 && { echo "已有 wineserver 在运行,先确认没有别的 DNF 会话再继续" >&2; exit 3; }

RUN_DIR="${DNF_RUN_DIR:-$ROOT/re/baseline/runs/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$RUN_DIR"

export CX_BOTTLE="$HOME/Library/Application Support/CrossOver/Bottles/DNF"
export WINEDEBUG="${DNF_WINEDEBUG:--all}"
export WINEDLLOVERRIDES="ijl15=n,b"
export WGPU_BACKEND="vulkan"
export WINE_D3D_CONFIG="${WINE_D3D_CONFIG:-csmt=0}"
export GAME_SERVER_IP
export DNF_PLUGIN_ENABLED=0
export DNF_PLUGIN_PATH="plugins"
export LANG="zh_CN.UTF-8"
export LC_ALL="zh_CN.UTF-8"

{
  echo "start=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "game_dir=$DNF_DIR"
  echo "bottle=$CX_BOTTLE"
  echo "WINEDEBUG=$WINEDEBUG WINEDLLOVERRIDES=$WINEDLLOVERRIDES WGPU_BACKEND=$WGPU_BACKEND WINE_D3D_CONFIG=$WINE_D3D_CONFIG"
  echo "DNF_PLUGIN_ENABLED=$DNF_PLUGIN_ENABLED DNF_PLUGIN_PATH=$DNF_PLUGIN_PATH"
  echo "launcher=dnf-launcher.exe sha256=$(shasum -a 256 "$DNF_DIR/dnf-launcher.exe" | cut -d' ' -f1)"
  echo "DNF.exe sha256=$(shasum -a 256 "$DNF_DIR/DNF.exe" | cut -d' ' -f1)"
} > "$RUN_DIR/run-info.txt"

echo "$RUN_DIR"
cd "$DNF_DIR"
exec "$CROSSOVER/bin/wine" dnf-launcher.exe >>"$RUN_DIR/wine.log" 2>&1
