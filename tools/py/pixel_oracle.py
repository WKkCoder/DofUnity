"""生成"逐像素对拍"的参考答案:随机抽 1000 个帧,用 Python 参考实现解码成 RGBA 并记下 SHA-256。

C# 命令行(dnf verify-pixels)读取 out/oracle_pixels.json,用自己的解码器重新算一遍,逐帧比较哈希。
抽样 = 分层 + 随机:
  - 每个类别各取 60 个:链接帧、(类型,压缩) 六种组合、空帧(宽或高为 0)、疑似占位帧(1×1、zlib、size<=4)
  - 其余从全部帧里均匀随机补足,总数 1000
这样稀有格式(例如 ARGB4444 未压缩只有 632 帧)也一定被覆盖。种子固定,结果可复现。
只输出 NPK 名、条目名、帧号与哈希,不输出像素。

用法: python3 pixel_oracle.py [种子]
"""
import hashlib
import json
import os
import random
import struct
import sys
import time
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from dnfpaths import ensure_out_dir, require_dnf_dir

PER_CLASS = 60
TOTAL = 1000


def classify(fr):
    if fr.type == D.T_LINK:
        return "link"
    if fr.w == 0 or fr.h == 0:
        return "empty"
    if fr.comp == 6 and fr.w * fr.h <= 1 and fr.size <= 4:
        return "placeholder"
    return "t%02X_c%d" % (fr.type, fr.comp)


def scan_index(path, name_filter=None):
    """只读每个 IMG 的头与索引,不碰像素数据。返回 [(entry_name, [frames])]。"""
    out = []
    toc = D.read_npk_toc(path)
    with open(path, "rb") as f:
        for ent in toc:
            f.seek(ent.offset)
            head = f.read(32)
            if head[:15] != b"Neople Img File":
                continue
            index_size, version, count = struct.unpack_from("<qii", head, 16)
            if version != 2:
                continue
            idx = f.read(index_size)
            pos = 0
            frames = []
            for _ in range(count):
                t = struct.unpack_from("<i", idx, pos)[0]
                if t == D.T_LINK:
                    frames.append(D.Frame(D.T_LINK, 0, 0, 0, 0, 0, 0, 0, 0, struct.unpack_from("<i", idx, pos + 4)[0]))
                    pos += 8
                else:
                    comp, w, h, size, x, y, fw, fh = struct.unpack_from("<8i", idx, pos + 4)
                    frames.append(D.Frame(t, comp, w, h, size, x, y, fw, fh, -1))
                    pos += 36
            out.append((ent.name, frames))
    return out


def decode_rgba(buf, frames, offs, i):
    """与 C# 约定一致:1×1 的 zlib 流被截断时,按全透明像素处理。"""
    j = D.resolve_link(frames, i)
    fr = frames[j]
    try:
        fr, raw = D.frame_raw(buf, frames, offs, i)
    except zlib.error:
        if fr.w * fr.h <= 1:
            raw = bytes(fr.w * fr.h * D.BPP[fr.type])
        else:
            raise
    return fr, D.to_rgba(fr, raw)


def main():
    seed = int(sys.argv[1]) if len(sys.argv) > 1 else 20261014
    rng = random.Random(seed)
    npk_dir = os.path.join(require_dnf_dir(), "ImagePacks2")
    names = sorted(n for n in os.listdir(npk_dir) if n.lower().endswith(".npk"))
    t0 = time.time()

    # 水塘抽样:每个类别 PER_CLASS 个,另有一个"全部帧"的水塘
    strata = {}
    seen = {}
    general = []
    general_seen = 0
    GENERAL_POOL = 3 * TOTAL
    for k, n in enumerate(names):
        for ent_name, frames in scan_index(os.path.join(npk_dir, n)):
            for i, fr in enumerate(frames):
                cls = classify(fr)
                rec = (n, ent_name, i)
                seen[cls] = seen.get(cls, 0) + 1
                pool = strata.setdefault(cls, [])
                if len(pool) < PER_CLASS:
                    pool.append(rec)
                else:
                    r = rng.randrange(seen[cls])
                    if r < PER_CLASS:
                        pool[r] = rec
                general_seen += 1
                if len(general) < GENERAL_POOL:
                    general.append(rec)
                else:
                    r = rng.randrange(general_seen)
                    if r < GENERAL_POOL:
                        general[r] = rec
        if (k + 1) % 400 == 0:
            print("扫描索引 %d/%d, %.0fs" % (k + 1, len(names), time.time() - t0), flush=True)
    print("索引扫描完成 %.0fs,类别计数: %s" % (time.time() - t0, dict(sorted(seen.items()))))

    chosen = []
    taken = set()
    for cls in sorted(strata):
        for rec in strata[cls]:
            if rec not in taken:
                taken.add(rec)
                chosen.append(rec)
    rng.shuffle(general)
    for rec in general:
        if len(chosen) >= TOTAL:
            break
        if rec not in taken:
            taken.add(rec)
            chosen.append(rec)

    # 解码并记哈希。按 NPK 与条目分组,避免重复读取
    by_npk = {}
    for rec in chosen:
        by_npk.setdefault(rec[0], {}).setdefault(rec[1], []).append(rec[2])
    results = []
    for n in sorted(by_npk):
        path = os.path.join(npk_dir, n)
        toc = {e.name: e for e in D.read_npk_toc(path)}
        with open(path, "rb") as f:
            for ent_name, idxs in by_npk[n].items():
                e = toc[ent_name]
                f.seek(e.offset)
                buf = f.read(e.size)
                frames, offs, _ = D.parse_img_v2(buf)
                for i in idxs:
                    fr, rgba = decode_rgba(buf, frames, offs, i)
                    results.append({
                        "npk": n, "entry": ent_name, "frame": i, "class": classify(frames[i]),
                        "type": fr.type, "w": fr.w, "h": fr.h, "sha256": hashlib.sha256(rgba).hexdigest(),
                    })
    results.sort(key=lambda r: (r["npk"], r["entry"], r["frame"]))
    out = os.path.join(ensure_out_dir(), "oracle_pixels.json")
    with open(out, "w") as f:
        json.dump({"seed": seed, "count": len(results), "class_counts": seen, "frames": results}, f)
    by_class = {}
    for r in results:
        by_class[r["class"]] = by_class.get(r["class"], 0) + 1
    print("已写入 %s:%d 个帧,按类别 %s,用时 %.0fs" % (out, len(results), dict(sorted(by_class.items())), time.time() - t0))


if __name__ == "__main__":
    main()
