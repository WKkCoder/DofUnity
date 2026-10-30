using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 空中攻击强化类技能的主角一侧(M56,证据 E-150):客户端的跳跃攻击状态(7)带一个技能号参数时,这一击的动作是技能指定的 [etc motion] 项,
/// 其余(重力、水平运动、一跳只能挥一次、落地打断、攻击种类)与普通的空中攻击完全一样。
/// 数据是合成的(<see cref="AvatarFixture.JumpSkill"/>):两种动作 A(<see cref="AvatarFixture.JumpSkillAAni"/>,四帧 × 50 毫秒)与
/// B(<see cref="AvatarFixture.JumpSkillBAni"/>,三帧 × 60 毫秒);普通空中攻击是 <see cref="AvatarFixture.JumpAttackAni"/>(100 + 50 + 50 毫秒)。
/// </summary>
public class HeroJumpSkillTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();
    private const int Skill = AvatarFixture.JumpSkill;
    private const int EtcA = AvatarFixture.JumpSkillAEtcIndex;
    private const int EtcB = AvatarFixture.JumpSkillBEtcIndex;

    private static TownHero NewHero(DnfAssets assets, double x = 100)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, Unrestricted, x, 50, false, TownHero.DungeonRestTag);
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

    /// <summary>推进到回到站立(最多 3 秒)。</summary>
    private static void RunToRest(TownHero hero)
    {
        for (int i = 0; i < 300 && hero.Motion != HeroMotion.Rest; i++)
        {
            hero.Update(10, 0, 0);
        }
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void OnlyAnAirborneJumpThatHasNotAirAttackedYetCanBeginTheSkillSwing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 站在地上、蹲姿里(还没离地)都放不出来,状态不变
        TownHero hero = NewHero(assets);
        Assert.False(hero.BeginJumpAttackSkill(Skill, EtcA));
        Assert.Equal((HeroMotion.Rest, 0L, 0), (hero.Motion, hero.SwingNumber, hero.JumpSkillStarts));
        hero.Update(10, 0, 0, false, true);
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.False(hero.BeginJumpAttackSkill(Skill, EtcA));
        Assert.Equal((HeroMotion.Jump, 0L, 0), (hero.Motion, hero.SwingNumber, hero.JumpSkillStarts));

        // 腾空之后:技能号为 0、动作读不到都放不出来(状态不变);都对才放得出来
        for (int i = 0; i < 14; i++)
        {
            hero.Update(10, 0, 0);
        }
        Assert.True(hero.Z > 0);
        Assert.False(hero.BeginJumpAttackSkill(0, EtcA));
        Assert.False(hero.BeginJumpAttackSkill(Skill, 99));
        Assert.False(hero.BeginJumpAttackSkill(Skill, -1));
        Assert.Equal((HeroMotion.Jump, 0L), (hero.Motion, hero.SwingNumber));
        Assert.True(hero.HasEtcMotion(EtcA) && hero.HasEtcMotion(EtcB));
        Assert.False(hero.HasEtcMotion(99));

        Assert.True(hero.BeginJumpAttackSkill(Skill, EtcA));
        Assert.Equal((HeroMotion.JumpAttack, 1L, 1, Skill), (hero.Motion, hero.SwingNumber, hero.JumpSkillStarts, hero.JumpAttackSkillId));

        // 正在挥的时候不能再放;挥完回到跳跃之后这一跳的空中攻击也用掉了(与普通空中攻击一样一跳一次)
        Assert.False(hero.BeginJumpAttackSkill(Skill, EtcB));
        for (int i = 0; i < 25 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            hero.Update(10, 0, 0);
        }
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.True(hero.Z > 0);
        Assert.False(hero.BeginJumpAttackSkill(Skill, EtcA));
        hero.Update(10, 0, 0, true);                                                 // 攻击键也不再起空中攻击
        Assert.Equal((HeroMotion.Jump, 1L), (hero.Motion, hero.SwingNumber));

        // 后跳里不行(后跳不能空中攻击)
        TownHero backStepping = NewHero(assets);
        Assert.True(backStepping.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        backStepping.Update(60, 0, 0);
        Assert.True(backStepping.IsBackStep);
        Assert.False(backStepping.BeginJumpAttackSkill(Skill, EtcA));

        // 落地回到站立后再起跳,又放得出来;一跳放一次
        RunToRest(hero);
        Jump(hero, 20);
        Assert.True(hero.BeginJumpAttackSkill(Skill, EtcB));
        Assert.Equal((2, 2L), (hero.JumpSkillStarts, hero.SwingNumber));
    }

    [Fact]
    public void TheLandingSegmentOfAJumpCannotBeginTheSkillSwingEither()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Jump(hero, 15);
        Assert.True(hero.Z > 0);

        // 没有用过空中攻击的一跳:落到地面之后还有一段落地的硬直(动作仍是跳跃,高度 0),这时放不出来
        bool landing = false;
        for (int i = 0; i < 300 && hero.Motion == HeroMotion.Jump; i++)
        {
            hero.Update(10, 0, 0);
            if (hero.Z <= 0.0 && hero.Motion == HeroMotion.Jump)
            {
                landing = true;
                break;
            }
        }
        Assert.True(landing);
        Assert.False(hero.BeginJumpAttackSkill(Skill, EtcA));
        Assert.Equal((HeroMotion.Jump, 0, 0L), (hero.Motion, hero.JumpSkillStarts, hero.SwingNumber));
        RunToRest(hero);
        Assert.False(hero.BeginJumpAttackSkill(Skill, EtcA));                          // 回到站立也不行
    }

    [Fact]
    public void TheSkillSwingPlaysTheChosenEtcMotionAsAJumpAttackAndEndsBackInTheJump()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 普通空中攻击:三帧(100 + 50 + 50 毫秒),作对照
        TownHero plain = NewHero(assets);
        Jump(plain, 20);
        plain.Update(10, 0, 0, true);
        Assert.Equal((HeroMotion.JumpAttack, 0, 0), (plain.Motion, plain.JumpAttackSkillId, plain.JumpSkillStarts));
        Assert.Equal((AvatarFixture.JumpAttackAni, 200.0), (plain.CurrentAnimationPath, plain.JumpAttackDurationMs));

        // 换成动作 A:仍是空中攻击种类(命中、声音、受击规则都走空中攻击的),动画与时长是 A 的:四帧 × 50 毫秒
        TownHero hero = NewHero(assets);
        Jump(hero, 20);
        double z0 = hero.Z;
        Assert.True(hero.BeginJumpAttackSkill(Skill, EtcA));
        Assert.Equal((HeroMotion.JumpAttack, HeroAttackKind.Jump, true), (hero.Motion, hero.AttackKind, hero.IsAttacking));
        Assert.Equal((AvatarFixture.JumpSkillAAni, "jumpskilla.ani", 200.0), (hero.CurrentAnimationPath, hero.AttackMotionFile, hero.JumpAttackDurationMs));
        Assert.Equal(0, hero.CurrentFrame);
        var frames = new List<int>();
        for (int i = 0; i < 19; i++)
        {
            hero.Update(10, 0, 0);
            frames.Add(hero.CurrentFrame);
        }
        // 每步 10 毫秒:第 1 到 4 步在帧 0,第 5 到 9 步在帧 1(50 到 100 毫秒),依此类推;第 20 步(200 毫秒)才结束
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3 }, frames);
        Assert.Equal((HeroMotion.JumpAttack, Skill), (hero.Motion, hero.JumpAttackSkillId));
        hero.Update(10, 0, 0);
        Assert.Equal((HeroMotion.Jump, 0), (hero.Motion, hero.JumpAttackSkillId));
        Assert.NotEqual(AvatarFixture.JumpSkillAAni, hero.CurrentAnimationPath);
        Assert.Equal(1, hero.JumpSkillStarts);                                         // 累计计数不随挥击结束清零

        // 动作 B:三帧 × 60 毫秒
        TownHero other = NewHero(assets);
        Jump(other, 20);
        Assert.True(other.BeginJumpAttackSkill(Skill, EtcB));
        Assert.Equal((AvatarFixture.JumpSkillBAni, 180.0), (other.CurrentAnimationPath, other.JumpAttackDurationMs));
        for (int i = 0; i < 17; i++)
        {
            other.Update(10, 0, 0);
        }
        Assert.Equal(HeroMotion.JumpAttack, other.Motion);
        other.Update(10, 0, 0);
        Assert.Equal(HeroMotion.Jump, other.Motion);
        Assert.True(z0 > 0);
    }

    [Fact]
    public void TheSkillSwingFallsAndMovesExactlyLikeThePlainAirAttack()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero plain = NewHero(assets);
        TownHero skill = NewHero(assets);
        Jump(plain, 20);
        Jump(skill, 20);
        plain.Update(10, 1, 0, true);                       // 普通的空中攻击,按着右键走
        Assert.True(skill.BeginJumpAttackSkill(Skill, EtcA));
        skill.Update(10, 1, 0);
        for (int i = 0; i < 12; i++)
        {
            plain.Update(10, 1, 0);
            skill.Update(10, 1, 0);
            Assert.Equal(plain.X, skill.X, 9);
            Assert.Equal(plain.Y, skill.Y, 9);
            Assert.Equal(plain.Z, skill.Z, 9);
        }
        Assert.True(skill.X > 100.0);
    }

    [Fact]
    public void TheTakeoverCallbackIsAskedOnlyWhenAnAirAttackReallyStartsAndDecidesTheSwing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        int calls = 0;
        int answerEtc = EtcB;
        bool answer = true;
        hero.TakeOverJumpAttack = (TownHero h, out int id, out int etc) =>
        {
            calls++;
            id = Skill;
            etc = answerEtc;
            return answer;
        };

        // 地上按攻击键是普通攻击,蹲姿里按攻击键没有用:都不问
        hero.Update(10, 0, 0, true);
        Assert.Equal((HeroMotion.Attack, 0), (hero.Motion, calls));
        TownHero crouching = NewHero(assets);
        crouching.TakeOverJumpAttack = hero.TakeOverJumpAttack;
        crouching.Update(10, 0, 0, false, true);
        crouching.Update(10, 0, 0, true);
        Assert.Equal((HeroMotion.Jump, 0, 0L), (crouching.Motion, calls, crouching.SwingNumber));

        // 腾空后按攻击键:问一次,回答 true 就换成它指定的动作
        TownHero swinging = NewHero(assets);
        swinging.TakeOverJumpAttack = hero.TakeOverJumpAttack;
        Jump(swinging, 20);
        swinging.Update(10, 0, 0, false);
        Assert.Equal(0, calls);                                                       // 没按攻击键不问
        swinging.Update(10, 0, 0, true);
        Assert.Equal((1, HeroMotion.JumpAttack, Skill, AvatarFixture.JumpSkillBAni), (calls, swinging.Motion, swinging.JumpAttackSkillId, swinging.CurrentAnimationPath));
        Assert.Equal(1, swinging.JumpSkillStarts);

        // 挥击中、挥完之后(这一跳用掉了)再按攻击键:不再问
        for (int i = 0; i < 30; i++)
        {
            swinging.Update(10, 0, 0, true);
        }
        Assert.Equal((1, HeroMotion.Jump), (calls, swinging.Motion));

        // 回答 false:普通的空中攻击,没有技能痕迹,也不增加累计计数
        answer = false;
        TownHero declined = NewHero(assets);
        declined.TakeOverJumpAttack = hero.TakeOverJumpAttack;
        Jump(declined, 20);
        declined.Update(10, 0, 0, true);
        Assert.Equal((2, HeroMotion.JumpAttack, 0, AvatarFixture.JumpAttackAni), (calls, declined.Motion, declined.JumpAttackSkillId, declined.CurrentAnimationPath));
        Assert.Equal(0, declined.JumpSkillStarts);

        // 回答 true 但指定的动作读不到:退回普通的空中攻击(不留技能号)
        answer = true;
        answerEtc = 99;
        TownHero missing = NewHero(assets);
        missing.TakeOverJumpAttack = hero.TakeOverJumpAttack;
        Jump(missing, 20);
        missing.Update(10, 0, 0, true);
        Assert.Equal((3, HeroMotion.JumpAttack, 0, AvatarFixture.JumpAttackAni), (calls, missing.Motion, missing.JumpAttackSkillId, missing.CurrentAnimationPath));
        Assert.Equal(0, missing.JumpSkillStarts);

        // 没有回调时一直是普通的空中攻击
        TownHero none = NewHero(assets);
        Jump(none, 20);
        none.Update(10, 0, 0, true);
        Assert.Equal((0, AvatarFixture.JumpAttackAni), (none.JumpAttackSkillId, none.CurrentAnimationPath));
    }

    [Fact]
    public void AfterASkillSwingTheNextJumpsPlainAirAttackIsPlainAgainAndLandingInterruptsTheSkillSwing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 技能的一击结束、落地、再起跳:不带上一次的技能痕迹
        TownHero hero = NewHero(assets);
        Jump(hero, 20);
        Assert.True(hero.BeginJumpAttackSkill(Skill, EtcA));
        RunToRest(hero);
        Assert.Equal(0, hero.JumpAttackSkillId);
        Jump(hero, 20);
        hero.Update(10, 0, 0, true);
        Assert.Equal((HeroMotion.JumpAttack, 0, AvatarFixture.JumpAttackAni), (hero.Motion, hero.JumpAttackSkillId, hero.CurrentAnimationPath));

        // 快落地时放技能:落地打断,跳回落地段,技能号消失,动画回到跳跃的
        TownHero low = NewHero(assets);
        Jump(low, 20);
        double previous = low.Z;
        for (int i = 0; i < 300; i++)
        {
            low.Update(10, 0, 0);
            if (low.Z < previous && low.Z < 25 && low.Z > 10)
            {
                break;
            }
            previous = low.Z;
        }
        Assert.InRange(low.Z, 10.0, 25.0);
        Assert.True(low.BeginJumpAttackSkill(Skill, EtcA));
        for (int i = 0; i < 6 && low.Z > 0; i++)
        {
            low.Update(10, 0, 0);
        }
        Assert.Equal((0.0, HeroMotion.Jump, 0), (low.Z, low.Motion, low.JumpAttackSkillId));
        Assert.Equal(AvatarFixture.JumpAni, low.CurrentAnimationPath);
    }
}
