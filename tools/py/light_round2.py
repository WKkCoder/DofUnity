#!/usr/bin/env python3
"""GateLight1 单层区域的整数流水线核对(证据 E-030,非 REA):S = round(B + L.rgb*L.a*A/65025),枚举整数 A。"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import town_probe as T  # noqa: E402
from PIL import Image  # noqa: E402

shot_path = sys.argv[1]
sc = T.Scene(shot_path)
shot = Image.open(shot_path).convert("RGB")
spx = shot.load()
bfr, brgba = sc.frame_rgba("sprite/map/gateback.img", 0)
lfr, lrgba = sc.frame_rgba("sprite/map/gatelight1.img", 0)
LX, LY = 73, -1
pts = []
for y in range(30, 200):
    for x in range(340, 600):
        bo = (y * bfr.w + (x + 24)) * 4
        B = (brgba[bo], brgba[bo + 1], brgba[bo + 2])
        lx, ly = x - LX, y - LY
        lo = (ly * lfr.w + lx) * 4
        L = (lrgba[lo], lrgba[lo + 1], lrgba[lo + 2], lrgba[lo + 3])
        pts.append((B, L, spx[x, y]))
print("像素", len(pts))
for A in range(150, 166):
    exact = within1 = 0
    for B, L, S in pts:
        v = tuple(min(255, int(B[c] + L[c] * L[3] * A / 65025.0 + 0.5)) for c in range(3))
        if v == tuple(S):
            exact += 1
        if all(abs(v[c] - S[c]) <= 1 for c in range(3)):
            within1 += 1
    print("A=%3d  精确一致 %6d/%d (%.2f%%)  ±1 内 %.2f%%" % (A, exact, len(pts), 100.0 * exact / len(pts), 100.0 * within1 / len(pts)))
