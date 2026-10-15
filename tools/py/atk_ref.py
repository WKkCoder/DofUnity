#!/usr/bin/env python3
"""`.atk`(攻击信息)的参考读取器:按服务端加载函数读出来的语义,逐标记地复现"原版会读出什么"。

它的用途有两个:
  1. 对全库 6,394 个 .atk 统计"原版加载函数遇到各种不规整数据时的行为"(游离标记、未知标签、关键字拼错、缺值……),
     判断读函数得出的语法有没有读错;
  2. 作为 C# 实现(Dnf.Game 的 AttackInfoLoader)的独立对照:`--dump` 写出每个文件读出的字段,与 `dnf atk-check` 的输出逐行比较。

语义来源(服务端二进制带符号,函数文本只在本机 re/ 里,不入库;依据与地址见 docs/evidence/ledger.md 的 E-047):
  标记读取原语  ScanType / ScanTypeOrStr / ScanInt / ScanFloat / ScanStr(每次读一个标记,读到什么都消耗掉)
  顶层循环      ImportAttackInfoScript:先 clear() 设默认值,再循环"读标签 -> 模块节处理 -> 按标签分派到处理块"
  模块节        [pvp] [warroom] [assault] [death tower] [boss tower] 到各自的 [/…] 之间在"副本"模块下整节跳过;[dungeon] 本身是空操作
本文件只含自己命名的字段与自己写的语法,不含原版代码。

用法:
  python3 tools/py/atk_ref.py                      统计
  python3 tools/py/atk_ref.py --dump out/atk_ref.txt   另外写出每个文件的读取结果(out/ 不入库)
"""
import argparse
import math
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402

# 标记类型(与 DNFLEX_TOKEN 的编号一致,见 E-047):0 结束 2 十进制 3 十六进制 4 浮点 5 标签 6 自定义(括号词) 7 字符串 8 连接符 9 对(整数) 10 对(字符串)
T_EOF, T_DEC, T_HEX, T_FLOAT, T_TAG, T_PAREN, T_STR, T_CONN, T_PAIR_INT, T_PAIR_STR = 0, 2, 3, 4, 5, 6, 7, 8, 9, 10

ELEMENTS = {"[fire element]": 0, "[water element]": 1, "[dark element]": 2, "[light element]": 3}
ELEMENT_NONE = 4

# 活动状态类型(服务端表 ACTIVESTATUS_ATTACK_TAG 的下标;没有匹配时为 18)
STATUS_TYPES = ["[slow]", "[freeze]", "[poison]", "[stun]", "[curse]", "[blind]", "[lightning]", "[stone]", "[sleep]",
                "[burn]", "[weapon break]", "[bleeding]", "[haste]", "[bless]", "[element]", "[confuse]", "[hold]", "[armor break]"]
STATUS_NONE = 18
# 个别类型后面还跟着几个整数(处理块里逐个读)
STATUS_EXTRA = {"[blind]": 2, "[slow]": 2, "[haste]": 2, "[stone]": 1, "[confuse]": 1, "[armor break]": 1, "[weapon break]": 1,
                "[poison]": 1, "[lightning]": 1, "[bleeding]": 1, "[burn]": 4, "[bless]": 4, "[curse]": 4}
STATUS_SLOTS = 3

# 模块节:名字 -> 副本模块下是否整节跳过
SKIPPED_MODULES = {"[pvp]": "[/pvp]", "[warroom]": "[/warroom]", "[assault]": "[/assault]", "[death tower]": "[/death tower]", "[boss tower]": "[/boss tower]"}
DUNGEON_TAGS = {"[dungeon]", "[/dungeon]"}


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def trunc_int(x):
    """x87 的 fistp 在控制字被设成"向零舍入"后的行为;超范围给 0x80000000。"""
    if math.isnan(x) or math.isinf(x) or x >= 2147483648.0 or x < -2147483648.0:
        return -2147483648
    return int(x)


class Cursor:
    def __init__(self, tokens, strings):
        self.tokens = tokens
        self.strings = strings
        self.i = 0

    def next(self):
        if self.i >= len(self.tokens):
            return T_EOF, 0
        t = self.tokens[self.i]
        self.i += 1
        return t

    def text(self, v):
        return self.strings.get(v)


def new_attack():
    return {
        "damage": 0.0, "damage_increase_rate": 0.0, "absolute_damage": 0, "damage_bonus": 0, "attack_type": 0,
        "weapon_damage_apply": 0, "human_active_status_rate": 1.0, "damage_reaction": 1, "push_aside": 0, "lift_up": 0,
        "critical_hit": 0.0, "stuck": 0.0, "stuck_prob_increase": 0, "knock_back": 0, "knock_back_param": 0,
        "weakness": [0.0, 0.0], "bounce": 0, "ignore_defense": 0, "hit_wav": "", "attack_direction": 1,
        "hit_type": 5, "hit_blood": 0, "hit_int": -1, "hit_float": -1.0, "use_weapon_hit_delay_rate": 0, "hit_direction": 0,
        "ignore_super_armor": 0, "ignore_weight": 0, "force_hit_stun_time": 0, "hp_drain": 0, "mp_drain": 0,
        "hit_number_increasing": 1, "ignore_diehard": 0, "hit_check_by_screen_pos": 0, "max_target_hit_number": -1,
        "attack_myself": 0, "attack_friend": 0, "attack_enemy": 1, "elements": set(),
        "status": [{"type": STATUS_NONE, "prob": 0.0, "level": 0, "duration": 0, "extra": []} for _ in range(STATUS_SLOTS)],
        "human_damage_rate": 100.0, "fort_damage_rate": 100.0, "monster_damage_rate": 100.0, "catch": [],
    }


class Loader:
    def __init__(self, tokens, strings):
        self.c = Cursor(tokens, strings)
        self.a = new_attack()
        self.ev = Counter()
        self.status_count = 0

    # ---- 标记读取原语(每次读一个标记) ----
    def scan_type(self, report=True):
        ok = True
        name = None
        while True:
            t, v = self.c.next()
            if t == T_TAG:
                name = self.c.text(v)
            else:
                ok = False
            if t == T_TAG or t == T_EOF:
                break
            if report:
                self.ev["scan_type_skipped_token"] += 1
        return ok, name

    def scan_type_or_str(self):
        text = None
        while True:
            t, v = self.c.next()
            if t == T_STR:
                text = self.c.text(v)
            elif t == T_TAG:
                text = self.c.text(v)
            elif t == T_PAIR_INT:
                t2, v2 = self.c.next()
                if t2 == T_PAIR_STR:
                    text = self.c.text(v2)
                    t = T_STR
            if t in (T_STR, T_TAG, T_EOF):
                break
            self.ev["keyword_skipped_token"] += 1
        return text

    def scan_int(self):
        t, v = self.c.next()
        if t in (T_DEC, T_HEX):
            return v, True
        if t == T_FLOAT:
            return trunc_int(struct.unpack("<f", struct.pack("<i", v))[0]), True
        return 0, False

    def scan_float(self):
        t, v = self.c.next()
        if t in (T_DEC, T_HEX):
            return f32(float(v)), True
        if t == T_FLOAT:
            return struct.unpack("<f", struct.pack("<i", v))[0], True
        return 0.0, False

    def scan_str(self):
        t, v = self.c.next()
        if t == T_STR:
            return self.c.text(v), True
        if t == T_PAIR_INT:
            t2, v2 = self.c.next()
            if t2 == T_PAIR_STR:
                return self.c.text(v2), True
        return None, False

    # ---- 带统计的读取 ----
    def i32(self, tag):
        v, ok = self.scan_int()
        if not ok:
            self.ev["bad_int"] += 1
        return v

    def fl(self, tag):
        v, ok = self.scan_float()
        if not ok:
            self.ev["bad_float"] += 1
        return v

    def keyword(self, table, field, tag):
        text = self.scan_type_or_str()
        if text in table:
            self.a[field] = table[text]
        else:
            self.ev["unknown_keyword"] += 1

    # ---- 顶层 ----
    def run(self):
        while True:
            ok, name = self.scan_type()
            if not ok:
                if name is not None:
                    self.ev["terminated_by_stray_token"] += 1
                else:
                    self.ev["ended_at_eof"] += 1
                return
            if name in DUNGEON_TAGS:
                continue
            if name in SKIPPED_MODULES:
                close = SKIPPED_MODULES[name]
                self.ev["module_section_skipped"] += 1
                while True:
                    ok2, n2 = self.scan_type(report=False)
                    if n2 == close:
                        break
                    if n2 is None and not ok2 and self.c.i >= len(self.c.tokens):
                        self.ev["module_section_unterminated"] += 1
                        return
                continue
            self.dispatch(name)

    def dispatch(self, name):
        a = self.a
        h = HANDLERS.get(name)
        if h is None:
            self.ev["unknown_tag"] += 1
            return
        h(self, a, name)


def _float(field):
    def h(L, a, name):
        a[field] = L.fl(name)
    return h


def _int(field):
    def h(L, a, name):
        a[field] = L.i32(name)
    return h


def _bool(field):
    def h(L, a, name):
        a[field] = 1 if L.i32(name) != 0 else 0
    return h


def _kw(field, table):
    def h(L, a, name):
        L.keyword(table, field, name)
    return h


def h_human_status_rate(L, a, name):
    a["human_active_status_rate"] = f32(L.fl(name) / 100.0)


def h_knock_back(L, a, name):
    a["knock_back"] = L.i32(name)
    if a["knock_back"] == 3:
        a["knock_back_param"] = trunc_int(L.fl(name))


def h_weakness(L, a, name):
    a["weakness"] = [L.fl(name), L.fl(name)]


def h_hit_wav(L, a, name):
    s, ok = L.scan_str()
    if ok:
        a["hit_wav"] = s
    else:
        L.ev["bad_str"] += 1


def h_hit_info(L, a, name):
    L.keyword({"[blow]": 0, "[cut]": 1, "[burn]": 2, "[etc]": 3, "[mana burn]": 4}, "hit_type", name)
    L.keyword({"[no blood]": 0, "[blood]": 1}, "hit_blood", name)
    a["hit_int"] = L.i32(name)
    a["hit_float"] = L.fl(name)


def h_element(L, a, name):
    text = L.scan_type_or_str()
    el = ELEMENTS.get(text, ELEMENT_NONE)
    if el == ELEMENT_NONE:
        a["elements"] = set()
    else:
        a["elements"].discard(ELEMENT_NONE)
        a["elements"].add(el)


def h_status(L, a, name):
    if L.status_count >= STATUS_SLOTS:
        L.ev["status_overflow"] += 1
        return
    slot = a["status"][L.status_count]
    text = L.scan_type_or_str()
    slot["type"] = STATUS_TYPES.index(text) if text in STATUS_TYPES else STATUS_NONE
    slot["prob"] = L.fl(name)
    slot["level"] = L.i32(name)
    slot["duration"] = L.i32(name)
    slot["extra"] = [L.i32(name) for _ in range(STATUS_EXTRA.get(text, 0))]
    L.status_count += 1


def h_catch(L, a, name):
    while True:
        first, ok = L.scan_int()
        if not ok:
            break
        second = L.i32(name)
        third = L.fl(name)
        a["catch"].append((first, second, third))


HANDLERS = {
    "[damage]": _float("damage"),
    "[damage increase rate]": _float("damage_increase_rate"),
    "[absolute damage]": _int("absolute_damage"),
    "[damage bonus]": _int("damage_bonus"),
    "[attack type]": _kw("attack_type", {"[physic]": 0, "[magic]": 1, "[ignore defense]": 2}),
    "[weapon damage apply]": _bool("weapon_damage_apply"),
    "[attack myself]": _bool("attack_myself"),
    "[attack friend]": _bool("attack_friend"),
    "[attack enemy]": _bool("attack_enemy"),
    "[elemental property]": h_element,
    "[active status]": h_status,
    "[damage reaction]": _kw("damage_reaction", {"[none]": 0, "[damage]": 1, "[down]": 2, "[damage except human]": 3}),
    "[push aside]": _int("push_aside"),
    "[lift up]": _int("lift_up"),
    "[critical hit]": _float("critical_hit"),
    "[stuck]": _float("stuck"),
    "[stuck prob increase]": _bool("stuck_prob_increase"),
    "[knuck back]": h_knock_back,
    "[weakness]": h_weakness,
    "[bounce]": _int("bounce"),
    "[ignore defense]": _bool("ignore_defense"),
    "[human damage rate]": _float("human_damage_rate"),
    "[fort damage rate]": _float("fort_damage_rate"),
    "[monster damage rate]": _float("monster_damage_rate"),
    "[HP drain]": _bool("hp_drain"),
    "[MP drain]": _bool("mp_drain"),
    "[hit wav]": h_hit_wav,
    "[attack direction]": _kw("attack_direction", {"[hit lift up]": 0, "[hit horizon]": 1, "[hit down]": 2}),
    "[hit info]": h_hit_info,
    "[is use weapon hit delay rate]": _bool("use_weapon_hit_delay_rate"),
    "[force hit stun time]": _int("force_hit_stun_time"),
    "[hit direction]": _kw("hit_direction", {"[auto]": 0, "[front]": 1, "[back]": 2, "[outer]": 3, "[inner]": 4}),
    "[ignore super armor]": _int("ignore_super_armor"),
    "[ignore weight]": _int("ignore_weight"),
    "[human active status rate]": h_human_status_rate,
    "[is hit number increasing]": _bool("hit_number_increasing"),
    "[catch object]": h_catch,
    "[max target hit number]": _int("max_target_hit_number"),
    "[ignore diehard]": _bool("ignore_diehard"),
    "[hit check by screen pos]": _bool("hit_check_by_screen_pos"),
}


def bits(x):
    """float32 的位模式(8 位十六进制),与 C# 的 BitConverter.SingleToInt32Bits 对应。"""
    return "%08x" % struct.unpack("<I", struct.pack("<f", x))[0]


def render(a):
    """把读出的字段写成稳定的一行文本(与 C# 的 AttackInfoData.Describe 逐字一致):字段名按字典序,浮点写成位模式。"""
    parts = []
    for k in sorted(a):
        v = a[k]
        if k == "elements":
            v = ",".join(str(x) for x in sorted(v))
        elif k == "status":
            v = ";".join("%d/%s/%d/%d/%s" % (s["type"], bits(s["prob"]), s["level"], s["duration"], ",".join(map(str, s["extra"]))) for s in v)
        elif k == "weakness":
            v = ",".join(bits(x) for x in v)
        elif k == "catch":
            v = ";".join("%d/%d/%s" % (x, y, bits(z)) for x, y, z in v)
        elif isinstance(v, float):
            v = bits(v)
        parts.append("%s=%s" % (k, v))
    return " ".join(parts)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", help="把每个文件读出的字段写到这个文件(放 out/,不入库)")
    a = ap.parse_args()
    pvf = D.Pvf()
    strings = S.Strings(pvf)
    totals = Counter()
    per_file = Counter()
    dump = open(a.dump, "w", encoding="utf-8") if a.dump else None
    n = 0
    for e in pvf.entries:
        if not e.path.lower().endswith(".atk"):
            continue
        raw = pvf.read(e)
        if raw[:2] != b"\xb0\xd0":
            totals["not_token_script"] += 1
            continue
        tokens = list(struct.iter_unpack("<Bi", raw[2:]))
        L = Loader(tokens, strings)
        L.run()
        n += 1
        totals["files"] += 1
        for k, v in L.ev.items():
            totals[k] += v
            per_file[k] += 1
        if dump:
            dump.write("%s\t%s\n" % (e.path.lower(), render(L.a)))
    if dump:
        dump.close()
    print("读取 %d 个 .atk" % n)
    print("事件(总次数 / 涉及文件数):")
    for k in sorted(totals):
        if k in ("files", "not_token_script"):
            continue
        print("  %-30s %8d / %d" % (k, totals[k], per_file[k]))


if __name__ == "__main__":
    main()
