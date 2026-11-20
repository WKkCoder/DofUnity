namespace Dnf.Play.Tests;

/// <summary>目录中性类型:频道的能进条件、推荐等级、服务器的可选条件、读目录结果。</summary>
public sealed class ServerDirectoryTests
{
    private static DirectoryChannel Channel(ChannelCongestion congestion, bool target = true, int min = 0, int max = 0)
        => new DirectoryChannel(1, "测试", congestion, target ? new ChannelTarget(30001) : null, min, max);

    [Theory]
    [InlineData(ChannelCongestion.Smooth, true, true)]
    [InlineData(ChannelCongestion.Good, true, true)]
    [InlineData(ChannelCongestion.Normal, true, true)]
    [InlineData(ChannelCongestion.Crowded, true, true)]
    [InlineData(ChannelCongestion.Full, true, false)]
    [InlineData(ChannelCongestion.Smooth, false, false)]
    [InlineData(ChannelCongestion.Crowded, false, false)]
    [InlineData(ChannelCongestion.Full, false, false)]
    public void A_channel_can_be_entered_only_with_a_target_and_when_not_full(ChannelCongestion congestion, bool hasTarget, bool canEnter)
    {
        DirectoryChannel channel = Channel(congestion, hasTarget);
        Assert.Equal(canEnter, channel.CanEnter);
        Assert.Equal(congestion == ChannelCongestion.Full, channel.IsFull);
    }

    [Theory]
    [InlineData(41, 55, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, false)]
    [InlineData(0, 55, false)]
    [InlineData(41, 0, false)]
    [InlineData(55, 41, false)]
    [InlineData(-1, 55, false)]
    public void The_recommended_level_needs_a_positive_minimum_and_a_maximum_not_below_it(int min, int max, bool valid)
    {
        Assert.Equal(valid, Channel(ChannelCongestion.Smooth, true, min, max).HasRecommendedLevel);
    }

    [Fact]
    public void A_channel_keeps_what_it_was_given_and_defaults_a_null_title()
    {
        var target = new ChannelTarget(30005, "10.0.0.1");
        var channel = new DirectoryChannel(5, null, ChannelCongestion.Good, target, 10, 20);
        Assert.Equal(5, channel.Number);
        Assert.Equal(string.Empty, channel.Title);
        Assert.Equal(ChannelCongestion.Good, channel.Congestion);
        Assert.Same(target, channel.Target);
        Assert.Equal((10, 20), (channel.RecommendedMinLevel, channel.RecommendedMaxLevel));
        Assert.Equal(DirectoryChannel.DefaultSlotFrame, channel.SlotFrame);
        Assert.Equal(0, new DirectoryChannel(0, "x", ChannelCongestion.Good, null).Number);
        Assert.Equal(5, new DirectoryChannel(6, "x", ChannelCongestion.Smooth, null, slotFrame: 5).SlotFrame);
    }

    [Fact]
    public void A_negative_channel_number_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectoryChannel(-1, "x", ChannelCongestion.Smooth, null));
    }

    [Fact]
    public void A_server_is_selectable_with_any_channel_even_if_all_are_full()
    {
        var full = new DirectoryServer("a", new[] { Channel(ChannelCongestion.Full), Channel(ChannelCongestion.Full) });
        Assert.True(full.IsSelectable);
        Assert.False(full.HasEnterableChannel);

        var mixed = new DirectoryServer("a", new[] { Channel(ChannelCongestion.Full), Channel(ChannelCongestion.Smooth) });
        Assert.True(mixed.IsSelectable);
        Assert.True(mixed.HasEnterableChannel);

        var noPort = new DirectoryServer("a", new[] { Channel(ChannelCongestion.Smooth, false) });
        Assert.True(noPort.IsSelectable);
        Assert.False(noPort.HasEnterableChannel);

        var empty = new DirectoryServer("a", null);
        Assert.False(empty.IsSelectable);
        Assert.False(empty.HasEnterableChannel);
        Assert.Empty(empty.Channels);
        Assert.Empty(empty.SpecialChannels);

        var onlySpecial = new DirectoryServer("a", null, false, new[] { Channel(ChannelCongestion.Smooth) });
        Assert.True(onlySpecial.IsSelectable);
        Assert.True(onlySpecial.HasEnterableChannel);

        var fullSpecial = new DirectoryServer("a", null, false, new[] { Channel(ChannelCongestion.Full) });
        Assert.True(fullSpecial.IsSelectable);
        Assert.False(fullSpecial.HasEnterableChannel);
    }

    [Fact]
    public void A_server_defaults_its_name_and_never_flags_an_event_unless_told()
    {
        var server = new DirectoryServer(null, null);
        Assert.Equal(string.Empty, server.Name);
        Assert.False(server.HasEventMark);
        Assert.True(new DirectoryServer("a", null, true).HasEventMark);
    }

    [Fact]
    public void The_empty_directory_has_no_servers_and_null_means_empty()
    {
        Assert.Empty(ServerDirectory.Empty.Servers);
        Assert.Empty(new ServerDirectory(null).Servers);
        var servers = new[] { new DirectoryServer("a", null) };
        Assert.Same(servers, new ServerDirectory(servers).Servers);
    }

    [Fact]
    public void A_successful_result_carries_the_directory_and_no_failure()
    {
        var directory = new ServerDirectory(new[] { new DirectoryServer("a", null) });
        ServerDirectoryResult ok = ServerDirectoryResult.Ok(directory);
        Assert.True(ok.Success);
        Assert.Same(directory, ok.Directory);
        Assert.Equal(OnlineNoticeKind.None, ok.Failure);
        Assert.Same(ServerDirectory.Empty, ServerDirectoryResult.Ok(null).Directory);
    }

    [Fact]
    public void A_failed_result_carries_only_the_kind_and_never_none()
    {
        ServerDirectoryResult fail = ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError);
        Assert.False(fail.Success);
        Assert.Null(fail.Directory);
        Assert.Equal(OnlineNoticeKind.NetworkError, fail.Failure);
        Assert.Equal(OnlineNoticeKind.Unexpected, ServerDirectoryResult.Fail(OnlineNoticeKind.None).Failure);
        Assert.Equal(OnlineNoticeKind.ProtocolError, ServerDirectoryResult.Fail(OnlineNoticeKind.ProtocolError).Failure);
    }
}
