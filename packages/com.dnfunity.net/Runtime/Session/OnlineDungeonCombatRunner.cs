using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>战斗跑器的结局。</summary>
    public enum OnlineDungeonCombatOutcome
    {
        /// <summary>两段结算通知收齐,可以回城(唯一的成功结局)。</summary>
        ReturnReady = 0,

        /// <summary>取消令牌触发(不抛异常,安静地结束)。</summary>
        Cancelled,

        /// <summary>对端关闭了连接。</summary>
        ConnectionClosed,

        /// <summary>切帧出错,会话不能再用。</summary>
        FramingFault,

        /// <summary>帧层没能编码出要发的帧(击杀、结算或心跳回包),不再继续。</summary>
        SendFailed,

        /// <summary>整段战斗或等结算通知超过了上限。</summary>
        Timeout,

        /// <summary>收发时抛了异常(类型名在 <see cref="OnlineDungeonCombatResult.FaultType"/>,不带消息)。</summary>
        Faulted,
    }

    /// <summary>战斗跑器的结果:结局、终了阶段、全部事件与各种计数(都不含载荷内容)。</summary>
    public sealed class OnlineDungeonCombatResult
    {
        internal OnlineDungeonCombatResult(OnlineDungeonCombatOutcome outcome, OnlineDungeonCombat machine, int heartbeatsAnswered,
            int envelopesOpened, int envelopesUnopened, int innerCopiesSkipped, int framesSeen, string faultType)
        {
            Outcome = outcome;
            Phase = machine.Phase;
            Events = machine.Events;
            Monsters = machine.Total;
            Room = machine.Room;
            MovesSent = machine.MovesSent;
            MoveRefusals = machine.MoveRefusals;
            LoadReports = machine.LoadReports;
            MoveInFlight = machine.MoveInFlight;
            LoadState = machine.LoadState;
            DuplicateNumbers = machine.DuplicateNumbers;
            Sent = machine.Sent;
            Confirmed = machine.Confirmed;
            Refused = machine.Refused;
            Blocked = machine.Blocked;
            OutOfOrder = machine.OutOfOrder;
            Duplicates = machine.Duplicates;
            UnknownNumbers = machine.UnknownNumbers;
            Malformed = machine.Malformed;
            PassthroughCount = machine.PassthroughCount;
            Passthrough = machine.Passthrough;
            HeartbeatsAnswered = heartbeatsAnswered;
            EnvelopesOpened = envelopesOpened;
            EnvelopesUnopened = envelopesUnopened;
            InnerCopiesSkipped = innerCopiesSkipped;
            FramesSeen = framesSeen;
            FaultType = faultType;
        }

        public OnlineDungeonCombatOutcome Outcome { get; }

        /// <summary>结束时状态机所处的阶段。</summary>
        public OnlineCombatPhase Phase { get; }

        /// <summary>整个过程的事件(按产出顺序)。</summary>
        public IReadOnlyList<OnlineCombatEvent> Events { get; }

        /// <summary>进过的各房间的怪数合计(只有起始地图时就是起始地图的怪数)。</summary>
        public int Monsters { get; }

        /// <summary>结束时状态机所在的房间序号(起始房间是 0;只有起始地图时恒为 0)。</summary>
        public int Room { get; }

        /// <summary>发出过的换房命令数(含被服务端拒绝的)。</summary>
        public int MovesSent { get; }

        /// <summary>服务端回了换房失败的次数。</summary>
        public int MoveRefusals { get; }

        /// <summary>发出过的装载完成命令数。</summary>
        public int LoadReports { get; }

        /// <summary>结束时是否还有一条换房命令在等回应(以 <see cref="OnlineDungeonCombatOutcome.Timeout"/> 收尾时,这是定位卡在哪一步的依据之一)。</summary>
        public bool MoveInFlight { get; }

        /// <summary>结束时当前房间的装载握手进行到哪一步。</summary>
        public OnlineRoomLoadState LoadState { get; }

        /// <summary>编号重复的怪数(正常为 0)。</summary>
        public int DuplicateNumbers { get; }

        public int Sent { get; }

        public int Confirmed { get; }

        public int Refused { get; }

        public int Blocked { get; }

        public int OutOfOrder { get; }

        public int Duplicates { get; }

        public int UnknownNumbers { get; }

        public int Malformed { get; }

        /// <summary>不在状态机包集合里、只计数透传的帧数。</summary>
        public int PassthroughCount { get; }

        /// <summary>透传的帧按(类、包号)分的计数。</summary>
        public IReadOnlyList<OnlineCombatPacketCount> Passthrough { get; }

        /// <summary>跑器自己回的心跳数。</summary>
        public int HeartbeatsAnswered { get; }

        /// <summary>拆开的 561 信封数。</summary>
        public int EnvelopesOpened { get; }

        /// <summary>没拆开(没有解包委托、委托返回 null 或抛异常、内层帧不成形、内层是压缩帧)的 561 信封数。</summary>
        public int EnvelopesUnopened { get; }

        /// <summary>被当作"会话附在信封后面的内层副本"而跳过的帧数(见 <see cref="OnlineDungeonCombatRunner"/> 的说明)。</summary>
        public int InnerCopiesSkipped { get; }

        /// <summary>跑器一共看到的帧数(含信封与跳过的副本)。</summary>
        public int FramesSeen { get; }

        /// <summary>收发抛出的异常类型名(没有为 null;只有 <see cref="OnlineDungeonCombatOutcome.Faulted"/> 才有)。</summary>
        public string FaultType { get; }

        /// <summary>是否走到了可以回城。</summary>
        public bool ReturnReady => Outcome == OnlineDungeonCombatOutcome.ReturnReady;
    }

    /// <summary>
    /// 联网副本战斗的跑器:在进副本拿到起始地图之后接着用同一个会话,把 <see cref="OnlineDungeonCombat"/> 状态机跑起来:
    /// 循环收帧逐帧交给状态机,把宿主排进队列的本地击杀依次交给状态机的击杀口,清图通知到后自动发结算命令,两段结算通知收齐时结束。
    /// 多房间副本里还负责换房:宿主排进来的换房请求(<see cref="PostMoveRoom(OnlineMoveRoomRequest)"/>)留在队首等闸门放开(当前房间的怪全部被确认、装载握手完成、没有换房命令在途)
    /// 再发出;新房间的起始地图通知到后状态机换表,跑器接着自动发装载完成命令(<see cref="OnlineDungeonCombatOptions.AcknowledgeRoomLoaded"/>),等装载完成通知。
    /// 换房与装载完成这两步的回应各自等不过 <see cref="OnlineDungeonCombatOptions.RoomHandshakeTimeout"/> 就以 <see cref="OnlineDungeonCombatOutcome.Timeout"/> 收尾。
    /// <para>
    /// 它独占会话的收发(与进副本编排、保活泵一样,同一个会话同一时刻只能有一条逻辑线在收):所以心跳通知(类 0、包号 0)也由它自己回
    /// (回包同 <see cref="OnlineHeartbeat"/>:类 1、包号 0、8 个 0 字节),不然战斗期间服务端 300 秒无应答的心跳计数会累加到断线。
    /// </para>
    /// <para>
    /// 561 信封与进副本编排同口径:用解包委托取内层帧,内层帧走同一条处理路径(拆不开只计数、不中断)。
    /// 另外 <see cref="GameClientSession.ReceiveAsync"/> 会把拆开的内层帧作为一帧附在信封帧之后一并交回;
    /// 为了不让同一条通知被数两遍(状态机会把第二遍记成"重复"),跑器拆开信封后遇到紧随其后的、类、包号、载荷都相同的普通帧就跳过它,计入 <see cref="OnlineDungeonCombatResult.InnerCopiesSkipped"/>。
    /// </para>
    /// <para>
    /// 线程:<see cref="RunAsync"/> 在调用方的线程上异步跑(宿主一般丢到后台线程);<see cref="PostKill(int)"/> 与 <see cref="PostMoveRoom(OnlineMoveRoomRequest)"/> 可以从任意线程调用(并发队列),
    /// 击杀与换房在下一轮循环(最长一个 <see cref="OnlineDungeonCombatOptions.PollInterval"/>)里按入队顺序交给状态机。一个跑器只能跑一次。
    /// </para>
    /// <para>
    /// 多房间时击杀请用带房间序号的 <see cref="PostKill(int, int)"/>:换房之后本地编号会按新表重新从 0 起,不带房间序号的请求按"交给状态机那一刻的当前房间"解释,
    /// 排队期间恰好换了房就会错位报到别的房间的怪上。带了房间序号、而房间已经换掉的请求会被状态机拦下(<see cref="OnlineCombatBlock.StaleRoom"/>)。
    /// </para>
    /// <para>
    /// 从不因为连接、收发或取消而抛异常:取消返回 <see cref="OnlineDungeonCombatOutcome.Cancelled"/>,其余意外异常返回 <see cref="OnlineDungeonCombatOutcome.Faulted"/>
    /// (只记类型名)。日志回调自己抛的异常被吞掉。
    /// </para>
    /// </summary>
    public sealed class OnlineDungeonCombatRunner
    {
        /// <summary>一次最多打多少行事件日志(再多的只记一行"还有 N 条")。</summary>
        public const int MaxEventLines = 128;

        private readonly IOnlineDungeonSession _session;
        private readonly Func<byte[], byte[]> _unwrap;
        private readonly OnlineDungeonCombatOptions _options;
        private readonly ConcurrentQueue<KillRequest> _kills = new ConcurrentQueue<KillRequest>();
        private readonly ConcurrentQueue<MoveRequest> _moves = new ConcurrentQueue<MoveRequest>();
        private int _started;
        private volatile bool _ended;
        private volatile OnlineDungeonCombat _machine;

        /// <summary>在已进城的真会话上创建;<paramref name="unwrapEnvelope"/> 是 561 信封的解包委托(同 <see cref="OnlineDungeonEntry"/>),可以为 null。</summary>
        public OnlineDungeonCombatRunner(GameClientSession session, Func<byte[], byte[]> unwrapEnvelope = null, OnlineDungeonCombatOptions options = null)
            : this(new GameClientSessionAdapter(session ?? throw new ArgumentNullException(nameof(session))), unwrapEnvelope, options)
        {
        }

        /// <summary>在会话接缝的任意实现上创建(合成测试用)。</summary>
        public OnlineDungeonCombatRunner(IOnlineDungeonSession session, Func<byte[], byte[]> unwrapEnvelope = null, OnlineDungeonCombatOptions options = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _unwrap = unwrapEnvelope;
            _options = options ?? new OnlineDungeonCombatOptions();
            if (_options.PollInterval <= TimeSpan.Zero || _options.PollInterval.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "PollInterval 必须大于 0 且不超过 int.MaxValue 毫秒");
            }
            if (_options.SettlementTimeout <= TimeSpan.Zero || _options.SettlementTimeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "SettlementTimeout 必须大于 0 且不超过 int.MaxValue 毫秒");
            }
            if (_options.OverallTimeout.HasValue && (_options.OverallTimeout.Value <= TimeSpan.Zero || _options.OverallTimeout.Value.TotalMilliseconds > int.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "OverallTimeout 必须大于 0 且不超过 int.MaxValue 毫秒");
            }
            if (_options.RoomHandshakeTimeout <= TimeSpan.Zero || _options.RoomHandshakeTimeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RoomHandshakeTimeout 必须大于 0 且不超过 int.MaxValue 毫秒");
            }
            if (_options.MaxMoveRefusalsPerRoom < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMoveRefusalsPerRoom 至少为 1");
            }
        }

        /// <summary>
        /// 跑起来之后的状态机(还没开始跑为 null)。只读用:对应表里的本地编号、服务端编号与记录在建好之后不再变;
        /// 击杀进度与各种计数由循环线程写,别的线程读到的是尽力而为的值。
        /// </summary>
        public OnlineDungeonCombat Machine => _machine;

        /// <summary>这次战斗是否已经结束(结束后 <see cref="PostKill(int)"/> 与 <see cref="PostMoveRoom(OnlineMoveRoomRequest)"/> 不再收)。</summary>
        public bool Ended => _ended;

        /// <summary>
        /// 一只本地怪死了,排进击杀队列(线程安全,立刻返回)。参数是本地编号(= 当前房间起始地图怪记录的序号);
        /// 编号合不合法、是不是已经报过,由状态机在交给它时判断(不合法的被本地拦下、不发包)。
        /// 请求不绑定房间:按交给状态机那一刻的当前房间的表解释,只有一张图的副本这就是起始地图的表;多房间时请用 <see cref="PostKill(int, int)"/>。
        /// 返回是否收下:战斗已结束返回 false(宿主可以据此不再轮询击杀)。
        /// </summary>
        public bool PostKill(int localId)
        {
            if (_ended)
            {
                return false;
            }
            _kills.Enqueue(new KillRequest(-1, localId));
            return true;
        }

        /// <summary>
        /// 一只本地怪死了,请求绑定在序号为 <paramref name="room"/> 的房间(<see cref="OnlineDungeonCombat.Room"/>):交给状态机时房间已经不是它,就被本地拦下(<see cref="OnlineCombatBlock.StaleRoom"/>)。
        /// 线程安全,立刻返回;返回是否收下(战斗已结束返回 false)。
        /// </summary>
        /// <param name="room">请求针对的房间序号,不小于 0。</param>
        /// <param name="localId">该房间对应表里的本地编号。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="room"/> 为负。</exception>
        public bool PostKill(int room, int localId)
        {
            if (room < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(room), "房间序号不能为负");
            }
            if (_ended)
            {
                return false;
            }
            _kills.Enqueue(new KillRequest(room, localId));
            return true;
        }

        /// <summary>
        /// 当前房间的怪都打完了,请求换到下一个房间(线程安全,立刻返回)。请求绑定在调用这一刻状态机所在的房间(还没开始跑时是起始房间 0),
        /// 留在换房队列的队首,等状态机的闸门放开(当前房间的怪全部被服务端确认、装载握手完成、没有换房命令在途、这个房间的拒绝额度没用完)才真正发出;
        /// 房间已经换掉、已经清图或进入结算、拒绝额度用完的请求被丢弃(状态机留一条 <see cref="OnlineCombatEventKind.RoomMoveBlocked"/> 事件)。
        /// 一次只发一条:发出后要等到新房间的起始地图通知或失败回包,队列里后面的请求才轮得到。返回是否收下(战斗已结束返回 false)。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 null。</exception>
        public bool PostMoveRoom(OnlineMoveRoomRequest request)
        {
            OnlineDungeonCombat machine = _machine;
            return PostMoveRoom(machine == null ? 0 : machine.Room, request);
        }

        /// <summary>同 <see cref="PostMoveRoom(OnlineMoveRoomRequest)"/>,请求显式绑定在序号为 <paramref name="room"/> 的房间。</summary>
        /// <param name="room">请求针对的房间序号,不小于 0。</param>
        /// <param name="request">换房请求(目标格、位置、分层标志)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="room"/> 为负。</exception>
        public bool PostMoveRoom(int room, OnlineMoveRoomRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (room < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(room), "房间序号不能为负");
            }
            if (_ended)
            {
                return false;
            }
            _moves.Enqueue(new MoveRequest(room, request));
            return true;
        }

        /// <summary>
        /// 跑状态机直到回城就绪、连接断开、取消或超时。<paramref name="map"/> 是进副本拿到的起始地图(<see cref="OnlineDungeonResult.StartMap"/>)。
        /// 每个阶段与结局打成以 <see cref="OnlineCombatLog.Prefix"/> 开头的行交给 <paramref name="log"/>(不含换行;null 表示不打)。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="map"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">这个跑器已经跑过一次。</exception>
        public async Task<OnlineDungeonCombatResult> RunAsync(StartMapNotice map, Action<string> log, CancellationToken cancellationToken = default)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("一个战斗跑器只能跑一次");
            }

            var attempt = new Attempt(this, map, log, cancellationToken);
            try
            {
                return await attempt.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                _ended = true;
            }
        }

        /// <summary>一次战斗的可变状态(每次调用一个,跑器对象本身只存跨线程共享的队列与标志)。</summary>
        private sealed class Attempt
        {
            private readonly OnlineDungeonCombatRunner _owner;
            private readonly IOnlineDungeonSession _session;
            private readonly OnlineDungeonCombat _combat;
            private readonly OnlineDungeonCombatOptions _options;
            private readonly Action<string> _log;
            private readonly CancellationToken _cancel;
            private readonly Stopwatch _watch = Stopwatch.StartNew();

            private int _flushed;
            private int _eventLines;
            private int _omittedLines;
            private TimeSpan _settlementSentAt;
            private TimeSpan _moveSentAt;
            private TimeSpan _loadSentAt;
            private int _heartbeats;
            private int _envelopesOpened;
            private int _envelopesUnopened;
            private int _innerCopiesSkipped;
            private int _framesSeen;
            private InnerCopy _copy;

            public Attempt(OnlineDungeonCombatRunner owner, StartMapNotice map, Action<string> log, CancellationToken cancel)
            {
                _owner = owner;
                _session = owner._session;
                _options = owner._options;
                _log = log;
                _cancel = cancel;
                _combat = new OnlineDungeonCombat(map, new SendPort(_session), _options);
                owner._machine = _combat;
            }

            public async Task<OnlineDungeonCombatResult> RunAsync()
            {
                Say(OnlineCombatLog.Begin(_combat));
                Flush();

                OnlineDungeonCombatOutcome outcome;
                string fault = null;
                try
                {
                    outcome = await LoopAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    outcome = OnlineDungeonCombatOutcome.Cancelled;
                }
                catch (Exception e)
                {
                    outcome = OnlineDungeonCombatOutcome.Faulted;
                    fault = e.GetBaseException().GetType().Name;
                }

                Flush();
                if (_omittedLines > 0)
                {
                    Say(OnlineCombatLog.EventsOmitted(_omittedLines));
                }
                var result = new OnlineDungeonCombatResult(outcome, _combat, _heartbeats, _envelopesOpened, _envelopesUnopened,
                    _innerCopiesSkipped, _framesSeen, fault);
                Say(OnlineCombatLog.End(result));
                return result;
            }

            private async Task<OnlineDungeonCombatOutcome> LoopAsync()
            {
                while (true)
                {
                    _cancel.ThrowIfCancellationRequested();

                    // 一:装载握手——状态机说装载完成命令该发了(进了要握手的房间)就立刻发,不等别的。
                    // 放在击杀之前是有意的:真客户端是装载完才开打(笔记 E-370:换房 = 换房命令 + 起始地图通知 + 装载完成命令),
                    // 新房间的头一条击杀命令不能抢在装载完成命令前面;换房之后宿主一看到新房间(例如在 RoomEntered 事件回调里)就排的击杀,最早也只在下一圈、握手命令之后发出
                    if (_combat.LoadState == OnlineRoomLoadState.ReportDue)
                    {
                        OnlineCombatSendResult sent = await _combat.ReportRoomLoadedAsync(_cancel).ConfigureAwait(false);
                        Flush();
                        if (sent.Outcome == OnlineCombatSendOutcome.FrameFailed)
                        {
                            return OnlineDungeonCombatOutcome.SendFailed;
                        }
                        if (sent.Sent)
                        {
                            _loadSentAt = _watch.Elapsed;
                        }
                    }

                    // 二:宿主排进来的本地击杀,按入队顺序交给状态机的击杀口(拦与不拦由状态机定;带房间序号的请求绑定那个房间)
                    while (_owner._kills.TryDequeue(out KillRequest kill))
                    {
                        OnlineCombatSendResult sent = kill.Room >= 0
                            ? await _combat.ReportKillAsync(kill.Room, kill.LocalId, _cancel).ConfigureAwait(false)
                            : await _combat.ReportKillAsync(kill.LocalId, _cancel).ConfigureAwait(false);
                        Flush();
                        if (sent.Outcome == OnlineCombatSendOutcome.FrameFailed)
                        {
                            return OnlineDungeonCombatOutcome.SendFailed;
                        }
                    }

                    // 三:换房——队首的请求等闸门放开才发,一次只发一条
                    if (await PumpMovesAsync().ConfigureAwait(false) == OnlineCombatSendOutcome.FrameFailed)
                    {
                        return OnlineDungeonCombatOutcome.SendFailed;
                    }

                    // 四:清图通知到了就发结算命令(闸门在状态机里:只有清图后的阶段才放行)
                    if (_combat.Phase == OnlineCombatPhase.Cleared)
                    {
                        OnlineCombatSendResult sent = await _combat.RequestSettlementAsync(_cancel).ConfigureAwait(false);
                        Flush();
                        if (sent.Outcome == OnlineCombatSendOutcome.FrameFailed)
                        {
                            return OnlineDungeonCombatOutcome.SendFailed;
                        }
                        if (sent.Sent)
                        {
                            _settlementSentAt = _watch.Elapsed;
                        }
                    }

                    // 五:终局与超时
                    if (_combat.Phase == OnlineCombatPhase.Settled)
                    {
                        return OnlineDungeonCombatOutcome.ReturnReady;
                    }
                    TimeSpan now = _watch.Elapsed;
                    if (_options.OverallTimeout.HasValue && now >= _options.OverallTimeout.Value)
                    {
                        return OnlineDungeonCombatOutcome.Timeout;
                    }
                    if (_combat.Phase == OnlineCombatPhase.Settling && now - _settlementSentAt >= _options.SettlementTimeout)
                    {
                        return OnlineDungeonCombatOutcome.Timeout;
                    }
                    // 换房与装载完成各等各的回应,谁等过了 RoomHandshakeTimeout 就收尾(进了结算之后换房不再有意义,在途的那一条也不再计时)
                    if (_combat.Phase == OnlineCombatPhase.Fighting && _combat.MoveInFlight && now - _moveSentAt >= _options.RoomHandshakeTimeout)
                    {
                        return OnlineDungeonCombatOutcome.Timeout;
                    }
                    if (_combat.LoadState == OnlineRoomLoadState.AwaitingNotice && now - _loadSentAt >= _options.RoomHandshakeTimeout)
                    {
                        return OnlineDungeonCombatOutcome.Timeout;
                    }

                    // 六:收帧(一帧一返回,有帧立刻处理;没有就最多等一个轮询窗口)
                    List<DecodedFrame> frames = await _session.ReceiveAsync(_options.PollInterval, 1, _cancel).ConfigureAwait(false);
                    if (frames != null && frames.Count > 0)
                    {
                        OnlineDungeonCombatOutcome? stop = await ProcessAsync(frames).ConfigureAwait(false);
                        if (stop.HasValue)
                        {
                            return stop.Value;
                        }
                        continue;
                    }

                    // 没读到帧:只有断了、切帧出错或到点三种可能,断与错要先认,免得空转
                    if (_session.PeerClosed)
                    {
                        return OnlineDungeonCombatOutcome.ConnectionClosed;
                    }
                    if (_session.LastFrameError != FrameError.None)
                    {
                        return OnlineDungeonCombatOutcome.FramingFault;
                    }
                }
            }

            // 处理换房队列的队首:闸门暂时没放开(换房在途、装载握手没完、当前房间没清)就原样留着下一轮再看;
            // 放开了就发,发出去之后这一轮不再看后面的(要等这一条的回应);被永久拦下的(房间已换、已清图、额度用完)交给状态机记一条事件后丢掉,接着看下一条。
            // 返回最后一次交给状态机的结局(没交过返回 Blocked,调用方只关心是不是帧层失败)。
            private async Task<OnlineCombatSendOutcome> PumpMovesAsync()
            {
                OnlineCombatSendOutcome last = OnlineCombatSendOutcome.Blocked;
                while (_owner._moves.TryPeek(out MoveRequest move))
                {
                    OnlineCombatBlock gate = _combat.MoveGate(move.Room);
                    if (gate == OnlineCombatBlock.MoveInFlight || gate == OnlineCombatBlock.LoadPending || gate == OnlineCombatBlock.RoomNotCleared)
                    {
                        return last;
                    }

                    _owner._moves.TryDequeue(out _);        // 循环线程是唯一的消费者,队首就是刚看到的这一条
                    OnlineCombatSendResult sent = await _combat.RequestMoveRoomAsync(move.Room, move.Request, _cancel).ConfigureAwait(false);
                    Flush();
                    last = sent.Outcome;
                    if (sent.Outcome == OnlineCombatSendOutcome.FrameFailed)
                    {
                        return last;
                    }
                    if (sent.Sent)
                    {
                        _moveSentAt = _watch.Elapsed;
                        return last;
                    }
                }
                return last;
            }

            private async Task<OnlineDungeonCombatOutcome?> ProcessAsync(List<DecodedFrame> frames)
            {
                _copy = null;           // 会话把内层副本附在信封之后、同一批里交回;上一批留下的标记不跨批
                for (int i = 0; i < frames.Count; i++)
                {
                    DecodedFrame frame = frames[i];
                    _framesSeen++;
                    InnerCopy copy = _copy;
                    _copy = null;
                    if (copy != null && copy.Matches(frame))
                    {
                        _innerCopiesSkipped++;
                        continue;
                    }

                    PacketClass cls = frame.Header.Class;
                    int id = frame.Header.PacketId;
                    byte[] payload = frame.Payload ?? new byte[0];
                    OnlineDungeonCombatOutcome? stop;
                    if (cls == PacketClass.Noti && id == OnlineDungeonEntry.ServerEnvelopePacketId)
                    {
                        _combat.OnFrame(cls, id, payload);          // 信封帧本身不在状态机的包集合里:记一笔透传
                        stop = await OpenEnvelopeAsync(payload).ConfigureAwait(false);
                    }
                    else
                    {
                        stop = await DispatchAsync(cls, id, payload).ConfigureAwait(false);
                    }
                    Flush();
                    if (stop.HasValue)
                    {
                        return stop;
                    }
                }
                return null;
            }

            // 一帧(直接收到的或信封里解出的):心跳自己回,其余交给状态机
            private async Task<OnlineDungeonCombatOutcome?> DispatchAsync(PacketClass cls, int id, byte[] payload)
            {
                if (cls == PacketClass.Noti && id == OnlineHeartbeat.HeartbeatPacketId)
                {
                    FrameStatus status = await _session.SendAsync(PacketClass.Cmd, OnlineHeartbeat.ReplyPacketId, OnlineHeartbeat.ReplyPayload, _cancel).ConfigureAwait(false);
                    if (status != FrameStatus.Ok)
                    {
                        return OnlineDungeonCombatOutcome.SendFailed;
                    }
                    _heartbeats++;
                }
                _combat.OnFrame(cls, id, payload);
                return null;
            }

            // 561 信封:交给委托取内层帧;取不到就只计数,取到了就把内层帧走同一条处理路径(同 OnlineDungeonEntry 的口径:不递归、压缩帧不解)
            private async Task<OnlineDungeonCombatOutcome?> OpenEnvelopeAsync(byte[] envelopePayload)
            {
                if (_owner._unwrap == null)
                {
                    _envelopesUnopened++;
                    return null;
                }

                byte[] inner;
                try
                {
                    inner = _owner._unwrap(envelopePayload);
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
                    _envelopesUnopened++;
                    return null;
                }

                _envelopesOpened++;
                _copy = new InnerCopy(header.Class, header.PacketId, innerPayload);
                if (header.Class == PacketClass.Noti && header.PacketId == OnlineDungeonEntry.ServerEnvelopePacketId)
                {
                    _combat.OnFrame(header.Class, header.PacketId, innerPayload);       // 信封套信封不递归拆,只记透传
                    return null;
                }
                return await DispatchAsync(header.Class, header.PacketId, innerPayload).ConfigureAwait(false);
            }

            // 把状态机新产出的事件打成日志行并回调宿主
            private void Flush()
            {
                IReadOnlyList<OnlineCombatEvent> events = _combat.Events;
                while (_flushed < events.Count)
                {
                    OnlineCombatEvent e = events[_flushed++];
                    if (e.Kind != OnlineCombatEventKind.MonstersSpawned)
                    {
                        if (_eventLines < MaxEventLines)
                        {
                            _eventLines++;
                            Say(OnlineCombatLog.Event(e));
                        }
                        else
                        {
                            _omittedLines++;
                        }
                    }
                    Action<OnlineCombatEvent> callback = _options.OnEvent;
                    if (callback != null)
                    {
                        try
                        {
                            callback(e);
                        }
                        catch (Exception)
                        {
                            // 宿主的事件回调出错不能影响战斗循环
                        }
                    }
                }
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
                    // 日志回调自己出错不能再往外抛
                }
            }
        }

        /// <summary>排队的一次击杀:<see cref="Room"/> 为 -1 表示不绑定房间(按交给状态机那一刻的当前房间)。</summary>
        private readonly struct KillRequest
        {
            public KillRequest(int room, int localId)
            {
                Room = room;
                LocalId = localId;
            }

            public int Room { get; }

            public int LocalId { get; }
        }

        /// <summary>排队的一次换房:绑定在发出请求时的房间。</summary>
        private readonly struct MoveRequest
        {
            public MoveRequest(int room, OnlineMoveRoomRequest request)
            {
                Room = room;
                Request = request;
            }

            public int Room { get; }

            public OnlineMoveRoomRequest Request { get; }
        }

        /// <summary>刚拆开的信封里那一帧的类、包号与载荷,用来认出会话附在信封之后的内层副本。</summary>
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
                byte[] other = frame.Payload ?? new byte[0];
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

        /// <summary>状态机的发送口:转调会话接缝的发送。</summary>
        private sealed class SendPort : IOnlineCombatPort
        {
            private readonly IOnlineDungeonSession _session;

            public SendPort(IOnlineDungeonSession session)
            {
                _session = session;
            }

            public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
            {
                return _session.SendAsync(packetClass, packetId, payload, cancellationToken);
            }
        }

        /// <summary>真会话的适配器:一对一转调,不加任何行为(与 <see cref="OnlineDungeonEntry"/> 里的那一个同形;它是私有的,这里不动它)。</summary>
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

    /// <summary>
    /// 联网战斗日志行的拼法(纯函数,调用方只管打)。每一行都以 <see cref="Prefix"/> 开头,只含阶段名、枚举名、本地序号、包类与包号和各种计数,
    /// <b>不含任何载荷内容</b>(包括服务端怪编号),也不含异常消息;数字一律按固定文化输出;没有值的字段写 <c>-</c>。
    /// 一个有意的例外:换房失败那一行带服务端回的错误码(<c>code=</c>,一个小整数的协议枚举值,没有它就没法在真服上对账是哪一道检查拒的);
    /// 换房成功那一行带新起始地图读完之后载荷里剩的字节数(<c>trailing=</c>,一个计数,用来对账怪记录的步长)。
    /// </summary>
    public static class OnlineCombatLog
    {
        /// <summary>联网战斗各行的统一前缀。</summary>
        public const string Prefix = "DNF_NET stage=combat";

        /// <summary>汇总行里最多列出多少个不同的透传包号(再多的只记一个"+N")。</summary>
        public const int MaxPassthroughIds = 32;

        /// <summary>战斗开始:怪数(对应表的大小)与编号重复的怪数。</summary>
        internal static string Begin(OnlineDungeonCombat combat)
        {
            return Prefix + " begin monsters=" + Number(combat.Total) + " duplicateNumbers=" + Number(combat.DuplicateNumbers);
        }

        /// <summary>一条状态机事件:事件名加该事件相关的几个枚举名与计数。</summary>
        internal static string Event(OnlineCombatEvent e)
        {
            var sb = new StringBuilder(Prefix);
            sb.Append(" event=").Append(e.Kind);
            switch (e.Kind)
            {
                case OnlineCombatEventKind.KillSent:
                case OnlineCombatEventKind.KillConfirmed:
                case OnlineCombatEventKind.KillRefused:
                    sb.Append(" local=").Append(Number(e.LocalId));
                    AppendProgress(sb, e);
                    break;
                case OnlineCombatEventKind.KillBlocked:
                    sb.Append(" local=").Append(Number(e.LocalId));
                    sb.Append(" block=").Append(e.Block);
                    break;
                case OnlineCombatEventKind.MapCleared:
                    AppendProgress(sb, e);
                    break;
                case OnlineCombatEventKind.SettlementBlocked:
                    sb.Append(" block=").Append(e.Block);
                    break;
                case OnlineCombatEventKind.SettlementArrived:
                    sb.Append(" part=").Append(e.Part);
                    break;
                case OnlineCombatEventKind.RoomMoveSent:
                case OnlineCombatEventKind.RoomLoadedSent:
                case OnlineCombatEventKind.RoomLoaded:
                    sb.Append(" room=").Append(Number(e.Room));
                    break;
                case OnlineCombatEventKind.RoomMoveBlocked:
                case OnlineCombatEventKind.RoomLoadedBlocked:
                    sb.Append(" room=").Append(Number(e.Room));
                    sb.Append(" block=").Append(e.Block);
                    break;
                case OnlineCombatEventKind.RoomMoveRefused:
                    sb.Append(" room=").Append(Number(e.Room));
                    sb.Append(" code=").Append(Number(e.ReplyCode));
                    break;
                case OnlineCombatEventKind.RoomEntered:
                    sb.Append(" room=").Append(Number(e.Room));
                    sb.Append(" roomMonsters=").Append(Number(e.RoomMonsters));
                    sb.Append(" trailing=").Append(Number(e.TrailingBytes));
                    sb.Append(" total=").Append(Number(e.Total));
                    break;
                case OnlineCombatEventKind.FrameIgnored:
                    sb.Append(" frame=").Append(FrameName(e.PacketClass, e.PacketId));
                    sb.Append(" verdict=").Append(e.Verdict);
                    if (e.LocalId >= 0)
                    {
                        sb.Append(" local=").Append(Number(e.LocalId));
                    }
                    break;
            }
            return sb.ToString();
        }

        internal static string EventsOmitted(int count)
        {
            return Prefix + " events omitted=" + Number(count);
        }

        /// <summary>
        /// 战斗结束的一行:走到可回城是 <c>done</c>,其余结局是 <c>ended</c>;带结局、终了阶段、状态机各计数、跑器各计数与透传包号。
        /// 走过换房或装载握手的战斗,在 <c>frames=</c> 之后多一段 <c>room=</c>(结束时所在房间)、<c>moves=</c>(发出数/被拒数)、<c>loadReports=</c>(装载完成命令数)、
        /// <c>waiting=</c>(结束时还在等什么:<c>move</c> = 换房命令在途,<c>AwaitingNotice</c> = 等装载完成通知,<c>ReportDue</c> = 装载完成命令该发还没发,<c>-</c> = 没有);
        /// 只有起始地图的战斗没有这一段,整行与以前逐字节一样。
        /// </summary>
        internal static string End(OnlineDungeonCombatResult r)
        {
            var sb = new StringBuilder(Prefix);
            sb.Append(r.ReturnReady ? " done" : " ended");
            sb.Append(" outcome=").Append(r.Outcome);
            sb.Append(" phase=").Append(r.Phase);
            sb.Append(" monsters=").Append(Number(r.Monsters));
            sb.Append(" sent=").Append(Number(r.Sent));
            sb.Append(" confirmed=").Append(Number(r.Confirmed));
            sb.Append(" refused=").Append(Number(r.Refused));
            sb.Append(" blocked=").Append(Number(r.Blocked));
            sb.Append(" outOfOrder=").Append(Number(r.OutOfOrder));
            sb.Append(" duplicate=").Append(Number(r.Duplicates));
            sb.Append(" unknownNumber=").Append(Number(r.UnknownNumbers));
            sb.Append(" malformed=").Append(Number(r.Malformed));
            sb.Append(" passthrough=").Append(Number(r.PassthroughCount));
            sb.Append(" heartbeats=").Append(Number(r.HeartbeatsAnswered));
            sb.Append(" envelopes=").Append(Number(r.EnvelopesOpened)).Append('/').Append(Number(r.EnvelopesUnopened));
            sb.Append(" innerSkipped=").Append(Number(r.InnerCopiesSkipped));
            sb.Append(" frames=").Append(Number(r.FramesSeen));
            if (r.Room > 0 || r.MovesSent > 0 || r.LoadReports > 0)
            {
                // 走过换房或装载握手的战斗才有这一段;只有起始地图的战斗这一行与以前逐字节一样
                sb.Append(" room=").Append(Number(r.Room));
                sb.Append(" moves=").Append(Number(r.MovesSent)).Append('/').Append(Number(r.MoveRefusals));
                sb.Append(" loadReports=").Append(Number(r.LoadReports));
                sb.Append(" waiting=").Append(r.MoveInFlight ? "move" : (r.LoadState == OnlineRoomLoadState.None ? "-" : r.LoadState.ToString()));
            }
            if (r.FaultType != null)
            {
                sb.Append(" exception=").Append(r.FaultType);
            }
            sb.Append(" passIds=").Append(PassthroughIds(r.Passthrough));
            return sb.ToString();
        }

        private static void AppendProgress(StringBuilder sb, OnlineCombatEvent e)
        {
            sb.Append(" sent=").Append(Number(e.Sent));
            sb.Append(" confirmed=").Append(Number(e.Confirmed));
            sb.Append(" total=").Append(Number(e.Total));
        }

        private static string PassthroughIds(IReadOnlyList<OnlineCombatPacketCount> counts)
        {
            if (counts == null || counts.Count == 0)
            {
                return "-";
            }
            var sb = new StringBuilder();
            int listed = Math.Min(counts.Count, MaxPassthroughIds);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(FrameName(counts[i].PacketClass, counts[i].PacketId)).Append(':').Append(Number(counts[i].Count));
            }
            if (counts.Count > listed)
            {
                sb.Append(",+").Append(Number(counts.Count - listed));
            }
            return sb.ToString();
        }

        private static string FrameName(PacketClass cls, int id)
        {
            string name = cls == PacketClass.Cmd ? "CMD" : (cls == PacketClass.Noti ? "NOTI" : "类" + Number((int)cls));
            return name + "/" + Number(id);
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
