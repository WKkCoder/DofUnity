using System.IO.Compression;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 放弃游戏(<c>--give-up-on-failure</c>,类 1、包号 45)测试共用的合成数据:回城信号的各种形态(<c>0/23</c>、<c>0/24</c>、压缩的 <c>0/2</c>、561 信封与会话附的内层副本)。
/// 全部是合成值(包号 900 以上、编号 9100 以上),不含任何真实数据。
/// </summary>
internal static class GiveUpKit
{
    /// <summary>放弃游戏命令的包号(类 1)。</summary>
    public const ushort GiveUpId = SessionLifecyclePayloads.GiveUpGamePacketId;

    public static byte[] UserAreaPayload(ushort uniqueId = 951, byte village = 3, byte area = 7)
    {
        return new NoticePayloadBuilder().U16(uniqueId).U8(village).U8(area).U16(320).U16(210).U8(1).U8(1).ToArray();
    }

    public static byte[] AreaUsersPayload(byte village = 3, byte area = 7, ushort uniqueId = 952)
    {
        return new NoticePayloadBuilder().U8(village).U8(area).U16(1).U16(uniqueId).U16(100).U16(200).U8(0).U8(1).ToArray();
    }

    /// <summary>回城信号之一:<c>0/23 USER_AREA</c>。</summary>
    public static CombatKit.ServerFrame Noti23()
    {
        return new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaPayload());
    }

    /// <summary>回城信号之二:<c>0/24 AREA_USERS</c>。</summary>
    public static CombatKit.ServerFrame Noti24()
    {
        return new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersPayload());
    }

    /// <summary>成对的回城信号(23 在前、24 在后)。</summary>
    public static CombatKit.ServerFrame[] Pair()
    {
        return new[] { Noti23(), Noti24() };
    }

    public static byte[] RawNoti2Payload()
    {
        return new NoticePayloadBuilder().U8(2).U8(0).U8(0).U8(0).U32(0).U16(0).ToArray();
    }

    /// <summary>回城信号的单帧形态:<c>0/2</c>,载荷是线上的压缩形态(包号 2 属于压缩包号集合)。</summary>
    public static CombatKit.ServerFrame Noti2()
    {
        return new CombatKit.ServerFrame(CombatKit.Noti, 2, ZlibCompress(RawNoti2Payload()));
    }

    public static byte[] ZlibCompress(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain, 0, plain.Length);
        }
        return ms.ToArray();
    }

    /// <summary>把一帧封进真 561 信封后解码出来的帧(给假会话接缝用)。</summary>
    public static DecodedFrame Envelope(CombatKit.ServerFrame frame)
    {
        byte[] wire = HostTestKit.RealEnvelope(frame.Class, frame.Id, frame.Payload);
        var decoder = new ServerFrameDecoder();
        Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame envelope));
        return envelope;
    }

    /// <summary>会话附在信封后面的内层副本(<see cref="DecodedKind.Plain"/>)。</summary>
    public static DecodedFrame Copy(CombatKit.ServerFrame frame)
    {
        return CombatKit.DecodedPlain(frame.Class, frame.Id, frame.Payload);
    }

    /// <summary>往假会话灌一个"信封加它的内层副本"单元(真会话一次收帧把它们一起交回)。</summary>
    public static void PushEnveloped(CombatKit.ServerFrame frame, CombatFakeSession session)
    {
        session.PushUnit(Envelope(frame), Copy(frame));
    }

    /// <summary>结算后菜单汇总的合成值(给策略与收尾编排的测试用,不跑真的菜单驱动)。</summary>
    public static OnlineAfterSettleSummary Menu(OnlineAfterSettleOutcome outcome)
    {
        return new OnlineAfterSettleSummary(outcome, false, 1, null, 0, 0, 0, 0, 0, null, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>战斗汇总的合成值(给策略与收尾编排的测试用)。</summary>
    public static OnlineFightSummary Fight(
        OnlineFightOutcome outcome,
        OnlineDungeonCombatOutcome? runnerOutcome = OnlineDungeonCombatOutcome.Cancelled,
        OnlineGiveUpSummary giveUp = null,
        TimeSpan? clearDuration = null)
    {
        bool ready = outcome == OnlineFightOutcome.AllDone;
        return new OnlineFightSummary(outcome, 7, 7, 7, 7, 0, 0, ready, ready ? OnlineFightSettlement.Complete : OnlineFightSettlement.None, ready,
            ready ? OnlineCombatPhase.Settled : OnlineCombatPhase.Fighting, runnerOutcome, null, -1, 0, TimeSpan.Zero, new TimeSpan[0],
            clearDuration, giveUp);
    }

    /// <summary>放弃汇总的合成值。</summary>
    public static OnlineGiveUpSummary GiveUp(string reason, OnlineGiveUpOutcome outcome, bool sent = true)
    {
        return new OnlineGiveUpSummary(reason, outcome, sent, TimeSpan.Zero, null);
    }
}

/// <summary>
/// 第一次被问延时就抛异常、之后原样转给内层时钟的假时钟包装:用来让驱动自己在"跑器还活着"的时候出错(驱动的异常收场),
/// 这是不依赖时序、确定的做法(真实的取消与断线都有竞态)。
/// </summary>
internal sealed class FaultOnceClock : IFightClock
{
    private readonly IFightClock _inner;
    private int _armed = 1;

    public FaultOnceClock(IFightClock inner)
    {
        _inner = inner;
    }

    public TimeSpan Now => _inner.Now;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new InvalidOperationException("synthetic clock fault");
        }
        return _inner.DelayAsync(delay, cancellationToken);
    }
}

/// <summary>
/// "收帧卡死门":包在会话接缝外面。<see cref="Stick"/> 之后的每一次收帧都停在门口——返回的任务不理会取消令牌、一直不完成,
/// 直到测试 <see cref="Release"/>;跑器的循环因此卡在收帧上,驱动让它停(取消)也停不下来,只能靠驱动自己的收尾宽限放弃等它。
/// 发送与各状态原样转调内层会话。
/// </summary>
internal sealed class StuckReceiveSession : IOnlineDungeonSession
{
    private readonly IOnlineDungeonSession _inner;
    private readonly TaskCompletionSource<List<DecodedFrame>> _gate = new TaskCompletionSource<List<DecodedFrame>>(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stuck;
    private volatile bool _entered;

    public StuckReceiveSession(IOnlineDungeonSession inner)
    {
        _inner = inner;
    }

    public bool IsReady => _inner.IsReady;

    public bool PeerClosed => _inner.PeerClosed;

    public FrameError LastFrameError => _inner.LastFrameError;

    /// <summary>从现在起,下一次收帧起停在门口。</summary>
    public void Stick()
    {
        _stuck = true;
    }

    /// <summary>有收帧已经走到门口(跑器此刻正卡在里面)。</summary>
    public bool Entered => _entered;

    /// <summary>放行:卡住的收帧返回一个空结果(收尾用,别让卡住的任务留在后台)。</summary>
    public void Release()
    {
        _gate.TrySetResult(new List<DecodedFrame>());
    }

    public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
    {
        return _inner.SendAsync(packetClass, packetId, payload, cancellationToken);
    }

    public Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
    {
        if (_stuck)
        {
            _entered = true;
            return _gate.Task;
        }
        return _inner.ReceiveAsync(window, stopAfterFrames, cancellationToken);
    }
}

/// <summary>
/// 放弃游戏的驱动级测试装置:合成起始地图(怪编号 9100 起)+ 照笔记建模的假服务端 + 线程安全的假会话 + 真的战斗跑器 + 手拨假时钟,
/// 与 <see cref="FightRig"/> 同一套做法,另外:驱动选项里可以开放弃(<see cref="OnlineFightOptions.GiveUpOnFailure"/>)并设上限;
/// 服务端对放弃命令(类 1、包号 45)的回应由测试通过 <see cref="OnGiveUp"/> 给(缺省一声不吭);跑器的放弃上限放得很宽(10 分钟),
/// 所以放弃的等待只可能由驱动自己的上限(假时钟)结束;跑器的两个整段上限也默认放宽,让驱动自己的两段等待先到。
/// </summary>
internal sealed class GiveUpRig
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(25);

    private readonly bool _dropReward;
    private readonly bool _silentSettlement;
    private volatile Func<IEnumerable<CombatKit.ServerFrame>> _onGiveUp;
    private readonly object _eventGate = new object();
    private readonly List<OnlineCombatEvent> _events = new List<OnlineCombatEvent>();

    /// <param name="giveUp">驱动选项里的 <c>GiveUpOnFailure</c>;null 表示压根不设这个属性(缺省路径)。</param>
    /// <param name="wrapClock">把交给驱动的时钟再包一层(测试仍然拨 <see cref="Clock"/> 这个内层);null 为不包。</param>
    /// <param name="wrapSession">把交给跑器的会话再包一层(测试仍然查 <see cref="Session"/> 这个内层的发送记录);null 为不包。</param>
    public GiveUpRig(
        int count = 3,
        bool? giveUp = true,
        TimeSpan? giveUpWait = null,
        bool continueOnRefuse = false,
        IEnumerable<int> table = null,
        bool withholdClear = false,
        bool dropReward = false,
        bool silentSettlement = false,
        double runnerOverallSeconds = 600,
        double runnerSettlementSeconds = 600,
        Func<byte[], byte[]> unwrap = null,
        int paceMs = 400,
        Func<IFightClock, IFightClock> wrapClock = null,
        Func<IOnlineDungeonSession, IOnlineDungeonSession> wrapSession = null)
    {
        _dropReward = dropReward;
        _silentSettlement = silentSettlement;
        Ids = CombatKit.Sequential(count);
        Map = CombatKit.Map(Ids);
        Server = new FakeCombatServer(table ?? Ids) { WithholdClear = withholdClear };
        Session = CombatFakeSession.ForServer(Server);
        Session.Respond = Respond;
        OnlineDungeonCombatOptions runnerOptions = CombatKit.Options(overallSeconds: runnerOverallSeconds, settlementSeconds: runnerSettlementSeconds);
        runnerOptions.GiveUpTimeout = TimeSpan.FromMinutes(10);
        runnerOptions.OnEvent = RecordEvent;
        Runner = new OnlineDungeonCombatRunner(wrapSession != null ? wrapSession(Session) : Session, unwrap, runnerOptions);
        Clock = new ManualFightClock();
        Options = new OnlineFightOptions
        {
            PaceMs = paceMs,
            ContinueOnRefuse = continueOnRefuse,
            ClearWait = Window,
            SettleWait = Window,
            Clock = wrapClock != null ? wrapClock(Clock) : Clock,
        };
        if (giveUp.HasValue)
        {
            Options.GiveUpOnFailure = giveUp.Value;
        }
        if (giveUpWait.HasValue)
        {
            Options.GiveUpWait = giveUpWait.Value;
        }
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

    public OnlineDungeonCombat Machine => Runner.Machine;

    /// <summary>服务端对放弃命令的回应(每次收到 1/45 调用一次,返回的帧追加进收件箱);null 为一声不吭。</summary>
    public Func<IEnumerable<CombatKit.ServerFrame>> OnGiveUp
    {
        get => _onGiveUp;
        set => _onGiveUp = value;
    }

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

    private IEnumerable<CombatKit.ServerFrame> Respond(PacketClass cls, ushort id, byte[] payload)
    {
        if (cls == PacketClass.Cmd && id == GiveUpKit.GiveUpId)
        {
            Func<IEnumerable<CombatKit.ServerFrame>> handler = _onGiveUp;
            return handler != null ? handler().ToArray() : null;
        }
        if (_silentSettlement && cls == PacketClass.Cmd && id == 49)
        {
            return null;                                // 服务端对结算命令一声不吭
        }
        IEnumerable<CombatKit.ServerFrame> replies = Server.Handle(cls, id, payload);
        // 结算不完整:只回通关结果、不回通关奖励
        return _dropReward ? replies.Where(f => !(f.Class == CombatKit.Noti && f.Id == 0x23)).ToList() : replies;
    }

    /// <summary>在池线程里启动驱动,并等到它已经把跑器起起来(状态机已建好)。</summary>
    public async Task<Task<OnlineFightSummary>> Start()
    {
        Task<OnlineFightSummary> run = Task.Run(() => Driver.RunAsync(Log.Add, Cts.Token));
        await CombatKit.WaitUntil(() => Runner.Machine != null || run.IsCompleted, "驱动启动了跑器");
        return run;
    }

    /// <summary>等到驱动停在(满足条件的)假时钟延时上。</summary>
    public Task WaitParked(Func<TimeSpan, bool> accept = null)
    {
        return CombatKit.WaitUntil(() => Clock.HasPending(accept ?? FightRig.NotFinishWait), "驱动停在假时钟的延时上");
    }

    public Task WaitAnswered(int answers)
    {
        return CombatKit.WaitUntil(() => Machine.Confirmed + Machine.Refused >= answers, "击杀回复累计到 " + answers + " 条");
    }

    public Task WaitSent(int sent)
    {
        return CombatKit.WaitUntil(() => Machine.Sent >= sent, "发出的击杀命令累计到 " + sent + " 条");
    }

    /// <summary>等到跑器真把放弃命令发出去(状态机进入放弃等待)。</summary>
    public Task WaitGiveUpSent()
    {
        return CombatKit.WaitUntil(() => Machine.IsGivingUp, "跑器发出了放弃命令");
    }

    /// <summary>等驱动停在延时上,再把假时钟拨到最早到点的那个延时(收尾宽限除外)。</summary>
    public async Task Step()
    {
        await WaitParked();
        Assert.True(Clock.AdvanceToNext(FightRig.NotFinishWait), "驱动应当停在一个可推进的延时上");
    }

    /// <summary>锁步推进击杀阶段,直到驱动排满 posts 只、并且跑器已把第 posts 只发出去(只用于击杀都能发出去的场景)。</summary>
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

    /// <summary>泵到条件成立:每个真实的毫秒,若驱动停在延时上就把假时钟拨到最早到点的那个(收尾宽限除外)。</summary>
    public async Task PumpUntil(Func<bool> condition, int realSeconds = 20)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Clock.AdvanceToNext(FightRig.NotFinishWait);
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

    /// <summary>泵假时钟直到驱动返回(正常路径用)。</summary>
    public async Task<OnlineFightSummary> Pump(Task<OnlineFightSummary> run)
    {
        await PumpUntil(() => run.IsCompleted);
        return await Done(run);
    }

    // ---- 三种驱动级失败的"带到收场点"(服务端的表与参数在各自的工厂里) ----

    /// <summary>被拒即停:服务端的表里没有第 3 只(本地序号 2),它的击杀被回"失败";驱动在节奏等待到点时看见被拒、即刻收场。</summary>
    public static GiveUpRig ForRefusedStop(bool? giveUp = true, TimeSpan? giveUpWait = null, Func<byte[], byte[]> unwrap = null)
    {
        return new GiveUpRig(6, giveUp, giveUpWait, table: CombatKit.Sequential(6).Where(id => id != CombatKit.BaseId + 2), unwrap: unwrap);
    }

    /// <summary>没等到清图通知:服务端扣下清图通知,窗口(25 秒假时钟)到点。</summary>
    public static GiveUpRig ForNoClearNotice(bool? giveUp = true, TimeSpan? giveUpWait = null, Func<byte[], byte[]> unwrap = null,
        Func<IOnlineDungeonSession, IOnlineDungeonSession> wrapSession = null)
    {
        return new GiveUpRig(3, giveUp, giveUpWait, withholdClear: true, unwrap: unwrap, wrapSession: wrapSession);
    }

    /// <summary>结算不完整:服务端只回通关结果、不回通关奖励,结算窗口(25 秒假时钟)到点。</summary>
    public static GiveUpRig ForSettleIncomplete(bool? giveUp = true, TimeSpan? giveUpWait = null, Func<byte[], byte[]> unwrap = null)
    {
        return new GiveUpRig(2, giveUp, giveUpWait, dropReward: true, unwrap: unwrap);
    }

    /// <summary>
    /// 启动驱动并带到收场点:返回驱动的任务;从这一刻起驱动正要(或已经)按结局决定放弃。
    /// 只用于对应的工厂造出来的装置。
    /// </summary>
    public async Task<Task<OnlineFightSummary>> Drive(OnlineFightOutcome outcome)
    {
        Task<OnlineFightSummary> run = await Start();
        switch (outcome)
        {
            case OnlineFightOutcome.RefusedStop:
                await PostKills(3);
                await WaitAnswered(3);
                await Step();                       // 节奏等待到点:驱动醒来先看有没有被拒
                break;
            case OnlineFightOutcome.NoClearNotice:
                await PostKills(3);
                await WaitAnswered(3);
                await WaitParked();                 // 驱动已排完最后一只,停在等清图的小步上
                Clock.Advance(Window);              // 窗口正好到点
                break;
            case OnlineFightOutcome.SettleIncomplete:
                await PostKills(2);
                await PumpUntil(() => Log.Lines.Any(l => l.Contains("phase=settleWait")));
                await CombatKit.WaitUntil(() => Events.Any(e => e.Kind == OnlineCombatEventKind.SettlementArrived), "通关结果到了");
                await WaitParked();
                Clock.Advance(Window + TimeSpan.FromSeconds(1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        return run;
    }

    /// <summary>
    /// 带到放弃的等待里:驱动已按结局请求放弃、跑器已把放弃命令发出去,驱动停在假时钟上等回城信号。
    /// 服务端对放弃命令的回应要在调用之前设好(<see cref="OnGiveUp"/>)。
    /// </summary>
    public async Task<Task<OnlineFightSummary>> DriveToGiveUpWait(OnlineFightOutcome outcome)
    {
        Task<OnlineFightSummary> run = await Drive(outcome);
        await WaitGiveUpSent();
        return run;
    }
}
