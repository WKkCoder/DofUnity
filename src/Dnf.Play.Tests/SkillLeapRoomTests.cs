using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 脚本前冲(客户端状态 0x2d 的模式 1)在房间会话里的接线:主角自己按动画时间走水平与高度(<see cref="TownHero"/>),会话办落地块里的事(声音、震屏、预载特效、撤特效)与每段开始的声音。
/// 数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):动作 2 是 400 毫秒(四帧各 100)、动作 3 是 300 毫秒(三帧各 100),[static data] 是 (200, −400, 100, −100, 2),
/// 预载特效两个(下标 0、1 是 <see cref="AvatarFixture.ChainEffect1"/>、<see cref="AvatarFixture.ChainEffect2"/>,各 350 毫秒,四帧:150、50、50、100 毫秒,第 2 帧从 200 毫秒起、第 3 帧从 250 毫秒起)。
/// 主角站在 (300, 100),按 10 毫秒一步推进,第 k 步结束时状态计时器是 10·k 毫秒(施放那一步算第 1 步)。
/// 水平位移按"距离 × 已过 / 落地时刻"手算(整数线性插值向零取整);高度的逐步值与纯函数 <see cref="SkillLeapPlan.HeightAt"/> 对拍(纯函数自己的取整另有手算用例)。
/// </summary>
public class SkillLeapRoomTests
{
    private static SkillStageSpec Plain(int motion, int endFrame = -1)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, endFrame: endFrame);
    }

    /// <summary>前冲段:水平距离取 [static data] 第 <paramref name="distanceIndex"/> 个数、最大高度取第 <paramref name="heightIndex"/> 个数(默认第 0 个 = 200)。</summary>
    private static SkillStageSpec Leaping(int motion, int distanceIndex, int landFrame, int heightIndex = 0, SkillLeapLanding landing = null, int endFrame = -1)
    {
        var leap = new SkillLeapSpec(distanceIndex, heightIndex, landFrame);
        return Plain(motion, endFrame).WithLeap(landing == null ? leap : leap.WithLanding(landing));
    }

    private static SkillRecipes One(SkillStageSpec[] stages, bool autoChain = false, SkillBodyHitSpec body = null)
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

    /// <summary>没有怪物、主角有一大片可走区域(或自己给一块)。</summary>
    private static RoomCombatTests.Arena Open(SkillRecipes recipes, bool facingLeft = false, List<WorldRect> walk = null)
    {
        var arena = new RoomCombatTests.Arena(new (int X, int Y)[0], hp: 100000, facingLeft: facingLeft, chr: AvatarFixture.ActionChrPath,
            walkArea: walk ?? new List<WorldRect> { new WorldRect(0, 0, 4000, 400) }, skillsFactory: data => Load(data, recipes));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    /// <summary>推进 <paramref name="steps"/> 步(每步 10 毫秒);第一步按下 <paramref name="firstStepKeys"/>。</summary>
    private static void Run(RoomCombatSession s, int steps, SkillKeys firstStepKeys = SkillKeys.None)
    {
        for (int i = 0; i < steps; i++)
        {
            s.Update(10, 0, 0, false, false, i == 0 ? firstStepKeys : SkillKeys.None);
        }
    }

    private static List<string> Tags(RoomCombatSession s)
    {
        var all = new List<SoundCue>();
        s.DrainSounds(all);
        return all.Where(c => c.Key.StartsWith("TEST_")).Select(c => c.Key).ToList();
    }

    /// <summary>落地块的事,全用测试名:两个声音、震屏 (5, 300)、特效 0(跟随)与特效 1(向前 50、向下 10)、撤特效(盯着特效 0 播到第 <paramref name="trimFrame"/> 帧就把 0 与 1 一起撤)。</summary>
    private static SkillLeapLanding Landing(int trimFrame = 2)
    {
        return new SkillLeapLanding(5, 300, new[] { "TEST_LAND_A", "TEST_LAND_B" },
            new[] { new SkillLandingEffect(0), new SkillLandingEffect(1, forward: 50, down: 10) },
            new SkillEffectTrim(watchIndex: 0, frame: trimFrame, removeIndices: new[] { 0, 1 }));
    }

    // ------------------------------------------------------------------ 水平位移

    [Fact]
    public void TheLeapFollowsTheLinearOffsetAndStopsAtTheLandingFrameWithoutSnappingToTheFullDistance()
    {
        // 距离 S2 = 100;落地帧 2 → 落地时刻 = 前两帧 = 200 毫秒;第 k 步偏移 = 100 × 10k / 200 = 5k
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 1, SkillKeys.A);
        Assert.Equal(305.0, s.Hero.X, 6);                    // 10 毫秒:5
        Run(s, 9);
        Assert.Equal(350.0, s.Hero.X, 6);                    // 100 毫秒:50
        Run(s, 9);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 190 毫秒:95
        Assert.Equal(0, s.Hero.LeapLandings);                // 还在空中
        Run(s, 1);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 200 毫秒 = 落地时刻:这一步不摆位置,不补满距离(客户端落地块不写水平位置)
        Assert.Equal(1, s.Hero.LeapLandings);
        Run(s, 18);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 之后在动画里不再动
        Assert.Equal(1, s.Hero.LeapLandings);                // 一次前冲只落地一次
    }

    [Fact]
    public void TheLeapIsNotTheClampedLinearTravelTheOldTravelGivesTheFullDistanceAtTheEnd()
    {
        // 新旧对照:同一段 200 毫秒、距离 100,按时长取数的线性位移(夹到满距离)到 400,脚本前冲停在 395
        using (var old = Open(One(new[] { Plain(2) }, body: new SkillBodyHitSpec(travels: new[] { new SkillTravelSpec(0, 2, 0) }))))
        {
            Run(old.Session, 20, SkillKeys.A);
            Assert.Equal(400.0, old.Session.Hero.X, 6);
            Assert.Equal(0, old.Session.Hero.LeapLandings);
        }
        using (var leap = Open(One(new[] { Leaping(2, 2, 2) })))
        {
            Run(leap.Session, 20, SkillKeys.A);
            Assert.Equal(395.0, leap.Session.Hero.X, 6);
        }
    }

    [Fact]
    public void ALeftFacingHeroLeapsLeftAndANegativeDistanceGoesTheOtherWay()
    {
        using (var arena = Open(One(new[] { Leaping(2, 2, 2) }), facingLeft: true))
        {
            Run(arena.Session, 19, SkillKeys.A);
            Assert.Equal(205.0, arena.Session.Hero.X, 6);    // 300 − 95
            Run(arena.Session, 10);
            Assert.Equal(205.0, arena.Session.Hero.X, 6);
        }
        using (var arena = Open(One(new[] { Leaping(2, 3, 2) })))                    // 距离 S3 = −100:朝右的主角往左走,100 毫秒时 −50
        {
            Run(arena.Session, 10, SkillKeys.A);
            Assert.Equal(250.0, arena.Session.Hero.X, 6);
        }
    }

    [Fact]
    public void AFasterAttackSpeedMakesTheHeroLandEarlierInWallClockTime()
    {
        // 动画快一倍:前两帧 200 动画毫秒 = 100 墙钟毫秒落地;90 毫秒时偏移 100 × 90 / 100 = 90,100 毫秒起不再动
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        s.Hero.AttackAnimationRate = 2.0;
        Run(s, 9, SkillKeys.A);
        Assert.Equal(390.0, s.Hero.X, 6);
        Assert.Equal(0, s.Hero.LeapLandings);
        Run(s, 1);
        Assert.Equal(390.0, s.Hero.X, 6);
        Assert.Equal(1, s.Hero.LeapLandings);                // 100 毫秒就落地
        Run(s, 20);
        Assert.Equal(390.0, s.Hero.X, 6);
    }

    [Fact]
    public void TheLandingFrameCountsTheFramesBeforeIt()
    {
        // 落地帧 3 → 落地时刻 300 毫秒:100 毫秒时 100 × 100 / 300 = 33.3 → 33;290 毫秒时 96.7 → 96;300 毫秒起不动
        using var arena = Open(One(new[] { Leaping(2, 2, 3) }));
        RoomCombatSession s = arena.Session;
        Run(s, 10, SkillKeys.A);
        Assert.Equal(333.0, s.Hero.X, 6);
        Run(s, 19);
        Assert.Equal(396.0, s.Hero.X, 6);
        Run(s, 1);
        Assert.Equal(396.0, s.Hero.X, 6);
        Assert.Equal(1, s.Hero.LeapLandings);
    }

    [Fact]
    public void OnlyTheStageWithTheLeapMovesAndItStartsFromWhereTheStageBegan()
    {
        // 两段自动接续,只有第 1 段(动作 3,300 毫秒)带前冲、落地帧 2 → 200 毫秒;第 0 段(400 毫秒)原地不动、不离地
        using var arena = Open(One(new[] { Plain(2), Leaping(3, 2, 2) }, autoChain: true));
        RoomCombatSession s = arena.Session;
        Run(s, 39, SkillKeys.A);
        Assert.Equal((0, 300.0, 0.0), (s.Hero.SkillStage, s.Hero.X, s.Hero.Z));
        Run(s, 10);                                          // 第 40 步换段,新一段的计时器从 0 起:第 49 步是 90 毫秒
        Assert.Equal((1, 345.0), (s.Hero.SkillStage, s.Hero.X));      // 100 × 90 / 200 = 45
        Assert.True(s.Hero.Z > 0.0);                         // 这一段在空中
        Run(s, 10);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 第 59 步:190 毫秒 → 95
        Run(s, 1);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 第 60 步:200 毫秒,落地
        Assert.Equal((1, 0.0), (s.Hero.LeapLandings, s.Hero.Z));
        Run(s, 30);
        Assert.Equal(395.0, s.Hero.X, 6);
    }

    [Fact]
    public void ASecondLeapStageFliesAgainAfterTheFirstOneHasLandedAndStartsFromWhereItLanded()
    {
        // 两段都是前冲(自动接续):第 0 段(动作 2,400 毫秒)距离 100、200 毫秒落地,停在 395;第 1 段(动作 3)距离 −100、200 毫秒落地,从 395 起算
        using var arena = Open(One(new[] { Leaping(2, 2, 2), Leaping(3, 3, 2) }, autoChain: true));
        RoomCombatSession s = arena.Session;
        Run(s, 39, SkillKeys.A);
        Assert.Equal((0, 395.0, 1), (s.Hero.SkillStage, s.Hero.X, s.Hero.LeapLandings));
        Run(s, 10);                                          // 第 40 步换段,第 49 步是 90 毫秒:−100 × 90 / 200 = −45
        Assert.Equal((1, 350.0), (s.Hero.SkillStage, s.Hero.X));
        Assert.True(s.Hero.Z > 0.0);                         // 第二次起跳:又在空中(上一段落地留下的"已落地"必须在这一段开头清掉)
        Run(s, 10);
        Assert.Equal(300.0, s.Hero.X, 6);                    // 第 59 步:190 毫秒 → −95
        Run(s, 1);                                           // 第 60 步:200 毫秒,第二次落地
        Assert.Equal((2, 0.0, 300.0), (s.Hero.LeapLandings, s.Hero.Z, s.Hero.X));
    }

    [Fact]
    public void ASkillWithoutALeapStaysPutOnTheGroundAndCountsNoLanding()
    {
        using var arena = Open(One(new[] { Plain(2) }));
        for (int step = 0; step < 30; step++)
        {
            arena.Session.Update(10, 0, 0, false, false, step == 0 ? SkillKeys.A : SkillKeys.None);
            Assert.Equal(0.0, arena.Session.Hero.Z);
        }
        Assert.Equal(300.0, arena.Session.Hero.X, 6);
        Assert.Equal((0, 0), (arena.Session.Hero.LeapLandings, arena.Session.LeapLandings));
    }

    // ------------------------------------------------------------------ 高度弧

    [Fact]
    public void TheHeightFollowsTheEaseOutThenEaseInArcAndTheHeroIsBackOnTheGroundWhenItLands()
    {
        // 最大高度 S0 = 200,落地时刻 200 毫秒 → 折半点 100:每一步的高度与纯函数对拍,并在几个手算点上直接断言
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        var heights = new List<int>();
        for (int step = 1; step <= 25; step++)
        {
            s.Update(10, 0, 0, false, false, step == 1 ? SkillKeys.A : SkillKeys.None);
            heights.Add((int)s.Hero.Z);
            if (step < 20)
            {
                Assert.Equal(SkillLeapPlan.HeightAt(200, 10 * step, 200), heights[step - 1]);
            }
        }
        Assert.Equal(150, heights[4]);                       // 第 5 步 50 毫秒:s = 0.5,200 × (1 − 0.25) = 150
        Assert.Equal(200, heights[9]);                       // 第 10 步 100 毫秒 = 折半点:后半段第 0 毫秒取起点 = 最大高度
        Assert.Equal(150, heights[14]);                      // 第 15 步 150 毫秒:后半段镜像,与 50 毫秒对称
        Assert.True(heights.Take(9).Zip(heights.Skip(1).Take(9), (a, b) => b > a).All(up => up));      // 前半段严格上升
        Assert.True(heights.Skip(9).Take(10).Zip(heights.Skip(10).Take(10), (a, b) => b < a).All(down => down));   // 后半段严格下降
        Assert.All(heights.Skip(19), h => Assert.Equal(0, h));                         // 第 20 步(200 毫秒)落地起回到地面
        for (int k = 1; k < 10; k++)
        {
            Assert.Equal(heights[10 - k - 1], heights[10 + k - 1]);                    // 折半点前后第 k 步的高度相等
        }
    }

    [Fact]
    public void TheHeroIsAirborneUntilTheLandingStepAndTheAirborneSkillCannotBeFlinchedOut()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 5, SkillKeys.A);
        Assert.True(s.Hero.LeapAirborne);
        Assert.False(s.Hero.BeginHurt(200, 10, 100, false));  // 空中放着技能(高度 &gt; 0)被打走倒地流程,不会进受击(与脚本跳跃一致)
        Run(s, 14);
        Assert.True(s.Hero.LeapAirborne);                     // 第 19 步:190 毫秒,还差 10 毫秒
        Run(s, 1);
        Assert.False(s.Hero.LeapAirborne);
        Assert.Equal(0.0, s.Hero.Z);
    }

    [Fact]
    public void AHeroKnockedDownInTheAirKeepsItsHeightForTheDownMachineAndTheLeapStopsDriving()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 5, SkillKeys.A);
        double z = s.Hero.Z;
        double x = s.Hero.X;
        Assert.True(z > 0.0);
        Assert.True(s.Hero.BeginDown());
        Assert.Equal(z, s.Hero.Z);                            // 离地高度交给倒地流程
        Assert.False(s.Hero.LeapAirborne);                    // 前冲计划被清掉
        s.Hero.Update(100, 0, 0);                             // 之后主角自己不再按前冲摆位置
        Assert.Equal(x, s.Hero.X, 6);
    }

    [Fact]
    public void AnInterruptedLeapLeavesNoStalePlanToDriveTheNextSkillThatHasNoLeapOfItsOwn()
    {
        // 空中被击倒打断,起身后换一个不带前冲规格的入口(先挥连招第 0 段、播到第 1 帧换成动作 3 的起手换动画入口,不经过前冲计划的重置)放另一个技能:
        // 旧计划必须已清掉,主体阶段里主角原地不被拉回旧起点、也不离地
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 5, SkillKeys.A);                               // 50 毫秒:空中,X = 325
        Assert.True(s.Hero.BeginDown());
        s.Hero.EndDown();
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        Assert.Equal(325.0, s.Hero.X, 6);
        Assert.True(s.Hero.BeginSkillWindup(AvatarFixture.ChainSkill, 0, 1, 3, 0.0, 0.0));
        long swing = s.Hero.SwingNumber;
        for (int i = 0; i < 100 && s.Hero.Motion == HeroMotion.Skill; i++)
        {
            s.Hero.Update(10, 0, 0);
            Assert.Equal(325.0, s.Hero.X, 6);                 // 旧计划没清的话,主体阶段里会被拽回 300 + 偏移
            Assert.Equal(0.0, s.Hero.Z);
        }
        Assert.True(s.Hero.SwingNumber > swing);              // 确实走到了主体阶段(起手换动画算新的一击)
        Assert.Equal(0, s.Hero.LeapLandings);
    }

    [Fact]
    public void ALeapThatEndsInTheAirBeforeLandingPutsTheHeroBackOnTheGround()
    {
        // 状态在第 1 帧开头(100 毫秒)就结束(结束帧),落地帧是 2(200 毫秒):提前结束时一定落回地面,不留在半空
        using var arena = Open(One(new[] { Leaping(2, 2, 2, endFrame: 1) }));
        RoomCombatSession s = arena.Session;
        Run(s, 9, SkillKeys.A);
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        Assert.True(s.Hero.Z > 0.0);
        Run(s, 1);                                            // 第 10 步:100 毫秒,状态结束
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        Assert.Equal(0.0, s.Hero.Z);
        Assert.Equal(0, s.Hero.LeapLandings);                 // 没落地就结束,不算落地(不办落地块)
        Run(s, 30);
        Assert.Equal(0.0, s.Hero.Z);
    }

    // ------------------------------------------------------------------ 落点碰撞:逐句复刻客户端 0x00871250 的参考实现

    /// <summary>
    /// 客户端带碰撞的取位置(0x00871250,逐条指令复刻):入口值 cur 与目标 target 相等直接返回;否则方向由"入口的 cur 是否 ≥ target"定(≥ 减一、&lt; 加一),
    /// 从上一个接受的坐标起每次走一个像素,问"这个像素能站吗"(虚槽 +0xe0,横轴时带当前纵坐标),能站就接受并在到达目标时返回,不能站就返回上一个接受的坐标。
    /// </summary>
    private static int ClientWalk(int cur, int target, Func<int, bool> canStand)
    {
        if (cur == target)
        {
            return cur;
        }
        int accepted = cur;
        while (true)
        {
            int next = cur >= target ? accepted - 1 : accepted + 1;
            if (!canStand(next))
            {
                return accepted;
            }
            accepted = next;
            if (accepted == target)
            {
                return accepted;
            }
        }
    }

    [Fact]
    public void TheClientWalkPortStopsAtTheLastStandablePixelAndRejectsTheRestOfTheWay()
    {
        Func<int, bool> area = x => x >= 250 && x <= 330;
        Assert.Equal(330, ClientWalk(300, 400, area));        // 往右撞到右边界
        Assert.Equal(250, ClientWalk(300, 200, area));        // 往左撞到左边界
        Assert.Equal(320, ClientWalk(300, 320, area));        // 目标在区域内:走到
        Assert.Equal(300, ClientWalk(300, 300, area));        // 入口值等于目标:直接返回
        Assert.Equal(330, ClientWalk(330, 400, area));        // 已经站在边界上:第一个像素就不能站,留在原地
        Assert.Equal(200, ClientWalk(200, 100, area));        // 出生在区域外:第一个像素(199)也不能站,留在原地
    }

    [Fact]
    public void EveryLeapStepLandsWhereAnIntegerPixelWalkOfTheClientWouldLand()
    {
        var walk = new List<WorldRect> { new WorldRect(250, 90, 80, 20) };               // x 在 [250, 330],y 在 [90, 110],闭区间
        Func<int, bool> canStand = x => walk[0].Contains(x, 100);
        int scenarios = 0;
        foreach (bool facingLeft in new[] { false, true })
        {
            foreach (int distanceIndex in new[] { 2, 3 })                                  // 距离 +100 与 −100
            {
                foreach (int startX in new[] { 250, 280, 300, 329, 330 })
                {
                    using var arena = Open(One(new[] { Leaping(2, distanceIndex, 2) }), facingLeft: facingLeft, walk: walk);
                    RoomCombatSession s = arena.Session;
                    s.Hero.Teleport(startX, 100);
                    int distance = distanceIndex == 2 ? 100 : -100;
                    int refX = startX;
                    for (int step = 1; step <= 25; step++)
                    {
                        int e = 10 * step;
                        if (SkillLeapPlan.InFlight(e, 200))
                        {
                            int dx = SkillLeapPlan.OffsetAt(distance, e, 200);
                            int target = startX + (facingLeft ? -dx : dx);
                            refX = ClientWalk(refX, target, canStand);
                        }
                        s.Update(10, 0, 0, false, false, step == 1 ? SkillKeys.A : SkillKeys.None);
                        Assert.True(refX == (int)s.Hero.X && s.Hero.X == refX,
                            $"面向左={facingLeft} 距离={distance} 起点={startX} 第 {step} 步:参考 {refX},主角 {s.Hero.X}");
                    }
                    scenarios++;
                }
            }
        }
        Assert.Equal(20, scenarios);
    }

    [Fact]
    public void TheLeapStopsAtTheWalkAreaEdgeWhileTheHeightArcKeepsGoing()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }), walk: new List<WorldRect> { new WorldRect(250, 90, 80, 20) });     // 右边界 330(含)
        RoomCombatSession s = arena.Session;
        Run(s, 12, SkillKeys.A);
        Assert.Equal(330.0, s.Hero.X, 6);                     // 120 毫秒时偏移 60 → 360,被边界挡在 330
        Assert.True(s.Hero.Z > 0.0);                          // 水平被挡住不影响高度
        Run(s, 20);
        Assert.Equal(330.0, s.Hero.X, 6);
        Assert.Equal(1, s.Hero.LeapLandings);
    }

    // ------------------------------------------------------------------ 落地块:震屏、声音、特效、撤特效

    [Fact]
    public void TheLandingBlockShakesPlaysTheSoundsAndPlacesTheEffectsOnlyAtTheLandingStep()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing()) }));
        RoomCombatSession s = arena.Session;
        Run(s, 19, SkillKeys.A);
        Assert.Equal((false, 0, 0), (s.Shake.Active, s.Effects.Count, s.LeapLandings));
        Assert.Empty(Tags(s));                                // 空中:什么都还没办
        Run(s, 1);                                            // 第 20 步:200 毫秒,落地
        Assert.Equal(1, s.LeapLandings);
        Assert.True(s.Shake.Active);
        Assert.Equal(5, s.Shake.Strength);
        Assert.Equal(new[] { "TEST_LAND_A", "TEST_LAND_B" }, Tags(s));                 // 两个声音按配方顺序
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2 }, s.Effects.Select(e => e.Path).ToArray());
        Run(s, 10);
        Assert.Equal(1, s.LeapLandings);                      // 之后不重复办
        Assert.Empty(Tags(s));
    }

    [Fact]
    public void TheShakeLastsTheConfiguredMillisecondsAfterTheLanding()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing()) }));
        RoomCombatSession s = arena.Session;
        Run(s, 20, SkillKeys.A);
        Assert.True(s.Shake.Active);
        Run(s, 28);                                           // 落地那一步起又过了 280 毫秒:还在震
        Assert.True(s.Shake.Active);
        Run(s, 3);                                            // 过了 300 毫秒:停
        Assert.False(s.Shake.Active);
    }

    [Fact]
    public void TheLandingEffectsAreAtTheHeroFeetAndTheOffsetOneIsAheadAndBelowFollowingTheFacing()
    {
        foreach (bool facingLeft in new[] { false, true })
        {
            using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing()) }), facingLeft: facingLeft);
            RoomCombatSession s = arena.Session;
            Run(s, 20, SkillKeys.A);
            double heroX = s.Hero.X;
            AnimatedEffect feet = s.Effects.Single(e => e.Path == AvatarFixture.ChainEffect1);
            AnimatedEffect ahead = s.Effects.Single(e => e.Path == AvatarFixture.ChainEffect2);
            Assert.Equal((heroX, s.Hero.Y, 0.0), (feet.X, feet.Y, feet.Z));                          // 没有偏移:在主角脚下
            Assert.Equal((heroX + (facingLeft ? -50 : 50), s.Hero.Y, -10.0), (ahead.X, ahead.Y, ahead.Z));   // 向前 50(随朝向翻转)、向下 10
            Assert.Equal(facingLeft, ahead.FacingLeft);
        }
    }

    [Fact]
    public void AnEffectWithoutAnOffsetFollowsTheHeroAndTheOffsetOneStaysWhereItWasPlaced()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing(trimFrame: 3)) }));
        RoomCombatSession s = arena.Session;
        Run(s, 21, SkillKeys.A);
        AnimatedEffect feet = s.Effects.Single(e => e.Path == AvatarFixture.ChainEffect1);
        AnimatedEffect ahead = s.Effects.Single(e => e.Path == AvatarFixture.ChainEffect2);
        double aheadX = ahead.X;
        s.Hero.Teleport(s.Hero.X - 40, s.Hero.Y);
        Run(s, 1);
        Assert.Equal(s.Hero.X, feet.X, 6);                    // 跟随:每一步挪到主角位置
        Assert.Equal(aheadX, ahead.X, 6);                     // 不跟随:留在落地点
    }

    [Fact]
    public void TheTrimRemovesTheWatchedEffectAndItsGroupWhenTheWatchedEffectReachesTheFrame()
    {
        // 盯着特效 0,撤的帧是它自己动画的第 2 帧(从 200 毫秒起):落地那一步放出,每步老化 10 毫秒,第 20 步后(又过 20 步)盯着的已播 200 毫秒,下一步检查时撤
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing(trimFrame: 2)) }));
        RoomCombatSession s = arena.Session;
        Run(s, 20, SkillKeys.A);
        Assert.Equal(2, s.Effects.Count);
        Assert.Equal(1, s.LeapTrimJobCount);                  // 落地那一步记下一单
        Run(s, 19);                                           // 第 39 步:检查时盯着的特效已播 190 毫秒,不到
        Assert.Equal(2, s.Effects.Count);
        Assert.Equal(1, s.LeapTrimJobCount);
        Run(s, 1);                                            // 第 40 步:检查时已播 200 毫秒 = 第 2 帧起点:一起撤
        Assert.Empty(s.Effects);
        Assert.Equal(0, s.LeapTrimJobCount);                  // 撤完这一单就销掉,不留着空转
        Run(s, 30);
        Assert.Empty(s.Effects);                              // 不会再冒出来
        Assert.Equal(0, s.LeapTrimJobCount);
    }

    [Fact]
    public void ATrimFrameThatTheWatchedEffectNeverReachesLeavesTheEffectsToPlayOutOnTheirOwn()
    {
        // 特效只有 4 帧(第 0 到 3 帧),要求撤的帧是第 4 帧:客户端里帧号永远到不了,不撤;特效 350 毫秒后自己播完被清走
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing(trimFrame: 4)) }));
        RoomCombatSession s = arena.Session;
        Run(s, 20 + 33, SkillKeys.A);                         // 落地后又过 330 毫秒
        Assert.Equal(2, s.Effects.Count);
        Run(s, 5);                                            // 超过 350 毫秒
        Assert.Empty(s.Effects);
    }

    [Fact]
    public void AnEffectIndexOutsideThePreloadTableIsSkippedWhileTheRestOfTheLandingStillHappens()
    {
        // 预载表有两项(下标 0、1):下标 2(正好等于项数,边界)与下标 9(远超)都不在表里
        var landing = new SkillLeapLanding(5, 300, new[] { "TEST_LAND_A" }, new[] { new SkillLandingEffect(9), new SkillLandingEffect(2), new SkillLandingEffect(1) },
            new SkillEffectTrim(watchIndex: 9, frame: 2, removeIndices: new[] { 9, 1 }));
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: landing) }));
        RoomCombatSession s = arena.Session;
        Run(s, 20, SkillKeys.A);
        Assert.Equal(new[] { AvatarFixture.ChainEffect2 }, s.Effects.Select(e => e.Path).ToArray());   // 下标 2、9 预载表里没有:不放
        Assert.Equal(0, s.LeapTrimJobCount);                  // 盯着的特效(下标 9)没放出来:没有撤特效这一单
        Assert.True(s.Shake.Active);
        Assert.Equal(new[] { "TEST_LAND_A" }, Tags(s));
        Run(s, 40);                                           // 盯着的特效没放出来:没有撤特效这一单,特效自己播完
        Assert.Empty(s.Effects);
    }

    [Fact]
    public void ALeapWithoutALandingBlockStillCountsTheLandingButDoesNothingElse()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 25, SkillKeys.A);
        Assert.Equal((1, 1), (s.Hero.LeapLandings, s.LeapLandings));
        Assert.False(s.Shake.Active);
        Assert.Empty(s.Effects);
        Assert.Empty(Tags(s));
    }

    [Fact]
    public void ALandingWithoutAShakeNeitherStartsOneNorCancelsTheOneThatIsAlreadyRunning()
    {
        // 幅度 0(时长 300)与时长 0(幅度 5)都等于"不震":落地那一步不开新的震动,也不把此刻正在震的别的震动掐掉
        foreach ((int strength, int ms) in new[] { (0, 300), (5, 0) })
        {
            var landing = new SkillLeapLanding(strength, ms, new string[0], new SkillLandingEffect[0]);
            using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: landing) }));
            RoomCombatSession s = arena.Session;
            s.Shake.Start(3, 5000);                          // 别处已经在震(例如被打)
            Run(s, 20, SkillKeys.A);
            Assert.Equal(1, s.LeapLandings);
            Assert.True(s.Shake.Active);
            Assert.Equal(3, s.Shake.Strength);               // 还是原来那一个
        }
    }

    [Fact]
    public void ALandingThatHappenedBeforeTheSessionFirstSawTheHeroIsNotReplayed()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing()) }));
        RoomCombatSession s = arena.Session;
        // 主角先自己放一次前冲并落地(会话一步都没推进过),相当于带着已有的落地计数进到一个新房间
        Assert.True(s.Hero.BeginSkillMotion(AvatarFixture.ChainSkill, 2, 0.0, 0.0, leap: new SkillLeapPlan(100, 200, 2)));
        for (int i = 0; i < 25; i++)
        {
            s.Hero.Update(10, 0, 0);
        }
        Assert.Equal(1, s.Hero.LeapLandings);
        Run(s, 3);                                            // 会话第一次看到主角:只记下计数,之后几步也不补放(声音、震屏、特效都没有)
        Assert.Equal(0, s.LeapLandings);
        Assert.False(s.Shake.Active);
        Assert.Empty(s.Effects);
        Assert.Empty(Tags(s));
    }

    [Fact]
    public void ALandingStillHappensWhenTheSkillStateEndsOnTheLandingStep()
    {
        // 结束帧 2(200 毫秒)与落地帧 2 同一时刻:落地那一步技能同时结束(主角的技能号已清空),落地块用单独记下的技能号与段号照办
        using var arena = Open(One(new[] { Leaping(2, 2, 2, landing: Landing(), endFrame: 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 20, SkillKeys.A);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        Assert.Equal(0, s.Hero.SkillId);
        Assert.Equal((AvatarFixture.ChainSkill, 0), (s.Hero.LeapLandingSkillId, s.Hero.LeapLandingStage));
        Assert.Equal(1, s.LeapLandings);
        Assert.True(s.Shake.Active);
        Assert.Equal(new[] { "TEST_LAND_A", "TEST_LAND_B" }, Tags(s));
        Assert.Equal(2, s.Effects.Count);
        Assert.Equal(0.0, s.Hero.Z);
    }

    [Fact]
    public void TheLandingBlockOfTheSecondStageUsesThatStagesSpec()
    {
        // 第 0 段不是前冲,第 1 段是:落地块的事取第 1 段(单独记的段号)
        using var arena = Open(One(new[] { Plain(2), Leaping(3, 2, 2, landing: Landing()) }, autoChain: true));
        RoomCombatSession s = arena.Session;
        Run(s, 59, SkillKeys.A);
        Assert.Empty(Tags(s));
        Run(s, 1);                                            // 第 60 步:第 1 段 200 毫秒落地
        Assert.Equal(1, s.Hero.LeapLandingStage);
        Assert.Equal(new[] { "TEST_LAND_A", "TEST_LAND_B" }, Tags(s));
        Assert.True(s.Shake.Active);
    }

    // ------------------------------------------------------------------ 每一段开始的声音

    [Fact]
    public void TheStartSoundsOfAStagePlayOnceWhenThatStageBeginsAndEachStageUsesItsOwn()
    {
        SkillStageSpec first = Plain(2).WithStartSounds("TEST_START_A", "TEST_START_B");
        SkillStageSpec second = Plain(3).WithStartSounds("TEST_START_C");
        using var arena = Open(One(new[] { first, second }, autoChain: true));
        RoomCombatSession s = arena.Session;
        Run(s, 1, SkillKeys.A);
        Assert.Equal(new[] { "TEST_START_A", "TEST_START_B" }, Tags(s));               // 起手那一步按配方顺序
        Run(s, 38);                                           // 第 39 步:同一段不重复
        Assert.Empty(Tags(s));
        Run(s, 1);                                            // 第 40 步:第 0 段(400 毫秒)播完换第 1 段,放第 1 段自己的
        Assert.Equal(1, s.Hero.SkillStage);
        Assert.Equal(new[] { "TEST_START_C" }, Tags(s));
        Run(s, 20);
        Assert.Empty(Tags(s));
    }

    [Fact]
    public void AStageWithoutStartSoundsOrAnEmptyTagPlaysNothing()
    {
        using (var arena = Open(One(new[] { Plain(2) })))
        {
            Run(arena.Session, 5, SkillKeys.A);
            Assert.Empty(Tags(arena.Session));
        }
        using (var arena = Open(One(new[] { Plain(2).WithStartSounds("") })))             // 空串:客户端的"没有声音"(基础职业那一支就是空名),不放
        {
            Run(arena.Session, 5, SkillKeys.A);
            var all = new List<SoundCue>();
            arena.Session.DrainSounds(all);
            Assert.DoesNotContain(all, c => string.IsNullOrEmpty(c.Key));
        }
    }
}
