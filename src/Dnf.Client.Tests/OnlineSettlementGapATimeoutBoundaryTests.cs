using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器各"等回复/等回城"超时的精确边界(逻辑时钟精确到毫秒,不读墙钟、不睡眠):
/// 起点之后恰好 N-1 毫秒仍在等,恰好 N 毫秒就超时(判定是 <c>&gt;=</c>);起点为逻辑时钟 0 时也从 0 起算;
/// 还没有逻辑时钟时发出的命令,起点在第一次轮询时才对齐;各超时项配 0 表示取默认常量(而不是 0 毫秒立刻超时)。
/// 全部用 <see cref="DirectRig"/>,期望值是手算的字面量。
/// </summary>
public sealed class OnlineSettlementGapATimeoutBoundaryTests
{
    private static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    /// <summary>把控制器推到菜单就绪(73 回第 0 槽有权 → 发 74),返回此刻的逻辑时钟;<paramref name="onEplp"/> 是对 1/75 的应答。</summary>
    private static long ReachMenu(DirectRig rig, long startAtMs, Action<byte[]> onEplp = null)
    {
        AutoAdvance(rig.Transport, CardRightBytes(1), onEplp);
        rig.PollAt(startAtMs);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        return rig.NowMs;
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(5000L)]
    public void Advance_72_wait_degrades_exactly_when_the_configured_interval_has_elapsed_and_logs_once(long startAtMs)
    {
        // 保护:等 (1,72) 回复的超时是"到点即超时"(>=):起点后 699 毫秒仍在等、700 毫秒恰好降级为带"没有回复"提示的退化菜单,只写一行超时日志;
        // 起点是逻辑时钟 0 时(第一次结算期间的轮询发生在 699)也必须从 0 起算,不能被第一次轮询"重新对齐"而推迟。
        using var rig = new DirectRig(o => o.AdvanceReplyTimeoutMs = 700);
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));   // 服务端静默
        rig.PollAt(startAtMs);                 // 空闲时的第一次轮询:只记下逻辑时钟
        rig.StartSettlement();                 // 1/72 在 startAtMs 发出
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72 }, SentIds(rig));

        rig.PollAt(startAtMs + 699);           // 结算期间的第一次轮询
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(0, rig.LogCount(P + " event=fail reason=advance_72_timeout step=72"));

        rig.PollAt(startAtMs + 700);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=advance_72_timeout step=72"));
        Assert.Equal(new ushort[] { 72 }, SentIds(rig));   // 超时不重发、不发 73/74
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs); // 菜单倒计时从降级这一刻起算

        rig.PollAt(startAtMs + 700 + 1000);
        Assert.Equal(4000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=advance_72_timeout step=72"));   // 之后不再重复写
    }

    [Fact]
    public void Advance_72_wait_started_before_any_clock_is_aligned_to_the_first_poll_and_then_times_out_exactly()
    {
        // 保护:还没有逻辑时钟(从没轮询过)时发出 1/72,超时起点在第一次轮询时对齐到那一刻的时钟(哪怕它很大),
        // 不会因为起点还没设就立刻超时;之后同样 N-1 毫秒还在等、N 毫秒降级。
        using var rig = new DirectRig(o => o.AdvanceReplyTimeoutMs = 700);
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
        rig.StartSettlement();
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);

        rig.PollAt(9_000_000);
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        rig.PollAt(9_000_699);
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        rig.PollAt(9_000_700);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=advance_72_timeout step=72"));
    }

    [Fact]
    public void Advance_73_and_74_waits_also_time_out_exactly_at_the_configured_interval()
    {
        // 保护:(1,73) 与 (1,74) 的等待超时同样是到点即超时(起点是发出该命令那一轮的逻辑时钟,不是整次结算的起点)。
        using (var rig = new DirectRig(o => o.AdvanceReplyTimeoutMs = 500))
        {
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72)
                {
                    rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                }
                return Task.FromResult(FrameStatus.Ok);   // 73 静默
            });
            rig.PollAt(1000);
            rig.StartSettlement();
            rig.PollAt(1100);   // 读到 72 的回复,在 1100 发出 73
            Assert.Equal(OnlineSettlementPhase.AdvancingCardRight, rig.Ctrl.Phase);
            rig.PollAt(1599);
            Assert.Equal(OnlineSettlementPhase.AdvancingCardRight, rig.Ctrl.Phase);
            rig.PollAt(1600);
            Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
            Assert.Equal(1, rig.LogCount(P + " event=fail reason=advance_73_timeout step=73"));
            Assert.Equal(new ushort[] { 72, 73 }, SentIds(rig));
        }

        using (var rig = new DirectRig(o => o.AdvanceReplyTimeoutMs = 500))
        {
            var pending74 = new TaskCompletionSource<FrameStatus>();
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72)
                {
                    rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                }
                else if (id == 73)
                {
                    rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                }
                else if (id == 74)
                {
                    return pending74.Task;   // 1/74 的发送一直不完成
                }
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.PollAt(1000);
            rig.StartSettlement();
            rig.PollAt(1100);   // 读到 72 的回复,在 1100 发出 73;73 的回复同步到达,同一轮就读到并在 1100 发出 74
            Assert.Equal(OnlineSettlementPhase.SendingSelectCard, rig.Ctrl.Phase);
            rig.PollAt(1599);
            Assert.Equal(OnlineSettlementPhase.SendingSelectCard, rig.Ctrl.Phase);
            rig.PollAt(1600);
            Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
            Assert.Equal(1, rig.LogCount(P + " event=fail reason=advance_74_timeout step=74"));
            Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        }
    }

    [Fact]
    public void Eplp_75_wait_falls_back_to_town_exactly_when_the_configured_interval_has_elapsed()
    {
        // 保护:等 (1,75) 回复的超时是到点即超时:选择那一刻起 699 毫秒仍在等,700 毫秒恰好走"eplp 超时回城"
        // (提示 EplpTimeout、记一次回城、回落进副本前的城镇区、只发过一条 1/75),不重发。
        using var rig = new DirectRig(o => o.EplpReplyTimeoutMs = 700);
        long t = ReachMenu(rig, 2000);                      // 1/75 不回应
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));   // 选择发生在逻辑时钟 t
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);

        rig.PollAt(t + 699);
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        Assert.Equal(0, rig.LogCount(P + " event=fail reason=eplp_timeout step=Retry"));

        rig.PollAt(t + 700);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementNoticeKind.EplpTimeout, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=eplp_timeout step=Retry"));
        Assert.Equal(1, rig.LogCount(P + " event=town reason=eplp_timeout area=pre_dungeon signal=timeout_fallback"));
        OnlineSettlementReturnTarget target = Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
        Assert.Equal(1, rig.Transport.Sent.Count(s => s.PacketId == 75));
    }

    [Fact]
    public void Timed_out_settlement_menu_started_before_any_clock_waits_from_the_first_poll_not_from_zero()
    {
        // 保护:"结算通知超时"菜单(还没有任何轮询、没有逻辑时钟)里就选回城:发一条 1/45(放弃)进入等回城,
        // 等待起点要在第一次轮询时对齐到当时的(很大的)时钟,而不是 0——否则第一次轮询就会判成"等回城超时"。
        // 之后同样 N-1 毫秒仍在等、N 毫秒走超时回城。
        using var rig = new DirectRig(o => o.TownReturnWaitTimeoutMs = 300);
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
        rig.StartSettlement(timedOut: true);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.SettlementTimeout, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Empty(rig.Transport.Sent);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));

        rig.PollAt(1_000_000);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        rig.PollAt(1_000_299);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        rig.PollAt(1_000_300);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=return_wait_timeout step=ReturnToTown"));
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));   // 不重发 1/45
    }

    // ---------------- 各超时项配 0:取默认常量 ----------------

    private static DirectRig DefaultTimeoutsRig()
    {
        return new DirectRig(o =>
        {
            o.AdvanceReplyTimeoutMs = 0;
            o.EplpReplyTimeoutMs = 0;
            o.ReturnWaitTimeoutMs = 0;
            o.TownReturnWaitTimeoutMs = 0;
            o.MenuCountdownMs = 0;
        });
    }

    [Fact]
    public void Zero_advance_timeout_means_the_default_five_seconds_not_an_immediate_timeout()
    {
        // 保护:AdvanceReplyTimeoutMs 配 0 取默认 5000 毫秒:发出 1/72 后 4999 毫秒仍在等,5000 毫秒才降级;降级后的菜单倒计时取默认 50000。
        using var rig = DefaultTimeoutsRig();
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
        rig.PollAt(2000);
        rig.StartSettlement();
        rig.PollAt(2000);   // 同一时刻再轮询一次:0 毫秒不能算超时
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        rig.PollAt(2000 + 4999);
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        rig.PollAt(2000 + 5000);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(50_000, rig.Ctrl.RemainingCountdownMs);
    }

    [Fact]
    public void Zero_eplp_timeout_means_the_default_five_seconds()
    {
        // 保护:EplpReplyTimeoutMs 配 0 取默认 5000 毫秒:选择后 4999 毫秒仍在等 (1,75) 回复,5000 毫秒走超时回城。
        using var rig = DefaultTimeoutsRig();
        long t = ReachMenu(rig, 2000);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        rig.PollAt(t);
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        rig.PollAt(t + 4999);
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        rig.PollAt(t + 5000);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=eplp_timeout step=SelectOtherDungeon"));
    }

    [Fact]
    public void Zero_town_return_wait_timeout_means_the_default_six_seconds_for_return_to_town()
    {
        // 保护:选"返回城镇"得到执行确认后,等服务端回城信号的上限是 TownReturnWaitTimeoutMs;配 0 取默认 6000 毫秒:
        // 读到确认那一轮起 5999 毫秒仍在等,6000 毫秒走"回城等待超时"收尾。
        using var rig = DefaultTimeoutsRig();
        ReachMenu(rig, 2000, payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1])));
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll(16);       // 读到执行确认:进入等回城,起点是这一轮
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        long t = rig.NowMs;
        rig.PollAt(t + 5999);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        rig.PollAt(t + 6000);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=return_wait_timeout step=ReturnToTown"));
    }

    [Fact]
    public void Zero_return_wait_timeout_means_the_default_eight_seconds_for_retry_and_never_resends_cmd_15()
    {
        // 保护:选"再次挑战"得到执行确认后,等回城信号的上限是 ReturnWaitTimeoutMs;配 0 取默认 8000 毫秒:7999 毫秒仍在等,
        // 8000 毫秒到点时因为服务端始终没确认回城,安全落回城镇(选定的是回城而不是再次挑战)。
        using var rig = DefaultTimeoutsRig();
        ReachMenu(rig, 2000, payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1])));
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.Poll(16);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        long t = rig.NowMs;
        rig.PollAt(t + 7999);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        rig.PollAt(t + 8000);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=return_wait_timeout step=Retry"));
    }

    [Fact]
    public void Zero_town_return_wait_timeout_is_also_the_default_six_seconds_in_the_settlement_timeout_menu()
    {
        // 保护:"结算通知超时"菜单选回城发 1/45 之后的等待上限同样用 TownReturnWaitTimeoutMs(配 0 取默认 6000 毫秒)。
        using var rig = DefaultTimeoutsRig();
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));
        rig.PollAt(2000);
        rig.StartSettlement(timedOut: true);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));   // 起点 2000
        Assert.Equal(new ushort[] { 45 }, SentIds(rig));
        rig.PollAt(2000 + 5999);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        rig.PollAt(2000 + 6000);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
    }
}
