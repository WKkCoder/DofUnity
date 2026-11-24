using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapA2Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器"自动推进 1/72 → 1/73 → 1/74 与各种推进失败"的<b>整份日志序列</b>测试:
/// 每个场景把控制器写出的全部脱敏日志行收集起来,与手算的字面量序列逐行相等(不用 Contains、不只看行数),
/// 同时断言每一步的阶段、提示种类、发送序列、选定回调。覆盖:标准成功全程(三种选择)、无翻牌权(跳过 1/74)、
/// 1/72、1/73、1/74 各自回包超时、1/72 与 1/73 回包畸形、推进步骤发送失败(五种失败形态 × 三个步骤)、1/75 发送失败、等回城期间发送失败。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟,无线程、无墙钟)。时间推导:
/// 先空闲轮询 1000;开始结算在 1000 发 1/72;之后每次轮询前进 16 毫秒——1016 一次轮询里排空 72 回复、发 73、排空 73 回复、发 74,1032 读到 74 发送成功进入菜单(倒计时从 1032 起算 5000)。
/// </summary>
public sealed class OnlineSettlementGapA2LogSequenceTests
{
    // ----------------------------------------------------------------------------------------------
    // 标准成功全程
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SettlementChoice.Retry, (byte)0, ChooseRetryByPlayer,
        "DNF_ONLINE stage=settle event=retry status=reenter cycle=1", 0)]
    [InlineData(SettlementChoice.SelectDungeon, (byte)1, ChooseSelectDungeonByPlayer,
        "DNF_ONLINE stage=settle event=choose choice=SelectDungeon source=confirmed", 0)]
    [InlineData(SettlementChoice.ReturnToTown, (byte)2, ChooseReturnByPlayer,
        TownConfirmedByNoti2, 1)]
    public void A_standard_settlement_logs_each_step_poll_by_poll_and_ends_with_the_server_confirmed_choice(
        SettlementChoice choice, byte command, string chooseLine, string endLine, int returnTargets)
    {
        // 保护:一次完整的联网结算(72 → 73 → 74 → 菜单 → 玩家选择 → 1/75 执行确认 + 0/2 回城信号)写出的日志与行序:
        // 开始那一刻写"展示"和"发 72";1016 那次轮询写 72 收、73 发、73 收、74 发;1032 写 74 发送成功;选择时写选择行;
        // 确认那次轮询写收尾行(再来一次:重进一轮计数 1;选其他副本:确认来源的选择行;回城:确认回城并带落点来源与信号)。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1), payload => ConfirmWithNoti2(rig.Transport, payload));
        rig.PollAt(1000);

        rig.StartSettlement();
        Assert.Equal(new[] { Show, Sent72 }, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.Advancing, rig.Ctrl.CurrentNotice?.Kind);

        rig.Poll();   // 1016
        Assert.Equal(new[] { Show, Sent72, Ok72, Sent73, Ok73, Sent74 }, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.SendingSelectCard, rig.Ctrl.Phase);
        Assert.Equal((byte)0, rig.Ctrl.SelectedCardIndex);

        rig.Poll();   // 1032
        Assert.Equal(StandardPreamble, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.CurrentNotice);
        Assert.Equal(1032, rig.NowMs);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        Assert.Equal(new byte[] { 0, 0 }, rig.Transport.Sent[2].Payload);   // 1/74:模式 0、牌位 0

        Assert.True(rig.Ctrl.Choose(choice));
        Assert.Equal(Seq(StandardPreamble, chooseLine), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
        Assert.Equal(new[] { (byte)1, command }, rig.Transport.Sent[3].Payload);

        rig.Poll();
        Assert.Equal(Seq(StandardPreamble, chooseLine, endLine), AllLogs(rig));
        Assert.Equal(new[] { choice }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(returnTargets, rig.Targets.Count);
        Assert.Equal(0, rig.Disconnects);
    }

    // ----------------------------------------------------------------------------------------------
    // 无翻牌权:跳过 1/74
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData((short)0, (short)0, (short)0, (short)0)]
    [InlineData((short)0, (short)-1, (short)-1, (short)-1)]
    [InlineData((short)-1, (short)-1, (short)-1, (short)-1)]
    [InlineData((short)0, (short)-1, (short)0, (short)-1)]
    public void Without_any_card_right_1_74_is_skipped_and_the_skip_is_logged_in_place_of_the_card_right_ok(
        short s0, short s1, short s2, short s3)
    {
        // 保护:四个槽位里没有一个是 1(无翻牌权/无成员)时不发 1/74:读到 73 回复的那次轮询写跳过行(card_skip no_right),
        // 而不是 card_right ok,也没有 select_card 的任何行;菜单立刻就绪、提示"已跳过翻牌"、倒计时就地起算;之后玩家选回城、服务端确认,序列照常收尾。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(s0, s1, s2, s3), payload => ConfirmWithNoti2(rig.Transport, payload));
        rig.PollAt(1000);
        rig.StartSettlement();

        rig.Poll();   // 1016:读 72 回复发 73,读 73 回复(无权):跳过 74,直接就绪
        Assert.Equal(new[] { Show, Sent72, Ok72, Sent73, SkipNoRight }, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.CardSelectSkipped, rig.Ctrl.CurrentNotice?.Kind);
        Assert.True(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.SelectedCardIndex);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(new ushort[] { 72, 73 }, SentIds(rig));

        // 以后再轮询不会补发 74,日志也不再增加
        rig.Poll();
        rig.Poll();
        Assert.Equal(new[] { Show, Sent72, Ok72, Sent73, SkipNoRight }, AllLogs(rig));
        Assert.Equal(new ushort[] { 72, 73 }, SentIds(rig));
        Assert.Equal(4968, rig.Ctrl.RemainingCountdownMs);   // 1016 起算 5000;1048 时剩 5000-32

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();
        Assert.Equal(Seq(Show, Sent72, Ok72, Sent73, SkipNoRight, ChooseReturnByPlayer, TownConfirmedByNoti2), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(new ushort[] { 72, 73, 75 }, SentIds(rig));
    }

    [Theory]
    [InlineData((short)0, (short)0, (short)1, (short)-1, (byte)2)]
    [InlineData((short)0, (short)1, (short)1, (short)1, (byte)1)]
    [InlineData((short)-1, (short)-1, (short)-1, (short)1, (byte)3)]
    [InlineData((short)1, (short)0, (short)0, (short)0, (byte)0)]
    public void With_a_card_right_the_first_right_slot_is_picked_and_the_pick_is_logged(
        short s0, short s1, short s2, short s3, byte expectedIndex)
    {
        // 保护:槽位里有翻牌权(值为 1)时挑第一个有权的槽发 1/74(模式 0、牌位 = 槽位下标),日志是 card_right ok 之后 select_card sent、select_card ok,
        // 不写跳过行;提示在 74 发送成功后清空;已选牌位可读。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(s0, s1, s2, s3));
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.Equal(StandardPreamble, AllLogs(rig));
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Equal(expectedIndex, rig.Ctrl.SelectedCardIndex);
        Assert.Null(rig.Ctrl.CurrentNotice);
        Assert.Equal(new[] { (byte)0, expectedIndex }, rig.Transport.Sent[2].Payload);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
    }

    // ----------------------------------------------------------------------------------------------
    // 1/72、1/73、1/74 各自回包超时
    // ----------------------------------------------------------------------------------------------

    private static string[] PrefixBeforeTimeout(int step)
    {
        switch (step)
        {
            case 72: return new[] { Show, Sent72 };
            case 73: return new[] { Show, Sent72, Ok72, Sent73 };
            default: return new[] { Show, Sent72, Ok72, Sent73, Ok73, Sent74 };
        }
    }

    [Theory]
    [InlineData(72, 1300L, "DNF_ONLINE stage=settle event=fail reason=advance_72_timeout step=72")]
    [InlineData(73, 1316L, "DNF_ONLINE stage=settle event=fail reason=advance_73_timeout step=73")]
    [InlineData(74, 1316L, "DNF_ONLINE stage=settle event=fail reason=advance_74_timeout step=74")]
    public void An_advance_step_without_reply_logs_its_own_timeout_then_the_countdown_returns_to_town(
        int silentStep, long timeoutAtMs, string timeoutLine)
    {
        // 保护:1/72、1/73、1/74 任何一步等不到回复(超时 300 毫秒,到点即超时):写该步骤自己的超时行(令牌与步骤号都按步骤区分),
        // 降级为带"没有回复"提示的菜单、不重发该步命令、倒计时从降级那一刻起算 5000;倒计时到点自动选回城(来源 timeout),
        // 发 1/75 [1,2],服务端确认后回城——整条序列逐行相等。
        // 时间:72 无回复 → 起点 1000,超时 1300;73/74 无回复 → 起点 1016(读到 72 回复那次轮询),超时 1316。
        using var rig = new DirectRig();
        var neverCompletes = new TaskCompletionSource<FrameStatus>();
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72 && silentStep != 72)
            {
                rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == 73 && silentStep != 73)
            {
                rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            }
            else if (id == 74 && silentStep == 74)
            {
                return neverCompletes.Task;   // 1/74 的发送一直不完成
            }
            else if (id == 75)
            {
                ConfirmWithNoti2(rig.Transport, payload);
            }
            return Task.FromResult(FrameStatus.Ok);
        });
        rig.PollAt(1000);
        rig.StartSettlement();
        if (silentStep != 72)
        {
            rig.PollAt(1016);
        }
        Assert.Equal(PrefixBeforeTimeout(silentStep), AllLogs(rig));

        rig.PollAt(timeoutAtMs - 1);
        Assert.Equal(PrefixBeforeTimeout(silentStep), AllLogs(rig));   // 差 1 毫秒:还没超时,没有新行
        Assert.NotEqual(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);

        rig.PollAt(timeoutAtMs);
        Assert.Equal(Seq(PrefixBeforeTimeout(silentStep), timeoutLine), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        ushort[] expectedSent = silentStep == 72 ? new ushort[] { 72 } : (silentStep == 73 ? new ushort[] { 72, 73 } : new ushort[] { 72, 73, 74 });
        Assert.Equal(expectedSent, SentIds(rig));   // 不重发

        rig.PollAt(timeoutAtMs + 4999);
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(Seq(PrefixBeforeTimeout(silentStep), timeoutLine), AllLogs(rig));

        rig.PollAt(timeoutAtMs + 5000);   // 倒计时到点:自动选回城
        Assert.Equal(Seq(PrefixBeforeTimeout(silentStep), timeoutLine, ChooseReturnByCountdown), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        Assert.Equal(new byte[] { 1, 2 }, rig.Transport.Sent[^1].Payload);

        rig.PollAt(timeoutAtMs + 5016);
        Assert.Equal(Seq(PrefixBeforeTimeout(silentStep), timeoutLine, ChooseReturnByCountdown, TownConfirmedByNoti2), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(expectedSent.Length + 1, rig.Transport.Sent.Count);
    }

    // ----------------------------------------------------------------------------------------------
    // 1/72、1/73 回包畸形
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(72, new byte[] { 0 })]
    [InlineData(72, new byte[] { 2 })]
    [InlineData(72, new byte[0])]
    [InlineData(73, new byte[0])]
    [InlineData(73, new byte[] { 1, 1, 0, 1, 0 })]
    [InlineData(73, new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(73, new byte[] { 2, 1, 0, 1, 0, 1, 0, 1, 0 })]
    public void A_malformed_72_or_73_reply_logs_its_own_malformed_line_and_degrades_without_resending(int step, byte[] badReply)
    {
        // 保护:(1,72) 回复首字节不是 1(或为空)、(1,73) 回复首字节不是 1 或槽位读不满 4 个时,写该步骤自己的"回复畸形"行
        // (令牌与步骤号按步骤区分),降级为"没有回复"菜单,不发后续命令、不重发本步;之后玩家选"选其他副本"、服务端确认,序列收尾。
        using var rig = new DirectRig();
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72)
            {
                rig.Transport.Push(PacketClass.Cmd, 72, step == 72 ? badReply : new byte[] { 1 });
            }
            else if (id == 73)
            {
                rig.Transport.Push(PacketClass.Cmd, 73, badReply);
            }
            else if (id == 75)
            {
                ConfirmWithNoti2(rig.Transport, payload);
            }
            return Task.FromResult(FrameStatus.Ok);
        });
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.Poll();   // 1016:读到畸形回复

        string[] expected = step == 72
            ? new[] { Show, Sent72, "DNF_ONLINE stage=settle event=fail reason=advance_72_malformed step=72" }
            : new[] { Show, Sent72, Ok72, Sent73, "DNF_ONLINE stage=settle event=fail reason=advance_73_malformed step=73" };
        Assert.Equal(expected, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(step == 72 ? new ushort[] { 72 } : new ushort[] { 72, 73 }, SentIds(rig));

        rig.Poll();
        rig.Poll();
        Assert.Equal(expected, AllLogs(rig));   // 之后没有新行、没有新命令
        Assert.Equal(step == 72 ? 1 : 2, rig.Transport.Sent.Count);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        rig.Poll();
        Assert.Equal(Seq(expected, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=choose choice=SelectDungeon source=confirmed"), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.SelectDungeon }, rig.Completed);
    }

    // ----------------------------------------------------------------------------------------------
    // 推进步骤发送失败(五种失败形态 × 三个步骤)
    // ----------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> AdvanceSendFailureCases()
    {
        foreach (int step in new[] { 72, 73, 74 })
        {
            foreach (string kind in new[] { "status", "faulted", "canceled", "null", "throws" })
            {
                yield return new object[] { step, kind };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AdvanceSendFailureCases))]
    public void A_failed_advance_send_logs_the_phase_it_failed_in_and_degrades_without_resending(int step, string kind)
    {
        // 保护:推进命令(1/72、1/73、1/74)的发送失败——非 Ok 状态、任务出错、任务被取消、空任务、同步抛异常,五种形态一视同仁——
        // 写"推进发送失败"行并带当时所处的阶段名(AdvancingScoreScroll / AdvancingCardRight / SendingSelectCard),
        // 降级为"没有回复"菜单、不重发;之后玩家选回城、服务端确认收尾。整份日志逐行相等。
        using var rig = new DirectRig();
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72)
            {
                if (step == 72)
                {
                    return FailedSend(kind);
                }
                rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == 73)
            {
                if (step == 73)
                {
                    return FailedSend(kind);
                }
                rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            }
            else if (id == 74)
            {
                return FailedSend(kind);
            }
            else if (id == 75)
            {
                ConfirmWithNoti2(rig.Transport, payload);
            }
            return Task.FromResult(FrameStatus.Ok);
        });
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.Poll();   // 1016
        rig.Poll();   // 1032

        string[] expected;
        ushort[] expectedSent;
        if (step == 72)
        {
            expected = new[] { Show, Sent72, "DNF_ONLINE stage=settle event=fail reason=advance_send_failed step=AdvancingScoreScroll" };
            expectedSent = new ushort[] { 72 };
        }
        else if (step == 73)
        {
            expected = new[] { Show, Sent72, Ok72, Sent73, "DNF_ONLINE stage=settle event=fail reason=advance_send_failed step=AdvancingCardRight" };
            expectedSent = new ushort[] { 72, 73 };
        }
        else
        {
            expected = new[] { Show, Sent72, Ok72, Sent73, Ok73, Sent74, "DNF_ONLINE stage=settle event=fail reason=advance_send_failed step=SendingSelectCard" };
            expectedSent = new ushort[] { 72, 73, 74 };
        }
        Assert.Equal(expected, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(expectedSent, SentIds(rig));
        Assert.Equal(0, rig.Disconnects);
        Assert.Empty(rig.Completed);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();
        Assert.Equal(Seq(expected, ChooseReturnByPlayer, TownConfirmedByNoti2), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(expectedSent.Length + 1, rig.Transport.Sent.Count);
    }

    // ----------------------------------------------------------------------------------------------
    // 1/75 发送失败、等回城期间发送失败
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_send_failed step=Retry")]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_send_failed step=SelectOtherDungeon")]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_send_failed step=ReturnToTown")]
    public void A_failed_1_75_send_logs_the_command_it_failed_for_and_falls_back_to_town(
        SettlementChoice choice, string chooseLine, string failLine)
    {
        // 保护:1/75 发送失败:写"1/75 发送失败"行并带命令名(再来一次/选其他副本/返回城镇各自的名字),
        // 然后无条件安全回城(选定回调收到的是回城,不管玩家选的是什么),收尾行的来源为 eplp_send_failed、信号为 fallback、落点来源为进副本前城镇区;
        // 提示是"发送失败";1/75 只发过一次。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1), _ => { });
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        rig.Transport.SetSendHandler((id, _) => Task.FromResult(id == 75 ? FrameStatus.NoCipher : FrameStatus.Ok));

        Assert.True(rig.Ctrl.Choose(choice));
        rig.Poll();

        Assert.Equal(
            Seq(StandardPreamble, chooseLine, failLine, "DNF_ONLINE stage=settle event=town reason=eplp_send_failed area=pre_dungeon signal=fallback"),
            AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementNoticeKind.SendFailed, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
        OnlineSettlementReturnTarget target = Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
    }

    [Theory]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=return_send_failed step=Retry")]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=return_send_failed step=SelectDungeon")]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=return_send_failed step=ReturnToTown")]
    public void A_send_that_fails_after_the_server_already_confirmed_logs_the_pending_choice_and_falls_back_to_town(
        SettlementChoice choice, string chooseLine, string failLine)
    {
        // 保护:1/75 的执行确认先到(进入等回城)、而那条 1/75 的发送任务稍后才以失败收场:写"等回城期间发送失败"行并带待决选择名
        // (这里是玩家选的 Retry/SelectDungeon/ReturnToTown,不是命令名),然后安全回城,收尾行的来源为 return_send_failed、信号 fallback。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);

        var lateFailure = new TaskCompletionSource<FrameStatus>();
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 75)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));   // 执行确认已经到了
                return lateFailure.Task;                                        // 发送任务还没收场
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        Assert.True(rig.Ctrl.Choose(choice));
        rig.Poll();   // 发送任务未完成:读到执行确认,进入等回城
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(Seq(StandardPreamble, chooseLine), AllLogs(rig));
        Assert.Empty(rig.Completed);

        lateFailure.SetResult(FrameStatus.NoCipher);
        rig.Poll();
        Assert.Equal(
            Seq(StandardPreamble, chooseLine, failLine, "DNF_ONLINE stage=settle event=town reason=return_send_failed area=pre_dungeon signal=fallback"),
            AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
    }
}
