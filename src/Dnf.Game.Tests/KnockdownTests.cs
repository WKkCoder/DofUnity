using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 倒地(DOWN)的开始处理与落地后的再请求(证据 E-082、E-083,静态读码)。体重取公式的中性点 10000
/// (此时缩放分母恰是 2100,便于手算),攻击信息的数全是合成的,只检验公式的形状、取整与随机抖动的区间,与原版数据无关。
/// </summary>
public class KnockdownTests
{
    private const int Neutral = 10000;

    private static KnockdownBody Fresh(int weight = Neutral) => new KnockdownBody(weight, Knockdown.Gravity, false, 0, 0, 0);

    private static KnockdownBody Downed(int stage, int type, double z = 0, int weight = Neutral) => new KnockdownBody(weight, Knockdown.Gravity, true, stage, type, z);

    [Theory]
    [InlineData(false, false, 0, 1)]
    [InlineData(false, true, 0, 0)]
    [InlineData(true, true, 0, 2)]
    [InlineData(true, false, 0, 3)]
    [InlineData(true, false, 1, 1)]   // 弹起只在输出二为 0 时生效
    [InlineData(true, true, 1, 0)]
    public void TheTypeIsTheBounceBitAndTheIgnoreWeightBit(bool bounce, bool ignoreWeight, int stage, int expected)
    {
        Assert.Equal(expected, Knockdown.TypeOf(bounce, ignoreWeight, stage));
    }

    [Fact]
    public void WeightScalingDividesByAWeightDependentDenominatorAndTruncates()
    {
        Assert.Equal(400, Knockdown.WeightScale(210, Neutral));          // 210 × 4000 ÷ 2100
        Assert.Equal(-400, Knockdown.WeightScale(-210, Neutral));
        Assert.Equal(0, Knockdown.WeightScale(0, Neutral));
        Assert.Equal(424, Knockdown.WeightScale(210, 0));                // 840000 ÷ 1979 = 424.4,向零取整
        Assert.Equal(378, Knockdown.WeightScale(210, 20000));            // 840000 ÷ 2221 = 378.2
        Assert.Equal(411, Knockdown.WeightScale(210, Neutral, 200));     // k 把体重折半:840000 ÷ 2039.5 = 411.9
        Assert.True(Knockdown.WeightScale(300, 30000) < Knockdown.WeightScale(300, 10000));   // 越重飞得越近
    }

    [Fact]
    public void TheJitterInterpolatesInSingleFloatAndTruncatesTowardsZero()
    {
        Assert.Equal(150, Knockdown.Lerp(100, 200, 500, 999));           // 100 + 100 × 500 ÷ 999 = 150.05
        Assert.Equal(100, Knockdown.Lerp(100, 200, 0, 999));
        Assert.Equal(200, Knockdown.Lerp(100, 200, 999, 999));
        Assert.Equal(100, Knockdown.Lerp(100, 200, -5, 999));            // r ≤ 0 取 a
        Assert.Equal(200, Knockdown.Lerp(100, 200, 5000, 999));          // r ≥ m 取 b
        Assert.Equal(2, Knockdown.Lerp(0, 5, 1, 2));                     // 2.5 → 2
        Assert.Equal(-2, Knockdown.Lerp(-5, 0, 1, 2));                   // -2.5 → -2
        Assert.Equal(149, Knockdown.Lerp(200, 100, 500, 999));           // 反向区间:149.95 → 149
    }

    [Fact]
    public void TheVerticalSpeedIsTheSquareRootOfTwoGravityTimesHeightWithTheSignOfTheHeight()
    {
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 128), Knockdown.ZSpeed(128), 2);
        Assert.Equal(-Math.Sqrt(2.0 * 1500 * 128), Knockdown.ZSpeed(-128), 2);
        Assert.Equal(0.0, Knockdown.ZSpeed(0));
        Assert.Equal(Math.Sqrt(2.0 * 800 * 50), Knockdown.ZSpeed(50, 800), 2);
    }

    [Fact]
    public void TypeZeroUsesThePushAndTheLiftAsTheyAreHalvesThePushAndJittersBoth()
    {
        // 水平 302 ÷ 2 = 151 → [0.8, 1.2] = [120, 181];高度 101(原值当高度)→ [0.9, 1.1] = [90, 111]
        var request = new KnockdownRequest(0, 0, 302, 101);
        KnockdownLaunch low = Knockdown.Begin(request, Fresh(), 0, 0);
        KnockdownLaunch high = Knockdown.Begin(request, Fresh(), 999, 999);
        Assert.Equal((0, 0, 120, 90), (low.Stage, low.Type, low.Horizontal, low.Height));
        Assert.Equal((181, 111), (high.Horizontal, high.Height));
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 90), low.ZSpeed, 2);
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 111), high.ZSpeed, 2);
        KnockdownLaunch mid = Knockdown.Begin(request, Fresh(), 500, 500);
        Assert.Equal((150, 100), (mid.Horizontal, mid.Height));          // 120 + 61 × 500 ÷ 999 = 150.5;90 + 21 × 500 ÷ 999 = 100.5
        Assert.False(low.StoresBounce);
        Assert.False(low.FlipSide);
        Assert.False(low.Continuing);
    }

    [Fact]
    public void TheWeightScaledTypesTurnTheLiftIntoAHeightBySquaringIt()
    {
        // 推 212 → 403 → 一半 201 → [160, 241];抬升 215 → 409 → 高度 409² ÷ 3000 = 55 → [49, 60]
        var request = new KnockdownRequest(0, 1, 212, 215);
        KnockdownLaunch low = Knockdown.Begin(request, Fresh(), 0, 0);
        KnockdownLaunch high = Knockdown.Begin(request, Fresh(), 999, 999);
        Assert.Equal((160, 49), (low.Horizontal, low.Height));
        Assert.Equal((241, 60), (high.Horizontal, high.Height));
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 49), low.ZSpeed, 2);
    }

    [Fact]
    public void AHeavierBodyFliesLowerAndShorterThanALightOneForTheSameHit()
    {
        var request = new KnockdownRequest(0, 1, 212, 215);
        KnockdownLaunch light = Knockdown.Begin(request, Fresh(0), 500, 500);
        KnockdownLaunch neutral = Knockdown.Begin(request, Fresh(Neutral), 500, 500);
        KnockdownLaunch heavy = Knockdown.Begin(request, Fresh(40000), 500, 500);
        Assert.True(light.Height > neutral.Height && neutral.Height > heavy.Height);
        Assert.True(light.Horizontal > neutral.Horizontal && neutral.Horizontal > heavy.Horizontal);
        Assert.True(light.ZSpeed > neutral.ZSpeed && neutral.ZSpeed > heavy.ZSpeed);
        // 类型 0(不受体重影响):同一击对谁都一样
        var ignore = new KnockdownRequest(0, 0, 212, 215);
        Assert.Equal(Knockdown.Begin(ignore, Fresh(0), 500, 500).Height, Knockdown.Begin(ignore, Fresh(40000), 500, 500).Height);
    }

    [Fact]
    public void ABounceSlamsFirstAndKeepsThePushAndTheLiftForTheSecondArc()
    {
        var request = new KnockdownRequest(0, 3, 212, 215);
        KnockdownLaunch slam = Knockdown.Begin(request, Fresh(), 0, 0);
        Assert.True(slam.StoresBounce);
        Assert.Equal((215, 212), (slam.StoredLift, slam.StoredPush));
        // 抬升换成 -400:缩放得 -761,高度 -(761² ÷ 3000) = -193 → [-212, -173],初速向下
        Assert.Equal(-173, slam.Height);
        Assert.Equal(-212, Knockdown.Begin(request, Fresh(), 0, 999).Height);
        Assert.True(slam.ZSpeed < 0);
        Assert.Equal(160, slam.Horizontal);                               // 推力照常缩放:212 → 403 → 201 → 160

        // 已经躺下(旧阶段 ≥ 2)再被打弹起:存下的抬升取一半(向零取整)
        KnockdownLaunch again = Knockdown.Begin(request, Downed(2, 0), 0, 0);
        Assert.Equal(107, again.StoredLift);
        Assert.Equal(212, again.StoredPush);
        Assert.Equal(215, Knockdown.Begin(request, Downed(1, 0), 0, 0).StoredLift);
        Assert.Equal(-107, Knockdown.Begin(new KnockdownRequest(0, 2, 0, -215), Downed(2, 0), 0, 0).StoredLift);

        // 类型 2(弹起且不受体重影响)的砸地阶段同样走缩放分支(开始处理里类型 1、2、3 同路),"不受体重影响"体现在第二次飞(类型 0,见 AfterLanding)
        KnockdownLaunch ignore = Knockdown.Begin(new KnockdownRequest(0, 2, 212, 215), Fresh(), 0, 0);
        Assert.True(ignore.StoresBounce);
        Assert.Equal(-173, ignore.Height);
        Assert.True(ignore.ZSpeed < 0);
    }

    [Fact]
    public void ALandingHopWithoutALiftGoesStraightToLying()
    {
        // 类型 0、阶段 1、抬升 ≤ 0、已在地面:阶段改成 2(不颠)
        KnockdownLaunch lying = Knockdown.Begin(new KnockdownRequest(1, 0, 151, 0), Downed(0, 1), 500, 500);
        Assert.Equal(2, lying.Stage);
        Assert.Equal((0, 0), (lying.Horizontal, lying.Height));
        Assert.Equal(0.0, lying.ZSpeed);

        // 抬升为正:颠一下,阶段仍是 1
        KnockdownLaunch hop = Knockdown.Begin(new KnockdownRequest(1, 0, 302, 51), Downed(0, 1), 0, 0);
        Assert.Equal(1, hop.Stage);
        Assert.Equal((120, 45), (hop.Horizontal, hop.Height));            // 51 → [45, 56]

        // 还在空中(z > 0)时不改阶段
        Assert.Equal(1, Knockdown.Begin(new KnockdownRequest(1, 0, 151, 0), Downed(0, 1, z: 5), 0, 0).Stage);
    }

    [Fact]
    public void AHopDuringAnEarlierDownAndTheLyingStageShrinkOrDropTheLaunch()
    {
        // 阶段 1 且已在倒地中(旧阶段 ≥ 1,例如躺着又挨一下):高度除以 4;刚落地的颠一下(旧阶段 0)不除
        var request = new KnockdownRequest(1, 0, 0, 101);
        Assert.Equal(22, Knockdown.Begin(request, Downed(1, 0), 0, 0).Height);      // 101 ÷ 4 = 25 → [22, 27]
        Assert.Equal(27, Knockdown.Begin(request, Downed(1, 0), 0, 999).Height);
        Assert.Equal(90, Knockdown.Begin(request, Downed(0, 1), 0, 0).Height);      // 101 → [90, 111]
        Assert.Equal(90, Knockdown.Begin(request, Fresh(), 0, 0).Height);

        // 阶段 2:水平与高度都是 0
        KnockdownLaunch lie = Knockdown.Begin(new KnockdownRequest(2, 1, 999, 999), Downed(1, 0), 500, 500);
        Assert.Equal((2, 0, 0), (lie.Stage, lie.Horizontal, lie.Height));
        Assert.Equal(0.0, lie.ZSpeed);

        // 类型 1 的阶段 1:缩放后的抬升为正、高度算出来不足 1 时按 1(随机抖动 [0, 1])
        var tiny = new KnockdownRequest(1, 1, 0, 10);
        Assert.Equal(1, Knockdown.Begin(tiny, Fresh(), 0, 999).Height);
        Assert.Equal(0, Knockdown.Begin(tiny, Fresh(), 0, 0).Height);
    }

    [Fact]
    public void ANegativePushFlipsTheSideUnlessTheFlightIsContinuing()
    {
        var request = new KnockdownRequest(0, 0, -302, 101);
        KnockdownLaunch first = Knockdown.Begin(request, Fresh(), 0, 0);
        Assert.True(first.FlipSide);
        Assert.Equal(120, first.Horizontal);                              // 取反后 302 ÷ 2 = 151 → [120, 181]

        // 接续飞行(落地后的再请求):不翻,负的水平量保留
        KnockdownLaunch next = Knockdown.Begin(new KnockdownRequest(1, 0, -302, 101), Downed(0, 1), 0, 0);
        Assert.True(next.Continuing);
        Assert.False(next.FlipSide);
        Assert.Equal(-120, next.Horizontal);                              // -151 → [-120, -181]
        Assert.Equal(-181, Knockdown.Begin(new KnockdownRequest(1, 0, -302, 101), Downed(0, 1), 999, 0).Horizontal);
    }

    [Fact]
    public void ContinuingMeansAlreadyDownOnTheGroundAndEitherBouncedOrMovingToALaterStage()
    {
        var req0 = new KnockdownRequest(0, 0, 0, 0);
        var req1 = new KnockdownRequest(1, 0, 0, 0);
        Assert.True(Knockdown.Begin(req0, Downed(0, 2), 0, 0).Continuing);                // 砸地之后再飞
        Assert.True(Knockdown.Begin(req0, Downed(0, 3), 0, 0).Continuing);
        Assert.True(Knockdown.Begin(req1, Downed(0, 1), 0, 0).Continuing);                // 落地后颠一下
        Assert.False(Knockdown.Begin(req1, Downed(0, 1, z: 4), 0, 0).Continuing);         // 在空中
        Assert.False(Knockdown.Begin(req1, Downed(2, 0), 0, 0).Continuing);               // 躺着又挨打:1 不大于 2
        Assert.False(Knockdown.Begin(req1, Fresh(), 0, 0).Continuing);                    // 之前没在倒地
        Assert.False(Knockdown.Begin(req0, Downed(0, 1), 0, 0).Continuing);               // 同阶段、非弹起
    }

    [Fact]
    public void AfterLandingTheMachineHopsFromTheMaxHeightOrReplaysTheBounce()
    {
        // 阶段 0、类型 0 或 1:颠一下 —— 阶段 1、类型 0、水平量沿用、高度 = 最高点 ÷ 5(整数)
        KnockdownRequest hop = Knockdown.AfterLanding(0, 1, 77, 99, 5, 6);
        Assert.Equal((1, 0, 77, 19), (hop.Stage, hop.Type, hop.PushAside, hop.LiftUp));
        Assert.Equal(1, Knockdown.AfterLanding(0, 0, 77, 99, 5, 6).Stage);

        // 弹起类:用存下的推力与抬升再飞一次,类型 2 → 0,类型 3 → 1
        KnockdownRequest second2 = Knockdown.AfterLanding(0, 2, 77, 99, 5, 6);
        Assert.Equal((0, 0, 5, 6), (second2.Stage, second2.Type, second2.PushAside, second2.LiftUp));
        KnockdownRequest second3 = Knockdown.AfterLanding(0, 3, 77, 99, 5, 6);
        Assert.Equal((0, 1, 5, 6), (second3.Stage, second3.Type, second3.PushAside, second3.LiftUp));

        // 阶段 1 落地:躺下
        KnockdownRequest lie = Knockdown.AfterLanding(1, 0, 77, 99, 5, 6);
        Assert.Equal((2, 0, 0, 0), (lie.Stage, lie.Type, lie.PushAside, lie.LiftUp));
    }

    [Fact]
    public void TheRequestChainEndsInLyingWhateverTheStart()
    {
        // 沿着"落地后的再请求"一路推下去:任何类型最终都会走到阶段 2,不会死循环
        for (int type = 0; type <= 3; type++)
        {
            KnockdownRequest r = new KnockdownRequest(0, type, 300, 300);
            KnockdownBody body = Fresh();
            int steps = 0;
            int stage = 0;
            int storedPush = 0, storedLift = 0, horizontal = 0;
            while (stage < 2 && steps++ < 10)
            {
                KnockdownLaunch l = Knockdown.Begin(r, body, 500, 500);
                if (l.StoresBounce)
                {
                    storedPush = l.StoredPush;
                    storedLift = l.StoredLift;
                }
                stage = l.Stage;
                horizontal = l.Horizontal;
                if (stage >= 2)
                {
                    break;
                }
                r = Knockdown.AfterLanding(stage, l.Type, horizontal, 100, storedPush, storedLift);
                body = Downed(stage, l.Type);
            }
            Assert.True(stage >= 2, "类型 " + type + " 没有躺下");
        }
    }
}
