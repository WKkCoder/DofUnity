"""DNF.exe 的只读静态探查:只看磁盘上的字节,不运行、不修改、不脱壳、不解密。

目的:回答一个可行性问题 —— 代码能不能被 Ghidra 这类静态工具直接读?
  - PE 头摘要与节表(名称、虚拟地址、大小、权限、整体熵)
  - 每个较大节里"高熵块"(4 KiB 块熵 > 7.5)的占比,用来区分明文代码与密文/压缩数据
  - 入口点落在哪个节,overlay 有多大
  - 一批已知常量与字符串是否以明文出现在文件里、出现在哪个节

用法: python3 pe_probe.py [DNF.exe 路径]    默认读 DNF_DIR 下的 DNF.exe
结果同时写入 out/pe_probe.json(已被 .gitignore 排除)。
"""
import json
import math
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dnfpaths import ensure_out_dir, require_dnf_dir

SCN_FLAGS = [(0x20000000, "X"), (0x40000000, "R"), (0x80000000, "W"), (0x00000020, "code"),
             (0x00000040, "idata"), (0x00000080, "udata"), (0x02000000, "discardable")]
DATA_DIRS = ["export", "import", "resource", "exception", "security", "reloc", "debug", "arch",
             "globalptr", "tls", "load_config", "bound_import", "iat", "delay_import", "clr", "reserved"]


def entropy(buf):
    n = len(buf)
    if n == 0:
        return 0.0
    c = Counter(buf)
    return -sum(v / n * math.log2(v / n) for v in c.values())


def parse_pe(d):
    if d[:2] != b"MZ":
        raise SystemExit("不是 PE 文件")
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    if d[pe:pe + 4] != b"PE\0\0":
        raise SystemExit("PE 签名不对")
    machine, nsec, ts, _, _, opt_size, chars = struct.unpack_from("<HHIIIHH", d, pe + 4)
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    plus = magic == 0x20B
    entry = struct.unpack_from("<I", d, opt + 16)[0]
    image_base = struct.unpack_from("<Q" if plus else "<I", d, opt + (24 if plus else 28))[0]
    sec_align, file_align = struct.unpack_from("<II", d, opt + 32)
    size_of_image, size_of_headers = struct.unpack_from("<II", d, opt + 56)
    subsystem, dll_chars = struct.unpack_from("<HH", d, opt + 68)
    dd_off = opt + (112 if plus else 96)
    ndd = struct.unpack_from("<I", d, dd_off - 4)[0]
    dirs = {}
    for i in range(min(ndd, 16)):
        rva, size = struct.unpack_from("<II", d, dd_off + i * 8)
        if rva or size:
            dirs[DATA_DIRS[i]] = (rva, size)
    secs = []
    so = opt + opt_size
    for i in range(nsec):
        name, vsize, vaddr, rsize, rptr = struct.unpack_from("<8sIIII", d, so + i * 40)
        flags = struct.unpack_from("<I", d, so + i * 40 + 36)[0]
        secs.append({"i": i, "name": name.rstrip(b"\0").decode("latin1"), "vsize": vsize, "vaddr": vaddr,
                     "rsize": rsize, "rptr": rptr, "flags": flags,
                     "flag_names": "".join(n if len(n) == 1 else "" for m, n in SCN_FLAGS if flags & m)})
    return {"machine": machine, "timestamp": ts, "characteristics": chars, "pe32plus": plus, "entry_rva": entry,
            "image_base": image_base, "section_alignment": sec_align, "file_alignment": file_align,
            "size_of_image": size_of_image, "size_of_headers": size_of_headers, "subsystem": subsystem,
            "dll_characteristics": dll_chars, "data_dirs": dirs, "sections": secs}


def section_of_offset(secs, off):
    for s in secs:
        if s["rptr"] <= off < s["rptr"] + s["rsize"]:
            return s
    return None


def section_of_rva(secs, rva):
    for s in secs:
        if s["vaddr"] <= rva < s["vaddr"] + max(s["vsize"], s["rsize"]):
            return s
    return None


def find_all(d, pat, limit=50):
    out = []
    i = d.find(pat)
    while i >= 0 and len(out) < limit:
        out.append(i)
        i = d.find(pat, i + 1)
    return out


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(require_dnf_dir(), "DNF.exe")
    with open(path, "rb") as f:
        d = f.read()
    info = parse_pe(d)
    secs = info["sections"]
    print("文件 %s  大小 %d 字节" % (os.path.basename(path), len(d)))
    print("机器 0x%04X  节数 %d  时间戳 %d  PE32+ %s  ImageBase 0x%X  入口 RVA 0x%X" % (
        info["machine"], len(secs), info["timestamp"], info["pe32plus"], info["image_base"], info["entry_rva"]))
    print("SizeOfImage 0x%X  子系统 %d  DllCharacteristics 0x%04X" % (
        info["size_of_image"], info["subsystem"], info["dll_characteristics"]))
    print("数据目录:", {k: "rva=0x%X size=0x%X" % v for k, v in info["data_dirs"].items()})

    print("\n%-3s %-10s %10s %10s %10s %-6s %7s %9s" % ("#", "name", "vaddr", "vsize", "rsize", "flags", "entropy", "高熵块%"))
    total_high = total_blocks = 0
    for s in secs:
        raw = d[s["rptr"]: s["rptr"] + s["rsize"]]
        s["entropy"] = round(entropy(raw), 3)
        blocks = high = 0
        for o in range(0, len(raw), 4096):
            blk = raw[o: o + 4096]
            if len(blk) < 1024:
                continue
            blocks += 1
            if entropy(blk) > 7.5:
                high += 1
        s["high_entropy_pct"] = round(100.0 * high / blocks, 1) if blocks else 0.0
        s["blocks"] = blocks
        total_high += high
        total_blocks += blocks
        print("%-3d %-10s 0x%08X 0x%08X 0x%08X %-6s %7.3f %8.1f%%" % (
            s["i"], s["name"], s["vaddr"], s["vsize"], s["rsize"], s["flag_names"], s["entropy"], s["high_entropy_pct"]))
    print("全部节合计:高熵块 %d / %d (%.1f%%)" % (total_high, total_blocks, 100.0 * total_high / max(total_blocks, 1)))

    ep = section_of_rva(secs, info["entry_rva"])
    print("入口点所在节:", ep["name"] + " (#%d)" % ep["i"] if ep else "不在任何节内")
    end_raw = max((s["rptr"] + s["rsize"] for s in secs), default=0)
    overlay = d[end_raw:]
    print("overlay: %d 字节, 熵 %.3f" % (len(overlay), entropy(overlay[:1 << 20])))

    # 已知常量与字符串
    probes = [
        ("PVF 解密常量 0x81A79011 (小端)", struct.pack("<I", 0x81A79011)),
        ("PVF 解密常量 0x81A79011 (大端)", struct.pack(">I", 0x81A79011)),
        ("NPK 名称密钥前缀 puchikon@neople", b"puchikon@neople"),
        ("魔数 Neople Img File", b"Neople Img File"),
        ("魔数 Neople Image File(旧版 IMG)", b"Neople Image File"),
        ("魔数 NeoplePack_Bill", b"NeoplePack_Bill"),
        ("字符串 Script.pvf (ASCII)", b"Script.pvf"),
        ("字符串 Script.pvf (UTF-16)", "Script.pvf".encode("utf-16le")),
        ("字符串 ImagePacks2 (ASCII)", b"ImagePacks2"),
        ("字符串 ImagePacks2 (UTF-16)", "ImagePacks2".encode("utf-16le")),
        ("字符串 stringtable.bin (ASCII)", b"stringtable.bin"),
        ("字符串 stringtable.bin (UTF-16)", "stringtable.bin".encode("utf-16le")),
        ("字符串 NeopleEngine (ASCII)", b"NeopleEngine"),
        ("字符串 NeopleEngine (UTF-16)", "NeopleEngine".encode("utf-16le")),
        ("Squirrel 版权串 Alberto Demichelis", b"Alberto Demichelis"),
        ("zlib 版权串 Mark Adler", b"Mark Adler"),
        ("字符串 TerSafe", b"TerSafe"),
        ("字符串 sq_", b"sq_"),
        ("指令 ror r32,6 (C1 C8+r 06)", None),
    ]
    results = []
    print("\n--- 已知常量与字符串在磁盘字节里的出现 ---")
    for label, pat in probes:
        if pat is None:
            hits = []
            for r in range(8):
                hits += find_all(d, bytes([0xC1, 0xC8 + r, 0x06]), limit=2000)
            hits.sort()
            by_sec = Counter((section_of_offset(secs, h) or {"name": "(头/overlay)"})["name"] + "#" + str(
                (section_of_offset(secs, h) or {"i": -1})["i"]) for h in hits)
            print("%-44s 共 %d 处; 按节: %s" % (label, len(hits), dict(by_sec)))
            results.append({"label": label, "count": len(hits)})
            continue
        hits = find_all(d, pat, limit=2000)
        by_sec = Counter()
        for h in hits:
            s = section_of_offset(secs, h)
            by_sec[(s["name"] + "#" + str(s["i"])) if s else "(头/overlay)"] += 1
        print("%-44s 共 %d 处; 按节: %s" % (label, len(hits), dict(by_sec)))
        sample = []
        for h in hits[:3]:
            s = section_of_offset(secs, h)
            va = (s["vaddr"] + (h - s["rptr"]) + info["image_base"]) if s else None
            sample.append({"file_offset": h, "va": va})
        results.append({"label": label, "count": len(hits), "by_section": dict(by_sec), "sample": sample})

    # 对 0x81A79011 的命中给出 ±16 字节十六进制,便于判断是不是 `xor reg, imm32` / `mov reg, imm32`
    for h in find_all(d, struct.pack("<I", 0x81A79011), limit=8):
        s = section_of_offset(secs, h)
        va = (s["vaddr"] + (h - s["rptr"]) + info["image_base"]) if s else 0
        print("  0x81A79011 @ 文件 0x%X (VA 0x%X, 节 %s): %s" % (
            h, va, s["name"] if s else "?", d[max(0, h - 8): h + 12].hex(" ")))

    out = os.path.join(ensure_out_dir(), "pe_probe.json")
    with open(out, "w") as f:
        json.dump({"file": os.path.basename(path), "size": len(d), "pe": info, "overlay": len(overlay),
                   "high_entropy_blocks": [total_high, total_blocks], "probes": results}, f, indent=1)
    print("\n结果已写入", out)


if __name__ == "__main__":
    main()
