using System.Collections.Concurrent;
using System.Globalization;
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
/// r18-settleui 端到端假会话流程测试:
/// 用真实 <see cref="OnlineDungeonFlowController"/> + 真实 <see cref="OnlineSettlementController"/> +
/// 真实 <see cref="OnlineDungeonRunner"/>(<c>keepAlive: null</c>) + 真实 <see cref="OnlineDungeonRoom"/> +
/// 真实 <see cref="HostedRoomRun"/> + 真实 <see cref="GameFlow"/> + <see cref="OnlineSettlementTownWorld"/>
/// 覆盖 72/73/74 → 75 命令序列、连续 3 次 Retry 零泄漏、SelectDungeon + 动态 (0,5) 许可更新、
/// ReturnToTown 落位(0/23 村区坐标与进副本前城镇区回退)、0/2 与 0/23+0/24 服务端主动回城、
/// &lt;60 秒倒计时自动回城、1/75 拒包字节序(错误码 8/0x85/1/其他)、72/73/74 无回应与超时、
/// CMD 16 再来一次被拒、结算通知超时(SettlementTimeout)、断线清理、以及真实 <see cref="OnlineClientSession"/> 传输适配。
/// 全程合成数据与 900+/9100+ 编号,不连真服。
/// </summary>
public sealed class OnlineSettlementFlowTests
{
    private const int DungeonAId = 9101;
    private const int DungeonBId = 9102;
    private const ushort SelfUid = 9501;
    private const ushort OtherUid = 9502;
    private const int QuestItemId = 9801;
    private const int RewardItemId = 9802;

    private sealed class StubHero : IHeroSession
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

    private sealed class RecordingTownWorld : ITownWorld
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

    private sealed class FakeSettlementTransport : IOnlineSettlementTransport
    {
        private readonly object _sync = new object();
        private readonly List<(ushort PacketId, byte[] Payload)> _sent = new List<(ushort, byte[])>();
        private Func<ushort, byte[], Task<FrameStatus>> _onSend;

        public bool IsDisconnected { get; set; }
        public int FrameSubscribers { get; private set; }
        public int DisconnectSubscribers { get; private set; }
        public bool Disposed { get; private set; }

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
            add { _frameReceived += value; FrameSubscribers++; }
            remove { _frameReceived -= value; FrameSubscribers--; }
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

        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _disconnected?.Invoke(reason);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeDungeonPort : IOnlineDungeonSessionPort
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

        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _currentNet?.ClosePeer();
            Disconnected?.Invoke(reason);
        }
    }

    private sealed class ScriptedNetSession : IOnlineDungeonSession
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

    private sealed class SettleRig : IDisposable
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
        public long NowMs { get; set; } = 10000; // 故意从非零时间起点开始,验证计时不依赖 0 起点

        public SettleRig(
            Action<OnlineSettlementOptions> configureSettle = null,
            Action<OnlineDungeonFlowOptions> configureDungeon = null,
            TownPlace? startPlace = null,
            TownPlace? defaultReturn = null)
        {
            TownPlace start = startPlace ?? new TownPlace(1, 3);
            TownPlace ret = defaultReturn ?? new TownPlace(1, 1, TownEntry.Return);
            RawWorld = new RecordingTownWorld(start, ret);
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
            SettleCtrl.Attach(DungeonCtrl);
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

        public void PumpUntil(Func<bool> predicate, int maxIterations = 300)
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
                Thread.Sleep(10);
            }
            Assert.True(predicate(), "Timed out; flow=" + Flow.Stage + " dungeonPhase=" + DungeonCtrl.Phase + " settlePhase=" + SettleCtrl.Phase);
        }

        public void Dispose()
        {
            SettleCtrl.Dispose();
            DungeonCtrl.Dispose();
            Fixture.Dispose();
        }
    }

    private static byte[] ValidSettlementABytes(byte rankValue = 0, int clearTime = 41200)
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

    private static byte[] ValidSettlementBBytes(int baseExp = 700, int rankBonusExp = 150, int gold = 520)
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

    private static byte[] UserAreaBytes(ushort uid, byte village, byte area, ushort x, ushort y, byte dir = 0)
    {
        return new NoticePayloadBuilder().U16(uid).U8(village).U8(area).U16(x).U16(y).U8(dir).U8(1).ToArray();
    }

    private static byte[] AreaUsersBytes(byte village, byte area)
    {
        return new NoticePayloadBuilder().U8(village).U8(area).U16(0).ToArray();
    }

    private static byte[] CardRightBytes(short s0, short s1 = -1, short s2 = -1, short s3 = -1)
    {
        return new NoticePayloadBuilder()
            .U8(1)
            .U16(unchecked((ushort)s0))
            .U16(unchecked((ushort)s1))
            .U16(unchecked((ushort)s2))
            .U16(unchecked((ushort)s3))
            .ToArray();
    }

    private static byte[] PermissionPayload(params (int DungeonId, byte Tier)[] entries)
    {
        var b = new NoticePayloadBuilder().U16(entries.Length);
        foreach ((int id, byte tier) in entries)
        {
            b.U16(id).U8(tier);
        }
        return b.ToArray();
    }

    private static void WireStandardDungeonResponder(SettleRig rig, int monsterCount = 2, bool omitSettlementB = false, bool omitSettlementAll = false, Func<int, bool> rejectCmd16OnAttempt = null)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsterCount));
        int selectAttempts = 0;

        rig.Port.SetResponder((cls, id, payload) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                combatServer = new FakeCombatServer(CombatKit.Sequential(monsterCount));
                return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                selectAttempts++;
                if (rejectCmd16OnAttempt != null && rejectCmd16OnAttempt(selectAttempts))
                {
                    return new[] { new CombatKit.ServerFrame(CombatKit.Cmd, OnlineDungeonEntry.SelectDungeonPacketId, new byte[] { 0, 14 }) };
                }
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(DungeonAId)),
                    new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(monsterCount), 0, 0, 905)),
                };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                // 守卫红线:结算命令 CMD 49 载荷必须保持 33 字节全零,绝不塞非零数值!
                Assert.Equal(OnlineCombatPayloads.SettlementLength, payload.Length);
                Assert.All(payload, b => Assert.Equal((byte)0, b));

                if (omitSettlementAll)
                {
                    return Array.Empty<CombatKit.ServerFrame>();
                }
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

    private static void AssertSanitizedSettleLogs(IEnumerable<string> logs)
    {
        string[] lines = logs.Where(l => l.StartsWith(OnlineSettlementLog.Prefix, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            Assert.DoesNotContain(DungeonAId.ToString(CultureInfo.InvariantCulture), line);
            Assert.DoesNotContain(SelfUid.ToString(CultureInfo.InvariantCulture), line);
            Assert.DoesNotContain(QuestItemId.ToString(CultureInfo.InvariantCulture), line);
            Assert.DoesNotContain(RewardItemId.ToString(CultureInfo.InvariantCulture), line);
            Assert.DoesNotContain("41200", line);
            Assert.DoesNotContain("700", line);
            Assert.DoesNotContain("520", line);
            Assert.DoesNotContain("88888", line);
            Assert.DoesNotContain("/", line);
        }
    }

    [Fact]
    public void Full_72_73_74_75_sequence_and_three_consecutive_Retry_cycles_leave_zero_leaked_state()
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 2);

        // 假传输自动回应 72 → 73 → 75(成功 + 0/2)
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == SettlementMenuPayloads.ScoreScrollStatePacketId)
            {
                Assert.Empty(payload);
                rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            }
            else if (id == SettlementMenuPayloads.CardSelectRightStatePacketId)
            {
                Assert.Empty(payload);
                rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1, -1, -1, -1));
            }
            else if (id == SettlementMenuPayloads.SelectCardPacketId)
            {
                Assert.Equal(new byte[] { 0, 0 }, payload); // mode=0, cardIndex=0
            }
            else if (id == SettlementMenuPayloads.EplpCommandPacketId)
            {
                Assert.Equal(2, payload.Length);
                Assert.Equal(SettlementMenuPayloads.EplpFlagExecute, payload[0]);
                byte cmd = payload[1];
                rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, cmd });
                rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();

        var seenRuns = new List<HostedRoomRun>();

        // 连续 3 次 Retry(共 4 局),最后一局 ReturnToTown
        for (int cycle = 0; cycle < 4; cycle++)
        {
            rig.PumpUntil(() => rig.Flow.Stage == GameStage.Dungeon && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon);
            HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
            seenRuns.Add(run);

            // 每局都必须是全新复位状态,无上局泄漏
            Assert.Equal(0, run.Reported);
            Assert.Equal(0, run.Dropped);
            Assert.Equal(1, run.RoomsEntered);
            Assert.Equal(0, run.RoomIndex);
            Assert.Equal(0, run.LocalKills);

            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

            // 停留期间画面已挂载结算叠层(含黑幕、菜单、灰显神秘商店、倒计时、本地评分)
            List<SceneSprite> frame = run.Build(rig.NowMs);
            Assert.Contains(frame, s => s.AnimationPath == "settle-fallback");
            Assert.Contains(frame, s => s.AnimationPath == "settle-secretshop-disabled");
            Assert.Contains(frame, s => s.AnimationPath == "settle-countdown");
            Assert.Equal(SettlementChoice.None, run.Choice);
            Assert.False(rig.SettleCtrl.IsChoiceEnabled(SettlementChoice.QuickParty));

            // 本轮有把握经验(700+150=850)恰好加一次
            Assert.Equal((cycle + 1) * 850, rig.Progress.TotalGained);

            // 确认发送序列严格为 72 → 73 → 74
            ushort[] advIds = rig.Transport.Sent.Select(x => x.PacketId).ToArray();
            Assert.Equal(new ushort[] { 72, 73, 74 }, advIds);

            if (cycle < 3)
            {
                // 点击第一行 Retry
                Assert.Equal(SettlementChoice.Retry, rig.SettleCtrl.Click(SettlementArt.PanelX + 10, SettlementArt.RowTop + 5));
                rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon);

                // 验证发送了 1/75 [1, 0]
                Assert.Equal((ushort)75, rig.Transport.Sent[^1].PacketId);
                Assert.Equal(new byte[] { 1, 0 }, rig.Transport.Sent[^1].Payload);
                Assert.Equal(cycle + 1, rig.SettleCtrl.RetryCycleCount);
                Assert.Null(run.HudOverlay); // 旧局叠层已卸载
                rig.Transport.ClearSent();
            }
            else
            {
                Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
                rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
                Assert.Equal((ushort)75, rig.Transport.Sent[^1].PacketId);
                Assert.Equal(new byte[] { 1, 2 }, rig.Transport.Sent[^1].Payload);
                Assert.Equal(0, rig.SettleCtrl.RetryCycleCount);
                Assert.Null(run.HudOverlay);
            }
        }

        // 4 局各自独立、收包权严格成对、传输退订无泄漏
        Assert.Equal(4, seenRuns.Distinct().Count());
        Assert.Equal(4, rig.Flow.Runs);
        Assert.Equal(4, rig.Flow.Settlements);
        Assert.Equal(4, rig.Port.EnterExclusiveCalls);
        Assert.Equal(4, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Equal(4, rig.Port.Commands.Count(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonCombat.SettlementPacketId));

        lock (rig.Logs) AssertSanitizedSettleLogs(rig.Logs);
    }

    [Fact]
    public void SelectDungeon_sends_cmd_1_and_applies_dynamic_0_5_permission_update_received_during_settlement()
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1);

        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(0, 0, 1, -1)); // 仅槽 2 有权
            else if (id == 74) Assert.Equal(new byte[] { 0, 2 }, payload); // 占位决定:选第一个有权槽 2
            else if (id == 75)
            {
                Assert.Equal(new byte[] { 1, 1 }, payload); // SelectOtherDungeon == 1
                rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, 1 });
                rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        Assert.Equal((byte)1, rig.Permissions.GetTier(DungeonAId));

        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal((byte)2, rig.SettleCtrl.SelectedCardIndex);

        // 0x23 之后服务端推来新的 (0,5) 许可表,把 DungeonA 档位升到 3
        rig.Port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId, PermissionPayload((DungeonAId, 3)));
        rig.Step();
        Assert.Equal((byte)3, rig.Permissions.GetTier(DungeonAId));

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.SelectDungeon));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.DungeonSelect && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
        Assert.Equal(3, rig.Flow.Select!.MaxDifficulty);
    }

    [Fact]
    public void ReturnToTown_places_hero_at_0_23_area_or_falls_back_to_pre_dungeon_area_when_only_0_2_arrives()
    {
        // 情况 A:收到 0/23 + 0/24(没有 0/2),按 0/23 的村/区/坐标落位;先单独收 0/23 不会提前完成,配齐 0/24 才完成
        using (var rig = new SettleRig(startPlace: new TownPlace(1, 3), defaultReturn: new TownPlace(1, 1, TownEntry.Return)))
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, payload) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75) rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] });
                return Task.FromResult(FrameStatus.Ok);
            });

            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.WaitingServerReturn);

            // 他人的 0/23 必须忽略
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(OtherUid, 9, 9, 10, 10));
            rig.Step();
            Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.SettleCtrl.Phase);

            // 本人 0/23 单独到达尚未配齐 0/24:仍在等待
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 2, 5, 412, 265, dir: 1));
            rig.Step();
            Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.SettleCtrl.Phase);

            // 补齐 0/24:立刻回城并按 0/23 落位
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(2, 5));
            rig.Step();
            Assert.Equal(GameStage.Town, rig.Flow.Stage);
            Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
            Assert.Equal(new TownPlace(2, 5, TownEntry.Return), rig.Flow.Place);
            OnlineSettlementReturnTarget target = Assert.Single(rig.AppliedReturnTargets);
            Assert.Equal(OnlineSettlementReturnSource.ServerUserArea, target.Source);
            Assert.Equal(412.0, target.X);
            Assert.Equal(265.0, target.Y);
            Assert.Equal(true, target.FacingLeft);
        }

        // 情况 B:只收到 0/2(没收到 0/23),回落到进副本前所在的城镇区 (1, 3) 而非默认 (1, 1)
        using (var rig = new SettleRig(startPlace: new TownPlace(1, 3), defaultReturn: new TownPlace(1, 1, TownEntry.Return)))
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, payload) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75)
                {
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] });
                    rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
                }
                return Task.FromResult(FrameStatus.Ok);
            });

            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
            rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
            Assert.Equal(new TownPlace(1, 3, TownEntry.Return), rig.Flow.Place);
            OnlineSettlementReturnTarget target = Assert.Single(rig.AppliedReturnTargets);
            Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
            Assert.False(target.HasCoordinates);
        }
    }

    [Fact]
    public void CardRight_with_no_eligible_slot_skips_1_74_and_still_opens_menu()
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1);
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(0, -1, -1, -1)); // 无翻牌权
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.SettleCtrl.CardSelectSkipped);
        Assert.Null(rig.SettleCtrl.SelectedCardIndex);
        Assert.Equal(OnlineSettlementNoticeKind.CardSelectSkipped, rig.SettleCtrl.CurrentNotice?.Kind);
        Assert.DoesNotContain(rig.Transport.Sent, x => x.PacketId == 74);
        Assert.Equal(new ushort[] { 72, 73 }, rig.Transport.Sent.Select(x => x.PacketId).ToArray());
    }

    [Fact]
    public void Menu_countdown_is_shorter_than_60s_and_auto_sends_ReturnToTown_at_deadline()
    {
        Assert.True(OnlineSettlementOptions.DefaultMenuCountdownMs < OnlineSettlementOptions.ServerReturnTimerMs);
        Assert.True(OnlineSettlementController.EffectiveMenuCountdownMs(0) < OnlineSettlementOptions.ServerReturnTimerMs);
        Assert.True(OnlineSettlementController.EffectiveMenuCountdownMs(90_000) < OnlineSettlementOptions.ServerReturnTimerMs);
        Assert.Equal(OnlineSettlementOptions.ServerReturnTimerMs - 1_000, OnlineSettlementController.EffectiveMenuCountdownMs(90_000));

        using var rig = new SettleRig(o => o.MenuCountdownMs = 200);
        WireStandardDungeonResponder(rig, monsterCount: 1);
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            else if (id == 75)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] });
                rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        // 尚未到 200ms 时不提前发 1/75
        rig.Step(100);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.SettleCtrl.Phase);
        Assert.DoesNotContain(rig.Transport.Sent, x => x.PacketId == 75);

        // 到点自动发 1/75 [1, 2] 并回城
        rig.Step(120);
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
        Assert.Contains(rig.Transport.Sent, x => x.PacketId == 75 && x.Payload.SequenceEqual(new byte[] { 1, 2 }));
        lock (rig.Logs)
        {
            Assert.Contains(rig.Logs, l => l.Contains("event=choose choice=ReturnToTown source=timeout"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Server_initiated_return_is_recognized_for_both_0_2_and_0_23_plus_0_24_signals(bool useNoti2)
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1);
        rig.Transport.SetSendHandler((id, _) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        // 玩家未点击任何选项,服务端主动推送回城信号
        if (useNoti2)
        {
            rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
        }
        else
        {
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 3, 2, 220, 180));
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(3, 2));
        }
        rig.Step();

        Assert.True(rig.SettleCtrl.ServerInitiatedReturn);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.DoesNotContain(rig.Transport.Sent, x => x.PacketId == 75);
        if (!useNoti2)
        {
            Assert.Equal(new TownPlace(3, 2, TownEntry.Return), rig.Flow.Place);
        }
        lock (rig.Logs)
        {
            Assert.Contains(rig.Logs, l => l.Contains("event=town reason=server_return"));
        }
    }

    [Theory]
    [InlineData(8, OnlineSettlementNoticeKind.EplpRejectedNotLeader)]
    [InlineData(0x85, OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen)]
    [InlineData(1, OnlineSettlementNoticeKind.EplpRejectedInvalidCommand)]
    [InlineData(0x22, OnlineSettlementNoticeKind.EplpRejectedOther)]
    public void Eplp_rejection_decodes_error_byte_at_index_1_distinguishes_codes_never_resends_and_allows_safe_town_return(
        byte errorCode, OnlineSettlementNoticeKind expectedKind)
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1);

        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            else if (id == 75)
            {
                if (payload[1] == (byte)EplpCommand.Retry)
                {
                    // 先发一条 UI 同步回显 [1, 2, 0] 确认不被误当成执行确认,再发失败回包 [0, errorCode, 0]
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 2, 0 });
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 0, errorCode, 0 });
                }
                else if (payload[1] == (byte)EplpCommand.ReturnToTown)
                {
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, 2 });
                    rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
                }
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.Retry));
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuDegraded);

        Assert.Equal(expectedKind, rig.SettleCtrl.CurrentNotice?.Kind);
        Assert.Equal(errorCode, rig.SettleCtrl.CurrentNotice?.Code);
        Assert.False(rig.SettleCtrl.IsChoiceEnabled(SettlementChoice.Retry)); // 被拒的命令不再允许重发
        Assert.False(rig.SettleCtrl.Choose(SettlementChoice.Retry));
        Assert.Equal(1, rig.Transport.Sent.Count(x => x.PacketId == 75 && x.Payload[1] == 0));

        // 改选 ReturnToTown 安全回城
        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
    }

    [Fact]
    public void Retry_rejected_by_server_at_CMD16_returns_safely_to_select_screen_with_paired_exclusive_counts()
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1, rejectCmd16OnAttempt: attempt => attempt == 2);

        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            else if (id == 75)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] });
                rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.Retry));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.DungeonSelect && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Idle);

        Assert.Equal(DungeonSelectNoticeKind.Rejected, rig.Flow.Select!.Notice?.Kind);
        Assert.Equal(2, rig.Port.EnterExclusiveCalls);
        Assert.Equal(2, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
    }

    [Fact]
    public void Advance_72_timeout_and_75_timeout_never_resend_and_fall_back_safely_to_town()
    {
        using var rig = new SettleRig(o =>
        {
            o.AdvanceReplyTimeoutMs = 100;
            o.EplpReplyTimeoutMs = 100;
        });
        WireStandardDungeonResponder(rig, monsterCount: 1);
        // 服务端对 72、75 均静默不回
        rig.Transport.SetSendHandler((_, _) => Task.FromResult(FrameStatus.Ok));

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.AdvancingScoreScroll);

        // 72 超时不重发,退化到 MenuDegraded 并弹 AdvanceNoReply 提示
        rig.Step(120);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.SettleCtrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.SettleCtrl.CurrentNotice?.Kind);
        Assert.Equal(1, rig.Transport.Sent.Count(x => x.PacketId == 72));
        Assert.DoesNotContain(rig.Transport.Sent, x => x.PacketId == 73 || x.PacketId == 74);

        // 玩家选 ReturnToTown → 发 1/75 一次 → 静默超时 → 安全回城不卡死、不重发
        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.SettleCtrl.Phase);
        rig.Step(120);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.Equal(1, rig.Transport.Sent.Count(x => x.PacketId == 75));
    }

    [Fact]
    public void Settlement_notice_timeout_shows_timeout_prompt_enables_only_ReturnToTown_and_returns_safely()
    {
        using var rig = new SettleRig(
            configureSettle: o => o.TownReturnWaitTimeoutMs = 100,
            configureDungeon: d => d.CombatOptions = new OnlineDungeonCombatOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettlementTimeout = TimeSpan.FromMilliseconds(80),
            });
        // 只回 0x22 不回 0x23 → 触发 SettlementTimeout
        WireStandardDungeonResponder(rig, monsterCount: 1, omitSettlementB: true);
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == SessionLifecyclePayloads.GiveUpGamePacketId)
            {
                Assert.Empty(payload);
                rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200));
                rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
            }
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuDegraded);

        Assert.True(rig.SettleCtrl.Summary!.IsTimedOut);
        Assert.Equal(OnlineSettlementNoticeKind.SettlementTimeout, rig.SettleCtrl.CurrentNotice?.Kind);
        Assert.Equal(OnlineSettlementParseState.Succeeded, rig.SettleCtrl.Summary.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, rig.SettleCtrl.Summary.RewardParseState);

        // 超时后仅提供返回城镇,不发 72/73/74
        Assert.False(rig.SettleCtrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.False(rig.SettleCtrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.SettleCtrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
        Assert.Empty(rig.Transport.Sent);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
        Assert.Contains(rig.Transport.Sent, x => x.PacketId == SessionLifecyclePayloads.GiveUpGamePacketId);
        Assert.Equal(new TownPlace(1, 2, TownEntry.Return), rig.Flow.Place);
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
    }

    [Fact]
    public void Disconnect_during_settlement_hold_cleans_up_overlay_resets_hold_flag_and_notifies_once()
    {
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig, monsterCount: 1);
        rig.Transport.SetSendHandler((id, _) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.NotNull(run.HudOverlay);

        rig.Transport.TriggerDisconnect(OnlineDisconnectReason.PeerClosed);
        rig.Port.TriggerDisconnect(OnlineDisconnectReason.PeerClosed);
        rig.Step();

        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Null(run.HudOverlay);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.True(rig.Disconnects >= 1);
    }

    [Fact]
    public void WaitingServerReturn_timeout_on_Retry_never_sends_CMD15_and_returns_to_town_safely()
    {
        using var rig = new SettleRig(o => o.ReturnWaitTimeoutMs = 100);
        WireStandardDungeonResponder(rig, monsterCount: 1);
        rig.Transport.SetSendHandler((id, payload) =>
        {
            if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
            else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
            else if (id == 75) rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] }); // 只回成功但不推 0/2 或 0/23+0/24
            return Task.FromResult(FrameStatus.Ok);
        });

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.Retry));
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.WaitingServerReturn);

        int cmd15Before = rig.Port.Commands.Count(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.StartGamePacketId);
        rig.Step(120);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        // 服务端从未确认回城,绝不贸然重发 CMD 15
        Assert.Equal(cmd15Before, rig.Port.Commands.Count(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.StartGamePacketId));
    }

    [Fact]
    public async Task RealOnlineSettlementTransport_does_not_dispose_underlying_session_and_rejects_send_after_dispose()
    {
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        Assert.True(transport.IsDisconnected);

        Action<PacketClass, int, byte[]> onFrame = (_, _, _) => { };
        Action<OnlineDisconnectReason> onDisc = _ => { };
        transport.FrameReceived += onFrame;
        transport.Disconnected += onDisc;
        transport.Dispose();

        // 释放传输适配器绝不释放底层 OnlineClientSession
        Assert.NotEqual(OnlineClientSessionState.Disposed, session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendCommandAsync(72, Array.Empty<byte>(), CancellationToken.None));
    }

    [Fact]
    public void Advance_72_malformed_and_73_timeout_or_malformed_and_74_send_fail_degrade_safely_without_resend()
    {
        // 1. (1,72) 畸形回复([0]):不发 73/74,退化为 MenuDegraded + AdvanceNoReply
        using (var rig = new SettleRig())
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 0 });
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuDegraded);
            Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.SettleCtrl.CurrentNotice?.Kind);
            Assert.Equal(new ushort[] { 72 }, rig.Transport.Sent.Select(x => x.PacketId).ToArray());
        }

        // 2. (1,73) 畸形回复:不发 74,退化为 MenuDegraded
        using (var rig = new SettleRig())
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, new byte[] { 1, 0 }); // 短包
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuDegraded);
            Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.SettleCtrl.CurrentNotice?.Kind);
            Assert.Equal(new ushort[] { 72, 73 }, rig.Transport.Sent.Select(x => x.PacketId).ToArray());
        }

        // 3. (1,73) 超时:不重发 73,不发 74,退化为 MenuDegraded
        using (var rig = new SettleRig(o => o.AdvanceReplyTimeoutMs = 80))
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.AdvancingCardRight);
            rig.Step(100);
            Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.SettleCtrl.Phase);
            Assert.Equal(1, rig.Transport.Sent.Count(x => x.PacketId == 73));
            Assert.DoesNotContain(rig.Transport.Sent, x => x.PacketId == 74);
        }

        // 4. 1/74 发送编码失败(NoCipher):退化为 MenuDegraded,绝不重发 74
        using (var rig = new SettleRig())
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 74) return Task.FromResult(FrameStatus.NoCipher);
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuDegraded);
            Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.SettleCtrl.CurrentNotice?.Kind);
            Assert.Equal(1, rig.Transport.Sent.Count(x => x.PacketId == 74));
        }
    }

    [Fact]
    public void Eplp_mismatched_command_reply_is_ignored_and_ReturnToTown_rejection_or_send_fail_falls_back_safely()
    {
        // 1. 选 Retry 发 1/75 [1,0],收到 cmd=2 的错配回包不放行;选 ReturnToTown 若被拒([0,8,2])则安全回城不卡死
        using (var rig = new SettleRig())
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, payload) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75 && payload[1] == 0)
                {
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, 2 }); // 错配命令 2,应被忽略
                }
                else if (id == 75 && payload[1] == 2)
                {
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 0, 8, 2 }); // ReturnToTown 本身被拒
                }
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

            // 验证 SetPointer / ClearPointer
            rig.SettleCtrl.SetPointer(SettlementArt.PanelX + 10, SettlementArt.RowTop + 5);
            Assert.Equal(0, rig.SettleCtrl.HoverRow);
            rig.SettleCtrl.ClearPointer();
            Assert.Equal(-1, rig.SettleCtrl.HoverRow);

            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.Retry));
            rig.Step();
            Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.SettleCtrl.Phase); // 错配回包没放行
        }

        // 2. ReturnToTown 的 1/75 被服务端拒([0,8,2]):立刻安全回城并记 NotLeader 提示,绝不重发
        using (var rig = new SettleRig())
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, payload) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75) rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 0, 8, payload[1] });
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
            rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
            Assert.Equal(OnlineSettlementNoticeKind.EplpRejectedNotLeader, rig.SettleCtrl.CurrentNotice?.Kind);
            Assert.Equal(1, rig.Transport.Sent.Count(x => x.PacketId == 75));
        }

        // 3. 1/75 发送返回 NoCipher:安全回城不卡死
        using (var rig = new SettleRig())
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, _) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75) return Task.FromResult(FrameStatus.NoCipher);
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
            rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
            Assert.Equal(OnlineSettlementNoticeKind.SendFailed, rig.SettleCtrl.CurrentNotice?.Kind);
        }
    }

    [Fact]
    public void Batch_0_2_with_0_23_and_0_24_captures_coords_and_malformed_0_23_with_0_24_falls_back_to_pre_dungeon_area()
    {
        // 同批到达 [0/2, 0/23, 0/24]:依然捕获 0/23 落位并记录 noti2_and_area
        using (var rig = new SettleRig(startPlace: new TownPlace(1, 3)))
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, payload) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75)
                {
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] });
                    rig.Transport.Push(PacketClass.Noti, UserInfoParser.PacketId, new byte[] { 0, 1, 0 });
                    rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 4, 2, 333, 210));
                    rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(4, 2));
                }
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
            rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
            Assert.Equal(new TownPlace(4, 2, TownEntry.Return), rig.Flow.Place);
            lock (rig.Logs) Assert.Contains(rig.Logs, l => l.Contains("signal=noti2_and_area"));
        }

        // 0/23 载荷损坏 + 0/24:不卡死,退回进副本前的城镇区 (1, 3)
        using (var rig = new SettleRig(startPlace: new TownPlace(1, 3)))
        {
            WireStandardDungeonResponder(rig, monsterCount: 1);
            rig.Transport.SetSendHandler((id, payload) =>
            {
                if (id == 72) rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
                else if (id == 73) rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
                else if (id == 75)
                {
                    rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, payload[1] });
                    rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, new byte[] { 1, 2 }); // 短包
                    rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 3));
                }
                return Task.FromResult(FrameStatus.Ok);
            });
            rig.EnterTownAndDungeon();
            rig.KillAllInCurrentRun();
            rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
            Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
            rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);
            Assert.Equal(new TownPlace(1, 3, TownEntry.Return), rig.Flow.Place);
        }
    }

    [Fact]
    public void Without_settlement_subscriber_Settling_timeout_still_runs_legacy_CombatAbort_path()
    {
        using var rig = new SettleRig(configureDungeon: d => d.CombatOptions = new OnlineDungeonCombatOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            SettlementTimeout = TimeSpan.FromMilliseconds(70),
        });
        rig.SettleCtrl.Detach(); // 无订阅者时必须保持既有 CombatAbort 行为一字不变
        WireStandardDungeonResponder(rig, monsterCount: 1, omitSettlementAll: true);

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Idle);

        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal(0, rig.Flow.Settlements);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.DungeonCtrl.ActiveNotice?.Kind);
    }

    [Fact]
    public void Synthetic_null_transport_mode_opens_menu_directly_and_completes_local_choice()
    {
        using var rig = new SettleRig(configureSettle: o => o.TransportFactory = _ => null);
        WireStandardDungeonResponder(rig, monsterCount: 1);

        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.SelectDungeon));
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.Equal(1, rig.Flow.Settlements);
    }
}
