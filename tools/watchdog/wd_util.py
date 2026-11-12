#!/usr/bin/env python3
"""看门狗的通用小工具。只用标准库,Python 3.9 起可用,仅面向 macOS。

  awake_clock   醒着的时间。机器休眠时它不走。所有"观察了多久""闲置了多久"都用它,绝不用挂钟或 ps 的 etime:
                实测本机开机 16.9 小时、真正醒着只有 2.2 小时,挂钟会把睡觉算成"闲置",进而误杀。
  run           带超时、从不抛异常的子进程调用(固定 LC_ALL=C,git 只读调用不抢 index.lock)。
  Log           带大小轮转的日志。
  redact        命令行脱敏(口令、令牌)。环境变量的取值从不读出来记录,只判断"有没有某个变量"。
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import time
from datetime import datetime
from typing import Any, Optional, Sequence, Tuple


# ---------------------------------------------------------------- 时间

def awake_clock() -> float:
    """醒着的秒数(休眠不计)。macOS 上是 CLOCK_UPTIME_RAW;其它平台退回 monotonic。"""
    clk = getattr(time, "CLOCK_UPTIME_RAW", None)
    if clk is not None:
        return time.clock_gettime(clk)
    return time.monotonic()


def boot_time() -> int:
    """开机时刻(epoch 秒);拿不到返回 0。用来判断状态文件是不是上一次开机留下的。"""
    rc, out, _ = run(["sysctl", "-n", "kern.boottime"], timeout=5)
    m = re.search(r"sec\s*=\s*(\d+)", out or "")
    return int(m.group(1)) if m else 0


def sleep_total(boot: int, now_wall: float, now_up: float) -> float:
    """开机以来累计睡了多久(秒)= 挂钟时间 - 醒着的时间。拿不到开机时间时返回 0(偏保守:年龄估计会偏大一点点)。"""
    if not boot:
        return 0.0
    return max(0.0, (now_wall - boot) - now_up)


def parse_clock(text: str) -> float:
    """解析 ps 的 etime / time 字段:[[dd-]hh:]mm:ss[.cc],或 mmm:ss.cc。返回秒。"""
    text = (text or "").strip()
    if not text:
        return 0.0
    days = 0
    if "-" in text:
        d, text = text.split("-", 1)
        days = int(d)
    total = 0.0
    for part in text.split(":"):
        total = total * 60 + float(part)
    return days * 86400 + total


def fmt_dur(seconds: float) -> str:
    seconds = max(0.0, float(seconds))
    if seconds < 90:
        return "%d 秒" % round(seconds)
    if seconds < 90 * 60:
        return "%d 分钟" % round(seconds / 60)
    if seconds < 48 * 3600:
        return "%.1f 小时" % (seconds / 3600)
    return "%.1f 天" % (seconds / 86400)


def fmt_bytes(n: float) -> str:
    n = float(n)
    for unit in ("B", "KB", "MB", "GB"):
        if abs(n) < 1024 or unit == "GB":
            return ("%d %s" % (n, unit)) if unit == "B" else ("%.1f %s" % (n, unit))
        n /= 1024.0
    return "%.1f GB" % n


# ---------------------------------------------------------------- 子进程

def run(cmd: Sequence[str], timeout: float = 30, cwd: Optional[str] = None,
        env: Optional[dict] = None, input_text: Optional[str] = None) -> Tuple[int, str, str]:
    """跑一个命令,返回 (退出码, stdout, stderr)。超时返回 124,找不到命令 127,其它系统错误 126。从不抛异常。

    固定 LC_ALL=C(ps 的 lstart、git 的提示都是英文,解析才稳定);
    GIT_OPTIONAL_LOCKS=0:只读的 git status 之类不顺手去刷新索引,免得和 agent 抢 index.lock。
    """
    full_env = dict(os.environ)
    full_env.update({"LC_ALL": "C", "GIT_OPTIONAL_LOCKS": "0", "GIT_TERMINAL_PROMPT": "0"})
    if env:
        full_env.update(env)
    try:
        p = subprocess.run(list(cmd), capture_output=True, text=True, errors="replace",
                           timeout=timeout, cwd=cwd, env=full_env, input=input_text,
                           stdin=None if input_text is not None else subprocess.DEVNULL)
        return p.returncode, p.stdout, p.stderr
    except subprocess.TimeoutExpired:
        return 124, "", "timeout after %ss" % timeout
    except FileNotFoundError:
        return 127, "", "command not found: %s" % cmd[0]
    except OSError as e:
        return 126, "", str(e)


# ---------------------------------------------------------------- 脱敏

_SECRET_ARG_RE = re.compile(
    r"(?i)(?<![\w-])(--?(?:password|passwd|pwd|token|secret|api[-_]?key|access[-_]?key|auth)(?:=|\s+))(\S+)")
_ASSIGN_RE = re.compile(r"(?i)\b([A-Z0-9_]*(?:PASSWORD|PASSWD|TOKEN|SECRET|API_?KEY)[A-Z0-9_]*)=(\S+)")
_TOKEN_RE = re.compile(r"\b(?:sk|ghp|gho|ghu|ghs|ghr|github_pat|xox[baprs])[-_][A-Za-z0-9_-]{10,}\b")


def redact(cmd: str, limit: int = 200) -> str:
    """日志里的命令行:抹掉口令/令牌样的取值,压缩空白,截断。"""
    s = _SECRET_ARG_RE.sub(r"\1***", cmd or "")
    s = _ASSIGN_RE.sub(r"\1=***", s)
    s = _TOKEN_RE.sub("***", s)
    s = " ".join(s.split())
    return s if len(s) <= limit else s[: limit - 1] + "…"


# ---------------------------------------------------------------- 文件

def atomic_write_json(path: str, obj: Any) -> None:
    """先写临时文件再 rename,读的人永远看不到写了一半的文件。"""
    d = os.path.dirname(path) or "."
    os.makedirs(d, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".tmp-", suffix=".json", dir=d)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(obj, f, ensure_ascii=False, indent=1, sort_keys=True)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def read_json(path: str, default: Any = None) -> Any:
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def path_within(path: str, root: str) -> bool:
    """path 是 root 本身或在 root 之下(按路径分量判断,/a/bc 不算在 /a/b 之下)。"""
    root = root.rstrip("/") or "/"
    return path == root or path.startswith(root + "/")


# ---------------------------------------------------------------- 日志

class Log:
    """追加写的日志文件,超过 max_bytes 就轮转成 .1 .2 …;写不进去时静默(看门狗不能因为日志挂掉)。"""

    def __init__(self, path: Optional[str] = None, max_bytes: int = 1 << 20, backups: int = 3,
                 echo: bool = False) -> None:
        self.path = path
        self.max_bytes = max_bytes
        self.backups = backups
        self.echo = echo

    def _rotate(self) -> None:
        try:
            if os.path.getsize(self.path) < self.max_bytes:
                return
        except OSError:
            return
        for i in range(self.backups, 0, -1):
            src = self.path if i == 1 else "%s.%d" % (self.path, i - 1)
            dst = "%s.%d" % (self.path, i)
            try:
                if os.path.exists(src):
                    os.replace(src, dst)
            except OSError:
                pass

    def emit(self, level: str, msg: str) -> None:
        line = "%s %-5s %s" % (datetime.now().strftime("%Y-%m-%d %H:%M:%S"), level, msg)
        if self.echo:
            print(line, file=sys.stderr)
        if not self.path:
            return
        try:
            os.makedirs(os.path.dirname(self.path), exist_ok=True)
            self._rotate()
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError:
            pass

    def info(self, msg: str) -> None:
        self.emit("INFO", msg)

    def warn(self, msg: str) -> None:
        self.emit("WARN", msg)

    def error(self, msg: str) -> None:
        self.emit("ERROR", msg)

    def act(self, msg: str) -> None:
        """真正动了手(杀进程、删文件)的记录,排查时 grep ACT 即可。"""
        self.emit("ACT", msg)
