using System.Reflection;
using Dnf.Data;
using Dnf.Game;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// r20fix-e2e D:无人值守驱动 <see cref="OnlineAutoDriver.StepGame"/> 里"联网进副本握手在途(<see cref="OnlineDungeonFlowPhase.Entering"/>)"那一段的确定性测试。
/// 这一段是为一个只在真机慢握手时才出现的竞态补的:再次挑战(Retry)发出之后,联网进副本控制器同步进入 Entering,
/// 但流程这时可能还停在旧局的 <see cref="GameStage.Dungeon"/>,或者(旧局的"回城"被流程读到之后)已经被切回 <see cref="GameStage.Town"/>;
/// 驱动必须在整段握手期间静待——不判失败、不重新发起选图、不去推进旧局的房间自动机、不写结果行——握手完成后接着把全程跑到 <c>result=ok stage=done</c>。
/// <para>
/// 这里不用真网络也不用墙钟闸门:真 <see cref="GameFlow"/>(不装转场图)、真 <see cref="OnlineDungeonFlowController"/>(假端口,进入独占收包那一步停在一把
/// 任务完成源上,握手被"按住"在 Entering)、真 <see cref="OnlineSettlementController"/>(假传输)、真 <see cref="HostedRoomBot"/> 驱动的联网房间局,
/// 每帧的调用顺序与宿主(<c>TownDemo.StepFlow</c>)一致:进副本控制器 → 结算控制器 → 驱动 → 流程。全部是合成数据。
/// </para>
/// <para>
/// 用例 3 起用同一套接线补几处只有接上真控制器才走得到的驱动行为:流程离开副本阶段后不留旧局房间自动机、结算选"选择副本"后在选图页重新确认、
/// 城镇里进副本控制器带着提示时的判法、房间自动机拿到的最短清图时间与整段上限、同一局里一直推进同一个房间自动机。
/// </para>
/// </summary>
public sealed class OnlineAutoDriverEnteringGapTests
{
    private const long FrameMs = 20;

    // ------------------------------------------------------------------ 夹具

    /// <summary>
    /// 把"联网进副本控制器 + 结算控制器 + 驱动"按宿主顺序串在一起的场景台。握手能被 <see cref="HoldNextEntry"/> 按住:
    /// 下一次"进入独占收包"停在一把任务完成源上,直到 <see cref="ReleaseEntry"/> 才继续——所以 Entering 持续多久完全由测试决定,不靠睡眠。
    /// </summary>
    private sealed class EnteringRig : IDisposable
    {
        private readonly HostedGatedRoomFixture _fixture = new HostedGatedRoomFixture();
        private readonly object _logSync = new object();
        private readonly List<string> _driverLines = new List<string>();
        private TaskCompletionSource<bool> _gate;

        public GameFlow Flow { get; }
        public OnlineDungeonFlowGapKit.FakeSessionPort Port { get; } = new OnlineDungeonFlowGapKit.FakeSessionPort();
        public FakeSettlementTransport Transport { get; } = new FakeSettlementTransport();
        public OnlineDungeonFlowController DungeonCtrl { get; }
        public OnlineSettlementController SettleCtrl { get; }
        public OnlineAutoDriver Driver { get; }

        /// <summary>按建房先后记下的每一局(第 0 项是第一局)。</summary>
        public List<HostedRoomRun> Runs { get; } = new List<HostedRoomRun>();

        /// <summary>逻辑时钟(毫秒):每帧加 <see cref="FrameMs"/>。故意从非零起点开始。</summary>
        public long NowMs { get; private set; } = 10000;

        public EnteringRig(OnlineAutoOptions options, int monsterCount = 1)
        {
            var rawWorld = new RecordingTownWorld(new TownPlace(1, 3), new TownPlace(1, 1, TownEntry.Return));
            var townWorld = new OnlineSettlementTownWorld(rawWorld);

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
            Flow = new GameFlow(townWorld, (_, _) => throw new InvalidOperationException("offline factory unused"), entries);
            OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(permissions, hasLocalData: _ => true, hasUltimate: _ => true);

            var expTable = new ExpTable(new[] { 1000, 3000, 6000, 12000, 25000, 50000 });
            var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50, 60 });
            var progress = new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, level: 1);

            var dungeonOptions = new OnlineDungeonFlowOptions
            {
                CombatOptions = CombatKit.Options(pollMilliseconds: 10),
                ReturnDelayMs = 20,
            };
            DungeonCtrl = new OnlineDungeonFlowController(Port, Flow, policy, ctx =>
            {
                int monsters = ctx.StartMap.Monsters.Count;
                var room = new OnlineDungeonRoom(ctx.CombatRunner);
                RoomMonsterSpawn[] spawns = Enumerable.Range(0, monsters)
                    .Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320 + 40 * i))
                    .ToArray();
                HostedRoomRun run = room.CreateRun(plan => _fixture.Create(spawns, plan, Array.Empty<GateSide>()), returnDelayMs: 20);
                Runs.Add(run);
                return OnlineDungeonRoomBuildResult.Success(room, run!);
            }, dungeonOptions);

            var settleOptions = new OnlineSettlementOptions
            {
                MenuCountdownMs = 5000,
                AdvanceReplyTimeoutMs = 300,
                EplpReplyTimeoutMs = 300,
                ReturnWaitTimeoutMs = 300,
                TownReturnWaitTimeoutMs = 300,
                GetProgress = () => progress,
                GetSelfUniqueId = () => SelfUid,
                GetPartySlot = () => 0,
                ApplyReturnTarget = townWorld.ArmReturn,
                TransportFactory = _ => Transport,
                LocalResultComputer = (data, stats, prog) =>
                {
                    var thresholds = new[] { 90, 80, 70, 60, 50, 40, 30, 20 };
                    var steps = new[] { new TimeBonusStep(0, 1), new TimeBonusStep(10000, 2), new TimeBonusStep(20000, 3) };
                    var averages = new[] { (DungeonAId, 1, new[] { 100000, 110000, 120000, 130000, 140000 }) };
                    return DungeonResult.Compute(stats, new RankSystem(thresholds, steps, averages), prog, data.DungeonId, 1, data.Difficulty, 1f);
                },
            };
            SettleCtrl = new OnlineSettlementController(Flow, settleOptions);
            SettleCtrl.Attach(DungeonCtrl);

            Driver = new OnlineAutoDriver(options, line =>
            {
                lock (_logSync)
                {
                    _driverLines.Add(line);
                }
            });

            WireServer(monsterCount);
        }

        /// <summary>驱动自己写出的日志行(只会有结果行)。</summary>
        public string[] DriverLines
        {
            get
            {
                lock (_logSync)
                {
                    return _driverLines.ToArray();
                }
            }
        }

        /// <summary>
        /// 假服务端:进副本两步按标准回包;CMD 49(通关上报)回 0x22 + 0x23;其余命令交给战斗假服务端。
        /// 结算传输:72 回 [1]、73 回翻牌权、1/75 回执行确认并推 0/2(回城信号)——不论选的是再次挑战还是回城。
        /// </summary>
        private void WireServer(int monsterCount)
        {
            var combatServer = new FakeCombatServer(CombatKit.Sequential(monsterCount));
            Port.SetResponder((cls, id, payload) =>
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
                        new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, ValidSettlementABytes()),
                        new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, ValidSettlementBBytes()),
                    };
                }
                return combatServer.Handle(cls, id, payload);
            });
            AutoAdvance(Transport, CardRightBytes(1), payload =>
            {
                Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
                PushNoti2(Transport);
            });
        }

        /// <summary>开局:城镇 → 记下进副本前的城镇区域 → 打开选图界面(驱动接手确认第 0 项)。</summary>
        public void StartAtDungeonSelect()
        {
            Flow.EnterTown("boot");
            SettleCtrl.Poll(NowMs, FrameMs);
            Flow.EnterSelect("gate");
        }

        /// <summary>按住下一次进入:它的"进入独占收包"会停在闸门上,直到 <see cref="ReleaseEntry"/>。必须在对应的进入被请求之前调用。</summary>
        public void HoldNextEntry()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gate = gate;
            Port.EnterExclusiveBehavior = ct =>
            {
                Port.CountEnterExclusive();
                return gate.Task.WaitAsync(ct);
            };
        }

        /// <summary>放开按住的进入(之后的进入不再被拦)。</summary>
        public void ReleaseEntry()
        {
            TaskCompletionSource<bool> gate = _gate;
            Port.EnterExclusiveBehavior = null;
            gate?.TrySetResult(true);
        }

        /// <summary>一帧:宿主顺序——进副本控制器、结算控制器、驱动、(驱动没收场时)流程。</summary>
        public void Frame()
        {
            NowMs += FrameMs;
            DungeonCtrl.Poll(NowMs);
            SettleCtrl.Poll(NowMs, FrameMs);
            Driver.StepGame(Flow, DungeonCtrl, SettleCtrl, FrameMs, NowMs);
            if (!Driver.Finished)
            {
                Flow.Poll();
            }
        }

        /// <summary>每圈走一帧(后台异步靠短暂让出时间片推进,只是等它完成,不靠它排顺序),直到条件成立;超过圈数就断言失败。</summary>
        public void PumpUntil(Func<bool> condition, string what, int maxFrames = 3000)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (condition())
                {
                    return;
                }
                Frame();
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.Fail("等待“" + what + "”超时:flow=" + Flow.Stage + " dungeonPhase=" + DungeonCtrl.Phase + " settlePhase=" + SettleCtrl.Phase
                + " driverStage=" + Driver.Stage + " finished=" + Driver.Finished + " result=" + Driver.ResultLine);
        }

        public void Dispose()
        {
            ReleaseEntry();
            SettleCtrl.Dispose();
            DungeonCtrl.Dispose();
            _fixture.Dispose();
        }
    }

    private static OnlineAutoOptions RetryOnce() => new OnlineAutoOptions(settlementChoices: new[] { SettlementChoice.Retry });

    /// <summary>握手在途的这一帧里驱动该有的样子:静待。逐项断言(每一项都对应一种"误判"):不收场、没有结果行、阶段名是 loading、没有房间自动机。</summary>
    private static void AssertDriverIsWaiting(EnteringRig rig, string when)
    {
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.DungeonCtrl.Phase);
        Assert.False(rig.Driver.Finished, when + ":驱动不该收场");
        Assert.False(rig.Driver.Succeeded, when + ":驱动不该报成功");
        Assert.Null(rig.Driver.ResultLine);
        Assert.Empty(rig.DriverLines);
        Assert.Equal("loading", rig.Driver.Stage);
        Assert.Null(rig.Driver.ActiveRoomBot);
        Assert.Equal(0, rig.Driver.ExitCode);
    }

    // ------------------------------------------------------------------ 用例 1:Entering 期间流程停在城镇

    [Fact]
    public void A_handshake_held_at_entering_while_the_flow_sits_in_town_keeps_the_driver_waiting_and_the_run_still_ends_ok()
    {
        // 首次进图:驱动在选图页确认第 0 项 → 控制器同步进入 Entering(握手被按住)→ 流程被切回城镇(模拟旧局的回城被流程读到)。
        // 此时驱动若落进"城镇"分支会重新发起选图(把还在途的进图搅乱);必须静待,握手放开后接着打到 result=ok stage=done。
        using var rig = new EnteringRig(new OnlineAutoOptions());
        rig.StartAtDungeonSelect();
        rig.HoldNextEntry();

        // 这一帧里驱动先确认选图、流程随后才发起进入,所以进入 Entering 的那一帧驱动还没见到它;再走一帧才是驱动在 Entering 期间的第一帧(流程仍在选图页)
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Entering, "驱动确认选图并进入 Entering");
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        rig.Frame();
        AssertDriverIsWaiting(rig, "选图页上握手在途");
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Runs);

        rig.Flow.EnterTown("synthetic stale return");
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        for (int i = 0; i < 5; i++)
        {
            rig.Frame();
            AssertDriverIsWaiting(rig, "城镇里握手在途,第 " + i + " 帧");
            Assert.Equal(GameStage.Town, rig.Flow.Stage);
            Assert.Equal(0, rig.Driver.CompletedSettlements);
        }
        Assert.Equal(0, rig.Flow.Runs);

        rig.ReleaseEntry();
        rig.PumpUntil(() => rig.Driver.Finished, "握手完成后驱动打完全程");

        Assert.True(rig.Driver.Succeeded);
        Assert.Equal(0, rig.Driver.ExitCode);
        Assert.Equal("done", rig.Driver.Stage);
        string expected = "DNF_ONLINE result=ok stage=done elapsedMs=" + rig.NowMs;
        Assert.Equal(expected, rig.Driver.ResultLine);
        Assert.Equal(new[] { expected }, rig.DriverLines);
        Assert.Equal(1, rig.Driver.CompletedSettlements);
        Assert.Equal(0, rig.Driver.RetryCycles);
        Assert.Equal(1, rig.Flow.Runs);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
    }

    [Fact]
    public void A_retry_whose_handshake_is_held_while_the_flow_was_forced_back_to_town_does_not_fail_and_the_second_run_ends_ok()
    {
        // 真机竞态的原样复现:再次挑战之后 _expectedTownReturn 为 false,驱动一旦在握手在途时看到城镇就会判 fail stage=dungeon。
        using var rig = new EnteringRig(RetryOnce());
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Dungeon && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon, "第一局开始");
        rig.HoldNextEntry();

        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Entering, "第一次结算选再次挑战并进入第二次 Entering");
        Assert.Equal(1, rig.Driver.RetryCycles);
        Assert.Equal(1, rig.Driver.CompletedSettlements);

        rig.Flow.EnterTown("synthetic stale return of the first run");
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        for (int i = 0; i < 5; i++)
        {
            rig.Frame();
            AssertDriverIsWaiting(rig, "再次挑战后城镇里握手在途,第 " + i + " 帧");
            Assert.Equal(GameStage.Town, rig.Flow.Stage);
            Assert.Equal(1, rig.Driver.RetryCycles);
        }
        Assert.Equal(1, rig.Flow.Runs);

        rig.ReleaseEntry();
        rig.PumpUntil(() => rig.Driver.Finished, "握手完成后驱动打完第二局");

        Assert.True(rig.Driver.Succeeded);
        Assert.Equal("done", rig.Driver.Stage);
        string expected = "DNF_ONLINE result=ok stage=done elapsedMs=" + rig.NowMs;
        Assert.Equal(expected, rig.Driver.ResultLine);
        Assert.Equal(new[] { expected }, rig.DriverLines);
        Assert.Equal(2, rig.Driver.CompletedSettlements);
        Assert.Equal(1, rig.Driver.RetryCycles);
        Assert.Equal(2, rig.Flow.Runs);
        Assert.Equal(2, rig.Runs.Count);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
    }

    // ------------------------------------------------------------------ 用例 2:Entering 期间流程仍停在旧局的副本阶段

    [Fact]
    public void After_a_retry_the_old_run_stays_untouched_while_the_next_handshake_is_held_and_the_second_run_ends_ok()
    {
        // 再次挑战发出后,流程还停在旧局的 Dungeon 阶段、旧局的房间局还在:驱动要把旧局的房间自动机丢掉、静待,
        // 而且不能再去推进旧局的房间局(推进一帧旧局就会"收尾"并在回城延迟之后选回城,把流程带回城镇)。
        using var rig = new EnteringRig(RetryOnce());
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Dungeon && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon, "第一局开始");
        HostedRoomRun firstRun = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Assert.Same(firstRun, rig.Runs[0]);
        rig.HoldNextEntry();

        // 先走到第一次结算界面:此时驱动手里还握着第一局的房间自动机(结算期间驱动不推进它,也不丢它)
        rig.PumpUntil(() => rig.Driver.Stage == "settle", "第一局打完进入结算");
        Assert.NotNull(rig.Driver.ActiveRoomBot);

        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Entering, "结算选再次挑战并进入第二次 Entering");
        Assert.Equal(1, rig.Driver.RetryCycles);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Same(firstRun, rig.Flow.Run);

        for (int i = 0; i < 5; i++)
        {
            rig.Frame();
            AssertDriverIsWaiting(rig, "旧局阶段里握手在途,第 " + i + " 帧");
            Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
            Assert.Same(firstRun, rig.Flow.Run);
            Assert.False(firstRun.Finished, "旧局没有被推进,所以不会收尾");
            Assert.Equal(SettlementChoice.None, firstRun.Choice);
            Assert.Equal(1, rig.Driver.RetryCycles);
            Assert.Equal(1, rig.Driver.CompletedSettlements);
        }
        Assert.Equal(1, rig.Flow.Runs);
        Assert.Single(rig.Runs);

        rig.ReleaseEntry();
        rig.PumpUntil(() => rig.Driver.Finished, "握手完成后驱动打完第二局");

        Assert.True(rig.Driver.Succeeded);
        Assert.Equal("done", rig.Driver.Stage);
        string expected = "DNF_ONLINE result=ok stage=done elapsedMs=" + rig.NowMs;
        Assert.Equal(expected, rig.Driver.ResultLine);
        Assert.Equal(new[] { expected }, rig.DriverLines);
        Assert.Equal(2, rig.Driver.CompletedSettlements);
        Assert.Equal(1, rig.Driver.RetryCycles);
        Assert.Equal(2, rig.Flow.Runs);
        Assert.Equal(2, rig.Runs.Count);
        Assert.NotSame(rig.Runs[0], rig.Runs[1]);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
    }

    [Fact]
    public void Once_the_handshake_has_finished_the_driver_builds_a_fresh_room_bot_for_the_new_run()
    {
        // Entering 结束的那一帧起驱动回到"副本"分支:为新局造一个新的房间自动机(不是旧局那个),并且开始推进新局。
        using var rig = new EnteringRig(RetryOnce());
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Dungeon && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon, "第一局开始");
        rig.HoldNextEntry();
        rig.PumpUntil(() => rig.Driver.Stage == "settle", "第一局打完进入结算");
        HostedRoomBot firstBot = rig.Driver.ActiveRoomBot;
        Assert.NotNull(firstBot);
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Entering, "进入第二次 Entering");
        AssertDriverIsWaiting(rig, "第二次 Entering");

        rig.ReleaseEntry();
        rig.PumpUntil(() => rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon && rig.Driver.Stage == "dungeon", "握手完成,驱动回到副本分支");

        HostedRoomRun secondRun = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Assert.NotSame(rig.Runs[0], secondRun);
        Assert.Same(rig.Runs[1], secondRun);
        HostedRoomBot secondBot = rig.Driver.ActiveRoomBot;
        Assert.NotNull(secondBot);
        Assert.NotSame(firstBot, secondBot);
        Assert.False(secondBot.Stopped);
        Assert.Equal("dungeon", rig.Driver.Stage);
        Assert.False(rig.Driver.Finished);
    }

    // ------------------------------------------------------------------ 用例 3:流程离开副本阶段之后,驱动不留着旧局的房间自动机

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Once_the_flow_has_left_the_dungeon_for_select_or_town_the_driver_holds_no_room_bot(bool toTown)
    {
        // 选图页与城镇两个分支各自都要把旧局的房间自动机丢掉(只靠 Entering 那一段不够:流程可以不经握手直接切走)。
        using var rig = new EnteringRig(new OnlineAutoOptions());
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Dungeon && rig.Driver.ActiveRoomBot != null, "第一局开始,驱动造了房间自动机");
        Assert.Equal("dungeon", rig.Driver.Stage);

        if (toTown)
        {
            rig.Flow.EnterTown("synthetic leave");
        }
        else
        {
            rig.Flow.EnterSelect("synthetic leave");
        }
        rig.Frame();

        Assert.Null(rig.Driver.ActiveRoomBot);
        if (!toTown)
        {
            // 城镇分支随后会因为"没选回城却回了城镇"判 fail,阶段名随之变成 dungeon;选图分支则停在 select
            Assert.Equal("select", rig.Driver.Stage);
            Assert.False(rig.Driver.Finished);
        }
        else
        {
            Assert.True(rig.Driver.Finished);
            Assert.Equal("DNF_ONLINE result=fail stage=dungeon elapsedMs=" + rig.NowMs, rig.Driver.ResultLine);
        }
    }

    // ------------------------------------------------------------------ 用例 4:结算选"选择副本"之后流程直接回到选图页

    [Fact]
    public void A_select_dungeon_choice_sends_the_flow_straight_back_to_the_select_page_and_the_driver_confirms_a_second_run()
    {
        // 选"选择副本"之后流程不经城镇、直接回选图页:驱动在副本分支里清过的"本轮已确认"标志必须仍是假,否则它在选图页永远不会再确认。
        using var rig = new EnteringRig(new OnlineAutoOptions(settlementChoices: new[] { SettlementChoice.SelectDungeon }));
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Dungeon && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.InDungeon, "第一局开始");

        rig.PumpUntil(() => rig.Driver.CompletedSettlements == 1, "第一次结算选了选择副本");
        Assert.Equal(0, rig.Driver.RetryCycles);
        Assert.False(rig.Driver.Finished);

        rig.PumpUntil(() => rig.Runs.Count == 2, "驱动在选图页重新确认,第二局建房");
        rig.PumpUntil(() => rig.Driver.Finished, "第二局打完");

        Assert.True(rig.Driver.Succeeded);
        Assert.Equal(0, rig.Driver.ExitCode);
        Assert.Equal("done", rig.Driver.Stage);
        string expected = "DNF_ONLINE result=ok stage=done elapsedMs=" + rig.NowMs;
        Assert.Equal(expected, rig.Driver.ResultLine);
        Assert.Equal(new[] { expected }, rig.DriverLines);
        Assert.Equal(2, rig.Driver.CompletedSettlements);
        Assert.Equal(0, rig.Driver.RetryCycles);
        Assert.Equal(2, rig.Flow.Runs);
        Assert.Equal(2, rig.Flow.Settlements);
        Assert.Equal(2, rig.Runs.Count);
        Assert.NotSame(rig.Runs[0], rig.Runs[1]);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
    }

    // ------------------------------------------------------------------ 用例 5:城镇里进副本控制器带着提示

    private static void SetPrivate(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    [Theory]
    [InlineData(DungeonSelectNoticeKind.Timeout, ShellTone.Error, true)]
    [InlineData(DungeonSelectNoticeKind.Aborted, ShellTone.Error, true)]
    [InlineData(DungeonSelectNoticeKind.Entering, ShellTone.Wait, false)]
    [InlineData(DungeonSelectNoticeKind.GivingUp, ShellTone.Wait, false)]
    [InlineData(DungeonSelectNoticeKind.Info, ShellTone.Info, false)]
    public void Only_an_error_toned_notice_on_the_dungeon_controller_fails_the_town_stage(DungeonSelectNoticeKind kind, ShellTone tone, bool fails)
    {
        // 进副本控制器的瞬时提示(返城后短暂提示)由控制器自己的私有字段持有;这里直接放一条不到期的,代表"回城时带着这样一条提示"。
        using var rig = new EnteringRig(new OnlineAutoOptions());
        rig.Flow.EnterTown("boot");
        SetPrivate(rig.DungeonCtrl, "_transientNotice", new DungeonSelectNotice(kind));
        Assert.Equal(kind, rig.DungeonCtrl.ActiveNotice?.Kind);
        Assert.Equal(tone, rig.DungeonCtrl.ActiveNotice.Tone);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);

        rig.Driver.StepGame(rig.Flow, rig.DungeonCtrl, rig.SettleCtrl, 16.0, 321);

        if (fails)
        {
            Assert.True(rig.Driver.Finished);
            Assert.False(rig.Driver.Succeeded);
            Assert.Equal(1, rig.Driver.ExitCode);
            Assert.Equal("town", rig.Driver.Stage);
            Assert.Equal("DNF_ONLINE result=fail stage=town elapsedMs=321", rig.Driver.ResultLine);
            Assert.Equal(GameStage.Town, rig.Flow.Stage);               // 没有去选图
        }
        else
        {
            Assert.False(rig.Driver.Finished);
            Assert.Null(rig.Driver.ResultLine);
            Assert.Equal("town", rig.Driver.Stage);
            Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);      // 照常发起选图
        }
    }

    // ------------------------------------------------------------------ 用例 6:房间自动机拿到最短清图时间,整段上限随之放宽

    [Theory]
    [InlineData(0, 0.0, 600000.0)]
    [InlineData(60, 60000.0, 600000.0)]
    [InlineData(480, 480000.0, 600000.0)]
    [InlineData(481, 481000.0, 601000.0)]
    [InlineData(900, 900000.0, 1020000.0)]
    public void The_room_bot_gets_the_min_clear_time_and_a_total_limit_two_minutes_above_it(int minClearSeconds, double minClearMs, double totalLimitMs)
    {
        using var rig = new EnteringRig(new OnlineAutoOptions(minClearTimeSeconds: minClearSeconds));
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Driver.ActiveRoomBot != null, "第一局开始,驱动造了房间自动机");

        HostedRoomBot bot = rig.Driver.ActiveRoomBot;

        Assert.Equal(minClearMs, bot.MinClearTimeMs);
        Assert.Equal(totalLimitMs, bot.TotalLimitMs);
        Assert.Equal(HostedRoomBot.DefaultNoProgressLimitMs, bot.NoProgressLimitMs);
        Assert.False(bot.Stopped);
    }

    // ------------------------------------------------------------------ 用例 7:同一局里一直是同一个房间自动机

    [Fact]
    public void The_driver_keeps_one_room_bot_for_the_whole_run_and_feeds_it_every_frame()
    {
        // 驱动只在"局换了"时才造新的房间自动机;同一局里每帧都推进同一个——它的时间累计才会一帧一帧往上走,
        // 最短清图时间的压制与整段上限才有意义(若每帧都换新的,时间归零,最短清图压制永远松不开)。
        using var rig = new EnteringRig(new OnlineAutoOptions());
        rig.StartAtDungeonSelect();
        rig.PumpUntil(() => rig.Driver.ActiveRoomBot != null, "第一局开始,驱动造了房间自动机");
        HostedRoomBot bot = rig.Driver.ActiveRoomBot;
        Assert.Equal(20.0, bot.ElapsedMs);                          // 造出来的那一帧就推进了一帧(FrameMs = 20)

        for (int i = 1; i <= 5; i++)
        {
            rig.Frame();

            Assert.Same(bot, rig.Driver.ActiveRoomBot);
            Assert.Equal(20.0 + 20.0 * i, bot.ElapsedMs);           // 每帧恰好多 20 毫秒:被推进的是同一个自动机
            Assert.False(bot.Stopped);
        }
    }
}
