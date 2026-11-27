#!/usr/bin/env bash
# r20-e2e 联网全流程 macOS 无人值守验收脚本：
#   默认 --dry：启动本地合成回环假服务端 (src/Dnf.NetFake) 并驱动 macos-dev 播放器走完
#   登录 → 选服务器 → 选频道 → 选角 → 进城 → 选副本 → 进图 → 自动机清怪 → 结算 → 再来一次 → 回城，
#   严格校验 DNF_ONLINE 阶段序列、零 fail、结局 ok 与耗时上限；全程不连任何外部网络，不回显凭据/地址/端口。
#   --live：必须显式提供 --prereg <非空预注册文件> 且 --mincleartime >= 60，单次登录失败即停、绝不自动重试。
#   --check-guards：与 --live 同用，只判定上面的 --live 护栏就退出——通过打印固定一行并退出 0，不通过退出 2 并打印原因行；
#     之后的任何步骤（env.sh、DNF_DIR、构建、播放器、本地凭据文件）一概不碰。
#   --selftest：对日志校验器（含正例与多种负对照）及 --live 护栏做离线自测，不需要 Unity；
#     护栏只经 --check-guards 入口、在隔离沙箱副本上验证，自测本身绝不会走到真服运行。
#     末尾还自带一组"失败路径负对照"：由 --selftest 自己自动运行（不用另外手动跑），把本脚本复制到临时目录、故意改坏几处，
#     在 LC_ALL=C 与 LC_ALL=en_US.UTF-8 下各跑一遍，要求改坏的自测退出 1 并打出 FAIL 行（防"失败被悄悄吞掉、退出 0"这类缺口，
#     见下面 st_negative_control 的说明）；本机没有 en_US.UTF-8 时大声提示并跳过 UTF-8 那一半。
set -euo pipefail

# 先记下脚本自身的绝对路径（下一行就 cd 到仓库根；--selftest 要拿它复制出隔离沙箱）
SELF_PATH="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

MODE="dry"
GUARD_ONLY="0"
PREREG_FILE=""
MIN_CLEAR_SECONDS=""
SETTLE_SPEC="retry,town"
CHAR_SLOT="0"
DUNGEON_ID=""
DUNGEON_MODE="0"
MAX_SECONDS="120"
NET_ROOT_LIVE=""
FAKE_ROOMS="1"
FAKE_MONSTERS="2"
EXTRA_NO_DAMAGE="1"

usage() {
  cat <<'EOF'
用法: tools/online-e2e-mac.sh [--dry | --live | --selftest] [选项]

模式:
  --dry                         默认。起本地回环假服务端 (dnf-netfake)，零外部网络与真凭据。
  --live                        真服验收（仅由集成者按预注册执行）：强制 --prereg 与 --mincleartime >= 60。
  --check-guards                须与 --live 同用：只判定 --live 护栏。通过打印 DNF_E2E_GUARDS ok 并退出 0；
                                不通过退出 2 并打印原因行。绝不走到 env.sh、DNF_DIR、构建、播放器与凭据文件。
  --selftest                    离线自测日志断言器（正例 + 负对照）与 --live 护栏（只经 --check-guards，在隔离沙箱副本上）。
                                末尾自动再跑一组失败路径负对照（由 --selftest 自己运行，无需另跑）：把本脚本复制到临时目录
                                故意改坏几处，在 LC_ALL=C 与 en_US.UTF-8 下各跑一遍，要求改坏的自测退出 1 并打出 FAIL 行。

选项:
  --prereg <文件>               --live 必填：非空预注册文件路径。
  --mincleartime <秒>           最短清图时间（--dry 默认 0；--live 必填且必须 >= 60）。
  --settle <序列>               结算菜单选择序列（默认 retry,town；支持 retry/select/town 逗号分隔）。
  --char <槽位>                 角色槽位序号（默认 0）。
  --dungeon <编号> <模式>       可选：指定副本编号与模式（0..4）。
  --max-seconds <秒>            整体墙钟与 elapsedMs 兜底上限（默认 120）。
  --netroot <目录>              --live 用的开发配置根（默认当前仓库根）。
  --rooms <1..8>                --dry 假服务端房间数（默认 1）。
  --monsters <1..16>            --dry 假服务端每房怪数（默认 2）。
  --damage                      关闭 -dnfnodamage（--dry 默认开启 -dnfnodamage）。
  -h, --help                    显示本帮助。

环境变量:
  DNF_SKIP_BUILD=1              若 macos-dev 播放器已存在则跳过 tools/unity-build-town.sh macos-dev 构建。
  DNF_DIR                       数据目录（含 Script.pvf）；未设时回退读取 local.env。
  E2E_SELFTEST_NESTED           内部使用：--selftest 的失败路径负对照给改坏的副本设置它来防止递归；手动运行不要设置。
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --dry) MODE="dry"; shift ;;
    --live) MODE="live"; shift ;;
    --check-guards) GUARD_ONLY="1"; shift ;;
    --selftest) MODE="selftest"; shift ;;
    --prereg) [ $# -ge 2 ] || { echo "缺少 --prereg 参数" >&2; exit 2; }; PREREG_FILE="$2"; shift 2 ;;
    --mincleartime) [ $# -ge 2 ] || { echo "缺少 --mincleartime 参数" >&2; exit 2; }; MIN_CLEAR_SECONDS="$2"; shift 2 ;;
    --settle) [ $# -ge 2 ] || { echo "缺少 --settle 参数" >&2; exit 2; }; SETTLE_SPEC="$2"; shift 2 ;;
    --char) [ $# -ge 2 ] || { echo "缺少 --char 参数" >&2; exit 2; }; CHAR_SLOT="$2"; shift 2 ;;
    --dungeon) [ $# -ge 3 ] || { echo "缺少 --dungeon <编号> <模式> 参数" >&2; exit 2; }; DUNGEON_ID="$2"; DUNGEON_MODE="$3"; shift 3 ;;
    --max-seconds) [ $# -ge 2 ] || { echo "缺少 --max-seconds 参数" >&2; exit 2; }; MAX_SECONDS="$2"; shift 2 ;;
    --netroot) [ $# -ge 2 ] || { echo "缺少 --netroot 参数" >&2; exit 2; }; NET_ROOT_LIVE="$2"; shift 2 ;;
    --rooms) [ $# -ge 2 ] || { echo "缺少 --rooms 参数" >&2; exit 2; }; FAKE_ROOMS="$2"; shift 2 ;;
    --monsters) [ $# -ge 2 ] || { echo "缺少 --monsters 参数" >&2; exit 2; }; FAKE_MONSTERS="$2"; shift 2 ;;
    --damage) EXTRA_NO_DAMAGE="0"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数: $1" >&2; usage >&2; exit 2 ;;
  esac
done

validate_log() {
  local log_file="$1"
  local max_ms="$2"
  local expect_retries="$3"
  local check_no_exception="${4:-1}"
  python3 - "$log_file" "$max_ms" "$expect_retries" "$check_no_exception" <<'PY'
import re
import sys

log_path = sys.argv[1]
max_ms = int(sys.argv[2])
expect_retries = int(sys.argv[3])
check_exc = sys.argv[4] == "1"

try:
    text = open(log_path, encoding="utf-8", errors="replace").read()
except OSError as exc:
    print(f"日志读取失败: {exc}", file=sys.stderr)
    sys.exit(1)

lines = [line.strip() for line in text.splitlines() if line.strip()]
if check_exc:
    for line in lines:
        if "DNF_TOWN_DEMO failed" in line or re.search(r'\b[A-Za-z0-9_.]+Exception:', line):
            print("日志包含未处理异常或失败标记", file=sys.stderr)
            sys.exit(1)

online_lines = [line for line in lines if line.startswith("DNF_ONLINE ")]
if not online_lines:
    print("日志缺少任何 DNF_ONLINE 行", file=sys.stderr)
    sys.exit(1)

fail_lines = [line for line in online_lines if " event=fail" in line or " result=fail " in line]
if fail_lines:
    print(f"日志出现失败事件 ({len(fail_lines)} 条): {fail_lines[0]}", file=sys.stderr)
    sys.exit(1)

required_stages = ["login", "server", "channel", "charselect", "loading", "ingame", "town", "dungeon", "settle"]
stage_re = re.compile(r'^DNF_ONLINE stage=([a-z]+) event=([a-z]+)\b')
first_index = {}
for idx, line in enumerate(online_lines):
    m = stage_re.match(line)
    if m:
        st = m.group(1)
        if st not in first_index:
            first_index[st] = idx

missing = [st for st in required_stages if st not in first_index]
if missing:
    print(f"缺少必需阶段: {','.join(missing)}", file=sys.stderr)
    sys.exit(1)

prev_idx = -1
prev_st = ""
for st in required_stages:
    cur_idx = first_index[st]
    if cur_idx < prev_idx:
        print(f"阶段首次出现顺序错位: {st} ({cur_idx}) 早于 {prev_st} ({prev_idx})", file=sys.stderr)
        sys.exit(1)
    prev_idx = cur_idx
    prev_st = st

if expect_retries > 0:
    retry_hits = [line for line in online_lines if line.startswith("DNF_ONLINE stage=settle event=retry status=reenter cycle=")]
    if len(retry_hits) < expect_retries:
        print(f"重试循环次数不足: 期望至少 {expect_retries}, 实际 {len(retry_hits)}", file=sys.stderr)
        sys.exit(1)

result_re = re.compile(r'^DNF_ONLINE result=(ok|fail) stage=([a-z0-9_]+) elapsedMs=(\d+)$')
results = []
for line in online_lines:
    m = result_re.match(line)
    if m:
        results.append((m.group(1), m.group(2), int(m.group(3))))

if len(results) != 1:
    print(f"结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 {len(results)} 条", file=sys.stderr)
    sys.exit(1)

res, st, elapsed_ms = results[0]
if res != "ok" or st != "done":
    print(f"结局未成功: result={res} stage={st}", file=sys.stderr)
    sys.exit(1)

if elapsed_ms > max_ms:
    print(f"总耗时超出上限: elapsedMs={elapsed_ms} > {max_ms}", file=sys.stderr)
    sys.exit(1)

print(f"DNF_E2E_CHECK ok stages={len(required_stages)} retries={expect_retries} elapsedMs={elapsed_ms}")
PY
}

validate_live_guards() {
  if [ -z "$PREREG_FILE" ] || [ ! -f "$PREREG_FILE" ] || [ ! -s "$PREREG_FILE" ]; then
    echo "--live 拒绝启动：必须提供存在的非空 --prereg 预注册文件" >&2
    return 1
  fi
  if [ -z "$MIN_CLEAR_SECONDS" ] || ! [[ "$MIN_CLEAR_SECONDS" =~ ^[0-9]+$ ]] || [ "$MIN_CLEAR_SECONDS" -lt 60 ]; then
    echo "--live 拒绝启动：必须显式提供 --mincleartime >= 60（防 E-414 通关过快反作弊计数）" >&2
    return 1
  fi
  return 0
}

# ===========================================================================
# --selftest：离线自测（不需要 Unity、不联网、不读本地凭据）
#   1) 日志校验器 validate_log：正例 + 表驱动负对照，逐项断言退出码与 stderr 原因行（逐字，含行尾换行）。
#   2) --live 护栏：只经 --check-guards 入口、在隔离沙箱副本上逐项断言
#      （退出码恰为 2 且 stderr 逐字等于固定原因行）。自测本身不会走到真服运行。
# ===========================================================================

# 断言失败：整行 FAIL + 缩进细节，整体失败退出（EXIT trap 清理临时目录）
st_fail() {
  echo "FAIL: $1" >&2
  shift
  local detail
  for detail in "$@"; do
    echo "  $detail" >&2
  done
  exit 1
}

# 实际文件与期望文本逐字节比较（含行尾换行）；期望文本为空串表示文件必须是空的
st_check_file() {
  local what="$1" actual="$2" want="$3"
  local expected_file="$_SELFTEST_TMP/expected.txt"
  if [ -n "$want" ]; then
    printf '%s\n' "$want" >"$expected_file"
  else
    : >"$expected_file"
  fi
  if ! cmp -s "$actual" "$expected_file"; then
    st_fail "$what 与期望不一致" "期望: [$want]" "实际: [$(cat "$actual")]"
  fi
}

# 校验器用例：st_log_case <名称> <期望退出码> <期望 stdout 整行，无为空串> <期望 stderr 整行，无为空串>
#                         <max_ms> <期望重试数> <查异常 0|1|->（- 表示省略第 4 参数） <对正例日志的变换…>
# 变换由 editlog.py 解释，可串联：none empty remove / drop <子串> / after <子串> <新行> / before <子串> <新行> /
# append <新行> / replace <唯一旧串> <新串> / swap <子串> <子串> / dup <子串> / crlf / indent <n> / blank / badbytes
st_log_case() {
  local name="$1" want_rc="$2" want_out="$3" want_err="$4" max_ms="$5" retries="$6" check_exc="$7"
  shift 7
  local log="$_SELFTEST_TMP/case.log" out="$_SELFTEST_TMP/case.out" err="$_SELFTEST_TMP/case.err" rc=0
  rm -f "$log"
  python3 "$_SELFTEST_TMP/editlog.py" "$_ST_GOOD" "$log" "$@" \
    || st_fail "[校验器 $name] 用例构造失败（变换与正例日志对不上）"
  if [ "$check_exc" = "-" ]; then
    validate_log "$log" "$max_ms" "$retries" >"$out" 2>"$err" || rc=$?
  else
    validate_log "$log" "$max_ms" "$retries" "$check_exc" >"$out" 2>"$err" || rc=$?
  fi
  _ST_LOG_CASES=$((_ST_LOG_CASES + 1))
  echo "[selftest] 校验器 $name"
  if [ "$rc" != "$want_rc" ]; then
    st_fail "[校验器 $name] 退出码应为 ${want_rc}，实际 $rc" "stdout: [$(cat "$out")]" "stderr: [$(cat "$err")]"
  fi
  st_check_file "[校验器 $name] stdout" "$out" "$want_out"
  st_check_file "[校验器 $name] stderr" "$err" "$want_err"
}

# 正例：退出 0、stdout 恰为给定一行、stderr 为空
st_ok() {
  local name="$1" want_out="$2"
  shift 2
  st_log_case "$name" 0 "$want_out" "" "$@"
}

# 负对照：退出 1、stdout 为空、stderr 恰为给定原因行
st_bad() {
  local name="$1" want_err="$2"
  shift 2
  st_log_case "$name" 1 "" "$want_err" "$@"
}

# 隔离沙箱：脚本副本 + 触发即记账的假后续步骤 + 空 HOME / 空 DNF_DIR；没有 local.env、local.secrets.env，
# 也没有真实的 tools/env.sh。护栏入口（--check-guards）本来就不会走到后续任何步骤，沙箱是双保险：
# 万一护栏被改坏、入口提前放行，后续步骤也只会撞上假 env.sh（记账并以 97 退出），碰不到真实仓库、凭据与 Unity。
st_make_box() {
  _ST_BOX="$_SELFTEST_TMP/box"
  mkdir -p "$_ST_BOX/tools" "$_ST_BOX/trip"
  cp "$SELF_PATH" "$_ST_BOX/tools/online-e2e-mac.sh"
  local step
  for step in env.sh unity-build-town.sh; do
    cat >"$_ST_BOX/tools/$step" <<EOF
printf 'MODE=%s MIN_CLEAR_SECONDS=%s\n' "\${MODE:-}" "\${MIN_CLEAR_SECONDS:-}" >"$_ST_BOX/trip/$step"
printf 'SETTLE_SPEC=%s CHAR_SLOT=%s DUNGEON_ID=%s DUNGEON_MODE=%s MAX_SECONDS=%s NET_ROOT_LIVE=%s FAKE_ROOMS=%s FAKE_MONSTERS=%s EXTRA_NO_DAMAGE=%s\n' "\${SETTLE_SPEC:-}" "\${CHAR_SLOT:-}" "\${DUNGEON_ID:-}" "\${DUNGEON_MODE:-}" "\${MAX_SECONDS:-}" "\${NET_ROOT_LIVE:-}" "\${FAKE_ROOMS:-}" "\${FAKE_MONSTERS:-}" "\${EXTRA_NO_DAMAGE:-}" >"$_ST_BOX/trip/$step.vars"
exit 97
EOF
  done
}

# 在沙箱副本上、清空的环境里运行一次：st_box_run <参数…>；结果落在 $_ST_RC / $_ST_OUT / $_ST_ERR，触发记账在 $_ST_BOX/trip
st_box_run() {
  _ST_OUT="$_SELFTEST_TMP/box.out"
  _ST_ERR="$_SELFTEST_TMP/box.err"
  _ST_RC=0
  rm -f "$_ST_BOX/trip/"*
  env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME="$_SELFTEST_TMP/home" DNF_DIR="$_SELFTEST_TMP/dnfdir" \
    "$BASH" "$_ST_BOX/tools/online-e2e-mac.sh" "$@" >"$_ST_OUT" 2>"$_ST_ERR" || _ST_RC=$?
}

# 护栏用例：st_guard_case <名称> <期望类别> <参数…>（参数自己带 --live --check-guards）
#   ok 通过（退出 0、stdout 恰为固定一行、stderr 空）/ prereg、mincl 两种护栏拒绝（退出恰为 2、stderr 恰为固定原因行）/
#   needlive、noarg_<选项> 用法错误（退出 2、stderr 恰为固定一行；noarg_ 系列是各选项缺值时脚本现有的提示）/
#   unknown=<原样参数> 未知参数（退出 2、stderr 首行恰为"未知参数: <原样参数>"，其后整段照印 --help 的用法文本）/
#   help 帮助（退出 0、stdout 恰为用法文本、stderr 空；用法文本取自开头那次 --help，并已钉死标题行）。
#   任何类别都不许触发沙箱里的后续步骤。
st_guard_case() {
  local name="$1" kind="$2"
  shift 2
  local want_rc want_out="" want_err=""
  case "$kind" in
    ok)             want_rc=0; want_out="DNF_E2E_GUARDS ok" ;;
    prereg)         want_rc=2; want_err="--live 拒绝启动：必须提供存在的非空 --prereg 预注册文件" ;;
    mincl)          want_rc=2; want_err="--live 拒绝启动：必须显式提供 --mincleartime >= 60（防 E-414 通关过快反作弊计数）" ;;
    needlive)       want_rc=2; want_err="--check-guards 必须与 --live 同用" ;;
    noarg_prereg)   want_rc=2; want_err="缺少 --prereg 参数" ;;
    noarg_mincl)    want_rc=2; want_err="缺少 --mincleartime 参数" ;;
    noarg_settle)   want_rc=2; want_err="缺少 --settle 参数" ;;
    noarg_char)     want_rc=2; want_err="缺少 --char 参数" ;;
    noarg_dungeon)  want_rc=2; want_err="缺少 --dungeon <编号> <模式> 参数" ;;
    noarg_maxsec)   want_rc=2; want_err="缺少 --max-seconds 参数" ;;
    noarg_netroot)  want_rc=2; want_err="缺少 --netroot 参数" ;;
    noarg_rooms)    want_rc=2; want_err="缺少 --rooms 参数" ;;
    noarg_monsters) want_rc=2; want_err="缺少 --monsters 参数" ;;
    unknown=*)      want_rc=2; want_err="未知参数: ${kind#unknown=}"$'\n'"${_ST_USAGE}" ;;
    help)           want_rc=0; want_out="${_ST_USAGE}" ;;
    *) st_fail "[护栏 $name] 用例表里的期望类别未知: ${kind}" ;;
  esac
  st_box_run "$@"
  _ST_GUARD_CASES=$((_ST_GUARD_CASES + 1))
  echo "[selftest] 护栏 $name"
  if [ "$_ST_RC" != "$want_rc" ]; then
    st_fail "[护栏 $name] 退出码应为 ${want_rc}，实际 $_ST_RC" "stdout: [$(cat "$_ST_OUT")]" "stderr: [$(cat "$_ST_ERR")]"
  fi
  st_check_file "[护栏 $name] stdout" "$_ST_OUT" "$want_out"
  st_check_file "[护栏 $name] stderr" "$_ST_ERR" "$want_err"
  if [ -n "$(ls -A "$_ST_BOX/trip")" ]; then
    st_fail "[护栏 $name] 护栏入口走到了后续步骤（沙箱里的假步骤被触发）" "触发记录: $(ls "$_ST_BOX/trip")"
  fi
}

# 沙箱放行对照（不带 --check-guards；后续步骤是沙箱里触发即记账的假 env.sh，退出 97）：
#   st_box_case <名称> <期望退出码> <期望 stderr 整行，无为空串> <期望 env.sh 记账，无为空串> <参数…>
st_box_case() {
  local name="$1" want_rc="$2" want_err="$3" want_trip="$4"
  shift 4
  st_box_run "$@"
  _ST_GUARD_CASES=$((_ST_GUARD_CASES + 1))
  echo "[selftest] 护栏 $name"
  if [ "$_ST_RC" != "$want_rc" ]; then
    st_fail "[护栏 $name] 退出码应为 ${want_rc}，实际 $_ST_RC" "stdout: [$(cat "$_ST_OUT")]" "stderr: [$(cat "$_ST_ERR")]"
  fi
  st_check_file "[护栏 $name] stdout" "$_ST_OUT" ""
  st_check_file "[护栏 $name] stderr" "$_ST_ERR" "$want_err"
  if [ -n "$want_trip" ]; then
    [ -f "$_ST_BOX/trip/env.sh" ] || st_fail "[护栏 $name] 放行后应进入下一步（假 env.sh），实际没有"
    st_check_file "[护栏 $name] env.sh 记账" "$_ST_BOX/trip/env.sh" "$want_trip"
    [ ! -e "$_ST_BOX/trip/unity-build-town.sh" ] || st_fail "[护栏 $name] 越过 env.sh 走到了构建脚本"
  elif [ -n "$(ls -A "$_ST_BOX/trip")" ]; then
    st_fail "[护栏 $name] 被拒绝的运行却走到了后续步骤" "触发记录: $(ls "$_ST_BOX/trip")"
  fi
}

# 解析结果对照：--dry 走到沙箱里的假 env.sh，它把脚本解析出的各个变量记到 trip/env.sh.vars 再以 97 退出
#   st_parse_case <名称> <期望记账，整行> <参数…>（退出恰为 97、stdout 与 stderr 都空、记账逐字相等）
st_parse_case() {
  local name="$1" want_vars="$2"
  shift 2
  st_box_run "$@"
  _ST_GUARD_CASES=$((_ST_GUARD_CASES + 1))
  echo "[selftest] 护栏 $name"
  if [ "$_ST_RC" != "97" ]; then
    st_fail "[护栏 $name] 退出码应为 97（走到假 env.sh），实际 ${_ST_RC}" "stdout: [$(cat "$_ST_OUT")]" "stderr: [$(cat "$_ST_ERR")]"
  fi
  st_check_file "[护栏 $name] stdout" "$_ST_OUT" ""
  st_check_file "[护栏 $name] stderr" "$_ST_ERR" ""
  [ -f "$_ST_BOX/trip/env.sh.vars" ] || st_fail "[护栏 $name] 假 env.sh 没有留下解析结果记账"
  st_check_file "[护栏 $name] 解析结果" "$_ST_BOX/trip/env.sh.vars" "$want_vars"
}

# ---- 失败路径负对照 ----
# 为什么要有它：--selftest 打印"通过"，不等于它在失败时真的会失败。上一轮发现过一个真缺口：macOS 自带的 bash 3.2 在 UTF-8
# 语言环境下，会把紧跟在 "$变量名" 后面的全角标点当成变量名的一部分，set -u 之下报 unbound variable，脚本悄悄退出且退出码为 0——
# --selftest 的失败路径（FAIL: 行 + 退出 1）在 UTF-8 下就这样被整个吞掉，C 语言环境下却一切正常，所以一直没被发现。
# 谁来运行：--selftest 自己在最后自动运行，不需要、也不应该另外手动跑；它只在隔离的临时目录里动手，不碰仓库文件。
# 做法：把本脚本复制到临时目录并故意改坏一处（BREAKS 表里的 5 种：三处退出码比较、stdout 期望、stderr 期望），在空环境
# （只有 HOME 与 PATH，另加 LC_ALL 与递归标记）里用 /bin/bash，分别在 LC_ALL=C 与 LC_ALL=en_US.UTF-8 下跑改坏的副本的 --selftest。
# 要求：退出码恰为 1；stderr 里第一条以 FAIL: 开头的行逐字符符合预期（包括"退出码应为 N，实际 M"里的数字——正是 UTF-8 下被吞的那一段）；
# stdout 里没有 ALL PASSED。另有一条没改坏的副本：带着递归标记跑，必须不再递归、照常以 ALL PASSED 收尾。
# 本机没有 en_US.UTF-8 时：大声说出来并跳过 UTF-8 那一半（stdout 与 stderr 各一行），绝不当作通过。
# 递归：改坏的副本里也有这段代码，所以外层给它们设了环境变量 E2E_SELFTEST_NESTED；带着它的运行跳过本段并打印说明。
st_negative_control() {
  local ctl="$_SELFTEST_TMP/negctl" utf8_ok=0
  mkdir -p "$ctl"
  if [ "$(LC_ALL=en_US.UTF-8 locale charmap 2>/dev/null || true)" = "UTF-8" ]; then
    utf8_ok=1
  fi
  echo "[selftest] 失败路径负对照（由本 --selftest 自动运行，不用另外手动跑）："
  echo "[selftest]   把本脚本复制到临时目录、各故意改坏一处；空环境（只有 HOME 与 PATH，另加 LC_ALL 与递归标记）、/bin/bash 下，"
  echo "[selftest]   分别用 LC_ALL=C 与 LC_ALL=en_US.UTF-8 跑改坏的副本的 --selftest，要求退出 1 且 stderr 有预期的失败行"
  python3 - "$SELF_PATH" "$ctl" "$utf8_ok" >"$ctl/result.txt" <<'NEGCTL_PY' || st_fail "负对照的驱动自己出错了（不是改坏的副本没失败）" "驱动的报错见上方 stderr"
import concurrent.futures
import os
import re
import subprocess
import sys

self_path, ctl, utf8_ok = sys.argv[1], sys.argv[2], sys.argv[3] == "1"
BASH = "/bin/bash"
MARK = "# ---- 失败路径负对照 ----"
text = open(self_path, encoding="utf-8").read()
head = text[: text.index(MARK)]   # 只在负对照段之前找锚点：本段自己的代码里也写着这些锚点，不能算进去

RC_RE = r"^FAIL: \[{what} [^\]]+\] 退出码应为 (\d+)，实际 \1$"
# (编号, 说明, 锚点, 锚点在 head 里应出现的次数, 改第几处（0 起）, 改成, 预期的第一条 FAIL 行)
BREAKS = [
    ("L1", "校验器用例的退出码比较反过来", '  if [ "$rc" != "$want_rc" ]; then', 1, 0,
     '  if [ "$rc" = "$want_rc" ]; then', RC_RE.format(what="校验器")),
    ("G1", "护栏用例的退出码比较反过来", '  if [ "$_ST_RC" != "$want_rc" ]; then', 2, 0,
     '  if [ "$_ST_RC" = "$want_rc" ]; then', RC_RE.format(what="护栏")),
    ("H1", "沙箱放行用例的退出码比较反过来", '  if [ "$_ST_RC" != "$want_rc" ]; then', 2, 1,
     '  if [ "$_ST_RC" = "$want_rc" ]; then', RC_RE.format(what="护栏")),
    ("O1", "校验器用例的 stdout 期望被污染", '  st_check_file "[校验器 $name] stdout" "$out" "$want_out"', 1, 0,
     '  st_check_file "[校验器 $name] stdout" "$out" "x${want_out}"', r"^FAIL: \[校验器 [^\]]+\] stdout 与期望不一致$"),
    ("E1", "校验器用例的 stderr 期望被污染", '  st_check_file "[校验器 $name] stderr" "$err" "$want_err"', 1, 0,
     '  st_check_file "[校验器 $name] stderr" "$err" "x${want_err}"', r"^FAIL: \[校验器 [^\]]+\] stderr 与期望不一致$"),
]
LANGS = ["C"] + (["en_US.UTF-8"] if utf8_ok else [])
JOBS = [("break", b, lang) for b in BREAKS for lang in LANGS] + [("clean", None, lang) for lang in LANGS]


def run(job):
    kind, b, lang = job
    bid = b[0] if b else "R1"
    what = b[1] if b else "没改坏的副本带着递归标记：不再递归、照常通过"
    label = "%s %s [LC_ALL=%s]" % (bid, what, lang)

    def result(status, detail):
        return status, bid, lang, label, detail

    body = text
    if b:
        anchor, count, which, repl = b[2], b[3], b[4], b[5]
        n = head.count(anchor)
        if n != count:
            return result("BAD", "锚点出现 %d 次、期望 %d 次（脚本被改动过？同步更新本段的锚点）：%s" % (n, count, anchor.strip()))
        pos = -1
        for _ in range(which + 1):
            pos = head.index(anchor, pos + 1)
        body = head[:pos] + repl + head[pos + len(anchor):] + text[len(head):]
    folder = os.path.join(ctl, "%s-%s" % (bid, "C" if lang == "C" else "UTF8"))
    os.makedirs(os.path.join(folder, "tools"))
    path = os.path.join(folder, "tools", "online-e2e-mac.sh")
    with open(path, "w", encoding="utf-8") as f:
        f.write(body)
    os.chmod(path, 0o755)
    env = {"HOME": ctl, "PATH": os.environ.get("PATH", "/usr/bin:/bin:/usr/sbin:/sbin"), "LC_ALL": lang, "E2E_SELFTEST_NESTED": "1"}
    try:
        p = subprocess.run([BASH, path, "--selftest"], env=env, cwd=folder, capture_output=True, timeout=300)
    except subprocess.TimeoutExpired:
        return result("BAD", "超过 300 秒没有结束")
    except OSError as e:
        return result("BAD", "起不来 %s：%s" % (BASH, e))
    out = p.stdout.decode("utf-8", "replace")
    err = p.stderr.decode("utf-8", "replace")
    problems = []
    if b:
        fails = [ln for ln in err.splitlines() if ln.startswith("FAIL: ")]
        if p.returncode != 1:
            problems.append("退出码应为 1，实际 %d（改坏的自测失败了却没有以 1 退出）" % p.returncode)
        if not fails:
            problems.append("stderr 里没有以 FAIL: 开头的行（失败被吞掉了）")
        elif not re.match(b[6], fails[0]):
            problems.append("第一条 FAIL 行与预期不符：[%s]" % fails[0])
        if "ALL PASSED" in out:
            problems.append("stdout 里居然出现了自测的收尾通过标记")
        if problems:
            tail = (err.strip().splitlines() or ["<空>"])[:2]
            problems.append("stderr 开头: %s" % " / ".join(tail).replace(path, "<改坏的副本>").replace(ctl, "<临时目录>"))
            return result("BAD", "；".join(problems))
        return result("OK", "退出 1，按预期失败：" + fails[0][len("FAIL: "):])
    lines = out.splitlines()
    if p.returncode != 0:
        problems.append("退出码应为 0，实际 %d" % p.returncode)
    if not lines or lines[-1] != "[selftest] ALL PASSED":
        problems.append("最后一行不是自测的收尾通过标记")
    if not any(ln.startswith("[selftest] 负对照: 本次是嵌套运行") for ln in lines):
        problems.append("没有打印嵌套运行的跳过说明（递归标记没起作用？）")
    if any(re.match(r"\[selftest\] 负对照 [A-Z][0-9] ", ln) for ln in lines):
        problems.append("它又跑了一遍负对照（递归没被挡住）")
    if err.strip():
        problems.append("stderr 应为空，实际: %s" % err.strip().splitlines()[0].replace(path, "<副本>").replace(ctl, "<临时目录>"))
    if problems:
        return result("BAD", "；".join(problems))
    return result("OK", "退出 0，自测照常收尾，已打印嵌套跳过说明、没有再递归")


with concurrent.futures.ThreadPoolExecutor(max_workers=6) as ex:
    for status, bid, lang, label, detail in ex.map(run, JOBS):
        print("|".join((status, bid, lang, label, detail)))
if not utf8_ok:
    print("|".join(("SKIP", "U8", "en_US.UTF-8", "UTF-8 语言环境下的全部负对照",
                    "本机没有 en_US.UTF-8（LC_ALL=en_US.UTF-8 locale charmap 的结果不是 UTF-8），这一半没有被验证")))
NEGCTL_PY
  local kind bid lang label detail bad=0 skipped=0 ok_c=0 ok_u=0 seen_c="" seen_u=""
  while IFS='|' read -r kind bid lang label detail; do
    case "$kind" in
      OK)
        echo "[selftest] 负对照 ${label}: ${detail}"
        if [ "$lang" = "C" ]; then
          ok_c=$((ok_c + 1)); seen_c="${seen_c} ${bid} "
        else
          ok_u=$((ok_u + 1)); seen_u="${seen_u} ${bid} "
        fi
        ;;
      SKIP)
        skipped=$((skipped + 1))
        echo "[selftest] 警告：负对照 ${label} 被跳过——${detail}"
        echo "WARNING: 负对照 ${label} 被跳过：${detail}" >&2
        ;;
      BAD)
        bad=$((bad + 1))
        echo "FAIL: [负对照 ${label}] ${detail}" >&2
        ;;
      *)
        bad=$((bad + 1))
        echo "FAIL: [负对照] 驱动输出了无法识别的一行: ${kind}|${bid}|${lang}|${label}|${detail}" >&2
        ;;
    esac
  done <"$ctl/result.txt"
  [ "$bad" = "0" ] || st_fail "失败路径负对照有 ${bad} 项没通过（逐项见上）"
  # 表被悄悄删掉几行也要能发现：每种改坏与那条没改坏的副本，在每个跑过的语言环境下都必须有一条通过的记录
  local id
  for id in L1 G1 H1 O1 E1 R1; do
    case "$seen_c" in *" ${id} "*) ;; *) st_fail "失败路径负对照缺了 ${id}（LC_ALL=C 下没有通过记录）" ;; esac
    if [ "$skipped" = "0" ]; then
      case "$seen_u" in *" ${id} "*) ;; *) st_fail "失败路径负对照缺了 ${id}（LC_ALL=en_US.UTF-8 下没有通过记录）" ;; esac
    fi
  done
  if [ "$skipped" = "0" ]; then
    echo "[selftest] 失败路径负对照：LC_ALL=C ${ok_c} 项、LC_ALL=en_US.UTF-8 ${ok_u} 项，全部通过"
  else
    echo "[selftest] 警告：失败路径负对照只验证了 LC_ALL=C 的 ${ok_c} 项；UTF-8 那一半被跳过，没有验证"
  fi
}

run_selftest() {
  _SELFTEST_TMP="$(mktemp -d)"
  trap 'rm -rf "${_SELFTEST_TMP:-}"' EXIT
  local tmp="$_SELFTEST_TMP"
  _ST_LOG_CASES=0
  _ST_GUARD_CASES=0

  # 双保险（不是唯一依据：唯一依据是 --live 护栏入口 --check-guards 根本不会走到后续任何步骤）：
  # 先清掉与真服相关的环境变量（DNF_* 含 DNF_DIR / DNF_SECRETS_FILE / 服务器连接项，另有 SSHPASS），
  # 再让 HOME 与 DNF_DIR 指向空目录——即使护栏被改坏，也读不到凭据与游戏目录。
  local v
  for v in $(compgen -v | grep -E '^(DNF_|SSHPASS$)' || true); do
    unset "$v"
  done
  mkdir -p "$tmp/home" "$tmp/dnfdir"
  export HOME="$tmp/home"
  export DNF_DIR="$tmp/dnfdir"
  echo "[selftest] 隔离: 已清除 DNF_* 与 SSHPASS，HOME 与 DNF_DIR 指向空目录"

  # ---- 正例日志（每条都是合成数据；第 0..23 条，下面的乱序用例按这些下标手算期望）----
  _ST_GOOD="$tmp/good.log"
  cat >"$_ST_GOOD" <<'EOF'
DNF_ONLINE stage=login event=enter reason=start
DNF_ONLINE stage=login event=ok reason=auth
DNF_ONLINE stage=server event=enter reason=login_ok
DNF_ONLINE stage=server event=ok reason=directory
DNF_ONLINE stage=server event=ok reason=choose
DNF_ONLINE stage=channel event=enter reason=choose
DNF_ONLINE stage=channel event=ok reason=connected
DNF_ONLINE stage=charselect event=enter reason=connected
DNF_ONLINE stage=charselect event=ok reason=confirm
DNF_ONLINE stage=loading event=enter reason=select
DNF_ONLINE stage=loading event=ok reason=selected
DNF_ONLINE stage=ingame event=enter reason=handover
DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=present snap=present slot=match level=match job=match grow=match
DNF_ONLINE stage=dungeon event=select outcome=enter difficulty=0 max=3
DNF_ONLINE stage=dungeon event=enter areakind=Normal entrance=false difficulty=0
DNF_ONLINE stage=dungeon event=room status=Ready multi=off spawned=2 expected=2 rooms=1
DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=2 dropped=0 rooms=1 result=Succeeded reward=Succeeded
DNF_ONLINE stage=settle event=advance step=score_scroll status=sent
DNF_ONLINE stage=settle event=advance step=score_scroll status=ok
DNF_ONLINE stage=settle event=choose choice=Retry source=player
DNF_ONLINE stage=settle event=retry status=reenter cycle=1
DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=player
DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair
DNF_ONLINE result=ok stage=done elapsedMs=8420
EOF

  # ---- 日志变换器：把正例日志改成各种坏日志 / 边界日志（只用于自测）----
  cat >"$tmp/editlog.py" <<'PY'
import sys

src, dst = sys.argv[1], sys.argv[2]
ops = sys.argv[3:]
lines = open(src, encoding="utf-8").read().splitlines()
tail = b""
eol = "\n"
pad = ""
blank = False
remove = False


def first(pat):
    for k, text in enumerate(lines):
        if pat in text:
            return k
    raise SystemExit("editlog: 正例日志里找不到: " + pat)


i = 0
while i < len(ops):
    op = ops[i]
    i += 1
    if op == "none":
        pass
    elif op == "empty":
        lines = []
    elif op == "remove":
        remove = True
    elif op == "drop":
        pat = ops[i]
        i += 1
        lines = [text for text in lines if pat not in text]
    elif op == "after":
        pat, new = ops[i], ops[i + 1]
        i += 2
        lines.insert(first(pat) + 1, new)
    elif op == "before":
        pat, new = ops[i], ops[i + 1]
        i += 2
        lines.insert(first(pat), new)
    elif op == "append":
        lines.append(ops[i])
        i += 1
    elif op == "replace":
        old, new = ops[i], ops[i + 1]
        i += 2
        joined = "\n".join(lines)
        if joined.count(old) != 1:
            raise SystemExit("editlog: replace 的旧串必须在日志里恰出现一次: " + old)
        lines = joined.replace(old, new).split("\n")
    elif op == "swap":
        a, b = first(ops[i]), first(ops[i + 1])
        i += 2
        lines[a], lines[b] = lines[b], lines[a]
    elif op == "dup":
        k = first(ops[i])
        i += 1
        lines.insert(k + 1, lines[k])
    elif op == "crlf":
        eol = "\r\n"
    elif op == "indent":
        pad = " " * int(ops[i])
        i += 1
    elif op == "blank":
        blank = True
    elif op == "badbytes":
        tail = b"noise \xff\xfe line\n"
    else:
        raise SystemExit("editlog: 未知变换: " + op)

if not remove:
    out = []
    for text in lines:
        out.append(pad + text + ("   " if blank else ""))
        if blank:
            out.append("")
    body = eol.join(out) + (eol if out else "")
    with open(dst, "wb") as fh:
        fh.write(body.encode("utf-8") + tail)
PY

  # ======================= 一、日志校验器 =======================
  # 期望值都是手算的字面量：每条原因行逐字；耗时/重试数的边界（恰等于、差 1）与期望 0/1/2/3 各有对照。

  # ---- 正例（stdout 整行逐字；stderr 必须为空）----
  st_ok "P01 完整日志，期望 1 次重试且恰有 1 次" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 none
  st_ok "P02 期望 0 次重试（日志里有 1 次也不查）" "DNF_E2E_CHECK ok stages=9 retries=0 elapsedMs=8420" 60000 0 1 none
  st_ok "P03 期望 0 次重试且日志里确实 0 次" "DNF_E2E_CHECK ok stages=9 retries=0 elapsedMs=8420" 60000 0 1 drop "event=retry"
  st_ok "P04 期望 2 次重试且恰有 2 次" "DNF_E2E_CHECK ok stages=9 retries=2 elapsedMs=8420" 60000 2 1 \
    after "event=retry status=reenter cycle=1" "DNF_ONLINE stage=settle event=retry status=reenter cycle=2"
  st_ok "P05 期望 1 次重试而日志里有 2 次（多于期望放行）" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 \
    after "event=retry status=reenter cycle=1" "DNF_ONLINE stage=settle event=retry status=reenter cycle=2"
  st_ok "P06 耗时 8420 恰等于上限 8420 放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 8420 1 1 none
  st_ok "P07 耗时 8420 比上限 8421 小 1 放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 8421 1 1 none
  st_ok "P08 另一组耗时 61234 恰等于上限 61234 放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=61234" 61234 1 1 \
    replace "elapsedMs=8420" "elapsedMs=61234"
  st_ok "P09 耗时 0 对上限 0 放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=0" 0 1 1 replace "elapsedMs=8420" "elapsedMs=0"
  st_ok "P10 不查异常（第 4 参数为 0）时异常行放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 0 \
    append "System.InvalidOperationException: boom"
  st_ok "P11 日志用 CRLF 行尾放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 crlf
  st_ok "P12 每行缩进 4 个空格并夹空行、行尾带空格放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 indent 4 blank
  st_ok "P13 夹带非 UTF-8 字节的噪声行放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 badbytes
  st_ok "P14 夹带无关噪声行放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 \
    after "stage=login event=enter" "[Unity] noise line"
  st_ok "P15 出现 Exception 字样但没有冒号的行放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 \
    append "NullReferenceException at Foo.Bar"
  st_ok "P16 没有类型名前缀的 Exception: 行放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 \
    append "Exception: plain"
  st_ok "P17 类型名只有点号前缀（..Exception:）的行放行（\\b 要求类型名从字母数字起头）" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 \
    append "..Exception: dotted"
  st_ok "P18 DNF_ONLINEX 开头（前缀后没有空格）的 event=fail 行不算 DNF_ONLINE 行，放行" "DNF_E2E_CHECK ok stages=9 retries=1 elapsedMs=8420" 60000 1 1 \
    append "DNF_ONLINEX stage=login event=fail reason=stray"

  # ---- 负对照：异常与失败标记（原因行恒为同一句）----
  local m_exc="日志包含未处理异常或失败标记"
  st_bad "E01 异常行 System.InvalidOperationException:" "$m_exc" 60000 1 1 append "System.InvalidOperationException: boom"
  st_bad "E02 异常行 System.Exception:（类型名就叫 Exception）" "$m_exc" 60000 1 1 append "System.Exception: boom"
  st_bad "E03 异常行 SomeException:" "$m_exc" 60000 1 1 append "SomeException: boom"
  st_bad "E04 异常行 Type2Exception:（类型名带数字）" "$m_exc" 60000 1 1 append "Type2Exception: boom"
  st_bad "E05 异常行 My_TypeException:（类型名带下划线）" "$m_exc" 60000 1 1 append "My_TypeException: boom"
  st_bad "E06 含 DNF_TOWN_DEMO failed 的行" "$m_exc" 60000 1 1 append "DNF_TOWN_DEMO failed reason=x"
  st_bad "E07 异常行出现在第一条 DNF_ONLINE 之前" "$m_exc" 60000 1 1 before "stage=login event=enter" "Foo.BarException: early"
  st_bad "E08 省略第 4 参数时默认查异常" "$m_exc" 60000 1 - append "System.InvalidOperationException: boom"

  # ---- 负对照：无 DNF_ONLINE 行 / 读不到日志 ----
  st_bad "N01 空日志" "日志缺少任何 DNF_ONLINE 行" 60000 1 1 empty
  st_bad "N02 只有噪声行没有 DNF_ONLINE 行" "日志缺少任何 DNF_ONLINE 行" 60000 1 1 drop "DNF_ONLINE" append "[Unity] player started"
  st_bad "N03 日志文件不存在" "日志读取失败: [Errno 2] No such file or directory: '$tmp/case.log'" 60000 1 1 remove
  st_bad "N04 只有 DNF_ONLINEX 开头（前缀后没有空格）的行不算 DNF_ONLINE 行" "日志缺少任何 DNF_ONLINE 行" 60000 1 1 \
    drop "DNF_ONLINE" append "DNF_ONLINEX stage=login event=enter reason=start"

  # ---- 负对照：缺必需阶段（每个必需阶段单独一条；原因行逐字）----
  st_bad "M01 缺 login 阶段" "缺少必需阶段: login" 60000 1 1 drop "stage=login "
  st_bad "M02 缺 server 阶段" "缺少必需阶段: server" 60000 1 1 drop "stage=server "
  st_bad "M03 缺 channel 阶段" "缺少必需阶段: channel" 60000 1 1 drop "stage=channel "
  st_bad "M04 缺 charselect 阶段" "缺少必需阶段: charselect" 60000 1 1 drop "stage=charselect "
  st_bad "M05 缺 loading 阶段" "缺少必需阶段: loading" 60000 1 1 drop "stage=loading "
  st_bad "M06 缺 ingame 阶段" "缺少必需阶段: ingame" 60000 1 1 drop "stage=ingame "
  st_bad "M07 缺 town 阶段" "缺少必需阶段: town" 60000 1 1 drop "stage=town "
  st_bad "M08 缺 dungeon 阶段" "缺少必需阶段: dungeon" 60000 1 1 drop "stage=dungeon "
  st_bad "M09 缺 settle 阶段" "缺少必需阶段: settle" 60000 1 1 drop "stage=settle "
  st_bad "M10 同时缺 server 与 dungeon（按必需阶段表顺序、逗号相连）" "缺少必需阶段: server,dungeon" 60000 1 1 \
    drop "stage=server " drop "stage=dungeon "
  st_bad "M11 九个阶段行全缺（只剩结局行）" "缺少必需阶段: login,server,channel,charselect,loading,ingame,town,dungeon,settle" 60000 1 1 \
    drop "DNF_ONLINE stage="
  st_bad "M12 没有 event= 的残缺 settle 行不算阶段" "缺少必需阶段: settle" 60000 0 1 \
    drop "stage=settle " append "DNF_ONLINE stage=settle reason=partial"
  st_bad "M13 事件名带数字后缀（event=ok2）不算阶段" "缺少必需阶段: settle" 60000 0 1 \
    drop "stage=settle " append "DNF_ONLINE stage=settle event=ok2 reason=x"
  st_bad "M14 事件名以数字开头（event=1）不算阶段" "缺少必需阶段: settle" 60000 0 1 \
    drop "stage=settle " append "DNF_ONLINE stage=settle event=1"
  st_bad "M15 阶段名大写（stage=Settle）不算阶段" "缺少必需阶段: settle" 60000 0 1 \
    drop "stage=settle " append "DNF_ONLINE stage=Settle event=ok"
  st_bad "M16 事件名大写（event=OK）不算阶段" "缺少必需阶段: settle" 60000 0 1 \
    drop "stage=settle " append "DNF_ONLINE stage=settle event=OK"

  # ---- 负对照：阶段乱序（后一阶段的首次出现早于前一阶段；括号里是手算的 0 起下标）----
  st_bad "O01 server 首现早于 login" "阶段首次出现顺序错位: server (0) 早于 login (1)" 60000 1 1 \
    before "stage=login " "DNF_ONLINE stage=server event=enter reason=stray"
  st_bad "O02 channel 首现早于 server" "阶段首次出现顺序错位: channel (2) 早于 server (3)" 60000 1 1 \
    before "stage=server " "DNF_ONLINE stage=channel event=enter reason=stray"
  st_bad "O03 charselect 首现早于 channel" "阶段首次出现顺序错位: charselect (5) 早于 channel (6)" 60000 1 1 \
    before "stage=channel " "DNF_ONLINE stage=charselect event=enter reason=stray"
  st_bad "O04 loading 首现早于 charselect" "阶段首次出现顺序错位: loading (7) 早于 charselect (8)" 60000 1 1 \
    before "stage=charselect " "DNF_ONLINE stage=loading event=enter reason=stray"
  st_bad "O05 ingame 首现早于 loading" "阶段首次出现顺序错位: ingame (9) 早于 loading (10)" 60000 1 1 \
    before "stage=loading " "DNF_ONLINE stage=ingame event=enter reason=stray"
  st_bad "O06 town 首现早于 ingame" "阶段首次出现顺序错位: town (11) 早于 ingame (12)" 60000 1 1 \
    before "stage=ingame " "DNF_ONLINE stage=town event=enter reason=stray"
  st_bad "O07 dungeon 首现早于 town" "阶段首次出现顺序错位: dungeon (12) 早于 town (13)" 60000 1 1 \
    before "stage=town " "DNF_ONLINE stage=dungeon event=enter reason=stray"
  st_bad "O08 settle 首现早于 dungeon" "阶段首次出现顺序错位: settle (13) 早于 dungeon (14)" 60000 1 1 \
    before "stage=dungeon " "DNF_ONLINE stage=settle event=enter reason=stray"
  st_bad "O09 town 与 dungeon 两行对调（原负对照 4）" "阶段首次出现顺序错位: dungeon (12) 早于 town (13)" 60000 1 1 \
    swap "stage=town " "stage=dungeon event=select"

  # ---- 负对照：失败事件（计数与展示的是最先出现的一条）----
  st_bad "F01 多出一条 event=fail" "日志出现失败事件 (1 条): DNF_ONLINE stage=channel event=fail reason=network" 60000 1 1 \
    after "stage=channel event=ok reason=connected" "DNF_ONLINE stage=channel event=fail reason=network"
  st_bad "F02 两条 event=fail（计数 2，展示更早出现的那条）" "日志出现失败事件 (2 条): DNF_ONLINE stage=server event=fail reason=refused" 60000 1 1 \
    after "stage=channel event=ok reason=connected" "DNF_ONLINE stage=channel event=fail reason=network" \
    after "stage=server event=ok reason=choose" "DNF_ONLINE stage=server event=fail reason=refused"
  st_bad "F03 结局行本身是 result=fail" "日志出现失败事件 (1 条): DNF_ONLINE result=fail stage=town elapsedMs=8420" 60000 1 1 \
    replace "result=ok stage=done" "result=fail stage=town"

  # ---- 负对照：重试次数（期望 1、2、3 各一条；只有 stage=settle event=retry status=reenter cycle= 开头的行才计数）----
  st_bad "R01 期望 1 次重试而日志里没有" "重试循环次数不足: 期望至少 1, 实际 0" 60000 1 1 drop "event=retry"
  st_bad "R02 期望 2 次重试而日志里只有 1 次" "重试循环次数不足: 期望至少 2, 实际 1" 60000 2 1 none
  st_bad "R03 期望 3 次重试而日志里只有 2 次" "重试循环次数不足: 期望至少 3, 实际 2" 60000 3 1 \
    after "event=retry status=reenter cycle=1" "DNF_ONLINE stage=settle event=retry status=reenter cycle=2"
  st_bad "R04 retry 行的 status 不是 reenter 不计入" "重试循环次数不足: 期望至少 1, 实际 0" 60000 1 1 \
    replace "event=retry status=reenter cycle=1" "event=retry status=failed cycle=1"
  st_bad "R05 reenter 行缺 cycle= 不计入" "重试循环次数不足: 期望至少 1, 实际 0" 60000 1 1 \
    replace "status=reenter cycle=1" "status=reenter"
  st_bad "R06 阶段不是 settle 的 retry 行不计入" "重试循环次数不足: 期望至少 1, 实际 0" 60000 1 1 \
    replace "stage=settle event=retry" "stage=town event=retry"
  st_bad "R07 retry 前缀只出现在行中间（不在行首）不计入" "重试循环次数不足: 期望至少 1, 实际 0" 60000 1 1 \
    replace "DNF_ONLINE stage=settle event=retry status=reenter cycle=1" "DNF_ONLINE stage=settle event=note text=DNF_ONLINE stage=settle event=retry status=reenter cycle=1"

  # ---- 负对照：结局行（数量、格式、内容）----
  st_bad "Z01 缺结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 drop "DNF_ONLINE result="
  st_bad "Z02 两行结局" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 2 条" 60000 1 1 \
    append "DNF_ONLINE result=ok stage=done elapsedMs=9000"
  st_bad "Z03 三行结局" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 3 条" 60000 1 1 \
    append "DNF_ONLINE result=ok stage=done elapsedMs=9000" append "DNF_ONLINE result=ok stage=done elapsedMs=9100"
  st_bad "Z04 结局行行尾带多余字符不算结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 \
    replace "elapsedMs=8420" "elapsedMs=8420 extra=1"
  st_bad "Z05 elapsedMs 不是数字不算结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 \
    replace "elapsedMs=8420" "elapsedMs=abc"
  st_bad "Z06 elapsedMs 为空不算结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 \
    replace "elapsedMs=8420" "elapsedMs="
  st_bad "Z07 缺 elapsedMs 字段不算结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 \
    replace " elapsedMs=8420" ""
  st_bad "Z08 result 值大写（result=OK）不算结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 \
    replace "result=ok stage=done" "result=OK stage=done"
  st_bad "Z09 结局阶段名大写（stage=Done）不算结局行" "结局行数量异常: 期望恰 1 条 DNF_ONLINE result=..., 实际 0 条" 60000 1 1 \
    replace "result=ok stage=done" "result=ok stage=Done"
  st_bad "J01 结局 result=ok 但 stage=town（不是 done）" "结局未成功: result=ok stage=town" 60000 1 1 \
    replace "result=ok stage=done" "result=ok stage=town"
  st_bad "J02 结局阶段名带数字与下划线（stage=town_2）仍按结局行判" "结局未成功: result=ok stage=town_2" 60000 1 1 \
    replace "result=ok stage=done" "result=ok stage=town_2"

  # ---- 负对照：耗时上限（差 1 即超；换一组不同数值再来一条）----
  st_bad "T01 耗时 8420 超上限 5000" "总耗时超出上限: elapsedMs=8420 > 5000" 5000 1 1 none
  st_bad "T02 耗时 8420 比上限 8419 大 1" "总耗时超出上限: elapsedMs=8420 > 8419" 8419 1 1 none
  st_bad "T03 另一组耗时 61234 比上限 61233 大 1" "总耗时超出上限: elapsedMs=61234 > 61233" 61233 1 1 \
    replace "elapsedMs=8420" "elapsedMs=61234"

  # ======================= 二、--live 护栏（只经 --check-guards 入口，隔离沙箱副本）=======================
  st_make_box
  local pre_ok="$tmp/prereg.ok" pre_empty="$tmp/prereg.empty" pre_missing="$tmp/prereg.missing"
  local pre_dir="$tmp/prereg.dir" pre_one="$tmp/prereg.one"
  printf '%s\n' "prereg-synthetic" >"$pre_ok"
  : >"$pre_empty"
  mkdir "$pre_dir"
  printf '\n' >"$pre_one"

  # 用法文本：取自沙箱副本的 --help（stdout）。后面 help、unknown= 两类用例都拿它当期望，所以先把它钉住：
  # 退出 0、stderr 为空、标题行逐字不变、不止一行——免得"两边都是空"也算过。
  st_box_run --help
  _ST_USAGE="$(cat "$_ST_OUT")"
  if [ "$_ST_RC" != "0" ] || [ -s "$_ST_ERR" ] \
    || [ "$(head -n 1 "$_ST_OUT")" != "用法: tools/online-e2e-mac.sh [--dry | --live | --selftest] [选项]" ] \
    || [ "$(wc -l <"$_ST_OUT" | tr -d ' ')" -lt 10 ]; then
    st_fail "[用法文本] --help 应退出 0、stderr 为空、stdout 标题行逐字不变且不止一行" \
      "退出码: ${_ST_RC}" "stdout 首行: [$(head -n 1 "$_ST_OUT")]" "stderr: [$(cat "$_ST_ERR")]"
  fi

  # ---- --mincleartime 的每个边界各自一条（预注册文件合格）----
  st_guard_case "G01 秒数 59 拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime 59
  st_guard_case "G02 秒数 60 放行" ok --live --check-guards --prereg "$pre_ok" --mincleartime 60
  st_guard_case "G03 秒数 61 放行" ok --live --check-guards --prereg "$pre_ok" --mincleartime 61
  st_guard_case "G04 秒数 0 拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime 0
  st_guard_case "G05 秒数为空串拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime ""
  st_guard_case "G06 没提供 --mincleartime 拒绝" mincl --live --check-guards --prereg "$pre_ok"
  st_guard_case "G07 秒数 abc 拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime abc
  st_guard_case "G08 秒数 60abc 拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime 60abc
  st_guard_case "G09 秒数 -1 拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime -1
  st_guard_case "G10 秒数前带空格拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime " 60"
  st_guard_case "G11 秒数后带空格拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime "60 "
  st_guard_case "G12 秒数前缀带字母（x60）拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime x60
  st_guard_case "G13 秒数带正号（+60）拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime +60
  st_guard_case "G14 秒数带小数（60.5）拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime 60.5
  st_guard_case "G15 秒数写成十六进制（0x3c）拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime 0x3c
  st_guard_case "G16 秒数后带换行拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime $'60\n'
  st_guard_case "G17 秒数 059（前导零，按十进制 59）拒绝" mincl --live --check-guards --prereg "$pre_ok" --mincleartime 059
  st_guard_case "G18 秒数 060（前导零，按十进制 60）放行" ok --live --check-guards --prereg "$pre_ok" --mincleartime 060
  st_guard_case "G19 秒数 3600 放行" ok --live --check-guards --prereg "$pre_ok" --mincleartime 3600

  # ---- --prereg 的每种情形各自一条（秒数合格）----
  st_guard_case "G20 没提供 --prereg 拒绝" prereg --live --check-guards --mincleartime 60
  st_guard_case "G21 --prereg 为空串拒绝" prereg --live --check-guards --prereg "" --mincleartime 60
  st_guard_case "G22 --prereg 指向不存在的文件拒绝" prereg --live --check-guards --prereg "$pre_missing" --mincleartime 60
  st_guard_case "G23 --prereg 指向存在但为空的文件拒绝" prereg --live --check-guards --prereg "$pre_empty" --mincleartime 60
  st_guard_case "G24 --prereg 指向目录拒绝（目录不是预注册文件）" prereg --live --check-guards --prereg "$pre_dir" --mincleartime 60
  st_guard_case "G25 --prereg 指向非空文件放行" ok --live --check-guards --prereg "$pre_ok" --mincleartime 60
  st_guard_case "G26 --prereg 指向恰 1 字节的文件放行" ok --live --check-guards --prereg "$pre_one" --mincleartime 60

  # ---- 两项都不合格：只报先判的预注册这一行 ----
  st_guard_case "G27 预注册不存在且秒数 59：只报预注册" prereg --live --check-guards --prereg "$pre_missing" --mincleartime 59

  # ---- 入口本身：参数顺序、与 --live 同用、缺值 ----
  st_guard_case "G30 --check-guards 写在 --live 之前，秒数 59 同样拒绝" mincl --check-guards --live --prereg "$pre_ok" --mincleartime 59
  st_guard_case "G31 --check-guards 写在 --live 之前，合格同样放行" ok --check-guards --live --prereg "$pre_ok" --mincleartime 60
  st_guard_case "G32 --check-guards 不带 --live 拒绝（不得悄悄放行）" needlive --check-guards --prereg "$pre_ok" --mincleartime 60
  st_guard_case "G33 --dry 与 --check-guards 同用拒绝" needlive --dry --check-guards --prereg "$pre_ok" --mincleartime 60
  st_guard_case "G34 --live 之后又写 --dry（最后一个模式生效）拒绝" needlive --live --dry --check-guards --prereg "$pre_ok" --mincleartime 60
  st_guard_case "G35 --prereg 缺值" noarg_prereg --live --check-guards --prereg
  st_guard_case "G36 --mincleartime 缺值" noarg_mincl --live --check-guards --prereg "$pre_ok" --mincleartime

  # ---- 其余选项的参数解析：解析循环先于护栏判定，缺值与未知参数在护栏之前就退出 2 ----
  # q 是一组合格的 --live 护栏参数；每条用例在它后面接要测的选项，"解析不过"与"护栏不过"一眼分得开：
  # 解析不过 → 退出 2 且 stderr 是解析阶段的固定提示；解析过了 → 走到护栏、打印固定一行（ok）。
  local q=(--live --check-guards --prereg "$pre_ok" --mincleartime 60)

  # 缺值：每个带参数的选项各一条，选项写在最后、后面一个词都没有（--dungeon 要三个词：完全缺值、只给编号各一条）
  st_guard_case "Q01 --settle 缺值" noarg_settle "${q[@]}" --settle
  st_guard_case "Q02 --char 缺值" noarg_char "${q[@]}" --char
  st_guard_case "Q03 --dungeon 缺值" noarg_dungeon "${q[@]}" --dungeon
  st_guard_case "Q04 --dungeon 只给编号、缺模式" noarg_dungeon "${q[@]}" --dungeon 3
  st_guard_case "Q05 --max-seconds 缺值" noarg_maxsec "${q[@]}" --max-seconds
  st_guard_case "Q06 --netroot 缺值" noarg_netroot "${q[@]}" --netroot
  st_guard_case "Q07 --rooms 缺值" noarg_rooms "${q[@]}" --rooms
  st_guard_case "Q08 --monsters 缺值" noarg_monsters "${q[@]}" --monsters

  # 值恰好是最后一个（--dungeon 是最后三个）词：不能因为"后面没东西了"就判缺值，也不能多吃或少吃
  st_guard_case "Q11 --settle 带值放在最后：放行" ok "${q[@]}" --settle retry,select,town
  st_guard_case "Q12 --char 带值放在最后：放行" ok "${q[@]}" --char 2
  st_guard_case "Q13 --dungeon 带编号与模式放在最后（三个词恰好用完）：放行" ok "${q[@]}" --dungeon 3 2
  st_guard_case "Q14 --max-seconds 带值放在最后：放行" ok "${q[@]}" --max-seconds 90
  st_guard_case "Q15 --netroot 带值放在最后：放行" ok "${q[@]}" --netroot "$tmp/netroot"
  st_guard_case "Q16 --rooms 带值放在最后：放行" ok "${q[@]}" --rooms 2
  st_guard_case "Q17 --monsters 带值放在最后：放行" ok "${q[@]}" --monsters 3
  # 每个选项后面都还有别的选项：各吃几个词、吃完正好接上下一个选项（--damage 不带值）
  st_guard_case "Q18 七个带值选项连写、--damage 收尾：放行" ok "${q[@]}" --settle retry,select,town --char 1 --dungeon 3 2 --max-seconds 90 --netroot "$tmp/netroot" --rooms 2 --monsters 3 --damage
  st_guard_case "Q19 --damage 夹在中间、也放在最后：放行" ok "${q[@]}" --damage --rooms 2 --damage

  # 未知参数：退出 2、stdout 空；stderr 首行恰为"未知参数: <原样>"，其后整段是用法文本
  st_guard_case "Q20 未知参数 --bogus 放在合格参数之后" unknown=--bogus "${q[@]}" --bogus
  st_guard_case "Q21 未知参数放在最前面" unknown=bogus bogus "${q[@]}"
  st_guard_case "Q22 --选项=值 的写法不被接受，原样回显" unknown=--mincleartime=60 --live --check-guards --prereg "$pre_ok" --mincleartime=60
  st_guard_case "Q23 未知参数含空格与汉字，原样回显" "unknown=带 空格 的参数" "${q[@]}" "带 空格 的参数"
  st_guard_case "Q24 空字符串参数也是未知参数" unknown= "${q[@]}" ""
  st_guard_case "Q25 --dry 之后多出一个位置参数" unknown=extra --dry extra

  # 帮助：-h / --help 把用法文本打到 stdout 并退出 0（不是 stderr）；写在别的选项之后也是先帮助、不走护栏
  st_guard_case "Q30 -h 打印用法并退出 0" help -h
  st_guard_case "Q31 --help 打印用法并退出 0" help --help
  st_guard_case "Q32 --help 写在合格参数之后：照样是帮助，不走护栏" help "${q[@]}" --help

  # 解析结果落到正确的变量：--dry 走到假 env.sh，它把各变量原样记下来；每个选项用互不相同的合成值，串错变量就对不上
  st_parse_case "V01 不带任何选项：全是缺省值" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry
  st_parse_case "V02 --settle 只改 SETTLE_SPEC" \
    "SETTLE_SPEC=select,retry CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry --settle select,retry
  st_parse_case "V03 --char 只改 CHAR_SLOT" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=3 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry --char 3
  st_parse_case "V04 --dungeon 编号进 DUNGEON_ID、模式进 DUNGEON_MODE" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID=4 DUNGEON_MODE=1 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry --dungeon 4 1
  st_parse_case "V05 --max-seconds 只改 MAX_SECONDS" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=77 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry --max-seconds 77
  st_parse_case "V06 --netroot 只改 NET_ROOT_LIVE" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE=/synthetic/netroot FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry --netroot /synthetic/netroot
  st_parse_case "V07 --rooms 只改 FAKE_ROOMS" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=5 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" --dry --rooms 5
  st_parse_case "V08 --monsters 只改 FAKE_MONSTERS" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=9 EXTRA_NO_DAMAGE=1" --dry --monsters 9
  st_parse_case "V09 --damage 只把 EXTRA_NO_DAMAGE 置 0" \
    "SETTLE_SPEC=retry,town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=0" --dry --damage
  st_parse_case "V10 全部选项连写（与缺省顺序相反）：各归各的变量、互不吞词" \
    "SETTLE_SPEC=town CHAR_SLOT=5 DUNGEON_ID=2 DUNGEON_MODE=3 MAX_SECONDS=33 NET_ROOT_LIVE=/synthetic/nr2 FAKE_ROOMS=6 FAKE_MONSTERS=11 EXTRA_NO_DAMAGE=0" \
    --dry --damage --monsters 11 --rooms 6 --netroot /synthetic/nr2 --max-seconds 33 --dungeon 2 3 --char 5 --settle town
  st_parse_case "V11 同一选项写两遍：后面的覆盖前面的" \
    "SETTLE_SPEC=town CHAR_SLOT=0 DUNGEON_ID=2 DUNGEON_MODE=2 MAX_SECONDS=120 NET_ROOT_LIVE= FAKE_ROOMS=7 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" \
    --dry --rooms 2 --dungeon 1 1 --settle select --rooms 7 --dungeon 2 2 --settle town
  st_parse_case "V12 值里的空格与汉字原样保留（不拆词、不裁边）" \
    "SETTLE_SPEC=retry, town CHAR_SLOT=0 DUNGEON_ID= DUNGEON_MODE=0 MAX_SECONDS=120 NET_ROOT_LIVE=/synthetic/带 空格 目录 FAKE_ROOMS=1 FAKE_MONSTERS=2 EXTRA_NO_DAMAGE=1" \
    --dry --settle "retry, town" --netroot "/synthetic/带 空格 目录"

  # ---- 沙箱放行对照：不带 --check-guards 时，护栏照样挡；放行后确实进入下一步（沙箱里的假 env.sh，退出 97）----
  st_box_case "H01 --live 合格（不带 --check-guards）放行后进入下一步，MODE 与秒数原样带过去" 97 "" "MODE=live MIN_CLEAR_SECONDS=60" \
    --live --prereg "$pre_ok" --mincleartime 60
  st_box_case "H02 --live 秒数 59（不带 --check-guards）同样在护栏处退出 2，没进下一步" 2 \
    "--live 拒绝启动：必须显式提供 --mincleartime >= 60（防 E-414 通关过快反作弊计数）" "" \
    --live --prereg "$pre_ok" --mincleartime 59
  st_box_case "H03 --live 没有预注册（不带 --check-guards）同样在护栏处退出 2，没进下一步" 2 \
    "--live 拒绝启动：必须提供存在的非空 --prereg 预注册文件" "" \
    --live --mincleartime 60
  st_box_case "H04 --dry 放行后进入下一步，秒数缺省为 0" 97 "" "MODE=dry MIN_CLEAR_SECONDS=0" --dry
  st_box_case "H05 --dry 显式秒数 7 原样保留" 97 "" "MODE=dry MIN_CLEAR_SECONDS=7" --dry --mincleartime 7

  # ======================= 三、失败路径负对照（自测自己证明"失败时真的会失败"）=======================
  if [ -n "${E2E_SELFTEST_NESTED:-}" ]; then
    echo "[selftest] 负对照: 本次是嵌套运行（环境变量 E2E_SELFTEST_NESTED 已设置，由外层 --selftest 启动），不再递归；手动运行时不要设置它"
  else
    st_negative_control
  fi

  echo "[selftest] 共 ${_ST_LOG_CASES} 条校验器对照、${_ST_GUARD_CASES} 条护栏对照"
  echo "[selftest] ALL PASSED"
}

if [ "$MODE" = "selftest" ]; then
  run_selftest
  exit 0
fi

if [ "$MODE" = "live" ]; then
  validate_live_guards || exit 2
else
  if [ -z "$MIN_CLEAR_SECONDS" ]; then
    MIN_CLEAR_SECONDS="0"
  fi
fi

# --check-guards：护栏判定到此为止。通过就打印固定一行并退出 0；以下任何一步（env.sh、DNF_DIR、构建、播放器、凭据文件）一概不碰。
if [ "$GUARD_ONLY" = "1" ]; then
  if [ "$MODE" != "live" ]; then
    echo "--check-guards 必须与 --live 同用" >&2
    exit 2
  fi
  echo "DNF_E2E_GUARDS ok"
  exit 0
fi

. "$REPO_ROOT/tools/env.sh"

# 解析本地 DNF_DIR（含 Script.pvf），不打印路径内容以外的机密
if [ -z "${DNF_DIR:-}" ] && [ -f "$REPO_ROOT/local.env" ]; then
  DNF_DIR="$(python3 - "$REPO_ROOT/local.env" <<'PY'
import sys
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    s = line.strip()
    if s.startswith("DNF_DIR="):
        v = s.split("=", 1)[1].strip().strip('"').strip("'")
        print(v)
        break
PY
)"
fi
if [ -z "${DNF_DIR:-}" ] || [ ! -f "$DNF_DIR/Script.pvf" ]; then
  echo "缺少有效的 DNF_DIR（需含 Script.pvf）" >&2
  exit 2
fi

# 复用 tools/unity-build-town.sh macos-dev（只调用，不 source、不复制）
APP_BUNDLE="$REPO_ROOT/unity/DnfUnity/Builds/macos/DnfTownDemo.app"
if [ "${DNF_SKIP_BUILD:-}" = "1" ] && [ -d "$APP_BUNDLE" ]; then
  echo "DNF_SKIP_BUILD=1: 复用已构建的 macos-dev 播放器"
else
  set +e
  bash "$REPO_ROOT/tools/unity-build-town.sh" macos-dev
  build_rc=$?
  set -e
  if [ "$build_rc" -eq 198 ]; then
    echo "Unity 授权过期（退出码 198），停止执行并报告集成者" >&2
    exit 198
  fi
  [ "$build_rc" -eq 0 ] || exit "$build_rc"
fi

APP_BIN="$(ls -d "$APP_BUNDLE"/Contents/MacOS/* 2>/dev/null | head -1 || true)"
[ -x "$APP_BIN" ] || { echo "找不到可执行播放器: $APP_BUNDLE" >&2; exit 2; }

mkdir -p "$REPO_ROOT/re"
E2E_LOG="$REPO_ROOT/re/unity-online-e2e-${MODE}-macos.log"
rm -f "$E2E_LOG"

EXPECT_RETRIES="$(python3 - "$SETTLE_SPEC" <<'PY'
import sys
parts = [p.strip().lower() for p in sys.argv[1].split(",") if p.strip()]
print(sum(1 for p in parts if p == "retry"))
PY
)"

TMP_DIR=""
FAKE_PID=""
cleanup() {
  if [ -n "$FAKE_PID" ] && kill -0 "$FAKE_PID" 2>/dev/null; then
    kill "$FAKE_PID" 2>/dev/null || true
    wait "$FAKE_PID" 2>/dev/null || true
  fi
  if [ -n "$TMP_DIR" ] && [ -d "$TMP_DIR" ]; then
    rm -rf "$TMP_DIR"
  fi
}
trap cleanup EXIT INT TERM

PLAYER_ARGS=(
  -batchmode
  -nographics
  -logFile "$E2E_LOG"
  -dnfdir "$DNF_DIR"
  -dnfonline
  -dnfserverui
  -dnfonlineauto
  -dnfonlineauto-char "$CHAR_SLOT"
  -dnfonlineauto-settle "$SETTLE_SPEC"
  -dnfonlineauto-mincleartime "$MIN_CLEAR_SECONDS"
  -dnfquitafter "$MAX_SECONDS"
)
if [ -n "$DUNGEON_ID" ]; then
  PLAYER_ARGS+=(-dnfonlineauto-dungeon "$DUNGEON_ID" "$DUNGEON_MODE")
fi
if [ "$EXTRA_NO_DAMAGE" = "1" ]; then
  PLAYER_ARGS+=(-dnfnodamage)
fi

if [ "$MODE" = "dry" ]; then
  dotnet build "$REPO_ROOT/src/Dnf.NetFake/Dnf.NetFake.csproj" -c Release -nologo -v q -p:NuGetAudit=false >/dev/null
  TMP_DIR="$(mktemp -d)"
  READY_FILE="$TMP_DIR/ready.env"
  FAKE_LOG="$TMP_DIR/netfake.log"
  FAKE_ARGS=(
    "$REPO_ROOT/src/Dnf.NetFake/bin/Release/net10.0/dnf-netfake.dll"
    --ready-file "$READY_FILE"
    --write-dev-root "$TMP_DIR/devroot"
    --rooms "$FAKE_ROOMS"
    --monsters "$FAKE_MONSTERS"
  )
  if [ -n "$DUNGEON_ID" ]; then
    FAKE_ARGS+=(--dungeon-id "$DUNGEON_ID")
  fi
  dotnet "${FAKE_ARGS[@]}" >"$FAKE_LOG" 2>&1 &
  FAKE_PID="$!"

  # 条件轮询等待假服写好 ready 文件（上限 10 秒）
  ready_ok=0
  for _ in $(seq 1 100); do
    if [ -s "$READY_FILE" ]; then
      ready_ok=1
      break
    fi
    if ! kill -0 "$FAKE_PID" 2>/dev/null; then
      echo "dnf-netfake 启动即退出" >&2
      exit 1
    fi
    sleep 0.1
  done
  [ "$ready_ok" -eq 1 ] || { echo "等待 dnf-netfake 就绪超时" >&2; exit 1; }

  # 进程内读取端口，绝不写入日志或 stdout
  CHAN_PORT="$(grep '^channel_port=' "$READY_FILE" | cut -d= -f2)"
  DIR_PORT="$(grep '^dir_port=' "$READY_FILE" | cut -d= -f2)"
  [ -n "$CHAN_PORT" ] && [ -n "$DIR_PORT" ] || { echo "dnf-netfake ready 文件缺少端口字段" >&2; exit 1; }

  PLAYER_ARGS+=(-dnfnetroot "$TMP_DIR/devroot" -dnfnetport "$CHAN_PORT" -dnfdirport "$DIR_PORT")
else
  LIVE_ROOT="${NET_ROOT_LIVE:-$REPO_ROOT}"
  [ -f "$LIVE_ROOT/local.env" ] && [ -f "$LIVE_ROOT/local.secrets.env" ] \
    || { echo "--live 缺少本地开发配置根下的凭据文件" >&2; exit 2; }
  PLAYER_ARGS+=(-dnfnetroot "$LIVE_ROOT")
fi

START_TS="$(python3 -c 'import time; print(int(time.monotonic()*1000))')"
set +e
"$APP_BIN" "${PLAYER_ARGS[@]}"
PLAYER_RC=$?
set -e
END_TS="$(python3 -c 'import time; print(int(time.monotonic()*1000))')"
WALL_MS=$((END_TS - START_TS))

echo "online-e2e (${MODE}) 播放器退出码=$PLAYER_RC 墙钟=${WALL_MS}ms"
grep -E '^DNF_ONLINE ' "$E2E_LOG" || true

if [ "$PLAYER_RC" -eq 198 ]; then
  echo "Unity 授权过期（退出码 198），停止执行并报告集成者" >&2
  exit 198
fi
if [ "$PLAYER_RC" -ne 0 ]; then
  echo "播放器返回非 0 退出码 ($PLAYER_RC)，详见日志: $E2E_LOG" >&2
  exit "$PLAYER_RC"
fi

MAX_MS=$((MAX_SECONDS * 1000))
validate_log "$E2E_LOG" "$MAX_MS" "$EXPECT_RETRIES" 1
