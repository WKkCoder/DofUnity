#!/usr/bin/env python3
"""覆盖率仪表(证据 E-098):脚本标签的覆盖、Squirrel 接口的核对状态、证据可追溯性、对拍通过率。

回答"做到多少了":
  1. 脚本标签三层覆盖:全库 3,408 种开标签里,客户端字符串里认识多少(= 知道有这个标签,E-043)、
     服务端字符串里认识多少(E-019)、我们的 C# 加载器读入了多少。按种数与按出现次数两种口径。
     "读入"只说明标签被加载器识别并存进数据结构,不等于玩法里已经生效;语法层(.act 的语法树、.ai 的词表)单列。
  2. 已接入的脚本类型里,最常见、代码还没读的标签(下一步从哪里补)。
  3. Squirrel 接口(sq_* 与对象方法)的核对状态:Verified / Inferred / Trace 的个数与按调用点加权。
  4. 证据可追溯性:台账条数、代码与文档引用的证据编号是否都能在台账里找到、生产代码里有多少文件引用了证据。
  5. 对拍通过率:读 out/verify-all.json(由 tools/verify-all.sh 产出)。

输入(都在本机、不入库):out/tag_catalog.json、re/notes/strings_decoded.tsv、re/ghidra/out/server_tag_refs.tsv、
out/verify-all.json;仓库里的 docs/native-audit.tsv、docs/sq-api-catalog.tsv、docs/evidence/ledger.md、packages/ 源码。
输出:out/coverage.json,以及 --write-doc 指定文件里 <!-- coverage:begin --> 与 <!-- coverage:end --> 之间的表格。
只含标签名、个数与比例,不含脚本内容、不含原版数值。

用法:python3 tools/py/coverage.py [--write-doc docs/coverage.md] [--json out/coverage.json]
"""
import argparse
import csv
import glob
import json
import os
import re
import sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PKG = os.path.join(ROOT, "packages", "com.dnfunity.")

# 哪些源文件读哪类脚本。级别:S = 读入数据结构(玩法代码使用),T = 只做语法/词表(还没有运行时)。
# 一个文件可以读多种脚本;一个标签字面量只有同时出现在这类脚本的标签目录里才算。
# 这是静态扫描(源文件里写成 "[标签]" 的字符串字面量),是近似:动态拼出来的标签、用大小写混写的标签按小写比较。
CONSUMERS = {
    "atk": [("S", "game/Runtime/Defs/AttackInfoLoader.cs"), ("S", "game/Runtime/Defs/WeaponHitInfo.cs")],
    "mob": [("S", "game/Runtime/Defs/MonsterDef.cs"), ("S", "game/Runtime/Defs/MonsterAbility.cs"),
            ("S", "game/Runtime/Defs/AttackKindRecord.cs"), ("S", "render/Runtime/MonsterModel.cs"), ("S", "play/Runtime/RoomCombat.cs")],
    "skl": [("S", "game/Runtime/Defs/SkillDef.cs"), ("S", "play/Runtime/HeroSkills.cs")],
    "chr": [("S", "game/Runtime/Defs/CharacterDef.cs"), ("S", "game/Runtime/Defs/WeaponSound.cs"),
            ("S", "game/Runtime/Defs/WeaponHitInfo.cs"), ("S", "render/Runtime/TownHero.cs"),
            ("S", "render/Runtime/Avatar.cs"), ("S", "play/Runtime/HeroVitals.cs"), ("S", "play/Runtime/RoomCombat.cs")],
    "equ": [("S", "render/Runtime/Avatar.cs")],
    "map": [("S", "render/Runtime/RoomMap.cs"), ("S", "render/Runtime/MapScript.cs"), ("S", "render/Runtime/TownScene.cs"),
            ("S", "render/Runtime/TownExits.cs"), ("S", "render/Runtime/RoomSession.cs"), ("S", "render/Runtime/RoomGate.cs"),
            ("S", "render/Runtime/RoomObjects.cs"), ("S", "render/Runtime/DungeonLayout.cs"), ("S", "render/Runtime/RoomScene.cs")],
    "til": [("S", "render/Runtime/RoomMap.cs")],
    "obj": [("S", "game/Runtime/Defs/PassiveObjectDef.cs"), ("S", "game/Runtime/Defs/ObjectDef.cs"),
            ("S", "render/Runtime/RoomObjects.cs")],
    "act": [("T", "game/Runtime/Act/ActParser.cs"), ("S", "play/Runtime/ClearCamp.cs")],
    "ai": [("T", "game/Runtime/Ai/AiVocabulary.cs"), ("T", "game/Runtime/Ai/AiScript.cs")],
    "dgn": [("S", "render/Runtime/DungeonMaze.cs"), ("S", "render/Runtime/DungeonLayout.cs"), ("S", "render/Runtime/WorldMapDef.cs"),
            ("S", "play/Runtime/SplashScreen.cs")],
    "twn": [("S", "render/Runtime/TownDef.cs"), ("S", "play/Runtime/SplashScreen.cs")],
    "wdm": [("S", "render/Runtime/WorldMapDef.cs")],
    "ui": [("S", "render/Runtime/WorldMapDef.cs")],
    "etc": [("S", "game/Runtime/Damage/DamageConstants.cs"), ("S", "game/Runtime/Damage/HitStun.cs")],
    "cre": [("S", "game/Runtime/Damage/DamageConstants.cs")],
    "npc": [("S", "play/Runtime/ClearCamp.cs")],
}

LOADER_NAMES = {
    "atk": "AttackInfoLoader", "mob": "MonsterDef / MonsterAbility / AttackKindRecord", "skl": "SkillDef", "chr": "CharacterDef / TownHero / Avatar",
    "equ": "Avatar(只读换装图层)", "map": "RoomMap / MapScript / TownScene", "til": "RoomMap", "obj": "PassiveObjectDef / ObjectDef",
    "act": "ActParser(语法树)", "ai": "AiVocabulary / AiParser(词表与解析)", "dgn": "DungeonMaze / DungeonLayout",
    "twn": "TownDef", "wdm": "WorldMapDef", "ui": "WorldMapDef(只读副本选择界面用到的几项)", "etc": "DamageConstants / HitStun",
    "cre": "DamageConstants", "npc": "ClearCamp",
}

# 哪些类型有"独立参照"的整库对拍(结果见表 5);其余类型的读法只有合成数据测试。
INDEPENDENT_CHECK = {
    "atk": "E-047:全库逐字段对 Python 参照",
    "mob": "E-062、E-063:数值块对 Python 参照与原版机器码",
    "act": "E-050:语法树对 Python 参照(只到结构)",
    "ai": "E-052:全库读通(没有独立参照)",
}

# 不实现原版规则的工具与基础设施,不要求引用证据编号(原因写在这里,文档里会列出)。
EXEMPT = {
    "play/DungeonBot.cs": "自动打副本的测试工具,不代表原版 AI",
    "play/CombatScript.cs": "按时间表按键的测试工具",
    "play/TouchScript.cs": "按时间表替手指操作触屏层的测试工具",
    "client/TouchOverlay.cs": "触屏控件的绘制与触摸读取(本项目自己的界面,不是原版规则)",
    "client/RenderSelfTest.cs": "Unity 播放器的自检",
    "client/UnityObjects.cs": "Unity 对象封装",
    "game/Defs/TagTable.cs": "按标签名索引的语法层便利,不赋予标签含义",
    "data/Latin1.cs": "字节与字符的小工具",
    "data/Adler32.cs": "校验和的小工具",
    "data/DnfFormatException.cs": "异常类型",
    "squirrel/MonoPInvokeCallbackAttribute.cs": "Unity 的属性声明",
}

LITERAL = re.compile(r'"(\[/?[^\]"\\\n]+\])"')
EVIDENCE = re.compile(r"E-\d{3}")


def pct(a, b):
    return 100.0 * a / b if b else 0.0


def read_tsv(path, skip_header=True):
    with open(path, encoding="utf-8", newline="\n") as fh:
        rows = [ln.rstrip("\n").split("\t") for ln in fh.read().split("\n") if ln.strip()]
    return rows[1:] if skip_header else rows


# ---------------------------------------------------------------- 标签覆盖

def code_literals():
    """每个消费文件里的标签字面量(小写,去掉闭合斜杠)。"""
    cache = {}
    for exts in CONSUMERS.values():
        for _, rel in exts:
            if rel in cache:
                continue
            path = PKG + rel.split("/", 1)[0] + "/" + rel.split("/", 1)[1]
            tags = set()
            if os.path.exists(path):
                text = open(path, encoding="utf-8").read()
                for m in LITERAL.finditer(text):
                    tags.add(m.group(1).lower().replace("[/", "["))
            else:
                print("警告:消费文件不存在 " + rel, file=sys.stderr)
            cache[rel] = tags
    return cache


def tag_coverage(catalog_path, client_path, server_path):
    cat = json.load(open(catalog_path, encoding="utf-8"))
    by_ext = {e: {t["tag"]: t["count"] for t in v["tags"]} for e, v in cat["extensions"].items()}
    files = {e: v["files"] for e, v in cat["extensions"].items()}

    client = set()
    if os.path.exists(client_path):
        for r in read_tsv(client_path):
            if len(r) >= 5 and re.fullmatch(r"\[[^\]\n]+\]", r[4]):
                client.add(r[4])
    server = set()
    if os.path.exists(server_path):
        for r in read_tsv(server_path):
            if len(r) >= 3 and int(r[2]) > 0:
                server.add(r[0])
    lits = code_literals()

    rows = []
    corpus = Counter()
    for ext, tags in by_ext.items():
        for t, n in tags.items():
            corpus[t] += n
        occ = sum(tags.values())
        read_s, read_t = set(), set()
        for level, rel in CONSUMERS.get(ext, []):
            for t in tags:
                if t.lower() in lits.get(rel, ()):
                    (read_s if level == "S" else read_t).add(t)
        read_t -= read_s
        kn_client = {t for t in tags if t in client}
        kn_server = {t for t in tags if t in server}
        rows.append({
            "ext": ext, "files": files[ext], "tags": len(tags), "occ": occ,
            "client_tags": len(kn_client), "client_occ": sum(tags[t] for t in kn_client),
            "server_tags": len(kn_server), "server_occ": sum(tags[t] for t in kn_server),
            "read_tags": len(read_s), "read_occ": sum(tags[t] for t in read_s),
            "syntax_tags": len(read_t), "syntax_occ": sum(tags[t] for t in read_t),
            "has_loader": ext in CONSUMERS,
            "unread": sorted(((t, tags[t]) for t in tags if t not in read_s), key=lambda x: -x[1]),
            "unread_known": {t: (t in client, t in server) for t in tags},
        })
    rows.sort(key=lambda r: -r["occ"])
    total_occ = sum(corpus.values())
    totals = {
        "distinct": len(corpus), "occ": total_occ,
        "client_distinct": sum(1 for t in corpus if t in client), "client_occ": sum(n for t, n in corpus.items() if t in client),
        "server_distinct": sum(1 for t in corpus if t in server), "server_occ": sum(n for t, n in corpus.items() if t in server),
        "client_strings": len(client), "server_strings": len(server),
        "ani_files": cat.get("skipped", {}).get("ani", 0),
    }
    return rows, totals


# ---------------------------------------------------------------- Squirrel 接口

def sq_status():
    """按"脚本里被调用的名字"(docs/sq-api-catalog.tsv)归类核对状态。登记表(docs/native-audit.tsv)里的名字常带类名前缀,
    所以同时按"去掉类名后的名字"匹配;同名多个登记时取最低的状态(保守)。宿主内置(加载脚本用的 pushState 等)不在登记表里,
    由 game-check 的 JSON(out/verify-all/game_check.json)的"已登记"列表判断;没有这个文件时它们会记成没登记。"""
    audit = list(csv.DictReader(open(os.path.join(ROOT, "docs", "native-audit.tsv"), encoding="utf-8"), delimiter="\t"))
    cat = list(csv.DictReader(open(os.path.join(ROOT, "docs", "sq-api-catalog.tsv"), encoding="utf-8"), delimiter="\t"))
    rank = {"Trace": 0, "Inferred": 1, "Verified": 2}
    by_name = {}

    def put(n, s):
        by_name[n] = s if n not in by_name else min(by_name[n], s, key=lambda x: rank[x])
    for r in audit:
        put(r["name"], r["status"])
        if "." in r["name"]:
            put(r["name"].split(".", 1)[1], r["status"])
    host = set()
    gc = os.path.join(ROOT, "out", "verify-all", "game_check.json")
    if os.path.exists(gc):
        host = set(json.load(open(gc, encoding="utf-8"))["coverage"]["registered"])
    names, calls = Counter(), Counter()
    for r in cat:
        n, c = r["name"], int(r["calls"])
        s = by_name.get(n) or ("宿主内置" if n in host else "没登记")
        names[s] += 1
        calls[s] += c
    entries = Counter(r["status"] for r in audit)
    called = {r["name"] for r in cat}
    unused = sum(1 for r in audit if r["name"] not in called and r["name"].split(".", 1)[-1] not in called)
    inferred = sorted((r for r in audit if r["status"] == "Inferred"), key=lambda r: -int(r["script_calls"] or 0))
    missing = sorted((r for r in cat if (by_name.get(r["name"]) is None and r["name"] not in host)), key=lambda r: -int(r["calls"]))
    return {
        "names": len(cat), "calls": sum(int(r["calls"]) for r in cat),
        "names_by_status": dict(names), "calls_by_status": dict(calls),
        "registry_entries": len(audit), "registry_by_status": dict(entries), "registry_unused": unused,
        "top_inferred": [(r["name"], int(r["script_calls"] or 0)) for r in inferred[:12]],
        "top_unregistered": [(r["name"], int(r["calls"])) for r in missing[:8]],
    }


# ---------------------------------------------------------------- 可追溯性

def traceability():
    ledger_text = open(os.path.join(ROOT, "docs", "evidence", "ledger.md"), encoding="utf-8").read()
    ids = []
    with_repro = 0
    for ln in ledger_text.split("\n"):
        m = re.match(r"\| (E-\d{3}) \|", ln)
        if not m:
            continue
        ids.append(m.group(1))
        cells = [c.strip() for c in ln.strip().strip("|").split(" | ")]
        # 台账的倒数第二栏是"复现"(命令或文件);要求里面至少有一处代码标记
        if len(cells) >= 4 and "`" in cells[-2]:
            with_repro += 1
    ledger = set(ids)
    cited = Counter()
    patterns = [os.path.join(ROOT, p) for p in (
        "packages/**/*.cs", "src/**/*.cs", "docs/*.md", "README.md", "tools/**/*.py", "tools/*.sh", "tools/ghidra/*.java")]
    for pat in patterns:
        for f in glob.glob(pat, recursive=True):
            # 台账自己、以及本脚本生成的 coverage.md(它会把"没人引用的编号"列出来,不能算引用)不参与
            if f.endswith("ledger.md") or f.endswith("coverage.md") or "/bin/" in f or "/obj/" in f:
                continue
            try:
                text = open(f, encoding="utf-8").read()
            except (OSError, UnicodeDecodeError):
                continue
            for e in set(EVIDENCE.findall(text)):
                cited[e] += 1
    dangling = sorted(e for e in cited if e not in ledger)
    uncited = sorted(e for e in ledger if e not in cited)

    per_pkg = defaultdict(lambda: [0, 0, 0, 0])  # 文件数、引用了证据的文件数、行数、未引用文件的行数
    uncited_files = []
    exempt_files = []
    for f in sorted(glob.glob(os.path.join(ROOT, "packages", "com.dnfunity.*", "Runtime", "**", "*.cs"), recursive=True)):
        pkg = f.split("com.dnfunity.")[1].split("/")[0]
        rel_short = os.path.relpath(f, ROOT).replace("packages/com.dnfunity.", "").replace("/Runtime", "")
        if rel_short in EXEMPT:
            exempt_files.append((rel_short, EXEMPT[rel_short]))
            continue
        text = open(f, encoding="utf-8").read()
        lines = text.count("\n") + 1
        per_pkg[pkg][0] += 1
        per_pkg[pkg][2] += lines
        if EVIDENCE.search(text):
            per_pkg[pkg][1] += 1
        else:
            per_pkg[pkg][3] += lines
            uncited_files.append((os.path.relpath(f, ROOT).replace("packages/com.dnfunity.", "").replace("/Runtime", ""), lines))
    return {
        "ledger_rows": len(ids), "ledger_first": ids[0] if ids else "", "ledger_last": ids[-1] if ids else "",
        "rows_with_repro": with_repro,
        "cited_ids": len(cited), "dangling": dangling, "uncited": uncited,
        "per_package": {k: v for k, v in sorted(per_pkg.items())}, "uncited_files": uncited_files, "exempt_files": exempt_files,
    }


# ---------------------------------------------------------------- 输出

def md_table(header, rows, align=None):
    out = ["| " + " | ".join(header) + " |", "|" + "|".join(("---:" if (align and align[i] == "r") else "---") for i in range(len(header))) + "|"]
    for r in rows:
        out.append("| " + " | ".join(str(x) for x in r) + " |")
    return "\n".join(out)


def fmt_n(n):
    return "{:,}".format(n)


def render(rows, totals, sq, tr, verify, key_exts):
    parts = []

    # 表 1
    header = ["类型", "文件", "标签种数", "出现次数", "占总次数", "客户端认识", "服务端认识", "加载器读入", "仅语法层", "读入的加载器", "独立核对"]
    tab = []
    shown = [r for r in rows if r["occ"] * 1000 >= totals["occ"] or r["has_loader"]]
    rest = [r for r in rows if r not in shown]
    for r in shown:
        tab.append(["`.%s`" % r["ext"], fmt_n(r["files"]), r["tags"], fmt_n(r["occ"]), "%.1f%%" % pct(r["occ"], totals["occ"]),
                    "%d / %.1f%%" % (r["client_tags"], pct(r["client_occ"], r["occ"])),
                    "%d / %.1f%%" % (r["server_tags"], pct(r["server_occ"], r["occ"])),
                    ("%d / %.1f%%" % (r["read_tags"], pct(r["read_occ"], r["occ"]))) if r["has_loader"] else "—",
                    ("%d / %.1f%%" % (r["syntax_tags"], pct(r["syntax_occ"], r["occ"]))) if r["syntax_tags"] else "",
                    LOADER_NAMES.get(r["ext"], ""), INDEPENDENT_CHECK.get(r["ext"], "")])
    if rest:
        occ = sum(r["occ"] for r in rest)
        tab.append(["其余 %d 类" % len(rest), fmt_n(sum(r["files"] for r in rest)), "—", fmt_n(occ), "%.1f%%" % pct(occ, totals["occ"]),
                    "%.1f%%" % pct(sum(r["client_occ"] for r in rest), occ), "%.1f%%" % pct(sum(r["server_occ"] for r in rest), occ),
                    "—", "", "(没有加载器)", ""])
    wired = [r for r in rows if r["has_loader"]]
    w_occ = sum(r["occ"] for r in wired)
    tab.append(["**全部**", fmt_n(sum(r["files"] for r in rows)), "—", fmt_n(totals["occ"]), "100%",
                "%d / %.2f%%" % (totals["client_distinct"], pct(totals["client_occ"], totals["occ"])),
                "%d / %.2f%%" % (totals["server_distinct"], pct(totals["server_occ"], totals["occ"])),
                "%.1f%%" % pct(sum(r["read_occ"] for r in wired), totals["occ"]),
                "%.1f%%" % pct(sum(r["syntax_occ"] for r in wired), totals["occ"]), "按出现次数", ""])
    tab.append(["**已接入的 %d 类**" % len(wired), fmt_n(sum(r["files"] for r in wired)), "—", fmt_n(w_occ), "%.1f%%" % pct(w_occ, totals["occ"]),
                "%.1f%%" % pct(sum(r["client_occ"] for r in wired), w_occ), "%.1f%%" % pct(sum(r["server_occ"] for r in wired), w_occ),
                "%.1f%%" % pct(sum(r["read_occ"] for r in wired), w_occ), "%.1f%%" % pct(sum(r["syntax_occ"] for r in wired), w_occ),
                "只算有加载器的类型", ""])
    parts.append("### 表 1　脚本标签的三层覆盖(按脚本类型)\n\n"
                 "每格是 `标签种数 / 按出现次数的占比`。全库共 %s 种开标签、%s 次出现(不含 `.ani`:%s 个文件,格式不同,见表 5 的 P1 行)。\n\n"
                 % (fmt_n(totals["distinct"]), fmt_n(totals["occ"]), fmt_n(totals["ani_files"])) + md_table(header, tab, "lrrrrrrrrll"))

    # 表 2
    lines = []
    for ext in key_exts:
        r = next((x for x in rows if x["ext"] == ext), None)
        if not r:
            continue
        top = r["unread"][:7]
        cells = []
        for t, n in top:
            c, s = r["unread_known"][t]
            cells.append("`%s` %s%s" % (t, fmt_n(n), "" if (c and s) else (" (仅客户端)" if c else (" (仅服务端)" if s else " (都不认识)"))))
        lines.append(["`.%s`" % ext, fmt_n(r["tags"] - r["read_tags"]), "; ".join(cells)])
    parts.append("### 表 2　已接入的类型里,最常见而加载器还没读入的标签\n\n"
                 "每类列出出现次数最多的 7 个(括号里说明哪边的词表不认识它)。这就是这些类型下一步该补的清单;"
                 "`.act` 与 `.ai` 的\"语法层\"已读通全部文件,但运行时(动作状态机、怪物 AI 求值)还没有做,所以它们的标签大多在这张表里。"
                 "判断是静态扫描源码里的字面量,有两类误差:靠默认值处理而没写出字面量的标签(例如 `.atk` 的\"无属性\"),会被记成没读;"
                 "字面量出现在读入的文件里但没真正用上的,会被记成读了。\n\n"
                 + md_table(["类型", "没读入的种数", "出现最多的没读标签(次数)"], lines))

    # 表 3
    nm, cl = sq["names_by_status"], sq["calls_by_status"]
    tab = []
    for name, label in (("Verified", "Verified:读过客户端实现并核对"), ("Inferred", "Inferred:按调用点与语义推断实现"),
                        ("Trace", "Trace:只占位(记录调用,不做事)"), ("宿主内置", "宿主内置:加载脚本用的 `pushState` 等,由脚本宿主实现"),
                        ("没登记", "没登记")):
        tab.append([label, nm.get(name, 0), fmt_n(cl.get(name, 0)), "%.1f%%" % pct(cl.get(name, 0), sq["calls"])])
    tab.append(["**合计**", sq["names"], fmt_n(sq["calls"]), "100%"])
    rg = sq["registry_by_status"]
    parts.append("### 表 3　Squirrel 接口(脚本里被调用的 %d 个名字、%s 个调用点)\n\n" % (sq["names"], fmt_n(sq["calls"]))
                 + md_table(["状态", "名字个数", "调用点", "占调用点"], tab, "lrrr")
                 + "\n\n登记表(`docs/native-audit.tsv`)共 %d 项:Verified %d、Inferred %d、Trace %d(其中 %d 项脚本里没人调用)。"
                 % (sq["registry_entries"], rg.get("Verified", 0), rg.get("Inferred", 0), rg.get("Trace", 0), sq["registry_unused"])
                 + "下一批该核对的(Inferred 里调用最多的 12 个):" + "、".join("`%s`(%d)" % x for x in sq["top_inferred"])
                 + "。最常被调用却没登记的:" + "、".join("`%s`(%d)" % x for x in sq["top_unregistered"]) + "。")

    # 表 4
    pp = tr["per_package"]
    tab = []
    tf = tc = tl = tu = 0
    for k, (n, c, l, ul) in pp.items():
        tab.append(["`com.dnfunity.%s`" % k, n, c, "%.0f%%" % pct(c, n), fmt_n(l), "%.0f%%" % pct(l - ul, l)])
        tf, tc, tl, tu = tf + n, tc + c, tl + l, tu + ul
    tab.append(["**合计**", tf, tc, "%.0f%%" % pct(tc, tf), fmt_n(tl), "%.0f%%" % pct(tl - tu, tl)])
    ex = tr["exempt_files"]
    left = sorted(tr["uncited_files"], key=lambda x: -x[1])
    parts.append("### 表 4　证据可追溯性\n\n"
                 "台账 %d 条(%s 到 %s),其中 %d 条的\"复现\"栏里有可运行的命令或文件。"
                 "代码、测试、文档与脚本里一共引用了 %d 个不同的证据编号;**台账里找不到的引用:%s**;台账里没人引用的编号:%s。\n\n"
                 % (tr["ledger_rows"], tr["ledger_first"], tr["ledger_last"], tr["rows_with_repro"],
                    tr["cited_ids"], ("、".join(tr["dangling"]) if tr["dangling"] else "无"),
                    ("、".join(tr["uncited"]) if tr["uncited"] else "无"))
                 + md_table(["包", "生产代码文件", "引用了证据的文件", "占比", "行数", "引用证据的文件所含行数占比"], tab, "lrrrrr")
                 + "\n\n另有 %d 个文件不实现原版规则(测试工具、Unity 自检与封装、小工具),不要求引用证据,不计入上表:" % len(ex)
                 + "、".join("`%s`" % f for f, _ in ex) + "。"
                 + ("\n\n还没引用证据的文件:" + "、".join("`%s`(%d 行)" % x for x in left) + "。" if left else "\n\n每个文件都引用了证据编号。"))

    # 表 5:对拍
    if verify:
        tab = []
        for r in verify["results"]:
            rate = ("%.4f%%" % pct(r["cases"] - r["bad"], r["cases"])) if r["cases"] else "—"
            if rate == "100.0000%":
                rate = "100%"
            tab.append([r["title"], r["kind"], r["evidence"], fmt_n(r["cases"]), fmt_n(r["bad"]), rate, r["note"]])
        parts.append("### 表 5　对拍与全库读通(`tools/verify-all.sh`,%s)\n\n" % verify["generated_utc"]
                     + md_table(["检查", "类型", "证据", "用例", "不一致/失败", "通过率", "说明"], tab, "lllrrrl"))
    else:
        parts.append("### 表 5　对拍与全库读通\n\n(没有 out/verify-all.json:先运行 `tools/verify-all.sh`。)")
    return "\n\n".join(parts)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--catalog", default=os.path.join(ROOT, "out", "tag_catalog.json"))
    ap.add_argument("--client", default=os.path.join(ROOT, "re", "notes", "strings_decoded.tsv"))
    ap.add_argument("--server", default=os.path.join(ROOT, "re", "ghidra", "out", "server_tag_refs.tsv"))
    ap.add_argument("--verify", default=os.path.join(ROOT, "out", "verify-all.json"))
    ap.add_argument("--json", default=os.path.join(ROOT, "out", "coverage.json"))
    ap.add_argument("--write-doc", default="")
    args = ap.parse_args()

    rows, totals = tag_coverage(args.catalog, args.client, args.server)
    sq = sq_status()
    tr = traceability()
    verify = json.load(open(args.verify, encoding="utf-8")) if os.path.exists(args.verify) else None
    key_exts = ["atk", "mob", "skl", "chr", "map", "obj", "act", "ai", "dgn", "twn", "etc", "cre", "npc"]

    text = render(rows, totals, sq, tr, verify, key_exts)
    os.makedirs(os.path.dirname(args.json), exist_ok=True)
    slim = [{k: v for k, v in r.items() if k not in ("unread", "unread_known")} for r in rows]
    with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
        json.dump({"tags": slim, "totals": totals, "sq": sq, "trace": tr, "verify": verify}, fh, ensure_ascii=False, indent=1)

    if args.write_doc:
        doc = open(args.write_doc, encoding="utf-8").read()
        begin, end = "<!-- coverage:begin -->", "<!-- coverage:end -->"
        i, j = doc.find(begin), doc.find(end)
        if i < 0 or j < 0 or j < i:
            print("文档里没有 %s … %s 标记" % (begin, end), file=sys.stderr)
            return 1
        doc = doc[:i + len(begin)] + "\n\n" + text + "\n\n" + doc[j:]
        with open(args.write_doc, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(doc)
        print("已更新 " + args.write_doc)
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
