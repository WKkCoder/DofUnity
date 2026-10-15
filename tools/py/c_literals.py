#!/usr/bin/env python3
"""给 Ghidra 的反编译文本(本机 re/ 下,不入库)做可读性处理:

  1. `decodeLiteral(&DAT_xxxxxxxx)`(也认 `&LAB_xxxxxxxx` 与十六进制数)换成解码后的字符串字面量
     (来自 re/notes/strings_decoded.tsv,静态解码结果);
  2. "取字面量 + 与标签比较"折叠成 `if (tok != "[X]") ...`,包括内联的"数长度 + 比较"写法;
  3. 去掉与逻辑无关的噪声行(异常处理状态号、栈保护、FS 偏移保存)。

只做文本替换,不做语义推断。输入输出都只放 re/ 或 out/(含原版文本,绝不提交)。

用法:python3 tools/py/c_literals.py <输入 .c.txt> <输出 .txt>
可选:--decode-name decodeLiteral   --eq-name wstrEqualsLit   --cmp-name wstrCompare(换成别的函数名时用)
"""
import argparse
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load_strings(path):
    d = {}
    for ln in open(path, encoding="utf-8"):
        p = ln.rstrip("\n").split("\t")
        if len(p) >= 5 and p[0] != "blob_va":
            d[p[0].lower().lstrip("0")] = p[4]
    return d


def join_wrapped(text):
    """Ghidra 会把过长的语句折行;把折行的续行并回上一行(上一行不以 ; { } */ : 结尾就并)。"""
    out = []
    buf = None
    for ln in text.split("\n"):
        if buf is not None:
            buf += ln.strip()
            if buf.rstrip().endswith((";", "{", "}", "*/", ":")):
                out.append(buf)
                buf = None
            continue
        r = ln.rstrip()
        if r == "" or r.endswith((";", "{", "}", "*/", ":")) or r.lstrip().startswith("/*") or r.lstrip().startswith("//"):
            out.append(ln)
        elif r.lstrip().startswith(("else", "do", "LAB_")) or r.lstrip().startswith("#"):
            out.append(ln)
        else:
            buf = r
    if buf is not None:
        out.append(buf)
    return "\n".join(out)


def quote(s):
    s = s.replace("\\", "\\\\").replace('"', '\\"')
    if len(s) > 120:
        s = s[:117] + "..."
    return '"' + s + '"'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--strings", default=os.path.join(ROOT, "re", "notes", "strings_decoded.tsv"))
    ap.add_argument("--decode-name", default="decodeLiteral")
    ap.add_argument("--eq-name", default="wstrEqualsLit")
    ap.add_argument("--cmp-name", default="wstrCompare")
    ap.add_argument("--slots", default=os.path.join(ROOT, "re", "notes", "slot_names.json"),
                    help="虚函数槽名字表(tools/py/slot_names.py 生成);存在则给 `+ 0xNN))(` 的虚调用加注释")
    a = ap.parse_args()

    for p in (a.src, a.dst):
        ap_ = os.path.abspath(p)
        if not any(ap_.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
            sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下(含原版文本,不入库)")

    strs = load_strings(a.strings)
    text = join_wrapped(open(a.src, encoding="utf-8").read())

    dec = re.escape(a.decode_name)
    eq = re.escape(a.eq_name)
    cmpn = re.escape(a.cmp_name)
    addr_arg = r"(?:&(?:DAT|LAB)_(?P<addr>[0-9a-fA-F]+)|0x(?P<addr2>[0-9a-fA-F]+))"

    def lit_of(m):
        addr = m.group("addr") or m.group("addr2")
        s = strs.get(addr.lower().lstrip("0"))
        return quote(s) if s is not None else "?LIT_" + addr

    # 1) 先把所有 decodeLiteral(...) 换成带引号的字面量(此时形如  某个局部变量 = "[X]";  或  (short *)"[X]")
    text = re.sub(dec + r"\(" + addr_arg + r"\)", lambda m: lit_of(m), text)

    # 2a) uVar = "lit"; [任意 0-1 行 in_stack 赋值]  cVar = wstrEqualsLit(tok,uVar);  if (cVar op '\0') REST
    fold_eq = re.compile(
        r"^(?P<ind>[ ]*)(?P<lv>\w+) = (?P<lit>\"(?:[^\"\\\n]|\\.)*\");\n"
        r"(?:[ ]*(?:in_stack_\w+|puVar\d+) = [^\n]*;\n)?"
        r"[ ]*(?P<cv>\w+) = " + eq + r"\((?P<tok>&?\w+),(?P=lv)\);\n"
        r"[ ]*if \((?P=cv) (?P<op>==|!=) '\\0'\)(?P<rest>[^\n]*)",
        re.M)

    def sub_eq(m):
        op = "!=" if m.group("op") == "==" else "=="
        return "%sif (%s %s %s)%s" % (m.group("ind"), m.group("tok"), op, m.group("lit"), m.group("rest"))

    text = fold_eq.sub(sub_eq, text)

    # 2b) 内联写法:p1 = (short *)"lit"; p2 = p1; do { s = *p2; p2 = p2 + 1; } while (s != 0);
    #     r = wstrCompare(tok,0,n,p1,(int)p2 - (int)(p1 + 1) >> 1);  if (r op 0) REST
    fold_cmp = re.compile(
        r"^(?P<ind>[ ]*)(?P<p1>\w+) = \(short \*\)(?P<lit>\"(?:[^\"\\\n]|\\.)*\");\n"
        r"[ ]*(?P<p2>\w+) = (?P=p1);\n"
        r"[ ]*do \{\n"
        r"[ ]*(?P<s>\w+) = \*(?P=p2);\n"
        r"[ ]*(?P=p2) = (?P=p2) \+ 1;\n"
        r"[ ]*\} while \((?P=s) != 0\);\n"
        r"[ ]*(?P<r>\w+) = " + cmpn + r"\((?P<tok>&?\w+),0,\w+,(?P=p1),\(int\)(?P=p2) - \(int\)\((?P=p1) \+ 1\) >> 1\);\n"
        r"[ ]*if \((?P=r) (?P<op>==|!=) 0\)(?P<rest>[^\n]*)",
        re.M)

    def sub_cmp(m):
        return "%sif (%s %s %s)%s" % (m.group("ind"), m.group("tok"), m.group("op"), m.group("lit"), m.group("rest"))

    text = fold_cmp.sub(sub_cmp, text)

    noise = re.compile(
        r"^[ ]*(uStack_8(\._0_1_)? = [^\n]*;|puStack_c = [^\n]*;|iStack_10 = [^\n]*;|uStack_14 = [^\n]*;"
        r"|\*unaff_FS_OFFSET = [^\n]*;|__security_check_cookie\([^\n]*\);|in_stack_\w+ = [^\n]*;)\n",
        re.M)
    text = noise.sub("", text)

    # 3) 虚调用加槽名注释:(**(code **)(*obj + 0xa4))()  ->  ... + 0xa4/*getXPos*/))()
    if a.slots and os.path.exists(a.slots):
        table = {}
        for k, v in json.load(open(a.slots, encoding="utf-8")).items():
            names = v.get("A") or v.get("B") or []
            if names:
                table[int(k, 16)] = "|".join(names[:3])

        def slot_sub(m):
            raw = m.group(1)
            n = int(raw, 16) if raw.startswith("0x") else int(raw)
            nm = table.get(n)
            return "+ %s/*%s*/))(" % (raw, nm) if nm else m.group(0)

        text = re.sub(r"\+ (0x[0-9a-fA-F]+|\d+)\)\)\(", slot_sub, text)
    open(a.dst, "w", encoding="utf-8").write(text)
    print("已写入", a.dst, len(text.split("\n")), "行")


if __name__ == "__main__":
    main()
