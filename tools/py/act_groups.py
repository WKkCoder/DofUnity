#!/usr/bin/env python3
"""从 importActionScript 的反汇编里提取"标签 -> 编号 -> 处理代码入口"的表(只读本机产物,非 REA 的静态测量)。

背景(见台账):加载函数把每一层的标签(动作文件顶层、[MOTION] 内、[TRIGGER] 内、[BEHAVIOR] 内的各条命令……)
各登记在一个函数内的静态字典里:每个字典有一个"已初始化"标志(1 字节全局变量)和一个映射对象(标志地址 + 8);
首次进入时按顺序执行"登记(标签字符串, 编号)",之后按编号分发到处理代码。每次登记前面有一个检查
`CMP byte [标志],0 ; JNZ <该标签的处理代码>`,所以:

    (标签, 编号)  <- 登记调用 CALL 登记函数 的前后文(字面量地址、编号立即数)
    处理代码入口  <- 紧挨在该登记之前的检查的 JNZ 目标

本脚本按字典(标志地址)分组输出 TSV:  group  flag  tag  id  insert_addr  body_addr
再对每个处理代码入口统计其区间里的读取调用(readInt/readFloat/readString/scanTag/值表达式/比较符)与
"记录类型号"(紧跟记录构造函数的第一个常量写入)。区间 = [入口, 同组按地址排序的下一个入口),是近似(共享的尾部会重复计入)。

输入:Ghidra 导出的反汇编文本(本机 re/ 下),strings_decoded.tsv(静态解码结果)。输出写到 out/ 或 re/(不入库)。

用法:python3 tools/py/act_groups.py <asm.txt> <输出.tsv> [--strings re/notes/strings_decoded.tsv]
"""
import argparse
import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# 加载函数里用到的辅助函数地址(名字是我们自己起的,见 tools/ghidra/names/act.tsv)
F_DECODE = 0x0119FEF0
F_INSERT = 0x00458BD0
F_READ_INT = 0x01223660
F_READ_FLOAT = 0x01223710
F_READ_INT_CHK = 0x012236E0
F_READ_STR = 0x012238C0
F_READ_STR2 = 0x01225150
F_SCAN_TAG = 0x01225210
F_SIGN = 0x0132FB50
F_EXPR = 0x01330240
F_REC_CTOR = 0x00536600
F_PUSH_INT = 0x00493770
F_LOG = 0x011A2CE0

INS = re.compile(r"^(?P<addr>[0-9a-f]{8})\s+(?P<bytes>[0-9a-f]+)\s+(?P<text>.+?)\s*$")


def load_asm(path):
    out = []
    for ln in open(path, encoding="utf-8"):
        m = INS.match(ln)
        if m:
            out.append((int(m.group("addr"), 16), m.group("text")))
    return out


def load_strings(path):
    d = {}
    for ln in open(path, encoding="utf-8"):
        p = ln.rstrip("\n").split("\t")
        if len(p) >= 5 and p[0] != "blob_va":
            d[int(p[0], 16)] = p[4]
    return d


def imm(tok):
    tok = tok.strip()
    return int(tok, 16) if tok.lower().startswith(("0x", "-0x")) else int(tok)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("asm")
    ap.add_argument("out")
    ap.add_argument("--strings", default=os.path.join(ROOT, "re", "notes", "strings_decoded.tsv"))
    a = ap.parse_args()
    for p in (a.asm, a.out):
        ap_ = os.path.abspath(p)
        if not any(ap_.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
            sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下")

    ins = load_asm(a.asm)
    strs = load_strings(a.strings)
    idx = {addr: i for i, (addr, _) in enumerate(ins)}

    inserts = []  # (i, tag, id, map_addr)
    for i, (addr, text) in enumerate(ins):
        if text != "CALL 0x%08x" % F_INSERT and text != "CALL 0x%x" % F_INSERT:
            continue
        lo = max(0, i - 16)
        window = ins[lo:i]
        mapaddr = None
        lit = None
        mov_ids = []
        for _, t in window:
            m = re.match(r"MOV ECX,0x([0-9a-f]+)$", t)
            if m:
                mapaddr = int(m.group(1), 16)
            m = re.match(r"PUSH 0x([0-9a-f]+)$", t)
            if m:
                lit = int(m.group(1), 16)
            m = re.match(r"MOV dword ptr \[EBP \+ 0x[0-9a-f]+\],(0x[0-9a-f]+|-?\d+)$", t)
            if m:
                mov_ids.append(imm(m.group(1)))
        if mapaddr is None or lit is None or not mov_ids:
            continue
        # 字面量地址取窗口里最后一个 PUSH imm(紧挨着 CALL 解码函数)
        tag = strs.get(lit)
        inserts.append((i, tag, mov_ids[-1], mapaddr, lit))

    rows = []
    for i, tag, ident, mapaddr, lit in inserts:
        flag = mapaddr - 8 if False else None
        # 标志地址:映射对象 = 标志地址 + 8(从数据看 flag=0x01da1f30,map=0x01da1f38)
        flag = mapaddr - 8
        # 往前找最近的 CMP byte [flag],0 ; JNZ target
        body = None
        for j in range(i - 1, max(0, i - 40), -1):
            t = ins[j][1]
            if t == "CMP byte ptr [0x%08x],0x0" % flag:
                if j + 1 < len(ins):
                    m = re.match(r"JNZ 0x([0-9a-f]+)$", ins[j + 1][1])
                    if m:
                        body = int(m.group(1), 16)
                break
        rows.append((flag, tag, ident, ins[i][0], body))

    # 每个处理区间的读取统计
    by_group = defaultdict(list)
    for r in rows:
        by_group[r[0]].append(r)
    all_bodies = sorted({r[4] for r in rows if r[4] is not None})

    def region_end(b):
        k = all_bodies.index(b)
        return all_bodies[k + 1] if k + 1 < len(all_bodies) else ins[-1][0] + 1

    names = {F_READ_INT: "I", F_READ_FLOAT: "F", F_READ_INT_CHK: "I?", F_READ_STR: "S", F_READ_STR2: "S2",
             F_SCAN_TAG: "T", F_SIGN: "G", F_EXPR: "E", F_LOG: "!"}

    def scan_region(b):
        end = region_end(b)
        seq = []
        types = []
        rec_ctor_seen = False
        i0 = idx.get(b)
        if i0 is None:
            return "", ""
        k = i0
        while k < len(ins) and ins[k][0] < end:
            t = ins[k][1]
            m = re.match(r"CALL 0x([0-9a-f]+)$", t)
            if m:
                tgt = int(m.group(1), 16)
                if tgt in names:
                    seq.append(names[tgt])
                if tgt == F_REC_CTOR:
                    rec_ctor_seen = True
            else:
                if rec_ctor_seen:
                    mm = re.match(r"MOV dword ptr \[EBP \+ 0x[0-9a-f]+\],(0x[0-9a-f]+|-?\d+)$", t)
                    if mm:
                        types.append(imm(mm.group(1)))
                        rec_ctor_seen = False
            k += 1
        return "".join(x if len(x) == 1 else "(" + x + ")" for x in seq), ",".join(hex(t) for t in types[:6])

    with open(a.out, "w", encoding="utf-8") as f:
        f.write("flag\ttag\tid\tinsert\tbody\treads\trec_types\n")
        for flag in sorted(by_group):
            for (fl, tag, ident, insaddr, body) in sorted(by_group[flag], key=lambda r: r[3]):
                reads, types = ("", "")
                if body is not None:
                    reads, types = scan_region(body)
                f.write("%08x\t%s\t0x%x\t%08x\t%s\t%s\t%s\n" % (
                    fl, tag if tag is not None else "?", ident, insaddr,
                    ("%08x" % body) if body is not None else "-", reads, types))
    print("登记 %d 条,分 %d 个字典" % (len(rows), len(by_group)))
    for flag in sorted(by_group):
        print("  标志 %08x: %d 个标签" % (flag, len(by_group[flag])))


if __name__ == "__main__":
    main()
