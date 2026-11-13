using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 多房间联网战斗测试共用的合成数据与替身:换房载荷的独立读法(<see cref="MoveRoomView"/>,按服务端读序手写,不复用产品代码的拼装)、
/// 合成的起始地图与回包帧(<see cref="RoomKit"/>)、照静态读码建模的多房间"服务端"(<see cref="FakeMultiRoomServer"/>)、状态机层的小装置(<see cref="RoomRig"/>)。
/// 数据全是合成的:怪编号 9100(第一间)与 9300(第二间)起逐个加一,位置、座位项等取 900 以上的合成值,不含任何真实编号或抓包字节。
/// 服务端替身里的错误码(19、20、1、18、21)是静态读码读到的协议枚举,不是实测值。
/// </summary>
internal static class RoomKit
{
    /// <summary>第二个房间的合成怪编号基数(第一间是 <see cref="CombatKit.BaseId"/>)。</summary>
    public const int SecondBaseId = 9300;

    /// <summary>第三个房间的合成怪编号基数。</summary>
    public const int ThirdBaseId = 9500;

    /// <summary>解析一份合成起始地图(指定迷宫格与地图编号);解不开就让测试失败。</summary>
    public static StartMapNotice Map(IReadOnlyList<int> serverIds, int cellX, int cellY, int mapId = 905)
    {
        Assert.True(StartMapParser.TryParse(CombatKit.Map29Payload(serverIds, cellX, cellY, mapId), out StartMapNotice map), "合成起始地图应当能解析");
        Assert.Equal(serverIds.Count, map.Monsters.Count);
        Assert.Equal(cellX, map.CellX);
        Assert.Equal(cellY, map.CellY);
        return map;
    }

    /// <summary>新房间的起始地图通知(类 0、包号 29)。</summary>
    public static CombatKit.ServerFrame StartMap(IReadOnlyList<int> serverIds, int cellX, int cellY, int mapId = 906)
    {
        return new CombatKit.ServerFrame(CombatKit.Noti, 29, CombatKit.Map29Payload(serverIds, cellX, cellY, mapId));
    }

    /// <summary>装载完成通知(类 0、包号 30):载荷一个 i32,取合成值。</summary>
    public static CombatKit.ServerFrame Loaded()
    {
        return new CombatKit.ServerFrame(CombatKit.Noti, 30, new NoticePayloadBuilder().I32(901).ToArray());
    }

    /// <summary>换房失败回包(类 1、包号 48):{u8 0, u8 错误码},后面可以跟若干补零(线上的折叠摘要与会话补零由真会话负责,这里只验证"只认前两个字节")。</summary>
    public static CombatKit.ServerFrame MoveRefusal(int code, int trailingZeros = 0)
    {
        return new CombatKit.ServerFrame(CombatKit.Cmd, 48, new NoticePayloadBuilder().U8(0).U8(code).Raw(new byte[trailingZeros]).ToArray());
    }

    /// <summary>
    /// 会话加密的线上字节:一帧明文服务端回包 / 一个真信封封着它。
    /// </summary>
    public static byte[] Wire(CombatKit.ServerFrame frame, bool enveloped)
    {
        return enveloped ? HostTestKit.RealEnvelope(frame.Class, frame.Id, frame.Payload) : frame.Wire();
    }

    /// <summary>换房请求的缩写。</summary>
    public static OnlineMoveRoomRequest Move(int x, int y, int positionX = 0, int positionY = 0, bool layered = false)
    {
        return new OnlineMoveRoomRequest(x, y, positionX, positionY, layered);
    }
}

/// <summary>
/// 换房命令(类 1、包号 48)载荷的独立读法:严格按服务端 <c>DisPatcher_MoveMap::read</c> 的读序逐项往后读
/// (i8、i8、i32、i32、u8、u16、4 × u16、4 × u32、u16、i32,共 43 字节),手写,不调产品代码;读序是顺序游标,不用偏移表,
/// 所以产品代码把某个字段挪了一个字节,这里读出来的值就会错位而不是碰巧对上。长度不是 43 就让测试失败。
/// </summary>
internal sealed class MoveRoomView
{
    public int Length { get; private set; }

    public sbyte TargetX { get; private set; }

    public sbyte TargetY { get; private set; }

    public int PositionX { get; private set; }

    public int PositionY { get; private set; }

    public byte Layered { get; private set; }

    public ushort TrapBits { get; private set; }

    public ushort[] Seats { get; private set; }

    public uint[] PlayTimes { get; private set; }

    public ushort Unused { get; private set; }

    public int Tail { get; private set; }

    /// <summary>读完之后游标停在哪(必须正好是 43)。</summary>
    public int Consumed { get; private set; }

    public static MoveRoomView Parse(byte[] payload)
    {
        Assert.NotNull(payload);
        Assert.True(payload.Length == 43, "换房命令载荷必须恰好 43 字节(服务端读满 43 字节才继续,读不满断线),实际 " + payload.Length);
        var v = new MoveRoomView { Length = payload.Length };
        int o = 0;

        byte U8() => payload[o++];
        ushort U16()
        {
            ushort x = (ushort)(payload[o] | (payload[o + 1] << 8));
            o += 2;
            return x;
        }
        uint U32()
        {
            uint x = (uint)(payload[o] | (payload[o + 1] << 8) | (payload[o + 2] << 16) | (payload[o + 3] << 24));
            o += 4;
            return x;
        }

        v.TargetX = (sbyte)U8();
        v.TargetY = (sbyte)U8();
        v.PositionX = (int)U32();
        v.PositionY = (int)U32();
        v.Layered = U8();
        v.TrapBits = U16();
        v.Seats = new ushort[4];
        for (int i = 0; i < 4; i++)
        {
            v.Seats[i] = U16();
        }
        v.PlayTimes = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            v.PlayTimes[i] = U32();
        }
        v.Unused = U16();
        v.Tail = (int)U32();
        v.Consumed = o;
        Assert.Equal(43, v.Consumed);
        return v;
    }
}

/// <summary>
/// 装载完成命令(类 1、包号 40)载荷的独立读法:两个 i32,共 8 字节(<c>DisPatcher_FinishLoad::read</c> 的读序);长度不是 8 就让测试失败。
/// </summary>
internal sealed class RoomLoadedView
{
    public int First { get; private set; }

    public int Second { get; private set; }

    public static RoomLoadedView Parse(byte[] payload)
    {
        Assert.NotNull(payload);
        Assert.True(payload.Length == 8, "装载完成命令载荷必须恰好 8 字节,实际 " + payload.Length);
        return new RoomLoadedView
        {
            First = payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24),
            Second = payload[4] | (payload[5] << 8) | (payload[6] << 16) | (payload[7] << 24),
        };
    }
}

/// <summary>
/// 多房间"服务端"的行为替身,照静态读码建模(只是测试用的建模,不是服务端的实现;没有任何真服抓包做依据):
/// <list type="bullet">
/// <item>击杀命令:同 <see cref="FakeCombatServer"/>(怪编号在当前房间的表里 → 删除并回击杀确认;不在 → 回 {0,5,编号});
/// 区别是<b>清图通知只在最后一间的表清空时才发</b>(笔记 §5 第 6 条);不是最后一间清空,不发任何通知。</item>
/// <item>换房命令:读不满 43 字节断线;目标格超出迷宫宽高断线(<c>check_error</c> 返回 -5);trapBits 不是 7 的倍数记 0x1f9 计数;座位项与末尾 i32 的计数从略(只记座位为 0 的 0x19b);
/// 第二次起位置不在"过门位置集合"里记 0x1f7 计数(<c>CheckMoveMapFitInPathHackCnt</c>,第一次豁免,被拒的也计);
/// 然后按 <c>CParty::move_map</c> 的次序:副本已通关回 19;当前房间还有怪(含只有服务端知道的)回 20;目标格就是当前格回 18;目标格不是相邻格回 21;
/// 都过了就换房:回新房间的起始地图通知(类 0、包号 29,<b>没有 48 号回包</b>),并起装载超时定时器。</item>
/// <item>装载完成命令:读不满 8 字节断线;定时器在跑就停表并回装载完成通知(类 0、包号 30);没在跑(重复或没换房就发)忽略。
/// <see cref="ExpireLoadingTimer"/> 模拟定时器到点:还没收到装载完成的成员被踢(断线)。</item>
/// <item>结算命令:同 <see cref="FakeCombatServer"/>。</item>
/// </list>
/// 线程安全(跑器在后台线程里调它,测试线程读它)。
/// </summary>
internal sealed class FakeMultiRoomServer
{
    /// <summary>"副本已通关或队伍状态不对"。</summary>
    public const int ErrDungeonCleared = 19;

    /// <summary>"当前房间还没清"。</summary>
    public const int ErrRoomNotCleared = 20;

    /// <summary>"已经在目标格"。</summary>
    public const int ErrSameCell = 18;

    /// <summary>"目标格不是相邻格"。</summary>
    public const int ErrNotAdjacent = 21;

    /// <summary>trapBits 非 7 的倍数的反作弊计数类型。</summary>
    public const int HackTrapBits = 0x1f9;

    /// <summary>过门位置不合的反作弊计数类型。</summary>
    public const int HackPathPosition = 0x1f7;

    /// <summary>座位项为 0 的反作弊计数类型。</summary>
    public const int HackSeatZero = 0x19b;

    /// <summary>迷宫里的一个房间:格坐标、客户端看得见的怪编号、只有服务端知道的怪编号(客户端的起始地图里没有)。</summary>
    public sealed class RoomPlan
    {
        public RoomPlan(int cellX, int cellY, IEnumerable<int> ids, IEnumerable<int> hidden = null)
        {
            CellX = cellX;
            CellY = cellY;
            Ids = ids.ToArray();
            Hidden = hidden == null ? new int[0] : hidden.ToArray();
        }

        public int CellX { get; }

        public int CellY { get; }

        public int[] Ids { get; }

        public int[] Hidden { get; }
    }

    private readonly object _gate = new object();
    private readonly List<RoomPlan> _rooms;
    private readonly HashSet<int> _table = new HashSet<int>();
    private readonly HashSet<int> _hidden = new HashSet<int>();
    private readonly List<string> _journal = new List<string>();
    private readonly List<KillView> _kills = new List<KillView>();
    private readonly List<MoveRoomView> _moves = new List<MoveRoomView>();
    private readonly List<RoomLoadedView> _loads = new List<RoomLoadedView>();
    private readonly Dictionary<int, int> _hack = new Dictionary<int, int>();
    private readonly HashSet<(int, int)> _gatePositions = new HashSet<(int, int)>();
    private int _room;
    private bool _cleared;
    private bool _disconnected;
    private bool _kicked;
    private bool _loadingPending;
    private bool _withholdClear;
    private bool _swallowLoaded;
    private bool _swallowMoves;
    private int _moveCalls;
    private int _ignoredLoads;
    private int _prematureSettlements;
    private int _settlements;
    private int _refusals;

    public FakeMultiRoomServer(params RoomPlan[] rooms)
    {
        Assert.NotEmpty(rooms);
        _rooms = rooms.ToList();
        LoadRoom(0);
        _loadingPending = true;         // 开局就起了装载超时定时器(send_loadingcheck_msg 在 dungeon_start 里)
    }

    /// <summary>迷宫宽(目标格 X 不小于它就断线)。</summary>
    public int MazeWidth { get; set; } = 4;

    /// <summary>迷宫高。</summary>
    public int MazeHeight { get; set; } = 3;

    /// <summary>当前所在房间的序号。</summary>
    public int Room { get { lock (_gate) { return _room; } } }

    /// <summary>为 true 时最后一间的表空了也不发清图通知(由测试稍后手动放出)。</summary>
    public bool WithholdClear
    {
        get { lock (_gate) { return _withholdClear; } }
        set { lock (_gate) { _withholdClear = value; } }
    }

    /// <summary>为 true 时收到装载完成命令只记账、不回装载完成通知(模拟握手卡住)。</summary>
    public bool SwallowLoaded
    {
        get { lock (_gate) { return _swallowLoaded; } }
        set { lock (_gate) { _swallowLoaded = value; } }
    }

    /// <summary>为 true 时收到合法的换房命令只记账、什么都不回(模拟换房回应丢了)。</summary>
    public bool SwallowMoves
    {
        get { lock (_gate) { return _swallowMoves; } }
        set { lock (_gate) { _swallowMoves = value; } }
    }

    public bool Cleared { get { lock (_gate) { return _cleared; } } }

    public bool Disconnected { get { lock (_gate) { return _disconnected; } } }

    /// <summary>被装载超时踢出了。</summary>
    public bool Kicked { get { lock (_gate) { return _kicked; } } }

    /// <summary>装载超时定时器还在跑(这个房间的装载完成命令还没收到)。</summary>
    public bool LoadingPending { get { lock (_gate) { return _loadingPending; } } }

    public int PrematureSettlements { get { lock (_gate) { return _prematureSettlements; } } }

    public int Settlements { get { lock (_gate) { return _settlements; } } }

    public int Refusals { get { lock (_gate) { return _refusals; } } }

    /// <summary>被忽略的装载完成命令数(定时器没在跑时收到的)。</summary>
    public int IgnoredLoadReports { get { lock (_gate) { return _ignoredLoads; } } }

    /// <summary>当前房间还剩的怪数(含只有服务端知道的)。</summary>
    public int Remaining { get { lock (_gate) { return _table.Count; } } }

    /// <summary>按发生顺序的流水(快照):kill:编号、refuse:编号、move:r当前:okrN / errN / disconnect、loaded:rN、loaded:ignored:rN、clear、roomclear:rN、premature、settle、kick。</summary>
    public string[] Journal { get { lock (_gate) { return _journal.ToArray(); } } }

    /// <summary>收到的每条击杀命令(解读后,快照)。</summary>
    public KillView[] Kills { get { lock (_gate) { return _kills.ToArray(); } } }

    /// <summary>收到的每条换房命令(读满 43 字节的,解读后,快照)。</summary>
    public MoveRoomView[] Moves { get { lock (_gate) { return _moves.ToArray(); } } }

    /// <summary>收到的每条装载完成命令(读满 8 字节的,解读后,快照)。</summary>
    public RoomLoadedView[] Loads { get { lock (_gate) { return _loads.ToArray(); } } }

    /// <summary>某类反作弊计数累计了几次。</summary>
    public int Hack(int type)
    {
        lock (_gate)
        {
            return _hack.TryGetValue(type, out int n) ? n : 0;
        }
    }

    /// <summary>把一个位置登记成"过门位置"(第二次起的换房位置在集合里才不记 0x1f7)。</summary>
    public void AllowGatePosition(int x, int y)
    {
        lock (_gate)
        {
            _gatePositions.Add((x, y));
        }
    }

    /// <summary>
    /// 只有服务端知道的怪消失了(例如队友打死了):当前房间的表里去掉它们。表因此空了的话,最后一间会发清图通知(返回给测试去灌),其余房间只记账。
    /// </summary>
    public List<CombatKit.ServerFrame> RemoveHidden()
    {
        lock (_gate)
        {
            var frames = new List<CombatKit.ServerFrame>();
            foreach (int id in _hidden.ToArray())
            {
                _table.Remove(id);
            }
            _hidden.Clear();
            _journal.Add("hidden-gone:r" + _room);
            AfterTableChange(frames);
            return frames;
        }
    }

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

    /// <summary>装载超时定时器到点:还没收到装载完成命令的成员被踢出(连接断开)。</summary>
    public void ExpireLoadingTimer()
    {
        lock (_gate)
        {
            if (_loadingPending && !_disconnected)
            {
                _kicked = true;
                _disconnected = true;
                _journal.Add("kick:r" + _room);
            }
        }
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
            switch (id)
            {
                case 42:
                    HandleKill(payload, replies);
                    break;
                case 48:
                    HandleMove(payload, replies);
                    break;
                case 40:
                    HandleLoaded(payload, replies);
                    break;
                case 49:
                    HandleSettlement(payload, replies);
                    break;
            }
            return replies;
        }
    }

    private bool IsLastRoom => _room == _rooms.Count - 1;

    private void LoadRoom(int index)
    {
        _room = index;
        _table.Clear();
        _hidden.Clear();
        foreach (int id in _rooms[index].Ids)
        {
            _table.Add(id);
        }
        foreach (int id in _rooms[index].Hidden)
        {
            _table.Add(id);
            _hidden.Add(id);
        }
    }

    private void AddHack(int type)
    {
        _hack.TryGetValue(type, out int n);
        _hack[type] = n + 1;
    }

    // 表变化之后:空了且是最后一间 → 清图(除非扣着);空了但不是最后一间 → 只记账,不发任何通知
    private void AfterTableChange(List<CombatKit.ServerFrame> replies)
    {
        if (_table.Count != 0)
        {
            return;
        }
        if (!IsLastRoom)
        {
            _journal.Add("roomclear:r" + _room);
            return;
        }
        if (!_withholdClear)
        {
            _cleared = true;
            _journal.Add("clear");
            replies.Add(CombatKit.Clear());
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
        if (_table.Remove(view.MobId))
        {
            _journal.Add("kill:" + view.MobId);
            replies.Add(CombatKit.Confirm(view.MobId));
            AfterTableChange(replies);
        }
        else
        {
            _refusals++;
            _journal.Add("refuse:" + view.MobId);
            replies.Add(CombatKit.Refusal(view.MobId));
        }
    }

    private void HandleMove(byte[] payload, List<CombatKit.ServerFrame> replies)
    {
        if (payload.Length < 43)
        {
            _disconnected = true;
            _journal.Add("move:short:disconnect");
            return;
        }
        MoveRoomView view = MoveRoomView.Parse(payload.Take(43).ToArray());
        _moves.Add(view);

        // check_error:目标格不小于迷宫宽或高 → 断线
        if (view.TargetX < 0 || view.TargetY < 0 || view.TargetX >= MazeWidth || view.TargetY >= MazeHeight)
        {
            _disconnected = true;
            _journal.Add("move:r" + _room + ":oversize:disconnect");
            return;
        }

        // 只记计数的反作弊检查(在 move_map 之前,所以被拒的换房也计)
        if (view.TrapBits % 7 != 0)
        {
            AddHack(HackTrapBits);
        }
        foreach (ushort seat in view.Seats)
        {
            if (seat == 0)
            {
                AddHack(HackSeatZero);
            }
        }
        bool first = _moveCalls == 0;
        _moveCalls++;
        if (!first && !_gatePositions.Contains((view.PositionX, view.PositionY)))
        {
            AddHack(HackPathPosition);
        }

        // CParty::move_map 的次序
        if (_cleared)
        {
            Refuse(replies, ErrDungeonCleared);
            return;
        }
        if (_table.Count != 0)
        {
            Refuse(replies, ErrRoomNotCleared);
            return;
        }
        RoomPlan here = _rooms[_room];
        if (view.TargetX == here.CellX && view.TargetY == here.CellY)
        {
            Refuse(replies, ErrSameCell);
            return;
        }
        if (Math.Abs(view.TargetX - here.CellX) + Math.Abs(view.TargetY - here.CellY) != 1 || IsLastRoom
            || view.TargetX != _rooms[_room + 1].CellX || view.TargetY != _rooms[_room + 1].CellY)
        {
            Refuse(replies, ErrNotAdjacent);
            return;
        }

        int from = _room;
        if (_swallowMoves)
        {
            _journal.Add("move:r" + from + ":swallowed");
            return;
        }
        LoadRoom(_room + 1);
        _loadingPending = true;
        _journal.Add("move:r" + from + ":ok:r" + _room);
        RoomPlan next = _rooms[_room];
        replies.Add(RoomKit.StartMap(next.Ids, next.CellX, next.CellY, 905 + _room));
    }

    private void Refuse(List<CombatKit.ServerFrame> replies, int code)
    {
        _journal.Add("move:r" + _room + ":err" + code);
        replies.Add(RoomKit.MoveRefusal(code));
    }

    private void HandleLoaded(byte[] payload, List<CombatKit.ServerFrame> replies)
    {
        if (payload.Length < 8)
        {
            _disconnected = true;
            _journal.Add("loaded:short:disconnect");
            return;
        }
        _loads.Add(RoomLoadedView.Parse(payload.Take(8).ToArray()));
        if (!_loadingPending)
        {
            _ignoredLoads++;
            _journal.Add("loaded:ignored:r" + _room);
            return;
        }
        _loadingPending = false;
        _journal.Add("loaded:r" + _room);
        if (!_swallowLoaded)
        {
            replies.Add(RoomKit.Loaded());
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

/// <summary>状态机层的小装置:合成起始地图 + 记录用发送口 + 状态机,带喂帧与换房的快捷方法(多房间版的 <see cref="CombatRig"/>)。</summary>
internal sealed class RoomRig
{
    public RoomRig(IReadOnlyList<int> serverIds, OnlineDungeonCombatOptions options = null, int cellX = 3, int cellY = 4)
    {
        Ids = serverIds.ToArray();
        Map = RoomKit.Map(serverIds, cellX, cellY, 905);
        Port = new RecordingCombatPort();
        Machine = new OnlineDungeonCombat(Map, Port, options);
    }

    public RoomRig(int count, OnlineDungeonCombatOptions options = null)
        : this(CombatKit.Sequential(count), options)
    {
    }

    public int[] Ids { get; }

    public StartMapNotice Map { get; }

    public RecordingCombatPort Port { get; }

    public OnlineDungeonCombat Machine { get; }

    public Task<OnlineCombatSendResult> Kill(int local) => Machine.ReportKillAsync(local);

    public OnlineCombatFrameVerdict Feed(CombatKit.ServerFrame frame) => Machine.OnFrame((PacketClass)frame.Class, frame.Id, frame.Payload);

    public OnlineCombatFrameVerdict Confirm(int serverId) => Feed(CombatKit.Confirm(serverId));

    /// <summary>当前房间的怪全部报击杀并确认(用状态机自己的对应表里的服务端编号)。</summary>
    public async Task KillAndConfirmAll()
    {
        foreach (OnlineCombatMonster m in Machine.Monsters.ToArray())
        {
            if (m.DuplicateNumber)
            {
                continue;
            }
            OnlineCombatSendResult sent = await Machine.ReportKillAsync(m.LocalId);
            Assert.True(sent.Sent, "本地编号 " + m.LocalId + " 应当发出击杀命令,实际 " + sent.Outcome + "/" + sent.Block);
            Assert.Equal(OnlineCombatFrameVerdict.Accepted, Confirm(m.ServerId));
        }
    }

    /// <summary>发一次换房请求(针对当前房间),返回结果。</summary>
    public Task<OnlineCombatSendResult> Move(int x = 4, int y = 4, int positionX = 0, int positionY = 0)
    {
        return Machine.RequestMoveRoomAsync(RoomKit.Move(x, y, positionX, positionY));
    }

    /// <summary>
    /// 走一遍成功换房:杀光当前房间、发换房、喂新房间的起始地图通知;断言每一步都对,返回新房间的怪编号。
    /// 装载握手由调用方接着做(<see cref="Handshake"/>)。
    /// </summary>
    public async Task<int[]> MoveToNextRoom(IReadOnlyList<int> nextIds, int cellX = 4, int cellY = 4)
    {
        await KillAndConfirmAll();
        OnlineCombatSendResult sent = await Move(cellX, cellY);
        Assert.True(sent.Sent, "换房应当发出,实际 " + sent.Outcome + "/" + sent.Block);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, Feed(RoomKit.StartMap(nextIds, cellX, cellY)));
        return nextIds.ToArray();
    }

    /// <summary>走一遍装载握手:发装载完成命令、收装载完成通知;断言每一步都对。</summary>
    public async Task Handshake()
    {
        OnlineCombatSendResult sent = await Machine.ReportRoomLoadedAsync();
        Assert.True(sent.Sent, "装载完成命令应当发出,实际 " + sent.Outcome + "/" + sent.Block);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, Feed(RoomKit.Loaded()));
        Assert.Equal(OnlineRoomLoadState.None, Machine.LoadState);
    }

    /// <summary>发送记录里某个包号的命令条数。</summary>
    public int Commands(int id) => Port.Sent.Count(s => s.Class == PacketClass.Cmd && s.Id == id);

    public int Count(OnlineCombatEventKind kind) => Machine.Events.Count(e => e.Kind == kind);

    /// <summary>发送记录(类 1 的包号序列)。</summary>
    public int[] Wire() => Port.Sent.Select(s => (int)s.Id).ToArray();
}
