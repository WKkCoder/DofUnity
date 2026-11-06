#!/usr/bin/env python3
"""在 DNF.exe 的普通代码段里按字节找"谁调用了某个地址"和"谁用到了某个 32 位立即数"(只读磁盘字节,不运行,不碰受保护区)。

用途:Ghidra 对 0x010b56c0 这种巨型函数反编译会失败、交叉引用也不全;读一个行为的执行块时,常要问
"这个构造函数在哪被调用"(找到类的创建点)或"这个类型描述符地址被谁拿去做 dynamicCast"(找到类的使用点)。

  --call VA [VA…]   找 E8 rel32(相对调用)与 E9 rel32(相对跳转)落在这些地址上的指令,打印它们的地址
  --imm  VA [VA…]   找整个代码段里出现这个 4 字节小端立即数的位置,同时打印前一个字节(0x68 是 PUSH imm32,0xb8~0xbf 是 MOV reg,imm32)
  --range 起 止      只看这个地址范围(默认整个普通代码段 0x00401000..0x0155a000)

输出只含地址与操作码字节,不含任何原版数据;结果放 re/ 或 out/ 下自己看,不入库。
用法:re/venv/bin/python tools/py/call_sites.py --call 00d20490 --imm 019db264
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from decode_strings import Pe  # noqa: E402
from vtable_scan import exe_path  # noqa: E402

CODE_LO = 0x00401000
CODE_HI = 0x0155A000


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--call", nargs="*", default=[])
    ap.add_argument("--imm", nargs="*", default=[])
    ap.add_argument("--range", nargs=2)
    a = ap.parse_args()
    lo, hi = CODE_LO, CODE_HI
    if a.range:
        lo, hi = int(a.range[0], 16), int(a.range[1], 16)
    pe = Pe(exe_path())
    blob = pe.read(lo, hi - lo)
    calls = {int(x, 16): [] for x in a.call}
    for i in range(len(blob) - 5):
        op = blob[i]
        if op == 0xE8 or op == 0xE9:
            rel = struct.unpack_from("<i", blob, i + 1)[0]
            target = (lo + i + 5 + rel) & 0xFFFFFFFF
            if target in calls:
                calls[target].append((lo + i, "call" if op == 0xE8 else "jmp"))
    for t, hits in calls.items():
        print("目标 %08x:%d 处" % (t, len(hits)))
        for addr, kind in hits:
            print("  %08x %s" % (addr, kind))
    for x in a.imm:
        v = int(x, 16)
        pat = struct.pack("<I", v)
        pos = 0
        hits = []
        while True:
            j = blob.find(pat, pos)
            if j < 0:
                break
            hits.append((lo + j, blob[j - 1] if j > 0 else 0))
            pos = j + 1
        print("立即数 %08x:%d 处" % (v, len(hits)))
        for addr, prev in hits:
            print("  %08x 前一字节 %02x" % (addr, prev))
    return 0


if __name__ == "__main__":
    sys.exit(main())
