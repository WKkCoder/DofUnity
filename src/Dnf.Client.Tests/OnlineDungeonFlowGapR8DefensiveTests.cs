using System.Reflection;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第八轮补测之五(OnlineDungeonFlowGapR8Defensive):对"正常流程造不出来的坏结果"的又一处防御——
/// 进入任务正常完成(没取消、没出错)却没有结果对象(null)。网络层进入流程的每条返回路径都会造出结果对象,经公开面造不出这个状态,
/// 所以(沿用 <see cref="OnlineDungeonFlowGapDefensiveTests"/> 的做法)用反射把一个"已完成、结果为 null"的任务塞进控制器的私有字段,
/// 只验证"控制器不把它当好结果、不抛空引用"。依赖的私有名字:控制器的 <c>_enterTask</c> 字段。
/// </summary>
public sealed class OnlineDungeonFlowGapR8DefensiveTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";

    private static void SetPrivate(object target, string field, object value)
    {
        FieldInfo info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        info.SetValue(target, value);
    }

    [Fact]
    public void An_entry_task_that_completes_with_no_result_at_all_is_treated_as_a_failed_entry()
    {
        // 保护:进入任务正常完成但没有结果对象(既没取消、也没出错)时,控制器当成入场失败收尾,而不是去读空结果的字段(那会空引用崩溃):
        // 写失败日志——原因写占位词 NullResult、步骤 Enter(区别于取消的 Cancelled 与出错时的异常类型名);
        // 提示是"断线"(没被取消就当连接出了问题,与出错的入场同类;被取消的才是"失败");从没进过独占收包也就不放、不建房、回到选择屏。
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig();
        rig.Port.EnterExclusiveBehavior = _ => hold.Task;     // 入场停在"进入独占收包":控制器停在 Entering,没有战斗跑器
        rig.Start();
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);

        SetPrivate(rig.Ctrl, "_enterTask", Task.FromResult<OnlineDungeonResult>(null));
        rig.PumpUntilIdle();
        hold.TrySetCanceled();

        Assert.Equal(new[]
        {
            P + "event=select outcome=enter difficulty=1 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=1",
            P + "event=fail reason=NullResult step=Enter",
        }, rig.Logs());
        Assert.Equal(0, rig.BuilderCalls);                                       // 没往下去建房
        Assert.Equal(0, rig.Port.LeaveExclusiveCalls);                           // 从没进过独占收包,也就不用放
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(DungeonSelectNoticeKind.Disconnected, rig.Flow.Select.Notice.Kind);
        Assert.False(rig.Flow.Select.Busy);
        Assert.Equal("online enter failed", OnlineDungeonFlowGapR4Kit.Reasons(rig.Flow).Last());
        Assert.Equal(0, rig.Disconnects);                                        // 没有触发断线清理(端口没断),走的是普通的入场失败收口
    }
}
