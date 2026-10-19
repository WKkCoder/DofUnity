"""把 DNF.exe 里一段虚拟地址范围线性反汇编成文本(只读,用 Capstone),格式与 Ghidra 的清单接近,方便用 rg 查找。

用途:Ghidra 工程里有些跳转表目标没有被反汇编(清单里是 "db/data ??"),
例如鬼剑士一族的每帧处理函数 0x00cb9980 按"当前状态"分派的各个分支。这个脚本不依赖 Ghidra 的函数划分,
直接从给定地址顺着字节往下反汇编;只在代码确实连续时可靠(分支块之间的数据、填充会产生乱码行)。

输出只放在 re/ 下(已被 .gitignore 与提交守卫排除),不入库。受保护的节(#3–#8)默认拒绝,
需要显式设置 DNF_ALLOW_PROTECTED=1(与 read_va.py 一致,并要在证据台账里记一笔)。

用法:
  re/venv/bin/python tools/py/disasm_va.py <起始地址> <结束地址> [--out <文件>]
  re/venv/bin/python tools/py/disasm_va.py 00cb9b3d 00cc19b6 --out re/notes/skill/hub_cases.asm.txt
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import decode_strings as DS
from dnfpaths import require_dnf_dir

REGS = re.compile(
    r"\b(eax|ebx|ecx|edx|esi|edi|ebp|esp|ax|bx|cx|dx|si|di|bp|sp|al|bl|cl|dl|ah|bh|ch|dh|xmm[0-7]|st\(\d\)|st)\b"
)


def format_line(insn):
    text = insn.op_str
    text = REGS.sub(lambda m: m.group(1).upper(), text)
    # Ghidra 风格:操作数之间用逗号不加空格
    text = text.replace(", ", ",")
    raw = insn.bytes.hex()
    return "%08x  %-20s %s %s" % (insn.address, raw, insn.mnemonic.upper(), text)


def check_sections(pe, start, end):
    """范围必须落在有磁盘内容的节里;受保护的节(#3 到 #8)默认拒绝,与 read_va.py 一致(2026-10-15 用户授权逆向受保护区域,E-059;
    要读就显式设 DNF_ALLOW_PROTECTED=1,并在证据台账里记一笔)。"""
    allow = os.environ.get("DNF_ALLOW_PROTECTED") == "1"
    addresses = list(range(start, end, 0x1000)) + [end - 1]
    for addr in addresses:
        hit = pe.section_of(addr)
        if hit is None:
            raise SystemExit("%08x 不在任何节内" % addr)
        if 3 <= hit[0] <= 8 and not allow:
            raise SystemExit("%08x 位于节 #%d(受保护区域及其相邻的节),默认不读;需要时设 DNF_ALLOW_PROTECTED=1" % (addr, hit[0]))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out_path = None
    if "--out" in sys.argv:
        out_path = sys.argv[sys.argv.index("--out") + 1]
        args = [a for a in args if a != out_path]
    if len(args) < 2:
        raise SystemExit(__doc__)
    start = int(args[0], 16)
    end = int(args[1], 16)
    if end <= start:
        raise SystemExit("结束地址必须大于起始地址")
    import capstone

    pe = DS.Pe(os.path.join(require_dnf_dir(), "DNF.exe"))
    check_sections(pe, start, end)
    code = pe.read(start, end - start)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.detail = False
    lines = []
    pos = 0
    while pos < len(code):
        got = False
        for insn in md.disasm(code[pos:], start + pos):
            lines.append(format_line(insn))
            pos = insn.address + insn.size - start
            got = True
        if pos >= len(code):
            break
        if not got or pos < len(code):
            # 遇到反汇编不了的字节:记一行 db 并跳过一个字节继续
            lines.append("%08x  %02x                   db ??" % (start + pos, code[pos]))
            pos += 1
    text = "\n".join(lines) + "\n"
    if out_path:
        os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(text)
        print("已写出 %d 行到 %s" % (len(lines), out_path))
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
