#!/usr/bin/env python3
"""给每个原生接口的客户端实现函数分类(读 Ghidra 导出的反汇编,不入库):

  vthunk    只有"取虚函数表、跳到第 N 个槽"(带或不带空指针检查):语义在虚函数里,这里只给槽号
  getter    只读 this 的一个字段并返回
  setter    只写 this 的一个字段
  forward   一条 JMP/CALL 转到另一个函数
  small     小函数(≤ 16 条指令),列出调用与字段偏移
  large     其余

输出 TSV:`address, size, ninstr, kind, detail`,以及把名字拼上去的 `--names` 视图。
只读本机产物 re/notes/natives/*.asm.txt 与 re/notes/sq_registry.tsv;输出写到 out/(不入库)。

用法:python3 tools/py/native_kinds.py [--out out/native_impl_kinds.tsv]
"""
import argparse
import csv
import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
NAT = os.path.join(ROOT, "re", "notes", "natives")

INS = re.compile(r"^([0-9a-f]{8})\s+([0-9a-f]+)\s+(.*)$")


def read_asm(addr):
    path = os.path.join(NAT, addr + ".asm.txt")
    if not os.path.exists(path):
        return None
    out = []
    size = 0
    for ln in open(path, encoding="utf-8").read().splitlines():
        if ln.startswith(";"):
            m = re.search(r"大小 (\d+) 字节", ln)
            if m:
                size = int(m.group(1))
            continue
        m = INS.match(ln)
        if m:
            out.append(m.group(3).strip())
    return size, out


def classify(ins):
    """返回 (kind, detail)。"""
    n = len(ins)
    text = " ; ".join(ins)
    # 去掉 PUSH EBP / MOV EBP,ESP / POP EBP 这类栈帧样板
    core = [i for i in ins if i not in ("PUSH EBP", "MOV EBP,ESP", "POP EBP")]
    vslots = []
    for i in ins:
        m = re.search(r"(?:JMP|CALL) dword ptr \[(E[A-Z]{2}) \+ (0x[0-9a-f]+)\]", i)
        if m:
            vslots.append(m.group(2))
    # 先找"MOV reg,[EAX + 0xNN]; ... JMP/CALL reg"的写法
    for k, i in enumerate(ins):
        m = re.search(r"MOV (E[A-Z]{2}),dword ptr \[(E[A-Z]{2}) \+ (0x[0-9a-f]+)\]$", i)
        if m and k > 0 and re.search(r"MOV E[A-Z]{2},dword ptr \[E[A-Z]{2}\]$", ins[k - 1]):
            tail = ins[k + 1:]
            if any(re.search(r"(JMP|CALL) " + m.group(1), t) for t in tail):
                vslots.append(m.group(3))
    calls = [re.search(r"CALL (0x[0-9a-f]+)", i).group(1) for i in ins if re.search(r"CALL 0x[0-9a-f]+", i)]
    jmps = [re.search(r"JMP (0x[0-9a-f]+)", i).group(1) for i in ins if re.search(r"^JMP 0x[0-9a-f]+", i)]
    fields = []
    for i in ins:
        for m in re.finditer(r"\[ECX(?: \+ (0x[0-9a-f]+))?\]", i):
            fields.append(m.group(1) or "0x0")
    if vslots and n <= 14 and not calls:
        guard = "null->-1" if "OR EAX,0xffffffff" in text and "TEST ECX,ECX" in text else ""
        return "vthunk", ",".join(sorted(set(vslots))) + (" " + guard if guard else "")
    if n <= 4 and len(core) == 2 and re.match(r"MOV EAX,dword ptr \[ECX(?: \+ 0x[0-9a-f]+)?\]$", core[0]) and core[1].startswith("RET"):
        return "getter", fields[0] if fields else "0x0"
    if n <= 5 and any(re.match(r"MOV dword ptr \[ECX(?: \+ 0x[0-9a-f]+)?\],", i) for i in ins) and ins[-1].startswith("RET") and not calls:
        return "setter", fields[0] if fields else "0x0"
    if len(core) == 1 and re.match(r"JMP 0x", core[0]):
        return "forward", core[0].split()[1]
    if n <= 16:
        return "small", "calls=%s vslots=%s fields=%s" % (",".join(sorted(set(calls))), ",".join(sorted(set(vslots))), ",".join(sorted(set(fields))))
    return "large", "calls=%d vslots=%s" % (len(calls), ",".join(sorted(set(vslots))))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(ROOT, "out", "native_impl_kinds.tsv"))
    a = ap.parse_args()
    reg = csv.DictReader(open(os.path.join(ROOT, "re", "notes", "sq_registry.tsv"), newline="\n", encoding="utf-8"), delimiter="\t")
    names = defaultdict(set)
    impls = set()
    for r in reg:
        if r["impl"]:
            impls.add(r["impl"])
            names[r["impl"]].add(r["name"])
    rows = []
    miss = 0
    for addr in sorted(impls):
        got = read_asm(addr)
        if got is None:
            miss += 1
            continue
        size, ins = got
        kind, detail = classify(ins)
        rows.append((addr, size, len(ins), kind, detail, ",".join(sorted(names[addr]))))
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("address\tsize\tninstr\tkind\tdetail\tnames\n")
        for r in rows:
            f.write("\t".join(str(x) for x in r) + "\n")
    from collections import Counter
    c = Counter(r[3] for r in rows)
    print("impl 函数 %d 个(缺导出 %d);分类:%s" % (len(rows), miss, dict(c)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
