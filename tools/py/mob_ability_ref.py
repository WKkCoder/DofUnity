#!/usr/bin/env python3
"""怪物数值的参考实现(证据 E-062):读 monster/monster.lst 列出的 .mob 与基础属性表 .tbl,
按服务端函数 setBasicAbilityByLevel 与 upgradeAbilityByMonsterParameterCategory 的语义算出"主动对象静态信息"数值块。

它是 C# 实现(Dnf.Game 的 MonsterDef / MonsterAbility)的独立对照:`--dump` 写出的文件与 `dnf mob-check --dump` 逐行比较,应当完全一致。
两份实现共用的只有"语义规格"(下面的常量表与算法说明);读脚本、解表、浮点落地各自实现。

语义要点(服务端二进制带符号,函数文本只在本机 re/ 里,不入库;依据与地址见 docs/evidence/ledger.md 的 E-062):
  * 数值块是 188 个 32 位槽,整数与浮点按位模式混存;槽号 = 结构字节偏移 / 4。
  * .tbl:整数流,每 24 个一行;文件第 1..24 列依次存到偏移 +0x00 +0x0C +0x10 +0x14 +0x18 +0x04 +0x08 +0x1C ..+0x30,
    其后 4 列 +0x34..+0x40、4 列 +0x44..+0x50,最后 +0x54 +0x58 +0x5C。
  * 选表:[ability table]==1 -> 3;首领 -> 2;有名怪物编号 -> 1;副本参数 2000..2002 -> 4;否则 0(后面的覆盖前面的)。
  * 成对标签随等级插值:cur >= total 取最大;cur <= 0 取最小;否则 最小 + (最大-最小)*cur/total(32 位乘法、向零整除)。
  * (int) 转换一律向零取整(服务端 FISTP 且舍入控制为截断);float 落地用 struct 的 '<f' 舍入。
  * 精度:原版用 x87 扩展精度(64 位有效位),每条 C 语句的中间结果留在 80 位寄存器里,FSTP 才舍入、FISTP 截断。
    这里用 x87.py 的精确模型逐运算复现,与服务端 ELF 在模拟器里跑出的结果逐位一致(tools/py/mob_ability_oracle.py 对拍)。
  * 数值块槽 5 是字节(默认 1,[life max] 写入低字节),其余 3 字节是未初始化的填充,参考实现与 C# 都置 0,对拍时只比低字节。

用法:
  python3 tools/py/mob_ability_ref.py --dump out/mob_ref.tsv        写出与 dnf mob-check --dump 同格式的文件
  然后: diff out/mob_cs.tsv out/mob_ref.tsv  (应无输出)
"""
import argparse
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402
import x87  # noqa: E402
from fractions import Fraction  # noqa: E402

SLOTS = 0xBC

STATUS_TAGS = ["[slow resistance]", "[freeze resistance]", "[poison resistance]", "[stun resistance]", "[curse resistance]",
               "[blind resistance]", "[lightning resistance]", "[stone resistance]", "[sleep resistance]", "[burn resistance]",
               "[weapon break resistance]", "[bleeding resistance]", "[piercing resistance]", "[stuck resistance]",
               "[confuse resistance]", "[hold resistance]", "[armor break resistance]"]
ELEMENT_RES_TAGS = ["[fire resistance]", "[water resistance]", "[dark resistance]", "[light resistance]"]
ELEMENT_ATK_TAGS = ["[fire attack]", "[water attack]", "[dark attack]", "[light attack]"]

CATEGORY_NAMES = ["[ATTACK_SPEED]", "[MOVE_SPEED]", "[PHYSICAL_ATTACK]", "[PHYSICAL_DEFENSE]", "[MAGICAL_ATTACK]",
                  "[MAGICAL_DEFENSE]", "[EQUIPMENT_PHYSICAL_ATTACK]", "[EQUIPMENT_PHYSICAL_DEFENSE]",
                  "[EQUIPMENT_MAGICAL_ATTACK]", "[EQUIPMENT_MAGICAL_DEFENSE]", "[HP MAX]", "[HIT_RECOVERY]", "[STUCK]",
                  "[HP MAX BOSS]"]
# 类别 -> 槽号(服务端跳转表里的字节偏移 / 4)
CATEGORY_SLOT = [0x120 // 4, 0x118 // 4, 0x18 // 4, 0x20 // 4, 0x28 // 4, 0x30 // 4, 0x158 // 4, 0x160 // 4, 0x168 // 4,
                 0x170 // 4, 0x4 // 4, 0x130 // 4, 0xAC // 4, 0x4 // 4]

TABLE_PATHS = ["monster/commonmonsterbaseparameter.tbl", "monster/namedmonsterbaseparameter.tbl",
               "monster/bossmonsterbaseparameter.tbl", "monster/summonmonsterbaseparameter.tbl",
               "monster/warroommonsterbaseparameter.tbl"]

# 文件列(从 0 数)-> 结构偏移 / 4 的顺序,即每个列存到哪个字段
COLUMN_FIELD = [0x00, 0x0C, 0x10, 0x14, 0x18, 0x04, 0x08, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30,
                0x34, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C]


def f32(x):
    """落地成 float32 再取回(与 FSTP float 一致)。"""
    return struct.unpack("<f", struct.pack("<f", x))[0]


def f32bits(x):
    return struct.unpack("<i", struct.pack("<f", x))[0]


def bits_f32(b):
    return struct.unpack("<f", struct.pack("<i", b))[0]


def trunc_int(x):
    """向零取整;溢出或 NaN 得到 -2^31(x87 的"整数不确定值")。"""
    if x != x or x >= 2147483648.0 or x <= -2147483649.0:
        return -2147483648
    return int(x)


def wrap32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


def c_div(a, b):
    """C 的有符号整除(向零取整)。"""
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def resolve(script_path, relative):
    rel = relative.replace("\\", "/")
    if rel.startswith("/"):
        base = rel
    else:
        d = script_path.replace("\\", "/")
        d = d[: d.rfind("/") + 1] if "/" in d else ""
        base = d + rel
    out = []
    for part in base.lower().split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if out:
                out.pop()
            continue
        out.append(part)
    return "/".join(out)


INT_RE = re.compile(r"^-?\d+$")


def parse_script(pvf, strings, path):
    raw = pvf.get(path)
    if raw is None or raw[:2] != b"\xb0\xd0":
        return None
    return S.decode_text(raw, strings)


def ints(vals):
    return [int(v) for v in vals if INT_RE.match(v)]


class Mob:
    def __init__(self, mob_id, path, lines):
        self.id = mob_id
        self.path = path
        self.first = {}
        for tag, vals in lines:
            if tag is not None and tag.lower() not in self.first:
                self.first[tag.lower()] = vals
        self.lines = lines

    def ints(self, tag):
        return ints(self.first.get(tag.lower(), []))

    def pair(self, tag, dmin=0, dmax=0):
        v = self.ints(tag)
        if not v:
            return (dmin, dmax)
        return (v[0], v[1] if len(v) > 1 else v[0])

    def categories(self):
        """[ability category] 的 (类别号, 是否乘, 数值) 列表;不认识的类别名丢弃。"""
        vals = self.first.get("[ability category]")
        out = []
        if vals is None:
            return out
        i = 0
        while i + 2 < len(vals):
            name, op, num = vals[i], vals[i + 1], vals[i + 2]
            if not (name.startswith('"') and op.startswith('"') and INT_RE.match(num)):
                break
            name = name[1:-1]
            op = op[1:-1]
            if name in CATEGORY_NAMES:
                out.append((CATEGORY_NAMES.index(name), op != "+", int(num)))
            i += 3
        return out

    def base_table_paths(self):
        vals = self.first.get("[base table]", [])
        return [v[1:-1] for v in vals if v.startswith('"')]


def parse_table(lines):
    flat = []
    for _tag, vals in lines or []:
        flat.extend(ints(vals))
    rows = []
    for i in range(0, len(flat) - len(flat) % 24, 24):
        rows.append(flat[i:i + 24])
    return rows


def row_fields(row):
    """文件一行 -> {结构偏移: 值}。"""
    return {COLUMN_FIELD[k]: row[k] for k in range(24)}


def is_named(mob_id):
    return 50000 <= mob_id <= 51000 or 56000 <= mob_id <= 60000


def select_table(mob, dungeon_param, kind):
    table_ix = 0
    ability_table = (mob.ints("[ability table]") or [0])[0]
    if ability_table == 1:
        table_ix = 3
    if kind == 3:
        table_ix = 2
    if is_named(mob.id):
        table_ix = 1
    if 1999 < dungeon_param < 2003:
        table_ix = 4
    return table_ix


def lerp(a, b, cur, total):
    if cur >= total:
        return b
    if cur <= 0:
        return a
    return wrap32(c_div(wrap32((b - a) * cur), total) + a)


class Stats:
    def __init__(self):
        r = [0] * SLOTS
        self.r = r
        one = f32bits(1.0)
        for i in (2, 4, 7, 9, 0xB, 0xD, 0x41, 0x43, 0x45, 0x47, 0x49, 0x4B, 0x4D, 0x4F, 0x51, 0x53, 0x55, 0x57, 0x59, 0x5B, 0x5D,
                  0x6E, 0x73, 0x76, 0xB7, 0xB8, 0xBB):
            r[i] = one
        for i in range(4):
            r[0x12 + i] = one
            r[0x1A + i] = one
        for i in range(0x11):
            r[0x2F + i] = one
        r[5] = 1
        r[0x46] = 1000
        r[0x48] = 1000
        r[0x4A] = 1000
        r[0x52] = 100

    def hp_max(self):
        return x87.trunc_i32(x87.mul(x87.from_int(self.r[1]), x87.from_f32_bits(self.r[2])))

    def mp_max(self):
        return x87.trunc_i32(x87.mul(x87.from_int(self.r[3]), x87.from_f32_bits(self.r[4])))


def basic_by_level(mob, tables, own_rows, level, dungeon_param, kind):
    s = Stats()
    r = s.r
    r[0] = level
    lmin, lmax = mob.pair("[level]")
    cur = level - lmin
    total = lmax - lmin
    t = select_table(mob, dungeon_param, kind)
    key = dungeon_param if t == 4 else level
    idx = -1
    for i, row in enumerate(tables[t]):
        if row[0] == key:
            idx = i
            break
    if idx < 0:
        return s
    row = tables[t][idx]
    if own_rows and idx < len(own_rows):
        row = own_rows[idx]
    f = row_fields(row)
    r[1] = f[0x04]
    r[3] = f[0x08]
    r[6] = f[0x0C]
    r[8] = f[0x10]
    r[10] = f[0x14]
    r[12] = f[0x18]
    r[0x56] = f[0x1C]
    r[0x58] = f[0x20]
    r[0x5A] = f[0x24]
    r[0x5C] = f[0x28]
    life_max = mob.ints("[life max]")
    r[5] = (life_max[0] if life_max else 1) & 0xFF   # 脚本字节 +0x3b8(默认 1)落到数值块槽 5 的低字节;其余 3 字节原版未初始化,这里置 0
    for i, tag in enumerate(ELEMENT_RES_TAGS):
        a, b = mob.pair(tag)
        r[0xE + i] = lerp(a, b, cur, total)
    for i, tag in enumerate(STATUS_TAGS):
        a, b = mob.pair(tag)
        r[0x1E + i] = lerp(a, b, cur, total)
    for slot, tag in ((0x46, "[move speed]"), (0x48, "[attack speed]"), (0x4A, "[cast speed]"), (0x4C, "[hit recovery]"),
                      (0x50, "[jump power]"), (0x52, "[jump speed]"), (0x54, "[weight]")):
        default = (100, 100) if tag == "[jump speed]" else (0, 0)
        a, b = mob.pair(tag, *default)
        r[slot] = lerp(a, b, cur, total)
    a, b = mob.pair("[HP regen speed]")
    hp_speed = lerp(a, b, cur, total)
    a, b = mob.pair("[MP regen speed]")
    mp_speed = lerp(a, b, cur, total)
    hp_rate = x87.to_f32(x87.div(x87.from_int(hp_speed), Fraction(1000)))
    mp_rate = x87.to_f32(x87.div(x87.from_int(mp_speed), Fraction(1000)))
    r[0x42] = x87.trunc_i32(x87.mul(x87.from_int(s.hp_max()), hp_rate))
    r[0x44] = x87.trunc_i32(x87.mul(x87.from_int(s.mp_max()), mp_rate))
    r[0x6A] = x87.f32_bits(x87.to_f32(x87.add(x87.from_f32_bits(r[0x6A]), x87.div(x87.from_int(f[0x2C]), Fraction(100)))))
    r[0x2B] = wrap32(r[0x2B] + x87.trunc_i32(x87.div(x87.from_int(f[0x30]), Fraction(10))))
    for i, tag in enumerate(ELEMENT_ATK_TAGS):
        a, b = mob.pair(tag)
        r[0x16 + i] = lerp(a, b, cur, total)
        r[0xE + i] = wrap32(r[0xE + i] + f[0x34 + 4 * i])
        r[0x16 + i] = wrap32(r[0x16 + i] + f[0x44 + 4 * i])
    r[0x4C] = wrap32(r[0x4C] + f[0x54])
    a, b = mob.pair("[rigidity]")
    r[0x4E] = lerp(a, b, cur, total)
    r[0x4E] = wrap32(r[0x4E] + f[0x58])
    for i in range(0x11):
        if i != 0xD:
            r[0x1E + i] = wrap32(r[0x1E + i] + f[0x5C])
    return s


def apply_categories(s, entries, boss):
    has_boss_hp = any(c == 13 for c, _m, _v in entries)
    for c, mul, value in entries:
        if has_boss_hp and boss and c == 10:
            continue
        if has_boss_hp and not boss and c == 13:
            continue
        slot = CATEGORY_SLOT[c]
        if mul:
            as_float = x87.to_f32(x87.from_int(s.r[slot]))
            s.r[slot] = x87.trunc_i32(x87.mul(x87.div(x87.from_int(value), Fraction(100)), as_float))
        else:
            s.r[slot] = wrap32(s.r[slot] + c_div(value, 100))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", help="写出每个怪物在最小/中间/最大等级、普通/首领种类下的数值块(含原版派生数值,放 out/,不入库)")
    args = ap.parse_args()

    pvf = D.Pvf()
    strings = S.Strings(pvf)

    list_lines = parse_script(pvf, strings, "monster/monster.lst")
    mob_list = {}
    for _tag, vals in list_lines:
        i = 0
        while i + 1 < len(vals):
            if INT_RE.match(vals[i]) and vals[i + 1].startswith('"'):
                mob_list.setdefault(int(vals[i]), vals[i + 1][1:-1])
                i += 2
            else:
                i += 1
    print("monster.lst:", len(mob_list), "项")

    tables = [parse_table(parse_script(pvf, strings, p)) for p in TABLE_PATHS]
    print("基础属性表行数:", [len(t) for t in tables])

    out_lines = []
    parsed = 0
    for mob_id in sorted(mob_list):
        path = resolve("monster/monster.lst", mob_list[mob_id])
        lines = parse_script(pvf, strings, path)
        if lines is None:
            print("读不出", path)
            continue
        parsed += 1
        mob = Mob(mob_id, path, lines)
        own = {}
        for rel in mob.base_table_paths():
            own_lines = parse_script(pvf, strings, resolve("monster/monster.lst", rel))
            if own_lines is None:
                continue
            for i, row in enumerate(parse_table(own_lines)):
                own[i] = row
        own_rows = [own[i] for i in range(len(own))] if own else None
        entries = mob.categories()
        lmin, lmax = mob.pair("[level]")
        levels = [lmin, (lmin + lmax) // 2 if (lmin + lmax) >= 0 else -((-(lmin + lmax)) // 2), lmax]
        for kind in (0, 3):
            for li, level in enumerate(levels):
                if li == 1 and levels[1] == levels[0]:
                    continue
                s = basic_by_level(mob, tables, own_rows, level, 0, kind)
                apply_categories(s, entries, kind == 3)
                out_lines.append("%d\t%d\t%d\t%s" % (mob_id, level, kind, ",".join(str(x) for x in s.r)))
    print("已解析", parsed, "个 .mob;输出", len(out_lines), "行")
    if args.dump:
        os.makedirs(os.path.dirname(os.path.abspath(args.dump)), exist_ok=True)
        with open(args.dump, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(out_lines) + "\n")
        print("已写出", args.dump)


if __name__ == "__main__":
    main()
