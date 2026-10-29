using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 多段命中计时器的主角侧(M46,证据 E-139):放技能时给了命中次数 N,计时器间隔 = 整段动画时长(按播放倍率折算)/ N(整数除法)、最多响 N 次,
/// 在动画开始后第一次更新时才起算,每次响铃都从"此刻"重新数一个间隔;每响一次命中名单重新武装(<see cref="TownHero.HitRearmNumber"/> 加 1),
/// 重武装不算新的一击。数据是合成的:跳跃动作 <see cref="AvatarFixture.HopAni"/>(七帧 × 100 毫秒,共 700 毫秒)放在 [etc motion] 第 6 项。
/// </summary>
public class HeroMultiHitTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, Unrestricted, 100, 50, false, TownHero.DungeonRestTag);
    }

    private static void Run(TownHero hero, int ms)
    {
        for (int t = 0; t < ms; t += 10)
        {
            hero.Update(10, 0, 0);
        }
    }

    [Fact]
    public void TheTimerStartsAtTheFirstUpdateAndRestartsFromNowOnEveryRing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.HopSkill, 6, 0, 0, rearmWindows: 3));
        long swing = hero.SwingNumber;
        Assert.Equal((3, 0, 0L), (hero.StageRearmsMax, hero.StageRearmsDone, hero.HitRearmNumber));

        // 间隔 700 / 3 = 233;第一次更新(10 毫秒)起算,到点在 243,按 10 毫秒一步是 250;第二次从 250 重新数,到点在 483,按步是 490;
        // 第三次到点在 723,动画 700 毫秒就结束了,响不到
        Run(hero, 240);
        Assert.Equal((0, 0L), (hero.StageRearmsDone, hero.HitRearmNumber));
        Run(hero, 10);
        Assert.Equal((1, 1L), (hero.StageRearmsDone, hero.HitRearmNumber));
        Run(hero, 230);
        Assert.Equal((1, 1L), (hero.StageRearmsDone, hero.HitRearmNumber));
        Run(hero, 10);
        Assert.Equal((2, 2L), (hero.StageRearmsDone, hero.HitRearmNumber));
        Run(hero, 200);
        Assert.Equal((HeroMotion.Skill, 2, 2L), (hero.Motion, hero.StageRearmsDone, hero.HitRearmNumber));
        Assert.Equal(swing, hero.SwingNumber);                                    // 重武装不算新的一击

        Run(hero, 20);                                                            // 技能结束:计时器清掉,累计的重武装次数留着
        Assert.Equal((HeroMotion.Rest, 0, 0, 2L), (hero.Motion, hero.StageRearmsDone, hero.StageRearmsMax, hero.HitRearmNumber));
    }

    [Fact]
    public void TheIntervalFollowsTheAnimationRateAndASingleWindowNeverRings()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 倍速 2:动画只要 350 毫秒,间隔 350 / 2 = 175;从 10 毫秒起算,到点在 185,按步是 190;下一次在 365,动画已经结束
        TownHero fast = NewHero(assets);
        fast.AttackAnimationRate = 2.0;
        Assert.True(fast.BeginSkillMotion(AvatarFixture.HopSkill, 6, 0, 0, rearmWindows: 2));
        Run(fast, 180);
        Assert.Equal(0L, fast.HitRearmNumber);
        Run(fast, 10);
        Assert.Equal(1L, fast.HitRearmNumber);
        Run(fast, 400);
        Assert.Equal((HeroMotion.Rest, 1L), (fast.Motion, fast.HitRearmNumber));

        // 次数 1:间隔就是整段动画,第一次到点在动画结束之后
        TownHero single = NewHero(assets);
        Assert.True(single.BeginSkillMotion(AvatarFixture.HopSkill, 6, 0, 0, rearmWindows: 1));
        Assert.Equal(1, single.StageRearmsMax);
        Run(single, 800);
        Assert.Equal((HeroMotion.Rest, 0L), (single.Motion, single.HitRearmNumber));
    }

    [Fact]
    public void WithoutAWindowCountThereIsNoTimerAndChainingToAnotherStageClearsIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        TownHero plain = NewHero(assets);
        Assert.True(plain.BeginSkillMotion(AvatarFixture.HopSkill, 6, 0, 0));
        Assert.Equal((0, 0), (plain.StageRearmsMax, plain.StageRearmsDone));
        Run(plain, 800);
        Assert.Equal(0L, plain.HitRearmNumber);

        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.HopSkill, 5, 0, 0, rearmWindows: 4));
        Assert.Equal(4, hero.StageRearmsMax);
        Run(hero, 100);
        Assert.True(hero.ChainSkillMotion(AvatarFixture.HopSkill, 6, 0, 0));        // 下一段没给次数:计时器清掉
        Assert.Equal((0, 0), (hero.StageRearmsMax, hero.StageRearmsDone));
        Run(hero, 800);
        Assert.Equal(0L, hero.HitRearmNumber);
    }
}
