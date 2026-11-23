using Dnf.Data;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 频道项第二行的固定文字(客户端函数 0x010f6560,台账 E-442/E-445):类型 2 的频道与 <c>[granfloris]</c> 区域画"数字编号文字表"里的固定文字,
/// 类型 3 什么也不画,其余照旧画推荐等级区间。文字、可执行文件、脚本、频道全是合成的;只有接口常量(编号 0x5362)是真的。
/// </summary>
public sealed class ChannelFixedLevelTextTests
{
    // 接口常量:客户端函数 0x010f6560 取文字用的编号。测试里的其余编号与文字都是合成的
    private const int FixedId = 0x5362;

    /// <summary>记下被问过哪些编号的解析器;只认 <see cref="FixedId"/>,返回 <see cref="Text"/>。</summary>
    private sealed class Resolver
    {
        public readonly List<int> Calls = new List<int>();

        public string Text = "SYNTH-NOTE";

        public string Lookup(int id)
        {
            Calls.Add(id);
            return id == FixedId ? Text : null;
        }
    }

    /// <summary>一个带槽位规则的频道条目:<paramref name="kind"/> 是规则的类型,<paramref name="areaTag"/> 是区域标签,<paramref name="levelFlag"/> 决定是否带推荐等级标志。</summary>
    private static ChannelEntry EntryOf(int kind, string areaTag, int? min = 41, int? max = 55, bool levelFlag = true, int number = 11)
    {
        var rule = new ChannelServerSlotRule(3, number, -1, string.Empty, "syn", kind, areaTag);
        ChannelEntryFlags flags = ChannelEntryFlags.HasValidPort;
        if (levelFlag)
        {
            flags |= ChannelEntryFlags.HasRecommendLevel;
        }
        var evaluation = new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u);
        return new ChannelEntry("siroco", number, "#ch." + number, "#ch.", 200, 10, string.Empty, 31000 + number, evaluation, flags, min, max, number, rule);
    }

    private static void AssertSameChannel(DirectoryChannel expected, DirectoryChannel actual)
    {
        Assert.Equal(expected.Number, actual.Number);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Congestion, actual.Congestion);
        Assert.Equal(expected.Target, actual.Target);
        Assert.Equal(expected.RecommendedMinLevel, actual.RecommendedMinLevel);
        Assert.Equal(expected.RecommendedMaxLevel, actual.RecommendedMaxLevel);
        Assert.Equal(expected.HasRecommendedLevel, actual.HasRecommendedLevel);
        Assert.Equal(expected.SlotFrame, actual.SlotFrame);
        Assert.Equal(expected.FixedLevelText, actual.FixedLevelText);
    }

    // ------------------------------------------------------------------ [granfloris] 区域

    [Fact]
    public void The_interface_constant_is_the_original_text_id()
    {
        Assert.Equal(0x5362, ChannelDirectoryAdapter.FixedLevelTextId);
    }

    [Fact]
    public void A_granfloris_channel_with_a_recommended_level_gets_the_fixed_text()
    {
        var resolver = new Resolver();

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(0, "[granfloris]"), null, resolver.Lookup);

        Assert.Equal("SYNTH-NOTE", mapped.FixedLevelText);
        Assert.Equal(new[] { FixedId }, resolver.Calls);                 // 恰好问了一次,问的是这个编号
        Assert.True(mapped.HasRecommendedLevel);                          // 区间本身不动(没有汉字字体时第二行还是画它)
        Assert.Equal((41, 55), (mapped.RecommendedMinLevel, mapped.RecommendedMaxLevel));
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(mapped));       // 画出来的就是这句,不是数值区间
    }

    [Theory]
    [InlineData("[GranFloris]")]
    [InlineData("[GRANFLORIS]")]
    [InlineData("[Granfloris]")]
    [InlineData(" [granfloris] ")]
    [InlineData("\t[GranFloris]\r\n")]
    public void The_area_tag_is_trimmed_and_compared_without_regard_to_case(string tag)
    {
        var resolver = new Resolver();

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(0, tag), null, resolver.Lookup);

        Assert.Equal("SYNTH-NOTE", mapped.FixedLevelText);
        Assert.Equal(new[] { FixedId }, resolver.Calls);
    }

    [Theory]
    [InlineData("[none]")]
    [InlineData("[deathtower]")]
    [InlineData("[sky_catle]")]
    [InlineData("[granflorisx]")]
    [InlineData("[granfloris2]")]
    [InlineData("[gran floris]")]
    [InlineData("[granfloris")]
    [InlineData("granfloris]")]
    [InlineData("granfloris")]
    [InlineData("[granfloris][none]")]
    [InlineData("")]
    public void Any_other_area_tag_keeps_the_numeric_range_and_the_resolver_is_not_asked(string tag)
    {
        var resolver = new Resolver();
        ChannelEntry entry = EntryOf(0, tag);

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(entry, null, resolver.Lookup);

        Assert.Null(mapped.FixedLevelText);
        Assert.Empty(resolver.Calls);
        AssertSameChannel(ChannelDirectoryAdapter.Channel(entry), mapped);
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(mapped));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void The_granfloris_rule_does_not_depend_on_the_other_channel_kinds(int kind)
    {
        // 类型 2 与 3 之外的类型都走"推荐等级可用 + 区域标签"这一条
        var resolver = new Resolver();

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(kind, "[granfloris]"), null, resolver.Lookup);

        Assert.Equal("SYNTH-NOTE", mapped.FixedLevelText);
    }

    [Theory]
    [InlineData(false, 41, 55)]          // 没有推荐等级标志(数值再齐也不算)
    [InlineData(true, null, 55)]         // 标志在,下限没取到
    [InlineData(true, 41, null)]         // 标志在,上限没取到
    [InlineData(true, null, null)]
    [InlineData(false, null, null)]
    public void A_granfloris_channel_without_a_usable_recommended_level_gets_no_fixed_text(bool levelFlag, int? min, int? max)
    {
        var resolver = new Resolver();
        ChannelEntry entry = EntryOf(0, "[granfloris]", min, max, levelFlag);

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(entry, null, resolver.Lookup);

        Assert.Null(mapped.FixedLevelText);
        Assert.Empty(resolver.Calls);
        AssertSameChannel(ChannelDirectoryAdapter.Channel(entry), mapped);
    }

    [Fact]
    public void A_zero_range_that_was_read_still_counts_as_a_usable_recommended_level()
    {
        // "可用"按现有取法:带标志且下限上限都取到了值。0~0 取到了值,所以固定文字照给(HasRecommendedLevel 对 0~0 为假,与固定文字无关)
        var resolver = new Resolver();

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(0, "[granfloris]", 0, 0), null, resolver.Lookup);

        Assert.False(mapped.HasRecommendedLevel);
        Assert.Equal("SYNTH-NOTE", mapped.FixedLevelText);
    }

    // ------------------------------------------------------------------ 类型 2

    [Theory]
    [InlineData("[none]", true, 41, 55)]
    [InlineData("[none]", false, null, null)]               // 没有推荐等级也有
    [InlineData("[none]", true, null, null)]
    [InlineData("[granfloris]", true, 1, 20)]
    [InlineData("[granfloris]", false, null, null)]
    [InlineData("[unknown_zone]", true, 1, 20)]
    [InlineData("", false, null, null)]
    public void A_type_2_channel_gets_the_fixed_text_whatever_its_tag_and_even_without_a_recommended_level(string tag, bool levelFlag, int? min, int? max)
    {
        var resolver = new Resolver();

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(2, tag, min, max, levelFlag), null, resolver.Lookup);

        Assert.Equal("SYNTH-NOTE", mapped.FixedLevelText);
        Assert.Equal(new[] { FixedId }, resolver.Calls);
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(mapped));
    }

    [Fact]
    public void A_type_2_channel_without_a_resolver_draws_nothing_as_before()
    {
        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(2, "[none]", null, null, false));

        Assert.Null(mapped.FixedLevelText);
        Assert.False(mapped.HasRecommendedLevel);
        Assert.Equal(string.Empty, ServerPickerText.Level(mapped));
    }

    // ------------------------------------------------------------------ 类型 3

    [Theory]
    [InlineData("[granfloris]", true, 1, 20)]
    [InlineData("[none]", true, 41, 55)]
    [InlineData("[none]", false, null, null)]
    [InlineData("[granfloris]", false, null, null)]
    public void A_type_3_channel_never_draws_a_second_line(string tag, bool levelFlag, int? min, int? max)
    {
        var resolver = new Resolver();

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(3, tag, min, max, levelFlag), null, resolver.Lookup);

        Assert.Null(mapped.FixedLevelText);
        Assert.False(mapped.HasRecommendedLevel);
        Assert.Equal((0, 0), (mapped.RecommendedMinLevel, mapped.RecommendedMaxLevel));
        Assert.Empty(resolver.Calls);
        Assert.Equal(string.Empty, ServerPickerText.Level(mapped));
    }

    // ------------------------------------------------------------------ 没有解析器或解析不出来:与今天一模一样

    public static IEnumerable<object[]> EveryRuleShape()
    {
        yield return new object[] { 0, "[granfloris]", true };
        yield return new object[] { 0, "[GranFloris]", true };
        yield return new object[] { 0, "[none]", true };
        yield return new object[] { 0, "[none]", false };
        yield return new object[] { 2, "[none]", false };
        yield return new object[] { 2, "[granfloris]", true };
        yield return new object[] { 3, "[granfloris]", true };
        yield return new object[] { 11, "[granfloris]", true };
    }

    [Theory]
    [MemberData(nameof(EveryRuleShape))]
    public void Without_a_usable_resolver_every_channel_maps_exactly_as_before(int kind, string tag, bool withLevel)
    {
        ChannelEntry entry = EntryOf(kind, tag, withLevel ? 41 : (int?)null, withLevel ? 55 : (int?)null, withLevel);
        DirectoryChannel baseline = ChannelDirectoryAdapter.Channel(entry);
        Assert.Null(baseline.FixedLevelText);

        var resolvers = new Func<int, string>[]
        {
            null,
            id => null,
            id => string.Empty,
            id => " ",
            id => "\t \r\n",
            id => "\u3000",                              // 全角空格也是空白
        };
        foreach (Func<int, string> resolver in resolvers)
        {
            DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(entry, null, resolver);
            Assert.Null(mapped.FixedLevelText);
            AssertSameChannel(baseline, mapped);
        }
    }

    [Fact]
    public void A_resolver_text_is_taken_verbatim_without_trimming_or_rewriting()
    {
        var resolver = new Resolver { Text = "  合成备注 a>b\\nc  " };

        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(EntryOf(0, "[granfloris]"), null, resolver.Lookup);

        Assert.Equal("  合成备注 a>b\\nc  ", mapped.FixedLevelText);
    }

    // ------------------------------------------------------------------ 整份快照:Map / Result

    /// <summary>
    /// 一份带槽位规则的快照:<c>[granfloris]</c> 的推荐等级 1~20、<c>[deathtower]</c> 的 20~70;
    /// 频道 11(类型 0,granfloris,主网格)、12(类型 0,deathtower,主网格)、13(类型 0,none,主网格,没有推荐等级)、
    /// 1(类型 11,granfloris,塔栏)、6(类型 3,granfloris,交易栏,等级被压掉)。
    /// </summary>
    private static ChannelDirectorySnapshot Snapshot()
    {
        const string scriptText =
            "[channel] `[granfloris]` 1 20\n" +
            "[channel] `[deathtower]` 20 70\n" +
            "[server] 3\n" +
            "  1 <4::cname_1> 11 `[granfloris]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  6 <4::cname_6> 3 `[granfloris]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  11 <4::cname_11> 0 `[granfloris]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  12 <4::cname_12> 0 `[deathtower]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  13 <4::cname_13> 0 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n";
        ChannelScriptData script = ChannelScriptParser.Parse(scriptText);
        return ChannelDirectorySnapshot.Build(
            "v1",
            false,
            script,
            new[]
            {
                new ChannelWireGroup("siroco", new[]
                {
                    new ChannelWireEntry("#ch.6", 200, 10, "", 19006),
                    new ChannelWireEntry("#ch.1", 200, 10, "", 19001),
                    new ChannelWireEntry("#ch.11", 200, 10, "", 19011),
                    new ChannelWireEntry("#ch.12", 200, 10, "", 19012),
                    new ChannelWireEntry("#ch.13", 200, 10, "", 19013),
                }),
            });
    }

    private static void AssertSnapshotCarriesTheFixedText(ServerDirectory directory, string expected)
    {
        DirectoryServer siroco = Assert.Single(directory.Servers);
        Assert.Equal(new[] { 11, 12, 13 }, siroco.Channels.Select(c => c.Number).ToArray());
        Assert.Equal(new[] { 1, 6 }, siroco.SpecialChannels.Select(c => c.Number).ToArray());
        // 主网格:只有 granfloris 且带推荐等级的那个有固定文字,别的区域标签与没有推荐等级的都没有
        Assert.Equal(new[] { expected, null, null }, siroco.Channels.Select(c => c.FixedLevelText).ToArray());
        // 特殊栏:塔栏(类型 11)里 granfloris 的有;交易栏(类型 3)的没有
        Assert.Equal(new[] { expected, null }, siroco.SpecialChannels.Select(c => c.FixedLevelText).ToArray());
        // 数值区间都还在(类型 3 的被压掉)
        Assert.Equal((1, 20), (siroco.Channels[0].RecommendedMinLevel, siroco.Channels[0].RecommendedMaxLevel));
        Assert.Equal((20, 70), (siroco.Channels[1].RecommendedMinLevel, siroco.Channels[1].RecommendedMaxLevel));
        Assert.False(siroco.Channels[2].HasRecommendedLevel);
        Assert.Equal((1, 20), (siroco.SpecialChannels[0].RecommendedMinLevel, siroco.SpecialChannels[0].RecommendedMaxLevel));
        Assert.False(siroco.SpecialChannels[1].HasRecommendedLevel);
    }

    [Fact]
    public void Map_carries_the_fixed_text_for_the_main_grid_and_the_tower_row()
    {
        var resolver = new Resolver();

        ServerDirectory directory = ChannelDirectoryAdapter.Map(Snapshot(), null, resolver.Lookup);

        AssertSnapshotCarriesTheFixedText(directory, "SYNTH-NOTE");
        Assert.Equal(new[] { FixedId, FixedId }, resolver.Calls);          // 只有这两个频道问过
    }

    [Fact]
    public void Result_carries_the_fixed_text_through_a_successful_directory()
    {
        var resolver = new Resolver();

        ServerDirectoryResult result = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.Ok, Snapshot(), null, resolver.Lookup);

        Assert.True(result.Success);
        AssertSnapshotCarriesTheFixedText(result.Directory, "SYNTH-NOTE");
    }

    [Fact]
    public void Map_and_Result_without_a_resolver_are_the_same_as_before()
    {
        ServerDirectory plain = ChannelDirectoryAdapter.Map(Snapshot());
        ServerDirectory nullResolver = ChannelDirectoryAdapter.Map(Snapshot(), null, null);
        ServerDirectoryResult viaResult = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.Ok, Snapshot(), null, null);

        foreach (ServerDirectory directory in new[] { plain, nullResolver, viaResult.Directory })
        {
            DirectoryServer siroco = Assert.Single(directory.Servers);
            Assert.All(siroco.Channels, c => Assert.Null(c.FixedLevelText));
            Assert.All(siroco.SpecialChannels, c => Assert.Null(c.FixedLevelText));
            Assert.Equal(new[] { 11, 12, 13 }, siroco.Channels.Select(c => c.Number).ToArray());
            Assert.Equal("(Lv.01~20)", ServerPickerText.Level(siroco.Channels[0]));
            Assert.Equal("(Lv.20~70)", ServerPickerText.Level(siroco.Channels[1]));
        }
    }

    [Fact]
    public void A_failed_outcome_is_still_a_failure_and_never_asks_the_resolver()
    {
        var resolver = new Resolver();

        ServerDirectoryResult failed = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.IoError, Snapshot(), null, resolver.Lookup);
        ServerDirectoryResult missing = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.Ok, null, null, resolver.Lookup);

        Assert.False(failed.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, failed.Failure);
        Assert.False(missing.Success);
        Assert.Equal(OnlineNoticeKind.Unexpected, missing.Failure);
        Assert.Empty(resolver.Calls);
    }

    // ------------------------------------------------------------------ ChannelDirectoryCatalog

    private static string Table => "1>SYNTH-ONE\r\n" + FixedId + ">SYNTH-NOTE\r\n";

    [Fact]
    public void FromScripts_keeps_the_text_resolver_it_was_given()
    {
        var resolver = new Resolver();

        ChannelDirectoryCatalog withText = ChannelDirectoryCatalog.FromScripts(path => null, null, resolver.Lookup);
        ChannelDirectoryCatalog without = ChannelDirectoryCatalog.FromScripts(path => null, null);
        ChannelDirectoryCatalog explicitNull = ChannelDirectoryCatalog.FromScripts(path => null, null, null);

        Assert.NotNull(withText.ResolveClientText);
        Assert.Equal("SYNTH-NOTE", withText.ResolveClientText(FixedId));
        Assert.Null(withText.ResolveClientText(7));
        Assert.Null(without.ResolveClientText);
        Assert.Null(explicitNull.ResolveClientText);
    }

    [Fact]
    public void Create_wires_the_executable_text_only_when_fonts_are_installed()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        ChannelDirectoryCatalog withFonts = ChannelDirectoryCatalog.Create(assets, true);
        ChannelDirectoryCatalog withoutFonts = ChannelDirectoryCatalog.Create(assets, false);

        Assert.NotNull(withFonts.ResolveClientText);
        Assert.Equal("SYNTH-NOTE", withFonts.ResolveClientText(FixedId));
        Assert.Equal("SYNTH-ONE", withFonts.ResolveClientText(1));
        Assert.Null(withFonts.ResolveClientText(2));                    // 表里没有的编号
        Assert.Null(withoutFonts.ResolveClientText);                    // 没有汉字字体:固定文字画不出来,不接
    }

    [Fact]
    public void Create_has_no_text_resolver_without_an_executable_or_a_data_directory()
    {
        using var empty = new MiniGameDirectory();
        using DnfAssets noExecutable = DnfAssets.Open(empty.Root);

        Assert.Null(ChannelDirectoryCatalog.Create(noExecutable, true).ResolveClientText);
        Assert.Null(ChannelDirectoryCatalog.Create(noExecutable, false).ResolveClientText);

        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table));
        using PvfArchive pvf = PvfArchive.Open(Path.Combine(game.Root, "Script.pvf"));
        var packs = new NpkLibrary(Path.Combine(game.Root, "ImagePacks2"));
        using var noDirectory = new DnfAssets(pvf, packs);

        Assert.Null(noDirectory.DataDirectory);
        Assert.Null(ChannelDirectoryCatalog.Create(noDirectory, true).ResolveClientText);
        Assert.Null(ChannelDirectoryCatalog.Create(noDirectory, false).ResolveClientText);
    }

    [Fact]
    public void An_unreadable_executable_leaves_the_catalog_without_a_text_resolver()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", new byte[] { 1, 2, 3, 4 });
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(ChannelDirectoryCatalog.Create(assets, true).ResolveClientText);
    }

    [Fact]
    public void The_whole_chain_from_the_executable_to_the_second_line_text()
    {
        // 可执行文件的资源 → DnfAssets.ClientText → 目录参数 → 适配层 → 频道 → 第二行的文字
        using var game = new MiniGameDirectory();
        game.Write("DNF.exe", MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);
        ChannelDirectoryCatalog catalog = ChannelDirectoryCatalog.Create(assets, true);

        ServerDirectory directory = ChannelDirectoryAdapter.Map(Snapshot(), catalog.ResolveString, catalog.ResolveClientText);

        AssertSnapshotCarriesTheFixedText(directory, "SYNTH-NOTE");
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(directory.Servers[0].Channels[0]));
        Assert.Equal("(Lv.20~70)", ServerPickerText.Level(directory.Servers[0].Channels[1]));
    }

    // ------------------------------------------------------------------ 后端(OnlineShellBackend)

    [Fact]
    public async Task The_backend_hands_the_catalog_text_resolver_to_the_directory_mapping()
    {
        var resolver = new Resolver();
        ChannelDirectoryCatalog catalog = ChannelDirectoryCatalog.FromScripts(path => null, null, resolver.Lookup);
        var rig = new BackendRig(true, p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.Ok, Snapshot())), false, catalog);
        OnlineAuthOutcome login = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(login.Success);

        ServerDirectoryResult result = await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));

        Assert.True(result.Success);
        AssertSnapshotCarriesTheFixedText(result.Directory, "SYNTH-NOTE");
    }

    [Fact]
    public async Task The_backend_without_a_catalog_maps_the_directory_as_before()
    {
        var rig = new BackendRig(true, p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.Ok, Snapshot())), false, null);
        OnlineAuthOutcome login = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(login.Success);

        ServerDirectoryResult result = await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));

        Assert.True(result.Success);
        DirectoryServer siroco = Assert.Single(result.Directory.Servers);
        Assert.All(siroco.Channels, c => Assert.Null(c.FixedLevelText));
        Assert.All(siroco.SpecialChannels, c => Assert.Null(c.FixedLevelText));
    }
}
