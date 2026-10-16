#!/usr/bin/env bash
# 以独立应用身份运行 DnfBaselineTool 的一个子命令,并把输出打印回来。
# 用法: tools/baseline/tool.sh status --prompt
#       tools/baseline/tool.sh windows
#       tools/baseline/tool.sh shot --owner dnf --out re/baseline/shots/a.png
#       tools/baseline/tool.sh key --owner dnf --keys 'example<tab>'
# 退出码:子命令成功(输出最后一行 RC=0)返回 0,否则非 0。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="$ROOT/re/baseline/DnfBaselineTool.app"
if [[ ! -d "$APP" ]]; then
  echo "还没构建:先运行 tools/baseline/build-tool.sh" >&2
  exit 2
fi

OUT="$(mktemp "$ROOT/re/baseline/.out.XXXXXX")"
ERR="$(mktemp "$ROOT/re/baseline/.err.XXXXXX")"
trap 'rm -f "$OUT" "$ERR"' EXIT

# --out / --shot / --out-dir / --index 的相对路径要变成绝对路径,因为应用经 open 启动时工作目录不是当前目录
args=()
expect_path=0
for a in "$@"; do
  if [[ $expect_path -eq 1 ]]; then
    case "$a" in /*) args+=("$a") ;; *) args+=("$PWD/$a") ;; esac
    expect_path=0
  else
    args+=("$a")
    case "$a" in --out|--shot|--out-dir|--index) expect_path=1 ;; esac
  fi
done

open -n -a "$APP" --stdout "$OUT" --stderr "$ERR" --args "${args[@]}" 2>/dev/null

# open -W 对 LSUIElement 应用不总是可靠,改为轮询输出里的 RC= 行(最多约 60 秒)
for _ in $(seq 1 600); do
  grep -q '^RC=' "$OUT" 2>/dev/null && break
  sleep 0.1
done
cat "$OUT"
[[ -s "$ERR" ]] && cat "$ERR" >&2
grep -q '^RC=0$' "$OUT"
