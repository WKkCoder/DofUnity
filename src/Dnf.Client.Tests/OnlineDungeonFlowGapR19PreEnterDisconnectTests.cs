using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第十九轮补测之一(OnlineDungeonFlowGapR19PreEnterDisconnect):请求进副本时入场预检发现"已断线",
/// 控制器只做三件事——写一行 <c>event=fail reason=Disconnected step=PreEnter</c> 日志、通知宿主做一次断线清理(宿主据此让联网外壳回登录界面并带"连接已断开"提示)、直接返回;
/// 除此之外不留任何痕迹:阶段仍空闲、没有"进入中"提示、不进独占收包、不向端口发任何包、不往选择屏上写提示,
/// 之后重新打开选择屏也不会冒出一条过期的断线提示。
/// (控制器里曾有一套"下次打开选择屏时补显断线提示"的字段与代码,没有任何地方把它的开关置成真,是死代码,已删除;这些可观察行为在删除前后一条不变。)
/// 每条断言都带一个"没断线"的对照(同一个条目、同一个难度、同一种调用,行为不同),保证断言不是空转。全部合成数据,不连任何服务;时间只走场景台的逻辑时钟。
/// </summary>
public sealed class OnlineDungeonFlowGapR19PreEnterDisconnectTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";
    private const string PreEnterLine = P + "event=fail reason=Disconnected step=PreEnter";
    private const string PeerClosedLine = P + "event=fail reason=Disconnected step=PeerClosed";

    public enum Cut
    {
        /// <summary>只有端口的断线标志为真(没触发过 Disconnected 事件):预检靠 <c>IsDisconnected</c> 发现。</summary>
        PortFlagOnly,

        /// <summary>只有控制器自己记下的断线事件为真(端口的标志还是"没断"):预检靠控制器记下的断线标志发现,事件处理器多写一行 PeerClosed 日志。</summary>
        ControllerEventOnly,
    }

    private static void CutConnection(Rig rig, Cut cut)
    {
        if (cut == Cut.PortFlagOnly)
        {
            rig.Port.IsDisconnected = true;
        }
        else
        {
            rig.Port.RaiseDisconnectedEventOnly(OnlineDisconnectReason.PeerClosed);
        }
    }

    /// <summary>逻辑时钟每圈 +20ms,用场景台的方式轮询控制器与流程若干次。</summary>
    private static void PollSeveralTimes(Rig rig, int times = 5)
    {
        for (int i = 0; i < times; i++)
        {
            rig.NowMs += 20;
            rig.Poll();
        }
    }

    [Theory]
    [InlineData(Cut.PortFlagOnly)]
    [InlineData(Cut.ControllerEventOnly)]
    public void A_disconnected_entry_request_tells_the_host_exactly_once_and_leaves_no_other_trace(Cut cut)
    {
        // 保护:选择屏上请求进副本(副本 A 难度 1,上限 3 之内原样保留)时预检发现已断线——
        //  · 通知宿主做断线清理恰好一次(对照:没断线不通知);
        //  · 控制器阶段仍是空闲(对照:没断线进入"进入中");
        //  · 没有"进入中"提示(对照:没断线时控制器与选择屏上各亮一条 Entering);
        //  · 没有进入独占收包——控制器的计数与端口的计数都是 0(对照:没断线都是 1);
        //  · 日志里有且只有那一行 PreEnter 失败(事件版多一行请求之前就写下的 PeerClosed),没有"选择/进入"两行起手日志(对照:没断线正是这两行);
        //  · 不向端口发任何包、不造跑器(对照:没断线发出"开始游戏"命令 1/15、造一个跑器);
        //  · 不往选择屏写提示也不标忙(对照:没断线时选择屏标忙并亮 Entering);
        //  · 不换选择屏、流程历史与阶段序号都不动(对照:没断线的那边等进入走完——没有服务端回包,等"装载通知"超时失败——之后,选择屏被换成新的一块、历史与序号都增加)。
        // 手算:副本 A 的档位上限是 3,请求难度 1 → 夹紧后仍是 1;缺省场景台没有当前区域 → areakind=unknown、entrance=true。
        using var broken = new Rig();
        using var live = new Rig();
        broken.Start();
        live.Start();
        DungeonSelectScreen brokenScreen = broken.Flow.Select;
        DungeonSelectScreen liveScreen = live.Flow.Select;

        CutConnection(broken, cut);
        string[] beforeRequest = cut == Cut.ControllerEventOnly ? new[] { PeerClosedLine } : new string[0];
        Assert.Equal(beforeRequest, broken.Logs());              // 事件处理器只记下断线(并写一行),清理要等预检或轮询
        Assert.Equal(0, broken.Disconnects);
        int history = broken.Flow.History.Count;
        int serial = broken.Flow.StageSerial;
        int liveHistory = live.Flow.History.Count;
        int liveSerial = live.Flow.StageSerial;

        broken.Flow.OnlineStartDungeon(broken.Entries[0], 1);
        live.Flow.OnlineStartDungeon(live.Entries[0], 1);

        // 通知宿主
        Assert.Equal(1, broken.Disconnects);
        Assert.Equal(0, live.Disconnects);

        // 阶段
        Assert.Equal(OnlineDungeonFlowPhase.Idle, broken.Ctrl.Phase);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, live.Ctrl.Phase);

        // 宿主叠层读的提示
        Assert.Null(broken.Ctrl.ActiveNotice);
        Assert.Equal(DungeonSelectNoticeKind.Entering, live.Ctrl.ActiveNotice?.Kind);

        // 独占收包
        Assert.Equal(0, broken.Ctrl.EnterExclusiveCount);
        Assert.Equal(0, broken.Port.EnterExclusiveCalls);
        Assert.Equal(1, live.Ctrl.EnterExclusiveCount);
        Assert.Equal(1, live.Port.EnterExclusiveCalls);

        // 日志
        Assert.Equal(beforeRequest.Concat(new[] { PreEnterLine }).ToArray(), broken.Logs());
        Assert.Equal(
            new[]
            {
                P + "event=select outcome=enter difficulty=1 max=3",
                P + "event=enter areakind=unknown entrance=true difficulty=1",
            },
            live.Logs());

        // 选择屏:不写提示、不标忙(对照:没断线时选择屏被标忙并亮 Entering)
        Assert.Null(brokenScreen.Notice);
        Assert.False(brokenScreen.Busy);
        Assert.Equal(DungeonSelectNoticeKind.Entering, liveScreen.Notice?.Kind);
        Assert.True(liveScreen.Busy);

        // 端口:没断线时"开始游戏"命令(1/15)真发出去了(等它到,不靠睡眠),跑器也造了;断线时一个包都没发、跑器没造
        live.WaitForCommand(OnlineDungeonEntry.StartGamePacketId);
        Assert.Contains(live.Port.Commands, c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.StartGamePacketId);
        Assert.Equal(1, live.Port.CreateRunnerCalls);
        PollSeveralTimes(broken);                                // 之后轮询也不会冒出新动静(断线清理已做过,不重复)
        Assert.Empty(broken.Port.Commands);
        Assert.Equal(0, broken.Port.CreateRunnerCalls);
        Assert.Equal(1, broken.Disconnects);
        Assert.Equal(beforeRequest.Concat(new[] { PreEnterLine }).ToArray(), broken.Logs());

        // 选择屏对象、流程历史与阶段序号:断线的这边原样不动;没断线的那边等进入走完之后选择屏被重新打开(换了新的一块),历史与序号都增加
        Assert.Same(brokenScreen, broken.Flow.Select);
        Assert.Equal(history, broken.Flow.History.Count);
        Assert.Equal(serial, broken.Flow.StageSerial);
        live.PumpUntilIdle();
        Assert.NotSame(liveScreen, live.Flow.Select);
        Assert.True(live.Flow.History.Count > liveHistory);
        Assert.True(live.Flow.StageSerial > liveSerial);
    }

    [Theory]
    [InlineData(Cut.PortFlagOnly)]
    [InlineData(Cut.ControllerEventOnly)]
    public void A_select_screen_reopened_after_a_disconnected_entry_request_carries_no_stale_disconnect_notice(Cut cut)
    {
        // 保护:预检断线之后玩家离开选择屏再重新打开(每次打开,流程都会叫控制器配置一遍新的选择屏),
        // 新屏上不能冒出一条过期的断线提示:提示是空的、不忙;控制器确实给新屏配置过(条目状态回调已接上),所以"没提示"不是因为控制器没管这块屏。
        // 对照:进入任务出错(不是预检断线)那条路径确实会把一条 Disconnected 提示交给重新打开的选择屏——探针读得到断线提示,不是瞎的。
        using var rig = new Rig();
        rig.Start();
        DungeonSelectScreen first = rig.Flow.Select;
        CutConnection(rig, cut);
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);
        Assert.Equal(1, rig.Disconnects);
        Assert.Null(first.Notice);

        rig.Flow.EnterTown("leave select");
        Assert.Null(rig.Flow.Select);
        rig.Flow.EnterSelect("reopen select");

        DungeonSelectScreen second = rig.Flow.Select;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.NotNull(second.EntryStatus);                      // 控制器的配置钩子跑过了
        Assert.Null(second.Notice);
        Assert.False(second.Busy);
        Assert.Equal(1, rig.Disconnects);                        // 重新打开选择屏不会再通知宿主

        // 对照:同一个场景台形状,进入任务在进独占收包时就抛(端口没断):选择屏被重新打开,并带着一条 Disconnected 提示
        using var faulted = new Rig();
        faulted.Port.EnterExclusiveBehavior = _ => Task.FromException(new InvalidOperationException("synthetic"));
        faulted.Start();
        DungeonSelectScreen before = faulted.Flow.Select;
        faulted.Flow.OnlineStartDungeon(faulted.Entries[0], 1);
        faulted.PumpUntilIdle();
        Assert.NotSame(before, faulted.Flow.Select);
        Assert.Equal(DungeonSelectNoticeKind.Disconnected, faulted.Flow.Select.Notice?.Kind);
        Assert.Equal(0, faulted.Disconnects);
    }
}
