#!/usr/bin/env python3
"""用"从服务端加载函数读出来的 .atk 语法"去解析整个库里的 .atk,看有多少文件能被吃得一个不剩。

这是对"读函数得出的语法"的反证:如果语法读错了(某个标签后面该有几个值、哪些是关键字),
文件就会在那个标签处对不上,或者末尾剩下没被吃掉的标记。语法本身写在 GRAMMAR 里,每一项对应加载函数里的一个处理块
(依据见 docs/evidence/ledger.md 的 E-047;函数的原始文本只在本机 re/ 里,不入库)。

只统计形状,不保存任何脚本内容:输出是各标签的出现次数与"通过/失败"计数、失败文件数、以及失败原因的计数。
用法:python3 tools/py/atk_validate.py [--show 10]
"""
import argparse
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402

# 每项:标签 -> 值的读法列表。
#   "i" 整数  "f" 浮点(整数也接受)  "s" 字符串  "k:a|b|c" 关键字(下一个标记必须是这些标签之一)  "kk" 任意关键字(标签)
#   "*" 后缀:重复到下一个标记不是数字为止
STATUS_EXTRA = {
    "[blind]": 2, "[slow]": 2, "[haste]": 2,
    "[stone]": 1, "[confuse]": 1, "[armor break]": 1, "[weapon break]": 1, "[poison]": 1, "[lightning]": 1, "[bleeding]": 1,
    "[burn]": 4, "[bless]": 4, "[curse]": 4,
}

GRAMMAR = {
    "[damage]": ["f"],
    "[damage increase rate]": ["f"],
    "[absolute damage]": ["i"],
    "[damage bonus]": ["i"],
    "[attack type]": ["k:[physic]|[magic]|[ignore defense]"],
    "[weapon damage apply]": ["i"],
    "[attack myself]": ["i"],
    "[attack friend]": ["i"],
    "[attack enemy]": ["i"],
    "[elemental property]": ["kk"],
    "[active status]": ["STATUS"],
    "[damage reaction]": ["k:[none]|[damage]|[down]|[damage except human]"],
    "[push aside]": ["i"],
    "[lift up]": ["i"],
    "[critical hit]": ["f"],
    "[stuck]": ["f"],
    "[stuck prob increase]": ["i"],
    "[knuck back]": ["KNOCK"],
    "[weakness]": ["f", "f"],
    "[bounce]": ["i"],
    "[ignore defense]": ["i"],
    "[human damage rate]": ["f"],
    "[fort damage rate]": ["f"],
    "[monster damage rate]": ["f"],
    "[HP drain]": ["i"],
    "[MP drain]": ["i"],
    "[hit wav]": ["s"],
    "[attack direction]": ["k:[hit lift up]|[hit horizon]|[hit down]"],
    "[hit info]": ["k:[blow]|[cut]|[burn]|[mana burn]|[etc]", "k:[no blood]|[blood]", "i", "f"],
    "[is use weapon hit delay rate]": ["i"],
    "[force hit stun time]": ["i"],
    "[hit direction]": ["k:[auto]|[front]|[back]|[outer]|[inner]"],
    "[ignore super armor]": ["i"],
    "[ignore weight]": ["i"],
    "[human active status rate]": ["f"],
    "[is hit number increasing]": ["i"],
    "[catch object]": ["CATCH"],
    "[max target hit number]": ["i"],
    "[ignore diehard]": ["i"],
    "[hit check by screen pos]": ["i"],
}


def flatten(lines):
    """[(标签, [值文本])] -> [("tag", 名) 或 ("val", 文本)] 的标记序列。"""
    out = []
    for tag, vals in lines:
        if tag is not None:
            out.append(("tag", tag))
        for v in vals:
            out.append(("val", v))
    return out


def is_int(t):
    return t[0] == "val" and t[1].lstrip("-").isdigit()


def is_num(t):
    if t[0] != "val":
        return False
    try:
        float(t[1])
        return True
    except ValueError:
        return False


def is_str(t):
    return t[0] == "val" and t[1].startswith('"')


class Fail(Exception):
    pass


DETAIL = False


def seen(tokens, i):
    """失败位置上实际遇到的标记(关键字名或值的形状,用来判断语法哪里读错;不保存脚本内容)。"""
    if not DETAIL:
        return ""
    if i >= len(tokens):
        return " <文件结束>"
    kind, text = tokens[i]
    if kind == "tag":
        return " 实际遇到标签 " + text
    return " 实际遇到值 " + ("字符串" if text.startswith('"') else text if len(text) < 12 else "长值")


def parse(tokens, stats):
    i = 0
    n = len(tokens)

    def need(kind, check, why):
        nonlocal i
        if i >= n or not check(tokens[i]):
            raise Fail(why + seen(tokens, i))
        i += 1

    while i < n:
        t = tokens[i]
        if t[0] != "tag":
            raise Fail("顶层不是标签的值")
        name = t[1]
        if name not in GRAMMAR:
            # 不认识的顶层标签:记下来,整个文件算失败
            raise Fail("未知标签 " + name)
        i += 1
        start = i
        for rule in GRAMMAR[name]:
            if rule == "i":
                need("i", is_int, name + " 要整数")
            elif rule == "f":
                need("f", is_num, name + " 要数")
            elif rule == "s":
                need("s", is_str, name + " 要字符串")
            elif rule == "kk":
                if i < n and tokens[i][0] == "tag" and tokens[i][1] not in GRAMMAR:
                    i += 1
                else:
                    raise Fail(name + " 要关键字" + seen(tokens, i))
            elif rule.startswith("k:"):
                allowed = rule[2:].split("|")
                if i < n and tokens[i][0] == "tag" and tokens[i][1] in allowed:
                    i += 1
                else:
                    raise Fail(name + " 的关键字不在 " + rule[2:] + seen(tokens, i))
            elif rule == "STATUS":
                if i < n and tokens[i][0] == "tag" and tokens[i][1] not in GRAMMAR:
                    kind = tokens[i][1]
                    i += 1
                else:
                    raise Fail(name + " 要状态类型关键字" + seen(tokens, i))
                need("f", is_num, name + " 概率")
                need("i", is_int, name + " 等级")
                need("i", is_int, name + " 持续")
                for _ in range(STATUS_EXTRA.get(kind, 0)):
                    need("i", is_int, name + " " + kind + " 的附加整数")
                stats["status:" + kind] += 1
            elif rule == "KNOCK":
                need("i", is_int, name + " 类型")
                if tokens[i - 1][1] == "3":
                    need("f", is_num, name + " 类型 3 的距离")
            elif rule == "CATCH":
                while i < n and is_int(tokens[i]):
                    need("i", is_int, name + " 三元组第 1 项")
                    need("i", is_int, name + " 三元组第 2 项")
                    need("f", is_num, name + " 三元组第 3 项")
            else:
                raise Fail("内部错误:未知规则 " + rule)
        stats["tag:" + name] += 1
        _ = start
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--show", type=int, default=8, help="列出多少个失败原因")
    ap.add_argument("--detail", action="store_true", help="失败原因里带上实际遇到的标记")
    a = ap.parse_args()
    global DETAIL
    DETAIL = a.detail
    pvf = D.Pvf()
    strings = S.Strings(pvf)
    total = ok = skipped = 0
    stats = Counter()
    reasons = Counter()
    for e in pvf.entries:
        if not e.path.lower().endswith(".atk"):
            continue
        raw = pvf.read(e)
        if raw[:2] != b"\xb0\xd0":
            skipped += 1
            continue
        total += 1
        tokens = flatten(S.decode_text(raw, strings))
        local = Counter()
        try:
            parse(tokens, local)
            ok += 1
            stats.update(local)
        except Fail as ex:
            reasons[str(ex)] += 1
    print("atk 文件(标记流) %d,按读出的语法能吃完 %d(%.2f%%),不能 %d,非标记流 %d" % (total, ok, 100.0 * ok / max(1, total), total - ok, skipped))
    print("失败原因(前 %d 项):" % a.show)
    for k, v in reasons.most_common(a.show):
        print("  %6d  %s" % (v, k))
    print("通过的文件里各顶层标签出现次数:")
    for k, v in sorted(stats.items()):
        if k.startswith("tag:"):
            print("  %7d  %s" % (v, k[4:]))
    print("活动状态类型出现次数:")
    for k, v in sorted(stats.items()):
        if k.startswith("status:"):
            print("  %7d  %s" % (v, k[7:]))


if __name__ == "__main__":
    main()
