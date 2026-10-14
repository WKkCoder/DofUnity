"""统计二进制 .ani 的全局属性 id 0(推断为"循环")与动画名称的关系。

做法:不用引擎里的属性表(我们没有),只看数据本身。对全库的二进制 .ani(与 ani_spec.py 同一个筛选:
去掉长度 < 8、以 B0 D0 开头的标记流文件、以 [ 开头的文本文件):
  - 多帧(>= 2 帧)的动画里,有全局属性 0 且负载第一个字节非 0 的有多少、有属性 0 但为 0 的有多少、没有属性的有多少;
  - 把文件名(不含目录与扩展名,小写)按关键词分成"通常会循环"(stay、wait、move、walk、run、idle、stand、dash)
    与"通常播一次"(attack、damage、die、down、cast、jump、throw)两组,各自统计多帧动画里带属性 0 的比例。
    分组规则有四种,主结果用"第一个英文单词与关键词完全相同"(把文件名里连续的小写字母当作单词),
    另外三种(任一单词相同、子串、前缀)作为敏感性对照。
属性 0 与动作名称强相关,是"它表示循环"的统计证据,不是引擎源码证据(证据 E-025)。

用法: python3 tools/py/ani_loop_probe.py [--json out/ani_loop_probe.json]
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


def _words(stem):
    return re.findall(r"[a-z]+", stem)


def first_word(stem):
    w = _words(stem)
    if w and w[0] in LOOP_WORDS:
        return "loop_named"
    if w and w[0] in ONCE_WORDS:
        return "once_named"
    return None


def any_word(stem):
    w = _words(stem)
    if any(x in LOOP_WORDS for x in w):
        return "loop_named"
    if any(x in ONCE_WORDS for x in w):
        return "once_named"
    return None


def substring(stem):
    if any(k in stem for k in LOOP_WORDS):
        return "loop_named"
    if any(k in stem for k in ONCE_WORDS):
        return "once_named"
    return None


def prefix(stem):
    if any(stem.startswith(k) for k in LOOP_WORDS):
        return "loop_named"
    if any(stem.startswith(k) for k in ONCE_WORDS):
        return "once_named"
    return None


RULES = {"first_word": first_word, "any_word": any_word, "prefix": prefix, "substring": substring}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=None)
    args = ap.parse_args()

    pvf = D.Pvf()
    binary = 0
    multi = Counter()       # 多帧动画的属性 0 状态
    payload_values = Counter()
    groups = {r: {"loop_named": Counter(), "once_named": Counter()} for r in RULES}
    for e in pvf.entries:
        if not e.path.endswith(".ani"):
            continue
        data = pvf.read(e)
        if len(data) < 8 or data[:2] == b"\xb0\xd0" or data[:1] == b"[":
            continue
        try:
            ani = ani_spec.parse(data)
        except (ani_spec.AniError, Exception):
            continue
        binary += 1
        if len(ani["frames"]) < 2:
            continue
        loop = None
        for pid, payload in ani["global"]:
            if pid == 0:
                loop = payload[0]
                payload_values[payload[0]] += 1
        has = loop is not None and loop != 0
        multi["none" if loop is None else ("loop1" if has else "loop0")] += 1
        stem = os.path.splitext(os.path.basename(e.path))[0].lower()
        for rule, fn in RULES.items():
            g = fn(stem)
            if g:
                groups[rule][g]["with_prop0" if has else "without"] += 1

    result = {
        "binary_ani_parsed": binary,
        "multi_frame_prop0_state": dict(multi),
        "prop0_payload_first_byte": {str(k): v for k, v in payload_values.items()},
        "by_rule": {},
    }
    for rule, g in groups.items():
        entry = {}
        for name, c in g.items():
            n = c["with_prop0"] + c["without"]
            entry[name] = {"with_prop0": c["with_prop0"], "without": c["without"],
                           "fraction": round(c["with_prop0"] / n, 4) if n else None}
        result["by_rule"][rule] = entry
    text = json.dumps(result, ensure_ascii=False, indent=2)
    print(text)
    out = args.json or os.path.join(OUT_DIR, "ani_loop_probe.json")
    with open(out, "w", encoding="utf-8") as f:
        f.write(text)


if __name__ == "__main__":
    main()
