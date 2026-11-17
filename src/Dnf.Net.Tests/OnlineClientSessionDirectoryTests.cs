using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>分阶段会话:登录后拉频道目录,状态机与门闩隔离。</summary>
public sealed class OnlineClientSessionDirectoryTests
{
    private const int CustomDirectoryPort = 31021;
    private static string ExpectedLoginHost => GatewayTestSupport.Dots(198, 51, 100, 5);

    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static OnlineFlowRig RigWithLoginHost()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.GameServerIp = ExpectedLoginHost;
        return rig;
    }

    [Fact]
    public async Task Load_before_login_throws_order_error()
    {
        OnlineClientSession client = new OnlineFlowRig().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoadChannelDirectoryAsync());
    }

    [Fact]
    public async Task Load_after_dispose_throws_object_disposed()
    {
        OnlineClientSession client = RigWithLoginHost().CreateSession();
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.LoadChannelDirectoryAsync());
    }

    [Fact]
    public async Task Load_passes_login_host_and_default_port_to_hook()
    {
        var rig = RigWithLoginHost();
        string seenHost = null;
        int seenPort = -1;
        int calls = 0;
        rig.Hooks.DirectoryLoader = (host, port, ct) =>
        {
            calls++;
            seenHost = host;
            seenPort = port;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "合成失败"));
        };
        OnlineClientSession client = await rig.LoggedIn();
        ChannelDirectoryLoadResult result = await client.LoadChannelDirectoryAsync();
        Assert.Equal(1, calls);
        Assert.Equal(ExpectedLoginHost, seenHost);
        Assert.Equal(ChannelServiceConstants.DefaultPort, seenPort);
        Assert.Equal(ChannelDirectoryLoadOutcome.IoError, result.Outcome);
    }

    [Fact]
    public async Task Load_honors_custom_directory_port()
    {
        var rig = RigWithLoginHost();
        int seenPort = -1;
        rig.Hooks.DirectoryLoader = (host, port, ct) =>
        {
            seenPort = port;
            return Task.FromResult(ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.InvalidArgument, "合成参数"));
        };
        OnlineClientSession client = await rig.LoggedIn();
        await client.LoadChannelDirectoryAsync(CustomDirectoryPort);
        Assert.Equal(CustomDirectoryPort, seenPort);
    }

    [Theory]
    [InlineData(ChannelDirectoryLoadOutcome.Ok)]
    [InlineData(ChannelDirectoryLoadOutcome.IoError)]
    [InlineData(ChannelDirectoryLoadOutcome.ParseFailed)]
    [InlineData(ChannelDirectoryLoadOutcome.InvalidArgument)]
    public async Task Load_returns_hook_result_unchanged(ChannelDirectoryLoadOutcome outcome)
    {
        var rig = RigWithLoginHost();
        ChannelDirectorySnapshot snap = ChannelDirectorySnapshot.Build("0", true, null, null);
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(outcome switch
        {
            ChannelDirectoryLoadOutcome.Ok => ChannelDirectoryLoadResult.Success(snap, 0, true),
            ChannelDirectoryLoadOutcome.IoError => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "合成 I/O"),
            ChannelDirectoryLoadOutcome.ParseFailed => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.ParseFailed, "合成解析"),
            _ => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.InvalidArgument, "合成参数"),
        });
        OnlineClientSession client = await rig.LoggedIn();
        ChannelDirectoryLoadResult result = await client.LoadChannelDirectoryAsync();
        Assert.Equal(outcome, result.Outcome);
        if (outcome == ChannelDirectoryLoadOutcome.Ok)
        {
            Assert.Same(snap, result.Snapshot);
        }
    }

    [Theory]
    [InlineData(OnlineClientSessionState.LoggedIn, OnlineHeartbeatState.Disabled, false)]
    [InlineData(OnlineClientSessionState.Connected, OnlineHeartbeatState.Disabled, false)]
    [InlineData(OnlineClientSessionState.CharacterSelected, OnlineHeartbeatState.Running, true)]
    public async Task Load_does_not_change_state_or_heartbeat(OnlineClientSessionState state, OnlineHeartbeatState heartbeat,
        bool autoHeartbeat)
    {
        var rig = RigWithLoginHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hooks.DirectoryLoader = async (_, _, ct) =>
        {
            await entered.Task.WaitAsync(ct);
            return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "合成");
        };
        OnlineClientSession client = autoHeartbeat
            ? (await rig.Selected(autoHeartbeat: true)).Client
            : state switch
            {
                OnlineClientSessionState.LoggedIn => await rig.LoggedIn(),
                OnlineClientSessionState.Connected => (await rig.Connected()).Client,
                _ => (await rig.Selected()).Client,
            };
        Assert.Equal(state, client.State);
        Assert.Equal(heartbeat, client.HeartbeatState);
        Task<ChannelDirectoryLoadResult> loading = client.LoadChannelDirectoryAsync();
        await Task.Delay(30);
        Assert.Equal(state, client.State);
        Assert.Equal(heartbeat, client.HeartbeatState);
        entered.TrySetResult();
        await Bounded(loading);
    }

    [Fact]
    public async Task Load_failure_invokes_hook_once_without_retry()
    {
        var rig = RigWithLoginHost();
        int calls = 0;
        rig.Hooks.DirectoryLoader = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.ParseFailed, "一次"));
        };
        OnlineClientSession client = await rig.LoggedIn();
        ChannelDirectoryLoadResult result = await client.LoadChannelDirectoryAsync();
        Assert.Equal(1, calls);
        Assert.Equal(ChannelDirectoryLoadOutcome.ParseFailed, result.Outcome);
    }

    [Fact]
    public async Task Load_respects_caller_cancellation()
    {
        var rig = RigWithLoginHost();
        rig.Hooks.DirectoryLoader = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "不应到达");
        };
        OnlineClientSession client = await rig.LoggedIn();
        using var stop = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadChannelDirectoryAsync(cancellationToken: stop.Token));
    }

    [Fact]
    public async Task Dispose_cancels_in_flight_directory_load()
    {
        var rig = RigWithLoginHost();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hooks.DirectoryLoader = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "不应到达");
        };
        OnlineClientSession client = await rig.LoggedIn();
        Task<ChannelDirectoryLoadResult> loading = client.LoadChannelDirectoryAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(loading));
    }

    [Fact]
    public async Task In_flight_directory_load_does_not_block_connect()
    {
        var rig = RigWithLoginHost();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hooks.DirectoryLoader = async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "合成");
        };
        OnlineClientSession client = await rig.LoggedIn();
        Task<ChannelDirectoryLoadResult> loading = client.LoadChannelDirectoryAsync();
        OnlineConnectResult connected = await Bounded(client.ConnectAsync(31011));
        Assert.True(connected.IsSuccess);
        release.TrySetResult();
        await Bounded(loading);
    }
}