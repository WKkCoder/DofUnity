#!/usr/bin/env python3
"""按汇编地址区间,从 DumpFunction 导出的 `.cl.txt`(每行带"该行对应的最低指令地址")里切出一段反编译文本。

用途:CNRDAction::doBehavior 这类被反编译器拆乱了的长分派链,每个类型的处理块在汇编里是连续的一段地址,
但反编译出的 C 文本里这些行不一定相邻。按地址切比按文本结构切可靠。
处理块的区间 = [该类型的处理块起始地址, 链表里下一个更高的处理块起始地址)(处理块按源码顺序排列,这是上界估计,
可能多包含尾部公共代码;用 --end 手动指定)。
输入输出都必须在 re/ 或 out/ 下(含原版反编译文本,不入库)。

用法:
  python3 tools/py/cl_slice.py out/cl/010b56c0.cl.txt --chains re/notes/engine2/doBehavior.chains.tsv --type 0x6b [--out f]
  python3 tools/py/cl_slice.py FILE.cl.txt --range 010b8375 010b8616
  python3 tools/py/cl_slice.py FILE.cl.txt --chains C.tsv --all --outdir out/act_slices/beh   (每个类型一个文件)
选项:--drop-decl 去掉没有地址的声明行;默认保留没有地址的行里"只有大括号"的行,方便看缩进结构。
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def check_path(p):
    ap = os.path.abspath(p)
    if not any(ap.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
        sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下(含原版反编译文本,不入库)")


def read_cl(path):
    rows = []
    for ln in open(path, encoding="utf-8", errors="replace").read().split("\n"):
        if "\t" not in ln:
            rows.append((0, ln))
            continue
        a, t = ln.split("\t", 1)
        try:
            rows.append((int(a, 16), t))
        except ValueError:
            rows.append((0, ln))
    return rows


def slice_rows(rows, lo, hi):
    out = []
    for a, t in rows:
        if a and lo <= a < hi:
            out.append("%08x  %s" % (a, t))
    return out


ASM_LINE = re.compile(r"^([0-9a-f]{8})\s")


def slice_asm(path, lo, hi):
    out = []
    for ln in open(path, encoding="utf-8", errors="replace").read().split("\n"):
        m = ASM_LINE.match(ln)
        if m and lo <= int(m.group(1), 16) < hi:
            out.append(ln)
    return out


def load_chains(path):
    res = []
    for ln in open(path, encoding="utf-8").read().split("\n")[1:]:
        p = ln.split("\t")
        if len(p) >= 4 and p[1].startswith("0x"):
            res.append((int(p[1], 16), int(p[3], 16)))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cl")
    ap.add_argument("--chains")
    ap.add_argument("--type", action="append", default=[])
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--range", nargs=2)
    ap.add_argument("--end", help="手动指定区间终点(十六进制),只对单个 --type 有效")
    ap.add_argument("--out")
    ap.add_argument("--outdir")
    ap.add_argument("--asm", help="同一函数的 .asm.txt;给出后,--all 时每个类型再写一个 tXX.asm.txt(同一区间的汇编)")
    a = ap.parse_args()
    check_path(a.cl)
    rows = read_cl(a.cl)
    if a.range:
        lo, hi = int(a.range[0], 16), int(a.range[1], 16)
        text = slice_rows(rows, lo, hi)
        emit(a, text)
        return
    if not a.chains:
        sys.exit("需要 --chains 或 --range")
    check_path(a.chains)
    chains = load_chains(a.chains)
    starts = sorted({h for _, h in chains})
    by_type = {}
    for t, h in chains:
        by_type.setdefault(t, []).append(h)

    def end_of(h):
        nxt = [s for s in starts if s > h]
        return nxt[0] if nxt else h + 0x1000

    if a.all:
        if not a.outdir:
            sys.exit("--all 需要 --outdir")
        check_path(a.outdir)
        if a.asm:
            check_path(a.asm)
        os.makedirs(a.outdir, exist_ok=True)
        n = 0
        for t, hs in sorted(by_type.items()):
            text = []
            atext = []
            for h in hs:
                text.append("// 类型 0x%x 处理块 %08x 到 %08x" % (t, h, end_of(h)))
                text.extend(slice_rows(rows, h, end_of(h)))
                if a.asm:
                    atext.append("; 类型 0x%x 处理块 %08x 到 %08x" % (t, h, end_of(h)))
                    atext.extend(slice_asm(a.asm, h, end_of(h)))
            open(os.path.join(a.outdir, "t%02x.txt" % t), "w", encoding="utf-8").write("\n".join(text) + "\n")
            if a.asm:
                open(os.path.join(a.outdir, "t%02x.asm.txt" % t), "w", encoding="utf-8").write("\n".join(atext) + "\n")
            n += 1
        print("写入", n, "个类型到", a.outdir)
        return
    text = []
    for ts in a.type:
        t = int(ts, 16)
        for h in by_type.get(t, []):
            hi = int(a.end, 16) if a.end else end_of(h)
            text.append("// 类型 0x%x 处理块 %08x 到 %08x" % (t, h, hi))
            text.extend(slice_rows(rows, h, hi))
    emit(a, text)


def emit(a, text):
    s = "\n".join(text) + "\n"
    if a.out:
        check_path(a.out)
        open(a.out, "w", encoding="utf-8").write(s)
        print("写入", a.out, len(text), "行")
    else:
        sys.stdout.write(s)


if __name__ == "__main__":
    main()
