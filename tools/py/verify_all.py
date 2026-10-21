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
名字:unit p1 atk act mob damage sq sweep flow sound oracle(oracle 需要 re/venv 里的 Unicorn 与本机的服务端二进制)
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
        ("选角→城镇→副本→结算→回城", ["flow-check", "--from", "char"], "flow_char.txt", [], []),
        ("城镇起(含加载画面与出口)", ["flow-check", "--from", "town"], "flow_town.txt", [], []),
        ("副本选择起", ["flow-check", "--from", "select"], "flow_select.txt", [], []),
        ("副本起", ["flow-check", "--from", "dungeon"], "flow_dungeon.txt", [], []),
        ("旧版单图城镇", ["flow-check", "--legacy-town", "--from", "char"], "flow_legacy.txt", [], []),
        # 主角不动站着挨打:一座首领房间里被打死,倒地、躺平不再动(E-105)
        ("首领房间里主角被打死后倒地躺平", ["combat-check", "--map", "map/lorien/b1.map", "--seconds", "120"], "flow_hero_down.txt",
         [r"主角死亡", r"主角倒地 [1-9]\d* 次", r"动作 Down"], []),
        # 站着挨打的主角被一击打倒(没死),躺着、起身回到站立(E-105;这座房间里的怪有倒地类攻击,是扫全库找到的几座之一)
        ("真实房间里主角活着被打倒又起身", ["combat-check", "--map", "map/deadtower/021f.map", "--seconds", "30"], "flow_hero_getup.txt",
         [r"主角倒地 [1-9]\d* 次\(起身 [1-9]\d* 次", r"动作 Rest", r"生命 [1-9]\d*/"], [r"主角死亡"]),
    ]
    bad = 0
    total = 0.0
    names = []
    for title, args, log, expect, forbid in runs:
        code, text, sec = sh(DNF + args, log)
        total += sec
        names.append(title)
        if code != 0 or any(re.search(pattern, text) is None for pattern in expect) or any(re.search(pattern, text) is not None for pattern in forbid):
            bad += 1
    return [item("flow", "整条流程无人值守检查(真实数据,自动机从起点打到结算、再回城镇;外加主角被打死倒地、活着被打倒又起身)", "整条流程", "E-093、E-096、E-097、E-105", len(runs), bad, total,
                 "七项:" + "、".join(names) + ";退出码 0 = 切换次数、首领清空、音乐序列、读文件失败数全部符合预期;后两项另查输出里的倒地与起身")]


def check_sound():
    code, text, sec = sh(DNF + ["sound-check", "--bot"], "sound.txt")
    return [item("sound", "声音数据与触发(audio.xml、文件可读、脚本引用的标签都能展开、自动机打一座副本)", "整条流程", "E-089、E-090", 1,
                 0 if code == 0 else 1, sec, "没有录音可对照:检查的是数据对得上、触发有发出")]


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
    ("damage", check_damage), ("sq", check_sq), ("sweep", check_sweep), ("flow", check_flow), ("sound", check_sound),
    ("oracle", check_oracle),
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
