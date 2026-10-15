#!/usr/bin/env python3
"""把 `dnf game-check --json` 里每个已登记原生接口的状态与依据整理成 docs/native-audit.tsv。

列:name(接口名,类内成员写作 类.名)、status(Verified = 读过客户端实现并核对 / Inferred = 按名字与用法推断 / Trace = 只占位)、
forms(Member 成员写法 / Global 全局写法 / Both)、script_calls(原版脚本里的调用点个数,来自 docs/sq-api-catalog.tsv)、
note(用自己的话写的语义与依据;读过客户端实现的会带上实现地址)。

只含名字、状态、数字和我们自己写的说明,不含任何反编译出的代码。用法:
  dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll game-check --json out/game_check_quick.json
  python3 tools/py/native_audit_table.py [--json out/game_check_quick.json] [--out docs/native-audit.tsv]
"""
import argparse
import json
import os

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ORDER = {"Verified": 0, "Inferred": 1, "Trace": 2}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default=os.path.join(REPO, "out", "game_check_quick.json"))
    ap.add_argument("--out", default=os.path.join(REPO, "docs", "native-audit.tsv"))
    a = ap.parse_args()
    natives = json.load(open(a.json, encoding="utf-8"))["coverage"]["natives"]
    calls = {}
    with open(os.path.join(REPO, "docs", "sq-api-catalog.tsv"), encoding="utf-8", newline="\n") as f:
        next(f)
        for line in f:
            cols = line.rstrip("\n").split("\t")
            if len(cols) >= 3 and cols[2].isdigit():
                calls[cols[0]] = calls.get(cols[0], 0) + int(cols[2])
    rows = []
    for n in natives:
        short = n["name"].split(".")[-1]
        note = " ".join(str(n["note"]).split())
        rows.append((ORDER.get(n["status"], 3), -calls.get(short, 0), n["name"], n["status"], n["forms"], calls.get(short, 0), note))
    rows.sort()
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("name\tstatus\tforms\tscript_calls\tnote\n")
        for _, _, name, status, forms, c, note in rows:
            f.write("%s\t%s\t%s\t%d\t%s\n" % (name, status, forms, c, note))
    counts = {}
    for r in rows:
        counts[r[3]] = counts.get(r[3], 0) + 1
    print("写出 %d 行 -> %s  %s" % (len(rows), a.out, counts))


if __name__ == "__main__":
    main()
