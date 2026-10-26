#!/usr/bin/env python3
"""副本难度加成的"原版机器码对拍":把服务端 ELF 里 upgradeAbilityByDifficultyOfDungeon 的三个重载放进 Unicorn 里真的跑一遍(见 elf_emu.py),
结果与 Python 参考实现、C# 的 MonsterDifficultyTable.Apply 逐槽比较(证据 E-125;非 REA:本机模拟器测量)。

三个重载(服务端 set_monster_ability 调的是第三个,它在副本没有自带加成时转调第二个,第二个在等级行不够时退到与第一个相同的全局算法):
  0x088B8C37  (难度, 数值块)                      只用全局系数
  0x088B85CF  (难度, 数值块, 怪物等级)             按等级的行;行数 < 等级时只用全局系数
  0x088B813A  (难度, 数值块, 怪物等级, 副本自带向量) 副本自带加成 × 全局;自带向量为空时转第二个

做了什么:
  1. 把合成的全局系数块(13 组 × 5 难度,float)写到服务端全局数组,把合成的等级行(5 个难度各一串 13 个 float 的行)按
     std::vector<std::vector<float>> 的布局写到另一个全局数组;副本自带加成同样按这个布局放在模拟堆里。
  2. 数值块用随机整数填(没动的槽保持随机哨兵值,用来确认其余槽没被碰),调用原版函数,读回 188 个 32 位槽。
  3. 与 Python 参考实现(x87.py 的精确模型)逐槽比较;第一个与第二个重载在"行不够"时必须与第三个给出同样的结果。
  4. --golden-file:写成金标准文件(全合成,可入库,C# 单元测试对着它回归);--write-cases:写出压力用例,交给
     `dnf difficulty-check --cases` 让 C# 重算对拍。

边界:只运行计算函数,不启动服务,不联网;输出里不含原版的数值,只有不一致的个数、槽号。
用法:
  re/venv/bin/python tools/py/mob_difficulty_oracle.py --stress 20000
  re/venv/bin/python tools/py/mob_difficulty_oracle.py --stress 20000 --write-cases out/diff_cases.txt
  re/venv/bin/python tools/py/mob_difficulty_oracle.py --golden-cases 420
"""
import argparse
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import elf_emu  # noqa: E402
import x87  # noqa: E402

OV_GLOBAL_ONLY = 0x088B8C37
OV_BY_LEVEL = 0x088B85CF
OV_WITH_DUNGEON = 0x088B813A
GLOBAL_BONUS = 0x093F4A00      # float[13][5]:DIFFICULTY_OF_DUNGEON_BONUS
LEVEL_BONUS = 0x09494F20       # std::vector<std::vector<float>>[5]:MONSTER_DIFFICULTY_BONUS

GROUPS = 13
DIFFS = 5
SLOTS = 0xBC
INFO_SIZE = 0x400
ROWS_CAP = 64

# 难度加成动过的槽(其余槽必须原样):生命值基数、17 项异常状态抗性(含第 13 项硬直抗性)、移动速度、攻击速度、受击恢复、装备四项
TOUCHED = [1] + list(range(0x1E, 0x2F)) + [0x46, 0x48, 0x4C, 0x56, 0x58, 0x5A, 0x5C]

IX_HP, IX_STATUS, IX_STUCK, IX_MOVE, IX_ATTACK_SPEED, IX_HIT_RECOVERY = 1, 0x1E, 0x2B, 0x46, 0x48, 0x4C
IX_EQUIP_PA, IX_EQUIP_PD, IX_EQUIP_MA, IX_EQUIP_MD = 0x56, 0x58, 0x5A, 0x5C
SKIPPED_STATUS = (12, 13)      # getToleranceActiveStatus 映射不到的两项


def wrap32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


def f32r(x):
    """把 Python 浮点数舍入成 float32(返回仍是 Python 浮点数,值恰好是一个 float32)。"""
    return struct.unpack("<f", struct.pack("<f", x))[0]


def f32_bits(x):
    return struct.unpack("<i", struct.pack("<f", x))[0]


def bits_f32(b):
    return struct.unpack("<f", struct.pack("<i", b))[0]


# ---------------------------------------------------------------- Python 参考实现(x87 精确模型)

def ref_mul_global(x, g):
    """FILD x;FLD g;FMULP;FISTP(截断)。"""
    return x87.trunc_i32(x87.mul(x87.from_int(x), x87.from_f32(g)))


def ref_mul_scaled(x, g, v):
    """FILD x;FLD g;FMULP;FSTP float(舍入到 24 位);FLD v;FMUL;FISTP(截断)。"""
    stored = x87.to_f32(x87.mul(x87.from_int(x), x87.from_f32(g)))
    return x87.trunc_i32(x87.mul(x87.from_f32(v), stored))


def ref_add_global(g):
    return x87.trunc_i32(x87.from_f32(g))


def ref_add_scaled(g, v):
    return x87.trunc_i32(x87.mul(x87.from_f32(v), x87.from_f32(g)))


def ref_apply(info, d, level, glob, rows, override):
    """info:188 个有符号整数;glob:65 个 float(按组、再按难度);rows[d]:行列表(每行 13 个 float);
    override:None 或 65 个 float。返回新的 188 个整数。"""
    r = list(info)

    def g(k):
        return glob[k * DIFFS + d]

    scale = None
    if override is not None:
        scale = [override[k * DIFFS + d] for k in range(GROUPS)]
    elif 1 <= level <= len(rows[d]):
        scale = rows[d][level - 1]

    def mult(x, k):
        return ref_mul_global(x, g(k)) if scale is None else ref_mul_scaled(x, g(k), scale[k])

    def add(k):
        return ref_add_global(g(k)) if scale is None else ref_add_scaled(g(k), scale[k])

    r[IX_HP] = mult(r[IX_HP], 4)
    r[IX_STUCK] = wrap32(r[IX_STUCK] + add(3))
    r[IX_HIT_RECOVERY] = wrap32(r[IX_HIT_RECOVERY] + add(10))
    for i in range(17):
        if i in SKIPPED_STATUS:
            continue
        r[IX_STATUS + i] = wrap32(r[IX_STATUS + i] + add(7))
    r[IX_ATTACK_SPEED] = mult(r[IX_ATTACK_SPEED], 5)
    if r[IX_MOVE] != 0:
        r[IX_MOVE] = wrap32(r[IX_MOVE] + add(6))
    r[IX_EQUIP_PA] = mult(r[IX_EQUIP_PA], 8)
    r[IX_EQUIP_MA] = mult(r[IX_EQUIP_MA], 8)
    r[IX_EQUIP_PD] = mult(r[IX_EQUIP_PD], 9)
    r[IX_EQUIP_MD] = mult(r[IX_EQUIP_MD], 9)
    return r


# ---------------------------------------------------------------- 模拟器

class Oracle:
    def __init__(self):
        self.emu = elf_emu.ElfEmu()
        emu = self.emu
        self.info = emu.alloc(INFO_SIZE)
        self.row_data = [emu.alloc(ROWS_CAP * GROUPS * 4) for _ in range(DIFFS)]
        self.row_vecs = [emu.alloc(ROWS_CAP * 12) for _ in range(DIFFS)]
        self.ov_data = emu.alloc(GROUPS * DIFFS * 4)
        self.ov_vecs = emu.alloc(GROUPS * 12)
        self.ov_header = emu.alloc(12)

    def load_tables(self, glob, rows):
        emu = self.emu
        emu.write(GLOBAL_BONUS, struct.pack("<%df" % (GROUPS * DIFFS), *glob))
        for d in range(DIFFS):
            n = len(rows[d])
            if n > ROWS_CAP:
                raise ValueError("等级行太多")
            if n == 0:
                emu.write(LEVEL_BONUS + 12 * d, b"\x00" * 12)
                continue
            emu.write(self.row_data[d], b"".join(struct.pack("<%df" % GROUPS, *row) for row in rows[d]))
            vecs = b"".join(struct.pack("<III", self.row_data[d] + i * GROUPS * 4, self.row_data[d] + (i + 1) * GROUPS * 4,
                                        self.row_data[d] + (i + 1) * GROUPS * 4) for i in range(n))
            emu.write(self.row_vecs[d], vecs)
            end = self.row_vecs[d] + 12 * n
            emu.write(LEVEL_BONUS + 12 * d, struct.pack("<III", self.row_vecs[d], end, end))

    def load_override(self, override):
        emu = self.emu
        if override is None:
            emu.write(self.ov_header, b"\x00" * 12)
            return
        emu.write(self.ov_data, struct.pack("<%df" % (GROUPS * DIFFS), *override))
        vecs = b"".join(struct.pack("<III", self.ov_data + k * DIFFS * 4, self.ov_data + (k + 1) * DIFFS * 4,
                                    self.ov_data + (k + 1) * DIFFS * 4) for k in range(GROUPS))
        emu.write(self.ov_vecs, vecs)
        end = self.ov_vecs + 12 * GROUPS
        emu.write(self.ov_header, struct.pack("<III", self.ov_vecs, end, end))

    def _fresh(self, info):
        self.emu.write(self.info, b"\x00" * INFO_SIZE)
        self.emu.write_i32s(self.info, info)

    def run3(self, d, level, info, override):
        self.load_override(override)
        self._fresh(info)
        self.emu.call(OV_WITH_DUNGEON, [d, self.info, level, self.ov_header])
        return self.emu.read_i32s(self.info, SLOTS)

    def run2(self, d, level, info):
        self._fresh(info)
        self.emu.call(OV_BY_LEVEL, [d, self.info, level])
        return self.emu.read_i32s(self.info, SLOTS)

    def run1(self, d, info):
        self._fresh(info)
        self.emu.call(OV_GLOBAL_ONLY, [d, self.info])
        return self.emu.read_i32s(self.info, SLOTS)


# ---------------------------------------------------------------- 合成用例

def rand_coef(rng):
    r = rng.random()
    if r < 0.25:
        return f32r(rng.choice([0.0, 1.0, 0.5, 1.1, 1.2, 1.4, 2.0, 2.5, 5.0, 0.01, 0.1, 0.3, 30.0, 200.0]))
    if r < 0.50:
        return f32r(rng.uniform(0.0, 6.0))
    if r < 0.65:
        return f32r(float(rng.randint(0, 400)))
    if r < 0.75:
        return f32r(rng.uniform(-3.0, 3.0))
    if r < 0.85:
        return f32r(rng.uniform(0.0, 1.0e3))
    if r < 0.95:
        return f32r(rng.uniform(0.0, 1.0e7))
    return f32r(rng.uniform(1.0e9, 1.0e10))        # 乘积会越过 32 位:专门打"整数不确定值"


def rand_slot(rng):
    r = rng.random()
    if r < 0.15:
        return 0
    if r < 0.55:
        return rng.randint(1, 3000)
    if r < 0.75:
        return rng.randint(1, 2_000_000)
    if r < 0.88:
        return rng.randint(-5000, 5000)
    if r < 0.95:
        return rng.randint(1_000_000_000, 2 ** 31 - 1)
    return rng.randint(-2 ** 31, -1_000_000_000)


def random_tables(rng, max_rows):
    glob = [rand_coef(rng) for _ in range(GROUPS * DIFFS)]
    rows = []
    for _d in range(DIFFS):
        n = rng.choice([0, 1, 2, rng.randint(1, max_rows), rng.randint(1, max_rows)])
        rows.append([[rand_coef(rng) for _ in range(GROUPS)] for _ in range(n)])
    return glob, rows


def random_info(rng):
    info = [rng.randint(-2 ** 31, 2 ** 31 - 1) for _ in range(SLOTS)]     # 没动的槽是随机哨兵
    for i in TOUCHED:
        info[i] = rand_slot(rng)
    return info


def random_case(rng, rows, override_prob):
    d = rng.randint(0, DIFFS - 1)
    most = max(len(r) for r in rows)
    level = rng.choice([rng.randint(1, most + 4), rng.randint(1, most + 4), rng.randint(1, 3), len(rows[d]) if rows[d] else 1,
                        (len(rows[d]) + 1)])
    level = max(1, level)
    override = [rand_coef(rng) for _ in range(GROUPS * DIFFS)] if rng.random() < override_prob else None
    return d, level, override, random_info(rng)


def check_case(oracle, glob, rows, d, level, override, info, hist, shown, tag):
    """跑一条:原版第三个重载 == Python 参考;没有自带加成时第二个重载也要一致;行不够时第一个重载也要一致。返回是否全部一致。"""
    ok = True
    want = ref_apply(info, d, level, glob, rows, override)
    outs = [("重载3", oracle.run3(d, level, info, override))]
    if override is None:
        outs.append(("重载2", oracle.run2(d, level, info)))
        if level > len(rows[d]):
            outs.append(("重载1", oracle.run1(d, info)))
    for name, got in outs:
        if got != want:
            ok = False
            diffs = [i for i in range(SLOTS) if got[i] != want[i]]
            for i in diffs:
                hist[i] = hist.get(i, 0) + 1
            if shown[0] < 12:
                shown[0] += 1
                print("  不一致", tag, name, "难度", d, "等级", level, "槽号", ["0x%X" % i for i in diffs[:6]])
    return ok


# ---------------------------------------------------------------- 输出文件

def ints_csv(values):
    return ",".join(str(x) for x in values)


def table_lines(tid, glob, rows):
    out = ["TABLE\t%d\t%s" % (tid, ints_csv(f32_bits(x) for x in glob))]
    for d in range(DIFFS):
        flat = [f32_bits(x) for row in rows[d] for x in row]
        out.append("ROWS\t%d\t%d\t%d\t%s" % (tid, d, len(rows[d]), ints_csv(flat) if flat else "-"))
    return out


def case_line(tid, d, level, override, info, want):
    ov = ints_csv(f32_bits(x) for x in override) if override is not None else "-"
    return "CASE\t%d\t%d\t%d\t%s\t%s\t%s" % (tid, d, level, ov, ints_csv(info[i] for i in TOUCHED), ints_csv(want[i] for i in TOUCHED))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stress", type=int, default=0, help="随机合成用例条数(每 25 条换一套合成表)")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--write-cases", help="把压力用例连同原版函数的输出写到这个文件(放 out/)")
    ap.add_argument("--golden-cases", type=int, default=0, help="写金标准文件:这么多条合成用例(全合成,可入库)")
    ap.add_argument("--golden-file", default=os.path.join(elf_emu.ROOT, "src", "Dnf.Game.Tests", "Golden", "mob_difficulty_golden.txt"))
    ap.add_argument("--golden-seed", type=int, default=20260102)
    args = ap.parse_args()

    oracle = Oracle()
    print("模拟器已就绪")
    total_bad = 0

    if args.stress:
        rng = random.Random(args.seed)
        ok = bad = 0
        hist = {}
        shown = [0]
        lines = []
        tid = 0
        glob = rows = None
        for n in range(args.stress):
            if n % 25 == 0:
                glob, rows = random_tables(rng, 12)
                oracle.load_tables(glob, rows)
                tid += 1
                if args.write_cases:
                    lines += table_lines(tid, glob, rows)
            d, level, override, info = random_case(rng, rows, 0.3)
            if check_case(oracle, glob, rows, d, level, override, info, hist, shown, "stress #%d" % n):
                ok += 1
            else:
                bad += 1
            if args.write_cases:
                lines.append(case_line(tid, d, level, override, info, oracle.run3(d, level, info, override)))
        print("随机压力对拍:一致 %d,不一致 %d" % (ok, bad))
        if hist:
            print("不一致的槽号分布:", {"0x%X" % k: v for k, v in sorted(hist.items())})
        if args.write_cases:
            with open(args.write_cases, "w", encoding="utf-8", newline="\n") as fh:
                fh.write("\n".join(lines) + "\n")
            print("已写出", args.write_cases, "%d 行" % len(lines))
        total_bad += bad

    if args.golden_cases:
        rng = random.Random(args.golden_seed)
        lines = ["# 副本难度加成金标准:合成系数表与合成用例,期望值来自服务端原版函数 upgradeAbilityByDifficultyOfDungeon(第三个重载)在模拟器里的运行结果。",
                 "# 由 tools/py/mob_difficulty_oracle.py --golden-cases 生成;不含原版数据。TABLE 行:全局系数块的 float 位模式(65 个);",
                 "# ROWS 行:表号、难度、行数、逐行展开的 float 位模式(每行 13 个);CASE 行:表号、难度、怪物等级、副本自带加成(65 个位模式或 -)、",
                 "# 动过的 25 个槽的输入、25 个槽的期望输出(槽号见 mob_difficulty_oracle.py 的 TOUCHED)。"]
        per_table = max(1, args.golden_cases // 7)
        bad = 0
        count = 0
        hist = {}
        shown = [0]
        for tid in range(1, 8):
            glob, rows = random_tables(rng, 10)
            oracle.load_tables(glob, rows)
            lines += table_lines(tid, glob, rows)
            for _ in range(per_table):
                d, level, override, info = random_case(rng, rows, 0.3)
                if not check_case(oracle, glob, rows, d, level, override, info, hist, shown, "golden"):
                    bad += 1
                lines.append(case_line(tid, d, level, override, info, oracle.run3(d, level, info, override)))
                count += 1
        with open(args.golden_file, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(lines) + "\n")
        print("已写出金标准", args.golden_file, count, "条用例,%d KB;参考与原版不一致 %d" % (os.path.getsize(args.golden_file) // 1024, bad))
        total_bad += bad
    return 1 if total_bad else 0


if __name__ == "__main__":
    sys.exit(main())
