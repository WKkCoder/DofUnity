#!/usr/bin/env python3
"""给 cl_slice.py 切出来的文本(每行带地址前缀)做字面量还原,方便人读:

  1. `decodeLiteral(&DAT_xxxxxxxx)`(也认 `&LAB_xxxxxxxx`、十六进制数)换成 `decodeLiteral("解码后的字符串")`;
     解码表来自 re/notes/strings_decoded.tsv(静态解码结果,本机文件,不入库);
  2. 某个变量刚被赋值成字面量,后面紧接着(同一缩进块里 6 行以内)的 `wstrEqualsLit(x, 变量)` 或 `wstrCompare(…, 变量, …)`
     行尾追加注释 `/* 比较的是 "字面量" */`。

只做文本替换,不做语义推断。输入输出都在 out/ 或 re/ 下(含原版反编译文本,不入库)。就地改写。
用法:python3 tools/py/slice_lits.py FILE_OR_DIR [...]
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STR_TSV = os.path.join(ROOT, "re", "notes", "strings_decoded.tsv")

DEC = re.compile(r"decodeLiteral\(&?(?:DAT_|LAB_)?([0-9a-fA-F]{8})\)")
ASSIGN = re.compile(r"\b(\w+) = decodeLiteral\(\"((?:[^\"\\]|\\.)*)\"\)")
CMPUSE = re.compile(r"(wstrEqualsLit|wstrCompare)\(([^;]*)\)")


def load_strings():
    d = {}
    for ln in open(STR_TSV, encoding="utf-8"):
        p = ln.rstrip("\n").split("\t")
        if len(p) >= 5 and p[0] != "blob_va":
            d[p[0].lower().lstrip("0")] = p[4]
    return d


def quote(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def process(path, strs):
    lines = open(path, encoding="utf-8").read().split("\n")
    out = []
    last = {}  # 变量名 -> (字面量, 行号)
    for i, ln in enumerate(lines):
        def rep(m):
            s = strs.get(m.group(1).lower().lstrip("0"))
            return "decodeLiteral(%s)" % quote(s) if s is not None else m.group(0)

        ln2 = DEC.sub(rep, ln)
        m = ASSIGN.search(ln2)
        if m:
            last[m.group(1)] = (m.group(2), i)
        else:
            m2 = CMPUSE.search(ln2)
            if m2:
                for var, (lit, at) in list(last.items()):
                    if i - at <= 6 and re.search(r"\b" + re.escape(var) + r"\b", m2.group(2)):
                        ln2 += "  /* 比较的是 \"%s\" */" % lit
                        break
        out.append(ln2)
    open(path, "w", encoding="utf-8").write("\n".join(out))


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    strs = load_strings()
    n = 0
    for arg in sys.argv[1:]:
        ap = os.path.abspath(arg)
        if not any(ap.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
            sys.exit("拒绝:只处理 re/ 或 out/ 下的文件")
        files = [ap] if os.path.isfile(ap) else [os.path.join(dp, f) for dp, _, fs in os.walk(ap) for f in fs if f.endswith(".txt") and not f.endswith(".asm.txt") and f != "INDEX.tsv"]
        for f in files:
            process(f, strs)
            n += 1
    print("处理了", n, "个文件")


if __name__ == "__main__":
    main()
