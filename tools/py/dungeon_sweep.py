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
EXC = re.compile(r"(Unhandled exception|未处理的异常|System\.\w+Exception|Exception:|   at Dnf\.)")


def list_dungeons():
    env = dict(os.environ)
    out = subprocess.run(
        [sys.executable, os.path.join(ROOT, "tools/py/script_dump.py"), "--find", "dungeon/", "--ext", "dgn", "--limit", "100000"],
        capture_output=True, text=True, env=env, cwd=ROOT).stdout
    return sorted(l.split()[0] for l in out.splitlines() if l.strip().endswith(tuple("0123456789")) and ".dgn" in l)


def run_one(path, seed, seconds, real_timeout, explore, damage=False, scale=5000, smash=False):
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
    ap.add_argument("--scale", type=int, default=5000, help="主角每一击伤害的倍数(默认 5000,排除一级主角打不动高等级怪物的干扰;脚本怪物(M36)出场后高等级首领的血量上百万,100 倍在 600 秒里打不完;1 = 不放大)")
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
    print("用时 %.0f 秒;明细 %s.json / %s.tsv" % (time.time() - t0, a.out, a.out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
