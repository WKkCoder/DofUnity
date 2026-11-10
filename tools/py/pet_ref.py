#!/usr/bin/env python3
"""宠物定义(`.cre`、通用配置、经验表,以及装备脚本上的宠物字段)的参考读取器:按服务端加载函数读出来的语义,独立复现"原版会读出什么"。

用途:作为 C# 实现(Dnf.Game 的 PetScript、PetCommon、PetExpTable、PetItemData、PetCatalog)的独立对照:
`--dump` 写出每个对象读出的字段,与 `dnf pet-check --dump` 的输出逐行比较(两边排序后应完全一致)。输出只含一致性计数,不含原版数值
(`--dump` 文件里有逐字段的值,只写到 out/,不入库)。

语义来源(服务端二进制带符号,函数文本只在本机 re/ 里,不入库):
  标记读取原语  ScanType / ScanInt / ScanFloat / ScanStr(每次读一个标记,读到什么都消耗掉)
  顶层循环      importCreatureScript:先 clear() 设缺省,再循环"读标签 -> 按名字分派到处理块"
  成长一节      [selective grow creature] 进入"块模式",块里只认七个子标签,[/selective grow creature] 才整体提交;
                块里读到游离标记或结尾就悄悄退出块模式(不提交,已读到的标签也丢掉)
  经验表        initCreatureScript:固定读 99 次整数,读不到记 0
本文件只含自己命名的字段与自己写的语法,不含原版代码。

与服务端的差异:服务端只在分派里实现了战斗之外之外的一部分标签,其余标签(约九十个)也在同一分派里;这里不读它们,
所以顶层读到不认得的标签时只是继续找下一个标签(不在顶层遇到游离标记就停止),数据规整时两者读出的字段相同。

用法:
  python3 tools/py/pet_ref.py                         统计
  python3 tools/py/pet_ref.py --dump out/pet_ref.txt  另外写出每个对象的读取结果(out/ 不入库)
"""
import argparse
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402

# 标记类型(与 DNFLEX_TOKEN 的编号一致):0 结束 2 十进制 3 十六进制 4 浮点 5 标签 7 字符串 9+10 "种类 + 键"对
T_EOF, T_DEC, T_HEX, T_FLOAT, T_TAG, T_STR, T_PAIR_KIND, T_PAIR_KEY = 0, 2, 3, 4, 5, 7, 9, 10

LIST_PATH = "creature/creature.lst"
EXP_TABLE_PATH = "creature/exptable.tbl"
COMMON_PATH = "creature/common/creaturecommon.cre"
EQUIPMENT_LIST_PATH = "equipment/equipment.lst"
EXP_TABLE_ENTRIES = 99
CREATURE_PART = 22  # 装备部位号:宠物

# 服务端写死的饱食字面量(只用来报告"数据里的取值是否与它们相同")
ENGINE_SPEND, ENGINE_CHARGE, ENGINE_MAX, ENGINE_FEED = 60, 360, 100, 30


class StringPool:
    """stringtable.bin:u32 count,count+1 个 u32 偏移(相对第 4 字节),字符串池无终止符;按 Latin1 逐字节还原。"""

    def __init__(self, raw):
        n = struct.unpack_from("<I", raw, 0)[0]
        self.raw = raw
        self.offsets = struct.unpack_from("<%dI" % (n + 1), raw, 4)
        self.count = n
        self.cache = {}

    def get(self, index):
        if not 0 <= index < self.count:
            return None
        s = self.cache.get(index)
        if s is None:
            s = self.raw[4 + self.offsets[index]: 4 + self.offsets[index + 1]].decode("latin1")
            self.cache[index] = s
        return s


def trunc_int(x):
    """x87 的 fistp 在"向零舍入"下的行为;超范围或非数给 0x80000000。"""
    if math.isnan(x) or math.isinf(x) or x >= 2147483648.0 or x < -2147483648.0:
        return -2147483648
    return int(x)


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


class Cursor:
    """标记游标:每次读取都前进一个标记,读到的不是想要的类型也已消耗;读完以后一直返回"结束"。"""

    def __init__(self, tokens, pool):
        self.tokens = tokens
        self.pool = pool
        self.pos = 0
        self.discarded = 0

    def next(self):
        if self.pos >= len(self.tokens):
            return T_EOF, 0
        t = self.tokens[self.pos]
        self.pos += 1
        return t

    def text(self, v):
        return self.pool.get(v)

    def scan_type(self):
        """读下一个标签。返回 (干净, 标签名);第一个标记就是标签才算干净,否则丢弃标记直到标签或结尾。"""
        clean = True
        while True:
            t, v = self.next()
            if t == T_TAG:
                return clean, self.text(v)
            clean = False
            if t == T_EOF:
                return False, None
            self.discarded += 1

    def scan_int(self):
        t, v = self.next()
        if t in (T_DEC, T_HEX):
            return v, True
        if t == T_FLOAT:
            return trunc_int(struct.unpack("<f", struct.pack("<i", v))[0]), True
        return 0, False

    def scan_float(self):
        t, v = self.next()
        if t in (T_DEC, T_HEX):
            return f32(float(v)), True
        if t == T_FLOAT:
            return struct.unpack("<f", struct.pack("<i", v))[0], True
        return 0.0, False

    def scan_str(self):
        t, v = self.next()
        if t == T_STR:
            return self.text(v), True
        if t == T_PAIR_KIND:
            t2, v2 = self.next()
            if t2 == T_PAIR_KEY:
                return self.text(v2), True
        return None, False


def new_grow():
    return {"use": 0, "sel": -1, "ac": -1, "bc": -1, "ai": -1, "bi": -1, "per": 1}


# 成长一节里的子标签 -> 字段名
GROW_FIELDS = {
    "[grow level]": "sel",
    "[grow Atype creature id]": "ac",
    "[grow Btype creature id]": "bc",
    "[grow Atype item index]": "ai",
    "[grow Btype item index]": "bi",
    "[item count for levelup]": "per",
}

# 顶层:标签 -> 字段名(都是"读一个整数,读到什么存什么")
TOP_INT_FIELDS = {
    "[start level]": "start",
    "[permission level]": "perm",
    "[max level]": "max",
    "[parent max level]": "pmax",
    "[evolution quest]": "evoq",
    "[evolution creature id]": "evoc",
    "[evolution level]": "evol",
}
ARTIFACT_BITS = {"red": 1, "blue": 2, "green": 4}


def read_pet_script(tokens, pool):
    """返回 (字段字典, 统计字典)。"""
    cur = Cursor(tokens, pool)
    d = {"name": "", "start": 1, "perm": 1, "max": 1, "pmax": 1, "art": 0, "evoq": 0, "evoc": 0, "evol": 0, "grow": new_grow()}
    stats = {"blocks": 0, "committed": 0, "abandoned": 0}
    block = None
    while True:
        if block is not None:
            clean, sub = cur.scan_type()
            if not clean:
                stats["abandoned"] += 1
                block = None
                continue
            if sub == "[/selective grow creature]":
                d["grow"] = dict(block)
                stats["committed"] += 1
                block = None
            elif sub == "[using exp item]":
                v, _ = cur.scan_int()
                block["use"] = 1 if v != 0 else 0
            elif sub in GROW_FIELDS:
                block[GROW_FIELDS[sub]], _ = cur.scan_int()
            continue
        _clean, tag = cur.scan_type()
        if tag is None:
            break
        if tag == "[name]":
            s, ok = cur.scan_str()
            if ok:
                d["name"] = s
        elif tag in TOP_INT_FIELDS:
            d[TOP_INT_FIELDS[tag]], _ = cur.scan_int()
        elif tag == "[artifact slot]":
            while True:
                s, ok = cur.scan_str()
                if not ok:
                    break
                d["art"] |= ARTIFACT_BITS.get(s, 0)
        elif tag == "[selective grow creature]":
            block = new_grow()
            stats["blocks"] += 1
    stats["discarded"] = cur.discarded
    return d, stats


def read_common(tokens, pool):
    cur = Cursor(tokens, pool)
    fields = {"[response delay]": "resp", "[second per fill point spend]": "spend", "[second per fill point charge]": "charge",
              "[MAX fill point]": "max", "[fill point to eat feed]": "feed", "[dying delay]": "dying"}
    d = {k: 0 for k in fields.values()}
    while True:
        _clean, tag = cur.scan_type()
        if tag is None:
            return d
        if tag in fields:
            d[fields[tag]], _ = cur.scan_int()


def read_exp_table(tokens, pool):
    cur = Cursor(tokens, pool)
    values = []
    ok_count = 0
    for _ in range(EXP_TABLE_ENTRIES):
        v, ok = cur.scan_int()
        values.append(v)
        ok_count += 1 if ok else 0
    return values, ok_count


def read_item_pet_fields(tokens, pool):
    cur = Cursor(tokens, pool)
    species, min_level, rate = 0, 0, 0.0
    while True:
        _clean, tag = cur.scan_type()
        if tag is None:
            return species, min_level, rate
        if tag == "[creature species]":
            species, _ = cur.scan_int()
        elif tag == "[creature minimum level]":
            min_level, _ = cur.scan_int()
        elif tag == "[creature experience amount rate]":
            rate, _ = cur.scan_float()


def read_list(tokens, pool):
    """"编号 路径"列表:相邻的(数字, 字符串)成对;重复编号先到先得。返回 {编号: 相对路径}。"""
    out = {}
    i = 0
    while i + 1 < len(tokens):
        t, v = tokens[i]
        t2, v2 = tokens[i + 1]
        if t in (T_DEC, T_HEX) and t2 == T_STR:
            out.setdefault(v, pool.get(v2))
            i += 2
        else:
            i += 1
    return out


def tokens_of(raw):
    if raw is None or raw[:2] != b"\xb0\xd0":
        return None
    return list(struct.iter_unpack("<Bi", raw[2:]))


def resolve(list_path, rel):
    """列表项路径相对列表文件所在目录;统一小写、正斜杠。"""
    base = list_path.rsplit("/", 1)[0]
    parts = []
    for seg in (base + "/" + rel.replace("\\", "/")).split("/"):
        if seg in ("", "."):
            continue
        if seg == "..":
            if parts:
                parts.pop()
            continue
        parts.append(seg)
    return "/".join(parts).lower()


def hex_of(text):
    return text.encode("latin1").hex()


def render_def(cid, path, d, stats):
    g = d["grow"]
    neither = g["ai"] == -1 and g["bi"] == -1
    kind = "g" if (not neither and g["use"] != 0) else "n"
    return "def\t%d\t%s\tname=%s\tstart=%d\tperm=%d\tmax=%d\tpmax=%d\tart=%d\tevoq=%d\tevoc=%d\tevol=%d\tgrow=%d,%d,%d,%d,%d,%d,%d\tkind=%s\tdisc=%d\tgb=%d/%d/%d" % (
        cid, path, hex_of(d["name"]), d["start"], d["perm"], d["max"], d["pmax"], d["art"], d["evoq"], d["evoc"], d["evol"],
        g["use"], g["sel"], g["ac"], g["bc"], g["ai"], g["bi"], g["per"], kind, stats["discarded"], stats["blocks"], stats["committed"], stats["abandoned"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", help="把每个对象读出的字段写到这个文件(放 out/,不入库)")
    a = ap.parse_args()
    pvf = D.Pvf()
    pool = StringPool(pvf.get("stringtable.bin"))
    lines = []

    listing = read_list(tokens_of(pvf.get(LIST_PATH)) or [], pool)
    ids = sorted(listing)
    n_ok = n_fail = n_normal = n_grow = 0
    blocks = committed = abandoned = with_disc = 0
    colors = {1: 0, 2: 0, 4: 0}
    evo_total = evo_unresolved = 0
    for cid in ids:
        path = resolve(LIST_PATH, listing[cid])
        tokens = tokens_of(pvf.get(path))
        if tokens is None:
            n_fail += 1
            continue
        d, stats = read_pet_script(tokens, pool)
        n_ok += 1
        g = d["grow"]
        grow_kind = not (g["ai"] == -1 and g["bi"] == -1) and g["use"] != 0
        n_grow += 1 if grow_kind else 0
        n_normal += 0 if grow_kind else 1
        blocks += stats["blocks"]
        committed += stats["committed"]
        abandoned += stats["abandoned"]
        with_disc += 1 if stats["discarded"] > 0 else 0
        for bit in colors:
            colors[bit] += 1 if d["art"] & bit else 0
        if d["evoc"] != 0:
            evo_total += 1
            evo_unresolved += 0 if d["evoc"] in listing else 1
        lines.append(render_def(cid, path, d, stats))

    common_tokens = tokens_of(pvf.get(COMMON_PATH))
    fill_match = fed_equal = None
    if common_tokens is not None:
        c = read_common(common_tokens, pool)
        fill_match = c["spend"] == ENGINE_SPEND and c["charge"] == ENGINE_CHARGE and c["max"] == ENGINE_MAX
        fed_equal = c["feed"] == ENGINE_FEED
        lines.append("common\tresp=%d\tspend=%d\tcharge=%d\tmax=%d\tfeed=%d\tdying=%d" % (c["resp"], c["spend"], c["charge"], c["max"], c["feed"], c["dying"]))

    table_read = None
    monotone = None
    table_tokens = tokens_of(pvf.get(EXP_TABLE_PATH))
    if table_tokens is not None:
        values, table_read = read_exp_table(table_tokens, pool)
        monotone = all(x <= y for x, y in zip(values[:table_read], values[1:table_read]))
        lines.append("exp\tread=%d\t%s" % (table_read, ",".join(str(v) for v in values)))

    equipment = read_list(tokens_of(pvf.get(EQUIPMENT_LIST_PATH)) or [], pool)
    items_with_fields = species_unresolved = 0
    for code in sorted(equipment):
        tokens = tokens_of(pvf.get(resolve(EQUIPMENT_LIST_PATH, equipment[code])))
        if tokens is None:
            continue
        species, min_level, rate = read_item_pet_fields(tokens, pool)
        if species == 0 and min_level == 0 and rate == 0.0:
            continue
        items_with_fields += 1
        if species != 0 and species not in listing:
            species_unresolved += 1
        lines.append("item\t%d\tspecies=%d\tminlv=%d\trate=%08x" % (code, species, min_level, struct.unpack("<I", struct.pack("<f", rate))[0]))

    print("读取 %d 个宠物脚本(读不出 %d 个);普通型 %d、成长型 %d" % (n_ok, n_fail, n_normal, n_grow))
    print("  成长一节 %d 个:提交 %d、未提交退出 %d;读取时越过过标记的脚本 %d 个(含没建模的标签的取值,不是脏数据的证据)" % (blocks, committed, abandoned, with_disc))
    print("  神器颜色(出现在多少只宠物上):红 %d、蓝 %d、绿 %d" % (colors[1], colors[2], colors[4]))
    print("  进化目标 %d 处,指向列表外 %d 处" % (evo_total, evo_unresolved))
    print("通用配置:饱食三项与引擎字面量相同 %s;\"吃饲料所需\"与引擎喂食增量相同 %s" % (fill_match, fed_equal))
    print("经验表:整数读取成功 %s 次(固定读 %d);非降 %s" % (table_read, EXP_TABLE_ENTRIES, monotone))
    print("物品:带宠物字段 %d 件;species 指向列表外 %d 件" % (items_with_fields, species_unresolved))

    if a.dump:
        os.makedirs(os.path.dirname(os.path.abspath(a.dump)), exist_ok=True)
        with open(a.dump, "w", encoding="utf-8", newline="\n") as fh:
            for ln in sorted(lines):
                fh.write(ln + "\n")
        print("已写出 %d 行 -> %s" % (len(lines), a.dump))


if __name__ == "__main__":
    main()
