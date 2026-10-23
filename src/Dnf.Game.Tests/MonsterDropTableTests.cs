using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物金币掉落(证据 E-116,服务端静态读码):掉落表的读取、概率的算法、掷骰的顺序与取值范围。
/// 数值全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class MonsterDropTableTests
{
    /// <summary>按剧本给数的随机数:记录每次调用的参数,方便断言"问了什么、按什么顺序问"。</summary>
    private sealed class ScriptedRandom : IDropRandom
    {
        private readonly Queue<int> _values;

        public List<string> Calls { get; } = new();

        public ScriptedRandom(params int[] values)
        {
            _values = new Queue<int>(values);
        }

        public int NextInclusive(int max)
        {
            Calls.Add("N" + max);
            return _values.Dequeue();
        }

        public int Range(int lo, int hi)
        {
            Calls.Add("R" + lo + ":" + hi);
            return _values.Dequeue();
        }
    }

    private static float[] Ones(int count) => Enumerable.Repeat(1f, count).ToArray();

    /// <summary>
    /// 一份完整的合成脚本:三行概率(等级区间 1..10、11..20、21..30;金币基础概率 2000/5000/10000)、
    /// 金币行的各种加成(队伍 1、2、3、4 人 = 1、1.5、2、2.5;难度 0..4 = 1、1、1、1.5、2;种类 0..3 = 1、2、3、6)、
    /// 首领的分堆数据(不触发/触发权重 90/10,堆数 1..2 与 4..6,金额 100% 与 110%)。
    /// </summary>
    private static TokenStreamBuilder Script(int volume0 = 100, int volume1 = 110)
    {
        var b = new TokenStreamBuilder();
        b.Tag("[drop prob count]").Int(3);
        b.Tag("[drop prob]")
            .Ints(1, 10, 2000, 11, 12, 13, 14)
            .Ints(11, 20, 5000, 21, 22, 23, 24)
            .Ints(21, 30, 10000, 31, 32, 33, 34);
        b.Tag("[basis of rarity dicision]").Ints(Enumerable.Range(1, 24).ToArray());
        b.Tag("[party member drop bonusrate]").Floats(1f, 1.5f, 2f, 2.5f).Floats(Ones(16));
        b.Tag("[dungeon difficulty drop bonusrate]").Floats(1f, 1f, 1f, 1.5f, 2f).Floats(Ones(20));
        b.Tag("[monster type drop bonusrate]").Floats(1f, 2f, 3f, 6f).Floats(Ones(16));
        b.Tag("[item drop ref table]").Ints(Enumerable.Range(1, 600).ToArray());
        b.Tag("[first boss/named mob hunting]").Ints(1, 1);
        b.Tag("[condition rate]").Ints(90, 10, 80, 20, 70, 30, 60, 40);
        b.Tag("[gold quantity]").Ints(1, 2, 4, 6);
        b.Tag("[gold volume]").Ints(volume0, volume1);
        return b;
    }

    private static MonsterDropTable Load(TokenStreamBuilder b)
    {
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        Assert.NotNull(table);
        return table;
    }

    /// <summary>金币基础金额表:200 组(等级 n,基础金额 n × 10,浮动 n % 7)。</summary>
    private static GoldGenTable GoldTable()
    {
        var b = new TokenStreamBuilder().Tag("[gold drop ref table]");
        for (int n = 1; n <= 200; n++)
        {
            b.Ints(n, n * 10, n % 7);
        }
        GoldGenTable table = GoldGenTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        return table;
    }

    // ------------------------------------------------------------------ 读取

    [Fact]
    public void Load_ReadsEveryTag()
    {
        MonsterDropTable t = Load(Script());
        Assert.Equal(3, t.RowCount);
        Assert.Equal(11, t.Row(1).LevelFrom);
        Assert.Equal(20, t.Row(1).LevelTo);
        Assert.Equal(5000, t.Row(1).Gold);
        Assert.Equal(new[] { 5000, 21, 22, 23, 24 }, t.Row(1).Values);
        Assert.Equal(1.5f, t.PartyBonus(0, 2));
        Assert.Equal(2.5f, t.PartyBonus(0, 4));
        Assert.Equal(1.5f, t.DifficultyBonus(0, 3));
        Assert.Equal(6f, t.RoleBonus(0, 3));
        Assert.Equal(1f, t.RoleBonus(1, 3));
    }

    [Fact]
    public void Load_OutOfRangeBonusLookupsAreNeutral()
    {
        MonsterDropTable t = Load(Script());
        Assert.Equal(1f, t.PartyBonus(0, 0));
        Assert.Equal(1f, t.PartyBonus(0, 5));
        Assert.Equal(1f, t.DifficultyBonus(0, 5));
        Assert.Equal(1f, t.RoleBonus(0, 4));
        Assert.Equal(1f, t.RoleBonus(9, 0));
    }

    [Fact]
    public void Load_UnknownTagFailsTheWholeTable()
    {
        TokenStreamBuilder b = new TokenStreamBuilder().Tag("[drop prob count]").Int(1).Tag("[no such tag]").Int(1);
        MonsterDropTable t = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(t);
        Assert.Contains("[no such tag]", error);
    }

    [Fact]
    public void Load_RunningOutOfNumbersFailsAndNamesTheTag()
    {
        TokenStreamBuilder b = new TokenStreamBuilder().Tag("[drop prob count]").Int(2).Tag("[drop prob]").Ints(1, 10, 100, 1, 2, 3, 4, 11, 20);
        MonsterDropTable t = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(t);
        Assert.Contains("[drop prob]", error);
    }

    [Fact]
    public void Load_StrayValueStopsLoadingButKeepsWhatWasRead()
    {
        // 原版的取标签原语:标签之间夹着游离的值时丢到下一个标签为止,顶层循环就此结束,前面读到的有效
        TokenStreamBuilder b = new TokenStreamBuilder()
            .Tag("[drop prob count]").Int(1)
            .Tag("[drop prob]").Ints(1, 10, 300, 1, 2, 3, 4)
            .Int(99)
            .Tag("[gold quantity]").Ints(7, 8, 9, 10);
        MonsterDropTable t = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        Assert.Equal(1, t.RowCount);
        Assert.Equal(300, t.Row(0).Gold);
        // [gold quantity] 没读到:首领分堆数据还是 0,堆数 0
        Assert.True(t.TryBossGold(1, new ScriptedRandom(0, 0), out int count, out _));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Load_EmptyScriptGivesAnEmptyTable()
    {
        MonsterDropTable t = MonsterDropTable.Load(new TokenStreamBuilder().Cursor(), out string error);
        Assert.Null(error);
        Assert.Equal(0, t.RowCount);
        Assert.Equal(-1, t.FindRow(5));
    }

    // ------------------------------------------------------------------ 选行与概率

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(10, 0)]
    [InlineData(11, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(30, 2)]
    [InlineData(31, -1)]
    public void FindRow_BoundsAreInclusive(int level, int expected)
    {
        Assert.Equal(expected, Load(Script()).FindRow(level));
    }

    [Fact]
    public void FindRow_FirstMatchingRowWins()
    {
        TokenStreamBuilder b = new TokenStreamBuilder()
            .Tag("[drop prob count]").Int(2)
            .Tag("[drop prob]").Ints(1, 50, 111, 0, 0, 0, 0).Ints(10, 20, 222, 0, 0, 0, 0);
        MonsterDropTable t = Load(b);
        Assert.Equal(0, t.FindRow(15));
    }

    [Fact]
    public void GoldProbability_MultipliesDifficultyRoleAndPartyBonusesWithTheRowValue()
    {
        MonsterDropTable t = Load(Script());
        // 第 0 行金币 2000;难度 3 = 1.5,种类 1 = 2,队伍 2 人 = 1.5 → 2000 × 1.5 × 2 × 1.5 = 9000
        Assert.Equal(9000, t.GoldProbability(0, role: 1, difficulty: 3, members: 2));
        Assert.Equal(2000, t.GoldProbability(0, role: 0, difficulty: 0, members: 1));
        Assert.Equal(0, t.GoldProbability(-1, 0, 0, 1));
        Assert.Equal(0, t.GoldProbability(3, 0, 0, 1));
    }

    [Fact]
    public void GoldProbability_RoundsToNearestEvenLikeTheFpu()
    {
        // 种类 0 的加成写成 0.5:5 × 0.5 = 2.5 → 2,7 × 0.5 = 3.5 → 4(平局取偶数)
        TokenStreamBuilder b = new TokenStreamBuilder()
            .Tag("[drop prob count]").Int(2)
            .Tag("[drop prob]").Ints(1, 10, 5, 0, 0, 0, 0).Ints(11, 20, 7, 0, 0, 0, 0)
            .Tag("[monster type drop bonusrate]").Floats(0.5f, 1f, 1f, 1f).Floats(Ones(16));
        MonsterDropTable t = Load(b);
        Assert.Equal(2, t.GoldProbability(0, 0, 0, 1));
        Assert.Equal(4, t.GoldProbability(1, 0, 0, 1));
    }

    // ------------------------------------------------------------------ 金币基础金额表

    [Fact]
    public void GoldGenTable_ReadsTriplesAndFallsBackToEntryZero()
    {
        GoldGenTable g = GoldTable();
        Assert.Equal((10, 1), g.Get(1));
        Assert.Equal((1500, 150 % 7), g.Get(150));
        Assert.Equal((2000, 200 % 7), g.Get(200));
        Assert.Equal((0, 0), g.Get(0));
        Assert.Equal((0, 0), g.Get(201));     // 大于 200 退回第 0 项(全 0)
        Assert.Equal((0, 0), g.Get(-5));
    }

    [Fact]
    public void GoldGenTable_WrongTagOrShortTableFails()
    {
        GoldGenTable a = GoldGenTable.Load(new TokenStreamBuilder().Tag("[other]").Int(1).Cursor(), out string errorA);
        Assert.Null(a);
        Assert.Contains("[other]", errorA);
        GoldGenTable b = GoldGenTable.Load(new TokenStreamBuilder().Tag("[gold drop ref table]").Ints(1, 10, 5, 2, 20, 5).Cursor(), out string errorB);
        Assert.Null(b);
        Assert.Contains("[gold drop ref table]", errorB);
    }

    // ------------------------------------------------------------------ 掷骰

    [Fact]
    public void Roll_DropsOnlyWhenTheRollIsStrictlyBelowTheProbability()
    {
        MonsterDropTable drops = Load(Script());
        GoldGenTable gold = GoldTable();
        var ctx = new DropContext(monsterLevel: 5, role: 0, dungeonLevel: 5, difficulty: 0);   // 概率 2000
        Assert.Empty(GoldDrop.Roll(drops, gold, ctx, new ScriptedRandom(2000)));
        Assert.Single(GoldDrop.Roll(drops, gold, ctx, new ScriptedRandom(1999, 0)));
        Assert.Single(GoldDrop.Roll(drops, gold, ctx, new ScriptedRandom(0, 0)));
    }

    [Fact]
    public void Roll_AsksTheTwoRandomFunctionsInServerOrderWithServerRanges()
    {
        MonsterDropTable drops = Load(Script());
        var rng = new ScriptedRandom(0, 3);
        List<int> pieces = GoldDrop.Roll(drops, GoldTable(), new DropContext(5, 0, 5, 0), rng);
        // 先 randInt(10000),再 GetRandomRange(−浮动, +浮动);等级 5 的浮动是 5 % 7 = 5
        Assert.Equal(new[] { "N10000", "R-5:5" }, rng.Calls);
        Assert.Equal(new[] { 50 + 50 * 3 / 100 }, pieces);   // 基础 50,浮动 +3% → 51
    }

    [Theory]
    [InlineData(100, -7, 93)]
    [InlineData(100, 7, 107)]
    [InlineData(15, -7, 14)]      // 15 × (−7) / 100 = −1.05,C 的整数除法向零取整 → −1
    [InlineData(15, 7, 16)]       // 15 × 7 / 100 = 1.05 → 1
    [InlineData(10, -6, 10)]      // 10 × (−6) / 100 = −0.6 → 0
    public void Roll_SwingIsAnIntegerPercentageTruncatedTowardZero(int baseAmount, int swing, int expected)
    {
        var b = new TokenStreamBuilder().Tag("[gold drop ref table]");
        for (int n = 1; n <= 200; n++)
        {
            b.Ints(n, baseAmount, 7);
        }
        GoldGenTable gold = GoldGenTable.Load(b.Cursor(), out _);
        List<int> pieces = GoldDrop.Roll(Load(Script()), gold, new DropContext(5, 0, 5, 0), new ScriptedRandom(0, swing));
        Assert.Equal(new[] { expected }, pieces);
    }

    [Fact]
    public void Roll_AmountIsAtLeastOne()
    {
        // 等级 0 与超过 200 的怪物在表里取到第 0 项(全 0),金额仍至少 1
        List<int> pieces = GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(250, 0, 5, 0), new ScriptedRandom(0, 0));
        Assert.Equal(new[] { 1 }, pieces);
    }

    [Fact]
    public void Roll_DropRateScalesTheProbabilityWithIntegerDivision()
    {
        MonsterDropTable drops = Load(Script());
        GoldGenTable gold = GoldTable();
        // 概率 2000,掉落率 150% → 3000;掉落率 50% → 1000
        Assert.Single(GoldDrop.Roll(drops, gold, new DropContext(5, 0, 5, 0, dropRate: 150), new ScriptedRandom(2999, 0)));
        Assert.Empty(GoldDrop.Roll(drops, gold, new DropContext(5, 0, 5, 0, dropRate: 150), new ScriptedRandom(3000)));
        Assert.Single(GoldDrop.Roll(drops, gold, new DropContext(5, 0, 5, 0, dropRate: 50), new ScriptedRandom(999, 0)));
        Assert.Empty(GoldDrop.Roll(drops, gold, new DropContext(5, 0, 5, 0, dropRate: 50), new ScriptedRandom(1000)));
        Assert.Empty(GoldDrop.Roll(drops, gold, new DropContext(5, 0, 5, 0, dropRate: 0), new ScriptedRandom(0)));
    }

    [Fact]
    public void Roll_NoRowForTheDungeonLevelMeansNoRollAtAll()
    {
        var rng = new ScriptedRandom();
        Assert.Empty(GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 0, 99, 0), rng));
        Assert.Empty(rng.Calls);    // 连随机数都不取
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(9)]
    public void Roll_RolesOutsideZeroToThreeDropNothing(int role)
    {
        var rng = new ScriptedRandom();
        Assert.Empty(GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, role, 5, 0), rng));
        Assert.Empty(rng.Calls);
    }

    [Fact]
    public void Roll_NullTablesDropNothing()
    {
        var ctx = new DropContext(5, 0, 5, 0);
        Assert.Empty(GoldDrop.Roll(null, GoldTable(), ctx, new ScriptedRandom()));
        Assert.Empty(GoldDrop.Roll(Load(Script()), null, ctx, new ScriptedRandom()));
    }

    [Fact]
    public void Roll_RepeatsOncePerRoll()
    {
        var rng = new ScriptedRandom(0, 0, 9999, 0, 0, 0);   // 第 1 次掉、第 2 次(9999 ≥ 2000)不掉、第 3 次掉
        List<int> pieces = GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 0, 5, 0, rolls: 3), rng);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(new[] { "N10000", "R-5:5", "N10000", "N10000", "R-5:5" }, rng.Calls);
    }

    [Fact]
    public void Roll_ProbabilityOverTenThousandAlwaysDrops()
    {
        // 第 2 行金币 10000 × 难度 4 的 2 倍 = 20000 > 10000,随机数最大 10000 也掉
        Assert.Single(GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(25, 0, 25, 4), new ScriptedRandom(10000, 0)));
    }

    // ------------------------------------------------------------------ 首领的金币堆

    [Fact]
    public void Roll_BossSplitsTheGoldIntoSeveralPiles()
    {
        // 首领(种类 3)人数 1:先 randInt(10000)(概率 2000 × 6 = 12000,必掉),再浮动,再 randInt(不触发 90 + 触发 10),再 randInt(堆数范围)
        var rng = new ScriptedRandom(0, 0, 95, 1);
        List<int> pieces = GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 3, 5, 0, members: 1), rng);
        Assert.Equal(new[] { "N10000", "R-5:5", "N100", "N1" }, rng.Calls);   // 第一组:堆数 1..2 → randInt(2 − 1)
        Assert.Equal(new[] { 1 + 1 }, new[] { pieces.Count });                  // 取到 1 → 堆数 1 + 1 = 2
        Assert.All(pieces, p => Assert.Equal(50, p));                           // 第一组金额 100%
    }

    [Fact]
    public void Roll_BossLuckyBranchUsesTheSecondGroupOfCountsAndTheSecondVolume()
    {
        // randInt(100) = 5 < 触发权重 10 → 第二组:堆数 4..6 → randInt(6 − 4) + 4;金额 110%
        var rng = new ScriptedRandom(0, 0, 5, 2);
        List<int> pieces = GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 3, 5, 0, members: 1), rng);
        Assert.Equal(new[] { "N10000", "R-5:5", "N100", "N2" }, rng.Calls);
        Assert.Equal(6, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(55, p));    // 50 × 110 / 100
    }

    [Fact]
    public void Roll_BossConditionRateFollowsThePartySize()
    {
        // 人数 3 的权重是 70/30:randInt(100);29 < 30 → 第二组,30 不小于 30 → 第一组
        var lucky = new ScriptedRandom(0, 0, 29, 0);
        Assert.Equal(4, GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 3, 5, 0, members: 3), lucky).Count);
        var plain = new ScriptedRandom(0, 0, 30, 0);
        Assert.Single(GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 3, 5, 0, members: 3), plain));
    }

    [Fact]
    public void Roll_BossWithAnOutOfRangePartySizeDropsOnePileAtFullAmount()
    {
        // 人数 5 不在 1 到 4:_getBossGoldDrop 返回 false,只出一堆、金额不乘百分比
        var rng = new ScriptedRandom(0, 0);
        List<int> pieces = GoldDrop.Roll(Load(Script()), GoldTable(), new DropContext(5, 3, 5, 0, members: 5), rng);
        Assert.Equal(new[] { 50 }, pieces);
    }

    [Fact]
    public void Roll_BossPileAmountIsAtLeastOne()
    {
        // 基础金额 2、金额百分比 40:2 × 40 / 100 = 0.8 → 0 → 取 1
        var b = new TokenStreamBuilder().Tag("[gold drop ref table]");
        for (int n = 1; n <= 200; n++)
        {
            b.Ints(n, 2, 0);
        }
        GoldGenTable gold = GoldGenTable.Load(b.Cursor(), out _);
        List<int> pieces = GoldDrop.Roll(Load(Script(volume0: 40, volume1: 40)), gold, new DropContext(5, 3, 5, 0, members: 1), new ScriptedRandom(0, 0, 95, 1));
        Assert.Equal(new[] { 1, 1 }, pieces);
    }

    // ------------------------------------------------------------------ 随机数

    [Fact]
    public void DropRandom_IsDeterministicPerSeed()
    {
        var a = new DropRandom(7);
        var b = new DropRandom(7);
        var c = new DropRandom(8);
        var sa = Enumerable.Range(0, 20).Select(_ => a.NextInclusive(1000)).ToArray();
        var sb = Enumerable.Range(0, 20).Select(_ => b.NextInclusive(1000)).ToArray();
        var sc = Enumerable.Range(0, 20).Select(_ => c.NextInclusive(1000)).ToArray();
        Assert.Equal(sa, sb);
        Assert.NotEqual(sa, sc);
    }

    [Fact]
    public void DropRandom_CoversBothEndsOfItsRanges()
    {
        var r = new DropRandom(1);
        var seen = new HashSet<int>();
        for (int i = 0; i < 400; i++)
        {
            int v = r.Range(-2, 2);
            Assert.InRange(v, -2, 2);
            seen.Add(v);
        }
        Assert.Equal(5, seen.Count);
        var upper = new HashSet<int>();
        for (int i = 0; i < 400; i++)
        {
            upper.Add(r.NextInclusive(3));
        }
        Assert.Equal(new[] { 0, 1, 2, 3 }, upper.OrderBy(v => v).ToArray());
    }

    // ------------------------------------------------------------------ 副本标准等级

    private static ScriptLine DgnLine(string tag, params int[] values)
    {
        var v = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            v[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, v);
    }

    [Fact]
    public void StandardLevel_ReadsTheBasisLevelTag()
    {
        Assert.Equal(37, GoldDrop.StandardLevel(new[] { DgnLine("[name]", 5), DgnLine("[basis level]", 37), DgnLine("[type]", 1) }));
    }

    [Fact]
    public void StandardLevel_LastOccurrenceWins()
    {
        Assert.Equal(9, GoldDrop.StandardLevel(new[] { DgnLine("[basis level]", 37), DgnLine("[basis level]", 9) }));
    }

    [Fact]
    public void StandardLevel_MissingTagOrNumberOrScriptIsZero()
    {
        Assert.Equal(0, GoldDrop.StandardLevel(new[] { DgnLine("[name]", 5) }));
        Assert.Equal(0, GoldDrop.StandardLevel(new[] { DgnLine("[basis level]") }));
        Assert.Equal(0, GoldDrop.StandardLevel(new ScriptLine[0]));
        Assert.Equal(0, GoldDrop.StandardLevel(null));
    }

    [Fact]
    public void StandardLevel_TagMatchIgnoresCase()
    {
        Assert.Equal(12, GoldDrop.StandardLevel(new[] { DgnLine("[BASIS LEVEL]", 12) }));
    }

    [Fact]
    public void DropRandom_DegenerateRanges()
    {
        var r = new DropRandom(3);
        Assert.Equal(0, r.NextInclusive(0));
        Assert.Equal(0, r.NextInclusive(-4));
        Assert.Equal(5, r.Range(5, 5));
        for (int i = 0; i < 50; i++)
        {
            Assert.InRange(r.Range(5, 2), 2, 5);      // lo 大于 hi 时对调(GetRandomRange 也是)
        }
    }
}
