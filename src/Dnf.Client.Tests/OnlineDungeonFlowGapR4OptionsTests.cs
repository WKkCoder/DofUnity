using System.Collections.Concurrent;
using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器"接线"一类的行为:流程选项里的战斗选项原样到达战斗跑器(轮询间隔、起始房间装载握手、握手超时、换房拒绝额度、事件回调)、
/// 选择屏被配置时拿到的名字与难度夹紧、控制器在选择屏已打开时才创建、选角界面上的断线、加载画面下不叠进入提示、
/// 副本编号 0 合法、释放时撤掉房间的收尾闸门、公开阶段枚举的数值、退出时对没跑完的任务做"观察"以免异常无主。
/// 全部用合成数据;没有真资源与进程级共享状态。
/// </summary>
public sealed class OnlineDungeonFlowGapR4OptionsTests
{
    private static OnlineDungeonCombatOptions Combat(Action<OnlineDungeonCombatOptions> tweak)
    {
        var options = new OnlineDungeonCombatOptions { PollInterval = TimeSpan.FromMilliseconds(10) };
        tweak(options);
        return options;
    }

    [Fact]
    public void The_combat_options_of_the_flow_reach_the_combat_runner_with_every_configured_value()
    {
        // 保护:流程选项里的战斗选项交给战斗跑器时,轮询间隔(37 毫秒)、"起始房间也报装载完成"、握手超时(300 毫秒)、事件回调一个都不丢:
        // 战斗一开始就发装载完成命令(1/40,8 字节);假服务端不认这条命令、不回通知,握手 300 毫秒后超时,战斗以 Timeout 收场(而不是默认的 10 秒,更不是没有握手一直挂着)。
        var kinds = new ConcurrentQueue<OnlineCombatEventKind>();
        using var rig = new Rig4(o => o.CombatOptions = Combat(c =>
        {
            c.PollInterval = TimeSpan.FromMilliseconds(37);
            c.AcknowledgeStartRoomLoaded = true;
            c.RoomHandshakeTimeout = TimeSpan.FromMilliseconds(300);
            c.OnEvent = e => kinds.Enqueue(e.Kind);
        }));
        ServeRoomDungeon(rig, 2);
        EnterDungeon(rig);

        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 800);

        (PacketClass Class, int PacketId, byte[] Payload)[] loaded = rig.Port.Commands
            .Where(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonCombat.RoomLoadedPacketId)
            .ToArray();
        Assert.Single(loaded);
        Assert.Equal(8, loaded[0].Payload.Length);
        Assert.Contains(TimeSpan.FromMilliseconds(37), rig.Port.ReceiveWindows);
        OnlineCombatEventKind[] seen = kinds.ToArray();
        Assert.Equal(OnlineCombatEventKind.MonstersSpawned, seen[0]);
        Assert.Contains(OnlineCombatEventKind.RoomLoadedSent, seen);
        Assert.DoesNotContain(OnlineCombatEventKind.RoomLoaded, seen);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Timeout reported=0 dropped=0 rooms=1",
            P + "event=fail reason=Timeout step=Combat",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.Ctrl.ActiveNotice?.Kind);
        Assert.Equal(1, rig.Flow.Failures);
    }

    [Fact]
    public void The_move_refusal_budget_of_the_flow_options_limits_the_combat_runners_retries()
    {
        // 保护:流程选项里的"每间房最多被拒几次换房"(这里 1)到达战斗跑器:第一次换房被服务端拒绝(目标格与当前格不相邻)后额度用完,
        // 第二次换房请求被本地拦下(记一条"换房被拦、额度用完"事件)、不发给服务端;默认额度(3)的话第二次会照发。
        var events = new ConcurrentQueue<(OnlineCombatEventKind Kind, OnlineCombatBlock Block)>();
        var server = new FakeMultiRoomServer(
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(1)),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId)));
        using var rig = new Rig4(o => o.CombatOptions = Combat(c =>
        {
            c.MaxMoveRefusalsPerRoom = 1;
            c.OnEvent = e => events.Enqueue((e.Kind, e.Block));
        }));
        ServeRoomDungeon(rig, 1);
        rig.Port.SetResponder((cls, id, payload) =>
        {
            CombatKit.ServerFrame[] entry = EntryReply(cls, id, DungeonAId, 1);
            return entry != null ? entry : server.Handle(cls, id, payload);
        });
        EnterDungeon(rig);
        OnlineDungeonCombatRunner runner = rig.LastContext.CombatRunner;

        Assert.True(runner.PostKill(0, 0));
        rig.PumpControllerUntil(() => runner.Machine.RoomCleared, 600);
        Assert.True(runner.PostMoveRoom(0, new OnlineMoveRoomRequest(3, 2)));
        rig.PumpControllerUntil(() => events.Any(e => e.Kind == OnlineCombatEventKind.RoomMoveRefused), 600);
        Assert.Single(server.Moves);

        Assert.True(runner.PostMoveRoom(0, new OnlineMoveRoomRequest(3, 2)));
        rig.PumpControllerUntil(() => events.Any(e => e.Kind == OnlineCombatEventKind.RoomMoveBlocked)
            || events.Count(e => e.Kind == OnlineCombatEventKind.RoomMoveSent) >= 2, 600);

        Assert.Contains((OnlineCombatEventKind.RoomMoveBlocked, OnlineCombatBlock.MoveBudgetExhausted), events);
        Assert.Equal(1, events.Count(e => e.Kind == OnlineCombatEventKind.RoomMoveSent));
        Assert.Single(server.Moves);
        Assert.Equal(1, server.Journal.Count(j => j.StartsWith("move:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Configuring_a_select_screen_gives_it_the_display_name_hook_and_reclamps_its_difficulty()
    {
        // 保护:流程让控制器配置一块新的选择屏时,屏上拿到副本显示名的解析钩子,并按屏自己的上限重新夹紧难度:
        // 屏构造时上限 3、难度 3;上限降到 1 之后被配置,难度跟着降到 1(不配置就还是 3)。
        using var rig = new Rig4();
        int cap = 3;
        var screen = new DungeonSelectScreen(rig.Entries, 0, null, _ => cap, 3);
        Assert.Equal(3, screen.Difficulty);
        Assert.Null(screen.EntryDisplayName);
        Assert.Null(screen.EntryStatus);

        cap = 1;
        rig.Flow.ConfigureSelect(screen);

        Assert.NotNull(screen.EntryDisplayName);
        Assert.NotNull(screen.EntryStatus);
        Assert.Equal(1, screen.Difficulty);
        Assert.Equal(1, screen.MaxDifficulty);
    }

    [Fact]
    public void A_select_screen_configured_by_the_flow_shows_the_region_name_the_policy_resolved_from_the_world_map_script()
    {
        // 保护:选择屏被配置时拿到策略解出的地区名(取自世界地图脚本的 [name] 与文字表),而不是留空。
        // 合成资源包里只有一份 worldmap/test.wld(一行 [name] map_200),文字表里 map_200 对应 GrandFlorest。
        using var region = new RegionAssets("worldmap/test.wld", "map_200");
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["worldmap/worldmap.kor.str"] = Encoding.ASCII.GetBytes("map_200>GrandFlorest\r\n"),
        };
        var texts = new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));
        using var rig = new Rig4(makePolicy: table => OnlineDungeonSelectPolicy.Create(
            table, region.Assets, hasLocalData: _ => true, texts: texts, worldMapPath: "worldmap/test.wld", hasUltimate: _ => true));
        Assert.Equal("GrandFlorest", rig.Policy.RegionDisplayName);

        var screen = new DungeonSelectScreen(rig.Entries, 0, null, rig.Policy.DifficultyLimit, 0);
        Assert.Null(screen.RegionDisplayName);
        rig.Flow.ConfigureSelect(screen);

        Assert.Equal("GrandFlorest", screen.RegionDisplayName);
    }

    [Fact]
    public void A_controller_created_while_the_select_screen_is_already_open_configures_that_screen_at_once()
    {
        // 保护:控制器是在选择屏已经打开之后才创建的(宿主晚接线)时,马上给这块已开的屏接上状态查询与显示名钩子;
        // 不然要等下一次开屏才生效,在那之前被服务端拒绝的副本不会灰显。
        using var rig = new Rig4(beforeController: f =>
        {
            f.EnterTown("boot");
            f.EnterSelect("gate");
        });
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        DungeonSelectScreen screen = rig.Flow.Select;
        Assert.NotNull(screen.EntryStatus);
        Assert.NotNull(screen.EntryDisplayName);

        rig.Table.MarkServerRejected(DungeonBId, 5);
        Assert.False(screen.StatusOf(rig.Entries[0]).Disabled);
        DungeonEntryStatus rejected = screen.StatusOf(rig.Entries[1]);
        Assert.True(rejected.Disabled);
        Assert.Equal(DungeonEntryBlockKind.ServerRejected, rejected.Kind);
    }

    [Fact]
    public void A_dungeon_load_screen_replaces_the_entering_notice_instead_of_stacking_on_it()
    {
        // 保护:进副本时流程有整屏加载画面(Loading 阶段)的话,控制器不再叠"正在进入"提示——加载画面自己就是过渡;
        // 没有加载画面时那条提示是在的(同一个局面,对照在 CombatEnd 测试里)。
        using var rig = new Rig4(dungeonSplash: path => new SplashScene(path, null, null, null, 0, 0, 1000));
        ServeRoomDungeon(rig);
        rig.Start();
        rig.ConfirmSelected();

        Assert.Equal(GameStage.Loading, rig.Flow.Stage);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void A_disconnect_seen_while_idle_is_reported_once_and_only_after_the_flow_leaves_the_character_select()
    {
        // 保护:空闲时收到断线事件:还在选角界面(宿主的选角流程自己处理断线)时控制器不报,
        // 流程一离开选角界面(进了城)的下一次轮询就通知宿主一次断线清理,之后再轮询不重复通知;事件处理器本身写一行失败日志(原因 Disconnected、步骤是断线原因)。
        using var rig = new Rig4(
            characterSelect: () => new CharacterSelectScreen(CharacterRoster.Local()),
            beforeController: f => f.EnterCharacterSelect("boot"));
        Assert.Equal(GameStage.CharacterSelect, rig.Flow.Stage);

        rig.Port.RaiseDisconnectedEventOnly(OnlineDisconnectReason.PeerClosed);
        for (int i = 0; i < 3; i++)
        {
            rig.NowMs += 20;
            rig.Ctrl.Poll(rig.NowMs);
        }
        Assert.Equal(0, rig.Disconnects);
        Assert.Equal(new[] { P + "event=fail reason=Disconnected step=PeerClosed" }, rig.Logs());

        rig.Flow.EnterTown("login");
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        rig.NowMs += 20;
        rig.Ctrl.Poll(rig.NowMs);
        Assert.Equal(1, rig.Disconnects);
        for (int i = 0; i < 3; i++)
        {
            rig.NowMs += 20;
            rig.Ctrl.Poll(rig.NowMs);
        }
        Assert.Equal(1, rig.Disconnects);
    }

    [Fact]
    public void Dungeon_id_zero_is_a_valid_id_and_is_sent_unchanged()
    {
        // 保护:副本编号 0 合法(和 65535 一样在范围内):不被当成"缺编号"拦下,照常进入进副本阶段,选副本命令里的编号是 00 00。
        var entries = new[] { new DungeonEntry("dungeon/test/dungzero.dgn", "DUNGZERO", new DungeonCard(0, 100, 100, 0, null, 0, 1)) };
        using var rig = new Rig(o =>
        {
            o.LoadingNoticeTimeout = TimeSpan.FromSeconds(30);
            o.DungeonInfoTimeout = TimeSpan.FromSeconds(30);
            o.StartMapTimeout = TimeSpan.FromSeconds(30);
        }, entries);
        rig.Port.SetResponder((cls, id, _) => cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId ? LoadingFrames() : Array.Empty<CombatKit.ServerFrame>());
        rig.Start();

        rig.Flow.OnlineStartDungeon(entries[0], 0);

        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.DoesNotContain(rig.Logs(), l => l.Contains("MissingDungeonId"));
        rig.WaitForCommand(OnlineDungeonEntry.SelectDungeonPacketId);
        (PacketClass Class, int PacketId, byte[] Payload) select = rig.Port.Commands.First(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.SelectDungeonPacketId);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x02, 0x00 }, select.Payload);
    }

    [Fact]
    public void Disposing_the_controller_withdraws_the_rooms_finish_gate_and_returns_to_idle()
    {
        // 保护:控制器被释放时撤掉房间的"可以收尾"闸门(房间不再受一个已经没有主人的控制器管),流程的"挂起退出"拦截也放下,阶段回空闲。
        using var rig = new Rig4();
        ServeRoomDungeon(rig);
        EnterDungeon(rig);
        OnlineDungeonRoom room = rig.LastRoom;
        Assert.NotNull(room.CanFinish);

        rig.Ctrl.Dispose();

        Assert.Null(room.CanFinish);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void The_public_phase_values_keep_their_documented_numbers()
    {
        // 保护:公开阶段枚举的数值是显式写定的对外约定(宿主与调试面板可能按数值记录阶段),不许悄悄改号或挪位。
        Assert.Equal(0, (int)OnlineDungeonFlowPhase.Idle);
        Assert.Equal(1, (int)OnlineDungeonFlowPhase.Entering);
        Assert.Equal(2, (int)OnlineDungeonFlowPhase.InDungeon);
        Assert.Equal(3, (int)OnlineDungeonFlowPhase.GivingUp);
        Assert.Equal(4, (int)OnlineDungeonFlowPhase.StoppingCombatBeforeRelease);
        Assert.Equal(5, (int)OnlineDungeonFlowPhase.ReleasingExclusive);
        Assert.Equal(6, (int)OnlineDungeonFlowPhase.HoldingForSettlement);
        Assert.Equal(7, (int)OnlineDungeonFlowPhase.SettlingExit);
        Assert.Equal(8, Enum.GetValues(typeof(OnlineDungeonFlowPhase)).Length);
    }

    [Fact]
    public void Quitting_while_the_entry_is_still_pending_observes_its_later_failure_so_it_never_surfaces_as_an_unobserved_task_exception()
    {
        // 保护:退出(释放)时进副本任务还没跑完,控制器要给它挂上"观察异常"的收尾:之后它失败了,异常不会作为无主任务异常漏出来
        // (被垃圾回收时触发 UnobservedTaskException)。用唯一的异常文字认领;再造一个没人观察的对照任务,证明本进程里这套检测手段确实灵(对照必须被报到)。
        const string marker = "r4-unobserved-entry-marker";
        const string control = "r4-unobserved-control-marker";
        var unobserved = new ConcurrentBag<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            foreach (Exception inner in e.Exception.Flatten().InnerExceptions)
            {
                unobserved.Add(inner.Message);
            }
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            QuitMidEntryThenFail(marker);
            CreateUnobservedFaultedTask(control);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Contains(control, unobserved);        // 对照:没人观察的失败任务确实会被报到
        Assert.DoesNotContain(marker, unobserved);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void QuitMidEntryThenFail(string message)
    {
        var enterGate = new TaskCompletionSource<bool>();
        using var rig = new Rig4(o => o.QuitWaitTimeoutMs = 0);
        ServeRoomDungeon(rig);
        rig.Port.EnterExclusiveBehavior = _ => enterGate.Task;        // 进入独占收包一直不回,进副本任务就卡在这里
        rig.Start();
        rig.ConfirmSelected();
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);

        rig.Ctrl.PrepareForQuit();
        rig.Ctrl.Dispose();
        enterGate.SetException(new InvalidOperationException(message));      // 退出之后它才失败
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CreateUnobservedFaultedTask(string message)
    {
        _ = Task.FromException(new InvalidOperationException(message));
    }

    /// <summary>只有一份世界地图脚本(<c>[name] key</c>)的合成资源包。</summary>
    private sealed class RegionAssets : IDisposable
    {
        public string Root { get; }

        public DnfAssets Assets { get; }

        public RegionAssets(string scriptPath, string nameKey)
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-r4-region-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            // 字符串表:0 = "[name]"(标签),1 = 名字键
            byte[] script = TestData.BuildTokenScript(new (byte Type, int Value)[]
            {
                ((byte)TokenType.Tag, 0),
                ((byte)TokenType.String, 1),
            });
            var strings = new List<byte[]> { Encoding.ASCII.GetBytes("[name]"), Encoding.ASCII.GetBytes(nameKey) };
            var files = new List<TestData.PvfFileSpec>
            {
                new TestData.PvfFileSpec("stringtable.bin", TestData.BuildStringTable(strings), 0),
                new TestData.PvfFileSpec(scriptPath, script, 0),
            };
            (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
            File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);
            Assets = DnfAssets.Open(Root);
        }

        public void Dispose()
        {
            Assets.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}
