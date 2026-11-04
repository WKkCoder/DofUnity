#!/usr/bin/env python3
"""客户端"两个对象之间的角度与距离"函数的"原版机器码对拍":把 DNF.exe 明文区里的几个纯函数放进 Unicorn 里真的跑一遍(见 emu.py),
把合成输入与原版输出写成金标准文件,交给 C# 的 ActGeometry 与 [MOVE TO TARGET] 的速度算法逐位比较(证据 E-169;非 REA:本机模拟器测量)。

被调用的函数(地址是客户端里的,调用约定 cdecl;参数顺序 (x1, y1, x2, y2, 模式),角度从 (x1, y1) 指向 (x2, y2)):
  0x007fa2c0  GetAngle(x1, y1, x2, y2, mode)     返回整数度 [0,360),函数最后把整数存回第 5 个参数的栈槽再 FILD 进 x87,所以从栈槽读
  0x007fb7c0  GetDistance(x1, y1, x2, y2, mode)  返回 EAX(整数距离)
  0x013db30f  CRT 余弦:双精度参数在 XMM0,结果在 XMM0  (推断:表索引偏移比下一个多 16 格 = 四分之一圈)
  0x013d848b  CRT 正弦:同上
  [MOVE TO TARGET] 的块里把"角度(整数度)× π/180(双精度常量 0x01588fc0)"送进这两个函数,取单精度结果乘速度再向零截断,
  所以三类用例分别验证:角度函数、距离函数、整数度 0..359 的余弦与正弦(单精度落点)。

做了什么:
  1. 角度、距离:随机的合成坐标(含负数、同点、横竖线、大数)× 两种模式,记原版输出。
  2. 三角:0 到 359 度每一度,记原版余弦与正弦的双精度位型(也证实哪个地址是余弦、哪个是正弦)。
  3. 写成金标准文件(全合成,可入库;C# 单元测试 ActGeometryGoldenTests 对着它回归)。

边界:只运行计算函数,不启动游戏、不联网;输出里没有任何原版数值(输入全是随机合成的坐标)。
用法:
  . ./local.env && export DNF_DIR
  re/venv/bin/python tools/py/geometry_oracle.py --golden src/Dnf.Game.Tests/Golden/act_geometry_golden.txt --cases 400
"""
import argparse
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from emu import ClientEmu, STACK_LO, STACK_SIZE  # noqa: E402
from unicorn import x86_const as X  # noqa: E402

GET_ANGLE = 0x007FA2C0
GET_DISTANCE = 0x007FB7C0
CRT_COS = 0x013DB30F
CRT_SIN = 0x013D848B
DEG_TO_RAD = 0x01588FC0       # 双精度 π/180(8 字节)


def u32(v):
    return v & 0xFFFFFFFF


def s32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


class Oracle:
    def __init__(self):
        self.e = ClientEmu()
        raw = self.e.read(DEG_TO_RAD, 8)
        self.deg_to_rad = struct.unpack("<d", raw)[0]

    def angle(self, x1, y1, x2, y2, mode):
        e = self.e
        r, _ = e.call(GET_ANGLE, args=[u32(x1), u32(y1), u32(x2), u32(y2), mode], fast=True)
        if r.stop != "end":
            raise RuntimeError("GetAngle 没正常返回:" + str(r.stop))
        base = STACK_LO + STACK_SIZE - 0x1000
        slot = base - 4                       # 第 5 个参数(mode)的栈槽,函数把整数结果写回了这里
        return s32(e.read32(slot))

    def distance(self, x1, y1, x2, y2, mode):
        r, _ = self.e.call(GET_DISTANCE, args=[u32(x1), u32(y1), u32(x2), u32(y2), mode], fast=True)
        if r.stop != "end":
            raise RuntimeError("GetDistance 没正常返回:" + str(r.stop))
        return s32(r.eax)

    def trig(self, addr, radians):
        e = self.e
        bits = struct.unpack("<Q", struct.pack("<d", radians))[0]
        e.uc.reg_write(X.UC_X86_REG_XMM0, bits)
        r, _ = e.call(addr, args=[], fast=True)
        if r.stop != "end":
            raise RuntimeError("三角函数没正常返回:" + str(r.stop))
        out = e.uc.reg_read(X.UC_X86_REG_XMM0) & 0xFFFFFFFFFFFFFFFF
        return out


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def synthetic_points(rng, count):
    pts = []
    # 横竖线、同点、对角线、小差与大数
    for dx, dy in [(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (100, 100), (-100, 100), (100, -100), (-100, -100),
                   (300, 0), (0, 300), (-300, 0), (0, -300), (29, 29), (100, 29), (3000, 7), (-3000, -7), (1, 1), (-1, -1),
                   (20000, 15000), (-20000, 15000), (7, 20000)]:
        pts.append((rng.randint(-500, 500), rng.randint(-200, 200), dx, dy))
    while len(pts) < count:
        scale = rng.choice([30, 300, 1500, 6000])
        pts.append((rng.randint(-1500, 1500), rng.randint(-600, 600), rng.randint(-scale, scale), rng.randint(-scale, scale)))
    return pts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--golden", required=True, help="金标准输出文件")
    ap.add_argument("--cases", type=int, default=400, help="角度与距离各生成多少组坐标")
    ap.add_argument("--seed", type=int, default=20261103)
    args = ap.parse_args()

    rng = random.Random(args.seed)
    o = Oracle()
    lines = [
        "# 角度与距离函数的金标准:全合成坐标,期望值是客户端函数 0x007fa2c0 / 0x007fb7c0 与 CRT 正余弦 0x013db30f / 0x013d848b 在模拟器里的输出",
        "# (tools/py/geometry_oracle.py 生成;证据 E-169,非 REA:本机模拟器测量)。",
        "# angle x1 y1 x2 y2 mode result   —— 角度函数:从 (x1, y1) 指向 (x2, y2),整数度",
        "# dist x1 y1 x2 y2 mode result    —— 距离函数:整数",
        "# trig degrees cosBits sinBits    —— 整数度 d 对应的 (double)(float)d × π/180 的原版余弦、正弦双精度位型(十六进制)",
    ]
    for x1, y1, dx, dy in synthetic_points(rng, args.cases):
        x2, y2 = x1 + dx, y1 + dy
        for mode in (0, 1):
            a = o.angle(x1, y1, x2, y2, mode)
            lines.append("angle\t%d\t%d\t%d\t%d\t%d\t%d" % (x1, y1, x2, y2, mode, a))
            d = o.distance(x1, y1, x2, y2, mode)
            lines.append("dist\t%d\t%d\t%d\t%d\t%d\t%d" % (x1, y1, x2, y2, mode, d))
    for deg in range(360):
        rad = float(f32(float(deg))) * o.deg_to_rad
        c = o.trig(CRT_COS, rad)
        s = o.trig(CRT_SIN, rad)
        lines.append("trig\t%d\t%016x\t%016x" % (deg, c, s))
    with open(args.golden, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("写了", len(lines), "行 →", args.golden)


if __name__ == "__main__":
    main()
