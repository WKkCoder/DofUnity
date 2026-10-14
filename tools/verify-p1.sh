#!/usr/bin/env bash
# P1 验收:单元测试 + 全库对拍(C# 数据层 vs Python 参考实现)。
# 需要:本机有 DNF 数据(local.env 里的 DNF_DIR)、用户级 .NET SDK(tools/env.sh)、python3。
# 参考输出缺失时会先用 tools/py 重新生成(约 1 分钟)。额外参数原样传给 `dnf verify all`。
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

need=0
for f in pvf_manifest.tsv.gz img_scan.json tag_catalog.json ani_verify.json oracle_pixels.json; do
  [ -f "out/$f" ] || need=1
done
if [ "$need" = 1 ]; then
  echo "== 生成 Python 参考输出 =="
  python3 tools/py/manifest.py
  python3 tools/py/scan_imgs.py
  python3 tools/py/tag_catalog.py
  python3 tools/py/ani_spec.py
  python3 tools/py/pixel_oracle.py
fi

echo "== 单元测试(合成数据)=="
dotnet test src/Dnf.Data.Tests/Dnf.Data.Tests.csproj -c Release

echo "== 全库对拍(用户自备数据)=="
dotnet build src/Dnf.Cli/Dnf.Cli.csproj -c Release -v q
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll verify all "$@"
