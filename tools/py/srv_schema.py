#!/usr/bin/env python3
"""把服务端的脚本加载函数按"标签处理分支"切开,给每个标签列出读取步骤(只读静态,不运行服务端)。

服务端的 ImportXxxScript 有两种写法,本脚本都认:

  A 型(静态 map + switch):函数里有一个静态 std::map<const char*, int>(标签文本 -> 编号),
    第一次进入时每个分支先把自己的 (标签, 编号) 登记进去,之后按编号跳到分支体里读值。
    每个分支的反汇编由两段组成:前半段登记(能读出标签文本),后半段(JZ 的目标)是读值的代码。
  B 型(比较链):循环里先 ScanType 取标签,再 `if (标签 == "[xxx]") {...} else if ...` 逐个比较,
    每个分支体在比较失败的跳转目标之前结束。

用法:
    python3 tools/py/srv_schema.py <函数 .asm.txt> [输出 TSV]

输入:tools/ghidra/DumpFunction.java 产出的 <地址>.asm.txt(设 DNF_SKIP_DECOMPILE=1 就不做反编译,
几万字节的加载函数反编译要几分钟,这里用不上)。
输出 TSV 列:编号、标签、风格、读值段起止、形状、读取步骤。
  形状:I 整数、F 浮点、S 字符串、T 标签名、M 混合类(TypeOrInt 等);直线分支才可靠,带 * 的表示
    分支体里有循环或条件跳转,形状只是下限。
  步骤里的 "+0x..." 是被填充的那个结构体(函数的第一个参数)里的偏移;偏移的含义要靠别处核对。

输出属于逆向产物,只放 re/ 或 out/(已被 .gitignore 与提交守卫排除),不提交。
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from read_elf_va import Elf32  # noqa: E402
from srv_annotate import Symbols, LINE  # noqa: E402

CMP_EAX = re.compile(r"^CMP EAX,(-?0x[0-9a-f]+|-?\d+)$")
JCC = re.compile(r"^(JZ|JNZ|JG|JL|JGE|JLE|JA|JB|JAE|JBE|JMP)\s+0x([0-9a-f]{8})$")
MOV_THIS = re.compile(r"^MOV (E[A-Z]{2}),dword ptr \[EBP \+ 0x8\]$")
ADD_IMM = re.compile(r"^ADD (E[A-Z]{2}),(0x[0-9a-f]+)$")
LEA = re.compile(r"^LEA (E[A-Z]{2}),\[(E[A-Z]{2})(?: \+ (0x[0-9a-f]+))?\]$")
MOV_ARG = re.compile(r"^MOV dword ptr \[ESP(?: \+ (0x[0-9a-f]+))?\],(E[A-Z]{2})$")
STORE_REG = re.compile(r"^MOV (?:dword|word|byte) ptr \[(E[A-Z]{2})(?: \+ (0x[0-9a-f]+))?\],(E[A-Z]{2}|AX|AL|DX|DL)$")
STORE_FP = re.compile(r"^FSTP dword ptr \[(E[A-Z]{2})(?: \+ (0x[0-9a-f]+))?\]$")
LOAD_LOCAL = re.compile(r"^MOV (E[A-Z]{2}),dword ptr \[EBP \+ (0x[0-9a-f]+)\]$")
FLD_LOCAL = re.compile(r"^FLD dword ptr \[EBP \+ (0x[0-9a-f]+)\]$")
LEA_LOCAL = re.compile(r"^LEA (E[A-Z]{2}),\[EBP \+ (0x[0-9a-f]+)\]$")
CALL = re.compile(r"^CALL 0x([0-9a-f]{8})$")
ROUND_FUNCS = {"GetRoundInt", "GetFloorInt", "GetCeilInt", "roundf", "floorf", "ceilf"}
IMM_TAG = re.compile(r"^MOV dword ptr \[ESP \+ 0x4\],(0x[0-9a-f]{7,8})$")
IMM_CODE = re.compile(r"^MOV dword ptr \[EBP \+ 0x[0-9a-f]+\],(0x[0-9a-f]+)$")


def strip_templates(name):
    """去掉名字里嵌套的 <...> 与参数表,留下 Class::method。"""
    out = []
    depth = 0
    for ch in name:
        if ch == "<":
            depth += 1
            continue
        if ch == ">":
            depth = max(0, depth - 1)
            continue
        if depth == 0:
            out.append(ch)
    s = "".join(out)
    i = s.find("(")
    if i > 0:
        s = s[:i]
    return s.replace("std::", "")


def load_asm(path):
    lines = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            m = LINE.match(raw.rstrip("\n"))
            if m:
                lines.append((int(m.group(1), 16), m.group(3)))
    return lines


def to_int(s):
    return int(s, 16) if s.startswith(("0x", "-0x")) else int(s)


def shape_letter(name):
    if name.startswith(("ScanInt",)):
        return "I"
    if name.startswith(("ScanFloat",)):
        return "F"
    if name.startswith(("ScanStr(", "ScanStr")) and "Or" not in name:
        return "S"
    if name == "ScanType":
        return "T"
    return "M"


def is_scan(name):
    return name.startswith(("Scan", "scan"))


def read_cstr(elf, va):
    try:
        return elf.cstr(va, 200)
    except ValueError:
        return ""


# ---------------------------------------------------------------- A 型

def find_dispatch(lines):
    """编号 -> 分支入口地址。比较链:CMP EAX,imm + JZ,以及 TEST EAX,EAX + JZ(编号 0)。"""
    index_of = {a: i for i, (a, _) in enumerate(lines)}

    def is_case_start(addr):
        i = index_of.get(addr)
        if i is None or i + 1 >= len(lines):
            return False
        return lines[i][1].startswith("MOVZX EAX,byte ptr [0x") and lines[i + 1][1] == "XOR EAX,0x1"

    pairs = []
    for i in range(len(lines) - 1):
        a, t = lines[i]
        _, t2 = lines[i + 1]
        m = CMP_EAX.match(t)
        j = JCC.match(t2)
        if m and j and j.group(1) == "JZ":
            pairs.append((a, to_int(m.group(1)), int(j.group(2), 16)))
        elif t == "TEST EAX,EAX" and j and j.group(1) == "JZ":
            pairs.append((a, 0, int(j.group(2), 16)))
    code_to_addr = {}
    for _, code, tgt in pairs:
        if is_case_start(tgt):
            code_to_addr.setdefault(code, tgt)
    return code_to_addr


def branches_style_a(lines, elf):
    code_to_addr = find_dispatch(lines)
    if not code_to_addr:
        return []
    starts = sorted(set(code_to_addr.values()))
    index_of = {a: i for i, (a, _) in enumerate(lines)}
    func_end = lines[-1][0] + 1
    out = []
    for si, st in enumerate(starts):
        end = starts[si + 1] if si + 1 < len(starts) else func_end
        i0 = index_of[st]
        body = []
        k = i0
        while k < len(lines) and lines[k][0] < end:
            body.append(lines[k])
            k += 1
        tag = None
        run_start = None
        saw_inited = False
        for _a, t in body[:40]:
            m = IMM_TAG.match(t)
            if m and tag is None:
                s = read_cstr(elf, int(m.group(1), 16))
                if s.startswith("["):
                    tag = s
            if t.startswith("MOVZX") and "byte ptr [0x" in t:
                saw_inited = True
            j = JCC.match(t)
            if j and j.group(1) == "JZ" and saw_inited and run_start is None:
                run_start = int(j.group(2), 16)
        codes = sorted(c for c, a in code_to_addr.items() if a == st)
        # 登记段到"JMP 下一个分支"为止;读值的循环体可能排在登记段之后、入口 run_start 之前,
        # 所以分析区间取 [登记段结束, 分支结束),而不是 [run_start, 分支结束)
        analysis_start = run_start
        for k2, (a2, t2) in enumerate(body[:60]):
            j2 = JCC.match(t2)
            if j2 and j2.group(1) == "JMP" and int(j2.group(2), 16) == end and k2 + 1 < len(body):
                analysis_start = body[k2 + 1][0]
                break
        if run_start is not None and analysis_start is not None:
            analysis_start = min(analysis_start, run_start)
        out.append({"codes": codes, "tag": tag, "style": "A", "start": st, "run_start": run_start,
                    "end": end, "body": body, "analysis_start": analysis_start})
    return out


# ---------------------------------------------------------------- B 型

def branches_style_b(lines, elf, syms):
    """找 `MOV [ESP+4],"[tag]"` + 调用 operator== / 字符串比较 + TEST AL,AL + JZ 下一个比较。"""
    index_of = {a: i for i, (a, _) in enumerate(lines)}
    cmp_calls = {}
    for a, name in syms.by_addr.items():
        n = name
        if ("operator==" in n and "basic_string" in n and "char const*" in n) or n.startswith(
                ("strcmp", "stricmp", "strcasecmp")):
            cmp_calls[a] = n
    found = []
    for i, (a, t) in enumerate(lines):
        m = CALL.match(t)
        if not m or int(m.group(1), 16) not in cmp_calls:
            continue
        lit = None
        lit_i = None
        for k in range(i - 1, max(-1, i - 6), -1):
            mm = IMM_TAG.match(lines[k][1])
            if mm:
                lit = read_cstr(elf, int(mm.group(1), 16))
                lit_i = k
                break
        if lit is None or not lit.startswith("["):
            continue
        # 后面应当是 TEST AL,AL + JZ 下一个比较
        jz_target = None
        body_start_i = None
        for k in range(i + 1, min(len(lines), i + 4)):
            j = JCC.match(lines[k][1])
            if j and j.group(1) == "JZ":
                jz_target = int(j.group(2), 16)
                body_start_i = k + 1
                break
        if jz_target is None or body_start_i is None or body_start_i >= len(lines):
            continue
        found.append({"tag": lit, "start": lines[lit_i][0], "run_start": lines[body_start_i][0], "end": jz_target})
    out = []
    for br in found:
        i0 = index_of.get(br["run_start"])
        body = []
        k = i0
        while k is not None and k < len(lines) and lines[k][0] < br["end"]:
            body.append(lines[k])
            k += 1
        out.append({"codes": [], "tag": br["tag"], "style": "B", "start": br["start"],
                    "run_start": br["run_start"], "end": br["end"], "body": body})
    return out


# ---------------------------------------------------------------- 分支体分析

def analyse_body(syms, body, run_start):
    """逐行追踪"这个寄存器里是不是被填充结构体的某个偏移",读出 Scan* 调用把值放到了哪里。"""
    regs = {}
    args = {}
    steps = []
    letters = []
    pending = None          # (名字, 临时变量偏移或 None)
    fp_temp = None
    loops = False
    branches = 0
    run = [(a, t) for a, t in body if a >= run_start]
    run_set = {a for a, _ in run}
    prev_test = False
    prev_text = ""
    since_scan = 99
    for a, t in run:
        prev_test = prev_text == "TEST AL,AL"
        prev_text = t
        since_scan += 1
        m = MOV_THIS.match(t)
        if m:
            regs[m.group(1)] = ("this", 0)
            continue
        m = ADD_IMM.match(t)
        if m and regs.get(m.group(1), (None,))[0] == "this":
            regs[m.group(1)] = ("this", regs[m.group(1)][1] + int(m.group(2), 16))
            continue
        m = LEA_LOCAL.match(t)
        if m:
            regs[m.group(1)] = ("local", int(m.group(2), 16))
            continue
        m = LEA.match(t)
        if m:
            src = regs.get(m.group(2))
            if src and src[0] == "this":
                regs[m.group(1)] = ("this", src[1] + (int(m.group(3), 16) if m.group(3) else 0))
            else:
                regs.pop(m.group(1), None)
            continue
        m = MOV_ARG.match(t)
        if m:
            off = int(m.group(1), 16) if m.group(1) else 0
            args[off] = regs.get(m.group(2))
            continue
        m = LOAD_LOCAL.match(t)
        if m:
            d = int(m.group(2), 16)
            if pending and pending[1] == d:
                regs[m.group(1)] = ("tempval", d)
            else:
                regs.pop(m.group(1), None)
            continue
        m = FLD_LOCAL.match(t)
        if m and pending and pending[1] == int(m.group(1), 16):
            fp_temp = pending[1]
            continue
        m = STORE_FP.match(t)
        if m and m.group(1) == "ESP":
            continue    # 把浮点数当参数压栈,不是存进结构体
        if m and (fp_temp is not None or (pending and pending[1] is None)) and pending:
            base = regs.get(m.group(1))
            if base and base[0] == "this":
                steps.append("%s→+0x%x" % (pending[0], base[1] + (int(m.group(2), 16) if m.group(2) else 0)))
            else:
                steps.append("%s→?" % pending[0])
            pending = None
            fp_temp = None
            continue
        m = STORE_REG.match(t)
        if m and pending:
            src_reg = m.group(3)
            carries = (pending[1] is None and src_reg in ("EAX", "AX", "AL")) or (
                pending[1] is not None and regs.get(src_reg) == ("tempval", pending[1]))
            if carries:
                base = regs.get(m.group(1))
                if base and base[0] == "this":
                    steps.append("%s→+0x%x" % (pending[0], base[1] + (int(m.group(2), 16) if m.group(2) else 0)))
                else:
                    steps.append("%s→?" % pending[0])
                pending = None
                continue
        m = CALL.match(t)
        if m:
            addr = int(m.group(1), 16)
            name = syms.get(addr) or ("sub_%08x" % addr)
            sname = strip_templates(name)
            if pending is not None and sname in ROUND_FUNCS:
                # 读出的浮点数先过取整函数,取整结果(EAX)才写进结构体
                pending = (pending[0] + "+取整", None)
                args = {}
                regs.pop("EAX", None)
                continue
            if pending is not None:
                steps.append("%s→?" % pending[0])
                pending = None
            if is_scan(sname):
                since_scan = 0
                letters.append(shape_letter(sname))
                first = args.get(0)
                if "Str" in sname or "Type" in sname or "scanScriptValue" in sname or "Or" in sname:
                    if first and first[0] == "this":
                        steps.append("%s→+0x%x" % (sname, first[1]))
                    else:
                        steps.append("%s→?" % sname)
                elif first and first[0] == "local":
                    pending = (sname, first[1])
                else:
                    pending = (sname, None)
            else:
                steps.append(sname)
            args = {}
            regs.pop("EAX", None)
            continue
        j = JCC.match(t)
        if j:
            tgt = int(j.group(2), 16)
            # 每次 Scan* 之后的"读失败就退出"检查(XOR EAX,1; TEST AL,AL; JZ 继续 / JNZ 出错)不算分支
            if j.group(1) in ("JZ", "JNZ") and prev_test and since_scan <= 6:
                since_scan = 99
                continue
            if j.group(1) != "JMP":
                branches += 1
            if tgt <= a and tgt in run_set:
                loops = True
            continue
    if pending is not None:
        steps.append("%s→?" % pending[0])
    shape = "".join(letters)
    if loops:
        steps.append("(含循环)")
        shape += "*"
    elif branches:
        steps.append("(含 %d 个条件跳转)" % branches)
        shape += "*"
    return shape, steps


def extract(src, elf=None, syms=None):
    """读一个加载函数的反汇编文件,返回每个标签分支的 (编号, 标签, 风格, 起始, 读值起始, 结束, 形状, 步骤)。

    A 型和 B 型同时找:有的加载函数先按 A 型分派一层(比如任务的条件种类),最后一个分支里再嵌一条 B 型比较链。
    A 型分支里嵌了 3 个以上 B 型分支的,当作"容器",自己不给形状,只列出里面的分支。
    """
    lines = load_asm(src)
    elf = elf or Elf32()
    syms = syms or Symbols()
    a_br = [b for b in branches_style_a(lines, elf) if b["tag"]]
    b_br = branches_style_b(lines, elf, syms)
    for b in a_br:
        nested = [x for x in b_br if b["start"] <= x["start"] < b["end"]]
        b["container"] = len(nested) if len(nested) >= 3 else 0
    rows = []
    for b in a_br:
        if b["run_start"] is None:
            rows.append((b["codes"], b["tag"], b["style"], b["start"], None, b["end"], "", ["(不是标准分支)"]))
        elif b["container"]:
            rows.append((b["codes"], b["tag"], "A", b["start"], b["run_start"], b["end"], "",
                         ["(容器:内含 %d 个比较链分支)" % b["container"]]))
        else:
            shape, steps = analyse_body(syms, b["body"], b.get("analysis_start") or b["run_start"])
            rows.append((b["codes"], b["tag"], b["style"], b["start"], b["run_start"], b["end"], shape, steps))
    for b in b_br:
        if not b["tag"] or b["run_start"] is None:
            continue
        shape, steps = analyse_body(syms, b["body"], b["run_start"])
        rows.append((b["codes"], b["tag"], "B", b["start"], b["run_start"], b["end"], shape, steps))
    rows.sort(key=lambda r: r[3])
    return rows


def main(argv):
    if not argv:
        print(__doc__)
        return 1
    src = argv[0]
    out_path = argv[1] if len(argv) > 1 else None
    rows = extract(src)
    out = open(out_path, "w", encoding="utf-8") if out_path else sys.stdout
    try:
        out.write("code\ttag\tstyle\tstart\trun_start\trun_end\tshape\tsteps\n")
        for codes, tag, style, st, rs, end, shape, steps in rows:
            out.write("%s\t%s\t%s\t%08x\t%s\t%08x\t%s\t%s\n" % (
                ",".join("0x%x" % c for c in codes), tag or "", style, st,
                ("%08x" % rs) if rs else "", end, shape, "; ".join(steps)))
    finally:
        if out_path:
            out.close()
    n_tag = sum(1 for r in rows if r[1])
    print("分支 %d 个,有标签文本 %d 个" % (len(rows), n_tag), file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
