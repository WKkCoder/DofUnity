#!/usr/bin/env python3
"""被动技能"属性加成来源"的本机计数(r15-growth;不是 REA,是对用户自备 Script.pvf 的本机读取)。

回答:技能数据里有没有能直接读出"这个被动加哪项属性、加多少"的字段?只打印数量,不写文件,不输出原版内容、技能名或任何编号。
  1. .skl 总数、被动类型数、其中带 [level info] 的数(这是 PassiveAttackBonus 现在读的"按级的数列",列的含义要靠技能脚本);
  2. 被动 .skl 里出现"九项面板属性标签"(与 Dnf.Game.StatBlock 同一批)的文件数——为 0 说明技能数据没有点名属性目标的字段;
  3. 被动 [level info] 的列数分布(列数 → 技能数);
  4. 全库脚本(.nut)里定义了被动入口 ProcPassiveSkill 的文件数,其中调用了"附加状态"一类接口的文件数
     (只数标识符是否出现,不读内容)。
用法: . tools/env.sh && python3 tools/py/growth_passive_sources.py
证据与结论见 out/growth/NOTES.md:服务端 CParty::check_passive_skill 0x085a932a 是空函数(没有"被动 → 属性"的服务端逻辑)。
"""
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402

# 与 Dnf.Game.StatBlock.TrySet 认的九个标签一致
STAT_TAGS = ("[hp max]", "[mp max]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]",
             "[inventory limit]", "[mp regen speed]", "[hit recovery]")
# 技能脚本里"改状态"类接口的标识符(只数是否出现)
STATUS_IDENTS = (b"sq_AddChangeStatus", b"CHANGE_STATUS_TYPE_", b"sq_AppendAppendage")


def main():
    pvf = D.Pvf()
    strings = S.Strings(pvf)

    total = passive = with_info = 0
    with_stat_tag = 0
    columns = Counter()
    nut_passive = 0
    nut_status = Counter()
    nut_total = 0
    for e in pvf.entries:
        path = e.path.lower()
        if path.endswith(".skl"):
            raw = pvf.get(e.path)
            if raw is None or raw[:2] != b"\xb0\xd0":
                continue
            total += 1
            tags = {}
            for tag, vals in S.decode_text(raw, strings):
                if tag is not None:
                    tags.setdefault(tag.lower(), vals)
            kind = tags.get("[type]")
            if not kind or kind[0].strip('"').lower() != "[passive]":
                continue
            passive += 1
            if any(t in tags for t in STAT_TAGS):
                with_stat_tag += 1
            info = tags.get("[level info]")
            if info:
                with_info += 1
                try:
                    columns[int(info[0])] += 1
                except ValueError:
                    columns[-1] += 1
        elif path.endswith(".nut"):
            raw = pvf.get(e.path)
            if raw is None:
                continue
            nut_total += 1
            if b"ProcPassiveSkill" in raw:
                nut_passive += 1
                for ident in STATUS_IDENTS:
                    if ident in raw:
                        nut_status[ident.decode()] += 1

    print(".skl 可解析:", total)
    print("  被动类型:", passive)
    print("  其中带 [level info]:", with_info)
    print("  其中出现九项面板属性标签:", with_stat_tag)
    print("  [level info] 列数分布(列数: 技能数):", ", ".join("%d: %d" % (k, columns[k]) for k in sorted(columns)))
    print("脚本 .nut:", nut_total)
    print("  定义了 ProcPassiveSkill:", nut_passive)
    for ident in STATUS_IDENTS:
        print("    其中含 %s: %d" % (ident.decode(), nut_status[ident.decode()]))


if __name__ == "__main__":
    main()
