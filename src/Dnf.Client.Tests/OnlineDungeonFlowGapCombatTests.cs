using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器在"副本里"的收尾:战斗整段超时、房间失败时战斗还没停、主动放弃回城(返回区域的判定、超时提示)。
/// 全部用场景台的逻辑时钟轮询;真正的墙钟只出现在被测行为本身的超时窗口(战斗整段超时、放弃等回包)里。
/// </summary>
public sealed class OnlineDungeonFlowGapCombatTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";

    private static string[] Reasons(GameFlow flow)
    {
        return flow.History.Select(h => h.Reason).ToArray();
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

    private static string[] EnteredLogs()
    {
        // 进副本成功、单房间(2 只怪全对上)的三行起手日志;难度取选择屏的缺省 0,该副本的档位上限是 3
        return new[]
        {
            P + "event=select outcome=enter difficulty=0 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=0",
            P + "event=room status=Ready multi=off spawned=2 expected=2 rooms=1",
        };
    }

    [Fact]
    public void A_combat_that_runs_out_of_time_ends_as_an_abort_with_the_timeout_reason_and_a_short_lived_town_notice()
    {
        // 保护:联网战斗整段超时既不是通关结算,也不是放弃回城,而是按"中止"收尾——
        // 离场与失败日志的原因都写 Timeout,流程记一次失败回城(原因 "online abort Timeout"),城镇上亮一条 Timeout 提示,
        // 提示的时长取配置值(这里配 1ms;配置为 0 或未配才回退到 4000ms),到点后提示消失;只放权一次。
        using var rig = new Rig(o =>
        {
            o.CombatOptions = CombatKit.Options(pollMilliseconds: 10, overallSeconds: 0.2);
            o.TransientNoticeDurationMs = 1;
        });
        ServeRoomDungeon(rig);
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);

        // 只轮询控制器:房间每步的检查会把"战斗已经收场"判成网络中止并抢在控制器之前,这里要的是"战斗任务先收场"这个顺序
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        string[] expected = EnteredLogs().Concat(new[]
        {
            // 手算:没有任何击杀,reported=0 dropped=0;只进过起始房间,rooms=1
            P + "event=leave reason=Timeout reported=0 dropped=0 rooms=1",
            P + "event=fail reason=Timeout step=Combat",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal(0, rig.Flow.GiveUps);
        Assert.Equal("online abort Timeout", Reasons(rig.Flow).Last());
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Disconnects);

        DungeonSelectNotice notice = rig.Ctrl.ActiveNotice;
        Assert.NotNull(notice);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, notice.Kind);
        Assert.Equal(rig.NowMs + 1, notice.ExpiresAtMs);
        rig.Ctrl.Poll(rig.NowMs + 1);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void A_failed_room_waits_for_the_combat_to_stop_and_then_reports_the_combat_s_real_ending()
    {
        // 保护:房间失败(复活倒计时走完)而战斗任务还没停时,控制器先进"停战等待"阶段、不放权;
        // 战斗收场后把它的结果记下来,离场与失败日志写战斗的真实结局(这里是被取消),不是"没有结果"的占位词 Aborted;
        // 提示是 Aborted(不是 Timeout),流程记一次失败回城。
        using var rig = new Rig();
        ServeRoomDungeon(rig);
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        // 把战斗循环停在收帧闸门上(闸门不看取消令牌):取消之后战斗任务也不会就地收场,"停战中"这个阶段才看得见
        ScriptedNetSession net = rig.Port.CurrentNet;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        net.ReceiveGate = gate;
        Assert.True(net.WaitForReceiveGate(10000), "the combat loop never reached the receive gate");

        // 主角倒下、复活倒计时走完:房间"失败"但不是被宿主放弃
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        run.Room.Revive = new HeroRevive(null, new ReviveCoins(0));
        run.Room.Revive.Begin();
        run.Room.Revive.Tick(HeroRevive.CountdownMs);
        Assert.True(run.Failed);
        Assert.False(run.Aborted);

        rig.Ctrl.Poll(rig.NowMs);
        Assert.Equal(OnlineDungeonFlowPhase.StoppingCombatBeforeRelease, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Port.LeaveExclusiveCalls);
        rig.Ctrl.Poll(rig.NowMs);
        Assert.Equal(OnlineDungeonFlowPhase.StoppingCombatBeforeRelease, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Port.LeaveExclusiveCalls);

        gate.SetResult(true);
        rig.PumpUntilIdle();

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Cancelled reported=0 dropped=0 rooms=1",
            P + "event=fail reason=Cancelled step=Combat",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal("online abort Cancelled", Reasons(rig.Flow).Last());
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(DungeonSelectNoticeKind.Aborted, rig.Ctrl.ActiveNotice?.Kind);
    }

    [Theory]
    // 图册版:返回位置 (1,1) 映射成线上 village/area = 1/1(图册里登记了 1 号城镇的 1、2 号区域)
    [InlineData(true, 1, 1, 1, 1, "match")]
    [InlineData(true, 1, 1, 1, 2, "mismatch")]
    [InlineData(true, 1, 9, 1, 9, "unknown")]
    // 无图册版:直接把城镇号/区域号当 byte 比对;0 与 255 是合法边界,越界一律 unknown(不能截断成 byte 去碰巧对上)
    [InlineData(false, 0, 0, 0, 0, "match")]
    [InlineData(false, 255, 255, 255, 255, "match")]
    [InlineData(false, 1, 1, 2, 1, "mismatch")]
    [InlineData(false, 300, 1, 44, 1, "unknown")]
    [InlineData(false, 1, -1, 1, 255, "unknown")]
    [InlineData(false, 1, 256, 1, 0, "unknown")]
    [InlineData(false, -1, 1, 255, 1, "unknown")]
    public void Giving_up_classifies_the_area_the_server_returns_us_to_against_the_expected_return_place(
        bool useAtlas, int town, int area, int replyVillage, int replyArea, string expectedTag)
    {
        // 保护:放弃回城收到服务端回的"用户所在区域"(0/23)后,按期望的返回位置判 match / mismatch / unknown 并写进日志;
        // 离场原因写战斗的真实结局 GaveUp,计一次放弃(不是失败),没有超时就不亮瞬态提示。
        // 手算:图册版 TryMapToServerArea 是恒等映射(要求该区域在图册里登记过);无图册版要求 0..255 的整数,否则 unknown。
        // 例如 (300,1) 截断成 byte 是 (44,1),与服务端回的 (44,1) 凑巧相同,但越界必须判 unknown。
        using var rig = new Rig(o =>
        {
            o.ReturnPlace = new TownPlace(town, area);
            o.Atlas = useAtlas ? AtlasWith(1, 1, 2) : null;
            o.CombatOptions = GiveUpOptions(5000);
        });
        ServeRoomDungeon(rig, 2, _ => new[]
        {
            new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes((byte)replyVillage, (byte)replyArea)),
            new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes((byte)replyVillage, (byte)replyArea)),
        });
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        rig.Ctrl.RequestGiveUpDungeon();
        Assert.Equal(OnlineDungeonFlowPhase.GivingUp, rig.Ctrl.Phase);
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=GaveUp reported=0 dropped=0 rooms=1",
            P + "event=return area=" + expectedTag,
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Flow.GiveUps);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("online giveup", Reasons(rig.Flow).Last());
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void A_give_up_the_server_never_answers_ends_as_a_timeout_with_an_unknown_area_and_a_notice_that_expires_on_schedule()
    {
        // 保护:放弃回城发出去之后服务端不回(等满放弃超时):仍然算放弃回城,离场原因写 Timeout,返回区域 unknown,
        // 回城后亮一条 Timeout 提示;提示时长取配置值(1ms,不是回退用的 4000ms),到点后消失。
        using var rig = new Rig(o =>
        {
            o.ReturnPlace = new TownPlace(ReturnTownId, ReturnAreaId);
            o.TransientNoticeDurationMs = 1;
            o.CombatOptions = GiveUpOptions(80);
        });
        ServeRoomDungeon(rig);
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        rig.Ctrl.RequestGiveUpDungeon();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Timeout reported=0 dropped=0 rooms=1",
            P + "event=return area=unknown",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Flow.GiveUps);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("online giveup", Reasons(rig.Flow).Last());

        DungeonSelectNotice notice = rig.Ctrl.ActiveNotice;
        Assert.NotNull(notice);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, notice.Kind);
        Assert.Equal(rig.NowMs + 1, notice.ExpiresAtMs);
        rig.Ctrl.Poll(rig.NowMs + 1);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }
}
