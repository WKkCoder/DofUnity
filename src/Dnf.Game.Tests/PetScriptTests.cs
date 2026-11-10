using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 宠物脚本(.cre)读取:各标签的读取形状、缺省值、神器格颜色的读法、成长一节的"块模式"(只在结束标签处提交)与种类判断。
/// 标记流全是合成的(编号 900 起),期望值按服务端装载函数的规则手算,不含任何原版数据。
/// </summary>
public class PetScriptTests
{
    private static PetDef Read(TokenStreamBuilder b, out PetScriptReport report)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return PetScript.Parse(tokens, strings, 901, "creature/test/p901.cre", out report);
    }

    private static PetDef Read(TokenStreamBuilder b) => Read(b, out _);

    private static TokenStreamBuilder Grow(params (string Tag, int Value)[] fields)
    {
        var b = new TokenStreamBuilder().Tag(PetScript.GrowOpenTag);
        foreach ((string tag, int value) in fields)
        {
            b.Tag(tag).Int(value);
        }
        return b;
    }

    // ------------------------------------------------------------------ 缺省值与数值标签

    [Fact]
    public void AnEmptyScriptKeepsTheServersClearDefaults()
    {
        PetDef def = Read(new TokenStreamBuilder(), out PetScriptReport report);
        Assert.Equal(901, def.Id);
        Assert.Equal("creature/test/p901.cre", def.Path);
        Assert.Equal(string.Empty, def.Name);
        Assert.Equal(1, def.StartLevel);
        Assert.Equal(1, def.PermissionLevel);
        Assert.Equal(1, def.MaxLevel);
        Assert.Equal(1, def.ParentMaxLevel);
        Assert.Equal(PetArtifactColors.None, def.ArtifactColors);
        Assert.Equal(0, def.EvolutionQuest);
        Assert.Equal(0, def.EvolutionCreatureId);
        Assert.Equal(0, def.EvolutionLevel);
        Assert.False(def.Grow.UsesExpItem);
        Assert.Equal(-1, def.Grow.SelectLevel);
        Assert.Equal(-1, def.Grow.ATypeCreatureId);
        Assert.Equal(-1, def.Grow.BTypeCreatureId);
        Assert.Equal(-1, def.Grow.ATypeItemIndex);
        Assert.Equal(-1, def.Grow.BTypeItemIndex);
        Assert.Equal(1, def.Grow.ItemsPerLevel);
        Assert.Equal(PetKind.Normal, def.Kind);
        Assert.Equal(0, report.DiscardedTokens);
        Assert.Equal(0, report.GrowBlocks);
        Assert.Equal(0, report.GrowCommitted);
        Assert.Equal(0, report.GrowAbandoned);
    }

    [Fact]
    public void EachNumericTagFillsItsOwnField()
    {
        PetDef def = Read(new TokenStreamBuilder()
            .Tag("[start level]").Int(3).Tag("[permission level]").Int(4).Tag("[max level]").Int(40).Tag("[parent max level]").Int(50)
            .Tag("[evolution quest]").Int(7).Tag("[evolution creature id]").Int(902).Tag("[evolution level]").Int(15));
        Assert.Equal(3, def.StartLevel);
        Assert.Equal(4, def.PermissionLevel);
        Assert.Equal(40, def.MaxLevel);
        Assert.Equal(50, def.ParentMaxLevel);
        Assert.Equal(7, def.EvolutionQuest);
        Assert.Equal(902, def.EvolutionCreatureId);
        Assert.Equal(15, def.EvolutionLevel);
    }

    [Fact]
    public void ANameIsTheStringAfterTheTag()
    {
        Assert.Equal("Synthetic Pet", Read(new TokenStreamBuilder().Tag("[name]").Str("Synthetic Pet")).Name);
        Assert.Equal("Caf\u00e9", Read(new TokenStreamBuilder().Tag("[name]").Str("Caf\u00e9")).Name);
    }

    [Fact]
    public void ANameThatIsNotAStringKeepsTheDefaultButEatsTheToken()
    {
        // 读到整数当名字:读取失败、整数被消耗,名字保持缺省;后面的标签照常读。
        PetDef def = Read(new TokenStreamBuilder().Tag("[name]").Int(5).Tag("[max level]").Int(9), out PetScriptReport report);
        Assert.Equal(string.Empty, def.Name);
        Assert.Equal(9, def.MaxLevel);
        Assert.Equal(0, report.DiscardedTokens);
    }

    [Fact]
    public void AFloatInAnIntegerPositionTruncatesTowardZero()
    {
        PetDef def = Read(new TokenStreamBuilder().Tag("[max level]").Float(12.9f).Tag("[start level]").Float(-3.9f));
        Assert.Equal(12, def.MaxLevel);
        Assert.Equal(-3, def.StartLevel);
    }

    [Fact]
    public void AnotherTokenInAnIntegerPositionStoresZeroAndIsConsumed()
    {
        // 读到字符串:取 0 照存(不是保留缺省 1),字符串被消耗,下一个标签不受影响。
        PetDef def = Read(new TokenStreamBuilder().Tag("[max level]").Str("x").Tag("[start level]").Int(3));
        Assert.Equal(0, def.MaxLevel);
        Assert.Equal(3, def.StartLevel);
    }

    [Fact]
    public void ATagInAnIntegerPositionIsEatenAsTheValue()
    {
        // [max level] 后面直接是下一个标签:标签被当成"值"吃掉得 0,它自己的数字 3 成了游离标记(丢弃)。
        PetDef def = Read(new TokenStreamBuilder().Tag("[max level]").Tag("[start level]").Int(3), out PetScriptReport report);
        Assert.Equal(0, def.MaxLevel);
        Assert.Equal(1, def.StartLevel);
        Assert.Equal(1, report.DiscardedTokens);
    }

    [Fact]
    public void UnmodeledTagsAreSkippedAndTheirValuesCountAsDiscarded()
    {
        PetDef def = Read(new TokenStreamBuilder().Tag("[unmodeled thing]").Int(100).Tag("[name]").Str("A"), out PetScriptReport report);
        Assert.Equal("A", def.Name);
        Assert.Equal(1, report.DiscardedTokens);
    }

    [Fact]
    public void ATokenFileWithoutAnyTagStillGivesTheDefaults()
    {
        PetDef def = Read(new TokenStreamBuilder().Int(1).Int(2), out PetScriptReport report);
        Assert.Equal(1, def.MaxLevel);
        Assert.Equal(2, report.DiscardedTokens);
    }

    // ------------------------------------------------------------------ 神器格颜色

    [Fact]
    public void ArtifactSlotColorsAreRedBlueGreenBits()
    {
        Assert.Equal(PetArtifactColors.Red, Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("red").Tag("[/artifact slot]")).ArtifactColors);
        Assert.Equal(PetArtifactColors.Blue, Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("blue").Tag("[/artifact slot]")).ArtifactColors);
        Assert.Equal(PetArtifactColors.Green, Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("green").Tag("[/artifact slot]")).ArtifactColors);
        PetDef all = Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("red").Str("blue").Str("green").Tag("[/artifact slot]"));
        Assert.Equal(PetArtifactColors.Red | PetArtifactColors.Blue | PetArtifactColors.Green, all.ArtifactColors);
        Assert.Equal(7, (int)all.ArtifactColors);
        Assert.Equal(PetArtifactColors.None, Read(new TokenStreamBuilder().Tag("[artifact slot]").Tag("[/artifact slot]")).ArtifactColors);
    }

    [Fact]
    public void ArtifactSlotColorNamesAreCaseSensitiveAndUnknownNamesAreIgnored()
    {
        Assert.Equal(PetArtifactColors.None, Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("Red").Str("BLUE").Tag("[/artifact slot]")).ArtifactColors);
        // 不认得的字符串被忽略,循环继续读下一个。
        Assert.Equal(PetArtifactColors.Green, Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("yellow").Str("green").Tag("[/artifact slot]")).ArtifactColors);
        // 同一种颜色写两遍还是一位。
        Assert.Equal(PetArtifactColors.Red, Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("red").Str("red").Tag("[/artifact slot]")).ArtifactColors);
    }

    [Fact]
    public void TheColorLoopEndsAtTheFirstNonStringAndThatTokenIsConsumed()
    {
        // 正常写法:结束标签正好是"第一个非字符串",被循环吃掉,后面的标签照常读。
        PetDef closed = Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("red").Tag("[/artifact slot]").Tag("[max level]").Int(9));
        Assert.Equal(PetArtifactColors.Red, closed.ArtifactColors);
        Assert.Equal(9, closed.MaxLevel);

        // 没写结束标签:紧跟的 [max level] 被当成终止符吃掉,它的值 9 成了游离标记,等级上限保持缺省(原版的怪癖)。
        PetDef open = Read(new TokenStreamBuilder().Tag("[artifact slot]").Str("red").Tag("[max level]").Int(9), out PetScriptReport report);
        Assert.Equal(PetArtifactColors.Red, open.ArtifactColors);
        Assert.Equal(1, open.MaxLevel);
        Assert.Equal(1, report.DiscardedTokens);
    }

    [Fact]
    public void AKeyedStringCountsAsAString()
    {
        // "种类 + 键"对(字符串引用)按键取字符串,所以也能写出颜色名。
        PetDef def = Read(new TokenStreamBuilder().Tag("[artifact slot]").Keyed(1, "blue").Tag("[/artifact slot]"));
        Assert.Equal(PetArtifactColors.Blue, def.ArtifactColors);
    }

    [Theory]
    [InlineData(PetArtifactColors.None, false, false, false)]
    [InlineData(PetArtifactColors.Red, true, false, false)]
    [InlineData(PetArtifactColors.Blue, false, true, false)]
    [InlineData(PetArtifactColors.Green, false, false, true)]
    [InlineData(PetArtifactColors.Red | PetArtifactColors.Green, true, false, true)]
    [InlineData(PetArtifactColors.Red | PetArtifactColors.Blue | PetArtifactColors.Green, true, true, true)]
    public void ArtifactPartsOpenFollowTheColors(PetArtifactColors colors, bool red, bool blue, bool green)
    {
        var def = new PetDef(901, "creature/test/p901.cre") { ArtifactColors = colors };
        Assert.Equal(red, def.AllowsArtifactPart(EquipmentParts.ArtifactRed));
        Assert.Equal(blue, def.AllowsArtifactPart(EquipmentParts.ArtifactBlue));
        Assert.Equal(green, def.AllowsArtifactPart(EquipmentParts.ArtifactGreen));
        // 其它部位号一律不开放(宠物格自己、普通装备、越界)。
        Assert.False(def.AllowsArtifactPart(EquipmentParts.Creature));
        Assert.False(def.AllowsArtifactPart(EquipmentParts.MagicStone));
        Assert.False(def.AllowsArtifactPart(EquipmentParts.ArtifactGreen + 1));
        Assert.False(def.AllowsArtifactPart(-1));
    }

    // ------------------------------------------------------------------ 成长一节(块模式)

    [Fact]
    public void AGrowBlockIsCommittedAtItsCloseTag()
    {
        TokenStreamBuilder b = Grow(
            (PetScript.GrowLevelTag, 11), (PetScript.GrowATypeCreatureTag, 902), (PetScript.GrowBTypeCreatureTag, 903),
            (PetScript.GrowATypeItemTag, 4), (PetScript.GrowBTypeItemTag, 5), (PetScript.GrowItemsPerLevelTag, 12), (PetScript.GrowUsesExpItemTag, 1))
            .Tag(PetScript.GrowCloseTag);
        PetDef def = Read(b, out PetScriptReport report);
        Assert.True(def.Grow.UsesExpItem);
        Assert.Equal(11, def.Grow.SelectLevel);
        Assert.Equal(902, def.Grow.ATypeCreatureId);
        Assert.Equal(903, def.Grow.BTypeCreatureId);
        Assert.Equal(4, def.Grow.ATypeItemIndex);
        Assert.Equal(5, def.Grow.BTypeItemIndex);
        Assert.Equal(12, def.Grow.ItemsPerLevel);
        Assert.Equal(PetKind.Grow, def.Kind);
        Assert.Equal(1, report.GrowBlocks);
        Assert.Equal(1, report.GrowCommitted);
        Assert.Equal(0, report.GrowAbandoned);
        Assert.Equal(0, report.DiscardedTokens);
    }

    [Fact]
    public void AGrowBlockWithoutACloseTagIsNeverCommitted()
    {
        PetDef def = Read(Grow((PetScript.GrowLevelTag, 11), (PetScript.GrowItemsPerLevelTag, 12)), out PetScriptReport report);
        Assert.Equal(-1, def.Grow.SelectLevel);
        Assert.Equal(1, def.Grow.ItemsPerLevel);
        Assert.Equal(1, report.GrowBlocks);
        Assert.Equal(0, report.GrowCommitted);
        Assert.Equal(1, report.GrowAbandoned);
    }

    [Fact]
    public void AStrayTokenInsideTheBlockAbandonsItAndDropsTheTagFound()
    {
        // 块里 [grow level] 11 之后跟了游离整数 7:找下一个标签时丢弃了东西,块模式悄悄退出,
        // 刚找到的 [item count for levelup] 也跟着丢掉,它的值 5 又成了顶层的游离标记;之前读到的 11 随局部对象一起作废。
        TokenStreamBuilder b = new TokenStreamBuilder().Tag(PetScript.GrowOpenTag)
            .Tag(PetScript.GrowLevelTag).Int(11)
            .Int(7)
            .Tag(PetScript.GrowItemsPerLevelTag).Int(5)
            .Tag(PetScript.GrowCloseTag);
        PetDef def = Read(b, out PetScriptReport report);
        Assert.Equal(-1, def.Grow.SelectLevel);
        Assert.Equal(1, def.Grow.ItemsPerLevel);
        Assert.Equal(1, report.GrowBlocks);
        Assert.Equal(0, report.GrowCommitted);
        Assert.Equal(1, report.GrowAbandoned);
        Assert.Equal(2, report.DiscardedTokens);
    }

    [Fact]
    public void AnUnknownValuelessTagInsideTheBlockIsIgnored()
    {
        TokenStreamBuilder b = new TokenStreamBuilder().Tag(PetScript.GrowOpenTag)
            .Tag("[some other inner tag]")
            .Tag(PetScript.GrowLevelTag).Int(11)
            .Tag(PetScript.GrowCloseTag);
        PetDef def = Read(b, out PetScriptReport report);
        Assert.Equal(11, def.Grow.SelectLevel);
        Assert.Equal(1, report.GrowCommitted);
        Assert.Equal(0, report.GrowAbandoned);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public void UsingExpItemMeansNotEqualToZero(int value, bool expected)
    {
        PetDef def = Read(Grow((PetScript.GrowUsesExpItemTag, value)).Tag(PetScript.GrowCloseTag));
        Assert.Equal(expected, def.Grow.UsesExpItem);
    }

    [Fact]
    public void ALaterBlockReplacesTheWholeEarlierOneInsteadOfMerging()
    {
        TokenStreamBuilder b = Grow((PetScript.GrowLevelTag, 11), (PetScript.GrowItemsPerLevelTag, 7)).Tag(PetScript.GrowCloseTag)
            .Tag(PetScript.GrowOpenTag).Tag(PetScript.GrowUsesExpItemTag).Int(1).Tag(PetScript.GrowATypeItemTag).Int(3).Tag(PetScript.GrowCloseTag);
        PetDef def = Read(b, out PetScriptReport report);
        Assert.Equal(-1, def.Grow.SelectLevel);
        Assert.Equal(1, def.Grow.ItemsPerLevel);
        Assert.True(def.Grow.UsesExpItem);
        Assert.Equal(3, def.Grow.ATypeItemIndex);
        Assert.Equal(2, report.GrowBlocks);
        Assert.Equal(2, report.GrowCommitted);
    }

    [Fact]
    public void AnAbandonedBlockLeavesAnEarlierCommittedOneAlone()
    {
        TokenStreamBuilder b = Grow((PetScript.GrowLevelTag, 11)).Tag(PetScript.GrowCloseTag)
            .Tag(PetScript.GrowOpenTag).Tag(PetScript.GrowLevelTag).Int(99);
        PetDef def = Read(b, out PetScriptReport report);
        Assert.Equal(11, def.Grow.SelectLevel);
        Assert.Equal(2, report.GrowBlocks);
        Assert.Equal(1, report.GrowCommitted);
        Assert.Equal(1, report.GrowAbandoned);
    }

    [Fact]
    public void GrowTagsOutsideTheBlockAreNotTopLevelTags()
    {
        PetDef def = Read(new TokenStreamBuilder().Tag(PetScript.GrowLevelTag).Int(11).Tag(PetScript.GrowUsesExpItemTag).Int(1), out PetScriptReport report);
        Assert.Equal(-1, def.Grow.SelectLevel);
        Assert.False(def.Grow.UsesExpItem);
        Assert.Equal(0, report.GrowBlocks);
        Assert.Equal(2, report.DiscardedTokens);
    }

    [Fact]
    public void TopLevelTagsInsideTheBlockAreNotRead()
    {
        // 块里只认七个子标签:[max level] 在块里被忽略(它的值成了游离标记,块被放弃),顶层的等级上限保持缺省。
        TokenStreamBuilder b = new TokenStreamBuilder().Tag(PetScript.GrowOpenTag).Tag("[max level]").Int(9).Tag(PetScript.GrowCloseTag);
        PetDef def = Read(b, out PetScriptReport report);
        Assert.Equal(1, def.MaxLevel);
        Assert.Equal(1, report.GrowAbandoned);
    }

    // ------------------------------------------------------------------ 种类判断

    [Theory]
    [InlineData(false, -1, -1, PetKind.Normal)]
    [InlineData(true, -1, -1, PetKind.Normal)]
    [InlineData(true, 3, -1, PetKind.Grow)]
    [InlineData(true, -1, 3, PetKind.Grow)]
    [InlineData(true, 0, -1, PetKind.Grow)]
    [InlineData(true, -1, 0, PetKind.Grow)]
    [InlineData(false, 3, 4, PetKind.Normal)]
    public void TheKindFollowsTheItemIndexesAndTheExpItemFlag(bool usesExpItem, int aItem, int bItem, PetKind expected)
    {
        var def = new PetDef(901, "creature/test/p901.cre");
        def.Grow.UsesExpItem = usesExpItem;
        def.Grow.ATypeItemIndex = aItem;
        def.Grow.BTypeItemIndex = bItem;
        Assert.Equal(expected, def.Kind);
    }

    [Fact]
    public void CopyingAGrowInfoIsFieldForFieldAndIndependent()
    {
        var a = new PetGrowInfo
        {
            UsesExpItem = true, SelectLevel = 1, ATypeCreatureId = 2, BTypeCreatureId = 3, ATypeItemIndex = 4, BTypeItemIndex = 5, ItemsPerLevel = 6,
        };
        PetGrowInfo b = a.Copy();
        Assert.NotSame(a, b);
        Assert.True(b.UsesExpItem);
        Assert.Equal(1, b.SelectLevel);
        Assert.Equal(2, b.ATypeCreatureId);
        Assert.Equal(3, b.BTypeCreatureId);
        Assert.Equal(4, b.ATypeItemIndex);
        Assert.Equal(5, b.BTypeItemIndex);
        Assert.Equal(6, b.ItemsPerLevel);
        b.ItemsPerLevel = 60;
        Assert.Equal(6, a.ItemsPerLevel);
    }

    // ------------------------------------------------------------------ 字节入口

    [Fact]
    public void TheByteEntryReadsTheSameAsTheTokenEntryAndRejectsNonTokenFiles()
    {
        var b = new TokenStreamBuilder().Tag("[max level]").Int(40).Tag("[name]").Str("Byte Pet");
        var strings = new PvfStringTable(b.StringTableBytes());
        PetDef def = PetScript.Parse(b.Bytes(), strings, 902, "creature/test/p902.cre", out PetScriptReport report);
        Assert.NotNull(def);
        Assert.NotNull(report);
        Assert.Equal(902, def.Id);
        Assert.Equal(40, def.MaxLevel);
        Assert.Equal("Byte Pet", def.Name);

        Assert.Null(PetScript.Parse(new byte[] { 1, 2, 3, 4 }, strings, 902, "x", out PetScriptReport none));
        Assert.Null(none);
        Assert.Null(PetScript.Parse(ReadOnlySpan<byte>.Empty, strings, 902, "x", out _));
        Assert.Null(PetScript.Parse(b.Bytes(), null, 902, "x", out _));
    }

    [Fact]
    public void ANullPathBecomesAnEmptyOne()
    {
        (ScriptToken[] tokens, PvfStringTable strings) = new TokenStreamBuilder().Build();
        Assert.Equal(string.Empty, PetScript.Parse(tokens, strings, 903, null, out _).Path);
    }
}
