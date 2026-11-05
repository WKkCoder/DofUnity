#!/usr/bin/env python3
"""生成"载荷读写原语"与"CHANNELINFO 载荷"的金标准向量(本机模拟器测量,非 REA)。

做法:把服务端 ELF(re/server/bin/df_game_r.orig)映射进 Unicorn,直接调用其中 PacketBuf 的原函数
(put_byte/put_short/put_int/put_str/put_binary、get_byte/get_short/get_int/get_str/get_binary),
喂合成输入,记录返回值、游标与写入/读出的字节。C# 的 PacketWriter/PacketReader/ChannelInfo 测试逐行对拍这些结果,
所以期望值来自原函数的执行结果,不是从实现里抄来的。

函数地址(服务端符号表):
  put_byte(int&,int) 0x0858c978   put_short(int&,int) 0x0858c9c8   put_int(int&,int) 0x0858cad4
  put_str(int&,char*,int) 0x0858cc68   put_binary(int&,char*,int) 0x0858cd68
  get_byte(unsigned char&) 0x0858cf70  get_short(short&) 0x0858cfc0  get_int(int&) 0x0858d0f0
  get_str(char*,int,int) 0x0858d2bc    get_binary(char*,int) 0x0858d3b2
PacketBuf 对象布局(读自 clear 0x0858c8e2 / 各原语):+0 字节序成员(0 小端、1 大端)、+4 游标、+8 长度、+0x14 缓冲指针(= 对象 + 0x18)。
写原语检查"游标 + 所需字节 > 0x172b4"(is_ptr_end 0x0858cec4),读原语检查"长度 < 游标 + 所需字节"(is_len_end 0x0858cefc)。

用法(需要 unicorn:re/venv/bin/python):
  re/venv/bin/python tools/py/net_packetbuf_vectors.py <原语向量输出> <CHANNELINFO 向量输出>
只读使用 ELF,不联网,不读任何配置。
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from elf_emu import ElfEmu  # noqa: E402

A_PUT_BYTE, A_PUT_SHORT, A_PUT_INT = 0x0858C978, 0x0858C9C8, 0x0858CAD4
A_PUT_STR, A_PUT_BIN = 0x0858CC68, 0x0858CD68
A_GET_BYTE, A_GET_SHORT, A_GET_INT = 0x0858CF70, 0x0858CFC0, 0x0858D0F0
A_GET_STR, A_GET_BIN = 0x0858D2BC, 0x0858D3B2

PB_SIZE = 0x17400
PB_BUF = 0x18
PUT_LIMIT = 0x172B4
FILL = 0xEE


class Rig:
    def __init__(self):
        self.emu = ElfEmu()
        self.pb = self.emu.alloc(PB_SIZE)
        self.cur = self.emu.alloc(16)        # 写原语的游标变量(int&)
        self.out = self.emu.alloc(0x400)     # 读原语的输出缓冲
        self.src = self.emu.alloc(0x400)     # 写原语的来源缓冲

    def reset(self, enc, length=0, cursor=0, data=b""):
        e = self.emu
        e.write(self.pb, b"\0" * PB_SIZE)
        e.write_u32(self.pb + 0, enc)
        e.write_u32(self.pb + 4, cursor)
        e.write_u32(self.pb + 8, length)
        e.write_u32(self.pb + 0x14, self.pb + PB_BUF)
        if data:
            e.write(self.pb + PB_BUF, data)
        e.write_u32(self.cur, cursor)
        e.write(self.out, bytes([FILL]) * 0x400)

    def buf(self, a, n):
        return self.emu.read(self.pb + PB_BUF + a, n)


def put_cases():
    """(op, 参数, 起始游标)。写游标上限 0x172b4;靠近上限的几组是边界。"""
    cases = []
    starts = (0xF, 0x172B4 - 8, 0x172B4 - 4, 0x172B4 - 3, 0x172B4 - 2, 0x172B4 - 1, 0x172B4)
    for st in starts:
        for v in (0, 1, 0x7F, 0x80, 0xFF, 0x1FF, -1):
            cases.append(("byte", v, st))
        for v in (0, 0x1234, 0xFFFF, 0x12345, -2):
            cases.append(("short", v, st))
        for v in (0, 0x01020304, 0x7FFFFFFF, -0x80000000, -1):
            cases.append(("int", v, st))
        # 字符串:(字节, 最大个数)——写入个数 = min(遇 0 之前的长度, 最大个数);空间检查按最大个数
        for text, mx in ((b"", 0), (b"abc", 3), (b"abc", 8), (b"abcdef", 4), (b"ab\0cd", 5), (b"abc", 0), (b"x" * 16, 16), (b"x" * 20, 16)):
            cases.append(("str", (text, mx), st))
        for blob in (b"\x01", b"\x01\x02\x03\x04\x05", b"\x01" * 32):
            cases.append(("bin", blob, st))
    return cases


def run_put(rig, enc, op, arg, st):
    e = rig.emu
    rig.reset(enc, cursor=st)
    pre = rig.buf(st, 24) if st + 24 <= PB_SIZE - PB_BUF else b""
    if op == "byte":
        ret = e.call(A_PUT_BYTE, (rig.pb, rig.cur, arg))
        argtxt = str(arg)
    elif op == "short":
        ret = e.call(A_PUT_SHORT, (rig.pb, rig.cur, arg))
        argtxt = str(arg)
    elif op == "int":
        ret = e.call(A_PUT_INT, (rig.pb, rig.cur, arg))
        argtxt = str(arg)
    elif op == "str":
        text, mx = arg
        e.write(rig.src, text + b"\0")
        ret = e.call(A_PUT_STR, (rig.pb, rig.cur, rig.src, mx))
        argtxt = "%s:%d" % (text.hex(), mx)
    else:
        e.write(rig.src, arg + b"\0")
        ret = e.call(A_PUT_BIN, (rig.pb, rig.cur, rig.src if arg else 0, len(arg)))
        argtxt = arg.hex()
    end = e.read_u32(rig.cur)
    written = rig.buf(st, end - st) if end > st else b""
    if ret & 0xFF == 0:
        # 失败时游标不动、缓冲不变(向量里把这一点固定下来)
        assert end == st, "失败却移动了游标"
        if pre:
            assert rig.buf(st, 24) == pre, "失败却写了字节"
    return "put\t%d\t%d\t%s\t%s\t%d\t%d\t%s" % (enc, st, op, argtxt or "-", ret & 0xFF, end, written.hex() or "-")


def get_cases():
    """(op, 参数, 数据, 长度, 起始游标)。"""
    cases = []
    data = bytes(range(0x11, 0x11 + 24))
    for ln in (0, 1, 2, 3, 4, 5, 8):
        for st in (0, 1, ln):
            if st > ln:
                continue
            d = data[:ln]
            cases += [("byte", "-", d, ln, st), ("short", "-", d, ln, st), ("int", "-", d, ln, st)]
    hi = bytes([0xFF, 0x80, 0x00, 0x7F, 0x80, 0xFE, 0x01, 0x02])
    for st in (0, 2):
        cases += [("byte", "-", hi, 8, st), ("short", "-", hi, 8, st), ("int", "-", hi, 8, st)]
    # get_str(缓冲大小, 长度)
    s1 = b"hello\0world!!!"          # 内嵌 0
    s2 = b"abcdefgh"                # 无 0
    for blob in (s1, s2):
        for bs, n in ((16, 5), (16, 8), (16, 6), (16, 0), (8, 8), (8, 7), (9, 8), (0, 0), (1, 0), (2, 1), (16, 100), (16, -1), (-1, 3), (6, 12), (16, 14)):
            cases.append(("str", "%d:%d" % (bs, n), blob, len(blob), 0))
    cases.append(("str", "16:3", b"\0\0\0\0", 4, 0))               # 开头就是 0
    cases.append(("str", "16:4", b"ab\0\0", 4, 0))
    cases.append(("str", "5:4", b"abcdXYZ", 7, 2))                 # 游标不在 0
    # get_binary(n)
    for n in (1, 3, 5, 6):
        cases.append(("bin", str(n), data[:5], 5, 0))
    cases.append(("bin", "2", data[:5], 5, 3))
    cases.append(("bin", "3", data[:5], 5, 3))
    return cases


def run_get(rig, enc, op, arg, data, ln, st):
    e = rig.emu
    rig.reset(enc, length=ln, cursor=st, data=data)
    out = rig.out
    if op == "byte":
        ret = e.call(A_GET_BYTE, (rig.pb, out))
    elif op == "short":
        ret = e.call(A_GET_SHORT, (rig.pb, out))
    elif op == "int":
        ret = e.call(A_GET_INT, (rig.pb, out))
    elif op == "str":
        bs, n = (int(x) for x in arg.split(":"))
        ret = e.call(A_GET_STR, (rig.pb, out, bs, n))
    else:
        ret = e.call(A_GET_BIN, (rig.pb, out, int(arg)))
    end = e.read_u32(rig.pb + 4)
    if ret & 0xFF == 0:
        assert end == st or op == "str", "读失败却移动了游标"
    return "get\t%d\t%d\t%d\t%s\t%s\t%s\t%d\t%d\t%s" % (enc, ln, st, data[:ln].hex() or "-", op, arg, ret & 0xFF, end, e.read(out, 16).hex())


def channelinfo_cases():
    key = bytes(((i * 5) ^ (i >> 2) ^ 0x33) & 0xFF for i in range(334))
    return key, [
        (b"SYNTH_CHANNEL", 0x1234, 0x56789, bytes([1, 2, 3]), 0x65432100, [b"SYNTH_A", b"SYNTH_B"], (7, 9)),
        (b"X", 0, 0, bytes([0, 0, 0]), 1, [], None),
        (b"SYNTH_NAME_LONGER_THAN_SIXTEEN", 0xFFFFFFFF, 0x7FFFFFFF, bytes([255, 128, 1]), 0xFFFFFFFF, [b"SYNTH_ADDRESS_LONGER_THAN_16_BYTES", b"S"], (0xFFFFFFFF, 0)),
        (b"", 5, 6, bytes([9, 8, 7]), 7, [b"A"], (1, 2)),
    ]


def build_channelinfo(rig, key, name, seed, rnd, env, sec, addrs, tail):
    """按服务端 CUser::InitOnAccept(0x086493c8)里 put 调用的顺序,用原函数拼载荷:
    put_binary(密钥块) → put_int(strlen) + put_str(名字) → put_int(日期种子) → put_int(随机数) → put_byte ×3 → put_int(秒)
    → put_int(地址个数) → 每个地址 put_int(strlen) + put_str(s, 0x10) → 个数非 0 时 put_int ×2 → put_int(0x25e) → put_int(0x232)。"""
    e = rig.emu
    rig.reset(0, cursor=0xF)

    def put_int(v):
        assert e.call(A_PUT_INT, (rig.pb, rig.cur, v)) & 0xFF == 1

    def put_byte(v):
        assert e.call(A_PUT_BYTE, (rig.pb, rig.cur, v)) & 0xFF == 1

    def put_str(s, mx):
        e.write(rig.src, s + b"\0")
        assert e.call(A_PUT_STR, (rig.pb, rig.cur, rig.src, mx)) & 0xFF == 1
    e.write(rig.src, key)
    assert e.call(A_PUT_BIN, (rig.pb, rig.cur, rig.src, len(key))) & 0xFF == 1
    put_int(len(name))
    put_str(name, len(name))
    put_int(seed)
    put_int(rnd)
    for b in env:
        put_byte(b)
    put_int(sec)
    put_int(len(addrs))
    for s in addrs:
        put_int(len(s))
        put_str(s, 0x10)
    if addrs:
        put_int(tail[0])
        put_int(tail[1])
    put_int(0x25E)
    put_int(0x232)
    end = e.read_u32(rig.cur)
    return rig.buf(0xF, end - 0xF)


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    rig = Rig()
    lines = ["# 本机模拟器测量,非 REA:服务端 PacketBuf 原函数(见 tools/py/net_packetbuf_vectors.py)对合成输入的执行结果。",
             "# put 列:kind enc start op arg ret end written_hex;arg:int 为十进制(byte/short 取低位),str 为 <字节hex>:<最大个数>,bin 为 <字节hex>;失败时 end==start 且缓冲不变。",
             "# get 列:kind enc len start data_hex op arg ret end out_hex;arg:str 为 <缓冲大小>:<长度>,bin 为 <长度>;out_hex 是输出缓冲前 16 字节(事先填 0xEE,所以能看出有没有写结尾 0)。",
             "# enc:包对象的字节序成员,0 = 小端,1 = 大端;ret 为 1 成功、0 失败。"]
    npu = nge = 0
    for enc in (0, 1):
        for op, arg, st in put_cases():
            lines.append(run_put(rig, enc, op, arg, st))
            npu += 1
        for op, arg, data, ln, st in get_cases():
            lines.append(run_get(rig, enc, op, arg, data, ln, st))
            nge += 1
    with open(sys.argv[1], "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("原语向量:put %d 行、get %d 行" % (npu, nge))

    key, cases = channelinfo_cases()
    out = ["# 本机模拟器测量,非 REA:用服务端 PacketBuf 原函数按 CUser::InitOnAccept(0x086493c8)的 put 顺序拼出的 CHANNELINFO 载荷(合成数据)。",
           "# 密钥块是合成的:第 i 字节 = ((i*5) ^ (i>>2) ^ 0x33) & 0xFF,i 从 0 到 333(SYNTH_KEY)。",
           "# 列:name_hex seed rand env_hex time addr_hex_list(逗号分隔,空为 -) tail0 tail1(无地址时 -) payload_hex"]
    for name, seed, rnd, env, sec, addrs, tail in cases:
        payload = build_channelinfo(rig, key, name, seed, rnd, env, sec, addrs, tail)
        out.append("\t".join([
            name.hex() or "-", str(seed), str(rnd), env.hex(), str(sec),
            ",".join(a.hex() for a in addrs) or "-",
            str(tail[0]) if tail else "-", str(tail[1]) if tail else "-", payload.hex()]))
    with open(sys.argv[2], "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")
    print("CHANNELINFO 向量:%d 行" % len(cases))
    return 0


if __name__ == "__main__":
    sys.exit(main())
