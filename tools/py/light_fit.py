#!/usr/bin/env python3
"""多层加法光效的强度回归(证据 E-030,非 REA):

模型:  S = min(255, B + Σ_i  k_i * L_i.rgb * L_i.a / 255 )
B 是不被别的东西盖住的背景像素(GateBack 在 (-24,0)),L_i 是各光效层在预测位置上的像素,k_i 是该层此刻的强度(0..1)。
对每个区域用最小二乘解出 k_i,并给出残差。区域要避开 HUD、文字、NPC、主角与 [close] 层的不透明像素。
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import town_probe as T  # noqa: E402
from PIL import Image  # noqa: E402


def solve(rows, n):
    """rows: [(target, [f1..fn])] 最小二乘解 n 个系数(法方程,高斯消元)。"""
    a = [[0.0] * (n + 1) for _ in range(n)]
    for t, f in rows:
        for i in range(n):
            for j in range(n):
                a[i][j] += f[i] * f[j]
            a[i][n] += f[i] * t
    for i in range(n):
        p = max(range(i, n), key=lambda r: abs(a[r][i]))
        if abs(a[p][i]) < 1e-9:
            return None
        a[i], a[p] = a[p], a[i]
        for r in range(n):
            if r != i:
                m = a[r][i] / a[i][i]
                for c in range(i, n + 1):
                    a[r][c] -= m * a[i][c]
    return [a[i][n] / a[i][i] for i in range(n)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shot", required=True)
    ap.add_argument("--region", required=True, help="x0,y0,x1,y1")
    ap.add_argument("--layer", action="append", required=True, help="名字@x,y(IMG 名,帧 0 的像素块左上角)")
    ap.add_argument("--cover", action="append", default=[], help="名字@x,y:在这些层不透明的地方跳过(画在上面的 [close] 层)")
    ap.add_argument("--back-x", type=int, default=-24)
    ap.add_argument("--back-y", type=int, default=0)
    args = ap.parse_args()

    sc = T.Scene(args.shot)
    shot = Image.open(args.shot).convert("RGB")
    spx = shot.load()
    bfr, brgba = sc.frame_rgba("sprite/map/gateback.img", 0)

    def parse(spec):
        name, pos = spec.split("@")
        x, y = [int(v) for v in pos.split(",")]
        fr, rgba = sc.frame_rgba(name, 0)
        return name, x, y, fr, rgba

    layers = [parse(s) for s in args.layer]
    covers = [parse(s) for s in args.cover]
    x0, y0, x1, y1 = [int(v) for v in args.region.split(",")]
    n = len(layers)

    rows = []  # (通道值差, [各层贡献])
    pix = []
    skipped_cover = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            bx, by = x - args.back_x, y - args.back_y
            if not (0 <= bx < bfr.w and 0 <= by < bfr.h):
                continue
            bo = (by * bfr.w + bx) * 4
            B = (brgba[bo], brgba[bo + 1], brgba[bo + 2])
            covered = False
            for _, cx, cy, cfr, crgba in covers:
                lx, ly = x - cx, y - cy
                if 0 <= lx < cfr.w and 0 <= ly < cfr.h and crgba[(ly * cfr.w + lx) * 4 + 3] != 0:
                    covered = True
                    break
            if covered:
                skipped_cover += 1
                continue
            S = spx[x, y]
            contrib = []
            for _, lxo, lyo, lfr, lrgba in layers:
                lx, ly = x - lxo, y - lyo
                if 0 <= lx < lfr.w and 0 <= ly < lfr.h:
                    o = (ly * lfr.w + lx) * 4
                    a = lrgba[o + 3] / 255.0
                    contrib.append((lrgba[o] * a, lrgba[o + 1] * a, lrgba[o + 2] * a))
                else:
                    contrib.append((0.0, 0.0, 0.0))
            pix.append((B, S, contrib))
    print("区域像素 %d(因 [close] 层覆盖跳过 %d)" % (len(pix), skipped_cover))

    for c in range(3):
        for B, S, contrib in pix:
            if S[c] >= 250 or B[c] >= 250:
                continue
            rows.append((S[c] - B[c], [contrib[i][c] for i in range(n)]))
    ks = solve(rows, n)
    if ks is None:
        print("方程奇异(各层贡献线性相关或没有像素)")
        return
    for (name, x, y, _, _), k in zip(layers, ks):
        print("层 %s@(%d,%d): k = %.4f  (×255 = %.1f)" % (name, x, y, k, k * 255))

    def clamp(v):
        return 255 if v > 255 else (0 if v < 0 else int(v + 0.5))

    tot = 0
    within = 0
    absum = 0
    for B, S, contrib in pix:
        for c in range(3):
            p = clamp(B[c] + sum(ks[i] * contrib[i][c] for i in range(n)))
            d = abs(p - S[c])
            absum += d
            tot += 1
            if d <= 2:
                within += 1
    print("残差:平均绝对误差 %.2f,误差<=2 的占 %.1f%%" % (absum / max(1, tot), 100.0 * within / max(1, tot)))


if __name__ == "__main__":
    main()
