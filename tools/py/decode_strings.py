#!/usr/bin/env python3
"""静态解码客户端里"数据块"形式的文本(E-041)。只读磁盘上的 DNF.exe 字节,不运行、不改文件。

块的格式(从客户端的两个访问函数读出,用自己的话写):
    偏移 0   2 字节 标志。宽字符版:0xA1 0x2C(即小端 0x2CA1)表示明文;窄字符版:第 0 字节为 0xA1 表示明文。
    偏移 1   (已编码时)第 1 字节 b1,取 b1 & 0xFE 记为 m
    偏移 2   2 字节 小端 u16,与 (m<<7 | m) 异或得到"字符数"
    偏移 4   正文。明文时是以 0 结尾的字符串;已编码时共 字符数 * 单位 字节(宽字符单位 2,窄字符单位 1)
解码:种子 key = m | 0x9A714CA0(32 位)。正文按 4 字节小端字一个个处理:
        out = word ^ key;  key = key * 0x1003F + out(模 2^32)
    不足 4 字节的尾巴按字节:out = byte ^ (key & 0xFF);  key = key * 0x107 + out
    输出就是明文(宽字符版是 UTF-16LE,含结尾的 0)。

用法:
    python3 tools/py/decode_strings.py --calls re/notes/string_calls.tsv --out re/notes/strings_decoded.tsv
    python3 tools/py/decode_strings.py --selftest
DNF.exe 的路径取自 local.env 的 DNF_DIR(也可用 --exe 指定)。输出只放 re/(不入库)。
"""
import argparse
import os
import struct
import sys

MASK = 0xFFFFFFFF
WIDE_PLAIN_FLAG = 0x2CA1
NARROW_PLAIN_FLAG = 0xA1
KEY_BASE = 0x9A714CA0


def decode_body(src, nbytes, key):
    """src: 密文字节(至少 nbytes 长),返回明文字节。"""
    out = bytearray(nbytes)
    words = nbytes >> 2
    pos = 0
    for _ in range(words):
        w = struct.unpack_from("<I", src, pos)[0]
        o = (w ^ key) & MASK
        struct.pack_into("<I", out, pos, o)
        key = (key * 0x1003F + o) & MASK
        pos += 4
    for _ in range(nbytes & 3):
        o = (src[pos] ^ (key & 0xFF)) & 0xFF
        out[pos] = o
        key = (key * 0x107 + o) & MASK
        pos += 1
    return bytes(out)


def encode_body(plain, key):
    """解码的逆运算,只用于自检。"""
    out = bytearray(len(plain))
    words = len(plain) >> 2
    pos = 0
    for _ in range(words):
        o = struct.unpack_from("<I", plain, pos)[0]
        w = (o ^ key) & MASK
        struct.pack_into("<I", out, pos, w)
        key = (key * 0x1003F + o) & MASK
        pos += 4
    for _ in range(len(plain) & 3):
        o = plain[pos]
        out[pos] = (o ^ (key & 0xFF)) & 0xFF
        key = (key * 0x107 + o) & MASK
        pos += 1
    return bytes(out)


def make_blob(text, wide, b1=0x55):
    """自检用:按上面的格式编一个块。"""
    unit = 2 if wide else 1
    data = (text + "\0").encode("utf-16-le" if wide else "latin-1")
    n = len(data) // unit
    m = b1 & 0xFE
    lenx = n ^ ((m << 7) | m)
    assert lenx <= 0xFFFF
    key = m | KEY_BASE
    return bytes([0x00, b1]) + struct.pack("<H", lenx) + encode_body(data, key)


def decode_blob(raw, wide):
    """raw: 从块起点开始的足够多的字节。返回 (种类, 明文字节或 None)。
    种类:plain 明文;encoded 已解码;bad 长度不合理或读不够。"""
    if len(raw) < 4:
        return "bad", None
    unit = 2 if wide else 1
    if wide:
        if struct.unpack_from("<H", raw, 0)[0] == WIDE_PLAIN_FLAG:
            return "plain", raw[4:]
    elif raw[0] == NARROW_PLAIN_FLAG:
        return "plain", raw[4:]
    m = raw[1] & 0xFE
    n = (((m << 7) | m) ^ struct.unpack_from("<H", raw, 2)[0]) * unit
    if n == 0 or 4 + n > len(raw):
        return "bad", None
    return "encoded", decode_body(raw[4:4 + n], n, m | KEY_BASE)


class Pe:
    def __init__(self, path):
        self.f = open(path, "rb")
        self.f.seek(0x3C)
        pe = struct.unpack("<I", self.f.read(4))[0]
        self.f.seek(pe + 4)
        _machine, nsec, _ts, _p, _n, opt_size, _ch = struct.unpack("<HHIIIHH", self.f.read(20))
        opt = pe + 24
        self.f.seek(opt + 28)
        self.image_base = struct.unpack("<I", self.f.read(4))[0]
        self.f.seek(opt + opt_size)
        self.sections = []
        for _ in range(nsec):
            name, vsize, va, rsize, roff = struct.unpack("<8sIIII", self.f.read(24))
            self.f.read(16)
            self.sections.append((name.rstrip(b"\0").decode("latin-1"), va, max(vsize, 0), roff, rsize))

    def section_of(self, va):
        rva = va - self.image_base
        for i, (name, sva, vsize, roff, rsize) in enumerate(self.sections):
            if sva <= rva < sva + max(vsize, rsize):
                return i, name, rva - sva, roff, rsize
        return None

    def read(self, va, n):
        """从 va 起读至多 n 字节(只读磁盘字节;超出文件内容的部分按 0 补)。"""
        hit = self.section_of(va)
        if hit is None:
            return None
        _i, _name, off, roff, rsize = hit
        avail = max(0, rsize - off)
        self.f.seek(roff + off)
        data = self.f.read(min(n, avail))
        return data + b"\0" * (n - len(data))


def printable_ratio(s):
    if not s:
        return 0.0
    ok = sum(1 for ch in s if ch >= " " or ch in "\t\r\n")
    return ok / len(s)


def selftest():
    for wide in (True, False):
        for text in ["", "a", "Script.pvf", "sq_GetLevelData", "你好,阿拉德", "abcdefg"]:
            if wide is False and any(ord(c) > 255 for c in text):
                continue
            for b1 in (0x00, 0x55, 0xFE, 0xAB):
                blob = make_blob(text, wide, b1)
                kind, plain = decode_blob(blob + b"\0" * 8, wide)
                assert kind == "encoded", (text, wide, b1, kind)
                got = plain.decode("utf-16-le" if wide else "latin-1")
                assert got == text + "\0", (text, wide, b1, got)
    # 明文标志
    kind, plain = decode_blob(bytes([0xA1, 0x2C, 0, 0]) + "hi\0".encode("utf-16-le"), True)
    assert kind == "plain" and plain.startswith("hi".encode("utf-16-le"))
    kind, plain = decode_blob(bytes([0xA1, 0, 0, 0]) + b"hi\0", False)
    assert kind == "plain" and plain.startswith(b"hi\0")
    print("selftest ok")


def load_exe_path(args):
    if args.exe:
        return args.exe
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    with open(os.path.join(root, "local.env"), encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line.startswith("DNF_DIR="):
                return os.path.join(line.split("=", 1)[1].strip("'\""), "DNF.exe")
    raise SystemExit("local.env 里没有 DNF_DIR,用 --exe 指定")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--calls", help="StringCalls.java 的输出")
    ap.add_argument("--out", help="解码结果 TSV")
    ap.add_argument("--exe")
    args = ap.parse_args()
    if args.selftest:
        selftest()
        return
    if not args.calls or not args.out:
        ap.error("需要 --calls 与 --out")

    pe = Pe(load_exe_path(args))
    blobs = {}  # (va, wide) -> 调用点数
    for line in open(args.calls, encoding="utf-8"):
        parts = line.rstrip("\n").split("\t")
        if len(parts) < 5 or not parts[4]:
            continue
        wide = parts[2] == "W"
        key = (int(parts[4], 16), wide)
        blobs[key] = blobs.get(key, 0) + 1

    stats = {"plain": 0, "encoded": 0, "bad": 0, "outside": 0, "not_text": 0}
    rows = []
    for (va, wide), calls in sorted(blobs.items()):
        raw = pe.read(va, 4)
        if raw is None:
            stats["outside"] += 1
            rows.append((va, wide, calls, "outside", ""))
            continue
        head = raw
        # 先读头,算出长度,再读够
        unit = 2 if wide else 1
        if len(head) < 4:
            stats["bad"] += 1
            continue
        if (wide and struct.unpack_from("<H", head, 0)[0] == WIDE_PLAIN_FLAG) or (not wide and head[0] == NARROW_PLAIN_FLAG):
            body = pe.read(va, 4 + 1024)
        else:
            m = head[1] & 0xFE
            n = (((m << 7) | m) ^ struct.unpack_from("<H", head, 2)[0]) * unit
            body = pe.read(va, 4 + min(n, 1 << 20))
        kind, plain = decode_blob(body, wide)
        if plain is None:
            stats["bad"] += 1
            rows.append((va, wide, calls, "bad", ""))
            continue
        enc = "utf-16-le" if wide else "latin-1"
        try:
            text = plain.decode(enc, errors="strict")
        except UnicodeDecodeError:
            stats["not_text"] += 1
            rows.append((va, wide, calls, kind + "_notext", plain[:32].hex()))
            continue
        end = text.find("\0")
        if end < 0 or printable_ratio(text[:end]) < 0.9:
            stats["not_text"] += 1
            rows.append((va, wide, calls, kind + "_notext", text[:32].encode("unicode_escape").decode("ascii")))
            continue
        stats[kind] += 1
        rows.append((va, wide, calls, kind, text[:end]))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("blob_va\twide\tcall_sites\tkind\ttext\n")
        for va, wide, calls, kind, text in rows:
            f.write("%08x\t%s\t%d\t%s\t%s\n" % (va, "W" if wide else "N", calls, kind,
                                                 text.replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")))
    print("不同的数据块 %d 个:%s" % (len(blobs), stats))


if __name__ == "__main__":
    main()
