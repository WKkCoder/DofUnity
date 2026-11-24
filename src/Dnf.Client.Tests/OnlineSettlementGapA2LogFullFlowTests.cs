using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA2Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 带真联网进副本控制器 + 结算控制器的<b>整条流程</b>日志序列测试(<see cref="SettleRig"/>:两个控制器的日志按写出顺序交织在同一份里,逐行相等):
/// ① 结算通知超时(只回 0x22、等不到 0x23)那条路:进副本三行、"离开(结算通知超时)"行、超时的展示行与差异行、超时失败行、选回城、放弃命令确认后落到服务端区域;
/// ② 标准成功那条路:进副本三行、"离开(返回就绪)"行、展示行(掉落与差异个数)、差异行、72/73/74 推进、选回城、0/2 确认。
/// 这两条路上的数值全部来自夹具的合成数据与固定的控制流程,推导写在各测试里。
/// </summary>
public sealed class OnlineSettlementGapA2LogFullFlowTests
{
    // 进副本三行(两条路相同):
    //  select:副本权限表给该副本的权限档是 1 → 难度上限 1;选的难度 0。
    //  enter:夹具没给当前区域(种类写 unknown);英雄等级缺省 1,等级 ≤ 1 一律算入口区域 → entrance=true。
    //  room:一只怪、单房间 → 摆出 1 只、预期 1 只、进过 1 个房间。
    private const string SelectEnter = "DNF_ONLINE stage=dungeon event=select outcome=enter difficulty=0 max=1";
    private const string EnterLine = "DNF_ONLINE stage=dungeon event=enter areakind=unknown entrance=true difficulty=0";
    private const string RoomReady = "DNF_ONLINE stage=dungeon event=room status=Ready multi=off spawned=1 expected=1 rooms=1";

    // 差异行(两条路相同的两行):
    //  clear_time:服务端通关用时 41200(夹具 0x22 载荷)有效,本地用时是几十毫秒级的逻辑时间,大于 0 且不等于 41200 → 差异;
    //  kills:夹具用"直接致死"杀怪,不经过主角出招路径,本地击杀计数(只在主角出招击杀时 +1)是 0,联网上报数是 1 → 差异。
    private const string DiffClearTime = "DNF_ONLINE stage=settle event=diff field=clear_time";
    private const string DiffKills = "DNF_ONLINE stage=settle event=diff field=kills";

    [Fact]
    public void A_settlement_notice_timeout_flow_logs_the_dungeon_leave_the_timed_out_menu_and_the_give_up_return_end_to_end()
    {
        // 保护:联网结算只收到第一个通知(0x22)、等不到第二个(0x23)到点时——联网进副本控制器写"离开、原因 SettlementTimeout、
        // 上报 1 / 丢弃 0 / 房间 1、结果解析成功、奖励不适用"一行,随后结算控制器写"超时的展示行(结果解析成功、奖励不适用、0 个掉落、2 项差异)"、
        // 两行差异行与"结算通知超时"失败行;菜单只开放回城、不发任何 1/72..1/75;选回城后发一次 1/45(放弃、空载荷),
        // 服务端推 0/23(自己)+ 0/24 后写"选择行、回城行(原因 confirmed、落点 server_area、信号 area_pair)"并落到服务端给的区域(1,2)。
        using var rig = new SettleRig(
            configureSettle: o => o.TownReturnWaitTimeoutMs = 100,
            configureDungeon: d => d.CombatOptions = new OnlineDungeonCombatOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettlementTimeout = TimeSpan.FromMilliseconds(80),
            });
        WireStandardDungeonResponder(rig, monsterCount: 1, omitSettlementB: true);   // 只回 0x22,不回 0x23
        rig.Transport.SetSendHandler((id, _) =>
        {
            if (id == SessionLifecyclePayloads.GiveUpGamePacketId)
            {
                rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200));
                rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuDegraded);

        string[] atMenu =
        {
            SelectEnter,
            EnterLine,
            RoomReady,
            "DNF_ONLINE stage=dungeon event=leave reason=SettlementTimeout reported=1 dropped=0 rooms=1 result=Succeeded reward=NotApplicable",
            "DNF_ONLINE stage=settle event=show timedout=true result=Succeeded reward=NotApplicable drops=0 diffs=2",
            DiffClearTime,
            DiffKills,
            TimeoutNotice,
        };
        Assert.Equal(atMenu, rig.LogSnapshot());
        Assert.Empty(rig.Transport.Sent);
        Assert.True(rig.SettleCtrl.Summary.IsTimedOut);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
        string[] expected = Seq(
            atMenu,
            ChooseReturnByPlayer,
            "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair");
        Assert.Equal(expected, rig.LogSnapshot());

        ushort[] sent = rig.Transport.Sent.Select(s => s.PacketId).ToArray();
        Assert.Equal(new ushort[] { 45 }, sent);
        Assert.Empty(rig.Transport.Sent[0].Payload);
        OnlineSettlementReturnTarget target = Assert.Single(rig.AppliedReturnTargets);
        Assert.Equal(OnlineSettlementReturnSource.ServerUserArea, target.Source);
        Assert.Equal(new TownPlace(1, 2, TownEntry.Return), target.Place);

        // 流程落回城镇之后不再多写日志
        rig.Step();
        rig.Step();
        Assert.Equal(expected, rig.LogSnapshot());
    }

    [Fact]
    public void A_standard_settlement_flow_logs_the_dungeon_leave_the_summary_the_advance_steps_and_the_confirmed_return_end_to_end()
    {
        // 保护:标准成功的联网结算整条流程。联网进副本控制器写"离开、原因 ReturnReady、上报 1 / 丢弃 0 / 房间 1、结果与奖励都解析成功";
        // 结算控制器写展示行:掉落 2 个(夹具 0x23 里 1 个任务物品 + 1 个有物品的奖励槽)、差异 4 项,
        // 差异行依次为通关用时、击杀(同超时那条路)、基础经验、评分加成经验(本地击杀为 0,本地通关经验按击杀折算也不是服务端合成值 700 / 150);
        // 奖励金币没有差异行(本地没有拾取金币,不比较);之后 72/73/74 推进六行、玩家选回城、0/2 确认后回城(原因 confirmed、落点进副本前、信号 noti2)。
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1);
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72)
            {
                rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == 73)
            {
                rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            }
            else if (id == 75)
            {
                ConfirmWithNoti2(rig.Transport, payload);
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);

        string[] expected = Seq(
            SelectEnter,
            EnterLine,
            RoomReady,
            "DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=1 dropped=0 rooms=1 result=Succeeded reward=Succeeded",
            "DNF_ONLINE stage=settle event=show timedout=false result=Succeeded reward=Succeeded drops=2 diffs=4",
            DiffClearTime,
            DiffKills,
            "DNF_ONLINE stage=settle event=diff field=base_exp",
            "DNF_ONLINE stage=settle event=diff field=rank_bonus_exp",
            Sent72,
            Ok72,
            Sent73,
            Ok73,
            Sent74,
            Ok74,
            ChooseReturnByPlayer,
            TownConfirmedByNoti2);
        Assert.Equal(expected, rig.LogSnapshot());
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, rig.Transport.Sent.Select(s => s.PacketId).ToArray());
        OnlineSettlementReturnTarget target = Assert.Single(rig.AppliedReturnTargets);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);

        rig.Step();
        rig.Step();
        Assert.Equal(expected, rig.LogSnapshot());
    }
}
