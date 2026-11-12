#!/usr/bin/env python3
"""看门狗的配置:默认值写在代码里,用户只需要在一个覆盖文件里写想改的那几项。

覆盖文件:<安装目录>/config.json(默认 ~/Library/Application Support/dnf-unity-watchdog/config.json),
内容是 JSON 对象,与 DEFAULTS 同结构、只写要改的键;字典逐层合并,列表整体替换。
想"追加"而不是"替换"签名与保护名单,用 processes.extra_signatures / processes.extra_protected。

严格校验:覆盖文件解析失败、出现不认识的键(多半是拼错)、数值越界、正则写错,都会让看门狗**降级为只报告**
(不杀进程、不删文件)并在日志里报错——拼错的键如果静默忽略,生效的会是默认值,可能比你本意更激进。
"""
from __future__ import annotations

import copy
import json
import os
import re
from typing import Any, Dict, List, Optional, Tuple

import wd_util

DEFAULT_LABEL = "com.dnfunity.watchdog"
KINDS = ("kill", "report", "ignore")


def label() -> str:
    """launchd 任务的标签。测试用环境变量换成一次性的标签,绝不去碰真正装好的那个任务。"""
    return os.environ.get("DNF_WATCHDOG_LABEL") or DEFAULT_LABEL

# 签名:按顺序匹配进程命令行,第一个命中的生效。
#   args          命令行正则
#   trusted       True = 只要属于本工程就可处理;False = 还要求整棵进程树里有 agent 的出身证明(环境里有 CURSOR_AGENT 等)
#   action        kill 处理 / report 只报告不动手 / ignore 完全不碰
#   max_age_minutes  "跑飞上限":孤儿根进程在醒着的观察时间里累计超过它,即使还在吃 CPU 也清掉(只给跑一次就该结束的任务设)
_SIGNATURES: List[Dict[str, Any]] = [
    # 编译服务器是共享的、自己会在空闲后退出,杀它只会拖慢别人的构建
    {"name": "compiler-server", "args": r"VBCSCompiler|build-server|RazorServer", "action": "ignore"},
    # Wine/CrossOver 里可能是用户自己在玩游戏(同一个瓶子),所以只报告,不动手
    {"name": "wine", "args": r"wineserver|wine-preloader|wine64|winewrapper|/CrossOver\.app/|dnf-launcher|[\\/]DNF\.exe",
     "action": "report"},
    # Unity 只认 -batchmode:用户在图形界面里打开的编辑器和玩的播放器没有这个参数,不会被碰
    {"name": "unity-batchmode", "args": r"\.app/Contents/MacOS/\S+\s(?:.*\s)?-batchmode\b",
     "trusted": True, "action": "kill", "max_age_minutes": 180},
    # dotnet 系:构建、测试宿主、MSBuild 节点、命令行工具、联网探针
    {"name": "dotnet", "args": r"^(?:\S*/)?(?:dotnet|testhost|vstest\.console)(?:\s|$)",
     "trusted": True, "action": "kill", "max_age_minutes": 180},
    # Ghidra 无头分析(图形界面的 GhidraRun 不在此列)。分析可以合法地跑很久,所以不设跑飞上限
    {"name": "ghidra-headless", "args": r"analyzeHeadless|ghidra\.app\.util\.headless",
     "trusted": True, "action": "kill"},
    # 各种脚本与远程工具:光凭路径不够,必须整棵树里有 agent 的出身证明
    {"name": "script-driver",
     "args": r"^(?:\S*/)?(?:[Pp]ython[0-9.]*|bash|sh|zsh|dash|node|ruby|perl|swift|ssh|scp|sshpass|rsync)(?:\s|$)",
     "action": "kill"},
]

# 保护名单:命中其中任何一条的进程(或其子树里有这样的进程)永远不动
_PROTECTED: List[str] = [
    r"/Applications/Cursor\.app/", r"Cursor Helper", r"anysphere\.cursor-agent",
    r"cursor-dispatch", r"(?:^|/)cursor-agent(?:\s|$)",         # 调度器与 agent 运行时:杀了会毁掉整轮并行任务
    r"(?:^|/)(?:tmux|screen|zellij)(?::|\s|$)",                  # 终端复用器:用户自己的会话
    r"(?:^|/)(?:ssh-agent|gpg-agent|launchd|sshd)(?:\s|$)",
    r"/Applications/(?:Xcode|Unity Hub)\.app/",
    r"^/(?:System|usr/libexec|usr/sbin|sbin)/",
    r"dnf-unity-watchdog|tools/watchdog/",                       # 看门狗自己
]

DEFAULTS: Dict[str, Any] = {
    "repo_root": None,             # 本工程主工作区;install 时写入覆盖文件,从仓库里直接跑时自动推断
    "workspace_label": None,       # 进程环境里 CURSOR_WORKSPACE_LABEL 的取值;默认取 repo_root 的目录名
    "interval_minutes": 20,        # launchd 的运行间隔(install 时写进 plist;改了要重新 install)
    "deadline_seconds": 240,       # 单次运行的时间预算,用完就跳过剩下的重活
    "state_max_gap_minutes": 180,  # 两次观察相隔超过这么久(醒着的时间),观察记录作废重来
    "log": {"max_bytes": 1048576, "backups": 3},
    "processes": {
        "enabled": True,
        "agent_env_markers": ["CURSOR_AGENT"],   # 出身证明:环境里有其中任一变量(只判断有无,不读取取值)
        "scope_auto": True,                      # 自动把主工作区、全部 git worktree 当作本工程范围
        "extra_scope_roots": [],                 # 额外算作本工程的路径前缀
        "idle_minutes": 60,                      # 整棵树连续这么久(醒着的时间)毫无进展才算闲置
        "confirm_minutes": 5,                    # 至少连续观察这么久才允许动手(防止一次性的瞬间状态)
        "cpu_epsilon_seconds": 2.0,              # 两次观察之间 CPU(含已回收子进程)增量低于它算没动静
        "io_epsilon_mb": 8,                      # 磁盘读写增量低于它算没动静
        "net_epsilon_kb": 16,                    # 网络收发字节增量低于它算没动静(空闲连接的心跳远小于此)
        "term_grace_seconds": 10,                # SIGTERM 之后等这么久,还没退再 SIGKILL
        "max_trees_per_run": 10,                 # 一次最多清理几棵进程树,其余留到下一轮(限流,防止判定出错时一次杀太多)
        "unknown_orphans": "kill",               # 没有签名可匹配的进程:kill=有出身证明且闲置才杀;report=只报告
        "protected": _PROTECTED,
        "extra_protected": [],
        "signatures": _SIGNATURES,
        "extra_signatures": [],
    },
    # 僵尸进程本身杀不掉,只能等父进程收尸。这里只做两件事:催父进程收尸(SIGCHLD,无害),并把迟迟不收尸的父进程报告出来。
    # 父进程若恰好是本轮判定为闲置的孤儿树根,会随整棵树被清理,僵尸也就一并消失;其余父进程(可能是 agent 正在等的前台命令)绝不动。
    "zombies": {
        "enabled": True,
        "nudge_after_minutes": 5,          # 僵尸存在这么久(醒着的时间)还没被收尸,就给父进程发一次 SIGCHLD
    },
    "garbage": {
        "enabled": True,
        "tmp_dirs": None,                  # None = 自动(用户临时目录、/tmp);测试时可指定
        "ipc_leftovers": True,             # 属主已死的 .NET 调试管道/诊断套接字、MSBuild 套接字
        "ipc_min_age_seconds": 120,        # 文件至少存在这么久才会处理,避开刚启动、还没来得及出现在进程表里的属主
        "msbuild_temp_empty_hours": 6,     # 空的 MSBuildTemp* 目录存在超过这么久就删(0 = 不处理)
        "stale_locks": True,               # 持有者已死或已被无关进程复用的 heavy.sh 锁目录、过期的 git 锁文件
        "lock_dirs": None,                 # None = 自动(<仓库>/re/*/locks 与 /tmp/*/locks);里面形如 *.lock*.d 的目录才算锁
        "lock_holder_pattern": r"heavy\.sh",   # 锁的持有者进程命令行应当匹配它;匹配不上说明 pid 被复用了
        "lock_stale_seconds": 120,         # 锁目录至少存在这么久才会回收(heavy.sh 自己的宽限是 60 秒)
        "git_lock_stale_minutes": 15,
        "worktree_prune": True,            # git worktree prune:只清理目录已不存在的登记
        "build_outputs": {
            "enabled": True,               # 清理长期闲置的 worktree / 草稿克隆里的构建产物
            "idle_hours": 48,
            "purge_obj": False,            # 默认保留 obj/(里面有 --no-restore 构建必需的 project.assets.json)
            "include_scratch_clones": True,
        },
        "worktrees": {
            "remove_merged_clean": False,  # 默认只报告;True 才会移除"已并入主分支、干净、没有交接/产物文件、没人用"的 worktree
            "idle_hours": 48,
        },
        "heavy_interval_minutes": 120,     # 构建产物清理这类重活最多每隔这么久做一次
    },
}


# ---------------------------------------------------------------- 路径

def app_dir() -> str:
    return os.environ.get("DNF_WATCHDOG_HOME") or os.path.expanduser("~/Library/Application Support/dnf-unity-watchdog")


def log_dir() -> str:
    return os.environ.get("DNF_WATCHDOG_LOGS") or os.path.expanduser("~/Library/Logs/dnf-unity-watchdog")


def user_config_path() -> str:
    return os.path.join(app_dir(), "config.json")


def detect_repo_root(start: str) -> Optional[str]:
    """从 start 所在的 git 仓库找到**主**工作区(即便 start 在某个 linked worktree 里)。"""
    rc, out, _ = wd_util.run(["git", "-C", start, "rev-parse", "--git-common-dir"], timeout=15)
    if rc != 0 or not out.strip():
        return None
    common = out.strip()
    if not os.path.isabs(common):
        common = os.path.join(start, common)
    common = os.path.realpath(common)
    if os.path.basename(common) != ".git":
        return None
    return os.path.dirname(common)


# ---------------------------------------------------------------- 合并与校验

def _merge(base: Dict[str, Any], over: Dict[str, Any]) -> Dict[str, Any]:
    out = copy.deepcopy(base)
    for k, v in over.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


def _unknown_keys(user: Dict[str, Any], defaults: Dict[str, Any], prefix: str = "") -> List[str]:
    bad = []
    for k, v in user.items():
        if k not in defaults:
            bad.append(prefix + k)
        elif isinstance(v, dict) and isinstance(defaults[k], dict):
            bad.extend(_unknown_keys(v, defaults[k], prefix + k + "."))
    return bad


def validate(cfg: Dict[str, Any]) -> List[str]:
    problems: List[str] = []

    def num(path: str, value: Any, lo: float = 0.0, hi: Optional[float] = None) -> None:
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value < lo or (hi is not None and value > hi):
            problems.append("%s 必须是 %s 的数字,当前是 %r" % (path, ">= %g" % lo if hi is None else "%g..%g" % (lo, hi), value))

    def regexes(path: str, items: Any) -> None:
        if not isinstance(items, list):
            problems.append("%s 必须是列表" % path)
            return
        for i, r in enumerate(items):
            try:
                re.compile(r)
            except (re.error, TypeError) as e:
                problems.append("%s[%d] 不是合法的正则:%s" % (path, i, e))

    num("interval_minutes", cfg["interval_minutes"], 1)
    num("deadline_seconds", cfg["deadline_seconds"], 10)
    num("state_max_gap_minutes", cfg["state_max_gap_minutes"], 1)
    p = cfg["processes"]
    for k, lo in (("idle_minutes", 0), ("confirm_minutes", 0), ("cpu_epsilon_seconds", 0), ("io_epsilon_mb", 0),
                  ("net_epsilon_kb", 0), ("term_grace_seconds", 0), ("max_trees_per_run", 1)):
        num("processes." + k, p[k], lo)
    if p["unknown_orphans"] not in ("kill", "report"):
        problems.append("processes.unknown_orphans 只能是 kill 或 report")
    if not (isinstance(p["agent_env_markers"], list) and all(isinstance(x, str) and x for x in p["agent_env_markers"])):
        problems.append("processes.agent_env_markers 必须是非空字符串列表")
    regexes("processes.protected", p["protected"])
    regexes("processes.extra_protected", p["extra_protected"])
    for field in ("signatures", "extra_signatures"):
        sigs = p[field]
        if not isinstance(sigs, list):
            problems.append("processes.%s 必须是列表" % field)
            continue
        for i, s in enumerate(sigs):
            where = "processes.%s[%d]" % (field, i)
            if not isinstance(s, dict) or "args" not in s or "name" not in s:
                problems.append("%s 必须是带 name 与 args 的对象" % where)
                continue
            regexes(where + ".args", [s["args"]])
            if s.get("action", "kill") not in KINDS:
                problems.append("%s.action 只能是 %s" % (where, "/".join(KINDS)))
            if s.get("max_age_minutes") is not None:
                num(where + ".max_age_minutes", s["max_age_minutes"], 0)
    num("zombies.nudge_after_minutes", cfg["zombies"]["nudge_after_minutes"], 0)
    g = cfg["garbage"]
    num("garbage.msbuild_temp_empty_hours", g["msbuild_temp_empty_hours"], 0)
    num("garbage.ipc_min_age_seconds", g["ipc_min_age_seconds"], 0)
    num("garbage.lock_stale_seconds", g["lock_stale_seconds"], 0)
    num("garbage.git_lock_stale_minutes", g["git_lock_stale_minutes"], 1)
    num("garbage.heavy_interval_minutes", g["heavy_interval_minutes"], 1)
    num("garbage.build_outputs.idle_hours", g["build_outputs"]["idle_hours"], 1)
    num("garbage.worktrees.idle_hours", g["worktrees"]["idle_hours"], 1)
    regexes("garbage.lock_holder_pattern", [g["lock_holder_pattern"]])
    for k in ("tmp_dirs", "lock_dirs"):
        v = g[k]
        if v is not None and not (isinstance(v, list) and all(isinstance(x, str) for x in v)):
            problems.append("garbage.%s 必须是 null 或字符串列表" % k)
    return problems


def load_config(path: Optional[str] = None) -> Tuple[Dict[str, Any], List[str]]:
    """返回 (合并后的配置, 问题列表)。问题列表非空时调用方必须降级为只报告。

    覆盖文件结构错得离谱(比如把 processes 写成字符串)时,返回的配置退回默认值,保证后续代码不会因结构异常崩溃。
    """
    cfg = copy.deepcopy(DEFAULTS)
    problems: List[str] = []
    path = path or user_config_path()
    user: Any = None
    if os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                user = json.load(f)
        except (OSError, ValueError) as e:
            problems.append("读取 %s 失败:%s" % (path, e))
            user = None
        else:
            if not isinstance(user, dict):
                problems.append("%s 的顶层必须是 JSON 对象" % path)
                user = None
    if user is None:
        problems.extend(validate(cfg))
        return cfg, problems
    for k in _unknown_keys(user, DEFAULTS):
        problems.append("%s 里有不认识的键:%s(拼错了?)" % (os.path.basename(path), k))
    merged = _merge(DEFAULTS, user)
    try:
        found = validate(merged)
    except (KeyError, TypeError, AttributeError) as e:
        problems.append("配置结构有误:%r" % (e,))
        return cfg, problems
    return merged, problems + found
