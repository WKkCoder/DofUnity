namespace Dnf.Game.Tests;

/// <summary>
/// 宠物成长与饱食的纯计算:饱食随时间的结算(消耗与回补、上下限、时钟倒退)、饿死的边界、加经验的加成算法与截断饱和。
/// 数值全是手算的合成取值;引擎常量那一组测试把写死的字面量钉住,免得被悄悄改掉。
/// </summary>
public class PetRulesTests
{
    [Fact]
    public void EngineLiteralsArePinned()
    {
        Assert.Equal(100f, PetRules.MaxStomach);
        Assert.Equal(60f, PetRules.DrainSecondsPerPoint);
        Assert.Equal(360f, PetRules.ChargeSecondsPerPoint);
        Assert.Equal(30f, PetRules.FeedPoints);
        Assert.Equal(1f, PetRules.StarvedBelow);
        Assert.Equal(1f, PetRules.RevivedStomach);
        Assert.Equal(100f, PetRules.GrowKindStomach);
        Assert.Equal(100, PetRules.HatchStomach);
        Assert.Equal(1, PetRules.StartLevel);
        Assert.Equal(100f, PetRules.ExpPercentDivisor);
        Assert.Equal(99, PetRules.ExpTableEntries);
        Assert.Equal(25, PetRules.DefaultGrowItemsPerLevel);
        Assert.Equal(60.0, PetRules.DeathCheckDelayFactor);
        Assert.Equal(1, (int)PetState.Draining);
        Assert.Equal(2, (int)PetState.Stored);
        Assert.Equal(3, (int)PetState.Charging);
    }

    // ------------------------------------------------------------------ 消耗

    [Theory]
    [InlineData(100f, 0, 100f)]
    [InlineData(100f, 30, 99.5f)]
    [InlineData(100f, 60, 99f)]
    [InlineData(100f, 600, 90f)]
    [InlineData(50f, 3000, 0f)]
    public void DrainingLosesOnePointPerSixtySeconds(float start, long seconds, float expected)
    {
        Assert.Equal(expected, PetRules.Settle(start, seconds, PetState.Draining));
    }

    [Fact]
    public void DrainingKeepsTheFractionalPart()
    {
        float v = PetRules.Settle(100f, 59, PetState.Draining);
        Assert.InRange(v, 99.016f, 99.017f);
        Assert.Equal(99, (int)v);
    }

    [Theory]
    [InlineData(1f, 60)]
    [InlineData(1f, 61)]
    [InlineData(1f, 6000)]
    [InlineData(0f, 1)]
    [InlineData(0.5f, 3600)]
    public void DrainingNeverGoesBelowZero(float start, long seconds)
    {
        float v = PetRules.Settle(start, seconds, PetState.Draining);
        Assert.Equal(0f, v);
        Assert.False(float.IsNegative(v));
    }

    [Fact]
    public void DrainingForAnAbsurdTimeStillStopsAtZero()
    {
        Assert.Equal(0f, PetRules.Settle(50f, long.MaxValue / 2, PetState.Draining));
        Assert.Equal(0f, PetRules.Settle(50f, long.MaxValue, PetState.Draining));
    }

    // ------------------------------------------------------------------ 回补

    [Theory]
    [InlineData(50f, 360, PetState.Stored, 51f)]
    [InlineData(50f, 360, PetState.Charging, 51f)]
    [InlineData(50f, 180, PetState.Stored, 50.5f)]
    [InlineData(50f, 0, PetState.Charging, 50f)]
    [InlineData(10f, 3600, PetState.Charging, 20f)]
    public void ChargingGainsOnePointPerThreeHundredSixtySeconds(float start, long seconds, PetState state, float expected)
    {
        Assert.Equal(expected, PetRules.Settle(start, seconds, state));
    }

    [Theory]
    [InlineData(99f, 360, 100f)]
    [InlineData(99f, 720, 100f)]
    [InlineData(100f, 1, 100f)]
    [InlineData(100f, 100000, 100f)]
    [InlineData(0f, long.MaxValue, 100f)]
    public void ChargingNeverGoesAboveOneHundred(float start, long seconds, float expected)
    {
        Assert.Equal(expected, PetRules.Settle(start, seconds, PetState.Charging));
        Assert.Equal(expected, PetRules.Settle(start, seconds, PetState.Stored));
    }

    // ------------------------------------------------------------------ 时钟与状态

    [Theory]
    [InlineData(PetState.Draining)]
    [InlineData(PetState.Stored)]
    [InlineData(PetState.Charging)]
    public void AClockGoingBackwardsCountsAsZeroSeconds(PetState state)
    {
        Assert.Equal(50f, PetRules.Settle(50f, -1, state));
        Assert.Equal(50f, PetRules.Settle(50f, -1000000, state));
        Assert.Equal(50f, PetRules.Settle(50f, long.MinValue, state));
        Assert.Equal(100f, PetRules.Settle(100f, -5, state));
        Assert.Equal(0f, PetRules.Settle(0f, -5, state));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void AnUnknownStateChangesNothing(int state)
    {
        Assert.Equal(42f, PetRules.Settle(42f, 100000, (PetState)state));
    }

    // ------------------------------------------------------------------ 饿死

    [Theory]
    [InlineData(-1f, true)]
    [InlineData(0f, true)]
    [InlineData(0.5f, true)]
    [InlineData(0.99999f, true)]
    [InlineData(1f, false)]
    [InlineData(1.0001f, false)]
    [InlineData(50f, false)]
    [InlineData(100f, false)]
    public void StarvedIsStrictlyBelowOne(float stomach, bool starved)
    {
        Assert.Equal(starved, PetRules.IsStarved(stomach));
    }

    // ------------------------------------------------------------------ 经验

    [Theory]
    [InlineData(100, 0f, 100L)]
    [InlineData(100, 50f, 150L)]
    [InlineData(10, 25f, 12L)]
    [InlineData(7, 50f, 10L)]
    [InlineData(1, 99.9f, 1L)]
    [InlineData(1, 100f, 2L)]
    [InlineData(0, 50f, 0L)]
    [InlineData(100, -50f, 50L)]
    [InlineData(10, -500f, -40L)]
    public void ExpGainIsTheBaseAmountPlusItsTruncatedBonusShare(int amount, float bonus, long expected)
    {
        Assert.Equal(expected, PetRules.ExpGain(amount, bonus));
    }

    [Fact]
    public void ExpGainSaturatesInsteadOfWrapping()
    {
        Assert.Equal(2L * int.MaxValue, PetRules.ExpGain(int.MaxValue, 100f));
        Assert.Equal(int.MaxValue + 1_000_000_000L, PetRules.ExpGain(1_000_000_000, 1000f));
        Assert.Equal(int.MinValue + 1000L, PetRules.ExpGain(1000, -1e9f));
    }

    [Fact]
    public void TruncationGoesTowardZeroAndSaturatesAtTheIntRange()
    {
        Assert.Equal(2L, PetRules.TruncateSaturating(2.9f));
        Assert.Equal(-2L, PetRules.TruncateSaturating(-2.9f));
        Assert.Equal(0L, PetRules.TruncateSaturating(0.99f));
        Assert.Equal(0L, PetRules.TruncateSaturating(float.NaN));
        Assert.Equal((long)int.MaxValue, PetRules.TruncateSaturating(2147483648f));
        Assert.Equal((long)int.MaxValue, PetRules.TruncateSaturating(float.PositiveInfinity));
        Assert.Equal((long)int.MinValue, PetRules.TruncateSaturating(-2147483648f));
        Assert.Equal((long)int.MinValue, PetRules.TruncateSaturating(float.NegativeInfinity));
        Assert.Equal(2147483520L, PetRules.TruncateSaturating(2147483520f));
    }

    // ------------------------------------------------------------------ 死亡检查延迟

    [Theory]
    [InlineData(100f, 6000)]
    [InlineData(50f, 3000)]
    [InlineData(1.5f, 90)]
    [InlineData(0.5f, 30)]
    [InlineData(0f, 0)]
    [InlineData(1f, 60)]
    [InlineData(1.995f, 119)]   // 119.7:向零取整是 119,四舍五入会是 120
    [InlineData(2.999f, 179)]   // 179.94:同上
    public void TheDeathCheckDelayIsTheStomachTimesSixtyTruncated(float stomach, int expected)
    {
        Assert.Equal(expected, PetRules.DeathCheckDelay(stomach));
    }
}
