#!/usr/bin/env python3
"""光效层混合实验(证据 E-030):

背景 GateBack 在 (-24,0) 精确吻合(由 `dnf fit-map` 得到)。这里在背景之上的区域(没有 HUD、文字、NPC 遮挡的那一块)里,
拿 截图像素 S、背景像素 B、光效图像素 L(RGBA)与一组候选混合公式比较,看哪个公式能解释 S。

非 REA(普通的 Python 测量)。只读原版数据与本地截图,不写入原版文件。
"""
import argparse
import sys
import os

sys.path.insert(0, os.path.dirname(__file__))
import town_probe as T  # noqa: E402
from PIL import Image  # noqa: E402


def load_sprite(sc, name, frame=0):
    fr, rgba = sc.frame_rgba(name, frame)
    return fr, rgba


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shot", required=True)
    ap.add_argument("--back-x", type=int, default=-24)
    ap.add_argument("--back-y", type=int, default=0)
    ap.add_argument("--light", default="sprite/map/gatelight1.img")
    ap.add_argument("--light-x", type=int, default=73)
    ap.add_argument("--light-y", type=int, default=-1)
    ap.add_argument("--region", default="340,30,600,200", help="x0,y0,x1,y1(含 x0,y0,不含 x1,y1)")
    args = ap.parse_args()

    sc = T.Scene(args.shot)
    shot = Image.open(args.shot).convert("RGB")
    spx = shot.load()
    bfr, brgba = load_sprite(sc, "sprite/map/gateback.img")
    lfr, lrgba = load_sprite(sc, args.light)
    x0, y0, x1, y1 = [int(v) for v in args.region.split(",")]

    # 光图的 alpha 与颜色统计
    hist = {}
    for i in range(0, len(lrgba), 4):
        a = lrgba[i + 3]
        hist[a >> 5] = hist.get(a >> 5, 0) + 1
    print("光图 alpha 分布(按 32 一档):", sorted(hist.items()))

    rows = []
    for y in range(y0, y1):
        for x in range(x0, x1):
            bx, by = x - args.back_x, y - args.back_y
            lx, ly = x - args.light_x, y - args.light_y
            if not (0 <= bx < bfr.w and 0 <= by < bfr.h):
                continue
            bo = (by * bfr.w + bx) * 4
            if brgba[bo + 3] != 255:
                continue
            B = (brgba[bo], brgba[bo + 1], brgba[bo + 2])
            S = spx[x, y]
            if 0 <= lx < lfr.w and 0 <= ly < lfr.h:
                lo = (ly * lfr.w + lx) * 4
                L = (lrgba[lo], lrgba[lo + 1], lrgba[lo + 2], lrgba[lo + 3])
            else:
                L = (0, 0, 0, 0)
            rows.append((B, S, L))
    print("区域像素数", len(rows))

    # 1) 没有光(L.a == 0)的像素:S 应当等于 B
    nolight = [(B, S) for (B, S, L) in rows if L[3] == 0]
    same = sum(1 for B, S in nolight if tuple(S) == B)
    print("光图 alpha=0 的像素 %d 个,其中 S==B 的 %d 个 (%.1f%%)" % (len(nolight), same, 100.0 * same / max(1, len(nolight))))

    lit = [(B, S, L) for (B, S, L) in rows if L[3] > 0]
    print("光图 alpha>0 的像素", len(lit))

    # 2) 对每个候选公式,扫一个强度 k,看平均绝对误差
    def clamp(v):
        return 255 if v > 255 else (0 if v < 0 else int(v + 0.5))

    models = {}

    def add(name, fn):
        models[name] = fn

    # k 是未知的整体强度(0..1),由 ANI 帧的染色 alpha 与帧间插值决定
    add("加法 B+L.rgb*La*k", lambda B, L, k: tuple(clamp(B[c] + L[c] * (L[3] / 255.0) * k) for c in range(3)))
    add("加法(忽略L.a) B+L.rgb*k", lambda B, L, k: tuple(clamp(B[c] + L[c] * k) for c in range(3)))
    add("alpha混合 B+(L-B)*La*k", lambda B, L, k: tuple(clamp(B[c] + (L[c] - B[c]) * (L[3] / 255.0) * k) for c in range(3)))
    add("滤色 screen(B, L*La*k)", lambda B, L, k: tuple(clamp(255 - (255 - B[c]) * (255 - L[c] * (L[3] / 255.0) * k) / 255.0) for c in range(3)))
    add("加法(预乘) B+L.rgb*k (L已含alpha)", lambda B, L, k: tuple(clamp(B[c] + L[c] * k) for c in range(3)))

    sample = lit[:: max(1, len(lit) // 6000)]
    best = []
    for name, fn in models.items():
        res = []
        for k100 in range(0, 101, 5):
            k = k100 / 100.0
            err = 0
            for B, S, L in sample:
                p = fn(B, L, k)
                err += abs(p[0] - S[0]) + abs(p[1] - S[1]) + abs(p[2] - S[2])
            res.append((err / (3.0 * len(sample)), k))
        res.sort()
        best.append((res[0][0], name, res[0][1]))
        print("%-34s 最佳 k=%.2f 平均绝对误差 %.2f" % (name, res[0][1], res[0][0]))

    # 3) 取最佳模型,细扫 k(步长 0.01),并看残差分布
    best.sort()
    _, bname, bk = best[0]
    fn = models[bname]
    fine = []
    for k1000 in range(max(0, int(bk * 1000) - 60), min(1500, int(bk * 1000) + 60), 5):
        k = k1000 / 1000.0
        err = 0
        within2 = 0
        for B, S, L in sample:
            p = fn(B, L, k)
            d = max(abs(p[0] - S[0]), abs(p[1] - S[1]), abs(p[2] - S[2]))
            err += d
            if d <= 2:
                within2 += 1
        fine.append((err / len(sample), k, within2))
    fine.sort()
    print("最佳模型 %s:细扫最佳 k=%.3f(平均最大通道误差 %.2f,误差<=2 的占 %.1f%%)" % (bname, fine[0][1], fine[0][0], 100.0 * fine[0][2] / len(sample)))


if __name__ == "__main__":
    main()
