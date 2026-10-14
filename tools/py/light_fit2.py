#!/usr/bin/env python3
"""城镇底部三层光效的强度回归(证据 E-030,非 REA)。

画面顺序(由 fit-map 的结果与 [animation] 的图层名推断):
  GateBack → GateOutLight(加法)→ GateLight1(加法)→ 地面上的 NPC/主角 → GatePillar(不透明部分盖住前面所有)→ GatePillarLight(加法)
所以:
  GatePillar 不透明的像素:  S = P + k_pl * PL
  其余像素:               S = B + k_out * OL + k_l1 * L1 + k_pl * PL
其中 X = X.rgb * X.a / 255。用最小二乘解 (k_out, k_l1, k_pl)。
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import town_probe as T  # noqa: E402
from PIL import Image  # noqa: E402


def solve(rows, n):
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
    ap.add_argument("--region", default="339,458,520,525")
    ap.add_argument("--l1", default="73,-1")
    ap.add_argument("--ol", default="336,410")
    ap.add_argument("--pl", default="306,423")
    ap.add_argument("--pillar", default="339,452")
    ap.add_argument("--dump", default=None, help="把区域的 残差 画成 PNG")
    args = ap.parse_args()

    sc = T.Scene(args.shot)
    shot = Image.open(args.shot).convert("RGB")
    spx = shot.load()
    bfr, brgba = sc.frame_rgba("sprite/map/gateback.img", 0)
    ofr, orgba = sc.frame_rgba("sprite/map/gateoutlight.img", 0)
    l1fr, l1rgba = sc.frame_rgba("sprite/map/gatelight1.img", 0)
    pfr, prgba = sc.frame_rgba("sprite/map/gatepillar.img", 0)
    plfr, plrgba = sc.frame_rgba("sprite/map/gatepillarlight.img", 0)
    l1x, l1y = [int(v) for v in args.l1.split(",")]
    olx, oly = [int(v) for v in args.ol.split(",")]
    plx, ply = [int(v) for v in args.pl.split(",")]
    px0, py0 = [int(v) for v in args.pillar.split(",")]
    x0, y0, x1, y1 = [int(v) for v in args.region.split(",")]

    def px(fr, rgba, x, y):
        if 0 <= x < fr.w and 0 <= y < fr.h:
            o = (y * fr.w + x) * 4
            a = rgba[o + 3] / 255.0
            return (rgba[o] * a, rgba[o + 1] * a, rgba[o + 2] * a), rgba[o + 3]
        return (0.0, 0.0, 0.0), 0

    rows = []
    pix = []
    for y in range(y0, y1):
        for x in range(x0, x1):
            S = spx[x, y]
            P, pa = px(pfr, prgba, x - px0, y - py0)
            PL, _ = px(plfr, plrgba, x - plx, y - ply)
            if pa == 255:
                base = (prgba[((y - py0) * pfr.w + (x - px0)) * 4], prgba[((y - py0) * pfr.w + (x - px0)) * 4 + 1], prgba[((y - py0) * pfr.w + (x - px0)) * 4 + 2])
                feats = [(0, 0, 0), (0, 0, 0), PL]
            elif pa != 0:
                continue  # 部分透明的 [close] 像素,混合顺序未知,跳过
            else:
                bo = ((y + 0) * bfr.w + (x + 24)) * 4
                base = (brgba[bo], brgba[bo + 1], brgba[bo + 2])
                OL, _ = px(ofr, orgba, x - olx, y - oly)
                L1, _ = px(l1fr, l1rgba, x - l1x, y - l1y)
                feats = [OL, L1, PL]
            pix.append((x, y, base, S, feats))
    print("像素", len(pix), "其中 [close] 不透明", sum(1 for p in pix if p[4][0] == (0, 0, 0) and p[4][1] == (0, 0, 0)))
    for c in range(3):
        for x, y, base, S, feats in pix:
            if S[c] >= 250 or base[c] >= 250:
                continue
            rows.append((S[c] - base[c], [feats[i][c] for i in range(3)]))
    ks = solve(rows, 3)
    if ks is None:
        print("奇异")
        return
    print("k_out = %.4f (×255=%.1f)  k_l1 = %.4f (×255=%.1f)  k_pl = %.4f (×255=%.1f)" % (ks[0], ks[0] * 255, ks[1], ks[1] * 255, ks[2], ks[2] * 255))

    def clamp(v):
        return 255 if v > 255 else (0 if v < 0 else int(v + 0.5))

    tot = within = 0
    absum = 0
    split = {"pillar": [0, 0, 0], "ground": [0, 0, 0]}
    for x, y, base, S, feats in pix:
        key = "pillar" if feats[0] == (0, 0, 0) and feats[1] == (0, 0, 0) else "ground"
        for c in range(3):
            p = clamp(base[c] + sum(ks[i] * feats[i][c] for i in range(3)))
            d = abs(p - S[c])
            absum += d
            tot += 1
            split[key][0] += 1
            split[key][1] += d
            if d <= 2:
                within += 1
                split[key][2] += 1
    print("总体:平均绝对误差 %.2f,误差<=2 的占 %.1f%%" % (absum / max(1, tot), 100.0 * within / max(1, tot)))
    for k, (n, s, w) in split.items():
        if n:
            print("  %s: %d 个通道值,平均绝对误差 %.2f,误差<=2 的 %.1f%%" % (k, n, s / n, 100.0 * w / n))


if __name__ == "__main__":
    main()
