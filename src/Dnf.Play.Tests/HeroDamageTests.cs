using Dnf.Data;
using Dnf.Game;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角打怪物的原版伤害公式接线(证据 E-110):装备与面板、攻击信息到伤害输入、会话里的"公式/占位"开关、
/// 额外命中只算第一击、暴击数字、怪物防御。数据全是合成的,期望值是手算(算式见每个测试的注释),没有用原版数值:
/// 迷你主角 10 级、面板物攻 50、装备物攻 30,一次不带加成的普攻 = (0 + 30) × (50 / 100 + 1) = 45;
/// 公式常量里随机幅度的底与系数都是 0,所以结果与种子无关;暴击用 [critical hit] 压到必不暴击(−1000)或必暴击(+10000)。
/// </summary>
public class HeroDamageTests
{
    private const float Never = -1000f;
    private const float Always = 10000f;

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static DamageConstants Constants()
    {
        return new DamageConstants
        {
            PhysicalDamageRate = 100f,
            MagicalDamageRate = 100f,
            PhysicalReduceRate = 20f,
            MagicalReduceRate = 20f,
            CommonElementBase = 100f,
        };
    }

    private static HeroPanel Panel()
    {
        return new HeroPanel(10, 50, 100, new EquipmentStats(30, 20));
    }

    /// <summary>一击:带武器攻击力的物理攻击,[damage bonus] 0,暴击由 <paramref name="critical"/> 控制。</summary>
    private static HeroStrikeInfo Strike(float critical = Never, float damagePercent = 0f)
    {
        return new HeroStrikeInfo(0, 0, false, source: new HitInput(0f, true, AttackKind.Physic, damagePercent, 0, critical));
    }

    private static HeroStrikeInfo[] Strikes(HeroStrikeInfo strike)
    {
        return new[] { strike, strike, strike };
    }

    private static Arena FormulaArena(HeroStrikeInfo strike, DefenderPanel defense, int hp = 1000)
    {
        return new Arena(new[] { (340, 100) }, Strikes(strike), hp: hp, heroPanel: Panel(), damageConstants: Constants(), defense: defense);
    }

    // ------------------------------------------------------------------ 装备

    [Fact]
    public void EquipmentStatsReadTheFirstNumberOfEachTag()
    {
        var lines = new[]
        {
            new ScriptLine("[equipment physical attack]", new[] { I(12), I(9) }),     // 后面有第二个数:只取第一个
            new ScriptLine("[equipment magical attack]", new[] { F(7.9f) }),           // 浮点向零取整
            new ScriptLine("[physical critical hit]", new[] { F(2.5f) }),
            new ScriptLine("[equipment physical attack]", new[] { I(999) }),           // 同名标签只取第一次出现
        };
        EquipmentStats stats = EquipmentStats.From(new TagTable(lines));
        Assert.Equal((12, 7, 2.5f, 0f), (stats.PhysicalAttack, stats.MagicalAttack, stats.PhysicalCritical, stats.MagicalCritical));
    }

    [Fact]
    public void EquipmentStatsWithoutTheTagsAreZeroAndTheyAdd()
    {
        EquipmentStats empty = EquipmentStats.From(new TagTable(new ScriptLine[0]));
        Assert.Equal((0, 0, 0f, 0f), (empty.PhysicalAttack, empty.MagicalAttack, empty.PhysicalCritical, empty.MagicalCritical));
        EquipmentStats sum = new EquipmentStats(10, 20, 1.5f, 2.5f).Plus(new EquipmentStats(1, 2, 0.5f, 0.25f));
        Assert.Equal((11, 22, 2f, 2.75f), (sum.PhysicalAttack, sum.MagicalAttack, sum.PhysicalCritical, sum.MagicalCritical));
    }

    [Fact]
    public void EquipmentStatsRejectMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => EquipmentStats.From(null));
        Assert.Throws<ArgumentNullException>(() => EquipmentStats.Read(null, "item/test.equ"));
    }

    [Fact]
    public void NoOutfitMeansNoEquipmentBonus()
    {
        EquipmentStats worn = HeroPanel.WornEquipment(null, null);
        Assert.Equal((0, 0, 0f, 0f), (worn.PhysicalAttack, worn.MagicalAttack, worn.PhysicalCritical, worn.MagicalCritical));
    }

    // ------------------------------------------------------------------ 面板

    [Fact]
    public void HeroPanelFromChrTakesTheFirstOccurrencesTruncated()
    {
        var chr = new CharacterDef(0, "character/test/mini.chr", new[]
        {
            new ScriptLine("[physical attack]", new[] { F(7.9f) }),
            new ScriptLine("[magical attack]", new[] { I(4) }),
            new ScriptLine("[physical attack]", new[] { I(999) }),                    // 后面成长表里的同名标签不算
        });
        HeroPanel panel = HeroPanel.FromChr(chr, 3, new EquipmentStats(1, 2));
        Assert.Equal((3, 7, 4), (panel.Level, panel.PhysicalAttack, panel.MagicalAttack));
        Assert.Equal((1, 2), (panel.Equipment.PhysicalAttack, panel.Equipment.MagicalAttack));
    }

    [Fact]
    public void HeroPanelWithoutAChrOrTheTagsHasNoPower()
    {
        HeroPanel none = HeroPanel.FromChr(null, 0, default);
        Assert.Equal((1, 0, 0), (none.Level, none.PhysicalAttack, none.MagicalAttack));
        HeroPanel bare = HeroPanel.FromChr(new CharacterDef(0, "character/test/bare.chr", new ScriptLine[0]), 5, default);
        Assert.Equal((5, 0, 0), (bare.Level, bare.PhysicalAttack, bare.MagicalAttack));
    }

    [Fact]
    public void HeroPanelBecomesACharacterAttackerWithTheEquipmentAsBonusBase()
    {
        var panel = new HeroPanel(7, 10, 20, new EquipmentStats(30, 40, 2.5f, 3.5f));
        AttackerPanel attacker = panel.ToAttacker();
        Assert.Equal(DamageObjectTypes.Character, attacker.ObjectType);
        Assert.Equal((7, 10, 20, 30, 40), (attacker.Level, attacker.PhysicalAttack, attacker.MagicalAttack, attacker.PhysicalBonusBase, attacker.MagicalBonusBase));
        Assert.Equal((2.5f, 3.5f), (attacker.PhysicalCritical, attacker.MagicalCritical));
        Assert.Equal((0, 0, 0, 0), (attacker.ElementAttack(0), attacker.ElementAttack(1), attacker.ElementAttack(2), attacker.ElementAttack(3)));
    }

    // ------------------------------------------------------------------ 攻击信息

    [Fact]
    public void AHandWrittenStrikeIsAPlainWeaponHitOfTheRightKind()
    {
        HitInput physical = new HeroStrikeInfo(20, 0, false).Input;
        Assert.Equal((0f, true, AttackKind.Physic, 20f), (physical.Base, physical.UseBonusBase, physical.Kind, physical.DamagePercent));
        Assert.Equal(AttackKind.Magic, new HeroStrikeInfo(0, 0, false, magical: true).Input.Kind);
    }

    [Fact]
    public void AStrikeReadFromAnAttackInfoKeepsItsInput()
    {
        var source = new HitInput(3f, false, AttackKind.Magic, 5f, 1, 2f, false, 4);
        Assert.Same(source, new HeroStrikeInfo(0, 0, false, source: source).Input);
    }

    [Fact]
    public void SkillDamageConvertsTheTwoColumnsAndSurvivesWithSkillDamage()
    {
        var source = new HitInput(0f, true, AttackKind.Magic, 0f, 0, Never, false, 4);
        HeroStrikeInfo skill = new HeroStrikeInfo(0, 0, false, skillRatePercent: 160, skillFixed: 40, source: source);
        Assert.True(skill.IsSkillDamage);
        Assert.Equal((60f, 25f, AttackKind.Magic, 4), (skill.Input.DamagePercent, skill.Input.Base, skill.Input.Kind, skill.Input.ElementMask));

        HeroStrikeInfo later = new HeroStrikeInfo(0, 0, false, source: source).WithSkillDamage(200, 50);
        Assert.Equal((100f, 25f, AttackKind.Magic, 4), (later.Input.DamagePercent, later.Input.Base, later.Input.Kind, later.Input.ElementMask));
    }

    // ------------------------------------------------------------------ 会话:公式还是占位

    [Fact]
    public void WithoutThePanelOrTheConstantsTheDamageStaysThePlaceholder()
    {
        using var plain = new Arena(new[] { (340, 100) });
        Assert.False(plain.Session.UsesFormulaDamage);
        Assert.Null(plain.Session.Panel);
        plain.Run(250, 0);
        Assert.InRange(plain.Session.TotalDamage, 25, 26);                          // 占位:25.3 概率进位

        using var onlyPanel = new Arena(new[] { (340, 100) }, heroPanel: Panel());
        Assert.False(onlyPanel.Session.UsesFormulaDamage);
        onlyPanel.Run(250, 0);
        Assert.InRange(onlyPanel.Session.TotalDamage, 25, 26);

        using var onlyConstants = new Arena(new[] { (340, 100) }, damageConstants: Constants());
        Assert.False(onlyConstants.Session.UsesFormulaDamage);
        onlyConstants.Run(250, 0);
        Assert.InRange(onlyConstants.Session.TotalDamage, 25, 26);
    }

    [Fact]
    public void AFormulaHitUsesThePanelAndTheNumberIsTheYellowOne()
    {
        // 防御方 60 级:没有额外命中。(0 + 30) × (50 / 100 + 1) = 45
        using Arena arena = FormulaArena(Strike(), new DefenderPanel { Level = 60 });
        RoomCombatSession s = arena.Session;
        Assert.True(s.UsesFormulaDamage);
        Assert.NotNull(s.Panel);
        arena.Run(250, 0);                                                          // 命中发生在 200 毫秒
        Assert.Equal(1, s.Hits);
        HitPopup popup = Assert.Single(s.Popups);
        Assert.Equal(45, popup.Value);
        Assert.Equal(DamageNumberFlags.AttackerIsHero, popup.Flags);                // 没暴击:没有暴击位
        Assert.Equal(1000 - 45, s.Monsters[0].Hp);
        Assert.Equal(45.0, s.TotalDamage);
    }

    [Fact]
    public void TheMonsterDefenseReducesTheHit()
    {
        // 防御 200、攻击者 10 级、系数 20:200 / (10 × 20 + 200) = 0.5 → 45 × 0.5 = 22.5 → 22
        using Arena arena = FormulaArena(Strike(), new DefenderPanel { Level = 60, PhysicalDefense = 200 });
        arena.Run(250, 0);
        Assert.Equal(22, Assert.Single(arena.Session.Popups).Value);
    }

    [Fact]
    public void ADamageBonusOfTheStrikeScalesTheFormulaHit()
    {
        // [damage bonus] 100:30 × 2 × 1.5 = 90
        using Arena arena = FormulaArena(Strike(damagePercent: 100f), new DefenderPanel { Level = 60 });
        arena.Run(250, 0);
        Assert.Equal(90, Assert.Single(arena.Session.Popups).Value);
    }

    [Fact]
    public void ACriticalHitShowsTheCriticalNumber()
    {
        // 45 × 1.5 = 67.5 → 67,数字带暴击位
        using Arena arena = FormulaArena(Strike(Always), new DefenderPanel { Level = 60 });
        arena.Run(250, 0);
        HitPopup popup = Assert.Single(arena.Session.Popups);
        Assert.Equal(67, popup.Value);
        Assert.Equal(DamageNumberFlags.AttackerIsHero | DamageNumberFlags.Critical, popup.Flags);
        Assert.Equal(1, popup.Template);
    }

    [Fact]
    public void OnlyTheFirstHitOnALowLevelMonsterIsABonusHit()
    {
        // 防御方 10 级(低于 50):第一击 45 × 1.1 = 49.5 → 49;同一个攻击者再打就不算了 → 45
        using Arena arena = FormulaArena(Strike(), new DefenderPanel { Level = 10 });
        RoomCombatSession s = arena.Session;
        MonsterActor m = s.Monsters[0];
        Assert.Equal(0, m.LastAttackerId);
        arena.Run(250, 0);
        Assert.Equal(49, Assert.Single(s.Popups).Value);
        Assert.Equal(RoomCombatSession.HeroAttackerId, m.LastAttackerId);
        arena.Run(600);                                                             // 等这一挥结束
        arena.Run(600, 0);                                                          // 再挥一次
        Assert.True(s.Hits >= 2);
        Assert.Equal(45, s.Popups[s.Popups.Count - 1].Value);
        Assert.Equal(49 + 45 * (s.Hits - 1), (int)s.TotalDamage);
    }

    [Fact]
    public void AHighLevelMonsterNeverGetsTheBonusHit()
    {
        using Arena arena = FormulaArena(Strike(), new DefenderPanel { Level = 50 });
        arena.Run(250, 0);
        Assert.Equal(45, Assert.Single(arena.Session.Popups).Value);
        Assert.Equal(RoomCombatSession.HeroAttackerId, arena.Session.Monsters[0].LastAttackerId);
    }

    [Fact]
    public void WithoutADefensePanelTheMonsterHasNoDefenseAndItsOwnLevel()
    {
        // 没给防御面板:零防御,等级取怪物自己的(1 级,低于 50)→ 第一击是额外命中 49
        using Arena arena = FormulaArena(Strike(), null);
        arena.Run(250, 0);
        Assert.Equal(49, Assert.Single(arena.Session.Popups).Value);
    }

    [Fact]
    public void RollFormulaDamageIsPureAndNeedsThePanel()
    {
        using Arena arena = FormulaArena(Strike(), new DefenderPanel { Level = 60 });
        RoomCombatSession s = arena.Session;
        MonsterActor m = s.Monsters[0];
        for (uint seed = 1; seed <= 5; seed++)
        {
            Assert.Equal(45, s.RollFormulaDamage(Strike(), m, seed, out bool critical));
            Assert.False(critical);
        }
        Assert.Equal(67, s.RollFormulaDamage(Strike(Always), m, 1u, out bool crit));
        Assert.True(crit);
        Assert.Equal(0, m.LastAttackerId);                                          // 只算,不记攻击者,也不改生命
        Assert.Equal(1000, m.Hp);
        Assert.Throws<ArgumentNullException>(() => s.RollFormulaDamage(Strike(), null, 1u, out _));

        using var placeholder = new Arena(new[] { (340, 100) });
        Assert.Throws<InvalidOperationException>(() => placeholder.Session.RollFormulaDamage(Strike(), placeholder.Session.Monsters[0], 1u, out _));
    }

    [Fact]
    public void TheSessionExposesTheStrikesForInspection()
    {
        using Arena arena = FormulaArena(Strike(), null);
        Assert.Equal(3, arena.Session.Strikes.Count);
        Assert.False(arena.Session.DashStrike.IsSkillDamage);
        Assert.False(arena.Session.JumpStrike.IsSkillDamage);
    }
}
