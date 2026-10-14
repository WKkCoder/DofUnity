"""统计脚本里 setTimeEvent 第三个参数(次数)的写法,用来核对"次数为 0 表示不限"这个推断(E-038)。

输入:re/notes/nut/sqr(由 tools/py/script_dump.py 从用户自备的 Script.pvf 解出,不入库)。
输出:只有计数与文件名,不含脚本内容。非 REA,只读测量。

setTimeEvent 的实参约定(推断,来自 E-037):(编号, 间隔毫秒, 次数, 是否立即触发)。
次数写成字面量 0、-1、正整数还是别的表达式,决定了"0 = 不限"这条推断有多少调用点在依赖它。

用法:python3 tools/py/sq_timeevent_probe.py [--json out/sq_timeevent_probe.json]
"""
import json
import os
import re
import sys
from collections import Counter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
NUT_ROOT = os.path.join(ROOT, "re", "notes", "nut", "sqr")

CALL = re.compile(r"\.\s*setTimeEvent\s*\(")


def split_args(text, start):
    """从 start(左括号之后)起按最外层逗号切分实参,返回 (实参列表, 右括号位置)。"""
    depth = 0
    args = []
    cur = []
    i = start
    in_str = False
    while i < len(text):
        ch = text[i]
        if in_str:
            cur.append(ch)
            if ch == "\\":
                i += 1
                if i < len(text):
                    cur.append(text[i])
            elif ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
            cur.append(ch)
        elif ch in "([{":
            depth += 1
            cur.append(ch)
        elif ch in ")]}":
            if depth == 0:
                args.append("".join(cur).strip())
                return args, i
            depth -= 1
            cur.append(ch)
        elif ch == "," and depth == 0:
            args.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
        i += 1
    return args, i


def main():
    if not os.path.isdir(NUT_ROOT):
        sys.exit("没有 %s;先运行 tools/py/script_dump.py" % NUT_ROOT)
    kinds = Counter()
    files_by_kind = {}
    total_files = 0
    for dirpath, _, names in os.walk(NUT_ROOT):
        for name in names:
            if not name.lower().endswith(".nut"):
                continue
            path = os.path.join(dirpath, name)
            with open(path, "rb") as f:
                text = f.read().decode("latin-1")
            hits = 0
            for m in CALL.finditer(text):
                args, _ = split_args(text, m.end())
                hits += 1
                if len(args) < 3:
                    kind = "实参不足 3 个"
                else:
                    third = args[2]
                    if re.fullmatch(r"-?\d+", third):
                        v = int(third)
                        kind = "字面量 0" if v == 0 else ("字面量 -1" if v == -1 else ("字面量正整数" if v > 0 else "字面量其他负数"))
                    else:
                        kind = "表达式或变量"
                kinds[kind] += 1
                files_by_kind.setdefault(kind, set()).add(os.path.relpath(path, NUT_ROOT).replace("\\", "/"))
            if hits:
                total_files += 1
    total = sum(kinds.values())
    print("setTimeEvent 调用点 %d 个,分布在 %d 个脚本里" % (total, total_files))
    for kind, n in kinds.most_common():
        print("  %-14s %5d 处,%d 个脚本" % (kind, n, len(files_by_kind[kind])))
    for kind in ("字面量 0", "字面量 -1"):
        if kind in files_by_kind:
            print("%s 出现在:%s" % (kind, ", ".join(sorted(files_by_kind[kind]))))
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        os.makedirs(os.path.dirname(os.path.join(ROOT, out)), exist_ok=True)
        with open(os.path.join(ROOT, out), "w", encoding="utf-8") as f:
            json.dump({"total": total, "files": total_files, "kinds": dict(kinds),
                       "files_by_kind": {k: sorted(v) for k, v in files_by_kind.items()}},
                      f, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
