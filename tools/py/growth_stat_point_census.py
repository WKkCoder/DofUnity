#!/usr/bin/env python3
"""全库脚本里有没有"属性点分配"这类标签的本机计数(r15-growth;不是 REA,是对用户自备 Script.pvf 的本机读取)。

回答:任务单写的"升级发放属性点、四维加点、洗点"在原版数据里有没有对应的结构?只打印数量,不写文件,不输出原版内容与标签名。
  1. 可解码的脚本文件数、不同标签的种数;
  2. 名字里含 point 的标签种数(任务、积分、技能点、活动等各种"点"都会算进来,所以只是上界);
  3. 名字里明确写着 stat point / status point / bonus point / stats point 的标签种数——为 0 说明没有"属性点"的数据结构;
  4. 名字里含 alloc / assign / distribut / attribute 的标签种数(分配类的另一种写法)。
用法: . tools/env.sh && python3 tools/py/growth_stat_point_census.py
证据与结论见 out/growth/NOTES.md:服务端也没有发放与加点的处理(CUser::_onLevelUp 0x0866311a 里没有,DisPatcher_* 里没有加点请求)。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402

GROUPS = (
    ("名字里含 point", ("point",)),
    ("名字里明确写着属性点", ("stat point", "status point", "bonus point", "stats point")),
    ("名字里含 alloc / assign / distribut", ("alloc", "assign", "distribut")),
    ("名字里含 attribute", ("attribute",)),
)


def main():
    pvf = D.Pvf()
    strings = S.Strings(pvf)
    tags = set()
    decoded = 0
    for e in pvf.entries:
        raw = pvf.get(e.path)
        if raw is None or raw[:2] != b"\xb0\xd0":
            continue
        decoded += 1
        try:
            for tag, _vals in S.decode_text(raw, strings):
                if tag is not None and not tag.startswith("[/"):
                    tags.add(tag.lower())
        except Exception:
            # 个别文件解不动不影响种数统计
            continue
    print("可解码的脚本文件: %d" % decoded)
    print("不同标签(不含结束标签): %d 种" % len(tags))
    for label, words in GROUPS:
        print("  %-36s %d 种" % (label, sum(1 for t in tags if any(w in t for w in words))))


if __name__ == "__main__":
    main()
