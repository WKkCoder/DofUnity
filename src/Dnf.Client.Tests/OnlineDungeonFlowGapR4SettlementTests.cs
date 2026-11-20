using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网副本收到两个结算通知之后控制器的交接:把原始会话交给结算处理器、处理器要求挂起(等菜单)时房间不许收尾、
/// 没挂起时控制器停在"结算退出"阶段直到流程离开副本,以及结算选择在这两个阶段里都被接受。
/// 全部用探针场景台(逻辑时钟轮询);房间的收尾闸门用公开的 <c>CanFinish</c> 直接探。
/// </summary>
public sealed class OnlineDungeonFlowGapR4SettlementTests
{
    private static void PumpRounds(Rig4 rig, int rounds)
    {
        int n = 0;
        rig.PumpUntil(() => n++ >= rounds);
    }

    [Fact]
    public void A_settlement_handler_that_asks_to_hold_gets_the_raw_session_and_keeps_the_room_from_finishing_until_it_chooses()
    {
        // 保护:结算交接时处理器拿到的就是端口的原始会话(不是空);处理器要求挂起时,控制器停在"挂起等结算"阶段、
        // 流程被拦着不退出副本、房间的收尾闸门保持关闭(多轮轮询之后依然关着);做了选择之后才回城,并撤掉闸门与拦截。
        OnlineClientSession raw = SessionProbe.NewUnconnected();
        using var rig = new Rig4(rawSession: raw);
        ServeSettlementDungeon(rig, 2, returnDelayMs: 0.0);
        OnlineDungeonSettlementReady ready = null;
        rig.Ctrl.SettlementStarted += r =>
        {
            r.HoldForMenu = true;
            ready = r;
        };
        EnterDungeon(rig);
        OnlineDungeonRoom room = rig.LastRoom;
        Assert.NotNull(room.CanFinish);
        Assert.False(room.CanFinish());             // 独占收包权还没放回,闸门是关的

        KillAll(rig);
        rig.PumpUntil(() => ready != null);

        Assert.Same(raw, ready.Session);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.Ctrl.Phase);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(room.CanFinish());             // 放权已完成,但处理器要等菜单:闸门仍关
        PumpRounds(rig, 15);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.Ctrl.Phase);
        Assert.False(room.CanFinish());
        Assert.Equal(1, rig.Ctrl.LeaveExclusiveCount);
        Assert.Contains(P + "event=leave reason=ReturnReady reported=2 dropped=0 rooms=1 result=Succeeded reward=Succeeded", rig.Logs());

        ready.CompleteChoice(SettlementChoice.ReturnToTown);

        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal("online settlement ReturnToTown", Reasons(rig.Flow).Last());
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Null(room.CanFinish);
        Assert.Equal(0, rig.Disconnects);
    }

    [Fact]
    public void A_settlement_without_a_hold_waits_in_the_settling_exit_phase_and_accepts_the_choice_made_meanwhile()
    {
        // 保护:处理器不要求挂起时,闸门放开,控制器停在"结算退出"阶段、不提前回到空闲(流程还在副本里时轮询多少圈都不变);
        // 这个阶段里做出的结算选择有效(回城、记一次结算、控制器回空闲),再次选择被忽略(不会多记一次)。
        OnlineClientSession raw = SessionProbe.NewUnconnected();
        using var rig = new Rig4(rawSession: raw);
        ServeSettlementDungeon(rig, 2, returnDelayMs: 600000.0);
        OnlineDungeonSettlementReady ready = null;
        rig.Ctrl.SettlementStarted += r => ready = r;
        EnterDungeon(rig);
        OnlineDungeonRoom room = rig.LastRoom;

        KillAll(rig);
        rig.PumpUntil(() => ready != null);

        Assert.Same(raw, ready.Session);
        Assert.False(ready.HoldForMenu);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.Ctrl.Phase);
        Assert.True(room.CanFinish());
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        PumpRounds(rig, 15);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.Ctrl.Phase);

        ready.CompleteChoice(SettlementChoice.ReturnToTown);

        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal("online settlement ReturnToTown", Reasons(rig.Flow).Last());
        Assert.Null(room.CanFinish);

        ready.CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
    }

    [Fact]
    public void The_settling_exit_phase_ends_only_after_the_flow_has_left_the_dungeon_by_itself()
    {
        // 保护:没有挂起也没有选择时,房间在返回延迟(400 毫秒,逻辑时钟约 20 圈)到点后让流程自己回城;
        // 控制器要等到流程真的离开副本之后的下一次轮询才回空闲(离开那一刻它仍是"结算退出"),之后处理器迟到的选择不再生效。
        using var rig = new Rig4();
        ServeSettlementDungeon(rig, 2, returnDelayMs: 400.0);
        OnlineDungeonSettlementReady ready = null;
        rig.Ctrl.SettlementStarted += r => ready = r;
        EnterDungeon(rig);

        KillAll(rig);
        rig.PumpUntil(() => ready != null);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.Ctrl.Phase);

        rig.PumpUntil(() => rig.Flow.Stage != GameStage.Dungeon);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.Ctrl.Phase);   // 流程刚走,控制器还没轮询到
        int settlementsBefore = rig.Flow.Settlements;

        rig.NowMs += 20;
        rig.Ctrl.Poll(rig.NowMs);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);

        ready.CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal(settlementsBefore, rig.Flow.Settlements);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(0, rig.Disconnects);
    }
}
