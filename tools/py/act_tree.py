#!/usr/bin/env python3
"""对 .act 全库做"闭合标签驱动"的结构解析,统计每个块标签的子标签集合(研究用,读 re/dumps 的本机文本)。
这是 C# `ActParser`(packages/com.dnfunity.game/Runtime/Act/ActParser.cs)的独立转写,用来对拍(证据 E-050)。

规则:
  1. 带 `[/X]` 闭合的标签是块,没有被闭合的是叶子;配对用"最近的同名开标签"(栈匹配)。
  2. 能做块的标签集合 = 全库里出现过配对闭合的标签(第一遍统计出来)+ 加载函数里认识闭合、但全库没出现过的 6 个。
  3. 笔误闭合:名字对不上任何开标签、且名字本身不是能做块的标签(如 [/TIRGGER]),
     与"最内层的、能做块的开标签"配对;名字是能做块的标签却找不到开标签的(多写的 [/BEHAVIOR])丢弃。
输出 JSON(默认 out/act_tree.json):
  blocks / leaves: {标签: 次数};children: {父块标签或 "<root>": {子标签: 次数}};parents: 反向表
  stray / repaired: 丢弃与按最内层配对的闭合个数

用法:python3 tools/py/act_tree.py [--dumps re/dumps] [--ext act] [--out out/act_tree.json]
"""
import argparse
import collections
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TAG = re.compile(r"^(\[[^\]]*\])\s*(.*)$")
# 加载函数 importActionScript(0x01333530)里认识闭合、但全库没出现过的块标签(见 E-050)
LOADER_ONLY_BLOCKS = {"[CAMERA TRACE]", "[EVENT RHYTHM ACTION]", "[MASTER APPENDAGE]", "[NO BUFF APPENDAGE]",
                      "[SET MOVABLE AREA]", "[SET SPEED CONTROL]"}


def read_tags(path):
    toks = []
    for ln in open(path, encoding="utf-8"):
        m = TAG.match(ln.rstrip("\n"))
        if m:
            toks.append(m.group(1))
    return toks


def is_closer(tag):
    return len(tag) > 2 and tag.startswith("[/")


def exact_match(toks):
    """只做精确同名栈匹配,返回 {开标签下标: 闭合下标}。"""
    stack = []
    pairs = {}
    for i, tag in enumerate(toks):
        if is_closer(tag):
            name = "[" + tag[2:]
            for j in range(len(stack) - 1, -1, -1):
                if toks[stack[j]] == name:
                    pairs[stack[j]] = i
                    del stack[j:]
                    break
        else:
            stack.append(i)
    return pairs


def full_match(toks, block_tags):
    stack = []
    pairs = {}
    stray = repaired = 0
    for i, tag in enumerate(toks):
        if not is_closer(tag):
            stack.append(i)
            continue
        name = "[" + tag[2:]
        at = -1
        for j in range(len(stack) - 1, -1, -1):
            if toks[stack[j]] == name:
                at = j
                break
        fixed = False
        if at < 0 and name not in block_tags:
            for j in range(len(stack) - 1, -1, -1):
                if toks[stack[j]] in block_tags:
                    at = j
                    fixed = True
                    break
        if at < 0:
            stray += 1
            continue
        if fixed:
            repaired += 1
        pairs[stack[at]] = i
        del stack[at:]
    return pairs, stray, repaired


def tree_edges(toks, pairs):
    """返回 [(标签, 父标签, 是否块)];父用 "<root>" 表示顶层。"""
    # 闭合下标 -> 开标签下标,用来弹栈
    closer_of = {c: o for o, c in pairs.items()}
    edges = []
    open_blocks = []  # 开标签下标
    for i, tag in enumerate(toks):
        if is_closer(tag):
            o = closer_of.get(i)
            if o is not None and o in open_blocks:
                k = open_blocks.index(o)
                del open_blocks[k:]
            continue
        parent = toks[open_blocks[-1]] if open_blocks else "<root>"
        edges.append((tag, parent, i in pairs))
        if i in pairs:
            open_blocks.append(i)
    return edges


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dumps", default=os.path.join(ROOT, "re", "dumps"))
    ap.add_argument("--ext", default="act")
    ap.add_argument("--out", default=os.path.join(ROOT, "out", "act_tree.json"))
    a = ap.parse_args()
    out_abs = os.path.abspath(a.out)
    if not any(out_abs.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
        sys.exit("拒绝:输出必须在 re/ 或 out/ 下")
    suffix = "." + a.ext + ".txt"
    paths = []
    for dp, _, fn in os.walk(a.dumps):
        for f in fn:
            if f.endswith(suffix):
                paths.append(os.path.join(dp, f))
    paths.sort()
    files_toks = [read_tags(p) for p in paths]

    # 第一遍:全库里哪些标签出现过配对闭合
    seen_blocks = set()
    for toks in files_toks:
        for o in exact_match(toks):
            seen_blocks.add(toks[o])
    block_tags = seen_blocks | LOADER_ONLY_BLOCKS

    parents = collections.defaultdict(collections.Counter)
    children = collections.defaultdict(collections.Counter)
    blocks = collections.Counter()
    leaves = collections.Counter()
    stray = repaired = 0
    nodes = 0
    for toks in files_toks:
        pairs, s, r = full_match(toks, block_tags)
        stray += s
        repaired += r
        for tag, parent, is_block in tree_edges(toks, pairs):
            nodes += 1
            parents[tag][parent] += 1
            children[parent][tag] += 1
            (blocks if is_block else leaves)[tag] += 1
    res = {
        "files": len(paths),
        "nodes": nodes,
        "stray": stray,
        "repaired": repaired,
        "block_tags": sorted(block_tags),
        "blocks": dict(blocks),
        "leaves": dict(leaves),
        "parents": {k: dict(v) for k, v in parents.items()},
        "children": {k: dict(v) for k, v in children.items()},
    }
    os.makedirs(os.path.dirname(out_abs), exist_ok=True)
    json.dump(res, open(out_abs, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("文件", len(paths), "节点", nodes, "块", sum(blocks.values()), "块标签", len(blocks), "(能做块的标签", len(block_tags), ")",
          "叶子标签", len(leaves), "多余闭合", stray, "按最内层配对", repaired)


if __name__ == "__main__":
    main()
