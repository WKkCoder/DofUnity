#!/bin/sh
# 脱敏后把本地的全部分支与标签发布到 GitHub(公开仓库)。不要直接 git push。
# 实际逻辑在 tools/guard/publish.py(审计、脱敏、校验、对照远端、原子推送);用法与原理见 --help 或 docs/usage.md。
#   tools/publish-github.sh --dry-run    先演练:审计 + 脱敏 + 全部校验,打印发布计划,不推送
#   tools/publish-github.sh --ssh        走 SSH 发布(需先把公钥登记到 GitHub)
exec python3 "$(dirname "$0")/guard/publish.py" "$@"
