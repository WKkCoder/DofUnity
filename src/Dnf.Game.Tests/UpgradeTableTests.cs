using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 强化表的读入与取行。数据全是合成的(数值随手写,选的都是二进制下精确的小数,方便手算),与原版无关。
/// 整库证据(独立 Python 实现逐项对拍)在 enhance-check 里,这里钉住读法、取整的三步次序与边界。
/// </summary>
public class UpgradeTableTests
{
    private static ScriptValue I(int v) => TableBuilder.I(v);

    private static ScriptValue F(float v) => TableBuilder.F(v);

    private static ScriptValue S(string v) => TableBuilder.S(v);

    private static ScriptValue[] Floats(params float[] v) => v.Select(F).ToArray();

    // ------------------------------------------------------------------ 读表

    [Fact]
    public void ARowIsSeventeenValuesAndLandsInTheStructOrder()
    {
        // 一行:A f1 f2 f3 f4 f5 | i1..i5 | B g1..g5;行结构里的次序是 [0]=A [1]=f1 [2]=f3 [3]=f5 [5]=f2 [6]=f4
        var line = new ScriptLine("[table]", new[]
        {
            I(10), F(1.5f), F(2.5f), F(3.5f), F(4.5f), F(5.5f),
            I(20000), I(2), I(3), I(910), I(7),
            I(90), F(11.5f), F(12.5f), F(13.5f), F(14.5f), F(15.5f),
        });
        Assert.True(UpgradeTable.TryParse(new[] { line }, out UpgradeTable table));
        Assert.Equal(1, table.RowCount);
        UpgradeRow row = table.Row(0);
        Assert.Equal(new[] { 10f, 1.5f, 3.5f, 5.5f, 0f, 2.5f, 4.5f, 0f }, row.StatsA);
        Assert.Equal(new[] { 90f, 11.5f, 13.5f, 15.5f, 0f, 12.5f, 14.5f, 0f }, row.StatsB);
        Assert.Equal((20000, 2, 3, 910, 7), (row.FailChance, row.PenaltyType, row.PenaltyLevels, row.MaterialCode, row.MaterialCount));
    }

    [Fact]
    public void ARowKeepsLargeIntegerCodesExactly()
    {
        // 材料编号这类整数不能先转成浮点再转回来(16777217 以上 float 装不下)
        UpgradeTable table = new TableBuilder().Row(1, 1, 1, 16777217, 1).Build();
        Assert.Equal(16777217, table.Row(0).MaterialCode);
    }

    [Fact]
    public void ATrailingPartialRowIsIgnoredAndRowsOutsideTheTableAreNull()
    {
        var values = new List<ScriptValue>();
        for (int i = 0; i < 17 + 9; i++)
        {
            values.Add(I(1));
        }
        Assert.True(UpgradeTable.TryParse(new[] { new ScriptLine("[table]", values.ToArray()) }, out UpgradeTable table));
        Assert.Equal(1, table.RowCount);
        Assert.Null(table.Row(1));
        Assert.Null(table.Row(-1));
        Assert.NotNull(table.Row(0));
    }

    [Fact]
    public void ThereCanBeNoTableAtAll()
    {
        Assert.True(UpgradeTable.TryParse(new ScriptLine[0], out UpgradeTable table));
        Assert.Equal(0, table.RowCount);
        Assert.Null(table.GetInfo(0, 1, 0, EquipmentParts.Coat));
    }

    [Fact]
    public void NoticeAndAmplificationConstantsAreRead()
    {
        UpgradeTable table = new TableBuilder().Rows(3, 1, 1, 1, 1, 1)
            .Line("[notice]", I(9))
            .Line("[amplification const]", I(1), I(2), I(3), I(4), I(5), I(6), I(7), I(8), I(9), I(10), I(11), I(12))
            .Build();
        Assert.Equal(9, table.NoticeLevel);
        Assert.Equal(new[] { 1, 2, 3, 4 }, table.Row(0).Amplification);
        Assert.Equal(new[] { 5, 6, 7, 8 }, table.Row(1).Amplification);
        Assert.Equal(new[] { 9, 10, 11, 12 }, table.Row(2).Amplification);
        Assert.Empty(table.Problems);
    }

    [Fact]
    public void ShortSectionsAreRecordedAsProblemsButTheTableStillLoads()
    {
        UpgradeTable table = new TableBuilder().Rows(2, 1, 1, 1, 1, 1)
            .Line("[amplification const]", I(1), I(2), I(3))
            .Line("[cost]", I(5), I(6))
            .Line("[level]", F(1f))
            .Build();
        Assert.True(table.Problems.Count >= 3);
        Assert.Equal(new[] { 1, 2, 3, 0 }, table.Row(0).Amplification);
        Assert.Equal(new[] { 0, 0, 0, 0 }, table.Row(1).Amplification);
    }

    [Fact]
    public void DestroyAndMaxLevelsDefaultToMinusOneAndReadByRarityName()
    {
        UpgradeTable plain = new TableBuilder().Rows(2, 1, 1, 1, 1, 1).Build();
        for (int rarity = 0; rarity < UpgradeTable.Rarities; rarity++)
        {
            Assert.Equal(-1, plain.DestroyLevel(rarity));
            Assert.Equal(-1, plain.MaxLevel(rarity));
        }
        UpgradeTable table = new TableBuilder().Rows(2, 1, 1, 1, 1, 1)
            .Line("[destroy level by rarity]", S("common"), I(7), S("uncommon"), I(8), S("chronicle"), I(9))
            .Line("[max upgrade level by rarity]", S("rare"), I(10), S("unique"), I(11), S("epic"), I(12))
            .Build();
        Assert.Equal(new[] { 7, 8, -1, -1, -1, 9 }, Enumerable.Range(0, 6).Select(table.DestroyLevel).ToArray());
        Assert.Equal(new[] { -1, -1, 10, 11, 12, -1 }, Enumerable.Range(0, 6).Select(table.MaxLevel).ToArray());
        Assert.Equal(-1, table.DestroyLevel(-1));
        Assert.Equal(-1, table.DestroyLevel(6));
        Assert.Equal(-1, table.MaxLevel(100));
    }

    [Theory]
    [InlineData("common", 0)]
    [InlineData("uncommon", 1)]
    [InlineData("rare", 2)]
    [InlineData("unique", 3)]
    [InlineData("epic", 4)]
    [InlineData("chronicle", 5)]
    [InlineData("legend", 6)]
    [InlineData("Common", 6)]
    [InlineData("", 6)]
    public void RarityNamesMapToIndexes(string name, int expected)
    {
        Assert.Equal(expected, UpgradeTable.RarityOf(name));
    }

    [Fact]
    public void AnUnknownRarityNameOrAMissingIntegerVoidsTheWholeTable()
    {
        Assert.False(UpgradeTable.TryParse(new[] { new ScriptLine("[destroy level by rarity]", new[] { S("mythic"), I(3) }) }, out UpgradeTable t1));
        Assert.Null(t1);
        Assert.False(UpgradeTable.TryParse(new[] { new ScriptLine("[max upgrade level by rarity]", new[] { S("common") }) }, out UpgradeTable t2));
        Assert.Null(t2);
        Assert.False(UpgradeTable.TryParse(new[] { new ScriptLine("[max upgrade level by rarity]", new[] { S("common"), S("rare") }) }, out UpgradeTable t3));
        Assert.Null(t3);
    }

    [Fact]
    public void TryParseRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => UpgradeTable.TryParse(null, out _));
    }

    [Fact]
    public void TheCostRelatedSectionsReadFloatsAndFillMissingTailWithZero()
    {
        UpgradeTable table = new TableBuilder().Rows(1, 1, 1, 1, 1, 1)
            .FlatCost(10)
            .Line("[repair cost rate by upgrade level]", Floats(2f, 3f))
            .Build();
        Assert.Equal(2f, table.RepairCostRate(0));
        Assert.Equal(3f, table.RepairCostRate(1));
        Assert.Equal(0f, table.RepairCostRate(2));
        Assert.Equal(1f, table.RepairCostRate(100));
        Assert.Equal(1f, table.RepairCostRate(-1));
        Assert.Contains(table.Problems, p => p.Contains("repair cost rate size"));
    }

    // ------------------------------------------------------------------ 取行

    private static UpgradeTable StatTable()
    {
        // 稀有度权重 0.5、1、2、4、8、16;[level] 10 20 40 80;一行:A=10 f1=2 f2=4 f3=8 f4=16 f5=32,B 全 0
        var b = new TableBuilder();
        b.Line("[table]", new[]
        {
            I(10), F(2f), F(4f), F(8f), F(16f), F(32f),
            I(30000), I(2), I(4), I(910), I(5),
            I(0), F(0f), F(0f), F(0f), F(0f), F(0f),
        });
        return b.Line("[stat weights by rarity]", Floats(0.5f, 1f, 2f, 4f, 8f, 16f))
            .Line("[level]", Floats(10f, 20f, 40f, 80f))
            .FlatCost(1)
            .Build();
    }

    [Fact]
    public void StatsScaleByTheRarityWeightAndFourOfThemByGradeOverLevelPlusOne()
    {
        UpgradeTable table = StatTable();
        // 装备等级 20:四项乘 (20÷档位+1) = 3、2、1.5、1.25;[5] [6] 只乘权重;[4] [7] 恒为 0。稀有度 0 权重 0.5
        UpgradeInfo info = table.GetInfo(0, 20, 0, EquipmentParts.Weapon);
        Assert.Equal(new[] { 15f, 2f, 6f, 20f, 0f, 2f, 8f, 0f }, info.StatsA);
        Assert.Equal(new float[8], info.StatsB);
        Assert.Equal((30000, 2, 4, 910, 5), (info.FailChance, info.PenaltyType, info.PenaltyLevels, info.MaterialCode, info.MaterialCount));
        // 稀有度 5 权重 16:同一行放大 32 倍
        UpgradeInfo high = table.GetInfo(5, 20, 0, EquipmentParts.Weapon);
        Assert.Equal(new[] { 480f, 64f, 192f, 640f, 0f, 64f, 256f, 0f }, high.StatsA);
    }

    [Fact]
    public void GradeZeroGivesFactorOneForTheScaledFields()
    {
        UpgradeInfo info = StatTable().GetInfo(1, 0, 0, EquipmentParts.Weapon);
        Assert.Equal(new[] { 10f, 2f, 8f, 32f, 0f, 4f, 16f, 0f }, info.StatsA);
    }

    [Theory]
    [InlineData(-1, 20, 0, 12)]   // 稀有度小于 0
    [InlineData(6, 20, 0, 12)]    // 稀有度大于 5
    [InlineData(0, -1, 0, 12)]    // 装备等级小于 0
    [InlineData(0, 100, 0, 12)]   // 装备等级大于 99
    [InlineData(0, 20, -1, 12)]   // 强化等级小于 0
    [InlineData(0, 20, 1, 12)]    // 强化等级不在表里(只有 1 行)
    [InlineData(0, 20, 0, 9)]     // 部位小于 10
    [InlineData(0, 20, 0, 22)]    // 部位大于 21
    public void GetInfoReturnsNullOutsideTheRanges(int rarity, int grade, int upgrade, int part)
    {
        Assert.Null(StatTable().GetInfo(rarity, grade, upgrade, part));
    }

    [Theory]
    [InlineData(0, 20, 0, 10)]
    [InlineData(5, 99, 0, 21)]
    [InlineData(0, 0, 0, 10)]
    public void GetInfoAcceptsTheEdgesOfTheRanges(int rarity, int grade, int upgrade, int part)
    {
        Assert.NotNull(StatTable().GetInfo(rarity, grade, upgrade, part));
    }

    [Fact]
    public void GoldCostRoundsInThreeSteps()
    {
        // [cost] 第 i 项 = 100 + i,装备等级 20 → 120;[type] 里 12 号部位(第 3 个)权重 1.5 → 180(+0.5 截断仍是 180);
        // 稀有度 2 的费用权重 0.75 → 135(135.5 截断);强化等级 3 的"按强化等级权重"2.0 → 2.0 × 135 = 270
        UpgradeTable table = new TableBuilder().Rows(4, 1, 1, 1, 1, 1)
            .CostByGrade(100)
            .Line("[type]", Floats(1f, 1f, 1.5f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f))
            .Line("[cost weights by rarity]", Floats(1f, 1f, 0.75f, 1f, 1f, 1f))
            .Line("[cost weight by upgrade level]", Floats(3f, 2f))
            .Build();
        Assert.Equal(270, table.GetInfo(2, 20, 3, 12).GoldCost);
        // 没有"按强化等级权重"的等级不走第三步:135
        Assert.Equal(135, table.GetInfo(2, 20, 2, 12).GoldCost);
        // 别的部位权重 1.0:120 → 120 → 稀有度 2 乘 0.75 = 90.5 → 90
        Assert.Equal(90, table.GetInfo(2, 20, 2, 10).GoldCost);
    }

    [Fact]
    public void EachStepTruncatesTowardZeroAfterAddingAHalf()
    {
        // 100 × 0.499 = 49.9 + 0.5 = 50.4 → 50(不是 49,也不是 51);100 × 0.495 = 49.5 + 0.5 = 50.0 → 50;100 × 0.494 = 49.4 + 0.5 = 49.9 → 49
        UpgradeTable table = new TableBuilder().Rows(1, 1, 1, 1, 1, 1)
            .FlatCost(100)
            .Line("[cost weights by rarity]", Floats(0.499f, 0.494f, 1f, 1f, 1f, 1f))
            .Build();
        Assert.Equal(50, table.GetInfo(0, 1, 0, 10).GoldCost);
        Assert.Equal(49, table.GetInfo(1, 1, 0, 10).GoldCost);
    }

    [Fact]
    public void TheFirstStepRoundsToNearestWhileTheLastStepOnlyTruncates()
    {
        // 第一步([type] 部位权重)先加 0.5 再截断:100 × 0.499 = 49.9 → 50;100 × 0.494 = 49.4 → 49
        // 第三步(按强化等级权重)不加 0.5:0.499 × 100 = 49.9 → 49;0.506 × 100 = 50.6 → 50(不是 51)
        UpgradeTable table = new TableBuilder().Rows(8, 1, 1, 1, 1, 1)
            .FlatCost(100)
            .Line("[type]", Floats(0.499f, 0.494f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f))
            .Line("[cost weight by upgrade level]", Floats(5f, 0.499f, 6f, 0.506f))
            .Build();
        Assert.Equal(50, table.GetInfo(0, 1, 0, 10).GoldCost);
        Assert.Equal(49, table.GetInfo(0, 1, 0, 11).GoldCost);
        Assert.Equal(49, table.GetInfo(0, 1, 5, 12).GoldCost);
        Assert.Equal(50, table.GetInfo(0, 1, 6, 12).GoldCost);
    }

    [Fact]
    public void TheLevelCostWeightUsesLevelEighteenForEverythingAbove()
    {
        UpgradeTable table = new TableBuilder().Rows(30, 1, 1, 1, 1, 1)
            .FlatCost(100)
            .Line("[cost weight by upgrade level]", Floats(18f, 0.5f, 17f, 3f))
            .Build();
        Assert.Equal(50, table.GetInfo(0, 1, 18, 10).GoldCost);
        Assert.Equal(50, table.GetInfo(0, 1, 25, 10).GoldCost);
        Assert.Equal(50, table.GetInfo(0, 1, 29, 10).GoldCost);
        Assert.Equal(300, table.GetInfo(0, 1, 17, 10).GoldCost);
        Assert.Equal(100, table.GetInfo(0, 1, 16, 10).GoldCost);
    }

    [Fact]
    public void WeightsDefaultToOneWhenTheirSectionsAreMissing()
    {
        UpgradeInfo info = new TableBuilder().Rows(1, 1, 1, 1, 1, 1).FlatCost(77).Build().GetInfo(3, 5, 0, 15);
        Assert.Equal(77, info.GoldCost);
    }

    [Fact]
    public void WithoutACostSectionTheBasePriceIsOne()
    {
        Assert.Equal(1, new TableBuilder().Rows(1, 1, 1, 1, 1, 1).Build().GetInfo(0, 50, 0, 10).GoldCost);
    }

    [Fact]
    public void GetInfoReturnsAFreshObjectEachTimeSoCallersMayAdjustIt()
    {
        UpgradeTable table = StatTable();
        UpgradeInfo a = table.GetInfo(0, 20, 0, 10);
        a.MaterialCount = 999;
        a.StatsA[0] = -1f;
        UpgradeInfo b = table.GetInfo(0, 20, 0, 10);
        Assert.Equal(5, b.MaterialCount);
        Assert.Equal(15f, b.StatsA[0]);
        Assert.Equal(5, table.Row(0).MaterialCount);
    }

    // ------------------------------------------------------------------ 惩罚类型

    [Fact]
    public void PenaltyTypeIsTheRowsOwnUntilTheRaritysDestroyLevelThenThree()
    {
        UpgradeTable table = new TableBuilder()
            .Row(1, 1, 1, 1, 1).Row(1, 2, 1, 1, 1).Row(1, 1, 1, 1, 1).Row(1, 2, 1, 1, 1)
            .Destroy("common", 2)
            .Build();
        Assert.Equal(1, table.PenaltyTypeAt(0, 0));
        Assert.Equal(2, table.PenaltyTypeAt(1, 0));
        Assert.Equal(3, table.PenaltyTypeAt(2, 0));   // 强化等级 = 销毁等级:销毁
        Assert.Equal(3, table.PenaltyTypeAt(3, 0));
        Assert.Equal(2, table.PenaltyTypeAt(3, 1));   // 稀有度 1 没有销毁等级(-1):用行自己的类型
        Assert.Equal(1, table.PenaltyTypeAt(99, 1));  // 越过表的行数:按 1
    }

    [Fact]
    public void ADestroyLevelOfZeroMeansEveryFailureDestroys()
    {
        UpgradeTable table = new TableBuilder().Rows(3, 1, 1, 1, 1, 1).Destroy("rare", 0).Build();
        Assert.Equal(3, table.PenaltyTypeAt(0, 2));
    }
}
