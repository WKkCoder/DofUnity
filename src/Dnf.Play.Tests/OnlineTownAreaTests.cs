using Dnf.Data;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 在线城镇选镇起点、局部图号到服务端编号映射、Gate 仅 0/24 vs 普通区域 0/23+0/24、
/// 严格单在途 1/38、0x44 分钟拒绝与超时回退、强切开关与日志脱敏验证。
/// </summary>
public sealed class OnlineTownAreaTests
{
    private const double FrameMs = 1000.0 / 60.0;

    private sealed class FakeTransport : ITownCommandTransport
    {
        public event Action<TownInboundEvent> InboundReceived;
        public event Action Disconnected;

        public int SubscribeCount { get; private set; }
        public int UnsubscribeCount { get; private set; }
        public bool Disposed { get; private set; }
        public int DisconnectCalls { get; private set; }

        public List<(byte DstV, byte DstA, ushort X, ushort Y, ushort SrcV, ushort SrcA, byte Flags, CancellationToken Ct)> Sent { get; } = new();
        public Func<(byte DstV, byte DstA), Task> SendBehavior { get; set; }

        event Action<TownInboundEvent> ITownCommandTransport.InboundReceived
        {
            add { SubscribeCount++; InboundReceived += value; }
            remove { UnsubscribeCount++; InboundReceived -= value; }
        }

        event Action ITownCommandTransport.Disconnected
        {
            add { SubscribeCount++; Disconnected += value; }
            remove { UnsubscribeCount++; Disconnected -= value; }
        }

        public Task SendSetUserAreaAsync(byte targetVillage, byte targetArea, ushort x, ushort y,
            ushort srcVillage, ushort srcArea, byte flags, CancellationToken cancellationToken)
        {
            Sent.Add((targetVillage, targetArea, x, y, srcVillage, srcArea, flags, cancellationToken));
            if (SendBehavior != null)
            {
                return SendBehavior((targetVillage, targetArea));
            }
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            return Task.CompletedTask;
        }

        public void Push(TownInboundEvent ev) => InboundReceived?.Invoke(ev);

        public void RaiseDisconnected() => Disconnected?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeTownSession : IHeroSession, ITownExitSource, ITownExitReset
    {
        private readonly TownExitTracker _tracker;
        private readonly IReadOnlyList<WorldRect> _walk;
        private TownExit? _pending;

        public FakeTownSession(string label, IReadOnlyList<WorldRect> walk, IReadOnlyList<TownExit> exits, WorldRect? bounds, TownArrival arrival)
        {
            MapPath = "fake/" + label;
            _walk = walk;
            _tracker = new TownExitTracker(exits, bounds);
            HeroX = arrival.X;
            HeroY = arrival.Y;
            HeroFacingLeft = arrival.FacingLeft;
            _tracker.Arrive(HeroX, HeroY);
        }

        public string MapPath { get; }
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public TownExit? PendingExit => _pending;
        public double HeroX { get; private set; }
        public double HeroY { get; private set; }
        public bool HeroFacingLeft { get; private set; }
        public int ClearCalls { get; private set; }

        public void OutwardOf(TownExit exit, out int dx, out int dy) => _tracker.OutwardOf(exit, out dx, out dy);

        public void ClearPendingExit()
        {
            ClearCalls++;
            _pending = null;
        }

        public void StepBackTo(double x, double y)
        {
            HeroX = x;
            HeroY = y;
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            if (dirX != 0)
            {
                HeroFacingLeft = dirX < 0;
            }
            double step = 220.0 * deltaMs / 1000.0;
            Move(dirX * step, true);
            Move(dirY * step, false);
            if (!_pending.HasValue)
            {
                _pending = _tracker.Step(HeroX, HeroY, dirX, dirY);
            }
        }

        private void Move(double distance, bool horizontal)
        {
            double remaining = Math.Abs(distance);
            double sign = Math.Sign(distance);
            while (remaining > 1e-9)
            {
                double d = Math.Min(1.0, remaining);
                double nx = horizontal ? HeroX + sign * d : HeroX;
                double ny = horizontal ? HeroY : HeroY + sign * d;
                if (!_walk.Any(r => r.Contains(nx, ny)))
                {
                    return;
                }
                HeroX = nx;
                HeroY = ny;
                remaining -= d;
            }
        }

        public List<SceneSprite> Build(long timeMs) => new();
    }

    private sealed class SyntheticWorld : ITownWorld
    {
        public SyntheticWorld(TownAtlas atlas, int startTown)
        {
            Atlas = atlas;
            TownDef def = atlas.GetTown(startTown);
            int gate = -1;
            int first = -1;
            foreach (TownArea a in def.Areas)
            {
                if (first < 0 || a.Id < first) first = a.Id;
                if (a.Kind == TownAreaKind.Gate && (gate < 0 || a.Id < gate)) gate = a.Id;
            }
            Start = new TownPlace(startTown, gate >= 0 ? gate : first);
            Return = atlas.FindDungeonGate(startTown) ?? Start;
        }

        public TownAtlas Atlas { get; }
        public TownPlace Start { get; }
        public TownPlace Return { get; }

        public IHeroSession Create(TownPlace place)
        {
            TownAreaInfo info = Atlas.GetArea(place.Town, place.Area);
            return new FakeTownSession(place.Town + "/" + place.Area, info.Walkable, info.Exits, info.Bounds, Atlas.Arrive(place));
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            return exit.HasTarget ? new TownPlace(exit.Town, exit.Area, TownEntry.Exit, from.Town, from.Area, facingLeft) : (TownPlace?)null;
        }

        public bool TryRoute(TownPlace from, out TownExit exit) => Atlas.TryRouteToDungeonGate(from, out exit);
    }

    private static ScriptLine Line(string tag, params object[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = values[i] is int iv ? new ScriptValue(ScriptValueKind.Int, iv, null) : new ScriptValue(ScriptValueKind.String, 0, (string)values[i]);
        }
        return new ScriptLine(tag, list);
    }

    private static TownAtlas BuildAtlas()
    {
        var scripts = new Dictionary<string, ScriptLine[]>
        {
            ["town/a.twn"] = new[]
            {
                Line("[area]", 0, "T/Gate.map", "[gate]", 200, 150),
                Line("[area]", 1, "T/Square.map", "[normal]"),
                Line("[area]", 2, "T/Dg.map", "[dungeon gate]", 1),
                Line("[area]", 3, "T/Safe.map", "[safe zone a]"),
            },
            ["town/b.twn"] = new[]
            {
                Line("[area]", 0, "U/Gate.map", "[gate]", 150, 150),
                Line("[area]", 1, "U/Plaza.map", "[normal]"),
            },
            ["map/t/gate.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 400, 200),
                Line("[town movable area]", 100, 290, 200, 20, 1, 1, 350, 120, 50, 160, 1, 99),
            },
            ["map/t/square.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 600, 200),
                Line("[town movable area]", 200, 90, 100, 30, 1, 0, 580, 120, 30, 160, 1, 2),
            },
            ["map/t/dg.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", -10, 120, 20, 160, 1, 1, 290, 120, 20, 160, -1, -1),
            },
            ["map/t/safe.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
            },
            ["map/u/gate.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", 270, 100, 40, 200, 5, 1),
            },
            ["map/u/plaza.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", 0, 100, 30, 200, 5, 0),
            },
        };
        return new TownAtlas(
            new Dictionary<int, string> { [1] = "town/a.twn", [5] = "town/b.twn" },
            p => scripts.TryGetValue(p, out ScriptLine[] doc) ? doc : null,
            _ => false);
    }

    private static GameFlow CreateFlow(ITownWorld world)
    {
        return new GameFlow(world, (p, _) => null, new[] { new DungeonEntry("dungeon/t.dgn", "T") });
    }

    private static void PushIntoExit(GameFlow flow, OnlineTownWorld onlineWorld, int dirX, int dirY, ref long nowMs, int maxFrames = 300)
    {
        for (int i = 0; i < maxFrames && !onlineWorld.IsAreaChangePending; i++)
        {
            onlineWorld.RecordSafePosition();
            flow.Session.Update(FrameMs, dirX, dirY);
            onlineWorld.Poll(nowMs += 16);
            flow.Poll();
        }
    }

    [Fact]
    public void Start_town_resolves_server_village_and_falls_back_to_default_one_when_unmapped()
    {
        TownAtlas atlas = BuildAtlas();

        Assert.Equal(5, OnlineTownAreaRules.ResolveStartTown(atlas, 5, out bool fallbackOk));
        Assert.False(fallbackOk);

        Assert.Equal(1, OnlineTownAreaRules.ResolveStartTown(atlas, 99, out bool fallbackBad));
        Assert.True(fallbackBad);

        Assert.Equal(1, OnlineTownAreaRules.ResolveStartTown(atlas, null, out bool fallbackNull));
        Assert.True(fallbackNull);
    }

    [Fact]
    public void Area_completion_distinguishes_gate_0_24_only_from_normal_0_23_plus_0_24_and_checks_entrance_kind()
    {
        TownAtlas atlas = BuildAtlas();
        TownAreaInfo gate = atlas.GetArea(1, 0);
        TownAreaInfo normal = atlas.GetArea(1, 1);
        TownAreaInfo dgate = atlas.GetArea(1, 2);
        TownAreaInfo safe = atlas.GetArea(1, 3);

        Assert.True(OnlineTownAreaRules.IsAreaComplete(gate, receivedUserArea: false, receivedAreaUsers: true));
        Assert.False(OnlineTownAreaRules.IsAreaComplete(gate, receivedUserArea: true, receivedAreaUsers: false));

        Assert.False(OnlineTownAreaRules.IsAreaComplete(normal, receivedUserArea: false, receivedAreaUsers: true));
        Assert.False(OnlineTownAreaRules.IsAreaComplete(normal, receivedUserArea: true, receivedAreaUsers: false));
        Assert.True(OnlineTownAreaRules.IsAreaComplete(normal, receivedUserArea: true, receivedAreaUsers: true));

        Assert.True(OnlineTownAreaRules.IsDungeonEntranceArea(dgate, 50));
        Assert.False(OnlineTownAreaRules.IsDungeonEntranceArea(safe, 50));
        Assert.True(OnlineTownAreaRules.IsDungeonEntranceArea(safe, 1));

        Assert.False(OnlineTownAreaRules.TryMapToServerArea(atlas, new TownPlace(256, 0), out _, out _));
        Assert.False(OnlineTownAreaRules.TryMapToServerArea(atlas, new TownPlace(-1, 0), out _, out _));
        Assert.True(OnlineTownAreaRules.TryMapToServerArea(atlas, new TownPlace(1, 2), out byte v, out byte a));
        Assert.Equal((byte)1, v);
        Assert.Equal((byte)2, a);
    }

    [Fact]
    public void Coordinator_enforces_single_inflight_matches_self_uid_and_unsubscribes_on_dispose()
    {
        TownAtlas atlas = BuildAtlas();
        var transport = new FakeTransport();
        using var coord = new OnlineTownCommandCoordinator(transport, selfUniqueId: 9012, areaSyncEnabled: true, timeoutMs: 2000);

        Assert.True(transport.SubscribeCount >= 2);
        Assert.True(coord.BeginAreaChange(new TownPlace(1, 0), new TownPlace(1, 1), atlas.GetArea(1, 1), 1, 1, 200, 295, 1, 0, 100));
        Assert.False(coord.BeginAreaChange(new TownPlace(1, 0), new TownPlace(1, 2), atlas.GetArea(1, 2), 1, 2, 200, 295, 1, 0, 110));
        Assert.Single(transport.Sent);

        // Wrong UID and wrong area notices are ignored (including right area + wrong UID and right UID + wrong area)
        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 1, 1, uniqueId: 9999));
        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 9, 1, uniqueId: 9012));
        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 1, 9, uniqueId: 9012));
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 9, areaUsers: new[] { new TownInboundAreaUser(9012, 200, 295) }));
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 1, areaUsers: new[] { new TownInboundAreaUser(9999, 200, 295) }));
        // Even if 0/24 contains self (9012), wrong-UID 0/23 must NOT latch GotUserArea!
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 1, areaUsers: new[]
        {
            new TownInboundAreaUser(9001, 10, 10),
            new TownInboundAreaUser(9012, 200, 295),
        }));
        Assert.Equal(AreaChangeStatus.None, coord.Poll(2099).Status); // boundary check: started=100, timeout=2000 -> 2099 < 2100 not timed out
        Assert.True(coord.HasInFlight);

        // Self 0/23 arrives after 0/24 -> completes!
        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 1, 1, uniqueId: 9012));
        AreaChangeResult ok = coord.Poll(2099);
        Assert.Equal(AreaChangeStatus.Confirmed, ok.Status);
        Assert.False(coord.HasInFlight);

        coord.AreaSyncEnabled = false;
        Assert.False(coord.BeginAreaChange(new TownPlace(1, 0), new TownPlace(1, 1), atlas.GetArea(1, 1), 1, 1, 200, 295, 1, 0, 2200));
        Assert.Single(transport.Sent);

        coord.Dispose();
        Assert.Equal(transport.SubscribeCount, transport.UnsubscribeCount);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void Coordinator_handles_generic_set_user_area_rejection_and_exact_timeout_boundary()
    {
        TownAtlas atlas = BuildAtlas();
        var transport = new FakeTransport();
        using var coord = new OnlineTownCommandCoordinator(transport, selfUniqueId: 9055, areaSyncEnabled: true, timeoutMs: 500);

        Assert.True(coord.BeginAreaChange(new TownPlace(1, 0), new TownPlace(1, 1), atlas.GetArea(1, 1), 1, 1, 200, 295, 1, 0, 1000));
        transport.Push(new TownInboundEvent(TownInboundKind.SetUserAreaError, errorCode: 0x11, remainingMinutes: 0));
        AreaChangeResult rejected = coord.Poll(1050);
        Assert.Equal(AreaChangeStatus.RolledBack, rejected.Status);
        Assert.Equal((byte)0x11, rejected.ErrorCode);
        Assert.False(coord.HasInFlight);

        // Wrong UID 0/24 must NOT latch GotAreaUsers even after self 0/23 arrived!
        Assert.True(coord.BeginAreaChange(new TownPlace(1, 0), new TownPlace(1, 1), atlas.GetArea(1, 1), 1, 1, 200, 295, 1, 0, 1500));
        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 1, 1, uniqueId: 9055));
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 1, areaUsers: new[] { new TownInboundAreaUser(9999, 200, 295) }));
        Assert.Equal(AreaChangeStatus.None, coord.Poll(1550).Status);
        Assert.True(coord.HasInFlight);
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 1, areaUsers: new[] { new TownInboundAreaUser(9055, 200, 295) }));
        Assert.Equal(AreaChangeStatus.Confirmed, coord.Poll(1560).Status);

        Assert.True(coord.BeginAreaChange(new TownPlace(1, 0), new TownPlace(1, 1), atlas.GetArea(1, 1), 1, 1, 200, 295, 1, 0, 2000));
        Assert.Equal(AreaChangeStatus.None, coord.Poll(2499).Status);
        Assert.Equal(AreaChangeStatus.Timeout, coord.Poll(2500).Status);
        Assert.False(coord.HasInFlight);
    }

    [Fact]
    public void Normal_and_gate_area_changes_sync_with_server_then_transition_flow_or_rollback_on_forbidden_and_timeout()
    {
        TownAtlas atlas = BuildAtlas();
        var transport = new FakeTransport();
        var logs = new List<string>();
        using var coord = new OnlineTownCommandCoordinator(transport, selfUniqueId: 9022, areaSyncEnabled: true, timeoutMs: 1000);
        var world = new OnlineTownWorld(new SyntheticWorld(atlas, 1), atlas, coord, logs.Add);
        GameFlow flow = CreateFlow(world);
        flow.EnterTown("start");

        long now = 1000;
        // 1. Walk south into Normal (1,1): sends 1/38 and holds at (1,0) until 0/23 + 0/24
        PushIntoExit(flow, world, 0, 1, ref now);
        Assert.True(world.IsAreaChangePending);
        Assert.Equal(OnlineTownPromptKind.Waiting, world.CurrentPrompt?.Kind);
        Assert.Equal(new TownPlace(1, 0), flow.Place);
        Assert.Single(transport.Sent);
        Assert.Equal((byte)1, transport.Sent[0].DstV);
        Assert.Equal((byte)1, transport.Sent[0].DstA);
        Assert.Equal((ushort)1, transport.Sent[0].SrcV);
        Assert.Equal((ushort)0, transport.Sent[0].SrcA);
        Assert.Equal((byte)5, transport.Sent[0].Flags);

        // Server rejects with 0x44 + 7 minutes -> stays in (1,0), prompt visible, clears pending exit
        transport.Push(new TownInboundEvent(TownInboundKind.SetUserAreaError, errorCode: 0x44, remainingMinutes: 7));
        AreaChangeResult forbidden = world.Poll(now += 16);
        Assert.Equal(AreaChangeStatus.Forbidden, forbidden.Status);
        Assert.Equal(7, forbidden.RemainingMinutes);
        Assert.False(world.IsAreaChangePending);
        Assert.Equal(new TownPlace(1, 0), flow.Place);
        Assert.Equal(OnlineTownPromptKind.ForbiddenMinutes, world.CurrentPrompt?.Kind);
        Assert.Contains("7 分钟", world.CurrentPrompt.Message(true));
        Assert.Null(((FakeTownSession)flow.Session).PendingExit);

        // Cooldown passes -> retry walking into (1,1) -> 0/23 + 0/24 confirms transition!
        now += 500;
        world.Poll(now);
        PushIntoExit(flow, world, 0, 1, ref now);
        Assert.Equal(2, transport.Sent.Count);

        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 1, 1, uniqueId: 9022));
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 1, areaUsers: new[] { new TownInboundAreaUser(9022, 200, 295) }));
        AreaChangeResult confirmed = world.Poll(now += 16);
        Assert.Equal(AreaChangeStatus.Confirmed, confirmed.Status);
        flow.Poll();
        Assert.Equal(new TownPlace(1, 1), flow.Place);
        Assert.Null(world.CurrentPrompt);

        // 2. Walk back into Gate (1,0): walk out first, then north -> Gate completes on 0/24 ONLY!
        var square = (FakeTownSession)flow.Session;
        square.StepBackTo(250, 140);
        square.ClearPendingExit();
        now += 16;
        world.Poll(now);
        PushIntoExit(flow, world, 0, -1, ref now);
        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal((byte)0, transport.Sent[2].DstA);

        // Timeout first -> silent rollback (prompt is null, hero stays in 1/1)
        AreaChangeResult timedOut = world.Poll(now + 1000);
        Assert.Equal(AreaChangeStatus.Timeout, timedOut.Status);
        Assert.Null(world.CurrentPrompt);
        Assert.Equal(new TownPlace(1, 1), flow.Place);

        // Retry after cooldown -> send only 0/24 -> Gate confirms immediately!
        now += 1500;
        world.Poll(now);
        PushIntoExit(flow, world, 0, -1, ref now);
        Assert.Equal(4, transport.Sent.Count);
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 1, 0, areaUsers: new[] { new TownInboundAreaUser(9022, 200, 100) }));
        Assert.Equal(AreaChangeStatus.Confirmed, world.Poll(now += 16).Status);
        flow.Poll();
        Assert.Equal(new TownPlace(1, 0), flow.Place);

        // Sanitized logs: no coordinates, no village/area ids, no UIDs
        Assert.Contains(logs, l => l.Contains("outcome=forbidden") && l.Contains("minutes=7"));
        Assert.Contains(logs, l => l.Contains("outcome=timeout"));
        Assert.Contains(logs, l => l.Contains("outcome=ok"));
        foreach (string line in logs)
        {
            Assert.DoesNotContain("9022", line);
            Assert.DoesNotContain("295", line);
            Assert.DoesNotContain("village", line);
        }
    }

    [Fact]
    public void Unmapped_area_refuses_send_and_kill_switch_falls_back_to_local_transition()
    {
        TownAtlas atlas = BuildAtlas();
        var transport = new FakeTransport();
        var logs = new List<string>();
        using var coord = new OnlineTownCommandCoordinator(transport, selfUniqueId: 9033, areaSyncEnabled: true);
        var world = new OnlineTownWorld(new SyntheticWorld(atlas, 1), atlas, coord, logs.Add);
        GameFlow flow = CreateFlow(world);
        flow.EnterTown("start");

        // Town 1 Gate has east exit -> area 99 (not in atlas): must refuse without sending 1/38
        long now = 100;
        var session = (FakeTownSession)flow.Session;
        session.StepBackTo(330, 180);
        for (int i = 0; i < 60 && flow.StageSerial == 1 && !world.IsAreaChangePending; i++)
        {
            world.RecordSafePosition();
            flow.Session.Update(FrameMs, 1, 0);
            world.Poll(now += 16);
            flow.Poll();
        }

        Assert.Empty(transport.Sent);
        Assert.Equal(OnlineTownPromptKind.UnmappedArea, world.CurrentPrompt?.Kind);
        Assert.Equal(new TownPlace(1, 0), flow.Place);
        Assert.Contains(logs, l => l.Contains("outcome=unmapped"));

        // Kill-switch off -> walk south into Normal (1,1) transitions locally without sending 1/38
        coord.AreaSyncEnabled = false;
        session.StepBackTo(200, 270);
        session.ClearPendingExit();
        now += 500;
        world.Poll(now);
        for (int i = 0; i < 120 && flow.Place.Value.Area == 0; i++)
        {
            world.RecordSafePosition();
            flow.Session.Update(FrameMs, 0, 1);
            world.Poll(now += 16);
            flow.Poll();
        }

        Assert.Empty(transport.Sent);
        Assert.Equal(new TownPlace(1, 1), flow.Place);
        Assert.Contains(logs, l => l.Contains("outcome=disabled"));
    }

    [Fact]
    public void Send_fault_rolls_back_hero_cleanly()
    {
        TownAtlas atlas = BuildAtlas();
        var transport = new FakeTransport
        {
            SendBehavior = _ => Task.FromException(new IOException("broken pipe")),
        };
        using var coord = new OnlineTownCommandCoordinator(transport, selfUniqueId: 9044, areaSyncEnabled: true);
        var world = new OnlineTownWorld(new SyntheticWorld(atlas, 1), atlas, coord);
        GameFlow flow = CreateFlow(world);
        flow.EnterTown("start");

        long now = 100;
        PushIntoExit(flow, world, 0, 1, ref now);
        AreaChangeResult result = world.Poll(now += 16);

        Assert.Equal(AreaChangeStatus.SendFailed, result.Status);
        Assert.Equal(new TownPlace(1, 0), flow.Place);
        Assert.Equal(OnlineTownPromptKind.Rejected, world.CurrentPrompt?.Kind);
        Assert.False(world.IsAreaChangePending);
    }
}
