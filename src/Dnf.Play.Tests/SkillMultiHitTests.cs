using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 多段命中(M46,证据 E-139):配方标了一个被动技能与 [static data] 里的次数下标,主角学过这个被动技能(等级大于 0)时,放出技能那一刻开计时器,
/// 间隔 = 整段动画时长 / 次数,每响一次命中名单重新武装,同一个目标可以再挨一下;没学过就只命中一轮。
/// 数据全是合成的(<see cref="AvatarFixture.ChainSkill"/>):第一段动作 400 毫秒,第 2 帧(200 到 300 毫秒)有攻击框,[static data] 第 4 个数是 2(次数);
/// 被动技能用合成的编号 905,只在 <see cref="HeroSkills.SkillLevelOf"/> 里回答"学过没有"。
/// </summary>
public class SkillMultiHitTests
{
    private const int Passive = 905;

    private static SkillRecipes Recipes(int countIndex)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[] { new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: 2, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1,
                multiHitPassiveSkillId: Passive, multiHitCountIndex: countIndex),
        });
    }

    private static HeroSkills Load(GameData data, int passiveLevel, int countIndex = 4)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 2));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);
        HeroSkills skills = HeroSkills.Create(data, chr, 0, book, Recipes(countIndex));
        skills.SkillLevelOf = id => id == Passive ? passiveLevel : book.LevelOf(id);
        return skills;
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, false, TownHero.DungeonRestTag);
    }

    [Theory]
    [InlineData(1, 4, 2)]       // 学过:次数取 [static data] 第 4 个数
    [InlineData(5, 4, 2)]       // 被动技能的等级高低不影响次数
    [InlineData(0, 4, 0)]       // 没学过:不开计时器
    [InlineData(1, 9, 0)]       // 下标超出 [static data]:当 0,不开
    [InlineData(1, -1, 0)]      // 配方没标次数的下标:不开
    public void TheTimerOnlyOpensWhenThePassiveIsLearnedAndTheCountComesFromTheStaticData(int passiveLevel, int countIndex, int expectedWindows)
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), passiveLevel, countIndex);
        TownHero hero = NewHero(assets);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal((expectedWindows, 0, 0L), (hero.StageRearmsMax, hero.StageRearmsDone, hero.HitRearmNumber));
    }

    [Fact]
    public void APlainRecipeNeverOpensTheTimerWhateverThePassiveSays()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 2));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);
        var plain = new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[] { new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: 2, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1),
        });
        HeroSkills skills = HeroSkills.Create(data, chr, 0, book, plain);
        skills.SkillLevelOf = _ => 9;
        TownHero hero = NewHero(assets);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal(0, hero.StageRearmsMax);
    }

    private static (long Hits, int Windows, long Damage) CastInTheRoom(int passiveLevel)
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 100000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, passiveLevel));
        RoomCombatSession s = arena.Session;
        for (int t = 0; t < 600; t += 10)
        {
            s.Update(10, 0, 0, false, false, t == 0 ? SkillKeys.A : SkillKeys.None);
        }
        return (s.Hits, s.HopWindows, s.TotalDamage);
    }

    [Fact]
    public void InTheRoomTheSameTargetIsHitAgainWhenTheTimerRingsButOnlyWithThePassive()
    {
        (long Hits, int Windows, long Damage) plain = CastInTheRoom(0);
        (long Hits, int Windows, long Damage) multi = CastInTheRoom(1);

        // 动画 400 毫秒、次数 2:间隔 200,第一次响铃在 210 毫秒左右,正好在攻击框所在的第 2 帧(200 到 300 毫秒)里:这一帧重新判一遍,又打中一次
        Assert.Equal((1L, 0), (plain.Hits, plain.Windows));
        Assert.Equal((2L, 2), (multi.Hits, multi.Windows));          // 第一窗口加一次重武装
        Assert.True(plain.Damage > 0);
        Assert.InRange(multi.Damage, plain.Damage * 2 - 1, plain.Damage * 2 + 1);   // 每一下伤害一样
    }
}
