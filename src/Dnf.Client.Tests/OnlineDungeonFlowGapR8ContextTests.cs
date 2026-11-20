using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第八轮补测之六(OnlineDungeonFlowGapR8Context):后台异步链不得把续体投回调用者的同步上下文。
/// 宿主(Unity)在主线程上调用控制器,主线程带着自己的同步上下文;控制器退出时要在主线程上做有限等待(<c>Task.Wait</c>),
/// 若后台续体被投回这个上下文,续体要等主线程、主线程在等续体,就会互相堵住。
/// 控制器里一共三处 <c>await</c>(进入独占收包、进副本握手、放开独占收包),每处都用 <c>ConfigureAwait(false)</c> 避免捕获上下文;
/// 测试线程上本来带的是 xUnit 的 <c>AsyncTestSyncContext</c>(不阻塞,Post 也不会出问题),所以不另装上下文就看不出区别。
/// 这里在"await 开始的那一刻"临时给测试线程装一个只数 <c>Post</c> 次数的上下文,三处各一条用例,等到该处的续体真的跑起来后断言一次都没被投回。
/// 计数只取决于 await 开始时线程上有没有上下文,与线程调度的先后无关;用例结束前一定还原线程原来的上下文。
/// </summary>
public sealed class OnlineDungeonFlowGapR8ContextTests
{
    /// <summary>只数 <see cref="Post"/> 次数的同步上下文:被投的回调照常放到线程池里执行(不堵任何东西),只是留下一笔记录。</summary>
    private sealed class CountingSyncContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            d(state);
        }

        public override SynchronizationContext CreateCopy()
        {
            return this;
        }
    }

    /// <summary>在给定的同步上下文之下执行一段动作,无论成败都还原线程原来的上下文。</summary>
    private static void RunUnder(SynchronizationContext context, Action action)
    {
        SynchronizationContext previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>三处等待共用的开局配置:进副本各阶段的超时放宽到 30 秒,保证"没有回包"时握手一直挂起,不会自己超时收场。</summary>
    private static void SlowEntryTimeouts(OnlineDungeonFlowOptions options)
    {
        options.LoadingNoticeTimeout = TimeSpan.FromSeconds(30);
        options.DungeonInfoTimeout = TimeSpan.FromSeconds(30);
        options.StartMapTimeout = TimeSpan.FromSeconds(30);
    }

    [Fact]
    public void The_entry_handshake_never_marshals_its_continuations_back_to_the_callers_synchronization_context()
    {
        // 保护:进副本握手(装载通知 → 选副本 → 副本信息与起始地图)的等待不捕获调用者的同步上下文。
        // 做法:只在"请求开始副本"这一次调用期间装计数上下文(调用结束即还原);握手回包由测试线程在还原之后才喂,
        // 保证握手的等待在调用时一定是挂起状态(没有回包就挂着),此后它的续体若要投回上下文,就必然留下 Post 记录。
        // 手算:控制器自己的续体一次都不该投回 → 进到副本里时 Post 次数是 0。
        var ctx = new CountingSyncContext();
        using var rig = new Rig(SlowEntryTimeouts);
        rig.RoomBuilder = built =>
        {
            var room = new OnlineDungeonRoom(built.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(2), plan, Array.Empty<GateSide>()));
            return OnlineDungeonRoomBuildResult.Success(room, run);
        };
        rig.Start();

        RunUnder(ctx, () => rig.Flow.OnlineStartDungeon(rig.Entries[0], 0));
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);

        // 回包由测试线程喂:先给装载通知 27,等到"选副本"命令发出后再给副本信息 28 + 起始地图 29
        foreach (CombatKit.ServerFrame frame in LoadingFrames())
        {
            rig.Port.CurrentNet.Enqueue(frame);
        }
        rig.WaitForCommand(OnlineDungeonEntry.SelectDungeonPacketId);
        foreach (CombatKit.ServerFrame frame in InfoAndMapFrames(DungeonAId, 2))
        {
            rig.Port.CurrentNet.Enqueue(frame);
        }
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        Assert.Equal(0, ctx.Posts);
    }

    [Fact]
    public void Acquiring_the_exclusive_receive_right_never_marshals_its_continuation_back_to_the_callers_synchronization_context()
    {
        // 保护:进入独占收包这一步的等待不捕获调用者的同步上下文。
        // 做法:替身端口的"进入独占收包"一直挂起(由测试放行);只在"请求开始副本"这次调用期间装计数上下文,
        // 随后测试线程(无上下文)放行,控制器的续体接着去造跑器 —— 续体一旦开始跑,就说明它被放行并执行了。
        // 手算:放行前跑器造 0 次;续体跑起来后造 1 次,且这条续体一次都不该经过上下文 → Post 次数是 0。
        var ctx = new CountingSyncContext();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(SlowEntryTimeouts);
        rig.Port.EnterExclusiveBehavior = _ => gate.Task;
        rig.Start();

        RunUnder(ctx, () => rig.Flow.OnlineStartDungeon(rig.Entries[0], 0));
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Port.CreateRunnerCalls);
        Assert.Equal(0, ctx.Posts);

        gate.SetResult(true);
        rig.PumpControllerUntil(() => rig.Port.CreateRunnerCalls == 1);

        Assert.Equal(0, ctx.Posts);
    }

    [Fact]
    public void Releasing_the_exclusive_receive_right_never_marshals_its_continuation_back_to_the_callers_synchronization_context()
    {
        // 保护:放开独占收包这一步的等待不捕获调用者的同步上下文。
        // 做法:没有任何回包,进副本握手在 70ms 后超时失败,控制器走"入场失败"出口并放开独占收包;替身端口的"放开独占收包"挂起(由测试放行)。
        // 放开这一步是宿主轮询(<c>Poll</c>)触发的,所以轮询在计数上下文之下进行;等到"放开"已发起后,测试线程放行,再继续轮询到回到空闲。
        // 手算:放开发起过 1 次;放行后续体把"独占已放开"记下来,控制器收尾回到 Idle,全程续体都不经过上下文 → Post 次数是 0。
        var ctx = new CountingSyncContext();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int leaveStarted = 0;
        using var rig = new Rig();
        rig.Port.LeaveExclusiveBehavior = _ =>
        {
            Interlocked.Increment(ref leaveStarted);
            return gate.Task;
        };
        rig.Start();

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 0);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        RunUnder(ctx, () => rig.PumpControllerUntil(() => Volatile.Read(ref leaveStarted) == 1));
        Assert.Equal(OnlineDungeonFlowPhase.ReleasingExclusive, rig.Ctrl.Phase);
        Assert.Equal(0, ctx.Posts);

        gate.SetResult(true);
        RunUnder(ctx, () => rig.PumpUntilIdle());

        Assert.Equal(1, Volatile.Read(ref leaveStarted));
        Assert.Equal(0, ctx.Posts);
    }
}
