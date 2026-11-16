using System.Text.RegularExpressions;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网多房间副本的宿主接线(r18-hostmulti)的内存端到端:联网战斗跑器(<see cref="OnlineDungeonCombatRunner"/>,后台线程)接会话假实现(<see cref="CombatFakeSession"/>),
/// 对面是照静态读码建模的多房间"服务端"替身(<see cref="FakeMultiRoomServer"/>);宿主这一侧是 <see cref="OnlineDungeonRoom"/> + <see cref="HostedRoomRun"/> + 自动机
/// (<see cref="HostedRoomBot"/>),房间是带出口的合成房间(<see cref="HostedGatedRoomFixture"/>),本地迷宫是合成的小迷宫。
/// 覆盖:整座多房间副本逐房间打通(杀光、走到门口、换房请求、新房间换表与装载握手、继续、最后清图结算)、对不上就退回单房间、每一种失败出口
/// (被拒到额度、等不到新房间、整段断线、新房间对不上、造不出房间)的结局与日志、绝不发不安全的换房。
/// 服务端替身是测试用的建模,不是真服的行为;数据全是合成的(怪编号 9100/9300/9500 起、地图编号 905 起),日志里不会出现这些数。
/// </summary>
public class OnlineDungeonRoomMultiTests
{
    private const int R = ExitBits.Right;
    private const int U = ExitBits.Up;
    private const int L = ExitBits.Left;
    private const int D = ExitBits.Down;
    private const string Host = "DNF_NET stage=room ";

    private static List<CellRect> Cells(params (int X, int Y)[] cells)
    {
        return cells.Select(c => new CellRect(c.X, c.Y, c.X, c.Y)).ToList();
    }

    private static DungeonMaze Maze(int cols, int rows, int[] exits, (int X, int Y)[] start, (int X, int Y)[] boss)
    {
        return new DungeonMaze("dungeon/test/m.dgn", cols, rows, exits, null, Cells(start), Cells(boss), null, null, 1f, -1, null, null);
    }

    /// <summary>二乘二的 L 形:(0,0) → (1,0) → (1,1),起点 (0,0)、首领 (1,1);(0,1) 是空格。</summary>
    private static DungeonMaze LRight() => Maze(2, 2, new[] { R, L | D, 0, U }, new[] { (0, 0) }, new[] { (1, 1) });

    /// <summary>另一种 L 形:(0,0) → (0,1) → (1,1);起点、首领同上。和 <see cref="LRight"/> 一起是"两个候选迷宫对第一步的意见不一"。</summary>
    private static DungeonMaze LDown() => Maze(2, 2, new[] { D, 0, U | R, L }, new[] { (0, 0) }, new[] { (1, 1) });

    private static IReadOnlyList<DungeonMaze> One(DungeonMaze maze) => new[] { maze };

    /// <summary>服务端的三间房,格坐标 (0,0)、(1,0)、(1,1),怪数 a、b、c;<paramref name="hiddenInFirst"/> 是第一间里另有一只只有服务端知道的怪。</summary>
    private static FakeMultiRoomServer.RoomPlan[] Plans(int a, int b, int c, int[] hiddenInFirst = null)
    {
        return new[]
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(a), hiddenInFirst),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(b, RoomKit.SecondBaseId)),
            new FakeMultiRoomServer.RoomPlan(1, 1, CombatKit.Sequential(c, RoomKit.ThirdBaseId)),
        };
    }

    private static RoomMonsterSpawn[] Templates(int count, int code = HostedRoomFixture.Buildable)
    {
        return Enumerable.Range(0, count).Select(i => HostedRoomFixture.Spawn(code, 340 + 40 * i)).ToArray();
    }

    /// <summary>把一串日志里连续的 kill: 行按编号排好(一间房里的几只怪谁先死不由测试决定),其余行原样。</summary>
    private static string[] Normalize(string[] journal)
    {
        var result = new List<string>();
        var kills = new List<string>();
        foreach (string line in journal)
        {
            if (line.StartsWith("kill:", StringComparison.Ordinal))
            {
                kills.Add(line);
                continue;
            }
            result.AddRange(kills.OrderBy(k => k, StringComparer.Ordinal));
            kills.Clear();
            result.Add(line);
        }
        result.AddRange(kills.OrderBy(k => k, StringComparer.Ordinal));
        return result.ToArray();
    }

    private sealed class LogSink
    {
        private readonly object _gate = new object();
        private readonly List<string> _lines = new List<string>();

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        public string[] Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToArray();
                }
            }
        }
    }

    /// <summary>造房间的回调带上整套设备:第一间房是在 <see cref="Rig.Start"/> 里就要造的,那时测试里还拿不到设备本身。</summary>
    private delegate RoomCombatSession RigFactory(Rig rig, OnlineRoomEntry entry, Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> plan);

    private sealed class Opts
    {
        public Action<OnlineDungeonCombatOptions> Combat { get; set; }
        public Action<OnlineMultiRoomSetup> Setup { get; set; }
        public Action<FakeMultiRoomServer> Server { get; set; }
        public RigFactory Factory { get; set; }
        public bool Multi { get; set; } = true;
        public bool Create { get; set; } = true;
        public bool DropKillReplies { get; set; }
    }

    /// <summary>
    /// 一整套:多房间假服务端 + 假会话 + 战斗跑器(后台线程里跑)+ 联网房间接线 + 合成房间夹具 + 自动机。
    /// <see cref="Start"/> 返回时战斗状态机已建好;<see cref="Opts.Create"/> 为真时这一局(<see cref="Run"/>)也造好了。
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private Task<OnlineDungeonCombatResult> _background;
        private bool _dropKillReplies;

        public HostedGatedRoomFixture Fx { get; } = new HostedGatedRoomFixture();
        public FakeMultiRoomServer Server { get; private set; }
        public CombatFakeSession Session { get; private set; }
        public OnlineDungeonCombatRunner Runner { get; private set; }
        public OnlineDungeonRoom Room { get; private set; }
        public OnlineMultiRoomSetup Setup { get; private set; }
        public LogSink Net { get; } = new LogSink();
        public LogSink HostLog { get; } = new LogSink();
        public List<OnlineRoomEntry> Entries { get; } = new List<OnlineRoomEntry>();
        public int LegacyBuilds { get; private set; }
        public HostedRoomRun Run { get; private set; }
        public HostedRoomBot Bot { get; } = new HostedRoomBot();

        public OnlineDungeonCombat Machine => Runner.Machine;

        public static async Task<Rig> Start(FakeMultiRoomServer.RoomPlan[] plans, IReadOnlyList<DungeonMaze> mazes, Opts o = null)
        {
            o = o ?? new Opts();
            var rig = new Rig { _dropKillReplies = o.DropKillReplies };
            rig.Server = new FakeMultiRoomServer(plans);
            o.Server?.Invoke(rig.Server);
            rig.Session = new CombatFakeSession { Respond = rig.Answer };
            OnlineDungeonCombatOptions combat = CombatKit.Options(overallSeconds: 60);
            o.Combat?.Invoke(combat);
            rig.Runner = new OnlineDungeonCombatRunner(rig.Session, null, combat);
            StartMapNotice map = RoomKit.Map(plans[0].Ids, plans[0].CellX, plans[0].CellY, 905);
            rig._background = Task.Run(() => rig.Runner.RunAsync(map, rig.Net.Add, rig._cancel.Token));
            await CombatKit.WaitUntil(() => rig.Runner.Machine != null, "战斗状态机建好");
            rig.Room = new OnlineDungeonRoom(rig.Runner);
            OnlineRoomEntryFactory factory = rig.DefaultFactory;
            if (o.Factory != null)
            {
                RigFactory custom = o.Factory;
                factory = (entry, plan) => custom(rig, entry, plan);
            }
            rig.Setup = new OnlineMultiRoomSetup(mazes, factory) { Log = rig.HostLog.Add };
            o.Setup?.Invoke(rig.Setup);
            if (o.Create)
            {
                rig.Run = rig.Room.CreateRun(rig.LegacyFactory, 0.0, o.Multi ? rig.Setup : null);
                Assert.NotNull(rig.Run);
            }
            return rig;
        }

        private IEnumerable<CombatKit.ServerFrame> Answer(PacketClass cls, ushort id, byte[] payload)
        {
            List<CombatKit.ServerFrame> replies = Server.Handle(cls, id, payload);
            if (_dropKillReplies && cls == PacketClass.Cmd && id == 42)
            {
                return new CombatKit.ServerFrame[0];
            }
            return replies;
        }

        /// <summary>多房间的缺省工厂:按怪表只数造房间,摆上进来的那一侧和通向下一间的那一侧出口。</summary>
        public RoomCombatSession DefaultFactory(OnlineRoomEntry entry, Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> plan)
        {
            Entries.Add(entry);
            return Fx.Create(Templates(6), plan, GatesOf(entry));
        }

        public static List<GateSide> GatesOf(OnlineRoomEntry entry)
        {
            var gates = new List<GateSide>();
            if (entry.Entry.HasValue)
            {
                gates.Add(entry.Entry.Value);
            }
            if (entry.Exit.HasValue)
            {
                gates.Add(entry.Exit.Value);
            }
            return gates;
        }

        /// <summary>单房间老路的工厂(对不上就退回它):一间有右出口的房间。</summary>
        public RoomCombatSession LegacyFactory(Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> plan)
        {
            LegacyBuilds++;
            return Fx.Create(Templates(6), plan, new[] { GateSide.Right });
        }

        /// <summary>自动机推进宿主(每次 100 毫秒、六个心跳)直到条件成立;超时让测试失败,信息里带上自动机、宿主日志与服务端流水。</summary>
        public async Task Drive(Func<bool> until, string what, int milliseconds = 20000)
        {
            try
            {
                await CombatKit.WaitUntil(() =>
                {
                    Bot.Advance(Run, 100.0);
                    return until();
                }, what, milliseconds);
            }
            catch (Xunit.Sdk.XunitException e)
            {
                throw new Xunit.Sdk.XunitException(e.Message + " [bot=" + Bot.LogLine() + " host=" + string.Join(" | ", HostLog.Lines) + " server=" + string.Join(",", Server.Journal) + "]");
            }
        }

        /// <summary>不用自动机:主角站着不动,只让时间走(宿主的出口检查、Pump 照常每步跑)。</summary>
        public async Task Wait(Func<bool> until, string what, int milliseconds = 20000)
        {
            await CombatKit.WaitUntil(() =>
            {
                Run.Update(10, 0, 0, false, false);
                return until();
            }, what, milliseconds);
        }

        /// <summary>打死当前房间里所有的怪(不推进时间)。</summary>
        public void Slay()
        {
            foreach (MonsterActor monster in Run.Room.Monsters)
            {
                if (monster.IsAlive)
                {
                    HostedRoomFixture.Kill(monster);
                }
            }
        }

        public void Stop()
        {
            _cancel.Cancel();
            try
            {
                _background?.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }
        }

        public void Dispose()
        {
            Stop();
            Fx.Dispose();
            _cancel.Dispose();
        }
    }

    // ================================================================== 整座副本逐房间打通

    [Fact]
    public async Task A_three_room_dungeon_is_cleared_room_by_room_through_the_doors_and_settled()
    {
        using Rig rig = await Rig.Start(Plans(2, 3, 1), One(LRight()));
        Assert.True(rig.Room.MultiRoom);
        Assert.Equal(HostedMazeStatus.Aligned, rig.Room.Alignment);
        Assert.Equal(OnlineRoomStatus.Ready, rig.Room.Status);
        Assert.True(rig.Run.MultiRoom);
        Assert.Equal(GateSide.Right, rig.Run.ExitSide);

        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "三间房打穿、结算收齐、回城");

        // 服务端看到的:每间房的击杀各用那一间的编号,换房成功之后才有装载完成,清图在最后一间,结算只一条
        Assert.Equal(
            new[]
            {
                "kill:9100", "kill:9101", "roomclear:r0", "move:r0:ok:r1", "loaded:r1",
                "kill:9300", "kill:9301", "kill:9302", "roomclear:r1", "move:r1:ok:r2", "loaded:r2",
                "kill:9500", "clear", "settle",
            },
            Normalize(rig.Server.Journal));
        // 客户端发出的命令:每间房的击杀、换房、装载完成(跑器自己发,新房间第一条击杀之前),最后结算
        Assert.Equal(new[] { 42, 42, 48, 40, 42, 42, 42, 48, 40, 42, 49 }, rig.Session.Sent.Select(s => (int)s.Id).ToArray());
        Assert.Equal(0, rig.Server.Refusals);
        Assert.Equal(0, rig.Server.PrematureSettlements);
        Assert.Equal(1, rig.Server.Settlements);
        Assert.False(rig.Server.Disconnected);
        Assert.Equal(2, rig.Server.Loads.Length);
        Assert.Equal(0, rig.Server.Hack(FakeMultiRoomServer.HackTrapBits) + rig.Server.Hack(FakeMultiRoomServer.HackSeatZero));

        // 宿主这边的整局数
        Assert.Equal(6, rig.Run.Reported);                                        // 整局累计
        Assert.Equal(0, rig.Run.Dropped);
        Assert.Equal(1, rig.Run.RoomReported);                                    // 最后一间的
        Assert.Equal((3, 2, 2), (rig.Run.RoomsEntered, rig.Run.RoomIndex, rig.Run.MoveRequests));
        Assert.Equal(0, rig.Run.MoveRefusals);
        Assert.False(rig.Run.Failed);
        Assert.False(rig.Run.Aborted);
        Assert.Equal(OnlineCombatPhase.Settled, rig.Machine.Phase);
        Assert.Equal(HostedBotStop.Settled, rig.Bot.Reason);
    }

    [Fact]
    public async Task The_move_requests_name_the_planned_neighbour_cell_with_the_hero_inside_the_door_rectangle()
    {
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()));

        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "三间房打穿");

        MoveRoomView[] moves = rig.Server.Moves;
        Assert.Equal(2, moves.Length);
        Assert.Equal((1, 0), ((int)moves[0].TargetX, (int)moves[0].TargetY));     // 第一间向右
        Assert.Equal((1, 1), ((int)moves[1].TargetX, (int)moves[1].TargetY));     // 第二间向下
        WorldRect right = HostedGatedRoomFixture.GateRect(GateSide.Right);
        WorldRect down = HostedGatedRoomFixture.GateRect(GateSide.Down);
        Assert.True(GateAreas.Contains(right, moves[0].PositionX, moves[0].PositionY), "第一次换房报的位置在右出口的识别矩形里");
        Assert.True(GateAreas.Contains(down, moves[1].PositionX, moves[1].PositionY), "第二次换房报的位置在下出口的识别矩形里");
        Assert.All(moves, m => Assert.Equal(0, m.Layered));
    }

    [Fact]
    public async Task Each_room_is_built_from_the_notice_of_that_room_with_its_own_cell_exits_and_monster_count()
    {
        using Rig rig = await Rig.Start(Plans(2, 3, 1), One(LRight()));

        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "三间房打穿");

        Assert.Equal(3, rig.Entries.Count);
        OnlineRoomEntry first = rig.Entries[0], second = rig.Entries[1], third = rig.Entries[2];
        Assert.Equal((0, 0, 0, 905), (first.Room, first.CellX, first.CellY, first.MapId));
        Assert.Equal((1, 1, 0, 906), (second.Room, second.CellX, second.CellY, second.MapId));
        Assert.Equal((2, 1, 1, 907), (third.Room, third.CellX, third.CellY, third.MapId));
        Assert.Equal((null, GateSide.Right), (first.Entry, first.Exit));
        Assert.Equal((GateSide.Left, GateSide.Down), (second.Entry, second.Exit));    // 从左进、向下出(进来的一侧是上一间出口的反面)
        Assert.Equal((GateSide.Up, null), (third.Entry, third.Exit));
        Assert.Equal(new[] { 2, 3, 1 }, rig.Entries.Select(e => e.Monsters).ToArray());
        Assert.Equal(new[] { R, L | D, U }, rig.Entries.Select(e => e.Exits).ToArray());
        Assert.Equal(new[] { true, false, false }, rig.Entries.Select(e => e.IsStart).ToArray());
        Assert.Equal(new[] { false, false, true }, rig.Entries.Select(e => e.IsLast).ToArray());
        Assert.All(rig.Entries, e => Assert.Same(rig.Entries[0].Maze, e.Maze));
    }

    [Fact]
    public async Task The_host_log_of_a_whole_dungeon_is_the_documented_lines_and_carries_no_ids_maps_or_paths()
    {
        using Rig rig = await Rig.Start(Plans(2, 3, 1), One(LRight()));

        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "三间房打穿");

        Assert.Equal(
            new[]
            {
                Host + "align status=Aligned mazes=1 plan=Step exit=Right",
                Host + "move status=Posted room=0 attempt=1 exit=Right",
                Host + "enter status=Ready room=1 spawned=3 expected=3 extra=0 exit=Down",
                Host + "move status=Posted room=1 attempt=2 exit=Down",
                Host + "enter status=Ready room=2 spawned=1 expected=1 extra=0 exit=none",
            },
            rig.HostLog.Lines);
        Assert.All(rig.HostLog.Lines, line =>
        {
            Assert.DoesNotMatch(new Regex(@"\d{3,}"), line);                      // 没有服务端怪编号、地图编号、坐标
            Assert.DoesNotContain("/", line);
            Assert.DoesNotContain("\\", line);
            Assert.DoesNotContain(".map", line);
        });
        Assert.Contains(" room=2 rooms=3 moves=2 refused=0 exit=none", rig.Bot.LogLine());
    }

    [Fact]
    public async Task A_refused_move_is_asked_again_after_the_cooldown_and_the_dungeon_goes_on_once_the_server_agrees()
    {
        // 第一间服务端另有一只只有它知道的怪:换房被回错误码 20,留在原地;这只怪"没了"之后再来一次就成
        using Rig rig = await Rig.Start(Plans(2, 1, 1, hiddenInFirst: new[] { 9150 }), One(LRight()));

        await rig.Drive(() => rig.Run.MoveRefusals == 1, "第一次换房被拒");
        Assert.False(rig.Run.Aborted);
        Assert.Equal(0, rig.Run.RoomIndex);
        Assert.Empty(rig.Server.RemoveHidden());                                  // 队友把那只打死了(第一间不是最后一间,不发清图)
        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "再换一次成功、打穿剩下的房间");

        Assert.Equal((3, 1, 3), (rig.Run.MoveRequests, rig.Run.MoveRefusals, rig.Run.RoomsEntered));      // 请求:被拒一次、成功一次、第二间再去第三间一次
        Assert.False(rig.Run.Failed);
        Assert.Contains("move:r0:err20", rig.Server.Journal);
        Assert.Contains(Host + "move status=Refused room=0 refusals=1", rig.HostLog.Lines);
        Assert.DoesNotContain(rig.HostLog.Lines, l => l.Contains("abort"));
    }

    [Fact]
    public async Task A_refusal_in_a_later_room_is_noticed_like_the_first_one_because_every_room_counts_its_own_refusals()
    {
        // 前两间房各有一只只有服务端知道的怪:每间房的第一次换房都被回错误码 20。联网层的拒绝额度按房间算(进新房间重新数),
        // 第二间的第一次被拒与第一间的第一次被拒在状态机里都是"1",宿主两次都要看见,不能拿上一间的数当这一间的
        FakeMultiRoomServer.RoomPlan[] plans =
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(1), new[] { 9150 }),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId), new[] { 9350 }),
            new FakeMultiRoomServer.RoomPlan(1, 1, CombatKit.Sequential(1, RoomKit.ThirdBaseId)),
        };
        using Rig rig = await Rig.Start(plans, One(LRight()));

        await rig.Drive(() => rig.Run.MoveRefusals == 1, "第一间的换房被拒", 10000);
        Assert.Equal(0, rig.Run.RoomIndex);
        Assert.Empty(rig.Server.RemoveHidden());
        await rig.Drive(() => rig.Run.RoomIndex == 1 && rig.Run.MoveRefusals == 2, "第二间的换房也被拒、宿主也看见了", 10000);
        Assert.False(rig.Run.Aborted);
        Assert.Empty(rig.Server.RemoveHidden());
        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "两次都再换一次成功、打穿剩下的房间");

        Assert.Equal((4, 2, 3), (rig.Run.MoveRequests, rig.Run.MoveRefusals, rig.Run.RoomsEntered));
        Assert.False(rig.Run.Failed);
        Assert.Equal(
            new[]
            {
                Host + "align status=Aligned mazes=1 plan=Step exit=Right",
                Host + "move status=Posted room=0 attempt=1 exit=Right",
                Host + "move status=Refused room=0 refusals=1",
                Host + "move status=Posted room=0 attempt=2 exit=Right",
                Host + "enter status=Ready room=1 spawned=1 expected=1 extra=0 exit=Down",
                Host + "move status=Posted room=1 attempt=3 exit=Down",
                Host + "move status=Refused room=1 refusals=1",
                Host + "move status=Posted room=1 attempt=4 exit=Down",
                Host + "enter status=Ready room=2 spawned=1 expected=1 extra=0 exit=none",
            },
            rig.HostLog.Lines);
    }

    [Fact]
    public async Task The_planner_never_walks_back_through_a_cell_it_has_left_so_a_dead_end_boss_candidate_is_the_last_room()
    {
        // 4×2 的迷宫:A(0,0) → B(1,0) → C(2,0) → Y(3,0),B 下面还挂着一格 X(1,1);首领候选格是 X 与 Y。
        // 服务端的三间房是 A、B、X(X 是最后一间)。X 离 B 一步、比 Y 近,规划先去 X;到了 X 之后唯一的路是回 B,而 B 是走过的格:
        // 记着走过的格就不会回头(X 是最后一间、没有出口),不记就会在 X 里多开一个朝上的出口,主角杀光之后还会往回走
        DungeonMaze maze = Maze(4, 2, new[] { R, L | R | D, L | R, L, 0, U, 0, 0 }, new[] { (0, 0) }, new[] { (1, 1), (3, 0) });
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(maze));

        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "三间房打穿");

        Assert.Equal(new GateSide?[] { GateSide.Right, GateSide.Down, null }, rig.Entries.Select(e => e.Exit).ToArray());
        Assert.Equal((3, 2), (rig.Run.RoomsEntered, rig.Run.MoveRequests));
        Assert.Equal(Host + "enter status=Ready room=2 spawned=1 expected=1 extra=0 exit=none", rig.HostLog.Lines[^1]);
        Assert.False(rig.Run.Failed);
    }

    // ================================================================== 失败出口:每一种都有明确的结局和日志,宿主回城

    [Fact]
    public async Task A_door_the_server_keeps_refusing_ends_the_run_with_MoveRefused_after_the_per_room_budget()
    {
        using Rig rig = await Rig.Start(Plans(2, 1, 1, hiddenInFirst: new[] { 9150 }), One(LRight()));

        await rig.Drive(() => rig.Run.Aborted, "换房被拒到额度、宿主放弃");

        Assert.Equal(HostedAbort.MoveRefused, rig.Run.AbortReason);
        Assert.True(rig.Run.Failed);                                              // 流程据此回城镇,与进副本失败同一条路
        Assert.Equal((3, 3), (rig.Run.MoveRequests, rig.Run.MoveRefusals));        // 联网层每间房只放 3 次
        Assert.Equal(3, rig.Server.Moves.Length);                                 // 服务端只收到 3 条,额度用完就不再发
        Assert.Equal(new[] { "move:r0:err20", "move:r0:err20", "move:r0:err20" }, rig.Server.Journal.Where(j => j.StartsWith("move:", StringComparison.Ordinal)).ToArray());
        Assert.Equal(
            new[]
            {
                Host + "align status=Aligned mazes=1 plan=Step exit=Right",
                Host + "move status=Posted room=0 attempt=1 exit=Right",
                Host + "move status=Refused room=0 refusals=1",
                Host + "move status=Posted room=0 attempt=2 exit=Right",
                Host + "move status=Refused room=0 refusals=2",
                Host + "move status=Posted room=0 attempt=3 exit=Right",
                Host + "move status=Refused room=0 refusals=3",
                Host + "abort reason=MoveRefused room=0 rooms=1 phase=Fighting netEnded=false moveInFlight=false load=None fallback=town",
            },
            rig.HostLog.Lines);
        rig.Bot.Advance(rig.Run, 100.0);
        Assert.Equal(HostedBotStop.Aborted, rig.Bot.Reason);                      // 自动机以"宿主放弃"停手
        Assert.EndsWith(" abort=MoveRefused", rig.Bot.LogLine());
    }

    [Fact]
    public async Task A_move_that_is_never_answered_ends_the_run_with_MoveTimeout_at_the_host_limit()
    {
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts
        {
            Server = s => s.SwallowMoves = true,
            Setup = s => s.MoveWaitLimitMs = 300.0,
        });

        await rig.Drive(() => rig.Run.Aborted, "等不到新房间:宿主放弃");

        Assert.Equal(HostedAbort.MoveTimeout, rig.Run.AbortReason);
        Assert.True(rig.Run.Failed);
        Assert.Contains("move:r0:swallowed", rig.Server.Journal);
        Assert.False(rig.Runner.Ended);                                           // 联网层自己的 10 秒超时还没到:是宿主的兜底先动手
        Assert.Equal(Host + "abort reason=MoveTimeout room=0 rooms=1 phase=Fighting netEnded=false moveInFlight=true load=None fallback=town", rig.HostLog.Lines.Last());
    }

    [Fact]
    public async Task A_move_the_net_layer_times_out_ends_the_run_with_MoveTimeout_when_the_net_runner_stops()
    {
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts
        {
            Server = s => s.SwallowMoves = true,
            Combat = c => c.RoomHandshakeTimeout = TimeSpan.FromMilliseconds(400),
        });

        await rig.Drive(() => rig.Run.Aborted, "联网层等换房回应超时收场、宿主放弃");

        Assert.Equal(HostedAbort.MoveTimeout, rig.Run.AbortReason);
        Assert.True(rig.Runner.Ended);
        Assert.Equal(Host + "abort reason=MoveTimeout room=0 rooms=1 phase=Fighting netEnded=true moveInFlight=true load=None fallback=town", rig.HostLog.Lines.Last());
        await CombatKit.WaitUntil(() => rig.Net.Lines.Any(l => l.StartsWith("DNF_NET stage=combat ended outcome=Timeout", StringComparison.Ordinal)), "联网层写了超时收场的日志");
    }

    [Fact]
    public async Task Kills_the_server_never_confirmed_keep_the_door_shut_and_the_host_gives_up_with_RoomNotCleared()
    {
        // 服务端收了击杀却不回确认:联网层的闸门认"全部确认"才放换房,门一直开不了
        using Rig rig = await Rig.Start(Plans(2, 1, 1), One(LRight()), new Opts
        {
            DropKillReplies = true,
            Setup = s => s.MoveWaitLimitMs = 300.0,
        });

        await rig.Drive(() => rig.Run.Aborted, "没有确认、换房被本地闸门拦着:宿主放弃");

        Assert.Equal(HostedAbort.RoomNotCleared, rig.Run.AbortReason);
        Assert.Empty(rig.Server.Moves);                                           // 闸门没开,一条换房命令也没发
        Assert.Equal(Host + "abort reason=RoomNotCleared room=0 rooms=1 phase=Fighting netEnded=false moveInFlight=false load=None fallback=town", rig.HostLog.Lines.Last());
    }

    [Fact]
    public async Task A_new_room_the_server_never_finishes_loading_ends_the_run_with_LoadTimeout()
    {
        using Rig rig = await Rig.Start(Plans(1, 2, 1), One(LRight()), new Opts
        {
            Server = s => s.SwallowLoaded = true,
            Combat = c => c.RoomHandshakeTimeout = TimeSpan.FromMilliseconds(400),
        });

        await rig.Drive(() => rig.Run.Aborted, "新房间装载握手没完:联网层超时收场、宿主放弃");

        Assert.Equal(HostedAbort.LoadTimeout, rig.Run.AbortReason);
        Assert.Equal(1, rig.Run.RoomIndex);                                       // 新房间已经进了,是它的装载握手没完
        Assert.Equal(2, rig.Run.RoomsEntered);
        Assert.True(rig.Runner.Ended);
        Assert.Equal(Host + "abort reason=LoadTimeout room=1 rooms=2 phase=Fighting netEnded=true moveInFlight=false load=AwaitingNotice fallback=town", rig.HostLog.Lines.Last());
    }

    [Fact]
    public async Task A_peer_that_disconnects_in_the_middle_of_a_room_ends_the_run_with_NetEnded()
    {
        using Rig rig = await Rig.Start(Plans(1, 3, 1), One(LRight()));
        await rig.Drive(() => rig.Run.RoomIndex == 1 && rig.Machine.LoadState == OnlineRoomLoadState.None, "进了第二间且装载握手完成");

        rig.Session.Closed = true;                                                // 对端断开
        await rig.Drive(() => rig.Run.Aborted, "联网层因连接关闭收场、宿主放弃");

        Assert.Equal(HostedAbort.NetEnded, rig.Run.AbortReason);
        Assert.True(rig.Run.Failed);
        Assert.Equal(1, rig.Run.RoomIndex);
        Assert.Equal(Host + "abort reason=NetEnded room=1 rooms=2 phase=Fighting netEnded=true moveInFlight=false load=None fallback=town", rig.HostLog.Lines.Last());
        await CombatKit.WaitUntil(() => rig.Net.Lines.Any(l => l.StartsWith("DNF_NET stage=combat ended outcome=ConnectionClosed", StringComparison.Ordinal)), "联网层写了连接关闭的收场日志");
        Assert.Equal(HostedBotStop.Aborted, StopReasonAfterAdvance(rig));
    }

    private static HostedBotStop StopReasonAfterAdvance(Rig rig)
    {
        rig.Bot.Advance(rig.Run, 100.0);
        return rig.Bot.Reason;
    }

    [Fact]
    public async Task A_disconnect_while_a_move_is_in_flight_is_classified_as_MoveTimeout_and_the_net_log_tells_which_it_was()
    {
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts { Server = s => s.SwallowMoves = true });
        await rig.Drive(() => rig.Run.MovePending && rig.Machine.MoveInFlight, "换房命令在途");

        rig.Session.Closed = true;
        await rig.Drive(() => rig.Run.Aborted, "宿主放弃");

        Assert.Equal(HostedAbort.MoveTimeout, rig.Run.AbortReason);               // 换房在途时收场:断线与超时分不开,归一类
        Assert.Equal(Host + "abort reason=MoveTimeout room=0 rooms=1 phase=Fighting netEnded=true moveInFlight=true load=None fallback=town", rig.HostLog.Lines.Last());
        await CombatKit.WaitUntil(() => rig.Net.Lines.Any(l => l.StartsWith("DNF_NET stage=combat ended outcome=ConnectionClosed", StringComparison.Ordinal)), "要分清是断线还是超时,看联网层日志里的 outcome");
    }

    [Fact]
    public async Task A_disconnect_while_the_load_notice_is_awaited_is_classified_as_LoadTimeout()
    {
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts { Server = s => s.SwallowLoaded = true });
        await rig.Drive(() => rig.Run.RoomIndex == 1 && rig.Machine.LoadState == OnlineRoomLoadState.AwaitingNotice, "进了第二间、装载完成命令已发出");

        rig.Session.Closed = true;
        await rig.Drive(() => rig.Run.Aborted, "宿主放弃");

        Assert.Equal(HostedAbort.LoadTimeout, rig.Run.AbortReason);
    }

    [Fact]
    public async Task A_new_room_with_fewer_buildable_monsters_than_the_new_table_ends_the_run_with_RoomMismatch_and_no_move_is_sent_after()
    {
        // 第二间的怪表有 3 条,本地造房间时模板全造不出来:对不上,不进新房间
        using Rig rig = await Rig.Start(Plans(1, 3, 1), One(LRight()), new Opts
        {
            Factory = (r, entry, plan) =>
            {
                r.Entries.Add(entry);
                return r.Fx.Create(Templates(6, entry.Room == 0 ? HostedRoomFixture.Buildable : HostedRoomFixture.Unbuildable), plan, Rig.GatesOf(entry));
            },
        });

        await rig.Drive(() => rig.Run.Aborted, "新房间对不上:宿主放弃");

        Assert.Equal(HostedAbort.RoomMismatch, rig.Run.AbortReason);
        Assert.Equal(0, rig.Run.RoomIndex);                                           // 没有换进去
        Assert.Equal(1, rig.Run.RoomsEntered);
        Assert.Contains(Host + "enter status=Mismatch room=1 spawned=0 expected=3 extra=0", rig.HostLog.Lines);
        string abort = rig.HostLog.Lines.Last();
        Assert.Matches(new Regex(@"^DNF_NET stage=room abort reason=RoomMismatch room=0 rooms=1 phase=Fighting netEnded=false moveInFlight=(true|false) load=\w+ fallback=town$"), abort);
        Assert.Single(rig.Server.Moves);                                              // 之后再没有换房命令
    }

    [Fact]
    public async Task A_new_cell_the_local_maze_cannot_explain_ends_the_run_with_CellMismatch()
    {
        // 第二间的地图编号(906)没有登记在这一格的出口码下:本地迷宫对不上
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts { Setup = s => s.MapAllowsExits = (mapId, exits) => mapId != 906 });

        await rig.Drive(() => rig.Run.Aborted, "新格对不上:宿主放弃");

        Assert.Equal(HostedAbort.CellMismatch, rig.Run.AbortReason);
        Assert.Equal(0, rig.Run.RoomIndex);
        Assert.Single(rig.Server.Moves);
        Assert.StartsWith(Host + "abort reason=CellMismatch room=0 rooms=1 phase=Fighting netEnded=false", rig.HostLog.Lines.Last());
    }

    [Fact]
    public async Task A_new_room_the_host_cannot_build_ends_the_run_with_RoomBuild_for_a_throwing_factory_a_null_room_and_a_room_without_the_exit_door()
    {
        var cases = new (string Name, RigFactory Make, string Line)[]
        {
            ("throws", (r, e, p) => throw new InvalidOperationException("boom"), "enter status=RoomBuild room=1 exception=InvalidOperationException"),
            ("null", (r, e, p) => null, "enter status=NoRoom room=1"),
            ("no exit door", (r, e, p) => r.Fx.Create(Templates(6), p, new[] { GateSide.Left }), "enter status=NoExitGate room=1"),
        };
        foreach (var c in cases)
        {
            RigFactory make = c.Make;
            using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts
            {
                Factory = (r, entry, plan) => entry.Room == 0 ? r.DefaultFactory(entry, plan) : make(r, entry, plan),
            });

            await rig.Drive(() => rig.Run.Aborted, c.Name + ":宿主放弃");

            Assert.Equal(HostedAbort.RoomBuild, rig.Run.AbortReason);
            Assert.Equal(0, rig.Run.RoomIndex);
            Assert.Contains(Host + c.Line, rig.HostLog.Lines);
            Assert.Single(rig.Server.Moves);
        }
    }

    // ================================================================== 对不上就退回单房间(老路一字不变)

    private static void AssertSingleRoom(Rig rig, string alignment, string alignLine, int multiBuilds)
    {
        Assert.False(rig.Room.MultiRoom);
        Assert.False(rig.Run.MultiRoom);
        Assert.Null(rig.Run.ExitSide);
        Assert.Null(rig.Run.ExitRequest);
        Assert.Null(rig.Run.Pump);
        Assert.Equal(1, rig.LegacyBuilds);                                        // 走的是单房间的老工厂
        Assert.Equal(multiBuilds, rig.Entries.Count);
        Assert.Equal(alignment, rig.Room.Alignment.ToString());
        Assert.Equal(new[] { Host + alignLine }, rig.HostLog.Lines);
        Assert.Equal(OnlineRoomStatus.Ready, rig.Room.Status);
    }

    [Fact]
    public async Task Without_a_multi_room_setup_the_run_is_the_plain_single_room_one_and_nothing_is_logged()
    {
        using Rig rig = await Rig.Start(Plans(2, 1, 1), One(LRight()), new Opts { Multi = false });

        Assert.False(rig.Room.MultiRoom);
        Assert.False(rig.Run.MultiRoom);
        Assert.Null(rig.Run.ExitSide);
        Assert.Null(rig.Run.ExitRequest);
        Assert.Null(rig.Run.Pump);
        Assert.Equal(HostedMazeStatus.NoMaze, rig.Room.Alignment);
        Assert.Equal(1, rig.LegacyBuilds);
        Assert.Empty(rig.Entries);
        Assert.Empty(rig.HostLog.Lines);
    }

    [Fact]
    public async Task A_start_cell_that_is_not_a_start_cell_of_any_candidate_maze_falls_back_to_single_room()
    {
        var plans = new[]
        {
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(2)),
            new FakeMultiRoomServer.RoomPlan(1, 1, CombatKit.Sequential(1, RoomKit.SecondBaseId)),
        };
        using Rig rig = await Rig.Start(plans, One(LRight()));

        AssertSingleRoom(rig, "CellMismatch", "align status=CellMismatch mazes=0 fallback=single", 0);
    }

    [Fact]
    public async Task A_start_map_the_exit_code_does_not_allow_falls_back_to_single_room()
    {
        using Rig rig = await Rig.Start(Plans(2, 1, 1), One(LRight()), new Opts { Setup = s => s.MapAllowsExits = (mapId, exits) => false });

        AssertSingleRoom(rig, "MapMismatch", "align status=MapMismatch mazes=0 fallback=single", 0);
    }

    [Fact]
    public async Task Candidate_mazes_that_disagree_on_the_first_step_fall_back_to_single_room()
    {
        using Rig rig = await Rig.Start(Plans(2, 1, 1), new[] { LRight(), LDown() });

        AssertSingleRoom(rig, "Aligned", "align status=Aligned mazes=2 plan=Ambiguous fallback=single", 0);
    }

    [Fact]
    public async Task A_dungeon_that_is_one_room_long_falls_back_to_single_room()
    {
        DungeonMaze one = Maze(1, 1, new[] { R }, new[] { (0, 0) }, new[] { (0, 0) });
        var plans = new[] { new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(2)) };
        using Rig rig = await Rig.Start(plans, One(one));

        AssertSingleRoom(rig, "Aligned", "align status=Aligned mazes=1 plan=Last fallback=single", 0);
    }

    [Fact]
    public async Task A_start_room_the_multi_factory_cannot_build_falls_back_to_the_single_room_factory()
    {
        var cases = new (string Name, RigFactory Factory, string Line)[]
        {
            ("throws", (r, e, p) => throw new InvalidOperationException("boom"), "align status=RoomBuild exception=InvalidOperationException fallback=single"),
            ("null", (r, e, p) => null, "align status=NoRoom fallback=single"),
        };
        foreach (var c in cases)
        {
            using Rig rig = await Rig.Start(Plans(2, 1, 1), One(LRight()), new Opts { Factory = c.Factory });
            Assert.False(rig.Room.MultiRoom, c.Name);
            Assert.False(rig.Run.MultiRoom, c.Name);
            Assert.Equal(1, rig.LegacyBuilds);
            Assert.Contains(Host + "align status=Aligned mazes=1 plan=Step exit=Right", rig.HostLog.Lines);
            Assert.Equal(Host + c.Line, rig.HostLog.Lines.Last());
        }
    }

    [Fact]
    public async Task A_start_room_without_the_planned_exit_door_falls_back_to_the_single_room_factory()
    {
        using Rig rig = await Rig.Start(Plans(2, 1, 1), One(LRight()), new Opts
        {
            Factory = (r, entry, plan) =>
            {
                r.Entries.Add(entry);
                return r.Fx.Create(Templates(6), plan, new[] { GateSide.Left });          // 计划的出口在右,这间房只有左出口
            },
        });

        Assert.False(rig.Room.MultiRoom);
        Assert.False(rig.Run.MultiRoom);
        Assert.Equal(1, rig.LegacyBuilds);
        Assert.Single(rig.Entries);                                                   // 多房间工厂被问过一次,它的房间没被采用
        Assert.Equal(Host + "align status=NoExitGate exit=Right fallback=single", rig.HostLog.Lines.Last());
    }

    [Fact]
    public async Task A_run_created_after_the_machine_already_left_the_start_room_is_single_room()
    {
        using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()), new Opts { Create = false });
        Assert.True(rig.Runner.PostKill(0, 0));
        Assert.True(rig.Runner.PostMoveRoom(0, RoomKit.Move(1, 0, 901, 902)));
        await CombatKit.WaitUntil(() => rig.Machine.Room == 1, "状态机进了第二间");

        HostedRoomRun run = rig.Room.CreateRun(rig.LegacyFactory, 0.0, rig.Setup);

        Assert.NotNull(run);
        Assert.False(rig.Room.MultiRoom);
        Assert.False(run.MultiRoom);
        Assert.Equal(new[] { Host + "align status=NotAtStart room=1 fallback=single" }, rig.HostLog.Lines);
    }

    // ================================================================== 绝不发不安全的换房

    [Fact]
    public async Task The_exit_request_only_posts_the_planned_side_and_clamps_the_hero_into_the_door_rectangle()
    {
        foreach ((int x, int y, int expectedX, int expectedY) in new[] { (5000, -300, 1095, 47), (-5000, 9000, 976, 152) })
        {
            using Rig rig = await Rig.Start(Plans(1, 1, 1), One(LRight()));
            rig.Slay();
            await rig.Wait(() => rig.Run.RoomCleared, "怪的尸体消失、清房成立");

            Assert.False(rig.Run.ExitRequest(GateSide.Left, x, y));               // 不是规划的那一侧:不排
            Assert.False(rig.Run.ExitRequest(GateSide.Up, x, y));
            Assert.False(rig.Run.ExitRequest(GateSide.Down, x, y));
            await Task.Delay(100);
            Assert.Empty(rig.Server.Moves);

            Assert.True(rig.Run.ExitRequest(GateSide.Right, x, y));               // 规划的那一侧:位置夹进右出口的识别矩形再排
            await CombatKit.WaitUntil(() => rig.Server.Moves.Length == 1, "服务端收到换房命令");
            MoveRoomView move = rig.Server.Moves[0];
            Assert.Equal((expectedX, expectedY), (move.PositionX, move.PositionY));
            Assert.Equal((1, 0), ((int)move.TargetX, (int)move.TargetY));
        }
    }

    [Fact]
    public async Task The_exit_request_posts_nothing_once_the_run_is_aborted_or_the_net_runner_has_ended()
    {
        using Rig aborted = await Rig.Start(Plans(1, 1, 1), One(LRight()));
        aborted.Run.Abort(HostedAbort.NetEnded);
        Assert.False(aborted.Run.ExitRequest(GateSide.Right, 1090, 100));
        await Task.Delay(100);
        Assert.Empty(aborted.Server.Moves);

        using Rig ended = await Rig.Start(Plans(1, 1, 1), One(LRight()));
        ended.Stop();
        Assert.True(ended.Runner.Ended);
        Assert.False(ended.Run.ExitRequest(GateSide.Right, 1090, 100));
        Assert.Empty(ended.Server.Moves);
    }

    [Fact]
    public async Task A_target_cell_beyond_what_a_move_request_can_carry_is_never_sent_and_ends_the_run_with_CellMismatch()
    {
        // 一条 52 格的横线:起点在第 49 格(0x31),下一格是第 50 格,超过换房请求能带的上限(0x31):不发
        var exits = new int[52];
        for (int i = 0; i < exits.Length; i++)
        {
            exits[i] = (i == 0 ? 0 : L) | (i == exits.Length - 1 ? 0 : R);
        }
        DungeonMaze wide = Maze(52, 1, exits, new[] { (49, 0) }, new[] { (51, 0) });
        var plans = new[]
        {
            new FakeMultiRoomServer.RoomPlan(49, 0, CombatKit.Sequential(1)),
            new FakeMultiRoomServer.RoomPlan(50, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId)),
            new FakeMultiRoomServer.RoomPlan(51, 0, CombatKit.Sequential(1, RoomKit.ThirdBaseId)),
        };
        using Rig rig = await Rig.Start(plans, One(wide), new Opts { Server = s => s.MazeWidth = 52 });
        Assert.True(rig.Room.MultiRoom);

        await rig.Drive(() => rig.Run.Aborted, "目标格拼不出合法请求:宿主放弃");

        Assert.Equal(HostedAbort.CellMismatch, rig.Run.AbortReason);
        Assert.Empty(rig.Server.Moves);                                           // 不安全的换房一条也没发
        Assert.Equal(0, rig.Run.MoveRequests);
        Assert.Equal(Host + "abort reason=CellMismatch room=0 rooms=1 phase=Fighting netEnded=false moveInFlight=false load=None fallback=town", rig.HostLog.Lines.Last());
    }

    // ================================================================== 清图之后出口不再有

    [Fact]
    public async Task A_clear_notice_that_arrives_while_a_door_is_still_planned_closes_the_door_for_good()
    {
        // 服务端只在最后一间发清图通知;万一它在别处发了,联网阶段就离开了"打怪",宿主不能再去排换房(联网层也已经拦着)
        using Rig rig = await Rig.Start(Plans(2, 1, 1), One(LRight()));
        Assert.Equal(GateSide.Right, rig.Run.ExitSide);

        rig.Session.Push(CombatKit.Clear());
        await rig.Drive(() => rig.Run.ExitSide == null, "清图通知来了、出口关掉");

        Assert.Matches(new Regex(@"^DNF_NET stage=room exit status=Closed phase=\w+ room=0$"), rig.HostLog.Lines.Last());
        Assert.NotEqual(OnlineCombatPhase.Fighting, rig.Machine.Phase);
        for (int i = 0; i < 100; i++)
        {
            rig.Bot.Advance(rig.Run, 100.0);                                          // 之后主角再怎么杀怪、站在哪里,也不再排换房
            await Task.Delay(1);
        }
        Assert.Equal(0, rig.Run.MoveRequests);
        Assert.Empty(rig.Server.Moves);
    }

    // ================================================================== 材料的参数检查

    [Fact]
    public void The_multi_room_setup_validates_its_arguments_and_the_move_wait_limit()
    {
        OnlineRoomEntryFactory factory = (e, p) => null;
        Assert.Throws<ArgumentNullException>(() => new OnlineMultiRoomSetup(null, factory));
        Assert.Throws<ArgumentNullException>(() => new OnlineMultiRoomSetup(One(LRight()), null));

        var setup = new OnlineMultiRoomSetup(One(LRight()), factory);
        Assert.Equal(OnlineMultiRoomSetup.DefaultMoveWaitLimitMs, setup.MoveWaitLimitMs);
        Assert.Equal(30000.0, OnlineMultiRoomSetup.DefaultMoveWaitLimitMs);
        setup.MoveWaitLimitMs = 1.0;
        Assert.Equal(1.0, setup.MoveWaitLimitMs);
        foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => setup.MoveWaitLimitMs = bad);
        }
        Assert.Equal(1.0, setup.MoveWaitLimitMs);                                  // 非法值不改原值
        Assert.Null(setup.MapAllowsExits);
        Assert.Null(setup.IsBossMap);
        Assert.Null(setup.Log);
    }

    [Fact]
    public void A_room_entry_needs_a_maze_and_reports_its_exits_start_and_last()
    {
        DungeonMaze maze = LRight();
        Assert.Throws<ArgumentNullException>(() => new OnlineRoomEntry(0, 0, 0, 905, null, GateSide.Right, null, 1));

        var start = new OnlineRoomEntry(0, 0, 0, 905, null, GateSide.Right, maze, 2);
        Assert.True(start.IsStart);
        Assert.False(start.IsLast);
        Assert.Equal(R, start.Exits);
        Assert.Equal(2, start.Monsters);

        var middle = new OnlineRoomEntry(1, 1, 0, -1, GateSide.Left, GateSide.Down, maze, 0);
        Assert.False(middle.IsStart);
        Assert.False(middle.IsLast);
        Assert.Equal(L | D, middle.Exits);
        Assert.Equal(-1, middle.MapId);
        Assert.Equal(0, middle.Monsters);                                          // 后面的房间可以是空房间

        var last = new OnlineRoomEntry(2, 1, 1, 907, GateSide.Up, null, maze, 1);
        Assert.False(last.IsStart);
        Assert.True(last.IsLast);
        Assert.Equal(U, last.Exits);
    }
}
