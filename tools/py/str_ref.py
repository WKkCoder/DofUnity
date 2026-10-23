#!/usr/bin/env python3
"""*.kor.str 文字表的 Python 参照读法(证据 E-121),用来和 C# 的 StrBook / Gb18030 / `dnf text-check` 比对。

规格(只共用语义,各自实现):
  - 每行 "键>文字",行尾 CRLF(兼容 LF);"//" 开头的是注释;空行与没有 ">" 的行忽略;文件开头可以有 UTF-8 BOM;
  - 键是 ASCII;文字是 GB18030 字节,文字里的两个字符 "\\n" 表示换行;同一个键出现两次,后面的覆盖前面的;
  - 指纹:按键排序,逐条 "键 TAB 文字 LF",UTF-8,SHA-256。

输出每张表一行 "<标签> sha256=<64 位十六进制>"(只有指纹,没有文字;文字是用户自己的数据,不入库)。
用法:python3 tools/py/str_ref.py          (需要 DNF_DIR 或 local.env,见 dnfpaths.py)
"""
import hashlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402

# 标签 → 路径:界面文字加 脚本里"文字种类 id"对应的各张表(种类 id 与路径的对应见证据 E-121,与 StrLibrary.PathOfKind 一致)
TABLES = [
    ("ui", "ui/ui.kor.str"),
    ("character", "character/character.kor.str"),
    ("dungeon", "dungeon/dungeon.kor.str"),
    ("equipment", "equipment/equipment.kor.str"),
    ("etc", "etc/etc.kor.str"),
    ("itemshop", "itemshop/itemshop.kor.str"),
    ("monster", "monster/monster.kor.str"),
    ("npc", "npc/npc.kor.str"),
    ("quest", "n_quest/quest.kor.str"),
    ("skill", "skill/skill.kor.str"),
    ("stackable", "stackable/stackable.kor.str"),
    ("town", "town/town.kor.str"),
    ("worldmap", "worldmap/worldmap.kor.str"),
    ("creature", "creature/creature.kor.str"),
    ("aicharacter", "aicharacter/aicharacter.kor.str"),
    ("randomoption", "etc/randomoption/randomoption.kor.str"),
]


def parse(raw):
    """字节 → {键: 文字}。"""
    if raw[:3] == b"\xef\xbb\xbf":
        raw = raw[3:]
    table = {}
    for line in raw.split(b"\n"):
        if line.endswith(b"\r"):
            line = line[:-1]
        if not line or line.startswith(b"//"):
            continue
        i = line.find(b">")
        if i <= 0:
            continue
        table[line[:i].decode("latin-1")] = line[i + 1:].decode("gb18030").replace("\\n", "\n")
    return table


def fingerprint(table):
    text = "".join(k + "\t" + table[k] + "\n" for k in sorted(table))
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def main():
    pvf = D.Pvf()
    index = {e.path.lower(): e for e in pvf.entries}
    for label, path in TABLES:
        entry = index.get(path)
        if entry is None:
            print("%s missing" % label)
            continue
        print("%s sha256=%s" % (label, fingerprint(parse(pvf.read(entry)))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
