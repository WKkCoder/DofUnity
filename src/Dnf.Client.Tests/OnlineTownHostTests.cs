using System.Text;
using Dnf.Data;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Client.Tests;

/// <summary>
/// 客户端在线城镇宿主编排端到端测试:
/// 涵盖 <c>SelectCharacterLoadInfo</c>(Village/UniqueId) 与 <c>TownEntrySnapshot</c> 消费、多城镇起点与回退、
/// <c>1/38</c> 切区(<c>0/23</c>+<c>0/24</c> 与 Gate 仅 <c>0/24</c>)、<c>0x44</c>/超时回退、
/// 踢下线(<c>0/199</c>)与断线回登录、普通/真界面(-dnfserverui)回选角重连、退出游戏发断线、事件成对退订与日志脱敏。
/// 全部运行在内存假网关与假传输上,不连任何真实服务器。
/// </summary>
public sealed class OnlineTownHostTests
{
    private const double FrameMs = 1000.0 / 60.0;

    private sealed class RecordingTransport : ITownCommandTransport
    {
        public event Action<TownInboundEvent> InboundReceived;
        public event Action Disconnected;

        public int SubscribeCount { get; private set; }
        public int UnsubscribeCount { get; private set; }
        public int DisconnectCalls { get; private set; }
        public bool Disposed { get; private set; }

        public List<(byte DstV, byte DstA, ushort X, ushort Y, ushort SrcV, ushort SrcA, byte Flags)> Sent { get; } = new();

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
            Sent.Add((targetVillage, targetArea, x, y, srcVillage, srcArea, flags));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            return Task.CompletedTask;
        }

        public void Push(TownInboundEvent ev) => InboundReceived?.Invoke(ev);

        public void RaiseDisconnect() => Disconnected?.Invoke();

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

        public void OutwardOf(TownExit exit, out int dx, out int dy) => _tracker.OutwardOf(exit, out dx, out dy);
        public void ClearPendingExit() => _pending = null;
        public void MoveTo(double x, double y) { HeroX = x; HeroY = y; }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            if (dirX != 0) HeroFacingLeft = dirX < 0;
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
        private readonly TownAtlas _atlas;

        public SyntheticWorld(TownAtlas atlas, int startTown)
        {
            _atlas = atlas;
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

        public TownPlace Start { get; }
        public TownPlace Return { get; }

        public IHeroSession Create(TownPlace place)
        {
            TownAreaInfo info = _atlas.GetArea(place.Town, place.Area);
            return new FakeTownSession(place.Town + "/" + place.Area, info.Walkable, info.Exits, info.Bounds, _atlas.Arrive(place));
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            return exit.HasTarget ? new TownPlace(exit.Town, exit.Area, TownEntry.Exit, from.Town, from.Area, facingLeft) : (TownPlace?)null;
        }

        public bool TryRoute(TownPlace from, out TownExit exit) => _atlas.TryRouteToDungeonGate(from, out exit);
    }

    private sealed class HostTownRig : IDisposable
    {
        private readonly object _sync = new();
        public List<string> Logs { get; } = new();
        public BackendRig Backend { get; }
        public OnlineShellHost Host { get; }
        public long Now = 1000;

        public HostTownRig(bool serverUi = false, Action<FakeSessionPort> onPort = null)
        {
            Backend = new BackendRig(configured: true, onPort: port =>
            {
                port.OnConnect = (channelPort, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, CustomRoster()));
                port.OnSwitch = (channelPort, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, CustomRoster()));
                port.OnConnectHost = (host, channelPort, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, CustomRoster()));
                port.OnSwitchHost = (host, channelPort, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, CustomRoster()));
                onPort?.Invoke(port);
            });
            var setup = new OnlineShellSetup(Backend.Template, OnlineShellConfigSource.ConfigFile, null, 31000, BackendRig.User, BackendRig.Password);
            Host = new OnlineShellHost(setup, Backend.Backend, null, line =>
            {
                lock (_sync) { Logs.Add(line); }
            }, null, serverUi: serverUi);
        }

        public void Tick(OnlineShellFrameInput input = default) => Host.Tick(input, 16, Now += 16);

        public void Until(Func<bool> done) => HostPump.Until(Host, ref Now, done);

        public void EnterInGame(int slot = 0, bool viaServerUi = false)
        {
            if (Host.Stage == OnlineShellStage.Login)
            {
                Tick(new OnlineShellFrameInput { Enter = true });
            }
            if (viaServerUi)
            {
                Until(() => Host.Stage == OnlineShellStage.ServerSelect && Host.Flow.ServerScreen.Phase == PickerPhase.Ready);
                Tick(new OnlineShellFrameInput { Enter = true });
                Until(() => Host.Stage == OnlineShellStage.ChannelSelect && Host.Flow.ChannelScreen.Phase == PickerPhase.Ready);
                Tick(new OnlineShellFrameInput { Enter = true });
            }
            Until(() => Host.Stage == OnlineShellStage.CharacterSelect);
            for (int i = 0; i < slot; i++)
            {
                Tick(new OnlineShellFrameInput { Right = true });
            }
            Tick(new OnlineShellFrameInput { Enter = true });
            Until(() => Host.InGame);
        }

        public string[] SnapshotLogs()
        {
            lock (_sync) { return Logs.ToArray(); }
        }

        public void Log(string line)
        {
            lock (_sync) { Logs.Add(line); }
        }

        public void Dispose() => Host.Dispose();
    }

    private static UserInfoNotice CustomRoster()
    {
        UserInfoTestData.EntrySpec first = UserInfoTestData.Standard(0, equipCount: 0);
        first.Uid = 9101;
        first.Name = Encoding.UTF8.GetBytes("测试角色零");
        first.Job = 0;
        first.GrowType = 0x12;
        first.Level = 55;

        UserInfoTestData.EntrySpec second = UserInfoTestData.Standard(1, equipCount: 0);
        second.Uid = 9102;
        second.Name = Encoding.UTF8.GetBytes("测试枪手一");
        second.Job = 2;
        second.GrowType = 0x01;
        second.Level = 32;

        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(new[] { first, second }), out UserInfoNotice notice));
        return notice;
    }

    private static SelectCharacterResult SelectPayload(ushort uniqueId, byte village)
    {
        var b = new NoticePayloadBuilder()
            .U8(1)             // flag = success
            .U32(1001)         // regTime
            .U32(1002)         // createTime
            .U16(uniqueId)     // uniqueId
            .U16(156)          // totalFatigue
            .U16(156)          // totalMaxFatigue
            .U16(10);          // usedFatigue
        b.U8(0);               // premium count N = 0
        b.I32(0);              // cera
        for (int i = 0; i < 20; i++)
        {
            b.U16(0).U32(0);   // 20 quest slots
        }
        for (int i = 0; i < 5; i++)
        {
            b.U32(0);          // 5 notify values
        }
        b.U8(village);         // village
        b.U8(0);               // tutorialSkipable
        b.U8(0);               // tutorial flag count b = 0
        b.U16(0).U16(0).U8(0); // battery, grownup, tradepunish
        Assert.True(SelectCharacterResultParser.TryParse(b.ToArray(), out SelectCharacterResult result));
        Assert.NotNull(result.Load);
        Assert.Equal(uniqueId, result.Load.UniqueId);
        Assert.Equal(village, result.Load.Village);
        return result;
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
            },
            ["town/b.twn"] = new[]
            {
                Line("[area]", 0, "U/Gate.map", "[gate]", 150, 150),
                Line("[area]", 1, "U/Plaza.map", "[normal]"),
            },
            ["map/t/gate.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 400, 200),
                Line("[town movable area]", 100, 290, 200, 20, 1, 1),
            },
            ["map/t/square.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 400, 200),
                Line("[town movable area]", 100, 90, 200, 20, 1, 0),
            },
            ["map/u/gate.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", 100, 290, 150, 20, 5, 1),
            },
            ["map/u/plaza.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", 100, 90, 150, 20, 5, 0),
            },
        };
        return new TownAtlas(
            new Dictionary<int, string> { [1] = "town/a.twn", [5] = "town/b.twn" },
            p => scripts.TryGetValue(p, out ScriptLine[] doc) ? doc : null,
            _ => false);
    }

    [Fact]
    public void End_to_end_controller_consumes_load_village_uid_syncs_1_38_and_sanitizes_all_logs()
    {
        using var rig = new HostTownRig(onPort: port =>
        {
            port.OnSelect = (slot, ct) => Task.FromResult(
                new OnlineSelectReport(true, false, null, SelectPayload(uniqueId: 4567, village: 5), null, null));
        });
        rig.EnterInGame(slot: 0);

        TownAtlas atlas = BuildAtlas();
        var transport = new RecordingTransport();
        using OnlineTownController controller = OnlineTownController.Create(
            rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: transport, commandTimeoutMs: 500);

        Assert.Equal("测试角色零", controller.Profile.Name);
        Assert.Equal((ushort)4567, controller.Profile.ServerUniqueId);
        Assert.Equal(5, controller.StartTown);
        Assert.False(controller.Profile.JobFallback);

        ITownWorld wrapped = controller.WrapWorld(new SyntheticWorld(atlas, controller.StartTown));
        var flow = new GameFlow(wrapped, (p, _) => null, new[] { new DungeonEntry("dungeon/t.dgn", "T") });
        flow.EnterTown("start");
        Assert.Equal(new TownPlace(5, 0), flow.Place);

        // Walk south toward Normal (5,1) -> triggers 1/38 with uid=4567 profile and pauses
        for (int i = 0; i < 200 && !controller.IsAreaChangePending; i++)
        {
            controller.RecordBeforeStep();
            flow.Session.Update(FrameMs, 0, 1);
            controller.Poll(rig.Now += 16);
            flow.Poll();
        }
        Assert.True(controller.IsAreaChangePending);
        Assert.Single(transport.Sent);
        Assert.Equal((byte)5, transport.Sent[0].DstV);
        Assert.Equal((byte)1, transport.Sent[0].DstA);

        // Confirmed by 0/23 + 0/24 using Load.UniqueId (4567)
        transport.Push(new TownInboundEvent(TownInboundKind.UserArea, 5, 1, uniqueId: 4567));
        transport.Push(new TownInboundEvent(TownInboundKind.AreaUsers, 5, 1, areaUsers: new[] { new TownInboundAreaUser(4567, 150, 295) }));
        controller.Poll(rig.Now += 16);
        flow.Poll();
        Assert.Equal(new TownPlace(5, 1), flow.Place);

        controller.Dispose();
        Assert.Equal(transport.SubscribeCount, transport.UnsubscribeCount);

        string[] logs = rig.SnapshotLogs();
        Assert.Contains(logs, l => l.Contains("DNF_ONLINE stage=town event=enter mode=online") && l.Contains("townfallback=false"));
        Assert.Contains(logs, l => l.Contains("DNF_ONLINE stage=town event=areachange outcome=ok"));
        foreach (string line in logs)
        {
            Assert.DoesNotContain("测试角色零", line);
            Assert.DoesNotContain("4567", line);
            Assert.DoesNotContain("9101", line);
            Assert.DoesNotContain("295", line);
            Assert.DoesNotContain(BackendRig.Password, line);
            Assert.DoesNotContain("127.0.0.1", line);
        }
    }

    [Fact]
    public void Kicked_0_199_and_disconnect_return_to_login_without_auto_retry_loop()
    {
        using var rig = new HostTownRig(onPort: port =>
        {
            port.OnSelect = (slot, ct) => Task.FromResult(
                new OnlineSelectReport(true, false, null, SelectPayload(uniqueId: 4601, village: 1), null, null));
        });
        rig.EnterInGame(slot: 0);

        TownAtlas atlas = BuildAtlas();
        var transport = new RecordingTransport();
        using var controller = OnlineTownController.Create(rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: transport);

        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 8));
        controller.Poll(rig.Now += 16);
        rig.Tick();

        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Host.Flow.Notice?.Kind);
        Assert.Contains(rig.SnapshotLogs(), l => l.Contains("DNF_ONLINE stage=town event=disconnect reason=kicked code=8"));

        // Advancing many frames stays on Login without auto-reconnecting
        int loginsBefore = rig.Backend.Port(0).LoginCalls;
        for (int i = 0; i < 60; i++)
        {
            rig.Tick();
        }
        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(loginsBefore, rig.Backend.Port(0).LoginCalls);
    }

    [Fact]
    public void Transport_loss_returns_host_to_login_cleanly()
    {
        using var rig = new HostTownRig(onPort: port =>
        {
            port.OnSelect = (slot, ct) => Task.FromResult(
                new OnlineSelectReport(true, false, null, SelectPayload(uniqueId: 4602, village: 1), null, null));
        });
        rig.EnterInGame(slot: 0);

        TownAtlas atlas = BuildAtlas();
        var transport = new RecordingTransport();
        using var controller = OnlineTownController.Create(rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: transport);

        transport.RaiseDisconnect();
        controller.Poll(rig.Now += 16);
        rig.Tick();

        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Host.Flow.Notice?.Kind);
        Assert.Contains(rig.SnapshotLogs(), l => l.Contains("DNF_ONLINE stage=town event=disconnect reason=connection_lost"));
    }

    [Fact]
    public void Flush_disconnect_before_dispose_logs_queued_kick_when_shell_recovers_first()
    {
        using var rig = new HostTownRig(onPort: port =>
        {
            port.OnSelect = (slot, ct) => Task.FromResult(
                new OnlineSelectReport(true, false, null, SelectPayload(uniqueId: 4603, village: 1), null, null));
        });
        rig.EnterInGame(slot: 0);

        TownAtlas atlas = BuildAtlas();
        var transport = new RecordingTransport();
        using var controller = OnlineTownController.Create(rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: transport);

        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 9));
        controller.FlushDisconnectBeforeDispose(rig.Now += 16);
        Assert.True(controller.DisconnectLogged);
        Assert.Contains(rig.SnapshotLogs(), l => l.Contains("reason=kicked code=9"));
    }

    [Fact]
    public void Reconnect_clears_previous_select_character_load_and_snapshot()
    {
        int attempt = 0;
        using var rig = new HostTownRig(onPort: port =>
        {
            port.OnSelect = (slot, ct) => Task.FromResult(attempt++ == 0
                ? new OnlineSelectReport(true, false, null, SelectPayload(uniqueId: 4610, village: 5), null, null)
                : new OnlineSelectReport(true, false, null));
        });
        rig.EnterInGame(slot: 0);
        Assert.NotNull(rig.Host.SelectCharacter);
        Assert.Equal(5, rig.Host.SelectCharacter.Load.Village);

        rig.Host.ReturnToCharacterSelect();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);
        Assert.Null(rig.Host.SelectCharacter);

        rig.EnterInGame(slot: 1);
        Assert.Null(rig.Host.SelectCharacter);
    }

    [Fact]
    public void Return_to_character_select_works_under_both_auto_channel_and_server_ui_and_loads_new_character()
    {
        foreach (bool serverUi in new[] { false, true })
        {
            using var rig = new HostTownRig(serverUi: serverUi, onPort: port =>
            {
                port.OnSelect = (slot, ct) => Task.FromResult(
                    new OnlineSelectReport(true, false, null,
                        slot == 0 ? SelectPayload(4701, village: 5) : SelectPayload(4702, village: 1), null, null));
            });
            rig.EnterInGame(slot: 0, viaServerUi: serverUi);
            Assert.Equal(OnlineShellViewChange.StartGame, rig.Host.TakeViewChange());

            TownAtlas atlas = BuildAtlas();
            var firstTransport = new RecordingTransport();
            using var firstController = OnlineTownController.Create(rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: firstTransport);
            Assert.Equal(5, firstController.StartTown);
            Assert.False(firstController.Profile.JobFallback);

            // System menu "Character Select": unsubscribes town, reconnects active channel even with serverUi=true, arrives at CharacterSelect!
            firstController.ReturnToCharacterSelect();
            Assert.Equal(firstTransport.SubscribeCount, firstTransport.UnsubscribeCount);
            rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);
            Assert.Equal(OnlineShellViewChange.ShowShell, rig.Host.TakeViewChange());
            Assert.True(rig.Backend.Port(0).SwitchCalls >= 1 || rig.Backend.Port(0).SwitchHostCalls >= 1);

            // Pick slot 1 (different character, Gunner -> fallback active, Village = 1)
            rig.EnterInGame(slot: 1, viaServerUi: false);
            Assert.Equal(OnlineShellViewChange.ResumeGame, rig.Host.TakeViewChange());

            var secondTransport = new RecordingTransport();
            using var secondController = OnlineTownController.Create(rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: secondTransport);
            Assert.Equal("测试枪手一", secondController.Profile.Name);
            Assert.True(secondController.Profile.JobFallback);
            Assert.Equal((ushort)4702, secondController.Profile.ServerUniqueId);
            Assert.Equal(1, secondController.StartTown);
        }
    }

    [Fact]
    public async Task Quit_game_gracefully_unsubscribes_and_disposes_backend_session()
    {
        using var rig = new HostTownRig(onPort: port =>
        {
            port.OnSelect = (slot, ct) => Task.FromResult(
                new OnlineSelectReport(true, false, null, SelectPayload(uniqueId: 4801, village: 1), null, null));
        });
        rig.EnterInGame(slot: 0);

        TownAtlas atlas = BuildAtlas();
        var transport = new RecordingTransport();
        var controller = OnlineTownController.Create(rig.Host, atlas, areaSyncEnabled: true, log: rig.Log, customTransport: transport);

        await controller.QuitGameAsync();

        Assert.Equal(transport.SubscribeCount, transport.UnsubscribeCount);
        Assert.True(transport.Disposed);
        Assert.True(rig.Backend.Port(0).WaitDisposed());
        string[] logs = rig.SnapshotLogs();
        Assert.Contains(logs, l => l.Contains("DNF_ONLINE stage=town event=exit target=quit outcome=requested"));
        Assert.Contains(logs, l => l.Contains("DNF_ONLINE stage=town event=exit target=quit outcome=done"));
        Assert.DoesNotContain(logs, l => l.Contains("event=disconnect"));
    }

    [Fact]
    public async Task Session_transport_pairs_dispose_cleanly_on_real_session_instance()
    {
        Assert.True(GatewayOptions.TryCreate("http://127.0.0.1:18080", GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey()),
            TimeSpan.FromSeconds(1), out GatewayOptions options, out _));
        var settings = new OnlineEntrySettings
        {
            Gateway = options,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = 7,
            ChannelPort = 31000,
        };
        using var session = new OnlineClientSession(settings);
        var transport = new OnlineTownSessionTransport(session);

        transport.Dispose();
        transport.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transport.SendSetUserAreaAsync(1, 1, 100, 100, 1, 0, 5, CancellationToken.None));
    }
}
