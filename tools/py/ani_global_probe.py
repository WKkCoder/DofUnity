"""统计二进制 .ani 的各个全局属性 id(0、1、3、18、28)与动画名称的关系,补充 E-025(只看 id 0)。

做法与 ani_loop_probe.py 相同:多帧(>= 2 帧)的二进制 .ani;把文件名第一个英文单词按关键词分成
"通常会循环"(stay、wait、move、walk、run、idle、stand、dash)与"通常播一次"(attack、damage、die、down、cast、jump、throw)两组,
对每个全局属性 id 给出两组里"带它"的比例;并给出各 id 的共现情况(id 0 与 id 1 是否互斥)。
非 REA,只输出数字,不输出脚本文本。

用法: python3 tools/py/ani_global_probe.py [--json out/ani_global_probe.json]
"""
import argparse
import json
import os
import re
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
import ani_spec
from dnfpaths import OUT_DIR

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
    multi = 0
    ids_total = Counter()
    combos = Counter()
    by_group = {"loop_named": Counter(), "once_named": Counter()}
    group_n = Counter()
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
        if len(ani["frames"]) < 2:
            continue
        multi += 1
        ids = sorted({pid for pid, _ in ani["global"]})
        combos[tuple(ids)] += 1
        for i in ids:
            ids_total[i] += 1
        g = group_of(os.path.splitext(os.path.basename(e.path))[0].lower())
        if g:
            group_n[g] += 1
            for i in ids:
                by_group[g][i] += 1

    result = {
        "multi_frame_binary_ani": multi,
        "files_with_id": {str(k): v for k, v in sorted(ids_total.items())},
        "combinations": {str(list(k)): v for k, v in combos.most_common(12)},
        "named_groups": {
            g: {"n": group_n[g], "fraction_with_id": {str(i): round(by_group[g][i] / group_n[g], 4) for i in sorted(ids_total)}}
            for g in by_group
        },
    }
    text = json.dumps(result, ensure_ascii=False, indent=2)
    print(text)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
