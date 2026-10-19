#!/usr/bin/env python3
"""把"一个脚本加载函数"的汇编清单按标签切段,列出每个标签的处理段写了对象的哪些字段。

背景:客户端的各种脚本加载函数(例如 .chr 的加载函数)是一长串"取标签字符串、比较、读值、写字段"。
字段没有名字,只有偏移;要知道 [jump power] 之类的标签落在对象的哪个位移,就逐段看它的写入。

输入(只在本机 re/ 或 out/ 下读写,内含原版代码文本,不入库):
  * DumpFunction.java 产出的 <地址>.asm.txt
  * re/notes/strings_decoded.tsv(客户端文字块解码表:blob_va、宽窄、调用点数、类型、文本)
做法:
  1. 找出每个 `PUSH <blob>` 紧跟 `CALL <字符串访问函数>` 的位置,作为一段的起点;
  2. 一段延伸到下一个起点或函数里的汇合点(`--join`,默认不用),只看这一段里
       - 往 `[基址 + 位移]` 写的指令(基址限 EBX/EDI/ESI/EAX/ECX/EDX,不含栈帧),
       - 取 `LEA 寄存器,[基址 + 位移]`(受保护整数的写入函数以"字段地址"为参数),
       - 调用的函数地址、读到的常数地址;
  3. 输出 TSV:标签文本、段起点、写入、LEA、调用、常数。

用法:
  python3 tools/py/loader_stores.py re/notes/hero/012a6fc0.asm.txt --out re/notes/hero/chr_stores.tsv
  python3 tools/py/loader_stores.py re/notes/hero/012a6fc0.asm.txt --tag "jump power" --tag "move speed"
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# 客户端的字符串访问函数(E-041):0x0119FEF0 取宽字符串块,0x0119FED0 取窄字符串块
STRING_ACCESSORS = ("0x0119fef0", "0x0119fed0")

LINE = re.compile(r"^([0-9a-f]{8})\s{2}([0-9a-f]+)\s+(\S+)\s*(.*)$")
MEM = re.compile(r"\[(EAX|EBX|ECX|EDX|ESI|EDI) \+ (0x[0-9a-f]+)\]")
CONST_MEM = re.compile(r"\[(0x[0-9a-f]{6,8})\]")


def check_path(p):
    ap = os.path.abspath(p)
    if not any(ap.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
        sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下(含原版文本,不入库)")


def load_strings(path):
    out = {}
    with open(path, encoding="utf-8") as f:
        next(f)
        for ln in f:
            parts = ln.rstrip("\n").split("\t", 4)
            if len(parts) < 5:
                continue
            blob, text = parts[0], parts[4]
            # 第一列可能带行号前缀(grep 输出);这里只取最后的 8 位十六进制
            blob = blob.split(":")[-1].lower().zfill(8)
            out[blob] = text
    return out


def parse_asm(path):
    rows = []
    with open(path, encoding="utf-8") as f:
        for ln in f:
            m = LINE.match(ln.rstrip("\n"))
            if m:
                rows.append((m.group(1), m.group(3), m.group(4)))
    return rows


def segments(rows, strings):
    """返回 [(标签文本, 起点地址, 该段指令列表)]。"""
    starts = []
    for i in range(len(rows) - 1):
        addr, mn, op = rows[i]
        if mn != "PUSH" or not op.startswith("0x"):
            continue
        a2, mn2, op2 = rows[i + 1]
        if mn2 == "CALL" and op2.lower() in STRING_ACCESSORS:
            blob = op.lower()[2:].zfill(8)
            if blob in strings:
                starts.append((i, blob))
    segs = []
    for k, (i, blob) in enumerate(starts):
        j = starts[k + 1][0] if k + 1 < len(starts) else len(rows)
        segs.append((strings[blob], rows[i][0], rows[i:j]))
    return segs


def summarize(seg_rows):
    stores, leas, calls, consts = [], [], [], []
    for addr, mn, op in seg_rows:
        m_all = MEM.findall(op)
        if mn.startswith("MOV") and "," in op:
            dest = op.split(",", 1)[0]
            md = MEM.search(dest)
            if md:
                width = "w" if dest.startswith("word") else "b" if dest.startswith("byte") else "d"
                stores.append("%s[%s+%s]/%s" % (addr[-4:], md.group(1), md.group(2), width))
        elif mn == "LEA" and m_all:
            leas.append("%s[%s+%s]" % (addr[-4:], m_all[0][0], m_all[0][1]))
        elif mn == "CALL":
            calls.append(op)
        cm = CONST_MEM.search(op)
        if cm and mn not in ("CALL", "PUSH"):
            consts.append(cm.group(1))
    return stores, leas, calls, consts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("asm")
    ap.add_argument("--strings", default=os.path.join(ROOT, "re/notes/strings_decoded.tsv"))
    ap.add_argument("--out")
    ap.add_argument("--tag", action="append", help="只列文本包含该子串的标签(可多次)")
    a = ap.parse_args()
    check_path(a.asm)
    check_path(a.strings)
    if a.out:
        check_path(a.out)

    strings = load_strings(a.strings)
    rows = parse_asm(a.asm)
    segs = segments(rows, strings)
    lines = ["tag\tstart\tstores\tleas\tcalls\tconsts"]
    for text, start, seg_rows in segs:
        if a.tag and not any(t in text for t in a.tag):
            continue
        stores, leas, calls, consts = summarize(seg_rows)
        lines.append("\t".join([
            text, start,
            " ".join(stores[:8]), " ".join(leas[:6]),
            " ".join(dict.fromkeys(calls)), " ".join(dict.fromkeys(consts)),
        ]))
    out = "\n".join(lines) + "\n"
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(out)
        print("写出 %d 段 -> %s" % (len(lines) - 1, a.out))
    else:
        sys.stdout.write(out)


if __name__ == "__main__":
    main()
