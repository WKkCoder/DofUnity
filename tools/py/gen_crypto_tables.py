"""校验(或重新生成)三个密码类里内嵌的公开算法常量表,免得手抄出错。

涉及的表(都是公开标准里的常量,不含任何原版内容):
  Cast5Cipher.cs     8 个 S 盒(S1..S8,各 256 个 32 位字)   来源:RFC 2144 附录 A 的纯文本
  SkipjackCipher.cs  F 表(256 字节)                          来源:libtomcrypt 公开源码 skipjack.c
  AnubisCipher.cs    原版 S 盒(256 字节,非 tweak 版)         来源:libtomcrypt 公开源码 anubis.c(不开 tweak 选项的那一组 T4 表的低字节)

输入是调用者自己下载好的公开文本文件(脚本不联网):
  python3 tools/py/gen_crypto_tables.py --check --rfc2144 <rfc2144.txt> --skipjack-c <skipjack.c> --anubis-c <anubis.c>
  python3 tools/py/gen_crypto_tables.py --write --rfc2144 <rfc2144.txt> --skipjack-c <skipjack.c> --anubis-c <anubis.c>
--check 只比对(一致退出 0,不一致打印差异并退出 1);--write 用公开文本里的表覆盖 .cs 里的表段。
只给部分来源参数时只处理对应的表。
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CRYPTO_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "packages", "com.dnfunity.net", "Runtime", "Crypto"))

INDENT = " " * 12


def read_text(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def cast5_from_rfc(text):
    """RFC 2144 附录 A:每个 S 盒是一段 8 位十六进制字的列表。"""
    start = text.index("Appendix A. S-Boxes")
    end = text.index("Appendix B. Test Vectors")
    parts = re.split(r"S-Box S(\d)", text[start:end])
    boxes = {}
    for i in range(1, len(parts), 2):
        boxes[int(parts[i])] = [int(x, 16) for x in re.findall(r"\b([0-9a-f]{8})\b", parts[i + 1])]
    if sorted(boxes) != list(range(1, 9)) or any(len(v) != 256 for v in boxes.values()):
        raise ValueError("RFC 附录 A 解析结果不是 8 个 256 项的表")
    return boxes


def skipjack_from_ltc(text):
    m = re.search(r"static const unsigned char sbox\[256\] = \{(.*?)\};", text, re.S)
    table = [int(x, 16) for x in re.findall(r"0x([0-9a-fA-F]{2})", m.group(1))]
    if len(table) != 256:
        raise ValueError("Skipjack F 表解析结果不是 256 项")
    return table


def anubis_from_ltc(text):
    """不开 tweak 选项的那一组表在 #else 之后;其中 T4[x] 的四个字节都等于 S 盒 S[x]。"""
    m = re.search(r"#if defined\(LTC_ANUBIS_TWEAK\)(.*?)\n#else(.*?)\n#endif", text, re.S)
    original = m.group(2)
    t4 = re.search(r"static const ulong32 T4\[256\] = \{(.*?)\};", original, re.S)
    words = [int(x, 16) for x in re.findall(r"0x([0-9a-fA-F]{8})U", t4.group(1))]
    if len(words) != 256 or any(w != (w & 0xFF) * 0x01010101 for w in words):
        raise ValueError("Anubis T4 解析结果不是 256 项逐字节重复的表")
    return [w & 0xFF for w in words]


def render(values, width, per_line):
    lines = []
    for i in range(0, len(values), per_line):
        lines.append(INDENT + ", ".join("0x%0*X" % (width, v) for v in values[i:i + per_line]) + ",")
    return "\n".join(lines)


def array_pattern(name, elem):
    # 例:private static readonly uint[] S1 =\n        {\n ... \n        };
    return re.compile(r"(private static readonly %s\[\] %s =\n\s*\{\n)(.*?)(\n\s*\};)" % (elem, name), re.S)


def parse_literals(body):
    return [int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]+)", body)]


def process(path, name, elem, expected, width, per_line, write):
    """比对(或改写)一个数组字面量;返回是否一致。"""
    text = read_text(path)
    pat = array_pattern(name, elem)
    m = pat.search(text)
    if not m:
        print("没有在 %s 里找到数组 %s" % (os.path.basename(path), name))
        return False
    have = parse_literals(m.group(2))
    same = have == expected
    if same:
        print("一致  %-12s %s(%d 项)" % (os.path.basename(path), name, len(expected)))
    else:
        diff = [i for i in range(min(len(have), len(expected))) if have[i] != expected[i]]
        print("不一致 %-12s %s:代码里 %d 项,公开文本 %d 项,首个不同下标 %s" % (
            os.path.basename(path), name, len(have), len(expected), diff[:5]))
    if write and not same:
        new_text = text[:m.start(2)] + render(expected, width, per_line) + text[m.end(2):]
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(new_text)
        print("已改写 %s 的 %s" % (os.path.basename(path), name))
        return True
    return same


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="只比对")
    mode.add_argument("--write", action="store_true", help="覆盖 .cs 里的表段")
    ap.add_argument("--rfc2144", help="RFC 2144 纯文本")
    ap.add_argument("--skipjack-c", help="libtomcrypt 的 skipjack.c")
    ap.add_argument("--anubis-c", help="libtomcrypt 的 anubis.c")
    args = ap.parse_args()
    if not (args.rfc2144 or args.skipjack_c or args.anubis_c):
        ap.error("至少给一个来源文件")

    ok = True
    if args.rfc2144:
        boxes = cast5_from_rfc(read_text(args.rfc2144))
        for n in range(1, 9):
            ok &= process(os.path.join(CRYPTO_DIR, "Cast5Cipher.cs"), "S%d" % n, "uint", boxes[n], 8, 8, args.write)
    if args.skipjack_c:
        ok &= process(os.path.join(CRYPTO_DIR, "SkipjackCipher.cs"), "F", "byte", skipjack_from_ltc(read_text(args.skipjack_c)), 2, 16, args.write)
    if args.anubis_c:
        ok &= process(os.path.join(CRYPTO_DIR, "AnubisCipher.cs"), "Sbox", "byte", anubis_from_ltc(read_text(args.anubis_c)), 2, 16, args.write)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
