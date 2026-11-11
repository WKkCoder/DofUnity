#!/usr/bin/env python3
"""分解链的第二份独立实现(Python,精确分数),与 C# 的 DisjointRules.Compute / LegacyDisjointScript.Compute / ExactFloat 对拍。
输入全是合成的(物品 9xx、系数是这里自己写的小数),不含原版数值。算式依据:静态读码 get_disjoint_result_ 0x085d234a 与 DisJoint::GetResult 0x084733f6。
x87 的 FILD/FMUL/FDIV/FISTP(向零)用 Fraction 精确复现;先存回单精度的几步(f32)与服务端一致。
用法:
  python3 tools/py/disjoint_ref.py                 打印所有场景的期望值(对照 C# 测试里的黄金值)
  python3 tools/py/disjoint_ref.py --random N      另做 N 个随机场景并输出 JSON 行
  python3 tools/py/disjoint_ref.py --csharp N      输出 N 个场景的 C# MemberData 行(贴进 DisjointRulesTests 的黄金表,种子固定)
  python3 tools/py/disjoint_ref.py --exact         输出 ExactFloat 测试里那批单步浮点运算的期望值
"""
import json
import random
import struct
import sys
from fractions import Fraction


def f32(x):
    """把有理数或浮点数舍入成单精度,返回精确的 Fraction。"""
    if isinstance(x, Fraction):
        x = float(x)
    return Fraction(struct.unpack('<f', struct.pack('<f', x))[0])


def bits32(b):
    """按位模式给出单精度数(用来写非规格数)。"""
    return struct.unpack('<f', struct.pack('<I', b))[0]


def trunc(fr):
    """x87 FISTP(舍入控制 = 向零)。超出 32 位给整数不定值 −2^31。"""
    v = int(Fraction(fr))
    if v < -2 ** 31 or v > 2 ** 31 - 1:
        return -2 ** 31
    return v


def cdiv(a, b):
    """C 的整数除法(向零)。"""
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def i32(x):
    x &= 0xFFFFFFFF
    return x - 2 ** 32 if x >= 2 ** 31 else x


F11 = f32(1.1)
F1000 = Fraction(1000)


def main_count(value, rate, base, factor):
    p1 = trunc(Fraction(value) * F11)
    sell = cdiv(i32(rate * p1), 1000)
    basis = cdiv(i32(sell * 1000), base)
    cnt = trunc(Fraction(basis) * f32(factor) / F1000)
    return max(1, cnt)


def pick(group, roll, grade):
    """get_additional_item:逐项抄进结果,范围内的比较权重,一项也没选中停在最后一项;空组 None。"""
    chosen = None
    for e in group:
        chosen = e
        if e['min'] <= grade <= e['max']:
            if roll <= e['weight']:
                return e
            roll -= e['weight']
    return chosen


def extra_count(grade, divisor):
    q = f32(Fraction(grade) / f32(divisor))
    return max(1, trunc(q))


def compute(script, etc, item, ctx, rolls):
    """script: dict(base, rows{(grade,rarity,unident)->row}, self{chance,item,count}); rolls: 依次给出 rand 的返回值(列表,会被消耗)。"""
    key = (ctx['grade'] & 255, item['rarity'] & 255, 1 if not item['identified'] else 0)
    row = script['rows'].get(key)
    if row is None:
        return {'error': 0xd3 if not item['identified'] else 0x11}
    if script['base'] == 0:
        return {'error': 0x13}
    items = [0] * 5
    counts = [0] * 5
    items[0] = row['item']
    counts[0] = main_count(item['value'], ctx['rates'][2 if item['random'] else 0], script['base'], row['factor'])
    big = row['pct'] * 100
    if ctx['mine']:
        big += etc.get((ctx['grade'] + 1) & 255, 0) * 100
        if ctx['active']:
            bonus = f32(100 * f32(ctx['bonus']))
            big = trunc(Fraction(big) + bonus)
    r1 = rolls.pop(0)
    group = row['big'] if r1 < big else row['add']
    r2 = rolls.pop(0)
    chosen = pick(group, r2, item['grade'])
    if chosen is not None and chosen['item'] != -1:
        items[1] = chosen['item']
        counts[1] = extra_count(item['grade'], chosen['div'])
    if not ctx['mine']:
        return {'items': items, 'counts': counts}
    if item['rarity'] >= 2:
        r3 = rolls.pop(0)
        if r3 < script['self']['chance']:
            counts[2] = script['self']['count']
            items[2] = script['self']['item']
    if ctx['active']:
        f = f32(Fraction(f32(ctx['variation'])) / f32(100))
        for i in range(5):
            counts[i] += trunc(Fraction(counts[i]) * f)
    return {'items': items, 'counts': counts}


def E(item, weight, div, mn=1, mx=50):
    return {'item': item, 'weight': weight, 'div': div, 'min': mn, 'max': mx}


ADD = {
    1: [E(951, 6000, 10.0), E(952, 4000, 5.0)],
    2: [E(953, 10000, 2.0)],
    3: [E(954, 5000, 4.0, 10, 20), E(955, 5000, 8.0, 30, 40)],
    4: [E(950, 5000, 2.0), E(951, 5000, 2.0)],
}
BIG = {
    1: [E(960, 10000, 2.0)],
    2: [E(961, 3000, 1.0), E(962, 7000, 4.0)],
    4: [E(951, 10000, 2.0)],
}


def row(item, factor, add, big, pct):
    return {'item': item, 'factor': factor, 'add': ADD.get(add, []) if add else [], 'big': BIG.get(big, []) if big else [], 'pct': pct}


SCRIPT = {
    'base': 100,
    'rows': {
        (0, 1, 0): row(950, 1.5, 1, 1, 10),
        (0, 2, 0): row(950, 2.0, 2, 2, 20),
        (0, 3, 0): row(950, 2.5, 3, 0, 0),
        (0, 4, 0): row(950, 1.0, 4, 4, 0),
        (1, 1, 0): row(950, 3.0, 1, 1, 30),
        (0, 1, 1): row(949, 0.5, 0, 0, 0),
    },
    'self': {'chance': 10, 'item': 970, 'count': 2},
}
ETC = {1: 0, 2: 5, 3: 10}
NEUTRAL = (1000, 1000, 1000)


def item(value=1000, grade=20, rarity=1, identified=True, random_option=False):
    return {'value': value, 'grade': grade, 'rarity': rarity, 'identified': identified, 'random': random_option}


def ctx(grade=0, mine=False, active=False, bonus=0.0, variation=0.0, rates=NEUTRAL):
    return {'grade': grade, 'mine': mine, 'active': active, 'bonus': bonus, 'variation': variation, 'rates': rates}


SCENARIOS = [
    # 名字, 物品, 环境, 随机数序列
    ('S1 大成功(roll 999 < 1000)', item(1000, 20, 1), ctx(), [999, 5000]),
    ('S1b 普通组,roll 5000', item(1000, 20, 1), ctx(), [1000, 5000]),
    ('S1c 普通组边界,roll 6000 仍选第一项', item(1000, 20, 1), ctx(), [1000, 6000]),
    ('S1d 普通组边界,roll 6001 选第二项', item(1000, 20, 1), ctx(), [1000, 6001]),
    ('S1e 普通组最大 roll 9999', item(1000, 20, 1), ctx(), [1000, 9999]),
    ('S2 自用,稀有度 2,自用额外产物命中(roll 9)', item(5000, 30, 2), ctx(mine=True), [1999, 3000, 9]),
    ('S2b 自用,稀有度 2,自用额外产物不中(roll 10)', item(5000, 30, 2), ctx(mine=True), [1999, 3000, 10]),
    ('S2c 自用,大成功边界 2000 不中', item(5000, 30, 2), ctx(mine=True), [2000, 3000, 99]),
    ('S3 自用+穿戴大成功加成 12.5 + 机器档加成,变化 10%', item(1000, 20, 1), ctx(grade=1, mine=True, active=True, bonus=12.5, variation=10.0), [4749, 5000]),
    ('S3b 同上,roll 4750 不中大成功', item(1000, 20, 1), ctx(grade=1, mine=True, active=True, bonus=12.5, variation=10.0), [4750, 5000]),
    ('S3c 变化 70% (0.7f 偏小)', item(1000, 20, 1), ctx(grade=0, mine=True, active=True, bonus=0.0, variation=70.0), [1000, 5000]),
    ('S4 别人的机器:穿戴、变化、机器档加成都不算', item(1000, 20, 1), ctx(grade=1, mine=False, active=True, bonus=50.0, variation=70.0), [2999, 5000]),
    ('S5 未鉴定行', item(1000, 20, 1, identified=False), ctx(), [0, 0]),
    ('S5b 行缺失(稀有度 5)', item(1000, 20, 5), ctx(), []),
    ('S5c 未鉴定且行缺失(稀有度 5)', item(1000, 20, 5, identified=False), ctx(), []),
    ('S6 随机选项取下标 2 的价格倍率', item(1000, 20, 1, random_option=True), ctx(rates=(800, 900, 700)), [1000, 5000]),
    ('S6b 非随机选项取下标 0', item(1000, 20, 1), ctx(rates=(800, 900, 700)), [1000, 5000]),
    ('S7 主产物个数不足 1 → 1', item(1, 20, 1), ctx(), [1000, 5000]),
    ('S7b 附加个数不足 1 → 1', item(1000, 2, 1), ctx(), [1000, 5000]),
    ('S8 等级不在任何范围:停在最后一项(组 3,等级 5)', item(1000, 5, 3), ctx(), [1000, 1]),
    ('S8b 组 3,等级 35:范围内的第二项,roll 7000 超权重也停在最后一项', item(1000, 35, 3), ctx(), [1000, 7000]),
    ('S8c 组 3,等级 15:第一项,roll 5000', item(1000, 15, 3), ctx(), [1000, 5000]),
    ('S8d 组 3,等级 15:第一项权重不够,第二项不在等级范围,停在最后一项', item(1000, 15, 3), ctx(), [1000, 5001]),
    ('S9 自用,稀有度 1:不抽自用额外产物', item(1000, 20, 1), ctx(mine=True), [1000, 5000]),
    ('S10 变化 −50%(负)', item(1000, 20, 1), ctx(mine=True, active=True, variation=-50.0), [1000, 5000]),
]


def run_all():
    for name, it, cx, rolls in SCENARIOS:
        r = compute(SCRIPT, ETC, it, cx, list(rolls))
        print(name, '→', r)


def predict(script, it, cx):
    out = []
    if cx['mine'] and it['rarity'] > 1:
        out.append(script['self']['item'])
    key = (cx['grade'] & 255, it['rarity'] & 255, 1 if not it['identified'] else 0)
    r = script['rows'].get(key)
    if r:
        out.append(r['item'])
        for lst in (r['add'], r['big']):
            for e in lst:
                if e['item'] not in out:
                    out.append(e['item'])
    return out


def run_random(n, seed=20261110):
    rnd = random.Random(seed)
    lines = []
    for _ in range(n):
        value = rnd.choice([1, 7, 99, 100, 999, 1000, 4321, 54321, 1234567, 20000000, 99999999])
        grade = rnd.randint(1, 60)
        rarity = rnd.choice([1, 1, 2, 3, 4])
        mine = rnd.random() < 0.6
        active = mine and rnd.random() < 0.7
        g = rnd.choice([0, 0, 1])
        bonus = rnd.choice([0.0, 1.0, 12.5, 33.3, 99.9])
        variation = rnd.choice([0.0, 10.0, 30.0, 70.0, -20.0, 133.3])
        random_option = rnd.random() < 0.3
        rates = rnd.choice([NEUTRAL, (800, 900, 700), (1234, 1, 987)])
        rolls = [rnd.randint(0, 9999), rnd.randint(0, 9999), rnd.randint(0, 99)]
        it, cx = item(value, grade, rarity, True, random_option), ctx(g, mine, active, bonus, variation, rates)
        res = compute(SCRIPT, ETC, it, cx, list(rolls))
        lines.append(json.dumps({'item': it, 'ctx': cx, 'rolls': rolls, 'result': res}))
    return lines


def emit_csharp(n, seed=20261110):
    """把随机场景和 Python 算出的期望输出成 C# 的 MemberData 行(贴进 DisjointRulesTests 的黄金表)。"""
    rnd = random.Random(seed)
    rate_sets = [NEUTRAL, (800, 900, 700), (1234, 1, 987)]
    out = []
    for _ in range(n):
        value = rnd.choice([1, 7, 99, 100, 999, 1000, 4321, 54321, 1234567, 20000000, 99999999])
        grade = rnd.randint(1, 60)
        rarity = rnd.choice([1, 1, 2, 3, 4])
        mine = rnd.random() < 0.6
        active = mine and rnd.random() < 0.7
        g = rnd.choice([0, 0, 1])
        bonus = rnd.choice([0.0, 1.0, 12.5, 33.3, 99.9])
        variation = rnd.choice([0.0, 10.0, 30.0, 70.0, -20.0, 133.3])
        random_option = rnd.random() < 0.3
        ri = rnd.randrange(3)
        rolls = [rnd.randint(0, 9999), rnd.randint(0, 9999), rnd.randint(0, 99)]
        it, cx = item(value, grade, rarity, True, random_option), ctx(g, mine, active, bonus, variation, rate_sets[ri])
        res = compute(SCRIPT, ETC, it, cx, list(rolls))
        if 'error' in res:
            exp = '0, "", ""'
            err = res['error']
        else:
            err = 0
            exp = '0, "%s", "%s"' % (','.join(map(str, res['items'])), ','.join(map(str, res['counts'])))
        out.append('        new object[] { %d, %d, %d, %s, %d, %s, %s, %sf, %sf, %d, %d, %d, %d, %d, "%s", "%s" },' % (
            value, grade, rarity, 'true' if random_option else 'false', g, 'true' if mine else 'false', 'true' if active else 'false',
            repr(bonus), repr(variation), ri, rolls[0], rolls[1], rolls[2], err,
            '' if err else ','.join(map(str, res['items'])), '' if err else ','.join(map(str, res['counts']))))
    return out


def exact_cases():
    """ExactFloatTests 的黄金值:x87 单操作数乘/除、向零取整、整数不定值,全用 Fraction 精确算。"""
    cases = [
        ('mul', 1000, 1.1, None), ('mul', 10, 0.7, None), ('mul', -10, 0.7, None), ('mul', 30, 0.1, None), ('mul', 20, 0.1, None),
        ('mul', 100, 0.3, None), ('mul', 7, 1.5, None), ('mul', 2147483647, 2.0, None), ('mul', -2147483648, 1.0, None), ('mul', 3, -0.5, None),
        ('mul', 1000000, 1.1, None), ('mul', 99999999, 1.1, None), ('mul', 20000000, 1.1, None),
        ('muldiv', 11000, 1.5, 1000.0), ('muldiv', 55000, 2.0, 1000.0), ('muldiv', 7700, 1.5, 1000.0), ('muldiv', 10, 1.5, 1000.0),
        ('muldiv', 2147483647, 3.5, 1000.0), ('muldiv', 1000, 1.0, 3.0), ('muldiv', 1, 1.0, 0.0), ('muldiv', 99999, 0.1, 1000.0), ('muldiv', 1234567, 2.5, 0.3),
        ('add', 3500, 1250.0, None), ('add', 100, -0.5, None), ('add', -100, 0.5, None), ('add', 1000, 0.99999994, None), ('add', 2147483647, 1.0, None),
        ('add', 0, 33.3 * 100, None), ('add', 1500, 99.9 * 100, None),
        # 单精度指数为正的加法(≥ 2^23 的浮点数),与上限附近的装得下 / 装不下
        ('add', 5, 16777216.0, None), ('add', -5, -16777216.0, None), ('add', 1, 1.0e9, None), ('add', -1, -1.0e9, None),
        ('add', 1147483647, 1.0e9, None), ('add', 1147483648, 1.0e9, None),
        # 非规格数:最大的非规格数 ÷ 最小的规格数 = 8388607 / 8388608(指数差一位比值就变成约 1/2)
        ('muldiv', 1000000, bits32(0x007FFFFF), bits32(0x00800000)), ('muldiv', -1000000, bits32(0x007FFFFF), bits32(0x00800000)),
        ('muldiv', 1000000, bits32(0x00800000), bits32(0x00800000)),
    ]
    out = []
    for kind, a, f, d in cases:
        ff = f32(f)
        if kind == 'mul':
            r = trunc(Fraction(a) * ff)
        elif kind == 'muldiv':
            dd = f32(d)
            r = -2 ** 31 if dd == 0 else trunc(Fraction(a) * ff / dd)
        else:
            r = trunc(Fraction(a) + ff)
        out.append('%s %d %r %r -> %d' % (kind, a, f, d, r))
    return out


if __name__ == '__main__':
    if len(sys.argv) >= 2 and sys.argv[1] == '--exact':
        for line in exact_cases():
            print(line)
    elif len(sys.argv) >= 3 and sys.argv[1] == '--csharp':
        for line in emit_csharp(int(sys.argv[2])):
            print(line)
    elif len(sys.argv) >= 3 and sys.argv[1] == '--random':
        for line in run_random(int(sys.argv[2])):
            print(line)
    else:
        run_all()
        print('predict mine r2:', predict(SCRIPT, item(rarity=2), ctx(mine=True)))
        print('predict other r2:', predict(SCRIPT, item(rarity=2), ctx()))
        print('predict mine r1:', predict(SCRIPT, item(rarity=1), ctx(mine=True)))
        print('predict mine r5 (no row):', predict(SCRIPT, item(rarity=5), ctx(mine=True)))
        print('predict other r5 (no row):', predict(SCRIPT, item(rarity=5), ctx()))
        print('predict r4 dedupe:', predict(SCRIPT, item(rarity=4), ctx()))
