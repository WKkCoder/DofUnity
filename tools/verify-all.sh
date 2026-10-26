#!/usr/bin/env bash
# 重跑全部对拍与全库读通检查(C# 实现 vs 独立参照),汇总到 out/verify-all.json(证据 E-098)。
# 需要:本机有 DNF 数据(local.env 里的 DNF_DIR)、用户级 .NET SDK(tools/env.sh)、python3;
# 其中 oracle 与 difficulty 的对拍部分还需要 re/venv(Unicorn)与本机的服务端二进制,缺了会记为失败。
# 参数原样传给 tools/py/verify_all.py(--only / --skip)。约 1 分钟。
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

echo "== 构建 =="
dotnet build DnfUnity.slnx -c Release -v q --nologo

echo "== 对拍与全库读通 =="
python3 tools/py/verify_all.py "$@"
