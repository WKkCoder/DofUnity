#!/usr/bin/env python3
"""把 ELF 里 DWARF 描述的类型与函数按编译单元(CU)导出成文本,便于阅读。

用法:
    .venv/bin/python tools/py/dwarf_dump.py <elf 文件> <输出目录> [CU 名称正则]

例:
    .venv/bin/python tools/py/dwarf_dump.py re/server/bin/df_game_r.orig re/server/dwarf 'FilePack|DnfLexer|Core/'

每个 CU 写一个文本文件,内容:
    struct / class / union:大小、基类、成员(偏移、类型、名字)
    enum:枚举项与取值
    函数:名字、链接名、参数类型、返回类型、起止地址(若有)
    全局变量:名字、类型、地址(若有)

输出属于逆向产物,只放 re/(已 gitignore),不提交。依赖:pyelftools(pip install pyelftools)。
"""
import os
import re
import sys

from elftools.elf.elffile import ELFFile


def attr_str(die, name):
    a = die.attributes.get(name)
    if a is None:
        return None
    v = a.value
    return v.decode("utf-8", "replace") if isinstance(v, bytes) else v


class TypeNamer:
    def __init__(self, dwarf):
        self.dwarf = dwarf
        self.cache = {}

    def die_of(self, die, attr="DW_AT_type"):
        a = die.attributes.get(attr)
        if a is None:
            return None
        if a.form in ("DW_FORM_ref_addr",):
            off = a.value
        else:
            off = die.cu.cu_offset + a.value
        return self.dwarf.get_DIE_from_refaddr(off)

    def name(self, die, depth=0):
        """die 是类型 DIE;None 表示 void。"""
        if die is None:
            return "void"
        key = die.offset
        if key in self.cache:
            return self.cache[key]
        if depth > 12:
            return "..."
        tag = die.tag
        n = attr_str(die, "DW_AT_name")
        if tag in ("DW_TAG_base_type", "DW_TAG_typedef", "DW_TAG_structure_type", "DW_TAG_class_type",
                   "DW_TAG_union_type", "DW_TAG_enumeration_type"):
            prefix = {"DW_TAG_structure_type": "struct ", "DW_TAG_class_type": "class ",
                      "DW_TAG_union_type": "union ", "DW_TAG_enumeration_type": "enum "}.get(tag, "")
            res = prefix + (n if n else "<anon@%x>" % die.offset)
        elif tag == "DW_TAG_pointer_type":
            res = self.name(self.die_of(die), depth + 1) + "*"
        elif tag == "DW_TAG_reference_type":
            res = self.name(self.die_of(die), depth + 1) + "&"
        elif tag == "DW_TAG_const_type":
            res = "const " + self.name(self.die_of(die), depth + 1)
        elif tag == "DW_TAG_volatile_type":
            res = "volatile " + self.name(self.die_of(die), depth + 1)
        elif tag == "DW_TAG_array_type":
            dims = []
            for ch in die.iter_children():
                if ch.tag == "DW_TAG_subrange_type":
                    ub = ch.attributes.get("DW_AT_upper_bound")
                    dims.append("[%s]" % ((ub.value + 1) if ub is not None and isinstance(ub.value, int) else ""))
            res = self.name(self.die_of(die), depth + 1) + "".join(dims)
        elif tag == "DW_TAG_subroutine_type":
            params = [self.name(self.die_of(ch), depth + 1) for ch in die.iter_children()
                      if ch.tag == "DW_TAG_formal_parameter"]
            res = "%s(*)(%s)" % (self.name(self.die_of(die), depth + 1), ", ".join(params))
        elif tag == "DW_TAG_ptr_to_member_type":
            res = "member-ptr"
        else:
            res = "<%s>" % tag
        self.cache[key] = res
        return res


def member_offset(die):
    a = die.attributes.get("DW_AT_data_member_location")
    if a is None:
        return None
    v = a.value
    if isinstance(v, int):
        return v
    if isinstance(v, (list, tuple)) and len(v) >= 2 and v[0] == 0x23:  # DW_OP_plus_uconst
        off = 0
        shift = 0
        for b in v[1:]:
            off |= (b & 0x7F) << shift
            shift += 7
            if not (b & 0x80):
                break
        return off
    return None


def dump_cu(cu, namer, out):
    top = cu.get_top_DIE()
    out.write("# CU %s\n\n" % attr_str(top, "DW_AT_name"))
    for die in top.iter_children():
        tag = die.tag
        if tag in ("DW_TAG_structure_type", "DW_TAG_class_type", "DW_TAG_union_type"):
            n = attr_str(die, "DW_AT_name")
            if not n or "DW_AT_declaration" in die.attributes:
                continue
            size = attr_str(die, "DW_AT_byte_size")
            out.write("%s %s  size=%s\n" % (tag.replace("DW_TAG_", "").replace("_type", ""), n, size))
            for ch in die.iter_children():
                if ch.tag == "DW_TAG_inheritance":
                    out.write("    : base %s @%s\n" % (namer.name(namer.die_of(ch)), member_offset(ch)))
                elif ch.tag == "DW_TAG_member":
                    off = member_offset(ch)
                    out.write("    +%-5s %-40s %s\n" % (off if off is not None else "-",
                                                         namer.name(namer.die_of(ch)), attr_str(ch, "DW_AT_name")))
            out.write("\n")
        elif tag == "DW_TAG_enumeration_type":
            n = attr_str(die, "DW_AT_name") or "<anon@%x>" % die.offset
            out.write("enum %s\n" % n)
            for ch in die.iter_children():
                if ch.tag == "DW_TAG_enumerator":
                    out.write("    %-48s = %s\n" % (attr_str(ch, "DW_AT_name"), ch.attributes["DW_AT_const_value"].value))
            out.write("\n")
        elif tag == "DW_TAG_subprogram":
            n = attr_str(die, "DW_AT_name")
            if not n:
                continue
            link = attr_str(die, "DW_AT_MIPS_linkage_name") or attr_str(die, "DW_AT_linkage_name") or ""
            low = die.attributes.get("DW_AT_low_pc")
            params = [namer.name(namer.die_of(ch)) + " " + (attr_str(ch, "DW_AT_name") or "")
                      for ch in die.iter_children() if ch.tag == "DW_TAG_formal_parameter"]
            out.write("fn %s  %s(%s) -> %s  %s\n" % (
                ("@%08x" % low.value) if low is not None else "(decl)",
                n, ", ".join(params), namer.name(namer.die_of(die)), link))
        elif tag == "DW_TAG_variable":
            n = attr_str(die, "DW_AT_name")
            if not n:
                continue
            out.write("var %s : %s\n" % (n, namer.name(namer.die_of(die))))


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    elf_path, out_dir = sys.argv[1], sys.argv[2]
    pattern = re.compile(sys.argv[3]) if len(sys.argv) > 3 else None
    os.makedirs(out_dir, exist_ok=True)
    with open(elf_path, "rb") as f:
        elf = ELFFile(f)
        dwarf = elf.get_dwarf_info()
        namer = TypeNamer(dwarf)
        written = 0
        for cu in dwarf.iter_CUs():
            name = attr_str(cu.get_top_DIE(), "DW_AT_name") or "unknown"
            if pattern and not pattern.search(name):
                continue
            safe = re.sub(r"[^A-Za-z0-9_.-]+", "_", name.strip("./"))
            path = os.path.join(out_dir, safe + ".txt")
            with open(path, "w", encoding="utf-8") as out:
                dump_cu(cu, namer, out)
            written += 1
            print("wrote", path, os.path.getsize(path), "bytes")
        print("CU files:", written)


if __name__ == "__main__":
    main()
