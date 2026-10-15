using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 伤害计算结构(DamageVariables)的字节布局测试。偏移、类型与默认值来自客户端的结构初始化函数(0x00818110)
/// 与服务端公式里的读写(证据 E-049);这里的数值都是合成的。
/// </summary>
public sealed class DamageVariablesTests
{
    [Fact]
    public void StructHasTheOriginalSize()
    {
        Assert.Equal(0x1FC, DamageVariables.Size);
        Assert.Equal(0x1FC, new DamageVariables().ToBytes().Length);
    }

    [Fact]
    public void DefaultsFollowTheClientInitializer()
    {
        var p = new DamageVariables();
        int[] ones =
        {
            0x06, 0x13, 0x17, 0x1B, 0x1F, 0x23, 0x27, 0x2B, 0x37, 0x3B, 0x47, 0x4B, 0x4F, 0x53, 0x57, 0x5B, 0x5F,
            0x6B, 0x6F, 0x77, 0x8F, 0xB3, 0x1A6, 0x1F0,
        };
        foreach (int offset in ones)
        {
            Assert.Equal(1.0f, p.SingleAt(offset));
        }
        for (int i = 0; i < DamageVariables.TypeRateCount * 2; i++)
        {
            Assert.Equal(1.0f, p.TypeRate(i));
        }

        // 整数默认值:两个防御百分比与威力百分比是整数 1(不是浮点 1.0)。
        Assert.Equal(1, p.PhysicalDefensePercent);
        Assert.Equal(1, p.MagicalDefensePercent);
        Assert.Equal(1, p.PowerPercent);
        Assert.Equal(1, p.Int32At(0x97));

        // 加成类与输出类字段为 0;攻击者等级默认 1;模块 0;种类物理。
        Assert.Equal(0f, p.Base);
        Assert.Equal(0f, p.FlatBonus);
        Assert.Equal(0f, p.TailAdd);
        Assert.Equal(0f, p.TailPercent);
        Assert.Equal(0, p.Result);
        Assert.False(p.IsCritical);
        Assert.Equal((sbyte)1, p.Level);
        Assert.Equal((sbyte)1, p.LevelAlt);
        Assert.Equal((sbyte)0, p.Module);
        Assert.Equal(AttackKind.Physic, p.Kind);
        Assert.Equal(0u, p.Seed);
        Assert.Equal((sbyte)0, p.DeclaredElement);
        for (int i = 0; i < 4; i++)
        {
            Assert.False(p.ElementFlag(i));
            Assert.Equal(0, p.ElementResist(i));
        }
    }

    [Fact]
    public void UnalignedFieldsLiveAtTheOriginalOffsets()
    {
        var p = new DamageVariables();
        p.Seed = 0x04030201;
        p.PowerPercent = 0x0A0B0C0D;
        p.Result = 0x11223344;
        p.DefenderOwnerType = 0x111;
        p.CreatureExtra = 1.5;
        p.CreatureRate = 2.5f;
        byte[] raw = p.ToBytes();

        // 种子在 +0x1D9(奇数偏移,小端)。
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, raw[0x1D9..0x1DD]);
        // 威力百分比在 +0x1D5。
        Assert.Equal(new byte[] { 0x0D, 0x0C, 0x0B, 0x0A }, raw[0x1D5..0x1D9]);
        // 输出的最终伤害在 +0x1E6。
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, raw[0x1E6..0x1EA]);
        // 防御方"主人"种类码在 +0x1C6。
        Assert.Equal(new byte[] { 0x11, 0x01, 0x00, 0x00 }, raw[0x1C6..0x1CA]);
        // 8 字节浮点在 +0x1AA。
        Assert.Equal(BitConverter.GetBytes(1.5), raw[0x1AA..0x1B2]);
        // 召唤物倍率在 +0x2B。
        Assert.Equal(BitConverter.GetBytes(2.5f), raw[0x2B..0x2F]);
    }

    [Fact]
    public void ArraysAndSignedBytesUseTheOriginalLayout()
    {
        var p = new DamageVariables();
        p.SetElementResist(3, -7);
        p.SetElementFlag(2, true);
        p.SetTypeRate(0, 2.0f);
        p.SetTypeRate(26, 3.0f);
        p.SetConditionalFlag(2, true);
        p.SetConditionalPercent(1, 4.0f);
        p.Module = -3;
        p.CreatureLevel = -1;
        byte[] raw = p.ToBytes();

        Assert.Equal(-7, BitConverter.ToInt32(raw, 0x7F + 4 * 3));
        Assert.Equal(1, raw[0x0A + 2]);
        Assert.Equal(2.0f, BitConverter.ToSingle(raw, 0xD6));
        Assert.Equal(3.0f, BitConverter.ToSingle(raw, 0x13E));
        Assert.Equal(1, raw[0xD3 + 2]);
        Assert.Equal(4.0f, BitConverter.ToSingle(raw, 0x1F + 4));
        Assert.Equal(0xFD, raw[0x1B2]);
        Assert.Equal(0xFF, raw[0x1B7]);
        Assert.Equal((sbyte)-3, p.Module);
        Assert.Equal((sbyte)-1, p.CreatureLevel);
    }

    [Fact]
    public void BytesRoundTripAndShortInputLeavesTheRestZero()
    {
        var p = new DamageVariables();
        p.Base = 123.5f;
        p.Seed = 99;
        DamageVariables copy = DamageVariables.FromBytes(p.ToBytes(), DamageVariables.Size);
        Assert.Equal(p.ToBytes(), copy.ToBytes());
        Assert.Equal(p.ToBytes(), p.Clone().ToBytes());

        // 服务端只收 500 字节:末尾两个浮点(+0x1F4/+0x1F8)得到 0,而不是默认值。
        p.TailAdd = 5f;
        p.TailPercent = 6f;
        DamageVariables wire = DamageVariables.FromBytes(p.ToBytes(), DamageVariables.WireLimit);
        Assert.Equal(0f, wire.TailAdd);
        Assert.Equal(0f, wire.TailPercent);
        Assert.Equal(123.5f, wire.Base);
        // 短输入不套用默认值:没有传入的字节全是 0(服务端的局部结构整块被覆盖)。
        DamageVariables partial = DamageVariables.FromBytes(new byte[] { 1, 2, 3 }, 3);
        Assert.Equal(0f, partial.SingleAt(0x06));
        Assert.Equal(0, partial.PowerPercent);
    }

    [Fact]
    public void ByteAtOutsideTheStructReadsZero()
    {
        var p = new DamageVariables();
        Assert.Equal(0, p.ByteAt(-1));
        Assert.Equal(0, p.ByteAt(DamageVariables.Size));
        Assert.Equal(1, p.ByteAt(0x1B3));
    }

    [Fact]
    public void IndexedAccessorsRejectOutOfRange()
    {
        var p = new DamageVariables();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.ElementFlag(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.ElementResist(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.TypeRate(52));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.ConditionalFlag(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SetConditionalPercent(-1, 1f));
    }
}
