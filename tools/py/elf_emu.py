#!/usr/bin/env python3
"""服务端 ELF(re/server/bin/df_game_r.orig)的函数级模拟器。非 REA:本机 Python + Unicorn 的测量。

做法:把 ELF 的 PT_LOAD 段原样映射进 Unicorn(32 位 x86,含 x87,默认控制字 0x37F = 扩展精度、就近舍入),
再按 cdecl 约定调用其中"自包含的纯计算函数"(不碰网络、文件、libc),读回结果。这样"原版的机器码在一个模拟 CPU 上真的跑一遍"
就成了 C# 实现的独立对照:两边喂同样的输入,比较输出。

边界:
  * 只调用计算函数,不启动服务进程,不连网络,不读任何账号/配置。
  * PLT/libc/动态链接器路径都不映射;函数一旦走进未映射的地址就报错并给出地址,不会悄悄继续。
  * 只读使用 ELF 文件(不改它)。结构体与全局变量由调用方按服务端的内存布局写进模拟内存。
本文件只含自己的加载器与调用封装,不含原版代码。
"""
import os
import struct
import sys

from unicorn import Uc, UcError, UC_ARCH_X86, UC_MODE_32, UC_PROT_ALL
from unicorn.x86_const import (UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDI, UC_X86_REG_EDX,
                               UC_X86_REG_EIP, UC_X86_REG_ESI, UC_X86_REG_ESP, UC_X86_REG_FPCW, UC_X86_REG_FPSW, UC_X86_REG_FPTAG)

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
DEFAULT_ELF = os.path.join(ROOT, "re", "server", "bin", "df_game_r.orig")

PAGE = 0x1000
STACK_BASE = 0x7E000000
STACK_SIZE = 0x400000
RET_MAGIC = 0x7D000000
HEAP_BASE = 0x50000000
HEAP_SIZE = 0x8000000


def _align_down(v):
    return v & ~(PAGE - 1)


def _align_up(v):
    return (v + PAGE - 1) & ~(PAGE - 1)


class ElfEmu:
    def __init__(self, path=DEFAULT_ELF):
        with open(path, "rb") as f:
            data = f.read()
        if data[:4] != b"\x7fELF" or data[4] != 1 or data[5] != 1:
            raise ValueError("不是小端 32 位 ELF")
        phoff = struct.unpack_from("<I", data, 0x1C)[0]
        phentsize, phnum = struct.unpack_from("<HH", data, 0x2A)
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        loads = []
        for i in range(phnum):
            p_type, p_off, p_vaddr, _pa, p_filesz, p_memsz, _fl, _al = struct.unpack_from("<IIIIIIII", data, phoff + i * phentsize)
            if p_type == 1:
                loads.append((p_vaddr, p_off, p_filesz, p_memsz))
        # 段之间可能共用页:先合并成不重叠的连续区间,每个区间只映射一次(逐页映射要几十秒)
        ranges = sorted((_align_down(v), _align_up(v + m)) for v, _o, _f, m in loads)
        merged = []
        for start, end in ranges:
            if merged and start <= merged[-1][1]:
                merged[-1][1] = max(merged[-1][1], end)
            else:
                merged.append([start, end])
        for start, end in merged:
            self.uc.mem_map(start, end - start, UC_PROT_ALL)
        for p_vaddr, p_off, p_filesz, _m in loads:
            if p_filesz:
                self.uc.mem_write(p_vaddr, data[p_off:p_off + p_filesz])
        self.uc.mem_map(STACK_BASE, STACK_SIZE, UC_PROT_ALL)
        self.uc.mem_map(RET_MAGIC, PAGE, UC_PROT_ALL)
        self.uc.mem_write(RET_MAGIC, b"\xcc" * 16)
        self.uc.mem_map(HEAP_BASE, HEAP_SIZE, UC_PROT_ALL)
        self._heap_top = HEAP_BASE
        self.insns = 0

    # ---- 内存 ----
    def alloc(self, size, align=16):
        self._heap_top = (self._heap_top + align - 1) & ~(align - 1)
        addr = self._heap_top
        self._heap_top += size
        if self._heap_top > HEAP_BASE + HEAP_SIZE:
            raise MemoryError("模拟堆用完")
        return addr

    def write(self, addr, data):
        self.uc.mem_write(addr, bytes(data))

    def read(self, addr, n):
        return bytes(self.uc.mem_read(addr, n))

    def write_i32s(self, addr, values):
        self.uc.mem_write(addr, struct.pack("<%di" % len(values), *values))

    def read_i32s(self, addr, n):
        return list(struct.unpack("<%di" % n, self.read(addr, 4 * n)))

    def write_u32(self, addr, value):
        self.uc.mem_write(addr, struct.pack("<I", value & 0xFFFFFFFF))

    def read_u32(self, addr):
        return struct.unpack("<I", self.read(addr, 4))[0]

    # ---- 调用 ----
    def call(self, addr, args=(), max_insns=5_000_000):
        """cdecl 调用:参数从右到左压栈,调用者清栈。返回 EAX(无符号)。"""
        uc = self.uc
        sp = STACK_BASE + STACK_SIZE - 0x1000
        sp -= 4 * len(args)
        sp &= ~0xF
        frame = struct.pack("<I", RET_MAGIC) + b"".join(struct.pack("<I", a & 0xFFFFFFFF) for a in args)
        sp -= len(frame)
        uc.mem_write(sp, frame)
        uc.reg_write(UC_X86_REG_ESP, sp)
        uc.reg_write(UC_X86_REG_EBP, 0)
        # 真实进程里 ld.so/glibc 把 x87 控制字设成 0x37F(扩展精度、就近舍入、异常全屏蔽);
        # Unicorn 的初始值是 0(单精度、异常不屏蔽),必须显式设置,否则每条 FMUL/FDIV 都按 24 位有效位舍入
        uc.reg_write(UC_X86_REG_FPCW, 0x37F)
        uc.reg_write(UC_X86_REG_FPSW, 0)
        uc.reg_write(UC_X86_REG_FPTAG, 0xFFFF)
        for reg in (UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDX, UC_X86_REG_ESI, UC_X86_REG_EDI):
            uc.reg_write(reg, 0)
        try:
            uc.emu_start(addr, RET_MAGIC, count=max_insns)
        except UcError as e:
            eip = uc.reg_read(UC_X86_REG_EIP)
            raise RuntimeError("模拟 %08x 出错:%s,停在 EIP=%08x" % (addr, e, eip)) from e
        eip = uc.reg_read(UC_X86_REG_EIP)
        if eip != RET_MAGIC:
            raise RuntimeError("模拟 %08x 没有返回(指令数上限),EIP=%08x" % (addr, eip))
        return uc.reg_read(UC_X86_REG_EAX)


def _selftest():
    emu = ElfEmu()
    # isNamedMonster(int):区间判断,纯计算
    for mob_id, want in ((1, 0), (0xC34F, 0), (0xC350, 1), (0xC738, 1), (0xC739, 0), (0xDABF, 0), (0xDAC0, 1), (0xEA60, 1), (0xEA61, 0)):
        got = emu.call(0x088BA18C, [mob_id]) & 0xFF
        status = "ok" if got == want else "不符"
        print("isNamedMonster(%d) = %d  %s" % (mob_id, got, status))
        if got != want:
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(_selftest())
