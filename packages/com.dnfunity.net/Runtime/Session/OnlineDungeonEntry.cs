using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>联网进副本的进度阶段(单调前进,失败时停在出错的那一步)。</summary>
    public enum OnlineDungeonStage
    {
        /// <summary>发"开始游戏"命令(类 1、包号 15,空载荷)。</summary>
        SendStartGame = 0,

        /// <summary>等装载起始通知(类 0、包号 27);它到之前不发"选副本"。</summary>
        WaitLoadingNotice = 1,

        /// <summary>发"选副本"命令(类 1、包号 16,5 字节载荷)。</summary>
        SendSelectDungeon = 2,

        /// <summary>等副本信息通知(类 0、包号 28)。</summary>
        WaitDungeonInfo = 3,

        /// <summary>等起始地图通知(类 0、包号 29);只有在 28 之后到的才算数。</summary>
        WaitStartMap = 4,

        /// <summary>全部走完(终局):27、28、29 都按序收齐。</summary>
        Done = 5,
    }

    /// <summary>联网进副本失败的原因(与停在哪一步一起看)。</summary>
    public enum OnlineDungeonFailure
    {
        /// <summary>没有失败。</summary>
        None = 0,

        /// <summary>入参本地检查不过(模式字节超出 0 到 4):什么都没有发。</summary>
        BadRequest,

        /// <summary>命令没发出去:帧层编码失败,状态在 <see cref="OnlineDungeonResult.SendStatus"/>。</summary>
        SendFailed,

        /// <summary>所在等待阶段的窗口到点还没等到要等的通知(停在哪一步看 <see cref="OnlineDungeonResult.Stage"/>)。</summary>
        Timeout,

        /// <summary>被服务端拒绝:被等命令的回包是失败形状,或收到了接受阶段的拒绝帧(码在 <see cref="OnlineDungeonResult.RejectionCode"/>)。</summary>
        Rejected,

        /// <summary>连接已断:对端关闭了流,或发命令时流已不可写。</summary>
        ConnectionClosed,

        /// <summary>入站字节流切帧出错(头里的整帧长度越界),会话不再接收。</summary>
        FramingFault,

        /// <summary>28 或 29 到了,但载荷读不下来(短包或个数声明超出载荷)。</summary>
        NoticeMalformed,
    }

    /// <summary>收到的一帧在这次进副本里被怎样对待(只描述处理结果,不含任何载荷内容)。</summary>
    public enum OnlineDungeonFrameNote
    {
        /// <summary>与这条链无关的帧,只记录(心跳、其它通知、没在等的命令回包都算)。</summary>
        None = 0,

        /// <summary>这一帧满足了当前在等的通知(27、28 或 29)。</summary>
        Accepted,

        /// <summary>来得不是时候的 28 或 29(29 先于 28,或在选副本命令发出之前):忽略,不缓存,只记录。</summary>
        OutOfOrder,

        /// <summary>已经收到过的通知又来一次(27、28 只认第一次;29 收下就进终局,之后的帧不再读):忽略,只记录。</summary>
        Duplicate,

        /// <summary>28 或 29 到了但载荷读不下来:进副本到此失败。</summary>
        NoticeMalformed,

        /// <summary>被等命令(15 或 16)的回包,成功形状:只记录。</summary>
        Acknowledged,

        /// <summary>被拒:被等命令的回包是失败形状,或收到了接受阶段的拒绝帧:进副本到此失败。</summary>
        Rejected,

        /// <summary>561 信封帧,解包委托给回了内层帧(内层帧另记一条,<see cref="OnlineDungeonFrame.FromEnvelope"/> 为真)。</summary>
        EnvelopeOpened,

        /// <summary>561 信封帧,没解开:没有注入解包委托、委托返回 null,或内层本身又是 561(不递归)。继续等。</summary>
        EnvelopeUnopened,

        /// <summary>561 信封帧,解包委托抛了异常:按没解开处理,继续等。</summary>
        EnvelopeFaulted,

        /// <summary>解包委托给回的字节不成一个内层帧(不足头长,或头里的整帧长度小于头长、大于给回的字节数)。忽略,继续等。</summary>
        InnerMalformed,

        /// <summary>内层帧带压缩标志(按包号 2、9、0x57 认):没有解压也没有解析,载荷长度是压缩态的。</summary>
        InnerCompressed,
    }

    /// <summary>进副本过程中收到的一帧的脱敏记录:只有到达时的阶段、类、包号、载荷长度与处理结果。</summary>
    public sealed class OnlineDungeonFrame
    {
        internal OnlineDungeonFrame(OnlineDungeonStage stage, PacketClass packetClass, int packetId, int payloadLength,
            bool fromEnvelope, OnlineDungeonFrameNote note)
        {
            Stage = stage;
            PacketClass = packetClass;
            PacketId = packetId;
            PayloadLength = payloadLength;
            FromEnvelope = fromEnvelope;
            Note = note;
        }

        /// <summary>这一帧被读到时,进副本所处的阶段。</summary>
        public OnlineDungeonStage Stage { get; }

        /// <summary>包类(内层帧是帧头第 0 字节原值;内层读不出头的 <see cref="OnlineDungeonFrameNote.InnerMalformed"/> 记为通知)。</summary>
        public PacketClass PacketClass { get; }

        /// <summary>包号(内层读不出头的 <see cref="OnlineDungeonFrameNote.InnerMalformed"/> 记为 -1)。</summary>
        public int PacketId { get; }

        /// <summary>
        /// 载荷字节数。直接收到的帧是解码后的长度(会话加密补的 0x00 算在内);内层帧是头里整帧长度减头长;
        /// 压缩的内层帧是压缩态的长度;读不出头的内层帧记解包委托给回的总字节数。
        /// </summary>
        public int PayloadLength { get; }

        /// <summary>是不是从 561 信封里解出来的内层帧(信封帧自己为 false)。</summary>
        public bool FromEnvelope { get; }

        /// <summary>这一帧被怎样对待。</summary>
        public OnlineDungeonFrameNote Note { get; }

        /// <summary>一行文字:<c>NOTI/28(21)</c>,内层帧后缀 <c>@561</c>,有处理结果时再接 <c>:结果</c>。不含任何载荷内容。</summary>
        public override string ToString()
        {
            string cls = PacketClass == PacketClass.Cmd ? "CMD" : (PacketClass == PacketClass.Noti ? "NOTI" : "类" + (int)PacketClass);
            return cls + "/" + PacketId + "(" + PayloadLength + ")" + (FromEnvelope ? "@561" : string.Empty)
                + (Note == OnlineDungeonFrameNote.None ? string.Empty : ":" + Note);
        }
    }

    /// <summary>
    /// 联网进副本的结局。停在哪一步由 <see cref="Stage"/> 与 <see cref="Failure"/> 一起说明:
    /// 比如"等 27 超时"是 <c>WaitLoadingNotice + Timeout</c>,"等起始地图超时"是 <c>WaitStartMap + Timeout</c>,"被拒"是 <c>Rejected</c>。
    /// 失败时已经收到的东西都如实保留(例如停在等 29 时,27 的原始载荷与 28 的解析结果仍在)。
    /// </summary>
    public sealed class OnlineDungeonResult
    {
        internal OnlineDungeonResult(OnlineDungeonStage stage, OnlineDungeonFailure failure, FrameStatus? sendStatus,
            ushort? rejectedPacketId, byte? rejectionCode, byte[] loadingNoticePayload,
            DungeonInfoNotice dungeonInfo, StartMapNotice startMap, IReadOnlyList<OnlineDungeonFrame> frames)
        {
            Stage = stage;
            Failure = failure;
            SendStatus = sendStatus;
            RejectedPacketId = rejectedPacketId;
            RejectionCode = rejectionCode;
            LoadingNoticePayload = loadingNoticePayload;
            DungeonInfo = dungeonInfo;
            StartMap = startMap;
            Frames = frames;
        }

        /// <summary>停在的阶段(成功为 <see cref="OnlineDungeonStage.Done"/>)。</summary>
        public OnlineDungeonStage Stage { get; }

        /// <summary>失败原因(成功为 <see cref="OnlineDungeonFailure.None"/>)。</summary>
        public OnlineDungeonFailure Failure { get; }

        /// <summary>是否三个通知都按序收齐。</summary>
        public bool Entered => Stage == OnlineDungeonStage.Done;

        /// <summary>
        /// 命令发送失败时帧层给的状态(仅 <see cref="OnlineDungeonFailure.SendFailed"/> 时有值;
        /// 此时 <see cref="Stage"/> 是 <see cref="OnlineDungeonStage.SendStartGame"/> 或 <see cref="OnlineDungeonStage.SendSelectDungeon"/>)。
        /// </summary>
        public FrameStatus? SendStatus { get; }

        /// <summary>被拒时的来源包号:15 或 16 是被拒的命令,1 是接受阶段的拒绝帧(仅 <see cref="OnlineDungeonFailure.Rejected"/> 时有值)。</summary>
        public ushort? RejectedPacketId { get; }

        /// <summary>被拒时回包里的失败码(回包里没有码时为 null)。</summary>
        public byte? RejectionCode { get; }

        /// <summary>
        /// 27 的原始载荷(没有解析,留给后续任务;没收到为 null)。直接收到的帧里会话加密补的 0x00 仍在末尾,
        /// 内层帧是头里整帧长度所指的那一段。
        /// </summary>
        public byte[] LoadingNoticePayload { get; }

        /// <summary>28 的解析结果(没收到为 null)。</summary>
        public DungeonInfoNotice DungeonInfo { get; }

        /// <summary>29 的解析结果(没收到为 null)。</summary>
        public StartMapNotice StartMap { get; }

        /// <summary>收到的帧按到达顺序的记录(只有类、包号、长度与处理结果;信封帧与它的内层帧各记一条)。</summary>
        public IReadOnlyList<OnlineDungeonFrame> Frames { get; }

        /// <summary>没解开的 561 信封帧数(没有解包委托、委托给回 null 或抛异常的都算)。</summary>
        public int UnopenedEnvelopeCount => Count(OnlineDungeonFrameNote.EnvelopeUnopened) + Count(OnlineDungeonFrameNote.EnvelopeFaulted);

        /// <summary>来得不是时候而被忽略的 28 或 29 的帧数。</summary>
        public int OutOfOrderCount => Count(OnlineDungeonFrameNote.OutOfOrder);

        /// <summary>重复到达而被忽略的 27、28 的帧数。</summary>
        public int DuplicateCount => Count(OnlineDungeonFrameNote.Duplicate);

        /// <summary>一行文字(不含任何载荷内容)。</summary>
        public override string ToString()
        {
            return "阶段=" + Stage + " 失败=" + Failure + " 帧=" + Frames.Count;
        }

        private int Count(OnlineDungeonFrameNote note)
        {
            int n = 0;
            foreach (OnlineDungeonFrame frame in Frames)
            {
                if (frame.Note == note)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// 进副本编排需要的会话接缝:只有它要用到的六个成员。<see cref="GameClientSession"/> 经内置适配器接入;
    /// 单独做成接口,是为了合成测试能造出真会话造不出的情形(帧层编码失败的 <see cref="FrameStatus"/>、发送时流已断),
    /// 不必为此改动会话类。
    /// </summary>
    public interface IOnlineDungeonSession
    {
        /// <summary>会话密码已就绪(已进城),可以发命令。</summary>
        bool IsReady { get; }

        /// <summary>对端已关闭连接。</summary>
        bool PeerClosed { get; }

        /// <summary>最近一次切帧错误(没有为 <see cref="FrameError.None"/>)。</summary>
        FrameError LastFrameError { get; }

        /// <summary>发一帧;含义同 <see cref="GameClientSession.SendAsync"/>(帧层失败返回状态,写失败抛 IO 异常)。</summary>
        Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken);

        /// <summary>
        /// 收帧;含义同 <see cref="GameClientSession.ReceiveAsync"/>(窗口到点、流结束或解出 <paramref name="stopAfterFrames"/> 帧就返回)。
        /// 编排每次只要一帧;实现多交了也不丢,编排会把多出的帧按序留给后面的阶段。
        /// </summary>
        Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 联网进副本编排(类比 <see cref="OnlineTownEntry"/>):在已进城的会话上做发送侧与"等通知"的状态推进,
    /// 条款依据探针的进副本段与客户端读码笔记:
    /// <list type="number">
    /// <item>发"开始游戏":类 1、包号 15、空载荷;服务端随后推装载起始通知(类 0、包号 27;读序复杂,这里只收原始载荷,不解析)。</item>
    /// <item>27 到了才发"选副本":类 1、包号 16、5 字节载荷 = u16 副本号(小端)、u8 模式(0 到 4)、u8 第三字节(固定 2)、u8 安全字节(调用方给,默认 0)。</item>
    /// <item>服务端推副本信息(类 0、包号 28,<see cref="DungeonInfoParser"/>),再推起始地图(类 0、包号 29,<see cref="StartMapParser"/>)。</item>
    /// </list>
    /// <para>
    /// 顺序纪律:27 没到就一直等(带阶段超时),27 到之前绝不发 16;28 必须先于 29。
    /// 早到的 29(以及发 16 之前读到的 28、29)记为 <see cref="OnlineDungeonFrameNote.OutOfOrder"/> 并【忽略,不缓存】:
    /// 真实客户端要求 28 先于 29,服务端不会合法地乱序发送,所以乱序的包要么是重放要么是上一轮的残留,
    /// 缓存下来再与后到的 28 拼用,可能把两轮的数据混在一起;忽略的代价只是真有乱序时多等一个 29 直到超时,而超时是如实的结局。
    /// 27、28 只认第一次,重复到达记为 <see cref="OnlineDungeonFrameNote.Duplicate"/>;29 收下就进终局,之后的帧不再读。
    /// 帧是一帧一帧处理的:阶段按"处理这一帧的那一刻"算,会话里早到却还没被读走的帧也一样;端口一次多交的帧先排在本地,留给后面的阶段。
    /// </para>
    /// <para>
    /// 信封:真实链路上 27、28、29 封在 561 号通知帧里(类 0)。信封的解包不在这里:构造时注入 <c>Func&lt;byte[], byte[]&gt;</c>,
    /// 入参是 561 帧的载荷(帧头之后的字节),返回完整的内层帧(15 字节头 + 载荷,头里整帧长度在偏移 3 起的 u32),解不开返回 null;
    /// 内层帧的类、包号、载荷走与直接收到的帧同一条处理路径。没注入委托(null)时 561 帧记为 <see cref="OnlineDungeonFrameNote.EnvelopeUnopened"/>
    /// 并继续等,该阶段最终超时。内层帧不核对校验,不递归拆信封;包号为 2、9、0x57 的内层帧视为压缩帧,不解压、不解析,只如实标注。
    /// </para>
    /// <para>
    /// 不做的事:不解密;不发 40 号"装载完成"命令(服务端可能要等它才推 29,见交接说明);不回心跳(收到的心跳只记录,
    /// 所以别与 <see cref="OnlineHeartbeat"/> 的泵同时收同一个会话);不核对 28 里的副本号与请求是否一致;
    /// 不清理调用前会话里已排队的帧;到 <see cref="OnlineDungeonStage.Done"/> 就停止读取,之后的帧留给调用方。
    /// </para>
    /// <para>失败返回带原因的结果而不抛异常;取消时抛 <see cref="OperationCanceledException"/>。全程没有落盘与打印。</para>
    /// </summary>
    public sealed class OnlineDungeonEntry
    {
        /// <summary>"开始游戏"命令的包号(类 1)。</summary>
        public const ushort StartGamePacketId = 15;

        /// <summary>"选副本"命令的包号(类 1)。</summary>
        public const ushort SelectDungeonPacketId = 16;

        /// <summary>装载起始通知的包号(类 0)。</summary>
        public const int LoadingNoticePacketId = 27;

        /// <summary>信封通知的包号(类 0):外部反作弊库把受保护的包封在里面。</summary>
        public const int ServerEnvelopePacketId = 0x231;

        /// <summary>"选副本"载荷长度:u16 副本号 + 模式 + 第三字节 + 安全字节。</summary>
        public const int SelectDungeonPayloadLength = 5;

        /// <summary>模式字节的最大值(取值 0 到 4)。</summary>
        public const byte MaxMode = 4;

        /// <summary>第三字节的默认值(固定 2;探针里真实客户端常用值)。</summary>
        public const byte DefaultThirdByte = 2;

        /// <summary>安全字节的默认值。</summary>
        public const byte DefaultSafeByte = 0;

        /// <summary>等 27 的默认窗口:探针发"开始游戏"后收 6 秒。</summary>
        public static readonly TimeSpan DefaultLoadingNoticeTimeout = TimeSpan.FromSeconds(6);

        /// <summary>等 28 的默认窗口:探针发"选副本"后收 8 秒。</summary>
        public static readonly TimeSpan DefaultDungeonInfoTimeout = TimeSpan.FromSeconds(8);

        /// <summary>等 29 的默认窗口:与等 28 同量级(服务端紧随 28 推出)。</summary>
        public static readonly TimeSpan DefaultStartMapTimeout = TimeSpan.FromSeconds(8);

        private readonly IOnlineDungeonSession _session;
        private readonly Func<byte[], byte[]> _unwrapEnvelope;

        /// <summary>
        /// 在已进城的会话上创建编排。<paramref name="unwrapEnvelope"/> 是 561 信封的解包委托(见类说明),可以为 null。
        /// </summary>
        public OnlineDungeonEntry(GameClientSession session, Func<byte[], byte[]> unwrapEnvelope = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            _session = new GameClientSessionAdapter(session);
            _unwrapEnvelope = unwrapEnvelope;
        }

        /// <summary>在任意实现了会话接缝的对象上创建编排(合成测试用)。</summary>
        public OnlineDungeonEntry(IOnlineDungeonSession session, Func<byte[], byte[]> unwrapEnvelope = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _unwrapEnvelope = unwrapEnvelope;
        }

        /// <summary>
        /// 走完"开始游戏 → 等 27 → 选副本 → 等 28 → 等 29"。
        /// </summary>
        /// <param name="dungeonId">副本号(u16,线上小端)。</param>
        /// <param name="mode">模式字节,取值 0 到 4;超出返回 <see cref="OnlineDungeonFailure.BadRequest"/>,什么都不发。</param>
        /// <param name="thirdByte">"选副本"载荷的第三字节,默认 2。</param>
        /// <param name="safeByte">"选副本"载荷的安全字节,默认 0。</param>
        /// <param name="loadingNoticeTimeout">等 27 的窗口;null 用 <see cref="DefaultLoadingNoticeTimeout"/>。</param>
        /// <param name="dungeonInfoTimeout">等 28 的窗口(从发出"选副本"起算);null 用 <see cref="DefaultDungeonInfoTimeout"/>。</param>
        /// <param name="startMapTimeout">等 29 的窗口(从收到 28 起算);null 用 <see cref="DefaultStartMapTimeout"/>。</param>
        /// <param name="onStage">每进入一个阶段(不含终局)回调一次。</param>
        /// <param name="cancellationToken">取消令牌;取消时抛 <see cref="OperationCanceledException"/>。</param>
        /// <exception cref="ArgumentOutOfRangeException">某个窗口不大于 0。</exception>
        /// <exception cref="InvalidOperationException">会话还没有就绪(没有收到启动包,不可能已进城)。</exception>
        public async Task<OnlineDungeonResult> EnterAsync(
            ushort dungeonId,
            byte mode,
            byte thirdByte = DefaultThirdByte,
            byte safeByte = DefaultSafeByte,
            TimeSpan? loadingNoticeTimeout = null,
            TimeSpan? dungeonInfoTimeout = null,
            TimeSpan? startMapTimeout = null,
            Action<OnlineDungeonStage> onStage = null,
            CancellationToken cancellationToken = default)
        {
            TimeSpan loadingWindow = Window(loadingNoticeTimeout, DefaultLoadingNoticeTimeout, nameof(loadingNoticeTimeout));
            TimeSpan infoWindow = Window(dungeonInfoTimeout, DefaultDungeonInfoTimeout, nameof(dungeonInfoTimeout));
            TimeSpan mapWindow = Window(startMapTimeout, DefaultStartMapTimeout, nameof(startMapTimeout));
            if (!_session.IsReady)
            {
                throw new InvalidOperationException("会话还没有收到启动包,不能进副本");
            }

            var attempt = new Attempt(_session, _unwrapEnvelope, onStage, cancellationToken);
            if (mode > MaxMode)
            {
                return attempt.Result(OnlineDungeonFailure.BadRequest);
            }
            if (_session.PeerClosed)
            {
                return attempt.Result(OnlineDungeonFailure.ConnectionClosed);
            }
            return await attempt.RunAsync(BuildSelectPayload(dungeonId, mode, thirdByte, safeByte), loadingWindow, infoWindow, mapWindow).ConfigureAwait(false);
        }

        /// <summary>"选副本"载荷:u16 副本号小端、模式、第三字节、安全字节(显式移位,不依赖本机字节序)。</summary>
        private static byte[] BuildSelectPayload(ushort dungeonId, byte mode, byte thirdByte, byte safeByte)
        {
            var payload = new byte[SelectDungeonPayloadLength];
            payload[0] = (byte)(dungeonId & 0xFF);
            payload[1] = (byte)(dungeonId >> 8);
            payload[2] = mode;
            payload[3] = thirdByte;
            payload[4] = safeByte;
            return payload;
        }

        private static TimeSpan Window(TimeSpan? value, TimeSpan fallback, string name)
        {
            TimeSpan window = value ?? fallback;
            if (window <= TimeSpan.Zero || window.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(name, "阶段超时必须大于 0 且不超过 int.MaxValue 毫秒");
            }
            return window;
        }

        /// <summary>真会话的适配器:一对一转调,不加任何行为。</summary>
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

        /// <summary>一次进副本的可变状态(每次调用一个,编排对象本身不存状态)。</summary>
        private sealed class Attempt
        {
            private readonly IOnlineDungeonSession _session;
            private readonly Func<byte[], byte[]> _unwrap;
            private readonly Action<OnlineDungeonStage> _onStage;
            private readonly CancellationToken _cancel;
            private readonly List<OnlineDungeonFrame> _frames = new List<OnlineDungeonFrame>();
            private readonly Queue<DecodedFrame> _backlog = new Queue<DecodedFrame>();    // 端口多交的帧(真会话一次只交一帧),按序留到后面的阶段

            private OnlineDungeonStage _stage = OnlineDungeonStage.SendStartGame;
            private OnlineDungeonFailure _failure;        // 读帧时发现的失败(被拒、通知读不下来):由等待循环在这一帧处理完后收尾
            private FrameStatus? _sendStatus;
            private ushort? _rejectedPacketId;
            private byte? _rejectionCode;
            private byte[] _loadingPayload;
            private DungeonInfoNotice _info;
            private StartMapNotice _map;

            public Attempt(IOnlineDungeonSession session, Func<byte[], byte[]> unwrap, Action<OnlineDungeonStage> onStage, CancellationToken cancel)
            {
                _session = session;
                _unwrap = unwrap;
                _onStage = onStage;
                _cancel = cancel;
            }

            public OnlineDungeonResult Result(OnlineDungeonFailure failure)
            {
                return new OnlineDungeonResult(_stage, failure, _sendStatus, _rejectedPacketId, _rejectionCode,
                    _loadingPayload, _info, _map, _frames.ToArray());
            }

            public async Task<OnlineDungeonResult> RunAsync(byte[] selectPayload, TimeSpan loadingWindow, TimeSpan infoWindow, TimeSpan mapWindow)
            {
                Enter(OnlineDungeonStage.SendStartGame);
                OnlineDungeonFailure failure = await SendAsync(StartGamePacketId, new byte[0]).ConfigureAwait(false);
                if (failure != OnlineDungeonFailure.None)
                {
                    return Result(failure);
                }

                Enter(OnlineDungeonStage.WaitLoadingNotice);
                failure = await WaitAsync(loadingWindow, () => _loadingPayload != null).ConfigureAwait(false);
                if (failure != OnlineDungeonFailure.None)
                {
                    return Result(failure);
                }

                // 顺序纪律:只有走到这里(27 已收到),"选副本"才会发出;上面等 27 失败的路径都在这一步之前返回。
                Enter(OnlineDungeonStage.SendSelectDungeon);
                failure = await SendAsync(SelectDungeonPacketId, selectPayload).ConfigureAwait(false);
                if (failure != OnlineDungeonFailure.None)
                {
                    return Result(failure);
                }

                Enter(OnlineDungeonStage.WaitDungeonInfo);
                failure = await WaitAsync(infoWindow, () => _info != null).ConfigureAwait(false);
                if (failure != OnlineDungeonFailure.None)
                {
                    return Result(failure);
                }

                Enter(OnlineDungeonStage.WaitStartMap);
                failure = await WaitAsync(mapWindow, () => _map != null).ConfigureAwait(false);
                if (failure != OnlineDungeonFailure.None)
                {
                    return Result(failure);
                }

                _stage = OnlineDungeonStage.Done;
                return Result(OnlineDungeonFailure.None);
            }

            private void Enter(OnlineDungeonStage stage)
            {
                _stage = stage;
                _onStage?.Invoke(stage);
            }

            /// <summary>发一条命令:帧层失败记状态,流已断记连接断开;发出去了返回 None。</summary>
            private async Task<OnlineDungeonFailure> SendAsync(ushort packetId, byte[] payload)
            {
                FrameStatus status;
                try
                {
                    status = await _session.SendAsync(PacketClass.Cmd, packetId, payload, _cancel).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return OnlineDungeonFailure.ConnectionClosed;
                }
                catch (ObjectDisposedException)
                {
                    return OnlineDungeonFailure.ConnectionClosed;
                }
                if (status != FrameStatus.Ok)
                {
                    _sendStatus = status;
                    return OnlineDungeonFailure.SendFailed;
                }
                return OnlineDungeonFailure.None;
            }

            /// <summary>
            /// 在 <paramref name="window"/> 内一帧一帧读,直到 <paramref name="done"/> 为真(返回 None)、读帧时发现失败、连接断了、切帧出错或窗口到点。
            /// 窗口到点后还会"最后看一眼"会话里已经排队的帧(不再等新数据),所以到点前已经到达的帧不会因为时间差被算成超时。
            /// 每帧都按处理它的那一刻的阶段对待:端口一次多交的帧先排在本地,等后面的阶段再按序处理(同真会话"没读走的帧仍在队里"一致)。
            /// </summary>
            private async Task<OnlineDungeonFailure> WaitAsync(TimeSpan window, Func<bool> done)
            {
                Stopwatch watch = Stopwatch.StartNew();
                while (true)
                {
                    _cancel.ThrowIfCancellationRequested();

                    bool lastLook = false;
                    DecodedFrame frame = null;
                    if (_backlog.Count > 0)
                    {
                        frame = _backlog.Dequeue();
                    }
                    else
                    {
                        TimeSpan left = window - watch.Elapsed;
                        lastLook = left <= TimeSpan.Zero;
                        List<DecodedFrame> frames = await _session.ReceiveAsync(lastLook ? TimeSpan.Zero : left, 1, _cancel).ConfigureAwait(false);
                        if (frames != null && frames.Count > 0)
                        {
                            frame = frames[0];
                            for (int i = 1; i < frames.Count; i++)
                            {
                                _backlog.Enqueue(frames[i]);
                            }
                        }
                    }

                    if (frame != null)
                    {
                        HandleFrame(frame);
                        if (_failure != OnlineDungeonFailure.None)
                        {
                            return _failure;
                        }
                        if (done())
                        {
                            return OnlineDungeonFailure.None;
                        }
                        continue;
                    }

                    // 没读到帧:只有断了、切帧出错或到点三种可能(会话在这三种情形才空手返回);断与错要先认,免得空转到超时。
                    if (_session.PeerClosed)
                    {
                        return OnlineDungeonFailure.ConnectionClosed;
                    }
                    if (_session.LastFrameError != FrameError.None)
                    {
                        return OnlineDungeonFailure.FramingFault;
                    }
                    if (lastLook)
                    {
                        return OnlineDungeonFailure.Timeout;
                    }
                }
            }

            private void HandleFrame(DecodedFrame frame)
            {
                PacketClass cls = frame.Header.Class;
                int id = frame.Header.PacketId;
                byte[] payload = frame.Payload ?? new byte[0];

                if (frame.Kind == DecodedKind.Rejected)
                {
                    Log(cls, id, payload.Length, false, OnlineDungeonFrameNote.Rejected);
                    Reject((ushort)id, payload.Length >= 2 ? payload[1] : (byte?)null);
                    return;
                }
                if (cls == PacketClass.Noti && id == ServerEnvelopePacketId)
                {
                    HandleEnvelope(payload);
                    return;
                }
                HandleNotice(cls, id, payload, false);
            }

            /// <summary>561 信封:交给委托取内层帧;取不到就记"没解开"继续等,取到了就把内层帧走同一条处理路径。</summary>
            private void HandleEnvelope(byte[] envelopePayload)
            {
                if (_unwrap == null)
                {
                    Log(PacketClass.Noti, ServerEnvelopePacketId, envelopePayload.Length, false, OnlineDungeonFrameNote.EnvelopeUnopened);
                    return;
                }

                byte[] inner;
                try
                {
                    inner = _unwrap(envelopePayload);
                }
                catch (Exception)
                {
                    Log(PacketClass.Noti, ServerEnvelopePacketId, envelopePayload.Length, false, OnlineDungeonFrameNote.EnvelopeFaulted);
                    return;
                }
                if (inner == null)
                {
                    Log(PacketClass.Noti, ServerEnvelopePacketId, envelopePayload.Length, false, OnlineDungeonFrameNote.EnvelopeUnopened);
                    return;
                }
                Log(PacketClass.Noti, ServerEnvelopePacketId, envelopePayload.Length, false, OnlineDungeonFrameNote.EnvelopeOpened);

                // 内层帧:15 字节头(类、包号、整帧长度、两个校验),载荷自偏移 15 起。整帧长度要落在 [头长, 给回的字节数];
                // 比给回的字节数小就把多出的尾巴(分组密码的补齐)丢掉。校验不核对:内层校验覆盖什么没有定论。
                int headerLength = FrameLayout.ServerToClient.HeaderLength;
                if (!PacketHeader.TryRead(FrameLayout.ServerToClient, inner, out PacketHeader header)
                    || header.TotalLength < (uint)headerLength
                    || header.TotalLength > (uint)inner.Length)
                {
                    bool hasHeader = inner.Length >= headerLength;
                    Log(hasHeader ? header.Class : PacketClass.Noti, hasHeader ? header.PacketId : -1, inner.Length, true,
                        OnlineDungeonFrameNote.InnerMalformed);
                    return;
                }

                var innerPayload = new byte[(int)header.TotalLength - headerLength];
                Buffer.BlockCopy(inner, headerLength, innerPayload, 0, innerPayload.Length);
                if (ServerFrameDecoder.IsCompressedId(header.PacketId))
                {
                    // 压缩帧:不解压、不解析(27、28、29 都不在压缩包号里,不会因此漏掉要等的通知)。
                    Log(header.Class, header.PacketId, innerPayload.Length, true, OnlineDungeonFrameNote.InnerCompressed);
                    return;
                }
                HandleNotice(header.Class, header.PacketId, innerPayload, true);
            }

            /// <summary>处理一个已拿到类、包号与载荷的帧(直接收到的与信封里解出的共用)。</summary>
            private void HandleNotice(PacketClass cls, int id, byte[] payload, bool fromEnvelope)
            {
                if (cls == PacketClass.Cmd)
                {
                    HandleCommandReply(id, payload, fromEnvelope);
                    return;
                }
                if (cls != PacketClass.Noti)
                {
                    Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.None);
                    return;
                }

                switch (id)
                {
                    case ServerEnvelopePacketId:
                        // 只有内层帧会走到这里(直接收到的 561 在上一层已拦下):信封里套信封不递归拆。
                        Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.EnvelopeUnopened);
                        break;

                    case LoadingNoticePacketId:
                        if (_stage == OnlineDungeonStage.WaitLoadingNotice)
                        {
                            _loadingPayload = (byte[])payload.Clone();
                            Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.Accepted);
                        }
                        else
                        {
                            Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.Duplicate);
                        }
                        break;

                    case DungeonInfoParser.PacketId:
                        if (_stage == OnlineDungeonStage.WaitDungeonInfo)
                        {
                            if (DungeonInfoParser.TryParse(payload, out DungeonInfoNotice info))
                            {
                                _info = info;
                                Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.Accepted);
                            }
                            else
                            {
                                Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.NoticeMalformed);
                                SetFailure(OnlineDungeonFailure.NoticeMalformed);
                            }
                        }
                        else
                        {
                            Log(cls, id, payload.Length, fromEnvelope,
                                _info != null ? OnlineDungeonFrameNote.Duplicate : OnlineDungeonFrameNote.OutOfOrder);
                        }
                        break;

                    case StartMapParser.PacketId:
                        if (_stage == OnlineDungeonStage.WaitStartMap)
                        {
                            if (StartMapParser.TryParse(payload, out StartMapNotice map))
                            {
                                _map = map;
                                Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.Accepted);
                            }
                            else
                            {
                                Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.NoticeMalformed);
                                SetFailure(OnlineDungeonFailure.NoticeMalformed);
                            }
                        }
                        else
                        {
                            // 28 之前到的 29(或发 16 之前读到的 29):忽略,不缓存(理由见类说明)。
                            // 收下 29 就进终局、不再读帧,所以走到这里的 29 不可能是重复的。
                            Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.OutOfOrder);
                        }
                        break;

                    default:
                        Log(cls, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.None);
                        break;
                }
            }

            /// <summary>
            /// 类 1 的帧:只有"当前在等的命令"的回包才有意义(等 27 时是 15,等 28 与 29 时是 16)。
            /// 失败形状 = 载荷 [0] 为 0 且至少有 2 字节、[1] 是失败码(与登录回包、选角回包同一形状约定;
            /// 选副本回包 8 字节的字段含义尚未读通,这个形状对它是推断,见交接说明),孤零零的 [0] 不算判决。
            /// </summary>
            private void HandleCommandReply(int id, byte[] payload, bool fromEnvelope)
            {
                bool awaited = (id == StartGamePacketId && _stage == OnlineDungeonStage.WaitLoadingNotice)
                    || (id == SelectDungeonPacketId && (_stage == OnlineDungeonStage.WaitDungeonInfo || _stage == OnlineDungeonStage.WaitStartMap));
                if (!awaited)
                {
                    Log(PacketClass.Cmd, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.None);
                    return;
                }

                if (payload.Length >= 2 && payload[0] == 0)
                {
                    Log(PacketClass.Cmd, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.Rejected);
                    Reject((ushort)id, payload[1]);
                }
                else
                {
                    Log(PacketClass.Cmd, id, payload.Length, fromEnvelope, OnlineDungeonFrameNote.Acknowledged);
                }
            }

            private void Reject(ushort packetId, byte? code)
            {
                if (_failure != OnlineDungeonFailure.None)
                {
                    return;
                }
                _failure = OnlineDungeonFailure.Rejected;
                _rejectedPacketId = packetId;
                _rejectionCode = code;
            }

            private void SetFailure(OnlineDungeonFailure failure)
            {
                if (_failure == OnlineDungeonFailure.None)
                {
                    _failure = failure;
                }
            }

            private void Log(PacketClass cls, int id, int payloadLength, bool fromEnvelope, OnlineDungeonFrameNote note)
            {
                _frames.Add(new OnlineDungeonFrame(_stage, cls, id, payloadLength, fromEnvelope, note));
            }
        }
    }
}
