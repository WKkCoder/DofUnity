using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 技能快捷槽与冷却(证据 E-076)。数据是合成的:角色脚本的初始技能表 [skill] 是 "181 2、5 1、46 3、169 1、174 1"(181 与 174 是被动),
/// 三个主动技能的 .skl 带 [command]、[cool time]、[consume MP]、[maximum level] 与 [static data](数是测试用的,见 <see cref="AvatarFixture"/>)。
/// </summary>
public class HeroSkillsTests
{
    private static HeroSkills Load(DnfAssets assets)
    {
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        return HeroSkills.Create(data, data.GetCharacter(0), 0);
    }

    private static TownHero NewHero(DnfAssets assets, double x = 100, double y = 50, bool facingLeft = false, string chr = null)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr ?? AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), x, y, facingLeft, TownHero.DungeonRestTag);
    }

    [Fact]
    public void TheInitialSkillTableFillsTheThreeQuickSlotsWithTheActiveCommandSkillsInOrder()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(assets);

        // 被动技能(181、174)没有 [command],不占槽;其余三个按出现顺序放进 A、S、D
        Assert.Equal(3, skills.Slots.Count);
        Assert.Equal(new[] { SkillKeys.A, SkillKeys.S, SkillKeys.D }, skills.Slots.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { AvatarFixture.HardAttackSkill, AvatarFixture.UpperSlashSkill, AvatarFixture.BackStepSkill }, skills.Slots.Select(s => s.SkillId).ToArray());
        Assert.Equal(new[] { 1, AvatarFixture.UpperSlashLevel, 1 }, skills.Slots.Select(s => s.Level).ToArray());
        Assert.Same(skills.Slots[1], skills.SlotOf(SkillKeys.S));
        Assert.Null(skills.SlotOf(SkillKeys.None));

        // 没有角色脚本时没有槽;只有已经实现放法的技能能放
        Assert.Empty(HeroSkills.Create(new GameData(new PvfScriptSource(assets.Pvf)), null, 0).Slots);
        Assert.True(HeroSkills.IsImplemented(AvatarFixture.HardAttackSkill));
        Assert.True(HeroSkills.IsImplemented(AvatarFixture.UpperSlashSkill));
        Assert.True(HeroSkills.IsImplemented(AvatarFixture.BackStepSkill));
        Assert.False(HeroSkills.IsImplemented(999));
    }

    [Fact]
    public void CooldownAndMpComeFromTheSkillScriptByLevel()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(assets);

        // 上挑 3 级:冷却 2000→1000 在 10 级里线性插值,第 3 级 = 2000 − 1000 × 2 / 9 = 1777.8 取整 1778;耗蓝 5→35 同理得 11.7 取整 12(放出来时的扣蓝见 HeroMpTests)
        HeroSkillSlot upper = skills.SlotOf(SkillKeys.S);
        Assert.Equal(1778, upper.CooldownMs);
        Assert.Equal(12, upper.ConsumeMp);
        // 鬼斩 1 级:冷却两个数相同;后跳没有 [cool time],冷却 0
        Assert.Equal((AvatarFixture.HardAttackCool, AvatarFixture.HardAttackMp), (skills.SlotOf(SkillKeys.A).CooldownMs, skills.SlotOf(SkillKeys.A).ConsumeMp));
        Assert.Equal((0, AvatarFixture.BackStepMp), (skills.SlotOf(SkillKeys.D).CooldownMs, skills.SlotOf(SkillKeys.D).ConsumeMp));
        Assert.All(skills.Slots, s => Assert.True(s.Ready));
    }

    [Fact]
    public void CastingStartsTheCooldownWhichCountsDownAndNeverGoesBelowZero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(assets);
        TownHero hero = NewHero(assets);
        HeroSkillSlot upper = skills.SlotOf(SkillKeys.S);

        Assert.Same(upper, skills.TryCast(SkillKeys.S, hero));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.UpperSlashSkill), (hero.Motion, hero.SkillId));
        Assert.Equal(1778.0, upper.CooldownLeftMs);
        Assert.False(upper.Ready);
        Assert.Equal(1.0, upper.CooldownFraction, 9);

        skills.Advance(778);
        Assert.Equal(1000.0, upper.CooldownLeftMs);
        Assert.Equal(1000.0 / 1778, upper.CooldownFraction, 9);
        skills.Advance(0);                                              // 不走时间不变
        skills.Advance(-5);
        Assert.Equal(1000.0, upper.CooldownLeftMs);

        // 冷却中:挥完回到站立之后也放不出来
        hero.Update(1000, 0, 0);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(SkillCastResult.Cooldown, skills.Cast(upper, hero));
        Assert.Null(skills.TryCast(SkillKeys.S, hero));
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        skills.Advance(5000);
        Assert.Equal(0.0, upper.CooldownLeftMs);
        Assert.True(upper.Ready);
        Assert.Equal(0.0, upper.CooldownFraction);
        Assert.Same(upper, skills.TryCast(SkillKeys.S, hero));          // 冷却好了又能放
    }

    [Fact]
    public void ASkillThatIsNotImplementedOrWhileTheHeroIsBusyDoesNotCastAndDoesNotStartACooldown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills loaded = Load(assets);
        // A 槽换成一个没有实现的技能(编号 999),S、D 用真的
        var stub = new HeroSkillSlot(SkillKeys.A, 999, 1, null);
        var skills = new HeroSkills(new[] { stub, loaded.SlotOf(SkillKeys.S), loaded.SlotOf(SkillKeys.D) });
        TownHero hero = NewHero(assets);
        HeroSkillSlot back = skills.SlotOf(SkillKeys.D);

        // 没实现的技能:按键没有反应,冷却不动
        Assert.Equal(SkillCastResult.NotImplemented, skills.Cast(stub, hero));
        Assert.Null(skills.TryCast(SkillKeys.A, hero));
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, stub.CooldownLeftMs));
        Assert.Equal(SkillCastResult.NotBound, skills.Cast(null, hero));
        Assert.Null(skills.TryCast(SkillKeys.None, hero));
        Assert.Null(skills.TryCast(SkillKeys.S, null));

        // 同一步按下几个键:按槽的次序试,第一个能放的放出来(A 放不了,轮到 S)
        Assert.Same(skills.SlotOf(SkillKeys.S), skills.TryCast(SkillKeys.A | SkillKeys.S | SkillKeys.D, hero));
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Assert.True(back.Ready);                                        // D 没有被一并放出

        // 放着上挑的时候后跳放不出来(忙),也不起冷却
        Assert.Equal(SkillCastResult.Busy, skills.Cast(back, hero));
        Assert.Equal(0.0, back.CooldownLeftMs);
    }

    [Fact]
    public void BackStepTakesItsTakeoffPowerFromTheStaticDataAndHasNoCooldown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(assets);
        TownHero hero = NewHero(assets, x: 300, facingLeft: true);

        Assert.Same(skills.SlotOf(SkillKeys.D), skills.TryCast(SkillKeys.D, hero));
        Assert.Equal((HeroMotion.Jump, true, AvatarFixture.BackStepSkill), (hero.Motion, hero.IsBackStep, hero.SkillId));
        hero.Update(10, 0, 0);
        double v0 = TownHero.TakeoffSpeed(AvatarFixture.BackStepPowerStat, hero.WeightStat, TownHero.BackStepJumpSpeedStat);
        double g = TownHero.BackStepJumpSpeedStat * TownHero.EngineGroundGravity / 100.0;
        Assert.Equal(v0 * 0.01 - 0.5 * g * 0.0001, hero.Z, 6);
        Assert.True(hero.X > 300);                                      // 朝左,往右退
        Assert.True(skills.SlotOf(SkillKeys.D).Ready);                  // 没有 [cool time]:随时能再放(但腾空中放不出)
        Assert.Equal(SkillCastResult.Busy, skills.Cast(skills.SlotOf(SkillKeys.D), hero));
    }

    [Fact]
    public void CooldownsCarryOverByTheSkillIdWhenTheRoomChanges()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills old = Load(assets);
        HeroSkills fresh = Load(assets);
        TownHero hero = NewHero(assets);
        old.TryCast(SkillKeys.S, hero);
        old.Advance(300);
        Assert.Equal(1478.0, old.SlotOf(SkillKeys.S).CooldownLeftMs);

        fresh.CopyCooldownsFrom(old);
        Assert.Equal(1478.0, fresh.SlotOf(SkillKeys.S).CooldownLeftMs);
        Assert.Equal(0.0, fresh.SlotOf(SkillKeys.A).CooldownLeftMs);
        fresh.CopyCooldownsFrom(fresh);                                  // 自己拷自己、拷 null 都不改
        fresh.CopyCooldownsFrom(null);
        Assert.Equal(1478.0, fresh.SlotOf(SkillKeys.S).CooldownLeftMs);
    }

    [Fact]
    public void TheHardAttackSlotReadsItsOwnAtkAndSkillDamageAndFindsItsFilesByNameNotByIndex()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        CharacterDef chr = data.GetCharacter(0);

        // 同名文件在两个表里的下标不同:动作表的第 1 项、攻击信息表的第 0 项;找不到、给 null 都是 -1
        Assert.Equal(1, chr.EtcMotionIndexOf("HardAttack.ANI"));
        Assert.Equal(0, chr.EtcAttackInfoIndexOf("hardattack.atk"));
        Assert.Equal(-1, chr.EtcMotionIndexOf("nothing.ani"));
        Assert.Equal(-1, chr.EtcAttackInfoIndexOf(null));
        Assert.Equal(-1, chr.EtcMotionIndexOf("hardattack.atk"));
        TownHero hero = NewHero(assets);
        Assert.Equal(1, hero.EtcMotionIndexOf("hardattack.ani"));
        Assert.Equal(-1, hero.EtcMotionIndexOf("hardattack.atk"));
        Assert.Equal(-1, hero.EtcMotionIndexOf(null));

        // 鬼斩的命中信息 = .atk 的击倒与击退 + .skl 一级的伤害两个数;上挑沿用第三段普攻的、后跳不攻击,都没有自己的
        HeroSkills skills = Load(assets);
        HeroStrikeInfo strike = skills.SlotOf(SkillKeys.A).Strike.Value;
        Assert.True(strike.IsSkillDamage);
        Assert.Equal((AvatarFixture.HardAttackRate, AvatarFixture.HardAttackFixed), (strike.SkillRatePercent, strike.SkillFixed));
        Assert.Equal((AvatarFixture.HardAttackPushAside, true), (strike.PushAside, strike.KnockDown));
        Assert.Null(skills.SlotOf(SkillKeys.S).Strike);
        Assert.Null(skills.SlotOf(SkillKeys.D).Strike);
        Assert.Same(skills.SlotOf(SkillKeys.A), skills.SlotOfSkill(AvatarFixture.HardAttackSkill));
        Assert.Null(skills.SlotOfSkill(999));

        // 鬼斩放出来时同时放角色目录下 effect/animation/ 里的两层斩击光影;另外两个技能没有特效
        Assert.Equal(new[] { AvatarFixture.HardAttackEffect1, AvatarFixture.HardAttackEffect2 }, skills.SlotOf(SkillKeys.A).EffectPaths.ToArray());
        Assert.Empty(skills.SlotOf(SkillKeys.S).EffectPaths);
        Assert.Empty(skills.SlotOf(SkillKeys.D).EffectPaths);
        Assert.Equal((HeroSkills.HardAttackEffectForwardX, 0, 0), (skills.SlotOf(SkillKeys.A).EffectForwardX, skills.SlotOf(SkillKeys.S).EffectForwardX, skills.SlotOf(SkillKeys.D).EffectForwardX));
    }

    [Fact]
    public void HardAttackPlaysItsOwnMotionInPlaceIgnoringTheDirectionKeysAndStartsItsCooldown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(assets);
        TownHero hero = NewHero(assets, x: 100, y: 50, facingLeft: true);
        HeroSkillSlot hard = skills.SlotOf(SkillKeys.A);

        Assert.Same(hard, skills.TryCast(SkillKeys.A, hero));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.HardAttackSkill, "hardattack.ani"), (hero.Motion, hero.SkillId, hero.SkillMotionName));
        Assert.Equal((double)AvatarFixture.HardAttackCool, hard.CooldownLeftMs);

        // 方向键不转身、不移动,动作的第 2 帧(200 到 300 毫秒)在 250 毫秒时
        hero.Update(250, 1, 0);
        Assert.Equal((HeroMotion.Skill, 2, 100.0, true), (hero.Motion, hero.CurrentFrame, hero.X, hero.FacingLeft));
        Assert.Equal(0.0, hero.SkillSlideSpeed);

        // 整段 600 毫秒放完回到站立,位置没变;放着的时候别的技能放不出来
        Assert.Equal(SkillCastResult.Busy, skills.Cast(skills.SlotOf(SkillKeys.D), hero));
        hero.Update(350, 1, 0);
        Assert.Equal((HeroMotion.Rest, 100.0, 50.0), (hero.Motion, hero.X, hero.Y));
        Assert.Equal(0, hero.SkillId);
    }

    [Fact]
    public void HardAttackNeedsItsMotionInTheCharacterScriptOtherwiseItIsBusyAndStartsNoCooldown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(assets);
        TownHero plain = NewHero(assets, chr: AvatarFixture.ChrPath);     // 这份角色脚本没有 [etc motion]
        HeroSkillSlot hard = skills.SlotOf(SkillKeys.A);

        Assert.Equal(-1, plain.EtcMotionIndexOf("hardattack.ani"));
        Assert.Equal(SkillCastResult.Busy, skills.Cast(hard, plain));
        Assert.Equal((HeroMotion.Rest, 0.0), (plain.Motion, hard.CooldownLeftMs));
    }
}
