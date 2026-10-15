#!/usr/bin/env python3
"""给服务端函数的反汇编(DumpFunction 的 .asm.txt)加注释,只读:

  * `CALL 0x地址`  -> 符号名(来自 re/server/symbols/all_nm_demangled.txt,已反修饰)
  * 指向 .rodata 的立即数/内存操作数 -> 字符串、浮点或整数常量
  * 其他指向有符号数据的立即数 -> 符号名

用法:
    python3 tools/py/srv_annotate.py <输入 .asm.txt> [输出文件] [--from 起始地址] [--to 结束地址]

没有输出文件就写到标准输出。输出只放 re/ 或 out/(已被 .gitignore 排除),不提交。
这是静态读取:不运行服务端,只读它的 ELF 文件。
"""
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from read_elf_va import Elf32, ROOT  # noqa: E402

SYMBOLS = os.path.join(ROOT, "re", "server", "symbols", "all_nm_demangled.txt")
LINE = re.compile(r"^([0-9a-f]{8})  ([0-9a-f]+)\s+(\S.*)$")
HEX = re.compile(r"0x([0-9a-f]{5,8})\b")
CALL = re.compile(r"^(CALL|JMP)\s+0x([0-9a-f]{8})$")

_PREF = {"T": 0, "W": 1, "t": 2, "w": 3, "D": 4, "R": 5, "B": 6, "V": 7, "d": 8, "r": 9, "b": 10, "u": 11}


class Symbols:
    """地址 -> 名字(同一地址有多个名字时,优先全局的、非 .L 局部标签的)。"""

    def __init__(self, path=SYMBOLS):
        best = {}
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw in f:
                parts = raw.rstrip("\n").split(" ", 2)
                if len(parts) < 3 or len(parts[0]) != 8:
                    continue
                try:
                    addr = int(parts[0], 16)
                except ValueError:
                    continue
                kind, name = parts[1], parts[2]
                if name.startswith(".L") or not name:
                    continue
                rank = _PREF.get(kind, 20)
                old = best.get(addr)
                if old is None or rank < old[0]:
                    best[addr] = (rank, name)
        self.by_addr = {a: n for a, (_, n) in best.items()}

    def get(self, addr):
        return self.by_addr.get(addr)


def printable(s):
    return len(s) >= 1 and all(32 <= ord(c) < 127 or c in "\t\n" for c in s)


def describe_const(elf, syms, va, text):
    """给一个地址常量写注释:符号名优先;否则按指令的宽度猜浮点/整数/字符串。"""
    name = syms.get(va)
    if name:
        return name
    try:
        if "qword ptr" in text:
            return "double %r" % struct.unpack("<d", elf.read(va, 8))[0]
        if "dword ptr" in text and any(op in text for op in ("FLD", "FMUL", "FADD", "FSUB", "FDIV", "FCOM", "FILD")):
            raw = elf.read(va, 4)
            if text.startswith("FILD"):
                return "int %d" % struct.unpack("<i", raw)[0]
            return "float %r" % struct.unpack("<f", raw)[0]
        s = elf.cstr(va, 120)
        if len(s) >= 2 and printable(s):
            return "%r" % s
        raw = elf.read(va, 4)
        return "dword 0x%08x" % struct.unpack("<I", raw)[0]
    except ValueError:
        return None


def annotate_line(elf, syms, text):
    note = None
    m = CALL.match(text)
    if m:
        addr = int(m.group(2), 16)
        n = syms.get(addr)
        if n:
            note = n
    else:
        for h in HEX.finditer(text):
            va = int(h.group(1), 16)
            # 服务端:代码 0x08048000..0x09000000 之外的小立即数不是地址;.rodata/.data 在 0x08e00000 之后
            if va < 0x08048000 or va > 0x0a000000:
                continue
            n = describe_const(elf, syms, va, text)
            if n:
                note = n
                break
    return note


def main(argv):
    if not argv:
        print(__doc__)
        return 1
    src = argv[0]
    out_path = None
    lo, hi = None, None
    i = 1
    while i < len(argv):
        a = argv[i]
        if a == "--from":
            lo = int(argv[i + 1], 16)
            i += 2
        elif a == "--to":
            hi = int(argv[i + 1], 16)
            i += 2
        else:
            out_path = a
            i += 1
    elf = Elf32()
    syms = Symbols()
    out = open(out_path, "w", encoding="utf-8") if out_path else sys.stdout
    try:
        with open(src, encoding="utf-8", errors="replace") as f:
            for raw in f:
                raw = raw.rstrip("\n")
                m = LINE.match(raw)
                if not m:
                    if lo is None:
                        out.write(raw + "\n")
                    continue
                addr = int(m.group(1), 16)
                if lo is not None and addr < lo:
                    continue
                if hi is not None and addr >= hi:
                    break
                text = m.group(3)
                note = annotate_line(elf, syms, text)
                if note:
                    out.write("%s  ; %s\n" % (raw, note))
                else:
                    out.write(raw + "\n")
    finally:
        if out_path:
            out.close()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
