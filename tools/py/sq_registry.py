#!/usr/bin/env python3
"""还原客户端的 Squirrel 原生接口登记表:名字 -> 实现函数地址 -> 登记函数地址(E-041/E-042)。

输入(都在 re/notes/,不入库):
  strings_decoded.tsv   decode_strings.py 的输出(数据块地址 -> 明文)
  0060b630.asm.txt      登记函数的反汇编(tools/ghidra/DumpFunction.java 的输出)
登记函数里每个登记都是同一个形状(从反汇编读出):
    PUSH <名字数据块地址>
    CALL <宽字符访问函数>          ; 取出明文名字
    [ADD ESP, n]                   ; 编译器偶尔在这里合并清栈
    PUSH EAX
    PUSH <实现函数地址>
    CALL <登记函数>                ; 若干个变体,变体本身可能带参数个数/类型掩码的信息
这个脚本按这个形状顺序扫一遍,输出 TSV;对不吻合形状的名字块单独列出,不猜。

用法:python3 tools/py/sq_registry.py --out re/notes/sq_registry.tsv
"""
import argparse
import collections
import json
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ACCESSOR = "0119fef0"

LINE = re.compile(r"^([0-9a-f]{8})\s+([0-9a-f]+)\s+(\S+)\s*(.*)$")


def load_names(path):
    names = {}
    with open(path, encoding="utf-8", newline="\n") as f:
        next(f)
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            va, _w, _calls, _kind, text = line.split("\t", 4)
            names[int(va, 16)] = text
    return names


CODE_LO, CODE_HI = 0x401000, 0x155A000  # DNF.exe 的 nsn#0 代码区(E-010)
DATA_LO, DATA_HI = 0x1560000, 0x1B00000  # 数据块所在的区间(够宽就行,下面还要求名字块能被解码)
IMM = re.compile(r"^0x[0-9a-f]+$")
MOV_IMM = re.compile(r"^E[A-D]X,(0x[0-9a-f]+)$")


def classify(insn):
    """把一条指令归到几个粗类:PB 压数据块地址、CA 调访问函数、PC 压代码地址、MC 给寄存器赋代码地址、
    PI 压其他立即数、PR 压寄存器、XZ 清零寄存器、AE 清栈、CX 其他调用、. 其余。"""
    _addr, mn, op = insn
    if mn == "PUSH":
        if IMM.match(op):
            v = int(op, 16)
            if CODE_LO <= v < CODE_HI:
                return "PC", v
            if DATA_LO <= v < DATA_HI:
                return "PB", v
            return "PI", v
        return "PR", None
    if mn == "CALL":
        if op.lower() == "0x" + ACCESSOR:
            return "CA", None
        return "CX", int(op, 16) if IMM.match(op) else None
    if mn == "MOV":
        m = MOV_IMM.match(op)
        if m:
            v = int(m.group(1), 16)
            return ("MC", v) if CODE_LO <= v < CODE_HI else ("MI", v)
    if mn == "XOR" and op in ("EAX,EAX", "EBX,EBX", "ECX,ECX", "EDX,EDX"):
        return "XZ", None
    if mn == "ADD" and op.startswith("ESP,"):
        return "AE", None
    return ".", None


def parse(asm_path, names):
    """顺序扫描登记函数。每个名字调用点(PUSH 名字块; CALL 访问函数)之后,向前扫到下一个名字调用点或下一个
    其他调用为止,这一段里:
      impl      第一个"代码地址"(PUSH 代码地址,或 MOV 寄存器,代码地址 之后再压栈)——登记的实现函数
      value     没有实现函数时的第一个其他立即数(登记整数常量时用)
      register  终止这一段的那个调用的地址(若是直接以其他调用结束)
      zero      这一段里有没有 XOR 寄存器,寄存器(紧接着压 0)
    这是按形状读出来的对应关系,不是证明;其中最常见的形状(见 shape 列)有成百上千个同形的例子,互相印证。"""
    insns = []
    with open(asm_path, encoding="utf-8") as f:
        for line in f:
            m = LINE.match(line)
            if m:
                insns.append((int(m.group(1), 16), m.group(3), m.group(4).strip()))
    cls = [classify(x) for x in insns]
    n = len(insns)
    entries = []
    def call_after(i):
        """PB 在 i;返回紧随其后(中间只夹至多 3 条不相干指令)的 CA 的下标,没有则 None。"""
        for k in range(i + 1, min(n, i + 5)):
            if cls[k][0] == "CA":
                return k
            if cls[k][0] != ".":
                return None
        return None

    for i in range(n - 1):
        ca = call_after(i) if cls[i][0] == "PB" else None
        if ca is not None:
            blob = cls[i][1]
            impl = None
            value = None
            register = None
            zero = False
            toks = []
            j = ca + 1
            while j < n and j < ca + 40:
                c, v = cls[j]
                if c == "PB" and call_after(j) is not None:
                    break
                if c == "CX":
                    register = v
                    toks.append("CX")
                    break
                if c in ("PC", "MC") and impl is None:
                    impl = v
                elif c == "PI" and value is None:
                    value = v
                elif c == "XZ":
                    zero = True
                if not toks or toks[-1] != c or c != ".":
                    toks.append(c)
                j += 1
            entries.append({"site": insns[i][0], "blob": blob, "name": names.get(blob), "impl": impl,
                            "value": value if impl is None else None, "register": register,
                            "zero": zero, "shape": " ".join(toks)})
    return entries


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--strings", default=os.path.join(ROOT, "re/notes/strings_decoded.tsv"))
    ap.add_argument("--asm", default=os.path.join(ROOT, "re/notes/0060b630.asm.txt"))
    ap.add_argument("--out", default=os.path.join(ROOT, "re/notes/sq_registry.tsv"))
    ap.add_argument("--json", help="另存一份统计 JSON(可提交的只有计数,不含原版内容)")
    args = ap.parse_args()

    names = load_names(args.strings)
    entries = parse(args.asm, names)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("site\tblob\tname\timpl\tvalue\tregister\tzero\tshape\n")
        for e in entries:
            f.write("%08x\t%08x\t%s\t%s\t%s\t%s\t%d\t%s\n" % (
                e["site"], e["blob"], e["name"],
                "" if e["impl"] is None else "%08x" % e["impl"],
                "" if e["value"] is None else "%d" % e["value"],
                "" if e["register"] is None else "%08x" % e["register"],
                1 if e["zero"] else 0, e["shape"]))
    distinct_names = set(e["name"] for e in entries if e["name"])
    with_impl = [e for e in entries if e["impl"] is not None]
    with_value = [e for e in entries if e["value"] is not None]
    impls = collections.Counter(e["impl"] for e in with_impl)
    print("名字调用点 %d 个;不同名字 %d;有实现地址的 %d 个(不同实现 %d);只有整数值的 %d 个;两者都没有的 %d 个"
          % (len(entries), len(distinct_names), len(with_impl), len(impls), len(with_value),
             len(entries) - len(with_impl) - len(with_value)))
    sq = [e for e in entries if e["name"] and e["name"].startswith("sq_")]
    sq_impl = [e for e in sq if e["impl"] is not None]
    print("sq_ 开头:调用点 %d 个、不同名字 %d;有实现地址 %d 个(不同名字 %d)"
          % (len(sq), len(set(e["name"] for e in sq)), len(sq_impl), len(set(e["name"] for e in sq_impl))))
    shared = sum(1 for c in impls.values() if c > 1)
    print("被多个名字共用的实现地址:%d 个" % shared)
    shapes = collections.Counter(e["shape"] for e in sq)
    print("sq_ 调用点的前几种形状:", shapes.most_common(5))
    by_kind = {}
    pairs = with_impl
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({
                "name_sites": len(entries), "distinct_names": len(distinct_names),
                "with_impl": len(with_impl), "distinct_impls": len(impls), "with_value": len(with_value),
                "sq_sites": len(sq), "sq_distinct": len(set(e["name"] for e in sq)),
                "sq_with_impl_distinct": len(set(e["name"] for e in sq_impl)), "shared_impls": shared,
            }, f, indent=2)


if __name__ == "__main__":
    main()
