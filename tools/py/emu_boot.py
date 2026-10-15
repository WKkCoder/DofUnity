#!/usr/bin/env python3
"""给模拟器里的客户端"开机":按 C 运行库启动例程的顺序跑初始化,再跑全局对象的构造函数,
这样全局的 std::map、单例指针等才是构造好的状态,后面才能调用真实的对象构造函数与成员函数(E-060)。
不运行 WinMain,不运行游戏本体;所有系统接口由 emu.py 里的替身应付。

启动例程的顺序来自对入口函数的反汇编(只记地址与顺序,不记反汇编文本):
  安全 cookie → 堆 → 多线程 → RTC → 输入输出 → 环境串 → 命令行 → _cinit(C 表、atexit、C++ 表) → (WinMain,不跑)

用法(库):
    from emu_boot import boot_client
    e, report = boot_client()
命令行:
    re/venv/bin/python tools/py/emu_boot.py [--budget 3000000]
"""
import argparse
import os
import pickle
import sys
import time
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from emu import (ClientEmu, ImportStubs, PAGE, SCRATCH_LO, TEB_BASE, align_up)  # noqa: E402
import unicorn.x86_const as X  # noqa: E402

NEW_ADDR = 0x013D6B1A                    # operator new(size),cdecl
CRT_STEPS = [                            # (名字, 函数, 参数)
    ("安全cookie", 0x013F65AB, []),
    ("堆", 0x013DD4C4, []),
    ("多线程", 0x013E8A7D, []),
    ("RTC", 0x013F0D1C, []),
    ("输入输出", 0x013EFF12, []),
    ("环境串", 0x013F6553, []),
    ("命令行解析", 0x013F64A5, []),
    ("环境变量", 0x013F6273, []),
    ("浮点初始化", 0x013E9C86, []),
]
CINIT_C = (0x01562000, 0x01562024)       # _initterm_e 的 C 初始化表
ATEXIT_RTC = (0x013D730A, 0x013F0D42)    # atexit(RTC 终止函数)
CINIT_CPP = (0x0155A710, 0x01561FFC)     # _initterm 的 C++ 全局构造表
CMDLINE_PTR = 0x01DA46F0                 # 启动例程把 GetCommandLine 的结果存在这里
ENVSTR_PTR = 0x01DA30A8                  # 环境串指针


def install_hooks(e):
    """operator new 直接从暂存区取,绕开运行库里的堆。"""
    def ret_cdecl(emu, eax):
        esp = emu.reg("esp")
        ra = emu.read32(esp)
        emu.set_reg("eax", eax)
        emu.set_reg("esp", esp + 4)
        emu.uc.reg_write(X.UC_X86_REG_EIP, ra)

    def hook_new(emu):
        size = emu.read32(emu.reg("esp") + 4)
        p = emu.scratch_alloc(max(size, 1))
        emu.alloc_sizes[p] = size
        ret_cdecl(emu, p)

    e.py_hooks[NEW_ADDR] = hook_new


CALLERS = {}


def _note_apis(e, log, unknown):
    for c in log.get("calls", []):
        label = c[0]
        if not isinstance(label, str) or "!" not in label:
            continue
        if "MessageBox" in label or "FindResource" in label:
            key = (label, c[1])
            CALLERS[key] = CALLERS.get(key, 0) + 1
        short = label.split("!", 1)[1]
        if short in ImportStubs.KNOWN or hasattr(e, "_api_" + short) or label.lower().startswith("msvcr"):
            continue
        unknown[label] = unknown.get(label, 0) + 1


def run_table(e, lo, hi, budget, report, label, unknown):
    n = ok = 0
    for p in range(lo, hi, 4):
        fn = e.read32(p)
        if fn == 0 or fn == 0xFFFFFFFF:
            continue
        n += 1
        r, log = e.call(fn, max_insns=budget)
        _note_apis(e, log, unknown)
        if r.stop == "end":
            ok += 1
        else:
            report.append((label, p, fn, r.stop, r.insns, "%08x" % r.eip))
    return n, ok


def boot_client(budget=3_000_000, verbose=False):
    e = ClientEmu()
    install_hooks(e)
    report = []
    unknown = {}
    t0 = time.time()
    steps = []
    for name, addr, args in CRT_STEPS:
        r, log = e.call(addr, args=args, max_insns=budget)
        _note_apis(e, log, unknown)
        steps.append((name, addr, r.stop, r.insns))
        if name == "环境串":
            e.write32(ENVSTR_PTR, r.eax)
        if r.stop != "end":
            report.append(("CRT", 0, addr, r.stop, r.insns, "%08x" % r.eip))
    e.write32(CMDLINE_PTR, e._api_GetCommandLineA([]))
    n1, ok1 = run_table(e, CINIT_C[0], CINIT_C[1], budget, report, "C", unknown)
    r, log = e.call(ATEXIT_RTC[0], args=[ATEXIT_RTC[1]], max_insns=budget)
    _note_apis(e, log, unknown)
    n2, ok2 = run_table(e, CINIT_CPP[0], CINIT_CPP[1], budget, report, "C++", unknown)
    info = {"steps": steps, "c": (n1, ok1), "cpp": (n2, ok2), "failures": report,
            "unknown_apis": unknown, "seconds": time.time() - t0}
    if verbose:
        for name, addr, stop, insns in steps:
            print("  %-10s %08x %s %s 条指令" % (name, addr, stop, insns))
        print("C 初始化:%d 项,%d 项正常;C++ 全局构造:%d 项,%d 项正常;用时 %.1f 秒" % (n1, ok1, n2, ok2, info["seconds"]))
    return e, info


SNAP_DEFAULT = os.path.join(os.path.dirname(HERE), "..", "re", "snap", "boot.snap")


def _pristine_image(e):
    """按装载规则重做一份干净的映像字节,用来找出开机以后被改过的页。"""
    end = 0
    for (_n, va, vsize, _roff, rsize) in e.pe.sections:
        end = max(end, va + max(vsize, rsize))
    size = align_up(end)
    img = bytearray(size)
    img[:0x400] = e.file[:0x400]
    for (_n, va, vsize, roff, rsize) in e.pe.sections:
        n = min(rsize, vsize) if vsize else rsize
        if n > 0:
            img[va:va + n] = e.file[roff:roff + n]
    return img


def snapshot_save(e, path=SNAP_DEFAULT):
    """把开机以后的状态存成文件(只存与干净映像不同的页、暂存区已用部分、线程块与簿记)。文件在 re/ 下,不入库。"""
    pristine = _pristine_image(e)
    base = e.base
    cur = bytes(e.uc.mem_read(base, len(pristine)))
    dirty = []
    for off in range(0, len(pristine), PAGE):
        if cur[off:off + PAGE] != bytes(pristine[off:off + PAGE]):
            dirty.append((base + off, cur[off:off + PAGE]))
    used = e._next_scratch - SCRATCH_LO
    state = {
        "dirty": dirty,
        "scratch": bytes(e.uc.mem_read(SCRATCH_LO, used)),
        "teb": bytes(e.uc.mem_read(TEB_BASE, 0x20000)),
        "next_scratch": e._next_scratch,
        "tls": dict(e.tls), "tls_next": e.tls_next, "qpc": e.qpc,
        "alloc_sizes": dict(e.alloc_sizes), "slots": dict(e.imports.slots),
        "auto_pages": [(lo, bytes(e.uc.mem_read(lo, hi - lo))) for lo, hi in e.mapped[e.initial_maps:]],
    }
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "wb") as f:
        f.write(zlib.compress(pickle.dumps(state, protocol=4), 3))
    return len(state["dirty"]), used


def snapshot_load(path=SNAP_DEFAULT):
    e = ClientEmu()
    install_hooks(e)
    with open(path, "rb") as f:
        st = pickle.loads(zlib.decompress(f.read()))
    for addr, data in st["dirty"]:
        e.uc.mem_write(addr, data)
    e.uc.mem_write(SCRATCH_LO, st["scratch"])
    e.uc.mem_write(TEB_BASE, st["teb"])
    for lo, data in st["auto_pages"]:
        if not e.is_mapped(lo):
            e._map(lo, len(data))
        e.uc.mem_write(lo, data)
    e._next_scratch = st["next_scratch"]
    e.tls = st["tls"]
    e.tls_next = st["tls_next"]
    e.qpc = st["qpc"]
    e.alloc_sizes = st["alloc_sizes"]
    e.imports.slots = st["slots"]
    return e


def boot_or_load(path=SNAP_DEFAULT, budget=30_000_000, force=False):
    """有快照就读快照(约几秒),没有就开机并存一份。"""
    if not force and os.path.exists(path):
        return snapshot_load(path), None
    e, info = boot_client(budget=budget)
    snapshot_save(e, path)
    return e, info


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--budget", type=int, default=3_000_000)
    ap.add_argument("--show", type=int, default=30)
    ap.add_argument("--save", action="store_true", help="开机后把状态存到 re/snap/boot.snap")
    a = ap.parse_args()
    e, rep = boot_client(a.budget, verbose=True)
    if a.save:
        n, used = snapshot_save(e)
        print("已存快照:%d 个改动页,暂存区 %d 字节" % (n, used))
    for lab, p, fn, stop, n, eip in rep["failures"][:a.show]:
        print("  失败 %s 表项 %08x 函数 %08x:%s(%d 条指令,停在 %s)" % (lab, p, fn, stop, n, eip))
    if len(rep["failures"]) > a.show:
        print("  ……另有 %d 项" % (len(rep["failures"]) - a.show))
    for (label, ra), n in sorted(CALLERS.items(), key=lambda kv: -kv[1])[:8]:
        print("  调用 %s 的返回地址 %08x:%d 次" % (label, ra, n))
    if rep["unknown_apis"]:
        print("没登记参数个数的接口(次数):")
        for k, v in sorted(rep["unknown_apis"].items(), key=lambda kv: -kv[1])[:40]:
            print("  %6d  %s" % (v, k))


if __name__ == "__main__":
    main()
