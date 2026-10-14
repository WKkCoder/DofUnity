"""统计 .skl 里 [executable states] 标签的出现次数,核对"该标签在一个文件里可以出现多次,引擎取它们的并集"这个假设(E-038)。

做法:对全库每个 .skl(标记流脚本),取出所有 [executable states] 标签行,看
  - 每个文件出现几次(0 / 1 / 2 次以上);
  - 出现多次时,后面几次里有没有第一次没有的状态编号(并集与"只取第一次"是否不同);
  - 该标签的取值个数分布。
读取是只读的,输出只有计数。非 REA。

依据的说明:这里只能数出文件里有多少个、取值怎样;"引擎实际取并集"并不能由数据本身证明,是 Dnf.Game 采用的推断。

用法:python3 tools/py/skl_executable_probe.py [--json out/skl_executable_probe.json]
"""
import argparse
import json
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
import dnfscripts_lite as L

TAG = "[executable states]"


def ints_of(values):
    out = []
    for v in values:
        for part in v.replace("\t", " ").split():
            try:
                out.append(int(part))
            except ValueError:
                pass
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    pvf = D.Pvf()
    files = 0
    occurrences = Counter()
    union_differs = 0
    later_adds_states = 0
    values_per_occurrence = Counter()
    for e in pvf.entries:
        if not e.path.endswith(".skl"):
            continue
        rows = L.lines(pvf, e.path)
        if not rows:
            continue
        files += 1
        sets = [ints_of(vals) for tag, vals in rows if tag.lower() == TAG]
        occurrences[min(len(sets), 3)] += 1
        for s in sets:
            values_per_occurrence[min(len(s), 10)] += 1
        if len(sets) >= 2:
            first = set(sets[0])
            union = set().union(*[set(s) for s in sets])
            if union != first:
                union_differs += 1
            if any(set(s) - first for s in sets[1:]):
                later_adds_states += 1
    result = {
        "skl_files_read": files,
        "files_by_occurrence_count": {"0": occurrences[0], "1": occurrences[1], "2": occurrences[2], "3+": occurrences[3]},
        "files_where_union_differs_from_first": union_differs,
        "files_where_a_later_occurrence_adds_states": later_adds_states,
        "values_per_occurrence_capped_at_10": dict(sorted(values_per_occurrence.items())),
    }
    text = json.dumps(result, ensure_ascii=False, indent=2)
    print(text)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
