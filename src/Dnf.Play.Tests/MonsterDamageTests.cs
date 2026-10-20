using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角的生命值(<see cref="HeroVitals"/>)与怪物打主角的伤害输入(<see cref="MonsterDamage"/>,证据 E-086)。
/// 输入怎么填是推断(客户端命中时的填充函数没读通),这里只验证"按文档说的方式填、交给原版公式",数全是合成的:
/// 公式常量取 物理/魔法攻击力项除以 1000、防御率的"每级减伤系数"100。
/// </summary>
public class MonsterDamageTests
{
    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static DamageConstants Constants(float randomBase = 0f, float randomRate = 0f)
    {
        return new DamageConstants
        {
            PhysicalDamageRate = 1000f,
            MagicalDamageRate = 1000f,
            PhysicalReduceRate = 100f,
            MagicalReduceRate = 100f,
            PhysicalRandomBase = randomBase,
            PhysicalRandomRate = randomRate,
            MagicalRandomBase = randomBase,
            MagicalRandomRate = randomRate,
        };
    }

    private static HeroVitals Victim(double physicalDefense = 100, double magicalDefense = 100)
    {
        return new HeroVitals(1, 100, 50, physicalDefense, magicalDefense, 0);
    }

    // ------------------------------------------------------------------ HeroVitals

    [Fact]
    public void FromChrReadsTheFirstOccurrenceOfTheInitialValueTags()
    {
        var chr = new CharacterDef(0, "character/test/mini.chr", new[]
        {
            new ScriptLine("[HP MAX]", new[] { I(120) }),
            new ScriptLine("[MP MAX]", new[] { I(60) }),
            new ScriptLine("[physical defense]", new[] { F(7.5f) }),
            new ScriptLine("[magical defense]", new[] { I(9) }),
            new ScriptLine("[hit recovery]", new[] { I(33) }),
            new ScriptLine("[HP MAX]", new[] { I(999) }),                // 后面还有成长表里的同名标签:只取第一次出现
            new ScriptLine("[hit recovery]", new[] { I(999) }),
        });
        HeroVitals v = HeroVitals.FromChr(chr, 3);
        Assert.Equal((3, 120, 120, 60, 60), (v.Level, v.HpMax, v.Hp, v.MpMax, v.Mp));
        Assert.Equal((7.5, 9.0, 33), (v.PhysicalDefense, v.MagicalDefense, v.HitRecovery));
        Assert.True(v.IsAlive);
    }

    [Fact]
    public void FromChrReadsNumbersStoredAsFloatsToo()
    {
        // 真实的 .chr 里这几项存成浮点数:按整数读会当成"没写",血量退成 1(第一次在真实数据上跑才发现,合成数据当初只用了整数)
        var chr = new CharacterDef(0, "character/test/floaty.chr", new[]
        {
            new ScriptLine("[HP MAX]", new[] { F(120f) }),
            new ScriptLine("[MP MAX]", new[] { F(60.5f) }),                // 四舍五入(远离零)
            new ScriptLine("[physical defense]", new[] { F(7.5f) }),
            new ScriptLine("[magical defense]", new[] { F(9f) }),
            new ScriptLine("[hit recovery]", new[] { F(33f) }),
        });
        HeroVitals v = HeroVitals.FromChr(chr);
        Assert.Equal((120, 120, 61, 61), (v.HpMax, v.Hp, v.MpMax, v.Mp));
        Assert.Equal((7.5, 9.0, 33), (v.PhysicalDefense, v.MagicalDefense, v.HitRecovery));
    }

    [Fact]
    public void CopyStateTakesTheOtherHeroesCurrentHpAndMpClampedToTheOwnMaximum()
    {
        var from = new HeroVitals(1, 100, 50, 0, 0, 0);
        from.Damage(30);                                                 // 70/100
        var to = new HeroVitals(1, 60, 20, 0, 0, 0);
        to.CopyStateFrom(from);
        Assert.Equal((60, 20), (to.Hp, to.Mp));                          // 比自己的上限大:压到上限

        var roomy = new HeroVitals(1, 200, 80, 0, 0, 0);
        roomy.CopyStateFrom(from);
        Assert.Equal((70, 50), (roomy.Hp, roomy.Mp));                    // 当前值原样带过去,不是回满

        Assert.Throws<ArgumentNullException>(() => roomy.CopyStateFrom(null!));
    }

    [Fact]
    public void MissingTagsGiveMinimalVitalsAndANullDefinitionToo()
    {
        var bare = new CharacterDef(0, "character/test/bare.chr", new[] { new ScriptLine("[job]", new[] { S("[mini]") }) });
        HeroVitals v = HeroVitals.FromChr(bare);
        Assert.Equal((1, 1, 0, 0.0, 0.0, 0), (v.HpMax, v.Hp, v.MpMax, v.PhysicalDefense, v.MagicalDefense, v.HitRecovery));

        HeroVitals none = HeroVitals.FromChr(null, 5);
        Assert.Equal((5, 1, 1), (none.Level, none.HpMax, none.Hp));
    }

    [Fact]
    public void ConstructorClampsNegativeValuesAndDamageReportsOnlyTheKillingBlow()
    {
        var v = new HeroVitals(0, 0, -5, -1, -2, -3);
        Assert.Equal((1, 1, 0, 0.0, 0.0, 0), (v.Level, v.HpMax, v.MpMax, v.PhysicalDefense, v.MagicalDefense, v.HitRecovery));

        var hero = new HeroVitals(1, 100, 10, 0, 0, 0);
        Assert.False(hero.Damage(30));
        Assert.Equal(70, hero.Hp);
        Assert.False(hero.Damage(-5));                                   // 负数不加血
        Assert.Equal(70, hero.Hp);
        Assert.True(hero.Damage(1000));                                  // 打到 0:这一下打死了
        Assert.Equal((0, false), (hero.Hp, hero.IsAlive));
        Assert.False(hero.Damage(10));                                   // 已经死了:不再算"打死"
        Assert.Equal(0, hero.Hp);

        hero.Restore();
        Assert.Equal((100, 10, true), (hero.Hp, hero.Mp, hero.IsAlive));
    }

    // ------------------------------------------------------------------ MonsterDamage

    [Fact]
    public void FillPutsTheDocumentedInputsIntoTheStructureAndNeutralValuesInTheRest()
    {
        var info = new HeroStrikeInfo(25, 0, false);
        HeroVitals victim = Victim(physicalDefense: 80.4, magicalDefense: 12.6);
        DamageVariables p = MonsterDamage.Fill(info, 7, 40, victim, 1234u);

        Assert.Equal(AttackKind.Physic, p.Kind);
        Assert.Equal(50.0f, p.Base);                                     // 40 × (100 + 25) / 100
        Assert.Equal((0.0f, 0.0f), (p.PhysicalAttack, p.MagicalAttack)); // 攻击力项填 0:怪物的攻击力已折进基础伤害
        Assert.Equal((sbyte)7, p.Level);
        Assert.Equal((80, 13), (p.PhysicalDefense, p.MagicalDefense));   // 四舍五入取整
        Assert.Equal((MonsterDamage.MonsterType, MonsterDamage.MonsterType, MonsterDamage.CharacterType), (p.AttackerType, p.DamagerType, p.DefenderType));
        Assert.Equal(1234u, p.Seed);
        Assert.Equal(-DamageFormula.CriticalBaseChance, p.CriticalRate);
        Assert.Equal((0.0f, 0.0f), (p.CriticalStatPhysical, p.CriticalStatMagical));   // 暴击属性的默认值是 1.0,不是中性

        // 默认值不是中性的,这里都改成"没有加成"
        Assert.Equal((0.0f, 0.0f, 0.0f, 0.0f), (p.DamagePercent, p.PhysicalPercent, p.MagicalPercent, p.ExtraRate));
        Assert.Equal((100.0f, 100), (p.FinalPercentA, p.PowerPercent));
        Assert.Equal((0, 0), (p.PhysicalDefensePercent, p.MagicalDefensePercent));

        DamageVariables magic = MonsterDamage.Fill(new HeroStrikeInfo(0, 0, false, magical: true), 1, 10, victim, 1u);
        Assert.Equal(AttackKind.Magic, magic.Kind);
        Assert.Equal(10.0f, magic.Base);
    }

    [Fact]
    public void FillRejectsAMissingVictimAndClampsLevelAndAttack()
    {
        Assert.Throws<ArgumentNullException>(() => MonsterDamage.Fill(new HeroStrikeInfo(0, 0, false), 1, 1, null, 1u));
        DamageVariables low = MonsterDamage.Fill(new HeroStrikeInfo(0, 0, false), -3, -10, Victim(), 1u);
        Assert.Equal(((sbyte)1, 0.0f), (low.Level, low.Base));
        DamageVariables high = MonsterDamage.Fill(new HeroStrikeInfo(0, 0, false), 4000, 10, Victim(), 1u);
        Assert.Equal(sbyte.MaxValue, high.Level);
        Assert.Throws<ArgumentNullException>(() => MonsterDamage.Roll(new HeroStrikeInfo(0, 0, false), 1, 1, Victim(), null, 1u));
    }

    [Fact]
    public void RollAppliesTheDefenseRateWithTheMonsterLevelInTheDenominator()
    {
        var info = new HeroStrikeInfo(25, 0, false);                     // 基础伤害 50
        DamageConstants k = Constants();
        Assert.Equal(25, MonsterDamage.Roll(info, 1, 40, Victim(100), k, 1u));       // 100 / (1 × 100 + 100) = 0.5
        Assert.Equal(37, MonsterDamage.Roll(info, 3, 40, Victim(100), k, 1u));       // 100 / (3 × 100 + 100) = 0.25 → 37.5 向零取整
        Assert.Equal(50, MonsterDamage.Roll(info, 9, 40, Victim(0), k, 1u));         // 没有防御
    }

    [Fact]
    public void RollNeverCriticalAndAlwaysAtLeastOne()
    {
        var info = new HeroStrikeInfo(25, 0, false);
        DamageConstants k = Constants();
        for (uint seed = 0; seed < 3000; seed++)
        {
            DamageVariables p = MonsterDamage.Fill(info, 1, 40, Victim(0), seed);
            Assert.Equal(50, DamageFormula.Compute(p, k));
            Assert.False(p.IsCritical);
        }
        Assert.Equal(1, MonsterDamage.Roll(info, 1, 0, Victim(0), k, 1u));           // 攻击力 0 也至少 1
        Assert.Equal(1, MonsterDamage.Roll(info, 1, 40, Victim(1e12), k, 1u));       // 防御率顶到上限
    }

    [Fact]
    public void TheRandomSpreadStaysInsideTheConfiguredRangeAndTheSeedIsTheOnlySourceOfRandomness()
    {
        var info = new HeroStrikeInfo(25, 0, false);                     // 基础伤害 50,幅度 ±(10 + 0 × 等级)
        DamageConstants k = Constants(randomBase: 10f);
        var seen = new HashSet<int>();
        for (uint seed = 1; seed <= 2000; seed++)
        {
            int damage = MonsterDamage.Roll(info, 1, 40, Victim(0), k, seed);
            Assert.InRange(damage, 40, 60);
            seen.Add(damage);
            Assert.Equal(damage, MonsterDamage.Roll(info, 1, 40, Victim(0), k, seed));   // 同一个种子同一个结果
        }
        Assert.True(seen.Count >= 15);                                   // 确实在 [40, 60] 里散开
    }

    [Fact]
    public void LoadConstantsIsNullWhenThePvfHasNoEtcParameterFile()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Null(MonsterDamage.LoadConstants(assets));
        Assert.Throws<ArgumentNullException>(() => MonsterDamage.LoadConstants(null));
    }
}
