"""列出还没实现的原生接口,并带上它们在脚本里的写法(成员/全局/自由函数)、实参个数分布、常见接收者。

输入:docs/sq-api-catalog.tsv(静态清单)和 out/game_check.json(`dnf game-check --json` 的输出,里面有已登记的名字)。
只读测量,非 REA;只输出名字与计数,不含脚本内容。

用法:python3 tools/py/sq_missing.py [数量,默认 60] [--filter 子串]
"""
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    limit = int(args[0]) if args else 60
    flt = None
    if "--filter" in sys.argv:
        flt = sys.argv[sys.argv.index("--filter") + 1]
        args = [a for a in args if a != flt]
        limit = int(args[0]) if args else 60
    registered = set(json.load(open(os.path.join(ROOT, "out", "game_check.json")))["coverage"]["registered"])
    rows = []
    with open(os.path.join(ROOT, "docs", "sq-api-catalog.tsv"), encoding="utf-8") as f:
        next(f)
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) < 6 or p[0] in registered:
                continue
            if flt and flt not in p[0]:
                continue
            rows.append((int(p[2]), p[0], p[1], p[3], p[4], p[5]))
    rows.sort(reverse=True)
    total = sum(r[0] for r in rows)
    print("未实现 %d 个名字,%d 个调用点" % (len(rows), total))
    for calls, name, form, files, argc, recv in rows[:limit]:
        print("%5d %-42s %-6s 文件%-3s 实参[%s] 接收者[%s]" % (calls, name, form, files, argc, recv[:60]))


if __name__ == "__main__":
    main()
