using System.Collections.Concurrent;
using System.Globalization;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 控制器端到端状态机与接线测试:
/// 用真实 <see cref="OnlineDungeonRunner"/>(<c>keepAlive: null</c>) + 真实 <see cref="OnlineDungeonRoom"/> + <see cref="GameFlow"/> +
/// 合成房间(<see cref="HostedGatedRoomFixture"/>)与合成服务端(<see cref="FakeMultiRoomServer"/>) 驱动整条进出副本链,
/// 不连真服、不含真实编号/路径/凭据。
/// </summary>
public sealed class OnlineDungeonFlowTests
{
    private const int DungeonAId = 9101;
    private const int DungeonBId = 9102;
    private const int ReturnTownId = 1;
    private const int ReturnAreaId = 1;

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

    private sealed class TestTownWorld : ITownWorld
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

    private sealed class FakeSessionPort : IOnlineDungeonSessionPort
    {
        private readonly ConcurrentQueue<string> _timeline = new ConcurrentQueue<string>();
        private readonly List<(PacketClass Class, int PacketId, byte[] Payload)> _commands =
            new List<(PacketClass, int, byte[])>();
        private readonly object _sync = new object();
        private ScriptedNetSession _currentNetSession;
        private Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _responder;

        public bool IsDisconnected { get; set; }
        public int EnterExclusiveCalls { get; private set; }
        public int LeaveExclusiveCalls { get; private set; }
        public bool LeaveExclusiveThrowsDisposed { get; set; }
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

        public IReadOnlyList<string> Timeline => _timeline.ToArray();

        public event Action<PacketClass, int, byte[]> FrameReceived;
        public event Action<OnlineDisconnectReason> Disconnected;

        public void RecordTimeline(string mark) => _timeline.Enqueue(mark);

        public void SetResponder(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> responder)
        {
            _responder = responder;
        }

        public Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterExclusiveCalls++;
            _timeline.Enqueue("EnterExclusive#" + EnterExclusiveCalls);
            return Task.CompletedTask;
        }

        public Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
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

        public void PushFrame(PacketClass cls, int packetId, byte[] payload)
        {
            FrameReceived?.Invoke(cls, packetId, payload);
        }

        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _currentNetSession?.ClosePeer();
            Disconnected?.Invoke(reason);
        }
    }

    private sealed class ScriptedNetSession : IOnlineDungeonSession
    {
        private readonly Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _onSend;
        private readonly ConcurrentQueue<DecodedFrame> _inbound = new ConcurrentQueue<DecodedFrame>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

        public ScriptedNetSession(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> onSend)
        {
            _onSend = onSend;
        }

        public bool IsReady => !PeerClosed;
        public bool PeerClosed { get; private set; }
        public FrameError LastFrameError => FrameError.None;

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

    private static byte[] ValidSettlementABytes(byte rankValue = 7, int clearTime = 42310, sbyte timeBonus = 18, sbyte baseScore = 76)
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

    private static byte[] ValidSettlementBBytes(int baseExp = 12345, int rankBonusExp = 678, int goldSlot0 = 450, int itemId = 9801)
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

    private static byte[] PermissionPayload(params (int DungeonId, byte Tier)[] entries)
    {
        var b = new NoticePayloadBuilder().U16(entries.Length);
        foreach ((int id, byte tier) in entries)
        {
            b.U16(id).U8(tier);
        }
        return b.ToArray();
    }

    private static byte[] UserAreaBytes(byte village, byte area)
    {
        return new NoticePayloadBuilder().U16(901).U8(village).U8(area).U16(300).U16(200).U8(0).U8(1).ToArray();
    }

    private static byte[] AreaUsersBytes(byte village, byte area)
    {
        return new NoticePayloadBuilder().U8(village).U8(area).U16(0).ToArray();
    }

    private static void StepAndPollUntil(GameFlow flow, OnlineDungeonFlowController ctrl, Func<bool> condition, int maxIterations = 250)
    {
        long nowMs = 0;
        for (int i = 0; i < maxIterations; i++)
        {
            if (condition())
            {
                return;
            }
            nowMs += 20;
            flow.Session?.Update(20, 0, 0);
            flow.Splash?.Advance(20);
            flow.Select?.Advance(20);
            ctrl.Poll(nowMs);
            flow.Poll();
            if (condition())
            {
                return;
            }
            Thread.Sleep(15);
        }
        Assert.True(condition(), "Timed out waiting for online dungeon flow state; stage=" + flow.Stage + " phase=" + ctrl.Phase);
    }

    private static void AssertSanitizedOnlineLogs(IEnumerable<string> logs)
    {
        string[] lines = logs.Where(l => l.StartsWith(OnlineDungeonLog.Prefix, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            Assert.DoesNotContain(DungeonAId.ToString(CultureInfo.InvariantCulture), line);
            Assert.DoesNotContain(DungeonBId.ToString(CultureInfo.InvariantCulture), line);
            Assert.DoesNotContain("/", line);
            Assert.DoesNotContain("\\", line);
            Assert.DoesNotContain(".dgn", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("9501", line);
            Assert.DoesNotContain("9801", line);
        }
    }

    private static (GameFlow Flow, TestTownWorld World, DungeonEntry[] Entries, OnlineDungeonPermissionTable Table, OnlineDungeonSelectPolicy Policy)
        CreateSelectFixture()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[]
        {
            new KeyValuePair<int, byte>(DungeonAId, 3),
            new KeyValuePair<int, byte>(DungeonBId, 1),
        }, hasSnapshot: true);

        var entries = new[]
        {
            new DungeonEntry("dungeon/test/dunga.dgn", "DUNGA", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
            new DungeonEntry("dungeon/test/dungb.dgn", "DUNGB", new DungeonCard(DungeonBId, 200, 200, 0, null, 0, 1)),
        };

        var world = new TestTownWorld(new TownPlace(1, 0), new TownPlace(ReturnTownId, ReturnAreaId));
        var flow = new GameFlow(world, (path, _) => throw new InvalidOperationException("offline factory should not run"), entries);
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasLocalData: _ => true, hasUltimate: _ => true);
        return (flow, world, entries, table, policy);
    }

    private static RoomMonsterSpawn[] Templates(int count)
    {
        return Enumerable.Range(0, count).Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320 + 40 * i)).ToArray();
    }

    [Fact]
    public void Happy_path_entry_combat_settlement_handoff_HoldForMenu_and_three_Retry_cycles()
    {
        using var fixture = new HostedGatedRoomFixture();
        (GameFlow flow, _, DungeonEntry[] entries, _, OnlineDungeonSelectPolicy policy) = CreateSelectFixture();
        var port = new FakeSessionPort();
        var logs = new List<string>();
        var disconnectedCount = 0;

        int monsterCount = 2;
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsterCount));

        port.SetResponder((cls, id, payload) =>
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
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, ValidSettlementABytes(rankValue: 9, clearTime: 31500, timeBonus: 20, baseScore: 84)),
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, ValidSettlementBBytes(baseExp: 22222, rankBonusExp: 3333, goldSlot0: 777, itemId: 9801)),
                };
            }
            return combatServer.Handle(cls, id, payload);
        });

        var options = new OnlineDungeonFlowOptions
        {
            CombatOptions = CombatKit.Options(pollMilliseconds: 10),
            ReturnDelayMs = 20,
            TransientNoticeDurationMs = 200,
            Log = line =>
            {
                lock (logs) logs.Add(line);
            },
            OnDisconnected = () => disconnectedCount++,
            CurrentArea = () => new TownAreaInfo(
                ReturnTownId,
                new TownArea(ReturnAreaId, "map/test/normal.map", TownAreaKind.Normal, Array.Empty<int>(), null, false),
                "map/test/normal.map",
                Array.Empty<WorldRect>(),
                Array.Empty<TownExit>()),
            HeroLevel = () => 15, // 非门区且等级>1 -> IsDungeonEntranceArea=false，仍须直接进入且不发 1/38
            ReturnPlace = new TownPlace(ReturnTownId, ReturnAreaId),
        };

        using var ctrl = new OnlineDungeonFlowController(port, flow, policy, ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(
                plan => fixture.Create(Templates(monsterCount), plan, Array.Empty<GateSide>()),
                returnDelayMs: 20);
            return room.Status == OnlineRoomStatus.Ready && run != null
                ? OnlineDungeonRoomBuildResult.Success(room, run)
                : OnlineDungeonRoomBuildResult.Failure(room.Status, room);
        }, options);

        OnlineDungeonSettlementReady lastReady = null;
        int settlementEvents = 0;
        ctrl.SettlementStarted += ready =>
        {
            port.RecordTimeline("SettlementStarted#" + (++settlementEvents));
            Assert.Equal(port.EnterExclusiveCalls, port.LeaveExclusiveCalls);
            ready.HoldForMenu = true;
            lastReady = ready;
        };

        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        flow.Select!.MoveDifficulty(2);
        flow.Select.Confirm();
        Assert.True(flow.Poll());

        // 连续跑 3 轮 (首次进 + 两次 Retry),验证无状态残留且收包权严格成对进出
        for (int cycle = 0; cycle < 3; cycle++)
        {
            StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Dungeon && ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
            var hostedRun = Assert.IsType<HostedRoomRun>(flow.Run);

            // 杀光房间内全部本地怪,驱动真实 1/39 -> 0/38 -> 0/31 -> 1/49 -> 0/34 + 0/35
            foreach (MonsterActor monster in hostedRun.Room.Monsters.ToArray())
            {
                HostedRoomFixture.Kill(monster);
            }

            StepAndPollUntil(flow, ctrl, () => lastReady != null && settlementEvents == cycle + 1);
            Assert.Equal(GameStage.Dungeon, flow.Stage);
            Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, ctrl.Phase);
            Assert.False(flow.Poll()); // HoldForMenu=true 时不自动回城

            Assert.Equal(DungeonAId, lastReady!.Data.DungeonId);
            Assert.Equal(entries[0].Path, lastReady.Data.DungeonPath);
            Assert.Equal(2, lastReady.Data.Difficulty);
            Assert.Equal(2, lastReady.Data.ReportedKills);
            Assert.Equal(0, lastReady.Data.DroppedKills);
            Assert.Equal(OnlineSettlementParseState.Succeeded, lastReady.Data.ResultParseState);
            Assert.Equal(OnlineSettlementParseState.Succeeded, lastReady.Data.RewardParseState);
            Assert.NotNull(lastReady.Data.Result);
            Assert.Equal((byte)9, lastReady.Data.Result.RankValue);
            Assert.Equal(31500, lastReady.Data.Result.ClearTime);
            Assert.NotNull(lastReady.Data.Reward);
            Assert.Equal(22222, lastReady.Data.Reward.BaseExp);
            Assert.Equal(3333, lastReady.Data.Reward.RankBonusExp);
            Assert.Equal(777, lastReady.Data.Reward.Rewards[0].Value);
            Assert.Equal(9801, lastReady.Data.Reward.Rewards[0].ItemId);

            if (cycle < 2)
            {
                lastReady.CompleteChoice(SettlementChoice.Retry);
            }
            else
            {
                lastReady.CompleteChoice(SettlementChoice.ReturnToTown);
            }
        }

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(3, flow.Runs);
        Assert.Equal(3, flow.Settlements);
        Assert.Equal(3, port.EnterExclusiveCalls);
        Assert.Equal(3, port.LeaveExclusiveCalls);
        Assert.Equal(0, disconnectedCount);

        // 非门区进图:不得先发 1/38(ChangeAreaPacketId),必须直接走 1/15
        Assert.DoesNotContain(port.Commands, c => c.Class == PacketClass.Cmd && c.PacketId == 38);
        Assert.Equal(3, port.Commands.Count(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.StartGamePacketId));

        // LeaveExclusive 必须严格在对应轮次的 SettlementStarted 之前完成
        string[] timeline = port.Timeline.ToArray();
        for (int c = 1; c <= 3; c++)
        {
            int idxLeave = Array.IndexOf(timeline, "LeaveExclusive#" + c);
            int idxSettle = Array.IndexOf(timeline, "SettlementStarted#" + c);
            Assert.True(idxLeave >= 0 && idxSettle > idxLeave, string.Join(",", timeline));
        }

        lock (logs)
        {
            Assert.Contains(logs, l => l.Contains("event=enter areakind=Normal entrance=false difficulty=2"));
            Assert.Contains(logs, l => l.Contains("event=leave reason=ReturnReady reported=2 dropped=0 rooms=1 result=Succeeded reward=Succeeded"));
            AssertSanitizedOnlineLogs(logs);
        }
    }

    [Fact]
    public void Entry_failures_handle_cmd15_cmd16_timeout_room_build_fail_and_dynamic_0_5_recovery()
    {
        using var fixture = new HostedGatedRoomFixture();
        (GameFlow flow, _, DungeonEntry[] entries, OnlineDungeonPermissionTable table, OnlineDungeonSelectPolicy policy) = CreateSelectFixture();
        var port = new FakeSessionPort();
        var logs = new List<string>();
        int disconnects = 0;

        string mode = "cmd15";
        port.SetResponder((cls, id, _) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                if (mode == "cmd15")
                {
                    return new[] { new CombatKit.ServerFrame(CombatKit.Cmd, OnlineDungeonEntry.StartGamePacketId, new byte[] { 0, 9 }) };
                }
                return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                if (mode == "cmd16")
                {
                    return new[] { new CombatKit.ServerFrame(CombatKit.Cmd, OnlineDungeonEntry.SelectDungeonPacketId, new byte[] { 0, 14 }) };
                }
                if (mode == "timeout")
                {
                    return Array.Empty<CombatKit.ServerFrame>(); // 不回 28/29 触发超时
                }
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(DungeonBId)),
                    new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(2), 0, 0, 905)),
                };
            }
            return Array.Empty<CombatKit.ServerFrame>();
        });

        var options = new OnlineDungeonFlowOptions
        {
            LoadingNoticeTimeout = TimeSpan.FromMilliseconds(70),
            DungeonInfoTimeout = TimeSpan.FromMilliseconds(70),
            StartMapTimeout = TimeSpan.FromMilliseconds(70),
            CombatOptions = CombatKit.Options(pollMilliseconds: 10),
            Log = line => { lock (logs) logs.Add(line); },
            OnDisconnected = () => disconnects++,
        };

        using var ctrl = new OnlineDungeonFlowController(port, flow, policy, ctx =>
        {
            // mode == "roomfail" 故意返回建房失败
            return OnlineDungeonRoomBuildResult.Failure(OnlineRoomStatus.NoRoom);
        }, options);

        flow.EnterTown("boot");
        flow.EnterSelect("gate");

        // 1. CMD 15 拒绝:退回选择屏并显示 Rejected 提示,但绝不灰显条目
        flow.Select!.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => ctrl.Phase == OnlineDungeonFlowPhase.Idle && flow.Stage == GameStage.DungeonSelect);
        Assert.Equal(DungeonSelectNoticeKind.Rejected, flow.Select.Notice!.Kind);
        Assert.False(policy.EntryStatus(entries[0]).Disabled);
        Assert.Equal(1, port.EnterExclusiveCalls);
        Assert.Equal(1, port.LeaveExclusiveCalls);

        // 2. CMD 16 拒绝:退回选择屏、记住 ServerRejected 并灰显条目,再次 Confirm 被拦
        mode = "cmd16";
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => ctrl.Phase == OnlineDungeonFlowPhase.Idle && flow.Stage == GameStage.DungeonSelect);
        Assert.Equal(DungeonSelectNoticeKind.Rejected, flow.Select.Notice!.Kind);
        DungeonEntryStatus blocked = policy.EntryStatus(entries[0]);
        Assert.True(blocked.Disabled);
        Assert.Equal(DungeonEntryBlockKind.ServerRejected, blocked.Kind);
        Assert.Equal((byte)14, blocked.Code);
        Assert.Equal(2, port.LeaveExclusiveCalls);

        flow.Select.Confirm();
        Assert.False(flow.Poll());
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);

        // 3. 动态 (0,5) 坏包保留已有表与拒绝标记;好包更新档位并解除对应副本 ServerRejected
        port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId, new byte[] { 5, 0 }); // declared count out of bounds
        Assert.Equal(1, table.MalformedNoticeCount);
        Assert.True(policy.EntryStatus(entries[0]).Disabled);

        port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId, PermissionPayload((DungeonAId, 2)));
        ctrl.Poll(100);
        Assert.False(policy.EntryStatus(entries[0]).Disabled);
        Assert.Equal(2, flow.Select.MaxDifficulty);

        // 4. 选副本 B(diff=1) -> Timeout -> 返回选择屏提示 Timeout; 随后动态 (0,5) 降权 tier=0 重新夹紧难度
        flow.Select.Move(1);
        flow.Select.MoveDifficulty(1);
        Assert.Equal(1, flow.Select.Difficulty);
        mode = "timeout";
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => ctrl.Phase == OnlineDungeonFlowPhase.Idle && flow.Stage == GameStage.DungeonSelect);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, flow.Select.Notice!.Kind);
        Assert.Equal(1, flow.Select.Index);

        port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId, PermissionPayload((DungeonBId, 0)));
        ctrl.Poll(200);
        Assert.Equal(0, flow.Select.Difficulty);

        // 5. 建房失败:停止后台战斗跑器,释放收包权,退回选择屏提示 RoomFailed
        mode = "roomfail";
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => ctrl.Phase == OnlineDungeonFlowPhase.Idle && flow.Stage == GameStage.DungeonSelect);
        Assert.Equal(DungeonSelectNoticeKind.RoomFailed, flow.Select.Notice!.Kind);
        Assert.Equal(port.EnterExclusiveCalls, port.LeaveExclusiveCalls);
        Assert.Equal(0, disconnects);

        lock (logs) AssertSanitizedOnlineLogs(logs);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Mid_dungeon_giveup_sends_1_45_avoids_tick_abort_checks_return_area_and_handles_timeout(bool matchingArea, bool simulateTimeout)
    {
        using var fixture = new HostedGatedRoomFixture();
        (GameFlow flow, TestTownWorld world, _, _, OnlineDungeonSelectPolicy policy) = CreateSelectFixture();
        var port = new FakeSessionPort();
        var logs = new List<string>();

        var combatServer = new FakeCombatServer(CombatKit.Sequential(2));
        int giveUpCount = 0;
        port.SetResponder((cls, id, payload) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(DungeonAId)),
                    new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(2), 0, 0, 905)),
                };
            }
            if (cls == PacketClass.Cmd && id == SessionLifecyclePayloads.GiveUpGamePacketId)
            {
                giveUpCount++;
                Assert.Empty(payload); // 1/45 必须是空载荷
                if (simulateTimeout)
                {
                    return Array.Empty<CombatKit.ServerFrame>();
                }
                byte v = matchingArea ? (byte)ReturnTownId : (byte)9;
                byte a = matchingArea ? (byte)ReturnAreaId : (byte)9;
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(v, a)),
                    new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(v, a)),
                };
            }
            return combatServer.Handle(cls, id, payload);
        });

        var options = new OnlineDungeonFlowOptions
        {
            CombatOptions = new OnlineDungeonCombatOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                GiveUpTimeout = TimeSpan.FromMilliseconds(simulateTimeout ? 80 : 2000),
                SettlementTimeout = TimeSpan.FromSeconds(5),
            },
            Log = line => { lock (logs) logs.Add(line); },
            ReturnPlace = new TownPlace(ReturnTownId, ReturnAreaId),
            TransientNoticeDurationMs = 500,
        };

        using var ctrl = new OnlineDungeonFlowController(port, flow, policy, ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => fixture.Create(Templates(2), plan, Array.Empty<GateSide>()));
            return OnlineDungeonRoomBuildResult.Success(room, run!);
        }, options);

        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        flow.Select!.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Dungeon && ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        var runRef = Assert.IsType<HostedRoomRun>(flow.Run);

        // 打开系统菜单选 ReturnToTown -> 走 GiveUp(1/45), 停留在 GivingUp, Tick 不得误报 Aborted
        Assert.True(flow.ToggleMenu());
        flow.Menu.Move(1);
        flow.Menu.Choose();
        Assert.True(flow.Poll());
        Assert.Equal(OnlineDungeonFlowPhase.GivingUp, ctrl.Phase);
        Assert.NotNull(ctrl.ActiveNotice);
        Assert.Equal(DungeonSelectNoticeKind.GivingUp, ctrl.ActiveNotice.Kind);

        StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Town && ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        Assert.Equal(1, giveUpCount);
        Assert.Equal(1, flow.GiveUps);
        Assert.Equal(0, flow.Failures);
        Assert.False(runRef.Aborted);
        Assert.Equal(1, port.EnterExclusiveCalls);
        Assert.Equal(1, port.LeaveExclusiveCalls);
        Assert.Equal(world.Return, flow.Place);

        string expectedAreaTag = simulateTimeout ? "area=unknown" : (matchingArea ? "area=match" : "area=mismatch");
        lock (logs)
        {
            Assert.Contains(logs, l => l.Contains("event=return " + expectedAreaTag));
            AssertSanitizedOnlineLogs(logs);
        }
        if (simulateTimeout)
        {
            Assert.Equal(DungeonSelectNoticeKind.Timeout, ctrl.ActiveNotice?.Kind);
            ctrl.Poll(5000);
            Assert.Null(ctrl.ActiveNotice);
        }
    }

    [Fact]
    public void MultiRoom_dungeon_uses_real_dungeonPath_and_handles_room_abort_disconnect_and_quit()
    {
        using var fixture = new HostedGatedRoomFixture();
        (GameFlow flow, _, _, _, OnlineDungeonSelectPolicy policy) = CreateSelectFixture();
        var port = new FakeSessionPort();
        var disconnected = 0;

        // 建立 2 房迷宫多房间服务端: (0,0) -> (1,0)
        var multiServer = new FakeMultiRoomServer(new[]
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(1)),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId)),
        });

        port.SetResponder((cls, id, payload) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(DungeonAId)),
                    new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(1), 0, 0, 905)),
                };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, ValidSettlementABytes()),
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, ValidSettlementBBytes()),
                };
            }
            return multiServer.Handle(cls, id, payload);
        });

        var maze = new DungeonMaze("dungeon/test/dunga.dgn", 2, 1, new[] { ExitBits.Right, ExitBits.Left }, null,
            new[] { new CellRect(0, 0, 0, 0) }, new[] { new CellRect(1, 0, 1, 0) }, null, null, 1f, -1, null, null);

        string capturedPath = null;
        HostedRoomRun activeRun = null;
        var options = new OnlineDungeonFlowOptions
        {
            CombatOptions = CombatKit.Options(pollMilliseconds: 10),
            OnDisconnected = () => disconnected++,
            QuitWaitTimeoutMs = 200,
        };

        using var ctrl = new OnlineDungeonFlowController(port, flow, policy, ctx =>
        {
            capturedPath = ctx.DungeonPath;
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            var setup = new OnlineMultiRoomSetup(new[] { maze }, (entry, plan) =>
                fixture.Create(Templates(entry.Monsters), plan, new[] { ExitBits.Right, ExitBits.Left }
                    .Select((_, idx) => idx == 0 ? GateSide.Right : GateSide.Left)
                    .Where(side => entry.Maze.HasExit(entry.CellX, entry.CellY, side))));
            HostedRoomRun run = room.CreateRun(plan => fixture.Create(Templates(1), plan, Array.Empty<GateSide>()), 0, setup);
            activeRun = run;
            return OnlineDungeonRoomBuildResult.Success(room, run!);
        }, options);

        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        flow.Select!.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Dungeon && ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        Assert.Equal("dungeon/test/dunga.dgn", capturedPath);

        // 验证 HostedRoomRun.Abort 能取消战斗、释放收包权并返回城镇记一次 Fail
        activeRun!.Abort(HostedAbort.MoveTimeout);
        StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Town && ctrl.Phase == OnlineDungeonFlowPhase.Idle);
        Assert.Equal(1, flow.Failures);
        Assert.Equal(1, port.EnterExclusiveCalls);
        Assert.Equal(1, port.LeaveExclusiveCalls);
        Assert.Equal(0, disconnected);

        // 再次进入后中途模拟服务端断线:释放收包权并触发 OnDisconnected
        flow.EnterSelect("gate2");
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Dungeon && ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        port.TriggerDisconnect(OnlineDisconnectReason.PeerClosed);
        StepAndPollUntil(flow, ctrl, () => disconnected == 1 && ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        // 再次重连准备后验证 Quit 不挂起、不留无主异常
        port.IsDisconnected = false;
        ctrl.PrepareForQuit();
        Assert.Null(flow.OnlineStartDungeon);
        Assert.Null(flow.OnlineGiveUpDungeon);
    }

    [Fact]
    public void Malformed_settlement_payloads_still_release_exclusive_and_expose_Failed_parse_state()
    {
        using var fixture = new HostedGatedRoomFixture();
        (GameFlow flow, _, _, _, OnlineDungeonSelectPolicy policy) = CreateSelectFixture();
        var port = new FakeSessionPort();
        var combatServer = new FakeCombatServer(CombatKit.Sequential(1));

        port.SetResponder((cls, id, payload) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                return new[] { new CombatKit.ServerFrame(CombatKit.Noti, OnlineDungeonEntry.LoadingNoticePacketId, DungeonEntryWire.Loading27()) };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                return new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, DungeonInfoParser.PacketId, DungeonEntryWire.Info28(DungeonAId)),
                    new CombatKit.ServerFrame(CombatKit.Noti, StartMapParser.PacketId, CombatKit.Map29Payload(CombatKit.Sequential(1), 0, 0, 905)),
                };
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                return new[]
                {
                    // 故意用过短无法解析的 0x22 与 0x23 载荷
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, new byte[] { 1, 2 }),
                    new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, new byte[] { 3, 4 }),
                };
            }
            return combatServer.Handle(cls, id, payload);
        });

        var options = new OnlineDungeonFlowOptions
        {
            CombatOptions = CombatKit.Options(pollMilliseconds: 10),
        };

        using var ctrl = new OnlineDungeonFlowController(port, flow, policy, ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => fixture.Create(Templates(1), plan, Array.Empty<GateSide>()));
            return OnlineDungeonRoomBuildResult.Success(room, run!);
        }, options);

        OnlineDungeonSettlementReady ready = null;
        ctrl.SettlementStarted += r =>
        {
            r.HoldForMenu = false;
            ready = r;
        };

        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        flow.Select!.Confirm();
        Assert.True(flow.Poll());
        StepAndPollUntil(flow, ctrl, () => flow.Stage == GameStage.Dungeon && ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        var run = Assert.IsType<HostedRoomRun>(flow.Run);
        HostedRoomFixture.Kill(run.Room.Monsters[0]);
        StepAndPollUntil(flow, ctrl, () => ready != null && flow.Stage == GameStage.Town);

        Assert.Equal(1, port.LeaveExclusiveCalls);
        Assert.Equal(OnlineSettlementParseState.Failed, ready!.Data.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Failed, ready.Data.RewardParseState);
        Assert.Null(ready.Data.Result);
        Assert.Null(ready.Data.Reward);
    }

    [Fact]
    public void Permission_adapter_and_room_builder_handle_snapshots_and_missing_assets()
    {
        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonPermissionAdapter.LoadSnapshot(table, null);
        Assert.False(table.HasSnapshot);
        Assert.Equal(0, table.EntryCount);

        DungeonPermissionSnapshot snap = DungeonPermissionParser.Collect(new[]
        {
            DungeonEntryWire.Decoded(CombatKit.Noti, DungeonPermissionParser.PacketId, PermissionPayload((DungeonAId, 3), (DungeonBId, 1))),
        });
        Assert.NotNull(snap);
        OnlineDungeonPermissionAdapter.LoadSnapshot(table, snap);
        Assert.True(table.HasSnapshot);
        Assert.Equal(2, table.EntryCount);
        Assert.Equal(3, table.GetTier(DungeonAId));

        // 测试 OnlineDungeonRoomBuilder 缺 assets 时返回 Failure(NoRoom) 而不抛异常
        var combat = new OnlineDungeonCombatRunner(new ScriptedNetSession((_, _, _) => Array.Empty<CombatKit.ServerFrame>()));
        var ctx = new OnlineDungeonRoomBuildContext(DungeonAId, "dungeon/test/dunga.dgn", 0, CombatKit.Map(1), combat);
        OnlineDungeonRoomBuildResult missingAssets = OnlineDungeonRoomBuilder.Build(ctx, null, null, 1, damageOn: true);
        Assert.False(missingAssets.Succeeded);
        Assert.Equal(OnlineRoomStatus.NoRoom, missingAssets.Status);
    }
}
