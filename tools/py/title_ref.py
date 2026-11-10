#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""称号与成就定义的参考解析器。

用途:和 C# 的 TitleAppearanceScript / TitleBalloonScript / TitleBookScript / AchievementScript 做"双实现逐记录对拍"。两边各写各的,
在同一份本机 Script.pvf 上读出,逐记录比对,只报"一致 N / 不一致 M"的条数。这份实现不依赖 C# 的任何代码,结构也刻意不同:
先把标记流切成"标签 + 取值"行,图鉴脚本用显式的下标循环读,称号物品借装备那条线(equip_stats_ref.py)里已经对拍过的块深度规则。

读法(两边共同遵守的约定;出处是对全库的观察与服务端静态读码(地址见 out/title/NOTES.md),不是对服务端的转抄):
  1. 称号物品 = equipment/equipment.lst(编号取首次出现)里路径落在 equipment/character/common/title/ 下的。每件只读深度 0 的标签,
     同名后一次覆盖前一次:[equipment type] 取第一个字符串(原样);[name] 取第一个"种类+键"的引用;[name2] 取第一个引用的键;
     [custom animation] 取第一个字符串(空串当没有);[usable period] 取第一个整数(没写 0);[icon] 取第一个字符串与第一个整数(帧号,没写 -1)。
  2. 称号气泡 = etc/titleballooninfo.etc 里 [title balloon info] 后面每 6 个值一条(编号、第二个编号、静止动画、移动动画、横偏移、纵偏移),
     一条里某个值类型对不上就丢掉这一行余下的部分。
  3. 图鉴 = etc/titlebook.etc,自上而下读顶层标签:[default reward]…[/default reward]、[title collection info] "分类" 格数 { 序号 标志 [成就 个数 物品…] }、
     [section] "分类"(一页一页读,[/reward type] 结束一页,[/section] 结束整段;页的终点截到起点之后 10 格里第一个空格)、
     [custom condition message]、[adjust list](三个整数一组)、[view titlebook eventmark]、[titlebook event minlevel];别的顶层标签只计数。
  4. 成就 = n_quest/quest.lst 里 [grade] 为 [title] 的任务(最后一个认得的 [grade] 算数,没写按 [normaly repeat]):
     [type] 的原样关键字、[int data]、[level] 第一个整数、[reward type]、[reward int data] 的奖励物品表、[check count](最后一行里连续的整数)、
     [send postal](最后一行的前两个整数与前两段文字)。初始计数 = (1,0,0) 被 [check count] 前三个整数按 16 位依次覆盖。

用法(需要 DNF_DIR,见 local.env;只读,输出只写在被 git 忽略的 out/ 下):
  python3 tools/py/title_ref.py dump <文件>                   逐记录输出规范文本(与 C# 的转储逐行比对用)
  python3 tools/py/title_ref.py compare <C#转储> <Python转储> [--show]   逐记录比对,只报计数(--show 额外打印前几条不一致的键,仅本机看)
"""
import argparse
import os
import struct
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import dnfdata as D  # noqa: E402
from script_dump import Strings  # noqa: E402
import equip_stats_ref as E  # noqa: E402  只借块深度规则、浮点位模式与相对路径解析

MAGIC = b"\xb0\xd0"
EQUIPMENT_LIST = "equipment/equipment.lst"
QUEST_LIST = "n_quest/quest.lst"
TITLE_DIR = "equipment/character/common/title/"
BOOK_PATH = "etc/titlebook.etc"
BALLOON_PATH = "etc/titleballooninfo.etc"
CATEGORIES = ("general", "specific", "despair", "event")
GRADE_TITLE = 7
GRADES = {
    "[epic]": 0, "[training]": 1, "[achievement]": 2, "[daily]": 3, "[daily random]": 3, "[special daily]": 3,
    "[normaly repeat]": 4, "[normal repeat]": 4, "[common unique]": 5, "[special]": 6, "[title]": 7, "[urgent]": 8,
}


# ---------------------------------------------------------------- 标记流

def sget(strings, index):
    text = strings.get(index)
    return None if text.startswith("<bad-string-index") else text


def read_lines(raw, strings):
    """标记流 -> [(标签原样或 None, [(种类, 数, 文)])];不是标记流或正文长度不是 5 的倍数返回 None。
    种类:i 整数、x 无符号整数(取 32 位有符号值,与 C# 的 Int 一致)、f 浮点、s 字符串、p 括号词、d 分隔符、
    k 种类+键的字符串引用(数 = 种类,文 = 键)、o 其他。第一个标签之前的值挂在标签为 None 的行上。"""
    if raw is None or raw[:2] != MAGIC or (len(raw) - 2) % 5:
        return None
    toks = list(struct.iter_unpack("<Bi", raw[2:]))
    lines = []
    tag = None
    have = False
    vals = []
    i = 0
    while i < len(toks):
        ty, v = toks[i]
        if ty == 5:
            if have or vals:
                lines.append((tag, vals))
            tag = sget(strings, v)
            have = True
            vals = []
        elif ty == 2:
            vals.append(("i", v, None))
        elif ty == 3:
            vals.append(("x", v, None))
        elif ty == 4:
            vals.append(("f", E.f32(v), None))
        elif ty == 7:
            vals.append(("s", v, sget(strings, v)))
        elif ty == 6:
            vals.append(("p", v, sget(strings, v)))
        elif ty == 8:
            vals.append(("d", v, sget(strings, v)))
        elif ty == 9 and i + 1 < len(toks) and toks[i + 1][0] == 10:
            vals.append(("k", v, sget(strings, toks[i + 1][1])))
            i += 1
        else:
            vals.append(("o", v, None))
        i += 1
    if have or vals:
        lines.append((tag, vals))
    return lines


def is_num(val):
    return val[0] in ("i", "x")


def ints_of(vals):
    return [v[1] for v in vals if is_num(v)]


def first_str(vals):
    for v in vals:
        if v[0] == "s":
            return v[2]
    return None


def first_int(vals, fallback):
    for v in vals:
        if is_num(v):
            return v[1]
    return fallback


def first_text(vals):
    """第一个字符串引用 (种类, 键) 或字符串 (-1, 文);都没有返回 None。"""
    for v in vals:
        if v[0] == "k":
            return (v[1], v[2])
        if v[0] == "s":
            return (-1, v[2])
    return None


def is_tag(line, name):
    return line[0] is not None and line[0].lower() == name


def cat_of(name):
    if name is not None and name.lower() in CATEGORIES:
        return CATEGORIES.index(name.lower())
    return None


def read_list(pvf, strings, path):
    """"编号 路径"成对的清单:编号取首次出现,路径解析成规范 PVF 路径。返回 {编号: 路径};读不到返回空表。"""
    lines = read_lines(pvf.get(path), strings)
    out = {}
    for _tag, vals in lines or []:
        j = 0
        while j + 1 < len(vals):
            if is_num(vals[j]) and vals[j + 1][0] == "s":
                if vals[j][1] not in out:
                    out[vals[j][1]] = E.resolve_relative(path, vals[j + 1][2] or "")
                j += 2
            else:
                j += 1
    return out


# ---------------------------------------------------------------- 规范文本

def fmt_num(x):
    if isinstance(x, int):
        return str(x)
    if x != x:
        return "nan"
    if x in (float("inf"), float("-inf")):
        return "inf" if x > 0 else "-inf"
    if x == int(x) and abs(x) < 1e15:
        return str(int(x))
    return repr(x)


def fmt_text(t):
    return "-" if t is None or not t[1] else "%d:%s" % (t[0], t[1])


def fmt_opt(s):
    return "-" if s is None or s == "" else s


def fmt_reward(r):
    if r[0] == "E":
        return "E,%d" % r[1]
    if r[0] == "P":
        return "P,%d,%d,%s" % (r[1], r[2], ";".join(fmt_text(t) for t in r[3]) or "-")
    return "S,%s,%s,%s" % (r[1], fmt_opt(r[2]), fmt_num(r[3]))


def fmt_rewards(rewards):
    return "|".join(fmt_reward(r) for r in rewards) or "-"


# ---------------------------------------------------------------- 称号物品与气泡

def read_appearance(raw, strings):
    """一件称号物品;不是标记流返回 None。只认深度 0(块规则借 equip_stats_ref.classify)。"""
    lines = read_lines(raw, strings)
    if lines is None:
        return None
    classified, _balanced, _stray = E.classify([(None if t is None else t.lower(), v) for t, v in lines])
    f = {"part": None, "name": None, "name2": None, "anim": None, "period": 0, "icon": None, "frame": -1}
    for depth, tag, vals in classified:
        if depth != 0 or tag is None:
            continue
        if tag == "[name]":
            for v in vals:
                if v[0] == "k":
                    f["name"] = (v[1], v[2])
                    break
        elif tag == "[name2]":
            for v in vals:
                if v[0] == "k":
                    f["name2"] = v[2]
                    break
        elif tag == "[equipment type]":
            f["part"] = first_str(vals)
        elif tag == "[custom animation]":
            f["anim"] = first_str(vals) or None
        elif tag == "[usable period]":
            nums = ints_of(vals)
            f["period"] = nums[0] if nums else 0
        elif tag == "[icon]":
            f["icon"] = first_str(vals)
            nums = ints_of(vals)
            f["frame"] = nums[0] if nums else -1
    return f


def fmt_appearance(code, f):
    if f is None:
        return "T\t%d\t<none>" % code
    name = "-" if f["name"] is None or f["name"][1] is None else "%d:%s" % f["name"]
    return "T\t%d\t%s\t%s\t%s\t%s\t%d\t%s:%d" % (
        code, fmt_opt(f["part"]), name, fmt_opt(f["name2"]), fmt_opt(f["anim"]), f["period"], fmt_opt(f["icon"]), f["frame"])


def read_balloons(lines):
    out = []
    for tag, vals in lines or []:
        if tag is None or tag.lower() != "[title balloon info]":
            continue
        for at in range(0, len(vals) - 5, 6):
            v = vals[at:at + 6]
            if not (is_num(v[0]) and is_num(v[1]) and v[2][0] == "s" and v[3][0] == "s" and is_num(v[4]) and is_num(v[5])):
                break
            out.append((v[0][1], v[1][1], v[2][2], v[3][2], v[4][1], v[5][1]))
    return out


# ---------------------------------------------------------------- 图鉴

def add_reward(into, tag, vals):
    if tag is None:
        return
    t = tag.lower()
    if t == "[give emotion]":
        into.append(("E", first_int(vals, 0)))
    elif t == "[send postal]":
        nums = ints_of(vals)
        texts = []
        for v in vals:
            if v[0] == "k":
                texts.append((v[1], v[2]))
            elif v[0] == "s":
                texts.append((-1, v[2]))
        into.append(("P", nums[0] if nums else 0, nums[1] if len(nums) > 1 else 0, texts))
    elif len(vals) >= 2 and vals[0][0] == "s" and (is_num(vals[1]) or vals[1][0] == "f"):
        into.append(("S", tag, vals[0][2], vals[1][1]))


def read_collection(book, vals):
    cat = cat_of(first_str(vals))
    if cat is None:
        book["unknown"] += 1
        return
    nums = ints_of(vals)
    if not nums:
        book["trunc"] += 1
        return
    slots = book["slots"][cat]
    at = 1
    for _ in range(nums[0]):
        if at + 2 > len(nums):
            book["trunc"] += 1
            return
        index, flag = nums[at], nums[at + 1]
        at += 2
        ach, items = -1, []
        if flag != -1:
            if at + 2 > len(nums):
                book["trunc"] += 1
                return
            ach, count = nums[at], nums[at + 1]
            at += 2
            for _j in range(count):
                if at >= len(nums):
                    book["trunc"] += 1
                    return
                items.append(nums[at])
                at += 1
        if index != len(slots):
            book["mismatch"] += 1
        slots.append({"flag": flag, "ach": ach, "items": items})


def new_page(cat, page):
    return {"cat": cat, "page": page, "start": page * 10, "end": page * 10 + 10, "sname": None, "sicon": None, "sframe": 0,
            "frametype": 0, "lock": None, "rname": None, "image": None, "iframe": 0, "effect": None, "rewards": []}


def read_page(sec, lines, i):
    """读一页;返回 (下一行的下标, 是否以 [/reward type] 结束)。以 [/section] 或文件结束收尾都算"没读成"。"""
    while i < len(lines):
        tag, vals = lines[i]
        i += 1
        if tag is None:
            continue
        t = tag.lower()
        if t == "[/section]":
            return i, False
        if t == "[/reward type]":
            return i, True
        if t == "[section name]":
            sec["sname"] = first_text(vals)
        elif t == "[section icon]":
            sec["sicon"] = first_str(vals)
            sec["sframe"] = first_int(vals, 0)
        elif t == "[frame type]":
            sec["frametype"] = first_int(vals, 0)
        elif t == "[lock message]":
            sec["lock"] = first_text(vals)
        elif t == "[name]":
            sec["rname"] = first_text(vals)
        elif t == "[image path]":
            sec["image"] = first_str(vals)
            sec["iframe"] = first_int(vals, 0)
        elif t == "[effect path]":
            sec["effect"] = first_str(vals)
        else:
            add_reward(sec["rewards"], tag, vals)
    return i, False


def read_sections(book, lines, i, head_vals):
    cat = cat_of(first_str(head_vals))
    if cat is None:
        book["unknown"] += 1
    while i < len(lines):
        page = sum(1 for p in book["pages"] if p["cat"] == cat) if cat is not None else 0
        sec = new_page(cat if cat is not None else 0, page)
        i, ended = read_page(sec, lines, i)
        if not ended:
            break
        if cat is None:
            continue
        slots = book["slots"][cat]
        if slots:
            for k in range(sec["start"], sec["start"] + 10):
                if k < len(slots) and slots[k]["flag"] == -1:
                    sec["end"] = k
                    break
        book["pages"].append(sec)
    return i


def read_book(lines):
    book = {"slots": [[], [], [], []], "pages": [], "default": [], "msgs": [], "adjusts": [], "view": False,
            "has_min": False, "min": 0, "skipped": 0, "trunc": 0, "mismatch": 0, "unknown": 0}
    n = len(lines or [])
    i = 0
    while i < n:
        tag, vals = lines[i]
        i += 1
        if tag is None:
            continue
        t = tag.lower()
        if t == "[default reward]":
            while i < n and not is_tag(lines[i], "[/default reward]"):
                add_reward(book["default"], lines[i][0], lines[i][1])
                i += 1
            if i < n:
                i += 1
        elif t == "[title collection info]":
            read_collection(book, vals)
        elif t == "[section]":
            i = read_sections(book, lines, i, vals)
        elif t == "[custom condition message]":
            for at in range(0, len(vals) - 2, 3):
                head, slot, text = vals[at], vals[at + 1], vals[at + 2]
                cat = cat_of(head[2]) if head[0] == "s" else None
                if head[0] != "s" or not is_num(slot) or cat is None:
                    break
                book["msgs"].append((cat, slot[1], (text[1], text[2]) if text[0] == "k" else (-1, text[2]) if text[0] == "s" else None))
        elif t == "[adjust list]":
            nums = ints_of(vals)
            for at in range(0, len(nums) - 2, 3):
                book["adjusts"].append(tuple(nums[at:at + 3]))
        elif t == "[view titlebook eventmark]":
            nums = ints_of(vals)
            book["view"] = bool(nums) and nums[0] != 0
        elif t == "[titlebook event minlevel]":
            nums = ints_of(vals)
            if nums:
                book["has_min"] = True
                book["min"] = nums[0]
        else:
            book["skipped"] += 1
    return book


def fmt_book(book):
    rows = []
    for cat in range(4):
        for index, slot in enumerate(book["slots"][cat]):
            rows.append("B\tslot\t%d\t%d\t%d\t%d\t%s" % (cat, index, slot["flag"], slot["ach"], ",".join(str(x) for x in slot["items"]) or "-"))
    for p in book["pages"]:
        rows.append("B\tpage\t%d\t%d\t%d\t%d\t%s\t%s:%d\t%d\t%s\t%s\t%s:%d\t%s\t%s" % (
            p["cat"], p["page"], p["start"], p["end"], fmt_text(p["sname"]), fmt_opt(p["sicon"]), p["sframe"], p["frametype"],
            fmt_text(p["lock"]), fmt_text(p["rname"]), fmt_opt(p["image"]), p["iframe"], fmt_opt(p["effect"]), fmt_rewards(p["rewards"])))
    rows.append("B\tdefault\t%s" % fmt_rewards(book["default"]))
    for n, (cat, slot, text) in enumerate(book["msgs"]):
        rows.append("B\tmsg\t%d\t%d\t%d\t%s" % (n, cat, slot, fmt_text(text)))
    for n, (q, item, ach) in enumerate(book["adjusts"]):
        rows.append("B\tadjust\t%d\t%d\t%d\t%d" % (n, q, item, ach))
    rows.append("B\tmisc\tview=%d;minlevel=%s;skipped=%d;trunc=%d;mismatch=%d;unknowncat=%d" % (
        1 if book["view"] else 0, str(book["min"]) if book["has_min"] else "-", book["skipped"], book["trunc"], book["mismatch"], book["unknown"]))
    return rows


# ---------------------------------------------------------------- 成就

def read_reward_items(vals, into):
    """奖励物品表:每项 编号 数量,或 编号 "[job]" 职业 转职方向 数量;不成形就停(只留 (编号, 数量))。"""
    i = 0
    while i < len(vals):
        if not is_num(vals[i]):
            i += 1
            continue
        code = vals[i][1]
        i += 1
        if i < len(vals) and vals[i][0] == "s":
            job_form = (vals[i][2] or "").lower() == "[job]"
            i += 1
            if job_form:
                if i + 1 >= len(vals) or not is_num(vals[i]) or not is_num(vals[i + 1]):
                    return
                i += 2
        if i >= len(vals) or not is_num(vals[i]):
            return
        into.append((code, vals[i][1]))
        i += 1


def read_quest(lines):
    """一个任务里和成就有关的字段。"""
    q = {"grade": 4, "kind": None, "int_data": [], "min_level": 1, "reward_type": "[item]", "rewards": [], "check": [], "postal": None}
    for tag, vals in lines:
        if tag is None:
            continue
        t = tag.lower()
        if t == "[grade]":
            g = GRADES.get((first_str(vals) or "").lower())
            if g is not None:
                q["grade"] = g
        elif t == "[type]":
            q["kind"] = first_str(vals)
        elif t == "[int data]":
            q["int_data"] = ints_of(vals)
        elif t == "[level]":
            nums = ints_of(vals)
            if nums:
                q["min_level"] = nums[0]
        elif t == "[reward type]":
            s = first_str(vals)
            if s is not None:
                q["reward_type"] = s
        elif t == "[reward int data]":
            read_reward_items(vals, q["rewards"])
        elif t == "[check count]":
            counts = []
            for v in vals:
                if not is_num(v):
                    break
                counts.append(v[1])
            q["check"] = counts
        elif t == "[send postal]":
            nums = ints_of(vals)
            texts = []
            for v in vals:
                if v[0] == "k":
                    texts.append((v[1], v[2]))
                elif v[0] == "s":
                    texts.append((-1, v[2]))
            q["postal"] = (nums[0] if nums else 0, nums[1] if len(nums) > 1 else 0, texts[0] if texts else None, texts[1] if len(texts) > 1 else None)
    return q


def fmt_achievement(qid, q):
    clear = q["int_data"][0] if (q["kind"] or "").lower() == "[clear quest]" and q["int_data"] else 0
    init = [1, 0, 0]
    for i in range(min(3, len(q["check"]))):
        init[i] = q["check"][i] & 0xFFFF
    title = q["rewards"][0][0] if q["rewards"] and q["reward_type"].lower() == "[item]" else 0
    p = q["postal"] or (0, 0, None, None)
    return "A\t%d\t%s\t%d\t%s\t%s\t%d\t%d,%d,%s,%s\t%d" % (
        qid, fmt_opt(q["kind"]), clear, ",".join(str(c) for c in q["check"]) or "-", ",".join(str(c) for c in init), title,
        p[0], p[1], fmt_text(p[2]), fmt_text(p[3]), q["min_level"])


# ---------------------------------------------------------------- 转储

def cmd_dump(pvf, strings, out_path):
    rows = []
    # 称号物品
    items = read_list(pvf, strings, EQUIPMENT_LIST)
    codes = sorted(c for c, p in items.items() if p.startswith(TITLE_DIR))
    unreadable = 0
    for code in codes:
        f = read_appearance(pvf.get(items[code]), strings)
        unreadable += f is None
        rows.append(fmt_appearance(code, f))
    # 气泡
    for n, b in enumerate(read_balloons(read_lines(pvf.get(BALLOON_PATH), strings))):
        rows.append("L\t%d\t%d\t%d\t%s\t%s\t%d\t%d" % (n, b[0], b[1], fmt_opt(b[2]), fmt_opt(b[3]), b[4], b[5]))
    # 图鉴
    rows.extend(fmt_book(read_book(read_lines(pvf.get(BOOK_PATH), strings))))
    # 成就
    quests = read_list(pvf, strings, QUEST_LIST)
    achievements = 0
    for qid in sorted(quests):
        lines = read_lines(pvf.get(quests[qid]), strings)
        if lines is None:
            continue
        q = read_quest(lines)
        if q["grade"] == GRADE_TITLE:
            rows.append(fmt_achievement(qid, q))
            achievements += 1
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        for row in rows:
            f.write(row + "\n")
    print("写出 %d 行(称号物品 %d 件,读不出 %d;成就 %d 个)-> %s" % (len(rows), len(codes), unreadable, achievements, out_path))


# ---------------------------------------------------------------- 比对

def split_key(line):
    """(记录类别, 键, 余下的文本)。"""
    f = line.split("\t")
    if f[0] == "T":
        return "T", f[1], "\t".join(f[2:])
    if f[0] == "L":
        return "L", f[1], "\t".join(f[2:])
    if f[0] == "A":
        return "A", f[1], "\t".join(f[2:])
    if f[1] in ("slot", "page"):
        return "B." + f[1], "%s,%s" % (f[2], f[3]), "\t".join(f[4:])
    if f[1] in ("msg", "adjust"):
        return "B." + f[1], f[2], "\t".join(f[3:])
    return "B." + f[1], "-", "\t".join(f[2:])


def canon(text):
    """比对用的规范化:奖励里的数值按浮点比(两边的整数/小数写法可能不同)。"""
    out = []
    for field in text.split("\t"):
        parts = []
        for item in field.split("|"):
            if item.startswith("S,"):
                head, _, num = item.rpartition(",")
                try:
                    item = head + "," + repr(float(num))
                except ValueError:
                    pass
            parts.append(item)
        out.append("|".join(parts))
    return "\t".join(out)


def load_dump(path):
    rows = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if line:
                kind, key, rest = split_key(line)
                rows[(kind, key)] = rest
    return rows


def cmd_compare(cs_path, py_path, show):
    cs = load_dump(cs_path)
    py = load_dump(py_path)
    kinds = sorted({k for k, _ in cs} | {k for k, _ in py})
    ok = Counter()
    bad = Counter()
    only_cs = Counter()
    only_py = Counter()
    bad_keys = {}
    for kind, key in sorted(set(cs) | set(py)):
        if (kind, key) not in py:
            only_cs[kind] += 1
        elif (kind, key) not in cs:
            only_py[kind] += 1
        elif canon(cs[(kind, key)]) == canon(py[(kind, key)]):
            ok[kind] += 1
        else:
            bad[kind] += 1
            bad_keys.setdefault(kind, []).append(key)
    label = {"T": "称号物品", "L": "称号气泡", "A": "成就", "B.slot": "图鉴格", "B.page": "图鉴页", "B.default": "默认奖励", "B.msg": "条件说明",
             "B.adjust": "补发条目", "B.misc": "杂项"}
    total_bad = 0
    for kind in kinds:
        n_bad = bad[kind] + only_cs[kind] + only_py[kind]
        total_bad += n_bad
        print("%-8s 一致 %d / 不一致 %d(只在 C# %d,只在 Python %d)" % (label.get(kind, kind), ok[kind], bad[kind], only_cs[kind], only_py[kind]))
    print("合计:一致 %d / 不一致 %d" % (sum(ok.values()), total_bad))
    if show:
        for kind, keys in bad_keys.items():
            print("%s 不一致的前 5 个键:%s" % (kind, keys[:5]))
    return 0 if total_bad == 0 and sum(ok.values()) > 0 else 1


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
