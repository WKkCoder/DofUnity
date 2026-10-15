#!/usr/bin/env python3
"""把 c_literals.py 处理过的"按标签分支"的反编译文本整理成一份大纲:每个标签一条,带处理代码的行号区间、
读取序列(readInt/readFloat/readString/scanTag/getTagToSign...)、对结构体字段的写入、类型号赋值、跳转标号与错误消息;
标签处理里嵌套的分支(例如 [TRIGGER] 里面循环读的各条件标签)缩进显示。

规则(只看文本结构,不做语义推断):
  if (tok == "[X]") { H }             -> 标签 X 的处理 = H
  if (tok != "[X]") { C } else { H }   -> 标签 X 的处理 = H;C 里继续是同一链的其它标签
  if (tok != "[X]") { C }              -> 标签 X 的处理 = 这个 if 之后同一层的语句(标成 fallthrough,需要人工核对)

输入输出都只放 re/ 或 out/(含原版文本,绝不提交)。

用法:python3 tools/py/c_outline.py <pseudo.txt> <outline.txt> [--tok auStack_54 --tok auStack_74 ...]
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

CMP = re.compile(r'^\s*(?:\} )?if \((?P<tok>&?\w+) (?P<op>==|!=) (?P<lit>"(?:[^"\\]|\\.)*")\)\s*\{\s*$')
CMP_GOTO = re.compile(r'^\s*if \((?P<tok>&?\w+) (?P<op>==|!=) (?P<lit>"(?:[^"\\]|\\.)*")\)\s*(?P<rest>goto \w+;|break;|continue;)\s*$')
ELSE = re.compile(r'^\s*(?:\} )?else(?P<h>.*)\{\s*$')
OPEN = re.compile(r'\{\s*$')
CLOSE = re.compile(r'^\s*\}')


class Block:
    def __init__(self, header, line):
        self.header = header          # 开头那行的文本(去掉缩进)
        self.line = line              # 起始行号(1 起)
        self.end = line
        self.items = []               # ("stmt", lineno, text) | ("block", Block)
        self.else_block = None
        self.cmp = None               # (tok, op, lit) 若是标签比较


def parse(lines, start):
    root = Block("<root>", start + 1)
    stack = [root]
    last_closed = None  # 刚关闭的块(用来挂 else)
    for i in range(start, len(lines)):
        ln = lines[i]
        t = ln.strip()
        if not t:
            continue
        no = i + 1
        m_else = ELSE.match(ln)
        if m_else and last_closed is not None:
            b = Block("else" + m_else.group("h"), no)
            last_closed.else_block = b
            stack.append(b)
            last_closed = None
            continue
        if t.startswith("}") and not OPEN.search(t):
            # 关闭一个块
            if len(stack) > 1:
                b = stack.pop()
                b.end = no
                last_closed = b
            continue
        if t.startswith("}") and OPEN.search(t):
            # "} while (..) {" 等罕见形式:先关再开
            if len(stack) > 1:
                b = stack.pop()
                b.end = no
                last_closed = b
        if OPEN.search(t):
            b = Block(t, no)
            m = CMP.match(ln)
            if m:
                b.cmp = (m.group("tok"), m.group("op"), m.group("lit"))
            stack[-1].items.append(("block", b))
            stack.append(b)
            last_closed = None
            continue
        m = CMP_GOTO.match(ln)
        if m:
            b = Block(t, no)
            b.cmp = (m.group("tok"), m.group("op"), m.group("lit"))
            b.items.append(("stmt", no, m.group("rest")))
            b.end = no
            stack[-1].items.append(("block", b))
            last_closed = b
            continue
        stack[-1].items.append(("stmt", no, t))
        last_closed = None
    return root


# 还没有起名字的几个读取函数(值表达式、关键字或整数、关键字或字符串、[LOAD TARGET] 解析)按地址匹配;
# 反编译器给未命名函数的自动名字由前缀加 8 位十六进制地址组成,这里用拼接写出,避免把工具源码误当成反编译输出
UNNAMED_READERS = ("01330240", "012253d0", "012258f0", "01330670")
READ_PAT = re.compile(r"\b(readInt|readFloat|readString|readStringToWstr|readIntChecked|scanTag|getTagToSign|"
                      + "|".join("FUN" + "_" + a for a in UNNAMED_READERS) + r")\(")
TYPE_PAT = re.compile(r'^\w+\[0\] = (?P<v>[^;]+);$')
STORE_PAT = re.compile(r'^\*\((?P<ty>[\w ]+) ?\*\)\((?P<base>\w+) \+ (?P<off>0x[0-9a-f]+)\) = (?P<val>[^;]+);$')
GOTO_PAT = re.compile(r'goto (LAB_\w+);')
MSG_PAT = re.compile(r'^(?P<v>\w+) = (?P<s>"(?:[^"\\]|\\.)*");$')


def flat_stmts(block):
    """块里不在"标签比较子块"内的语句(含普通 if/循环里的语句),顺序保持。"""
    out = []
    for it in block.items:
        if it[0] == "stmt":
            out.append((it[1], it[2]))
        else:
            b = it[1]
            if b.cmp is not None:
                continue
            out.append((b.line, b.header))
            out.extend(flat_stmts(b))
            if b.else_block is not None:
                out.append((b.else_block.line, b.else_block.header))
                out.extend(flat_stmts(b.else_block))
    return out


CAST = re.compile(r"\((?:undefined\d?|int|uint|byte|char|short) \**\)")
ASSIGN_CONST = re.compile(r"^(?P<v>\w+) = (?P<c>(?:\((?:undefined\d?|int|uint) \**\))?(?:0x[0-9a-f]+|\d+|&DAT_[0-9a-f]+))( ?;)$")
ASSIGN_READ = re.compile(r"^(?P<v>\w+) = (?:\((?:undefined\d?|int|uint) \**\))?(?P<f>readInt|readFloat|readString)\(")
PUSH = re.compile(r"vectorPushStub\(\w+,(?:\(int \*\))?&(?P<v>\w+)\)")


def describe(block):
    reads, stores, types, gotos, msgs, pushes = [], [], [], [], [], []
    consts = {}
    for no, t in flat_stmts(block):
        m = ASSIGN_CONST.match(t)
        if m:
            c = CAST.sub("", m.group("c")).strip()
            if c.startswith("&DAT_"):
                c = "0x" + c[5:].lstrip("0") if c[5:].strip("0") else "0x0"
            consts[m.group("v")] = c
        else:
            m = ASSIGN_READ.match(t)
            if m:
                consts[m.group("v")] = m.group("f")
        m = PUSH.search(t)
        if m:
            pushes.append(consts.get(m.group("v"), "?"))
        for m in READ_PAT.finditer(t):
            reads.append(m.group(1))
        m = TYPE_PAT.match(t)
        if m and re.match(r"^(auStack_c34|\w+)\[0\]", t):
            types.append(m.group("v"))
        m = STORE_PAT.match(t)
        if m:
            stores.append("%s+%s=%s" % (m.group("ty").replace("undefined", "u"), m.group("off"), m.group("val")[:30]))
        for m in GOTO_PAT.finditer(t):
            gotos.append(m.group(1))
        m = MSG_PAT.match(t)
        if m and "오류" in m.group("s") or (m and "Error" in m.group("s")):
            msgs.append(m.group("s")[:70])
    return reads, stores, types, gotos, msgs, pushes


def walk(block, depth, out, toks):
    for it in block.items:
        if it[0] != "block":
            continue
        b = it[1]
        if b.cmp is None:
            walk(b, depth, out, toks)
            if b.else_block is not None:
                walk(b.else_block, depth, out, toks)
            continue
        tok, op, lit = b.cmp
        if op == "==":
            handler, fall = b, False
            emit(lit, handler, depth, out, toks, fall)
            # if (==) {H} else {rest of chain}
            if b.else_block is not None:
                walk(b.else_block, depth, out, toks)
        else:
            if b.else_block is not None:
                emit(lit, b.else_block, depth, out, toks, False)
            else:
                emit(lit, None, depth, out, toks, True)
            walk(b, depth, out, toks)


def emit(lit, handler, depth, out, toks, fall):
    pad = "  " * depth
    if handler is None:
        out.append("%s%s  (fallthrough: 处理在同层 if 之后)" % (pad, lit))
        return
    reads, stores, types, gotos, msgs, pushes = describe(handler)
    s = "%s%s  L%d-%d" % (pad, lit, handler.line, handler.end)
    if types:
        s += "  type=" + ",".join(types[:3])
    if pushes:
        s += "  push=" + ",".join(pushes[:10]) + ("..." if len(pushes) > 10 else "")
    if reads:
        s += "  reads=" + ",".join(reads[:14]) + ("..." if len(reads) > 14 else "")
    if stores:
        s += "  stores=" + ";".join(stores[:6]) + ("..." if len(stores) > 6 else "")
    if gotos:
        s += "  goto=" + ",".join(dict.fromkeys(gotos))
    if msgs:
        s += "  msg=" + " | ".join(dict.fromkeys(msgs))
    out.append(s)
    # 嵌套的比较链
    sub = []
    walk(handler, depth + 1, sub, toks)
    out.extend(sub)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--tok", action="append", default=[])
    a = ap.parse_args()
    for p in (a.src, a.dst):
        ap_ = os.path.abspath(p)
        if not any(ap_.startswith(os.path.join(ROOT, d) + os.sep) for d in ("re", "out")):
            sys.exit("拒绝:输入输出必须在 re/ 或 out/ 下(含原版文本,不入库)")
    lines = open(a.src, encoding="utf-8").read().split("\n")
    decl = re.compile(r"^  (undefined\d?|int|uint|float|float10|char|short|ushort|byte|bool|long|ulong|double|longlong|code|ulonglong|dword|word|undefined[0-9]*)[ \*]")
    start = next(i for i, l in enumerate(lines) if i > 20 and l.startswith("  ") and not decl.match(l) and l.strip())
    root = parse(lines, start)
    out = []
    walk(root, 0, out, a.tok)
    open(a.dst, "w", encoding="utf-8").write("\n".join(out) + "\n")
    print("已写入", a.dst, len(out), "条")


if __name__ == "__main__":
    main()
