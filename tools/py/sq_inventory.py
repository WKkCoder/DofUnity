"""统计 PVF 里明文 Squirrel 脚本(`.nut`)调用了哪些 `sq_*` 接口、各调用多少次(P4 的实现顺序依据)。

做法(非 REA,只读 Script.pvf):
  1. 取出全部 `.nut`,按字节读入(用 latin1 当 8 位字节看,标识符只有 ASCII);
  2. 去掉 `//`、`#`、`/* */` 注释与字符串字面量(含 `@"..."` 的逐字字符串),避免把注释里的名字算进去;
  3. 找标识符:后面紧跟 `(` 的算"调用",前面是 `function` 的算"定义",其余算"提及";
  4. 前面是 `.` 的算"成员调用",单独统计(游戏对象的方法也是原生接口,但名字不带 sq_ 前缀)。

输出:
  out/sq_inventory.json   完整统计(只有名字与数字,不含脚本内容);out/ 已被 .gitignore 排除
  标准输出               摘要与调用频次最高的名字

用法:
  python3 tools/py/sq_inventory.py [--top 40]
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

IDENT = re.compile(rb"[A-Za-z_][A-Za-z0-9_]*")


def strip_noise(src):
    """去掉注释与字符串字面量,保持长度不变(被去掉的字符换成空格,换行保留),返回新的 bytes。"""
    out = bytearray(src)
    n = len(src)
    i = 0
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else 0
        if c == 0x2F and nxt == 0x2F or c == 0x23:  # // 或 #  行注释
            j = src.find(b"\n", i)
            j = n if j < 0 else j
            for k in range(i, j):
                out[k] = 0x20
            i = j
        elif c == 0x2F and nxt == 0x2A:  # /* */
            j = src.find(b"*/", i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if src[k] != 0x0A:
                    out[k] = 0x20
            i = j
        elif c == 0x40 and nxt == 0x22:  # @"逐字字符串":两个引号表示一个引号
            j = i + 2
            while j < n:
                if src[j] == 0x22:
                    if j + 1 < n and src[j + 1] == 0x22:
                        j += 2
                        continue
                    break
                j += 1
            j = min(n, j + 1)
            for k in range(i, j):
                if src[k] != 0x0A:
                    out[k] = 0x20
            i = j
        elif c == 0x22:  # "字符串":反斜杠转义
            j = i + 1
            while j < n and src[j] != 0x22 and src[j] != 0x0A:
                j += 2 if src[j] == 0x5C else 1
            j = min(n, j + 1)
            for k in range(i, j):
                out[k] = 0x20
            i = j
        elif c == 0x27:  # '字符'
            j = i + 1
            while j < n and src[j] != 0x27 and src[j] != 0x0A:
                j += 2 if src[j] == 0x5C else 1
            j = min(n, j + 1)
            for k in range(i, j):
                out[k] = 0x20
            i = j
        else:
            i += 1
    return bytes(out)


def scan(clean):
    """返回 [(名字, 种类)],种类是 call / def / member_call / mention。"""
    res = []
    for m in IDENT.finditer(clean):
        name = m.group().decode("ascii")
        s, e = m.span()
        # 后面的第一个非空白字符
        k = e
        while k < len(clean) and clean[k] in b" \t\r\n":
            k += 1
        is_call = k < len(clean) and clean[k] == 0x28
        # 前面的第一个非空白字符与单词
        b = s - 1
        while b >= 0 and clean[b] in b" \t\r\n":
            b -= 1
        prev_dot = b >= 0 and clean[b] == 0x2E
        prev_word = b"".join(re.findall(rb"[A-Za-z_][A-Za-z0-9_]*$", clean[max(0, b - 20): b + 1]))
        receiver = ""
        if prev_dot:
            r = b - 1
            while r >= 0 and clean[r] in b" \t\r\n":
                r -= 1
            rw = re.findall(rb"[A-Za-z_][A-Za-z0-9_]*$", clean[max(0, r - 60): r + 1])
            receiver = rw[0].decode("ascii") if rw else ""
        if prev_word == b"function":
            res.append((name, "def", ""))
        elif prev_dot and is_call:
            res.append((name, "member_call", receiver))
        elif prev_dot:
            res.append((name, "member_ref", receiver))
        elif is_call:
            res.append((name, "call", ""))
        else:
            res.append((name, "mention", ""))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--dump", metavar="目录", help="把全部 .nut 原样写到这个目录(保持 PVF 里的相对路径),只用于本机分析,不入库;用来给上游 sq 命令行逐个编译")
    args = ap.parse_args()

    pvf = D.Pvf()
    nuts = [e for e in pvf.entries if e.path.endswith(".nut")]
    if args.dump:
        for e in nuts:
            target = os.path.join(args.dump, *e.path.split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as f:
                f.write(pvf.read(e))
        print("已写出 %d 个 .nut 到 %s" % (len(nuts), args.dump))
    per = {k: Counter() for k in ("call", "def", "member_call", "member_ref", "mention")}
    files_with = {k: defaultdict(set) for k in per}
    receivers = defaultdict(Counter)
    per_file = defaultdict(Counter)  # 路径 -> {sq_ 名字: 调用次数}
    other_per_file = defaultdict(Counter)  # 路径 -> {非 sq_ 的成员或自由调用名字: 调用次数}
    total_bytes = 0
    for e in nuts:
        data = pvf.read(e)
        total_bytes += len(data)
        for name, kind, receiver in scan(strip_noise(data)):
            per[kind][name] += 1
            files_with[kind][name].add(e.path)
            if kind == "member_call":
                receivers[name][receiver or "(链式或表达式)"] += 1
            if name.startswith("sq_") and kind in ("call", "member_call"):
                per_file[e.path][name] += 1
            elif kind == "member_call":
                other_per_file[e.path][name] += 1
            elif kind == "call":
                other_per_file[e.path]["free:" + name] += 1

    defined = set(per["def"])
    sq_all = set(n for k in per for n in per[k] if n.startswith("sq_"))
    native = sorted(n for n in sq_all if n not in defined)

    def total_calls(n):
        return per["call"][n] + per["member_call"][n]

    def files_of(n):
        return files_with["call"][n] | files_with["member_call"][n]

    sq_called = {n: total_calls(n) for n in native if total_calls(n) > 0}
    sq_defined_here = sorted(n for n in sq_all if n in defined)
    sq_never_called = sorted(n for n in native if total_calls(n) == 0)

    # 没有 sq_ 前缀、也没有在脚本里定义的调用:可能是 Squirrel 标准库,也可能是别的原生接口
    other_free = {n: c for n, c in per["call"].items() if not n.startswith("sq_") and n not in defined}
    members = {n: c for n, c in per["member_call"].items() if not n.startswith("sq_") and n not in defined}

    summary = {
        "nut_files": len(nuts),
        "nut_bytes": total_bytes,
        "sq_distinct_names": len(sq_all),
        "sq_native_names": len(native),
        "sq_called_names": len(sq_called),
        "sq_total_calls": sum(sq_called.values()),
        "sq_global_calls": sum(per["call"][n] for n in native),
        "sq_member_calls": sum(per["member_call"][n] for n in native),
        "sq_names_global_only": sum(1 for n in sq_called if per["member_call"][n] == 0),
        "sq_names_member_only": sum(1 for n in sq_called if per["call"][n] == 0),
        "sq_names_both": sum(1 for n in sq_called if per["call"][n] > 0 and per["member_call"][n] > 0),
        "sq_defined_in_scripts": sq_defined_here,
        "sq_never_called": sq_never_called,
        "other_free_calls_distinct": len(other_free),
        "member_calls_nonsq_distinct": len(members),
        "script_defined_functions": len(defined),
    }
    out = {
        "summary": summary,
        "sq_calls": dict(sorted(sq_called.items(), key=lambda kv: (-kv[1], kv[0]))),
        "sq_call_files": {n: len(files_of(n)) for n in sq_called},
        "sq_global_calls": {n: per["call"][n] for n in sq_called},
        "sq_member_calls": {n: per["member_call"][n] for n in sq_called},
        "sq_receivers": {n: dict(receivers[n].most_common(6)) for n in sq_called if receivers[n]},
        "other_free_calls": dict(sorted(other_free.items(), key=lambda kv: (-kv[1], kv[0]))),
        "member_calls_nonsq": dict(sorted(members.items(), key=lambda kv: (-kv[1], kv[0]))),
        "sq_per_file": {p: dict(c) for p, c in per_file.items()},
        "other_per_file": {p: dict(c) for p, c in other_per_file.items()},
    }
    os.makedirs(dnfpaths.OUT_DIR, exist_ok=True)
    path = os.path.join(dnfpaths.OUT_DIR, "sq_inventory.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)

    print(json.dumps({k: v for k, v in summary.items() if not isinstance(v, list)}, ensure_ascii=False, indent=1))
    print("脚本里自己定义的 sq_ 名字:", sq_defined_here)
    print("出现过但从未被调用的原生 sq_ 名字:", len(sq_never_called))
    print("\n调用频次最高的 sq_ 名字(全局调用 / 成员调用):")
    cum = 0
    total = summary["sq_total_calls"]
    for i, (n, c) in enumerate(list(out["sq_calls"].items())[: args.top], 1):
        cum += c
        print("%3d. %-40s 调用 %5d (全局 %4d / 成员 %4d)  涉及文件 %3d  累计 %5.1f%%" % (
            i, n, c, out["sq_global_calls"][n], out["sq_member_calls"][n], out["sq_call_files"][n], 100.0 * cum / total))
    # 覆盖曲线:要实现多少个名字,才能覆盖 50% / 80% / 90% / 99% 的调用
    cum = 0
    marks = {50: None, 80: None, 90: None, 99: None}
    for i, (n, c) in enumerate(out["sq_calls"].items(), 1):
        cum += c
        for p in marks:
            if marks[p] is None and cum * 100 >= p * total:
                marks[p] = i
    print("\n覆盖调用次数的名字数:", {("%d%%" % p): v for p, v in marks.items()})
    print("写入", path)


if __name__ == "__main__":
    main()
