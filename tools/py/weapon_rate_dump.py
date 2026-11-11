#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把服务端 ELF 里的"武器强化攻击倍率表"(符号 upgradeWeaponAttackPowerRate_)按文本写到本机的 out/ 下,给 WeaponUpgradeRateTable.TryParse 读。

表是原版的平衡数据,不入库:本脚本只在本机运行,输出默认写到被 git 忽略的 out/enhance/weapon_rate.local.txt,终端只打印计数、不打印数值。
服务端二进制只做静态读取(读磁盘字节,不运行)。表的形状:11 行 × 12 个单精度浮点(528 字节);客户端只写了前 10 行(120 个),
服务端多出来的第 11 行全是 0(没有写值),WeaponUpgradeRateTable 在没有第 11 行时按倍率 1 处理,所以默认只写前 10 行。

用法:
  python3 tools/py/weapon_rate_dump.py [--elf <df_game_r.orig>] [--out <文件>] [--with-last-row] [--client <本机的客户端倍率文本>]
    --with-last-row  把第 11 行(全 0)也写出来(132 个数);默认只写 120 个
    --client         可选:本机另存的客户端倍率文本(每行 12 个数,没有值的行写 NA),逐格比对,只报"一致 N / 120"
"""
import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import read_elf_va as R  # noqa: E402

SYMBOL = "upgradeWeaponAttackPowerRate_"
COLUMNS = 12
ROWS_WRITTEN = 10


def find_symbol(elf, needle):
    """在 .symtab 里按名字片段找符号,返回 [(名字, 地址, 大小)]。"""
    d = elf.data
    shoff = struct.unpack_from("<I", d, 0x20)[0]
    shentsize, shnum, _shstrndx = struct.unpack_from("<HHH", d, 0x2E)
    sections = [struct.unpack_from("<IIIIIIIIII", d, shoff + i * shentsize) for i in range(shnum)]
    for _name, typ, _flags, _addr, off, size, link, _info, _align, entsize in sections:
        if typ != 2 or entsize == 0:          # SHT_SYMTAB
            continue
        str_off = sections[link][4]
        found = []
        needle_bytes = needle.encode("ascii")
        for i in range(size // entsize):
            st_name, st_value, st_size, _st_info, _st_other, _st_shndx = struct.unpack_from("<IIIBBH", d, off + i * entsize)
            start = str_off + st_name
            end = d.index(b"\0", start)
            if needle_bytes in d[start:end]:
                found.append((d[start:end].decode("latin-1"), st_value, st_size))
        return found
    return []


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--elf", default=R.DEFAULT_ELF)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.dirname(HERE)), "out", "enhance", "weapon_rate.local.txt"))
    ap.add_argument("--with-last-row", action="store_true")
    ap.add_argument("--client")
    args = ap.parse_args()

    elf = R.Elf32(args.elf)
    found = find_symbol(elf, SYMBOL)
    if len(found) != 1:
        print("符号 %s:找到 %d 个(需要恰好 1 个)" % (SYMBOL, len(found)))
        return 1
    _name, va, size = found[0]
    if size % (COLUMNS * 4) != 0 or size == 0:
        print("符号大小 %d 字节,不是 %d 个浮点的整数行" % (size, COLUMNS))
        return 1
    values = struct.unpack("<%df" % (size // 4), elf.read(va, size))
    rows = size // (COLUMNS * 4)
    table = [values[r * COLUMNS:(r + 1) * COLUMNS] for r in range(rows)]
    last_zero = rows > ROWS_WRITTEN and all(x == 0.0 for row in table[ROWS_WRITTEN:] for x in row)
    print("读到 %d 个浮点(%d 行 × %d 列);第 %d 行起%s" % (
        len(values), rows, COLUMNS, ROWS_WRITTEN + 1, "全是 0" if last_zero else "不全是 0"))
    if any(x != x or x in (float("inf"), float("-inf")) for x in values):
        print("有非有限数,不写")
        return 1

    if args.client:
        client = []
        with open(args.client, encoding="utf-8") as f:
            for line in f:
                parts = line.split()
                if parts and "NA" not in parts:
                    client.append([struct.unpack("<f", struct.pack("<f", float(p)))[0] for p in parts])
        same = sum(1 for r in range(min(len(client), ROWS_WRITTEN)) for c in range(COLUMNS)
                   if abs(client[r][c] - table[r][c]) <= 1e-6)
        print("与客户端文本逐格比对:一致 %d / %d" % (same, ROWS_WRITTEN * COLUMNS))

    keep = rows if args.with_last_row else ROWS_WRITTEN
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("# 武器强化攻击倍率表(本机生成,不入库):%d 行 × %d 列,每行是一个职业(客户端次序),列是 武器种类 0..5 各 (物理, 魔法)\n" % (keep, COLUMNS))
        for r in range(keep):
            f.write(" ".join(repr(float(x)) for x in table[r]) + "\n")
    print("写出 %d 个数到 %s(本机文件,别提交)" % (keep * COLUMNS, args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
