#!/usr/bin/env python3
"""基线录制运行器:按场景脚本发固定的输入、定时截图,并把每张截图的时间戳记在 index.tsv 里。

它只是 tools/baseline/tool.sh(DnfBaselineTool 小工具)的一层编排,不碰别的程序:
  - 每一步都经 tool.sh,小工具自己只认窗口所有者名含 wine / dnf / crossover 的进程;
  - 键盘只允许:方向键、x(普攻)、c(跳跃)、esc,以及 <wait:毫秒>、<hold:方向键:毫秒>;其余一律拒绝
    (不允许技能键、回车、数字、字母输入——没有购买、丢弃、交易、聊天的可能);
  - 鼠标只允许点游戏窗口内的坐标(窗口点数,左上角为原点);
  - `front` 会把游戏窗口切到前台,必须加 --allow-front(只在你离开电脑时用);
  - 屏幕锁定时停手(session-check.sh),截图与输入都没有意义。

场景脚本是文本文件,一行一步,# 开头是注释:
  check                      检查屏幕没锁(锁了就退出,退出码 3)
  front                      把游戏窗口切到前台(需要 --allow-front)
  wait 毫秒                   等待
  target 所有者 [标题]         之后的截图、按键、点击都针对这个窗口(默认 DNF.exe / DNF Taiwan;启动器是 dnf-launcher.exe / DNF Launcher)
  shot 名字                   截一张目标窗口,存成 <运行目录>/shots/<名字>.png
  tryshot 名字                同 shot,但窗口还不存在时只记一行 missing,不算失败(用在启动过程里)
  key 脚本                    发键盘(见上面的限制),等它发完
  bgkey 脚本                  同 key,但放到后台线程里发,不等
  join                       等所有 bgkey 发完
  click X Y [double]          在游戏窗口内的 (X, Y) 点一下(或双击)
  note 文字                   往 index.tsv 记一行备注(例如"这里站在传送门前")

截图里有用户的角色名、聊天栏和服务器内容:只留在本机的 re/baseline/ 下,不入库(提交守卫会拦)。
时间精度:每次调用 tool.sh 要经 `open` 启动一次小工具,约 0.5 到 1 秒,所以连拍间隔做不到 100 毫秒;
要对 .ani 的帧延时,需要把"定时输入 + 窗口连拍"放进同一个进程的运行器(用稳定签名,避免重建后重新授权)。

用法:python3 tools/baseline/record.py --run re/baseline/runs/<目录> [--allow-front] 场景脚本...
      python3 tools/baseline/record.py --run <目录> --do "key <hold:right:500>" --do "shot a"
"""
import argparse
import os
import re
import subprocess
import sys
import threading
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TOOL = os.path.join(ROOT, "tools", "baseline", "tool.sh")
SESSION = os.path.join(ROOT, "tools", "baseline", "session-check.sh")

OWNER = "DNF.exe"
TITLE = "DNF Taiwan"
WINDOW_W = 800
WINDOW_H = 600

PLAIN_KEYS = {"x", "c"}
NAMED_KEYS = {"left", "right", "up", "down", "esc"}
HOLD_KEYS = {"left", "right", "up", "down"}
MAX_HOLD_MS = 6000
MAX_WAIT_MS = 60000


class SceneError(Exception):
    pass


def validate_keys(script):
    """检查键盘脚本只含允许的按键;不合法抛 SceneError。"""
    i = 0
    n = len(script)
    while i < n:
        ch = script[i]
        if ch == "<":
            close = script.find(">", i)
            if close < 0:
                raise SceneError("键盘脚本里的 < 没有配对的 >: %r" % script)
            token = script[i + 1:close]
            parts = token.split(":")
            name = parts[0].lower()
            if name == "wait":
                if len(parts) != 2 or not parts[1].isdigit() or int(parts[1]) > MAX_WAIT_MS:
                    raise SceneError("<wait:毫秒> 的毫秒要是不超过 %d 的整数: <%s>" % (MAX_WAIT_MS, token))
            elif name == "hold":
                if (len(parts) != 3 or parts[1].lower() not in HOLD_KEYS or not parts[2].isdigit()
                        or int(parts[2]) > MAX_HOLD_MS):
                    raise SceneError("<hold:方向键:毫秒> 只允许方向键且不超过 %d 毫秒: <%s>" % (MAX_HOLD_MS, token))
            elif name in NAMED_KEYS and len(parts) == 1:
                pass
            else:
                raise SceneError("不允许的按键 <%s>(只允许 %s、wait、hold)" % (token, "、".join(sorted(NAMED_KEYS))))
            i = close + 1
        else:
            if ch.lower() not in PLAIN_KEYS or ch != ch.lower():
                raise SceneError("不允许的字符 %r(只允许 %s)" % (ch, "、".join(sorted(PLAIN_KEYS))))
            i += 1


def validate_click(x, y):
    if not (0 <= x < WINDOW_W and 0 <= y < WINDOW_H):
        raise SceneError("点击坐标 (%s, %s) 不在游戏窗口 %dx%d 之内" % (x, y, WINDOW_W, WINDOW_H))


class Recorder:
    def __init__(self, run_dir, allow_front):
        self.run_dir = os.path.abspath(run_dir)
        self.shots = os.path.join(self.run_dir, "shots")
        os.makedirs(self.shots, exist_ok=True)
        self.allow_front = allow_front
        self.owner = OWNER
        self.title = TITLE
        self.index_path = os.path.join(self.run_dir, "index.tsv")
        self.index_lock = threading.Lock()
        self.threads = []
        self.errors = []
        if not os.path.exists(self.index_path):
            with open(self.index_path, "w", encoding="utf-8") as f:
                f.write("epoch_start\tepoch_end\tkind\tname\tdetail\n")

    def log(self, t0, t1, kind, name, detail=""):
        with self.index_lock:
            with open(self.index_path, "a", encoding="utf-8") as f:
                f.write("%.3f\t%.3f\t%s\t%s\t%s\n" % (t0, t1, kind, name, detail))

    def tool(self, args, timeout=90):
        proc = subprocess.run([TOOL] + args, capture_output=True, text=True, timeout=timeout)
        return proc.returncode, (proc.stdout + proc.stderr).strip()

    def title_args(self):
        return ["--title", self.title] if self.title else []

    def session_ok(self):
        proc = subprocess.run([SESSION], capture_output=True, text=True)
        return proc.returncode == 0, proc.stdout.strip()

    # ---------------------------------------------------------------- 步骤
    def step_check(self):
        t0 = time.time()
        ok, text = self.session_ok()
        self.log(t0, time.time(), "check", "session", text)
        if not ok:
            raise SystemExit("屏幕已锁定,停手:%s" % text)

    def step_front(self):
        if not self.allow_front:
            raise SceneError("front 会抢前台窗口,需要 --allow-front")
        t0 = time.time()
        rc, out = self.tool(["front", "--owner", self.owner])
        self.log(t0, time.time(), "front", "", out.replace("\n", " "))
        if rc != 0:
            raise SceneError("front 失败: " + out)

    def step_wait(self, ms):
        if ms > MAX_WAIT_MS:
            raise SceneError("等待太久: %d" % ms)
        t0 = time.time()
        time.sleep(ms / 1000.0)
        self.log(t0, time.time(), "wait", str(ms))

    def step_shot(self, name, optional=False):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", name):
            raise SceneError("截图名只能含字母、数字、下划线、点、减号: %r" % name)
        path = os.path.join(self.shots, name + ".png")
        t0 = time.time()
        rc, out = self.tool(["shot", "--owner", self.owner] + self.title_args() + ["--out", path])
        t1 = time.time()
        if rc != 0 and optional:
            self.log(t0, t1, "shot-missing", name, out.replace("\n", " "))
            return
        self.log(t0, t1, "shot", name, out.replace("\n", " "))
        if rc != 0:
            raise SceneError("shot 失败: " + out)

    def step_target(self, owner, title):
        if not re.search(r"wine|dnf|crossover", owner, re.I):
            raise SceneError("目标窗口所有者必须含 wine / dnf / crossover: %r" % owner)
        self.owner = owner
        self.title = title
        self.log(time.time(), time.time(), "target", owner, title)

    def _key(self, script, kind):
        validate_keys(script)
        t0 = time.time()
        rc, out = self.tool(["key", "--owner", self.owner, "--keys", script])
        self.log(t0, time.time(), kind, script, out.replace("\n", " "))
        if rc != 0:
            self.errors.append("key 失败: " + out)

    def step_key(self, script):
        validate_keys(script)
        self._key(script, "key")

    def step_bgkey(self, script):
        validate_keys(script)
        th = threading.Thread(target=self._key, args=(script, "bgkey"), daemon=True)
        th.start()
        self.threads.append(th)

    def step_join(self):
        for th in self.threads:
            th.join()
        self.threads = []
        if self.errors:
            raise SceneError("; ".join(self.errors))

    def step_click(self, x, y, double):
        validate_click(x, y)
        args = ["click", "--owner", self.owner] + self.title_args() + ["--x", str(x), "--y", str(y)]
        if double:
            args.append("--double")
        t0 = time.time()
        rc, out = self.tool(args)
        self.log(t0, time.time(), "click", "%d,%d%s" % (x, y, " double" if double else ""), out.replace("\n", " "))
        if rc != 0:
            raise SceneError("click 失败: " + out)

    def step_note(self, text):
        t = time.time()
        self.log(t, t, "note", "", text)

    # ---------------------------------------------------------------- 解释
    def run_line(self, line):
        line = line.strip()
        if not line or line.startswith("#"):
            return
        cmd, _, rest = line.partition(" ")
        rest = rest.strip()
        if cmd == "check":
            self.step_check()
        elif cmd == "front":
            self.step_front()
        elif cmd == "wait":
            self.step_wait(int(rest))
        elif cmd == "shot":
            self.step_shot(rest)
        elif cmd == "tryshot":
            self.step_shot(rest, optional=True)
        elif cmd == "target":
            parts = rest.split(None, 1)
            if not parts:
                raise SceneError("target 所有者 [标题]")
            self.step_target(parts[0], parts[1] if len(parts) > 1 else "")
        elif cmd == "key":
            self.step_key(rest)
        elif cmd == "bgkey":
            self.step_bgkey(rest)
        elif cmd == "join":
            self.step_join()
        elif cmd == "click":
            parts = rest.split()
            if len(parts) not in (2, 3) or (len(parts) == 3 and parts[2] != "double"):
                raise SceneError("click X Y [double]: %r" % rest)
            self.step_click(int(parts[0]), int(parts[1]), len(parts) == 3)
        elif cmd == "note":
            self.step_note(rest)
        else:
            raise SceneError("未知步骤: %r" % line)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="运行目录(截图放在它的 shots/ 下,时间戳记在 index.tsv)")
    ap.add_argument("--allow-front", action="store_true", help="允许把游戏窗口切到前台(只在你离开电脑时)")
    ap.add_argument("--do", action="append", default=[], help="直接执行一步(可重复),格式同场景脚本的一行")
    ap.add_argument("scenes", nargs="*", help="场景脚本文件")
    args = ap.parse_args()
    if not args.do and not args.scenes:
        ap.error("需要场景脚本或 --do")
    rec = Recorder(args.run, args.allow_front)
    try:
        for line in args.do:
            rec.run_line(line)
        for path in args.scenes:
            with open(path, encoding="utf-8") as f:
                for n, line in enumerate(f, 1):
                    try:
                        rec.run_line(line)
                    except SceneError as e:
                        raise SystemExit("%s:%d: %s" % (path, n, e))
        rec.step_join()
    except SceneError as e:
        rec.step_join() if rec.threads else None
        raise SystemExit(str(e))
    print("完成;记录见 " + rec.index_path)


if __name__ == "__main__":
    main()
