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
名字:unit p1 atk act mob damage sq sweep flow dungeons actrun sound exp text oracle difficulty breakable skill status(oracle 与 difficulty 的对拍部分需要 re/venv 里的 Unicorn 与本机的服务端二进制;dungeons 是全库副本扫描,非门槛项)
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
        # M22 起怪物要先发现主角、按节拍走近、站着才出招,打死不动的主角比以前慢;
        # M54 起敌对满 10 秒回游荡、目标一直在视野里就不再自动接战,站着不动的主角挨打更少,默认种子要六分钟上下才死,时限放宽到 900 秒
        ("首领房间里主角被打死后倒地躺平", ["combat-check", "--map", "map/lorien/b1.map", "--seconds", "900"], "flow_hero_down.txt",
         [r"主角死亡", r"主角倒地 [1-9]\d* 次", r"动作 Down"], []),
        # 站着挨打的主角被一击打倒(没死),躺着、起身回到站立(E-105;这座房间里的怪有倒地类攻击,是扫全库找到的几座之一)
        # M54 起(绕背冲刺、边走边出招、被挡住后反向)怪物到得更早、更密:换了出生点(400,330,左),
        # 第一次被打倒后在下一次挨打之前起身站定,时限停在这段站定的中间(窗口宽约一秒半;怪物 AI 的时序一变,这里要跟着调)
        ("真实房间里主角活着被打倒又起身", ["combat-check", "--map", "map/deadtower/021f.map", "--start", "400,330,left", "--seconds", "5.6"], "flow_hero_getup.txt",
         [r"主角倒地 [1-9]\d* 次\(起身 [1-9]\d* 次", r"动作 Rest", r"生命 [1-9]\d*/"], [r"主角死亡"]),
        # 物品掉落池(E-117):整库扫描装备与可叠加物品的脚本头、建池、按怪物等级抽样;每一项结构检查都要通过(退出码 0),地上图与图标的帧都在图包里
        ("物品掉落池整库扫描", ["item-check", "--level", "30", "--samples", "2000"], "item_check.txt",
         [r"\[通过\] 能掉的物品", r"\[通过\] 地上图的帧都在图包里", r"\[通过\] 图标的帧都在图包里", r"抽样:怪物等级 30"], [r"\[失败\]"]),
        # 装备栏与穿脱(E-118):整库扫描能掉落的装备,类型、可用职业与穿戴规则的结构,剑魂能穿的武器外观与攻击动画,穿戴演习的账目;每一项结构检查都要通过(退出码 0)
        ("装备栏与穿脱整库扫描", ["equip-check", "--level", "30"], "equip_check.txt",
         [r"\[通过\] 剑魂有能穿的武器", r"\[通过\] 有能在画面上穿出来的武器", r"\[通过\] HeroLook\.CanShow 与逐动作检查一致", r"\[通过\] 起手武器的外观", r"\[通过\] 面板装备加成等于", r"\[通过\] 存档文本能读回来", r"\[通过\] 还原之后再拍的快照", r"\[通过\] 全部脱下"], [r"\[失败\]"]),
        # NPC 商店(E-119):整库读 itemshop.lst 与每家店的脚本、NPC 角色里的商店编号、城镇里摆着的商店 NPC(走到可走区域边缘总够得着)、价格倍率表;
        # 每家店的每件商品演习买一件卖回去(一次买三件卖三件),核对钱与背包的账;窗口光标与滚动、无人值守探针、画面;每一项结构检查都要通过(退出码 0)
        ("NPC 商店整库扫描与买卖演习", ["shop-check", "--level", "30"], "shop_check.txt",
         [r"\[通过\] 列在 equipment\.lst 与 stackable\.lst 里的商品都读得出物品脚本", r"\[通过\] 价格倍率表读得出来", r"\[通过\] 角色里写的商店不在 itemshop\.lst 里的 NPC",
          r"\[通过\] 可走区域里离商店 NPC 最近的点", r"\[通过\] 钱与背包的账都对得上", r"\[通过\] 买入页的光标一路下移", r"\[通过\] 无人值守探针在每家有可买商品的店都买成",
          r"\[通过\] 商店画面不是空的"], [r"\[失败\]"]),
        # 消耗品(E-120):整库读可叠加物品脚本里的 [hp recovery]/[mp recovery]/[cool time]/[cooltime group],字段结构、图标帧;
        # 85 级剑魂对每一种用得了的回复药演习(进快捷栏、用一件、每 10 毫秒推进到发完、核对终值与背包的账、冷却登记与拒绝、冷却走完再用),快捷栏放满六种做存档往返;每一项都要通过(退出码 0)
        ("消耗品整库扫描与使用演习", ["consumable-check", "--level", "30"], "consumable_check.txt",
         [r"\[通过\] 写了 \[hp recovery\] 或 \[mp recovery\] 的物品", r"\[通过\] 持续时间都不为负", r"\[通过\] 冷却时间不为负", r"\[通过\] 图标的帧都在图包里",
          r"\[通过\] 写了回复的物品不会被判成", r"\[通过\] 逐种演习", r"\[通过\] 快捷栏放了", r"\[通过\] 还原之后再拍的快照与原来逐字节相同"], [r"\[失败\]"]),
        # 任务(E-123):整库读 quest.lst 与每个任务脚本(类型都认得、奖励参数表、支持范围、引用的 NPC 与地图与怪物都在列表里、每个任务的生命周期演习);
        # 再自动选副本,把真正的任务系统接到副本会话上无人值守打(两遍同一局:第一遍记账、第二遍接着任务打,猎杀不够数就多打几局),核对计数、达成、奖励、提示条与追踪;每一项都要通过(退出码 0)
        ("任务整库扫描与生命周期演习", ["quest-check"], "quest_check.txt",
         [r"\[通过\] 每个任务脚本都读得出来", r"\[通过\] 支持的过房间任务的地图都在 map\.lst 里", r"\[通过\] 支持的猎杀任务的怪物都在 monster\.lst 里", r"\[通过\] 每个任务的生命周期演习都对"], [r"\[失败\]"]),
        ("任务在副本里的一整圈", ["quest-flow-check", "--dungeon", "auto"], "quest_flow.txt",
         [r"覆盖的任务种类:猎杀 有、通关 有", r"\[通过\] 自动机把副本打通了", r"任务一整圈走通"], [r"\[失败\]"]),
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
    return [item("flow", "整条流程无人值守检查(真实数据,自动机从起点打到结算、再回城镇;外加主角被打死倒地、活着被打倒又起身、死亡后用币复活、没有币时副本失败;外加整库装备栏、NPC 商店的买卖演习与消耗品的使用演习;外加整库任务与副本里的任务一整圈)", "整条流程", "E-093、E-096、E-097、E-105、E-115、E-116、E-117、E-118、E-119、E-120、E-123", len(runs), bad, total,
                 "%d 项:" % len(runs) + "、".join(names) + ";前五项退出码 0 = 切换次数、首领清空、音乐序列、读文件失败数全部符合预期(选角起与副本起两项另查金币:至少掉出一堆、掉出的都被捡起,物品掉出的也都被捡进背包);物品池一项查整库物品脚本头与掉落池的结构;装备栏与 NPC 商店两项查整库的结构与买卖穿戴演习的账目(商店脚本里有些商品编号两张物品列表里都没有,是数据里悬空的,只记观察不算失败);消耗品一项查整库回复类物品的字段结构与 85 级剑魂逐种使用演习的账目(终值、背包、冷却、存档往返);任务两项查整库任务脚本的结构与生命周期演习、副本里的计数与达成与奖励与提示条的一整圈(任务的编号、怪物与数量只在本机输出里,不入库);后四项另查输出里的倒地与起身、复活与失败(没有币那一项退出码 2 是预期)")]


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
    a = re.search(r"脚本\(只在本机,E-130\):有出招带触发器的怪物的副本 (\d+) 个,其中放出过被动对象的 (\d+) 个", text)
    if a is not None:
        note += ";.act 脚本(E-130):有出招带触发器的怪物的副本 %s 个,其中放出过被动对象的 %s 个" % (a.group(1), a.group(2))
    return [item("dungeons", "全库副本扫描:每个副本定义跑一局探索自动机(600 模拟秒上限、关伤害)", "整条流程", "E-113", total, total - cleared, sec, note, gate=False)]


def check_actrun():
    """.act 触发器运行期(M36-4,E-130):整库结构检查(act-run-check 的每一项 [通过]:引用读得到、没有读不通的 [CREATE PASSIVEOBJECT]、认得的触发器占比、被动对象的 .obj 与模板配得出),
    加一项实机:从全库副本扫描的明细里挑放出过被动对象的副本,开伤害各跑一局自动机,至少一局里脚本放出了被动对象、并且打中了主角。"""
    code, text, sec = sh(DNF + ["act-run-check"], "actrun.txt")
    ok = len(re.findall(r"^\s*\[通过\]", text, re.M))
    bad = len(re.findall(r"^\s*\[失败\]", text, re.M))
    if ok + bad == 0:
        return [item("actrun", ".act 触发器运行期", "全库读通", "E-130", 0, 1, sec, "没解析出检查项,见 out/verify-all/actrun.txt")]
    total = sec
    sweep_json = os.path.join(ROOT, "out", "dungeon-sweep.json")

    def load():
        try:
            with open(sweep_json, encoding="utf-8") as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return []

    results = load()
    if not any("passives" in r for r in results):
        # 没有带脚本统计的扫描明细(没跑过 dungeons 项,或明细是旧的):先扫一遍,只为挑副本
        c2, t2, s2 = sh(PY + [os.path.join(ROOT, "tools", "py", "dungeon_sweep.py"), "--seconds", "600", "--real-timeout", "180", "--jobs", "8"], "actrun_sweep.txt")
        total += s2
        results = load()
    cands = [r for r in results if r.get("status") == "cleared" and 0 < r.get("passives", 0) < 3000]
    cands.sort(key=lambda r: (-r["passives"], r["dungeon"]))
    live = "没有放出过被动对象的副本可选"
    live_ok = False
    for n, r in enumerate(cands[:6], 1):
        c3, t3, s3 = sh(DNF + ["dungeon-check", "--dungeon", r["dungeon"], "--bot", "--explore", "--seed", "1", "--seconds", "400", "--damage-scale", "5000"], "actrun_live_%d.txt" % n)
        total += s3
        m = re.search(r"脚本\(E-130\):进过的房间里出招带 \.act 触发器的怪物 (\d+) 只,脚本放出被动对象 (\d+) 个,打中主角 (\d+) 次", t3)
        live = "试了 %d 个副本" % n
        if m is not None and int(m.group(2)) > 0 and int(m.group(3)) > 0:
            live_ok = True
            live = "第 %d 个副本里脚本放出被动对象 %s 个、打中主角 %s 次" % (n, m.group(2), m.group(3))
            break
    return [item("actrun", ".act 触发器运行期(整库引用与触发器认得的占比、被动对象模板配得出、实机里脚本放出被动对象并打中主角)", "全库读通+整条流程", "E-130", ok + bad + 1,
                 bad + (0 if code == 0 else 1) + (0 if live_ok else 1), total,
                 "结构检查 %d 项;实机:%s;副本名与数值只在本机 out/verify-all/ 下,不入库" % (ok + bad, live))]


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
    return [item("exp", "经验与升级(升级经验表、技能点表、三张倍率表读通;成长块与各级属性不减;一级的 FromStats 等于 FromChr;1 到 85 级升级循环与满级封顶;击杀经验;全库 .dgn 的经验权重;"
                 "副本评分系统与评分加成比例读通、门槛与用时档有序、全库副本的标准等级读得到、通关经验的算术)",
                 "全库读通", "E-111、E-124", ok + bad, bad + (0 if code == 0 else 1), sec,
                 "只含个数;原版数值只在本机 out/verify-all/exp.txt 里,不入库;基础经验表缺 monster_reward_ref 导出时用占位表(输出里有标注)")]


def check_text():
    """中文文字表(E-121):Python 参照读每张 *.kor.str 得到指纹,C# 的 text-check 另读一遍(并与 .NET 自带的 GB18030 解码逐条比对、查物品与商店页的文字键),指纹要逐张相同。"""
    c1, ref_text, s1 = sh(PY + ["tools/py/str_ref.py"], "text_ref.txt")
    c2, cs_text, s2 = sh(DNF + ["text-check"], "text_cs.txt")
    ref = dict(re.findall(r"^(\S+) sha256=([0-9a-f]{64})", ref_text, re.M))
    cs = dict(re.findall(r"^\s+(\S+) keys=\d+ bytes=\d+ fffd=\d+ mismatch=\d+ absent=\d+ sha256=([0-9a-f]{64})", cs_text, re.M))
    ok = len(re.findall(r"^\s*\[通过\]", cs_text, re.M))
    failed = len(re.findall(r"^\s*\[失败\]", cs_text, re.M))
    if c1 != 0 or not ref or not cs:
        return [item("text", "中文文字表 vs Python 参照", "对拍:独立参照", "E-121", 0, 1, s1 + s2, "没跑成,见 out/verify-all/text_ref.txt 与 text_cs.txt")]
    diff = sum(1 for k in set(ref) | set(cs) if ref.get(k) != cs.get(k))
    return [item("text", "中文文字表(GB18030 解码的指纹与 Python 参照逐张相同;与 .NET 的 GB18030 逐条一致;物品与商店页的文字键都查得到)", "对拍:独立参照", "E-121",
                 len(set(ref) | set(cs)) + ok + failed, diff + failed + (0 if c2 == 0 else 1), s1 + s2,
                 "只含个数与是否一致;文字与条数在 out/verify-all/text_cs.txt,是用户自己的数据,不入库")]


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


def check_difficulty():
    """副本难度(E-125):真数据的结构检查,加上"服务端机器码 vs C# 重算"的压力对拍。"""
    rows = []
    code, text, sec = sh(DNF + ["difficulty-check"], "difficulty.txt")
    ok = len(re.findall(r"^\s*\[通过\]", text, re.M))
    bad = len(re.findall(r"^\s*\[失败\]", text, re.M))
    if ok + bad == 0:
        rows.append(item("difficulty", "副本难度的数据结构", "全库读通", "E-125", 0, 1, sec, "没解析出检查项,见 out/verify-all/difficulty.txt"))
    else:
        rows.append(item("difficulty", "副本难度的数据结构(难度加成表五档都有按等级的行、全库副本自带的加成格式对、难度 3 清单里的编号都在 dungeon.lst、"
                         "同一座副本各难度下每只怪物的最大生命只升不降)", "全库读通", "E-125", ok + bad, bad + (0 if code == 0 else 1), sec,
                         "只含个数;原版数值只在本机 out/verify-all/difficulty.txt 里,不入库"))
    if not os.path.exists(VENV_PY):
        rows.append(item("difficulty-oracle", "副本难度加成 vs 原版机器码(模拟器)", "对拍:原版二进制", "E-125", 0, 1, 0, "缺 re/venv(Unicorn)"))
        return rows
    cases_file = os.path.join(WORK, "difficulty_cases.txt")
    os.makedirs(WORK, exist_ok=True)
    c1, t1, s1 = sh([VENV_PY, "tools/py/mob_difficulty_oracle.py", "--stress", "20000", "--write-cases", cases_file], "difficulty_oracle.txt")
    m1 = re.search(r"随机压力对拍:一致 (\d+),不一致 (\d+)", t1)
    c2, t2, s2 = sh(DNF + ["difficulty-check", "--cases", cases_file], "difficulty_cs.txt")
    m2 = re.search(r"一致 (\d+),不一致 (\d+)", t2)
    if not m1 or not m2:
        rows.append(item("difficulty-oracle", "副本难度加成 vs 原版机器码(模拟器)", "对拍:原版二进制", "E-125", 0, 1, s1 + s2,
                         "输出格式没解析出个数,见 out/verify-all/difficulty_oracle.txt 与 difficulty_cs.txt"))
        return rows
    cases = int(m1.group(1)) + int(m1.group(2))
    diff = int(m1.group(2)) + int(m2.group(2)) + (0 if c1 == 0 and c2 == 0 else 1)
    rows.append(item("difficulty-oracle", "副本难度加成(服务端 ELF 里 upgradeAbilityByDifficultyOfDungeon 的三个重载在 Unicorn 里跑原函数 = Python 参考 = C# 重算,逐槽比较)",
                     "对拍:原版二进制", "E-125", cases, diff, s1 + s2,
                     "2 万个合成用例(合成的系数表与等级行、随机的副本自带加成);原版函数只在模拟器里跑,不启动服务、不联网;C# 另有 420 条入库金标准单测"))
    return rows


def check_breakable():
    """可破坏物件与物件掉物品(E-126):真数据的结构检查(物件掉落表读得到、[special passive object] 文法、[item] 动作的掉落项下标),
    加上实机房间里把物件一个个打碎、触发 [item] 动作掉出物品。"""
    code, text, sec = sh(DNF + ["breakable-check"], "breakable.txt")
    ok = len(re.findall(r"^\s*\[通过\]", text, re.M))
    bad = len(re.findall(r"^\s*\[失败\]", text, re.M))
    if ok + bad == 0:
        return [item("breakable", "可破坏物件与物件掉物品", "全库读通", "E-126", 0, 1, sec, "没解析出检查项,见 out/verify-all/breakable.txt")]
    return [item("breakable", "可破坏物件与物件掉物品(物件掉落表读得到并建出池、全部地图的 [special passive object] 文法与 RoomMap 一致、每个 [item] 动作的掉落项下标在所属副本范围内、"
                 "真数据按 [item] 动作能掷出物品、实机房间里的物件能被打中、打碎或触发并掉出物品)", "全库读通", "E-126", ok + bad, bad + (0 if code == 0 else 1), sec,
                 "只含个数;原版数值只在本机 out/verify-all/breakable.txt 里,不入库")]


def check_skill():
    """技能学习(E-127):真数据的结构检查(技能列表与目录对得上、花费补齐后每个技能到各成长类型的最高级都有花费、前置技能都在目录里且无环、六个成长类型的白送技能、
    每个成长类型学满/退光/重置的点数守恒、本地主角的技能点与觉醒点是该级总额、存档往返不变、自动机策略把点数花光且守恒;
    M37 起加一项:技能配方表里每个技能在真数据里读得通,E-129;
    M47 起加三项:整个职业目录的出招指令解析与输入层认得出、已实现技能打自己的指令放出自己、躺着的主角打出倒地起身的指令起身,E-140;
    M48 起加一项:增益类技能表里每个技能读得通、真实会话里按指令放一遍并挂上到点失效,E-141;M49 起这一项还查概率触发类增益的概率不超过 100%,E-142)。"""
    code, text, sec = sh(DNF + ["skill-check"], "skill.txt")
    ok = len(re.findall(r"^\s*\[通过\]", text, re.M))
    bad = len(re.findall(r"^\s*\[失败\]", text, re.M))
    if ok + bad == 0:
        return [item("skill", "技能学习", "全库读通", "E-127", 0, 1, sec, "没解析出检查项,见 out/verify-all/skill.txt")]
    return [item("skill", "技能学习与技能配方(技能目录读通、花费补齐、前置技能无环、六个成长类型的白送技能、学满/退光/重置的点数守恒、存档往返、自动机加点策略、"
                 "配方表里每个技能的动作/攻击信息/特效/静态数据/等级列都读得通、出招指令的解析与输入层、按指令放技能、倒地起身、增益类技能)", "全库读通", "E-127/E-129/E-140/E-141/E-142", ok + bad,
                 bad + (0 if code == 0 else 1), sec, "只含个数;技能数据与数值只在本机 out/verify-all/skill.txt 里,不入库")]


def check_status():
    """异常状态(E-136):真数据的结构检查与运行期模拟(数值表读得到、公式自洽、怪物 .atk 的状态槽都挂得上主角、持续伤害每跳不超过服务端允许值且总量不超过伤害量、
    时间步长不影响结果、状态到点解除),加上"服务端机器码 vs C# 重算"的压力对拍(共享公式库在 Unicorn 里跑原函数)。"""
    rows = []
    code, text, sec = sh(DNF + ["status-check"], "status.txt")
    ok = len(re.findall(r"^\s*\[通过\]", text, re.M))
    bad = len(re.findall(r"^\s*\[失败\]", text, re.M))
    if ok + bad == 0:
        rows.append(item("status", "异常状态的数据结构与运行期模拟", "全库读通", "E-136", 0, 1, sec, "没解析出检查项,见 out/verify-all/status.txt"))
    else:
        rows.append(item("status", "异常状态的数据结构与运行期模拟(数值表读得到、持续时间公式不出负数、同步种类对得上、怪物 .atk 状态槽的附加参数个数、"
                         "每个槽挂到主角身上走完时间:每跳不超过服务端允许值、总量不超过伤害量、步长无关、状态到点摘除、减速百分数在范围内;等级差修正:同级原样、受害者越高概率与时长越不大、结果不为负)", "全库读通", "E-136",
                         ok + bad, bad + (0 if code == 0 else 1), sec, "只含个数;原版数值只在本机 out/verify-all/status.txt 里,不入库"))
    if not os.path.exists(VENV_PY):
        rows.append(item("status-oracle", "异常状态公式 vs 原版机器码(模拟器)", "对拍:原版二进制", "E-136", 0, 1, 0, "缺 re/venv(Unicorn)"))
        return rows
    cases_file = os.path.join(WORK, "status_cases.txt")
    os.makedirs(WORK, exist_ok=True)
    c1, t1, s1 = sh([VENV_PY, "tools/py/active_status_oracle.py", "--stress", "20000", "--write-cases", cases_file], "status_oracle.txt")
    m1 = re.search(r"压力用例 (\d+) 条", t1)
    c2, t2, s2 = sh(DNF + ["status-check", "--cases", cases_file], "status_cs.txt")
    m2 = re.search(r"一致 (\d+),不一致 (\d+)", t2)
    if not m1 or not m2:
        rows.append(item("status-oracle", "异常状态公式 vs 原版机器码(模拟器)", "对拍:原版二进制", "E-136", 0, 1, s1 + s2,
                         "输出格式没解析出个数,见 out/verify-all/status_oracle.txt 与 status_cs.txt"))
        return rows
    cases = int(m2.group(1)) + int(m2.group(2))
    diff = int(m2.group(2)) + (0 if c1 == 0 and c2 == 0 and cases == int(m1.group(1)) else 1)
    rows.append(item("status-oracle", "异常状态公式(服务端 ELF 里 SDCActiveStatusSyncCommon 的等级插值、威力、附加参数、持续时间、毒与灼伤与出血的伤害、同步种类,"
                     "在 Unicorn 里跑原函数 = C# 重算,逐条比较)", "对拍:原版二进制", "E-136", cases, diff, s1 + s2,
                     "2 万个合成用例(合成的数值表与参数,x87 扩展精度逐位);原版函数只在模拟器里跑,不启动服务、不联网;C# 另有入库金标准单测"))
    return rows


CHECKS = [
    ("unit", check_unit), ("p1", check_p1), ("atk", check_atk), ("act", check_act), ("mob", check_mob),
    ("damage", check_damage), ("sq", check_sq), ("sweep", check_sweep), ("flow", check_flow), ("dungeons", check_dungeons), ("actrun", check_actrun), ("sound", check_sound),
    ("exp", check_exp), ("text", check_text), ("oracle", check_oracle), ("difficulty", check_difficulty), ("breakable", check_breakable), ("skill", check_skill), ("status", check_status),
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
