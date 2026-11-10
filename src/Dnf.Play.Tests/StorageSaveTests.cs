using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 仓库存档的文本格式(本项目自己的设计,没有原版证据):账号一个文件,与角色存档分开;只有数字与物品编号。
/// 期望的文本是手写的;每个范围的两端各有一个刚好合法与一个刚好越界的例子。
/// </summary>
public class StorageSaveTests
{
    private const string Header = "DNFUNITY-STORAGE 1\n";

    private static StorageSave Full()
    {
        var save = new StorageSave { AccountCapacity = 16, AccountGold = 1234 };
        save.AccountSlots.Add(new SavedVaultSlot(0, 900, 1));
        save.AccountSlots.Add(new SavedVaultSlot(3, 911, 55));
        var alpha = new SavedPersonalVault("alpha", 24);
        alpha.Slots.Add(new SavedVaultSlot(0, 901, 1));
        alpha.Slots.Add(new SavedVaultSlot(23, 912, 7));
        save.Personal.Add(alpha);
        save.Personal.Add(new SavedPersonalVault("beta", 8));
        return save;
    }

    private static StorageSave Parse(string text)
    {
        Assert.True(StorageSave.TryParse(text, out StorageSave save, out string error), error);
        Assert.Null(error);
        return save;
    }

    private static string Fail(string text)
    {
        Assert.False(StorageSave.TryParse(text, out StorageSave save, out string error));
        Assert.Null(save);
        Assert.False(string.IsNullOrEmpty(error));
        return error;
    }

    // ------------------------------------------------------------------ 写

    [Fact]
    public void TheTextLayoutIsFixed()
    {
        const string expected =
            "DNFUNITY-STORAGE 1\n" +
            "acap 16\n" +
            "agold 1234\n" +
            "aslot 0 900 1\n" +
            "aslot 3 911 55\n" +
            "pcap alpha 24\n" +
            "pslot alpha 0 901 1\n" +
            "pslot alpha 23 912 7\n" +
            "pcap beta 8\n";
        Assert.Equal(expected, Full().ToText());
        Assert.Equal("DNFUNITY-STORAGE", StorageSave.Magic);
        Assert.Equal(1, StorageSave.CurrentVersion);
    }

    [Fact]
    public void AnEmptySaveIsJustTheHeader()
    {
        Assert.Equal(Header, new StorageSave().ToText());
    }

    [Fact]
    public void NoAccountLinesAreWrittenWithoutAVault()
    {
        var save = new StorageSave { AccountCapacity = 0, AccountGold = 99 };
        save.AccountSlots.Add(new SavedVaultSlot(0, 900, 1));
        Assert.Equal(Header, save.ToText());
        save.AccountCapacity = -4;
        Assert.Equal(Header, save.ToText());
        save.AccountCapacity = 1;
        Assert.Equal(Header + "acap 1\nagold 99\naslot 0 900 1\n", save.ToText());
    }

    [Fact]
    public void TheVersionWrittenIsTheOneCarriedByTheSave()
    {
        Assert.StartsWith("DNFUNITY-STORAGE 1\n", new StorageSave().ToText());
        Assert.StartsWith("DNFUNITY-STORAGE 7\n", new StorageSave { Version = 7 }.ToText());
    }

    [Fact]
    public void NumbersAreWrittenWithoutCultureSeparators()
    {
        var save = new StorageSave { AccountCapacity = 2_000_000_000, AccountGold = long.MaxValue };
        Assert.Equal(Header + "acap 2000000000\nagold 9223372036854775807\n", save.ToText());
    }

    // ------------------------------------------------------------------ 来回

    [Fact]
    public void ARoundTripKeepsEverything()
    {
        StorageSave original = Full();
        StorageSave back = Parse(original.ToText());
        Assert.Equal(original.ToText(), back.ToText());
        Assert.Equal(16, back.AccountCapacity);
        Assert.Equal(1234, back.AccountGold);
        Assert.Equal(2, back.AccountSlots.Count);
        Assert.Equal(3, back.AccountSlots[1].Slot);
        Assert.Equal(911, back.AccountSlots[1].Code);
        Assert.Equal(55, back.AccountSlots[1].Count);
        Assert.Equal(new[] { "alpha", "beta" }, back.Personal.Select(v => v.Key).ToArray());
        Assert.Equal(24, back.Personal[0].Capacity);
        Assert.Equal(8, back.Personal[1].Capacity);
        Assert.Equal(2, back.Personal[0].Slots.Count);
        Assert.Equal(23, back.Personal[0].Slots[1].Slot);
        Assert.Empty(back.Personal[1].Slots);
    }

    [Fact]
    public void ALargeBalanceSurvivesARoundTrip()
    {
        var save = new StorageSave { AccountCapacity = int.MaxValue, AccountGold = long.MaxValue };
        StorageSave back = Parse(save.ToText());
        Assert.Equal(int.MaxValue, back.AccountCapacity);
        Assert.Equal(long.MaxValue, back.AccountGold);
    }

    [Fact]
    public void ParsingKeepsTheVersionOfTheFile()
    {
        Assert.Equal(1, Parse(Header).Version);
    }

    // ------------------------------------------------------------------ 读:宽容的部分

    [Fact]
    public void BlankLinesCommentsCarriageReturnsAndExtraSpacesAreFine()
    {
        string text =
            "\n# 注释\n  DNFUNITY-STORAGE   1  \r\n\r\n" +
            "# 另一行注释\r\n" +
            "acap  8 \r\n" +
            "agold 5\n" +
            "   aslot  0   900   1   \n" +
            "unknownkey 1 2 3\n" +
            "pcap  gamma   16\r\n";
        StorageSave save = Parse(text);
        Assert.Equal(8, save.AccountCapacity);
        Assert.Equal(5, save.AccountGold);
        Assert.Single(save.AccountSlots);
        Assert.Equal(900, save.AccountSlots[0].Code);
        Assert.Equal("gamma", save.Personal[0].Key);
        Assert.Equal(16, save.Personal[0].Capacity);
    }

    [Fact]
    public void UnknownKeysAreIgnoredEvenWithOddValues()
    {
        StorageSave save = Parse(Header + "future 1 2 3\nacapx not-a-number\nAcap 5\nacap 8\n");
        Assert.Equal(8, save.AccountCapacity);
    }

    [Fact]
    public void ASlotWithoutItsCapacityLineGetsTheInitialCapacity()
    {
        StorageSave save = Parse(Header + "pslot delta 2 900 1\n");
        Assert.Single(save.Personal);
        Assert.Equal("delta", save.Personal[0].Key);
        Assert.Equal(StorageRules.PersonalInitialCapacity, save.Personal[0].Capacity);
        Assert.Equal(2, save.Personal[0].Slots[0].Slot);
    }

    [Fact]
    public void PersonalVaultsComeOutSortedByKeyAndTheLastCapacityLineWins()
    {
        StorageSave save = Parse(Header + "pcap b 16\npslot a 0 900 1\npcap c 24\npcap b 32\npslot b 1 901 2\n");
        Assert.Equal(new[] { "a", "b", "c" }, save.Personal.Select(v => v.Key).ToArray());
        Assert.Equal(8, save.Personal[0].Capacity);
        Assert.Equal(32, save.Personal[1].Capacity);
        Assert.Equal(24, save.Personal[2].Capacity);
        Assert.Single(save.Personal[1].Slots);
    }

    [Fact]
    public void ADuplicateSlotLineIsKeptForTheRestoreToDrop()
    {
        StorageSave save = Parse(Header + "acap 8\naslot 1 900 1\naslot 1 901 1\n");
        Assert.Equal(2, save.AccountSlots.Count);
    }

    // ------------------------------------------------------------------ 读:不认的

    [Fact]
    public void AMissingOrWrongHeaderIsRefused()
    {
        Assert.Equal("没有内容", Fail(null));
        Assert.Equal("没有内容", Fail(""));
        Assert.Equal("没有内容", Fail("   \n# 只有注释\n\n"));
        Assert.Contains("第 1 行", Fail("DNFUNITY-HERO 1\n"));
        Assert.Contains("第 2 行", Fail("# 注释\nacap 8\nDNFUNITY-STORAGE 1\n"));
        Assert.Contains("不是仓库存档", Fail("acap 8\n"));
    }

    [Theory]
    [InlineData("DNFUNITY-STORAGE\n")]
    [InlineData("DNFUNITY-STORAGE x\n")]
    [InlineData("DNFUNITY-STORAGE 0\n")]
    [InlineData("DNFUNITY-STORAGE -1\n")]
    [InlineData("DNFUNITY-STORAGE 1 2\n")]
    [InlineData("DNFUNITY-STORAGE 1.5\n")]
    public void ABrokenVersionIsRefused(string text)
    {
        Assert.Contains("版本号写坏了", Fail(text));
    }

    [Fact]
    public void ANewerVersionIsRefused()
    {
        Assert.Contains("比这个程序认得的 1 新", Fail("DNFUNITY-STORAGE 2\n"));
        Assert.Contains("比这个程序认得的 1 新", Fail("DNFUNITY-STORAGE 2147483647\n"));
        Assert.Equal(1, Parse("DNFUNITY-STORAGE 1\n").Version);
    }

    [Theory]
    [InlineData("acap 8 9", "第 2 行", "acap 需要 1 个值")]
    [InlineData("acap", "第 2 行", "acap 需要 1 个值")]
    [InlineData("agold", "第 2 行", "agold 需要 1 个值")]
    [InlineData("aslot 0 900", "第 2 行", "aslot 需要 3 个值")]
    [InlineData("aslot 0 900 1 2", "第 2 行", "aslot 需要 3 个值")]
    [InlineData("pcap k", "第 2 行", "pcap 需要 2 个值")]
    [InlineData("pcap k 8 9", "第 2 行", "pcap 需要 2 个值")]
    [InlineData("pslot k 0 900", "第 2 行", "pslot 需要 4 个值")]
    [InlineData("pslot k 0 900 1 2", "第 2 行", "pslot 需要 4 个值")]
    public void AWrongNumberOfFieldsIsRefused(string line, string where, string what)
    {
        string error = Fail(Header + line + "\n");
        Assert.Contains(where, error);
        Assert.Contains(what, error);
    }

    [Theory]
    [InlineData("acap x")]
    [InlineData("acap 1.5")]
    [InlineData("acap 8.0")]                       // 带小数点的写法哪怕值是整数也不收
    [InlineData("acap 1e1")]                       // 指数写法也不收
    [InlineData("acap 99999999999999999999")]
    [InlineData("agold x")]
    [InlineData("agold 1e3")]
    [InlineData("agold 9223372036854775808")]
    [InlineData("aslot a 900 1")]
    [InlineData("aslot 0 b 1")]
    [InlineData("aslot 0 900 c")]
    [InlineData("pcap k x")]
    [InlineData("pslot k a 900 1")]
    [InlineData("pslot k 0 900 c")]
    public void ANonIntegerValueIsRefused(string line)
    {
        Assert.Contains("不是整数", Fail(Header + line + "\n"));
    }

    [Theory]
    [InlineData("acap -1")]
    [InlineData("acap 2147483648")]
    [InlineData("agold -1")]
    [InlineData("aslot -1 900 1")]
    [InlineData("aslot 56 900 1")]
    [InlineData("aslot 0 0 1")]
    [InlineData("aslot 0 -5 1")]
    [InlineData("aslot 0 900 0")]
    [InlineData("aslot 0 900 -1")]
    [InlineData("aslot 0 2147483648 1")]
    [InlineData("aslot 0 900 2147483648")]
    [InlineData("pcap k 0")]
    [InlineData("pcap k -8")]
    [InlineData("pcap k 153")]
    [InlineData("pslot k -1 900 1")]
    [InlineData("pslot k 152 900 1")]
    [InlineData("pslot k 0 0 1")]
    [InlineData("pslot k 0 900 0")]
    public void AValueOutOfRangeIsRefused(string line)
    {
        Assert.Contains("超出范围", Fail(Header + line + "\n"));
    }

    [Theory]
    [InlineData("acap 0")]
    [InlineData("acap 2147483647")]
    [InlineData("agold 0")]
    [InlineData("agold 9223372036854775807")]
    [InlineData("aslot 0 1 1")]
    [InlineData("aslot 55 2147483647 2147483647")]
    [InlineData("pcap k 1")]
    [InlineData("pcap k 152")]
    [InlineData("pslot k 0 1 1")]
    [InlineData("pslot k 151 2147483647 2147483647")]
    public void TheEdgesOfEveryRangeAreAccepted(string line)
    {
        Parse(Header + line + "\n");
    }

    [Theory]
    [InlineData("pcap #x 8")]
    [InlineData("pslot #x 0 900 1")]
    public void APersonalKeyThatCannotBeSavedIsRefused(string line)
    {
        Assert.Contains("角色键不合法", Fail(Header + line + "\n"));
    }

    [Fact]
    public void ALongKeyIsRefusedAtTheLimit()
    {
        Parse(Header + "pcap " + new string('k', 64) + " 8\n");
        Assert.Contains("角色键不合法", Fail(Header + "pcap " + new string('k', 65) + " 8\n"));
    }

    [Fact]
    public void ABadLineAnywhereRefusesTheWholeSaveAndNamesTheLine()
    {
        string text = Header + "acap 8\nagold 5\n\naslot 0 900 1\nagold oops\naslot 1 900 1\n";
        string error = Fail(text);
        Assert.Contains("第 6 行", error);
        Assert.Contains("agold", error);
    }

    [Fact]
    public void ALineSeparatedByTabsReadsAsOneUnknownKeyAndIsIgnored()
    {
        // 只按空格分词(同 HeroSave):写成制表符的行整行是一个不认得的键,被忽略;不会把半行当真
        StorageSave save = Parse(Header + "acap 8\naslot\t0\t900\t1\n");
        Assert.Equal(8, save.AccountCapacity);
        Assert.Empty(save.AccountSlots);
    }
}
