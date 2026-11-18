using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网外壳宿主(<see cref="OnlineShellHost"/>)的"退出游戏"收尾与进城载荷转发的逐项测试:
/// <see cref="OnlineShellHost.DisconnectForQuitAsync"/> 一开始就把宿主置为已释放(进行中的每帧推进与再次退出都是空操作)、
/// 经后端关闭到频道的连接恰好一次(不发 <c>1/3</c>)并带着调用者的取消令牌、关闭外壳阶段机、释放后端(之后网络操作都失败);
/// 以及宿主的选角回报、进城快照与副本许可都原样转发后端当前的那一份。全部跑在内存假会话上。
/// </summary>
public sealed class OnlineShellHostQuitTests
{
    /// <summary>卡在"关闭到频道的连接"那一步的退出:返回时那一步已经进入、还没放行。</summary>
    private sealed class GatedQuit
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(OnlineClientSession Session, CancellationToken Token)> _calls = new();

        public GatedQuit(TownHostRig rig, CancellationToken token = default)
        {
            rig.Backend.SessionDisconnect = (session, callToken) =>
            {
                lock (_calls)
                {
                    _calls.Add((session, callToken));
                }
                return _release.Task;
            };
            Quit = rig.Host.DisconnectForQuitAsync(token);
        }

        public Task Quit { get; }

        public (OnlineClientSession Session, CancellationToken Token)[] Calls()
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }

        public void Release() => _release.SetResult(true);
    }

    [Fact]
    public async Task A_quit_in_progress_already_counts_as_disposed_for_ticks_and_for_a_second_quit()
    {
        using var rig = new TownHostRig(realSession: true);
        rig.EnterInGame();
        var gated = new GatedQuit(rig);
        Assert.False(gated.Quit.IsCompleted);
        Assert.Single(gated.Calls());

        // 退出还卡在关闭连接上:此刻来的"外壳阶段机该处理的事"(通知断线 + 每帧推进)不会被处理
        rig.Host.Flow.NotifyDisconnected();
        rig.Tick();
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
        Assert.Null(rig.Host.Flow.Notice);
        Task second = rig.Host.DisconnectForQuitAsync();
        Assert.True(second.IsCompleted);
        await Waits.Bounded(second);
        Assert.Single(gated.Calls());

        gated.Release();
        await Waits.Bounded(gated.Quit);
        Assert.Single(gated.Calls());
    }

    [Fact]
    public async Task Quitting_closes_the_channel_connection_once_with_the_callers_token_and_ticks_stay_no_ops_afterwards()
    {
        using var rig = new TownHostRig(realSession: true);
        rig.EnterInGame();
        OnlineClientSession real = rig.Host.Session;
        Assert.NotNull(real);
        using var cts = new CancellationTokenSource();
        var gated = new GatedQuit(rig, cts.Token);
        gated.Release();

        await Waits.Bounded(gated.Quit);
        await Waits.Bounded(rig.Host.DisconnectForQuitAsync(cts.Token));

        var call = Assert.Single(gated.Calls());
        Assert.Same(real, call.Session);
        Assert.Equal(cts.Token, call.Token);
        Assert.False(rig.Backend.HasSession);
        Assert.Null(rig.Host.Session);
        Assert.True(rig.Port(0).Inner.WaitDisposed());
        rig.Host.Flow.NotifyDisconnected();
        rig.Tick();
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
    }

    [Fact]
    public async Task Quitting_shuts_the_shell_flow_down_so_it_ignores_every_later_event()
    {
        using var rig = new TownHostRig();
        rig.EnterInGame();

        await Waits.Bounded(rig.Host.DisconnectForQuitAsync());

        // 不经宿主的每帧推进,直接对阶段机来:关闭之后它不处理断线通知,也不转阶段、不给提示
        rig.Host.Flow.NotifyDisconnected();
        rig.Host.Flow.Poll(rig.Now += 16);
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
        Assert.Null(rig.Host.Flow.Notice);
    }

    [Fact]
    public async Task Quitting_disposes_the_backend_so_it_refuses_any_further_login()
    {
        using var rig = new TownHostRig(realSession: true);
        rig.EnterInGame();

        await Waits.Bounded(rig.Host.DisconnectForQuitAsync());

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            Waits.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default)));
        Assert.False(rig.Backend.HasSession);
    }

    [Fact]
    public async Task Quitting_without_a_real_session_still_shuts_the_flow_down_and_disposes_the_backend()
    {
        using var rig = new TownHostRig();
        rig.EnterInGame();
        var closes = 0;
        rig.Backend.SessionDisconnect = (session, token) =>
        {
            closes++;
            return Task.CompletedTask;
        };

        await Waits.Bounded(rig.Host.DisconnectForQuitAsync());

        Assert.Equal(0, closes);
        Assert.True(rig.Port(0).Inner.WaitDisposed());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            Waits.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default)));
    }

    [Fact]
    public async Task Quitting_a_host_that_was_already_disposed_does_nothing()
    {
        using var rig = new TownHostRig(realSession: true);
        rig.EnterInGame();
        var closes = 0;
        rig.Backend.SessionDisconnect = (session, token) =>
        {
            closes++;
            return Task.CompletedTask;
        };
        rig.Host.Dispose();

        await Waits.Bounded(rig.Host.DisconnectForQuitAsync());

        Assert.Equal(0, closes);
        rig.Host.Dispose();
    }

    [Fact]
    public void The_hosts_select_character_snapshot_and_permissions_are_the_backends_current_ones()
    {
        SelectCharacterResult load = OnlineTownHostTests.SelectPayload(4321, 1);
        TownEntrySnapshot snapshot = NetObjects.Snapshot(0, 55, 0, 2);
        DungeonPermissionSnapshot permissions = NetObjects.Permissions((906, 1), (907, 2));
        using var rig = new TownHostRig(select: slot => new OnlineSelectReport(true, false, null, load, snapshot, permissions), realSession: true);
        Assert.Null(rig.Host.SelectCharacter);
        Assert.Null(rig.Host.Snapshot);
        Assert.Null(rig.Host.DungeonPermissions);
        Assert.Null(rig.Host.Session);

        rig.EnterInGame();

        Assert.NotNull(rig.Host.Snapshot);
        Assert.NotNull(rig.Host.DungeonPermissions);
        Assert.Same(snapshot, rig.Host.Snapshot);
        Assert.Same(permissions, rig.Host.DungeonPermissions);
        Assert.Same(load, rig.Host.SelectCharacter);
        Assert.Same(rig.Backend.Snapshot, rig.Host.Snapshot);
        Assert.Same(rig.Backend.DungeonPermissions, rig.Host.DungeonPermissions);
        Assert.Same(rig.Backend.SelectCharacter, rig.Host.SelectCharacter);
        Assert.Same(rig.Port(0).Real, rig.Host.Session);
    }

    [Fact]
    public void A_plain_dispose_makes_later_ticks_no_ops_and_is_idempotent()
    {
        using var rig = new TownHostRig();
        rig.EnterInGame();

        rig.Host.Dispose();
        rig.Host.Dispose();
        rig.Host.Flow.NotifyDisconnected();
        rig.Tick();

        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
        Assert.Null(rig.Host.Flow.Notice);
    }
}
