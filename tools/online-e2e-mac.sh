#!/usr/bin/env bash
# r20-e2e 联网全流程 macOS 无人值守验收脚本：
#   默认 --dry：启动本地合成回环假服务端 (src/Dnf.NetFake) 并驱动 macos-dev 播放器走完
#   登录 → 选服务器 → 选频道 → 选角 → 进城 → 选副本 → 进图 → 自动机清怪 → 结算 → 再来一次 → 回城，
#   严格校验 DNF_ONLINE 阶段序列、零 fail、结局 ok 与耗时上限；全程不连任何外部网络，不回显凭据/地址/端口。
#   --live：必须显式提供 --prereg <非空预注册文件> 且 --mincleartime >= 60，单次登录失败即停、绝不自动重试。
#   --selftest：对日志校验器（含正例与多种负对照）及 --live 安全门禁做离线自测，不需要 Unity。
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

MODE="dry"
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
  --selftest                    离线自测日志断言器（正例 + 负对照）与 --live 安全门禁。

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
  if [ -z "$PREREG_FILE" ] || [ ! -s "$PREREG_FILE" ]; then
    echo "--live 拒绝启动：必须提供存在的非空 --prereg 预注册文件" >&2
    return 1
  fi
  if [ -z "$MIN_CLEAR_SECONDS" ] || ! [[ "$MIN_CLEAR_SECONDS" =~ ^[0-9]+$ ]] || [ "$MIN_CLEAR_SECONDS" -lt 60 ]; then
    echo "--live 拒绝启动：必须显式提供 --mincleartime >= 60（防 E-414 通关过快反作弊计数）" >&2
    return 1
  fi
  return 0
}

run_selftest() {
  _SELFTEST_TMP="$(mktemp -d)"
  trap 'rm -rf "${_SELFTEST_TMP:-}"' EXIT
  local tmp="$_SELFTEST_TMP"

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

  echo "[selftest] 负对照 6: --live 缺预注册 / 缺 mincleartime>=60 -> 期望失败"
  if "$0" --live --mincleartime 60 >/dev/null 2>&1; then
    echo "FAIL: --live 缺 --prereg 未被拦截" >&2; exit 1
  fi
  : >"$tmp/empty.prereg"
  if "$0" --live --prereg "$tmp/empty.prereg" --mincleartime 60 >/dev/null 2>&1; then
    echo "FAIL: --live 空 --prereg 未被拦截" >&2; exit 1
  fi
  echo "prereg-ok" >"$tmp/valid.prereg"
  if "$0" --live --prereg "$tmp/valid.prereg" --mincleartime 59 >/dev/null 2>&1; then
    echo "FAIL: --live mincleartime<60 未被拦截" >&2; exit 1
  fi

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
