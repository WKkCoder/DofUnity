#!/usr/bin/env python3
"""GatePillar 不透明像素上的整数流水线核对(证据 E-030,非 REA):

在这些像素上 S = P + 加法光效 PL,没有别的层参与。枚举 8 位的染色 alpha A(0..255)与几种取整方式,
看哪种能让逐像素精确一致的比例最高。目的:确定加法混合在 8 位整数下的取整规则(是否需要 ±1 的容差)。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import town_probe as T  # noqa: E402
from PIL import Image  # noqa: E402

shot_path = sys.argv[1]
sc = T.Scene(shot_path)
shot = Image.open(shot_path).convert("RGB")
spx = shot.load()
pfr, prgba = sc.frame_rgba("sprite/map/gatepillar.img", 0)
plfr, plrgba = sc.frame_rgba("sprite/map/gatepillarlight.img", 0)
PX, PY = 339, 452
PLX, PLY = 306, 423

pts = []
for y in range(458, 525):
    for x in range(339, 520):
        px, py = x - PX, y - PY
        if not (0 <= px < pfr.w and 0 <= py < pfr.h):
            continue
        o = (py * pfr.w + px) * 4
        if prgba[o + 3] != 255:
            continue
        P = (prgba[o], prgba[o + 1], prgba[o + 2])
        lx, ly = x - PLX, y - PLY
        if 0 <= lx < plfr.w and 0 <= ly < plfr.h:
            lo = (ly * plfr.w + lx) * 4
            L = (plrgba[lo], plrgba[lo + 1], plrgba[lo + 2], plrgba[lo + 3])
        else:
            L = (0, 0, 0, 0)
        pts.append((P, L, spx[x, y]))
print("像素数", len(pts), "其中光图 alpha>0 的", sum(1 for p in pts if p[1][3] > 0))
lit = [p for p in pts if p[1][3] > 0]


def variants(P, L, A):
    out = {}
    la = L[3]
    # V1: 先把 L.a 与 A 相乘取整,再乘颜色取整
    sa = (la * A + 127) // 255
    out["V1 整数两步(先 a 后 rgb)"] = tuple(min(255, P[c] + (L[c] * sa + 127) // 255) for c in range(3))
    # V2: 一步浮点,最后四舍五入
    out["V2 浮点一步四舍五入"] = tuple(min(255, int(P[c] + L[c] * la * A / 65025.0 + 0.5)) for c in range(3))
    # V3: 一步浮点,向下取整
    out["V3 浮点一步向下取整"] = tuple(min(255, int(P[c] + L[c] * la * A / 65025.0)) for c in range(3))
    # V4: 先把颜色乘 A 取整,再乘 a
    ca = tuple((L[c] * A + 127) // 255 for c in range(3))
    out["V4 先 A 后 a(整数)"] = tuple(min(255, P[c] + (ca[c] * la + 127) // 255) for c in range(3))
    # V5: 不取整四舍五入,先加后取整(同 V2)但 alpha 不除 255 而用 256
    out["V5 除 256 近似"] = tuple(min(255, P[c] + ((L[c] * la * A) >> 16)) for c in range(3))
    return out


best = {}
for A in range(100, 181):
    counts = {}
    for P, L, S in lit:
        for name, v in variants(P, L, A).items():
            if v == tuple(S):
                counts[name] = counts.get(name, 0) + 1
    for name, n in counts.items():
        if name not in best or n > best[name][0]:
            best[name] = (n, A)
for name, (n, A) in sorted(best.items(), key=lambda kv: -kv[1][0]):
    print("%-26s 最佳 A=%3d 精确一致 %d/%d (%.1f%%)" % (name, A, n, len(lit), 100.0 * n / len(lit)))

# 允许 ±1
print("--- 允许每通道 ±1 ---")
best1 = {}
for A in range(100, 181):
    counts = {}
    for P, L, S in lit:
        for name, v in variants(P, L, A).items():
            if all(abs(v[c] - S[c]) <= 1 for c in range(3)):
                counts[name] = counts.get(name, 0) + 1
    for name, n in counts.items():
        if name not in best1 or n > best1[name][0]:
            best1[name] = (n, A)
for name, (n, A) in sorted(best1.items(), key=lambda kv: -kv[1][0]):
    print("%-26s 最佳 A=%3d ±1 内 %d/%d (%.1f%%)" % (name, A, n, len(lit), 100.0 * n / len(lit)))
