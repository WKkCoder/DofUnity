using System.Net;
using System.Net.Sockets;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 分阶段会话:连频道/换频道的主机字面量重载与非法主机失败不变量;
/// 以及默认的频道打开器(不设 ChannelFactory 钩子、走真套接字)连的是传进来的主机而不是会话自己的游戏服地址。
/// </summary>
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

    // ---------- 默认的频道打开器(不设 ChannelFactory 钩子):连的必须是传进来的主机,不是会话自己的游戏服地址 ----------
    // 上面所有用例都用 ChannelFactory 钩子绕开了真套接字;这几条把钩子去掉,让默认打开器真的去连一个只监听回环地址的"频道端"。
    // 会话自己的游戏服地址是文档网段(谁也连不上),所以只有"连的就是传进来的那个主机"时,监听端才接得到连接。
    // 沙箱里不能绑本地套接字(Permission denied):这几条在那里失败是预期的,要在能绑回环端口的环境里跑。

    /// <summary>监听端等连接的时限:连对了主机,回环上几毫秒就到;连错了地址,连接永远不会来,到点用例失败而不是卡住。</summary>
    private static readonly TimeSpan AcceptLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 只监听回环地址的"频道端":等一个连接进来,交给调用方后直接关掉(会话那头随后的握手失败无所谓,这几条用例只看"连到了没有")。
    /// </summary>
    private sealed class LoopbackChannelEndpoint : IDisposable
    {
        private readonly TcpListener _listener;

        private LoopbackChannelEndpoint(TcpListener listener, IPAddress address)
        {
            _listener = listener;
            Host = address.ToString();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>传给会话当 host 参数的 IP 字面量(IPv4 环回或 <c>::1</c>)。</summary>
        public string Host { get; }

        public int Port { get; }

        /// <summary>
        /// 在 IPv4 环回(<paramref name="ipv6"/> 为 false)或 IPv6 环回上开监听。本机没有 IPv6 环回时返回 null(那个组合不适用);
        /// 其它套接字错误(比如沙箱里的 Permission denied)原样抛出,让用例失败,而不是悄悄通过。
        /// </summary>
        public static LoopbackChannelEndpoint Start(bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6)
            {
                return null;
            }
            IPAddress address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            TcpListener listener = new(address, 0);
            try
            {
                listener.Start();
            }
            catch (SocketException e) when (ipv6 && (e.SocketErrorCode == SocketError.AddressNotAvailable
                || e.SocketErrorCode == SocketError.AddressFamilyNotSupported))
            {
                listener.Stop();
                return null;
            }
            return new LoopbackChannelEndpoint(listener, address);
        }

        public Task<TcpClient> AcceptAsync()
        {
            return _listener.AcceptTcpClientAsync();
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }

    /// <summary>
    /// 用 <paramref name="start"/> 发起一次连/换频道(默认打开器、真套接字),要求 <paramref name="endpoint"/> 在时限内接到一个连接。
    /// 接到就算过(随后握手失败无所谓);时限内没接到(会话连去了别的地址)就断言失败。
    /// 不管成败,这次连接最后都取消并等它收尾(带超时),不留后台任务。
    /// </summary>
    private static async Task AssertReachedAsync(LoopbackChannelEndpoint endpoint, Func<CancellationToken, Task<OnlineConnectResult>> start)
    {
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        Task<TcpClient> accepting = endpoint.AcceptAsync();
        Task<OnlineConnectResult> connecting = start(stop.Token);
        try
        {
            using CancellationTokenSource timer = new();
            await Task.WhenAny(accepting, connecting, Task.Delay(AcceptLimit, timer.Token));
            timer.Cancel();
            Assert.True(accepting.IsCompleted, "监听端在 " + AcceptLimit.TotalSeconds + " 秒内没有接到连接:会话连的不是传进来的主机");
            using TcpClient accepted = await accepting;
        }
        finally
        {
            stop.Cancel();
            try
            {
                await GatewayTestSupport.Bounded(connecting, 10);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_with_a_host_literal_reaches_that_host_through_the_default_channel_opener(bool ipv6)
    {
        using LoopbackChannelEndpoint endpoint = LoopbackChannelEndpoint.Start(ipv6);
        if (endpoint == null)
        {
            return;     // 本机没有 IPv6 环回:没有地方可监听,这个组合不适用(IPv4 那一条照常跑)
        }
        OnlineFlowRig rig = new();
        rig.Hooks.ChannelFactory = null;                                           // 关键:不走测试钩子,默认的真套接字打开器才会被走到
        using OnlineClientSession client = await LoggedInWithGatewayHost(rig);     // 会话自己的游戏服地址:文档网段,连不上
        await AssertReachedAsync(endpoint, ct => client.ConnectAsync(endpoint.Host, endpoint.Port, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switch_channel_with_a_host_literal_reaches_that_host_through_the_default_channel_opener(bool ipv6)
    {
        using LoopbackChannelEndpoint endpoint = LoopbackChannelEndpoint.Start(ipv6);
        if (endpoint == null)
        {
            return;     // 本机没有 IPv6 环回:没有地方可监听,这个组合不适用(IPv4 那一条照常跑)
        }
        OnlineFlowRig rig = new();
        rig.Hooks.ChannelFactory = null;
        using OnlineClientSession client = await LoggedInWithGatewayHost(rig);
        await AssertReachedAsync(endpoint, ct => client.SwitchChannelAsync(endpoint.Host, endpoint.Port, ct));
    }

    [Fact]
    public async Task Connect_without_a_host_reaches_the_gateway_address_through_the_default_channel_opener()
    {
        // 对照:不给主机时默认打开器连的是会话自己的游戏服地址(这回它就是监听端),用来证明上面两条的装置本身是通的
        using LoopbackChannelEndpoint endpoint = LoopbackChannelEndpoint.Start(false);
        OnlineFlowRig rig = new();
        rig.Hooks.ChannelFactory = null;
        rig.FakeGateway.GameServerIp = endpoint.Host;
        using OnlineClientSession client = await rig.LoggedIn();
        await AssertReachedAsync(endpoint, ct => client.ConnectAsync(null, endpoint.Port, ct));
    }
}