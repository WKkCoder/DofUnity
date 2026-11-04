#!/usr/bin/env python3
"""M92 汇总数:护甲 runtime [category] 与非常规条件伤害率 .atk(只输出计数,非 REA)。"""
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import atk_ref as A  # noqa: E402
import dnfdata as D  # noqa: E402
import mob_ability_ref as M  # noqa: E402

INT_RE = re.compile(r"^-?\d+$")
# 与 ActObjectCategories 表一致(0x0129c140 次序,E-171)
NAMES = [
    "human", "beast", "plant", "insect", "machine", "organic", "hybrid", "undead", "devil", "spirit", "dragon", "angel",
    "melee combat", "range combat", "close-passive", "close-carefully", "full-superarmor", "close-tough", "close-manace",
    "oneside move", "human armor", "fort armor", "divine armor", "goblin", "tau", "lugaru", "fixture", "stay", "melee",
    "missile", "energy", None, None, "bullet", "cannon", "explosion", "inflame", "non inflame", "light", "dark", "water",
    "fire", "fort", "unbeatable",
]


def resolve_tag(token):
    if not token.startswith('"'):
        return None
    s = token[1:-1].strip()
    if s.startswith("[") and s.endswith("]"):
        s = s[1:-1]
    s = s.lower()
    for i, n in enumerate(NAMES):
        if n and n == s:
            return i
    return None


def runtime_cats(mob):
    vals = mob.first.get("[category]", [])
    found = set()
    for v in vals:
        cid = resolve_tag(v)
        if cid in (0x14, 0x15, 0x16):
            found.add(cid)
    return found


def atk_rates_non_default(pvf, strings, path):
    raw = pvf.get(path)
    if raw is None or raw[:2] != b"\xb0\xd0":
        return False
    tokens = list(struct.iter_unpack("<Bi", raw[2:]))
    L = A.Loader(tokens, strings)
    L.run()
    a = L.a
    return (a.get("human_damage_rate", 100.0) != 100.0 or a.get("fort_damage_rate", 100.0) != 100.0
            or a.get("monster_damage_rate", 100.0) != 100.0)


def main():
    pvf = D.Pvf()
    strings = A.S.Strings(pvf) if hasattr(A, "S") else __import__("script_dump").Strings(pvf)
    import script_dump as S  # noqa: E402
    strings = S.Strings(pvf)
    list_lines = M.parse_script(pvf, strings, "monster/monster.lst")
    mob_list = {}
    for _tag, vals in list_lines:
        i = 0
        while i + 1 < len(vals):
            if INT_RE.match(vals[i]) and vals[i + 1].startswith('"'):
                mob_list.setdefault(int(vals[i]), vals[i + 1][1:-1])
                i += 2
            else:
                i += 1
    m14 = m15 = m16 = m_any = 0
    pairs = 0
    for mob_id in sorted(mob_list):
        path = M.resolve("monster/monster.lst", mob_list[mob_id])
        lines = M.parse_script(pvf, strings, path)
        if lines is None:
            continue
        mob = M.Mob(mob_id, path, lines)
        cats = runtime_cats(mob)
        if not cats:
            continue
        m_any += 1
        if 0x14 in cats:
            m14 += 1
        if 0x15 in cats:
            m15 += 1
        if 0x16 in cats:
            m16 += 1
        infos = mob.first.get("[attack info]", [])
        for rel in infos:
            if not rel.startswith('"'):
                continue
            rels = rel[1:-1]
            if not rels:
                continue
            ap = M.resolve(path, rels)
            if atk_rates_non_default(pvf, strings, ap):
                pairs += 1
    atk_total = atk_nd = 0
    for e in pvf.entries:
        if not e.path.lower().endswith(".atk"):
            continue
        atk_total += 1
        if atk_rates_non_default(pvf, strings, e.path.lower()):
            atk_nd += 1
    print("mobs_with_armor_category_0x14", m14)
    print("mobs_with_armor_category_0x15", m15)
    print("mobs_with_armor_category_0x16", m16)
    print("mobs_with_any_armor_category", m_any)
    print("atk_files_total", atk_total)
    print("atk_files_nondefault_conditional_rate", atk_nd)
    print("mob_attack_pairs_armor_mob_x_nondefault_atk", pairs)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
