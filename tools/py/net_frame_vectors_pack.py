#!/usr/bin/env python3
"""把"会话密码层与整帧"研究产出的制表符向量文件整理成仓库里的金标准文件(只搬运与瘦身,不改任何结果)。

输入目录里应有(都是本机模拟器跑服务端原函数得到的合成向量,非 REA):
  session_packet_vectors.tsv  session_block_oracle_vector.tsv  session_slot_meta_vector.tsv
  frame_vectors.tsv  frame_block_oracle_vector.tsv  frame_keys_vector.tsv  frame_reject_vector.tsv
  bootstrap_vectors.tsv  pi_layer_vectors.tsv  pi_first_layer_vector.tsv
输出(都带原文件的出处注释):
  frame_keys_vector.txt            合成密钥(key_id 与 334 字节的十六进制),只出现一次
  frame_session_packet_vector.txt  包级加解密向量,去掉每行重复的密钥列(改用 key_id)
  frame_block_oracle_vector.txt    各槽"块级预言机"表:key_id slot 明文块 密文块(三份来源合并去重)
  frame_slot_meta_vector.txt       各槽密钥长度与块长
  frame_frames_vector.txt          整帧向量(服务端到客户端 240 行、客户端到服务端 135 行)
  frame_bootstrap_vector.txt       固定种子变换
  frame_reject_vector.txt          拒绝帧与不加密帧
  frame_pi_vector.txt              包完整性第二层
  frame_pi1_vector.txt             包完整性第一层(载荷尾折叠哈希,客户端到服务端的检查范围)

用法:python3 tools/py/net_frame_vectors_pack.py --src <输入目录> --out src/Dnf.Net.Tests/Golden
"""
import argparse
import os
import sys


def read_tsv(path):
    comments, rows = [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            if line.startswith("#"):
                comments.append(line)
            else:
                rows.append(line.split("\t"))
    return comments, rows


def write(path, header, comments, rows):
    with open(path, "w", encoding="utf-8") as f:
        for h in header:
            f.write(h + "\n")
        for c in comments:
            f.write(c + "\n")
        for r in rows:
            f.write("\t".join(r) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    src = lambda n: os.path.join(a.src, n)
    out = lambda n: os.path.join(a.out, n)
    SYN = "# 来源:服务端会话密码层研究的本机模拟器向量(合成密钥与合成数据),此处只做整理,不改结果。"

    # 1) 密钥:会话向量里每行重复带 334 字节的密钥,这里抽成一份
    keys = {}
    c_sess, sess = read_tsv(src("session_packet_vectors.tsv"))
    for r in sess:
        kid, khex = r[3], r[4]
        assert keys.setdefault(kid, khex) == khex, "同一个 key_id 对应了不同的密钥"
    _, fk = read_tsv(src("frame_keys_vector.tsv"))
    for r in fk:
        assert keys.setdefault(r[0], r[1]) == r[1]
    for kid, khex in keys.items():
        assert len(khex) == 334 * 2, kid
    write(out("frame_keys_vector.txt"), [SYN, "# 合成会话密钥块(SYNTH,334 字节)。列:key_id 十六进制"], [], sorted(([k, v] for k, v in keys.items())))

    # 2) 包级向量:去掉密钥列
    rows = [r[:4] + r[5:] for r in sess]
    comments = [c.replace("key_hex ", "") for c in c_sess]
    write(out("frame_session_packet_vector.txt"), [SYN, "# 密钥改由 key_id 在 frame_keys_vector.txt 里查;原列 key_hex 已去掉。"], comments, rows)

    # 3) 块级预言机:三份合并去重
    merged, seen = [], set()
    cm = []
    for name in ("session_block_oracle_vector.tsv", "frame_block_oracle_vector.tsv"):
        c, rs = read_tsv(src(name))
        cm += c
        for r in rs:
            key = (r[0], r[1], r[2])
            if key in seen:
                continue
            seen.add(key)
            merged.append(r)
    # 同一明文块只能对应一个密文块(块函数是函数),密文块也应唯一(块函数是双射)
    d, inv = {}, {}
    for kid, slot, p, c in merged:
        assert d.setdefault((kid, slot, p), c) == c
        assert inv.setdefault((kid, slot, c), p) == p
    merged.sort(key=lambda r: (r[0], int(r[1]), r[2]))
    write(out("frame_block_oracle_vector.txt"), [SYN], sorted(set(cm)), merged)

    # 4) 其余原样搬运
    for src_name, dst in (("session_slot_meta_vector.tsv", "frame_slot_meta_vector.txt"), ("frame_vectors.tsv", "frame_frames_vector.txt"),
                          ("bootstrap_vectors.tsv", "frame_bootstrap_vector.txt"), ("frame_reject_vector.tsv", "frame_reject_vector.txt"),
                          ("pi_layer_vectors.tsv", "frame_pi_vector.txt"), ("pi_first_layer_vector.tsv", "frame_pi1_vector.txt")):
        c, rs = read_tsv(src(src_name))
        write(out(dst), [SYN], c, rs)
        print("%s -> %s:%d 行" % (src_name, dst, len(rs)))
    print("密钥 %d 把;包级 %d 行;预言机 %d 行" % (len(keys), len(rows), len(merged)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
