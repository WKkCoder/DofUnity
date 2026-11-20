using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第八轮补测之一(OnlineDungeonFlowGapR8Dispose):控制器 <c>Dispose()</c> 之后必须是"惰性"的——
/// 轮询不再做断线清理与许可同步、重复释放不重复收尾。每个用例都带一个"活着的控制器"对照(同样的操作在活着的控制器上确实有动作),
/// 保证断言不是空转。全部合成数据,不连任何服务;时间只走场景台的逻辑时钟。直接复用 <see cref="OnlineDungeonFlowGapKit"/>(internal)的场景台与假端口。
/// </summary>
public sealed class OnlineDungeonFlowGapR8DisposeTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";
    private const string PeerClosedLine = P + "event=fail reason=Disconnected step=PeerClosed";

    /// <summary>逻辑时钟每圈 +20ms,用场景台的方式轮询控制器与流程若干次(只为让"每帧都会发生的事"有机会发生)。</summary>
    private static void PollSeveralTimes(Rig rig, int times = 5)
    {
        for (int i = 0; i < times; i++)
        {
            rig.NowMs += 20;
            rig.Poll();
        }
    }

    [Fact]
    public void A_disposed_controller_does_not_run_the_disconnect_cleanup_it_had_already_recorded()
    {
        // 保护:断线事件已被控制器记下、但还没轮询到时,宿主释放了控制器——释放之后的轮询必须什么都不做:
        // 不通知宿主做断线清理(OnDisconnected 回调零次)、不去复位流程的"暂缓退出副本"标志、不再写日志、不动选择屏与流程阶段。
        // 对照:不释放的控制器,同样的断线在第一次轮询时恰好通知宿主一次(空闲 + 已断线 + 不在选角界面)。
        using (var live = new Rig())
        {
            live.Start();
            live.Port.TriggerDisconnect(OnlineDisconnectReason.PeerClosed);
            Assert.Equal(0, live.Disconnects);                   // 事件处理器只记下断线,清理要等轮询
            live.Poll();
            Assert.Equal(1, live.Disconnects);
        }

        using var rig = new Rig();
        rig.Start();
        DungeonSelectScreen screen = rig.Flow.Select;
        rig.Port.TriggerDisconnect(OnlineDisconnectReason.PeerClosed);
        Assert.Equal(new[] { PeerClosedLine }, rig.Logs());      // 事件处理器写了这一行(释放之前)
        Assert.Equal(0, rig.Disconnects);

        rig.Ctrl.Dispose();
        rig.Flow.OnlineHoldDungeonExit = true;                   // 哨兵:断线清理会把它复位成 false
        int history = rig.Flow.History.Count;
        int serial = rig.Flow.StageSerial;

        PollSeveralTimes(rig);

        Assert.Equal(0, rig.Disconnects);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(new[] { PeerClosedLine }, rig.Logs());      // 释放之后一行日志都没多
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Same(screen, rig.Flow.Select);
        Assert.Equal(history, rig.Flow.History.Count);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(1, rig.Port.DisconnectedRemoves);           // 释放时退订了断线事件(只退一次)
    }

    [Fact]
    public void A_disposed_controller_stops_syncing_permission_changes_into_the_open_select_screen()
    {
        // 保护:许可表变化时,活着的控制器在下一次轮询把新的难度上限同步给已打开的选择屏(难度重新夹紧);释放之后不再同步。
        // 手算:副本 A 初始档位 3 → 难度上限 3,选择屏难度调到 3。许可更新为档位 2 → 上限 2:活着时一次轮询后难度夹到 2。
        // 释放之后许可再更新为档位 1 → 上限 1:轮询后难度必须仍是 2(超出上限也不被夹到 1),因为已释放的控制器不再碰界面。
        using var rig = new Rig();
        rig.Start();
        DungeonSelectScreen screen = rig.Flow.Select;
        screen.MoveDifficulty(3);
        Assert.Equal(3, screen.Difficulty);

        rig.Table.ApplyUpdate(new[] { new KeyValuePair<int, byte>(DungeonAId, (byte)2) });
        Assert.Equal(2, screen.MaxDifficulty);
        Assert.Equal(3, screen.Difficulty);                      // 还没轮询:界面还停在旧难度
        rig.Poll();
        Assert.Equal(2, screen.Difficulty);                      // 活着的控制器同步了:夹到 2

        rig.Ctrl.Dispose();
        rig.Table.ApplyUpdate(new[] { new KeyValuePair<int, byte>(DungeonAId, (byte)1) });
        Assert.Equal(1, screen.MaxDifficulty);
        PollSeveralTimes(rig);
        Assert.Equal(2, screen.Difficulty);                      // 已释放:不再同步,仍是 2
        Assert.Empty(rig.Logs());                                // 全程控制器没写过日志(许可更新是直接改表,不经端口帧)
    }

    [Fact]
    public void Disposing_again_does_not_repeat_the_teardown()
    {
        // 保护:Dispose 幂等——第二次及以后是空操作,不再重做一遍收尾(取消、有限等待、清运行态、把"暂缓退出副本"标志复位)。
        // 做法:第一次释放后立起哨兵标志(模拟宿主又把结算停留标志立起),再释放两次:标志必须还立着,端口事件也只退订过一次。
        using var rig = new Rig();
        rig.Start();
        rig.Flow.OnlineHoldDungeonExit = true;

        rig.Ctrl.Dispose();
        Assert.False(rig.Flow.OnlineHoldDungeonExit);            // 第一次释放把它复位
        Assert.Equal(1, rig.Port.FrameReceivedRemoves);
        Assert.Equal(1, rig.Port.DisconnectedRemoves);

        rig.Flow.OnlineHoldDungeonExit = true;
        rig.Ctrl.Dispose();
        rig.Ctrl.Dispose();

        Assert.True(rig.Flow.OnlineHoldDungeonExit);             // 后两次释放没有再去复位它
        Assert.Equal(1, rig.Port.FrameReceivedRemoves);
        Assert.Equal(1, rig.Port.DisconnectedRemoves);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
    }
}
