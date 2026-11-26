using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dnf.Game;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.NetFake;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Xunit;

namespace Dnf.Client.Tests;

/// <summary>
/// r20-e2e 阶段 1 进程内端到端回归测试：
/// 真实 <see cref="OnlineShellBackend"/> + 真实 <see cref="OnlineShellHost"/> + 真实 <see cref="OnlineTownController"/> +
/// 真实 <see cref="OnlineDungeonFlowController"/> + 真实 <see cref="OnlineSettlementController"/> + <see cref="OnlineAutoDriver"/>
/// 经由真实回环 TCP（端口 0、真加密、真 561 信封、真目录服务）连向 <see cref="LoopbackOnlineStack"/>，
/// 断言完整 <c>DNF_ONLINE</c> 日志序列逐行相等、服务端命令序列每条恰一次、以及五种故障剧本与资源零泄漏。
/// </summary>
public sealed class OnlineFullFlowLoopbackTests
{
    private const int DungeonAId = 901;
    private const int TownId = 1;
    private const int AreaId = 0;
    private const double FrameMs = 1000.0 / 60.0;

    private sealed class E2eHarness : IDisposable
    {
        public readonly LoopbackOnlineStack Stack;
        public readonly HostedGatedRoomFixture Fixture;
        public readonly OnlineShellHost Shell;
        public readonly OnlineAutoDriver Driver;
        public readonly List<string> Logs = new List<string>();

        public GameFlow Flow { get; private set; }
        public HeroProgress Progress { get; private set; }
        public OnlineTownController TownController { get; private set; }
        public OnlineDungeonFlowController DungeonController { get; private set; }
        public OnlineSettlementController SettlementController { get; private set; }

        private readonly bool _multiRoom;
        private readonly long _settlementTimeoutMs;
        private readonly object _logLock = new object();
        private bool _gameStarted;

        public E2eHarness(
            FakeChannelScenario scenario,
            OnlineAutoOptions autoOptions,
            bool serverUi = true,
            bool multiRoom = false,
            long settlementTimeoutMs = 4000)
        {
            _multiRoom = multiRoom;
            _settlementTimeoutMs = settlementTimeoutMs;
            Stack = new LoopbackOnlineStack(scenario);
            Fixture = new HostedGatedRoomFixture();

            OnlineEntrySettings settings = Stack.CreateEntrySettings();
            var setup = new OnlineShellSetup(
                settings,
                OnlineShellConfigSource.LocalDev,
                null,
                Stack.ChannelPort,
                LoopbackOnlineStack.DefaultAccount,
                LoopbackOnlineStack.DefaultPassword);

            Shell = OnlineShellHost.Create(
                setup,
                art: null,
                log: AddLog,
                serverUi: serverUi);
            Driver = new OnlineAutoDriver(autoOptions, AddLog);
        }

        private void AddLog(string line)
        {
            if (line == null)
            {
                return;
            }
            lock (_logLock)
            {
                Logs.Add(line);
            }
        }

        public string[] SnapshotLogs()
        {
            lock (_logLock)
            {
                return Logs.ToArray();
            }
        }

        public void RunUntilFinished(int timeoutMs = 20_000)
        {
            var sw = Stopwatch.StartNew();
            long lastMs = 0;

            while (!Driver.Finished && sw.ElapsedMilliseconds < timeoutMs)
            {
                long nowMs = sw.ElapsedMilliseconds;
                // 保持模拟时钟与墙钟 1:1 推进（与 Unity 每帧 unscaledDeltaTime 一致，封顶 50ms），
                // 防止紧循环把 60 秒模拟超时在几百毫秒墙钟内燃尽
                double deltaMs = Math.Min(Math.Max(nowMs - lastMs, 1.0), 50.0);
                lastMs = nowMs;

                Shell.Tick(default(OnlineShellFrameInput), deltaMs, nowMs);
                Driver.StepShell(Shell.Flow, nowMs);
                if (Driver.Finished)
                {
                    break;
                }

                OnlineShellViewChange view = Shell.TakeViewChange();
                if (view == OnlineShellViewChange.StartGame && !_gameStarted)
                {
                    StartInGame();
                }

                if (Shell.InGame && Flow != null)
                {
                    TownController?.Poll(nowMs);
                    DungeonController?.Poll(nowMs);
                    SettlementController?.Poll(nowMs, deltaMs);
                    Driver.StepGame(Flow, DungeonController, SettlementController, deltaMs, nowMs);
                    if (Driver.Finished)
                    {
                        break;
                    }
                    Flow.Poll();
                }

                Thread.Sleep(5);
            }

            Assert.True(Driver.Finished,
                "E2E 未在 " + timeoutMs + "ms 内结束 (shell=" + Shell.Flow.Stage + ", game=" + (Flow?.Stage.ToString() ?? "null")
                + ", driverStage=" + Driver.Stage + ", logs=" + string.Join(" | ", SnapshotLogs()) + ")");
        }

        private void StartInGame()
        {
            _gameStarted = true;

            var baseWorld = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(TownId, AreaId), new TownPlace(TownId, AreaId));
            var returnWorld = new OnlineSettlementTownWorld(baseWorld);
            var entries = new[]
            {
                new DungeonEntry("dungeon/test/syn901.dgn", "SYN901", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
            };
            Flow = new GameFlow(returnWorld, (p, _) => throw new InvalidOperationException("offline factory forbidden"), entries);
            Flow.EnterTown("boot");

            TownController = OnlineTownController.Create(
                Shell,
                OnlineTownHostTests.BuildAtlas(),
                titles: null,
                areaSyncEnabled: true,
                log: AddLog);

            OnlineItemSyncData initialItems = TownController.BuildInitialItemSyncData();
            Progress = HeroProgress.CreateForOnlineHero(Fixture.Assets, TownController.Profile, initialItems);

            var permTable = new OnlineDungeonPermissionTable();
            OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
                permTable,
                hasLocalData: _ => true,
                hasUltimate: _ => true);

            var combatOpts = new OnlineDungeonCombatOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettlementTimeout = TimeSpan.FromMilliseconds(_settlementTimeoutMs),
                GiveUpTimeout = TimeSpan.FromSeconds(3),
            };

            DungeonController = OnlineDungeonFlowController.Create(
                Shell.Session,
                Shell.DungeonPermissions,
                Flow,
                policy,
                ctx => BuildSyntheticRoom(ctx),
                new OnlineDungeonFlowOptions
                {
                    CombatOptions = combatOpts,
                    ReturnDelayMs = 20,
                    TransientNoticeDurationMs = 200,
                    Log = AddLog,
                    OnDisconnected = () => Shell.Flow.NotifyDisconnected(),
                    CurrentArea = () => new TownAreaInfo(
                        TownId,
                        new TownArea(AreaId, "map/test/normal.map", TownAreaKind.Normal, Array.Empty<int>(), null, false),
                        "map/test/normal.map",
                        Array.Empty<WorldRect>(),
                        Array.Empty<TownExit>()),
                    HeroLevel = () => 15,
                    ReturnPlace = new TownPlace(TownId, AreaId),
                });

            SettlementController = new OnlineSettlementController(Flow, new OnlineSettlementOptions
            {
                GetProgress = () => Progress,
                GetSelfUniqueId = () => TownController.Profile?.ServerUniqueId,
                GetPartySlot = () => 0,
                GetCurrentTownPlace = () => Flow.Stage == GameStage.Town ? Flow.Place : null,
                ApplyReturnTarget = target => returnWorld.ArmReturn(target),
                Log = AddLog,
                OnDisconnected = () => Shell.Flow.NotifyDisconnected(),
                AdvanceReplyTimeoutMs = 1500,
                EplpReplyTimeoutMs = 1500,
                ReturnWaitTimeoutMs = 2500,
                TownReturnWaitTimeoutMs = 1500,
            });
            SettlementController.Attach(DungeonController);
        }

        private OnlineDungeonRoomBuildResult BuildSyntheticRoom(OnlineDungeonRoomBuildContext ctx)
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            int mobCount = Math.Max(1, ctx.StartMap.Monsters.Count);
            RoomMonsterSpawn[] Templates(int n) => Enumerable.Range(0, n)
                .Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320 + 35 * i))
                .ToArray();

            OnlineMultiRoomSetup multi = null;
            if (_multiRoom)
            {
                // 2x1 迷宫：(0,0) 右出口 -> (1,0) Boss 房（左出口）
                var maze = new DungeonMaze(
                    "dungeon/test/syn2.dgn",
                    cols: 2,
                    rows: 1,
                    exits: new[] { ExitBits.Right, ExitBits.Left },
                    second: null,
                    startCells: new[] { new CellRect(0, 0, 0, 0) },
                    bossCells: new[] { new CellRect(1, 0, 1, 0) },
                    gateSet: null);
                multi = new OnlineMultiRoomSetup(
                    new[] { maze },
                    (entry, plan) =>
                    {
                        var gates = new List<GateSide>();
                        if (entry.Entry.HasValue) gates.Add(entry.Entry.Value);
                        if (entry.Exit.HasValue) gates.Add(entry.Exit.Value);
                        return Fixture.Create(Templates(Math.Max(1, entry.Monsters)), plan, gates);
                    });
            }

            HostedRoomRun run = room.CreateRun(
                plan => Fixture.Create(Templates(mobCount), plan, Array.Empty<GateSide>()),
                returnDelayMs: 20,
                multi);
            return room.Status == OnlineRoomStatus.Ready && run != null
                ? OnlineDungeonRoomBuildResult.Success(room, run)
                : OnlineDungeonRoomBuildResult.Failure(room.Status, room);
        }

        public void Dispose()
        {
            SettlementController?.Dispose();
            DungeonController?.Dispose();
            TownController?.Dispose();
            Shell.Dispose();
            Fixture.Dispose();
            Stack.Dispose();
        }
    }

    [Fact]
    public void Happy_path_full_flow_with_server_ui_item_sync_return_retry_x2_and_exact_logs_and_commands()
    {
        var scenario = new FakeChannelScenario
        {
            Fault = FakeFaultMode.None,
            ReturnSignal = FakeReturnSignalMode.AreaPair,
            InitialGold = 12345,
        };
        // 按任务单顺序：结算 → 回城 → 再进图 → 再来一次 ×2 → 回城（共 4 局）
        var options = new OnlineAutoOptions(
            characterSlot: 0,
            dungeonId: DungeonAId,
            dungeonMode: 0,
            settlementChoices: new[]
            {
                SettlementChoice.ReturnToTown,
                SettlementChoice.Retry,
                SettlementChoice.Retry,
                SettlementChoice.ReturnToTown,
            });

        FakeReceivedCommand[] recordedCommands;
        string[] logs;
        int acceptedConnections;
        long syncedGold;
        int itemMalformed;

        using (var h = new E2eHarness(scenario, options, serverUi: true, multiRoom: false))
        {
            h.RunUntilFinished(timeoutMs: 25_000);

            Assert.True(h.Driver.Succeeded, "logs: " + string.Join(" | ", h.SnapshotLogs())
                + " cmds=" + string.Join(",", h.Stack.ReceivedCommands.Select(c => c.PacketClass + "/" + c.PacketId)));
            Assert.Equal(0, h.Driver.ExitCode);
            Assert.Equal("done", h.Driver.Stage);
            Assert.Equal(4, h.Driver.CompletedSettlements);
            Assert.Equal(2, h.Driver.RetryCycles);
            Assert.Equal(GameStage.Town, h.Flow.Stage);

            syncedGold = h.Progress.Gold;
            itemMalformed = h.Shell.ItemSync?.FramesMalformed ?? -1;
            logs = h.SnapshotLogs();
            recordedCommands = h.Stack.ReceivedCommands.ToArray();
            acceptedConnections = h.Stack.ChannelAcceptedConnections;

            // 先释放客户端连接，验证假服侧活跃连接归零（无泄漏）
            h.SettlementController.Dispose();
            h.DungeonController.Dispose();
            h.TownController.Dispose();
            h.Shell.Dispose();
            Assert.True(SpinWait.SpinUntil(() => h.Stack.ChannelActiveConnections == 0, 5000),
                "客户端释放后假频道服仍有活跃连接: " + h.Stack.ChannelActiveConnections);
        }

        // 1) 进城物品同步零畸形且金币已同步
        Assert.Equal(0, itemMalformed);
        Assert.Equal(12345L, syncedGold);
        Assert.Equal(1, acceptedConnections);

        // 2) 逐行断言完整 DNF_ONLINE 日志序列（忽略非确定性的 elapsedMs / localExp 等动态数值只验证结果行结构）
        string[] onlineLogs = logs.Where(l => l.StartsWith("DNF_ONLINE ", StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(onlineLogs, l => l.Contains("event=fail", StringComparison.Ordinal));

        string[] shellPrefix =
        {
            "DNF_ONLINE stage=login event=enter reason=start",
            "DNF_ONLINE stage=login event=ok reason=auth",
            "DNF_ONLINE stage=server event=enter reason=login_ok",
            "DNF_ONLINE stage=server event=ok reason=directory",
            "DNF_ONLINE stage=server event=ok reason=choose",
            "DNF_ONLINE stage=channel event=enter reason=choose",
            "DNF_ONLINE stage=channel event=ok reason=connected",
            "DNF_ONLINE stage=charselect event=enter reason=connected",
            "DNF_ONLINE stage=charselect event=ok reason=confirm",
            "DNF_ONLINE stage=loading event=enter reason=select",
            "DNF_ONLINE stage=loading event=ok reason=selected",
            "DNF_ONLINE stage=ingame event=enter reason=handover",
            "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=present snap=present slot=match level=match job=match grow=match",
        };
        for (int i = 0; i < shellPrefix.Length; i++)
        {
            Assert.Equal(shellPrefix[i], onlineLogs[i]);
        }

        // 每局选图/进图/离开/结算推进/选择/回城或重试完整出现
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=dungeon event=enter areakind=Normal entrance=false difficulty=0"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=dungeon event=room status=Ready multi=off spawned=2 expected=2 rooms=1"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=2 dropped=0 rooms=1 result=Succeeded reward=Succeeded"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=advance step=score_scroll status=sent"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=advance step=score_scroll status=ok"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=advance step=card_right status=sent"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=advance step=card_right status=ok"));
        Assert.Equal(4, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=advance step=select_card status=sent"));
        Assert.Equal(2, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=player"));
        Assert.Equal(2, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=choose choice=Retry source=player"));
        Assert.Contains("DNF_ONLINE stage=settle event=retry status=reenter cycle=1", onlineLogs);
        Assert.Contains("DNF_ONLINE stage=settle event=retry status=reenter cycle=2", onlineLogs);
        Assert.Equal(2, onlineLogs.Count(l => l == "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair"));

        // 结局行格式精确校验（唯一一次且在末尾）
        string lastLine = onlineLogs[onlineLogs.Length - 1];
        Assert.StartsWith("DNF_ONLINE result=ok stage=done elapsedMs=", lastLine);
        Assert.Equal(1, onlineLogs.Count(l => l.StartsWith("DNF_ONLINE result=", StringComparison.Ordinal)));

        // 3) 服务端命令序列逐条精确核对（过滤心跳 1/0 后每条命令恰一次、绝不重发）
        FakeReceivedCommand[] nonKeepAlive = recordedCommands
            .Where(c => !(c.PacketClass == PacketClass.Cmd && c.PacketId == 0))
            .ToArray();

        var expectedCmdIds = new List<ushort> { 1, 8, 4 };
        for (int run = 0; run < 4; run++)
        {
            expectedCmdIds.AddRange(new ushort[] { 15, 16, 42, 42, 49, 72, 73, 74, 75 });
        }

        Assert.Equal(expectedCmdIds.ToArray(), nonKeepAlive.Select(c => c.PacketId).ToArray());
        Assert.All(nonKeepAlive, c => Assert.Equal(PacketClass.Cmd, c.PacketClass));

        // 核对 4 次 1/75 的命令字节依次为 ReturnToTown(2), Retry(0), Retry(0), ReturnToTown(2)（会话块密码末尾补 0）
        FakeReceivedCommand[] eplps = nonKeepAlive.Where(c => c.PacketId == 75).ToArray();
        Assert.Equal(4, eplps.Length);
        Assert.Equal(new byte[] { 1, 2 }, eplps[0].Payload.Take(2).ToArray());
        Assert.Equal(new byte[] { 1, 0 }, eplps[1].Payload.Take(2).ToArray());
        Assert.Equal(new byte[] { 1, 0 }, eplps[2].Payload.Take(2).ToArray());
        Assert.Equal(new byte[] { 1, 2 }, eplps[3].Payload.Take(2).ToArray());
        Assert.All(eplps, c => Assert.All(c.Payload.Skip(2), b => Assert.Equal((byte)0, b)));

        // 核对 1/72、1/73 载荷长度 0，1/74 载荷前 2 字节为 (mode=0, card=0) 且其余补零
        Assert.All(nonKeepAlive.Where(c => c.PacketId == 72), c => Assert.Equal(0, c.PayloadLength));
        Assert.All(nonKeepAlive.Where(c => c.PacketId == 73), c => Assert.Equal(0, c.PayloadLength));
        Assert.All(nonKeepAlive.Where(c => c.PacketId == 74), c =>
        {
            Assert.True(c.PayloadLength >= 2);
            Assert.Equal(new byte[] { 0, 0 }, c.Payload.Take(2).ToArray());
            Assert.All(c.Payload.Skip(2), b => Assert.Equal((byte)0, b));
        });
    }

    [Fact]
    public void Multi_room_flow_moves_through_gate_with_1_48_1_40_and_returns_via_Noti2()
    {
        var scenario = new FakeChannelScenario
        {
            Fault = FakeFaultMode.None,
            ReturnSignal = FakeReturnSignalMode.Noti2,
        };
        scenario.Rooms.Clear();
        scenario.Rooms.Add(new FakeRoomSpec { CellX = 0, CellY = 0, MapId = 905, MonsterServerIds = new[] { 9100 } });
        scenario.Rooms.Add(new FakeRoomSpec { CellX = 1, CellY = 0, MapId = 906, MonsterServerIds = new[] { 9200 } });

        var options = new OnlineAutoOptions(
            characterSlot: 0,
            dungeonId: DungeonAId,
            dungeonMode: 0,
            settlementChoices: new[] { SettlementChoice.ReturnToTown });

        using var h = new E2eHarness(scenario, options, serverUi: false, multiRoom: true);
        h.RunUntilFinished(timeoutMs: 25_000);

        Assert.True(h.Driver.Succeeded);
        Assert.Equal(0, h.Driver.ExitCode);
        Assert.Equal(1, h.Driver.CompletedSettlements);

        ushort[] ids = h.Stack.ReceivedCommands
            .Where(c => !(c.PacketClass == PacketClass.Cmd && c.PacketId == 0))
            .Select(c => c.PacketId)
            .ToArray();
        Assert.Equal(new ushort[] { 1, 8, 4, 15, 16, 42, 48, 40, 42, 49, 72, 73, 74, 75 }, ids);

        string[] logs = h.SnapshotLogs();
        Assert.Contains("DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=2 dropped=0 rooms=2 result=Succeeded reward=Succeeded", logs);
        Assert.Contains("DNF_ONLINE stage=settle event=town reason=confirmed area=pre_dungeon signal=noti2", logs);
    }

    [Fact]
    public void Fault_AuthFail_stops_at_login_with_zero_channel_connections()
    {
        var scenario = new FakeChannelScenario { Fault = FakeFaultMode.AuthFail };
        using var h = new E2eHarness(scenario, new OnlineAutoOptions(), serverUi: true);
        h.RunUntilFinished(timeoutMs: 15_000);

        Assert.False(h.Driver.Succeeded);
        Assert.Equal(1, h.Driver.ExitCode);
        Assert.Equal("login", h.Driver.Stage);
        Assert.Equal(0, h.Stack.ChannelAcceptedConnections);
        Assert.Empty(h.Stack.ReceivedCommands);

        string[] logs = h.SnapshotLogs();
        Assert.Contains("DNF_ONLINE stage=login event=fail reason=wrong_credentials", logs);
        Assert.StartsWith("DNF_ONLINE result=fail stage=login elapsedMs=", h.Driver.ResultLine);
    }

    [Fact]
    public void Fault_SelectRefused_stops_at_charselect_with_cmd4_sent_once()
    {
        var scenario = new FakeChannelScenario { Fault = FakeFaultMode.SelectRefused };
        using var h = new E2eHarness(scenario, new OnlineAutoOptions(), serverUi: false);
        h.RunUntilFinished(timeoutMs: 15_000);

        Assert.False(h.Driver.Succeeded);
        Assert.Equal(1, h.Driver.ExitCode);
        Assert.Equal("charselect", h.Driver.Stage);

        ushort[] ids = h.Stack.ReceivedCommands
            .Where(c => !(c.PacketClass == PacketClass.Cmd && c.PacketId == 0))
            .Select(c => c.PacketId)
            .ToArray();
        Assert.Equal(new ushort[] { 1, 8, 4 }, ids);

        string[] logs = h.SnapshotLogs();
        Assert.Contains("DNF_ONLINE stage=loading event=fail reason=select_failed", logs);
        Assert.StartsWith("DNF_ONLINE result=fail stage=charselect elapsedMs=", h.Driver.ResultLine);
    }

    [Fact]
    public void Fault_MidDisconnect_stops_at_disconnect_without_reconnecting()
    {
        var scenario = new FakeChannelScenario { Fault = FakeFaultMode.MidDisconnect };
        using var h = new E2eHarness(scenario, new OnlineAutoOptions(dungeonId: DungeonAId), serverUi: false);
        h.RunUntilFinished(timeoutMs: 15_000);

        Assert.False(h.Driver.Succeeded);
        Assert.Equal(1, h.Driver.ExitCode);
        Assert.Equal("disconnect", h.Driver.Stage);
        Assert.Equal(1, h.Stack.ChannelAcceptedConnections); // 断线后绝不自动重连
        Assert.StartsWith("DNF_ONLINE result=fail stage=disconnect elapsedMs=", h.Driver.ResultLine);
    }

    [Fact]
    public void Fault_SettleIncomplete_stops_at_settle_on_missing_0x23_timeout()
    {
        var scenario = new FakeChannelScenario { Fault = FakeFaultMode.SettleIncomplete };
        using var h = new E2eHarness(scenario, new OnlineAutoOptions(dungeonId: DungeonAId), serverUi: false, settlementTimeoutMs: 400);
        h.RunUntilFinished(timeoutMs: 15_000);

        Assert.False(h.Driver.Succeeded);
        Assert.Equal(1, h.Driver.ExitCode);
        Assert.Equal("settle", h.Driver.Stage);

        string[] logs = h.SnapshotLogs();
        Assert.Contains("DNF_ONLINE stage=dungeon event=leave reason=SettlementTimeout reported=2 dropped=0 rooms=1 result=Succeeded reward=NotApplicable", logs);
        Assert.StartsWith("DNF_ONLINE result=fail stage=settle elapsedMs=", h.Driver.ResultLine);
    }

    [Fact]
    public void Fault_MalformedReply_on_1_72_stops_at_settle_without_resending_72()
    {
        var scenario = new FakeChannelScenario { Fault = FakeFaultMode.MalformedReply };
        using var h = new E2eHarness(scenario, new OnlineAutoOptions(dungeonId: DungeonAId), serverUi: false);
        h.RunUntilFinished(timeoutMs: 15_000);

        Assert.False(h.Driver.Succeeded);
        Assert.Equal(1, h.Driver.ExitCode);
        Assert.Equal("settle", h.Driver.Stage);

        int count72 = h.Stack.ReceivedCommands.Count(c => c.PacketClass == PacketClass.Cmd && c.PacketId == 72);
        Assert.Equal(1, count72); // 1/72 绝不自动重发

        string[] logs = h.SnapshotLogs();
        Assert.Contains("DNF_ONLINE stage=settle event=fail reason=advance_72_malformed step=72", logs);
        Assert.StartsWith("DNF_ONLINE result=fail stage=settle elapsedMs=", h.Driver.ResultLine);
    }
}
