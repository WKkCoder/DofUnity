#!/usr/bin/env python3
"""原生接口目录:按调用点统计脚本需要的每个原生接口(名字、形式、调用次数、涉及的脚本数、实参个数范围、接收者)。

和 sq_inventory.py 的区别:这里同时统计不带 `sq_` 前缀的原生接口(`getVar()`、`isMyControlObject()` 一类),
排除脚本自己定义的函数与 Squirrel 内置方法,并数每个调用点的实参个数(字符串字面量先换成占位符,避免被当成空参数)。

输出:
  docs/sq-api-catalog.tsv   名字与数字(可提交:只有接口名、次数与实参个数,不含脚本内容)
  out/sq_catalog.json       同上加每个脚本需要的接口(本机用)

用法: python3 tools/py/sq_catalog.py [--tsv docs/sq-api-catalog.tsv]
"""
import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
import dnfpaths

KEYWORDS = {
    "if", "for", "foreach", "while", "switch", "return", "function", "catch", "try", "throw", "class", "constructor",
    "local", "else", "do", "case", "default", "break", "continue", "yield", "resume", "typeof", "delete", "in", "instanceof",
    "base", "this", "null", "true", "false", "clone", "static", "enum", "const", "extends",
}
# Squirrel 内置的成员方法(整数、浮点、字符串、数组、表、函数、生成器的方法):不算原生接口
BUILTIN_MEMBERS = {
    "tofloat", "tointeger", "tostring", "tochar", "len", "slice", "find", "tolower", "toupper", "weakref", "append", "push",
    "pop", "top", "insert", "remove", "resize", "extend", "reverse", "sort", "map", "filter", "apply", "reduce", "clear",
    "rawget", "rawset", "rawdelete", "rawin", "keys", "values", "getdelegate", "setdelegate", "call", "acall", "pcall",
    "pacall", "bindenv", "getinfos", "getstatus", "swap", "tostring", "tointeger", "tofloat", "tochar", "hash", "cmp",
}
# Squirrel 内置的自由函数与我们注册的标准库(math、string)
BUILTIN_FREE = {
    "print", "error", "getroottable", "getconsttable", "setroottable", "setconsttable", "assert", "array", "type", "callee",
    "collectgarbage", "resurrectunreachable", "newthread", "seterrorhandler", "setdebughook", "enabledebuginfo", "compilestring",
    "suspend", "dofile", "loadfile", "writeclosuretofile", "regexp", "format", "split", "strip", "lstrip", "rstrip", "escape",
    "abs", "sqrt", "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "exp", "log", "log10", "pow", "floor", "ceil",
    "fabs", "srand", "rand", "min", "max",
}


def clean(src):
    """去掉注释;字符串字面量换成 0(保住实参个数);返回 str(Latin-1 视图)。"""
    out = []
    n = len(src)
    i = 0
    while i < n:
        c = src[i]
        nx = src[i + 1] if i + 1 < n else 0
        if (c == 0x2F and nx == 0x2F) or c == 0x23:
            while i < n and src[i] != 0x0A:
                i += 1
        elif c == 0x2F and nx == 0x2A:
            i += 2
            while i + 1 < n and not (src[i] == 0x2A and src[i + 1] == 0x2F):
                i += 1
            i = min(n, i + 2)
            out.append(" ")
        elif c == 0x40 and nx == 0x22:
            i += 2
            while i < n:
                if src[i] == 0x22:
                    if i + 1 < n and src[i + 1] == 0x22:
                        i += 2
                        continue
                    break
                i += 1
            i = min(n, i + 1)
            out.append("0")
        elif c in (0x22, 0x27):
            q = c
            i += 1
            while i < n and src[i] != q and src[i] != 0x0A:
                i += 2 if src[i] == 0x5C else 1
            i = min(n, i + 1)
            out.append("0")
        else:
            out.append(chr(c))
            i += 1
    return "".join(out)


IDENT_CALL = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)\s*\(")


def count_args(text, open_index):
    """text[open_index] 是 '(';返回实参个数(顶层逗号数 + 1,空括号为 0),找不到配对的右括号返回 -1。"""
    depth = 0
    commas = 0
    has_content = False
    i = open_index
    n = len(text)
    while i < n:
        ch = text[i]
        if ch in "([{":
            depth += 1
            if depth > 1:
                has_content = True
        elif ch in ")]}":
            depth -= 1
            if depth == 0:
                return (commas + 1) if has_content else 0
        elif ch == "," and depth == 1:
            commas += 1
        elif not ch.isspace() and depth >= 1 and ch not in "(":
            has_content = True
        i += 1
    return -1


def scan(text):
    """返回 [(名字, 形式, 接收者, 实参个数)],形式 = def / free / member。"""
    res = []
    for m in IDENT_CALL.finditer(text):
        name = m.group(1)
        if name in KEYWORDS:
            continue
        b = m.start() - 1
        while b >= 0 and text[b] in " \t\r\n":
            b -= 1
        before_word = re.findall(r"[A-Za-z_][A-Za-z0-9_]*$", text[max(0, b - 20): b + 1])
        if before_word and before_word[0] == "function":
            res.append((name, "def", "", 0))
            continue
        paren = m.end() - 1
        args = count_args(text, paren)
        if b >= 0 and text[b] == ".":
            r = b - 1
            while r >= 0 and text[r] in " \t\r\n":
                r -= 1
            rw = re.findall(r"[A-Za-z_][A-Za-z0-9_]*$", text[max(0, r - 60): r + 1])
            res.append((name, "member", rw[0] if rw else "", args))
        else:
            res.append((name, "free", "", args))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tsv", default=os.path.join(dnfpaths.REPO_ROOT, "docs", "sq-api-catalog.tsv") if hasattr(dnfpaths, "REPO_ROOT") else "docs/sq-api-catalog.tsv")
    args = ap.parse_args()

    pvf = D.Pvf()
    nuts = [e for e in pvf.entries if e.path.endswith(".nut")]
    per_file_calls = {}
    defined = set()
    for e in nuts:
        items = scan(clean(pvf.read(e)))
        per_file_calls[e.path] = items
        for name, kind, _, _ in items:
            if kind == "def":
                defined.add(name)

    free = defaultdict(lambda: {"calls": 0, "files": set(), "arity": Counter()})
    member = defaultdict(lambda: {"calls": 0, "files": set(), "arity": Counter(), "receivers": Counter()})
    need_per_file = defaultdict(set)
    for path, items in per_file_calls.items():
        for name, kind, receiver, nargs in items:
            if kind == "def":
                continue
            if kind == "free":
                if name in defined or name in BUILTIN_FREE:
                    continue
                f = free[name]
                f["calls"] += 1
                f["files"].add(path)
                f["arity"][nargs] += 1
                need_per_file[path].add("free:" + name)
            else:
                if name in BUILTIN_MEMBERS and not name.startswith("sq_"):
                    continue
                if name in defined and not name.startswith("sq_"):
                    # 脚本自己定义的函数被当成成员调用的情况(例如表里的方法):不计入原生接口
                    continue
                mm = member[name]
                mm["calls"] += 1
                mm["files"].add(path)
                mm["arity"][nargs] += 1
                mm["receivers"][receiver or "(链式)"] += 1
                need_per_file[path].add("member:" + name)

    # 合并 free/member:接口 = 名字;形式 = 全局 / 成员 / 两者
    names = sorted(set(free) | set(member))
    rows = []
    for n in names:
        fcalls = free[n]["calls"] if n in free else 0
        mcalls = member[n]["calls"] if n in member else 0
        files = (free[n]["files"] if n in free else set()) | (member[n]["files"] if n in member else set())
        arity = Counter()
        if n in free:
            arity.update(free[n]["arity"])
        if n in member:
            arity.update(member[n]["arity"])
        recv = member[n]["receivers"].most_common(4) if n in member else []
        form = "both" if fcalls and mcalls else ("free" if fcalls else "member")
        rows.append({
            "name": n, "prefix": "sq_" if n.startswith("sq_") else "", "form": form, "calls": fcalls + mcalls,
            "free_calls": fcalls, "member_calls": mcalls, "files": len(files),
            "arity_min": min(arity) if arity else -1, "arity_max": max(arity) if arity else -1,
            "arity": {str(k): v for k, v in sorted(arity.items())},
            "receivers": recv,
        })
    rows.sort(key=lambda r: (-r["calls"], r["name"]))
    total = sum(r["calls"] for r in rows)

    # 覆盖曲线:按调用次数从高到低实现时,累计覆盖多少调用点、多少个脚本被完全支持
    order = [r["name"] for r in rows]
    need_sets = {}
    for path, needs in need_per_file.items():
        need_sets[path] = {x.split(":", 1)[1] for x in needs}
    rank = {n: i for i, n in enumerate(order)}
    thresholds = [10, 25, 50, 100, 150, 200, 300, 400, len(order)]
    cumulative = []
    running = 0
    for i, r in enumerate(rows, 1):
        running += r["calls"]
        cumulative.append(running)
    coverage = []
    for t in thresholds:
        t = min(t, len(order))
        implemented = set(order[:t])
        full = sum(1 for p, ns in need_sets.items() if ns <= implemented)
        coverage.append({"top": t, "call_share": round(cumulative[t - 1] / total, 4), "scripts_fully_supported": full, "scripts_total": len(need_sets)})

    os.makedirs(os.path.dirname(args.tsv), exist_ok=True)
    with open(args.tsv, "w", encoding="utf-8") as f:
        f.write("name\tform\tcalls\tfiles\targ_counts\ttop_receivers\n")
        for r in rows:
            arity = ",".join("%s:%d" % (k, v) for k, v in r["arity"].items())
            recv = ",".join("%s:%d" % (a, c) for a, c in r["receivers"])
            f.write("%s\t%s\t%d\t%d\t%s\t%s\n" % (r["name"], r["form"], r["calls"], r["files"], arity, recv))
    out = {
        "summary": {
            "natives": len(rows), "sq_prefixed": sum(1 for r in rows if r["prefix"]), "other": sum(1 for r in rows if not r["prefix"]),
            "calls": total, "script_defined_functions": len(defined), "scripts_with_calls": len(need_sets),
        },
        "coverage": coverage,
        "rows": rows,
        "needs_per_script": {p: sorted(ns) for p, ns in need_sets.items()},
    }
    os.makedirs(dnfpaths.OUT_DIR, exist_ok=True)
    with open(os.path.join(dnfpaths.OUT_DIR, "sq_catalog.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print(json.dumps(out["summary"], ensure_ascii=False))
    for c in coverage:
        print("前 %3d 个接口:覆盖 %5.1f%% 的调用点;完全受支持的脚本 %3d / %d" % (c["top"], 100 * c["call_share"], c["scripts_fully_supported"], c["scripts_total"]))
    print("写入", args.tsv)


if __name__ == "__main__":
    main()
