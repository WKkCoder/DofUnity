#!/usr/bin/env python3
"""从函数汇编里提取"按类型码比较的链":  CMP 寄存器,立即数 ; JNZ 下一个比较  ——  条件成立时顺着往下执行就是该类型的处理块。

用于 CNRDAction::doBehavior 这类被反编译器拆乱了的长分派链(控制流图里的汇编比反编译文本可靠)。
输入:tools/ghidra/DumpFunction.java 导出的 .asm.txt(只读本机 re/ 或 out/ 下的文件);输出 TSV:
  类型码、比较地址、处理块起始地址、下一个比较地址。
用法:python3 tools/py/asm_chain.py re/notes/engine2/010b56c0.asm.txt --start 010b58fa --reg ECX [--out file.tsv]
      python3 tools/py/asm_chain.py FILE.asm.txt --auto [--min-len 4]   (自动找出全部链)
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LINE = re.compile(r"^([0-9a-f]{8})\s+([0-9a-f]+)\s+(.*)$")


def check_path(p):
    ap = os.path.abspath(p)
    if not any(ap.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
        sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下(含原版汇编,不入库)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("asm")
    ap.add_argument("--start", help="链的第一个 CMP 的地址(十六进制);与 --auto 二选一")
    ap.add_argument("--auto", action="store_true", help="从函数里所有尚未被覆盖的比较出发,找出全部链")
    ap.add_argument("--min-len", type=int, default=4, help="--auto 时只保留不短于这个长度的链")
    ap.add_argument("--reg", default="ECX")
    ap.add_argument("--out")
    a = ap.parse_args()
    if not a.start and not a.auto:
        sys.exit("需要 --start 或 --auto")
    check_path(a.asm)
    if a.out:
        check_path(a.out)
    ins = {}
    order = []
    for ln in open(a.asm, encoding="utf-8"):
        m = LINE.match(ln.rstrip("\n"))
        if not m:
            continue
        addr = int(m.group(1), 16)
        ins[addr] = (len(m.group(2)) // 2, m.group(3).strip())
        order.append(addr)
    nxt = {order[i]: order[i + 1] for i in range(len(order) - 1)}
    # 比较有两种写法:CMP 寄存器,立即数  或  CMP dword ptr [EAX],立即数(前面是 CALL 迭代器解引用)
    cmp_re = re.compile(r"^CMP (?:%s|dword ptr \[EAX\]),(0x[0-9a-f]+|-?\d+)$" % re.escape(a.reg))
    jnz_re = re.compile(r"^JNZ 0x([0-9a-f]+)$")
    jz_re = re.compile(r"^JZ 0x([0-9a-f]+)$")
    def follow(cur, seen):
        rows = []
        while cur in ins and cur not in seen:
            seen.add(cur)
            size, text = ins[cur]
            m = cmp_re.match(text)
            if not m:
                break
            j = nxt.get(cur)
            # 编译器可能在 CMP 与跳转之间塞几条不改标志位的指令(LEA/MOV)
            for _ in range(3):
                if j is None or j not in ins or jnz_re.match(ins[j][1]) or jz_re.match(ins[j][1]) or not ins[j][1].startswith(("LEA", "MOV")):
                    break
                j = nxt.get(j)
            if j is None or j not in ins:
                break
            mj = jnz_re.match(ins[j][1])
            mz = jz_re.match(ins[j][1])
            if mj:  # 不相等就跳到下一个比较;顺着往下是这个类型的处理块
                handler = nxt.get(j)
                target = int(mj.group(1), 16)
            elif mz:  # 相等就跳到(可能被多个类型共用的)处理块;顺着往下是下一个比较
                handler = int(mz.group(1), 16)
                target = nxt.get(j)
            else:
                break
            rows.append((int(m.group(1), 0), cur, handler, target))
            # 下一段可能以"迭代器有效性检查"或"取迭代器元素"开头,跳过它们找到下一个 CMP
            probe = target
            for _ in range(12):
                if probe not in ins:
                    probe = None
                    break
                t = ins[probe][1]
                if cmp_re.match(t):
                    break
                if t.startswith(("TEST", "JZ", "JA ", "JNC", "JC ", "CMP", "MOV EDI,EDI", "LEA ECX", "CALL 0x00ce5640")):
                    probe = nxt.get(probe)
                    continue
                probe = None
                break
            if probe is None:
                break
            cur = probe
        return rows

    chains = []
    seen = set()
    if a.auto:
        for addr in order:
            if addr in seen or not cmp_re.match(ins[addr][1]):
                continue
            rows = follow(addr, seen)
            if len(rows) >= a.min_len:
                chains.append(rows)
    else:
        chains.append(follow(int(a.start, 16), seen))
    out = open(a.out, "w", encoding="utf-8") if a.out else sys.stdout
    out.write("chain\ttype\tcmp_addr\thandler\tnext_cmp\n")
    total = 0
    for k, rows in enumerate(chains):
        for t, c, h, n in rows:
            out.write("%d\t0x%x\t%08x\t%s\t%s\n" % (k, t, c, "%08x" % h if h else "", "%08x" % n if n else ""))
            total += 1
    if a.out:
        out.close()
        print("链", len(chains), "条,共", total, "个类型比较,写入", a.out)
    else:
        print("链", len(chains), "条,共", total, "个类型比较", file=sys.stderr)


if __name__ == "__main__":
    main()
