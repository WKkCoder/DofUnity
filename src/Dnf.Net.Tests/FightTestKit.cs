using System.Diagnostics;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 手拨的假时钟(给 <see cref="OnlineFightDriver"/> 的 <see cref="IFightClock"/> 接缝用):时间只在测试调用 <see cref="Advance"/> 或
/// <see cref="AdvanceToNext"/> 时才走,所以断言不依赖真实的快慢,也不用真等。
/// 每次 <see cref="DelayAsync"/> 请求都被记下(<see cref="Requests"/>),节奏间隔、等待步长与"该等多久"都能直接断言。
/// 起点取 1000 秒而不是 0,免得把"相对开始的时间"与"绝对读数"混用的错误被 0 掩盖。
/// </summary>
internal sealed class ManualFightClock : IFightClock
{
    private sealed class Waiter
    {
        public Waiter(TimeSpan due, TimeSpan delay)
        {
            Due = due;
            Delay = delay;
        }

        public TimeSpan Due { get; }

        public TimeSpan Delay { get; }

        public TaskCompletionSource<bool> Source { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }

    private readonly object _gate = new object();
    private readonly List<Waiter> _waiters = new List<Waiter>();
    private readonly List<TimeSpan> _requests = new List<TimeSpan>();
    private TimeSpan _now = TimeSpan.FromSeconds(1000);

    public TimeSpan Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    /// <summary>每次 DelayAsync 请求的时长(含 0 与负数),按请求顺序(快照)。</summary>
    public TimeSpan[] Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>还在等的延时数。</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(delay);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            if (delay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }
            var waiter = new Waiter(_now + delay, delay);
            _waiters.Add(waiter);
            waiter.Registration = cancellationToken.Register(() =>
            {
                lock (_gate)
                {
                    _waiters.Remove(waiter);
                }
                waiter.Source.TrySetCanceled(cancellationToken);
            });
            return waiter.Source.Task;
        }
    }

    /// <summary>是否有(满足条件的)延时在等。</summary>
    public bool HasPending(Func<TimeSpan, bool> accept = null)
    {
        lock (_gate)
        {
            return _waiters.Any(w => accept == null || accept(w.Delay));
        }
    }

    /// <summary>把时间拨快一段,到点的延时全部完成(按到点先后)。</summary>
    public void Advance(TimeSpan by)
    {
        List<Waiter> due;
        lock (_gate)
        {
            _now += by;
            due = _waiters.Where(w => w.Due <= _now).OrderBy(w => w.Due).ToList();
            foreach (Waiter w in due)
            {
                _waiters.Remove(w);
            }
        }
        Complete(due);
    }

    /// <summary>
    /// 把时间拨到(满足条件的)最早到点的那个延时处,让到点的(同样满足条件的)延时完成;没有可拨的返回 false。
    /// 不满足条件的延时不会被完成,即使它已经到点。
    /// </summary>
    public bool AdvanceToNext(Func<TimeSpan, bool> accept = null)
    {
        List<Waiter> due;
        lock (_gate)
        {
            Waiter first = _waiters.Where(w => accept == null || accept(w.Delay)).OrderBy(w => w.Due).FirstOrDefault();
            if (first == null)
            {
                return false;
            }
            if (first.Due > _now)
            {
                _now = first.Due;
            }
            due = _waiters.Where(w => w.Due <= _now && (accept == null || accept(w.Delay))).OrderBy(w => w.Due).ToList();
            foreach (Waiter w in due)
            {
                _waiters.Remove(w);
            }
        }
        Complete(due);
        return true;
    }

    private static void Complete(List<Waiter> due)
    {
        foreach (Waiter w in due)
        {
            w.Registration.Dispose();
            w.Source.TrySetResult(true);
        }
    }
}

/// <summary>
/// 无头战斗驱动的测试装置:合成起始地图(怪编号 9100 起)+ 照笔记建模的假服务端 + 线程安全的假会话 + 真的战斗跑器 + 手拨假时钟。
/// 跑器与驱动都在后台线程里跑(跑器按真时间轮询,驱动按假时间等),所以装置提供"锁步"的推进方法:
/// 只在真实的跑器处理完前面的回复之后才拨动假时钟,虚拟时间不会跑在真实的跑器前面。
/// 驱动放进 <see cref="Task.Run(Func{Task})"/> 里启动:万一被改坏的驱动原地空转,卡住的是池线程而不是测试线程,测试仍能因超时失败。
/// </summary>
internal sealed class FightRig
{
    /// <summary>"不是收尾宽限"的延时:自动泵只推进这类延时(收尾宽限要留给真实的跑器自己收场)。</summary>
    public static readonly Func<TimeSpan, bool> NotFinishWait = delay => delay != OnlineFightDriver.FinishWait;

    public FightRig(
        int count = 5,
        int paceMs = 400,
        bool continueOnRefuse = false,
        IEnumerable<int> table = null,
        bool withholdClear = false,
        TimeSpan? clearWait = null,
        TimeSpan? settleWait = null,
        double runnerSettlementSeconds = 10)
    {
        Ids = CombatKit.Sequential(count);
        Map = CombatKit.Map(Ids);
        Server = new FakeCombatServer(table ?? Ids) { WithholdClear = withholdClear };
        Session = CombatFakeSession.ForServer(Server);
        OnlineDungeonCombatOptions runnerOptions = CombatKit.Options(overallSeconds: 60, settlementSeconds: runnerSettlementSeconds);
        runnerOptions.OnEvent = RecordEvent;
        Runner = new OnlineDungeonCombatRunner(Session, null, runnerOptions);
        Clock = new ManualFightClock();
        Options = new OnlineFightOptions
        {
            PaceMs = paceMs,
            ContinueOnRefuse = continueOnRefuse,
            ClearWait = clearWait ?? TimeSpan.FromSeconds(25),
            SettleWait = settleWait ?? TimeSpan.FromSeconds(25),
            Clock = Clock,
        };
        Driver = new OnlineFightDriver(Runner, Map, Options);
    }

    public int[] Ids { get; }

    public StartMapNotice Map { get; }

    public FakeCombatServer Server { get; }

    public CombatFakeSession Session { get; }

    public OnlineDungeonCombatRunner Runner { get; }

    public ManualFightClock Clock { get; }

    public OnlineFightOptions Options { get; }

    public OnlineFightDriver Driver { get; }

    public HostTestKit.LogSink Log { get; } = new HostTestKit.LogSink();

    public CancellationTokenSource Cts { get; } = new CancellationTokenSource();

    private readonly object _eventGate = new object();
    private readonly List<OnlineCombatEvent> _events = new List<OnlineCombatEvent>();

    private void RecordEvent(OnlineCombatEvent e)
    {
        lock (_eventGate)
        {
            _events.Add(e);
        }
    }

    /// <summary>跑器状态机已经发生的事件(快照;由跑器的 OnEvent 回调实时记下,线程安全)。</summary>
    public OnlineCombatEvent[] Events
    {
        get
        {
            lock (_eventGate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>等到某种事件至少出现 count 次。</summary>
    public Task WaitEvent(OnlineCombatEventKind kind, int count = 1)
    {
        return CombatKit.WaitUntil(() => Events.Count(e => e.Kind == kind) >= count, "事件 " + kind + " 至少出现 " + count + " 次");
    }

    public OnlineDungeonCombat Machine => Runner.Machine;

    /// <summary>在池线程里启动驱动,并等到它已经把跑器起起来(状态机已建好)。</summary>
    public async Task<Task<OnlineFightSummary>> Start()
    {
        Task<OnlineFightSummary> run = Task.Run(() => Driver.RunAsync(Log.Add, Cts.Token));
        await CombatKit.WaitUntil(() => Runner.Machine != null || run.IsCompleted, "驱动启动了跑器");
        return run;
    }

    /// <summary>等到驱动停在(满足条件的)假时钟延时上,即它在等时间。</summary>
    public Task WaitParked(Func<TimeSpan, bool> accept = null)
    {
        return CombatKit.WaitUntil(() => Clock.HasPending(accept ?? NotFinishWait), "驱动停在假时钟的延时上");
    }

    /// <summary>等到状态机收到的击杀回复(确认加被拒)累计到 answers 条。</summary>
    public Task WaitAnswered(int answers)
    {
        return CombatKit.WaitUntil(() => Machine.Confirmed + Machine.Refused >= answers, "击杀回复累计到 " + answers + " 条");
    }

    /// <summary>等到跑器真发出的击杀命令累计到 sent 条。</summary>
    public Task WaitSent(int sent)
    {
        return CombatKit.WaitUntil(() => Machine.Sent >= sent, "发出的击杀命令累计到 " + sent + " 条");
    }

    /// <summary>等驱动停在延时上,再把假时钟拨到最早到点的那个延时(收尾宽限除外)。</summary>
    public async Task Step()
    {
        await WaitParked();
        Assert.True(Clock.AdvanceToNext(NotFinishWait), "驱动应当停在一个可推进的延时上");
    }

    /// <summary>
    /// 锁步推进击杀阶段,直到驱动排满 posts 只、并且跑器已把第 posts 只发出去:每一步都等真实的跑器处理完前一只的回复,再拨一个节奏间隔。
    /// 只用于击杀都能发出去的场景。
    /// </summary>
    public async Task PostKills(int posts)
    {
        await WaitSent(1);
        for (int k = 1; k < posts; k++)
        {
            await WaitAnswered(k);
            await Step();
            await WaitSent(k + 1);
        }
    }

    /// <summary>
    /// 自动泵:每个真实的毫秒,若驱动停在延时上,就把假时钟拨到最早到点的那个(收尾宽限除外),直到驱动返回。
    /// 虚拟时间每个真实毫秒最多走一个等待步长,所以用很宽的等待窗口时,窗口不会在真实的跑器处理完之前耗尽。
    /// </summary>
    public async Task<OnlineFightSummary> Pump(Task<OnlineFightSummary> run, int realSeconds = 20)
    {
        await PumpUntil(() => run.IsCompleted, realSeconds);
        return await run;
    }

    /// <summary>同 <see cref="Pump"/>,但泵到条件成立就停(不要求驱动已返回)。</summary>
    public async Task PumpUntil(Func<bool> condition, int realSeconds = 20)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            Clock.AdvanceToNext(NotFinishWait);
            await Task.Delay(1);
            if (watch.Elapsed > TimeSpan.FromSeconds(realSeconds))
            {
                Assert.Fail("等待条件超时(" + realSeconds + " 秒,真实时间);驱动日志:\n" + string.Join("\n", Log.Lines));
            }
        }
    }

    /// <summary>驱动应当在很短的真实时间内返回(不需要再拨假时钟)。</summary>
    public Task<OnlineFightSummary> Done(Task<OnlineFightSummary> run, int realSeconds = 10)
    {
        return GatewayTestSupport.Bounded(run, realSeconds);
    }

    /// <summary>服务端期望的击杀流水:按本地序号 from 到 to-1 的怪编号。</summary>
    public string[] Kills(int from, int to)
    {
        var list = new List<string>();
        for (int i = from; i < to; i++)
        {
            list.Add("kill:" + Ids[i]);
        }
        return list.ToArray();
    }
}
