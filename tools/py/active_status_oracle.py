#!/usr/bin/env python3
"""异常状态共享公式的"原版机器码对拍":把服务端 ELF 里 SDCActiveStatusSyncCommon 的公式函数放进 Unicorn 里真的跑一遍(见 elf_emu.py),
把输入与原版的输出写成用例文件,交给 C# 的 ActiveStatusFormulas 重算比较(证据 E-136;非 REA:本机模拟器测量)。

被调用的函数(地址是服务端符号表里的,调用约定 cdecl,成员函数的 this 传 0):
  0x088924c1  GetUniformVelocityFloatSDC(float a, float b, int i, int n)         返回 x87 栈顶(扩展精度)
  0x0808d37a  getActiveStatusPower1(this, kind, level)                            返回 x87 栈顶(落到 float 的值)
  0x0808d3dc  getActiveStatusPower2(this, kind, level)
  0x0808d43e  getActiveStatusEtcParameter(this, kind, level, slot)
  0x0808d272  getActiveStatusDurationServer(this, kind, level, objType, p5, p6)  返回 EAX
  0x0808dcc4  getPoisonDamage(this, data, 0, step)                                 返回 EAX
  0x0808dd5c  getDiseaseDamage(this, data, step)
  0x0808de08  getBurnDamage(this, flagPtr, data, 0, step)
  0x0808de9e  getBleedingDamageClient(this, data, 0, 0)
  0x0808df1a  getBleedingDamageServer(this, data)
  0x0808d074  isSyncActiveStatus(kind)
没调用的:makeEtcParameters(要 std::vector 与 operator new,模拟器里没有 libstdc++;它只是上面 EtcParameter 取整后按种类拼起来,C# 单测覆盖)。

做了什么:
  1. 把合成的表(最高等级、默认秒数、威力 1/2、附加参数、玩家对玩家系数)写到服务端的全局数组(地址见下面的常量)。
  2. 每个用例喂随机输入,读回原版输出:整数在 EAX,x87 返回值用一小段桩代码(FSTP m80)存到内存再读出。
  3. --write-cases:写成用例文件,`dnf status-check --cases 文件` 让 C# 重算比较;--golden-cases:写金标准文件(全合成,可入库,
     C# 单元测试对着它回归)。

边界:只运行计算函数,不启动服务,不联网;输出里没有任何原版数值(表是随机合成的)。
用法:
  re/venv/bin/python tools/py/active_status_oracle.py --stress 20000 --write-cases out/as_cases.txt
  re/venv/bin/python tools/py/active_status_oracle.py --golden-cases 600
"""
import argparse
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import elf_emu  # noqa: E402
from unicorn import UcError  # noqa: E402
from unicorn.x86_const import (UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDI, UC_X86_REG_EDX,  # noqa: E402
                               UC_X86_REG_EIP, UC_X86_REG_ESI, UC_X86_REG_ESP, UC_X86_REG_FPCW, UC_X86_REG_FPSW, UC_X86_REG_FPTAG)

F_UNIFORM = 0x088924C1
F_POWER1 = 0x0808D37A
F_POWER2 = 0x0808D3DC
F_ETC = 0x0808D43E
F_DURATION = 0x0808D272
F_POISON = 0x0808DCC4
F_DISEASE = 0x0808DD5C
F_BURN = 0x0808DE08
F_BLEED_CLIENT = 0x0808DE9E
F_BLEED_SERVER = 0x0808DF1A
F_SYNC = 0x0808D074

G_MAXLEVEL = 0x0936E484
G_U2U = 0x0936E48C
G_U2U_TOWER = 0x0936E490
G_DURATION = 0x093F58A0
G_POWER1 = 0x093F5900
G_POWER2 = 0x093F59A0
G_ETC = 0x093F5A40

KINDS = 18
INT_MIN = -2 ** 31


def f32r(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def f32_bits(x):
    return struct.unpack("<i", struct.pack("<f", x))[0]


def wrap32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


class Oracle:
    def __init__(self):
        self.emu = elf_emu.ElfEmu()
        emu = self.emu
        self.store = emu.alloc(16)
        # 桩:RET_MAGIC 处放 FSTP m80 [store];x87 返回值的函数以 RET_MAGIC + 6 为终点
        emu.write(elf_emu.RET_MAGIC, b"\xDB\x3D" + struct.pack("<I", self.store) + b"\xCC")
        self.data = emu.alloc(64)
        self.flag = emu.alloc(16)

    def load_table(self, max_level, durations, p1, p2, etc, u2u, u2u_tower):
        emu = self.emu
        emu.write(G_MAXLEVEL, struct.pack("<i", max_level))
        emu.write(G_DURATION, struct.pack("<%di" % KINDS, *durations))
        emu.write(G_POWER1, struct.pack("<%df" % (KINDS * 2), *p1))
        emu.write(G_POWER2, struct.pack("<%df" % (KINDS * 2), *p2))
        emu.write(G_ETC, struct.pack("<%df" % (KINDS * 8), *etc))
        emu.write(G_U2U, struct.pack("<f", u2u))
        emu.write(G_U2U_TOWER, struct.pack("<f", u2u_tower))

    def run(self, addr, args):
        """cdecl 调用,统一以 RET_MAGIC + 6(桩里 FSTP 之后)为终点:Unicorn 会缓存"终点"处的翻译块,
        一部分调用用 RET_MAGIC 当终点、另一部分用别的,后者会在 RET_MAGIC 处被提前截停。返回 EAX。"""
        uc = self.emu.uc
        sp = elf_emu.STACK_BASE + elf_emu.STACK_SIZE - 0x1000
        sp -= 4 * len(args)
        sp &= ~0xF
        frame = struct.pack("<I", elf_emu.RET_MAGIC) + b"".join(struct.pack("<I", a & 0xFFFFFFFF) for a in args)
        sp -= len(frame)
        uc.mem_write(sp, frame)
        uc.reg_write(UC_X86_REG_ESP, sp)
        uc.reg_write(UC_X86_REG_EBP, 0)
        uc.reg_write(UC_X86_REG_FPCW, 0x37F)
        uc.reg_write(UC_X86_REG_FPSW, 0)
        uc.reg_write(UC_X86_REG_FPTAG, 0xFFFF)
        for reg in (UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDX, UC_X86_REG_ESI, UC_X86_REG_EDI):
            uc.reg_write(reg, 0)
        end = elf_emu.RET_MAGIC + 6
        try:
            uc.emu_start(addr, end, count=2_000_000)
        except UcError as e:
            eip = uc.reg_read(UC_X86_REG_EIP)
            raise RuntimeError("模拟 %08x 出错:%s,停在 EIP=%08x" % (addr, e, eip)) from e
        eip = uc.reg_read(UC_X86_REG_EIP)
        if eip != end:
            raise RuntimeError("模拟 %08x 没有返回,EIP=%08x" % (addr, eip))
        return uc.reg_read(UC_X86_REG_EAX)

    def call_fp(self, addr, args):
        """调用返回 x87 栈顶的函数;返回 (符号, 64 位尾数, 15 位指数) 的原始三元组。"""
        self.run(addr, args)
        raw = self.emu.read(self.store, 10)
        mant = struct.unpack("<Q", raw[:8])[0]
        se = struct.unpack("<H", raw[8:])[0]
        return (se >> 15, mant, se & 0x7FFF)

    # ---- 把扩展精度原始三元组规范成 "有符号奇数尾数 e 指数"(零写 0 e 0) ----
    @staticmethod
    def norm(triple):
        sign, mant, e15 = triple
        if mant == 0:
            return "0 e 0"
        exp = e15 - 16383 - 63
        while mant % 2 == 0:
            mant //= 2
            exp += 1
        return "%d e %d" % (-mant if sign else mant, exp)

    def uniform(self, a, b, i, n):
        return self.norm(self.call_fp(F_UNIFORM, [f32_bits(a), f32_bits(b), i, n]))

    def power(self, which, kind, level):
        t = self.call_fp(F_POWER1 if which == 1 else F_POWER2, [0, kind, level])
        return self.float_bits(t)

    def etc(self, kind, level, slot):
        return self.float_bits(self.call_fp(F_ETC, [0, kind, level, slot]))

    @staticmethod
    def float_bits(triple):
        """扩展精度三元组(值一定是某个 float)换成 float 的位模式(有符号 32 位)。"""
        sign, mant, e15 = triple
        if mant == 0:
            return 0x80000000 - (1 << 32) if sign else 0
        exp = e15 - 16383
        # 值 = mant / 2^63 × 2^exp,先换成 double 再落 float(值本来就是 float,不会再丢位)
        value = (mant / float(1 << 63)) * (2.0 ** exp)
        return f32_bits(-value if sign else value)

    def duration(self, kind, level, obj_type, p5, p6):
        return wrap32(self.run(F_DURATION, [0, kind, level, obj_type, p5, p6]))

    def write_data(self, base, span, obj_type, u2u, guard):
        buf = bytearray(64)
        struct.pack_into("<i", buf, 3, base)
        struct.pack_into("<i", buf, 7, span)
        buf[0xB] = obj_type & 0xFF
        buf[0xC] = 1 if u2u else 0
        struct.pack_into("<i", buf, 0x1D, guard)
        self.emu.write(self.data, bytes(buf))

    def poison(self, base, span, obj_type, u2u, guard, step):
        self.write_data(base, span, obj_type, u2u, guard)
        return wrap32(self.run(F_POISON, [0, self.data, 0, step]))

    def disease(self, base, span, obj_type, u2u, guard, step):
        self.write_data(base, span, obj_type, u2u, guard)
        return wrap32(self.run(F_DISEASE, [0, self.data, step]))

    def burn(self, base, span, obj_type, u2u, guard, step):
        self.write_data(base, span, obj_type, u2u, guard)
        self.emu.write(self.flag, b"\x00" * 4)
        return wrap32(self.run(F_BURN, [0, self.flag, self.data, 0, step]))

    def bleed_client(self, base, span):
        self.write_data(base, span, 0, False, 0)
        return wrap32(self.run(F_BLEED_CLIENT, [0, self.data, 0, 0]))

    def bleed_server(self, base):
        self.write_data(base, 0, 0, False, 0)
        return wrap32(self.run(F_BLEED_SERVER, [0, self.data]))

    def sync(self, kind):
        return self.run(F_SYNC, [kind]) & 0xFF


# ---------------------------------------------------------------- 随机合成

NICE = [0.0, 1.0, 0.5, 0.25, 0.98, 1.02, 2.0, 2.5, 3.0, 5.0, 7.5, 9.0, 10.0, 26.0, 30.0, 35.0, 40.0, 60.0, 100.0, 200.0, 900.0, 1350.0,
        1500.0, 2100.0, 7200.0, 9000.0, 10500.0, -0.5, -1.0, 0.1, 0.3, 1.1]


def rand_float(rng):
    r = rng.random()
    if r < 0.40:
        return f32r(rng.choice(NICE))
    if r < 0.60:
        return f32r(float(rng.randint(-5, 3000)))
    if r < 0.80:
        return f32r(rng.uniform(-10.0, 3000.0))
    if r < 0.95:
        return f32r(rng.uniform(0.0, 1.0e6))
    return f32r(rng.uniform(1.0e8, 5.0e9))      # 越过 32 位整数:打"整数不确定值"


def rand_int(rng):
    r = rng.random()
    if r < 0.10:
        return 0
    if r < 0.60:
        return rng.randint(1, 3000)
    if r < 0.75:
        return rng.randint(-3000, -1)
    if r < 0.88:
        return rng.randint(3000, 3_000_000)
    if r < 0.95:
        return rng.randint(1 << 24, 2 ** 31 - 1)       # 大于 float 精度:打 int → float 的转换
    return rng.randint(INT_MIN, -(1 << 24))


def random_table(rng):
    max_level = rng.choice([0, 1, 2, 3, 10, 50, 150, 150, 150, 200])
    durations = [rng.choice([0, 0, 1, 7, 10, 15, 20, rng.randint(0, 40)]) for _ in range(KINDS)]
    p1 = [rand_float(rng) for _ in range(KINDS * 2)]
    p2 = [rand_float(rng) for _ in range(KINDS * 2)]
    etc = [rand_float(rng) for _ in range(KINDS * 8)]
    u2u = f32r(rng.choice([0.5, 1.0, 0.25, 0.3, 2.0, rng.uniform(0.0, 3.0)]))
    tower = f32r(rng.choice([0.5, 1.0, 0.75, rng.uniform(0.0, 3.0)]))
    return max_level, durations, p1, p2, etc, u2u, tower


def random_level(rng, max_level):
    r = rng.random()
    if r < 0.25:
        return rng.randint(-3, 3)
    if r < 0.60:
        return rng.randint(1, max(2, max_level + 3))
    if r < 0.75:
        return max_level
    return rng.randint(-5, 260)


def random_kind(rng):
    r = rng.random()
    if r < 0.85:
        return rng.randint(0, KINDS - 1)
    return rng.choice([18, 19, 25, 31, 32, 33, 100, -1])


OBJ_TYPES = [0, 0x211, 0x11, 0x200, 0x2FF, 0x1211, 0x10, 0x201, 0x213, 0x8211]
RATIOS = [0, 1, 2, 3, 7, 100, 999, -5, 1 << 20, 50]


def tid_line(tid, table):
    max_level, durations, p1, p2, etc, u2u, tower = table
    return "TABLE\t%d\t%d\t%s\t%s\t%s\t%s\t%d\t%d" % (
        tid, max_level, ",".join(str(d) for d in durations), ",".join(str(f32_bits(x)) for x in p1),
        ",".join(str(f32_bits(x)) for x in p2), ",".join(str(f32_bits(x)) for x in etc), f32_bits(u2u), f32_bits(tower))


def make_cases(oracle, rng, n, per_table, lines):
    """随机用例;返回写进 lines 的条数。一套表配 per_table 个用例。"""
    count = 0
    tid = 0
    table = None
    while count < n:
        if count % per_table == 0:
            table = random_table(rng)
            oracle.load_table(*table)
            tid += 1
            lines.append(tid_line(tid, table))
        kind = random_kind(rng)
        valid_kind = rng.randint(0, KINDS - 1)     # 威力与附加参数的数组没有越界保护:越界的种类读到的是别的全局变量,不比
        level = random_level(rng, table[0])
        choice = rng.random()
        if choice < 0.12:
            a, b = rand_float(rng), rand_float(rng)
            i, nn = rng.choice([rng.randint(-3, 5), rng.randint(0, 200)]), rng.choice([rng.randint(-3, 5), rng.randint(1, 200)])
            lines.append("UV\t%d\t%d\t%d\t%d\t%s" % (f32_bits(a), f32_bits(b), i, nn, oracle.uniform(a, b, i, nn)))
        elif choice < 0.30:
            which = rng.choice([1, 2])
            lines.append("POW\t%d\t%d\t%d\t%d\t%d" % (tid, which, valid_kind, level, oracle.power(which, valid_kind, level)))
        elif choice < 0.46:
            slot = rng.randint(0, 3)
            lines.append("ETC\t%d\t%d\t%d\t%d\t%d" % (tid, valid_kind, level, slot, oracle.etc(valid_kind, level, slot)))
        elif choice < 0.66:
            obj = rng.choice(OBJ_TYPES)
            ratio = rng.choice(RATIOS)
            lines.append("DUR\t%d\t%d\t%d\t%d\t%d\t%d" % (tid, kind, level, obj, ratio, oracle.duration(kind, level, obj, 0, ratio)))
        elif choice < 0.74:
            base, span = rand_int(rng), rand_int(rng)
            lines.append("BLEED\t%d\t%d\t%d\t%d" % (base, span, oracle.bleed_client(base, span), oracle.bleed_server(base)))
        elif choice < 0.80:
            k = rng.randint(0, 20)
            lines.append("SYNC\t%d\t%d" % (k, oracle.sync(k)))
        else:
            base, span = rand_int(rng), rng.choice([rand_int(rng), rng.randint(1, 5000)])
            obj = rng.choice([0, 10, 10, 3, 255])
            u2u = rng.random() < 0.5
            guard = rng.choice([0, 0, 50, 99, 100, 101, rng.randint(-5, 300)])
            step = rng.choice([span, rng.randint(-2, 6), rng.randint(0, 5000), 0])
            which = rng.choice(["POISON", "DISEASE", "BURN"])
            args = (base, span, obj, u2u, guard, step)
            got = {"POISON": oracle.poison, "DISEASE": oracle.disease, "BURN": oracle.burn}[which](*args)
            lines.append("%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%d" % (which, tid, base, span, obj, 1 if u2u else 0, guard, step, got))
        count += 1
    return count


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stress", type=int, default=0, help="随机合成用例条数(每 40 条换一套合成表)")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--write-cases", help="把压力用例连同原版函数的输出写到这个文件(放 out/)")
    ap.add_argument("--golden-cases", type=int, default=0, help="写金标准文件:这么多条合成用例(全合成,可入库)")
    ap.add_argument("--golden-file", default=os.path.join(elf_emu.ROOT, "src", "Dnf.Game.Tests", "Golden", "active_status_golden.txt"))
    ap.add_argument("--golden-seed", type=int, default=20260301)
    args = ap.parse_args()

    oracle = Oracle()
    print("模拟器已就绪")
    if args.stress:
        rng = random.Random(args.seed)
        lines = []
        n = make_cases(oracle, rng, args.stress, 40, lines)
        print("压力用例 %d 条" % n)
        if args.write_cases:
            os.makedirs(os.path.dirname(os.path.abspath(args.write_cases)), exist_ok=True)
            with open(args.write_cases, "w") as f:
                f.write("\n".join(lines) + "\n")
            print("已写", args.write_cases)
    if args.golden_cases:
        rng = random.Random(args.golden_seed)
        lines = ["# 异常状态共享公式的金标准:全合成的表与输入,期望值是服务端函数在模拟器里的输出(tools/py/active_status_oracle.py --golden-cases)。"]
        n = make_cases(oracle, rng, args.golden_cases, 30, lines)
        with open(args.golden_file, "w") as f:
            f.write("\n".join(lines) + "\n")
        print("金标准 %d 条 → %s" % (n, args.golden_file))


if __name__ == "__main__":
    main()
