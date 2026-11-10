using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 脚本前冲(客户端状态 0x2d 的模式 1)的纯规则:还在空中的判断、水平偏移、高度弧,以及规格(落地块的事、撤特效、开始的声音)的校验与拷贝。期望值全部手算(算式写在注释里),不抄实现的输出:
/// 水平 = 单精度 (距离 − 0) × 已过 / 落地时刻 向零取整;缓出弧 = 高度 × (2s − s²)(s = 已过 / 折半点),缓入弧是它的镜像,向零取整。
/// </summary>
public class SkillLeapRulesTests
{
    // ------------------------------------------------------------------ "还在空中"(落地时刻的折算在房间测试里用主角的真实动画与倍率覆盖)

    [Fact]
    public void TheHeroIsInTheAirOnlyStrictlyBeforeTheLandingTime()
    {
        Assert.True(SkillLeapPlan.InFlight(0, 600));
        Assert.True(SkillLeapPlan.InFlight(599, 600));           // 差一毫秒落地:还在空中(边界)
        Assert.False(SkillLeapPlan.InFlight(600, 600));          // 恰到落地时刻就走落地块(边界)
        Assert.False(SkillLeapPlan.InFlight(601, 600));
        Assert.False(SkillLeapPlan.InFlight(0, 0));              // 落地时刻为 0:一步都不在空中
    }

    // ------------------------------------------------------------------ 水平偏移

    [Fact]
    public void TheOffsetIsAClampedSinglePrecisionLerpTruncatedTowardsZero()
    {
        Assert.Equal(0, SkillLeapPlan.OffsetAt(250, 0, 600));
        Assert.Equal(41, SkillLeapPlan.OffsetAt(250, 100, 600));     // 250 × 100 / 600 = 41.67 → 41
        Assert.Equal(125, SkillLeapPlan.OffsetAt(250, 300, 600));    // 正好一半
        Assert.Equal(249, SkillLeapPlan.OffsetAt(250, 599, 600));    // 249.58 → 249:差一毫秒落地时离满距离还差 1 像素
        Assert.Equal(250, SkillLeapPlan.OffsetAt(250, 600, 600));    // 夹住:到了落地时刻给满距离(房间里这一步不再摆位置)
        Assert.Equal(250, SkillLeapPlan.OffsetAt(250, 9000, 600));
        Assert.Equal(0, SkillLeapPlan.OffsetAt(250, -30, 600));      // 负的已过时间当 0
        Assert.Equal(-41, SkillLeapPlan.OffsetAt(-250, 100, 600));   // 距离为负:向零取整是 −41(向下取整会得 −42)
    }

    // ------------------------------------------------------------------ 高度弧

    [Fact]
    public void TheHalfWayPointIsTheSignedHalvingOfTheLandingTime()
    {
        Assert.Equal(100, SkillLeapPlan.HalfMs(200));
        Assert.Equal(100, SkillLeapPlan.HalfMs(201));            // 奇数:向下(正数)
        Assert.Equal(0, SkillLeapPlan.HalfMs(1));
        Assert.Equal(0, SkillLeapPlan.HalfMs(0));
        // 负数(客户端指令序列 CDQ、SUB、SAR):−3 − (−1) = −2,右移一位 −1;直接右移一位会得 −2
        Assert.Equal(-1, SkillLeapPlan.HalfMs(-3));
        Assert.Equal(-2, SkillLeapPlan.HalfMs(-4));
    }

    [Fact]
    public void TheHeightRisesAlongAnEaseOutArcInTheFirstHalf()
    {
        // 最大高度 100、落地时刻 200 → 折半点 100;s = 已过 / 100,高度 = 100 × (2s − s²)
        Assert.Equal(0, SkillLeapPlan.HeightAt(100, 0, 200));
        Assert.Equal(43, SkillLeapPlan.HeightAt(100, 25, 200));      // s = 0.25:100 × 0.4375 = 43.75 → 43
        Assert.Equal(55, SkillLeapPlan.HeightAt(100, 33, 200));      // s = 0.33:100 × (0.66 − 0.1089) = 55.11 → 55
        Assert.Equal(93, SkillLeapPlan.HeightAt(100, 75, 200));      // s = 0.75:100 × 0.9375 = 93.75 → 93
        Assert.Equal(99, SkillLeapPlan.HeightAt(100, 99, 200));      // s = 0.99:100 × 0.9999 = 99.99 → 99(不会四舍五入到 100)
        Assert.Equal(0, SkillLeapPlan.HeightAt(100, -10, 200));      // 负的已过时间:落在起点
    }

    [Fact]
    public void TheHeightPeaksAtTheHalfWayPointAndFallsAlongTheMirrorArc()
    {
        Assert.Equal(100, SkillLeapPlan.HeightAt(100, 100, 200));    // 恰在折半点:后半段第 0 毫秒,取起点 = 最大高度(边界)
        // 镜像:折半点后第 k 毫秒的高度 = 折半点前第 k 毫秒的高度
        for (int k = 1; k < 100; k++)
        {
            Assert.Equal(SkillLeapPlan.HeightAt(100, 100 - k, 200), SkillLeapPlan.HeightAt(100, 100 + k, 200));
        }
        Assert.Equal(93, SkillLeapPlan.HeightAt(100, 125, 200));     // 与 75 对称
        Assert.Equal(1, SkillLeapPlan.HeightAt(100, 199, 200));      // 与 1 对称:1.99 → 1
        Assert.Equal(0, SkillLeapPlan.HeightAt(100, 200, 200));      // 落地:回到 0
        Assert.Equal(0, SkillLeapPlan.HeightAt(100, 5000, 200));
    }

    [Fact]
    public void AnOddLandingTimeMakesTheSecondHalfReachZeroOneMillisecondEarly()
    {
        // 落地时刻 201:折半点 100,两个半段的范围都是 100;后半段已过 − 100 = 100 时已到范围末尾(取终点 0),比落地早 1 毫秒
        Assert.Equal(1, SkillLeapPlan.HeightAt(100, 199, 201));      // 后半段 99 毫秒:镜像 1 毫秒处 = 1.99 → 1
        Assert.Equal(0, SkillLeapPlan.HeightAt(100, 200, 201));      // 落地前 1 毫秒就已是 0
        Assert.Equal(0, SkillLeapPlan.HeightAt(100, 201, 201));
        Assert.Equal(43, SkillLeapPlan.HeightAt(100, 25, 201));      // 前半段不受奇偶影响
    }

    [Fact]
    public void AZeroMaxHeightNeverLeavesTheGround()
    {
        for (int t = 0; t <= 200; t += 20)
        {
            Assert.Equal(0, SkillLeapPlan.HeightAt(0, t, 200));
        }
    }

    // ------------------------------------------------------------------ 计划

    [Fact]
    public void ALeapPlanNeedsALandingFrameAfterTheFirstFrameAndNeverHasANegativeHeight()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillLeapPlan(100, 50, 0));    // 落在第 0 帧开头:没有空中段
        var plan = new SkillLeapPlan(-100, 50, 1);                                         // 第 1 帧是最小的合法落地帧(边界);距离可以为负
        Assert.Equal((-100, 50, 1), (plan.Distance, plan.Height, plan.LandFrame));
        Assert.Equal(0, new SkillLeapPlan(100, -30, 2).Height);                            // 数据里的负高度(客户端不夹)夹成 0,免得主角钻到地下
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
        Assert.Null(leap.Landing);
    }

    [Fact]
    public void ALandingDescribesTheShakeSoundsEffectsAndTheTrimRuleAndValidatesThem()
    {
        var trim = new SkillEffectTrim(watchIndex: 4, frame: 5, removeIndices: new[] { 4, 5 });
        var landing = new SkillLeapLanding(5, 300, new[] { "A", "B" }, new[] { new SkillLandingEffect(4), new SkillLandingEffect(6, forward: 121, down: 10) }, trim);
        Assert.Equal((5, 300), (landing.ShakeStrength, landing.ShakeMs));
        Assert.Equal(new[] { "A", "B" }, landing.Sounds.ToArray());
        Assert.Equal(new[] { 4, 6 }, landing.Effects.Select(e => e.Index).ToArray());
        Assert.Same(trim, landing.Trim);
        Assert.True(landing.Effects[0].Follows);                         // 没有偏移的特效跟着主角走
        Assert.False(landing.Effects[1].Follows);                        // 带偏移的摆在落地点
        Assert.Equal((121, 10), (landing.Effects[1].Forward, landing.Effects[1].Down));
        Assert.False(new SkillLandingEffect(1, forward: 0, down: 3).Follows);   // 只要有一个偏移不为 0 就不跟

        var bare = new SkillLeapLanding(0, 0, null, null);               // 什么都不办的落地块是合法的
        Assert.Empty(bare.Sounds);
        Assert.Empty(bare.Effects);
        Assert.Null(bare.Trim);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillLeapLanding(-1, 300, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillLeapLanding(5, -1, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillLandingEffect(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillEffectTrim(-1, 5, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillEffectTrim(4, 0, null));      // 第 0 帧一开始就撤:没有意义
        Assert.Empty(new SkillEffectTrim(4, 1, null).RemoveIndices);
    }

    [Fact]
    public void ALeapGetsItsLandingFromACopyAndTheOriginalIsUntouched()
    {
        var leap = new SkillLeapSpec(0, 1, 5);
        var landing = new SkillLeapLanding(5, 300, null, null);
        SkillLeapSpec with = leap.WithLanding(landing);
        Assert.Null(leap.Landing);
        Assert.Same(landing, with.Landing);
        Assert.Equal((0, 1, 5), (with.DistanceStaticIndex, with.HeightStaticIndex, with.LandFrame));
        Assert.Throws<ArgumentNullException>(() => leap.WithLanding(null));
    }

    [Fact]
    public void AStageGetsItsLeapAndStartSoundsFromCopiesAndKeepsThemThroughTheOtherStageCopies()
    {
        var plain = new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
        var leap = new SkillLeapSpec(0, 1, 5);
        SkillStageSpec leaping = plain.WithLeap(leap);
        Assert.Null(plain.Leap);                                  // 原来的阶段不变
        Assert.Same(leap, leaping.Leap);
        Assert.Same(leap, leaping.WithoutSlide().Leap);           // 别的拷贝方法不丢前冲
        Assert.Same(leap, leaping.WithAutoAdvance().Leap);
        Assert.Throws<ArgumentNullException>(() => plain.WithLeap(null));

        Assert.Empty(plain.StartSounds);
        SkillStageSpec sounding = plain.WithStartSounds("A", "B");
        Assert.Empty(plain.StartSounds);
        Assert.Equal(new[] { "A", "B" }, sounding.StartSounds.ToArray());
        Assert.Equal(new[] { "A", "B" }, sounding.WithoutSlide().StartSounds.ToArray());
        Assert.Equal(new[] { "A", "B" }, sounding.WithLeap(leap).StartSounds.ToArray());     // 两个拷贝方法互不丢对方
        Assert.Same(leap, sounding.WithLeap(leap).WithStartSounds("C").Leap);
        Assert.Throws<ArgumentNullException>(() => plain.WithStartSounds(null));
    }

    [Fact]
    public void ASlotWithoutASkillDefinitionAndAStageWithoutALeapSpecGetNoLeapPlan()
    {
        // 读不到技能定义(没有 [static data] 可取)就没有计划:不抛异常,技能不走前冲;阶段没有前冲规格同样没有计划
        var slot = new HeroSkillSlot(SkillKeys.A, 901, 1, null);
        var plain = new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
        Assert.Null(HeroSkills.LeapPlanOf(slot, plain.WithLeap(new SkillLeapSpec(0, 1, 5))));
        Assert.Null(HeroSkills.LeapPlanOf(slot, plain));
    }
}
