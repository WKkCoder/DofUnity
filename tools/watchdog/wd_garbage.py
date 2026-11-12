#!/usr/bin/env python3
"""垃圾资源清理。只删"可以证明已经没人需要"的东西;拿不准就留着。

每一类都写明了删除的依据:

  IPC 残留   .NET 运行时为每个进程建的调试管道(clr-debug-pipe-<pid>-<键>-in/out)、诊断套接字
             (dotnet-diagnostic-<pid>-<键>-socket)和 MSBuild 节点套接字(/tmp/MSBuild<pid>)。进程被 kill -9 时来不及删,
             一台机器上两小时就能积出五百多个。键是属主的启动时刻(epoch 秒,已用活进程逐个验证过),
             所以"pid 不存在,或 pid 存在但启动时刻对不上(pid 被复用了)"就是属主已死。文件还必须是本用户的、
             类型对(fifo/socket)、存在超过 ipc_min_age_seconds,并且用的是刚取的进程快照,避开刚启动的属主。
  MSBuildTemp*  空目录且存在超过 N 小时才删,用 rmdir(非空会失败,绝不递归删除)。
  锁         ① heavy.sh 的锁目录:持有者 pid 已死,或活着但命令行不是 heavy.sh(pid 被复用,heavy.sh 自己只用 kill -0 判断,
             会因此被永远卡住)。② git 的锁文件:只认白名单里的名字(绝不碰 .git/cursor/ 之类别人的锁),
             存在超过 N 分钟,且当前没有任何 git 进程在本工程里跑。
  worktree 登记  git worktree prune:只清理"目录已经不存在"的登记。
  构建产物   长期闲置的 worktree / 草稿克隆里的 bin/。整棵树必须同时满足:没有进程在用、git 索引/HEAD/bin/obj
             都超过 N 小时没动;并且 bin/ 本身必须是某个 *.csproj 旁边的、被 git 忽略的真实目录(不是符号链接)。
             obj/ 默认保留:agent 常用 --no-restore 构建,obj 里的 project.assets.json 删了就构建不了。
             主工作区永远不动(那是人在用的)。

从不自动删除 worktree 或克隆本身:git worktree remove 会连同被忽略的 HANDOFF.json、re/、out/ 一起删掉,
而绝大多数 worktree 都有未提交的改动或未合并的提交。移除 worktree 只有显式打开 garbage.worktrees.remove_merged_clean 才会做,
并且要求"已并入主分支、干净、没有交接/产物文件、没人在用、闲置够久、未锁定",且不加 --force。
"""
from __future__ import annotations

import fnmatch
import glob
import os
import re
import shutil
import stat
import tempfile
import time
from collections import Counter
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

import wd_core
import wd_util
from wd_core import Context, Proc

IPC_PATTERNS = [
    ("clr-debug-pipe", re.compile(r"^clr-debug-pipe-(\d+)-(\d+)-(?:in|out)$"), stat.S_ISFIFO),
    ("dotnet-diagnostic", re.compile(r"^dotnet-diagnostic-(\d+)-(\d+)-socket$"), stat.S_ISSOCK),
    ("MSBuild 节点套接字", re.compile(r"^MSBuild(\d+)$"), stat.S_ISSOCK),
]
GIT_TOP_LOCKS = ("index.lock", "HEAD.lock", "packed-refs.lock", "config.lock", "shallow.lock")
PROJECT_SUFFIXES = (".csproj", ".fsproj", ".vbproj")
SKIP_DIRS = {"node_modules", "Library", "re", "out", ".git", "Temp", "Logs", "obj", "bin", "Builds"}


# ---------------------------------------------------------------- 公共

def tmp_roots(ctx: Context) -> List[str]:
    explicit = ctx.cfg["garbage"]["tmp_dirs"]
    if explicit is not None:
        cands = [os.path.expanduser(x) for x in explicit]
    else:
        cands = [tempfile.gettempdir(), "/tmp"]
        rc, out, _ = wd_util.run(["getconf", "DARWIN_USER_TEMP_DIR"], timeout=5)
        if rc == 0 and out.strip():
            cands.append(out.strip())
    seen: Set[str] = set()
    res: List[str] = []
    for d in cands:
        real = os.path.realpath(d)
        if real not in seen and os.path.isdir(real):
            seen.add(real)
            res.append(real)
    return res


def owner_alive(pid: int, key: Optional[int], procs: Dict[int, Proc]) -> bool:
    """属主是否还活着。拿不准一律当作活着(宁可留着垃圾,也不删活进程的文件)。"""
    p = procs.get(pid)
    if p is None:
        return False
    if key is None:
        return True                              # 没有键可比对:pid 还在就保留
    if not (1_000_000_000 < key < 4_000_000_000):
        return True                              # 键不像 epoch 秒:格式变了,不敢判断
    ep = wd_core.start_epoch(p.lstart)
    if ep is None:
        return True
    return abs(ep - key) <= 2                    # 启动时刻对不上 = pid 已经被别的进程复用,原属主确实死了


def _safe(ctx: Context, name: str, fn: Callable[..., None], *args: Any) -> None:
    """一类清理出了意外不能拖垮其它类。"""
    try:
        fn(ctx, *args)
    except Exception as e:   # noqa: BLE001 —— 看门狗必须往下走
        ctx.log.error("垃圾清理「%s」出错:%r" % (name, e))
        ctx.report.add("garbage", "error", name, "出错:%r" % (e,))


# ---------------------------------------------------------------- IPC 残留与 MSBuildTemp

def clean_ipc(ctx: Context, procs: Dict[int, Proc]) -> None:
    g = ctx.cfg["garbage"]
    now = time.time()
    victims: List[Tuple[str, str]] = []
    for d in tmp_roots(ctx):
        try:
            entries = list(os.scandir(d))
        except OSError:
            continue
        for e in entries:
            for kind, rx, is_type in IPC_PATTERNS:
                m = rx.match(e.name)
                if m:
                    break
            else:
                continue
            try:
                st = e.stat(follow_symlinks=False)
            except OSError:
                continue
            if st.st_uid != ctx.uid or not is_type(st.st_mode) or now - st.st_mtime < g["ipc_min_age_seconds"]:
                continue
            pid = int(m.group(1))
            key = int(m.group(2)) if m.re.groups >= 2 else None
            if not owner_alive(pid, key, procs):
                victims.append((e.path, kind))
    if not victims:
        return
    by = Counter(k for _, k in victims)
    detail = "、".join("%s %d 个" % (k, n) for k, n in sorted(by.items()))
    if ctx.act("garbage", "IPC 残留文件共 %d 个" % len(victims), "属主进程已不存在或 pid 已被复用:" + detail) == "done":
        removed = 0
        for path, _ in victims:
            try:
                os.unlink(path)
                removed += 1
            except OSError:
                pass
        ctx.log.info("IPC 残留:实际删除 %d/%d 个" % (removed, len(victims)))


def clean_msbuild_temp(ctx: Context) -> None:
    hours = ctx.cfg["garbage"]["msbuild_temp_empty_hours"]
    if not hours:
        return
    now = time.time()
    rx = re.compile(r"^MSBuildTemp[A-Za-z0-9._-]*$")
    victims: List[str] = []
    for d in tmp_roots(ctx):
        try:
            entries = list(os.scandir(d))
        except OSError:
            continue
        for e in entries:
            if not rx.match(e.name):
                continue
            try:
                st = e.stat(follow_symlinks=False)
                if not stat.S_ISDIR(st.st_mode) or st.st_uid != ctx.uid or now - st.st_mtime < hours * 3600:
                    continue
                if os.listdir(e.path):
                    continue
            except OSError:
                continue
            victims.append(e.path)
    if not victims:
        return
    if ctx.act("garbage", "空的 MSBuildTemp 目录共 %d 个" % len(victims), "空目录且存在超过 %s 小时" % hours) == "done":
        removed = 0
        for p in victims:
            try:
                os.rmdir(p)          # 非空会失败:绝不递归删除
                removed += 1
            except OSError:
                pass
        ctx.log.info("MSBuildTemp:实际删除 %d/%d 个" % (removed, len(victims)))


# ---------------------------------------------------------------- 锁

def lock_roots(ctx: Context) -> List[str]:
    explicit = ctx.cfg["garbage"]["lock_dirs"]
    if explicit is not None:
        cands = [os.path.expanduser(x) for x in explicit]
    else:
        cands = glob.glob(os.path.join(ctx.repo_root, "re", "*", "locks")) + glob.glob("/tmp/*/locks")
    seen: Set[str] = set()
    res: List[str] = []
    for d in cands:
        real = os.path.realpath(d)
        if real not in seen and os.path.isdir(real):
            seen.add(real)
            res.append(real)
    return res


def clean_heavy_locks(ctx: Context, procs: Dict[int, Proc]) -> None:
    g = ctx.cfg["garbage"]
    holder_rx = re.compile(g["lock_holder_pattern"])
    now = time.time()
    for root in lock_roots(ctx):
        try:
            entries = sorted(os.scandir(root), key=lambda e: e.name)
        except OSError:
            continue
        for e in entries:
            if not fnmatch.fnmatch(e.name, "*.lock*.d"):
                continue
            try:
                st = e.stat(follow_symlinks=False)
            except OSError:
                continue
            if not stat.S_ISDIR(st.st_mode) or st.st_uid != ctx.uid or now - st.st_mtime < g["lock_stale_seconds"]:
                continue
            holder: Optional[int] = None
            try:
                with open(os.path.join(e.path, "pid"), "r", encoding="utf-8") as f:
                    holder = int(f.read().strip())
            except (OSError, ValueError):
                holder = None
            if holder is None:
                why = "没有有效的 pid 文件,且已存在 %s" % wd_util.fmt_dur(now - st.st_mtime)
            elif holder not in procs:
                why = "持有者 pid %d 已不存在" % holder
            elif not holder_rx.search(procs[holder].cmd):
                why = "持有者 pid %d 已被无关进程复用(%s)" % (holder, wd_util.redact(procs[holder].cmd, 50))
            else:
                continue                                  # 持有者还活着,是个正常的锁
            if ctx.act("garbage", "陈旧锁目录 %s" % e.name, why) == "done":
                real = os.path.realpath(e.path)
                if os.path.islink(e.path) or os.path.basename(os.path.dirname(real)) != "locks":
                    ctx.log.warn("锁目录路径异常,放弃删除:%s" % e.name)
                    continue
                try:
                    shutil.rmtree(e.path)
                except OSError as err:
                    ctx.log.warn("删除锁目录 %s 失败:%s" % (e.name, err))


def git_lock_candidates(common_dir: str) -> List[str]:
    cands = [os.path.join(common_dir, n) for n in GIT_TOP_LOCKS]
    for dp, dn, fn in os.walk(os.path.join(common_dir, "refs")):
        cands.extend(os.path.join(dp, f) for f in fn if f.endswith(".lock"))
    wt = os.path.join(common_dir, "worktrees")
    try:
        for name in os.listdir(wt):
            cands.extend(os.path.join(wt, name, n) for n in ("index.lock", "HEAD.lock"))
    except OSError:
        pass
    return [c for c in cands if os.path.isfile(c) and not os.path.islink(c)]


def clean_git_locks(ctx: Context, procs: Dict[int, Proc]) -> None:
    stale_s = ctx.cfg["garbage"]["git_lock_stale_minutes"] * 60
    common = os.path.join(ctx.repo_root, ".git")
    if not os.path.isdir(common):
        return
    now = time.time()
    old: List[str] = []
    ages: Dict[str, float] = {}
    for p in git_lock_candidates(common):
        try:
            ages[p] = now - os.stat(p).st_mtime
        except OSError:
            continue                                      # 刚被别人删了:正好
        if ages[p] >= stale_s:
            old.append(p)
    if not old:
        return
    for p in procs.values():
        exe = p.cmd.split(" ", 1)[0].rsplit("/", 1)[-1]
        if p.uid == ctx.uid and not p.zombie and (exe == "git" or exe.startswith("git-")):
            if ctx.scope.path_in(ctx.cwds.get(p.pid)) or ctx.scope.cmd_mentions(p.cmd):
                ctx.report.add("garbage", "keep", "过期的 git 锁文件 %d 个" % len(old), "工程里有 git 进程(pid %d)在跑,这一轮不动" % p.pid)
                return
    for p in old:
        rel = os.path.relpath(p, common)
        if ctx.act("garbage", "陈旧的 git 锁 .git/%s" % rel, "存在超过 %s 且没有 git 进程在跑" % wd_util.fmt_dur(ages[p])) == "done":
            try:
                os.unlink(p)
            except OSError as err:
                ctx.log.warn("删除 %s 失败:%s" % (rel, err))


def prune_worktrees(ctx: Context) -> None:
    args = ["git", "-C", ctx.repo_root, "worktree", "prune", "--expire", "6.hours.ago"]
    rc, out, err = wd_util.run(args + ["--dry-run", "-v"], timeout=60)
    lines = [ln for ln in (out + "\n" + err).splitlines() if ln.strip()]
    if rc != 0 or not lines:
        return
    if ctx.act("garbage", "失效的 worktree 登记 %d 条" % len(lines), "对应目录已不存在:" + wd_util.redact(lines[0], 90)) == "done":
        rc2, _, err2 = wd_util.run(args, timeout=60)
        if rc2 != 0:
            ctx.log.warn("git worktree prune 失败:%s" % err2.strip()[:200])


# ---------------------------------------------------------------- 构建产物

def find_projects(root: str, depth: int = 4) -> List[str]:
    res: List[str] = []

    def walk(d: str, lvl: int) -> None:
        try:
            ents = list(os.scandir(d))
        except OSError:
            return
        if any(e.name.endswith(PROJECT_SUFFIXES) for e in ents):
            res.append(d)
        if lvl >= depth:
            return
        for e in ents:
            if e.name not in SKIP_DIRS and e.is_dir(follow_symlinks=False):
                walk(e.path, lvl + 1)

    walk(root, 0)
    return res


def dir_size(path: str) -> int:
    total = 0
    for dp, dn, fn in os.walk(path):
        for f in fn:
            try:
                total += os.lstat(os.path.join(dp, f)).st_size
            except OSError:
                pass
    return total


def has_recent(path: str, threshold: float) -> bool:
    """目录里有没有比 threshold 更新的文件(找到第一个就返回,活跃的目录几乎不用遍历)。"""
    for dp, dn, fn in os.walk(path):
        for f in fn:
            try:
                if os.lstat(os.path.join(dp, f)).st_mtime >= threshold:
                    return True
            except OSError:
                pass
    return False


def git_admin_dir(root: str) -> Optional[str]:
    g = os.path.join(root, ".git")
    if os.path.isdir(g):
        return g
    try:
        with open(g, "r", encoding="utf-8") as f:
            m = re.match(r"gitdir:\s*(.+)", f.read().strip())
    except OSError:
        return None
    if not m:
        return None
    p = m.group(1).strip()
    return p if os.path.isabs(p) else os.path.normpath(os.path.join(root, p))


def tree_activity(root: str) -> float:
    """这棵树最近一次被动过的时刻(取根目录、git 索引、HEAD 里最新的)。读不到任何一个时按"刚刚"算,偏保守。

    故意不看 logs/HEAD:实测 git gc 一类的批量维护会同时重写所有 worktree 的 reflog,
    让每棵树都显得"刚活动过"。索引则不会——add/commit/checkout/reset 才会重写它。
    """
    stamps: List[float] = []
    adm = git_admin_dir(root)
    paths = [root] + ([os.path.join(adm, n) for n in ("index", "HEAD")] if adm else [])
    for p in paths:
        try:
            stamps.append(os.stat(p).st_mtime)
        except OSError:
            pass
    return max(stamps) if stamps else time.time()


def ignored_paths(root: str, rels: List[str]) -> Set[str]:
    if not rels:
        return set()
    rc, out, _ = wd_util.run(["git", "-C", root, "check-ignore", "-z", "--stdin"], timeout=60,
                             input_text="\0".join(rels) + "\0")
    if rc not in (0, 1):
        return set()
    return {p for p in out.split("\0") if p}


class Usage:
    """哪些目录当前有进程在用(进程的 cwd 在其中,或命令行引用了它)。"""

    def __init__(self, ctx: Context) -> None:
        self.rows: List[Tuple[int, Optional[str], str]] = [
            (p.pid, ctx.cwds.get(p.pid), p.cmd) for p in ctx.procs.values()
            if p.uid == ctx.uid and not p.zombie and p.pid not in ctx.self_pids]

    def users(self, root: str) -> List[int]:
        scope = wd_core.Scope([root], alias_dirs=())
        return [pid for pid, cwd, cmd in self.rows if scope.path_in(cwd) or scope.cmd_mentions(cmd)]


def clean_build_outputs(ctx: Context, usage: Usage) -> bool:
    """返回是否做完(被时间预算打断返回 False,下一轮继续)。"""
    cfg = ctx.cfg["garbage"]["build_outputs"]
    if not cfg["enabled"]:
        return True
    idle_s = cfg["idle_hours"] * 3600
    roots = [w.path for w in ctx.worktrees if not w.main and not w.prunable and os.path.isdir(w.path)]
    if cfg["include_scratch_clones"]:
        roots += [c for c in ctx.clones if os.path.isdir(c)]
    now = time.time()
    total_freed = 0
    n_trees = 0
    for root in roots:
        if ctx.deadline.expired():
            ctx.log.warn("构建产物清理被时间预算打断,下一轮继续")
            return False
        if now - tree_activity(root) < idle_s:
            continue
        users = usage.users(root)
        if users:
            ctx.report.add("garbage", "keep", os.path.basename(root), "构建产物:有进程在用(pid %s)" % ",".join(str(u) for u in users[:3]))
            continue
        projects = find_projects(root)
        dirs = [os.path.join(p, sub) for p in projects for sub in ("bin", "obj") if os.path.isdir(os.path.join(p, sub))]
        if any(has_recent(d, now - idle_s) for d in dirs):
            continue
        bins = [os.path.join(p, "bin") for p in projects
                if os.path.isdir(os.path.join(p, "bin")) and not os.path.islink(os.path.join(p, "bin"))]
        if cfg["purge_obj"]:
            bins += [os.path.join(p, "obj") for p in projects
                     if os.path.isdir(os.path.join(p, "obj")) and not os.path.islink(os.path.join(p, "obj"))]
        rels = [os.path.relpath(b, root) for b in bins]
        ok = ignored_paths(root, rels)
        real_root = os.path.realpath(root)
        doomed: List[Tuple[str, int]] = []
        for b, rel in zip(bins, rels):
            if rel not in ok or not wd_util.path_within(os.path.realpath(b), real_root):
                continue                                   # 不是被 git 忽略的真实目录:不碰
            doomed.append((b, dir_size(b)))
        if not doomed:
            continue
        tree_freed = sum(size for _, size in doomed)
        what = "、".join(sorted({os.path.basename(b) for b, _ in doomed}))
        if ctx.act("garbage", "%s:%d 个构建产物目录(%s)" % (os.path.basename(root), len(doomed), what),
                   "整棵树已闲置 ≥ %s,没有进程在用,构建产物可以再生" % wd_util.fmt_dur(idle_s), tree_freed) == "done":
            for b, _ in doomed:
                shutil.rmtree(b, ignore_errors=True)
        n_trees += 1
        total_freed += tree_freed
    if n_trees:
        ctx.log.info("构建产物:%d 棵闲置的树,%s %s" % (n_trees, "将释放" if ctx.dry_run else "已释放", wd_util.fmt_bytes(total_freed)))
    return True


# ---------------------------------------------------------------- worktree 清点(只报告;移除要显式开启)

def audit_worktrees(ctx: Context, usage: Usage) -> None:
    cfg = ctx.cfg["garbage"]["worktrees"]
    idle_s = cfg["idle_hours"] * 3600
    main = next((w for w in ctx.worktrees if w.main), None)
    if main is None:
        return
    main_ref = ("refs/heads/" + main.branch) if main.branch else main.head
    now = time.time()
    reasons_count: Counter = Counter()
    removable: List[Any] = []
    for w in ctx.worktrees:
        if w.main:
            continue
        if ctx.deadline.expired():
            ctx.log.warn("worktree 清点被时间预算打断")
            break
        reasons: List[str] = []
        if w.prunable or not os.path.isdir(w.path):
            reasons.append("目录已不存在")
        else:
            if w.locked:
                reasons.append("已被锁定")
            rc, out, _ = wd_util.run(["git", "-C", w.path, "status", "--porcelain"], timeout=60)
            dirty = len([ln for ln in out.splitlines() if ln.strip()]) if rc == 0 else -1
            if dirty != 0:
                reasons.append("有未提交的改动" if dirty > 0 else "状态读取失败")
            rc, _, _ = wd_util.run(["git", "-C", ctx.repo_root, "merge-base", "--is-ancestor", w.head, main_ref], timeout=30)
            if rc != 0:
                reasons.append("有未并入主分支的提交")
            special = [n for n in ("HANDOFF.json", "re", "out") if os.path.exists(os.path.join(w.path, n))]
            if special:
                reasons.append("有交接/产物文件")
            if usage.users(w.path):
                reasons.append("有进程在用")
            if now - tree_activity(w.path) < idle_s:
                reasons.append("近期有活动")
        if reasons:
            reasons_count.update(reasons)
        else:
            removable.append(w)
    total = len([w for w in ctx.worktrees if not w.main])
    summary = "、".join("%s %d 个" % (k, n) for k, n in reasons_count.most_common()) or "无"
    ctx.report.add("worktree", "report", "worktree 共 %d 个(不含主工作区)" % total,
                   "可安全移除 %d 个;其余各有保留理由(同一个可以有多条):%s" % (len(removable), summary))
    for w in removable:
        if cfg["remove_merged_clean"]:
            if ctx.act("worktree", os.path.basename(w.path), "已并入主分支、干净、没有交接/产物、没人用、闲置 ≥ %s" % wd_util.fmt_dur(idle_s)) == "done":
                rc, _, err = wd_util.run(["git", "-C", ctx.repo_root, "worktree", "remove", w.path], timeout=120)   # 不加 --force
                if rc != 0:
                    ctx.log.warn("git worktree remove %s 被 git 拒绝:%s" % (os.path.basename(w.path), err.strip()[:160]))
        else:
            ctx.report.add("worktree", "report", os.path.basename(w.path),
                           "可安全移除(git worktree remove);想让看门狗自动做,把 garbage.worktrees.remove_merged_clean 设为 true")


# ---------------------------------------------------------------- 入口

def run(ctx: Context) -> None:
    g = ctx.cfg["garbage"]
    if not g["enabled"]:
        ctx.report.add("garbage", "skip", "垃圾清理", "配置里已关闭")
        return
    try:
        procs = wd_core.snapshot()          # 刚取的快照,不复用早先那份:判断属主死活要用最新的
    except RuntimeError as e:
        ctx.log.error("垃圾清理取不到进程快照,整体跳过:%s" % e)
        ctx.report.add("garbage", "error", "垃圾清理", "取不到进程快照")
        return
    if g["ipc_leftovers"]:
        _safe(ctx, "IPC 残留", clean_ipc, procs)
    _safe(ctx, "MSBuildTemp", clean_msbuild_temp)
    if g["stale_locks"]:
        _safe(ctx, "heavy 锁", clean_heavy_locks, procs)
        _safe(ctx, "git 锁", clean_git_locks, procs)
    if g["worktree_prune"]:
        _safe(ctx, "worktree 登记", prune_worktrees)

    gstate = ctx.state.setdefault("garbage", {})
    due = ctx.deep or (time.time() - float(gstate.get("heavy_wall", 0) or 0)) >= g["heavy_interval_minutes"] * 60
    if not due:
        return
    usage = Usage(ctx)
    finished = [True]

    def _builds(c: Context) -> None:
        finished[0] = clean_build_outputs(c, usage)

    _safe(ctx, "构建产物", _builds)
    if ctx.deep or g["worktrees"]["remove_merged_clean"]:
        _safe(ctx, "worktree 清点", audit_worktrees, usage)
    if finished[0] and not ctx.dry_run:
        gstate["heavy_wall"] = time.time()
