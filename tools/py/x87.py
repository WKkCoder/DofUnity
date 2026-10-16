#!/usr/bin/env python3
"""x87 扩展精度(64 位有效位、就近舍入偶数)的精确模型,只含服务端数值函数用到的几种运算。

原版(32 位 x86、x87 FPU、默认控制字 0x37F)里,一条 C 语句的中间结果留在 80 位栈寄存器里,只有 FSTP 到 float/double 时才舍入,
FISTP 时用截断。用 Python 双精度模拟会在"整数边界附近"差 1。这里用有理数精确运算再按 64 位有效位舍入,
与真机(或 Unicorn 里的 x87)逐位一致。仅处理有限值与正常范围(能力数值函数的输入都是整数和小浮点,不会出现无穷/非数/次正规数)。

值一律用 fractions.Fraction 表示(所有被舍入后的值都是二进制小数,能精确表示)。
"""
import struct
from fractions import Fraction

EXT_BITS = 64
F32_BITS = 24
F64_BITS = 53


def round_sig(x, bits):
    """把有理数 x 就近舍入(平局取偶)到 bits 位有效二进制位。"""
    if x == 0:
        return Fraction(0)
    neg = x < 0
    a = -x if neg else x
    n, d = a.numerator, a.denominator
    e = n.bit_length() - d.bit_length()          # a 在 [2^(e-1), 2^(e+1)) 内
    # 精确判断 a < 2^e 就把 e 减 1,使 2^e <= a < 2^(e+1)
    lhs = n << (-e) if e < 0 else n
    rhs = d << e if e > 0 else d
    if lhs < rhs:
        e -= 1
    shift = bits - 1 - e
    if shift >= 0:
        nn, dd = n << shift, d
    else:
        nn, dd = n, d << (-shift)
    q, r = divmod(nn, dd)
    twice = 2 * r
    if twice > dd or (twice == dd and (q & 1)):
        q += 1
    res = Fraction(q) * (Fraction(2) ** (-shift))
    return -res if neg else res


def ext(x):
    """任何有理数 -> 扩展精度寄存器里的值。"""
    return round_sig(Fraction(x), EXT_BITS)


def from_int(i):
    """FILD:32 位整数精确载入。"""
    return Fraction(i)


def from_f32_bits(bits):
    """FLD float:把 32 位模式读成 float(精确)。"""
    return Fraction(struct.unpack("<f", struct.pack("<i", bits))[0])


def from_f32(x):
    return Fraction(struct.unpack("<f", struct.pack("<f", x))[0])


def div(a, b):
    return ext(Fraction(a) / Fraction(b))


def mul(a, b):
    return ext(Fraction(a) * Fraction(b))


def add(a, b):
    return ext(Fraction(a) + Fraction(b))


def to_f32(x):
    """FSTP float:舍入到 24 位有效位。返回值仍是 Fraction(精确的 float 值)。"""
    return round_sig(Fraction(x), F32_BITS)


def f32_bits(x):
    """float 值(Fraction)的 32 位模式(有符号整数)。"""
    return struct.unpack("<i", struct.pack("<f", float(x)))[0]


def trunc_i32(x):
    """FISTP(舍入控制 = 截断):向零取整;超出 32 位有符号范围得 -2^31(整数不确定值)。"""
    v = int(Fraction(x))
    if v >= 2 ** 31 or v < -(2 ** 31):
        return -(2 ** 31)
    return v
