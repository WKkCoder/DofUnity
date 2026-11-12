#!/usr/bin/env python3
"""看门狗的测试。运行:python3 -m unittest discover -s tools/watchdog -p 'test_*.py'

这个工具会杀进程、删文件,所以测试用的是真实进程和真实文件,而不是 mock:
- 孤儿进程用"外层 sh 把命令放后台再退出"造出来,真的被 launchd 收养(PPID=1);
- "时间"用假的醒着时钟推进,不用真等几十分钟;
- 工程是临时目录里的 git 仓库,看门狗的范围只限于它,不会碰这台机器上真正在跑的任务;
- 每个测试起的进程都在收尾时清掉。

可能触发提交守卫的字样(绝对路径、令牌)都在运行时拼接,不写成字面量。
"""
from __future__ import annotations

import contextlib
import copy
import errno
import importlib.util
import io
import json
import os
import plistlib
import shlex
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from typing import Any, Callable, Dict, List, Optional
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import wd_config  # noqa: E402
import wd_core  # noqa: E402
import wd_garbage  # noqa: E402
import wd_procs  # noqa: E402
import wd_util  # noqa: E402

_spec = importlib.util.spec_from_file_location("wd_main", os.path.join(HERE, "watchdog.py"))
wd_main = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(wd_main)  # type: ignore[union-attr]

HOUR = 3600.0


def sh(*args: str, cwd: Optional[str] = None) -> str:
    p = subprocess.run(list(args), cwd=cwd, capture_output=True, text=True)
    if p.returncode != 0:
        raise AssertionError("命令失败 %s\n%s%s" % (args, p.stdout, p.stderr))
    return p.stdout


def alive(pid: int) -> bool:
    rc, out, _ = wd_util.run(["ps", "-o", "stat=", "-p", str(pid)], timeout=10)
    return rc == 0 and bool(out.strip()) and not out.strip().startswith("Z")


def make_old(path: str, hours: float = 100) -> None:
    t = time.time() - hours * HOUR
    os.utime(path, (t, t), follow_symlinks=False)


def touch(path: str, text: str = "") -> None:
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


def read_text(path: str) -> str:
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


def make_tree_old(root: str, hours: float = 100) -> None:
    for dp, dn, fn in os.walk(root):
        for n in dn + fn:
            make_old(os.path.join(dp, n), hours)
        make_old(dp, hours)


def make_socket(directory: str, name: str) -> str:
    """macOS 的 AF_UNIX 路径上限约 104 字节,临时目录太长,所以切到目录里用相对路径绑定。"""
    old = os.getcwd()
    os.chdir(directory)
    try:
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        s.bind(name)
        s.close()
    finally:
        os.chdir(old)
    return os.path.join(directory, name)


def lsof_cwds(pids: List[int]) -> Dict[int, str]:
    if not pids:
        return {}
    rc, out, _ = wd_util.run(["lsof", "-a", "-d", "cwd", "-p", ",".join(str(p) for p in pids), "-Fpn"], timeout=30)
    res: Dict[int, str] = {}
    cur: Optional[int] = None
    for line in out.splitlines():
        if line.startswith("p"):
            cur = int(line[1:])
        elif line.startswith("n") and cur is not None:
            res[cur] = line[1:]
    return res


class Base(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = os.path.realpath(tempfile.mkdtemp(prefix="wd-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self._roots: List[int] = []
        self._seq = 0
        self.addCleanup(self._kill_spawned)           # 后注册先执行:先杀进程,再删目录

    # ---- 进程
    def _kill_tree(self, root: int) -> None:
        try:
            procs = wd_core.snapshot()
        except RuntimeError:
            return
        for m in wd_core.tree_of(procs, wd_core.build_children(procs), root):
            try:
                os.kill(m.pid, signal.SIGKILL)
            except OSError:
                pass

    def _kill_spawned(self) -> None:
        for r in list(self._roots):
            self._kill_tree(r)

    def _reap(self, p: "subprocess.Popen[bytes]") -> None:
        self._kill_tree(p.pid)
        p.wait()

    def wait_for(self, cond: Callable[[], Any], timeout: float = 8.0, what: str = "") -> Any:
        end = time.time() + timeout
        while time.time() < end:
            v = cond()
            if v:
                return v
            time.sleep(0.1)
        self.fail("等待超时:" + what)

    def wait_cpu(self, pid: int, extra: float, timeout: float = 90.0) -> None:
        """等该进程再多烧 extra 秒 CPU。机器很忙时墙钟时间不可靠,所以按 CPU 时间同步。"""
        start = wd_procs.rusage(pid)
        self.assertIsNotNone(start)
        self.wait_for(lambda: ((wd_procs.rusage(pid) or (0.0, 0))[0] - start[0]) >= extra, timeout, "进程应当烧够 CPU")

    def ppid_of(self, pid: int) -> Optional[int]:
        rc, out, _ = wd_util.run(["ps", "-o", "ppid=", "-p", str(pid)], timeout=10)
        try:
            return int(out.strip())
        except ValueError:
            return None

    def spawn_orphan(self, argv: List[str], cwd: str, agent: bool = True, env: Optional[Dict[str, str]] = None) -> int:
        """起一个会被 launchd 收养的后台进程:外层 sh 把它放后台、写下 pid 就退出。"""
        self._seq += 1
        pidfile = os.path.join(self.tmp, "pid.%d" % self._seq)
        e = dict(os.environ)
        e.pop("WD_TEST_AGENT", None)
        if agent:
            e["WD_TEST_AGENT"] = "1"
        e.update(env or {})
        cmd = " ".join(shlex.quote(a) for a in argv)
        subprocess.run(["sh", "-c", "%s </dev/null >/dev/null 2>&1 & echo $! > %s" % (cmd, shlex.quote(pidfile))],
                       cwd=cwd, env=e, check=True)
        pid = int(self.wait_for(lambda: read_text(pidfile).strip(), 5, "pid 文件"))
        self._roots.append(pid)
        self.wait_for(lambda: self.ppid_of(pid) == 1, 5, "被 launchd 收养(PPID=1)")
        return pid

    def spawn_child(self, argv: List[str], cwd: str, agent: bool = True) -> int:
        e = dict(os.environ)
        e.pop("WD_TEST_AGENT", None)
        if agent:
            e["WD_TEST_AGENT"] = "1"
        p = subprocess.Popen(argv, cwd=cwd, env=e, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self._roots.append(p.pid)
        self.addCleanup(self._reap, p)
        return p.pid

    def sleeper(self, tag: str) -> List[str]:
        return [sys.executable, "-c", "import time; time.sleep(600)  # " + tag]

    def busy(self, tag: str) -> List[str]:
        return [sys.executable, "-c", "while True: pass  # " + tag]

    # ---- 工程与上下文
    def make_repo(self, name: str = "repo") -> str:
        d = os.path.join(self.tmp, name)
        os.makedirs(d)
        sh("git", "init", "-q", "-b", "main", d)
        sh("git", "-C", d, "config", "user.email", "t@example.invalid")
        sh("git", "-C", d, "config", "user.name", "t")
        with open(os.path.join(d, ".gitignore"), "w", encoding="utf-8") as f:
            f.write("src/**/bin/\nobj/\nHANDOFF.json\nre/\nout/\n")
        sh("git", "-C", d, "add", "-A")
        sh("git", "-C", d, "commit", "-q", "-m", "init")
        return d

    def cfg(self, repo: str, **proc_over: Any) -> Dict[str, Any]:
        c = copy.deepcopy(wd_config.DEFAULTS)
        c["repo_root"] = repo
        c["processes"].update({"agent_env_markers": ["WD_TEST_AGENT"], "idle_minutes": 30, "confirm_minutes": 5,
                               "cpu_epsilon_seconds": 0.1, "term_grace_seconds": 3, "max_trees_per_run": 10})
        c["processes"].update(proc_over)
        c["garbage"].update({"tmp_dirs": [os.path.join(self.tmp, "tmpdir")], "lock_dirs": [], "ipc_min_age_seconds": 0})
        os.makedirs(os.path.join(self.tmp, "tmpdir"), exist_ok=True)
        return c

    def make_ctx(self, repo: str, cfg: Dict[str, Any], state: Dict[str, Any], now_up: float, dry_run: bool = False,
                 clones: Optional[List[str]] = None) -> wd_core.Context:
        procs = wd_core.snapshot()
        kids = wd_core.build_children(procs)
        pids = {os.getpid()}
        for r in self._roots:
            pids |= {m.pid for m in wd_core.tree_of(procs, kids, r)}
        wts = wd_core.list_worktrees(repo)
        scope = wd_core.Scope([w.path for w in wts] + list(clones or []), alias_dirs=())
        ctx = wd_core.Context(
            cfg=cfg, log=wd_util.Log(None), dry_run=dry_run, uid=os.getuid(), now_up=now_up, now_wall=time.time(),
            sleep_total=0.0, procs=procs, cwds=lsof_cwds(sorted(pids)), repo_root=repo, label=os.path.basename(repo),
            worktrees=wts, clones=list(clones or []), scope=scope, state=state, report=wd_core.Report(),
            deadline=wd_core.Deadline(120), self_pids={os.getpid()} | wd_core.ancestors(procs, os.getpid()))
        ctx.net, ctx.net_checked = {}, True           # 默认视为"没有任何网络流量";个别测试覆盖
        return ctx

    def advance(self, repo: str, cfg: Dict[str, Any], state: Dict[str, Any], minutes: float, dry_run: bool = False,
                real_sleep: float = 0.0) -> wd_core.Context:
        if real_sleep:
            time.sleep(real_sleep)
        ctx = self.make_ctx(repo, cfg, state, 1000.0 + minutes * 60, dry_run)
        wd_procs.run(ctx)
        return ctx

    @staticmethod
    def texts(ctx: wd_core.Context, area: str, action: Optional[str] = None) -> str:
        return "\n".join("%s|%s|%s" % (i["action"], i["target"], i["why"]) for i in ctx.report.select(area, action))


# ====================================================================== 工具函数

class TestUtil(unittest.TestCase):
    def test_parse_clock(self) -> None:
        self.assertEqual(wd_util.parse_clock("00:05"), 5)
        self.assertEqual(wd_util.parse_clock("1:02:03"), 3723)
        self.assertEqual(wd_util.parse_clock("2-01:00:00"), 2 * 86400 + 3600)
        self.assertAlmostEqual(wd_util.parse_clock("0:00.50"), 0.5)
        self.assertAlmostEqual(wd_util.parse_clock("123:45.60"), 7425.6)
        self.assertEqual(wd_util.parse_clock(""), 0)

    def test_fmt(self) -> None:
        self.assertEqual(wd_util.fmt_dur(45), "45 秒")
        self.assertEqual(wd_util.fmt_dur(3600), "60 分钟")
        self.assertEqual(wd_util.fmt_dur(7200), "2.0 小时")
        self.assertEqual(wd_util.fmt_dur(3 * 86400), "3.0 天")
        self.assertEqual(wd_util.fmt_bytes(512), "512 B")
        self.assertEqual(wd_util.fmt_bytes(1536), "1.5 KB")

    def test_redact_masks_secrets_but_keeps_the_rest(self) -> None:
        tok = "gh" + "p_" + "A" * 36
        flag = "--pass" + "word"
        cmd = "tool %s hunter2 --name ok API_TOKEN=abc123 %s" % (flag, tok)
        out = wd_util.redact(cmd)
        for secret in ("hunter2", "abc123", tok):
            self.assertNotIn(secret, out)
        self.assertIn("--name ok", out)
        self.assertLessEqual(len(wd_util.redact("x " * 500, 50)), 50)

    def test_path_within_respects_component_boundaries(self) -> None:
        self.assertTrue(wd_util.path_within("/a/b/c", "/a/b"))
        self.assertTrue(wd_util.path_within("/a/b", "/a/b"))
        self.assertFalse(wd_util.path_within("/a/bc", "/a/b"))

    def test_log_rotates_and_never_raises(self) -> None:
        d = tempfile.mkdtemp(prefix="wd-")
        self.addCleanup(shutil.rmtree, d, True)
        log = wd_util.Log(os.path.join(d, "x.log"), max_bytes=200, backups=2)
        for i in range(40):
            log.info("line %d" % i)
        names = sorted(os.listdir(d))
        self.assertIn("x.log.1", names)
        self.assertNotIn("x.log.3", names)
        wd_util.Log("/dev/null/cannot/write.log").info("不能抛异常")

    def test_run_handles_timeout_and_missing_command(self) -> None:
        self.assertEqual(wd_util.run(["sleep", "5"], timeout=0.3)[0], 124)
        self.assertEqual(wd_util.run(["no-such-command-xyz"])[0], 127)

    def test_awake_clock_is_monotonic(self) -> None:
        a = wd_util.awake_clock()
        time.sleep(0.05)
        self.assertGreater(wd_util.awake_clock(), a)


# ====================================================================== 配置

class TestConfig(unittest.TestCase):
    def write(self, obj: Any) -> str:
        d = tempfile.mkdtemp(prefix="wd-")
        self.addCleanup(shutil.rmtree, d, True)
        p = os.path.join(d, "config.json")
        with open(p, "w", encoding="utf-8") as f:
            f.write(obj if isinstance(obj, str) else json.dumps(obj))
        return p

    def test_defaults_are_valid(self) -> None:
        self.assertEqual(wd_config.validate(copy.deepcopy(wd_config.DEFAULTS)), [])
        cfg, problems = wd_config.load_config("/nonexistent/config.json")
        self.assertEqual(problems, [])
        self.assertEqual(cfg["processes"]["idle_minutes"], 60)

    def test_override_merges_deeply(self) -> None:
        cfg, problems = wd_config.load_config(self.write({"processes": {"idle_minutes": 90}, "interval_minutes": 5}))
        self.assertEqual(problems, [])
        self.assertEqual(cfg["processes"]["idle_minutes"], 90)
        self.assertEqual(cfg["processes"]["confirm_minutes"], 5)         # 没改的保持默认
        self.assertEqual(cfg["processes"]["signatures"], wd_config.DEFAULTS["processes"]["signatures"])

    def test_typos_and_bad_values_are_reported(self) -> None:
        _, p = wd_config.load_config(self.write({"processes": {"idle_minute": 90}}))
        self.assertTrue(any("idle_minute" in x for x in p))
        _, p = wd_config.load_config(self.write({"processes": {"idle_minutes": -1}}))
        self.assertTrue(any("idle_minutes" in x for x in p))
        _, p = wd_config.load_config(self.write({"processes": {"extra_protected": ["(unclosed"]}}))
        self.assertTrue(any("正则" in x for x in p))
        _, p = wd_config.load_config(self.write({"processes": {"extra_signatures": [{"name": "x", "args": "y", "action": "nuke"}]}}))
        self.assertTrue(any("action" in x for x in p))
        _, p = wd_config.load_config(self.write({"processes": {"unknown_orphans": "maybe"}}))
        self.assertTrue(any("unknown_orphans" in x for x in p))

    def test_garbage_structure_falls_back_to_defaults(self) -> None:
        cfg, p = wd_config.load_config(self.write({"processes": "oops"}))
        self.assertTrue(p)
        self.assertEqual(cfg["processes"]["idle_minutes"], 60)
        _, p = wd_config.load_config(self.write("{not json"))
        self.assertTrue(p)
        _, p = wd_config.load_config(self.write("[1, 2]"))
        self.assertTrue(p)

    def test_builtin_signatures_classify_known_commands(self) -> None:
        sigs, protected, unknown = wd_procs.compile_policy(wd_config.DEFAULTS["processes"])

        def name(cmd: str) -> str:
            s = wd_procs.match_sig(sigs, cmd)
            return s.name if s else "unknown"
        self.assertEqual(name("/usr/local/share/dotnet/dotnet test foo.dll"), "dotnet")
        self.assertEqual(name("dotnet /sdk/MSBuild.dll /nodemode:1"), "dotnet")
        self.assertEqual(name("/Applications/Unity/Hub/Editor/6000.0/Unity.app/Contents/MacOS/Unity -batchmode -projectPath x"), "unity-batchmode")
        self.assertEqual(name("/Applications/Unity/Hub/Editor/6000.0/Unity.app/Contents/MacOS/Unity -projectpath x"), "unknown")   # 图形界面不带 -batchmode
        self.assertEqual(name("/x/dotnet /sdk/VBCSCompiler.dll"), "compiler-server")
        self.assertEqual(name("java -cp g ghidra.app.util.headless.AnalyzeHeadless p"), "ghidra-headless")
        self.assertEqual(name("/opt/py/Python.app/Contents/MacOS/Python -u run.py"), "script-driver")
        self.assertEqual(name("bash -c sleep 5"), "script-driver")
        self.assertEqual(name("/usr/bin/ssh host cmd"), "script-driver")
        self.assertEqual(name("/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wineserver"), "wine")
        self.assertEqual(name("/usr/sbin/cfprefsd agent"), "unknown")
        for cmd in ("/Applications/Cursor.app/Contents/MacOS/Cursor", "Cursor Helper (Plugin)", "tmux: server",
                    "/usr/libexec/trustd", "/usr/bin/python3 /opt/app-support/dnf-unity-watchdog/bin/watchdog.py run"):
            self.assertTrue(protected.search(cmd), cmd)
        self.assertFalse(protected.search("python3 -u /tmp/m72/sweeps.py"))


# ====================================================================== 数据层

class TestCore(Base):
    def test_parse_ps(self) -> None:
        text = ("  123     1   123   501 Ss    01:02:03   0:05.50   1024 Wed Nov  5 10:48:23 2026 /usr/bin/python3 -c print('x y')\n"
                "  124   123   123   501 Z+    00:10      0:00.00      0 Wed Nov 11 09:00:01 2026 (sh)\n"
                "garbage line\n")
        procs = wd_core.parse_ps(text)
        self.assertEqual(set(procs), {123, 124})
        p = procs[123]
        self.assertEqual((p.ppid, p.pgid, p.uid, p.etime, p.rss_kb), (1, 123, 501, 3723, 1024))
        self.assertAlmostEqual(p.cpu, 5.5)
        self.assertEqual(p.lstart, "Wed Nov 5 10:48:23 2026")
        self.assertEqual(p.cmd, "/usr/bin/python3 -c print('x y')")
        self.assertTrue(procs[124].zombie)
        self.assertEqual(procs[124].cmd, "(sh)")

    def test_start_epoch_matches_ps(self) -> None:
        procs = wd_core.snapshot()
        ep = wd_core.start_epoch(procs[os.getpid()].lstart)
        self.assertIsNotNone(ep)
        self.assertLess(abs(time.time() - ep), 6 * 3600 * 24 * 365)
        self.assertLessEqual(ep, time.time() + 2)

    def test_real_cwd_reading(self) -> None:
        cwds = wd_core.read_cwds(os.getuid())
        self.assertEqual(os.path.realpath(cwds[os.getpid()]), os.path.realpath(os.getcwd()))

    def test_scope_matching_is_boundary_aware(self) -> None:
        root = os.path.join(self.tmp, "proj")
        os.makedirs(os.path.join(root, "data"))
        s = wd_core.Scope([root], alias_dirs=())
        self.assertTrue(s.path_in(root + "/sub/dir"))
        self.assertFalse(s.path_in(root + "X"))
        self.assertTrue(s.cmd_mentions("python3 %s/x.py" % root))
        self.assertTrue(s.cmd_mentions("--projectPath=%s" % root))
        self.assertTrue(s.cmd_mentions("run '%s'" % root))
        self.assertFalse(s.cmd_mentions("python3 %s-backup/x.py" % root))
        self.assertFalse(s.cmd_mentions("python3 %sX/x.py" % root))
        self.assertFalse(s.cmd_mentions("python3 elsewhere.py"))
        self.assertFalse(wd_core.Scope([], alias_dirs=()).cmd_mentions("anything"))

    def test_scope_handles_private_var_aliases(self) -> None:
        root = os.path.join(self.tmp, "proj")
        os.makedirs(root)
        if not root.startswith("/private/var/"):
            self.skipTest("临时目录不在 /private/var 下")
        s = wd_core.Scope([root], alias_dirs=())
        self.assertTrue(s.cmd_mentions("tool " + root[len("/private"):] + "/a"))

    def test_scope_rejects_roots_that_are_too_shallow(self) -> None:
        s = wd_core.Scope(["/", "/tmp", ""], alias_dirs=())
        self.assertEqual(s.rejected, ["/", "/tmp"])
        self.assertFalse(s.path_in("/tmp/anything"))
        self.assertFalse(s.cmd_mentions("ls /tmp/x"))

    def test_scope_follows_symlink_aliases(self) -> None:
        root = os.path.join(self.tmp, "proj")
        os.makedirs(os.path.join(root, "re", "m1"))
        aliases = os.path.join(self.tmp, "aliases")
        os.makedirs(aliases)
        os.symlink(os.path.join(root, "re", "m1"), os.path.join(aliases, "m1"))
        os.symlink("/usr", os.path.join(aliases, "elsewhere"))
        s = wd_core.Scope([root], alias_dirs=(aliases,))
        self.assertTrue(s.cmd_mentions("python3 %s/m1/job.py" % aliases))
        self.assertFalse(s.cmd_mentions("python3 %s/elsewhere/job.py" % aliases))

    def test_worktree_listing_and_scratch_clones(self) -> None:
        repo = self.make_repo()
        base = os.path.join(self.tmp, "wt")
        os.makedirs(base)
        sh("git", "-C", repo, "worktree", "add", "-q", "-b", "feat", os.path.join(base, "a"))
        sh("git", "clone", "-q", repo, os.path.join(base, "clone"))
        other = os.path.join(base, "other")
        os.makedirs(other)
        sh("git", "init", "-q", "-b", "main", other)
        sh("git", "-C", other, "config", "user.email", "t@example.invalid")
        sh("git", "-C", other, "config", "user.name", "t")
        sh("git", "-C", other, "commit", "-q", "--allow-empty", "-m", "unrelated root")
        wts = wd_core.list_worktrees(repo)
        self.assertEqual([(w.main, w.branch) for w in wts], [(True, "main"), (False, "feat")])
        self.assertEqual(os.path.realpath(wts[0].path), repo)
        self.assertEqual(wd_core.find_scratch_clones(repo, wts), [os.path.join(base, "clone")])
        self.assertEqual(wd_config.detect_repo_root(os.path.join(base, "a")), repo)     # 从 worktree 里也能找到主工作区


# ====================================================================== 进程:该杀的杀,不该杀的绝不动

class TestProcs(Base):
    def test_rusage_and_net_readings_exist(self) -> None:
        r = wd_procs.rusage(os.getpid())
        self.assertIsNotNone(r)
        self.assertGreater(r[0], 0)
        self.assertIsNone(wd_procs.rusage(2 ** 22 + 12345))
        net = wd_procs.read_net()
        self.assertTrue(net is None or isinstance(net, dict))

    def test_rusage_sees_reaped_children_cpu(self) -> None:
        before = wd_procs.rusage(os.getpid())[0]
        # 子进程烧满 0.4 秒"CPU 时间"(不是墙钟:机器忙时墙钟会虚长),退出被回收后应当算进父进程的累计里
        subprocess.run([sys.executable, "-c", "import time\nwhile time.process_time() < 0.4: pass"], check=True)
        self.assertGreater(wd_procs.rusage(os.getpid())[0] - before, 0.3)

    def test_exited_members_are_ignored_but_unreadable_live_members_are_a_gap(self) -> None:
        repo = self.make_repo()
        ctx = self.make_ctx(repo, self.cfg(repo), {}, 1000.0)
        ctx.net, ctx.net_checked = {}, True                          # 不让网络仪表的缺失干扰这条
        finished = subprocess.Popen(["true"])
        finished.wait()
        ghost = wd_core.Proc(finished.pid, 1, finished.pid, os.getuid(), "S", 0.0, 0, 0, "Mon Jan  1 00:00:00 2024", "true")
        me = wd_core.snapshot()[os.getpid()]

        stats, _, gaps = wd_procs.measure(ctx, [me, ghost])
        self.assertEqual(gaps, [], "拿到快照之后才退出的短命成员不是仪表缺失")
        self.assertEqual(list(stats), [me.key])

        with mock.patch.object(wd_procs, "_rusage_raw", return_value=(None, errno.EPERM)):
            _, _, gaps = wd_procs.measure(ctx, [me])
        self.assertTrue(gaps, "进程活着却读不到,必须算仪表缺失(没有证据就不是闲置)")

        with mock.patch.object(wd_procs, "_rusage_raw", return_value=(None, wd_procs.NATIVE_UNAVAILABLE)):
            _, _, gaps = wd_procs.measure(ctx, [ghost])
        self.assertTrue(gaps, "仪表本身不可用时,即使成员已退出也不能当作读到了")

    def test_env_flags_only_report_presence(self) -> None:
        repo = self.make_repo()
        pid = self.spawn_orphan(self.sleeper("WD_T_ENV"), repo, env={"WD_TEST_SECRET_VALUE": "do-not-leak"})
        flags = wd_procs.read_env_flags([pid], ["WD_TEST_AGENT", "WD_NOT_THERE"])
        self.assertEqual(flags[pid][0], {"WD_TEST_AGENT"})
        self.assertNotIn("do-not-leak", repr(flags))

    def test_idle_orphan_is_killed_only_after_the_idle_window(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.sleeper("WD_T_IDLE"), repo)
        root_key = wd_core.snapshot()[pid].key
        cfg = self.cfg(repo)
        ctx = self.advance(repo, cfg, state, 0)
        self.assertIn("刚开始观察", self.texts(ctx, "proc", "keep"))
        self.assertTrue(alive(pid))
        ctx = self.advance(repo, cfg, state, 10)
        self.assertIn("仍在活动", self.texts(ctx, "proc", "keep"))             # 观察够久了,但闲置还没到 30 分钟
        self.assertTrue(alive(pid))
        ctx = self.advance(repo, cfg, state, 31)
        self.assertTrue(ctx.report.select("proc", "done"), self.texts(ctx, "proc"))
        self.wait_for(lambda: not alive(pid), 8, "闲置的孤儿应当被清掉")
        self.assertNotIn(root_key, state.get("procs", {}), "清掉之后不该再保留它的观察记录")

    def test_dry_run_never_kills(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.sleeper("WD_T_DRY"), repo)
        cfg = self.cfg(repo)
        self.advance(repo, cfg, state, 0, dry_run=True)
        ctx = self.advance(repo, cfg, state, 40, dry_run=True)
        self.assertTrue(ctx.report.select("proc", "would"))
        self.assertFalse(ctx.report.select("proc", "done"))
        time.sleep(0.5)
        self.assertTrue(alive(pid))

    def test_busy_orphan_survives(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.busy("WD_T_BUSY"), repo)
        cfg = self.cfg(repo)
        for minutes in (0, 35, 70):                                 # 每次观察之间都让它实打实地烧掉 0.4 秒 CPU
            self.wait_cpu(pid, 0.4)
            ctx = self.advance(repo, cfg, state, minutes)
            self.assertFalse(ctx.report.select("proc", "done"), self.texts(ctx, "proc"))
        self.assertTrue(alive(pid))
        self.assertIn("CPU", self.texts(ctx, "proc", "keep"))

    def test_max_age_cap_kills_even_a_busy_runaway(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.busy("WD_T_RUNAWAY"), repo)
        sig = {"name": "oneshot", "args": "WD_T_RUNAWAY", "trusted": True, "action": "kill", "max_age_minutes": 20}
        cfg = self.cfg(repo, extra_signatures=[sig])
        self.advance(repo, cfg, state, 0, real_sleep=0.5)
        ctx = self.advance(repo, cfg, state, 10, real_sleep=1.2)
        self.assertTrue(alive(pid))
        ctx = self.advance(repo, cfg, state, 21, real_sleep=1.2)
        self.assertIn("上限", self.texts(ctx, "proc", "done"))
        self.wait_for(lambda: not alive(pid), 8, "跑飞的任务应当被清掉")

    def test_scripts_need_provenance_but_trusted_signatures_do_not(self) -> None:
        repo = self.make_repo()
        no_prov, state = self.spawn_orphan(self.sleeper("WD_T_NOPROV"), repo, agent=False), {}
        trusted = self.spawn_orphan(self.sleeper("WD_T_TRUSTED"), repo, agent=False)
        cfg = self.cfg(repo, extra_signatures=[{"name": "t", "args": "WD_T_TRUSTED", "trusted": True, "action": "kill"}])
        self.advance(repo, cfg, state, 0)
        ctx = self.advance(repo, cfg, state, 40)
        self.assertIn("没有 agent 出身证明", self.texts(ctx, "proc", "keep"))
        self.wait_for(lambda: not alive(trusted), 8, "可信签名的闲置进程应当被清掉")
        self.assertTrue(alive(no_prov), "没有出身证明的脚本绝不能动")

    def test_out_of_scope_process_is_untouched(self) -> None:
        repo, state = self.make_repo(), {}
        elsewhere = os.path.join(self.tmp, "elsewhere")
        os.makedirs(elsewhere)
        pid = self.spawn_orphan(self.sleeper("WD_T_FOREIGN"), elsewhere)
        cfg = self.cfg(repo)
        self.advance(repo, cfg, state, 0)
        ctx = self.advance(repo, cfg, state, 120)
        self.assertFalse(ctx.report.select("proc", "done"))
        self.assertNotIn("WD_T_FOREIGN", self.texts(ctx, "proc"))
        self.assertTrue(alive(pid))

    def test_command_line_mention_brings_a_process_into_scope(self) -> None:
        repo, state = self.make_repo(), {}
        elsewhere = os.path.join(self.tmp, "elsewhere")
        os.makedirs(elsewhere)
        pid = self.spawn_orphan([sys.executable, "-c", "import time; time.sleep(600)  # %s/job.py" % repo], elsewhere)
        cfg = self.cfg(repo)
        self.advance(repo, cfg, state, 0)
        ctx = self.advance(repo, cfg, state, 40)
        self.assertTrue(ctx.report.select("proc", "done"))
        self.wait_for(lambda: not alive(pid), 8, "命令行里引用了工程路径的闲置孤儿应当被清掉")

    def test_protected_tree_is_untouched(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.sleeper("WD_T_PROTECT_ME"), repo)
        cfg = self.cfg(repo, extra_protected=["WD_T_PROTECT_ME"])
        self.advance(repo, cfg, state, 0)
        ctx = self.advance(repo, cfg, state, 120)
        self.assertFalse(ctx.report.select("proc", "done"))
        time.sleep(0.5)
        self.assertTrue(alive(pid))

    def test_ignore_signature_is_never_killed_even_inside_a_doomed_tree(self) -> None:
        repo, state = self.make_repo(), {}
        fake = os.path.join(self.tmp, "VBCSCompiler")
        os.symlink("/bin/sleep", fake)                              # 命令行里带 VBCSCompiler,命中"共享编译服务器"
        script = os.path.join(self.tmp, "driver.sh")
        with open(script, "w", encoding="utf-8") as f:
            f.write("'%s' 600 &\npython3 -c 'import time; time.sleep(600)' WD_T_PLAIN_CHILD &\nwait\n" % fake)
        root = self.spawn_orphan(["sh", script], repo)
        cfg = self.cfg(repo)
        vb = self.wait_for(lambda: next((p.pid for p in wd_core.snapshot().values() if p.cmd.startswith(fake) and p.ppid == root), None), 8, "编译服务器子进程")
        plain = self.wait_for(lambda: next((p.pid for p in wd_core.snapshot().values() if "WD_T_PLAIN_CHILD" in p.cmd and p.ppid == root), None), 8, "普通子进程")
        self._roots.append(vb)
        self.advance(repo, cfg, state, 0)
        self.advance(repo, cfg, state, 40)
        self.wait_for(lambda: not alive(root) and not alive(plain), 8, "根进程和普通子进程应当被清掉")
        self.assertTrue(alive(vb), "共享的编译服务器不能被连带杀掉")

    def test_sleep_churn_does_not_count_as_progress(self) -> None:
        repo, state = self.make_repo(), {}
        # 纯 sh+sleep 的树是看不到环境的(系统二进制受 SIP 保护),没有出身证明就不动——这是对的;所以驱动用 python
        driver = [sys.executable, "-c", "import subprocess\nwhile True: subprocess.run(['sleep', '1'])  # WD_T_CHURN"]
        pid = self.spawn_orphan(driver, repo)
        cfg = self.cfg(repo, cpu_epsilon_seconds=0.25)
        self.advance(repo, cfg, state, 0, real_sleep=1.5)            # 先让解释器启动完,启动开销不算进展
        done, ctx = False, None
        for minutes in (15, 40, 80):                                 # 两次观察之间 sleep 子进程换了好几轮
            ctx = self.advance(repo, cfg, state, minutes, real_sleep=2.3)
            if ctx.report.select("proc", "done"):
                done = True
                break
        # 若 sleep 的出现/消失被算成了进展,这棵树就永远不会被判闲置,这里就会失败
        self.assertTrue(done, self.texts(ctx, "proc"))
        self.wait_for(lambda: not alive(pid), 8, "只剩 sleep 空转的循环应当被判为闲置")

    def test_missing_instruments_count_as_progress_never_as_idle(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.sleeper("WD_T_BLIND"), repo)
        cfg = self.cfg(repo)

        def blind(minutes: float) -> wd_core.Context:
            ctx = self.make_ctx(repo, cfg, state, 1000.0 + minutes * 60)
            ctx.net, ctx.net_checked = None, True                    # 模拟 nettop 不可用
            wd_procs.run(ctx)
            return ctx
        blind(0)
        blind(40)
        ctx = blind(80)
        self.assertFalse(ctx.report.select("proc", "done"))
        self.assertIn("仪表不全", self.texts(ctx, "proc", "keep"))
        self.assertTrue(alive(pid))

    def test_stale_observations_are_discarded(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.sleeper("WD_T_GAP"), repo)
        cfg = self.cfg(repo)
        self.advance(repo, cfg, state, 0)
        ctx = self.advance(repo, cfg, state, 400)                   # 超过 180 分钟的间隔:旧记录作废,重新计时
        self.assertFalse(ctx.report.select("proc", "done"))
        self.assertIn("刚开始观察", self.texts(ctx, "proc", "keep"))
        self.assertTrue(alive(pid))

    def test_corrupted_state_restarts_the_clock_instead_of_crashing(self) -> None:
        repo = self.make_repo()
        pid = self.spawn_orphan(self.sleeper("WD_T_CORRUPT"), repo)
        cfg = self.cfg(repo)
        for bad in ({"procs": "oops"}, {"procs": {"x": 1}}):
            ctx = self.advance(repo, cfg, copy.deepcopy(bad), 0)
            self.assertFalse(ctx.report.select("proc", "done"))
        root_key = wd_core.snapshot()[pid].key
        for bad_rec in ({"first_up": 0, "last_up": 1000.0, "progress_up": 0, "stats": {"a": [1]}},
                        {"first_up": "x", "last_up": 1000.0, "progress_up": 0, "stats": {}}):
            ctx = self.advance(repo, cfg, {"procs": {root_key: bad_rec}}, 0)
            self.assertFalse(ctx.report.select("proc", "done"))
        self.assertTrue(alive(pid))

    def test_throttle_limits_kills_per_run(self) -> None:
        repo, state = self.make_repo(), {}
        pids = [self.spawn_orphan(self.sleeper("WD_T_THR%d" % i), repo) for i in range(2)]
        cfg = self.cfg(repo, max_trees_per_run=1)
        self.advance(repo, cfg, state, 0)
        ctx = self.advance(repo, cfg, state, 40)
        self.assertEqual(len(ctx.report.select("proc", "done")), 1)
        self.assertIn("上限", self.texts(ctx, "proc", "skip"))
        self.wait_for(lambda: sum(1 for p in pids if alive(p)) == 1, 8, "这一轮只该杀一棵")
        ctx = self.advance(repo, cfg, state, 41)
        self.wait_for(lambda: not any(alive(p) for p in pids), 8, "下一轮再清另一棵")

    def test_recheck_aborts_when_new_members_show_up(self) -> None:
        repo = self.make_repo()
        root = self.spawn_orphan(["sh", "-c", "python3 -c 'import time; time.sleep(600)' WD_T_NEWKID & wait"], repo)
        self.wait_for(lambda: any("WD_T_NEWKID" in p.cmd and p.ppid == root for p in wd_core.snapshot().values()), 8, "子进程")
        cfg = self.cfg(repo)
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        sigs, protected, unknown = wd_procs.compile_policy(cfg["processes"])
        tree = wd_procs.Tree(root=ctx.procs[root], members=[ctx.procs[root]], sig=wd_procs.match_sig(sigs, ctx.procs[root].cmd) or unknown)
        why, victims = wd_procs.recheck(ctx, tree, sigs)
        self.assertIn("新成员", why or "")
        self.assertEqual(victims, [])
        self.assertTrue(alive(root))

    def test_self_and_ancestors_are_never_candidates(self) -> None:
        repo = self.make_repo()
        cfg = self.cfg(repo, idle_minutes=0, confirm_minutes=0)
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_procs.run(ctx)
        self.assertTrue(alive(os.getpid()))
        self.assertFalse(ctx.report.select("proc", "done"))

    def test_stopped_process_is_continued_then_terminated(self) -> None:
        repo, state = self.make_repo(), {}
        pid = self.spawn_orphan(self.sleeper("WD_T_STOPPED"), repo)
        # 只发一次 SIGSTOP 会偶发落空(实测约 5%):Homebrew 的 python3 是启动跳板,会再 exec 成真正的解释器,
        # 信号恰好落在 exec 跳转的窗口里就丢了,进程一直是 S 态。所以重发,直到 ps 确认它真的停了;对已停的进程重发无害。
        def stopped() -> bool:
            os.kill(pid, signal.SIGSTOP)
            return "T" in wd_util.run(["ps", "-o", "stat=", "-p", str(pid)])[1]
        self.wait_for(stopped, 5, "进程被暂停")
        cfg = self.cfg(repo)
        self.advance(repo, cfg, state, 0)
        self.advance(repo, cfg, state, 40)
        self.wait_for(lambda: not alive(pid), 10, "被暂停的闲置进程也应当被清掉")


class TestZombies(Base):
    def test_zombie_parent_is_nudged_reported_but_never_killed(self) -> None:
        repo = self.make_repo()
        parent = self.spawn_child([sys.executable, "-c", "import subprocess, time; subprocess.Popen(['true']); time.sleep(600)  # WD_T_ZPARENT"], repo)
        self.wait_for(lambda: any(p.zombie and p.ppid == parent for p in wd_core.snapshot().values()), 8, "出现僵尸")
        cfg = self.cfg(repo)
        _, protected, _ = wd_procs.compile_policy(cfg["processes"])
        state: Dict[str, Any] = {}

        def step(minutes: float) -> wd_core.Context:
            ctx = self.make_ctx(repo, cfg, state, 1000.0 + minutes * 60)
            wd_procs.handle_zombies(ctx, protected)
            return ctx
        ctx = step(0)
        self.assertIn("刚出现", self.texts(ctx, "zombie", "keep"))
        ctx = step(6)
        self.assertIn("SIGCHLD", self.texts(ctx, "zombie", "done"))
        ctx = step(10)
        self.assertIn("无法被杀", self.texts(ctx, "zombie", "report"))
        self.assertTrue(alive(parent), "父进程不能被动")
        self.assertTrue(any(p.zombie and p.ppid == parent for p in wd_core.snapshot().values()), "Python 不理会 SIGCHLD,僵尸应仍在")

    def test_zombies_of_unrelated_parents_are_ignored(self) -> None:
        repo = self.make_repo()
        elsewhere = os.path.join(self.tmp, "elsewhere")
        os.makedirs(elsewhere)
        parent = self.spawn_child([sys.executable, "-c", "import subprocess, time; subprocess.Popen(['true']); time.sleep(600)  # WD_T_ZFOREIGN"], elsewhere, agent=False)
        self.wait_for(lambda: any(p.zombie and p.ppid == parent for p in wd_core.snapshot().values()), 8, "出现僵尸")
        cfg = self.cfg(repo)
        _, protected, _ = wd_procs.compile_policy(cfg["processes"])
        ctx = self.make_ctx(repo, cfg, {}, 5000)
        wd_procs.handle_zombies(ctx, protected)
        self.assertEqual(ctx.report.items, [])


# ====================================================================== 垃圾

class TestGarbage(Base):
    def unused_pid(self) -> int:
        p = subprocess.Popen(["true"])
        p.wait()
        self.assertNotIn(p.pid, wd_core.snapshot())
        return p.pid

    def test_ipc_leftovers_are_removed_only_when_the_owner_is_provably_dead(self) -> None:
        repo = self.make_repo()
        d = os.path.join(self.tmp, "tmpdir")
        os.makedirs(d, exist_ok=True)
        me = os.getpid()
        ep = wd_core.start_epoch(wd_core.snapshot()[me].lstart)
        dead = self.unused_pid()

        def fifo(name: str) -> str:
            p = os.path.join(d, name)
            os.mkfifo(p)
            return p
        gone = [fifo("clr-debug-pipe-%d-%d-in" % (dead, ep)), fifo("clr-debug-pipe-%d-%d-out" % (dead, ep)),
                make_socket(d, "dotnet-diagnostic-%d-%d-socket" % (dead, ep)), make_socket(d, "MSBuild%d" % dead),
                fifo("clr-debug-pipe-%d-%d-out" % (me, ep - 5000))]           # pid 还在,但启动时刻对不上:pid 被复用了
        kept = [fifo("clr-debug-pipe-%d-%d-in" % (me, ep)),                  # 属主活着
                make_socket(d, "MSBuild%d" % me),
                fifo("clr-debug-pipe-%d-123-in" % me),                       # 键不像 epoch,不敢判断
                os.path.join(d, "clr-debug-pipe-%d-%d-out" % (dead, ep + 1))]
        with open(kept[-1], "w", encoding="utf-8") as f:                     # 类型不对(普通文件)
            f.write("x")
        other = os.path.join(d, "unrelated-file")
        with open(other, "w", encoding="utf-8") as f:
            f.write("x")
        kept.append(other)
        cfg = self.cfg(repo)
        ctx = self.make_ctx(repo, cfg, {}, 1000, dry_run=True)
        wd_garbage.clean_ipc(ctx, wd_core.snapshot())
        self.assertTrue(all(os.path.lexists(p) for p in gone + kept), "dry-run 不能删任何东西")
        self.assertIn("5", self.texts(ctx, "garbage", "would"))
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.clean_ipc(ctx, wd_core.snapshot())
        self.assertEqual([p for p in gone if os.path.lexists(p)], [])
        self.assertEqual([p for p in kept if not os.path.lexists(p)], [])

    def test_young_ipc_files_are_left_alone(self) -> None:
        repo = self.make_repo()
        d = os.path.join(self.tmp, "tmpdir")
        os.makedirs(d, exist_ok=True)
        dead = self.unused_pid()
        p = os.path.join(d, "clr-debug-pipe-%d-%d-in" % (dead, 1_700_000_000))
        os.mkfifo(p)
        cfg = self.cfg(repo)
        cfg["garbage"]["ipc_min_age_seconds"] = 120                 # 默认值:刚建的文件属主也许还没出现在进程表里
        wd_garbage.clean_ipc(self.make_ctx(repo, cfg, {}, 1000), wd_core.snapshot())
        self.assertTrue(os.path.lexists(p))
        make_old(p, 1)
        wd_garbage.clean_ipc(self.make_ctx(repo, cfg, {}, 1000), wd_core.snapshot())
        self.assertFalse(os.path.lexists(p))

    def test_msbuild_temp_only_removes_old_empty_directories(self) -> None:
        repo = self.make_repo()
        d = os.path.join(self.tmp, "tmpdir")
        os.makedirs(d, exist_ok=True)
        old_empty, young_empty, old_full = (os.path.join(d, n) for n in ("MSBuildTempAAAAAA", "MSBuildTempBBBBBB", "MSBuildTempCCCCCC"))
        for p in (old_empty, young_empty, old_full):
            os.mkdir(p)
        with open(os.path.join(old_full, "keep.txt"), "w", encoding="utf-8") as f:
            f.write("x")
        for p in (old_empty, old_full):
            make_old(p, 10)
        unrelated = os.path.join(d, "SomethingElse")
        os.mkdir(unrelated)
        make_old(unrelated, 10)
        wd_garbage.clean_msbuild_temp(self.make_ctx(repo, self.cfg(repo), {}, 1000))
        self.assertFalse(os.path.exists(old_empty))
        self.assertTrue(all(os.path.exists(p) for p in (young_empty, old_full, unrelated, os.path.join(old_full, "keep.txt"))))

    def test_stale_heavy_locks(self) -> None:
        repo = self.make_repo()
        locks = os.path.join(self.tmp, "locks")
        os.makedirs(locks)

        def lock(name: str, pid: Optional[int], age_hours: float = 1) -> str:
            p = os.path.join(locks, name)
            os.mkdir(p)
            if pid is not None:
                with open(os.path.join(p, "pid"), "w", encoding="utf-8") as f:
                    f.write("%d\n" % pid)
            make_old(p, age_hours)
            return p
        holder = self.spawn_child(["sh", "-c", "sleep 600; true", "heavy.sh"], repo)     # 命令行带 heavy.sh 的活持有者
        self.wait_for(lambda: "heavy.sh" in wd_core.snapshot()[holder].cmd, 5, "持有者命令行")
        dead_lock = lock("heavy.lock.d", self.unused_pid())
        reused_lock = lock("heavy.lock2.d", os.getpid())                     # pid 活着,但是个无关进程:pid 被复用了
        nopid_lock = lock("heavy.lock3.d", None)
        live_lock = lock("heavy.lock4.d", holder)
        young_lock = lock("heavy.lock5.d", self.unused_pid(), age_hours=0)   # 刚建的:给持有者留出写 pid 的时间
        plain_file = os.path.join(locks, "round.lock")
        touch(plain_file)
        not_a_lock = os.path.join(locks, "keepme.d")
        os.mkdir(not_a_lock)
        make_old(not_a_lock, 5)
        cfg = self.cfg(repo)
        cfg["garbage"]["lock_dirs"] = [locks]
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.clean_heavy_locks(ctx, wd_core.snapshot())
        for p in (dead_lock, reused_lock, nopid_lock):
            self.assertFalse(os.path.exists(p), p)
        for p in (live_lock, young_lock, plain_file, not_a_lock):
            self.assertTrue(os.path.exists(p), p)

    def test_git_locks_whitelist_age_and_live_git_process(self) -> None:
        repo = self.make_repo()
        sh("git", "-C", repo, "worktree", "add", "-q", "-b", "w1", os.path.join(self.tmp, "w1"))
        common = os.path.join(repo, ".git")
        os.makedirs(os.path.join(common, "cursor", "crepe"))

        def mklock(rel: str, age: float) -> str:
            p = os.path.join(common, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            touch(p)
            make_old(p, age)
            return p
        stale = [mklock("index.lock", 2), mklock("refs/heads/x.lock", 2), mklock("worktrees/w1/index.lock", 2)]
        young = mklock("HEAD.lock", 0)
        foreign = mklock("cursor/crepe/build.lock", 50)                     # 别人的锁,绝不能碰
        not_whitelisted = mklock("something.lock", 50)
        fake_git = os.path.join(self.tmp, "git")
        os.symlink("/bin/sleep", fake_git)
        cfg = self.cfg(repo)
        pid = self.spawn_child([fake_git, "600"], repo)                      # 工程里有个"git 进程"在跑
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.clean_git_locks(ctx, ctx.procs)
        self.assertTrue(all(os.path.exists(p) for p in stale), "有 git 进程在工程里跑时一律不动")
        os.kill(pid, signal.SIGKILL)
        self.wait_for(lambda: not alive(pid), 5, "假 git 退出")
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.clean_git_locks(ctx, ctx.procs)
        self.assertEqual([p for p in stale if os.path.exists(p)], [])
        self.assertTrue(all(os.path.exists(p) for p in (young, foreign, not_whitelisted)))

    def test_worktree_prune_removes_only_registrations_of_missing_directories(self) -> None:
        repo = self.make_repo()
        keep, gone = os.path.join(self.tmp, "keep"), os.path.join(self.tmp, "gone")
        sh("git", "-C", repo, "worktree", "add", "-q", "-b", "k", keep)
        sh("git", "-C", repo, "worktree", "add", "-q", "-b", "g", gone)
        shutil.rmtree(gone)
        for n in ("index", "gitdir"):                               # git 用 index 的修改时间判断"是否已经闲置够久"
            make_old(os.path.join(repo, ".git", "worktrees", "gone", n), 10)
        cfg = self.cfg(repo)
        wd_garbage.prune_worktrees(self.make_ctx(repo, cfg, {}, 1000, dry_run=True))
        self.assertEqual(len(wd_core.list_worktrees(repo)), 3)
        wd_garbage.prune_worktrees(self.make_ctx(repo, cfg, {}, 1000))
        self.assertEqual([os.path.basename(w.path) for w in wd_core.list_worktrees(repo)], ["repo", "keep"])

    # ---- 构建产物
    def project(self, root: str, with_obj: bool = True) -> None:
        os.makedirs(os.path.join(root, "src", "A", "bin", "Debug"), exist_ok=True)
        touch(os.path.join(root, "src", "A", "A.csproj"))
        touch(os.path.join(root, "src", "A", "bin", "Debug", "a.dll"), "x" * 1000)
        if with_obj:
            os.makedirs(os.path.join(root, "src", "A", "obj"), exist_ok=True)
            touch(os.path.join(root, "src", "A", "obj", "project.assets.json"), "{}")
        os.makedirs(os.path.join(root, "tools", "B", "bin"), exist_ok=True)         # 没被 .gitignore 覆盖的 bin
        touch(os.path.join(root, "tools", "B", "B.csproj"))
        touch(os.path.join(root, "tools", "B", "bin", "tracked.txt"), "x")

    def idle_worktree(self, repo: str, name: str, old: bool = True) -> str:
        wt = os.path.join(self.tmp, name)
        sh("git", "-C", repo, "worktree", "add", "-q", "-b", name, wt)
        self.project(wt)
        if old:
            make_tree_old(wt)
            adm = os.path.join(repo, ".git", "worktrees", name)
            for n in ("index", "HEAD"):
                make_old(os.path.join(adm, n))
        return wt

    def test_build_outputs_cleanup_rules(self) -> None:
        repo = self.make_repo()
        self.project(repo)
        make_tree_old(repo)
        idle = self.idle_worktree(repo, "idle")
        busy = self.idle_worktree(repo, "busy", old=False)
        used = self.idle_worktree(repo, "used")
        rebuilding = self.idle_worktree(repo, "rebuilding")
        touch(os.path.join(rebuilding, "src", "A", "obj", "fresh.cache"), "x")     # obj 里有新文件:正在构建
        self.spawn_child(["sleep", "600"], used)
        symlinked = self.idle_worktree(repo, "symlinked")
        shutil.rmtree(os.path.join(symlinked, "src", "A", "bin"))
        target = os.path.join(self.tmp, "real-bin")
        os.mkdir(target)
        touch(os.path.join(target, "precious"), "x")
        make_tree_old(target)
        os.symlink(target, os.path.join(symlinked, "src", "A", "bin"))
        cfg = self.cfg(repo)

        def bin_of(wt: str) -> str:
            return os.path.join(wt, "src", "A", "bin")
        ctx = self.make_ctx(repo, cfg, {}, 1000, dry_run=True)
        wd_garbage.clean_build_outputs(ctx, wd_garbage.Usage(ctx))
        self.assertTrue(os.path.exists(bin_of(idle)), "dry-run 不能删")
        self.assertIn("idle", self.texts(ctx, "garbage", "would"))
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.clean_build_outputs(ctx, wd_garbage.Usage(ctx))
        self.assertFalse(os.path.exists(bin_of(idle)), "整棵树闲置、bin 被 git 忽略:应删")
        self.assertTrue(os.path.exists(os.path.join(idle, "src", "A", "obj", "project.assets.json")), "obj 要保留(--no-restore 要用)")
        self.assertTrue(os.path.exists(os.path.join(idle, "tools", "B", "bin", "tracked.txt")), "没被 git 忽略的 bin 不碰")
        for wt, why in ((busy, "近期有活动"), (used, "有进程在用"), (rebuilding, "obj 里有新文件"), (repo, "主工作区永远不动")):
            self.assertTrue(os.path.exists(bin_of(wt)), why)
        self.assertTrue(os.path.exists(os.path.join(target, "precious")), "符号链接指向的内容不能删")
        self.assertTrue(os.path.islink(bin_of(symlinked)))

    def test_purge_obj_is_opt_in(self) -> None:
        repo = self.make_repo()
        wt = self.idle_worktree(repo, "idle")
        cfg = self.cfg(repo)
        cfg["garbage"]["build_outputs"]["purge_obj"] = True
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.clean_build_outputs(ctx, wd_garbage.Usage(ctx))
        self.assertFalse(os.path.exists(os.path.join(wt, "src", "A", "obj")))

    def test_scratch_clones_are_cleaned_too(self) -> None:
        repo = self.make_repo()
        clone = os.path.join(self.tmp, "clone")
        sh("git", "clone", "-q", repo, clone)
        self.project(clone)
        make_tree_old(clone)
        for n in ("index", "HEAD"):
            make_old(os.path.join(clone, ".git", n))
        cfg = self.cfg(repo)
        ctx = self.make_ctx(repo, cfg, {}, 1000, clones=[clone])
        wd_garbage.clean_build_outputs(ctx, wd_garbage.Usage(ctx))
        self.assertFalse(os.path.exists(os.path.join(clone, "src", "A", "bin")))

    # ---- worktree 清点
    def plain_worktree(self, repo: str, name: str) -> str:
        wt = os.path.join(self.tmp, name)
        sh("git", "-C", repo, "worktree", "add", "-q", "-b", name, wt)
        return wt

    def test_worktree_audit_and_opt_in_removal(self) -> None:
        repo = self.make_repo()
        clean = self.plain_worktree(repo, "mergedclean")
        dirty = self.plain_worktree(repo, "dirty")
        touch(os.path.join(dirty, "untracked.txt"), "x")
        unmerged = self.plain_worktree(repo, "unmerged")
        sh("git", "-C", unmerged, "commit", "-q", "--allow-empty", "-m", "own work")
        handoff = self.plain_worktree(repo, "handoff")
        touch(os.path.join(handoff, "HANDOFF.json"), "{}")       # 被忽略的交接文件:status 看不见,但不能删
        for wt, name in ((clean, "mergedclean"), (dirty, "dirty"), (unmerged, "unmerged"), (handoff, "handoff")):
            make_old(wt)
            for n in ("index", "HEAD"):
                make_old(os.path.join(repo, ".git", "worktrees", name, n))
        cfg = self.cfg(repo)
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.audit_worktrees(ctx, wd_garbage.Usage(ctx))
        text = self.texts(ctx, "worktree")
        self.assertIn("可安全移除 1 个", text)
        self.assertIn("mergedclean", text)
        self.assertTrue(all(os.path.isdir(p) for p in (clean, dirty, unmerged, handoff)), "默认只报告,不删")
        cfg["garbage"]["worktrees"]["remove_merged_clean"] = True
        ctx = self.make_ctx(repo, cfg, {}, 1000)
        wd_garbage.audit_worktrees(ctx, wd_garbage.Usage(ctx))
        self.assertFalse(os.path.exists(clean))
        self.assertTrue(all(os.path.isdir(p) for p in (dirty, unmerged, handoff)), "有改动/未合并/有交接文件的一个都不能删")

    def test_heavy_jobs_are_rate_limited_by_interval(self) -> None:
        repo = self.make_repo()
        wt = self.idle_worktree(repo, "idle")
        cfg = self.cfg(repo)
        state: Dict[str, Any] = {"garbage": {"heavy_wall": time.time()}}
        ctx = self.make_ctx(repo, cfg, state, 1000)
        wd_garbage.run(ctx)
        self.assertTrue(os.path.exists(os.path.join(wt, "src", "A", "bin")), "刚做过重活,这一轮不做")
        state["garbage"]["heavy_wall"] = time.time() - 3 * HOUR
        wd_garbage.run(self.make_ctx(repo, cfg, state, 1000))
        self.assertFalse(os.path.exists(os.path.join(wt, "src", "A", "bin")))
        self.assertLess(time.time() - state["garbage"]["heavy_wall"], 60)

    def test_one_failing_cleaner_does_not_stop_the_others(self) -> None:
        repo = self.make_repo()
        d = os.path.join(self.tmp, "tmpdir")
        os.makedirs(d, exist_ok=True)
        dead = self.unused_pid()
        p = os.path.join(d, "clr-debug-pipe-%d-%d-in" % (dead, 1_700_000_000))
        os.mkfifo(p)
        ctx = self.make_ctx(repo, self.cfg(repo), {}, 1000)
        with mock.patch.object(wd_garbage, "clean_msbuild_temp", side_effect=RuntimeError("boom")):
            wd_garbage.run(ctx)
        self.assertTrue(ctx.report.select("garbage", "error"))
        self.assertFalse(os.path.lexists(p), "IPC 清理应照常完成")


# ====================================================================== 整体

class TestMain(Base):
    def env(self) -> Dict[str, str]:
        home, logs, agents = (os.path.join(self.tmp, n) for n in ("home", "logs", "agents"))
        return {"DNF_WATCHDOG_HOME": home, "DNF_WATCHDOG_LOGS": logs, "DNF_WATCHDOG_AGENTS_DIR": agents,
                "DNF_WATCHDOG_LABEL": "com.dnfunity.watchdog.test%d" % os.getpid()}

    def test_state_is_discarded_after_reboot_and_version_change(self) -> None:
        with mock.patch.dict(os.environ, self.env()):
            wd_util.atomic_write_json(wd_main.state_path(), {"meta": {"boot": 111, "version": wd_main.STATE_VERSION}, "procs": {"a": 1}})
            self.assertEqual(wd_main.load_state(111)["procs"], {"a": 1})
            self.assertNotIn("procs", wd_main.load_state(222))
            wd_util.atomic_write_json(wd_main.state_path(), {"meta": {"boot": 111, "version": 0}, "procs": {"a": 1}})
            self.assertNotIn("procs", wd_main.load_state(111))
            with open(wd_main.state_path(), "w") as f:
                f.write("{broken")
            self.assertEqual(wd_main.load_state(111)["meta"]["boot"], 111)

    def test_plist_is_well_formed(self) -> None:
        with mock.patch.dict(os.environ, self.env()):
            pl = wd_main.build_plist("/usr/bin/python3", "/x/watchdog.py", 10)
            self.assertEqual(pl["ProgramArguments"], ["/usr/bin/python3", "/x/watchdog.py", "run"])
            self.assertEqual(pl["StartInterval"], 600)
            self.assertTrue(pl["RunAtLoad"])
            self.assertTrue(pl["Label"].startswith("com.dnfunity.watchdog.test"))
            self.assertEqual(plistlib.loads(plistlib.dumps(pl))["Label"], pl["Label"])

    def test_run_once_end_to_end_cleans_and_records(self) -> None:
        repo = self.make_repo()
        d = os.path.join(self.tmp, "tmpdir")
        os.makedirs(d, exist_ok=True)
        dead = subprocess.Popen(["true"])
        dead.wait()
        ipc = os.path.join(d, "clr-debug-pipe-%d-%d-in" % (dead.pid, 1_700_000_000))
        os.mkfifo(ipc)
        pid = self.spawn_orphan(self.sleeper("WD_T_E2E"), repo)
        cfg = self.cfg(repo, idle_minutes=0, confirm_minutes=0)           # 首次观察就判定闲置:整轮不用等时间
        with mock.patch.dict(os.environ, self.env()):
            ctx, rc = wd_main.run_once(cfg, [], dry_run=False, persist=True)
            self.assertEqual(rc, 0, wd_main.format_report(ctx))
            self.wait_for(lambda: not alive(pid), 8, "端到端:闲置孤儿应被清掉")
            self.assertFalse(os.path.lexists(ipc))
            last = wd_util.read_json(wd_main.last_run_path())
            self.assertGreaterEqual(last["done"], 2)
            self.assertFalse(last["dry_run"])
            self.assertIn("watchdog.log", os.listdir(wd_config.log_dir()))
            self.assertIn("ACT", read_text(wd_main.log_path()))
            self.assertEqual(wd_util.read_json(wd_main.state_path())["meta"]["version"], wd_main.STATE_VERSION)

    def test_invalid_config_forces_report_only(self) -> None:
        repo = self.make_repo()
        pid = self.spawn_orphan(self.sleeper("WD_T_FORCED"), repo)
        cfg = self.cfg(repo, idle_minutes=0, confirm_minutes=0)
        with mock.patch.dict(os.environ, self.env()):
            ctx, _ = wd_main.run_once(cfg, ["processes.idle_minute 拼错了"], dry_run=False, persist=True)
            self.assertTrue(ctx.dry_run)
            self.assertFalse(ctx.report.select("proc", "done"))
            self.assertTrue(wd_util.read_json(wd_main.last_run_path())["forced_dry_run"])
            self.assertIn("降级", read_text(wd_main.log_path()))
        time.sleep(0.5)
        self.assertTrue(alive(pid))

    def test_scan_is_read_only(self) -> None:
        repo = self.make_repo()
        d = os.path.join(self.tmp, "tmpdir")
        os.makedirs(d, exist_ok=True)
        dead = subprocess.Popen(["true"])
        dead.wait()
        ipc = os.path.join(d, "clr-debug-pipe-%d-%d-in" % (dead.pid, 1_700_000_000))
        os.mkfifo(ipc)
        cfg = self.cfg(repo)
        with mock.patch.dict(os.environ, self.env()):
            ctx, _ = wd_main.run_once(cfg, [], dry_run=True, persist=False)
            self.assertTrue(os.path.lexists(ipc))
            self.assertFalse(os.path.exists(wd_main.state_path()))
            self.assertFalse(os.path.exists(wd_main.last_run_path()))
            self.assertFalse(os.path.exists(wd_main.log_path()))
            self.assertIn("预演", wd_main.format_report(ctx))

    def test_pause_and_lock(self) -> None:
        with mock.patch.dict(os.environ, self.env()), contextlib.redirect_stdout(io.StringIO()):
            ns = mock.Mock(config=None, dry_run=False, deep=False, json=False)
            wd_main.cmd_pause(mock.Mock())
            self.assertEqual(wd_main.cmd_run(ns), 0)
            self.assertFalse(os.path.exists(wd_main.last_run_path()), "暂停时不能跑")
            self.assertIn("已暂停", read_text(wd_main.log_path()))
            wd_main.cmd_resume(mock.Mock())
            self.assertFalse(os.path.exists(wd_main.paused_path()))
            fd = wd_main.acquire_lock()
            self.assertIsNotNone(fd)
            self.assertIsNone(wd_main.acquire_lock(), "上一轮没结束,新一轮不能同时跑")
            os.close(fd)
            fd2 = wd_main.acquire_lock()
            self.assertIsNotNone(fd2)
            os.close(fd2)

    def test_install_copies_a_vendored_copy_and_writes_a_valid_plist(self) -> None:
        env = dict(os.environ)
        env.update(self.env())
        script = os.path.join(HERE, "watchdog.py")
        r = subprocess.run([sys.executable, script, "install", "--no-load", "--interval", "7"], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        app_bin = os.path.join(env["DNF_WATCHDOG_HOME"], "bin")
        installed = sorted(os.listdir(app_bin))
        self.assertIn("watchdog.py", installed)
        self.assertTrue(all(n == "watchdog.py" or n.startswith("wd_") for n in installed))
        self.assertNotIn("test_watchdog.py", installed)
        plist = os.path.join(env["DNF_WATCHDOG_AGENTS_DIR"], env["DNF_WATCHDOG_LABEL"] + ".plist")
        with open(plist, "rb") as f:
            pl = plistlib.load(f)
        self.assertEqual(pl["StartInterval"], 420)
        self.assertEqual(pl["ProgramArguments"][1], os.path.join(app_bin, "watchdog.py"))
        self.assertEqual(wd_util.run(["plutil", "-lint", plist])[0], 0)
        cfg = json.loads(read_text(os.path.join(env["DNF_WATCHDOG_HOME"], "config.json")))
        self.assertTrue(os.path.isdir(os.path.join(cfg["repo_root"], ".git")))
        # 装好的那份副本自己能跑(目录里的代码互相独立,不依赖仓库里的文件)
        r = subprocess.run([sys.executable, os.path.join(app_bin, "watchdog.py"), "status"], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("安装:是", r.stdout)
        # 卸载(标签是一次性的,不会碰真正装好的任务)
        r = subprocess.run([sys.executable, os.path.join(app_bin, "watchdog.py"), "uninstall", "--purge"], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertFalse(os.path.exists(plist))
        self.assertFalse(os.path.exists(env["DNF_WATCHDOG_HOME"]))

    def test_default_schedule_is_every_20_minutes(self) -> None:
        self.assertEqual(wd_config.DEFAULTS["interval_minutes"], 20)
        env = dict(os.environ)
        env.update(self.env())
        script = os.path.join(HERE, "watchdog.py")
        plist = os.path.join(env["DNF_WATCHDOG_AGENTS_DIR"], env["DNF_WATCHDOG_LABEL"] + ".plist")

        def installed_interval() -> int:
            r = subprocess.run([sys.executable, script, "install", "--no-load"], env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            with open(plist, "rb") as f:
                return int(plistlib.load(f)["StartInterval"])
        try:
            self.assertEqual(installed_interval(), 1200, "不带 --interval 安装:默认 20 分钟")
            cfg_path = os.path.join(env["DNF_WATCHDOG_HOME"], "config.json")
            cfg = json.loads(read_text(cfg_path))
            cfg["interval_minutes"] = 30
            wd_util.atomic_write_json(cfg_path, cfg)
            self.assertEqual(installed_interval(), 1800, "覆盖文件里的 interval_minutes 要被采用")
        finally:
            subprocess.run([sys.executable, script, "uninstall", "--purge"], env=env, capture_output=True, text=True)

    def test_selfcheck_passes_on_this_machine(self) -> None:
        env = dict(os.environ)
        env.update(self.env())
        r = subprocess.run([sys.executable, os.path.join(HERE, "watchdog.py"), "selfcheck"], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("自检通过", r.stdout)

    def test_source_files_have_no_local_paths_or_credentials(self) -> None:
        guard = os.path.join(HERE, "..", "guard")
        if not os.path.isdir(guard):
            self.skipTest("没有提交守卫")
        sys.path.insert(0, guard)
        try:
            import precommit
            import secretscan
        finally:
            sys.path.remove(guard)
        for name in sorted(os.listdir(HERE)):
            if name.endswith(".py"):
                text = read_text(os.path.join(HERE, name))
                for label, rx in (("本机路径", secretscan.LOCAL_PATH_RE), ("凭据", secretscan.CREDENTIAL_RE), ("反编译特征", precommit.DECOMPILER_RE)):
                    self.assertIsNone(rx.search(text), "%s 含有%s" % (name, label))


if __name__ == "__main__":
    unittest.main(verbosity=2)
