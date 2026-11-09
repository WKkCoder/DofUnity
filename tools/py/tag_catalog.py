"""为 PVF 里"标记流"脚本(B0 D0 开头)生成 tag 目录与覆盖率。

已验证的语法(见 docs/formats.md):
  文件 = B0 D0 + N 个 5 字节标记, 每个标记 = u8 类型 + i32 值
  类型 2 = 整数      类型 3 = 整数(少量, Unicode 范围表里出现)   类型 4 = 浮点(按 f32 解释 i32 位模式)
  类型 5 = 标签     值 = stringtable 下标, 字符串形如 [name] 或 [/name]
  类型 6 = 括号词   如 (UP) (SKILL)                类型 7 = 字符串/关键字   如 `路径` 或 [fixed]
  类型 8 = 分隔符   ","                              类型 9 + 类型 10 总是成对出现(字符串引用)
stringtable.bin = u32 count + (count+1) 个 u32 偏移(相对 4 字节处) + 字符串数据, 字符串无终止符。

输出 out/tag_catalog.json。只统计标签名与值类型形状,不保存任何脚本内容。
"""
import json
import os
import struct
import sys
import time
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from dnfpaths import OUT_DIR as OUT

LETTER = {2: "i", 3: "u", 4: "f", 6: "c", 7: "s", 8: "d"}


def load_strings(pvf):
    st = pvf.get("stringtable.bin")
    n = struct.unpack_from("<I", st, 0)[0]
    offs = struct.unpack_from("<%dI" % (n + 1), st, 4)
    return st, n, offs


def rle(sig, limit=12):
    out = []
    i = 0
    while i < len(sig):
        j = i
        while j < len(sig) and sig[j] == sig[i]:
            j += 1
        out.append("%s%s" % (sig[i], j - i if j - i > 1 else ""))
        i = j
        if len(out) >= limit:
            out.append("...")
            break
    return " ".join(out)


def shape(vals):
    """把一个标签后面的值序列压成形状字符串; 9+10 合并成 L。"""
    letters = []
    k = 0
    while k < len(vals):
        t = vals[k]
        if t == 9 and k + 1 < len(vals) and vals[k + 1] == 10:
            letters.append("L")
            k += 2
            continue
        letters.append(LETTER.get(t, "?%d" % t))
        k += 1
    return rle(letters)


def main():
    t0 = time.time()
    p = D.Pvf()
    st, n, offs = load_strings(p)
    cache = {}

    def S(i):
        s = cache.get(i)
        if s is None:
            s = st[4 + offs[i]: 4 + offs[i + 1]].decode("latin1")
            cache[i] = s
        return s

    per_ext = defaultdict(lambda: {"files": 0, "tokens": 0, "tag_open": Counter(), "tag_close": Counter(),
                                   "tag_files": Counter(), "shapes": defaultdict(Counter)})
    skipped = Counter()
    type_total = Counter()
    for e in p.entries:
        base = e.path.rsplit("/", 1)[-1]
        ext = base.rsplit(".", 1)[-1] if "." in base else "(none)"
        if ext in ("ani", "nut") or e.length < 2:
            skipped[ext] += 1
            continue
        d = p.read(e)
        if d[:2] != b"\xb0\xd0":
            skipped[ext + ":non-token"] += 1
            continue
        pe = per_ext[ext]
        pe["files"] += 1
        toks = list(struct.iter_unpack("<Bi", d[2:]))
        pe["tokens"] += len(toks)
        cur = None
        vals = []
        seen = set()

        def flush():
            if cur is not None:
                pe["shapes"][cur][shape(vals)] += 1

        for ty, v in toks:
            type_total[ty] += 1
            if ty == 5:
                flush()
                name = S(v)
                if name.startswith("[/"):
                    pe["tag_close"][name[2:-1]] += 1
                    cur = None
                else:
                    pe["tag_open"][name] += 1
                    seen.add(name)
                    cur = name
                vals = []
            else:
                vals.append(ty)
        flush()
        for t in seen:
            pe["tag_files"][t] += 1

    # 汇总
    global_open = Counter()
    for ext, pe in per_ext.items():
        global_open.update(pe["tag_open"])
    total_open = sum(global_open.values())
    ranked = global_open.most_common()
    cover = {}
    acc = 0
    marks = [10, 25, 50, 100, 200, 300, 500, 1000, 2000]
    for i, (name, c) in enumerate(ranked, 1):
        acc += c
        if i in marks:
            cover[i] = round(100.0 * acc / total_open, 2)
    cover[len(ranked)] = 100.0

    result = {
        "seconds": round(time.time() - t0, 1),
        "token_types": dict(sorted(type_total.items())),
        "skipped": dict(skipped),
        "distinct_open_tags": len(ranked),
        "open_tag_occurrences": total_open,
        "coverage_pct_by_top_n": cover,
        "extensions": {},
        "top_tags": [(name, c) for name, c in ranked[:80]],
    }
    for ext, pe in sorted(per_ext.items(), key=lambda kv: -kv[1]["files"]):
        tags = []
        for name, c in pe["tag_open"].most_common():
            shapes = pe["shapes"][name].most_common(4)
            tags.append({"tag": name, "count": c, "files": pe["tag_files"][name],
                         "closer": pe["tag_close"].get(name[1:-1], 0),
                         "shapes": [(s, n_) for s, n_ in shapes],
                         "distinct_shapes": len(pe["shapes"][name])})
        total = sum(pe["tag_open"].values())
        acc = 0
        ext_cover = {}
        for i, t in enumerate(tags, 1):
            acc += t["count"]
            if i in (5, 10, 25, 50, 100, 200):
                ext_cover[i] = round(100.0 * acc / total, 2)
        result["extensions"][ext] = {"files": pe["files"], "tokens": pe["tokens"], "distinct_tags": len(tags),
                                     "open_tag_occurrences": total, "coverage_pct_by_top_n": ext_cover,
                                     "tags": tags}
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "tag_catalog.json"), "w") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)

    print("用时 %.1fs" % result["seconds"])
    print("标记类型计数:", result["token_types"])
    print("跳过:", dict(skipped))
    print("全库不同的开标签:", len(ranked), " 开标签出现次数:", total_open)
    print("覆盖率(前 N 个标签占全部出现次数的百分比):", cover)
    print("%-6s %8s %10s %7s  %s" % ("ext", "files", "tokens", "tags", "cover(top5/10/25/50/100/200)"))
    for ext, info in list(result["extensions"].items())[:32]:
        cv = info["coverage_pct_by_top_n"]
        print("%-6s %8d %10d %7d  %s" % (ext, info["files"], info["tokens"], info["distinct_tags"],
                                         "/".join(str(cv.get(k, "-")) for k in (5, 10, 25, 50, 100, 200))))


if __name__ == "__main__":
    main()
