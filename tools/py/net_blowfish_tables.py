#!/usr/bin/env python3
"""Blowfish 初值表(P 盒 18 个字 + 4 个 S 盒共 1024 个字)的生成与核对。

初值表就是 π 的十六进制小数位(去掉整数部分 3)依次取 32 位字,是公开算法常量(Schneier,1993)。
这里按定义用 Machin 公式(π = 16·arctan(1/5) − 4·arctan(1/239),Python 大整数)重算,不依赖任何抄来的表。

用法(在仓库根目录运行):
  python3 tools/py/net_blowfish_tables.py --check   核对 BlowfishCipher.cs 里的 InitP/InitS 与重算结果逐字一致(退出码 0 = 一致)
  python3 tools/py/net_blowfish_tables.py --print   按 BlowfishCipher.cs 的排版(每行 6 个字)打印两个数组的内容
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SOURCE = os.path.join(ROOT, "packages", "com.dnfunity.net", "Runtime", "Crypto", "BlowfishCipher.cs")
P_WORDS = 18
S_WORDS = 1024


def arctan_inverse(x, scale):
    """arctan(1/x) * scale,级数逐项整除(误差只落在保护位里)。"""
    x2 = x * x
    term = scale // x
    total = term
    k = 1
    sign = -1
    while term:
        term //= x2
        total += sign * (term // (2 * k + 1))
        sign = -sign
        k += 1
    return total


def pi_fraction_words(count, guard_bits=128):
    bits = 32 * count + guard_bits
    scale = 1 << bits
    pi_scaled = 16 * arctan_inverse(5, scale) - 4 * arctan_inverse(239, scale)
    fraction = (pi_scaled - (3 << bits)) >> guard_bits
    return [(fraction >> (32 * (count - 1 - i))) & 0xFFFFFFFF for i in range(count)]


def render(words):
    lines = []
    for i in range(0, len(words), 6):
        lines.append("            " + ", ".join("0x%08Xu" % w for w in words[i:i + 6]) + ",")
    return "\n".join(lines)


def source_array(text, name):
    m = re.search(r"uint\[\] %s =\s*\{(.*?)\};" % name, text, re.S)
    if not m:
        raise SystemExit("在 BlowfishCipher.cs 里找不到数组 " + name)
    return [int(h, 16) for h in re.findall(r"0x([0-9A-Fa-f]{8})u", m.group(1))]


def main():
    words = pi_fraction_words(P_WORDS + S_WORDS)
    if "--print" in sys.argv:
        print("InitP:\n" + render(words[:P_WORDS]))
        print("InitS:\n" + render(words[P_WORDS:]))
        return 0
    text = open(SOURCE, encoding="utf-8").read()
    ok = source_array(text, "InitP") == words[:P_WORDS] and source_array(text, "InitS") == words[P_WORDS:]
    print("InitP/InitS 与重算的 π 小数位%s" % ("逐字一致(共 %d 个字)" % len(words) if ok else "不一致"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
