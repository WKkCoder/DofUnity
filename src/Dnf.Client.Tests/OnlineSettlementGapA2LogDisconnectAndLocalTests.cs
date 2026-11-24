using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapA2Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器"断线、菜单倒计时自动回城、没有传输(本地结算)、状态拨动后备防线"的<b>整份日志序列</b>测试(逐行相等的手算字面量)。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟)。时间线与 <see cref="OnlineSettlementGapA2Kit.ReachMenu"/> 一致:
/// 空闲轮询 1000、开始结算 1000;1016 排空 72/73 回复并发 74;1032 读到 74 发送成功进入菜单(倒计时到点 6032,默认倒计时 5000、各回复超时 300、回城等待 300)。
/// </summary>
public sealed class OnlineSettlementGapA2LogDisconnectAndLocalTests
{
    private const string DisconnectedAtMenuReady = "DNF_ONLINE stage=settle event=fail reason=disconnected step=MenuReady";
    private const string RejectedRetryNotLeader = "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=Retry code=8";
    private const string Retry1 = "DNF_ONLINE stage=settle event=retry status=reenter cycle=1";

    private static void SetPrivate(OnlineSettlementController ctrl, string name, object value)
    {
        FieldInfo field = typeof(OnlineSettlementController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(ctrl, value);
    }

    // ----------------------------------------------------------------------------------------------
    // 断线:七个阶段 × 三种信号形态
    // ----------------------------------------------------------------------------------------------

    /// <summary>把场景台摆到某阶段,返回断线之前应有的整份日志与已发出的包号序列。</summary>
    private static (string[] Logs, ushort[] Sent) Arrange(DirectRig rig, string scenario)
    {
        switch (scenario)
        {
            case "AdvancingScoreScroll":
                rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));   // 服务端静默
                rig.PollAt(1000);
                rig.StartSettlement();
                rig.Poll();   // 1016:72 已发出、无回复、未到 300
                return (new[] { Show, Sent72 }, new ushort[] { 72 });
            case "AdvancingCardRight":
                AutoAdvance(rig.Transport, null);   // 72 有回复,73 静默
                rig.PollAt(1000);
                rig.StartSettlement();
                rig.Poll();   // 1016:排空 72 回复,发出 73
                return (new[] { Show, Sent72, Ok72, Sent73 }, new ushort[] { 72, 73 });
            case "SendingSelectCard":
                AutoAdvance(rig.Transport, CardRightBytes(1));
                rig.PollAt(1000);
                rig.StartSettlement();
                rig.Poll();   // 1016:排空 72、73 回复,发出 74;74 的发送结果要到下一次轮询才读
                return (new[] { Show, Sent72, Ok72, Sent73, Ok73, Sent74 }, new ushort[] { 72, 73, 74 });
            case "MenuReady":
                ReachMenu(rig);
                return (StandardPreamble, new ushort[] { 72, 73, 74 });
            case "MenuDegraded":
                rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
                rig.PollAt(1000);
                rig.StartSettlement();
                rig.Poll();           // 1016
                rig.PollAt(1300);     // 1300 - 1000 = 300 到点:72 无回复,降级菜单
                return (new[] { Show, Sent72, "DNF_ONLINE stage=settle event=fail reason=advance_72_timeout step=72" }, new ushort[] { 72 });
            case "WaitingEplpReply":
                ReachMenu(rig);   // 1/75 无应答
                Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
                return (Seq(StandardPreamble, ChooseRetryByPlayer), new ushort[] { 72, 73, 74, 75 });
            default:   // "WaitingServerReturn":1/75 执行确认已到、回城信号未到
                ReachMenu(rig, payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1])));
                Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
                rig.Poll();   // 1048
                return (Seq(StandardPreamble, ChooseRetryByPlayer), new ushort[] { 72, 73, 74, 75 });
        }
    }

    public static IEnumerable<object[]> DisconnectCases()
    {
        foreach (string signal in new[] { "both", "event_only", "flag_only" })
        {
            yield return new object[] { "AdvancingScoreScroll", OnlineSettlementPhase.AdvancingScoreScroll, "DNF_ONLINE stage=settle event=fail reason=disconnected step=AdvancingScoreScroll", signal };
            yield return new object[] { "AdvancingCardRight", OnlineSettlementPhase.AdvancingCardRight, "DNF_ONLINE stage=settle event=fail reason=disconnected step=AdvancingCardRight", signal };
            yield return new object[] { "SendingSelectCard", OnlineSettlementPhase.SendingSelectCard, "DNF_ONLINE stage=settle event=fail reason=disconnected step=SendingSelectCard", signal };
            yield return new object[] { "MenuReady", OnlineSettlementPhase.MenuReady, DisconnectedAtMenuReady, signal };
            yield return new object[] { "MenuDegraded", OnlineSettlementPhase.MenuDegraded, "DNF_ONLINE stage=settle event=fail reason=disconnected step=MenuDegraded", signal };
            yield return new object[] { "WaitingEplpReply", OnlineSettlementPhase.WaitingEplpReply, "DNF_ONLINE stage=settle event=fail reason=disconnected step=WaitingEplpReply", signal };
            yield return new object[] { "WaitingServerReturn", OnlineSettlementPhase.WaitingServerReturn, "DNF_ONLINE stage=settle event=fail reason=disconnected step=WaitingServerReturn", signal };
        }
    }

    [Theory]
    [MemberData(nameof(DisconnectCases))]
    public void A_disconnect_in_any_phase_logs_that_phase_and_cleans_up_exactly_once(
        string scenario, OnlineSettlementPhase phase, string expectedLine, string signal)
    {
        // 保护:断线(传输的断线事件、"已断线"标志、或两者同时)在结算的任一阶段被识别时——
        // 恰好写一行"断线"日志、带断线时所处的阶段名;控制器收尾回空闲(退订传输、清空结算摘要与待决选择、不交还选择、不应用回城目标)、
        // 撤掉流程的"拦着不退出副本"标志、通知宿主恰好一次;之后再轮询不重复写日志也不重复通知;
        // 断线之后能照常开下一次结算(重新发 72)。信号要等下一次轮询的第一步才处理。
        using var rig = new DirectRig();
        (string[] before, ushort[] sent) = Arrange(rig, scenario);
        Assert.Equal(phase, rig.Ctrl.Phase);
        Assert.Equal(before, AllLogs(rig));
        Assert.Equal(sent, SentIds(rig));
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);

        switch (signal)
        {
            case "both":
                rig.Transport.TriggerDisconnect();
                break;
            case "event_only":
                rig.Transport.RaiseDisconnectedEventOnly();
                break;
            default:
                rig.Transport.IsDisconnected = true;
                break;
        }
        Assert.Equal(phase, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Disconnects);
        Assert.Equal(before, AllLogs(rig));

        rig.Poll();
        Assert.Equal(Seq(before, expectedLine), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(1, rig.Disconnects);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Targets);
        Assert.Equal(sent, SentIds(rig));
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Null(rig.Ctrl.Summary);
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.Equal(0, rig.Ctrl.RetryCycleCount);

        rig.Poll();
        rig.Poll();
        Assert.Equal(Seq(before, expectedLine), AllLogs(rig));
        Assert.Equal(1, rig.Disconnects);

        // 下一次结算从干净状态开始:重新展示并重新发 72
        rig.Transport.IsDisconnected = false;
        rig.StartSettlement();
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(Seq(before, expectedLine, Show, Sent72), AllLogs(rig));
        Assert.Equal(sent.Append((ushort)72).ToArray(), SentIds(rig));
    }

    [Fact]
    public void A_transport_that_is_already_disconnected_when_the_settlement_starts_is_reported_at_the_idle_step_and_the_next_settlement_notifies_again()
    {
        // 保护:结算一开始传输就已断线——展示行之后立刻写"断线、所处阶段 Idle"(还没发过任何命令),通知宿主一次、不留订阅、不交还选择;
        // 传输恢复后的下一次结算照常走完推进,并且这一次断线仍然会再通知宿主一次(通知标志按每次结算重置)。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.Transport.IsDisconnected = true;
        rig.PollAt(1000);
        rig.StartSettlement();

        const string disconnectedAtIdle = "DNF_ONLINE stage=settle event=fail reason=disconnected step=Idle";
        Assert.Equal(new[] { Show, disconnectedAtIdle }, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(1, rig.Disconnects);
        Assert.Empty(SentIds(rig));
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Empty(rig.Completed);

        rig.Transport.IsDisconnected = false;
        rig.PollAt(2000);
        rig.StartSettlement();
        rig.PollAt(2016);
        rig.PollAt(2032);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Equal(Seq(Show, disconnectedAtIdle, StandardPreamble), AllLogs(rig));
        Assert.Equal(1, rig.Disconnects);

        rig.Transport.TriggerDisconnect();
        rig.PollAt(2048);
        Assert.Equal(Seq(Show, disconnectedAtIdle, StandardPreamble, DisconnectedAtMenuReady), AllLogs(rig));
        Assert.Equal(2, rig.Disconnects);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
    }

    [Fact]
    public void A_disconnect_in_a_timed_out_settlement_menu_is_logged_with_the_phase_before_and_after_the_give_up()
    {
        // 保护:"结算通知超时"的菜单(降级菜单,没有发过任何 1/72..1/75)里断线——写"断线、阶段 MenuDegraded"、不发放弃命令;
        // 另一次结算里玩家先选了回城(发出一次 1/45 放弃、进入等回城阶段)再断线——写"断线、阶段 WaitingServerReturn"。
        using (var rig = new DirectRig())
        {
            rig.PollAt(1000);
            rig.StartSettlement(timedOut: true);
            rig.Poll();
            Assert.Equal(new[] { ShowTimedOut, TimeoutNotice }, AllLogs(rig));
            rig.Transport.TriggerDisconnect();
            rig.Poll();
            Assert.Equal(
                new[] { ShowTimedOut, TimeoutNotice, "DNF_ONLINE stage=settle event=fail reason=disconnected step=MenuDegraded" },
                AllLogs(rig));
            Assert.Empty(SentIds(rig));
            Assert.Equal(1, rig.Disconnects);
            Assert.Empty(rig.Completed);
        }

        using (var rig = new DirectRig())
        {
            rig.PollAt(1000);
            rig.StartSettlement(timedOut: true);
            Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
            rig.Poll();
            Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
            Assert.Equal(new ushort[] { 45 }, SentIds(rig));
            Assert.Empty(rig.Transport.Sent[0].Payload);

            rig.Transport.RaiseDisconnectedEventOnly();
            rig.Poll();
            Assert.Equal(
                new[]
                {
                    ShowTimedOut,
                    TimeoutNotice,
                    ChooseReturnByPlayer,
                    "DNF_ONLINE stage=settle event=fail reason=disconnected step=WaitingServerReturn",
                },
                AllLogs(rig));
            Assert.Equal(1, rig.Disconnects);
            Assert.Empty(rig.Completed);
            Assert.Empty(rig.Targets);
        }
    }

    [Fact]
    public void A_disconnect_resets_the_retry_cycle_count_so_the_next_retry_counts_from_one_again()
    {
        // 保护:"再来一次"的周期计数在断线时清零。三次结算:第一次再来一次(第 1 次);第二次进菜单后断线;
        // 第三次再来一次——计数从 1 重新数起,而不是接着数成 2。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1), payload => ConfirmWithNoti2(rig.Transport, payload));

        // 第一次
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollAt(1016);
        rig.PollAt(1032);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.PollAt(1048);
        Assert.Equal(1, rig.Ctrl.RetryCycleCount);
        Assert.Equal(new[] { SettlementChoice.Retry }, rig.Completed);

        // 第二次:进菜单后断线
        rig.PollAt(2000);
        rig.StartSettlement();
        rig.PollAt(2016);
        rig.PollAt(2032);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Equal(1, rig.Ctrl.RetryCycleCount);   // 还没断线,计数仍在
        rig.Transport.TriggerDisconnect();
        rig.PollAt(2048);
        Assert.Equal(0, rig.Ctrl.RetryCycleCount);
        Assert.Equal(1, rig.Disconnects);

        // 第三次:从 1 重新数
        rig.Transport.IsDisconnected = false;
        rig.PollAt(3000);
        rig.StartSettlement();
        rig.PollAt(3016);
        rig.PollAt(3032);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.PollAt(3048);

        Assert.Equal(
            Seq(
                StandardPreamble, ChooseRetryByPlayer, Retry1,
                StandardPreamble, DisconnectedAtMenuReady,
                StandardPreamble, ChooseRetryByPlayer, Retry1),
            AllLogs(rig));
        Assert.Equal(1, rig.Ctrl.RetryCycleCount);
        Assert.Equal(new[] { SettlementChoice.Retry, SettlementChoice.Retry }, rig.Completed);
    }

    // ----------------------------------------------------------------------------------------------
    // 菜单倒计时到点自动回城:五种服务端反应 + 被拒后倒计时不重置
    // ----------------------------------------------------------------------------------------------

    /// <summary>倒计时到点前一毫秒什么都不发生;到点那一刻自动选回城并发出 1/75 [1, 2]。</summary>
    private static void ExpireCountdown(DirectRig rig)
    {
        rig.PollAt(6031);
        Assert.Equal(StandardPreamble, AllLogs(rig));
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);

        rig.PollAt(6032);
        Assert.Equal(Seq(StandardPreamble, ChooseReturnByCountdown), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        Assert.Equal(SettlementChoice.ReturnToTown, rig.Ctrl.PendingChoice);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
        Assert.Equal(new byte[] { 1, 2 }, rig.Transport.Sent[^1].Payload);
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
    }

    private static void AssertReturnedToPreDungeonTown(DirectRig rig)
    {
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        OnlineSettlementReturnTarget target = Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
        Assert.Equal(0, rig.Ctrl.RetryCycleCount);
    }

    [Fact]
    public void Countdown_expiry_chooses_return_to_town_and_a_confirming_server_completes_it()
    {
        // 保护:菜单倒计时到点(开始 1032 + 5000):前一毫秒什么都没发生;到点自动选回城(来源 timeout)并发 1/75 [1, 2];
        // 服务端执行确认 + 0/2 后回城完成(原因 confirmed、信号 noti2)。
        using var rig = new DirectRig();
        ReachMenu(rig, payload => ConfirmWithNoti2(rig.Transport, payload));
        ExpireCountdown(rig);
        rig.PollAt(6048);
        Assert.Equal(Seq(StandardPreamble, ChooseReturnByCountdown, TownConfirmedByNoti2), AllLogs(rig));
        AssertReturnedToPreDungeonTown(rig);
    }

    [Fact]
    public void Countdown_expiry_with_a_silent_server_times_out_the_1_75_and_falls_back_to_town()
    {
        // 保护:倒计时自动选回城后服务端对 1/75 不回:到点 + 300(6332)才超时——前一毫秒无事;
        // 写"1/75 超时、命令 ReturnToTown"与回城行(原因 eplp_timeout、信号 timeout_fallback)。
        using var rig = new DirectRig();
        ReachMenu(rig);
        ExpireCountdown(rig);
        rig.PollAt(6331);
        Assert.Equal(Seq(StandardPreamble, ChooseReturnByCountdown), AllLogs(rig));
        rig.PollAt(6332);
        Assert.Equal(
            Seq(
                StandardPreamble,
                ChooseReturnByCountdown,
                "DNF_ONLINE stage=settle event=fail reason=eplp_timeout step=ReturnToTown",
                "DNF_ONLINE stage=settle event=town reason=eplp_timeout area=pre_dungeon signal=timeout_fallback"),
            AllLogs(rig));
        AssertReturnedToPreDungeonTown(rig);
        Assert.Equal(OnlineSettlementNoticeKind.EplpTimeout, rig.Ctrl.CurrentNotice?.Kind);
    }

    [Fact]
    public void Countdown_expiry_with_a_rejecting_server_falls_back_to_town_directly()
    {
        // 保护:倒计时自动选的回城被服务端拒绝(错误码 8):写"被拒、命令 ReturnToTown、错误码 8"与回城行(原因 eplp_rejected_fallback、信号 fallback)。
        using var rig = new DirectRig();
        ReachMenu(rig, payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(8, payload[1])));
        ExpireCountdown(rig);
        rig.PollAt(6048);
        Assert.Equal(
            Seq(
                StandardPreamble,
                ChooseReturnByCountdown,
                "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=ReturnToTown code=8",
                "DNF_ONLINE stage=settle event=town reason=eplp_rejected_fallback area=pre_dungeon signal=fallback"),
            AllLogs(rig));
        AssertReturnedToPreDungeonTown(rig);
        Assert.Equal(OnlineSettlementNoticeKind.EplpRejectedNotLeader, rig.Ctrl.CurrentNotice?.Kind);
    }

    [Fact]
    public void Countdown_expiry_with_a_confirmation_but_no_return_signal_times_out_waiting_for_the_return()
    {
        // 保护:倒计时自动选的回城拿到执行确认但迟迟没有回城信号:进入等回城阶段(回城等待 300,从读到确认的 6048 起算),
        // 6347 无事,6348 超时:写"等回城超时、命令 ReturnToTown"与回城行(原因 return_timeout、信号 timeout_fallback)。
        using var rig = new DirectRig();
        ReachMenu(rig, payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1])));
        ExpireCountdown(rig);
        rig.PollAt(6048);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(Seq(StandardPreamble, ChooseReturnByCountdown), AllLogs(rig));
        rig.PollAt(6347);
        Assert.Equal(Seq(StandardPreamble, ChooseReturnByCountdown), AllLogs(rig));
        rig.PollAt(6348);
        Assert.Equal(
            Seq(
                StandardPreamble,
                ChooseReturnByCountdown,
                "DNF_ONLINE stage=settle event=fail reason=return_wait_timeout step=ReturnToTown",
                "DNF_ONLINE stage=settle event=town reason=return_timeout area=pre_dungeon signal=timeout_fallback"),
            AllLogs(rig));
        AssertReturnedToPreDungeonTown(rig);
    }

    [Fact]
    public void Countdown_expiry_whose_1_75_cannot_be_sent_falls_back_to_town_with_the_send_failure_logged()
    {
        // 保护:倒计时自动选回城、但 1/75 发送失败(状态非成功):写"1/75 发送失败、命令 ReturnToTown"与回城行(原因 eplp_send_failed、信号 fallback)。
        using var rig = new DirectRig();
        ReachMenu(rig);
        rig.Transport.SetSendHandler((_, _) => FailedSend("status"));
        ExpireCountdown(rig);
        rig.PollAt(6048);
        Assert.Equal(
            Seq(
                StandardPreamble,
                ChooseReturnByCountdown,
                "DNF_ONLINE stage=settle event=fail reason=eplp_send_failed step=ReturnToTown",
                "DNF_ONLINE stage=settle event=town reason=eplp_send_failed area=pre_dungeon signal=fallback"),
            AllLogs(rig));
        AssertReturnedToPreDungeonTown(rig);
        Assert.Equal(OnlineSettlementNoticeKind.SendFailed, rig.Ctrl.CurrentNotice?.Kind);
    }

    [Fact]
    public void A_rejected_choice_does_not_restart_the_running_countdown()
    {
        // 保护:被拒的"再次挑战"把菜单退回降级菜单时,正在跑的倒计时保持原来的到点时刻(6032),不因被拒而重新起算:
        // 6031 仍无事,6032 自动选回城(来源 timeout),服务端确认后回城完成。
        using var rig = new DirectRig();
        int eplpCalls = 0;
        ReachMenu(rig, payload =>
        {
            eplpCalls++;
            if (eplpCalls == 1)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(8, payload[1]));
            }
            else
            {
                ConfirmWithNoti2(rig.Transport, payload);
            }
        });
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.PollAt(1048);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(4984, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(Seq(StandardPreamble, ChooseRetryByPlayer, RejectedRetryNotLeader), AllLogs(rig));

        rig.PollAt(6031);
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(Seq(StandardPreamble, ChooseRetryByPlayer, RejectedRetryNotLeader), AllLogs(rig));
        rig.PollAt(6032);
        Assert.Equal(Seq(StandardPreamble, ChooseRetryByPlayer, RejectedRetryNotLeader, ChooseReturnByCountdown), AllLogs(rig));
        rig.PollAt(6048);
        Assert.Equal(
            Seq(StandardPreamble, ChooseRetryByPlayer, RejectedRetryNotLeader, ChooseReturnByCountdown, TownConfirmedByNoti2),
            AllLogs(rig));
        Assert.Equal(new ushort[] { 72, 73, 74, 75, 75 }, SentIds(rig));
        AssertReturnedToPreDungeonTown(rig);
    }

    // ----------------------------------------------------------------------------------------------
    // 没有传输(本地结算):菜单直接就绪,选择与倒计时都本地收尾
    // ----------------------------------------------------------------------------------------------

    private static DirectRig NewLocalRig() => new DirectRig(o => o.TransportFactory = _ => null);

    [Theory]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, Retry1, 0)]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, ChooseSelectDungeonByPlayer, 0)]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=town reason=player area=pre_dungeon signal=local", 1)]
    public void Without_a_transport_the_menu_is_ready_at_once_and_each_choice_completes_locally(
        SettlementChoice choice, string chooseLine, string endLine, int appliedTargets)
    {
        // 保护:没有联网传输(工厂返回空)时不发任何命令、不订阅任何东西,开始结算后菜单直接就绪(无提示、倒计时满额 5000);
        // 玩家的选择在本地收尾:再来一次写"再来一次、周期 1";选其他副本写两行同样的选择(第二行来自收尾,来源仍是 player);
        // 回城写回城行(原因 player、进副本前的城镇、信号 local)并应用回城目标。快速组队永远不可选。
        using var rig = NewLocalRig();
        rig.PollAt(1000);
        rig.StartSettlement();
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.CurrentNotice);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Equal(new[] { Show }, AllLogs(rig));

        Assert.False(rig.Ctrl.Choose(SettlementChoice.QuickParty));
        Assert.Equal(new[] { Show }, AllLogs(rig));

        Assert.True(rig.Ctrl.Choose(choice));
        Assert.Equal(new[] { Show, chooseLine, endLine }, AllLogs(rig));
        Assert.Equal(new[] { choice }, rig.Completed);
        Assert.Equal(appliedTargets, rig.Targets.Count);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Empty(SentIds(rig));
        rig.Poll();
        Assert.Equal(new[] { Show, chooseLine, endLine }, AllLogs(rig));
    }

    [Fact]
    public void Without_a_transport_the_countdown_expiry_returns_to_town_locally_at_the_exact_deadline()
    {
        // 保护:没有传输时倒计时从开始结算那一刻(1000)起算,到点 6000:前一毫秒无事、剩余 1;
        // 到点写"选回城(来源 timeout)"与回城行(原因 timeout、进副本前的城镇、信号 local)。
        using var rig = NewLocalRig();
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollAt(5999);
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(new[] { Show }, AllLogs(rig));
        rig.PollAt(6000);
        Assert.Equal(
            new[]
            {
                Show,
                ChooseReturnByCountdown,
                "DNF_ONLINE stage=settle event=town reason=timeout area=pre_dungeon signal=local",
            },
            AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
    }

    [Fact]
    public void A_settlement_started_before_any_poll_starts_its_countdown_and_its_timers_at_the_first_poll()
    {
        // 保护:结算在控制器还没被轮询过时开始——倒计时(没有传输)先"待启动",剩余显示满额;
        // 第一次轮询(逻辑时钟 2000)才起算,到点 7000;有传输时第一次轮询(5000)才把回复超时的起点对齐,300 毫秒后(5300)才超时。
        using (var rig = NewLocalRig())
        {
            rig.StartSettlement();
            Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
            Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
            rig.PollAt(2000);
            Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
            rig.PollAt(6999);
            Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
            Assert.Equal(new[] { Show }, AllLogs(rig));
            rig.PollAt(7000);
            Assert.Equal(
                new[]
                {
                    Show,
                    ChooseReturnByCountdown,
                    "DNF_ONLINE stage=settle event=town reason=timeout area=pre_dungeon signal=local",
                },
                AllLogs(rig));
        }

        using (var rig = new DirectRig())
        {
            rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
            rig.StartSettlement();
            Assert.Equal(new[] { Show, Sent72 }, AllLogs(rig));
            rig.PollAt(5000);
            rig.PollAt(5299);
            Assert.Equal(new[] { Show, Sent72 }, AllLogs(rig));
            rig.PollAt(5300);
            Assert.Equal(
                new[] { Show, Sent72, "DNF_ONLINE stage=settle event=fail reason=advance_72_timeout step=72" },
                AllLogs(rig));
            Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
            Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        }
    }

    [Fact]
    public void Without_a_transport_a_timed_out_settlement_only_offers_return_to_town_and_sends_nothing()
    {
        // 保护:没有传输、又是"结算通知超时"的数据——展示行与超时失败行之后菜单是降级菜单;只开放回城;
        // 回城在本地收尾(原因 player、信号 local),没有传输所以不发放弃命令;再来一次/选其他副本不可选且不写日志。
        using var rig = NewLocalRig();
        rig.PollAt(1000);
        rig.StartSettlement(timedOut: true);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.SettlementTimeout, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(new[] { ShowTimedOut, TimeoutNotice }, AllLogs(rig));
        Assert.False(rig.Ctrl.Choose(SettlementChoice.Retry));
        Assert.False(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        Assert.Equal(new[] { ShowTimedOut, TimeoutNotice }, AllLogs(rig));

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(
            new[]
            {
                ShowTimedOut,
                TimeoutNotice,
                ChooseReturnByPlayer,
                "DNF_ONLINE stage=settle event=town reason=player area=pre_dungeon signal=local",
            },
            AllLogs(rig));
        Assert.Empty(SentIds(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
    }

    // ----------------------------------------------------------------------------------------------
    // 公开操作碰不到的后备防线(反射拨动私有状态)
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void When_the_return_choice_cannot_be_made_the_countdown_falls_back_to_town_without_a_choose_line_or_a_1_75()
    {
        // 保护:倒计时到点时"自动选回城"若被拒(回城项已不可选——公开操作碰不到:回城被 1/75 拒绝时控制器直接回城并整体复位,
        // 所以先用反射把"被拒记录"里的回城位置 1),控制器不能卡在菜单:直接收尾回城,原因 countdown_fallback、信号 timeout_fallback;
        // 不写"选择"行、不发 1/75,选定回调给回城并应用回城目标。到点之前(6031)什么都不发生。
        using var rig = new DirectRig();
        ReachMenu(rig);
        SetPrivate(rig.Ctrl, "_rejectedChoicesMask", 1 << (int)SettlementChoice.ReturnToTown);   // 1 << 4
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));

        rig.PollAt(6031);
        Assert.Equal(StandardPreamble, AllLogs(rig));
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);

        rig.PollAt(6032);
        Assert.Equal(
            Seq(StandardPreamble, "DNF_ONLINE stage=settle event=town reason=countdown_fallback area=pre_dungeon signal=timeout_fallback"),
            AllLogs(rig));
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        AssertReturnedToPreDungeonTown(rig);
    }

    [Fact]
    public void A_return_to_town_choice_whose_1_75_was_already_sent_completes_as_a_fallback_instead_of_sending_it_again()
    {
        // 保护:同一条 1/75 命令绝不自动重发,但回城是例外——回城命令(2)已发过(公开操作碰不到:发过之后控制器要么等回复、要么已收尾,
        // 所以先用反射把"已发命令"掩码的回城位置 1)时再选回城,不再发 1/75,直接回城收尾:
        // 写"选择"行(来源 player)与回城行(原因 player、信号 fallback);选定回调给回城。
        using var rig = new DirectRig();
        ReachMenu(rig);
        SetPrivate(rig.Ctrl, "_sent75CommandMask", 1 << 2);   // 回城命令的值为 2

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(
            Seq(StandardPreamble, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=town reason=player area=pre_dungeon signal=fallback"),
            AllLogs(rig));
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        AssertReturnedToPreDungeonTown(rig);
    }
}
