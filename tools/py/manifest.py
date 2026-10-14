"""生成 Script.pvf 的"文件清单":每个文件的 路径、解密后长度、明文 SHA-256。

清单用途:C# 数据层库(P1)与这份 Python 参考实现逐文件对拍。清单本身只含路径与哈希,
但仍然是从原版数据派生的,所以写进 out/(已被 .gitignore 排除),不提交。

聚合哈希 = 对清单里每个文件的十六进制 SHA-256 字符串,按 PVF 目录顺序依次喂给一个 SHA-256。
用法:
    python3 manifest.py            生成 out/pvf_manifest.tsv.gz 并打印聚合哈希
    python3 manifest.py --expect <hex>   额外与期望的聚合哈希比较,不一致则以非零状态退出
"""
import argparse
import gzip
import hashlib
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from dnfpaths import ensure_out_dir


def build(pvf):
    agg = hashlib.sha256()
    rows = []
    for e in pvf.entries:
        h = hashlib.sha256(pvf.read(e)).hexdigest()
        agg.update(h.encode("ascii"))
        rows.append("%s\t%d\t%s" % (e.path, e.length, h))
    return rows, agg.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--expect", help="期望的聚合哈希(十六进制)")
    args = ap.parse_args()

    t0 = time.time()
    pvf = D.Pvf()
    rows, agg = build(pvf)
    out = os.path.join(ensure_out_dir(), "pvf_manifest.tsv.gz")
    with gzip.open(out, "wt", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(rows))
    print("文件数 %d,聚合 SHA-256 %s,用时 %.1fs" % (len(rows), agg, time.time() - t0))
    print("清单已写入", out)
    if args.expect and args.expect.lower() != agg:
        print("聚合哈希与期望值不一致:期望 %s" % args.expect.lower(), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
