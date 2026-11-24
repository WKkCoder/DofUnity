using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapA3Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器向结算订阅者交接时,房间的"收尾闸门"(<see cref="OnlineDungeonRoom.CanFinish"/> = 独占收包权已放回 且 没有挂起等菜单)的取值时序:
/// 订阅者被调用的那一刻闸门必须已经放开(<c>LeaveExclusiveReceiveAsync</c> 已完成,先放权、后通知),
/// 订阅者随后要求挂起,闸门就关上;选择完成之后房间的闸门被摘掉(null)。正常结算路径与"结算通知超时"路径都要这样。
/// 用带真联网进副本控制器的 <see cref="OnlineSettlementGapA3Kit.HandoffScene"/>(不挂结算控制器,订阅者由用例自己摆;逻辑时钟轮询)。
/// </summary>
public sealed class OnlineSettlementGapA5FinishGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_room_finish_gate_is_already_open_when_the_subscriber_is_called_closes_on_hold_and_is_removed_after_the_choice(bool timedOut)
    {
        // 保护:交接那一刻闸门读出来是 true(独占收包权先放回、再通知订阅者;超时路径同样要先放行再通知),
        // 订阅者要求挂起之后读出来是 false(挂起期间无论过多久都不放行),选择完成后房间上的闸门被摘掉(null);
        // 同时钉住:交接对象的"超时"标志与日志里的"离开"行(超时路径是 SettlementTimeout、正常路径是 ReturnReady)、订阅者只被调用一次。
        using var scene = new HandoffScene(timedOut, HandoffSubscriber.None);
        SettleRig rig = scene.Rig;
        var gateSeenInHandler = new List<bool>();
        var gatePresentInHandler = new List<bool>();
        OnlineDungeonSettlementReady ready = null;
        rig.DungeonCtrl.SettlementStarted += r =>
        {
            ready = r;
            Func<bool> gate = rig.LastRoom?.CanFinish;
            gatePresentInHandler.Add(gate != null);
            gateSeenInHandler.Add(gate != null && gate());
            r.HoldForMenu = true;
        };

        scene.EnterAndKillEverything();
        OnlineDungeonRoom room = rig.LastRoom;
        Assert.NotNull(room);
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement);

        Assert.Equal(new[] { true }, gatePresentInHandler);
        Assert.Equal(new[] { true }, gateSeenInHandler);
        Assert.NotNull(ready);
        Assert.Equal(timedOut, ready!.Data.IsTimedOut);
        Assert.True(ready.HoldForMenu);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);

        string[] expectedLogs = Lines(Opening, timedOut ? LeaveSettlementTimeout : LeaveReturnReady);
        Assert.Equal(expectedLogs, scene.Logs());

        // 挂起期间:闸门关着,过 600 毫秒逻辑时间也不放行,流程仍停在副本里
        Assert.NotNull(room.CanFinish);
        Assert.False(room.CanFinish!());
        for (int i = 0; i < 30; i++)
        {
            rig.Step(20);
        }
        Assert.False(room.CanFinish!());
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Single(gateSeenInHandler);

        // 选择完成:回城、控制器回空闲、房间上的闸门被摘掉
        ready.CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Null(room.CanFinish);
        Assert.Equal(expectedLogs, scene.Logs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_subscriber_that_does_not_hold_still_sees_the_gate_open_during_the_handoff(bool timedOut)
    {
        // 保护:订阅者不要求挂起时,交接那一刻闸门同样是放开的(先放权后通知,与是否挂起无关);
        // 超时路径随后走通用的超时中止(回城镇),正常路径走"结算退出"、返回延迟后自己回城;两条路都不留"拦着不退出副本"的标志。
        using var scene = new HandoffScene(timedOut, HandoffSubscriber.None);
        SettleRig rig = scene.Rig;
        var seen = new List<bool>();
        rig.DungeonCtrl.SettlementStarted += _ =>
        {
            Func<bool> gate = rig.LastRoom?.CanFinish;
            seen.Add(gate != null && gate());
        };

        scene.EnterAndKillEverything();
        Assert.NotNull(rig.LastRoom);
        rig.PumpUntil(() => scene.BackInTownAndIdle);

        Assert.Equal(new[] { true }, seen);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(timedOut ? 1 : 0, rig.Flow.Failures);
        Assert.Equal(timedOut ? 0 : 1, rig.Flow.Settlements);
    }
}
