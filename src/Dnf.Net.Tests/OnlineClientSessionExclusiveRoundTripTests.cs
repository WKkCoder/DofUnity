using System.Collections.Concurrent;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// N3a 收包权往返(<see cref="OnlineClientSession.LeaveExclusiveReceiveAsync"/>)与端到端往返测试。
/// 全部用假网关与内存管道(确定性同步点)，编号全为合成值(900+/9100+)。
/// </summary>
public sealed class OnlineClientSessionExclusiveRoundTripTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static async Task Bounded(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, finished);
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        await Bounded(Task.Run(async () =>
        {
            while (!condition()) await Task.Delay(5);
            return 0;
        }));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("logged-in")]
    [InlineData("connected")]
    [InlineData("selected")]
    public async Task LeaveExclusiveReceiveAsync_is_rejected_in_every_non_exclusive_state(string state)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client = state switch
        {
            "created" => rig.CreateSession(),
            "logged-in" => await rig.LoggedIn(),
            "connected" => (await rig.Connected()).Client,
            _ => (await rig.Selected(autoHeartbeat: true)).Client,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LeaveExclusiveReceiveAsync());
        client.Dispose();
    }

    [Fact]
    public async Task LeaveExclusiveReceiveAsync_after_dispose_throws_ObjectDisposedException_and_respects_pre_cancellation()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        await Bounded(client.EnterExclusiveReceiveAsync());

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LeaveExclusiveReceiveAsync(cancelled.Token));
        }
        Assert.Equal(OnlineClientSessionState.ExclusiveReceive, client.State);

        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.LeaveExclusiveReceiveAsync());
    }

    [Fact]
    public async Task Five_round_trips_preserve_state_single_reader_and_existing_subscriptions()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;
        var seen = new ConcurrentQueue<int>();
        client.FrameReceived += f => seen.Enqueue(f.PacketId);

        int expectedHeartbeats = 0;
        for (int cycle = 0; cycle < 5; cycle++)
        {
            await Bounded(client.EnterExclusiveReceiveAsync());
            Assert.Equal(OnlineClientSessionState.ExclusiveReceive, client.State);
            Assert.Equal(OnlineHeartbeatState.ExclusiveReceive, client.HeartbeatState);

            // 独占期间推一帧非心跳,由独占收包端读走
            pipe.Push(DungeonEntryWire.Notice((ushort)(910 + cycle), new byte[] { (byte)cycle }));
            List<DecodedFrame> mine = await Bounded(client.GameSession.ReceiveAsync(TimeSpan.FromMilliseconds(200), 1));
            Assert.Single(mine);
            Assert.Equal(910 + cycle, mine[0].Header.PacketId);

            await Bounded(client.LeaveExclusiveReceiveAsync());
            Assert.Equal(OnlineClientSessionState.CharacterSelected, client.State);
            Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);

            // 每轮切回后发 1 个心跳只得恰好 1 个回包(证明没有双读者/双泵),且原有订阅仍然有效
            pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
            expectedHeartbeats++;
            await WaitUntil(() => pipe.ClientFrames.Count(f => f.Id == 0) == expectedHeartbeats);
            await Task.Delay(40);
            Assert.Equal(expectedHeartbeats, pipe.ClientFrames.Count(f => f.Id == 0));

            int beforeCount = seen.Count;
            pipe.Push(DungeonEntryWire.Notice((ushort)(950 + cycle), new byte[] { 1 }));
            await WaitUntil(() => seen.Count == beforeCount + 1);
            Assert.Contains(950 + cycle, seen);
        }
        client.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeaveExclusiveReceiveAsync_restores_heartbeat_switch_as_it_was_before_entering(bool manualDisable)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        // 两种关闭起点:AutoHeartbeat=false 或进入前手动 SetHeartbeatAsync(false)
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: manualDisable);
        if (manualDisable)
        {
            await Bounded(client.SetHeartbeatAsync(false));
        }
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);

        await Bounded(client.EnterExclusiveReceiveAsync());
        Assert.Equal(OnlineHeartbeatState.ExclusiveReceive, client.HeartbeatState);

        await Bounded(client.LeaveExclusiveReceiveAsync());
        Assert.Equal(OnlineClientSessionState.CharacterSelected, client.State);
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);

        // 泵确实未重启:心跳不自动回复,发命令仍抛顺序异常
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
        await Task.Delay(80);
        Assert.Equal(0, rig.Channels[0].Pipe.ClientFrames.Count(f => f.Id == 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendCommandAsync(901, new byte[] { 1 }));

        // 手动再打开后,下一轮 Enter/Leave 会恢复成打开
        await Bounded(client.SetHeartbeatAsync(true));
        await WaitUntil(() => rig.Channels[0].Pipe.ClientFrames.Count(f => f.Id == 0) == 1);
        await Bounded(client.EnterExclusiveReceiveAsync());
        await Bounded(client.LeaveExclusiveReceiveAsync());
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(901, new byte[] { 1 })));
        client.Dispose();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Link_loss_during_exclusive_mode_reports_Disconnected_once_across_round_trips(bool heartbeatWasOn, bool readError)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var disconnects = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: heartbeatWasOn, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += disconnects.Add;

        await Bounded(client.EnterExclusiveReceiveAsync());
        if (readError)
        {
            rig.Channels[0].Link.ReadFault = true;
        }
        rig.Channels[0].Pipe.CloseServer();
        await client.GameSession.ReceiveAsync(TimeSpan.FromMilliseconds(50), 1);

        await Bounded(client.LeaveExclusiveReceiveAsync());
        Assert.Equal(OnlineClientSessionState.CharacterSelected, client.State);
        OnlineDisconnectReason expected = readError ? OnlineDisconnectReason.ReadError : OnlineDisconnectReason.PeerClosed;
        Assert.Equal(new[] { expected }, disconnects.Reasons);

        // 再往返一次同一条死链路,仍然只报一次
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        await Bounded(client.EnterExclusiveReceiveAsync());
        await Bounded(client.LeaveExclusiveReceiveAsync());
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        Assert.Equal(new[] { expected }, disconnects.Reasons);
        client.Dispose();
    }

    [Fact]
    public async Task LeaveExclusiveReceiveAsync_resets_idle_timer_on_restarted_pump()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromMilliseconds(160);
        var disconnects = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += disconnects.Add;

        await Bounded(client.EnterExclusiveReceiveAsync());
        // 独占期间等待超过 HeartbeatTimeout,泵停着不能超时
        await Task.Delay(250);
        Assert.Empty(disconnects.Reasons);

        await Bounded(client.LeaveExclusiveReceiveAsync());
        Assert.Empty(disconnects.Reasons); // Leave 当刻刚重起泵,空闲计时从头起算
        await WaitUntil(() => disconnects.Count == 1);
        Assert.Equal(new[] { OnlineDisconnectReason.HeartbeatTimeout }, disconnects.Reasons);
        client.Dispose();
    }

    [Fact]
    public async Task Frames_arriving_between_combat_return_and_Leave_are_delivered_without_loss()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var received = new ConcurrentQueue<int>();
        client.FrameReceived += f => received.Enqueue(f.PacketId);
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        await Bounded(client.EnterExclusiveReceiveAsync());
        // 模拟战斗结束返回后、尚未调 Leave 之前服务端先推来的帧
        pipe.Push(DungeonEntryWire.Notice(901, new byte[] { 1 }));
        pipe.Push(DungeonEntryWire.Notice(902, new byte[] { 2 }));
        await Task.Delay(30);
        Assert.Empty(received); // 独占期间泵未在跑

        await Bounded(client.LeaveExclusiveReceiveAsync());
        await WaitUntil(() => received.Count == 2);
        Assert.Equal(new[] { 901, 902 }, received.ToArray());
        client.Dispose();
    }

    [Fact]
    public async Task Leave_with_concurrent_SetHeartbeat_Disconnect_and_Dispose_never_deadlocks_or_double_pumps()
    {
        var rig1 = new OnlineFlowRig();
        rig1.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession c1, _) = await rig1.Selected(autoHeartbeat: true);
        await Bounded(c1.EnterExclusiveReceiveAsync());
        await Bounded(Task.WhenAll(
            c1.LeaveExclusiveReceiveAsync(),
            c1.SetHeartbeatAsync(false)));
        // 只剩 0 或 1 个泵:无论如何发一个心跳不得回两次
        if (c1.HeartbeatState == OnlineHeartbeatState.Running)
        {
            rig1.Channels[0].Pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
            await WaitUntil(() => rig1.Channels[0].Pipe.ClientFrames.Count(f => f.Id == 0) == 1);
            await Task.Delay(40);
            Assert.Equal(1, rig1.Channels[0].Pipe.ClientFrames.Count(f => f.Id == 0));
        }
        c1.Dispose();

        var rig2 = new OnlineFlowRig();
        (OnlineClientSession c2, _) = await rig2.Selected(autoHeartbeat: true);
        await Bounded(c2.EnterExclusiveReceiveAsync());
        var enteredDisposeGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDisposeGate = new ManualResetEventSlim(false);
        rig2.Channels[0].Connection.OnDispose = () =>
        {
            enteredDisposeGate.TrySetResult(0);
            releaseDisposeGate.Wait(TimeSpan.FromSeconds(5));
        };
        Task disconnect2 = Task.Run(() => c2.DisconnectAsync());
        await Bounded(enteredDisposeGate.Task);
        Task leave2 = c2.LeaveExclusiveReceiveAsync();
        await Task.Delay(30);
        Assert.False(leave2.IsCompleted); // Leave 必须经门闩串行等待 DisconnectAsync 完成
        releaseDisposeGate.Set();
        await Bounded(disconnect2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Bounded(leave2));
        Assert.Equal(OnlineClientSessionState.LoggedIn, c2.State);
        c2.Dispose();

        var rig3 = new OnlineFlowRig();
        (OnlineClientSession c3, _) = await rig3.Selected(autoHeartbeat: true);
        await Bounded(c3.EnterExclusiveReceiveAsync());
        Task leave3 = c3.LeaveExclusiveReceiveAsync();
        Task dispose3 = Task.Run(c3.Dispose);
        await Bounded(dispose3);
        try { await Bounded(leave3); } catch (Exception ex) when (ex is ObjectDisposedException || ex is OperationCanceledException || ex is InvalidOperationException) { }
        Assert.Equal(OnlineClientSessionState.Disposed, c3.State);
    }

    [Fact]
    public async Task End_to_end_town_commands_to_dungeon_combat_and_back_to_town_commands()
    {
        int[] ids = CombatKit.Sequential(2);
        var combatServer = new FakeCombatServer(ids);
        byte[] aBytes = new SettlementTestPayloads.ASpec { ClearTime = 321000 }.Build();
        byte[] bBytes = new SettlementTestPayloads.BSpec { Percent = 90 }.Build();

        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        rig.ScriptFactory = _ => HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            others: frame =>
            {
                if (frame.Id == 901) return new[] { DungeonEntryWire.Reply(901, new byte[] { 1, 2 }) };
                if (frame.Id == 15) return new[] { DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()) };
                if (frame.Id == 16) return new[] { DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, CombatKit.Map29Payload(ids)) };
                if (frame.Id == 49) return new[] { DungeonEntryWire.Notice(0x22, aBytes), DungeonEntryWire.Notice(0x23, bBytes) };
                if (frame.Id == 75) return new[] { DungeonEntryWire.Reply(75, new byte[] { 1, 1, 2 }), DungeonEntryWire.Notice(23, new byte[10]) };
                return combatServer.Handle(frame.Class, frame.Id, frame.Plain).Select(r => r.Wire()).ToList();
            });

        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var observed = new ConcurrentQueue<OnlineReceivedFrame>();
        client.FrameReceived += f => observed.Enqueue(f);

        // 1. 城镇态发命令 + 收回包
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(901, new byte[] { 9 })));
        await WaitUntil(() => observed.Any(f => f.PacketClass == PacketClass.Cmd && f.PacketId == 901));

        // 2. 切独占跑进副本 + 战斗(用公开的 keepAlive: null 重载)
        await Bounded(client.EnterExclusiveReceiveAsync());
        var runner = new OnlineDungeonRunner(client.GameSession, keepAlive: null);
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options());
        OnlineDungeonResult entered = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 2, null, default, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                _ => { combat.PostKill(0); combat.PostKill(1); }), 20);
        Assert.True(entered.Entered);
        Assert.True(combat.Ended);
        Assert.Equal(OnlineCombatPhase.Settled, combat.Machine.Phase);
        Assert.Equal(321000, combat.Machine.SettlementResult.ClearTime);

        // 3. 回城镇态,订阅仍在,继续发命令与收推送
        await Bounded(client.LeaveExclusiveReceiveAsync());
        Assert.Equal(OnlineClientSessionState.CharacterSelected, client.State);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);

        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(75, new byte[] { 1, 2 })));
        await WaitUntil(() => observed.Any(f => f.PacketClass == PacketClass.Cmd && f.PacketId == 75)
                             && observed.Any(f => f.PacketClass == PacketClass.Noti && f.PacketId == 23));
        client.Dispose();
    }
}
