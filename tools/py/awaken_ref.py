#!/usr/bin/env python3
"""觉醒(r15-awaken)相关脚本字段的参照读取器:按服务端加载函数的语义,独立复现"原版会读出什么"。

用途:作为 C# 实现(Dnf.Game 的 CharacterGrowth、SkillGrants、SkillDef、QuestScript/QuestRules)的独立对照:
`--dump` 写出每个脚本读出的觉醒相关字段,与 `dnf awaken-check --dump` 的输出逐行比较(两边排序后应完全一致);
`--against <C# 的 dump>` 直接比较并给出一致与不一致的行数。dump 文件里有逐字段的数值,只写到 out/,不入库;控制台只打印计数。

读哪些东西(每一类一种行,与 C# 一一对应):
  growth  每个 .chr、每个 (等级、成长类型 0 到 5、觉醒 0 到 2) 的逐级增量:等级 < 15 取成长类型 0 的块;< 50 或觉醒不是 1、2 取所选成长类型的块;
          否则取 [growtype N] 下 [awakening a] 的块(下标 = 成长类型 × 2 + a − 1,块缺失按全 0);
  stats   同样的累计属性:初始值加上 L = 1 到 level − 1 每一级的增量(所以觉醒块从 50 到 51 那一步起作用);
          属性按服务端的换算存成"十分之一"的整数:单精度的 10.0f × 值,再非负加 0.5、负数减 0.5,向零取整;
  gifts   每个 .chr、每个 (成长类型、觉醒) 拥有的白送技能:开局块与自己成长类型块里的 [skill],加上觉醒块里对应的 [awakening skill]("编号 等级"成对,同技能取高等级);
  skl     每个 .skl 的 [second growtype maximum level](最后一次出现为准)、读档上限、六个成长类型 × 三个觉醒状态下的最高级
          (觉醒了且这次觉醒写了非 0 的最高级就取它,否则取 [growtype maximum level] 的这一项);
  qst     每个 .qst 里属于觉醒任务链(成就类且 [job change quest] 为 2)或奖励类型是 [awakening type] 的:觉醒号与等级范围。
本文件只含自己命名的字段与自己写的语法,不含原版代码、也不抄原版数据。

与 C# 的有意差异:无(这是对拍的参照,不是第二份实现;两边只共用上面的语义规格,各自读脚本、各自按规格算)。

用法:
  python3 tools/py/awaken_ref.py                                 只统计
  python3 tools/py/awaken_ref.py --dump out/awaken_ref.txt       写出每个脚本的读取结果(out/ 不入库)
  python3 tools/py/awaken_ref.py --against out/awaken_cs.txt     与 `dnf awaken-check --dump out/awaken_cs.txt` 的输出比较
"""
import argparse
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402

GROWTYPE_COUNT = 6
AWAKENING_COUNT = 2
NOVICE_UNTIL = 15
AWAKENING_FROM = 50
INT_MAX = 2147483647
DUMP_LEVELS = (1, 14, 15, 49, 50, 99)
STAT_LEVELS = (50, 51, 60, 99)
STAT_TAGS = ("[hp max]", "[mp max]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]",
             "[inventory limit]", "[mp regen speed]", "[hit recovery]")
GRADE_TAGS = {
    "[epic]": "epic", "[training]": "training", "[achievement]": "achievement",
    "[daily]": "daily", "[daily random]": "daily", "[special daily]": "daily",
    "[normaly repeat]": "normal", "[normal repeat]": "normal",
    "[common unique]": "common", "[special]": "special", "[title]": "title", "[urgent]": "urgent",
}
INT_RE = re.compile(r"^\s*[+-]?[0-9]+\s*$")


# ------------------------------------------------------------------ 标记流读取

class StringPool:
    """stringtable.bin:u32 个数,个数 + 1 个 u32 偏移(相对第 4 字节),按 Latin1 逐字节还原(标签与路径都是 ASCII)。"""

    def __init__(self, raw):
        n = struct.unpack_from("<I", raw, 0)[0]
        self.raw = raw
        self.count = n
        self.offsets = struct.unpack_from("<%dI" % (n + 1), raw, 4)
        self.cache = {}

    def get(self, index):
        if not 0 <= index < self.count:
            return None
        s = self.cache.get(index)
        if s is None:
            s = self.raw[4 + self.offsets[index]: 4 + self.offsets[index + 1]].decode("latin1")
            self.cache[index] = s
        return s


def lines_of(raw, pool):
    """标记流 -> [(标签或 None, [(种类, 值)])];不是标记流(没有 B0 D0,或正文不是 5 的倍数)返回 None。
    种类:i 整数(十进制与十六进制)、f 单精度浮点、s 字符串、p 括号词、c 分隔符、k 种类加键、o 其他。"""
    if raw is None or len(raw) < 2 or raw[0] != 0xB0 or raw[1] != 0xD0 or (len(raw) - 2) % 5 != 0:
        return None
    toks = list(struct.iter_unpack("<Bi", raw[2:]))
    lines = []
    tag = None
    have_tag = False
    vals = []
    i = 0
    while i < len(toks):
        ty, v = toks[i]
        if ty == 5:
            if have_tag or vals:
                lines.append((tag, vals))
            tag = pool.get(v)
            have_tag = True
            vals = []
        elif ty in (2, 3):
            vals.append(("i", v))
        elif ty == 4:
            vals.append(("f", struct.unpack("<f", struct.pack("<i", v))[0]))
        elif ty == 7:
            vals.append(("s", pool.get(v)))
        elif ty == 6:
            vals.append(("p", pool.get(v)))
        elif ty == 8:
            vals.append(("c", pool.get(v)))
        elif ty == 9 and i + 1 < len(toks) and toks[i + 1][0] == 10:
            vals.append(("k", (v, pool.get(toks[i + 1][1]))))
            i += 1
        else:
            vals.append(("o", v))
        i += 1
    if have_tag or vals:
        lines.append((tag, vals))
    return lines


def ints(vals):
    return [v for k, v in vals if k == "i"]


def first_int(vals, fallback):
    got = ints(vals)
    return got[0] if got else fallback


def first_string(vals):
    for k, v in vals:
        if k == "s":
            return v
    return None


def first_number(vals):
    for k, v in vals:
        if k == "i":
            return float(v)
        if k == "f":
            return v
    return None


def parse_int(text):
    """与 int.TryParse 一样:允许前后空白与一个符号,必须落在 32 位有符号范围内。"""
    if not INT_RE.match(text):
        return None
    n = int(text)
    return n if -2 ** 31 <= n < 2 ** 31 else None


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


# ------------------------------------------------------------------ 成长块

def scaled_round(value):
    """服务端读属性的换算:单精度 10.0f × 值;非负加 0.5、负数减 0.5,向零取整。"""
    scaled = f32(10.0 * f32(value))
    return int(scaled - 0.5 if scaled < 0 else scaled + 0.5)


def block_index(tag, prefix, limit, growtype_style):
    if not tag.startswith(prefix) or not tag.endswith("]"):
        return None
    n = parse_int(tag[len(prefix): len(tag) - 1])
    if n is None or n < 1 or n > limit:
        return None
    return n - 1 if growtype_style else n


class Growth:
    def __init__(self, lines):
        zero = (0,) * 9
        self.initial = zero
        self.growtypes = [zero] * GROWTYPE_COUNT
        self.awakenings = [zero] * (GROWTYPE_COUNT * AWAKENING_COUNT)
        mode, growtype, awakening, current = 0, -1, 0, zero

        def commit():
            if mode == 1:
                self.initial = current
            elif mode == 2:
                self.growtypes[growtype] = current
            elif mode == 3:
                self.awakenings[growtype * AWAKENING_COUNT + awakening - 1] = current

        for tag, vals in lines:
            if tag is None:
                continue
            t = tag.lower()
            if t == "[initial value]":
                commit()
                mode, current = 1, zero
                continue
            g = block_index(t, "[growtype ", GROWTYPE_COUNT, True)
            if g is not None:
                commit()
                mode, growtype, awakening, current = 2, g, 0, zero
                continue
            if growtype >= 0:
                a = block_index(t, "[awakening ", AWAKENING_COUNT, False)
                if a is not None:
                    commit()
                    mode, awakening, current = 3, a, zero
                    continue
            if mode == 0:
                continue
            number = first_number(vals)
            if number is not None and t in STAT_TAGS:
                row = list(current)
                row[STAT_TAGS.index(t)] = scaled_round(number)
                current = tuple(row)
        commit()

    def increment(self, level, growtype, awakening):
        if not 0 <= growtype < GROWTYPE_COUNT:
            growtype = 0
        if level < NOVICE_UNTIL:
            return self.growtypes[0]
        if level < AWAKENING_FROM or awakening < 1 or awakening > AWAKENING_COUNT:
            return self.growtypes[growtype]
        return self.awakenings[growtype * AWAKENING_COUNT + awakening - 1]

    def stats_at(self, level, growtype, awakening):
        total = list(self.initial)
        for l in range(1, level):
            inc = self.increment(l, growtype, awakening)
            total = [x + y for x, y in zip(total, inc)]
        return tuple(total)


# ------------------------------------------------------------------ 白送技能

def add_pairs(result, pairs):
    for i in range(0, len(pairs) - 1, 2):
        skill, level = pairs[i], pairs[i + 1]
        if level < 1:
            continue
        for j, (s, l) in enumerate(result):
            if s == skill:
                if level > l:
                    result[j] = (skill, level)
                break
        else:
            result.append((skill, level))


def awakening_block_number(tag):
    prefix = "[awakening "
    if not tag.endswith("]"):
        return 0
    a = parse_int(tag[len(prefix): len(tag) - 1])
    return a if a is not None and 1 <= a <= AWAKENING_COUNT else 0


def gifts_of(lines, growtype, awakening):
    result = []
    mode, block, awake = 0, -1, 0   # 0 还没遇到块,1 开局,2 成长类型 N,3 觉醒块(里面的 [skill] 不读)
    for tag, vals in lines:
        if tag is None:
            continue
        t = tag.lower()
        if t == "[initial value]":
            mode, awake = 1, 0
            continue
        if t.startswith("[growtype ") and t.endswith("]"):
            n = parse_int(t[len("[growtype "): len(t) - 1])
            mode = 2 if n is not None and n >= 1 else 0
            block = n - 1 if mode == 2 else -1
            awake = 0
            continue
        if t == "[awakening skill]":
            if mode == 3 and block == growtype and awakening >= 1 and awake == awakening:
                add_pairs(result, ints(vals))
            continue
        if t.startswith("[awakening "):
            mode = 3
            awake = awakening_block_number(t)
            continue
        if t != "[skill]" or (mode != 1 and not (mode == 2 and block == growtype)):
            continue
        add_pairs(result, ints(vals))
    return result


# ------------------------------------------------------------------ 技能脚本

def read_skill(lines):
    growtype_max, second = [], []
    for tag, vals in lines:
        if tag is None:
            continue
        t = tag.lower()
        if t == "[growtype maximum level]":
            growtype_max = ints(vals)
        elif t == "[second growtype maximum level]":
            second = ints(vals)
    maximum = 0
    for tag, vals in lines:
        if tag is not None and tag.lower() == "[maximum level]":
            maximum = first_int(vals, 0)    # 同名标签只看第一次出现
            break
    effective = maximum
    for m in growtype_max:
        if m > effective:
            effective = m
    ceiling = effective
    for m in second:
        if m > ceiling:
            ceiling = m
    return growtype_max, second, ceiling


def type_max(growtype_max, second, growtype, awakening):
    if 1 <= awakening <= AWAKENING_COUNT and 0 <= growtype < GROWTYPE_COUNT:
        index = growtype * AWAKENING_COUNT + awakening - 1
        if index < len(second) and second[index] != 0:
            return second[index]
    return growtype_max[growtype] if 0 <= growtype < len(growtype_max) else 0


# ------------------------------------------------------------------ 任务脚本

def read_quest(lines):
    grade, job_change, lo, hi = "normal", 0, 1, None
    reward_type, reward_data = "[item]", []
    for tag, vals in lines:
        if tag is None:
            continue
        t = tag.lower()
        if t == "[grade]":
            s = first_string(vals)
            if s is not None and s.lower() in GRADE_TAGS:
                grade = GRADE_TAGS[s.lower()]
        elif t == "[job change quest]":
            job_change = first_int(vals, 0)
        elif t == "[level]":
            v = ints(vals)
            if len(v) > 0:
                lo = v[0]
            if len(v) > 1:
                hi = v[1]
        elif t == "[reward type]":
            s = first_string(vals)
            if s is not None:
                reward_type = s
        elif t == "[reward int data]":
            reward_data += ints(vals)
    is_chain = grade == "achievement" and job_change == 2
    is_reward = reward_type.lower() == "[awakening type]"
    awk = 0
    if is_reward and reward_data and 1 <= reward_data[0] <= AWAKENING_COUNT:
        awk = reward_data[0]
    hi_text = "max" if hi is None or hi == INT_MAX else str(hi)
    return is_chain, is_reward, awk, "%d,%s" % (lo, hi_text)


# ------------------------------------------------------------------ 主流程

def fmt(row):
    return " ".join(str(x) for x in row)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", help="把每个脚本读出的字段写到这个文件(放 out/,不入库)")
    ap.add_argument("--against", help="与 `dnf awaken-check --dump` 写出的文件比较(排序后逐行)")
    a = ap.parse_args()

    pvf = D.Pvf()
    pool = StringPool(pvf.get("stringtable.bin"))
    entries = sorted(((e.path.lower(), e) for e in pvf.entries), key=lambda x: x[0])
    out = []
    kinds = {"growth": 0, "stats": 0, "gifts": 0, "skl": 0, "qst": 0}
    n_chr = n_skl = n_qst = chain = reward = 0

    for path, e in entries:
        if path.startswith("character/") and path.endswith(".chr"):
            lines = lines_of(pvf.read(e), pool)
            if lines is None:
                continue
            n_chr += 1
            growth = Growth(lines)
            for g in range(GROWTYPE_COUNT):
                for aw in range(AWAKENING_COUNT + 1):
                    for level in DUMP_LEVELS:
                        out.append("growth %s L=%d g=%d a=%d %s" % (path, level, g, aw, fmt(growth.increment(level, g, aw))))
                        kinds["growth"] += 1
                    for level in STAT_LEVELS:
                        out.append("stats %s L=%d g=%d a=%d %s" % (path, level, g, aw, fmt(growth.stats_at(level, g, aw))))
                        kinds["stats"] += 1
                    gifts = sorted(gifts_of(lines, g, aw))
                    out.append("gifts %s g=%d a=%d %s" % (path, g, aw, " ".join("%d:%d" % x for x in gifts) if gifts else "-"))
                    kinds["gifts"] += 1
        elif path.startswith("skill/") and path.endswith(".skl"):
            lines = lines_of(pvf.read(e), pool)
            if lines is None:
                continue
            n_skl += 1
            growtype_max, second, ceiling = read_skill(lines)
            typemax = " ".join(str(type_max(growtype_max, second, g, aw)) for g in range(GROWTYPE_COUNT) for aw in range(AWAKENING_COUNT + 1))
            out.append("skl %s second=%s ceiling=%d typemax %s" % (path, ",".join(str(x) for x in second) if second else "-", ceiling, typemax))
            kinds["skl"] += 1
        elif path.startswith("n_quest/") and path.endswith(".qst"):
            lines = lines_of(pvf.read(e), pool)
            if lines is None:
                continue
            n_qst += 1
            is_chain, is_reward, awk, lv = read_quest(lines)
            if is_chain or is_reward:
                chain += 1 if is_chain else 0
                reward += 1 if is_reward else 0
                out.append("qst %s chain=%d awk=%d lv=%s" % (path, 1 if is_chain else 0, awk, lv))
                kinds["qst"] += 1

    out.sort()
    print("角色脚本 %d 个,技能脚本 %d 个,任务脚本 %d 个;觉醒链上的 %d 个,奖励是觉醒的 %d 个" % (n_chr, n_skl, n_qst, chain, reward))
    print("读出的行:" + ",".join("%s %d" % (k, v) for k, v in kinds.items()) + ",共 %d 行" % len(out))

    if a.dump:
        os.makedirs(os.path.dirname(os.path.abspath(a.dump)), exist_ok=True)
        with open(a.dump, "w", encoding="utf-8", newline="\n") as f:
            for line in out:
                f.write(line + "\n")
        print("已写出 %d 行 -> %s" % (len(out), a.dump))

    if a.against:
        with open(a.against, encoding="utf-8") as f:
            other = sorted(line.rstrip("\n") for line in f)
        mine = set(out)
        theirs = set(other)
        same = len(mine & theirs)
        only_mine = sorted(mine - theirs)
        only_theirs = sorted(theirs - mine)
        print("对拍:Python %d 行,C# %d 行,一致 %d 行,只在 Python 的 %d 行,只在 C# 的 %d 行" % (len(out), len(other), same, len(only_mine), len(only_theirs)))
        for line in only_mine[:5]:
            print("  只在 Python:" + line)
        for line in only_theirs[:5]:
            print("  只在 C#:" + line)
        if only_mine or only_theirs or len(out) != len(other):
            sys.exit(1)
        print("对拍一致")


if __name__ == "__main__":
    main()
