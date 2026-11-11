#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""强化等级与品级值换算成装备属性的参考实现:和 C# 的 EquipUpgradeEffect / EquipGradeVariation / WeaponUpgradeRateTable 做双实现对拍。

依据是客户端静态读码(函数地址与结论见 out/enhance/NOTES.md);这里只写算法,不带任何原版数值,也不含客户端代码原文。
两边各写各的:C# 用 float 运算,这里用有理数精确模拟"单精度每一步舍入"(tools/py/x87.py 的 to_f32 = 舍入到 24 位有效位、平局取偶),
所以浮点边界上的差异会在对拍里暴露,而不是两边抄同一个写法。

1. 强化属性(effect.txt):对每件武器与防具首饰,取强化表(普通表)第"强化等级−1"行放大后的两组 8 格(A 组、B 组),按部位分路:
     武器(10):A0 × 倍率,向零截断;物理一路要装备带非 0 的装备物理攻击,魔法一路要带非 0 的装备魔法攻击;
     防具(12 到 16):整数 = 格 1 向零截断、浮点 = 格 5;首饰(17 到 19):整数 = 格 2、浮点 = 格 6;辅助与魔法石(20、21):整数 = 格 3。
   武器倍率 = 倍率表[((职业 × 6) + 武器种类) × 2 + (0 物理 | 1 魔法)],职业 = 可用职业里按客户端次序最靠前的那个(非职业名当 0;没写 −1 → 倍率 0),
   武器种类 = [sub type](不在 0 到 5 倍率 0);第 11 个职业没有行时倍率 1。对拍用合成倍率表 cells[i] = 0.5 + i/128(i = 0 到 131),不是原版数值。
2. 品级浮动(variation.txt):见 jitter()。随机数、档位、各档公式与 C# 同一个设计说明(EquipGradeVariation 的文件头),
   [stat random variation] 的百分比与上限取本机 etc/etcparameter.etc 的真实值(只写在被忽略的 out/ 下)。
"""
import struct
from fractions import Fraction

import x87 as X

HERE_FILES = ("effect.txt", "variation.txt")

# 单精度常数:字节取自客户端(0.2f、-0.4f、0.5f),这里直接写字节
C_0_2 = Fraction(struct.unpack("<f", bytes.fromhex("cdcc4c3e"))[0])
C_N0_4 = Fraction(struct.unpack("<f", bytes.fromhex("cdccccbe"))[0])
C_0_5 = Fraction(1, 2)
C_100 = Fraction(100)

CLIENT_JOBS = ("[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[at gunner]", "[at fighter]", "[at mage]",
               "[demonic swordman]", "[creator mage]", "[thief]")
JOBS_WITH_ROWS = 10
SUBTYPES = 6

# 十个被浮动的属性(标签),次序 = 客户端逐项浮动的次序
VARIED_TAGS = ("[hp max]", "[mp max]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]",
               "[equipment physical attack]", "[equipment physical defense]", "[equipment magical attack]",
               "[equipment magical defense]")
SEEDS = (1, 2, 3, 7, 99, 12345, 999999998, 2147483647, -5, -2147483648)
LEVELS = (1, 2, 5, 10, 18, 24)

PART_TAGS = (
    "[hat avatar]", "[hair avatar]", "[face avatar]", "[coat avatar]", "[pants avatar]", "[shoes avatar]", "[breast avatar]",
    "[waist avatar]", "[skin avatar]", "[aurora avatar]", "[weapon]", "[title name]", "[coat]", "[shoulder]", "[pants]",
    "[shoes]", "[waist]", "[amulet]", "[wrist]", "[ring]", "[support]", "[magic stone]", "[creature]", "[artifact red]",
    "[artifact blue]", "[artifact green]",
)
UNKNOWN_PART = 26


# ---------------------------------------------------------------- 单精度的每一步舍入

def f32(x):
    return X.to_f32(Fraction(x))


def mul32(a, b):
    return X.to_f32(Fraction(a) * Fraction(b))


def div32(a, b):
    return X.to_f32(Fraction(a) / Fraction(b))


def add32(a, b):
    return X.to_f32(Fraction(a) + Fraction(b))


def trunc(x):
    return int(Fraction(x))      # Fraction 的 int() 向零截断


def bits(x):
    return "%08x" % struct.unpack("<I", struct.pack("<f", float(x)))[0]


def s32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v >= (1 << 31) else v


# ---------------------------------------------------------------- 随机数、档位、浮动

def next_random(state):
    """返回 (随机数, 新状态):三步 s = s×0x41c64e6d + 0x3039,再按 10 位一段拼第三、第二、第一步的高位。"""
    m = 0xFFFFFFFF
    s1 = (state * 0x41C64E6D + 0x3039) & m
    s2 = (s1 * 0x41C64E6D + 0x3039) & m
    s3 = (s2 * 0x41C64E6D + 0x3039) & m
    value = ((s3 >> 16) & 0x3FF) ^ ((((s2 >> 16) & 0x3FF) ^ (((s1 >> 16) & 0x7FF) << 10)) << 10)
    return value, s3


def band_of(seed):
    r = next_random(seed & 0xFFFFFFFF)[0] % 100
    if r < 20:
        return 0
    if r < 40:
        return 1
    if r < 60:
        return 2
    return 3 if r < 80 else 4


def round_half_away(x):
    x = Fraction(x)
    return trunc(add32(x, C_0_5)) if x >= 0 else trunc(X.to_f32(x - C_0_5))


def jitter(base, percent, cap, band, state):
    """返回 (浮动后的值, 新状态)。基础值 0 不抽随机数。"""
    if base == 0:
        return 0, state
    if not 0 <= band <= 4:
        return base, state
    product = mul32(f32(percent), f32(base))
    rng = round_half_away(div32(product, C_100))
    m = rng if rng <= cap else cap
    width = max(1, 2 * m + 1)
    wf = f32(width)
    a = max(1, trunc(mul32(wf, C_0_2)))
    b = max(1, -trunc(mul32(wf, C_N0_4)) - a)
    c = max(1, width - 2 * (b + a))
    if band == 4 and state == 0x3B9AC9FE:
        return m + base, state
    r, state = next_random(state)
    if band == 4:
        return (m - r % a) + base, state
    if band == 3:
        return (max(0, m - a) - r % b) + base, state
    if band == 2:
        lead = b - m + a
        return (r % c + (lead if lead < 1 else 0)) + base, state
    if band == 1:
        lead = a - m
        return (r % b + (lead if lead < 1 else 0)) + base, state
    return (r % a - m) + base, state


def variation_deltas(values, pairs, seed):
    """values:十个基础值(整数);pairs:[(百分比, 上限)] 至少十对;返回十个差值。"""
    state = seed & 0xFFFFFFFF
    band = band_of(seed)
    out = []
    for i in range(10):
        varied, state = jitter(values[i], pairs[i][0], pairs[i][1], band, state)
        out.append(varied - values[i])
    return out


def applies(part):
    return 10 <= part <= 25 and part not in (11, 22)


# ---------------------------------------------------------------- 武器倍率

def synthetic_rates():
    """对拍用的合成倍率表(132 个,含第 11 个职业):0.5 + i/128,单精度下精确。"""
    return [float(Fraction(1, 2) + Fraction(i, 128)) for i in range(132)]


def first_job(jobs):
    if not jobs:
        return -1
    best = 1 << 30
    for tag in jobs:
        at = CLIENT_JOBS.index(tag) if tag in CLIENT_JOBS else 0
        best = min(best, at)
    return best


def weapon_rate(cells, jobs, sub, physical):
    job = first_job(jobs)
    if job < 0:
        return Fraction(0)
    if not 0 <= sub < SUBTYPES:
        return Fraction(0)
    if job >= JOBS_WITH_ROWS and len(cells) < (JOBS_WITH_ROWS + 1) * SUBTYPES * 2:
        return Fraction(1)
    return f32(cells[((job * SUBTYPES) + sub) * 2 + (0 if physical else 1)])


# ---------------------------------------------------------------- 强化属性

def channels(group, part, jobs, sub, base_pa, base_ma, cells):
    """group:八格(Fraction);返回 (物攻, 魔攻, 物防, 浮点甲, 魔防, 浮点乙, 四维)。"""
    if part == 10:
        pa = trunc(mul32(group[0], weapon_rate(cells, jobs, sub, True))) if base_pa != 0 else 0
        ma = trunc(mul32(group[0], weapon_rate(cells, jobs, sub, False))) if base_ma != 0 else 0
        return (pa, ma, 0, Fraction(0), 0, Fraction(0), 0)
    if 12 <= part <= 16:
        return (0, 0, trunc(group[1]), group[5], 0, Fraction(0), 0)
    if 17 <= part <= 19:
        return (0, 0, 0, Fraction(0), trunc(group[2]), group[6], 0)
    if part in (20, 21):
        return (0, 0, 0, Fraction(0), 0, Fraction(0), trunc(group[3]))
    return (0, 0, 0, Fraction(0), 0, Fraction(0), 0)


def format_channels(ch):
    return "%d,%d,%d,%d,%d,%s,%s" % (ch[0], ch[1], ch[2], ch[4], ch[6], bits(ch[3]), bits(ch[5]))


def effect_line(table, scaled, in_range, facts, level, cells):
    """一件装备在强化等级 level 的规范文本;没有效果是 "-"。table/scaled/in_range 来自 enhance_ref。"""
    part = facts["part"]
    if level <= 0 or not in_range(table, facts["rarity"], facts["grade"], level - 1, part):
        return "-"
    a, b = scaled(table, facts["rarity"], facts["grade"], level - 1)
    base_pa = facts["values"].get("[equipment physical attack]", 0)
    base_ma = facts["values"].get("[equipment magical attack]", 0)
    na = channels(a, part, facts["jobs"], facts["sub"], base_pa, base_ma, cells)
    nb = channels(b, part, facts["jobs"], facts["sub"], base_pa, base_ma, cells)
    if all(x == 0 for x in na) and all(x == 0 for x in nb):
        return "-"
    return "%s|%s" % (format_channels(na), format_channels(nb))


# ---------------------------------------------------------------- 物品事实(与 C# ItemScript.Parse 同一套"后者覆盖前者、取标签后第一个对应类型的值")

def first_int(vals):
    for k, p in vals:
        if k in ("i", "u"):
            return s32(p) if k == "u" else p
    return None


def facts_of(stats, classified):
    part = UNKNOWN_PART
    rarity = 0
    grade = 0
    sub = 0
    jobs = []
    for _depth, tag, vals in classified:
        if tag == "[equipment type]":
            text = next((p for k, p in vals if k == "s"), None)
            if text is not None:
                part = PART_TAGS.index(text.lower()) if text.lower() in PART_TAGS else UNKNOWN_PART
        elif tag == "[rarity]":
            v = first_int(vals)
            if v is not None:
                rarity = v
        elif tag == "[grade]":
            v = first_int(vals)
            if v is not None:
                grade = v
        elif tag == "[sub type]":
            v = first_int(vals)
            if v is not None:
                sub = v
        elif tag == "[usable job]":
            jobs = [p.lower() for k, p in vals if k == "s"]
    values = {}
    for tag in VARIED_TAGS:
        got = stats.get(tag)
        if got:
            x = got[0]
            values[tag] = int(x)
    return {"part": part, "rarity": rarity, "grade": grade, "sub": sub, "jobs": jobs, "values": values}


# ---------------------------------------------------------------- 转储

def parse_pairs(lines):
    """etc/etcparameter.etc 的 [stat random variation]:整数两个一对;不够十对返回 None。取最后一次出现。"""
    found = None
    for tag, vals in lines:
        if tag == "[stat random variation]":
            found = [s32(p) if k == "u" else (int(p) if k == "f" else p) for k, p in vals if k in ("i", "u", "f")]
    if found is None or len(found) // 2 < 10:
        return None
    n = min(17, len(found) // 2)
    return [(found[2 * i], found[2 * i + 1]) for i in range(n)]


def equipment_facts(pvf, strings, E):
    """全库装备 -> [(编号, facts)](按编号升序;读不到的跳过)。E 是 equip_stats_ref。"""
    items = E.read_items(pvf, strings)
    out = []
    for code in sorted(items):
        got = E.read_one(pvf, strings, items[code])
        if got is None:
            continue
        stats, _look, classified, _bal, _stray = got
        out.append((code, facts_of(stats, classified)))
    return out


def dump_effect(table, scaled, in_range, all_facts):
    cells = synthetic_rates()
    lines = []
    if table is None:
        return ["V\tinvalid"]
    rows = len(table.rows)
    for code, facts in all_facts:
        if not 10 <= facts["part"] <= 21:
            continue
        for level in LEVELS + (rows, rows + 1):
            lines.append("%d %d\t%s" % (code, level, effect_line(table, scaled, in_range, facts, level, cells)))
    return lines


def dump_variation(pairs, all_facts):
    if pairs is None:
        return ["V\tinvalid"]
    lines = []
    for code, facts in all_facts:
        if not applies(facts["part"]):
            continue
        values = [facts["values"].get(tag, 0) for tag in VARIED_TAGS]
        for seed in SEEDS:
            lines.append("%d %d\t%s" % (code, seed, ",".join(str(d) for d in variation_deltas(values, pairs, seed))))
    return lines
