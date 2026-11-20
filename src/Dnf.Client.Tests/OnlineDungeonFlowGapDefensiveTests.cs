using System.Reflection;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器对"合法流程造不出来的坏结果"的防御:入场结局声称成功却没有起始地图、建房结果声称成功却缺房间或缺这一局、
/// 战斗任务以取消或出错收场。这些状态正常的网络层与建房工厂都造不出来(网络层的入场结局只能由本程序集内部构造,
/// 建房结果的工厂方法拒绝空值),所以用反射把它们塞进控制器的私有字段或直接实例化,只验证"控制器不把坏结果当好结果、也不抛异常"。
/// 依赖的私有名字:<c>_enterTask</c>、<c>_combatTask</c>、<c>OnlineDungeonRoomBuildResult</c> 与 <c>OnlineDungeonResult</c> 的非公开构造函数。
/// </summary>
public sealed class OnlineDungeonFlowGapDefensiveTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";
    private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static void SetPrivate(object target, string field, object value)
    {
        FieldInfo info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        info.SetValue(target, value);
    }

    private static string[] Reasons(GameFlow flow)
    {
        return flow.History.Select(h => h.Reason).ToArray();
    }

    [Fact]
    public void An_entry_result_that_claims_success_without_a_start_map_is_treated_as_a_failed_entry()
    {
        // 保护:入场结局只要没带起始地图,不管它说自己成没成功,控制器都当成入场失败收尾(记失败日志、给"失败"提示、返回选择屏),
        // 不会往下去建房。手算:结局是 阶段 Done、失败原因 None,所以失败日志写 reason=None step=Done;提示是 Failed(不是断线、不是超时)。
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig();
        rig.Port.EnterExclusiveBehavior = _ => hold.Task;     // 入场停在"进入独占收包":控制器停在 Entering,没有战斗跑器
        rig.Start();
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);

        object bad = Activator.CreateInstance(
            typeof(OnlineDungeonResult),
            NonPublic,
            null,
            new object[] { OnlineDungeonStage.Done, OnlineDungeonFailure.None, null, null, null, null, null, null, Array.Empty<OnlineDungeonFrame>() },
            null);
        var badResult = (OnlineDungeonResult)bad;
        Assert.True(badResult.Entered);
        Assert.Null(badResult.StartMap);
        SetPrivate(rig.Ctrl, "_enterTask", Task.FromResult(badResult));
        rig.PumpUntilIdle();
        hold.TrySetCanceled();

        Assert.Equal(new[]
        {
            P + "event=select outcome=enter difficulty=1 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=1",
            P + "event=fail reason=None step=Done",
        }, rig.Logs());
        Assert.Equal(0, rig.Port.LeaveExclusiveCalls);                           // 从没进过独占收包,也就不用放
        Assert.Equal(0, rig.BuilderCalls);                                       // 没往下去建房
        Assert.Equal("online enter failed", Reasons(rig.Flow).Last());
        Assert.Equal(DungeonSelectNoticeKind.Failed, rig.Flow.Select.Notice.Kind);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Disconnects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_build_result_that_claims_success_without_its_run_or_room_is_treated_as_a_failed_build(bool runMissing)
    {
        // 保护:建房结果说"成功"但缺这一局(或缺房间对象)时,控制器按建房失败收尾——写房间状态与失败日志、给"房间失败"提示、
        // 取消战斗、释放独占收包、回到选择屏——而不是拿着空的这一局往下走(那会在进副本时空引用崩溃)。
        // 手算:状态取房间自己的状态 Ready;缺这一局时房间还在,所以怪数照实写 spawned=2 expected=2;缺房间时只能写缺省的 0。
        using var rig = new Rig();
        ServeRoomDungeon(rig);
        rig.RoomBuilder = ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(2), plan, Array.Empty<GateSide>()));
            Assert.Equal(OnlineRoomStatus.Ready, room.Status);
            object built = Activator.CreateInstance(
                typeof(OnlineDungeonRoomBuildResult),
                NonPublic,
                null,
                new object[] { true, room.Status, runMissing ? room : null, runMissing ? null : run },
                null);
            return (OnlineDungeonRoomBuildResult)built;
        };
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntilIdle();

        Assert.Equal(1, rig.BuilderCalls);
        Assert.Equal(new[]
        {
            P + "event=select outcome=enter difficulty=0 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=0",
            P + (runMissing
                ? "event=room status=Ready multi=off spawned=2 expected=2 rooms=1"
                : "event=room status=Ready multi=off spawned=0 expected=0 rooms=1"),
            P + "event=fail reason=RoomBuildFailed step=Ready",
        }, rig.Logs());
        Assert.Equal("online room build failed", Reasons(rig.Flow).Last());
        Assert.Equal(DungeonSelectNoticeKind.RoomFailed, rig.Flow.Select.Notice.Kind);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);                  // 回到选择屏,没有进副本
        Assert.Equal(0, rig.Disconnects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_combat_task_that_ends_faulted_or_canceled_gives_no_result_and_the_dungeon_is_aborted(bool canceled)
    {
        // 保护:战斗任务以出错或被取消收场时,控制器拿不到结果,但不会因此抛异常、也不当成结算:按"中止"收尾——
        // 离场与失败日志的原因写占位词 Aborted(没有结果可报),提示是 Aborted,流程记一次失败回城,放权一次。
        using var rig = new Rig();
        ServeRoomDungeon(rig);
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        Task<OnlineDungeonCombatResult> broken = canceled
            ? Task.FromCanceled<OnlineDungeonCombatResult>(new CancellationToken(true))
            : Task.FromException<OnlineDungeonCombatResult>(new InvalidOperationException("synthetic"));
        SetPrivate(rig.Ctrl, "_combatTask", broken);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        Assert.Equal(new[]
        {
            P + "event=select outcome=enter difficulty=0 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=0",
            P + "event=room status=Ready multi=off spawned=2 expected=2 rooms=1",
            P + "event=leave reason=Aborted reported=0 dropped=0 rooms=1",
            P + "event=fail reason=Aborted step=Combat",
        }, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal("online abort Aborted", Reasons(rig.Flow).Last());
        Assert.Equal(DungeonSelectNoticeKind.Aborted, rig.Ctrl.ActiveNotice?.Kind);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
    }
}
