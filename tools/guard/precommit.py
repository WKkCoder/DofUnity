#!/usr/bin/env python3
"""提交前守卫:把"不提交原版内容、不提交反编译代码"这条边界变成机械检查。

挡下的情况(任何一条命中就拒绝提交):
  1. 路径落在 re/、out/ 下,或是 local.env
  2. 扩展名是 .pvf .npk .img .ani .exe .pcap .pcapng
  3. 单个文件超过 1 MiB
  4. 文件开头是原版容器的魔数(NPK、IMG、旧版 IMG、标记流脚本 B0 D0)
  5. 文本里出现反编译器输出的特征(Ghidra 自动生成的名字与类型)
  6. 凭据:local.secrets.env 本身,或任何被暂存的文本里出现了该文件里的取值(口令、服务器地址等)

绕过只有一个办法:git commit --no-verify。那是你自己的明确决定,不是守卫的漏洞。
安装:sh tools/guard/install-hooks.sh
"""
import os
import re
import subprocess
import sys

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


def secret_values():
    """读取 local.secrets.env 里的取值(长度 >= 8),用来拦截它们出现在被暂存的文本里。"""
    root = git("rev-parse", "--show-toplevel").decode("utf-8").strip()
    path = os.path.join(root, "local.secrets.env")
    values = []
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                v = line.split("=", 1)[1].strip().strip("'\"")
                if len(v) >= 8:  # 太短的取值(如用户名)容易误伤普通文字
                    values.append(v)
    except OSError:
        pass
    return values


def main():
    names = [n for n in git("diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z").decode("utf-8").split("\0") if n]
    problems = []
    secrets = secret_values()
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
        leaked = next((s for s in secrets if s in text), None)
        if leaked:
            line = text.count("\n", 0, text.index(leaked)) + 1
            problems.append((n, "第 %d 行含有 local.secrets.env 里的取值(口令或服务器地址);请删掉" % line))
            continue
        m = DECOMPILER_RE.search(text)
        if m:
            line = text.count("\n", 0, m.start()) + 1
            problems.append((n, "第 %d 行像反编译器输出(%r);请改写成自己的描述,函数用地址与自拟名称" % (line, m.group())))
    if problems:
        print("提交被守卫拒绝:", file=sys.stderr)
        for n, why in problems:
            print("  %s\n    %s" % (n, why), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
