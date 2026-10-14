"""统计二进制 .ani 的帧属性 25(0 字节)与 26(4 字节)的分布,检验"25 = 循环起点、26 = 循环终点(带次数)"的假设。

假设来自文本 .ani 的 [LOOP START] / [LOOP END] 标签(依据是对公开资料的记忆,不是本机证据),这里只用数据本身核对:
  - 带 25 / 带 26 的动画数,两者都带的数量,25 所在帧是否早于 26 所在帧(同一动画内);
  - 26 的负载(32 位整数)取值分布;
  - 26 是否多落在最后一帧,25 是否多落在第 0 帧之后;
  - 按文件名第一个英文单词分的"通常循环/通常一次"两组里带 25 或 26 的比例(对照 E-025)。
非 REA,只输出数字。

用法: python3 tools/py/ani_loopmark_probe.py [--json out/ani_loopmark_probe.json]
"""
import argparse
import json
import os
import re
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
import ani_spec

LOOP_WORDS = ("stay", "wait", "move", "walk", "run", "idle", "stand", "dash")
ONCE_WORDS = ("attack", "damage", "die", "down", "cast", "jump", "throw")


def group_of(stem):
    w = re.findall(r"[a-z]+", stem)
    if w and w[0] in LOOP_WORDS:
        return "loop_named"
    if w and w[0] in ONCE_WORDS:
        return "once_named"
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    pvf = D.Pvf()
    anis = 0
    has25 = has26 = both = 0
    order = Counter()
    payload26 = Counter()
    pos26 = Counter()
    pos25 = Counter()
    frames_with25 = Counter()
    frames_with26 = Counter()
    group_n = Counter()
    group_has = {"loop_named": Counter(), "once_named": Counter()}
    for e in pvf.entries:
        if not e.path.endswith(".ani"):
            continue
        data = pvf.read(e)
        if len(data) < 8 or data[:2] == b"\xb0\xd0" or data[:1] == b"[":
            continue
        try:
            ani = ani_spec.parse(data)
        except Exception:
            continue
        frames = ani["frames"]
        if len(frames) < 2:
            continue
        anis += 1
        idx25, idx26 = [], []
        for i, fr in enumerate(frames):
            for pid, raw in fr["props"]:
                if pid == 25:
                    idx25.append(i)
                elif pid == 26:
                    idx26.append(i)
                    payload26[struct.unpack("<i", raw)[0]] += 1
        frames_with25[len(idx25)] += 1 if idx25 else 0
        frames_with26[len(idx26)] += 1 if idx26 else 0
        if idx25:
            has25 += 1
            pos25["first" if idx25[0] == 0 else "later"] += 1
        if idx26:
            has26 += 1
            pos26["last" if idx26[-1] == len(frames) - 1 else "inside"] += 1
        if idx25 and idx26:
            both += 1
            order["25_before_26" if idx25[0] < idx26[0] else ("same_frame" if idx25[0] == idx26[0] else "26_before_25")] += 1
        g = group_of(os.path.splitext(os.path.basename(e.path))[0].lower())
        if g:
            group_n[g] += 1
            if idx25 or idx26:
                group_has[g]["has"] += 1
    result = {
        "multi_frame_binary_ani": anis,
        "with_prop25": has25,
        "with_prop26": has26,
        "with_both": both,
        "order_when_both": dict(order),
        "prop25_position": dict(pos25),
        "prop26_position": dict(pos26),
        "prop26_payload_top": [[k, v] for k, v in payload26.most_common(12)],
        "named_groups": {g: {"n": group_n[g], "with_25_or_26": group_has[g]["has"],
                             "fraction": round(group_has[g]["has"] / group_n[g], 4)} for g in group_has},
    }
    text = json.dumps(result, ensure_ascii=False, indent=2)
    print(text)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
