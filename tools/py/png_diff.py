"""比较两张 PNG 的像素(非 REA 的测量工具;只输出数字)。

用法:
  python3 tools/py/png_diff.py a.png b.png [--max-diff N] [--crop x0,y0,x1,y1]
两张图尺寸必须相同(--crop 对两张图同时生效)。打印不同的像素数与每通道最大差;
最大差超过 --max-diff(默认 0)时退出码为 1,否则为 0。
要求系统 Python 带 Pillow(macOS 自带的 python3 有)。
"""
import argparse
import sys

from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--max-diff", type=int, default=0)
    ap.add_argument("--crop", default=None)
    args = ap.parse_args()
    ia = Image.open(args.a).convert("RGBA")
    ib = Image.open(args.b).convert("RGBA")
    if args.crop:
        box = tuple(int(v) for v in args.crop.split(","))
        ia, ib = ia.crop(box), ib.crop(box)
    if ia.size != ib.size:
        print("尺寸不同: %s 对 %s" % (ia.size, ib.size))
        return 1
    pa, pb = ia.tobytes(), ib.tobytes()
    n = len(pa) // 4
    differing = 0
    worst = 0
    for i in range(0, len(pa), 4):
        if pa[i:i + 4] != pb[i:i + 4]:
            differing += 1
            d = max(abs(pa[i + c] - pb[i + c]) for c in range(4))
            if d > worst:
                worst = d
    print("像素 %d,不同 %d,每通道最大差 %d" % (n, differing, worst))
    return 0 if worst <= args.max_diff else 1


if __name__ == "__main__":
    sys.exit(main())
