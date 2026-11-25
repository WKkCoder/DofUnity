using System.Collections.Concurrent;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 真 <see cref="OnlineClientSession"/> 配 <c>new OnlineDungeonRunner(session.GameSession, keepAlive: null)</c> 的
/// 交互收包往返与中途 Disconnect/Quit 无挂起、无未观察异常测试(r18-dungeonflow)。
/// 全部使用 <see cref="OnlineFlowRig"/> 内存管道与合成编号,不连真服。
/// </summary>
[Collection("UnobservedTaskException")]
public sealed class OnlineClientSessionExclusiveQuitTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 15);

    private static async Task Bounded(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(task, finished);
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        await Bounded(Task.Run(async () =>
        {
            while (!condition())
            {
                await Task.Delay(5);
            }
            return 0;
        }));
    }

    [Fact]
    public async Task Runner_with_null_keepAlive_round_trips_three_times_and_restores_heartbeat_command_and_permissions_subscription()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        var received = new ConcurrentQueue<(PacketClass Cls, int Id, byte[] Payload)>();
        client.FrameReceived += f => received.Enqueue((f.PacketClass, f.PacketId, f.Payload));

        int expectedHeartbeats = 0;

        for (int cycle = 0; cycle < 3; cycle++)
        {
            var server = new FakeCombatServer(CombatKit.Sequential(1));
            pipe.Script = frame =>
            {
                if (frame.Id == OnlineDungeonEntry.StartGamePacketId)
                {
                    return new[] { DungeonEntryWire.Notice((ushort)OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
                }
                if (frame.Id == OnlineDungeonEntry.SelectDungeonPacketId)
                {
                    return new[]
                    {
                        DungeonEntryWire.Notice((ushort)DungeonInfoParser.PacketId, DungeonEntryWire.Info28(9101)),
                        DungeonEntryWire.Notice((ushort)StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(1), 0, 0, 905)),
                    };
                }
                return server.Handle(frame.Class, frame.Id, frame.Plain)
                    .Select(r => r.Wire())
                    .ToArray();
            };

            await Bounded(client.EnterExclusiveReceiveAsync());
            Assert.Equal(OnlineClientSessionState.ExclusiveReceive, client.State);
            Assert.Equal(OnlineHeartbeatState.ExclusiveReceive, client.HeartbeatState);

            var runner = new OnlineDungeonRunner(client.GameSession, keepAlive: null);
            OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options(pollMilliseconds: 10));
            OnlineDungeonResult entry = await Bounded(runner.EnterAsync(9101, 2, null));
            Assert.True(entry.Entered);
            Assert.NotNull(entry.StartMap);

            Task<OnlineDungeonCombatResult> combatTask = combat.RunAsync(entry.StartMap, null);
            Assert.True(combat.PostKill(0));
            OnlineDungeonCombatResult fought = await Bounded(combatTask);
            Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, fought.Outcome);

            // 退出独占:必须恢复主会话单一收包与心跳泵(不能因旧 keepAlive 产生双读者)
            await Bounded(client.LeaveExclusiveReceiveAsync());
            Assert.Equal(OnlineClientSessionState.CharacterSelected, client.State);
            Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);

            pipe.Script = null;
            int cmdsBefore = pipe.ClientFrames.Count;
            Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync((ushort)(930 + cycle), new byte[] { (byte)cycle })));
            Assert.Equal(cmdsBefore + 1, pipe.ClientFrames.Count);

            // 推送 1 次心跳应只得到恰好 1 次回包
            pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
            expectedHeartbeats++;
            await WaitUntil(() => pipe.ClientFrames.Count(f => f.Id == 0) == expectedHeartbeats);
            await Task.Delay(35);
            Assert.Equal(expectedHeartbeats, pipe.ClientFrames.Count(f => f.Id == 0));

            // 推送一条动态 (0,5) 权限更新帧,原有 FrameReceived 订阅仍可正常收到并解析
            byte[] permBody = new NoticePayloadBuilder().U16(1).U16(9101).U8((byte)(1 + cycle)).ToArray();
            int recvBefore = received.Count;
            pipe.Push(DungeonEntryWire.Notice((ushort)DungeonPermissionParser.PacketId, permBody));
            await WaitUntil(() => received.Count == recvBefore + 1);
            Assert.True(received.TryPeek(out _));
        }

        var lastPerm = received.Last(x => x.Cls == PacketClass.Noti && x.Id == DungeonPermissionParser.PacketId);
        Assert.True(DungeonPermissionParser.TryParse(lastPerm.Payload, out DungeonPermissionNotice notice));
        Assert.Single(notice.Entries);
        Assert.Equal(9101, notice.Entries[0].DungeonId);
        Assert.Equal(3, notice.Entries[0].Tier);

        client.Dispose();
    }

    [Fact]
    public async Task DisconnectAsync_during_active_ExclusiveReceive_combat_loop_finishes_promptly_without_unobserved_exceptions()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobserved.Enqueue(e.Exception);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;

        try
        {
            var rig = new OnlineFlowRig();
            rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
            DungeonEntryPipe pipe = rig.Channels[0].Pipe;

            pipe.Script = frame =>
            {
                if (frame.Id == OnlineDungeonEntry.StartGamePacketId)
                {
                    return new[] { DungeonEntryWire.Notice((ushort)OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
                }
                if (frame.Id == OnlineDungeonEntry.SelectDungeonPacketId)
                {
                    return new[]
                    {
                        DungeonEntryWire.Notice((ushort)DungeonInfoParser.PacketId, DungeonEntryWire.Info28(9101)),
                        DungeonEntryWire.Notice((ushort)StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(1), 0, 0, 905)),
                    };
                }
                return Array.Empty<byte[]>();
            };

            await Bounded(client.EnterExclusiveReceiveAsync());

            var runner = new OnlineDungeonRunner(client.GameSession, keepAlive: null);
            OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options(pollMilliseconds: 20));
            OnlineDungeonResult entry = await Bounded(runner.EnterAsync(9101, 0, null));
            Assert.True(entry.Entered);

            using var cts = new CancellationTokenSource();
            Task<OnlineDungeonCombatResult> combatTask = combat.RunAsync(entry.StartMap!, null, cts.Token);

            // 确保战斗读包循环已经挂上管道 ReadAsync 后触发断线/退出
            await WaitUntil(() => combat.Machine != null && pipe.HasPendingRead);

            cts.Cancel();
            await Bounded(client.DisconnectAsync());
            OnlineDungeonCombatResult ended = await Bounded(combatTask);
            Assert.True(ended.Outcome == OnlineDungeonCombatOutcome.Cancelled || ended.Outcome == OnlineDungeonCombatOutcome.ConnectionClosed);
            Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
            Assert.Null(client.GameSession);

            client.Dispose();
            Assert.Equal(OnlineClientSessionState.Disposed, client.State);

            // 再测一遍:不先触发 cts.Cancel(),直接在对端仍阻塞 ReceiveAsync 时调 DisconnectAsync(),同样必须迅速收敛且无未观察异常
            var rig2 = new OnlineFlowRig();
            rig2.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            (OnlineClientSession client2, _) = await rig2.Selected(autoHeartbeat: true);
            rig2.Channels[0].Pipe.Script = pipe.Script;
            rig2.Channels[0].Connection.OnDispose = () => rig2.Channels[0].Pipe.CloseServer();
            await Bounded(client2.EnterExclusiveReceiveAsync());
            var runner2 = new OnlineDungeonRunner(client2.GameSession, keepAlive: null);
            OnlineDungeonCombatRunner combat2 = runner2.UseCombat(CombatKit.Options(pollMilliseconds: 20));
            OnlineDungeonResult entry2 = await Bounded(runner2.EnterAsync(9101, 0, null));
            Assert.True(entry2.Entered);
            Task<OnlineDungeonCombatResult> combatTask2 = combat2.RunAsync(entry2.StartMap!, null);
            await WaitUntil(() => combat2.Machine != null && rig2.Channels[0].Pipe.HasPendingRead);

            await Bounded(client2.DisconnectAsync());
            OnlineDungeonCombatResult ended2 = await Bounded(combatTask2);
            Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, ended2.Outcome);
            Assert.Equal(OnlineClientSessionState.LoggedIn, client2.State);
            client2.Dispose();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }
}
