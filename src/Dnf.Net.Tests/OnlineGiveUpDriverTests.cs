using System.Text.RegularExpressions;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// <c>--give-up-on-failure</c> 在战斗驱动(<see cref="OnlineFightDriver"/>)层的测试:真的战斗跑器 + 照笔记建模的假服务端 + 线程安全的假会话 + 手拨假时钟。
/// 三种异常收场(被拒即停、没等到清图通知、结算不完整)开着开关时,收尾处请求跑器发且只发一次 1/45,再在上限内等回城信号;
/// 关着开关(缺省)时一条 1/45 也不发、汇总里没有放弃、日志里没有 giveup 行。成功、断线、异常、取消各占一条不触发的用例。
/// 怪编号 9100 起、副本号只在别处出现,没有任何真实数值。
/// </summary>
public class OnlineGiveUpDriverTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(3);

    public static TheoryData<OnlineFightOutcome> TriggerOutcomes()
    {
        return new TheoryData<OnlineFightOutcome>
        {
            OnlineFightOutcome.RefusedStop,
            OnlineFightOutcome.NoClearNotice,
            OnlineFightOutcome.SettleIncomplete,
        };
    }

    public static TheoryData<OnlineFightOutcome, bool> TriggerOutcomesByOrder()
    {
        var data = new TheoryData<OnlineFightOutcome, bool>();
        foreach (OnlineFightOutcome outcome in new[] { OnlineFightOutcome.RefusedStop, OnlineFightOutcome.NoClearNotice, OnlineFightOutcome.SettleIncomplete })
        {
            data.Add(outcome, false);
            data.Add(outcome, true);
        }
        return data;
    }

    public static TheoryData<OnlineFightOutcome, bool> TriggerOutcomesBySwitchSetting()
    {
        var data = new TheoryData<OnlineFightOutcome, bool>();
        foreach (OnlineFightOutcome outcome in new[] { OnlineFightOutcome.RefusedStop, OnlineFightOutcome.NoClearNotice, OnlineFightOutcome.SettleIncomplete })
        {
            data.Add(outcome, false);   // 显式关
            data.Add(outcome, true);    // 压根不设这个属性(缺省路径)
        }
        return data;
    }

    private static GiveUpRig RigFor(OnlineFightOutcome outcome, bool? giveUp = true, TimeSpan? wait = null, Func<byte[], byte[]> unwrap = null)
    {
        switch (outcome)
        {
            case OnlineFightOutcome.RefusedStop:
                return GiveUpRig.ForRefusedStop(giveUp, wait, unwrap);
            case OnlineFightOutcome.NoClearNotice:
                return GiveUpRig.ForNoClearNotice(giveUp, wait, unwrap);
            case OnlineFightOutcome.SettleIncomplete:
                return GiveUpRig.ForSettleIncomplete(giveUp, wait, unwrap);
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static string[] GiveUpLines(GiveUpRig rig)
    {
        return rig.Log.Lines.Where(l => l.StartsWith(OnlineFightLog.GiveUpPrefix, StringComparison.Ordinal)).ToArray();
    }

    private static string Step(string token, OnlineFightOutcome reason)
    {
        return OnlineFightLog.GiveUpStep(token, reason.ToString());
    }

    private static string[] DriverLines(GiveUpRig rig)
    {
        return rig.Log.Lines.Where(l => l.StartsWith(OnlineFightLog.Prefix, StringComparison.Ordinal)).ToArray();
    }

    // 三种异常收场在"放弃之前那一刻"的计数(驱动汇总冻结在这一刻:放弃命令与迟到帧都不改它)
    private static void AssertAsAtTheFailure(OnlineFightOutcome outcome, OnlineFightSummary s, GiveUpRig rig)
    {
        Assert.Equal(outcome, s.Outcome);
        switch (outcome)
        {
            case OnlineFightOutcome.RefusedStop:
                Assert.Equal(3, s.Posted);
                Assert.Equal(3, s.Sent);
                Assert.Equal(2, s.Confirmed);
                Assert.Equal(1, s.Refused);
                Assert.Equal(2, s.FirstRefusedLocal);
                Assert.False(s.ClearNoticeSeen);
                Assert.Equal(OnlineFightSettlement.None, s.Settlement);
                Assert.Equal(OnlineCombatPhase.Fighting, s.Phase);
                Assert.Equal(3, rig.Session.Commands(42));
                Assert.Equal(0, rig.Session.Commands(49));
                break;
            case OnlineFightOutcome.NoClearNotice:
                Assert.Equal(3, s.Sent);
                Assert.Equal(3, s.Confirmed);
                Assert.False(s.ClearNoticeSeen);
                Assert.Equal(OnlineFightSettlement.None, s.Settlement);
                Assert.Equal(OnlineCombatPhase.Fighting, s.Phase);
                Assert.Equal(3, rig.Session.Commands(42));
                Assert.Equal(0, rig.Session.Commands(49));
                break;
            case OnlineFightOutcome.SettleIncomplete:
                Assert.Equal(2, s.Sent);
                Assert.Equal(2, s.Confirmed);
                Assert.True(s.ClearNoticeSeen);
                Assert.Equal(OnlineFightSettlement.Partial, s.Settlement);
                Assert.Equal(OnlineCombatPhase.Settling, s.Phase);
                Assert.Equal(2, rig.Session.Commands(42));
                Assert.Equal(1, rig.Session.Commands(49));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        Assert.False(s.ReturnReady);
    }

    // ======== 开关打开:发且只发一次,等到回城信号 ========

    [Theory]
    [MemberData(nameof(TriggerOutcomesByOrder))]
    public async Task A_failed_fight_sends_exactly_one_give_up_and_reports_returned_when_the_pair_arrives(OnlineFightOutcome outcome, bool reversedPair)
    {
        GiveUpRig rig = RigFor(outcome);
        rig.OnGiveUp = () => reversedPair ? new[] { GiveUpKit.Noti24(), GiveUpKit.Noti23() } : GiveUpKit.Pair();
        Task<OnlineFightSummary> run = await rig.Drive(outcome);

        OnlineFightSummary s = await rig.Done(run);

        // 放弃命令:类 1、包号 45、载荷是产品既有的那一帧,整个过程只有一条
        Assert.Equal(1, rig.Session.Commands(45));
        (PacketClass Class, ushort Id, byte[] Payload) give = rig.Session.Sent.Single(x => x.Class == PacketClass.Cmd && x.Id == 45);
        Assert.Equal(SessionLifecyclePayloads.BuildGiveUpGame(), give.Payload);
        Assert.Equal(45, rig.Session.Sent.Last().Id);       // 放弃之后没有再发任何东西

        // 结局与计数停在放弃之前,退出码仍由原结局决定
        AssertAsAtTheFailure(outcome, s, rig);
        Assert.Equal(outcome == OnlineFightOutcome.SettleIncomplete ? 12 : (outcome == OnlineFightOutcome.NoClearNotice ? 11 : 10), OnlineFightExitCodes.ForOutcome(s.Outcome));
        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, s.RunnerOutcome);
        Assert.Null(s.FaultType);

        // 放弃汇总
        Assert.NotNull(s.GiveUp);
        Assert.Equal(OnlineGiveUpOutcome.Returned, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.ReturnedToTown);
        Assert.True(s.GiveUp.Sent);
        Assert.Equal(outcome.ToString(), s.GiveUp.Reason);
        Assert.Null(s.GiveUp.FaultType);

        // 脱敏日志:先 sent、后 returned,各一行,reason 只是枚举名
        Assert.Equal(new[] { Step("sent", outcome), Step("returned", outcome) }, GiveUpLines(rig));
        // 结果行:字段多一个 giveUp=returned,其余与以前同形
        OnlineFightArguments flag = ParseWithSwitch();
        Assert.Equal(OnlineFightLog.ResultFields(s) + " giveUp=returned", OnlineFightLog.ResultFieldsFor(flag, s, null, s.GiveUp));
    }

    private static OnlineFightArguments ParseWithSwitch()
    {
        Assert.True(OnlineFightArguments.TryParse(new[] { "--dungeon", "901", "0", "--give-up-on-failure" }, out OnlineFightArguments parsed, out _));
        return parsed;
    }

    [Theory]
    [MemberData(nameof(TriggerOutcomes))]
    public async Task A_silent_server_is_waited_for_up_to_the_cap_then_the_give_up_times_out(OnlineFightOutcome outcome)
    {
        GiveUpRig rig = RigFor(outcome, wait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(outcome);

        // 命令已发出、服务端不吭声:驱动在假时钟上等,不是立刻放弃等待
        await rig.WaitParked();
        Assert.False(run.IsCompleted);
        rig.Clock.Advance(ShortWait - TimeSpan.FromMilliseconds(100));
        await rig.WaitParked();
        Assert.False(run.IsCompleted, "上限还差 100 毫秒,不能收场");

        rig.Clock.Advance(TimeSpan.FromMilliseconds(100));      // 正好到点
        OnlineFightSummary s = await rig.Done(run);

        Assert.NotNull(s.GiveUp);
        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.Sent);
        Assert.False(s.GiveUp.ReturnedToTown);
        Assert.True(s.GiveUp.Elapsed >= ShortWait, "放弃的用时至少是上限:" + s.GiveUp.Elapsed);
        Assert.Equal(1, rig.Session.Commands(45));              // 超时了也绝不重发
        AssertAsAtTheFailure(outcome, s, rig);
        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, s.RunnerOutcome);   // 等满之后驱动让跑器停下
        Assert.Equal(new[] { Step("sent", outcome), Step("timeout", outcome) }, GiveUpLines(rig));
        Assert.Equal(OnlineFightLog.ResultFields(s) + " giveUp=timeout", OnlineFightLog.ResultFieldsFor(ParseWithSwitch(), s, null, s.GiveUp));
    }

    [Fact]
    public async Task The_default_cap_is_fifteen_seconds_on_the_injected_clock()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice();
        Assert.Equal(TimeSpan.FromSeconds(15), rig.Options.GiveUpWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);

        await rig.WaitParked();
        rig.Clock.Advance(TimeSpan.FromSeconds(14.9));
        await rig.WaitParked();
        Assert.False(run.IsCompleted, "15 秒的上限还差 100 毫秒");

        rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.Elapsed >= TimeSpan.FromSeconds(15));
        Assert.True(s.GiveUp.Elapsed < TimeSpan.FromSeconds(16));
    }

    [Fact]
    public async Task A_return_signal_just_inside_the_cap_still_counts_as_returned()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice(giveUpWait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);

        await rig.WaitParked();
        rig.Clock.Advance(ShortWait - TimeSpan.FromMilliseconds(100));
        await rig.WaitParked();
        Assert.False(run.IsCompleted);

        rig.Session.Push(GiveUpKit.Pair());                     // 差 100 毫秒到上限时回城信号到了
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Returned, s.GiveUp.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, s.RunnerOutcome);
        Assert.Equal(1, rig.Session.Commands(45));
    }

    // ======== 开关关着(缺省):一条 1/45 也不发,没有任何放弃的痕迹 ========

    [Theory]
    [MemberData(nameof(TriggerOutcomesBySwitchSetting))]
    public async Task Without_the_switch_no_give_up_is_sent_and_nothing_about_it_shows_anywhere(OnlineFightOutcome outcome, bool notSetAtAll)
    {
        GiveUpRig rig = RigFor(outcome, giveUp: notSetAtAll ? (bool?)null : false);
        rig.OnGiveUp = () => GiveUpKit.Pair();                  // 就算有人回应,也没有人去问
        Assert.False(rig.Options.GiveUpOnFailure);
        Task<OnlineFightSummary> run = await rig.Drive(outcome);

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Null(s.GiveUp);
        AssertAsAtTheFailure(outcome, s, rig);
        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, s.RunnerOutcome);    // 与以前一样:驱动收场时让跑器取消
        Assert.Empty(GiveUpLines(rig));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("giveup", StringComparison.OrdinalIgnoreCase));
        // 收场行与结果行逐字就是以前的拼法
        Assert.Equal(OnlineFightLog.End(s), DriverLines(rig).Last());
        Assert.DoesNotContain("giveUp", OnlineFightLog.End(s));
        Assert.DoesNotContain("giveUp", s.ToString());
        Assert.Equal(OnlineFightLog.ResultFields(s), s.ToString());
    }

    [Theory]
    [MemberData(nameof(TriggerOutcomes))]
    public async Task The_driver_lines_are_identical_with_the_switch_off_and_with_it_never_set(OnlineFightOutcome outcome)
    {
        GiveUpRig off = RigFor(outcome, giveUp: false);
        GiveUpRig unset = RigFor(outcome, giveUp: null);
        OnlineFightSummary sOff = await off.Done(await off.Drive(outcome));
        OnlineFightSummary sUnset = await unset.Done(await unset.Drive(outcome));

        Assert.Equal(DriverLines(unset), DriverLines(off));
        Assert.Equal(OnlineFightLog.ResultFields(sUnset), OnlineFightLog.ResultFields(sOff));
        Assert.Equal(unset.Session.Sent.Select(x => (x.Class, x.Id)).ToArray(), off.Session.Sent.Select(x => (x.Class, x.Id)).ToArray());
    }

    // ======== 不触发:成功、断线、异常、取消(开关都是开着的) ========

    [Fact]
    public async Task A_successful_run_never_gives_up()
    {
        var rig = new GiveUpRig(3);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.True(s.ReturnReady);
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, s.RunnerOutcome);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Empty(GiveUpLines(rig));
    }

    [Fact]
    public async Task A_closed_session_never_gives_up()
    {
        var rig = new GiveUpRig(5);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.WaitAnswered(2);

        rig.Session.Closed = true;
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SessionClosed, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, s.RunnerOutcome);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Empty(GiveUpLines(rig));
    }

    [Fact]
    public async Task A_framing_fault_never_gives_up()
    {
        var rig = new GiveUpRig(3);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.WaitSent(1);
        await rig.WaitAnswered(1);

        rig.Session.Fault = FrameError.LengthAboveMaximum;
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SessionClosed, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.FramingFault, s.RunnerOutcome);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Empty(GiveUpLines(rig));
    }

    [Fact]
    public async Task A_faulted_run_never_gives_up_and_the_exception_message_stays_out_of_the_log()
    {
        var rig = new GiveUpRig(2);
        rig.Session.FailureOfSend = index => new IOException("secret-detail-77031");
        Task<OnlineFightSummary> run = await rig.Start();

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Faulted, s.Outcome);
        Assert.Equal("IOException", s.FaultType);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Empty(GiveUpLines(rig));
        Assert.DoesNotContain("secret", string.Join("\n", rig.Log.Lines));
    }

    [Fact]
    public async Task A_frame_that_cannot_be_encoded_is_a_fault_and_never_gives_up()
    {
        var rig = new GiveUpRig(2);
        rig.Session.StatusOfSend = index => FrameStatus.EncryptFailed;
        Task<OnlineFightSummary> run = await rig.Start();

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Faulted, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, s.RunnerOutcome);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Single(rig.Session.Sent);                        // 只试了那一次发送,没有再试
        Assert.Empty(GiveUpLines(rig));
    }

    [Fact]
    public async Task A_user_abort_while_waiting_for_the_clear_notice_stops_at_once_and_never_gives_up()
    {
        var rig = GiveUpRig.ForNoClearNotice();
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);
        await rig.WaitAnswered(3);
        await rig.WaitParked();

        rig.Cts.Cancel();                                       // 不拨假时钟:取消必须自己把等待打断
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Cancelled, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.Cancelled, s.RunnerOutcome);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Equal(0, rig.Session.Commands(49));
        Assert.Empty(GiveUpLines(rig));
        Assert.Equal(0, rig.Clock.Pending);
    }

    [Fact]
    public async Task A_user_abort_while_waiting_for_the_settlement_stops_at_once_and_never_gives_up()
    {
        var rig = GiveUpRig.ForSettleIncomplete();
        rig.Session.Respond = (cls, id, payload) => id == 49 ? null : rig.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.PumpUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await rig.WaitParked();

        rig.Cts.Cancel();
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Cancelled, s.Outcome);
        Assert.True(s.ClearNoticeSeen);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Empty(GiveUpLines(rig));
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_posts_nothing_and_never_gives_up()
    {
        var rig = new GiveUpRig(3);
        rig.Cts.Cancel();

        Task<OnlineFightSummary> run = await rig.Start();
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.Cancelled, s.Outcome);
        Assert.Equal(0, s.Posted);
        Assert.Null(s.GiveUp);
        Assert.Empty(rig.Session.Sent);
        Assert.Empty(GiveUpLines(rig));
    }

    // ======== 放弃的等待期间 ========

    [Fact]
    public async Task A_user_abort_during_the_give_up_wait_stops_at_once_without_a_second_command_and_keeps_the_exit_code()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice();
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);

        rig.Cts.Cancel();                                       // 不拨假时钟:取消必须自己把放弃的等待打断
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Cancelled, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.Sent);                             // 命令已经发出去了
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);              // 退出码仍是 11
        Assert.Equal(11, OnlineFightExitCodes.ForOutcome(s.Outcome));
        Assert.Equal(new[] { Step("sent", OnlineFightOutcome.NoClearNotice), Step("cancelled", OnlineFightOutcome.NoClearNotice) }, GiveUpLines(rig));
        Assert.Equal(OnlineFightLog.ResultFields(s) + " giveUp=cancelled", OnlineFightLog.ResultFieldsFor(ParseWithSwitch(), s, null, s.GiveUp));
    }

    [Fact]
    public async Task A_connection_that_dies_during_the_wait_reports_closed_and_does_not_change_the_outcome()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice();
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);

        rig.Session.Closed = true;
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Closed, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.Sent);
        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);              // 不是 SessionClosed:放弃之前的结局不被改写
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, s.RunnerOutcome);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(new[] { Step("sent", OnlineFightOutcome.NoClearNotice), Step("closed", OnlineFightOutcome.NoClearNotice) }, GiveUpLines(rig));
    }

    [Fact]
    public async Task A_give_up_command_that_cannot_be_encoded_is_reported_as_send_failed_and_is_not_retried()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice();
        // 前面三条击杀正常,第四次发送(放弃命令)编码失败
        rig.Session.StatusOfSend = index => index == 3 ? FrameStatus.EncryptFailed : FrameStatus.Ok;
        Task<OnlineFightSummary> run = await rig.Drive(OnlineFightOutcome.NoClearNotice);

        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.SendFailed, s.GiveUp.Outcome);
        Assert.False(s.GiveUp.Sent);
        Assert.Equal(1, rig.Session.Commands(45));              // 试了一次,没有再试
        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);              // 没发出去:与没开时一样按最终状态读,仍是原结局
        Assert.Equal(new[] { Step("sendfailed", OnlineFightOutcome.NoClearNotice) }, GiveUpLines(rig));
    }

    // ======== 只发一次:迟到帧、重复信号、心跳 ========

    [Fact]
    public async Task Late_frames_and_duplicate_signals_never_cause_a_second_give_up_or_another_command_and_leave_the_outcome_alone()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice();
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);

        // 放弃命令发出之后才到的:心跳、迟到的清图通知与两段结算、重复的回城信号、无关帧
        rig.Session.Push(CombatKit.Heartbeat());
        rig.Session.Push(CombatKit.Clear(), CombatKit.Result(), CombatKit.Reward());
        rig.Session.Push(CombatKit.Unrelated(902), GiveUpKit.Noti23(), GiveUpKit.Noti23(), CombatKit.Heartbeat(), GiveUpKit.Noti24(), GiveUpKit.Noti24(), GiveUpKit.Noti23());
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(0, rig.Session.Commands(49));              // 放弃期间迟到的清图通知不会引出结算命令
        Assert.Equal(3, rig.Session.Commands(42));
        Assert.Equal(2, rig.Session.Commands((int)OnlineHeartbeat.ReplyPacketId));      // 每个心跳回一次
        Assert.Equal(2, s.HeartbeatsAnswered);
        Assert.Equal(OnlineGiveUpOutcome.Returned, s.GiveUp.Outcome);
        // 迟到的清图通知状态机确实收下了(阶段已经往前走),但汇总不被改写:结局与计数就是放弃之前那一刻的
        Assert.Equal(OnlineCombatPhase.Cleared, rig.Machine.Phase);
        AssertAsAtTheFailure(OnlineFightOutcome.NoClearNotice, s, rig);
        Assert.Equal(11, OnlineFightExitCodes.ForOutcome(s.Outcome));
        Assert.Equal(new[] { Step("sent", OnlineFightOutcome.NoClearNotice), Step("returned", OnlineFightOutcome.NoClearNotice) }, GiveUpLines(rig));
    }

    [Fact]
    public async Task Heartbeats_during_the_wait_are_answered_with_the_product_reply_one_for_one()
    {
        GiveUpRig rig = GiveUpRig.ForRefusedStop();
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.RefusedStop);

        rig.Session.Push(CombatKit.Heartbeat(), CombatKit.Heartbeat(), CombatKit.Heartbeat());
        await CombatKit.WaitUntil(() => rig.Session.Commands((int)OnlineHeartbeat.ReplyPacketId) == 3, "三个心跳都回了");
        Assert.False(run.IsCompleted, "只有心跳不是回城信号");
        Assert.Equal(1, rig.Session.Commands(45));

        rig.Session.Push(GiveUpKit.Pair());
        OnlineFightSummary s = await rig.Done(run);

        (PacketClass Class, ushort Id, byte[] Payload)[] replies = rig.Session.Sent.Where(x => x.Class == PacketClass.Cmd && x.Id == OnlineHeartbeat.ReplyPacketId).ToArray();
        Assert.Equal(3, replies.Length);
        Assert.All(replies, r => Assert.Equal(new byte[8], r.Payload));
        Assert.Equal(3, s.HeartbeatsAnswered);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(OnlineGiveUpOutcome.Returned, s.GiveUp.Outcome);
    }

    [Fact]
    public async Task Only_one_of_the_two_return_signals_is_not_enough_on_the_runner_path()
    {
        GiveUpRig rig = GiveUpRig.ForSettleIncomplete(giveUpWait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.SettleIncomplete);

        rig.Session.Push(GiveUpKit.Noti23(), GiveUpKit.Noti23());       // 只有 0/23(还来了两次)
        await rig.WaitParked();
        rig.Clock.Advance(ShortWait);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.Equal(1, rig.Session.Commands(45));
    }

    // ======== 信封与压缩内层(跑器路径) ========

    [Theory]
    [MemberData(nameof(TriggerOutcomes))]
    public async Task The_pair_inside_561_envelopes_with_inner_copies_also_returns(OnlineFightOutcome outcome)
    {
        GiveUpRig rig = RigFor(outcome, unwrap: OnlineDungeonRunner.UnwrapEnvelope);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(outcome);

        GiveUpKit.PushEnveloped(GiveUpKit.Noti23(), rig.Session);
        GiveUpKit.PushEnveloped(GiveUpKit.Noti24(), rig.Session);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Returned, s.GiveUp.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, s.RunnerOutcome);
        Assert.Equal(1, rig.Session.Commands(45));
        AssertAsAtTheFailure(outcome, s, rig);
    }

    [Fact]
    public async Task A_lone_0_2_is_not_a_return_signal_on_the_runner_path_which_only_knows_the_0_23_and_0_24_pair()
    {
        // 已知局限(见 NOTES 与 HANDOFF 的风险):战斗跑器与状态机不在本线范围,它们只认 0/23 + 0/24;
        // 0/2 单帧只在会话层放弃路径(跑器已先收场时)里被认。如果将来状态机也认 0/2,这条要跟着改。
        GiveUpRig rig = GiveUpRig.ForNoClearNotice(giveUpWait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);

        rig.Session.Push(GiveUpKit.Noti2());
        await rig.WaitParked();
        rig.Clock.Advance(ShortWait);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.Sent);
        Assert.Equal(1, rig.Session.Commands(45));
    }

    // ======== 跑器先收场:交给会话层放弃(同一个 FinishAsync,同一条日志,同样只发一次) ========

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_runner_times_out_first_the_session_level_give_up_takes_over_for_a_missing_clear_notice(bool singleFrame)
    {
        var rig = new GiveUpRig(2, giveUp: true, withholdClear: true, runnerOverallSeconds: 1.5);
        rig.OnGiveUp = () => singleFrame ? new[] { GiveUpKit.Noti2() } : GiveUpKit.Pair();
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await CombatKit.WaitUntil(() => rig.Runner.Ended, "跑器的整段上限先到", 15000);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, s.RunnerOutcome);
        Assert.Null(s.GiveUp);                                  // 驱动级没有放弃(跑器已经收场了)
        Assert.Equal(0, rig.Session.Commands(45));

        var sessionDriver = new OnlineAfterSettleDriver(rig.Session);
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            OnlineGiveUpOnFailure.FinishAsync(true, sessionDriver, s, null, rig.Log.Add, CancellationToken.None), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal("NoClearNotice", g.Reason);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(45, rig.Session.Sent.Last().Id);
        Assert.Equal(new[] { Step("sent", OnlineFightOutcome.NoClearNotice), Step("returned", OnlineFightOutcome.NoClearNotice) }, GiveUpLines(rig));
        Assert.Equal(11, OnlineFightExitCodes.ForOutcome(s.Outcome));       // 退出码仍由战斗结局定
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_runner_times_out_first_the_session_level_give_up_takes_over_for_an_incomplete_settlement(bool singleFrame)
    {
        var rig = new GiveUpRig(1, giveUp: true, silentSettlement: true, runnerSettlementSeconds: 1.0);
        rig.OnGiveUp = () => singleFrame ? new[] { GiveUpKit.Noti2() } : GiveUpKit.Pair();
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(1);
        await rig.PumpUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await CombatKit.WaitUntil(() => rig.Runner.Ended, "跑器的结算上限先到", 15000);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, s.RunnerOutcome);
        Assert.Null(s.GiveUp);
        Assert.Equal(0, rig.Session.Commands(45));

        var sessionDriver = new OnlineAfterSettleDriver(rig.Session);
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            OnlineGiveUpOnFailure.FinishAsync(true, sessionDriver, s, null, rig.Log.Add, CancellationToken.None), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal("SettleIncomplete", g.Reason);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(new[] { Step("sent", OnlineFightOutcome.SettleIncomplete), Step("returned", OnlineFightOutcome.SettleIncomplete) }, GiveUpLines(rig));
    }

    [Fact]
    public async Task A_refusal_on_a_connection_that_is_already_dead_sends_nothing_and_reports_skipped()
    {
        var rig = new GiveUpRig(4, giveUp: true, table: CombatKit.Sequential(4).Where(id => id != CombatKit.BaseId + 1));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);                                 // 第 0 只确认、第 1 只被拒
        await rig.WaitAnswered(2);

        rig.Session.Closed = true;                              // 被拒之后连接又被掐断
        OnlineFightSummary s = await rig.Done(run);
        Assert.Equal(OnlineFightOutcome.RefusedStop, s.Outcome);
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, s.RunnerOutcome);
        Assert.Null(s.GiveUp);

        var sessionDriver = new OnlineAfterSettleDriver(rig.Session);
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            OnlineGiveUpOnFailure.FinishAsync(true, sessionDriver, s, null, rig.Log.Add, CancellationToken.None), 10);

        Assert.Equal(OnlineGiveUpOutcome.Skipped, g.Outcome);
        Assert.False(g.Sent);
        Assert.Equal(0, rig.Session.Commands(45));
        Assert.Equal(new[] { Step("skipped", OnlineFightOutcome.RefusedStop) }, GiveUpLines(rig));
    }

    [Fact]
    public async Task The_session_level_wrap_up_returns_a_driver_level_give_up_unchanged_and_never_sends_again()
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice(giveUpWait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);
        await rig.WaitParked();
        rig.Clock.Advance(ShortWait);
        OnlineFightSummary s = await rig.Done(run);
        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.Equal(1, rig.Session.Commands(45));

        // 会话还活着、跑器已让停:如果收尾编排再放弃一次就会有第二条 1/45
        var sessionDriver = new OnlineAfterSettleDriver(rig.Session);
        OnlineGiveUpSummary first = await OnlineGiveUpOnFailure.FinishAsync(true, sessionDriver, s, null, rig.Log.Add, CancellationToken.None);
        OnlineGiveUpSummary second = await OnlineGiveUpOnFailure.FinishAsync(true, sessionDriver, s, null, rig.Log.Add, CancellationToken.None);

        Assert.Same(s.GiveUp, first);
        Assert.Same(s.GiveUp, second);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(new[] { Step("sent", OnlineFightOutcome.NoClearNotice), Step("timeout", OnlineFightOutcome.NoClearNotice) }, GiveUpLines(rig));
    }

    // ======== 日志脱敏 ========

    [Theory]
    [MemberData(nameof(TriggerOutcomes))]
    public async Task Every_give_up_line_is_just_a_step_name_and_an_enum_name(OnlineFightOutcome outcome)
    {
        GiveUpRig rig = RigFor(outcome, wait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(outcome);
        await rig.WaitParked();
        rig.Clock.Advance(ShortWait);
        await rig.Done(run);

        var shape = new Regex("^DNF_NET stage=giveup step=(sent|returned|timeout|closed|sendfailed|skipped|cancelled|faulted) reason=(RefusedStop|NoClearNotice|SettleIncomplete)$");
        string[] lines = GiveUpLines(rig);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Matches(shape, l));
        // 探针的转行白名单放行这些行,转出去的是去掉 DNF_NET 前缀的原文
        Assert.All(lines, l => Assert.Equal(l.Substring("DNF_NET ".Length), OnlineFightLog.ProbeText(l)));
    }
}
