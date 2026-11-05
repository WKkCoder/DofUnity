using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 身体命中一族(客户端状态 0x3c、0x3e、0x3f,证据 E-162、E-163)里要在房间会话里逐步看时间与位置的规则:定间隔重武装、段尾保持、关键帧起滑、线性位移、
/// 按键接段的接线、自动接续的上界。数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):动作 2 是 400 毫秒(四帧各 100,第 2 帧有攻击框)、动作 3 是 300 毫秒、
/// 动作 <see cref="AvatarFixture.FlagEtcIndex"/> 是五帧各 100 毫秒(标志 1 在第 1 帧、标志 2 在第 3 帧)、动作 <see cref="AvatarFixture.RiseEtcIndex"/> 的第 0 帧带攻击框并停 10000 毫秒;
/// [static data] 是 (200, −400, 100, −100, 2)。主角站在 (300, 100),按 10 毫秒一步推进,第 k 步结束时状态计时器是 10·k 毫秒(施放那一步算第 1 步)。
/// 位移按匀变速公式 v0·t + a·t²/2 与线性插值手算。
/// </summary>
public class BodyHitRoomTests
{
    private static SkillStageSpec Strike(int motion, int attackInfo)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: attackInfo, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
    }

    private static SkillStageSpec Plain(int motion)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
    }

    private static SkillRecipes One(SkillStageSpec[] stages, SkillBodyHitSpec body, bool autoChain = false)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill, stages, slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue,
                slideFollowsHeldDirection: false, autoChain: autoChain, bodyHit: body),
        });
    }

    private static HeroSkills Load(GameData data, SkillRecipes recipes)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 1));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    /// <summary>怪物站在主角前方 (340, 100)、主角只有脚下一小块可走区域(不动、怪物也不动):看命中次数用。</summary>
    private static RoomCombatTests.Arena Near(SkillRecipes recipes)
    {
        var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 100000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: data => Load(data, recipes));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    /// <summary>没有怪物、主角有一大片可走区域(或自己给一块):看位移用。</summary>
    private static RoomCombatTests.Arena Open(SkillRecipes recipes, bool facingLeft = false, List<WorldRect> walk = null)
    {
        var arena = new RoomCombatTests.Arena(new (int X, int Y)[0], hp: 100000, facingLeft: facingLeft, chr: AvatarFixture.ActionChrPath,
            walkArea: walk ?? new List<WorldRect> { new WorldRect(0, 0, 4000, 400) }, skillsFactory: data => Load(data, recipes));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    /// <summary>推进 <paramref name="steps"/> 步(每步 10 毫秒);第一步按下 <paramref name="firstStepKeys"/>,方向键全程按着 (dirX, dirY)。</summary>
    private static void Run(RoomCombatSession s, int steps, SkillKeys firstStepKeys = SkillKeys.None, int dirX = 0, int dirY = 0)
    {
        for (int i = 0; i < steps; i++)
        {
            s.Update(10, dirX, dirY, false, false, i == 0 ? firstStepKeys : SkillKeys.None);
        }
    }

    private static void Tap(RoomCombatSession s, SkillKeys keys = SkillKeys.None, bool attack = false)
    {
        s.Update(10, 0, 0, attack, false, keys);
    }

    // ------------------------------------------------------------------ 定间隔重武装

    private static SkillRecipes Rearm(int motion, int attackInfo, int startFrame, int intervalIndex, int countIndex)
    {
        return One(new[] { Strike(motion, attackInfo) }, new SkillBodyHitSpec(rearms: new[] { new SkillRearmSpec(0, startFrame, intervalIndex, countIndex) }));
    }

    [Fact]
    public void TheRearmClockOpensAnotherHitWindowFromTheStartFrameAndEveryIntervalAfterTheLastFire()
    {
        // 第 0 帧的攻击框停 10000 毫秒(攻击信息用普通受击那条,打中不倒地,怪物还能再挨);从第 0 帧起、间隔 S2 = 100 毫秒、次数 S4 = 2
        using var arena = Near(Rearm(AvatarFixture.RiseEtcIndex, 1, 0, 2, 4));
        RoomCombatSession s = arena.Session;
        Run(s, 1, SkillKeys.A);
        Assert.Equal((10L, 1, 1), (s.Hero.MotionMs, s.BodyRearms, s.Hits));      // 第 1 步(10 毫秒):第一次响,第一个窗口打中
        Run(s, 9);
        Assert.Equal((100L, 1, 1), (s.Hero.MotionMs, s.BodyRearms, s.Hits));     // 100 毫秒:距上次响(10)才 90,还没到
        Run(s, 1);
        Assert.Equal((110L, 2, 2), (s.Hero.MotionMs, s.BodyRearms, s.Hits));     // 110 毫秒:距上次响满 100,第二次响,同一个怪物再挨一下
        Run(s, 200);
        Assert.Equal((2, 2), (s.BodyRearms, s.Hits));                            // 次数用完,不再响
    }

    [Fact]
    public void WithoutARearmRuleTheHitListIsNeverClearedSoOnlyOneWindowOpens()
    {
        foreach (SkillBodyHitSpec body in new[] { null, new SkillBodyHitSpec() })
        {
            using var arena = Near(One(new[] { Strike(AvatarFixture.RiseEtcIndex, 1) }, body));
            RoomCombatSession s = arena.Session;
            Run(s, 1, SkillKeys.A);
            Run(s, 150);
            Assert.Equal((1, 0), (s.Hits, s.BodyRearms));
        }
    }

    [Fact]
    public void ANegativeCountOrAStartFrameThatIsNotReachedNeverFires()
    {
        using (var arena = Near(Rearm(AvatarFixture.RiseEtcIndex, 1, 0, 2, 1)))          // 次数取 S1 = −400
        {
            Run(arena.Session, 1, SkillKeys.A);
            Run(arena.Session, 100);
            Assert.Equal((1, 0), (arena.Session.Hits, arena.Session.BodyRearms));
        }
        using (var arena = Near(Rearm(AvatarFixture.RiseEtcIndex, 1, 1, 2, 4)))          // 从第 1 帧起,而第 0 帧停 10000 毫秒:一秒内到不了
        {
            Run(arena.Session, 1, SkillKeys.A);
            Run(arena.Session, 100);
            Assert.Equal((1, 0), (arena.Session.Hits, arena.Session.BodyRearms));
        }
    }

    [Fact]
    public void TheStartFrameIsTheWallClockTimeWhereThatFrameBegins()
    {
        // 动作 2:第 2 帧从 200 毫秒起;间隔 S2 = 100、次数 S4 = 2 → 200 毫秒响一次,300 毫秒再响一次(第 2 帧的攻击框 300 毫秒结束,第二次窗口里没有框)
        using var arena = Near(Rearm(2, 1, 2, 2, 4));
        RoomCombatSession s = arena.Session;
        Run(s, 19, SkillKeys.A);
        Assert.Equal((190L, 0, 0), (s.Hero.MotionMs, s.BodyRearms, s.Hits));
        Run(s, 1);
        Assert.Equal((200L, 1, 1), (s.Hero.MotionMs, s.BodyRearms, s.Hits));
        Run(s, 9);
        Assert.Equal((290L, 1, 1), (s.Hero.MotionMs, s.BodyRearms, s.Hits));
        Run(s, 1);
        Assert.Equal((300L, 2, 1), (s.Hero.MotionMs, s.BodyRearms, s.Hits));       // 第二次响了,但攻击框已经过去,没有新的命中
    }

    [Fact]
    public void AFasterAttackSpeedMovesTheRearmStartEarlierInWallClockTime()
    {
        using var arena = Near(Rearm(2, 1, 2, 2, 4));
        RoomCombatSession s = arena.Session;
        s.Hero.AttackAnimationRate = 2.0;                                           // 动画快一倍:第 2 帧从墙钟 100 毫秒起
        Run(s, 9, SkillKeys.A);
        Assert.Equal((90L, 0), (s.Hero.MotionMs, s.BodyRearms));
        Run(s, 1);
        Assert.Equal((100L, 1), (s.Hero.MotionMs, s.BodyRearms));
    }

    // ------------------------------------------------------------------ 段尾保持

    private static SkillRecipes Hold(SkillHoldKind kind, int staticIndex)
    {
        return One(new[] { Plain(3) }, new SkillBodyHitSpec(holds: new[] { new SkillStageHold(0, kind, staticIndex) }));
    }

    private static int StepsUntilTheSkillEnds(RoomCombatSession s)
    {
        Run(s, 1, SkillKeys.A);
        int steps = 1;
        while (s.Hero.Motion == HeroMotion.Skill && steps < 400)
        {
            Run(s, 1);
            steps++;
        }
        return steps;
    }

    [Fact]
    public void AHoldAfterTheAnimationKeepsTheSkillGoingForTheHoldTime()
    {
        // 动作 3 是 300 毫秒,保持 S2 = 100:技能到 400 毫秒才结束(第 40 步);没有保持的版本 300 毫秒结束(第 30 步)
        using (var arena = Open(Hold(SkillHoldKind.AfterAnimation, 2)))
        {
            Assert.Equal(40, StepsUntilTheSkillEnds(arena.Session));
        }
        using (var arena = Open(One(new[] { Plain(3) }, null)))
        {
            Assert.Equal(30, StepsUntilTheSkillEnds(arena.Session));
        }
    }

    [Fact]
    public void ANegativeAfterHoldAndAnAtLeastHoldShorterThanTheAnimationDoNotChangeTheEnd()
    {
        using (var arena = Open(Hold(SkillHoldKind.AfterAnimation, 3)))                // S3 = −100:当 0
        {
            Assert.Equal(30, StepsUntilTheSkillEnds(arena.Session));
        }
        using (var arena = Open(Hold(SkillHoldKind.AtLeast, 0)))                       // 至少 S0 = 200 毫秒,动画有 300:以动画为准
        {
            Assert.Equal(30, StepsUntilTheSkillEnds(arena.Session));
        }
    }

    [Fact]
    public void AHoldDelaysTheAutoChainToTheNextStageByTheHoldTime()
    {
        // 两段自动接续:第 0 段(400 毫秒)保持 S2 = 100,第 1 段在 500 毫秒才开始
        var recipes = One(new[] { Plain(2), Plain(3) }, new SkillBodyHitSpec(holds: new[] { new SkillStageHold(0, SkillHoldKind.AfterAnimation, 2) }), autoChain: true);
        using var arena = Open(recipes);
        RoomCombatSession s = arena.Session;
        Run(s, 49, SkillKeys.A);
        Assert.Equal((0, 0), (s.Hero.SkillStage, s.SkillChains));
        Run(s, 1);
        Assert.Equal((1, 1), (s.Hero.SkillStage, s.SkillChains));
    }

    // ------------------------------------------------------------------ 关键帧起滑

    private static SkillRecipes ScriptSlide(int flag)
    {
        return One(new[] { Plain(AvatarFixture.FlagEtcIndex) }, new SkillBodyHitSpec(scriptSlides: new[] { new SkillScriptSlide(0, flag, 0, 1) }));
    }

    [Fact]
    public void AScriptSlideStartsAtTheFlaggedFrameAndFollowsTheConstantDecelerationFormula()
    {
        // 标志 1 在第 1 帧(100 毫秒)。滑行 v0 = S0 = 200、a = S1 = −400:Δ 秒后位移 200·Δ − 200·Δ²
        using var arena = Open(ScriptSlide(AvatarFixture.FlagA));
        RoomCombatSession s = arena.Session;
        Run(s, 9, SkillKeys.A);
        Assert.Equal((90L, 0, 300.0), (s.Hero.MotionMs, s.BodySlides, s.Hero.X));      // 标志帧还没到
        Run(s, 1);
        Assert.Equal((100L, 1, 300.0), (s.Hero.MotionMs, s.BodySlides, s.Hero.X));     // 标志帧:起滑,这一刻位移 0
        Run(s, 10);
        Assert.Equal(318.0, s.Hero.X, 6);                                               // Δ = 0.1:20 − 2 = 18
        Run(s, 10);
        Assert.Equal(332.0, s.Hero.X, 6);                                               // Δ = 0.2:40 − 8 = 32
        Run(s, 15);
        Assert.Equal(345.5, s.Hero.X, 6);                                               // Δ = 0.35:70 − 24.5 = 45.5
        Assert.Equal(1, s.BodySlides);                                                  // 第 3 帧的标志 2 配方里没登记,不再起滑
    }

    [Fact]
    public void TheScriptSlideIsSkippedOnlyForABackwardHeldKeyAndPointsTheOtherWayWhenFacingLeft()
    {
        foreach ((int dirX, int dirY, bool left, double expectedX, int slides) in new[]
        {
            (0, 0, false, 332.0, 1),        // 没按:滑
            (1, 0, false, 332.0, 1),        // 朝右按右:滑(速度不变,不看"顺着按")
            (0, 1, false, 332.0, 1),        // 只按上下:滑
            (-1, 0, false, 300.0, 0),       // 朝右按左:不滑,标志照样消费
            (-1, 1, false, 300.0, 0),
            (-1, 0, true, 268.0, 1),        // 朝左按左:顺着,向左滑 32
            (1, 0, true, 300.0, 0),         // 朝左按右:不滑
            (0, 0, true, 268.0, 1),
        })
        {
            using var arena = Open(ScriptSlide(AvatarFixture.FlagA), facingLeft: left);
            RoomCombatSession s = arena.Session;
            Run(s, 30, SkillKeys.A, dirX, dirY);
            Assert.Equal((expectedX, slides), (s.Hero.X, s.BodySlides));
        }
    }

    [Fact]
    public void AScriptSlideListensOnlyToTheFlagItWasGiven()
    {
        // 登记标志 2(第 3 帧,300 毫秒):450 毫秒时 Δ = 0.15:30 − 4.5 = 25.5;之前不动
        using var arena = Open(ScriptSlide(AvatarFixture.FlagB));
        RoomCombatSession s = arena.Session;
        Run(s, 29, SkillKeys.A);
        Assert.Equal((290L, 300.0, 0), (s.Hero.MotionMs, s.Hero.X, s.BodySlides));      // 标志 1 在 100 毫秒过了,没有起滑
        Run(s, 1);
        Assert.Equal((300L, 1), (s.Hero.MotionMs, s.BodySlides));
        Run(s, 15);
        Assert.Equal(325.5, s.Hero.X, 6);
    }

    [Fact]
    public void TheScriptSlideStopsAtTheWalkAreaEdgeAndAfterTheSkillEnds()
    {
        // 可走区的右边界在 x = 310(含)
        using (var arena = Open(ScriptSlide(AvatarFixture.FlagA), walk: new List<WorldRect> { new WorldRect(250, 90, 60, 20) }))
        {
            RoomCombatSession s = arena.Session;
            Run(s, 45, SkillKeys.A);
            Assert.InRange(s.Hero.X, 309.0, 310.0);                                    // 一像素一步地挪,停在边界内一像素以内
            Assert.Equal(100.0, s.Hero.Y);
        }
        // 技能 500 毫秒结束(最后一个完整的步是 490 毫秒:Δ = 0.39 → 78 − 30.42 = 47.58);之后位置不再动
        using (var arena = Open(ScriptSlide(AvatarFixture.FlagA)))
        {
            RoomCombatSession s = arena.Session;
            Run(s, 49, SkillKeys.A);
            Assert.Equal(347.58, s.Hero.X, 6);
            Run(s, 60);
            Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
            Assert.Equal(347.58, s.Hero.X, 6);
        }
    }

    // ------------------------------------------------------------------ 线性位移

    private static SkillRecipes Travel(int distanceIndex, int durationIndex)
    {
        return One(new[] { Plain(2) }, new SkillBodyHitSpec(travels: new[] { new SkillTravelSpec(0, distanceIndex, durationIndex) }));
    }

    [Fact]
    public void TheTravelPutsTheHeroAtTheLinearOffsetOfTheElapsedTimeAndHoldsItAtTheEnd()
    {
        // 距离 S2 = 100、时长 S0 = 200 毫秒:第 k 步偏移 = 100 × 10k / 200 = 5k,到 200 毫秒封顶
        using var arena = Open(Travel(2, 0));
        RoomCombatSession s = arena.Session;
        Run(s, 1, SkillKeys.A);
        Assert.Equal(305.0, s.Hero.X, 6);
        Run(s, 9);
        Assert.Equal(350.0, s.Hero.X, 6);                                               // 100 毫秒:50
        Run(s, 9);
        Assert.Equal(395.0, s.Hero.X, 6);                                               // 190 毫秒:95
        Run(s, 1);
        Assert.Equal(400.0, s.Hero.X, 6);                                               // 200 毫秒:到头
        Run(s, 18);
        Assert.Equal(400.0, s.Hero.X, 6);                                               // 之后在动画里不再动
    }

    [Fact]
    public void TheTravelTruncatesToWholePixelsAndGoesTheOtherWayForLeftFacingAndNegativeDistances()
    {
        // 距离 S4 = 2、时长 S0 = 200:90 毫秒 0.9 → 0;100 毫秒 1.0 → 1;190 毫秒 1.9 → 1;200 毫秒 2
        using (var arena = Open(Travel(4, 0)))
        {
            RoomCombatSession s = arena.Session;
            Run(s, 9, SkillKeys.A);
            Assert.Equal(300.0, s.Hero.X, 6);
            Run(s, 1);
            Assert.Equal(301.0, s.Hero.X, 6);
            Run(s, 9);
            Assert.Equal(301.0, s.Hero.X, 6);
            Run(s, 1);
            Assert.Equal(302.0, s.Hero.X, 6);
        }
        using (var arena = Open(Travel(2, 0), facingLeft: true))                        // 朝左:往左走
        {
            Run(arena.Session, 20, SkillKeys.A);
            Assert.Equal(200.0, arena.Session.Hero.X, 6);
        }
        using (var arena = Open(Travel(3, 0)))                                          // 距离 S3 = −100:朝右的主角往左走 50(100 毫秒时)
        {
            Run(arena.Session, 10, SkillKeys.A);
            Assert.Equal(250.0, arena.Session.Hero.X, 6);
        }
    }

    [Fact]
    public void TheTravelStopsAtTheWalkAreaEdge()
    {
        using var arena = Open(Travel(2, 0), walk: new List<WorldRect> { new WorldRect(250, 90, 80, 20) });     // 右边界 330(含)
        Run(arena.Session, 30, SkillKeys.A);
        Assert.Equal(330.0, arena.Session.Hero.X, 6);
    }

    [Fact]
    public void EachStageTravelsFromWhereItStartsAndAStageWithoutATravelDoesNotMove()
    {
        // 两段自动接续,只有第 1 段配了位移:第 0 段(400 毫秒)不动,第 1 段从 300 起走 100
        var onlySecond = One(new[] { Plain(2), Plain(3) }, new SkillBodyHitSpec(travels: new[] { new SkillTravelSpec(1, 2, 0) }), autoChain: true);
        using (var arena = Open(onlySecond))
        {
            RoomCombatSession s = arena.Session;
            Run(s, 39, SkillKeys.A);
            Assert.Equal((0, 300.0), (s.Hero.SkillStage, s.Hero.X));
            Run(s, 10);                                                                  // 第 40 步第 0 段播完换段,新一段的计时器从 0 起:第 49 步是 90 毫秒
            Assert.Equal((1, 345.0), (s.Hero.SkillStage, s.Hero.X));                   // 100 × 90 / 200 = 45
            Run(s, 1);
            Assert.Equal(350.0, s.Hero.X, 6);                                           // 第 50 步:100 毫秒 → 50
            Run(s, 20);
            Assert.Equal(400.0, s.Hero.X, 6);
        }
        // 两段都配:第 0 段走到 400,第 1 段从 400 起再走 100 到 500(不是从施放处 300 算)
        var both = One(new[] { Plain(2), Plain(3) }, new SkillBodyHitSpec(travels: new[] { new SkillTravelSpec(0, 2, 0), new SkillTravelSpec(1, 2, 0) }), autoChain: true);
        using (var arena = Open(both))
        {
            RoomCombatSession s = arena.Session;
            Run(s, 39, SkillKeys.A);
            Assert.Equal(400.0, s.Hero.X, 6);
            Run(s, 10);                                                                  // 第 49 步:第 1 段 90 毫秒
            Assert.Equal((1, 445.0), (s.Hero.SkillStage, s.Hero.X));                   // 400 + 100 × 90 / 200
            Run(s, 20);
            Assert.Equal(500.0, s.Hero.X, 6);
        }
    }

    // ------------------------------------------------------------------ 按键接段的接线

    private static SkillRecipes ChainTable(SkillChainKeys keys, int fromFrame)
    {
        return One(new[] { Strike(2, 1), Strike(3, 2) }, new SkillBodyHitSpec(chains: new[] { new SkillKeyChain(0, 0, 1, fromFrame, keys) }));
    }

    [Fact]
    public void TheSkillKeyChainsOnlyFromTheThresholdFrameAndTheChainIsNotANewCast()
    {
        using var arena = Open(ChainTable(SkillChainKeys.SkillKey, 2));
        RoomCombatSession s = arena.Session;
        Run(s, 19, SkillKeys.A);                                                       // 190 毫秒:第 1 帧
        Tap(s, SkillKeys.CommandSkill);                                                // 这一步开头是 190 毫秒(第 1 帧):不接
        Assert.Equal((0, 0), (s.Hero.SkillStage, s.SkillChains));
        Tap(s, SkillKeys.CommandSkill);                                                // 这一步开头是 200 毫秒(第 2 帧):接第 1 段
        Assert.Equal((1, 1, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
    }

    [Fact]
    public void AWrongKeyDoesNotChainAndAnIdleSkillKeyDoesNothing()
    {
        using var arena = Open(ChainTable(SkillChainKeys.SkillKey, 0));
        RoomCombatSession s = arena.Session;
        Tap(s, SkillKeys.CommandSkill);                                                // 还没放技能:技能键没有可接的
        Assert.Equal((0, 0), (s.SkillChains, s.SkillCasts));
        Run(s, 1, SkillKeys.A);
        Tap(s, SkillKeys.None, attack: true);                                          // 攻击键:这条规则不认
        Tap(s, SkillKeys.A);                                                           // 自己的快捷键:不认,而且还在冷却里放不出第二次
        Assert.Equal((0, 0, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
    }

    [Fact]
    public void TheAttackKeyChainsWhenTheRuleAcceptsItAndTheKeyIsConsumed()
    {
        using var arena = Open(ChainTable(SkillChainKeys.AttackKey, 0));
        RoomCombatSession s = arena.Session;
        Run(s, 1, SkillKeys.A);
        Tap(s, SkillKeys.None, attack: true);
        Assert.Equal((1, 1, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);                                  // 攻击键用来接段了,没有同时起一次普攻
    }

    // ------------------------------------------------------------------ 自动接续的上界(会话计数)

    [Fact]
    public void TheSessionCountsOnlyTheAutoChainsTheBoundAllows()
    {
        foreach ((int bound, int chains) in new[] { (0, 1), (1, 2), (-1, 2) })
        {
            var recipes = One(new[] { Plain(2), Plain(3), Plain(2) }, new SkillBodyHitSpec(autoChainLastFromStage: bound), autoChain: true);
            using var arena = Open(recipes);
            RoomCombatSession s = arena.Session;
            Run(s, 1, SkillKeys.A);
            Run(s, 150);
            Assert.Equal((chains, HeroMotion.Rest), (s.SkillChains, s.Hero.Motion));     // 都已经放完;上界 0 只接一次,上界 1 或没有上界接两次
        }
    }
}
