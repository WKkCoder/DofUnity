using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapA2Kit;
using static Dnf.Client.Tests.OnlineSettlementGapA5Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 自动推进(1/72 → 1/73 → 1/74)的某一次发送失败时,同一次轮询里的后续处理:
/// 控制器先把菜单降级(提示"推进无回复"、倒计时起算),随后<b>同一次轮询里</b>仍要继续排空已到达的入站帧并检查服务端回城信号——
/// 所以失败那一刻若队列里已经有服务端的回城通知(0/2 或 0/23 + 0/24),这一次轮询就直接按"服务端已回城"收尾,不必再等下一次轮询。
/// 与"发送失败后下一次轮询才处理"的区别只能这样观察:失败轮询之前先把回城帧入队,只轮询一次,看是否已收尾。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟,无线程);日志行是手算的整行字面量(夹具:菜单倒计时 5000、各回复超时 300)。
/// </summary>
public sealed class OnlineSettlementGapA5SameTickFailureTests
{
    private const string FailAt72 = "DNF_ONLINE stage=settle event=fail reason=advance_send_failed step=AdvancingScoreScroll";
    private const string FailAt73 = "DNF_ONLINE stage=settle event=fail reason=advance_send_failed step=AdvancingCardRight";
    private const string FailAt74 = "DNF_ONLINE stage=settle event=fail reason=advance_send_failed step=SendingSelectCard";

    private const string TownNoti2 = "DNF_ONLINE stage=settle event=town reason=server_return area=pre_dungeon signal=noti2";
    private const string TownPair = "DNF_ONLINE stage=settle event=town reason=server_return area=server_area signal=area_pair";
    private const string TownBoth = "DNF_ONLINE stage=settle event=town reason=server_return area=server_area signal=noti2_and_area";

    private static void PushSignal(DirectRig rig, string signal)
    {
        if (signal == "noti2" || signal == "both")
        {
            PushNoti2(rig.Transport);
        }
        if (signal == "pair" || signal == "both")
        {
            PushUserArea(rig.Transport, village: 2, area: 5);
            PushAreaUsers(rig.Transport, village: 2, area: 5);
        }
    }

    [Theory]
    [InlineData(72, "noti2")]
    [InlineData(73, "noti2")]
    [InlineData(74, "noti2")]
    [InlineData(72, "pair")]
    [InlineData(73, "pair")]
    [InlineData(74, "pair")]
    [InlineData(72, "both")]
    [InlineData(74, "both")]
    public void A_failed_advance_send_still_drains_the_frames_already_queued_and_finishes_the_server_return_in_the_same_poll(int failingStep, string signal)
    {
        // 保护:推进命令发送失败 → 降级菜单之后,同一次轮询不会提前返回:已入队的服务端回城通知照常被排空并检查,
        // 这一次轮询就以"服务端主动回城"收尾(交还回城、服务端主动回城标志、回城日志里的信号种类与落位来源都对)。
        // 若失败那次轮询提前返回,收尾要晚一次轮询,下面"只轮询一次"的断言就不成立。
        using var rig = new DirectRig();
        ushort failingId = failingStep == 72
            ? SettlementMenuPayloads.ScoreScrollStatePacketId
            : (failingStep == 73 ? SettlementMenuPayloads.CardSelectRightStatePacketId : SettlementMenuPayloads.SelectCardPacketId);

        // 前面的几步都成功并自动回包,只有第 failingStep 步的发送失败(状态非 Ok)
        rig.Transport.SetSendHandler((id, _) =>
        {
            if (id == failingId)
            {
                return Task.FromResult(FrameStatus.NoCipher);
            }
            if (id == SettlementMenuPayloads.ScoreScrollStatePacketId)
            {
                rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == SettlementMenuPayloads.CardSelectRightStatePacketId)
            {
                rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Transport.Sent.Any(s => s.PacketId == failingId));

        // 失败的任务已经在途(同步完成),但控制器还没读到它;此刻把回城通知入队
        OnlineSettlementPhase advancing = failingStep == 72
            ? OnlineSettlementPhase.AdvancingScoreScroll
            : (failingStep == 73 ? OnlineSettlementPhase.AdvancingCardRight : OnlineSettlementPhase.SendingSelectCard);
        Assert.Equal(advancing, rig.Ctrl.Phase);
        PushSignal(rig, signal);
        Assert.Empty(rig.Completed);

        long before = rig.NowMs;
        rig.PollAt(before + 16);   // 只轮询一次:读到发送失败 → 降级菜单 → 同一次轮询排空回城帧 → 收尾

        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.True(rig.Ctrl.ServerInitiatedReturn);
        Assert.Equal(0, rig.Disconnects);

        OnlineSettlementReturnTarget target = Assert.Single(rig.Targets);
        string[] expected;
        string failLine = failingStep == 72 ? FailAt72 : (failingStep == 73 ? FailAt73 : FailAt74);
        string townLine = signal == "noti2" ? TownNoti2 : (signal == "pair" ? TownPair : TownBoth);
        if (signal == "noti2")
        {
            Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
        }
        else
        {
            Assert.Equal(OnlineSettlementReturnSource.ServerUserArea, target.Source);
            Assert.Equal(2, target.Place!.Value.Town);
            Assert.Equal(5, target.Place!.Value.Area);
        }

        if (failingStep == 72)
        {
            expected = new[] { Show, Sent72, failLine, townLine };
        }
        else if (failingStep == 73)
        {
            expected = new[] { Show, Sent72, Ok72, Sent73, failLine, townLine };
        }
        else
        {
            expected = new[] { Show, Sent72, Ok72, Sent73, Ok73, Sent74, failLine, townLine };
        }
        Assert.Equal(expected, AllLogs(rig));

        // 收尾之后再轮询什么都不再发生
        rig.Poll();
        rig.Poll();
        Assert.Equal(expected, AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
    }

    [Theory]
    [InlineData(72)]
    [InlineData(73)]
    [InlineData(74)]
    public void A_failed_advance_send_without_any_queued_frames_just_degrades_the_menu_and_starts_the_countdown_at_that_poll(int failingStep)
    {
        // 保护:对照组——失败那次轮询里队列是空的:菜单降级(阶段 MenuDegraded)、提示"推进无回复"、
        // 倒计时从"失败被读到的这一次轮询"起算(剩余 = 夹具的 5000 毫秒),没有收尾、没有多余日志。
        using var rig = new DirectRig();
        ushort failingId = failingStep == 72
            ? SettlementMenuPayloads.ScoreScrollStatePacketId
            : (failingStep == 73 ? SettlementMenuPayloads.CardSelectRightStatePacketId : SettlementMenuPayloads.SelectCardPacketId);
        rig.Transport.SetSendHandler((id, _) =>
        {
            if (id == failingId)
            {
                return Task.FromResult(FrameStatus.NoCipher);
            }
            if (id == SettlementMenuPayloads.ScoreScrollStatePacketId)
            {
                rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == SettlementMenuPayloads.CardSelectRightStatePacketId)
            {
                rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Transport.Sent.Any(s => s.PacketId == failingId));
        long failingPoll = rig.NowMs + 16;
        rig.PollAt(failingPoll);

        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        Assert.Empty(rig.Completed);
        Assert.False(rig.Ctrl.ServerInitiatedReturn);

        // 倒计时确实从 failingPoll 起算:再过 1000 毫秒剩 4000,过 5000 毫秒到点按"超时"回城
        rig.PollAt(failingPoll + 1000);
        Assert.Equal(4000, rig.Ctrl.RemainingCountdownMs);
        Assert.Empty(rig.Completed);
    }
}
