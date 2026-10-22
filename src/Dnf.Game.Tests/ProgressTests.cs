using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 经验与升级(证据 E-111):升级经验表、技能点表、升级循环、击杀经验的运算顺序与舍入、等级差倍率、倍率表、基础经验表、角色成长。
/// 数值全是合成的(经验表是 100 × L 的等差区间,与原版数据无关)。
/// </summary>
public class ProgressTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static ScriptLine Line(string tag, params ScriptValue[] values) => new ScriptLine(tag, values);

    /// <summary>合成经验表:升到 L 级的累计经验 = 100 × (L - 1) × L / 2(L = 2 到 91),所以 L 级里要攒 100 × L。</summary>
    private static ExpTable Table()
    {
        var thresholds = new List<int>();
        for (int level = 2; level <= 91; level++)
        {
            thresholds.Add(100 * (level - 1) * level / 2);
        }
        return new ExpTable(thresholds);
    }

    /// <summary>合成技能点表:升到 L 级给 L 点(下标 0 是 0)。</summary>
    private static SpTable Sp()
    {
        var values = new List<int>();
        for (int level = 0; level <= 95; level++)
        {
            values.Add(level);
        }
        return new SpTable(values);
    }

    // ------------------------------------------------------------------ ExpTable

    [Fact]
    public void LevelExpIsTheTableEntryOfTheLevelMinusTwoAndZeroOutsideTwoToTwoHundred()
    {
        var table = new ExpTable(new[] { 100, 300, 600 });
        Assert.Equal(0, table.LevelExp(0));
        Assert.Equal(0, table.LevelExp(1));          // 1 级固定 0
        Assert.Equal(100, table.LevelExp(2));
        Assert.Equal(300, table.LevelExp(3));
        Assert.Equal(600, table.LevelExp(4));
        Assert.Equal(0, table.LevelExp(5));          // 表里没写到的项是 0
        Assert.Equal(0, table.LevelExp(201));        // 范围外
        Assert.Equal(0, table.LevelExp(-3));
        Assert.Equal(3, table.Count);
    }

    [Fact]
    public void SectionIsTheDifferenceToTheNextLevelAndTheFirstEntryForLevelOne()
    {
        var table = new ExpTable(new[] { 100, 300, 600 });
        Assert.Equal(100, table.Section(1));
        Assert.Equal(200, table.Section(2));
        Assert.Equal(300, table.Section(3));
        Assert.Equal(-600, table.Section(4));        // 下一级的项是 0:差是负的(升级循环据此判断走到头)
        Assert.Equal(0, table.Section(10));
    }

    [Fact]
    public void LevelForExpClimbsWhileTheThresholdIsNotAboveTheExp()
    {
        ExpTable table = Table();
        Assert.Equal(1, table.LevelForExp(0));
        Assert.Equal(1, table.LevelForExp(99));
        Assert.Equal(2, table.LevelForExp(100));
        Assert.Equal(2, table.LevelForExp(299));
        Assert.Equal(3, table.LevelForExp(300));
        Assert.Equal(5, table.LevelForExp(1000));
    }

    [Fact]
    public void TableParsesEveryIntegerOfEveryLineInOrder()
    {
        ExpTable table = ExpTable.Parse(new[] { Line("[a]", I(5), I(6)), Line("[b]", I(7)) });
        Assert.Equal(3, table.Count);
        Assert.Equal(5, table.LevelExp(2));
        Assert.Equal(7, table.LevelExp(4));
        Assert.Equal(0, ExpTable.Parse(null).Count);
    }

    // ------------------------------------------------------------------ SpTable

    [Fact]
    public void SpTableTakesTheSecondNumberOfEveryPairAndIgnoresTheLevelColumn()
    {
        // 等级列写成 5、9(不是 1、2):服务端只取第二列,按出现顺序放进下标 1、2
        SpTable sp = SpTable.Parse(new[] { Line("[sp table]", I(5), I(10), I(9), I(20), I(1), I(30)) });
        Assert.Equal(0, sp.AtLevelUp(0));
        Assert.Equal(10, sp.AtLevelUp(1));
        Assert.Equal(20, sp.AtLevelUp(2));
        Assert.Equal(30, sp.AtLevelUp(3));
        Assert.Equal(0, sp.AtLevelUp(4));            // 表里没写到
        Assert.Equal(4, sp.Count);                   // 含下标 0 的那个 0
    }

    [Fact]
    public void SpTableIsZeroOutsideOneToNinetyFiveAndEmptyWithoutTheBlock()
    {
        SpTable sp = Sp();
        Assert.Equal(1, sp.AtLevelUp(1));
        Assert.Equal(95, sp.AtLevelUp(95));
        Assert.Equal(0, sp.AtLevelUp(96));
        Assert.Equal(0, sp.AtLevelUp(-1));
        Assert.Equal(0, SpTable.Parse(new[] { Line("[other]", I(1), I(2)) }).Count);
        Assert.Equal(0, SpTable.Parse(null).AtLevelUp(2));
    }

    // ------------------------------------------------------------------ Leveling

    [Fact]
    public void GainBelowTheNextThresholdOnlyAddsExp()
    {
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 1, 0, 99);
        Assert.Equal(1, r.Level);
        Assert.Equal(99, r.Exp);
        Assert.False(r.LeveledUp);
        Assert.Equal(0, r.SkillPoints);
    }

    [Fact]
    public void GainExactlyToTheThresholdLevelsUpAndSitsOnTheThreshold()
    {
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 1, 0, 100);
        Assert.Equal(2, r.Level);
        Assert.Equal(100, r.Exp);
        Assert.Equal(1, r.LevelsGained);
        Assert.Equal(2, r.SkillPoints);              // 升到 2 级:表项 2
        Assert.Equal(0, r.AwakeningPoints);
    }

    [Fact]
    public void LeftoverExpRollsIntoTheNextLevel()
    {
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 1, 0, 150);
        Assert.Equal(2, r.Level);
        Assert.Equal(150, r.Exp);
        Assert.Equal(1, r.LevelsGained);
    }

    [Fact]
    public void OneBigGainCanLevelSeveralTimesAndSumsTheSkillPoints()
    {
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 1, 0, 1000);   // 正好到 5 级的门槛
        Assert.Equal(5, r.Level);
        Assert.Equal(1000, r.Exp);
        Assert.Equal(4, r.LevelsGained);
        Assert.Equal(2 + 3 + 4 + 5, r.SkillPoints);
    }

    [Fact]
    public void GainFromMidLevelContinuesFromTheCurrentExp()
    {
        // 2 级里已有 250(门槛 100,下一级 300),再加 60:到 3 级,多出 10 滚进 3 级
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 2, 250, 60);
        Assert.Equal(3, r.Level);
        Assert.Equal(310, r.Exp);
        Assert.Equal(1, r.LevelsGained);
    }

    [Fact]
    public void ExpBelowTheLevelFloorIsRepairedToTheFloorBeforeAdding()
    {
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 3, 5, 10);      // 3 级的门槛是 300
        Assert.Equal(3, r.Level);
        Assert.Equal(310, r.Exp);
    }

    [Fact]
    public void ExpAboveTheNextThresholdIsRepairedToTheFloorToo()
    {
        ExpGainResult r = Leveling.Gain(Table(), Sp(), 2, 9999, 5);
        Assert.Equal(2, r.Level);
        Assert.Equal(105, r.Exp);
    }

    [Fact]
    public void ZeroOrNegativeGainChangesNothing()
    {
        ExpGainResult zero = Leveling.Gain(Table(), Sp(), 3, 350, 0);
        Assert.Equal(3, zero.Level);
        Assert.Equal(350, zero.Exp);
        ExpGainResult negative = Leveling.Gain(Table(), Sp(), 3, 350, -40);
        Assert.Equal(3, negative.Level);
        Assert.Equal(350, negative.Exp);
    }

    [Fact]
    public void LevelingStopsAtTheCapAndThrowsTheLeftoverAway()
    {
        ExpTable table = Table();
        ExpGainResult r = Leveling.Gain(table, Sp(), 84, table.LevelExp(84), 1_000_000);
        Assert.Equal(85, r.Level);
        Assert.Equal(table.LevelExp(85), r.Exp);
        Assert.Equal(1, r.LevelsGained);
        Assert.Equal(85, r.SkillPoints);
    }

    [Fact]
    public void AtTheCapNothingChangesNotEvenTheExp()
    {
        ExpTable table = Table();
        ExpGainResult r = Leveling.Gain(table, Sp(), 85, table.LevelExp(85) + 7, 5000);
        Assert.Equal(85, r.Level);
        Assert.Equal(table.LevelExp(85) + 7, r.Exp);
        Assert.False(r.LeveledUp);
    }

    [Fact]
    public void LevelingStopsWhereTheTableRunsOut()
    {
        var table = new ExpTable(new[] { 100, 300 });                   // 只有 2、3 级的门槛
        ExpGainResult r = Leveling.Gain(table, Sp(), 3, 300, 5000);
        Assert.Equal(3, r.Level);
        Assert.Equal(300, r.Exp);
    }

    [Fact]
    public void FromLevelFiftyEveryNewLevelAlsoGivesAnAwakeningPoint()
    {
        ExpTable table = Table();
        // 48 级攒满到 52 级的门槛:新等级 49、50、51、52,其中 50、51、52 各一个觉醒点
        int toLevel52 = table.LevelExp(52) - table.LevelExp(48);
        ExpGainResult r = Leveling.Gain(table, Sp(), 48, table.LevelExp(48), toLevel52);
        Assert.Equal(52, r.Level);
        Assert.Equal(4, r.LevelsGained);
        Assert.Equal(3, r.AwakeningPoints);
        Assert.Equal(49 + 50 + 51 + 52, r.SkillPoints);
    }

    [Fact]
    public void LevelingWithoutAnSpTableGivesNoSkillPoints()
    {
        ExpGainResult r = Leveling.Gain(Table(), null, 1, 0, 1000);
        Assert.Equal(5, r.Level);
        Assert.Equal(0, r.SkillPoints);
        Assert.Throws<ArgumentNullException>(() => Leveling.Gain(null, Sp(), 1, 0, 10));
    }

    // ------------------------------------------------------------------ ExpRates

    private static ExpRates Rates()
    {
        return new ExpRates(new[] { 1f, 1.5f, 2f, 2.5f }, new[] { 1f, 2f, 3f, 4f, 5f }, new[] { 1f, 1.5f, 2f, 3f });
    }

    [Fact]
    public void RatesParseTheThreeTagsAndAcceptIntegersAsFloats()
    {
        ExpRates rates = ExpRates.Parse(new[]
        {
            Line("[party user number exp bonusrate]", F(1f), F(1.25f), I(2), F(3f)),
            Line("[dungeon difficulty exp bonusrate]", F(1f), F(2f), F(3f), F(4f), F(5f)),
            Line("[drop bonusrate of monster kind]", F(1f), F(1.5f), F(2f), F(2.5f)),
        });
        Assert.Equal(1.25f, rates.PartyRate(2));
        Assert.Equal(2f, rates.PartyRate(3));
        Assert.Equal(5f, rates.DifficultyRate(4));
        Assert.Equal(2.5f, rates.KindRate(3));
    }

    [Fact]
    public void RatesOutsideTheTablesAreOne()
    {
        ExpRates rates = Rates();
        Assert.Equal(1f, rates.PartyRate(0));              // 人数小于 1 取 1
        Assert.Equal(1f, rates.PartyRate(-2));
        Assert.Equal(1f, rates.PartyRate(1));              // 表的第一项恰好是 1
        Assert.Equal(2.5f, rates.PartyRate(4));
        Assert.Equal(2.5f, rates.PartyRate(9));            // 超过 4 人按 4 人算
        Assert.Equal(1f, rates.DifficultyRate(-1));
        Assert.Equal(1f, rates.DifficultyRate(5));
        Assert.Equal(1f, rates.KindRate(4));               // 种类大于 3 取 1
        Assert.Equal(1f, rates.KindRate(-1));
        Assert.Equal(3f, rates.KindRate(3));
    }

    [Fact]
    public void MissingTagsOrFileGiveNeutralRates()
    {
        ExpRates none = ExpRates.Parse(null);
        Assert.Equal(1f, none.PartyRate(3));
        Assert.Equal(1f, none.DifficultyRate(2));
        Assert.Equal(1f, none.KindRate(2));
        ExpRates partial = ExpRates.Parse(new[] { Line("[party user number exp bonusrate]", F(1f), F(1.5f)) });
        Assert.Equal(1.5f, partial.PartyRate(2));
        Assert.Equal(1f, partial.PartyRate(3));            // 表里只写了两项,其余补 1
        Assert.Equal(1f, partial.KindRate(0));
    }

    // ------------------------------------------------------------------ MobRewardTable

    [Fact]
    public void CsvParsesLevelAndExpWithSeveralSeparatorsAndSkipsNoise()
    {
        MobRewardTable table = MobRewardTable.ParseCsv("level,exp\r\n# 注释\r\n1,100\r\n2;200\r\n3\t300\r\n 4 400 \r\n0,5\r\n200,7\r\n-1,9\r\nbad,line\r\n\r\n");
        Assert.Equal(100u, table.BaseExp(1));
        Assert.Equal(200u, table.BaseExp(2));
        Assert.Equal(300u, table.BaseExp(3));
        Assert.Equal(400u, table.BaseExp(4));
        Assert.Equal(0u, table.BaseExp(0));                // 越界的等级被忽略
        Assert.Equal(0u, table.BaseExp(199));
        Assert.Equal(0u, table.BaseExp(200));
        Assert.Equal(4, table.Count);
        Assert.False(table.IsPlaceholder);
        Assert.Equal("csv", table.Source);
    }

    [Fact]
    public void LaterRowsOverrideEarlierOnesAndNegativeExpIsIgnored()
    {
        MobRewardTable table = MobRewardTable.FromRows(new[]
        {
            new KeyValuePair<int, long>(5, 10),
            new KeyValuePair<int, long>(5, 20),
            new KeyValuePair<int, long>(6, -4),
            new KeyValuePair<int, long>(199, 77),
            new KeyValuePair<int, long>(7, 5_000_000_000L),
        });
        Assert.Equal(20u, table.BaseExp(5));
        Assert.Equal(0u, table.BaseExp(6));
        Assert.Equal(77u, table.BaseExp(199));
        Assert.Equal(uint.MaxValue, table.BaseExp(7));     // 超过 32 位的截到最大
    }

    [Fact]
    public void EmptyCsvGivesAnEmptyTable()
    {
        Assert.Equal(0, MobRewardTable.ParseCsv("").Count);
        Assert.Equal(0, MobRewardTable.ParseCsv(null).Count);
    }

    [Fact]
    public void PlaceholderIsASectionOver25WithAFloorOfTwoAndIsLabelled()
    {
        var table = new ExpTable(new[] { 100, 130, 1130 });            // 区间:1 级 100,2 级 30,3 级 1000,4 级起 -1130(表尽头)
        MobRewardTable placeholder = MobRewardTable.Placeholder(table);
        Assert.True(placeholder.IsPlaceholder);
        Assert.Equal("placeholder", placeholder.Source);
        Assert.Equal(4u, placeholder.BaseExp(1));                      // 100 / 25
        Assert.Equal(2u, placeholder.BaseExp(2));                      // 30 / 25 = 1,最少 2
        Assert.Equal(40u, placeholder.BaseExp(3));                     // 1000 / 25
        Assert.Equal(0u, placeholder.BaseExp(4));                      // 没有区间
        Assert.Equal(0u, placeholder.BaseExp(150));
        Assert.Equal(0, MobRewardTable.Placeholder(null).Count);
    }

    // ------------------------------------------------------------------ KillExp

    [Fact]
    public void LevelGapRateMatchesTheClientConstantsBitForBit()
    {
        // 立即数:0x3d4ccccd、0x3e4ccccd、0x3f000000、0x3f400000、0x3f800000、0x3f8f5c29、0x3f333333、0x3f19999a
        static int Bits(float v) => BitConverter.SingleToInt32Bits(v);
        int hero = 50;
        Assert.Equal(0x3d4ccccd, Bits(KillExp.LevelGapRate(hero, hero - 20)));
        Assert.Equal(0x3d4ccccd, Bits(KillExp.LevelGapRate(hero, hero - 7)));
        Assert.Equal(0x3e4ccccd, Bits(KillExp.LevelGapRate(hero, hero - 6)));
        Assert.Equal(0x3f000000, Bits(KillExp.LevelGapRate(hero, hero - 5)));
        Assert.Equal(0x3f400000, Bits(KillExp.LevelGapRate(hero, hero - 4)));
        for (int gap = -3; gap <= 0; gap++)
        {
            Assert.Equal(0x3f800000, Bits(KillExp.LevelGapRate(hero, hero + gap)));
        }
        for (int gap = 1; gap <= 3; gap++)
        {
            Assert.Equal(0x3f8f5c29, Bits(KillExp.LevelGapRate(hero, hero + gap)));
        }
        Assert.Equal(0x3f800000, Bits(KillExp.LevelGapRate(hero, hero + 4)));
        Assert.Equal(0x3f800000, Bits(KillExp.LevelGapRate(hero, hero + 5)));
        Assert.Equal(0x3f400000, Bits(KillExp.LevelGapRate(hero, hero + 6)));
        Assert.Equal(0x3f333333, Bits(KillExp.LevelGapRate(hero, hero + 7)));
        Assert.Equal(0x3f19999a, Bits(KillExp.LevelGapRate(hero, hero + 8)));
        Assert.Equal(0x3f000000, Bits(KillExp.LevelGapRate(hero, hero + 9)));
        Assert.Equal(0x3d4ccccd, Bits(KillExp.LevelGapRate(hero, hero + 10)));
        Assert.Equal(0x3d4ccccd, Bits(KillExp.LevelGapRate(hero, hero + 40)));
    }

    [Fact]
    public void TotalExpHalvesTheBaseFirstAndTruncatesAtTheEnd()
    {
        ExpRates neutral = ExpRates.Neutral;
        Assert.Equal(50u, KillExp.MonsterTotalExp(100, 0, 0, 1f, 1f, false, neutral));
        Assert.Equal(0u, KillExp.MonsterTotalExp(1, 0, 0, 1f, 1f, false, neutral));      // 0.5 → 0
        Assert.Equal(1u, KillExp.MonsterTotalExp(3, 0, 0, 1f, 1f, false, neutral));      // 1.5 → 1
        Assert.Equal(0u, KillExp.MonsterTotalExp(0, 0, 0, 1f, 1f, false, neutral));
    }

    [Fact]
    public void TotalExpKeepsExtendedPrecisionBetweenTheMultiplications()
    {
        // 0.7f 比 0.7 略小:10 × 0.7f = 6.99999988…,x87 留在 80 位里,取整得 6;若中间落成 float 会舍入成 7.0 而得 7
        Assert.Equal(6u, KillExp.MonsterTotalExp(20, 0, 0, 1f, 0.7f, false, ExpRates.Neutral));
        Assert.Equal(6u, KillExp.MonsterTotalExp(20, 0, 0, 0.7f, 1f, false, ExpRates.Neutral));
        // 1.2f 比 1.2 略大:50 × 1.2f = 60.0000024 → 60
        Assert.Equal(60u, KillExp.MonsterTotalExp(100, 0, 0, 1.2f, 1f, false, ExpRates.Neutral));
        // 对照:纯单精度的 10 × 0.7f 会舍入到 7.0,取整得 7——这就是要用 x87 模型的原因
        float tenTimes = 10f;
        float seven = tenTimes * 0.7f;
        Assert.Equal(7, (int)seven);
    }

    [Fact]
    public void TotalExpUsesKindDifficultyAndTheNamedMultiplier()
    {
        ExpRates rates = Rates();
        Assert.Equal(50u, KillExp.MonsterTotalExp(100, 0, 0, 1f, 1f, false, rates));      // 种类 0 难度 0
        Assert.Equal(75u, KillExp.MonsterTotalExp(100, 1, 0, 1f, 1f, false, rates));      // 种类 1:×1.5
        Assert.Equal(150u, KillExp.MonsterTotalExp(100, 3, 0, 1f, 1f, false, rates));     // 种类 3:×3
        Assert.Equal(50u, KillExp.MonsterTotalExp(100, 4, 0, 1f, 1f, false, rates));      // 种类大于 3:×1
        Assert.Equal(100u, KillExp.MonsterTotalExp(100, 0, 1, 1f, 1f, false, rates));     // 难度 1:×2
        Assert.Equal(250u, KillExp.MonsterTotalExp(100, 0, 4, 1f, 1f, false, rates));     // 难度 4:×5
        Assert.Equal(50u, KillExp.MonsterTotalExp(100, 0, 9, 1f, 1f, false, rates));      // 难度越界:×1
        Assert.Equal(150u, KillExp.MonsterTotalExp(100, 0, 0, 1f, 1f, true, rates));      // 有名怪物:种类倍率再 ×3
        Assert.Equal(225u, KillExp.MonsterTotalExp(100, 1, 0, 1f, 1f, true, rates));      // 1.5 × 3 = 4.5
        Assert.Equal(150u, KillExp.MonsterTotalExp(100, 4, 0, 1f, 1f, true, rates));      // 种类大于 3 先得 1.0,有名再 ×3
    }

    [Fact]
    public void TotalExpUsesThePartyRateAndTheDungeonWeight()
    {
        ExpRates rates = Rates();
        Assert.Equal(62u, KillExp.MonsterTotalExp(100, 0, 0, 1f, 1.25f, false, rates));   // 62.5 → 62
        Assert.Equal(100u, KillExp.MonsterTotalExp(100, 0, 0, 2f, 1f, false, rates));
        Assert.Equal(3000u, KillExp.MonsterTotalExp(100, 3, 4, 2f, 2f, false, rates));    // 50 × 队伍 2 × 难度 4(×5)× 权重 2 × 种类 3
    }

    [Fact]
    public void MemberExpDividesTheScaledTotalByTheMemberCountAndTruncates()
    {
        Assert.Equal(100u, KillExp.MemberExp(100, 1f, 1));
        Assert.Equal(112u, KillExp.MemberExp(100, 1.12f, 1));        // 1.12f 略大,112.0000005 → 112
        Assert.Equal(5u, KillExp.MemberExp(100, 0.05f, 1));          // 0.05f 略大,5.00000007 → 5
        Assert.Equal(25u, KillExp.MemberExp(101, 0.5f, 2));          // 25.25 → 25
        Assert.Equal(3u, KillExp.MemberExp(7, 1f, 2));
        Assert.Equal(6u, KillExp.MemberExp(10, 0.7f, 1));            // 与总经验同理:6.99999988 → 6
        Assert.Equal(100u, KillExp.MemberExp(100, 1f, 0));           // 队员数小于 1 当 1
        Assert.Equal(0u, KillExp.MemberExp(0, 1.12f, 1));
    }

    [Fact]
    public void ForKillChainsRewardTotalAndGapRate()
    {
        MobRewardTable rewards = MobRewardTable.FromRows(new[]
        {
            new KeyValuePair<int, long>(10, 1000),
            new KeyValuePair<int, long>(20, 40),
        });
        ExpRates rates = Rates();
        // 同级、普通怪、难度 0、权重 1:1000 / 2 = 500,等级差 0 → ×1
        Assert.Equal(500, KillExp.ForKill(rewards, rates, 10, 10, 0, false, 0, 1f));
        // 首领(种类 3,×3)、难度 1(×2)、权重 1.5:500 × 2 × 1.5 × 3 = 4500
        Assert.Equal(4500, KillExp.ForKill(rewards, rates, 10, 10, 3, false, 1, 1.5f));
        // 主角低 2 级(怪物高 2 级):等级差倍率 1.12f:4500 × 1.12f = 5040.0000214 → 5040
        Assert.Equal(5040, KillExp.ForKill(rewards, rates, 8, 10, 3, false, 1, 1.5f));
        // 怪物比主角低 7 级:倍率 0.05f;500 × 0.05f = 25.0000037 → 25
        Assert.Equal(25, KillExp.ForKill(rewards, rates, 17, 10, 0, false, 0, 1f));
        // 没有这个等级的基础经验:0;没有基础经验表:0
        Assert.Equal(0, KillExp.ForKill(rewards, rates, 10, 11, 0, false, 0, 1f));
        Assert.Equal(0, KillExp.ForKill(null, rates, 10, 10, 0, false, 0, 1f));
    }

    [Fact]
    public void ForKillPartyOfTwoSplitsTheTotalAndUsesTheTwoPlayerRate()
    {
        MobRewardTable rewards = MobRewardTable.FromRows(new[] { new KeyValuePair<int, long>(10, 1000) });
        ExpRates rates = Rates();
        // 两人队:总经验 500 × 1.5(两人倍率)= 750,每人 750 / 2 = 375
        Assert.Equal(375, KillExp.ForKill(rewards, rates, 10, 10, 0, false, 0, 1f, members: 2));
    }

    // ------------------------------------------------------------------ CharacterGrowth

    private static ScriptLine[] GrowthLines()
    {
        return new[]
        {
            Line("[HP MAX]", I(77)),                       // 任何块之前:忽略
            Line("[initial value]"),
            Line("[HP MAX]", I(1000)),
            Line("[MP MAX]", I(500)),
            Line("[physical attack]", F(12.5f)),
            Line("[physical defense]", I(50)),
            Line("[inventory limit]", I(30)),
            Line("[move speed]", I(999)),                  // 九项之外的标签:忽略
            Line("[growtype 1]"),
            Line("[HP MAX]", I(20)),
            Line("[MP MAX]", I(10)),
            Line("[physical attack]", F(1.5f)),
            Line("[growtype 2]"),
            Line("[HP MAX]", I(30)),
            Line("[awakening 1]"),
            Line("[HP MAX]", I(40)),
            Line("[awakening 2]"),
            Line("[HP MAX]", I(50)),
            Line("[growtype name]", I(1)),                 // 不是块标签
            Line("[growtype 3]"),
            Line("[HP MAX]", I(60)),
        };
    }

    [Fact]
    public void ScaledRoundIsTenTimesTheValueRoundedHalfAwayFromZero()
    {
        Assert.Equal(10000, CharacterGrowth.ScaledRound(1000f));
        Assert.Equal(125, CharacterGrowth.ScaledRound(12.5f));
        Assert.Equal(3, CharacterGrowth.ScaledRound(0.25f));          // 2.5 → 3
        Assert.Equal(-3, CharacterGrowth.ScaledRound(-0.25f));        // -2.5 → -3
        Assert.Equal(0, CharacterGrowth.ScaledRound(0.04f));          // 0.4 → 0
        Assert.Equal(10, CharacterGrowth.ScaledRound(1.04f));         // 10.4 → 10
        Assert.Equal(11, CharacterGrowth.ScaledRound(1.06f));         // 10.6 → 11
        Assert.Equal(0, CharacterGrowth.ScaledRound(0f));
    }

    [Fact]
    public void GrowthParsesTheInitialBlockAndTheGrowtypeBlocksInTenths()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(GrowthLines());
        Assert.True(growth.HasInitial);
        Assert.Equal(10000, growth.Initial.HpMax);                    // 1000 × 10
        Assert.Equal(5000, growth.Initial.MpMax);
        Assert.Equal(125, growth.Initial.PhysicalAttack);
        Assert.Equal(500, growth.Initial.PhysicalDefense);
        Assert.Equal(300, growth.Initial.InventoryLimit);
        Assert.Equal(0, growth.Initial.MagicalAttack);
        Assert.Equal(3, growth.GrowtypeBlocks);
        Assert.Equal(200, growth.Growtype(0).HpMax);
        Assert.Equal(15, growth.Growtype(0).PhysicalAttack);
        Assert.Equal(300, growth.Growtype(1).HpMax);
        Assert.Equal(600, growth.Growtype(2).HpMax);
        Assert.Equal(0, growth.Growtype(3).HpMax);                    // 没写
        Assert.Equal(0, growth.Growtype(9).HpMax);                    // 越界
        Assert.Equal(1000.0, StatBlock.Units(growth.Initial.HpMax));
    }

    [Fact]
    public void BelowLevelFifteenEveryStepUsesTheFirstGrowtypeWhateverWasChosen()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(GrowthLines());
        Assert.Equal(10000, growth.StatsAt(1, 1).HpMax);              // 1 级:只有初始值
        Assert.Equal(10000 + 200, growth.StatsAt(2, 1).HpMax);
        Assert.Equal(10000 + 14 * 200, growth.StatsAt(15, 1).HpMax);  // L = 1 到 14 都 < 15,用成长类型 0
        Assert.Equal(10000 + 14 * 200, growth.StatsAt(15, 0).HpMax);
    }

    [Fact]
    public void FromLevelFifteenTheChosenGrowtypeAppliesAndFromFiftyTheAwakeningBlock()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(GrowthLines());
        long baseTo15 = 10000 + 14 * 200;
        Assert.Equal(baseTo15 + 300, growth.StatsAt(16, 1).HpMax);                       // L = 15:成长类型 1(30)
        Assert.Equal(baseTo15 + 200, growth.StatsAt(16, 0).HpMax);                       // 成长类型 0(20)
        Assert.Equal(baseTo15 + 600, growth.StatsAt(16, 2).HpMax);
        long to50Type1 = baseTo15 + 35 * 300;                                            // L = 15 到 49
        Assert.Equal(to50Type1, growth.StatsAt(50, 1).HpMax);
        Assert.Equal(to50Type1 + 300, growth.StatsAt(51, 1, 0).HpMax);                   // L = 50,觉醒下标 0:仍是成长类型本身
        Assert.Equal(to50Type1 + 400, growth.StatsAt(51, 1, 1).HpMax);                   // 觉醒 1
        Assert.Equal(to50Type1 + 500, growth.StatsAt(51, 1, 2).HpMax);                   // 觉醒 2
        Assert.Equal(to50Type1 + 3 * 500, growth.StatsAt(53, 1, 2).HpMax);
    }

    [Fact]
    public void MissingAwakeningBlockContributesNothingAndBadIndicesFallBackToTheFirstGrowtype()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(GrowthLines());
        long to50 = 10000 + 14 * 200 + 35 * 600;                                         // 成长类型 2:每级 60
        Assert.Equal(to50, growth.StatsAt(50, 2).HpMax);
        // 成长类型下标 2 那块没有觉醒块:L = 50 的增量是 0,所以 51 级与 50 级一样
        Assert.Equal(to50, growth.StatsAt(51, 2, 1).HpMax);
        // 成长类型下标越界按 0 处理
        Assert.Equal(growth.StatsAt(20, 0).HpMax, growth.StatsAt(20, 99).HpMax);
        Assert.Equal(growth.Initial.HpMax, growth.StatsAt(0).HpMax);
    }

    [Fact]
    public void GrowthWithoutDataIsAllZero()
    {
        CharacterGrowth none = CharacterGrowth.Parse(null);
        Assert.False(none.HasInitial);
        Assert.Equal(0, none.GrowtypeBlocks);
        Assert.Equal(0, none.StatsAt(30, 1).HpMax);
    }

    [Fact]
    public void LaterTagsInTheSameBlockOverrideEarlierOnes()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(new[]
        {
            Line("[initial value]"),
            Line("[HP MAX]", I(10)),
            Line("[HP MAX]", I(20)),
        });
        Assert.Equal(200, growth.Initial.HpMax);
    }

    [Fact]
    public void StatBlocksAddFieldByField()
    {
        var a = new StatBlock(1, 2, 3, 4, 5, 6, 7, 8, 9);
        var b = new StatBlock(10, 20, 30, 40, 50, 60, 70, 80, 90);
        StatBlock sum = a + b;
        Assert.Equal(11, sum.HpMax);
        Assert.Equal(22, sum.MpMax);
        Assert.Equal(33, sum.PhysicalAttack);
        Assert.Equal(44, sum.PhysicalDefense);
        Assert.Equal(55, sum.MagicalAttack);
        Assert.Equal(66, sum.MagicalDefense);
        Assert.Equal(77, sum.InventoryLimit);
        Assert.Equal(88, sum.MpRegenSpeed);
        Assert.Equal(99, sum.HitRecovery);
    }
}
