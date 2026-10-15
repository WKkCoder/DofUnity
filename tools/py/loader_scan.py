#!/usr/bin/env python3
"""从服务端脚本加载函数(例如 ImportAttackInfoScript)的 Ghidra 反编译文本里,抽出"标签 → 结构体偏移 → 读取方式"。

服务端的加载函数是一个很规整的结构(逐步读出来的,不是猜的):
  * 第一次调用时往一张静态 map 里登记"标签字符串 → 数字编号",之后每次按标签查编号;
  * 每个编号对应一个标签处理块:`if (local_30 == 编号) goto LAB_x`,块里 `__inited == 1` 那一支才是真正的处理(ScanInt / ScanFloat /
    ScanTypeOrStr 读值,存进 param_1[下标]);
  * 登记用的 `else` 那一支里,`local_n = 编号` 紧挨着 `"[标签名]"` 字符串。

这个脚本做的就是把这三件事对起来。它不改任何文件,输出是 TSV(放 re/,不入库):
    id  tag  offset  op  stores  strings
其中 offset 是 param_1[下标] 的字节偏移(下标×4),op 是读值函数,stores 是处理块里所有写入结构体的表达式,
strings 是块里出现的其他 "[...]" 字符串(通常是枚举取值)。

用法:python3 tools/py/loader_scan.py <反编译文本> [--out re/notes/server/xxx.tsv]
反编译文本由 tools/ghidra/DumpFunction.java 产出(<地址>.c.txt)。这些文本属于逆向产物,不进台账、不入库。
"""
import argparse
import re
import sys

ID_RE = re.compile(r"local_[0-9a-f]+ = (0x[0-9a-f]+|\d+);")
STR_RE = re.compile(r'ppfVar6\[1\] = \(float \*\)"(\[[^"]*\]|[^"]*)";')
GOTO_RE = re.compile(r"if \(local_30 == (0x[0-9a-f]+|\d+)\)\s*(\{|goto (LAB_[0-9a-f]+);)?")
LABEL_RE = re.compile(r"^\s*(LAB_[0-9a-f]+):")
INITED_ONE = re.compile(r"__inited == '\\x01'")
STORE_RE = [
    re.compile(r"param_1\[(0x[0-9a-f]+|\d+)\]\s*=\s*([^;]+);"),
    re.compile(r"\*param_1\s*=\s*([^;]+);"),
    re.compile(r"\*\((?:undefined\d?|char|byte|short|int|uint|float) \*\)\(param_1 \+ (0x[0-9a-f]+|\d+)\)\s*=\s*([^;]+);"),
    re.compile(r"\*\((?:undefined\d?|char|byte|short|int|uint|float) \*\)\(\(int\)param_1 \+ (0x[0-9a-f]+|\d+)\)\s*=\s*([^;]+);"),
]
OPS = ["ScanIntPb", "ScanFloatPb", "ScanTypeOrStr", "ScanTypeRSsb", "ScanStringRSsb", "ScanStr", "ScanHex", "ScanRect", "ScanNumList"]


def num(s):
    return int(s, 16) if s.startswith("0x") else int(s)


def branch_body(lines, start):
    """从 start 行(含 `if (... __inited == '\\x01') {`)起,按花括号配对取出这一支的行。"""
    depth = 0
    out = []
    started = False
    for i in range(start, len(lines)):
        ln = lines[i]
        for ch in ln:
            if ch == "{":
                depth += 1
                started = True
            elif ch == "}":
                depth -= 1
        out.append(ln)
        if started and depth <= 0:
            break
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("--out")
    a = ap.parse_args()
    lines = open(a.src, encoding="utf-8", errors="replace").read().splitlines()

    # 1. 编号 → 标签字符串:`local_n = 编号;` 之后几行内的第一个字符串
    id_tag = {}
    for i, ln in enumerate(lines):
        m = ID_RE.search(ln)
        if not m:
            continue
        for j in range(i + 1, min(i + 4, len(lines))):
            s = STR_RE.search(lines[j])
            if s:
                id_tag.setdefault(num(m.group(1)), s.group(1))
                break

    # 2. 标签处理块的入口:编号 → 标号
    labels = {}
    for i, ln in enumerate(lines):
        m = LABEL_RE.match(ln)
        if m:
            labels[m.group(1)] = i
    id_label = {}
    for i, ln in enumerate(lines):
        m = GOTO_RE.search(ln)
        if not m:
            continue
        ident = num(m.group(1))
        if m.group(3):
            id_label.setdefault(ident, m.group(3))
        elif m.group(2) == "{":
            for j in range(i + 1, min(i + 3, len(lines))):
                lm = LABEL_RE.match(lines[j])
                if lm:
                    id_label.setdefault(ident, lm.group(1))
                    break

    rows = []
    for ident in sorted(set(id_tag) | set(id_label)):
        tag = id_tag.get(ident, "?")
        label = id_label.get(ident)
        op = ""
        stores = []
        strings = []
        if label and label in labels:
            start = labels[label]
            # 标号后面第一个关于 __inited 的 if:
            #   写法 a:`if (__inited == 1) { 处理 } else { 登记 ... }`,处理在 if 里;
            #   写法 b:`if (__inited != 1) { 登记 ...; goto 下一个 } 处理`,处理紧跟在 if 块后面,到所在块结束为止。
            for k in range(start, min(start + 6, len(lines))):
                if "__inited" in lines[k] and "if" in lines[k]:
                    if INITED_ONE.search(lines[k]):
                        body = branch_body(lines, k)
                    else:
                        skipped = branch_body(lines, k)
                        body = []
                        for t in range(k + len(skipped), min(k + len(skipped) + 15, len(lines))):
                            stripped = lines[t].strip()
                            if stripped == "}" or stripped.startswith("LAB_") or stripped.startswith("goto"):
                                break
                            body.append(lines[t])
                    text = "\n".join(body)
                    for o in OPS:
                        if o in text:
                            op = (op + "+" + o) if op else o
                    for rx in STORE_RE:
                        for sm in rx.finditer(text):
                            if sm.re is STORE_RE[1]:
                                stores.append("0x0=" + sm.group(1).strip())
                            else:
                                stores.append("0x%x=%s" % (num(sm.group(1)) * (4 if sm.re is STORE_RE[0] else 1), sm.group(2).strip()))
                    for sm in STR_RE.finditer(text):
                        strings.append(sm.group(1))
                    break
        rows.append((ident, tag, op, stores, strings))

    out = open(a.out, "w", encoding="utf-8", newline="\n") if a.out else sys.stdout
    out.write("id\ttag\top\tstores\tstrings\n")
    for ident, tag, op, stores, strings in rows:
        out.write("0x%x\t%s\t%s\t%s\t%s\n" % (ident, tag, op, " | ".join(stores), " ".join(strings)))
    if a.out:
        out.close()
        print("tags=%d with_store=%d -> %s" % (len(rows), sum(1 for r in rows if r[3]), a.out))


if __name__ == "__main__":
    main()
