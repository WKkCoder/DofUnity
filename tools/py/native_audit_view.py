#!/usr/bin/env python3
"""把一个(或一批)原生接口的审计材料并排打印出来,供人工对照:

    我们的登记(状态与说明) | 脚本里的用法(调用次数与接收者) | 客户端注册表里的实现地址 | 实现函数的反汇编

用法:
    python3 tools/py/native_audit_view.py [--asm] [--c] 名字 [名字 ...]
    python3 tools/py/native_audit_view.py --top 20 --skip 0      # 按脚本调用次数从高到低

输入(都是本机产物,不入库):
    out/game_check_quick.json    `dnf game-check --json` 的 coverage.natives(我们的登记)
    docs/sq-api-catalog.tsv      脚本调用次数(入库)
    re/notes/sq_registry.tsv     客户端注册表(E-042)
    re/notes/natives/<地址>.{asm,c}.txt   Ghidra 导出的实现函数(DumpFunction.java)

输出只用于阅读,不写文件;反编译文本属于逆向产物,不进台账,台账里只写自己的结论与地址。
"""
import argparse
import csv
import json
import os
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load():
    nat = {}
    path = os.path.join(ROOT, "out", "game_check_quick.json")
    if os.path.exists(path):
        for x in json.load(open(path, encoding="utf-8"))["coverage"]["natives"]:
            nat[x["name"].split(".")[-1]] = x
    cat = {}
    for r in csv.DictReader(open(os.path.join(ROOT, "docs", "sq-api-catalog.tsv"), newline="\n", encoding="utf-8"), delimiter="\t"):
        cat[r["name"]] = r
    reg = defaultdict(list)
    for r in csv.DictReader(open(os.path.join(ROOT, "re", "notes", "sq_registry.tsv"), newline="\n", encoding="utf-8"), delimiter="\t"):
        reg[r["name"]].append(r)
    return nat, cat, reg


def read(path, limit):
    if not os.path.exists(path):
        return None
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    if len(lines) > limit:
        lines = lines[:limit] + ["... (截断,共 %d 行)" % len(lines)]
    return "\n".join(lines)


def show(name, nat, cat, reg, want_asm, want_c, limit):
    c = cat.get(name)
    n = nat.get(name)
    print("=" * 100)
    print("%s   调用 %s 次/%s 个文件;接收者 %s" % (name, c["calls"] if c else "-", c["files"] if c else "-", (c["top_receivers"] if c else "-")[:90]))
    if n:
        print("  我们:%s %s — %s" % (n["status"], n["forms"], n["note"]))
    else:
        print("  我们:未登记")
    impls = []
    for r in reg.get(name, []):
        if r["impl"]:
            impls.append(r["impl"])
            print("  客户端:登记点 %s 实现 %s 绑定 %s 形状 %s" % (r["site"], r["impl"], r["register"] or "-", r["shape"]))
    if not reg.get(name):
        print("  客户端:注册表里没有这个名字")
    for a in sorted(set(impls)):
        base = os.path.join(ROOT, "re", "notes", "natives", a)
        if want_asm:
            t = read(base + ".asm.txt", limit)
            print("  --- 反汇编 %s" % a)
            print(t if t else "  (没有导出)")
        if want_c:
            t = read(base + ".c.txt", limit)
            print("  --- 反编译 %s(Ghidra 自动命名,仅供阅读)" % a)
            print(t if t else "  (没有导出)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="*")
    ap.add_argument("--asm", action="store_true")
    ap.add_argument("--c", action="store_true")
    ap.add_argument("--top", type=int, default=0)
    ap.add_argument("--skip", type=int, default=0)
    ap.add_argument("--limit", type=int, default=60)
    a = ap.parse_args()
    nat, cat, reg = load()
    names = list(a.names)
    if a.top:
        order = sorted(cat.values(), key=lambda r: -int(r["calls"]))
        names += [r["name"] for r in order[a.skip:a.skip + a.top]]
    if not names:
        ap.print_help()
        return 1
    for nm in names:
        show(nm, nat, cat, reg, a.asm, a.c, a.limit)
    return 0


if __name__ == "__main__":
    sys.exit(main())
