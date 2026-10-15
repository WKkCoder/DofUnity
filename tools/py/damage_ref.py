#!/usr/bin/env python3
"""伤害公式的 Python 参考实现与对拍用例生成器(证据 E-049)。

依据与 C# 版(packages/com.dnfunity.game/Runtime/Damage/DamageFormula.cs)相同:服务端"伤害校验"模块的汇编
(computeHpDamageSDC 及其子函数)。两份实现是分别照汇编逐条写出来的,用来互相找笔误:

  python3 tools/py/damage_ref.py --gen 20000 --seed 1 --out out/damage_cases.txt
  dnf damage-check --cases out/damage_cases.txt

数值语义与 C# 版一致:每条 C 语句的结果落地成 float(f32),语句内部用 Python 的双精度(原版是 x87 扩展精度,
差异只在极少数舍入边界);(int) 转换向零取整,溢出得 -2^31;除零按 IEEE 得到 inf/nan;比较遇到 nan 一律为假。
用例一半是"混沌输入"(随机的结构内容,含 nan/inf 这类极端值,覆盖各个分支和下限/上限),
一半是"合理取值"(数值落在像真的范围里,让大多数分支真的有数值参与,不会被钳位吞掉,对笔误更灵敏);不含任何原版数据。
"""

import argparse
import math
import random
import struct
import sys

SIZE = 0x1FC


def f32(x):
    """双精度 -> float32(就近舍入);溢出得 ±inf,nan 保持 nan。"""
    try:
        return struct.unpack("<f", struct.pack("<f", x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)


def fdiv(a, b):
    """IEEE 除法:除数为 0 时得 inf 或 nan,不抛异常。"""
    try:
        return a / b
    except ZeroDivisionError:
        if a != a or a == 0:
            return math.nan
        return math.copysign(math.inf, a) * math.copysign(1.0, b)


def trunc_i32(x):
    """x87 FISTP(截断)的 int32 转换:溢出或 nan 得 -2^31。"""
    if x != x or x >= 2147483648.0 or x <= -2147483649.0:
        return -2147483648
    return int(x)


def i32_wrap(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


# 汇编里的 float 常量(@0x08B12F54 起)与立即数
C_PCT = f32(0.01)
C_SAME_ELEMENT = f32(1.05)
C_FIXED_SPREAD = f32(0.07)
C_QUARTER = f32(0.25)
C_HUNDRED = f32(100.0)
C_BONUS_HIT = f32(1.1)
C_CRIT_BASE = f32(3.0)

# 客户端结构初始化函数(0x00818110)里默认值为 1.0 的浮点偏移
ONES = [0x06, 0x13, 0x17, 0x1B, 0x1F, 0x23, 0x27, 0x2B, 0x37, 0x3B, 0x47, 0x4B, 0x4F, 0x53, 0x57, 0x5B, 0x5F,
        0x6B, 0x6F, 0x77, 0x8F, 0xB3, 0x1A6, 0x1F0]


class Blob:
    def __init__(self, data=None):
        self.b = bytearray(SIZE)
        if data is not None:
            self.b[: len(data)] = data[:SIZE]

    @staticmethod
    def with_defaults():
        p = Blob()
        for o in ONES:
            p.set_f(o, 1.0)
        for i in range(52):
            p.set_f(0xD6 + 4 * i, 1.0)
        p.set_i(0x97, 1)
        p.set_i(0xA3, 1)
        p.set_i(0x1D5, 1)
        p.b[0x1B3] = 1
        p.b[0x1B4] = 1
        return p

    def f(self, o):
        return struct.unpack_from("<f", self.b, o)[0]

    def set_f(self, o, v):
        struct.pack_into("<f", self.b, o, v)

    def i(self, o):
        return struct.unpack_from("<i", self.b, o)[0]

    def set_i(self, o, v):
        struct.pack_into("<i", self.b, o, i32_wrap(v))

    def u(self, o):
        return struct.unpack_from("<I", self.b, o)[0]

    def d(self, o):
        return struct.unpack_from("<d", self.b, o)[0]

    def u8(self, o):
        return self.b[o] if 0 <= o < SIZE else 0

    def s8(self, o):
        v = self.u8(o)
        return v - 256 if v > 127 else v


class Constants:
    """26 个公式常量(文件顺序)+ 两个写死的初值 + 召唤物表。"""

    FIELDS = [
        "phys_dmg", "phys_dmg_add", "phys_reduce", "phys_reduce_add", "phys_rand_base", "phys_rand_rate",
        "mag_dmg", "mag_dmg_add", "mag_reduce", "mag_reduce_add", "mag_rand_base", "mag_rand_rate",
        "elem_base", "elem_rate", "elem_rate_pvp", "elem_rate_ladder", "hp_inc", "hp_regen", "mp_inc", "mp_regen",
        "status_dmg", "status_def", "phys_def_add", "mag_def_add", "hp_regen_add_rate", "mp_regen_add_rate",
    ]

    def __init__(self):
        for n in self.FIELDS:
            setattr(self, n, 0.0)
        self.max_total_defense = f32(0.01)
        self.crit_default = 1.5
        self.vector = []
        self.module_rates = {}
        self.index_rates = {}

    def creature_level_rate(self, level, add, extra):
        if level > 50:
            level = 50
        total = i32_wrap(level + add)
        rate = 1.0
        v = self.vector
        if len(v) > 6:
            s = v[0] + float(extra)
            lead = float(v[1]) * total + s
            ratio_num = float(v[3]) * total + v[2]
            product = (fdiv(ratio_num, float(v[4])) + 1.0) * lead
            tail_num = float(v[3]) * total + v[5]
            rate = (fdiv(tail_num, float(v[6])) + product) / 100.0 * rate
        return f32(rate)


def is_type(code, mask):
    return (code & mask) == mask


def sdc_rand(seed):
    m, c = 0x41C64E6D, 0x3039
    x1 = (seed * m + c) & 0xFFFFFFFF
    x2 = (x1 * m + c) & 0xFFFFFFFF
    x3 = (x2 * m + c) & 0xFFFFFFFF
    r = (((((x1 >> 16) & 0x7FF) << 10) ^ ((x2 >> 16) & 0x3FF)) << 10) ^ ((x3 >> 16) & 0x3FF)
    return r, x3


def uniform(a, b, i, n):
    if i >= n:
        return b
    if i <= 0:
        return a
    return ((b - a) * i) / n + a


def valid_element(p, idx):
    if idx == 4:
        return p.u8(0x0A) == 0 and p.u8(0x0B) == 0 and p.u8(0x0C) == 0 and p.u8(0x0D) == 0
    return p.u8(0x0A + idx) != 0


def basis_level(p, flag):
    if flag and is_type(p.u(0x1BE), 0x111):
        return p.s8(0x1B4)
    return p.s8(0x1B3)


def inflict_collision(p, k):
    kind = p.u8(0x05)
    x = p.f(0x00)
    if p.u8(0x04):
        if kind == 0 or kind == 2:
            x = f32(p.f(0x2F) + x)
        elif kind == 1:
            x = f32(p.f(0x33) + x)
    x = f32(((p.f(0x06) * C_PCT) + 1.0) * x)
    if kind == 0:
        x = f32(((p.f(0x37) * C_PCT) + 1.0) * x)
    elif kind == 1:
        x = f32(((p.f(0x3B) * C_PCT) + 1.0) * x)
    if kind == 0:
        c = p.f(0x3F)
        if p.u8(0x1B2) != 4 and p.u8(0x1B2) != 5:
            c = f32(1.0 * c)
        x = f32((fdiv(c, k.phys_dmg) + 1.0) * x)
    elif kind == 1:
        c = p.f(0x43)
        if p.u8(0x1B2) != 4 and p.u8(0x1B2) != 5:
            c = f32(1.0 * c)
        x = f32((fdiv(c, k.mag_dmg) + 1.0) * x)
    x = f32(1.0 * x)
    x = f32(p.f(0x47) * x + x)
    return x


def inflict_creature(p, k):
    if p.u8(0x1B5) == 0:
        return 0.0
    x0 = p.f(0x00)
    rate = k.creature_level_rate(p.s8(0x1B7), p.s8(0x1B8), trunc_i32(p.d(0x1AA)))
    x = f32(rate * x0)
    x = f32(p.f(0x2B) * x)
    x = f32(k.module_rates.get(p.s8(0x1B2), 1.0) * x)
    if p.u8(0x1B2) == 9:
        x = f32(k.index_rates.get(p.s8(0x1B9), 1.0) * x)
    return x


def inflict(p, k):
    damager = p.u(0x1BA)
    if is_type(damager, 0x21):
        if p.u8(0x1B5) and p.u8(0x1B6):
            return inflict_creature(p, k)
        return inflict_collision(p, k)
    if is_type(damager, 0x81):
        return inflict_creature(p, k)
    return inflict_collision(p, k)


def defense_rate(p, k):
    kind = p.u8(0x05)
    level = basis_level(p, False)
    defense = 0.0
    reduce = 0.0
    pen = 0.0
    if kind == 0:
        defense = f32(float(p.i(0x93)))
        defense = f32((float(p.i(0x97)) * C_PCT + 1.0) * defense)
        reduce = k.phys_reduce
        pen = f32(p.f(0x9B) * C_PCT)
        if p.u8(0x1B2) == 9:
            pen = f32(1.0 * pen)
    elif kind == 1:
        defense = f32(float(p.i(0x9F)))
        defense = f32((float(p.i(0xA3)) * C_PCT + 1.0) * defense)
        reduce = k.mag_reduce
        pen = f32(p.f(0xA7) * C_PCT)
        if p.u8(0x1B2) == 9:
            pen = f32(1.0 * pen)
    defense = f32(1.0 * defense)
    if p.u8(0x1E5):
        reduce = f32(reduce + reduce)
    positive = defense if 0.0 < defense else 0.0
    den = f32(float(level) * reduce + positive)
    den = 1.0 if den < 1.0 else den
    rate = f32(fdiv(positive, den))
    return rate, pen


def check_critical(p, k, dmg, seed):
    """返回 (新伤害, 新种子, 是否暴击);同时写入 p 的 +0x1CE。"""
    kind = p.u8(0x05)
    chance = C_CRIT_BASE
    chance = f32(p.f(0x13) + chance)
    if p.u8(0x1B6) != 0:
        if is_type(p.u(0x1BE), 0x81):
            chance = f32(p.f(0x5B) + chance)
    elif is_type(p.u(0x1BE), 0x11):
        if kind == 0:
            chance = f32(p.f(0x4B) + chance)
        elif kind == 1:
            chance = f32(p.f(0x4F) + chance)
    if p.u8(0x1DE) != 0:
        chance = f32(p.f(0x17) + chance)
        if is_type(p.u(0x1BE), 0x11):
            if kind == 0:
                chance = f32(p.f(0x53) + chance)
            elif kind == 1:
                chance = f32(p.f(0x57) + chance)
    if is_type(p.u(0x1C2), 0x11):
        chance = f32(chance - p.f(0xCF))
    r, seed = sdc_rand(seed)
    roll = r % 10000
    if chance * C_HUNDRED > roll:
        m = k.crit_default
        if is_type(p.u(0x1BE), 0x11):
            m = f32(p.f(0x6F) * m)
        m = f32(p.f(0x73) + m)
        dmg = f32(dmg * m)
        p.b[0x1CE] = 1
    else:
        p.b[0x1CE] = 0
    return dmg, seed, p.b[0x1CE] != 0


def compute(p, k):
    """computeHpDamageSDC:返回 (结果, 是否暴击, 额外命中加成量);同时写入 p。"""
    seed = p.u(0x1D9)
    kind = p.u8(0x05)
    d = f32(inflict(p, k))

    if p.u8(0x1DD) != 4 and valid_element(p, p.s8(0x1DD)):
        d = f32(C_SAME_ELEMENT * d)

    if is_type(p.u(0x1C2), 0x11) and not valid_element(p, 4):
        best = -1.0
        for i in range(4):
            if p.u8(0x0A + i) != 0:
                resist = p.i(0x7F + 4 * i)
                v = f32(((k.elem_base - float(resist) * p.f(0x8F)) * C_PCT) * d)
                if v > best:
                    best = v
        d = best

    spread = 0.0
    if kind == 0:
        spread = k.phys_rand_base
        lv = basis_level(p, p.u8(0x04) != 0)
        spread = f32(float(lv) * k.phys_rand_rate + spread)
    elif kind == 1:
        spread = k.mag_rand_base
        lv = basis_level(p, p.u8(0x04) != 0)
        spread = f32(float(lv) * k.mag_rand_rate + spread)
    elif kind == 2:
        spread = f32(d * C_FIXED_SPREAD)
    if p.u8(0x1B2) == 0x0E:
        spread = 0.0
    r, seed = sdc_rand(seed)
    d = f32(d + uniform(-spread, spread, r % 1000, 999))

    if is_type(p.u(0x1C2), 0x11) and kind != 2 and p.u8(0x0E) == 0:
        rate, pen = defense_rate(p, k)
        m = f32((1.0 - rate) - pen * p.f(0x5F))
        if m < k.max_total_defense:
            m = k.max_total_defense
        if m < 0.0:
            m = 0.0
        d = f32(d * m)

    if kind == 0:
        a = p.f(0x0F)
        if p.u8(0x04):
            a = f32(float(p.i(0x63)) * p.f(0xB3) + a)
        a = f32(((p.f(0x06) * C_PCT) + 1.0) * a)
        a = f32(((p.f(0x37) * C_PCT) + 1.0) * a)
        d = f32(d + 1.0 * a)
        if is_type(p.u(0x1C2), 0x11) and p.u8(0x0E) == 0:
            f = f32(float(p.i(0xB7)))
            f = f32(p.f(0x5F) * f)
            f = f32(float(p.i(0xC7)) + f)
            saved = d
            d = f32(saved - 1.0 * f)
        d = f32(d - p.f(0xBF) * d)
    elif kind == 1:
        a = p.f(0x0F)
        if p.u8(0x04):
            a = f32(float(p.i(0x67)) * p.f(0xB3) + a)
        a = f32(((p.f(0x06) * C_PCT) + 1.0) * a)
        a = f32(((p.f(0x3B) * C_PCT) + 1.0) * a)
        d = f32(d + 1.0 * a)
        if is_type(p.u(0x1C2), 0x11) and p.u8(0x0E) == 0:
            f = f32(float(p.i(0xBB)))
            f = f32(p.f(0x5F) * f)
            f = f32(float(p.i(0xCB)) + f)
            saved = d
            d = f32(saved - 1.0 * f)
        d = f32(d - p.f(0xC3) * d)
    if d < 0.0:
        d = 0.0

    d, seed, crit = check_critical(p, k, d, seed)

    bonus = 0
    if p.u8(0x1CF) != 0:
        bonus = trunc_i32(d * C_BONUS_HIT - d)
        p.set_i(0x1D0, bonus)
        d = f32(d * C_BONUS_HIT)

    d = f32((p.f(0x1B) * d) * C_PCT)
    d = f32((float(p.i(0x1D5)) * d) * C_PCT)
    if p.u8(0xD3) != 0:
        d = f32((p.f(0x1F) * C_PCT) * d)
    if p.u8(0xD4) != 0:
        d = f32((p.f(0x23) * C_PCT) * d)
    if p.u8(0xD5) != 0:
        d = f32((p.f(0x27) * C_PCT) * d)
    d = f32(p.f(0x6B) * d)
    if d < 1.0:
        d = 1.0

    if is_type(p.u(0x1C6), 0x111):
        base = 26 if p.u8(0x1CE) != 0 else 0
        for i in range(26):
            d = f32(p.f(0xD6 + 4 * (base + i)) * d)

    if is_type(p.u(0x1BE), 0x11) and p.f(0x77) != 1.0:
        d = f32(p.f(0x77) * d)
    if is_type(p.u(0x1BE), 0x11) and p.i(0x7B) != 0:
        d = f32(float(p.i(0x7B)) + d)
    d = f32(p.f(0x1A6) * d)
    if p.u8(0x1DF) != 0 and is_type(p.u(0x1BE), 0x111):
        t = trunc_i32(d * C_QUARTER)
        d = f32(float(t) + d)
    if p.u8(0x1E0) != 0:
        d = f32(float(p.i(0x1E1)) + d)
    if p.u8(0x1EF) != 0 and is_type(p.u(0x1BE), 0x111):
        d = f32(p.f(0x1F0) * d)
    d = f32(fdiv(p.f(0x1F8) * d, C_HUNDRED) + d)
    d = f32(p.f(0x1F4) + d)
    result = trunc_i32(d)
    p.set_i(0x1E6, result)
    return result, p.b[0x1CE] != 0, bonus


# ---- 随机用例 ----

TYPES = [0x00, 0x01, 0x02, 0x11, 0x111, 0x211, 0x21, 0x81]


def rnd_float(rng):
    roll = rng.random()
    if roll < 0.01:
        return rng.choice([math.nan, math.inf, -math.inf, 3.0e38, -3.0e38])
    if roll < 0.15:
        return 0.0
    if roll < 0.30:
        return 1.0
    if roll < 0.40:
        return -1.0
    if roll < 0.60:
        return f32(rng.uniform(0.0, 3.0))
    if roll < 0.85:
        return f32(rng.uniform(0.0, 120.0))
    return f32(rng.uniform(-60.0, 400.0))


def rnd_int(rng):
    roll = rng.random()
    if roll < 0.02:
        return rng.choice([2147483647, -2147483648, 0x7FFFFF00])
    if roll < 0.35:
        return 0
    if roll < 0.7:
        return rng.randint(0, 200)
    return rng.randint(-100, 3000)


def gen_case(rng):
    p = Blob.with_defaults()
    # 每个浮点/整数字段各自以一定概率改成随机值,其余保持默认,让分支组合多样
    float_offsets = [0x00, 0x06, 0x0F, 0x13, 0x17, 0x1B, 0x1F, 0x23, 0x27, 0x2B, 0x2F, 0x33, 0x37, 0x3B, 0x3F, 0x43,
                     0x47, 0x4B, 0x4F, 0x53, 0x57, 0x5B, 0x5F, 0x6B, 0x6F, 0x73, 0x77, 0x8F, 0x9B, 0xA7, 0xB3, 0xBF,
                     0xC3, 0xCF, 0x1A6, 0x1F0, 0x1F4, 0x1F8]
    int_offsets = [0x63, 0x67, 0x7B, 0x7F, 0x83, 0x87, 0x8B, 0x93, 0x97, 0x9F, 0xA3, 0xB7, 0xBB, 0xC7, 0xCB, 0x1D5, 0x1E1]
    for o in float_offsets:
        if rng.random() < 0.6:
            p.set_f(o, rnd_float(rng))
    for o in int_offsets:
        if rng.random() < 0.6:
            p.set_i(o, rnd_int(rng))
    for o in range(0xD6, 0xD6 + 4 * 52, 4):
        if rng.random() < 0.08:
            p.set_f(o, rnd_float(rng))
    p.b[0x04] = rng.choice([0, 1])
    p.b[0x05] = rng.choice([0, 0, 1, 1, 2, 3])
    for o in range(0x0A, 0x0E):
        p.b[o] = rng.choice([0, 0, 1])
    p.b[0x0E] = rng.choice([0, 0, 0, 1])
    for o in (0xD3, 0xD4, 0xD5, 0x1CF, 0x1DE, 0x1DF, 0x1E0, 0x1E5, 0x1EF, 0x1B5, 0x1B6):
        p.b[o] = rng.choice([0, 1])
    p.b[0x1B2] = rng.choice([0, 3, 4, 5, 9, 14, 14, 255, 7])
    p.b[0x1B3] = rng.choice([1, 10, 50, 60, 70, 100, 200, 0])
    p.b[0x1B4] = rng.choice([1, 20, 60, 90, 130])
    p.b[0x1B7] = rng.choice([0, 10, 50, 60, 100, 200])
    p.b[0x1B8] = rng.choice([0, 0, 1, 5, 250])
    p.b[0x1B9] = rng.choice([0, 1, 2, 3, 250])
    p.b[0x1DD] = rng.choice([0, 1, 2, 3, 4, 4, 5, 200])
    struct.pack_into("<d", p.b, 0x1AA, rng.choice([0.0, 0.0, 1.5, 7.0, -3.5, 1e12, math.nan]))
    for o in (0x1BA, 0x1BE, 0x1C2, 0x1C6):
        struct.pack_into("<I", p.b, o, rng.choice(TYPES))
    struct.pack_into("<I", p.b, 0x1D9, rng.getrandbits(32))

    k = Constants()
    for n in Constants.FIELDS:
        setattr(k, n, f32(rng.choice([0.0, 1.0, 50.0, 100.0, 250.0, 400.0, 0.28, 0.9, rng.uniform(0, 500), -1.0])))
    if rng.random() < 0.9:
        k.vector = [rng.choice([0, 1, 2, 10, 25, 100, 250, -3]) for _ in range(rng.choice([0, 3, 7, 7, 7, 8]))]
    if rng.random() < 0.6:
        k.module_rates[rng.choice([0, 3, 9, 14, -1])] = f32(rng.uniform(0, 2))
    if rng.random() < 0.6:
        for _ in range(rng.randint(1, 3)):
            k.index_rates[rng.choice([0, 1, 2, 3, -6])] = f32(rng.uniform(0, 2))
    if rng.random() < 0.2:
        k.max_total_defense = f32(rng.choice([0.0, 0.5, 1.0, 0.01]))
    if rng.random() < 0.2:
        k.crit_default = f32(rng.choice([0.0, 1.0, 2.0, 1.5]))
    return p, k


def gen_plausible_case(rng):
    """数值落在"像真"的范围里的用例:大多数分支都有实际数值参与,不会被下限/上限(伤害<1、防御率钳位等)吞掉。"""
    p = Blob.with_defaults()
    k = Constants()

    def uf(lo, hi):
        return f32(rng.uniform(lo, hi))

    p.b[0x05] = rng.choice([0, 0, 1, 1, 2])
    p.b[0x04] = rng.choice([0, 1])
    p.set_f(0x00, uf(100, 20000))
    p.set_f(0x06, uf(0, 200))
    p.set_f(0x0F, uf(0, 2000))
    p.set_f(0x13, uf(0, 30))
    p.set_f(0x17, uf(0, 30))
    p.set_f(0x1B, uf(50, 150))
    for o in (0x1F, 0x23, 0x27):
        p.set_f(o, uf(50, 150))
    p.set_f(0x2B, uf(0.2, 2))
    p.set_f(0x2F, uf(0, 500))
    p.set_f(0x33, uf(0, 500))
    p.set_f(0x37, uf(0, 100))
    p.set_f(0x3B, uf(0, 100))
    p.set_f(0x3F, uf(0, 5000))
    p.set_f(0x43, uf(0, 5000))
    p.set_f(0x47, uf(0, 1))
    for o in (0x4B, 0x4F, 0x53, 0x57, 0x5B):
        p.set_f(o, uf(0, 20))
    p.set_f(0x5F, uf(0, 2))
    p.set_i(0x63, rng.randint(0, 50))
    p.set_i(0x67, rng.randint(0, 50))
    p.set_f(0x6B, uf(0.5, 2))
    p.set_f(0x6F, uf(0.5, 2))
    p.set_f(0x73, uf(0, 1))
    p.set_f(0x77, rng.choice([1.0, uf(0.5, 2)]))
    p.set_i(0x7B, rng.choice([0, rng.randint(1, 500)]))
    for i in range(4):
        p.set_i(0x7F + 4 * i, rng.randint(0, 100))
        p.b[0x0A + i] = rng.choice([0, 0, 1])
    p.set_f(0x8F, uf(0.5, 2))
    p.set_i(0x93, rng.randint(0, 20000))
    p.set_i(0x97, rng.randint(0, 100))
    p.set_f(0x9B, uf(0, 50))
    p.set_i(0x9F, rng.randint(0, 20000))
    p.set_i(0xA3, rng.randint(0, 100))
    p.set_f(0xA7, uf(0, 50))
    p.set_f(0xB3, uf(0, 100))
    p.set_i(0xB7, rng.randint(0, 1000))
    p.set_i(0xBB, rng.randint(0, 1000))
    p.set_f(0xBF, uf(0, 0.5))
    p.set_f(0xC3, uf(0, 0.5))
    p.set_i(0xC7, rng.randint(0, 1000))
    p.set_i(0xCB, rng.randint(0, 1000))
    p.set_f(0xCF, uf(0, 10))
    for o in (0xD3, 0xD4, 0xD5, 0x0E, 0x1CF, 0x1DE, 0x1DF, 0x1E0, 0x1E5, 0x1EF, 0x1B5, 0x1B6):
        p.b[o] = rng.choice([0, 1])
    for i in range(52):
        if rng.random() < 0.3:
            p.set_f(0xD6 + 4 * i, uf(0.8, 1.3))
    p.set_f(0x1A6, uf(0.8, 1.3))
    p.set_i(0x1D5, rng.randint(50, 150))
    p.set_i(0x1E1, rng.randint(0, 500))
    p.set_f(0x1F0, uf(0.8, 1.5))
    p.set_f(0x1F4, uf(0, 100))
    p.set_f(0x1F8, uf(0, 50))
    p.b[0x1B2] = rng.choice([0, 3, 3, 4, 5, 9, 14])
    p.b[0x1B3] = rng.randint(1, 100)
    p.b[0x1B4] = rng.randint(1, 100)
    p.b[0x1B7] = rng.randint(1, 100)
    p.b[0x1B8] = rng.choice([0, 0, 1, 3])
    p.b[0x1B9] = rng.choice([0, 1, 2, 3])
    p.b[0x1DD] = rng.choice([0, 1, 2, 3, 4, 4])
    struct.pack_into("<d", p.b, 0x1AA, rng.choice([0.0, 2.0, 15.5]))
    struct.pack_into("<I", p.b, 0x1BA, rng.choice([0x01, 0x01, 0x21, 0x81]))
    struct.pack_into("<I", p.b, 0x1BE, rng.choice([0x111, 0x111, 0x211, 0x81, 0x11]))
    struct.pack_into("<I", p.b, 0x1C2, rng.choice([0x111, 0x211, 0x211, 0x21]))
    struct.pack_into("<I", p.b, 0x1C6, rng.choice([0x111, 0x211]))
    struct.pack_into("<I", p.b, 0x1D9, rng.getrandbits(32))

    k.phys_dmg = uf(100, 2000)
    k.mag_dmg = uf(100, 2000)
    k.phys_reduce = uf(5, 60)
    k.mag_reduce = uf(5, 60)
    k.phys_rand_base = uf(0, 10)
    k.phys_rand_rate = uf(0, 1)
    k.mag_rand_base = uf(0, 10)
    k.mag_rand_rate = uf(0, 1)
    k.elem_base = uf(100, 200)
    k.vector = [rng.randint(0, 300) for _ in range(7)]
    k.vector[4] = rng.randint(1, 300)
    k.vector[6] = rng.randint(1, 10000)
    k.module_rates = {9: uf(0.1, 1.0)}
    k.index_rates = {i: uf(0.1, 1.0) for i in range(rng.randint(0, 4))}
    return p, k


def bits(f):
    return "%08x" % struct.unpack("<I", struct.pack("<f", f))[0]


def format_case(index, p0, p, k, result, crit, bonus):
    consts = ",".join(bits(getattr(k, n)) for n in Constants.FIELDS) + "," + bits(k.max_total_defense) + "," + bits(k.crit_default)
    vec = ",".join(str(v) for v in k.vector) or "-"
    mods = ",".join("%d:%s" % (m, bits(r)) for m, r in sorted(k.module_rates.items())) or "-"
    idxs = ",".join("%d:%s" % (m, bits(r)) for m, r in sorted(k.index_rates.items())) or "-"
    return "case %d %d %d %d %s %s %s %s %s" % (index, result, 1 if crit else 0, bonus, consts, vec, mods, idxs, bytes(p0.b).hex())


def generate(count, seed, out_path):
    rng = random.Random(seed)
    crits = 0
    bonuses = 0
    with open(out_path, "w", encoding="utf-8", newline="\n") as w:
        w.write("# 伤害公式对拍用例(damage_ref.py --gen);列:case 序号 结果 暴击 额外命中量 常量 向量 模块倍率 序号倍率 结构字节\n")
        for i in range(count):
            p, k = gen_plausible_case(rng) if i % 2 == 0 else gen_case(rng)
            p0 = Blob(bytes(p.b))
            result, crit, bonus = compute(p, k)
            crits += 1 if crit else 0
            bonuses += 1 if bonus else 0
            w.write(format_case(i, p0, p, k, result, crit, bonus) + "\n")
    print("已生成 %d 个用例(暴击 %d,额外命中 %d)-> %s" % (count, crits, bonuses, out_path))


def selftest():
    """手算核对的几个简单情形(与 C# 单元测试里的一致)。"""
    k = Constants()
    k.phys_dmg = 100.0
    k.mag_dmg = 100.0
    k.phys_reduce = 20.0
    k.mag_reduce = 20.0
    k.elem_base = 100.0

    def neutral():
        p = Blob.with_defaults()
        p.b[0x05] = 0
        p.set_f(0x00, 1000.0)
        for o in (0x06, 0x37, 0x3B, 0x47):
            p.set_f(o, 0.0)
        p.set_f(0x13, -3.0)
        p.set_f(0x1B, 100.0)
        p.set_i(0x1D5, 100)
        return p

    p = neutral()
    assert compute(p, k)[0] == 1000, "pass-through"
    p = neutral()
    struct.pack_into("<I", p.b, 0x1C2, 0x211)
    p.set_i(0x93, 1000)
    p.set_i(0x97, 0)
    p.b[0x1B3] = 10
    assert compute(p, k)[0] == 166, "defense"
    p = neutral()
    p.set_f(0x13, 10000.0)
    assert compute(p, k)[0] == 1500, "crit"
    print("selftest 通过")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--gen", type=int, help="生成这么多个随机对拍用例")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--out", default="out/damage_cases.txt")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args()
    if args.selftest:
        selftest()
    if args.gen:
        generate(args.gen, args.seed, args.out)
    if not args.selftest and not args.gen:
        ap.print_help()
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
