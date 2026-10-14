#!/usr/bin/env bash
# 在 Unity(批处理模式)里跑 EditMode 测试:验证 UPM 包能被 Unity 编译,并在 Unity 的运行时上行为一致。
# 用法: tools/unity-test.sh [EditMode|PlayMode]
# 环境变量:
#   UNITY_EDITOR                 Unity 可执行文件(默认 Hub 的 6000.3.26f1 路径)
#   DNF_DIR                      数据目录(可由 local.env 提供);缺省时真实数据测试被跳过
#   DNF_EXPECT_PVF_AGGREGATE     PVF 聚合哈希的期望值(默认用本机已验证的值)
#   UNITY_GRAPHICS=1             不加 -nographics,让需要图形设备的测试(GPU 与 CPU 合成器的对照)真正运行;
#                                需要本机有可用的 GPU 与图形会话,默认关闭
#   UNITY_TEST_TARGET            编辑器的活动构建目标(默认 StandaloneOSX)。不指定时 Unity 会沿用上一次批处理构建留下的目标
#                                (比如刚构建过 Android),编辑器就按移动端精度编译着色器,GPU 对拍的结果会跟着变(证据 E-031)
# 结果:out/unity-<平台>-results.xml;日志 re/unity-<平台>.log(都被 .gitignore 排除)。
set -euo pipefail
cd "$(dirname "$0")/.."
. tools/env.sh

platform="${1:-EditMode}"
UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity}"
if [ ! -x "$UNITY" ]; then
  echo "找不到 Unity 编辑器: $UNITY(可用环境变量 UNITY_EDITOR 指定)" >&2
  exit 2
fi
export DNF_EXPECT_PVF_AGGREGATE="${DNF_EXPECT_PVF_AGGREGATE:-cb377e11645e024d9a3d775fdd613880bd520409382dbe961163878b65511154}"

mkdir -p out re
results="$PWD/out/unity-${platform}-results.xml"
log="$PWD/re/unity-${platform}.log"
rm -f "$results"

graphics_flag="-nographics"
if [ "${UNITY_GRAPHICS:-0}" = "1" ]; then
  graphics_flag=""
fi

set +e
"$UNITY" -batchmode $graphics_flag -projectPath "$PWD/unity/DnfUnity" \
  -buildTarget "${UNITY_TEST_TARGET:-StandaloneOSX}" \
  -runTests -testPlatform "$platform" -testResults "$results" -logFile "$log"
code=$?
set -e

if [ ! -f "$results" ]; then
  echo "Unity 没有产出测试结果(退出码 $code),看日志 $log" >&2
  grep -n 'error CS' "$log" | head -20 >&2 || true
  exit 1
fi

python3 - "$results" <<'EOF'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
keys = ("total", "passed", "failed", "skipped", "inconclusive")
print("Unity 测试:" + "  ".join("%s=%s" % (k, root.attrib.get(k, "?")) for k in keys))
bad = 0
for tc in root.iter("test-case"):
    if tc.attrib.get("result") not in ("Passed",):
        bad += 1
        print("  %-12s %s" % (tc.attrib.get("result"), tc.attrib.get("fullname")))
sys.exit(1 if int(root.attrib.get("failed", "0")) > 0 else 0)
EOF
