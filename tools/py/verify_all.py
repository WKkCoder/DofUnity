#!/usr/bin/env python3
"""重跑全部"C# 实现 vs 独立参照"的对拍与全库读通检查,把通过率汇总到 out/verify-all.json(证据 E-098)。

每一项给出:用例数(cases)、不一致/失败数(bad)、用时、依据的证据编号。
这里的"参照"有三种:
  - Python 独立实现(读规格、各自读脚本,只共用语义规格),
  - 原版二进制:服务端 ELF 在 Unicorn 模拟器里跑出来的结果(oracle,需要 re/venv),
  - 全库读通:C# 数据层读完全部用户自备数据,不报错且与清单一致。
输出只含个数与通过率,不含原版数值(原版派生数值只写到 out/ 下的临时文件)。

用法(由 tools/verify-all.sh 调用;需要先 . tools/env.sh 并已构建 dnf):
  python3 tools/py/verify_all.py [--only 名字,名字] [--skip 名字,名字] [--out out/verify-all.json]
名字:unit p1 atk act mob damage sq sweep flow dungeons sound exp oracle(oracle 需要 re/venv 里的 Unicorn 与本机的服务端二进制;dungeons 是全库副本扫描,非门槛项)
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WORK = os.path.join(ROOT, "out", "verify-all")
DNF = ["dotnet", os.path.join(ROOT, "src", "Dnf.Cli", "bin", "Release", "net10.0", "dnf.dll")]
PY = [sys.executable]
VENV_PY = os.path.join(ROOT, "re", "venv", "bin", "python")


def sh(cmd, log_name, env=None):
    """运行一条命令,把输出写到 WORK/<log_name>,返回 (退出码, 输出文本, 秒)。"""
    os.makedirs(WORK, exist_ok=True)
    t0 = time.time()
    p = subprocess.run(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env or os.environ.copy())
    text = p.stdout.decode("utf-8", errors="replace")
    with open(os.path.join(WORK, log_name), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    return p.returncode, text, time.time() - t0


def item(key, title, kind, evidence, cases, bad, seconds, note="", gate=True):
    """gate=False 的项只作记录(例如逐技能扫描:没跑完不等于出错),不计入"全部通过"。"""
    return {"key": key, "title": title, "kind": kind, "evidence": evidence, "cases": cases, "bad": bad,
            "seconds": round(seconds, 1), "note": note, "gate": gate}


def read_lines(path):
    with open(path, encoding="utf-8") as fh:
        return [ln.rstrip("\n") for ln in fh if ln.strip()]


def compare_lines(ref_lines, cs_lines):
    """把两份输出当作"行的多重集"比较(与行序无关):返回 (参照的行数, 两边对不上的行数)。
    对不上的行数 = 只在一边出现的行的总数(两边各一行不同算 2)。"""
    from collections import Counter
    a, b = Counter(ref_lines), Counter(cs_lines)
    diff = sum(((a - b) + (b - a)).values())
    return len(ref_lines), diff


# ---------------------------------------------------------------- 各项检查

def check_unit():
    env = os.environ.copy()
    env["DOTNET_CLI_UI_LANGUAGE"] = "en"
    code, text, sec = sh(["dotnet", "test", "DnfUnity.slnx", "-c", "Release", "--no-build"], "unit.txt", env)
    total = passed = failed = skipped = 0
    projects = 0
    for m in re.finditer(r"(?:Passed|Failed|已通过|失败)!\s*-\s*(?:Failed|失败):\s*(\d+)[,，]\s*(?:Passed|通过):\s*(\d+)[,，]\s*(?:Skipped|已跳过):\s*(\d+)[,，]\s*(?:Total|总计):\s*(\d+)", text):
        failed += int(m.group(1))
        passed += int(m.group(2))
        skipped += int(m.group(3))
        total += int(m.group(4))
        projects += 1
    note = "%d 个测试项目(合成数据,不含原版数值);跳过 %d" % (projects, skipped)
    return [item("unit", "单元测试(合成数据)", "合成数据测试", "—", total, failed + (0 if code == 0 or projects else 1), sec, note)]


def check_p1():
    code, text, sec = sh(DNF + ["verify", "all"], "p1.txt")
    path = os.path.join(ROOT, "out", "p1_verification_all.json")
    if code != 0 and not os.path.exists(path):
        return [item("p1", "P1 全库读通", "全库读通", "E-008", 0, 1, sec, "verify all 没有产出报告")]
    j = json.load(open(path, encoding="utf-8"))
    by = {c["name"]: c for c in j["checks"]}
    out = []

    def add(name, title, cases, bad, note):
        c = by.get(name)
        if c is None:
            out.append(item("p1_" + name, title, "全库读通", "E-008", 0, 1, 0, "缺这一项"))
            return
        bad_total = bad(c["metrics"]) + (0 if c["passed"] else 1)
        out.append(item("p1_" + name, title, "全库读通", "E-008", cases(c["metrics"]), bad_total, c["seconds"], note(c["metrics"])))

    add("pvf", "PVF:全部文件逐个核对 CRC 与清单", lambda m: m["files"],
        lambda m: m["file_checksum_failures"] + m["hash_mismatches"] + m["length_mismatches"] + m["path_mismatches"],
        lambda m: "目录树校验%s;明文 %d MB" % ("通过" if m["directory_checksum_valid"] else "失败", m["plaintext_bytes"] // 1000000))
    add("tags", "标记流脚本:全库读通,标签目录与 Python 逐项一致", lambda m: m["files_parsed"],
        lambda m: m["bad_tag_indexes"],
        lambda m: "%d 种开标签、%d 次出现;另有 %d 个文件不是标记流(.ani 等,单独检查)" % (m["distinct_open_tags"], m["open_tag_occurrences"], m["skipped_total"]))
    add("ani", ".ani 动画:全部读通,统计与 Python 逐项一致", lambda m: m["attempted"],
        lambda m: m["attempted"] - m["parsed_ok"],
        lambda m: "%d 帧、%d 个框" % (m["frames"], m["boxes"]))
    add("imgs", "NPK/IMG:全部读通,帧类型与 Python 扫描逐项一致", lambda m: m["counters"]["frames"],
        lambda m: max(0, m["counters"].get("anomaly:zlib", 0) - m["explained_placeholder_frames"]),
        lambda m: "%d 个 NPK、%d 张 IMG;%d 帧是原版数据里就解不开的占位帧(已逐帧说明)" % (m["npk_files"], m["counters"]["entries"], m["explained_placeholder_frames"]))
    add("pixels", "像素:随机 1000 帧与参考解码逐字节一致", lambda m: m["frames_compared"],
        lambda m: m["frames_mismatched"], lambda m: "每种帧类型与压缩方式都有样本")
    return out


def check_atk():
    ref = os.path.join(WORK, "atk_ref.txt")
    cs = os.path.join(WORK, "atk_cs.txt")
    c1, _, s1 = sh(PY + ["tools/py/atk_ref.py", "--dump", ref], "atk_ref.log")
    c2, _, s2 = sh(DNF + ["atk-check", "--dump", cs], "atk_cs.log")
    if c1 != 0 or c2 != 0:
        return [item("atk", ".atk 加载器 vs Python 参照", "对拍:独立参照", "E-047", 0, 1, s1 + s2, "有一边没跑成")]
    n, bad = compare_lines(read_lines(ref), read_lines(cs))
    return [item("atk", ".atk 加载器 vs Python 参照(每个文件的全部字段)", "对拍:独立参照", "E-047", n, bad, s1 + s2,
                 "全库 .atk;原版对不规整之处不报错,按各自的宽容规则继续,两边规则一致")]


def check_act():
    ref = os.path.join(WORK, "act_tree.json")
    cs = os.path.join(WORK, "act_cs.json")
    c1, _, s1 = sh(PY + ["tools/py/act_tree.py", "--out", ref], "act_ref.log")
    c2, _, s2 = sh(DNF + ["act-check", "--json", cs], "act_cs.log")
    if c1 != 0 or c2 != 0:
        return [item("act", ".act 语法树 vs Python 参照", "对拍:独立参照", "E-050", 0, 1, s1 + s2, "有一边没跑成")]
    a = json.load(open(ref, encoding="utf-8"))
    b = json.load(open(cs, encoding="utf-8"))

    def flat(c):
        return {(p, k): n for p, d in c.items() for k, n in d.items()}
    fa, fb = flat(a["children"]), flat(b["children"])
    keys = set(fa) | set(fb)
    bad = sum(1 for k in keys if fa.get(k) != fb.get(k))
    extra = sum(1 for k in ("files", "nodes", "stray", "repaired") if a.get(k) != b.get(k))
    return [item("act", ".act 语法树 vs Python 参照(父块→子标签→次数表与四个总数)", "对拍:独立参照", "E-050",
                 len(keys) + 4, bad + extra, s1 + s2, "%d 个文件、%d 个节点" % (b["files"], b["nodes"]))]


def check_mob():
    ref = os.path.join(WORK, "mob_ref.tsv")
    cs = os.path.join(WORK, "mob_cs.tsv")
    c1, _, s1 = sh(PY + ["tools/py/mob_ability_ref.py", "--dump", ref], "mob_ref.log")
    c2, _, s2 = sh(DNF + ["mob-check", "--dump", cs, "--json", os.path.join(WORK, "mob_check.json")], "mob_cs.log")
    if c1 != 0 or c2 != 0:
        return [item("mob", "怪物数值 vs Python 参照", "对拍:独立参照", "E-062", 0, 1, s1 + s2, "有一边没跑成")]
    n, bad = compare_lines(read_lines(ref), read_lines(cs))
    j = json.load(open(os.path.join(WORK, "mob_check.json"), encoding="utf-8"))
    # 同一份 dump 还要给 oracle 用(它读 out/mob_cs.tsv)
    try:
        with open(cs, "rb") as fi, open(os.path.join(ROOT, "out", "mob_cs.tsv"), "wb") as fo:
            fo.write(fi.read())
    except OSError:
        pass
    return [item("mob", "怪物数值 vs Python 参照(每个怪物在最小/中间/最大等级×普通/首领的完整数值块)", "对拍:独立参照", "E-062", n, bad, s1 + s2,
                 "monster.lst 列出的 %d 个 .mob,读通 %d、没读通 %d" % (j["listed"], j["parsed"], j["notParsed"]))]


def check_damage():
    cases = os.path.join(WORK, "damage_cases.txt")
    c1, _, s1 = sh(PY + ["tools/py/damage_ref.py", "--gen", "200000", "--seed", "20261015", "--out", cases], "damage_gen.log")
    c2, text, s2 = sh(DNF + ["damage-check", "--cases", cases], "damage_cs.log")
    m = re.search(r"对拍 (\d+) 个用例.*?:(全部一致|不一致 (\d+) 个)", text)
    if c1 != 0 or not m:
        return [item("damage", "伤害公式 vs Python 参照", "对拍:独立参照", "E-049", 0, 1, s1 + s2, "没跑成")]
    n = int(m.group(1))
    bad = 0 if m.group(2) == "全部一致" else int(m.group(3))
    return [item("damage", "伤害公式 vs Python 参照(随机 20 万个用例,含暴击与额外命中)", "对拍:独立参照", "E-049", n, bad, s1 + s2,
                 "Python 参照与服务端伤害校验的机器码按语句核对过;种子固定 20261015")]


def check_sq():
    out = os.path.join(WORK, "sq_check.json")
    c, text, sec = sh(DNF + ["sq-check", "--json", out], "sq.txt")
    # sq-check 在缺接口时退出码为 1,不代表失败;看 JSON
    if not os.path.exists(out):
        return [item("sq", "Squirrel 脚本编译与加载链", "全库读通", "E-033", 0, 1, sec, "没有产出 JSON")]
    j = json.load(open(out, encoding="utf-8"))
    return [
        item("sq_compile", "Squirrel 3.2 编译全部明文 .nut", "全库读通", "E-033", j["nutFiles"], len(j["compileFailures"]), sec,
             "%d 字节;Squirrel 版本 %d(逐字节未修改)" % (j["nutBytes"], j["squirrel"])),
        item("sq_load", "按原版加载约定跑 sqr/loadstate.nut 的整条加载链", "全库读通", "E-035", j["loadedScripts"],
             len(j["loadErrors"]) + len(j["missingScripts"]), sec, "登记 %d 个状态的回调;脚本向引擎索要却没有定义的名字 %d 个" % (len(j["states"]), len(j["unresolvedNames"]))),
    ]


def check_sweep():
    out = os.path.join(WORK, "game_check.json")
    c, text, sec = sh(DNF + ["game-check", "--sweep", "--json", out], "sweep.txt")
    if not os.path.exists(out):
        return [item("sweep", "逐技能扫描", "原版脚本直跑", "E-046", 0, 1, sec, "没有产出 JSON")]
    j = json.load(open(out, encoding="utf-8"))
    sweep = j["sweep"]
    ran = sum(1 for s in sweep if s["Result"] == "跑完")
    errors = sum(len(s["Errors"]) for s in sweep)
    cov = j["coverage"]
    note = "原版状态脚本不改动地跑完 %d/%d 个技能状态,脚本错误 %d 条;sq 接口登记 %d/%d 个名字,覆盖调用点 %d/%d" % (
        ran, len(sweep), errors, cov["implemented"], cov["names"], cov["coveredCalls"], cov["calls"])
    return [item("sweep", "逐技能扫描:原版 Squirrel 状态脚本直接跑(假人、技能等级 10、最多 12 秒)", "原版脚本直跑", "E-046", len(sweep),
                 len(sweep) - ran, sec, note + ";没跑完的是没进入状态或超时,不等于行为错误", gate=False)]


def check_flow():
    # (标题, 参数, 日志文件, 输出里必须出现的正则, 输出里不许出现的正则)
    runs = [
        # 金币(E-116):每一局的自动机把掉在地上的金币都捡完,至少掉过一堆;"掉出 N 堆,捡起 N 堆" 用反向引用要求两数相同
        # 物品(E-117):自动机把掉在地上的物品也都捡进背包(背包没满;怪物的物品概率很低,可能一件也没掉,所以只要求两个数相同)
        ("选角→城镇→副本→结算→回城", ["flow-check", "--from", "char"], "flow_char.txt",
         [r"金币\(E-116\):各局掉出 ([1-9]\d*) 堆,捡起 \1 堆", r"物品\(E-117\):各局掉出 (\d+) 件,捡起 \1 件"], []),
        ("城镇起(含加载画面与出口)", ["flow-check", "--from", "town"], "flow_town.txt", [], []),
        ("副本选择起", ["flow-check", "--from", "select"], "flow_select.txt", [], []),
        ("副本起", ["flow-check", "--from", "dungeon"], "flow_dungeon.txt",
         [r"金币\(E-116\):各局掉出 ([1-9]\d*) 堆,捡起 \1 堆", r"物品\(E-117\):各局掉出 (\d+) 件,捡起 \1 件"], []),
        ("旧版单图城镇", ["flow-check", "--legacy-town", "--from", "char"], "flow_legacy.txt", [], []),
        # 主角不动站着挨打:一座首领房间里被打死,倒地、躺平不再动(E-105)
        # M22 起怪物要先发现主角、按节拍走近、站着才出招,打死不动的主角比以前慢,时限放宽到 300 秒
        ("首领房间里主角被打死后倒地躺平", ["combat-check", "--map", "map/lorien/b1.map", "--seconds", "300"], "flow_hero_down.txt",
         [r"主角死亡", r"主角倒地 [1-9]\d* 次", r"动作 Down"], []),
        # 站着挨打的主角被一击打倒(没死),躺着、起身回到站立(E-105;这座房间里的怪有倒地类攻击,是扫全库找到的几座之一)
        # M22 起这座房间里七只怪不再同时扑上来:从这个出生点开始,第一次被打倒在 5.9 秒、起身在 9.35 秒、下一次被打在 9.7 秒,
        # 时限停在两次被打之间(怪物 AI 的时序一变,这里要跟着调)
        ("真实房间里主角活着被打倒又起身", ["combat-check", "--map", "map/deadtower/021f.map", "--start", "300,330,left", "--seconds", "9.5"], "flow_hero_getup.txt",
         [r"主角倒地 [1-9]\d* 次\(起身 [1-9]\d* 次", r"动作 Rest", r"生命 [1-9]\d*/"], [r"主角死亡"]),
        # 物品掉落池(E-117):整库扫描装备与可叠加物品的脚本头、建池、按怪物等级抽样;每一项结构检查都要通过(退出码 0),地上图与图标的帧都在图包里
        ("物品掉落池整库扫描", ["item-check", "--level", "30", "--samples", "2000"], "item_check.txt",
         [r"\[通过\] 能掉的物品", r"\[通过\] 地上图的帧都在图包里", r"\[通过\] 图标的帧都在图包里", r"抽样:怪物等级 30"], [r"\[失败\]"]),
        # 死亡与复活(E-115):怪物伤害放大到一击必杀(测试旋钮,不是原版规则);自动机在死亡窗口里按攻击键用币复活,
        # 币给足时继续打完整座副本(首领清空,退出码 0);币给 0 枚时 10 秒窗口走完、副本失败(退出码 2,倒下与失败之间正好一个窗口)
        ("一击必杀下用币复活后打完副本", ["dungeon-check", "--dungeon", "dungeon/act1/lorien.dgn", "--bot", "--monster-damage-scale", "100000", "--coins", "99", "--seconds", "120"],
         "flow_revive.txt", [r"主角倒下\(第 1 次", r"主角复活\(第 1 次", r"首领清空"], [r"副本失败"]),
        ("没有复活币的主角死后窗口走完副本失败", ["dungeon-check", "--dungeon", "dungeon/act1/lorien.dgn", "--bot", "--monster-damage-scale", "100000", "--coins", "0", "--seconds", "120"],
         "flow_fail.txt", [r"主角倒下\(第 1 次", r"副本失败\(复活窗口走完", r"死亡 1 次,复活 0 次"], [r"主角复活"], (2,)),
    ]
    bad = 0
    total = 0.0
    names = []
    for title, args, log, expect, forbid, *rest in runs:
        codes = rest[0] if rest else (0,)           # 可选的第六项:这一项允许的退出码(默认只有 0)
        code, text, sec = sh(DNF + args, log)
        total += sec
        names.append(title)
        if code not in codes or any(re.search(pattern, text) is None for pattern in expect) or any(re.search(pattern, text) is not None for pattern in forbid):
            bad += 1
    return [item("flow", "整条流程无人值守检查(真实数据,自动机从起点打到结算、再回城镇;外加主角被打死倒地、活着被打倒又起身、死亡后用币复活、没有币时副本失败)", "整条流程", "E-093、E-096、E-097、E-105、E-115、E-116、E-117", len(runs), bad, total,
                 "%d 项:" % len(runs) + "、".join(names) + ";前五项退出码 0 = 切换次数、首领清空、音乐序列、读文件失败数全部符合预期(选角起与副本起两项另查金币:至少掉出一堆、掉出的都被捡起,物品掉出的也都被捡进背包);物品池一项查整库物品脚本头与掉落池的结构;后四项另查输出里的倒地与起身、复活与失败(没有币那一项退出码 2 是预期)")]


def check_dungeons():
    """全库副本扫描(M21,E-113):346 个副本定义各跑一局无人值守的探索自动机。只作记录(gate=False):
    进阶祭坛类、战争屋、没有登记房间的定义目前还通不了,通关数是进度指标而不是对错。"""
    code, text, sec = sh(PY + [os.path.join(ROOT, "tools", "py", "dungeon_sweep.py"), "--seconds", "600", "--real-timeout", "180", "--jobs", "8"], "dungeons.txt")
    m = re.search(r"合计 (\d+):cleared (\d+)\s+stuck (\d+)\s+nolayout (\d+)\s+nomaps (\d+)\s+crash (\d+)\s+timeout (\d+)\s+other (\d+)", text)
    if m is None:
        return [item("dungeons", "全库副本扫描", "整条流程", "E-113", 0, 1, sec, "没解析出汇总行,见 out/verify-all/dungeons.txt", gate=False)]
    total, cleared, stuck, nolayout, nomaps, crash, timeout, other = (int(g) for g in m.groups())
    note = "通关 %d/%d;卡住 %d、没有迷宫 %d、没有登记房间 %d、崩溃 %d、超时 %d、其他 %d(进阶祭坛类、战争屋等还没做,见 docs/flow.md §7);明细在 out/dungeon-sweep.tsv,不入库" % (
        cleared, total, stuck, nolayout, nomaps, crash, timeout, other)
    return [item("dungeons", "全库副本扫描:每个副本定义跑一局探索自动机(600 模拟秒上限、关伤害)", "整条流程", "E-113", total, total - cleared, sec, note, gate=False)]


def check_sound():
    code, text, sec = sh(DNF + ["sound-check", "--bot"], "sound.txt")
    return [item("sound", "声音数据与触发(audio.xml、文件可读、脚本引用的标签都能展开、自动机打一座副本)", "整条流程", "E-089、E-090", 1,
                 0 if code == 0 else 1, sec, "没有录音可对照:检查的是数据对得上、触发有发出")]


def check_exp():
    code, text, sec = sh(DNF + ["exp-check"], "exp.txt")
    ok = len(re.findall(r"^\s*\[通过\]", text, re.M))
    bad = len(re.findall(r"^\s*\[失败\]", text, re.M))
    if ok + bad == 0:
        return [item("exp", "经验与升级", "全库读通", "E-111", 0, 1, sec, "没解析出检查项,见 out/verify-all/exp.txt")]
    return [item("exp", "经验与升级(升级经验表、技能点表、三张倍率表读通;成长块与各级属性不减;一级的 FromStats 等于 FromChr;1 到 85 级升级循环与满级封顶;击杀经验;全库 .dgn 的经验权重)",
                 "全库读通", "E-111", ok + bad, bad + (0 if code == 0 else 1), sec,
                 "只含个数;原版数值只在本机 out/verify-all/exp.txt 里,不入库;基础经验表缺 monster_reward_ref 导出时用占位表(输出里有标注)")]


def check_oracle():
    if not os.path.exists(VENV_PY):
        return [item("oracle", "怪物数值 vs 原版机器码(模拟器)", "对拍:原版二进制", "E-063", 0, 1, 0, "缺 re/venv(Unicorn)")]
    code, text, sec = sh([VENV_PY, "tools/py/mob_ability_oracle.py", "--stress", "20000"], "oracle.txt")
    # 输出里有两行:"全库对拍:一致 N,不一致 M"、"随机压力对拍:一致 K,不一致 J"
    cases = bad = 0
    for m in re.finditer(r"一致 (\d+),不一致 (\d+)", text):
        cases += int(m.group(1)) + int(m.group(2))
        bad += int(m.group(2))
    if cases == 0:
        return [item("oracle", "怪物数值 vs 原版机器码(模拟器)", "对拍:原版二进制", "E-063", 0, 1, sec, "输出格式没解析出个数,见 out/verify-all/oracle.txt")]
    return [item("oracle", "怪物数值 vs 原版机器码(服务端 ELF 在 Unicorn 里跑原函数,逐槽比较)", "对拍:原版二进制", "E-063", cases, bad + (0 if code == 0 else 1), sec,
                 "全库用例加 2 万个随机压力用例;原版函数只在模拟器里跑,不启动服务、不联网")]


CHECKS = [
    ("unit", check_unit), ("p1", check_p1), ("atk", check_atk), ("act", check_act), ("mob", check_mob),
    ("damage", check_damage), ("sq", check_sq), ("sweep", check_sweep), ("flow", check_flow), ("dungeons", check_dungeons), ("sound", check_sound),
    ("exp", check_exp), ("oracle", check_oracle),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--skip", default="")
    ap.add_argument("--out", default=os.path.join(ROOT, "out", "verify-all.json"))
    args = ap.parse_args()
    only = set(x for x in args.only.split(",") if x)
    skip = set(x for x in args.skip.split(",") if x)

    results = []
    for key, fn in CHECKS:
        if only and key not in only:
            continue
        if key in skip:
            continue
        print("== %s ..." % key, flush=True)
        t0 = time.time()
        try:
            rows = fn()
        except Exception as ex:  # 一项挂掉不影响其他项
            rows = [item(key, key, "出错", "", 0, 1, time.time() - t0, "异常:%r" % (ex,))]
        for r in rows:
            rate = "%.4f%%" % (100.0 * (r["cases"] - r["bad"]) / r["cases"]) if r["cases"] else "-"
            print("   %-8s 用例 %-10d 不一致/失败 %-6d 通过率 %-10s %5.1fs  %s" % (r["key"], r["cases"], r["bad"], rate, r["seconds"], r["title"]), flush=True)
        results.extend(rows)

    report = {"generated_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "results": results,
              "all_passed": all(r["bad"] == 0 and r["cases"] > 0 for r in results if r.get("gate", True))}
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=1)
    print("报告:%s;%s" % (args.out, "全部通过" if report["all_passed"] else "有不一致或失败"))
    return 0 if report["all_passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
