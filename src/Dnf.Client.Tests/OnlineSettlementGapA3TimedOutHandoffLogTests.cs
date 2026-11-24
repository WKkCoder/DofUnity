using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA3Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// "结算通知超时"路径(服务端只回 0x22、等不到 0x23,战斗在结算等待到点后以 Timeout 收场)上,
/// 联网进副本控制器向 <c>SettlementStarted</c> 订阅者交接时写出的<b>全部</b>日志行——五种情形各自整份序列逐行相等:
/// 订阅者抛异常且没要求挂起、先要求挂起再抛异常、只要求挂起、既不挂起也不抛、根本没有订阅者。
/// 带真联网进副本控制器的 <see cref="OnlineSettlementGapAKit.SettleRig"/>(不挂结算控制器,订阅者由用例自己摆;逻辑时钟轮询)。
/// 期望值都是手算的整行字面量(夹具:两只怪、单房间、全部上报),异常类型名写在 InlineData 里,不从被抛的对象取。
/// </summary>
public sealed class OnlineSettlementGapA3TimedOutHandoffLogTests
{
    /// <summary>超时路径上"没人接手挂起"时通用的中止收尾:回城镇、控制器回空闲、记一次失败(不是结算选择)、城镇上亮"超时"提示。</summary>
    private static void AssertGenericTimeoutAbortFinished(HandoffScene scene)
    {
        SettleRig rig = scene.Rig;
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal(0, rig.Flow.Settlements);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal("online abort Timeout", scene.FlowReasons().Last());
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.DungeonCtrl.ActiveNotice?.Kind);
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Disconnects);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("SyntheticSubscriberFault")]
    public void A_subscriber_that_throws_on_a_timed_out_settlement_is_swallowed_and_logged_and_the_generic_timeout_abort_still_finishes(string faultTypeName)
    {
        // 保护:结算通知超时路径上订阅者抛异常(抛之前没要求挂起)——异常不传出控制器;控制器先写超时的"离开"行,
        // 再写"订阅者失败"行(原因 SettlementHandlerFault、步骤是实际抛出的异常类型名),因为没人要求挂起,随后照常走通用的超时中止收尾:
        // 再写"离开(Timeout)"与"失败(Timeout/Combat)"两行,回城镇、记一次失败。整份日志序列逐行相等,收尾后不再多写。
        using var scene = new HandoffScene(timedOut: true, HandoffSubscriber.ThrowOnly, faultTypeName);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        Exception leaked = Record.Exception(() => rig.PumpUntil(() => scene.BackInTownAndIdle));
        Assert.Null(leaked);

        string[] expected = Lines(Opening, LeaveSettlementTimeout, HandlerFault(faultTypeName), LeaveTimeout, FailTimeoutCombat);
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.True(scene.Ready.Data.IsTimedOut);
        Assert.Equal(OnlineSettlementParseState.Succeeded, scene.Ready.Data.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, scene.Ready.Data.RewardParseState);
        Assert.False(scene.Ready.HoldForMenu);
        AssertGenericTimeoutAbortFinished(scene);

        // 收尾之后再轮询也不再写日志、不再触发订阅者
        rig.Step();
        rig.Step();
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("SyntheticSubscriberFault")]
    public void A_subscriber_that_holds_and_then_throws_on_a_timed_out_settlement_still_holds_the_flow_for_the_menu(string faultTypeName)
    {
        // 保护:超时路径上订阅者先要求挂起、再抛异常——异常被吞掉并写"订阅者失败"行(类型名逐字);挂起的要求仍然生效:
        // 控制器停在"挂起等结算"阶段、流程被拦着不退出副本、房间收尾闸门保持关闭,不走通用的超时中止(没有"离开(Timeout)"行、不记失败);
        // 订阅者之后做出选择才回城(记一次结算、原因 online settlement ReturnToTown),此后控制器不再写日志。
        // 注意:这是"现状行为"的钉子——真结算控制器的处理函数一进来就先要求挂起,若它随后在自己内部抛异常(例如宿主回调出错),
        // 流程会一直挂着、没有人来收尾(疑似缺陷,待作者定夺);若日后改成"抛异常即放弃挂起",本用例的挂起断言与日志序列要随之改写。
        using var scene = new HandoffScene(timedOut: true, HandoffSubscriber.HoldThenThrow, faultTypeName);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        OnlineDungeonRoom room = rig.LastRoom;
        Exception leaked = Record.Exception(() => rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement));
        Assert.Null(leaked);

        string[] expected = Lines(Opening, LeaveSettlementTimeout, HandlerFault(faultTypeName));
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.True(scene.Ready.HoldForMenu);
        Assert.True(scene.Ready.Data.IsTimedOut);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(room.CanFinish());
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);

        // 再过 600 毫秒逻辑时间:仍然挂着、没有多写日志、没有再触发订阅者
        for (int i = 0; i < 30; i++)
        {
            rig.Step(20);
        }
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Failures);
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
    public void A_timed_out_settlement_handler_that_holds_without_throwing_logs_only_the_settlement_timeout_leave_line()
    {
        // 保护:超时路径上订阅者正常接手并要求挂起——控制器只写一行"离开(SettlementTimeout,结果解析成功、奖励不适用)",
        // 没有"订阅者失败"行、也没有通用超时中止的两行;挂起期间与选择回城之后都不再多写。
        using var scene = new HandoffScene(timedOut: true, HandoffSubscriber.Hold);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement);

        string[] expected = Lines(Opening, LeaveSettlementTimeout);
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.True(scene.Ready.Data.IsTimedOut);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);

        for (int i = 0; i < 10; i++)
        {
            rig.Step(20);
        }
        Assert.Equal(expected, scene.Logs());

        scene.Ready.CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal("online settlement ReturnToTown", scene.FlowReasons().Last());
        Assert.Equal(expected, scene.Logs());
    }

    [Fact]
    public void A_timed_out_settlement_handler_that_neither_holds_nor_throws_falls_through_to_the_generic_timeout_abort_after_the_settlement_timeout_line()
    {
        // 保护:超时路径上订阅者收到了交接、但既没要求挂起也没抛——控制器已经写了"离开(SettlementTimeout)"一行,
        // 因为没人挂起,接着走通用的超时中止:"离开(Timeout)"与"失败(Timeout/Combat)"两行,回城镇、记一次失败;没有"订阅者失败"行。
        using var scene = new HandoffScene(timedOut: true, HandoffSubscriber.Plain);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        Exception leaked = Record.Exception(() => rig.PumpUntil(() => scene.BackInTownAndIdle));
        Assert.Null(leaked);

        string[] expected = Lines(Opening, LeaveSettlementTimeout, LeaveTimeout, FailTimeoutCombat);
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(1, scene.Invocations);
        Assert.True(scene.Ready.Data.IsTimedOut);
        AssertGenericTimeoutAbortFinished(scene);

        rig.Step();
        rig.Step();
        Assert.Equal(expected, scene.Logs());
    }

    [Fact]
    public void A_timed_out_settlement_without_any_subscriber_takes_the_generic_timeout_abort_without_the_settlement_timeout_line()
    {
        // 保护:超时路径上事件没有任何订阅者——控制器不走"结算通知超时"的交接(不写"离开(SettlementTimeout)"行、不造交接数据),
        // 直接走通用的超时中止:"离开(Timeout)"与"失败(Timeout/Combat)"两行,回城镇、记一次失败、亮"超时"提示。
        using var scene = new HandoffScene(timedOut: true, HandoffSubscriber.None);
        SettleRig rig = scene.Rig;

        scene.EnterAndKillEverything();
        Exception leaked = Record.Exception(() => rig.PumpUntil(() => scene.BackInTownAndIdle));
        Assert.Null(leaked);

        string[] expected = Lines(Opening, LeaveTimeout, FailTimeoutCombat);
        Assert.Equal(expected, scene.Logs());
        Assert.Equal(0, scene.Invocations);
        Assert.Null(scene.Ready);
        AssertGenericTimeoutAbortFinished(scene);

        rig.Step();
        rig.Step();
        Assert.Equal(expected, scene.Logs());
    }
}
