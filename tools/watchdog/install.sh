#!/bin/sh
# 安装看门狗:把代码复制到用户目录,并注册 launchd 定时任务(默认每 20 分钟一轮)。
# 它定期清理本工程开发时 agent 留下的孤儿进程、僵尸进程和垃圾资源;说明见 docs/usage.md 的「看门狗」一节。
#   sh tools/watchdog/install.sh                安装或升级,立即生效
#   sh tools/watchdog/install.sh --interval 10  改成每 10 分钟一轮
#   sh tools/watchdog/install.sh --no-load      只复制文件、生成 plist,不注册到 launchd
# 查看状态:python3 tools/watchdog/watchdog.py status
# 卸载:    python3 tools/watchdog/watchdog.py uninstall [--purge]
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
# 优先用系统自带的 python3(不会因为 brew 升级而失效);定时任务实际用哪个解释器由 watchdog.py 自己挑
PY=/usr/bin/python3
[ -x "$PY" ] || PY=python3
exec "$PY" "$HERE/watchdog.py" install "$@"
