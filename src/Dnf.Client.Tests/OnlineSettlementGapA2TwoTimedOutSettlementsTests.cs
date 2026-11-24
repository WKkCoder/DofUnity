using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// "同一个结算控制器连续做几次结算通知超时的结算"的测试:
/// 结算通知超时那一档(数据里 <c>IsTimedOut == true</c>)只开放返回城镇;选择回城(玩家点击、或菜单倒计时到点)时,
/// 要发<b>一条且只发一条</b>放弃游戏命令 1/45,然后等服务端回城信号(或等回城超时兜底)。
/// 每次结算结束控制器都复位,"已发过放弃命令"的标志必须回到"没发过"——否则第二次起永远不发 1/45、直接兜底回城。
/// 每一次结算都断言:发送序列、按序的<b>整份</b>脱敏日志(逐行相等的手算字面量)、选定回调、落点、订阅数、阶段。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟,无线程、无墙钟),期望值是手算的字面量。
/// </summary>
public sealed class OnlineSettlementGapA2TwoTimedOutSettlementsTests
{
    // 结算开始时的两行日志(结算通知超时的数据:解析状态都记"不适用"、没有掉落、没有差异字段;随后写一行"结算通知超时")
    private const string Show = "DNF_ONLINE stage=settle event=show timedout=true result=NotApplicable reward=NotApplicable drops=0 diffs=0";
    private const string TimeoutNotice = "DNF_ONLINE stage=settle event=fail reason=settlement_timeout step=notices";

    private static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    /// <summary>结算通知超时的结算刚开始:退化菜单、只开放回城、倒计时满额、还没发任何命令。</summary>
    private static void AssertTimedOutMenuJustOpened(DirectRig rig)
    {
        Assert.True(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.SettlementTimeout, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.QuickParty));
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
        Assert.Empty(rig.Transport.Sent);
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);
    }

    private static void AssertIdleAfterSettlement(DirectRig rig, int completedCount)
    {
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(completedCount, rig.Completed.Count);
        Assert.All(rig.Completed, c => Assert.Equal(SettlementChoice.ReturnToTown, c));
        Assert.Equal(completedCount, rig.Targets.Count);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Equal(0, rig.Disconnects);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
    }

    // ----------------------------------------------------------------------------------------------
    // 四种结束方式(每种各自断言发送序列与本次结算新增的日志;累计日志由调用者逐次比较)
    // ----------------------------------------------------------------------------------------------

    /// <summary>玩家选回城,服务端用 0/2 确认:发一条 1/45,日志 choose(player) + town(confirmed, noti2)。</summary>
    private static void EndByPlayerConfirmedByNoti2(DirectRig rig, long startAtMs, List<string> expectedLogs)
    {
        rig.Transport.ClearSent();
        rig.Transport.SetSendHandler((id, _) =>
        {
            if (id == 45)
            {
                PushNoti2(rig.Transport);
            }
            return Task.FromResult(FrameStatus.Ok);
        });
        rig.PollAt(startAtMs);
        rig.StartSettlement(timedOut: true);
        AssertTimedOutMenuJustOpened(rig);
        int before = rig.Completed.Count;

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.WaitingReturn, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));
        Assert.Empty(rig.Transport.Sent[0].Payload);
        Assert.False(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));   // 有待决选择:不可再选,不会多发
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));

        rig.Poll();
        Assert.Equal(before + 1, rig.Completed.Count);
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));
        expectedLogs.Add(Show);
        expectedLogs.Add(TimeoutNotice);
        expectedLogs.Add("DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=player");
        expectedLogs.Add("DNF_ONLINE stage=settle event=town reason=confirmed area=pre_dungeon signal=noti2");
        Assert.Equal(expectedLogs.ToArray(), rig.Logs.ToArray());
        AssertIdleAfterSettlement(rig, before + 1);
    }

    /// <summary>
    /// 菜单倒计时到点自动回城,服务端始终不确认:发一条 1/45,日志 choose(timeout) + fail(return_wait_timeout) + town(return_timeout, timeout_fallback)。
    /// 逻辑时钟:开始于 <paramref name="startAtMs"/>,倒计时 5000 → 到点 startAtMs+5000;回城等待 300 → 超时 startAtMs+5300。
    /// </summary>
    private static void EndByCountdownWithoutConfirmation(DirectRig rig, long startAtMs, List<string> expectedLogs)
    {
        rig.Transport.ClearSent();
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
        rig.PollAt(startAtMs);
        rig.StartSettlement(timedOut: true);
        AssertTimedOutMenuJustOpened(rig);
        int before = rig.Completed.Count;

        rig.PollAt(startAtMs + 4999);
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Empty(rig.Transport.Sent);

        rig.PollAt(startAtMs + 5000);   // 倒计时到点:自动选回城
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(SettlementChoice.ReturnToTown, rig.Ctrl.PendingChoice);
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));
        Assert.Empty(rig.Transport.Sent[0].Payload);
        Assert.Equal(before, rig.Completed.Count);

        rig.PollAt(startAtMs + 5299);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        rig.PollAt(startAtMs + 5300);
        Assert.Equal(before + 1, rig.Completed.Count);
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));   // 等回城超时不重发 1/45
        expectedLogs.Add(Show);
        expectedLogs.Add(TimeoutNotice);
        expectedLogs.Add("DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=timeout");
        expectedLogs.Add("DNF_ONLINE stage=settle event=fail reason=return_wait_timeout step=ReturnToTown");
        expectedLogs.Add("DNF_ONLINE stage=settle event=town reason=return_timeout area=pre_dungeon signal=timeout_fallback");
        Assert.Equal(expectedLogs.ToArray(), rig.Logs.ToArray());
        AssertIdleAfterSettlement(rig, before + 1);
    }

    /// <summary>玩家选回城,发 1/45 失败(返回非 Ok 状态):日志 choose(player) + fail(return_send_failed) + town(return_send_failed, fallback);1/45 只发这一次。</summary>
    private static void EndByPlayerWithFailedGiveUpSend(DirectRig rig, long startAtMs, List<string> expectedLogs)
    {
        rig.Transport.ClearSent();
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.NoCipher));
        rig.PollAt(startAtMs);
        rig.StartSettlement(timedOut: true);
        AssertTimedOutMenuJustOpened(rig);
        int before = rig.Completed.Count;

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);

        rig.Poll();   // 读到发送失败的结果:安全回城
        Assert.Equal(before + 1, rig.Completed.Count);
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));
        expectedLogs.Add(Show);
        expectedLogs.Add(TimeoutNotice);
        expectedLogs.Add("DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=player");
        expectedLogs.Add("DNF_ONLINE stage=settle event=fail reason=return_send_failed step=ReturnToTown");
        expectedLogs.Add("DNF_ONLINE stage=settle event=town reason=return_send_failed area=pre_dungeon signal=fallback");
        Assert.Equal(expectedLogs.ToArray(), rig.Logs.ToArray());
        AssertIdleAfterSettlement(rig, before + 1);
    }

    /// <summary>玩家还没选,服务端先用 0/2 把人带回城(服务端主动回城):一条命令都不发,日志 town(server_return, noti2)。</summary>
    private static void EndByServerInitiatedReturnBeforeAnyChoice(DirectRig rig, long startAtMs, List<string> expectedLogs)
    {
        rig.Transport.ClearSent();
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
        rig.PollAt(startAtMs);
        rig.StartSettlement(timedOut: true);
        AssertTimedOutMenuJustOpened(rig);
        int before = rig.Completed.Count;

        PushNoti2(rig.Transport);
        rig.Poll();
        Assert.Equal(before + 1, rig.Completed.Count);
        Assert.Empty(rig.Transport.Sent);   // 没选择就没有放弃命令
        expectedLogs.Add(Show);
        expectedLogs.Add(TimeoutNotice);
        expectedLogs.Add("DNF_ONLINE stage=settle event=town reason=server_return area=pre_dungeon signal=noti2");
        Assert.Equal(expectedLogs.ToArray(), rig.Logs.ToArray());
        AssertIdleAfterSettlement(rig, before + 1);
    }

    [Fact]
    public void Two_consecutive_settlement_notice_timeouts_each_send_the_give_up_command_once_player_choice_then_countdown()
    {
        // 保护:第一次(玩家选回城、服务端确认)与第二次(倒计时到点自动回城)的结算通知超时,放弃命令 1/45 各发且只发一次;
        // 第二次的日志必须是"choose(timeout) → 等回城超时 → town(return_timeout)",而不是没发命令就直接兜底的 "town(timeout, fallback)"。
        using var rig = new DirectRig();
        var expected = new List<string>();
        EndByPlayerConfirmedByNoti2(rig, 1000, expected);
        EndByCountdownWithoutConfirmation(rig, 2000, expected);
        Assert.Equal(4 + 5, rig.Logs.Count);
    }

    [Fact]
    public void Two_consecutive_settlement_notice_timeouts_each_send_the_give_up_command_once_countdown_then_player_choice()
    {
        // 保护:同上,顺序反过来:第一次倒计时到点自动回城,第二次玩家选回城(此时放弃命令标志必须已复位,第二次照样发 1/45 并等 0/2 确认)。
        using var rig = new DirectRig();
        var expected = new List<string>();
        EndByCountdownWithoutConfirmation(rig, 1000, expected);
        EndByPlayerConfirmedByNoti2(rig, 20000, expected);
        Assert.Equal(5 + 4, rig.Logs.Count);
    }

    [Fact]
    public void Four_consecutive_settlement_notice_timeouts_with_different_endings_each_behave_like_the_first()
    {
        // 保护:连续四次结算通知超时,结束方式各不相同(服务端先回城 / 发 1/45 失败 / 玩家选回城 / 倒计时到点):
        // 服务端先回城的那次没选择、不发命令;其后每一次(包括放弃命令发送失败之后的一次)仍然各发一条 1/45;累计日志逐行相等。
        using var rig = new DirectRig();
        var expected = new List<string>();
        EndByServerInitiatedReturnBeforeAnyChoice(rig, 1000, expected);
        EndByPlayerWithFailedGiveUpSend(rig, 2000, expected);
        EndByPlayerConfirmedByNoti2(rig, 3000, expected);
        EndByCountdownWithoutConfirmation(rig, 4000, expected);
        Assert.Equal(3 + 5 + 4 + 5, rig.Logs.Count);
    }
}
