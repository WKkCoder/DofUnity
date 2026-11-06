#!/usr/bin/env python3
"""被动威力加成接口的真实脚本普查(本机脚本扫描,非 REA;只输出汇总计数,不输出脚本名、技能名、技能号)。

统计对象(PVF 里全部明文 .nut):
  登记  sq_AddPassiveSkillAttackBonusRate / sq_addPassiveSkillAttackBonusRate(客户端两个类各一套,大小写不同)
  删除  sq_RemovePassiveSkillAttackBonusRate / sq_removePassiveSkillAttackBonusRate
  读取  sq_GetPassiveAttackRate / sq_getPassiveAttackRate
  消费  sq_GetBonusRateWithPassive / sq_GetPowerWithPassive / sq_SetAttackPowerWithPassive(及小写首字母写法)
  通知  sq_SendChangeSkillEffectPacket
对每个调用点:所在的脚本函数、函数名类别(只取引擎回调的前缀类别,不带脚本/技能后缀)、所在职业目录对应的职业编号;
登记的技能(第一个实参)按常量名解析成技能号,取该技能 .skl 的 [executable states],
再统计"状态实参可解析为常量且落在该状态集合里"的消费调用点数(客户端按状态过滤,见 0x00684510 → 0x0090dd10)。

用法:python3 tools/py/passive_power_census.py
需要 DNF_DIR 或 local.env(见 dnfpaths.py);输出只有计数。
"""
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dnfdata as D  # noqa: E402
import dnfscripts_lite as L  # noqa: E402
from sq_inventory import strip_noise  # noqa: E402

# 职业目录 → 职业编号(character/character.lst 里 .chr 的文件名;只用于分组,输出里只出现编号)
JOB_DIRS = {
    "swordman": 0, "fighter": 1, "gunner": 2, "mage": 3, "priest": 4, "atgunner": 5, "thief": 6,
    "atfighter": 7, "atmage": 8, "demonicswordman": 9, "creatormage": 10,
}

ADD = ("sq_addpassiveskillattackbonusrate",)
REMOVE = ("sq_removepassiveskillattackbonusrate",)
READ = ("sq_getpassiveattackrate",)
# 消费者:名字(小写)→ (成员写法里状态实参的下标)
CONSUME_STATE_INDEX = {
    "sq_getbonusratewithpassive": 1,
    "sq_getpowerwithpassive": 1,
    "sq_setattackpowerwithpassive": 1,
}
SEND = ("sq_sendchangeskilleffectpacket",)

CALL = re.compile(r"\b(sq_[A-Za-z]*(?:PassiveSkillAttackBonusRate|PassiveAttackRate|WithPassive)|sq_SendChangeSkillEffectPacket)\s*\(")
FUNC = re.compile(r"\bfunction\s+([A-Za-z_]\w*)\s*\(")
CONST = re.compile(r"\b([A-Za-z_]\w*)\s*(?:<-|=)\s*(-?\d+)\s*[;,\n]")


def split_args(text, start):
    """从 start(紧跟左括号之后)起,按深度 0 的逗号切分实参,返回 (实参列表, 右括号位置)。"""
    depth = 0
    args, cur = [], []
    i = start
    while i < len(text):
        c = text[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                args.append("".join(cur).strip())
                return args, i
            depth -= 1
        if c == "," and depth == 0:
            args.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    return args, len(text)


def function_spans(text):
    spans = []
    for m in FUNC.finditer(text):
        j = text.find("{", m.end())
        if j < 0:
            continue
        depth, k = 0, j
        while k < len(text):
            if text[k] == "{":
                depth += 1
            elif text[k] == "}":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        spans.append((j, k, m.group(1)))
    return spans


def callback_class(name):
    """函数名 → 回调类别:只留引擎回调的前缀(onStart_appendage_xxx → onStart_appendage;ProcPassiveSkill_xxx → ProcPassiveSkill*);
    不是引擎回调的脚本自定义函数统一记成"脚本自定义函数",避免把技能相关的名字带进输出。"""
    if name is None:
        return "(函数外)"
    low = name.lower()
    if low.startswith("procpassiveskill"):
        return "ProcPassiveSkill*"
    engine_prefix = re.match(r"^(on[A-Z][A-Za-z]*|proc|prepareDraw|isEnd|isAppend|drawAppend)(?:_|$)", name)
    if not engine_prefix:
        return "脚本自定义函数"
    tag = "_appendage" if "_appendage" in low else ("_po" if "_po_" in low else "")
    return engine_prefix.group(1) + tag


def job_of(path):
    m = re.match(r"sqr/character/([a-z_]+)/", path)
    if m and m.group(1) in JOB_DIRS:
        return JOB_DIRS[m.group(1)]
    return None


def main():
    pvf = D.Pvf()
    cleaned = {}
    for e in pvf.entries:
        lp = e.path.lower()
        if lp.endswith(".nut"):
            cleaned[lp] = strip_noise(pvf.read(e)).decode("latin1")
    consts = defaultdict(dict)  # 常量名 → {路径: 值}
    for path, text in cleaned.items():
        for m in CONST.finditer(text):
            consts[m.group(1)][path] = int(m.group(2))

    def resolve(name, job):
        if re.fullmatch(r"-?\d+", name):
            return int(name)
        vals = consts.get(name)
        if not vals:
            return None
        pref = [v for p_, v in vals.items() if job_of(p_) == job or job_of(p_) is None]
        pool = pref or list(vals.values())
        return pool[0] if len(set(pool)) == 1 else None

    calls = []  # (类别, 小写名, 路径, 函数名, 实参, 是否成员写法)
    for path, text in cleaned.items():
        spans = function_spans(text)
        for m in CALL.finditer(text):
            args, _ = split_args(text, m.end())
            member = m.start() > 0 and text[m.start() - 1] == "."
            fn = None
            for a, b, nm in spans:
                if a <= m.start() <= b:
                    fn = nm
            calls.append((m.group(1), path, fn, args, member))

    def group(names):
        return [c for c in calls if c[0].lower() in names]

    print("PVF 里的 .nut 文件数:%d" % len(cleaned))
    for title, names in (("登记", ADD), ("删除", REMOVE), ("读取", READ), ("通知(sq_SendChangeSkillEffectPacket)", SEND)):
        g = group(names)
        by_job = Counter(job_of(c[1]) for c in g)
        classes = Counter(callback_class(c[2]) for c in g)
        print("%s:调用点 %d,脚本 %d;按职业编号 %s;按函数类别 %s" % (
            title, len(g), len({c[1] for c in g}), dict(sorted(by_job.items(), key=lambda kv: str(kv[0]))), dict(classes)))
    cons = [c for c in calls if c[0].lower() in CONSUME_STATE_INDEX]
    print("消费者(取被动倍率的原生接口):调用点 %d,脚本 %d;按职业编号 %s" % (
        len(cons), len({c[1] for c in cons}), dict(sorted(Counter(job_of(c[1]) for c in cons).items(), key=lambda kv: str(kv[0])))))

    # 登记里"登记在 onChangeSkillEffect_* 回调里"的数量(旧实现的假设)
    adds = group(ADD) + group(REMOVE)
    in_change = sum(1 for c in adds if c[2] and c[2].lower().startswith("onchangeskilleffect"))
    print("登记/删除调用点中位于 onChangeSkillEffect_* 回调内的:%d / %d" % (in_change, len(adds)))
    job0_scripts = len({c[1] for c in adds if job_of(c[1]) == 0})
    print("登记/删除脚本中属于职业 0 的:%d / %d" % (job0_scripts, len({c[1] for c in adds})))

    # 登记键(第一个实参)→ 技能号 → .skl 的 executable states
    skill_lists = {}
    rows = L.lines(pvf, "skill/skilllist.lst")
    vals = [v for _, vs in rows for v in vs]
    for i in range(0, len(vals) - 1, 2):
        skill_lists[int(vals[i])] = vals[i + 1].strip('"')

    def skl_states(job, sid):
        lst = skill_lists.get(job)
        if not lst:
            return None, None
        items = [v for _, vs in L.lines(pvf, "skill/" + lst) for v in vs]
        for i in range(0, len(items) - 1, 2):
            if items[i].strip() == str(sid):
                path = "skill/" + items[i + 1].strip('"')
                typ, states = None, None
                for tag, vs in L.lines(pvf, path):
                    if tag.lower() == "[type]":
                        typ = vs[0].strip('"') if vs else None
                    if tag.lower() == "[executable states]":
                        states = [int(x) for v in vs for x in v.replace("\t", " ").split() if x.lstrip("-").isdigit()]
                return typ, states
        return None, None

    keys = {}
    for c in group(ADD) + group(REMOVE):
        job = job_of(c[1])
        if c[3]:
            sid = resolve(c[3][0], job)
            if sid is not None:
                keys[(job, sid)] = c
    print("登记键(去重后的 (职业, 技能) 对):%d" % len(keys))
    type_count = Counter()
    link_hist = []
    for (job, sid), _ in sorted(keys.items(), key=lambda kv: (kv[0][0] if kv[0][0] is not None else -1, kv[0][1])):
        typ, states = skl_states(job, sid)
        type_count[typ] += 1
        resolvable = unresolvable = hit = 0
        hit_skills = set()
        for c in cons:
            if job_of(c[1]) != job:
                continue
            idx = CONSUME_STATE_INDEX[c[0].lower()] + (0 if c[4] else 1)
            if len(c[3]) <= idx:
                continue
            st = resolve(c[3][idx], job)
            if st is None:
                unresolvable += 1
            else:
                resolvable += 1
                if states and st in states:
                    hit += 1
                    hit_skills.add(resolve(c[3][CONSUME_STATE_INDEX[c[0].lower()] + (0 if c[4] else 1) - 1], job))
        link_hist.append((len(states or []), resolvable, hit, unresolvable, len(hit_skills)))
    print("登记键的技能类型:%s" % dict(type_count))
    for i, (n_states, resolvable, hit, unresolvable, skills) in enumerate(link_hist, 1):
        print("  键 %d:executable states 个数 %d;同职业消费调用点中状态可解析 %d(其中落在该集合内 %d,涉及不同主动技能 %d 个)、不可解析 %d" % (
            i, n_states, resolvable, hit, skills, unresolvable))
    # 消费调用点的状态实参形态
    shapes = Counter()
    for c in cons:
        idx = CONSUME_STATE_INDEX[c[0].lower()] + (0 if c[4] else 1)
        if len(c[3]) <= idx:
            shapes["实参不足"] += 1
        else:
            shapes["可解析常量" if resolve(c[3][idx], job_of(c[1])) is not None else "变量或表达式"] += 1
    print("消费调用点的状态实参:%s" % dict(shapes))
    # 消费调用点的倍率实参(最后一个)是不是字面量 1.0
    rate_lit = Counter()
    for c in cons:
        last = c[3][-1] if c[3] else ""
        rate_lit["1.0 字面量" if re.fullmatch(r"1(\.0*)?f?", last) else "其他"] += 1
    print("消费调用点的倍率实参:%s" % dict(rate_lit))


if __name__ == "__main__":
    main()
