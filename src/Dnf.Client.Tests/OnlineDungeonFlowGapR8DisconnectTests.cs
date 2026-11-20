using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第八轮补测之三(OnlineDungeonFlowGapR8Disconnect):断线清理(通知宿主一次 + 复位流程的"暂缓退出副本"标志)的两个触发口——
/// 空闲时由轮询触发、请求进副本前的预检触发——都必须把 <see cref="GameFlow.OnlineHoldDungeonExit"/> 复位成 false,且只做一次。
/// 全部合成数据,不连任何服务;时间只走场景台的逻辑时钟。
/// </summary>
public sealed class OnlineDungeonFlowGapR8DisconnectTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";
    private const string PeerClosedLine = P + "event=fail reason=Disconnected step=PeerClosed";

    [Fact]
    public void A_polled_disconnect_cleanup_resets_the_hold_flag_and_runs_only_once()
    {
        // 保护:空闲、端口已断、流程不在选角界面时,轮询触发断线清理:通知宿主恰好一次,并把流程的"暂缓退出副本"标志复位成 false
        // (断线后不能再让流程傻等一个再也不会来的结算菜单);之后再轮询不重复清理——既不再通知,也不再去动这个标志。
        // 手算:选择屏阶段(不是选角界面)→ 满足触发条件;预置标志 true → 清理后必须是 false;
        // 清理后再立起标志并轮询 5 次:回调数仍是 1,标志仍是 true(没有第二次清理)。
        using var rig = new Rig();
        rig.Start();
        rig.Flow.OnlineHoldDungeonExit = true;                   // 预置:假装结算菜单的停留标志还立着
        rig.Port.TriggerDisconnect(OnlineDisconnectReason.PeerClosed);
        Assert.Equal(0, rig.Disconnects);                        // 事件处理器只记下断线并写一行日志,清理要等轮询
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(new[] { PeerClosedLine }, rig.Logs());

        rig.Poll();

        Assert.Equal(1, rig.Disconnects);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);   // 清理只通知宿主,不自己换阶段
        Assert.Equal(new[] { PeerClosedLine }, rig.Logs());      // 清理本身不写日志

        rig.Flow.OnlineHoldDungeonExit = true;
        for (int i = 0; i < 5; i++)
        {
            rig.NowMs += 20;
            rig.Poll();
        }

        Assert.Equal(1, rig.Disconnects);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
    }

    [Theory]
    [InlineData(true)]    // 只有端口的断线标志为真(没触发过 Disconnected 事件)
    [InlineData(false)]   // 只有控制器自己记下的断线事件为真(端口的标志还是"没断")
    public void A_pre_enter_disconnect_cleanup_resets_the_hold_flag_and_refuses_the_entry(bool portFlagOnly)
    {
        // 保护:请求进副本时预检到断线,走的是同一个断线清理:通知宿主一次,并把"暂缓退出副本"标志复位成 false;
        // 请求被拒(不进阶段、不碰端口),选择屏不被标忙;再请求一次不重复清理(回调数仍是 1,标志不被第二次复位)。
        using var rig = new Rig();
        rig.Start();
        rig.Flow.OnlineHoldDungeonExit = true;                   // 预置:清理后必须是 false
        if (portFlagOnly)
        {
            rig.Port.IsDisconnected = true;
        }
        else
        {
            rig.Port.RaiseDisconnectedEventOnly(OnlineDisconnectReason.PeerClosed);
        }

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);

        Assert.Equal(1, rig.Disconnects);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
        Assert.Empty(rig.Port.Commands);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Select.Busy);

        rig.Flow.OnlineHoldDungeonExit = true;
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);

        Assert.Equal(1, rig.Disconnects);                        // 断线清理只做一次
        Assert.True(rig.Flow.OnlineHoldDungeonExit);             // 第二次请求没有再复位它
    }
}
