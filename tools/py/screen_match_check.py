"""核对"原版客户端的屏幕输出"与"我们的 IMG 像素解码"是否逐像素一致,并比较两种 1555 展开规则。

输入:
  --shot    原版客户端截图(PNG,8 位 RGB 或 RGBA,窗口内容 1:1,不缩放)
  --locate  `dnf locate --json` 输出(每个匹配含 npk、entry、frame、x、y)
对每个匹配的帧:用 Python 参考实现(dnfdata.py)独立解码,把"不透明像素"与截图同一位置逐像素比较,
  - 采用的规则:5 位通道 round(v*255/31)(当前实现)
  - 对照规则:位复制 (v<<3)|(v>>2)(早先的实现)
只输出数字(帧名、像素数、不一致像素数),不输出任何像素内容。

只有不透明像素(alpha=255)会被比较;被别的精灵遮住的部分会算作不一致,所以单个帧的 100% 并不是必然。
用法:
  python3 tools/py/screen_match_check.py --shot re/baseline/runs/<run>/shots/02-client-window.png \
      --locate out/locate_after_fix.json [--json out/screen_match_check.json]
"""
import argparse
import json
import os
import struct
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from dnfpaths import require_dnf_dir


def load_png(path):
    """最小的 PNG 读取:8 位、非隔行、RGB 或 RGBA。返回 (宽, 高, 每像素字节数, 行缓冲列表)。"""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit("不是 PNG: " + path)
    pos = 8
    idat = []
    width = height = depth = ctype = interlace = None
    while pos < len(data):
        n, tag = struct.unpack_from(">I4s", data, pos)
        body = data[pos + 8: pos + 8 + n]
        pos += 12 + n
        if tag == b"IHDR":
            width, height, depth, ctype, _comp, _flt, interlace = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat.append(body)
        elif tag == b"IEND":
            break
    if depth != 8 or ctype not in (2, 6) or interlace != 0:
        raise SystemExit("只支持 8 位、非隔行的 RGB/RGBA PNG")
    bpp = 3 if ctype == 2 else 4
    raw = zlib.decompress(b"".join(idat))
    stride = width * bpp
    rows = []
    prev = bytearray(stride)
    p = 0
    for _ in range(height):
        ft = raw[p]
        line = bytearray(raw[p + 1: p + 1 + stride])
        p += 1 + stride
        if ft == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 255
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif ft == 3:
            for i in range(stride):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                pa = abs(b - c)
                pb = abs(a - c)
                pc = abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        elif ft != 0:
            raise SystemExit("PNG 滤波类型 %d" % ft)
        rows.append(line)
        prev = line
    return width, height, bpp, rows


def replicate_1555(raw):
    """早先的规则:位复制。返回 RGBA 字节串。"""
    n = len(raw) // 2
    words = struct.unpack("<%dH" % n, raw[: n * 2])
    out = bytearray(n * 4)
    for i, p in enumerate(words):
        r, g, b = (p >> 10) & 31, (p >> 5) & 31, p & 31
        out[i * 4] = (r << 3) | (r >> 2)
        out[i * 4 + 1] = (g << 3) | (g >> 2)
        out[i * 4 + 2] = (b << 3) | (b >> 2)
        out[i * 4 + 3] = 255 if (p >> 15) & 1 else 0
    return bytes(out)


def compare(rgba, fr, x0, y0, sw, sh, bpp, rows):
    """在 (x0,y0) 处比较不透明像素。返回 (不透明且在屏幕内的像素数, 不一致像素数, 最大通道差)。"""
    total = bad = maxd = 0
    w, h = fr.w, fr.h
    for y in range(h):
        sy = y0 + y
        if not 0 <= sy < sh:
            continue
        row = rows[sy]
        for x in range(w):
            sx = x0 + x
            if not 0 <= sx < sw:
                continue
            o = (y * w + x) * 4
            if rgba[o + 3] != 255:
                continue
            total += 1
            so = sx * bpp
            d = max(abs(rgba[o] - row[so]), abs(rgba[o + 1] - row[so + 1]), abs(rgba[o + 2] - row[so + 2]))
            if d:
                bad += 1
                if d > maxd:
                    maxd = d
    return total, bad, maxd


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shot", required=True)
    ap.add_argument("--locate", required=True)
    ap.add_argument("--json")
    args = ap.parse_args()

    sw, sh, bpp, rows = load_png(args.shot)
    matches = json.load(open(args.locate, encoding="utf-8"))["matches"]
    npk_dir = os.path.join(require_dnf_dir(), "ImagePacks2")

    results = []
    toc_cache = {}
    for m in matches:
        path = os.path.join(npk_dir, m["npk"])
        if path not in toc_cache:
            toc_cache[path] = {e.name: e for e in D.read_npk_toc(path)}
        ent = toc_cache[path][m["entry"]]
        with open(path, "rb") as f:
            f.seek(ent.offset)
            buf = f.read(ent.size)
        frames, offs, _ = D.parse_img_v2(buf)
        fr, raw = D.frame_raw(buf, frames, offs, m["frame"])
        rgba = D.to_rgba(fr, raw)
        total, bad, maxd = compare(rgba, fr, m["x"], m["y"], sw, sh, bpp, rows)
        rec = {
            "npk": m["npk"], "entry": m["entry"], "frame": m["frame"], "type": "0x%02X" % fr.type,
            "x": m["x"], "y": m["y"], "opaque_in_screen": total, "mismatch_rounded": bad, "max_delta_rounded": maxd,
        }
        if fr.type == D.T_ARGB1555:
            t2, bad2, maxd2 = compare(replicate_1555(raw), fr, m["x"], m["y"], sw, sh, bpp, rows)
            rec["mismatch_replicate"] = bad2
            rec["max_delta_replicate"] = maxd2
        results.append(rec)

    print("%-6s %-9s %-9s %-10s %s" % ("类型", "不透明", "round不符", "位复制不符", "帧"))
    for r in results:
        print("%-6s %-9d %-9d %-10s %s #%d @(%d,%d)" % (
            r["type"], r["opaque_in_screen"], r["mismatch_rounded"],
            str(r.get("mismatch_replicate", "-")), r["entry"], r["frame"], r["x"], r["y"]))

    summary = {}
    for r in results:
        s = summary.setdefault(r["type"], {"frames": 0, "opaque": 0, "mismatch_rounded": 0, "mismatch_replicate": 0, "frames_exact_rounded": 0})
        s["frames"] += 1
        s["opaque"] += r["opaque_in_screen"]
        s["mismatch_rounded"] += r["mismatch_rounded"]
        s["mismatch_replicate"] += r.get("mismatch_replicate", 0)
        if r["mismatch_rounded"] == 0:
            s["frames_exact_rounded"] += 1
    print("\n汇总(按像素类型):")
    for k, s in sorted(summary.items()):
        print("  %s: %s" % (k, json.dumps(s, ensure_ascii=False)))

    if args.json:
        os.makedirs(os.path.dirname(os.path.abspath(args.json)), exist_ok=True)
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"screenshot": os.path.basename(args.shot), "frames": results, "summary": summary}, f, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
