#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""强化表、套装、附魔卡、强化券、品级样本的参考实现:和 C# 的 UpgradeTable / EnhanceData / EquipGradeSamples / EquipGradeGenerator 做双实现对拍。

两边各写各的,在同一份本机 Script.pvf 上读同一批数据,把结果写成规范文本再逐行比对,只报"一致 N / 不一致 M"的行数(不打印数据本身)。
这份实现不依赖 C# 的任何代码:数值走 x87 的精确模型(tools/py/x87.py),取整、舍入的次序按服务端静态读码写(地址见 out/enhance/NOTES.md),
物品脚本的切行、块深度复用 equip_stats_ref.py 里已经和 C# 全库对拍通过的那一套。

读法(两边共同遵守的约定):
  1. 强化表(etc/upgrade.etc,增幅表 etc/amplifyupgrade.etc 同格式):
       [table] 后面每 17 个数一行:A f1 f2 f3 f4 f5 | 失败概率 惩罚类型 惩罚级数 材料编号 材料数量 | B g1 g2 g3 g4 g5;
       稀有度权重、部位权重、档位、[cost]、修理系数、销毁等级、封顶等级、按强化等级的金币权重按各自标签读;缺的值按 0 或默认;
       "按稀有度"的段里稀有度名字不认得或缺整数,整张表作废(与服务端一样不是"跳过这一项")。
  2. 取一行并算数值:属性项 = 稀有度权重 × 项 ×(装备等级 ÷ 档位 + 1)(前四项)或 × 1(第 5、6 项),第 5、8 格恒 0,落成 float 才舍入;
       金币 = 三步取整(x87 截断):[cost][装备等级] × 部位权重、× 稀有度金币权重、(有权重时)× 按强化等级的权重(强化等级取到 18 为止)。
  3. 物品脚本:只认深度 0 的 [set item master]、[set item]、[set ability](块)、[piece set ability] N(块)、[enchant](块)、[monster card id]、
       [equipment reinforcement ticket]、[equipment amplify reinforcement ticket]、[limit upgradable level];同名后者覆盖;
       附魔卡能附的部位取 [string data] 里认得的部位标签(去重保持次序),只有有 [enchant] 块的物品才读。
  4. 品级:五段 [grade_N],每段的整数是样本;抽档时第 0 档权重先乘倍率(x87 截断),随机数含上界,第一个"随机数不大于累计权重"的档胜出。

用法(需要 DNF_DIR,见 local.env;只读,输出只写在被 git 忽略的 out/ 下):
  python3 tools/py/enhance_ref.py dump <目录>                      写出 upgrade_normal.txt upgrade_amplify.txt items.txt grade.txt pick.txt
  python3 tools/py/enhance_ref.py compare <C#目录> <Python目录> [--show]   逐行比对,只报计数(--show 额外打印前几个不一致的键,仅本机看)
"""
import argparse
import os
import re
import struct
import sys
from collections import Counter, OrderedDict
from fractions import Fraction

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import dnfdata as D  # noqa: E402
import equip_stats_ref as E  # noqa: E402
import x87 as X  # noqa: E402
from script_dump import Strings  # noqa: E402

UPGRADE_PATH = "etc/upgrade.etc"
AMPLIFY_PATH = "etc/amplifyupgrade.etc"
GRADE_PATH = "etc/equipmentgradesample.etc"
STACKABLE_LIST_PATH = "stackable/stackable.lst"

FILES = ("upgrade_normal.txt", "upgrade_amplify.txt", "items.txt", "grade.txt", "pick.txt")

RARITY_NAMES = ("common", "uncommon", "rare", "unique", "epic", "chronicle")
FIRST_PART, LAST_PART = 10, 21
COST_SLOTS = 100
WIDTH = 17

# 表里一行的 17 个数:文件列 -> (组内格子)。格子 0 是整数 A/B;格子 1、5、2、6、3 是 f1..f5 落在行结构里的位置(5、6 是不放大的两项)
FILE_COLUMN_TO_SLOT = {1: 1, 2: 5, 3: 2, 4: 6, 5: 3}

# 一组对拍用的网格(两边写同样的常数)
FEW_LEVELS = (0, 1, 7, 18, -1)            # -1 表示"表的最后一行"
SAMPLED = ((0, 0), (1, 33), (3, 50), (5, 99))   # (稀有度, 装备等级)
SCALED_ALL_LEVELS = None                  # 采样组合下遍历全部强化等级
GOLD_LEVELS = tuple(range(0, 21))         # 采样组合下金币遍历的强化等级(0..20 把 19 对权重都碰到)

MULTIPLIERS = (0.0, 0.5, 1.0, 1.5, 2.37, 10.0, 0.001)
WEIGHT_SETS = ((1000, 1000, 1000, 1000, 1000), (1, 2, 3, 4, 5), (0, 0, 0, 0, 0), (5000, 1, 1, 1, 1), (7,),
               (2000000000, 2000000000, 1, 1, 1))
ROLLS = (0, 1, 2, 499, 500, 999, 1000, 1001, 1500, 2000, 2999, 3000, 3001, 4000, 4999, 5000, 5001, 7000, 100000)
MULTIPLIER_TABLES = OrderedDict([
    ("A", [(50, 1.5)]),
    ("B", [(10, 2.0), (30, 3.0), (60, 4.0)]),
    ("C", [(60, 4.0), (30, 3.0), (10, 2.0)]),
    ("D", [(20, 2.0), (20, 3.0), (20, 4.0)]),
    ("E", []),
])
MULTIPLIER_LEVELS = tuple(range(0, 81))


# ---------------------------------------------------------------- 数的读法

def s32(v):
    """无符号 32 位 -> 有符号。"""
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v >= (1 << 31) else v


def to_f32(x):
    """整数或浮点 -> 最近的 float32 值(Python 浮点表示)。"""
    return struct.unpack("<f", struct.pack("<f", float(x)))[0]


def int_of(kind, p):
    """ScanInt:整数照读,浮点按截断。"""
    if kind == "f":
        return int(p)
    return s32(p) if kind == "u" else p


def float_of(kind, p):
    """ScanFloat:浮点照读,整数先落成 float32。"""
    if kind == "f":
        return p
    return to_f32(s32(p) if kind == "u" else p)


def numeric(vals):
    return [(k, p) for k, p in vals if k in ("i", "u", "f")]


def bits(x):
    """float32 值的 32 位模式(十六进制,8 位)。"""
    return "%08x" % struct.unpack("<I", struct.pack("<f", float(x)))[0]


# ---------------------------------------------------------------- 强化表

class Table:
    def __init__(self):
        self.rows = []
        self.level = [1.0] * 4
        self.type_weight = [1.0] * 22
        self.cost_rarity = [1.0] * 6
        self.stat_rarity = [1.0] * 6
        self.cost = [1] * COST_SLOTS
        self.repair = [1.0] * COST_SLOTS
        self.destroy = [-1] * 6
        self.maxlv = [-1] * 6
        self.level_cost = {}
        self.notice = 0
        self._stat_cache = {}
        self._gold_cache = {}


def read_floats(t, nums, into, start, count):
    v = [float_of(k, p) for k, p in nums]
    for i in range(count):
        into[start + i] = v[i] if i < len(v) else 0.0


def read_by_rarity(vals, into):
    """"稀有度名字 整数"成对;名字不认得或后面不是整数返回 False。"""
    i = 0
    while i < len(vals):
        kind, text = vals[i]
        if kind != "s":
            i += 1
            continue
        rarity = RARITY_NAMES.index(text) if text in RARITY_NAMES else 6
        if i + 1 >= len(vals) or vals[i + 1][0] not in ("i", "u"):
            return False
        if rarity >= 6:
            return False
        into[rarity] = int_of(vals[i + 1][0], vals[i + 1][1])
        i += 2
    return True


def parse_table(lines):
    t = Table()
    for tag, vals in lines:
        if tag is None:
            continue
        nums = numeric(vals)
        if tag == "[table]":
            t.rows = []
            at = 0
            while at + WIDTH <= len(nums):
                chunk = nums[at: at + WIDTH]
                row = {"a": [0.0] * 8, "b": [0.0] * 8, "amp": [0] * 4}
                for group, base in (("a", 0), ("b", 11)):
                    row[group][0] = float(int_of(*chunk[base]))
                    for col, slot in FILE_COLUMN_TO_SLOT.items():
                        row[group][slot] = float_of(*chunk[base + col])
                row["fail"] = int_of(*chunk[6])
                row["pen"] = int_of(*chunk[7])
                row["penlv"] = int_of(*chunk[8])
                row["mat"] = int_of(*chunk[9])
                row["matcnt"] = int_of(*chunk[10])
                t.rows.append(row)
                at += WIDTH
        elif tag == "[amplification const]":
            v = [int_of(k, p) for k, p in nums]
            for i, row in enumerate(t.rows):
                for k in range(4):
                    if i * 4 + k < len(v):
                        row["amp"][k] = v[i * 4 + k]
        elif tag == "[cost weights by rarity]":
            read_floats(t, nums, t.cost_rarity, 0, 6)
        elif tag == "[stat weights by rarity]":
            read_floats(t, nums, t.stat_rarity, 0, 6)
        elif tag == "[type]":
            read_floats(t, nums, t.type_weight, FIRST_PART, LAST_PART - FIRST_PART + 1)
        elif tag == "[level]":
            read_floats(t, nums, t.level, 0, 4)
        elif tag == "[cost]":
            v = [int_of(k, p) for k, p in nums]
            for i in range(COST_SLOTS):
                t.cost[i] = v[i] if i < len(v) else 0
        elif tag == "[notice]":
            if nums:
                t.notice = int_of(*nums[0])
        elif tag == "[repair cost rate by upgrade level]":
            v = [float_of(k, p) for k, p in nums]
            for i in range(COST_SLOTS):
                t.repair[i] = v[i] if i < len(v) else 0.0
        elif tag == "[destroy level by rarity]":
            if not read_by_rarity(vals, t.destroy):
                return None
        elif tag == "[max upgrade level by rarity]":
            if not read_by_rarity(vals, t.maxlv):
                return None
        elif tag == "[cost weight by upgrade level]":
            t.level_cost = {}
            v = [float_of(k, p) for k, p in nums]
            for i in range(0, len(v) - 1, 2):
                t.level_cost[int(v[i])] = v[i + 1]
    return t


def in_range(t, rarity, grade, level, part):
    return 0 <= level < len(t.rows) and 0 <= rarity < 6 and FIRST_PART <= part <= LAST_PART and 0 <= grade < COST_SLOTS


def scaled(t, rarity, grade, level):
    """按稀有度权重与装备等级放大后的两组属性项(各 8 个,Fraction)。"""
    key = (rarity, grade, level)
    got = t._stat_cache.get(key)
    if got is not None:
        return got
    row = t.rows[level]
    weight = X.from_f32(t.stat_rarity[rarity])
    one = X.from_f32(1.0)
    grade_reg = X.from_int(grade)
    out = []
    for group in ("a", "b"):
        vals = []
        for k in range(8):
            if k in (4, 7):
                vals.append(Fraction(0))
                continue
            product = X.mul(weight, X.from_f32(row[group][k]))
            if k < 4:
                factor = X.add(X.div(grade_reg, X.from_f32(t.level[k])), one)
                product = X.mul(product, factor)
            vals.append(X.to_f32(product))
        out.append(vals)
    t._stat_cache[key] = out
    return out


def gold_cost(t, rarity, grade, level, part):
    key = (rarity, grade, min(level, 18), part)
    got = t._gold_cache.get(key)
    if got is not None:
        return got
    half = X.from_f32(0.5)
    c = t.cost[grade]
    c = X.trunc_i32(X.add(half, X.mul(X.from_int(c), X.from_f32(t.type_weight[part]))))
    c = X.trunc_i32(X.add(half, X.mul(X.from_int(c), X.from_f32(t.cost_rarity[rarity]))))
    weight = t.level_cost.get(min(level, 18))
    if weight is not None:
        c = X.trunc_i32(X.mul(X.from_f32(weight), X.from_f32(float(c))))
    t._gold_cache[key] = c
    return c


def penalty_type(t, level, rarity):
    row = t.rows[level] if 0 <= level < len(t.rows) else None
    destroy = t.destroy[rarity] if 0 <= rarity < 6 else -1
    if destroy == -1 or level < destroy:
        return 1 if row is None else row["pen"]
    return 3


def last_level(t, level):
    return len(t.rows) - 1 if level == -1 else level


def upgrade_lines(t):
    rows = len(t.rows)
    out = ["M\t%d %d" % (rows, t.notice)]
    for r in range(6):
        out.append("D %d\t%d %d" % (r, t.destroy[r], t.maxlv[r]))
    for l in range(COST_SLOTS):
        out.append("P %d\t%s" % (l, bits(t.repair[l])))
    for l, row in enumerate(t.rows):
        out.append("R %d\t%d %d %d %d %d %s %s %s" % (
            l, row["fail"], row["pen"], row["penlv"], row["mat"], row["matcnt"],
            ",".join(str(x) for x in row["amp"]),
            ",".join(bits(x) for x in row["a"]), ",".join(bits(x) for x in row["b"])))
    for r in range(-1, 8):
        for l in range(-1, rows + 2):
            out.append("T %d %d\t%d" % (r, l, penalty_type(t, l, r)))
    if rows == 0:
        return out
    # 取行的边界:稀有度、装备等级、强化等级、部位各取两端内外
    for r in (-1, 0, 5, 6):
        for g in (-1, 0, 99, 100):
            for l in (-1, 0, rows - 1, rows):
                for p in (9, 10, 21, 22):
                    out.append("N %d %d %d %d\t%s" % (r, g, l, p, "ok" if in_range(t, r, g, l, p) else "null"))
    for r in range(6):
        for g in range(COST_SLOTS):
            for l in FEW_LEVELS:
                lv = last_level(t, l)
                a, b = scaled(t, r, g, lv)
                out.append("S %d %d %d\t%s %s" % (r, g, lv, ",".join(bits(x) for x in a), ",".join(bits(x) for x in b)))
                out.append("G %d %d %d\t%s" % (r, g, lv, ",".join(str(gold_cost(t, r, g, lv, p)) for p in range(FIRST_PART, LAST_PART + 1))))
    for r, g in SAMPLED:
        for lv in range(rows):
            a, b = scaled(t, r, g, lv)
            out.append("S %d %d %d\t%s %s" % (r, g, lv, ",".join(bits(x) for x in a), ",".join(bits(x) for x in b)))
        for lv in tuple(x for x in GOLD_LEVELS if x < rows) + (rows - 1,):
            out.append("G %d %d %d\t%s" % (r, g, lv, ",".join(str(gold_cost(t, r, g, lv, p)) for p in range(FIRST_PART, LAST_PART + 1))))
    return out


# ---------------------------------------------------------------- 品级

def parse_grade(lines):
    """[(档, 样本列表)];下标必须从 0 连续,否则整份作废(返回 None)。"""
    bands = {}
    for tag, vals in lines:
        m = re.fullmatch(r"\[grade_(\d+)\]", tag or "")
        if not m:
            continue
        bands[int(m.group(1))] = [s32(p) if k == "u" else p for k, p in vals if k in ("i", "u")]
    if not bands:
        return None
    for i, key in enumerate(sorted(bands)):
        if key != i:
            return None
    return [bands[k] for k in sorted(bands)]


def multiplier_for(table, level):
    m = 1.0
    if not table:
        return m
    last = len(table) - 1
    if table[0][0] < level:
        m = table[0][1]
    elif level < table[last][0]:
        m = table[last][1]
    else:
        for key, value in table:
            if key == level:
                m = value
    return m


def pick(weights, mult, roll):
    """返回 (档, 传给随机源的上界);不抽签时 (-1, -1)。"""
    w = list(weights)
    w[0] = X.trunc_i32(X.mul(X.from_int(weights[0]), X.from_f32(mult)))
    total = sum(w)
    if total <= 0 or total > 2 ** 31 - 1:
        return -1, -1
    roll = min(roll, total)
    acc = 0
    for i, x in enumerate(w):
        acc += x
        if roll <= acc:
            return i, total
    return len(w) - 1, total


def grade_lines(bands):
    if bands is None:
        return ["V\tinvalid"]
    out = ["V\t%d" % len(bands)]
    for i, samples in enumerate(bands):
        out.append("B %d\t%d %d %d" % (i, len(samples), samples[0] if samples else 0, sum(samples)))
    return out


def pick_lines():
    out = []
    for name, table in MULTIPLIER_TABLES.items():
        for level in MULTIPLIER_LEVELS:
            out.append("L %s %d\t%s" % (name, level, bits(multiplier_for([(k, to_f32(v)) for k, v in table], level))))
    for mult in MULTIPLIERS:
        m = to_f32(mult)
        for weights in WEIGHT_SETS:
            for roll in ROLLS:
                band, maximum = pick(weights, m, roll)
                out.append("K %s %s %d\t%d %d" % (bits(m), ",".join(str(x) for x in weights), roll, band, maximum))
    return out


# ---------------------------------------------------------------- 物品脚本

INTERESTING = frozenset((
    "[set item master]", "[set item]", "[set ability]", "[piece set ability]", "[enchant]", "[monster card id]",
    "[equipment reinforcement ticket]", "[equipment amplify reinforcement ticket]", "[limit upgradable level]",
))
UPGRADE_TYPES = {"normal upgrade": 1, "amplify upgrade": 2, "separate upgrade": 3, "3rd chronicle upgrade": 4}
PART_TAGS = (
    "[hat avatar]", "[hair avatar]", "[face avatar]", "[coat avatar]", "[pants avatar]", "[shoes avatar]", "[breast avatar]",
    "[waist avatar]", "[skin avatar]", "[aurora avatar]", "[weapon]", "[title name]", "[coat]", "[shoulder]", "[pants]",
    "[shoes]", "[waist]", "[amulet]", "[wrist]", "[ring]", "[support]", "[magic stone]", "[creature]", "[artifact red]",
    "[artifact blue]", "[artifact green]",
)


def read_list(pvf, strings, list_path):
    """.lst 里"编号 路径"成对的清单(编号取首次出现,路径相对清单所在目录)。"""
    raw = pvf.get(list_path)
    if raw is None or raw[:2] != E.MAGIC:
        raise SystemExit("读不到 " + list_path)
    toks = list(struct.iter_unpack("<Bi", raw[2:]))
    items = {}
    i = 0
    while i + 1 < len(toks):
        if toks[i][0] in (E.T_INT, E.T_UINT, E.T_FLOAT) and toks[i + 1][0] == E.T_STRING:
            code = toks[i][1]
            if code not in items:
                items[code] = E.resolve_relative(list_path, strings.get(toks[i + 1][1]))
            i += 2
        else:
            i += 1
    return items


def ints_of(vals):
    return [s32(p) if k == "u" else p for k, p in vals if k in ("i", "u")]


def block_stats(lines, at, closer):
    """第 at 行(开头标签)到它后面第一个 closer 之间直接写的属性;没有收尾返回 None。"""
    for j in range(at + 1, len(lines)):
        if lines[j][0] == closer:
            classified, _balanced, _stray = E.classify(lines[at + 1: j])
            return E.stats_of(classified)
    return None


def ticket_of(vals):
    v = ints_of(vals)
    if len(v) < 2:
        return None
    return (v[0] if 0 <= v[0] <= 31 else 0, v[1] if 0 <= v[1] <= 100 else 0)


def read_item(raw, strings):
    """物品脚本 -> 规范文本(对应 C# 的 EnhanceData.Read;没有任何相关标签就是默认值那一行)。"""
    lines = E.split_lines(raw, strings)
    master = 0
    members = None
    full = None
    pieces = {}
    enchant = None
    card = 0
    reinforce = None
    amplify = None
    ranges = []
    if any(tag in INTERESTING for tag, _ in lines):
        classified, _balanced, _stray = E.classify(lines)
        for at, (depth, tag, vals) in enumerate(classified):
            if depth != 0 or tag not in INTERESTING:
                continue
            if tag == "[set item master]":
                v = ints_of(vals)
                if v:
                    master = v[0]
            elif tag == "[set item]":
                members = ints_of(vals)
            elif tag == "[set ability]":
                block = block_stats(lines, at, "[/set ability]")
                if block is not None:
                    full = block
            elif tag == "[piece set ability]":
                block = block_stats(lines, at, "[/piece set ability]")
                v = ints_of(vals)
                if block is not None and v:
                    pieces[v[0]] = block
            elif tag == "[enchant]":
                block = block_stats(lines, at, "[/enchant]")
                if block is not None:
                    enchant = block
            elif tag == "[monster card id]":
                v = ints_of(vals)
                if v:
                    card = v[0]
            elif tag == "[equipment reinforcement ticket]":
                reinforce = ticket_of(vals) or reinforce
            elif tag == "[equipment amplify reinforcement ticket]":
                amplify = ticket_of(vals) or amplify
            else:
                kind = 0
                numbers = []
                for k, p in vals:
                    if k == "s":
                        kind = UPGRADE_TYPES.get(p.lower(), 0)
                        numbers = []
                    elif k in ("i", "u"):
                        numbers.append(s32(p) if k == "u" else p)
                        if len(numbers) == 2:
                            if kind != 0 and all(r[0] != kind for r in ranges):
                                lo = 1 if numbers[0] == -1 else numbers[0]
                                hi = 70 if numbers[1] == -1 else numbers[1]
                                ranges.append((kind, lo, hi))
                            numbers = []
    parts = []
    if enchant is not None:
        parts = enchant_parts(classified)
    return "set=%d/%s/%s/%s|ench=%s/%d/%s|tick=%s/%s|rng=%s" % (
        master,
        ",".join(str(x) for x in members) if members else "-",
        "~" if full is None else E.fmt_stats(full),
        "^".join("%d:%s" % (n, E.fmt_stats(pieces[n])) for n in sorted(pieces)) or "-",
        "~" if enchant is None else E.fmt_stats(enchant),
        card,
        ",".join(str(x) for x in parts) or "-",
        "-" if reinforce is None else "%d:%d" % reinforce,
        "-" if amplify is None else "%d:%d" % amplify,
        ",".join("%d:%d:%d" % r for r in ranges) or "-")


def enchant_parts(classified):
    """深度 0 的 [string data] 里认得的部位标签(去重保持次序)。"""
    parts = []
    for depth, tag, vals in classified:
        if depth != 0 or tag != "[string data]":
            continue
        for k, p in vals:
            if k != "s":
                continue
            text = p.lower()
            if text in PART_TAGS:
                part = PART_TAGS.index(text)
                if part not in parts:
                    parts.append(part)
    return parts


def item_lines(pvf, strings):
    items = read_list(pvf, strings, E.LIST_PATH)
    for code, path in read_list(pvf, strings, STACKABLE_LIST_PATH).items():
        items.setdefault(code, path)
    out = []
    missing = 0
    for code in sorted(items):
        raw = pvf.get(items[code])
        if raw is None or raw[:2] != E.MAGIC:
            missing += 1
            out.append("%d\t<none>" % code)
            continue
        out.append("%d\t%s" % (code, read_item(raw, strings)))
    return out, len(items), missing


# ---------------------------------------------------------------- 转储与比对

def write_lines(path, lines):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for line in lines:
            f.write(line)
            f.write("\n")


def table_from(pvf, strings, path):
    raw = pvf.get(path)
    if raw is None or raw[:2] != E.MAGIC:
        return None
    return parse_table(E.split_lines(raw, strings))


def cmd_dump(pvf, strings, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for name, path in (("upgrade_normal.txt", UPGRADE_PATH), ("upgrade_amplify.txt", AMPLIFY_PATH)):
        t = table_from(pvf, strings, path)
        lines = ["V\tinvalid"] if t is None else upgrade_lines(t)
        write_lines(os.path.join(out_dir, name), lines)
        print("%s:%d 行(表 %s)" % (name, len(lines), "读不出" if t is None else "%d 行数据" % len(t.rows)))
    lines, total, missing = item_lines(pvf, strings)
    write_lines(os.path.join(out_dir, "items.txt"), lines)
    print("items.txt:%d 件(读不到 %d 件)" % (total, missing))
    raw = pvf.get(GRADE_PATH)
    bands = parse_grade(E.split_lines(raw, strings)) if raw is not None and raw[:2] == E.MAGIC else None
    write_lines(os.path.join(out_dir, "grade.txt"), grade_lines(bands))
    lines = pick_lines()
    write_lines(os.path.join(out_dir, "pick.txt"), lines)
    print("grade.txt、pick.txt:%d 行(抽档与倍率)" % len(lines))


NUMBER = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


def normalize(text):
    """把文本里的数都规范成 repr(float)(C# 的 "R" 与 Python 的 repr 对 1E-05、100 之类写法不同,数值相同)。"""
    return NUMBER.sub(lambda m: repr(float(m.group())), text)


def load_dump(path):
    rows = OrderedDict()
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            key, _, value = line.partition("\t")
            rows[key] = value
    return rows


def kind_of(name, key):
    return "件" if name == "items.txt" else key.split(" ")[0]


def cmd_compare(cs_dir, py_dir, show):
    total_bad = 0
    total_same = 0
    for name in FILES:
        cs_path, py_path = os.path.join(cs_dir, name), os.path.join(py_dir, name)
        if not os.path.exists(cs_path) or not os.path.exists(py_path):
            print("%s:缺文件(C# %s,Python %s)" % (name, os.path.exists(cs_path), os.path.exists(py_path)))
            total_bad += 1
            continue
        cs, py = load_dump(cs_path), load_dump(py_path)
        same, bad, only_cs, only_py = Counter(), Counter(), Counter(), Counter()
        bad_keys = []
        for key in cs:
            kind = kind_of(name, key)
            if key not in py:
                only_cs[kind] += 1
            elif (normalize(cs[key]) == normalize(py[key])) if name == "items.txt" else cs[key] == py[key]:
                same[kind] += 1
            else:
                bad[kind] += 1
                bad_keys.append(key)
        for key in py:
            if key not in cs:
                only_py[kind_of(name, key)] += 1
        kinds = sorted(set(same) | set(bad) | set(only_cs) | set(only_py))
        print("%s:C# %d 行,Python %d 行;一致 %d / 不一致 %d(只在 C# %d,只在 Python %d)" % (
            name, len(cs), len(py), sum(same.values()), sum(bad.values()), sum(only_cs.values()), sum(only_py.values())))
        if len(kinds) > 1:
            print("  按类:" + ",".join("%s %d/%d" % (k, same[k], same[k] + bad[k] + only_cs[k] + only_py[k]) for k in kinds))
        if show and bad_keys:
            print("  不一致的前 5 个键:", bad_keys[:5])
        total_same += sum(same.values())
        total_bad += sum(bad.values()) + sum(only_cs.values()) + sum(only_py.values())
    print("合计:一致 %d / 不一致 %d" % (total_same, total_bad))
    return 0 if total_bad == 0 else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("dump")
    d.add_argument("out")
    c = sub.add_parser("compare")
    c.add_argument("cs")
    c.add_argument("py")
    c.add_argument("--show", action="store_true")
    args = ap.parse_args()
    if args.cmd == "compare":
        return cmd_compare(args.cs, args.py, args.show)
    pvf = D.Pvf()
    strings = Strings(pvf)
    cmd_dump(pvf, strings, args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
