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
}
