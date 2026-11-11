#!/usr/bin/env python3
"""副本疲劳相关标签的本机统计(r15-growth;不是 REA,是对用户自备 Script.pvf 的本机读取)。

回答三个问题(只打印数量,不写文件,不输出原版内容、副本名或任何编号):
  1. 全库 .dgn 里,服务端 ImportDungeonScript 读的"疲劳相关标签"各出现在多少个副本里;
  2. 其中"走疲劳判定会被跳过"的那几类特殊副本标签(见 Dnf.Game.DungeonFatigueRules 的注释)各有多少;
  3. 世界地图(.wdm)上无条件、且在 dungeon.lst 里有路径的副本(玩家实际能选的候选集)里,上面各标签的数量——
     若特殊玩法标签在这个候选集里为 0,说明"只认 [no fatigue]"在本项目的副本集合上与服务端的完整判定等价。
用法: . tools/env.sh && python3 tools/py/growth_dgn_fatigue.py
证据:服务端 ImportDungeonScript 0x088e5faa(标签名字面量 0x08e0db3c 起)、CDungeon::set_dungeon 0x0834acc4(字段抄写)、
CParty::canFatigueUp 0x085a8c88 与 CParty::IsUseFatigueDungeon 0x085be9c8(判定本身)。
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402

# 标签 → 它在服务端 STDungeonScript 里写的偏移(只用于对账,不进运行时)
FATIGUE_TAGS = ("[no fatigue]", "[fatigue]", "[fatigue result]", "[phase fatigue]", "[tournament round fatigue]")
# 会让 canFatigueUp 为 false 的特殊玩法标签(CDungeon+0x89f 血色副本类型、+0x87a 复仇副本),以及其它走另一条入口分支的特殊副本
SPECIAL_TAGS = ("[blood dungeon]", "[village attack revenge dungeon]", "[ancient dungeon]", "[powerwar dungeon]",
                "[defense dungeon]", "[tournament dungeon]", "[tower of despair]")


def read_list(pvf, strings, path, directory):
    """*.lst:值依次是 编号 "路径"(路径相对列表所在目录)。返回 编号 → 小写路径。"""
    raw = pvf.get(path)
    out = {}
    if raw is None:
        return out
    toks = []
    for _tag, vals in S.decode_text(raw, strings):
        toks.extend(vals)
    # 文件开头没有标签:decode_text 把整段当作一个无标签行时,值已经按顺序给出
    for i in range(0, len(toks) - 1, 2):
        try:
            n = int(toks[i])
        except ValueError:
            continue
        out.setdefault(n, (directory + toks[i + 1].strip('"').replace("\\", "/")).lower())
    return out


def main():
    pvf = D.Pvf()
    strings = S.Strings(pvf)

    tags_by_path = {}
    for e in pvf.entries:
        if not e.path.lower().endswith(".dgn"):
            continue
        raw = pvf.get(e.path)
        if raw is None or raw[:2] != b"\xb0\xd0":
            continue
        found = {}
        for tag, vals in S.decode_text(raw, strings):
            if tag in FATIGUE_TAGS or tag in SPECIAL_TAGS:
                found[tag] = vals
        tags_by_path[e.path.lower()] = found

    total = len(tags_by_path)
    print("可解析的 .dgn:", total)
    print("全库各标签出现的副本数:")
    for tag in FATIGUE_TAGS + SPECIAL_TAGS:
        print("  %-34s %4d" % (tag, sum(1 for f in tags_by_path.values() if tag in f)))

    # 世界地图候选集
    dungeon_list = read_list(pvf, strings, "dungeon/dungeon.lst", "dungeon/")
    wdm_list = read_list(pvf, strings, "worldmap/worldmap.lst", "worldmap/")
    candidates = set()
    for _no, wdm in wdm_list.items():
        raw = pvf.get(wdm)
        if raw is None:
            continue
        for tag, vals in S.decode_text(raw, strings):
            if tag != "[dungeon]":
                continue
            nums = []
            for v in vals:
                try:
                    nums.append(int(v))
                except ValueError:
                    pass
            for i in range(0, len(nums) - 1, 2):
                if nums[i + 1] == -1 and nums[i] in dungeon_list:
                    candidates.add(dungeon_list[nums[i]])
    print("世界地图上无条件且有路径的副本(去重):", len(candidates), " 其中 .dgn 可解析:", sum(1 for p in candidates if p in tags_by_path))
    print("候选集里各标签出现的副本数:")
    for tag in FATIGUE_TAGS + SPECIAL_TAGS:
        print("  %-34s %4d" % (tag, sum(1 for p in candidates if tag in tags_by_path.get(p, {}))))
    nofat = sum(1 for p in candidates if "[no fatigue]" in tags_by_path.get(p, {}))
    special = sum(1 for p in candidates if any(t in tags_by_path.get(p, {}) for t in SPECIAL_TAGS))
    print("候选集里 要扣疲劳的副本(没有 [no fatigue] 也没有特殊玩法标签):", len(candidates) - len(
        [p for p in candidates if "[no fatigue]" in tags_by_path.get(p, {}) or any(t in tags_by_path.get(p, {}) for t in SPECIAL_TAGS)]))
    print("候选集里 带 [no fatigue]:", nofat, " 带特殊玩法标签:", special)
    return 0


if __name__ == "__main__":
    sys.exit(main())
