#!/usr/bin/env python3
"""读几个核心类的虚表,列出指定虚槽(位移)在各类里的实现函数地址。

用途:C++ 对象的方法没有名字,只有"虚表位移";知道某个位移被调用后,要看各类对它的实现是什么。
只读静态:读 PE 里 .rdata 的虚表,不运行程序,不碰受保护区域(虚表在 .rdata)。

用法:
  python3 tools/py/vslots.py 2d0 2d4 2d8 2dc 2e0           # 按位移(十六进制)列各类实现
  python3 tools/py/vslots.py --funcs 2b4 204 --out out/x.txt # 只输出去重后的函数地址,一行一个

核心类的虚表地址来自 E-051(RTTI 恢复的继承树),只列主虚表(this_off == 0)。
"""
import argparse
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

CLASSES = [
    ("CollisionObject", 0x01567C5C),
    ("PassiveObject", 0x0156F14C),
    ("ActiveObject", 0x0170439C),
    ("Monster", 0x0170BE64),
    ("Character", 0x01704B1C),
    ("AICharacter", 0x0170C71C),
]


def read_bytes(addr, n):
    out = subprocess.run(
        [sys.executable, os.path.join(HERE, "read_va.py"), "b:%08x:%d" % (addr, n)],
        capture_output=True, text=True, cwd=ROOT,
    ).stdout.strip().splitlines()[-1]
    return bytes.fromhex(out.split("]")[-1].strip())


def slot(vt, off):
    return struct.unpack("<I", read_bytes(vt + off, 4))[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("offsets", nargs="+", help="虚槽位移(十六进制,如 2dc)")
    ap.add_argument("--funcs", action="store_true", help="只输出去重后的函数地址")
    ap.add_argument("--out", help="输出文件(缺省打印)")
    args = ap.parse_args()
    offs = [int(x, 16) for x in args.offsets]
    table = {}
    for name, vt in CLASSES:
        table[name] = [slot(vt, o) for o in offs]
    lines = []
    if args.funcs:
        seen = []
        for name, _ in CLASSES:
            for f in table[name]:
                if f not in seen:
                    seen.append(f)
        lines = ["%08x" % f for f in seen]
    else:
        lines.append("%-16s " % "class" + " ".join("+%x" % o for o in offs))
        for name, _ in CLASSES:
            lines.append("%-16s " % name + " ".join("%08x" % f for f in table[name]))
    text = "\n".join(lines) + "\n"
    if args.out:
        with open(args.out, "w") as fh:
            fh.write(text)
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
