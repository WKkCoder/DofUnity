using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 技能动画的关键帧标志与脚本升空的主角侧(M65,证据 E-163):动画走到带标志的帧就记一笔(带技能段号,一步走过几帧每个带标志的帧都给,每次更新开头清空),
/// 命中名单重武装;脚本升空时第 0 帧停住、高度走缓出弧、水平沿朝向的反方向后滑、升空时间一到停在最高点;技能结束从当时的高度落下来,
/// 空中受击打不断而倒地打得断。数据全是合成的:<see cref="AvatarFixture.FlagAni"/>(五帧 × 100 毫秒,第 1 帧标志 1、第 3 帧标志 2)与
/// <see cref="AvatarFixture.RiseAni"/>(第 0 帧停 10000 毫秒占位,第 1、2 帧 30 与 40 毫秒);升空参数是 (高度 40、升空 300 毫秒、后滑 30)。
/// </summary>
public class HeroKeyFlagTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, bool facingLeft = false, double x = 100, double y = 50)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, Unrestricted, x, y, facingLeft, TownHero.DungeonRestTag);
    }

    private static SkillRisePlan Plan(int height = AvatarFixture.RiseHeight, int ms = AvatarFixture.RiseMs, int back = AvatarFixture.RiseBack)
    {
        return new SkillRisePlan(height, ms, back);
    }

    /// <summary>升空段:动画第 0 帧停到升空时间结束(原速,动画时间等于墙钟)。</summary>
    private static TownHero Rising(DnfAssets assets, SkillRisePlan plan = null, bool facingLeft = false)
    {
        TownHero hero = NewHero(assets, facingLeft);
        plan ??= Plan();
        Assert.True(hero.BeginSkillMotion(AvatarFixture.RiseSkill, AvatarFixture.RiseEtcIndex, 0, 0, plan.DurationMs, -1, 0, plan));
        return hero;
    }

    /// <summary>推进 <paramref name="ms"/> 毫秒(每步 10),返回每一步之后的 (已过毫秒, 这一步报出的标志)。</summary>
    private static List<(int Elapsed, int Stage, int Flag)> Collect(TownHero hero, int ms)
    {
        var seen = new List<(int, int, int)>();
        for (int t = 10; t <= ms; t += 10)
        {
            hero.Update(10, 0, 0);
            foreach (SkillKeyFlag k in hero.KeyFlagsThisStep)
            {
                seen.Add((t, k.Stage, k.Flag));
            }
        }
        return seen;
    }

    // ------------------------------------------------------------------ 关键帧标志

    [Fact]
    public void AFlaggedFrameIsReportedOnceWhenTheAnimationArrivesAtIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0));

        var seen = Collect(hero, 450);
        Assert.Equal(2, seen.Count);
        // 第 1 帧 100 毫秒起、第 3 帧 300 毫秒起:停在帧上的那些步不重复报
        Assert.Equal((AvatarFixture.FlagA, 0), (seen[0].Flag, seen[0].Stage));
        Assert.InRange(seen[0].Elapsed, 100, 110);
        Assert.Equal((AvatarFixture.FlagB, 0), (seen[1].Flag, seen[1].Stage));
        Assert.InRange(seen[1].Elapsed, 300, 310);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
    }

    [Fact]
    public void TheFlagsStayUntilTheirOwnerConsumesThemAndAreClearedByTheNextUpdate()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0));
        hero.Update(110, 0, 0);                                       // 一步走到第 1 帧(100 毫秒起):标志 1
        Assert.Single(hero.KeyFlagsThisStep);
        Assert.Single(hero.KeyFlagsThisStep);                         // 外面没调 Update(命中停顿定住主角的那几步):还在,不会自己消失
        hero.ConsumeKeyFlags();
        Assert.Empty(hero.KeyFlagsThisStep);                          // 办完了就清掉:同一个标志不会被办第二遍
        hero.Update(10, 0, 0);
        Assert.Empty(hero.KeyFlagsThisStep);                          // 下一步没有新的标志帧:还是空
    }

    [Fact]
    public void FlagsOnFramesPastTheEndFrameAreNotReportedEvenWhenOneBigStepCrossesThem()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        // 状态码在第 2 帧开头(200 毫秒)请求站立;动画第 1 帧(100 毫秒起)标志 1、第 3 帧(300 毫秒起)标志 2
        Assert.True(hero.BeginSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0, -1, 2));
        hero.Update(150, 0, 0);
        Assert.Single(hero.KeyFlagsThisStep);                         // 标志 1
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        hero.Update(150, 0, 0);                                       // 150 到 300 毫秒的一大步:跨过结束帧也跨过第 3 帧,结束帧之后的帧不算
        Assert.Empty(hero.KeyFlagsThisStep);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void AStepThatCrossesSeveralFlaggedFramesReportsEachOfThemInOrder()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0));

        hero.Update(10, 0, 0);
        Assert.Empty(hero.KeyFlagsThisStep);
        hero.Update(350, 0, 0);                                      // 一大步从 10 走到 360 毫秒:跨过标志 1 的帧与标志 2 的帧,一个也不丢
        Assert.Equal(new[] { AvatarFixture.FlagA, AvatarFixture.FlagB }, hero.KeyFlagsThisStep.Select(k => k.Flag).ToArray());
        hero.Update(10, 0, 0);                                       // 下一步开头清空
        Assert.Empty(hero.KeyFlagsThisStep);
    }

    [Fact]
    public void EachChainedStageCountsItsFramesFromTheStartAndCarriesItsOwnStageNumber()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0));
        Assert.Equal(2, Collect(hero, 350).Count);
        Assert.True(hero.ChainSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0));     // 同一个动画再接一段:从头数帧

        var seen = Collect(hero, 200);
        Assert.Single(seen);
        Assert.Equal((AvatarFixture.FlagA, 1), (seen[0].Flag, seen[0].Stage));
        Assert.Equal(1, hero.SkillStage);
    }

    [Fact]
    public void NothingIsReportedOutsideSkillsAndForAnimationsWithoutFlags()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0);
        Assert.Empty(hero.KeyFlagsThisStep);
        hero.Update(10, 1, 0);                                       // 走路
        Assert.Empty(hero.KeyFlagsThisStep);

        TownHero other = NewHero(assets);
        Assert.True(other.BeginSkillMotion(AvatarFixture.ChainSkill, 2, 0, 0));                // 没有标志的技能动画
        Assert.Empty(Collect(other, 500));
    }

    [Fact]
    public void RearmingTheHitsBumpsOnlyTheRearmNumber()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.FlagSkill, AvatarFixture.FlagEtcIndex, 0, 0));
        long swing = hero.SwingNumber;
        long rearm = hero.HitRearmNumber;
        hero.RearmHits();
        hero.RearmHits();
        Assert.Equal((swing, rearm + 2), (hero.SwingNumber, hero.HitRearmNumber));
    }

    // ------------------------------------------------------------------ 脚本升空

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRiseFollowsTheEasedHeightAndSlidesBackwardAgainstTheFacing(bool facingLeft)
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Rising(assets, facingLeft: facingLeft);
        double startX = hero.X;
        Assert.True(hero.Rising);

        for (int e = 10; e <= AvatarFixture.RiseMs; e += 10)
        {
            hero.Update(10, 0, 0);
            int back = SkillHopPlan.IntLerp(0, AvatarFixture.RiseBack, e, AvatarFixture.RiseMs);
            Assert.Equal(SkillHopPlan.EasedLerp(0, AvatarFixture.RiseHeight, e, AvatarFixture.RiseMs, true), (int)hero.Z);
            Assert.Equal(startX + (facingLeft ? back : -back), hero.X);                // 朝右往左退、朝左往右退
            Assert.Equal(facingLeft, hero.FacingLeft);                                 // 朝向不变
        }
        Assert.Equal((double)AvatarFixture.RiseHeight, hero.Z);                        // 升空时间到:最高点、后滑到位
        Assert.Equal(startX + (facingLeft ? AvatarFixture.RiseBack : -AvatarFixture.RiseBack), hero.X);
        Assert.False(hero.Rising);
    }

    [Fact]
    public void TheHeldFirstFrameIsReleasedWhenTheRiseTimeIsUpAndThenTheRestOfTheFramesPlay()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Rising(assets);
        for (int e = 10; e < AvatarFixture.RiseMs; e += 10)
        {
            hero.Update(10, 0, 0);
            Assert.Equal(0, hero.CurrentFrame);                                        // 升空期间一直是第 0 帧(带攻击框的那一帧)
        }
        hero.Update(10, 0, 0);                                                         // 300 毫秒:第 1 帧
        Assert.Equal(1, hero.CurrentFrame);
        hero.Update(30, 0, 0);                                                         // 330 毫秒:第 2 帧
        Assert.Equal(2, hero.CurrentFrame);
    }

    [Fact]
    public void WhenTheRisingSkillEndsTheHeroFallsFromTheApexAndLandsAtRest()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Rising(assets);
        double startX = hero.X;
        for (int e = 10; e <= 370; e += 10)                                            // 300 + 30 + 40:动画播完
        {
            hero.Update(10, 0, 0);
        }
        // 动画一结束:从最高点起自由落体(跳跃动作的下落相位,速度 0 起),不能空中挥击
        Assert.Equal((HeroMotion.Jump, true, 0), (hero.Motion, hero.IsAirborne, hero.SkillId));
        Assert.InRange(hero.Z, 1.0, AvatarFixture.RiseHeight);
        double previous = hero.Z;
        hero.Update(10, 0, 0, attackPressed: true);
        Assert.Equal(HeroMotion.Jump, hero.Motion);                                    // 攻击键没有起空中攻击
        Assert.True(hero.Z < previous);
        Assert.Equal(startX - AvatarFixture.RiseBack, hero.X);                         // 落下去水平不动

        for (int i = 0; i < 400 && hero.Motion != HeroMotion.Rest; i++)
        {
            hero.Update(10, 0, 0);
            Assert.True(hero.Z <= previous + 1e-9);                                    // 一路只降不升
            previous = hero.Z;
        }
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.Z));
    }

    [Fact]
    public void ARiseOfZeroHeightEndsOnTheGroundWithoutAFall()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Rising(assets, Plan(height: 0));
        for (int e = 10; e <= 370; e += 10)
        {
            hero.Update(10, 0, 0);
        }
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.Z));
    }

    [Fact]
    public void AHitCannotInterruptTheRisingSkillButAKnockdownCanAndKeepsTheHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Rising(assets);
        for (int e = 10; e <= 150; e += 10)
        {
            hero.Update(10, 0, 0);
        }
        Assert.True(hero.Z > 0);
        Assert.False(hero.BeginHurt(100, 0, 0, true));                                 // 在空中:普通受击打不断
        Assert.Equal(HeroMotion.Skill, hero.Motion);

        double z = hero.Z;
        Assert.True(hero.BeginDown());                                                 // 倒地能打断,高度交给倒地流程
        Assert.Equal((HeroMotion.Down, z, false), (hero.Motion, hero.Z, hero.Rising));
        hero.EndDown();
        for (int e = 0; e < 600; e += 10)
        {
            hero.Update(10, 0, 0);                                                     // 之后再没有升空的残留:站着不动
        }
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.Z));
    }

    [Fact]
    public void ChainingToAStageWithoutARisePlanStopsTheRise()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Rising(assets);
        hero.Update(100, 0, 0);
        Assert.True(hero.Z > 0);
        Assert.True(hero.ChainSkillMotion(AvatarFixture.RiseSkill, 2, 0, 0));          // 接一段没有升空计划的动作:不再升空
        double z = hero.Z;
        hero.Update(100, 0, 0);
        Assert.Equal(z, hero.Z);
        Assert.False(hero.Rising);
    }
}
