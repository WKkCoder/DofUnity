#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""装备属性与外观图层的参考解析器。

用途:和 C# 的 EquipStats / EquipLook 读法做"双实现逐字段对拍"。两边各写各的,在同一份本机 Script.pvf 上按同一张物品清单
(equipment/equipment.lst,编号取首次出现、路径相对 equipment/ 目录)逐件读出,逐字段比对,只报"一致 N / 不一致 M"的件数。
这份实现不依赖 C# 的任何代码,结构也刻意不同:先把标记流切成"标签 + 取值"行,再用块栈标出每一行的深度,最后按深度 0 取属性。

读法(两边共同遵守的约定;出处是对全库的观察(2026-11 普查),不是对服务端读码的转抄):
  1. 标记流:标签(类型 5)后面跟着它的取值,直到下一个标签。
  2. 块:文件里同时有 [x] 与 [/x] 的 [x] 才算块的开头。块里面(深度 > 0)的标签不是装备自己的属性
     (套装、等级段、头像选择……的块里也会写同名属性标签)。[/x] 弹到栈里最近的 [x];栈里没有 [x] 的 [/x] 忽略。
  3. 只记深度 0 的属性标签;同名标签后一次覆盖前一次;取值是标签后面的数值标记(整数、无符号整数、浮点),
     只留前两个;一个数值都没有的行不覆盖之前的。
  4. 外观图层只在武器上读([equipment type] 的字符串取最后一次出现的那个,等于 [weapon] 才读),也只读深度 0
     (块里的外观标签是 [level section ani]、[expand ani] 里的备选外观,不是基础外观;只数个数,记在"块内外观标签数"里):
     [animation job] 开一段,取后面的字符串;[variation] 取前两个整数(后者覆盖前者);[layer variation] 取一个整数加一个字符串,
     在本段追加一层;[equipment ani script] 取一个字符串,在本段追加一个脚本;[sub type] 取第一个整数(同名标签第一次的算)。
     层与脚本分开存,不做配对(全库每段两者个数相等,只有一段不是严格交替,配对规则没有依据)。
     第一个 [animation job] 之前出现的 [variation]/[layer variation]/[equipment ani script] 归在"职业为空"的段里。

用法(需要 DNF_DIR,见 local.env;只读,输出只写在被 git 忽略的 out/ 下):
  python3 tools/py/equip_stats_ref.py survey                 整库勘察:各属性标签的分布(只打印计数)
  python3 tools/py/equip_stats_ref.py dump <文件>             逐件输出规范文本,与 C# 的转储逐行比对用
  python3 tools/py/equip_stats_ref.py compare <C#转储> <Python转储> [--show]   逐字段比对,只报计数(--show 额外打印前几个不一致的编号,仅本机看)
"""
import argparse
import os
import struct
import sys
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import dnfdata as D  # noqa: E402
from script_dump import Strings  # noqa: E402

MAGIC = b"\xb0\xd0"
T_INT, T_UINT, T_FLOAT, T_TAG, T_PAREN, T_STRING, T_SEP, T_REFKIND, T_REFKEY = range(2, 11)

LIST_PATH = "equipment/equipment.lst"

# 收进属性表的标签。依据:服务端 importEquipmentParameterInfo 读成纯数值的标签 ∩ 数据里深度 0 纯数值的标签,
# 去掉价格、冷却、概率、技能附加体一类不是"穿上身加属性"的;外加服务端不读、只有客户端用的生命魔法类(.chr 里同名)。
# 被排除的标签清单是本机普查的输出,不入库。分组只是给人看的,比对不用。
STAT_TAGS = (
    # 与 .chr 里九项属性同名(StatBlock)
    "[hp max]", "[mp max]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]",
    "[inventory limit]", "[mp regen speed]", "[hit recovery]",
    # 装备自带的攻击防御(服务端读两个数)
    "[equipment physical attack]", "[equipment magical attack]", "[equipment physical defense]", "[equipment magical defense]",
    "[separate attack]",
    # 速度、暴击、硬直等
    "[attack speed]", "[cast speed]", "[move speed]", "[jump power]", "[stuck]", "[stuck resistance]",
    "[physical critical hit]", "[magical critical hit]", "[hp regen speed]", "[anti evil]",
    # 与 .chr 属性同名族的"比率"(只汇总,不折进属性块)
    "[mp max rate]", "[mp regen speed rate]",
    # 属性强化与抗性
    "[fire attack]", "[water attack]", "[dark attack]", "[light attack]", "[all elemental attack]",
    "[fire resistance]", "[water resistance]", "[dark resistance]", "[light resistance]", "[all elemental resistance]",
    # 异常状态抗性
    "[freeze resistance]", "[poison resistance]", "[bleeding resistance]", "[stun resistance]", "[confuse resistance]",
    "[curse resistance]", "[lightning resistance]", "[slow resistance]", "[blind resistance]", "[burn resistance]",
    "[sleep resistance]", "[stone resistance]", "[hold resistance]", "[piercing resistance]", "[all activestatus resistance]",
)
STAT_SET = frozenset(STAT_TAGS)
TWO_NUMBER_TAGS = (
    "[equipment physical attack]", "[equipment magical attack]", "[equipment physical defense]", "[equipment magical defense]",
    "[separate attack]",
)
LOOK_TAGS = ("[animation job]", "[variation]", "[layer variation]", "[equipment ani script]", "[sub type]")


# ---------------------------------------------------------------- 读取

def norm_path(path):
    """小写、'\\' 换成 '/'、折叠 '.' 与 '..'、去掉首部的 '/'(与 C# 的 PvfPath.Normalize 同义)。"""
    parts = []
    for part in path.replace("\\", "/").lower().split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if parts:
                parts.pop()
            continue
        parts.append(part)
    return "/".join(parts)


def resolve_relative(script_path, relative):
    rel = relative.replace("\\", "/")
    if rel.startswith("/"):
        return norm_path(rel)
    script = script_path.replace("\\", "/")
    slash = script.rfind("/")
    return norm_path((script[: slash + 1] if slash >= 0 else "") + rel)


def f32(bits):
    return struct.unpack("<f", struct.pack("<i", bits))[0]


def read_items(pvf, strings):
    """equipment.lst 里"编号 路径"成对的清单:编号取首次出现,路径解析成规范 PVF 路径。返回 {编号: 路径}。"""
    raw = pvf.get(LIST_PATH)
    if raw is None or raw[:2] != MAGIC:
        raise SystemExit("读不到 " + LIST_PATH)
    toks = list(struct.iter_unpack("<Bi", raw[2:]))
    items = {}
    i = 0
    while i + 1 < len(toks):
        if toks[i][0] in (T_INT, T_UINT, T_FLOAT) and toks[i + 1][0] == T_STRING:
            code = toks[i][1]
            if code not in items:
                items[code] = resolve_relative(LIST_PATH, strings.get(toks[i + 1][1]))
            i += 2
        else:
            i += 1
    return items


def split_lines(raw, strings):
    """标记流 -> [(小写标签或 None, [(种类, 取值)])]。种类:i 整数、u 无符号整数、f 浮点、s 字符串、p 括号词、d 分隔符、k 字符串引用。
    第一个标签之前的标记丢弃。字符串池下标越界的标签记成 None(与 C# 一致:不是任何已知标签)。"""
    lines = []
    cur = None
    for ty, v in struct.iter_unpack("<Bi", raw[2:]):
        if ty == T_TAG:
            text = strings.get(v)
            cur = (None if text.startswith("<bad-string-index") else text.lower(), [])
            lines.append(cur)
        elif cur is None:
            continue
        elif ty == T_INT:
            cur[1].append(("i", v))
        elif ty == T_UINT:
            cur[1].append(("u", v & 0xFFFFFFFF))
        elif ty == T_FLOAT:
            cur[1].append(("f", f32(v)))
        elif ty == T_STRING:
            cur[1].append(("s", strings.get(v)))
        elif ty == T_PAREN:
            cur[1].append(("p", strings.get(v)))
        elif ty == T_SEP:
            cur[1].append(("d", ","))
        else:
            cur[1].append(("k", v))
    return lines


def classify(lines):
    """给每一行标上深度:[(深度, 标签, 取值)]。块规则见文件头第 2 条。返回时顺带给出"文件结束时栈是否为空"和"没有对应开头的收尾标签数"。"""
    openers = set()
    for tag, _ in lines:
        if tag is not None and tag.startswith("[/"):
            openers.add("[" + tag[2:])
    stack = []
    out = []
    stray = 0
    for tag, vals in lines:
        if tag is None:
            out.append((len(stack), None, vals))
            continue
        if tag.startswith("[/"):
            name = "[" + tag[2:]
            if name in stack:
                while stack.pop() != name:
                    pass
            else:
                stray += 1
            out.append((len(stack), tag, vals))
            continue
        out.append((len(stack), tag, vals))
        if tag in openers:
            stack.append(tag)
    return out, len(stack) == 0, stray


def numbers(vals):
    return [p for k, p in vals if k in ("i", "u", "f")]


def stats_of(classified):
    """深度 0 的属性:{标签: [第一个数, 第二个数?]}。"""
    stats = {}
    for depth, tag, vals in classified:
        if depth == 0 and tag in STAT_SET:
            nums = numbers(vals)
            if nums:
                stats[tag] = nums[:2]
    return stats


def part_of(classified):
    """[equipment type] 的第一个字符串(小写),取最后一次出现的;没有返回 None。"""
    part = None
    for _depth, tag, vals in classified:
        if tag == "[equipment type]":
            text = next((p for k, p in vals if k == "s"), None)
            if text is not None:
                part = text.lower()
    return part


def look_of(classified):
    """武器的外观数据:(子类型或 None, 块内外观标签数, [段]);段 = {job, var, layers:[(序, 名)], scripts:[路径]}。"""
    segs = []
    cur = None
    sub = None
    nested = 0
    for depth, tag, vals in classified:
        if tag not in LOOK_TAGS:
            continue
        if depth != 0:
            nested += 1
            continue
        if tag == "[animation job]":
            cur = {"job": next((p for k, p in vals if k == "s"), None), "var": None, "layers": [], "scripts": []}
            segs.append(cur)
            continue
        if tag == "[sub type]":
            ints = [p for k, p in vals if k in ("i", "u")]
            if ints and sub is None:
                sub = ints[0]
            continue
        if cur is None:
            cur = {"job": None, "var": None, "layers": [], "scripts": []}
            segs.append(cur)
        if tag == "[variation]":
            ints = [p for k, p in vals if k in ("i", "u")]
            if len(ints) >= 2:
                cur["var"] = (ints[0], ints[1])
        elif tag == "[layer variation]":
            ints = [p for k, p in vals if k in ("i", "u")]
            text = next((p for k, p in vals if k == "s"), None)
            if ints and text is not None:
                cur["layers"].append((ints[0], text))
        elif tag == "[equipment ani script]":
            text = next((p for k, p in vals if k == "s"), None)
            if text is not None:
                cur["scripts"].append(text)
    return sub, nested, segs


# ---------------------------------------------------------------- 规范文本

def fmt_number(x):
    if isinstance(x, float):
        return repr(x)
    return str(x)


def fmt_stats(stats):
    if not stats:
        return "-"
    return ";".join(t + "=" + ",".join(fmt_number(n) for n in stats[t]) for t in sorted(stats))


def fmt_look(look):
    sub, nested, segs = look
    parts = ["sub=%s;nested=%d" % ("-" if sub is None else str(sub), nested)]
    for seg in segs:
        var = "-" if seg["var"] is None else "%d,%d" % seg["var"]
        layers = ",".join("%d:%s" % (o, n) for o, n in seg["layers"]) or "-"
        scripts = ",".join(seg["scripts"]) or "-"
        parts.append("J%s;V%s;L%s;S%s" % ("-" if seg["job"] is None else seg["job"], var, layers, scripts))
    return "|".join(parts)


def read_one(pvf, strings, path):
    """读一件;文件缺失或不是标记流返回 None。返回 (属性, 外观或 None, 块统计)。"""
    raw = pvf.get(path)
    if raw is None or raw[:2] != MAGIC:
        return None
    classified, balanced, stray = classify(split_lines(raw, strings))
    look = look_of(classified) if part_of(classified) == "[weapon]" else None
    return stats_of(classified), look, classified, balanced, stray


def cmd_dump(pvf, strings, out_path):
    items = read_items(pvf, strings)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    missing = 0
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        for code in sorted(items):
            r = read_one(pvf, strings, items[code])
            if r is None:
                missing += 1
                f.write("%d\t<none>\t<none>\n" % code)
                continue
            stats, look, _c, _b, _s = r
            f.write("%d\t%s\t%s\n" % (code, fmt_stats(stats), "-" if look is None else fmt_look(look)))
    print("写出 %d 件(读不到 %d 件)-> %s" % (len(items), missing, out_path))


# ---------------------------------------------------------------- 比对

def parse_stats(text):
    if text in ("-", "<none>"):
        return text
    out = {}
    for item in text.split(";"):
        tag, _, nums = item.rpartition("=")
        out[tag] = [float(n) for n in nums.split(",")]
    return out


def same_stats(a, b):
    if isinstance(a, str) or isinstance(b, str):
        return a == b
    if a.keys() != b.keys():
        return False
    for tag in a:
        if len(a[tag]) != len(b[tag]):
            return False
        for x, y in zip(a[tag], b[tag]):
            if not (x == y or (x != x and y != y)):
                return False
    return True


def load_dump(path):
    rows = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            code, stats, look = line.split("\t")
            rows[int(code)] = (stats, look)
    return rows


def cmd_compare(cs_path, py_path, show):
    cs = load_dump(cs_path)
    py = load_dump(py_path)
    only_cs = sorted(set(cs) - set(py))
    only_py = sorted(set(py) - set(cs))
    both = sorted(set(cs) & set(py))
    stat_ok = stat_bad = look_ok = look_bad = 0
    bad_stat_codes, bad_look_codes = [], []
    weapons = 0
    tag_bad = Counter()
    for code in both:
        a_stats, a_look = cs[code]
        b_stats, b_look = py[code]
        sa, sb = parse_stats(a_stats), parse_stats(b_stats)
        if same_stats(sa, sb):
            stat_ok += 1
        else:
            stat_bad += 1
            bad_stat_codes.append(code)
            if not isinstance(sa, str) and not isinstance(sb, str):
                for t in set(sa) | set(sb):
                    if sa.get(t) != sb.get(t):
                        tag_bad[t] += 1
        if b_look != "-":
            weapons += 1
        if a_look == b_look:
            look_ok += 1
        else:
            look_bad += 1
            bad_look_codes.append(code)
    print("比对 %d 件(C# %d 行,Python %d 行;只在 C# 有 %d,只在 Python 有 %d)" % (len(both), len(cs), len(py), len(only_cs), len(only_py)))
    print("属性字段:一致 %d / 不一致 %d" % (stat_ok, stat_bad))
    print("外观字段:一致 %d / 不一致 %d(其中武器 %d 件)" % (look_ok, look_bad, weapons))
    if tag_bad:
        print("不一致的属性标签(按件数):" + ", ".join("%s %d" % kv for kv in tag_bad.most_common(8)))
    if show:
        print("属性不一致的前 5 个编号:", bad_stat_codes[:5])
        print("外观不一致的前 5 个编号:", bad_look_codes[:5])
    return 0 if stat_bad == 0 and look_bad == 0 and not only_cs and not only_py else 1


# ---------------------------------------------------------------- 勘察

def shape_of(vals):
    return "".join(k for k, _p in vals)


def top(counter, n=3):
    return ",".join("%s:%d" % kv for kv in counter.most_common(n)) or "-"


def cmd_survey(pvf, strings):
    items = read_items(pvf, strings)
    listed = len(items)
    missing = notscript = 0
    unbalanced = 0
    stray_files = 0
    tag_files = Counter()
    tag_d0 = Counter()
    tag_nested = Counter()
    tag_dupfiles = Counter()
    tag_dupdiff = Counter()
    tag_empty = Counter()
    tag_nonnum = Counter()
    tag_uint = Counter()
    tag_shapes = defaultdict(Counter)
    tag_extra = Counter()          # 数值多于两个的行
    tag_is_opener = Counter()      # 属性标签同时是块开头的文件数
    other_numeric = defaultdict(lambda: [0, Counter()])
    second = defaultdict(Counter)
    look_nested = Counter()
    look_d0 = Counter()
    weapons = 0
    weapons_nested_look = 0
    weapon_segs = Counter()
    segs_script_mismatch = 0
    segs_not_alternating = 0
    pre_job_segments = 0
    sub_multi = sub_multi_diff = 0
    var_shapes = Counter()
    layer_shapes = Counter()
    script_shapes = Counter()
    job_shapes = Counter()
    non_weapon_with_look = 0
    wpn_stats = Counter()
    for code in sorted(items):
        raw = pvf.get(items[code])
        if raw is None:
            missing += 1
            continue
        if raw[:2] != MAGIC:
            notscript += 1
            continue
        lines = split_lines(raw, strings)
        classified, balanced, stray = classify(lines)
        if not balanced:
            unbalanced += 1
        if stray:
            stray_files += 1
        closers = {"[" + t[2:] for t, _ in lines if t is not None and t.startswith("[/")}
        seen0 = defaultdict(list)
        for depth, tag, vals in classified:
            if tag is None:
                continue
            if tag in STAT_SET:
                if depth > 0:
                    tag_nested[tag] += 1
                    continue
                nums = numbers(vals)
                if tag in closers:
                    tag_is_opener[tag] += 1
                if not nums:
                    tag_empty[tag] += 1
                    continue
                tag_d0[tag] += 1
                tag_shapes[tag][shape_of(vals)] += 1
                if len(nums) != len(vals):
                    tag_nonnum[tag] += 1
                if any(k == "u" for k, _p in vals):
                    tag_uint[tag] += 1
                if len(nums) > 2:
                    tag_extra[tag] += 1
                seen0[tag].append(nums)
            elif depth == 0 and tag not in LOOK_TAGS:
                nums = numbers(vals)
                if nums and len(nums) == len(vals):
                    rec = other_numeric[tag]
                    rec[0] += 1
                    rec[1][shape_of(vals)] += 1
            if tag in LOOK_TAGS:
                (look_nested if depth > 0 else look_d0)[tag] += 1
        for tag, occ in seen0.items():
            tag_files[tag] += 1
            if len(occ) > 1:
                tag_dupfiles[tag] += 1
                if any(o != occ[-1] for o in occ[:-1]):
                    tag_dupdiff[tag] += 1
            if tag in TWO_NUMBER_TAGS and len(occ[-1]) >= 2:
                a, b = occ[-1][0], occ[-1][1]
                second[tag]["b==a" if b == a else "b==0" if b == 0 else "b>a" if b > a else "b<a"] += 1
        if part_of(classified) == "[weapon]":
            weapons += 1
            _sub, nested, segs = look_of(classified)
            weapon_segs[len(segs)] += 1
            if nested:
                weapons_nested_look += 1
            for seg in segs:
                if seg["job"] is None:
                    pre_job_segments += 1
            wpn_stats[len(stats_of(classified)) > 0] += 1
        elif any(t == "[animation job]" for _d, t, _v in classified):
            non_weapon_with_look += 1
        # 外观形状与配对(全部带 [animation job] 的文件,不限武器)
        cur_layers = cur_scripts = 0
        order = []
        subs = []
        for depth, tag, vals in classified:
            if depth != 0:
                continue
            if tag == "[animation job]":
                if order:
                    segs_script_mismatch += cur_layers != cur_scripts
                    segs_not_alternating += any(order[i] == order[i + 1] for i in range(len(order) - 1)) or (order and order[0] != "L")
                cur_layers = cur_scripts = 0
                order = []
                job_shapes[shape_of(vals)] += 1
            elif tag == "[variation]":
                var_shapes[shape_of(vals)] += 1
            elif tag == "[layer variation]":
                layer_shapes[shape_of(vals)] += 1
                cur_layers += 1
                order.append("L")
            elif tag == "[equipment ani script]":
                script_shapes[shape_of(vals)] += 1
                cur_scripts += 1
                order.append("S")
            elif tag == "[sub type]":
                subs.append(numbers(vals)[:1])
        if order:
            segs_script_mismatch += cur_layers != cur_scripts
            segs_not_alternating += any(order[i] == order[i + 1] for i in range(len(order) - 1)) or (order and order[0] != "L")
        if len(subs) > 1:
            sub_multi += 1
            if any(s != subs[0] for s in subs[1:]):
                sub_multi_diff += 1
    print("清单 %d 件;文件缺失 %d;不是标记流 %d;块结构:文件结束时栈未清空 %d 件,含没有开头的收尾标签 %d 件" % (listed, missing, notscript, unbalanced, stray_files))
    print()
    print("%-34s %7s %7s %7s %6s %6s %6s %6s %6s  %s" % ("属性标签", "文件", "深度0", "嵌套", "重复", "重复异", "空行", "非数值", "多数", "取值形状(前3)"))
    for tag in STAT_TAGS:
        print("%-34s %7d %7d %7d %6d %6d %6d %6d %6d  %s%s%s" % (
            tag, tag_files[tag], tag_d0[tag], tag_nested[tag], tag_dupfiles[tag], tag_dupdiff[tag], tag_empty[tag],
            tag_nonnum[tag], tag_extra[tag], top(tag_shapes[tag]),
            ("  [无符号 %d]" % tag_uint[tag]) if tag_uint[tag] else "", ("  [同时是块开头 %d]" % tag_is_opener[tag]) if tag_is_opener[tag] else ""))
    print()
    print("两个数的标签,第二个数与第一个数的关系(最后一次出现,只数件数):")
    for tag in TWO_NUMBER_TAGS:
        print("  %-30s %s" % (tag, top(second[tag], 4)))
    print()
    print("没收进属性表的深度 0 纯数值标签(按文件数,前 45):")
    for tag, (files, shapes) in sorted(other_numeric.items(), key=lambda kv: -kv[1][0])[:45]:
        print("  %-34s 次数 %-7d 形状 %s" % (tag, files, top(shapes, 2)))
    print()
    print("外观标签:深度 0 / 嵌套 出现次数")
    for tag in LOOK_TAGS:
        print("  %-26s 深度0 %-7d 嵌套 %d" % (tag, look_d0[tag], look_nested[tag]))
    print("武器 %d 件(其中有属性标签的 %d);非武器却带 [animation job] 的 %d 件" % (weapons, wpn_stats[True], non_weapon_with_look))
    print("武器的段数分布:" + top(weapon_segs, 8) + ";职业为空的段 %d 个;块内有外观标签的武器 %d 件" % (pre_job_segments, weapons_nested_look))
    print("[animation job] 形状 %s;[variation] 形状 %s;[layer variation] 形状 %s;[equipment ani script] 形状 %s" % (
        top(job_shapes), top(var_shapes), top(layer_shapes), top(script_shapes)))
    print("层数 != 脚本数的段 %d 个;不是严格 层/脚本 交替的段 %d 个" % (segs_script_mismatch, segs_not_alternating))
    print("[sub type] 出现多次的文件 %d 个(取值不同的 %d 个)" % (sub_multi, sub_multi_diff))
    uncovered = [t for t in STAT_TAGS if tag_d0[t] == 0]
    print("属性表里在深度 0 一次都没出现的标签:" + (", ".join(uncovered) if uncovered else "无"))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("survey")
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
    if args.cmd == "survey":
        cmd_survey(pvf, strings)
    else:
        cmd_dump(pvf, strings, args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
