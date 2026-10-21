using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 主角倒地的"提线木偶"一侧(<see cref="HeroMotion.Down"/>,证据 E-105):主角自己不算倒地的运动(那是 Play 里与怪物共用的倒地状态机的事),
/// 只负责 [down motion] 的读取、进入时打断手上的事、被摆到状态机给的位置与动画时钟(<see cref="TownHero.SetDownPose"/>)、起身。
/// 动画与角色脚本是合成的(<see cref="AvatarFixture.DownAni"/>:80、10000、10000、400 毫秒四帧,前三帧站立受击框、躺着的一帧是扁框)。
/// </summary>
public class HeroDownPoseTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ActionChrPath, double x = 300, double y = 100, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, Unrestricted, x, y, facingLeft, TownHero.DungeonRestTag);
    }

    [Fact]
    public void OnlyAScriptWithADownMotionCanBeKnockedDown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.CanBeKnockedDown);
        Assert.NotNull(hero.DownAni);
        Assert.Equal(4, hero.DownAni.Frames.Length);

        foreach (string chr in new[] { AvatarFixture.HurtOnlyChrPath, AvatarFixture.ChrPath })
        {
            TownHero other = NewHero(assets, chr);
            Assert.False(other.CanBeKnockedDown);
            Assert.Null(other.DownAni);
            Assert.False(other.BeginDown());                              // 没有倒地动作:什么也不做
            Assert.Equal((HeroMotion.Rest, 0), (other.Motion, other.DownCount));
        }
    }

    [Fact]
    public void BeginningADownInterruptsAnAttackAndTheHeroIgnoresEveryInputWhileDown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, true, false);
        Assert.True(hero.IsAttacking);

        Assert.True(hero.BeginDown());
        Assert.Equal((HeroMotion.Down, true, false, 1), (hero.Motion, hero.IsDown, hero.IsAttacking, hero.DownCount));
        Assert.Equal(AvatarFixture.DownAni, hero.CurrentAnimationPath);
        Assert.Same(hero.DownAni, hero.CurrentAni);
        Assert.Equal(0, hero.CurrentFrame);

        // 方向、攻击、跳跃键都不起作用,位置与动作原样不动
        for (int i = 0; i < 50; i++)
        {
            hero.Update(10, 1, 1, i % 3 == 0, i % 5 == 0);
        }
        Assert.Equal((HeroMotion.Down, 300.0, 100.0, 0.0), (hero.Motion, hero.X, hero.Y, hero.Z));

        Assert.True(hero.BeginDown());                                    // 已经在倒地里:不重新开始、不重复计数
        Assert.Equal(1, hero.DownCount);
    }

    [Fact]
    public void BeginningADownInTheAirKeepsTheHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);
        for (int i = 0; i < 100 && hero.Z <= 0; i++)
        {
            hero.Update(10, 0, 0, false, false);
        }
        Assert.True(hero.IsAirborne);
        double z = hero.Z;
        Assert.True(z > 0);

        Assert.True(hero.BeginDown());
        Assert.Equal(HeroMotion.Down, hero.Motion);
        Assert.False(hero.IsAirborne);
        Assert.Equal(z, hero.Z, 9);                                       // 离地高度交给倒地状态机接着算
    }

    [Fact]
    public void ThePoseFollowsWhatTheFlightGivesAndTheFrameFollowsTheAnimationClock()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.SetDownPose(250, 30, true, 0);                               // 不在倒地里:什么也不做
        Assert.Equal((300.0, 0.0, false), (hero.X, hero.Z, hero.FacingLeft));

        hero.BeginDown();
        hero.SetDownPose(250, 30, true, 0);
        Assert.Equal((250.0, 100.0, 30.0, true), (hero.X, hero.Y, hero.Z, hero.FacingLeft));
        Assert.Equal(0, hero.CurrentFrame);
        hero.SetDownPose(250, 30, true, 80);                              // 第一个长停留帧从 80 毫秒起
        Assert.Equal((1, 80.0), (hero.CurrentFrame, hero.DownAnimationMs));
        hero.SetDownPose(250, 30, true, 10080);                           // 第二个长停留帧
        Assert.Equal(2, hero.CurrentFrame);
        hero.SetDownPose(250, 0, true, 20080);                            // 躺着的一帧
        Assert.Equal(3, hero.CurrentFrame);
        hero.SetDownPose(250, -5, true, -3);                              // 负数夹到 0
        Assert.Equal((0.0, 0.0, 0), (hero.Z, hero.DownAnimationMs, hero.CurrentFrame));
    }

    [Fact]
    public void GettingUpReturnsToRestOnTheGroundAndTheHeroTakesInputAgain()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.BeginDown();
        hero.SetDownPose(280, 12, false, 100);
        hero.EndDown();
        Assert.Equal((HeroMotion.Rest, false, 0.0, 280.0), (hero.Motion, hero.IsDown, hero.Z, hero.X));
        hero.EndDown();                                                   // 不在倒地里:什么也不做
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        hero.Update(10, 1, 0, false, false);
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Assert.Equal(1, hero.DownCount);
    }

    [Fact]
    public void ADownIsNeverPlacedDirectlyAndItBlocksSkillsHurtAndBackSteps()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Place(300, 100, false, HeroMotion.Down, 0);
        Assert.Equal(HeroMotion.Rest, hero.Motion);                       // 倒地的位置与时钟由倒地状态机给,不能直接摆出来

        hero.BeginDown();
        Assert.False(hero.BeginHurt(300, 30, 150, true));                 // 倒地里不走普通受击
        Assert.Equal(HeroMotion.Down, hero.Motion);
        Assert.False(hero.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));
        Assert.False(hero.BeginBackStep(AvatarFixture.BackStepSkill, 0));
        Assert.Equal(HeroMotion.Down, hero.Motion);
    }
}
