using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>无头结算后菜单驱动(<see cref="OnlineAfterSettleDriver"/>)的结局,只描述走到哪一步停下,不含任何载荷内容。</summary>
    public enum OnlineAfterSettleOutcome
    {
        /// <summary>已收到服务端回城信号(<c>0/2</c> 或 <c>0/23 + 0/24</c>),成功回城。</summary>
        ReturnedToTown = 0,

        /// <summary>发送 <c>1/72</c> 后等待 <c>(1, 72)</c> 回包超时。</summary>
        ScoreScrollTimeout,

        /// <summary>发送 <c>1/73</c> 后等待 <c>(1, 73)</c> 回包超时。</summary>
        CardRightTimeout,

        /// <summary>发送 <c>1/74</c> 选牌命令失败(帧层返回非 <see cref="FrameStatus.Ok"/>)。</summary>
        SelectCardSendFailed,

        /// <summary>发送 <c>1/75</c> 后等待 <c>(1, 75)</c> 执行确认回包超时。</summary>
        EplpTimeout,

        /// <summary><c>1/75</c> 确认后等待回城信号(<c>0/2</c> 或 <c>0/23 + 0/24</c>)超时。</summary>
        ReturnTimeout,

        /// <summary><c>(1, 75)</c> 回包明确带失败标志(<c>Succeeded == false</c>)。</summary>
        ReplyFailed,

        /// <summary>等待中的命令回包(<c>1/72</c>、<c>1/73</c> 或 <c>1/75</c>)载荷畸形。</summary>
        ReplyMalformed,

        /// <summary>对端关闭了连接或切帧出错。</summary>
        SessionClosed,

        /// <summary>发送/接收抛了异常或 <c>1/72</c>/<c>1/73</c>/<c>1/75</c>/心跳发送编码失败。</summary>
        Faulted,

        /// <summary>取消令牌触发(安静结束,不抛异常)。</summary>
        Cancelled,
    }

    /// <summary>无头结算后菜单驱动的可调项,超时缺省与 <c>OnlineSettlementController</c> 对齐。</summary>
    public sealed class OnlineAfterSettleOptions
    {
        /// <summary><c>1/72</c>、<c>1/73</c> 回包等待超时缺省值(5 秒)。</summary>
        public static readonly TimeSpan DefaultAdvanceReplyTimeout = TimeSpan.FromSeconds(5);

        /// <summary><c>1/75</c> 回包等待超时缺省值(5 秒)。</summary>
        public static readonly TimeSpan DefaultEplpReplyTimeout = TimeSpan.FromSeconds(5);

        /// <summary>选择返回城镇时等待回城信号的超时缺省值(6 秒)。</summary>
        public static readonly TimeSpan DefaultTownReturnWaitTimeout = TimeSpan.FromSeconds(6);

        /// <summary>非返回城镇菜单命令等待回城信号的超时缺省值(8 秒)。</summary>
        public static readonly TimeSpan DefaultReturnWaitTimeout = TimeSpan.FromSeconds(8);

        /// <summary>收帧轮询步长缺省值(50 毫秒)。</summary>
        public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

        /// <summary>放弃游戏(<see cref="OnlineAfterSettleDriver.GiveUpAsync(OnlineFightOutcome, Action{string}, CancellationToken)"/>)之后等回城信号的缺省上限(15 秒,与 <see cref="OnlineDungeonCombatOptions.GiveUpTimeout"/> 的缺省一致)。</summary>
        public static readonly TimeSpan DefaultGiveUpWaitTimeout = TimeSpan.FromSeconds(15);

        /// <summary>要发的 <c>1/75</c> 菜单命令,缺省 <see cref="EplpCommand.ReturnToTown"/>。</summary>
        public EplpCommand Command { get; set; } = EplpCommand.ReturnToTown;

        /// <summary><c>1/72</c> 与 <c>1/73</c> 的回包超时。</summary>
        public TimeSpan AdvanceReplyTimeout { get; set; } = DefaultAdvanceReplyTimeout;

        /// <summary><c>1/75</c> 的回包超时。</summary>
        public TimeSpan EplpReplyTimeout { get; set; } = DefaultEplpReplyTimeout;

        /// <summary>选择 <see cref="EplpCommand.ReturnToTown"/> 时等待回城信号的超时。</summary>
        public TimeSpan TownReturnWaitTimeout { get; set; } = DefaultTownReturnWaitTimeout;

        /// <summary>其他菜单选择等待回城信号的超时。</summary>
        public TimeSpan ReturnWaitTimeout { get; set; } = DefaultReturnWaitTimeout;

        /// <summary>收帧轮询步长。</summary>
        public TimeSpan PollInterval { get; set; } = DefaultPollInterval;

        /// <summary>时钟;null 用 <see cref="SystemFightClock.Instance"/>。</summary>
        public IFightClock Clock { get; set; }

        /// <summary>放弃游戏(1/45)发出之后等回城信号的上限,缺省 <see cref="DefaultGiveUpWaitTimeout"/>;只有调用 <c>GiveUpAsync</c> 时才用到。</summary>
        public TimeSpan GiveUpWaitTimeout { get; set; } = DefaultGiveUpWaitTimeout;
    }

    /// <summary>无头结算后菜单驱动的汇总(不含任何载荷内容)。</summary>
    public sealed class OnlineAfterSettleSummary
    {
        internal OnlineAfterSettleSummary(
            OnlineAfterSettleOutcome outcome,
            bool cardSelectSkipped,
            byte? selectedCardIndex,
            byte? eplpErrorCode,
            int heartbeatsAnswered,
            int ignoredFrames,
            int envelopesOpened,
            int envelopesUnopened,
            int innerCopiesSkipped,
            string faultType,
            TimeSpan elapsed)
        {
            Outcome = outcome;
            CardSelectSkipped = cardSelectSkipped;
            SelectedCardIndex = selectedCardIndex;
            EplpErrorCode = eplpErrorCode;
            HeartbeatsAnswered = heartbeatsAnswered;
            IgnoredFrames = ignoredFrames;
            EnvelopesOpened = envelopesOpened;
            EnvelopesUnopened = envelopesUnopened;
            InnerCopiesSkipped = innerCopiesSkipped;
            FaultType = faultType;
            Elapsed = elapsed;
        }

        /// <summary>结局。</summary>
        public OnlineAfterSettleOutcome Outcome { get; }

        /// <summary>是否成功回城。</summary>
        public bool ReturnedToTown => Outcome == OnlineAfterSettleOutcome.ReturnedToTown;

        /// <summary>是否因没有有权槽位而跳过了 <c>1/74</c>。</summary>
        public bool CardSelectSkipped { get; }

        /// <summary>发 <c>1/74</c> 时选中的槽位下标(跳过或未走到时为 null)。</summary>
        public byte? SelectedCardIndex { get; }

        /// <summary><c>(1, 75)</c> 失败回包里的服务端错误码(未失败时为 null)。</summary>
        public byte? EplpErrorCode { get; }

        /// <summary>驱动期间回答的心跳数。</summary>
        public int HeartbeatsAnswered { get; }

        /// <summary>收到的无关或被忽略帧数。</summary>
        public int IgnoredFrames { get; }

        /// <summary>拆开的 561 信封数。</summary>
        public int EnvelopesOpened { get; }

        /// <summary>未拆开的 561 信封数(或压缩内层留给会话展开的信封数)。</summary>
        public int EnvelopesUnopened { get; }

        /// <summary>跳过的会话附带内层副本帧数。</summary>
        public int InnerCopiesSkipped { get; }

        /// <summary>异常类型名(无异常时为 null;不含异常消息)。</summary>
        public string FaultType { get; }

        /// <summary>从驱动开始到结束的时间(取注入时钟)。</summary>
        public TimeSpan Elapsed { get; }
    }

    /// <summary>
    /// 结算后菜单与回城的无头驱动:在战斗跑器到达 <c>ReturnReady</c> 之后接管同一条会话,
    /// 按序发 <c>1/72</c> → <c>1/73</c> → (<c>1/74</c>) → <c>1/75</c> 并等待回城信号(<c>0/2</c> 或 <c>0/23 + 0/24</c>)。
    /// 每条命令只发一次、绝不自动重发;收到心跳包自动回 <see cref="OnlineHeartbeat.ReplyPayload"/>;
    /// 正确处理 <c>561</c> 信封(跳过附带内层副本,避免重复计数);无关帧只计数、不解析也不打印载荷。
    /// <para>
    /// 另有默认不用的会话层放弃(<see cref="GiveUpAsync(OnlineFightOutcome, Action{string}, CancellationToken)"/>,探针 <c>--give-up-on-failure</c>):
    /// 异常收场而会话还活着时,断开前直接发一次放弃游戏命令(<c>1/45</c>)并有限地等回城信号(<c>0/23 + 0/24</c> 或 <c>0/2</c>),复用同一套收帧、心跳、561 信封与压缩内层的实现;
    /// 一个驱动只能放弃一次,与 <see cref="RunAsync"/> 各有各的一次性开关,互不影响。
    /// </para>
    /// </summary>
    public sealed class OnlineAfterSettleDriver
    {
        private readonly IOnlineDungeonSession _session;
        private readonly Func<byte[], byte[]> _unwrap;
        private readonly EplpCommand _command;
        private readonly TimeSpan _advanceTimeout;
        private readonly TimeSpan _eplpTimeout;
        private readonly TimeSpan _returnTimeout;
        private readonly TimeSpan _poll;
        private readonly IFightClock _clock;
        private readonly TimeSpan _giveUpTimeout;
        private int _started;
        private int _giveUpStarted;

        /// <summary>在真会话上创建,默认使用 <see cref="OnlineDungeonRunner.UnwrapEnvelope"/> 解包 561 信封。</summary>
        public OnlineAfterSettleDriver(GameClientSession session, OnlineAfterSettleOptions options = null)
            : this(new GameClientSessionAdapter(session ?? throw new ArgumentNullException(nameof(session))), OnlineDungeonRunner.UnwrapEnvelope, options)
        {
        }

        /// <summary>在真会话上创建并显式指定 561 解包委托。</summary>
        public OnlineAfterSettleDriver(GameClientSession session, Func<byte[], byte[]> unwrapEnvelope, OnlineAfterSettleOptions options = null)
            : this(new GameClientSessionAdapter(session ?? throw new ArgumentNullException(nameof(session))), unwrapEnvelope, options)
        {
        }

        /// <summary>在会话接缝上创建(默认使用 <see cref="OnlineDungeonRunner.UnwrapEnvelope"/>)。</summary>
        public OnlineAfterSettleDriver(IOnlineDungeonSession session, OnlineAfterSettleOptions options = null)
            : this(session, OnlineDungeonRunner.UnwrapEnvelope, options)
        {
        }

        /// <summary>在会话接缝上创建并显式指定 561 解包委托(可为 null,用于测试未拆包路径)。</summary>
        public OnlineAfterSettleDriver(IOnlineDungeonSession session, Func<byte[], byte[]> unwrapEnvelope, OnlineAfterSettleOptions options = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _unwrap = unwrapEnvelope;
            OnlineAfterSettleOptions o = options ?? new OnlineAfterSettleOptions();
            if ((byte)o.Command > SettlementMenuPayloads.MaxEplpCommand)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Command 超出 0..3");
            }
            CheckWindow(o.AdvanceReplyTimeout, "AdvanceReplyTimeout");
            CheckWindow(o.EplpReplyTimeout, "EplpReplyTimeout");
            CheckWindow(o.TownReturnWaitTimeout, "TownReturnWaitTimeout");
            CheckWindow(o.ReturnWaitTimeout, "ReturnWaitTimeout");
            CheckWindow(o.PollInterval, "PollInterval");
            CheckWindow(o.GiveUpWaitTimeout, "GiveUpWaitTimeout");
            _command = o.Command;
            _advanceTimeout = o.AdvanceReplyTimeout;
            _eplpTimeout = o.EplpReplyTimeout;
            _returnTimeout = o.Command == EplpCommand.ReturnToTown ? o.TownReturnWaitTimeout : o.ReturnWaitTimeout;
            _poll = o.PollInterval;
            _clock = o.Clock ?? SystemFightClock.Instance;
            _giveUpTimeout = o.GiveUpWaitTimeout;
        }

        /// <summary>
        /// 战斗驱动以 <paramref name="reason"/> 收场之后的会话层放弃:<paramref name="reason"/> 不是触发放弃的结局
        /// (<see cref="OnlineGiveUpOnFailure.ShouldGiveUp(OnlineFightOutcome)"/>)时什么都不发、不碰会话、不占一次性开关,返回 null;
        /// 否则直接发一次放弃游戏命令(类 1、包号 45)并等回城信号(<c>0/23 + 0/24</c> 或 <c>0/2</c>),最多等 <see cref="OnlineAfterSettleOptions.GiveUpWaitTimeout"/>,
        /// 等待期间回心跳。只发一次、绝不重发;会话已关或切帧出错就什么都不发(<see cref="OnlineGiveUpOutcome.Skipped"/>)。
        /// 日志行是 <see cref="OnlineFightLog.GiveUpStep"/> 拼的脱敏行,reason 只是结局的枚举名。取消令牌触发立刻停,结局记 <see cref="OnlineGiveUpOutcome.Cancelled"/>,不抛。
        /// </summary>
        /// <exception cref="InvalidOperationException">这个驱动已经放弃过一次(只有真的触发了放弃才占用一次性开关)。</exception>
        public Task<OnlineGiveUpSummary> GiveUpAsync(OnlineFightOutcome reason, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            return OnlineGiveUpOnFailure.ShouldGiveUp(reason)
                ? GiveUpCoreAsync(reason.ToString(), log, cancellationToken)
                : Task.FromResult<OnlineGiveUpSummary>(null);
        }

        /// <summary>
        /// 结算后菜单以 <paramref name="reason"/> 收场之后的会话层放弃:行为同 <see cref="GiveUpAsync(OnlineFightOutcome, Action{string}, CancellationToken)"/>,
        /// 触发的结局见 <see cref="OnlineGiveUpOnFailure.ShouldGiveUp(OnlineAfterSettleOutcome)"/>(成功回城、会话已关、异常、取消都不触发,返回 null)。
        /// </summary>
        /// <exception cref="InvalidOperationException">这个驱动已经放弃过一次。</exception>
        public Task<OnlineGiveUpSummary> GiveUpAsync(OnlineAfterSettleOutcome reason, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            return OnlineGiveUpOnFailure.ShouldGiveUp(reason)
                ? GiveUpCoreAsync(reason.ToString(), log, cancellationToken)
                : Task.FromResult<OnlineGiveUpSummary>(null);
        }

        private Task<OnlineGiveUpSummary> GiveUpCoreAsync(string reason, Action<string> log, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _giveUpStarted, 1) != 0)
            {
                throw new InvalidOperationException("一个结算后菜单驱动只能放弃一次");
            }
            var attempt = new Attempt(this, log, cancellationToken);
            return attempt.RunGiveUpAsync(reason);
        }

        /// <summary>执行一次结算后菜单推进与回城等待;每个步骤向 <paramref name="log"/> 输出一条脱敏日志行。</summary>
        /// <exception cref="InvalidOperationException">同一个驱动实例被运行了两次。</exception>
        public Task<OnlineAfterSettleSummary> RunAsync(Action<string> log = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("一个结算后菜单驱动只能跑一次");
            }
            var attempt = new Attempt(this, log, cancellationToken);
            return attempt.RunAsync();
        }

        // 取第一个状态为 1 的槽位下标;没有返回 -1(与 OnlineSettlementController 同逻辑,保留本地私有副本以免 Net 引用 Client)。
        private static int PickCardSlotFromRights(IReadOnlyList<short> slotStates)
        {
            if (slotStates == null)
            {
                return -1;
            }
            for (int i = 0; i < slotStates.Count; i++)
            {
                if (slotStates[i] == 1)
                {
                    return i;
                }
            }
            return -1;
        }

        private static TimeSpan Shorter(TimeSpan a, TimeSpan b)
        {
            return a <= b ? a : b;
        }

        private static void CheckWindow(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException("options", name + " 必须大于 0 且不超过 int.MaxValue 毫秒");
            }
        }

        private sealed class Attempt
        {
            private readonly OnlineAfterSettleDriver _owner;
            private readonly IOnlineDungeonSession _session;
            private readonly Action<string> _log;
            private readonly CancellationToken _cancel;
            private readonly Queue<DecodedFrame> _backlog = new Queue<DecodedFrame>();
            private readonly TimeSpan _startedAt;

            private bool _cardSelectSkipped;
            private byte? _selectedCardIndex;
            private byte? _eplpErrorCode;
            private int _heartbeats;
            private int _ignoredTotal;
            private int _envelopesOpened;
            private int _envelopesUnopened;
            private int _innerCopiesSkipped;
            private string _faultType;

            private bool _trackReturnSignals;
            private bool _gotNoti2;
            private bool _gotUserArea;
            private bool _gotAreaUsers;
            private InnerCopy _copy;

            public Attempt(OnlineAfterSettleDriver owner, Action<string> log, CancellationToken cancel)
            {
                _owner = owner;
                _session = owner._session;
                _log = log;
                _cancel = cancel;
                _startedAt = owner._clock.Now;
            }

            private bool HasReturnSignal => _gotNoti2 || (_gotUserArea && _gotAreaUsers);

            public async Task<OnlineAfterSettleSummary> RunAsync()
            {
                // 1. 1/72 SCORE_SCROLL_STATE
                OnlineAfterSettleOutcome? fail = await RunStep72Async().ConfigureAwait(false);
                if (fail.HasValue)
                {
                    return Summarize(fail.Value);
                }

                // 2. 1/73 CARD_SELECT_RIGHT_STATE
                CardSelectRightReplyNotice rights = null;
                fail = await RunStep73Async(r => rights = r).ConfigureAwait(false);
                if (fail.HasValue)
                {
                    return Summarize(fail.Value);
                }

                // 3. 1/74 SELECT_CARD (有有权槽位才发;发出即过,不等待 CMD/74 回包)
                fail = await RunStep74Async(rights).ConfigureAwait(false);
                if (fail.HasValue)
                {
                    return Summarize(fail.Value);
                }

                // 4. 1/75 EPLP_COMMAND
                fail = await RunStep75Async().ConfigureAwait(false);
                if (fail.HasValue)
                {
                    return Summarize(fail.Value);
                }

                // 5. 等待回城信号(0/2 或 0/23 + 0/24)
                OnlineAfterSettleOutcome finalOutcome = await RunStepReturnAsync().ConfigureAwait(false);
                return Summarize(finalOutcome);
            }

            // 放弃游戏:会话不可用就什么都不发;否则只发一次 1/45(空载荷),之后只等回城信号(0/2 或 0/23 + 0/24)并回心跳,从不重发。
            // 收帧、心跳、561 信封与压缩内层、取消与断线的处理全部复用上面各步骤同一套(WaitLoopAsync / DispatchAsync / OpenEnvelopeAsync / TryConsumeReturnSignal)。
            public async Task<OnlineGiveUpSummary> RunGiveUpAsync(string reason)
            {
                bool sent = false;
                OnlineGiveUpOutcome outcome;
                try
                {
                    if (!TryPreSendCheck(out _, out _))
                    {
                        outcome = OnlineGiveUpOutcome.Skipped;
                    }
                    else
                    {
                        FrameStatus status = await _session.SendAsync(PacketClass.Cmd, SessionLifecyclePayloads.GiveUpGamePacketId,
                            SessionLifecyclePayloads.BuildGiveUpGame(), _cancel).ConfigureAwait(false);
                        if (status != FrameStatus.Ok)
                        {
                            outcome = OnlineGiveUpOutcome.SendFailed;
                        }
                        else
                        {
                            sent = true;
                            _trackReturnSignals = true;
                            Say(OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpSentStep, reason));

                            OnlineAfterSettleOutcome? stop = await WaitLoopAsync(
                                _owner._giveUpTimeout,
                                OnlineAfterSettleOutcome.ReturnTimeout,
                                () => HasReturnSignal,
                                (cls, id, payload) =>
                                {
                                    TryConsumeReturnSignal(cls, id);
                                    return null;
                                }).ConfigureAwait(false);
                            outcome = GiveUpOutcomeOf(stop);
                        }
                    }
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    outcome = OnlineGiveUpOutcome.Cancelled;
                }
                catch (Exception e) when (IsClosedException(e))
                {
                    outcome = sent ? OnlineGiveUpOutcome.Closed : OnlineGiveUpOutcome.SendFailed;
                }
                catch (Exception e)
                {
                    _faultType = e.GetBaseException().GetType().Name;
                    outcome = OnlineGiveUpOutcome.Faulted;
                }

                Say(OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpToken(outcome), reason));
                return new OnlineGiveUpSummary(reason, outcome, sent, _owner._clock.Now - _startedAt, _faultType);
            }

            // 等回城信号的收场:没有收场原因就是收齐了信号;超时、连接断、心跳回包发不出去各对应一个放弃结局。
            private static OnlineGiveUpOutcome GiveUpOutcomeOf(OnlineAfterSettleOutcome? stop)
            {
                if (!stop.HasValue)
                {
                    return OnlineGiveUpOutcome.Returned;
                }
                switch (stop.Value)
                {
                    case OnlineAfterSettleOutcome.ReturnTimeout:
                        return OnlineGiveUpOutcome.Timeout;
                    case OnlineAfterSettleOutcome.SessionClosed:
                        return OnlineGiveUpOutcome.Closed;
                    default:
                        return OnlineGiveUpOutcome.Faulted;
                }
            }

            private async Task<OnlineAfterSettleOutcome?> RunStep72Async()
            {
                TimeSpan stepStart = _owner._clock.Now;
                int stepIgnored = 0;
                bool sent = false;
                try
                {
                    if (!TryPreSendCheck(out OnlineAfterSettleOutcome preFail, out string preReply))
                    {
                        EmitStep("72", false, preReply, stepStart, stepIgnored);
                        return preFail;
                    }
                    FrameStatus status = await _session.SendAsync(PacketClass.Cmd, SettlementMenuPayloads.ScoreScrollStatePacketId,
                        SettlementMenuPayloads.BuildScoreScrollState(), _cancel).ConfigureAwait(false);
                    if (status != FrameStatus.Ok)
                    {
                        EmitStep("72", false, "failed", stepStart, stepIgnored);
                        return OnlineAfterSettleOutcome.Faulted;
                    }
                    sent = true;

                    bool done = false;
                    OnlineAfterSettleOutcome? outcome = await WaitLoopAsync(
                        _owner._advanceTimeout,
                        OnlineAfterSettleOutcome.ScoreScrollTimeout,
                        () => done,
                        (cls, id, payload) =>
                        {
                            if (cls == PacketClass.Cmd && id == SettlementMenuPayloads.ScoreScrollStatePacketId)
                            {
                                if (SettlementMenuPayloads.TryParseScoreScrollReply(payload, out _))
                                {
                                    done = true;
                                    return null;
                                }
                                return OnlineAfterSettleOutcome.ReplyMalformed;
                            }
                            stepIgnored++;
                            _ignoredTotal++;
                            return null;
                        }).ConfigureAwait(false);

                    if (outcome.HasValue)
                    {
                        EmitStep("72", sent, ReplyTokenForOutcome(outcome.Value), stepStart, stepIgnored);
                        return outcome.Value;
                    }
                    EmitStep("72", sent, "ok", stepStart, stepIgnored);
                    return null;
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    EmitStep("72", sent, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Cancelled;
                }
                catch (Exception e) when (IsClosedException(e))
                {
                    EmitStep("72", sent, "closed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.SessionClosed;
                }
                catch (Exception e)
                {
                    _faultType = e.GetBaseException().GetType().Name;
                    EmitStep("72", sent, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Faulted;
                }
            }

            private async Task<OnlineAfterSettleOutcome?> RunStep73Async(Action<CardSelectRightReplyNotice> setReply)
            {
                TimeSpan stepStart = _owner._clock.Now;
                int stepIgnored = 0;
                bool sent = false;
                try
                {
                    if (!TryPreSendCheck(out OnlineAfterSettleOutcome preFail, out string preReply))
                    {
                        EmitStep("73", false, preReply, stepStart, stepIgnored);
                        return preFail;
                    }
                    FrameStatus status = await _session.SendAsync(PacketClass.Cmd, SettlementMenuPayloads.CardSelectRightStatePacketId,
                        SettlementMenuPayloads.BuildCardSelectRightState(), _cancel).ConfigureAwait(false);
                    if (status != FrameStatus.Ok)
                    {
                        EmitStep("73", false, "failed", stepStart, stepIgnored);
                        return OnlineAfterSettleOutcome.Faulted;
                    }
                    sent = true;

                    bool done = false;
                    OnlineAfterSettleOutcome? outcome = await WaitLoopAsync(
                        _owner._advanceTimeout,
                        OnlineAfterSettleOutcome.CardRightTimeout,
                        () => done,
                        (cls, id, payload) =>
                        {
                            if (cls == PacketClass.Cmd && id == SettlementMenuPayloads.CardSelectRightStatePacketId)
                            {
                                if (SettlementMenuPayloads.TryParseCardSelectRightReply(payload, out CardSelectRightReplyNotice parsed))
                                {
                                    setReply(parsed);
                                    done = true;
                                    return null;
                                }
                                return OnlineAfterSettleOutcome.ReplyMalformed;
                            }
                            stepIgnored++;
                            _ignoredTotal++;
                            return null;
                        }).ConfigureAwait(false);

                    if (outcome.HasValue)
                    {
                        EmitStep("73", sent, ReplyTokenForOutcome(outcome.Value), stepStart, stepIgnored);
                        return outcome.Value;
                    }
                    EmitStep("73", sent, "ok", stepStart, stepIgnored);
                    return null;
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    EmitStep("73", sent, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Cancelled;
                }
                catch (Exception e) when (IsClosedException(e))
                {
                    EmitStep("73", sent, "closed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.SessionClosed;
                }
                catch (Exception e)
                {
                    _faultType = e.GetBaseException().GetType().Name;
                    EmitStep("73", sent, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Faulted;
                }
            }

            private async Task<OnlineAfterSettleOutcome?> RunStep74Async(CardSelectRightReplyNotice rights)
            {
                TimeSpan stepStart = _owner._clock.Now;
                int slot = PickCardSlotFromRights(rights != null ? rights.SlotStates : null);
                if (slot < 0)
                {
                    _cardSelectSkipped = true;
                    EmitStep("74", false, "skipped", stepStart, 0);
                    return null;
                }

                byte cardIndex = (byte)slot;
                _selectedCardIndex = cardIndex;
                try
                {
                    if (!TryPreSendCheck(out OnlineAfterSettleOutcome preFail, out string preReply))
                    {
                        EmitStep("74", false, preReply, stepStart, 0);
                        return preFail;
                    }
                    byte[] payload = SettlementMenuPayloads.BuildSelectCard(0, cardIndex);
                    FrameStatus status = await _session.SendAsync(PacketClass.Cmd, SettlementMenuPayloads.SelectCardPacketId,
                        payload, _cancel).ConfigureAwait(false);
                    if (status != FrameStatus.Ok)
                    {
                        EmitStep("74", false, "failed", stepStart, 0);
                        return OnlineAfterSettleOutcome.SelectCardSendFailed;
                    }
                    EmitStep("74", true, "ok", stepStart, 0);
                    return null;
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    EmitStep("74", false, "failed", stepStart, 0);
                    return OnlineAfterSettleOutcome.Cancelled;
                }
                catch (Exception e) when (IsClosedException(e))
                {
                    EmitStep("74", false, "closed", stepStart, 0);
                    return OnlineAfterSettleOutcome.SessionClosed;
                }
                catch (Exception e)
                {
                    _faultType = e.GetBaseException().GetType().Name;
                    EmitStep("74", false, "failed", stepStart, 0);
                    return OnlineAfterSettleOutcome.Faulted;
                }
            }

            private async Task<OnlineAfterSettleOutcome?> RunStep75Async()
            {
                TimeSpan stepStart = _owner._clock.Now;
                int stepIgnored = 0;
                bool sent = false;
                try
                {
                    if (!TryPreSendCheck(out OnlineAfterSettleOutcome preFail, out string preReply))
                    {
                        EmitStep("75", false, preReply, stepStart, stepIgnored);
                        return preFail;
                    }
                    byte[] cmdPayload = SettlementMenuPayloads.BuildEplpCommand(SettlementMenuPayloads.EplpFlagExecute, _owner._command);
                    FrameStatus status = await _session.SendAsync(PacketClass.Cmd, SettlementMenuPayloads.EplpCommandPacketId,
                        cmdPayload, _cancel).ConfigureAwait(false);
                    if (status != FrameStatus.Ok)
                    {
                        EmitStep("75", false, "failed", stepStart, stepIgnored);
                        return OnlineAfterSettleOutcome.Faulted;
                    }
                    sent = true;
                    _trackReturnSignals = true;

                    bool done = false;
                    OnlineAfterSettleOutcome? outcome = await WaitLoopAsync(
                        _owner._eplpTimeout,
                        OnlineAfterSettleOutcome.EplpTimeout,
                        () => done,
                        (cls, id, payload) =>
                        {
                            if (cls == PacketClass.Cmd && id == SettlementMenuPayloads.EplpCommandPacketId)
                            {
                                if (!SettlementMenuPayloads.TryParseEplpReply(payload, out EplpReplyNotice reply))
                                {
                                    return OnlineAfterSettleOutcome.ReplyMalformed;
                                }
                                if (!reply.Command.HasValue || reply.Command.Value != _owner._command)
                                {
                                    stepIgnored++;
                                    _ignoredTotal++;
                                    return null;
                                }
                                if (!reply.Succeeded)
                                {
                                    _eplpErrorCode = reply.ErrorCode;
                                    return OnlineAfterSettleOutcome.ReplyFailed;
                                }
                                if (reply.Flag != SettlementMenuPayloads.EplpFlagExecute)
                                {
                                    // flag == 2 (UI 光标同步) 等非执行确认一律忽略继续等
                                    stepIgnored++;
                                    _ignoredTotal++;
                                    return null;
                                }
                                done = true;
                                return null;
                            }
                            if (TryConsumeReturnSignal(cls, id))
                            {
                                return null;
                            }
                            stepIgnored++;
                            _ignoredTotal++;
                            return null;
                        }).ConfigureAwait(false);

                    if (outcome.HasValue)
                    {
                        EmitStep("75", sent, ReplyTokenForOutcome(outcome.Value), stepStart, stepIgnored);
                        return outcome.Value;
                    }
                    EmitStep("75", sent, "ok", stepStart, stepIgnored);
                    return null;
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    EmitStep("75", sent, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Cancelled;
                }
                catch (Exception e) when (IsClosedException(e))
                {
                    EmitStep("75", sent, "closed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.SessionClosed;
                }
                catch (Exception e)
                {
                    _faultType = e.GetBaseException().GetType().Name;
                    EmitStep("75", sent, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Faulted;
                }
            }

            private async Task<OnlineAfterSettleOutcome> RunStepReturnAsync()
            {
                TimeSpan stepStart = _owner._clock.Now;
                int stepIgnored = 0;
                try
                {
                    if (HasReturnSignal)
                    {
                        EmitStep("return", false, "ok", stepStart, stepIgnored);
                        return OnlineAfterSettleOutcome.ReturnedToTown;
                    }

                    OnlineAfterSettleOutcome? outcome = await WaitLoopAsync(
                        _owner._returnTimeout,
                        OnlineAfterSettleOutcome.ReturnTimeout,
                        () => HasReturnSignal,
                        (cls, id, payload) =>
                        {
                            if (TryConsumeReturnSignal(cls, id))
                            {
                                return null;
                            }
                            stepIgnored++;
                            _ignoredTotal++;
                            return null;
                        }).ConfigureAwait(false);

                    if (outcome.HasValue)
                    {
                        EmitStep("return", false, ReplyTokenForOutcome(outcome.Value), stepStart, stepIgnored);
                        return outcome.Value;
                    }
                    EmitStep("return", false, "ok", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.ReturnedToTown;
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    EmitStep("return", false, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Cancelled;
                }
                catch (Exception e) when (IsClosedException(e))
                {
                    EmitStep("return", false, "closed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.SessionClosed;
                }
                catch (Exception e)
                {
                    _faultType = e.GetBaseException().GetType().Name;
                    EmitStep("return", false, "failed", stepStart, stepIgnored);
                    return OnlineAfterSettleOutcome.Faulted;
                }
            }

            private bool TryPreSendCheck(out OnlineAfterSettleOutcome failure, out string replyToken)
            {
                _cancel.ThrowIfCancellationRequested();
                if (_session.PeerClosed || _session.LastFrameError != FrameError.None)
                {
                    failure = OnlineAfterSettleOutcome.SessionClosed;
                    replyToken = "closed";
                    return false;
                }
                failure = default(OnlineAfterSettleOutcome);
                replyToken = null;
                return true;
            }

            private bool TryConsumeReturnSignal(PacketClass cls, int id)
            {
                if (!_trackReturnSignals || cls != PacketClass.Noti)
                {
                    return false;
                }
                if (id == UserInfoParser.PacketId)
                {
                    _gotNoti2 = true;
                    return true;
                }
                if (id == TownAreaPayloads.UserAreaPacketId)
                {
                    _gotUserArea = true;
                    return true;
                }
                if (id == TownAreaPayloads.AreaUsersPacketId)
                {
                    _gotAreaUsers = true;
                    return true;
                }
                return false;
            }

            private async Task<OnlineAfterSettleOutcome?> WaitLoopAsync(
                TimeSpan window,
                OnlineAfterSettleOutcome timeoutOutcome,
                Func<bool> isDone,
                Func<PacketClass, int, byte[], OnlineAfterSettleOutcome?> handleFrame)
            {
                TimeSpan deadline = _owner._clock.Now + window;
                while (true)
                {
                    _cancel.ThrowIfCancellationRequested();

                    while (_backlog.Count > 0)
                    {
                        DecodedFrame queued = _backlog.Dequeue();
                        OnlineAfterSettleOutcome? stop = await ProcessFrameAsync(queued, handleFrame).ConfigureAwait(false);
                        if (stop.HasValue)
                        {
                            return stop.Value;
                        }
                        if (isDone())
                        {
                            if (_copy != null && _backlog.Count > 0 && _copy.Matches(_backlog.Peek()))
                            {
                                _backlog.Dequeue();
                                _innerCopiesSkipped++;
                                _copy = null;
                            }
                            return null;
                        }
                    }

                    if (_session.PeerClosed || _session.LastFrameError != FrameError.None)
                    {
                        return OnlineAfterSettleOutcome.SessionClosed;
                    }

                    TimeSpan left = deadline - _owner._clock.Now;
                    bool lastLook = left <= TimeSpan.Zero;
                    TimeSpan step = lastLook ? TimeSpan.Zero : Shorter(_owner._poll, left);
                    TimeSpan beforeRecv = _owner._clock.Now;
                    List<DecodedFrame> frames = await _session.ReceiveAsync(step, 1, _cancel).ConfigureAwait(false);
                    if (frames != null && frames.Count > 0)
                    {
                        _copy = null;
                        for (int i = 0; i < frames.Count; i++)
                        {
                            _backlog.Enqueue(frames[i]);
                        }
                        continue;
                    }

                    if (_session.PeerClosed || _session.LastFrameError != FrameError.None)
                    {
                        return OnlineAfterSettleOutcome.SessionClosed;
                    }
                    if (lastLook)
                    {
                        return timeoutOutcome;
                    }
                    if (_owner._clock.Now == beforeRecv)
                    {
                        await _owner._clock.DelayAsync(step, _cancel).ConfigureAwait(false);
                    }
                }
            }

            private async Task<OnlineAfterSettleOutcome?> ProcessFrameAsync(
                DecodedFrame frame,
                Func<PacketClass, int, byte[], OnlineAfterSettleOutcome?> handleFrame)
            {
                InnerCopy copy = _copy;
                _copy = null;
                if (copy != null && copy.Matches(frame))
                {
                    _innerCopiesSkipped++;
                    return null;
                }

                PacketClass cls = frame.Header.Class;
                int id = frame.Header.PacketId;
                byte[] payload = frame.Payload ?? Array.Empty<byte>();
                if (cls == PacketClass.Noti && id == OnlineDungeonEntry.ServerEnvelopePacketId)
                {
                    return await OpenEnvelopeAsync(payload, handleFrame).ConfigureAwait(false);
                }
                return await DispatchAsync(cls, id, payload, handleFrame).ConfigureAwait(false);
            }

            private async Task<OnlineAfterSettleOutcome?> DispatchAsync(
                PacketClass cls,
                int id,
                byte[] payload,
                Func<PacketClass, int, byte[], OnlineAfterSettleOutcome?> handleFrame)
            {
                if (cls == PacketClass.Noti && id == OnlineHeartbeat.HeartbeatPacketId)
                {
                    FrameStatus hbStatus = await _session.SendAsync(PacketClass.Cmd, OnlineHeartbeat.ReplyPacketId,
                        OnlineHeartbeat.ReplyPayload, _cancel).ConfigureAwait(false);
                    if (hbStatus != FrameStatus.Ok)
                    {
                        return OnlineAfterSettleOutcome.Faulted;
                    }
                    _heartbeats++;
                    return null;
                }
                return handleFrame(cls, id, payload);
            }

            private async Task<OnlineAfterSettleOutcome?> OpenEnvelopeAsync(
                byte[] envelopePayload,
                Func<PacketClass, int, byte[], OnlineAfterSettleOutcome?> handleFrame)
            {
                Func<byte[], byte[]> unwrap = _owner._unwrap;
                if (unwrap == null)
                {
                    _envelopesUnopened++;
                    return null;
                }

                byte[] inner;
                try
                {
                    inner = unwrap(envelopePayload);
                }
                catch (Exception)
                {
                    _envelopesUnopened++;
                    return null;
                }
                if (inner == null)
                {
                    _envelopesUnopened++;
                    return null;
                }

                int headerLength = FrameLayout.ServerToClient.HeaderLength;
                if (!PacketHeader.TryRead(FrameLayout.ServerToClient, inner, out PacketHeader header)
                    || header.TotalLength < (uint)headerLength
                    || header.TotalLength > (uint)inner.Length)
                {
                    _envelopesUnopened++;
                    return null;
                }

                var innerPayload = new byte[(int)header.TotalLength - headerLength];
                Buffer.BlockCopy(inner, headerLength, innerPayload, 0, innerPayload.Length);
                if (ServerFrameDecoder.IsCompressedId(header.PacketId))
                {
                    // 压缩内层帧(例如 0/2)由真会话自己解压并附在后面,这里不设 _copy 以便下一帧正常接收它
                    _envelopesUnopened++;
                    return null;
                }

                _envelopesOpened++;
                _copy = new InnerCopy(header.Class, header.PacketId, innerPayload);
                if (header.Class == PacketClass.Noti && header.PacketId == OnlineDungeonEntry.ServerEnvelopePacketId)
                {
                    return null;
                }
                return await DispatchAsync(header.Class, header.PacketId, innerPayload, handleFrame).ConfigureAwait(false);
            }

            private void EmitStep(string step, bool sent, string reply, TimeSpan stepStart, int stepIgnored)
            {
                long elapsedMs = Math.Max(0L, (long)(_owner._clock.Now - stepStart).TotalMilliseconds);
                string line = OnlineFightLog.AfterSettleStep(step, sent, reply, elapsedMs, stepIgnored);
                Say(line);
            }

            private void Say(string line)
            {
                if (_log == null)
                {
                    return;
                }
                try
                {
                    _log(line);
                }
                catch (Exception)
                {
                    // 日志回调自己出错不能影响驱动
                }
            }

            private OnlineAfterSettleSummary Summarize(OnlineAfterSettleOutcome outcome)
            {
                return new OnlineAfterSettleSummary(
                    outcome,
                    _cardSelectSkipped,
                    _selectedCardIndex,
                    _eplpErrorCode,
                    _heartbeats,
                    _ignoredTotal,
                    _envelopesOpened,
                    _envelopesUnopened,
                    _innerCopiesSkipped,
                    _faultType,
                    _owner._clock.Now - _startedAt);
            }

            private static string ReplyTokenForOutcome(OnlineAfterSettleOutcome outcome)
            {
                switch (outcome)
                {
                    case OnlineAfterSettleOutcome.ReturnedToTown:
                        return "ok";
                    case OnlineAfterSettleOutcome.ScoreScrollTimeout:
                    case OnlineAfterSettleOutcome.CardRightTimeout:
                    case OnlineAfterSettleOutcome.EplpTimeout:
                    case OnlineAfterSettleOutcome.ReturnTimeout:
                        return "timeout";
                    case OnlineAfterSettleOutcome.ReplyMalformed:
                        return "malformed";
                    case OnlineAfterSettleOutcome.SessionClosed:
                        return "closed";
                    default:
                        return "failed";
                }
            }

            private static bool IsClosedException(Exception e)
            {
                return e is IOException || e is ObjectDisposedException;
            }
        }

        private sealed class InnerCopy
        {
            private readonly PacketClass _class;
            private readonly int _id;
            private readonly byte[] _payload;

            public InnerCopy(PacketClass cls, int id, byte[] payload)
            {
                _class = cls;
                _id = id;
                _payload = payload;
            }

            public bool Matches(DecodedFrame frame)
            {
                if (frame.Kind != DecodedKind.Plain || frame.Header.Class != _class || frame.Header.PacketId != _id)
                {
                    return false;
                }
                byte[] other = frame.Payload ?? Array.Empty<byte>();
                if (other.Length != _payload.Length)
                {
                    return false;
                }
                for (int i = 0; i < other.Length; i++)
                {
                    if (other[i] != _payload[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private sealed class GameClientSessionAdapter : IOnlineDungeonSession
        {
            private readonly GameClientSession _session;

            public GameClientSessionAdapter(GameClientSession session)
            {
                _session = session;
            }

            public bool IsReady => _session.IsReady;

            public bool PeerClosed => _session.PeerClosed;

            public FrameError LastFrameError => _session.LastFrameError;

            public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
            {
                return _session.SendAsync(packetClass, packetId, payload, cancellationToken);
            }

            public Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
            {
                return _session.ReceiveAsync(window, stopAfterFrames, cancellationToken);
            }
        }
    }
}
