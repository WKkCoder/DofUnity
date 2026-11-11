using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 战斗跑器(<see cref="OnlineDungeonCombatRunner"/>)的测试,用会话接缝的线程安全假实现(<see cref="CombatFakeSession"/>)加"服务端"替身
/// (<see cref="FakeCombatServer"/>):主链从合成起始地图一路到"可以回城",负例(没清图不结算、未知编号不发包、乱序与重复不推进两次)、
/// 心跳、信封与内层副本、断线与超时、取消、日志行格式。数据全是合成的:怪编号 9100 起,无关帧包号 900 以上,没有真实编号。
/// </summary>
public class OnlineDungeonCombatRunnerTests
{
    private const string Prefix = "DNF_NET stage=combat";

    /// <summary>一场战斗的装置:合成地图、服务端替身、假会话、跑器、日志收集器。</summary>
    private sealed class Fight
    {
        public Fight(int count, OnlineDungeonCombatOptions options = null, Func<byte[], byte[]> unwrap = null, bool withholdClear = false)
        {
            Ids = CombatKit.Sequential(count);
            Map = CombatKit.Map(Ids);
            Server = new FakeCombatServer(Ids) { WithholdClear = withholdClear };
            Session = CombatFakeSession.ForServer(Server);
            Runner = new OnlineDungeonCombatRunner(Session, unwrap, options ?? CombatKit.Options());
        }

        public int[] Ids { get; }

        public StartMapNotice Map { get; }

        public FakeCombatServer Server { get; }

        public CombatFakeSession Session { get; }

        public OnlineDungeonCombatRunner Runner { get; }

        public HostTestKit.LogSink Log { get; } = new HostTestKit.LogSink();

        public Task<OnlineDungeonCombatResult> Run(CancellationToken cancellationToken = default)
        {
            return GatewayTestSupport.Bounded(Runner.RunAsync(Map, Log.Add, cancellationToken), 20);
        }

        public void PostAll()
        {
            for (int i = 0; i < Ids.Length; i++)
            {
                Assert.True(Runner.PostKill(i));
            }
        }
    }

    private static (PacketClass Class, ushort Id)[] Wire(CombatFakeSession session)
    {
        return session.Sent.Select(s => (s.Class, s.Id)).ToArray();
    }

    // ======== 主链 ========

    /// <summary>
    /// 主链(假会话):5 只怪的合成起始地图,宿主不按线上顺序把击杀排进队列;跑器逐只发命令(每条的怪编号是对应记录的第 3 项)→ 服务端确认 →
    /// 最后一只之后清图通知 → 跑器才发结算命令(且只发一条)→ 两段结算通知 → 回城就绪。从服务端视角核对次序:先杀完、清图、再结算,没有过早结算。
    /// </summary>
    [Fact]
    public async Task The_full_chain_runs_over_a_fake_session_to_return_ready()
    {
        var f = new Fight(5);
        int[] order = { 3, 0, 4, 1, 2 };
        foreach (int local in order)
        {
            Assert.True(f.Runner.PostKill(local));
        }

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.True(r.ReturnReady);
        Assert.Equal(OnlineCombatPhase.Settled, r.Phase);
        Assert.Equal(5, r.Monsters);
        Assert.Equal(5, r.Sent);
        Assert.Equal(5, r.Confirmed);
        Assert.Equal(0, r.Refused);
        Assert.Equal(0, r.Blocked);
        Assert.Equal(0, r.OutOfOrder + r.Duplicates + r.UnknownNumbers + r.Malformed + r.PassthroughCount);

        var killCommand = (PacketClass.Cmd, (ushort)42);
        Assert.Equal(
            new[] { killCommand, killCommand, killCommand, killCommand, killCommand, (PacketClass.Cmd, (ushort)49) },
            Wire(f.Session));
        Assert.Equal(new byte[33], f.Session.Sent[5].Payload);

        // 命令里的怪编号 = 起始地图记录的第 3 项,顺序与宿主排的一致
        Assert.Equal(order.Select(i => f.Ids[i]).ToArray(), f.Server.Kills.Select(k => (int)k.MobId).ToArray());
        // 服务端视角:先杀完、清图、再结算,没有过早结算
        Assert.Equal(new[] { "kill:9103", "kill:9100", "kill:9104", "kill:9101", "kill:9102", "clear", "settle" }, f.Server.Journal);
        Assert.Equal(0, f.Server.PrematureSettlements);
        Assert.Equal(1, f.Server.Settlements);
        Assert.False(f.Server.Disconnected);

        Assert.Equal(
            "Spawned KillSent:3 KillSent:0 KillSent:4 KillSent:1 KillSent:2 " +
            "KillConfirmed:3 KillConfirmed:0 KillConfirmed:4 KillConfirmed:1 KillConfirmed:2 " +
            "MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(r.Events));
        Assert.True(f.Runner.Ended);
    }

    /// <summary>击杀不是一次排完,而是战斗进行中一只一只排进来(宿主的轮询线程那样);跑器在下一轮循环里发出,链照样走完。</summary>
    [Fact]
    public async Task Kills_posted_while_the_runner_is_running_are_reported_one_by_one()
    {
        var f = new Fight(4);
        Task<OnlineDungeonCombatResult> run = f.Run();

        int[] order = { 2, 0, 3, 1 };
        for (int n = 0; n < order.Length; n++)
        {
            int expected = n + 1;
            Assert.True(f.Runner.PostKill(order[n]));
            await CombatKit.WaitUntil(() => f.Server.Kills.Length >= expected, "服务端收到第 " + expected + " 条击杀命令");
        }

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { "kill:9102", "kill:9100", "kill:9103", "kill:9101", "clear", "settle" }, f.Server.Journal);
        Assert.Equal(0, f.Server.PrematureSettlements);
    }

    // ======== 清图闸门 ========

    /// <summary>
    /// 服务端扣着清图通知不发:怪全杀了、全确认了,跑器也一条结算命令都不发(闸门只认清图通知),一直等;通知放出之后才发,然后走完。
    /// </summary>
    [Fact]
    public async Task The_runner_never_settles_while_the_clear_notice_is_withheld_and_does_once_it_arrives()
    {
        var f = new Fight(3, withholdClear: true);
        f.PostAll();
        Task<OnlineDungeonCombatResult> run = f.Run();

        await CombatKit.WaitUntil(() => f.Server.Kills.Length == 3, "三条击杀命令都到了服务端");
        await CombatKit.WaitUntil(() => f.Runner.Machine.Confirmed == 3, "三条确认都被处理了");
        await Task.Delay(300);                                       // 十几个轮询周期:闸门漏的话早就发了
        Assert.Equal(0, f.Session.Commands(49));
        Assert.False(run.IsCompleted);
        Assert.Equal(OnlineCombatPhase.Fighting, f.Runner.Machine.Phase);
        Assert.Equal(0, f.Server.Settlements + f.Server.PrematureSettlements);

        f.Session.Push(f.Server.ReleaseClear());

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { "kill:9100", "kill:9101", "kill:9102", "clear", "settle" }, f.Server.Journal);
        Assert.Equal(0, f.Server.PrematureSettlements);
        Assert.Equal(1, f.Session.Commands(49));
    }

    [Fact]
    public async Task Without_a_clear_notice_the_run_times_out_and_never_settles()
    {
        var f = new Fight(2, CombatKit.Options(overallSeconds: 0.4), withholdClear: true);
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.False(r.ReturnReady);
        Assert.Equal(OnlineCombatPhase.Fighting, r.Phase);
        Assert.Equal(2, r.Confirmed);
        Assert.Equal(0, f.Session.Commands(49));
        Assert.Equal(0, f.Server.Settlements);
        Assert.Equal(0, f.Server.PrematureSettlements);
        Assert.StartsWith(Prefix + " ended outcome=Timeout phase=Fighting monsters=2 sent=2 confirmed=2 ", f.Log.Lines.Last());
    }

    /// <summary>发出结算命令之后服务端不回通知:到了结算上限就以超时收场,阶段停在结算中。</summary>
    [Fact]
    public async Task A_silent_server_after_the_settlement_command_ends_with_a_timeout()
    {
        var ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) => id == 49 ? null : server.Handle(cls, id, payload),
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options(overallSeconds: 15, settlementSeconds: 0.3));
        Assert.True(runner.PostKill(0));

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.Equal(OnlineCombatPhase.Settling, r.Phase);
        Assert.Equal(1, session.Commands(49));
    }

    // ======== 本地拦截 ========

    [Fact]
    public async Task Unknown_local_ids_posted_by_the_host_never_reach_the_wire()
    {
        var f = new Fight(2, CombatKit.Options(overallSeconds: 0.4));
        Assert.True(f.Runner.PostKill(-1));
        Assert.True(f.Runner.PostKill(99));
        Assert.True(f.Runner.PostKill(0));
        Assert.True(f.Runner.PostKill(0));          // 同一只报两次:第二次也拦下

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(3, r.Blocked);
        Assert.Equal(1, r.Sent);
        Assert.Equal(1, f.Session.Commands(42));
        Assert.Equal(new[] { "kill:9100" }, f.Server.Journal);
        Assert.Equal(
            "Spawned KillBlocked:-1:UnknownLocalId KillBlocked:99:UnknownLocalId KillSent:0 KillBlocked:0:AlreadyReported KillConfirmed:0",
            CombatKit.Trace(r.Events));
    }

    // ======== 乱序、重复、无关帧 ========

    /// <summary>
    /// 线上夹杂噪声:每条击杀回包前先来一条编号不在表里的确认、后面再重复一遍真确认;清图通知来两遍;结算先来奖励再来结果、结果再重复一遍。
    /// 链照样只推进一次:3 只怪 3 次确认,结算命令只发一条,回城就绪只有一次。
    /// </summary>
    [Fact]
    public async Task Noise_on_the_wire_does_not_advance_the_chain_twice()
    {
        var ids = CombatKit.Sequential(3);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                var replies = new List<CombatKit.ServerFrame>();
                List<CombatKit.ServerFrame> real = server.Handle(cls, id, payload);
                if (cls == PacketClass.Cmd && id == 42)
                {
                    replies.Add(CombatKit.Confirm(9999));                 // 编号不在表里
                    replies.Add(real[0]);                                 // 真确认
                    replies.Add(real[0]);                                 // 同一条确认再来一遍
                    for (int i = 1; i < real.Count; i++)
                    {
                        replies.Add(real[i]);                             // 清图通知,也来两遍
                        replies.Add(real[i]);
                    }
                }
                else if (cls == PacketClass.Cmd && id == 49)
                {
                    replies.Add(real[1]);                                 // 奖励先到
                    replies.Add(real[0]);                                 // 结果
                    replies.Add(real[0]);                                 // 结果重复
                    replies.Add(real[1]);                                 // 奖励(这一条才算数)
                }
                return replies;
            },
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        for (int i = 0; i < 3; i++)
        {
            runner.PostKill(i);
        }

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(3, r.Confirmed);
        Assert.Equal(3, r.UnknownNumbers);
        Assert.Equal(5, r.Duplicates);              // 3 条重复确认 + 1 条重复清图 + 1 条重复结果
        Assert.Equal(1, r.OutOfOrder);              // 早到的奖励
        Assert.Equal(0, r.Malformed);
        Assert.Equal(1, session.Commands(49));
        Assert.Equal(1, r.Events.Count(e => e.Kind == OnlineCombatEventKind.ReadyToReturnToTown));
        Assert.Equal(1, r.Events.Count(e => e.Kind == OnlineCombatEventKind.MapCleared));
        Assert.Equal(2, r.Events.Count(e => e.Kind == OnlineCombatEventKind.SettlementArrived));
    }

    /// <summary>别的通知只计数并透传:包号 900 以上的无关帧、类 1 的无关帧,不影响主链;汇总行按(类、包号)升序列出计数。</summary>
    [Fact]
    public async Task Unrelated_frames_are_counted_and_do_not_disturb_the_chain()
    {
        var f = new Fight(2);
        f.Session.Push(CombatKit.Unrelated(902), CombatKit.Unrelated(901), CombatKit.Unrelated(901),
            new CombatKit.ServerFrame(CombatKit.Cmd, 905, new byte[] { 1, 2, 3 }));
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(4, r.PassthroughCount);
        Assert.Equal(
            new[] { "Noti/901:2", "Noti/902:1", "Cmd/905:1" },
            r.Passthrough.Select(c => c.PacketClass + "/" + c.PacketId + ":" + c.Count).ToArray());
        Assert.EndsWith(" passIds=NOTI/901:2,NOTI/902:1,CMD/905:1", f.Log.Lines.Last());
        Assert.Equal(0, r.OutOfOrder + r.Duplicates + r.UnknownNumbers + r.Malformed);
    }

    /// <summary>汇总行最多列 32 个不同的透传包号,多的只记一个"+N"。</summary>
    [Fact]
    public async Task The_summary_line_caps_the_passthrough_ids()
    {
        var f = new Fight(1);
        for (int i = 0; i < 40; i++)
        {
            f.Session.Push(CombatKit.Unrelated(901 + i));
        }
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(40, r.PassthroughCount);
        Assert.Equal(40, r.Passthrough.Count);
        string last = f.Log.Lines.Last();
        string ids = last.Substring(last.IndexOf(" passIds=", StringComparison.Ordinal) + " passIds=".Length);
        string[] parts = ids.Split(',');
        Assert.Equal(33, parts.Length);
        Assert.Equal("NOTI/901:1", parts[0]);
        Assert.Equal("NOTI/932:1", parts[31]);
        Assert.Equal("+8", parts[32]);
    }

    // ======== 心跳 ========

    /// <summary>战斗期间的心跳通知由跑器自己回(类 1、包号 0、8 个零字节),不然服务端的心跳计数会累加到断线;心跳也计入透传。</summary>
    [Fact]
    public async Task Heartbeat_notices_are_answered_with_the_zero_reply_during_the_fight()
    {
        var f = new Fight(2);
        f.Session.Push(CombatKit.Heartbeat(), CombatKit.Unrelated(901), CombatKit.Heartbeat());
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.HeartbeatsAnswered);
        var replies = f.Session.Sent.Where(s => s.Class == PacketClass.Cmd && s.Id == 0).ToArray();
        Assert.Equal(2, replies.Length);
        Assert.All(replies, s => Assert.Equal(new byte[8], s.Payload));
        Assert.Equal(3, r.PassthroughCount);
        Assert.EndsWith(" passIds=NOTI/0:2,NOTI/901:1", f.Log.Lines.Last());
        Assert.Contains(" heartbeats=2 ", f.Log.Lines.Last());
    }

    [Fact]
    public async Task A_failed_heartbeat_reply_ends_the_run_with_a_send_failure()
    {
        var f = new Fight(2);
        f.Session.StatusOfSend = index => FrameStatus.EncryptFailed;
        f.Session.Push(CombatKit.Heartbeat());

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, r.Outcome);
        Assert.Equal(0, r.HeartbeatsAnswered);
        Assert.Equal(1, f.Session.Commands(0));
    }

    // ======== 发送失败、异常、断线、取消 ========

    [Fact]
    public async Task A_kill_frame_failure_ends_the_run_and_leaves_the_monster_unreported()
    {
        var f = new Fight(2);
        f.Session.StatusOfSend = index => FrameStatus.EncryptFailed;
        f.Runner.PostKill(1);

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, r.Outcome);
        Assert.Equal(0, r.Sent);
        Assert.Equal(1, r.Blocked);
        Assert.Equal(OnlineCombatMonsterState.Alive, f.Runner.Machine.Monsters[1].State);
        Assert.Equal(OnlineCombatPhase.Fighting, r.Phase);
    }

    [Fact]
    public async Task A_settlement_frame_failure_ends_the_run_with_the_gate_still_open()
    {
        var f = new Fight(1);
        f.Session.StatusOfSend = index => index == 1 ? FrameStatus.EncryptFailed : FrameStatus.Ok;     // 第 0 条是击杀,第 1 条是结算
        f.Runner.PostKill(0);

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, r.Outcome);
        Assert.Equal(OnlineCombatPhase.Cleared, r.Phase);
        Assert.Equal(1, r.Blocked);
        Assert.Equal(1, f.Session.Commands(49));
        Assert.Equal(0, f.Server.Settlements);
    }

    /// <summary>发送抛异常:战斗以 Faulted 收场,汇总行与结果里只有异常类型名,没有异常消息(可能带地址、凭据一类的东西)。</summary>
    [Fact]
    public async Task A_send_exception_becomes_a_fault_and_only_the_type_name_is_logged()
    {
        var f = new Fight(2);
        f.Session.FailureOfSend = index => new IOException("secret-detail-12345");
        f.Runner.PostKill(0);

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.Faulted, r.Outcome);
        Assert.Equal("IOException", r.FaultType);
        Assert.Contains(" exception=IOException ", f.Log.Lines.Last());
        Assert.DoesNotContain("secret", string.Join("\n", f.Log.Lines));
        Assert.StartsWith(Prefix + " ended outcome=Faulted ", f.Log.Lines.Last());
    }

    /// <summary>对端关闭:先把已经到了的帧处理完,再以断线收场。</summary>
    [Fact]
    public async Task A_closed_connection_ends_the_run_after_the_queued_frames_are_processed()
    {
        var f = new Fight(1);
        f.Session.Push(CombatKit.Unrelated(901), CombatKit.Unrelated(902));
        f.Session.Closed = true;

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, r.Outcome);
        Assert.Equal(2, r.PassthroughCount);
        Assert.Equal(OnlineCombatPhase.Fighting, r.Phase);
        Assert.StartsWith(Prefix + " ended outcome=ConnectionClosed phase=Fighting ", f.Log.Lines.Last());
    }

    [Fact]
    public async Task A_framing_fault_ends_the_run()
    {
        var f = new Fight(1);
        f.Session.Fault = FrameError.LengthAboveMaximum;

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.FramingFault, r.Outcome);
    }

    [Fact]
    public async Task Cancelling_ends_the_run_quietly_and_closes_the_kill_queue()
    {
        using var cts = new CancellationTokenSource();
        var f = new Fight(2, CombatKit.Options(overallSeconds: null));
        Task<OnlineDungeonCombatResult> run = f.Run(cts.Token);
        await Task.Delay(100);
        Assert.False(run.IsCompleted);

        cts.Cancel();
        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, r.Outcome);
        Assert.False(r.ReturnReady);
        Assert.Null(r.FaultType);
        Assert.StartsWith(Prefix + " ended outcome=Cancelled phase=Fighting ", f.Log.Lines.Last());
        Assert.True(f.Runner.Ended);
        Assert.False(f.Runner.PostKill(0));
    }

    [Fact]
    public async Task A_token_cancelled_before_the_start_ends_without_sending_anything()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var f = new Fight(2);
        f.Runner.PostKill(0);

        OnlineDungeonCombatResult r = await f.Run(cts.Token);

        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, r.Outcome);
        Assert.Empty(f.Session.Sent);
        Assert.Equal(2, f.Log.Lines.Length);                          // 开头与收尾两行
    }

    // ======== 运行器自身:一次性、入参、排队 ========

    [Fact]
    public async Task A_runner_runs_only_once()
    {
        var f = new Fight(1);
        f.Runner.PostKill(0);
        await f.Run();

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Runner.RunAsync(f.Map, null));
    }

    [Fact]
    public async Task RunAsync_refuses_a_null_map_without_burning_the_single_run()
    {
        var f = new Fight(1);

        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Runner.RunAsync(null, null));

        f.Runner.PostKill(0);
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, (await f.Run()).Outcome);
    }

    [Fact]
    public void The_constructors_refuse_a_missing_session_and_unusable_windows()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonCombatRunner((GameClientSession)null));
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonCombatRunner((IOnlineDungeonSession)null));
        var session = new CombatFakeSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { PollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { PollInterval = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { PollInterval = TimeSpan.FromDays(40) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { SettlementTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { OverallTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { OverallTimeout = TimeSpan.FromMilliseconds(-5) }));
    }

    [Fact]
    public async Task PostKill_is_accepted_until_the_run_has_ended_and_the_machine_appears_with_the_run()
    {
        var f = new Fight(1);
        Assert.Null(f.Runner.Machine);
        Assert.False(f.Runner.Ended);
        Assert.True(f.Runner.PostKill(0));

        await f.Run();

        Assert.NotNull(f.Runner.Machine);
        Assert.True(f.Runner.Ended);
        Assert.False(f.Runner.PostKill(0));
        Assert.Equal(1, f.Session.Commands(42));                     // 结束后排的不会被发
    }

    // ======== 日志行 ========

    /// <summary>
    /// 日志行格式逐行核对(2 只怪,宿主先报 1 号再报 0 号):每行以固定前缀开头,只有阶段名、枚举名、本地序号与计数,
    /// 没有任何载荷内容,也没有服务端怪编号(合成编号 9100 起,行里不能出现 910 开头的数)。
    /// </summary>
    [Fact]
    public async Task The_log_lines_have_the_documented_shape_and_leak_no_numbers_from_the_wire()
    {
        var f = new Fight(2);
        f.Runner.PostKill(1);
        f.Runner.PostKill(0);

        await f.Run();

        Assert.Equal(
            new[]
            {
                Prefix + " begin monsters=2 duplicateNumbers=0",
                Prefix + " event=KillSent local=1 sent=1 confirmed=0 total=2",
                Prefix + " event=KillSent local=0 sent=2 confirmed=0 total=2",
                Prefix + " event=KillConfirmed local=1 sent=2 confirmed=1 total=2",
                Prefix + " event=KillConfirmed local=0 sent=2 confirmed=2 total=2",
                Prefix + " event=MapCleared sent=2 confirmed=2 total=2",
                Prefix + " event=SettlementSent",
                Prefix + " event=SettlementArrived part=Result",
                Prefix + " event=SettlementArrived part=Reward",
                Prefix + " event=ReadyToReturnToTown",
                Prefix + " done outcome=ReturnReady phase=Settled monsters=2 sent=2 confirmed=2 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 passthrough=0 heartbeats=0 envelopes=0/0 innerSkipped=0 frames=5 passIds=-",
            },
            f.Log.Lines);
        Assert.All(f.Log.Lines, line => Assert.DoesNotContain("910", line));
    }

    /// <summary>被拦与被忽略的事件也有行:原因用枚举名,被忽略的帧写成 NOTI/包号 与判定。</summary>
    [Fact]
    public async Task Blocked_and_ignored_events_are_logged_with_enum_names()
    {
        var ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        var session = CombatFakeSession.ForServer(server);
        session.Push(CombatKit.Confirm(ids[1]));                       // 还没报就来了确认:乱序
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options(overallSeconds: 0.3));
        var log = new HostTestKit.LogSink();
        runner.PostKill(7);

        await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), log.Add), 20);

        Assert.Contains(Prefix + " event=KillBlocked local=7 block=UnknownLocalId", log.Lines);
        Assert.Contains(Prefix + " event=FrameIgnored frame=NOTI/38 verdict=OutOfOrder local=1", log.Lines);
    }

    /// <summary>事件行有上限(128 行),多的只记一行"还有 N 条";事件本身与回调一条不少。</summary>
    [Fact]
    public async Task The_event_lines_are_capped_but_the_events_and_the_callback_are_not()
    {
        var callbacks = new List<OnlineCombatEvent>();
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.OnEvent = callbacks.Add;
        var f = new Fight(150, options);
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        // 事件:150 条击杀发出 + 150 条确认 + 清图 + 结算发出 + 两段到达 + 回城就绪 = 305 条(另有开头的"怪已建表"不打行)
        Assert.Equal(306, r.Events.Count);
        Assert.Equal(r.Events.Count, callbacks.Count);
        Assert.Equal(r.Events.Select(e => e.Kind).ToArray(), callbacks.Select(e => e.Kind).ToArray());
        string[] lines = f.Log.Lines;
        Assert.Equal(1 + OnlineDungeonCombatRunner.MaxEventLines + 1 + 1, lines.Length);
        Assert.Equal(Prefix + " events omitted=177", lines[lines.Length - 2]);
        Assert.StartsWith(Prefix + " done outcome=ReturnReady ", lines[lines.Length - 1]);
    }

    /// <summary>宿主的事件回调与日志回调抛异常,不影响战斗循环。</summary>
    [Fact]
    public async Task Exceptions_from_the_callbacks_never_disturb_the_run()
    {
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.OnEvent = e => throw new InvalidOperationException("callback 900");
        var f = new Fight(2, options);
        f.PostAll();

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(
            f.Runner.RunAsync(f.Map, line => throw new InvalidOperationException("log 900")), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.Confirmed);
    }

    /// <summary>不给日志回调(null)也能跑。</summary>
    [Fact]
    public async Task A_null_log_callback_is_fine()
    {
        var f = new Fight(2);
        f.PostAll();

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(f.Runner.RunAsync(f.Map, null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
    }

    /// <summary>自定的载荷工厂经跑器也生效(换成带击杀者与攻击记录的拼法时,编号仍是对应记录的第 3 项)。</summary>
    [Fact]
    public async Task The_payload_factories_reach_the_wire_through_the_runner()
    {
        // 自定的结算载荷要照服务端读序读得满(n 字节为 0,共 33 字节),各忽略的字段放上可辨认的值
        byte[] customSettlement = new byte[33];
        customSettlement[0] = 7;
        customSettlement[1] = 9;
        customSettlement[2] = 0x11;
        customSettlement[5] = 0x21;
        customSettlement[32] = 0x33;
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.KillPayload = mobId => OnlineCombatPayloads.BuildKill(mobId, 901, new[] { new OnlineCombatAttack(0x111, 901, 500, 2) }, 11, 22);
        options.SettlementPayload = () => customSettlement;
        var f = new Fight(2, options);
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        KillView[] kills = f.Server.Kills;
        Assert.Equal(new[] { f.Ids[0], f.Ids[1] }, kills.Select(k => (int)k.MobId).ToArray());
        Assert.All(kills, k =>
        {
            Assert.Equal(901, k.Killer);
            Assert.Equal(1, k.AttackCount);
            Assert.Equal(54 + 10, k.Length);
        });
        Assert.Equal(customSettlement, f.Session.Sent[2].Payload);
    }

    // ======== 信封 ========

    /// <summary>
    /// 一批帧:561 信封帧加会话附在它后面的内层副本(普通帧,类、包号、载荷与信封里的相同)。跑器用解包委托拆开信封、处理里面的帧,
    /// 跳过紧随其后的那份副本(不然同一条通知会被数两遍,状态机把第二遍记成重复)。
    /// </summary>
    [Fact]
    public async Task Frames_inside_envelopes_are_opened_and_their_inner_copies_are_skipped()
    {
        var book = new DungeonEntryEnvelopeBook();
        var f = new Fight(1, unwrap: book.Unwrap);
        f.Session.Respond = null;                                        // 服务端替身不自己回:下面手工把每条回包装进信封灌进去
        Task<OnlineDungeonCombatResult> run = f.Run();
        int tag = 0;

        void PushEnveloped(CombatKit.ServerFrame frame)
        {
            byte[] sealedBody = book.Seal(DungeonEntryWire.Inner(frame.Class, frame.Id, frame.Payload), ++tag);
            f.Session.PushUnit(DungeonEntryWire.Decoded(CombatKit.Noti, 561, sealedBody), CombatKit.DecodedPlain(frame.Class, frame.Id, frame.Payload));
        }

        f.Runner.PostKill(0);
        await CombatKit.WaitUntil(() => f.Session.Commands(42) == 1, "击杀命令发出");
        PushEnveloped(CombatKit.Confirm(f.Ids[0]));
        PushEnveloped(CombatKit.Clear());
        await CombatKit.WaitUntil(() => f.Session.Commands(49) == 1, "清图之后发出结算命令");
        PushEnveloped(CombatKit.Result());
        PushEnveloped(CombatKit.Reward());

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(4, r.EnvelopesOpened);
        Assert.Equal(0, r.EnvelopesUnopened);
        Assert.Equal(4, r.InnerCopiesSkipped);
        Assert.Equal(1, r.Confirmed);
        Assert.Equal(0, r.Duplicates);                                   // 副本被跳过,没有被数成重复
        Assert.Equal(4, r.PassthroughCount);                             // 信封帧本身不在状态机的包集合里,记透传
        Assert.Equal(8, r.FramesSeen);                                   // 4 条信封 + 4 条副本
        Assert.Equal(4, book.Seen.Count);
        Assert.Contains(" envelopes=4/0 innerSkipped=4 frames=8 ", f.Log.Lines.Last());
    }

    /// <summary>没有解包委托:信封只计数、不拆,会话附上的内层副本当普通帧处理,链照样走通(副本既然没被跳过,就不会漏)。</summary>
    [Fact]
    public async Task Without_an_unwrap_delegate_the_envelopes_are_counted_and_the_inner_copies_do_the_work()
    {
        var f = new Fight(1);
        f.Session.Respond = null;                                        // 服务端替身不自己回:下面手工灌
        Task<OnlineDungeonCombatResult> run = f.Run();

        void PushWithCopy(CombatKit.ServerFrame frame)
        {
            f.Session.PushUnit(DungeonEntryWire.Decoded(CombatKit.Noti, 561, GatewayTestSupport.Pattern(33, 3, 5)), CombatKit.DecodedPlain(frame.Class, frame.Id, frame.Payload));
        }

        f.Runner.PostKill(0);
        await CombatKit.WaitUntil(() => f.Session.Commands(42) == 1, "击杀命令发出");
        PushWithCopy(CombatKit.Confirm(f.Ids[0]));
        PushWithCopy(CombatKit.Clear());
        await CombatKit.WaitUntil(() => f.Session.Commands(49) == 1, "结算命令发出");
        PushWithCopy(CombatKit.Result());
        PushWithCopy(CombatKit.Reward());

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(0, r.EnvelopesOpened);
        Assert.Equal(4, r.EnvelopesUnopened);
        Assert.Equal(0, r.InnerCopiesSkipped);
        Assert.Equal(1, r.Confirmed);
        Assert.Equal(8, r.FramesSeen);
        Assert.Equal(4, r.PassthroughCount);                             // 只有 4 条信封帧本身;内层副本走了正常处理
    }

    /// <summary>委托拆不开(返回 null、抛异常)或内层不成形:只计"没拆开",不中断,也不把后面的帧当副本跳过。</summary>
    [Fact]
    public async Task Envelopes_that_cannot_be_opened_are_counted_and_nothing_after_them_is_skipped()
    {
        int calls = 0;
        Func<byte[], byte[]> unwrap = payload =>
        {
            calls++;
            switch (calls)
            {
                case 1: return null;
                case 2: throw new InvalidOperationException("synthetic 900");
                case 3: return new byte[5];                                 // 连一个帧头都装不下
                default: return DungeonEntryWire.Inner(CombatKit.Noti, 901, new byte[3]);
            }
        };
        var f = new Fight(1, unwrap: unwrap);
        for (int i = 0; i < 4; i++)
        {
            // 每批:一个信封加一帧无关的普通帧(与信封里的内层不同):后者必须被处理,不能被误当副本
            f.Session.PushUnit(
                DungeonEntryWire.Decoded(CombatKit.Noti, 561, GatewayTestSupport.Pattern(33, i, 3)),
                CombatKit.Unrelated(920 + i).Decoded());
        }
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(1, r.EnvelopesOpened);                                 // 第 4 个拆开了
        Assert.Equal(3, r.EnvelopesUnopened);
        Assert.Equal(0, r.InnerCopiesSkipped);                              // 第 4 个信封后面那帧与内层不同,不是副本
        Assert.Equal(4, calls);
        Assert.Equal(4 + 4 + 1, r.PassthroughCount);                        // 4 个信封 + 4 帧无关帧 + 第 4 个信封里的无关内层帧(包号 901)
    }

    /// <summary>信封里装着心跳:照样回心跳(回包走同一条路径)。</summary>
    [Fact]
    public async Task A_heartbeat_inside_an_envelope_is_answered_too()
    {
        var book = new DungeonEntryEnvelopeBook();
        var f = new Fight(1, unwrap: book.Unwrap);
        byte[] sealedBody = book.Seal(DungeonEntryWire.Inner(CombatKit.Noti, 0, new byte[0]), 1);
        f.Session.PushUnit(DungeonEntryWire.Decoded(CombatKit.Noti, 561, sealedBody), CombatKit.DecodedPlain(CombatKit.Noti, 0, new byte[0]));
        f.PostAll();

        OnlineDungeonCombatResult r = await f.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(1, r.HeartbeatsAnswered);                              // 副本被跳过,所以只回一次
        Assert.Equal(1, r.InnerCopiesSkipped);
        Assert.Equal(1, f.Session.Commands(0));
    }
}
