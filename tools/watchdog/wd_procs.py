#!/usr/bin/env python3
"""孤儿进程与僵尸进程的判定与清理。

核心原则:宁可漏杀,不可错杀。一棵进程树要被清理,必须依次过完下面所有关口:

  1. 是"孤儿树根":父进程是 launchd(PPID=1)。注意 PPID=1 本身不说明任何问题——
     合法的后台任务(nohup 起的分片驱动、图形界面启动的 Unity、共享的编译服务器)也是 PPID=1。
  2. 不受保护:树里没有 Cursor 本体、调度器、tmux、系统进程、看门狗自己;共享的编译服务器永不处理。
  3. 属于本工程:树里有进程的 cwd 在工程范围(主工作区+全部 worktree)内,或命令行按路径边界提到了它,
     或环境里的工作区标签就是本工程。
  4. 有出身证明:对 dotnet / Unity 批处理 / Ghidra 无头这类"一看就是开发工具"的签名可以豁免;
     其余(脚本、远程工具、未知进程)要求树里有进程的环境里带 CURSOR_AGENT,证明它是 agent 启动的。
  5. 整棵树持续闲置:看门狗自己连续观察,在 idle_minutes 的"醒着的时间"里,整棵树的 CPU(含已回收子进程)、
     磁盘 I/O、网络收发都没有进展,成员也没有变动(sleep 这种空转不算)。
     观察用的是"醒着的时钟":机器睡眠不计时;刚安装时没有任何观察记录,所以什么都不会被杀,
     要等观察窗口满了才可能动手。任何一种仪表读不到,当轮一律按"有进展"处理——没有证据就不是闲置。
  6. 动手前复核:重新取快照,确认根进程没变、树里没有新冒出来的成员,再按 pid+启动时刻逐个发信号。

只有"跑一次就该结束"的签名(dotnet、Unity 批处理)设有存活上限 max_age_minutes:即使还在吃 CPU,
孤儿状态下累计观察超过上限也清掉(死循环、失控的构建)。脚本、Ghidra 分析、未知进程没有上限,闲置才处理。

终止方式:SIGTERM → 等 term_grace_seconds → 仍活着的 SIGKILL;每次发信号前都核对 pid 与启动时刻(防 PID 复用),
不按进程组发信号;单次最多处理 max_trees_per_run 棵树(限流,万一判定有误也不会一次杀一片)。

僵尸进程本身杀不掉(它只是进程表里的一个条目),只能等父进程收尸。这里催一次 SIGCHLD 并把迟迟不收尸的父进程报告出来;
父进程若恰好是本轮判定该清理的孤儿树根,会随整棵树消失,僵尸也就跟着没了。其余父进程一概不动。
"""
from __future__ import annotations

import ctypes
import errno
import os
import re
import signal
import struct
import time
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Pattern, Set, Tuple

import wd_core
import wd_util
from wd_core import Context, Proc

NOOP_EXES = ("sleep",)   # 这类成员只是空转,它们的出现与消失不算"进展"

# ---------------------------------------------------------------- 仪表:CPU / 磁盘(proc_pid_rusage)

_RU_FLAVOR = 2                      # RUSAGE_INFO_V2
_RU_FMT = "<16s18Q"                 # 160 字节:uuid + 18 个 uint64
_RU_SIZE = struct.calcsize(_RU_FMT)
_native: Optional[Tuple[Any, Tuple[int, int]]] = None
_native_failed = False


def _init_native() -> bool:
    global _native, _native_failed
    if _native is not None:
        return True
    if _native_failed:
        return False
    try:
        lib = ctypes.CDLL("/usr/lib/libSystem.B.dylib", use_errno=True)
        lib.proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
        lib.proc_pid_rusage.restype = ctypes.c_int

        class _TB(ctypes.Structure):
            _fields_ = [("numer", ctypes.c_uint32), ("denom", ctypes.c_uint32)]

        tb = _TB()
        lib.mach_timebase_info(ctypes.byref(tb))
        if tb.denom == 0:
            raise OSError("mach_timebase_info 返回了 denom=0")
        _native = (lib, (tb.numer, tb.denom))
        return True
    except (OSError, AttributeError):
        _native_failed = True
        return False


NATIVE_UNAVAILABLE = -1             # 仪表本身不可用(没有 libSystem / 符号缺失),与具体进程无关


def _rusage_raw(pid: int) -> Tuple[Optional[Tuple[float, int]], int]:
    """(读数, errno)。成功时 errno=0;失败时读数为 None,errno 说明原因:
    ESRCH=进程已经不存在,EPERM=进程活着但无权读取,NATIVE_UNAVAILABLE=仪表本身不可用。"""
    if not _init_native():
        return None, NATIVE_UNAVAILABLE
    lib, (num, den) = _native  # type: ignore[misc]
    buf = ctypes.create_string_buffer(_RU_SIZE)
    ctypes.set_errno(0)
    if lib.proc_pid_rusage(pid, _RU_FLAVOR, buf) != 0:
        return None, ctypes.get_errno() or errno.EIO
    v = struct.unpack_from(_RU_FMT, buf.raw)
    ticks = v[1] + v[2] + v[11] + v[12]      # 用户态+内核态,以及已回收子进程的用户态+内核态
    return (ticks * num / den / 1e9, v[17] + v[18]), 0


def rusage(pid: int) -> Optional[Tuple[float, int]]:
    """(累计 CPU 秒[本体 + 已回收的子进程], 累计磁盘读写字节);进程已消失或读不到时返回 None。"""
    return _rusage_raw(pid)[0]


def _gone(pid: int) -> bool:
    """进程确实已经不存在(不是"存在但没权限")。"""
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return True
    except OSError:
        return False                         # EPERM:存在,只是不归我们管
    return False


# ---------------------------------------------------------------- 仪表:网络(nettop)与环境(ps eww)

_NET_LINE = re.compile(r"^(.*)\.(\d+),(\d+),(\d+),?\s*$")


def read_net() -> Optional[Dict[int, int]]:
    """pid → 累计网络收发字节(没有 socket 的进程不在表里)。取不到返回 None。"""
    rc, out, _ = wd_util.run(["nettop", "-P", "-L", "1", "-x", "-J", "bytes_in,bytes_out"], timeout=30)
    if rc != 0 or not out.strip():
        return None
    res: Dict[int, int] = {}
    for line in out.splitlines():
        m = _NET_LINE.match(line)
        if m:
            pid = int(m.group(2))
            res[pid] = res.get(pid, 0) + int(m.group(3)) + int(m.group(4))
    return res


def ensure_net(ctx: Context) -> Optional[Dict[int, int]]:
    if not ctx.net_checked:
        ctx.net = read_net()
        ctx.net_checked = True
        if ctx.net is None:
            ctx.log.warn("取不到网络流量(nettop),本轮所有进程树都按'有进展'处理,不会因闲置被清理")
    return ctx.net


_LABEL_RE = re.compile(r"(?:^|\s)CURSOR_WORKSPACE_LABEL=(\S+)")


def read_env_flags(pids: List[int], markers: List[str]) -> Dict[int, Tuple[Set[str], Optional[str]]]:
    """pid → (环境里出现了哪些出身标记变量, 工作区标签的取值)。

    只判断变量名是否出现、并只取工作区标签这一个无敏感信息的值;环境的其余内容不读、不记、不落盘。
    被 SIP 保护的系统二进制(/bin/bash 等)看不到环境,但同一棵树里的 python、dotnet 等能看到,所以按树判断。
    """
    out: Dict[int, Tuple[Set[str], Optional[str]]] = {}
    marker_res = [(m, re.compile(r"(?:^|\s)%s=" % re.escape(m))) for m in markers]
    uniq = sorted(set(pids))
    for i in range(0, len(uniq), 150):
        chunk = uniq[i:i + 150]
        rc, text, _ = wd_util.run(["ps", "eww", "-o", "pid=,command=", "-p", ",".join(str(p) for p in chunk)], timeout=30)
        for line in text.splitlines():
            m = re.match(r"\s*(\d+)\s+(.*)$", line)
            if not m:
                continue
            body = m.group(2)
            present = {name for name, rx in marker_res if rx.search(body)}
            lm = _LABEL_RE.search(body)
            out[int(m.group(1))] = (present, lm.group(1) if lm else None)
    return out


# ---------------------------------------------------------------- 签名与保护名单

@dataclass
class Sig:
    name: str
    rx: Optional[Pattern[str]]
    trusted: bool
    action: str                     # kill / report / ignore
    max_age: Optional[float]        # 秒;None=无上限


def compile_policy(pcfg: Dict[str, Any]) -> Tuple[List[Sig], Pattern[str], Sig]:
    sigs: List[Sig] = []
    for s in list(pcfg["extra_signatures"]) + list(pcfg["signatures"]):     # 追加的排在前面,可以覆盖内置
        age = s.get("max_age_minutes")
        sigs.append(Sig(s["name"], re.compile(s["args"]), bool(s.get("trusted", False)), s.get("action", "kill"),
                        float(age) * 60 if age is not None else None))
    pats = list(pcfg["protected"]) + list(pcfg["extra_protected"])
    protected = re.compile("|".join("(?:%s)" % p for p in pats)) if pats else re.compile(r"(?!x)x")
    unknown = Sig("未知", None, False, "kill" if pcfg["unknown_orphans"] == "kill" else "report", None)
    return sigs, protected, unknown


def match_sig(sigs: List[Sig], cmd: str) -> Optional[Sig]:
    for s in sigs:
        if s.rx is not None and s.rx.search(cmd):
            return s
    return None


def exe_of(cmd: str) -> str:
    return cmd.split(" ", 1)[0].rsplit("/", 1)[-1]


def short(cmd: str, limit: int = 110) -> str:
    return wd_util.redact(cmd, limit)


# ---------------------------------------------------------------- 进程树的分类

@dataclass
class Tree:
    root: Proc
    members: List[Proc]
    sig: Sig
    protected: Optional[str] = None
    includes_self: bool = False
    in_scope: bool = False
    scope_why: str = ""
    provenance: bool = False
    prov_why: str = ""
    rec: Dict[str, Any] = field(default_factory=dict)
    progress_why: str = ""
    action: str = "keep"
    why: str = ""

    def label(self) -> str:
        return "pid %d [%s] 共 %d 个进程 | %s" % (self.root.pid, self.sig.name, len(self.members), short(self.root.cmd))


def collect_trees(ctx: Context, sigs: List[Sig], protected_rx: Pattern[str], unknown: Sig) -> List[Tree]:
    kids = wd_core.build_children(ctx.procs)
    trees: List[Tree] = []
    for r in sorted(ctx.procs.values(), key=lambda p: p.pid):
        if r.uid != ctx.uid or r.ppid != 1 or r.zombie or r.pid <= 1:
            continue
        members = wd_core.tree_of(ctx.procs, kids, r.pid)
        t = Tree(root=r, members=members, sig=match_sig(sigs, r.cmd) or unknown)
        t.includes_self = any(m.pid in ctx.self_pids for m in members)
        for m in members:
            hit = protected_rx.search(m.cmd)
            if hit:
                t.protected = "pid %d 命中保护名单(%s)" % (m.pid, hit.group(0)[:40])
                break
        trees.append(t)
    return trees


def classify(ctx: Context, t: Tree, env: Dict[int, Tuple[Set[str], Optional[str]]]) -> None:
    for m in t.members:
        markers, mlabel = env.get(m.pid, (set(), None))
        if markers and not t.provenance:
            t.provenance, t.prov_why = True, "pid %d 的环境带 %s" % (m.pid, sorted(markers)[0])
        if not t.in_scope:
            if ctx.scope.path_in(ctx.cwds.get(m.pid)):
                t.in_scope, t.scope_why = True, "pid %d 的工作目录在工程范围内" % m.pid
            elif ctx.scope.cmd_mentions(m.cmd):
                t.in_scope, t.scope_why = True, "pid %d 的命令行引用了工程路径" % m.pid
            elif ctx.label and mlabel == ctx.label:
                t.in_scope, t.scope_why = True, "pid %d 带有本工程的工作区标签" % m.pid


# ---------------------------------------------------------------- 观察与判定

def measure(ctx: Context, members: List[Proc]) -> Tuple[Dict[str, List[float]], Dict[str, str], List[str]]:
    """每个成员当前的 [CPU 秒, 磁盘字节, 网络字节]、可执行名,以及仪表不全的原因(空=齐全)。"""
    net = ensure_net(ctx)
    stats: Dict[str, List[float]] = {}
    exes: Dict[str, str] = {}
    gaps: List[str] = []
    if net is None:
        gaps.append("网络流量取不到")
    unreadable = 0
    for m in members:
        r, err = _rusage_raw(m.pid)
        if r is None:
            # 拿到快照之后才退出的短命成员(典型:循环里的 sleep)不是"仪表缺失",它已不属于这棵树,直接忽略;
            # 只有进程还活着却读不到(权限等)、或仪表本身不可用,才是真正的缺失——那时按"有进展"处理。
            if err == errno.ESRCH or (err != NATIVE_UNAVAILABLE and _gone(m.pid)):
                continue
            unreadable += 1
            continue
        stats[m.key] = [r[0], float(r[1]), float((net or {}).get(m.pid, 0))]
        exes[m.key] = exe_of(m.cmd)
    if unreadable:
        gaps.append("%d 个成员活着但读不到资源用量" % unreadable)
    return stats, exes, gaps


def observe(ctx: Context, t: Tree, old: Optional[Dict[str, Any]]) -> Tuple[Dict[str, Any], str]:
    """更新这棵树的观察记录。返回 (新记录, 本轮观察到的进展说明;空串=没有进展)。"""
    p = ctx.cfg["processes"]
    now = ctx.now_up
    stats, exes, gaps = measure(ctx, t.members)
    rec: Dict[str, Any] = {"first_up": now, "last_up": now, "progress_up": now, "stats": stats, "exes": exes}
    gap_limit = ctx.cfg["state_max_gap_minutes"] * 60
    valid = False
    first_up = last_up = progress_up = 0.0
    try:
        if isinstance(old, dict) and isinstance(old["stats"], dict) and isinstance(old.get("exes", {}), dict):
            first_up, last_up, progress_up = float(old["first_up"]), float(old["last_up"]), float(old["progress_up"])
            # 记录里的时刻不能晚于现在(醒着的时钟只会往前走),否则一定是坏数据
            valid = 0 <= now - last_up <= gap_limit and first_up <= now and progress_up <= now
    except (KeyError, TypeError, ValueError):
        valid = False
    if not valid or old is None:
        return rec, "首次观察(从现在开始计时)"
    rec["first_up"] = first_up
    rec["progress_up"] = progress_up
    if gaps:
        rec["progress_up"] = now
        return rec, "仪表不全(%s),按有进展处理" % "、".join(gaps)

    prev: Dict[str, List[float]] = old["stats"]
    prev_exes: Dict[str, str] = old.get("exes", {})
    d_cpu = d_io = d_net = 0.0
    added: List[str] = []
    try:
        for k, (c, i, n) in stats.items():
            pv = prev.get(k)
            if pv is None:                       # 新成员:它的全部用量都发生在上次观察之后
                d_cpu, d_io, d_net = d_cpu + c, d_io + i, d_net + n
                added.append(k)
            else:
                d_cpu += max(0.0, c - float(pv[0]))
                d_io += max(0.0, i - float(pv[1]))
                d_net += max(0.0, n - float(pv[2]))
    except (IndexError, TypeError, ValueError):   # 状态文件被改坏了:当作首次观察,从头计时
        return {"first_up": now, "last_up": now, "progress_up": now, "stats": stats, "exes": exes}, "观察记录损坏,重新计时"
    removed = [k for k in prev if k not in stats]
    n_added = len([k for k in added if exes.get(k) not in NOOP_EXES])
    n_removed = len([k for k in removed if prev_exes.get(k) not in NOOP_EXES])
    reasons: List[str] = []
    if n_added or n_removed:
        reasons.append("成员变动 +%d/-%d" % (n_added, n_removed))
    if d_cpu >= p["cpu_epsilon_seconds"]:
        reasons.append("CPU +%.1f 秒" % d_cpu)
    if d_io >= p["io_epsilon_mb"] * 1048576:
        reasons.append("磁盘 +%s" % wd_util.fmt_bytes(int(d_io)))
    if d_net >= p["net_epsilon_kb"] * 1024:
        reasons.append("网络 +%s" % wd_util.fmt_bytes(int(d_net)))
    if reasons:
        rec["progress_up"] = now
    return rec, "、".join(reasons)


def decide(ctx: Context, t: Tree) -> Tuple[str, str]:
    p = ctx.cfg["processes"]
    now = ctx.now_up
    observed = now - t.rec["first_up"]
    idle = now - t.rec["progress_up"]
    if observed < p["confirm_minutes"] * 60:
        return "keep", "刚开始观察(已观察 %s,至少 %s 之后才会判断)" % (wd_util.fmt_dur(observed), wd_util.fmt_dur(p["confirm_minutes"] * 60))
    if idle >= p["idle_minutes"] * 60:
        return "kill", "整棵进程树已连续 %s 毫无进展(阈值 %s)" % (wd_util.fmt_dur(idle), wd_util.fmt_dur(p["idle_minutes"] * 60))
    if t.sig.max_age is not None and observed >= t.sig.max_age:
        return "kill", "作为孤儿已存活 %s,超过 %s 的上限" % (wd_util.fmt_dur(observed), wd_util.fmt_dur(t.sig.max_age))
    detail = t.progress_why or "本轮没有进展"
    return "keep", "仍在活动(%s);已持续观察 %s,距上次进展 %s,闲置满 %s 才会清理" % (
        detail, wd_util.fmt_dur(observed), wd_util.fmt_dur(idle), wd_util.fmt_dur(p["idle_minutes"] * 60))


# ---------------------------------------------------------------- 动手

def _alive(expected: Dict[int, str]) -> Set[int]:
    """expected: pid → 启动时刻。返回仍然存活且确实是同一个进程(启动时刻一致)的 pid。"""
    if not expected:
        return set()
    rc, out, _ = wd_util.run(["ps", "-o", "pid=,stat=,lstart=", "-p", ",".join(str(p) for p in expected)], timeout=20)
    alive: Set[int] = set()
    for line in out.splitlines():
        parts = line.split(None, 7)
        if len(parts) < 7:
            continue
        try:
            pid = int(parts[0])
        except ValueError:
            continue
        if parts[1].startswith("Z"):
            continue
        if expected.get(pid) == " ".join(parts[2:7]):
            alive.add(pid)
    return alive


def _signal(ctx: Context, pids: Set[int], sig: int) -> None:
    for pid in sorted(pids):
        if pid <= 1 or pid in ctx.self_pids:
            continue
        try:
            os.kill(pid, sig)
        except ProcessLookupError:
            pass
        except PermissionError as e:
            ctx.log.warn("没有权限向 pid %d 发信号:%s" % (pid, e))


def recheck(ctx: Context, t: Tree, sigs: List[Sig]) -> Tuple[Optional[str], List[Proc]]:
    """动手前最后一道复核:重新取快照。返回 (放弃原因或 None, 要处理的成员)。"""
    try:
        fresh = wd_core.snapshot()
    except RuntimeError as e:
        return "复核时取不到进程快照:%s" % e, []
    root = fresh.get(t.root.pid)
    if root is None or root.lstart != t.root.lstart or root.ppid != 1 or root.zombie:
        return "根进程已经变化或退出", []
    members = wd_core.tree_of(fresh, wd_core.build_children(fresh), root.pid)
    before = {m.key for m in t.members}
    fresh_new = [m for m in members if m.key not in before and exe_of(m.cmd) not in NOOP_EXES]
    if fresh_new:
        return "判定之后进程树里冒出了新成员(pid %s),说明仍有活动" % ",".join(str(m.pid) for m in fresh_new[:5]), []
    if any(m.pid in ctx.self_pids for m in members):
        return "树里包含看门狗自身", []
    victims = []
    for m in members:
        s = match_sig(sigs, m.cmd)
        if s is not None and s.action == "ignore":
            continue                        # 共享的编译服务器等:即使挂在这棵树下也不杀
        victims.append(m)
    return None, victims


def terminate(ctx: Context, victims: List[Proc]) -> Tuple[int, int]:
    """SIGTERM → 等待 → SIGKILL。返回 (已终止数, 杀不掉的数)。每次发信号前都核对 pid+启动时刻。"""
    expected = {m.pid: m.lstart for m in victims}
    _signal(ctx, _alive(expected), signal.SIGTERM)
    stopped = {m.pid for m in victims if m.stopped}
    if stopped:                                  # 被 SIGSTOP 暂停的进程要先恢复,才会处理挂起的 SIGTERM
        _signal(ctx, stopped & _alive(expected), signal.SIGCONT)
    grace = float(ctx.cfg["processes"]["term_grace_seconds"])
    t0 = time.time()
    while time.time() - t0 < grace:
        if not _alive(expected):
            break
        time.sleep(0.5)
    left = _alive(expected)
    if left:
        _signal(ctx, left, signal.SIGKILL)
        time.sleep(1.0)
        left = _alive(expected)
    return len(expected) - len(left), len(left)


# ---------------------------------------------------------------- 僵尸

def handle_zombies(ctx: Context, protected_rx: Pattern[str]) -> None:
    zc = ctx.cfg["zombies"]
    if not zc["enabled"]:
        return
    now = ctx.now_up
    gap_limit = ctx.cfg["state_max_gap_minutes"] * 60
    zs = [p for p in ctx.procs.values() if p.zombie and p.uid == ctx.uid]
    old = ctx.state.get("zombies", {})
    new: Dict[str, Any] = {}
    parents = sorted({z.ppid for z in zs if z.ppid > 1 and z.ppid in ctx.procs})
    env = read_env_flags(parents, ctx.cfg["processes"]["agent_env_markers"]) if parents else {}
    unrelated = 0
    for z in zs:
        parent = ctx.procs.get(z.ppid)
        if parent is None or parent.pid <= 1 or parent.uid != ctx.uid:
            continue                                        # 父进程是 launchd 或已不在:系统会自己收尸
        markers, mlabel = env.get(parent.pid, (set(), None))
        related = bool(markers) or ctx.scope.path_in(ctx.cwds.get(parent.pid)) or ctx.scope.cmd_mentions(parent.cmd) \
            or bool(ctx.label and mlabel == ctx.label)
        protected = bool(protected_rx.search(parent.cmd))
        if not related and not protected:
            unrelated += 1
            continue
        prev = old.get(z.key) if isinstance(old, dict) else None
        first_up, nudged = now, False
        try:
            if isinstance(prev, dict):
                f, last = float(prev["first_up"]), float(prev["last_up"])
                if 0 <= now - last <= gap_limit and f <= now:
                    first_up, nudged = f, bool(prev.get("nudged"))
        except (KeyError, TypeError, ValueError):
            pass                                            # 记录坏了:当作刚出现,重新计时
        rec: Dict[str, Any] = {"first_up": first_up, "last_up": now, "nudged": nudged}
        age = now - rec["first_up"]
        name = z.cmd.strip("()") or "?"
        target = "僵尸 pid %d (%s),父进程 pid %d | %s" % (z.pid, name, parent.pid, short(parent.cmd, 70))
        if age < zc["nudge_after_minutes"] * 60:
            ctx.report.add("zombie", "keep", target, "刚出现(已观察 %s),先等父进程自己收尸" % wd_util.fmt_dur(age))
        elif not rec["nudged"]:
            if ctx.act("zombie", target, "已存在 %s 仍未被收尸,给父进程发 SIGCHLD 催一下" % wd_util.fmt_dur(age)) == "done":
                if _alive({parent.pid: parent.lstart}):
                    _signal(ctx, {parent.pid}, signal.SIGCHLD)
                    rec["nudged"] = True
        else:
            ctx.report.add("zombie", "report", target,
                           "已催过仍未被收尸(已 %s):僵尸本身无法被杀,需要父进程收尸或退出%s" % (
                               wd_util.fmt_dur(age), ";父进程受保护,不会动它" if protected else ""))
        new[z.key] = rec
    ctx.state["zombies"] = new
    if unrelated:
        ctx.log.info("另有 %d 个僵尸属于与本工程无关的进程,不处理" % unrelated)


# ---------------------------------------------------------------- 入口

def run(ctx: Context) -> None:
    pcfg = ctx.cfg["processes"]
    if not pcfg["enabled"]:
        ctx.report.add("proc", "skip", "进程清理", "配置里已关闭")
        return
    sigs, protected_rx, unknown = compile_policy(pcfg)
    markers: List[str] = pcfg["agent_env_markers"]
    for bad in ctx.scope.rejected:
        ctx.log.warn("范围前缀过浅,已拒收:%s" % bad)

    trees = collect_trees(ctx, sigs, protected_rx, unknown)
    candidates = [t for t in trees if not t.protected and t.sig.action != "ignore" and not t.includes_self]
    env = read_env_flags([m.pid for t in candidates for m in t.members], markers)

    judged: List[Tree] = []
    unrelated = 0
    for t in candidates:
        classify(ctx, t, env)
        if not t.in_scope:
            unrelated += 1
            continue
        if t.sig.action == "report":
            ctx.report.add("proc", "report", t.label(), "%s:只报告,不处理(%s)" % (t.sig.name, t.scope_why))
        elif not t.sig.trusted and not t.provenance:
            ctx.report.add("proc", "keep", t.label(), "没有 agent 出身证明(环境里没有 %s),不动" % "/".join(markers))
        else:
            judged.append(t)

    old = ctx.state.get("procs", {})
    new_state: Dict[str, Any] = {}
    for t in judged:
        t.rec, t.progress_why = observe(ctx, t, old.get(t.root.key) if isinstance(old, dict) else None)
        t.action, t.why = decide(ctx, t)
        new_state[t.root.key] = t.rec

    kills = sorted((t for t in judged if t.action == "kill"), key=lambda t: t.rec["progress_up"])
    max_trees = int(pcfg["max_trees_per_run"])
    done = 0
    for t in judged:
        if t.action != "kill":
            ctx.report.add("proc", "keep", t.label(), t.why)
    for idx, t in enumerate(kills):
        if idx >= max_trees:
            ctx.report.add("proc", "skip", t.label(), "本轮清理数已达上限 %d,留到下一轮" % max_trees)
            continue
        if ctx.deadline.expired():
            ctx.report.add("proc", "skip", t.label(), "本轮时间预算用完,留到下一轮")
            continue
        why_not, victims = recheck(ctx, t, sigs)
        if why_not:
            ctx.report.add("proc", "keep", t.label(), "放弃清理:" + why_not)
            continue
        if ctx.act("proc", t.label(), t.why + ";%s;%s" % (t.scope_why, t.prov_why or "签名免出身证明")) == "done":
            killed, stubborn = terminate(ctx, victims)
            ctx.log.act("pid %d 这一棵:已终止 %d 个进程%s" % (t.root.pid, killed, ",仍有 %d 个杀不掉" % stubborn if stubborn else ""))
            if stubborn:
                ctx.report.add("proc", "warn", t.label(), "有 %d 个进程 SIGKILL 之后仍在(可能卡在内核态)" % stubborn)
        new_state.pop(t.root.key, None)
        done += 1
    ctx.state["procs"] = new_state
    skipped = len(trees) - len(candidates)
    ctx.log.info("进程:孤儿树 %d 棵(受保护/共享/自身 %d,与本工程无关 %d,本工程相关 %d),进入判定 %d 棵,%s %d 棵" % (
        len(trees), skipped, unrelated, len(candidates) - unrelated, len(judged),
        "将清理" if ctx.dry_run else "已清理", done))
    handle_zombies(ctx, protected_rx)
