#!/usr/bin/env python3
"""对 .act 全库的 [MOTION] 做"叠加层与声音"的结构统计与事件流推演(研究用,读本机 PVF)。
这是 C# `ActMotionCompiler` / `ActMotionPlayer`(packages/com.dnfunity.game/Runtime/Act/ActMotion.Events.cs、ActMotionPlayer.cs)的独立转写,
用来与 `dnf act-motion-check --json` 对拍:标记流自己解码(不经 C# 的解析器)、块的配对复用 tools/py/act_tree.py(它本身就是 ActParser 的独立转写)、
.ani 帧数用 tools/py/ani_spec.py 读。只统计结构与个数,不输出脚本里的路径或声音标签;输出文件必须放 out/ 或 re/ 下,不入库。

读法(取自客户端加载函数 importActionScript 0x01333530 的条目形状,整数的含义是本机样本统计出来的推断):
  [SUB ANI]            条目 = 路径 + 2 个整数(起始帧、层序键)
  [SUB ANI WITH XY]    条目 = 路径 + 4 个整数(起始帧、层序键、偏移 x、偏移 y)
  [SUB ANI WITH XYZ]   条目 = 路径 + 5 个整数(起始帧、层序键、偏移 x、y、z)
  [SOUND]              条目 = 声音标签 + 1 个整数(帧号;概率固定 100)
  [SOUND PROBABILITY]  条目 = 声音标签 + 2 个整数(帧号、百分数)
  一条 = 一个非空字符串加规定个数的数值(整数、十六进制、浮点按向零截断);字符串后数值不够的那条丢掉并记一次,块里不在字符串后面的游离值也记一次。
  层的编号(Index)与声音的编号各自按文件顺序数,跨块连续。
事件流(逐帧 f = 上次帧 + 1 到当前帧):先出叠加层开始(没起过、起始帧 ≤ f,按 (层序键, 文件顺序) 升序),再出声音(没响过、帧 == f,按文件顺序);
  帧号比上次小(循环绕圈):声音重装、叠加层不重起;收口:已起的叠加层按同一顺序各出一个结束,帧号是停住的那一帧。

用法:
  python3 tools/py/act_motion_ref.py [--out out/act_motion_ref.json]                 统计并写出全部计数
  python3 tools/py/act_motion_ref.py compare --cs out/xxx.json                      与 `dnf act-motion-check --json` 写出的文件逐项比较(只报个数与键名)
"""
import argparse
import collections
import json
import os
import posixpath
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import dnfdata as D  # noqa: E402
import script_dump as S  # noqa: E402
import ani_spec as A  # noqa: E402
import act_tree as T  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(HERE))
FNV_OFFSET = 14695981039346656037
FNV_PRIME = 1099511628211
MASK = 0xFFFFFFFFFFFFFFFF
LAYER_TAGS = {"[SUB ANI]": ("plain", 2), "[SUB ANI WITH XY]": ("xy", 4), "[SUB ANI WITH XYZ]": ("xyz", 5)}
SOUND_TAGS = {"[SOUND]": (False, 1), "[SOUND PROBABILITY]": (True, 2)}
LAYER_START, LAYER_END, SOUND = 1, 2, 3


# --------------------------------------------------------------------------- 标记流解码(类型:i 整数 h 十六进制 f 浮点 s 字符串 p 括号词 k 种类键 c 分隔符 o 其它)

def decode_typed(raw, strings):
    """返回 [(标签或 None, [(类型, 值)])];与 script_dump.decode_text 同一套切分规则,只是值带类型。"""
    lines = []
    cur = None
    vals = []
    toks = list(struct.iter_unpack("<Bi", raw[2:]))
    have = False
    k = 0
    while k < len(toks):
        ty, v = toks[k]
        if ty == 5:
            if have or vals:
                lines.append((cur, vals))
            cur = strings.get(v)
            have = True
            vals = []
        elif ty == 2:
            vals.append(("i", v))
        elif ty == 3:
            vals.append(("h", v))
        elif ty == 4:
            vals.append(("f", struct.unpack("<f", struct.pack("<i", v))[0]))
        elif ty == 7:
            vals.append(("s", strings.get(v)))
        elif ty == 8:
            vals.append(("c", v))
        elif ty == 9 and k + 1 < len(toks) and toks[k + 1][0] == 10:
            vals.append(("k", strings.get(toks[k + 1][1])))
            k += 1
        elif ty == 6:
            vals.append(("p", strings.get(v)))
        else:
            vals.append(("o", v))
        k += 1
    if have or vals:
        lines.append((cur, vals))
    return lines


def build_roots(lines, pairs):
    """按 act_tree 的配对结果建树;返回顶层节点列表,节点 = {tag, vals, children}。"""
    closer_to_open = {c: o for o, c in pairs.items()}
    roots = []
    stack = []
    for i, (tag, vals) in enumerate(lines):
        if T.is_closer(tag):
            o = closer_to_open.get(i)
            if o is not None:
                for k in range(len(stack) - 1, -1, -1):
                    if stack[k][0] == o:
                        del stack[k:]
                        break
            continue
        node = {"tag": tag, "vals": vals, "children": []}
        (stack[-1][1]["children"] if stack else roots).append(node)
        if i in pairs:
            stack.append((i, node))
    return roots


# --------------------------------------------------------------------------- 条目读取

def int_of(kv):
    kind, v = kv
    if kind == "f":
        return int(v)       # 向零截断
    return v


def read_entries(vals, need):
    """一个块里的值 → ([(文本, [整数...])], 丢掉的个数)。"""
    out = []
    bad = 0
    i = 0
    while i < len(vals):
        kind, v = vals[i]
        if kind != "s" or v == "":
            bad += 1
            i += 1
            continue
        j = i + 1
        got = 0
        while got < need and j + got < len(vals) and vals[j + got][0] in ("i", "h", "f"):
            got += 1
        i = j + got
        if got < need:
            bad += 1
            continue
        out.append((v, [int_of(vals[j + k]) for k in range(need)]))
    return out, bad


def read_motion(motion):
    """第一个 [MOTION] 节点 → dict(base, layers, sounds, malformed)。layers: [(form, 起始帧, 层序键)];sounds: [(标签, 帧, 百分数, 是否概率)]。"""
    base = None
    layers = []
    sounds = []
    bad = 0
    for c in motion["children"]:
        tag = c["tag"]
        if tag == "[BASE ANI]" and base is None:
            vals = c["vals"]
            base = vals[0][1] if vals and vals[0][0] in ("s", "p", "k") else None
        if tag in LAYER_TAGS:
            form, need = LAYER_TAGS[tag]
            ents, b = read_entries(c["vals"], need)
            bad += b
            for _, nums in ents:
                layers.append((form, nums[0], nums[1]))
        elif tag in SOUND_TAGS:
            prob, need = SOUND_TAGS[tag]
            ents, b = read_entries(c["vals"], need)
            bad += b
            for key, nums in ents:
                sounds.append((key, nums[0], nums[1] if prob else 100, prob))
    return {"base": base, "layers": layers, "sounds": sounds, "malformed": bad}


# --------------------------------------------------------------------------- 事件播放器

class Player:
    def __init__(self, layers, sounds):
        self.layers = layers
        self.sounds = sounds
        self.order = sorted(range(len(layers)), key=lambda i: (layers[i][2], i))
        self.begin()

    def begin(self):
        self.started = [False] * len(self.layers)
        self.fired = [False] * len(self.sounds)
        self.last = -1
        self.ended = False

    def advance(self, frame, out):
        if self.ended or frame < 0:
            return
        if frame < self.last:
            self.fired = [False] * len(self.sounds)
            self.last = -1
        for f in range(self.last + 1, frame + 1):
            for i in self.order:
                if not self.started[i] and self.layers[i][1] <= f:
                    self.started[i] = True
                    out.append((LAYER_START, f, i))
            for j, s in enumerate(self.sounds):
                if not self.fired[j] and s[1] == f:
                    self.fired[j] = True
                    out.append((SOUND, f, j))
        if frame > self.last:
            self.last = frame

    def end(self, out):
        if self.ended:
            return
        self.ended = True
        frame = 0 if self.last < 0 else self.last
        for i in self.order:
            if self.started[i]:
                out.append((LAYER_END, frame, i))


def mix(h, v):
    return ((h ^ (v & 0xFFFFFFFF)) * FNV_PRIME) & MASK


def mix_events(h, events):
    for code, frame, idx in events:
        h = mix(mix(mix(h, code), frame), idx)
    return h


# --------------------------------------------------------------------------- 统计

class Tally:
    def __init__(self):
        self.stats = collections.Counter()

    def add(self, key, n=1):
        self.stats[key] += n


def bucket(n, cap):
    return "%d+" % cap if n >= cap else str(n)


def frame_key(n):
    return "neg" if n < 0 else ("10+" if n >= 10 else str(n))


def top_directory(path):
    i = path.find("/")
    return "(root)" if i <= 0 else path[:i].lower()


def tabulate(t, sound_keys, m, has_triggers, top):
    layers, sounds = m["layers"], m["sounds"]
    t.add("layersPerMotion." + bucket(len(layers), 9))
    t.add("soundsPerMotion." + bucket(len(sounds), 9))
    if m["malformed"] > 0:
        t.add("malformedFiles")
        t.add("malformed", m["malformed"])
    if layers:
        t.add("withLayers")
        t.add("dir.%s.withLayers" % top)
    if sounds:
        t.add("withSounds")
        t.add("dir.%s.withSounds" % top)
    if layers and sounds:
        t.add("withBoth")
    if layers or sounds:
        t.add("events.withTriggers" if has_triggers else "events.noTriggers")
        t.add("dir.%s.%s" % (top, "eventsWithTriggers" if has_triggers else "eventsNoTriggers"))
    for form, start, depth in layers:
        t.add("layers")
        t.add("layers." + form)
        t.add("startFrame." + frame_key(start))
        t.add("depth." + ("neg" if depth < 0 else "zero" if depth == 0 else "pos"))
    if len(layers) >= 2:
        d = [x[2] for x in layers]
        asc = all(a <= b for a, b in zip(d, d[1:]))
        desc = all(a >= b for a, b in zip(d, d[1:]))
        equal = all(a == b for a, b in zip(d, d[1:]))
        t.add("depthOrder." + ("equal" if equal else "asc" if asc else "desc" if desc else "mixed"))
    for key, frame, percent, prob in sounds:
        t.add("sounds")
        t.add("sounds.prob" if prob else "sounds.plain")
        t.add("soundFrame." + frame_key(frame))
        if prob:
            t.add("percent." + ("other" if percent < 0 or percent > 100 else "0" if percent == 0 else "100" if percent == 100 else "1-99"))
        sound_keys.add(key)
        if any(ch in key for ch in "/\\."):
            t.add("soundKey.pathLike")
    if len(sounds) >= 2:
        frames = [s[1] for s in sounds]
        t.add("soundOrder." + ("sorted" if all(a <= b for a, b in zip(frames, frames[1:])) else "unsorted"))
        t.add("soundSameFrame." + ("yes" if len(set(frames)) < len(frames) else "no"))


def count_events(t, prefix, events):
    for code, _, _ in events:
        t.add(prefix + ".events")
        t.add(prefix + (".starts" if code == LAYER_START else ".ends" if code == LAYER_END else ".sounds"))


def simulate(t, m, dig):
    layers, sounds = m["layers"], m["sounds"]
    last = 0
    for _, start, _ in layers:
        last = max(last, start)
    for _, frame, _, _ in sounds:
        last = max(last, frame)
    frames = last + 1
    t.add("sim.motions")
    player = Player(layers, sounds)
    events = []
    for f in range(frames):
        player.advance(f, events)
    count_events(t, "sim.passA", events)
    dig["passA"] = mix_events(dig["passA"], events)

    events = []
    player.end(events)
    count_events(t, "sim.end", events)
    dig["end"] = mix_events(dig["end"], events)

    events = []
    player.begin()
    for f in range(0, frames, 3):
        player.advance(f, events)
    player.advance(frames - 1, events)
    count_events(t, "sim.passB", events)
    dig["passB"] = mix_events(dig["passB"], events)

    events = []
    player.advance(0, events)
    for f in range(1, frames):
        player.advance(f, events)
    count_events(t, "sim.loop", events)
    dig["loop"] = mix_events(dig["loop"], events)


class AniFrames:
    """主动画帧数(读不到是 -1);路径相对 .act 所在目录,折叠 '..',小写。"""

    def __init__(self, pvf):
        self.pvf = pvf
        self.cache = {}

    def get(self, act_path, rel):
        path = posixpath.normpath(posixpath.join(posixpath.dirname(act_path.lower()), rel.replace("\\", "/"))).lower()
        if path in self.cache:
            return self.cache[path]
        n = -1
        raw = self.pvf.get(path)
        if raw is not None:
            try:
                n = len(A.parse(raw)["frames"])
            except Exception:  # noqa: BLE001 读不出的一律当读不到
                n = -1
        self.cache[path] = n
        return n


def align(t, m, act_path, anis):
    if not (m["layers"] or m["sounds"]):
        return
    if not m["base"]:
        t.add("align.noBase")
        return
    n = anis.get(act_path, m["base"])
    if n < 0:
        t.add("align.baseUnreadable")
        return
    t.add("align.baseReadable")
    for _, start, _ in m["layers"]:
        t.add("align.layerInRange" if start < n else "align.layerOutOfRange")
    for _, frame, _, _ in m["sounds"]:
        t.add("align.soundInRange" if 0 <= frame < n else "align.soundOutOfRange")


def collect():
    pvf = D.Pvf()
    strings = S.Strings(pvf)
    acts = [e for e in pvf.entries if e.path.lower().endswith(".act")]
    t = Tally()
    parsed = []
    for e in acts:
        raw = pvf.read(e)
        if raw[:2] != b"\xb0\xd0":
            t.add("notTokens")
            continue
        # 没有标签的开头游离值行不进树(与 ActParser 的 LeadingValues 一样不算节点)
        lines = [ln for ln in decode_typed(raw, strings) if ln[0] is not None]
        parsed.append((e.path, lines))
    seen = set()
    for _, lines in parsed:
        for o in T.exact_match([tag for tag, _ in lines]):
            seen.add(lines[o][0])
    block_tags = seen | T.LOADER_ONLY_BLOCKS

    sound_keys = set()
    anis = AniFrames(pvf)
    dig = {k: FNV_OFFSET for k in ("order", "passA", "passB", "end", "loop")}
    for path, lines in parsed:
        pairs, _, _ = T.full_match([tag for tag, _ in lines], block_tags)
        roots = build_roots(lines, pairs)
        t.add("files")
        top = top_directory(path)
        t.add("dir.%s.files" % top)
        triggers = sum(1 for r in roots if r["tag"] == "[TRIGGER]")
        if triggers > 0:
            t.add("withTriggers")
        motions = [r for r in roots if r["tag"] == "[MOTION]"]
        if not motions:
            continue
        t.add("withMotion")
        t.add("dir.%s.withMotion" % top)
        if len(motions) > 1:
            t.add("multiMotion")
        m = read_motion(motions[0])
        tabulate(t, sound_keys, m, triggers > 0, top)
        align(t, m, path, anis)
        if m["layers"] or m["sounds"]:
            order = sorted(range(len(m["layers"])), key=lambda i: (m["layers"][i][2], i))
            h = mix(dig["order"], len(m["layers"]))
            for i in order:
                h = mix(h, i)
            dig["order"] = h
            simulate(t, m, dig)
    t.add("soundKey.distinct", len(sound_keys))
    return dict(sorted(t.stats.items())), {k: str(v) for k, v in sorted(dig.items())}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", nargs="?", default="stats", choices=["stats", "compare"])
    ap.add_argument("--out", default=os.path.join(ROOT, "out", "act_motion_ref.json"))
    ap.add_argument("--cs", default=os.path.join(ROOT, "out", "act_motion_cs.json"))
    a = ap.parse_args()
    out_abs = os.path.abspath(a.out)
    if not any(out_abs.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
        sys.exit("拒绝:输出必须在 re/ 或 out/ 下")
    stats, digests = collect()
    os.makedirs(os.path.dirname(out_abs), exist_ok=True)
    json.dump({"stats": stats, "digests": digests}, open(out_abs, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    if a.mode == "stats":
        print("键", len(stats), "摘要", len(digests), "->", out_abs)
        return
    cs = json.load(open(a.cs, encoding="utf-8"))
    cs_stats = cs["stats"]
    same = diff = 0
    bad = []
    for key in sorted(set(stats) | set(cs_stats)):
        py, cx = stats.get(key, 0), cs_stats.get(key, 0)
        if py == cx:
            same += 1
        else:
            diff += 1
            bad.append((key, py, cx))
    dsame = sum(1 for k in set(digests) | set(cs["digests"]) if digests.get(k) == cs["digests"].get(k))
    ddiff = len(set(digests) | set(cs["digests"])) - dsame
    print("计数键 %d 个:一致 %d,不一致 %d;顺序摘要 %d 项:一致 %d,不一致 %d" % (same + diff, same, diff, dsame + ddiff, dsame, ddiff))
    for key, py, cx in bad[:40]:
        print("  不一致 %-34s python=%d  c#=%d" % (key, py, cx))
    sys.exit(0 if diff == 0 and ddiff == 0 else 1)


if __name__ == "__main__":
    main()
