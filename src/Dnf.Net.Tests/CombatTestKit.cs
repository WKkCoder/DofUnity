using System.Diagnostics;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网副本战斗测试共用的合成数据与替身:合成起始地图(怪记录的各项互不相同,怪编号取 9100 以上的合成值,不含任何真实编号)、
/// 服务端方向的合成载荷、客户端载荷的独立读法(<see cref="KillView"/>、<see cref="SettlementView"/>,按既有笔记的布局表手写,不复用产品代码的拼装)、
/// 照笔记 §4.5、§4.6、§5 建模的"服务端"(<see cref="FakeCombatServer"/>)与线程安全的会话假实现(<see cref="CombatFakeSession"/>)。
/// </summary>
internal static class CombatKit
{
    /// <summary>合成的怪编号基数(真服是每个房间 [0, 60000) 内的随机数,这里取固定的合成值,逐个加一)。</summary>
    public const int BaseId = 9100;

    public const byte Noti = (byte)PacketClass.Noti;

    public const byte Cmd = (byte)PacketClass.Cmd;

    /// <summary>记录第 i 条的 A 项(u16):与怪编号、序号都不同。</summary>
    public static int FieldA(int i) => 9200 + i;

    /// <summary>记录第 i 条的 B 项(i32)。</summary>
    public static int FieldB(int i) => 70000 + i;

    /// <summary>记录第 i 条的 D 项(i32,怪码)。</summary>
    public static int FieldD(int i) => 80000 + i;

    /// <summary>连续的怪编号:基数起逐个加一。</summary>
    public static int[] Sequential(int count, int baseId = BaseId)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = baseId + i;
        }
        return ids;
    }

    /// <summary>
    /// 合成的起始地图载荷(地图类型 1、有地图体):头部十字节、地图编号 905、<paramref name="serverIds"/> 条怪记录(第 3 项是怪编号,
    /// 其余各项每条不同且都不等于编号)、0 条物品、全局 3、0 组四元组、玩家序号 0。读序同 <c>StartMapParser</c>,手工拼,不用产品代码。
    /// </summary>
    public static byte[] Map29Payload(IReadOnlyList<int> serverIds)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder()
            .U8(3).U8(4).U8(0).I32(90001).U8(1).U8(2)
            .U8(1)
            .U16(905)
            .U8(serverIds.Count);
        for (int i = 0; i < serverIds.Count; i++)
        {
            b.U16(FieldA(i)).I32(FieldB(i)).U16(serverIds[i]).I32(FieldD(i))
                .U8(1 + i % 200).U8(2 + i % 200).U8(3 + i % 200).U8(4 + i % 200);
        }
        return b.U8(0).U8(0x63).U8(0).U8(0).ToArray();
    }

    /// <summary>解析合成的起始地图;解不开就让测试失败。</summary>
    public static StartMapNotice Map(IReadOnlyList<int> serverIds)
    {
        Assert.True(StartMapParser.TryParse(Map29Payload(serverIds), out StartMapNotice map), "合成起始地图应当能解析");
        Assert.Equal(serverIds.Count, map.Monsters.Count);
        return map;
    }

    public static StartMapNotice Map(int count, int baseId = BaseId)
    {
        return Map(Sequential(count, baseId));
    }

    /// <summary>地图类型 0 的短形载荷(没有地图体,所以没有怪记录)。</summary>
    public static StartMapNotice MapWithoutBody()
    {
        Assert.True(StartMapParser.TryParse(DungeonEntryWire.Map29(), out StartMapNotice map));
        Assert.Empty(map.Monsters);
        return map;
    }

    /// <summary>测试用的跑器可调项:轮询窗口短、整段上限宽(15 秒,只是保险)。</summary>
    public static OnlineDungeonCombatOptions Options(int pollMilliseconds = 20, double? overallSeconds = 15, double settlementSeconds = 10)
    {
        return new OnlineDungeonCombatOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(pollMilliseconds),
            SettlementTimeout = TimeSpan.FromSeconds(settlementSeconds),
            OverallTimeout = overallSeconds.HasValue ? TimeSpan.FromSeconds(overallSeconds.Value) : (TimeSpan?)null,
        };
    }

    // ---------------- 服务端方向的合成载荷 ----------------

    /// <summary>击杀确认通知(类 0、0x26)的最小形态:u16 怪编号、00、00、FF、00(笔记 §4.7 无掉落的 6 字节形态)。</summary>
    public static byte[] ConfirmPayload(int mobId)
    {
        return new NoticePayloadBuilder().U16(mobId).U8(0).U8(0).U8(0xFF).U8(0).ToArray();
    }

    /// <summary>击杀失败回包(类 1、42)的载荷:{u8 0, u8 错误码, u16 怪编号}(笔记 §4.6)。</summary>
    public static byte[] RefusalPayload(int mobId, int code = 5)
    {
        return new NoticePayloadBuilder().U8(0).U8(code).U16(mobId).ToArray();
    }

    /// <summary>通关结果通知(0x22)的合成载荷:内容不透明,状态机不读。</summary>
    public static byte[] ResultPayload()
    {
        return GatewayTestSupport.Pattern(10, 0x30, 3);
    }

    /// <summary>通关奖励通知(0x23)的合成载荷:内容不透明,状态机不读。</summary>
    public static byte[] RewardPayload()
    {
        return GatewayTestSupport.Pattern(12, 0x50, 5);
    }

    /// <summary>服务端一帧:类、包号、载荷。</summary>
    public readonly struct ServerFrame
    {
        public ServerFrame(byte cls, ushort id, byte[] payload)
        {
            Class = cls;
            Id = id;
            Payload = payload;
        }

        public byte Class { get; }

        public ushort Id { get; }

        public byte[] Payload { get; }

        /// <summary>解码好的帧(给假会话接缝用)。</summary>
        public DecodedFrame Decoded() => DungeonEntryWire.Decoded(Class, Id, Payload);

        /// <summary>会话加密的线上字节(给真会话接内存管道用)。</summary>
        public byte[] Wire() => Class == Noti ? DungeonEntryWire.Notice(Id, Payload) : DungeonEntryWire.Reply(Id, Payload);
    }

    public static ServerFrame Confirm(int mobId) => new ServerFrame(Noti, 0x26, ConfirmPayload(mobId));

    public static ServerFrame Clear() => new ServerFrame(Noti, 0x1f, new byte[0]);

    public static ServerFrame Result() => new ServerFrame(Noti, 0x22, ResultPayload());

    public static ServerFrame Reward() => new ServerFrame(Noti, 0x23, RewardPayload());

    public static ServerFrame Refusal(int mobId, int code = 5) => new ServerFrame(Cmd, 42, RefusalPayload(mobId, code));

    public static ServerFrame Heartbeat() => new ServerFrame(Noti, 0, new byte[0]);

    /// <summary>与战斗无关的通知,包号 900 以上的合成值。</summary>
    public static ServerFrame Unrelated(int id = 901) => new ServerFrame(Noti, (ushort)id, GatewayTestSupport.Pattern(7, 0x11, 2));

    /// <summary>
    /// 造一个 <see cref="DecodedKind.Plain"/> 的解码帧(与真会话附在信封之后的内层副本同种):解码器默认只有 561 是明文包号,
    /// 这里临时把要造的包号加进去。载荷原样,走真解码器。
    /// </summary>
    public static DecodedFrame DecodedPlain(byte cls, ushort id, byte[] payload)
    {
        var decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        decoder.PlainNotiIds.Add(id);
        FrameStatus status = decoder.Decode(DungeonEntryWire.Frame(cls, id, payload), out DecodedFrame frame);
        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(cls == Noti ? DecodedKind.Plain : DecodedKind.Session, frame.Kind);
        return frame;
    }

    /// <summary>
    /// 把事件序列渲染成一行文字,断言时一次比对整条链:Spawned、KillSent:本地号、KillConfirmed:本地号、KillRefused:本地号、
    /// KillBlocked:本地号:原因、MapCleared、SettlementSent、SettlementBlocked:原因、SettlementArrived:段、ReadyToReturnToTown、Ignored:判定[:本地号]。
    /// </summary>
    public static string Trace(IEnumerable<OnlineCombatEvent> events)
    {
        return string.Join(" ", events.Select(Describe));
    }

    private static string Describe(OnlineCombatEvent e)
    {
        switch (e.Kind)
        {
            case OnlineCombatEventKind.MonstersSpawned: return "Spawned";
            case OnlineCombatEventKind.KillSent: return "KillSent:" + e.LocalId;
            case OnlineCombatEventKind.KillConfirmed: return "KillConfirmed:" + e.LocalId;
            case OnlineCombatEventKind.KillRefused: return "KillRefused:" + e.LocalId;
            case OnlineCombatEventKind.KillBlocked: return "KillBlocked:" + e.LocalId + ":" + e.Block;
            case OnlineCombatEventKind.MapCleared: return "MapCleared";
            case OnlineCombatEventKind.SettlementSent: return "SettlementSent";
            case OnlineCombatEventKind.SettlementBlocked: return "SettlementBlocked:" + e.Block;
            case OnlineCombatEventKind.SettlementArrived: return "SettlementArrived:" + e.Part;
            case OnlineCombatEventKind.ReadyToReturnToTown: return "ReadyToReturnToTown";
            case OnlineCombatEventKind.FrameIgnored: return "Ignored:" + e.Verdict + (e.LocalId >= 0 ? ":" + e.LocalId : "");
            default: return e.Kind.ToString();
        }
    }

    /// <summary>轮询等条件成立,超时让测试失败(给后台线程里跑的跑器用)。</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int milliseconds = 8000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds)
            {
                Assert.Fail("等待超时:" + what);
            }
            await Task.Delay(5);
        }
    }
}

/// <summary>
/// 怪物死亡命令(类 1、包号 42)载荷的独立读法:严格按笔记 §4.1 的布局表(u16 怪编号、u16 击杀者、四个 i32、u8 条数、
/// n × {u16, u16, i32, u16}、尾部 33 字节)手写,不调产品代码。长度不够布局就让测试失败。
/// </summary>
internal sealed class KillView
{
    public int Length { get; private set; }

    public ushort MobId { get; private set; }

    public ushort Killer { get; private set; }

    public int[] Secure { get; private set; }

    public int AttackCount { get; private set; }

    public List<(ushort Type, ushort Id, int Damage, ushort Hits)> Attacks { get; private set; }

    /// <summary>攻击记录之后的偏移 q = 21 + 10n。</summary>
    public int Q { get; private set; }

    public ushort X { get; private set; }

    public ushort Y { get; private set; }

    /// <summary>q+4 与 q+5 两个字节。</summary>
    public byte[] Flags45 { get; private set; }

    /// <summary>被动物件标志(q+6)。</summary>
    public byte Passive { get; private set; }

    /// <summary>q+7 起到尾部结束的全部字节(q+7 .. q+32)。</summary>
    public byte[] TailAfterPassive { get; private set; }

    public static KillView Parse(byte[] payload)
    {
        Assert.NotNull(payload);
        Assert.True(payload.Length >= 54, "击杀命令载荷至少 54 字节,实际 " + payload.Length);
        var v = new KillView { Length = payload.Length };
        v.MobId = U16(payload, 0);
        v.Killer = U16(payload, 2);
        v.Secure = new[] { I32(payload, 4), I32(payload, 8), I32(payload, 12), I32(payload, 16) };
        v.AttackCount = payload[20];
        int n = v.AttackCount;
        v.Q = 21 + 10 * n;
        Assert.True(payload.Length >= v.Q + 33, "载荷读不满:n=" + n + " 需要 " + (v.Q + 33) + " 字节,实际 " + payload.Length);
        v.Attacks = new List<(ushort, ushort, int, ushort)>();
        for (int i = 0; i < n; i++)
        {
            int o = 21 + 10 * i;
            v.Attacks.Add((U16(payload, o), U16(payload, o + 2), I32(payload, o + 4), U16(payload, o + 8)));
        }
        int q = v.Q;
        v.X = U16(payload, q);
        v.Y = U16(payload, q + 2);
        v.Flags45 = new[] { payload[q + 4], payload[q + 5] };
        v.Passive = payload[q + 6];
        v.TailAfterPassive = payload.Skip(q + 7).Take(33 - 7).ToArray();
        return v;
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    private static int I32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
}

/// <summary>
/// 结算命令(类 1、包号 49)载荷的独立读法:按服务端读序 u8、u8、u16、u8 n、min(n,4) 条 {u16, i32}、i32、i32、再 5 个 i32;
/// 读不满(服务端会断线)就让测试失败,读完返回各字段。
/// </summary>
internal sealed class SettlementView
{
    public byte[] Raw { get; private set; }

    public int Consumed { get; private set; }

    public int N { get; private set; }

    public int[] Fields { get; private set; }

    public static SettlementView Parse(byte[] payload)
    {
        Assert.NotNull(payload);
        var v = new SettlementView { Raw = payload };
        int o = 0;
        var fields = new List<int>();

        void Need(int count) => Assert.True(o + count <= payload.Length, "结算载荷读不满:在偏移 " + o + " 还需要 " + count + " 字节,总长 " + payload.Length);

        Need(1);
        fields.Add(payload[o]);
        o += 1;
        Need(1);
        fields.Add(payload[o]);
        o += 1;
        Need(2);
        fields.Add(payload[o] | (payload[o + 1] << 8));
        o += 2;
        Need(1);
        v.N = payload[o];
        o += 1;
        int signedN = (sbyte)v.N;
        int records = signedN <= 0 ? 0 : Math.Min(signedN, 4);
        for (int i = 0; i < records; i++)
        {
            Need(6);
            o += 6;
        }
        for (int i = 0; i < 7; i++)
        {
            Need(4);
            fields.Add(payload[o] | (payload[o + 1] << 8) | (payload[o + 2] << 16) | (payload[o + 3] << 24));
            o += 4;
        }
        v.Consumed = o;
        v.Fields = fields.ToArray();
        return v;
    }
}

/// <summary>
/// 服务端的行为替身,照笔记 §4.5、§4.6、§5 建模:
/// 怪物死亡命令读不满断线;被动物件标志为 1 静默;怪编号在当前图的表里 → 删除、发击杀确认(类 0、0x26);表空了 → 再发清图通知(类 0、0x1f,空载荷);
/// 编号不在表里 → 回 {0, 5, 编号}(类 1、42);第二次上报同一编号同样找不到。
/// 结算命令读不满断线;没清图时静默并记一次"过早结算"(服务端记反作弊计数);清图后 → 回通关结果(0x22)与通关奖励(0x23)。
/// 只是测试用的建模,不是服务端的实现。线程安全(跑器在后台线程里调它,测试线程读它)。
/// </summary>
internal sealed class FakeCombatServer
{
    private readonly object _gate = new object();
    private readonly HashSet<int> _table;
    private readonly List<string> _journal = new List<string>();
    private readonly List<KillView> _kills = new List<KillView>();
    private bool _withholdClear;
    private bool _cleared;
    private bool _disconnected;
    private int _prematureSettlements;
    private int _settlements;
    private int _refusals;
    private int _passive;

    public FakeCombatServer(IEnumerable<int> table)
    {
        _table = new HashSet<int>(table);
    }

    /// <summary>为 true 时表空了也不发清图通知(由测试稍后手动放出),用来验证"没清图不结算"。</summary>
    public bool WithholdClear
    {
        get { lock (_gate) { return _withholdClear; } }
        set { lock (_gate) { _withholdClear = value; } }
    }

    public bool Cleared { get { lock (_gate) { return _cleared; } } }

    public bool Disconnected { get { lock (_gate) { return _disconnected; } } }

    public int PrematureSettlements { get { lock (_gate) { return _prematureSettlements; } } }

    public int Settlements { get { lock (_gate) { return _settlements; } } }

    public int Refusals { get { lock (_gate) { return _refusals; } } }

    public int Passive { get { lock (_gate) { return _passive; } } }

    public int Remaining { get { lock (_gate) { return _table.Count; } } }

    /// <summary>按发生顺序的流水(快照):kill:编号、refuse:编号、clear、premature、settle、passive。</summary>
    public string[] Journal { get { lock (_gate) { return _journal.ToArray(); } } }

    /// <summary>收到的每条击杀命令(解读后,快照)。</summary>
    public KillView[] Kills { get { lock (_gate) { return _kills.ToArray(); } } }

    /// <summary>"服务端"这时放出清图通知(配合 <see cref="WithholdClear"/>):表必须已经空了。</summary>
    public CombatKit.ServerFrame ReleaseClear()
    {
        lock (_gate)
        {
            Assert.Empty(_table);
            _cleared = true;
            _journal.Add("clear");
        }
        return CombatKit.Clear();
    }

    /// <summary>处理客户端的一帧,返回服务端要回的帧(按发送顺序)。</summary>
    public List<CombatKit.ServerFrame> Handle(PacketClass cls, int id, byte[] payload)
    {
        lock (_gate)
        {
            var replies = new List<CombatKit.ServerFrame>();
            if (_disconnected || cls != PacketClass.Cmd)
            {
                return replies;
            }
            if (id == 42)
            {
                HandleKill(payload, replies);
            }
            else if (id == 49)
            {
                HandleSettlement(payload, replies);
            }
            return replies;
        }
    }

    private void HandleKill(byte[] payload, List<CombatKit.ServerFrame> replies)
    {
        if (payload.Length < 54)
        {
            _disconnected = true;
            return;
        }
        int n = payload[20];
        if (payload.Length < 54 + 10 * n)
        {
            _disconnected = true;
            return;
        }
        KillView view = KillView.Parse(payload);
        _kills.Add(view);
        if (view.Passive == 1)
        {
            _passive++;
            _journal.Add("passive");
            return;
        }
        if (_table.Remove(view.MobId))
        {
            _journal.Add("kill:" + view.MobId);
            replies.Add(CombatKit.Confirm(view.MobId));
            if (_table.Count == 0 && !_withholdClear)
            {
                _cleared = true;
                _journal.Add("clear");
                replies.Add(CombatKit.Clear());
            }
        }
        else
        {
            _refusals++;
            _journal.Add("refuse:" + view.MobId);
            replies.Add(CombatKit.Refusal(view.MobId));
        }
    }

    private void HandleSettlement(byte[] payload, List<CombatKit.ServerFrame> replies)
    {
        if (payload.Length < 33)
        {
            _disconnected = true;
            return;
        }
        SettlementView.Parse(payload);
        if (!_cleared)
        {
            _prematureSettlements++;
            _journal.Add("premature");
            return;
        }
        if (_settlements > 0)
        {
            return;
        }
        _settlements++;
        _journal.Add("settle");
        replies.Add(CombatKit.Result());
        replies.Add(CombatKit.Reward());
    }
}

/// <summary>记录用的发送口:每次发送记下(类、包号、载荷副本),可按序号指定帧层状态或异常。</summary>
internal sealed class RecordingCombatPort : IOnlineCombatPort
{
    public List<(PacketClass Class, ushort Id, byte[] Payload)> Sent { get; } = new List<(PacketClass, ushort, byte[])>();

    /// <summary>第 n 次发送(从 0 起)返回的帧层状态。</summary>
    public Func<int, FrameStatus> StatusOfSend { get; set; } = index => FrameStatus.Ok;

    /// <summary>第 n 次发送要抛的异常(null 为不抛)。</summary>
    public Func<int, Exception> FailureOfSend { get; set; } = index => null;

    public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
    {
        int index = Sent.Count;
        Sent.Add((packetClass, packetId, (byte[])payload.Clone()));
        Exception failure = FailureOfSend(index);
        if (failure != null)
        {
            throw failure;
        }
        return Task.FromResult(StatusOfSend(index));
    }

    public int KillCommands => Sent.Count(s => s.Class == PacketClass.Cmd && s.Id == 42);

    public int SettlementCommands => Sent.Count(s => s.Class == PacketClass.Cmd && s.Id == 49);
}

/// <summary>
/// 会话接缝的线程安全假实现,带"服务端"回调:跑器在后台线程里收发,测试线程随时可以往里灌帧、读发送记录。
/// 收件箱按"帧单元"排队:一个单元是一帧,或"信封帧加会话附在它后面的内层副本"这样一批(真会话一次收帧把它们一起交回);
/// 每次收帧最多交出 <c>stopAfterFrames</c> 个单元;队空就按小步轮询等满窗口(与真会话"到点空手返回"一致),窗口内灌进来的帧立刻能读到。
/// </summary>
internal sealed class CombatFakeSession : IOnlineDungeonSession
{
    private readonly object _gate = new object();
    private readonly Queue<DecodedFrame[]> _inbox = new Queue<DecodedFrame[]>();
    private readonly List<(PacketClass Class, ushort Id, byte[] Payload)> _sent = new List<(PacketClass, ushort, byte[])>();
    private volatile bool _closed;
    private volatile FrameError _fault;
    private int _receiveCalls;

    public bool Ready { get; set; } = true;

    public bool Closed
    {
        get => _closed;
        set => _closed = value;
    }

    public FrameError Fault
    {
        get => _fault;
        set => _fault = value;
    }

    /// <summary>第 n 次发送(从 0 起)返回的帧层状态。</summary>
    public Func<int, FrameStatus> StatusOfSend { get; set; } = index => FrameStatus.Ok;

    /// <summary>第 n 次发送要抛的异常(null 为不抛)。</summary>
    public Func<int, Exception> FailureOfSend { get; set; } = index => null;

    /// <summary>"服务端"回调:每次发送成功后调用,返回的帧追加进收件箱(各占一个单元)。</summary>
    public Func<PacketClass, ushort, byte[], IEnumerable<CombatKit.ServerFrame>> Respond { get; set; }

    public bool IsReady => Ready;

    public bool PeerClosed => _closed;

    public FrameError LastFrameError => _fault;

    public int ReceiveCalls => Volatile.Read(ref _receiveCalls);

    /// <summary>发送记录(快照,含失败的)。</summary>
    public (PacketClass Class, ushort Id, byte[] Payload)[] Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    public int Commands(int id) => Sent.Count(s => s.Class == PacketClass.Cmd && s.Id == id);

    /// <summary>只回应战斗命令(42、49)的会话:战斗从已拿到起始地图的时刻开始。</summary>
    public static CombatFakeSession ForServer(FakeCombatServer server)
    {
        return new CombatFakeSession
        {
            Respond = (cls, id, payload) => server.Handle(cls, id, payload),
        };
    }

    /// <summary>
    /// 从进副本开始的会话:选副本流程的两条命令(15、16)分别回 27 与 28 加 29(29 是 <paramref name="map29"/>),其余交给 <paramref name="server"/>。
    /// </summary>
    public static CombatFakeSession ForEntry(FakeCombatServer server, byte[] map29)
    {
        return new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == 15)
                {
                    return new[] { new CombatKit.ServerFrame(CombatKit.Noti, 27, DungeonEntryWire.Loading27()) };
                }
                if (cls == PacketClass.Cmd && id == 16)
                {
                    return new[]
                    {
                        new CombatKit.ServerFrame(CombatKit.Noti, 28, DungeonEntryWire.Info28()),
                        new CombatKit.ServerFrame(CombatKit.Noti, 29, map29),
                    };
                }
                return server.Handle(cls, id, payload);
            },
        };
    }

    /// <summary>灌进若干帧(线程安全),每帧一个单元。</summary>
    public void Push(params CombatKit.ServerFrame[] frames)
    {
        lock (_gate)
        {
            foreach (CombatKit.ServerFrame frame in frames)
            {
                _inbox.Enqueue(new[] { frame.Decoded() });
            }
        }
    }

    /// <summary>灌进一个单元:几帧一起交付(例如信封加它的内层副本)。</summary>
    public void PushUnit(params DecodedFrame[] unit)
    {
        lock (_gate)
        {
            _inbox.Enqueue(unit);
        }
    }

    public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
    {
        int index;
        lock (_gate)
        {
            index = _sent.Count;
            _sent.Add((packetClass, packetId, (byte[])payload.Clone()));
        }
        Exception failure = FailureOfSend(index);
        if (failure != null)
        {
            throw failure;
        }
        FrameStatus status = StatusOfSend(index);
        if (status == FrameStatus.Ok && Respond != null)
        {
            IEnumerable<CombatKit.ServerFrame> replies = Respond(packetClass, packetId, payload);
            if (replies != null)
            {
                Push(replies.ToArray());
            }
        }
        return Task.FromResult(status);
    }

    public async Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _receiveCalls);
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_inbox.Count > 0)
                {
                    var result = new List<DecodedFrame>();
                    int units = 0;
                    while (_inbox.Count > 0 && (stopAfterFrames <= 0 || units < stopAfterFrames))
                    {
                        result.AddRange(_inbox.Dequeue());
                        units++;
                    }
                    return result;
                }
            }
            if (_closed || _fault != FrameError.None || window <= TimeSpan.Zero || watch.Elapsed >= window)
            {
                return new List<DecodedFrame>();
            }
            TimeSpan left = window - watch.Elapsed;
            await Task.Delay(left < TimeSpan.FromMilliseconds(5) ? left : TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }
}

/// <summary>状态机层的小装置:合成起始地图 + 记录用发送口 + 状态机,带几个喂帧的快捷方法。</summary>
internal sealed class CombatRig
{
    public CombatRig(IReadOnlyList<int> serverIds, OnlineDungeonCombatOptions options = null)
    {
        Ids = serverIds.ToArray();
        Map = CombatKit.Map(serverIds);
        Port = new RecordingCombatPort();
        Machine = new OnlineDungeonCombat(Map, Port, options);
    }

    public CombatRig(int count, OnlineDungeonCombatOptions options = null)
        : this(CombatKit.Sequential(count), options)
    {
    }

    public int[] Ids { get; }

    public StartMapNotice Map { get; }

    public RecordingCombatPort Port { get; }

    public OnlineDungeonCombat Machine { get; }

    public Task<OnlineCombatSendResult> Kill(int local) => Machine.ReportKillAsync(local);

    public Task<OnlineCombatSendResult> Settle() => Machine.RequestSettlementAsync();

    public OnlineCombatFrameVerdict Feed(CombatKit.ServerFrame frame) => Machine.OnFrame((PacketClass)frame.Class, frame.Id, frame.Payload);

    public OnlineCombatFrameVerdict Confirm(int serverId) => Feed(CombatKit.Confirm(serverId));

    public OnlineCombatFrameVerdict Clear() => Feed(CombatKit.Clear());

    public OnlineCombatFrameVerdict Result() => Feed(CombatKit.Result());

    public OnlineCombatFrameVerdict Reward() => Feed(CombatKit.Reward());

    /// <summary>报某只怪的击杀并断言发出去了,返回发出的那条命令的解读。</summary>
    public async Task<KillView> KillAndRead(int local)
    {
        int before = Port.Sent.Count;
        OnlineCombatSendResult result = await Kill(local);
        Assert.True(result.Sent, "本地编号 " + local + " 应当发出击杀命令,实际 " + result.Outcome + "/" + result.Block);
        Assert.Equal(before + 1, Port.Sent.Count);
        (PacketClass cls, ushort id, byte[] payload) = Port.Sent[before];
        Assert.Equal(PacketClass.Cmd, cls);
        Assert.Equal(42, id);
        return KillView.Parse(payload);
    }

    /// <summary>走到"闸门打开":把所有怪报一遍并确认,再收清图通知。</summary>
    public async Task FightToClear()
    {
        for (int i = 0; i < Ids.Length; i++)
        {
            await Kill(i);
            Confirm(Ids[i]);
        }
        Clear();
    }

    public int Count(OnlineCombatEventKind kind) => Machine.Events.Count(e => e.Kind == kind);
}
