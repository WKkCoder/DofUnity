using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 蓄力跳跃类技能的主角侧(M45,证据 E-138):三个插值函数的取整规则、脚本跳跃按动画时间摆位置与高度、落地一次、
/// 命中窗口的重武装计时、动画变速系数、空中不能被普通受击打断、技能结束与换技能时清干净。数据全是合成的:
/// 蓄力姿势 <see cref="AvatarFixture.HopReadyAni"/> 与跳跃 <see cref="AvatarFixture.HopAni"/>(七帧 × 100 毫秒,第 3 到 5 帧有攻击框,共 700 毫秒)。
/// 测试里的跳跃参数是 (距离 100、高度 60、窗口数 1、上升结束第 2 帧、落地第 4 帧、命中第 3 到 5 帧),原速时上升段 200 毫秒、下落段 200 毫秒。
/// </summary>
public class HeroHopTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, IReadOnlyList<WorldRect> area = null, double x = 100, double y = 50, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, area ?? Unrestricted, x, y, facingLeft, TownHero.DungeonRestTag);
    }

    private static SkillHopPlan Plan(int distance = 100, int height = 60, int hits = 1)
    {
        return new SkillHopPlan(distance, height, hits, riseFrame: 2, landFrame: 4, hitStartFrame: 3, hitEndFrame: 5);
    }

    /// <summary>蓄力姿势(第 5 项)开始,马上换成跳跃段(第 6 项):与会话里 BeginHop 做的一样。</summary>
    private static TownHero Hopping(DnfAssets assets, SkillHopPlan plan, double rateScale = 1.0, bool facingLeft = false, IReadOnlyList<WorldRect> area = null, int endFrame = -1)
    {
        TownHero hero = NewHero(assets, area: area, facingLeft: facingLeft);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.HopSkill, 5, 0, 0));
        Assert.True(hero.ChainSkillMotion(AvatarFixture.HopSkill, 6, 0, 0, -1, endFrame, plan, rateScale));
        return hero;
    }

    private static void Run(TownHero hero, int ms)
    {
        for (int t = 0; t < ms; t += 10)
        {
            hero.Update(10, 0, 0);
        }
    }

    // ------------------------------------------------------------------ 三个插值函数

    [Theory]
    [InlineData(0, 80, 50, 100, 40)]
    [InlineData(80, 100, 190, 200, 99)]
    [InlineData(0, 100, 1, 3, 33)]          // 33.33 向零取整
    [InlineData(0, -100, 1, 3, -33)]        // −33.33 向零取整(不是向下)
    [InlineData(5, 9, 0, 10, 5)]            // x ≤ 0 取起点
    [InlineData(5, 9, -4, 10, 5)]
    [InlineData(5, 9, 10, 10, 9)]           // x ≥ range 取终点
    [InlineData(5, 9, 99, 10, 9)]
    [InlineData(5, 9, 0, 0, 9)]             // 区间为 0:先判 x ≥ range,取终点
    public void TheIntegerLerpClampsAtBothEndsAndTruncatesTowardZero(int a, int b, int x, int range, int expected)
    {
        Assert.Equal(expected, SkillHopPlan.IntLerp(a, b, x, range));
    }

    [Fact]
    public void TheFloatLerpDoesNotRoundAndClampsLikeTheIntegerOne()
    {
        Assert.Equal(1.5f, SkillHopPlan.FloatLerp(1f, 3f, 1, 4));
        Assert.Equal(2.25f, SkillHopPlan.FloatLerp(1f, 3f, 5, 8));
        Assert.Equal(1f, SkillHopPlan.FloatLerp(1f, 3f, 0, 4));
        Assert.Equal(1f, SkillHopPlan.FloatLerp(1f, 3f, -3, 4));
        Assert.Equal(3f, SkillHopPlan.FloatLerp(1f, 3f, 4, 4));
        Assert.Equal(3f, SkillHopPlan.FloatLerp(1f, 3f, 40, 4));
    }

    [Theory]
    // 缓出(上升):从 a 出发,先快后慢;表里的数是按单精度逐步算出来的(整数部分,向零取整)
    [InlineData(0, 60, 0, 200, true, 0)]
    [InlineData(0, 60, 10, 200, true, 5)]
    [InlineData(0, 60, 50, 200, true, 26)]
    [InlineData(0, 60, 100, 200, true, 45)]
    [InlineData(0, 60, 150, 200, true, 56)]
    [InlineData(0, 60, 190, 200, true, 59)]
    [InlineData(0, 60, 200, 200, true, 60)]
    [InlineData(0, 60, -5, 200, true, 0)]
    // 缓入(下落):对称,先慢后快
    [InlineData(60, 0, 0, 200, false, 60)]
    [InlineData(60, 0, 10, 200, false, 59)]
    [InlineData(60, 0, 50, 200, false, 56)]
    [InlineData(60, 0, 100, 200, false, 45)]
    [InlineData(60, 0, 150, 200, false, 26)]
    [InlineData(60, 0, 190, 200, false, 5)]
    [InlineData(60, 0, 200, 200, false, 0)]
    public void TheEasedLerpFollowsTheParabolaAndTruncatesTowardZero(int a, int b, int x, int range, bool easeOut, int expected)
    {
        Assert.Equal(expected, SkillHopPlan.EasedLerp(a, b, x, range, easeOut));
    }

    [Fact]
    public void ThePlanKeepsAtLeastOneHitWindow()
    {
        Assert.Equal(1, new SkillHopPlan(100, 60, 0, 2, 4, 3, 5).Hits);
        Assert.Equal(1, new SkillHopPlan(100, 60, -3, 2, 4, 3, 5).Hits);
        Assert.Equal(3, Plan(hits: 3).Hits);
    }

    // ------------------------------------------------------------------ 位移与高度

    [Fact]
    public void AHopMovesAndRisesOnTheAnimationScheduleLandsOnceAndStaysPut()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hopping(assets, Plan());
        Assert.Equal((HeroMotion.Skill, 1, true, 100.0, 0.0), (hero.Motion, hero.SkillStage, hero.HopAirborne, hero.X, hero.Z));
        Assert.Equal(AvatarFixture.HopAni, hero.CurrentAnimationPath);

        // 上升段 200 毫秒:水平走全程的 0.8(80 像素),高度走缓出弧;下落段再 200 毫秒:走完余下 20 像素,高度走缓入弧
        var expected = new (int T, int Dx, int Z)[]
        {
            (10, 4, 5), (50, 20, 26), (100, 40, 45), (150, 60, 56), (190, 76, 59),
            (200, 80, 60), (250, 85, 56), (300, 90, 45), (350, 95, 26), (390, 99, 5),
        };
        int e = 0;
        foreach ((int t, int dx, int z) in expected)
        {
            Run(hero, t - e);
            e = t;
            Assert.Equal((100.0 + dx, (double)z, true), (hero.X, hero.Z, hero.HopAirborne));
        }

        // 400 毫秒是落地帧的开头:这一步高度归零、落一次地,水平位置停在最后摆的那个位置上
        Assert.Equal(0, hero.HopLandings);
        Run(hero, 10);
        Assert.Equal((0.0, 199.0, false, 1, 1), (hero.Z, hero.X, hero.HopAirborne, hero.HopLandings, hero.HopLandingHits));
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Assert.Equal(1, hero.HopHits);                                // 技能还没结束,落地之后窗口数还读得到

        // 落地以后一直站着放完动画(700 毫秒),不再移动、不再落地
        Run(hero, 290);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Run(hero, 10);
        Assert.Equal((HeroMotion.Rest, 199.0, 0.0, 1), (hero.Motion, hero.X, hero.Z, hero.HopLandings));
        Assert.Equal((0, 0), (hero.HopHits, hero.HopRearmsDone));
    }

    [Fact]
    public void AFasterAnimationCompressesTheWholeScheduleAndTheSkillEndsEarlier()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hopping(assets, Plan(), rateScale: 2.0);

        // 倍速:上升段 100 毫秒、下落段 100 毫秒,同一个时刻比例上位置与高度和原速时相同
        Run(hero, 50);
        Assert.Equal((140.0, 45.0), (hero.X, hero.Z));
        Assert.Equal(100, hero.AttackAnimationMs);                    // 动画时间是墙钟时间的两倍:50 毫秒已经放到第 1 帧(100 毫秒起)
        Assert.Equal(1, hero.CurrentFrame);
        Run(hero, 50);
        Assert.Equal((180.0, 60.0), (hero.X, hero.Z));
        Run(hero, 90);
        Assert.Equal((198.0, 11.0, true), (hero.X, hero.Z, hero.HopAirborne));
        Run(hero, 10);
        Assert.Equal((0.0, 198.0, 1), (hero.Z, hero.X, hero.HopLandings));

        // 整段动画 700 毫秒按倍速折成 350 毫秒:340 还在放,350 回到站立
        Run(hero, 140);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Run(hero, 10);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void AHopFacingLeftGoesLeftAndAWallStopsItAtTheBorder()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        TownHero free = Hopping(assets, Plan(), facingLeft: true);
        Run(free, 100);
        Assert.Equal((60.0, 45.0), (free.X, free.Z));
        Run(free, 600);
        Assert.Equal((HeroMotion.Rest, 1.0, 0.0), (free.Motion, free.X, free.Z));

        // 左边的墙在 x = 90:朝左跳的主角停在边界上(区域 x ∈ [90, 200]),高度照常走
        var area = new List<WorldRect> { new WorldRect(90, 0, 110, 100) };
        TownHero walled = Hopping(assets, Plan(), facingLeft: true, area: area);
        Run(walled, 100);
        Assert.Equal((90.0, 45.0), (walled.X, walled.Z));
        Run(walled, 600);
        Assert.Equal((HeroMotion.Rest, 90.0, 0.0), (walled.Motion, walled.X, walled.Z));
    }

    [Fact]
    public void ASkillThatEndsInTheAirComesDownToTheGround()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 状态在动画第 2 帧开头(200 毫秒)就结束:那一步还摆到了上升段的终点,随后一定落回地面
        TownHero hero = Hopping(assets, Plan(), endFrame: 2);
        Run(hero, 190);
        Assert.Equal((HeroMotion.Skill, 176.0, 59.0), (hero.Motion, hero.X, hero.Z));
        Run(hero, 10);
        Assert.Equal((HeroMotion.Rest, 180.0, 0.0), (hero.Motion, hero.X, hero.Z));
        Assert.False(hero.HopAirborne);
    }

    // ------------------------------------------------------------------ 命中窗口的重武装

    [Fact]
    public void TheRearmTimerFiresWindowsMinusOneTimesAtEvenIntervalsFromTheFirstHitFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hopping(assets, Plan(hits: 3));
        long swing = hero.SwingNumber;
        Assert.Equal((3, 0, 0L), (hero.HopHits, hero.HopRearmsDone, hero.HitRearmNumber));

        // 命中起始帧(第 3 帧)300 毫秒、结束帧(第 5 帧)500 毫秒:间隔 (500 − 300) / 3 = 66,第一次 366、第二次 432
        Run(hero, 360);
        Assert.Equal((0, 0L), (hero.HopRearmsDone, hero.HitRearmNumber));
        Run(hero, 10);
        Assert.Equal((1, 1L), (hero.HopRearmsDone, hero.HitRearmNumber));
        Run(hero, 60);
        Assert.Equal((1, 1L), (hero.HopRearmsDone, hero.HitRearmNumber));
        Run(hero, 10);
        Assert.Equal((2, 2L), (hero.HopRearmsDone, hero.HitRearmNumber));
        Run(hero, 200);
        Assert.Equal((2, 2L), (hero.HopRearmsDone, hero.HitRearmNumber));        // 只响窗口数减一次
        Assert.Equal(swing, hero.SwingNumber);                                    // 重武装不算新的一击
    }

    [Fact]
    public void TheRearmTimerScalesWithTheAnimationRateAndASingleWindowNeverRearms()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 倍速 2:起始帧 150、结束帧 250,间隔 (250 − 150) / 2 = 50,响一次在 200
        TownHero fast = Hopping(assets, Plan(hits: 2), rateScale: 2.0);
        Run(fast, 190);
        Assert.Equal(0L, fast.HitRearmNumber);
        Run(fast, 10);
        Assert.Equal(1L, fast.HitRearmNumber);

        TownHero single = Hopping(assets, Plan(hits: 1));
        Run(single, 700);
        Assert.Equal(0L, single.HitRearmNumber);
    }

    // ------------------------------------------------------------------ 受击、结束与换技能

    [Fact]
    public void AnAirborneHopCannotBeHurtButOneThatHasLandedCan()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hopping(assets, Plan());
        Run(hero, 100);
        Assert.True(hero.HopAirborne);
        Assert.False(hero.BeginHurt(500, 20, 100, true));
        Assert.Equal((HeroMotion.Skill, 0), (hero.Motion, hero.HurtCount));

        // 落地之后(技能动画还在放)在地面上,可以被打断
        Run(hero, 310);
        Assert.Equal((HeroMotion.Skill, 0.0, false), (hero.Motion, hero.Z, hero.HopAirborne));
        Assert.True(hero.BeginHurt(500, 20, 100, true));
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.Equal((0, 0), (hero.HopHits, hero.HopRearmsDone));
    }

    [Fact]
    public void TheRateScaleAndTheHopPlanDoNotLeakIntoTheNextSkill()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hopping(assets, Plan(hits: 3), rateScale: 2.0);
        Run(hero, 350);
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.Z));
        long rearms = hero.HitRearmNumber;

        // 接着放一个普通的技能动作(第 2 项 400 毫秒):不变速、没有位移与重武装
        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, 2, 0, 0));
        double x = hero.X;
        Run(hero, 390);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Assert.Equal((x, 0.0, 0, rearms), (hero.X, hero.Z, hero.HopHits, hero.HitRearmNumber));
        Assert.Equal(390, hero.AttackAnimationMs);
        Run(hero, 10);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 没带跳跃参数的接段(普通的多段技能)从头到尾没有这些
        TownHero chain = NewHero(assets);
        Assert.True(chain.BeginSkillMotion(AvatarFixture.ChainSkill, 2, 0, 0));
        Assert.True(chain.ChainSkillMotion(AvatarFixture.ChainSkill, 3, 0, 0));
        Run(chain, 100);
        Assert.Equal((0, 0, false, 0, 0L), (chain.HopHits, chain.HopRearmsDone, chain.HopAirborne, chain.HopLandings, chain.HitRearmNumber));
    }
}
