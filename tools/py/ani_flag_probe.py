"""检验"帧属性 23 = 关键帧标志(SET FLAG),负载 i32 是标志编号"这一推断(非 REA 的只读测量)。

方法:对 AT 魔法师(job 8)每个技能状态脚本,取脚本里引用的 CUSTOM_ANI_* 常量,经 .chr 的 [etc motion] 表找到动画,
收集这些动画里属性 23 的负载值;再取脚本里 onKeyFrameFlag_* 函数中 `flagIndex == N` 比较的 N。
若推断成立,脚本里比较的 N 应当(几乎)总是出现在对应动画的属性 23 取值里。只输出数字与名字,不输出脚本内容。
用法: python3 tools/py/ani_flag_probe.py [--job 8]
"""
import argparse
import collections
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ani_spec as A
import dnfdata as D
from dnfpaths import REPO_ROOT


def script_texts(root):
    for dp, _, fn in os.walk(root):
        for f in fn:
            if f.lower().endswith(".nut"):
                p = os.path.join(dp, f)
                yield p, open(p, "rb").read().decode("latin-1")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--nut-dir", default=os.path.join(REPO_ROOT, "re", "notes", "nut", "sqr", "character", "atmage"))
    ap.add_argument("--chr", default="character/mage/atmage.chr")
    args = ap.parse_args()
    pvf = D.Pvf()

    # 全库统计:属性 id、负载长度、盒子类型
    prop_count = collections.Counter()
    prop_len = collections.defaultdict(collections.Counter)
    flag_values = collections.Counter()
    skipped = 0
    for e in pvf.entries:
        if not e.path.endswith(".ani"):
            continue
        try:
            ani = A.parse(pvf.read(e))
        except A.AniError:
            skipped += 1
            continue
        for fr in ani["frames"]:
            for pid, raw in fr["props"]:
                prop_count[pid] += 1
                prop_len[pid][len(raw)] += 1
                if pid == 23 and len(raw) == 4:
                    flag_values[struct.unpack("<i", raw)[0]] += 1
    print("无法按二进制解析而跳过的 .ani:", skipped)
    print("帧属性 id -> 出现次数(负载长度分布):")
    for pid in sorted(prop_count):
        print("  %3d  %9d  %s" % (pid, prop_count[pid], dict(prop_len[pid])))
    print("属性 23 的取值(前 12):", flag_values.most_common(12))

    # 读 .chr 的 [etc motion]
    import dnfscripts_lite as L
    etc = L.chr_list(pvf, args.chr, "[etc motion]")
    base = os.path.dirname(args.chr)
    # 头文件常量 CUSTOM_ANI_*
    consts = {}
    for p, t in script_texts(args.nut_dir):
        for m in re.finditer(r"(CUSTOM_ANI_[A-Z0-9_]+)\s*<-\s*(\d+)", t):
            consts[m.group(1)] = int(m.group(2))
    ani_flags = {}

    def flags_of(idx):
        if idx in ani_flags:
            return ani_flags[idx]
        raw = pvf.get((base + "/" + etc[idx]).lower().replace("\\", "/"))
        s = set()
        if raw is not None:
            for fr in A.parse(raw)["frames"]:
                for pid, pl in fr["props"]:
                    if pid == 23 and len(pl) == 4:
                        s.add(struct.unpack("<i", pl)[0])
        ani_flags[idx] = s
        return s

    total = hit = 0
    rows = []
    for p, t in script_texts(args.nut_dir):
        # 函数体里的 flagIndex == N
        for m in re.finditer(r"function\s+(onKeyFrameFlag_\w+)\s*\([^)]*\)\s*\{", t):
            start = m.end()
            depth = 1
            i = start
            while i < len(t) and depth:
                depth += (t[i] == "{") - (t[i] == "}")
                i += 1
            body = t[start:i]
            used = set(int(x) for x in re.findall(r"flagIndex\s*==\s*(\d+)", body))
            if not used:
                continue
            anis = set()
            for c in re.findall(r"CUSTOM_ANI_[A-Z0-9_]+", t):
                if c in consts and consts[c] < len(etc):
                    anis |= flags_of(consts[c])
            suffix = m.group(1)[len("onKeyFrameFlag_"):]
            fixed = {"Attack": "[attack motion]", "JumpAttack": "[jumpattack motion]", "ATMageDashAttack": "[dashattack motion]"}
            if suffix in fixed:
                for rel in L.chr_list(pvf, args.chr, fixed[suffix]):
                    raw = pvf.get((base + "/" + rel).lower().replace("\\", "/"))
                    if raw is not None:
                        for fr in A.parse(raw)["frames"]:
                            for pid, pl in fr["props"]:
                                if pid == 23 and len(pl) == 4:
                                    anis.add(struct.unpack("<i", pl)[0])
            if suffix.startswith("po_") or not anis:
                rows.append((m.group(1), sorted(used), sorted(anis), None))
                continue
            ok = used <= anis
            total += 1
            hit += ok
            rows.append((m.group(1), sorted(used), sorted(anis), ok))
    print("状态脚本里 onKeyFrameFlag 比较的编号是否包含于其动画的属性 23 取值:%d / %d(可检验的状态脚本;被动对象 po_* 的动画不在 .chr 里,未检验)" % (hit, total))
    for r in rows:
        if r[3] is False:
            print("  不包含:", r[0], "脚本", r[1], "动画", r[2])
        elif r[3] is None:
            print("  未检验:", r[0], "脚本", r[1])


if __name__ == "__main__":
    main()
