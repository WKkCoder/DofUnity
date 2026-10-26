#!/usr/bin/env python3
"""检查播放器日志里的 DNF_CLEAR 行(M32,证据 E-124;M33 起行首多了 difficulty= 与 opened=,证据 E-125):
评分字母与字母图的帧一致(SSS 0、SS 1、S 2、A 3、B 4、C 5、D 6、E 7、F 8),难度在 0 到 4、新开放的难度在 0 到 3,至少有指定行数。

用法: check_clear_log.py <日志> <最少行数> [<最少的难度大于 0 的行数>];全部对得上退出 0,否则打印哪一行不对并退出 1。
只看字母、帧号与难度编号,不涉及任何原版数值。
"""
import re
import sys

LETTERS = ["SSS", "SS", "S", "A", "B", "C", "D", "E", "F"]


def main() -> int:
    if len(sys.argv) not in (3, 4):
        print(__doc__)
        return 2
    path, minimum = sys.argv[1], int(sys.argv[2])
    minimum_hard = int(sys.argv[3]) if len(sys.argv) == 4 else 0
    count = 0
    bad = 0
    hard = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"DNF_CLEAR (?:difficulty=(\d+) opened=(\d+) )?rank=(\w+) frame=(\d+) ", line)
            if not m:
                continue
            count += 1
            frame = int(m.group(4))
            if frame >= len(LETTERS) or LETTERS[frame] != m.group(3):
                bad += 1
                print("字母与帧对不上:rank=%s frame=%d" % (m.group(3), frame))
            if m.group(1) is not None:
                difficulty, opened = int(m.group(1)), int(m.group(2))
                if difficulty > 4 or opened > 3:
                    bad += 1
                    print("难度或新开放的难度越界:difficulty=%d opened=%d" % (difficulty, opened))
                if difficulty > 0:
                    hard += 1
    print("DNF_CLEAR 行 %d 条(难度大于 0 的 %d 条),不一致 %d 条" % (count, hard, bad))
    return 0 if bad == 0 and count >= minimum and hard >= minimum_hard else 1


if __name__ == "__main__":
    sys.exit(main())
