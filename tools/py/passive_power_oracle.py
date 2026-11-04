#!/usr/bin/env python3
"""被动威力列受保护浮点乘子:读客户端全局初始化与 0x011a0de0(本机模拟器测量,非 REA)。

观察:0x014fd605 从 0x01565cc0 装入受保护槽 0x01a6fbd4;0x00684510 经 0x011a0de0 读出后 FMUL static。
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from emu import ClientEmu  # noqa: E402

INIT_CONST_VA = 0x01565CC0
PROTECTED_SLOT_VA = 0x01A6FBD4


def measure_multiplier():
    e = ClientEmu()
    mult = struct.unpack("<f", e.read(INIT_CONST_VA, 4))[0]
    return mult


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--golden", metavar="PATH")
    args = ap.parse_args()
    mult = measure_multiplier()
    if args.selftest:
        if not (0.00999 <= mult <= 0.01001):
            raise SystemExit("multiplier out of band: " + str(mult))
        print("ok", mult)
        return
    if args.golden:
        with open(args.golden, "w", encoding="utf-8") as f:
            f.write("MULTIPLIER\t{:.9g}\n".format(mult))
            f.write("CASE\tstatic\t50\tdelta\t{:.9g}\n".format(50 * mult))
        print("wrote", args.golden)
        return
    print(mult)


if __name__ == "__main__":
    main()
