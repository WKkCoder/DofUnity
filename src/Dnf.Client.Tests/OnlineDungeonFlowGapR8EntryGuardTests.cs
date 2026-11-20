using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第八轮补测之二(OnlineDungeonFlowGapR8EntryGuard):"开始副本"入口守卫(已释放 或 条目为空 或 阶段非空闲 三条腿任一为真就忽略)
/// 的前两条腿——已释放、条目为空——各用"刚好只有这一条腿成立"的输入打一遍:另两条腿都不成立(阶段是空闲;另一条腿的条目非空 / 控制器未释放)。
/// 每个用例都带对照:同一个控制器(或同一种调用)在腿不成立时确实会开始进副本。全部合成数据;时间只走场景台的逻辑时钟。
/// </summary>
public sealed class OnlineDungeonFlowGapR8EntryGuardTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";

    /// <summary>进了两行起手日志(选择 + 进入)的"被受理"形态:难度 <paramref name="difficulty"/> 在副本 A 的上限 3 之内原样保留。</summary>
    private static string[] AcceptedLogs(int difficulty)
    {
        string d = difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new[]
        {
            P + "event=select outcome=enter difficulty=" + d + " max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=" + d,
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_start_request_without_an_entry_is_ignored_by_a_live_idle_controller(bool selectScreenOpen)
    {
        // 保护:联网"开始副本"入口对"没有条目"(null)一律忽略——控制器没释放、阶段是空闲,唯独条目为空:
        // 阶段仍空闲、一行日志都不写(不会冒出 blocked / MissingDungeonId 那一行)、端口无任何动静;
        // 在选择屏上:忙标志与提示对象原样不动(不会被"被禁条目"分支清忙并换成空提示);在城镇里:阶段不变、不会被重新打开选择屏、历史不增。
        using var rig = new Rig();
        DungeonSelectScreen screen = null;
        var sentinel = new DungeonSelectNotice(DungeonSelectNoticeKind.Failed);
        if (selectScreenOpen)
        {
            rig.Start();
            screen = rig.Flow.Select;
            screen.Busy = true;                                  // 哨兵:被禁条目分支会把它清成 false
            screen.Notice = sentinel;                            // 哨兵:被禁条目分支会把它换成"空提示"
        }
        else
        {
            rig.Flow.EnterTown("boot");
        }
        GameStage stage = rig.Flow.Stage;
        int history = rig.Flow.History.Count;
        int serial = rig.Flow.StageSerial;

        rig.Flow.OnlineStartDungeon(null, 2);

        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Empty(rig.Logs());
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
        Assert.Equal(0, rig.Port.CreateRunnerCalls);
        Assert.Empty(rig.Port.Commands);
        Assert.Equal(stage, rig.Flow.Stage);
        Assert.Equal(history, rig.Flow.History.Count);
        Assert.Equal(serial, rig.Flow.StageSerial);
        if (selectScreenOpen)
        {
            Assert.Same(screen, rig.Flow.Select);
            Assert.True(screen.Busy);
            Assert.Same(sentinel, screen.Notice);
        }
        else
        {
            Assert.Null(rig.Flow.Select);
        }

        // 对照:同一个活着的、空闲的控制器,给一个真条目就开始进副本(难度 2 在副本 A 的上限 3 之内原样保留)
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 2);

        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.Equal(AcceptedLogs(2), rig.Logs());
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
    }

    [Fact]
    public void A_start_hook_saved_before_disposal_does_nothing_afterwards()
    {
        // 保护:宿主可能在释放控制器之前就把流程的"联网开始副本"委托抄存了一份(比如异步回调里);
        // 释放后再调用这份旧委托,必须被忽略——控制器已释放,哪怕条目非空、阶段也是空闲:阶段仍空闲、不写日志、
        // 不碰端口(不进独占收包、不造跑器、不发命令)、选择屏上的忙标志与提示对象原样不动、流程不换阶段也不增历史。
        // 对照:同样的抄存委托、同样的条目与难度,打在没释放的控制器上,立刻进入"进入中"。
        using (var live = new Rig())
        {
            live.Start();
            Action<DungeonEntry, int> liveHook = live.Flow.OnlineStartDungeon;
            Assert.NotNull(liveHook);
            liveHook(live.Entries[0], 2);
            Assert.Equal(OnlineDungeonFlowPhase.Entering, live.Ctrl.Phase);
            Assert.Equal(AcceptedLogs(2), live.Logs());
            Assert.Equal(1, live.Port.EnterExclusiveCalls);
        }

        using var rig = new Rig();
        rig.Start();
        Action<DungeonEntry, int> hook = rig.Flow.OnlineStartDungeon;
        Assert.NotNull(hook);
        DungeonSelectScreen screen = rig.Flow.Select;
        var sentinel = new DungeonSelectNotice(DungeonSelectNoticeKind.Failed);
        screen.Notice = sentinel;                                // 哨兵:被受理的请求会把它换成"进入中"提示
        Assert.False(screen.Busy);                               // 哨兵:被受理的请求会把选择屏标忙

        rig.Ctrl.Dispose();
        Assert.Null(rig.Flow.OnlineStartDungeon);                // 释放时钩子已归还流程;抄存的旧委托是唯一的入口
        int history = rig.Flow.History.Count;
        int serial = rig.Flow.StageSerial;

        hook(rig.Entries[0], 2);

        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Empty(rig.Logs());
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
        Assert.Equal(0, rig.Port.CreateRunnerCalls);
        Assert.Empty(rig.Port.Commands);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Same(screen, rig.Flow.Select);
        Assert.False(screen.Busy);
        Assert.Same(sentinel, screen.Notice);
        Assert.Equal(history, rig.Flow.History.Count);
        Assert.Equal(serial, rig.Flow.StageSerial);
    }
}
