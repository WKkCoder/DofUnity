using Dnf.Data;
using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>
/// 强化等级与品级值换算成装备属性:品级浮动的随机数、档位与各档公式(期望值来自独立的 Python 实现 tools/py/enhance_effect_ref.py,输入全是合成的)、
/// 强化表按部位分路、武器倍率表的读法与取值、合成的 <see cref="IEquipInstanceBonus"/> 接进装备栏汇总。物品编号 900 起,数值随手写。
/// 整库对拍在 enhance-check 里(与 Python 逐行比对,只报计数)。
/// </summary>
public class EquipInstanceBonusTests
{
    private static ScriptValue I(int v) => TableBuilder.I(v);

    private static ScriptValue F(float v) => TableBuilder.F(v);

    private static ItemInfo Parse(TokenStreamBuilder b, int code = 900)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, ItemKind.Equipment, code, "equipment/test/e" + code + ".equ");
    }

    // ------------------------------------------------------------------ 随机数

    [Theory]
    [InlineData(1u, 476707713, 0x2781e494u)]
    [InlineData(12345u, 1036784229, 0xd6651c2cu)]
    [InlineData(0x3b9ac9feu, 779771089, 0x18d18075u)]
    [InlineData(0u, 1012484, 0xa70427dfu)]
    [InlineData(0xffffffffu, 1670702726, 0x26866b2au)]
    public void TheRandomNumberWalksThreeCongruentStepsAndMixesTheirHighBits(uint seed, int expected, uint nextState)
    {
        uint state = seed;
        Assert.Equal(expected, EquipGradeVariation.NextRandom(ref state));
        Assert.Equal(nextState, state);
    }

    [Fact]
    public void TheRandomNumbersChainThroughTheState()
    {
        uint state = 7;
        Assert.Equal(1187592820, EquipGradeVariation.NextRandom(ref state));
        Assert.Equal(0x2a7450d2u, state);
        Assert.Equal(153898277, EquipGradeVariation.NextRandom(ref state));
        Assert.Equal(1377876785, EquipGradeVariation.NextRandom(ref state));
        Assert.Equal(0x1f3116ccu, state);
    }

    [Fact]
    public void TheRandomNumberIsNeverNegative()
    {
        uint state = 12345;
        for (int i = 0; i < 20000; i++)
        {
            Assert.True(EquipGradeVariation.NextRandom(ref state) >= 0);
        }
    }

    // ------------------------------------------------------------------ 档位

    [Theory]
    [InlineData(1, 0)]
    [InlineData(13, 0)]
    [InlineData(3, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(16, 2)]
    [InlineData(2, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(12, 4)]
    [InlineData(0, 4)]
    [InlineData(-5, 2)]
    public void TheBandComesFromTheFirstDrawModOneHundred(int gradeValue, int band)
    {
        Assert.Equal(band, EquipGradeVariation.BandOf(gradeValue));
    }

    [Fact]
    public void TheBandSplitsAFlatRangeOfSeedsIntoFiveRoughlyEqualParts()
    {
        var counts = new int[EquipGradeVariation.BandCount];
        for (int seed = 0; seed < 10000; seed++)
        {
            counts[EquipGradeVariation.BandOf(seed)]++;
        }
        Assert.Equal(new[] { 1978, 1998, 2025, 1997, 2002 }, counts);
    }

    // ------------------------------------------------------------------ 取整

    [Fact]
    public void RoundingGoesHalfAwayFromZeroInSinglePrecision()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 0, -1, -2 },
            new[] { 0.4f, 0.5f, 1.5f, 2.5f, -0.4f, -0.5f, -1.5f }.Select(EquipGradeVariation.RoundHalfAway).ToArray());
        // 刚好小于 0.5 的单精度数:加 0.5 在单精度里进位成 1.0(平局取偶),照原版走成 1 / −1
        float justBelowHalf = BitConverter.Int32BitsToSingle(0x3EFFFFFF);
        Assert.Equal(1, EquipGradeVariation.RoundHalfAway(justBelowHalf));
        Assert.Equal(-1, EquipGradeVariation.RoundHalfAway(-justBelowHalf));
    }

    // ------------------------------------------------------------------ 单项浮动

    // 期望值来自 Python 参考实现。第一组:基础 1000、百分比 10、上限 50(幅度 50;W=101,a=20,b=20,c=21)
    [Theory]
    [InlineData(1000, 10, 50, 0, 7u, 950, 0x2a7450d2u)]
    [InlineData(1000, 10, 50, 0, 99u, 966, 0x57a421deu)]
    [InlineData(1000, 10, 50, 1, 7u, 970, 0x2a7450d2u)]
    [InlineData(1000, 10, 50, 1, 99u, 986, 0x57a421deu)]
    [InlineData(1000, 10, 50, 2, 7u, 991, 0x2a7450d2u)]
    [InlineData(1000, 10, 50, 2, 99u, 995, 0x57a421deu)]
    [InlineData(1000, 10, 50, 3, 7u, 1030, 0x2a7450d2u)]
    [InlineData(1000, 10, 50, 3, 99u, 1014, 0x57a421deu)]
    [InlineData(1000, 10, 50, 4, 7u, 1050, 0x2a7450d2u)]
    [InlineData(1000, 10, 50, 4, 99u, 1034, 0x57a421deu)]
    // 基础 37、百分比 15、上限 1000:幅度 round(5.55) = 6
    [InlineData(37, 15, 1000, 0, 7u, 31, 0x2a7450d2u)]
    [InlineData(37, 15, 1000, 1, 99u, 35, 0x57a421deu)]
    [InlineData(37, 15, 1000, 2, 99u, 38, 0x57a421deu)]
    [InlineData(37, 15, 1000, 3, 7u, 40, 0x2a7450d2u)]
    [InlineData(37, 15, 1000, 4, 7u, 43, 0x2a7450d2u)]
    // 上限比幅度小:幅度被上限截成 7
    [InlineData(1000, 50, 7, 0, 7u, 994, 0x2a7450d2u)]
    [InlineData(1000, 50, 7, 2, 99u, 1001, 0x57a421deu)]
    [InlineData(1000, 50, 7, 4, 7u, 1006, 0x2a7450d2u)]
    // 上限 0:幅度 0,五档都不变,随机数仍然抽(状态照常往前走)
    [InlineData(1000, 10, 0, 0, 7u, 1000, 0x2a7450d2u)]
    [InlineData(1000, 10, 0, 3, 99u, 1000, 0x57a421deu)]
    [InlineData(1000, 10, 0, 4, 7u, 1000, 0x2a7450d2u)]
    // 负的基础值:幅度 −10(W 取 1,a = b = c = 1)
    [InlineData(-100, 10, 1000, 0, 7u, -90, 0x2a7450d2u)]
    [InlineData(-100, 10, 1000, 2, 7u, -100, 0x2a7450d2u)]
    [InlineData(-100, 10, 1000, 4, 99u, -110, 0x57a421deu)]
    // 基础 5、百分比 10:0.5 四舍五入成 1
    [InlineData(5, 10, 1000, 0, 7u, 4, 0x2a7450d2u)]
    [InlineData(5, 10, 1000, 4, 99u, 6, 0x57a421deu)]
    public void EachBandFollowsItsOwnFormula(int baseValue, int percent, int cap, int band, uint seed, int expected, uint nextState)
    {
        uint state = seed;
        Assert.Equal(expected, EquipGradeVariation.Jitter(baseValue, percent, cap, band, ref state));
        Assert.Equal(nextState, state);
    }

    // 小基础值(幅度 0 到 5,W 与 a、b、c 的下限都会起作用)与负值,五档各两颗种子;期望值来自 Python 参考实现
    private static readonly (int Base, int Percent, int Cap, int Band, uint Seed, int Expected, uint NextState)[] SmallRanges =
    {
        (4, 10, 50, 0, 7u, 4, 0x2a7450d2u),
        (4, 10, 50, 0, 99u, 4, 0x57a421deu),
        (4, 10, 50, 1, 7u, 4, 0x2a7450d2u),
        (4, 10, 50, 1, 99u, 4, 0x57a421deu),
        (4, 10, 50, 2, 7u, 4, 0x2a7450d2u),
        (4, 10, 50, 2, 99u, 4, 0x57a421deu),
        (4, 10, 50, 3, 7u, 4, 0x2a7450d2u),
        (4, 10, 50, 3, 99u, 4, 0x57a421deu),
        (4, 10, 50, 4, 7u, 4, 0x2a7450d2u),
        (4, 10, 50, 4, 99u, 4, 0x57a421deu),
        (5, 10, 50, 0, 7u, 4, 0x2a7450d2u),
        (5, 10, 50, 0, 99u, 4, 0x57a421deu),
        (5, 10, 50, 1, 7u, 5, 0x2a7450d2u),
        (5, 10, 50, 1, 99u, 5, 0x57a421deu),
        (5, 10, 50, 2, 7u, 5, 0x2a7450d2u),
        (5, 10, 50, 2, 99u, 5, 0x57a421deu),
        (5, 10, 50, 3, 7u, 5, 0x2a7450d2u),
        (5, 10, 50, 3, 99u, 5, 0x57a421deu),
        (5, 10, 50, 4, 7u, 6, 0x2a7450d2u),
        (5, 10, 50, 4, 99u, 6, 0x57a421deu),
        (11, 10, 50, 0, 7u, 10, 0x2a7450d2u),
        (11, 10, 50, 0, 99u, 10, 0x57a421deu),
        (11, 10, 50, 1, 7u, 11, 0x2a7450d2u),
        (11, 10, 50, 1, 99u, 11, 0x57a421deu),
        (11, 10, 50, 2, 7u, 11, 0x2a7450d2u),
        (11, 10, 50, 2, 99u, 11, 0x57a421deu),
        (11, 10, 50, 3, 7u, 11, 0x2a7450d2u),
        (11, 10, 50, 3, 99u, 11, 0x57a421deu),
        (11, 10, 50, 4, 7u, 12, 0x2a7450d2u),
        (11, 10, 50, 4, 99u, 12, 0x57a421deu),
        (20, 10, 50, 0, 7u, 18, 0x2a7450d2u),
        (20, 10, 50, 0, 99u, 18, 0x57a421deu),
        (20, 10, 50, 1, 7u, 19, 0x2a7450d2u),
        (20, 10, 50, 1, 99u, 19, 0x57a421deu),
        (20, 10, 50, 2, 7u, 20, 0x2a7450d2u),
        (20, 10, 50, 2, 99u, 20, 0x57a421deu),
        (20, 10, 50, 3, 7u, 21, 0x2a7450d2u),
        (20, 10, 50, 3, 99u, 21, 0x57a421deu),
        (20, 10, 50, 4, 7u, 22, 0x2a7450d2u),
        (20, 10, 50, 4, 99u, 22, 0x57a421deu),
        (60, 10, 50, 0, 7u, 54, 0x2a7450d2u),
        (60, 10, 50, 0, 99u, 54, 0x57a421deu),
        (60, 10, 50, 1, 7u, 57, 0x2a7450d2u),
        (60, 10, 50, 1, 99u, 58, 0x57a421deu),
        (60, 10, 50, 2, 7u, 60, 0x2a7450d2u),
        (60, 10, 50, 2, 99u, 61, 0x57a421deu),
        (60, 10, 50, 3, 7u, 63, 0x2a7450d2u),
        (60, 10, 50, 3, 99u, 62, 0x57a421deu),
        (60, 10, 50, 4, 7u, 66, 0x2a7450d2u),
        (60, 10, 50, 4, 99u, 66, 0x57a421deu),
        (-4, 10, 50, 0, 7u, -4, 0x2a7450d2u),
        (-4, 10, 50, 0, 99u, -4, 0x57a421deu),
        (-4, 10, 50, 1, 7u, -4, 0x2a7450d2u),
        (-4, 10, 50, 1, 99u, -4, 0x57a421deu),
        (-4, 10, 50, 2, 7u, -4, 0x2a7450d2u),
        (-4, 10, 50, 2, 99u, -4, 0x57a421deu),
        (-4, 10, 50, 3, 7u, -4, 0x2a7450d2u),
        (-4, 10, 50, 3, 99u, -4, 0x57a421deu),
        (-4, 10, 50, 4, 7u, -4, 0x2a7450d2u),
        (-4, 10, 50, 4, 99u, -4, 0x57a421deu),
        (-20, 10, 50, 0, 7u, -18, 0x2a7450d2u),
        (-20, 10, 50, 0, 99u, -18, 0x57a421deu),
        (-20, 10, 50, 1, 7u, -20, 0x2a7450d2u),
        (-20, 10, 50, 1, 99u, -20, 0x57a421deu),
        (-20, 10, 50, 2, 7u, -20, 0x2a7450d2u),
        (-20, 10, 50, 2, 99u, -20, 0x57a421deu),
        (-20, 10, 50, 3, 7u, -20, 0x2a7450d2u),
        (-20, 10, 50, 3, 99u, -20, 0x57a421deu),
        (-20, 10, 50, 4, 7u, -22, 0x2a7450d2u),
        (-20, 10, 50, 4, 99u, -22, 0x57a421deu),
    };

    [Fact]
    public void SmallAndNegativeRangesFollowTheReferenceInEveryBand()
    {
        foreach (var row in SmallRanges)
        {
            uint state = row.Seed;
            int got = EquipGradeVariation.Jitter(row.Base, row.Percent, row.Cap, row.Band, ref state);
            Assert.True(row.Expected == got && row.NextState == state, $"base {row.Base} band {row.Band} seed {row.Seed}: {got}/{state:x8}");
        }
    }

    [Fact]
    public void ABaseOfZeroStaysZeroAndDrawsNothing()
    {
        uint state = 7;
        Assert.Equal(0, EquipGradeVariation.Jitter(0, 10, 50, 2, ref state));
        Assert.Equal(7u, state);
    }

    [Fact]
    public void TheSentinelSeedGivesTheTopOfTheRangeInBandFourWithoutDrawing()
    {
        uint state = 0x3b9ac9fe;
        Assert.Equal(1050, EquipGradeVariation.Jitter(1000, 10, 50, 4, ref state));
        Assert.Equal(0x3b9ac9feu, state);
        // 别的档位不走这条捷径:抽一次,状态往前走
        Assert.Equal(1021, EquipGradeVariation.Jitter(1000, 10, 50, 3, ref state));
        Assert.Equal(0x18d18075u, state);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void ABandOutsideZeroToFourLeavesTheValueAlone(int band)
    {
        uint state = 7;
        Assert.Equal(1000, EquipGradeVariation.Jitter(1000, 10, 50, band, ref state));
        Assert.Equal(7u, state);
    }

    // ------------------------------------------------------------------ 整件装备

    private static EquipGradeVariation Flat(int percent, int cap)
    {
        return new EquipGradeVariation(Enumerable.Repeat(percent, 17).ToArray(), Enumerable.Repeat(cap, 17).ToArray());
    }

    private static ItemInfo Equip(int part, params (EquipStat Stat, double Value)[] stats)
    {
        ItemInfo item = EnhanceFixtures.Gear(900, part, 0, 0);
        var b = new EquipStats.Builder();
        foreach ((EquipStat stat, double value) in stats)
        {
            b.Set(stat, value);
        }
        item.Stats = b.Build();
        return item;
    }

    [Theory]
    [InlineData(7, -30, -2)]
    [InlineData(99, -14, -2)]
    [InlineData(12345, -21, -2)]
    [InlineData(-5, 4, -1)]
    [InlineData(1, -37, -4)]
    [InlineData(2, 23, 2)]
    [InlineData(2147483647, -24, -3)]
    [InlineData(999999998, 50, 4)]                  // 哨兵种子:档 4 且状态正好是哨兵,每项都取区间顶、不抽随机数
    [InlineData(-2147483648, 46, 4)]
    public void AnItemIsVariedStatByStatWithOneSharedState(int seed, int hp, int physical)
    {
        // 生命 1000、物攻 37、其余没有:魔法(下标 1)是 0,不抽随机数,物攻接着生命的状态往下抽
        ItemInfo item = Equip(EquipmentParts.Coat, (EquipStat.HpMax, 1000), (EquipStat.PhysicalAttack, 37));
        EquipStats delta = Flat(10, 50).DeltaOf(item.Stats, item.Part, seed);
        Assert.Equal(hp, delta[EquipStat.HpMax]);
        Assert.Equal(physical, delta[EquipStat.PhysicalAttack]);
        Assert.Equal(2, delta.Count);
    }

    [Theory]
    [InlineData(3, new[] { -10, -8, -8, -13, -25, -20, -18, -32, -36, -45 })]
    [InlineData(8, new[] { 1, -1, -6, 1, 3, 8, 7, -6, -16, 8 })]
    [InlineData(2, new[] { 8, 7, 19, 26, 27, 34, 33, 34, 38, 23 })]
    [InlineData(5, new[] { 21, 23, 29, 40, 45, 54, 59, 66, 51, 65 })]
    [InlineData(1, new[] { -22, -24, -24, -39, -33, -44, -51, -68, -55, -72 })]
    [InlineData(7, new[] { -15, -19, -17, -15, -17, -13, -25, -19, -25, -33 })]
    public void AllTenStatsGoInTheClientsOrderEachWithItsOwnPair(int seed, int[] expected)
    {
        // 十项基础值 500 + 13i;第 i 对 = (5 + i)% 与上限 100 + 7i(期望值是 Python 实现按档 0 到 4 各取一个种子算的)
        var variation = new EquipGradeVariation(Enumerable.Range(0, 17).Select(i => 5 + i).ToArray(), Enumerable.Range(0, 17).Select(i => 100 + (7 * i)).ToArray());
        var b = new EquipStats.Builder();
        for (int i = 0; i < EquipGradeVariation.ModeledPairs; i++)
        {
            b.Set(EquipGradeVariation.Stats[i], 500 + (13 * i));
        }
        EquipStats delta = variation.DeltaOf(b.Build(), EquipmentParts.Weapon, seed);
        Assert.Equal(expected, EquipGradeVariation.Stats.Select(s => (int)delta[s]).ToArray());
    }

    [Fact]
    public void TheStatsAreVariedInTheClientsOrderNotTheEnumsOrder()
    {
        Assert.Equal(new[]
        {
            EquipStat.HpMax, EquipStat.MpMax, EquipStat.PhysicalAttack, EquipStat.PhysicalDefense, EquipStat.MagicalAttack, EquipStat.MagicalDefense,
            EquipStat.EquipmentPhysicalAttack, EquipStat.EquipmentPhysicalDefense, EquipStat.EquipmentMagicalAttack, EquipStat.EquipmentMagicalDefense,
        }, EquipGradeVariation.Stats);
    }

    [Fact]
    public void OnlyTheFirstNumberIsVariedAndTheSecondIsNotInTheDelta()
    {
        var b = new EquipStats.Builder().Set(EquipStat.HpMax, 1000, 5);
        EquipStats delta = Flat(10, 50).DeltaOf(b.Build(), EquipmentParts.Coat, 7);
        Assert.Equal(-30.0, delta[EquipStat.HpMax]);
        Assert.False(delta.HasSecond(EquipStat.HpMax));
    }

    [Fact]
    public void StatsOutsideTheTenAreNotTouched()
    {
        ItemInfo item = Equip(EquipmentParts.Coat, (EquipStat.AttackSpeed, 500), (EquipStat.PhysicalCriticalHit, 300), (EquipStat.FireAttack, 40));
        Assert.True(Flat(10, 50).DeltaOf(item.Stats, item.Part, 7).IsEmpty);
    }

    [Theory]
    [InlineData(EquipmentParts.Weapon, true)]
    [InlineData(EquipmentParts.TitleName, false)]
    [InlineData(EquipmentParts.Coat, true)]
    [InlineData(EquipmentParts.Waist, true)]
    [InlineData(EquipmentParts.MagicStone, true)]
    [InlineData(EquipmentParts.Creature, false)]
    [InlineData(EquipmentParts.ArtifactRed, true)]
    [InlineData(EquipmentParts.ArtifactGreen, true)]
    [InlineData(EquipmentParts.Unknown, false)]
    [InlineData(EquipmentParts.AuroraAvatar, false)]
    [InlineData(EquipmentParts.HatAvatar, false)]
    [InlineData(-1, false)]
    public void OnlyWearableGearIsVariedNotTitlesCreaturesOrAvatars(int part, bool applies)
    {
        Assert.Equal(applies, EquipGradeVariation.AppliesTo(part));
        EquipStats stats = Stats(EquipStat.HpMax, 1000);
        Assert.Equal(applies, !Flat(10, 50).DeltaOf(stats, part, 7).IsEmpty);
    }

    [Fact]
    public void NoGradeValueNoStatsOrNoItemGiveNothing()
    {
        EquipGradeVariation v = Flat(10, 50);
        EquipStats stats = Stats(EquipStat.HpMax, 1000);
        Assert.True(v.DeltaOf(stats, EquipmentParts.Coat, 0).IsEmpty);
        Assert.True(v.DeltaOf(EquipStats.Empty, EquipmentParts.Coat, 7).IsEmpty);
        Assert.True(v.DeltaOf((EquipStats)null, EquipmentParts.Coat, 7).IsEmpty);
        Assert.True(v.DeltaOf((ItemInfo)null).IsEmpty);
        ItemInfo definition = Equip(EquipmentParts.Coat, (EquipStat.HpMax, 1000));
        Assert.True(v.DeltaOf(definition).IsEmpty);                                        // 没有个体状态
        Assert.True(v.DeltaOf(With(definition, 5)).IsEmpty);                               // 有强化没有品级值
        Assert.Equal(-30.0, v.DeltaOf(With(definition, 0, grade: 7))[EquipStat.HpMax]);
        Assert.True(v.DeltaOf(Stack(910)).IsEmpty);
    }

    [Fact]
    public void ADeltaOfZeroEverywhereIsEmpty()
    {
        ItemInfo item = Equip(EquipmentParts.Coat, (EquipStat.HpMax, 1000));
        Assert.True(Flat(10, 0).DeltaOf(item.Stats, item.Part, 7).IsEmpty);
    }

    [Fact]
    public void TheSameGradeValueAlwaysGivesTheSameDelta()
    {
        ItemInfo item = Equip(EquipmentParts.Weapon, (EquipStat.EquipmentPhysicalAttack, 800), (EquipStat.HpMax, 90));
        EquipGradeVariation v = Flat(8, 60);
        Assert.Equal(v.DeltaOf(item.Stats, item.Part, 4242), v.DeltaOf(item.Stats, item.Part, 4242));
        Assert.NotEqual(v.DeltaOf(item.Stats, item.Part, 4242), v.DeltaOf(item.Stats, item.Part, 4243));
    }

    [Fact]
    public void ABaseStatIsTruncatedToAnIntegerBeforeVarying()
    {
        var floaty = new EquipStats.Builder().Set(EquipStat.HpMax, 1000.9).Build();
        var whole = new EquipStats.Builder().Set(EquipStat.HpMax, 1000).Build();
        Assert.Equal(Flat(10, 50).DeltaOf(whole, EquipmentParts.Coat, 7), Flat(10, 50).DeltaOf(floaty, EquipmentParts.Coat, 7));
    }

    [Fact]
    public void ConstructionNeedsTenPairsOfEach()
    {
        Assert.Throws<ArgumentNullException>(() => new EquipGradeVariation(null, new int[17]));
        Assert.Throws<ArgumentNullException>(() => new EquipGradeVariation(new int[17], null));
        Assert.Throws<ArgumentException>(() => new EquipGradeVariation(new int[9], new int[17]));
        Assert.Throws<ArgumentException>(() => new EquipGradeVariation(new int[17], new int[9]));
        var ten = new EquipGradeVariation(new int[10], new int[10]);
        Assert.Equal((0, 0), (ten.PercentOf(9), ten.CapOf(9)));
    }

    // ------------------------------------------------------------------ 读 [stat random variation]

    private static ScriptLine Variation(params ScriptValue[] values) => new ScriptLine("[stat random variation]", values);

    private static ScriptValue[] Counting(int pairs) =>
        Enumerable.Range(0, pairs).SelectMany(i => new[] { I(i + 1), I(100 + i) }).ToArray();

    [Fact]
    public void TheVariationTagIsReadAsPercentCapPairs()
    {
        Assert.True(EquipGradeVariation.TryParse(new[] { Variation(Counting(17)) }, out EquipGradeVariation v));
        Assert.Equal((1, 100), (v.PercentOf(0), v.CapOf(0)));
        Assert.Equal((10, 109), (v.PercentOf(9), v.CapOf(9)));
    }

    [Fact]
    public void TenPairsAreEnoughAndAnOddTrailingNumberIsIgnored()
    {
        Assert.True(EquipGradeVariation.TryParse(new[] { Variation(Counting(10)) }, out EquipGradeVariation v));
        Assert.Equal((10, 109), (v.PercentOf(9), v.CapOf(9)));
        ScriptValue[] odd = Counting(10).Concat(new[] { I(99) }).ToArray();
        Assert.True(EquipGradeVariation.TryParse(new[] { Variation(odd) }, out v));
        Assert.Equal((10, 109), (v.PercentOf(9), v.CapOf(9)));
    }

    [Fact]
    public void FewerThanTenPairsMissingTagOrNoNumbersFail()
    {
        Assert.False(EquipGradeVariation.TryParse(new[] { Variation(Counting(9)) }, out EquipGradeVariation v));
        Assert.Null(v);
        Assert.False(EquipGradeVariation.TryParse(new[] { Variation(Counting(9).Concat(new[] { I(5) }).ToArray()) }, out v));
        Assert.False(EquipGradeVariation.TryParse(new[] { new ScriptLine("[other]", Counting(17)) }, out v));
        Assert.False(EquipGradeVariation.TryParse(new[] { Variation(TableBuilder.S("x")) }, out v));
        Assert.False(EquipGradeVariation.TryParse(Array.Empty<ScriptLine>(), out v));
        Assert.Throws<ArgumentNullException>(() => EquipGradeVariation.TryParse(null, out v));
    }

    [Fact]
    public void ALaterTagWinsFloatsTruncateAndTheTagIgnoresCase()
    {
        ScriptValue[] first = Counting(10);
        ScriptValue[] second = Enumerable.Range(0, 10).SelectMany(i => new[] { F(i + 7.9f), I(500 + i) }).ToArray();
        Assert.True(EquipGradeVariation.TryParse(new[] { Variation(first), new ScriptLine("[STAT RANDOM VARIATION]", second) }, out EquipGradeVariation v));
        Assert.Equal((7, 500), (v.PercentOf(0), v.CapOf(0)));
        Assert.Equal((16, 509), (v.PercentOf(9), v.CapOf(9)));
    }

    // ------------------------------------------------------------------ 武器倍率表

    private static float[] Counted(int cells) => Enumerable.Range(0, cells).Select(i => (float)i).ToArray();

    private static ItemInfo Weapon(int subType, params string[] jobs)
    {
        ItemInfo w = EnhanceFixtures.Gear(900, EquipmentParts.Weapon, 0, 0);
        w.UsableJobs.Clear();
        w.UsableJobs.AddRange(jobs);
        w.SubType = subType;
        return w;
    }

    [Fact]
    public void TheWeaponRateTableIsIndexedByJobKindAndPhysicalOrMagical()
    {
        var table = new WeaponUpgradeRateTable(Counted(132));
        // 下标 = ((职业 × 6) + 种类) × 2 + (0 物理 | 1 魔法);这里每格的值就是它的下标
        Assert.Equal(4f, table.RateOf(Weapon(2, "[swordman]"), true));
        Assert.Equal(5f, table.RateOf(Weapon(2, "[swordman]"), false));
        Assert.Equal(35f, table.RateOf(Weapon(5, "[gunner]"), false));
        Assert.Equal(60f, table.RateOf(Weapon(0, "[at gunner]"), true));
        Assert.Equal(115f, table.RateOf(Weapon(3, "[creator mage]"), false));
        Assert.Equal(122f, table.RateOf(Weapon(1, "[thief]"), true));
        Assert.Equal(131f, table.RateOf(Weapon(5, "[thief]"), false));
    }

    [Fact]
    public void WithoutALastJobRowTheLastJobGetsRateOne()
    {
        var table = new WeaponUpgradeRateTable(Counted(120));
        Assert.False(table.HasLastJob);
        Assert.Equal(1f, table.RateOf(Weapon(1, "[thief]"), true));
        Assert.Equal(1f, table.RateOf(Weapon(5, "[thief]"), false));
        Assert.Equal(119f, table.RateOf(Weapon(5, "[creator mage]"), false));
        Assert.True(new WeaponUpgradeRateTable(Counted(132)).HasLastJob);
    }

    [Fact]
    public void NoUsableJobOrAnOutOfRangeKindMeansNoWeaponUpgradeAttack()
    {
        var table = new WeaponUpgradeRateTable(Counted(132));
        Assert.Equal(0f, table.RateOf(Weapon(0), true));                         // 没写可用职业
        Assert.Equal(0f, table.RateOf(Weapon(6, "[swordman]"), true));          // 种类不小于 6
        Assert.Equal(0f, table.RateOf(Weapon(-1, "[swordman]"), false));
        Assert.Equal(0f, table.RateOf(Weapon(6, "[thief]"), true));
        Assert.Equal(0f, table.RateOf(null, true));
        Assert.Equal(0f, table.Cell(11, 0, true));
        Assert.Equal(0f, table.Cell(0, 6, true));
        Assert.Equal(0f, table.Cell(-1, 0, true));
    }

    [Theory]
    [InlineData(new[] { "[swordman]" }, 0)]
    [InlineData(new[] { "[priest]" }, 4)]
    [InlineData(new[] { "[at gunner]" }, 5)]
    [InlineData(new[] { "[at fighter]" }, 6)]
    [InlineData(new[] { "[at mage]" }, 7)]
    [InlineData(new[] { "[demonic swordman]" }, 8)]
    [InlineData(new[] { "[creator mage]" }, 9)]
    [InlineData(new[] { "[thief]" }, 10)]
    [InlineData(new[] { "[THIEF]" }, 10)]
    [InlineData(new[] { "[priest]", "[fighter]", "[thief]" }, 1)]
    [InlineData(new[] { "[thief]", "[creator mage]" }, 9)]
    [InlineData(new[] { "[all]" }, 0)]
    [InlineData(new[] { "[thief]", "[all]" }, 0)]
    [InlineData(new string[0], -1)]
    public void TheJobIsTheFirstOneInTheClientsOrder(string[] jobs, int expected)
    {
        Assert.Equal(expected, WeaponUpgradeRateTable.FirstJob(Weapon(0, jobs)));
    }

    [Fact]
    public void ANullItemHasNoJob()
    {
        Assert.Equal(-1, WeaponUpgradeRateTable.FirstJob(null));
    }

    [Fact]
    public void TheClientOrderIsNotTheServerOrder()
    {
        // 服务端 EquipRules 的次序里暗夜使者是 6、男格斗是 7;客户端的倍率表按觉醒职业共用本职业的行排:男格斗在 6,暗夜使者排最后
        Assert.Equal(6, WeaponUpgradeRateTable.FirstJob(Weapon(0, "[at fighter]")));
        Assert.Equal(7, EquipRules.JobOf("[at fighter]"));
        Assert.Equal(10, WeaponUpgradeRateTable.FirstJob(Weapon(0, "[thief]")));
        Assert.Equal(6, EquipRules.JobOf("[thief]"));
    }

    [Fact]
    public void ARateTableTextIsReadWithCommentsAndAnyWhitespaceOrCommas()
    {
        var lines = new List<string> { "# 合成的倍率表", string.Empty, "   ", null };
        for (int row = 0; row < WeaponUpgradeRateTable.Jobs; row++)
        {
            lines.Add(string.Join(row % 2 == 0 ? " " : ",", Enumerable.Range(0, 12).Select(c => (row * 12) + c)));
        }
        Assert.True(WeaponUpgradeRateTable.TryParse(lines, out WeaponUpgradeRateTable table));
        Assert.False(table.HasLastJob);
        Assert.Equal(Counted(120).Select((v, i) => table.Cell(i / 12, (i % 12) / 2, i % 2 == 0)).ToArray(), Counted(120));
    }

    [Fact]
    public void ARateTableTextWithAnElevenRowBodyKeepsTheLastRow()
    {
        string[] lines = Enumerable.Range(0, 11).Select(row => string.Join(' ', Enumerable.Range(0, 12).Select(c => ((row * 12) + c).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        Assert.True(WeaponUpgradeRateTable.TryParse(lines, out WeaponUpgradeRateTable table));
        Assert.True(table.HasLastJob);
        Assert.Equal(131f, table.Cell(10, 5, false));
    }

    [Theory]
    [InlineData(119)]
    [InlineData(121)]
    [InlineData(131)]
    [InlineData(133)]
    [InlineData(0)]
    public void ARateTableTextWithTheWrongCountFails(int count)
    {
        string[] lines = { string.Join(' ', Enumerable.Repeat("1.0", count)) };
        Assert.False(WeaponUpgradeRateTable.TryParse(lines, out WeaponUpgradeRateTable table));
        Assert.Null(table);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1.0.0")]
    public void ARateTableTextWithABadTokenFails(string token)
    {
        string[] lines = { string.Join(' ', Enumerable.Repeat("1.0", 119)) + " " + token };
        Assert.False(WeaponUpgradeRateTable.TryParse(lines, out _));
    }

    [Fact]
    public void TheRateTableChecksItsInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new WeaponUpgradeRateTable(null));
        Assert.Throws<ArgumentException>(() => new WeaponUpgradeRateTable(Counted(12)));
        Assert.Throws<ArgumentNullException>(() => WeaponUpgradeRateTable.TryParse(null, out _));
    }

    [Fact]
    public void TheFlatRateIsTheSameForEveryWeapon()
    {
        Assert.Equal(1f, FlatWeaponUpgradeRate.One.RateOf(Weapon(3, "[thief]"), true));
        Assert.Equal(1f, FlatWeaponUpgradeRate.One.RateOf(null, false));
        Assert.Equal(2.5f, new FlatWeaponUpgradeRate(2.5f).RateOf(Weapon(0), false));
    }

    // ------------------------------------------------------------------ [sub type]

    [Fact]
    public void TheSubTypeTagIsReadAsTheFirstIntegerAndTheLastTagWins()
    {
        Assert.Equal(0, Parse(new TokenStreamBuilder().Tag("[equipment type]").Str("[weapon]")).SubType);
        Assert.Equal(3, Parse(new TokenStreamBuilder().Tag("[equipment type]").Str("[weapon]").Tag("[sub type]").Int(3)).SubType);
        Assert.Equal(4, Parse(new TokenStreamBuilder().Tag("[sub type]").Int(2).Tag("[sub type]").Int(4).Int(9)).SubType);
        Assert.Equal(2, Parse(new TokenStreamBuilder().Tag("[sub type]").Int(2).Tag("[sub type]").Str("x")).SubType);
    }

    // ------------------------------------------------------------------ 强化表按部位分路

    // 一行:A 组 a=100 f1=11 f2=0.5 f3=22 f4=0.25 f5=33;B 组 b=200 g1=44 g2=1.5 g3=55 g4=2.5 g5=66(装备等级 0,权重与档位默认 1,取行结果原样)
    private static UpgradeTable ChannelTable()
    {
        return new TableBuilder().StatRow(100, 11f, 0.5f, 22f, 0.25f, 33f, 200, 44f, 1.5f, 55f, 2.5f, 66f).Build();
    }

    private sealed class Rates : IWeaponUpgradeRate
    {
        public float Physical { get; set; } = 1.5f;

        public float Magical { get; set; } = 0.25f;

        public float RateOf(ItemInfo weapon, bool physical) => physical ? Physical : Magical;
    }

    private static ItemInfo Worn(int part, int upgrade, int gradeValue = 0, params (EquipStat Stat, double Value)[] stats)
    {
        ItemInfo def = Equip(part, stats);
        return def.WithInstance(new EquipInstance(upgrade, gradeValue: gradeValue));
    }

    [Fact]
    public void AWeaponTurnsTheFirstCellTimesTheRateIntoPhysicalAndMagicalAttack()
    {
        ItemInfo both = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, 50), (EquipStat.EquipmentMagicalAttack, 40));
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(both, ChannelTable(), new Rates());
        Assert.Equal(new UpgradeChannels(150, 25, 0, 0f, 0, 0f, 0), e.Normal);
        Assert.Equal(new UpgradeChannels(300, 50, 0, 0f, 0, 0f, 0), e.Alternate);
        Assert.Equal(1, e.Level);
        EquipStats s = e.ToStats();
        Assert.Equal((150.0, 25.0, 300.0, 50.0), (s[EquipStat.EquipmentPhysicalAttack], s[EquipStat.EquipmentMagicalAttack], s.SecondOf(EquipStat.EquipmentPhysicalAttack), s.SecondOf(EquipStat.EquipmentMagicalAttack)));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void AWeaponNeedsItsOwnBaseAttackOfThatKindForThatChannel()
    {
        ItemInfo physicalOnly = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, 50));
        EquipUpgradeEffect p = EquipUpgradeEffect.Of(physicalOnly, ChannelTable(), new Rates());
        Assert.Equal((150, 0), (p.Normal.PhysicalAttack, p.Normal.MagicalAttack));
        ItemInfo magicalOnly = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentMagicalAttack, 40));
        EquipUpgradeEffect m = EquipUpgradeEffect.Of(magicalOnly, ChannelTable(), new Rates());
        Assert.Equal((0, 25), (m.Normal.PhysicalAttack, m.Normal.MagicalAttack));
        ItemInfo neither = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.HpMax, 5));
        Assert.True(EquipUpgradeEffect.Of(neither, ChannelTable(), new Rates()).IsEmpty);
    }

    [Fact]
    public void AFractionalBaseAttackIsCutToAnIntegerBeforeTheWeaponGateIsTested()
    {
        // 装备攻击在客户端的属性集里是整数:0.5 取整成 0 没有物理一路,1.9 取整成 1 有魔法一路
        ItemInfo w = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, 0.5), (EquipStat.EquipmentMagicalAttack, 1.9));
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(w, ChannelTable(), new Rates());
        Assert.Equal((0, 25), (e.Normal.PhysicalAttack, e.Normal.MagicalAttack));
        Assert.Equal((0, 50), (e.Alternate.PhysicalAttack, e.Alternate.MagicalAttack));
        ItemInfo negative = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, -3));
        Assert.Equal(150, EquipUpgradeEffect.Of(negative, ChannelTable(), new Rates()).Normal.PhysicalAttack);
    }

    [Fact]
    public void TheIntegerChannelsOfArmorAccessoriesAndSupportGearCutTowardZero()
    {
        // 第一格以外的整数项也是向零截断,不是四舍五入:防具 f1、首饰 f3、辅助 f5(A 组为正、B 组为负)
        UpgradeTable table = new TableBuilder().StatRow(0, 5.9f, 0.5f, 7.9f, 0.25f, 9.9f, 0, -5.9f, 0f, -7.9f, 0f, -9.9f).Build();
        EquipUpgradeEffect armor = EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 1), table);
        Assert.Equal((5, -5), (armor.Normal.PhysicalDefense, armor.Alternate.PhysicalDefense));
        EquipUpgradeEffect accessory = EquipUpgradeEffect.Of(Worn(EquipmentParts.Amulet, 1), table);
        Assert.Equal((7, -7), (accessory.Normal.MagicalDefense, accessory.Alternate.MagicalDefense));
        EquipUpgradeEffect support = EquipUpgradeEffect.Of(Worn(EquipmentParts.Support, 1), table);
        Assert.Equal((9, -9), (support.Normal.AllStats, support.Alternate.AllStats));
    }

    [Fact]
    public void ASecondNumberIsOnlyWrittenWhenTheAlternateGroupHasOne()
    {
        UpgradeTable onlyNormal = new TableBuilder().StatRow(100, 11f, 0.5f, 22f, 0.25f, 33f).Build();
        EquipStats a = EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 1), onlyNormal).ToStats();
        Assert.Equal(11.0, a[EquipStat.EquipmentPhysicalDefense]);
        Assert.False(a.HasSecond(EquipStat.EquipmentPhysicalDefense));
        Assert.Equal(1, a.Count);

        // 只有 B 组有数:第一个数是 0、第二个数是 B 组的数,仍然记下来
        UpgradeTable onlyAlternate = new TableBuilder().StatRow(0, 0f, 0f, 0f, 0f, 0f, 200, 44f, 1.5f, 55f, 2.5f, 66f).Build();
        EquipStats b = EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 1), onlyAlternate).ToStats();
        Assert.Equal(1, b.Count);
        Assert.Equal(0.0, b[EquipStat.EquipmentPhysicalDefense]);
        Assert.True(b.HasSecond(EquipStat.EquipmentPhysicalDefense));
        Assert.Equal(44.0, b.SecondOf(EquipStat.EquipmentPhysicalDefense));

        // 辅助装备:四项基础属性都只有第一个数
        EquipStats c = EquipUpgradeEffect.Of(Worn(EquipmentParts.Support, 1), onlyNormal).ToStats();
        Assert.Equal(4, c.Count);
        Assert.False(c.HasSecond(EquipStat.PhysicalAttack));
        Assert.Equal(33.0, c[EquipStat.MagicalDefense]);
    }

    [Fact]
    public void TheWeaponProductIsSinglePrecisionAndTruncatesTowardZero()
    {
        ItemInfo w = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, 50), (EquipStat.EquipmentMagicalAttack, 50));
        UpgradeTable table = new TableBuilder().StatRow(7, 0f, 0f, 0f, 0f, 0f, -7).Build();
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(w, table, new Rates { Physical = 0.5f, Magical = 0.99f });
        Assert.Equal((3, 6), (e.Normal.PhysicalAttack, e.Normal.MagicalAttack));            // 3.5 → 3,6.93 → 6
        Assert.Equal((-3, -6), (e.Alternate.PhysicalAttack, e.Alternate.MagicalAttack));    // 向零截断,不是向下
    }

    [Fact]
    public void WithoutAWeaponRateTheCellIsUsedAsIs()
    {
        ItemInfo w = Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, 50));
        Assert.Equal(100, EquipUpgradeEffect.Of(w, ChannelTable()).Normal.PhysicalAttack);
    }

    [Theory]
    [InlineData(EquipmentParts.Coat)]
    [InlineData(EquipmentParts.Shoulder)]
    [InlineData(EquipmentParts.Pants)]
    [InlineData(EquipmentParts.Shoes)]
    [InlineData(EquipmentParts.Waist)]
    public void ArmorTakesTheSecondCellAsPhysicalDefenseAndTheSixthAsAFloat(int part)
    {
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(Worn(part, 1), ChannelTable(), new Rates());
        Assert.Equal(new UpgradeChannels(0, 0, 11, 0.5f, 0, 0f, 0), e.Normal);
        Assert.Equal(new UpgradeChannels(0, 0, 44, 1.5f, 0, 0f, 0), e.Alternate);
        EquipStats s = e.ToStats();
        Assert.Equal((11.0, 44.0), (s[EquipStat.EquipmentPhysicalDefense], s.SecondOf(EquipStat.EquipmentPhysicalDefense)));
        Assert.Equal(1, s.Count);                                                                // 浮点项不投影
    }

    [Theory]
    [InlineData(EquipmentParts.Amulet)]
    [InlineData(EquipmentParts.Wrist)]
    [InlineData(EquipmentParts.Ring)]
    public void AccessoriesTakeTheThirdCellAsMagicalDefenseAndTheSeventhAsAFloat(int part)
    {
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(Worn(part, 1), ChannelTable(), new Rates());
        Assert.Equal(new UpgradeChannels(0, 0, 0, 0f, 22, 0.25f, 0), e.Normal);
        Assert.Equal(new UpgradeChannels(0, 0, 0, 0f, 55, 2.5f, 0), e.Alternate);
        EquipStats s = e.ToStats();
        Assert.Equal((22.0, 55.0), (s[EquipStat.EquipmentMagicalDefense], s.SecondOf(EquipStat.EquipmentMagicalDefense)));
        Assert.Equal(1, s.Count);
    }

    [Theory]
    [InlineData(EquipmentParts.Support)]
    [InlineData(EquipmentParts.MagicStone)]
    public void SupportGearAndStonesAddTheFourthCellToAllFourBaseStats(int part)
    {
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(Worn(part, 1), ChannelTable(), new Rates());
        Assert.Equal(new UpgradeChannels(0, 0, 0, 0f, 0, 0f, 33), e.Normal);
        Assert.Equal(new UpgradeChannels(0, 0, 0, 0f, 0, 0f, 66), e.Alternate);
        EquipStats s = e.ToStats();
        foreach (EquipStat stat in new[] { EquipStat.PhysicalAttack, EquipStat.PhysicalDefense, EquipStat.MagicalAttack, EquipStat.MagicalDefense })
        {
            Assert.Equal((33.0, 66.0), (s[stat], s.SecondOf(stat)));
        }
        Assert.Equal(4, s.Count);
    }

    [Fact]
    public void TitlesAndCreaturesGetNothing()
    {
        Assert.True(EquipUpgradeEffect.Of(Worn(EquipmentParts.TitleName, 1), ChannelTable(), new Rates()).IsEmpty);
        Assert.True(EquipUpgradeEffect.Of(Worn(EquipmentParts.Creature, 1), ChannelTable(), new Rates()).IsEmpty);
        Assert.Same(EquipUpgradeEffect.None, EquipUpgradeEffect.Of(Worn(EquipmentParts.TitleName, 1), ChannelTable(), new Rates()));
    }

    [Fact]
    public void TheRowIsTheUpgradeLevelMinusOneAndLevelZeroOrBeyondTheTableGivesNothing()
    {
        UpgradeTable table = new TableBuilder()
            .StatRow(10, 1f, 0f, 0f, 0f, 0f)
            .StatRow(20, 2f, 0f, 0f, 0f, 0f)
            .StatRow(30, 3f, 0f, 0f, 0f, 0f)
            .Build();
        Assert.True(EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 0, 5), table).IsEmpty);
        Assert.Equal(1, EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 1), table).Normal.PhysicalDefense);
        Assert.Equal(2, EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 2), table).Normal.PhysicalDefense);
        Assert.Equal(3, EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 3), table).Normal.PhysicalDefense);
        Assert.Equal(10, EquipUpgradeEffect.Of(Worn(EquipmentParts.Weapon, 1, 0, (EquipStat.EquipmentPhysicalAttack, 1)), table).Normal.PhysicalAttack);
        Assert.Equal(30, EquipUpgradeEffect.Of(Worn(EquipmentParts.Weapon, 3, 0, (EquipStat.EquipmentPhysicalAttack, 1)), table).Normal.PhysicalAttack);
        Assert.True(EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 4), table).IsEmpty);
        Assert.True(EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 31), table).IsEmpty);
    }

    [Fact]
    public void RarityWeightAndGradeScaleTheCellsBeforeTheyAreCut()
    {
        // 稀有度 2 权重 2;[level] 第二项(f1 那一格)10:装备等级 20 → ×(20÷10+1) = 3;f1 = 5 → 5 × 2 × 3 = 30;f2 = 3 只乘权重 → 6.0(浮点)
        UpgradeTable table = new TableBuilder().StatRow(0, 5f, 3f, 0f, 0f, 0f)
            .Line("[stat weights by rarity]", Enumerable.Repeat(1f, 2).Concat(new[] { 2f }).Concat(Enumerable.Repeat(1f, 3)).Select(F).ToArray())
            .Line("[level]", F(1f), F(10f), F(1f), F(1f))
            .Build();
        ItemInfo coat = EnhanceFixtures.Gear(900, EquipmentParts.Coat, 2, 20);
        EquipUpgradeEffect e = EquipUpgradeEffect.Of(With(coat, 1), table);
        Assert.Equal(new UpgradeChannels(0, 0, 30, 6f, 0, 0f, 0), e.Normal);
    }

    [Fact]
    public void NoInstanceNoTableOrNotGearGivesNone()
    {
        UpgradeTable table = ChannelTable();
        ItemInfo definition = Equip(EquipmentParts.Coat);
        Assert.Same(EquipUpgradeEffect.None, EquipUpgradeEffect.Of(definition, table));
        Assert.Same(EquipUpgradeEffect.None, EquipUpgradeEffect.Of(null, table));
        Assert.Same(EquipUpgradeEffect.None, EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 1), null));
        Assert.Same(EquipUpgradeEffect.None, EquipUpgradeEffect.Of(Stack(910), table));
        Assert.True(EquipUpgradeEffect.None.IsEmpty);
        Assert.True(EquipUpgradeEffect.None.ToStats().IsEmpty);
    }

    [Fact]
    public void AnAllZeroRowGivesNone()
    {
        UpgradeTable zero = new TableBuilder().StatRow(0, 0f, 0f, 0f, 0f, 0f).Build();
        Assert.Same(EquipUpgradeEffect.None, EquipUpgradeEffect.Of(Worn(EquipmentParts.Coat, 1), zero));
    }

    [Fact]
    public void ChannelsOfChecksItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => EquipUpgradeEffect.ChannelsOf(null, Equip(EquipmentParts.Coat), null));
        Assert.Throws<ArgumentException>(() => EquipUpgradeEffect.ChannelsOf(new float[7], Equip(EquipmentParts.Coat), null));
        Assert.True(EquipUpgradeEffect.ChannelsOf(new float[8], null, null).IsEmpty);
    }

    [Fact]
    public void ChannelsCompareAndHashByValue()
    {
        var a = new UpgradeChannels(1, 2, 3, 4f, 5, 6f, 7);
        var b = new UpgradeChannels(1, 2, 3, 4f, 5, 6f, 7);
        Assert.Equal(a, b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new UpgradeChannels(1, 2, 3, 4f, 5, 6f, 8));
        Assert.NotEqual(a, new UpgradeChannels(1, 2, 3, 4.5f, 5, 6f, 7));
        Assert.False(a.Equals("x"));
        Assert.Contains("物攻1", a.ToString());
        Assert.False(a.IsEmpty);
        Assert.True(default(UpgradeChannels).IsEmpty);
        Assert.False(new UpgradeChannels(0, 0, 0, 0.1f, 0, 0f, 0).IsEmpty);
    }

    // ------------------------------------------------------------------ 默认的个体状态加成

    private static EquipInstanceBonus Bonus(UpgradeTable table = null, EquipGradeVariation variation = null, IWeaponUpgradeRate rate = null)
    {
        return EquipInstanceBonus.For(table ?? ChannelTable(), variation, rate ?? new Rates());
    }

    [Fact]
    public void NothingWornOrNoStateGivesNoBonus()
    {
        EquipInstanceBonus bonus = Bonus(variation: Flat(10, 50));
        Assert.Null(bonus.BonusOf(null));
        Assert.Null(bonus.BonusOf(Equip(EquipmentParts.Coat, (EquipStat.HpMax, 1000))));          // 定义,没有个体状态
        Assert.Null(bonus.BonusOf(Stack(910)));
        Assert.Null(bonus.BonusOf(Worn(EquipmentParts.Coat, 0, 0, (EquipStat.HpMax, 1000))));     // 回到定义
    }

    [Fact]
    public void TheUpgradeAndTheGradeAreAddedTogether()
    {
        EquipInstanceBonus bonus = Bonus(variation: Flat(10, 50));
        ItemInfo coat = Worn(EquipmentParts.Coat, 1, 7, (EquipStat.HpMax, 1000), (EquipStat.PhysicalAttack, 37));
        EquipStats b = bonus.BonusOf(coat);
        Assert.Equal((11.0, 44.0), (b[EquipStat.EquipmentPhysicalDefense], b.SecondOf(EquipStat.EquipmentPhysicalDefense)));     // 强化:防具格 1
        Assert.Equal((-30.0, -2.0), (b[EquipStat.HpMax], b[EquipStat.PhysicalAttack]));                                          // 品级:种子 7 的差
        Assert.Equal(b, bonus.UpgradeOf(coat).ToStats().Plus(bonus.GradeDeltaOf(coat)));
    }

    [Fact]
    public void OnlyTheUpgradeOrOnlyTheGradeStandsAlone()
    {
        EquipInstanceBonus bonus = Bonus(variation: Flat(10, 50));
        EquipStats up = bonus.BonusOf(Worn(EquipmentParts.Coat, 1, 0, (EquipStat.HpMax, 1000)));
        Assert.Equal(1, up.Count);
        Assert.Equal(11.0, up[EquipStat.EquipmentPhysicalDefense]);
        EquipStats grade = bonus.BonusOf(Worn(EquipmentParts.Coat, 0, 7, (EquipStat.HpMax, 1000)));
        Assert.Equal(1, grade.Count);
        Assert.Equal(-30.0, grade[EquipStat.HpMax]);
    }

    [Fact]
    public void TheTwoSwitchesTurnEachHalfOff()
    {
        EquipInstanceBonus bonus = Bonus(variation: Flat(10, 50));
        ItemInfo coat = Worn(EquipmentParts.Coat, 1, 7, (EquipStat.HpMax, 1000));
        bonus.UpgradeEnabled = false;
        Assert.Equal(new[] { EquipStat.HpMax }, bonus.BonusOf(coat).Entries.Select(e => e.Stat).ToArray());
        bonus.GradeEnabled = false;
        Assert.Null(bonus.BonusOf(coat));
        bonus.UpgradeEnabled = true;
        Assert.Equal(new[] { EquipStat.EquipmentPhysicalDefense }, bonus.BonusOf(coat).Entries.Select(e => e.Stat).ToArray());
    }

    [Fact]
    public void NoVariationDataMeansNoGradeEffect()
    {
        EquipInstanceBonus bonus = Bonus();
        Assert.True(bonus.GradeDeltaOf(Worn(EquipmentParts.Coat, 0, 7, (EquipStat.HpMax, 1000))).IsEmpty);
        Assert.Null(bonus.BonusOf(Worn(EquipmentParts.Coat, 0, 7, (EquipStat.HpMax, 1000))));
    }

    [Fact]
    public void ATableCanBeSwappedWithoutRebuildingTheBonus()
    {
        UpgradeTable current = ChannelTable();
        var bonus = new EquipInstanceBonus(() => current);
        ItemInfo coat = Worn(EquipmentParts.Coat, 1);
        Assert.Equal(11.0, bonus.BonusOf(coat)[EquipStat.EquipmentPhysicalDefense]);
        current = new TableBuilder().StatRow(0, 70f, 0f, 0f, 0f, 0f).Build();
        Assert.Equal(70.0, bonus.BonusOf(coat)[EquipStat.EquipmentPhysicalDefense]);
        current = null;
        Assert.Null(bonus.BonusOf(coat));
        Assert.Null(new EquipInstanceBonus(null).BonusOf(coat));
    }

    [Fact]
    public void TheWeaponRateAndTheVariationCanBeSetLater()
    {
        EquipInstanceBonus bonus = EquipInstanceBonus.For(ChannelTable());
        ItemInfo w = Worn(EquipmentParts.Weapon, 1, 7, (EquipStat.EquipmentPhysicalAttack, 1000));
        Assert.Equal(100.0, bonus.BonusOf(w)[EquipStat.EquipmentPhysicalAttack]);               // 倍率 1
        bonus.WeaponRate = new Rates { Physical = 2f };
        Assert.Equal(200.0, bonus.BonusOf(w)[EquipStat.EquipmentPhysicalAttack]);
        bonus.GradeVariation = Flat(10, 50);
        Assert.Equal(200.0 + bonus.GradeDeltaOf(w)[EquipStat.EquipmentPhysicalAttack], bonus.BonusOf(w)[EquipStat.EquipmentPhysicalAttack]);
        Assert.NotEqual(0.0, bonus.GradeDeltaOf(w)[EquipStat.EquipmentPhysicalAttack]);
    }

    // ------------------------------------------------------------------ 接进装备栏汇总

    private static (EquipmentSlots Slots, EquipBonusOverlay Overlay) Wear(EquipInstanceBonus bonus, params (int Slot, ItemInfo Item)[] gear)
    {
        var slots = new EquipmentSlots();
        var overlay = new EquipBonusOverlay(_ => null) { InstanceBonus = bonus };
        overlay.Attach(slots);
        foreach ((int slot, ItemInfo item) in gear)
        {
            slots.Restore(slot, item);
        }
        return (slots, overlay);
    }

    [Fact]
    public void TheBonusFlowsIntoTheWornTotalsAndTheStatBlock()
    {
        EquipInstanceBonus bonus = Bonus(variation: Flat(10, 50));
        ItemInfo coat = Worn(EquipmentParts.Coat, 1, 7, (EquipStat.HpMax, 1000), (EquipStat.PhysicalAttack, 37));
        ItemInfo stone = Worn(EquipmentParts.MagicStone, 1, 0, (EquipStat.PhysicalAttack, 5));
        (EquipmentSlots slots, _) = Wear(bonus, (EquipmentParts.Coat, coat), (EquipmentParts.MagicStone, stone));
        EquipStats total = slots.TotalStats();
        // 基础:生命 1000、物攻 37 + 5;强化:防具物防 +11、魔法石四维各 +33;品级:生命 −30、物攻 −2
        Assert.Equal(970.0, total[EquipStat.HpMax]);
        Assert.Equal(37.0 + 5.0 - 2.0 + 33.0, total[EquipStat.PhysicalAttack]);
        Assert.Equal(33.0, total[EquipStat.MagicalAttack]);
        Assert.Equal(11.0, total[EquipStat.EquipmentPhysicalDefense]);
        StatBlock block = slots.TotalStatBlock();
        Assert.Equal((9700L, 730L), (block.HpMax, block.PhysicalAttack));
        Assert.Equal((330L, 330L), (block.MagicalAttack, block.MagicalDefense));
        Assert.Equal(330L + 110L, block.PhysicalDefense);                 // 装备物防折进物防:魔法石 33 + 防具 11,各 ×10
    }

    [Fact]
    public void ChangingTheWornItemRefreshesTheBonusThroughTheSlotVersion()
    {
        EquipInstanceBonus bonus = Bonus();
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Wear(bonus, (EquipmentParts.Coat, Worn(EquipmentParts.Coat, 1)));
        Assert.Equal(11.0, slots.TotalStats()[EquipStat.EquipmentPhysicalDefense]);
        slots.Restore(EquipmentParts.Coat, Worn(EquipmentParts.Coat, 1).WithInstance(new EquipInstance(0)));
        Assert.True(slots.TotalStats().IsEmpty);
        slots.Restore(EquipmentParts.Coat, Worn(EquipmentParts.Coat, 1));
        Assert.Equal(11.0, slots.TotalStats()[EquipStat.EquipmentPhysicalDefense]);
        // 改了开关要 Invalidate 才重算
        bonus.UpgradeEnabled = false;
        Assert.Equal(11.0, slots.TotalStats()[EquipStat.EquipmentPhysicalDefense]);
        overlay.Invalidate();
        Assert.True(slots.TotalStats().IsEmpty);
    }

    [Fact]
    public void WithoutAnyInstanceStateTheTotalsAreUnchanged()
    {
        ItemInfo plain = Equip(EquipmentParts.Coat, (EquipStat.HpMax, 1000));
        (EquipmentSlots slots, _) = Wear(Bonus(variation: Flat(10, 50)), (EquipmentParts.Coat, plain));
        Assert.Equal(plain.Stats, slots.TotalStats());
    }
}
