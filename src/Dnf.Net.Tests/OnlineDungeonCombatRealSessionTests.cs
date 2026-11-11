using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 战斗跑器接在真 <see cref="GameClientSession"/> 上的测试(下面是内存管道,没有网络):命令真的经过帧构建器(折叠哈希、会话加密、补零)写出去,
/// 服务端回包走会话加密或真信封(反机器人算法 16 + 合成序号,会话层拆开后把内层副本附在信封帧之后)。
/// 假会话的测试覆盖逻辑分支,这里只验证"线上字节"与"真会话的批量交付"这两件假会话造不出来的事。数据全是合成的(怪编号 9100 起)。
/// </summary>
public class OnlineDungeonCombatRealSessionTests
{
    /// <summary>
    /// 主链接在真会话上:服务端回包一半走会话加密、一半走真信封;结束时核对线上字节:每条击杀命令是"54 字节载荷(编号、没有击杀者、其余零)+ 折叠哈希 + 补零",
    /// 结算命令是"33 个零 + 折叠哈希 + 补零";信封被拆开并且内层副本只数一次。
    /// </summary>
    [Fact]
    public async Task The_chain_runs_over_a_real_session_with_plain_and_enveloped_replies()
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        int counter = 0;
        pipe.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd)
            {
                return null;
            }
            return server.Handle(frame.Class, frame.Id, frame.Plain)
                .Select(reply => counter++ % 2 == 0 ? reply.Wire() : HostTestKit.RealEnvelope(reply.Class, reply.Id, reply.Payload))
                .ToList();
        };
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, CombatKit.Options());
        runner.PostKill(1);
        runner.PostKill(0);
        var log = new HostTestKit.LogSink();

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), log.Add), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.Confirmed);
        Assert.Equal(new[] { "kill:9101", "kill:9100", "clear", "settle" }, server.Journal);
        Assert.Equal(0, server.PrematureSettlements);
        // 回包共 5 条:确认、确认、清图、结果、奖励,交替直接/信封,所以 2 条信封;每条信封后面附一份内层副本
        Assert.Equal(2, r.EnvelopesOpened);
        Assert.Equal(0, r.EnvelopesUnopened);
        Assert.Equal(2, r.InnerCopiesSkipped);
        Assert.Equal(0, r.Duplicates);
        Assert.Equal(2, r.PassthroughCount);                         // 两个 561 信封帧本身
        Assert.Equal(7, r.FramesSeen);                               // 3 条直接帧 + 2 条信封 + 2 条副本

        CombatTestFrames(pipe, ids);
    }

    private static void CombatTestFrames(DungeonEntryPipe pipe, int[] ids)
    {
        DungeonEntryClientFrame[] kills = pipe.ClientFrames.Where(f => f.Class == PacketClass.Cmd && f.Id == 42).ToArray();
        Assert.Equal(2, kills.Length);
        int[] expectedIds = { ids[1], ids[0] };
        for (int i = 0; i < kills.Length; i++)
        {
            var expected = new byte[54];                              // 手写:编号、没有击杀者、其余全零
            expected[0] = (byte)expectedIds[i];
            expected[1] = (byte)(expectedIds[i] >> 8);
            expected[2] = 0xFF;
            expected[3] = 0xFF;
            byte[] withHash = PacketIntegrity.AppendFoldHash(expected);
            Assert.Equal(withHash, kills[i].Plain.Take(withHash.Length).ToArray());
            Assert.All(kills[i].Plain.Skip(withHash.Length), b => Assert.Equal(0, b));      // 会话加密补齐的零
        }

        DungeonEntryClientFrame settlement = pipe.ClientFrames.Single(f => f.Class == PacketClass.Cmd && f.Id == 49);
        byte[] settlementWithHash = PacketIntegrity.AppendFoldHash(new byte[33]);
        Assert.Equal(settlementWithHash, settlement.Plain.Take(settlementWithHash.Length).ToArray());
        Assert.All(settlement.Plain.Skip(settlementWithHash.Length), b => Assert.Equal(0, b));
    }

    /// <summary>服务端的心跳通知经真会话到达:跑器回类 1、包号 0、8 个零字节,战斗照常走完。</summary>
    [Fact]
    public async Task A_heartbeat_over_a_real_session_is_answered_with_eight_zero_bytes()
    {
        int[] ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame => frame.Class == PacketClass.Cmd ? server.Handle(frame.Class, frame.Id, frame.Plain).Select(r => r.Wire()).ToList() : null;
        pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, CombatKit.Options());
        runner.PostKill(0);

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(1, r.HeartbeatsAnswered);
        DungeonEntryClientFrame reply = pipe.ClientFrames.Single(f => f.Class == PacketClass.Cmd && f.Id == 0);
        Assert.Equal(new byte[8], reply.Plain.Take(8).ToArray());
    }

    /// <summary>服务端在战斗中关了连接:真会话读到流结束,战斗以断线收场(不空转、不抛)。</summary>
    [Fact]
    public async Task The_server_closing_the_connection_ends_the_run_over_a_real_session()
    {
        int[] ids = CombatKit.Sequential(2);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class == PacketClass.Cmd && frame.Id == 42)
            {
                pipe.CloseServer();
            }
            return null;
        };
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, CombatKit.Options());
        runner.PostKill(0);

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, r.Outcome);
        Assert.Equal(1, r.Sent);
        Assert.Equal(OnlineCombatPhase.Fighting, r.Phase);
    }

    /// <summary>
    /// 完整接力在真会话上:进副本五步(真会话、真信封之外的普通帧)→ 战斗 → 保活。保活换成计数的替身(真心跳泵另有测试)。
    /// </summary>
    [Fact]
    public async Task The_whole_relay_runs_over_a_real_session()
    {
        int[] ids = CombatKit.Sequential(3);
        var server = new FakeCombatServer(ids);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd)
            {
                return null;
            }
            if (frame.Id == 15)
            {
                return new[] { DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()) };
            }
            if (frame.Id == 16)
            {
                return new[] { DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, CombatKit.Map29Payload(ids)) };
            }
            return server.Handle(frame.Class, frame.Id, frame.Plain).Select(r => r.Wire()).ToList();
        };
        int keepAlive = 0;
        var runner = new OnlineDungeonRunner(session, token =>
        {
            keepAlive++;
            return Task.FromResult(3);
        });
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options());
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 3, log.Add, default, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                entered: r =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        combat.PostKill(i);
                    }
                }),
            30);

        Assert.True(result.Entered);
        Assert.Equal(3, result.StartMap.Monsters.Count);
        Assert.Equal(1, keepAlive);
        Assert.Equal(new[] { "kill:9100", "kill:9101", "kill:9102", "clear", "settle" }, server.Journal);
        Assert.Equal(OnlineCombatPhase.Settled, combat.Machine.Phase);
        Assert.Contains("DNF_NET stage=combat begin monsters=3 duplicateNumbers=0", log.Lines);
        Assert.Contains(log.Lines, l => l.StartsWith("DNF_NET stage=combat done outcome=ReturnReady phase=Settled monsters=3 sent=3 confirmed=3 "));
        Assert.Equal(new[] { 15, 16 }, pipe.ClientFrames.Where(f => f.Id == 15 || f.Id == 16).Select(f => (int)f.Id).ToArray());
    }
}
