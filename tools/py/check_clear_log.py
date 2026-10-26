#!/usr/bin/env python3
"""检查播放器日志里的 DNF_CLEAR 行(M32,证据 E-124):评分字母与字母图的帧一致(SSS 0、SS 1、S 2、A 3、B 4、C 5、D 6、E 7、F 8),至少有指定行数。

用法: check_clear_log.py <日志> <最少行数>;全部对得上退出 0,否则打印哪一行不对并退出 1。只看字母与帧号,不涉及任何原版数值。
"""
import re
import sys

LETTERS = ["SSS", "SS", "S", "A", "B", "C", "D", "E", "F"]


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    path, minimum = sys.argv[1], int(sys.argv[2])
    count = 0
    bad = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"DNF_CLEAR rank=(\w+) frame=(\d+) ", line)
            if not m:
                continue
            count += 1
            frame = int(m.group(2))
            if frame >= len(LETTERS) or LETTERS[frame] != m.group(1):
                bad += 1
                print("字母与帧对不上:rank=%s frame=%d" % (m.group(1), frame))
    print("DNF_CLEAR 行 %d 条,字母与帧不一致 %d 条" % (count, bad))
    return 0 if bad == 0 and count >= minimum else 1


if __name__ == "__main__":
    sys.exit(main())
