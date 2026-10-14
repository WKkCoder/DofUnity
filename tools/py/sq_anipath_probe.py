"""核对脚本里 GetAnimationMap / sq_CreateAnimation / sq_LoadSkillEffectAni 这几个接口的 .ani 路径字面量,
是不是相对 PVF 根目录写的(E-038 的"路径约定"一条)。

做法:在 re/notes/nut/sqr(由 tools/py/script_dump.py 解出,不入库)里找这几个调用,取同一行里所有以 .ani 结尾的字符串字面量,
各自按两种方式去 PVF 里查:① 当成相对 PVF 根(小写、反斜杠换斜杠);② 找不到时,看有没有把它当成相对脚本所在目录能找到。
只输出计数与未找到的个数,不输出脚本内容。非 REA,只读测量。

用法:python3 tools/py/sq_anipath_probe.py [--json out/sq_anipath_probe.json]
"""
import argparse
import json
import os
import re
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
NUT_ROOT = os.path.join(ROOT, "re", "notes", "nut", "sqr")

CALLS = ("GetAnimationMap", "sq_CreateAnimation", "sq_LoadSkillEffectAni")
LITERAL = re.compile(r'"([^"\r\n]+?\.ani)"', re.IGNORECASE)


def norm(p):
    return p.replace("\\", "/").lower().lstrip("/")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    if not os.path.isdir(NUT_ROOT):
        sys.exit("没有 %s;先运行 tools/py/script_dump.py" % NUT_ROOT)
    pvf = D.Pvf()
    existing = {norm(e.path) for e in pvf.entries}
    found = Counter()
    total = Counter()
    missing = Counter()
    for dirpath, _, names in os.walk(NUT_ROOT):
        for name in names:
            if not name.lower().endswith(".nut"):
                continue
            with open(os.path.join(dirpath, name), "rb") as f:
                text = f.read().decode("latin-1")
            for line in text.split("\n"):
                for call in CALLS:
                    if call not in line:
                        continue
                    for m in LITERAL.finditer(line):
                        total[call] += 1
                        if norm(m.group(1)) in existing:
                            found[call] += 1
                        else:
                            missing[call] += 1
                    break
    result = {c: {"literals": total[c], "found_from_pvf_root": found[c], "not_found": missing[c]} for c in CALLS}
    text = json.dumps(result, ensure_ascii=False, indent=2)
    print(text)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
