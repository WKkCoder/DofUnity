using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第四轮补测(OnlineDungeonFlowGapR4*)共用的小工具:直接复用上一轮的 <see cref="OnlineDungeonFlowGapKit"/>(场景台、假端口、载荷构造),
/// 这里只补三样——"杀光就结算"的假服务端装配、杀光本局全部本地怪、几处日志与历史的取值。全部是合成数据,不含任何真实数值。
/// </summary>
internal static class OnlineDungeonFlowGapR4Kit
{
    public const string P = "DNF_ONLINE stage=dungeon ";

    /// <summary>流程历史里每条记录的原因,按先后。</summary>
    public static string[] Reasons(GameFlow flow)
    {
        return flow.History.Select(h => h.Reason).ToArray();
    }

    /// <summary>
    /// 进副本成功、单房间(<paramref name="monsters"/> 只怪全对上)的三行起手日志;难度取选择屏缺省的 0,副本 A 的档位上限是 3。
    /// </summary>
    public static string[] EnteredLogs(int monsters = 2)
    {
        string n = monsters.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new[]
        {
            P + "event=select outcome=enter difficulty=0 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=0",
            P + "event=room status=Ready multi=off spawned=" + n + " expected=" + n + " rooms=1",
        };
    }

    /// <summary>
    /// 把场景台摆成"能杀光、能结算的单房间副本":入口两步命令按标准回包,结算命令(1/49)回标准的结算帧(0x22 + 0x23),
    /// 其余命令交给战斗假服务端(<paramref name="monsters"/> 只怪;清图后它自己回通关通知);建房工厂造一间单房间(合成房间夹具,怪数同)。
    /// </summary>
    public static void ServeSettlementDungeon(Rig rig, int monsters = 2, double returnDelayMs = 0.0)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsters));
        rig.Port.SetResponder((cls, id, payload) =>
        {
            CombatKit.ServerFrame[] entry = EntryReply(cls, id, DungeonAId, monsters);
            if (entry != null)
            {
                return entry;
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                return SettlementFrames();
            }
            return combatServer.Handle(cls, id, payload);
        });
        rig.RoomBuilder = ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(monsters), plan, Array.Empty<GateSide>()), returnDelayMs);
            return OnlineDungeonRoomBuildResult.Success(room, run);
        };
    }

    /// <summary>杀光本局当前房间里的全部本地怪(之后房间每步会把击杀上报给战斗跑器)。</summary>
    public static void KillAll(Rig rig)
    {
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        foreach (MonsterActor monster in run.Room.Monsters.ToArray())
        {
            HostedRoomFixture.Kill(monster);
        }
    }

    /// <summary>服务端对放弃回城(1/45)的回包:0/23 用户所在区域 + 0/24 区域用户(都落在 1 号城镇 1 号区域)。</summary>
    public static IEnumerable<CombatKit.ServerFrame> TownFrames(byte village = 1, byte area = 1)
    {
        return new[]
        {
            new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(village, area)),
            new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(village, area)),
        };
    }

    /// <summary>进副本:选择屏确认 → 轮询到进了副本(<see cref="OnlineDungeonFlowPhase.InDungeon"/>)。</summary>
    public static void EnterDungeon(Rig rig)
    {
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
    }

    // ------------------------------------------------------------------------------------------------
    // 探针场景台(Rig4):与上一轮的 Rig 同构,只把假端口换成能"看得见收帧窗口、能让发送返回帧层失败、能给出真会话对象"的探针端口,
    // 并多了"造流程时给副本加载画面"的开关。上一轮的 Rig 与假端口不可继承、字段又是私有的,所以只能复制一份。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 包住一个 <see cref="ScriptedNetSession"/> 的会话:记下每次收帧的窗口,并可按发送序号让发送返回指定的帧层状态(这时不真发,但命令照记)。
    /// </summary>
    internal sealed class ProbeNet : IOnlineDungeonSession
    {
        private readonly ScriptedNetSession _inner;
        private readonly ProbePort _port;
        private int _sends;

        public ProbeNet(ScriptedNetSession inner, ProbePort port)
        {
            _inner = inner;
            _port = port;
        }

        /// <summary>被包住的接缝会话(测试要关对端、切帧出错时用)。</summary>
        public ScriptedNetSession Inner => _inner;

        public bool IsReady => _inner.IsReady;

        public bool PeerClosed => _inner.PeerClosed;

        public FrameError LastFrameError => _inner.LastFrameError;

        public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _sends) - 1;
            FrameStatus forced = _port.SendStatus(index);
            if (forced != FrameStatus.Ok)
            {
                _port.RecordCommand(packetClass, packetId, payload ?? Array.Empty<byte>());
                return Task.FromResult(forced);
            }
            return _inner.SendAsync(packetClass, packetId, payload, cancellationToken);
        }

        public Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
        {
            _port.RecordWindow(window);
            return _inner.ReceiveAsync(window, stopAfterFrames, cancellationToken);
        }
    }

    /// <summary>
    /// 探针端口:功能同上一轮的 <see cref="FakeSessionPort"/>(记命令、可替换进入/离开独占收包的行为、可手动触发断线),
    /// 另外能给出 <see cref="RawSession"/>、记下每次收帧窗口(<see cref="ReceiveWindows"/>)、让第 n 次发送返回帧层失败(<see cref="SendStatus"/>)。
    /// </summary>
    internal sealed class ProbePort : IOnlineDungeonSessionPort
    {
        private readonly object _sync = new object();
        private readonly List<(PacketClass Class, int PacketId, byte[] Payload)> _commands = new List<(PacketClass, int, byte[])>();
        private readonly List<TimeSpan> _windows = new List<TimeSpan>();
        private Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> _responder;
        private Action<PacketClass, int, byte[]> _frameReceived;
        private Action<OnlineDisconnectReason> _disconnected;
        private ProbeNet _currentNet;

        public OnlineClientSession RawSession { get; set; }

        public bool IsDisconnected { get; set; }

        public int EnterExclusiveCalls { get; private set; }

        public int LeaveExclusiveCalls { get; private set; }

        /// <summary>替换"进入独占收包"的行为;null 是缺省:令牌已取消就抛,否则立刻完成。</summary>
        public Func<CancellationToken, Task> EnterExclusiveBehavior { get; set; }

        /// <summary>替换"离开独占收包"的行为;null 是缺省:立刻完成并计数。</summary>
        public Func<CancellationToken, Task> LeaveExclusiveBehavior { get; set; }

        /// <summary>第 n 次发送(从 0 起,按每个会话各自计)返回的帧层状态;缺省全是 Ok。</summary>
        public Func<int, FrameStatus> SendStatus { get; set; } = _ => FrameStatus.Ok;

        /// <summary>最近一次造出来的跑器所用的会话(还没造过是 null)。</summary>
        public ProbeNet CurrentNet => _currentNet;

        /// <summary>收到的每条命令(含被强制返回帧层失败的),按先后。</summary>
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

        /// <summary>战斗循环每次收帧用的窗口,按先后。</summary>
        public TimeSpan[] ReceiveWindows
        {
            get
            {
                lock (_sync)
                {
                    return _windows.ToArray();
                }
            }
        }

        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add => _frameReceived += value;
            remove => _frameReceived -= value;
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add => _disconnected += value;
            remove => _disconnected -= value;
        }

        public void SetResponder(Func<PacketClass, int, byte[], IEnumerable<CombatKit.ServerFrame>> responder)
        {
            _responder = responder;
        }

        public void RecordCommand(PacketClass cls, int id, byte[] payload)
        {
            lock (_sync)
            {
                _commands.Add((cls, id, payload));
            }
        }

        public void RecordWindow(TimeSpan window)
        {
            lock (_sync)
            {
                _windows.Add(window);
            }
        }

        public Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            if (EnterExclusiveBehavior != null)
            {
                return EnterExclusiveBehavior(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnterExclusiveCalls++;
            return Task.CompletedTask;
        }

        public Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            if (LeaveExclusiveBehavior != null)
            {
                return LeaveExclusiveBehavior(cancellationToken);
            }
            LeaveExclusiveCalls++;
            return Task.CompletedTask;
        }

        public OnlineDungeonRunner CreateDungeonRunner()
        {
            var inner = new ScriptedNetSession((cls, id, payload) =>
            {
                RecordCommand(cls, id, payload);
                return _responder != null ? _responder(cls, id, payload) : Array.Empty<CombatKit.ServerFrame>();
            });
            _currentNet = new ProbeNet(inner, this);
            return new OnlineDungeonRunner(_currentNet, keepAlive: null);
        }

        /// <summary>与真断线一致:置断线标志、关接缝会话的对端、再触发 Disconnected 事件。</summary>
        public void TriggerDisconnect(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            IsDisconnected = true;
            _currentNet?.Inner.ClosePeer();
            _disconnected?.Invoke(reason);
        }

        /// <summary>只触发 Disconnected 事件(不置断线标志、不关会话)。</summary>
        public void RaiseDisconnectedEventOnly(OnlineDisconnectReason reason = OnlineDisconnectReason.PeerClosed)
        {
            _disconnected?.Invoke(reason);
        }
    }

    /// <summary>探针场景台:GameFlow(副本 A 档位 3、副本 B 档位 1)+ 探针端口 + 控制器 + 日志捕获 + 逻辑时钟 <see cref="NowMs"/>;用法同上一轮的 <see cref="Rig"/>。</summary>
    internal sealed class Rig4 : IDisposable
    {
        private readonly List<string> _logs = new List<string>();
        private HostedGatedRoomFixture _fixture;

        public GameFlow Flow { get; }
        public TestTownWorld World { get; }
        public DungeonEntry[] Entries { get; }
        public OnlineDungeonPermissionTable Table { get; }
        public OnlineDungeonSelectPolicy Policy { get; }
        public ProbePort Port { get; } = new ProbePort();
        public OnlineDungeonFlowOptions Options { get; }
        public OnlineDungeonFlowController Ctrl { get; }

        /// <summary>逻辑时钟(毫秒):<see cref="PumpUntil"/> 每圈加 20。</summary>
        public long NowMs;

        /// <summary><see cref="OnlineDungeonFlowOptions.OnDisconnected"/> 被调用的次数。</summary>
        public int Disconnects;

        /// <summary>建房工厂的调用次数。</summary>
        public int BuilderCalls;

        /// <summary>可换的建房工厂;缺省 <c>Failure(NoRoom)</c>。</summary>
        public Func<OnlineDungeonRoomBuildContext, OnlineDungeonRoomBuildResult> RoomBuilder =
            _ => OnlineDungeonRoomBuildResult.Failure(OnlineRoomStatus.NoRoom);

        /// <summary>最近一次建房上下文(建房工厂被调用过才有)。</summary>
        public OnlineDungeonRoomBuildContext LastContext;

        /// <summary>最近一次由 <c>ServeRoomDungeon</c>/<c>ServeSettlementDungeon</c> 的建房工厂造出的房间。</summary>
        public OnlineDungeonRoom LastRoom;

        public HostedGatedRoomFixture Fixture => _fixture ??= new HostedGatedRoomFixture();

        public Rig4(
            Action<OnlineDungeonFlowOptions> configure = null,
            Func<string, SplashScene> dungeonSplash = null,
            OnlineClientSession rawSession = null)
        {
            Table = new OnlineDungeonPermissionTable();
            Table.LoadInitial(new[]
            {
                new KeyValuePair<int, byte>(DungeonAId, 3),
                new KeyValuePair<int, byte>(DungeonBId, 1),
            }, hasSnapshot: true);

            Entries = new[]
            {
                new DungeonEntry("dungeon/test/dunga.dgn", "DUNGA", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
                new DungeonEntry("dungeon/test/dungb.dgn", "DUNGB", new DungeonCard(DungeonBId, 200, 200, 0, null, 0, 1)),
            };

            World = new TestTownWorld(new TownPlace(1, 0), new TownPlace(ReturnTownId, ReturnAreaId));
            Flow = new GameFlow(World, (path, _) => throw new InvalidOperationException("offline factory should not run"), Entries, null, dungeonSplash);
            Policy = OnlineDungeonSelectPolicy.Create(Table, hasLocalData: _ => true, hasUltimate: _ => true);
            Port.RawSession = rawSession;

            Options = new OnlineDungeonFlowOptions
            {
                CombatOptions = CombatKit.Options(pollMilliseconds: 10),
                LoadingNoticeTimeout = TimeSpan.FromMilliseconds(70),
                DungeonInfoTimeout = TimeSpan.FromMilliseconds(70),
                StartMapTimeout = TimeSpan.FromMilliseconds(70),
                Log = line => { lock (_logs) _logs.Add(line); },
                OnDisconnected = () => Interlocked.Increment(ref Disconnects),
            };
            configure?.Invoke(Options);

            Ctrl = new OnlineDungeonFlowController(Port, Flow, Policy, ctx =>
            {
                Interlocked.Increment(ref BuilderCalls);
                LastContext = ctx;
                return RoomBuilder(ctx);
            }, Options);
        }

        /// <summary>日志快照(只含控制器的 <c>DNF_ONLINE stage=dungeon</c> 行,按写入顺序)。</summary>
        public string[] Logs()
        {
            lock (_logs)
            {
                return _logs.ToArray();
            }
        }

        /// <summary>城镇 → 副本选择屏。</summary>
        public void Start()
        {
            Flow.EnterTown("boot");
            Flow.EnterSelect("gate");
        }

        /// <summary>选择屏上确认当前选中的副本并让流程接手。</summary>
        public void ConfirmSelected()
        {
            Flow.Select.Confirm();
            Assert.True(Flow.Poll());
        }

        /// <summary>逻辑时钟每圈前进 20ms,轮询控制器与流程,直到条件成立(同上一轮 <see cref="Rig.PumpUntil"/>)。</summary>
        public void PumpUntil(Func<bool> condition, int maxIterations = 4000)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (condition())
                {
                    return;
                }
                NowMs += 20;
                Flow.Session?.Update(20, 0, 0);
                Flow.Splash?.Advance(20);
                Flow.Select?.Advance(20);
                Ctrl.Poll(NowMs);
                Flow.Poll();
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.True(condition(), "Timed out waiting for online dungeon flow state; stage=" + Flow.Stage + " phase=" + Ctrl.Phase);
        }

        /// <summary>同 <see cref="PumpUntil"/>,但只轮询控制器(不推进房间与流程)。</summary>
        public void PumpControllerUntil(Func<bool> condition, int maxIterations = 4000)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (condition())
                {
                    return;
                }
                NowMs += 20;
                Ctrl.Poll(NowMs);
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.True(condition(), "Timed out waiting for online dungeon controller state; stage=" + Flow.Stage + " phase=" + Ctrl.Phase);
        }

        /// <summary>等到控制器回到 Idle。</summary>
        public void PumpUntilIdle()
        {
            PumpUntil(() => Ctrl.Phase == OnlineDungeonFlowPhase.Idle);
        }

        public void Dispose()
        {
            Ctrl.Dispose();
            _fixture?.Dispose();
        }
    }

    /// <summary>同 <see cref="ServeRoomDungeon(Rig, int, Func{byte[], IEnumerable{CombatKit.ServerFrame}})"/>,探针场景台版。</summary>
    public static void ServeRoomDungeon(Rig4 rig, int monsters = 2, Func<byte[], IEnumerable<CombatKit.ServerFrame>> onGiveUp = null)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsters));
        rig.Port.SetResponder((cls, id, payload) =>
        {
            CombatKit.ServerFrame[] entry = EntryReply(cls, id, DungeonAId, monsters);
            if (entry != null)
            {
                return entry;
            }
            if (cls == PacketClass.Cmd && id == SessionLifecyclePayloads.GiveUpGamePacketId)
            {
                return onGiveUp != null ? onGiveUp(payload) : Array.Empty<CombatKit.ServerFrame>();
            }
            return combatServer.Handle(cls, id, payload);
        });
        rig.RoomBuilder = ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            rig.LastRoom = room;
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(monsters), plan, Array.Empty<GateSide>()));
            return OnlineDungeonRoomBuildResult.Success(room, run);
        };
    }

    /// <summary>同 <see cref="ServeSettlementDungeon(Rig, int, double)"/>,探针场景台版。</summary>
    public static void ServeSettlementDungeon(Rig4 rig, int monsters = 2, double returnDelayMs = 0.0)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsters));
        rig.Port.SetResponder((cls, id, payload) =>
        {
            CombatKit.ServerFrame[] entry = EntryReply(cls, id, DungeonAId, monsters);
            if (entry != null)
            {
                return entry;
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                return SettlementFrames();
            }
            return combatServer.Handle(cls, id, payload);
        });
        rig.RoomBuilder = ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            rig.LastRoom = room;
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(monsters), plan, Array.Empty<GateSide>()), returnDelayMs);
            return OnlineDungeonRoomBuildResult.Success(room, run);
        };
    }

    /// <summary>同 <see cref="KillAll(Rig)"/>,探针场景台版。</summary>
    public static void KillAll(Rig4 rig)
    {
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        foreach (MonsterActor monster in run.Room.Monsters.ToArray())
        {
            HostedRoomFixture.Kill(monster);
        }
    }

    /// <summary>同 <see cref="EnterDungeon(Rig)"/>,探针场景台版。</summary>
    public static void EnterDungeon(Rig4 rig)
    {
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
    }
}
