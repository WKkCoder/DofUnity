#!/usr/bin/env python3
"""汇总 .act 的标签表(只含标签名、记录类型号、从属块、全库统计,不含任何原版数值或脚本内容),写成 docs/act-tags.tsv。

输入(都在本机,不入库):
  re/notes/engine2/act_groups.tsv                 tools/py/act_groups.py 从加载函数汇编里抽出的 21 个"标签→编号"字典
  re/notes/engine2/importActionScript.outline.txt  tools/py/c_outline.py 对加载函数的大纲(条件与事件的类型号)
  out/cl/013322f0.outline.txt                      同上,对 [WHICH]/[CHECKUP] 单个条件的解析函数
  out/tag_catalog.json                             tools/py/tag_catalog 生成的全库标签目录(次数、文件数、值的形状)
  out/act_tree.json                                tools/py/act_tree.py 的结构统计(用来推断各字典属于哪个块)
输出:docs/act-tags.tsv,列:
  section   behavior | keyword | trigger | selector | object-type
  tag       标签(含方括号)
  type      记录类型号(行为与触发各自一套编号;选择器与对象类型是它们自己的小码表)
  owner     这个标签出现在哪个块里(字典属于哪个块是按"全库里该块的子标签集合与字典集合的重合度"推断的,写在 evidence 列)
  count/files/shapes  全库统计(形状:i=整数 f=浮点 s=字符串,数字是个数;如 i2 = 两个整数)
  evidence  type 与 owner 各自的依据
用法:python3 tools/py/act_tag_table.py
"""
import collections
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "docs", "act-tags.tsv")


def load_catalog():
    d = json.load(open(os.path.join(ROOT, "out", "tag_catalog.json"), encoding="utf-8"))
    return {t["tag"]: t for t in d["extensions"]["act"]["tags"]}


def shapes_of(entry):
    if not entry:
        return ""
    return " ".join("%s:%d" % (s[0] or "-", s[1]) for s in entry.get("shapes", [])[:4])


def outline_types(path):
    """大纲里带 type= 的标签 -> 类型号。"""
    res = []
    for ln in open(path, encoding="utf-8"):
        m = re.match(r'^\s*("\[[^"]*\]")\s+L(\d+)-(\d+)\s+(.*)$', ln.rstrip("\n"))
        if not m:
            continue
        t = re.search(r"type=(0x[0-9a-f]+|\d+)(?:,|\s|$)", m.group(4))
        if t:
            res.append((m.group(1).strip('"'), int(t.group(1), 0)))
    return res


def outline_pushes(path, parent):
    """某个块下的子标签 -> push= 里的第一个常数(选择器编号)。"""
    res = []
    inside = False
    for ln in open(path, encoding="utf-8"):
        m = re.match(r'^(\s*)("\[[^"]*\]")\s+(?:L\d+-\d+\s+)?(.*)$', ln.rstrip("\n"))
        if not m:
            continue
        indent = len(m.group(1))
        tag = m.group(2).strip('"')
        if indent == 0:
            inside = tag == parent
            continue
        if inside and indent == 2:
            p = re.search(r"push=(0x[0-9a-f]+|\d+)", m.group(3))
            res.append((tag, int(p.group(1), 0) if p else None))
    return res


def main():
    cat = load_catalog()
    tree = json.load(open(os.path.join(ROOT, "out", "act_tree.json"), encoding="utf-8"))
    children = {k: set(v) for k, v in tree["children"].items()}

    groups = collections.OrderedDict()
    for ln in open(os.path.join(ROOT, "re", "notes", "engine2", "act_groups.tsv"), encoding="utf-8"):
        p = ln.rstrip("\n").split("\t")
        if p[0] == "flag":
            continue
        groups.setdefault(p[0], []).append(p)

    rows = []

    def add(section, tag, typ, owner, evidence):
        e = cat.get(tag)
        rows.append((section, tag, typ, owner, e["count"] if e else 0, e["files"] if e else 0, shapes_of(e), evidence))

    for flag, items in groups.items():
        tags = {p[1] for p in items}
        if flag == "01da1f30":
            owner, ev_owner = "[BEHAVIOR]", "174 个标签中 148 个在全库里是 [BEHAVIOR] 的子标签"
            section = "behavior"
        else:
            best = None
            for blk, ch in children.items():
                inter = len(tags & ch)
                if inter and (best is None or (inter / len(tags), inter / len(ch)) > best[0]):
                    best = ((inter / len(tags), inter / len(ch)), blk, inter)
            if best and best[0][0] >= 0.6:
                owner = best[1]
                ev_owner = "字典 %s 的 %d 个标签有 %d 个出现在全库的 %s 之下" % (flag, len(tags), best[2], best[1])
            else:
                owner = "?"
                ev_owner = "字典 %s 的归属没有确定(最佳重合度低于 0.6)" % flag
            section = "keyword"
        for p in items:
            rec = p[6].replace(",", "|") if len(p) > 6 else ""
            ev = "字典项(加载函数 0x01333530 里的 tag→编号表,键 %s);%s" % (p[2], ev_owner)
            add(section, p[1], rec, owner, ev)

    # 触发与条件:加载函数大纲里的类型号 + 单条件解析函数 0x013322f0 的类型号
    seen = set()
    for path, src in (
        (os.path.join(ROOT, "re", "notes", "engine2", "importActionScript.outline.txt"), "加载函数 0x01333530 的分支里写入的记录类型号"),
        (os.path.join(ROOT, "out", "cl", "013322f0.outline.txt"), "条件解析函数 0x013322f0 的分支里写入的记录类型号"),
    ):
        for tag, typ in outline_types(path):
            key = (tag, typ)
            if key in seen:
                continue
            seen.add(key)
            add("trigger", tag, "0x%x" % typ, "[TRIGGER]|[CHECKUP]", src)
    # 类型号不是字面赋值的几个(大纲抓不到),按加载函数里的写法补上
    manual = [
        ("[IS TARGET STATE]", 0x49, "加载函数 0x01333530:类型号 = 0x49 + (标签是否等于 [IS NOT TARGET STATE]),随后读整数到非整数为止"),
        ("[IS NOT TARGET STATE]", 0x4A, "同上(与 [IS TARGET STATE] 共用解析,类型号加 1)"),
        ("[IS CHILD]", 0x60, "加载函数 0x01333530:类型号 0x60,随后读一个整数"),
        ("[IS OBJECT TYPE]", 0x16, "条件解析函数 0x013322f0:类型号 0x16,随后读关键字(对象类型,见 object-type 段)到非关键字为止"),
        ("[WHICH]", 0x1f, "条件解析函数 0x013322f0:类型号 0x1f,随后读一个选择器关键字(见 selector 段)"),
    ]
    for tag, typ, ev in manual:
        if (tag, typ) not in seen:
            add("trigger", tag, "0x%x" % typ, "[TRIGGER]|[CHECKUP]", ev)

    # 选择器编号:[WHICH] 之后的关键字 -> 编号
    fixed = {
        # 这两个的编号不在"推入常数"的分支里(大纲标成 fallthrough),按反编译文本里的赋值补上
        "[LOAD TARGET]": (0x15, "0x013322f0:编号 0x15 之后再读两个整数推入记录"),
        "[SUMMON OBJECT]": (0x18, "0x013322f0:该链末尾的编号 0x18"),
    }
    for tag, code in outline_pushes(os.path.join(ROOT, "out", "cl", "013322f0.outline.txt"), "[WHICH]"):
        if tag in fixed:
            add("selector", tag, "0x%x" % fixed[tag][0], "[WHICH]", fixed[tag][1])
        else:
            add("selector", tag, "0x%x" % code if code is not None else "", "[WHICH]", "0x013322f0 里该关键字分支推入记录参数 0 的常数")
    # [IS OBJECT TYPE] 之后的对象类型关键字 -> 编号(0x013322f0 里一条 if-else 链,按结构读出)
    for tag, code in (("[COLLISION OBJECT]", 0), ("[ACTIVE OBJECT]", 1), ("[CHARACTER]", 2), ("[MONSTER]", 3),
                      ("[AI CHARACTER]", 4), ("[PASSIVE OBJECT]", 5), ("[TRAP]", 6), ("[BREAKABLE]", 7)):
        add("object-type", tag, "0x%x" % code, "[IS OBJECT TYPE]", "0x013322f0 里该关键字分支赋给局部变量、随后推入记录的常数")
    rows.sort(key=lambda r: (r[0], int(r[2].split("|")[0], 16) if r[2] and r[2].split("|")[0].startswith("0x") else 1 << 30, r[1]))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("section\ttag\ttype\towner\tcount\tfiles\tshapes\tevidence\n")
        for r in rows:
            f.write("\t".join(str(x) for x in r) + "\n")
    by = collections.Counter(r[0] for r in rows)
    print("写入", OUT, dict(by))


if __name__ == "__main__":
    main()
