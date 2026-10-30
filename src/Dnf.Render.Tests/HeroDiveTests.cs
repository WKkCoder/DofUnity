using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 腾空悬停后俯冲类技能的主角一侧(M55,证据 E-149):只在腾空的跳跃状态里放;放出来动画定在空中攻击动作的最后一帧,速度清零、悬停一小段,
/// 之后水平沿朝向、竖直向下一起冲(速度 = 计划里的数 × 移动速度属性的千分比),碰到地面落地,落地后播一遍坐姿再回到站立。
/// 数据是合成的(<see cref="AvatarFixture.DiveSkill"/>):移动速度属性 400,所以水平 470 变成 188 像素/秒、竖直 −900 变成 −360 像素/秒;悬停 200 毫秒。
/// 竖直的积分与重力沿用跳跃的公式,落地时刻取一步内的精确触地时间(水平只走到那一刻)。
/// </summary>
public class HeroDiveTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();
    private static readonly SkillDivePlan Plan = new SkillDivePlan(200, 470, -900, AvatarFixture.DiveImpactMinHeight);
    private const int Skill = AvatarFixture.DiveSkill;
    private const double SpeedX = 470 * AvatarFixture.MoveSpeedStat / 1000.0;      // 188
    private const double SpeedZ = -900 * AvatarFixture.MoveSpeedStat / 1000.0;     // −360

    private static TownHero NewHero(DnfAssets assets, IReadOnlyList<WorldRect> area = null, double x = 100, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, area ?? Unrestricted, x, 50, facingLeft, TownHero.DungeonRestTag);
    }

    /// <summary>起跳并推进 <paramref name="steps"/> 个 10 毫秒(第一步按下跳跃键;前 10 步是蹲姿,之后腾空)。</summary>
    private static void Jump(TownHero hero, int steps)
    {
        hero.Update(10, 0, 0, false, true);
        for (int i = 1; i < steps; i++)
        {
            hero.Update(10, 0, 0);
        }
    }

    /// <summary>推进到俯冲落地(<see cref="TownHero.DiveLandings"/> 加了 1),每步 <paramref name="stepMs"/> 毫秒;返回过去的总毫秒数(最多 3 秒,超过算失败)。</summary>
    private static double RunToLanding(TownHero hero, double stepMs = 10)
    {
        int before = hero.DiveLandings;
        double t = 0;
        while (hero.DiveLandings == before && t < 3000)
        {
            hero.Update(stepMs, 0, 0);
            t += stepMs;
        }
        Assert.Equal(before + 1, hero.DiveLandings);
        return t;
    }

    [Fact]
    public void OnlyAnAirborneJumpCanBeginADive()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 站在地上、蹲姿里(还没离地)都放不出来,状态不变
        TownHero hero = NewHero(assets);
        Assert.False(hero.BeginSkillDive(Skill, Plan));
        Assert.Equal((HeroMotion.Rest, 0L), (hero.Motion, hero.SwingNumber));
        hero.Update(10, 0, 0, false, true);
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.False(hero.BeginSkillDive(Skill, Plan));
        Assert.Equal((HeroMotion.Jump, false, 0), (hero.Motion, hero.DiveAirborne, hero.DiveStarts));

        // 没有计划放不出来;腾空了才放得出来
        for (int i = 0; i < 14; i++)
        {
            hero.Update(10, 0, 0);                                           // 蹲 100 毫秒 + 腾空 50 毫秒
        }
        Assert.True(hero.Z > 0);
        Assert.False(hero.BeginSkillDive(Skill, null));
        Assert.Equal(HeroMotion.Jump, hero.Motion);

        // 空中挥击里放不出来(空中攻击是另一种状态);后跳里也不行
        TownHero swinging = NewHero(assets);
        Jump(swinging, 20);
        swinging.Update(10, 0, 0, true);
        Assert.Equal(HeroMotion.JumpAttack, swinging.Motion);
        Assert.False(swinging.BeginSkillDive(Skill, Plan));
        Assert.Equal(HeroMotion.JumpAttack, swinging.Motion);

        TownHero backStepping = NewHero(assets);
        Assert.True(backStepping.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        backStepping.Update(60, 0, 0);
        Assert.True(backStepping.IsBackStep);
        Assert.False(backStepping.BeginSkillDive(Skill, Plan));
        Assert.True(backStepping.IsBackStep);

        // 落地段(跳跃动画最后一帧)里也不行:落到地上的那一刻还在跳跃动作里
        TownHero landing = NewHero(assets);
        Jump(landing, 30);
        for (int i = 0; i < 300 && landing.Z > 0; i++)
        {
            landing.Update(10, 0, 0);
        }
        Assert.Equal((HeroMotion.Jump, 0.0), (landing.Motion, landing.Z));
        Assert.False(landing.BeginSkillDive(Skill, Plan));
    }

    [Fact]
    public void ADiveHoldsTheLastFrameOfTheAirAttackHoversFallingOnlyUnderGravityThenDivesAtTheScaledSpeeds()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, x: 100);
        Jump(hero, 30);                                                      // 蹲 100 毫秒 + 腾空 200 毫秒
        double z0 = hero.Z;
        double g = hero.JumpGravity;
        Assert.True(z0 > AvatarFixture.DiveImpactMinHeight + 30);
        long swing = hero.SwingNumber;

        Assert.True(hero.BeginSkillDive(Skill, Plan));
        Assert.Equal((HeroMotion.Skill, Skill, swing + 1), (hero.Motion, hero.SkillId, hero.SwingNumber));
        Assert.Equal((true, true, true, false), (hero.IsAttacking, hero.DiveAirborne, hero.FrameHeld, hero.IsDiving));
        Assert.Equal(HeroAttackKind.Skill, hero.AttackKind);
        Assert.Equal("jumpattack.ani", hero.AttackMotionFile);
        Assert.Equal(AvatarFixture.JumpAttackAni, hero.CurrentAnimationPath);
        Assert.Equal((2, 2), (hero.CurrentFrame, hero.StrikeFirstFrame));    // 三帧动画的最后一帧;命中从这一帧起判
        Assert.Equal(z0, hero.DiveStartHeight);
        Assert.Equal((0, 0), (hero.DiveStarts, hero.DiveLandings));

        // 悬停 190 毫秒:水平不动,竖直只受重力(速度从 0 起),动画一直是最后一帧
        for (int i = 0; i < 19; i++)
        {
            hero.Update(10, 1, 0);                                           // 按着方向键也没有用
        }
        Assert.Equal((100.0, false, 2), (hero.X, hero.IsDiving, hero.CurrentFrame));
        Assert.Equal(z0 - 0.5 * g * 0.19 * 0.19, hero.Z, 6);

        // 悬停到 200 毫秒的这一步:悬停的最后一步走完,开始俯冲,速度按移动速度属性换算,朝向没变
        hero.Update(10, 0, 0);
        Assert.Equal((true, 1, Skill), (hero.IsDiving, hero.DiveStarts, hero.DiveStartSkillId));
        Assert.Equal((SpeedX, SpeedZ), (hero.DiveSpeedX, hero.DiveSpeedZ));
        double zh = z0 - 0.5 * g * 0.2 * 0.2;
        Assert.Equal(100.0, hero.X);
        Assert.Equal(zh, hero.Z, 6);
        Assert.False(hero.FacingLeft);

        // 俯冲的一步:水平 188 像素/秒、竖直初速 −360(一次设成这个数,不是在悬停的下落速度上叠加)再加重力
        hero.Update(10, 0, 0);
        Assert.Equal(100 + SpeedX * 0.01, hero.X, 6);
        Assert.Equal(zh + SpeedZ * 0.01 - 0.5 * g * 0.01 * 0.01, hero.Z, 6);
        Assert.Equal(2, hero.CurrentFrame);

        // 落地:触地时刻是 zh + v t − g t² / 2 = 0 的正根,水平只走到那一刻;落地计数加 1、高度归零
        double touch = (SpeedZ + Math.Sqrt(SpeedZ * SpeedZ + 2 * g * zh)) / g;
        Assert.True(touch > 0.02);
        RunToLanding(hero);
        Assert.Equal((1, true, 0.0), (hero.DiveLandings, hero.DiveLandingImpact, hero.Z));
        Assert.Equal(Skill, hero.DiveLandingSkillId);
        Assert.Equal(100 + SpeedX * touch, hero.X, 6);
        Assert.Equal((false, false, false), (hero.DiveAirborne, hero.FrameHeld, hero.IsDiving));

        // 落地后进入坐姿:从头播 [sit motion](三帧 × 100 毫秒),仍算在放技能,播完回到站立
        Assert.Equal((HeroMotion.Skill, AvatarFixture.SitAni), (hero.Motion, hero.CurrentAnimationPath));
        Assert.Equal(0.0, hero.SkillSlideSpeed);
        Assert.InRange(hero.MotionMs, 0, 10);                                // 落地那一步里触地之后剩下的时间(不到一步)算进坐姿
        for (int i = 0; i < 40 && hero.Motion == HeroMotion.Skill; i++)
        {
            hero.Update(10, 0, 0);
        }
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0.0, hero.Z);
    }

    [Fact]
    public void ADiveBeginningLowLandsWhileHoveringAndTheThresholdDecidesTheImpact()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 刚离地就放:悬停期间已经落地,没有开始俯冲;起点高度不够,不放冲击
        TownHero low = NewHero(assets);
        Jump(low, 11);                                                       // 蹲 100 毫秒 + 腾空 10 毫秒
        Assert.True(low.Z > 0 && low.Z < AvatarFixture.DiveImpactMinHeight);
        Assert.True(low.BeginSkillDive(Skill, Plan));
        RunToLanding(low);
        Assert.Equal((0, 1, false, 0.0), (low.DiveStarts, low.DiveLandings, low.DiveLandingImpact, low.Z));
        Assert.Equal(100.0, low.X);                                          // 没有俯冲,水平一直没动
        Assert.Equal(AvatarFixture.SitAni, low.CurrentAnimationPath);

        // 同样的高度,计划里的最小高度很小时放冲击
        TownHero small = NewHero(assets);
        Jump(small, 11);
        Assert.True(small.BeginSkillDive(Skill, new SkillDivePlan(200, 470, -900, 1)));
        RunToLanding(small);
        Assert.True(small.DiveLandingImpact);

        // 边界:放出那一刻的高度取整(截断,不是四舍五入)后与最小高度比,等于就放("不小于"),差一像素就不放
        TownHero equal = NewHero(assets);
        Jump(equal, 11);
        int whole = (int)equal.Z;
        Assert.True(whole > 0 && equal.Z > whole);                          // 带小数部分,才分得出截断与四舍五入
        Assert.True(equal.BeginSkillDive(Skill, new SkillDivePlan(200, 470, -900, whole)));
        RunToLanding(equal);
        Assert.True(equal.DiveLandingImpact);

        TownHero above = NewHero(assets);
        Jump(above, 11);
        Assert.Equal(whole, (int)above.Z);
        Assert.True(above.BeginSkillDive(Skill, new SkillDivePlan(200, 470, -900, whole + 1)));
        RunToLanding(above);
        Assert.False(above.DiveLandingImpact);

        // 高处放而最小高度很大时不放冲击,但照样俯冲、水平走了一段
        TownHero high = NewHero(assets);
        Jump(high, 30);
        Assert.True(high.BeginSkillDive(Skill, new SkillDivePlan(200, 470, -900, 100000)));
        RunToLanding(high);
        Assert.Equal((1, 1, false), (high.DiveStarts, high.DiveLandings, high.DiveLandingImpact));
        Assert.True(high.X > 100.0);
    }

    [Fact]
    public void ADiveGoesAlongTheFacingStopsAtTheWalkableEdgeAndTheStepSizeDoesNotChangeTheResult()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        TownHero left = NewHero(assets, x: 300, facingLeft: true);
        Jump(left, 30);
        Assert.True(left.BeginSkillDive(Skill, Plan));
        RunToLanding(left);
        Assert.True(left.X < 300.0 && left.FacingLeft);                       // 朝左就往左冲

        // 可走区域的右边界在 110:冲到边界就停在那里(不受边界限制时这一次俯冲会走 25 像素以上)
        var area = new List<WorldRect> { new WorldRect(0, 0, 110, 100) };
        TownHero edge = NewHero(assets, area, x: 100);
        Jump(edge, 30);
        Assert.True(edge.BeginSkillDive(Skill, Plan));
        RunToLanding(edge);
        Assert.Equal(110.0, edge.X);

        // 步长不同(1 毫秒一步与 30 毫秒一步,后者有一步跨过悬停结束),落地之前的位置一致;落地的总时间相差不超过一步,落地位置一致
        TownHero fine = NewHero(assets);
        TownHero coarse = NewHero(assets);
        Jump(fine, 30);
        Jump(coarse, 30);
        Assert.True(fine.BeginSkillDive(Skill, Plan));
        Assert.True(coarse.BeginSkillDive(Skill, Plan));
        for (int i = 0; i < 240; i++)
        {
            fine.Update(1, 0, 0);
        }
        for (int i = 0; i < 8; i++)
        {
            coarse.Update(30, 0, 0);
        }
        Assert.Equal((true, true), (fine.IsDiving, coarse.IsDiving));
        Assert.Equal(fine.X, coarse.X, 6);
        Assert.Equal(fine.Z, coarse.Z, 6);
        double fineMs = 240 + RunToLanding(fine, 1);
        double coarseMs = 240 + RunToLanding(coarse, 30);
        Assert.InRange(coarseMs - fineMs, -1, 30);
        Assert.Equal(fine.X, coarse.X, 6);
    }

    [Fact]
    public void TheTimeLeftOverInTheLandingStepCountsInTheSitAnimationWhateverTheStepSize()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        (double Ms, long SitMs) Run(double stepMs)
        {
            TownHero hero = NewHero(assets);
            Jump(hero, 30);
            Assert.True(hero.BeginSkillDive(Skill, Plan));
            double t = RunToLanding(hero, stepMs);
            Assert.Equal((HeroMotion.Skill, AvatarFixture.SitAni), (hero.Motion, hero.CurrentAnimationPath));
            return (t, hero.AttackAnimationMs);
        }

        // 坐姿已经播的毫秒数 = 落地那一刻到这一步结束之间的时间,所以两种步长的坐姿进度之差就是两边总时间之差
        // (1 毫秒一步的那边剩余不到 1 毫秒,取整各差 1 以内)
        (double fineMs, long fineSit) = Run(1);
        double widest = 0;
        foreach (double step in new[] { 7.0, 13.0, 19.0, 23.0, 30.0 })
        {
            (double ms, long sit) = Run(step);
            double expected = ms - fineMs;
            Assert.InRange(sit - fineSit - expected, -2.0, 2.0);
            widest = Math.Max(widest, Math.Abs(expected));
        }
        Assert.True(widest >= 5.0);                      // 这几个步长里至少有一个在落地那一步剩了好几毫秒,测得出来
    }

    [Fact]
    public void ADiveInTheAirIsOnlyInterruptedByTheDownFlowAndNewSkillsStartClean()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 在空中被普通受击打不断(腾空走倒地流程,与别的空中技能一致)
        TownHero hero = NewHero(assets);
        Jump(hero, 30);
        Assert.True(hero.BeginSkillDive(Skill, Plan));
        Assert.False(hero.BeginHurt(200, 0, 0, true));
        Assert.Equal((HeroMotion.Skill, true), (hero.Motion, hero.DiveAirborne));

        // 倒地流程接管:高度保留、不再走俯冲的物理
        double z = hero.Z;
        Assert.True(hero.BeginDown());
        Assert.Equal((HeroMotion.Down, false, false), (hero.Motion, hero.DiveAirborne, hero.FrameHeld));
        Assert.Equal(z, hero.Z);
        hero.Update(100, 0, 0);
        Assert.Equal(z, hero.Z);
        hero.EndDown();

        // 俯冲结束后放别的技能:没有残留的定住帧与"从这一帧起判"
        TownHero next = NewHero(assets);
        Jump(next, 30);
        Assert.True(next.BeginSkillDive(Skill, Plan));
        RunToLanding(next);
        for (int i = 0; i < 40 && next.Motion == HeroMotion.Skill; i++)
        {
            next.Update(10, 0, 0);
        }
        Assert.Equal(HeroMotion.Rest, next.Motion);
        Assert.True(next.BeginSkillMotion(AvatarFixture.ChainSkill, 2, 0, 0));
        Assert.Equal((HeroMotion.Skill, false, 0, 0), (next.Motion, next.FrameHeld, next.StrikeFirstFrame, next.CurrentFrame));
        Assert.Equal((1, 1), (next.DiveStarts, next.DiveLandings));            // 计数永远递增,不被新技能清掉
    }
}
