using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 宿主进副本的薄封装(Unity 宿主 <c>-dnfnetdungeon</c> 的会话层一侧):在已进城的会话上走完
    /// <see cref="OnlineDungeonEntry"/> 的五步,把阶段变化与结局按固定的行格式交给调用方的日志回调,进副本之后接上既有的心跳保活。
    /// 协议逻辑一点都不在这里(全在 <see cref="OnlineDungeonEntry"/> 里),这里只做四件事:
    /// <list type="number">
    /// <item>入参检查:副本号 0 到 65535、模式 0 到 4,不合规返回 <see cref="OnlineDungeonFailure.BadRequest"/>,什么都不发(不回绕成别的数);</item>
    /// <item>把 <see cref="AntibotEnvelope.TryUnwrap(byte[], out byte[])"/> 作为 561 信封的解包委托接给 <see cref="OnlineDungeonEntry"/>(<see cref="UnwrapEnvelope"/>);</item>
    /// <item>日志:每一行都以 <c>DNF_NET stage=dungeon</c> 开头,只有阶段名、失败原因与计数,没有任何载荷内容(<see cref="OnlineNetLog"/>);</item>
    /// <item>保活的先后次序:进副本期间会话归进副本编排独占(它不回心跳,不能与保活泵同时收同一个会话),
    /// 所以保活泵只在进副本这一步结束之后才起;连接已断或切帧出错的会话不起泵(泵遇到切帧错误会空转)。</item>
    /// </list>
    /// 不做战斗、不处理怪物、不发"装载完成"命令(那是后续任务)。
    /// </summary>
    public sealed class OnlineDungeonRunner
    {
        /// <summary>副本号的取值上限(线上是 u16)。</summary>
        public const int MaxDungeonId = ushort.MaxValue;

        /// <summary>一次最多把多少个收帧记录打成日志行(再多的只记一行"还有 N 条")。</summary>
        public const int MaxFrameLines = 64;

        private readonly OnlineDungeonEntry _entry;
        private readonly Func<string> _unusableReason;
        private readonly Func<CancellationToken, Task<int>> _keepAlive;
        private readonly Func<IReadOnlyList<SessionEvent>> _events;     // 真会话才有事件表;接缝替身没有,不打汇总行

        /// <summary>在已进城的真会话上创建;进副本之后用 <see cref="OnlineHeartbeat"/> 保活。</summary>
        public OnlineDungeonRunner(GameClientSession session)
            : this(session, token => OnlineHeartbeat.RunAsync(session, token))
        {
        }

        /// <summary>真会话加自定的保活(测试用:换掉真的心跳泵,看进副本之后保活怎么被调用);<paramref name="keepAlive"/> 为 null 表示不保活。</summary>
        internal OnlineDungeonRunner(GameClientSession session, Func<CancellationToken, Task<int>> keepAlive)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            _entry = new OnlineDungeonEntry(session, UnwrapEnvelope);
            _unusableReason = () => session.PeerClosed ? "ConnectionClosed" : (session.LastFrameError != FrameError.None ? "FramingFault" : null);
            _keepAlive = keepAlive;
            _events = () => session.Events;
        }

        /// <summary>
        /// 在会话接缝的任意实现上创建(合成测试用)。<paramref name="keepAlive"/> 是进副本之后要跑的保活(返回回了几次心跳);
        /// 为 null 表示不保活。
        /// </summary>
        public OnlineDungeonRunner(IOnlineDungeonSession session, Func<CancellationToken, Task<int>> keepAlive = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            _entry = new OnlineDungeonEntry(session, UnwrapEnvelope);
            _unusableReason = () => session.PeerClosed ? "ConnectionClosed" : (session.LastFrameError != FrameError.None ? "FramingFault" : null);
            _keepAlive = keepAlive;
        }

        /// <summary>
        /// 561 信封的解包委托:入参是信封帧的载荷,返回完整的内层帧(15 字节头加载荷),解不开返回 null。
        /// 就是 <see cref="AntibotEnvelope.TryUnwrap(byte[], out byte[])"/> 套成 <see cref="OnlineDungeonEntry"/> 要的委托形状,不加别的行为。
        /// </summary>
        public static byte[] UnwrapEnvelope(byte[] envelopePayload)
        {
            return AntibotEnvelope.TryUnwrap(envelopePayload, out byte[] inner) ? inner : null;
        }

        /// <summary>副本号与模式是否合规(副本号 0 到 65535,模式 0 到 <see cref="OnlineDungeonEntry.MaxMode"/>)。</summary>
        public static bool IsValidRequest(int dungeonId, int mode)
        {
            return dungeonId >= 0 && dungeonId <= MaxDungeonId && mode >= 0 && mode <= OnlineDungeonEntry.MaxMode;
        }

        /// <summary>
        /// 宿主命令行 <c>-dnfnetdungeon &lt;副本编号&gt; &lt;模式&gt;</c> 的两个参数:只收纯十进制数字(不收空白、正负号、小数点、全角数字,不受当前文化影响),
        /// 且合乎 <see cref="IsValidRequest"/> 才算成功;缺参数(null)、不是整数、越界都返回 false,<paramref name="dungeonId"/> 与 <paramref name="mode"/> 此时为 0。
        /// </summary>
        public static bool TryParseArguments(string dungeonText, string modeText, out int dungeonId, out int mode)
        {
            dungeonId = 0;
            mode = 0;
            if (dungeonText == null || modeText == null)
            {
                return false;
            }
            if (!int.TryParse(dungeonText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedDungeon)
                || !int.TryParse(modeText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedMode)
                || !IsValidRequest(parsedDungeon, parsedMode))
            {
                return false;
            }
            dungeonId = parsedDungeon;
            mode = parsedMode;
            return true;
        }

        /// <summary>
        /// 进副本的结局之后还该不该接保活:连接已断、切帧出错的不接;其余结局(含被拒、超时、入参不合规)连接都还活着,照接,
        /// 与"没带副本参数时进城后就保活"的既有行为一致。
        /// </summary>
        public static bool ShouldKeepAlive(OnlineDungeonResult result)
        {
            return result != null
                && result.Failure != OnlineDungeonFailure.ConnectionClosed
                && result.Failure != OnlineDungeonFailure.FramingFault;
        }

        /// <summary>
        /// 只走进副本这一步(不保活):检查入参 → 走 <see cref="OnlineDungeonEntry.EnterAsync"/> → 把阶段与结局打成日志行。
        /// 失败返回带原因的结果而不抛;取消、会话没就绪等 <see cref="OnlineDungeonEntry.EnterAsync"/> 本来会抛的异常原样抛出。
        /// </summary>
        /// <param name="dungeonId">副本号(0 到 65535)。</param>
        /// <param name="mode">模式(0 到 4)。</param>
        /// <param name="log">每一行日志交给它(不含换行);null 表示不打。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <param name="loadingNoticeTimeout">等 27 的窗口;null 用 <see cref="OnlineDungeonEntry.DefaultLoadingNoticeTimeout"/>。</param>
        /// <param name="dungeonInfoTimeout">等 28 的窗口;null 用 <see cref="OnlineDungeonEntry.DefaultDungeonInfoTimeout"/>。</param>
        /// <param name="startMapTimeout">等 29 的窗口;null 用 <see cref="OnlineDungeonEntry.DefaultStartMapTimeout"/>。</param>
        public async Task<OnlineDungeonResult> EnterAsync(
            int dungeonId,
            int mode,
            Action<string> log,
            CancellationToken cancellationToken = default,
            TimeSpan? loadingNoticeTimeout = null,
            TimeSpan? dungeonInfoTimeout = null,
            TimeSpan? startMapTimeout = null)
        {
            log?.Invoke(OnlineNetLog.DungeonBegin(dungeonId, mode));

            OnlineDungeonResult result;
            bool ran = false;
            if (!IsValidRequest(dungeonId, mode))
            {
                result = new OnlineDungeonResult(OnlineDungeonStage.SendStartGame, OnlineDungeonFailure.BadRequest, null, null, null,
                    null, null, null, new OnlineDungeonFrame[0]);
            }
            else
            {
                ran = true;
                result = await _entry.EnterAsync(
                    (ushort)dungeonId,
                    (byte)mode,
                    OnlineDungeonEntry.DefaultThirdByte,
                    OnlineDungeonEntry.DefaultSafeByte,
                    loadingNoticeTimeout,
                    dungeonInfoTimeout,
                    startMapTimeout,
                    stage => log?.Invoke(OnlineNetLog.DungeonStep(stage)),
                    cancellationToken).ConfigureAwait(false);
            }

            if (log != null)
            {
                int shown = Math.Min(result.Frames.Count, MaxFrameLines);
                for (int i = 0; i < shown; i++)
                {
                    log(OnlineNetLog.DungeonFrame(i, result.Frames[i]));
                }
                if (result.Frames.Count > shown)
                {
                    log(OnlineNetLog.DungeonFramesOmitted(result.Frames.Count - shown));
                }
                if (ran && _events != null)
                {
                    // 此刻进副本刚结束、保活还没起,会话没有别的线程在写事件表,可以直接读;放在完成/失败行之前,让那一行仍是进副本的最后一行
                    log(OnlineNetLog.DungeonSessionDigest(_events()));
                }
                log(result.Entered ? OnlineNetLog.DungeonDone(result) : OnlineNetLog.DungeonFailed(result));
            }
            return result;
        }

        /// <summary>
        /// 进副本、再接保活:先 <see cref="EnterAsync"/>,结局允许时(<see cref="ShouldKeepAlive"/> 且会话还可用)起保活泵并一直跑到连接断开或取消。
        /// 返回进副本的结局;整个调用在保活结束时才返回,宿主一般把它丢到后台线程不等它。
        /// 从不抛异常:取消时安静地返回 null,别的意外异常打一行 <c>DNF_NET stage=dungeon failed exception=类型名</c>(不带异常消息)后返回 null。
        /// 保活结束的日志行与宿主原来的两行同文:<c>DNF_NET heartbeat answered=N</c>、<c>DNF_NET heartbeat ended=异常类型名</c>(取消不打)。
        /// </summary>
        /// <param name="dungeonId">副本号(0 到 65535)。</param>
        /// <param name="mode">模式(0 到 4)。</param>
        /// <param name="log">每一行日志交给它(不含换行);null 表示不打;它自己抛的异常被吞掉。</param>
        /// <param name="cancellationToken">取消令牌(进副本与保活共用)。</param>
        /// <param name="loadingNoticeTimeout">等 27 的窗口;null 用 <see cref="OnlineDungeonEntry.DefaultLoadingNoticeTimeout"/>。</param>
        /// <param name="dungeonInfoTimeout">等 28 的窗口;null 用 <see cref="OnlineDungeonEntry.DefaultDungeonInfoTimeout"/>。</param>
        /// <param name="startMapTimeout">等 29 的窗口;null 用 <see cref="OnlineDungeonEntry.DefaultStartMapTimeout"/>。</param>
        /// <param name="entered">
        /// 进副本这一步有了结局之后、保活开始之前,恰好调用一次:成功或失败(被拒、超时、入参不合规……)传结果,
        /// 取消或意外异常传 null。宿主靠它知道"可以往下走了",保活仍在后台接着跑;它自己抛的异常被吞掉。
        /// </param>
        public async Task<OnlineDungeonResult> RunAsync(
            int dungeonId,
            int mode,
            Action<string> log,
            CancellationToken cancellationToken = default,
            TimeSpan? loadingNoticeTimeout = null,
            TimeSpan? dungeonInfoTimeout = null,
            TimeSpan? startMapTimeout = null,
            Action<OnlineDungeonResult> entered = null)
        {
            OnlineDungeonResult result;
            try
            {
                result = await EnterAsync(dungeonId, mode, log, cancellationToken, loadingNoticeTimeout, dungeonInfoTimeout, startMapTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SafeEntered(entered, null);
                return null;
            }
            catch (Exception e)
            {
                SafeLog(log, OnlineNetLog.DungeonException(e));
                SafeEntered(entered, null);
                return null;
            }
            SafeEntered(entered, result);

            try
            {
                string unusable = _unusableReason();
                if (_keepAlive == null)
                {
                    log?.Invoke(OnlineNetLog.DungeonKeepAlive("skip", "NoKeepAlive"));
                }
                else if (!ShouldKeepAlive(result))
                {
                    log?.Invoke(OnlineNetLog.DungeonKeepAlive("skip", result.Failure.ToString()));
                }
                else if (unusable != null)
                {
                    log?.Invoke(OnlineNetLog.DungeonKeepAlive("skip", unusable));
                }
                else
                {
                    log?.Invoke(OnlineNetLog.DungeonKeepAlive("start", null));
                    int answered = await _keepAlive(cancellationToken).ConfigureAwait(false);
                    log?.Invoke(OnlineNetLog.HeartbeatAnswered(answered));
                }
            }
            catch (OperationCanceledException)
            {
                // 取消:与宿主原来的保活一样,什么都不打
            }
            catch (Exception e)
            {
                SafeLog(log, OnlineNetLog.HeartbeatEnded(e));
            }
            return result;
        }

        private static void SafeLog(Action<string> log, string line)
        {
            try
            {
                log?.Invoke(line);
            }
            catch (Exception)
            {
                // 日志回调自己出错不能再往外抛(调用方是后台线程里的"发后不管")
            }
        }

        private static void SafeEntered(Action<OnlineDungeonResult> entered, OnlineDungeonResult result)
        {
            try
            {
                entered?.Invoke(result);
            }
            catch (Exception)
            {
                // 宿主的"进副本有结局了"回调出错,不能影响后面的保活
            }
        }
    }

    /// <summary>
    /// 联网宿主日志行的拼法(纯函数,调用方只管打)。所有进副本的行都以 <see cref="DungeonPrefix"/> 开头,
    /// 只含阶段名、失败原因(枚举名)、包类与包号、载荷长度和各种计数,<b>不含任何载荷内容</b>,也不含凭据与异常消息。
    /// 数字一律按固定文化输出;没有值的字段写 <c>-</c>。
    /// </summary>
    public static class OnlineNetLog
    {
        /// <summary>进副本各行的统一前缀。</summary>
        public const string DungeonPrefix = "DNF_NET stage=dungeon";

        /// <summary>
        /// 进城快照的一行:选角槽号、等级、职业、成长类型(取不到写 <c>-</c>)、是否见到进城完成通知,以及解析器的三个诊断计数。
        /// 看这一行就知道角色回填有没有生效:<c>level=-</c> 且 <c>seen</c> 很大,再看 <c>malformed</c>:
        /// 为 0 说明没见到用户信息通知(列表应答没到或信封没拆开),大于 0 说明见到了但读不通。
        /// </summary>
        public static string Snapshot(TownEntrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "DNF_NET snapshot none";
            }
            return "DNF_NET snapshot slot=" + Number(snapshot.Slot)
                + " level=" + Number(snapshot.Level)
                + " job=" + Number(snapshot.Job)
                + " growType=" + Number(snapshot.GrowType)
                + " enterComplete=" + (snapshot.EnterComplete ? "1" : "0")
                + " seen=" + Number(snapshot.FramesSeen)
                + " parsed=" + Number(snapshot.FramesParsed)
                + " malformed=" + Number(snapshot.FramesMalformed);
        }

        /// <summary>汇总行里最多列出多少个不同的内层包号(再多的只记一个"+N")。</summary>
        public const int MaxDigestIds = 32;

        // 与 GameClientSession 写进事件备注的字样一致(见它的 AppendAntibotInner);测试用真会话核对,改一边忘了另一边会被抓到
        private const string InnerOpenedNote = "信封内层";
        private const string UnwrapFailedNote = "信封拆包失败";

        /// <summary>
        /// 会话事件的一行汇总(进城阶段结束时打一次):收了多少帧、其中 561 信封几个、拆开了几个(内层带了哪些包号)、没拆开几个(按失败原因分),
        /// 以及角色列表应答(类 0、包号 2)是直接来的还是封在信封里来的。快照行里等级、职业、成长类型取不到时看这一行分辨原因:
        /// 应答根本没来(<c>id2Direct</c> 与 <c>id2Inner</c> 都是 0)、信封没拆开(<c>unwrapFailed</c> 大于 0,原因在 <c>unwrapReasons</c>)、
        /// 还是来了但读不通(看快照行的 <c>malformed</c>)。只有计数、包号与失败原因的枚举名,没有载荷内容。
        /// </summary>
        public static string SessionDigest(IReadOnlyList<SessionEvent> events)
        {
            return Digest("DNF_NET", events);
        }

        /// <summary>
        /// 同一份汇总,打在进副本这一步结束时(行首带 <see cref="DungeonPrefix"/>):数的是整个会话到此刻的累计,
        /// 与进城那一行相减就是进副本这几步新增的信封(拆开几个、没拆开的原因)。
        /// </summary>
        internal static string DungeonSessionDigest(IReadOnlyList<SessionEvent> events)
        {
            return Digest(DungeonPrefix, events);
        }

        private static string Digest(string prefix, IReadOnlyList<SessionEvent> events)
        {
            if (events == null)
            {
                return prefix + " session none";
            }
            int received = 0;
            int opened = 0;
            int unwrapFailed = 0;
            int innerShort = 0;
            int id2Direct = 0;
            int id2Inner = 0;
            var innerIds = new List<int>();
            var reasons = new Dictionary<string, int>();
            for (int i = 0; i < events.Count; i++)
            {
                SessionEvent e = events[i];
                if (e == null || e.Outbound)
                {
                    continue;
                }
                received++;
                string note = e.Note ?? string.Empty;
                bool listNotice = e.PacketClass == PacketClass.Noti && e.PacketId == 2;
                if (note == InnerOpenedNote)
                {
                    opened++;
                    if (!innerIds.Contains(e.PacketId))
                    {
                        innerIds.Add(e.PacketId);
                    }
                    if (listNotice)
                    {
                        id2Inner++;
                    }
                }
                else if (note.StartsWith(UnwrapFailedNote, StringComparison.Ordinal))
                {
                    unwrapFailed++;
                    string reason = UnwrapReason(note);
                    reasons.TryGetValue(reason, out int seen);
                    reasons[reason] = seen + 1;
                }
                else if (note.StartsWith(InnerOpenedNote, StringComparison.Ordinal))
                {
                    innerShort++;           // "信封内层(帧头不足,未展开)":拆开了,但内层不成一个完整的帧
                }
                else if (listNotice)
                {
                    id2Direct++;
                }
            }
            innerIds.Sort();

            var ids = new StringBuilder();
            int listed = Math.Min(innerIds.Count, MaxDigestIds);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0)
                {
                    ids.Append(',');
                }
                ids.Append(Number(innerIds[i]));
            }
            if (innerIds.Count > listed)
            {
                ids.Append(",+").Append(Number(innerIds.Count - listed));
            }

            var reasonNames = new List<string>(reasons.Keys);
            reasonNames.Sort(StringComparer.Ordinal);
            var reasonText = new StringBuilder();
            foreach (string name in reasonNames)
            {
                if (reasonText.Length > 0)
                {
                    reasonText.Append(',');
                }
                reasonText.Append(name).Append(':').Append(Number(reasons[name]));
            }

            return prefix + " session events=" + Number(events.Count)
                + " received=" + Number(received)
                + " envelopes=" + Number(opened + unwrapFailed + innerShort)
                + " opened=" + Number(opened)
                + " unwrapFailed=" + Number(unwrapFailed)
                + " unwrapReasons=" + (reasonText.Length == 0 ? "-" : reasonText.ToString())
                + " innerShort=" + Number(innerShort)
                + " innerIds=" + (ids.Length == 0 ? "-" : ids.ToString())
                + " id2Direct=" + Number(id2Direct)
                + " id2Inner=" + Number(id2Inner);
        }

        // 拆包失败事件的备注是 "信封拆包失败:" 加 AntibotEnvelopeStatus 的枚举名;只认枚举名(其余一律记 Unknown),不把备注里的任何别的文字写进日志
        private static string UnwrapReason(string note)
        {
            int colon = note.IndexOf(':');
            string text = colon >= 0 ? note.Substring(colon + 1) : string.Empty;
            bool known = Enum.TryParse(text, false, out AntibotEnvelopeStatus status)
                && Enum.IsDefined(typeof(AntibotEnvelopeStatus), status)
                && status.ToString() == text;           // 挡掉 "5"、"99" 这类数字写法(未定义的数值 ToString 也是数字本身)
            return known ? text : "Unknown";
        }

        internal static string DungeonBegin(int dungeonId, int mode)
        {
            return DungeonPrefix + " begin dungeon=" + Number(dungeonId) + " mode=" + Number(mode);
        }

        internal static string DungeonStep(OnlineDungeonStage stage)
        {
            return DungeonPrefix + " step=" + stage;
        }

        internal static string DungeonFrame(int index, OnlineDungeonFrame frame)
        {
            return DungeonPrefix + " frame n=" + Number(index) + " step=" + frame.Stage + " " + frame;
        }

        internal static string DungeonFramesOmitted(int count)
        {
            return DungeonPrefix + " frames omitted=" + Number(count);
        }

        internal static string DungeonDone(OnlineDungeonResult result)
        {
            var sb = new StringBuilder(DungeonPrefix);
            sb.Append(" done step=").Append(result.Stage);
            AppendCounts(sb, result);
            return sb.ToString();
        }

        internal static string DungeonFailed(OnlineDungeonResult result)
        {
            var sb = new StringBuilder(DungeonPrefix);
            sb.Append(" failed step=").Append(result.Stage);
            sb.Append(" failure=").Append(result.Failure);
            sb.Append(" send=").Append(result.SendStatus.HasValue ? result.SendStatus.Value.ToString() : "-");
            sb.Append(" rejected=").Append(Number(result.RejectedPacketId));
            sb.Append(" code=").Append(Number(result.RejectionCode));
            AppendCounts(sb, result);
            return sb.ToString();
        }

        internal static string DungeonException(Exception e)
        {
            return DungeonPrefix + " failed exception=" + e.GetBaseException().GetType().Name;
        }

        internal static string DungeonKeepAlive(string state, string reason)
        {
            return DungeonPrefix + " keepalive=" + state + (reason == null ? string.Empty : " reason=" + reason);
        }

        /// <summary>保活泵正常结束(对端关闭连接):与 Unity 宿主原来的那一行同文。</summary>
        internal static string HeartbeatAnswered(int answered)
        {
            return "DNF_NET heartbeat answered=" + Number(answered);
        }

        /// <summary>保活泵因异常结束:与 Unity 宿主原来的那一行同文(只有异常类型名)。</summary>
        internal static string HeartbeatEnded(Exception e)
        {
            return "DNF_NET heartbeat ended=" + e.GetBaseException().GetType().Name;
        }

        // 收帧记录的汇总计数:帧数、没解开的信封、乱序被忽略的、重复被忽略的;以及三个通知是否收到(27 记载荷长度,28、29 记 0 或 1)
        private static void AppendCounts(StringBuilder sb, OnlineDungeonResult result)
        {
            sb.Append(" frames=").Append(Number(result.Frames.Count));
            sb.Append(" unopened=").Append(Number(result.UnopenedEnvelopeCount));
            sb.Append(" outOfOrder=").Append(Number(result.OutOfOrderCount));
            sb.Append(" duplicate=").Append(Number(result.DuplicateCount));
            sb.Append(" loading27=").Append(result.LoadingNoticePayload == null ? "-" : Number(result.LoadingNoticePayload.Length));
            sb.Append(" info28=").Append(result.DungeonInfo != null ? "1" : "0");
            sb.Append(" map29=").Append(result.StartMap != null ? "1" : "0");
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Number(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private static string Number(ushort? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private static string Number(byte? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }
    }
}
