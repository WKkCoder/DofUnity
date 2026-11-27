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
  8. shell 脚本(*.sh)的非注释行里,"$变量名" 后面紧跟非 ASCII 字符(全角逗号、全角括号、汉字……)。
     macOS 自带的 bash 3.2 在 UTF-8 语言环境下会把这个多字节字符当成变量名的一部分,展开的是一个不存在的变量;
     配合 set -u 就是 unbound variable,脚本悄悄退出,退出码还是 0(C 语言环境没有这个问题)。请写成 ${变量名}。
     整行注释不查;前面有奇数个反斜杠的 \\$ 是字面量的美元符,也不算。

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


# 第 8 条:"$变量名" 后面紧跟非 ASCII 字符。第一组吃掉 $ 前面连着的反斜杠(奇数个 = 这个 $ 被转义成字面量)。
SHELL_VAR_BEFORE_NON_ASCII_RE = re.compile(r"(\\*)\$([A-Za-z_][A-Za-z0-9_]*)(?=[^\x00-\x7f])")


def shell_var_before_non_ascii(text):
    """shell 脚本文本里,非注释行中 "$变量名" 后面紧跟非 ASCII 字符的位置:[(行号, 变量名, 紧跟的字符), ...]。

    只按 \\n 分行(CRLF 行尾的 \\r 是 ASCII,不影响判断);首个非空白字符(空格、制表符)是 # 的整行是注释,不查。
    $1、$@、$?、$$ 这类特殊参数不是 "$变量名"(名字必须以字母或下划线开头),后面紧跟什么都不会被当成名字的一部分,不算。"""
    hits = []
    for no, line in enumerate(text.split("\n"), 1):
        if line.lstrip(" \t").startswith("#"):
            continue
        for m in SHELL_VAR_BEFORE_NON_ASCII_RE.finditer(line):
            if len(m.group(1)) % 2 == 0:
                hits.append((no, m.group(2), line[m.end():m.end() + 1]))
    return hits


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
            continue
        if low.endswith(".sh"):
            hits = shell_var_before_non_ascii(text)
            if hits:
                no, name, ch = hits[0]
                problems.append((n, "第 %d 行的 $%s 后面紧跟非 ASCII 字符 %r(共 %d 处):macOS 自带的 bash 3.2 在 UTF-8 语言环境下会把它当成变量名的一部分,"
                                    "set -u 之下报 unbound variable、脚本悄悄退出且退出码为 0。请写成 ${%s}"
                                 % (no, name, ch, len(hits), name)))
    if problems:
        print("提交被守卫拒绝:", file=sys.stderr)
        for n, why in problems:
            print("  %s\n    %s" % (n, why), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
