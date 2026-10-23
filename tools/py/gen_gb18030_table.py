"""生成 packages/com.dnfunity.data/Runtime/Gb18030Table.cs:GB18030 的双字节部分(含 GBK、GB2312)到 UTF-16 的对照表。

用法:
  python3 tools/py/gen_gb18030_table.py            写文件(覆盖)
  python3 tools/py/gen_gb18030_table.py --check    只检查已有文件和 Python 编解码器是否一致

为什么要自带一张表:Unity 的 .NET Standard 2.1 配置在 IL2CPP(iOS、Android)下不保证带 GBK/GB18030 编码器,
而 Script.pvf 里的 *.kor.str 文本(物品名、说明、NPC 对白……)是 GB18030 编码的。这张表是公开的编码标准,不含任何原版内容。

表的形状:126 行(首字节 0x81..0xFE),每行 191 个字符(次字节 0x40..0xFE);没有定义的位置是 U+FFFD。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "packages", "com.dnfunity.data", "Runtime", "Gb18030Table.cs"))

LEAD_FIRST, LEAD_LAST = 0x81, 0xFE
TRAIL_FIRST, TRAIL_LAST = 0x40, 0xFE
ROWS = LEAD_LAST - LEAD_FIRST + 1
COLS = TRAIL_LAST - TRAIL_FIRST + 1


def build_rows():
    rows = []
    for lead in range(LEAD_FIRST, LEAD_LAST + 1):
        chars = []
        for trail in range(TRAIL_FIRST, TRAIL_LAST + 1):
            c = "\ufffd"
            if trail != 0x7F:
                try:
                    s = bytes([lead, trail]).decode("gb18030")
                    if len(s) == 1 and ord(s) <= 0xFFFF:
                        c = s
                except UnicodeDecodeError:
                    pass
            chars.append(c)
        rows.append("".join(chars))
    return rows


def literal(row):
    out = []
    for ch in row:
        o = ord(ch)
        if 0x4E00 <= o <= 0x9FFF:
            out.append(ch)
        else:
            out.append("\\u%04X" % o)
    return "".join(out)


def render(rows):
    lines = []
    lines.append("// 由 tools/py/gen_gb18030_table.py 生成,不要手改。")
    lines.append("namespace Dnf.Data")
    lines.append("{")
    lines.append("    /// <summary>")
    lines.append("    /// GB18030 双字节部分的对照表(公开的编码标准,不含原版内容):第 i 行对应首字节 0x81 + i,")
    lines.append("    /// 行内第 j 个字符对应次字节 0x40 + j;没有定义的位置是 U+FFFD。")
    lines.append("    /// </summary>")
    lines.append("    internal static class Gb18030Table")
    lines.append("    {")
    lines.append("        internal const int FirstLead = 0x81;")
    lines.append("        internal const int LastLead = 0xFE;")
    lines.append("        internal const int FirstTrail = 0x40;")
    lines.append("        internal const int LastTrail = 0xFE;")
    lines.append("")
    lines.append("        internal static readonly string[] Rows =")
    lines.append("        {")
    for r in rows:
        lines.append('            "%s",' % literal(r))
    lines.append("        };")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines) + "\n"


def main():
    rows = build_rows()
    assert len(rows) == ROWS and all(len(r) == COLS for r in rows)
    text = render(rows)
    if "--check" in sys.argv:
        with open(OUT, "r", encoding="utf-8") as f:
            current = f.read()
        if current != text:
            print("Gb18030Table.cs 与 Python 编解码器的结果不一致")
            sys.exit(1)
        print("Gb18030Table.cs 一致:%d 行 × %d 列" % (ROWS, COLS))
        return
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    defined = sum(1 for r in rows for c in r if c != "\ufffd")
    print("写出 %s(%d 个有定义的位置)" % (os.path.relpath(OUT), defined))


if __name__ == "__main__":
    main()
