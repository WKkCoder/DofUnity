"""把剑士系技能状态的"进入块"与"逐帧块"里的调用整理成清单(只读 DNF.exe 普通代码段,Capstone 静态读码,非 REA)。

做什么:对给定的状态号,按进入槽 0x00ccd2a0 的第二张分派表(下标 = 状态号 − 3)与逐帧函数 0x00cb9980 的
字节表 + 跳转表找到两段代码的地址范围,线性反汇编,把每个调用(直接调用与经主角虚表的调用)连同"按调用顺序排的栈参数"列出来;
立即数直接写数,其余写成符号(寄存器或内存操作数)。另外列出对"动画当前帧号"(读自动画对象 +0x144)的比较常量,
这些常量就是"动画播到第几帧时做某事"的来源。

只输出地址、虚表槽偏移、参数数字、帧号常量;不输出任何原版数据文件的内容。受保护区(0x01dd9000–0x02c98000)里的目标当黑盒:
直接跳进去的函数标成 protected,不去读。

用法:
  re/venv/bin/python tools/py/skill_block_calls.py 0x26            # 一个状态的进入块与逐帧块
  re/venv/bin/python tools/py/skill_block_calls.py 0x26 0x3c 0x3e  # 若干个状态
  re/venv/bin/python tools/py/skill_block_calls.py --range 0xcda8d9 0xcdaabf
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import decode_strings as DS
from dnfpaths import require_dnf_dir

HERO_VTABLE = 0x01706A24          # 剑士类虚表(状态进入槽 +0x29c、逐帧槽 +0x1f0 都在这张表里)
ENTER_TABLE2 = 0x00CE1998         # 进入槽的第二张分派表:65 项,下标 = 新状态号 − 3
FRAME_BYTE_TABLE = 0x00CCA368     # 逐帧函数:状态号 → 分支号(字节)
FRAME_JUMP_TABLE = 0x00CCA2DC     # 逐帧函数:分支号 → 地址
PROTECTED_LO, PROTECTED_HI = 0x01DD9000, 0x02C98000

# 自己起的名字(读码结论,不是原版符号);只列已经读通的,其余只写地址
DIRECT_NAMES = {
    0x6902A0: "放预载特效(层,技能键,序号,x,y,…)",
    0x690210: "放预载特效对象进图层",
    0x690420: "取预载特效对象(技能键,序号)",
    0x6903B0: "撤预载特效",
    0x911930: "静态数据第 i 个数(i,主角)",
    0x9138C0: "等级信息百分比列(列,系数)",
    0x913D70: "等级信息固定值列",
    0x8459F0: "等级信息列(列,等级,主角,属性倍率)",
    0x911B90: "尺寸列(列,等级,主角,倍率,…)",
    0x9139A0: "尺寸列B",
    0x109C900: "动画暂停/继续(旗)",
    0x109C990: "动画定位",
    0x44E720: "动画是否暂停(推断)",
    0x49C8F0: "是否基础职业",
    0x7F9EB0: "是否本机控制",
    0x11BA640: "取当前时刻",
    0x13C1780: "受保护整数写入",
    0x862D50: "放预载效果助手(推断)",
    0x11A0DE0: "读受保护整数",
    0x11A0930: "写受保护整数",
    0x119FEF0: "按键名取句柄",
    0x13D83E0: "浮点转整数",
    0x70213F: "取攻击信息第 n 项",
    0x1092340: "动画变速(动画,倍率,标志)",
}
SLOT_NAMES = {
    0x30C: "发设状态包(状态,参数,优先级,0,0)",
    0x49C: "取[etc motion]第 n 项",
    0x12C: "播放动作",
    0x200: "设攻击信息",
    0x208: "清命中名单",
    0x238: "取属性倍率(类型,…)",
    0x340: "创建被动对象",
    0x348: "按名取尺寸结构",
    0x370: "(逐帧一次性调用,含义不明)",
    0x3A8: "(进入块里设时间类字段)",
    0x42C: "设攻击身份",
    0x5F0: "设滑行速度(两个轴)",
    0x5FC: "设朝向/速度系数",
    0x604: "设朝向/速度系数B",
    0x738: "取技能表",
    0x778: "播放动作并设攻击信息(动作项,攻击信息项,标志)",
    0x7AC: "职业/武器变体选择",
    0x7D0: "取技能等级(技能号)",
    0x804: "取技能对象(技能号)",
    0x864: "技能可用/已学检查",
    0x918: "取快捷栏键",
    0x9BC: "技能可用检查(技能号,…)",
    0x154: "播放声音(句柄,…)",
    0xF8: "取主角朝向",
    0x134: "挂特效图层",
    0x138: "撤特效图层",
}


def load_pe():
    return DS.Pe(os.path.join(require_dnf_dir(), "DNF.exe"))


def read_dwords(pe, va, n):
    return list(struct.unpack("<%dI" % n, pe.read(va, 4 * n)))


def state_ranges(pe, state):
    """(进入块范围, 逐帧块范围)。块尾取"表里比它大的下一个块首"。"""
    t2 = read_dwords(pe, ENTER_TABLE2, 65)
    jt = read_dwords(pe, FRAME_JUMP_TABLE, 35)
    if state < 3 or state - 3 >= len(t2):
        raise SystemExit("状态号越界")
    enter = t2[state - 3]
    nxt = [x for x in sorted(set(t2)) if x > enter]
    enter_end = nxt[0] if nxt else enter + 0x400
    case = pe.read(FRAME_BYTE_TABLE + state, 1)[0] if state <= 0x43 else 34
    frame = jt[case]
    nxt = [x for x in sorted(set(jt)) if x > frame]
    frame_end = nxt[0] if nxt else frame + 0x400
    return (enter, enter_end), (frame, frame_end, case)


class Scanner:
    def __init__(self, pe):
        import capstone
        self.pe = pe
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        self.md.detail = True
        self.cs = capstone
        self._argc = {}

    def callee_argc(self, addr):
        """从被调函数开头向下找第一个 `ret imm`:imm/4 就是栈参数个数;跳进受保护区或读不到返回 None。"""
        if addr in self._argc:
            return self._argc[addr]
        result = None
        try:
            code = self.pe.read(addr, 0x600)
        except Exception:
            code = None
        if code:
            for insn in self.md.disasm(code, addr):
                if insn.mnemonic == "jmp" and insn.op_str.startswith("0x"):
                    tgt = int(insn.op_str, 16)
                    if PROTECTED_LO <= tgt < PROTECTED_HI:
                        result = None
                        break
                if insn.mnemonic == "ret":
                    result = (int(insn.op_str, 16) // 4) if insn.op_str else 0
                    break
        self._argc[addr] = result
        return result

    def slot_target(self, off):
        try:
            return read_dwords(self.pe, HERO_VTABLE + off, 1)[0]
        except Exception:
            return None

    def scan(self, start, end):
        code = self.pe.read(start, end - start)
        pos = 0
        events = []
        stack = []
        frame_regs = set()           # 刚读过动画帧号(+0x144)的寄存器
        last_load = {}               # 寄存器 → 最近一次 mov reg,[xxx+off] 的偏移
        while pos < len(code):
            got = False
            for insn in self.md.disasm(code[pos:], start + pos):
                got = True
                pos = insn.address + insn.size - start
                m = insn.mnemonic
                ops = insn.op_str
                if m == "push":
                    stack.append(self.sym(ops))
                elif m == "sub" and ops.startswith("esp,"):
                    try:
                        n = int(ops.split(",")[1], 16 if "0x" in ops else 10)
                        stack.extend(["(栈槽)"] * (n // 4))
                    except ValueError:
                        pass
                elif m == "mov":
                    dst, _, src = ops.partition(", ")
                    if "+ 0x144]" in src:
                        frame_regs.add(dst)
                    elif dst in frame_regs:
                        frame_regs.discard(dst)
                    if src.startswith("dword ptr [") and "+ 0x" in src and dst in ("eax", "edx", "ecx", "ebx", "edi", "esi"):
                        try:
                            off = int(src.split("+ ")[1].rstrip("]"), 16)
                            last_load[dst] = off
                        except ValueError:
                            pass
                elif m == "cmp":
                    dst, _, src = ops.partition(", ")
                    if dst in frame_regs or "+ 0x144]" in dst:
                        try:
                            v = int(src, 16) if src.startswith(("0x", "-0x")) else int(src)
                            events.append((insn.address, "帧号比较", [v]))
                        except ValueError:
                            pass
                elif m == "call":
                    name, argc = None, None
                    if ops.startswith("0x"):
                        tgt = int(ops, 16)
                        name = DIRECT_NAMES.get(tgt, "函数 0x%x" % tgt)
                        argc = self.callee_argc(tgt)
                    else:
                        reg = ops
                        off = last_load.get(reg)
                        if off is not None:
                            name = "槽 0x%x %s" % (off, SLOT_NAMES.get(off, ""))
                            tgt = self.slot_target(off)
                            argc = self.callee_argc(tgt) if tgt else None
                        else:
                            name = "间接调用 " + ops
                    if argc is None:
                        args = list(reversed(stack))
                        stack = []
                    else:
                        take = stack[len(stack) - argc:] if argc <= len(stack) else list(stack)
                        stack = stack[:len(stack) - len(take)]
                        args = list(reversed(take))
                    events.append((insn.address, name.strip(), args))
                    for r in ("eax", "ecx", "edx"):      # 被调函数可以改这三个寄存器,其余(ebx/esi/edi)跨调用保留
                        frame_regs.discard(r)
                elif m in ("jmp", "ret"):
                    pass
                if pos >= len(code):
                    break
            if pos >= len(code):
                break
            if not got:
                pos += 1
        return events

    @staticmethod
    def sym(ops):
        ops = ops.strip()
        try:
            if ops.startswith(("0x", "-0x")):
                return int(ops, 16)
            return int(ops)
        except ValueError:
            return ops


def fmt(args):
    out = []
    for a in args:
        out.append(("0x%x" % a if isinstance(a, int) and (a > 255 or a < -255) else str(a)))
    return ", ".join(out)


def report(sc, label, start, end):
    print("== %s 0x%x–0x%x (%d 字节)" % (label, start, end, end - start))
    for addr, name, args in sc.scan(start, end):
        if name == "帧号比较":
            print("  %08x  [帧号 ≥/< 比较常量] %s" % (addr, args[0]))
        else:
            print("  %08x  %s (%s)" % (addr, name, fmt(args)))


def main():
    argv = sys.argv[1:]
    if not argv:
        raise SystemExit(__doc__)
    pe = load_pe()
    sc = Scanner(pe)
    if argv[0] == "--range":
        report(sc, "范围", int(argv[1], 16), int(argv[2], 16))
        return
    for a in argv:
        state = int(a, 16) if a.startswith("0x") else int(a)
        (es, ee), (fs, fe, case) = state_ranges(pe, state)
        print("#### 状态 0x%x" % state)
        report(sc, "进入块", es, ee)
        report(sc, "逐帧块(分支 %d)" % case, fs, fe)


if __name__ == "__main__":
    main()
