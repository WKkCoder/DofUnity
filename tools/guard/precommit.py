#!/usr/bin/env python3
"""提交前守卫:把"不提交原版内容、不提交反编译代码、不提交凭据"这条边界变成机械检查。

挡下的情况(任何一条命中就拒绝提交):
  1. 路径落在 re/、out/ 下,或是 local.env
  2. 扩展名是 .pvf .npk .img .ani .exe .pcap .pcapng
  3. 单个文件超过 1 MiB
  4. 文件开头是原版容器的魔数(NPK、IMG、旧版 IMG、标记流脚本 B0 D0)
  5. 文本里出现反编译器输出的特征(Ghidra 自动生成的名字与类型)
  6. 凭据:local.secrets.env 本身,或任何被暂存的文本里出现了该文件里的取值(账号、口令、服务器地址等),
     包括它们的常见编码写法(十六进制、Base64、UTF-16、摘要……,规则见 secretscan.py)
  7. 本机绝对路径(/Users/名字、/home/名字、C:\\Users\\名字),以及私钥块与常见访问令牌

取值文件的位置:环境变量 DNF_SECRETS_FILE;否则当前工作区根目录;再否则主工作区(各 worktree 里没有这个被忽略的文件)。
找不到取值文件时只提示、不拦截,因为没有取值可比对。

绕过只有一个办法:git commit --no-verify。那是你自己的明确决定,不是守卫的漏洞。
安装:sh tools/guard/install-hooks.sh
"""
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import secretscan  # noqa: E402  与发布前审计共用同一套取值识别规则

MAX_BYTES = 1 << 20
FORBIDDEN_PREFIXES = ("re/", "out/")
FORBIDDEN_NAMES = ("local.env", "local.secrets.env")
FORBIDDEN_SUFFIXES = (".secrets.env",)
FORBIDDEN_EXTS = (".pvf", ".npk", ".img", ".ani", ".exe", ".pcap", ".pcapng")
MAGICS = [
    (b"NeoplePack_Bill", "NPK 容器"),
    (b"Neople Img File", "IMG v2"),
    (b"Neople Image File", "旧版 IMG"),
    (b"\xb0\xd0", "PVF 标记流脚本"),
]
# 用拼接避免本文件自己命中自己
_FUN = "FU" + "N_"
_DAT = "DA" + "T_"
_LAB = "LA" + "B_"
DECOMPILER_RE = re.compile(
    r"\b(?:%s|%s|%s)[0-9a-fA-F]{6,8}\b|\bund" "efined[1248]?\\b(?=\\s+[A-Za-z_*])|\\b[uicp]{1,2}Var\\d+\\b"
    % (_FUN, _DAT, _LAB)
)


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, check=True).stdout


def main():
    names = [n for n in git("diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z").decode("utf-8").split("\0") if n]
    problems = []
    matchers = secretscan.load_matchers()
    if not matchers and names:
        print("提示:没找到 local.secrets.env(找过:%s),这次提交没有做凭据检查。"
              % "、".join(secretscan.candidate_paths() or ["当前目录不在 git 仓库里"]), file=sys.stderr)
    for n in names:
        low = n.lower()
        if n.startswith(FORBIDDEN_PREFIXES) or n in FORBIDDEN_NAMES or low.endswith(FORBIDDEN_SUFFIXES):
            problems.append((n, "路径属于逆向产物、本机配置或凭据,不能提交"))
            continue
        if low.endswith(FORBIDDEN_EXTS):
            problems.append((n, "扩展名属于原版文件或抓包"))
            continue
        blob = git("show", ":" + n)
        if len(blob) > MAX_BYTES:
            problems.append((n, "文件超过 1 MiB(%d 字节),原版素材通常很大" % len(blob)))
            continue
        head = blob[:32]
        hit = next((label for magic, label in MAGICS if head.startswith(magic)), None)
        if hit:
            problems.append((n, "文件头是原版容器魔数:" + hit))
            continue
        try:
            text = blob.decode("utf-8")
        except UnicodeDecodeError:
            continue
        leaked = matchers.find(text)
        if leaked:
            name, label, m = leaked
            line = text.count("\n", 0, m.start()) + 1
            problems.append((n, "第 %d 行含有 local.secrets.env 里 %s 的取值(写法:%s);请删掉" % (line, name, label)))
            continue
        m = DECOMPILER_RE.search(text)
        if m:
            line = text.count("\n", 0, m.start()) + 1
            problems.append((n, "第 %d 行像反编译器输出(%r);请改写成自己的描述,函数用地址与自拟名称" % (line, m.group())))
            continue
        m = secretscan.LOCAL_PATH_RE.search(text)
        if m:
            line = text.count("\n", 0, m.start()) + 1
            problems.append((n, "第 %d 行含有本机绝对路径(%s);个人目录名不该进公开历史,请改成相对路径或占位写法" % (line, m.group()[:40])))
            continue
        m = secretscan.CREDENTIAL_RE.search(text)
        if m:
            line = text.count("\n", 0, m.start()) + 1
            problems.append((n, "第 %d 行像私钥或访问令牌;请删掉,并且该令牌应当视为已泄露、立即作废" % line))
    if problems:
        print("提交被守卫拒绝:", file=sys.stderr)
        for n, why in problems:
            print("  %s\n    %s" % (n, why), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
