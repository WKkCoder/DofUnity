#!/usr/bin/env python3
"""把一个很长的函数的汇编(re/notes/**/<addr>.asm.txt)压成"调用大纲",便于读懂执行顺序。

反编译器对这类 C++ 函数(参数全走栈、成员指针偏移很多)输出很乱,但"按地址顺序调用了谁"仍然是清楚的:
  - 直接调用:CALL 0x008779b0           → 目标地址
  - 虚函数调用:MOV EDX,[EAX + 0x30c] … CALL EDX → 虚表槽偏移 0x30c(往前找最近一次给该寄存器赋值的 [reg + 位移])
  - 间接调用但找不到来源:CALL reg       → 标记 ?

附加信息(只读,来自 re/notes 下已有的产物,没有的就不显示):
  - 已命名的虚表槽(--slots 文件:每行 `槽偏移<TAB>名字`)
  - 已命名的函数(--names 文件:每行 `地址<TAB>名字`)
  - 字符串块引用:PUSH 0x015xxxxx 且该块在 strings_decoded.tsv 里有明文 → 显示该文本(用来给函数里的"路标"加注释)
  - 比较/跳转路标:CMP reg/内存,立即数(显示立即数,常常是状态号)

用法:
  python3 tools/py/calls_outline.py re/notes/engine/008680c0.asm.txt [--slots re/notes/slots.tsv] [--names re/notes/names.tsv] [--cmp]

输出里不含反编译出的代码,只是地址、槽号和字符串;输出放在终端或 re/ 下,不提交。
"""
import argparse
import os
import re
import sys

LINE = re.compile(r'^([0-9a-f]{8})\s+((?:[0-9a-f]{2})+)\s+(\S+)\s*(.*)$')


def load_strings(path):
    out = {}
    if not os.path.exists(path):
        return out
    with open(path, encoding='utf-8', newline='\n') as f:
        next(f, None)
        for ln in f:
            parts = ln.rstrip('\n').split('\t')
            if len(parts) >= 5:
                out[int(parts[0], 16)] = parts[4]
    return out


def load_map(path):
    out = {}
    if path and os.path.exists(path):
        with open(path, encoding='utf-8', newline='\n') as f:
            for ln in f:
                p = ln.rstrip('\n').split('\t')
                if len(p) >= 2 and not p[0].startswith('#'):
                    try:
                        out[int(p[0], 16)] = p[1]
                    except ValueError:
                        pass
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('asm')
    ap.add_argument('--slots')
    ap.add_argument('--names')
    ap.add_argument('--cmp', action='store_true', help='同时列出 CMP/CASE 的立即数')
    ap.add_argument('--strings', default='re/notes/strings_decoded.tsv')
    args = ap.parse_args()

    strings = load_strings(args.strings)
    slots = load_map(args.slots)
    names = load_map(args.names)

    regs = {}   # 寄存器 -> 最近一次 MOV reg,[x + 0xN] 的位移
    rows = []
    mem = re.compile(r'\[(\w+)(?: \+ (\w+)\*0x\d+)?(?: \+ (-?0x[0-9a-f]+))?\]')
    for ln in open(args.asm, encoding='utf-8'):
        m = LINE.match(ln.rstrip('\n'))
        if not m:
            continue
        addr, _b, mn, ops = int(m.group(1), 16), m.group(2), m.group(3), m.group(4)
        mn = mn.upper()
        if mn == 'MOV':
            dst, _, src = ops.partition(',')
            dst = dst.strip().upper()
            mm = mem.search(src)
            if mm and 'ptr' in src and dst in ('EAX', 'EBX', 'ECX', 'EDX', 'ESI', 'EDI'):
                off = mm.group(3)
                regs[dst] = (int(off, 16) if off else 0, mm.group(1).upper())
            elif dst in regs:
                regs.pop(dst, None)
        elif mn in ('XOR', 'LEA', 'POP', 'ADD', 'SUB', 'AND', 'OR', 'IMUL', 'SHL', 'SHR', 'SAR'):
            dst = ops.split(',')[0].strip().upper()
            regs.pop(dst, None)
        if mn == 'CALL':
            o = ops.strip()
            if re.fullmatch(r'0x[0-9a-f]+', o, re.I):
                t = int(o, 16)
                rows.append((addr, 'call', t, names.get(t, '')))
            else:
                reg = o.upper().replace('DWORD PTR ', '')
                mm = mem.search(o)
                if mm and 'ptr' in o:
                    off = int(mm.group(3), 16) if mm.group(3) else 0
                    rows.append((addr, 'vcall', off, slots.get(off, '')))
                elif reg in regs:
                    rows.append((addr, 'vcall', regs[reg][0], slots.get(regs[reg][0], '')))
                else:
                    rows.append((addr, 'icall', 0, ''))
        elif mn == 'PUSH':
            o = ops.strip()
            if re.fullmatch(r'0x[0-9a-f]+', o, re.I):
                v = int(o, 16)
                if v in strings:
                    rows.append((addr, 'str', v, strings[v]))
                elif args.cmp is False:
                    pass
        elif args.cmp and mn == 'CMP':
            mm = re.search(r',\s*(-?0x[0-9a-f]+)$', ops.strip())
            if mm:
                rows.append((addr, 'cmp', int(mm.group(1), 16), ops.strip()))
    for addr, kind, v, extra in rows:
        if kind == 'call':
            print('%08x  CALL   %08x  %s' % (addr, v, extra))
        elif kind == 'vcall':
            print('%08x  VCALL  +0x%-4x  %s' % (addr, v, extra))
        elif kind == 'str':
            print('%08x  STR    %08x  %s' % (addr, v, extra))
        elif kind == 'cmp':
            print('%08x  CMP    %-8d  %s' % (addr, v, extra))
        else:
            print('%08x  ICALL' % addr)


if __name__ == '__main__':
    sys.exit(main())
