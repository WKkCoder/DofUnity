using System.Net;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>分阶段会话:连频道/换频道的主机字面量重载与非法主机失败不变量。</summary>
public sealed class OnlineClientSessionHostTests
{
    private const int ChannelPort = 31011;
    private const int AltChannelPort = 31012;

    private static string GatewayGameHost => GatewayTestSupport.Dots(192, 0, 2, 10);
    private static string OverrideV4 => GatewayTestSupport.Dots(203, 0, 113, 20);
    private static string OverrideV6 => "2001:db8::20";

    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static (OnlineFlowRig Rig, List<IPAddress> Addresses) TrackAddresses(OnlineFlowRig rig = null)
    {
        rig ??= new OnlineFlowRig();
        var addresses = new List<IPAddress>();
        Func<IPAddress, int, CancellationToken, Task<OnlineChannelLink>> inner = rig.Hooks.ChannelFactory;
        rig.Hooks.ChannelFactory = (address, port, ct) =>
        {
            addresses.Add(address);
            return inner(address, port, ct);
        };
        return (rig, addresses);
    }

    private static async Task<OnlineClientSession> LoggedInWithGatewayHost(OnlineFlowRig rig)
    {
        rig.FakeGateway.GameServerIp = GatewayGameHost;
        return await rig.LoggedIn();
    }

    [Fact]
    public async Task Connect_null_host_uses_gateway_address_like_port_only_overload()
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        OnlineConnectResult ok = await Bounded(client.ConnectAsync(null, ChannelPort));
        Assert.True(ok.IsSuccess);
        Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse(GatewayGameHost), addresses[0]);
    }

    [Fact]
    public async Task Connect_ipv4_literal_overrides_gateway_address()
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        Assert.True((await Bounded(client.ConnectAsync(OverrideV4, ChannelPort))).IsSuccess);
        Assert.Equal(IPAddress.Parse(OverrideV4), addresses[0]);
    }

    [Fact]
    public async Task Connect_ipv6_literal_overrides_gateway_address()
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        Assert.True((await Bounded(client.ConnectAsync(OverrideV6, ChannelPort))).IsSuccess);
        Assert.Equal(IPAddress.Parse(OverrideV6), addresses[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-an-ip")]
    [InlineData("999.1.1.1")]
    public async Task Connect_invalid_host_returns_connect_failed_without_calling_factory(string host)
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        OnlineConnectResult result = await client.ConnectAsync(host, ChannelPort);
        Assert.False(result.IsSuccess);
        Assert.Equal(OnlineStage.ChannelConnect, result.Stage);
        Assert.Equal(OnlineFailure.ConnectFailed, result.Failure);
        Assert.Empty(addresses);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.True((await Bounded(client.ConnectAsync(ChannelPort))).IsSuccess);
    }

    [Fact]
    public async Task Connect_and_switch_ignore_the_whitespace_around_an_ip_literal()
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        Assert.True((await Bounded(client.ConnectAsync("  " + OverrideV4 + " ", ChannelPort))).IsSuccess);
        Assert.Equal(IPAddress.Parse(OverrideV4), addresses[^1]);
        Assert.True((await Bounded(client.SwitchChannelAsync(" " + OverrideV6 + "  ", AltChannelPort))).IsSuccess);
        Assert.Equal(IPAddress.Parse(OverrideV6), addresses[^1]);
    }

    [Fact]
    public async Task Switch_null_host_uses_gateway_address()
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        Assert.True((await Bounded(client.ConnectAsync(ChannelPort))).IsSuccess);
        addresses.Clear();
        Assert.True((await Bounded(client.SwitchChannelAsync(null, AltChannelPort))).IsSuccess);
        Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse(GatewayGameHost), addresses[0]);
    }

    [Fact]
    public async Task Switch_ipv4_and_ipv6_literals_override_gateway()
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        (OnlineClientSession client, _) = await rig.Connected();
        Assert.True((await Bounded(client.SwitchChannelAsync(OverrideV4, AltChannelPort))).IsSuccess);
        Assert.Equal(IPAddress.Parse(OverrideV4), addresses[^1]);
        Assert.True((await Bounded(client.SwitchChannelAsync(OverrideV6, ChannelPort))).IsSuccess);
        Assert.Equal(IPAddress.Parse(OverrideV6), addresses[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public async Task Switch_invalid_host_closes_old_link_and_returns_logged_in_retryable(string host)
    {
        (OnlineFlowRig rig, List<IPAddress> addresses) = TrackAddresses();
        (OnlineClientSession client, _) = await rig.Connected();
        int disposed = rig.Channels[0].Connection.DisposeCount;
        int factoryCallsBeforeSwitch = addresses.Count;
        OnlineConnectResult result = await client.SwitchChannelAsync(host, AltChannelPort);
        Assert.Equal(OnlineFailure.ConnectFailed, result.Failure);
        Assert.Equal(OnlineStage.ChannelConnect, result.Stage);
        Assert.Equal(factoryCallsBeforeSwitch, addresses.Count);
        Assert.Equal(disposed + 1, rig.Channels[0].Connection.DisposeCount);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.True((await Bounded(client.ConnectAsync(ChannelPort))).IsSuccess);
    }

    [Fact]
    public async Task Connect_before_login_still_order_error_for_host_overload()
    {
        OnlineClientSession client = new OnlineFlowRig().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(OverrideV4, ChannelPort));
    }

    [Fact]
    public async Task Switch_before_login_still_order_error_for_host_overload()
    {
        OnlineClientSession client = new OnlineFlowRig().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SwitchChannelAsync(OverrideV4, ChannelPort));
    }

    [Fact]
    public async Task Switch_in_exclusive_receive_still_order_error_for_host_overload()
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Selected();
        await client.EnterExclusiveReceiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SwitchChannelAsync(OverrideV4, ChannelPort));
    }
}