using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 技能一段动画播完时问一下"有没有下一段"的回调(M60,证据 E-154,客户端状态 0x20 的自动接续)。
/// 数据是合成的(<see cref="AvatarFixture.ChainSkill"/>):第一段 400 毫秒、第二段 300 毫秒两个动作;回调由测试自己写,
/// 只在主体阶段的动画自然播完那一刻被问到(被打断、起手阶段播完、动画还没播完都不问)。
/// </summary>
public class HeroFollowUpTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, double x = 100, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, Unrestricted, x, 50, facingLeft, TownHero.DungeonRestTag);
    }

    [Fact]
    public void WhenTheMotionPlaysOutTheHookIsAskedAndAChainedStageKeepsTheSkillGoing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        int a = hero.EtcMotionIndexOf("chaina.ani");
        int b = hero.EtcMotionIndexOf("chainb.ani");
        int calls = 0;
        hero.SkillFollowUp = h =>
        {
            calls++;
            return calls == 1 && h.ChainSkillMotion(AvatarFixture.ChainSkill, b, 0, 0);
        };

        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        long swings = hero.SwingNumber;
        hero.Update(399, 0, 0);
        Assert.Equal((0, 0, HeroMotion.Skill), (calls, hero.SkillStage, hero.Motion));        // 第一段 400 毫秒:没播完不问

        hero.Update(1, 0, 0);
        Assert.Equal((1, 1, "chainb.ani", HeroMotion.Skill), (calls, hero.SkillStage, hero.SkillMotionName, hero.Motion));
        Assert.Equal((swings + 1, 0L, 0), (hero.SwingNumber, hero.MotionMs, hero.CurrentFrame));    // 接上的这一段是新的一击,从头播

        hero.Update(299, 0, 0);
        Assert.Equal((1, HeroMotion.Skill), (calls, hero.Motion));
        hero.Update(1, 0, 0);                                                                  // 第二段 300 毫秒播完:再问一次,这次不接,技能结束
        Assert.Equal((2, HeroMotion.Rest, 0, 0), (calls, hero.Motion, hero.SkillId, hero.SkillStage));
    }

    [Fact]
    public void WithoutAHookOrWhenItDeclinesTheSkillEndsWhereItAlwaysDid()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        TownHero plain = NewHero(assets);
        int a = plain.EtcMotionIndexOf("chaina.ani");
        Assert.Null(plain.SkillFollowUp);
        Assert.True(plain.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        plain.Update(400, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0, 0), (plain.Motion, plain.SkillId, plain.SkillStage));

        TownHero declined = NewHero(assets);
        int asked = 0;
        declined.SkillFollowUp = _ =>
        {
            asked++;
            return false;
        };
        Assert.True(declined.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        declined.Update(400, 0, 0);
        Assert.Equal((1, HeroMotion.Rest, 0), (asked, declined.Motion, declined.SkillId));
    }

    [Fact]
    public void AHookThatChainsAMissingMotionLeavesTheSkillToEndNormally()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        int a = hero.EtcMotionIndexOf("chaina.ani");
        hero.SkillFollowUp = h => h.ChainSkillMotion(AvatarFixture.ChainSkill, 99, 0, 0);       // 动作读不到:换段失败,回调返回 false
        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        hero.Update(400, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0), (hero.Motion, hero.SkillId));
    }

    [Fact]
    public void AnInterruptedSkillAndAWindupThatEndsNeverAskTheHook()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 播到一半被打:受击姿势里不再问(技能已经被打断)
        TownHero hurt = NewHero(assets);
        int a = hurt.EtcMotionIndexOf("chaina.ani");
        int calls = 0;
        hurt.SkillFollowUp = _ =>
        {
            calls++;
            return true;
        };
        Assert.True(hurt.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        hurt.Update(100, 0, 0);
        Assert.True(hurt.BeginHurt(200, 0, 0, true));
        Assert.Equal(HeroMotion.Hurt, hurt.Motion);
        hurt.Update(2000, 0, 0);
        Assert.Equal((0, HeroMotion.Rest, 0), (calls, hurt.Motion, hurt.SkillId));

        // 起手阶段(先挥普攻再换动画)里普攻动画自己播完:这不是技能主体的结束,不问
        TownHero windup = NewHero(assets);
        int b = windup.EtcMotionIndexOf("chainb.ani");
        int windupCalls = 0;
        windup.SkillFollowUp = _ =>
        {
            windupCalls++;
            return true;
        };
        Assert.True(windup.BeginSkillWindup(AvatarFixture.UpperSlashSkill, 0, 99, b, 0, 0));
        Assert.True(windup.SkillWindingUp);
        windup.Update(2000, 0, 0);
        Assert.Equal((0, HeroMotion.Rest, 0), (windupCalls, windup.Motion, windup.SkillId));
    }
}
