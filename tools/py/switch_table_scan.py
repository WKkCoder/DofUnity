"""在 DNF.exe 的磁盘字节里找"switch 跳转表"的签名。只读,不运行、不脱壳、不解密。

动机:二进制 .ani 的属性 id 是一个小整数集合(帧属性 0,1,2,7,8,9,10,11,12,13,16,17,23..27;全局属性 0,1,3,18,28)。
客户端解析时一定有一个按 id 分派的 switch;MSVC 会把稠密的 switch 编译成跳转表,
所以"哪些下标指向同一个默认处理、哪些下标指向各自的处理"本身就是一个很强的指纹,不依赖任何字符串。

做法:把 PE 映像按节映射到虚拟地址,在全部节里扫描
  - 4 字节表:N=max(case)+1 个 dword,全部落在代码范围内;非 case 下标全部相同(默认处理),case 下标都不等于默认处理
  - 1 字节表(MSVC 的"索引表 + 地址表"两级形式):N 个字节,非 case 下标全部相同,case 下标都不等于它
输出每个命中的虚拟地址与目标。之后用 Ghidra 的 Xrefs.java 找谁引用了这张表。

用法: python3 switch_table_scan.py --cases 0,1,2,7,8,9,10,11,12,13,16,17,23,24,25,26,27 [--exe DNF.exe] [--max-hits 40]
"""
import argparse
import array
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dnfpaths import require_dnf_dir


def load_image(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    opt_size = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    image_base = struct.unpack_from("<I", d, opt + 28)[0]
    so = opt + opt_size
    secs = []
    for i in range(nsec):
        name = d[so + i * 40: so + i * 40 + 8].rstrip(b"\0").decode("latin1")
        vsize, vaddr, rsize, roff = struct.unpack_from("<IIII", d, so + i * 40 + 8)
        flags = struct.unpack_from("<I", d, so + i * 40 + 36)[0]
        secs.append({"name": name, "vaddr": vaddr, "vsize": vsize, "rsize": rsize, "roff": roff, "flags": flags,
                     "data": d[roff: roff + min(rsize, vsize if vsize else rsize)]})
    return image_base, secs


def find_dword_tables(data, cases, in_code, min_default_frac=0.6):
    """在 data 里按 4 字节对齐找 dword 跳转表。

    指纹:非 case 下标里至少 min_default_frac 的下标等于同一个"默认处理"(客户端可能还处理了数据里从未出现的 id,
    所以不要求全部相等);所有 case 下标都不等于默认处理,且都在代码范围内。
    """
    n = max(cases) + 1
    case_set = set(cases)
    defaults = [i for i in range(n) if i not in case_set]
    if len(defaults) < 2:
        raise SystemExit("case 集合太稠密,没有足够的默认下标可作指纹")
    need = max(2, int(len(defaults) * min_default_frac + 0.9999))
    a = array.array("I")
    a.frombytes(data[: len(data) // 4 * 4])
    if sys.byteorder != "little":
        a.byteswap()
    out = []
    c0, c1 = cases[0], cases[1]
    for i in range(0, len(a) - n + 1):
        v0 = a[i + c0]
        if not in_code(v0):
            continue
        if not in_code(a[i + c1]):
            continue
        # 默认处理 = 非 case 下标里出现最多的值
        cnt = {}
        for k in defaults:
            v = a[i + k]
            cnt[v] = cnt.get(v, 0) + 1
        d, c = max(cnt.items(), key=lambda kv: kv[1])
        if c < need or not in_code(d):
            continue
        ok = True
        for k in cases:
            v = a[i + k]
            if v == d or not in_code(v):
                ok = False
                break
        if ok:
            out.append((i * 4, d, [a[i + k] for k in cases]))
    return out


def find_byte_tables(data, cases, min_default_frac=0.6):
    """找 1 字节索引表(MSVC 的"索引表 + 地址表"两级 switch 的第一级),指纹同上。"""
    n = max(cases) + 1
    case_set = set(cases)
    defaults = [i for i in range(n) if i not in case_set]
    if len(defaults) < 2:
        raise SystemExit("case 集合太稠密,没有足够的默认下标可作指纹")
    need = max(2, int(len(defaults) * min_default_frac + 0.9999))
    mv = memoryview(data)
    out = []
    dsub = defaults
    for off in range(0, len(data) - n + 1):
        # 快速淘汰:任意两个默认下标不等,且第三个也不等于其中之一,则不可能有 >= 60% 相等
        a0, a1, a2 = mv[off + dsub[0]], mv[off + dsub[1]], mv[off + dsub[2]]
        if a0 != a1 and a0 != a2 and a1 != a2:
            continue
        cnt = {}
        for k in dsub:
            v = mv[off + k]
            cnt[v] = cnt.get(v, 0) + 1
        d, c = max(cnt.items(), key=lambda kv: kv[1])
        if c < need:
            continue
        ok = True
        for k in cases:
            if mv[off + k] == d:
                ok = False
                break
        if ok:
            vals = bytes(mv[off: off + n])
            # 区分度:case 下标的取值至少要有几种,避免全零类的假阳性
            if len(set(vals[k] for k in cases)) >= max(3, len(cases) // 3):
                out.append((off, d, [vals[k] for k in cases]))
    return out


def selftest():
    """用合成数据验证两个扫描函数确实能找到埋进去的表,并且不会在随机噪声上误报。"""
    import random
    rnd = random.Random(7)
    cases = [0, 1, 2, 7, 8, 9, 10, 11, 12, 13, 16, 17, 23, 24, 25, 26, 27]
    n = max(cases) + 1
    lo, hi = 0x401000, 0x501000
    in_code = lambda v: lo <= v < hi
    default = 0x450000
    targets = [0x401100 + 0x40 * i for i in range(len(cases))]
    tab = [default] * n
    for k, t in zip(cases, targets):
        tab[k] = t
    blob = bytearray(rnd.randbytes(4096))
    blob += b"\0" * 12 + struct.pack("<%dI" % n, *tab) + rnd.randbytes(512)
    found = find_dword_tables(bytes(blob), cases, in_code)
    assert len(found) == 1 and found[0][1] == default, found
    btab = bytearray([17] * n)
    for j, k in enumerate(cases):
        btab[k] = j
    blob2 = rnd.randbytes(2048) + bytes(btab) + rnd.randbytes(2048)
    foundb = find_byte_tables(blob2, cases)
    assert len(foundb) == 1 and foundb[0][1] == 17, foundb
    noise = rnd.randbytes(1 << 20)
    assert not find_dword_tables(noise, cases, in_code) and not find_byte_tables(noise, cases)
    print("自测通过")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", help="逗号分隔的 case 值")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--exe")
    ap.add_argument("--max-hits", type=int, default=40)
    ap.add_argument("--code-sections", default="0,3,5,8", help="哪些 nsn 节算代码范围(按节序号,逗号分隔)")
    ap.add_argument("--min-default-frac", type=float, default=0.6,
                    help="非 case 下标里至少这个比例指向同一个默认处理(1.0 = 严格,全部相同;默认 0.6)")
    args = ap.parse_args()
    if args.selftest:
        selftest()
        return
    if not args.cases:
        raise SystemExit("需要 --cases 或 --selftest")

    cases = sorted(set(int(x) for x in args.cases.split(",")))
    n = max(cases) + 1
    case_set = set(cases)
    defaults = [i for i in range(n) if i not in case_set]
    exe = args.exe or os.path.join(require_dnf_dir(), "DNF.exe")
    base, secs = load_image(exe)

    code_ranges = []
    for idx in (int(x) for x in args.code_sections.split(",") if x):
        s = secs[idx]
        code_ranges.append((base + s["vaddr"], base + s["vaddr"] + s["vsize"]))

    def in_code(v):
        for lo, hi in code_ranges:
            if lo <= v < hi:
                return True
        return False

    hits4 = []
    hits1 = []
    for si, s in enumerate(secs):
        for off, d, tg in find_dword_tables(s["data"], cases, in_code, args.min_default_frac):
            hits4.append((si, s["name"], base + s["vaddr"] + off, d, tg))
        for off, d, tg in find_byte_tables(s["data"], cases, args.min_default_frac):
            hits1.append((si, s["name"], base + s["vaddr"] + off, d, tg))

    print("cases=%s  表长 N=%d  默认下标=%s" % (cases, n, defaults))
    print("4 字节表命中 %d 个:" % len(hits4))
    for si, name, va, d, tg in hits4[: args.max_hits]:
        print("  节#%d %-4s 表 VA 0x%08X  默认处理 0x%08X  case 目标 %s" % (si, name, va, d, " ".join("%X" % t for t in tg)))
    print("1 字节表命中 %d 个(只列前 %d):" % (len(hits1), args.max_hits))
    for si, name, va, d, tg in hits1[: args.max_hits]:
        print("  节#%d %-4s 表 VA 0x%08X  默认值 %d  case 取值 %s" % (si, name, va, d, tg))


if __name__ == "__main__":
    main()
