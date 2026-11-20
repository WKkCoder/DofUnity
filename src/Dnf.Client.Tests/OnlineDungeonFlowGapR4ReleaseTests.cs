using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器"放权(离开独占收包)失败"的收尾,以及独占收包的进出计数:五种退出(进副本失败、建房失败、放弃回城、战斗中止、结算)
/// × 三类放权异常(对象已释放、非法操作、其他异常),全部用场景台的逻辑时钟轮询。
/// </summary>
public sealed class OnlineDungeonFlowGapR4ReleaseTests
{
    public enum ExitKind
    {
        EntryTimeout,
        RoomBuildFailed,
        GiveUp,
        CombatTimeout,
        Settlement,
    }

    private static Exception Fault(string name)
    {
        switch (name)
        {
            case "Disposed":
                return new ObjectDisposedException("OnlineClientSession");
            case "InvalidOperation":
                return new InvalidOperationException("synthetic");
            default:
                return new IOException("synthetic");
        }
    }

    [Theory]
    [InlineData(ExitKind.EntryTimeout, "Disposed")]
    [InlineData(ExitKind.EntryTimeout, "InvalidOperation")]
    [InlineData(ExitKind.EntryTimeout, "Other")]
    [InlineData(ExitKind.RoomBuildFailed, "Disposed")]
    [InlineData(ExitKind.RoomBuildFailed, "InvalidOperation")]
    [InlineData(ExitKind.RoomBuildFailed, "Other")]
    [InlineData(ExitKind.GiveUp, "Disposed")]
    [InlineData(ExitKind.GiveUp, "InvalidOperation")]
    [InlineData(ExitKind.GiveUp, "Other")]
    [InlineData(ExitKind.CombatTimeout, "Disposed")]
    [InlineData(ExitKind.CombatTimeout, "InvalidOperation")]
    [InlineData(ExitKind.CombatTimeout, "Other")]
    [InlineData(ExitKind.Settlement, "Disposed")]
    [InlineData(ExitKind.Settlement, "InvalidOperation")]
    [InlineData(ExitKind.Settlement, "Other")]
    public void A_failed_release_of_the_receive_right_turns_any_exit_into_a_disconnect_cleanup_instead_of_its_normal_completion(ExitKind kind, string faultKind)
    {
        // 保护:离开独占收包失败(对象已释放、非法操作、别的异常,一律算"没放成")时,五种退出都不能走各自的正常收尾(不回流程、不记失败/放弃/结算、
        // 不弹提示),而是只通知一次宿主断线清理,控制器回到 Idle,放权计数不加;只有"别的异常"才多写一行 LeaveExclusiveFault 日志,
        // 前两类是静默的。进入计数照常是 1(进入是成功的)。
        int leaves = 0;
        int settlements = 0;
        using var rig = new Rig(o =>
        {
            if (kind == ExitKind.CombatTimeout)
            {
                o.CombatOptions = CombatKit.Options(pollMilliseconds: 10, overallSeconds: 0.2);
            }
        });
        rig.Ctrl.SettlementStarted += _ => settlements++;
        Exception fault = Fault(faultKind);
        rig.Port.LeaveExclusiveBehavior = _ =>
        {
            leaves++;
            return Task.FromException(fault);
        };

        switch (kind)
        {
            case ExitKind.EntryTimeout:
                rig.Port.SetResponder((cls, id, payload) => Array.Empty<CombatKit.ServerFrame>());   // 什么都不回:装载通知等不到,超时
                rig.Start();
                rig.ConfirmSelected();
                rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);   // 只轮询控制器:选择屏没被流程复位,再轮询流程会立刻重新进一次
                break;
            case ExitKind.RoomBuildFailed:
                rig.Port.SetResponder((cls, id, payload) => EntryReply(cls, id) ?? Array.Empty<CombatKit.ServerFrame>());   // 入口成功,默认建房工厂返回 NoRoom
                rig.Start();
                rig.ConfirmSelected();
                rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);
                break;
            case ExitKind.GiveUp:
                ServeRoomDungeon(rig, 2, _ => TownFrames());
                EnterDungeon(rig);
                rig.Ctrl.RequestGiveUpDungeon();
                rig.PumpUntilIdle();
                break;
            case ExitKind.CombatTimeout:
                ServeRoomDungeon(rig);
                EnterDungeon(rig);
                rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);   // 只轮询控制器:让"战斗任务先收场"
                break;
            default:
                ServeSettlementDungeon(rig);
                EnterDungeon(rig);
                KillAll(rig);
                rig.PumpUntilIdle();
                break;
        }

        // 共同结局:只放权一次且失败、宿主被通知一次、没有任何提示、控制器回到 Idle
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(1, leaves);
        Assert.Equal(1, rig.Disconnects);
        Assert.Equal(1, rig.Ctrl.EnterExclusiveCount);
        Assert.Equal(0, rig.Ctrl.LeaveExclusiveCount);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(0, settlements);
        Assert.Equal(0, rig.Flow.Settlements);

        string[] reasons = Reasons(rig.Flow);
        string[] logs = rig.Logs();
        string faultLine = P + "event=fail reason=LeaveExclusiveFault step=IOException";
        Assert.Equal(faultKind == "Other" ? 1 : 0, logs.Count(l => l.Contains("LeaveExclusiveFault")));
        Assert.Equal(faultKind == "Other", logs.Contains(faultLine));

        switch (kind)
        {
            case ExitKind.EntryTimeout:
                Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
                Assert.DoesNotContain("online enter failed", reasons);
                Assert.Equal(0, rig.Flow.Failures);
                break;
            case ExitKind.RoomBuildFailed:
                Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
                Assert.DoesNotContain("online room build failed", reasons);
                Assert.Equal(0, rig.Flow.Failures);
                break;
            case ExitKind.GiveUp:
                // 放弃回城:离场与区域两行日志照写(在判放权结果之前),但不记"放弃回城",仍停在副本里
                Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
                Assert.Equal(0, rig.Flow.GiveUps);
                Assert.Contains(P + "event=leave reason=GaveUp reported=0 dropped=0 rooms=1", logs);
                Assert.Contains(P + "event=return area=unknown", logs);
                break;
            case ExitKind.CombatTimeout:
                Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
                Assert.Equal(0, rig.Flow.Failures);
                Assert.DoesNotContain("online abort Timeout", reasons);
                Assert.Contains(P + "event=leave reason=Timeout reported=0 dropped=0 rooms=1", logs);
                Assert.Contains(P + "event=fail reason=Timeout step=Combat", logs);
                break;
            default:
                // 结算:放权失败直接清理返回,连 "leave reason=ReturnReady" 都不写,房间也没被放行
                Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
                Assert.DoesNotContain(logs, l => l.Contains("reason=ReturnReady"));
                break;
        }
    }

    [Fact]
    public void The_receive_right_counters_advance_once_per_completed_acquisition_and_release_across_attempts()
    {
        // 保护:EnterExclusiveCount 在"进入独占收包"真的完成之后加一、LeaveExclusiveCount 在"离开"真的完成之后加一;
        // 两局(都在建房失败后正常放权,回选择屏再来一次)后恰好是 2 与 2,中途不会多记也不会漏记。
        using var rig = new Rig();
        rig.Port.SetResponder((cls, id, payload) => EntryReply(cls, id) ?? Array.Empty<CombatKit.ServerFrame>());   // 入口成功;默认建房工厂返回 NoRoom
        Assert.Equal(0, rig.Ctrl.EnterExclusiveCount);
        Assert.Equal(0, rig.Ctrl.LeaveExclusiveCount);

        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntilIdle();
        Assert.Equal(1, rig.Ctrl.EnterExclusiveCount);
        Assert.Equal(1, rig.Ctrl.LeaveExclusiveCount);
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(DungeonSelectNoticeKind.RoomFailed, rig.Flow.Select.Notice.Kind);

        rig.ConfirmSelected();
        rig.PumpUntilIdle();
        Assert.Equal(2, rig.Ctrl.EnterExclusiveCount);
        Assert.Equal(2, rig.Ctrl.LeaveExclusiveCount);
        Assert.Equal(2, rig.Port.EnterExclusiveCalls);
        Assert.Equal(2, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(2, rig.BuilderCalls);
        Assert.Equal(0, rig.Disconnects);
    }
}
