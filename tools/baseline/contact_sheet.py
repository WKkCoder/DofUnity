#!/usr/bin/env python3
"""把一组截图拼成一张联系表(缩小、带序号),方便一眼看完一个场景。只读输入,只写 --out 指定的文件。

用法:
  contact_sheet.py --out sheet.png [--cols 4] [--scale 0.5] [--every N] [--indices 0,10,20] [--crop X,Y,W,H] 目录或文件...

--every N      每 N 张取一张;--indices 明确指定序号(0 起);二者都不给就取全部。
--crop X,Y,W,H 先按原图像素裁出一块再缩放(想放大看某个区域时用)。
"""
import argparse
import glob
import os
import sys

from PIL import Image, ImageDraw


def gather(paths):
    files = []
    for p in paths:
        if os.path.isdir(p):
            files.extend(sorted(glob.glob(os.path.join(p, "*.png"))))
        else:
            files.append(p)
    return files


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cols", type=int, default=4)
    ap.add_argument("--scale", type=float, default=0.5)
    ap.add_argument("--every", type=int, default=0)
    ap.add_argument("--indices", default="")
    ap.add_argument("--crop", default="")
    ap.add_argument("inputs", nargs="+")
    args = ap.parse_args()

    files = gather(args.inputs)
    if not files:
        sys.exit("没有找到 PNG")
    picked = list(enumerate(files))
    if args.indices:
        want = {int(x) for x in args.indices.split(",") if x.strip()}
        picked = [(i, f) for i, f in picked if i in want]
    elif args.every > 1:
        picked = [(i, f) for i, f in picked if i % args.every == 0]
    if not picked:
        sys.exit("按条件没有选到任何一张")

    crop = None
    if args.crop:
        x, y, w, h = (int(v) for v in args.crop.split(","))
        crop = (x, y, x + w, y + h)

    tiles = []
    for i, f in picked:
        im = Image.open(f).convert("RGB")
        if crop:
            im = im.crop(crop)
        tw, th = max(1, int(im.width * args.scale)), max(1, int(im.height * args.scale))
        tiles.append((i, im.resize((tw, th), Image.LANCZOS)))

    tw, th = tiles[0][1].size
    cols = max(1, min(args.cols, len(tiles)))
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tw, rows * th), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    for n, (i, im) in enumerate(tiles):
        ox, oy = (n % cols) * tw, (n // cols) * th
        sheet.paste(im, (ox, oy))
        d.rectangle([ox, oy, ox + 34, oy + 14], fill=(0, 0, 0))
        d.text((ox + 3, oy + 2), "#%d" % i, fill=(255, 255, 0))
    sheet.save(args.out)
    print("%s: %d 张, %dx%d" % (args.out, len(tiles), sheet.width, sheet.height))


if __name__ == "__main__":
    main()
