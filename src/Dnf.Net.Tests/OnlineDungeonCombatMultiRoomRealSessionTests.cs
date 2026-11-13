using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 多房间战斗跑器接在真 <see cref="GameClientSession"/> 上的测试(下面是内存管道,没有网络):换房命令(类 1、包号 48)与装载完成命令(类 1、包号 40)真的经过帧构建器
/// (会话加密、补零;折叠哈希只有服务端检查表里的包号才加,这两个包号不在表里)写出去,线上字节与手写的期望逐字节对;服务端的通知一半走会话加密、起始地图与装载完成通知走真信封(反机器人算法 16 + 合成序号,
/// 会话层拆开后把内层副本附在信封帧之后);换房失败回包(类 1,经会话加密补零)照样读得出错误码。
/// 假会话的测试(<see cref="OnlineDungeonCombatMultiRoomTests"/>)覆盖逻辑分支,这里只验证"线上字节"与"真会话的批量交付"这两件假会话造不出来的事。
/// 数据全是合成的:怪编号第一间 9100 起、第二间 9300 起,位置 901、902,没有真实编号或抓包字节。
/// </summary>
public class OnlineDungeonCombatMultiRoomRealSessionTests
{
    /// <summary>
    /// 手写的换房命令载荷期望值(偏移 0 目标格 X、1 目标格 Y、2..5 位置 X、6..9 位置 Y、10 分层、11..12 trapBits、13..20 四个座位项、21..36 四个游玩时间、37..38 未用、39..42 末尾),
    /// 其余取缺省:分层 0、trapBits 0、座位项全是 0xFFFF(跳过)、游玩时间 0、末尾 0。位置值取小于 65536 的合成值,所以高两个字节是 0。
    /// </summary>
    private static byte[] ExpectedMove(int targetX, int targetY, int positionX, int positionY)
    {
        var bytes = new byte[43];
        bytes[0] = (byte)targetX;
        bytes[1] = (byte)targetY;
        bytes[2] = (byte)positionX;
        bytes[3] = (byte)(positionX >> 8);
        bytes[6] = (byte)positionY;
        bytes[7] = (byte)(positionY >> 8);
        for (int offset = 13; offset < 21; offset++)
        {
            bytes[offset] = 0xFF;
        }
        return bytes;
    }

    /// <summary>
    /// 线上明文 = 载荷(需要折叠哈希的包号后面再跟 4 字节折叠哈希)+ 会话加密补齐的零。
    /// 哪些包号带哈希由帧层按服务端的检查表定(<see cref="PacketIntegrity.ClientFoldHashPacketIds"/>):击杀 42、结算 49 在表里,换房 48、装载完成 40 不在。
    /// </summary>
    private static void AssertWireBytes(DungeonEntryClientFrame frame, byte[] expectedPayload, bool withHash)
    {
        byte[] expected = withHash ? PacketIntegrity.AppendFoldHash(expectedPayload) : expectedPayload;
        Assert.Equal(expected, frame.Plain.Take(expected.Length).ToArray());
        Assert.All(frame.Plain.Skip(expected.Length), b => Assert.Equal(0, b));            // 会话加密补齐的零
    }

    /// <summary>
    /// 帧层的折叠哈希策略(既有代码,本任务不改):服务端只对类 1 的 17 个包号核对载荷尾哈希,换房 48 与装载完成 40 不在其中,所以这两条命令线上是"载荷 + 补零",没有哈希;
    /// 击杀 42、结算 49 在表里,带哈希。读码出处:<c>PI_GetType_CS</c> 0x808c5d8(<c>_NS_PI_Check_CMD</c> 0x8274778 先问它,返回 0 就不核对)。
    /// </summary>
    [Fact]
    public void The_fold_hash_policy_of_the_frame_layer_covers_kill_and_settlement_but_not_move_and_loaded()
    {
        Assert.True(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, 42));
        Assert.True(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, 49));
        Assert.False(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, 48));
        Assert.False(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, 40));
    }

    /// <summary>
    /// 两间房的主链接在真会话上:第一间还有一只服务端才知道的怪,第一次换房被回错误码 20(类 1 回包,经会话加密);
    /// 那只怪没了之后再换,成功,新房间的起始地图通知与装载完成通知装在真信封里到达;核对线上的换房命令、装载完成命令与新表的击杀命令的字节。
    /// </summary>
    [Fact]
    public async Task The_two_room_walk_runs_over_a_real_session_with_exact_wire_bytes()
    {
        var plans = new[]
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(2), new[] { 9150 }),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId)),
        };
        var server = new FakeMultiRoomServer(plans);
        server.AllowGatePosition(901, 902);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        int counter = 0;
        int envelopes = 0;
        pipe.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd)
            {
                return null;
            }
            return server.Handle(frame.Class, frame.Id, frame.Plain)
                .Select(reply =>
                {
                    bool enveloped = reply.Class == CombatKit.Noti && (reply.Id == 29 || reply.Id == 30 || counter++ % 2 == 1);
                    if (!enveloped)
                    {
                        return reply.Wire();
                    }
                    envelopes++;
                    return HostTestKit.RealEnvelope(reply.Class, reply.Id, reply.Payload);
                })
                .ToList();
        };
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, CombatKit.Options());
        var log = new HostTestKit.LogSink();
        Assert.True(runner.PostKill(0, 0));
        Assert.True(runner.PostKill(0, 1));
        Assert.True(runner.PostMoveRoom(0, RoomKit.Move(1, 0, 901, 902)));
        Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(RoomKit.Map(plans[0].Ids, 0, 0), log.Add), 30);

        await CombatKit.WaitUntil(() => runner.Machine != null && runner.Machine.MoveRefusals == 1, "第一次换房被服务端拒绝");
        Assert.Empty(server.RemoveHidden());
        Assert.True(runner.PostMoveRoom(RoomKit.Move(1, 0, 901, 902)));
        await CombatKit.WaitUntil(
            () => runner.Machine.Room == 1 && runner.Machine.LoadReports == 1 && runner.Machine.LoadState == OnlineRoomLoadState.None, "进了第二间且装载握手完成");
        Assert.True(runner.PostKill(1, 0));

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(1, r.Room);
        Assert.Equal((2, 1, 1), (r.MovesSent, r.MoveRefusals, r.LoadReports));
        Assert.Equal(
            new[] { "kill:9100", "kill:9101", "move:r0:err20", "hidden-gone:r0", "roomclear:r0", "move:r0:ok:r1", "loaded:r1", "kill:9300", "clear", "settle" },
            server.Journal);
        Assert.Equal(envelopes, r.EnvelopesOpened);
        Assert.Equal(0, r.EnvelopesUnopened);
        Assert.Equal(envelopes, r.InnerCopiesSkipped);
        Assert.Equal(0, r.Duplicates + r.OutOfOrder + r.UnknownNumbers + r.Malformed);
        Assert.True(envelopes >= 2, "起始地图与装载完成通知都走了信封");
        Assert.Contains(Prefix + " event=RoomMoveRefused room=0 code=20", log.Lines);

        // 线上的命令顺序
        DungeonEntryClientFrame[] frames = pipe.ClientFrames.Where(f => f.Class == PacketClass.Cmd && (f.Id == 42 || f.Id == 48 || f.Id == 40 || f.Id == 49)).ToArray();
        Assert.Equal(new[] { 42, 42, 48, 48, 40, 42, 49 }, frames.Select(f => (int)f.Id).ToArray());

        // 两条换房命令:43 字节载荷 + 补零(换房不在折叠哈希检查表里,没有哈希),与手写期望逐字节对
        byte[] expectedMove = ExpectedMove(1, 0, 901, 902);
        Assert.Equal(43, expectedMove.Length);
        AssertWireBytes(frames[2], expectedMove, withHash: false);
        AssertWireBytes(frames[3], expectedMove, withHash: false);

        // 装载完成命令:8 个零字节 + 补零(同样没有哈希)
        AssertWireBytes(frames[4], new byte[8], withHash: false);

        // 第二间的击杀:新表里 0 号的服务端编号(合成 9300)、没有击杀者、其余全零;击杀在检查表里,带哈希
        var expectedKill = new byte[54];
        expectedKill[0] = (byte)(RoomKit.SecondBaseId & 0xFF);
        expectedKill[1] = (byte)(RoomKit.SecondBaseId >> 8);
        expectedKill[2] = 0xFF;
        expectedKill[3] = 0xFF;
        AssertWireBytes(frames[5], expectedKill, withHash: true);

        // 结算:33 个零,带哈希
        AssertWireBytes(frames[6], new byte[33], withHash: true);
    }

    private const string Prefix = "DNF_NET stage=combat";
}
