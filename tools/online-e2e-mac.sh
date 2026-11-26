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
#   1) 日志校验器 validate_log：正例与负对照（本提交不改动）。
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
#   needlive、noarg_prereg、noarg_mincl 用法错误（退出 2、stderr 恰为固定一行）。任何类别都不许触发沙箱里的后续步骤。
st_guard_case() {
  local name="$1" kind="$2"
  shift 2
  local want_rc want_out="" want_err=""
  case "$kind" in
    ok)           want_rc=0; want_out="DNF_E2E_GUARDS ok" ;;
    prereg)       want_rc=2; want_err="--live 拒绝启动：必须提供存在的非空 --prereg 预注册文件" ;;
    mincl)        want_rc=2; want_err="--live 拒绝启动：必须显式提供 --mincleartime >= 60（防 E-414 通关过快反作弊计数）" ;;
    needlive)     want_rc=2; want_err="--check-guards 必须与 --live 同用" ;;
    noarg_prereg) want_rc=2; want_err="缺少 --prereg 参数" ;;
    noarg_mincl)  want_rc=2; want_err="缺少 --mincleartime 参数" ;;
    *) st_fail "[护栏 $name] 用例表里的期望类别未知: $kind" ;;
  esac
  st_box_run "$@"
  _ST_GUARD_CASES=$((_ST_GUARD_CASES + 1))
  echo "[selftest] 护栏 $name"
  if [ "$_ST_RC" != "$want_rc" ]; then
    st_fail "[护栏 $name] 退出码应为 $want_rc，实际 $_ST_RC" "stdout: [$(cat "$_ST_OUT")]" "stderr: [$(cat "$_ST_ERR")]"
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
    st_fail "[护栏 $name] 退出码应为 $want_rc，实际 $_ST_RC" "stdout: [$(cat "$_ST_OUT")]" "stderr: [$(cat "$_ST_ERR")]"
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

run_selftest() {
  _SELFTEST_TMP="$(mktemp -d)"
  trap 'rm -rf "${_SELFTEST_TMP:-}"' EXIT
  local tmp="$_SELFTEST_TMP"
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

  # ======================= 一、日志校验器（本提交不改，沿用原有的正例与负对照）=======================
  local good="$tmp/good.log"
  cat >"$good" <<'EOF'
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

  echo "[selftest] 正例: 完整日志 -> 期望退出 0"
  validate_log "$good" 60000 1 1 >/dev/null

  echo "[selftest] 负对照 1: 缺少 settle 阶段 -> 期望失败"
  grep -v 'stage=settle' "$good" >"$tmp/missing_settle.log"
  if validate_log "$tmp/missing_settle.log" 60000 0 1 >/dev/null 2>&1; then
    echo "FAIL: 缺阶段负对照未被拦截" >&2; exit 1
  fi

  echo "[selftest] 负对照 2: 多出一条 event=fail -> 期望失败"
  sed 's/stage=channel event=ok reason=connected/stage=channel event=ok reason=connected\nDNF_ONLINE stage=channel event=fail reason=network/' "$good" >"$tmp/extra_fail.log"
  if validate_log "$tmp/extra_fail.log" 60000 1 1 >/dev/null 2>&1; then
    echo "FAIL: 多 fail 负对照未被拦截" >&2; exit 1
  fi

  echo "[selftest] 负对照 3: 缺少结局行 -> 期望失败"
  grep -v '^DNF_ONLINE result=' "$good" >"$tmp/no_result.log"
  if validate_log "$tmp/no_result.log" 60000 1 1 >/dev/null 2>&1; then
    echo "FAIL: 缺结局行负对照未被拦截" >&2; exit 1
  fi

  echo "[selftest] 负对照 4: 阶段顺序颠倒 (dungeon 早于 town) -> 期望失败"
  python3 - "$good" "$tmp/bad_order.log" <<'PY'
import sys
lines = open(sys.argv[1]).read().splitlines()
i_town = next(i for i, l in enumerate(lines) if "stage=town " in l)
i_dung = next(i for i, l in enumerate(lines) if "stage=dungeon " in l)
lines[i_town], lines[i_dung] = lines[i_dung], lines[i_town]
open(sys.argv[2], "w").write("\n".join(lines) + "\n")
PY
  if validate_log "$tmp/bad_order.log" 60000 1 1 >/dev/null 2>&1; then
    echo "FAIL: 阶段乱序负对照未被拦截" >&2; exit 1
  fi

  echo "[selftest] 负对照 5: 耗时超上限 -> 期望失败"
  if validate_log "$good" 5000 1 1 >/dev/null 2>&1; then
    echo "FAIL: 超时负对照未被拦截" >&2; exit 1
  fi


  # ======================= 二、--live 护栏（只经 --check-guards 入口，隔离沙箱副本）=======================
  st_make_box
  local pre_ok="$tmp/prereg.ok" pre_empty="$tmp/prereg.empty" pre_missing="$tmp/prereg.missing"
  local pre_dir="$tmp/prereg.dir" pre_one="$tmp/prereg.one"
  printf '%s\n' "prereg-synthetic" >"$pre_ok"
  : >"$pre_empty"
  mkdir "$pre_dir"
  printf '\n' >"$pre_one"

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

  echo "[selftest] 共 ${_ST_GUARD_CASES} 条护栏对照"
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
