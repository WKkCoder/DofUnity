using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA3Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 正常结算路径(服务端回齐 0x22 + 0x23,战斗以"返回就绪"收场)上,
/// 联网进副本控制器向 <c>SettlementStarted</c> 订阅者交接时写出的<b>全部</b>日志行——五种情形各自整份序列逐行相等:
/// 订阅者抛异常且没要求挂起、先要求挂起再抛异常、只要求挂起、既不挂起也不抛、根本没有订阅者。
/// 带真联网进副本控制器的 <see cref="OnlineSettlementGapAKit.SettleRig"/>(不挂结算控制器,订阅者由用例自己摆;逻辑时钟轮询)。
/// 期望值都是手算的整行字面量(夹具:两只怪、单房间、全部上报,房间返回延迟 20 毫秒),异常类型名写在 InlineData 里,不从被抛的对象取。
/// </summary>
public sealed class OnlineSettlementGapA3NormalHandoffLogTests
{
    /// <summary>
    /// 没人要求挂起的正常结算:交接完控制器停在"结算退出"阶段(闸门放开、流程不拦、流程还在副本里),
    /// 房间返回延迟到点后流程自己回城(记一次结算、原因 choice ReturnToTown),控制器随后回空闲;全程日志恰为 <paramref name="expected"/>。
    /// </summary>
    private static void AssertNoHoldSettlementLeavesByItself(HandoffScene scene, string[] expected)
    {
        SettleRig rig = scene.Rig;
        scene.EnterAndKillEverything();
        OnlineDungeonRoom room = rig.LastRoom;
        Exception leaked = Record.Exception(() => rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.SettlingExit));
        Assert.Null(leaked);

        Assert.Equal(expected, scene.Logs());
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.True(room.CanFinish());
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);

        rig.PumpUntil(() => scene.BackInTownAndIdle);
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("choice ReturnToTown", scene.FlowReasons().Last());
        Assert.Equal(0, rig.Disconnects);

        rig.Step();
        rig.Step();
        Assert.Equal(expected, scene.Logs());
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("SyntheticSubscriberFault")]
    public void A_subscriber_that_throws_on_the_normal_settlement_is_swallowed_and_logged_and_the_flow_still_leaves_by_itself(string faultTypeName)
    {
        // 保护:正常结算路径上订阅者抛异常(抛之前没要求挂起)——异常不传出控制器;控制器先写"离开(ReturnReady,结果与奖励都解析成功)"行,
        // 再写"订阅者失败"行(原因 SettlementHandlerFault、步骤是实际抛出的异常类型名);因为没人要求挂起,控制器照常停在"结算退出"阶段、
        // 放开房间闸门,流程在返回延迟之后自己回城。整份日志序列逐行相等,没有通用中止的行。
        using var scene = new HandoffScene(timedOut: false, Subscriber.ThrowOnly, faultTypeName);

        AssertNoHoldSettlementLeavesByItself(scene, Lines(Opening, LeaveReturnReady, HandlerFault(faultTypeName)));

        Assert.Equal(1, scene.Invocations);
        Assert.False(scene.Ready.Data.IsTimedOut);
        Assert.Equal(OnlineSettlementParseState.Succeeded, scene.Ready.Data.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Succeeded, scene.Ready.Data.RewardParseState);
        Assert.False(scene.Ready.HoldForMenu);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("SyntheticSubscriberFault")]
    public void A_subscriber_that_holds_and_then_throws_on_the_normal_settlement_still_holds_the_flow_for_the_menu(string faultTypeName)
    {
        // 保护:正常结算路径上订阅者先要求挂起、再抛异常——异常被吞掉并写"订阅者失败"行(类型名逐字);挂起的要求仍然生效:
        // 控制器停在"挂起等结算"阶段、流程被拦着不退出副本、房间收尾闸门保持关闭(过多久都不变);
        // 订阅者之后选择回城才收尾(记一次结算、原因 online settlement ReturnToTown),此后控制器不再写日志。
        // 注意:这是"现状行为"的钉子——真结算控制器的处理函数一进来就先要求挂起,若它随后在自己内部抛异常(例如宿主回调出错),
        // 流程会一直挂着、没有人来收尾(疑似缺陷,待作者定夺);若日后改成"抛异常即放弃挂起",本用例的挂起断言与日志序列要随之改写。
        using var scene = new HandoffScene(timedOut: false, Subscriber.HoldThenThrow, faultTypeName);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        OnlineDungeonRoom room = rig.LastRoom;
        Exception leaked = Record.Exception(() => rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement));
        Assert.Null(leaked);

        string[] expected = Lines(Opening, LeaveReturnReady, HandlerFault(faultTypeName));
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.True(scene.Ready.HoldForMenu);
        Assert.False(scene.Ready.Data.IsTimedOut);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(room.CanFinish());
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);

        for (int i = 0; i < 30; i++)
        {
            rig.Step(20);
        }
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Settlements);

        scene.Ready.CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal("online settlement ReturnToTown", scene.FlowReasons().Last());
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(0, rig.Disconnects);
    }

    [Fact]
    public void A_normal_settlement_handler_that_holds_without_throwing_logs_only_the_return_ready_leave_line()
    {
        // 保护:正常路径上订阅者正常接手并要求挂起——控制器只写一行"离开(ReturnReady,结果与奖励都解析成功)",
        // 没有"订阅者失败"行;挂起期间与选择之后都不再多写日志。选"选择地下城"时流程回到副本选择屏、原因 online settlement SelectDungeon。
        using var scene = new HandoffScene(timedOut: false, Subscriber.Hold);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement);

        string[] expected = Lines(Opening, LeaveReturnReady);
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.False(scene.Ready.Data.IsTimedOut);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);

        for (int i = 0; i < 10; i++)
        {
            rig.Step(20);
        }
        Assert.Equal(expected, scene.Logs());

        scene.Ready.CompleteChoice(SettlementChoice.SelectDungeon);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal("online settlement SelectDungeon", scene.FlowReasons().Last());
        Assert.Equal(expected, scene.Logs());
    }

    [Fact]
    public void A_normal_settlement_handler_that_neither_holds_nor_throws_logs_only_the_return_ready_leave_line_and_the_flow_leaves_by_itself()
    {
        // 保护:正常路径上订阅者收到了交接、既没要求挂起也没抛——控制器只写一行"离开(ReturnReady)",没有"订阅者失败"行,
        // 停在"结算退出"阶段,流程在返回延迟之后自己回城。
        using var scene = new HandoffScene(timedOut: false, Subscriber.Plain);

        AssertNoHoldSettlementLeavesByItself(scene, Lines(Opening, LeaveReturnReady));

        Assert.Equal(1, scene.Invocations);
        Assert.False(scene.Ready.Data.IsTimedOut);
        Assert.False(scene.Ready.HoldForMenu);
    }

    [Fact]
    public void A_normal_settlement_without_any_subscriber_logs_only_the_return_ready_leave_line_and_the_flow_leaves_by_itself()
    {
        // 保护:正常路径上事件没有任何订阅者——控制器照常写"离开(ReturnReady)"一行,不写"订阅者失败"行、不抛异常,
        // 停在"结算退出"阶段,流程在返回延迟之后自己回城。
        using var scene = new HandoffScene(timedOut: false, Subscriber.None);

        AssertNoHoldSettlementLeavesByItself(scene, Lines(Opening, LeaveReturnReady));

        Assert.Equal(0, scene.Invocations);
        Assert.Null(scene.Ready);
    }
}
