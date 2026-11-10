using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 宠物的三份配套数据:通用配置(<see cref="PetCommon"/>)、全局经验表(<see cref="PetExpTable"/>)、物品上的宠物字段(<see cref="PetItemData"/>)。
/// 标记流全是合成的,期望值手算。
/// </summary>
public class PetDataTests
{
    // ------------------------------------------------------------------ 通用配置

    private static PetCommon ReadCommon(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return PetCommon.Parse(tokens, strings);
    }

    [Fact]
    public void CommonReadsSixNumbersIntoSixFields()
    {
        PetCommon c = ReadCommon(new TokenStreamBuilder()
            .Tag("[response delay]").Int(11).Tag("[second per fill point spend]").Int(22).Tag("[second per fill point charge]").Int(33)
            .Tag("[MAX fill point]").Int(44).Tag("[fill point to eat feed]").Int(55).Tag("[dying delay]").Int(66));
        Assert.Equal(11, c.ResponseDelay);
        Assert.Equal(22, c.SecondsPerPointSpend);
        Assert.Equal(33, c.SecondsPerPointCharge);
        Assert.Equal(44, c.MaxFillPoint);
        Assert.Equal(55, c.FillPointToEatFeed);
        Assert.Equal(66, c.DyingDelay);
    }

    [Fact]
    public void CommonFieldsDefaultToZeroAndUnknownTagsAreSkipped()
    {
        PetCommon c = ReadCommon(new TokenStreamBuilder().Tag("[something else]").Int(9).Tag("[MAX fill point]").Int(44));
        Assert.Equal(44, c.MaxFillPoint);
        Assert.Equal(0, c.ResponseDelay);
        Assert.Equal(0, c.SecondsPerPointSpend);
        Assert.Equal(0, c.SecondsPerPointCharge);
        Assert.Equal(0, c.FillPointToEatFeed);
        Assert.Equal(0, c.DyingDelay);
        Assert.Equal(0, ReadCommon(new TokenStreamBuilder()).MaxFillPoint);
    }

    [Fact]
    public void CommonFillTermsAreComparedWithTheEngineLiteralsOneByOne()
    {
        Assert.True(new PetCommon { SecondsPerPointSpend = 60, SecondsPerPointCharge = 360, MaxFillPoint = 100 }.FillTermsMatchEngineLiterals);
        Assert.False(new PetCommon { SecondsPerPointSpend = 61, SecondsPerPointCharge = 360, MaxFillPoint = 100 }.FillTermsMatchEngineLiterals);
        Assert.False(new PetCommon { SecondsPerPointSpend = 60, SecondsPerPointCharge = 359, MaxFillPoint = 100 }.FillTermsMatchEngineLiterals);
        Assert.False(new PetCommon { SecondsPerPointSpend = 60, SecondsPerPointCharge = 360, MaxFillPoint = 99 }.FillTermsMatchEngineLiterals);
        // 吃饲料所需一项不参与这个比较(服务端喂食用写死的增量)。
        Assert.True(new PetCommon { SecondsPerPointSpend = 60, SecondsPerPointCharge = 360, MaxFillPoint = 100, FillPointToEatFeed = 7 }.FillTermsMatchEngineLiterals);
    }

    [Fact]
    public void CommonRejectsNonTokenFiles()
    {
        var strings = new PvfStringTable(new TokenStreamBuilder().StringTableBytes());
        Assert.Null(PetCommon.Parse(new byte[] { 1, 2, 3 }, strings));
        Assert.Null(PetCommon.Parse(new TokenStreamBuilder().Bytes(), null));
        var b = new TokenStreamBuilder().Tag("[dying delay]").Int(5);
        Assert.Equal(5, PetCommon.Parse(b.Bytes(), new PvfStringTable(b.StringTableBytes())).DyingDelay);
    }

    // ------------------------------------------------------------------ 经验表

    private static PetExpTable ReadTable(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return PetExpTable.Parse(tokens, strings);
    }

    [Fact]
    public void ATableAlwaysReadsNinetyNineIntegersAndPadsTheRestWithZero()
    {
        PetExpTable t = ReadTable(new TokenStreamBuilder().Ints(10, 20, 30));
        Assert.Equal(3, t.ReadCount);
        Assert.Equal(10, t.Entry(0));
        Assert.Equal(20, t.Entry(1));
        Assert.Equal(30, t.Entry(2));
        for (int i = 3; i < PetRules.ExpTableEntries; i++)
        {
            Assert.Equal(0, t.Entry(i));
        }
        Assert.Equal(0, t.Entry(PetRules.ExpTableEntries));
        Assert.Equal(0, t.Entry(-1));
    }

    [Fact]
    public void ATableIgnoresIntegersBeyondNinetyNine()
    {
        var b = new TokenStreamBuilder();
        for (int i = 1; i <= 120; i++)
        {
            b.Int(i);
        }
        PetExpTable t = ReadTable(b);
        Assert.Equal(PetRules.ExpTableEntries, t.ReadCount);
        Assert.Equal(1, t.Entry(0));
        Assert.Equal(99, t.Entry(98));
    }

    [Fact]
    public void ANonIntegerTokenInTheTableCostsOneReadAndStoresZero()
    {
        PetExpTable t = ReadTable(new TokenStreamBuilder().Int(5).Str("x").Int(7));
        Assert.Equal(2, t.ReadCount);
        Assert.Equal(5, t.Entry(0));
        Assert.Equal(0, t.Entry(1));
        Assert.Equal(7, t.Entry(2));
        // 浮点放在整数位置向零取整。
        Assert.Equal(12, ReadTable(new TokenStreamBuilder().Float(12.9f)).Entry(0));
    }

    [Fact]
    public void ExpToReachIsEntryLevelMinusTwoAndZeroOutsideTwoToNinetyNine()
    {
        var t = new PetExpTable(10, 30, 60, 100);
        Assert.Equal(0, t.ExpToReach(1));
        Assert.Equal(10, t.ExpToReach(2));
        Assert.Equal(30, t.ExpToReach(3));
        Assert.Equal(100, t.ExpToReach(5));
        Assert.Equal(0, t.ExpToReach(6));
        Assert.Equal(0, t.ExpToReach(99));
        Assert.Equal(0, t.ExpToReach(100));
        Assert.Equal(0, t.ExpToReach(0));
        Assert.Equal(0, t.ExpToReach(-5));
    }

    [Fact]
    public void ExpToReachReadsTheLastLevelSlotToo()
    {
        // 第 l 级查第 l-2 项:第 2 级是下标 0,第 99 级是下标 97;第 99 项(下标 98)读了、可哪一级都用不到。
        var t = new PetExpTable(Enumerable.Range(1, 99).Select(i => i * 10).ToArray());
        Assert.Equal(10, t.ExpToReach(2));
        Assert.Equal(970, t.ExpToReach(98));
        Assert.Equal(980, t.ExpToReach(99));
        Assert.Equal(990, t.Entry(98));
        Assert.Equal(0, t.ExpToReach(100));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(29, 2)]
    [InlineData(30, 3)]
    [InlineData(59, 3)]
    public void LevelOfExpFindsTheLastThresholdNotAboveTheExp(int exp, int level)
    {
        // 门槛 10、30、60(第 2、3、4 级),之后全是 0:经验还没到最后一个有效门槛时,表等级就是"门槛不大于经验的个数 + 1"。
        Assert.Equal(level, new PetExpTable(10, 30, 60).LevelOfExp(exp));
    }

    [Fact]
    public void ExpAtTheLastRealThresholdJumpsToNinetyEightBecauseTheZeroPaddingKeepsPassing()
    {
        // 服务端怪癖:门槛 0 一路满足,经验到了最后一个有效门槛(含)以后表等级直接是 98(循环上界),
        // 由脚本的 [max level] 夹住;这里把它钉住,好让上层的夹法有据可查。
        var t = new PetExpTable(10, 30, 60);
        Assert.Equal(3, t.LevelOfExp(59));
        Assert.Equal(98, t.LevelOfExp(60));
        Assert.Equal(98, t.LevelOfExp(61));
        Assert.Equal(98, t.LevelOfExp(int.MaxValue));
        Assert.Equal(98, new PetExpTable().LevelOfExp(0));
    }

    [Fact]
    public void ATableConstructedWithTooManyOrNoThresholdsStillHasNinetyNineSlots()
    {
        var many = new int[150];
        many[98] = 7;
        many[99] = 9;
        var t = new PetExpTable(many);
        Assert.Equal(PetRules.ExpTableEntries, t.ReadCount);
        Assert.Equal(7, t.Entry(98));
        Assert.Equal(0, new PetExpTable((int[])null).ReadCount);
        Assert.Equal(0, new PetExpTable().Entry(0));
    }

    [Fact]
    public void TheTableRejectsNonTokenFiles()
    {
        var strings = new PvfStringTable(new TokenStreamBuilder().StringTableBytes());
        Assert.Null(PetExpTable.Parse(new byte[] { 9, 9 }, strings));
        Assert.Null(PetExpTable.Parse(new TokenStreamBuilder().Bytes(), null));
        var b = new TokenStreamBuilder().Ints(4, 8);
        PetExpTable t = PetExpTable.Parse(b.Bytes(), new PvfStringTable(b.StringTableBytes()));
        Assert.Equal(2, t.ReadCount);
        Assert.Equal(8, t.Entry(1));
    }

    // ------------------------------------------------------------------ 物品上的宠物字段

    private static PetItemData ReadItem(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return PetItemData.Parse(tokens, strings);
    }

    [Fact]
    public void AnItemCarriesSpeciesMinimumLevelAndExperienceRate()
    {
        PetItemData d = ReadItem(new TokenStreamBuilder()
            .Tag("[creature species]").Int(901).Tag("[creature minimum level]").Int(12).Tag("[creature experience amount rate]").Float(12.5f));
        Assert.Equal(901, d.Species);
        Assert.Equal(12, d.MinimumLevel);
        Assert.Equal(12.5f, d.ExperienceRate);
    }

    [Fact]
    public void ItemFieldsDefaultToZeroAndOtherTagsAreSkipped()
    {
        PetItemData d = ReadItem(new TokenStreamBuilder().Tag("[name]").Str("Some Item").Tag("[minimum level]").Int(50).Tag("[price]").Int(100));
        Assert.Equal(0, d.Species);
        Assert.Equal(0, d.MinimumLevel);
        Assert.Equal(0f, d.ExperienceRate);
    }

    [Fact]
    public void TheCreatureMinimumLevelIsNotTheItemsOwnMinimumLevel()
    {
        PetItemData d = ReadItem(new TokenStreamBuilder().Tag("[minimum level]").Int(50).Tag("[creature minimum level]").Int(3));
        Assert.Equal(3, d.MinimumLevel);
    }

    [Fact]
    public void AnIntegerWrittenAsTheRateBecomesAFloat()
    {
        Assert.Equal(7f, ReadItem(new TokenStreamBuilder().Tag("[creature experience amount rate]").Int(7)).ExperienceRate);
        Assert.Equal(0f, ReadItem(new TokenStreamBuilder().Tag("[creature experience amount rate]").Str("x")).ExperienceRate);
    }

    [Fact]
    public void TheItemParserRejectsNonTokenFilesAndReadsBytes()
    {
        var strings = new PvfStringTable(new TokenStreamBuilder().StringTableBytes());
        Assert.Null(PetItemData.Parse(new byte[] { 0 }, strings));
        Assert.Null(PetItemData.Parse(new TokenStreamBuilder().Bytes(), null));
        var b = new TokenStreamBuilder().Tag("[creature species]").Int(903);
        Assert.Equal(903, PetItemData.Parse(b.Bytes(), new PvfStringTable(b.StringTableBytes())).Species);
    }
}
