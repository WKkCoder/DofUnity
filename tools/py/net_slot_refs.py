#!/usr/bin/env python3
"""会话算法槽 0 至 6 的纯 Python 参考实现:只按读码结论与公开算法写,与 C# 实现、与服务端二进制都无关,
用来独立复核 Golden 目录里已提交的向量。不需要服务端二进制,也不需要模拟器。

    re/venv/bin/python tools/py/net_slot_refs.py --selftest        退出码 0 = 全部相符

覆盖(逐行核对 crypto_ext_slot<N>_vector.txt、crypto_slots_vectors.txt 里可复现的旧行、槽 0 的 cryptoshift_encrypt_vector.txt):
  槽 0 移位   原版的移位结果被丢弃,只剩"逐字节异或切片后 4 字节"
  槽 1 AES    标准 AES-128(pycryptodome 的 ECB)
  槽 2 Blowfish  标准 Blowfish(pycryptodome 的 ECB,56 字节密钥)
  槽 3 RC6 变体  扩展密钥表是 44 个字节(S[0]=0x63、S[i]=S[i-1]-0x47,混合阶段取低 8 位),只用切片前 32 字节;块函数其余与标准 RC6 相同
  槽 5 XTEA   大端字、32 循环;16 字节块是同一把密钥下两个互相独立的 8 字节块
槽 4 Twofish 与槽 6 KASUMI 与公开标准一致,没有另写参考实现:它们由 net_slot_vectors.py --selftest 用公开向量在原版上校验
(那一步需要本机的服务端二进制),并由 C# 测试里的公开向量把关。
"""
import os
import struct
import sys

M32 = 0xFFFFFFFF
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GOLDEN = os.path.join(ROOT, "src", "Dnf.Net.Tests", "Golden")

# 每个有参考实现的槽:新向量文件应有的行数(防止文件被截断)
EXT_ROWS = {0: 24, 1: 16, 2: 16, 3: 16, 5: 24}
KEY_LEN = {0: 8, 1: 16, 2: 56, 3: 60, 5: 16}
BLOCK = {0: 4, 1: 16, 2: 8, 3: 16, 5: 16}


def rol32(x, n):
    n &= 31
    return ((x << n) | (x >> (32 - n))) & M32 if n else x & M32


def ror32(x, n):
    return rol32(x, 32 - (n & 31))


# ---------------------------------------------------------------- 槽 0
def shift_crypt(key, block):
    """加密与解密相同:逐字节异或切片[4..8)。"""
    return bytes(b ^ k for b, k in zip(block, key[4:8]))


# ---------------------------------------------------------------- 槽 1、2
def aes_enc(key, block):
    from Crypto.Cipher import AES
    return AES.new(key, AES.MODE_ECB).encrypt(block)


def aes_dec(key, block):
    from Crypto.Cipher import AES
    return AES.new(key, AES.MODE_ECB).decrypt(block)


def blowfish_enc(key, block):
    from Crypto.Cipher import Blowfish
    return Blowfish.new(key, Blowfish.MODE_ECB).encrypt(block)


def blowfish_dec(key, block):
    from Crypto.Cipher import Blowfish
    return Blowfish.new(key, Blowfish.MODE_ECB).decrypt(block)


# ---------------------------------------------------------------- 槽 3
def rc6_variant_setup(key32):
    words = [int.from_bytes(key32[4 * i:4 * i + 4], "little") for i in range(8)]
    s = [0] * 44
    s[0] = 0x63
    for i in range(1, 44):
        s[i] = (s[i - 1] - 0x47) & 0xFF
    a = b = i = j = 0
    for _ in range(3 * max(44, 8)):
        s[i] = rol32((s[i] + a + b) & M32, 3) & 0xFF
        a = s[i]
        words[j] = rol32((words[j] + a + b) & M32, (a + b) & 31)
        b = words[j]
        i = (i + 1) % 44
        j = (j + 1) % 8
    return s


def rc6_variant_enc(key, block):
    s = rc6_variant_setup(key[:32])
    a, b, c, d = struct.unpack("<4I", block)
    b = (b + s[0]) & M32
    d = (d + s[1]) & M32
    for i in range(2, 41, 2):
        t = rol32((b * (2 * b + 1)) & M32, 5)
        u = rol32((d * (2 * d + 1)) & M32, 5)
        a = (rol32(a ^ t, u & 31) + s[i]) & M32
        c = (rol32(c ^ u, t & 31) + s[i + 1]) & M32
        a, b, c, d = b, c, d, a
    a = (a + s[42]) & M32
    c = (c + s[43]) & M32
    return struct.pack("<4I", a, b, c, d)


def rc6_variant_dec(key, block):
    s = rc6_variant_setup(key[:32])
    a, b, c, d = struct.unpack("<4I", block)
    c = (c - s[43]) & M32
    a = (a - s[42]) & M32
    for i in range(40, 1, -2):
        a, b, c, d = d, a, b, c
        u = rol32((d * (2 * d + 1)) & M32, 5)
        t = rol32((b * (2 * b + 1)) & M32, 5)
        c = ror32((c - s[i + 1]) & M32, t & 31) ^ u
        a = ror32((a - s[i]) & M32, u & 31) ^ t
    d = (d - s[1]) & M32
    b = (b - s[0]) & M32
    return struct.pack("<4I", a, b, c, d)


# ---------------------------------------------------------------- 槽 5
def xtea_half_enc(k, v0, v1):
    s = 0
    for _ in range(32):
        v0 = (v0 + ((((v1 << 4) ^ (v1 >> 5)) + v1) ^ (s + k[s & 3]))) & M32
        s = (s + 0x9E3779B9) & M32
        v1 = (v1 + ((((v0 << 4) ^ (v0 >> 5)) + v0) ^ (s + k[(s >> 11) & 3]))) & M32
    return v0, v1


def xtea_half_dec(k, v0, v1):
    s = (0x9E3779B9 * 32) & M32
    for _ in range(32):
        v1 = (v1 - ((((v0 << 4) ^ (v0 >> 5)) + v0) ^ (s + k[(s >> 11) & 3]))) & M32
        s = (s - 0x9E3779B9) & M32
        v0 = (v0 - ((((v1 << 4) ^ (v1 >> 5)) + v1) ^ (s + k[s & 3]))) & M32
    return v0, v1


def xtea_pair(half, key, block):
    k = struct.unpack(">4I", key)
    out = b""
    for off in (0, 8):
        v0, v1 = struct.unpack(">2I", block[off:off + 8])
        v0, v1 = half(k, v0, v1)
        out += struct.pack(">2I", v0, v1)
    return out


def xtea_enc(key, block):
    return xtea_pair(xtea_half_enc, key, block)


def xtea_dec(key, block):
    return xtea_pair(xtea_half_dec, key, block)


REFS = {
    0: (shift_crypt, shift_crypt),
    1: (aes_enc, aes_dec),
    2: (blowfish_enc, blowfish_dec),
    3: (rc6_variant_enc, rc6_variant_dec),
    5: (xtea_enc, xtea_dec),
}


# ---------------------------------------------------------------- 读向量文件
def read_rows(name, columns):
    rows = []
    with open(os.path.join(GOLDEN, name), encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            if not line or line.startswith("#"):
                continue
            cols = line.split("\t")
            if len(cols) != columns:
                raise SystemExit("%s:列数应为 %d,实际 %d" % (name, columns, len(cols)))
            rows.append(cols)
    return rows


def check_row(slot, key, plain, cipher, label, failures):
    enc, dec = REFS[slot]
    got = enc(key, plain)
    if got != cipher:
        failures.append("%s 加密:期望 %s,参考实现 %s" % (label, cipher.hex(), got.hex()))
    back = dec(key, cipher)
    if back != plain:
        failures.append("%s 解密:期望 %s,参考实现 %s" % (label, plain.hex(), back.hex()))


def cmd_selftest():
    failures = []
    counts = {}

    for slot, expected in sorted(EXT_ROWS.items()):
        rows = read_rows("crypto_ext_slot%d_vector.txt" % slot, 3)
        if len(rows) != expected:
            failures.append("槽 %d 新向量应有 %d 行,实际 %d" % (slot, expected, len(rows)))
        for i, (k, p, c) in enumerate(rows):
            key, plain, cipher = bytes.fromhex(k), bytes.fromhex(p), bytes.fromhex(c)
            if len(key) != KEY_LEN[slot] or len(plain) != BLOCK[slot] or len(cipher) != BLOCK[slot]:
                failures.append("槽 %d 第 %d 行长度不对" % (slot, i + 1))
                continue
            check_row(slot, key, plain, cipher, "槽 %d 第 %d 行" % (slot, i + 1), failures)
        counts[slot] = len(rows)

    # 旧向量:每槽 1 行;密钥列对大切片只写了开头的字节,其余是零;槽 5 那一行是对象构造时的内部随机密钥,无法复现
    old_used = 0
    for row in read_rows("crypto_slots_vectors.txt", 7):
        slot = int(row[0])
        if slot not in REFS or row[3].startswith("SYNTH_CTOR_INTERNAL"):
            continue
        key = bytes.fromhex(row[3]).ljust(KEY_LEN[slot], b"\x00")
        check_row(slot, key, bytes.fromhex(row[4]), bytes.fromhex(row[5]), "旧向量 槽 %d" % slot, failures)
        old_used += 1
    if old_used != 4:
        failures.append("旧向量里可复现的行应为 4(槽 0、1、2、3),实际 %d" % old_used)

    # 槽 0 的旧文件:密钥列是写进对象字段的值(1 字节移位量 + 4 字节异或键,按 32 位数值写出),
    # 还原成 8 字节切片 = [移位量 小端 4 字节][异或键 小端 4 字节]
    shift_rows = read_rows("cryptoshift_encrypt_vector.txt", 3)
    if len(shift_rows) != 3:
        failures.append("cryptoshift_encrypt_vector.txt 应有 3 行,实际 %d" % len(shift_rows))
    for i, (k, p, c) in enumerate(shift_rows):
        field = bytes.fromhex(k)
        key = bytes([field[0], 0, 0, 0]) + bytes(reversed(field[1:5]))
        check_row(0, key, bytes.fromhex(p), bytes.fromhex(c), "cryptoshift 旧文件第 %d 行" % (i + 1), failures)

    for slot in sorted(counts):
        print("槽 %d:新向量 %d 行已与参考实现逐行核对" % (slot, counts[slot]))
    print("旧向量 %d 行 + 槽 0 旧文件 %d 行已核对" % (old_used, len(shift_rows)))
    if failures:
        for line in failures[:20]:
            print("不符:" + line)
        print("共 %d 处不符" % len(failures))
        return 1
    print("全部相符")
    return 0


def main():
    if "--selftest" in sys.argv[1:]:
        return cmd_selftest()
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
