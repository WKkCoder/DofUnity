using System.Reflection;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器里"公开面造不出来的状态"上的防御行为。每条用例都只用反射碰下面这几个私有成员(别的一概走公开面):
/// <list type="bullet">
/// <item><c>OnlineDungeonFlowController._activeRun</c>:在"等放权完成"期间把当前这一局清成 null(正常路径上这时它一定有值),
/// 用来触及"没有这一局时退回用战斗结果的数"的回退。</item>
/// <item><c>OnlineDungeonFlowController._activeRunNotice</c>:读/写当前这一局的叠层提示(正常路径上空闲时它一定是空的;进图失败收尾时它一定有值)。</item>
/// <item><c>OnlineDungeonFlowController._exitKind</c>:待收尾的退出种类;只读它的枚举名(私有枚举类型 <c>PendingExitKind</c> 本身不引用)。</item>
/// </list>
/// 这些回退与防御在公开面上走不到,但各自守的行为(退出日志里的数、失败提示的种类、空闲不留旧提示、新控制器没有待收尾的退出)是明确的。
/// </summary>
public sealed class OnlineDungeonFlowGapR4WhiteBoxTests
{
    private static FieldInfo Field(string name)
    {
        return typeof(OnlineDungeonFlowController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OnlineDungeonFlowController).FullName, name);
    }

    private static OnlineDungeonCombatOptions GiveUpOptions()
    {
        return new OnlineDungeonCombatOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            GiveUpTimeout = TimeSpan.FromSeconds(5),
            SettlementTimeout = TimeSpan.FromSeconds(30),
        };
    }

    [Fact]
    public void A_give_up_exit_with_no_hosted_run_reports_the_combat_counters_and_the_rooms_it_reached()
    {
        // 保护:放弃回城收尾时手里没有这一局(房间那边的数拿不到)就退回用战斗结果的数:报告的击杀数是"已发出"的(这里 1),
        // 不是被服务端拒绝的(0);房间数是"战斗到过的房间序号加一"(只有起始房间时是 1),不是序号本身(0)。
        var releaseGate = new TaskCompletionSource<bool>();
        using var rig = new Rig4(o => o.CombatOptions = GiveUpOptions());
        ServeRoomDungeon(rig, 2, _ => TownFrames());
        EnterDungeon(rig);
        rig.Port.LeaveExclusiveBehavior = _ => releaseGate.Task;
        OnlineDungeonCombatRunner runner = rig.LastContext.CombatRunner;
        Assert.True(runner.PostKill(0, 0));
        rig.PumpControllerUntil(() => runner.Machine.Confirmed == 1, 1500);

        rig.Ctrl.RequestGiveUpDungeon();
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.ReleasingExclusive, 1500);
        Field("_activeRun").SetValue(rig.Ctrl, null);
        releaseGate.SetResult(true);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 1500);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=GaveUp reported=1 dropped=0 rooms=1",
            P + "event=return area=unknown",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.GiveUps);
    }

    [Fact]
    public void An_aborted_combat_exit_with_no_hosted_run_reports_the_combat_counters_and_the_rooms_it_reached()
    {
        // 保护:战斗中止(这里是整局超时)的收尾同样在没有这一局时退回用战斗结果的数:击杀数取"已发出"的(1)、房间数取"序号加一"(1);
        // 日志里的离场原因是战斗的结局名(Timeout),并弹"超时"提示。
        var releaseGate = new TaskCompletionSource<bool>();
        using var rig = new Rig4(o => o.CombatOptions = CombatKit.Options(pollMilliseconds: 10, overallSeconds: 0.8));
        ServeRoomDungeon(rig, 2);
        EnterDungeon(rig);
        rig.Port.LeaveExclusiveBehavior = _ => releaseGate.Task;
        OnlineDungeonCombatRunner runner = rig.LastContext.CombatRunner;
        Assert.True(runner.PostKill(0, 0));
        rig.PumpControllerUntil(() => runner.Machine.Confirmed == 1, 1500);

        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.ReleasingExclusive, 1500);
        Field("_activeRun").SetValue(rig.Ctrl, null);
        releaseGate.SetResult(true);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 1500);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Timeout reported=1 dropped=0 rooms=1",
            P + "event=fail reason=Timeout step=Combat",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.Ctrl.ActiveNotice?.Kind);
        Assert.Equal(1, rig.Flow.Failures);
    }

    [Fact]
    public void An_entry_that_timed_out_falls_back_to_a_timeout_notice_when_no_notice_is_pending()
    {
        // 保护:进图超时(服务端一直不回应)的失败收尾里,控制器记着"这次失败是进图超时";要是这时手头没有挂着的提示,
        // 回选择屏用的提示种类是"超时"、不是笼统的"失败"。正常路径上提示一定是现成的,所以这里清空 _activeRunNotice 才触得到这条回退。
        var releaseGate = new TaskCompletionSource<bool>();
        using var rig = new Rig4();
        rig.Port.LeaveExclusiveBehavior = _ => releaseGate.Task;
        rig.Start();
        rig.ConfirmSelected();

        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.ReleasingExclusive, 1500);
        Assert.NotNull(Field("_activeRunNotice").GetValue(rig.Ctrl));
        Field("_activeRunNotice").SetValue(rig.Ctrl, null);
        releaseGate.SetResult(true);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 1500);

        Assert.Contains(rig.Logs(), l => l.StartsWith(P + "event=fail reason=Timeout step=", StringComparison.Ordinal));
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.Flow.Select.Notice?.Kind);
        Assert.Equal("online enter failed", Reasons(rig.Flow).Last());
    }

    [Fact]
    public void An_idle_controller_drops_a_stale_run_notice_on_its_next_poll()
    {
        // 保护:控制器空闲时不留旧的叠层提示——万一有一条残留的(正常路径上收尾都会清掉),下一次轮询就撤掉它,宿主不会一直看到过期的"正在放弃"之类。
        using var rig = new Rig4();
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Field("_activeRunNotice").SetValue(rig.Ctrl, new DungeonSelectNotice(DungeonSelectNoticeKind.GivingUp));
        Assert.Equal(DungeonSelectNoticeKind.GivingUp, rig.Ctrl.ActiveNotice?.Kind);

        rig.NowMs += 20;
        rig.Ctrl.Poll(rig.NowMs);

        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
    }

    [Fact]
    public void A_fresh_controller_has_no_exit_pending()
    {
        // 保护:刚创建的控制器没有待收尾的退出:退出种类字段(零值)就是"无"。
        using var rig = new Rig4();
        object exit = Field("_exitKind").GetValue(rig.Ctrl);
        Assert.Equal("None", exit.ToString());
    }
}
