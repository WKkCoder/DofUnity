using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 仓库用到的三份脚本数据的读法(证据:服务端 importAccountCargoScript 0x088c7e80、AccountCargoScript::GetCreateInfo 0x088c8056 / GetCurrUpgradeInfo 0x088c80ba / GetNextUpgradeInfo 0x088c8124、
/// importGoldLimitByLevel 0x088fdb69、GetMoneyLimitPerLevel 0x08360ce0、ImportStackableScript 0x089aeec8 的几处标签)。数据全是合成的,只检验读法的形状。
/// </summary>
public class StorageScriptTests
{
    private static TokenStreamBuilder ValidAccountScript()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[required level]").Int(10);
        b.Tag("[required item]").Int(931);
        b.Tag("[upgrade info]").Ints(8, 1000, 2, 500, 16, 5000, 3, 1500, 24, 20000, 0, 0, 60, 99999, 5, 8000).Tag("[/upgrade info]");
        b.Tag("[account cargo end]");
        return b;
    }

    private static AccountCargoScript ParseAccount(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return AccountCargoScript.Parse(tokens, strings);
    }

    private static GoldLimitTable ParseLimits(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return GoldLimitTable.Parse(tokens, strings);
    }

    private static ScriptLine[] Lines(TokenStreamBuilder b)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ScriptDocument.Parse(b.Bytes(), strings);
    }

    // ------------------------------------------------------------------ 账号金库脚本

    [Fact]
    public void AccountScriptReadsRequirementsAndEveryTier()
    {
        AccountCargoScript script = ParseAccount(ValidAccountScript());
        Assert.NotNull(script);
        Assert.Equal(10, script.RequiredLevel);
        Assert.Equal(931, script.RequiredItem);
        Assert.Equal(4, script.Tiers.Count);
        Assert.Equal(new[] { 8, 16, 24, 60 }, script.Tiers.Select(t => t.Capacity));
        Assert.Equal(new[] { 1000, 5000, 20000, 99999 }, script.Tiers.Select(t => t.GoldLimit));
        Assert.Equal(new[] { 2, 3, 0, 5 }, script.Tiers.Select(t => t.MaterialCount));
        Assert.Equal(new[] { 500, 1500, 0, 8000 }, script.Tiers.Select(t => t.PriceGold));
    }

    [Fact]
    public void CreateIsTheSmallestCapacityAndCurrentIsAnExactLookup()
    {
        AccountCargoScript script = ParseAccount(ValidAccountScript());
        Assert.Equal(8, script.Create.Capacity);
        Assert.Equal(16, script.Current(16).Capacity);
        Assert.Equal(60, script.Current(60).Capacity);
        Assert.Null(script.Current(9));
        Assert.Null(script.Current(0));
        Assert.Null(script.Current(-8));
    }

    [Fact]
    public void NextIsTheTierRightAfterAnExistingCapacityAndNullOtherwise()
    {
        AccountCargoScript script = ParseAccount(ValidAccountScript());
        Assert.Equal(16, script.Next(8).Capacity);
        Assert.Equal(24, script.Next(16).Capacity);
        Assert.Equal(60, script.Next(24).Capacity);
        Assert.Null(script.Next(60));        // 最后一档没有下一档
        Assert.Null(script.Next(9));         // 不在表里的容量没有"下一档"(服务端是按键精确查找再往后一个)
        Assert.Null(script.Next(0));
        Assert.Null(script.Next(100));
    }

    [Fact]
    public void ADuplicateCapacityKeepsTheFirstRecord()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[upgrade info]").Ints(8, 1, 1, 1, 8, 2, 2, 2, 16, 3, 3, 3).Tag("[/upgrade info]").Tag("[account cargo end]");
        AccountCargoScript script = ParseAccount(b);
        Assert.Equal(2, script.Tiers.Count);
        Assert.Equal((1, 1, 1), (script.Current(8).GoldLimit, script.Current(8).MaterialCount, script.Current(8).PriceGold));
    }

    [Fact]
    public void TiersAreSortedByCapacityWhateverTheFileOrder()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[upgrade info]").Ints(60, 6, 6, 6, 8, 1, 1, 1, 16, 2, 2, 2).Tag("[/upgrade info]").Tag("[account cargo end]");
        AccountCargoScript script = ParseAccount(b);
        Assert.Equal(new[] { 8, 16, 60 }, script.Tiers.Select(t => t.Capacity));
        Assert.Equal(8, script.Create.Capacity);
        Assert.Equal(60, script.Next(16).Capacity);
    }

    [Fact]
    public void WithoutTheEndTagTheLoadFails()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[required level]").Int(10);
        b.Tag("[upgrade info]").Ints(8, 1000, 2, 500).Tag("[/upgrade info]");
        Assert.Null(ParseAccount(b));
        Assert.Null(ParseAccount(new TokenStreamBuilder()));
    }

    [Fact]
    public void ATierBlockWithoutAClosingMarkerSwallowsTheEndTag()
    {
        // 整数读到读不出为止,多读的那个标记已经被消耗:块后面没有收尾标记时 [account cargo end] 就是那个多读的标记。
        var b = new TokenStreamBuilder();
        b.Tag("[upgrade info]").Ints(8, 1000, 2, 500).Tag("[account cargo end]");
        Assert.Null(ParseAccount(b));
    }

    [Fact]
    public void AShortRecordShiftsTheStreamAndTheLoadFailsAsOnTheServer()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[upgrade info]").Ints(8, 1000, 2).Tag("[/upgrade info]").Tag("[account cargo end]");
        Assert.Null(ParseAccount(b));
    }

    [Fact]
    public void MissingRequirementsAreZeroAndNoTiersGivesAnEmptyTable()
    {
        AccountCargoScript script = ParseAccount(new TokenStreamBuilder().Tag("[account cargo end]"));
        Assert.NotNull(script);
        Assert.Equal(0, script.RequiredLevel);
        Assert.Equal(0, script.RequiredItem);
        Assert.Empty(script.Tiers);
        Assert.Null(script.Create);
        Assert.Null(script.Current(8));
        Assert.Null(script.Next(8));
    }

    [Fact]
    public void AnUnknownTagWithoutValuesIsIgnoredButAStrayValueStopsTheLoad()
    {
        var ok = new TokenStreamBuilder().Tag("[something else]").Tag("[required level]").Int(3).Tag("[account cargo end]");
        Assert.Equal(3, ParseAccount(ok).RequiredLevel);
        var stray = new TokenStreamBuilder().Tag("[something else]").Int(5).Tag("[required level]").Int(3).Tag("[account cargo end]");
        Assert.Null(ParseAccount(stray));
    }

    [Fact]
    public void AMissingRequirementValueIsZeroAndTheNextTagStillReads()
    {
        // [required level] 后面不是整数:读到的标记被消耗、等级按 0;所以紧跟着的标签被吃掉,这里用一个多余的标签垫着。
        var b = new TokenStreamBuilder().Tag("[required level]").Tag("[padding]").Tag("[required item]").Int(7).Tag("[account cargo end]");
        AccountCargoScript script = ParseAccount(b);
        Assert.Equal(0, script.RequiredLevel);
        Assert.Equal(7, script.RequiredItem);
    }

    [Fact]
    public void AccountScriptConstructorSortsKeepsTheFirstOfADuplicateAndSkipsNulls()
    {
        var script = new AccountCargoScript(1, 2, new[]
        {
            new AccountCargoTier(30, 3, 3, 3), null, new AccountCargoTier(10, 1, 1, 1), new AccountCargoTier(30, 9, 9, 9),
        });
        Assert.Equal(new[] { 10, 30 }, script.Tiers.Select(t => t.Capacity));
        Assert.Equal(3, script.Current(30).GoldLimit);
        Assert.Empty(new AccountCargoScript(0, 0, null).Tiers);
    }

    [Fact]
    public void AccountScriptParsesRawBytesOnlyForATokenScriptWithStrings()
    {
        TokenStreamBuilder b = ValidAccountScript();
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        Assert.NotNull(AccountCargoScript.Parse(b.Bytes(), strings));
        Assert.Null(AccountCargoScript.Parse(new byte[] { 1, 2, 3, 4 }, strings));
        Assert.Null(AccountCargoScript.Parse(b.Bytes(), null));
    }

    [Fact]
    public void AccountScriptIsReadFromItsPathAndFailureGivesNull()
    {
        TokenStreamBuilder b = ValidAccountScript();
        var source = new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes()).AddBytes(AccountCargoScript.Path, b.Bytes());
        AccountCargoScript script = AccountCargoScript.Read(new GameData(source));
        Assert.Equal(4, script.Tiers.Count);

        Assert.Null(AccountCargoScript.Read(new GameData(new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes()))));
        Assert.Null(AccountCargoScript.Read(new GameData(new MemoryScriptSource().AddBytes(AccountCargoScript.Path, b.Bytes()))));
        var truncated = new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes())
            .AddBytes(AccountCargoScript.Path, new byte[] { TokenScript.Magic0, TokenScript.Magic1, 5, 0 });
        Assert.Null(AccountCargoScript.Read(new GameData(truncated)));
        Assert.Throws<ArgumentNullException>(() => AccountCargoScript.Read(null));
        Assert.Equal("Etc/AccountCargo.etc", AccountCargoScript.Path);
    }

    // ------------------------------------------------------------------ 每级金币上限

    [Fact]
    public void GoldLimitsReadLevelValuePairsAndUnwrittenLevelsAreZero()
    {
        var b = new TokenStreamBuilder().Tag("[gold limit from level]").Ints(1, 100, 2, 250, 99, 5000).Tag("[/gold limit from level]");
        GoldLimitTable table = ParseLimits(b);
        Assert.Equal(100, table.Entry(1));
        Assert.Equal(250, table.Entry(2));
        Assert.Equal(5000, table.Entry(99));
        Assert.Equal(0, table.Entry(0));
        Assert.Equal(0, table.Entry(3));
    }

    [Fact]
    public void GoldLimitPairsOutsideTheTableAreConsumedButNotStored()
    {
        var b = new TokenStreamBuilder().Tag("[gold limit from level]").Ints(100, 7, -1, 8, 5, 55, 1000, 9).Tag("[/gold limit from level]");
        GoldLimitTable table = ParseLimits(b);
        Assert.Equal(55, table.Entry(5));
        Assert.Equal(0, table.Entry(0));
        Assert.Equal(0, table.Entry(99));
    }

    [Fact]
    public void AtMostAHundredPairsAreRead()
    {
        var b = new TokenStreamBuilder().Tag("[gold limit from level]");
        for (int level = 0; level < 100; level++)
        {
            b.Ints(level, level + 1000);
        }
        b.Ints(5, 777).Tag("[/gold limit from level]");
        GoldLimitTable table = ParseLimits(b);
        Assert.Equal(1005, table.Entry(5));       // 第 101 对(5, 777)不读
        Assert.Equal(1099, table.Entry(99));
    }

    [Fact]
    public void GoldLimitReadingStopsAtTheFirstValueThatIsNotAnInteger()
    {
        // 读"等级 上限"对子时等级的位置读不出整数就整节停(服务端的循环在这里 break),后面的整数不再当对子读;
        // 游离的标签被当成那个读不出的值吃掉,外层扫描再丢掉后面的数值直到下一个标签,所以 (2, 20) 读不到。
        var b = new TokenStreamBuilder().Tag("[gold limit from level]").Ints(1, 10).Tag("[stray]").Ints(2, 20).Tag("[/gold limit from level]");
        GoldLimitTable table = ParseLimits(b);
        Assert.Equal(10, table.Entry(1));
        Assert.Equal(0, table.Entry(2));
    }

    [Fact]
    public void GoldLimitTagMayAppearSeveralTimesAndOtherTagsWithoutValuesAreSkipped()
    {
        var b = new TokenStreamBuilder().Tag("[other]").Tag("[gold limit from level]").Ints(1, 10).Tag("[/gold limit from level]")
            .Tag("[gold limit from level]").Ints(2, 20).Tag("[/gold limit from level]");
        GoldLimitTable table = ParseLimits(b);
        Assert.Equal(10, table.Entry(1));
        Assert.Equal(20, table.Entry(2));
    }

    [Theory]
    [InlineData(-5, 1000)]      // 等级先夹到 1 到 200
    [InlineData(0, 1000)]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(99, 99000)]
    [InlineData(100, 99000)]    // 表只有 100 项,超出的取表尾(服务端会读到表外的内存,没有查明)
    [InlineData(200, 99000)]
    [InlineData(201, 99000)]
    [InlineData(100000, 99000)]
    public void LimitOfClampsTheLevelAndTakesTheLastEntryBeyondTheTable(int level, int expected)
    {
        Assert.Equal((long)expected, StorageFixtures.Limits().LimitOf(level));
    }

    [Fact]
    public void GoldLimitConstructorKeepsAtMostAHundredAndPadsWithZero()
    {
        var many = new int[150];
        for (int i = 0; i < many.Length; i++)
        {
            many[i] = i + 1;
        }
        var table = new GoldLimitTable(many);
        Assert.Equal(1, table.Entry(0));
        Assert.Equal(100, table.Entry(99));
        Assert.Equal(0, table.Entry(100));
        Assert.Equal(0, table.Entry(-1));
        Assert.Equal(0, new GoldLimitTable(5, 6).Entry(2));
        Assert.Equal(6, new GoldLimitTable(5, 6).Entry(1));
        Assert.Equal(0, new GoldLimitTable(null).Entry(0));
        Assert.Equal(1, GoldLimitTable.MinLevel);
        Assert.Equal(200, GoldLimitTable.MaxLevel);
        Assert.Equal(100, GoldLimitTable.Entries);
    }

    [Fact]
    public void GoldLimitsParseRawBytesAndReadFromThePath()
    {
        var b = new TokenStreamBuilder().Tag("[gold limit from level]").Ints(3, 33).Tag("[/gold limit from level]");
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        Assert.Equal(33, GoldLimitTable.Parse(b.Bytes(), strings).Entry(3));
        Assert.Null(GoldLimitTable.Parse(new byte[] { 9, 9 }, strings));
        Assert.Null(GoldLimitTable.Parse(b.Bytes(), null));

        var source = new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes()).AddBytes(GoldLimitTable.Path, b.Bytes());
        Assert.Equal(33, GoldLimitTable.Read(new GameData(source)).Entry(3));
        Assert.Null(GoldLimitTable.Read(new GameData(new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes()))));
        Assert.Null(GoldLimitTable.Read(new GameData(new MemoryScriptSource().AddBytes(GoldLimitTable.Path, b.Bytes()))));
        var truncated = new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes())
            .AddBytes(GoldLimitTable.Path, new byte[] { TokenScript.Magic0, TokenScript.Magic1, 5, 0 });
        Assert.Null(GoldLimitTable.Read(new GameData(truncated)));
        Assert.Throws<ArgumentNullException>(() => GoldLimitTable.Read(null));
        Assert.Equal("Etc/GoldLimitByLevel.etc", GoldLimitTable.Path);
    }

    // ------------------------------------------------------------------ 物品脚本里的三个标签

    [Fact]
    public void PackagableIsTrueWhenTheFirstIntegerIsNotZero()
    {
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]").Int(1))).Packagable);
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]").Int(7))).Packagable);
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]").Int(-1))).Packagable);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]").Int(0))).Packagable);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]"))).Packagable);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[price]").Int(5))).Packagable);
    }

    [Fact]
    public void ARepeatedPackagableTagTakesTheLastOne()
    {
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]").Int(1).Tag("[packagable]").Int(0))).Packagable);
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[packagable]").Int(0).Tag("[packagable]").Int(1))).Packagable);
    }

    [Fact]
    public void ImpossibleContentsFlagsOnlyTheCharacCargoWord()
    {
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]").Str("charac cargo"))).ImpossibleCharacCargo);
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]").Str("trade").Str("charac cargo"))).ImpossibleCharacCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]").Str("trade").Str("mail"))).ImpossibleCharacCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]"))).ImpossibleCharacCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]").Str("Charac Cargo"))).ImpossibleCharacCargo);
    }

    [Fact]
    public void ImpossibleContentsStopsAtTheFirstNonStringAndTheFlagNeverClears()
    {
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]").Str("trade").Int(5).Str("charac cargo"))).ImpossibleCharacCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[impossible contents]").Keyed(1, "charac cargo"))).ImpossibleCharacCargo);   // 带键的值不是字符串,哪怕文字一样
        var b = new TokenStreamBuilder().Tag("[impossible contents]").Str("charac cargo").Tag("[impossible contents]").Str("trade");
        Assert.True(StorageItemScript.FromScript(Lines(b)).ImpossibleCharacCargo);
    }

    [Fact]
    public void ActionTypeIsTheUpgradeCargoActionOnlyWhenItIsTheFirstValueAndTheLastTag()
    {
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[action type]").Str("[upgrade cargo]"))).UpgradesCargo);
        Assert.True(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[action type]").Str("[upgrade cargo]").Int(3))).UpgradesCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[action type]").Str("[other action]"))).UpgradesCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[action type]").Int(5).Str("[upgrade cargo]"))).UpgradesCargo);
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[action type]").Keyed(1, "[upgrade cargo]"))).UpgradesCargo);   // 带键的值不是字符串,哪怕文字一样
        Assert.False(StorageItemScript.FromScript(Lines(new TokenStreamBuilder().Tag("[action type]"))).UpgradesCargo);
        var last = new TokenStreamBuilder().Tag("[action type]").Str("[upgrade cargo]").Tag("[action type]").Str("[other action]");
        Assert.False(StorageItemScript.FromScript(Lines(last)).UpgradesCargo);
    }

    [Fact]
    public void ItemFactsAreIndependentAndEmptyScriptsGiveNone()
    {
        var b = new TokenStreamBuilder().Tag("[packagable]").Int(1).Tag("[action type]").Str("[upgrade cargo]");
        StorageItemFacts facts = StorageItemScript.FromScript(Lines(b));
        Assert.True(facts.Packagable);
        Assert.False(facts.ImpossibleCharacCargo);
        Assert.True(facts.UpgradesCargo);
        Assert.Equal(StorageItemFacts.None.Packagable, StorageItemScript.FromScript(null).Packagable);
        Assert.False(StorageItemScript.FromScript(null).UpgradesCargo);
        Assert.False(StorageItemScript.FromScript(null).ImpossibleCharacCargo);
        Assert.False(StorageItemScript.FromScript(new ScriptLine[0]).Packagable);
    }

    [Fact]
    public void ItemFactsAreReadFromTheItemScriptPath()
    {
        ItemInfo item = StorageFixtures.Stack(911);
        var b = new TokenStreamBuilder().Tag("[packagable]").Int(1).Tag("[impossible contents]").Str("charac cargo");
        var source = new MemoryScriptSource().AddBytes(GameData.StringTablePath, b.StringTableBytes()).AddBytes(item.Path, b.Bytes());
        var data = new GameData(source);
        StorageItemFacts facts = StorageItemScript.Read(data, item);
        Assert.True(facts.Packagable);
        Assert.True(facts.ImpossibleCharacCargo);
        Assert.False(facts.UpgradesCargo);
        Assert.False(StorageItemScript.Read(data, StorageFixtures.Stack(912)).Packagable);   // 读不到脚本
        Assert.False(StorageItemScript.Read(data, null).Packagable);
        Assert.Throws<ArgumentNullException>(() => StorageItemScript.Read(null, item));
    }

    [Fact]
    public void SettingsFromDataLoadBothTablesAndTheItemFacts()
    {
        ItemInfo item = StorageFixtures.Stack(911);
        var pool = new List<string>();                // 同一个字符串池:三份脚本共用一份 stringtable.bin
        var a = new TokenStreamBuilder(pool);
        a.Tag("[required level]").Int(4).Tag("[upgrade info]").Ints(8, 100, 0, 0).Tag("[/upgrade info]").Tag("[account cargo end]");
        var g = new TokenStreamBuilder(pool);
        g.Tag("[gold limit from level]").Ints(1, 111).Tag("[/gold limit from level]");
        var i = new TokenStreamBuilder(pool);
        i.Tag("[packagable]").Int(1);
        var source = new MemoryScriptSource().AddBytes(GameData.StringTablePath, a.StringTableBytes())
            .AddBytes(AccountCargoScript.Path, a.Bytes()).AddBytes(GoldLimitTable.Path, g.Bytes()).AddBytes(item.Path, i.Bytes());
        StorageSettings settings = StorageSettings.FromData(new GameData(source));
        Assert.Equal(4, settings.AccountScript.RequiredLevel);
        Assert.Equal(111, settings.GoldLimits.LimitOf(1));
        Assert.True(settings.FactsOf(item).Packagable);
        Assert.False(settings.FactsOf(StorageFixtures.Stack(912)).Packagable);

        StorageSettings empty = StorageSettings.FromData(new GameData(new MemoryScriptSource()));
        Assert.Null(empty.AccountScript);
        Assert.Null(empty.GoldLimits);
        Assert.False(empty.FactsOf(item).Packagable);
        Assert.Throws<ArgumentNullException>(() => StorageSettings.FromData(null));
    }

    [Fact]
    public void SettingsExcludedCodeRangeIsInclusiveAndEmptyByDefault()
    {
        var settings = new StorageSettings();
        Assert.False(settings.IsExcludedCode(0));
        Assert.False(settings.IsExcludedCode(1));
        Assert.False(settings.IsExcludedCode(int.MaxValue));
        Assert.True(settings.RequirePackagable);
        settings.ExcludedCodeFrom = 920;
        settings.ExcludedCodeTo = 925;
        Assert.False(settings.IsExcludedCode(919));
        Assert.True(settings.IsExcludedCode(920));
        Assert.True(settings.IsExcludedCode(922));
        Assert.True(settings.IsExcludedCode(925));
        Assert.False(settings.IsExcludedCode(926));
        Assert.Equal(StorageItemFacts.None.Packagable, settings.FactsOf(null).Packagable);
        Assert.False(settings.FactsOf(StorageFixtures.Stack(911)).Packagable);
    }

    [Fact]
    public void FactsOfAMissingItemIsNoneEvenWhenTheLookupExists()
    {
        var known = new Dictionary<int, StorageItemFacts> { [911] = new StorageItemFacts(true, true, true) };
        StorageSettings settings = StorageFixtures.Settings(known);
        StorageItemFacts none = settings.FactsOf(null);
        Assert.False(none.Packagable);
        Assert.False(none.ImpossibleCharacCargo);
        Assert.False(none.UpgradesCargo);
        StorageItemFacts found = settings.FactsOf(StorageFixtures.Stack(911));
        Assert.True(found.Packagable);
        Assert.True(found.ImpossibleCharacCargo);
        Assert.True(found.UpgradesCargo);
    }
}
