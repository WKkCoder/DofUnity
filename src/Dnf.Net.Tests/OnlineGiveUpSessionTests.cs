using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// <c>--give-up-on-failure</c> 的会话层放弃(<see cref="OnlineAfterSettleDriver.GiveUpAsync(OnlineFightOutcome, Action{string}, CancellationToken)"/>)
/// 与收尾编排(<see cref="OnlineGiveUpOnFailure.FinishAsync"/>)的测试:结算后菜单阶段的失败(推进超时、选牌发送失败、菜单命令超时、等回城超时、回包带失败标志、回包畸形)触发放弃,
/// 成功回城、会话已关、异常、取消不触发;发且只发一次、等回城信号有上限、等待期间回心跳、信封与压缩内层、各种断线与取消。
/// 回城信号在这条路径上认 <c>0/2</c> 单帧或 <c>0/23 + 0/24</c> 一对(先后不限),与结算后菜单自己等回城时认的一致。
/// 合成值(包号 900 以上、编号 9100 以上),没有任何真实数据。
/// </summary>
public class OnlineGiveUpSessionTests
{
    private const ushort Pkt72 = 72;
    private const ushort Pkt73 = 73;
    private const ushort Pkt75 = 75;

    private static CombatKit.ServerFrame Reply72Ok(byte status = 1) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt72, new[] { status });

    private static CombatKit.ServerFrame Reply73(short s0, short s1, short s2, short s3, byte status = 1) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt73, new NoticePayloadBuilder()
            .U8(status)
            .U16(unchecked((ushort)s0))
            .U16(unchecked((ushort)s1))
            .U16(unchecked((ushort)s2))
            .U16(unchecked((ushort)s3))
            .ToArray());

    private static CombatKit.ServerFrame Reply75Ok(byte flag = 1, byte cmd = 2) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt75, new byte[] { 1, flag, cmd });

    private static CombatKit.ServerFrame Reply75Fail(byte errorCode, byte cmd = 2) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt75, new byte[] { 0, errorCode, cmd });

    private static OnlineAfterSettleDriver Direct(IOnlineDungeonSession session, ManualFightClock clock = null, TimeSpan? giveUpWait = null)
    {
        var options = new OnlineAfterSettleOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            Clock = clock ?? new ManualFightClock(),
        };
        if (giveUpWait.HasValue)
        {
            options.GiveUpWaitTimeout = giveUpWait.Value;
        }
        return new OnlineAfterSettleDriver(session, options);
    }

    private static Task<OnlineGiveUpSummary> Give(OnlineAfterSettleDriver driver, OnlineFightOutcome reason, Action<string> log = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => driver.GiveUpAsync(reason, log, cancellationToken));
    }

    private static string Line(string token, object reason)
    {
        return OnlineFightLog.GiveUpStep(token, reason.ToString());
    }

    private static ushort[] SentIds(CombatFakeSession session)
    {
        return session.Sent.Select(x => x.Id).ToArray();
    }

    // 回城信号没来、服务端不吭声:在假时钟上等到上限差 50 毫秒时确认还没收场,再拨最后 50 毫秒
    private static async Task<OnlineGiveUpSummary> WaitOutTheCap(ManualFightClock clock, Task<OnlineGiveUpSummary> run, TimeSpan cap)
    {
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等回城信号");
        clock.Advance(cap - TimeSpan.FromMilliseconds(50));
        await CombatKit.WaitUntil(() => clock.HasPending(), "上限还差 50 毫秒");
        Assert.False(run.IsCompleted, "上限还差 50 毫秒,不能收场");
        clock.Advance(TimeSpan.FromMilliseconds(50));
        return await GatewayTestSupport.Bounded(run, 10);
    }

    // ======== 结算后菜单阶段的失败:一个场景一个结局 ========

    private sealed class MenuStage
    {
        public CombatFakeSession Session { get; } = new CombatFakeSession();

        public ManualFightClock Clock { get; } = new ManualFightClock();

        public HostTestKit.LogSink Log { get; } = new HostTestKit.LogSink();

        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();

        public OnlineAfterSettleSummary Summary { get; set; }
    }

    // 真的跑结算后菜单驱动,直到它以 outcome 收场;之后会话还是活的(服务端对放弃命令回 0/23 + 0/24),等着放弃那一步接手。
    private static async Task<MenuStage> RunMenuTo(OnlineAfterSettleOutcome outcome)
    {
        var stage = new MenuStage();
        CombatFakeSession session = stage.Session;
        Func<PacketClass, ushort, byte[], IEnumerable<CombatKit.ServerFrame>> menu;
        Action<string> onLine = null;
        TimeSpan? advance = null;
        bool cancelWhenParked = false;

        switch (outcome)
        {
            case OnlineAfterSettleOutcome.ScoreScrollTimeout:
                menu = (c, id, p) => null;
                advance = TimeSpan.FromSeconds(5);
                break;
            case OnlineAfterSettleOutcome.CardRightTimeout:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : null;
                advance = TimeSpan.FromSeconds(5);
                break;
            case OnlineAfterSettleOutcome.SelectCardSendFailed:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(0, 1, -1, -1) } : null);
                session.StatusOfSend = index => index == 2 ? FrameStatus.EncryptFailed : FrameStatus.Ok;     // 第 0、1、2 次发送是 72、73、74
                break;
            case OnlineAfterSettleOutcome.EplpTimeout:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, -1, -1, -1) } : null);
                advance = TimeSpan.FromSeconds(5);
                break;
            case OnlineAfterSettleOutcome.ReturnTimeout:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, -1, -1, -1) } : (id == Pkt75 ? new[] { Reply75Ok() } : null));
                advance = TimeSpan.FromSeconds(6);
                break;
            case OnlineAfterSettleOutcome.ReplyFailed:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, -1, -1, -1) } : (id == Pkt75 ? new[] { Reply75Fail(8) } : null));
                break;
            case OnlineAfterSettleOutcome.ReplyMalformed:
                menu = (c, id, p) => id == Pkt72 ? new[] { new CombatKit.ServerFrame(CombatKit.Cmd, Pkt72, new byte[0]) } : null;
                break;
            case OnlineAfterSettleOutcome.ReturnedToTown:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, -1, -1, -1) } : (id == Pkt75 ? new[] { Reply75Ok(), GiveUpKit.Noti2() } : null));
                break;
            case OnlineAfterSettleOutcome.SessionClosed:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : null;
                onLine = line =>
                {
                    if (line.Contains("step=72 "))
                    {
                        session.Closed = true;
                    }
                };
                break;
            case OnlineAfterSettleOutcome.Faulted:
                menu = (c, id, p) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(0, 1, -1, -1) } : null);
                session.FailureOfSend = index => index == 2 ? new InvalidOperationException("secret-fault-31337") : null;
                break;
            case OnlineAfterSettleOutcome.Cancelled:
                menu = (c, id, p) => null;
                cancelWhenParked = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        session.Respond = (c, id, p) => c == PacketClass.Cmd && id == 45 ? GiveUpKit.Pair() : menu(c, id, p);
        var driver = new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { PollInterval = TimeSpan.FromMilliseconds(50), Clock = stage.Clock });
        Task<OnlineAfterSettleSummary> task = Task.Run(() => driver.RunAsync(line =>
        {
            stage.Log.Add(line);
            onLine?.Invoke(line);
        }, stage.Cts.Token));

        if (advance.HasValue)
        {
            await CombatKit.WaitUntil(() => stage.Clock.HasPending(), "停在等回包");
            stage.Clock.Advance(advance.Value);
        }
        else if (cancelWhenParked)
        {
            await CombatKit.WaitUntil(() => stage.Clock.HasPending(), "停在等回包");
            stage.Cts.Cancel();
        }
        stage.Summary = await GatewayTestSupport.Bounded(task, 10);
        Assert.Equal(outcome, stage.Summary.Outcome);
        return stage;
    }

    public static TheoryData<OnlineAfterSettleOutcome> MenuTriggers()
    {
        return new TheoryData<OnlineAfterSettleOutcome>
        {
            OnlineAfterSettleOutcome.ScoreScrollTimeout,
            OnlineAfterSettleOutcome.CardRightTimeout,
            OnlineAfterSettleOutcome.SelectCardSendFailed,
            OnlineAfterSettleOutcome.EplpTimeout,
            OnlineAfterSettleOutcome.ReturnTimeout,
            OnlineAfterSettleOutcome.ReplyFailed,
            OnlineAfterSettleOutcome.ReplyMalformed,
        };
    }

    public static TheoryData<OnlineAfterSettleOutcome> MenuNonTriggers()
    {
        return new TheoryData<OnlineAfterSettleOutcome>
        {
            OnlineAfterSettleOutcome.ReturnedToTown,
            OnlineAfterSettleOutcome.SessionClosed,
            OnlineAfterSettleOutcome.Faulted,
            OnlineAfterSettleOutcome.Cancelled,
        };
    }

    [Theory]
    [MemberData(nameof(MenuTriggers))]
    public async Task A_menu_stage_failure_sends_exactly_one_give_up_and_reports_returned(OnlineAfterSettleOutcome outcome)
    {
        MenuStage stage = await RunMenuTo(outcome);
        int sentBefore = stage.Session.Sent.Length;
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            OnlineGiveUpOnFailure.FinishAsync(true, Direct(stage.Session, stage.Clock), GiveUpKit.Fight(OnlineFightOutcome.AllDone, OnlineDungeonCombatOutcome.ReturnReady), stage.Summary, log.Add, stage.Cts.Token), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal(outcome.ToString(), g.Reason);
        Assert.Equal(1, stage.Session.Commands(45));
        Assert.Equal(sentBefore + 1, stage.Session.Sent.Length);        // 放弃只多发了这一条
        Assert.Equal(45, stage.Session.Sent.Last().Id);
        Assert.Equal(SessionLifecyclePayloads.BuildGiveUpGame(), stage.Session.Sent.Last().Payload);
        Assert.Equal(new[] { Line("sent", outcome), Line("returned", outcome) }, log.Lines);
        // 菜单阶段的退出码仍由菜单结局决定(字面值),放弃不碰它
        Assert.Equal(ExpectedMenuExitCode(outcome), OnlineAfterSettleExitCodes.ForOutcome(stage.Summary.Outcome));
    }

    private static int ExpectedMenuExitCode(OnlineAfterSettleOutcome outcome)
    {
        switch (outcome)
        {
            case OnlineAfterSettleOutcome.ScoreScrollTimeout:
            case OnlineAfterSettleOutcome.CardRightTimeout:
                return 16;
            case OnlineAfterSettleOutcome.EplpTimeout:
                return 17;
            case OnlineAfterSettleOutcome.ReturnTimeout:
                return 18;
            case OnlineAfterSettleOutcome.ReplyFailed:
                return 19;
            case OnlineAfterSettleOutcome.ReplyMalformed:
                return 20;
            case OnlineAfterSettleOutcome.SelectCardSendFailed:
                return 23;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    [Theory]
    [MemberData(nameof(MenuNonTriggers))]
    public async Task A_menu_stage_that_ends_any_other_way_never_gives_up(OnlineAfterSettleOutcome outcome)
    {
        MenuStage stage = await RunMenuTo(outcome);
        int sentBefore = stage.Session.Sent.Length;
        var log = new HostTestKit.LogSink();
        OnlineAfterSettleDriver giveUpDriver = Direct(stage.Session, stage.Clock);

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            OnlineGiveUpOnFailure.FinishAsync(true, giveUpDriver, GiveUpKit.Fight(OnlineFightOutcome.AllDone, OnlineDungeonCombatOutcome.ReturnReady), stage.Summary, log.Add, stage.Cts.Token), 10);

        Assert.Null(g);
        Assert.Equal(sentBefore, stage.Session.Sent.Length);
        Assert.Equal(0, stage.Session.Commands(45));
        Assert.Empty(log.Lines);
        Assert.DoesNotContain("secret", string.Join("\n", stage.Log.Lines));
        // 没有放弃就没有占用放弃驱动的一次性开关:之后它还能正常放弃一次
        OnlineGiveUpSummary later = await GatewayTestSupport.Bounded(Give(giveUpDriver, OnlineFightOutcome.NoClearNotice), 10);
        // 会话已经关了的那一种只能是"什么都没发";其余的正常放弃并等到回城信号
        Assert.Equal(outcome == OnlineAfterSettleOutcome.SessionClosed ? OnlineGiveUpOutcome.Skipped : OnlineGiveUpOutcome.Returned, later.Outcome);
    }

    [Fact]
    public async Task A_menu_stage_give_up_waits_the_default_cap_on_a_silent_server_and_never_resends()
    {
        MenuStage stage = await RunMenuTo(OnlineAfterSettleOutcome.ReplyFailed);
        stage.Session.Respond = (c, id, p) => null;                     // 服务端对放弃命令一声不吭
        var log = new HostTestKit.LogSink();
        Task<OnlineGiveUpSummary> run = Task.Run(() => OnlineGiveUpOnFailure.FinishAsync(
            true, Direct(stage.Session, stage.Clock), GiveUpKit.Fight(OnlineFightOutcome.AllDone, OnlineDungeonCombatOutcome.ReturnReady), stage.Summary, log.Add, stage.Cts.Token));

        OnlineGiveUpSummary g = await WaitOutTheCap(stage.Clock, run, TimeSpan.FromSeconds(15));

        Assert.Equal(OnlineGiveUpOutcome.Timeout, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal("ReplyFailed", g.Reason);
        Assert.Equal(1, stage.Session.Commands(45));
        Assert.Equal(new[] { Line("sent", OnlineAfterSettleOutcome.ReplyFailed), Line("timeout", OnlineAfterSettleOutcome.ReplyFailed) }, log.Lines);
    }

    // ======== 回城信号的形态 ========

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_pair_of_notices_returns_in_either_order(bool reversed)
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? (reversed ? new[] { GiveUpKit.Noti24(), GiveUpKit.Noti23() } : GiveUpKit.Pair()) : null;
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.RefusedStop, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.True(g.ReturnedToTown);
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.RefusedStop), Line("returned", OnlineFightOutcome.RefusedStop) }, log.Lines);
    }

    [Fact]
    public async Task A_lone_0_2_frame_returns()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? new[] { GiveUpKit.Noti2() } : null;

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
    }

    [Theory]
    [InlineData(0)]     // 只有 0/23
    [InlineData(1)]     // 只有 0/24
    [InlineData(2)]     // 0/23 来了两次
    [InlineData(3)]     // 0/24 来了两次
    public async Task Only_one_of_the_pair_is_not_a_return_signal_and_the_wait_runs_to_the_cap(int shape)
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        CombatKit.ServerFrame[] frames =
            shape == 0 ? new[] { GiveUpKit.Noti23() }
            : shape == 1 ? new[] { GiveUpKit.Noti24() }
            : shape == 2 ? new[] { GiveUpKit.Noti23(), GiveUpKit.Noti23() }
            : new[] { GiveUpKit.Noti24(), GiveUpKit.Noti24() };
        session.Respond = (c, id, p) => id == 45 ? frames : null;
        var log = new HostTestKit.LogSink();
        var cap = TimeSpan.FromSeconds(2);

        Task<OnlineGiveUpSummary> run = Give(Direct(session, clock, cap), OnlineFightOutcome.SettleIncomplete, log.Add);
        OnlineGiveUpSummary g = await WaitOutTheCap(clock, run, cap);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.SettleIncomplete), Line("timeout", OnlineFightOutcome.SettleIncomplete) }, log.Lines);
    }

    [Fact]
    public async Task A_lone_0_2_that_arrives_while_waiting_returns()
    {
        // 命令发出、服务端先不吭声,等待中途才到的 0/2 照样算回城信号
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => null;
        var cap = TimeSpan.FromSeconds(3);
        Task<OnlineGiveUpSummary> run = Give(Direct(session, clock, cap), OnlineFightOutcome.NoClearNotice);
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等回城信号");
        Assert.False(run.IsCompleted);

        session.Push(GiveUpKit.Noti2());
        clock.Advance(TimeSpan.FromMilliseconds(50));       // 驱动在假时钟的小步上醒来,读到它
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(1, session.Commands(45));
    }

    [Fact]
    public async Task The_pair_inside_561_envelopes_with_inner_copies_returns_and_nothing_else_is_sent()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) =>
        {
            if (id == 45)
            {
                GiveUpKit.PushEnveloped(GiveUpKit.Noti23(), session);
                GiveUpKit.PushEnveloped(GiveUpKit.Noti24(), session);
            }
            return null;
        };

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
    }

    [Fact]
    public async Task A_compressed_0_2_inside_a_561_envelope_returns_through_the_inner_copy_the_session_appends()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) =>
        {
            if (id == 45)
            {
                GiveUpKit.PushEnveloped(GiveUpKit.Noti2(), session);   // 信封里是压缩帧:驱动不拆,等会话附上的内层副本
            }
            return null;
        };

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
    }

    [Fact]
    public async Task Over_a_real_session_the_pair_in_561_envelopes_returns()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class == PacketClass.Cmd && frame.Id == 45)
            {
                return new[]
                {
                    HostTestKit.RealEnvelope((byte)PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, GiveUpKit.UserAreaPayload()),
                    HostTestKit.RealEnvelope((byte)PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, GiveUpKit.AreaUsersPayload()),
                };
            }
            return null;
        };
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(session).GiveUpAsync(OnlineFightOutcome.NoClearNotice, log.Add), 20);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.True(g.Sent);
        DungeonEntryClientFrame[] sent = pipe.ClientFrames.Where(f => f.Class == PacketClass.Cmd).ToArray();
        Assert.Equal(new ushort[] { 45 }, sent.Select(f => f.Id).ToArray());
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.NoClearNotice), Line("returned", OnlineFightOutcome.NoClearNotice) }, log.Lines);
    }

    [Fact]
    public async Task Over_a_real_session_a_compressed_0_2_in_a_561_envelope_returns()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class == PacketClass.Cmd && frame.Id == 45)
            {
                // 包号 2 属于压缩包号集合:真会话把信封里的压缩内层展开后附在后面交付
                return new[] { HostTestKit.RealEnvelope((byte)PacketClass.Noti, 2, GiveUpKit.ZlibCompress(GiveUpKit.RawNoti2Payload())) };
            }
            return null;
        };

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(session).GiveUpAsync(OnlineFightOutcome.SettleIncomplete), 20);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(new ushort[] { 45 }, pipe.ClientFrames.Where(f => f.Class == PacketClass.Cmd).Select(f => f.Id).ToArray());
    }

    // ======== 只发一次:心跳、迟到帧、重复信号 ========

    [Fact]
    public async Task Heartbeats_are_answered_one_for_one_and_are_not_a_return_signal()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45
            ? new[] { CombatKit.Heartbeat(), CombatKit.Heartbeat(), GiveUpKit.Noti23(), CombatKit.Heartbeat(), GiveUpKit.Noti24() }
            : null;

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(new ushort[] { 45, 0, 0, 0 }, SentIds(session));
        (PacketClass Class, ushort Id, byte[] Payload)[] replies = session.Sent.Where(x => x.Id == OnlineHeartbeat.ReplyPacketId).ToArray();
        Assert.Equal(3, replies.Length);
        Assert.All(replies, r => Assert.Equal(new byte[8], r.Payload));
        Assert.All(replies, r => Assert.Equal(PacketClass.Cmd, r.Class));
    }

    [Fact]
    public async Task Late_settlement_frames_and_unrelated_frames_never_make_the_driver_send_anything_but_the_one_command()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45
            ? new[] { CombatKit.Clear(), CombatKit.Result(), CombatKit.Reward(), CombatKit.Unrelated(903), GiveUpKit.Noti23(), GiveUpKit.Noti23(), GiveUpKit.Noti24(), GiveUpKit.Noti24(), CombatKit.Heartbeat() }
            : null;

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.SettleIncomplete), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        // 命令发出之后只有这一条;信号凑齐就收场,后面还没读的重复帧与心跳不再碰
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
        Assert.Equal(0, session.Commands(49));
        Assert.Equal(0, session.Commands(42));
    }

    [Fact]
    public async Task A_second_give_up_on_the_same_driver_is_refused_and_sends_nothing()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        OnlineAfterSettleDriver driver = Direct(session);
        OnlineGiveUpSummary first = await GatewayTestSupport.Bounded(Give(driver, OnlineFightOutcome.RefusedStop), 10);
        Assert.Equal(OnlineGiveUpOutcome.Returned, first.Outcome);

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.GiveUpAsync(OnlineFightOutcome.RefusedStop));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.GiveUpAsync(OnlineAfterSettleOutcome.ReturnTimeout));

        Assert.Equal(1, session.Commands(45));
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
    }

    [Theory]
    [InlineData(OnlineFightOutcome.AllDone)]
    [InlineData(OnlineFightOutcome.SessionClosed)]
    [InlineData(OnlineFightOutcome.Faulted)]
    [InlineData(OnlineFightOutcome.Cancelled)]
    public async Task A_fight_outcome_that_does_not_trigger_returns_null_sends_nothing_and_keeps_the_driver_unused(OnlineFightOutcome outcome)
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        OnlineAfterSettleDriver driver = Direct(session);
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary none = await GatewayTestSupport.Bounded(Give(driver, outcome, log.Add), 10);

        Assert.Null(none);
        Assert.Empty(session.Sent);
        Assert.Empty(log.Lines);
        // 没有占用一次性开关
        OnlineGiveUpSummary later = await GatewayTestSupport.Bounded(Give(driver, OnlineFightOutcome.NoClearNotice), 10);
        Assert.Equal(OnlineGiveUpOutcome.Returned, later.Outcome);
        Assert.Equal(1, session.Commands(45));
    }

    [Theory]
    [InlineData(OnlineFightOutcome.RefusedStop)]
    [InlineData(OnlineFightOutcome.NoClearNotice)]
    [InlineData(OnlineFightOutcome.SettleIncomplete)]
    public async Task Each_triggering_fight_outcome_sends_exactly_one_command_on_the_session_path(OnlineFightOutcome outcome)
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), outcome, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(outcome.ToString(), g.Reason);
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
        Assert.Equal(new[] { Line("sent", outcome), Line("returned", outcome) }, log.Lines);
    }

    // ======== 上限 ========

    [Fact]
    public async Task The_default_cap_is_fifteen_seconds()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        Assert.Equal(TimeSpan.FromSeconds(15), new OnlineAfterSettleOptions().GiveUpWaitTimeout);

        Task<OnlineGiveUpSummary> run = Give(Direct(session, clock), OnlineFightOutcome.NoClearNotice);
        OnlineGiveUpSummary g = await WaitOutTheCap(clock, run, TimeSpan.FromSeconds(15));

        Assert.Equal(OnlineGiveUpOutcome.Timeout, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal(TimeSpan.FromSeconds(15), g.Elapsed);
        Assert.Equal(1, session.Commands(45));              // 超时了也绝不重发
    }

    [Fact]
    public async Task A_custom_cap_is_honoured_to_the_step()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        var cap = TimeSpan.FromSeconds(2);

        Task<OnlineGiveUpSummary> run = Give(Direct(session, clock, cap), OnlineFightOutcome.NoClearNotice);
        OnlineGiveUpSummary g = await WaitOutTheCap(clock, run, cap);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, g.Outcome);
        Assert.Equal(cap, g.Elapsed);
        Assert.Equal(1, session.Commands(45));
    }

    [Fact]
    public async Task A_return_signal_just_inside_the_cap_still_counts()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        var cap = TimeSpan.FromSeconds(2);

        Task<OnlineGiveUpSummary> run = Give(Direct(session, clock, cap), OnlineFightOutcome.NoClearNotice);
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等回城信号");
        clock.Advance(cap - TimeSpan.FromMilliseconds(50));
        await CombatKit.WaitUntil(() => clock.HasPending(), "上限还差 50 毫秒");
        Assert.False(run.IsCompleted);

        session.Push(GiveUpKit.Pair());
        clock.Advance(TimeSpan.FromMilliseconds(50));       // 最后一个小步:到点那一次的最后一眼也要把收件箱里的帧读完
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(1, session.Commands(45));
    }

    [Fact]
    public async Task The_cap_is_real_time_when_no_clock_is_injected()
    {
        // 没注入时钟:用系统时钟,短上限真的等满然后超时(不会无限等,也不会一上来就超时)。
        // 机器很忙时真实的延时会被拖长,所以"上限之内"那一半用宽松的 5 秒上限(只证明没有一上来就超时),
        // "等满上限"那一半的断言只依赖已用时间下界(拖得再长也只会更大),都不怕负载。
        var session = new CombatFakeSession();
        var driver = new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions
        {
            GiveUpWaitTimeout = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(20),
        });

        Task<OnlineGiveUpSummary> run = Give(driver, OnlineFightOutcome.NoClearNotice);
        await CombatKit.WaitUntil(() => session.Commands(45) == 1, "命令发出");
        await Task.Delay(150);
        Assert.False(run.IsCompleted, "5 秒的上限,150 毫秒时不能收场");
        session.Push(GiveUpKit.Pair());                      // 上限之内到的信号照样算
        OnlineGiveUpSummary returned = await GatewayTestSupport.Bounded(run, 10);
        Assert.Equal(OnlineGiveUpOutcome.Returned, returned.Outcome);

        var silentSession = new CombatFakeSession();
        var silent = new OnlineAfterSettleDriver(silentSession, new OnlineAfterSettleOptions
        {
            GiveUpWaitTimeout = TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(20),
        });
        OnlineGiveUpSummary timedOut = await GatewayTestSupport.Bounded(Give(silent, OnlineFightOutcome.NoClearNotice), 10);
        Assert.Equal(OnlineGiveUpOutcome.Timeout, timedOut.Outcome);
        Assert.True(timedOut.Elapsed >= TimeSpan.FromMilliseconds(250), "等满了上限:" + timedOut.Elapsed);
        Assert.Equal(1, silentSession.Commands(45));
    }

    // ======== 断线、发送失败、异常、取消 ========

    [Fact]
    public async Task A_connection_that_is_already_closed_is_skipped_with_nothing_on_the_wire()
    {
        var session = new CombatFakeSession { Closed = true };
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.Skipped, g.Outcome);
        Assert.False(g.Sent);
        Assert.Empty(session.Sent);
        Assert.Equal(new[] { Line("skipped", OnlineFightOutcome.NoClearNotice) }, log.Lines);
    }

    [Fact]
    public async Task A_framing_fault_before_sending_is_skipped_with_nothing_on_the_wire()
    {
        var session = new CombatFakeSession { Fault = FrameError.LengthAboveMaximum };

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.RefusedStop), 10);

        Assert.Equal(OnlineGiveUpOutcome.Skipped, g.Outcome);
        Assert.False(g.Sent);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task A_connection_that_closes_during_the_wait_is_reported_closed()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) =>
        {
            if (id == 45)
            {
                session.Closed = true;
            }
            return null;
        };
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.Closed, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.NoClearNotice), Line("closed", OnlineFightOutcome.NoClearNotice) }, log.Lines);
    }

    [Fact]
    public async Task A_framing_fault_during_the_wait_is_reported_closed()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) =>
        {
            if (id == 45)
            {
                session.Fault = FrameError.LengthAboveMaximum;
            }
            return null;
        };

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.SettleIncomplete), 10);

        Assert.Equal(OnlineGiveUpOutcome.Closed, g.Outcome);
        Assert.True(g.Sent);
    }

    [Fact]
    public async Task A_command_that_cannot_be_encoded_is_send_failed_and_is_not_retried()
    {
        var session = new CombatFakeSession { StatusOfSend = index => FrameStatus.EncryptFailed };
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.SendFailed, g.Outcome);
        Assert.False(g.Sent);
        Assert.Equal(1, session.Commands(45));              // 试了一次,没有再试
        Assert.Single(session.Sent);
        Assert.Equal(new[] { Line("sendfailed", OnlineFightOutcome.NoClearNotice) }, log.Lines);
    }

    [Theory]
    [InlineData(0)]     // IOException
    [InlineData(1)]     // ObjectDisposedException
    public async Task A_write_that_fails_because_the_connection_is_gone_is_send_failed_and_is_not_retried(int kind)
    {
        var session = new CombatFakeSession();
        session.FailureOfSend = index => kind == 0 ? new IOException("secret-gone-1") : (Exception)new ObjectDisposedException("secret-gone-2");
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.SendFailed, g.Outcome);
        Assert.False(g.Sent);
        Assert.Equal(1, session.Commands(45));
        Assert.DoesNotContain("secret", string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task Any_other_exception_is_faulted_and_only_its_type_name_is_kept()
    {
        var session = new CombatFakeSession { FailureOfSend = index => new InvalidOperationException("secret-fault-4242") };
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.Faulted, g.Outcome);
        Assert.Equal("InvalidOperationException", g.FaultType);
        Assert.False(g.Sent);
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(new[] { Line("faulted", OnlineFightOutcome.NoClearNotice) }, log.Lines);
        Assert.DoesNotContain("secret", string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task A_heartbeat_reply_that_cannot_be_sent_ends_the_wait_as_faulted_without_a_second_give_up()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? new[] { CombatKit.Heartbeat(), GiveUpKit.Noti23(), GiveUpKit.Noti24() } : null;
        session.StatusOfSend = index => index == 1 ? FrameStatus.EncryptFailed : FrameStatus.Ok;     // 第 1 次发送是心跳回包

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice), 10);

        Assert.Equal(OnlineGiveUpOutcome.Faulted, g.Outcome);
        Assert.True(g.Sent);
        Assert.Equal(1, session.Commands(45));
    }

    [Theory]
    [InlineData(0)]     // IOException
    [InlineData(1)]     // ObjectDisposedException
    public async Task A_connection_that_breaks_while_answering_a_heartbeat_after_the_give_up_was_sent_is_closed_not_send_failed(int kind)
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? new[] { CombatKit.Heartbeat() } : null;
        // 第 0 次发送是放弃命令(成功发出),第 1 次是心跳回包:写它的时候连接已经没了
        session.FailureOfSend = index => index == 1
            ? (kind == 0 ? new IOException("secret-gone-1") : (Exception)new ObjectDisposedException("secret-gone-2"))
            : null;
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add), 10);

        Assert.Equal(OnlineGiveUpOutcome.Closed, g.Outcome);       // 命令发出之后断的,不是"命令没发出去"
        Assert.True(g.Sent);
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(1, session.Commands((int)OnlineHeartbeat.ReplyPacketId));      // 心跳回包也只试了一次
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.NoClearNotice), Line("closed", OnlineFightOutcome.NoClearNotice) }, log.Lines);
        Assert.DoesNotContain("secret", string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_sends_nothing_and_reports_cancelled()
    {
        var session = new CombatFakeSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(Give(Direct(session), OnlineFightOutcome.NoClearNotice, log.Add, cts.Token), 10);

        Assert.Equal(OnlineGiveUpOutcome.Cancelled, g.Outcome);
        Assert.False(g.Sent);
        Assert.Empty(session.Sent);
        Assert.Equal(new[] { Line("cancelled", OnlineFightOutcome.NoClearNotice) }, log.Lines);
    }

    [Fact]
    public async Task A_user_abort_during_the_wait_stops_at_once_without_touching_the_clock_or_resending()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        using var cts = new CancellationTokenSource();
        var log = new HostTestKit.LogSink();

        Task<OnlineGiveUpSummary> run = Give(Direct(session, clock), OnlineFightOutcome.NoClearNotice, log.Add, cts.Token);
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等回城信号");
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.NoClearNotice) }, log.Lines);      // 等待期间 sent 已经在日志里,不是收场时才补

        cts.Cancel();                                       // 不拨假时钟:取消必须自己把等待打断
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineGiveUpOutcome.Cancelled, g.Outcome);
        Assert.True(g.Sent);                                // 命令已经发出去了
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(0, clock.Pending);
        Assert.Equal(new[] { Line("sent", OnlineFightOutcome.NoClearNotice), Line("cancelled", OnlineFightOutcome.NoClearNotice) }, log.Lines);
    }

    [Fact]
    public async Task A_log_callback_that_throws_does_not_stop_the_give_up()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;

        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            Give(Direct(session), OnlineFightOutcome.NoClearNotice, _ => throw new InvalidOperationException("broken sink")), 10);

        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(1, session.Commands(45));
    }

    // ======== 收尾编排 FinishAsync ========

    [Fact]
    public async Task Not_enabled_does_nothing_for_any_failed_run_and_leaves_the_session_alone()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        OnlineAfterSettleDriver driver = Direct(session);
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary a = await OnlineGiveUpOnFailure.FinishAsync(false, driver, GiveUpKit.Fight(OnlineFightOutcome.NoClearNotice, OnlineDungeonCombatOutcome.Timeout), null, log.Add, CancellationToken.None);
        OnlineGiveUpSummary b = await OnlineGiveUpOnFailure.FinishAsync(false, driver, GiveUpKit.Fight(OnlineFightOutcome.AllDone, OnlineDungeonCombatOutcome.ReturnReady), GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnTimeout), log.Add, CancellationToken.None);
        OnlineGiveUpSummary c = await OnlineGiveUpOnFailure.FinishAsync(false, driver, GiveUpKit.Fight(OnlineFightOutcome.RefusedStop, runnerOutcome: null), null, log.Add, CancellationToken.None);

        Assert.Null(a);
        Assert.Null(b);
        Assert.Null(c);
        Assert.Empty(session.Sent);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task Not_enabled_does_not_even_look_at_its_arguments()
    {
        Assert.Null(await OnlineGiveUpOnFailure.FinishAsync(false, null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task When_enabled_the_driver_and_the_fight_summary_are_required()
    {
        var driver = Direct(new CombatFakeSession());
        await Assert.ThrowsAsync<ArgumentNullException>(() => OnlineGiveUpOnFailure.FinishAsync(true, null, GiveUpKit.Fight(OnlineFightOutcome.AllDone), null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OnlineGiveUpOnFailure.FinishAsync(true, driver, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_give_up_already_made_by_the_fight_driver_is_returned_unchanged_and_nothing_is_sent()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        OnlineGiveUpSummary made = GiveUpKit.GiveUp("NoClearNotice", OnlineGiveUpOutcome.Timeout);
        OnlineFightSummary fight = GiveUpKit.Fight(OnlineFightOutcome.NoClearNotice, OnlineDungeonCombatOutcome.Cancelled, made);
        OnlineAfterSettleDriver driver = Direct(session);
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary first = await OnlineGiveUpOnFailure.FinishAsync(true, driver, fight, null, log.Add, CancellationToken.None);
        // 即使同时有触发放弃的菜单结局(实际不会同时出现),也不会再发第二次
        OnlineGiveUpSummary second = await OnlineGiveUpOnFailure.FinishAsync(true, driver, fight, GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnTimeout), log.Add, CancellationToken.None);

        Assert.Same(made, first);
        Assert.Same(made, second);
        Assert.Empty(session.Sent);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task The_menu_outcome_decides_when_a_menu_summary_is_present()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        var log = new HostTestKit.LogSink();

        // 菜单成功回城:战斗汇总再难看也不放弃
        OnlineAfterSettleDriver ok = Direct(session);
        OnlineGiveUpSummary none = await OnlineGiveUpOnFailure.FinishAsync(true, ok, GiveUpKit.Fight(OnlineFightOutcome.NoClearNotice, OnlineDungeonCombatOutcome.Timeout), GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnedToTown), log.Add, CancellationToken.None);
        Assert.Null(none);
        Assert.Empty(session.Sent);

        // 菜单等回城超时:按菜单结局放弃,reason 是菜单结局名
        OnlineAfterSettleDriver failed = Direct(session);
        OnlineGiveUpSummary g = await OnlineGiveUpOnFailure.FinishAsync(true, failed, GiveUpKit.Fight(OnlineFightOutcome.AllDone, OnlineDungeonCombatOutcome.ReturnReady), GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnTimeout), log.Add, CancellationToken.None);
        Assert.Equal("ReturnTimeout", g.Reason);
        Assert.Equal(OnlineGiveUpOutcome.Returned, g.Outcome);
        Assert.Equal(1, session.Commands(45));
    }

    [Theory]
    [InlineData(OnlineFightOutcome.RefusedStop, true)]
    [InlineData(OnlineFightOutcome.NoClearNotice, true)]
    [InlineData(OnlineFightOutcome.SettleIncomplete, true)]
    [InlineData(OnlineFightOutcome.AllDone, false)]
    [InlineData(OnlineFightOutcome.SessionClosed, false)]
    [InlineData(OnlineFightOutcome.Faulted, false)]
    [InlineData(OnlineFightOutcome.Cancelled, false)]
    public async Task Without_a_menu_summary_the_fight_outcome_decides(OnlineFightOutcome outcome, bool expectSend)
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        var log = new HostTestKit.LogSink();

        // 跑器已经自己收场(没有驱动级放弃):会话层接手;取消的结局用活的令牌,证明不是因为令牌才没发
        OnlineGiveUpSummary g = await GatewayTestSupport.Bounded(
            OnlineGiveUpOnFailure.FinishAsync(true, Direct(session), GiveUpKit.Fight(outcome, OnlineDungeonCombatOutcome.Timeout), null, log.Add, CancellationToken.None), 10);

        Assert.Equal(expectSend, g != null);
        Assert.Equal(expectSend ? 1 : 0, session.Commands(45));
        if (expectSend)
        {
            Assert.Equal(outcome.ToString(), g.Reason);
            Assert.Equal(new[] { Line("sent", outcome), Line("returned", outcome) }, log.Lines);
        }
        else
        {
            Assert.Empty(log.Lines);
        }
    }

    [Fact]
    public async Task A_runner_that_did_not_return_in_time_may_still_hold_the_receive_loop_so_no_second_reader_is_added()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        OnlineAfterSettleDriver driver = Direct(session);
        var log = new HostTestKit.LogSink();

        OnlineGiveUpSummary g = await OnlineGiveUpOnFailure.FinishAsync(true, driver, GiveUpKit.Fight(OnlineFightOutcome.NoClearNotice, runnerOutcome: null), null, log.Add, CancellationToken.None);

        Assert.Equal(OnlineGiveUpOutcome.Skipped, g.Outcome);
        Assert.False(g.Sent);
        Assert.Equal("NoClearNotice", g.Reason);
        Assert.Empty(session.Sent);
        Assert.Equal(new[] { Line("skipped", OnlineFightOutcome.NoClearNotice) }, log.Lines);

        // 不触发的结局即使跑器没返回也什么都不记
        var quiet = new HostTestKit.LogSink();
        Assert.Null(await OnlineGiveUpOnFailure.FinishAsync(true, driver, GiveUpKit.Fight(OnlineFightOutcome.Faulted, runnerOutcome: null), null, quiet.Add, CancellationToken.None));
        Assert.Empty(quiet.Lines);
    }

    [Fact]
    public async Task The_wrap_up_may_be_called_only_once_per_session_driver_and_never_sends_a_second_command()
    {
        var session = new CombatFakeSession();
        session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
        OnlineAfterSettleDriver driver = Direct(session);
        OnlineFightSummary fight = GiveUpKit.Fight(OnlineFightOutcome.NoClearNotice, OnlineDungeonCombatOutcome.Timeout);

        OnlineGiveUpSummary first = await OnlineGiveUpOnFailure.FinishAsync(true, driver, fight, null, null, CancellationToken.None);
        Assert.Equal(OnlineGiveUpOutcome.Returned, first.Outcome);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OnlineGiveUpOnFailure.FinishAsync(true, driver, fight, null, null, CancellationToken.None));
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(new ushort[] { 45 }, SentIds(session));
    }

    [Fact]
    public async Task A_user_abort_before_the_wrap_up_never_sends_for_an_aborted_fight_even_with_a_live_summary()
    {
        // 战斗被用户中止:取消不触发放弃(用户中止要立刻停)。令牌已取消与未取消两种情形结论一致
        foreach (bool tokenCancelled in new[] { false, true })
        {
            var session = new CombatFakeSession();
            session.Respond = (c, id, p) => id == 45 ? GiveUpKit.Pair() : null;
            using var cts = new CancellationTokenSource();
            if (tokenCancelled)
            {
                cts.Cancel();
            }

            OnlineGiveUpSummary g = await OnlineGiveUpOnFailure.FinishAsync(true, Direct(session), GiveUpKit.Fight(OnlineFightOutcome.Cancelled, OnlineDungeonCombatOutcome.Cancelled), null, null, cts.Token);

            Assert.Null(g);
            Assert.Empty(session.Sent);
        }
    }
}
