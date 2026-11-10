#!/usr/bin/env python3
"""脱敏后把本地的全部分支与标签发布到 GitHub(公开仓库)。不要直接 git push,更不要 git push --all / --mirror。

为什么要多这一步:
  公开仓库一旦推上去就收不回来。历史里有若干个旧提交的 tools/baseline/tool.sh,示例命令里写着真实的游戏账号名
  (只是名字,不是口令;当前版本早已改成 example)。所以先在临时克隆里审计、脱敏、校验,最后只推送临时克隆。
  本地仓库、分支、worktree 与未提交的改动一概不动。

做了什么:
  1. 快照:在临时目录克隆 main,再把其余的本地分支(refs/heads/*)与标签(refs/tags/*)取进来。
     refs/original、refs/stash、远程跟踪分支与 worktree 里未提交的内容都不在范围内,永远不会发布。
  2. 审计(audit_refs.py):对每个引用的整段历史做检查——守卫的全部规则(原版容器与扩展名、体积、反编译器输出、
     local.secrets.env 里的取值及其十六进制/Base64/UTF-16/摘要等编码写法),再加本机路径、私钥与令牌、提交信息、
     引用名、子模块。凭据命中只允许出现在 SCRUB_FILES 里(会被脱敏);别处有命中的引用整个跳过,不发布,并说明原因。
     main 通不过则全部中止。
  3. 脱敏:git filter-branch --index-filter 只改写 SCRUB_FILES 里的文件(取值替换为 example),全部引用一起改写,
     其余内容、作者、时间、提交信息逐字不变。重写是确定性的:同样的输入永远得到同样的提交号,
     所以以后每次发布都是对远端的快进;本地与 GitHub 上的提交号因此不同,但最新树哈希相同。
  4. 校验:脱敏后重新审计,必须全部 PASS;每个引用的最新树哈希与提交数必须与脱敏前一致。
  5. 对照远端:新引用直接创建;远端是本次结果的祖先则快进;不是(例如本地分支被 rebase 过)则跳过并报告。
     main 与标签永远不强推;加 --force-branches 才会用 --force-with-lease 覆盖被改写过的普通分支。
     远端多出来的引用不会删除。
  6. 一次原子推送(--dry-run 时在第 5 步后停止),推完再核对远端。

用法:
  tools/publish-github.sh [--dry-run] [--ssh] [--url 地址] [--twice] [--keep] [--force-branches]
    --dry-run          只做审计、脱敏与全部校验,打印发布计划,不推送
    --ssh              用 git@github.com:WKkCoder/DofUnity.git 代替 https 地址
    --url 地址          发布到别的远端(例如本地裸仓库,用来演练)
    --twice            把重写做两遍并比对全部提交号,验证确定性
    --keep             保留临时目录(排错用)
    --force-branches   允许覆盖远端上被改写过的普通分支(main 与标签除外)
  需要本机有 local.secrets.env(环境变量 DNF_SECRETS_FILE 优先,其次当前工作区、主工作区)。
  推送需要 GitHub 授权:在终端里运行时 git 会自己提示(https 用个人访问令牌当密码;或改用 --ssh 并先把公钥登记到 GitHub)。
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from collections import OrderedDict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import audit_refs  # noqa: E402
import secretscan  # noqa: E402

DEFAULT_URL = "https://github.com/WKkCoder/DofUnity.git"
SSH_URL = "git@github.com:WKkCoder/DofUnity.git"
MAIN_REF = "refs/heads/main"
PLACEHOLDER = "example"
DEFAULT_SCRUB_FILES = ["tools/baseline/tool.sh"]   # 历史里允许被改写的文件
BATCH = 100                                         # 每次 fetch 的引用数

# git filter-branch 对每个提交运行的脚本:只改写 PUBLISH_SCRUB_FILES 里的文件
INDEX_FILTER = r'''
for f in $PUBLISH_SCRUB_FILES; do
  ent=$(git ls-files -s -- "$f")
  [ -n "$ent" ] || continue
  mode=${ent%% *}; rest=${ent#* }; old=${rest%% *}
  new=$(git cat-file blob "$old" | "$SCRUB_PYTHON" "$SCRUB_TOOL" scrub --placeholder "$PLACEHOLDER" | git hash-object -w --stdin)
  [ "$new" = "$old" ] || git update-index --cacheinfo "$mode,$new,$f"
done
'''


class Abort(Exception):
    pass


def say(msg=""):
    print(msg, flush=True)


def redact(text):
    """地址里万一带了 user:token@ 就遮掉,免得它出现在输出里。"""
    return re.sub(r"://[^/@\s]+@", "://***@", text)


def short(sha):
    return sha[:7] if sha else "-------"


def git(*args, cwd, check=True, env=None):
    p = subprocess.run(["git", *args], cwd=cwd, env=env, capture_output=True)
    if check and p.returncode != 0:
        raise Abort("git %s 失败:%s" % (args[0], redact(p.stderr.decode("utf-8", "replace").strip())[:600]))
    return p


def out(*args, cwd):
    return git(*args, cwd=cwd).stdout.decode("utf-8", "replace").strip()


def local_refs(repo):
    refs = OrderedDict()
    text = out("for-each-ref", "--format=%(refname)%09%(objectname)", "refs/heads", "refs/tags", cwd=repo)
    for line in text.splitlines():
        name, sha = line.split("\t")
        refs[name] = sha
    ordered = OrderedDict()
    if MAIN_REF in refs:
        ordered[MAIN_REF] = refs[MAIN_REF]
    for k, v in refs.items():
        ordered.setdefault(k, v)
    return ordered


def make_snapshot(root, dest, refs):
    """克隆 main,再取入 refs 里其余的引用。clone 与 fetch 之间源仓库若前进,不影响:以克隆里的状态为准。"""
    git("clone", "-q", "--no-local", "--single-branch", "--branch", "main", "--no-tags", root, dest, cwd=os.path.dirname(dest))
    git("remote", "remove", "origin", cwd=dest)
    others = [r for r in refs if r != MAIN_REF]
    for i in range(0, len(others), BATCH):
        git("fetch", "-q", "--no-tags", root, *["+%s:%s" % (r, r) for r in others[i:i + BATCH]], cwd=dest)


def tips_in(repo, refs):
    return OrderedDict((r, out("rev-parse", r, cwd=repo)) for r in refs)


def rewrite(clone, scrub_files, secrets_path):
    env = dict(os.environ, FILTER_BRANCH_SQUELCH_WARNING="1", DNF_SECRETS_FILE=secrets_path,
               PUBLISH_SCRUB_FILES=" ".join(scrub_files), SCRUB_PYTHON=sys.executable,
               SCRUB_TOOL=os.path.join(os.path.dirname(os.path.abspath(__file__)), "audit_refs.py"), PLACEHOLDER=PLACEHOLDER)
    p = subprocess.run(["git", "filter-branch", "-f", "-d", clone + ".fb", "--index-filter", INDEX_FILTER,
                        "--tag-name-filter", "cat", "--", "--branches", "--tags"],
                       cwd=clone, env=env, capture_output=True)
    if p.returncode != 0:
        raise Abort("git filter-branch 失败:" + (p.stderr + p.stdout).decode("utf-8", "replace").strip()[-800:])
    for ref in out("for-each-ref", "--format=%(refname)", "refs/original", cwd=clone).split():
        git("update-ref", "-d", ref, cwd=clone)   # filter-branch 留下的是脱敏前的备份,不能留着


def ls_remote(url, cwd):
    p = git("ls-remote", "--heads", "--tags", url, cwd=cwd, check=False)
    if p.returncode != 0:
        raise Abort("连不上远端 %s:%s" % (redact(url), redact(p.stderr.decode("utf-8", "replace").strip())[:400]))
    remote = {}
    for line in p.stdout.decode("utf-8", "replace").splitlines():
        sha, _, ref = line.partition("\t")
        if not ref.endswith("^{}"):
            remote[ref] = sha
    return remote


def run(args):
    top = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True)
    if top.returncode != 0:
        raise Abort("当前目录不在 git 仓库里")
    root = top.stdout.decode("utf-8").strip()
    url = SSH_URL if args.ssh else (args.url or DEFAULT_URL)
    scrub_files = args.scrub_file or [f for f in os.environ.get("PUBLISH_SCRUB_FILES", "").split() if f] or DEFAULT_SCRUB_FILES

    secrets_path = secretscan.find_secrets_file()
    if not secrets_path:
        raise Abort("找不到 local.secrets.env(找过:%s)。没有它就无法校验历史里有没有凭据,拒绝发布。"
                    % "、".join(secretscan.candidate_paths()))
    matchers = secretscan.load_matchers(secrets_path)
    if not matchers:
        raise Abort("%s 里没有长度 >= 3 的取值,无法校验,拒绝发布。" % secrets_path)

    refs = local_refs(root)
    if MAIN_REF not in refs:
        raise Abort("本地没有 main 分支")
    heads = [r for r in refs if r.startswith("refs/heads/")]
    tags = [r for r in refs if r.startswith("refs/tags/")]

    tmp = tempfile.mkdtemp(prefix="dnf-publish.")
    try:
        say("== 1/6 快照:%d 个分支、%d 个标签 → 临时目录 ==" % (len(heads), len(tags)))
        clone = os.path.join(tmp, "repo")
        make_snapshot(root, clone, list(refs))
        before = tips_in(clone, refs)
        trees_before = OrderedDict((r, out("rev-parse", r + "^{tree}", cwd=clone)) for r in before)
        counts_before = OrderedDict((r, int(out("rev-list", "--count", r, cwd=clone))) for r in before)

        say("== 2/6 审计全部历史(凭据命中只允许出现在:%s) ==" % "、".join(scrub_files))
        try:
            report = audit_refs.audit(clone, list(before), matchers, scrub_files)
        except audit_refs.AuditError as e:
            raise Abort("审计器出错,拒绝发布:%s" % e)
        report.print(say)
        skipped = report.refs_with(audit_refs.FAIL)
        if MAIN_REF in skipped:
            raise Abort("main 没有通过审计(见上),不发布任何东西。请先处理问题清单里的项目。")
        eligible = [r for r in before if r not in skipped]
        if skipped:
            say("  ⚠ 下面这 %d 个引用没通过审计,不会发布:" % len(skipped))
            for r in skipped:
                ids = report.verdicts[r][1]
                say("    %s  问题 %s" % (report.shown(r), "".join("[%d]" % i for i in ids)))
            for r in skipped:
                git("update-ref", "-d", r, cwd=clone)
        need_rewrite = any(report.status(r) == audit_refs.SCRUB for r in eligible)
        commits_before = set(out("rev-list", *eligible, cwd=clone).split())

        if need_rewrite:
            say("== 3/6 脱敏:全部引用一起改写(只改 %s,取值替换为 %s) ==" % ("、".join(scrub_files), PLACEHOLDER))
            rewrite(clone, scrub_files, secrets_path)
            if args.twice:
                say("  --twice:从头再做一遍,比对全部引用的提交号以验证确定性")
                clone2 = os.path.join(tmp, "repo2")
                make_snapshot(root, clone2, eligible)
                rewrite(clone2, scrub_files, secrets_path)
                a, b = tips_in(clone, eligible), tips_in(clone2, eligible)
                diff = [r for r in eligible if a[r] != b[r]]
                if diff:
                    raise Abort("两次重写的结果不同(%d 个引用,例如 %s),重写不是确定性的,不能用来增量发布。" % (len(diff), diff[0]))
                say("  两次结果一致(%d 个引用)" % len(eligible))
        else:
            say("== 3/6 历史里没有需要脱敏的内容,无需改写 ==")

        say("== 4/6 校验 ==")
        try:
            post = audit_refs.audit(clone, eligible, matchers, ())
        except audit_refs.AuditError as e:
            raise Abort("脱敏后的审计器出错,拒绝发布:%s" % e)
        not_pass = [r for r in eligible if post.status(r) != audit_refs.PASS]
        if not_pass:
            post.print(say)
            raise Abort("脱敏后仍有 %d 个引用没有通过审计(例如 %s),拒绝发布。" % (len(not_pass), post.shown(not_pass[0])))
        say("  脱敏后重新审计:%d 个引用全部 PASS(取值零命中)" % len(eligible))
        after = tips_in(clone, eligible)
        for r in eligible:
            tree_after = out("rev-parse", r + "^{tree}", cwd=clone)
            if tree_after != trees_before[r]:
                raise Abort("%s 的最新树哈希在脱敏后变了:脱敏不应改变最新版本的内容。" % r)
            count_after = int(out("rev-list", "--count", r, cwd=clone))
            if count_after != counts_before[r]:
                raise Abort("%s 的提交数在脱敏后变了(%d → %d)。" % (r, counts_before[r], count_after))
        commits_after = set(out("rev-list", *after, cwd=clone).split())
        same = len(commits_before & commits_after)
        say("  每个引用的最新树哈希与提交数都与脱敏前一致")
        say("  提交共 %d 个;其中 %d 个提交号不变,%d 个因祖先被改写而换号" % (len(commits_after), same, len(commits_after) - same))

        say("== 5/6 对照远端 %s ==" % redact(url))
        remote = ls_remote(url, clone)
        if remote:
            git("fetch", "-q", "--no-tags", url, "+refs/heads/*:refs/pub/heads/*", "+refs/tags/*:refs/pub/tags/*", cwd=clone)
        plan = []
        for r in eligible:
            new, old = after[r], remote.get(r)
            if old is None:
                plan.append(("create", r, old, new))
            elif old == new:
                plan.append(("same", r, old, new))
            elif r.startswith("refs/tags/"):
                plan.append(("skip", r, old, new))   # 标签已存在且指向别处:git 不允许更新,我们也不动它
            elif git("merge-base", "--is-ancestor", old, new, cwd=clone, check=False).returncode == 0:
                plan.append(("ff", r, old, new))
            elif r == MAIN_REF:
                raise Abort("远端 main(%s)不是本次结果的祖先:远端有本地没有的提交,或历史已不兼容。为安全起见不强推,请先人工核对。" % short(old))
            elif r.startswith("refs/heads/") and args.force_branches:
                plan.append(("force", r, old, new))
            else:
                plan.append(("skip", r, old, new))
        names = {"create": "创建", "ff": "快进", "force": "覆盖", "same": "已是最新", "skip": "跳过"}
        counts = {k: sum(1 for p in plan if p[0] == k) for k in names}
        say("  计划:创建 %d、快进 %d、覆盖 %d、已是最新 %d、跳过 %d" % tuple(counts[k] for k in ("create", "ff", "force", "same", "skip")))
        for action, r, old, new in plan:
            if action == "same":
                continue
            note = ""
            if action == "skip":
                note = ("  (标签已存在且指向别的提交,不改动)" if r.startswith("refs/tags/")
                        else "  (远端已有被改写过的版本,不是快进;确认要覆盖请加 --force-branches)")
            elif action == "force":
                note = "  (--force-branches:用 --force-with-lease 覆盖)"
            say("    %-4s %-34s %s → %s%s" % (names[action], r, short(old), short(new), note))
        extra = sorted(r for r in remote if r not in after and r not in skipped)
        if extra:
            say("  远端另有 %d 个本次没有的引用(不会删除):%s" % (len(extra), "、".join(extra[:6]) + (" …" if len(extra) > 6 else "")))

        if args.dry_run:
            say("== --dry-run:到此为止,没有推送任何东西 ==")
            return 0

        specs = [("+" if a == "force" else "") + "%s:%s" % (r, r) for a, r, _o, _n in plan if a in ("create", "ff", "force")]
        leases = ["--force-with-lease=%s:%s" % (r, o) for a, r, o, _n in plan if a == "force"]
        if not specs:
            say("== 6/6 远端已经是最新,无需推送 ==")
            return 0
        say("== 6/6 推送 %d 个引用(原子,不强推 main 与标签) ==" % len(specs))
        p = subprocess.run(["git", "-C", clone, "push", "--atomic", *leases, url, *specs])
        if p.returncode != 0:
            raise Abort("git push 失败(退出码 %d)。远端没有任何改变(原子推送);授权问题请看上面 git 的提示。" % p.returncode)
        check = ls_remote(url, clone)
        bad = [r for a, r, _o, n in plan if a in ("create", "ff", "force") and check.get(r) != n]
        if bad:
            raise Abort("推送后核对远端发现 %d 个引用与预期不符(例如 %s)。" % (len(bad), bad[0]))
        say("  推送后已核对远端:%d 个引用与预期一致" % len(specs))
        say("已发布:%s" % redact(url))
        say("  main @ %s(本地 main 是 %s;提交号不同是因为脱敏改写了祖先,最新树哈希相同)"
            % (short(after[MAIN_REF]), short(refs[MAIN_REF])))
        say("本地仓库没有任何改动。以后每次发布都重新运行本脚本,不要手敲 git push。")
        return 0
    finally:
        if args.keep:
            say("已保留临时目录:%s" % tmp)
        else:
            shutil.rmtree(tmp, ignore_errors=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--ssh", action="store_true")
    ap.add_argument("--url")
    ap.add_argument("--twice", action="store_true")
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--force-branches", action="store_true")
    ap.add_argument("--scrub-file", action="append", default=[], help=argparse.SUPPRESS)
    args = ap.parse_args(argv)
    try:
        return run(args)
    except Abort as e:
        print("错误:%s" % e, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
