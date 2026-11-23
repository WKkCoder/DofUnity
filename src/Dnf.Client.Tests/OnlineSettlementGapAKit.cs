using System.Collections.Concurrent;
using Dnf.Game;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 缺口补测"(OnlineSettlementGapA*)共用夹具。
/// 整体是从 <c>OnlineSettlementFlowTests</c> 里那几个私有嵌套类<b>复制</b>而来(不改原文件),并加了几处只为"可观察"而设的钩子:
/// 假传输能"只触发断线事件"或"只置断线标志"、能记下控制器订阅时交来的委托(摘掉订阅之后还能手动调用);
/// 假端口能"只触发断线事件";另外多了一个只带结算控制器、不带联网进副本控制器的 <see cref="DirectRig"/>——
/// 它直接调用控制器公开的 <c>OnSettlementStarted</c>,全部时间走自己手动给的逻辑时钟,没有线程、没有墙钟,适合精确到毫秒的边界测试。
/// 全部是合成数据(编号 9101/9102/9501 等),不含任何真实数值。
/// </summary>
internal static class OnlineSettlementGapAKit
{
    public const int DungeonAId = 9101;
    public const int DungeonBId = 9102;
    public const ushort SelfUid = 9501;
    public const ushort OtherUid = 9502;
    public const int QuestItemId = 9801;
    public const int RewardItemId = 9802;

    /// <summary>结算日志前缀(与生产格式化器一致的字面量,断言日志行时在它后面接 <c>" event=..."</c>)。</summary>
    public const string P = "DNF_ONLINE stage=settle";

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

    internal sealed class RecordingTownWorld : ITownWorld
    {
        public RecordingTownWorld(TownPlace start, TownPlace ret)
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
        public bool TryRoute(TownPlace from, out TownExit exit) { exit = default; return false; }
    }

    /// <summary>不是联网房间局的另一种局会话(只为让流程停在副本阶段、而 <c>Run as HostedRoomRun</c> 为 null)。</summary>
    internal sealed class OtherRun : IRunSession
    {
        public SettlementChoice Choice => SettlementChoice.None;
        public bool Failed => false;
        public string MapPath => "fake/other-run";
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    // ------------------------------------------------------------------------------------------------
    // 结算传输替身(复制 + 钩子)
    // ------------------------------------------------------------------------------------------------

    internal sealed class FakeSettlementTransport : IOnlineSettlementTransport
    {
        private readonly object _sync = new object();
        private readonly List<(ushort PacketId, byte[] Payload)> _sent = new List<(ushort, byte[])>();
        private Func<ushort, byte[], Task<FrameStatus>> _onSend;

        public bool IsDisconnected { get; set; }
        public int FrameSubscribers { get; private set; }
        public int DisconnectSubscribers { get; private set; }
        public bool Disposed { get; private set; }

        /// <summary>最近一次订阅 <see cref="FrameReceived"/> 时交来的委托(摘掉订阅之后仍保留,用来模拟"摘掉前已在途的一帧")。</summary>
        public Action<PacketClass, int, byte[]> LastFrameHandler { get; private set; }

        public IReadOnlyList<(ushort PacketId, byte[] Payload)> Sent
        {
            get
            {
                lock (_sync)
                {
                    return _sent.ToArray();
                }
            }
        }

        public void ClearSent()
        {
            lock (_sync)
            {
                _sent.Clear();
            }
        }

        public void SetSendHandler(Func<ushort, byte[], Task<FrameStatus>> handler) => _onSend = handler;

        private Action<PacketClass, int, byte[]> _frameReceived;
        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add
            {
                _frameReceived += value;
                FrameSubscribers++;
                LastFrameHandler = value;
            }
            remove
            {
                _frameReceived -= value;
                FrameSubscribers--;
            }
        }

        private Action<OnlineDisconnectReason> _disconnected;
        public event Action<OnlineDisconnectReason> Disconnected
        {
            add { _disconnected += value; DisconnectSubscribers++; }
            remove { _disconnected -= value; DisconnectSubscribers--; }
        }

        public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken)
        {
            byte[] copy = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
            lock (_sync)
            {
                _sent.Add((packetId, copy));
            }
            if (_onSend != null)
            {
                return _onSend(packetId, copy);
            }
            return Task.FromResult(FrameStatus.Ok);
        }

        public void Push(PacketClass cls, int packetId, byte[] payload)
        {
            _frameReceived?.Invoke(cls, packetId, payload ?? Array.Empty<byte>());
        }

        /// <summary>与真断线一致:置断线标志再触发事件(两个信号同时到)。</summary>
        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _disconnected?.Invoke(reason);
        }

        /// <summary>只触发 <see cref="Disconnected"/> 事件,不置 <see cref="IsDisconnected"/>(两个信号里只来一个)。</summary>
        public void RaiseDisconnectedEventOnly(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            _disconnected?.Invoke(reason);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 联网进副本会话端口替身(复制 + 钩子)
    // ------------------------------------------------------------------------------------------------

    internal sealed class FakeDungeonPort : IOnlineDungeonSessionPort
    {
        private readonly object _sync = new object();
        private readonly List<(PacketClass Class, int PacketId, byte[] Payload)> _commands =
            new List<(PacketClass, int, byte[])>();
        private Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _responder;
        private ScriptedNetSession _currentNet;

        public bool IsDisconnected { get; set; }
        public int EnterExclusiveCalls { get; private set; }
        public int LeaveExclusiveCalls { get; private set; }
        public OnlineClientSession RawSession => null;

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

        public event Action<PacketClass, int, byte[]> FrameReceived;
        public event Action<OnlineDisconnectReason> Disconnected;

        public void SetResponder(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> responder) => _responder = responder;

        public Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterExclusiveCalls++;
            return Task.CompletedTask;
        }

        public Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            LeaveExclusiveCalls++;
            return Task.CompletedTask;
        }

        public OnlineDungeonRunner CreateDungeonRunner()
        {
            var net = new ScriptedNetSession((cls, id, payload) =>
            {
                byte[] copy = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
                lock (_sync)
                {
                    _commands.Add((cls, id, copy));
                }
                return _responder != null ? _responder(cls, id, copy) : Array.Empty<CombatKit.ServerFrame>();
            });
            _currentNet = net;
            return new OnlineDungeonRunner(net, keepAlive: null);
        }

        public void PushFrame(PacketClass cls, int packetId, byte[] payload) => FrameReceived?.Invoke(cls, packetId, payload);

        /// <summary>与真断线一致:置断线标志、关接缝会话的对端、再触发事件(两个信号同时到)。</summary>
        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _currentNet?.ClosePeer();
            Disconnected?.Invoke(reason);
        }

        /// <summary>只触发 <see cref="Disconnected"/> 事件:不置 <see cref="IsDisconnected"/>、也不关接缝会话(两个信号里只来一个)。</summary>
        public void RaiseDisconnectedEventOnly(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            Disconnected?.Invoke(reason);
        }
    }

    internal sealed class ScriptedNetSession : IOnlineDungeonSession
    {
        private readonly Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _onSend;
        private readonly ConcurrentQueue<DecodedFrame> _inbound = new ConcurrentQueue<DecodedFrame>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

        public ScriptedNetSession(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> onSend) => _onSend = onSend;
        public bool IsReady => !PeerClosed;
        public bool PeerClosed { get; private set; }
        public FrameError LastFrameError => FrameError.None;

        public void ClosePeer()
        {
            PeerClosed = true;
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
                foreach (CombatKit.ServerFrame f in replies)
                {
                    _inbound.Enqueue(f.Decoded());
                    _signal.Release();
                }
            }
            return Task.FromResult(FrameStatus.Ok);
        }

        public async Task<List<DecodedFrame>> ReceiveAsync(TimeSpan timeout, int maxFrames = 16, CancellationToken cancellationToken = default)
        {
            var list = new List<DecodedFrame>();
            if (!_inbound.TryDequeue(out DecodedFrame first))
            {
                if (PeerClosed)
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

    public static byte[] ValidSettlementABytes(byte rankValue = 0, int clearTime = 41200)
    {
        return new NoticePayloadBuilder()
            .U8(rankValue)
            .I32(clearTime)
            .U8(15)
            .U8(0)
            .U8(0)
            .U8(1)
            .U16(SelfUid).I32(39500).U8(1)
            .ToArray();
    }

    public static byte[] ValidSettlementBBytes(int baseExp = 700, int rankBonusExp = 150, int gold = 520)
    {
        var b = new NoticePayloadBuilder()
            .I32(baseExp)
            .I32(rankBonusExp)
            .I32(88888) // unknown slot 2
            .I32(40)    // avatar
            .U8(0);
        for (int i = 0; i < 12; i++)
        {
            b.I32(0);
        }
        b.U8(1); // 1 quest item
        b.U16(0).I32(QuestItemId).I32(2).U16(0).U8(0).U16(0);
        // slot 0 reward with item, slots 1..3 empty
        b.U8(2).I32(0).I32(gold).I32(RewardItemId).I32(1);
        b.U8(0).U8(0).U8(0);
        b.I32(77777); // unknown receiverValue
        b.U8(0).U8(0).U8(0).U8(0);
        b.U8(0).U8(0).U8(0).U8(0); // premiumRewards
        b.I32(99); // unknown percent
        b.U8(0).U8(0).U8(0);
        return b.ToArray();
    }

    public static byte[] UserAreaBytes(ushort uid, byte village, byte area, ushort x, ushort y, byte dir = 0)
    {
        return new NoticePayloadBuilder().U16(uid).U8(village).U8(area).U16(x).U16(y).U8(dir).U8(1).ToArray();
    }

    public static byte[] AreaUsersBytes(byte village, byte area)
    {
        return new NoticePayloadBuilder().U8(village).U8(area).U16(0).ToArray();
    }

    public static byte[] CardRightBytes(short s0, short s1 = -1, short s2 = -1, short s3 = -1)
    {
        return new NoticePayloadBuilder()
            .U8(1)
            .U16(unchecked((ushort)s0))
            .U16(unchecked((ushort)s1))
            .U16(unchecked((ushort)s2))
            .U16(unchecked((ushort)s3))
            .ToArray();
    }

    /// <summary>"成功/执行"的 (1,75) 回复 <c>[1, 1, cmd]</c>。</summary>
    public static byte[] EplpOk(byte cmd) => new byte[] { 1, 1, cmd };

    /// <summary>"被拒"的 (1,75) 回复 <c>[0, errorCode, cmd]</c>。</summary>
    public static byte[] EplpRejected(byte errorCode, byte cmd) => new byte[] { 0, errorCode, cmd };

    /// <summary>服务端回城信号之一:(0,2) 用户信息。</summary>
    public static void PushNoti2(FakeSettlementTransport t) => t.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });

    /// <summary>给假传输装上"自动应答":72 → 回 <c>[1]</c>;73 → 回 <paramref name="cardRight"/>(null 不回);75 交给 <paramref name="onEplp"/>(null 不回)。</summary>
    public static void AutoAdvance(FakeSettlementTransport t, byte[] cardRight, Action<byte[]> onEplp = null)
    {
        t.SetSendHandler((id, payload) =>
        {
            if (id == SettlementMenuPayloads.ScoreScrollStatePacketId)
            {
                t.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == SettlementMenuPayloads.CardSelectRightStatePacketId)
            {
                if (cardRight != null)
                {
                    t.Push(PacketClass.Cmd, 73, cardRight);
                }
            }
            else if (id == SettlementMenuPayloads.EplpCommandPacketId)
            {
                onEplp?.Invoke(payload);
            }
            return Task.FromResult(FrameStatus.Ok);
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 只带结算控制器的场景台(手动逻辑时钟,无线程)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 只有 <see cref="GameFlow"/>(停在城镇 (1,3))+ <see cref="OnlineSettlementController"/> + 假传输的场景台。
    /// 结算由 <see cref="StartSettlement"/> 直接调控制器公开的 <c>OnSettlementStarted</c>(合成的结算数据,解析状态记"不适用");
    /// 控制器通知宿主的"选定"进 <see cref="Completed"/>。逻辑时钟由测试自己给:<see cref="PollAt"/> 精确到毫秒,<see cref="Poll"/> 按步长推进。
    /// 缺省超时与倒计时:菜单倒计时 5000、各回复超时 300、回城等待 300(可由 <c>configure</c> 改)。
    /// </summary>
    internal sealed class DirectRig : IDisposable
    {
        public RecordingTownWorld RawWorld { get; }
        public GameFlow Flow { get; }
        public FakeSettlementTransport Transport { get; } = new FakeSettlementTransport();
        public OnlineSettlementController Ctrl { get; }
        public List<string> Logs { get; } = new List<string>();
        public List<SettlementChoice> Completed { get; } = new List<SettlementChoice>();
        public List<OnlineSettlementReturnTarget> Targets { get; } = new List<OnlineSettlementReturnTarget>();
        public int Disconnects { get; private set; }
        public OnlineDungeonSettlementReady Ready { get; private set; }

        /// <summary>最近一次轮询用的逻辑时钟(毫秒)。</summary>
        public long NowMs { get; private set; }

        public DirectRig(Action<OnlineSettlementOptions> configure = null)
        {
            RawWorld = new RecordingTownWorld(new TownPlace(1, 3), new TownPlace(1, 1, TownEntry.Return));
            var entries = new[]
            {
                new DungeonEntry("dungeon/test/dunga.dgn", "DUNGA", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
            };
            Flow = new GameFlow(RawWorld, (_, _) => new OtherRun(), entries);
            Flow.EnterTown("boot");

            var options = new OnlineSettlementOptions
            {
                MenuCountdownMs = 5000,
                AdvanceReplyTimeoutMs = 300,
                EplpReplyTimeoutMs = 300,
                ReturnWaitTimeoutMs = 300,
                TownReturnWaitTimeoutMs = 300,
                GetSelfUniqueId = () => SelfUid,
                GetPartySlot = () => 0,
                ApplyReturnTarget = t => Targets.Add(t),
                Log = l => Logs.Add(l),
                OnDisconnected = () => Disconnects++,
                TransportFactory = _ => Transport,
            };
            configure?.Invoke(options);
            Ctrl = new OnlineSettlementController(Flow, options);
        }

        /// <summary>
        /// 开一次结算:合成结算数据交给控制器公开的 <c>OnSettlementStarted</c>;<paramref name="timedOut"/> 为真时是"结算通知超时"那种数据。
        /// 返回的交接对象的"选定"回调记进 <see cref="Completed"/>。
        /// </summary>
        public OnlineDungeonSettlementReady StartSettlement(bool timedOut = false)
        {
            var data = new OnlineDungeonSettlementData(
                DungeonAId,
                "dungeon/test/dunga.dgn",
                0,
                2,
                0,
                1,
                OnlineSettlementParseState.NotApplicable,
                null,
                OnlineSettlementParseState.NotApplicable,
                null,
                timedOut);
            Ready = new OnlineDungeonSettlementReady(data, null, c => Completed.Add(c));
            Ctrl.OnSettlementStarted(Ready);
            return Ready;
        }

        /// <summary>把逻辑时钟精确设成 <paramref name="nowMs"/> 轮询一次(步长默认 16 毫秒,只影响营地动画计时)。</summary>
        public void PollAt(long nowMs, double deltaMs = 16)
        {
            NowMs = nowMs;
            Ctrl.Poll(nowMs, deltaMs);
        }

        /// <summary>逻辑时钟前进 <paramref name="advanceMs"/> 毫秒再轮询一次。</summary>
        public void Poll(long advanceMs = 16)
        {
            PollAt(NowMs + advanceMs, advanceMs);
        }

        /// <summary>每次前进 16 毫秒地轮询,直到条件成立(最多 <paramref name="maxPolls"/> 次,不成立就断言失败)。</summary>
        public void PollUntil(Func<bool> condition, int maxPolls = 30)
        {
            for (int i = 0; i < maxPolls; i++)
            {
                if (condition())
                {
                    return;
                }
                Poll(16);
            }
            Assert.True(condition(), "PollUntil 超过 " + maxPolls + " 次仍不成立;phase=" + Ctrl.Phase);
        }

        /// <summary>日志里以 <paramref name="text"/> 开头的行数(整行精确比较用 <see cref="LogCount"/>)。</summary>
        public int LogCount(string line) => Logs.Count(l => l == line);

        public void Dispose()
        {
            Ctrl.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 带真联网进副本控制器 + 结算控制器的场景台(复制 + 钩子)
    // ------------------------------------------------------------------------------------------------

    internal sealed class SettleRig : IDisposable
    {
        public HostedGatedRoomFixture Fixture { get; } = new HostedGatedRoomFixture();
        public RecordingTownWorld RawWorld { get; }
        public OnlineSettlementTownWorld TownWorld { get; }
        public GameFlow Flow { get; }
        public DungeonEntry[] Entries { get; }
        public OnlineDungeonPermissionTable Permissions { get; } = new OnlineDungeonPermissionTable();
        public OnlineDungeonSelectPolicy Policy { get; }
        public FakeDungeonPort Port { get; } = new FakeDungeonPort();
        public FakeSettlementTransport Transport { get; } = new FakeSettlementTransport();
        public OnlineDungeonFlowController DungeonCtrl { get; }
        public OnlineSettlementController SettleCtrl { get; }
        public HeroProgress Progress { get; }
        public List<string> Logs { get; } = new List<string>();
        public List<OnlineSettlementReturnTarget> AppliedReturnTargets { get; } = new List<OnlineSettlementReturnTarget>();
        public int Disconnects { get; private set; }

        /// <summary>最近一次由建房工厂造出的联网房间(进过副本才有)。</summary>
        public OnlineDungeonRoom LastRoom { get; private set; }

        public long NowMs { get; set; } = 10000; // 故意从非零时间起点开始

        public SettleRig(
            Action<OnlineSettlementOptions> configureSettle = null,
            Action<OnlineDungeonFlowOptions> configureDungeon = null,
            bool attachSettlement = true)
        {
            RawWorld = new RecordingTownWorld(new TownPlace(1, 3), new TownPlace(1, 1, TownEntry.Return));
            TownWorld = new OnlineSettlementTownWorld(RawWorld);

            Permissions.LoadInitial(new[]
            {
                new KeyValuePair<int, byte>(DungeonAId, 1),
                new KeyValuePair<int, byte>(DungeonBId, 1),
            }, hasSnapshot: true);

            Entries = new[]
            {
                new DungeonEntry("dungeon/test/dunga.dgn", "DUNGA", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
                new DungeonEntry("dungeon/test/dungb.dgn", "DUNGB", new DungeonCard(DungeonBId, 200, 200, 0, null, 0, 1)),
            };

            Flow = new GameFlow(TownWorld, (_, _) => throw new InvalidOperationException("offline factory unused"), Entries);
            Policy = OnlineDungeonSelectPolicy.Create(Permissions, hasLocalData: _ => true, hasUltimate: _ => true);

            var expTable = new ExpTable(new[] { 1000, 3000, 6000, 12000, 25000, 50000 });
            var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50, 60 });
            Progress = new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, level: 1);

            var dungeonOpts = new OnlineDungeonFlowOptions
            {
                CombatOptions = CombatKit.Options(pollMilliseconds: 10),
                ReturnDelayMs = 20,
                Log = l => { lock (Logs) Logs.Add(l); },
                OnDisconnected = () => Disconnects++,
            };
            configureDungeon?.Invoke(dungeonOpts);

            DungeonCtrl = new OnlineDungeonFlowController(Port, Flow, Policy, ctx =>
            {
                int monsters = ctx.StartMap.Monsters.Count;
                var room = new OnlineDungeonRoom(ctx.CombatRunner);
                LastRoom = room;
                RoomMonsterSpawn[] spawns = Enumerable.Range(0, monsters)
                    .Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320 + 40 * i))
                    .ToArray();
                HostedRoomRun run = room.CreateRun(plan => Fixture.Create(spawns, plan, Array.Empty<GateSide>()), returnDelayMs: 20);
                return OnlineDungeonRoomBuildResult.Success(room, run!);
            }, dungeonOpts);

            var settleOpts = new OnlineSettlementOptions
            {
                MenuCountdownMs = 5000,
                AdvanceReplyTimeoutMs = 300,
                EplpReplyTimeoutMs = 300,
                ReturnWaitTimeoutMs = 300,
                TownReturnWaitTimeoutMs = 300,
                GetProgress = () => Progress,
                GetSelfUniqueId = () => SelfUid,
                GetPartySlot = () => 0,
                ItemNameResolver = id => id == QuestItemId ? "合成任务石" : (id == RewardItemId ? "合成奖励剑" : null),
                ApplyReturnTarget = t =>
                {
                    AppliedReturnTargets.Add(t);
                    TownWorld.ArmReturn(t);
                },
                Log = l => { lock (Logs) Logs.Add(l); },
                OnDisconnected = () => Disconnects++,
                TransportFactory = _ => Transport,
                LocalResultComputer = (data, stats, prog) =>
                {
                    var thresholds = new[] { 90, 80, 70, 60, 50, 40, 30, 20 };
                    var steps = new[] { new TimeBonusStep(0, 1), new TimeBonusStep(10000, 2), new TimeBonusStep(20000, 3) };
                    var averages = new[] { (DungeonAId, 1, new[] { 100000, 110000, 120000, 130000, 140000 }) };
                    return DungeonResult.Compute(stats, new RankSystem(thresholds, steps, averages), prog, data.DungeonId, 1, data.Difficulty, 1f);
                },
            };
            configureSettle?.Invoke(settleOpts);

            SettleCtrl = new OnlineSettlementController(Flow, settleOpts);
            if (attachSettlement)
            {
                SettleCtrl.Attach(DungeonCtrl);
            }
        }

        public void EnterTownAndDungeon(int dungeonIndex = 0)
        {
            Flow.EnterTown("boot");
            SettleCtrl.Poll(NowMs, 20); // 记录进副本前的城镇区域
            Flow.EnterSelect("gate");
            if (dungeonIndex > 0)
            {
                Flow.Select!.Move(dungeonIndex);
            }
            Flow.Select!.Confirm();
            Assert.True(Flow.Poll());
            PumpUntil(() => Flow.Stage == GameStage.Dungeon && DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon);
        }

        public void KillAllInCurrentRun()
        {
            HostedRoomRun run = Assert.IsType<HostedRoomRun>(Flow.Run);
            foreach (MonsterActor m in run.Room.Monsters.ToArray())
            {
                HostedRoomFixture.Kill(m);
            }
        }

        public void Step(long advanceMs = 20)
        {
            NowMs += advanceMs;
            Flow.Session?.Update(advanceMs, 0, 0);
            Flow.Splash?.Advance(advanceMs);
            Flow.Select?.Advance(advanceMs);
            DungeonCtrl.Poll(NowMs);
            SettleCtrl.Poll(NowMs, advanceMs);
            Flow.Poll();
        }

        /// <summary>每圈让逻辑时钟前进 20 毫秒并轮询两个控制器与流程;后台任务靠短暂让出时间片推进(只是等异步完成,不靠它排顺序)。</summary>
        public void PumpUntil(Func<bool> predicate, int maxIterations = 1500)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (predicate())
                {
                    return;
                }
                Step(20);
                if (predicate())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.True(predicate(), "Timed out; flow=" + Flow.Stage + " dungeonPhase=" + DungeonCtrl.Phase + " settlePhase=" + SettleCtrl.Phase);
        }

        public string[] LogSnapshot()
        {
            lock (Logs)
            {
                return Logs.ToArray();
            }
        }

        public void Dispose()
        {
            SettleCtrl.Dispose();
            DungeonCtrl.Dispose();
            Fixture.Dispose();
        }
    }

    /// <summary>
    /// 把假端口摆成"杀光就结算"的标准联网副本:进副本两步按标准回包;CMD 49 回 0x22 + 0x23
    /// (<paramref name="omitSettlementB"/> 为真时只回 0x22,让战斗跑器在等 0x23 时超时);其余命令交给战斗假服务端。
    /// </summary>
    public static void WireStandardDungeonResponder(SettleRig rig, int monsterCount = 1, bool omitSettlementB = false)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsterCount));
        rig.Port.SetResponder((cls, id, payload) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                combatServer = new FakeCombatServer(CombatKit.Sequential(monsterCount));
                return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(DungeonAId)),
                    new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(monsterCount), 0, 0, 905)),
                };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                if (omitSettlementB)
                {
                    return new[] { new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, ValidSettlementABytes()) };
                }
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, ValidSettlementABytes()),
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, ValidSettlementBBytes()),
                };
            }
            return combatServer.Handle(cls, id, payload);
        });
    }
}
