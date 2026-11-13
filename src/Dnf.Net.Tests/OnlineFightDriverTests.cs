using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 无头战斗驱动(<see cref="OnlineFightDriver"/>)的测试:真的战斗跑器 + 照笔记建模的假服务端 + 线程安全的假会话 + 手拨假时钟。
/// 驱动的节奏与两段等待都跑在假时钟上(不真等);跑器按真时间轮询,所以断言"拒绝之后立即停"这类依赖先后的场景时,
/// 用 <see cref="FightRig.PostKills"/> 锁步推进(等真实的跑器处理完前一只的回复再拨一个间隔)。
/// 怪编号取 9100 起的合成值、副本号只在别处出现(901),没有任何真实数值。
/// </summary>
public class OnlineFightDriverTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(25);

    // ======== 正常路径 ========

    [Fact]
    public async Task The_normal_path_kills_every_monster_in_order_then_clears_settles_and_is_ready_to_return()
    {
        var rig = new FightRig(5);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(5);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(5, s.Monsters);
        Assert.Equal(5, s.Posted);
        Assert.Equal(5, s.Sent);
        Assert.Equal(5, s.Confirmed);
        Assert.Equal(0, s.Refused);
        Assert.Equal(0, s.Blocked);
        Assert.True(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.Complete, s.Settlement);
        Assert.True(s.ReturnReady);
        Assert.Equal(OnlineCombatPhase.Settled, s.Phase);
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, s.RunnerOutcome);
        Assert.Null(s.FaultType);
        Assert.Equal(-1, s.FirstRefusedLocal);
        Assert.Equal(0, s.Pending);
        Assert.Equal(OnlineFightKillVerdict.Accepted, s.KillVerdict);

        // 服务端视角:五条击杀按本地序号顺序,然后清图、结算各一次;结算没有早于清图
        Assert.Equal(rig.Kills(0, 5).Concat(new[] { "clear", "settle" }).ToArray(), rig.Server.Journal);
        Assert.Equal(5, rig.Session.Commands(42));
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Equal(0, rig.Server.PrematureSettlements);
        Assert.True(rig.Runner.Ended);
    }

    [Fact]
    public async Task Every_kill_is_posted_one_pace_apart_on_the_injected_clock()
    {
        var rig = new FightRig(4, paceMs: 250);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(4);

        OnlineFightSummary s = await rig.Pump(run);

        // 驱动的假时钟读数:第 0 只立刻排(相对开始是 0),之后每只正好隔一个间隔
        Assert.Equal(
            new[] { 0, 250, 500, 750 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            s.PostTimes.ToArray());
        // 节奏等待正好 N-1 次、每次正好 PaceMs,而且是最先发出的几个延时请求;最后一只之后不再等节奏
        TimeSpan[] requests = rig.Clock.Requests;
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(250), 3).ToArray(), requests.Take(3).ToArray());
        Assert.All(requests.Skip(3).Where(r => r != OnlineFightDriver.FinishWait), r => Assert.True(r <= rig.Options.PollInterval, "之后只剩等清图与等结算的小步"));
    }

    [Fact]
    public async Task A_zero_pace_posts_every_kill_at_once_without_waiting_on_the_clock()
    {
        var rig = new FightRig(4, paceMs: 0);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.WaitSent(4);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }, s.PostTimes.ToArray());
        Assert.DoesNotContain(rig.Clock.Requests, r => r == TimeSpan.Zero);
        Assert.Equal(rig.Kills(0, 4).Concat(new[] { "clear", "settle" }).ToArray(), rig.Server.Journal);
    }

    // ======== 被拒 ========

    [Fact]
    public async Task The_first_refusal_stops_the_kills_by_default()
    {
        // 服务端的表里没有第 2 只:它的击杀会被回"失败"
        var rig = new FightRig(6, table: CombatKit.Sequential(6).Where(id => id != CombatKit.BaseId + 2));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.WaitAnswered(3);
        await rig.Step();                           // 节奏等待到点:驱动醒来先看有没有被拒

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.RefusedStop, s.Outcome);
        Assert.Equal(3, s.Posted);
        Assert.Equal(3, s.Sent);
        Assert.Equal(2, s.Confirmed);
        Assert.Equal(1, s.Refused);
        Assert.Equal(2, s.FirstRefusedLocal);
        Assert.False(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.None, s.Settlement);
        Assert.False(s.ReturnReady);
        Assert.Equal(OnlineFightKillVerdict.Refused, s.KillVerdict);
        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, s.RunnerOutcome);

        // 服务端只见过三条击杀:被拒之后没有再发,更没有发结算
        Assert.Equal(new[] { "kill:9100", "kill:9101", "refuse:9102" }, rig.Server.Journal);
        Assert.Equal(0, rig.Session.Commands(49));
        Assert.Equal(0, rig.Server.PrematureSettlements);
        // 驱动没有进入等清图:除了(收尾时可能有的)收尾宽限,延时请求只有三次节奏等待
        Assert.Equal(
            Enumerable.Repeat(TimeSpan.FromMilliseconds(400), 3).ToArray(),
            rig.Clock.Requests.Where(r => r != OnlineFightDriver.FinishWait).ToArray());
        Assert.Contains(rig.Log.Lines, l => l == OnlineFightLog.Prefix + " refused local=2 action=stop");
    }

    [Fact]
    public async Task Continuing_on_refusal_posts_the_rest_and_still_finishes_with_the_refusal_counted()
    {
        var rig = new FightRig(6, continueOnRefuse: true, table: CombatKit.Sequential(6).Where(id => id != CombatKit.BaseId + 2));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(6);

        OnlineFightSummary s = await rig.Pump(run);

        // 剩下五只都确认、表空了服务端就说清图,链照常走完;被拒的那一只留在计数里
        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(6, s.Posted);
        Assert.Equal(6, s.Sent);
        Assert.Equal(5, s.Confirmed);
        Assert.Equal(1, s.Refused);
        Assert.Equal(2, s.FirstRefusedLocal);
        Assert.Equal(OnlineFightKillVerdict.Refused, s.KillVerdict);
        Assert.True(s.ReturnReady);
        Assert.Equal(
            new[] { "kill:9100", "kill:9101", "refuse:9102", "kill:9103", "kill:9104", "kill:9105", "clear", "settle" },
            rig.Server.Journal);
        Assert.Contains(rig.Log.Lines, l => l == OnlineFightLog.Prefix + " refused local=2 action=continue");
    }

    // ======== 两段等待 ========

    [Fact]
    public async Task Without_a_clear_notice_the_driver_waits_the_clear_window_from_the_last_kill_then_gives_up_without_settling()
    {
        var rig = new FightRig(3, withholdClear: true);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.WaitAnswered(3);
        await rig.WaitParked();                     // 驱动已排完最后一只,停在等清图的小步上

        // 窗口是 25 秒、从最后一只排完起算:差 100 毫秒到点时驱动醒来看一眼,仍然接着等
        rig.Clock.Advance(Window - TimeSpan.FromMilliseconds(100));
        await rig.WaitParked();
        Assert.False(run.IsCompleted, "窗口没到点,不能收场");

        rig.Clock.Advance(TimeSpan.FromMilliseconds(100));      // 正好到点
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Equal(3, s.Sent);
        Assert.Equal(3, s.Confirmed);
        Assert.False(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.None, s.Settlement);
        Assert.False(s.ReturnReady);
        Assert.Equal(OnlineCombatPhase.Fighting, s.Phase);
        Assert.Equal(OnlineFightKillVerdict.Accepted, s.KillVerdict);
        Assert.True(s.Elapsed >= Window);
        // 闸门一直关着:一条结算命令也没发,服务端也没记过早结算
        Assert.Equal(0, rig.Session.Commands(49));
        Assert.Equal(0, rig.Server.PrematureSettlements);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith(OnlineFightLog.Prefix + " phase=clearWait"));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("phase=settleWait"));
    }

    [Fact]
    public async Task Only_one_settlement_part_is_an_incomplete_settlement()
    {
        var rig = new FightRig(2);
        // 服务端只回通关结果、不回通关奖励
        rig.Session.Respond = (cls, id, payload) => rig.Server.Handle(cls, id, payload).Where(f => !(f.Class == CombatKit.Noti && f.Id == 0x23));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.PumpUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await rig.WaitEvent(OnlineCombatEventKind.SettlementArrived);
        await rig.WaitParked();

        rig.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s.Outcome);
        Assert.True(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.Partial, s.Settlement);
        Assert.False(s.ReturnReady);
        Assert.Equal(OnlineCombatPhase.Settling, s.Phase);
        Assert.Equal(2, s.Sent);
        Assert.Equal(2, s.Confirmed);
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, s.RunnerOutcome);
    }

    [Fact]
    public async Task A_silent_server_after_the_settlement_command_leaves_the_settlement_empty()
    {
        var rig = new FightRig(1);
        rig.Session.Respond = (cls, id, payload) => id == 49 ? null : rig.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(1);
        await rig.PumpUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await CombatKit.WaitUntil(() => rig.Session.Commands(49) == 1, "结算命令发出");
        await rig.WaitParked();

        rig.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s.Outcome);
        Assert.True(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.None, s.Settlement);
        Assert.False(s.ReturnReady);
    }

    // ======== 断线 ========

    [Fact]
    public async Task A_session_closed_in_the_middle_of_the_kills_is_a_closed_session()
    {
        var rig = new FightRig(5);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.WaitAnswered(2);

        rig.Session.Closed = true;                  // 不拨假时钟:跑器一收场,驱动就该立刻收场
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SessionClosed, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, s.RunnerOutcome);
        Assert.Equal(2, s.Posted);
        Assert.Equal(2, s.Sent);
        Assert.Equal(2, s.Confirmed);
        Assert.False(s.ClearNoticeSeen);
        Assert.False(s.ReturnReady);
        Assert.False(rig.Runner.PostKill(2));       // 跑器已收场,不再收击杀
    }

    [Fact]
    public async Task A_session_closed_while_waiting_for_the_clear_notice_is_not_mistaken_for_a_missing_notice()
    {
        var rig = new FightRig(2, withholdClear: true);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.WaitAnswered(2);
        await rig.WaitParked();

        rig.Session.Closed = true;
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SessionClosed, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, s.RunnerOutcome);
        Assert.Equal(2, s.Confirmed);
        Assert.True(s.Elapsed < Window, "没有等满窗口");
    }

    [Fact]
    public async Task A_framing_fault_also_counts_as_a_closed_session()
    {
        var rig = new FightRig(3);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.WaitSent(1);
        await rig.WaitAnswered(1);

        rig.Session.Fault = FrameError.LengthAboveMaximum;
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SessionClosed, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.FramingFault, s.RunnerOutcome);
    }

    // ======== 没有怪 ========

    [Fact]
    public async Task With_no_monsters_there_is_nothing_to_post_and_the_server_notice_still_opens_the_gate()
    {
        var rig = new FightRig(0);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.WaitParked();                     // 没有击杀可排:一开始就在等清图通知
        Assert.Equal(0, rig.Machine.Total);

        rig.Session.Push(rig.Server.ReleaseClear());
        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(0, s.Monsters);
        Assert.Equal(0, s.Posted);
        Assert.Equal(0, s.Sent);
        Assert.Equal(OnlineFightKillVerdict.None, s.KillVerdict);
        Assert.True(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.Complete, s.Settlement);
        Assert.True(s.ReturnReady);
        Assert.Empty(rig.Server.Kills);
        Assert.Equal(1, rig.Server.Settlements);
    }

    [Fact]
    public async Task With_no_monsters_and_a_silent_server_the_clear_window_runs_out()
    {
        var rig = new FightRig(0);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.WaitParked();

        rig.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Equal(0, s.Posted);
        Assert.Equal(0, s.Sent);
        Assert.Equal(OnlineFightKillVerdict.None, s.KillVerdict);
        Assert.False(s.ClearNoticeSeen);
        Assert.True(rig.Clock.Requests[0] <= rig.Options.PollInterval, "第一个延时是等清图的小步,不是节奏等待");
        Assert.Equal(0, rig.Session.Commands(49));
    }

    // ======== 取消 ========

    [Fact]
    public async Task Cancelling_in_the_middle_of_the_kills_stops_at_once_without_touching_the_clock()
    {
        var rig = new FightRig(5);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.WaitAnswered(2);

        rig.Cts.Cancel();                           // 不拨假时钟:取消必须自己把等待打断
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Cancelled, s.Outcome);
        Assert.Equal(2, s.Posted);
        Assert.Equal(2, s.Confirmed);
        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, s.RunnerOutcome);
        Assert.True(rig.Runner.Ended);
        Assert.False(rig.Runner.PostKill(2));
        Assert.Equal(2, rig.Server.Kills.Length);
        Assert.Equal(0, rig.Clock.Pending);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_clear_notice_and_while_waiting_for_the_settlement_both_stop_at_once()
    {
        var waitingForClear = new FightRig(2, withholdClear: true);
        Task<OnlineFightSummary> run1 = await waitingForClear.Start();
        await waitingForClear.PostKills(2);
        await waitingForClear.WaitAnswered(2);
        await waitingForClear.WaitParked();
        waitingForClear.Cts.Cancel();
        OnlineFightSummary s1 = await waitingForClear.Done(run1);
        Assert.Equal(OnlineFightOutcome.Cancelled, s1.Outcome);
        Assert.Equal(0, waitingForClear.Session.Commands(49));

        var waitingForSettlement = new FightRig(1);
        waitingForSettlement.Session.Respond = (cls, id, payload) => id == 49 ? null : waitingForSettlement.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run2 = await waitingForSettlement.Start();
        await waitingForSettlement.PostKills(1);
        await waitingForSettlement.PumpUntil(() => waitingForSettlement.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await waitingForSettlement.WaitParked();
        waitingForSettlement.Cts.Cancel();
        OnlineFightSummary s2 = await waitingForSettlement.Done(run2);
        Assert.Equal(OnlineFightOutcome.Cancelled, s2.Outcome);
        Assert.True(s2.ClearNoticeSeen);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_posts_nothing()
    {
        var rig = new FightRig(3);
        rig.Cts.Cancel();

        Task<OnlineFightSummary> run = await rig.Start();
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Cancelled, s.Outcome);
        Assert.Equal(0, s.Posted);
        Assert.Equal(0, s.Sent);
        Assert.Equal(0, rig.Session.Commands(42));
        Assert.True(rig.Runner.Ended);
    }

    // ======== 心跳、发送失败 ========

    [Fact]
    public async Task A_heartbeat_during_the_wait_is_answered_by_the_runner_with_the_product_reply()
    {
        var rig = new FightRig(2, withholdClear: true);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.WaitAnswered(2);
        await rig.WaitParked();

        rig.Session.Push(CombatKit.Heartbeat());
        await CombatKit.WaitUntil(() => rig.Session.Commands((int)OnlineHeartbeat.ReplyPacketId) == 1, "心跳回包发出");
        rig.Cts.Cancel();
        OnlineFightSummary s = await rig.Done(run);

        // 回的是产品既有的那一帧(类 1、包号 0、八个零字节),驱动没有另造;计数进了汇总
        (PacketClass Class, ushort Id, byte[] Payload) reply = rig.Session.Sent.Single(x => x.Class == PacketClass.Cmd && x.Id == OnlineHeartbeat.ReplyPacketId);
        Assert.Equal(OnlineHeartbeat.ReplyPayload, reply.Payload);
        Assert.Equal(new byte[8], reply.Payload);
        Assert.Equal(1, s.HeartbeatsAnswered);
        Assert.Equal(OnlineFightOutcome.Cancelled, s.Outcome);
    }

    [Fact]
    public async Task A_send_exception_is_a_fault_and_only_its_type_name_is_reported()
    {
        var rig = new FightRig(2);
        rig.Session.FailureOfSend = index => new IOException("secret-detail-12345");
        Task<OnlineFightSummary> run = await rig.Start();

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Faulted, s.Outcome);
        Assert.Equal("IOException", s.FaultType);
        Assert.Equal(OnlineDungeonCombatOutcome.Faulted, s.RunnerOutcome);
        Assert.False(s.ReturnReady);
        string text = string.Join("\n", rig.Log.Lines);
        Assert.DoesNotContain("secret", text);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith(OnlineFightLog.Prefix + " end outcome=Faulted ") && l.EndsWith(" exception=IOException"));
    }

    [Fact]
    public async Task A_frame_that_cannot_be_encoded_ends_the_run_as_faulted_with_the_send_counted_as_blocked()
    {
        var rig = new FightRig(2);
        rig.Session.StatusOfSend = index => FrameStatus.EncryptFailed;
        Task<OnlineFightSummary> run = await rig.Start();

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Faulted, s.Outcome);
        Assert.Null(s.FaultType);
        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, s.RunnerOutcome);
        Assert.Equal(0, s.Sent);
        Assert.Equal(1, s.Blocked);
        Assert.Equal(OnlineFightKillVerdict.None, s.KillVerdict);
    }

    // ======== 清图通知先到、被拒的各种先后 ========

    [Fact]
    public async Task Once_the_server_says_the_map_is_cleared_the_remaining_monsters_are_not_posted()
    {
        // 服务端的表里只有前三只:三只确认之后表空了,它就说清图;后两只不必再报。
        // 结算命令无人应答,跑器不会提前收场,所以驱动必须自己在节奏等待到点之后看到清图、不再排击杀。
        var rig = new FightRig(5, table: CombatKit.Sequential(5).Take(3));
        rig.Session.Respond = (cls, id, payload) => id == 49 ? null : rig.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.WaitEvent(OnlineCombatEventKind.MapCleared);
        await rig.Step();                           // 节奏等待到点:驱动醒来先看到清图
        await rig.PumpUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await rig.WaitParked();

        rig.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s.Outcome);
        Assert.Equal(5, s.Monsters);
        Assert.Equal(3, s.Posted);
        Assert.Equal(3, s.Sent);
        Assert.Equal(3, s.Confirmed);
        Assert.Equal(0, s.Blocked);
        Assert.True(s.ClearNoticeSeen);
        Assert.Equal(3, rig.Server.Kills.Length);
        Assert.Equal(rig.Kills(0, 3).Concat(new[] { "clear" }).ToArray(), rig.Server.Journal);
    }

    [Fact]
    public async Task A_refusal_of_the_last_monster_is_noticed_while_waiting_for_the_clear_notice()
    {
        // 最后一只被拒,服务端也不会说清图:驱动在等清图的小步里发现被拒,不必等满窗口
        var rig = new FightRig(3, withholdClear: true, table: CombatKit.Sequential(3).Where(id => id != CombatKit.BaseId + 2));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.WaitAnswered(3);
        await rig.Step();                           // 等清图的一个小步

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.RefusedStop, s.Outcome);
        Assert.Equal(3, s.Sent);
        Assert.Equal(2, s.Confirmed);
        Assert.Equal(1, s.Refused);
        Assert.Equal(2, s.FirstRefusedLocal);
        Assert.False(s.ClearNoticeSeen);
        Assert.True(s.Elapsed < Window, "没有等满清图窗口");
        Assert.Equal(0, rig.Session.Commands(49));
    }

    [Fact]
    public async Task A_refusal_outranks_a_closed_session_in_the_default_mode()
    {
        var rig = new FightRig(4, table: CombatKit.Sequential(4).Where(id => id != CombatKit.BaseId + 1));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);                     // 第 0 只确认、第 1 只被拒
        await rig.WaitAnswered(2);

        rig.Session.Closed = true;                  // 被拒之后连接又被掐断:原因先于后果,结局记被拒
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.RefusedStop, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, s.RunnerOutcome);
        Assert.Equal(1, s.FirstRefusedLocal);
        Assert.Equal(1, s.Refused);
    }

    [Fact]
    public async Task Continuing_after_refusals_without_a_clear_notice_ends_as_a_missing_notice_and_logs_only_the_first_refusal()
    {
        // 表是空的:每一只都被拒;继续模式照排,没有清图通知,窗口耗尽
        var rig = new FightRig(3, continueOnRefuse: true, table: new int[0]);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.WaitAnswered(3);
        await rig.WaitParked();

        rig.Clock.Advance(Window + TimeSpan.FromSeconds(1));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Equal(3, s.Sent);
        Assert.Equal(0, s.Confirmed);
        Assert.Equal(3, s.Refused);
        Assert.Equal(0, s.FirstRefusedLocal);
        Assert.Equal(OnlineFightKillVerdict.Refused, s.KillVerdict);
        Assert.Single(rig.Log.Lines, l => l.StartsWith(OnlineFightLog.Prefix + " refused "));
    }

    // ======== 两段窗口的起点与精度 ========

    [Fact]
    public async Task The_settlement_window_is_counted_from_the_clear_notice_not_from_the_start()
    {
        var rig = new FightRig(1, withholdClear: true, clearWait: TimeSpan.FromSeconds(100), settleWait: TimeSpan.FromSeconds(10));
        rig.Session.Respond = (cls, id, payload) => id == 49 ? null : rig.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(1);
        await rig.WaitAnswered(1);
        await rig.WaitParked();

        rig.Clock.Advance(TimeSpan.FromSeconds(60));            // 等清图等了 60 秒,窗口 100 秒,还在等
        await rig.WaitParked();
        Assert.False(run.IsCompleted);
        rig.Session.Push(rig.Server.ReleaseClear());            // 服务端这时才说清图
        await rig.WaitEvent(OnlineCombatEventKind.MapCleared);
        await rig.Step();                                       // 驱动醒来看到清图,开始等结算
        await CombatKit.WaitUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")), "进入等结算");
        await rig.WaitParked();
        Assert.False(run.IsCompleted, "结算窗口从清图通知起算,不是从开始起算");

        rig.Clock.Advance(TimeSpan.FromSeconds(9.9));
        await rig.WaitParked();
        Assert.False(run.IsCompleted, "10 秒窗口还差 100 毫秒");
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s.Outcome);
        Assert.True(s.ClearNoticeSeen);
        Assert.Equal(OnlineFightSettlement.None, s.Settlement);
        Assert.Equal(1, rig.Session.Commands(49));
    }

    [Fact]
    public async Task The_clear_window_option_is_honoured_to_the_millisecond()
    {
        // 7.03 秒不是 50 毫秒步长的整数倍:最后一步必须缩短到刚好到点
        var rig = new FightRig(1, withholdClear: true, clearWait: TimeSpan.FromMilliseconds(7030));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(1);
        await rig.WaitAnswered(1);
        await rig.WaitParked();

        rig.Clock.Advance(TimeSpan.FromSeconds(7));
        await rig.WaitParked();
        Assert.False(run.IsCompleted, "还差 30 毫秒");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(30));       // 驱动请求的最后一个小步正好是 30 毫秒
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Contains(TimeSpan.FromMilliseconds(30), rig.Clock.Requests);
    }

    // ======== 跑器自己先超时 ========

    [Fact]
    public async Task A_runner_that_times_out_first_is_mapped_by_the_phase_it_was_in()
    {
        // 战斗中:跑器的整段上限先到(真时间),清图通知没来
        var fighting = new FightRig(2, withholdClear: true, runnerOverallSeconds: 0.5);
        Task<OnlineFightSummary> run1 = await fighting.Start();
        await fighting.PostKills(2);
        OnlineFightSummary s1 = await fighting.Done(run1);
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, s1.RunnerOutcome);
        Assert.Equal(OnlineFightOutcome.NoClearNotice, s1.Outcome);
        Assert.False(s1.ClearNoticeSeen);

        // 结算中:服务端对结算命令一声不吭,跑器的结算上限先到
        var settling = new FightRig(1, runnerSettlementSeconds: 0.3);
        settling.Session.Respond = (cls, id, payload) => id == 49 ? null : settling.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run2 = await settling.Start();
        await settling.PostKills(1);
        OnlineFightSummary s2 = await settling.Pump(run2);
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, s2.RunnerOutcome);
        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s2.Outcome);
        Assert.True(s2.ClearNoticeSeen);
        Assert.Equal(OnlineCombatPhase.Settling, s2.Phase);
    }

    // ======== 只能跑一次、日志 ========

    [Fact]
    public async Task A_driver_runs_once_and_refuses_a_runner_that_has_already_run()
    {
        var rig = new FightRig(1);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(1);
        await rig.Pump(run);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Driver.RunAsync());
        var second = new OnlineFightDriver(rig.Runner, rig.Map, rig.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.RunAsync());
    }

    [Fact]
    public async Task The_driver_lines_name_the_phases_in_order_and_no_line_carries_a_server_number()
    {
        var rig = new FightRig(3);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.Pump(run);

        string[] lines = rig.Log.Lines;
        string[] own = lines.Where(l => l.StartsWith(OnlineFightLog.Prefix)).ToArray();
        Assert.Equal(
            new[]
            {
                OnlineFightLog.Prefix + " begin monsters=3 paceMs=400 clearWaitMs=25000 settleWaitMs=25000 continueOnRefuse=0",
                OnlineFightLog.Prefix + " phase=kills posted=0",
                OnlineFightLog.Prefix + " phase=clearWait posted=3",
                OnlineFightLog.Prefix + " phase=settleWait posted=3",
                OnlineFightLog.Prefix + " end outcome=AllDone monsters=3 sent=3 confirmed=3 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1 posted=3 pending=0 heartbeats=0 runner=ReturnReady",
            },
            own);
        // 跑器自己的行也走同一个回调
        Assert.Contains(lines, l => l.StartsWith(OnlineCombatLog.Prefix + " begin monsters=3 "));
        Assert.Contains(lines, l => l.StartsWith(OnlineCombatLog.Prefix + " done outcome=ReturnReady "));
        // 任何一行里都没有合成的服务端怪编号、记录里的各项数值
        for (int i = 0; i < 3; i++)
        {
            foreach (int number in new[] { CombatKit.BaseId + i, CombatKit.FieldA(i), CombatKit.FieldB(i), CombatKit.FieldD(i) })
            {
                Assert.DoesNotContain(lines, l => l.Contains(number.ToString()));
            }
        }
    }

    [Fact]
    public async Task A_log_callback_that_throws_does_not_disturb_the_run()
    {
        var rig = new FightRig(2);
        Task<OnlineFightSummary> run = Task.Run(() => rig.Driver.RunAsync(line => throw new InvalidOperationException("log sink broke"), rig.Cts.Token));
        await CombatKit.WaitUntil(() => rig.Runner.Machine != null, "跑器起来了");
        await rig.PostKills(2);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(2, s.Confirmed);
    }
}
