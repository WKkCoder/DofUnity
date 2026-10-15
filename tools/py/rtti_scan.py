#!/usr/bin/env python3
"""从 DNF.exe 磁盘字节里恢复 MSVC 风格 RTTI 的类层次与虚表(只读磁盘字节,不运行,不碰受保护的节)。

背景:客户端的类型描述符(type_info)名字被抹成了 "." 加一个 16 位编号,所以不能靠名字认类;
但"完整对象定位器 → 类层次描述符 → 基类数组"这些结构还在(`__RTDynamicCast` 要用)。
这里用它们恢复:每个类型描述符的基类、虚表地址、虚表槽数,以及若干槽里"只返回常量"的小函数的常量值
(例如槽 0x34 返回的对象种类码、槽 0x40 返回的对象索引)。

只读 #1(只读数据)与 #2(全局变量初值)两个节里的结构,以及 #0(主代码段)里目标函数开头的几个字节。
受保护区域(#3 到 #8)里的函数一律不读,只记录"虚表槽指向受保护区"。

用法:python3 tools/py/rtti_scan.py [--out re/notes/rtti_classes.tsv] [--show 0x019d920c ...]
输出放 re/(不入库)。
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from decode_strings import Pe  # noqa: E402
from vtable_scan import exe_path  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TYPE_INFO_VTABLE = 0x018AA350  # 类型描述符第一个字(从磁盘字节里观察到)


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


class Image:
    def __init__(self):
        self.pe = Pe(exe_path())
        self.blobs = []  # (va0, bytes)
        for idx in (1, 2):
            name, rva, vsize, roff, rsize = self.pe.sections[idx]
            va0 = self.pe.image_base + rva
            n = min(vsize, rsize)
            self.pe.f.seek(roff)
            self.blobs.append((va0, self.pe.f.read(n)))

    def dword(self, va):
        for va0, b in self.blobs:
            if va0 <= va < va0 + len(b) - 3:
                return u32(b, va - va0)
        return None

    def in_data(self, va):
        return any(va0 <= va < va0 + len(b) for va0, b in self.blobs)

    def scan_dword(self, value):
        pat = struct.pack("<I", value)
        out = []
        for va0, b in self.blobs:
            i = b.find(pat)
            while i >= 0:
                if i % 4 == 0:
                    out.append(va0 + i)
                i = b.find(pat, i + 1)
        return out


def code_kind(pe, va):
    hit = pe.section_of(va)
    if hit is None:
        return "?"
    i = hit[0]
    if i == 0:
        return "code"
    if 3 <= i <= 8:
        return "protected"
    return "other"


def const_return(pe, va):
    """函数开头是否是"返回一个常量"的小函数;只读 #0 里的字节。返回字符串描述或空。"""
    if code_kind(pe, va) != "code":
        return ""
    b = pe.read(va, 12)
    if b[0] == 0xB8 and b[5] in (0xC3, 0xC2):  # mov eax, imm32 ; ret
        return "0x%x" % u32(b, 1)
    if b[0] == 0x33 and b[1] == 0xC0 and b[2] in (0xC3, 0xC2):  # xor eax,eax ; ret
        return "0x0"
    if b[0] == 0x31 and b[1] == 0xC0 and b[2] in (0xC3, 0xC2):
        return "0x0"
    if b[0] == 0x6A and b[2] == 0x58 and b[3] in (0xC3, 0xC2):  # push imm8 ; pop eax ; ret
        return "0x%x" % b[1]
    if b[0] == 0xB0 and b[2] in (0xC3, 0xC2):  # mov al, imm8 ; ret
        return "0x%x(al)" % b[1]
    if b[0] == 0x32 and b[1] == 0xC0 and b[2] in (0xC3, 0xC2):  # xor al,al ; ret
        return "0x0(al)"
    return ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(ROOT, "re", "notes", "rtti_classes.tsv"))
    ap.add_argument("--show", nargs="*", default=[])
    ap.add_argument("--slots", default="0x34,0x38,0x40", help="要解码常量的槽偏移")
    a = ap.parse_args()
    slots = [int(s, 16) for s in a.slots.split(",")]
    img = Image()
    pe = img.pe

    # 1) 类型描述符
    tds = {}
    for va in img.scan_dword(TYPE_INFO_VTABLE):
        if img.dword(va + 4) == 0:
            nm = None
            for va0, b in img.blobs:
                if va0 <= va + 8 < va0 + len(b) - 4:
                    o = va + 8 - va0
                    if b[o] == 0x2E:
                        nm = b[o + 1:o + 3].hex()
            if nm is not None:
                tds[va] = nm
    print("类型描述符:", len(tds))

    # 2) 完整对象定位器(COL):指向类型描述符的位置减 12
    cols = {}
    for td in tds:
        for p in img.scan_dword(td):
            col = p - 12
            sig = img.dword(col)
            chd = img.dword(col + 16)
            if sig != 0 or chd is None or not img.in_data(chd):
                continue
            if img.dword(chd) != 0:
                continue
            nb = img.dword(chd + 8)
            if nb is None or not (1 <= nb <= 64):
                continue
            cols[col] = (td, img.dword(col + 4), img.dword(col + 8), chd, nb)
    print("完整对象定位器:", len(cols))

    # 3) 每个 COL → 虚表(COL 指针在 vtable[-1])
    vt_of = {}
    for col in cols:
        for p in img.scan_dword(col):
            vt = p + 4
            vt_of.setdefault(col, []).append(vt)

    rows = []
    for col, (td, off, cdoff, chd, nb) in sorted(cols.items()):
        bca = img.dword(chd + 12)
        bases = []
        if bca is not None and img.in_data(bca):
            for k in range(nb):
                bcd = img.dword(bca + 4 * k)
                if bcd is None or not img.in_data(bcd):
                    break
                btd = img.dword(bcd)
                bases.append(btd)
        for vt in vt_of.get(col, [None]):
            n = 0
            entries = []
            if vt is not None:
                while True:
                    v = img.dword(vt + 4 * n)
                    if v is None or code_kind(pe, v) not in ("code", "protected"):
                        break
                    entries.append(v)
                    n += 1
                    if n > 400:
                        break
            consts = []
            for s in slots:
                idx = s // 4
                if idx < len(entries):
                    t = entries[idx]
                    c = const_return(pe, t)
                    kind = code_kind(pe, t)
                    consts.append("%x:%08x%s%s" % (s, t, "(P)" if kind == "protected" else "", "=" + c if c else ""))
                else:
                    consts.append("%x:-" % s)
            rows.append((td, tds[td], col, off, cdoff, vt, len(entries), bases, consts))

    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        f.write("td\tname_id\tcol\tthis_off\tvdisp\tvtable\tslots\tbases(td:id)\tslot_consts\n")
        for td, nm, col, off, cdoff, vt, n, bases, consts in rows:
            bs = ",".join("%08x:%s" % (b, tds.get(b, "?")) for b in bases if b != td)
            f.write("%08x\t%s\t%08x\t%d\t%d\t%s\t%d\t%s\t%s\n" % (
                td, nm, col, off, cdoff, ("%08x" % vt) if vt else "-", n, bs, " ".join(consts)))
    print("写出", a.out, len(rows), "行")

    want = [int(x, 16) for x in a.show]
    for td, nm, col, off, cdoff, vt, n, bases, consts in rows:
        if td in want:
            print("TD %08x id=%s col=%08x off=%d vtable=%s slots=%d" % (td, nm, col, off, ("%08x" % vt) if vt else "-", n))
            print("   bases:", " ".join("%08x:%s" % (b, tds.get(b, "?")) for b in bases))
            print("   slots:", " ".join(consts))
    return 0


if __name__ == "__main__":
    sys.exit(main())
