#!/usr/bin/env python3
"""把客户端里"引用了标签字符串的函数"与全库各扩展名的标签集合对上,找出每种脚本的加载函数候选。

输入:
  - re/notes/string_calls.tsv      字符串访问函数的调用点(tools/ghidra/StringCalls.java 产出)
  - re/notes/strings_decoded.tsv   解码后的数据块(tools/py/decode_strings.py 产出)
  - out/tag_catalog.json           全库标签目录(tools/py/tag_catalog.py 产出)
  - re/notes/func_coverage_all.tsv 可选:函数之间的空隙表(tools/ghidra/FuncCoverage.java 产出,最小空隙 16)。
                                   调用点落在"不属于任何函数"的地址时,按空隙表归到紧挨着它前面的那个函数
                                   (推断:switch 的 case 代码紧跟在宿主函数后面)。

做法:对每个扩展名,拿它的标签集合与每个函数引用的"[标签]"字符串集合求交,交集最大的函数就是候选。
这只是"哪个函数认识这些标签"的线索,不证明那个函数就是解析器;每个候选还要读代码核实。

输出:终端摘要;--json 指定的文件(不入库)。只含地址、标签名与计数,不含反编译代码、不含解码文本的全集。
用法: python3 tools/py/loader_map.py [--calls F] [--decoded F] [--catalog F] [--gaps F] [--json F] [--min-tags N]
"""
import argparse
import bisect
import collections
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TAG = re.compile(r"\[[^\]\n]+\]")


def norm(va):
    return va.lower().replace("0x", "").zfill(8)


def read_tsv(path, skip_header=False):
    with open(path, encoding="utf-8", newline="\n") as fh:
        rows = [ln.rstrip("\n").split("\t") for ln in fh.read().split("\n") if ln.strip()]
    return rows[1:] if skip_header else rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--calls", default=os.path.join(ROOT, "re/notes/string_calls.tsv"))
    ap.add_argument("--decoded", default=os.path.join(ROOT, "re/notes/strings_decoded.tsv"))
    ap.add_argument("--catalog", default=os.path.join(ROOT, "out/tag_catalog.json"))
    ap.add_argument("--gaps", default="")
    ap.add_argument("--json", default="")
    ap.add_argument("--min-tags", type=int, default=8)
    args = ap.parse_args()

    catalog = json.load(open(args.catalog, encoding="utf-8"))
    by_ext = {e: {t["tag"]: t["count"] for t in v["tags"]} for e, v in catalog["extensions"].items()}
    corpus = collections.Counter()
    for tags in by_ext.values():
        for t, n in tags.items():
            corpus[t] += n
    total_occ = sum(corpus.values())

    decoded = {}
    for r in read_tsv(args.decoded, skip_header=True):
        if len(r) >= 5:
            decoded[norm(r[0])] = r[4]

    gaps = []
    if args.gaps and os.path.exists(args.gaps):
        for r in read_tsv(args.gaps):
            gaps.append((int(r[0], 16), int(r[1], 16), r[6]))
        gaps.sort()
    starts = [g[0] for g in gaps]

    def parent_of(addr):
        i = bisect.bisect_right(starts, addr) - 1
        if i >= 0 and gaps[i][0] <= addr <= gaps[i][1] and gaps[i][2] != "-":
            return gaps[i][2]
        return ""

    funcs = collections.defaultdict(set)
    orphan = reassigned = 0
    for r in read_tsv(args.calls):
        if len(r) < 5 or not r[4]:
            continue
        owner = r[1]
        if owner == "":
            orphan += 1
            owner = parent_of(int(r[0], 16))
            if owner:
                reassigned += 1
        text = decoded.get(norm(r[4]))
        if text and TAG.fullmatch(text):
            funcs[owner].add(text)

    client_tags = set()
    for r in read_tsv(args.decoded, skip_header=True):
        if len(r) >= 5 and TAG.fullmatch(r[4]):
            client_tags.add(r[4])
    hit = [t for t in corpus if t in client_tags]
    print("客户端里的 [..] 形式字符串:%d 个;全库 %d 种开标签里有 %d 种在其中,按出现次数加权 %.2f%%"
          % (len(client_tags), len(corpus), len(hit), 100.0 * sum(corpus[t] for t in hit) / total_occ))
    print("没有所属函数的调用点 %d 个,按空隙表归到前一个函数的 %d 个" % (orphan, reassigned))

    result = {}
    print("\n扩展名  标签数  出现次数  最佳候选(函数入口 交集/函数引用的标签数 覆盖该扩展名的比例)")
    for ext in sorted(by_ext, key=lambda e: -sum(by_ext[e].values())):
        tags = set(by_ext[ext])
        if len(tags) < args.min_tags:
            continue
        cand = []
        for owner, fset in funcs.items():
            if not owner:
                continue
            ov = len(fset & tags)
            if ov >= 3:
                cand.append((ov, owner, len(fset)))
        cand.sort(reverse=True)
        top = cand[:3]
        result[ext] = {"tags": len(tags), "occurrences": sum(by_ext[ext].values()),
                       "candidates": [{"function": o, "overlap": ov, "function_tags": n,
                                       "coverage": round(ov / len(tags), 3)} for ov, o, n in top]}
        print("%-7s %5d %9d  %s" % (ext, len(tags), sum(by_ext[ext].values()),
                                    "; ".join("%s %d/%d %.0f%%" % (o, ov, n, 100.0 * ov / len(tags)) for ov, o, n in top)))
    if args.json:
        os.makedirs(os.path.dirname(args.json), exist_ok=True)
        with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(result, fh, ensure_ascii=False, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
