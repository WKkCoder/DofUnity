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
        /// <summary>起始地图已建表,在打:收击杀确认、等清图通知。结算命令的闸门关着。</summary>
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

        /// <summary>结算命令已发出,不再报击杀。</summary>
        PhaseClosed,

        /// <summary>结算:还没有收到清图通知,闸门没开,不发包(服务端没清图就收到结算会记反作弊计数)。</summary>
        NotCleared,

        /// <summary>结算:结算命令已经发过,不再发。</summary>
        SettlementAlreadySent,

        /// <summary>命令没发出去:帧层编码失败(状态机的状态没有变化)。</summary>
        FrameFailed,
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
    }

    /// <summary>
    /// 状态机产出的一条结构化事件(只读)。<see cref="Total"/>、<see cref="Sent"/>、<see cref="Confirmed"/> 是事件发生那一刻的计数快照,
    /// 日志只靠它们与枚举名,不碰载荷;<see cref="ServerId"/> 是服务端怪编号(来自起始地图记录),只给程序用,日志不写。
    /// </summary>
    public sealed class OnlineCombatEvent
    {
        internal OnlineCombatEvent(OnlineCombatEventKind kind, int localId, int serverId, OnlineCombatBlock block,
            OnlineCombatFrameVerdict verdict, OnlineCombatSettlementPart part, PacketClass packetClass, int packetId,
            int total, int sent, int confirmed)
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
        }

        public OnlineCombatEventKind Kind { get; }

        /// <summary>本地编号(按起始地图记录顺序的序号);事件与某只怪无关时为 -1。</summary>
        public int LocalId { get; }

        /// <summary>服务端怪编号;事件与某只怪无关或没有编号时为 -1。</summary>
        public int ServerId { get; }

        /// <summary>被拦的原因(仅 <see cref="OnlineCombatEventKind.KillBlocked"/>、<see cref="OnlineCombatEventKind.SettlementBlocked"/>)。</summary>
        public OnlineCombatBlock Block { get; }

        /// <summary>忽略的原因(仅 <see cref="OnlineCombatEventKind.FrameIgnored"/>)。</summary>
        public OnlineCombatFrameVerdict Verdict { get; }

        /// <summary>哪一段结算通知(仅 <see cref="OnlineCombatEventKind.SettlementArrived"/>)。</summary>
        public OnlineCombatSettlementPart Part { get; }

        /// <summary>被忽略的那一帧的包类(仅 <see cref="OnlineCombatEventKind.FrameIgnored"/>)。</summary>
        public PacketClass PacketClass { get; }

        /// <summary>被忽略的那一帧的包号(仅 <see cref="OnlineCombatEventKind.FrameIgnored"/>,其余为 -1)。</summary>
        public int PacketId { get; }

        /// <summary>起始地图的怪数。</summary>
        public int Total { get; }

        /// <summary>此刻已发出击杀命令的怪数。</summary>
        public int Sent { get; }

        /// <summary>此刻已被服务端确认的怪数。</summary>
        public int Confirmed { get; }
    }

    /// <summary>对应表里的一只怪:本地编号 ↔ 服务端编号,以及它的击杀进度。</summary>
    public sealed class OnlineCombatMonster
    {
        internal OnlineCombatMonster(int localId, StartMapMonsterRecord record, ushort serverId, bool duplicateNumber)
        {
            LocalId = localId;
            Record = record;
            ServerId = serverId;
            DuplicateNumber = duplicateNumber;
        }

        /// <summary>本地编号:这条记录在起始地图怪记录里的序号(从 0 起,线上顺序)。</summary>
        public int LocalId { get; }

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

        /// <summary>跑器每产出一条事件就在它的循环线程上回调一次;null 不回调。抛出的异常被吞掉。</summary>
        public Action<OnlineCombatEvent> OnEvent { get; set; }
    }

    /// <summary>
    /// 联网副本战斗的薄状态机:输入是进副本拿到的起始地图快照(<see cref="StartMapNotice"/>)与一个发送口,产出一串结构化事件(<see cref="OnlineCombatEvent"/>)。
    /// 它只管"本地击杀 → 服务端认账 → 清图 → 结算 → 可以回城"这一条链,不碰战斗本身(怪怎么生成、怎么打死是宿主与离线战斗的事),
    /// 也不读帧(收帧由 <see cref="OnlineDungeonCombatRunner"/> 或调用方做,逐帧交给 <see cref="OnFrame"/>)。
    /// <para>
    /// 对应表:本地编号 = 起始地图怪记录在线上的顺序号(从 0 起),服务端编号 = 同一条记录的第 3 项(<see cref="StartMapMonsterRecord.C"/>)。
    /// 依据是既有定案(combat 线静态读码笔记的 §4.4,下面简称"笔记",出处说明见 <see cref="OnlineCombatPayloads"/>):服务端每个房间取一个 [0, 60000) 的随机基数、怪与物品共用计数器逐个加一给每只怪发键,
    /// 键经起始地图通知的怪记录第 3 项下发(推断,判据:上报本地小编号时服务端会回击杀失败);客户端本地计数器没有任何网络灌入通道,
    /// 所以本地编号灌不进服务端,只能查表。表错位一只就会把别的怪的键报上去,测试逐只核对。
    /// </para>
    /// <para>
    /// 链的规则(服务端侧依据 笔记 §4.5–§5):
    /// <list type="bullet">
    /// <item>击杀:每只怪只报一次(<see cref="ReportKillAsync"/>);本地编号不在表里、已报过、结算已发出的一律在本地拦下不发包;服务端按键在当前地图的怪表里找,找到即删除并广播确认(类 0、包号 0x26),找不到回失败(类 1、包号 0x2a)。</item>
    /// <item>清图:服务端只在最后一间清空时发一条空载荷通知(类 0、包号 0x1f),此前没有"按房间清空"的通知(笔记 §5 第 5、6 条)。收到它,结算闸门才打开;
    /// 它到时哪怕还有怪没报或没被确认也照收——服务端的清图判定只看"当前地图里还有没有待清怪"(笔记 §5 第 1 条,待清的含义没读通),
    /// 与本地记的击杀进度没有关系,服务端说了算。</item>
    /// <item>结算:闸门没开(没收到清图通知)时 <see cref="RequestSettlementAsync"/> 一律拦下、不发包——服务端在没清图时收到结算命令不处理且记反作弊计数,所以没有任何"直通"路径,
    /// 起始地图没有怪物时也一样(见下)。发出结算命令后,等通关结果通知(0x22)与通关奖励通知(0x23)按序到齐,到齐就发 <see cref="OnlineCombatEventKind.ReadyToReturnToTown"/>。</item>
    /// <item>起始地图怪为空:没有怪要报,"击杀都已上报"空真;但闸门照旧只认服务端的清图通知,不因为没有怪就放行——服务端的清图判定挂在"击杀成功"之后(笔记 §5 第 1、3 条),
    /// 空图没有击杀,这条通知可能永远不来(推断,没有证据说空图会被服务端直接判清),那是调用方的超时要处理的结局,不是状态机该绕过的;
    /// 提前发结算命令只会被服务端当作无效并记一次反作弊计数(笔记 §5 结算链第 1 条:要求通关状态为 1)。</item>
    /// <item>范围只有起始地图这一张图、只管单人:换房(移动命令加新的起始地图通知)的握手这一版没有做。服务端只在最后一间清空时才发清图通知(笔记 §5 第 6 条),
    /// 所以多房间副本里起始房清完之后状态机会一直停在 <see cref="OnlineCombatPhase.Fighting"/>,由调用方的整段超时收尾(<see cref="OnlineDungeonCombatOptions.OverallTimeout"/>);
    /// 队友的击杀确认(自己没报过就来了确认)按乱序忽略。</item>
    /// <item>乱序与重复沿用 <see cref="OnlineDungeonEntry"/> 的口径:来得不是时候的包记为乱序、忽略、不缓存;已处理过的再来记为重复、只认第一次;都只计数不推进。</item>
    /// <item>不在包集合里的帧只计数(<see cref="Passthrough"/>)并原样透传(<see cref="OnFrame"/> 的返回值告诉调用方它没被消费)。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 线程:状态机不加锁,调用方保证同一时刻只有一个线程在调它(<see cref="OnlineDungeonCombatRunner"/> 把击杀排进队列、在自己的循环里串行调用)。
    /// 发送类调用只在帧层成功之后才推进状态;发送抛出的异常原样传出,状态不变。
    /// </para>
    /// </summary>
    public sealed class OnlineDungeonCombat
    {
        /// <summary>怪物死亡命令的包号(类 1,CMD 42 DIE_MONSTER)。</summary>
        public const ushort KillPacketId = 42;

        /// <summary>结算命令的包号(类 1,CMD 49 SET_PLAY_RESULT)。</summary>
        public const ushort SettlementPacketId = 49;

        /// <summary>击杀确认通知的包号(类 0,0x26 DIE_MONSTER;载荷以 u16 怪编号开头)。</summary>
        public const int KillConfirmNoticeId = 0x26;

        /// <summary>清图通知的包号(类 0,0x1f ENABLE_CLEAR_DUNGEON;空载荷)。</summary>
        public const int ClearNoticeId = 0x1f;

        /// <summary>通关结果通知的包号(类 0,0x22 PLAY_RESULT)。</summary>
        public const int ResultNoticeId = 0x22;

        /// <summary>通关奖励通知的包号(类 0,0x23 CLEAR_DUNGEON_REWARD)。</summary>
        public const int RewardNoticeId = 0x23;

        /// <summary>击杀失败回包的包号(类 1,与击杀命令同号 42 = 0x2a;载荷 {u8 0, u8 错误码, u16 怪编号})。</summary>
        public const int KillReplyPacketId = 0x2a;

        private readonly IOnlineCombatPort _port;
        private readonly Func<ushort, byte[]> _killPayload;
        private readonly Func<byte[]> _settlementPayload;
        private readonly OnlineCombatMonster[] _monsters;
        private readonly Dictionary<ushort, int> _localOfServer = new Dictionary<ushort, int>();
        private readonly List<OnlineCombatEvent> _events = new List<OnlineCombatEvent>();
        private readonly SortedDictionary<long, int> _passthrough = new SortedDictionary<long, int>();

        private OnlineCombatPhase _phase = OnlineCombatPhase.Fighting;
        private bool _haveResult;
        private bool _haveReward;
        private PacketClass _frameClass;
        private int _frameId = -1;
        private int _frameServerId = -1;

        /// <summary>
        /// 用起始地图快照建对应表并开始。
        /// </summary>
        /// <param name="map">进副本拿到的起始地图(<see cref="OnlineDungeonResult.StartMap"/>)。</param>
        /// <param name="port">发送口。</param>
        /// <param name="options">可调项;null 全用缺省。</param>
        public OnlineDungeonCombat(StartMapNotice map, IOnlineCombatPort port, OnlineDungeonCombatOptions options = null)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _killPayload = options?.KillPayload ?? (mobId => OnlineCombatPayloads.BuildKill(mobId));
            _settlementPayload = options?.SettlementPayload ?? OnlineCombatPayloads.BuildSettlement;

            IReadOnlyList<StartMapMonsterRecord> records = map.Monsters;
            _monsters = new OnlineCombatMonster[records.Count];
            for (int i = 0; i < _monsters.Length; i++)
            {
                ushort number = records[i].C;          // 服务端怪编号 = 记录第 3 项(笔记 §4.4)
                bool duplicate = _localOfServer.ContainsKey(number);
                if (!duplicate)
                {
                    _localOfServer[number] = i;
                }
                else
                {
                    DuplicateNumbers++;
                }
                _monsters[i] = new OnlineCombatMonster(i, records[i], number, duplicate);
            }
            Raise(OnlineCombatEventKind.MonstersSpawned, -1, OnlineCombatBlock.None, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
        }

        /// <summary>当前阶段。</summary>
        public OnlineCombatPhase Phase => _phase;

        /// <summary>对应表(按本地编号排列,线上顺序)。</summary>
        public IReadOnlyList<OnlineCombatMonster> Monsters => _monsters;

        /// <summary>到此刻为止产出的全部事件(只增不减,按产出顺序)。</summary>
        public IReadOnlyList<OnlineCombatEvent> Events => _events;

        /// <summary>起始地图的怪数。</summary>
        public int Total => _monsters.Length;

        /// <summary>起始地图有没有怪。</summary>
        public bool HasMonsters => _monsters.Length > 0;

        /// <summary>服务端编号与前面某条记录重复的怪数(正常数据为 0)。</summary>
        public int DuplicateNumbers { get; private set; }

        /// <summary>已发出击杀命令的怪数。</summary>
        public int Sent { get; private set; }

        /// <summary>已被服务端确认的怪数。</summary>
        public int Confirmed { get; private set; }

        /// <summary>服务端回了击杀失败的怪数。</summary>
        public int Refused { get; private set; }

        /// <summary>被本地拦下(或没能发出)的发送次数,击杀与结算合计。</summary>
        public int Blocked { get; private set; }

        /// <summary>因乱序被忽略的帧数。</summary>
        public int OutOfOrder { get; private set; }

        /// <summary>因重复被忽略的帧数。</summary>
        public int Duplicates { get; private set; }

        /// <summary>带怪编号但编号不在起始地图记录里而被忽略的帧数。</summary>
        public int UnknownNumbers { get; private set; }

        /// <summary>包号认得但载荷读不下来而被忽略的帧数。</summary>
        public int Malformed { get; private set; }

        /// <summary>不在包集合里、只计数透传的帧数。</summary>
        public int PassthroughCount { get; private set; }

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

        /// <summary>所有能独立上报的怪都已报过击杀(没有怪时空真)。</summary>
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

        /// <summary>已发出击杀却还没等到确认或失败回包的怪数。</summary>
        public int PendingConfirmations => Sent - Confirmed - Refused;

        /// <summary>
        /// 一只本地怪死了:按对应表把它的服务端编号报给服务端(类 1、包号 42)。
        /// 本地编号不在表里、这只怪已报过、编号重复没有独立编号、结算已发出,都在本地拦下不发包(返回 <see cref="OnlineCombatSendOutcome.Blocked"/>);
        /// 帧层失败返回 <see cref="OnlineCombatSendOutcome.FrameFailed"/>,状态不变(可以重试)。
        /// </summary>
        /// <param name="localId">本地编号(= 起始地图怪记录的序号)。</param>
        public async Task<OnlineCombatSendResult> ReportKillAsync(int localId, CancellationToken cancellationToken = default)
        {
            if (localId < 0 || localId >= _monsters.Length)
            {
                return BlockKill(localId, OnlineCombatBlock.UnknownLocalId);
            }
            OnlineCombatMonster monster = _monsters[localId];
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
        /// 收到服务端的一帧(已解出类、包号与载荷;信封里的内层帧也走这里)。载荷末尾的会话加密补零不影响判断。
        /// 返回这一帧被怎样对待:<see cref="OnlineCombatFrameVerdict.Passthrough"/> 表示它不在状态机的包集合里(只计了数,调用方自己处理)。
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

        private OnlineCombatSendResult BlockKill(int localId, OnlineCombatBlock block)
        {
            Blocked++;
            Raise(OnlineCombatEventKind.KillBlocked, localId, block, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
            return new OnlineCombatSendResult(OnlineCombatSendOutcome.Blocked, block, FrameStatus.Ok);
        }

        private OnlineCombatSendResult BlockSettlement(OnlineCombatBlock block)
        {
            Blocked++;
            Raise(OnlineCombatEventKind.SettlementBlocked, -1, block, OnlineCombatFrameVerdict.Passthrough, OnlineCombatSettlementPart.None);
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

        private void Raise(OnlineCombatEventKind kind, int localId, OnlineCombatBlock block, OnlineCombatFrameVerdict verdict, OnlineCombatSettlementPart part)
        {
            bool framed = kind == OnlineCombatEventKind.FrameIgnored;
            int serverId = framed ? _frameServerId : localId >= 0 && localId < _monsters.Length ? _monsters[localId].ServerId : -1;
            _events.Add(new OnlineCombatEvent(kind, localId, serverId, block, verdict, part,
                framed ? _frameClass : PacketClass.Noti, framed ? _frameId : -1, _monsters.Length, Sent, Confirmed));
        }
    }
}
