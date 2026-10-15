#!/usr/bin/env python3
"""受保护区"跳板"的作用探测(E-060):明文里的 `CALL 受保护地址` 大多是被偷走的少数几条指令,
块里塞满垃圾指令后回到调用点的下一条。这个脚本在 CPU 模拟器里跑一个这样的跳板,用"标记值"推断它的净效果:

  - 每个寄存器指向自己的一块 0x1000 字节的内存,正中(+0x800)是寄存器初值,块里每个 32 位字都是
    "标记 | 寄存器号 | 偏移",所以跑完之后,寄存器的新值如果是个标记,就能读出它等于 [某寄存器 + 位移];
  - 新值等于某个寄存器的初值(±小常数)也能认出来;
  - 同时列出写到栈以外的内存(地址、值)、停止原因与指令数,以及跑完后的标志位(ZF/CF/SF/OF);
  - `--poke ebp-0xc=0` 之类的选项可以在运行前改某个寄存器所指处的 32 位字(用来看跳板的标志位依赖哪个值)。

只读磁盘上的 DNF.exe;模拟器不运行游戏本体;不输出任何反汇编文本,只输出推断出的"寄存器 = 表达式"。
结果只对"净效果是少数几条指令"的跳板有意义;含分支或依赖对象内容的块要另外分析。

用法:
    re/venv/bin/python tools/py/emu_stub_probe.py 0x020ef5a7 --ret 0x0087ecad
    (--ret 是调用点的下一条地址,即模拟的 CALL 压入的返回地址)
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from emu import ClientEmu, STACK_LO, STACK_SIZE  # noqa: E402

REGS = ("eax", "ebx", "ecx", "edx", "esi", "edi", "ebp")
TAG = 0xA0000000
HALF = 0x800


def build(emu):
    """给每个寄存器造一块标记内存,返回 {寄存器: 初值}。"""
    init = {}
    for i, r in enumerate(REGS):
        base = emu.scratch_alloc(0x1000)
        for off in range(0, 0x1000, 4):
            emu.write32(base + off, TAG | (i << 16) | off)
        init[r] = base + HALF
    return init


def explain(value, init):
    """把一个新值翻译成"寄存器 = 表达式"的描述。"""
    if (value & 0xFF000000) == TAG:
        i = (value >> 16) & 0xFF
        off = value & 0xFFFF
        if i < len(REGS):
            disp = off - HALF
            sign = "+" if disp >= 0 else "-"
            return "[%s %s 0x%x]" % (REGS[i], sign, abs(disp))
    for r, v in init.items():
        d = (value - v) & 0xFFFFFFFF
        if d >= 0x80000000:
            d -= 1 << 32
        if abs(d) <= 0x400:
            return r if d == 0 else "%s %s 0x%x" % (r, "+" if d > 0 else "-", abs(d))
    return "未识别(0x%08x)" % value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("addr", type=lambda s: int(s, 0))
    ap.add_argument("--ret", type=lambda s: int(s, 0), required=True)
    ap.add_argument("--max-insns", type=int, default=2_000_000)
    ap.add_argument("--twice", action="store_true", help="跑两次(第二次不受一次性初始化影响)")
    ap.add_argument("--flags", type=lambda s: int(s, 0), default=0x202, help="运行前的 EFLAGS(缺省 0x202;ZF 置位用 0x246)")
    ap.add_argument("--reg", action="append", default=[], help="运行前改寄存器初值,形如 esi=0(可重复;值为 0 时该寄存器不再指向标记块)")
    ap.add_argument("--poke", action="append", default=[], help="运行前改内存,形如 ebp-0xc=0 或 esi+0x10=5(可重复)")
    a = ap.parse_args()

    emu = ClientEmu()
    init = build(emu)
    for item in a.reg:
        name, val = item.split("=")
        init[name.strip()] = int(val, 0)
    for item in a.poke:
        lhs, rhs = item.split("=")
        for sign in "+-":
            if sign in lhs:
                reg, disp = lhs.split(sign, 1)
                disp = int(disp, 0) * (1 if sign == "+" else -1)
                break
        else:
            reg, disp = lhs, 0
        emu.write32(init[reg.strip()] + disp, int(rhs, 0))
    runs = 2 if a.twice else 1
    for k in range(runs):
        regs = dict(init)
        regs["eflags"] = a.flags
        res, log = emu.call(a.addr, regs=regs, until=a.ret, return_address=a.ret, max_insns=a.max_insns,
                            watch=[(0x00400000, 0x04100000)])
        balanced = STACK_LO + STACK_SIZE - 0x1000      # 正常返回后栈指针应回到的位置
        delta = res.esp - balanced
        if delta >= 0x80000000:
            delta -= 1 << 32
        print("第 %d 次:停止原因 %s,%d 条指令,停在 %08x,栈指针相对正常返回偏差 %d" % (k + 1, res.stop, res.insns, res.eip, delta))
        fl = emu.reg("eflags")
        print("  标志位:ZF=%d CF=%d SF=%d OF=%d" % ((fl >> 6) & 1, fl & 1, (fl >> 7) & 1, (fl >> 11) & 1))
        for r in REGS:
            now = emu.reg(r)
            if now != init[r]:
                print("  %s = %s" % (r, explain(now, init)))
        outside = [w for w in res.writes if not (0x20000000 <= w[0] < 0x20200000)]
        for addr, size, value, eip in outside[:20]:
            print("  写 [%08x] 宽 %d 值 %08x" % (addr, size, value))
        if len(outside) > 20:
            print("  ……另有 %d 次写" % (len(outside) - 20))
        if res.faults:
            print("  缺页/中断:", [(hex(f[0]), f[2]) for f in res.faults[:6]])


if __name__ == "__main__":
    main()
