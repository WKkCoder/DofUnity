using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器在"副本里"各种收场的归类:战斗跑器自己以放弃回城收场、端口只置断线标志、战斗因连接问题(对端关闭、切帧出错、发送失败)结束
/// (副本里与放弃等回包时各一遍)、房间被宿主中止、建房结果的房间没造好,以及提示的生命周期。全部用探针场景台的逻辑时钟轮询;
/// 真正的墙钟只出现在战斗循环的轮询窗口里(10 毫秒)。
/// </summary>
public sealed class OnlineDungeonFlowGapR4CombatEndTests
{
    public enum EndPhase
    {
        InDungeon,
        GivingUp,
    }

    public enum CombatEnd
    {
        PeerClosed,
        FramingFault,
        SendFailed,
    }

    private static OnlineDungeonCombatOptions GiveUpOptions(int giveUpTimeoutMs)
    {
        return new OnlineDungeonCombatOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            GiveUpTimeout = TimeSpan.FromMilliseconds(giveUpTimeoutMs),
            SettlementTimeout = TimeSpan.FromSeconds(30),
        };
    }

    [Fact]
    public void A_combat_that_gives_up_on_its_own_while_the_controller_is_in_the_dungeon_completes_as_a_give_up_and_not_an_abort()
    {
        // 保护:战斗跑器自己以"已放弃回城"收场(这里由宿主绕过控制器直接叫跑器放弃,控制器还停在副本里)时,控制器按放弃回城收尾——
        // 离场原因 GaveUp、返回区域一行、记一次放弃回城——而不是当成战斗中止(没有失败日志、不记失败、不弹提示)。
        using var rig = new Rig4(o => o.CombatOptions = GiveUpOptions(5000));
        ServeRoomDungeon(rig, 2, _ => TownFrames());
        EnterDungeon(rig);
        Assert.Equal(OnlineDungeonFlowPhase.InDungeon, rig.Ctrl.Phase);

        Assert.True(rig.LastContext.CombatRunner.PostGiveUp());
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=GaveUp reported=0 dropped=0 rooms=1",
            P + "event=return area=unknown",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.GiveUps);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("online giveup", Reasons(rig.Flow).Last());
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(0, rig.Disconnects);
        Assert.Equal(1, rig.Ctrl.LeaveExclusiveCount);
    }

    [Fact]
    public void A_port_that_only_raises_its_disconnected_flag_ends_the_run_in_the_dungeon_as_a_disconnect_cleanup()
    {
        // 保护:副本里端口说"已断线"(只置标志,没有断线事件、会话也没关)时,控制器自己发现:停战、放权、写离场与失败日志(原因 Disconnected、步骤 Session),
        // 通知宿主一次断线清理,而不是继续傻等;流程不被告知失败(宿主负责收尾)。放权这一步是成功的,放权计数加一。
        using var rig = new Rig4();
        ServeRoomDungeon(rig);
        EnterDungeon(rig);

        rig.Port.IsDisconnected = true;
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Disconnected reported=0 dropped=0 rooms=1",
            P + "event=fail reason=Disconnected step=Session",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Disconnects);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(1, rig.Ctrl.LeaveExclusiveCount);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Theory]
    [InlineData(EndPhase.InDungeon, CombatEnd.PeerClosed)]
    [InlineData(EndPhase.InDungeon, CombatEnd.FramingFault)]
    [InlineData(EndPhase.InDungeon, CombatEnd.SendFailed)]
    [InlineData(EndPhase.GivingUp, CombatEnd.PeerClosed)]
    [InlineData(EndPhase.GivingUp, CombatEnd.FramingFault)]
    [InlineData(EndPhase.GivingUp, CombatEnd.SendFailed)]
    public void A_combat_that_ends_because_the_connection_failed_is_a_disconnect_cleanup_in_the_dungeon_and_while_giving_up(EndPhase phase, CombatEnd end)
    {
        // 保护:战斗因连接问题收场——对端关闭、切帧出错、发送被帧层拒绝(这三种)——不管发生在副本里还是在放弃回城等回包时,
        // 都按断线处理:离场与失败日志写 Disconnected/Session,通知宿主一次断线清理,不记失败也不记放弃回城(流程原地不动),不弹提示。
        // 放弃等回包时那一路不能被当成"放弃回城完成"(那样会写战斗的真实结局、记一次放弃)。
        bool failSends = false;
        using var rig = new Rig4(o => o.CombatOptions = GiveUpOptions(5000));
        rig.Port.SendStatus = _ => failSends ? FrameStatus.NoCipher : FrameStatus.Ok;
        ServeRoomDungeon(rig);
        EnterDungeon(rig);

        if (phase == EndPhase.GivingUp)
        {
            failSends = end == CombatEnd.SendFailed;     // 发送失败那一行:放弃命令(1/45)本身发不出去
            rig.Ctrl.RequestGiveUpDungeon();
            Assert.Equal(OnlineDungeonFlowPhase.GivingUp, rig.Ctrl.Phase);
            if (end != CombatEnd.SendFailed)
            {
                // 先等放弃命令真的发出去(不然关会话会让这条发送抛 IO 异常,战斗以"出错"而不是"连接断开"收场)
                rig.PumpControllerUntil(() => rig.Port.Commands.Any(c => c.Class == PacketClass.Cmd && c.PacketId == SessionLifecyclePayloads.GiveUpGamePacketId), 600);
            }
        }

        switch (end)
        {
            case CombatEnd.PeerClosed:
                rig.Port.CurrentNet.Inner.ClosePeer();
                break;
            case CombatEnd.FramingFault:
                rig.Port.CurrentNet.Inner.FaultFraming();
                break;
            default:
                if (phase == EndPhase.InDungeon)
                {
                    failSends = true;
                    Assert.True(rig.LastContext.CombatRunner.PostKill(0, 0));     // 击杀命令发不出去
                }
                break;
        }
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Disconnected reported=0 dropped=0 rooms=1",
            P + "event=fail reason=Disconnected step=Session",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Disconnects);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal(0, rig.Flow.GiveUps);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
    }

    [Fact]
    public void A_room_aborted_by_the_host_leaves_with_the_abort_reason_and_records_a_failed_return_to_town()
    {
        // 保护:宿主把房间中止(这里是换房被拒够次数)时,控制器停战、放权,离场与失败日志写的是中止原因本身(MoveRefused),
        // 不是占位的 None 或战斗的结局;流程记一次失败回城(原因 "online abort MoveRefused"),城镇上亮一条 Aborted 提示。
        using var rig = new Rig4();
        ServeRoomDungeon(rig);
        EnterDungeon(rig);

        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        run.Abort(HostedAbort.MoveRefused);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=MoveRefused reported=0 dropped=0 rooms=1",
            P + "event=fail reason=MoveRefused step=Combat",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal("online abort MoveRefused", Reasons(rig.Flow).Last());
        Assert.Equal(DungeonSelectNoticeKind.Aborted, rig.Ctrl.ActiveNotice?.Kind);
        Assert.Equal(0, rig.Disconnects);
    }

    [Fact]
    public void A_build_result_whose_room_never_became_ready_is_a_room_build_failure_and_not_a_dungeon_entry()
    {
        // 保护:建房工厂交回"成功"结果、但里面的房间状态不是 Ready(这里是一间根本没造过的房间,配着另一间房造出来的一局)时,
        // 控制器不进副本:写房间状态行(NotReady)与失败行(RoomBuildFailed/NotReady),停战放权后回选择屏,提示房间失败。
        using var rig = new Rig4();
        ServeRoomDungeon(rig);
        rig.RoomBuilder = ctx =>
        {
            var built = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = built.CreateRun(plan => rig.Fixture.Create(Templates(2), plan, Array.Empty<GateSide>()));
            var neverBuilt = new OnlineDungeonRoom(ctx.CombatRunner);
            Assert.Equal(OnlineRoomStatus.NotReady, neverBuilt.Status);
            return OnlineDungeonRoomBuildResult.Success(neverBuilt, run);
        };
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);

        string[] expected = EnteredLogs().Take(2).Concat(new[]
        {
            P + "event=room status=NotReady multi=off spawned=0 expected=0 rooms=1",
            P + "event=fail reason=RoomBuildFailed step=NotReady",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal("online room build failed", Reasons(rig.Flow).Last());
        Assert.Equal(DungeonSelectNoticeKind.RoomFailed, rig.Flow.Select.Notice?.Kind);
        Assert.Equal(1, rig.BuilderCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
    }

    [Fact]
    public void The_entering_notice_shows_while_the_entry_is_pending_and_is_gone_once_the_dungeon_is_entered()
    {
        // 保护:没有加载画面的流程里,进副本过程中控制器亮一条 Entering 提示(选择屏上的"正在进入");
        // 进了副本(建房成功、阶段变成 InDungeon)之后这条提示立刻撤掉,不残留到副本里。
        using var rig = new Rig4();
        ServeRoomDungeon(rig);
        rig.Start();
        rig.ConfirmSelected();
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.Equal(DungeonSelectNoticeKind.Entering, rig.Ctrl.ActiveNotice?.Kind);

        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void A_transient_town_notice_without_a_positive_configured_duration_lasts_four_seconds_exactly(long configured)
    {
        // 保护:瞬态城镇提示的时长取配置值;配置为 0 或负数(没有配)就回退到 4000 毫秒——
        // 到点前一毫秒(3999)还在,正好到点(4000)才消失。手算:提示是在逻辑时刻 NowMs 写下的,到期时刻 = NowMs + 4000。
        using var rig = new Rig4(o =>
        {
            o.CombatOptions = CombatKit.Options(pollMilliseconds: 10, overallSeconds: 0.2);
            o.TransientNoticeDurationMs = configured;
        });
        ServeRoomDungeon(rig);
        EnterDungeon(rig);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);

        DungeonSelectNotice notice = rig.Ctrl.ActiveNotice;
        Assert.NotNull(notice);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, notice.Kind);
        Assert.Equal(rig.NowMs + 4000, notice.ExpiresAtMs);

        rig.Ctrl.Poll(rig.NowMs + 3999);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.Ctrl.ActiveNotice?.Kind);
        rig.Ctrl.Poll(rig.NowMs + 4000);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }
}
