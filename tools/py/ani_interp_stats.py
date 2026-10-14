"""检验"帧属性 10 = 与下一帧插值"这一推断(证据 E-030,非 REA):

对全库二进制 .ani 的每一对相邻帧 (i, i+1),看第 i 帧是否带属性 10,以及相邻两帧在 染色(9)、位置(x,y)、缩放(7)、旋转(8)
上是否不同。如果属性 10 是"插值开关",带它的帧应当更常出现在"相邻帧的某个可插值量不同"的位置。只数数字,不输出脚本内容。
用法: python3 tools/py/ani_interp_stats.py [--json out/ani_interp_stats.json]
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


def prop(fr, pid):
    for p, v in fr["props"]:
        if p == pid:
            return v
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    pvf = D.Pvf()
    p10_values = Counter()
    effects = Counter()
    effect_with_p10 = Counter()
    pairs = Counter()  # (量, p10 有无, 相邻是否不同)
    frames = 0
    anis_with10 = 0
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
        fs = a["frames"]
        has10 = False
        for i, fr in enumerate(fs):
            frames += 1
            v10 = prop(fr, 10)
            if v10 is not None:
                p10_values[v10[0]] += 1
                has10 = True
            v11 = prop(fr, 11)
            if v11 is not None:
                t = struct.unpack("<H", v11[:2])[0]
                effects[t] += 1
                if v10 is not None:
                    effect_with_p10[t] += 1
            if i + 1 >= len(fs):
                continue
            nx = fs[i + 1]
            flag = 1 if (v10 is not None and v10[0] != 0) else 0
            tint_a = prop(fr, 9) or b"\xff\xff\xff\xff"
            tint_b = prop(nx, 9) or b"\xff\xff\xff\xff"
            pairs[("tint", flag, tint_a != tint_b)] += 1
            pairs[("tint_alpha", flag, tint_a[3] != tint_b[3])] += 1
            pairs[("pos", flag, (fr["x"], fr["y"]) != (nx["x"], nx["y"]))] += 1
            sa = prop(fr, 7) or struct.pack("<2f", 1.0, 1.0)
            sb = prop(nx, 7) or struct.pack("<2f", 1.0, 1.0)
            pairs[("scale", flag, sa != sb)] += 1
            ra = prop(fr, 8) or struct.pack("<f", 0.0)
            rb = prop(nx, 8) or struct.pack("<f", 0.0)
            pairs[("rotate", flag, ra != rb)] += 1
            pairs[("image", flag, (fr["img"], fr["frame"]) != (nx["img"], nx["frame"]))] += 1
        anis_with10 += has10
    out = {"frames": frames, "anis_with_prop10": anis_with10, "prop10_values": dict(p10_values),
           "effects": {str(k): v for k, v in sorted(effects.items())},
           "effects_on_frames_with_prop10": {str(k): v for k, v in sorted(effect_with_p10.items())}, "pairs": {}}
    for q in ("tint", "tint_alpha", "pos", "scale", "rotate", "image"):
        rows = {}
        for flag in (0, 1):
            diff = pairs[(q, flag, True)]
            same = pairs[(q, flag, False)]
            rows["p10=%d" % flag] = {"相邻不同": diff, "相邻相同": same, "不同占比": round(diff / max(1, diff + same), 4)}
        out["pairs"][q] = rows
    text = json.dumps(out, ensure_ascii=False, indent=1)
    print(text)
    with open(args.json or os.path.join(OUT_DIR, "ani_interp_stats.json"), "w", encoding="utf-8") as f:
        f.write(text)


if __name__ == "__main__":
    main()
