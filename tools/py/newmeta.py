#!/usr/bin/env python3
"""给新的 .cs 文件写 Unity 的 .meta(fileFormatVersion 2 + 随机 guid,无结尾换行,与仓库里已有的 .meta 一致)。用法:newmeta.py 文件.cs [...]"""
import sys, uuid, os

for path in sys.argv[1:]:
    meta = path + ".meta"
    if os.path.exists(meta):
        print("已存在,跳过:", meta)
        continue
    with open(meta, "w", newline="") as f:
        f.write("fileFormatVersion: 2\nguid: " + uuid.uuid4().hex)
    print("写了", meta)
