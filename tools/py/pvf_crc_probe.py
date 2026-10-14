#!/usr/bin/env python3
"""验证客户端 PVF 加载例程里的完整性校验语义(非 REA:Python 度量,对照 Ghidra 静态分析的结论)。

Ghidra 静态分析(见 docs/evidence/ledger.md E-010)显示,客户端在 VA 0x011AB180 的例程对每个文件:
    1. 逐 32 位字解密:w = ROR32(w ^ 0x81A79011 ^ key, 6)(key 来自目录项 crc)
    2. 对解密后的字节(按 4 字节对齐的整段)累积标准 CRC32(多项式 0xEDB88320),
       种子取自文件条目的第一个字段,结果与目录项 crc 比较。
本脚本在真实数据上检验:
    H1  zlib.crc32(解密后的对齐整段, 种子 = 目录项 fileNo) == 目录项 crc
    H2  同上,但只取 [:length](不含对齐填充)
    H0  种子 0(此前的假设,预期失败)
另外检查目录树:解密后整段(含末尾填充)的隐含种子是什么。

只读;不写任何素材。用法:
    python3 tools/py/pvf_crc_probe.py [--limit N] [--out out/pvf_crc_probe.json]
"""
import argparse
import json
import os
import struct
import sys
import time
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata  # noqa: E402

# 标准反射 CRC32 表;高字节是 0..255 的一个排列,所以可以反向求"隐含种子"。
_T = []
for _n in range(256):
    _c = _n
    for _ in range(8):
        _c = (_c >> 1) ^ 0xEDB88320 if _c & 1 else _c >> 1
    _T.append(_c)
_TOP = {_T[i] >> 24: i for i in range(256)}


def implied_seed(data, final_crc):
    """求 S,使 zlib.crc32(data, S) == final_crc(把 CRC 状态倒着推回起点)。"""
    s = (~final_crc) & 0xFFFFFFFF
    for b in reversed(data):
        i = _TOP[s >> 24]
        s = (((s ^ _T[i]) << 8) & 0xFFFFFFFF) | (i ^ b)
    return (~s) & 0xFFFFFFFF


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="只检查前 N 个文件(0 = 全部)")
    ap.add_argument("--out", default=None, help="结果 JSON 路径")
    args = ap.parse_args()

    # 自检:隐含种子求解器
    for k in range(50):
        d = bytes((k * 31 + i * 7) & 0xFF for i in range(k + 1))
        s = (k * 2654435761) & 0xFFFFFFFF
        assert implied_seed(d, zlib.crc32(d, s) & 0xFFFFFFFF) == s

    t0 = time.time()
    pvf = dnfdata.Pvf()
    f = pvf._f

    # 目录树:读原文,解密,同时取出每个条目的 fileNo
    f.seek(pvf.header_len)
    tree_plain = dnfdata.ror6_decrypt(f.read(pvf.tree_len), pvf.tree_crc)
    file_nos = []
    pos = 0
    for _ in range(pvf.n_files):
        fno, plen = struct.unpack_from("<II", tree_plain, pos)
        file_nos.append(fno)
        pos += 8 + plen + 12
    tree_tail = tree_plain[pos:]
    tree_seed = implied_seed(tree_plain, pvf.tree_crc)

    entries = pvf.entries[: args.limit] if args.limit else pvf.entries
    n = len(entries)
    h0 = h1 = h2 = 0
    h1_fail_examples = []
    pad_files = pad_nonzero = 0
    dup_file_no = n - len(set(file_nos[:n]))
    for idx, e in enumerate(entries):
        f.seek(pvf.data_base + e.offset)
        padded_len = (e.length + 3) & ~3
        plain = dnfdata.ror6_decrypt(f.read(padded_len), e.crc)
        if zlib.crc32(plain) & 0xFFFFFFFF == e.crc:
            h0 += 1
        if zlib.crc32(plain, file_nos[idx]) & 0xFFFFFFFF == e.crc:
            h1 += 1
        elif len(h1_fail_examples) < 5:
            h1_fail_examples.append({"index": idx, "path": e.path, "length": e.length, "crc": e.crc, "file_no": file_nos[idx]})
        if zlib.crc32(plain[: e.length], file_nos[idx]) & 0xFFFFFFFF == e.crc:
            h2 += 1
        if padded_len != e.length:
            pad_files += 1
            if any(plain[e.length:]):
                pad_nonzero += 1

    res = {
        "files_checked": n,
        "h0_seed0_padded": h0,
        "h1_seed_fileNo_padded": h1,
        "h2_seed_fileNo_exact_length": h2,
        "files_with_padding": pad_files,
        "files_with_nonzero_padding_after_decrypt": pad_nonzero,
        "duplicate_fileNo_in_checked_range": dup_file_no,
        "dirtree": {
            "length": pvf.tree_len,
            "bytes_after_last_entry": len(tree_tail),
            "tail_bytes_hex": tree_tail.hex(),
            "crc_field": pvf.tree_crc,
            "implied_seed_padded": tree_seed,
            "n_files_field": pvf.n_files,
            "version_field": pvf.version,
            "crc32_seed0_matches": (zlib.crc32(tree_plain) & 0xFFFFFFFF) == pvf.tree_crc,
            "crc32_seed_nFiles_matches": (zlib.crc32(tree_plain, pvf.n_files) & 0xFFFFFFFF) == pvf.tree_crc,
            "crc32_seed_version_matches": (zlib.crc32(tree_plain, pvf.version) & 0xFFFFFFFF) == pvf.tree_crc,
        },
        "h1_fail_examples": h1_fail_examples,
        "seconds": round(time.time() - t0, 1),
    }
    print(json.dumps(res, ensure_ascii=False, indent=2))
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as fo:
            json.dump(res, fo, ensure_ascii=False, indent=2)
    pvf.close()
    return 0 if h1 == n else 1


if __name__ == "__main__":
    sys.exit(main())
