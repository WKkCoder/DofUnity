using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// "同一个结算控制器连续做两次结算"的复位测试:第一次结算故意把各种状态都弄脏(跳过选牌、鼠标悬停、重试被拒、服务端回城信号),
/// 第二次结算必须像全新的一样从头走完;另外一帧在第一次结算结束后才被送到(摘掉订阅前已在途)时,不能被第二次结算当成自己的消息消费。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟,无线程),期望值都是手算的字面量。
/// </summary>
public sealed class OnlineSettlementGapATwoSettlementsTests
{
    private const int RowX = SettlementArt.PanelX + 10;

    /// <summary>菜单第 <paramref name="row"/> 行里的一个点(行 0 再次挑战、1 快速组队、2 选其他副本、3 回城)。</summary>
    private static int RowY(int row) => SettlementArt.RowTop + SettlementArt.RowHeight * row + 5;

    private static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    [Fact]
    public void A_second_settlement_on_the_same_controller_starts_from_a_clean_slate_and_runs_the_whole_flow_again()
    {
        // 保护:一次结算(含被拒的重试、跳过选牌、鼠标悬停)结束后,同一个控制器再开一次结算时——
        // 悬停行回到 -1、"跳过选牌"标志与已选下标清空、重试可选、72/73/74 重新发、倒计时按新结算起算、被拒的提示不残留,
        // 且第二次结算能完整走到菜单并用另一个选项结束。
        using var rig = new DirectRig();

        // 还没有结算时挪鼠标:悬停行必须是 -1(不是别的负数)
        rig.Ctrl.SetPointer(RowX, RowY(0));
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.False(rig.Ctrl.CardSelectSkipped);

        // ---------------- 第一次结算:没有翻牌权(跳过 1/74);再次挑战被拒(不是队长);回城成功 ----------------
        AutoAdvance(rig.Transport, CardRightBytes(0, 0, 0, 0), payload =>
        {
            byte cmd = payload[1];
            if (cmd == (byte)EplpCommand.Retry)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(OnlineSettlementController.ErrorNotLeader, cmd));
            }
            else if (cmd == (byte)EplpCommand.ReturnToTown)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(cmd));
                PushNoti2(rig.Transport);
            }
        });
        rig.PollAt(1000);
        rig.StartSettlement();
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.SelectedCardIndex);
        Assert.Equal(OnlineSettlementNoticeKind.CardSelectSkipped, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(new ushort[] { 72, 73 }, SentIds(rig));

        // 鼠标悬停在第 0 行;发出再次挑战(1/75 [1,0])之后悬停复位;有待决选择时再挪鼠标仍是 -1
        rig.Ctrl.SetPointer(RowX, RowY(0));
        Assert.Equal(0, rig.Ctrl.HoverRow);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.Ctrl.SetPointer(RowX, RowY(2));
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.Equal(new byte[] { 1, 0 }, rig.Transport.Sent[^1].Payload);

        // 读到被拒回复:退化菜单、再次挑战被禁用、别的选项仍可选
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.EplpRejectedNotLeader, rig.Ctrl.CurrentNotice?.Kind);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.QuickParty));

        // 悬停到第 3 行再选回城:服务端确认(1/75 成功 + 0/2)后结算收尾
        rig.Ctrl.SetPointer(RowX, RowY(3));
        Assert.Equal(3, rig.Ctrl.HoverRow);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(new byte[] { 1, 2 }, rig.Transport.Sent[^1].Payload);
        rig.Poll();

        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Null(rig.Ctrl.Summary);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.SelectedCardIndex);
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));   // 已空闲:什么都不可选
        // 空闲时挪鼠标:同样是 -1
        rig.Ctrl.SetPointer(RowX, RowY(1));
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        OnlineSettlementReturnTarget firstTarget = Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, firstTarget.Source);
        Assert.Equal(new TownPlace(1, 3, TownEntry.Return), firstTarget.Place);

        // ---------------- 第二次结算:有翻牌权(槽 2),选其他副本成功 ----------------
        rig.Transport.ClearSent();
        AutoAdvance(rig.Transport, CardRightBytes(0, 0, 1, -1), payload =>
        {
            byte cmd = payload[1];
            if (cmd == (byte)EplpCommand.SelectOtherDungeon)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(cmd));
                PushNoti2(rig.Transport);
            }
        });
        rig.StartSettlement();
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.SelectedCardIndex);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.Equal(OnlineSettlementNoticeKind.Advancing, rig.Ctrl.CurrentNotice?.Kind);
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        long readyAt = rig.NowMs;

        // 又发了一遍 72 → 73 → 74(选槽 2:模式 0、下标 2)
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        Assert.Equal(new byte[] { 0, 2 }, rig.Transport.Sent[2].Payload);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Equal((byte)2, rig.Ctrl.SelectedCardIndex);
        Assert.Null(rig.Ctrl.CurrentNotice);
        Assert.False(rig.Ctrl.ServerInitiatedReturn);

        // 重试没有残留的"已被拒"标记;快速组队永远禁用
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.QuickParty));
        Assert.Equal(-1, rig.Ctrl.HoverRow);

        // 倒计时按新结算起算:刚进菜单是 5000,过 1000 毫秒剩 4000
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        rig.PollAt(readyAt + 1000);
        Assert.Equal(4000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        Assert.Equal(new byte[] { 1, 1 }, rig.Transport.Sent[^1].Payload);
        rig.Poll();

        Assert.Equal(new[] { SettlementChoice.ReturnToTown, SettlementChoice.SelectDungeon }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.SelectedCardIndex);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Single(rig.Targets);   // 选其他副本不落城镇目标
        Assert.Equal(0, rig.Disconnects);

        // 两次结算各写一遍"开始"日志(解析状态都是"不适用"),说明第二次是重新开的
        Assert.Equal(2, rig.LogCount(P + " event=show timedout=false result=NotApplicable reward=NotApplicable drops=0 diffs=0"));
        Assert.Equal(2, rig.LogCount(P + " event=advance step=score_scroll status=sent"));
    }

    [Fact]
    public void A_frame_handed_over_after_the_settlement_ended_is_not_consumed_by_the_next_settlement()
    {
        // 保护:结算结束后控制器已摘掉传输订阅,但"摘掉之前已在途"的一帧(这里用订阅时记下的委托模拟)再被送来时必须被丢弃,
        // 不能留在队列里被下一次结算当成自己的消息(这里是一条会让下一次结算立刻被判成"服务端主动回城"的 0/2)。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
            PushNoti2(rig.Transport);
        });
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Transport.FrameSubscribers);

        Action<PacketClass, int, byte[]> staleHandler = rig.Transport.LastFrameHandler;
        Assert.NotNull(staleHandler);
        staleHandler(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
        staleHandler(PacketClass.Cmd, 72, new byte[] { 1 });

        // 第二次结算:不回应任何命令,只看有没有被"过期的 0/2"带跑
        rig.Transport.ClearSent();
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        rig.Poll();
        rig.Poll();

        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.ServerInitiatedReturn);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);   // 第二次结算还没有选定
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        Assert.Single(rig.Targets);
    }
}
