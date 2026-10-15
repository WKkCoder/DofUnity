#!/usr/bin/env python3
"""把客户端 `.atk` 加载函数(入口 0x012d9d40)里"标签 → 编号 → 处理块 → 写入的对象偏移"对出来。

背景(见 docs/evidence/ledger.md 的 E-057):这个函数的写法是先把每个标签的字符串与一个编号登记到一张表里,
之后读到标签就按编号分派:编号 ≤ 0xfe 走"字节索引表 + 地址表",0x102 单独一条分支,≥ 0x106 走"减 0x106 的跳转表"。
每个分派入口只有一条 "CMP 标志,0 / JNZ 处理块" 的开头,所以处理块地址就是入口里 JNZ 的目标。
处理块里每次写字段都是 "LEA ECX,[ESI + 偏移] / CALL 写入函数"(0x011a0930 写受保护值,0x00404160 写普通整数)。

输入:
  1. `AsmRange.java` 对 0x012d9d40..0x012dc000 的汇编输出(默认 out/hit_work/atk_loader_full.asm)
       analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
         -scriptPath tools/ghidra -postScript AsmRange.java out/hit_work/atk_loader_full.asm 012d9d40 012dc000 20000
  2. re/notes/strings_decoded.tsv(解码后的字符串表,用来把字符串地址换成标签名)
  3. 磁盘上的跳转表(经 tools/py/read_va.py 读取,只读,不涉及受保护区域)

输出:TSV(标签、编号、处理块地址、该处理块里写到的对象偏移列表)。只含地址、标签名与偏移,不含反编译文本。
这些偏移是"客户端 CAttackInfo 结构"的字段位置;服务端的偏移不同(E-055),不要混用。

用法: python3 tools/py/atk_loader_tags.py [--asm out/hit_work/atk_loader_full.asm] [--out out/atk_client_offsets.tsv]
"""
import argparse
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# 这个函数的固定地址(从汇编读出):分派部分
ID_SPECIAL = 0x102            # 比较树里单独处理的编号
SPECIAL_HANDLER = 0x012da4bd  # 编号 0x102 的入口
BYTE_INDEX_TABLE = 0x012dc018  # 编号 ≤ 0xfe:按编号取一个字节作为地址表下标
LOW_HANDLERS = 0x012dbfd8      # 上面那张地址表(16 项,最后一项是"忽略")
LOW_MAX = 0xfe
HIGH_BASE = 0x106              # 编号 ≥ 0x106:先减 0x106
HIGH_INDEX_TABLE = 0x012dc17c  # 字节索引表(0xcd 项,看汇编里的 CMP EAX,0xcc)
HIGH_HANDLERS = 0x012dc118     # 地址表(25 项)
IGNORE = 0x012dbd59            # "忽略/回到循环"


def read_va(spec):
    py = os.path.join(ROOT, "tools", "py", "read_va.py")
    return subprocess.check_output([sys.executable, py, spec], cwd=ROOT).decode()


def read_dword(addr):
    out = read_va("d:%08x" % addr)
    return int(re.search(r"dword 0x([0-9a-f]+)", out).group(1), 16)


def read_bytes(addr, n):
    out = read_va("b:%08x:%d" % (addr, n)).replace(" ", "")
    return bytes.fromhex(re.search(r"([0-9a-f]{%d})" % (2 * n), out).group(1))


def load_asm(path):
    lines, order = {}, []
    for raw in open(path, encoding="utf-8"):
        m = re.match(r"^([0-9a-f]{8})\s+([0-9a-f]+)\s+(.*)$", raw.rstrip("\n"))
        if m:
            a = int(m.group(1), 16)
            lines[a] = m.group(3).strip()
            order.append(a)
    return lines, order


def load_tags(path):
    tags = {}
    for raw in open(path, encoding="utf-8"):
        p = raw.rstrip("\n").split("\t")
        if len(p) >= 5 and re.fullmatch(r"[0-9a-f]{8}", p[0]):
            tags[int(p[0], 16)] = p[4]
    return tags


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--asm", default=os.path.join(ROOT, "out", "hit_work", "atk_loader_full.asm"))
    ap.add_argument("--strings", default=os.path.join(ROOT, "re", "notes", "strings_decoded.tsv"))
    ap.add_argument("--out", default="")
    a = ap.parse_args()

    lines, order = load_asm(a.asm)
    pos = {addr: i for i, addr in enumerate(order)}
    tags = load_tags(a.strings)

    # 1. 登记表:"PUSH 字符串地址" 之后不远处的 "MOV [EBP+x],编号"
    id2tag = {}
    for i, addr in enumerate(order):
        m = re.match(r"PUSH 0x([0-9a-f]+)$", lines[addr])
        if not m or int(m.group(1), 16) not in tags:
            continue
        for b in order[i + 1:i + 16]:
            mm = re.search(r"MOV dword ptr \[EBP \+ 0x[0-9a-f]+\],0x([0-9a-f]+)$", lines[b])
            if mm:
                id2tag[int(mm.group(1), 16)] = tags[int(m.group(1), 16)]
                break

    # 2. 分派表
    low = [read_dword(LOW_HANDLERS + 4 * i) for i in range(16)]
    low_idx = read_bytes(BYTE_INDEX_TABLE, LOW_MAX + 1)
    n_high = (HIGH_INDEX_TABLE - HIGH_HANDLERS) // 4
    high = [read_dword(HIGH_HANDLERS + 4 * i) for i in range(n_high)]
    high_idx = read_bytes(HIGH_INDEX_TABLE, 0xcd)

    def handler(ident):
        if ident == ID_SPECIAL:
            return SPECIAL_HANDLER
        if ident <= LOW_MAX:
            return low[low_idx[ident]]
        k = ident - HIGH_BASE
        if 0 <= k < len(high_idx):
            return high[high_idx[k]]
        return None

    def body_of(h):
        if h is None or h == IGNORE or h not in pos:
            return None
        for addr in order[pos[h]:pos[h] + 4]:
            m = re.match(r"JNZ 0x([0-9a-f]+)", lines[addr])
            if m:
                return int(m.group(1), 16)
        return None

    writers = ("CALL 0x011a0930", "CALL 0x00404160")  # 受保护值写入 / 普通整数写入

    def offsets_in(body):
        # 处理块:从 body 开始,到第一条 "JMP 忽略地址" 为止。
        # 对每个写入函数调用,往回找最近一次给 ECX 赋值的指令:LEA ECX,[ESI + 偏移] 或 MOV ECX,ESI(偏移 0)。
        # 另外记下直接写 [ESI + 偏移] 的 MOV(标成 "m偏移")。
        offs = []
        i = pos[body]
        start = i
        while i < len(order):
            ins = lines[order[i]]
            if ins.startswith(writers):
                for back in range(1, 9):
                    prev = lines.get(order[i - back], "")
                    m = re.match(r"LEA ECX,\[ESI \+ 0x([0-9a-f]+)\]", prev)
                    if m:
                        offs.append("0x%x" % int(m.group(1), 16))
                        break
                    if prev == "MOV ECX,ESI":
                        offs.append("0x0")
                        break
            else:
                m = re.match(r"MOV (?:byte|word|dword) ptr \[ESI \+ 0x([0-9a-f]+)\],", ins)
                if m:
                    offs.append("m0x%x" % int(m.group(1), 16))
            if ins.startswith("JMP 0x%08x" % IGNORE) or ins.startswith("JMP 0x%x" % IGNORE):
                break
            i += 1
            if i - start > 400:
                break
        return offs

    rows = []
    for ident, tag in sorted(id2tag.items()):
        body = body_of(handler(ident))
        offs = offsets_in(body) if body in pos else []
        rows.append((tag, ident, body, offs))

    out = sys.stdout if not a.out else open(a.out, "w", encoding="utf-8")
    out.write("tag\tid\tbody\toffsets\n")
    for tag, ident, body, offs in rows:
        out.write("%s\t0x%x\t%s\t%s\n" % (
            tag, ident, ("0x%08x" % body) if body else "-", ",".join(offs)))
    if a.out:
        out.close()
        print("写出 %d 行 -> %s" % (len(rows), a.out))


if __name__ == "__main__":
    main()
