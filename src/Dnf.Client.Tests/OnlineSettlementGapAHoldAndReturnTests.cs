using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算停留与回城确认的三组缺口测试:
/// ① "结算通知超时"路径上处理器要求挂起时,房间收尾闸门、流程"拦着不退出副本"标志都必须保持(与正常结算路径同一承诺);
/// ② 结算开始时流程里没有联网房间局(城镇阶段、或副本阶段但局会话不是联网房间局)也要完整走完菜单、不抛异常;
/// ③ 服务端回城信号里 0/23 载荷损坏、再收到 0/24 成对时,仍判定回城完成并落回进副本前的城镇区。
/// 第 ① 组用带真联网进副本控制器的 <see cref="SettleRig"/>(逻辑时钟轮询,不挂结算控制器,订阅者自己要求挂起),②③ 用 <see cref="DirectRig"/>。
/// </summary>
public sealed class OnlineSettlementGapAHoldAndReturnTests
{
    private static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    [Fact]
    public void A_settlement_notice_timeout_that_the_handler_holds_keeps_the_room_gate_closed_and_the_flow_held_until_the_choice()
    {
        // 保护:结算通知超时(只收到第一个结算通知、第二个等不到)时,若订阅者要求挂起等菜单——
        // 控制器停在"挂起等结算"阶段;流程"拦着不退出副本"标志为真;房间的收尾闸门(CanFinish)保持关闭,不管过去多久都不放行;
        // 订阅者做出选择之后才回城,并撤掉拦截与闸门。数据里标明是超时那一种。
        using var rig = new SettleRig(
            attachSettlement: false,
            configureDungeon: d => d.CombatOptions = new OnlineDungeonCombatOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettlementTimeout = TimeSpan.FromMilliseconds(80),
            });
        WireStandardDungeonResponder(rig, monsterCount: 1, omitSettlementB: true);   // 只回 0x22,不回 0x23
        OnlineDungeonSettlementReady ready = null;
        int started = 0;
        rig.DungeonCtrl.SettlementStarted += r =>
        {
            started++;
            r.HoldForMenu = true;
            ready = r;
        };

        rig.EnterTownAndDungeon();
        OnlineDungeonRoom room = rig.LastRoom;
        Assert.NotNull(room.CanFinish);
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement);

        Assert.Equal(1, started);
        Assert.NotNull(ready);
        Assert.True(ready.Data.IsTimedOut);
        Assert.True(ready.HoldForMenu);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(room.CanFinish());

        // 再过 600 毫秒逻辑时间(30 圈 × 20 毫秒):仍然拦着、闸门仍然关着、没有再触发结算
        for (int i = 0; i < 30; i++)
        {
            rig.Step(20);
        }
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(room.CanFinish());
        Assert.Equal(1, started);
        Assert.Equal(0, rig.Flow.Settlements);
        Assert.Equal(0, rig.Disconnects);

        // 订阅者选择回城:记一次结算、回城镇、撤掉拦截与闸门
        ready.CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Null(room.CanFinish);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_settlement_started_without_a_hosted_room_run_still_walks_the_whole_menu_flow(bool inDungeonWithForeignRun)
    {
        // 保护:结算开始时流程里取不到联网房间局(城镇阶段没有局会话;或副本阶段但局会话不是联网房间局)——
        // 控制器不碰房间相关的叠层与营地,不抛异常,照常发 72/73、进菜单、选回城、落回进副本前的城镇区并交还选择。
        using var rig = new DirectRig();
        if (inDungeonWithForeignRun)
        {
            rig.Flow.StartDungeon("dungeon/test/dunga.dgn");
            Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
            Assert.IsType<OtherRun>(rig.Flow.Run);
        }
        else
        {
            Assert.Equal(GameStage.Town, rig.Flow.Stage);
            Assert.Null(rig.Flow.Run);
        }
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            byte cmd = payload[1];
            rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(cmd));
            PushNoti2(rig.Transport);
        });

        rig.PollAt(1000);
        rig.StartSettlement();
        Assert.True(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72 }, SentIds(rig));

        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        Assert.Equal(0, rig.Disconnects);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();

        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        OnlineSettlementReturnTarget target = Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
        Assert.Equal(new TownPlace(1, 3, TownEntry.Return), target.Place);
        Assert.Equal(1, rig.LogCount(P + " event=town reason=confirmed area=pre_dungeon signal=noti2"));
    }

    [Fact]
    public void A_corrupted_user_area_notice_still_counts_towards_the_return_pair_and_falls_back_to_the_pre_dungeon_area()
    {
        // 保护:回城确认要 0/23 与 0/24 成对;0/23 载荷损坏(解析失败)也算"收到了 0/23"——
        // 只有损坏的 0/23 时仍在等;随后 0/24 到达就判定回城完成,信号记为成对(area_pair),落点退回进副本前的城镇区(来源 PreDungeonArea);
        // 回城等待超时设得很长,保证是"成对确认"完成的、不是超时兜底。
        using var rig = new DirectRig(o => o.TownReturnWaitTimeoutMs = 100000);
        AutoAdvance(rig.Transport, CardRightBytes(1), payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1])));
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);   // 1/75 成功回复:等服务端回城信号

        // 只来了损坏的 0/23(2 字节,长度不对):还不算回城完成
        rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, new byte[] { 1, 2 });
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Targets);

        // 0/24 到了:成对,回城完成
        rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
        rig.Poll();

        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        OnlineSettlementReturnTarget target = Assert.Single(rig.Targets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
        Assert.Equal(new TownPlace(1, 3, TownEntry.Return), target.Place);
        Assert.Equal(1, rig.LogCount(P + " event=town reason=confirmed area=pre_dungeon signal=area_pair"));
        Assert.Equal(0, rig.Disconnects);
    }
}
