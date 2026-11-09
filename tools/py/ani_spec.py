"""二进制 .ani 的参考解析器(Python),以及对全库"恰好吃完整个文件"的验证。

结构(在全部 187,365 个二进制 .ani 上验证通过, 见 docs/formats.md):
  u16 frameCount, u16 imageCount
  imageCount * (u32 len, bytes path)
  u16 globalCount, globalCount * (u16 id + 负载)
  frameCount * 帧:
      u16 boxCount, boxCount * (u16 boxType + 6 * i32)       # boxType 14 = 伤害框, 15 = 攻击框
      u16 imageIdx(0xFFFF = 无图), 若 imageIdx != 0xFFFF 再跟 u16 frameIdx
      i32 x, i32 y, u16 propCount, propCount * (u16 id + 负载)

属性 id 的"名字"是根据负载形状与取值推断的, 没有引擎里的原表; 凡标 (推断) 的都不是证据。
"""
import json
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from dnfpaths import OUT_DIR as OUT

GLOBAL_FIXED = {0: 1, 1: 1, 3: 2, 18: 15, 28: 2}
FRAME_FIXED = {0: 1, 1: 1, 2: 8, 7: 8, 8: 4, 9: 4, 10: 1, 12: 4, 13: 2, 17: 0, 23: 4, 24: 2, 25: 0, 26: 4, 27: 8}
FRAME_STR = {16}                 # u32 长度 + 内容(推断为音效路径)
EFFECT_ID = 11                   # u16 类型; 类型 5 后面多 3 字节(推断为 RGB)
EFFECT_EXTRA = {5: 3}

FRAME_NAMES = {  # (推断) 仅用于阅读
    7: "image rate (2 x f32)", 8: "image rotate (f32)", 9: "rgba (4 x u8)", 10: "u8 flag (interpolation?)",
    11: "graphic effect (u16 type [+rgb])", 12: "delay ms (i32)", 13: "damage type (u16)", 16: "string (sound path?)",
}


class AniError(Exception):
    pass


def parse(d, collect=None):
    """解析一个二进制 .ani。成功返回 dict, 任何不一致抛 AniError。"""
    n = len(d)
    if n < 8:
        raise AniError("short")
    frames, images = struct.unpack_from("<HH", d, 0)
    pos = 4
    paths = []
    for _ in range(images):
        ln = struct.unpack_from("<I", d, pos)[0]
        if ln > 400 or pos + 4 + ln > n:
            raise AniError("path")
        paths.append(d[pos + 4: pos + 4 + ln].decode("latin1"))
        pos += 4 + ln
    gc = struct.unpack_from("<H", d, pos)[0]
    pos += 2
    gprops = []
    for _ in range(gc):
        pid = struct.unpack_from("<H", d, pos)[0]
        if pid not in GLOBAL_FIXED:
            raise AniError("unknown G%d" % pid)
        gprops.append((pid, d[pos + 2: pos + 2 + GLOBAL_FIXED[pid]]))
        pos += 2 + GLOBAL_FIXED[pid]
    out_frames = []
    for _ in range(frames):
        nb = struct.unpack_from("<H", d, pos)[0]
        pos += 2
        if nb > 64 or pos + nb * 26 > n:
            raise AniError("box")
        boxes = []
        for _ in range(nb):
            boxes.append((struct.unpack_from("<H", d, pos)[0],) + struct.unpack_from("<6i", d, pos + 2))
            pos += 26
        img = struct.unpack_from("<H", d, pos)[0]
        pos += 2
        fidx = -1
        if img != 0xFFFF:
            fidx = struct.unpack_from("<H", d, pos)[0]
            pos += 2
            if img >= max(images, 1):
                raise AniError("imageIdx")
        x, y, pc = struct.unpack_from("<iiH", d, pos)
        pos += 10
        if pc > 40:
            raise AniError("propCount")
        props = []
        for _ in range(pc):
            pid = struct.unpack_from("<H", d, pos)[0]
            if pid == EFFECT_ID:
                et = struct.unpack_from("<H", d, pos + 2)[0]
                size = 2 + EFFECT_EXTRA.get(et, 0)
            elif pid in FRAME_FIXED:
                size = FRAME_FIXED[pid]
            elif pid in FRAME_STR:
                ln = struct.unpack_from("<I", d, pos + 2)[0]
                if ln > 300:
                    raise AniError("str")
                size = 4 + ln
            else:
                raise AniError("unknown F%d" % pid)
            props.append((pid, d[pos + 2: pos + 2 + size]))
            pos += 2 + size
            if pos > n:
                raise AniError("eof")
        out_frames.append({"img": img, "frame": fidx, "x": x, "y": y, "boxes": boxes, "props": props})
    if pos != n:
        raise AniError("trailing %d" % (n - pos))
    return {"paths": paths, "global": gprops, "frames": out_frames}


def main():
    p = D.Pvf()
    ok = total = 0
    fail = Counter()
    st = Counter()
    delays = Counter()
    strings = []
    for e in p.entries:
        if not e.path.endswith(".ani"):
            continue
        d = p.read(e)
        if len(d) < 8 or d[:2] == b"\xb0\xd0" or d[:1] == b"[":
            fail["empty-or-text"] += 1
            continue
        total += 1
        try:
            a = parse(d)
        except (AniError, struct.error) as ex:
            fail[str(ex)] += 1
            continue
        ok += 1
        for pid, v in a["global"]:
            st[("G", pid)] += 1
        for fr in a["frames"]:
            for b in fr["boxes"]:
                st[("B", b[0])] += 1
            for pid, v in fr["props"]:
                st[("F", pid)] += 1
                if pid == 12:
                    delays[struct.unpack("<i", v)[0]] += 1
                elif pid == 11:
                    st[("E", struct.unpack_from("<H", v, 0)[0])] += 1
                elif pid == 16 and len(strings) < 6:
                    strings.append(v[4:].decode("latin1"))
    print("二进制 .ani 完整解析: %d / %d; 空文件或文本: %d; 其他失败: %s" % (ok, total, fail["empty-or-text"], dict(fail)))
    print("id 12 取值前 10:", delays.most_common(10))
    print("id 16 样例:", strings)
    os.makedirs(OUT, exist_ok=True)
    json.dump({"ok": ok, "total": total, "fail": dict(fail),
               "stat": {"%s:%d" % k: v for k, v in sorted(st.items())},
               "delay_top": delays.most_common(20)},
              open(os.path.join(OUT, "ani_verify.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
