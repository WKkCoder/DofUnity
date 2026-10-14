"""全量扫描 ImagePacks2 下所有 NPK 里的 IMG,核对 IMG v2 的格式假设。

检查项(每个 IMG):
  1. 魔数与版本:v2 / 旧版 "Neople Image File" / 其他
  2. 索引表按 8 字节(链接帧)与 36 字节(普通帧)解析后,长度恰好等于头里的 indexSize
  3. 所有帧数据长度之和 + 索引区 == IMG 条目大小(帧数据之间没有空隙)
  4. 每个普通帧:类型在已知集合内;compress 在 {5,6};解压后长度 == w*h*bpp
  5. 每个链接帧:目标在范围内,且 16 跳内能落到普通帧
只统计,不输出任何素材。结果写入 out/img_scan.json。
"""
import json
import os
import sys
import time
import zlib
from collections import Counter
from concurrent.futures import ProcessPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D
from dnfpaths import OUT_DIR as OUT, require_dnf_dir

MAX_ANOMALIES = 25


def scan_npk(path):
    c = Counter()
    anomalies = []
    sizes = {"comp_bytes": 0, "raw_bytes": 0}

    def bad(kind, detail):
        c["anomaly:" + kind] += 1
        if len(anomalies) < MAX_ANOMALIES:
            anomalies.append((os.path.basename(path), kind, detail))

    try:
        toc = D.read_npk_toc(path)
    except Exception as e:  # noqa: BLE001
        return os.path.basename(path), {"anomaly:toc": 1}, [(os.path.basename(path), "toc", repr(e))], sizes

    with open(path, "rb") as f:
        for ent in toc:
            f.seek(ent.offset)
            buf = f.read(ent.size)
            c["entries"] += 1
            if len(buf) != ent.size:
                bad("short-read", ent.name)
                continue
            if buf[:15] == b"Neople Image Fi":  # 旧版 "Neople Image File\0"
                c["legacy_img"] += 1
                continue
            if buf[:15] != b"Neople Img File":
                c["unknown_magic"] += 1
                bad("magic", "%s %r" % (ent.name, buf[:16]))
                continue
            try:
                frames, offs, total = D.parse_img_v2(buf)
            except D.ImgError as e:
                bad("parse", "%s %s" % (ent.name, e))
                continue
            c["img_v2"] += 1
            if total != len(buf):
                bad("data-size", "%s expected %d actual %d" % (ent.name, total, len(buf)))
            c["frames"] += len(frames)
            if len(frames) == 0:
                c["img_with_0_frames"] += 1
            for i, fr in enumerate(frames):
                if fr.type == D.T_LINK:
                    c["frame_link"] += 1
                    try:
                        D.resolve_link(frames, i)
                    except D.ImgError:
                        bad("link", "%s frame %d -> %d" % (ent.name, i, fr.link))
                    continue
                c["frame_type_0x%02X" % fr.type] += 1
                c["frame_comp_%d" % fr.comp] += 1
                c["type_0x%02X_comp_%d" % (fr.type, fr.comp)] += 1
                bpp = D.BPP.get(fr.type)
                if bpp is None:
                    bad("frame-type", "%s frame %d type 0x%X" % (ent.name, i, fr.type))
                    continue
                if fr.w < 0 or fr.h < 0:
                    bad("negative-size", "%s frame %d" % (ent.name, i))
                    continue
                want = fr.w * fr.h * bpp
                if fr.w == 0 or fr.h == 0:
                    c["frame_empty"] += 1
                data = buf[offs[i]: offs[i] + D.stored_len(fr)]
                if fr.comp == 6:
                    try:
                        raw = zlib.decompress(data)
                    except zlib.error as e:
                        bad("zlib", "%s frame %d %s" % (ent.name, i, e))
                        continue
                elif fr.comp == 5:
                    raw = data
                else:
                    bad("compress", "%s frame %d comp %d" % (ent.name, i, fr.comp))
                    continue
                sizes["comp_bytes"] += len(data)
                sizes["raw_bytes"] += len(raw)
                if len(raw) != want:
                    bad("raw-len", "%s frame %d got %d want %d (%dx%d bpp%d)" % (ent.name, i, len(raw), want, fr.w, fr.h, bpp))
                else:
                    c["frame_ok"] += 1
    return os.path.basename(path), dict(c), anomalies, sizes


def main():
    npk_dir = D.NPK_DIR or os.path.join(require_dnf_dir(), "ImagePacks2")
    names = sorted(n for n in os.listdir(npk_dir) if n.lower().endswith(".npk"))
    paths = [os.path.join(npk_dir, n) for n in names]
    # 大文件先跑,避免尾部拖尾
    paths.sort(key=lambda p: -os.path.getsize(p))
    total = Counter()
    anomalies = []
    sizes = Counter()
    t0 = time.time()
    done = 0
    with ProcessPoolExecutor(max_workers=max(2, (os.cpu_count() or 4) - 2)) as ex:
        futs = [ex.submit(scan_npk, p) for p in paths]
        for fu in as_completed(futs):
            name, c, an, sz = fu.result()
            total.update(c)
            sizes.update(sz)
            for a in an:
                if len(anomalies) < 200:
                    anomalies.append(a)
            done += 1
            if done % 50 == 0 or done == len(paths):
                print("%d/%d NPK, %.0fs, frames so far %d, anomalies %d" % (
                    done, len(paths), time.time() - t0, total["frames"],
                    sum(v for k, v in total.items() if k.startswith("anomaly:"))), flush=True)
    result = {"npk_files": len(paths), "seconds": round(time.time() - t0, 1),
              "counters": dict(sorted(total.items())), "sizes": dict(sizes), "anomalies": anomalies}
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "img_scan.json"), "w") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    print(json.dumps(result["counters"], indent=1))
    print("sizes", result["sizes"])
    print("DONE", result["seconds"], "s")


if __name__ == "__main__":
    main()
