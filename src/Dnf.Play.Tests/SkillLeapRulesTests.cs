namespace Dnf.Play.Tests;

/// <summary>
/// 脚本前冲(客户端状态 0x2d 的模式 1)的纯规则:落地时刻、水平偏移、高度弧、规格校验。期望值全部手算(算式写在注释里),不抄实现的输出:
/// 水平 = 单精度 (距离 − 0) × 已过 / 落地时刻 向零取整;缓出弧 = 高度 × (2s − s²)(s = 已过 / 折半点),缓入弧是它的镜像,向零取整。
/// </summary>
public class SkillLeapRulesTests
{
    // ------------------------------------------------------------------ 落地时刻与"还在空中"

    [Fact]
    public void TheLandingTimeIsTheFramesBeforeTheLandingFrameConvertedToWallClock()
    {
        Assert.Equal(500, SkillLeapRules.LandMs(500, 1.0));
        Assert.Equal(250, SkillLeapRules.LandMs(500, 2.0));       // 倍速 2:500 动画毫秒走 250 墙钟毫秒
        Assert.Equal(1000, SkillLeapRules.LandMs(500, 0.5));
        Assert.Equal(166, SkillLeapRules.LandMs(500, 3.0));       // 166.67 向零取整
        Assert.Equal(500, SkillLeapRules.LandMs(500, 0.0));       // 倍率不为正按 1
        Assert.Equal(0, SkillLeapRules.LandMs(0, 2.0));           // 没有前面的帧(边界)
    }

    [Fact]
    public void TheHeroIsInTheAirOnlyStrictlyBeforeTheLandingTime()
    {
        Assert.True(SkillLeapRules.InFlight(0, 600));
        Assert.True(SkillLeapRules.InFlight(599, 600));           // 差一毫秒落地:还在空中(边界)
        Assert.False(SkillLeapRules.InFlight(600, 600));          // 恰到落地时刻就走落地块(边界)
        Assert.False(SkillLeapRules.InFlight(601, 600));
        Assert.False(SkillLeapRules.InFlight(0, 0));              // 落地时刻为 0:一步都不在空中
    }

    // ------------------------------------------------------------------ 水平偏移

    [Fact]
    public void TheOffsetIsAClampedSinglePrecisionLerpTruncatedTowardsZero()
    {
        Assert.Equal(0, SkillLeapRules.Offset(250, 0, 600));
        Assert.Equal(41, SkillLeapRules.Offset(250, 100, 600));     // 250 × 100 / 600 = 41.67 → 41
        Assert.Equal(125, SkillLeapRules.Offset(250, 300, 600));    // 正好一半
        Assert.Equal(249, SkillLeapRules.Offset(250, 599, 600));    // 249.58 → 249:差一毫秒落地时离满距离还差 1 像素
        Assert.Equal(250, SkillLeapRules.Offset(250, 600, 600));    // 夹住:到了落地时刻给满距离(房间里这一步不再摆位置)
        Assert.Equal(250, SkillLeapRules.Offset(250, 9000, 600));
        Assert.Equal(0, SkillLeapRules.Offset(250, -30, 600));      // 负的已过时间当 0
        Assert.Equal(-41, SkillLeapRules.Offset(-250, 100, 600));   // 距离为负:向零取整是 −41(向下取整会得 −42)
    }

    // ------------------------------------------------------------------ 高度弧

    [Fact]
    public void TheHalfWayPointIsTheSignedHalvingOfTheLandingTime()
    {
        Assert.Equal(100, SkillLeapRules.HalfMs(200));
        Assert.Equal(100, SkillLeapRules.HalfMs(201));            // 奇数:向下(正数)
        Assert.Equal(0, SkillLeapRules.HalfMs(1));
        Assert.Equal(0, SkillLeapRules.HalfMs(0));
        // 负数(客户端指令序列 CDQ、SUB、SAR):−3 − (−1) = −2,右移一位 −1;直接右移一位会得 −2
        Assert.Equal(-1, SkillLeapRules.HalfMs(-3));
        Assert.Equal(-2, SkillLeapRules.HalfMs(-4));
    }

    [Fact]
    public void TheHeightRisesAlongAnEaseOutArcInTheFirstHalf()
    {
        // 最大高度 100、落地时刻 200 → 折半点 100;s = 已过 / 100,高度 = 100 × (2s − s²)
        Assert.Equal(0, SkillLeapRules.Height(100, 0, 200));
        Assert.Equal(43, SkillLeapRules.Height(100, 25, 200));      // s = 0.25:100 × 0.4375 = 43.75 → 43
        Assert.Equal(55, SkillLeapRules.Height(100, 33, 200));      // s = 0.33:100 × (0.66 − 0.1089) = 55.11 → 55
        Assert.Equal(93, SkillLeapRules.Height(100, 75, 200));      // s = 0.75:100 × 0.9375 = 93.75 → 93
        Assert.Equal(99, SkillLeapRules.Height(100, 99, 200));      // s = 0.99:100 × 0.9999 = 99.99 → 99(不会四舍五入到 100)
        Assert.Equal(0, SkillLeapRules.Height(100, -10, 200));      // 负的已过时间:落在起点
    }

    [Fact]
    public void TheHeightPeaksAtTheHalfWayPointAndFallsAlongTheMirrorArc()
    {
        Assert.Equal(100, SkillLeapRules.Height(100, 100, 200));    // 恰在折半点:后半段第 0 毫秒,取起点 = 最大高度(边界)
        // 镜像:折半点后第 k 毫秒的高度 = 折半点前第 k 毫秒的高度
        for (int k = 1; k < 100; k++)
        {
            Assert.Equal(SkillLeapRules.Height(100, 100 - k, 200), SkillLeapRules.Height(100, 100 + k, 200));
        }
        Assert.Equal(93, SkillLeapRules.Height(100, 125, 200));     // 与 75 对称
        Assert.Equal(1, SkillLeapRules.Height(100, 199, 200));      // 与 1 对称:1.99 → 1
        Assert.Equal(0, SkillLeapRules.Height(100, 200, 200));      // 落地:回到 0
        Assert.Equal(0, SkillLeapRules.Height(100, 5000, 200));
    }

    [Fact]
    public void AnOddLandingTimeMakesTheSecondHalfReachZeroOneMillisecondEarly()
    {
        // 落地时刻 201:折半点 100,两个半段的范围都是 100;后半段已过 − 100 = 100 时已到范围末尾(取终点 0),比落地早 1 毫秒
        Assert.Equal(1, SkillLeapRules.Height(100, 199, 201));      // 后半段 99 毫秒:镜像 1 毫秒处 = 1.99 → 1
        Assert.Equal(0, SkillLeapRules.Height(100, 200, 201));      // 落地前 1 毫秒就已是 0
        Assert.Equal(0, SkillLeapRules.Height(100, 201, 201));
        Assert.Equal(43, SkillLeapRules.Height(100, 25, 201));      // 前半段不受奇偶影响
    }

    [Fact]
    public void AZeroMaxHeightNeverLeavesTheGround()
    {
        for (int t = 0; t <= 200; t += 20)
        {
            Assert.Equal(0, SkillLeapRules.Height(0, t, 200));
        }
    }

    // ------------------------------------------------------------------ 规格与阶段拷贝

    [Fact]
    public void ALeapSpecNeedsStaticIndicesAndALandingFrameAfterTheFirstFrame()
    {
        Assert.Throws<ArgumentException>(() => new SkillLeapSpec(-1, 0, 5));
        Assert.Throws<ArgumentException>(() => new SkillLeapSpec(0, -1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillLeapSpec(0, 1, 0));    // 落在第 0 帧开头:没有空中段
        var leap = new SkillLeapSpec(0, 1, 1);                                           // 第 1 帧是最小的合法落地帧(边界)
        Assert.Equal((0, 1, 1), (leap.DistanceStaticIndex, leap.HeightStaticIndex, leap.LandFrame));
    }

    [Fact]
    public void AStageGetsItsLeapFromACopyAndKeepsItThroughTheOtherStageCopies()
    {
        var plain = new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
        var leap = new SkillLeapSpec(0, 1, 5);
        SkillStageSpec leaping = plain.WithLeap(leap);
        Assert.Null(plain.Leap);                                  // 原来的阶段不变
        Assert.Same(leap, leaping.Leap);
        Assert.Same(leap, leaping.WithoutSlide().Leap);           // 别的拷贝方法不丢前冲
        Assert.Same(leap, leaping.WithAutoAdvance().Leap);
        Assert.Throws<ArgumentNullException>(() => plain.WithLeap(null));
    }
}
