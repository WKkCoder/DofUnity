#!/usr/bin/env python3
"""会话算法槽 0 到 6 的模拟器向量工厂。本机模拟器测量,非 REA。

做法:用 elf_emu.ElfEmu 把服务端 ELF(re/server/bin/df_game_r.orig)映射进 Unicorn(32 位 x86),
导入函数(malloc、memcpy、memcmp 等)按 .rel.plt 自动识别并用 Python 桩实现——没有桩的函数一旦被调用就报出函数名;
然后对每个槽:CreateCryptoGraph 建对象 → Initialize(切片全长的合成密钥) → Encrypt/Decrypt,
把"合成输入 → 原函数输出"写成向量表,供 C# 测试逐位对拍(E-204、E-262、E-263)。
只读使用服务端二进制,不启动服务,不联网,不读任何账号或配置。本文件只含自己的加载封装与合成输入,不含原版代码。

用法(在仓库根目录运行,用 re/venv/bin/python,因为 unicorn 只装在那里):
  re/venv/bin/python tools/py/net_slot_vectors.py --write      写 src/Dnf.Net.Tests/Golden/crypto_ext_slot<N>_vector.txt(N = 0..6)
  re/venv/bin/python tools/py/net_slot_vectors.py --probe      逐槽探测:密钥字节敏感性、是否 ECB、有无跨调用状态、非整块长度的返回码
  re/venv/bin/python tools/py/net_slot_vectors.py --selftest   用公开标准向量校验"模拟器封装 + 原函数"本身(退出码 0 = 全过)

要点(观察,见 --probe):
  * 向量里的合成密钥必须取切片全长。前任向量文件把密钥列只写到 39 字节,而喂给原函数的是补零到切片长度的密钥;
    对拍时若不补零,槽 2 就会被误判为"与标准 Blowfish 不一致"。
  * 槽 3 只有切片前 32 字节参与单块加解密;槽 0 只有后 4 字节(异或键)参与。
"""
import argparse
import os
import random
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)

from elf_emu import DEFAULT_ELF, ElfEmu  # noqa: E402

OK = 0x6FFFFFFF
STUB_BASE = 0x60000000
STUB_SPAN = 0x10000

# 槽 -> (类名, Initialize, Encrypt(const 重载), Decrypt(const 重载));地址取自服务端符号表(静态读码,非 REA)
SLOT_FUNCS = {
    0: ("Shift", 0x0809C4FE, 0x0809C6C8, 0x0809C7B0),
    1: ("Rijndael", 0x0809C18A, 0x0809C2E8, 0x0809C376),
    2: ("BlowFish", 0x080999A8, 0x08099D16, 0x08099DFC),
    3: ("Rc6", 0x0809BC84, 0x0809BD1A, 0x0809BE06),
    4: ("TwoFish", 0x0809D47C, 0x0809D6D4, 0x0809D7C6),
    5: ("Tea", 0x0809D0C2, 0x0809D228, 0x0809D2B6),
    6: ("Kasumi", 0x0809A604, 0x0809A85C, 0x0809A94E),
}
KEY_LEN = {0: 8, 1: 16, 2: 56, 3: 60, 4: 32, 5: 16, 6: 16}      # 切片密钥长度(E-262)
BLOCK = {0: 4, 1: 16, 2: 8, 3: 16, 4: 16, 5: 16, 6: 8}          # 块长(E-204 的虚函数实测值)
CREATE_GRAPH = 0x0809DF90                                        # CreateCryptoGraph(槽号)
# 每槽写多少条向量:带大表的算法(Twofish 的 q 盒、Kasumi 的 S 盒)多写一些,让每个表项都被用到
VECTOR_COUNT = {0: 24, 1: 16, 2: 16, 3: 16, 4: 64, 5: 24, 6: 128}
SEED_BASE = 0x4D313133                                           # 固定种子,向量可重现
# 标准 RC6-32/20 的 256 位密钥公开向量的输入(AES 提交集);公开密文是 c8241816f0d7e48920ad16a1674e5d48
RC6_STD_KEY = bytes.fromhex("0123456789abcdef0112233445566778") + bytes.fromhex("899aabbccddeeff01032547698badcfe")
RC6_STD_PLAIN = bytes.fromhex("02132435465768798a9bacbdcedfe0f1")
RC6_STD_CIPHER = bytes.fromhex("c8241816f0d7e48920ad16a1674e5d48")


# --------------------------------------------------------------------------- 模拟器封装
def plt_imports(path=DEFAULT_ELF):
    """读 .rel.plt/.dynsym/.dynstr,返回 [(GOT 槽虚拟地址, 导入函数名)]。"""
    with open(path, "rb") as f:
        d = f.read()
    shoff = struct.unpack_from("<I", d, 0x20)[0]
    shentsize, shnum, shstrndx = struct.unpack_from("<HHH", d, 0x2E)
    secs = [struct.unpack_from("<IIIIIIIIII", d, shoff + i * shentsize) for i in range(shnum)]
    names = secs[shstrndx]

    def sname(n):
        s = names[4] + n
        return d[s:d.index(b"\0", s)].decode()

    by = {sname(s[0]): s for s in secs}
    relplt, dynsym, dynstr = by[".rel.plt"], by[".dynsym"], by[".dynstr"]
    out = []
    for i in range(relplt[5] // 8):
        r_off, r_info = struct.unpack_from("<II", d, relplt[4] + i * 8)
        nm = struct.unpack_from("<I", d, dynsym[4] + (r_info >> 8) * 16)[0]
        s = dynstr[4] + nm
        out.append((r_off, d[s:d.index(b"\0", s)].decode()))
    return out


class SlotEmu(ElfEmu):
    """ElfEmu + 通用 PLT 桩。桩只实现这几个槽的代码路径会用到的 libc 函数。"""

    def __init__(self, path=DEFAULT_ELF):
        from unicorn import UC_HOOK_CODE, UC_PROT_ALL
        super().__init__(path)
        self._rand = 0x53594E54
        self.uc.mem_map(STUB_BASE, STUB_SPAN, UC_PROT_ALL)
        self.stubs = {}
        for idx, (got, name) in enumerate(plt_imports(path)):
            addr = STUB_BASE + idx * 0x10
            self.uc.mem_write(addr, b"\xc3")
            self.write_u32(got, addr)
            self.stubs[addr] = name
        self.uc.hook_add(UC_HOOK_CODE, self._hook, None, STUB_BASE, STUB_BASE + STUB_SPAN)

    def _hook(self, uc, address, size, user):
        from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_EIP, UC_X86_REG_ESP
        name = self.stubs.get(address)
        if name is None:
            return
        esp = uc.reg_read(UC_X86_REG_ESP)
        retaddr = struct.unpack("<I", bytes(uc.mem_read(esp, 4)))[0]

        def arg(i):
            return struct.unpack("<I", bytes(uc.mem_read(esp + 4 + 4 * i, 4)))[0]

        ret = 0
        if name == "malloc":
            ret = self.alloc(max(1, arg(0)))
        elif name == "calloc":
            ret = self.alloc(max(1, arg(0) * arg(1)))
        elif name == "free":
            ret = 0
        elif name == "memset":
            dst, val, n = arg(0), arg(1), arg(2)
            if n:
                uc.mem_write(dst, bytes([val & 0xFF]) * n)
            ret = dst
        elif name in ("memcpy", "memmove"):
            dst, src, n = arg(0), arg(1), arg(2)
            if n:
                uc.mem_write(dst, bytes(uc.mem_read(src, n)))
            ret = dst
        elif name == "memcmp":
            a, b, n = arg(0), arg(1), arg(2)
            x, y = bytes(uc.mem_read(a, n)), bytes(uc.mem_read(b, n))
            ret = 0 if x == y else (1 if x > y else 0xFFFFFFFF)
        elif name == "strlen":
            p, n = arg(0), 0
            while bytes(uc.mem_read(p + n, 1)) != b"\0":
                n += 1
            ret = n
        elif name == "rand":
            self._rand = (self._rand * 1103515245 + 12345) & 0x7FFFFFFF
            ret = self._rand
        elif name == "srand":
            self._rand = arg(0) & 0x7FFFFFFF
        else:
            raise RuntimeError("导入函数 %s 没有桩(返回地址 %08x)" % (name, retaddr))
        uc.reg_write(UC_X86_REG_EAX, ret)
        uc.reg_write(UC_X86_REG_ESP, esp + 4)
        uc.reg_write(UC_X86_REG_EIP, retaddr)

    def call(self, addr, args=(), max_insns=60_000_000):
        return super().call(addr, args, max_insns)


class Slot:
    """一个槽的原版对象。每次新建对象 + Initialize,避免上一把密钥的残留(槽 5 内部有"同密钥则跳过"的捷径)。"""

    def __init__(self, emu, slot):
        self.emu, self.slot = emu, slot
        self.obj = emu.call(CREATE_GRAPH, (slot,))
        self.name, self.init_va, self.enc_va, self.dec_va = SLOT_FUNCS[slot]

    def set_key(self, key):
        p = self.emu.alloc(max(len(key), 16))
        self.emu.write(p, key)
        return self.emu.call(self.init_va, (self.obj, p, len(key)))

    def _run(self, va, data):
        e = self.emu
        n = len(data)
        pin, pout = e.alloc(max(n, 16)), e.alloc(max(n, 16) + 16)
        e.write(pin, data)
        e.write(pout, b"\xaa" * (max(n, 16) + 16))
        ret = e.call(va, (self.obj, pin, n, pout, n))
        return ret, e.read(pout, n)

    def encrypt(self, data):
        return self._run(self.enc_va, data)

    def decrypt(self, data):
        return self._run(self.dec_va, data)


def encrypt_once(emu, slot, key, plain):
    s = Slot(emu, slot)
    r = s.set_key(key)
    if r != OK:
        raise RuntimeError("槽 %d Initialize 返回 %08x" % (slot, r))
    r, ct = s.encrypt(plain)
    if r != OK:
        raise RuntimeError("槽 %d Encrypt 返回 %08x" % (slot, r))
    r, back = s.decrypt(ct)
    if r != OK or back != plain:
        raise RuntimeError("槽 %d 往返不一致" % slot)
    return ct


# --------------------------------------------------------------------------- 合成输入
def synth_rows(slot, count):
    """(合成密钥, 明文) 列表:先放结构化的边界行,再放固定种子的随机行。"""
    kl, bl = KEY_LEN[slot], BLOCK[slot]
    rows = [
        (bytes(kl), bytes(bl)),                                       # 全 0
        (b"\xff" * kl, b"\xff" * bl),                                 # 全 1
        (bytes(i & 0xFF for i in range(kl)), bytes(i & 0xFF for i in range(bl))),   # 递增
        (bytes(kl), b"\x01" + bytes(bl - 1)),                         # 密钥全 0,明文只有最低位
        (b"\x01" + bytes(kl - 1), bytes(bl)),                         # 明文全 0,密钥首字节最低位
        (bytes(kl - 1) + b"\x80", bytes(bl - 1) + b"\x80"),           # 末字节最高位
    ]
    if slot == 3:
        # 标准 RC6-32/20 在 256 位密钥下的公开输入;原版给出的密文与公开密文不同(变体),向量里记原版的输出
        rows.append((RC6_STD_KEY + b"\xa5" * 28, RC6_STD_PLAIN))
    rng = random.Random(SEED_BASE + slot)
    while len(rows) < count:
        key = bytes(rng.getrandbits(8) for _ in range(kl))
        plain = bytes(rng.getrandbits(8) for _ in range(bl))
        rows.append((key, plain))
    return rows[:count]


def header(slot):
    name = SLOT_FUNCS[slot][0]
    return [
        "# 本机模拟器测量,非 REA: 服务端槽 %d(%s)的 Initialize/Encrypt/Decrypt 在 Unicorn 里逐条执行,SYNTH 合成输入" % (slot, name),
        "# 列(TAB 分隔): SYNTH 密钥(切片全长 %d 字节) / 明文(%d 字节) / 密文;每行另校验过原函数的解密能还原明文" % (KEY_LEN[slot], BLOCK[slot]),
        "# 重新生成: re/venv/bin/python tools/py/net_slot_vectors.py --write",
    ]


def cmd_write(out_dir):
    emu = SlotEmu()
    os.makedirs(out_dir, exist_ok=True)
    for slot in range(7):
        lines = header(slot)
        for key, plain in synth_rows(slot, VECTOR_COUNT[slot]):
            ct = encrypt_once(emu, slot, key, plain)
            lines.append("%s\t%s\t%s" % (key.hex(), plain.hex(), ct.hex()))
        path = os.path.join(out_dir, "crypto_ext_slot%d_vector.txt" % slot)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")
        print("槽 %d(%s):写入 %d 条 → %s" % (slot, SLOT_FUNCS[slot][0], len(lines) - 3, os.path.relpath(path, ROOT)))
    return 0


# --------------------------------------------------------------------------- 探测
def cmd_probe():
    emu = SlotEmu()
    for slot in range(7):
        kl, bl = KEY_LEN[slot], BLOCK[slot]
        rng = random.Random(SEED_BASE + 100 + slot)
        key = bytes(rng.getrandbits(8) for _ in range(kl))
        pt = bytes(rng.getrandbits(8) for _ in range(bl))
        s = Slot(emu, slot)
        r = s.set_key(key)
        print("槽 %d %-8s Initialize 返回 %08x(密钥 %d 字节,块 %d 字节)" % (slot, s.name, r, kl, bl))
        _, ct = s.encrypt(pt)
        _, ct_again = s.encrypt(pt)
        _, ct_two = s.encrypt(pt + pt)
        pt2 = bytes(rng.getrandbits(8) for _ in range(bl))
        _, ct_mix = s.encrypt(pt + pt2)
        _, ct_pt2 = s.encrypt(pt2)
        short, _ = s.encrypt(pt[:bl - 1])
        print("  同对象再次加密相同:%s;两个相同块一次调用两半相同:%s;第二块与单独加密相同:%s;非整块长度返回 %08x"
              % (ct == ct_again, ct_two[:bl] == ct_two[bl:], ct_mix[bl:] == ct_pt2, short))
        used = []
        for i in range(kl):
            k2 = bytearray(key)
            k2[i] ^= 1
            s2 = Slot(emu, slot)
            if s2.set_key(bytes(k2)) != OK:
                continue
            _, c2 = s2.encrypt(pt)
            if c2 != ct:
                used.append(i)
        unused = [i for i in range(kl) if i not in used]
        print("  翻转一位后密文会变的密钥字节:%d/%d;不起作用的字节下标:%s" % (len(used), kl, unused if unused else "无"))
        short_key = Slot(emu, slot)
        print("  密钥过短(少 1 字节)时 Initialize 返回 %08x" % short_key.set_key(key[:-1]))
    return 0


# --------------------------------------------------------------------------- 自检:公开标准向量
def h(s):
    return bytes.fromhex(s.replace(" ", ""))


def public_cases():
    """(槽, 来源, 合成后的切片密钥, 明文, 期望密文);期望值全部来自公开标准/论文/参考实现的测试向量。"""
    cases = []
    # 槽 1:FIPS-197 附录 C.1
    cases.append((1, "FIPS-197 C.1", h("000102030405060708090a0b0c0d0e0f"), h("00112233445566778899aabbccddeeff"), h("69c4e0d86a7b0430d8cdb78070b4c55a")))
    # 槽 1:FIPS-197 附录 B 的示例密钥与 NIST SP 800-38A F.1.1(ECB-AES128)的 4 个块
    sp_key = h("2b7e151628aed2a6abf7158809cf4f3c")
    cases.append((1, "FIPS-197 附录 B", sp_key, h("3243f6a8885a308d313198a2e0370734"), h("3925841d02dc09fbdc118597196a0b32")))
    for p, c in (("6bc1bee22e409f96e93d7e117393172a", "3ad77bb40d7a3660a89ecaf32466ef97"),
                 ("ae2d8a571e03ac9c9eb76fac45af8e51", "f5d3d58503b9699de785895a96fdbaaf"),
                 ("30c81c46a35ce411e5fbc1191a0a52ef", "43b1cd7f598ece23881b00e3ed030688"),
                 ("f69f2445df4f9b17ad2b417be66c3710", "7b0c785e27e8ad3f8223207104725dd4")):
        cases.append((1, "SP 800-38A F.1.1", sp_key, h(p), h(c)))
    # 槽 2:Blowfish 的 8 字节密钥重复 7 次 = 56 字节切片,与 8 字节密钥的循环装入等价(56 是 8 的倍数)
    for k, p, c in (("0000000000000000", "0000000000000000", "4ef997456198dd78"),
                    ("ffffffffffffffff", "ffffffffffffffff", "51866fd5b85ecb8a"),
                    ("3000000000000000", "1000000000000001", "7d856f9a613063f2"),
                    ("1111111111111111", "1111111111111111", "2466dd878b963c9d"),
                    ("0123456789abcdef", "1111111111111111", "61f9c3802281b096"),
                    ("fedcba9876543210", "0123456789abcdef", "0aceab0fc6a0a28d")):
        cases.append((2, "Blowfish 公开向量", h(k) * 7, h(p), h(c)))
    # 槽 3 没有公开向量可用:原版的扩展密钥表按字节存取,与标准 RC6-32/20 不一致(见 RC6_STD_*)
    # 槽 5:类名叫 Tea,轮函数实为 XTEA(32 个循环,字节与字按大端转换);16 字节块 = 两个独立的 8 字节块。
    # 零密钥零明文的 XTEA 公开已知答案是 dee9d4d8f7131ed9(标准 TEA 的答案是 41ea3a0a94baa940,与原版不符)
    cases.append((5, "XTEA 零密钥零明文(两个块)", bytes(16), bytes(16), h("dee9d4d8f7131ed9") * 2))
    # 公开 XTEA 参考实现(大端字)自带的几条向量;16 字节块拆成两个 8 字节块各取一条
    xk = h("000102030405060708090a0b0c0d0e0f")
    cases.append((5, "XTEA 公开向量 1+2", xk, h("4142434445464748") + h("4141414141414141"),
                  h("497df3d072612cb5") + h("e78f2d13744341d8")))
    cases.append((5, "XTEA 公开向量 3", xk, h("5a5b6e278948d77f") + h("5a5b6e278948d77f"),
                  h("4141414141414141") * 2))
    # 槽 4:Twofish 论文/参考实现的 256 位向量
    tfk = h("0123456789abcdeffedcba9876543210") + h("00112233445566778899aabbccddeeff")
    cases.append((4, "Twofish 256 位", tfk, bytes(16), h("37527be0052334b89f0cfccae87cfa20")))
    cases.append((4, "Twofish 256 位全零", bytes(32), bytes(16), h("57ff739d4dc92c1bd7fc01700cc8216f")))
    # 槽 6:3GPP TS 35.203 测试集 1
    cases.append((6, "KASUMI 测试集 1", h("9900aabbccddeeff1122334455667788"), h("fedcba0987654321"), h("514896226caa4f20")))
    # 槽 6:公共领域参考实现(libtomcrypt)自带的 5 条单比特密钥向量,明文全 0
    for kk, cc in (("80000000000000000000000000000000", "4b58a771afc7e5e8"),
                   ("00800000000000000000000000000000", "7eef113c95bb5a77"),
                   ("00008000000000000000000000000000", "5f140686d7ad5a39"),
                   ("00000000000000000000000000000001", "2e1491cf70aa465d"),
                   ("00000000000000000000000000000100", "b54586f4ab9ae546")):
        cases.append((6, "KASUMI 单比特密钥", h(kk), bytes(8), h(cc)))
    return cases


def cmd_selftest():
    emu = SlotEmu()
    bad = 0
    for slot, src, key, plain, want in public_cases():
        got = encrypt_once(emu, slot, key, plain)
        ok = got == want
        bad += 0 if ok else 1
        print("%s 槽 %d  %s" % ("通过" if ok else "不符", slot, src))
    print("公开向量自检:%d 条,不符 %d" % (len(public_cases()), bad))
    # 槽 3 的反向断言:原版不复现标准 RC6 的公开密文(它的扩展密钥表是字节表)
    got = encrypt_once(emu, 3, RC6_STD_KEY + b"\xa5" * 28, RC6_STD_PLAIN)
    differs = got != RC6_STD_CIPHER
    print("%s 槽 3  与标准 RC6 的公开密文不同(变体)" % ("通过" if differs else "不符"))
    return 1 if (bad or not differs) else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--probe", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--out", default=os.path.join(ROOT, "src", "Dnf.Net.Tests", "Golden"))
    a = ap.parse_args()
    if a.write:
        return cmd_write(a.out)
    if a.probe:
        return cmd_probe()
    if a.selftest:
        return cmd_selftest()
    ap.print_help()
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
