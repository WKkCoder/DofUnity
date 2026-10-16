#!/usr/bin/env python3
"""全库 .dgn 迷宫的出口位掩码校验(证据 E-070;不是 REA,是对用户自备 Script.pvf 的本机读取)。

读每个副本定义的 [maze info]:[size] 列 行;[greed] 每格两个字母,按行优先;首字母 = 'A' + 出口位掩码。
检查三件事:
  1. 在"右、左、上、下各占一个位"的 24 种排列里,哪些能让所有多格迷宫里相邻两格的出口两侧一致(期望只有一种:右=1、上=2、左=4、下=8);
  2. 按这个排列,迷宫外的出口位(格在边缘、出口朝外)出现在哪里(期望:多格迷宫没有,单格迷宫只有朝右的);
  3. 每格的第二个字母是否都等于第一个。
用法: . tools/env.sh && python3 tools/py/dgn_maze_check.py
只打印统计,不写任何文件,也不输出原版内容。
"""
import itertools
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402

DIRS = ("right", "left", "up", "down")


def read_mazes(pvf, strings):
    """(路径, 列, 行, 首字母编码表, 第二字母是否全等于第一) 的列表;返回无法解析的路径。"""
    mazes, broken = [], []
    for e in pvf.entries:
        if not e.path.lower().endswith(".dgn"):
            continue
        raw = pvf.get(e.path)
        if raw is None or raw[:2] != b"\xb0\xd0":
            continue
        cols = rows = None
        greed = None
        for tag, vals in S.decode_text(raw, strings):
            if tag == "[size]" and len(vals) >= 2:
                cols, rows = int(vals[0]), int(vals[1])
            elif tag == "[greed]" and vals:
                greed = vals[0].strip('"')
        if cols is None or greed is None:
            continue
        letters = re.sub(r"[^A-Za-z]", "", greed)
        if len(letters) != cols * rows * 2:
            broken.append(e.path)
            continue
        first = {}
        pairs_equal = True
        for y in range(rows):
            for x in range(cols):
                a = letters[(y * cols + x) * 2].upper()
                b = letters[(y * cols + x) * 2 + 1].upper()
                pairs_equal &= a == b
                first[(x, y)] = ord(a) - 65
        mazes.append((e.path, cols, rows, first, pairs_equal))
    return mazes, broken


def consistent(cells, bit):
    for (x, y), m in cells.items():
        for d, dx, dy, opp in (("right", 1, 0, "left"), ("down", 0, 1, "up"), ("left", -1, 0, "right"), ("up", 0, -1, "down")):
            n = cells.get((x + dx, y + dy))
            if n is not None and bool(m & bit[d]) != bool(n & bit[opp]):
                return False
    return True


def main():
    pvf = D.Pvf()
    strings = S.Strings(pvf)
    mazes, broken = read_mazes(pvf, strings)
    multi = [m for m in mazes if m[1] * m[2] > 1]
    print("有 [maze info] 且格数对得上的 .dgn:", len(mazes), " 其中 1×1:", len(mazes) - len(multi), " 多格:", len(multi), " 格数对不上:", len(broken))
    print("第二个字母全部等于第一个的迷宫:", sum(1 for m in mazes if m[4]), "/", len(mazes))
    good = []
    for perm in itertools.permutations(range(4)):
        bit = {d: 1 << perm[i] for i, d in enumerate(DIRS)}
        if all(consistent(m[3], bit) for m in multi):
            good.append(bit)
    print("让全部多格迷宫两侧一致的位排列:", len(good), good)
    if len(good) != 1:
        return 1
    bit = good[0]
    out = {d: 0 for d in DIRS}
    out_multi = 0
    biggest = 0
    for _path, cols, rows, cells, _eq in mazes:
        for (x, y), m in cells.items():
            biggest = max(biggest, m)
            edges = (("left", x == 0), ("right", x == cols - 1), ("up", y == 0), ("down", y == rows - 1))
            for d, at_edge in edges:
                if at_edge and m & bit[d]:
                    out[d] += 1
                    if cols * rows > 1:
                        out_multi += 1
    print("最大编码:", biggest, "(字母", chr(65 + biggest) + ")")
    print("朝迷宫外的出口位(按方向):", out, " 其中在多格迷宫里的:", out_multi)
    return 0


if __name__ == "__main__":
    sys.exit(main())
