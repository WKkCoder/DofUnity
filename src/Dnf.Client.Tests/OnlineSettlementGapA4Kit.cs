using Dnf.Game;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第四轮:结算开始中途异常安全"(OnlineSettlementGapA4*)共用夹具。
/// 修的是 <c>OnlineSettlementController.OnSettlementStarted</c> 一进来就置 <c>ready.HoldForMenu = true</c>、中途抛异常被流程控制器吞掉之后,
/// 流程仍"挂起等结算"而结算控制器却是空闲(永久留在副本)的软锁。
/// 这里放:日志行字面量(手算)、几种合成异常、可以在"订阅传输/问是否断线"这两处抛异常的假传输、
/// 以及一个带<b>真</b>联网进副本控制器 + <b>真</b>结算控制器、回城计时可调的场景台 <see cref="FaultRig"/>(从 <see cref="SettleRig"/> 复制并加了几处可观察钩子)。
/// 全部是合成数据(编号 9101/9501 等),不含任何真实数值。
/// </summary>
internal static class OnlineSettlementGapA4Kit
{
    /// <summary>结算日志前缀(手写的字面量,不从生产代码取)。</summary>
    public const string SettlePrefix = "DNF_ONLINE stage=settle";

    /// <summary>合成异常的消息文本:日志里绝不能出现它(日志只许写异常的类型名)。</summary>
    public const string SecretMessage = "start-fault-secret-message-4417";

    /// <summary>结算开始中途失败时控制器写的唯一一行:令牌 start_fault,步骤只放异常的类型名(不含命名空间、消息与数值)。</summary>
    public static string StartFault(string exceptionTypeName)
    {
        return "DNF_ONLINE stage=settle event=fail reason=start_fault step=" + exceptionTypeName;
    }

    // ----- 标准成功路径(一只怪)上,展示之后才抛异常的那几个点之前已经写出的行(数值同 A2 全流程用例的手算:掉落 2、差异 4) -----
    public const string ShowStandard = "DNF_ONLINE stage=settle event=show timedout=false result=Succeeded reward=Succeeded drops=2 diffs=4";
    public const string DiffClearTime = "DNF_ONLINE stage=settle event=diff field=clear_time";
    public const string DiffKills = "DNF_ONLINE stage=settle event=diff field=kills";
    public const string DiffBaseExp = "DNF_ONLINE stage=settle event=diff field=base_exp";
    public const string DiffRankBonusExp = "DNF_ONLINE stage=settle event=diff field=rank_bonus_exp";

    /// <summary>展示行 + 四行差异行(结算摘要已构造完、展示日志已写出之后才抛异常的点,这五行已经在日志里)。</summary>
    public static readonly string[] ShowAndDiffs = { ShowStandard, DiffClearTime, DiffKills, DiffBaseExp, DiffRankBonusExp };

    /// <summary>控制器真的把"展示"一步走完之后推进的第一行(标准路径无故障时紧跟差异行)。</summary>
    public const string Sent72 = OnlineSettlementGapA2Kit.Sent72;

    /// <summary>"没有故障"时一个合成的自定义异常(直接继承 Exception,证明 catch 不是只认某几种常见类型)。</summary>
    internal sealed class SyntheticStartFault : Exception
    {
        public SyntheticStartFault() : base(SecretMessage)
        {
        }
    }

    /// <summary>按类型名造一个合成异常;类型名既是造异常的依据,也是 InlineData 里写死的期望字面量(用例不从被抛对象取期望)。</summary>
    public static Exception MakeFault(string typeName)
    {
        switch (typeName)
        {
            case "InvalidOperationException":
                return new InvalidOperationException(SecretMessage);
            case "NotSupportedException":
                return new NotSupportedException(SecretMessage);
            case "NullReferenceException":
                return new NullReferenceException(SecretMessage);
            case "SyntheticStartFault":
                return new SyntheticStartFault();
            default:
                throw new ArgumentException("未知的合成异常类型名: " + typeName, nameof(typeName));
        }
    }

    /// <summary>会在 <c>OnSettlementStarted</c> 里被调用、又能注入异常的各个点(按方法里的先后排)。</summary>
    internal enum StartFaultPoint
    {
        GetCurrentTownPlace,
        GetProgress,
        GetSelfUniqueId,
        LocalResultComputer,
        GetPartySlot,

        /// <summary>摘要构造、展示日志、绘制钩子都已就位之后,问宿主要传输。</summary>
        TransportFactory,

        /// <summary>传输已挂上 FrameReceived 之后,挂 Disconnected 时抛。</summary>
        TransportDisconnectedSubscribe,

        /// <summary>传输已全部挂好、方法即将分阶段时,问"是否已断线"抛。</summary>
        TransportIsDisconnected,
    }

    /// <summary>
    /// 一个可以在"挂 Disconnected 事件"或"读 IsDisconnected"时抛异常的结算传输替身;订阅数按委托真实个数算(重复退订不会算成负数)。
    /// </summary>
    internal sealed class FaultTransport : IOnlineSettlementTransport
    {
        private readonly Func<Exception> _onDisconnectedSubscribe;
        private readonly Func<Exception> _onIsDisconnected;
        private Action<PacketClass, int, byte[]> _frameReceived;
        private Action<OnlineDisconnectReason> _disconnected;

        public FaultTransport(Func<Exception> onDisconnectedSubscribe = null, Func<Exception> onIsDisconnected = null)
        {
            _onDisconnectedSubscribe = onDisconnectedSubscribe;
            _onIsDisconnected = onIsDisconnected;
        }

        public bool Disposed { get; private set; }
        public int SentCount { get; private set; }
        public int FrameSubscribers => _frameReceived == null ? 0 : _frameReceived.GetInvocationList().Length;
        public int DisconnectSubscribers => _disconnected == null ? 0 : _disconnected.GetInvocationList().Length;

        public bool IsDisconnected
        {
            get
            {
                if (_onIsDisconnected != null)
                {
                    throw _onIsDisconnected();
                }
                return false;
            }
        }

        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add { _frameReceived += value; }
            remove { _frameReceived -= value; }
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add
            {
                if (_onDisconnectedSubscribe != null)
                {
                    throw _onDisconnectedSubscribe();
                }
                _disconnected += value;
            }
            remove { _disconnected -= value; }
        }

        public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken)
        {
            SentCount++;
            return Task.FromResult(FrameStatus.Ok);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>
    /// 把假端口摆成"杀光就结算"的标准联网副本(与 <see cref="OnlineSettlementGapAKit.WireStandardDungeonResponder"/> 同一套回包,只是直接收端口):
    /// 进副本两步按标准回包;CMD 49 回 0x22 + 0x23(<paramref name="omitSettlementB"/> 为真时只回 0x22,让战斗在等 0x23 时超时);其余命令交给战斗假服务端。
    /// 每次"开始游戏"都重新造一份假战斗服务端,所以能连进两次副本。
    /// </summary>
    public static void WireStandardResponder(FakeDungeonPort port, int monsterCount = 1, bool omitSettlementB = false)
    {
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

    /// <summary>
    /// 带真联网进副本控制器 + 真结算控制器的场景台(从 <see cref="SettleRig"/> 复制):
    /// ① 房间局的"结算完成后再等多久回城"可调(<see cref="ReturnDelayMs"/>,虚拟时间),用来看清"默认结算退出"到底在什么时刻回城;
    /// ② 结算控制器订阅之后再挂一个<b>探针订阅者</b>——事件按订阅顺序依次调用,前一个订阅者抛异常会让后面的收不到,
    ///    所以探针被调到就直接证明"异常没有传出结算控制器";探针还记下交接对象,供用例读 <c>HoldForMenu</c>;
    /// ③ <see cref="Step"/> 记下房间局"打完"(<see cref="HostedRoomRun.Finished"/>)的那一圈的虚拟时刻,用来精确推算默认回城时刻。
    /// 时间全走逻辑时钟(每圈默认 20 毫秒)。
    /// </summary>
    internal sealed class FaultRig : IDisposable
    {
        /// <summary>
        /// 房间局打完之后再等多久回城(毫秒,虚拟时间)。取 30000:比 <see cref="PumpUntil"/> 的总圈数上限(1500 圈 × 20 毫秒)还长,
        /// 保证"结算开始的那一圈"一定早于这一局自己的回城计时到点(与线程调度快慢无关);生产缺省是 3 秒,夹具不取生产常量。
        /// </summary>
        public const long ReturnDelayMs = 30_000;

        public HostedGatedRoomFixture Fixture { get; } = new HostedGatedRoomFixture();
        public RecordingTownWorld RawWorld { get; }
        public OnlineSettlementTownWorld TownWorld { get; }
        public GameFlow Flow { get; }
        public FakeDungeonPort Port { get; } = new FakeDungeonPort();
        public FakeSettlementTransport Transport { get; } = new FakeSettlementTransport();
        public OnlineDungeonFlowController DungeonCtrl { get; }
        public OnlineSettlementController SettleCtrl { get; }
        public HeroProgress Progress { get; }
        public List<string> Logs { get; } = new List<string>();
        public List<OnlineSettlementReturnTarget> AppliedReturnTargets { get; } = new List<OnlineSettlementReturnTarget>();
        public int Disconnects { get; private set; }

        /// <summary>探针订阅者最近一次收到的交接对象(还没触发是 null)。</summary>
        public OnlineDungeonSettlementReady Ready { get; private set; }

        /// <summary>探针订阅者被调用的次数(排在结算控制器之后订阅,被调到说明结算控制器的处理函数没把异常传出来)。</summary>
        public int ProbeCalls { get; private set; }

        /// <summary>当前这一局"打完"的那一圈结束时的虚拟时刻;还没打完是 -1(进下一局副本时清回 -1)。</summary>
        public long FinishedAtMs { get; private set; } = -1;

        public long NowMs { get; private set; } = 10000; // 故意从非零时间起点开始

        public FaultRig(Action<OnlineSettlementOptions> configureSettle = null, Action<OnlineDungeonFlowOptions> configureDungeon = null)
        {
            RawWorld = new RecordingTownWorld(new TownPlace(1, 3), new TownPlace(1, 1, TownEntry.Return));
            TownWorld = new OnlineSettlementTownWorld(RawWorld);

            var permissions = new OnlineDungeonPermissionTable();
            permissions.LoadInitial(new[]
            {
                new KeyValuePair<int, byte>(DungeonAId, 1),
                new KeyValuePair<int, byte>(DungeonBId, 1),
            }, hasSnapshot: true);

            var entries = new[]
            {
                new DungeonEntry("dungeon/test/dunga.dgn", "DUNGA", new DungeonCard(DungeonAId, 100, 100, 0, null, 0, 1)),
                new DungeonEntry("dungeon/test/dungb.dgn", "DUNGB", new DungeonCard(DungeonBId, 200, 200, 0, null, 0, 1)),
            };

            Flow = new GameFlow(TownWorld, (_, _) => throw new InvalidOperationException("offline factory unused"), entries);
            OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(permissions, hasLocalData: _ => true, hasUltimate: _ => true);

            var expTable = new ExpTable(new[] { 1000, 3000, 6000, 12000, 25000, 50000 });
            var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50, 60 });
            Progress = new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, level: 1);

            var dungeonOpts = new OnlineDungeonFlowOptions
            {
                CombatOptions = CombatKit.Options(pollMilliseconds: 10),
                ReturnDelayMs = ReturnDelayMs,
                Log = l => { lock (Logs) Logs.Add(l); },
                OnDisconnected = () => Disconnects++,
            };
            configureDungeon?.Invoke(dungeonOpts);

            DungeonCtrl = new OnlineDungeonFlowController(Port, Flow, policy, ctx =>
            {
                int monsters = ctx.StartMap.Monsters.Count;
                var room = new OnlineDungeonRoom(ctx.CombatRunner);
                RoomMonsterSpawn[] spawns = Enumerable.Range(0, monsters)
                    .Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320 + 40 * i))
                    .ToArray();
                HostedRoomRun run = room.CreateRun(plan => Fixture.Create(spawns, plan, Array.Empty<GateSide>()), returnDelayMs: ReturnDelayMs);
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

            // 探针:排在结算控制器之后订阅。结算控制器的处理函数若把异常传出来,多播委托就不会再调到这里。
            DungeonCtrl.SettlementStarted += ready =>
            {
                ProbeCalls++;
                Ready = ready;
            };
        }

        public void EnterTownAndDungeon()
        {
            FinishedAtMs = -1;
            Flow.EnterTown("boot");
            SettleCtrl.Poll(NowMs, 20); // 记录进副本前的城镇区域
            Flow.EnterSelect("gate");
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

        /// <summary>逻辑时钟前进一圈:房间局先走(可能就此"打完"或到回城计时)、再轮询联网进副本控制器、结算控制器与流程。</summary>
        public void Step(long advanceMs = 20)
        {
            NowMs += advanceMs;
            Flow.Session?.Update(advanceMs, 0, 0);
            Flow.Splash?.Advance(advanceMs);
            Flow.Select?.Advance(advanceMs);
            if (FinishedAtMs < 0 && Flow.Run is HostedRoomRun run && run.Finished)
            {
                FinishedAtMs = NowMs;
            }
            DungeonCtrl.Poll(NowMs);
            SettleCtrl.Poll(NowMs, advanceMs);
            Flow.Poll();
        }

        /// <summary>每圈推进 20 毫秒虚拟时间并轮询,直到条件成立(上限 1500 圈;后台任务靠每圈让出 2 毫秒时间片推进——有上限的轮询,条件一成立就返回,不是固定等待)。</summary>
        public void PumpUntil(Func<bool> predicate, int maxIterations = 1500)
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
                Thread.Sleep(2);
            }
            Assert.True(predicate(), "Timed out; flow=" + Flow.Stage + " dungeonPhase=" + DungeonCtrl.Phase + " settlePhase=" + SettleCtrl.Phase);
        }

        /// <summary>只推进虚拟时间(没有后台任务要等,不让出时间片),直到流程回到城镇;返回回到城镇那一圈的虚拟时刻。</summary>
        public long StepUntilTown(int maxSteps = 2000)
        {
            for (int i = 0; i < maxSteps && Flow.Stage != GameStage.Town; i++)
            {
                Step(20);
            }
            Assert.Equal(GameStage.Town, Flow.Stage);
            return NowMs;
        }

        public string[] LogSnapshot()
        {
            lock (Logs)
            {
                return Logs.ToArray();
            }
        }

        /// <summary>日志里所有结算控制器写的行(前缀 <see cref="SettlePrefix"/>),按写出顺序。</summary>
        public string[] SettleLines() => LogSnapshot().Where(l => l.StartsWith(SettlePrefix, StringComparison.Ordinal)).ToArray();

        public void Dispose()
        {
            SettleCtrl.Dispose();
            DungeonCtrl.Dispose();
            Fixture.Dispose();
        }
    }
}
