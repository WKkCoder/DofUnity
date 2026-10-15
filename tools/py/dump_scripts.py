#!/usr/bin/env python3
"""把 PVF 里指定扩展名的全部标记流脚本按"一个标签一行"批量打成文本,方便用 rg 跨文件检索。

输出放在 re/dumps/<PVF 内路径>.txt(re/ 被 .gitignore 与提交守卫排除),内容含原版脚本,只留在本机,绝不提交。
这是研究用的辅助:检索"某个标签在哪些文件里怎么用",不是引擎的一部分。

用法:
  python3 tools/py/dump_scripts.py --ext act                 # 只导出 .act
  python3 tools/py/dump_scripts.py --ext act --ext ai --out re/dumps
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from script_dump import Strings, to_text

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ext", action="append", required=True, help="扩展名(不带点),可重复")
    ap.add_argument("--out", default=os.path.join(ROOT, "re", "dumps"))
    a = ap.parse_args()
    exts = tuple("." + e.lower().lstrip(".") for e in a.ext)

    out_root = os.path.abspath(a.out)
    # 守卫:输出必须在 re/ 或 out/ 之下,避免不小心把原版脚本文本写进会被提交的目录
    allowed = [os.path.join(ROOT, "re"), os.path.join(ROOT, "out")]
    if not any(out_root == p or out_root.startswith(p + os.sep) for p in allowed):
        sys.exit("拒绝:输出目录必须在 re/ 或 out/ 下(这些目录不入库)")

    p = D.Pvf()
    strings = Strings(p)
    done = skipped = 0
    for e in p.entries:
        lp = e.path.lower()
        if not lp.endswith(exts):
            continue
        raw = p.get(e.path)
        text = to_text(raw, strings) if raw is not None else None
        if text is None:
            skipped += 1
            continue
        dest = os.path.join(out_root, lp.replace("\\", "/") + ".txt")
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "w", encoding="utf-8") as f:
            f.write(text)
        done += 1
    print("已写入 %d 个文件(跳过 %d 个非标记流脚本)到 %s" % (done, skipped, out_root))


if __name__ == "__main__":
    main()
