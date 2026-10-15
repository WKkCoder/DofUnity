#!/usr/bin/env python3
"""在 DNF.exe 的数据节里找虚函数表:连续的 32 位小端字,每个都指向主代码段。只读磁盘字节,不运行。

客户端没有 RTTI(只有 std::tr1 的引用计数类型名泄露了几个类名),所以虚表没有名字。
这里只找"形状":起始地址与长度(槽数)。再用原生接口注册表里的槽号(例如 sq_GetXPos 是 0xA4)
去对照各虚表在该槽放的函数,从而知道一个槽在不同类里的实现。

用法:python3 tools/py/vtable_scan.py [--min 24] [--out re/notes/vtables.tsv]
DNF.exe 的路径取自 local.env 的 DNF_DIR。输出放 re/(不入库)。
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from decode_strings import Pe  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def exe_path():
    d = None
    for ln in open(os.path.join(ROOT, "local.env"), encoding="utf-8"):
        if ln.startswith("DNF_DIR="):
            d = ln.split("=", 1)[1].strip().strip("'\"")
    for p in (os.path.join(d, "DNF.exe"), os.path.join(d, "DNF", "DNF.exe")):
        if os.path.exists(p):
            return p
    raise SystemExit("找不到 DNF.exe(检查 local.env 的 DNF_DIR)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--min", type=int, default=24)
    ap.add_argument("--out", default=os.path.join(ROOT, "re", "notes", "vtables.tsv"))
    a = ap.parse_args()
    pe = Pe(exe_path())
    base = pe.image_base
    code_lo, code_hi = 0x401000, 0x155A000  # nsn#0 的虚拟地址范围
    runs = []
    for name, rva, vsize, roff, rsize in pe.sections:
        va0 = base + rva
        # 只扫数据节(不是 0x401000 起的主代码节)
        if va0 == 0x401000:
            continue
        n = min(vsize, rsize)
        pe.f.seek(roff)
        data = pe.f.read(n)
        i = 0
        while i + 4 <= len(data):
            start = i
            while i + 4 <= len(data):
                v = struct.unpack_from("<I", data, i)[0]
                if not (code_lo <= v < code_hi):
                    break
                i += 4
            length = (i - start) // 4
            if length >= a.min:
                runs.append((va0 + start, length))
            i = max(i + 4, start + 4) if length == 0 else i + 4
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("start\tslots\n")
        for s, n in runs:
            f.write("%08x\t%d\n" % (s, n))
    print("找到 %d 个候选虚表(槽数 ≥ %d)" % (len(runs), a.min))
    from collections import Counter
    buckets = Counter()
    for _s, n in runs:
        buckets[(n // 50) * 50] += 1
    for k in sorted(buckets):
        print("  槽数 %4d-%4d: %d 个" % (k, k + 49, buckets[k]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
