using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 断线的"单信号"测试:传输/端口给的两个断线信号(事件、"已断线"标志)是<b>独立</b>的,各来一个就必须被识别并收尾;
/// 既有用例总是两个同时置位,杀不了"或"写成"且"。两个控制器各一组:
/// 结算控制器(<see cref="OnlineSettlementController"/>)与联网进副本控制器(<see cref="OnlineDungeonFlowController"/> 的结算停留阶段)。
/// </summary>
public sealed class OnlineSettlementGapADisconnectTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_settlement_controller_reacts_to_either_disconnect_signal_alone_and_notifies_once(bool flagOnly, bool midAdvance)
    {
        // 保护:结算控制器在轮询第一步识别断线——只触发传输的 Disconnected 事件(IsDisconnected 为假),或只让 IsDisconnected 为真(不触发事件),
        // 两种都要:收尾回空闲(退订传输、不交还选择)、通知宿主恰好一次、日志写一行断线(带断线时所处的阶段)。
        using var rig = new DirectRig();
        if (midAdvance)
        {
            rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));   // 服务端静默,停在推进阶段
        }
        else
        {
            AutoAdvance(rig.Transport, CardRightBytes(1));
        }
        rig.PollAt(1000);
        rig.StartSettlement();
        OnlineSettlementPhase expected = midAdvance ? OnlineSettlementPhase.AdvancingScoreScroll : OnlineSettlementPhase.MenuReady;
        rig.PollUntil(() => midAdvance || rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(expected, rig.Ctrl.Phase);
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);

        if (flagOnly)
        {
            rig.Transport.IsDisconnected = true;
        }
        else
        {
            rig.Transport.RaiseDisconnectedEventOnly();
        }
        // 信号是在下一次轮询的第一步才处理的:轮询之前控制器还没收尾
        Assert.Equal(expected, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Disconnects);

        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(1, rig.Disconnects);
        Assert.Empty(rig.Completed);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=disconnected step=" + expected));

        // 之后再轮询也不会重复通知
        rig.Poll();
        rig.Poll();
        Assert.Equal(1, rig.Disconnects);
        Assert.Equal(1, rig.LogCount(P + " event=fail reason=disconnected step=" + expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_dungeon_controller_leaves_the_settlement_hold_on_either_disconnect_signal_alone(bool flagOnly)
    {
        // 保护:联网进副本控制器在"结算停留"阶段的断线清理——场景 1 只触发端口的 Disconnected 事件(IsDisconnected 为假),
        // 场景 2 只让 IsDisconnected 为真(不触发事件):两种都必须撤掉"拦着不退出副本"的标志、回空闲、通知宿主恰好一次;
        // 控制器空闲之后,结算处理器迟到的选择不再生效(不记结算、不换阶段)。
        using var rig = new SettleRig(attachSettlement: false);
        WireStandardDungeonResponder(rig, monsterCount: 1);
        OnlineDungeonSettlementReady ready = null;
        rig.DungeonCtrl.SettlementStarted += r =>
        {
            r.HoldForMenu = true;
            ready = r;
        };
        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement);
        Assert.NotNull(ready);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(0, rig.Disconnects);

        if (flagOnly)
        {
            rig.Port.IsDisconnected = true;
        }
        else
        {
            rig.Port.RaiseDisconnectedEventOnly();
        }
        // 控制器要等下一次轮询才处理
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        rig.Step();

        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(1, rig.Disconnects);
        Assert.Null(rig.LastRoom.CanFinish);

        rig.Step();
        rig.Step();
        Assert.Equal(1, rig.Disconnects);

        // 撤掉拦截之后流程可能按房间自己的返回延迟走掉;这里只看迟到的选择不再生效(记结算数不变、阶段不变)
        int settlementsBefore = rig.Flow.Settlements;
        GameStage stageBefore = rig.Flow.Stage;
        ready.CompleteChoice(SettlementChoice.SelectDungeon);
        Assert.Equal(settlementsBefore, rig.Flow.Settlements);
        Assert.Equal(stageBefore, rig.Flow.Stage);
    }
}
