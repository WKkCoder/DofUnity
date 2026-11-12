#!/usr/bin/env python3
"""看门狗的数据层:进程快照、cwd、"属于本工程"的范围判断、git worktree 清单、运行报告与上下文。

这一层只读、不动任何东西;杀进程与删文件都在 wd_procs / wd_garbage 里,并且都要先过这里给出的范围判断。
"""
from __future__ import annotations

import os
import re
import time
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, List, Optional, Sequence, Set

import wd_util

PS_COLUMNS = "pid=,ppid=,pgid=,uid=,stat=,etime=,time=,rss=,lstart=,command="


# ---------------------------------------------------------------- 进程快照

@dataclass
class Proc:
    pid: int
    ppid: int
    pgid: int
    uid: int
    stat: str
    etime: float      # ps 的存活时间(挂钟,含休眠):只作展示与保守估计,判定用的是自己的观察时钟
    cpu: float        # 累计 CPU 秒(用户+系统)
    rss_kb: int
    lstart: str       # 启动时刻,"pid:lstart" 唯一标识一个进程,防止 PID 复用时认错人
    cmd: str          # 完整命令行(僵尸进程是 "(名字)")

    @property
    def key(self) -> str:
        return "%d:%s" % (self.pid, self.lstart)

    @property
    def zombie(self) -> bool:
        return self.stat.startswith("Z")

    @property
    def stopped(self) -> bool:
        return "T" in self.stat


def parse_ps(text: str) -> Dict[int, Proc]:
    procs: Dict[int, Proc] = {}
    for line in text.splitlines():
        # pid ppid pgid uid stat etime time rss + lstart 的 5 个词(星期 月 日 时间 年) + 命令行(其余部分)
        parts = line.split(None, 13)
        if len(parts) < 13:
            continue
        try:
            pid, ppid, pgid, uid = (int(x) for x in parts[:4])
            procs[pid] = Proc(pid, ppid, pgid, uid, parts[4], wd_util.parse_clock(parts[5]),
                              wd_util.parse_clock(parts[6]), int(parts[7]), " ".join(parts[8:13]),
                              parts[13] if len(parts) > 13 else "")
        except ValueError:
            continue
    return procs


def snapshot() -> Dict[int, Proc]:
    rc, out, err = wd_util.run(["ps", "-axww", "-o", PS_COLUMNS], timeout=45)
    procs = parse_ps(out)
    if not procs:
        raise RuntimeError("ps 没有返回任何进程(rc=%s):%s" % (rc, err.strip()[:200]))
    return procs


def read_cwds(uid: int) -> Dict[int, str]:
    """一次 lsof 取回本用户全部进程的 cwd(约 1 秒)。取不到的进程不在结果里,后续只靠命令行与环境判断。"""
    rc, out, _ = wd_util.run(["lsof", "-a", "-d", "cwd", "-u", str(uid), "-Fpn"], timeout=60)
    cwds: Dict[int, str] = {}
    cur: Optional[int] = None
    for line in out.splitlines():
        if line.startswith("p"):
            try:
                cur = int(line[1:])
            except ValueError:
                cur = None
        elif line.startswith("n") and cur is not None:
            cwds[cur] = line[1:]
    return cwds


def start_epoch(lstart: str) -> Optional[int]:
    """ps 的 lstart("Wed Nov 11 10:48:23 2026")转 epoch 秒;解析失败返回 None。"""
    try:
        return int(time.mktime(time.strptime(lstart, "%a %b %d %H:%M:%S %Y")))
    except ValueError:
        return None


def build_children(procs: Dict[int, Proc]) -> Dict[int, List[int]]:
    kids: Dict[int, List[int]] = {}
    for p in procs.values():
        if p.ppid != p.pid:
            kids.setdefault(p.ppid, []).append(p.pid)
    return kids


def tree_of(procs: Dict[int, Proc], kids: Dict[int, List[int]], root: int) -> List[Proc]:
    """root 及其全部存活后代(不含僵尸,它们没有自己的活动,随父进程消失)。防环。"""
    out: List[Proc] = []
    seen: Set[int] = set()
    stack = [root]
    while stack:
        pid = stack.pop()
        if pid in seen or pid not in procs:
            continue
        seen.add(pid)
        p = procs[pid]
        if not p.zombie:
            out.append(p)
        stack.extend(kids.get(pid, ()))
    return out


def ancestors(procs: Dict[int, Proc], pid: int) -> Set[int]:
    out: Set[int] = set()
    while pid in procs and pid not in out and pid > 1:
        out.add(pid)
        pid = procs[pid].ppid
    return out


# ---------------------------------------------------------------- 范围

def _variants(path: str) -> Set[str]:
    """同一个目录的各种写法:原样、绝对化、真实路径,以及 macOS 上 /tmp /var /etc 与 /private/... 的互换写法。"""
    out = {os.path.abspath(path), os.path.realpath(path)}
    for p in list(out):
        for pre in ("/tmp", "/var", "/etc"):
            if p == pre or p.startswith(pre + "/"):
                out.add("/private" + p)
        if p.startswith("/private/"):
            rest = p[len("/private"):]
            for pre in ("/tmp", "/var", "/etc"):
                if rest == pre or rest.startswith(pre + "/"):
                    out.add(rest)
    return out


class Scope:
    """"本工程的地盘":一组目录前缀。进程的 cwd 在其中,或命令行里按路径边界提到了其中某个目录,就算在范围内。

    - 命令行里常以符号链接的写法出现(例如 /tmp/xxx 实际指向仓库里的目录),所以会扫描 alias_dirs 下
      指向地盘之内的符号链接,把它们的路径也算进来;
    - 太浅的前缀(如 / 或 /tmp,不足两级)一律拒收并记入 rejected:配错一个根就让全机进程都"在范围内"太危险。
    """

    def __init__(self, roots: Iterable[str], alias_dirs: Sequence[str] = ("/tmp",)) -> None:
        self.rejected: List[str] = []
        real_roots: Set[str] = set()
        variants: Set[str] = set()
        for r in roots:
            if not r:
                continue
            if len([c for c in os.path.abspath(r).split("/") if c]) < 2:
                self.rejected.append(r)
                continue
            variants |= _variants(r)
            real_roots.add(os.path.realpath(r))
        for d in alias_dirs:
            for base in {d, "/private" + d if not d.startswith("/private") else d}:
                try:
                    entries = list(os.scandir(base))
                except OSError:
                    continue
                for e in entries:
                    try:
                        if e.is_symlink() and any(wd_util.path_within(os.path.realpath(e.path), rr) for rr in real_roots):
                            variants |= _variants(e.path)
                    except OSError:
                        continue
        self.roots: List[str] = sorted({v.rstrip("/") for v in variants if v.rstrip("/")}, key=len, reverse=True)
        alts = "|".join(re.escape(r) for r in self.roots)
        self._cmd_re = re.compile(r"(?<![\w.~/-])(?:%s)(?=[/\s'\"=:,;)]|$)" % alts) if alts else None

    def path_in(self, path: Optional[str]) -> bool:
        return bool(path) and any(wd_util.path_within(path, r) for r in self.roots)

    def cmd_mentions(self, cmd: str) -> bool:
        return bool(self._cmd_re and self._cmd_re.search(cmd or ""))


# ---------------------------------------------------------------- git worktree

@dataclass
class Worktree:
    path: str
    head: str
    branch: Optional[str]
    detached: bool
    locked: bool
    prunable: bool
    main: bool


def list_worktrees(repo_root: str) -> List[Worktree]:
    rc, out, _ = wd_util.run(["git", "-C", repo_root, "worktree", "list", "--porcelain"], timeout=30)
    res: List[Worktree] = []
    if rc != 0:
        return res
    for i, block in enumerate(b for b in out.strip().split("\n\n") if b.strip()):
        path = head = ""
        branch: Optional[str] = None
        detached = locked = prunable = False
        for line in block.splitlines():
            if line.startswith("worktree "):
                path = line[len("worktree "):]
            elif line.startswith("HEAD "):
                head = line[len("HEAD "):]
            elif line.startswith("branch "):
                ref = line[len("branch "):]
                branch = ref[len("refs/heads/"):] if ref.startswith("refs/heads/") else ref
            elif line == "detached":
                detached = True
            elif line.startswith("locked"):
                locked = True
            elif line.startswith("prunable"):
                prunable = True
        if path:
            res.append(Worktree(path, head, branch, detached, locked, prunable, main=(i == 0)))
    return res


def find_scratch_clones(repo_root: str, worktrees: List[Worktree]) -> List[str]:
    """与主仓库同源(根提交相同)、但没有登记成 worktree 的克隆目录。

    只在 linked worktree 常用的"基地目录"里找一层;主仓库的同级目录(通常放着别的项目)和家目录一律不扫。
    克隆的 .git 是目录,linked worktree 的 .git 是文件,以此区分。
    """
    known = {os.path.realpath(w.path) for w in worktrees}
    main_parent = os.path.dirname(os.path.realpath(repo_root))
    home = os.path.realpath(os.path.expanduser("~"))
    bases = set()
    for w in worktrees:
        if w.main:
            continue
        base = os.path.dirname(os.path.realpath(w.path))
        if base not in (main_parent, home, "/"):
            bases.add(base)
    if not bases:
        return []
    rc, out, _ = wd_util.run(["git", "-C", repo_root, "rev-list", "--max-parents=0", "HEAD"], timeout=30)
    roots = set(out.split()) if rc == 0 else set()
    if not roots:
        return []
    found: List[str] = []
    for base in sorted(bases):
        try:
            entries = sorted(os.scandir(base), key=lambda e: e.name)
        except OSError:
            continue
        for e in entries:
            real = os.path.realpath(e.path)
            if real in known or not e.is_dir(follow_symlinks=False) or not os.path.isdir(os.path.join(real, ".git")):
                continue
            rc, o, _ = wd_util.run(["git", "-C", real, "rev-list", "--max-parents=0", "HEAD"], timeout=15)
            if rc == 0 and roots & set(o.split()):
                found.append(real)
    return found


# ---------------------------------------------------------------- 报告与上下文

class Deadline:
    def __init__(self, seconds: float) -> None:
        self.end = time.time() + seconds

    def expired(self) -> bool:
        return time.time() >= self.end


class Report:
    """一次运行里发生(或在 dry-run 下将会发生)的每一件事。

    action:done 已执行 / would dry-run 下会执行 / keep 保留(并说明为什么) / report 只报告 / skip 跳过 / warn / error
    """

    def __init__(self) -> None:
        self.items: List[Dict[str, Any]] = []

    def add(self, area: str, action: str, target: str, why: str = "", size: int = 0, **extra: Any) -> None:
        item = {"area": area, "action": action, "target": target, "why": why, "size": int(size)}
        item.update(extra)
        self.items.append(item)

    def select(self, area: Optional[str] = None, action: Optional[str] = None) -> List[Dict[str, Any]]:
        return [i for i in self.items if (area is None or i["area"] == area) and (action is None or i["action"] == action)]

    def freed(self) -> int:
        return sum(i["size"] for i in self.items if i["action"] == "done")

    def would_free(self) -> int:
        return sum(i["size"] for i in self.items if i["action"] == "would")


@dataclass
class Context:
    cfg: Dict[str, Any]
    log: "wd_util.Log"
    dry_run: bool
    uid: int
    now_up: float                 # 醒着的时钟
    now_wall: float
    sleep_total: float
    procs: Dict[int, Proc]
    cwds: Dict[int, str]
    repo_root: str
    label: str
    worktrees: List[Worktree]
    clones: List[str]             # 草稿克隆:与主仓库同源但不是 worktree
    scope: Scope
    state: Dict[str, Any]
    report: Report
    deadline: Deadline
    self_pids: Set[int] = field(default_factory=set)
    deep: bool = False            # scan --deep:把平时不做的重活(worktree 清点)也做一遍
    net: Optional[Dict[int, int]] = None   # pid → 累计网络收发字节(nettop);None=本轮取不到
    net_checked: bool = False

    def act(self, area: str, target: str, why: str, size: int = 0, **extra: Any) -> str:
        """记录一次动作并返回 'done' 或 'would'(调用方据此决定是否真的执行)。"""
        action = "would" if self.dry_run else "done"
        self.report.add(area, action, target, why, size, **extra)
        self.log.act("%s%s [%s] %s%s" % ("(dry-run) " if self.dry_run else "", target, area, why,
                                         (" 释放 %s" % wd_util.fmt_bytes(size)) if size else ""))
        return action
