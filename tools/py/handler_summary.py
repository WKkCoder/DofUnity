#!/usr/bin/env python3
"""把"按记录类型分派"的大函数(CNRDAction 的条件求值/行为执行)拆成每个类型一段,并给出摘要。

输入:经 c_literals.py 处理过的伪代码文本(含槽名注释)。只处理本机 re/ 或 out/ 下的文件,输出也在那里。
做法:找 `(else )?if (VAR == 常数)` 与 `case 常数:` 两种分派,按大括号/下一个 case 切出每段,
对每段统计:行数、读取记录参数的下标、调用的虚函数槽(带名字)、被调函数地址、字符串字面量(日志文字)。
摘要写成 TSV;`--show 0x6b` 打印某一类型的整段文本(便于人工细读)。

用法:
  python3 tools/py/handler_summary.py SRC.pseudo.txt --var <记录类型变量名> --out re/notes/engine2/x_summary.tsv
  python3 tools/py/handler_summary.py SRC.pseudo.txt --var <记录类型变量名> --show 0x4b
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def check_path(p):
    ap = os.path.abspath(p)
    if not any(ap.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
        sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下(含原版文本,不入库)")


# 读记录参数的函数 0x0107b2e0:套用名字之前的反编译文本里是自动名(前缀加地址),之后是 recordParam;用拼接写出自动名
PARAM_ACCESSOR = "recordParam|FUN" + "_0107b2e0"


def block_end(lines, i):
    """行 i 以 `{` 结尾,返回与之配对的 `}` 所在行(含)。"""
    depth = 0
    for j in range(i, len(lines)):
        ln = lines[j]
        depth += ln.count("{") - ln.count("}")
        if depth <= 0 and j >= i:
            return j
    return len(lines) - 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("--var", required=True, help="按它与常数比较来分派的变量名(从本机反编译文本里读出)")
    ap.add_argument("--out")
    ap.add_argument("--show")
    ap.add_argument("--switch", action="store_true", help="按 switch/case 切分(行为执行函数)")
    a = ap.parse_args()
    check_path(a.src)
    if a.out:
        check_path(a.out)
    lines = open(a.src, encoding="utf-8").read().split("\n")
    v = re.escape(a.var)
    spans = []  # (types, start, end)
    if not a.switch:
        pat = re.compile(r"^\s*(?:\} else |else )?if \((?P<cond>\(?%s == (?:0x[0-9a-f]+|\d+)\)?(?: \|\| \(%s == (?:0x[0-9a-f]+|\d+)\))*)\) \{$" % (v, v))
        for i, ln in enumerate(lines):
            m = pat.match(ln)
            if m:
                types = [int(x, 16) if x.startswith("0x") else int(x) for x in re.findall(r"== (0x[0-9a-f]+|\d+)", m.group("cond"))]
                spans.append((types, i, block_end(lines, i)))
    else:
        case = re.compile(r"^\s*case (0x[0-9a-f]+|\d+):$")
        idx = [(i, int(m.group(1), 16) if m.group(1).startswith("0x") else int(m.group(1)))
               for i, ln in enumerate(lines) for m in [case.match(ln)] if m]
        for k, (i, t) in enumerate(idx):
            j = idx[k + 1][0] - 1 if k + 1 < len(idx) else len(lines) - 1
            spans.append(([t], i, j))
    if a.show:
        want = int(a.show, 16) if a.show.startswith("0x") else int(a.show)
        for types, s, e in spans:
            if want in types:
                print("// 类型 %s 行 %d-%d" % (",".join(hex(t) for t in types), s + 1, e + 1))
                print("\n".join(lines[s:e + 1]))
        return
    rows = []
    for types, s, e in spans:
        body = "\n".join(lines[s:e + 1])
        params = sorted({int(x, 0) for x in re.findall(r"(?:" + PARAM_ACCESSOR + r")\((?:\w+),(\w+)\)", body) if re.fullmatch(r"0x[0-9a-f]+|\d+", x)})
        slots = sorted(set(re.findall(r"\+ (?:0x[0-9a-f]+|\d+)/\*([^*]+)\*/", body)))
        funcs = sorted(set(re.findall(r"FUN_([0-9a-f]{8})", body)))
        strs = sorted(set(re.findall(r'"((?:[^"\\]|\\.){4,})"', body)))
        rows.append((",".join(hex(t) for t in types), e - s + 1, params, slots, funcs, strs))
    out = open(a.out, "w", encoding="utf-8") if a.out else sys.stdout
    out.write("types\tlines\trec_params\tslots\tfuncs\tstrings\n")
    for t, n, p, sl, fn, st in rows:
        out.write("%s\t%d\t%s\t%s\t%s\t%s\n" % (t, n, ",".join(map(str, p)), ",".join(sl), ",".join(fn[:12]), " | ".join(st[:3])))
    if a.out:
        out.close()
        print("段数", len(rows), "写入", a.out)


if __name__ == "__main__":
    main()
