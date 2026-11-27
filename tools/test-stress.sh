#!/usr/bin/env bash
# 在 CPU 并发压力下重复运行指定 .NET 测试工程，定位偶发失败（r20-netdeflake）。
# 用法: tools/test-stress.sh --project <csproj> [选项]
#       tools/test-stress.sh --selftest
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIR/$(basename "$0")"
. "$SCRIPT_DIR/env.sh"

usage() {
  cat <<'EOF'
用法: tools/test-stress.sh --project <csproj> [选项]
      tools/test-stress.sh --selftest

选项:
  --project <csproj>      测试工程路径（必填，除非使用 --selftest）
  --filter <expr>         传给 dotnet test --filter 的过滤表达式
  --rounds <R>            压测轮数（默认: 10，正整数）
  --parallel <P>          每轮并发 dotnet test 进程数（默认: 4，正整数）
  --timeout <sec>         单进程超时秒数（默认: 300，正整数）
  --exclude-list <file>   排除列表文件（每行一个测试名/片段或过滤表达式，# 开头为注释）
  --configuration <cfg>   构建配置（默认: Release）
  --selftest              运行自检（覆盖全过/必挂/偶发/超时/排除列表/非法参数）
  -h, --help              显示此帮助
EOF
}

die_usage() {
  echo "参数错误: $1" >&2
  usage >&2
  exit 64
}

is_pos_int() {
  case "${1:-}" in
    ''|*[!0-9]*) return 1 ;;
    *) [ "$1" -ge 1 ] 2>/dev/null ;;
  esac
}

PROJECT=""
FILTER=""
ROUNDS=10
PARALLEL=4
TIMEOUT=300
EXCLUDE_LIST=""
CONFIGURATION="Release"
SELFTEST=0

if [ "$#" -eq 0 ]; then
  die_usage "缺少参数"
fi

while [ "$#" -gt 0 ]; do
  case "$1" in
    --project)
      [ "$#" -ge 2 ] || die_usage "--project 缺少参数值"
      PROJECT="$2"
      shift 2
      ;;
    --filter)
      [ "$#" -ge 2 ] || die_usage "--filter 缺少参数值"
      FILTER="$2"
      shift 2
      ;;
    --rounds)
      [ "$#" -ge 2 ] || die_usage "--rounds 缺少参数值"
      is_pos_int "$2" || die_usage "--rounds 必须为正整数: $2"
      ROUNDS="$2"
      shift 2
      ;;
    --parallel)
      [ "$#" -ge 2 ] || die_usage "--parallel 缺少参数值"
      is_pos_int "$2" || die_usage "--parallel 必须为正整数: $2"
      PARALLEL="$2"
      shift 2
      ;;
    --timeout)
      [ "$#" -ge 2 ] || die_usage "--timeout 缺少参数值"
      is_pos_int "$2" || die_usage "--timeout 必须为正整数: $2"
      TIMEOUT="$2"
      shift 2
      ;;
    --exclude-list)
      [ "$#" -ge 2 ] || die_usage "--exclude-list 缺少参数值"
      EXCLUDE_LIST="$2"
      shift 2
      ;;
    --configuration)
      [ "$#" -ge 2 ] || die_usage "--configuration 缺少参数值"
      CONFIGURATION="$2"
      shift 2
      ;;
    --selftest)
      SELFTEST=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      die_usage "未知选项: $1"
      ;;
  esac
done

run_selftest() {
  local st_dir
  st_dir="$(mktemp -d "${TMPDIR:-/tmp}/test-stress-selftest.XXXXXX")"
  trap 'rm -rf "$st_dir"' EXIT

  echo "== selftest: invalid usage (expect exit 64) =="
  local rc=0
  local bad_cases=0
  for case_args in \
    "" \
    "--unknown-flag" \
    "--project" \
    "--project $st_dir/missing.csproj" \
    "--project $st_dir/pass/Pass.csproj --rounds 0" \
    "--project $st_dir/pass/Pass.csproj --parallel abc" \
    "--project $st_dir/pass/Pass.csproj --timeout 0" \
    "--project $st_dir/pass/Pass.csproj --exclude-list $st_dir/missing.txt"
  do
    set +e
    if [ -z "$case_args" ]; then
      "$SCRIPT_PATH" >/dev/null 2>&1
    else
      # shellcheck disable=SC2086
      "$SCRIPT_PATH" $case_args >/dev/null 2>&1
    fi
    rc=$?
    set -e
    if [ "$rc" -ne 64 ]; then
      echo "SELFTEST FAIL: expected exit 64 for [$case_args], got $rc" >&2
      return 1
    fi
    bad_cases=$((bad_cases + 1))
  done
  echo "selftest: $bad_cases invalid usage cases returned 64"

  create_mini_proj() {
    local dir="$1"
    local name="$2"
    local cs_body="$3"
    mkdir -p "$dir"
    cat >"$dir/$name.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
EOF
    printf '%s\n' "$cs_body" >"$dir/Tests.cs"
  }

  create_mini_proj "$st_dir/pass" "PassProj" '
namespace Selftest;
public class PassTests
{
    [Fact]
    public void AlwaysPass() => Assert.True(true);
}
'

  create_mini_proj "$st_dir/fail" "FailProj" '
namespace Selftest;
public class FailTests
{
    [Fact]
    public void AlwaysPassInFailProj() => Assert.True(true);

    [Fact]
    public void AlwaysFail() => Assert.True(false);
}
'

  create_mini_proj "$st_dir/intermittent" "IntermittentProj" '
namespace Selftest;
public class IntermittentTests
{
    [Fact]
    public void IntermittentFail()
    {
        string marker = System.Environment.GetEnvironmentVariable("STRESS_SELFTEST_MARKER")!;
        try
        {
            using var _ = System.IO.File.Open(marker, System.IO.FileMode.CreateNew);
            Assert.Fail("simulated first-run failure");
        }
        catch (System.IO.IOException)
        {
        }
    }
}
'

  create_mini_proj "$st_dir/timeout" "TimeoutProj" '
namespace Selftest;
public class TimeoutTests
{
    [Fact]
    public void SlowTest() => System.Threading.Thread.Sleep(10000);
}
'

  echo "== selftest: always-pass (expect exit 0) =="
  local out_pass
  out_pass="$("$SCRIPT_PATH" --project "$st_dir/pass/PassProj.csproj" --rounds 2 --parallel 2)"
  printf '%s\n' "$out_pass"
  printf '%s\n' "$out_pass" | grep -q "passed=4 failed=0" || { echo "SELFTEST FAIL: pass summary mismatch" >&2; return 1; }

  echo "== selftest: always-fail (expect exit 1 + exact test name & count 4) =="
  local out_fail=""
  set +e
  out_fail="$("$SCRIPT_PATH" --project "$st_dir/fail/FailProj.csproj" --rounds 2 --parallel 2 2>&1)"
  rc=$?
  set -e
  printf '%s\n' "$out_fail"
  [ "$rc" -eq 1 ] || { echo "SELFTEST FAIL: always-fail expected exit 1, got $rc" >&2; return 1; }
  printf '%s\n' "$out_fail" | grep -q $'^Selftest\\.FailTests\\.AlwaysFail\t4$' || {
    echo "SELFTEST FAIL: always-fail missing exact test name & count 4" >&2
    return 1
  }

  echo "== selftest: exclude-list (expect exit 0) =="
  cat >"$st_dir/exclude.txt" <<'EOF'
# comment line
Selftest.FailTests.AlwaysFail
EOF
  local out_excl
  out_excl="$("$SCRIPT_PATH" --project "$st_dir/fail/FailProj.csproj" --exclude-list "$st_dir/exclude.txt" --rounds 2 --parallel 2)"
  printf '%s\n' "$out_excl"
  printf '%s\n' "$out_excl" | grep -q "passed=4 failed=0" || { echo "SELFTEST FAIL: exclude-list summary mismatch" >&2; return 1; }

  echo "== selftest: intermittent-fail (expect exit 1 + exact test name & count 1) =="
  local out_inter=""
  set +e
  out_inter="$(STRESS_SELFTEST_MARKER="$st_dir/intermittent.marker" "$SCRIPT_PATH" --project "$st_dir/intermittent/IntermittentProj.csproj" --rounds 2 --parallel 2 2>&1)"
  rc=$?
  set -e
  printf '%s\n' "$out_inter"
  [ "$rc" -eq 1 ] || { echo "SELFTEST FAIL: intermittent-fail expected exit 1, got $rc" >&2; return 1; }
  printf '%s\n' "$out_inter" | grep -q $'^Selftest\\.IntermittentTests\\.IntermittentFail\t1$' || {
    echo "SELFTEST FAIL: intermittent-fail missing exact test name & count 1" >&2
    return 1
  }

  echo "== selftest: timeout kill (expect exit 1 + <TIMEOUT> count 1) =="
  local out_to=""
  set +e
  out_to="$("$SCRIPT_PATH" --project "$st_dir/timeout/TimeoutProj.csproj" --rounds 1 --parallel 1 --timeout 2 2>&1)"
  rc=$?
  set -e
  printf '%s\n' "$out_to"
  [ "$rc" -eq 1 ] || { echo "SELFTEST FAIL: timeout expected exit 1, got $rc" >&2; return 1; }
  printf '%s\n' "$out_to" | grep -q $'^<TIMEOUT>\t1$' || {
    echo "SELFTEST FAIL: timeout missing <TIMEOUT> count 1" >&2
    return 1
  }

  rm -rf "$st_dir"
  trap - EXIT
  [ ! -d "$st_dir" ] || { echo "SELFTEST FAIL: temp dir not cleaned" >&2; return 1; }
  echo "== selftest: ALL PASSED =="
}

if [ "$SELFTEST" -eq 1 ]; then
  run_selftest
  exit 0
fi

[ -n "$PROJECT" ] || die_usage "必须指定 --project <csproj>"
[ -f "$PROJECT" ] || die_usage "测试工程不存在"
if [ -n "$EXCLUDE_LIST" ] && [ ! -f "$EXCLUDE_LIST" ]; then
  die_usage "排除列表文件不存在"
fi

BUILD_LOG="$(mktemp "${TMPDIR:-/tmp}/test-stress-build.XXXXXX")"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/test-stress-run.XXXXXX")"
trap 'rm -rf "$WORK_DIR" "$BUILD_LOG"' EXIT

if ! dotnet build "$PROJECT" -c "$CONFIGURATION" -p:NuGetAudit=false --nologo -v q >"$BUILD_LOG" 2>&1; then
  echo "构建失败（配置: ${CONFIGURATION}），以下为过滤路径后的构建错误：" >&2
  sed -E 's#/[^ ]*/#.../#g' "$BUILD_LOG" >&2
  exit 2
fi

python3 - "$PROJECT" "$FILTER" "$ROUNDS" "$PARALLEL" "$TIMEOUT" "$EXCLUDE_LIST" "$CONFIGURATION" "$WORK_DIR" <<'PYEOF'
import os
import signal
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter

project, user_filter, rounds_s, parallel_s, timeout_s, exclude_file, config, work_dir = sys.argv[1:9]
rounds = int(rounds_s)
parallel = int(parallel_s)
timeout_sec = int(timeout_s)

filter_parts = []
if user_filter.strip():
    filter_parts.append(f"({user_filter.strip()})")

if exclude_file:
    with open(exclude_file, "r", encoding="utf-8") as fh:
        for raw in fh:
            line = raw.split("#", 1)[0].strip()
            if not line:
                continue
            if any(op in line for op in ("!=", "!~", "=", "~", "&", "|")):
                filter_parts.append(f"({line})")
            else:
                filter_parts.append(f"(FullyQualifiedName!~{line})")

effective_filter = "&".join(filter_parts)

failure_counts = Counter()
total_runs = rounds * parallel
passed_runs = 0
failed_runs = 0


def kill_proc_group(proc):
    try:
        os.killpg(proc.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        proc.wait(timeout=2)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        try:
            proc.wait(timeout=2)
        except subprocess.TimeoutExpired:
            pass


def parse_trx_failures(trx_path):
    names = []
    if not os.path.isfile(trx_path):
        return names
    try:
        tree = ET.parse(trx_path)
    except ET.ParseError:
        return names
    root = tree.getroot()
    for elem in root.iter():
        if elem.tag.endswith("UnitTestResult"):
            if elem.attrib.get("outcome") == "Failed":
                test_name = elem.attrib.get("testName", "<UNKNOWN_TEST>")
                names.append(test_name)
    return names


for r in range(1, rounds + 1):
    round_dir = os.path.join(work_dir, f"r{r}")
    os.makedirs(round_dir, exist_ok=True)
    procs = []
    for p in range(1, parallel + 1):
        wdir = os.path.join(round_dir, f"w{p}")
        os.makedirs(wdir, exist_ok=True)
        trx_name = "run.trx"
        cmd = [
            "dotnet", "test", project,
            "-c", config,
            "--no-build", "--no-restore",
            "-p:NuGetAudit=false",
            "--nologo",
            "--logger", f"trx;LogFileName={trx_name}",
            "--results-directory", wdir,
        ]
        if effective_filter:
            cmd.extend(["--filter", effective_filter])
        log_path = os.path.join(wdir, "worker.log")
        log_fh = open(log_path, "wb")
        proc = subprocess.Popen(
            cmd,
            stdout=log_fh,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        procs.append((p, proc, wdir, os.path.join(wdir, trx_name), log_fh))

    round_passed = 0
    round_failed = 0
    import time
    deadline = time.monotonic() + timeout_sec

    for p, proc, wdir, trx_path, log_fh in procs:
        remaining = deadline - time.monotonic()
        timed_out = False
        rc = None
        try:
            rc = proc.wait(timeout=max(0.05, remaining))
        except subprocess.TimeoutExpired:
            timed_out = True
            kill_proc_group(proc)
        finally:
            log_fh.close()

        if timed_out:
            round_failed += 1
            failure_counts["<TIMEOUT>"] += 1
            continue

        failed_tests = parse_trx_failures(trx_path)
        if rc == 0 and not failed_tests:
            round_passed += 1
        else:
            round_failed += 1
            if failed_tests:
                for name in failed_tests:
                    failure_counts[name] += 1
            else:
                failure_counts[f"<PROCESS_EXIT_{rc}>"] += 1

    passed_runs += round_passed
    failed_runs += round_failed
    status = "PASS" if round_failed == 0 else "FAIL"
    print(f"round {r}/{rounds}: {status} ({round_passed}/{parallel} passed, {round_failed} failed)", flush=True)

print("== summary ==", flush=True)
print(f"rounds={rounds} parallel={parallel} total_runs={total_runs} passed={passed_runs} failed={failed_runs}", flush=True)
print("== failures ==", flush=True)
if not failure_counts:
    print("(none)", flush=True)
else:
    for name, count in sorted(failure_counts.items(), key=lambda kv: (-kv[1], kv[0])):
        print(f"{name}\t{count}", flush=True)

sys.exit(0 if failed_runs == 0 else 1)
PYEOF
