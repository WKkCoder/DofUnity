using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 伤害公式的语义测试(证据 E-049)。常量与输入都是合成的,期望值来自手算(每个场景的注释里写了算式),
/// 没有用原版数值。完整的逐位核对由 <c>dnf damage-check --cases</c> 对 tools/py/damage_ref.py 做大批量对拍。
/// </summary>
public sealed class DamageFormulaTests
{
    private const uint Active = DamageObjectTypes.Active;
    private const uint Character = DamageObjectTypes.Character;
    private const uint Creature = DamageObjectTypes.Creature;
    private const uint Passive = DamageObjectTypes.Passive;

    /// <summary>
    /// 全部"直通"的一击:基础 1000,所有加成归零,默认值里不是中性的那几项(百分比、额外倍率、最终百分比、
    /// 威力百分比、防御百分比)都改成中性,暴击率压得足够低(攻击者的暴击属性默认是 1,也压得住),
    /// 所以结果等于基础伤害。注意攻击自带元素默认是 0(火):场景里带了火标记时会触发"同元素加成"。
    /// </summary>
    private static DamageVariables Neutral(float baseDamage = 1000f)
    {
        var p = new DamageVariables();
        p.Base = baseDamage;
        p.DamagePercent = 0f;
        p.PhysicalPercent = 0f;
        p.MagicalPercent = 0f;
        p.ExtraRate = 0f;
        p.CriticalRate = -1000f;
        p.FinalPercentA = 100f;
        p.PowerPercent = 100;
        p.PhysicalDefensePercent = 0;
        p.MagicalDefensePercent = 0;
        return p;
    }

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

    private static int Run(DamageVariables p, DamageConstants k = null)
    {
        return DamageFormula.Compute(p, k ?? Constants());
    }

    /// <summary>在 1.. 里找第一个种子,使它第 <paramref name="draw"/> 次(从 1 起)抽到的数 % 10000 满足条件。</summary>
    private static uint FindSeed(int draw, Func<int, bool> accept)
    {
        for (uint seed = 1; seed < 100000; seed++)
        {
            uint s = seed;
            int roll = 0;
            for (int i = 0; i < draw; i++)
            {
                roll = SdcRandom.Next(ref s) % 10000;
            }
            if (accept(roll))
            {
                return seed;
            }
        }
        throw new InvalidOperationException("no seed found");
    }

    // ---- 基础伤害 ----

    [Fact]
    public void NeutralHitPassesTheBaseDamageThrough()
    {
        DamageVariables p = Neutral();
        Assert.Equal(1000, Run(p));
        Assert.Equal(1000, p.Result);
        Assert.False(p.IsCritical);
        Assert.Equal(0, p.BonusHitDamage);
    }

    [Fact]
    public void AttackPowerScalesPhysicalAndMagicalDamage()
    {
        // 物理:1000 * (300 / 100 + 1) = 4000。
        DamageVariables physical = Neutral();
        physical.PhysicalAttack = 300f;
        Assert.Equal(4000, Run(physical));

        // 魔法:1000 * (100 / 100 + 1) = 2000;物理攻击力对魔法没有影响。
        DamageVariables magical = Neutral();
        magical.Kind = AttackKind.Magic;
        magical.MagicalAttack = 100f;
        magical.PhysicalAttack = 900f;
        Assert.Equal(2000, Run(magical));

        // 无视防御种类没有攻击力这一项(模块 14 去掉随机幅度)。
        DamageVariables ignoring = Neutral();
        ignoring.Kind = AttackKind.IgnoreDefense;
        ignoring.Module = 14;
        ignoring.PhysicalAttack = 900f;
        ignoring.MagicalAttack = 900f;
        Assert.Equal(1000, Run(ignoring));
    }

    [Fact]
    public void PercentBonusesMultiplyInOrder()
    {
        // 1000 * 1.5 * 1.2 = 1800,再加 额外倍率 0.1 * 1800 = 1980。
        DamageVariables p = Neutral();
        p.DamagePercent = 50f;
        p.PhysicalPercent = 20f;
        p.ExtraRate = 0.1f;
        Assert.Equal(1980, Run(p));

        // 魔法种类用魔法百分比,物理百分比不起作用。
        DamageVariables magic = Neutral();
        magic.Kind = AttackKind.Magic;
        magic.PhysicalPercent = 500f;
        magic.MagicalPercent = 100f;
        Assert.Equal(2000, Run(magic));
    }

    [Fact]
    public void BonusBaseAndFlatBonusAreAddedForTheMatchingKind()
    {
        // 固定值加成 100:直接加到伤害上。
        DamageVariables flat = Neutral();
        flat.FlatBonus = 100f;
        Assert.Equal(1100, Run(flat));

        // 要求附加基础值:基础 += 50;固定值基数 += 次数 3 * 单位量 10 = 130。
        DamageVariables bonus = Neutral();
        bonus.UseBonusBase = true;
        bonus.BonusBasePhysical = 50f;
        bonus.BonusBaseMagical = 7000f;
        bonus.BonusBaseCountPhysical = 3;
        bonus.BonusBaseScale = 10f;
        bonus.FlatBonus = 100f;
        Assert.Equal(1000 + 50 + 130, Run(bonus));

        // 魔法种类用魔法一侧的附加值与次数。
        DamageVariables magic = Neutral();
        magic.Kind = AttackKind.Magic;
        magic.UseBonusBase = true;
        magic.BonusBasePhysical = 7000f;
        magic.BonusBaseMagical = 500f;
        magic.BonusBaseCountMagical = 2;
        magic.BonusBaseScale = 10f;
        Assert.Equal(1000 + 500 + 20, Run(magic));
    }

    // ---- 随机幅度 ----

    [Fact]
    public void RandomSpreadStaysInsideItsRangeAndIsDeterministic()
    {
        DamageConstants k = Constants();
        k.PhysicalRandomBase = 3f;
        k.PhysicalRandomRate = 0.5f;

        var seen = new HashSet<int>();
        for (uint seed = 1; seed <= 300; seed++)
        {
            DamageVariables p = Neutral();
            p.Level = 10;
            p.Seed = seed;
            int first = Run(p, k);
            // 幅度 = 3 + 10 * 0.5 = 8:基础 1000 上下各 8(取整后 991 到 1008)。
            Assert.InRange(first, 991, 1008);
            seen.Add(first);
            // 同一份数据再算一次结果相同,结构里的种子不变。
            Assert.Equal(first, Run(p, k));
            Assert.Equal(seed, p.Seed);
        }
        Assert.True(seen.Count > 8);
    }

    [Fact]
    public void RandomSpreadMatchesAHandComputedDraw()
    {
        DamageConstants k = Constants();
        k.PhysicalRandomBase = 3f;
        k.PhysicalRandomRate = 0.5f;
        DamageVariables p = Neutral();
        p.Level = 10;
        p.Seed = 12345;

        // 种子 12345 的第一个随机数 1036784229,% 1000 = 229;
        // 幅度 8 → 加到 1000 上的是 -8 + 16 * 229 / 999 = -4.3323,结果 995.67 取整 995。
        uint s = 12345;
        Assert.Equal(229, SdcRandom.Next(ref s) % 1000);
        Assert.Equal(995, Run(p, k));
    }

    [Fact]
    public void SpreadUsesTheAlternateLevelOnlyForCharacterAttackersThatAskForIt()
    {
        DamageVariables p = Neutral();
        p.Level = 1;
        p.LevelAlt = 100;
        Assert.Equal(1, DamageFormula.BasisLevel(p, false));
        Assert.Equal(1, DamageFormula.BasisLevel(p, true));
        p.AttackerType = Character;
        Assert.Equal(1, DamageFormula.BasisLevel(p, false));
        Assert.Equal(100, DamageFormula.BasisLevel(p, true));
        // 有符号字节:负数等级保持负数。
        p.LevelAlt = -3;
        Assert.Equal(-3, DamageFormula.BasisLevel(p, true));
    }

    [Fact]
    public void ModuleFourteenRemovesTheSpread()
    {
        DamageConstants k = Constants();
        k.PhysicalRandomBase = 50f;
        k.PhysicalRandomRate = 1f;
        for (uint seed = 1; seed <= 50; seed++)
        {
            DamageVariables p = Neutral();
            p.Module = 14;
            p.Seed = seed;
            Assert.Equal(1000, Run(p, k));
        }
    }

    [Fact]
    public void IgnoreDefenseKindSpreadsBySevenPercentOfTheDamage()
    {
        var seen = new HashSet<int>();
        for (uint seed = 1; seed <= 300; seed++)
        {
            DamageVariables p = Neutral();
            p.Kind = AttackKind.IgnoreDefense;
            p.Seed = seed;
            int result = Run(p);
            // 幅度 = 1000 * 0.07 = 70。
            Assert.InRange(result, 929, 1070);
            seen.Add(result);
        }
        Assert.True(seen.Count > 50);
    }

    // ---- 防御 ----

    [Fact]
    public void DefenseReducesDamageByTheLevelBasedRate()
    {
        // 防御率 = 1000 / (10 * 20 + 1000) = 5/6;伤害 = 1000 * (1 - 5/6) = 166.67 → 166。
        DamageVariables physical = Neutral();
        physical.DefenderType = Active;
        physical.PhysicalDefense = 1000;
        physical.Level = 10;
        Assert.Equal(166, Run(physical));

        DamageVariables magical = Neutral();
        magical.Kind = AttackKind.Magic;
        magical.DefenderType = Active;
        magical.MagicalDefense = 1000;
        magical.PhysicalDefense = 99999;
        magical.Level = 10;
        Assert.Equal(166, Run(magical));
    }

    [Fact]
    public void DefenseIsSkippedWhenItShouldBe()
    {
        // 防御方不是活动对象。
        DamageVariables passive = Neutral();
        passive.PhysicalDefense = 1000;
        passive.Level = 10;
        Assert.Equal(1000, Run(passive));

        // 攻击要求跳过防御。
        DamageVariables ignoring = Neutral();
        ignoring.DefenderType = Active;
        ignoring.PhysicalDefense = 1000;
        ignoring.Level = 10;
        ignoring.IgnoreDefense = true;
        Assert.Equal(1000, Run(ignoring));

        // 无视防御种类(模块 14 去掉随机幅度)。
        DamageVariables kindTwo = Neutral();
        kindTwo.Kind = AttackKind.IgnoreDefense;
        kindTwo.Module = 14;
        kindTwo.DefenderType = Active;
        kindTwo.PhysicalDefense = 1000;
        kindTwo.Level = 10;
        Assert.Equal(1000, Run(kindTwo));
    }

    [Fact]
    public void WeakenedDefenseDoublesTheReduceTerm()
    {
        // 减伤系数翻倍:1000 / (10 * 40 + 1000) = 5/7;伤害 = 1000 * 2/7 = 285.7 → 285。
        DamageVariables p = Neutral();
        p.DefenderType = Active;
        p.PhysicalDefense = 1000;
        p.Level = 10;
        p.DefenseWeakened = true;
        Assert.Equal(285, Run(p));
    }

    [Fact]
    public void DefensePercentIsAnIntegerThatScalesDefense()
    {
        // 防御 1000、百分比 100 → 有效防御 2000;防御率 = 2000 / (10 * 20 + 2000) = 10/11;伤害 = 1000 / 11 = 90.9 → 90。
        DamageVariables p = Neutral();
        p.DefenderType = Active;
        p.PhysicalDefense = 1000;
        p.PhysicalDefensePercent = 100;
        p.Level = 10;
        Assert.Equal(90, Run(p));
    }

    [Fact]
    public void PenetrationLowersTheDefenseMultiplierAndIsFlooredByTheMaximumDefenseRate()
    {
        // 防御 200、等级 10:防御率 0.5;穿透 25 点 → 0.25;乘数 = 1 - 0.5 - 0.25 = 0.25 → 250。
        DamageVariables p = Neutral();
        p.DefenderType = Active;
        p.PhysicalDefense = 200;
        p.Level = 10;
        p.PhysicalPenetration = 25f;
        Assert.Equal(250, Run(p));

        // 穿透很大时乘数被"最大总防御率"托住(默认 0.01):1000 * 0.01 = 10。
        p.PhysicalPenetration = 5000f;
        Assert.Equal(10, Run(p));
        DamageConstants k = Constants();
        k.MaxTotalDefenseRate = 0.5f;
        Assert.Equal(500, Run(p, k));

        // 托底值为负时再与 0 取大,伤害归零后下限取 1。
        k.MaxTotalDefenseRate = -1f;
        Assert.Equal(1, Run(p, k));
    }

    [Fact]
    public void DefenseRateReportsRateAndPenetrationSeparately()
    {
        DamageConstants k = Constants();
        DamageVariables p = Neutral();
        p.PhysicalDefense = 200;
        p.Level = 10;
        p.PhysicalPenetration = 25f;
        // 方法本身不看防御方种类:调用方才决定要不要用。
        DamageFormula.DefenseRate(p, k, out float rate, out float penetration);
        Assert.Equal(0.5f, rate);
        Assert.Equal(0.25f, penetration);

        // 魔法一侧。
        p.Kind = AttackKind.Magic;
        p.MagicalDefense = 600;
        p.MagicalPenetration = 50f;
        DamageFormula.DefenseRate(p, k, out rate, out penetration);
        Assert.Equal(0.75f, rate);
        Assert.Equal(0.5f, penetration);

        // 无视防御种类:两者都是 0。
        p.Kind = AttackKind.IgnoreDefense;
        DamageFormula.DefenseRate(p, k, out rate, out penetration);
        Assert.Equal(0f, rate);
        Assert.Equal(0f, penetration);
    }

    [Fact]
    public void DefenseRateUsesTheFirstLevelEvenWhenTheSpreadUsesTheAlternate()
    {
        DamageConstants k = Constants();
        DamageVariables p = Neutral();
        p.PhysicalDefense = 200;
        p.Level = 10;
        p.LevelAlt = 100;
        p.UseBonusBase = true;
        p.AttackerType = Character;
        DamageFormula.DefenseRate(p, k, out float rate, out _);
        Assert.Equal(0.5f, rate);
    }

    [Fact]
    public void NegativeDefenseCountsAsZeroAndTheDenominatorHasAFloorOfOne()
    {
        DamageConstants k = Constants();
        DamageVariables p = Neutral();
        p.PhysicalDefense = -500;
        p.Level = 10;
        DamageFormula.DefenseRate(p, k, out float rate, out _);
        Assert.Equal(0f, rate);

        // 防御 0、等级 0:分母 0 被托成 1,防御率是 0(不是 NaN)。
        p.PhysicalDefense = 0;
        p.Level = 0;
        DamageFormula.DefenseRate(p, k, out rate, out _);
        Assert.Equal(0f, rate);

        // 防御 1、等级 0:分母 = 0 * 20 + 1 = 1,防御率 1。
        p.PhysicalDefense = 1;
        DamageFormula.DefenseRate(p, k, out rate, out _);
        Assert.Equal(1f, rate);
    }

    [Fact]
    public void FlatReductionAppliesOnlyToActiveDefenders()
    {
        // 防御为 0,所以防御乘数为 1。减去 B + A * 穿透缩放 = 10 + 30 * 2 = 70。
        DamageVariables p = Neutral();
        p.DefenderType = Active;
        p.PhysicalFlatReduceA = 30;
        p.PhysicalFlatReduceB = 10;
        p.PenetrationScale = 2f;
        Assert.Equal(930, Run(p));

        // 魔法种类读魔法那一组。
        DamageVariables magic = Neutral();
        magic.Kind = AttackKind.Magic;
        magic.DefenderType = Active;
        magic.PhysicalFlatReduceA = 999;
        magic.MagicalFlatReduceA = 30;
        magic.MagicalFlatReduceB = 10;
        magic.PenetrationScale = 2f;
        Assert.Equal(930, Run(magic));

        // 防御方不是活动对象:不减。
        DamageVariables passive = Neutral();
        passive.PhysicalFlatReduceA = 30;
        passive.PhysicalFlatReduceB = 10;
        Assert.Equal(1000, Run(passive));

        // 减伤率:伤害 -= 伤害 * 0.25(不分防御方种类)。
        DamageVariables rate = Neutral();
        rate.PhysicalReduceRate = 0.25f;
        Assert.Equal(750, Run(rate));
    }

    [Fact]
    public void ReductionNeverMakesTheResultLessThanOne()
    {
        DamageVariables p = Neutral();
        p.DefenderType = Active;
        p.PhysicalFlatReduceB = 5000;
        Assert.Equal(1, Run(p));
    }

    // ---- 元素 ----

    [Fact]
    public void ElementResistancePicksTheBestElementOfTheAttack()
    {
        // 火(0)与光(3):(100 - 20) * 0.01 = 0.8,(100 - 40) * 0.01 = 0.6;取大的,伤害 800。
        // 攻击自带元素设成"无"(4),免得同元素加成混进来。
        DamageVariables p = Neutral();
        p.DeclaredElement = 4;
        p.DefenderType = Active;
        p.SetElementFlag(0, true);
        p.SetElementFlag(3, true);
        p.SetElementResist(0, 20);
        p.SetElementResist(3, 40);
        p.ElementResistScale = 1f;
        Assert.Equal(800, Run(p));

        // 抗性缩放起作用:20 * 2 = 40 → 0.6;光为 40 * 2 = 80 → 0.2;取 600。
        p.ElementResistScale = 2f;
        Assert.Equal(600, Run(p));
    }

    [Fact]
    public void ElementResistanceIsSkippedWithoutFlagsOrForPassiveDefenders()
    {
        DamageVariables noFlags = Neutral();
        noFlags.DefenderType = Active;
        noFlags.SetElementResist(0, 50);
        Assert.Equal(1000, Run(noFlags));

        DamageVariables passive = Neutral();
        passive.DeclaredElement = 4;
        passive.SetElementFlag(0, true);
        passive.SetElementResist(0, 50);
        Assert.Equal(1000, Run(passive));
    }

    [Fact]
    public void SameElementBonusIsFivePercent()
    {
        // 攻击自带火元素(0)且元素标记里有火:1000 * 1.05。
        DamageVariables p = Neutral();
        p.DeclaredElement = 0;
        p.SetElementFlag(0, true);
        Assert.Equal(1050, Run(p));

        // 自带元素不在标记里、或是"无元素"(4):没有加成。
        p.DeclaredElement = 1;
        Assert.Equal(1000, Run(p));
        p.DeclaredElement = 4;
        Assert.Equal(1000, Run(p));
    }

    [Fact]
    public void ElementValidityMatchesTheOriginalOffsets()
    {
        DamageVariables p = Neutral();
        Assert.True(DamageFormula.IsValidElement(p, 4));
        Assert.False(DamageFormula.IsValidElement(p, 0));
        p.SetElementFlag(2, true);
        Assert.False(DamageFormula.IsValidElement(p, 4));
        Assert.True(DamageFormula.IsValidElement(p, 2));
        Assert.False(DamageFormula.IsValidElement(p, 1));
    }

    // ---- 暴击 ----

    [Fact]
    public void CriticalHitMultipliesByTheDefaultRate()
    {
        DamageVariables p = Neutral();
        p.CriticalRate = 10000f;
        Assert.Equal(1500, Run(p));
        Assert.True(p.IsCritical);
    }

    [Fact]
    public void ActiveAttackersScaleTheCriticalMultiplier()
    {
        // 活动攻击者:倍率 = 2 * 1.5 + 0.5 = 3.5。
        DamageVariables active = Neutral();
        active.CriticalRate = 10000f;
        active.AttackerType = Active;
        active.CriticalMultiplier = 2f;
        active.CriticalAdd = 0.5f;
        Assert.Equal(3500, Run(active));

        // 其它攻击者不套用缩放,只加加成:1.5 + 0.5 = 2。
        DamageVariables other = Neutral();
        other.CriticalRate = 10000f;
        other.CriticalMultiplier = 2f;
        other.CriticalAdd = 0.5f;
        Assert.Equal(2000, Run(other));
    }

    [Fact]
    public void CriticalChanceAddsOnlyTheApplicableTerms()
    {
        DamageConstants k = Constants();
        // 基础 3 + 暴击率 + 攻击者暴击属性(物理)。三个种子的第一次随机数都很小的情形不稳定,
        // 所以直接看"暴击率 * 100 > 随机数"的边界。
        uint seed = FindSeed(1, roll => roll >= 100 && roll % 25 == 0);
        uint s0 = seed;
        int roll = SdcRandom.Next(ref s0) % 10000;
        float exact = roll / 100f;

        DamageVariables p = Neutral();
        p.AttackerType = Active;
        p.CriticalStatPhysical = 0f;
        p.CriticalStatMagical = 99999f;
        p.CriticalRate = exact - 3f;

        float damage = 1000f;
        uint s = seed;
        Assert.False(DamageFormula.CheckCritical(p, k, ref damage, ref s));
        Assert.Equal(1000f, damage);

        // 暴击率高一格(0.25)就暴击;魔法属性对物理攻击不起作用。
        p.CriticalRate = exact - 3f + 0.25f;
        damage = 1000f;
        s = seed;
        Assert.True(DamageFormula.CheckCritical(p, k, ref damage, ref s));
        Assert.Equal(1500f, damage);

        // 攻击者属性(物理)被计入:CriticalStatPhysical 补上缺的 0.25。
        p.CriticalRate = exact - 3f;
        p.CriticalStatPhysical = 0.25f;
        damage = 1000f;
        s = seed;
        Assert.True(DamageFormula.CheckCritical(p, k, ref damage, ref s));

        // 防御方是活动对象:扣防御方的暴击抗性。
        p.DefenderType = Active;
        p.CriticalResist = 0.25f;
        damage = 1000f;
        s = seed;
        Assert.False(DamageFormula.CheckCritical(p, k, ref damage, ref s));
    }

    [Fact]
    public void CriticalRollIsStrictlyLessThanTheChance()
    {
        // Compute 里暴击用第二次随机数(第一次给了随机幅度)。
        DamageConstants k = Constants();
        uint seed = FindSeed(2, roll => roll >= 100 && roll % 25 == 0);
        uint s = seed;
        SdcRandom.Next(ref s);
        int roll = SdcRandom.Next(ref s) % 10000;
        float exact = roll / 100f;

        DamageVariables p = Neutral();
        p.Seed = seed;
        p.CriticalRate = exact - 3f;
        Assert.Equal(1000, Run(p, k));
        Assert.False(p.IsCritical);

        p.CriticalRate = exact - 3f + 0.25f;
        Assert.Equal(1500, Run(p, k));
        Assert.True(p.IsCritical);
    }

    [Fact]
    public void CheckCriticalAdvancesTheSeedOnceWhetherOrNotItCrits()
    {
        DamageConstants k = Constants();
        DamageVariables p = Neutral();
        uint expected = 777;
        SdcRandom.Next(ref expected);

        float damage = 1000f;
        uint miss = 777;
        Assert.False(DamageFormula.CheckCritical(p, k, ref damage, ref miss));
        Assert.Equal(expected, miss);

        p.CriticalRate = 10000f;
        uint hit = 777;
        Assert.True(DamageFormula.CheckCritical(p, k, ref damage, ref hit));
        Assert.Equal(expected, hit);
    }

    [Fact]
    public void SkillCriticalTermsNeedTheirFlag()
    {
        DamageConstants k = Constants();
        DamageVariables p = Neutral();
        p.SkillCriticalRate = 10000f;
        float damage = 1000f;
        uint seed = 5;
        Assert.False(DamageFormula.CheckCritical(p, k, ref damage, ref seed));

        p.SkillCriticalFlag = true;
        damage = 1000f;
        seed = 5;
        Assert.True(DamageFormula.CheckCritical(p, k, ref damage, ref seed));
    }

    [Fact]
    public void SummonedAttackersUseTheCreatureCriticalStat()
    {
        DamageConstants k = Constants();
        DamageVariables p = Neutral();
        p.CreatureSource = true;
        p.AttackerType = Creature;
        p.CreatureCriticalStat = 10000f;
        p.CriticalStatPhysical = 0f;
        float damage = 1000f;
        uint seed = 5;
        Assert.True(DamageFormula.CheckCritical(p, k, ref damage, ref seed));

        // 攻击者种类不是召唤物时不计入。
        p.AttackerType = 0;
        damage = 1000f;
        seed = 5;
        Assert.False(DamageFormula.CheckCritical(p, k, ref damage, ref seed));
    }

    // ---- 额外命中、最终倍率与下限 ----

    [Fact]
    public void BonusHitMultipliesByOnePointOneAndRecordsTheExtra()
    {
        DamageVariables p = Neutral();
        p.BonusHit = true;
        Assert.Equal(1100, Run(p));
        Assert.Equal(100, p.BonusHitDamage);
    }

    [Fact]
    public void FinalPercentagesAndRatesMultiplyTheDamage()
    {
        DamageVariables a = Neutral();
        a.FinalPercentA = 50f;
        Assert.Equal(500, Run(a));

        DamageVariables power = Neutral();
        power.PowerPercent = 250;
        Assert.Equal(2500, Run(power));

        DamageVariables rate = Neutral();
        rate.FinalRate = 1.5f;
        Assert.Equal(1500, Run(rate));
    }

    [Fact]
    public void ConditionalPercentsNeedTheirOwnFlags()
    {
        DamageVariables p = Neutral();
        p.SetConditionalPercent(0, 500f);
        p.SetConditionalPercent(1, 200f);
        p.SetConditionalPercent(2, 300f);
        Assert.Equal(1000, Run(p));

        p.SetConditionalFlag(1, true);
        Assert.Equal(2000, Run(p));
        p.SetConditionalFlag(2, true);
        Assert.Equal(6000, Run(p));
        p.SetConditionalFlag(0, true);
        Assert.Equal(30000, Run(p));
    }

    [Fact]
    public void DamageIsAtLeastOne()
    {
        Assert.Equal(1, Run(Neutral(0f)));
        Assert.Equal(1, Run(Neutral(-50f)));
        Assert.Equal(1, Run(Neutral(0.5f)));
    }

    // ---- 类型倍率表、攻击者与收尾 ----

    [Fact]
    public void TypeRatesApplyOnlyWhenTheDefenderOwnerIsACharacter()
    {
        DamageVariables p = Neutral();
        p.SetTypeRate(0, 2f);
        p.SetTypeRate(26, 3f);
        Assert.Equal(1000, Run(p));

        p.DefenderOwnerType = Character;
        Assert.Equal(2000, Run(p));

        // 暴击用后一组:1000 * 1.5 * 3,前一组的 2 不再乘。
        p.CriticalRate = 10000f;
        Assert.Equal(4500, Run(p));
        Assert.True(p.IsCritical);

        // 活动但不是角色的主人不套用。
        p.DefenderOwnerType = Active;
        Assert.Equal(1500, Run(p));
    }

    [Fact]
    public void AttackerTermsApplyOnlyToActiveAttackers()
    {
        DamageVariables active = Neutral();
        active.AttackerType = Active;
        active.AttackerRate = 1.5f;
        active.AttackerFlat = 25;
        Assert.Equal(1525, Run(active));

        DamageVariables other = Neutral();
        other.AttackerRate = 1.5f;
        other.AttackerFlat = 25;
        Assert.Equal(1000, Run(other));
    }

    [Fact]
    public void TailRateAppliesToEveryAttacker()
    {
        DamageVariables p = Neutral();
        p.TailRate = 2f;
        Assert.Equal(2000, Run(p));
    }

    [Fact]
    public void QuarterBonusNeedsFlagAndCharacterAttacker()
    {
        DamageVariables p = Neutral();
        p.QuarterBonusFlag = true;
        Assert.Equal(1000, Run(p));
        p.AttackerType = Active;
        Assert.Equal(1000, Run(p));
        p.AttackerType = Character;
        Assert.Equal(1250, Run(p));
        p.QuarterBonusFlag = false;
        Assert.Equal(1000, Run(p));
    }

    [Fact]
    public void FlatAddNeedsItsFlag()
    {
        DamageVariables p = Neutral();
        p.FlatAdd = 7;
        Assert.Equal(1000, Run(p));
        p.FlatAddFlag = true;
        Assert.Equal(1007, Run(p));
    }

    [Fact]
    public void SecondTailRateNeedsFlagAndCharacterAttacker()
    {
        DamageVariables p = Neutral();
        p.TailRate2 = 3f;
        p.TailRate2Flag = true;
        Assert.Equal(1000, Run(p));
        p.AttackerType = Character;
        Assert.Equal(3000, Run(p));
        p.TailRate2Flag = false;
        Assert.Equal(1000, Run(p));
    }

    [Fact]
    public void TailPercentAndAddComeLast()
    {
        DamageVariables p = Neutral();
        p.TailPercent = 10f;
        p.TailAdd = 5f;
        // (10 * 1000) / 100 + 1000 = 1100,再 + 5。
        Assert.Equal(1105, Run(p));
    }

    [Fact]
    public void TailTermsApplyInTheOriginalOrder()
    {
        // 角色攻击者,各收尾项都打开:
        // 1000 * 1.5 = 1500;+ 25 = 1525;* 2 = 3050;+ trunc(3050 * 0.25) = 3812;+ 7 = 3819;
        // * 3 = 11457;+ 11457 * 10 / 100 = 12602.7;+ 5 = 12607.7 → 12607。
        DamageVariables p = Neutral();
        p.AttackerType = Character;
        p.AttackerRate = 1.5f;
        p.AttackerFlat = 25;
        p.TailRate = 2f;
        p.QuarterBonusFlag = true;
        p.FlatAddFlag = true;
        p.FlatAdd = 7;
        p.TailRate2Flag = true;
        p.TailRate2 = 3f;
        p.TailPercent = 10f;
        p.TailAdd = 5f;
        Assert.Equal(12607, Run(p));
    }

    // ---- 召唤物 ----

    private static DamageConstants CreatureConstants()
    {
        DamageConstants k = Constants();
        // 退化的向量:等级倍率 = 等级 / 100。
        k.SetCreatureDamageVector(0, 1, 0, 0, 1, 0, 1);
        return k;
    }

    [Fact]
    public void CreatureDamagersUseTheCreatureFormula()
    {
        // 等级 20 → 倍率 0.2;基础 1000 → 200。
        DamageVariables p = Neutral();
        p.DamagerType = Creature;
        p.CreatureHit = true;
        p.CreatureLevel = 20;
        Assert.Equal(200, Run(p, CreatureConstants()));

        // 额外量取整数部分加进去:(0 + 30) + 20 = 50 → 0.5。
        p.CreatureExtra = 30.9;
        Assert.Equal(500, Run(p, CreatureConstants()));
    }

    [Fact]
    public void CreatureFormulaAppliesCreatureRateModuleRateAndIndexRate()
    {
        DamageConstants k = CreatureConstants();
        k.AddCreatureModuleRate(9, 0.5f);
        k.SetCreatureIndexRate(2, 0.5f);

        DamageVariables p = Neutral();
        p.DamagerType = Creature;
        p.CreatureHit = true;
        p.CreatureLevel = 20;
        p.CreatureRate = 3f;
        p.CreatureIndex = 2;

        // 模块 0:没有模块倍率、没有序号折减;200 * 3 = 600。
        Assert.Equal(600, Run(p, k));

        // 模块 9:再乘 0.5(模块)与 0.5(序号):600 * 0.25 = 150。
        p.Module = 9;
        Assert.Equal(150, Run(p, k));

        // 序号折减只在模块 9 起作用。
        k.AddCreatureModuleRate(4, 0.5f);
        p.Module = 4;
        Assert.Equal(300, Run(p, k));
    }

    [Fact]
    public void CreatureHitSwitchOffGivesTheMinimumDamage()
    {
        DamageVariables p = Neutral();
        p.DamagerType = Creature;
        p.CreatureHit = false;
        p.CreatureLevel = 20;
        Assert.Equal(1, Run(p, CreatureConstants()));
        Assert.Equal(0f, DamageFormula.InflictCreature(p, CreatureConstants()));
    }

    [Fact]
    public void PassiveDamagersTakeTheCreaturePathOnlyWithBothSwitches()
    {
        DamageConstants k = CreatureConstants();
        DamageVariables p = Neutral();
        p.DamagerType = Passive;
        p.CreatureLevel = 20;

        Assert.Equal(1000, Run(p, k));
        p.CreatureHit = true;
        Assert.Equal(1000, Run(p, k));
        p.CreatureSource = true;
        Assert.Equal(200, Run(p, k));
        p.CreatureHit = false;
        Assert.Equal(1000, Run(p, k));
    }

    // ---- 小函数与常量 ----

    [Fact]
    public void UniformVelocityInterpolatesBetweenTheEnds()
    {
        Assert.Equal(-8.0, DamageFormula.UniformVelocity(-8f, 8f, 0, 999));
        Assert.Equal(-8.0, DamageFormula.UniformVelocity(-8f, 8f, -5, 999));
        Assert.Equal(8.0, DamageFormula.UniformVelocity(-8f, 8f, 999, 999));
        Assert.Equal(8.0, DamageFormula.UniformVelocity(-8f, 8f, 5000, 999));
        Assert.Equal(16.0 * 500 / 999 - 8.0, DamageFormula.UniformVelocity(-8f, 8f, 500, 999), 12);
        Assert.Equal(0.0, DamageFormula.UniformVelocity(0f, 0f, 1, 999));
    }

    [Fact]
    public void TruncateToIntFollowsFistpTruncation()
    {
        Assert.Equal(1, DamageFormula.TruncateToInt(1.9));
        Assert.Equal(-1, DamageFormula.TruncateToInt(-1.9));
        Assert.Equal(0, DamageFormula.TruncateToInt(-0.99));
        Assert.Equal(int.MaxValue, DamageFormula.TruncateToInt(2147483647.9));
        Assert.Equal(int.MinValue, DamageFormula.TruncateToInt(2147483648.0));
        Assert.Equal(int.MinValue, DamageFormula.TruncateToInt(-2147483648.9));
        Assert.Equal(int.MinValue, DamageFormula.TruncateToInt(-2147483649.0));
        Assert.Equal(int.MinValue, DamageFormula.TruncateToInt(double.NaN));
        Assert.Equal(int.MinValue, DamageFormula.TruncateToInt(double.PositiveInfinity));
        Assert.Equal(int.MinValue, DamageFormula.TruncateToInt(double.NegativeInfinity));
    }

    [Fact]
    public void FloatConstantsHaveTheBitPatternsOfTheOriginalConstants()
    {
        // 与程序里的常量(或指令立即数)按位一致,这些位模式是从原程序读出来的。
        Assert.Equal(0x3C23D70A, BitConverter.SingleToInt32Bits(DamageFormula.Percent));
        Assert.Equal(0x3F866666, BitConverter.SingleToInt32Bits(DamageFormula.SameElementBonus));
        Assert.Equal(0x3D8F5C29, BitConverter.SingleToInt32Bits(DamageFormula.FixedSpreadRate));
        Assert.Equal(0x3E800000, BitConverter.SingleToInt32Bits(DamageFormula.QuarterRate));
        Assert.Equal(0x42C80000, BitConverter.SingleToInt32Bits(DamageFormula.Hundred));
        Assert.Equal(0x3F8CCCCD, BitConverter.SingleToInt32Bits(DamageFormula.BonusHitRate));
        Assert.Equal(0x40400000, BitConverter.SingleToInt32Bits(DamageFormula.CriticalBaseChance));
        Assert.Equal(10000, DamageFormula.CriticalRollModulus);
        Assert.Equal(1000, DamageFormula.SpreadRollModulus);
        Assert.Equal(999, DamageFormula.SpreadSteps);
    }

    [Fact]
    public void ObjectTypeMasksFollowTheServerChecks()
    {
        Assert.True(DamageObjectTypes.Is(0x111, DamageObjectTypes.Active));
        Assert.True(DamageObjectTypes.Is(0x211, DamageObjectTypes.Active));
        Assert.True(DamageObjectTypes.Is(0x11, DamageObjectTypes.Active));
        Assert.False(DamageObjectTypes.Is(0x01, DamageObjectTypes.Active));
        Assert.True(DamageObjectTypes.Is(0x111, DamageObjectTypes.Character));
        Assert.False(DamageObjectTypes.Is(0x211, DamageObjectTypes.Character));
        Assert.True(DamageObjectTypes.Is(0x81, DamageObjectTypes.Creature));
        Assert.True(DamageObjectTypes.Is(0x21, DamageObjectTypes.Passive));
        Assert.False(DamageObjectTypes.Is(0x81, DamageObjectTypes.Passive));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => DamageFormula.Compute(null, Constants()));
        Assert.Throws<ArgumentNullException>(() => DamageFormula.Compute(Neutral(), null));
    }

    // ---- 随机数 ----

    [Theory]
    [InlineData(1u, 476707713, 1186278907, 505671508, 3210001534u)]
    [InlineData(12345u, 1036784229, 1520991917, 1373464794, 551188310u)]
    [InlineData(0xFFFFFFFFu, 1670702726, 99100226, 931463008, 2673909348u)]
    [InlineData(0u, 1012484, 1716955679, 1792309082, 2941955441u)]
    public void SdcRandomMatchesTheGlibcRandRAlgorithm(uint seed, int first, int second, int third, uint finalSeed)
    {
        // 期望值由文档里的 glibc rand_r 算法独立算出(tools/py/damage_ref.py 的自检里也核对过)。
        uint s = seed;
        Assert.Equal(first, SdcRandom.Next(ref s));
        Assert.Equal(second, SdcRandom.Next(ref s));
        Assert.Equal(third, SdcRandom.Next(ref s));
        Assert.Equal(finalSeed, s);
    }

    [Fact]
    public void SdcRandomOutputsAreNonNegative()
    {
        uint s = 0xDEADBEEF;
        for (int i = 0; i < 10000; i++)
        {
            Assert.True(SdcRandom.Next(ref s) >= 0);
        }
    }
}
