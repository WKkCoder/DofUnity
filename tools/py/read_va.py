#!/usr/bin/env python3
"""按虚拟地址读 DNF.exe 磁盘字节里的常量(只读文件,不运行)。用来确认反汇编里引用的"全局常量"的值。

用法:python3 tools/py/read_va.py f:015620fc d:01a39e60 b:01562188:16 ...
  f:地址        32 位浮点        d:地址        32 位无符号整数        i:地址  32 位有符号整数
  q:地址        64 位浮点(double)    b:地址:长度  十六进制字节
只能读到磁盘上有内容的部分:运行时才被写入的全局变量(.data 里初值为 0 的那些)在这里读到的是文件里的初值,
不是运行时的值;不要把它当作运行时取值的证据。节的编号见 E-044:这里只读 #0(代码)、#1(数据块与常量表)、#2(全局变量)
和 .idata/.rsrc;#3 到 #8 是受保护区域与相邻的节,按用户决定不碰,地址落在那里就拒绝。
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from decode_strings import Pe  # noqa: E402
from vtable_scan import exe_path  # noqa: E402


def main(argv):
    if not argv:
        print(__doc__)
        return 1
    pe = Pe(exe_path())
    for spec in argv:
        parts = spec.split(":")
        kind, addr = parts[0], int(parts[1], 16)
        hit = pe.section_of(addr)
        if hit is None:
            print("%08x  不在任何节内" % addr)
            continue
        name = "节 #%d" % hit[0]
        if 3 <= hit[0] <= 8 and os.environ.get("DNF_ALLOW_PROTECTED") != "1":
            # 2026-10-15 用户授权逆向受保护区域(E-059);要读就显式设 DNF_ALLOW_PROTECTED=1,每次越界都留下痕迹
            print("%08x  位于 %s(受保护区域及其相邻的节),默认不读;需要时设 DNF_ALLOW_PROTECTED=1" % (addr, name))
            continue
        if kind == "b":
            n = int(parts[2]) if len(parts) > 2 else 16
            print("%08x  [%s] %s" % (addr, name, pe.read(addr, n).hex()))
        elif kind == "f":
            print("%08x  [%s] float %r" % (addr, name, struct.unpack("<f", pe.read(addr, 4))[0]))
        elif kind == "q":
            print("%08x  [%s] double %r" % (addr, name, struct.unpack("<d", pe.read(addr, 8))[0]))
        elif kind == "i":
            print("%08x  [%s] int %d" % (addr, name, struct.unpack("<i", pe.read(addr, 4))[0]))
        else:
            v = struct.unpack("<I", pe.read(addr, 4))[0]
            print("%08x  [%s] dword 0x%08x (%d)" % (addr, name, v, v))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
