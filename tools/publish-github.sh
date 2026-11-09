#!/usr/bin/env bash
# 脱敏后把本地 main 发布到 GitHub(公开仓库)。不要直接 git push。
#
# 为什么要多这一步:
#   历史里有若干个旧提交的 tools/baseline/tool.sh,示例命令里写着真实的游戏账号名(只是名字,不是口令;
#   当前版本早已改成 example)。公开仓库一旦推上去就收不回来,所以先在临时克隆里把这几个旧版本里的取值
#   替换成 example,再推送临时克隆。本地仓库、其它分支与 worktree 一概不动。
#
# 做了什么:
#   1. 在临时目录克隆 main(只含 main 的历史)。
#   2. 脱敏前先扫一遍全部历史(口径与 tools/guard/precommit.py 相同:local.secrets.env 里的取值,
#      长度 8 以上按子串、3 到 7 按独立单词),命中只允许出现在 SCRUB_FILES 里;别处有命中就中止。
#      扫描器自带哨兵自检,防止它哑掉后静默返回"没有命中"。
#   3. 用 git filter-branch --index-filter 只改写 SCRUB_FILES 里的文件;其余内容、作者、时间、提交信息逐字不变。
#      重写是确定性的:同样的输入永远得到同样的提交号,所以以后每次发布都是对远端的快进。
#   4. 脱敏后再扫一遍(必须零命中),并核对:最新树哈希与本地一致、提交数一致。
#   5. 对照远端:远端 main 必须是本次结果的祖先,否则中止;绝不强推。
#   6. 推送(--dry-run 时在第 5 步后停止)。
#
# 用法:
#   tools/publish-github.sh [--dry-run] [--ssh] [--url 地址] [--twice] [--keep]
#     --dry-run   只做脱敏与全部校验,不推送
#     --ssh       用 git@github.com:WKkCoder/DofUnity.git 代替 https 地址
#     --url 地址   发布到别的远端(例如本地裸仓库,用来演练)
#     --twice     把重写做两遍并比对提交号,验证确定性
#     --keep      保留临时目录(排错用)
#   需要本机有 local.secrets.env(默认在仓库根;可用环境变量 DNF_SECRETS_FILE 指定)。
#   推送需要 GitHub 授权:在终端里运行时 git 会自己提示(https 用个人访问令牌当密码;或改用 --ssh 并先把公钥登记到 GitHub)。
set -euo pipefail

DEFAULT_URL="https://github.com/WKkCoder/DofUnity.git"
SSH_URL="git@github.com:WKkCoder/DofUnity.git"
BRANCH="main"
SCRUB_FILES="tools/baseline/tool.sh"   # 历史里允许被改写的文件(空格分隔)
PLACEHOLDER="example"

die() { echo "错误:$*" >&2; exit 1; }
usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

DRY=0; TWICE=0; KEEP=0; URL="$DEFAULT_URL"
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY=1 ;;
    --ssh) URL="$SSH_URL" ;;
    --url) shift; [ $# -gt 0 ] || die "--url 需要一个地址"; URL="$1" ;;
    --twice) TWICE=1 ;;
    --keep) KEEP=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数:$1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

ROOT="$(git rev-parse --show-toplevel)"
SECRETS="${DNF_SECRETS_FILE:-$ROOT/local.secrets.env}"
[ -r "$SECRETS" ] || die "读不到 $SECRETS。没有它就无法校验历史里有没有凭据,拒绝发布(可用 DNF_SECRETS_FILE 指定位置)。"
git -C "$ROOT" rev-parse --verify -q "refs/heads/$BRANCH" >/dev/null || die "本地没有 $BRANCH 分支"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/dnf-publish.XXXXXX")"
cleanup() { if [ "$KEEP" = 1 ]; then echo "已保留临时目录:$TMP"; else rm -rf "$TMP"; fi; }
trap cleanup EXIT

# ---- 辅助脚本:脱敏与扫描共用同一套取值解析(与提交守卫同口径) ----
cat > "$TMP/scrub.py" <<'PY'
import os, re, subprocess, sys

def load_patterns():
    pats = []
    with open(os.environ["DNF_SECRETS_FILE"], encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            v = line.split("=", 1)[1].strip().strip("'\"")
            if len(v) >= 8:
                pats.append(re.compile(re.escape(v)))
            elif len(v) >= 3:
                pats.append(re.compile(r"(?<![A-Za-z0-9_])" + re.escape(v) + r"(?![A-Za-z0-9_])"))
    return pats

def scrub():
    data = sys.stdin.buffer.read()
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        sys.stdout.buffer.write(data)
        return
    for p in load_patterns():
        text = p.sub(os.environ.get("PLACEHOLDER", "example"), text)
    sys.stdout.buffer.write(text.encode("utf-8"))

SENTINEL = re.compile(r"com\.dnfunity")   # 历史里必然存在的字串:扫描器自检用

def scan(branch, allowed):
    """退出码:0 无命中;10 命中只在允许的路径;1 有越界命中;3 扫描器自身有问题。只打印路径与短哈希,不打印取值。"""
    pats = load_patterns()
    if not pats:
        print("没有从取值文件里读到任何取值,无法校验。", file=sys.stderr)
        return 3
    listing = subprocess.run(["git", "rev-list", "--objects", branch], capture_output=True, check=True).stdout.decode("utf-8", "replace")
    objs = []
    for ln in listing.splitlines():
        sha, _, path = ln.partition(" ")
        objs.append((sha, path))
    HIDDEN = "(路径里含有取值,已隐藏)"   # 路径本身含取值时不能原样打印,否则报告自己就泄露了
    def shown(path):
        return HIDDEN if any(p.search(path) for p in pats) else path
    hits = {}
    for sha, path in objs:
        if path and any(p.search(path) for p in pats):
            hits[(sha[:7], HIDDEN)] = 1
    sentinel_seen = False
    blobs = 0
    for i in range(0, len(objs), 400):
        chunk = objs[i:i + 400]
        res = subprocess.run(["git", "cat-file", "--batch"], input=("\n".join(s for s, _ in chunk) + "\n").encode(),
                             capture_output=True, check=True).stdout
        pos = 0
        for sha, path in chunk:
            nl = res.index(b"\n", pos)
            hdr = res[pos:nl].split()
            if len(hdr) != 3:
                print("cat-file 返回异常:%r" % res[pos:nl], file=sys.stderr)
                return 3
            typ, size = hdr[1].decode(), int(hdr[2])
            body = res[nl + 1:nl + 1 + size]
            pos = nl + 1 + size + 1
            if typ != "blob":
                continue
            blobs += 1
            try:
                text = body.decode("utf-8")
            except UnicodeDecodeError:
                text = body.decode("latin-1")   # 取值都是 ASCII,latin-1 解码不丢信息
            if SENTINEL.search(text):
                sentinel_seen = True
            if any(p.search(text) for p in pats):
                hits[(sha[:7], shown(path))] = 1
    msgs = subprocess.run(["git", "log", branch, "--format=%H%x00%an%x00%ae%x00%cn%x00%ce%x00%B%x01"],
                          capture_output=True, check=True).stdout.decode("utf-8", "replace")
    for rec in msgs.split("\x01"):
        if rec.strip() and any(p.search(rec) for p in pats):
            hits[(rec.strip().split("\x00", 1)[0][:7], "(提交信息或作者字段)")] = 1
    if not sentinel_seen:
        print("扫描器自检失败:在 %d 个 blob 里连哨兵字串都没找到,扫描结果不可信。" % blobs, file=sys.stderr)
        return 3
    print("  扫描了 %d 个对象(其中 blob %d 个),取值 %d 个,哨兵自检通过" % (len(objs), blobs, len(pats)))
    if not hits:
        print("  结果:零命中")
        return 0
    bad = [h for h in sorted(hits) if h[1] not in allowed]
    for sha7, path in sorted(hits):
        print("  命中:%s(对象 %s)%s" % (path, sha7, "" if (sha7, path) not in bad else "  <-- 不在允许改写的范围内"))
    return 1 if bad else 10

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "scrub":
        scrub()
    elif mode == "scan":
        sys.exit(scan(sys.argv[2], set(sys.argv[3:])))
PY

export DNF_SECRETS_FILE="$SECRETS" PLACEHOLDER SCRUB_FILES SCRUB_PY="$TMP/scrub.py"

scan_in() {  # $1=克隆目录;其余参数=允许命中的路径。返回扫描器退出码
  local dir="$1"; shift
  local rc=0
  ( cd "$dir" && python3 "$TMP/scrub.py" scan "$BRANCH" "$@" ) || rc=$?
  return "$rc"
}

clone_into() { git clone -q --no-local --single-branch --branch "$BRANCH" --no-tags "$ROOT" "$1"; }

rewrite_in() {  # $1=克隆目录:就地重写 main
  local dir="$1"
  ( cd "$dir" && FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f -d "$dir.fb" --index-filter '
      for f in $SCRUB_FILES; do
        ent=$(git ls-files -s -- "$f")
        [ -n "$ent" ] || continue
        mode=${ent%% *}; rest=${ent#* }; old=${rest%% *}
        new=$(git cat-file blob "$old" | python3 "$SCRUB_PY" scrub | git hash-object -w --stdin)
        [ "$new" = "$old" ] || git update-index --cacheinfo "$mode,$new,$f"
      done
    ' -- "$BRANCH" >"$dir.log" 2>&1 ) || { cat "$dir.log" >&2; die "git filter-branch 失败"; }
}

echo "== 1/5 克隆 $BRANCH 到临时目录 =="
clone_into "$TMP/repo"

echo "== 2/5 脱敏前扫描全部历史(命中只允许出现在:$SCRUB_FILES) =="
rc=0; scan_in "$TMP/repo" $SCRUB_FILES || rc=$?
case "$rc" in
  0)  echo "  历史本来就是干净的,不需要改写。" ; NEED_REWRITE=0 ;;
  10) NEED_REWRITE=1 ;;
  1)  die "历史里有越界命中(见上),不在允许改写的文件里。请先处理,本脚本不会带着它发布。" ;;
  *)  die "扫描器出错(退出码 $rc),拒绝发布。" ;;
esac

if [ "$NEED_REWRITE" = 1 ]; then
  echo "== 3/5 重写历史(只改 $SCRUB_FILES,取值替换为 $PLACEHOLDER) =="
  rewrite_in "$TMP/repo"
  if [ "$TWICE" = 1 ]; then
    echo "  --twice:从头再做一遍,比对提交号以验证确定性"
    clone_into "$TMP/repo2"; rewrite_in "$TMP/repo2"
    a=$(git -C "$TMP/repo" rev-parse "$BRANCH"); b=$(git -C "$TMP/repo2" rev-parse "$BRANCH")
    [ "$a" = "$b" ] || die "两次重写的结果不同($a 与 $b),重写不是确定性的,不能用来增量发布。"
    echo "  两次结果一致:$a"
  fi
else
  echo "== 3/5 无需重写 =="
fi

echo "== 4/5 校验 =="
echo "  脱敏后再扫一遍:"
rc=0; scan_in "$TMP/repo" || rc=$?
[ "$rc" = 0 ] || die "脱敏后仍有命中或扫描器出错(退出码 $rc),拒绝发布。"
lt=$(git -C "$ROOT" rev-parse "$BRANCH^{tree}"); nt=$(git -C "$TMP/repo" rev-parse "$BRANCH^{tree}")
[ "$lt" = "$nt" ] || die "最新树哈希与本地不一致($lt 与 $nt):脱敏不应改变最新版本的内容。"
lc=$(git -C "$ROOT" rev-list --count "$BRANCH"); nc=$(git -C "$TMP/repo" rev-list --count "$BRANCH")
[ "$lc" = "$nc" ] || die "提交数不一致(本地 $lc,脱敏后 $nc)。"
same=$(comm -12 <(git -C "$ROOT" rev-list "$BRANCH" | sort) <(git -C "$TMP/repo" rev-list "$BRANCH" | sort) | wc -l | tr -d ' ')
TIP="$(git -C "$TMP/repo" rev-parse "$BRANCH")"
echo "  最新树哈希与本地一致:$lt"
echo "  提交数一致:$nc 个;其中 $same 个提交号与本地相同,$((nc - same)) 个因祖先被改写而换号"
echo "  待发布的最新提交:$TIP(本地是 $(git -C "$ROOT" rev-parse "$BRANCH"))"

echo "== 5/5 对照远端 $URL =="
REMOTE_TIP="$(git ls-remote "$URL" "refs/heads/$BRANCH" 2>/dev/null | cut -f1 || true)"
if [ -z "$REMOTE_TIP" ]; then
  echo "  远端没有 $BRANCH(空仓库,或暂时连不上):将创建它。"
else
  git -C "$TMP/repo" fetch -q "$URL" "refs/heads/$BRANCH" || die "拉取远端 $BRANCH 失败"
  if [ "$REMOTE_TIP" = "$TIP" ]; then
    echo "  远端已经是最新($TIP),无需推送。"; exit 0
  fi
  git -C "$TMP/repo" merge-base --is-ancestor "$REMOTE_TIP" "$TIP" \
    || die "远端 $BRANCH($REMOTE_TIP)不是本次结果的祖先:远端有本地没有的提交,或历史已不兼容。为安全起见不强推,请先人工核对。"
  echo "  远端 $BRANCH 是本次结果的祖先,可以快进。"
fi

if [ "$DRY" = 1 ]; then
  echo "== --dry-run:到此为止,没有推送任何东西 =="
  exit 0
fi

echo "== 推送 $BRANCH(不强推) =="
git -C "$TMP/repo" push "$URL" "$BRANCH:refs/heads/$BRANCH"
echo "已发布:$URL  $BRANCH @ $TIP"
echo "本地仓库没有任何改动。以后每次发布都重新运行本脚本,不要手敲 git push。"
