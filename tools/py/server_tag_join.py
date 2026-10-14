#!/usr/bin/env python3
"""把全库脚本里的标签目录(out/tag_catalog.json)与服务端二进制里的"[标签]"字符串引用表
(re/ghidra/out/server_tag_refs.tsv,由 tools/ghidra/TagRefs.java 生成)对接。

回答三个问题:
  1. 全库 3,408 种开标签里,有多少在服务端二进制里作为字符串出现(= 服务端的加载函数认识它们)?
  2. 按出现次数加权是多少?按文件扩展名分别是多少?
  3. 每个扩展名的标签主要被服务端哪些加载函数引用(给标签语义标注指路)?

输出 out/server_tag_join.json(不入库)与终端摘要。只含标签名、计数与函数名,不含反编译代码。
用法: python3 tools/py/server_tag_join.py [tag_catalog.json] [server_tag_refs.tsv]
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CATALOG = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "out", "tag_catalog.json")
REFS = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "re", "ghidra", "out", "server_tag_refs.tsv")
OUT = os.path.join(ROOT, "out", "server_tag_join.json")


_DEMANGLED = {}


def demangle_all(mangled_names):
    """批量调用 c++filt -n(ELF 符号没有 Mach-O 的前导下划线,所以要 -n)。"""
    import subprocess
    todo = sorted(set(m for m in mangled_names if m not in _DEMANGLED))
    if not todo:
        return
    out = subprocess.run(["c++filt", "-n"], input="\n".join(todo) + "\n", capture_output=True, text=True).stdout.splitlines()
    for m, d in zip(todo, out):
        _DEMANGLED[m] = d


def loader_name(func):
    """'_Z21ImportEquipmentScript...@addr' -> 'ImportEquipmentScript'(去掉参数列表与模板实参;方法保留 Class::method)。"""
    name = _DEMANGLED.get(func.split("@", 1)[0], func.split("@", 1)[0])
    name = re.sub(r"\((?:[^()]|\([^()]*\))*\)(\s*const)?$", "", name)
    for _ in range(3):
        name = re.sub(r"<[^<>]*>", "", name)
    return name


def main():
    catalog = json.load(open(CATALOG, encoding="utf-8"))

    server = {}  # tag -> (refs, [functions])
    with open(REFS, encoding="utf-8") as f:
        next(f)
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 5:
                continue
            tag, _addr, refs, _nf, funcs = parts[0], parts[1], int(parts[2]), int(parts[3]), parts[4]
            fl = [x.strip() for x in funcs.replace(" ...", "").split(" | ") if x.strip()]
            demangle_all(x.split("@", 1)[0] for x in fl)
            old = server.get(tag)
            if old is None:
                server[tag] = (refs, fl)
            else:
                server[tag] = (old[0] + refs, old[1] + fl)

    global_count = Counter()
    per_ext = {}
    for ext, info in catalog["extensions"].items():
        known_tags = 0
        known_occ = 0
        total_occ = 0
        owners = Counter()
        for t in info["tags"]:
            total_occ += t["count"]
            global_count[t["tag"]] += t["count"]
            hit = server.get(t["tag"])
            if hit and hit[0] > 0:
                known_tags += 1
                known_occ += t["count"]
                for fn in set(hit[1]):
                    owners[loader_name(fn)] += 1
        per_ext[ext] = {
            "files": info["files"],
            "distinct_tags": info["distinct_tags"],
            "server_known_tags": known_tags,
            "occurrences": total_occ,
            "server_known_occurrences": known_occ,
            "top_server_loaders": owners.most_common(8),
        }

    distinct = len(global_count)
    known = [t for t in global_count if t in server and server[t][0] > 0]
    total_occ = sum(global_count.values())
    known_occ = sum(global_count[t] for t in known)
    unknown_top = [(t, c) for t, c in global_count.most_common() if t not in server or server[t][0] == 0][:40]

    result = {
        "corpus_distinct_open_tags": distinct,
        "server_known_distinct": len(known),
        "corpus_occurrences": total_occ,
        "server_known_occurrences": known_occ,
        "server_tag_like_strings": len(server),
        "per_extension": per_ext,
        "top_unknown_to_server": unknown_top,
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)

    print("全库不同的开标签: %d;服务端二进制里有引用的: %d(%.1f%%)" % (distinct, len(known), 100.0 * len(known) / distinct))
    print("按出现次数加权: %d / %d(%.1f%%)" % (known_occ, total_occ, 100.0 * known_occ / total_occ))
    print("服务端的 [标签] 字符串总数(含闭合标签): %d" % len(server))
    print()
    print("%-6s %7s %6s %10s %10s  %s" % ("ext", "files", "tags", "srv-known", "occ-cover", "服务端主要引用函数(括号内是它引用的不同标签数)"))
    for ext, v in sorted(per_ext.items(), key=lambda kv: -kv[1]["occurrences"])[:30]:
        cover = 100.0 * v["server_known_occurrences"] / v["occurrences"] if v["occurrences"] else 0.0
        owners = ", ".join("%s(%d)" % (o, n) for o, n in v["top_server_loaders"][:3])
        print("%-6s %7d %6d %10d %9.1f%%  %s" % (ext, v["files"], v["distinct_tags"], v["server_known_tags"], cover, owners))
    print()
    print("服务端不认识、但出现次数最多的标签(前 15):")
    for t, c in unknown_top[:15]:
        print("  %-40s %d" % (t, c))
    print("已写入", OUT)


if __name__ == "__main__":
    main()
