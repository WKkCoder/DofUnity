using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// 副本宿主呈现合流(r15-dungeonhost)的内存端到端:合成起始地图的怪记录 → 宿主房间里按记录造本地怪(<see cref="OnlineDungeonRoom"/>,房间与怪物走 Play 层既有的
/// <see cref="RoomCombatSession.Create"/>)→ 本地怪死亡经 <see cref="RoomKillWatcher"/>(在 <see cref="HostedRoomRun"/> 里)只报一次、带服务端编号 → 假服务端按协议清图、结算 →
/// 回城。会话与服务端是联网层测试共用的内存假实现(<see cref="CombatFakeSession"/>、<see cref="FakeCombatServer"/>),不连任何真实服务;
/// 怪编号是 9100 起的合成值,怪物编号是夹具里的合成编号(1 完整、3 造不出来)。
/// </summary>
public partial class OnlineDungeonRoomTests
{
    private const int Ok = HostedRoomFixture.Buildable;
    private const int Bad = HostedRoomFixture.Unbuildable;
    private static readonly TimeSpan Roomy = TimeSpan.FromSeconds(5);

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

    /// <summary>假的城镇会话(流程回城之后拿到的东西):没有任何数据。</summary>
    private sealed class FakeTown : IHeroSession
    {
        public string MapPath => "fake/town";

        public TownHero Hero => null;

        public TownCamera Camera => null;

        public int LayerCount => 0;

        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
        }

        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    /// <summary>
    /// 一整套:假服务端 + 假会话 + 进副本薄封装(接了联网战斗,后台线程里跑)+ 联网房间接线 + 合成房间夹具。
    /// <see cref="Start"/> 返回时进副本已成功、战斗状态机已建好;<paramref name="autoCreate"/> 为真时房间也造好了(用生产路径 <see cref="RoomCombatSession.Create"/>)。
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private Task _background;

        public int[] Ids { get; private set; }
        public FakeCombatServer Server { get; private set; }
        public CombatFakeSession Session { get; private set; }
        public OnlineDungeonRunner Runner { get; private set; }
        public OnlineDungeonCombatRunner Combat { get; private set; }
        public OnlineDungeonRoom Room { get; private set; }
        public HostedRoomFixture Fixture { get; private set; }
        public LogSink Log { get; } = new LogSink();
        public HostedRoomRun Run { get; set; }

        public OnlineDungeonCombat Machine => Combat.Machine;

        /// <param name="ids">起始地图里每条怪记录的服务端编号(合成值)。</param>
        /// <param name="templates">房间地图自己的 [monster](出生点模板)。</param>
        /// <param name="serverTable">假服务端怪表里的编号;不给就是 <paramref name="ids"/> 去重。</param>
        /// <param name="withholdClear">怪表清空了假服务端也先不发清图通知(测试稍后手动放出)。</param>
        public static async Task<Rig> Start(int[] ids, IReadOnlyList<RoomMonsterSpawn> templates, IEnumerable<int> serverTable = null,
            bool withholdClear = false, bool autoCreate = true, double returnDelayMs = 0.0, byte[] map29 = null)
        {
            var rig = new Rig { Ids = ids };
            rig.Server = new FakeCombatServer(serverTable ?? ids.Distinct()) { WithholdClear = withholdClear };
            rig.Session = CombatFakeSession.ForEntry(rig.Server, map29 ?? CombatKit.Map29Payload(ids));
            rig.Runner = new OnlineDungeonRunner(rig.Session, token => Task.FromResult(0));
            rig.Combat = rig.Runner.UseCombat(CombatKit.Options());
            rig.Room = new OnlineDungeonRoom(rig.Combat);
            rig.Fixture = new HostedRoomFixture(templates);

            // 进副本回调是在联网跑器的后台线程里同步调的:让等它的测试代码另起线程继续,别在跑器线程上内联执行
            var entered = new TaskCompletionSource<OnlineDungeonResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig._background = Task.Run(() => rig.Runner.RunAsync(901, 3, rig.Log.Add, rig._cancel.Token, Roomy, Roomy, Roomy, r => entered.TrySetResult(r)));
            OnlineDungeonResult result = await GatewayTestSupport.Bounded(entered.Task, 30);
            Assert.True(result.Entered, "合成的进副本应当成功");
            await CombatKit.WaitUntil(() => rig.Combat.Machine != null, "战斗状态机建好");
            if (autoCreate)
            {
                rig.Run = rig.Room.CreateRun(plan => rig.Fixture.Create(plan), returnDelayMs);
                Assert.NotNull(rig.Run);
            }
            return rig;
        }

        /// <summary>推进房间(每步 10 毫秒)直到条件成立;超时让测试失败(失败信息里带上此刻的阶段、结束与否、服务端流水,方便定位)。</summary>
        public async Task Drive(Func<bool> until, string what, int milliseconds = 8000)
        {
            try
            {
                await CombatKit.WaitUntil(() =>
                {
                    HostStep(Run, 10);
                    return until();
                }, what, milliseconds);
            }
            catch (Xunit.Sdk.XunitException e)
            {
                string trace;
                try
                {
                    trace = CombatKit.Trace(Machine.Events);
                }
                catch (InvalidOperationException)
                {
                    trace = "(事件表正被写入)";
                }
                throw new Xunit.Sdk.XunitException(e.Message + " [phase=" + Machine?.Phase + " ended=" + Combat.Ended + " blocked=" + Machine?.Blocked
                    + " choice=" + Run.Choice + " cmd42=" + Session.Commands(42) + " cmd49=" + Session.Commands(49)
                    + " trace=" + trace + " end=" + (Log.Lines.FirstOrDefault(l => l.StartsWith("DNF_NET stage=combat ended") || l.StartsWith("DNF_NET stage=combat done")) ?? "none") + "]");
            }
        }

        /// <summary>打死一只本地怪(按位置),推进房间,等假服务端收到这条击杀(它的击杀命令数达到 <paramref name="expectedKillCommands"/>)。</summary>
        public async Task KillAndWait(int position, int expectedKillCommands)
        {
            HostedRoomFixture.Kill(Run.Room.Monsters[position]);
            await Drive(() => Session.Commands(42) >= expectedKillCommands, "第 " + expectedKillCommands + " 条击杀命令到达假服务端");
        }

        /// <summary>多推进一会儿(真实的几十毫秒),给"不该再发的包"留出出现的机会。</summary>
        public async Task Settle(int steps = 30)
        {
            for (int i = 0; i < steps; i++)
            {
                HostStep(Run, 10);
                await Task.Delay(5);
            }
        }

        public KillView[] KillsSeenByServer => Session.Sent
            .Where(s => s.Class == PacketClass.Cmd && s.Id == 42)
            .Select(s => KillView.Parse(s.Payload))
            .ToArray();

        /// <summary>取消联网战斗并等后台收场:之后读状态机的事件表就不会和循环线程并发(事件表是普通 List)。</summary>
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
            Fixture?.Dispose();
            _cancel.Dispose();
        }
    }

    private static RoomMonsterSpawn[] Templates(params int[] codes)
    {
        return codes.Select((code, i) => HostedRoomFixture.Spawn(code, 340 + 40 * i)).ToArray();
    }

    /// <summary>
    /// 宿主每帧推进会话的方式(与 TownDemo.StepSession 走同一条路:按住的技能键清空、出招指令打开,会话是技能会话就走六个参数的 Update,没有按键的一帧)。
    /// 端到端用这条路推进,是为了让"宿主真正调用的那个重载"也被测到,而不只是三个参数的那个。
    /// </summary>
    private static void HostStep(IHeroSession session, double deltaMs)
    {
        if (session is ISkillHoldSession hold)
        {
            hold.SkillsHeld = SkillKeys.None;
        }
        if (session is ICommandSession commands)
        {
            commands.CommandsEnabled = true;
        }
        if (session is ISkillSession skills)
        {
            skills.Update(deltaMs, 0, 0, false, false, SkillKeys.None);
        }
        else
        {
            session.Update(deltaMs, 0, 0);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 一、生成:怪记录 ↔ 本地怪
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_room_gets_one_local_monster_per_start_map_record_in_record_order()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok));

        Assert.Equal(OnlineRoomStatus.Ready, rig.Room.Status);
        Assert.Equal(3, rig.Machine.Monsters.Count);
        Assert.Equal((3, 3, 0, 0), (rig.Room.Expected, rig.Room.Spawned, rig.Room.Missing.Count, rig.Room.Extra));
        IReadOnlyList<MonsterActor> monsters = rig.Run.Room.Monsters;
        Assert.Equal(3, monsters.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, rig.Machine.Monsters[i].LocalId);
            Assert.Equal(i, monsters[i].Index);                                   // 第 i 只本地怪对应第 i 条记录
            Assert.Equal(ids[i], rig.Machine.Monsters[i].ServerId);               // 对应表里这条记录的服务端编号
            Assert.Equal(340 + 40 * i, (int)monsters[i].X);                       // 第 i 条记录拿第 i 个出生点模板
            Assert.True(monsters[i].IsAlive);
        }
        Assert.Equal(3, rig.Run.Room.AliveCount);
    }

    [Fact]
    public async Task More_records_than_templates_cycle_the_templates_and_fewer_records_use_only_the_first_templates()
    {
        int[] five = CombatKit.Sequential(5);
        using (Rig rig = await Rig.Start(five, Templates(Ok, Ok)))
        {
            Assert.Equal((5, 5, 0), (rig.Room.Expected, rig.Room.Spawned, rig.Room.Missing.Count));
            Assert.Equal(new[] { 340, 380, 340, 380, 340 }, rig.Run.Room.Monsters.Select(m => (int)m.X).ToArray());
        }

        int[] two = CombatKit.Sequential(2);
        using (Rig rig = await Rig.Start(two, Templates(Ok, Ok, Ok, Ok)))
        {
            Assert.Equal((2, 2, 0, 0), (rig.Room.Expected, rig.Room.Spawned, rig.Room.Missing.Count, rig.Room.Extra));
            Assert.Equal(2, rig.Run.Room.Monsters.Count);                          // 多出来的模板不会变成没有服务端编号的怪
        }
    }

    [Fact]
    public async Task Templates_that_cannot_be_built_leave_their_records_missing_without_shifting_the_others()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Bad, Ok));

        Assert.Equal(OnlineRoomStatus.Ready, rig.Room.Status);
        Assert.Equal((3, 2, 0), (rig.Room.Expected, rig.Room.Spawned, rig.Room.Extra));
        Assert.Equal(new[] { 1 }, rig.Room.Missing.ToArray());
        Assert.Equal(new[] { 0, 2 }, rig.Run.Room.Monsters.Select(m => m.Index).ToArray());   // 第三条记录的怪仍是 2
    }

    [Fact]
    public async Task The_room_is_not_built_when_there_is_nothing_to_put_in_it()
    {
        int[] ids = CombatKit.Sequential(2);

        using (Rig noTemplates = await Rig.Start(ids, Templates(), autoCreate: false))
        {
            Assert.Null(noTemplates.Room.CreateRun(plan => noTemplates.Fixture.Create(plan)));
            Assert.Equal(OnlineRoomStatus.NothingSpawned, noTemplates.Room.Status);
            Assert.Null(noTemplates.Room.Run);
        }

        using (Rig allBad = await Rig.Start(ids, Templates(Bad, Bad), autoCreate: false))
        {
            Assert.Null(allBad.Room.CreateRun(plan => allBad.Fixture.Create(plan)));
            Assert.Equal(OnlineRoomStatus.NothingSpawned, allBad.Room.Status);
            Assert.Equal(new[] { 0, 1 }, allBad.Room.Missing.ToArray());
        }

        using (Rig noRoom = await Rig.Start(ids, Templates(Ok), autoCreate: false))
        {
            Assert.Null(noRoom.Room.CreateRun(plan => null));
            Assert.Equal(OnlineRoomStatus.NoRoom, noRoom.Room.Status);
        }

        using (Rig empty = await Rig.Start(new int[0], Templates(Ok), autoCreate: false, map29: DungeonEntryWire.Map29()))
        {
            bool asked = false;
            Assert.Null(empty.Room.CreateRun(plan =>
            {
                asked = true;
                return empty.Fixture.Create(plan);
            }));
            Assert.Equal(OnlineRoomStatus.NoMonsters, empty.Room.Status);
            Assert.False(asked);                                                    // 没有怪就不造房间
        }
    }

    [Fact]
    public void Before_the_combat_machine_exists_nothing_is_built()
    {
        var session = new CombatFakeSession();
        OnlineDungeonCombatRunner combat = new OnlineDungeonRunner(session).UseCombat(CombatKit.Options());
        var room = new OnlineDungeonRoom(combat);
        bool asked = false;

        Assert.False(room.MachineReady);
        Assert.Null(room.CreateRun(plan =>
        {
            asked = true;
            return null;
        }));

        Assert.Equal(OnlineRoomStatus.NotReady, room.Status);
        Assert.False(asked);
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonRoom(null!));
        Assert.Throws<ArgumentNullException>(() => room.CreateRun(null!));
    }

    [Fact]
    public async Task Creating_the_run_twice_gives_the_same_run_and_asks_the_factory_once()
    {
        using Rig rig = await Rig.Start(CombatKit.Sequential(3), Templates(Ok, Ok, Ok), autoCreate: false);
        int asked = 0;
        OnlineRoomFactory factory = plan =>
        {
            asked++;
            return rig.Fixture.Create(plan);
        };

        HostedRoomRun first = rig.Room.CreateRun(factory);
        HostedRoomRun second = rig.Room.CreateRun(factory);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(first, rig.Room.Run);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task The_log_line_has_the_status_and_the_counts_and_no_numbers_from_the_map()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Bad, Ok));

        string line = rig.Room.LogLine();

        Assert.Equal("DNF_NET stage=room status=Ready spawned=2 expected=3 missing=1 extra=0", line);
        Assert.StartsWith(OnlineDungeonRoom.LogPrefix, line);
        foreach (int id in ids)
        {
            Assert.DoesNotContain(id.ToString(), line);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 二、击杀:本地怪死亡 → 恰好一个击杀包,带服务端编号
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_local_kill_sends_exactly_one_kill_packet_and_it_carries_the_server_id_not_the_local_one()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok), withholdClear: true);

        await rig.KillAndWait(1, expectedKillCommands: 1);
        await rig.Settle();                                                         // 之后再多推进也不该有第二个包

        KillView[] kills = rig.KillsSeenByServer;
        Assert.Single(kills);
        Assert.Equal(ids[1], kills[0].MobId);                                      // 9101,不是本地编号 1
        Assert.NotEqual(1, kills[0].MobId);
        Assert.Equal(new[] { "kill:" + ids[1] }, rig.Server.Journal);
        Assert.Equal(1, rig.Session.Commands(42));
        Assert.Equal((1, 0), (rig.Run.Reported, rig.Run.Dropped));
        Assert.Equal(0, rig.Machine.Blocked);                                      // 宿主侧只交一次,状态机没有拦下任何东西
        Assert.Equal(OnlineCombatMonsterState.Confirmed, rig.Machine.Monsters[1].State);
        Assert.Equal(OnlineCombatMonsterState.Alive, rig.Machine.Monsters[0].State);
        Assert.Equal(OnlineCombatMonsterState.Alive, rig.Machine.Monsters[2].State);
    }

    [Fact]
    public async Task Every_monster_reports_its_own_server_id_in_the_order_the_player_killed_them()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok), withholdClear: true);

        await rig.KillAndWait(2, 1);
        await rig.KillAndWait(0, 2);
        await rig.KillAndWait(1, 3);
        await rig.Settle();

        Assert.Equal(new[] { ids[2], ids[0], ids[1] }, rig.KillsSeenByServer.Select(k => (int)k.MobId).ToArray());
        Assert.Equal(new[] { "kill:" + ids[2], "kill:" + ids[0], "kill:" + ids[1] }, rig.Server.Journal);   // 清图通知被压住,这里只看击杀
        Assert.Equal(0, rig.Server.Remaining);
        Assert.Equal(3, rig.Run.Reported);
        Assert.Equal(0, rig.Machine.Blocked);
        Assert.Equal(0, rig.Server.Refusals);
    }

    [Fact]
    public async Task A_planned_room_with_a_missing_record_still_reports_each_survivor_under_its_own_record()
    {
        // 模板 [可造, 造不出, 可造]:第三只怪必须报第三条记录的编号,而不是顶替第二条记录的位置
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Bad, Ok));

        Assert.Equal(new[] { 0, 2 }, rig.Run.Room.Monsters.Select(m => m.Index).ToArray());
        await rig.KillAndWait(1, 1);                                                // 列表位置 1 = 记录 2
        await rig.KillAndWait(0, 2);
        await rig.Settle();

        Assert.Equal(new[] { ids[2], ids[0] }, rig.KillsSeenByServer.Select(k => (int)k.MobId).ToArray());
        Assert.Equal(new[] { "kill:" + ids[2], "kill:" + ids[0] }, rig.Server.Journal);   // 记录 1 的怪本地没有,服务端不会清图
        Assert.False(rig.Server.Cleared);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
        Assert.Equal(0, rig.Session.Commands(49));
    }

    [Fact]
    public async Task A_kill_the_state_machine_blocks_makes_no_packet_and_the_rest_still_clear_the_map()
    {
        // 记录里有两条服务端编号相同:第三条没有独立编号,状态机在本地拦下;本地照样打死它,但不多出任何包
        int[] ids = { 9100, 9101, 9101 };
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok));
        Assert.True(rig.Machine.Monsters[2].DuplicateNumber);

        HostedRoomFixture.Kill(rig.Run.Room.Monsters[2]);                           // 先打死没有独立编号的那只:状态机拦下,不发包
        await rig.Drive(() => rig.Machine.Blocked >= 1, "状态机拦下重复编号的击杀");
        Assert.Equal(0, rig.Session.Commands(42));
        await rig.KillAndWait(0, 1);
        await rig.KillAndWait(1, 2);
        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "清图结算之后回城");

        Assert.Equal(2, rig.Session.Commands(42));                                  // 只有两个击杀包
        Assert.Equal(new[] { 9100, 9101 }, rig.KillsSeenByServer.Select(k => (int)k.MobId).ToArray());
        Assert.Equal(1, rig.Machine.Blocked);
        Assert.Contains("KillBlocked:2:NoServerNumber", CombatKit.Trace(rig.Machine.Events));
        Assert.Equal(0, rig.Server.Refusals);
        Assert.Equal(3, rig.Run.Reported);                                          // 宿主三只都交了,拦不拦是联网层的事
    }

    [Fact]
    public async Task A_monster_outside_the_table_is_never_reported_as_a_kill_packet()
    {
        // 房间工厂没照计划造(多出一只地图自己的怪):序号 3 不在 3 条记录的表里,状态机拦下,不发包
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok, Ok), autoCreate: false);
        rig.Run = rig.Room.CreateRun(plan => rig.Fixture.Create(null));              // 无视计划:地图自己的 4 只

        Assert.NotNull(rig.Run);
        Assert.Equal((3, 3, 0, 1), (rig.Room.Expected, rig.Room.Spawned, rig.Room.Missing.Count, rig.Room.Extra));
        HostedRoomFixture.Kill(rig.Run.Room.Monsters[3]);
        await rig.Drive(() => rig.Machine.Blocked >= 1, "状态机拦下表外编号的击杀");
        await rig.Settle();
        rig.Stop();

        Assert.Equal(0, rig.Session.Commands(42));
        Assert.Contains("KillBlocked:3:UnknownLocalId", CombatKit.Trace(rig.Machine.Events));
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
    }

    [Fact]
    public async Task A_kill_after_the_combat_has_ended_is_dropped_without_a_packet()
    {
        int[] ids = CombatKit.Sequential(2);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok), withholdClear: true);
        rig.Session.Closed = true;                                                  // 连接断开:战斗跑器收场
        await CombatKit.WaitUntil(() => rig.Combat.Ended, "战斗跑器结束");

        HostedRoomFixture.Kill(rig.Run.Room.Monsters[0]);
        await rig.Settle(steps: 5);

        Assert.Equal(0, rig.Session.Commands(42));
        Assert.Equal((0, 1), (rig.Run.Reported, rig.Run.Dropped));
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);                        // 没有结算完成,不回城
    }

    // ---------------------------------------------------------------------------------------------
    // 三、清图 → 结算 → 回城
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Killing_everything_clears_settles_and_the_run_asks_for_the_return_to_town()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok));

        await rig.KillAndWait(0, 1);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
        await rig.KillAndWait(1, 2);
        await rig.KillAndWait(2, 3);
        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "结算收齐之后回城");

        Assert.Equal(new[] { "kill:" + ids[0], "kill:" + ids[1], "kill:" + ids[2], "clear", "settle" }, rig.Server.Journal);
        Assert.Equal(0, rig.Server.PrematureSettlements);
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Equal(OnlineCombatPhase.Settled, rig.Machine.Phase);
        Assert.True(rig.Combat.Ended);
        Assert.True(rig.Run.Finished);
        Assert.False(rig.Run.Failed);
        Assert.Equal(3, rig.Run.Reported);
        Assert.Equal(0, rig.Run.Dropped);
        Assert.Equal(0, rig.Machine.Blocked);
    }

    [Fact]
    public async Task Killing_everything_locally_does_not_return_until_the_server_has_cleared_and_settled()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok), withholdClear: true);

        await rig.KillAndWait(0, 1);
        await rig.KillAndWait(1, 2);
        await rig.KillAndWait(2, 3);
        await rig.Settle(steps: 60);                                                // 本地杀光了,服务端还没发清图通知

        Assert.Equal(0, rig.Run.Room.AliveCount);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
        Assert.Equal(OnlineCombatPhase.Fighting, rig.Machine.Phase);
        Assert.Equal(0, rig.Session.Commands(49));
        Assert.Equal(0, rig.Server.PrematureSettlements);

        rig.Session.Push(rig.Server.ReleaseClear());                                // 服务端这时才发清图通知
        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "清图通知之后结算并回城");

        Assert.Equal(1, rig.Server.Settlements);
        Assert.Equal(0, rig.Server.PrematureSettlements);
        Assert.Equal(OnlineCombatPhase.Settled, rig.Machine.Phase);
    }

    [Fact]
    public async Task The_return_waits_for_the_configured_delay_after_the_settlement_is_done()
    {
        int[] ids = CombatKit.Sequential(1);
        using Rig rig = await Rig.Start(ids, Templates(Ok), returnDelayMs: 400);

        await rig.KillAndWait(0, 1);
        await CombatKit.WaitUntil(() => rig.Combat.Ended && rig.Machine.Phase == OnlineCombatPhase.Settled, "联网结算收齐");
        HostStep(rig.Run, 100);                                                     // 看到结算完成的这一步算第一个 100 毫秒
        HostStep(rig.Run, 100);
        HostStep(rig.Run, 100);

        Assert.True(rig.Run.Finished);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);                        // 300 毫秒,不到 400

        HostStep(rig.Run, 100);
        Assert.Equal(SettlementChoice.ReturnToTown, rig.Run.Choice);
    }

    [Fact]
    public async Task After_the_settlement_the_game_flow_goes_back_to_a_runnable_town()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok), autoCreate: false);
        var towns = new List<FakeTown>();
        int dungeonsBuilt = 0;
        var flow = new GameFlow(
            () =>
            {
                var town = new FakeTown();
                towns.Add(town);
                return town;
            },
            (path, run) =>
            {
                dungeonsBuilt++;
                return rig.Room.CreateRun(plan => rig.Fixture.Create(plan));
            },
            new[] { new DungeonEntry("dungeon/test/online.dgn", "ONLINE") });

        flow.EnterTown("start");
        flow.StartDungeon("dungeon/test/online.dgn", "net room");

        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(1, dungeonsBuilt);
        Assert.Same(rig.Room.Run, flow.Run);
        rig.Run = rig.Room.Run;

        // 宿主每帧:推进当前会话,再让流程 Poll;本地怪被打死、联网清图结算之后流程自己回城
        await rig.KillAndWait(0, 1);
        await rig.KillAndWait(1, 2);
        await rig.KillAndWait(2, 3);
        await CombatKit.WaitUntil(() =>
        {
            HostStep(flow.Session, 10);
            flow.Poll();
            return flow.Stage == GameStage.Town;
        }, "流程回到城镇");

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(1, flow.Settlements);
        Assert.Equal(0, flow.Failures);
        Assert.Equal(0, flow.GiveUps);
        Assert.Equal(2, towns.Count);                                               // 回城造了新的城镇会话,是流程原来的回城路径
        Assert.Same(towns[1], flow.Session);
        Assert.Equal("choice ReturnToTown", flow.History[flow.History.Count - 1].Reason);

        // 回到城镇后宿主能照常推进(城镇会话不是联网房间,不再有击杀要报)
        HostStep(flow.Session, 10);
        Assert.False(flow.Poll());
        Assert.Equal(3, rig.Session.Commands(42));
    }

    // ---------------------------------------------------------------------------------------------
    // 四、与进副本日志同族的接线日志
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_chain_leaves_the_usual_combat_log_lines_and_no_room_numbers()
    {
        int[] ids = CombatKit.Sequential(2);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok));

        await rig.KillAndWait(0, 1);
        await rig.KillAndWait(1, 2);
        await rig.Drive(() => rig.Run.Choice == SettlementChoice.ReturnToTown, "回城");
        await CombatKit.WaitUntil(() => rig.Log.Lines.Any(l => l.StartsWith("DNF_NET stage=combat done ")), "战斗完成行");

        Assert.Contains(rig.Log.Lines, l => l.StartsWith("DNF_NET stage=combat begin monsters=2 "));
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("DNF_NET stage=combat done outcome=ReturnReady phase=Settled monsters=2 sent=2 confirmed=2 "));
        Assert.StartsWith("DNF_NET stage=room status=Ready", rig.Room.LogLine());
    }
}
