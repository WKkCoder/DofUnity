using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>战斗状态机所处的阶段(单调前进)。</summary>
    public enum OnlineCombatPhase
    {
        /// <summary>起始地图已建表,在打:收击杀确认、等清图通知(多房间副本里换房也发生在这个阶段:换房成功只换表,阶段不变)。结算命令的闸门关着。</summary>
        Fighting = 0,

        /// <summary>清图通知(类 0、包号 0x1f)到了:结算命令的闸门打开。</summary>
        Cleared = 1,

        /// <summary>结算命令(类 1、包号 49)已发出,等两个结算通知(0x22、0x23)按序到齐;不再报击杀。</summary>
        Settling = 2,

        /// <summary>两个结算通知收齐:可以回城(终局)。</summary>
        Settled = 3,
    }

    /// <summary>一只怪在本地对应表里的状态。</summary>
    public enum OnlineCombatMonsterState
    {
        /// <summary>还没报过击杀。</summary>
        Alive = 0,

        /// <summary>击杀命令已发出,等服务端确认。</summary>
        KillSent,

        /// <summary>服务端已确认(击杀确认通知到了)。</summary>
        Confirmed,

        /// <summary>服务端回了击杀失败(它的怪表里没有这个编号)。</summary>
        Refused,
    }

    /// <summary>一条击杀或结算命令被本地拦下(或没能发出)的原因。</summary>
    public enum OnlineCombatBlock
    {
        /// <summary>没有被拦。</summary>
        None = 0,

        /// <summary>本地编号不在对应表里(负数或不小于怪数):不发包。</summary>
        UnknownLocalId,

        /// <summary>这只怪的服务端编号与前面某条记录重复,没有独立的编号可报:不发包(服务端同一编号只认一次)。</summary>
        NoServerNumber,

        /// <summary>这只怪的击杀已经报过:不再发(客户端每只怪只发一次,服务端第二次必失败)。</summary>
        AlreadyReported,

        /// <summary>击杀:结算命令已发出,不再报击杀。换房:已经进入结算流程(清图通知到了),不再换房。</summary>
        PhaseClosed,

        /// <summary>结算:还没有收到清图通知,闸门没开,不发包(服务端没清图就收到结算会记反作弊计数)。</summary>
        NotCleared,

        /// <summary>结算:结算命令已经发过,不再发。</summary>
        SettlementAlreadySent,

        /// <summary>命令没发出去:帧层编码失败(状态机的状态没有变化)。</summary>
        FrameFailed,

        /// <summary>换房:上一条换房命令还在等回应(新房间的起始地图通知或失败回包),不重复发。</summary>
        MoveInFlight,

        /// <summary>
        /// 换房:当前房间的怪还没有全部被服务端确认击杀,闸门没开,不发包——服务端在当前房间没清时一律回错误码 20,所以先在本地拦下。
        /// 这是客户端的预过滤(只认"全部确认"这条正面证据),放行与否仍以服务端为准。
        /// </summary>
        RoomNotCleared,

        /// <summary>换房:进上一个房间之后的装载握手还没完(装载完成命令没发出,或发出了装载完成通知还没到)。</summary>
        LoadPending,

        /// <summary>换房:这个房间里已经被服务端拒绝够次数(<see cref="OnlineDungeonCombatOptions.MaxMoveRefusalsPerRoom"/>),不再发。</summary>
        MoveBudgetExhausted,

        /// <summary>击杀或换房:请求绑定的房间序号与状态机此刻所在的房间不符(房间已经换了,或那个房间还没到),不发包。</summary>
        StaleRoom,

        /// <summary>装载完成命令:此刻没有等着报告的装载握手(还没换过房,或已经报过,或已经完成)。</summary>
        NoLoadPending,
    }

    /// <summary>发送类调用的结局。</summary>
    public enum OnlineCombatSendOutcome
    {
        /// <summary>命令已发出,状态已推进。</summary>
        Sent = 0,

        /// <summary>被本地规则拦下,没有发包(原因在 <see cref="OnlineCombatSendResult.Block"/>)。</summary>
        Blocked,

        /// <summary>帧层没能编码出这一帧(状态在 <see cref="OnlineCombatSendResult.FrameStatus"/>),状态机没有推进。</summary>
        FrameFailed,
    }

    /// <summary>发送类调用的结果。</summary>
    public readonly struct OnlineCombatSendResult
    {
        public OnlineCombatSendResult(OnlineCombatSendOutcome outcome, OnlineCombatBlock block, FrameStatus frameStatus)
        {
            Outcome = outcome;
            Block = block;
            FrameStatus = frameStatus;
        }

        public OnlineCombatSendOutcome Outcome { get; }

        /// <summary>被拦的原因(<see cref="OnlineCombatSendOutcome.Blocked"/> 或帧层失败时有值,其余为 <see cref="OnlineCombatBlock.None"/>)。</summary>
        public OnlineCombatBlock Block { get; }

        /// <summary>帧层状态(只有帧层失败时不是 <see cref="Frame.FrameStatus.Ok"/>)。</summary>
        public FrameStatus FrameStatus { get; }

        /// <summary>是否真的发出去了。</summary>
        public bool Sent => Outcome == OnlineCombatSendOutcome.Sent;
    }

    /// <summary>收到的一帧被状态机怎样对待。</summary>
    public enum OnlineCombatFrameVerdict
    {
        /// <summary>不在状态机的包集合里:只计数,原样透传给调用方。</summary>
        Passthrough = 0,

        /// <summary>推进了状态。</summary>
        Accepted,

        /// <summary>来得不是时候(例如还没报过击杀就来了确认、结算命令之前就来了结算通知):忽略,不缓存。</summary>
        OutOfOrder,

        /// <summary>已经处理过的又来一次:忽略。</summary>
        Duplicate,

        /// <summary>带怪编号的帧,编号不在起始地图的记录里:忽略。</summary>
        UnknownNumber,

        /// <summary>包号认得但载荷读不下来:忽略。</summary>
        Malformed,
    }

    /// <summary>结算通知的两段。</summary>
    public enum OnlineCombatSettlementPart
    {
        None = 0,

        /// <summary>通关结果通知(类 0、包号 0x22)。</summary>
        Result = 1,

        /// <summary>通关奖励通知(类 0、包号 0x23)。</summary>
        Reward = 2,
    }

    /// <summary>状态机产出的结构化事件的种类。</summary>
    public enum OnlineCombatEventKind
    {
        /// <summary>起始地图建好了对应表(<see cref="OnlineCombatEvent.Total"/> 是怪数,可以是 0);构造时恰好一条。</summary>
        MonstersSpawned = 0,

        /// <summary>击杀命令已发出。</summary>
        KillSent,

        /// <summary>击杀被本地拦下(原因在 <see cref="OnlineCombatEvent.Block"/>)。</summary>
        KillBlocked,

        /// <summary>服务端确认了一只怪的击杀(击杀确认通知)。</summary>
        KillConfirmed,

        /// <summary>服务端回了击杀失败。</summary>
        KillRefused,

        /// <summary>清图通知到了(<see cref="OnlineCombatEvent.Sent"/> 与 <see cref="OnlineCombatEvent.Confirmed"/> 是此刻的计数)。</summary>
        MapCleared,

        /// <summary>结算命令已发出。</summary>
        SettlementSent,

        /// <summary>结算命令被本地拦下(原因在 <see cref="OnlineCombatEvent.Block"/>)。</summary>
        SettlementBlocked,

        /// <summary>一段结算通知到了(哪一段在 <see cref="OnlineCombatEvent.Part"/>)。</summary>
        SettlementArrived,

        /// <summary>两段结算通知收齐,可以回城;终局事件,恰好一条。</summary>
        ReadyToReturnToTown,

        /// <summary>一帧在包集合里却因乱序、重复、编号未知或载荷读不下来被忽略(原因在 <see cref="OnlineCombatEvent.Verdict"/>)。</summary>
        FrameIgnored,

        /// <summary>换房命令已发出(<see cref="OnlineCombatEvent.Room"/> 是离开的那个房间),等新房间的起始地图通知或失败回包。</summary>
        RoomMoveSent,

        /// <summary>换房被本地拦下(原因在 <see cref="OnlineCombatEvent.Block"/>)。</summary>
        RoomMoveBlocked,

        /// <summary>服务端回了换房失败(错误码在 <see cref="OnlineCombatEvent.ReplyCode"/>):状态机留在当前房间,什么都没换。</summary>
        RoomMoveRefused,

        /// <summary>
        /// 新房间的起始地图通知到了并被接受:对应表已换成新房间的怪表(<see cref="OnlineCombatEvent.Room"/> 是新房间序号,
        /// <see cref="OnlineCombatEvent.RoomMonsters"/> 是新表的怪数,<see cref="OnlineCombatEvent.TrailingBytes"/> 是读完之后载荷里剩的字节数)。
        /// </summary>
        RoomEntered,

        /// <summary>装载完成命令已发出。</summary>
        RoomLoadedSent,

        /// <summary>装载完成命令被本地拦下(原因在 <see cref="OnlineCombatEvent.Block"/>)。</summary>
        RoomLoadedBlocked,

        /// <summary>装载完成通知到了并被接受:这个房间的装载握手完成。</summary>
        RoomLoaded,
    }

    /// <summary>
    /// 状态机产出的一条结构化事件(只读)。<see cref="Total"/>、<see cref="Sent"/>、<see cref="Confirmed"/> 是事件发生那一刻的计数快照(跨房间累计),
    /// 日志只靠它们与枚举名,不碰载荷;<see cref="ServerId"/> 是服务端怪编号(来自起始地图记录),只给程序用,日志不写。
    /// </summary>
    public sealed class OnlineCombatEvent
    {
        internal OnlineCombatEvent(OnlineCombatEventKind kind, int localId, int serverId, OnlineCombatBlock block,
            OnlineCombatFrameVerdict verdict, OnlineCombatSettlementPart part, PacketClass packetClass, int packetId,
            int total, int sent, int confirmed, int room, int roomMonsters, int replyCode, int trailingBytes)
        {
            Kind = kind;
            LocalId = localId;
            ServerId = serverId;
            Block = block;
            Verdict = verdict;
            Part = part;
            PacketClass = packetClass;
            PacketId = packetId;
            Total = total;
            Sent = sent;
            Confirmed = confirmed;
            Room = room;
            RoomMonsters = roomMonsters;
            ReplyCode = replyCode;
            TrailingBytes = trailingBytes;
        }

        public OnlineCombatEventKind Kind { get; }

        /// <summary>本地编号(按当时所在房间的怪记录顺序的序号);事件与某只怪无关时为 -1。</summary>
        public int LocalId { get; }

        /// <summary>服务端怪编号;事件与某只怪无关或没有编号时为 -1。</summary>
        public int ServerId { get; }

        /// <summary>
        /// 被拦的原因(仅 <see cref="OnlineCombatEventKind.KillBlocked"/>、<see cref="OnlineCombatEventKind.SettlementBlocked"/>、
        /// <see cref="OnlineCombatEventKind.RoomMoveBlocked"/>、<see cref="OnlineCombatEventKind.RoomLoadedBlocked"/>)。
        /// </summary>
        public OnlineCombatBlock Block { get; }

        /// <summary>忽略的原因(仅 <see cref="OnlineCombatEventKind.FrameIgnored"/>)。</summary>
        public OnlineCombatFrameVerdict Verdict { get; }

        /// <summary>哪一段结算通知(仅 <see cref="OnlineCombatEventKind.SettlementArrived"/>)。</summary>
        public OnlineCombatSettlementPart Part { get; }

        /// <summary>被忽略的那一帧的包类(仅 <see cref="OnlineCombatEventKind.FrameIgnored"/>)。</summary>
        public PacketClass PacketClass { get; }

        /// <summary>被忽略的那一帧的包号(仅 <see cref="OnlineCombatEventKind.FrameIgnored"/>,其余为 -1)。</summary>
        public int PacketId { get; }

        /// <summary>到此刻为止进过的各房间的怪数合计(只有起始地图时就是起始地图的怪数)。</summary>
        public int Total { get; }

        /// <summary>此刻已发出击杀命令的怪数(跨房间累计)。</summary>
        public int Sent { get; }

        /// <summary>此刻已被服务端确认的怪数(跨房间累计)。</summary>
        public int Confirmed { get; }

        /// <summary>
        /// 事件发生时状态机所在的房间序号:起始房间是 0,每接受一条新房间的起始地图通知加 1。
        /// <see cref="OnlineCombatEventKind.RoomEntered"/> 里是进入之后的新序号;<see cref="OnlineCombatEventKind.RoomMoveSent"/> 等换房事件里是被离开的房间。
        /// </summary>
        public int Room { get; }

        /// <summary>事件发生时对应表(当前房间)里的怪数。</summary>
        public int RoomMonsters { get; }

        /// <summary>换房失败回包里的错误码(仅 <see cref="OnlineCombatEventKind.RoomMoveRefused"/>,其余为 -1)。</summary>
        public int ReplyCode { get; }

        /// <summary>新房间起始地图通知读完之后载荷里剩的字节数(仅 <see cref="OnlineCombatEventKind.RoomEntered"/>,其余为 -1;真服复验时拿它对账怪记录步长)。</summary>
        public int TrailingBytes { get; }
    }

    /// <summary>对应表里的一只怪:本地编号 ↔ 服务端编号,以及它的击杀进度。</summary>
    public sealed class OnlineCombatMonster
    {
        internal OnlineCombatMonster(int localId, StartMapMonsterRecord record, ushort serverId, bool duplicateNumber, int room)
        {
            LocalId = localId;
            Record = record;
            ServerId = serverId;
            DuplicateNumber = duplicateNumber;
            Room = room;
        }

        /// <summary>本地编号:这条记录在它所属房间的起始地图怪记录里的序号(从 0 起,线上顺序);不同房间的本地编号各自从 0 起。</summary>
        public int LocalId { get; }

        /// <summary>这只怪所属的房间序号(起始房间是 0;表换掉之后旧表里的怪对象仍保留它们最后的状态,<see cref="Room"/> 还是旧的)。</summary>
        public int Room { get; }

        /// <summary>服务端怪编号:记录的第 3 项(<see cref="StartMapMonsterRecord.C"/>)。</summary>
        public ushort ServerId { get; }

        /// <summary>整条记录(宿主要按它的别的项生成本地怪时用)。</summary>
        public StartMapMonsterRecord Record { get; }

        /// <summary>服务端编号与前面某条记录重复(服务端同一张图里编号必须唯一,所以只会出现在坏数据里):这只怪不能独立上报击杀。</summary>
        public bool DuplicateNumber { get; }

        /// <summary>击杀进度。</summary>
        public OnlineCombatMonsterState State { get; internal set; }
    }

    /// <summary>某个(类、包号)被透传了多少帧。</summary>
    public readonly struct OnlineCombatPacketCount
    {
        public OnlineCombatPacketCount(PacketClass packetClass, int packetId, int count)
        {
            PacketClass = packetClass;
            PacketId = packetId;
            Count = count;
        }

        public PacketClass PacketClass { get; }

        public int PacketId { get; }

        public int Count { get; }
    }

    /// <summary>当前房间的装载握手进行到哪一步(装载完成命令 CMD 40 与装载完成通知 30)。</summary>
    public enum OnlineRoomLoadState
    {
        /// <summary>没有握手在等:起始房间缺省如此(见 <see cref="OnlineDungeonCombatOptions.AcknowledgeStartRoomLoaded"/>)、握手已完成、或没开握手(<see cref="OnlineDungeonCombatOptions.AcknowledgeRoomLoaded"/> 为 false)。</summary>
        None = 0,

        /// <summary>进了房间,装载完成命令还没发(应当马上发:<see cref="OnlineDungeonCombat.ReportRoomLoadedAsync"/>)。换房被 <see cref="OnlineCombatBlock.LoadPending"/> 拦着。</summary>
        ReportDue,

        /// <summary>装载完成命令已发出,等装载完成通知。换房仍被 <see cref="OnlineCombatBlock.LoadPending"/> 拦着。</summary>
        AwaitingNotice,
    }

    /// <summary>状态机发命令用的发送口:只有一个"发一帧",语义同 <see cref="GameClientSession.SendAsync"/>(帧层失败返回状态,写失败抛 IO 异常)。</summary>
    public interface IOnlineCombatPort
    {
        Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 战斗状态机与它的跑器的可调项,全有缺省值。跑器在收到清图通知后总是自动发结算命令;
    /// 想自己掌握结算时机的调用方不用跑器,直接驱动 <see cref="OnlineDungeonCombat"/>(它的 <see cref="OnlineDungeonCombat.RequestSettlementAsync"/> 是公开的)。
    /// </summary>
    public sealed class OnlineDungeonCombatOptions
    {
        /// <summary>跑器每次收帧的窗口,也是它多久回头看一次击杀队列;缺省 100 毫秒。</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>发出结算命令后等两个结算通知的上限;缺省 20 秒(服务端自己的兜底定时是 15 秒量级)。</summary>
        public TimeSpan SettlementTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>整段战斗的上限;null(缺省)不设上限,一直跑到回城就绪、连接断开或取消。</summary>
        public TimeSpan? OverallTimeout { get; set; }

        /// <summary>按服务端怪编号拼击杀命令载荷;null 用 <see cref="OnlineCombatPayloads.BuildKill"/> 的缺省(没有击杀者、没有攻击记录)。</summary>
        public Func<ushort, byte[]> KillPayload { get; set; }

        /// <summary>拼结算命令载荷;null 用 <see cref="OnlineCombatPayloads.BuildSettlement"/>。</summary>
        public Func<byte[]> SettlementPayload { get; set; }

        /// <summary>
        /// 按换房请求拼换房命令载荷;null 用 <see cref="OnlineCombatPayloads.BuildMoveRoom"/> 的缺省(目标格、位置与分层标志取自请求,其余取座位跳过等"读得满、不触发座位与 trapBits 计数"的取值)。
        /// 返回 null 或长度不是 <see cref="OnlineCombatPayloads.MoveRoomLength"/>(服务端读不满就断线)时状态机抛 <see cref="InvalidOperationException"/>,不发包、状态不变。
        /// </summary>
        public Func<OnlineMoveRoomRequest, byte[]> MovePayload { get; set; }

        /// <summary>
        /// 拼装载完成命令载荷;null 用 <see cref="OnlineCombatPayloads.BuildRoomLoaded"/>(两个 0)。
        /// 返回 null 或长度不是 <see cref="OnlineCombatPayloads.RoomLoadedLength"/> 时状态机抛 <see cref="InvalidOperationException"/>,不发包、状态不变。
        /// </summary>
        public Func<byte[]> LoadedPayload { get; set; }

        /// <summary>
        /// 进了新房间之后要不要走装载握手(发装载完成命令、等装载完成通知):缺省 true。
        /// 为 true 时跑器在新房间的起始地图通知被接受之后自动发装载完成命令,状态机把"装载握手完成"算作下一次换房的前置条件;
        /// 为 false 时状态机不等握手(想自己掌握装载上报时机的调用方,可以直接调 <see cref="OnlineDungeonCombat.ReportRoomLoadedAsync"/>,
        /// 但状态机此时不会进入"等着报告"的状态,那个调用会被 <see cref="OnlineCombatBlock.NoLoadPending"/> 拦下)。
        /// 为什么缺省打开:静态读码看到服务端每次成功换房都起一个装载超时定时器,到点没报装载完成的成员会被踢出副本(见 <see cref="OnlineCombatPayloads"/> 的说明)。
        /// 只影响换房之后的新房间;起始房间要不要报装载完成看 <see cref="AcknowledgeStartRoomLoaded"/>。
        /// </summary>
        public bool AcknowledgeRoomLoaded { get; set; } = true;

        /// <summary>
        /// 起始房间要不要也走装载握手(战斗一开始就发一条装载完成命令、等装载完成通知):缺省 false。
        /// 只有 <see cref="AcknowledgeRoomLoaded"/> 为 true 时才生效。
        /// 缺省关着是为了让"只有起始地图一张图"的既有行为一个字节都不变:进副本的编排(<see cref="OnlineDungeonEntry"/>)从来不发装载完成命令,
        /// 真服也证实了服务端不等它就推起始地图(库内账目 E-387),所以单图战斗线上只有击杀与结算命令。
        /// 但真实客户端是每张图装载完都发一次(E-370),而服务端在开局时就起了装载超时定时器(单人 120、组队 90,单位没读通),
        /// 到点还没报装载完成的成员会被踢出副本——起始房要打得比这个时限久的调用方(多房间副本的第一间)应当把它打开。
        /// 打开的代价:服务端在"当前格是起始点"时会拿命令里的两个校验和与自己算的比,比不上只记反作弊计数(0x25b、0x25c,不拒绝;
        /// 缺省填 0,多半比不上,这是推断),计数过阈值后的处置没读通——这是缺省不打开的另一个理由,真服复验时要看这一条。
        /// </summary>
        public bool AcknowledgeStartRoomLoaded { get; set; }

        /// <summary>同一个房间里换房被服务端拒绝多少次之后,状态机不再替这个房间发换房命令;缺省 3,至少 1。到下一个房间重新计数。</summary>
        public int MaxMoveRefusalsPerRoom { get; set; } = 3;

        /// <summary>
        /// 换房握手每一步等回应的上限:换房命令发出后等新房间的起始地图通知或失败回包、装载完成命令发出后等装载完成通知;缺省 10 秒。
        /// 超过就以 <see cref="OnlineDungeonCombatOutcome.Timeout"/> 收尾(服务端自己的装载超时常量单人 120、组队 90,单位没读通)。
        /// </summary>
        public TimeSpan RoomHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>跑器每产出一条事件就在它的循环线程上回调一次;null 不回调。抛出的异常被吞掉。</summary>
        public Action<OnlineCombatEvent> OnEvent { get; set; }
    }

    /// <summary>
    /// 联网副本战斗的薄状态机:输入是进副本拿到的起始地图快照(<see cref="StartMapNotice"/>)与一个发送口,产出一串结构化事件(<see cref="OnlineCombatEvent"/>)。
    /// 它只管"本地击杀 → 服务端认账 →(多房间时)换房 → 清图 → 结算 → 可以回城"这一条链,不碰战斗本身(怪怎么生成、怎么打死是宿主与离线战斗的事),
    /// 也不读帧(收帧由 <see cref="OnlineDungeonCombatRunner"/> 或调用方做,逐帧交给 <see cref="OnFrame"/>)。
    /// <para>
    /// 对应表:本地编号 = 所在房间的起始地图怪记录在线上的顺序号(从 0 起),服务端编号 = 同一条记录的第 3 项(<see cref="StartMapMonsterRecord.C"/>)。
    /// 依据是既有定案(combat 线静态读码笔记的 §4.4,下面简称"笔记",出处说明见 <see cref="OnlineCombatPayloads"/>):服务端每个房间取一个 [0, 60000) 的随机基数、怪与物品共用计数器逐个加一给每只怪发键,
    /// 键经起始地图通知的怪记录第 3 项下发(推断,判据:上报本地小编号时服务端会回击杀失败);客户端本地计数器没有任何网络灌入通道,
    /// 所以本地编号灌不进服务端,只能查表。表错位一只就会把别的怪的键报上去,测试逐只核对。
    /// 每个房间一张表:起始房间的表来自构造时的起始地图;换房成功之后整体换成新房间的起始地图通知里的表(本地编号重新从 0 起,服务端编号是新房间自己的),旧表里的怪对象保持它们最后的状态、不再更新。
    /// </para>
    /// <para>
    /// 链的规则(服务端侧依据 笔记 §4.5–§5):
    /// <list type="bullet">
    /// <item>击杀:每只怪只报一次(<see cref="ReportKillAsync(int, CancellationToken)"/>);本地编号不在当前房间的表里、已报过、结算已发出的一律在本地拦下不发包;服务端按键在当前地图的怪表里找,找到即删除并广播确认(类 0、包号 0x26),找不到回失败(类 1、包号 0x2a)。
    /// 多房间时调用方应当用带房间序号的重载(<see cref="ReportKillAsync(int, int, CancellationToken)"/>)把请求绑定到某个房间:房间已经换过(或还没到)的请求按 <see cref="OnlineCombatBlock.StaleRoom"/> 拦下,免得换房前后序号错位、报到别的房间的怪上。</item>
    /// <item>清图:服务端只在最后一间清空时发一条空载荷通知(类 0、包号 0x1f),此前没有"按房间清空"的通知(笔记 §5 第 5、6 条)。收到它,结算闸门才打开;
    /// 它到时哪怕还有怪没报或没被确认也照收——服务端的清图判定只看"当前地图里还有没有待清怪"(笔记 §5 第 1 条,待清的含义没读通),
    /// 与本地记的击杀进度没有关系,服务端说了算。</item>
    /// <item>结算:闸门没开(没收到清图通知)时 <see cref="RequestSettlementAsync"/> 一律拦下、不发包——服务端在没清图时收到结算命令不处理且记反作弊计数,所以没有任何"直通"路径,
    /// 起始地图没有怪物时也一样(见下)。发出结算命令后,等通关结果通知(0x22)与通关奖励通知(0x23)按序到齐,到齐就发 <see cref="OnlineCombatEventKind.ReadyToReturnToTown"/>。</item>
    /// <item>起始地图怪为空:没有怪要报,"击杀都已上报"空真;但结算闸门照旧只认服务端的清图通知,不因为没有怪就放行——服务端的清图判定挂在"击杀成功"之后(笔记 §5 第 1、3 条),
    /// 空图没有击杀,这条通知可能永远不来(推断,没有证据说空图会被服务端直接判清),那是调用方的超时要处理的结局,不是状态机该绕过的;
    /// 提前发结算命令只会被服务端当作无效并记一次反作弊计数(笔记 §5 结算链第 1 条:要求通关状态为 1)。</item>
    /// <item>
    /// 换房(r16-multiroom,多房间副本):清图通知只在最后一间才有,所以多房间副本要靠换房命令(类 1、包号 48,<see cref="RequestMoveRoomAsync(OnlineMoveRoomRequest, CancellationToken)"/>)一间一间走。
    /// 本地闸门是客户端的预过滤(放不放行最终仍以服务端为准),按这个次序拦(<see cref="MoveGate(int)"/>):请求绑定的房间不是当前房间(<see cref="OnlineCombatBlock.StaleRoom"/>)、
    /// 已经清图或进入结算(<see cref="OnlineCombatBlock.PhaseClosed"/>)、本房间被拒绝已够次数(<see cref="OnlineCombatBlock.MoveBudgetExhausted"/>)、
    /// 上一条换房命令还在途(<see cref="OnlineCombatBlock.MoveInFlight"/>)、装载握手没完(<see cref="OnlineCombatBlock.LoadPending"/>)、
    /// 当前房间的怪没有全部被服务端确认(<see cref="OnlineCombatBlock.RoomNotCleared"/>,见 <see cref="RoomCleared"/>)。
    /// 成功时服务端不回 48 号包,唯一的肯定回应是新房间的起始地图通知(类 0、包号 29):状态机只在换房命令在途时才认它,
    /// 把对应表整体换成新房间的表、房间序号加一、拒绝额度清零,并产出 <see cref="OnlineCombatEventKind.RoomEntered"/>;
    /// 失败时服务端回类 1、包号 48 的 {u8 0, u8 错误码}(19 已通关、20 当前房间没清、1 目标格越界、18 已在目标格、21 不是相邻格等),
    /// 状态机留在当前房间、什么都不换,只记一次拒绝(<see cref="OnlineCombatEventKind.RoomMoveRefused"/>)。
    /// 没有在途换房命令时来的 29 号通知与 48 号回包,按"不在包集合里"处理(只计数、透传),与只有一张图时的既有行为一致。
    /// 已读到而没解决的缺口:服务端在某些副本(<c>CDungeon</c> 的 +0x89f 字节大于 0)里换房成功时发的是 211 号通知而不是 29 号,布局没读,状态机不认它,
    /// 换房会停在"在途"、由跑器的 <see cref="OnlineDungeonCombatOptions.RoomHandshakeTimeout"/> 收尾。
    /// </item>
    /// <item>
    /// 装载握手:服务端每次成功换房(开局也一样)都起一个装载超时定时器,到点还没报装载完成的成员会被踢出副本。
    /// 所以进了新房间(<see cref="OnlineDungeonCombatOptions.AcknowledgeRoomLoaded"/> 为 true 时)状态机进入 <see cref="OnlineRoomLoadState.ReportDue"/>,
    /// 调用方(跑器自动)用 <see cref="ReportRoomLoadedAsync"/> 发装载完成命令(类 1、包号 40),服务端回装载完成通知(类 0、包号 30,载荷一个 i32、这里不读)后握手完成;
    /// 握手没完之前换房被本地拦下。起始房间缺省不握手(保持只有一张图时线上字节不变),见 <see cref="OnlineDungeonCombatOptions.AcknowledgeStartRoomLoaded"/>。
    /// 没在等装载完成通知时来的 30 号通知按"不在包集合里"处理。
    /// </item>
    /// <item>乱序与重复沿用 <see cref="OnlineDungeonEntry"/> 的口径:来得不是时候的包记为乱序、忽略、不缓存;已处理过的再来记为重复、只认第一次;都只计数不推进。</item>
    /// <item>不在包集合里的帧只计数(<see cref="Passthrough"/>)并原样透传(<see cref="OnFrame"/> 的返回值告诉调用方它没被消费)。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 线程:状态机不加锁,调用方保证同一时刻只有一个线程在调它(<see cref="OnlineDungeonCombatRunner"/> 把击杀与换房请求排进队列、在自己的循环里串行调用)。
    /// 发送类调用只在帧层成功之后才推进状态;发送抛出的异常原样传出,状态不变。
    /// <see cref="Room"/>、<see cref="Monsters"/>、<see cref="Map"/> 可以从别的线程读(换房时整体替换,读到的要么是旧的要么是新的,三者之间不保证同一刻),其余属性是尽力而为的值。
    /// </para>
    /// </summary>
    public sealed class OnlineDungeonCombat
    {
        /// <summary>怪物死亡命令的包号(类 1,CMD 42 DIE_MONSTER)。</summary>
        public const ushort KillPacketId = 42;

        /// <summary>结算命令的包号(类 1,CMD 49 SET_PLAY_RESULT)。</summary>
        public const ushort SettlementPacketId = 49;

        /// <summary>换房命令的包号(类 1,CMD 48 MOVE_MAP;载荷 43 字节,见 <see cref="OnlineCombatPayloads.BuildMoveRoom"/>)。</summary>
        public const ushort MoveRoomPacketId = 48;

        /// <summary>装载完成命令的包号(类 1,CMD 40 FINISH_LOADING;载荷 8 字节,见 <see cref="OnlineCombatPayloads.BuildRoomLoaded"/>)。</summary>
        public const ushort RoomLoadedPacketId = 40;

        /// <summary>击杀确认通知的包号(类 0,0x26 DIE_MONSTER;载荷以 u16 怪编号开头)。</summary>
        public const int KillConfirmNoticeId = 0x26;

        /// <summary>清图通知的包号(类 0,0x1f ENABLE_CLEAR_DUNGEON;空载荷)。</summary>
        public const int ClearNoticeId = 0x1f;

        /// <summary>通关结果通知的包号(类 0,0x22 PLAY_RESULT)。</summary>
        public const int ResultNoticeId = 0x22;

        /// <summary>通关奖励通知的包号(类 0,0x23 CLEAR_DUNGEON_REWARD)。</summary>
        public const int RewardNoticeId = 0x23;

        /// <summary>
        /// 新房间的起始地图通知的包号(类 0,29;换房成功时服务端与开局发起始地图用同一个构造函数,见 <see cref="StartMapParser.PacketId"/>)。
        /// 状态机只在换房命令在途时才认它。
        /// </summary>
        public const int StartMapNoticeId = StartMapParser.PacketId;

        /// <summary>装载完成通知的包号(类 0,30 = 0x1e;载荷一个 i32,状态机不读)。状态机只在等它的时候才认。</summary>
        public const int RoomLoadedNoticeId = 30;

        /// <summary>击杀失败回包的包号(类 1,与击杀命令同号 42 = 0x2a;载荷 {u8 0, u8 错误码, u16 怪编号})。</summary>
        public const int KillReplyPacketId = 0x2a;

        /// <summary>换房失败回包的包号(类 1,与换房命令同号 48;载荷以 {u8 0, u8 错误码} 开头;换房成功没有这个回包)。</summary>
        public const int MoveRoomReplyPacketId = 48;

        private readonly IOnlineCombatPort _port;
        private readonly Func<ushort, byte[]> _killPayload;
        private readonly Func<byte[]> _settlementPayload;
        private readonly Func<OnlineMoveRoomRequest, byte[]> _movePayload;
        private readonly Func<byte[]> _loadedPayload;
        private readonly bool _acknowledgeRoomLoaded;
        private readonly int _maxMoveRefusalsPerRoom;
        private readonly List<OnlineCombatEvent> _events = new List<OnlineCombatEvent>();
        private readonly SortedDictionary<long, int> _passthrough = new SortedDictionary<long, int>();

        private volatile OnlineCombatMonster[] _monsters;
        private volatile StartMapNotice _map;
        private volatile int _room;
        private Dictionary<ushort, int> _localOfServer;
        private int _totalMonsters;
        private OnlineCombatPhase _phase = OnlineCombatPhase.Fighting;
        private bool _haveResult;
        private bool _haveReward;
        private bool _moveInFlight;
        private int _refusalsInRoom;
        private OnlineRoomLoadState _load;
        private PacketClass _frameClass;
        private int _frameId = -1;
        private int _frameServerId = -1;

        /// <summary>
        /// 用起始地图快照建对应表并开始。
        /// </summary>
        /// <param name="map">进副本拿到的起始地图(<see cref="OnlineDungeonResult.StartMap"/>)。</param>
        /// <param name="port">发送口。</param>
        /// <param name="options">可调项;null 全用缺省。</param>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="OnlineDungeonCombatOptions.MaxMoveRefusalsPerRoom"/> 小于 1。</exception>
        public OnlineDungeonCombat(StartMapNotice map, IOnlineCombatPort port, OnlineDungeonCombatOptions options = null)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _killPayload = options?.KillPayload ?? (mobId => OnlineCombatPayloads.BuildKill(mobId));
            _settlementPayload = options?.SettlementPayload ?? OnlineCombatPayloads.BuildSettlement;
            _movePayload = options?.MovePayload
                ?? (request => OnlineCombatPayloads.BuildMoveRoom(request.TargetX, request.TargetY, request.PositionX, request.PositionY, request.Layered));
            _loadedPayload = options?.LoadedPayload ?? (() => OnlineCombatPayloads.BuildRoomLoaded());
            _acknowledgeRoomLoaded = options?.AcknowledgeRoomLoaded ?? true;
            _maxMoveRefusalsPerRoom = options?.MaxMoveRefusalsPerRoom ?? 3;
            if (_maxMoveRefusalsPerRoom < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMoveRefusalsPerRoom 至少为 1");
            }

            BuildTable(map, 0, out OnlineCombatMonster[] monsters, out Dictionary<ushort, int> localOfServer);
            _monsters = monsters;
            _localOfServer = localOfServer;
            _map = map;
            _totalMonsters = monsters.Length;
            if (_acknowledgeRoomLoaded && options != null && options.AcknowledgeStartRoomLoaded)
            {
                _load = OnlineRoomLoadState.ReportDue;
            }
            Raise(OnlineCombatEventKind.MonstersSpawned, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
        }

        /// <summary>当前阶段。</summary>
        public OnlineCombatPhase Phase => _phase;

        /// <summary>当前房间的对应表(按本地编号排列,线上顺序);换房成功后整体换成新房间的表,旧表里的怪对象不再更新。</summary>
        public IReadOnlyList<OnlineCombatMonster> Monsters => _monsters;

        /// <summary>当前房间的起始地图通知(起始房间是构造时给的那份;换房成功后是新房间的那份)。宿主据它(地图编号、格子、种子)装载本地房间。</summary>
        public StartMapNotice Map => _map;

        /// <summary>当前房间序号:起始房间是 0,每接受一条新房间的起始地图通知加 1。</summary>
        public int Room => _room;

        /// <summary>到此刻为止产出的全部事件(只增不减,按产出顺序)。</summary>
        public IReadOnlyList<OnlineCombatEvent> Events => _events;

        /// <summary>到此刻为止进过的各房间的怪数合计(只有起始地图时就是起始地图的怪数)。</summary>
        public int Total => _totalMonsters;

        /// <summary>当前房间有没有怪。</summary>
        public bool HasMonsters => _monsters.Length > 0;

        /// <summary>服务端编号与同一张表里前面某条记录重复的怪数(各房间合计;正常数据为 0)。</summary>
        public int DuplicateNumbers { get; private set; }

        /// <summary>已发出击杀命令的怪数(各房间合计)。</summary>
        public int Sent { get; private set; }

        /// <summary>已被服务端确认的怪数(各房间合计)。</summary>
        public int Confirmed { get; private set; }

        /// <summary>服务端回了击杀失败的怪数(各房间合计)。</summary>
        public int Refused { get; private set; }

        /// <summary>被本地拦下(或没能发出)的发送次数,击杀、结算、换房与装载完成合计。</summary>
        public int Blocked { get; private set; }

        /// <summary>因乱序被忽略的帧数。</summary>
        public int OutOfOrder { get; private set; }

        /// <summary>因重复被忽略的帧数。</summary>
        public int Duplicates { get; private set; }

        /// <summary>带怪编号但编号不在当前房间表里而被忽略的帧数。</summary>
        public int UnknownNumbers { get; private set; }

        /// <summary>包号认得但载荷读不下来而被忽略的帧数。</summary>
        public int Malformed { get; private set; }

        /// <summary>不在包集合里、只计数透传的帧数。</summary>
        public int PassthroughCount { get; private set; }

        /// <summary>已发出的换房命令数。</summary>
        public int MovesSent { get; private set; }

        /// <summary>服务端回了换房失败的次数(各房间合计)。</summary>
        public int MoveRefusals { get; private set; }

        /// <summary>已发出的装载完成命令数。</summary>
        public int LoadReports { get; private set; }

        /// <summary>当前房间里换房被服务端拒绝的次数(换房成功进入新房间后清零;达到 <see cref="OnlineDungeonCombatOptions.MaxMoveRefusalsPerRoom"/> 就不再替这个房间发换房命令)。</summary>
        public int RoomRefusals => _refusalsInRoom;

        /// <summary>换房命令已发出、还没等到起始地图通知或失败回包。</summary>
        public bool MoveInFlight => _moveInFlight;

        /// <summary>当前房间的装载握手进行到哪一步。</summary>
        public OnlineRoomLoadState LoadState => _load;

        /// <summary>装载握手还没完(命令没发出,或发出了通知还没到):此时换房被 <see cref="OnlineCombatBlock.LoadPending"/> 拦着。</summary>
        public bool LoadPending => _load != OnlineRoomLoadState.None;

        /// <summary>透传的帧按(类、包号)分的计数,按类、包号升序。</summary>
        public IReadOnlyList<OnlineCombatPacketCount> Passthrough
        {
            get
            {
                var list = new List<OnlineCombatPacketCount>(_passthrough.Count);
                foreach (KeyValuePair<long, int> pair in _passthrough)
                {
                    list.Add(new OnlineCombatPacketCount((PacketClass)(pair.Key >> 32), (int)(pair.Key & 0xFFFFFFFFL), pair.Value));
                }
                return list;
            }
        }

        /// <summary>当前房间所有能独立上报的怪都已报过击杀(没有怪时空真)。</summary>
        public bool AllKillsReported
        {
            get
            {
                foreach (OnlineCombatMonster m in _monsters)
                {
                    if (!m.DuplicateNumber && m.State == OnlineCombatMonsterState.Alive)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>已发出击杀却还没等到确认或失败回包的怪数(各房间合计)。</summary>
        public int PendingConfirmations => Sent - Confirmed - Refused;

        /// <summary>
        /// 当前房间的怪是不是已经全部被服务端确认击杀(换房闸门的"清图"一项):每只能独立上报的怪(编号重复、没有独立编号的不算)都处在
        /// <see cref="OnlineCombatMonsterState.Confirmed"/>。没有怪时空真。只认"全部确认"这条正面证据:报了没确认、被服务端拒了(<see cref="OnlineCombatMonsterState.Refused"/>)的都不算。
        /// 这是客户端的预过滤:放行不等于服务端也认(服务端怪表里还有客户端不知道的怪时会回错误码 20),拦下也不等于服务端不认。
        /// </summary>
        public bool RoomCleared
        {
            get
            {
                foreach (OnlineCombatMonster m in _monsters)
                {
                    if (!m.DuplicateNumber && m.State != OnlineCombatMonsterState.Confirmed)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// 一只本地怪死了:按对应表把它的服务端编号报给服务端(类 1、包号 42)。
        /// 本地编号不在表里、这只怪已报过、编号重复没有独立编号、结算已发出,都在本地拦下不发包(返回 <see cref="OnlineCombatSendOutcome.Blocked"/>);
        /// 帧层失败返回 <see cref="OnlineCombatSendOutcome.FrameFailed"/>,状态不变(可以重试)。
        /// 查的是调用这一刻的当前房间的表;多房间时换房前后序号会错位,请用 <see cref="ReportKillAsync(int, int, CancellationToken)"/> 绑定房间。
        /// </summary>
        /// <param name="localId">本地编号(= 当前房间起始地图怪记录的序号)。</param>
        public async Task<OnlineCombatSendResult> ReportKillAsync(int localId, CancellationToken cancellationToken = default)
        {
            OnlineCombatMonster[] monsters = _monsters;
            if (localId < 0 || localId >= monsters.Length)
            {
                return BlockKill(localId, OnlineCombatBlock.UnknownLocalId);
            }
            OnlineCombatMonster monster = monsters[localId];
            if (monster.DuplicateNumber)
            {
                return BlockKill(localId, OnlineCombatBlock.NoServerNumber);
            }
            if (_phase >= OnlineCombatPhase.Settling)
            {
                return BlockKill(localId, OnlineCombatBlock.PhaseClosed);
            }
            if (monster.State != OnlineCombatMonsterState.Alive)
            {
                return BlockKill(localId, OnlineCombatBlock.AlreadyReported);
            }

            FrameStatus status = await _port.SendAsync(PacketClass.Cmd, KillPacketId, _killPayload(monster.ServerId), cancellationToken).ConfigureAwait(false);
            if (status != FrameStatus.Ok)
            {
                Blocked++;
                Raise(OnlineCombatEventKind.KillBlocked, localId, OnlineCombatBlock.FrameFailed, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
                return new OnlineCombatSendResult(OnlineCombatSendOutcome.FrameFailed, OnlineCombatBlock.FrameFailed, status);
            }

            monster.State = OnlineCombatMonsterState.KillSent;
            Sent++;
            Raise(OnlineCombatEventKind.KillSent, localId, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Sent, OnlineCombatBlock.None, FrameStatus.Ok);
        }

        /// <summary>
        /// 一只本地怪死了,请求绑定在序号为 <paramref name="room"/> 的房间:不是当前房间(换房已经发生,或那个房间还没到)就在本地拦下
        /// (<see cref="OnlineCombatBlock.StaleRoom"/>,事件里没有服务端编号),否则同 <see cref="ReportKillAsync(int, CancellationToken)"/>。
        /// </summary>
        /// <param name="room">请求针对的房间序号(<see cref="Room"/>)。</param>
        /// <param name="localId">该房间对应表里的本地编号。</param>
        public Task<OnlineCombatSendResult> ReportKillAsync(int room, int localId, CancellationToken cancellationToken = default)
        {
            if (room != _room)
            {
                return Task.FromResult(BlockKill(localId, OnlineCombatBlock.StaleRoom, false));
            }
            return ReportKillAsync(localId, cancellationToken);
        }

        /// <summary>
        /// 发结算命令(类 1、包号 49)。只有收到清图通知(<see cref="Phase"/> 为 <see cref="OnlineCombatPhase.Cleared"/>)才发;
        /// 其余阶段本地拦下不发包:还在打是 <see cref="OnlineCombatBlock.NotCleared"/>,已经发过是 <see cref="OnlineCombatBlock.SettlementAlreadySent"/>。
        /// </summary>
        public async Task<OnlineCombatSendResult> RequestSettlementAsync(CancellationToken cancellationToken = default)
        {
            if (_phase == OnlineCombatPhase.Fighting)
            {
                return BlockSettlement(OnlineCombatBlock.NotCleared);
            }
            if (_phase != OnlineCombatPhase.Cleared)
            {
                return BlockSettlement(OnlineCombatBlock.SettlementAlreadySent);
            }

            FrameStatus status = await _port.SendAsync(PacketClass.Cmd, SettlementPacketId, _settlementPayload(), cancellationToken).ConfigureAwait(false);
            if (status != FrameStatus.Ok)
            {
                Blocked++;
                Raise(OnlineCombatEventKind.SettlementBlocked, -1, OnlineCombatBlock.FrameFailed, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
                return new OnlineCombatSendResult(OnlineCombatSendOutcome.FrameFailed, OnlineCombatBlock.FrameFailed, status);
            }

            _phase = OnlineCombatPhase.Settling;
            Raise(OnlineCombatEventKind.SettlementSent, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Sent, OnlineCombatBlock.None, FrameStatus.Ok);
        }

        /// <summary>
        /// 此刻往序号为 <paramref name="room"/> 的房间发换房命令,会被本地闸门拦在哪一道上;<see cref="OnlineCombatBlock.None"/> 表示放行。
        /// 只读判断,不产出事件、不计数。次序:房间不符 → 已清图或结算 → 拒绝额度用完 → 换房命令在途 → 装载握手没完 → 当前房间没清。
        /// 前三道是"这个请求再等也没用"(永久),后三道等服务端的回应或宿主的击杀就会放开(暂时)——跑器据此决定丢掉请求还是留着下一轮再试。
        /// </summary>
        /// <param name="room">请求针对的房间序号。</param>
        public OnlineCombatBlock MoveGate(int room)
        {
            if (room != _room)
            {
                return OnlineCombatBlock.StaleRoom;
            }
            if (_phase != OnlineCombatPhase.Fighting)
            {
                return OnlineCombatBlock.PhaseClosed;
            }
            if (_refusalsInRoom >= _maxMoveRefusalsPerRoom)
            {
                return OnlineCombatBlock.MoveBudgetExhausted;
            }
            if (_moveInFlight)
            {
                return OnlineCombatBlock.MoveInFlight;
            }
            if (_load != OnlineRoomLoadState.None)
            {
                return OnlineCombatBlock.LoadPending;
            }
            if (!RoomCleared)
            {
                return OnlineCombatBlock.RoomNotCleared;
            }
            return OnlineCombatBlock.None;
        }

        /// <summary>对当前房间的 <see cref="MoveGate(int)"/>。</summary>
        public OnlineCombatBlock MoveGate()
        {
            return MoveGate(_room);
        }

        /// <summary>
        /// 发换房命令(类 1、包号 48),针对调用这一刻的当前房间。闸门见 <see cref="MoveGate(int)"/>;被拦下返回 <see cref="OnlineCombatSendOutcome.Blocked"/>、不发包、状态不变。
        /// 发出后状态机进入"换房在途":等新房间的起始地图通知(换表,<see cref="OnlineCombatEventKind.RoomEntered"/>)或失败回包(留在原房间,<see cref="OnlineCombatEventKind.RoomMoveRefused"/>)。
        /// 帧层失败返回 <see cref="OnlineCombatSendOutcome.FrameFailed"/>,状态不变。
        /// </summary>
        /// <param name="request">换房请求(目标格、位置、分层标志)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">自定的换房载荷工厂返回 null 或长度不是 <see cref="OnlineCombatPayloads.MoveRoomLength"/>(此时没有发包、状态不变)。</exception>
        public Task<OnlineCombatSendResult> RequestMoveRoomAsync(OnlineMoveRoomRequest request, CancellationToken cancellationToken = default)
        {
            return RequestMoveRoomAsync(_room, request, cancellationToken);
        }

        /// <summary>
        /// 同 <see cref="RequestMoveRoomAsync(OnlineMoveRoomRequest, CancellationToken)"/>,请求绑定在序号为 <paramref name="room"/> 的房间:不是当前房间就按
        /// <see cref="OnlineCombatBlock.StaleRoom"/> 拦下(请求是在换房之前排的,不能被用到换房之后的房间上)。
        /// </summary>
        /// <param name="room">请求针对的房间序号(<see cref="Room"/>)。</param>
        /// <param name="request">换房请求。</param>
        public async Task<OnlineCombatSendResult> RequestMoveRoomAsync(int room, OnlineMoveRoomRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            OnlineCombatBlock gate = MoveGate(room);
            if (gate != OnlineCombatBlock.None)
            {
                return BlockMove(gate);
            }

            byte[] payload = _movePayload(request);
            if (payload == null || payload.Length != OnlineCombatPayloads.MoveRoomLength)
            {
                throw new InvalidOperationException("换房载荷必须恰好 " + OnlineCombatPayloads.MoveRoomLength + " 字节(服务端读不满就断线)");
            }

            FrameStatus status = await _port.SendAsync(PacketClass.Cmd, MoveRoomPacketId, payload, cancellationToken).ConfigureAwait(false);
            if (status != FrameStatus.Ok)
            {
                Blocked++;
                Raise(OnlineCombatEventKind.RoomMoveBlocked, -1, OnlineCombatBlock.FrameFailed, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
                return new OnlineCombatSendResult(OnlineCombatSendOutcome.FrameFailed, OnlineCombatBlock.FrameFailed, status);
            }

            _moveInFlight = true;
            MovesSent++;
            Raise(OnlineCombatEventKind.RoomMoveSent, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Sent, OnlineCombatBlock.None, FrameStatus.Ok);
        }

        /// <summary>
        /// 发装载完成命令(类 1、包号 40)。只有 <see cref="LoadState"/> 为 <see cref="OnlineRoomLoadState.ReportDue"/>(进了房间、命令还没发)才发;
        /// 其余情形本地拦下不发包(<see cref="OnlineCombatBlock.NoLoadPending"/>):还没换过房、已经报过、握手已完成、或没开握手。
        /// 发出后等装载完成通知(<see cref="OnlineCombatEventKind.RoomLoaded"/>)。帧层失败返回 <see cref="OnlineCombatSendOutcome.FrameFailed"/>,状态不变(可以重试)。
        /// </summary>
        /// <exception cref="InvalidOperationException">自定的载荷工厂返回 null 或长度不是 <see cref="OnlineCombatPayloads.RoomLoadedLength"/>(此时没有发包、状态不变)。</exception>
        public async Task<OnlineCombatSendResult> ReportRoomLoadedAsync(CancellationToken cancellationToken = default)
        {
            if (_load != OnlineRoomLoadState.ReportDue)
            {
                return BlockLoaded(OnlineCombatBlock.NoLoadPending);
            }

            byte[] payload = _loadedPayload();
            if (payload == null || payload.Length != OnlineCombatPayloads.RoomLoadedLength)
            {
                throw new InvalidOperationException("装载完成载荷必须恰好 " + OnlineCombatPayloads.RoomLoadedLength + " 字节(服务端读不满就断线)");
            }

            FrameStatus status = await _port.SendAsync(PacketClass.Cmd, RoomLoadedPacketId, payload, cancellationToken).ConfigureAwait(false);
            if (status != FrameStatus.Ok)
            {
                Blocked++;
                Raise(OnlineCombatEventKind.RoomLoadedBlocked, -1, OnlineCombatBlock.FrameFailed, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
                return new OnlineCombatSendResult(OnlineCombatSendOutcome.FrameFailed, OnlineCombatBlock.FrameFailed, status);
            }

            _load = OnlineRoomLoadState.AwaitingNotice;
            LoadReports++;
            Raise(OnlineCombatEventKind.RoomLoadedSent, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Sent, OnlineCombatBlock.None, FrameStatus.Ok);
        }

        /// <summary>
        /// 收到服务端的一帧(已解出类、包号与载荷;信封里的内层帧也走这里)。载荷末尾的会话加密补零不影响判断。
        /// 返回这一帧被怎样对待:<see cref="OnlineCombatFrameVerdict.Passthrough"/> 表示它不在状态机的包集合里(只计了数,调用方自己处理)。
        /// 起始地图通知(29)、装载完成通知(30)与换房失败回包(类 1、48)只在状态机正等着它们的时候才算在包集合里,否则同样透传。
        /// </summary>
        public OnlineCombatFrameVerdict OnFrame(PacketClass packetClass, int packetId, byte[] payload)
        {
            byte[] body = payload ?? new byte[0];
            _frameClass = packetClass;
            _frameId = packetId;
            _frameServerId = -1;

            if (packetClass == PacketClass.Noti)
            {
                switch (packetId)
                {
                    case KillConfirmNoticeId:
                        return OnKillConfirm(body);
                    case ClearNoticeId:
                        return OnClear();
                    case ResultNoticeId:
                        return OnSettlementNotice(OnlineCombatSettlementPart.Result);
                    case RewardNoticeId:
                        return OnSettlementNotice(OnlineCombatSettlementPart.Reward);
                    case StartMapNoticeId:
                        if (_moveInFlight)
                        {
                            return OnStartMap(body);
                        }
                        break;
                    case RoomLoadedNoticeId:
                        if (_load == OnlineRoomLoadState.AwaitingNotice)
                        {
                            return OnRoomLoaded();
                        }
                        break;
                }
            }
            else if (packetClass == PacketClass.Cmd && packetId == KillReplyPacketId)
            {
                if (OnlineCombatPayloads.TryReadKillRefusal(body, out byte code, out ushort mobId))
                {
                    _frameServerId = mobId;
                    return OnKillRefusal(mobId);
                }
                if (body.Length > 0 && body[0] == 0)
                {
                    return Ignore(OnlineCombatFrameVerdict.Malformed, -1);      // 失败形状却读不下来
                }
                // 成功形状的回包:客户端也直接忽略,这里当作无关帧透传
            }
            else if (packetClass == PacketClass.Cmd && packetId == MoveRoomReplyPacketId && _moveInFlight)
            {
                if (OnlineCombatPayloads.TryReadMoveRoomRefusal(body, out byte refusal))
                {
                    return OnMoveRefusal(refusal);
                }
                if (body.Length > 0 && body[0] == 0)
                {
                    return Ignore(OnlineCombatFrameVerdict.Malformed, -1);      // 失败形状却读不下来
                }
                // 成功形状:服务端换房成功没有 48 号回包(成功的回应是起始地图通知),当作无关帧透传
            }

            PassthroughCount++;
            long key = ((long)(byte)packetClass << 32) | (uint)packetId;
            _passthrough.TryGetValue(key, out int seen);
            _passthrough[key] = seen + 1;
            return OnlineCombatFrameVerdict.Passthrough;
        }

        private OnlineCombatFrameVerdict OnKillConfirm(byte[] body)
        {
            if (!OnlineCombatPayloads.TryReadKillConfirm(body, out ushort mobId))
            {
                return Ignore(OnlineCombatFrameVerdict.Malformed, -1);
            }
            _frameServerId = mobId;
            if (!_localOfServer.TryGetValue(mobId, out int local))
            {
                return Ignore(OnlineCombatFrameVerdict.UnknownNumber, -1);
            }

            OnlineCombatMonster monster = _monsters[local];
            switch (monster.State)
            {
                case OnlineCombatMonsterState.KillSent:
                    monster.State = OnlineCombatMonsterState.Confirmed;
                    Confirmed++;
                    Raise(OnlineCombatEventKind.KillConfirmed, local, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None);
                    return OnlineCombatFrameVerdict.Accepted;
                case OnlineCombatMonsterState.Confirmed:
                    return Ignore(OnlineCombatFrameVerdict.Duplicate, local);
                default:
                    // 还没报过这只怪的击杀(或报了却被服务端拒了)就来了确认:乱序,忽略,不缓存
                    return Ignore(OnlineCombatFrameVerdict.OutOfOrder, local);
            }
        }

        private OnlineCombatFrameVerdict OnKillRefusal(ushort mobId)
        {
            if (!_localOfServer.TryGetValue(mobId, out int local))
            {
                return Ignore(OnlineCombatFrameVerdict.UnknownNumber, -1);
            }

            OnlineCombatMonster monster = _monsters[local];
            switch (monster.State)
            {
                case OnlineCombatMonsterState.KillSent:
                    monster.State = OnlineCombatMonsterState.Refused;
                    Refused++;
                    Raise(OnlineCombatEventKind.KillRefused, local, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None);
                    return OnlineCombatFrameVerdict.Accepted;
                case OnlineCombatMonsterState.Alive:
                    return Ignore(OnlineCombatFrameVerdict.OutOfOrder, local);
                default:
                    return Ignore(OnlineCombatFrameVerdict.Duplicate, local);
            }
        }

        private OnlineCombatFrameVerdict OnClear()
        {
            if (_phase != OnlineCombatPhase.Fighting)
            {
                return Ignore(OnlineCombatFrameVerdict.Duplicate, -1);
            }
            _phase = OnlineCombatPhase.Cleared;
            Raise(OnlineCombatEventKind.MapCleared, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None);
            return OnlineCombatFrameVerdict.Accepted;
        }

        // 换房命令在途时来的起始地图通知:读得下来就把对应表整体换成新房间的表(清图或结算之后来的算乱序,不换)
        private OnlineCombatFrameVerdict OnStartMap(byte[] body)
        {
            if (_phase != OnlineCombatPhase.Fighting)
            {
                return Ignore(OnlineCombatFrameVerdict.OutOfOrder, -1);
            }
            if (!StartMapParser.TryParse(body, out StartMapNotice map))
            {
                return Ignore(OnlineCombatFrameVerdict.Malformed, -1);
            }
            EnterRoom(map);
            return OnlineCombatFrameVerdict.Accepted;
        }

        // 换房命令在途时来的失败回包:留在当前房间,什么都不换,只记一次拒绝
        private OnlineCombatFrameVerdict OnMoveRefusal(byte code)
        {
            _moveInFlight = false;
            _refusalsInRoom++;
            MoveRefusals++;
            Raise(OnlineCombatEventKind.RoomMoveRefused, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None, code);
            return OnlineCombatFrameVerdict.Accepted;
        }

        // 等着装载完成通知时来的通知:这个房间的握手完成
        private OnlineCombatFrameVerdict OnRoomLoaded()
        {
            _load = OnlineRoomLoadState.None;
            Raise(OnlineCombatEventKind.RoomLoaded, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None);
            return OnlineCombatFrameVerdict.Accepted;
        }

        private OnlineCombatFrameVerdict OnSettlementNotice(OnlineCombatSettlementPart part)
        {
            if (_phase < OnlineCombatPhase.Settling)
            {
                // 结算命令还没发就来了结算通知:乱序,忽略,不缓存
                return Ignore(OnlineCombatFrameVerdict.OutOfOrder, -1);
            }
            bool already = part == OnlineCombatSettlementPart.Result ? _haveResult : _haveReward;
            if (already)
            {
                return Ignore(OnlineCombatFrameVerdict.Duplicate, -1);
            }
            if (part == OnlineCombatSettlementPart.Reward && !_haveResult)
            {
                // 服务端先发结果再发奖励:奖励先到是乱序,忽略,不缓存
                return Ignore(OnlineCombatFrameVerdict.OutOfOrder, -1);
            }

            if (part == OnlineCombatSettlementPart.Result)
            {
                _haveResult = true;
            }
            else
            {
                _haveReward = true;
            }
            Raise(OnlineCombatEventKind.SettlementArrived, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, part);
            if (_haveResult && _haveReward)
            {
                _phase = OnlineCombatPhase.Settled;
                Raise(OnlineCombatEventKind.ReadyToReturnToTown, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None);
            }
            return OnlineCombatFrameVerdict.Accepted;
        }

        // 用一份起始地图建一个房间的对应表:本地编号 = 记录顺序号,服务端编号 = 记录第 3 项(笔记 §4.4);重复编号只有第一条进查找表,其余记入 DuplicateNumbers
        private void BuildTable(StartMapNotice map, int room, out OnlineCombatMonster[] monsters, out Dictionary<ushort, int> localOfServer)
        {
            IReadOnlyList<StartMapMonsterRecord> records = map.Monsters;
            monsters = new OnlineCombatMonster[records.Count];
            localOfServer = new Dictionary<ushort, int>();
            for (int i = 0; i < monsters.Length; i++)
            {
                ushort number = records[i].C;          // 服务端怪编号 = 记录第 3 项(笔记 §4.4)
                bool duplicate = localOfServer.ContainsKey(number);
                if (!duplicate)
                {
                    localOfServer[number] = i;
                }
                else
                {
                    DuplicateNumbers++;
                }
                monsters[i] = new OnlineCombatMonster(i, records[i], number, duplicate, room);
            }
        }

        // 换房成功:整体换表、换房间、清掉在途与拒绝额度,装载握手重新开始
        private void EnterRoom(StartMapNotice map)
        {
            int room = _room + 1;
            BuildTable(map, room, out OnlineCombatMonster[] monsters, out Dictionary<ushort, int> localOfServer);
            _localOfServer = localOfServer;
            _monsters = monsters;
            _map = map;
            _totalMonsters += monsters.Length;
            _room = room;
            _moveInFlight = false;
            _refusalsInRoom = 0;
            _load = _acknowledgeRoomLoaded ? OnlineRoomLoadState.ReportDue : OnlineRoomLoadState.None;
            Raise(OnlineCombatEventKind.RoomEntered, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Accepted, OnlineCombatSettlementPart.None, -1, map.TrailingByteCount);
        }

        private OnlineCombatSendResult BlockKill(int localId, OnlineCombatBlock block, bool lookupServerId = true)
        {
            Blocked++;
            Raise(OnlineCombatEventKind.KillBlocked, localId, block, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None, -1, -1, lookupServerId);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Blocked, block, FrameStatus.Ok);
        }

        private OnlineCombatSendResult BlockSettlement(OnlineCombatBlock block)
        {
            Blocked++;
            Raise(OnlineCombatEventKind.SettlementBlocked, -1, block, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Blocked, block, FrameStatus.Ok);
        }

        private OnlineCombatSendResult BlockMove(OnlineCombatBlock block)
        {
            Blocked++;
            Raise(OnlineCombatEventKind.RoomMoveBlocked, -1, block, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Blocked, block, FrameStatus.Ok);
        }

        private OnlineCombatSendResult BlockLoaded(OnlineCombatBlock block)
        {
            Blocked++;
            Raise(OnlineCombatEventKind.RoomLoadedBlocked, -1, block, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Blocked, block, FrameStatus.Ok);
        }

        // 忽略一帧:记计数、记一条事件
        private OnlineCombatFrameVerdict Ignore(OnlineCombatFrameVerdict verdict, int localId)
        {
            switch (verdict)
            {
                case OnlineCombatFrameVerdict.OutOfOrder:
                    OutOfOrder++;
                    break;
                case OnlineCombatFrameVerdict.Duplicate:
                    Duplicates++;
                    break;
                case OnlineCombatFrameVerdict.UnknownNumber:
                    UnknownNumbers++;
                    break;
                case OnlineCombatFrameVerdict.Malformed:
                    Malformed++;
                    break;
            }
            Raise(OnlineCombatEventKind.FrameIgnored, localId, OnlineCombatBlock.None, verdict, OnlineCombatSettlementPart.None);
            return verdict;
        }

        private void Raise(OnlineCombatEventKind kind, int localId, OnlineCombatBlock block, OnlineCombatFrameVerdict verdict, OnlineCombatSettlementPart part,
            int replyCode = -1, int trailingBytes = -1, bool lookupServerId = true)
        {
            bool framed = kind == OnlineCombatEventKind.FrameIgnored;
            OnlineCombatMonster[] monsters = _monsters;
            int serverId = framed ? _frameServerId : lookupServerId && localId >= 0 && localId < monsters.Length ? monsters[localId].ServerId : -1;
            _events.Add(new OnlineCombatEvent(kind, localId, serverId, block, verdict, part,
                framed ? _frameClass : PacketClass.Noti, framed ? _frameId : -1, _totalMonsters, Sent, Confirmed, _room, monsters.Length, replyCode, trailingBytes));
        }
    }
}
