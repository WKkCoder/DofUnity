#!/usr/bin/env python3
"""从"脚本可调用的原生函数"的实现里归纳虚函数槽的含义(只读已导出的函数文本,不运行游戏)。

原理:原生函数(例如 getXPos 的实现)大多只是"取对象 → 调它的某个虚函数 → 返回",
所以实现里唯一的 `CALL/JMP [寄存器 + 偏移]`(尾调用写成 JMP)的偏移,就是这个虚函数的槽,脚本接口名可以当作槽的名字线索。

输入:re/notes/sq_registry.tsv(登记表,impl 列是实现地址)、re/notes/natives/<地址>.asm.txt(函数汇编)。
输出:re/notes/slot_names.tsv(槽、证据强度、候选名字)与 re/notes/slot_names.json。
证据强度:A = 函数里只有这一个槽调用(强);B = 函数里有 2 个槽调用(弱);更多不记录。

用法:python3 tools/py/slot_names.py
"""
import collections
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REG = os.path.join(ROOT, "re", "notes", "sq_registry.tsv")
NAT = os.path.join(ROOT, "re", "notes", "natives")
OUT = os.path.join(ROOT, "re", "notes", "slot_names.tsv")
OUTJ = os.path.join(ROOT, "re", "notes", "slot_names.json")
CALL = re.compile(r"(?:CALL|JMP)\s+dword ptr \[(E[A-Z]{2}) \+ 0x([0-9a-f]+)\]")


def main():
    names = collections.defaultdict(list)
    with open(REG, encoding="utf-8") as f:
        next(f)
        for ln in f:
            p = ln.rstrip("\n").split("\t")
            if len(p) < 6:
                continue
            for col in (3, 5):  # impl 与 register 两列都可能是实现地址
                a = p[col].strip()
                if a:
                    names[a.lstrip("0").zfill(8)].append(p[2])
    table = collections.defaultdict(lambda: collections.defaultdict(list))
    seen = 0
    for addr, nlist in names.items():
        path = os.path.join(NAT, addr + ".asm.txt")
        if not os.path.exists(path):
            continue
        seen += 1
        txt = open(path, encoding="utf-8").read()
        calls = [int(m.group(2), 16) for m in CALL.finditer(txt) if int(m.group(2), 16) >= 0x40]
        if len(calls) == 1:
            grade = "A"
        elif len(calls) == 2:
            grade = "B"
        else:
            continue
        for slot in set(calls):
            for n in nlist:
                table[slot][n].append(grade)
    rows = []
    for slot in sorted(table):
        cands = table[slot]
        a_names = sorted(n for n, g in cands.items() if "A" in g)
        b_names = sorted(n for n, g in cands.items() if "A" not in g)
        rows.append((slot, a_names, b_names))
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("slot\tstrong(A)\tweak(B)\n")
        for slot, a, b in rows:
            f.write("0x%x\t%s\t%s\n" % (slot, ",".join(a), ",".join(b[:12])))
    json.dump({hex(s): {"A": a, "B": b} for s, a, b in rows}, open(OUTJ, "w"), ensure_ascii=False, indent=1)
    print("扫描原生实现", seen, "个;有名字线索的槽", len(rows), "个,其中强证据", sum(1 for r in rows if r[1]), "个")


if __name__ == "__main__":
    main()
