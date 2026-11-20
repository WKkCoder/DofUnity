using System.Collections.Concurrent;
using Dnf.Data;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器"缺口补测"(OnlineDungeonFlowGap*)共用夹具:从 <c>OnlineDungeonFlowTests</c> 里那几个私有嵌套类<b>复制</b>而来(不改原文件),
/// 并在上面加了几处只为"可观察"而设的钩子——端口事件的订阅/退订计数、进入/释放收包权的可阻塞钩子、接缝会话的"切帧出错"开关、
/// 以及一个把"建 GameFlow + 控制器 + 假端口 + 日志捕获 + 逻辑时钟"一次组好的 <see cref="Rig"/>。全部是合成数据(副本编号 9101/9102、假路径),不含任何真实数值。
/// </summary>
internal static class OnlineDungeonFlowGapKit
{
    public const int DungeonAId = 9101;
    public const int DungeonBId = 9102;
    public const int ReturnTownId = 1;
    public const int ReturnAreaId = 1;

    // ------------------------------------------------------------------------------------------------
    // 城镇世界替身(复制)
    // ------------------------------------------------------------------------------------------------

    internal sealed class StubHero : IHeroSession
    {
        public string Label { get; }
        public StubHero(string label) => Label = label;
        public string MapPath => "fake/" + Label;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    internal sealed class TestTownWorld : ITownWorld
    {
        public TestTownWorld(TownPlace start, TownPlace ret)
        {
            Start = start;
            Return = ret;
        }

        public TownPlace Start { get; }
        public TownPlace Return { get; }
        public List<TownPlace> CreatedPlaces { get; } = new List<TownPlace>();

        public IHeroSession Create(TownPlace place)
        {
            CreatedPlaces.Add(place);
            return new StubHero("t" + place.Town + "/" + place.Area);
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool heroFacingLeft) => null;

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            exit = default;
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 会话端口替身(复制 + 钩子)
    // ------------------------------------------------------------------------------------------------

    internal sealed class FakeSessionPort : IOnlineDungeonSessionPort
    {
        private readonly ConcurrentQueue<string> _timeline = new ConcurrentQueue<string>();
        private readonly List<(PacketClass Class, int PacketId, byte[] Payload)> _commands =
            new List<(PacketClass, int, byte[])>();
        private readonly object _sync = new object();
        private ScriptedNetSession _currentNetSession;
        private Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _responder;
        private Action<PacketClass, int, byte[]> _frameReceived;
        private Action<OnlineDisconnectReason> _disconnected;
        private int _frameAdds;
        private int _frameRemoves;
        private int _disconnectedAdds;
        private int _disconnectedRemoves;
        private int _createRunnerCalls;

        public bool IsDisconnected { get; set; }
        public int EnterExclusiveCalls { get; private set; }
        public int LeaveExclusiveCalls { get; private set; }
        public bool LeaveExclusiveThrowsDisposed { get; set; }
        public OnlineClientSession RawSession => null;

        /// <summary>替换"进入独占收包"的行为(拿到运行令牌);null 是缺省:令牌已取消就抛,否则立刻完成。</summary>
        public Func<CancellationToken, Task> EnterExclusiveBehavior { get; set; }

        /// <summary>替换"离开独占收包"的行为;null 是缺省:立刻完成。</summary>
        public Func<CancellationToken, Task> LeaveExclusiveBehavior { get; set; }

        public int FrameReceivedAdds => Volatile.Read(ref _frameAdds);
        public int FrameReceivedRemoves => Volatile.Read(ref _frameRemoves);
        public int DisconnectedAdds => Volatile.Read(ref _disconnectedAdds);
        public int DisconnectedRemoves => Volatile.Read(ref _disconnectedRemoves);
        public int CreateRunnerCalls => Volatile.Read(ref _createRunnerCalls);

        /// <summary>最近一次造出来的跑器所用的接缝会话(还没造过是 null)。</summary>
        public ScriptedNetSession CurrentNet => _currentNetSession;

        public IReadOnlyList<(PacketClass Class, int PacketId, byte[] Payload)> Commands
        {
            get
            {
                lock (_sync)
                {
                    return _commands.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Timeline => _timeline.ToArray();

        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add
            {
                _frameReceived += value;
                Interlocked.Increment(ref _frameAdds);
            }
            remove
            {
                _frameReceived -= value;
                Interlocked.Increment(ref _frameRemoves);
            }
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add
            {
                _disconnected += value;
                Interlocked.Increment(ref _disconnectedAdds);
            }
            remove
            {
                _disconnected -= value;
                Interlocked.Increment(ref _disconnectedRemoves);
            }
        }

        public void RecordTimeline(string mark) => _timeline.Enqueue(mark);

        public void SetResponder(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> responder)
        {
            _responder = responder;
        }

        public Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            if (EnterExclusiveBehavior != null)
            {
                return EnterExclusiveBehavior(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnterExclusiveCalls++;
            _timeline.Enqueue("EnterExclusive#" + EnterExclusiveCalls);
            return Task.CompletedTask;
        }

        public Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            if (LeaveExclusiveBehavior != null)
            {
                return LeaveExclusiveBehavior(cancellationToken);
            }
            if (LeaveExclusiveThrowsDisposed)
            {
                throw new ObjectDisposedException("OnlineClientSession");
            }
            LeaveExclusiveCalls++;
            _timeline.Enqueue("LeaveExclusive#" + LeaveExclusiveCalls);
            return Task.CompletedTask;
        }

        public OnlineDungeonRunner CreateDungeonRunner()
        {
            Interlocked.Increment(ref _createRunnerCalls);
            var net = new ScriptedNetSession((cls, id, payload) =>
            {
                lock (_sync)
                {
                    _commands.Add((cls, id, payload));
                }
                return _responder != null ? _responder(cls, id, payload) : Array.Empty<CombatKit.ServerFrame>();
            });
            _currentNetSession = net;
            return new OnlineDungeonRunner(net, keepAlive: null);
        }

        /// <summary>替身自己计的"进入独占收包"次数(给替换了 <see cref="EnterExclusiveBehavior"/> 的测试用)。</summary>
        public void CountEnterExclusive()
        {
            EnterExclusiveCalls++;
            _timeline.Enqueue("EnterExclusive#" + EnterExclusiveCalls);
        }

        /// <summary>替身自己计的"离开独占收包"次数。</summary>
        public void CountLeaveExclusive()
        {
            LeaveExclusiveCalls++;
            _timeline.Enqueue("LeaveExclusive#" + LeaveExclusiveCalls);
        }

        public void PushFrame(PacketClass cls, int packetId, byte[] payload)
        {
            _frameReceived?.Invoke(cls, packetId, payload);
        }

        /// <summary>与真断线一致:置断线标志、关接缝会话的对端、再触发 Disconnected 事件。</summary>
        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _currentNetSession?.ClosePeer();
            _disconnected?.Invoke(reason);
        }

        /// <summary>只触发 Disconnected 事件,不置端口的断线标志、也不关接缝会话(控制器自己记下的 <c>_disconnected</c> 为真,而端口还说"没断")。</summary>
        public void RaiseDisconnectedEventOnly(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            _disconnected?.Invoke(reason);
        }
    }

    internal sealed class ScriptedNetSession : IOnlineDungeonSession
    {
        private readonly Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _onSend;
        private readonly ConcurrentQueue<DecodedFrame> _inbound = new ConcurrentQueue<DecodedFrame>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private volatile FrameError _frameError;

        public ScriptedNetSession(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> onSend)
        {
            _onSend = onSend;
        }

        public bool IsReady => !PeerClosed;
        public bool PeerClosed { get; private set; }
        public FrameError LastFrameError => _frameError;

        /// <summary>
        /// 收帧闸门:非 null 时,每次 <see cref="ReceiveAsync"/> 先在这里停住(<b>故意不看取消令牌</b>),直到测试放行。
        /// 用来把"战斗任务还没收场"这一状态钉死,不靠线程调度的快慢——取消令牌被取消时,等待信号量的收帧会就地(同一线程)完成,
        /// 战斗任务也就跟着同步收场,测试看不到"停战中"这个阶段;停在闸门上就不会。
        /// </summary>
        public TaskCompletionSource<bool> ReceiveGate { get; set; }

        private readonly TaskCompletionSource<bool> _gateEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>等收帧第一次停在 <see cref="ReceiveGate"/> 上(确认战斗循环已经停在闸门上);超时返回 false。</summary>
        public bool WaitForReceiveGate(int timeoutMs)
        {
            return _gateEntered.Task.Wait(timeoutMs);
        }

        public void Enqueue(CombatKit.ServerFrame frame)
        {
            _inbound.Enqueue(frame.Decoded());
            _signal.Release();
        }

        public void ClosePeer()
        {
            PeerClosed = true;
            _signal.Release();
        }

        /// <summary>模拟入站字节流切帧出错(会话不再可用,收帧返回空)。</summary>
        public void FaultFraming(FrameError error = FrameError.LengthAboveMaximum)
        {
            _frameError = error;
            _signal.Release();
        }

        public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PeerClosed)
            {
                return Task.FromException<FrameStatus>(new IOException("closed"));
            }
            IEnumerable<CombatKit.ServerFrame> replies = _onSend(packetClass, packetId, payload ?? Array.Empty<byte>());
            if (replies != null)
            {
                foreach (CombatKit.ServerFrame reply in replies)
                {
                    _inbound.Enqueue(reply.Decoded());
                    _signal.Release();
                }
            }
            return Task.FromResult(FrameStatus.Ok);
        }

        public async Task<List<DecodedFrame>> ReceiveAsync(TimeSpan timeout, int maxFrames = 16, CancellationToken cancellationToken = default)
        {
            var list = new List<DecodedFrame>();
            TaskCompletionSource<bool> gate = ReceiveGate;
            if (gate != null)
            {
                _gateEntered.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
            }
            if (!_inbound.TryDequeue(out DecodedFrame first))
            {
                if (PeerClosed || _frameError != FrameError.None)
                {
                    return list;
                }
                await _signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!_inbound.TryDequeue(out first))
                {
                    return list;
                }
            }
            list.Add(first);
            while (list.Count < maxFrames && _inbound.TryDequeue(out DecodedFrame more))
            {
                list.Add(more);
            }
            return list;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 载荷构造(复制)
    // ------------------------------------------------------------------------------------------------

    public static byte[] ValidSettlementABytes(byte rankValue = 7, int clearTime = 42310, sbyte timeBonus = 18, sbyte baseScore = 76)
    {
        return new NoticePayloadBuilder()
            .U8(rankValue)
            .I32(clearTime)
            .U8(unchecked((byte)timeBonus))
            .U8(unchecked((byte)baseScore))
            .U8(0) // questMazeFlag
            .U8(1) // 1 entry
            .U16(9501).I32(clearTime).U8(1)
            .ToArray();
    }

    public static byte[] ValidSettlementBBytes(int baseExp = 12345, int rankBonusExp = 678, int goldSlot0 = 450, int itemId = 9801)
    {
        var b = new NoticePayloadBuilder()
            .I32(baseExp)
            .I32(rankBonusExp)
            .I32(0)
            .I32(0)
            .U8(1); // expKind
        for (int i = 0; i < 12; i++)
        {
            b.I32(i * 10);
        }
        b.U8(0); // questItemCount = 0
        // slot 0: status=2 item reward; slots 1..3: status=0
        b.U8(2).I32(0).I32(goldSlot0).I32(itemId).I32(3);
        b.U8(0).U8(0).U8(0);
        b.I32(99); // receiverValue
        b.U8(0).U8(0).U8(0).U8(0);
        b.U8(0).U8(0).U8(0).U8(0); // premiumRewards
        b.I32(100); // percent
        b.U8(0).U8(1).U8(0); // flags
        return b.ToArray();
    }

    public static byte[] PermissionPayload(params (int DungeonId, byte Tier)[] entries)
    {
        var b = new NoticePayloadBuilder().U16(entries.Length);
        foreach ((int id, byte tier) in entries)
        {
            b.U16(id).U8(tier);
        }
        return b.ToArray();
    }

    public static byte[] UserAreaBytes(byte village, byte area)
    {
        return new NoticePayloadBuilder().U16(901).U8(village).U8(area).U16(300).U16(200).U8(0).U8(1).ToArray();
    }

    public static byte[] AreaUsersBytes(byte village, byte area)
    {
        return new NoticePayloadBuilder().U8(village).U8(area).U16(0).ToArray();
    }

    /// <summary>标准的进副本回包:"开始游戏"回装载通知 27。</summary>
    public static CombatKit.ServerFrame[] LoadingFrames()
    {
        return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
    }

    /// <summary>标准的进副本回包:"选副本"回副本信息 28 + 起始地图 29(<paramref name="monsters"/> 只怪)。</summary>
    public static CombatKit.ServerFrame[] InfoAndMapFrames(int dungeonId, int monsters)
    {
        return new[]
        {
            new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(dungeonId)),
            new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(monsters), 0, 0, 905)),
        };
    }

    /// <summary>标准的结算回包:0x22 + 0x23。</summary>
    public static CombatKit.ServerFrame[] SettlementFrames(byte rank = 9, int clearTime = 31500, int baseExp = 22222, int rankBonusExp = 3333, int gold = 777)
    {
        return new[]
        {
            new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, ValidSettlementABytes(rankValue: rank, clearTime: clearTime)),
            new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, ValidSettlementBBytes(baseExp: baseExp, rankBonusExp: rankBonusExp, goldSlot0: gold)),
        };
    }

    public static RoomMonsterSpawn[] Templates(int count)
    {
        return Enumerable.Range(0, count).Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320 + 40 * i)).ToArray();
    }

    /// <summary>一个什么都没有的城镇区域(普通区,不是副本门区)。</summary>
    public static TownAreaInfo NormalArea()
    {
        return new TownAreaInfo(
            ReturnTownId,
            new TownArea(ReturnAreaId, "map/test/normal.map", TownAreaKind.Normal, Array.Empty<int>(), null, false),
            "map/test/normal.map",
            Array.Empty<WorldRect>(),
            Array.Empty<TownExit>());
    }

    /// <summary>进副本两步命令的标准回包:"开始游戏"回 27,"选副本"回 28+29;别的命令返回 null(交给调用方接着判断)。</summary>
    public static CombatKit.ServerFrame[] EntryReply(PacketClass cls, int packetId, int dungeonId = DungeonAId, int monsters = 2)
    {
        if (cls != PacketClass.Cmd)
        {
            return null;
        }
        if (packetId == OnlineDungeonEntry.StartGamePacketId)
        {
            return LoadingFrames();
        }
        if (packetId == OnlineDungeonEntry.SelectDungeonPacketId)
        {
            return InfoAndMapFrames(dungeonId, monsters);
        }
        return null;
    }

    private static ScriptLine ScriptLineOf(string tag, params object[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = values[i] is int iv ? new ScriptValue(ScriptValueKind.Int, iv, null) : new ScriptValue(ScriptValueKind.String, 0, (string)values[i]);
        }
        return new ScriptLine(tag, list);
    }

    /// <summary>只有一座城镇、几块普通区域的合成图册(都没有出口与可走区域):够 <c>TryMapToServerArea</c> 判"这个区域登记过"。</summary>
    public static TownAtlas AtlasWith(int town, params int[] areaIds)
    {
        string twn = "town/k" + town + ".twn";
        var paths = new Dictionary<int, string> { [town] = twn };
        var scripts = new Dictionary<string, ScriptLine[]>();
        var twnLines = new List<ScriptLine>();
        foreach (int areaId in areaIds)
        {
            twnLines.Add(ScriptLineOf("[area]", areaId, "K" + town + "/A" + areaId + ".map", "[normal]"));
            scripts["map/k" + town + "/a" + areaId + ".map"] = Array.Empty<ScriptLine>();
        }
        scripts[twn] = twnLines.ToArray();
        return new TownAtlas(paths, p => scripts.TryGetValue(p, out ScriptLine[] doc) ? doc : null, _ => false);
    }

    /// <summary>
    /// 把场景台摆成"能进一间真房间的副本":入口两步命令按标准回包,放弃回城(1/45)交给 <paramref name="onGiveUp"/>(null 就当没收到),
    /// 其余命令交给战斗假服务端(<paramref name="monsters"/> 只怪);建房工厂造一间单房间(合成房间夹具,怪数同)。
    /// </summary>
    public static void ServeRoomDungeon(Rig rig, int monsters = 2, Func<byte[], IEnumerable<CombatKit.ServerFrame>> onGiveUp = null)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsters));
        rig.Port.SetResponder((cls, id, payload) =>
        {
            CombatKit.ServerFrame[] entry = EntryReply(cls, id, DungeonAId, monsters);
            if (entry != null)
            {
                return entry;
            }
            if (cls == PacketClass.Cmd && id == SessionLifecyclePayloads.GiveUpGamePacketId)
            {
                return onGiveUp != null ? onGiveUp(payload) : Array.Empty<CombatKit.ServerFrame>();
            }
            return combatServer.Handle(cls, id, payload);
        });
        rig.RoomBuilder = ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(monsters), plan, Array.Empty<GateSide>()));
            return OnlineDungeonRoomBuildResult.Success(room, run);
        };
    }

    // ------------------------------------------------------------------------------------------------
    // 一次组好的场景台
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 场景台:GameFlow(副本 A 档位 3、副本 B 档位 1)+ 假端口 + 控制器 + 日志捕获 + 逻辑时钟 <see cref="NowMs"/>。
    /// 默认的建房工厂返回 <c>Failure(NoRoom)</c>(要真房间的测试把 <see cref="RoomBuilder"/> 换掉);所有时间都走 <see cref="NowMs"/>,不读墙钟。
    /// </summary>
    internal sealed class Rig : IDisposable
    {
        private readonly List<string> _logs = new List<string>();
        private HostedGatedRoomFixture _fixture;

        public GameFlow Flow { get; }
        public TestTownWorld World { get; }
        public DungeonEntry[] Entries { get; }
        public OnlineDungeonPermissionTable Table { get; }
        public OnlineDungeonSelectPolicy Policy { get; }
        public FakeSessionPort Port { get; } = new FakeSessionPort();
        public OnlineDungeonFlowOptions Options { get; }
        public OnlineDungeonFlowController Ctrl { get; }

        /// <summary>逻辑时钟(毫秒):<see cref="PumpUntil"/> 每圈加 20,<see cref="Poll"/> 用当前值。</summary>
        public long NowMs;

        /// <summary><see cref="OnlineDungeonFlowOptions.OnDisconnected"/> 被调用的次数。</summary>
        public int Disconnects;

        /// <summary>建房工厂的调用次数。</summary>
        public int BuilderCalls;

        /// <summary>可换的建房工厂;缺省 <c>Failure(NoRoom)</c>。</summary>
        public Func<OnlineDungeonRoomBuildContext, OnlineDungeonRoomBuildResult> RoomBuilder =
            _ => OnlineDungeonRoomBuildResult.Failure(OnlineRoomStatus.NoRoom);

        /// <summary>最近一次建房上下文(建房工厂被调用过才有)。</summary>
        public OnlineDungeonRoomBuildContext LastContext;

        public HostedGatedRoomFixture Fixture => _fixture ??= new HostedGatedRoomFixture();

        public Rig(
            Action<OnlineDungeonFlowOptions> configure = null,
            DungeonEntry[] entries = null,
            Func<OnlineDungeonPermissionTable, OnlineDungeonSelectPolicy> makePolicy = null)
        {
            Table = new OnlineDungeonPermissionTable();
            Table.LoadInitial(new[]
            {
                new KeyValuePair<int, byte>(DungeonAId, 3),
                new KeyValuePair<int, byte>(DungeonBId, 1),
            }, hasSnapshot: true);

            Entries = entries ?? new[]
            {
                new DungeonEntry("dungeon/test/dunga.dgn", "DUNGA", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
                new DungeonEntry("dungeon/test/dungb.dgn", "DUNGB", new DungeonCard(DungeonBId, 200, 200, 0, null, 0, 1)),
            };

            World = new TestTownWorld(new TownPlace(1, 0), new TownPlace(ReturnTownId, ReturnAreaId));
            Flow = new GameFlow(World, (path, _) => throw new InvalidOperationException("offline factory should not run"), Entries);
            Policy = makePolicy != null
                ? makePolicy(Table)
                : OnlineDungeonSelectPolicy.Create(Table, hasLocalData: _ => true, hasUltimate: _ => true);

            Options = new OnlineDungeonFlowOptions
            {
                CombatOptions = CombatKit.Options(pollMilliseconds: 10),
                LoadingNoticeTimeout = TimeSpan.FromMilliseconds(70),
                DungeonInfoTimeout = TimeSpan.FromMilliseconds(70),
                StartMapTimeout = TimeSpan.FromMilliseconds(70),
                Log = line => { lock (_logs) _logs.Add(line); },
                OnDisconnected = () => Interlocked.Increment(ref Disconnects),
            };
            configure?.Invoke(Options);

            Ctrl = new OnlineDungeonFlowController(Port, Flow, Policy, ctx =>
            {
                Interlocked.Increment(ref BuilderCalls);
                LastContext = ctx;
                return RoomBuilder(ctx);
            }, Options);
        }

        /// <summary>日志快照(只含控制器的 <c>DNF_ONLINE stage=dungeon</c> 行,按写入顺序)。</summary>
        public string[] Logs()
        {
            lock (_logs)
            {
                return _logs.ToArray();
            }
        }

        /// <summary>城镇 → 副本选择屏。</summary>
        public void Start()
        {
            Flow.EnterTown("boot");
            Flow.EnterSelect("gate");
        }

        /// <summary>选择屏上确认当前选中的副本并让流程接手(触发 <see cref="GameFlow.OnlineStartDungeon"/>)。</summary>
        public void ConfirmSelected()
        {
            Flow.Select.Confirm();
            Assert.True(Flow.Poll());
        }

        /// <summary>用当前逻辑时钟轮询一次控制器与流程。</summary>
        public void Poll()
        {
            Ctrl.Poll(NowMs);
            Flow.Poll();
        }

        /// <summary>
        /// 逻辑时钟每圈前进 20ms,轮询控制器与流程,直到条件成立;后台任务靠短暂让出时间片推进(只是等异步完成,不靠它排顺序)。
        /// </summary>
        public void PumpUntil(Func<bool> condition, int maxIterations = 4000)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (condition())
                {
                    return;
                }
                NowMs += 20;
                Flow.Session?.Update(20, 0, 0);
                Flow.Splash?.Advance(20);
                Flow.Select?.Advance(20);
                Ctrl.Poll(NowMs);
                Flow.Poll();
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.True(condition(), "Timed out waiting for online dungeon flow state; stage=" + Flow.Stage + " phase=" + Ctrl.Phase);
        }

        /// <summary>
        /// 同 <see cref="PumpUntil"/>,但只轮询控制器(不推进房间与流程):想让"战斗任务先收场"这个顺序确定、
        /// 不被房间每步的检查抢先判成网络中止时用。逻辑时钟照样每圈前进 20ms。
        /// </summary>
        public void PumpControllerUntil(Func<bool> condition, int maxIterations = 4000)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (condition())
                {
                    return;
                }
                NowMs += 20;
                Ctrl.Poll(NowMs);
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.True(condition(), "Timed out waiting for online dungeon controller state; stage=" + Flow.Stage + " phase=" + Ctrl.Phase);
        }

        /// <summary>等到控制器回到 Idle(各退出分支的收尾都走完)。</summary>
        public void PumpUntilIdle()
        {
            PumpUntil(() => Ctrl.Phase == OnlineDungeonFlowPhase.Idle);
        }

        /// <summary>等端口收到某条命令(类 1 命令的包号)至少 <paramref name="count"/> 次。</summary>
        public void WaitForCommand(int packetId, int count = 1)
        {
            PumpUntil(() => Port.Commands.Count(c => c.Class == PacketClass.Cmd && c.PacketId == packetId) >= count);
        }

        public void Dispose()
        {
            Ctrl.Dispose();
            _fixture?.Dispose();
        }
    }
}
