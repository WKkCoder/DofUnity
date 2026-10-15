#!/usr/bin/env python3
"""把本机 re/notes/act_cards/ 下各阅读小组交来的索引表(cards*.tsv,每行一张卡的一行摘要)合并成一张表,
写到 docs/act-semantics.tsv(入库)。

只合并"摘要行"(类别、编号、标签、一句话含义、置信度、未解决点),不碰卡片正文。
合并规则:编号统一成 0x 开头的十六进制(运行器与函数用地址);同一(类别,编号)有多行时留置信度高的、再留摘要长的;
反编译器自动命名(FUN_xxxxxxxx)改写成"函数 0xxxxxxxx",保证入库文本里没有它们。

用法:python3 tools/py/merge_cards.py [--cards <目录>] [--out <文件>]
"""
import argparse
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RANK = {"高": 3, "中": 2, "低": 1}
ORDER = {"runner": 0, "func": 1, "sel": 2, "cond": 3, "beh": 4}


def norm_code(kind, code):
    code = code.strip()
    if kind in ("beh", "cond", "sel"):
        c = code[2:] if code.lower().startswith("0x") else code
        return "0x%02x" % int(c, 16)
    if kind in ("runner", "func") and re.fullmatch(r"[0-9a-fA-F]{6,8}", code):
        return "0x%08x" % int(code, 16)
    return code


def clean(text):
    return re.sub(r"FUN_([0-9a-fA-F]{8})", lambda m: "函数 0x" + m.group(1).lower(), text)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cards", default=os.path.join(ROOT, "re/notes/act_cards"))
    ap.add_argument("--out", default=os.path.join(ROOT, "docs/act-semantics.tsv"))
    args = ap.parse_args()
    rows = {}
    n_in = 0
    for path in sorted(glob.glob(os.path.join(args.cards, "cards*.tsv"))):
        for line in open(path, encoding="utf-8"):
            line = line.rstrip("\n")
            if not line.strip():
                continue
            p = line.split("\t")
            if len(p) < 6:
                p += [""] * (6 - len(p))
            kind, code, tag, summary, conf, unknown = [x.strip() for x in p[:6]]
            if kind not in ORDER:
                print("跳过未知类别:", kind, file=sys.stderr)
                continue
            n_in += 1
            code = norm_code(kind, code)
            row = (kind, code, clean(tag), clean(summary), conf, clean(unknown))
            key = (kind, code)
            old = rows.get(key)
            if old is None or (RANK.get(conf, 0), len(summary)) > (RANK.get(old[4], 0), len(old[3])):
                rows[key] = row
    out = sorted(rows.values(), key=lambda r: (ORDER[r[0]], r[1]))
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("kind\tcode\ttag\tsummary\tconfidence\topen\n")
        for r in out:
            f.write("\t".join(r) + "\n")
    by = {}
    for r in out:
        by[r[0]] = by.get(r[0], 0) + 1
    print("读入 %d 行,合并成 %d 行:%s -> %s" % (n_in, len(out), by, args.out))


if __name__ == "__main__":
    main()
