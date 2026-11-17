using System.Net;
using System.Net.Sockets;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 真后端接真会话(<see cref="OnlineClientSession"/>)的本地失败路径:不需要任何服务器就能走通的那几条——
/// 本地输入不合规(没发出请求)、网关连不上(本机一个刚关掉的端口,连接被拒)、会话的更替与释放、顺序错误。
/// 不连任何真实服务器:唯一的网络动作是连本机一个没人监听的端口。
/// </summary>
public sealed class OnlineShellRealSessionTests
{
    private static OnlineEntrySettings Template(int gatewayPort)
    {
        Assert.True(GatewayOptions.TryCreate(
            "http://localhost:" + gatewayPort,
            GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey()),
            TimeSpan.FromSeconds(2),
            out GatewayOptions options,
            out _));
        return new OnlineEntrySettings
        {
            Gateway = options,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = 7,
            ChannelPort = 31000,
        };
    }

    /// <summary>取一个刚被关掉的本机端口:连它一定被拒绝。</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task A_real_login_with_an_invalid_username_is_a_local_failure_with_its_own_category()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.LoginAsync("ab", "synthetic-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.InvalidUsername, result.Failure);
        Assert.Equal(0, result.RetryAfterSeconds);
        backend.Dispose();
    }

    [Fact]
    public async Task A_real_login_to_a_closed_port_is_a_network_failure_without_a_countdown()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        Assert.Equal(0, result.RetryAfterSeconds);
        backend.Dispose();
    }

    [Fact]
    public async Task A_real_register_through_the_http_transport_to_a_closed_port_is_a_network_failure()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.RegisterAsync("new_player", "synthetic-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        Assert.False(backend.HasSession);
        backend.Dispose();
    }

    [Fact]
    public async Task A_real_change_password_to_a_closed_port_is_a_network_failure()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.ChangePasswordAsync("player_01", "synthetic-pw", "another-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        backend.Dispose();
    }

    [Fact]
    public async Task The_real_session_stays_reachable_after_a_failed_login_and_is_released_by_disconnect()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession session = backend.Session;
        Assert.NotNull(session);
        Assert.True(backend.HasSession);

        backend.Disconnect();

        Assert.Null(backend.Session);
        Assert.False(backend.HasSession);
        Assert.True(SpinWait.SpinUntil(() => session.State == OnlineClientSessionState.Disposed, 5000));
        backend.Dispose();
    }

    [Fact]
    public async Task Connecting_on_a_session_that_never_logged_in_faults_instead_of_pretending_to_work()
    {
        // 顺序错误是编程错误:真会话抛 InvalidOperationException,外壳把任何意外异常都显示成"意外错误"
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ConnectChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SwitchChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SelectCharacterAsync(0, default));
        backend.Dispose();
    }

    [Fact]
    public async Task Disconnect_never_raises_the_disconnected_event_for_a_real_session()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        int lost = 0;
        backend.Disconnected += () => Interlocked.Increment(ref lost);
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession session = backend.Session;

        backend.Disconnect();
        Assert.True(SpinWait.SpinUntil(() => session.State == OnlineClientSessionState.Disposed, 5000));

        Assert.Equal(0, Volatile.Read(ref lost));
        backend.Dispose();
    }

    [Fact]
    public async Task A_second_real_login_replaces_the_session_and_releases_the_first()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession first = backend.Session;

        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession second = backend.Session;

        Assert.NotSame(first, second);
        Assert.True(SpinWait.SpinUntil(() => first.State == OnlineClientSessionState.Disposed, 5000));
        Assert.NotEqual(OnlineClientSessionState.Disposed, second.State);
        backend.Dispose();
        Assert.Equal(OnlineClientSessionState.Disposed, second.State);
    }

    [Fact]
    public async Task Disposing_the_backend_releases_the_real_session_before_returning()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession session = backend.Session;

        backend.Dispose();

        Assert.Equal(OnlineClientSessionState.Disposed, session.State);
        Assert.Null(backend.Session);
    }
}
