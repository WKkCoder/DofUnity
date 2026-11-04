using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Xunit;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物防御者类别编号接入 <see cref="DefenderPanel.CategoryIds"/> 后条件伤害率开关(E-212)。
/// </summary>
public sealed class MonsterDefenderCategoryDamageTests
{
    private static DamageConstants Constants() => new DamageConstants
    {
        PhysicalDamageRate = 1000f,
        MagicalDamageRate = 1000f,
        PhysicalReduceRate = 100f,
        MagicalReduceRate = 100f,
    };

    private static HitInput Strike(float humanRate = 100f) =>
        new HitInput(100f, false, AttackKind.Physic, 0f, 0, 0f, false, 0, humanRate);

    private static AttackerPanel HeroAttacker() => new AttackerPanel { Level = 1, PhysicalAttack = 10 };

    [Fact]
    public void HumanArmorCategoryOnMonsterEnablesHumanRateInFormula()
    {
        // 手算:开关0开且 humanRate=50 → 条件项 ×0.5(相对默认 100 的 ×1.0)
        var stats = new MonsterStats();
        DefenderPanel bare = DefenderPanel.FromMonster(stats, 1);
        DefenderPanel armored = DefenderPanel.FromMonster(stats, 1);
        armored.CategoryIds = new[] { ConditionalDamageRateFlags.HumanArmorCategoryId };
        HitInput input = Strike(humanRate: 50f);
        int without = DamageFormula.Compute(HitDamage.Fill(input, HeroAttacker(), bare, false, 1u), Constants());
        int with = DamageFormula.Compute(HitDamage.Fill(input, HeroAttacker(), armored, false, 2u), Constants());
        Assert.True(with < without);
        Assert.Equal((true, false, false), (
            HitDamage.Fill(input, HeroAttacker(), armored, false, 3u).ConditionalFlag(0),
            HitDamage.Fill(input, HeroAttacker(), armored, false, 3u).ConditionalFlag(1),
            HitDamage.Fill(input, HeroAttacker(), armored, false, 3u).ConditionalFlag(2)));
    }

    [Fact]
    public void FortArmorCategoryEnablesFortRateNotHuman()
    {
        var stats = new MonsterStats();
        DefenderPanel panel = DefenderPanel.FromMonster(stats, 1);
        panel.CategoryIds = new[] { ConditionalDamageRateFlags.FortArmorCategoryId };
        HitInput input = new HitInput(100f, false, AttackKind.Physic, 0f, 0, 0f, false, 0, 100f, 50f, 100f);
        DamageVariables p = HitDamage.Fill(input, HeroAttacker(), panel, false, 1u);
        Assert.Equal((false, true, false), (p.ConditionalFlag(0), p.ConditionalFlag(1), p.ConditionalFlag(2)));
    }

    [Fact]
    public void DivineArmorCategoryClearsMonsterSwitch()
    {
        var stats = new MonsterStats();
        DefenderPanel panel = DefenderPanel.FromMonster(stats, 1);
        panel.CategoryIds = new[] { ConditionalDamageRateFlags.DivineArmorCategoryId };
        DamageVariables p = HitDamage.Fill(Strike(), HeroAttacker(), panel, false, 1u);
        Assert.False(p.ConditionalFlag(2));
    }

    [Fact]
    public void NoCategoriesMatchesLegacyDefaultFlagsAndHumanRateIgnored()
    {
        var stats = new MonsterStats();
        DefenderPanel panel = DefenderPanel.FromMonster(stats, 1);
        HitInput low = Strike(humanRate: 50f);
        HitInput high = Strike(humanRate: 100f);
        int dLow = DamageFormula.Compute(HitDamage.Fill(low, HeroAttacker(), panel, false, 1u), Constants());
        int dHigh = DamageFormula.Compute(HitDamage.Fill(high, HeroAttacker(), panel, false, 2u), Constants());
        Assert.Equal(dLow, dHigh);
        Assert.Equal((false, false, true), (
            HitDamage.Fill(low, HeroAttacker(), panel, false, 1u).ConditionalFlag(0),
            HitDamage.Fill(low, HeroAttacker(), panel, false, 1u).ConditionalFlag(1),
            HitDamage.Fill(low, HeroAttacker(), panel, false, 1u).ConditionalFlag(2)));
    }

    [Fact]
    public void GetActCategoryIdsReturnsSortedDistinctIds()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");
        var m = new MonsterActor(0, spawn, model, 3, 100, 222.5, 1500, 0);
        m.AddActCategory(ConditionalDamageRateFlags.DivineArmorCategoryId);
        m.AddActCategory(ConditionalDamageRateFlags.HumanArmorCategoryId);
        Assert.Equal(new[] { ConditionalDamageRateFlags.HumanArmorCategoryId, ConditionalDamageRateFlags.DivineArmorCategoryId }, m.GetActCategoryIds());
    }

    private static HeroStrikeInfo StrikeHuman(float humanRate) =>
        new HeroStrikeInfo(0, 0, false, source: new HitInput(0f, true, AttackKind.Physic, 0f, 0, -1000f, false, 0, humanRate));

    private static Arena FormulaArena(HeroStrikeInfo strike, DefenderPanel defense) =>
        new Arena(new[] { (340, 100) }, new[] { strike, strike, strike }, hp: 1000,
            heroPanel: new HeroPanel(10, 50, 100, new EquipmentStats(30, 20)),
            damageConstants: Constants(), defense: defense);

    [Fact]
    public void RollFormulaDamageUsesCategoriesAfterSyncOnDefensePanel()
    {
        var defense = new DefenderPanel { Level = 1 };
        using Arena arena = FormulaArena(StrikeHuman(50f), defense);
        MonsterActor m = arena.Session.Monsters[0];
        m.Defense = defense;
        m.AddActCategory(ConditionalDamageRateFlags.HumanArmorCategoryId);
        m.Defense.CategoryIds = Array.Empty<int>();
        int low = arena.Session.RollFormulaDamage(StrikeHuman(50f), m, 1u, out _);
        int high = arena.Session.RollFormulaDamage(StrikeHuman(100f), m, 2u, out _);
        Assert.True(low < high);
    }

    [Fact]
    public void RollFormulaDamageUsesCategoriesWhenDefensePanelIsNull()
    {
        using Arena arena = FormulaArena(StrikeHuman(50f), new DefenderPanel { Level = 1 });
        MonsterActor m = arena.Session.Monsters[0];
        m.Defense = null;
        m.AddActCategory(ConditionalDamageRateFlags.HumanArmorCategoryId);
        int low = arena.Session.RollFormulaDamage(StrikeHuman(50f), m, 1u, out _);
        int high = arena.Session.RollFormulaDamage(StrikeHuman(100f), m, 2u, out _);
        Assert.True(low < high);
    }

    [Fact]
    public void SyncDefenseCategoryIdsCopiesRuntimeBitsetOntoDefensePanel()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");
        var m = new MonsterActor(0, spawn, model, 3, 100, 222.5, 1500, 0);
        m.Defense = DefenderPanel.FromMonster(new MonsterStats(), 3);
        m.AddActCategory(ConditionalDamageRateFlags.HumanArmorCategoryId);
        Assert.Equal(new[] { ConditionalDamageRateFlags.HumanArmorCategoryId }, m.Defense.CategoryIds);
    }
}
