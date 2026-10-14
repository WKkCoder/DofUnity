#!/usr/bin/env python3
"""[normal] 层的光效(GateLight1)与 NPC 谁在上面(证据 E-030,非 REA)。

对每个 NPC 的每个不透明像素,若 GateLight1 在该处 alpha > 0,比较:
  (a) 光在 NPC 下面:截图像素 == NPC 像素
  (b) 光在 NPC 上面:截图像素 == round(NPC 像素 + L.rgb × L.a × 157 / 65025)(加法)
统计只满足 (a) 的、只满足 (b) 的像素个数。被主角或界面盖住的像素两者都不满足,不影响判断。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import town_probe as T  # noqa: E402
import ani_spec  # noqa: E402
from PIL import Image  # noqa: E402

shot_path = sys.argv[1]
A = int(sys.argv[2]) if len(sys.argv) > 2 else 157
sc = T.Scene(shot_path)
shot = Image.open(shot_path).convert("RGB")
spx = shot.load()
lfr, lrgba = sc.frame_rgba("sprite/map/gatelight1.img", 0)
LX, LY = 73, -1

npcs = sc.map_npcs("map/elvengard/(r)gate.map")
for npc_id, wx, wy, wz in npcs:
    ani_path = sc.npc_ani(npc_id)
    if ani_path is None:
        continue
    a = ani_spec.parse(sc.pvf.get(ani_path))
    best = None
    for k, f in enumerate(a["frames"]):
        if f["img"] < 0:
            continue
        fr, rgba = sc.frame_rgba("sprite/" + a["paths"][f["img"]].lower(), f["frame"])
        if fr is None:
            continue
        px = wx - 24 + f["x"] + fr.x
        py = wy - wz + 200 + f["y"] + fr.y
        tot, ok = sc.exact_count(rgba, fr, px, py)
        if best is None or ok > best[0]:
            best = (ok, k, px, py, fr, rgba)
    if best is None:
        continue
    ok, k, px, py, fr, rgba = best
    only_a = only_b = both = neither = 0
    for y in range(fr.h):
        for x in range(fr.w):
            o = (y * fr.w + x) * 4
            if rgba[o + 3] != 255:
                continue
            sx, sy = px + x, py + y
            if not (0 <= sx < 800 and 0 <= sy < 600):
                continue
            lx, ly = sx - LX, sy - LY
            if not (0 <= lx < lfr.w and 0 <= ly < lfr.h):
                continue
            lo = (ly * lfr.w + lx) * 4
            la = lrgba[lo + 3]
            if la == 0:
                continue
            P = (rgba[o], rgba[o + 1], rgba[o + 2])
            S = spx[sx, sy]
            va = P
            vb = tuple(min(255, int(P[c] + lrgba[lo + c] * la * A / 65025.0 + 0.5)) for c in range(3))
            ma = S == va
            mb = S == vb
            if ma and mb:
                both += 1
            elif ma:
                only_a += 1
            elif mb:
                only_b += 1
            else:
                neither += 1
    print("NPC %-4d 帧 %-3d 重叠像素(光 alpha>0):只满足(a)光在下 %d,只满足(b)光在上 %d,两者都满足 %d,都不满足 %d" % (npc_id, k, only_a, only_b, both, neither))
