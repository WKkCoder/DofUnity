using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第八轮补测之四(OnlineDungeonFlowGapR8Lifecycle):控制器生命周期两端的小处——
/// 创建时接管流程要把"暂缓退出副本"标志清成 false;只调用 <c>Dispose()</c>(不先 <c>PrepareForQuit()</c>)也要取消并有限等待后台任务。
/// 全部合成数据,不连任何服务。
/// </summary>
public sealed class OnlineDungeonFlowGapR8LifecycleTests
{
    [Fact]
    public void A_new_controller_clears_the_hold_flag_it_finds_on_the_flow()
    {
        // 保护:控制器创建时接管流程,把"暂缓退出副本"标志清成 false——上一个会话(或宿主)可能留下立着的标志,
        // 不清的话新会话第一次进副本、第一次出副本都会被这面旧旗误拦。流程上本来就是 false 时也保持 false(不是"置位")。
        using var raised = new Rig4(beforeController: f => f.OnlineHoldDungeonExit = true);
        Assert.False(raised.Flow.OnlineHoldDungeonExit);

        using var plain = new Rig();
        Assert.False(plain.Flow.OnlineHoldDungeonExit);
    }

    [Fact]
    public void Disposing_waits_for_a_pending_entry_task_that_is_slow_to_wind_down()
    {
        // 保护:宿主只调用 Dispose(没有先 PrepareForQuit)时,释放也要取消进入任务并有限等待它收尾,收尾完才返回,
        // 而不是取消之后立刻回来把后台任务丢在身后。进入任务在取消之后还要收尾一小会儿(用一段真实的短延时模拟"收尾慢",
        // 不是拿它排顺序;等待上限 5 秒远大于它,等待是事件驱动的,任务一结束就返回)。
        using var windDownDone = new ManualResetEventSlim(false);
        using var rig = new Rig(o => o.QuitWaitTimeoutMs = 5000);
        rig.Port.EnterExclusiveBehavior = async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(100);        // 取消之后的收尾
                windDownDone.Set();
                throw;
            }
        };
        rig.Start();
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 0);   // 进入任务挂在"进入独占收包"上
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.False(windDownDone.IsSet);

        rig.Ctrl.Dispose();

        Assert.True(windDownDone.IsSet);                   // 释放返回时,后台任务已经收尾完
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }
}
