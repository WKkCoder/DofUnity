using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 真后端对"带主机的频道目标"和"读频道目录"的处理(r18-serverui 加的那一半):按主机连、换、读目录、失败结局映射、没有会话与取消。
/// 会话用假的(只造报告),不连任何网络。
/// </summary>
public sealed class OnlineShellBackendHostTests
{
    private static async Task<BackendRig> LoggedIn(Action<FakeSessionPort> onPort = null, bool legacyOnly = false)
    {
        var rig = new BackendRig(true, onPort, legacyOnly);
        OnlineAuthOutcome login = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(login.Success);
        return rig;
    }

    // ------------------------------------------------------------------ 带主机的目标

    [Fact]
    public async Task A_target_with_a_host_connects_to_that_host_and_not_through_the_port_only_call()
    {
        BackendRig rig = await LoggedIn();

        OnlineConnectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31777, "10.1.2.3"), default));

        Assert.True(result.Success);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.ConnectHostCalls);
        Assert.Equal("10.1.2.3", port.LastHost);
        Assert.Equal(31777, port.LastPort);
        Assert.Equal(0, port.ConnectCalls);
        Assert.Equal(0, port.SwitchCalls);
        Assert.Equal(0, port.SwitchHostCalls);
    }

    [Fact]
    public async Task A_target_with_a_host_switches_through_the_host_call()
    {
        BackendRig rig = await LoggedIn();

        OnlineConnectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(new ChannelTarget(31888, "10.9.8.7"), default));

        Assert.True(result.Success);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.SwitchHostCalls);
        Assert.Equal("10.9.8.7", port.LastHost);
        Assert.Equal(31888, port.LastPort);
        Assert.Equal(0, port.SwitchCalls);
        Assert.Equal(0, port.ConnectHostCalls);
        Assert.Equal(0, port.ConnectCalls);
    }

    [Fact]
    public async Task A_target_without_a_host_keeps_using_the_port_only_calls()
    {
        BackendRig rig = await LoggedIn();

        await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31777), default));
        await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(new ChannelTarget(31778), default));

        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.ConnectCalls);
        Assert.Equal(1, port.SwitchCalls);
        Assert.Equal(0, port.ConnectHostCalls);
        Assert.Equal(0, port.SwitchHostCalls);
    }

    [Fact]
    public async Task A_host_connect_maps_its_outcome_like_the_port_only_connect()
    {
        BackendRig rig = await LoggedIn(p =>
        {
            p.OnConnectHost = (host, port, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.ConnectFailed, OnlineCharacterListStatus.NotReceived, null));
            p.OnSwitchHost = (host, port, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.NotReceived, null));
        });

        OnlineConnectOutcome connect = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31000, "10.1.2.3"), default));
        OnlineConnectOutcome change = await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(new ChannelTarget(31000, "10.1.2.3"), default));

        Assert.False(connect.Success);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, connect.Failure);
        Assert.False(change.Success);
        Assert.Equal(OnlineNoticeKind.CharacterListMissing, change.Failure);
    }

    [Fact]
    public async Task A_successful_host_connect_carries_the_roster()
    {
        BackendRig rig = await LoggedIn();

        OnlineConnectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31000, "10.1.2.3"), default));

        Assert.True(result.Success);
        Assert.Equal(2, result.Characters.Entries.Count);
    }

    [Fact]
    public async Task A_host_connect_that_throws_faults_the_task_with_the_same_exception()
    {
        BackendRig rig = await LoggedIn(p => p.OnConnectHost = (host, port, ct) => throw new InvalidOperationException("synthetic"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.ConnectChannelAsync(new ChannelTarget(31000, "10.1.2.3"), default));
    }

    [Fact]
    public async Task A_host_target_without_a_session_fails_like_any_other_network_operation()
    {
        var rig = new BackendRig();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.ConnectChannelAsync(new ChannelTarget(31000, "10.1.2.3"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.SwitchChannelAsync(new ChannelTarget(31000, "10.1.2.3"), default));
    }

    [Fact]
    public async Task The_host_connect_passes_the_cancellation_token_through()
    {
        BackendRig rig = await LoggedIn();
        using var cts = new CancellationTokenSource();

        await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31000, "10.1.2.3"), cts.Token));

        Assert.Equal(cts.Token, rig.Port(0).LastToken);
    }

    // ------------------------------------------------------------------ 读目录

    [Fact]
    public async Task Loading_the_directory_maps_the_snapshot_and_reads_it_once()
    {
        BackendRig rig = await LoggedIn();

        ServerDirectoryResult result = await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));

        Assert.True(result.Success);
        Assert.Equal(new[] { "希洛克", "卡恩" }, result.Directory.Servers.Select(s => s.Name).ToArray());
        Assert.Equal(1, rig.Port(0).DirectoryCalls);
    }

    [Fact]
    public async Task The_directory_read_passes_the_cancellation_token_through()
    {
        BackendRig rig = await LoggedIn();
        using var cts = new CancellationTokenSource();

        await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(cts.Token));

        Assert.Equal(cts.Token, rig.Port(0).LastToken);
    }

    [Theory]
    [InlineData(ChannelDirectoryLoadOutcome.IoError, OnlineNoticeKind.NetworkError)]
    [InlineData(ChannelDirectoryLoadOutcome.FrameError, OnlineNoticeKind.ProtocolError)]
    [InlineData(ChannelDirectoryLoadOutcome.ParseFailed, OnlineNoticeKind.ProtocolError)]
    [InlineData(ChannelDirectoryLoadOutcome.KeyExchangeRejected, OnlineNoticeKind.Rejected)]
    [InlineData(ChannelDirectoryLoadOutcome.VersionRejected, OnlineNoticeKind.Rejected)]
    [InlineData(ChannelDirectoryLoadOutcome.InvalidArgument, OnlineNoticeKind.Unexpected)]
    public async Task A_failed_directory_read_becomes_a_failure_with_a_notice_kind(ChannelDirectoryLoadOutcome outcome, OnlineNoticeKind kind)
    {
        BackendRig rig = await LoggedIn(p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(outcome, null)));

        ServerDirectoryResult result = await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));

        Assert.False(result.Success);
        Assert.Equal(kind, result.Failure);
    }

    [Fact]
    public async Task An_empty_snapshot_is_a_successful_empty_directory()
    {
        BackendRig rig = await LoggedIn(p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.Ok, DirectoryTestData.Snapshot())));

        ServerDirectoryResult result = await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));

        Assert.True(result.Success);
        Assert.Empty(result.Directory.Servers);
    }

    [Fact]
    public async Task A_directory_read_that_throws_faults_the_task()
    {
        BackendRig rig = await LoggedIn(p => p.OnDirectory = ct => throw new InvalidOperationException("synthetic"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.LoadAsync(default));
    }

    [Fact]
    public async Task A_directory_read_that_is_canceled_stays_canceled()
    {
        BackendRig rig = await LoggedIn(p => p.OnDirectory = ct => Task.FromCanceled<OnlineDirectoryReport>(new CancellationToken(true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Backend.LoadAsync(default));
    }

    [Fact]
    public async Task A_token_that_is_already_canceled_never_touches_the_session()
    {
        BackendRig rig = await LoggedIn();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Backend.LoadAsync(cts.Token));

        Assert.Equal(0, rig.Port(0).DirectoryCalls);
    }

    [Fact]
    public async Task Reading_the_directory_without_a_session_fails_like_any_other_network_operation()
    {
        var rig = new BackendRig();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.LoadAsync(default));

        BackendRig loggedIn = await LoggedIn();
        loggedIn.Backend.Disconnect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => loggedIn.Backend.LoadAsync(default));
        Assert.Equal(0, loggedIn.Port(0).DirectoryCalls);
    }

    [Fact]
    public async Task Reading_the_directory_after_the_backend_is_disposed_fails()
    {
        BackendRig rig = await LoggedIn();
        rig.Backend.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.LoadAsync(default));
    }

    [Fact]
    public async Task A_session_that_only_knows_the_old_interface_cannot_read_the_directory_and_is_not_touched()
    {
        BackendRig rig = await LoggedIn(legacyOnly: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.LoadAsync(default));

        Assert.Equal(0, rig.Port(0).DirectoryCalls);
    }

    [Fact]
    public async Task Each_login_reads_the_directory_from_its_own_session()
    {
        BackendRig rig = await LoggedIn();
        await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));

        Assert.Equal(2, rig.PortCount);
        Assert.Equal(1, rig.Port(0).DirectoryCalls);
        Assert.Equal(1, rig.Port(1).DirectoryCalls);
    }

    // ------------------------------------------------------------------ 目录 → 连接

    [Fact]
    public async Task The_hosts_in_the_directory_become_the_targets_the_backend_connects_to()
    {
        ChannelDirectorySnapshot snapshot = DirectoryTestData.Snapshot(DirectoryTestData.Group("希洛克",
            DirectoryTestData.Entry("希洛克", 1, host: "", port: 31001),
            DirectoryTestData.Entry("希洛克", 2, host: "10.1.2.3", port: 31002),
            DirectoryTestData.Entry("希洛克", 3, host: "game.example.com", port: 31003)));
        BackendRig rig = await LoggedIn(p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.Ok, snapshot)));

        ServerDirectoryResult result = await GatewayTestSupport.Bounded(rig.Backend.LoadAsync(default));
        IReadOnlyList<DirectoryChannel> channels = result.Directory.Servers[0].Channels;
        Assert.Equal(new ChannelTarget(31001), channels[0].Target);
        Assert.Equal(new ChannelTarget(31002, "10.1.2.3"), channels[1].Target);
        Assert.Null(channels[2].Target);                                   // 域名:不能选

        FakeSessionPort port = rig.Port(0);
        await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(channels[0].Target, default));
        Assert.Equal(1, port.ConnectCalls);                                // 目录里没写主机:走只带端口的老入口,连网关给的地址
        Assert.Equal(31001, port.LastPort);
        Assert.Equal(0, port.ConnectHostCalls);

        await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(channels[1].Target, default));
        Assert.Equal(1, port.SwitchHostCalls);                             // 写了 IP:按那台连
        Assert.Equal("10.1.2.3", port.LastHost);
        Assert.Equal(31002, port.LastPort);
        Assert.Equal(0, port.SwitchCalls);
    }
}
