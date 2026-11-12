#!/usr/bin/env python3
"""DNF 工程的看门狗:定期清理开发时 agent 留下的孤儿进程、僵尸进程和垃圾资源。

用法(都在仓库根目录执行):
  python3 tools/watchdog/watchdog.py scan [--deep] [--json]   只看不动:列出"现在跑一轮会做什么",不改任何东西(包括观察记录)
  python3 tools/watchdog/watchdog.py install [--interval N]    安装并启动 launchd 定时任务(默认每 20 分钟)
  python3 tools/watchdog/watchdog.py status                    安装状态、上一轮的结果、日志位置
  python3 tools/watchdog/watchdog.py pause | resume            暂停/恢复(暂停期间定时任务照常触发,但立刻退出)
  python3 tools/watchdog/watchdog.py uninstall [--purge]       卸载定时任务(--purge 连安装目录、配置、日志一起删)
  python3 tools/watchdog/watchdog.py run [--dry-run] [--deep]  真正跑一轮(定时任务调用的就是它)
  python3 tools/watchdog/watchdog.py selfcheck                 自检:依赖的系统工具、配置是否可用

安装时会把代码复制到"安装目录"(默认 ~/Library/Application Support/dnf-unity-watchdog/bin),定时任务跑的是那份副本:
agent 们会在仓库里 git clean、切分支,直接跑仓库里的文件随时可能被它们弄坏,也会让看门狗自己的命令行落进工程范围。
升级看门狗 = 重新执行 install。配置覆盖文件在安装目录的 config.json,写法见 wd_config.py 与 docs/usage.md。
"""
from __future__ import annotations

import argparse
import fcntl
import json
import os
import plistlib
import shutil
import sys
import time
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import wd_config  # noqa: E402
import wd_core  # noqa: E402
import wd_garbage  # noqa: E402
import wd_procs  # noqa: E402
import wd_util  # noqa: E402

STATE_VERSION = 1
TAGS = {"done": "已执行", "would": "将执行", "keep": "保留", "report": "仅报告", "skip": "跳过", "warn": "警告", "error": "出错"}
AREAS = (("proc", "孤儿进程"), ("zombie", "僵尸进程"), ("garbage", "垃圾资源"), ("worktree", "worktree 清点"))


# ---------------------------------------------------------------- 路径

def state_path() -> str:
    return os.path.join(wd_config.app_dir(), "state.json")


def last_run_path() -> str:
    return os.path.join(wd_config.app_dir(), "last-run.json")


def paused_path() -> str:
    return os.path.join(wd_config.app_dir(), "paused")


def lock_path() -> str:
    return os.path.join(wd_config.app_dir(), "run.lock")


def log_path() -> str:
    return os.path.join(wd_config.log_dir(), "watchdog.log")


def agents_dir() -> str:
    return os.environ.get("DNF_WATCHDOG_AGENTS_DIR") or os.path.expanduser("~/Library/LaunchAgents")


def plist_path() -> str:
    return os.path.join(agents_dir(), wd_config.label() + ".plist")


def launchd_target() -> str:
    return "gui/%d/%s" % (os.getuid(), wd_config.label())


# ---------------------------------------------------------------- 一轮运行

def build_context(cfg: Dict[str, Any], log: wd_util.Log, dry_run: bool, state: Dict[str, Any], deep: bool = False,
                  now_up: Optional[float] = None, now_wall: Optional[float] = None) -> wd_core.Context:
    uid = os.getuid()
    repo_root = cfg["repo_root"] or wd_config.detect_repo_root(os.path.dirname(os.path.abspath(__file__)))
    if not repo_root or not os.path.isdir(os.path.join(repo_root, ".git")):
        raise RuntimeError("找不到工程的主工作区(repo_root);请在 config.json 里设置 repo_root")
    now_up = wd_util.awake_clock() if now_up is None else now_up
    now_wall = time.time() if now_wall is None else now_wall
    boot = wd_util.boot_time()
    procs = wd_core.snapshot()
    pcfg = cfg["processes"]
    worktrees = wd_core.list_worktrees(repo_root)
    clones = wd_core.find_scratch_clones(repo_root, worktrees) if pcfg["scope_auto"] else []
    roots = ([w.path for w in worktrees] + clones if pcfg["scope_auto"] else [repo_root]) + list(pcfg["extra_scope_roots"])
    scope = wd_core.Scope(roots, alias_dirs=("/tmp",))
    return wd_core.Context(
        cfg=cfg, log=log, dry_run=dry_run, uid=uid, now_up=now_up, now_wall=now_wall,
        sleep_total=wd_util.sleep_total(boot, now_wall, now_up), procs=procs, cwds=wd_core.read_cwds(uid),
        repo_root=repo_root, label=cfg["workspace_label"] or os.path.basename(repo_root), worktrees=worktrees,
        clones=clones, scope=scope, state=state, report=wd_core.Report(),
        deadline=wd_core.Deadline(float(cfg["deadline_seconds"])),
        self_pids={os.getpid()} | wd_core.ancestors(procs, os.getpid()), deep=deep)


def load_state(boot: int) -> Dict[str, Any]:
    """读观察记录。换了一次开机就作废:醒着的时钟随开机归零,旧记录里的时刻全都没有意义了。"""
    st = wd_util.read_json(state_path(), {})
    meta = st.get("meta") if isinstance(st, dict) else None
    if not isinstance(meta, dict) or meta.get("boot") != boot or meta.get("version") != STATE_VERSION:
        return {"meta": {"boot": boot, "version": STATE_VERSION}}
    return st


def run_once(cfg: Dict[str, Any], problems: List[str], dry_run: bool, deep: bool = False, persist: bool = True,
             echo: bool = False, state: Optional[Dict[str, Any]] = None) -> Tuple[wd_core.Context, int]:
    """跑一轮。persist=False 时不写观察记录、不写 last-run(scan 用)。返回 (上下文, 退出码)。"""
    log = wd_util.Log(log_path() if persist else None, cfg["log"]["max_bytes"], cfg["log"]["backups"], echo=echo)
    t0 = time.time()
    for p in problems:
        log.error("配置问题:" + p)
    forced = bool(problems) and not dry_run
    if forced:
        log.error("配置有问题,本轮降级为只报告(不杀进程、不删文件);修好 config.json 后恢复")
        dry_run = True
    boot = wd_util.boot_time()
    if state is None:
        state = load_state(boot)
    ctx = build_context(cfg, log, dry_run, state, deep=deep)
    log.info("开始一轮%s:进程 %d 个,工作区标签 %r,范围前缀 %d 个,%d 个 worktree + %d 个草稿克隆%s" % (
        "(只报告)" if dry_run else "", len(ctx.procs), ctx.label, len(ctx.scope.roots), len(ctx.worktrees), len(ctx.clones),
        ",深度模式" if deep else ""))
    for name, fn in (("进程", wd_procs.run), ("垃圾", wd_garbage.run)):
        try:
            fn(ctx)
        except Exception as e:   # noqa: BLE001 —— 一部分挂了,另一部分还要接着做
            log.error("%s清理出错:%r" % (name, e))
            ctx.report.add("garbage" if name == "垃圾" else "proc", "error", name + "清理", "出错:%r" % (e,))
    n_err = len(ctx.report.select(action="error"))
    n_done = len(ctx.report.select(action="done"))
    n_would = len(ctx.report.select(action="would"))
    dur = time.time() - t0
    log.info("本轮结束:耗时 %.1f 秒,%s %d 项,释放 %s,出错 %d 项" % (
        dur, "将执行" if dry_run else "已执行", n_would if dry_run else n_done,
        wd_util.fmt_bytes(ctx.report.would_free() if dry_run else ctx.report.freed()), n_err))
    if persist and not dry_run:
        wd_util.atomic_write_json(state_path(), ctx.state)
    if persist:
        wd_util.atomic_write_json(last_run_path(), {
            "time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"), "duration_seconds": round(dur, 1), "dry_run": dry_run,
            "forced_dry_run": forced, "problems": problems, "done": n_done, "would": n_would, "errors": n_err,
            "freed_bytes": ctx.report.freed(),
            "actions": [i for i in ctx.report.items if i["action"] in ("done", "warn", "error")][:40]})
    return ctx, (1 if n_err else 0)


def format_report(ctx: wd_core.Context) -> str:
    lines: List[str] = []
    for area, title in AREAS:
        items = ctx.report.select(area)
        if not items:
            continue
        lines.append("== %s ==" % title)
        for it in items:
            size = "  (%s)" % wd_util.fmt_bytes(it["size"]) if it["size"] else ""
            lines.append("  [%s] %s —— %s%s" % (TAGS.get(it["action"], it["action"]), it["target"], it["why"], size))
    if ctx.dry_run:
        lines.append("以上只是预演,没有改动任何东西。预计释放 %s。" % wd_util.fmt_bytes(ctx.report.would_free()))
    else:
        lines.append("已释放 %s。" % wd_util.fmt_bytes(ctx.report.freed()))
    return "\n".join(lines)


def acquire_lock() -> Optional[int]:
    os.makedirs(wd_config.app_dir(), exist_ok=True)
    fd = os.open(lock_path(), os.O_CREAT | os.O_RDWR, 0o644)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return fd
    except OSError:
        os.close(fd)
        return None


# ---------------------------------------------------------------- 命令:run / scan

def cmd_run(args: argparse.Namespace, scan: bool = False) -> int:
    cfg, problems = wd_config.load_config(args.config)
    interactive = scan or sys.stderr.isatty()
    if not scan and os.path.exists(paused_path()):
        wd_util.Log(log_path(), cfg["log"]["max_bytes"], cfg["log"]["backups"]).info("已暂停(resume 之后恢复),本轮跳过")
        return 0
    if scan:
        ctx, rc = run_once(cfg, problems, dry_run=True, deep=args.deep, persist=False, echo=False)
    else:
        fd = acquire_lock()
        if fd is None:
            wd_util.Log(log_path(), cfg["log"]["max_bytes"], cfg["log"]["backups"]).info("上一轮还没结束,本轮跳过")
            return 0
        try:
            ctx, rc = run_once(cfg, problems, dry_run=args.dry_run, deep=args.deep, persist=not args.dry_run, echo=interactive)
        finally:
            os.close(fd)
    if args.json:
        print(json.dumps({"dry_run": ctx.dry_run, "problems": problems, "items": ctx.report.items}, ensure_ascii=False, indent=1))
    elif interactive:
        for p in problems:
            print("配置问题:" + p, file=sys.stderr)
        print(format_report(ctx))
    return rc


# ---------------------------------------------------------------- 命令:selfcheck / install / uninstall / status / pause / resume

def pick_python() -> str:
    """定时任务用的解释器:优先系统自带的 /usr/bin/python3(不会因为 brew 升级而路径失效),要求 ≥ 3.9。"""
    for cand in ("/usr/bin/python3", sys.executable):
        if cand and os.path.exists(cand):
            rc, _, _ = wd_util.run([cand, "-c", "import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)"], timeout=30)
            if rc == 0:
                return cand
    raise RuntimeError("找不到 Python ≥ 3.9")


def cmd_selfcheck(args: argparse.Namespace) -> int:
    hard: List[str] = []
    soft: List[str] = []
    cfg, problems = wd_config.load_config(args.config)
    hard.extend("配置问题:" + p for p in problems)
    for tool in ("ps", "lsof", "git", "getconf", "sysctl"):
        if shutil.which(tool) is None:
            hard.append("缺少系统工具:" + tool)
    repo = cfg["repo_root"] or wd_config.detect_repo_root(os.path.dirname(os.path.abspath(__file__)))
    if not repo or not os.path.isdir(os.path.join(repo, ".git")):
        hard.append("找不到工程主工作区(repo_root)")
    if wd_procs.rusage(os.getpid()) is None:
        soft.append("proc_pid_rusage 不可用:读不到进程资源用量,进程不会因闲置被清理")
    if wd_procs.read_net() is None:
        soft.append("nettop 不可用:读不到网络流量,进程不会因闲置被清理")
    try:
        wd_core.snapshot()
    except RuntimeError as e:
        hard.append(str(e))
    for m in hard:
        print("[错误] " + m)
    for m in soft:
        print("[警告] " + m)
    if not hard:
        print("自检通过:repo_root=%s,python=%s%s" % (repo, sys.version.split()[0], "(有警告)" if soft else ""))
    return 1 if hard else 0


def build_plist(python: str, script: str, interval_minutes: int) -> Dict[str, Any]:
    return {
        "Label": wd_config.label(),
        "ProgramArguments": [python, script, "run"],
        "StartInterval": int(interval_minutes) * 60,
        "RunAtLoad": True,
        "ProcessType": "Background",
        "LowPriorityIO": True,
        "Nice": 10,
        "WorkingDirectory": wd_config.app_dir(),
        "StandardOutPath": os.path.join(wd_config.log_dir(), "launchd.out.log"),
        "StandardErrorPath": os.path.join(wd_config.log_dir(), "launchd.err.log"),
        "EnvironmentVariables": {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
                                 **{k: os.environ[k] for k in ("DNF_WATCHDOG_HOME", "DNF_WATCHDOG_LOGS") if k in os.environ}},
    }


def launchctl(*a: str) -> Tuple[int, str, str]:
    return wd_util.run(["launchctl"] + list(a), timeout=30)


def cmd_install(args: argparse.Namespace) -> int:
    src = os.path.dirname(os.path.abspath(__file__))
    repo = wd_config.detect_repo_root(src)
    if not repo:
        print("[错误] 找不到仓库根目录,请在仓库里执行 install", file=sys.stderr)
        return 2
    app_bin = os.path.join(wd_config.app_dir(), "bin")
    os.makedirs(app_bin, exist_ok=True)
    os.makedirs(wd_config.log_dir(), exist_ok=True)
    copied = []
    for name in sorted(os.listdir(src)):
        if name == "watchdog.py" or (name.startswith("wd_") and name.endswith(".py")):
            tmp = os.path.join(app_bin, ".%s.tmp" % name)
            shutil.copyfile(os.path.join(src, name), tmp)
            os.replace(tmp, os.path.join(app_bin, name))
            copied.append(name)
    cfg_file = args.config or wd_config.user_config_path()
    if not os.path.exists(cfg_file):
        wd_util.atomic_write_json(cfg_file, {"repo_root": repo})
    python = pick_python()
    script = os.path.join(app_bin, "watchdog.py")
    rc, out, err = wd_util.run([python, script, "selfcheck"] + (["--config", args.config] if args.config else []), timeout=120)
    print(out.rstrip())
    if rc != 0:
        print("[错误] 自检没通过,没有注册定时任务。" + err.strip(), file=sys.stderr)
        return 1
    cfg, _ = wd_config.load_config(args.config)
    minutes = args.interval or cfg["interval_minutes"]
    plist = build_plist(python, script, minutes)
    os.makedirs(agents_dir(), exist_ok=True)
    with open(plist_path(), "wb") as f:
        plistlib.dump(plist, f)
    rc, out, err = wd_util.run(["plutil", "-lint", plist_path()], timeout=15)
    if rc != 0:
        print("[错误] plist 校验失败:" + (out + err).strip(), file=sys.stderr)
        return 1
    print("已安装 %d 个文件到 %s,配置文件 %s" % (len(copied), app_bin, cfg_file))
    if args.no_load:
        print("(--no-load)只生成了 plist:%s,没有注册到 launchd" % plist_path())
        return 0
    launchctl("bootout", launchd_target())     # 已经装过就先卸掉(没装过会报错,忽略)
    rc, out, err = launchctl("bootstrap", "gui/%d" % os.getuid(), plist_path())
    if rc != 0:
        print("[错误] launchctl bootstrap 失败:%s" % (out + err).strip(), file=sys.stderr)
        return 1
    launchctl("enable", launchd_target())
    print("已注册 launchd 任务 %s,每 %s 分钟一轮,马上会先跑一轮。" % (wd_config.label(), minutes))
    print("提示:macOS 可能弹出「后台项目已添加」的通知,这是系统对新增登录项的正常提醒。")
    return 0


def cmd_uninstall(args: argparse.Namespace) -> int:
    rc, out, err = launchctl("bootout", launchd_target())
    print("已从 launchd 卸载。" if rc == 0 else "launchd 里没有这个任务(或已卸载):%s" % (out + err).strip()[:120])
    if os.path.exists(plist_path()):
        os.unlink(plist_path())
        print("已删除 " + plist_path())
    if args.purge:
        for d in (wd_config.app_dir(), wd_config.log_dir()):
            shutil.rmtree(d, ignore_errors=True)
            print("已删除 " + d)
    else:
        print("安装目录、配置和日志都保留着(想删干净:uninstall --purge)。")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    cfg, problems = wd_config.load_config(args.config)
    installed = os.path.exists(plist_path())
    rc, out, _ = launchctl("print", launchd_target())
    loaded = rc == 0
    print("安装:%s;launchd 已加载:%s;%s" % ("是" if installed else "否", "是" if loaded else "否",
                                      "已暂停" if os.path.exists(paused_path()) else "运行中(未暂停)"))
    if loaded:
        for key in ("state", "runs", "last exit code", "run interval"):
            for line in out.splitlines():
                if line.strip().startswith(key + " ="):
                    print("  " + line.strip())
                    break
    print("配置:%s%s" % (wd_config.user_config_path(), "(有问题,已降级为只报告)" if problems else ""))
    for p in problems:
        print("  配置问题:" + p)
    last = wd_util.read_json(last_run_path())
    if last:
        print("上一轮:%s,耗时 %s 秒,%s %d 项,出错 %d 项,释放 %s%s" % (
            last["time"], last["duration_seconds"], "将执行" if last["dry_run"] else "已执行",
            last["would"] if last["dry_run"] else last["done"], last["errors"], wd_util.fmt_bytes(last["freed_bytes"]),
            ";因配置问题降级为只报告" if last.get("forced_dry_run") else ""))
        for a in last["actions"][:10]:
            print("  [%s] %s —— %s" % (TAGS.get(a["action"], a["action"]), a["target"], a["why"]))
    else:
        print("还没有运行记录。")
    print("日志:" + log_path())
    return 0


def cmd_pause(args: argparse.Namespace) -> int:
    os.makedirs(wd_config.app_dir(), exist_ok=True)
    with open(paused_path(), "w", encoding="utf-8") as f:
        f.write(datetime.now().strftime("%Y-%m-%d %H:%M:%S") + "\n")
    print("已暂停:定时任务照常触发,但每轮立刻退出,什么都不做。恢复:resume")
    return 0


def cmd_resume(args: argparse.Namespace) -> int:
    try:
        os.unlink(paused_path())
        print("已恢复。")
    except FileNotFoundError:
        print("本来就没有暂停。")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="watchdog.py", description="清理 agent 留下的孤儿进程、僵尸进程和垃圾资源")
    ap.add_argument("--config", help="配置覆盖文件路径(默认安装目录里的 config.json)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("run", help="真正跑一轮")
    p.add_argument("--dry-run", action="store_true", help="只预演")
    p.add_argument("--deep", action="store_true", help="连平时不做的重活(构建产物、worktree 清点)也做")
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("scan", help="只看不动")
    p.add_argument("--deep", action="store_true")
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("install", help="安装并启动定时任务")
    p.add_argument("--interval", type=int, help="每隔多少分钟跑一轮(默认取配置里的 interval_minutes)")
    p.add_argument("--no-load", action="store_true", help="只复制文件、生成 plist,不注册到 launchd")
    p = sub.add_parser("uninstall", help="卸载定时任务")
    p.add_argument("--purge", action="store_true", help="连安装目录、配置、日志一起删")
    sub.add_parser("status")
    sub.add_parser("pause")
    sub.add_parser("resume")
    sub.add_parser("selfcheck")
    args = ap.parse_args(argv)
    try:
        if args.cmd == "run":
            return cmd_run(args)
        if args.cmd == "scan":
            return cmd_run(args, scan=True)
        return {"install": cmd_install, "uninstall": cmd_uninstall, "status": cmd_status, "pause": cmd_pause,
                "resume": cmd_resume, "selfcheck": cmd_selfcheck}[args.cmd](args)
    except RuntimeError as e:
        print("[错误] %s" % e, file=sys.stderr)
        wd_util.Log(log_path()).error(str(e))
        return 2


if __name__ == "__main__":
    sys.exit(main())
