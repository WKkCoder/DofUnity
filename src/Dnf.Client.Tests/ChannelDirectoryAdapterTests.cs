using System.Text;
using Dnf.Data;
using Dnf.Net.ChannelDirectory;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>联网层的频道目录快照 → Play 层中性目录的翻译:分组与顺序、序号与名字、推荐等级、五档拥挤与满载、目标地址的主机回落、失败结局的提示类别。</summary>
public sealed class ChannelDirectoryAdapterTests
{
    private static DirectoryChannel Map(ChannelEntry entry) => ChannelDirectoryAdapter.Channel(entry);

    // ------------------------------------------------------------------ 整份目录

    [Fact]
    public void No_snapshot_or_no_groups_is_the_empty_directory()
    {
        Assert.Same(ServerDirectory.Empty, ChannelDirectoryAdapter.Map(null));
        Assert.Same(ServerDirectory.Empty, ChannelDirectoryAdapter.Map(DirectoryTestData.Snapshot()));
    }

    [Fact]
    public void One_group_with_no_channels_is_a_server_that_cannot_be_selected()
    {
        ServerDirectory directory = ChannelDirectoryAdapter.Map(DirectoryTestData.Snapshot(DirectoryTestData.Group("希洛克")));
        DirectoryServer server = Assert.Single(directory.Servers);
        Assert.Equal("希洛克", server.Name);
        Assert.Empty(server.Channels);
        Assert.False(server.IsSelectable);
    }

    [Fact]
    public void Groups_become_servers_and_channels_keep_the_directory_order_and_global_numbers()
    {
        ServerDirectory directory = ChannelDirectoryAdapter.Map(DirectoryTestData.Default());
        Assert.Equal(new[] { "希洛克", "卡恩" }, directory.Servers.Select(s => s.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, directory.Servers[0].Channels.Select(c => c.Number).ToArray());
        Assert.Equal(new[] { 4, 5 }, directory.Servers[1].Channels.Select(c => c.Number).ToArray());     // 第二个服务器的第一个频道不是 ch1
        Assert.All(directory.Servers, s => Assert.True(s.IsSelectable));
    }

    [Fact]
    public void Many_groups_and_channels_are_all_kept_in_order()
    {
        var groups = new List<ChannelGroup>();
        int number = 1;
        for (int g = 0; g < 12; g++)
        {
            var entries = new List<ChannelEntry>();
            for (int c = 0; c < g % 4; c++)
            {
                entries.Add(DirectoryTestData.Entry("服务器" + g, number++));
            }
            groups.Add(DirectoryTestData.Group("服务器" + g, entries.ToArray()));
        }
        ServerDirectory directory = ChannelDirectoryAdapter.Map(DirectoryTestData.Snapshot(groups.ToArray()));
        Assert.Equal(12, directory.Servers.Count);
        Assert.Equal(Enumerable.Range(0, 12).Select(g => "服务器" + g).ToArray(), directory.Servers.Select(s => s.Name).ToArray());
        Assert.Equal(Enumerable.Range(0, 12).Select(g => g % 4).ToArray(), directory.Servers.Select(s => s.Channels.Count).ToArray());
        Assert.Equal(Enumerable.Range(1, number - 1).ToArray(), directory.Servers.SelectMany(s => s.Channels).Select(c => c.Number).ToArray());
    }

    [Fact]
    public void More_than_fifteen_channels_in_a_group_all_come_through_for_the_screen_to_page()
    {
        ChannelEntry[] entries = Enumerable.Range(1, 40).Select(n => DirectoryTestData.Entry("希洛克", n)).ToArray();
        ServerDirectory directory = ChannelDirectoryAdapter.Map(DirectoryTestData.Snapshot(DirectoryTestData.Group("希洛克", entries)));
        Assert.Equal(40, directory.Servers[0].Channels.Count);
        Assert.Equal(Enumerable.Range(1, 40).ToArray(), directory.Servers[0].Channels.Select(c => c.Number).ToArray());
    }

    [Fact]
    public void A_blank_group_tag_becomes_an_empty_name()
    {
        ServerDirectory directory = ChannelDirectoryAdapter.Map(DirectoryTestData.Snapshot(DirectoryTestData.Group(null, DirectoryTestData.Entry("", 1))));
        Assert.Equal(string.Empty, directory.Servers[0].Name);
    }

    [Fact]
    public void The_event_mark_is_never_set_because_the_directory_has_no_such_field()
    {
        ServerDirectory directory = ChannelDirectoryAdapter.Map(DirectoryTestData.Default());
        Assert.All(directory.Servers, s => Assert.False(s.HasEventMark));
    }

    // ------------------------------------------------------------------ 一个频道

    [Fact]
    public void A_channel_takes_its_number_and_the_name_without_digits()
    {
        DirectoryChannel channel = Map(DirectoryTestData.Entry("希洛克", 11, "神殿"));
        Assert.Equal(11, channel.Number);
        Assert.Equal("神殿", channel.Title);
        Assert.Equal(string.Empty, Map(DirectoryTestData.Entry("希洛克", 3, "")).Title);
    }

    [Fact]
    public void A_negative_number_is_clamped_to_zero_and_a_null_entry_is_refused()
    {
        Assert.Equal(0, Map(DirectoryTestData.Entry("希洛克", -4)).Number);
        Assert.Throws<ArgumentNullException>(() => ChannelDirectoryAdapter.Channel(null));
        Assert.Throws<ArgumentNullException>(() => ChannelDirectoryAdapter.Congestion(null));
        Assert.Throws<ArgumentNullException>(() => ChannelDirectoryAdapter.Target(null));
    }

    [Theory]
    [InlineData(ChannelCongestionTier.Comfortable, ChannelCongestion.Smooth)]
    [InlineData(ChannelCongestionTier.Harmony, ChannelCongestion.Good)]
    [InlineData(ChannelCongestionTier.Normal, ChannelCongestion.Normal)]
    [InlineData(ChannelCongestionTier.Crowded, ChannelCongestion.Crowded)]
    [InlineData(ChannelCongestionTier.Full, ChannelCongestion.Full)]
    public void The_five_congestion_tiers_map_one_to_one(ChannelCongestionTier tier, ChannelCongestion expected)
    {
        Assert.Equal(expected, ChannelDirectoryAdapter.Congestion(DirectoryTestData.Entry("希洛克", 1, tier: tier)));
        Assert.Equal(expected, Map(DirectoryTestData.Entry("希洛克", 1, tier: tier)).Congestion);
    }

    [Theory]
    [InlineData(ChannelCongestionTier.Comfortable)]
    [InlineData(ChannelCongestionTier.Harmony)]
    [InlineData(ChannelCongestionTier.Normal)]
    [InlineData(ChannelCongestionTier.Crowded)]
    public void The_full_flag_wins_over_any_tier_and_makes_the_channel_unenterable(ChannelCongestionTier tier)
    {
        DirectoryChannel channel = Map(DirectoryTestData.Entry("希洛克", 1, tier: tier, full: true));
        Assert.Equal(ChannelCongestion.Full, channel.Congestion);
        Assert.True(channel.IsFull);
        Assert.False(channel.CanEnter);
    }

    [Fact]
    public void The_full_tier_without_the_flag_is_still_full()
    {
        DirectoryChannel channel = Map(DirectoryTestData.Entry("希洛克", 1, tier: ChannelCongestionTier.Full, full: false));
        Assert.Equal(ChannelCongestion.Full, channel.Congestion);
        Assert.False(channel.CanEnter);
    }

    [Fact]
    public void The_recommended_level_needs_the_flag_and_both_ends()
    {
        DirectoryChannel with = Map(DirectoryTestData.Entry("希洛克", 1, minLevel: 41, maxLevel: 55));
        Assert.True(with.HasRecommendedLevel);
        Assert.Equal((41, 55), (with.RecommendedMinLevel, with.RecommendedMaxLevel));

        DirectoryChannel without = Map(DirectoryTestData.Entry("希洛克", 1));
        Assert.False(without.HasRecommendedLevel);
        Assert.Equal((0, 0), (without.RecommendedMinLevel, without.RecommendedMaxLevel));

        // 标志在、数值缺一头:不算有推荐等级
        ChannelEntry half = new ChannelEntry("希洛克", 1, "测试1", "测试", 1000, 0, "", 31001,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u),
            ChannelEntryFlags.HasValidPort | ChannelEntryFlags.HasRecommendLevel, 41, null);
        Assert.False(Map(half).HasRecommendedLevel);
        Assert.Equal((0, 0), (Map(half).RecommendedMinLevel, Map(half).RecommendedMaxLevel));

        // 数值在、标志没有:同样不算
        ChannelEntry noFlag = new ChannelEntry("希洛克", 1, "测试1", "测试", 1000, 0, "", 31001,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u),
            ChannelEntryFlags.HasValidPort, 41, 55);
        Assert.False(Map(noFlag).HasRecommendedLevel);
    }

    // ------------------------------------------------------------------ 目标地址

    [Fact]
    public void An_empty_host_means_the_gateway_given_game_server_address()
    {
        ChannelTarget target = ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, host: "", port: 31005));
        Assert.Equal(new ChannelTarget(31005), target);
        Assert.Null(target.Host);

        Assert.Null(ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, host: "   ", port: 31005)).Host);
        Assert.Null(ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, host: null, port: 31005)).Host);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData(" 0.0.0.0 ")]
    [InlineData("0:0:0:0:0:0:0:0")]
    public void The_bind_all_addresses_are_not_connectable_and_fall_back_like_an_empty_host(string host)
    {
        ChannelTarget target = ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, host: host, port: 31005));
        Assert.Equal(new ChannelTarget(31005), target);
        Assert.Null(target.Host);
    }

    [Theory]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData(" 10.1.2.3 ", "10.1.2.3")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("fe80::1", "fe80::1")]
    public void A_valid_ip_literal_is_used_as_is_after_trimming(string host, string expected)
    {
        ChannelTarget target = ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, host: host, port: 31005));
        Assert.Equal(new ChannelTarget(31005, expected), target);
        Assert.Equal(expected, target.Host);
    }

    [Theory]
    [InlineData("game.example.com")]
    [InlineData("localhost")]
    [InlineData("10.1.2")]
    [InlineData("10.1")]
    [InlineData("167772162")]
    [InlineData("10.1.2.3.4")]
    [InlineData("10.1.2.3:31000")]
    [InlineData("not an ip")]
    public void A_host_that_is_not_an_ip_literal_makes_the_channel_unconnectable(string host)
    {
        Assert.Null(ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, host: host, port: 31005)));
        DirectoryChannel channel = Map(DirectoryTestData.Entry("希洛克", 1, host: host, port: 31005));
        Assert.Null(channel.Target);
        Assert.False(channel.CanEnter);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(31000, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    [InlineData(100000, false)]
    public void The_port_must_be_in_range_and_the_flag_is_checked_too(int port, bool valid)
    {
        ChannelEntry flagged = new ChannelEntry("希洛克", 1, "测试1", "测试", 1000, 0, "", port,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u), ChannelEntryFlags.HasValidPort, null, null);
        Assert.Equal(valid, ChannelDirectoryAdapter.Target(flagged) != null);
        if (valid)
        {
            Assert.Equal(port, ChannelDirectoryAdapter.Target(flagged).Port);
        }

        // 数值合法但没有"端口有效"标志:联网层判过无效,这里不推翻它
        ChannelEntry unflagged = new ChannelEntry("希洛克", 1, "测试1", "测试", 1000, 0, "", port,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u), ChannelEntryFlags.None, null, null);
        Assert.Null(ChannelDirectoryAdapter.Target(unflagged));
    }

    [Fact]
    public void A_channel_without_a_usable_port_is_unenterable_even_when_not_full()
    {
        DirectoryChannel channel = Map(DirectoryTestData.Entry("希洛克", 1, validPort: false));
        Assert.Null(channel.Target);
        Assert.False(channel.IsFull);
        Assert.False(channel.CanEnter);
    }

    // ------------------------------------------------------------------ 读目录的结局

    [Theory]
    [InlineData(ChannelDirectoryLoadOutcome.IoError, OnlineNoticeKind.NetworkError)]
    [InlineData(ChannelDirectoryLoadOutcome.FrameError, OnlineNoticeKind.ProtocolError)]
    [InlineData(ChannelDirectoryLoadOutcome.BadKeyExchangeLength, OnlineNoticeKind.ProtocolError)]
    [InlineData(ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed, OnlineNoticeKind.ProtocolError)]
    [InlineData(ChannelDirectoryLoadOutcome.ParseFailed, OnlineNoticeKind.ProtocolError)]
    [InlineData(ChannelDirectoryLoadOutcome.KeyExchangeRejected, OnlineNoticeKind.Rejected)]
    [InlineData(ChannelDirectoryLoadOutcome.VersionRejected, OnlineNoticeKind.Rejected)]
    [InlineData(ChannelDirectoryLoadOutcome.InvalidArgument, OnlineNoticeKind.Unexpected)]
    [InlineData(ChannelDirectoryLoadOutcome.Ok, OnlineNoticeKind.Unexpected)]
    [InlineData((ChannelDirectoryLoadOutcome)99, OnlineNoticeKind.Unexpected)]
    public void Failure_outcomes_map_to_the_closest_notice_kind(ChannelDirectoryLoadOutcome outcome, OnlineNoticeKind expected)
    {
        Assert.Equal(expected, ChannelDirectoryAdapter.Failure(outcome));
    }

    [Fact]
    public void Every_known_outcome_is_covered_by_a_mapping()
    {
        foreach (ChannelDirectoryLoadOutcome outcome in Enum.GetValues<ChannelDirectoryLoadOutcome>())
        {
            if (outcome == ChannelDirectoryLoadOutcome.Ok)
            {
                continue;
            }
            ServerDirectoryResult result = ChannelDirectoryAdapter.Result(outcome, null);
            Assert.False(result.Success, outcome.ToString());
            Assert.NotEqual(OnlineNoticeKind.None, result.Failure);
        }
    }

    [Fact]
    public void A_successful_outcome_carries_the_mapped_directory()
    {
        ServerDirectoryResult result = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.Ok, DirectoryTestData.Default());
        Assert.True(result.Success);
        Assert.Equal(2, result.Directory.Servers.Count);
        Assert.Equal(OnlineNoticeKind.None, result.Failure);
    }

    [Fact]
    public void A_successful_outcome_with_no_snapshot_is_an_unexpected_failure()
    {
        ServerDirectoryResult result = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.Ok, null);
        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.Unexpected, result.Failure);
    }

    [Fact]
    public void A_failed_outcome_ignores_a_snapshot_that_came_with_it()
    {
        ServerDirectoryResult result = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.IoError, DirectoryTestData.Default());
        Assert.False(result.Success);
        Assert.Null(result.Directory);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
    }

    [Fact]
    public void An_empty_snapshot_is_a_successful_empty_directory()
    {
        ServerDirectoryResult result = ChannelDirectoryAdapter.Result(ChannelDirectoryLoadOutcome.Ok, DirectoryTestData.Snapshot());
        Assert.True(result.Success);
        Assert.Empty(result.Directory.Servers);
    }

    // ------------------------------------------------------------------ 真实频道号 / #ch. 前缀清洗 / 本地目录脚本

    [Fact]
    public void Channel_prefers_ParsedChannelNo_resolves_SlotRule_title_and_strips_machine_prefixes()
    {
        var rule = new ChannelServerSlotRule(3, 11, 4, "cname_syn_11", string.Empty, 0, "[zone_alpha]");
        var entry = new ChannelEntry(
            "siroco",
            1,
            "#ch.11",
            "#ch.",
            200,
            10,
            "",
            19011,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u),
            ChannelEntryFlags.HasValidPort | ChannelEntryFlags.HasRecommendLevel,
            41,
            85,
            11,
            rule);

        // 1) 有字表时解析 <4::cname_syn_11>
        DirectoryChannel resolved = ChannelDirectoryAdapter.Channel(
            entry,
            (kind, key) => kind == 4 && key == "cname_syn_11" ? "合成频道甲" : null);
        Assert.Equal(11, resolved.Number);
        Assert.Equal("合成频道甲", resolved.Title);
        Assert.Equal(41, resolved.RecommendedMinLevel);
        Assert.Equal(85, resolved.RecommendedMaxLevel);
        Assert.Equal("ch11.合成频道甲", ServerPickerText.ChannelTitle(resolved, cjk: true));
        Assert.Equal("ch11", ServerPickerText.ChannelTitle(resolved, cjk: false));

        // 2) 没有字表/字体时清掉 #ch.,不出现 ch11#ch.
        DirectoryChannel unresolved = ChannelDirectoryAdapter.Channel(entry, resolveString: null);
        Assert.Equal(11, unresolved.Number);
        Assert.Equal(string.Empty, unresolved.Title);
        Assert.Equal("ch11", ServerPickerText.ChannelTitle(unresolved, cjk: true));

        // 3) 字面名称优先作为回退;没有 SlotRule 时清洗 ch./#test.,保留人类前缀
        var literalRule = new ChannelServerSlotRule(3, 21, -1, string.Empty, "直写频道", 0, "[none]");
        var literalEntry = new ChannelEntry(
            "siroco", 2, "#ch.21", "#ch.", 200, 10, "", 19021,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u),
            ChannelEntryFlags.HasValidPort, null, null, 21, literalRule);
        DirectoryChannel literal = ChannelDirectoryAdapter.Channel(literalEntry);
        Assert.Equal(21, literal.Number);
        Assert.Equal("直写频道", literal.Title);

        Assert.Equal(string.Empty, Map(DirectoryTestData.Entry("希洛克", 3, "ch.")).Title);
        Assert.Equal(string.Empty, Map(DirectoryTestData.Entry("希洛克", 3, "#test.")).Title);
        Assert.Equal("神殿", Map(DirectoryTestData.Entry("希洛克", 3, "神殿")).Title);

        Assert.Equal((ushort)3, ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("希洛克", 1, port: 31005)).ServerGroup);
        Assert.Equal((ushort)3, ChannelDirectoryAdapter.Target(DirectoryTestData.Entry("siroco", 1, host: "10.1.2.3", port: 31005)).ServerGroup);
    }

    [Fact]
    public void ChannelDirectoryCatalog_FromScripts_extracts_thresholds_slots_and_string_resolver()
    {
        ScriptLine[] recommendLines =
        {
            new ScriptLine("[channel rate full]", new[]
            {
                new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(87.5f), null),
            }),
            new ScriptLine("[channel]", new[]
            {
                new ScriptValue(ScriptValueKind.String, 0, "[zone_alpha]"),
                new ScriptValue(ScriptValueKind.Int, 41, null),
                new ScriptValue(ScriptValueKind.Int, 85, null),
            }),
        };

        ScriptLine[] infoLines =
        {
            new ScriptLine("[server]", new[]
            {
                new ScriptValue(ScriptValueKind.Int, 3, null),
                // slot 11 (15 个值)
                new ScriptValue(ScriptValueKind.Int, 11, null),
                new ScriptValue(ScriptValueKind.Keyed, 4, "cname_syn_11"),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.String, 0, "[zone_alpha]"),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                // slot 52 (15 个值)
                new ScriptValue(ScriptValueKind.Int, 52, null),
                new ScriptValue(ScriptValueKind.Keyed, 4, "cname_syn_52"),
                new ScriptValue(ScriptValueKind.Int, 13, null),
                new ScriptValue(ScriptValueKind.String, 0, "[none]"),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                new ScriptValue(ScriptValueKind.Int, 0, null),
                new ScriptValue(ScriptValueKind.Int, 0, null),
            }),
        };

        Func<string, ScriptLine[]> reader = path => path switch
        {
            "etc/channelrecommendlevel.etc" => recommendLines,
            "etc/channel_info.etc" => infoLines,
            _ => null,
        };

        ChannelDirectoryCatalog noStrings = ChannelDirectoryCatalog.FromScripts(reader, strings: null);
        Assert.Null(noStrings.ResolveString);
        Assert.Equal(87.5f, noStrings.FallbackScript.FullThresholdPercent);
        Assert.True(noStrings.FallbackScript.HasExplicitFullThreshold);
        ChannelRecommendLevelRule rec = Assert.Single(noStrings.FallbackScript.RecommendLevels);
        Assert.Equal("[zone_alpha]", rec.Tag);
        Assert.Equal(41, rec.MinLevel);
        Assert.Equal(85, rec.MaxLevel);
        ChannelServerSlotRule slot11 = noStrings.FallbackScript.FindServerSlot("siroco", 11);
        Assert.NotNull(slot11);
        Assert.Equal("cname_syn_11", slot11.TitleStringKey);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] gbkBook = Encoding.GetEncoding(54936).GetBytes("cname_syn_11>合成频道甲\r\ncname_syn_52>合成频道乙\r\n");
        var strings = new StrLibrary(p => string.Equals(p, "etc/etc.kor.str", StringComparison.OrdinalIgnoreCase) ? gbkBook : null);

        ChannelDirectoryCatalog withStrings = ChannelDirectoryCatalog.FromScripts(reader, strings);
        Assert.NotNull(withStrings.ResolveString);
        Assert.Equal("合成频道甲", withStrings.ResolveString(4, "cname_syn_11"));
        Assert.Equal("合成频道乙", withStrings.ResolveString(4, "cname_syn_52"));
    }

    [Fact]
    public void Map_RoutesSpecialChannels_FiltersPvpHiddenAndUnknown_And_PreservesUnscriptedGroups()
    {
        const string scriptText =
            "[channel] `[deathtower]` 20 70\n" +
            "[channel] `[granfloris]` 1 20\n" +
            "[server] 3\n" +
            "  1 <4::cname_1> 11 `[deathtower]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  6 <4::cname_6> 3 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  7 <4::cname_7> 3 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  9 <4::cname_9> 3 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  11 <4::cname_11> 0 `[granfloris]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  52 <4::cname_52> 13 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  53 <4::cname_53> 8 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  54 <4::cname_54> 4 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  55 <4::cname_55> 10 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  56 <4::cname_56> 16 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  99 <4::cname_99> 17 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n" +
            "[server] 1\n" +
            "  52 <4::cname_52> 13 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n" +
            "[server] 2\n" +
            "  6 <4::cname_6> 3 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n";
        ChannelScriptData script = ChannelScriptParser.Parse(scriptText);

        ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build(
            "v1",
            false,
            script,
            new[]
            {
                // 注意线缆顺序刻意把交易频道(6,7,9)排在亡者峡谷(1)之前,验证 11 优先、3 补位、上限 3
                new ChannelWireGroup("siroco", new[]
                {
                    new ChannelWireEntry("#ch.6", 200, 10, "", 19006),
                    new ChannelWireEntry("#ch.7", 200, 10, "", 19007),
                    new ChannelWireEntry("#ch.9", 200, 10, "", 19009),
                    new ChannelWireEntry("#ch.1", 200, 10, "", 19001),
                    new ChannelWireEntry("#ch.11", 200, 10, "", 19011),
                    new ChannelWireEntry("#ch.52", 200, 150, "", 19052),
                    new ChannelWireEntry("#ch.53", 200, 20, "", 19053),
                    new ChannelWireEntry("#ch.54", 200, 20, "", 19054),
                    new ChannelWireEntry("#ch.55", 200, 20, "", 19055),
                    new ChannelWireEntry("#ch.56", 200, 20, "", 19056),
                    new ChannelWireEntry("#ch.99", 200, 20, "", 19099),
                    new ChannelWireEntry("#ch.88", 200, 20, "", 19088), // 已建表组里未定义 -> 未知频道跳过
                }),
                // 卡恩只有 PK 频道上线 -> 灰牌不可选
                new ChannelWireGroup("cain", new[]
                {
                    new ChannelWireEntry("#ch.52", 200, 5, "", 19152),
                }),
                // 狄瑞吉只有特殊频道上线 -> 主网格空但特殊栏非空,仍可选
                new ChannelWireGroup("diregie", new[]
                {
                    new ChannelWireEntry("#ch.6", 200, 5, "", 19206),
                }),
                // 普雷脚本未建表 -> SlotRule 为 null 时因 HasServerSlotsForGroup 为 false,按合成测试兼容进入主网格
                new ChannelWireGroup("prey", new[]
                {
                    new ChannelWireEntry("综合频道201", 200, 5, "", 19301),
                }),
            });

        static string Lookup(int kind, string key) => kind == 4 ? key switch
        {
            "cname_1" => "亡者峡谷",
            "cname_6" => "交易一区",
            "cname_7" => "交易二区",
            "cname_9" => "交易三区",
            "cname_11" => "格兰之森",
            _ => null,
        } : null;

        ServerDirectory dir = ChannelDirectoryAdapter.Map(snapshot, Lookup);
        Assert.Equal(4, dir.Servers.Count);

        DirectoryServer siroco = dir.Servers[0];
        Assert.True(siroco.IsSelectable);
        DirectoryChannel main = Assert.Single(siroco.Channels);
        Assert.Equal(11, main.Number);
        Assert.Equal("格兰之森", main.Title);
        Assert.Equal(0, main.SlotFrame);
        Assert.Equal(new[] { 1, 6, 7 }, siroco.SpecialChannels.Select(c => c.Number).ToArray());
        Assert.Equal(new[] { 8, 5, 5 }, siroco.SpecialChannels.Select(c => c.SlotFrame).ToArray());
        Assert.True(siroco.SpecialChannels[0].HasRecommendedLevel);
        Assert.False(siroco.SpecialChannels[1].HasRecommendedLevel);

        DirectoryServer cain = dir.Servers[1];
        Assert.Empty(cain.Channels);
        Assert.Empty(cain.SpecialChannels);
        Assert.False(cain.IsSelectable);

        DirectoryServer diregie = dir.Servers[2];
        Assert.Empty(diregie.Channels);
        DirectoryChannel diregieSpecial = Assert.Single(diregie.SpecialChannels);
        Assert.Equal(6, diregieSpecial.Number);
        Assert.True(diregie.IsSelectable);

        DirectoryServer prey = dir.Servers[3];
        DirectoryChannel preyMain = Assert.Single(prey.Channels);
        Assert.Equal(201, preyMain.Number);
        Assert.Equal(DirectoryChannel.DefaultSlotFrame, preyMain.SlotFrame);
        Assert.True(prey.IsSelectable);
    }

    [Theory]
    [InlineData(2, "[none]", 4)]
    [InlineData(3, "[granfloris]", 5)]
    [InlineData(11, "[none]", 8)]
    [InlineData(0, "[granfloris]", 0)]
    [InlineData(0, "[sky_catle]", 1)]
    [InlineData(0, "[behemoth]", 2)]
    [InlineData(0, "[Alfhlyra]", 3)]
    [InlineData(0, "[elven_guard]", 4)]
    [InlineData(0, "[north_myre]", 6)]
    [InlineData(0, "[stormpass]", 7)]
    [InlineData(0, "[deathtower]", 8)]
    [InlineData(0, "[ancient]", 9)]
    [InlineData(0, "[Antwer]", 10)]
    [InlineData(0, "[Hall]", 11)]
    [InlineData(0, "[Fortress]", 12)]
    [InlineData(0, "[impossible]", 13)]
    [InlineData(0, "[seatrain]", 14)]
    [InlineData(0, "[dragonroad]", 16)]
    [InlineData(0, "[timedoor]", 17)]
    [InlineData(0, "[powerstation]", 18)]
    [InlineData(0, " [TIMEDOOR] ", 17)]
    [InlineData(0, "[unknown_zone]", 0)]
    [InlineData(0, "[none]", 0)]
    public void ResolveSlotFrame_MatchesAllOriginalKindsAndAreaTags(int kind, string areaTag, int expectedFrame)
    {
        var rule = new ChannelServerSlotRule(3, 11, -1, string.Empty, "syn", kind, areaTag);
        Assert.Equal(expectedFrame, ChannelDirectoryAdapter.ResolveSlotFrame(rule));
        Assert.Equal(DirectoryChannel.DefaultSlotFrame, ChannelDirectoryAdapter.ResolveSlotFrame(null));
    }

    [Fact]
    public void Channel_SuppressesRecommendLevel_ForTradeChannelKind3()
    {
        var rule = new ChannelServerSlotRule(3, 6, -1, string.Empty, "交易", 3, "[none]");
        var entry = new ChannelEntry(
            "siroco",
            1,
            "#ch.6",
            "#ch.",
            200,
            0,
            "",
            19006,
            new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0u),
            ChannelEntryFlags.HasValidPort | ChannelEntryFlags.HasRecommendLevel,
            10,
            70,
            6,
            rule);
        DirectoryChannel mapped = ChannelDirectoryAdapter.Channel(entry);
        Assert.Equal(5, mapped.SlotFrame);
        Assert.False(mapped.HasRecommendedLevel);
        Assert.Equal((0, 0), (mapped.RecommendedMinLevel, mapped.RecommendedMaxLevel));
    }
}
