"""把 PVF 里的标记流脚本(`B0 D0` + 5 字节标记)按"一个标签一行"打成可读文本,方便人工研究。

用法:
  python3 tools/py/script_dump.py --find seria --ext map        按路径子串(与扩展名)查找
  python3 tools/py/script_dump.py map/town/seriaroom.map        打印一个脚本
  python3 tools/py/script_dump.py <路径> --out re/notes/x.txt   写到文件(re/ 不入库)

输出里:整数直接写;浮点按 32 位解释;字符串用引号;类型 8 的"连接符"写成 <c:值>;
类型 9+10 的"数值+字符串对"写成 (数值,"字符串");类型 6 写成 <data:十六进制>。
注意:这是研究用的查看器。输出含原版脚本内容,只留在本机(re/),不要提交。
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D


class Strings:
    def __init__(self, pvf):
        st = pvf.get("stringtable.bin")
        n = struct.unpack_from("<I", st, 0)[0]
        self.st = st
        self.offs = struct.unpack_from("<%dI" % (n + 1), st, 4)
        self.cache = {}

    def get(self, i):
        s = self.cache.get(i)
        if s is None:
            if 0 <= i < len(self.offs) - 1:
                raw = self.st[4 + self.offs[i]: 4 + self.offs[i + 1]]
                try:
                    s = raw.decode("ascii")
                except UnicodeDecodeError:
                    # 非 ASCII 的名字:先按 GB18030 看(显示用);库里的权威表示仍是原始字节
                    s = raw.decode("gb18030", errors="replace")
            else:
                s = "<bad-string-index %d>" % i
            self.cache[i] = s
        return s


def decode_text(raw, strings):
    """返回 [(标签或 None, 值文本列表)]。"""
    lines = []
    cur = None
    vals = []
    toks = list(struct.iter_unpack("<Bi", raw[2:]))
    k = 0
    while k < len(toks):
        ty, v = toks[k]
        if ty == 5:
            if cur is not None or vals:
                lines.append((cur, vals))
            cur = strings.get(v)
            vals = []
        elif ty == 2:
            vals.append(str(v))
        elif ty == 3:
            vals.append("0x%X" % (v & 0xFFFFFFFF))
        elif ty == 4:
            f = struct.unpack("<f", struct.pack("<i", v))[0]
            vals.append(("%.6g" % f))
        elif ty == 7:
            vals.append('"%s"' % strings.get(v))
        elif ty == 8:
            vals.append("<c:%d>" % v)
        elif ty == 9 and k + 1 < len(toks) and toks[k + 1][0] == 10:
            vals.append('(%d,"%s")' % (v, strings.get(toks[k + 1][1])))
            k += 1
        elif ty == 6:
            vals.append("<data:%08X>" % (v & 0xFFFFFFFF))
        else:
            vals.append("<t%d:%d>" % (ty, v))
        k += 1
    if cur is not None or vals:
        lines.append((cur, vals))
    return lines


def to_text(raw, strings):
    if raw[:2] != b"\xb0\xd0":
        return None
    out = []
    for tag, vals in decode_text(raw, strings):
        out.append(("%s %s" % (tag or "(无标签)", " ".join(vals))).rstrip())
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path", nargs="?")
    ap.add_argument("--find")
    ap.add_argument("--ext")
    ap.add_argument("--out")
    ap.add_argument("--limit", type=int, default=60)
    args = ap.parse_args()

    p = D.Pvf()
    if args.find:
        needle = args.find.lower()
        n = 0
        for e in p.entries:
            lp = e.path.lower()
            if needle in lp and (not args.ext or lp.endswith("." + args.ext.lower())):
                print(e.path, e.length)
                n += 1
                if n >= args.limit:
                    print("... (用 --limit 看更多)")
                    break
        return
    if not args.path:
        ap.error("需要路径或 --find")
    raw = p.get(args.path)
    if raw is None:
        sys.exit("PVF 里没有 " + args.path)
    text = to_text(raw, Strings(p))
    if text is None:
        sys.exit("不是标记流脚本(首两字节不是 B0 D0):前 16 字节 " + raw[:16].hex())
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
        print("已写入", args.out, len(text), "字符")
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
