using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 状态族扩展的纯规则(M104,证据 E-271、E-272,客户端状态 0x21、0x25、0x27 的配方部分):
/// 段首瞬移的落点(逐像素往朝向一侧探,到第一个站不住的点之前停下)、逐击连段的击数与"倒数第二击被锁"开关、
/// 四段模板按击数展开成 击数 × 2 段、每段的"不滑行 / 播完自己接 / 不许按键接"标记与配方的额外 [static data] 需求。
/// 全部是不读数据的纯函数;期望值都是手算:落点按"每步 1 像素、闭区间的矩形可走区域"数。
/// </summary>
public class SkillWarpRepeatSpecTests
{
    private static ScriptLine Ints(string tag, params int[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, list);
    }

    private static SkillDef Def(params int[] statics)
    {
        return new SkillDef(901, "skill/test/m104.skl", new[] { Ints("[static data]", statics) });
    }

    private static IWalkArea Area(int x, int y, int width, int height)
    {
        return new RectUnionArea(new List<WorldRect> { new WorldRect(x, y, width, height) });
    }

    private static SkillStageSpec Stage(int motion, bool strike = false)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: strike ? 1 : -1, damageRateColumn: strike ? 0 : -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
    }

    /// <summary>四段模板:非最后一击的起手 10 与打击 11、最后一击的起手 12 与打击 13。</summary>
    private static SkillRecipe Template(SkillRepeatSpec repeat, IEnumerable<SkillObjectSpec> objects = null)
    {
        return new SkillRecipe(901,
            new[]
            {
                Stage(10).WithAutoAdvance().WithoutSlide(),
                Stage(11, strike: true),
                Stage(12).WithAutoAdvance().WithoutSlide(),
                Stage(13, strike: true),
            },
            slideSpeedIndex: 2, slideAccelIndex: 3, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 2, slideFollowsHeldDirection: false, objects: objects)
            .WithRepeat(repeat);
    }

    // ------------------------------------------------------------------ 瞬移落点

    [Fact]
    public void TheWarpLandsADistanceAwayOnTheFacingSideInAnOpenArea()
    {
        IWalkArea open = Area(0, 0, 1000, 200);
        Assert.Equal(500.0, SkillWarpSpec.Destination(300, 100, 200, false, open));        // 朝右:300 + 200
        Assert.Equal(100.0, SkillWarpSpec.Destination(300, 100, 200, true, open));         // 朝左:300 − 200
    }

    [Fact]
    public void TheWarpStopsOnTheLastStandablePixelBeforeAWall()
    {
        // 可走区域到 x = 450(闭区间):301 到 450 共 150 个点站得住,451 站不住 → 停在 450
        Assert.Equal(450.0, SkillWarpSpec.Destination(300, 100, 200, false, Area(0, 0, 450, 200)));
        // 朝左:区域从 x = 150 起(闭区间),299 往下数到 150 站得住,149 站不住 → 停在 150
        Assert.Equal(150.0, SkillWarpSpec.Destination(300, 100, 200, true, Area(150, 0, 850, 200)));
    }

    [Fact]
    public void TheWarpStopsAtTheFirstBlockedPixelEvenWhenStandableGroundLiesBeyondIt()
    {
        // 两块可走区域中间有一道空隙(401 到 419 站不住):客户端逐像素走,碰到第一个站不住的点就停,不会越过空隙
        var gap = new RectUnionArea(new List<WorldRect> { new WorldRect(0, 0, 400, 200), new WorldRect(420, 0, 580, 200) });
        Assert.Equal(400.0, SkillWarpSpec.Destination(300, 100, 200, false, gap));          // 目标 500 本身站得住,但 401 站不住:停在 400
        Assert.Equal(420.0, SkillWarpSpec.Destination(430, 100, 200, true, gap));           // 朝左从 430 出发:到 420 站得住,419 站不住:停在 420
        Assert.Equal(350.0, SkillWarpSpec.Destination(300, 100, 50, false, gap));           // 对照:不碰空隙的短距离照常走满
    }

    [Fact]
    public void AWallExactlyAtTheDestinationDoesNotShortenTheWarpButOnePixelShortDoes()
    {
        Assert.Equal(500.0, SkillWarpSpec.Destination(300, 100, 200, false, Area(0, 0, 500, 200)));     // 目标点正好是最后一个站得住的点
        Assert.Equal(499.0, SkillWarpSpec.Destination(300, 100, 200, false, Area(0, 0, 499, 200)));     // 差一个像素:停在 499
    }

    [Fact]
    public void AWarpWhoseFirstStepIsBlockedStaysWhereItStartedAndSoDoesAZeroDistance()
    {
        Assert.Equal(300.0, SkillWarpSpec.Destination(300, 100, 200, false, Area(0, 0, 300, 200)));     // 第一步(301)就站不住:原地
        Assert.Equal(300.0, SkillWarpSpec.Destination(300, 100, 0, false, Area(0, 0, 1000, 200)));      // 距离 0:原地
        Assert.Equal(300.0, SkillWarpSpec.Destination(300, 100, 0, true, Area(0, 0, 1000, 200)));
    }

    [Fact]
    public void ANegativeDistanceWarpsTheOtherWayRoundButStillStopsAtTheWall()
    {
        IWalkArea open = Area(0, 0, 1000, 200);
        Assert.Equal(250.0, SkillWarpSpec.Destination(300, 100, -50, false, open));        // 朝右、距离 −50:往左 50
        Assert.Equal(350.0, SkillWarpSpec.Destination(300, 100, -50, true, open));         // 朝左、距离 −50:往右 50
        Assert.Equal(280.0, SkillWarpSpec.Destination(300, 100, -50, false, Area(280, 0, 720, 200)));   // 往左碰到 x = 280 的墙
    }

    [Fact]
    public void TheWarpChecksTheDepthCoordinateToo()
    {
        // 可走区域只覆盖 y 从 0 到 200;主角站在 y = 500 上,哪一步都站不住
        Assert.Equal(300.0, SkillWarpSpec.Destination(300, 500, 200, false, Area(0, 0, 1000, 200)));
        Assert.Equal(500.0, SkillWarpSpec.Destination(300, 200, 200, false, Area(0, 0, 1000, 200)));    // y = 200 是闭区间的边,站得住
    }

    [Fact]
    public void AnAreaThatSaysItIsUnrestrictedAndANullAreaNeverBlockTheWarp()
    {
        var unrestricted = new RectUnionArea(new List<WorldRect>());       // 没有矩形 = 不限制(Contains 对任何点都是假)
        Assert.True(unrestricted.IsUnrestricted);
        Assert.False(unrestricted.Contains(301, 100));
        Assert.Equal(500.0, SkillWarpSpec.Destination(300, 100, 200, false, unrestricted));
        Assert.Equal(500.0, SkillWarpSpec.Destination(300, 100, 200, false, null));
    }

    [Fact]
    public void AFractionalStartKeepsItsFractionAndTheWallIsTestedOnTheFractionalPoints()
    {
        // 起点 300.5,每步 1 像素:301.5、302.5、…、449.5 站得住(150 步里的前 149 步),450.5 超过 450 的墙 → 449.5
        Assert.Equal(449.5, SkillWarpSpec.Destination(300.5, 100, 200, false, Area(0, 0, 450, 200)));
        // 朝左同理:起点 300.5,299.5、…、150.5 站得住(150 步),149.5 在 150 的墙外 → 150.5
        Assert.Equal(150.5, SkillWarpSpec.Destination(300.5, 100, 200, true, Area(150, 0, 850, 200)));
    }

    [Fact]
    public void TheWarpSpecNeedsAStaticDataIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillWarpSpec(-1));
        Assert.Equal(0, new SkillWarpSpec(0).DistanceStaticIndex);
        Assert.Equal(7, new SkillWarpSpec(7).DistanceStaticIndex);
    }

    // ------------------------------------------------------------------ 击数与锁

    [Fact]
    public void TheHitCountIsTheStaticDataNumberButNeverBelowOne()
    {
        var repeat = new SkillRepeatSpec(countStaticIndex: 0);
        Assert.Equal(5, repeat.HitsOf(Def(5, 0, -3)));
        Assert.Equal(1, repeat.HitsOf(Def(1, 0, -3)));
        Assert.Equal(1, new SkillRepeatSpec(1).HitsOf(Def(5, 0, -3)));          // 数是 0:至少打一击
        Assert.Equal(1, new SkillRepeatSpec(2).HitsOf(Def(5, 0, -3)));          // 数是负的:至少打一击
        Assert.Equal(1, new SkillRepeatSpec(9).HitsOf(Def(5, 0, -3)));          // 下标越界读成 0:至少打一击
        Assert.Equal(1, repeat.HitsOf(null));                                   // 读不到技能定义:一击
    }

    [Fact]
    public void ThePenultimateLockIsOnForAnyNonZeroNumberAndOffForZeroNoIndexOrNoDefinition()
    {
        var on = new SkillRepeatSpec(0, lockPenultimateStaticIndex: 0);
        Assert.True(on.LocksPenultimate(Def(5, 0, -3)));                         // 5 ≠ 0
        Assert.True(new SkillRepeatSpec(0, 2).LocksPenultimate(Def(5, 0, -3)));  // −3 ≠ 0:负数也算开
        Assert.False(new SkillRepeatSpec(0, 1).LocksPenultimate(Def(5, 0, -3))); // 0:关
        Assert.False(new SkillRepeatSpec(0, 9).LocksPenultimate(Def(5, 0, -3))); // 越界读成 0:关
        Assert.False(new SkillRepeatSpec(0).LocksPenultimate(Def(5, 0, -3)));    // 没写锁的下标:关
        Assert.False(on.LocksPenultimate(null));                                 // 读不到技能定义:关
    }

    [Fact]
    public void TheRepeatSpecNeedsACountIndexButTheLockIndexIsOptional()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillRepeatSpec(-1));
        Assert.Equal(-1, new SkillRepeatSpec(0).LockPenultimateStaticIndex);
        Assert.Equal(4, new SkillRepeatSpec(0, 4).LockPenultimateStaticIndex);
        Assert.Equal(4, SkillRepeatSpec.TemplateStages);
    }

    // ------------------------------------------------------------------ 模板展开

    private static int[] Motions(SkillRecipe recipe)
    {
        return recipe.Stages.Select(st => st.MotionIndex).ToArray();
    }

    [Fact]
    public void TheTemplateExpandsToTwoStagesPerHitAndTheLastHitUsesTheSecondPair()
    {
        SkillRecipe t = Template(new SkillRepeatSpec(0));
        Assert.Equal(new[] { 12, 13 }, Motions(t.ExpandFor(Def(1))));                           // 一击:只有最后一击的一对
        Assert.Equal(new[] { 10, 11, 12, 13 }, Motions(t.ExpandFor(Def(2))));                   // 两击
        Assert.Equal(new[] { 10, 11, 10, 11, 12, 13 }, Motions(t.ExpandFor(Def(3))));           // 三击:非最后一击重复
        Assert.Equal(new[] { 10, 11, 10, 11, 10, 11, 12, 13 }, Motions(t.ExpandFor(Def(4))));
    }

    [Fact]
    public void ANonPositiveOrUnreadableHitCountExpandsToASingleHit()
    {
        SkillRecipe t = Template(new SkillRepeatSpec(0));
        Assert.Equal(new[] { 12, 13 }, Motions(t.ExpandFor(Def(0))));
        Assert.Equal(new[] { 12, 13 }, Motions(t.ExpandFor(Def(-2))));
        Assert.Equal(new[] { 12, 13 }, Motions(t.ExpandFor(Def())));                            // 没有 [static data]
        Assert.Equal(new[] { 12, 13 }, Motions(t.ExpandFor(null)));
    }

    [Fact]
    public void AnExpandedRecipeKeepsTheStageFlagsAndEverythingElseOfTheTemplate()
    {
        var obj = new SkillObjectSpec(9221, stage: 1, frame: 0, damageRateColumn: 1, damageFixedColumn: -1);
        SkillRecipe t = Template(new SkillRepeatSpec(0), new[] { obj });
        SkillRecipe e = t.ExpandFor(Def(3));
        Assert.Null(e.Repeat);
        Assert.Equal(6, e.Stages.Count);
        // 起手段:播完自己接、不滑行、没有自己的命中;打击段:有命中、滑行、按键接
        for (int i = 0; i < 6; i += 2)
        {
            Assert.True(e.Stages[i].AutoAdvance && !e.Stages[i].Slides && !e.Stages[i].HasOwnStrike);
            Assert.True(e.Stages[i + 1].HasOwnStrike && e.Stages[i + 1].Slides && !e.Stages[i + 1].AutoAdvance && e.Stages[i + 1].KeyChainOut);
        }
        Assert.Equal((901, 2, 2, 3, false), (e.SkillId, e.ChainFromFrame, e.SlideSpeedIndex, e.SlideAccelIndex, e.AutoChain));
        Assert.Same(obj, Assert.Single(e.Objects));
    }

    [Fact]
    public void ExpandingNeverChangesTheTemplateItself()
    {
        SkillRecipe t = Template(new SkillRepeatSpec(0, 1));
        SkillRecipe e = t.ExpandFor(Def(3, 1));
        Assert.NotNull(t.Repeat);
        Assert.Equal(new[] { 10, 11, 12, 13 }, Motions(t));
        Assert.True(t.Stages.All(st => st.KeyChainOut));                  // 锁只落在展开出来的拷贝上
        Assert.False(e.Stages[3].KeyChainOut);
    }

    [Fact]
    public void ARecipeWithoutARepeatExpandsToItselfWhateverTheDefinition()
    {
        var plain = new SkillRecipe(901, new[] { Stage(10), Stage(11, strike: true) }, -1, -1, -1, -1, 2);
        Assert.Same(plain, plain.ExpandFor(Def(3)));
        Assert.Same(plain, plain.ExpandFor(null));
    }

    [Fact]
    public void WithTheLockOnOnlyThePenultimateHitsStrikeStageStopsChainingByKey()
    {
        SkillRecipe t = Template(new SkillRepeatSpec(0, lockPenultimateStaticIndex: 1));
        // 三击、锁开(static[1] = 8):第 2 击(倒数第二击)的打击段是展开后的第 3 段
        SkillRecipe three = t.ExpandFor(Def(3, 8));
        Assert.Equal(new[] { true, true, true, false, true, true }, three.Stages.Select(st => st.KeyChainOut).ToArray());
        // 两击、锁开:倒数第二击就是第 1 击,展开后的第 1 段
        SkillRecipe two = t.ExpandFor(Def(2, 8));
        Assert.Equal(new[] { true, false, true, true }, two.Stages.Select(st => st.KeyChainOut).ToArray());
        // 一击、锁开:没有倒数第二击,谁也不锁
        Assert.True(t.ExpandFor(Def(1, 8)).Stages.All(st => st.KeyChainOut));
        // 三击、锁关(static[1] = 0):谁也不锁
        Assert.True(t.ExpandFor(Def(3, 0)).Stages.All(st => st.KeyChainOut));
    }

    [Fact]
    public void ARepeatTemplateIsExactlyFourStagesAndCannotBeAnAutoChainOrOtherSpecialRecipe()
    {
        var repeat = new SkillRepeatSpec(0);
        SkillRecipe three = new SkillRecipe(901, new[] { Stage(10), Stage(11, true), Stage(12) }, -1, -1, -1, -1, 2);
        SkillRecipe five = new SkillRecipe(901, new[] { Stage(10), Stage(11, true), Stage(12), Stage(13, true), Stage(14) }, -1, -1, -1, -1, 2);
        Assert.Throws<ArgumentException>(() => three.WithRepeat(repeat));
        Assert.Throws<ArgumentException>(() => five.WithRepeat(repeat));
        var four = new[] { Stage(10), Stage(11, true), Stage(12), Stage(13, true) };
        Assert.Throws<ArgumentException>(() => new SkillRecipe(901, four, -1, -1, -1, -1, 2, autoChain: true).WithRepeat(repeat));
        Assert.Throws<ArgumentNullException>(() => new SkillRecipe(901, four, -1, -1, -1, -1, 2).WithRepeat(null));
        Assert.NotNull(new SkillRecipe(901, four, -1, -1, -1, -1, 2).WithRepeat(repeat).Repeat);          // 对照:四段、普通配方可以
    }

    // ------------------------------------------------------------------ 额外的 [static data] 需求

    [Fact]
    public void TheExtraStaticNeedIsTheLargestIndexUsedByTheWarpOrTheRepeatPlusOne()
    {
        var plain = new SkillRecipe(901, new[] { Stage(10), Stage(11, true) }, -1, -1, -1, -1, 2);
        Assert.Equal(0, plain.ExtraStaticNeed);
        var warp = new SkillRecipe(901, new[] { Stage(10), Stage(11).WithWarp(new SkillWarpSpec(3)) }, -1, -1, -1, -1, 2, autoChain: true);
        Assert.Equal(4, warp.ExtraStaticNeed);
        Assert.Equal(1, Template(new SkillRepeatSpec(0)).ExtraStaticNeed);                 // 只有击数下标 0,锁没有
        Assert.Equal(5, Template(new SkillRepeatSpec(4, 1)).ExtraStaticNeed);              // 击数下标 4 最大
        Assert.Equal(8, Template(new SkillRepeatSpec(1, 7)).ExtraStaticNeed);              // 锁的下标 7 最大
        // 瞬移的下标比击数、锁都大时取瞬移的
        SkillRecipe both = new SkillRecipe(901,
            new[] { Stage(10).WithWarp(new SkillWarpSpec(9)).WithAutoAdvance(), Stage(11, true), Stage(12), Stage(13, true) }, -1, -1, -1, -1, 2)
            .WithRepeat(new SkillRepeatSpec(2, 3));
        Assert.Equal(10, both.ExtraStaticNeed);
    }

    // ------------------------------------------------------------------ 每段的标记

    [Fact]
    public void AStageStartsWithTheOldBehaviourAndEachMarkerReturnsACopyWithoutTouchingTheOriginal()
    {
        SkillStageSpec plain = Stage(10, strike: true);
        Assert.Equal((true, true, false), (plain.Slides, plain.KeyChainOut, plain.AutoAdvance));
        Assert.Null(plain.Warp);

        SkillStageSpec still = plain.WithoutSlide();
        SkillStageSpec warp = plain.WithWarp(new SkillWarpSpec(2));
        SkillStageSpec auto = plain.WithAutoAdvance();
        SkillStageSpec locked = plain.WithKeyChainOut(false);
        Assert.False(still.Slides);
        Assert.Equal(2, warp.Warp.DistanceStaticIndex);
        Assert.True(auto.AutoAdvance);
        Assert.False(locked.KeyChainOut);
        // 原来的那一段没变
        Assert.Equal((true, true, false), (plain.Slides, plain.KeyChainOut, plain.AutoAdvance));
        Assert.Null(plain.Warp);
        // 各个标记互不影响,也不丢原有的内容
        SkillStageSpec all = plain.WithoutSlide().WithWarp(new SkillWarpSpec(1)).WithAutoAdvance().WithKeyChainOut(false);
        Assert.Equal((false, false, true, 1, 10, true), (all.Slides, all.KeyChainOut, all.AutoAdvance, all.Warp.DistanceStaticIndex, all.MotionIndex, all.HasOwnStrike));
        Assert.True(all.WithKeyChainOut(true).KeyChainOut);
        Assert.Throws<ArgumentNullException>(() => plain.WithWarp(null));
    }

    [Fact]
    public void AnObjectIsPlacedWhereTheHeroIsByDefaultAndAtWarpOriginReturnsAFlaggedCopy()
    {
        var plain = new SkillObjectSpec(9221, stage: 1, frame: 2, damageRateColumn: 1, damageFixedColumn: 3, forwardX: 20, offsetY: 5, offsetZ: 7);
        Assert.False(plain.FromWarpOrigin);
        SkillObjectSpec origin = plain.AtWarpOrigin();
        Assert.True(origin.FromWarpOrigin);
        Assert.False(plain.FromWarpOrigin);                                        // 原来的没变
        Assert.Equal((9221, 1, 2, 1, 3, 20, 5, 7), (origin.ObjectId, origin.Stage, origin.Frame, origin.DamageRateColumn, origin.DamageFixedColumn, origin.ForwardX, origin.OffsetY, origin.OffsetZ));
    }

    [Fact]
    public void OnlyAStageThatPlainlyAutoAdvancesFollowsUpAndOnlyAnOpenStageChainsByKey()
    {
        var recipe = new SkillRecipe(901,
            new[]
            {
                Stage(10, strike: true),                          // 普通段
                Stage(11).WithAutoAdvance(),                      // 播完自己接
                Stage(12, strike: true).WithKeyChainOut(false),   // 不许按键接
                Stage(13, strike: true).WithAutoAdvance().WithKeyChainOut(true),
            },
            -1, -1, -1, -1, 2);
        Assert.Equal(new[] { false, true, false, true }, Enumerable.Range(0, 4).Select(i => HeroSkills.AutoAdvancesFrom(recipe, i)).ToArray());
        Assert.Equal(new[] { true, false, false, false }, Enumerable.Range(0, 4).Select(i => HeroSkills.KeyChainsFrom(recipe, i)).ToArray());
        // 段号越界:不自己接;按键接按旧行为(交给调用方再判有没有下一段)
        Assert.False(HeroSkills.AutoAdvancesFrom(recipe, -1));
        Assert.False(HeroSkills.AutoAdvancesFrom(recipe, 4));
        Assert.True(HeroSkills.KeyChainsFrom(recipe, -1));
        Assert.True(HeroSkills.KeyChainsFrom(recipe, 4));
    }
}
