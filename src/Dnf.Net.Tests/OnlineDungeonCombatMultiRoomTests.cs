using System.Diagnostics;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 多房间联网战斗(换房、换表、装载握手)的端到端测试:战斗跑器(<see cref="OnlineDungeonCombatRunner"/>)接会话接缝的线程安全假实现
/// (<see cref="CombatFakeSession"/>),对面是照静态读码建模的多房间"服务端"替身(<see cref="FakeMultiRoomServer"/>)。
/// 服务端替身的行为是测试用的建模(协议来源见它的类注释),<b>不是</b>真服的行为,也没有真服抓包做依据。
/// 数据全是合成的:怪编号第一间 9100 起、第二间 9300 起、第三间 9500 起,位置取 901、902 一类,回包的错误码是协议枚举(19、20)。
/// 主链:进图、杀光第一间、未清图换房被回错误码留在原地、清图后换房成功、新图换表后击杀用新表编号、杀光、清图通知、结算两段收齐。
/// </summary>
public class OnlineDungeonCombatMultiRoomTests
{
    private const string Prefix = "DNF_NET stage=combat";

    private static FakeMultiRoomServer.RoomPlan[] OneRoom(int count)
    {
        return new[] { new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(count)) };
    }

    /// <summary>两间房:第一间 3 只(<paramref name="hiddenInFirst"/> 时服务端表里另有一只只有它知道的),第二间 2 只、是最后一间。</summary>
    private static FakeMultiRoomServer.RoomPlan[] TwoRooms(bool hiddenInFirst = false)
    {
        return new[]
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(3), hiddenInFirst ? new[] { 9150 } : null),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(2, RoomKit.SecondBaseId)),
        };
    }

    /// <summary>三间房,格坐标 (0,0)、(1,0)、(1,1),怪数 2、3、1。</summary>
    private static FakeMultiRoomServer.RoomPlan[] ThreeRooms()
    {
        return new[]
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(2)),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(3, RoomKit.SecondBaseId)),
            new FakeMultiRoomServer.RoomPlan(1, 1, CombatKit.Sequential(1, RoomKit.ThirdBaseId)),
        };
    }

    /// <summary>测试用可调项:轮询窗口短,整段上限 <paramref name="overallSeconds"/>,换房与装载握手各等 <paramref name="handshakeSeconds"/> 秒。</summary>
    private static OnlineDungeonCombatOptions Options(double handshakeSeconds = 5, double overallSeconds = 15)
    {
        OnlineDungeonCombatOptions options = CombatKit.Options(overallSeconds: overallSeconds);
        options.RoomHandshakeTimeout = TimeSpan.FromSeconds(handshakeSeconds);
        return options;
    }

    /// <summary>一次多房间战斗的装置:服务端替身、第一间的起始地图、假会话、跑器、日志收集器;可选"事件驱动的宿主"(进了新房间就排那个房间的击杀和去下一间的换房)。</summary>
    private sealed class Walk
    {
        private readonly Func<int, OnlineMoveRoomRequest> _autoMove;

        public Walk(FakeMultiRoomServer.RoomPlan[] plans, OnlineDungeonCombatOptions options = null,
            Func<int, OnlineMoveRoomRequest> autoMove = null, Func<byte[], byte[]> unwrap = null)
        {
            Plans = plans;
            _autoMove = autoMove;
            Server = new FakeMultiRoomServer(plans);
            Map = RoomKit.Map(plans[0].Ids, plans[0].CellX, plans[0].CellY, 905);
            Session = new CombatFakeSession { Respond = (cls, id, payload) => Server.Handle(cls, id, payload) };
            options = options ?? Options();
            if (autoMove != null)
            {
                Action<OnlineCombatEvent> inner = options.OnEvent;
                options.OnEvent = e =>
                {
                    inner?.Invoke(e);
                    if (e.Kind == OnlineCombatEventKind.RoomEntered)
                    {
                        PostRoom(e.Room);           // 在循环线程里、换表刚完成时排:最早也在下一圈、装载完成命令之后才会发出
                    }
                };
            }
            Runner = new OnlineDungeonCombatRunner(Session, unwrap, options);
        }

        public FakeMultiRoomServer.RoomPlan[] Plans { get; }

        public FakeMultiRoomServer Server { get; }

        public StartMapNotice Map { get; }

        public CombatFakeSession Session { get; }

        public OnlineDungeonCombatRunner Runner { get; }

        public HostTestKit.LogSink Log { get; } = new HostTestKit.LogSink();

        public OnlineDungeonCombat Machine => Runner.Machine;

        public Task<OnlineDungeonCombatResult> Run(CancellationToken cancellationToken = default)
        {
            return GatewayTestSupport.Bounded(Runner.RunAsync(Map, Log.Add, cancellationToken), 20);
        }

        /// <summary>排某个房间的全部击杀(绑定那个房间);带 autoMove 的装置再排去下一间的换房。全部收下返回 true。</summary>
        public bool PostRoom(int room)
        {
            bool accepted = PostKills(room);
            if (_autoMove != null && room < Plans.Length - 1)
            {
                accepted &= Runner.PostMoveRoom(room, _autoMove(room));
            }
            return accepted;
        }

        /// <summary>只排某个房间的全部击杀(绑定那个房间)。</summary>
        public bool PostKills(int room)
        {
            bool accepted = true;
            for (int i = 0; i < Plans[room].Ids.Length; i++)
            {
                accepted &= Runner.PostKill(room, i);
            }
            return accepted;
        }

        /// <summary>客户端发出的包号序列(顺带断言全是类 1 的命令)。</summary>
        public int[] Wire()
        {
            var sent = Session.Sent;
            Assert.All(sent, s => Assert.Equal(PacketClass.Cmd, s.Class));
            return sent.Select(s => (int)s.Id).ToArray();
        }

        /// <summary>等到进了第 <paramref name="room"/> 间且那一间的装载握手完成(命令发了、通知收了)。</summary>
        public Task WaitForRoomLoaded(int room, int loadReports)
        {
            return CombatKit.WaitUntil(
                () => Machine != null && Machine.Room == room && Machine.LoadReports == loadReports && Machine.LoadState == OnlineRoomLoadState.None,
                "进了第 " + room + " 间且装载握手完成");
        }
    }

    /// <summary>主链(见类注释),宿主在线程里按观察到的状态一步步排请求。返回装置与结果。</summary>
    private static async Task<(Walk Walk, OnlineDungeonCombatResult Result)> WalkTheMainChain(OnlineDungeonCombatOptions options = null)
    {
        var w = new Walk(TwoRooms(hiddenInFirst: true), options);
        w.Server.AllowGatePosition(901, 902);
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0, 901, 902)));
        Task<OnlineDungeonCombatResult> run = w.Run();

        // 第一间杀光,客户端的闸门开了,换房发出;服务端表里还留着只有它知道的一只:回错误码 20,留在原地
        await CombatKit.WaitUntil(() => w.Machine != null && w.Machine.MoveRefusals == 1, "换房被服务端拒绝");
        // 服务端那只只有它知道的怪没了(队友打死的),再换一次:成功
        Assert.Empty(w.Server.RemoveHidden());
        Assert.True(w.Runner.PostMoveRoom(RoomKit.Move(1, 0, 901, 902)));
        await w.WaitForRoomLoaded(1, 1);
        // 新表:本地编号重新从 0 起,宿主按新表排第二间的击杀
        Assert.True(w.PostKills(1));

        return (w, await run);
    }

    // ======== 主链 ========

    /// <summary>
    /// 主链端到端:客户端发出的命令序列、服务端视角的流水、各计数、换房命令逐字段都对;
    /// 被拒的换房不改任何东西(包括不发装载完成命令),成功的换房之后发一条装载完成命令,第二间的击杀报的是新表里的编号。
    /// </summary>
    [Fact]
    public async Task The_two_map_walk_runs_end_to_end_over_a_fake_session()
    {
        (Walk w, OnlineDungeonCombatResult r) = await WalkTheMainChain();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.True(r.ReturnReady);
        Assert.Equal(OnlineCombatPhase.Settled, r.Phase);
        Assert.Equal(1, r.Room);
        Assert.Equal(5, r.Monsters);                                       // 两间合计
        Assert.Equal((5, 5, 0, 0), (r.Sent, r.Confirmed, r.Refused, r.Blocked));
        Assert.Equal((2, 1, 1), (r.MovesSent, r.MoveRefusals, r.LoadReports));
        Assert.False(r.MoveInFlight);
        Assert.Equal(OnlineRoomLoadState.None, r.LoadState);
        Assert.Equal(0, r.OutOfOrder + r.Duplicates + r.UnknownNumbers + r.Malformed + r.PassthroughCount);

        // 客户端的命令序列:第一间三条击杀、换房(被拒)、换房(成功)、装载完成、第二间两条击杀、结算
        Assert.Equal(new[] { 42, 42, 42, 48, 48, 40, 42, 42, 49 }, w.Wire());
        // 服务端视角:换房被拒时服务端没动;只有成功的换房之后才收到装载完成;结算在清图通知之后、只一条
        Assert.Equal(
            new[]
            {
                "kill:9100", "kill:9101", "kill:9102", "move:r0:err20", "hidden-gone:r0", "roomclear:r0", "move:r0:ok:r1", "loaded:r1",
                "kill:9300", "kill:9301", "clear", "settle",
            },
            w.Server.Journal);
        Assert.Equal(new[] { 9100, 9101, 9102, 9300, 9301 }, w.Server.Kills.Select(k => (int)k.MobId).ToArray());   // 第二间用的是新表的编号
        Assert.Equal(0, w.Server.Refusals);
        Assert.Equal(0, w.Server.PrematureSettlements);
        Assert.Equal(1, w.Server.Settlements);
        Assert.Equal(1, w.Server.Room);
        Assert.False(w.Server.Disconnected);
        Assert.False(w.Server.Kicked);
        Assert.False(w.Server.LoadingPending);
        Assert.Equal(0, w.Server.IgnoredLoadReports);
        Assert.Equal(0, w.Server.Hack(FakeMultiRoomServer.HackTrapBits) + w.Server.Hack(FakeMultiRoomServer.HackSeatZero) + w.Server.Hack(FakeMultiRoomServer.HackPathPosition));

        // 两条换房命令逐字段:目标格、位置原样,其余取缺省(座位项全是"跳过"、游玩时间 0、末尾 0)
        MoveRoomView[] moves = w.Server.Moves;
        Assert.Equal(2, moves.Length);
        Assert.All(moves, v =>
        {
            Assert.Equal((1, 0), ((int)v.TargetX, (int)v.TargetY));
            Assert.Equal((901, 902), (v.PositionX, v.PositionY));
            Assert.Equal(0, v.Layered);
            Assert.Equal(0, v.TrapBits);
            Assert.Equal(new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF }, v.Seats);
            Assert.Equal(new uint[4], v.PlayTimes);
            Assert.Equal(0, v.Unused);
            Assert.Equal(0, v.Tail);
        });
        RoomLoadedView load = Assert.Single(w.Server.Loads);
        Assert.Equal((0, 0), (load.First, load.Second));

        // 换房之后新表整体换了:本地编号 0、1 对应第二间的服务端编号
        Assert.Equal(new[] { 9300, 9301 }, w.Machine.Monsters.Select(m => (int)m.ServerId).ToArray());
        Assert.All(w.Machine.Monsters, m => Assert.Equal(1, m.Room));
    }

    /// <summary>主链的事件链与日志行逐行核对:换房、被拒(带错误码)、换表(带房间怪数与载荷剩余字节)、装载握手、汇总行多出的一段;行里没有任何载荷里的编号。</summary>
    [Fact]
    public async Task The_two_map_walk_has_the_documented_event_chain_and_log_lines()
    {
        (Walk w, OnlineDungeonCombatResult r) = await WalkTheMainChain();

        Assert.Equal(
            "Spawned KillSent:0 KillSent:1 KillSent:2 KillConfirmed:0 KillConfirmed:1 KillConfirmed:2 " +
            "MoveSent:r0 MoveRefused:r0:code20 MoveSent:r0 RoomEntered:r1:m2 LoadedSent:r1 RoomLoaded:r1 " +
            "KillSent:0 KillSent:1 KillConfirmed:0 KillConfirmed:1 " +
            "MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(r.Events));
        Assert.Equal(
            new[]
            {
                Prefix + " begin monsters=3 duplicateNumbers=0",
                Prefix + " event=KillSent local=0 sent=1 confirmed=0 total=3",
                Prefix + " event=KillSent local=1 sent=2 confirmed=0 total=3",
                Prefix + " event=KillSent local=2 sent=3 confirmed=0 total=3",
                Prefix + " event=KillConfirmed local=0 sent=3 confirmed=1 total=3",
                Prefix + " event=KillConfirmed local=1 sent=3 confirmed=2 total=3",
                Prefix + " event=KillConfirmed local=2 sent=3 confirmed=3 total=3",
                Prefix + " event=RoomMoveSent room=0",
                Prefix + " event=RoomMoveRefused room=0 code=20",
                Prefix + " event=RoomMoveSent room=0",
                Prefix + " event=RoomEntered room=1 roomMonsters=2 trailing=0 total=5",
                Prefix + " event=RoomLoadedSent room=1",
                Prefix + " event=RoomLoaded room=1",
                Prefix + " event=KillSent local=0 sent=4 confirmed=3 total=5",
                Prefix + " event=KillSent local=1 sent=5 confirmed=3 total=5",
                Prefix + " event=KillConfirmed local=0 sent=5 confirmed=4 total=5",
                Prefix + " event=KillConfirmed local=1 sent=5 confirmed=5 total=5",
                Prefix + " event=MapCleared sent=5 confirmed=5 total=5",
                Prefix + " event=SettlementSent",
                Prefix + " event=SettlementArrived part=Result",
                Prefix + " event=SettlementArrived part=Reward",
                Prefix + " event=ReadyToReturnToTown",
                Prefix + " done outcome=ReturnReady phase=Settled monsters=5 sent=5 confirmed=5 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
                    "passthrough=0 heartbeats=0 envelopes=0/0 innerSkipped=0 frames=11 room=1 moves=2/1 loadReports=1 waiting=- passIds=-",
            },
            w.Log.Lines);
        // 日志里不能出现载荷里的数:服务端怪编号(910x、930x)与合成位置(901、902)
        Assert.All(w.Log.Lines, line =>
        {
            Assert.DoesNotContain("910", line);
            Assert.DoesNotContain("930", line);
            Assert.DoesNotContain("901", line);
            Assert.DoesNotContain("902", line);
        });
    }

    /// <summary>三间房一路走完,宿主是"事件驱动"的(进了新房间就在事件回调里排那个房间的击杀和去下一间的换房):每一间都在它的第一条击杀之前发装载完成命令。</summary>
    [Fact]
    public async Task A_three_room_walk_acknowledges_every_room_before_its_first_kill()
    {
        FakeMultiRoomServer.RoomPlan[] plans = ThreeRooms();
        var w = new Walk(plans, autoMove: room => RoomKit.Move(plans[room + 1].CellX, plans[room + 1].CellY, 901 + room, 911 + room));
        w.Server.AllowGatePosition(902, 912);                               // 第二次起换房的位置要在"过门位置"里(第一次豁免)
        Assert.True(w.PostRoom(0));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.Room);
        Assert.Equal(6, r.Monsters);
        Assert.Equal((6, 6, 0, 0), (r.Sent, r.Confirmed, r.Refused, r.Blocked));
        Assert.Equal((2, 0, 2), (r.MovesSent, r.MoveRefusals, r.LoadReports));
        // 每次换房之后紧跟一条装载完成命令,然后才是新房间的第一条击杀
        Assert.Equal(new[] { 42, 42, 48, 40, 42, 42, 42, 48, 40, 42, 49 }, w.Wire());
        Assert.Equal(
            new[]
            {
                "kill:9100", "kill:9101", "roomclear:r0", "move:r0:ok:r1", "loaded:r1",
                "kill:9300", "kill:9301", "kill:9302", "roomclear:r1", "move:r1:ok:r2", "loaded:r2",
                "kill:9500", "clear", "settle",
            },
            w.Server.Journal);
        Assert.Equal(2, w.Server.Loads.Length);
        Assert.Equal(0, w.Server.Refusals);
        Assert.Equal(0, w.Server.Hack(FakeMultiRoomServer.HackPathPosition));
        // 最后一间的装载超时定时器也停了:到点没人可踢
        w.Server.ExpireLoadingTimer();
        Assert.False(w.Server.Kicked);
        Assert.Equal(
            "Spawned KillSent:0 KillSent:1 KillConfirmed:0 KillConfirmed:1 MoveSent:r0 RoomEntered:r1:m3 LoadedSent:r1 " +
            "KillSent:0 KillSent:1 KillSent:2 RoomLoaded:r1 KillConfirmed:0 KillConfirmed:1 KillConfirmed:2 " +
            "MoveSent:r1 RoomEntered:r2:m1 LoadedSent:r2 KillSent:0 RoomLoaded:r2 KillConfirmed:0 " +
            "MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(r.Events));
    }

    // ======== 单图行为不变 ========

    /// <summary>只有一张图时(缺省选项):线上只有击杀与结算命令,没有换房、没有装载完成;汇总行与以前逐字节一样,不带房间那一段。</summary>
    [Fact]
    public async Task A_single_map_run_with_the_default_options_sends_neither_moves_nor_loaded_reports()
    {
        var w = new Walk(OneRoom(2));
        Assert.True(w.PostKills(0));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { 42, 42, 49 }, w.Wire());
        Assert.Empty(w.Server.Moves);
        Assert.Empty(w.Server.Loads);
        Assert.Equal((0, 0, 0), (r.Room, r.MovesSent, r.LoadReports));
        Assert.Equal(OnlineRoomLoadState.None, r.LoadState);
        Assert.Equal(
            Prefix + " done outcome=ReturnReady phase=Settled monsters=2 sent=2 confirmed=2 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
            "passthrough=0 heartbeats=0 envelopes=0/0 innerSkipped=0 frames=5 passIds=-",
            w.Log.Lines.Last());
    }

    /// <summary>没有换房命令在途时来的起始地图通知与装载完成通知(单图战斗里的迟到重复一类),只计数透传:不换表、不换房,链照样走完。</summary>
    [Fact]
    public async Task Unsolicited_room_notices_are_only_counted_in_a_single_map_run()
    {
        var w = new Walk(OneRoom(2));
        w.Session.Push(RoomKit.StartMap(new[] { 9300 }, 1, 0), RoomKit.Loaded());
        Assert.True(w.PostKills(0));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(0, r.Room);
        Assert.Equal(2, r.Monsters);
        Assert.Equal(2, r.PassthroughCount);
        Assert.Equal(new[] { "Noti/29:1", "Noti/30:1" }, r.Passthrough.Select(c => c.PacketClass + "/" + c.PacketId + ":" + c.Count).ToArray());
        Assert.Equal(new[] { 42, 42, 49 }, w.Wire());
        Assert.EndsWith(" passIds=NOTI/29:1,NOTI/30:1", w.Log.Lines.Last());
    }

    /// <summary>起始房间缺省不握手;选项打开后,装载完成命令是线上的第一条命令(排在宿主已排好的击杀之前),服务端的起始装载超时定时器也随之停了。</summary>
    [Fact]
    public async Task With_the_start_room_acknowledgement_on_the_loaded_report_is_the_first_command()
    {
        OnlineDungeonCombatOptions options = Options();
        options.AcknowledgeStartRoomLoaded = true;
        var w = new Walk(OneRoom(2), options);
        Assert.True(w.PostKills(0));                                        // 击杀在循环开始前就排好了

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { 40, 42, 42, 49 }, w.Wire());
        Assert.Equal(new[] { "loaded:r0", "kill:9100", "kill:9101", "clear", "settle" }, w.Server.Journal);
        Assert.False(w.Server.LoadingPending);
        Assert.Equal((0, 0, 1), (r.Room, r.MovesSent, r.LoadReports));
        Assert.Contains(Prefix + " event=RoomLoadedSent room=0", w.Log.Lines);
        Assert.Contains(Prefix + " event=RoomLoaded room=0", w.Log.Lines);
        Assert.Contains(" frames=6 room=0 moves=0/0 loadReports=1 waiting=- passIds=-", w.Log.Lines.Last());
    }

    // ======== 装载完成命令何时发:被踢模型 ========

    /// <summary>
    /// 服务端对"装载完成命令"的静态读码模型:换房成功起一个装载超时定时器,到点还没收到装载完成命令的成员被踢出。
    /// 默认选项下换房之后跑器自动发装载完成命令,定时器到点时已经没人可踢;关掉 <see cref="OnlineDungeonCombatOptions.AcknowledgeRoomLoaded"/> 就会被踢。
    /// </summary>
    [Fact]
    public async Task The_loaded_report_after_a_move_is_what_keeps_the_member_from_being_kicked()
    {
        // 默认:换房之后自动报装载完成
        var on = new Walk(TwoRooms());
        Assert.True(on.PostKills(0));
        Assert.True(on.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Task<OnlineDungeonCombatResult> onRun = on.Run();
        await on.WaitForRoomLoaded(1, 1);
        on.Server.ExpireLoadingTimer();
        Assert.False(on.Server.Kicked);
        Assert.False(on.Server.Disconnected);
        Assert.True(on.PostKills(1));
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, (await onRun).Outcome);

        // 关掉:换房之后不发装载完成命令,服务端的定时器到点就踢人
        OnlineDungeonCombatOptions options = Options(overallSeconds: 5);
        options.AcknowledgeRoomLoaded = false;
        var off = new Walk(TwoRooms(), options);
        Assert.True(off.PostKills(0));
        Assert.True(off.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Task<OnlineDungeonCombatResult> offRun = off.Run();
        await CombatKit.WaitUntil(() => off.Machine != null && off.Machine.Room == 1, "进了第二间");
        Assert.True(off.Server.LoadingPending);
        off.Server.ExpireLoadingTimer();
        Assert.True(off.Server.Kicked);
        Assert.True(off.Server.Disconnected);
        off.Session.Closed = true;                                          // 被踢 = 连接断了
        OnlineDungeonCombatResult kicked = await offRun;
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, kicked.Outcome);
        Assert.Equal(0, off.Session.Commands(40));
        Assert.Equal(0, kicked.LoadReports);
    }

    /// <summary>被拒的换房之后不发装载完成命令:整段走下来装载完成命令的条数 = 成功换房的次数。</summary>
    [Fact]
    public async Task Refused_moves_never_cause_a_loaded_report()
    {
        var options = Options(overallSeconds: 1.0);
        options.MaxMoveRefusalsPerRoom = 3;
        var w = new Walk(TwoRooms(hiddenInFirst: true), options);       // 只有服务端知道的那只一直在:换房一律被拒
        Assert.True(w.PostKills(0));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        }

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);        // 一直没清图:到整段上限收场
        Assert.Equal((3, 3, 0), (r.MovesSent, r.MoveRefusals, r.LoadReports));
        Assert.Equal(new[] { 42, 42, 42, 48, 48, 48 }, w.Wire());
        Assert.Equal(0, r.Room);
        Assert.Equal(0, w.Server.Room);
        Assert.Empty(w.Server.Loads);
        Assert.Equal(3, w.Server.Journal.Count(j => j == "move:r0:err20"));
    }

    // ======== 握手卡住与超时 ========

    /// <summary>装载完成通知一直不来:命令只发一次(不重发),跑器在握手上限到点时以超时收场,汇总行写明卡在"等装载完成通知"。</summary>
    [Fact]
    public async Task A_missing_loaded_notice_ends_the_run_with_a_timeout_that_names_the_step()
    {
        var w = new Walk(TwoRooms(), Options(handshakeSeconds: 0.4, overallSeconds: 8));       // 整段上限 8 秒:握手上限没生效的话要等到那时才收场
        w.Server.SwallowLoaded = true;
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Stopwatch watch = Stopwatch.StartNew();

        OnlineDungeonCombatResult r = await w.Run();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "握手上限 0.4 秒到点就该收场,实际用了 " + watch.Elapsed);
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.Equal(OnlineCombatPhase.Fighting, r.Phase);
        Assert.Equal(1, r.Room);
        Assert.Equal(OnlineRoomLoadState.AwaitingNotice, r.LoadState);
        Assert.False(r.MoveInFlight);
        Assert.Equal(1, r.LoadReports);
        Assert.Equal(1, w.Session.Commands(40));
        Assert.Equal(
            Prefix + " ended outcome=Timeout phase=Fighting monsters=5 sent=3 confirmed=3 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
            "passthrough=0 heartbeats=0 envelopes=0/0 innerSkipped=0 frames=4 room=1 moves=1/0 loadReports=1 waiting=AwaitingNotice passIds=-",
            w.Log.Lines.Last());
    }

    /// <summary>换房命令发出后服务端什么都不回:同样按握手上限收场,汇总行写明卡在"换房命令在途"。</summary>
    [Fact]
    public async Task A_move_nobody_answers_ends_the_run_with_a_timeout_that_names_the_step()
    {
        var w = new Walk(TwoRooms(), Options(handshakeSeconds: 0.4, overallSeconds: 8));
        w.Server.SwallowMoves = true;
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Stopwatch watch = Stopwatch.StartNew();

        OnlineDungeonCombatResult r = await w.Run();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "换房等回应的上限 0.4 秒到点就该收场,实际用了 " + watch.Elapsed);
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.True(r.MoveInFlight);
        Assert.Equal(0, r.Room);
        Assert.Equal((1, 0, 0), (r.MovesSent, r.MoveRefusals, r.LoadReports));
        Assert.Equal(
            Prefix + " ended outcome=Timeout phase=Fighting monsters=3 sent=3 confirmed=3 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
            "passthrough=0 heartbeats=0 envelopes=0/0 innerSkipped=0 frames=3 room=0 moves=1/0 loadReports=0 waiting=move passIds=-",
            w.Log.Lines.Last());
    }

    /// <summary>
    /// 握手没完成,下一次换房就不发(闸门要等装载完成通知):即使新房间的怪已经全被确认、换房请求早就排在队首。
    /// 装载完成通知补到之后,队首的换房才发出,整条链接着走完。
    /// </summary>
    [Fact]
    public async Task The_next_move_waits_for_the_loaded_notice()
    {
        FakeMultiRoomServer.RoomPlan[] plans = ThreeRooms();
        var w = new Walk(plans);
        w.Server.SwallowLoaded = true;
        w.Server.AllowGatePosition(0, 0);
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await CombatKit.WaitUntil(
            () => w.Machine.Room == 1 && w.Machine.LoadState == OnlineRoomLoadState.AwaitingNotice, "进了第二间、装载完成命令发了、通知没来");

        Assert.True(w.PostKills(1));
        Assert.True(w.Runner.PostMoveRoom(1, RoomKit.Move(1, 1)));
        await CombatKit.WaitUntil(() => w.Machine.Confirmed == 5, "第二间的怪全部确认");
        await Task.Delay(300);                                              // 十几个轮询周期:握手闸门漏的话早就发了
        Assert.Equal(1, w.Session.Commands(48));
        Assert.True(w.Machine.RoomCleared);
        Assert.Equal(OnlineCombatBlock.LoadPending, w.Machine.MoveGate(1));
        Assert.False(run.IsCompleted);

        w.Server.SwallowLoaded = false;                                     // 第三间的装载完成命令服务端要回通知
        w.Session.Push(RoomKit.Loaded());                                   // 第二间那条迟到的通知补上
        await w.WaitForRoomLoaded(2, 2);
        Assert.Equal(2, w.Session.Commands(48));
        Assert.True(w.PostKills(2));

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { 42, 42, 48, 40, 42, 42, 42, 48, 40, 42, 49 }, w.Wire());
        Assert.Equal(2, r.Room);
    }

    /// <summary>装载握手的等待期间服务端的心跳通知照样被回(跑器独占收发,不回的话服务端心跳计数会累加到断线),握手仍能完成。</summary>
    [Fact]
    public async Task Heartbeats_while_waiting_for_the_loaded_notice_are_answered()
    {
        var w = new Walk(TwoRooms());
        w.Server.SwallowLoaded = true;
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await CombatKit.WaitUntil(() => w.Machine.LoadState == OnlineRoomLoadState.AwaitingNotice, "等装载完成通知");

        w.Session.Push(CombatKit.Heartbeat(), CombatKit.Heartbeat());
        await CombatKit.WaitUntil(() => w.Session.Commands(0) == 2, "两条心跳都回了");
        Assert.Equal(OnlineRoomLoadState.AwaitingNotice, w.Machine.LoadState);
        w.Session.Push(RoomKit.Loaded());
        await w.WaitForRoomLoaded(1, 1);
        Assert.True(w.PostKills(1));

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.HeartbeatsAnswered);
    }

    // ======== 换房请求的排队 ========

    /// <summary>换房请求排得比击杀早:留在队首不发,直到当前房间的怪全部被服务端确认;只确认一部分时也不发。</summary>
    [Fact]
    public async Task A_move_posted_early_waits_in_the_queue_until_the_room_is_cleared()
    {
        var w = new Walk(TwoRooms());
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Assert.True(w.Runner.PostKill(0, 0));
        Assert.True(w.Runner.PostKill(0, 1));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await CombatKit.WaitUntil(() => w.Machine.Confirmed == 2, "两只怪确认了(第三只还活着)");

        await Task.Delay(300);
        Assert.Equal(0, w.Session.Commands(48));
        Assert.False(w.Machine.RoomCleared);

        Assert.True(w.Runner.PostKill(0, 2));
        await w.WaitForRoomLoaded(1, 1);
        Assert.Equal(1, w.Session.Commands(48));
        Assert.True(w.PostKills(1));

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { 42, 42, 42, 48, 40, 42, 42, 49 }, w.Wire());
    }

    /// <summary>
    /// 同一个房间排了两次换房:第一条成功换了房之后,第二条(绑定的是旧房间)被状态机拦下丢掉(记一条 StaleRoom 的拦截事件),不会误发到新房间。
    /// 不绑定房间的击杀则按交给状态机那一刻的当前房间解释:换房之后排的,报的是新表的编号。
    /// </summary>
    [Fact]
    public async Task A_second_move_bound_to_the_old_room_is_dropped_once_the_first_one_has_succeeded()
    {
        var w = new Walk(TwoRooms());
        Assert.True(w.PostKills(0));
        OnlineMoveRoomRequest move = RoomKit.Move(1, 0);
        Assert.True(w.Runner.PostMoveRoom(0, move));
        Assert.True(w.Runner.PostMoveRoom(0, move));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await w.WaitForRoomLoaded(1, 1);
        await CombatKit.WaitUntil(() => w.Machine.Blocked == 1, "第二条换房被拦下");

        Assert.True(w.Runner.PostKill(0, 1));                               // 绑定旧房间的击杀:拦下,新房间的怪不受影响
        await CombatKit.WaitUntil(() => w.Machine.Blocked == 2, "旧房间的击杀被拦下");
        Assert.True(w.Runner.PostKill(0));                                  // 不绑定:按当前房间(第二间)的表,本地 0 号 = 9300
        Assert.True(w.Runner.PostKill(1, 1));

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(new[] { 42, 42, 42, 48, 40, 42, 42, 49 }, w.Wire());
        Assert.Equal(new[] { 9100, 9101, 9102, 9300, 9301 }, w.Server.Kills.Select(k => (int)k.MobId).ToArray());
        Assert.Equal(2, r.Blocked);
        Assert.Equal(
            "Spawned KillSent:0 KillSent:1 KillSent:2 KillConfirmed:0 KillConfirmed:1 KillConfirmed:2 " +
            "MoveSent:r0 RoomEntered:r1:m2 LoadedSent:r1 MoveBlocked:r1:StaleRoom RoomLoaded:r1 KillBlocked:1:StaleRoom " +
            "KillSent:0 KillSent:1 KillConfirmed:0 KillConfirmed:1 " +
            "MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(r.Events));
    }

    /// <summary>
    /// 不带房间序号的换房请求绑定在"排请求那一刻状态机所在的房间":在第二间里排的,就是第二间去第三间的换房(而不是被当成第一间的旧请求丢掉)。
    /// </summary>
    [Fact]
    public async Task An_unbound_move_binds_to_the_room_the_machine_is_in_when_it_is_posted()
    {
        FakeMultiRoomServer.RoomPlan[] plans = ThreeRooms();
        var w = new Walk(plans);
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(RoomKit.Move(1, 0)));                 // 还在第一间:绑 0
        Task<OnlineDungeonCombatResult> run = w.Run();
        await w.WaitForRoomLoaded(1, 1);

        Assert.True(w.PostKills(1));
        Assert.True(w.Runner.PostMoveRoom(RoomKit.Move(1, 1)));                 // 已经在第二间:绑 1
        await w.WaitForRoomLoaded(2, 2);
        Assert.True(w.PostKills(2));

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.Room);
        Assert.Equal(0, r.Blocked);
        Assert.Equal(new[] { 42, 42, 48, 40, 42, 42, 42, 48, 40, 42, 49 }, w.Wire());
        Assert.Equal(new[] { (1, 0), (1, 1) }, w.Server.Moves.Select(v => ((int)v.TargetX, (int)v.TargetY)).ToArray());
    }

    /// <summary>
    /// 同一个房间排了两条目标不同的换房(第一条的目标格不是相邻格,服务端回错误码 21;第二条才对):
    /// 第一条在途的时候,第二条留在队首等着(不是被当成"已有一条在途"丢掉),第一条被拒之后它才发出,换房成功。线上顺序:两条换房命令、一条装载完成命令。
    /// </summary>
    [Fact]
    public async Task A_second_move_for_the_same_room_waits_for_the_first_one_and_goes_out_after_its_refusal()
    {
        var w = new Walk(TwoRooms());
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(3, 2)));              // 不是相邻格:服务端回 21
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));              // 对的那条
        Task<OnlineDungeonCombatResult> run = w.Run();
        await w.WaitForRoomLoaded(1, 1);
        Assert.True(w.PostKills(1));

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal((2, 1), (r.MovesSent, r.MoveRefusals));
        Assert.Equal(0, r.Blocked);
        Assert.Equal(1, r.Room);
        Assert.Equal(new[] { 42, 42, 42, 48, 48, 40, 42, 42, 49 }, w.Wire());
        Assert.Equal(new[] { (3, 2), (1, 0) }, w.Server.Moves.Select(v => ((int)v.TargetX, (int)v.TargetY)).ToArray());
        Assert.Contains(Prefix + " event=RoomMoveRefused room=0 code=21", w.Log.Lines);
        Assert.Contains("MoveSent:r0 MoveRefused:r0:code21 MoveSent:r0 RoomEntered:r1:m2 LoadedSent:r1 RoomLoaded:r1", CombatKit.Trace(r.Events));
    }

    /// <summary>
    /// 拒绝额度按房间算:这一间的换房被拒满额度之后,再排的换房请求被丢掉(记 MoveBudgetExhausted 的拦截事件),不再发到线上;
    /// 线上只有额度内的几条换房命令,整段以超时收场(没有清图)。
    /// </summary>
    [Fact]
    public async Task The_refusal_budget_stops_the_runner_from_asking_again_and_again()
    {
        OnlineDungeonCombatOptions options = Options(overallSeconds: 1.0);
        options.MaxMoveRefusalsPerRoom = 2;
        var w = new Walk(TwoRooms(hiddenInFirst: true), options);
        Assert.True(w.PostKills(0));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        }

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.Equal((2, 2), (r.MovesSent, r.MoveRefusals));
        Assert.Equal(new[] { 42, 42, 42, 48, 48 }, w.Wire());
        Assert.Equal(1, r.Blocked);
        Assert.Equal(
            "Spawned KillSent:0 KillSent:1 KillSent:2 KillConfirmed:0 KillConfirmed:1 KillConfirmed:2 " +
            "MoveSent:r0 MoveRefused:r0:code20 MoveSent:r0 MoveRefused:r0:code20 MoveBlocked:r0:MoveBudgetExhausted",
            CombatKit.Trace(r.Events));
        Assert.Contains(Prefix + " event=RoomMoveBlocked room=0 block=MoveBudgetExhausted", w.Log.Lines);
    }

    /// <summary>
    /// 最后一间里也排了换房(宿主不知道它是最后一间):最后一只确认之后清图通知紧跟着到,换房要么被服务端以"副本已通关"(19)拒绝,要么在清图通知先到时被本地拦下;
    /// 两种情形结算都照常、只发一条,不会因为这条多余的换房出岔子。
    /// </summary>
    [Fact]
    public async Task A_move_posted_in_the_last_room_cannot_derail_the_settlement()
    {
        var w = new Walk(OneRoom(1));
        Assert.True(w.Runner.PostKill(0, 0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(1, w.Server.Settlements);
        Assert.Equal(0, w.Server.PrematureSettlements);
        Assert.Equal(1, w.Session.Commands(49));
        Assert.Equal(r.MovesSent, r.MoveRefusals);                          // 发出去的(0 条或 1 条)都被拒了,没有哪条被当成成功
        Assert.Equal(0, r.Room);
        Assert.Equal(0, w.Server.Room);
        Assert.Equal(0, r.LoadReports);
        Assert.False(w.Server.Disconnected);
        if (r.MovesSent == 1)
        {
            Assert.Contains(Prefix + " event=RoomMoveRefused room=0 code=19", w.Log.Lines);
        }
    }

    // ======== 帧层与异常 ========

    /// <summary>换房命令帧层失败:跑器以发送失败收场,没有换房、没有进入在途状态,计一次拦下。</summary>
    [Fact]
    public async Task A_move_frame_failure_ends_the_run_with_a_send_failure()
    {
        var w = new Walk(TwoRooms());
        w.Session.StatusOfSend = index => index == 3 ? FrameStatus.EncryptFailed : FrameStatus.Ok;     // 第 0~2 条击杀,第 3 条是换房
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, r.Outcome);
        Assert.Equal((0, 1), (r.MovesSent, r.Blocked));
        Assert.False(r.MoveInFlight);
        Assert.Equal(0, r.Room);
        Assert.Equal(new[] { 42, 42, 42, 48 }, w.Wire());
        Assert.Empty(w.Server.Moves);
    }

    /// <summary>装载完成命令帧层失败:跑器以发送失败收场,状态停在"该发了"。</summary>
    [Fact]
    public async Task A_loaded_report_frame_failure_ends_the_run_with_a_send_failure()
    {
        var w = new Walk(TwoRooms());
        w.Session.StatusOfSend = index => index == 4 ? FrameStatus.EncryptFailed : FrameStatus.Ok;     // 第 4 条是装载完成命令
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, r.Outcome);
        Assert.Equal(1, r.Room);
        Assert.Equal((0, 1), (r.LoadReports, r.Blocked));
        Assert.Equal(OnlineRoomLoadState.ReportDue, r.LoadState);
        Assert.EndsWith(" room=1 moves=1/0 loadReports=0 waiting=ReportDue passIds=-", w.Log.Lines.Last());
    }

    /// <summary>发换房命令时抛异常:战斗以 Faulted 收场,汇总行与结果里只有异常类型名,没有异常消息。</summary>
    [Fact]
    public async Task A_send_exception_during_a_move_becomes_a_fault_and_only_the_type_name_is_logged()
    {
        var w = new Walk(TwoRooms());
        w.Session.FailureOfSend = index => index == 3 ? new IOException("secret-detail-12345") : null;
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.Faulted, r.Outcome);
        Assert.Equal("IOException", r.FaultType);
        Assert.Equal(0, r.MovesSent);
        Assert.False(r.MoveInFlight);
        Assert.DoesNotContain("secret", string.Join("\n", w.Log.Lines));
        Assert.Contains(" exception=IOException ", w.Log.Lines.Last());
    }

    // ======== 信封与新房间的起始地图 ========

    /// <summary>
    /// 服务端的通知全装进真信封(笔记 E-387:起始地图通知经信封到达)、内层副本跟在后面:新房间的起始地图通知与装载完成通知照样被拆开处理,
    /// 副本只数一次;换房失败回包(类 1)直接到,不进信封。
    /// </summary>
    [Fact]
    public async Task Room_notices_inside_envelopes_are_opened_and_the_walk_still_completes()
    {
        var book = new DungeonEntryEnvelopeBook();
        int tag = 0;
        int envelopes = 0;
        var w = new Walk(TwoRooms(hiddenInFirst: true), unwrap: book.Unwrap);
        w.Session.Respond = (cls, id, payload) =>
        {
            foreach (CombatKit.ServerFrame frame in w.Server.Handle(cls, id, payload))
            {
                if (frame.Class == CombatKit.Noti)
                {
                    byte[] sealedBody = book.Seal(DungeonEntryWire.Inner(frame.Class, frame.Id, frame.Payload), ++tag);
                    w.Session.PushUnit(DungeonEntryWire.Decoded(CombatKit.Noti, 561, sealedBody), CombatKit.DecodedPlain(frame.Class, frame.Id, frame.Payload));
                    envelopes++;
                }
                else
                {
                    w.Session.Push(frame);
                }
            }
            return null;
        };
        w.Server.AllowGatePosition(901, 902);
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0, 901, 902)));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await CombatKit.WaitUntil(() => w.Machine.MoveRefusals == 1, "换房被拒");
        w.Server.RemoveHidden();
        Assert.True(w.Runner.PostMoveRoom(RoomKit.Move(1, 0, 901, 902)));
        await w.WaitForRoomLoaded(1, 1);
        Assert.True(w.PostKills(1));

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(1, r.Room);
        Assert.Equal(new[] { 42, 42, 42, 48, 48, 40, 42, 42, 49 }, w.Wire());
        Assert.Equal(envelopes, r.EnvelopesOpened);
        Assert.Equal(0, r.EnvelopesUnopened);
        Assert.Equal(envelopes, r.InnerCopiesSkipped);
        Assert.Equal(0, r.Duplicates + r.OutOfOrder + r.UnknownNumbers + r.Malformed);
        Assert.Equal(envelopes, r.PassthroughCount);                        // 信封帧本身记透传;内层副本被跳过
        Assert.Equal(5 + 1 + 1 + 1 + 2, envelopes);                         // 5 条确认、起始地图、装载完成、清图、结果与奖励
    }

    /// <summary>新房间的起始地图通知后面带多余字节:照样换表,多出的字节数写进 RoomEntered 的日志行(trailing=,给真服对账怪记录步长用)。</summary>
    [Fact]
    public async Task Trailing_bytes_of_the_new_start_map_are_counted_in_the_room_entered_line()
    {
        var w = new Walk(TwoRooms());
        w.Session.Respond = (cls, id, payload) =>
        {
            var replies = new List<CombatKit.ServerFrame>();
            foreach (CombatKit.ServerFrame frame in w.Server.Handle(cls, id, payload))
            {
                replies.Add(frame.Class == CombatKit.Noti && frame.Id == 29
                    ? new CombatKit.ServerFrame(frame.Class, frame.Id, frame.Payload.Concat(new byte[5]).ToArray())
                    : frame);
            }
            return replies;
        };
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await w.WaitForRoomLoaded(1, 1);
        Assert.True(w.PostKills(1));

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Contains(Prefix + " event=RoomEntered room=1 roomMonsters=2 trailing=5 total=5", w.Log.Lines);
    }

    // ======== 服务端防作弊计数的建模 ========

    /// <summary>
    /// 位置的建模(审查发现):<c>CheckMoveMapFitInPathHackCnt</c> 在换房之前就查位置,第一次豁免,第二次起位置不在过门位置里记 0x1f7 计数(只计数,换房照样成功)。
    /// 缺省位置 (0,0) 不保证不计数;宿主给的位置是过门位置才不计。这条测试固定的是"位置确实原样带到了服务端",计数规则本身是建模。
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task The_path_position_reaches_the_server_and_is_counted_from_the_second_move_on(bool gatePositions, int expectedCount)
    {
        FakeMultiRoomServer.RoomPlan[] plans = ThreeRooms();
        var w = new Walk(plans, autoMove: room => RoomKit.Move(plans[room + 1].CellX, plans[room + 1].CellY));      // 缺省位置 (0,0)
        if (gatePositions)
        {
            w.Server.AllowGatePosition(0, 0);
        }
        Assert.True(w.PostRoom(0));

        OnlineDungeonCombatResult r = await w.Run();

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.Equal(2, r.MovesSent);
        Assert.Equal(expectedCount, w.Server.Hack(FakeMultiRoomServer.HackPathPosition));
        Assert.All(w.Server.Moves, v => Assert.Equal((0, 0), (v.PositionX, v.PositionY)));
        Assert.False(w.Server.Disconnected);
    }

    // ======== 选项与接口 ========

    [Fact]
    public void The_constructor_refuses_unusable_room_options()
    {
        var session = new CombatFakeSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { RoomHandshakeTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { RoomHandshakeTimeout = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { RoomHandshakeTimeout = TimeSpan.FromDays(40) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = -3 }));
        Assert.NotNull(new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions()));       // 缺省值本身是合法的
    }

    /// <summary>缺省选项:握手总开关开、起始房间不握手、拒绝额度 3、握手上限 10 秒。</summary>
    [Fact]
    public void The_room_options_have_the_documented_defaults()
    {
        var options = new OnlineDungeonCombatOptions();

        Assert.True(options.AcknowledgeRoomLoaded);
        Assert.False(options.AcknowledgeStartRoomLoaded);
        Assert.Equal(3, options.MaxMoveRefusalsPerRoom);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RoomHandshakeTimeout);
        Assert.Null(options.MovePayload);
        Assert.Null(options.LoadedPayload);
    }

    [Fact]
    public async Task The_post_methods_validate_their_arguments_and_stop_taking_requests_with_the_run()
    {
        var w = new Walk(OneRoom(1));

        Assert.Throws<ArgumentNullException>(() => w.Runner.PostMoveRoom(null));
        Assert.Throws<ArgumentNullException>(() => w.Runner.PostMoveRoom(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Runner.PostMoveRoom(-1, RoomKit.Move(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Runner.PostKill(-1, 0));
        Assert.True(w.Runner.PostKill(0, 0));

        await w.Run();

        Assert.False(w.Runner.PostKill(0, 0));
        Assert.False(w.Runner.PostKill(1, 0));
        Assert.False(w.Runner.PostMoveRoom(RoomKit.Move(1, 0)));
        Assert.False(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
    }

    /// <summary>自定的换房载荷工厂经跑器也生效:座位项、游玩时间、末尾都带到服务端;长度不对时跑器以故障收场(不发包)。</summary>
    [Fact]
    public async Task The_move_payload_factory_reaches_the_server_through_the_runner()
    {
        OnlineDungeonCombatOptions options = Options();
        options.MovePayload = request => OnlineCombatPayloads.BuildMoveRoom(request.TargetX, request.TargetY, request.PositionX, request.PositionY,
            request.Layered, 7 * 3, new ushort[] { 911, 912, 913, 914 }, new uint[] { 1, 2, 3, 4 }, 931, 941);
        var w = new Walk(TwoRooms(), options);
        Assert.True(w.PostKills(0));
        Assert.True(w.Runner.PostMoveRoom(0, RoomKit.Move(1, 0, 901, 902, layered: true)));
        Task<OnlineDungeonCombatResult> run = w.Run();
        await w.WaitForRoomLoaded(1, 1);
        Assert.True(w.PostKills(1));
        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, (await run).Outcome);

        MoveRoomView v = Assert.Single(w.Server.Moves);
        Assert.Equal((1, 0, 901, 902), ((int)v.TargetX, (int)v.TargetY, v.PositionX, v.PositionY));
        Assert.Equal(1, v.Layered);
        Assert.Equal(21, v.TrapBits);
        Assert.Equal(new ushort[] { 911, 912, 913, 914 }, v.Seats);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, v.PlayTimes);
        Assert.Equal((931, 941), ((int)v.Unused, v.Tail));

        var bad = Options();
        bad.MovePayload = request => new byte[42];
        var broken = new Walk(TwoRooms(), bad);
        Assert.True(broken.PostKills(0));
        Assert.True(broken.Runner.PostMoveRoom(0, RoomKit.Move(1, 0)));
        OnlineDungeonCombatResult faulted = await broken.Run();
        Assert.Equal(OnlineDungeonCombatOutcome.Faulted, faulted.Outcome);
        Assert.Equal("InvalidOperationException", faulted.FaultType);
        Assert.Equal(0, broken.Session.Commands(48));
    }
}
