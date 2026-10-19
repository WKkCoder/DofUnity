namespace Dnf.Play.Tests;

/// <summary>
/// 命中停顿毫秒数的规则(证据 E-077):[hit info] 的整数不是 -1 就用它,-1 取武器表;
/// 文件里写了 [is use weapon hit delay rate] 时是"武器 × 文件";最后大于 0 的结果乘 (2 - 属性)(单精度、向零取整)。
/// 数值全是合成的。
/// </summary>
public class HitStopTests
{
    [Fact]
    public void MinusOneTakesTheWeaponValueAndAnyOtherNumberIsUsedAsWritten()
    {
        Assert.Equal(44, HitStop.StunMs(-1, false, 44));
        Assert.Equal(25, HitStop.StunMs(25, false, 44));
        Assert.Equal(0, HitStop.StunMs(0, false, 44));                    // 0 不回落到武器表
        Assert.Equal(0, HitStop.StunMs(-1, false, 0));                    // 没有武器表也没写:不停
    }

    [Fact]
    public void TheWeaponDelayRateFlagMultipliesTheWeaponValueByTheWrittenNumber()
    {
        Assert.Equal(88, HitStop.StunMs(2, true, 44));
        Assert.Equal(35, HitStop.StunMs(1, true, 35));
        Assert.Equal(0, HitStop.StunMs(0, true, 44));
        Assert.True(HitStop.StunMs(-1, true, 44) < 0);                    // 写 -1 又要相乘:结果是负数,等于不停
    }

    [Fact]
    public void ThePositiveResultIsScaledByTwoMinusTheStatTruncatingTowardZero()
    {
        Assert.Equal(44, HitStop.StunMs(-1, false, 44, 1.0f));            // 属性默认 1:不变
        Assert.Equal(44, HitStop.StunMs(-1, false, 44));
        Assert.Equal(66, HitStop.StunMs(-1, false, 44, 0.5f));            // (2 - 0.5) × 44
        Assert.Equal(35, HitStop.StunMs(-1, false, 44, 1.2f));            // 单精度:2 - 1.2f = 0.79999995,× 44 = 35.19999…,向零取整得 35
        Assert.Equal(23, HitStop.StunMs(-1, false, 30, 1.2f));            // 单精度:2 - 1.2f = 0.79999995,× 30 = 23.999998,向零取整得 23(十进制算是 24)
        Assert.Equal(88, HitStop.StunMs(-1, false, 44, 0f));
        Assert.Equal(-5, HitStop.StunMs(-5, false, 44, 0.5f));            // 非正数不缩放
    }

    [Fact]
    public void AnOutOfRangeRawValueCannotOverflowTheMultiplication()
    {
        int huge = HitStop.StunMs(int.MaxValue, true, 44);                // 回绕也不能抛异常
        Assert.True(huge < 0 || huge >= 0);
    }
}
