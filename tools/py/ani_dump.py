"""打印若干个 .ani 的帧属性(延时、染色、缩放、旋转、插值标志、图形特效),非 REA 的只读测量。

只输出数字(帧偏移与属性取值),不输出像素、不输出脚本文本。路径是 PVF 里的路径(小写,如 map/elvengard/animation/gatelight1.ani)。
用法:
  python3 tools/py/ani_dump.py map/elvengard/animation/gatelight1.ani map/elvengard/animation/gatepillarlight.ani
属性语义(推断)见 docs/formats.md 与证据台账 E-025、E-029、E-030。
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ani_spec as A
import dnfdata as D
from dnfpaths import require_dnf_dir


def describe(props):
    out = []
    for pid, raw in props:
        if pid == 12:
            out.append("延时=%d" % struct.unpack("<i", raw)[0])
        elif pid == 9:
            out.append("染色=(%d,%d,%d,%d)" % tuple(raw))
        elif pid == 7:
            out.append("缩放=(%g,%g)" % struct.unpack("<ff", raw))
        elif pid == 8:
            out.append("旋转=%g" % struct.unpack("<f", raw)[0])
        elif pid == 10:
            out.append("插值标志=%d" % raw[0])
        elif pid == 11:
            out.append("特效=%d" % struct.unpack_from("<H", raw)[0])
        elif pid == 16:
            out.append("音效键")
        else:
            out.append("属性%d" % pid)
    return " ".join(out) if out else "(无属性)"


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    pvf = D.Pvf(os.path.join(require_dnf_dir(), "Script.pvf"))
    for path in sys.argv[1:]:
        raw = pvf.get(path.lower())
        if raw is None:
            print(path, ": PVF 里没有这个文件")
            continue
        ani = A.parse(raw)
        loop = any(pid == 0 for pid, _ in ani["global"])
        print("%s  帧数 %d  全局属性 %s  循环=%s" % (path, len(ani["frames"]), [pid for pid, _ in ani["global"]], loop))
        for i, fr in enumerate(ani["frames"]):
            print("  帧 %d  图 %d/%d  偏移(%d,%d)  %s" % (i, fr["img"], fr["frame"], fr["x"], fr["y"], describe(fr["props"])))


if __name__ == "__main__":
    main()
