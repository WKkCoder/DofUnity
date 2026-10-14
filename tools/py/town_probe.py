"""城镇场景的"位置模型"校验:地图里的 NPC 世界坐标 + 动画偏移,能不能预测它们在原版截图里的位置?

链条(全部来自用户自己的数据与一张原版客户端截图,不运行游戏):
  地图脚本的 [NPC] 列表(每个 NPC:编号、x、y、z)
    → npc/npc.lst 找到 .npc 脚本 → [field animation] 指向的 .ani(路径相对 npc/)
    → .ani 的第 k 帧:图片表里的 IMG + 帧号、帧偏移 (ax, ay)
    → IMG 帧头:像素在画布里的偏移 (hx, hy)
  预测的屏幕左上角 = (世界x − 相机x + ax + hx,  世界y − 世界z + 纵向常数 + ay + hy)
然后在预测位置周围 ±R 像素的网格里,数"不透明像素与截图完全相同"的个数,看峰值是否正好落在预测位置、是否远高于邻居。

只输出数字;--sheet 会把"帧 vs 截图裁剪"的并排图写到 re/notes/town/(含原版像素,只放 re/,不入库)。

用法:
  python3 tools/py/town_probe.py --shot <截图.png> [--map 'map/elvengard/(r)gate.map'] [--cam-x 24] [--y0 200] [--radius 3] [--sheet]
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ani_spec
import dnfdata as D
import screen_match_check as M
import script_dump as S
from dnfpaths import REPO_ROOT, require_dnf_dir


class Scene:
    def __init__(self, shot_path):
        self.pvf = D.Pvf(os.path.join(require_dnf_dir(), "Script.pvf"))
        self.st = S.Strings(self.pvf)
        self.sw, self.sh, self.bpp, self.rows = M.load_png(shot_path)
        self.npk_dir = os.path.join(require_dnf_dir(), "ImagePacks2")
        self._idx = None
        lst = S.to_text(self.pvf.get("npc/npc.lst"), self.st)
        self.npc_file = {int(i): n for i, n in re.findall(r'(\d+) "([^"]+)"', lst)}

    # ---- NPK 索引:IMG 名字(小写)-> [(npk 文件名, 条目)];重名取最后一个(覆盖顺序未研究,见 formats.md)
    @property
    def idx(self):
        if self._idx is None:
            self._idx = {}
            for n in sorted(os.listdir(self.npk_dir)):
                if n.lower().endswith(".npk"):
                    for e in D.read_npk_toc(os.path.join(self.npk_dir, n)):
                        self._idx.setdefault(e.name.lower(), []).append((n, e))
        return self._idx

    def frame_rgba(self, img_name, frame):
        ents = self.idx.get(img_name.lower())
        if not ents:
            return None, None
        n, e = ents[-1]
        with open(os.path.join(self.npk_dir, n), "rb") as f:
            f.seek(e.offset)
            buf = f.read(e.size)
        frames, offs, _ = D.parse_img_v2(buf)
        j = D.resolve_link(frames, frame)
        fr, raw = D.frame_raw(buf, frames, offs, frame)
        return fr, D.to_rgba(fr, raw)

    def npc_ani(self, npc_id):
        raw = self.pvf.get("npc/" + self.npc_file[npc_id].lower())
        for tag, vals in S.decode_text(raw, self.st):
            if tag and tag.lower() == "[field animation]":
                return "npc/" + vals[0].strip('"').lower()
        return None

    def map_npcs(self, map_path):
        raw = self.pvf.get(map_path)
        if raw is None:
            raise SystemExit("PVF 里没有 " + map_path)
        out = []
        for tag, vals in S.decode_text(raw, self.st):
            if tag == "[NPC]":
                nums = [int(v) for v in vals if re.fullmatch(r"-?\d+", v)]
                for i in range(0, len(nums) - 3, 4):
                    out.append(tuple(nums[i:i + 4]))
        return out

    def exact_count(self, rgba, fr, x0, y0):
        total = ok = 0
        for y in range(fr.h):
            sy = y0 + y
            if not 0 <= sy < self.sh:
                continue
            row = self.rows[sy]
            for x in range(fr.w):
                sx = x0 + x
                if not 0 <= sx < self.sw:
                    continue
                o = (y * fr.w + x) * 4
                if rgba[o + 3] != 255:
                    continue
                total += 1
                so = sx * self.bpp
                if rgba[o] == row[so] and rgba[o + 1] == row[so + 1] and rgba[o + 2] == row[so + 2]:
                    ok += 1
        return total, ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shot", required=True)
    ap.add_argument("--map", default="map/elvengard/(r)gate.map")
    ap.add_argument("--cam-x", type=int, default=24)
    ap.add_argument("--y0", type=int, default=200)
    ap.add_argument("--radius", type=int, default=3)
    ap.add_argument("--sheet", action="store_true")
    args = ap.parse_args()

    sc = Scene(args.shot)
    npcs = sc.map_npcs(args.map)
    print("地图 %s 的 [NPC]:%d 个" % (args.map, len(npcs)))
    R = args.radius
    out_dir = os.path.join(REPO_ROOT, "re", "notes", "town")
    if args.sheet:
        os.makedirs(out_dir, exist_ok=True)
        from PIL import Image
        shot_img = Image.open(args.shot).convert("RGB")

    print("%-5s %-22s %-6s %-8s %-9s %-7s %-7s %s" % ("NPC", "脚本", "ani帧", "峰值偏移", "峰值精确", "邻居最大", "不透明", "预测左上角"))
    for npc_id, wx, wy, wz in npcs:
        ani_path = sc.npc_ani(npc_id)
        if ani_path is None:
            print("%-5d %-22s 没有 [field animation]" % (npc_id, sc.npc_file.get(npc_id)))
            continue
        a = ani_spec.parse(sc.pvf.get(ani_path))
        best = None
        for k, f in enumerate(a["frames"]):
            if f["img"] < 0:
                continue
            fr, rgba = sc.frame_rgba("sprite/" + a["paths"][f["img"]].lower(), f["frame"])
            if fr is None:
                continue
            px = wx - args.cam_x + f["x"] + fr.x
            py = wy - wz + args.y0 + f["y"] + fr.y
            grid = {}
            for dy in range(-R, R + 1):
                for dx in range(-R, R + 1):
                    grid[(dx, dy)] = sc.exact_count(rgba, fr, px + dx, py + dy)
            (pdx, pdy), (tot, ok) = max(grid.items(), key=lambda kv: kv[1][1])
            neighbor = max(v[1] for key, v in grid.items() if key != (pdx, pdy))
            if best is None or ok > best[0]:
                best = (ok, k, (pdx, pdy), neighbor, tot, (px, py), fr, rgba, f)
        if best is None:
            print("%-5d %-22s 没有可画的帧" % (npc_id, sc.npc_file.get(npc_id)))
            continue
        ok, k, (pdx, pdy), neighbor, tot, (px, py), fr, rgba, f = best
        print("%-5d %-22s %-6d (%+d,%+d)   %-9d %-7d %-7d (%d,%d)" % (
            npc_id, sc.npc_file.get(npc_id), k, pdx, pdy, ok, neighbor, tot, px, py))
        if args.sheet:
            im = Image.frombytes("RGBA", (fr.w, fr.h), rgba)
            bg = Image.new("RGBA", (fr.w, fr.h), (255, 0, 255, 255))
            bg.alpha_composite(im)
            crop = shot_img.crop((px, py, px + fr.w, py + fr.h))
            sheet = Image.new("RGB", (fr.w * 2 + 4, fr.h), (40, 40, 40))
            sheet.paste(bg.convert("RGB"), (0, 0))
            sheet.paste(crop, (fr.w + 4, 0))
            sheet.resize((sheet.width * 2, sheet.height * 2), Image.NEAREST).save(os.path.join(out_dir, "npc_%d.png" % npc_id))


if __name__ == "__main__":
    main()
