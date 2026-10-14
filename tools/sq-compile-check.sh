#!/usr/bin/env bash
# 用上游 Squirrel 3.2 的命令行 `sq -c` 逐个编译(只检查语法,不运行)PVF 里全部 .nut,作为 `dnf sq-check` 里 A 段的独立复核。
# 用法: tools/sq-compile-check.sh
# 需要 DNF_DIR(或 local.env)。脚本被转储到 re/notes/nut(被 .gitignore 排除,不入库);上游源码检出到 re/third_party/squirrel-3.2。
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

SQ_DIR="re/third_party/squirrel-3.2"
if [ ! -x "$SQ_DIR/bin/sq" ]; then
  mkdir -p re/third_party
  if [ ! -d "$SQ_DIR" ]; then
    git clone --depth 1 --branch v3.2 https://github.com/albertodemichelis/squirrel.git "$SQ_DIR"
  fi
  make -C "$SQ_DIR" -j4 >/dev/null
fi

DUMP="re/notes/nut"
rm -rf "$DUMP"
python3 tools/py/sq_inventory.py --dump "$DUMP" --top 0 >/dev/null

total=0
failed=0
: > re/notes/nut_compile_fail.txt
while IFS= read -r f; do
  total=$((total + 1))
  if ! "$SQ_DIR/bin/sq" -c -o /dev/null "$f" >/dev/null 2>&1; then
    failed=$((failed + 1))
    echo "$f" >> re/notes/nut_compile_fail.txt
  fi
done < <(find "$DUMP" -name '*.nut' | sort)
echo "上游 sq -c:共 $total 个 .nut,编译失败 $failed 个(失败清单 re/notes/nut_compile_fail.txt)"
[ "$failed" -eq 0 ]
