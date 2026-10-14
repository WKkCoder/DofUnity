"""定位用户自备的 DNF 数据目录,以及本仓库的输出目录。

解析顺序:
  1. 环境变量 DNF_DIR
  2. 仓库根目录的 local.env 里的一行 DNF_DIR=/path/to/DNF  (local.env 已被 .gitignore 排除)

DNF 目录里应当有 Script.pvf 和 ImagePacks2/。本仓库不包含、也不应当提交其中任何内容。
"""
import os

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(REPO_ROOT, "out")  # 生成物(JSON、清单),已被 .gitignore 排除


def _from_local_env():
    path = os.path.join(REPO_ROOT, "local.env")
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line.startswith("DNF_DIR="):
                return line.split("=", 1)[1].strip().strip('"').strip("'")
    return None


def find_dnf_dir():
    p = os.environ.get("DNF_DIR") or _from_local_env()
    if p and os.path.isdir(p):
        return p
    return None


def require_dnf_dir():
    p = find_dnf_dir()
    if p is None:
        raise SystemExit("找不到 DNF 数据目录:请设置环境变量 DNF_DIR,或在仓库根目录的 local.env 里写一行 DNF_DIR=/你的/DNF/目录")
    return p


def ensure_out_dir():
    os.makedirs(OUT_DIR, exist_ok=True)
    return OUT_DIR
