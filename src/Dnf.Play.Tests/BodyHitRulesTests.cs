namespace Dnf.Play.Tests;

/// <summary>
/// 身体命中一族(客户端状态 0x3c、0x3e、0x3f,证据 E-162、E-163)比单段配方多出的几条纯规则:横轴起滑、伤害倍率、定间隔重武装、按键接段、
/// 播完后的保持、关键帧起滑、线性位移、自动接续的段号上界。全是不碰主角对象的纯函数与配方校验,数字都是合成的、期望值手算(算式写在用例旁),
/// 倍率那组用单精度的独立脚本核过。每条规则至少一个反例与一个边界。
/// </summary>
public class BodyHitRulesTests
{
    // ------------------------------------------------------------------ 起滑:只看横轴按住的方向

    /// <summary>基础 (速度 200, 加速度 −400),"顺着按"的速度 350。</summary>
    private static (double Speed, double Accel) Held(int heldX, bool facingLeft, bool hasForward = true)
    {
        SkillBodyHitRules.HeldSlide(200, -400, hasForward, 350, heldX, facingLeft, out double speed, out double accel);
        return (speed, accel);
    }

    [Fact]
    public void WithoutAHorizontalKeyTheBaseSlideIsUsedWhateverTheFacing()
    {
        Assert.Equal((200.0, -400.0), Held(0, false));
        Assert.Equal((200.0, -400.0), Held(0, true));
    }

    [Fact]
    public void HoldingTowardsTheFacingReplacesOnlyTheSpeed()
    {
        Assert.Equal((350.0, -400.0), Held(1, false));        // 朝右按右:速度换成 350,加速度还是基础的 −400
        Assert.Equal((350.0, -400.0), Held(-1, true));        // 朝左按左
        Assert.Equal((350.0, -400.0), Held(7, false));        // 只看符号,按住的数值大小不管
        Assert.Equal((350.0, -400.0), Held(-30, true));
    }

    [Fact]
    public void HoldingAgainstTheFacingDoesNotSlideAtAll()
    {
        Assert.Equal((0.0, 0.0), Held(-1, false));
        Assert.Equal((0.0, 0.0), Held(1, true));
        Assert.Equal((0.0, 0.0), Held(-1, false, hasForward: false));    // 有没有"顺着按"的速度都一样
    }

    [Fact]
    public void WithoutAForwardSpeedHoldingForwardKeepsTheBaseSlide()
    {
        Assert.Equal((200.0, -400.0), Held(1, false, hasForward: false));
        Assert.Equal((200.0, -400.0), Held(-1, true, hasForward: false));
    }

    // ------------------------------------------------------------------ 伤害倍率:单精度乘、向零取整

    // 算法:倍率 = (float)percent × 0.01f;超出部分 = (int)(rate × 倍率 − 100.0)(向零取整);结果 = 超出部分 + 100。
    // 下面各行的倍率与乘积是独立的单精度脚本算出来的;"整数除法"一栏是 rate × percent / 100,不同的行说明单精度误差真的会改变结果。
    [Theory]
    [InlineData(150, 100, 150)]     // 倍率恰为 100%:1.0f 精确,结果与原值一致(边界)
    [InlineData(137, 100, 137)]
    [InlineData(180, 200, 360)]     // 倍率 2.0 精确:180 × 2 − 100 = 260,加回 100
    [InlineData(140, 450, 630)]     // 倍率 4.5:140 × 4.5 = 630
    [InlineData(50, 300, 150)]      // 倍率 3.0:150 − 100 = 50 → 150
    [InlineData(100, 0, 0)]         // 倍率 0:−100 + 100 = 0(边界)
    [InlineData(250, 120, 299)]     // 倍率 1.199999928:250 × 倍率 = 299.999982,减 100 取 199 → 299(整数除法会得 300)
    [InlineData(150, 70, 104)]      // 倍率 0.699999988:150 × 倍率 = 104.9999982,减 100 取 4 → 104(整数除法会得 105)
    [InlineData(130, 260, 337)]     // 倍率 2.599999905:130 × 倍率 = 337.99998765,减 100 取 237 → 337(整数除法会得 338)
    [InlineData(100, 70, 70)]       // 100 × 0.699999988 = 69.99999881,减 100 = −30.00000119,向零取整 −30 → 70(向下取整会得 69)
    [InlineData(90, 110, 100)]      // 90 × 1.100000024 = 99.00000216,减 100 = −0.99999784,向零取整 0 → 100(向下取整会得 99)
    public void TheStagePercentIsRescaledInSinglePrecisionAndTruncatedTowardsZero(int rate, int percent, int expected)
    {
        Assert.Equal(expected, SkillBodyHitRules.ScaleRate(rate, percent));
    }

    // ------------------------------------------------------------------ 定间隔重武装的计时器

    [Fact]
    public void TheClockFiresFirstAtItsStartAndNotBefore()
    {
        var clock = new SkillRearmClock(startMs: 100, intervalMs: 50, count: 2);
        Assert.False(clock.Poll(0));
        Assert.False(clock.Poll(99));                        // 起点前一毫秒还没响(边界)
        Assert.True(clock.Poll(100));                        // 恰在起点响第一次
        Assert.Equal(1, clock.Fired);
        Assert.False(clock.Poll(100));                       // 一次查询最多响一次,下一次要等一个间隔
        Assert.False(clock.Poll(149));
        Assert.True(clock.Poll(150));                        // 距上次响恰好一个间隔(边界)
        Assert.Equal(2, clock.Fired);
    }

    [Fact]
    public void TheClockStopsAfterItsCountAndAZeroOrNegativeCountNeverFires()
    {
        var clock = new SkillRearmClock(0, 10, 2);
        Assert.True(clock.Poll(0));
        Assert.True(clock.Poll(10));
        Assert.False(clock.Poll(1000));                      // 次数用完,再晚也不响
        Assert.Equal(2, clock.Fired);

        Assert.False(new SkillRearmClock(0, 10, 0).Poll(500));
        Assert.False(new SkillRearmClock(0, 10, -3).Poll(500));
        Assert.True(new SkillRearmClock(0, 10, 1).Poll(0));
        Assert.False(new SkillRearmClock(0, 10, 1).Poll(-1));    // 时间在起点之前(边界)
    }

    [Fact]
    public void ALateStepPushesTheFollowingFiresBackInsteadOfKeepingAFixedBeat()
    {
        // 起点 0、间隔 100、三次。第二次响在 130(晚了 30),下一次要等到 130 + 100 = 230;固定节拍的话会在 200 响
        var clock = new SkillRearmClock(0, 100, 3);
        Assert.True(clock.Poll(0));
        Assert.True(clock.Poll(130));
        Assert.False(clock.Poll(200));
        Assert.False(clock.Poll(229));
        Assert.True(clock.Poll(230));
        Assert.Equal(3, clock.Fired);
        Assert.False(clock.Poll(400));
    }

    [Fact]
    public void AZeroIntervalFiresOnEveryPollUntilTheCountIsUsed()
    {
        var clock = new SkillRearmClock(0, 0, 2);
        Assert.True(clock.Poll(5));
        Assert.True(clock.Poll(5));
        Assert.False(clock.Poll(5));
    }

    // ------------------------------------------------------------------ 按键接段

    private static SkillBodyHitSpec Chains(params SkillKeyChain[] chains)
    {
        return new SkillBodyHitSpec(chains: chains);
    }

    [Fact]
    public void AChainNeedsTheFrameThresholdAndOneOfItsKeys()
    {
        // 第 0 段播到第 5 帧起,按技能键接第 1 段
        SkillBodyHitSpec spec = Chains(new SkillKeyChain(0, 0, 1, 5, SkillChainKeys.SkillKey));
        Assert.Equal(1, SkillBodyHitRules.ChainTarget(spec, 0, 5, SkillChainKeys.SkillKey));    // 恰在门槛帧(边界)
        Assert.Equal(1, SkillBodyHitRules.ChainTarget(spec, 0, 9, SkillChainKeys.SkillKey));
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 0, 4, SkillChainKeys.SkillKey));   // 差一帧不行(边界)
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 0, 5, SkillChainKeys.Own));        // 不认的键
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 0, 5, SkillChainKeys.AttackKey));
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 0, 5, SkillChainKeys.None));
        Assert.Equal(1, SkillBodyHitRules.ChainTarget(spec, 0, 5, SkillChainKeys.Own | SkillChainKeys.SkillKey));   // 同时按了别的键,只要有一个认的就行
    }

    [Fact]
    public void AChainOnlyStartsFromTheStagesInItsRange()
    {
        // 第 0 到第 1 段都能接第 2 段,按自己的键或攻击键,从第 0 帧起
        SkillBodyHitSpec spec = Chains(new SkillKeyChain(0, 1, 2, 0, SkillChainKeys.Own | SkillChainKeys.AttackKey));
        Assert.Equal(2, SkillBodyHitRules.ChainTarget(spec, 0, 0, SkillChainKeys.Own));
        Assert.Equal(2, SkillBodyHitRules.ChainTarget(spec, 1, 0, SkillChainKeys.AttackKey));
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 1, 0, SkillChainKeys.SkillKey));
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 2, 0, SkillChainKeys.Own));        // 已经在第 2 段:范围之外(边界)
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(spec, 3, 5, SkillChainKeys.Own));
    }

    [Fact]
    public void TheFirstMatchingChainWinsAndNoSpecOrNoChainFindsNothing()
    {
        SkillBodyHitSpec both = Chains(new SkillKeyChain(0, 0, 1, 0, SkillChainKeys.Own), new SkillKeyChain(0, 0, 2, 0, SkillChainKeys.Own));
        Assert.Equal(1, SkillBodyHitRules.ChainTarget(both, 0, 0, SkillChainKeys.Own));
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(null, 0, 9, SkillChainKeys.Own));
        Assert.Equal(-1, SkillBodyHitRules.ChainTarget(new SkillBodyHitSpec(), 0, 9, SkillChainKeys.Own));
    }

    // ------------------------------------------------------------------ 播完后的保持

    [Fact]
    public void AHoldAfterTheAnimationAddsToTheAnimationLength()
    {
        Assert.Equal(600, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AfterAnimation, 400, 200));     // 400 + 200
        Assert.Equal(400, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AfterAnimation, 400, 0));       // 保持 0:到动画结束(边界)
        Assert.Equal(400, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AfterAnimation, 400, -50));     // 负数当 0,不会缩短动画
        Assert.Equal(900, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AfterAnimation, 400.9, 500));   // 动画时长取整数毫秒:400 + 500
    }

    [Fact]
    public void AnAtLeastHoldNeverShortensTheAnimation()
    {
        Assert.Equal(500, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AtLeast, 300, 500));            // 比动画长:取 500
        Assert.Equal(600, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AtLeast, 600, 500));            // 比动画短:动画为准
        Assert.Equal(500, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AtLeast, 500, 500));            // 相等(边界)
        Assert.Equal(400, SkillBodyHitRules.HoldUntilMs(SkillHoldKind.AtLeast, 400, -5));
    }

    // ------------------------------------------------------------------ 关键帧起滑

    [Fact]
    public void AScriptSlideIsSkippedOnlyWhenTheHeldDirectionIsExactlyBackwards()
    {
        Assert.True(SkillBodyHitRules.ScriptSlideStarts(0, false));        // 没按
        Assert.True(SkillBodyHitRules.ScriptSlideStarts(0, true));
        Assert.True(SkillBodyHitRules.ScriptSlideStarts(1, false));        // 朝右按右
        Assert.True(SkillBodyHitRules.ScriptSlideStarts(-1, true));        // 朝左按左
        Assert.False(SkillBodyHitRules.ScriptSlideStarts(-1, false));      // 朝右按左:反方向
        Assert.False(SkillBodyHitRules.ScriptSlideStarts(1, true));        // 朝左按右
        Assert.False(SkillBodyHitRules.ScriptSlideStarts(-9, false));      // 只看符号
        Assert.True(SkillBodyHitRules.ScriptSlideStarts(9, false));
    }

    // ------------------------------------------------------------------ 线性位移

    [Fact]
    public void TheTravelOffsetIsAClampedSinglePrecisionLerpTruncatedTowardsZero()
    {
        // 距离 100、总时长 400:偏移 = 100 × 已过 / 400,向零取整
        Assert.Equal(0, SkillBodyHitRules.TravelOffset(100, 400, 0));
        Assert.Equal(25, SkillBodyHitRules.TravelOffset(100, 400, 100));      // 25.0
        Assert.Equal(32, SkillBodyHitRules.TravelOffset(100, 400, 130));      // 32.5 → 32
        Assert.Equal(99, SkillBodyHitRules.TravelOffset(100, 400, 399));      // 99.75 → 99(边界:差一毫秒到头)
        Assert.Equal(100, SkillBodyHitRules.TravelOffset(100, 400, 400));     // 恰到总时长(边界)
        Assert.Equal(100, SkillBodyHitRules.TravelOffset(100, 400, 5000));    // 夹住,不会冲过头
        Assert.Equal(0, SkillBodyHitRules.TravelOffset(100, 400, -20));       // 负的已过时间当 0
    }

    [Fact]
    public void ANegativeDistanceTruncatesTowardsZeroAndAZeroDurationJumpsStraightToTheEnd()
    {
        Assert.Equal(-50, SkillBodyHitRules.TravelOffset(-100, 400, 200));    // −100 × 200 / 400 = −50
        Assert.Equal(-32, SkillBodyHitRules.TravelOffset(-100, 400, 130));    // −32.5 向零取整是 −32(向下取整会得 −33)
        Assert.Equal(100, SkillBodyHitRules.TravelOffset(100, 0, 0));         // 总时长 0:先判"已过 ≥ 总时长",一步到位且不除零(边界)
        Assert.Equal(100, SkillBodyHitRules.TravelOffset(100, 0, 10));
        Assert.Equal(7, SkillBodyHitRules.TravelOffset(7, 1, 1));
    }

    // ------------------------------------------------------------------ 自动接续的上界、只第 0 段起滑、动画起点折算

    [Fact]
    public void AutoChainIsBoundedByTheLastFromStageAndUnboundedWhenNegative()
    {
        var bounded0 = new SkillBodyHitSpec(autoChainLastFromStage: 0);
        Assert.True(SkillBodyHitRules.AutoChainAllowed(bounded0, 0));          // 上界本身还能接(边界)
        Assert.False(SkillBodyHitRules.AutoChainAllowed(bounded0, 1));         // 过了上界不接
        var bounded1 = new SkillBodyHitSpec(autoChainLastFromStage: 1);
        Assert.True(SkillBodyHitRules.AutoChainAllowed(bounded1, 1));
        Assert.False(SkillBodyHitRules.AutoChainAllowed(bounded1, 2));
        Assert.True(SkillBodyHitRules.AutoChainAllowed(new SkillBodyHitSpec(), 7));      // 不设上界(-1):每一段都能接
        Assert.True(SkillBodyHitRules.AutoChainAllowed(null, 7));                         // 没有身体命中规则的配方:旧行为
    }

    [Fact]
    public void OnlyTheFirstStageSlidesWhenTheSpecSaysSoAndEveryStageDoesOtherwise()
    {
        var first = new SkillBodyHitSpec(slideFirstStageOnly: true);
        Assert.True(SkillBodyHitRules.SlidesInStage(first, 0));
        Assert.False(SkillBodyHitRules.SlidesInStage(first, 1));
        Assert.False(SkillBodyHitRules.SlidesInStage(first, 2));
        var all = new SkillBodyHitSpec();
        Assert.True(SkillBodyHitRules.SlidesInStage(all, 1));
        Assert.True(SkillBodyHitRules.SlidesInStage(null, 1));
    }

    [Fact]
    public void TheFrameStartIsConvertedToWallClockByTheAttackAnimationRate()
    {
        Assert.Equal(200, SkillBodyHitRules.FrameStartWallMs(200, 1.0));
        Assert.Equal(100, SkillBodyHitRules.FrameStartWallMs(200, 2.0));       // 倍速 2:200 动画毫秒走 100 墙钟毫秒
        Assert.Equal(400, SkillBodyHitRules.FrameStartWallMs(200, 0.5));
        Assert.Equal(83, SkillBodyHitRules.FrameStartWallMs(250, 3.0));        // 83.33 向零取整
        Assert.Equal(0, SkillBodyHitRules.FrameStartWallMs(0, 1.5));           // 从第 0 帧起(边界)
        Assert.Equal(200, SkillBodyHitRules.FrameStartWallMs(200, 0.0));       // 倍率不为正按 1
        Assert.Equal(200, SkillBodyHitRules.FrameStartWallMs(200, -2.0));
    }

    // ------------------------------------------------------------------ 配方里的校验与查找

    [Fact]
    public void AKeyChainRejectsBadRangesBackwardTargetsNegativeFramesAndNoKeys()
    {
        Assert.Throws<ArgumentException>(() => new SkillKeyChain(-1, 0, 1, 0, SkillChainKeys.Own));       // 起始段为负
        Assert.Throws<ArgumentException>(() => new SkillKeyChain(1, 0, 2, 0, SkillChainKeys.Own));        // 范围倒过来
        Assert.Throws<ArgumentException>(() => new SkillKeyChain(0, 1, 1, 0, SkillChainKeys.Own));        // 目标段不在范围之后(边界)
        Assert.Throws<ArgumentException>(() => new SkillKeyChain(0, 0, 0, 0, SkillChainKeys.Own));
        Assert.Throws<ArgumentException>(() => new SkillKeyChain(0, 0, 1, -1, SkillChainKeys.Own));       // 帧号为负
        Assert.Throws<ArgumentException>(() => new SkillKeyChain(0, 0, 1, 0, SkillChainKeys.None));       // 一个键都不认
        var ok = new SkillKeyChain(0, 1, 2, 0, SkillChainKeys.Own);                                       // 帧号 0、目标恰在范围后一段(边界)
        Assert.Equal((0, 1, 2, 0, SkillChainKeys.Own), (ok.FromFirstStage, ok.FromLastStage, ok.ToStage, ok.FromFrame, ok.Keys));
    }

    [Fact]
    public void TheOtherRuleRecordsRejectNegativeStagesAndIndexesButAcceptZero()
    {
        Assert.Throws<ArgumentException>(() => new SkillRearmSpec(-1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new SkillRearmSpec(0, -1, 0, 0));
        Assert.Throws<ArgumentException>(() => new SkillRearmSpec(0, 0, -1, 0));
        Assert.Throws<ArgumentException>(() => new SkillRearmSpec(0, 0, 0, -1));
        var zero = new SkillRearmSpec(0, 0, 0, 0);
        Assert.Equal((0, 0, 0, 0), (zero.Stage, zero.StartFrame, zero.IntervalStaticIndex, zero.CountStaticIndex));
        var set = new SkillRearmSpec(2, 3, 4, 5);
        Assert.Equal((2, 3, 4, 5), (set.Stage, set.StartFrame, set.IntervalStaticIndex, set.CountStaticIndex));
        Assert.Throws<ArgumentException>(() => new SkillStageScale(-1, 0));
        Assert.Throws<ArgumentException>(() => new SkillStageScale(0, -1));
        Assert.Equal(0, new SkillStageScale(0, 0).PercentStaticIndex);
        Assert.Throws<ArgumentException>(() => new SkillStageHold(-1, SkillHoldKind.AtLeast, 0));
        Assert.Throws<ArgumentException>(() => new SkillStageHold(0, SkillHoldKind.AtLeast, -1));
        Assert.Equal(SkillHoldKind.AtLeast, new SkillStageHold(0, SkillHoldKind.AtLeast, 0).Kind);
        Assert.Throws<ArgumentException>(() => new SkillScriptSlide(-1, 1, 0, 0));
        Assert.Throws<ArgumentException>(() => new SkillScriptSlide(0, 1, -1, 0));
        Assert.Throws<ArgumentException>(() => new SkillScriptSlide(0, 1, 0, -1));
        Assert.Equal(1, new SkillScriptSlide(0, 1, 0, 0).Flag);
        Assert.Throws<ArgumentException>(() => new SkillTravelSpec(-1, 0, 0));
        Assert.Throws<ArgumentException>(() => new SkillTravelSpec(0, -1, 0));
        Assert.Throws<ArgumentException>(() => new SkillTravelSpec(0, 0, -1));
        Assert.Equal(0, new SkillTravelSpec(0, 0, 0).DistanceStaticIndex);
    }

    [Fact]
    public void ALookupFindsTheRuleOfThatStageOnly()
    {
        var scale = new SkillStageScale(1, 3);
        var rearm = new SkillRearmSpec(2, 0, 1, 2);
        var hold = new SkillStageHold(0, SkillHoldKind.AtLeast, 4);
        var slide = new SkillScriptSlide(1, 7, 0, 1);
        var travel = new SkillTravelSpec(2, 5, 4);
        var spec = new SkillBodyHitSpec(scales: new[] { scale }, rearms: new[] { rearm }, holds: new[] { hold }, scriptSlides: new[] { slide }, travels: new[] { travel });
        Assert.Same(scale, spec.ScaleOf(1));
        Assert.Null(spec.ScaleOf(0));
        Assert.Null(spec.ScaleOf(2));
        Assert.Same(rearm, spec.RearmOf(2));
        Assert.Null(spec.RearmOf(1));
        Assert.Same(hold, spec.HoldOf(0));
        Assert.Null(spec.HoldOf(1));
        Assert.Same(travel, spec.TravelOf(2));
        Assert.Null(spec.TravelOf(0));
        Assert.Same(slide, spec.ScriptSlideAt(1, 7));
        Assert.Null(spec.ScriptSlideAt(1, 8));        // 段对、标志不对
        Assert.Null(spec.ScriptSlideAt(0, 7));        // 标志对、段不对
    }

    private static SkillStageSpec Stage(int motion)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
    }

    private static SkillRecipe Recipe(int stages, SkillBodyHitSpec bodyHit)
    {
        var list = new List<SkillStageSpec>();
        for (int i = 0; i < stages; i++)
        {
            list.Add(Stage(2 + i));
        }
        return new SkillRecipe(901, list, -1, -1, -1, -1, int.MaxValue, bodyHit: bodyHit);
    }

    [Fact]
    public void ARecipeChecksEveryStageNumberTheBodyHitRulesRefer()
    {
        // 两段的配方:段号 0、1 合法;2 越界(边界:恰等于段数)
        Assert.NotNull(Recipe(2, new SkillBodyHitSpec(scales: new[] { new SkillStageScale(1, 0) })));
        Assert.Throws<ArgumentException>(() => Recipe(2, new SkillBodyHitSpec(scales: new[] { new SkillStageScale(2, 0) })));
        Assert.Throws<ArgumentException>(() => Recipe(2, new SkillBodyHitSpec(rearms: new[] { new SkillRearmSpec(2, 0, 0, 0) })));
        Assert.Throws<ArgumentException>(() => Recipe(2, new SkillBodyHitSpec(holds: new[] { new SkillStageHold(2, SkillHoldKind.AtLeast, 0) })));
        Assert.Throws<ArgumentException>(() => Recipe(2, new SkillBodyHitSpec(scriptSlides: new[] { new SkillScriptSlide(2, 1, 0, 0) })));
        Assert.Throws<ArgumentException>(() => Recipe(2, new SkillBodyHitSpec(travels: new[] { new SkillTravelSpec(2, 0, 0) })));
        Assert.Throws<ArgumentException>(() => Recipe(2, new SkillBodyHitSpec(chains: new[] { new SkillKeyChain(0, 0, 2, 0, SkillChainKeys.Own) })));   // 目标段越界
        Assert.NotNull(Recipe(3, new SkillBodyHitSpec(chains: new[] { new SkillKeyChain(0, 1, 2, 0, SkillChainKeys.Own) })));                           // 三段里目标是最后一段:合法
    }

    [Fact]
    public void ARecipeWithoutBodyHitRulesHasNoneAndKeepsTheOneItWasGiven()
    {
        Assert.Null(Recipe(2, null).BodyHit);
        var spec = new SkillBodyHitSpec(horizontalHeldSlide: true);
        Assert.Same(spec, Recipe(2, spec).BodyHit);
        Assert.True(spec.HorizontalHeldSlide);
        Assert.False(new SkillBodyHitSpec().HorizontalHeldSlide);
        Assert.False(new SkillBodyHitSpec().SlideFirstStageOnly);
        Assert.Equal(-1, new SkillBodyHitSpec().AutoChainLastFromStage);
        Assert.Empty(new SkillBodyHitSpec().Chains);
    }

    // ------------------------------------------------------------------ 默认配方表

    [Fact]
    public void TheDefaultTableGivesBodyHitRulesToExactlyThreeRecipesAndTheirReferencesStayInRange()
    {
        int withRules = 0;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.BodyHit == null)
            {
                continue;
            }
            withRules++;
            Assert.True(HeroSkills.IsImplemented(r.SkillId));
            // 规则里引用的段号都在配方的段数里;重武装与保持的下标非负;每个段最多一条同类规则
            foreach (SkillRearmSpec x in r.BodyHit.Rearms) { Assert.InRange(x.Stage, 0, r.Stages.Count - 1); }
            foreach (SkillStageHold x in r.BodyHit.Holds) { Assert.InRange(x.Stage, 0, r.Stages.Count - 1); }
            foreach (SkillTravelSpec x in r.BodyHit.Travels) { Assert.InRange(x.Stage, 0, r.Stages.Count - 1); }
            foreach (SkillKeyChain x in r.BodyHit.Chains) { Assert.InRange(x.ToStage, 1, r.Stages.Count - 1); }
            Assert.Equal(r.BodyHit.Rearms.Count, r.BodyHit.Rearms.Select(x => x.Stage).Distinct().Count());
            Assert.Equal(r.BodyHit.Holds.Count, r.BodyHit.Holds.Select(x => x.Stage).Distinct().Count());
            Assert.Equal(r.BodyHit.Travels.Count, r.BodyHit.Travels.Select(x => x.Stage).Distinct().Count());
        }
        Assert.Equal(3, withRules);
    }
}
