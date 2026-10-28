using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 主角受击硬直(<see cref="HeroMotion.Hurt"/>,证据 E-086):被打中后放 [damage motion 1]、[damage motion 2](交替),定住调用方给的硬直毫秒数,
/// 期间先快后慢地被推开,硬直一到回到站立;会打断挥击与技能、腾空中不处理、不能在硬直里放技能;硬直是一块可以被命中停顿以外的时间推进的秒表。
/// 动画与角色脚本是合成的(<see cref="AvatarFixture.ActionChrPath"/> 带两段各一帧、停留 10000 毫秒的受击动画)。
/// </summary>
public class HeroHurtTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ActionChrPath, IReadOnlyList<WorldRect> area = null,
        double x = 300, double y = 100, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, area ?? Unrestricted, x, y, facingLeft, TownHero.DungeonRestTag);
    }

    private static void Steps(TownHero hero, int steps, int dirX = 0, int dirY = 0)
    {
        for (int i = 0; i < steps; i++)
        {
            hero.Update(10, dirX, dirY, false, false);
        }
    }

    [Fact]
    public void OnlyAScriptWithDamageMotionsCanBeHurt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.True(NewHero(assets).CanBeHurt);
        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);
        Assert.False(plain.CanBeHurt);
        Assert.False(plain.BeginHurt(300, 0, 0, true));                 // 没有受击动作:什么也不做
        Assert.Equal((HeroMotion.Rest, 0), (plain.Motion, plain.HurtCount));
    }

    [Fact]
    public void AHurtHeroStandsInTheDamagePoseForTheStunTimeThenReturnsToRest()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        Assert.True(hero.BeginHurt(300, 0, 0, true));
        Assert.Equal((HeroMotion.Hurt, true, false), (hero.Motion, hero.IsHurt, hero.IsAttacking));
        Assert.Equal(AvatarFixture.Damage1Ani, hero.CurrentAnimationPath);
        Assert.Equal(0, hero.CurrentFrame);
        Assert.Equal((300.0, 0.0, 1), (hero.HurtSpanMs, hero.HurtClockMs, hero.HurtCount));

        Steps(hero, 29, dirY: 1);                                       // 290 毫秒:还在硬直里,按着方向键也走不动
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.Equal((300.0, 100.0), (hero.X, hero.Y));
        Assert.Equal(290.0, hero.HurtClockMs, 6);
        Steps(hero, 1);                                                 // 满 300 毫秒:结束
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.False(hero.IsHurt);

        hero.Update(10, 1, 0, false, false);                            // 之后恢复操作
        Assert.Equal(HeroMotion.Move, hero.Motion);
    }

    [Fact]
    public void TheTwoDamageMotionsAlternateAndAHitWhileHurtRestartsTheStun()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.BeginHurt(200, 0, 0, false);
        Assert.Equal(AvatarFixture.Damage1Ani, hero.CurrentAnimationPath);
        Steps(hero, 15);
        Assert.Equal(150.0, hero.HurtClockMs, 6);
        hero.BeginHurt(200, 0, 0, false);                               // 硬直里又挨一下:换另一段动作,硬直重新计时
        Assert.Equal(AvatarFixture.Damage2Ani, hero.CurrentAnimationPath);
        Assert.Equal((0.0, 2), (hero.HurtClockMs, hero.HurtCount));
        Steps(hero, 19);
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Steps(hero, 1);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        hero.BeginHurt(100, 0, 0, false);                               // 第三次回到第一段
        Assert.Equal(AvatarFixture.Damage1Ani, hero.CurrentAnimationPath);
    }

    [Fact]
    public void TheKnockbackEasesOutAndTheHeroDoesNotTurnAround()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, facingLeft: false);

        // 往左推 40 像素,200 毫秒推完;位移比例 1 − (1 − t)²:100 毫秒时 75%
        hero.BeginHurt(500, 40, 200, true);
        Steps(hero, 10);
        Assert.Equal(300 - 40 * 0.75, hero.X, 6);
        Steps(hero, 10);
        Assert.Equal(300 - 40.0, hero.X, 6);
        Steps(hero, 10);
        Assert.Equal(300 - 40.0, hero.X, 6);                            // 推完就停,硬直还没结束
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.False(hero.FacingLeft);                                  // 受击不转向

        // 往右推;推的时间为 0 时一步到位
        TownHero other = NewHero(assets);
        other.BeginHurt(100, 10, 0, false);
        other.Update(10, 0, 0, false, false);
        Assert.Equal(310.0, other.X, 9);
    }

    [Fact]
    public void TheKnockbackStopsAtTheEdgeOfTheWalkableArea()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var area = new List<WorldRect> { new WorldRect(290, 0, 100, 200) };
        TownHero hero = NewHero(assets, area: area, x: 300, y: 100);
        hero.BeginHurt(500, 40, 100, true);
        Steps(hero, 12);
        Assert.InRange(hero.X, 289.9, 300.0);                           // 往左最多退到区域的左边界
        Assert.True(hero.X >= 290 - 1e-6);
    }

    [Fact]
    public void BeingHurtInterruptsASwingAndSkillsCannotBeUsedWhileHurt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, true, false);
        Assert.True(hero.IsAttacking);
        long swings = hero.SwingNumber;

        Assert.True(hero.BeginHurt(300, 0, 0, true));
        Assert.Equal((HeroMotion.Hurt, false, 0, 0), (hero.Motion, hero.IsAttacking, hero.AttackStage, hero.SkillId));
        Assert.Equal(swings, hero.SwingNumber);

        Steps(hero, 3, dirX: 1);
        Assert.False(hero.BeginSkillMotion(AvatarFixture.HardAttackSkill, 1, 0, 0));    // 硬直里放不了技能
        Assert.False(hero.BeginSkillWindup(AvatarFixture.HardAttackSkill, 0, 1, 1, 0, 0));
        Assert.False(hero.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));
        Assert.False(hero.BeginBackStep(AvatarFixture.BackStepSkill, 270));
        Assert.Equal(HeroMotion.Hurt, hero.Motion);

        // 硬直一结束又能放
        Steps(hero, 30);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.HardAttackSkill, 1, 0, 0));
    }

    [Fact]
    public void ARunningHeroStopsRunningWhenHurt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(50, 1, 0, false, false);
        hero.Update(50, 0, 0, false, false);
        hero.Update(50, 1, 0, false, false);
        Assert.True(hero.RunArmed);
        hero.BeginHurt(100, 0, 0, true);
        Assert.False(hero.RunArmed);
    }

    [Fact]
    public void AHeroOffTheGroundIsNotHurtButOneStillCrouchingForTheJumpIs()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);                             // 蹲姿:在跳跃状态里,但还没离地(z = 0)
        Assert.True(hero.IsAirborne);
        Assert.Equal(0.0, hero.Z);
        Assert.True(hero.BeginHurt(300, 0, 0, true));                   // 蹲姿被打断,回到地面的受击(证据 E-105)
        Assert.Equal((HeroMotion.Hurt, false, 1), (hero.Motion, hero.IsAirborne, hero.HurtCount));

        hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);
        for (int i = 0; i < 100 && hero.Z <= 0; i++)
        {
            hero.Update(10, 0, 0, false, false);
        }
        Assert.True(hero.Z > 0);
        Assert.False(hero.BeginHurt(300, 0, 0, true));                  // 离地之后被打走倒地流程(HeroKnockdown),不进入受击硬直
        Assert.True(hero.IsAirborne);
        Assert.Equal(0, hero.HurtCount);
    }

    [Fact]
    public void TheStunClockKeepsRunningDuringTheHitStopOutsideTheHeroUpdate()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.AddHurtClock(50);                                          // 不在受击里:什么也不做
        Assert.Equal(0.0, hero.HurtClockMs);

        hero.BeginHurt(100, 0, 0, true);
        hero.AddHurtClock(60);                                          // 命中停顿里主角不推进,秒表照走
        Assert.Equal(60.0, hero.HurtClockMs, 9);
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Steps(hero, 3);                                                 // 再 30 毫秒:90,还没完
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Steps(hero, 1);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void ABackwardKnockIntoAnExitBodyDoesNotCountAsWalkingIntoTheExit()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var area = new List<WorldRect> { new WorldRect(0, 0, 1000, 200) };
        TownHero hero = NewHero(assets, area: area, x: 400, y: 100);
        hero.Blocker = new Wall(410);                                   // x ≥ 410 是一堵墙
        hero.BeginHurt(300, 40, 100, false);                            // 往右推,会撞上去
        for (int i = 0; i < 12; i++)
        {
            hero.Update(10, 0, 0, false, false);
            Assert.Null(hero.PushedInto);                               // 被打退撞墙不触发出口
            Assert.False(hero.PushedUp);
            Assert.False(hero.PushedDown);
        }
        Assert.True(hero.X < 410);
    }

    [Fact]
    public void PlacingAHurtMotionJustStandsTheHeroUp()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Place(100, 50, true, HeroMotion.Hurt, 0);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void HoldingTheHurtPoseStartsAPushlessHurtOrKeepsTheRunningOneAndCanBeShortened()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        Assert.False(hero.HoldHurt(0, true));                           // 没有时长:什么也不做
        Assert.Equal((HeroMotion.Rest, 0), (hero.Motion, hero.HurtCount));

        Assert.True(hero.HoldHurt(300, true));                          // 不在受击里:开一次零硬直、不带击退的受击,留到 300 毫秒
        Assert.Equal((HeroMotion.Hurt, 1, 0.0, 300.0), (hero.Motion, hero.HurtCount, hero.HurtSpanMs, hero.HurtHoldEndMs));
        Assert.Equal(AvatarFixture.Damage1Ani, hero.CurrentAnimationPath);
        Steps(hero, 10);                                                // 100 毫秒:硬直早到点了,留住的还没
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.True(hero.HoldHurt(500, false));                         // 在受击里:重新定成此刻起 500 毫秒之后(100 + 500),不重新计时、不换动作
        Assert.Equal((1, 600.0, 100.0), (hero.HurtCount, hero.HurtHoldEndMs, hero.HurtClockMs));
        Assert.Equal(AvatarFixture.Damage1Ani, hero.CurrentAnimationPath);
        Assert.True(hero.HoldHurt(100, false));                         // 可以缩短(被摇脱):截止变成 100 + 100
        Assert.Equal(200.0, hero.HurtHoldEndMs);

        Steps(hero, 9, dirX: 1);                                        // 190 毫秒:按着方向键也走不动
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.Equal(300.0, hero.X);                                    // 没有击退
        Steps(hero, 1);
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.HurtHoldEndMs));
    }

    [Fact]
    public void TheHoldAndTheStunAreIndependentAndBothMustRunOutAndANewHitReleasesTheHold()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.BeginHurt(300, 0, 0, true);
        Assert.True(hero.HoldHurt(100, true));                          // 留得比硬直短:硬直决定
        Steps(hero, 29);
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Steps(hero, 1);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        hero.BeginHurt(100, 0, 0, true);
        Assert.True(hero.HoldHurt(500, true));                          // 留得比硬直长:留住的决定
        Steps(hero, 49);
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Steps(hero, 1);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        hero.BeginHurt(100, 0, 0, true);
        hero.HoldHurt(500, true);
        hero.BeginHurt(100, 0, 0, true);                                // 新的一击放开留住的:调用方随后再留
        Assert.Equal(0.0, hero.HurtHoldEndMs);
        hero.HoldHurt(500, true);
        Assert.False(hero.HoldHurt(0, true));                           // 不是正数就放开
        Assert.Equal(0.0, hero.HurtHoldEndMs);
        Steps(hero, 10);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void HoldingTheHurtPoseDoesNothingWithoutDamageMotionsOrOffTheGround()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);
        Assert.False(plain.HoldHurt(300, true));
        Assert.Equal(HeroMotion.Rest, plain.Motion);

        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);
        for (int i = 0; i < 100 && hero.Z <= 0; i++)
        {
            hero.Update(10, 0, 0, false, false);
        }
        Assert.True(hero.Z > 0);
        Assert.False(hero.HoldHurt(300, true));                         // 离地:下一次落地之后再接上
        Assert.Equal(0, hero.HurtCount);
    }

    /// <summary>x ≥ <see cref="From"/> 的地方是一堵墙。</summary>
    private sealed class Wall : IHeroBlocker
    {
        public readonly double From;

        public Wall(double from)
        {
            From = from;
        }

        public object BlockedBy(double x, double y)
        {
            return x >= From ? this : null;
        }
    }
}
