#!/usr/bin/env python3
"""守卫、历史审计与发布脚本的自测。全部用合成的取值与临时仓库,不需要任何游戏数据,也不碰真实的 local.secrets.env。

运行:python3 -m unittest discover -s tools/guard -p 'test_*.py'
覆盖:
  1. secretscan  原样规则(长取值子串、短取值按独立单词、过短忽略)、各种编码写法、无关数据不误报、取值不外泄、取值文件的查找顺序
  2. precommit   守卫对路径、扩展名、体积、魔数、反编译器输出、凭据(含编码写法)的拦截,以及找不到取值文件时的提示
  3. audit_refs  逐引用的 PASS / SCRUB / FAIL 判定与归属(一个坏分支不会拖累好分支)、各类问题、允许脱敏的边界
  4. publish     用本地裸仓库演练全流程:审计、脱敏、创建、快进、被改写的分支、dry-run、main 不合格时远端分文不动

文件里的"危险字样"(反编译器输出、令牌、本机路径、私钥头)一律在运行时拼出来,免得这份测试自己被守卫和审计拦下。
"""
import base64
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import audit_refs  # noqa: E402
import precommit  # noqa: E402
import secretscan  # noqa: E402

PY = sys.executable
IP, USER, PASSWORD, ACCOUNT, PASSWORD2, KEYHEX = "203.0.113.77", "svcops", "Qx7!mP2zLk", "zq81", "Rt5Hn8Wc3", "a1b2c3d4e5f60718"
SYN = [("SYN_IP", IP), ("SYN_USER", USER), ("SYN_PASSWORD", PASSWORD), ("SYN_ACCOUNT", ACCOUNT),
       ("SYN_PASSWORD2", PASSWORD2), ("SYN_KEY_HEX", KEYHEX)]
SYN_FILE_TEXT = "# 合成取值,仅供自测\n" + "".join("%s=%s\n" % kv for kv in SYN) + "SYN_TOO_SHORT=ab\n"

BASE_ENV = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1",
                GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
                GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid")
BASE_ENV.pop("DNF_SECRETS_FILE", None)


def hexs(text, sep=" ", upper=False):
    h = [("%02X" if upper else "%02x") % b for b in text.encode()]
    return sep.join(h)


class Repo:
    clock = 0

    def __init__(self, path, bare=False):
        self.path = path
        os.makedirs(path, exist_ok=True)
        self.git(*(["init", "-q", "--bare"] if bare else ["init", "-q", "-b", "main"]))

    def git(self, *args, env=None, check=True):
        e = dict(BASE_ENV)
        e.update(env or {})
        p = subprocess.run(["git", "-C", self.path, *args], env=e, capture_output=True)
        if check and p.returncode:
            raise AssertionError("git %s 失败:%s" % (args, p.stderr.decode("utf-8", "replace")))
        return p.stdout.decode("utf-8", "replace").strip()

    def put(self, rel, data):
        full = os.path.join(self.path, rel)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        if isinstance(data, bytes):
            with open(full, "wb") as f:
                f.write(data)
        else:
            with open(full, "w", encoding="utf-8", newline="") as f:
                f.write(data)

    def commit(self, msg, files=None, remove=()):
        for rel, data in (files or {}).items():
            self.put(rel, data)
        for rel in remove:
            os.remove(os.path.join(self.path, rel))
        self.git("add", "-A", "-f")
        Repo.clock += 1
        stamp = "2020-01-01T00:%02d:%02d +0000" % divmod(Repo.clock, 60)
        self.git("commit", "-q", "--allow-empty", "-m", msg, env={"GIT_AUTHOR_DATE": stamp, "GIT_COMMITTER_DATE": stamp})
        return self.git("rev-parse", "HEAD")

    def branch(self, name, msg, files=None, remove=(), base="main"):
        self.git("checkout", "-q", "-b", name, base)
        sha = self.commit(msg, files, remove)
        self.git("checkout", "-q", "main")
        return sha


def tool(*args, cwd=None, env=None, input=None):
    e = dict(BASE_ENV)
    e.update(env or {})
    return subprocess.run([PY, *args], cwd=cwd, env=e, input=input, capture_output=True)


def decompiler_text():
    return "call " + "FU" + "N_" + "00401a2c" + " here\n"


def local_path_text():
    return "see " + "/Us" + "ers/zzuser/project/file\n"


def token_text():
    return "token=" + "gh" + "p_" + "A" * 36 + "\n"


def private_key_text():
    return "-----BEGIN " + "RSA PRIVATE KEY-----\nxxxx\n"


def leaked_tool_sh():
    return "#!/bin/sh\ntool --keys '%s\t' --x\n" % ACCOUNT


CLEAN_TOOL_SH = "#!/bin/sh\ntool --keys 'example\t' --x\n"


class SecretScanTests(unittest.TestCase):
    def setUp(self):
        self.m = secretscan.Matchers(SYN)

    def test_plain_rules(self):
        # 长取值按子串;3 到 7 位按独立单词;更短的在读取时就被忽略
        self.assertEqual(self.m.find("x" + PASSWORD + "y")[:2], ("SYN_PASSWORD", "原样"))
        self.assertIsNotNone(self.m.find("账号 %s 结束" % ACCOUNT))
        self.assertIsNone(self.m.find("a%s" % ACCOUNT))
        self.assertIsNone(self.m.find("%s0" % ACCOUNT))
        self.assertIsNone(self.m.find("_%s" % ACCOUNT))
        with tempfile.NamedTemporaryFile("w", suffix=".env", delete=False, encoding="utf-8") as f:
            f.write("# 注释\nexport SYN_A='%s'\nSYN_B=\"%s\"\nSYN_SHORT=ab\n不是赋值\n" % (PASSWORD, ACCOUNT))
        try:
            pairs = secretscan.load_secrets(f.name)
        finally:
            os.unlink(f.name)
        self.assertEqual(pairs, [("SYN_A", PASSWORD), ("SYN_B", ACCOUNT)])

    def test_selftest_passes(self):
        self.assertEqual(self.m.selftest(), [])

    def test_encoded_forms(self):
        raw = ACCOUNT.encode()
        cases = [
            (hexs(ACCOUNT), "十六进制字节串"),
            ("0x" + ", 0x".join("%02x" % b for b in raw), "十六进制字节串"),
            ("".join("\\x%02X" % b for b in raw), "十六进制字节串"),
            (ACCOUNT.encode("utf-16-le").decode("latin-1"), "UTF-16 宽字符(原样)"),
            ("{" + ", ".join(str(b) for b in raw) + "}", "十进制字节列表"),
            (hashlib.md5(PASSWORD2.encode()).hexdigest().upper(), "摘要 MD5"),
            (hashlib.sha256(PASSWORD.encode()).hexdigest(), "摘要 SHA256"),
            ("aa" + base64.b64encode(b"zz" + PASSWORD.encode() + b"yy").decode() + "bb", "Base64"),
            ("0x" + bytes(int(x) for x in IP.split(".")).hex(), "IP 数值 4 字节(大端"),
            (str(int.from_bytes(bytes(int(x) for x in IP.split(".")), "big")), "IP 32 位整数(大端)"),
            (bytes.fromhex(KEYHEX)[::-1].hex(), "十六进制片段"),
            (bytes.fromhex(KEYHEX)[1:7].hex().upper(), "十六进制片段"),
            (str(int.from_bytes(bytes.fromhex(KEYHEX), "little")), "64 位整数(小端)"),
        ]
        for text, label in cases:
            hit = self.m.find(text)
            self.assertIsNotNone(hit, "没认出:%s / %r" % (label, text[:30]))
            self.assertTrue(hit[1].startswith(label), "写法名不对:%s != %s" % (hit[1], label))

    def test_no_false_positive_on_neutral_data(self):
        neutral = [
            "The quick brown fox jumps over the lazy dog 0123456789\n",
            "deadbeef cafebabe 00112233445566778899aabbccddeeff\n" * 50,
            " ".join(str(i) for i in range(300)),
            "\n".join("%d,%d,%d,%d" % (i, i * 3, i * 7, i * 11) for i in range(500)),
            "f0 e1 d2 c3 b4 a5 96 87 78 69 5a 4b 3c 2d 1e 0f\n" * 20,
            "10.0.0.1 192.168.1.1 127.0.0.1 255.255.255.0\n",
        ]
        for text in neutral:
            self.assertIsNone(self.m.find(text), "误报:%r" % text[:40])

    def test_hit_never_carries_the_value(self):
        for name, value in SYN:
            for text in (value, hexs(value), value.encode("utf-16-le").decode("latin-1")):
                hit = self.m.find("pre " + text + " post")
                if hit is None:
                    continue
                self.assertNotIn(value, repr(hit[:2]))

    def test_scrub_replaces_only_plain_forms_in_file_order(self):
        plain_only = secretscan.Matchers(SYN, encoded=False)
        self.assertEqual(plain_only.replace_plain("a %s b %s c" % (ACCOUNT, PASSWORD), "example"), "a example b example c")
        self.assertEqual(plain_only.replace_plain("x" + ACCOUNT + "y", "example"), "x" + ACCOUNT + "y")   # 短取值按独立单词
        self.assertEqual(plain_only.replace_plain(hexs(ACCOUNT), "example"), hexs(ACCOUNT))              # 编码写法不归脱敏管

    def test_secrets_file_lookup_order(self):
        with tempfile.TemporaryDirectory() as d:
            main = Repo(os.path.join(d, "main"))
            main.commit("c1", {"a.txt": "a\n"})
            with open(os.path.join(main.path, "local.secrets.env"), "w") as f:
                f.write(SYN_FILE_TEXT)
            wt = os.path.join(d, "wt")
            main.git("worktree", "add", "-q", "-b", "side", wt)
            code = "import secretscan; print(secretscan.find_secrets_file())"
            env = {"PYTHONPATH": HERE}
            p = tool("-c", code, cwd=wt, env=env)
            self.assertEqual(os.path.realpath(p.stdout.decode().strip()), os.path.realpath(os.path.join(main.path, "local.secrets.env")),
                             "linked worktree 里应当找到主工作区的取值文件")
            other = os.path.join(d, "other.env")
            with open(other, "w") as f:
                f.write(SYN_FILE_TEXT)
            env["DNF_SECRETS_FILE"] = other
            self.assertEqual(tool("-c", code, cwd=wt, env=env).stdout.decode().strip(), other, "环境变量优先")
            env["DNF_SECRETS_FILE"] = os.path.join(d, "missing.env")
            self.assertEqual(tool("-c", code, cwd=wt, env=env).stdout.decode().strip(), "None", "环境变量指向不存在的文件时不再回退")


class PrecommitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.secrets = os.path.join(cls.tmp, "syn.env")
        with open(cls.secrets, "w", encoding="utf-8") as f:
            f.write(SYN_FILE_TEXT)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def stage(self, files, secrets=True):
        repo = Repo(tempfile.mkdtemp(dir=self.tmp))
        for rel, data in files.items():
            repo.put(rel, data)
        repo.git("add", "-A", "-f")
        env = {"DNF_SECRETS_FILE": self.secrets if secrets else os.path.join(self.tmp, "nope.env")}
        return tool(os.path.join(HERE, "precommit.py"), cwd=repo.path, env=env)

    def rejected(self, files, needle):
        p = self.stage(files)
        self.assertEqual(p.returncode, 1, p.stderr.decode())
        self.assertIn(needle, p.stderr.decode())
        return p.stderr.decode()

    def test_clean_commit_passes(self):
        p = self.stage({"src/a.cs": "class A {}\n", "docs/x.md": "说明\n"})
        self.assertEqual(p.returncode, 0, p.stderr.decode())

    def test_forbidden_path_ext_size_magic_decompiler(self):
        self.rejected({"out/x.txt": "x\n"}, "路径属于逆向产物")
        self.rejected({"data/a.pvf": "x\n"}, "扩展名属于原版文件")
        self.rejected({"data/big.txt": "x" * ((1 << 20) + 1)}, "超过 1 MiB")
        self.rejected({"data/p.bin": b"NeoplePack_Bill" + b"\0" * 20}, "魔数")
        self.rejected({"src/a.txt": decompiler_text()}, "反编译器输出")

    def test_secret_plain_and_encoded(self):
        err = self.rejected({"docs/a.md": "账号 %s\n" % ACCOUNT}, "SYN_ACCOUNT")
        self.assertNotIn(ACCOUNT, err.replace("SYN_ACCOUNT", ""))
        err = self.rejected({"src/v.txt": "vector: " + hexs(ACCOUNT, upper=True) + "\n"}, "十六进制字节串")
        self.assertNotIn(hexs(ACCOUNT, upper=True), err)
        self.rejected({"src/k.cs": "new byte[] { %s };\n" % ", ".join("0x%02x" % b for b in bytes.fromhex(KEYHEX)[:6])}, "SYN_KEY_HEX")

    def test_missing_secrets_file_only_warns(self):
        p = self.stage({"src/a.cs": "class A {}\n"}, secrets=False)
        self.assertEqual(p.returncode, 0)
        self.assertIn("提示", p.stderr.decode())


def build_source(base, name="src"):
    """主线 + 好分支 + 各种坏分支 + 标签。main 的最早提交里 tool.sh 带着(合成的)账号名,之后被改成 example。"""
    r = Repo(os.path.join(base, name))
    r.commit("c1", {"README.md": "hi\n", "tools/baseline/tool.sh": leaked_tool_sh()})
    r.commit("c2", {"tools/baseline/tool.sh": CLEAN_TOOL_SH})
    r.commit("c3", {"src/a.txt": "a\n"})
    r.branch("good", "good work", {"src/good.txt": "g\n"})
    r.branch("merged", "merged work", {"src/m.txt": "m\n"})
    r.git("merge", "-q", "--no-ff", "-m", "merge merged", "merged")
    r.git("tag", "v1")
    r.branch("bad-forbidden", "bad", {"out/x.txt": "x\n"})
    r.branch("bad-hex", "bad", {"src/vec.txt": "v " + hexs(ACCOUNT) + "\n"})
    return r


def refs_of(r):
    return [x for x in r.git("for-each-ref", "--format=%(refname)", "refs/heads", "refs/tags").split("\n") if x]


class AuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.m = secretscan.Matchers(SYN)
        cls.r = build_source(cls.tmp)
        r = cls.r
        r.branch("bad-msg", "提交信息里写了 %s 这个名字" % USER, {"src/b.txt": "b\n"})
        r.branch("bad-path", "bad", {"docs/p.md": local_path_text()})
        r.branch("bad-token", "bad", {"docs/t.md": token_text()})
        r.branch("bad-key", "bad", {"docs/k.md": private_key_text()})
        r.branch("bad-magic", "bad", {"data/blob.bin": b"Neople Img File" + b"\0" * 40})
        r.branch("bad-big", "bad", {"data/big.txt": "x" * ((1 << 20) + 1)})
        r.branch("bad-decompiler", "bad", {"docs/d.md": decompiler_text()})
        r.branch("bad-wide", "bad", {"data/w.bin": b"\x00\x01" + ACCOUNT.encode("utf-16-le") + b"\x02"})
        r.branch("bad-twice", "bad", {"docs/copy.sh": leaked_tool_sh()})            # 同一份内容也出现在不允许的路径
        r.branch("bad-hex-toolsh", "bad", {"tools/baseline/tool.sh": "x " + hexs(ACCOUNT) + "\n"})   # 允许路径里的编码写法
        r.branch("bad-gone", "bad", {"docs/gone.md": "账号 %s\n" % PASSWORD})
        r.git("checkout", "-q", "bad-gone")
        r.commit("删掉它", remove=["docs/gone.md"])                                  # 后来删了,历史里还在
        r.git("checkout", "-q", "main")
        r.git("branch", "acct-%s" % ACCOUNT, "good")                                 # 引用名里含取值
        cls.refs = refs_of(r)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def audit(self, refs=None, allow=("tools/baseline/tool.sh",)):
        return audit_refs.audit(self.r.path, refs or self.refs, self.m, allow)

    def kinds(self, report, ref):
        return {report.findings[i - 1].kind for i in report.verdicts[ref][1] if not report.findings[i - 1].allowed}

    def test_good_refs_are_scrubbable_not_failed(self):
        rep = self.audit()
        for ref in ("refs/heads/main", "refs/heads/good", "refs/heads/merged", "refs/tags/v1"):
            self.assertEqual(rep.status(ref), audit_refs.SCRUB, ref)
        self.assertEqual(rep.exit_code(), 1)   # 总体有 FAIL

    def test_each_bad_branch_fails_for_its_own_reason(self):
        rep = self.audit()
        expect = {
            "bad-forbidden": "路径", "bad-hex": "凭据", "bad-msg": "提交信息或作者字段·凭据", "bad-path": "本机路径",
            "bad-token": "私钥或令牌", "bad-key": "私钥或令牌", "bad-magic": "容器魔数", "bad-big": "体积",
            "bad-decompiler": "反编译器输出", "bad-wide": "凭据", "bad-twice": "凭据", "bad-hex-toolsh": "凭据",
            "bad-gone": "凭据", "acct-" + ACCOUNT: "引用名",
        }
        for name, kind in expect.items():
            ref = "refs/heads/" + name
            self.assertEqual(rep.status(ref), audit_refs.FAIL, name)
            self.assertIn(kind, self.kinds(rep, ref), "%s 的问题种类 %s" % (name, self.kinds(rep, ref)))

    def test_bad_branches_do_not_taint_siblings(self):
        rep = self.audit()
        # good 没有任何不允许的问题;acct-<取值> 指向同一个提交,只有引用名这一条
        self.assertEqual(self.kinds(rep, "refs/heads/good"), set())
        self.assertEqual(self.kinds(rep, "refs/heads/acct-" + ACCOUNT), {"引用名"})

    def test_without_allow_list_the_leak_in_early_history_fails_everything(self):
        rep = self.audit(["refs/heads/main", "refs/heads/good"], allow=())
        self.assertEqual({rep.status("refs/heads/main"), rep.status("refs/heads/good")}, {audit_refs.FAIL})

    def test_report_never_prints_values(self):
        rep = self.audit()
        text = "\n".join(rep.describe(f) for f in rep.findings)
        buf = []
        rep.print(buf.append, list_refs=True)
        text += "\n".join(buf)
        for name, value in SYN:
            self.assertNotIn(value, text, name)

    def test_object_hash_verification_detects_bad_reads(self):
        real_get = audit_refs.ObjectReader.get
        state = {"n": 0}

        def bad_get(reader, sha):
            typ, body = real_get(reader, sha)
            if typ == "blob":
                state["n"] += 1
                if state["n"] == 1:
                    body = body + b"x"   # 模拟读到了被改动的字节
            return typ, body

        audit_refs.ObjectReader.get = bad_get
        try:
            with self.assertRaises(audit_refs.AuditError):
                audit_refs.audit(self.r.path, ["refs/heads/main"], self.m, ())
        finally:
            audit_refs.ObjectReader.get = real_get

    def test_refuses_to_run_without_secrets(self):
        with self.assertRaises(audit_refs.AuditError):
            audit_refs.audit(self.r.path, ["refs/heads/main"], secretscan.Matchers([]), ())

    def test_clean_repo_passes_outright(self):
        with tempfile.TemporaryDirectory() as d:
            r = Repo(os.path.join(d, "c"))
            r.commit("c1", {"a.txt": "a\n"})
            rep = audit_refs.audit(r.path, ["refs/heads/main"], self.m, ())
            self.assertEqual((rep.status("refs/heads/main"), rep.exit_code()), (audit_refs.PASS, 0))

    def test_cli_exit_codes_and_scrub(self):
        env = {"DNF_SECRETS_FILE": self.secrets_file()}
        p = tool(os.path.join(HERE, "audit_refs.py"), "audit", "--repo", self.r.path, "--allow-secret-path", "tools/baseline/tool.sh",
                 "refs/heads/main", "refs/heads/good", env=env)
        self.assertEqual(p.returncode, 10, p.stdout.decode() + p.stderr.decode())
        p = tool(os.path.join(HERE, "audit_refs.py"), "audit", "--repo", self.r.path, "refs/heads/bad-forbidden", env=env)
        self.assertEqual(p.returncode, 1)
        scrubbed = tool(os.path.join(HERE, "audit_refs.py"), "scrub", env=env, input=("a %s b %s c\n" % (ACCOUNT, PASSWORD)).encode())
        self.assertEqual(scrubbed.stdout.decode(), "a example b example c\n")
        binary = b"\xff\xfe" + ACCOUNT.encode() + b"\x00"
        self.assertEqual(tool(os.path.join(HERE, "audit_refs.py"), "scrub", env=env, input=binary).stdout, binary)

    def secrets_file(self):
        path = os.path.join(self.tmp, "syn.env")
        with open(path, "w", encoding="utf-8") as f:
            f.write(SYN_FILE_TEXT)
        return path


class PublishTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.secrets = os.path.join(self.tmp, "syn.env")
        with open(self.secrets, "w", encoding="utf-8") as f:
            f.write(SYN_FILE_TEXT)
        self.src = build_source(self.tmp)
        self.remote = Repo(os.path.join(self.tmp, "remote.git"), bare=True)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def publish(self, *args):
        p = tool(os.path.join(HERE, "publish.py"), "--url", self.remote.path, *args, cwd=self.src.path,
                 env={"DNF_SECRETS_FILE": self.secrets})
        return p.returncode, p.stdout.decode() + p.stderr.decode()

    def remote_refs(self):
        text = self.remote.git("for-each-ref", "--format=%(refname) %(objectname)")
        return dict(line.split(" ") for line in text.split("\n") if line)

    def assert_plan(self, out, action, ref):
        """发布计划里确实有这一行(汇总行里的动作名不算)。"""
        self.assertRegex(out, r"(?m)^\s+%s\s+%s\s" % (action, ref))

    def test_publishes_good_refs_scrubbed_and_skips_bad_ones(self):
        refs_snapshot = self.src.git("for-each-ref", "--format=%(refname) %(objectname)")
        rc, out = self.publish("--twice")
        self.assertEqual(rc, 0, out)
        got = self.remote_refs()
        self.assertEqual(sorted(got), ["refs/heads/good", "refs/heads/main", "refs/heads/merged", "refs/tags/v1"], out)
        self.assertIn("没通过审计", out)
        # 远端历史里没有任何取值(含全部提交的全部内容与信息)
        dump = self.remote.git("log", "--all", "-p", "--format=%H%n%B")
        for name, value in SYN:
            self.assertNotIn(value, dump, name)
        # 最新树与本地一致;提交数一致;新的审计全部 PASS
        for ref in got:
            self.assertEqual(self.remote.git("rev-parse", ref + "^{tree}"), self.src.git("rev-parse", ref + "^{tree}"), ref)
            self.assertEqual(self.remote.git("rev-list", "--count", ref), self.src.git("rev-list", "--count", ref), ref)
        rep = audit_refs.audit(self.remote.path, sorted(got), secretscan.Matchers(SYN), ())
        self.assertEqual({rep.status(r) for r in got}, {audit_refs.PASS})
        # 本地没有任何改动
        self.assertEqual(refs_snapshot, self.src.git("for-each-ref", "--format=%(refname) %(objectname)"))
        self.assertEqual(self.src.git("status", "--short"), "")

    def test_second_run_is_a_noop_and_new_commits_fast_forward(self):
        self.assertEqual(self.publish()[0], 0)
        before = self.remote_refs()
        rc, out = self.publish()
        self.assertEqual(rc, 0, out)
        self.assertIn("无需推送", out)
        self.assertEqual(self.remote_refs(), before)
        self.src.commit("c4 more work", {"src/b.txt": "b\n"})
        self.src.git("checkout", "-q", "good")
        self.src.commit("good more", {"src/good2.txt": "g\n"})
        self.src.git("checkout", "-q", "main")
        rc, out = self.publish()
        self.assertEqual(rc, 0, out)
        after = self.remote_refs()
        self.assert_plan(out, "快进", "refs/heads/main")
        self.assert_plan(out, "快进", "refs/heads/good")
        for ref in ("refs/heads/main", "refs/heads/good"):
            self.assertNotEqual(after[ref], before[ref])
            self.remote.git("merge-base", "--is-ancestor", before[ref], after[ref])   # 旧的是新的祖先:确实是快进
        self.assertEqual(after["refs/heads/merged"], before["refs/heads/merged"])

    def test_rewritten_branch_is_skipped_unless_forced(self):
        self.assertEqual(self.publish()[0], 0)
        before = self.remote_refs()
        self.src.git("checkout", "-q", "good")
        self.src.git("commit", "-q", "--amend", "-m", "good work (amended)", env={"GIT_COMMITTER_DATE": "2021-01-01T00:00:00 +0000"})
        self.src.git("checkout", "-q", "main")
        rc, out = self.publish()
        self.assertEqual(rc, 0, out)
        self.assert_plan(out, "跳过", "refs/heads/good")
        self.assertIn("远端已有被改写过的版本", out)
        self.assertEqual(self.remote_refs(), before)
        rc, out = self.publish("--force-branches")
        self.assertEqual(rc, 0, out)
        self.assert_plan(out, "覆盖", "refs/heads/good")
        after = self.remote_refs()
        self.assertNotEqual(after["refs/heads/good"], before["refs/heads/good"])
        self.assertEqual(after["refs/heads/main"], before["refs/heads/main"])
        self.assertEqual(self.remote.git("log", "-1", "--format=%s", "refs/heads/good"), "good work (amended)")

    def test_dry_run_changes_nothing(self):
        rc, out = self.publish("--dry-run")
        self.assertEqual(rc, 0, out)
        self.assertIn("没有推送任何东西", out)
        self.assertEqual(self.remote_refs(), {})

    def test_failing_main_aborts_before_anything_is_pushed(self):
        self.src.commit("oops", {"out/x.txt": "x\n"})
        rc, out = self.publish()
        self.assertEqual(rc, 1, out)
        self.assertIn("main 没有通过审计", out)
        self.assertEqual(self.remote_refs(), {})

    def test_diverged_main_is_never_forced(self):
        self.assertEqual(self.publish()[0], 0)
        other = Repo(os.path.join(self.tmp, "other"))
        other.commit("unrelated", {"z.txt": "z\n"})
        other.git("push", "-q", "--force", self.remote.path, "main:refs/heads/main")
        before = self.remote_refs()
        rc, out = self.publish("--force-branches")
        self.assertEqual(rc, 1, out)
        self.assertIn("不是本次结果的祖先", out)
        self.assertEqual(self.remote_refs(), before)

    def test_scrub_that_cannot_scrub_is_caught_by_reaudit(self):
        # 非 UTF-8 的旧版本:审计按 latin-1 读,认得出取值;脱敏只处理 UTF-8 文本,原样放过。必须由脱敏后的复审拦下。
        r = Repo(os.path.join(self.tmp, "binsrc"))
        r.commit("c1", {"tools/baseline/tool.sh": b"\xff\xfe tool --keys '" + ACCOUNT.encode() + b"\t'\n"})
        r.commit("c2", {"tools/baseline/tool.sh": CLEAN_TOOL_SH})
        p = tool(os.path.join(HERE, "publish.py"), "--url", self.remote.path, cwd=r.path, env={"DNF_SECRETS_FILE": self.secrets})
        self.assertEqual(p.returncode, 1, p.stdout.decode() + p.stderr.decode())
        self.assertIn("脱敏后仍有", p.stderr.decode())
        self.assertEqual(self.remote_refs(), {})

    def test_moved_tag_is_not_touched(self):
        self.assertEqual(self.publish()[0], 0)
        before = self.remote_refs()
        self.src.commit("c4", {"src/b.txt": "b\n"})
        self.src.git("tag", "-f", "v1")   # 本地标签挪到新提交上
        rc, out = self.publish()
        self.assertEqual(rc, 0, out)
        self.assert_plan(out, "跳过", "refs/tags/v1")
        self.assertIn("标签已存在且指向别的提交", out)
        after = self.remote_refs()
        self.assertEqual(after["refs/tags/v1"], before["refs/tags/v1"])
        self.assertNotEqual(after["refs/heads/main"], before["refs/heads/main"])   # 其余照常快进

    def test_refuses_without_secrets_file(self):
        p = tool(os.path.join(HERE, "publish.py"), "--url", self.remote.path, cwd=self.src.path,
                 env={"DNF_SECRETS_FILE": os.path.join(self.tmp, "missing.env")})
        self.assertEqual(p.returncode, 1)
        self.assertIn("找不到 local.secrets.env", p.stderr.decode())
        self.assertEqual(self.remote_refs(), {})


if __name__ == "__main__":
    unittest.main()
