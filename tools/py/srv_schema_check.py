#!/usr/bin/env python3
"""服务端加载函数的"标签 -> 读取形状"表 与 全库实际出现的取值形状 对账(只读,静态)。

用法:
    python3 tools/py/srv_schema_check.py [输出目录,缺省 out/srv_schema]

输入:
  out/srv_loaders/<函数地址>.asm.txt     DumpFunction.java(DNF_SKIP_DECOMPILE=1)产出的加载函数反汇编
  out/stats_<扩展名>.json                dnf script-stats --ext <扩展名> --json 产出的全库标签统计
输出(都在输出目录下,不入库):
  <ext>.tsv    每个加载函数的标签表(srv_schema.py 的格式)
  summary.json 每种脚本类型的对账汇总
终端打印:每种脚本类型的标签覆盖(按出现次数加权)、形状一致率、一致性最差的标签。

形状字母:I 整数(含十六进制)、F 浮点、S 字符串(含字符串表引用、括号串)、T/M 混合(通配)。
对账规则(按出现次数加权):
  exact   服务端形状(去掉 *)与全库这一行的形状完全一致
  prefix  全库这一行的取值比服务端读的少(可选的尾部取值,服务端读不到就留默认)
  loose   服务端形状带 *(分支体里有循环或条件跳转),只检查字母类型兼容
  unknown 服务端形状为空(分支体用了本脚本不认识的辅助函数读值,要人工读)
  differ  其他
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
from read_elf_va import Elf32  # noqa: E402
from srv_annotate import Symbols  # noqa: E402
import srv_schema  # noqa: E402

LOADERS = [
    ("equ", "08991aeb", "ImportEquipmentScript"),
    ("stk", "089aeec8", "ImportStackableScript"),
    ("atk", "088d145a", "ImportAttackInfoScript"),
    ("mob", "08a12827", "ImportMonsterScript"),
    ("map", "089dcf54", "ImportMapScript"),
    ("qst", "08a61813", "importQuestScript"),
    ("obj", "08a2c51a", "ImportPassiveObjectScript"),
    ("skl", "08a9792f", "ImportSkillScript"),
    ("dgn", "088e5faa", "ImportDungeonScript"),
    ("aic", "088c9ccc", "importAICharacterCommonScript"),
    ("npc", "089fc264", "importNpcScript"),
    ("cre", "08a35e47", "importCreatureScript"),
    ("chr", "08a0ce06", "ImportCharacterScript"),
    ("til", "089dcbb6", "ImportTileScript"),
    ("etc", "08a870b7", "importServerParameterScript"),
    ("pet", "08a358d7", "importPetScript"),
    ("twn", "08aadaa8", "importTownScript"),
]

LETTER = {"i": "I", "h": "I", "f": "F", "s": "S", "k": "S", "p": "S"}


def norm_corpus(shape):
    out = []
    for tok in shape.split(" "):
        if tok in ("", "|"):
            continue
        out.append(LETTER.get(tok, "?"))
    return "".join(out)


def compatible(server_letters, corpus_letters):
    """逐位比较:T/M 通配;其他必须相同。"""
    if len(server_letters) != len(corpus_letters):
        return False
    for a, b in zip(server_letters, corpus_letters):
        if a in "TM":
            continue
        if a != b:
            return False
    return True


def prefix_ok(server_letters, corpus_letters):
    if len(corpus_letters) >= len(server_letters):
        return False
    return compatible(server_letters[:len(corpus_letters)], corpus_letters)


def classify(server_shape, corpus_shape):
    star = server_shape.endswith("*")
    sl = server_shape.rstrip("*")
    cl = norm_corpus(corpus_shape)
    if sl == "":
        return "unknown"
    if star:
        # 带循环或条件的分支体:只要全库的取值不比服务端读的"最少个数"更少、类型兼容就算 loose
        n = min(len(sl), len(cl))
        if compatible(sl[:n], cl[:n]):
            return "loose"
        return "differ"
    if compatible(sl, cl):
        return "exact"
    if prefix_ok(sl, cl):
        return "prefix"
    return "differ"


def main(argv):
    out_dir = argv[0] if argv else os.path.join(ROOT, "out", "srv_schema")
    os.makedirs(out_dir, exist_ok=True)
    elf = Elf32()
    syms = Symbols()
    summary = {}
    print("%-5s %-32s %6s %6s | %8s %8s %8s %8s %8s" % (
        "类型", "加载函数", "分支", "覆盖%", "exact", "prefix", "loose", "differ", "unknown"))
    for ext, addr, name in LOADERS:
        asm = os.path.join(ROOT, "out", "srv_loaders", addr + ".asm.txt")
        stats_path = os.path.join(ROOT, "out", "stats_%s.json" % ext)
        if not os.path.exists(asm) or not os.path.exists(stats_path):
            print("%-5s 缺文件,跳过" % ext)
            continue
        rows = srv_schema.extract(asm, elf, syms)
        with open(os.path.join(out_dir, ext + ".tsv"), "w", encoding="utf-8") as f:
            f.write("code\ttag\tstyle\tstart\trun_start\trun_end\tshape\tsteps\n")
            for codes, tag, style, st, rs, end, shape, steps in rows:
                f.write("%s\t%s\t%s\t%08x\t%s\t%08x\t%s\t%s\n" % (
                    ",".join("0x%x" % c for c in codes), tag or "", style, st,
                    ("%08x" % rs) if rs else "", end, shape, "; ".join(steps)))
        by_tag = {}
        for codes, tag, style, st, rs, end, shape, steps in rows:
            if tag:
                by_tag.setdefault(tag.lower(), (shape, steps, codes))
        stats = json.load(open(stats_path, encoding="utf-8"))["tags"]
        total = 0
        covered = 0
        cls_count = {"exact": 0, "prefix": 0, "loose": 0, "differ": 0, "unknown": 0}
        worst = []
        uncovered = []
        for tag, info in stats.items():
            if tag.startswith("[/"):
                continue
            cnt = info["count"]
            total += cnt
            if tag in by_tag:
                covered += cnt
                shape = by_tag[tag][0]
                tag_diff = 0
                for cshape, ccount in info["shapes"].items():
                    c = classify(shape, cshape)
                    cls_count[c] += ccount
                    if c == "differ":
                        tag_diff += ccount
                if tag_diff:
                    worst.append((tag_diff, tag, shape, dict(sorted(info["shapes"].items(), key=lambda kv: -kv[1])[:3])))
            else:
                uncovered.append((cnt, tag))
        worst.sort(reverse=True)
        uncovered.sort(reverse=True)
        n_branch = sum(1 for r in rows if r[1])
        cov = 100.0 * covered / total if total else 0.0
        denom = sum(cls_count.values()) or 1
        print("%-5s %-32s %6d %6.1f | %7.1f%% %7.1f%% %7.1f%% %7.1f%% %7.1f%%" % (
            ext, name, n_branch, cov, *(100.0 * cls_count[k] / denom for k in ("exact", "prefix", "loose", "differ", "unknown"))))
        for cnt, tag, shape, shapes in worst[:3]:
            print("      不一致 %-34s 服务端 %-8s 全库 %s(%d 行)" % (tag, shape, shapes, cnt))
        for cnt, tag in uncovered[:3]:
            print("      服务端没有 %-32s %d 次" % (tag, cnt))
        summary[ext] = {
            "loader": name, "branches": n_branch, "coverage": round(cov, 2),
            "lines": cls_count, "worst": [(t, s, c) for c, t, s, _ in worst[:10]],
            "uncovered": [(t, c) for c, t in uncovered[:20]],
        }
    with open(os.path.join(out_dir, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
