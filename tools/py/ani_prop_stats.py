"""统计二进制 .ani 里帧属性 7(两个 f32,推断为图像缩放"image rate")、8(一个 f32,推断为旋转)、9(4 字节,推断为 RGBA 染色)的取值分布。

目的:检验"属性 7 是 (x 缩放, y 缩放)、x 为 -1 表示水平镜像"这类推断(证据 E-029)。只数数字,不输出任何脚本内容。
用法: python3 tools/py/ani_prop_stats.py [--json out/ani_prop_stats.json]
"""
import argparse
import json
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
import ani_spec
from dnfpaths import OUT_DIR


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    pvf = D.Pvf()
    frames = 0
    p7 = Counter()
    p8 = Counter()
    p9 = Counter()
    anis_with7 = anis_with8 = anis_with9 = 0
    for e in pvf.entries:
        if not e.path.endswith(".ani"):
            continue
        data = pvf.read(e)
        if len(data) < 8 or data[:2] == b"\xb0\xd0" or data[:1] == b"[":
            continue
        try:
            a = ani_spec.parse(data)
        except Exception:
            continue
        has = [False, False, False]
        for fr in a["frames"]:
            frames += 1
            for pid, v in fr["props"]:
                if pid == 7:
                    x, y = struct.unpack("<2f", v)
                    p7[(round(x, 3), round(y, 3))] += 1
                    has[0] = True
                elif pid == 8:
                    r = struct.unpack("<f", v)[0]
                    p8[round(r, 3)] += 1
                    has[1] = True
                elif pid == 9:
                    p9[tuple(v)] += 1
                    has[2] = True
        anis_with7 += has[0]
        anis_with8 += has[1]
        anis_with9 += has[2]
    n7, n8, n9 = sum(p7.values()), sum(p8.values()), sum(p9.values())
    out = {
        "frames": frames,
        "prop7": {"frames": n7, "anis": anis_with7, "top": [[list(k), c] for k, c in p7.most_common(15)],
                  "x_negative_frames": sum(c for (x, y), c in p7.items() if x < 0),
                  "y_negative_frames": sum(c for (x, y), c in p7.items() if y < 0),
                  "abs_one_both": sum(c for (x, y), c in p7.items() if abs(x) == 1.0 and abs(y) == 1.0)},
        "prop8": {"frames": n8, "anis": anis_with8, "top": [[k, c] for k, c in p8.most_common(15)]},
        "prop9": {"frames": n9, "anis": anis_with9, "top": [[list(k), c] for k, c in p9.most_common(10)]},
    }
    text = json.dumps(out, ensure_ascii=False, indent=1)
    print(text)
    with open(args.json or os.path.join(OUT_DIR, "ani_prop_stats.json"), "w", encoding="utf-8") as f:
        f.write(text)


if __name__ == "__main__":
    main()
