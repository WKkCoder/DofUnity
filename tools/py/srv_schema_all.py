#!/usr/bin/env python3
"""服务端各脚本类型的"标签 -> 读取形状"总表:从每个类型的加载函数出发,把它调用到的、同样引用标签的函数一起切开,
再与全库实际取值形状对账(只读静态)。

用法:
    python3 tools/py/srv_schema_all.py [输出目录,缺省 out/srv_schema]

输入:
  out/srv_loaders/<地址>.asm.txt 与 .xref.txt   DumpFunction.java(DNF_SKIP_DECOMPILE=1)的输出,
                                                覆盖 re/ghidra/out/server_tag_refs.tsv 里引用 2 个以上标签的函数
  out/stats_<扩展名>.json                       dnf script-stats --ext <扩展名> --json 的全库统计
输出(不入库):
  <ext>.tsv           这个类型的标签表(加载函数及其调用到的辅助函数的并集)
  schema_all.tsv      所有类型合并;可整理后入库 docs/server-tag-schemas.tsv
  summary.json        对账汇总
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
from read_elf_va import Elf32  # noqa: E402
from srv_annotate import Symbols  # noqa: E402
import srv_schema  # noqa: E402
from srv_schema_check import LOADERS, classify  # noqa: E402

LOADER_DIR = os.path.join(ROOT, "out", "srv_loaders")


def callees(addr):
    path = os.path.join(LOADER_DIR, addr + ".xref.txt")
    out = []
    if not os.path.exists(path):
        return out
    seen_header = False
    for line in open(path, encoding="utf-8", errors="replace"):
        if line.startswith("# 被调用者"):
            seen_header = True
            continue
        if seen_header:
            m = re.match(r"^([0-9a-f]{8})\b", line)
            if m:
                out.append(m.group(1))
    return out


def closure(root, available):
    seen = {root}
    stack = [root]
    while stack:
        a = stack.pop()
        for c in callees(a):
            if c in available and c not in seen:
                seen.add(c)
                stack.append(c)
    return seen


def simplify_steps(steps):
    """把步骤压成"偏移列表":ScanInt→+0xc 变成 I@0xc,其余调用名丢掉(保留是否含循环/条件)。"""
    out = []
    for st in steps:
        m = re.match(r"^(Scan\w*|scan\w*)→\+0x([0-9a-f]+)$", st)
        if m:
            out.append("%s@0x%s" % (srv_schema.shape_letter(m.group(1)), m.group(2)))
        elif st.startswith("(含循环)"):
            out.append("loop")
        elif st.startswith("(容器"):
            out.append("container")
    return " ".join(out)


def main(argv):
    out_dir = argv[0] if argv else os.path.join(ROOT, "out", "srv_schema")
    os.makedirs(out_dir, exist_ok=True)
    elf = Elf32()
    syms = Symbols()
    available = {f[:8] for f in os.listdir(LOADER_DIR) if f.endswith(".asm.txt")}
    cache = {}

    def rows_of(addr):
        if addr not in cache:
            cache[addr] = srv_schema.extract(os.path.join(LOADER_DIR, addr + ".asm.txt"), elf, syms)
        return cache[addr]

    print("%-4s %-30s %4s %6s | %7s %7s %7s %7s %7s" % (
        "类型", "加载函数(+辅助)", "函数", "覆盖%", "exact", "prefix", "loose", "differ", "unknown"))
    summary = {}
    all_rows = []
    for ext, addr, name in LOADERS:
        stats_path = os.path.join(ROOT, "out", "stats_%s.json" % ext)
        if addr not in available or not os.path.exists(stats_path):
            continue
        funcs = sorted(closure(addr, available))
        by_tag = {}
        out_rows = []
        for fa in funcs:
            for codes, tag, style, st, rs, end, shape, steps in rows_of(fa):
                if not tag:
                    continue
                key = tag.lower()
                # 同一个标签在加载函数本体与辅助函数里都出现时,以加载函数本体里的为主;
                # 其余按形状更长(信息更多)的为准
                prev = by_tag.get(key)
                entry = (fa, shape, steps, style, codes, st)
                if prev is None or (prev[0] != addr and fa == addr) or (
                        prev[0] != addr and len(shape) > len(prev[1])):
                    by_tag[key] = entry
                out_rows.append((fa, tag, style, shape, steps, codes, st))
        with open(os.path.join(out_dir, ext + ".tsv"), "w", encoding="utf-8") as f:
            f.write("func\\tcode\\ttag\\tstyle\\tstart\\tshape\\tsteps\\n".replace("\\t", "\t").replace("\\n", "\n"))
            for fa, tag, style, shape, steps, codes, st in out_rows:
                f.write("%s\t%s\t%s\t%s\t%08x\t%s\t%s\n" % (
                    fa, ",".join("0x%x" % c for c in codes), tag, style, st, shape, "; ".join(steps)))
        stats = json.load(open(stats_path, encoding="utf-8"))["tags"]
        total = covered = 0
        cls_count = {"exact": 0, "prefix": 0, "loose": 0, "differ": 0, "unknown": 0}
        uncovered = []
        worst = []
        for tag, info in stats.items():
            if tag.startswith("[/"):
                continue
            cnt = info["count"]
            total += cnt
            ent = by_tag.get(tag)
            if ent is None:
                uncovered.append((cnt, tag))
                continue
            covered += cnt
            tag_diff = 0
            for cshape, ccount in info["shapes"].items():
                c = classify(ent[1], cshape)
                cls_count[c] += ccount
                if c == "differ":
                    tag_diff += ccount
            if tag_diff:
                worst.append((tag_diff, tag, ent[1]))
            all_rows.append((ext, tag, ent[1], simplify_steps(ent[2]), ent[0]))
        uncovered.sort(reverse=True)
        worst.sort(reverse=True)
        cov = 100.0 * covered / total if total else 0.0
        denom = sum(cls_count.values()) or 1
        print("%-4s %-30s %4d %6.1f | %6.1f%% %6.1f%% %6.1f%% %6.1f%% %6.1f%%" % (
            ext, name, len(funcs), cov, *(100.0 * cls_count[k] / denom for k in ("exact", "prefix", "loose", "differ", "unknown"))))
        for cnt, tag in uncovered[:4]:
            print("       服务端没有 %-34s %d 次" % (tag, cnt))
        for cnt, tag, shape in worst[:3]:
            print("       不一致     %-34s 服务端 %s(%d 行)" % (tag, shape[:30], cnt))
        summary[ext] = {"loader": name, "functions": len(funcs), "coverage": round(cov, 2), "lines": cls_count,
                        "uncovered": [(t, c) for c, t in uncovered[:30]],
                        "worst": [(t, s, c) for c, t, s in worst[:15]]}
    with open(os.path.join(out_dir, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=1)
    with open(os.path.join(out_dir, "schema_all.tsv"), "w", encoding="utf-8") as f:
        f.write("ext\ttag\tshape\toffsets\tfunc\n")
        for ext, tag, shape, offs, fa in all_rows:
            f.write("%s\t%s\t%s\t%s\t%s\n" % (ext, tag, shape, offs, fa))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
