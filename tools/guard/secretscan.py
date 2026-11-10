#!/usr/bin/env python3
"""凭据匹配:提交守卫(precommit.py)与发布前审计(audit_refs.py)共用的一套规则,保证两边口径永远一致。

取值来自 local.secrets.env(账号、口令、服务器地址等;只放本机,绝不提交)。每个取值会被展开成一组正则:

  原样写法
    长度 >= 8 的取值按子串匹配;3 到 7 按"独立单词"匹配(前后不能紧挨字母、数字、下划线),
    既挡住 `--keys '账号<tab>'` 这类写进示例的泄露,又不误伤恰好含有它的别的单词;长度 < 3 的忽略。
  换了写法的同一个东西(只对字节数 >= 4 的取值做;更短的取值编码后太容易撞上无关数据)
    十六进制字节串(大小写不限;字节之间可以隔空白、逗号、冒号、点、下划线、横线、0x、\\x)、
    UTF-16 宽字符(原样与十六进制)、Base64(三种对齐)、十进制字节列表、MD5 / SHA1 / SHA256 摘要;
    取值是 IPv4 时再加 4 字节数值(大小端)与 32 位整数;
    取值是纯十六进制时再加 6 字节窗口的片段、字节反序、按 4 字节字交换、64 位整数。
  测试向量与文档里的十六进制转储是取值最容易被"编码后漏进去"的地方,所以才要认这些写法。

命中结果只携带"变量名 + 形态名",绝不携带取值本身。
"""
import base64
import hashlib
import ipaddress
import os
import re
import subprocess

# 不依赖取值文件、任何环境下都该拦的两类内容(守卫与审计共用):
# 本机绝对路径(用户目录名属于个人信息,不该出现在公开历史里),以及私钥块与常见访问令牌。
LOCAL_PATH_RE = re.compile(r"(?:/Users/[A-Za-z0-9._-]+|/home/[A-Za-z0-9._-]+|[A-Za-z]:\\Users\\[A-Za-z0-9._-]+)")
CREDENTIAL_RE = re.compile(
    r"-----BEGIN [A-Z ]*PRIVATE KEY-----"
    r"|\bgh[pousr]_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{30,}\b"
    r"|\bAKIA[0-9A-Z]{16}\b|\bxox[baprs]-[A-Za-z0-9-]{10,}\b|\bsk-[A-Za-z0-9]{20,}\b"
)

SECRETS_NAME = "local.secrets.env"
MIN_ENCODED_BYTES = 4          # 取值短于这个字节数时只做原样匹配
WINDOW = 6                     # 十六进制取值的片段窗口(字节)
PLAIN = "原样"
# 字节与字节之间允许出现的分隔写法
SEP = r"(?:0x|\\x|\\u00|[\s,:;._-])*"


def _git(*args, cwd=None):
    try:
        out = subprocess.run(["git", *args], cwd=cwd, capture_output=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return ""
    return out.decode("utf-8", "replace").strip()


def candidate_paths():
    """取值文件可能所在的位置,按优先级。

    环境变量 DNF_SECRETS_FILE 优先;否则先看当前工作区根目录,再看主工作区
    (各 worktree 里没有这个被忽略的文件,不往主工作区找的话,它们的提交就没有凭据检查)。
    """
    env = os.environ.get("DNF_SECRETS_FILE")
    if env:
        return [env]
    paths = []
    top = _git("rev-parse", "--show-toplevel")
    if top:
        paths.append(os.path.join(top, SECRETS_NAME))
        common = _git("rev-parse", "--git-common-dir", cwd=top)
        if common:
            main_git = os.path.normpath(os.path.join(top, common))
            paths.append(os.path.join(os.path.dirname(main_git), SECRETS_NAME))
    return paths


def find_secrets_file():
    for p in candidate_paths():
        if os.path.isfile(p):
            return p
    return None


def load_secrets(path=None):
    """读取取值,返回 [(变量名, 取值)];文件不存在返回空列表。长度 < 3 的取值被忽略。"""
    path = path or find_secrets_file()
    pairs = []
    if not path:
        return pairs
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, value = line.split("=", 1)
                key = key.strip()
                if key.startswith("export "):
                    key = key[len("export "):].strip()
                value = value.strip().strip("'\"")
                if len(value) >= 3:
                    pairs.append((key, value))
    except OSError:
        pass
    return pairs


def _hex_pattern(bs):
    return re.compile(SEP.join("%02x" % x for x in bs), re.I)


def _dec_list_pattern(bs):
    return re.compile(r"(?<!\d)" + r"[\s,;]+".join(str(x) for x in bs) + r"(?!\d)")


def _b64_patterns(bs):
    """Base64 在三种字节对齐下的写法(嵌在更长数据里时,开头与结尾的几个字符取决于相邻字节,要去掉)。"""
    out = []
    for pad in (0, 1, 2):
        enc = base64.b64encode(b"\x00" * pad + bs).decode()
        if pad:
            enc = enc[(pad * 4 + 2) // 3:]
        enc = enc.rstrip("=")
        if (pad + len(bs)) % 3:
            enc = enc[:-1]
        if len(enc) >= 6:
            out.append(re.compile(re.escape(enc)))
    return out


class Matchers:
    """一组 (变量名, 形态, 正则, 是否原样) 的集合。encoded=False 时只有原样写法(脱敏替换用)。"""

    def __init__(self, pairs, encoded=True):
        self.pairs = list(pairs)
        self.items = []
        for name, value in self.pairs:
            self._expand(name, value, encoded)

    def __bool__(self):
        return bool(self.items)

    def _add(self, name, label, regex, plain=False):
        self.items.append((name, label, regex, plain))

    def _expand(self, name, value, encoded):
        if len(value) >= 8:
            self._add(name, PLAIN, re.compile(re.escape(value)), True)
        else:
            self._add(name, PLAIN, re.compile(r"(?<![A-Za-z0-9_])" + re.escape(value) + r"(?![A-Za-z0-9_])"), True)
        raw = value.encode("utf-8")
        if not encoded or len(raw) < MIN_ENCODED_BYTES:
            return
        self._add(name, "十六进制字节串", _hex_pattern(raw))
        wide = value.encode("utf-16-le")
        self._add(name, "UTF-16 宽字符(原样)", re.compile(re.escape(wide.decode("latin-1"))))
        self._add(name, "UTF-16 宽字符(十六进制)", _hex_pattern(wide))
        self._add(name, "十进制字节列表", _dec_list_pattern(raw))
        for label, algo in (("MD5", hashlib.md5), ("SHA1", hashlib.sha1), ("SHA256", hashlib.sha256)):
            self._add(name, "摘要 " + label, re.compile(algo(raw).hexdigest(), re.I))
        for p in _b64_patterns(raw):
            self._add(name, "Base64", p)
        try:
            ip = ipaddress.IPv4Address(value)
        except ValueError:
            ip = None
        if ip is not None:
            big, little = ip.packed, ip.packed[::-1]
            self._add(name, "IP 数值 4 字节(大端,十六进制)", _hex_pattern(big))
            self._add(name, "IP 数值 4 字节(小端,十六进制)", _hex_pattern(little))
            self._add(name, "IP 数值 4 字节(十进制列表,大端)", _dec_list_pattern(big))
            self._add(name, "IP 数值 4 字节(十进制列表,小端)", _dec_list_pattern(little))
            self._add(name, "IP 32 位整数(大端)", re.compile(r"(?<!\d)%d(?!\d)" % int(ip)))
            self._add(name, "IP 32 位整数(小端)", re.compile(r"(?<!\d)%d(?!\d)" % int.from_bytes(little, "big")))
        if re.fullmatch(r"(?:[0-9a-fA-F]{2})+", value):
            data = bytes.fromhex(value)
            variants = (
                ("正序", data),
                ("字节反序", data[::-1]),
                ("按 4 字节字交换", b"".join(data[i:i + 4][::-1] for i in range(0, len(data), 4))),
            )
            for vname, vbytes in variants:
                for i in range(0, len(vbytes) - WINDOW + 1):
                    self._add(name, "十六进制片段(%s)" % vname, _hex_pattern(vbytes[i:i + WINDOW]))
            if len(data) <= 8:
                self._add(name, "64 位整数(大端)", re.compile(r"(?<!\d)%d(?!\d)" % int.from_bytes(data, "big")))
                self._add(name, "64 位整数(小端)", re.compile(r"(?<!\d)%d(?!\d)" % int.from_bytes(data, "little")))
            for p in _b64_patterns(data):
                self._add(name, "Base64(按原始字节)", p)

    def find(self, text):
        """第一处命中,返回 (变量名, 形态, match);没有返回 None。"""
        for name, label, regex, _plain in self.items:
            m = regex.search(text)
            if m:
                return name, label, m
        return None

    def hides(self, text):
        """text(例如一个路径)里有没有出现原样的取值;有就不能原样打印出来。"""
        return any(regex.search(text) for _n, _l, regex, plain in self.items if plain)

    def replace_plain(self, text, replacement):
        """把原样出现的取值替换掉(脱敏用)。顺序与取值文件一致,所以结果是确定的。"""
        for _n, _l, regex, plain in self.items:
            if plain:
                text = regex.sub(replacement, text)
        return text

    def selftest(self):
        """正向对照:用与实现无关的写法给每个取值造出各种编码形态,检测必须全部命中。返回失败说明的列表。"""
        failures = []

        def expect(name, prefix, text, why):
            if not any(regex.search(text) for n, label, regex, _p in self.items if n == name and label.startswith(prefix)):
                failures.append("%s / %s / %s" % (name, prefix, why))

        for name, value in self.pairs:
            raw = value.encode("utf-8")
            expect(name, PLAIN, "x " + value + " y" if len(value) >= 8 else " " + value + " ", "原样")
            if len(raw) < MIN_ENCODED_BYTES:
                continue
            for t in (" ".join("%02X" % x for x in raw), "0x" + ", 0x".join("%02x" % x for x in raw),
                      "".join("\\x%02x" % x for x in raw), ":".join("%02x" % x for x in raw), raw.hex(), raw.hex().upper()):
                expect(name, "十六进制字节串", t, "十六进制写法")
            wide = value.encode("utf-16-le")
            expect(name, "UTF-16 宽字符(原样)", wide.decode("latin-1"), "宽字符原样")
            expect(name, "UTF-16 宽字符(十六进制)", wide.hex(), "宽字符十六进制")
            expect(name, "十进制字节列表", "{" + ", ".join(str(x) for x in raw) + "}", "十进制列表")
            expect(name, "摘要 MD5", hashlib.md5(raw).hexdigest().upper(), "MD5")
            expect(name, "摘要 SHA256", hashlib.sha256(raw).hexdigest(), "SHA256")
            if len(raw) >= 5:
                for pre in (b"", b"A", b"AB"):
                    enc = base64.b64encode(pre + raw + b"tail-bytes").decode()
                    expect(name, "Base64", enc, "Base64 对齐 %d" % len(pre))
            try:
                ip = ipaddress.IPv4Address(value)
            except ValueError:
                ip = None
            if ip is not None:
                expect(name, "IP 数值 4 字节(大端", "0x" + ip.packed.hex(), "IP 大端")
                expect(name, "IP 数值 4 字节(小端", ip.packed[::-1].hex(), "IP 小端")
                expect(name, "IP 32 位整数(大端)", str(int(ip)), "IP 整数")
                expect(name, "IP 数值 4 字节(十进制列表,大端)", ", ".join(str(x) for x in ip.packed), "IP 字节十进制")
            if re.fullmatch(r"(?:[0-9a-fA-F]{2})+", value) and len(value) // 2 >= WINDOW:
                data = bytes.fromhex(value)
                for t in (value.upper(), data[::-1].hex(), data[2:2 + WINDOW].hex()):
                    expect(name, "十六进制片段", t, "密钥片段/反序")
                if len(data) <= 8:
                    expect(name, "64 位整数(小端)", str(int.from_bytes(data, "little")), "64 位整数")
        return failures


def load_matchers(path=None, encoded=True):
    return Matchers(load_secrets(path), encoded=encoded)
