#!/usr/bin/env python3
"""历史审计与发布脱敏(命令行工具,也被 publish.py 当库用)。

audit:对一组引用(分支、标签)背后的全部历史逐个检查,给出"这个引用能不能公开"的结论。
  口径 = 提交守卫(precommit.py)的全部规则:路径、扩展名、体积、容器魔数、反编译器输出特征、
  local.secrets.env 里的取值及其编码写法(secretscan.py);
  再加几项只有"发布前整体过一遍"才值得做的:本机绝对路径、私钥与访问令牌、提交信息与作者字段、引用名本身、子模块。
  守卫只看暂存区,这里看的是每个引用整段历史里的每一个文件版本(含已经被后来的提交删掉的)。
  每个文件内容读出来后都会按对象号重新哈希核对,证明扫描器读到的就是历史里的那份字节。
  结论:PASS    可以发布;
        SCRUB   只有"允许改写的文件"里有取值命中,脱敏(scrub)之后可以发布;
        FAIL    不可发布。
  报告只含路径、短对象号、变量名与写法名,不含取值;路径本身含取值时用占位字样代替。

scrub:从标准输入读文件内容,把原样出现的取值替换掉再写到标准输出(git filter-branch 的 index-filter 用)。
selftest:只跑取值识别的正向自检。
secrets-path:打印取值文件的位置(找不到时退出码 3)。

用法:
  audit_refs.py audit [--repo 目录] [--secrets 文件] [--allow-secret-path 路径]... [--list-refs] 引用...
  audit_refs.py scrub [--secrets 文件] [--placeholder 文字]
  audit_refs.py selftest [--secrets 文件]
  audit_refs.py secrets-path
退出码(audit):0 全部 PASS;10 没有 FAIL 但有 SCRUB;1 有 FAIL;3 审计器自身出问题(含没有可用的取值)。
"""
import argparse
import hashlib
import os
import re
import subprocess
import sys
from collections import OrderedDict, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import precommit  # noqa: E402  复用守卫的常量,两边口径永远一致
import secretscan  # noqa: E402

PASS, SCRUB, FAIL = "PASS", "SCRUB", "FAIL"
HIDDEN = "(路径里含有取值,已隐藏)"   # 路径本身含取值时不能原样打印,否则报告自己就泄露了

# 本机绝对路径、私钥块与访问令牌的识别与提交守卫共用(定义在 secretscan.py)
LOCAL_PATH_RE = secretscan.LOCAL_PATH_RE
CREDENTIAL_RE = secretscan.CREDENTIAL_RE


class AuditError(Exception):
    """审计器自身出问题(读不到对象、哈希对不上……):结果不可信,必须中止。"""


class Repo:
    def __init__(self, path):
        self.path = path

    def run(self, *args, input=None):
        return subprocess.run(["git", "-C", self.path, *args], input=input, capture_output=True, check=True).stdout

    def text(self, *args):
        return self.run(*args).decode("utf-8", "replace")


class ObjectReader:
    """常驻的 git cat-file --batch:一问一答,避免给几千个对象各起一个进程。"""

    def __init__(self, repo):
        self.proc = subprocess.Popen(["git", "-C", repo.path, "cat-file", "--batch"],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE)

    def get(self, sha):
        self.proc.stdin.write(sha.encode() + b"\n")
        self.proc.stdin.flush()
        header = self.proc.stdout.readline().split()
        if len(header) != 3:
            raise AuditError("读不到对象 %s" % sha)
        size = int(header[2])
        body = self.proc.stdout.read(size)
        self.proc.stdout.read(1)
        if len(body) != size:
            raise AuditError("对象 %s 读到的长度不对" % sha)
        return header[1].decode(), body

    def close(self):
        try:
            self.proc.stdin.close()
        finally:
            self.proc.wait()


def _hasher(repo):
    try:
        fmt = repo.text("rev-parse", "--show-object-format").strip()
    except subprocess.CalledProcessError:
        fmt = "sha1"
    return (hashlib.sha256, 32) if fmt == "sha256" else (hashlib.sha1, 20)


def _oid(algo, typ, body):
    return algo(("%s %d" % (typ, len(body))).encode() + b"\0" + body).hexdigest()


def parse_tree(body, hash_len):
    entries = {}
    i = 0
    while i < len(body):
        sp = body.index(b" ", i)
        nul = body.index(b"\0", sp)
        name = body[sp + 1:nul].decode("utf-8", "surrogateescape")
        entries[name] = (body[i:sp].decode(), body[nul + 1:nul + 1 + hash_len].hex())
        i = nul + 1 + hash_len
    return entries


def parse_commit(body):
    head, _, message = body.partition(b"\n\n")
    tree, author, committer = None, b"", b""
    for line in head.split(b"\n"):
        if line.startswith(b"tree "):
            tree = line[5:].decode()
        elif line.startswith(b"author "):
            author = line[7:]
        elif line.startswith(b"committer "):
            committer = line[10:]
    if tree is None:
        raise AuditError("提交对象里没有 tree 行")
    return tree, author, committer, message


class Finding:
    """一条问题。范围是三选一:blob(+路径集合)、提交、引用名。"""

    def __init__(self, kind, detail, blob=None, paths=(), commit=None, ref=None, plain_secret=False):
        self.kind, self.detail = kind, detail
        self.blob, self.paths = blob, frozenset(paths)
        self.commit, self.ref = commit, ref
        self.plain_secret = plain_secret
        self.allowed = False
        self.id = 0


def content_checks(text, matchers, is_text):
    """对一段内容做"与路径无关"的检查,返回 [(种类, 说明, 是否原样取值)]。每种最多报一条。"""
    out = []
    leaked = matchers.find(text)
    if leaked:
        name, label, _m = leaked
        out.append(("凭据", "含有 %s 的取值(写法:%s)" % (name, label), label == secretscan.PLAIN))
    if is_text:
        m = precommit.DECOMPILER_RE.search(text)
        if m:
            out.append(("反编译器输出", "像反编译器输出(%r)" % m.group(), False))
    m = LOCAL_PATH_RE.search(text)
    if m:
        out.append(("本机路径", "含有本机绝对路径(%s)" % m.group()[:40], False))
    if CREDENTIAL_RE.search(text):
        out.append(("私钥或令牌", "含有私钥块或访问令牌的特征", False))
    return out


def path_checks(path):
    low = path.lower()
    base = os.path.basename(path)
    out = []
    if path.startswith(precommit.FORBIDDEN_PREFIXES) or path in precommit.FORBIDDEN_NAMES \
            or base in precommit.FORBIDDEN_NAMES or low.endswith(precommit.FORBIDDEN_SUFFIXES):
        out.append(("路径", "路径属于逆向产物、本机配置或凭据"))
    if low.endswith(precommit.FORBIDDEN_EXTS):
        out.append(("扩展名", "扩展名属于原版文件或抓包"))
    return out


class Report:
    def __init__(self):
        self.verdicts = OrderedDict()   # 引用 -> (结论, [问题编号])
        self.findings = []              # 全部问题(已编号)
        self.stats = {}
        self.shown = lambda p: p

    def status(self, ref):
        return self.verdicts[ref][0]

    def refs_with(self, status):
        return [r for r, (s, _ids) in self.verdicts.items() if s == status]

    def exit_code(self):
        if self.refs_with(FAIL):
            return 1
        return 10 if self.refs_with(SCRUB) else 0

    def describe(self, f):
        where = ""
        if f.blob:
            ps = sorted(f.paths)
            where = "  %s%s(对象 %s)" % (self.shown(ps[0]), " 等 %d 处" % len(ps) if len(ps) > 1 else "", f.blob[:7])
        elif f.commit:
            where = "  提交 %s" % f.commit[:7]
        elif f.ref:
            where = "  引用 %s" % self.shown(f.ref)
        note = "  → 允许:发布时脱敏" if f.allowed else ""
        return "[%d] %s:%s%s%s" % (f.id, f.kind, f.detail, where, note)

    def print(self, log=print, list_refs=False):
        s = self.stats
        log("  范围:引用 %d 个、提交 %d 个、目录 %d 个、文件内容 %d 个(逐个按对象号核对过)"
            % (s["refs"], s["commits"], s["trees"], s["blobs"]))
        log("  规则:取值 %d 个(检测写法 %d 条);守卫全部规则,另加本机路径、私钥与令牌、提交信息、引用名、子模块"
            % (s["secrets"], s["rules"]))
        if self.findings:
            log("  问题清单(%d 项):" % len(self.findings))
            for f in self.findings:
                log("    " + self.describe(f))
        groups = OrderedDict()
        for ref, (status, ids) in self.verdicts.items():
            groups.setdefault((status, tuple(ids)), []).append(ref)
        counts = {k: len(self.refs_with(k)) for k in (PASS, SCRUB, FAIL)}
        log("  引用结论:PASS %d,SCRUB %d,FAIL %d" % (counts[PASS], counts[SCRUB], counts[FAIL]))
        for (status, ids), refs in groups.items():
            if status == PASS and not list_refs:
                continue
            names = [self.shown(r) for r in refs]
            shown_names = names if (list_refs or len(names) <= 4) else names[:4] + ["…(另 %d 个)" % (len(names) - 4)]
            log("    %-5s ×%d  %s%s" % (status, len(refs), "、".join(shown_names),
                                       "  问题 " + "".join("[%d]" % i for i in ids) if ids else ""))


def audit(repo_path, refs, matchers, allow_paths=()):
    """审计 refs(完整引用名列表)背后的全部历史,返回 Report。审计器自身出问题时抛 AuditError。"""
    if not matchers:
        raise AuditError("没有可用的取值:找不到 local.secrets.env,或其中没有长度 >= 3 的取值。没有它无法判断历史里有没有凭据。")
    failures = matchers.selftest()
    if failures:
        raise AuditError("取值识别的正向自检失败(%d 项),扫描结果不可信:%s" % (len(failures), ";".join(failures[:3])))
    allow = frozenset(allow_paths)
    repo = Repo(repo_path)
    algo, hash_len = _hasher(repo)
    reader = ObjectReader(repo)
    report = Report()
    report.shown = lambda p: HIDDEN if matchers.hides(p) else p
    try:
        ref_commits = OrderedDict()
        for ref in refs:
            ref_commits[ref] = set(repo.text("rev-list", ref).split())
        all_commits = set()
        for s in ref_commits.values():
            all_commits |= s

        trees = {}

        def get_tree(sha):
            if sha not in trees:
                typ, body = reader.get(sha)
                if typ != "tree" or _oid(algo, typ, body) != sha:
                    raise AuditError("树对象 %s 读取后核对不上" % sha)
                trees[sha] = parse_tree(body, hash_len)
            return trees[sha]

        findings = []
        commit_tree = {}
        for c in sorted(all_commits):
            typ, body = reader.get(c)
            if typ != "commit" or _oid(algo, typ, body) != c:
                raise AuditError("提交对象 %s 读取后核对不上" % c)
            tree, author, committer, message = parse_commit(body)
            commit_tree[c] = tree
            meta = (author + b"\n" + committer + b"\n" + message).decode("utf-8", "replace")
            for kind, detail, plain in content_checks(meta, matchers, False):
                findings.append(Finding("提交信息或作者字段·" + kind, detail, commit=c, plain_secret=False))
        for ref in refs:
            hit = matchers.find(ref)
            if hit:
                findings.append(Finding("引用名", "引用名含有 %s 的取值(写法:%s)" % (hit[0], hit[1]), ref=ref))

        paths_of = defaultdict(set)       # 文件内容对象号 -> 出现过的全部路径
        gitlinks = defaultdict(set)       # 子模块路径 -> 提交号
        seen = set()

        def walk(tree_sha, prefix):
            if (tree_sha, prefix) in seen:
                return
            seen.add((tree_sha, prefix))
            for name, (mode, sha) in get_tree(tree_sha).items():
                path = prefix + name
                if mode == "40000":
                    walk(sha, path + "/")
                elif mode == "160000":
                    gitlinks[path].add(sha)
                else:
                    paths_of[sha].add(path)

        for c in all_commits:
            walk(commit_tree[c], "")

        for path, shas in gitlinks.items():
            for sha in shas:
                findings.append(Finding("子模块", "含有子模块(外部仓库引用)", blob=sha, paths=[path]))

        for sha in sorted(paths_of):
            typ, body = reader.get(sha)
            if typ != "blob" or _oid(algo, typ, body) != sha:
                raise AuditError("文件内容 %s 读取后核对不上" % sha)
            paths = paths_of[sha]
            for p in sorted(paths):
                for kind, detail in path_checks(p):
                    findings.append(Finding(kind, detail, blob=sha, paths=[p]))
            if len(body) > precommit.MAX_BYTES:
                findings.append(Finding("体积", "文件超过 1 MiB(%d 字节),原版素材通常很大" % len(body), blob=sha, paths=paths))
            head = body[:32]
            magic = next((label for m, label in precommit.MAGICS if head.startswith(m)), None)
            if magic:
                findings.append(Finding("容器魔数", "文件头是原版容器魔数:" + magic, blob=sha, paths=paths))
            try:
                text, is_text = body.decode("utf-8"), True
            except UnicodeDecodeError:
                text, is_text = body.decode("latin-1"), False   # 取值与路径特征都是 ASCII,latin-1 解码不丢信息
            for kind, detail, plain in content_checks(text, matchers, is_text):
                if plain and allow:
                    # 原样取值:按路径分别判定。同一份内容在允许路径里可以脱敏,在别的路径里就不行
                    for p in sorted(paths):
                        findings.append(Finding(kind, detail, blob=sha, paths=[p], plain_secret=True))
                else:
                    findings.append(Finding(kind, detail, blob=sha, paths=paths, plain_secret=plain))

        # 归属:一条问题影响哪些提交,再据此判断每个引用
        def follow(tree_sha, path):
            parts = path.split("/")
            cur = tree_sha
            for i, part in enumerate(parts):
                ent = get_tree(cur).get(part)
                if ent is None:
                    return None
                mode, sha = ent
                if i == len(parts) - 1:
                    return sha
                if mode != "40000":
                    return None
                cur = sha
            return None

        affected_cache = {}

        def affected_commits(f):
            if f.commit:
                return {f.commit}
            key = (f.blob, f.paths)
            if key not in affected_cache:
                hit = set()
                for c in all_commits:
                    if any(follow(commit_tree[c], p) == f.blob for p in f.paths):
                        hit.add(c)
                affected_cache[key] = hit
            return affected_cache[key]

        for f in findings:
            # 只有"原样取值"且命中的正是允许改写的路径,才算可脱敏;编码写法脱敏替换不了,一律不允许
            f.allowed = bool(f.plain_secret and f.blob and len(f.paths) == 1 and next(iter(f.paths)) in allow)
        # 编号:同一问题只列一次,按"先不允许、后允许"再按位置稳定排序
        findings.sort(key=lambda f: (f.allowed, f.kind, f.blob or f.commit or f.ref or "", sorted(f.paths)))
        for i, f in enumerate(findings, 1):
            f.id = i
        report.findings = findings

        for ref in refs:
            ids, bad, scrub = [], False, False
            for f in findings:
                if f.ref:
                    hit = f.ref == ref
                else:
                    hit = bool(affected_commits(f) & ref_commits[ref])
                if hit:
                    ids.append(f.id)
                    if f.allowed:
                        scrub = True
                    else:
                        bad = True
            report.verdicts[ref] = (FAIL if bad else SCRUB if scrub else PASS, ids)
        report.stats = {"refs": len(refs), "commits": len(all_commits), "trees": len(trees), "blobs": len(paths_of),
                        "secrets": len(matchers.pairs), "rules": len(matchers.items)}
        return report
    finally:
        reader.close()


def cmd_audit(args):
    matchers = secretscan.load_matchers(args.secrets)
    try:
        report = audit(args.repo, args.refs, matchers, args.allow_secret_path)
    except AuditError as e:
        print("审计器出错:%s" % e, file=sys.stderr)
        return 3
    print("== 审计 ==")
    report.print(print, args.list_refs)
    return report.exit_code()


def cmd_scrub(args):
    matchers = secretscan.load_matchers(args.secrets, encoded=False)
    data = sys.stdin.buffer.read()
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        sys.stdout.buffer.write(data)
        return 0
    sys.stdout.buffer.write(matchers.replace_plain(text, args.placeholder).encode("utf-8"))
    return 0


def cmd_selftest(args):
    matchers = secretscan.load_matchers(args.secrets)
    if not matchers:
        print("没有可用的取值,无法自检。", file=sys.stderr)
        return 3
    failures = matchers.selftest()
    for f in failures:
        print("自检失败:" + f, file=sys.stderr)
    print("取值 %d 个,检测写法 %d 条,自检失败 %d 项" % (len(matchers.pairs), len(matchers.items), len(failures)))
    return 1 if failures else 0


def cmd_secrets_path(_args):
    p = secretscan.find_secrets_file()
    if not p:
        print("找不到取值文件。找过:%s" % "、".join(secretscan.candidate_paths() or ["(当前目录不在 git 仓库里)"]), file=sys.stderr)
        return 3
    print(p)
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="历史审计与发布脱敏(详见文件头部说明)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("audit", help="审计一组引用背后的全部历史")
    a.add_argument("--repo", default=".")
    a.add_argument("--secrets")
    a.add_argument("--allow-secret-path", action="append", default=[], help="允许出现原样取值(发布时会被脱敏)的路径")
    a.add_argument("--list-refs", action="store_true", help="逐个列出 PASS 的引用")
    a.add_argument("refs", nargs="+")
    a.set_defaults(fn=cmd_audit)
    s = sub.add_parser("scrub", help="标准输入 -> 标准输出,替换原样出现的取值")
    s.add_argument("--secrets")
    s.add_argument("--placeholder", default="example")
    s.set_defaults(fn=cmd_scrub)
    t = sub.add_parser("selftest", help="取值识别的正向自检")
    t.add_argument("--secrets")
    t.set_defaults(fn=cmd_selftest)
    p = sub.add_parser("secrets-path", help="打印取值文件的位置")
    p.set_defaults(fn=cmd_secrets_path)
    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
