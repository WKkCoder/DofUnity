#!/usr/bin/env python3
"""配方(`stackable.lst` 里 `[stackable type]` 为 `[recipe]` 的可叠加物品)的参考读取器:按服务端加载函数读出来的语义,独立复现"原版会读出什么"。

用途:作为 C# 实现(Dnf.Game 的 RecipeScript、RecipeCatalog)的独立对照:
  `--dump` 写出每个配方读出的字段,与 `dnf craft-check --dump` 的输出逐行比较(两边排序后应完全一致);
  `compare <C# 转储> <Python 转储>` 逐行逐字段比较两份转储,只报一致与不一致的个数(不输出任何原版数值)。
`--dump` 文件里有逐字段的值,只写到 out/,不入库。

语义来源(服务端二进制带符号,函数文本只在本机 re/ 里,不入库):
  标记读取原语  ScanType / ScanInt / ScanStr(每次读一个标记,读到什么都消耗掉;读不出整数时那个标记也被消耗)
  顶层循环      ImportStackableScript 0x089aeec8:循环"读标签 -> 按名字分派"
                  [stackable type]            读一个字符串(种类名)再读一个整数;[recipe] 的种类号是 3
                  [int data] / [fail output]  反复读整数直到读不出(结尾标签就在这时被消耗),每次遇到先清空,后一段覆盖前一段
                  [bead item]                 反复读"一对整数"直到读不出,不清空,多段累加
                  [success rate] [broadcast type] [maintain ability]  各读一个整数
                  [minimum itemmaking level]  读一个类别名(十一个名字之一)和一个整数;名字不认得或读不全,整个脚本装载失败
                  [string data]               反复读字符串直到读不出
  配方切法      createRecipeInfo 0x089ba83c:整数串 = n1 + n1 对(输入) + n2 + n2 对(产物) + n3 + n3 对(技能要求) [+ 标志];
                  标志 1 会员专属、2 副职业配方,没有或别的值是普通配方
  附加信息      RecipeInfo::additionalInfo 0x08513d44:失败产物表按两个一对读,落单的最后一个丢弃
本文件只含自己命名的字段与自己写的语法,不含原版代码。

与服务端的差异:服务端在顶层遇到游离标记会停止装载,这里只是继续找下一个标签(全库配方的写法都规整,两者读出的字段相同)。

用法:
  python3 tools/py/craft_ref.py                           统计
  python3 tools/py/craft_ref.py --dump out/craft_ref.txt  另外写出每个配方的读取结果(out/ 不入库)
  python3 tools/py/craft_ref.py compare A.txt B.txt       比较两份转储(退出码 0 表示完全一致)
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

STACKABLE_LIST = "stackable/stackable.lst"
EQUIPMENT_LIST = "equipment/equipment.lst"
RECIPE_KIND_NAME = "[recipe]"
MAKING_NAMES = ("weapon", "cloth", "leather", "larmor", "harmor", "plate", "amulet", "wrist", "ring", "support", "magic stone")
GOLD = 0


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


class Cursor:
    """标记游标:每次读取都前进一个标记,读到的不是想要的类型也已消耗;读完以后一直返回"结束"。"""

    def __init__(self, tokens, pool):
        self.tokens = tokens
        self.pool = pool
        self.pos = 0

    def next(self):
        if self.pos >= len(self.tokens):
            return T_EOF, 0
        t = self.tokens[self.pos]
        self.pos += 1
        return t

    def tag(self):
        """读下一个标签(跳过前面的游离标记);读到结尾返回 None。"""
        while True:
            t, v = self.next()
            if t == T_TAG:
                return self.pool.get(v)
            if t == T_EOF:
                return None

    def integer(self):
        t, v = self.next()
        if t in (T_DEC, T_HEX):
            return v, True
        if t == T_FLOAT:
            return trunc_int(struct.unpack("<f", struct.pack("<i", v))[0]), True
        return 0, False

    def string(self):
        t, v = self.next()
        if t == T_STR:
            return self.pool.get(v), True
        if t == T_PAIR_KIND:
            t2, v2 = self.next()
            if t2 == T_PAIR_KEY:
                return self.pool.get(v2), True
        return None, False

    def integers(self):
        """反复读整数直到读不出;读不出的那个标记(结尾标签)已被消耗。"""
        out = []
        while True:
            v, ok = self.integer()
            if not ok:
                return out
            out.append(v)

    def strings(self):
        out = []
        while True:
            s, ok = self.string()
            if not ok:
                return out
            out.append(s)


def tokens_of(raw):
    if raw is None or raw[:2] != b"\xb0\xd0":
        return None
    return list(struct.iter_unpack("<Bi", raw[2:]))


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


def cut_tables(ints):
    """按 createRecipeInfo 的切法把整数串切成 (输入, 产物, 技能要求, 标志, 是否残缺);整数不够时已读到的项保留。"""
    pos = [0]
    bad = [False]

    def table():
        if pos[0] >= len(ints):
            bad[0] = True
            return []
        n = ints[pos[0]]
        pos[0] += 1
        rows = []
        for _ in range(max(n, 0)):
            if bad[0] or pos[0] + 1 >= len(ints):
                bad[0] = True
                break
            rows.append((ints[pos[0]], ints[pos[0] + 1]))
            pos[0] += 2
        return rows

    inputs = table()
    outputs = table() if not bad[0] else []
    skills = table() if not bad[0] else []
    flag = 0
    if not bad[0] and pos[0] < len(ints):
        flag = ints[pos[0]]
    return inputs, outputs, skills, flag, bad[0]


def read_recipe(tokens, pool):
    """读一份物品脚本;不是配方物品返回 None,否则返回字段字典。"""
    cur = Cursor(tokens, pool)
    st = {"kind_name": None, "tidx": 0, "ints": None, "fail": None, "bead": [], "cat": [], "rate": 0, "bcast": 0, "maint": 0,
          "mk": (-1, 0), "bad": False}
    while True:
        tag = cur.tag()
        if tag is None:
            break
        if tag == "[stackable type]":
            name, ok = cur.string()
            st["kind_name"] = name if ok else None
            st["tidx"], _ = cur.integer()
        elif tag == "[int data]":
            st["ints"] = cur.integers()
        elif tag == "[fail output]":
            st["fail"] = cur.integers()
        elif tag == "[success rate]":
            st["rate"], _ = cur.integer()
        elif tag == "[broadcast type]":
            st["bcast"], _ = cur.integer()
        elif tag == "[maintain ability]":
            st["maint"], _ = cur.integer()
        elif tag == "[bead item]":
            while True:
                a, ok = cur.integer()
                if not ok:
                    break
                b, ok = cur.integer()
                if not ok:
                    break
                st["bead"].append((a, b))
        elif tag == "[string data]":
            st["cat"] = cur.strings()
        elif tag == "[minimum itemmaking level]":
            name, ok = cur.string()
            if not ok or name not in MAKING_NAMES:
                st["bad"] = True
                break
            level, ok = cur.integer()
            st["mk"] = (MAKING_NAMES.index(name), level)
            if not ok:
                st["bad"] = True
                break
    if st["kind_name"] != RECIPE_KIND_NAME:
        return None
    inputs, outputs, skills, flag, bad = ([], [], [], 0, False)
    if st["ints"]:
        inputs, outputs, skills, flag, bad = cut_tables(st["ints"])
    fail = []
    if st["fail"]:
        fi = st["fail"]
        fail = [(fi[i], fi[i + 1]) for i in range(0, len(fi) - 1, 2)]
    return {
        "kind": 1 if flag == 1 else 2 if flag == 2 else 0,
        "in": inputs, "out": outputs, "req": skills, "bead": st["bead"], "rate": st["rate"], "fail": fail,
        "bcast": st["bcast"], "maint": st["maint"], "mk": st["mk"], "cat": st["cat"], "tidx": st["tidx"],
        "bad": 1 if (bad or st["bad"]) else 0,
    }


def pairs(rows):
    return ",".join("%d:%d" % r for r in rows) if rows else "-"


def render(code, path, r):
    cats = ",".join(c.encode("latin1").hex() for c in r["cat"]) if r["cat"] else "-"
    return "recipe\t%d\t%s\tkind=%d\tin=%s\tout=%s\treq=%s\tbead=%s\trate=%d\tfail=%s\tbcast=%d\tmaint=%d\tmk=%d:%d\tcat=%s\ttidx=%d\tbad=%d" % (
        code, path, r["kind"], pairs(r["in"]), pairs(r["out"]), pairs(r["req"]), pairs(r["bead"]), r["rate"], pairs(r["fail"]),
        r["bcast"], r["maint"], r["mk"][0], r["mk"][1], cats, r["tidx"], r["bad"])


def parse_dump(path):
    """转储文件 -> {编号: (路径, {字段: 值})};只用于比较。"""
    out = {}
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 3 or parts[0] != "recipe":
                continue
            fields = {}
            for p in parts[3:]:
                k, _, v = p.partition("=")
                fields[k] = v
            out[parts[1]] = (parts[2], fields)
    return out


def compare(a_path, b_path):
    """逐配方逐字段比较两份转储,只输出个数;完全一致返回 0。"""
    a, b = parse_dump(a_path), parse_dump(b_path)
    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    same = differ = 0
    per_field = {}
    for code in sorted(set(a) & set(b)):
        pa, fa = a[code]
        pb, fb = b[code]
        bad = False
        if pa != pb:
            per_field["path"] = per_field.get("path", 0) + 1
            bad = True
        for key in sorted(set(fa) | set(fb)):
            if fa.get(key) != fb.get(key):
                per_field[key] = per_field.get(key, 0) + 1
                bad = True
        if bad:
            differ += 1
        else:
            same += 1
    print("对拍:两边共有 %d 个配方,其中逐字段一致 %d 个、不一致 %d 个;只在第一份里有 %d 个、只在第二份里有 %d 个" % (
        same + differ, same, differ, len(only_a), len(only_b)))
    if per_field:
        print("  不一致的字段(个数):" + "、".join("%s %d" % (k, v) for k, v in sorted(per_field.items())))
    return 0 if differ == 0 and not only_a and not only_b else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", help="把每个配方读出的字段写到这个文件(放 out/,不入库)")
    ap.add_argument("compare", nargs="*", help="compare <第一份转储> <第二份转储>")
    a = ap.parse_args()
    if a.compare and a.compare[0] == "compare":
        if len(a.compare) != 3:
            ap.error("compare 需要两个转储文件")
        sys.exit(compare(a.compare[1], a.compare[2]))

    pvf = D.Pvf()
    pool = StringPool(pvf.get("stringtable.bin"))
    stackable = read_list(tokens_of(pvf.get(STACKABLE_LIST)) or [], pool)
    equipment = read_list(tokens_of(pvf.get(EQUIPMENT_LIST)) or [], pool)
    known = set(stackable) | set(equipment)

    lines = []
    n_kind = {0: 0, 1: 0, 2: 0}
    c_rate = c_fail = c_bcast = c_maint = c_bead = c_making = c_skill = c_nocat = c_bad = 0
    c_gold = c_empty = c_unknown_out = c_unknown_in = c_dup = c_zero = c_equip_out = 0
    for code in sorted(stackable):
        path = resolve(STACKABLE_LIST, stackable[code])
        tokens = tokens_of(pvf.get(path))
        if tokens is None:
            continue
        r = read_recipe(tokens, pool)
        if r is None:
            continue
        n_kind[r["kind"]] += 1
        c_rate += 1 if r["rate"] != 0 else 0
        c_fail += 1 if r["fail"] else 0
        c_bcast += 1 if r["bcast"] != 0 else 0
        c_maint += 1 if r["maint"] != 0 else 0
        c_bead += 1 if r["bead"] else 0
        c_making += 1 if r["mk"][1] != 0 else 0
        c_skill += 1 if r["req"] else 0
        c_nocat += 0 if r["cat"] else 1
        c_bad += r["bad"]
        c_empty += 0 if r["out"] else 1
        all_in = r["in"] + r["bead"]
        codes_in = [c for c, _n in all_in]
        c_gold += 1 if GOLD in codes_in else 0
        c_dup += 1 if len(set(codes_in)) != len(codes_in) else 0
        c_zero += 1 if any(n == 0 for _c, n in all_in) else 0
        c_unknown_in += 1 if any(c != GOLD and c not in known for c in codes_in) else 0
        c_unknown_out += 1 if any(c == GOLD or c not in known for c, _n in r["out"]) else 0
        c_equip_out += 1 if any(c in equipment for c, _n in r["out"]) else 0
        lines.append(render(code, path, r))

    print("读取 %d 个配方;数据残缺 %d 个" % (len(lines), c_bad))
    print("  种类:普通 %d、会员专属 %d、副职业 %d" % (n_kind[0], n_kind[1], n_kind[2]))
    print("  带成功率 %d、带失败产物 %d、带播报 %d、带保留词条 %d、带额外材料 %d、带制作熟练度 %d、带技能要求 %d、没有制作类别 %d" % (
        c_rate, c_fail, c_bcast, c_maint, c_bead, c_making, c_skill, c_nocat))
    print("  材料含金币 %d、含查不到的编号 %d、同编号重复 %d、含零数量 %d" % (c_gold, c_unknown_in, c_dup, c_zero))
    print("  产物为空 %d、含查不到的编号 %d、含装备 %d" % (c_empty, c_unknown_out, c_equip_out))

    if a.dump:
        os.makedirs(os.path.dirname(os.path.abspath(a.dump)), exist_ok=True)
        with open(a.dump, "w", encoding="utf-8", newline="\n") as fh:
            for ln in sorted(lines):
                fh.write(ln + "\n")
        print("已写出 %d 行 -> %s" % (len(lines), a.dump))


if __name__ == "__main__":
    main()
