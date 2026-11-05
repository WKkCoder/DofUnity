#!/usr/bin/env python3
"""技能施放入口映射(只读 DNF.exe 磁盘字节,静态读码,非 REA):技能号 → 分派 case → 技能键 → 处理函数 → 检查调用的参数 → 设状态的状态号。

背景(台账 E-271):分派函数 0x004a02d0 把"技能号 − 2"当下标查字节表 0x004a0c08,再按跳转表 0x004a0b80 进入某个 case;
case 里先压一个整数当"技能键",经 0x00b982e0 在对象 +0x5e4c 的映射里取处理函数;映射的内容由构造函数 0x0049f860 起的一长串登记写入。
处理函数(在 0x0049c000 到 0x004a1000 一带)先调虚槽 +0x9bc 做"能不能放"的检查,再调虚槽 +0x30c 设状态,最后压入的立即数就是状态号。

这个脚本做三件事,用来核对"某个技能号到底对应哪个处理函数":
  1. 从字节表与跳转表还原每个技能号对应的 case 地址,再从 case 里读出压入的技能键;
  2. 从登记代码里读出(技能键 → 处理函数地址)的配对;
  3. 在每个处理函数里读出检查调用与设状态调用的立即数参数(按调用顺序:最后压栈的是第一个参数)。
重点:只用"处理函数里出现过等于某技能号的立即数"来认技能会认错(检查调用的第二个参数、状态号都可能碰巧等于某个技能号),
所以这里只信"分派 case 压入的键"与"登记配对里的键"。

用法:
  re/venv/bin/python tools/py/skill_start_map.py [--skills <技能号,技能号,…>] [--tsv 输出.tsv]
输出里没有任何 PVF 数据,只有 DNF.exe 的代码地址与立即数。
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import capstone  # noqa: E402
from capstone import x86  # noqa: E402
from decode_strings import Pe  # noqa: E402
from vtable_scan import exe_path  # noqa: E402

DISPATCH = 0x004A02D0
BYTE_TABLE = 0x004A0C08
JUMP_TABLE = 0x004A0B80
DEFAULT_CASE = 0x004A0B6D
REG_START = 0x0049F860
REG_END = 0x004A02C7
KEY_CTOR = 0x00409DC0
HANDLER_LO = 0x0049C000
HANDLER_HI = 0x004A1000
SLOT_CHECK = "0x9bc"
SLOT_SETSTATE = "0x30c"


def u32(pe, va):
    return int.from_bytes(pe.read(va, 4), "little")


def disasm(pe, lo, hi):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.detail = True
    return list(md.disasm(pe.read(lo, hi - lo), lo))


def imm_of(ins):
    """指令最后一个操作数是立即数时返回它(无符号 32 位),否则 None。"""
    if not ins.operands:
        return None
    op = ins.operands[-1]
    if op.type == x86.X86_OP_IMM:
        return op.imm & 0xFFFFFFFF
    return None


def dispatch_cases(pe):
    """返回(技能号 → case 地址,case 地址 → 压入的技能键)。"""
    cases = {}
    n = 0
    while True:
        a = u32(pe, JUMP_TABLE + 4 * n)
        if not (DISPATCH < a < DISPATCH + 0x1000):
            break
        cases[n] = a
        n += 1
    by_skill = {}
    for sid in range(2, 2 + 0xA8):
        by_skill[sid] = cases.get(pe.read(BYTE_TABLE + sid - 2, 1)[0])
    insns = disasm(pe, DISPATCH, DISPATCH + 0xC00)
    idx = {i.address: k for k, i in enumerate(insns)}
    key_of = {}
    for addr in set(cases.values()):
        if addr == DEFAULT_CASE or addr not in idx:
            key_of[addr] = None
            continue
        k = idx[addr]
        key = None
        # case 开头:push 键;lea ecx,[局部];call 0x409dc0(把整数包成键对象)
        for j in range(k, min(k + 6, len(insns))):
            ins = insns[j]
            if ins.mnemonic == "push" and imm_of(ins) is not None and key is None:
                key = imm_of(ins)
            if ins.mnemonic == "call" and imm_of(ins) == KEY_CTOR:
                break
        key_of[addr] = key
    return by_skill, key_of


def registrations(pe):
    """登记代码里的(技能键 → 处理函数地址)。登记的写法:mov ebx,<处理函数>;……;mov [ebp-x],<键>;……;call(插入映射)。"""
    insns = disasm(pe, REG_START, REG_END)
    pairs = []
    handler = None
    for i, ins in enumerate(insns):
        if ins.mnemonic == "mov" and ins.op_str.startswith("ebx, 0x"):
            v = imm_of(ins)
            if v is not None and HANDLER_LO <= v < HANDLER_HI:
                handler = v
        if ins.mnemonic == "mov" and re.match(r"dword ptr \[ebp - 0x[0-9a-f]+\], 0x[0-9a-f]+$", ins.op_str):
            v = imm_of(ins)
            nxt = insns[i + 1] if i + 1 < len(insns) else None
            # SEH 的状态号写在 [ebp-4],键写在别的局部;键之后紧跟 call(插入映射)
            if handler is not None and v is not None and v < 0x1000 and "[ebp - 4]" not in ins.op_str \
                    and nxt is not None and nxt.mnemonic == "call":
                pairs.append((v, handler))
    return pairs


def handler_facts(pe, start):
    """处理函数里每次"经 [edx+0x9bc]"(检查)与"经 [eax+0x30c]"(设状态)的虚调用:返回[(种类, 参数列表)];参数按调用顺序,寄存器压栈记 None。"""
    insns = disasm(pe, start, start + 0x240)
    out = []
    kind = None
    pushes = []
    for k, ins in enumerate(insns):
        if k > 0 and ins.mnemonic == "ret" and kind is None and not pushes:
            break
        if ins.mnemonic == "mov" and ins.op_str.startswith("edx, dword ptr [e") and ins.op_str.endswith("+ %s]" % SLOT_CHECK):
            kind, pushes = "检查", []
        elif ins.mnemonic == "mov" and ins.op_str.startswith("edx, dword ptr [e") and ins.op_str.endswith("+ %s]" % SLOT_SETSTATE):
            kind, pushes = "设状态", []
        elif ins.mnemonic == "push" and kind:
            pushes.append(imm_of(ins))
        elif ins.mnemonic == "call" and ins.op_str == "edx" and kind:
            out.append((kind, list(reversed(pushes))))
            kind, pushes = None, []
    return out


def fmt_args(args):
    return "(" + ",".join("寄存器" if v is None else "0x%x" % v for v in args) + ")"


def main():
    args = sys.argv[1:]
    only = None
    tsv = None
    if "--skills" in args:
        only = {int(x) for x in args[args.index("--skills") + 1].split(",")}
    if "--tsv" in args:
        tsv = args[args.index("--tsv") + 1]
    pe = Pe(exe_path())
    by_skill, key_of = dispatch_cases(pe)
    pairs = registrations(pe)
    reg = {}
    for k, h in pairs:
        reg.setdefault(k, []).append(h)
    print("# 登记配对 %d 个;分派表覆盖技能号 %d 个(其中默认出口 %d 个)" % (
        len(pairs), len(by_skill), sum(1 for a in by_skill.values() if a == DEFAULT_CASE)))
    rows = []
    for sid in sorted(by_skill):
        if only and sid not in only:
            continue
        case = by_skill[sid]
        if case is None or case == DEFAULT_CASE:
            rows.append((sid, "默认出口", "", "", "", ""))
            continue
        key = key_of.get(case)
        hs = reg.get(key, [])
        hdesc = ",".join("0x%08x" % h for h in hs) if hs else "未登记"
        chk = ""
        st = ""
        if len(hs) == 1:
            facts = handler_facts(pe, hs[0])
            chk = ";".join(fmt_args(a) for kind, a in facts if kind == "检查")
            st = ";".join(fmt_args(a) for kind, a in facts if kind == "设状态")
        rows.append((sid, "case 0x%08x" % case, "键 %s" % key, hdesc, chk, st))
    for r in rows:
        print("\t".join(str(x) for x in r))
    if tsv:
        with open(tsv, "w", encoding="utf-8") as f:
            f.write("技能号\t分派\t键\t处理函数\t检查调用参数\t设状态参数\n")
            for r in rows:
                f.write("\t".join(str(x) for x in r) + "\n")


if __name__ == "__main__":
    main()
