#!/usr/bin/env python3
"""怪物数值的"原版机器码对拍":把服务端 ELF 里的 setBasicAbilityByLevel 与 upgradeAbilityByMonsterParameterCategory
放进 Unicorn 里真的跑一遍(见 elf_emu.py),结果与 C#/Python 实现逐槽比较(证据 E-062;非 REA:本机模拟器测量)。

做了什么:
  1. 用 ELF 里的 STMonsterScript::Clear() 得到默认值镜像(模拟时跳过它里面所有 CALL,只取内联的默认值写入)。
  2. 对全库每个 .mob:把 [level]、各种成对标签、[ability category]、[base table] 等按服务端的结构偏移写进模拟内存,
     把五张基础属性表按 vector<vector<MonsterBaseParameter>> 的布局写到全局 0x094ff278。
  3. 对 (怪物, 等级, 种类) 调用原版函数,读回 188 个 32 位槽,与 `dnf mob-check --dump` 的输出(out/mob_cs.tsv)比较。
  4. --stress N:另外随机生成 N 个合成怪物(随机等级区间、随机类别运算符与数值、随机自带表),
     比较原版函数与 Python 参考实现,专门压浮点舍入边界。
  5. --golden N:用全合成的基础属性表与 N 条合成用例写金标准文件(可入库,C# 单元测试用)。
  6. --write-cases FILE:把这些合成用例连同原版函数的输出写成文本(格式见 `dnf mob-check --cases` 的说明),
     交给 C# 的 MonsterAbility 重算对拍。

边界:只运行计算函数,不启动服务,不联网;输出里不含原版的数值,只有不一致的个数、槽号与比较结果。
用法:
  re/venv/bin/python tools/py/mob_ability_oracle.py                     全库对拍(需要先生成 out/mob_cs.tsv)
  re/venv/bin/python tools/py/mob_ability_oracle.py --stress 20000      再加随机压力用例
  re/venv/bin/python tools/py/mob_ability_oracle.py --skip-real --stress 30000 --write-cases out/mob_cases.txt
  dotnet run --project src/Dnf.Cli -c Release --no-build -- mob-check --cases out/mob_cases.txt
"""
import argparse
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import elf_emu  # noqa: E402
import mob_ability_ref as M  # noqa: E402
import script_dump as S  # noqa: E402
from unicorn import UC_HOOK_CODE  # noqa: E402
from unicorn.x86_const import UC_X86_REG_EIP  # noqa: E402

# 服务端函数
CLEAR_ADDR = 0x08A398AA
CLEAR_SIZE = 3214
SET_BASIC = 0x088B9603
UPGRADE_CATEGORY = 0x088B7F36
GLOBAL_TABLES = 0x094FF278   # std::vector<std::vector<MonsterBaseParameter>>

SCRIPT_SIZE = 0x4000
INFO_SIZE = 0x400

# STMonsterScript 的结构偏移(来自服务端 .mob 加载函数的读取目标,见 docs/evidence/ledger.md E-062)
OFF_ID = 0x0
OFF_CATEGORIES = 0x7C
OFF_OWN_TABLE = 0x12C
OFF_LIFE_MAX = 0x3B8
OFF_ABILITY_TABLE = 0x3BC
OFF_LEVEL = 0x3EC
OFF_ELEMENT_RES = 0x3F4
OFF_STATUS_RES = 0x414
PAIR_OFFSETS = {
    "[HP regen speed]": 0x49C, "[MP regen speed]": 0x4A4, "[move speed]": 0x4AC, "[attack speed]": 0x4B4, "[cast speed]": 0x4BC,
    "[hit recovery]": 0x4C4, "[jump power]": 0x4CC, "[jump speed]": 0x4D4, "[weight]": 0x4EC, "[rigidity]": 0x528,
}
OFF_ELEMENT_ATK = 0x508


def pack_row(row):
    """文件里的 24 列 -> 96 字节的 MonsterBaseParameter(列按 COLUMN_FIELD 落到各自的结构偏移)。"""
    buf = bytearray(96)
    for k in range(24):
        struct.pack_into("<i", buf, M.COLUMN_FIELD[k], row[k])
    return bytes(buf)


def write_vector(emu, addr, buf, capacity, data):
    """在 addr 处写 std::vector 的三个指针(begin,end,cap);元素内容 data 放进预先分配的缓冲 buf(重复使用,避免耗尽模拟堆)。"""
    if not data:
        emu.write(addr, b"\x00" * 12)
        return
    if len(data) > capacity:
        raise ValueError("向量内容超过缓冲容量")
    emu.write(buf, data)
    emu.write(addr, struct.pack("<III", buf, buf + len(data), buf + len(data)))


class Oracle:
    def __init__(self):
        self.emu = elf_emu.ElfEmu()
        self.script = self.emu.alloc(SCRIPT_SIZE)
        self.info = self.emu.alloc(INFO_SIZE)
        self.cat_cap = 12 * 128
        self.own_cap = 96 * 256
        self.cat_buf = self.emu.alloc(self.cat_cap)
        self.own_buf = self.emu.alloc(self.own_cap)
        self.defaults = self._harvest_defaults()
        self.table_row_counts = None

    def _harvest_defaults(self):
        emu = self.emu
        skipped = []

        def hook(uc, address, size, user):
            if uc.mem_read(address, 1)[0] == 0xE8:
                skipped.append(address)
                uc.reg_write(UC_X86_REG_EIP, address + 5)

        h = emu.uc.hook_add(UC_HOOK_CODE, hook, None, CLEAR_ADDR, CLEAR_ADDR + CLEAR_SIZE)
        scratch = emu.alloc(SCRIPT_SIZE)
        emu.write(scratch, b"\x00" * SCRIPT_SIZE)
        emu.call(CLEAR_ADDR, [scratch])
        emu.uc.hook_del(h)
        return emu.read(scratch, SCRIPT_SIZE)

    def load_tables(self, tables):
        """五张全局基础属性表(每行的第一列是等级)。"""
        emu = self.emu
        inner = bytearray()
        for rows in tables:
            data = b"".join(pack_row(r) for r in rows)
            if data:
                arr = emu.alloc(len(data) + 16)
                emu.write(arr, data)
                inner += struct.pack("<III", arr, arr + len(data), arr + len(data))
            else:
                inner += b"\x00" * 12
        outer = emu.alloc(len(inner) + 16)
        emu.write(outer, bytes(inner))
        emu.write(GLOBAL_TABLES, struct.pack("<III", outer, outer + len(inner), outer + len(inner)))

    def fill_script(self, mob, own_rows):
        emu = self.emu
        s = self.script
        emu.write(s, self.defaults)
        emu.write_u32(s + OFF_ID, mob.id)
        entries = mob.categories()
        data = b"".join(struct.pack("<iii", c, 1 if mul else 0, v) for c, mul, v in entries)
        write_vector(emu, s + OFF_CATEGORIES, self.cat_buf, self.cat_cap, data)
        write_vector(emu, s + OFF_OWN_TABLE, self.own_buf, self.own_cap, b"".join(pack_row(r) for r in own_rows) if own_rows else b"")
        life = mob.ints("[life max]")
        if life:
            emu.write(s + OFF_LIFE_MAX, bytes([life[0] & 0xFF]))
        at = mob.ints("[ability table]")
        if at:
            emu.write_u32(s + OFF_ABILITY_TABLE, at[0])
        for tag, off in PAIR_OFFSETS.items():
            if tag.lower() in mob.first and mob.ints(tag):
                a, b = mob.pair(tag)
                emu.write_i32s(s + off, [a, b])
        if mob.ints("[level]"):
            a, b = mob.pair("[level]")
            emu.write_i32s(s + OFF_LEVEL, [a, b])
        for i, tag in enumerate(M.ELEMENT_RES_TAGS):
            if mob.ints(tag):
                emu.write_i32s(s + OFF_ELEMENT_RES + 8 * i, list(mob.pair(tag)))
        for i, tag in enumerate(M.STATUS_TAGS):
            if mob.ints(tag):
                emu.write_i32s(s + OFF_STATUS_RES + 8 * i, list(mob.pair(tag)))
        for i, tag in enumerate(M.ELEMENT_ATK_TAGS):
            if mob.ints(tag):
                emu.write_i32s(s + OFF_ELEMENT_ATK + 8 * i, list(mob.pair(tag)))

    def run(self, level, dungeon_param, kind):
        emu = self.emu
        emu.write(self.info, b"\xff" * INFO_SIZE)  # 先填脏,确认 Clear() 把该清的都清了
        emu.call(SET_BASIC, [self.script, self.info, level, dungeon_param, kind, 0, 0])
        emu.call(UPGRADE_CATEGORY, [self.script + OFF_CATEGORIES, self.info, 1 if kind == 3 else 0])
        return emu.read_i32s(self.info, M.SLOTS)


def load_all(pvf, strings):
    lst = M.parse_script(pvf, strings, "monster/monster.lst")
    mob_list = {}
    for _t, vals in lst:
        i = 0
        while i + 1 < len(vals):
            if M.INT_RE.match(vals[i]) and vals[i + 1].startswith('"'):
                mob_list.setdefault(int(vals[i]), vals[i + 1][1:-1])
                i += 2
            else:
                i += 1
    tables = [M.parse_table(M.parse_script(pvf, strings, p)) for p in M.TABLE_PATHS]
    return mob_list, tables


def compare(tag_name, got, want, hist, shown, limit=12):
    # 槽 5 是字节字段(默认 1,[life max] 写入低字节),其余 3 个字节是原版未初始化的填充,只比低字节
    got = list(got)
    want = list(want)
    got[5] &= 0xFF
    want[5] &= 0xFF
    if got == want:
        return True
    diffs = [(i, g, w) for i, (g, w) in enumerate(zip(got, want)) if g != w]
    for i, _g, _w in diffs:
        hist[i] = hist.get(i, 0) + 1
    if shown[0] < limit:
        shown[0] += 1
        print("  不一致", tag_name, "槽号", ["0x%X" % i for i, _g, _w in diffs[:6]])
    return False


def random_case(rng, n, level_cap, own_count, own_prob, dungeons, level_extra):
    """一个随机合成怪物与调用参数(全部是合成数据,不含原版内容)。返回 (Mob, 脚本行, 自带表行, 种类, 等级, 副本参数)。"""
    names = M.CATEGORY_NAMES
    named_ids = [50000, 50500, 56000, 59999]
    lmin = rng.randint(1, level_cap)
    lmax = lmin + rng.choice([0, 0, rng.randint(1, max(1, level_cap // 4))])
    lines = [("[level]", [str(lmin), str(lmax)])]
    if rng.random() < 0.5:
        lines.append(("[ability table]", ["1"]))
    for tag in M.ELEMENT_RES_TAGS + M.STATUS_TAGS + M.ELEMENT_ATK_TAGS:
        if rng.random() < 0.3:
            a = rng.randint(-50, 200)
            lines.append((tag, [str(a), str(a + rng.randint(0, 100))]))
    for tag in ("[HP regen speed]", "[MP regen speed]", "[move speed]", "[attack speed]", "[cast speed]", "[hit recovery]",
                "[jump power]", "[jump speed]", "[weight]", "[rigidity]"):
        if rng.random() < 0.6:
            a = rng.choice([rng.randint(0, 3000), rng.randint(0, 3000), rng.randint(-2 ** 31, 2 ** 31 - 1)])
            b = rng.choice([a + rng.randint(0, 3000), rng.randint(-2 ** 31, 2 ** 31 - 1)])
            lines.append((tag, [str(a), str(b)]))
    cat_vals = []
    for _ in range(rng.randint(0, 6)):
        name = rng.choice(names)
        op = rng.choice(["*", "*", "+"])
        value = rng.choice([rng.randint(1, 100), rng.randint(100, 1000), rng.randint(-300, 5000), rng.randint(-2 ** 31, 2 ** 31 - 1)])
        cat_vals += ['"%s"' % name, '"%s"' % op, str(value)]
    if cat_vals:
        lines.append(("[ability category]", cat_vals))
    if rng.random() < 0.15:
        lines.append(("[life max]", [str(rng.choice([1, 5, 50, 300]))]))
    mob_id = rng.choice([1000 + n, 1000 + n, rng.choice(named_ids)])
    mob = M.Mob(mob_id, "synthetic", lines)
    own_rows = None
    if rng.random() < own_prob:
        # 自带表要与公共表等长:原版按公共表里的行号直接下标自带表,没有越界检查,表更短时读到数组之外(未定义行为)
        big = rng.random() < 0.5   # 一半的自带表用满 32 位范围,专门打溢出与"整数不确定值"路径
        own_rows = [[lv] + [rng.randint(-2 ** 31, 2 ** 31 - 1) if big else rng.randint(0, 2_000_000) for _ in range(23)]
                    for lv in range(1, own_count + 1)]
    kind = rng.choice([0, 3])
    level = rng.choice([lmin, lmax, (lmin + lmax) // 2, rng.randint(0, level_extra)])
    dungeon = rng.choice(dungeons)
    return mob, lines, own_rows, kind, level, dungeon


def case_line(mob, lines, own_rows, kind, level, dungeon, got):
    script = "|".join("%s=%s" % (tag, ",".join(vals)) for tag, vals in lines)
    own_text = ",".join(str(x) for row in own_rows for x in row) if own_rows else "-"
    return "\t".join([str(mob.id), str(kind), str(level), str(dungeon), script, own_text, ",".join(str(x) for x in got)])


def write_golden(args, oracle):
    """金标准文件:全合成的基础属性表(每表 40 行,战争房表 5 行)与随机合成用例,期望值来自服务端原版函数的运行结果。
    不含任何原版数据,可以入库,让 C# 的单元测试在没有原版文件、没有模拟器的机器上也能对着"原版函数的行为"回归。"""
    rng = random.Random(args.golden_seed)
    tables = []
    for _ in range(4):
        tables.append([[lv] + [rng.choice([rng.randint(0, 3000), rng.randint(0, 2_000_000)]) for _ in range(23)] for lv in range(1, 41)])
    tables.append([[2000 + i] + [rng.randint(0, 2_000_000) for _ in range(23)] for i in range(5)])
    oracle.load_tables(tables)
    out = ["# 怪物数值金标准:合成表与合成用例,期望值来自服务端原版函数(setBasicAbilityByLevel + upgradeAbilityByMonsterParameterCategory)在模拟器里的运行结果。",
           "# 由 tools/py/mob_ability_oracle.py --golden 生成;不含原版数据。TABLE 行:表号、逐行展开的整数(每行 24 列);CASE 行见 dnf mob-check --cases。"]
    for i, rows in enumerate(tables):
        out.append("TABLE\t%d\t%s" % (i, ",".join(str(x) for row in rows for x in row)))
    count = 0
    own_budget = 6
    for n in range(args.golden):
        mob, lines, own_rows, kind, level, dungeon = random_case(rng, n, level_cap=30, own_count=40, own_prob=0.08,
                                                                  dungeons=[0, 0, 0, 2000, 2001, 2002, 2004, 5], level_extra=45)
        if own_rows is not None:
            if own_budget == 0:
                own_rows = None
            else:
                own_budget -= 1
        oracle.fill_script(mob, own_rows)
        got = oracle.run(level, dungeon, kind)
        out.append("CASE\t" + case_line(mob, lines, own_rows, kind, level, dungeon, got))
        count += 1
    with open(args.golden_file, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(out) + "\n")
    print("已写出金标准", args.golden_file, count, "条用例,%d KB" % (os.path.getsize(args.golden_file) // 1024))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cs", default=os.path.join(elf_emu.ROOT, "out", "mob_cs.tsv"), help="dnf mob-check --dump 的输出")
    ap.add_argument("--stress", type=int, default=0, help="另外随机生成的合成怪物个数")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--skip-real", action="store_true")
    ap.add_argument("--write-cases", help="把合成用例与原版函数的输出写到这个文件(含原版派生数值,放 out/,不入库)")
    ap.add_argument("--golden", type=int, default=0, help="写金标准文件:合成表 + 这么多条合成用例(不含原版数据,可入库)")
    ap.add_argument("--golden-file", default=os.path.join(elf_emu.ROOT, "src", "Dnf.Game.Tests", "Golden", "mob_ability_golden.txt"))
    ap.add_argument("--golden-seed", type=int, default=20260101)
    args = ap.parse_args()

    pvf = D.Pvf()
    strings = S.Strings(pvf)
    mob_list, tables = load_all(pvf, strings)
    oracle = Oracle()
    oracle.load_tables(tables)
    print("默认值镜像已取得;全局基础属性表已写入")

    total_bad = 0
    if not args.skip_real:
        want = {}
        with open(args.cs, encoding="utf-8") as fh:
            for line in fh:
                parts = line.rstrip("\n").split("\t")
                want[(int(parts[0]), int(parts[1]), int(parts[2]))] = [int(x) for x in parts[3].split(",")]
        ok = bad = 0
        hist = {}
        shown = [0]
        for mob_id in sorted(mob_list):
            path = M.resolve("monster/monster.lst", mob_list[mob_id])
            lines = M.parse_script(pvf, strings, path)
            if lines is None:
                continue
            mob = M.Mob(mob_id, path, lines)
            own = {}
            for rel in mob.base_table_paths():
                own_lines = M.parse_script(pvf, strings, M.resolve("monster/monster.lst", rel))
                if own_lines is not None:
                    for i, row in enumerate(M.parse_table(own_lines)):
                        own[i] = row
            own_rows = [own[i] for i in range(len(own))] if own else None
            oracle.fill_script(mob, own_rows)
            lmin, lmax = mob.pair("[level]")
            levels = {lmin, lmax, (lmin + lmax) // 2}
            for kind in (0, 3):
                for level in sorted(levels):
                    key = (mob_id, level, kind)
                    if key not in want:
                        continue
                    got = oracle.run(level, 0, kind)
                    if compare("mob %d level %d kind %d" % key, got, want[key], hist, shown):
                        ok += 1
                    else:
                        bad += 1
        print("全库对拍:一致 %d,不一致 %d" % (ok, bad))
        if hist:
            print("不一致的槽号分布:", {"0x%X" % k: v for k, v in sorted(hist.items())})
        total_bad += bad

    if args.stress:
        rng = random.Random(args.seed)
        ok = bad = 0
        hist = {}
        shown = [0]
        case_lines = []
        for n in range(args.stress):
            mob, lines, own_rows, kind, level, dungeon = random_case(rng, n, level_cap=80, own_count=200, own_prob=0.03,
                                                                      dungeons=[0, 0, 2000, 2001, 2002, 5], level_extra=120)
            oracle.fill_script(mob, own_rows)
            got = oracle.run(level, dungeon, kind)
            if args.write_cases:
                case_lines.append(case_line(mob, lines, own_rows, kind, level, dungeon, got))
            stats = M.basic_by_level(mob, tables, own_rows, level, dungeon, kind)
            M.apply_categories(stats, mob.categories(), kind == 3)
            if compare("stress #%d (mob %d level %d kind %d dungeon %d)" % (n, mob.id, level, kind, dungeon), got, stats.r, hist, shown):
                ok += 1
            else:
                bad += 1
        print("随机压力对拍:一致 %d,不一致 %d" % (ok, bad))
        if args.write_cases:
            with open(args.write_cases, "w", encoding="utf-8", newline="\n") as fh:
                fh.write("\n".join(case_lines) + "\n")
            print("已写出", args.write_cases, len(case_lines), "条用例")
        if hist:
            print("不一致的槽号分布:", {"0x%X" % k: v for k, v in sorted(hist.items())})
        total_bad += bad

    if args.golden:
        write_golden(args, oracle)
    return 1 if total_bad else 0


if __name__ == "__main__":
    sys.exit(main())
