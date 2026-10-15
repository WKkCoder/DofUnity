#!/usr/bin/env python3
"""按虚拟地址读服务端 ELF(re/server/bin/df_game_r.orig)磁盘字节里的常量(只读文件,不运行)。

用法:python3 tools/py/read_elf_va.py f:08b12f54 d:0936e4a8 b:08b12f54:16 ...
  f:地址        32 位浮点        d:地址        32 位无符号整数        i:地址  32 位有符号整数
  q:地址        64 位浮点(double)    b:地址:长度  十六进制字节        s:地址   以 0 结尾的字符串
.rodata/.data 里的初值能读到;.bss 里的全局变量(运行时才写入)在文件中没有内容,会提示"在 bss 内"。
用来确认反汇编里引用的全局常量的值。服务端二进制只做静态读取。
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
DEFAULT_ELF = os.path.join(ROOT, "re", "server", "bin", "df_game_r.orig")


class Elf32:
    def __init__(self, path=DEFAULT_ELF):
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        if d[:4] != b"\x7fELF" or d[4] != 1 or d[5] != 1:
            raise ValueError("不是小端 32 位 ELF")
        phoff = struct.unpack_from("<I", d, 0x1C)[0]
        phentsize, phnum = struct.unpack_from("<HH", d, 0x2A)
        self.loads = []
        for i in range(phnum):
            p_type, p_off, p_vaddr, _p_paddr, p_filesz, p_memsz, _fl, _al = struct.unpack_from(
                "<IIIIIIII", d, phoff + i * phentsize)
            if p_type == 1:
                self.loads.append((p_vaddr, p_off, p_filesz, p_memsz))

    def read(self, va, n):
        for vaddr, off, filesz, memsz in self.loads:
            if vaddr <= va < vaddr + memsz:
                if va + n > vaddr + filesz:
                    raise ValueError("%08x 在 bss 内(文件中没有内容,运行时才写入)" % va)
                s = off + (va - vaddr)
                return self.data[s:s + n]
        raise ValueError("%08x 不在任何加载段内" % va)

    def cstr(self, va, limit=256):
        out = bytearray()
        for i in range(limit):
            b = self.read(va + i, 1)
            if b == b"\x00":
                break
            out += b
        return bytes(out).decode("latin-1")


def main(argv):
    if not argv:
        print(__doc__)
        return 1
    elf = Elf32()
    for spec in argv:
        parts = spec.split(":")
        kind, addr = parts[0], int(parts[1], 16)
        try:
            if kind == "b":
                n = int(parts[2]) if len(parts) > 2 else 16
                print("%08x  %s" % (addr, elf.read(addr, n).hex()))
            elif kind == "f":
                print("%08x  float %r" % (addr, struct.unpack("<f", elf.read(addr, 4))[0]))
            elif kind == "q":
                print("%08x  double %r" % (addr, struct.unpack("<d", elf.read(addr, 8))[0]))
            elif kind == "i":
                print("%08x  int %d" % (addr, struct.unpack("<i", elf.read(addr, 4))[0]))
            elif kind == "s":
                print("%08x  %r" % (addr, elf.cstr(addr)))
            else:
                v = struct.unpack("<I", elf.read(addr, 4))[0]
                print("%08x  dword 0x%08x (%d)" % (addr, v, v))
        except ValueError as e:
            print(str(e))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
