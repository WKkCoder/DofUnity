using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网外壳真后端(<see cref="OnlineShellBackend"/>)的会话槽与选角回报的逐项测试:
/// 联网层选角结果到后端回报的映射(成功带快照日志与三份载荷、失败只带"能否换槽再试")、
/// 槽上三份进城载荷(选角回报、进城快照、副本许可)的保存、失败时清空、换频道时清空、被放弃的槽不再接收迟到的选角结果,
/// 以及主动退出时的优雅断线(只关到频道的那条连接、不发 <c>1/3</c>:恰好关一次并带着调用者的取消令牌、先摘断线订阅、槽被放弃与释放、任何异常都吞掉、没有会话是空操作)。
/// 全部跑在内存假会话上;"真会话"是一个什么都没连的真对象,关闭那一步用测试接缝换成记录用的实现。
/// </summary>
public sealed class OnlineShellBackendSlotTests
{
    private sealed class Rig : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<string> _logs = new();

        public Rig(bool realSession = false, Func<int, CancellationToken, Task<OnlineSelectReport>> select = null, bool throwingLog = false)
        {
            var kit = new BackendRig(configured: true);
            Backend = new OnlineShellBackend(
                kit.Template,
                line =>
                {
                    lock (_sync)
                    {
                        _logs.Add(line);
                    }
                    if (throwingLog)
                    {
                        throw new InvalidOperationException("synthetic log sink failure");
                    }
                },
                settings =>
                {
                    var inner = new FakeSessionPort(settings);
                    if (select != null)
                    {
                        inner.OnSelect = select;
                    }
                    var port = new TownPort(inner, realSession ? SessionProbe.NewUnconnected() : null);
                    lock (_sync)
                    {
                        Ports.Add(port);
                    }
                    return port;
                },
                options => new TrackingTransport(kit.Gateway));
        }

        public OnlineShellBackend Backend { get; }

        public List<TownPort> Ports { get; } = new();

        public async Task LoggedInAndConnected(int channelPort = 31000)
        {
            OnlineAuthOutcome login = await GatewayTestSupport.Bounded(Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
            Assert.True(login.Success);
            OnlineConnectOutcome connect = await GatewayTestSupport.Bounded(Backend.ConnectChannelAsync(new ChannelTarget(channelPort), default));
            Assert.True(connect.Success);
        }

        public Task<OnlineSelectOutcome> Select(int slot) => GatewayTestSupport.Bounded(Backend.SelectCharacterAsync(slot, default));

        public string[] Logs()
        {
            lock (_sync)
            {
                return _logs.ToArray();
            }
        }

        public void Dispose() => Backend.Dispose();
    }

    private static OnlineSelectReport Entered(SelectCharacterResult load, TownEntrySnapshot snapshot, DungeonPermissionSnapshot permissions, string line = "DNF_NET snapshot synthetic-line")
    {
        return new OnlineSelectReport(true, false, line, load, snapshot, permissions);
    }

    // ------------------------------------------------------------------ 选角结果 → 回报

    [Fact]
    public void A_successful_select_result_maps_to_a_report_carrying_the_flags_the_log_line_and_the_three_payloads()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4311, 3);
        TownEntrySnapshot snapshot = NetObjects.Snapshot(slot: 0, level: 55, job: 0, growType: 2, enterComplete: true, framesSeen: 120, framesParsed: 118, framesMalformed: 1);
        DungeonPermissionSnapshot permissions = NetObjects.Permissions((901, 1), (902, 2));

        OnlineSelectReport reportWithRetry = RealOnlineSessionPort.ToReport(NetObjects.SelectResult(true, true, 0, load, snapshot, permissions));
        OnlineSelectReport reportWithoutRetry = RealOnlineSessionPort.ToReport(NetObjects.SelectResult(true, false, 0, load, snapshot, permissions));

        Assert.True(reportWithRetry.Entered);
        Assert.True(reportWithRetry.MayTryAnotherSlot);
        Assert.Equal("DNF_NET snapshot slot=0 level=55 job=0 growType=2 enterComplete=1 seen=120 parsed=118 malformed=1", reportWithRetry.SnapshotLine);
        Assert.Same(load, reportWithRetry.SelectCharacter);
        Assert.Same(snapshot, reportWithRetry.Snapshot);
        Assert.Same(permissions, reportWithRetry.DungeonPermissions);
        Assert.True(reportWithoutRetry.Entered);
        Assert.False(reportWithoutRetry.MayTryAnotherSlot);
    }

    [Fact]
    public void A_failed_select_result_maps_to_a_report_with_only_the_retry_flag()
    {
        OnlineSelectReport mayRetry = RealOnlineSessionPort.ToReport(NetObjects.SelectResult(false, true, 1, null, null, null));
        OnlineSelectReport mayNot = RealOnlineSessionPort.ToReport(NetObjects.SelectResult(false, false, 1, null, null, null));

        Assert.False(mayRetry.Entered);
        Assert.True(mayRetry.MayTryAnotherSlot);
        Assert.Null(mayRetry.SnapshotLine);
        Assert.Null(mayRetry.SelectCharacter);
        Assert.Null(mayRetry.Snapshot);
        Assert.Null(mayRetry.DungeonPermissions);
        Assert.False(mayNot.Entered);
        Assert.False(mayNot.MayTryAnotherSlot);
    }

    // ------------------------------------------------------------------ 槽上的三份载荷

    [Fact]
    public async Task A_successful_select_keeps_the_three_payloads_and_a_failed_select_clears_all_of_them()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4311, 3);
        TownEntrySnapshot snapshot = NetObjects.Snapshot(0, 55, 0, 2);
        DungeonPermissionSnapshot permissions = NetObjects.Permissions((901, 1), (902, 2));
        int calls = 0;
        using var rig = new Rig(select: (slot, ct) => Task.FromResult(
            calls++ == 0 ? Entered(load, snapshot, permissions) : new OnlineSelectReport(false, true, null)));
        await rig.LoggedInAndConnected();
        Assert.Null(rig.Backend.SelectCharacter);
        Assert.Null(rig.Backend.Snapshot);
        Assert.Null(rig.Backend.DungeonPermissions);

        OnlineSelectOutcome ok = await rig.Select(0);

        Assert.True(ok.Entered);
        Assert.Same(load, rig.Backend.SelectCharacter);
        Assert.Same(snapshot, rig.Backend.Snapshot);
        Assert.Same(permissions, rig.Backend.DungeonPermissions);

        OnlineSelectOutcome failed = await rig.Select(1);

        Assert.False(failed.Entered);
        Assert.True(failed.MayTryAnotherSlot);
        Assert.Null(rig.Backend.SelectCharacter);
        Assert.Null(rig.Backend.Snapshot);
        Assert.Null(rig.Backend.DungeonPermissions);
    }

    [Fact]
    public async Task A_failed_select_that_may_not_be_retried_is_reported_as_such_and_logs_nothing()
    {
        using var rig = new Rig(select: (slot, ct) => Task.FromResult(new OnlineSelectReport(false, false, null)));
        await rig.LoggedInAndConnected();

        OnlineSelectOutcome failed = await rig.Select(0);

        Assert.False(failed.Entered);
        Assert.False(failed.MayTryAnotherSlot);
        Assert.Empty(rig.Logs());
    }

    [Fact]
    public async Task Only_a_successful_select_writes_its_snapshot_line_to_the_log()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4312, 3);
        int calls = 0;
        using var rig = new Rig(select: (slot, ct) => Task.FromResult(
            calls++ == 0 ? Entered(load, null, null, "DNF_NET snapshot first-line") : new OnlineSelectReport(false, true, "DNF_NET snapshot ignored-line")));
        await rig.LoggedInAndConnected();

        await rig.Select(0);
        await rig.Select(1);

        Assert.Equal(new[] { "DNF_NET snapshot first-line" }, rig.Logs());
    }

    [Fact]
    public async Task A_successful_select_without_a_snapshot_line_writes_nothing_to_the_log()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4317, 3);
        using var rig = new Rig(select: (slot, ct) => Task.FromResult(new OnlineSelectReport(true, false, null, load, null, null)));
        await rig.LoggedInAndConnected();

        OnlineSelectOutcome outcome = await rig.Select(0);

        Assert.True(outcome.Entered);
        Assert.Same(load, rig.Backend.SelectCharacter);
        Assert.Empty(rig.Logs());
    }

    [Fact]
    public async Task A_log_sink_that_throws_never_breaks_a_select()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4316, 3);
        using var rig = new Rig(throwingLog: true, select: (slot, ct) => Task.FromResult(Entered(load, null, null, "DNF_NET snapshot throwing-line")));
        await rig.LoggedInAndConnected();

        OnlineSelectOutcome outcome = await rig.Select(0);

        Assert.True(outcome.Entered);
        Assert.Same(load, rig.Backend.SelectCharacter);
        Assert.Contains("DNF_NET snapshot throwing-line", rig.Logs());
    }

    [Fact]
    public async Task A_select_whose_token_is_already_cancelled_never_reaches_the_port()
    {
        int asked = 0;
        using var rig = new Rig(select: (slot, ct) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(Entered(null, null, null));
        });
        await rig.LoggedInAndConnected();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Task<OnlineSelectOutcome> task = rig.Backend.SelectCharacterAsync(0, cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayTestSupport.Bounded(task));
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task Selecting_without_a_session_or_after_the_session_was_dropped_faults()
    {
        using var rig = new Rig();

        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayTestSupport.Bounded(rig.Backend.SelectCharacterAsync(0, default)));

        await rig.LoggedInAndConnected();
        rig.Backend.Disconnect();

        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayTestSupport.Bounded(rig.Backend.SelectCharacterAsync(0, default)));
    }

    [Fact]
    public async Task Connecting_or_switching_channel_clears_every_payload_of_the_previous_entry()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4313, 3);
        TownEntrySnapshot snapshot = NetObjects.Snapshot(0, 55, 0, 2);
        DungeonPermissionSnapshot permissions = NetObjects.Permissions((903, 3));
        using var rig = new Rig(select: (slot, ct) => Task.FromResult(Entered(load, snapshot, permissions)));
        await rig.LoggedInAndConnected();

        await rig.Select(0);
        Assert.NotNull(rig.Backend.SelectCharacter);
        Assert.NotNull(rig.Backend.Snapshot);
        Assert.NotNull(rig.Backend.DungeonPermissions);
        OnlineConnectOutcome switched = await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(new ChannelTarget(31001), default));
        Assert.True(switched.Success);
        Assert.Null(rig.Backend.SelectCharacter);
        Assert.Null(rig.Backend.Snapshot);
        Assert.Null(rig.Backend.DungeonPermissions);

        await rig.Select(0);
        Assert.NotNull(rig.Backend.SelectCharacter);
        Assert.NotNull(rig.Backend.Snapshot);
        Assert.NotNull(rig.Backend.DungeonPermissions);
        OnlineConnectOutcome connected = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31002), default));
        Assert.True(connected.Success);
        Assert.Null(rig.Backend.SelectCharacter);
        Assert.Null(rig.Backend.Snapshot);
        Assert.Null(rig.Backend.DungeonPermissions);
    }

    [Fact]
    public async Task A_select_result_that_arrives_after_the_slot_was_dropped_is_not_stored()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4314, 3);
        var gate = new TaskCompletionSource<OnlineSelectReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(select: (slot, ct) => gate.Task);
        await rig.LoggedInAndConnected();
        Task<OnlineSelectOutcome> pending = rig.Backend.SelectCharacterAsync(0, default);

        rig.Backend.Disconnect();
        gate.SetResult(Entered(load, NetObjects.Snapshot(0, 55, 0, 2), NetObjects.Permissions((904, 1))));
        OnlineSelectOutcome outcome = await GatewayTestSupport.Bounded(pending);

        Assert.True(outcome.Entered);
        Assert.False(rig.Backend.HasSession);
        Assert.Null(rig.Backend.SelectCharacter);
        Assert.Null(rig.Backend.Snapshot);
        Assert.Null(rig.Backend.DungeonPermissions);
    }

    [Fact]
    public async Task A_select_result_that_arrives_after_a_newer_login_replaced_the_slot_is_not_stored()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4315, 3);
        var gate = new TaskCompletionSource<OnlineSelectReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(select: (slot, ct) => gate.Task);
        await rig.LoggedInAndConnected();
        Task<OnlineSelectOutcome> pending = rig.Backend.SelectCharacterAsync(0, default);

        OnlineAuthOutcome relogin = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(relogin.Success);
        gate.SetResult(Entered(load, NetObjects.Snapshot(0, 55, 0, 2), NetObjects.Permissions((905, 1))));
        await GatewayTestSupport.Bounded(pending);

        Assert.True(rig.Backend.HasSession);
        Assert.Null(rig.Backend.SelectCharacter);
        Assert.Null(rig.Backend.Snapshot);
        Assert.Null(rig.Backend.DungeonPermissions);
    }

    // ------------------------------------------------------------------ 优雅断线

    [Fact]
    public async Task Graceful_disconnect_closes_the_real_session_once_with_the_callers_token_after_detaching_the_lost_handler()
    {
        using var rig = new Rig(realSession: true);
        await rig.LoggedInAndConnected();
        TownPort port = rig.Ports[0];
        OnlineClientSession real = rig.Backend.Session;
        Assert.Same(port.Real, real);
        Assert.True(port.Inner.HasLostSubscribers);
        var calls = new List<(OnlineClientSession Session, CancellationToken Token, bool LostHandlerStillAttached, bool HasSession)>();
        rig.Backend.SessionDisconnect = (session, token) =>
        {
            calls.Add((session, token, port.Inner.HasLostSubscribers, rig.Backend.HasSession));
            return Task.CompletedTask;
        };
        int disconnectedEvents = 0;
        rig.Backend.Disconnected += () => disconnectedEvents++;
        using var cts = new CancellationTokenSource();

        await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync(cts.Token));

        var call = Assert.Single(calls);
        Assert.Same(real, call.Session);
        Assert.Equal(cts.Token, call.Token);
        Assert.False(call.LostHandlerStillAttached);
        Assert.False(call.HasSession);
        Assert.False(rig.Backend.HasSession);
        Assert.Null(rig.Backend.Session);
        Assert.True(port.Inner.WaitDisposed());
        Assert.Equal(1, port.Inner.DisposeCount);
        // 放弃之后,会话再报断线也不算数
        port.Inner.RaiseLost();
        Assert.Equal(0, disconnectedEvents);
    }

    [Fact]
    public async Task Graceful_disconnect_is_a_no_op_the_second_time_and_when_there_never_was_a_session()
    {
        using var rig = new Rig(realSession: true);
        var calls = new List<OnlineClientSession>();
        rig.Backend.SessionDisconnect = (session, token) =>
        {
            calls.Add(session);
            return Task.CompletedTask;
        };

        await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync());
        Assert.Empty(calls);
        Assert.Empty(rig.Ports);

        await rig.LoggedInAndConnected();
        await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync());
        await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync());

        Assert.Single(calls);
        Assert.Single(rig.Ports);
    }

    [Fact]
    public async Task Graceful_disconnect_without_a_real_session_never_calls_the_close_and_still_releases_the_slot()
    {
        using var rig = new Rig(realSession: false);
        await rig.LoggedInAndConnected();
        var calls = new List<OnlineClientSession>();
        rig.Backend.SessionDisconnect = (session, token) =>
        {
            calls.Add(session);
            return Task.CompletedTask;
        };
        Assert.True(rig.Backend.HasSession);

        await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync());

        Assert.Empty(calls);
        Assert.False(rig.Backend.HasSession);
        Assert.True(rig.Ports[0].Inner.WaitDisposed());
        Assert.False(rig.Ports[0].Inner.HasLostSubscribers);
    }

    [Fact]
    public async Task Graceful_disconnect_swallows_whatever_the_close_throws_and_still_releases_the_session()
    {
        Func<Exception>[] failures =
        {
            () => new IOException("synthetic close failure"),
            () => new InvalidOperationException("synthetic state failure"),
            () => new OperationCanceledException(),
            () => new TimeoutException("synthetic timeout"),
            () => new NotSupportedException("synthetic unsupported"),
        };

        foreach (Func<Exception> failure in failures)
        {
            // 异步失败(任务出错)与同步失败(调用时就抛)各来一遍
            foreach (bool synchronous in new[] { false, true })
            {
                using var rig = new Rig(realSession: true);
                await rig.LoggedInAndConnected();
                rig.Backend.SessionDisconnect = (session, token) =>
                {
                    if (synchronous)
                    {
                        throw failure();
                    }
                    return Task.FromException(failure());
                };

                await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync());

                Assert.False(rig.Backend.HasSession);
                Assert.True(rig.Ports[0].Inner.WaitDisposed());
            }
        }
    }

    [Fact]
    public async Task Graceful_disconnect_waits_for_the_close_to_finish_before_it_completes()
    {
        using var rig = new Rig(realSession: true);
        await rig.LoggedInAndConnected();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Backend.SessionDisconnect = (session, token) => gate.Task;

        Task graceful = rig.Backend.DisconnectGracefullyAsync();

        Assert.False(graceful.IsCompleted);
        Assert.False(rig.Backend.HasSession);
        gate.SetResult(true);
        await Waits.Bounded(graceful);
        Assert.True(rig.Ports[0].Inner.WaitDisposed());
    }

    [Fact]
    public async Task By_default_the_close_goes_to_the_real_session_which_has_nothing_to_close_before_connecting()
    {
        using var rig = new Rig(realSession: true);
        await rig.LoggedInAndConnected();
        OnlineClientSession real = rig.Backend.Session;

        await Waits.Bounded(rig.Backend.DisconnectGracefullyAsync());

        Assert.False(rig.Backend.HasSession);
        Assert.True(rig.Ports[0].Inner.WaitDisposed());
        Assert.Equal(OnlineClientSessionState.Disposed, real.State);
    }
}
