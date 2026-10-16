using System.Numerics;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// x87 扩展精度精确模型的测试(证据 E-062)。期望值要么是纯数学性质(平局取偶、向零取整),
/// 要么是已用服务端原版函数在模拟器里直接验证过的结果;全部是合成数字,不含原版数据。
/// </summary>
public sealed class X87Tests
{
    private static int PercentOf(int field, int percent)
    {
        // 服务端 upgradeAbilityByMonsterParameterCategory 的乘法:数值 / 100 留在 80 位寄存器,乘上落成 float 的字段,截断
        return X87.TruncateToInt(X87.Mul(X87.Div(X87.FromInt(percent), X87.FromInt(100)), X87.FromSingle((float)field)));
    }

    [Fact]
    public void PercentMultiplyTruncatesLikeTheOriginalNotLikeDouble()
    {
        // 原版函数直接运行的结果:100 的 53% 得 52,29% 得 29,57% 得 57,58% 得 58
        Assert.Equal(52, PercentOf(100, 53));
        Assert.Equal(29, PercentOf(100, 29));
        Assert.Equal(57, PercentOf(100, 57));
        Assert.Equal(58, PercentOf(100, 58));
        Assert.Equal(58, PercentOf(200, 29));
        Assert.Equal(530, PercentOf(1000, 53));

        // 对照:同样的式子用 double 算,边界两侧的结果与原版不同(这正是需要精确模型的原因)
        double hundred = 100.0;
        double fiftyThree = 53;
        double twentyNine = 29;
        Assert.Equal(53, (int)(fiftyThree / 100.0 * hundred));
        Assert.Equal(28, (int)(twentyNine / 100.0 * hundred));
    }

    [Fact]
    public void FloatStoreRoundsHalfToEven()
    {
        Assert.Equal(16777216f, X87.ToSingle(X87.FromInt(16777217))); // 2^24 + 1 恰在两个 float 中间,取偶数一侧
        Assert.Equal(16777220f, X87.ToSingle(X87.FromInt(16777219)));
        Assert.Equal(0.3f, X87.ToSingle(X87.Div(X87.FromInt(300), X87.FromInt(1000))));
        Assert.Equal(1f / 3f, X87.ToSingle(X87.Div(X87.FromInt(1), X87.FromInt(3))));
        Assert.Equal(-0.5f, X87.ToSingle(X87.Div(X87.FromInt(-1), X87.FromInt(2))));
    }

    [Fact]
    public void ExtendedPrecisionRoundsTiesToEven()
    {
        BigInteger two64 = BigInteger.One << 64;

        // 2^64 + 1 需要 65 位有效位,恰是平局,偶数一侧是 2^64
        X87Value a = X87.Add(new X87Value(two64, 0), X87.FromInt(1));
        Assert.Equal(two64, a.Mantissa << a.Exponent);

        // 2^64 + 3:平局,偶数一侧是 2^64 + 4
        X87Value b = X87.Add(new X87Value(two64, 0), X87.FromInt(3));
        Assert.Equal(two64 + 4, b.Mantissa << b.Exponent);

        // 2^64 + 2 在 65 位下可以精确表示为 (2^63 + 1) × 2,不舍入
        X87Value c = X87.Add(new X87Value(two64, 0), X87.FromInt(2));
        Assert.Equal(two64 + 2, c.Mantissa << c.Exponent);
    }

    [Fact]
    public void DivisionUsesAStickyBitSoNearTiesRoundUp()
    {
        // 1 / 3 的二进制展开是 0.010101…,取 64 位有效位后再乘 3 不会回到 1,而是差一个很小的量:
        // 乘回去再按 64 位舍入,结果恰好是 1(这是 x87 上 (1/3)*3 == 1 的原因)
        X87Value third = X87.Div(X87.FromInt(1), X87.FromInt(3));
        X87Value back = X87.Mul(third, X87.FromInt(3));
        Assert.Equal(1, X87.TruncateToInt(back));

        // 精确的商保持精确:6 / 3 = 2
        X87Value two = X87.Div(X87.FromInt(6), X87.FromInt(3));
        Assert.Equal(BigInteger.One << 1, two.Mantissa << two.Exponent);
    }

    [Fact]
    public void TruncationGoesTowardZeroAndOverflowGivesTheIndefiniteInteger()
    {
        Assert.Equal(-3, X87.TruncateToInt(X87.Div(X87.FromInt(-7), X87.FromInt(2))));
        Assert.Equal(3, X87.TruncateToInt(X87.Div(X87.FromInt(7), X87.FromInt(2))));
        Assert.Equal(0, X87.TruncateToInt(X87.Div(X87.FromInt(1), X87.FromInt(3))));
        Assert.Equal(0, X87.TruncateToInt(X87.Div(X87.FromInt(-1), X87.FromInt(3))));
        Assert.Equal(int.MaxValue, X87.TruncateToInt(X87.FromInt(int.MaxValue)));
        Assert.Equal(int.MinValue, X87.TruncateToInt(X87.Mul(X87.FromInt(int.MaxValue), X87.FromInt(2))));
        Assert.Equal(int.MinValue, X87.TruncateToInt(X87.Mul(X87.FromInt(int.MinValue), X87.FromInt(2))));
        // 刚好越界一个单位:2^31 超出 int 范围
        Assert.Equal(int.MinValue, X87.TruncateToInt(X87.Add(X87.FromInt(int.MaxValue), X87.FromInt(1))));
    }

    [Fact]
    public void FloatsLoadExactlyAndNonFiniteValuesAreRejected()
    {
        foreach (float f in new[] { 0.1f, -3.5f, 1e10f, 1.5e-30f, float.Epsilon, 0f, 1f, -1f, 16777216f })
        {
            Assert.Equal(f, X87.ToSingle(X87.FromSingle(f)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => X87.FromSingle(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => X87.FromSingle(float.PositiveInfinity));
        Assert.Throws<DivideByZeroException>(() => X87.Div(X87.FromInt(1), X87.FromInt(0)));
    }

    [Fact]
    public void AddAlignsExponentsAndHandlesZeros()
    {
        Assert.Equal(5, X87.TruncateToInt(X87.Add(X87.FromInt(2), X87.FromInt(3))));
        Assert.Equal(-1, X87.TruncateToInt(X87.Add(X87.FromInt(2), X87.FromInt(-3))));
        Assert.Equal(7, X87.TruncateToInt(X87.Add(X87.FromInt(0), X87.FromInt(7))));
        Assert.Equal(7, X87.TruncateToInt(X87.Add(X87.FromInt(7), X87.FromInt(0))));
        // 浮点字段加"除以 100 的整数":0.5 + 1011/100 = 10.61,先在 80 位里相加,落成 float
        float sum = X87.ToSingle(X87.Add(X87.FromSingle(0.5f), X87.Div(X87.FromInt(1011), X87.FromInt(100))));
        Assert.Equal(10.61f, sum);
    }
}
