#!/usr/bin/env python3
"""受保护区"跳板"普查(E-060):明文里有大量 `CALL 受保护区` 的调用点,这些跳板是"被偷走的指令":
被调用的块里塞满垃圾指令,最后回到调用点的下一条指令。这个脚本在 CPU 模拟器里逐个跑,统计
  - 能不能正常"返回"到调用点的下一条;
  - 执行了多少条指令;
  - 对寄存器与非栈内存的净影响(用随机初值与哈希填充的内存探测)。
只读磁盘上的 DNF.exe;模拟器不运行游戏本体;输出放 out/(不入库)。

用法:re/venv/bin/python tools/py/emu_stub_census.py [抽样个数,缺省 300] [--seed N] [--out out/prot_work/census.tsv]
"""
import argparse
import os
import random
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from emu import ClientEmu, PROTECTED_LO, PROTECTED_HI, PLAIN_LO, PLAIN_HI  # noqa: E402

ARENA = 0x31000000
ARENA_SIZE = 0x00100000
REGS = ("eax", "ebx", "ecx", "edx", "esi", "edi", "ebp")


def find_call_sites(emu, lo, hi, target_lo, target_hi):
    """在 [lo, hi) 里找 E8 rel32 调用,目标落在 [target_lo, target_hi)。"""
    data = emu.read(lo, hi - lo)
    sites = []
    for m in re.finditer(b"\xe8", data):
        i = m.start()
        if i + 5 > len(data):
            break
        rel = struct.unpack_from("<i", data, i + 1)[0]
        site = lo + i
        target = (site + 5 + rel) & 0xFFFFFFFF
        if target_lo <= target < target_hi:
            sites.append((site, target))
    return sites


def fill_arena(emu, seed):
    rnd = random.Random(seed)
    emu.write(ARENA, bytes(rnd.getrandbits(8) for _ in range(ARENA_SIZE // 16)) * 16)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("count", nargs="?", type=int, default=300)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(HERE), "..", "out", "prot_work", "census.tsv"))
    ap.add_argument("--max-insns", type=int, default=20000)
    a = ap.parse_args()

    emu = ClientEmu()
    emu._map(ARENA, ARENA_SIZE)
    sites = find_call_sites(emu, PLAIN_LO, PLAIN_HI, PROTECTED_LO, PROTECTED_HI)
    print("明文里指向受保护区的 CALL:%d 处;不同目标 %d 个" % (len(sites), len({t for _, t in sites})))
    rnd = random.Random(a.seed)
    sample = rnd.sample(sites, min(a.count, len(sites)))
    out_path = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    stats = {"返回到下一条": 0, "没返回": 0}
    with open(out_path, "w", encoding="utf-8") as out:
        out.write("site\ttarget\tstop\tinsns\tesp_delta\tchanged_regs\tnonstack_writes\texit_eip\n")
        for site, target in sample:
            fill_arena(emu, rnd.getrandbits(32))
            base = {r: ARENA + 0x1000 * (i + 1) + 0x400 for i, r in enumerate(REGS)}
            base["ebp"] = 0x20100000
            res, log = emu.call(target, args=[], regs=base, until=site + 5, return_address=site + 5,
                                watch=[(ARENA, ARENA + ARENA_SIZE)], max_insns=a.max_insns)
            esp0 = 0x20000000 + 0x200000 - 0x1000 - 4
            ok = (res.eip == site + 5 and res.stop == "end")
            stats["返回到下一条" if ok else "没返回"] += 1
            changed = []
            for r in REGS:
                if emu.reg(r) != base[r]:
                    changed.append("%s=%08x" % (r, emu.reg(r)))
            writes = len([w for w in res.writes if ARENA <= w[0] < ARENA + ARENA_SIZE])
            out.write("%08x\t%08x\t%s\t%d\t%d\t%s\t%d\t%08x\n" % (site, target, res.stop, res.insns, res.esp - (esp0 + 4),
                                                                   ",".join(changed), writes, res.eip))
    print("抽样 %d:%s" % (len(sample), stats))
    print("明细:", out_path)


if __name__ == "__main__":
    main()
