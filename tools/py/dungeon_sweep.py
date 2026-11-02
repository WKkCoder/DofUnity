#!/usr/bin/env python3
"""全库副本扫描(M21):让自动机把每个副本定义从起点打到首领清空,统计通关率并把没打通的分类。

和 tools/py/verify_all.py 的区别:verify_all 只用 LORIEN(洛兰)走整条流程;这里把 Script.pvf 里
全部 `dungeon/**/*.dgn` 各跑一遍 `dnf dungeon-check --bot --explore`,用来发现"某个副本进不去、某个房间卡死、
某个怪物打不死"这类只在别的副本里才出现的缺口。结果只在本机(out/,不入库);要记进文档的是汇总数字和卡点的机制,
不是逐副本的清单。

用法:
  tools/env.sh 之后(或先 `. ./local.env`):
  python3 tools/py/dungeon_sweep.py                      # 全部 346 个,6 个并行
  python3 tools/py/dungeon_sweep.py --only act1,act2     # 只扫这些目录(dungeon/ 后的第一级)
  python3 tools/py/dungeon_sweep.py --seed 2 --seconds 900 --jobs 4
  python3 tools/py/dungeon_sweep.py --damage             # 主角会被打(1 级主角打高等级副本会死,结果多半是 stuck)
  python3 tools/py/dungeon_sweep.py --smash              # 自动机清房、捡完金币之后顺手打翻房间里的可破坏物件(M34,E-126);汇总里多一行物件统计(只在本机)
  python3 tools/py/dungeon_sweep.py --dungeon dungeon/act1/mirkwood.dgn   # 只跑一个并打印尾部

输出:out/dungeon-sweep.json(逐副本一条)、out/dungeon-sweep.tsv(同内容的表)和汇总。
分类(status):
  cleared    首领清空(命令行退出码 0)
  stuck      自动机到时间还没打完(退出码 2):房间里有清不掉的怪物、够不着的出口、或自动机走不到
  nolayout   读不出迷宫(命令直接报错退出,没有产出布局)
  nomaps     迷宫读得出,但没有任何房间地图属于这个副本(异常信息里"房间候选 0 张")
  crash      其它未处理异常(输出里有异常堆栈)
  timeout    真实时间超过 --real-timeout 还没结束(死循环嫌疑)
  other      其它退出码
"""
import argparse
import concurrent.futures
import json
import os
import re
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DNF = [os.path.join(ROOT, "src/Dnf.Cli/bin/Release/net10.0/dnf.dll")]
LAYOUT = re.compile(r"迷宫 (\d+) 列 × (\d+) 行")
END = re.compile(r"结束:t=([\d.]+)s\s+(\S+)\s+进过 (\d+) 个房间,清空 (\d+) 个")
HITS = re.compile(r"命中 (\d+) 次,击杀 (\d+) 个")
OBJECTS = re.compile(r"物件\(E-126\):进过的房间里有 (\d+) 个可破坏物件,挥中 (\d+) 次,打翻 (\d+) 个,放出 \[item\] 动作 (\d+) 次,掉出 (\d+) 件")
ACTS = re.compile(r"脚本\(E-130\):进过的房间里出招带 \.act 触发器的怪物 (\d+) 只,脚本放出被动对象 (\d+) 个,打中主角 (\d+) 次")
SUMMONS = re.compile(r"召唤\(E-134\):脚本召唤出怪物 (\d+) 只,被拒绝 (\d+) 次,召唤物被打死 (\d+) 只")
SWITCH = re.compile(r"脚本跨招开关\(E-145\):落到存在的招与触发器 (\d+) 次,落空 (\d+) 次,指向未接线的动作族 (\d+) 次")
BOARD = re.compile(r"被动对象的动作开关板\(E-158\):读别的动作的开关 (\d+) 次\(落空 (\d+)\),写 (\d+) 次\(落空 (\d+)\)")
CUSTOM = re.compile(r"怪物的自定义动作\(E-159\):进过的房间里写了 \[etc action\] 的怪物 (\d+) 只,切进自定义动作 (\d+) 次,自己放完 (\d+) 次,要切的不存在 (\d+) 次")
APPEND = re.compile(r"属性改变附加物\(E-161\):怪物挂上 (\d+) 次\(顶替 (\d+)、删除 (\d+)、到期 (\d+)\),主角挂上 (\d+) 次\(顶替 (\d+)、删除 (\d+)、到期 (\d+)\);按类型\(\* = 这一侧没有读它的地方\):(.*)")
FAMILY = re.compile(r"脚本非出招动作族\(E-146\):进过的房间里装了站立、行走、受击、倒地、过程或最后一幕触发器的怪物 (\d+) 只,运行器启动 (\d+) 次,\[SET ACTION\] 落实 (\d+) 次、没落实 (\d+) 次")
FILES = re.compile(r"脚本换动作文件\(E-151\):换上 (\d+) 次,记成待换 (\d+) 次,这一版没有对应动作的族 (\d+) 次,序号越界 (\d+) 次,文件读不到 (\d+) 次")
MIX = re.compile(r"怪物构成\(E-147\):进过的房间里共 (\d+) 只,\.ai 脚本指挥 (\d+) 只,默认 AI 走位大脑 (\d+) 只,默认 AI 占位 (\d+) 只")
UNS_BEH = re.compile(r"脚本没做的行为\(E-147,实际执行次数\):(.*)")
UNS_TRG = re.compile(r"脚本读不懂的触发器标签\(E-147,运行器每次开始算一次\):(.*)")
STATUS = re.compile(r"异常状态\(E-136\):挂到主角身上 (\d+) 次,概率没中 (\d+) 次,持续伤害共 (\d+) 点(?:,脚本 \[ACTIVE STATUS\]\(E-144\)发起的请求 (\d+) 次)?")
EXC = re.compile(r"(Unhandled exception|未处理的异常|System\.\w+Exception|Exception:|   at Dnf\.)")


def parse_tag_table(text):
    """把"标签=次数; 标签=次数"解析成字典;"无"是空表。"""
    out = {}
    text = text.strip()
    if not text or text == "无":
        return out
    for part in text.split("; "):
        tag, _, n = part.rpartition("=")
        if tag and n.isdigit():
            out[tag] = out.get(tag, 0) + int(n)
    return out


def list_dungeons():
    env = dict(os.environ)
    out = subprocess.run(
        [sys.executable, os.path.join(ROOT, "tools/py/script_dump.py"), "--find", "dungeon/", "--ext", "dgn", "--limit", "100000"],
        capture_output=True, text=True, env=env, cwd=ROOT).stdout
    return sorted(l.split()[0] for l in out.splitlines() if l.strip().endswith(tuple("0123456789")) and ".dgn" in l)


def run_one(path, seed, seconds, real_timeout, explore, damage=False, scale=1000000, smash=False):
    cmd = ["dotnet"] + DNF + ["dungeon-check", "--dungeon", path, "--bot", "--seed", str(seed), "--seconds", str(seconds)]
    if explore:
        cmd.insert(len(cmd), "--explore")
    if not damage:
        cmd.append("--no-damage")   # 主角不掉血:这里量的是布局/进图/清房/出口通不通,不是 1 级主角打不打得过高等级怪物
    if scale > 1:
        cmd += ["--damage-scale", str(scale)]   # 主角每一击乘上 scale 倍(测试旋钮),同上
    if smash:
        cmd.append("--smash")                   # 自动机清房之后顺手打翻物件(M34)
    t0 = time.time()
    rec = {"dungeon": path, "status": "other", "exit": None, "real_s": 0.0}
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, cwd=ROOT, timeout=real_timeout)
        text = (p.stdout or "") + (p.stderr or "")
        rec["exit"] = p.returncode
    except subprocess.TimeoutExpired as e:
        text = ((e.stdout or b"").decode("utf-8", "replace") if isinstance(e.stdout, bytes) else (e.stdout or ""))
        rec["status"] = "timeout"
        rec["real_s"] = round(time.time() - t0, 1)
        rec["tail"] = text[-600:]
        return rec
    rec["real_s"] = round(time.time() - t0, 1)
    m = LAYOUT.search(text)
    if m:
        rec["cols"], rec["rows"] = int(m.group(1)), int(m.group(2))
    e = END.search(text)
    if e:
        rec["sim_s"] = float(e.group(1))
        rec["end"] = e.group(2)
        rec["rooms_entered"] = int(e.group(3))
        rec["rooms_cleared"] = int(e.group(4))
    h = HITS.search(text)
    if h:
        rec["hits"], rec["kills"] = int(h.group(1)), int(h.group(2))
    o = OBJECTS.search(text)
    if o:
        rec["objects"], rec["obj_hit"], rec["obj_destroyed"], rec["obj_item_actions"], rec["obj_items"] = (int(o.group(i)) for i in range(1, 6))
    a = ACTS.search(text)
    if a:
        rec["act_monsters"], rec["passives"], rec["passive_hits"] = (int(a.group(i)) for i in range(1, 4))
    sm = SUMMONS.search(text)
    if sm:
        rec["summoned"], rec["summon_refused"], rec["summoned_killed"] = (int(sm.group(i)) for i in range(1, 4))
    sw = SWITCH.search(text)
    if sw:
        rec["switch_applied"], rec["switch_missed"], rec["switch_unhosted"] = (int(sw.group(i)) for i in range(1, 4))
    bd = BOARD.search(text)
    if bd:
        rec["board_reads"], rec["board_read_misses"], rec["board_writes"], rec["board_write_misses"] = (int(bd.group(i)) for i in range(1, 5))
    cu = CUSTOM.search(text)
    if cu:
        rec["custom_monsters"], rec["custom_started"], rec["custom_finished"], rec["custom_rejected"] = (int(cu.group(i)) for i in range(1, 5))
    ap = APPEND.search(text)
    if ap:
        for i, k in enumerate(("ap_m_added", "ap_m_replaced", "ap_m_removed", "ap_m_expired", "ap_h_added", "ap_h_replaced", "ap_h_removed", "ap_h_expired")):
            rec[k] = int(ap.group(i + 1))
        types = {}
        txt = ap.group(9).strip()
        if txt and txt != "无":
            for part in txt.split("; "):
                tag, _, nums = part.rpartition(" ")
                unread = nums.endswith("*")
                m_, _, h_ = nums.rstrip("*").partition("/")
                if tag and m_.isdigit() and h_.isdigit():
                    types[tag] = (int(m_), int(h_), unread)
        rec["ap_types"] = types
    fm = FAMILY.search(text)
    if fm:
        rec["family_monsters"], rec["family_runs"], rec["family_set_applied"], rec["family_set_unhosted"] = (int(fm.group(i)) for i in range(1, 5))
    fl = FILES.search(text)
    if fl:
        rec["files_applied"], rec["files_deferred"], rec["files_unhosted"], rec["files_rejected"], rec["files_missing"] = (int(fl.group(i)) for i in range(1, 6))
    mx = MIX.search(text)
    if mx:
        rec["mon_total"], rec["mon_scripted"], rec["mon_brain"], rec["mon_placeholder"] = (int(mx.group(i)) for i in range(1, 5))
    ub = UNS_BEH.search(text)
    if ub:
        rec["unsup_beh"] = parse_tag_table(ub.group(1))
    ut = UNS_TRG.search(text)
    if ut:
        rec["unsup_trg"] = parse_tag_table(ut.group(1))
    st = STATUS.search(text)
    if st:
        rec["status_applied"], rec["status_missed"], rec["status_tick_damage"] = (int(st.group(i)) for i in range(1, 4))
        rec["act_status_requests"] = int(st.group(4) or 0)
    if EXC.search(text):
        rec["status"] = "crash"
        lines = [l for l in text.splitlines() if l.strip()]
        rec["tail"] = "\n".join(lines[-8:])[-900:]
        first = next((l for l in lines if "Exception:" in l), "")
        rec["error"] = first[first.find("Exception:") + 10:].strip()[:200]
        if "房间候选 0 张" in text:
            rec["status"] = "nomaps"    # 没有任何房间地图指向这个副本定义(未登记的开发残留、或只有素材没有数据)
    elif rec["exit"] == 0 and e:
        rec["status"] = "cleared"
    elif rec["exit"] == 2 and e:
        rec["status"] = "stuck"
        # 最后一次"进入格"就是卡住的那一格
        i = text.find("未走完的现场")
        if i >= 0:
            rec["diag"] = text[i:].strip()[:1500]
        enters = re.findall(r"进入格 \((\d+),(\d+)\) (\S+)", text)
        if enters:
            rec["stuck_cell"] = [int(enters[-1][0]), int(enters[-1][1])]
            rec["stuck_map"] = enters[-1][2]
    elif not m:
        rec["status"] = "nolayout"
        lines = [l for l in text.splitlines() if l.strip()]
        rec["tail"] = "\n".join(lines[-4:])[-500:]
    return rec


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="逗号分隔的 dungeon/ 下一级目录名")
    ap.add_argument("--dungeon", default="", help="只跑一个副本定义并打印输出尾部")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--seconds", type=int, default=900)
    ap.add_argument("--jobs", type=int, default=6)
    ap.add_argument("--real-timeout", type=float, default=180.0)
    ap.add_argument("--no-explore", action="store_true")
    ap.add_argument("--damage", action="store_true", help="主角会被打(默认不会:无伤害模式,只量布局与流程)")
    ap.add_argument("--smash", action="store_true", help="自动机清房之后顺手打翻房间里的可破坏物件(M34)")
    ap.add_argument("--scale", type=int, default=1000000, help="主角每一击伤害的倍数(默认 1000000,排除一级主角打不动高等级怪物的干扰;脚本怪物(M36)出场后高等级首领的血量上百万,100 倍在 600 秒里打不完;M63 之前默认 5000,更正防御方字段(E-160)后一级主角对高等级怪物每击只剩保底 1 点,旋钮重标定;1 = 不放大)")
    ap.add_argument("--out", default=os.path.join(ROOT, "out/dungeon-sweep"))
    a = ap.parse_args(argv)

    if a.dungeon:
        r = run_one(a.dungeon, a.seed, a.seconds, a.real_timeout, not a.no_explore, a.damage, a.scale, a.smash)
        print(json.dumps(r, ensure_ascii=False, indent=1))
        return 0 if r["status"] == "cleared" else 2

    names = list_dungeons()
    if a.only:
        keep = set(a.only.split(","))
        names = [n for n in names if n.split("/")[1] in keep]
    print("扫描 %d 个副本定义(种子 %d,最长 %d 秒,%d 个并行)" % (len(names), a.seed, a.seconds, a.jobs), flush=True)
    results = []
    t0 = time.time()
    with concurrent.futures.ThreadPoolExecutor(max_workers=a.jobs) as ex:
        futs = {ex.submit(run_one, n, a.seed, a.seconds, a.real_timeout, not a.no_explore, a.damage, a.scale, a.smash): n for n in names}
        for i, f in enumerate(concurrent.futures.as_completed(futs), 1):
            results.append(f.result())
            if i % 40 == 0:
                print("  已完成 %d/%d(%.0f 秒)" % (i, len(names), time.time() - t0), flush=True)
    results.sort(key=lambda r: r["dungeon"])
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out + ".json", "w", encoding="utf-8") as fh:
        json.dump(results, fh, ensure_ascii=False, indent=1)
    cols = ["dungeon", "status", "exit", "cols", "rows", "rooms_entered", "rooms_cleared", "kills", "sim_s", "real_s", "stuck_map", "error"]
    if a.smash:
        cols[10:10] = ["objects", "obj_hit", "obj_destroyed", "obj_item_actions", "obj_items"]
    with open(a.out + ".tsv", "w", encoding="utf-8") as fh:
        fh.write("\t".join(cols) + "\n")
        for r in results:
            fh.write("\t".join(str(r.get(c, "")) for c in cols) + "\n")

    by = {}
    for r in results:
        d = r["dungeon"].split("/")[1]
        by.setdefault(d, {}).setdefault(r["status"], 0)
        by[d][r["status"]] += 1
    order = ["cleared", "stuck", "nolayout", "nomaps", "crash", "timeout", "other"]
    print("\n按目录汇总(" + " / ".join(order) + "):")
    tot = {k: 0 for k in order}
    for d in sorted(by):
        row = by[d]
        for k in order:
            tot[k] += row.get(k, 0)
        print("  %-18s %s" % (d, " / ".join(str(row.get(k, 0)) for k in order)))
    print("合计 %d:%s" % (len(results), "  ".join("%s %d" % (k, tot[k]) for k in order)))
    if a.smash:
        keys = ["objects", "obj_hit", "obj_destroyed", "obj_item_actions", "obj_items"]
        sums = {k: sum(r.get(k, 0) for r in results) for k in keys}
        withobj = sum(1 for r in results if r.get("objects", 0) > 0)
        print("物件(只在本机):有物件的副本 %d 个;可破坏物件 %d 个,挥中 %d 次,打翻 %d 个,[item] 动作 %d 次,掉出 %d 件"
              % (withobj, sums["objects"], sums["obj_hit"], sums["obj_destroyed"], sums["obj_item_actions"], sums["obj_items"]))
    act_keys = ["act_monsters", "passives", "passive_hits"]
    act_sums = {k: sum(r.get(k, 0) for r in results) for k in act_keys}
    with_acts = sum(1 for r in results if r.get("act_monsters", 0) > 0)
    spawned = sum(1 for r in results if r.get("passives", 0) > 0)
    print("脚本(只在本机,E-130):有出招带触发器的怪物的副本 %d 个,其中放出过被动对象的 %d 个;怪物 %d 只,放出被动对象 %d 个,打中主角 %d 次"
          % (with_acts, spawned, act_sums["act_monsters"], act_sums["passives"], act_sums["passive_hits"]))
    sum_keys = ["summoned", "summon_refused", "summoned_killed"]
    sum_sums = {k: sum(r.get(k, 0) for r in results) for k in sum_keys}
    with_summons = sum(1 for r in results if r.get("summoned", 0) > 0)
    print("召唤(只在本机,E-134):召唤过怪物的副本 %d 个;召唤出 %d 只,被拒绝 %d 次,召唤物被打死 %d 只"
          % (with_summons, sum_sums["summoned"], sum_sums["summon_refused"], sum_sums["summoned_killed"]))
    sw_keys = ["switch_applied", "switch_missed", "switch_unhosted"]
    sw_sums = {k: sum(r.get(k, 0) for r in results) for k in sw_keys}
    with_switch = sum(1 for r in results if r.get("switch_applied", 0) > 0)
    print("跨招开关(只在本机,E-145):脚本落实过按动作族 [TRIGGER] 的副本 %d 个;落到存在的招与触发器 %d 次,落空 %d 次,指向未接线的动作族 %d 次"
          % (with_switch, sw_sums["switch_applied"], sw_sums["switch_missed"], sw_sums["switch_unhosted"]))
    board_keys = ["board_reads", "board_read_misses", "board_writes", "board_write_misses"]
    board_sums = {k: sum(r.get(k, 0) for r in results) for k in board_keys}
    with_board = sum(1 for r in results if r.get("board_reads", 0) + r.get("board_writes", 0) > 0)
    print("被动对象开关板(只在本机,E-158):读写过别的动作开关的副本 %d 个;读 %d 次(落空 %d 次),写 %d 次(落空 %d 次)"
          % (with_board, board_sums["board_reads"], board_sums["board_read_misses"], board_sums["board_writes"], board_sums["board_write_misses"]))
    custom_keys = ["custom_monsters", "custom_started", "custom_finished", "custom_rejected"]
    custom_sums = {k: sum(r.get(k, 0) for r in results) for k in custom_keys}
    with_custom = sum(1 for r in results if r.get("custom_started", 0) > 0)
    print("怪物自定义动作(只在本机,E-159):切进过自定义动作的副本 %d 个;有列表的怪物 %d 只,切进 %d 次,自己放完 %d 次,要切的不存在 %d 次"
          % (with_custom, custom_sums["custom_monsters"], custom_sums["custom_started"], custom_sums["custom_finished"], custom_sums["custom_rejected"]))
    ap_keys = ["ap_m_added", "ap_m_replaced", "ap_m_removed", "ap_m_expired", "ap_h_added", "ap_h_replaced", "ap_h_removed", "ap_h_expired"]
    ap_sums = {k: sum(r.get(k, 0) for r in results) for k in ap_keys}
    with_ap = sum(1 for r in results if r.get("ap_m_added", 0) + r.get("ap_h_added", 0) > 0)
    print("属性改变附加物(只在本机,E-161):挂过的副本 %d 个;怪物挂上 %d 次(顶替 %d、删除 %d、到期 %d),主角挂上 %d 次(顶替 %d、删除 %d、到期 %d)"
          % ((with_ap,) + tuple(ap_sums[k] for k in ap_keys)))
    ap_types = {}
    for r in results:
        for tag, (m_, h_, unread) in r.get("ap_types", {}).items():
            row = ap_types.setdefault(tag, [0, 0, False])
            row[0] += m_
            row[1] += h_
            row[2] = row[2] or unread
    print("  按类型(怪物/主角,* = 这一侧没有读它的地方):" + ("; ".join("%s %d/%d%s" % (t, row[0], row[1], "*" if row[2] else "")
          for t, row in sorted(ap_types.items(), key=lambda kv: (-(kv[1][0] + kv[1][1]), kv[0]))) or "无"))
    fam_keys = ["family_monsters", "family_runs", "family_set_applied", "family_set_unhosted"]
    fam_sums = {k: sum(r.get(k, 0) for r in results) for k in fam_keys}
    with_family = sum(1 for r in results if r.get("family_runs", 0) > 0)
    print("非出招动作族(只在本机,E-146):有宿主运行过的副本 %d 个;怪物 %d 只,运行器启动 %d 次,[SET ACTION] 落实 %d 次、没落实 %d 次"
          % (with_family, fam_sums["family_monsters"], fam_sums["family_runs"], fam_sums["family_set_applied"], fam_sums["family_set_unhosted"]))
    file_keys = ["files_applied", "files_deferred", "files_unhosted", "files_rejected", "files_missing"]
    file_sums = {k: sum(r.get(k, 0) for r in results) for k in file_keys}
    with_files = sum(1 for r in results if r.get("files_applied", 0) + r.get("files_deferred", 0) + r.get("files_unhosted", 0) > 0)
    print("换动作文件(只在本机,E-151):脚本换过动作文件的副本 %d 个;换上 %d 次,记成待换 %d 次,没有对应动作的族 %d 次,序号越界 %d 次,文件读不到 %d 次"
          % (with_files, file_sums["files_applied"], file_sums["files_deferred"], file_sums["files_unhosted"], file_sums["files_rejected"], file_sums["files_missing"]))
    mix = {}
    for r in results:
        d = r["dungeon"].split("/")[1]
        row = mix.setdefault(d, [0, 0, 0, 0])
        for i, k in enumerate(("mon_total", "mon_scripted", "mon_brain", "mon_placeholder")):
            row[i] += r.get(k, 0)
    tot_mix = [sum(row[i] for row in mix.values()) for i in range(4)]
    print("怪物构成(只在本机,E-147):进过的房间里共 %d 只,.ai 脚本指挥 %d 只、默认 AI 走位大脑 %d 只、默认 AI 占位 %d 只" % tuple(tot_mix))
    print("  按目录(总数/脚本/大脑/占位):" + ";".join("%s %d/%d/%d/%d" % (d, *row) for d, row in sorted(mix.items(), key=lambda kv: -kv[1][0]) if row[0] > 0))
    for key, title in (("unsup_beh", "没做的行为(实际执行次数)"), ("unsup_trg", "读不懂的触发器标签(运行器启动次数)")):
        total = {}
        for r in results:
            for tag, n in r.get(key, {}).items():
                total[tag] = total.get(tag, 0) + n
        top = sorted(total.items(), key=lambda kv: (-kv[1], kv[0]))
        print("%s(只在本机,E-147):%d 种,合计 %d 次;前 14:%s"
              % (title, len(top), sum(total.values()), "; ".join("%s=%d" % kv for kv in top[:14]) if top else "无"))
    if a.damage:
        with_status = sum(1 for r in results if r.get("status_applied", 0) > 0)
        with_act_status = sum(1 for r in results if r.get("act_status_requests", 0) > 0)
        print("异常状态(只在本机,E-136/E-144,--damage):挂到主角身上的副本 %d 个,其中脚本 [ACTIVE STATUS] 发起过请求的 %d 个;挂上 %d 次,概率没中 %d 次,脚本请求 %d 次"
              % (with_status, with_act_status, sum(r.get("status_applied", 0) for r in results), sum(r.get("status_missed", 0) for r in results),
                 sum(r.get("act_status_requests", 0) for r in results)))
    print("用时 %.0f 秒;明细 %s.json / %s.tsv" % (time.time() - t0, a.out, a.out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
