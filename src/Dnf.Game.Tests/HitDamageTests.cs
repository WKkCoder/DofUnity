using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 命中时的伤害输入填充(HitDamage)与面板类型的语义测试(证据 E-110)。常量、面板与攻击信息都是合成的,
/// 每个场景的期望值来自手算(注释里写了算式),没有用原版数值。随机幅度的底与系数都设成 0,结果与种子无关;
/// 暴击用 [critical hit] 压到必不暴击(−1000)或必暴击(+10000)。
/// </summary>
public sealed class HitDamageTests
{
    private const float Never = -1000f;
    private const float Always = 10000f;

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

    /// <summary>攻击者:物攻 50、魔攻 100、装备物攻 30、装备魔攻 20、等级 10,没有暴击属性。</summary>
    private static AttackerPanel Attacker()
    {
        return new AttackerPanel { Level = 10, PhysicalAttack = 50, MagicalAttack = 100, PhysicalBonusBase = 30, MagicalBonusBase = 20 };
    }

    private static DefenderPanel Defender(int physicalDefense = 0, int magicalDefense = 0)
    {
        return new DefenderPanel { Level = 5, PhysicalDefense = physicalDefense, MagicalDefense = magicalDefense };
    }

    private static HitInput Physical(float damagePercent = 0f, float critical = Never, bool ignoreDefense = false, int elementMask = 0,
        float monsterRate = 100f, float humanRate = 100f)
    {
        return new HitInput(0f, true, AttackKind.Physic, damagePercent, 0, critical, ignoreDefense, elementMask, humanRate, 100f, monsterRate);
    }

    private static int Roll(HitInput input, AttackerPanel attacker, DefenderPanel defender, bool bonusHit = false, uint seed = 1)
    {
        return HitDamage.Roll(input, attacker, defender, bonusHit, Constants(), seed, out _);
    }

    // ------------------------------------------------------------------ 输入的来源

    [Fact]
    public void FromReadsTheAttackInfoFields()
    {
        ScriptCursor cursor = new TokenStreamBuilder()
            .Tag("[damage]").Float(12.5f)
            .Tag("[damage bonus]").Int(-15)
            .Tag("[absolute damage]").Int(7)
            .Tag("[attack type]").Tag("[magic]")
            .Tag("[weapon damage apply]").Int(1)
            .Tag("[critical hit]").Float(4f)
            .Tag("[ignore defense]").Int(1)
            .Tag("[elemental property]").Tag("[dark element]")
            .Tag("[elemental property]").Tag("[light element]")
            .Tag("[human damage rate]").Float(50f)
            .Tag("[fort damage rate]").Float(60f)
            .Tag("[monster damage rate]").Float(70f)
            .Cursor();
        AttackInfoData data = AttackInfoLoader.Load(cursor, out AttackInfoLoadReport report);
        Assert.True(report.Clean);

        HitInput input = HitInput.From(data);
        Assert.Equal(12.5f, input.Base);
        Assert.Equal(-15f, input.DamagePercent);
        Assert.Equal(7, input.AbsoluteDamage);
        Assert.Equal(AttackKind.Magic, input.Kind);
        Assert.True(input.UseBonusBase);
        Assert.Equal(4f, input.CriticalHit);
        Assert.True(input.IgnoreDefense);
        Assert.False(input.HasElement(0));
        Assert.False(input.HasElement(1));
        Assert.True(input.HasElement(2));
        Assert.True(input.HasElement(3));
        Assert.Equal((50f, 60f, 70f), (input.HumanRate, input.FortRate, input.MonsterRate));
    }

    [Fact]
    public void FromAnEmptyAttackInfoGivesTheDefaults()
    {
        AttackInfoData data = AttackInfoLoader.Load(new TokenStreamBuilder().Cursor(), out _);
        HitInput input = HitInput.From(data);
        Assert.Equal((0f, false, AttackKind.Physic, 0f, 0), (input.Base, input.UseBonusBase, input.Kind, input.DamagePercent, input.AbsoluteDamage));
        Assert.Equal(0, input.ElementMask);
        Assert.Equal((100f, 100f, 100f), (input.HumanRate, input.FortRate, input.MonsterRate));
        Assert.Throws<ArgumentNullException>(() => HitInput.From(null));
    }

    [Fact]
    public void FromBonusIsAPlainWeaponHit()
    {
        HitInput physical = HitInput.FromBonus(20, false);
        Assert.Equal((0f, true, AttackKind.Physic, 20f), (physical.Base, physical.UseBonusBase, physical.Kind, physical.DamagePercent));
        Assert.Equal(AttackKind.Magic, HitInput.FromBonus(0, true).Kind);
    }

    [Fact]
    public void ElementMaskKeepsOnlyTheFourElementBits()
    {
        var input = new HitInput(0f, false, AttackKind.Physic, 0f, elementMask: 0x1F);
        Assert.Equal(0xF, input.ElementMask);
        Assert.True(input.HasElement(0));
        Assert.True(input.HasElement(3));
        Assert.False(input.HasElement(4));
        Assert.False(input.HasElement(-1));
    }

    [Fact]
    public void SkillDamageTurnsTheTwoColumnsIntoBonusAndBase()
    {
        // 倍率 160%、固定值 40:[damage bonus] = 60,[damage] = 40 × 100 / 160 = 25
        HitInput skill = new HitInput(0f, true, AttackKind.Magic, 0f, 0, 3f, false, 4, 90f, 80f, 70f).WithSkillDamage(160, 40);
        Assert.Equal(60f, skill.DamagePercent);
        Assert.Equal(25f, skill.Base);
        // 其余字段原样保留
        Assert.Equal((true, AttackKind.Magic, 3f, 4), (skill.UseBonusBase, skill.Kind, skill.CriticalHit, skill.ElementMask));
        Assert.Equal((90f, 80f, 70f), (skill.HumanRate, skill.FortRate, skill.MonsterRate));
        // 倍率不是正数时只留固定值
        HitInput flat = Physical().WithSkillDamage(0, 40);
        Assert.Equal((0f, 40f), (flat.DamagePercent, flat.Base));
    }

    [Fact]
    public void SkillDamageScalesTheWeaponTermButNotTheFixedPart()
    {
        // 魔法攻击 0:没有攻击力放大项。装备魔攻 0:结果 = 25 × 1.6 = 40(就是固定值)
        AttackerPanel plain = Attacker();
        plain.MagicalAttack = 0;
        plain.MagicalBonusBase = 0;
        HitInput skill = new HitInput(0f, true, AttackKind.Magic, 0f, 0, Never).WithSkillDamage(160, 40);
        Assert.Equal(40, Roll(skill, plain, Defender()));
        // 装备魔攻 10:(25 + 10) × 1.6 = 56——武器那一项按倍率放大
        plain.MagicalBonusBase = 10;
        Assert.Equal(56, Roll(skill, plain, Defender()));
    }

    // ------------------------------------------------------------------ 填充

    [Fact]
    public void FillCopiesTheAttackInfoAndPanelsIntoTheStruct()
    {
        var input = new HitInput(12.5f, true, AttackKind.Magic, -15f, 7, 4f, true, 4 | 8, 50f, 60f, 70f);
        AttackerPanel attacker = Attacker();
        attacker.SetElementAttack(2, 10);
        attacker.PhysicalCritical = 2f;
        attacker.MagicalCritical = 6f;
        attacker.SecondLevel = 3;
        DefenderPanel defender = Defender(11, 22);
        defender.SetElementResist(2, 30);
        defender.SetElementResist(0, 99);   // 攻击不带火:不会进到伤害结构里

        DamageVariables p = HitDamage.Fill(input, attacker, defender, true, 1234u);

        Assert.Equal((12.5f, true, AttackKind.Magic, -15f, 7f, 4f, true), (p.Base, p.UseBonusBase, p.Kind, p.DamagePercent, p.FlatBonus, p.CriticalRate, p.IgnoreDefense));
        Assert.Equal((false, false, true, true), (p.ElementFlag(0), p.ElementFlag(1), p.ElementFlag(2), p.ElementFlag(3)));
        // 防御方抗性 − 攻击者元素攻击(只填攻击带的元素)
        Assert.Equal((0, 0, 20, 0), (p.ElementResist(0), p.ElementResist(1), p.ElementResist(2), p.ElementResist(3)));
        // 三个条件百分比:没有特殊状态的普通怪物只有第三个开关为真
        Assert.Equal((50f, 60f, 70f), (p.ConditionalPercent(0), p.ConditionalPercent(1), p.ConditionalPercent(2)));
        Assert.Equal((false, false, true), (p.ConditionalFlag(0), p.ConditionalFlag(1), p.ConditionalFlag(2)));
        // 攻击者
        Assert.Equal((50f, 100f, 30f, 20f), (p.PhysicalAttack, p.MagicalAttack, p.BonusBasePhysical, p.BonusBaseMagical));
        Assert.Equal((2f, 6f), (p.CriticalStatPhysical, p.CriticalStatMagical));
        Assert.Equal((10, 3), ((int)p.Level, (int)p.LevelAlt));
        Assert.Equal((DamageObjectTypes.Character, DamageObjectTypes.Character), (p.AttackerType, p.DamagerType));
        // 防御方
        Assert.Equal((11, 22), (p.PhysicalDefense, p.MagicalDefense));
        Assert.Equal((DamageObjectTypes.Monster, DamageObjectTypes.Monster), (p.DefenderType, p.DefenderOwnerType));
        // 额外命中与种子
        Assert.True(p.BonusHit);
        Assert.Equal(2, p.HitCounter);
        Assert.Equal(1234u, p.Seed);
    }

    [Fact]
    public void FillNeutralizesTheClientDefaultsThatHaveNoSource()
    {
        DamageVariables p = HitDamage.Fill(Physical(), Attacker(), Defender(), false, 1u);
        Assert.Equal((0f, 0f, 0f), (p.PhysicalPercent, p.MagicalPercent, p.ExtraRate));
        Assert.Equal((100f, 100), (p.FinalPercentA, p.PowerPercent));
        Assert.Equal((0, 0), (p.PhysicalDefensePercent, p.MagicalDefensePercent));
        Assert.Equal((0f, 0f, 0f, 0f), (p.SkillCriticalRate, p.SkillCriticalStatPhysical, p.SkillCriticalStatMagical, p.CreatureCriticalStat));
        Assert.False(p.BonusHit);
        Assert.Equal(1, p.HitCounter);
    }

    [Fact]
    public void FillClampsTheLevelsToTheSignedByteRange()
    {
        AttackerPanel attacker = Attacker();
        attacker.Level = 500;
        attacker.SecondLevel = -4;
        DamageVariables p = HitDamage.Fill(Physical(), attacker, Defender(), false, 1u);
        Assert.Equal((127, 1), ((int)p.Level, (int)p.LevelAlt));
    }

    [Fact]
    public void FillRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => HitDamage.Fill(null, Attacker(), Defender(), false, 1u));
        Assert.Throws<ArgumentNullException>(() => HitDamage.Fill(Physical(), null, Defender(), false, 1u));
        Assert.Throws<ArgumentNullException>(() => HitDamage.Fill(Physical(), Attacker(), null, false, 1u));
        Assert.Throws<ArgumentNullException>(() => HitDamage.Roll(Physical(), Attacker(), Defender(), false, null, 1u, out _));
    }

    // ------------------------------------------------------------------ 公式结果(手算)

    [Fact]
    public void APlainPhysicalHitUsesTheWeaponTermAndTheAttackPower()
    {
        // 基础 0 + 装备物攻 30 = 30;[damage bonus] 0 → ×1;物攻 50:× (50 / 100 + 1) = 45;防御 0 → 45
        Assert.Equal(45, Roll(Physical(), Attacker(), Defender()));
    }

    [Fact]
    public void ADamageBonusScalesTheHit()
    {
        // 30 × (−20 / 100 + 1) = 24;× 1.5 = 36
        Assert.Equal(36, Roll(Physical(-20f), Attacker(), Defender()));
        // 30 × 2 = 60;× 1.5 = 90
        Assert.Equal(90, Roll(Physical(100f), Attacker(), Defender()));
    }

    [Fact]
    public void AMagicalHitUsesTheMagicalPanel()
    {
        // 装备魔攻 20 × 2 = 40;魔攻 100:× (100 / 100 + 1) = 80;物攻(这里放得很大)不参与
        var input = new HitInput(0f, true, AttackKind.Magic, 100f, 0, Never);
        AttackerPanel attacker = Attacker();
        attacker.PhysicalAttack = 9999;
        Assert.Equal(80, Roll(input, attacker, Defender()));
    }

    [Fact]
    public void WithoutTheWeaponApplyFlagTheEquipmentTermIsLeftOut()
    {
        // [weapon damage apply] 0:基础 8 × 1 × 1.5 = 12
        var input = new HitInput(8f, false, AttackKind.Physic, 0f, 0, Never);
        Assert.Equal(12, Roll(input, Attacker(), Defender()));
        // 打开以后 (8 + 30) × 1.5 = 57
        var applied = new HitInput(8f, true, AttackKind.Physic, 0f, 0, Never);
        Assert.Equal(57, Roll(applied, Attacker(), Defender()));
    }

    [Fact]
    public void DefenseReducesTheHitByTheOriginalRatio()
    {
        // 防御 200、等级 10、系数 20:200 / (10 × 20 + 200) = 0.5 → 45 × 0.5 = 22.5 → 22
        Assert.Equal(22, Roll(Physical(), Attacker(), Defender(physicalDefense: 200)));
        // 魔法用魔防:80 × 0.5 = 40
        var magical = new HitInput(0f, true, AttackKind.Magic, 100f, 0, Never);
        Assert.Equal(40, Roll(magical, Attacker(), Defender(physicalDefense: 9999, magicalDefense: 200)));
    }

    [Fact]
    public void IgnoreDefenseSkipsTheReductionAndSoDoesTheIgnoreDefenseKind()
    {
        Assert.Equal(45, Roll(Physical(ignoreDefense: true), Attacker(), Defender(physicalDefense: 200)));
        // 无视防御种类没有"攻击力放大"一项:基础 0 + 装备物攻 30 = 30,随机幅度是伤害的 7%(±2.1);
        // 防御 200 若生效会减半成 15 上下,所以结果都落在 27 到 32 之间就说明防御被跳过了
        var kind = new HitInput(0f, true, AttackKind.IgnoreDefense, 0f, 0, Never);
        for (uint seed = 1; seed <= 60; seed++)
        {
            Assert.InRange(Roll(kind, Attacker(), Defender(physicalDefense: 200), seed: seed), 27, 32);
        }
    }

    [Fact]
    public void AnElementLetsTheResistanceBiteAndOnlyForTheElementsTheHitCarries()
    {
        // 暗元素:(100 − (抗性 30 − 元素攻击 10) × 1) × 0.01 × 45 = 36
        AttackerPanel attacker = Attacker();
        attacker.SetElementAttack(2, 10);
        DefenderPanel defender = Defender();
        defender.SetElementResist(2, 30);
        Assert.Equal(36, Roll(Physical(elementMask: 4), attacker, defender));
        // 攻击没有元素时抗性不起作用
        Assert.Equal(45, Roll(Physical(), attacker, defender));
        // 暗与光两个元素:取折算后最大的那个:(100 − 30) × 0.01 × 45 = 31.5,(100 − 10) × 0.01 × 45 = 40.5 → 40
        var both = Defender();
        both.SetElementResist(2, 30);
        both.SetElementResist(3, 10);
        Assert.Equal(40, Roll(Physical(elementMask: 4 | 8), Attacker(), both));
    }

    [Fact]
    public void TheMonsterRateScalesAPlainMonsterAndTheHumanRateDoesNot()
    {
        // 普通怪物:第三个开关为真 → × 0.5 = 22.5 → 22;人类伤害率这里不参与
        Assert.Equal(22, Roll(Physical(monsterRate: 50f, humanRate: 10f), Attacker(), Defender()));
        Assert.Equal(45, Roll(Physical(humanRate: 10f), Attacker(), Defender()));
    }

    [Fact]
    public void ACriticalHitMultipliesByTheDefaultRateAndIsReported()
    {
        // 45 × 1.5 = 67.5 → 67
        int damage = HitDamage.Roll(Physical(critical: Always), Attacker(), Defender(), false, Constants(), 1u, out bool critical);
        Assert.True(critical);
        Assert.Equal(67, damage);
        HitDamage.Roll(Physical(), Attacker(), Defender(), false, Constants(), 1u, out critical);
        Assert.False(critical);
    }

    [Fact]
    public void TheCriticalStatRaisesTheChanceOfTheMatchingKindOnly()
    {
        // 暴击率 = 3 + 暴击属性:属性 −3 时刚好 0%(随机数 % 10000 必不小于 0),物理暴击属性对魔法不起作用
        AttackerPanel attacker = Attacker();
        attacker.PhysicalCritical = -3f;
        attacker.MagicalCritical = 10000f;
        HitDamage.Roll(Physical(critical: 0f), attacker, Defender(), false, Constants(), 1u, out bool critical);
        Assert.False(critical);
        var magical = new HitInput(0f, true, AttackKind.Magic, 0f, 0, 0f);
        HitDamage.Roll(magical, attacker, Defender(), false, Constants(), 1u, out critical);
        Assert.True(critical);
    }

    [Fact]
    public void TheBonusHitIsTenPercentMore()
    {
        // 45 × 1.1 = 49.5 → 49
        Assert.Equal(49, Roll(Physical(), Attacker(), Defender(), bonusHit: true));
        // 100:30 × 2 × (50 / 100 + 1) = 90 → 99
        Assert.Equal(99, Roll(Physical(100f), Attacker(), Defender(), bonusHit: true));
    }

    [Fact]
    public void TheDamageIsAtLeastOne()
    {
        var weak = new AttackerPanel { Level = 1, PhysicalAttack = 0, PhysicalBonusBase = 0 };
        Assert.Equal(1, Roll(Physical(), weak, Defender()));
    }

    [Fact]
    public void TheSameSeedGivesTheSameDamageAndTheSpreadStaysInsideItsRange()
    {
        // 随机幅度 = 底 4 + 第二等级 10 × 系数 0.5 = 9(带 [weapon damage apply] 的攻击用第二等级):基础 45 的上下 ±9
        DamageConstants k = Constants();
        k.PhysicalRandomBase = 4f;
        k.PhysicalRandomRate = 0.5f;
        AttackerPanel attacker = Attacker();
        attacker.SecondLevel = 10;
        int low = int.MaxValue;
        int high = int.MinValue;
        for (uint seed = 1; seed <= 400; seed++)
        {
            int a = HitDamage.Roll(Physical(), attacker, Defender(), false, k, seed, out _);
            int b = HitDamage.Roll(Physical(), attacker, Defender(), false, k, seed, out _);
            Assert.Equal(a, b);
            low = Math.Min(low, a);
            high = Math.Max(high, a);
        }
        Assert.InRange(low, 36, 45);
        Assert.InRange(high, 45, 54);
        Assert.NotEqual(low, high);
    }

    // ------------------------------------------------------------------ 额外命中的条件

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(49, false, true)]
    [InlineData(50, false, false)]
    [InlineData(127, false, false)]
    [InlineData(1, true, false)]
    [InlineData(49, true, false)]
    public void TheBonusHitNeedsALowLevelDefenderAndANewAttacker(int level, bool sameAttacker, bool expected)
    {
        Assert.Equal(expected, HitDamage.BonusHitApplies(level, sameAttacker));
    }

    // ------------------------------------------------------------------ 面板

    [Fact]
    public void ADefenderPanelTakesTheProductOfTheIntegerAndTheMultiplier()
    {
        var stats = new MonsterStats();
        stats.Raw[MonsterStats.IxEquipPhysicalDefense] = 100;
        stats.SetF(MonsterStats.IxEquipPhysicalDefense + 1, 1.5f);
        stats.Raw[MonsterStats.IxEquipMagicalDefense] = 77;
        stats.SetF(MonsterStats.IxEquipMagicalDefense + 1, 1.0f);
        stats.Raw[MonsterStats.IxElementResistance + 2] = 40;
        stats.SetF(MonsterStats.IxElementResistance + 4 + 2, 0.5f);

        DefenderPanel panel = DefenderPanel.FromMonster(stats, 12);
        Assert.Equal((12, 150, 77), (panel.Level, panel.PhysicalDefense, panel.MagicalDefense));
        Assert.Equal((0, 0, 20, 0), (panel.ElementResist(0), panel.ElementResist(1), panel.ElementResist(2), panel.ElementResist(3)));
        Assert.Equal(DamageObjectTypes.Monster, panel.ObjectType);
    }

    [Fact]
    public void ThePlainDefensePairDoesNotFeedTheFormulaDefense()
    {
        // 客户端填充函数里公式读的防御力是"装备防御"一对;普通防御一对(下标 8/9、12/13)只进另外的浮点格,不进公式(E-160)
        var stats = new MonsterStats();
        stats.Raw[MonsterStats.IxPhysicalDefense] = 5000;
        stats.Raw[MonsterStats.IxMagicalDefense] = 6000;
        stats.Raw[MonsterStats.IxEquipPhysicalDefense] = 11;
        stats.Raw[MonsterStats.IxEquipMagicalDefense] = 22;

        DefenderPanel panel = DefenderPanel.FromMonster(stats, 5);
        Assert.Equal((11, 22), (panel.PhysicalDefense, panel.MagicalDefense));
    }

    [Fact]
    public void ADefenderPanelClampsDefenseAndLevel()
    {
        var stats = new MonsterStats();
        stats.Raw[MonsterStats.IxEquipPhysicalDefense] = -4000;
        stats.SetF(MonsterStats.IxEquipPhysicalDefense + 1, 1.0f);
        DefenderPanel panel = DefenderPanel.FromMonster(stats, 0);
        Assert.Equal((1, -1500), (panel.Level, panel.PhysicalDefense));
        Assert.Throws<ArgumentNullException>(() => DefenderPanel.FromMonster(null, 1));
    }

    [Fact]
    public void ElementIndicesOutsideTheFourAreRejected()
    {
        var attacker = new AttackerPanel();
        var defender = new DefenderPanel();
        Assert.Throws<ArgumentOutOfRangeException>(() => attacker.ElementAttack(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => attacker.SetElementAttack(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => defender.ElementResist(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => defender.SetElementResist(-1, 1));
    }

    [Fact]
    public void AMonsterIsAnActiveObjectButNotACharacter()
    {
        Assert.True(DamageObjectTypes.Is(DamageObjectTypes.Monster, DamageObjectTypes.Active));
        Assert.False(DamageObjectTypes.Is(DamageObjectTypes.Monster, DamageObjectTypes.Character));
    }

    [Fact]
    public void AbsoluteDefenseFillsThePenetrationCellsWithAScaleOfOne()
    {
        // 防御方的"绝对防御"(属性记录下标 96、99)进穿透项 +0x9B、+0xA7;穿透缩放取 1(攻击者面板没有削弱它的项)(E-161)
        DefenderPanel defender = Defender();
        defender.PhysicalPenetration = 50f;
        defender.MagicalPenetration = 25f;
        DamageVariables p = HitDamage.Fill(Physical(), Attacker(), defender, false, 1u);
        Assert.Equal((50f, 25f, 1f), (p.PhysicalPenetration, p.MagicalPenetration, p.PenetrationScale));

        DamageVariables none = HitDamage.Fill(Physical(), Attacker(), Defender(), false, 1u);
        Assert.Equal((0f, 0f, 1f), (none.PhysicalPenetration, none.MagicalPenetration, none.PenetrationScale));
    }

    [Fact]
    public void AbsoluteDefenseTakesPercentagePointsOffTheDamageMultiplier()
    {
        // 防御 0:防御率 0;穿透 50 → 50 × 0.01 = 0.5,乘数 (1 − 0) − 0.5 × 1 = 0.5:45 × 0.5 = 22.5 → 22
        DefenderPanel physical = Defender();
        physical.PhysicalPenetration = 50f;
        Assert.Equal(22, Roll(Physical(), Attacker(), physical));
        // 魔法一侧只看魔法那一格:80 × 0.5 = 40;物理那一格不影响
        var magical = new HitInput(0f, true, AttackKind.Magic, 100f, 0, Never);
        DefenderPanel both = Defender();
        both.MagicalPenetration = 50f;
        both.PhysicalPenetration = 90f;
        Assert.Equal(40, Roll(magical, Attacker(), both));
        // 无视防御的一击不套防御率,也不套这一项
        Assert.Equal(45, Roll(Physical(ignoreDefense: true), Attacker(), physical));
    }

    [Fact]
    public void AMonstersAbsoluteDefenseCellsComeFromItsRecordFloats()
    {
        var stats = new MonsterStats();
        stats.SetF(MonsterStats.IxPhysicalAbsoluteDefense, 12.5f);
        stats.SetF(MonsterStats.IxMagicalAbsoluteDefense, 7f);
        DefenderPanel panel = DefenderPanel.FromMonster(stats, 3);
        Assert.Equal((12.5f, 7f), (panel.PhysicalPenetration, panel.MagicalPenetration));
        Assert.Equal((0f, 0f), (DefenderPanel.FromMonster(new MonsterStats(), 3).PhysicalPenetration, DefenderPanel.FromMonster(new MonsterStats(), 3).MagicalPenetration));
    }
}
