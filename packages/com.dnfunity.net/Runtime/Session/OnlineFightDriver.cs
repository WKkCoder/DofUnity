using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.Session
{
    /// <summary>无头战斗驱动(<see cref="OnlineFightDriver"/>)的结局。只描述走到哪一步停下,不含任何载荷内容。</summary>
    public enum OnlineFightOutcome
    {
        /// <summary>清图通知到了、两段结算通知收齐,可以回城(唯一的成功结局)。</summary>
        AllDone = 0,

        /// <summary>服务端回了击杀失败(被拒):缺省首次被拒就不再发击杀并收场(<see cref="OnlineFightOptions.ContinueOnRefuse"/> 为真时不会有这个结局)。</summary>
        RefusedStop,

        /// <summary>击杀都发完了(或根本没有怪),等清图通知超时:服务端没说清图,结算闸门一直关着,结算命令一条没发。</summary>
        NoClearNotice,

        /// <summary>清图通知到了、结算命令发出了,但两段结算通知没在等待时间内收齐。</summary>
        SettleIncomplete,

        /// <summary>对端关闭了连接,或切帧出错、会话不能再用。</summary>
        SessionClosed,

        /// <summary>收发抛了异常(类型名在 <see cref="OnlineFightSummary.FaultType"/>),或帧层没能编码出要发的帧。</summary>
        Faulted,

        /// <summary>取消令牌触发(安静结束,不抛异常)。六个约定结局之外多出的一个:取消不是服务端行为,单列出来免得被误读成别的。</summary>
        Cancelled,
    }

    /// <summary>两段结算通知(0x22 通关结果、0x23 通关奖励)收到了几段。</summary>
    public enum OnlineFightSettlement
    {
        /// <summary>一段也没收到。</summary>
        None = 0,

        /// <summary>只收到一段(状态机要求结果先于奖励,所以是结果到了、奖励没到)。</summary>
        Partial,

        /// <summary>两段收齐。</summary>
        Complete,
    }

    /// <summary>
    /// 击杀载荷在服务端那里的待遇(本条线要回答的头号问题)的一句话判读,只看计数:
    /// 发出去的击杀是被确认了、被拒了,还是没有任何回音。
    /// </summary>
    public enum OnlineFightKillVerdict
    {
        /// <summary>一条击杀都没发出去(还没发就收场了,或者全被本地拦下)。</summary>
        None = 0,

        /// <summary>发出去的击杀全部被服务端确认。</summary>
        Accepted,

        /// <summary>一部分被确认,其余既没确认也没拒绝(收场时仍在途,或服务端没回音);没有被拒。</summary>
        Partial,

        /// <summary>至少有一条被服务端拒绝(回了击杀失败)。</summary>
        Refused,

        /// <summary>发出去了,但一条确认也没有、一条拒绝也没有:服务端静默(载荷被忽略、读不满,或已断线)。</summary>
        Silent,
    }

    /// <summary>无头战斗驱动的可调项,全有缺省值。</summary>
    public sealed class OnlineFightOptions
    {
        /// <summary>相邻两次排击杀之间的缺省间隔(毫秒)。</summary>
        public const int DefaultPaceMs = 400;

        /// <summary>发完击杀后等清图通知的缺省时长。</summary>
        public static readonly TimeSpan DefaultClearWait = TimeSpan.FromSeconds(25);

        /// <summary>清图通知到了之后等两段结算通知收齐的缺省时长。</summary>
        public static readonly TimeSpan DefaultSettleWait = TimeSpan.FromSeconds(25);

        /// <summary>两段等待里回头看一次跑器状态的缺省步长。</summary>
        public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// 相邻两次排击杀之间等多久(毫秒,取驱动的时钟):第 0 只立刻排,之后每只与前一只隔这么久;0 表示不等。缺省 <see cref="DefaultPaceMs"/>。
        /// </summary>
        public int PaceMs { get; set; } = DefaultPaceMs;

        /// <summary>
        /// 首次被拒之后是否继续排剩下的击杀。缺省 false:一旦服务端回了击杀失败,就不再发击杀、立即以 <see cref="OnlineFightOutcome.RefusedStop"/> 收场
        /// (拒了一次基本说明载荷或编号有问题,再发只会多记反作弊计数)。
        /// </summary>
        public bool ContinueOnRefuse { get; set; }

        /// <summary>最后一只的击杀排完之后,等清图通知的最长时间(取驱动的时钟);没有怪时从开始就起算。缺省 <see cref="DefaultClearWait"/>。</summary>
        public TimeSpan ClearWait { get; set; } = DefaultClearWait;

        /// <summary>清图通知到达之后,等两段结算通知收齐的最长时间(取驱动的时钟)。缺省 <see cref="DefaultSettleWait"/>。</summary>
        public TimeSpan SettleWait { get; set; } = DefaultSettleWait;

        /// <summary>两段等待里每隔多久回头看一次跑器的状态(清图了没有、被拒了没有、收场了没有)。缺省 <see cref="DefaultPollInterval"/>。</summary>
        public TimeSpan PollInterval { get; set; } = DefaultPollInterval;

        /// <summary>时钟;null 用 <see cref="SystemFightClock.Instance"/>。测试换成手拨的假时钟。</summary>
        public IFightClock Clock { get; set; }
    }

    /// <summary>
    /// 一次无头战斗的汇总:结局加一串计数与状态名,<b>不含任何载荷内容</b>(也没有服务端怪编号)。
    /// 计数口径与状态机一致(<see cref="OnlineDungeonCombat"/>):<see cref="Sent"/> 是真发到线上的击杀命令数,<see cref="Blocked"/> 是被本地拦下(或没能发出)的发送次数,
    /// <see cref="Posted"/> 是驱动自己排进队列的击杀数(排进去不等于发出去:状态机会拦、跑器可能已经收场)。
    /// </summary>
    public sealed class OnlineFightSummary
    {
        internal OnlineFightSummary(OnlineFightOutcome outcome, int monsters, int posted, int sent, int confirmed, int refused, int blocked,
            bool clearNoticeSeen, OnlineFightSettlement settlement, bool returnReady, OnlineCombatPhase phase,
            OnlineDungeonCombatOutcome? runnerOutcome, string faultType, int firstRefusedLocal, int heartbeatsAnswered,
            TimeSpan elapsed, IReadOnlyList<TimeSpan> postTimes)
        {
            Outcome = outcome;
            Monsters = monsters;
            Posted = posted;
            Sent = sent;
            Confirmed = confirmed;
            Refused = refused;
            Blocked = blocked;
            ClearNoticeSeen = clearNoticeSeen;
            Settlement = settlement;
            ReturnReady = returnReady;
            Phase = phase;
            RunnerOutcome = runnerOutcome;
            FaultType = faultType;
            FirstRefusedLocal = firstRefusedLocal;
            HeartbeatsAnswered = heartbeatsAnswered;
            Elapsed = elapsed;
            PostTimes = postTimes;
        }

        /// <summary>结局。</summary>
        public OnlineFightOutcome Outcome { get; }

        /// <summary>起始地图的怪数(对应表的大小)。</summary>
        public int Monsters { get; }

        /// <summary>驱动排进击杀队列的怪数(没有被取消、被拒即停或提前清图打断时等于怪数)。</summary>
        public int Posted { get; }

        /// <summary>真发到线上的击杀命令数(状态机口径)。</summary>
        public int Sent { get; }

        /// <summary>被服务端确认的击杀数。</summary>
        public int Confirmed { get; }

        /// <summary>被服务端拒绝(回了击杀失败)的击杀数。</summary>
        public int Refused { get; }

        /// <summary>被本地拦下(或没能发出)的发送次数,击杀与结算合计。</summary>
        public int Blocked { get; }

        /// <summary>是否收到了清图通知。</summary>
        public bool ClearNoticeSeen { get; }

        /// <summary>两段结算通知收到了几段。</summary>
        public OnlineFightSettlement Settlement { get; }

        /// <summary>是否可以回城:两段结算通知收齐(状态机走到了终局)。</summary>
        public bool ReturnReady { get; }

        /// <summary>收场时状态机所处的阶段。</summary>
        public OnlineCombatPhase Phase { get; }

        /// <summary>跑器自己的结局;跑器没在收尾宽限内返回(或根本没跑起来)时为 null。</summary>
        public OnlineDungeonCombatOutcome? RunnerOutcome { get; }

        /// <summary>抛出的异常的类型名(没有为 null;只有类型名,不带消息)。</summary>
        public string FaultType { get; }

        /// <summary>第一只被拒的怪的本地序号(没有为 -1;只是起始地图记录里的序号,不是服务端编号)。</summary>
        public int FirstRefusedLocal { get; }

        /// <summary>跑器在战斗期间自己回的心跳数。</summary>
        public int HeartbeatsAnswered { get; }

        /// <summary>从驱动开始到收场用的时间(取驱动的时钟)。</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>每次排击杀时驱动的时钟读数(相对驱动开始),按排的顺序;第 0 只恒为 0 附近。</summary>
        public IReadOnlyList<TimeSpan> PostTimes { get; }

        /// <summary>已发出击杀却还没等到确认或拒绝的怪数(收场时的在途数)。</summary>
        public int Pending => Sent - Confirmed - Refused;

        /// <summary>击杀载荷待遇的一句话判读(见 <see cref="OnlineFightKillVerdict"/>)。</summary>
        public OnlineFightKillVerdict KillVerdict
        {
            get
            {
                if (Sent == 0)
                {
                    return OnlineFightKillVerdict.None;
                }
                if (Refused > 0)
                {
                    return OnlineFightKillVerdict.Refused;
                }
                if (Confirmed == Sent)
                {
                    return OnlineFightKillVerdict.Accepted;
                }
                return Confirmed == 0 ? OnlineFightKillVerdict.Silent : OnlineFightKillVerdict.Partial;
            }
        }

        /// <summary>一行文字:结局加各计数(不含任何载荷内容)。</summary>
        public override string ToString()
        {
            return OnlineFightLog.ResultFields(this);
        }
    }

    /// <summary>
    /// 联网战斗的无头驱动:给一个还没跑过的战斗跑器(<see cref="OnlineDungeonCombatRunner"/>)与进副本拿到的起始地图,
    /// 不要渲染、不要本地怪,把"杀光起始地图的怪 → 清图 → 结算 → 可回城"自己走一遍,返回只含状态名与计数的汇总(<see cref="OnlineFightSummary"/>)。
    /// 它是 Unity 宿主(<c>TownDemo</c> + <c>OnlineDungeonRoom</c>)那条链的无头版:同一个跑器、同一个状态机、同一份击杀载荷与结算载荷,
    /// 只是"本地怪被打死"换成了"按怪物表顺序每隔一个间隔就报一只",所以 .NET 控制台探针不经 Unity 就能在真服上验证联网战斗。
    /// <para>
    /// 流程:启动 <see cref="OnlineDungeonCombatRunner.RunAsync"/>;按本地序号 0 到 N-1 依次 <see cref="OnlineDungeonCombatRunner.PostKill"/>(第 0 只立刻排,
    /// 之后每只与前一只隔 <see cref="OnlineFightOptions.PaceMs"/>);每次排之前看一眼状态:跑器已收场就收场、清图通知已到就不再排(没怪可杀了)、
    /// 出现第一次击杀被拒就停(缺省,<see cref="OnlineFightOptions.ContinueOnRefuse"/> 为真才继续);最后一只排完后等清图通知(<see cref="OnlineFightOptions.ClearWait"/>),
    /// 清图通知到了由跑器自己发结算命令,驱动再等两段结算通知收齐(<see cref="OnlineFightOptions.SettleWait"/>);任何一段等待超时就收场并让跑器停下。
    /// </para>
    /// <para>
    /// 心跳:战斗期间由跑器自己回(类 1、包号 0、8 个 0 字节,见 <see cref="OnlineDungeonCombatRunner"/>),驱动不另造。
    /// 时间:驱动自己的节奏与两段等待只用注入的时钟(<see cref="IFightClock"/>)与取消令牌,没有写死墙钟的延时;跑器自己的轮询窗口与上限仍是真时间。
    /// 空地图(怪数 0):没有击杀可排,结算闸门仍只认服务端的清图通知(状态机不因为没有怪就放行),所以直接等清图通知,等不来就是 <see cref="OnlineFightOutcome.NoClearNotice"/>。
    /// 一个驱动只能跑一次;它启动的跑器在收场时一定已被让停(取消),之后 <see cref="OnlineDungeonCombatRunner.PostKill"/> 不再收。
    /// 从不因为连接、收发或取消而抛异常(见 <see cref="OnlineFightOutcome"/>);日志回调自己抛的异常被吞掉。
    /// </para>
    /// </summary>
    public sealed class OnlineFightDriver
    {
        /// <summary>让跑器停下之后,最多等它多久返回(取驱动的时钟);防止一次卡住的收发把调用方挂死。</summary>
        public static readonly TimeSpan FinishWait = TimeSpan.FromSeconds(5);

        private readonly OnlineDungeonCombatRunner _runner;
        private readonly StartMapNotice _map;
        private readonly int _paceMs;
        private readonly bool _continueOnRefuse;
        private readonly TimeSpan _clearWait;
        private readonly TimeSpan _settleWait;
        private readonly TimeSpan _poll;
        private readonly IFightClock _clock;
        private int _started;

        /// <summary>
        /// 创建驱动。<paramref name="runner"/> 必须是还没跑过的跑器(它的状态机还不存在),<paramref name="map"/> 是进副本拿到的起始地图(<see cref="OnlineDungeonResult.StartMap"/>)。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="runner"/> 或 <paramref name="map"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException">间隔为负,或某个等待时长不大于 0、超过 int.MaxValue 毫秒。</exception>
        public OnlineFightDriver(OnlineDungeonCombatRunner runner, StartMapNotice map, OnlineFightOptions options = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _map = map ?? throw new ArgumentNullException(nameof(map));
            OnlineFightOptions o = options ?? new OnlineFightOptions();
            if (o.PaceMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "PaceMs 不能为负");
            }
            CheckWindow(o.ClearWait, "ClearWait");
            CheckWindow(o.SettleWait, "SettleWait");
            CheckWindow(o.PollInterval, "PollInterval");
            _paceMs = o.PaceMs;
            _continueOnRefuse = o.ContinueOnRefuse;
            _clearWait = o.ClearWait;
            _settleWait = o.SettleWait;
            _poll = o.PollInterval;
            _clock = o.Clock ?? SystemFightClock.Instance;
        }

        /// <summary>
        /// 跑完整条链并返回汇总。每个阶段与结局按 <see cref="OnlineFightLog.Prefix"/> 开头的行交给 <paramref name="log"/>(不含换行;null 表示不打),
        /// 跑器自己的 <see cref="OnlineCombatLog.Prefix"/> 行也走同一个回调。
        /// </summary>
        /// <param name="log">日志回调(可能在跑器的循环线程与调用线程上被调用);null 不打。</param>
        /// <param name="cancellationToken">取消令牌:触发后立即停(驱动的等待与跑器都被让停),返回 <see cref="OnlineFightOutcome.Cancelled"/>,不抛。</param>
        /// <exception cref="InvalidOperationException">驱动已经跑过一次,或者给的跑器已经跑过(有了状态机或已收场)。</exception>
        public async Task<OnlineFightSummary> RunAsync(Action<string> log = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("一个战斗驱动只能跑一次");
            }
            if (_runner.Machine != null || _runner.Ended)
            {
                throw new InvalidOperationException("战斗跑器已经跑过,驱动要用一个还没跑过的跑器");
            }

            var state = new RunState { Started = _clock.Now };
            Task<OnlineDungeonCombatResult> runTask = null;
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Say(log, OnlineFightLog.Begin(_map.Monsters.Count, _paceMs, _clearWait, _settleWait, _continueOnRefuse));
                try
                {
                    runTask = _runner.RunAsync(_map, log, linked.Token);
                    if (_runner.Machine == null)
                    {
                        throw new InvalidOperationException("战斗状态机没有建起来");
                    }
                    await DriveAsync(runTask, state, log, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    state.Cancelled = true;
                }
                catch (Exception e)
                {
                    state.FaultType = e.GetBaseException().GetType().Name;
                }

                // 收尾:让跑器停下(它从不抛,取消只当安静结束),再读它的最终计数。
                linked.Cancel();
                OnlineDungeonCombatResult result = await FinishRunnerAsync(runTask, state).ConfigureAwait(false);
                OnlineFightSummary summary = Summarize(state, result);
                Say(log, OnlineFightLog.End(summary));
                return summary;
            }
        }

        // 三段:排击杀、等清图、等结算。只在这里"定结局"的那几种(被拒即停、两段超时)写进 state.Verdict;
        // 跑器自己收场、结算收齐、被取消都不在这里定,留给 Summarize 按最终状态判。
        private async Task DriveAsync(Task<OnlineDungeonCombatResult> runTask, RunState state, Action<string> log, CancellationToken token)
        {
            int total = _map.Monsters.Count;

            Say(log, OnlineFightLog.Phase("kills", 0));
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                if (runTask.IsCompleted)
                {
                    return;
                }
                OnlineDungeonCombat machine = _runner.Machine;
                if (machine.Phase >= OnlineCombatPhase.Cleared)
                {
                    break;                                  // 服务端已经说清图了:没有怪可杀,不再排
                }
                if (NoteRefusal(machine, state, log) && !_continueOnRefuse)
                {
                    state.Verdict = OnlineFightOutcome.RefusedStop;
                    return;
                }

                if (!_runner.PostKill(i))
                {
                    return;                                 // 跑器已收场:结局按它的结局认
                }
                state.Posted++;
                state.PostTimes.Add(_clock.Now - state.Started);

                if (i + 1 < total && _paceMs > 0)
                {
                    if (!await WaitAsync(runTask, TimeSpan.FromMilliseconds(_paceMs), token).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }

            Say(log, OnlineFightLog.Phase("clearWait", state.Posted));
            TimeSpan clearDeadline = _clock.Now + _clearWait;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (runTask.IsCompleted)
                {
                    return;
                }
                OnlineDungeonCombat machine = _runner.Machine;
                if (machine.Phase >= OnlineCombatPhase.Cleared)
                {
                    break;
                }
                if (NoteRefusal(machine, state, log) && !_continueOnRefuse)
                {
                    state.Verdict = OnlineFightOutcome.RefusedStop;
                    return;
                }
                TimeSpan left = clearDeadline - _clock.Now;
                if (left <= TimeSpan.Zero)
                {
                    state.Verdict = OnlineFightOutcome.NoClearNotice;
                    return;
                }
                if (!await WaitAsync(runTask, Shorter(_poll, left), token).ConfigureAwait(false))
                {
                    return;
                }
            }

            // 清图通知到了:结算命令由跑器自己发(闸门在状态机里),这里只等两段结算通知收齐。
            Say(log, OnlineFightLog.Phase("settleWait", state.Posted));
            TimeSpan settleDeadline = _clock.Now + _settleWait;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (runTask.IsCompleted)
                {
                    return;
                }
                if (_runner.Machine.Phase == OnlineCombatPhase.Settled)
                {
                    return;
                }
                TimeSpan left = settleDeadline - _clock.Now;
                if (left <= TimeSpan.Zero)
                {
                    state.Verdict = OnlineFightOutcome.SettleIncomplete;
                    return;
                }
                if (!await WaitAsync(runTask, Shorter(_poll, left), token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        // 看一眼有没有被拒:有就(第一次)记下是哪一只并打一行;返回"到此刻为止有过被拒"。
        private bool NoteRefusal(OnlineDungeonCombat machine, RunState state, Action<string> log)
        {
            if (machine.Refused <= 0)
            {
                return false;
            }
            if (state.FirstRefusedLocal < 0)
            {
                state.FirstRefusedLocal = FirstRefusedOf(machine);
                Say(log, OnlineFightLog.Refused(state.FirstRefusedLocal, !_continueOnRefuse));
            }
            return true;
        }

        // 等 delay(取驱动的时钟);跑器先收场就立刻返回 false;取消令牌触发抛 OperationCanceledException。
        // 每次等待自带一个小令牌,等完就取消它,免得假时钟里或真定时器里留下没人等的延时。
        private async Task<bool> WaitAsync(Task runTask, TimeSpan delay, CancellationToken token)
        {
            using (CancellationTokenSource step = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task delayTask = _clock.DelayAsync(delay, step.Token);
                Task winner = await Task.WhenAny(runTask, delayTask).ConfigureAwait(false);
                step.Cancel();
                if (winner == runTask)
                {
                    return false;
                }
                await delayTask.ConfigureAwait(false);
                return true;
            }
        }

        // 跑器已被让停:等它返回(最多 FinishWait),读它的结果;它没在宽限内返回就当没有结果,按活的状态机读数。
        private async Task<OnlineDungeonCombatResult> FinishRunnerAsync(Task<OnlineDungeonCombatResult> runTask, RunState state)
        {
            if (runTask == null)
            {
                return null;
            }
            if (!runTask.IsCompleted)
            {
                using (var grace = new CancellationTokenSource())
                {
                    Task graceTask = _clock.DelayAsync(FinishWait, grace.Token);
                    await Task.WhenAny(runTask, graceTask).ConfigureAwait(false);
                    grace.Cancel();
                }
            }
            if (!runTask.IsCompleted)
            {
                return null;
            }
            try
            {
                return await runTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (state.FaultType == null)
                {
                    state.FaultType = e.GetBaseException().GetType().Name;
                }
                return null;
            }
        }

        private OnlineFightSummary Summarize(RunState state, OnlineDungeonCombatResult result)
        {
            OnlineDungeonCombat machine = _runner.Machine;
            int sent = 0;
            int confirmed = 0;
            int refused = 0;
            int blocked = 0;
            OnlineCombatPhase phase = OnlineCombatPhase.Fighting;
            OnlineFightSettlement settlement = OnlineFightSettlement.None;
            int firstRefused = state.FirstRefusedLocal;
            if (machine != null)
            {
                sent = machine.Sent;
                confirmed = machine.Confirmed;
                refused = machine.Refused;
                blocked = machine.Blocked;
                phase = machine.Phase;
                settlement = SettlementOf(machine);
                if (firstRefused < 0 && refused > 0)
                {
                    firstRefused = FirstRefusedOf(machine);
                }
            }

            OnlineDungeonCombatOutcome? runnerOutcome = null;
            string fault = state.FaultType;
            int heartbeats = 0;
            if (result != null)
            {
                runnerOutcome = result.Outcome;
                heartbeats = result.HeartbeatsAnswered;
                if (fault == null)
                {
                    fault = result.FaultType;
                }
            }

            OnlineFightOutcome outcome = Decide(state, phase, refused, runnerOutcome, fault != null);
            return new OnlineFightSummary(outcome, _map.Monsters.Count, state.Posted, sent, confirmed, refused, blocked,
                phase >= OnlineCombatPhase.Cleared, settlement, phase == OnlineCombatPhase.Settled, phase,
                runnerOutcome, fault, firstRefused, heartbeats, _clock.Now - state.Started, state.PostTimes.ToArray());
        }

        // 结局的判法,先后即优先级:
        //  1 状态机走到了终局(两段结算收齐):AllDone,不管之后发生了什么;
        //  2 抛了异常:Faulted;  3 调用方取消:Cancelled;
        //  4 缺省(首次被拒即停)下只要有过被拒:RefusedStop(原因先于后果:被拒之后连接被掐也记被拒);
        //  5 驱动自己定的结局(等清图超时、等结算超时);  6 跑器自己收场的结局按它的原因映射。
        private OnlineFightOutcome Decide(RunState state, OnlineCombatPhase phase, int refused, OnlineDungeonCombatOutcome? runner, bool faulted)
        {
            if (phase == OnlineCombatPhase.Settled)
            {
                return OnlineFightOutcome.AllDone;
            }
            if (faulted)
            {
                return OnlineFightOutcome.Faulted;
            }
            if (state.Cancelled)
            {
                return OnlineFightOutcome.Cancelled;
            }
            if (!_continueOnRefuse && refused > 0)
            {
                return OnlineFightOutcome.RefusedStop;
            }
            if (state.Verdict.HasValue)
            {
                return state.Verdict.Value;
            }
            if (runner.HasValue)
            {
                switch (runner.Value)
                {
                    case OnlineDungeonCombatOutcome.ReturnReady:
                        return OnlineFightOutcome.AllDone;
                    case OnlineDungeonCombatOutcome.ConnectionClosed:
                    case OnlineDungeonCombatOutcome.FramingFault:
                        return OnlineFightOutcome.SessionClosed;
                    case OnlineDungeonCombatOutcome.Timeout:
                        return phase == OnlineCombatPhase.Fighting ? OnlineFightOutcome.NoClearNotice : OnlineFightOutcome.SettleIncomplete;
                    case OnlineDungeonCombatOutcome.Cancelled:
                        return OnlineFightOutcome.Cancelled;
                }
            }
            return OnlineFightOutcome.Faulted;
        }

        // 两段结算通知收到了几段:数事件表里的 SettlementArrived。跑器收场之后事件表不再被写;没收场时也只按下标读已有的几条,不遍历。
        private static OnlineFightSettlement SettlementOf(OnlineDungeonCombat machine)
        {
            IReadOnlyList<OnlineCombatEvent> events = machine.Events;
            bool result = false;
            bool reward = false;
            int count = events.Count;
            for (int i = 0; i < count; i++)
            {
                OnlineCombatEvent e = events[i];
                if (e == null || e.Kind != OnlineCombatEventKind.SettlementArrived)
                {
                    continue;
                }
                if (e.Part == OnlineCombatSettlementPart.Result)
                {
                    result = true;
                }
                else if (e.Part == OnlineCombatSettlementPart.Reward)
                {
                    reward = true;
                }
            }
            if (result && reward)
            {
                return OnlineFightSettlement.Complete;
            }
            return result || reward ? OnlineFightSettlement.Partial : OnlineFightSettlement.None;
        }

        // 第一只被拒的怪的本地序号(按序号升序找第一只状态是被拒的);没有返回 -1。
        private static int FirstRefusedOf(OnlineDungeonCombat machine)
        {
            IReadOnlyList<OnlineCombatMonster> monsters = machine.Monsters;
            for (int i = 0; i < monsters.Count; i++)
            {
                if (monsters[i].State == OnlineCombatMonsterState.Refused)
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

        private static void Say(Action<string> log, string line)
        {
            if (log == null)
            {
                return;
            }
            try
            {
                log(line);
            }
            catch (Exception)
            {
                // 日志回调自己出错不能影响驱动
            }
        }

        /// <summary>一次运行的可变状态(每次调用一个,驱动对象本身只存入参)。</summary>
        private sealed class RunState
        {
            public TimeSpan Started;
            public int Posted;
            public readonly List<TimeSpan> PostTimes = new List<TimeSpan>();
            public int FirstRefusedLocal = -1;
            public OnlineFightOutcome? Verdict;
            public bool Cancelled;
            public string FaultType;
        }
    }
}
