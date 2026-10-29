using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 增益类技能的主角侧(M48,证据 E-141):角色脚本的 [buff motion]、原地施法的动作(不滑行、播完回到站立)、什么时候放不出来,
/// 以及跳跃力增益对起跳初速度与跳跃高度的影响。数据全是合成的:动作 <see cref="AvatarFixture.BuffAni"/>(三帧 × <see cref="AvatarFixture.BuffFrameMs"/> 毫秒,没有攻击框)。
/// </summary>
public class HeroBuffMotionTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ActionChrPath, double x = 100, double y = 50)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, Unrestricted, x, y, false, TownHero.DungeonRestTag);
    }

    private static void Run(TownHero hero, int ms, bool jumpFirst = false)
    {
        for (int t = 0; t < ms; t += 10)
        {
            hero.Update(10, 0, 0, false, jumpFirst && t == 0);
        }
    }

    [Fact]
    public void TheBuffMotionIsReadFromTheActionScriptAndAbsentElsewhere()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.True(NewHero(assets).HasBuffMotion);
        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);               // 没写 [buff motion] 的角色脚本
        Assert.False(plain.HasBuffMotion);
        Assert.False(plain.BeginBuffMotion(AvatarFixture.JumpBuffSkill));
        Assert.NotEqual(HeroMotion.Skill, plain.Motion);
    }

    [Fact]
    public void ACastPlaysTheBuffMotionInPlaceAndThenReturnsToStanding()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginBuffMotion(AvatarFixture.JumpBuffSkill));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.JumpBuffSkill, 100.0, 50.0), (hero.Motion, hero.SkillId, hero.X, hero.Y));
        Assert.Equal(AvatarFixture.BuffAni, hero.CurrentAnimationPath);

        // 三帧 × 100 毫秒 = 300 毫秒(原速):期间位置不动,方向键不起作用;播满回到站立
        Run(hero, 150);
        Assert.Equal((HeroMotion.Skill, 100.0, 50.0), (hero.Motion, hero.X, hero.Y));
        hero.Update(10, 1, 0);                                                 // 施法中按方向键:不走
        Assert.Equal((HeroMotion.Skill, 100.0, 50.0), (hero.Motion, hero.X, hero.Y));
        Run(hero, 300);
        Assert.NotEqual(HeroMotion.Skill, hero.Motion);
        Assert.Equal((100.0, 50.0), (hero.X, hero.Y));
    }

    [Fact]
    public void ACastIsRefusedWhileBusyAndLeavesTheStateAlone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 正在施放别的技能(或这个技能)时:不能叠加
        TownHero casting = NewHero(assets);
        Assert.True(casting.BeginBuffMotion(AvatarFixture.JumpBuffSkill));
        Assert.False(casting.BeginBuffMotion(AvatarFixture.MagicBuffSkill));
        Assert.Equal(AvatarFixture.JumpBuffSkill, casting.SkillId);

        // 腾空时不能
        TownHero air = NewHero(assets);
        Run(air, 10, jumpFirst: true);
        Run(air, 150);
        Assert.True(air.IsAirborne);
        Assert.False(air.BeginBuffMotion(AvatarFixture.JumpBuffSkill));
        Assert.True(air.IsAirborne);

        // 受伤、倒地时不能
        TownHero hurt = NewHero(assets);
        Assert.True(hurt.BeginHurt(300, 0, 0, true));
        Assert.True(hurt.IsHurt);
        Assert.False(hurt.BeginBuffMotion(AvatarFixture.JumpBuffSkill));
        Assert.True(hurt.IsHurt);
    }

    [Fact]
    public void ABuffCastInterruptedByAHitStopsPlayingTheMotion()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginBuffMotion(AvatarFixture.JumpBuffSkill));
        Run(hero, 100);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Assert.True(hero.BeginHurt(300, 0, 0, true));
        Assert.Equal(HeroMotion.Hurt, hero.Motion);                            // 施法被打断,不再是技能状态
    }

    [Fact]
    public void TheJumpPowerBonusRaisesTheTakeoffSpeedAndTheJumpApex()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero plain = NewHero(assets);
        TownHero boosted = NewHero(assets);
        Assert.Equal(0.0, boosted.JumpPowerBonus);
        Assert.Equal(plain.JumpTakeoffSpeed, boosted.JumpTakeoffSpeed, 9);

        boosted.JumpPowerBonus = 30;
        Assert.Equal(TownHero.TakeoffSpeed(plain.JumpPower + 30, plain.WeightStat, plain.JumpSpeedStat), boosted.JumpTakeoffSpeed, 9);
        Assert.True(boosted.JumpTakeoffSpeed > plain.JumpTakeoffSpeed);
        Assert.True(boosted.JumpApexHeight > plain.JumpApexHeight);

        // 实际起跳的最大高度:增益之后更高;增益是随时摆进来的,摘掉就回到原来的高度
        double plainPeak = Peak(plain);
        double boostedPeak = Peak(boosted);
        Assert.True(boostedPeak > plainPeak);
        boosted.JumpPowerBonus = 0;
        Assert.Equal(plainPeak, Peak(NewHero(assets)), 6);
    }

    private static double Peak(TownHero hero)
    {
        double peak = 0.0;
        for (int t = 0; t < 2000; t += 10)
        {
            hero.Update(10, 0, 0, false, t == 0);
            peak = Math.Max(peak, hero.Z);
            if (t > 100 && !hero.IsAirborne)
            {
                break;
            }
        }
        return peak;
    }
}
