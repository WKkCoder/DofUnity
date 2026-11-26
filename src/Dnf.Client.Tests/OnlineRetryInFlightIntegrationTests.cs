using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// r20fix-gameflow-retry(客户端集成一侧):真 <see cref="OnlineDungeonFlowController"/> + 真 <see cref="GameFlow"/>(<b>没有过渡图</b>)+ 假会话端口 + 逻辑时钟,
/// 照真实接线走"进图 → 杀光 → 结算停留 → 选再次挑战 → 第二次握手 → 新局"。钉住:没有过渡图时,点"再次挑战"之后到握手完成之前,
/// 流程不能被旧局的残留选择带回城镇(闪一下城镇、同一次选择在 <see cref="GameFlow.Settlements"/> 里记两次、握手完成再切回副本)。
/// <para>
/// 真实接线下的竞态是这样来的(读 <c>OnlineDungeonFlowController</c> 得出,不是任务书里"点击前旧局就已是 ReturnToTown"):结算停留期间旧房间的收尾闸门
/// (<c>CanFinish</c>)是关着的,旧局走不完、<c>Choice</c> 一直是 None;玩家点"再次挑战"→ <c>CompleteSettlementChoice</c> 先 <c>ClearRunState()</c>(闸门置 null、
/// 旧局从此可以收尾),再同步进 <c>RequestEnterDungeon</c> 开第二次握手;没有过渡图时流程仍停在副本阶段、当前局仍是旧局,宿主每帧还在推进它——
/// 它走完回城延迟就把 <c>Choice</c> 变成 <see cref="SettlementChoice.ReturnToTown"/>,握手若比这个延迟慢,<c>GameFlow.PollDungeon</c> 就读到它。
/// 本文件把第二次握手卡在"进入独占收包"那一步(<see cref="OnlineDungeonFlowGapR4Kit.ProbePort.EnterExclusiveBehavior"/> 返回一个由测试放行的任务),
/// 让"握手比回城延迟慢"这件事用逻辑时钟确定地发生,不靠线程快慢;卡住期间每一步都记下流程阶段,闸门放行后再看终结点。
/// </para>
/// 数据全是合成的(副本编号 9101、假路径);回城延迟用 100 毫秒的逻辑时间。
/// </summary>
public class OnlineRetryInFlightIntegrationTests
{
    private const int Monsters = 2;
    private const double ReturnDelayMs = 100.0;

    /// <summary>卡住期间逻辑时钟走的步数:20 步 × 20 毫秒 = 400 毫秒,远超回城延迟(旧局第 5 步就会走完延迟)。</summary>
    private const int WindowSteps = 20;

    private static string[] HistoryOf(GameFlow flow) => flow.History.Select(e => e.ToString()).ToArray();

    private static SpriteFrame Coded(int width, int height, byte tag)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4 + 2] = tag;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(width, height, 0, 0, width, height, rgba);
    }

    private static readonly string[] AfterFirstEntry =
    {
        "#1 Town -> Town (boot)",
        "#2 Town -> DungeonSelect (gate)",
        "#3 DungeonSelect -> Dungeon (online enter)",
    };

    /// <summary>
    /// 场景:探针场景台 + 能连着跑多局的假服务端(每次"开始游戏"都重建战斗假服务端,它自己是有状态的)+ 建房工厂(回城延迟 <see cref="ReturnDelayMs"/>)
    /// + 结算处理器(扣着不放行,存下结算就绪对象)+ 记录阶段轨迹的单步推进。
    /// </summary>
    private sealed class RetryScenario : IDisposable
    {
        private volatile bool _silentStartGame;
        private FakeCombatServer _combat;
        private readonly List<OnlineDungeonRoom> _rooms = new List<OnlineDungeonRoom>();

        public RetryScenario(bool withSplash = false, Action<OnlineDungeonRoom> onSettlementStarted = null)
        {
            SplashScene splash = new SplashScene("test", Coded(800, 600, 77), null, null, 0, 0, 500.0);
            Rig = new Rig4(dungeonSplash: withSplash ? (Func<string, SplashScene>)(_ => splash) : null);
            Rig.Port.SetResponder((cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
                {
                    _combat = new FakeCombatServer(CombatKit.Sequential(Monsters));
                    return _silentStartGame ? Array.Empty<CombatKit.ServerFrame>() : LoadingFrames();
                }
                if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.SelectDungeonPacketId)
                {
                    return InfoAndMapFrames(DungeonAId, Monsters);
                }
                if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
                {
                    return SettlementFrames();
                }
                if (cls == PacketClass.Cmd && id == SessionLifecyclePayloads.GiveUpGamePacketId)
                {
                    return TownFrames();
                }
                return _combat.Handle(cls, id, payload);
            });
            Rig.RoomBuilder = ctx =>
            {
                var room = new OnlineDungeonRoom(ctx.CombatRunner);
                HostedRoomRun run = room.CreateRun(plan => Rig.Fixture.Create(Templates(Monsters), plan, Array.Empty<GateSide>()), ReturnDelayMs);
                _rooms.Add(room);
                return OnlineDungeonRoomBuildResult.Success(room, run);
            };
            Rig.Ctrl.SettlementStarted += ready =>
            {
                ready.HoldForMenu = true;
                LastReady = ready;
                SettlementEvents++;
                onSettlementStarted?.Invoke(_rooms[_rooms.Count - 1]);
            };
        }

        public Rig4 Rig { get; }

        public OnlineDungeonSettlementReady LastReady { get; private set; }

        public int SettlementEvents { get; private set; }

        /// <summary>置真后,服务端对"开始游戏"(1/15)不再回装载通知——第二次握手会超时失败。</summary>
        public bool SilentStartGame
        {
            get => _silentStartGame;
            set => _silentStartGame = value;
        }

        /// <summary>逻辑时钟逐步推进时每一步之后的流程阶段,按先后(只有 <see cref="Step"/> 与 <see cref="StepUntil"/> 会记)。</summary>
        public List<GameStage> Trace { get; } = new List<GameStage>();

        /// <summary>城镇 → 选择屏(难度 2)→ 确认 → 第一次握手完成进副本;返回第一局。</summary>
        public HostedRoomRun EnterFirstRun()
        {
            Rig.Start();
            Rig.Flow.Select.MoveDifficulty(2);
            Rig.ConfirmSelected();
            Rig.PumpUntil(() => Rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
            return Assert.IsType<HostedRoomRun>(Rig.Flow.Run);
        }

        /// <summary>杀光当前局 → 一路轮询到结算处理器被叫(扣着不放行);此时流程仍在副本、出口被扣着、<c>Poll</c> 不动。</summary>
        public OnlineDungeonSettlementReady KillAllAndWaitForSettlement()
        {
            int before = SettlementEvents;
            KillAll(Rig);
            Rig.PumpUntil(() => SettlementEvents == before + 1);
            Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, Rig.Ctrl.Phase);
            Assert.Equal(GameStage.Dungeon, Rig.Flow.Stage);
            Assert.True(Rig.Flow.OnlineHoldDungeonExit);
            Assert.False(Rig.Flow.Poll());
            return LastReady;
        }

        /// <summary>
        /// 把下一次"进入独占收包"卡住(返回的任务由测试放行),然后点"再次挑战"。<c>CompleteChoice</c> 是同步的:它里面已经走完
        /// <c>ClearRunState → CompleteOnlineSettlement → RequestEnterDungeon</c>,第二次握手的任务此刻已建好、停在闸门上。
        /// </summary>
        public TaskCompletionSource<bool> ClickRetryWithGatedHandshake(OnlineDungeonSettlementReady ready)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Rig.Port.EnterExclusiveBehavior = async ct => await gate.Task.WaitAsync(ct);
            ready.CompleteChoice(SettlementChoice.Retry);
            return gate;
        }

        /// <summary>逻辑时钟走一步(20 毫秒):推进当前会话、控制器、流程,记下之后的流程阶段。不睡眠。</summary>
        public void Step()
        {
            Rig.NowMs += 20;
            Rig.Flow.Session?.Update(20, 0, 0);
            Rig.Flow.Splash?.Advance(20);
            Rig.Flow.Select?.Advance(20);
            Rig.Ctrl.Poll(Rig.NowMs);
            Rig.Flow.Poll();
            Trace.Add(Rig.Flow.Stage);
        }

        /// <summary>同 <see cref="Step"/>,一直走到条件成立;后台握手任务靠每圈短暂让出时间片推进(与场景台的 <c>PumpUntil</c> 一样,只是等异步完成)。</summary>
        public void StepUntil(Func<bool> condition, int maxIterations = 4000)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (condition())
                {
                    return;
                }
                Step();
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Assert.True(condition(), "Timed out; stage=" + Rig.Flow.Stage + " phase=" + Rig.Ctrl.Phase);
        }

        public void Dispose() => Rig.Dispose();
    }

    // ------------------------------------------------------------------ 真实接线:握手比回城延迟慢

    [Fact]
    public void RetryWithoutASplashDoesNotFlashToTownWhileTheSecondHandshakeIsStillOpen()
    {
        // 保护(任务书"联网集成",修前红):没有过渡图 + 真控制器。第一局杀光、结算停留(旧局 Choice 为 None、没走完)→ 选"再次挑战",
        // 第二次握手卡在收包权上;逻辑时钟走 400 毫秒:旧局放开收尾闸门、走完回城延迟,残留选择变成 ReturnToTown(宿主一直在推进它)。
        // 这期间流程必须一直停在副本、当前局仍是旧局、结算只记一次、没有为"闪回城镇"造过城镇会话;闸门放行后握手完成,当前局换成新局(局数 2、历史只多一条
        // "Dungeon -> Dungeon"),新局照常被轮询,走完第二次结算选返回城镇才真的回城。
        using var s = new RetryScenario();
        Rig4 rig = s.Rig;
        HostedRoomRun oldRun = s.EnterFirstRun();
        Assert.Equal(AfterFirstEntry, HistoryOf(rig.Flow));
        OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();
        Assert.Equal(SettlementChoice.None, oldRun.Choice);
        Assert.False(oldRun.Finished);                          // 停留期间收尾闸门是关的:旧局走不完

        TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);

        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Same(oldRun, rig.Flow.Run);
        Assert.Equal((1, 1, false), (rig.Flow.Settlements, rig.Flow.Runs, rig.Flow.OnlineHoldDungeonExit));

        for (int i = 0; i < WindowSteps; i++)
        {
            s.Step();
        }

        Assert.True(oldRun.Finished);                           // 竞态的前提确实成立:旧局已走完回城延迟,残留的是 ReturnToTown(不是空测)
        Assert.Equal(SettlementChoice.ReturnToTown, oldRun.Choice);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);   // 握手仍卡着
        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Equal(WindowSteps, s.Trace.Count);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Same(oldRun, rig.Flow.Run);
        Assert.Equal((1, 1), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Single(rig.World.CreatedPlaces);                 // 只有开场那一个城镇会话,没有闪回城镇的

        gate.SetResult(true);
        s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        HostedRoomRun newRun = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Assert.NotSame(oldRun, newRun);
        Assert.Equal(SettlementChoice.None, newRun.Choice);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal((1, 2), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal(new[] { AfterFirstEntry[0], AfterFirstEntry[1], AfterFirstEntry[2], "#4 Dungeon -> Dungeon (online enter)" }, HistoryOf(rig.Flow));
        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Single(rig.World.CreatedPlaces);
        for (int i = 0; i < 3; i++)
        {
            s.Step();
        }
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Same(newRun, rig.Flow.Run);

        s.KillAllAndWaitForSettlement().CompleteChoice(SettlementChoice.ReturnToTown);

        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal((2, 2), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal("#5 Dungeon -> Town (online settlement ReturnToTown)", rig.Flow.History[^1].ToString());
        Assert.Equal(2, rig.World.CreatedPlaces.Count);
    }

    [Fact]
    public void RetryAfterTheOldRunAlreadyFinishedDuringTheHoldDoesNotFlashToTown()
    {
        // 保护(任务书描述的状态,修前红):旧局在点击之前就已经 Finished 且残留 ReturnToTown——真实接线里闸门是选择之后才放开,这里在结算处理器里把旧房间的闸门
        // 提前拆掉(CanFinish = null)来造这个状态;停留期间出口被扣着,流程不读它。点"再次挑战"之后第二次握手卡住:再走几步,流程仍不能读到那个残留选择;
        // 闸门放行后握手完成,新局照常。
        using var s = new RetryScenario(onSettlementStarted: room => room.CanFinish = null);
        Rig4 rig = s.Rig;
        HostedRoomRun oldRun = s.EnterFirstRun();
        OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();

        for (int i = 0; i < WindowSteps; i++)
        {
            s.Step();
        }
        Assert.True(oldRun.Finished);                           // 点击之前,旧局已经走完延迟:残留 ReturnToTown
        Assert.Equal(SettlementChoice.ReturnToTown, oldRun.Choice);
        Assert.Equal((GameStage.Dungeon, true), (rig.Flow.Stage, rig.Flow.OnlineHoldDungeonExit));
        Assert.DoesNotContain(GameStage.Town, s.Trace);        // 出口被扣着:停留期间不读

        TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        for (int i = 0; i < 3; i++)
        {
            s.Step();
        }

        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Same(oldRun, rig.Flow.Run);
        Assert.Equal((1, 1), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Single(rig.World.CreatedPlaces);

        gate.SetResult(true);
        s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        Assert.NotSame(oldRun, rig.Flow.Run);
        Assert.Equal((GameStage.Dungeon, 1, 2), (rig.Flow.Stage, rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal(new[] { AfterFirstEntry[0], AfterFirstEntry[1], AfterFirstEntry[2], "#4 Dungeon -> Dungeon (online enter)" }, HistoryOf(rig.Flow));
        Assert.DoesNotContain(GameStage.Town, s.Trace);
    }

    // ------------------------------------------------------------------ 握手失败

    [Fact]
    public void ARetryHandshakeThatTimesOutReopensTheSelectScreenWithoutFlashingToTownAndTheNextEntrySucceeds()
    {
        // 保护(修前红):第二次握手期间服务端对"开始游戏"不回装载通知(SilentStartGame),闸门放行后握手超时失败。卡住期间流程不能闪回城镇;
        // 超时终结 FailOnlineDungeonStart:回选择界面,停在刚打的副本 A(序号 0)与难度 2,带超时提示、不忙,历史只多一条原因"online enter failed"的
        // Dungeon -> DungeonSelect,结算只记一次;之后在选择界面再确认,服务端恢复正常,握手成功进新局,新局照常被轮询,走完选返回城镇才回城。
        using var s = new RetryScenario();
        Rig4 rig = s.Rig;
        HostedRoomRun oldRun = s.EnterFirstRun();
        OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();
        s.SilentStartGame = true;

        TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);
        for (int i = 0; i < WindowSteps; i++)
        {
            s.Step();
        }

        Assert.Equal(SettlementChoice.ReturnToTown, oldRun.Choice);
        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Equal((GameStage.Dungeon, 1), (rig.Flow.Stage, rig.Flow.Settlements));
        Assert.Single(rig.World.CreatedPlaces);

        gate.SetResult(true);
        s.StepUntil(() => rig.Flow.Stage == GameStage.DungeonSelect && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Null(rig.Flow.Run);
        Assert.Equal((0, 2), (rig.Flow.Select.Index, rig.Flow.Select.Difficulty));
        Assert.False(rig.Flow.Select.Busy);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.Flow.Select.Notice.Kind);
        Assert.Equal((1, 1), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal(new[] { AfterFirstEntry[0], AfterFirstEntry[1], AfterFirstEntry[2], "#4 Dungeon -> DungeonSelect (online enter failed)" }, HistoryOf(rig.Flow));
        Assert.Single(rig.World.CreatedPlaces);

        s.SilentStartGame = false;
        rig.Flow.Select.Confirm();
        Assert.True(rig.Flow.Poll());
        s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        HostedRoomRun third = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Assert.NotSame(oldRun, third);
        Assert.Equal(SettlementChoice.None, third.Choice);
        Assert.Equal((GameStage.Dungeon, 1, 2), (rig.Flow.Stage, rig.Flow.Settlements, rig.Flow.Runs));
        for (int i = 0; i < 3; i++)
        {
            s.Step();
        }
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);

        s.KillAllAndWaitForSettlement().CompleteChoice(SettlementChoice.ReturnToTown);
        Assert.Equal((GameStage.Town, 2, 2), (rig.Flow.Stage, rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal("#6 Dungeon -> Town (online settlement ReturnToTown)", rig.Flow.History[^1].ToString());
    }

    [Fact]
    public void ADisconnectWhileTheRetryHandshakeIsOpenDoesNotFlashToTownAndLeavesTheCleanupToTheHost()
    {
        // 保护(修前红):第二次握手期间断线(假端口置断线并让卡着的"进入独占收包"以 IO 错误收场)。卡住期间流程不闪回城镇;握手任务出错后控制器走断线清理
        // (TriggerDisconnectCleanup 只通知宿主 OnDisconnected 一次,不碰流程;真实宿主随后丢弃这条流程并回外壳),所以流程停在原地:副本阶段、当前局仍是旧局、
        // 结算只记一次、历史没有新增。这里钉的是"没有闪回城镇,也没有被谁卡死";流程之后的去向归宿主管。
        using var s = new RetryScenario();
        Rig4 rig = s.Rig;
        HostedRoomRun oldRun = s.EnterFirstRun();
        OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();

        TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);
        for (int i = 0; i < WindowSteps; i++)
        {
            s.Step();
        }
        Assert.Equal(SettlementChoice.ReturnToTown, oldRun.Choice);
        Assert.DoesNotContain(GameStage.Town, s.Trace);

        rig.Port.TriggerDisconnect();
        gate.SetException(new IOException("synthetic closed"));
        s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);
        s.Step();

        Assert.Equal(1, rig.Disconnects);
        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Same(oldRun, rig.Flow.Run);
        Assert.Equal((1, 1), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal(AfterFirstEntry, HistoryOf(rig.Flow));
        Assert.Single(rig.World.CreatedPlaces);
    }

    // ------------------------------------------------------------------ 连续多局

    [Fact]
    public void TwoGuardedRetriesInARowThenReturnToTownAreEachCountedOnceWithoutFlashingToTown()
    {
        // 保护(修前红):连续两次"再次挑战"(每次第二次握手都卡住、让旧局走完回城延迟)再返回城镇:每一轮守卫都重新置位、握手完成时都清掉,
        // 每次选择只在结算里记一次,历史里除最后的回城外没有任何城镇,局数 3、城镇会话 2 个(开场一个、最后回城一个)。
        using var s = new RetryScenario();
        Rig4 rig = s.Rig;
        HostedRoomRun previous = s.EnterFirstRun();
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();
            TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);
            for (int i = 0; i < WindowSteps; i++)
            {
                s.Step();
            }
            Assert.Equal(SettlementChoice.ReturnToTown, previous.Choice);
            Assert.DoesNotContain(GameStage.Town, s.Trace);
            Assert.Same(previous, rig.Flow.Run);
            Assert.Equal((GameStage.Dungeon, cycle, cycle), (rig.Flow.Stage, rig.Flow.Settlements, rig.Flow.Runs));

            gate.SetResult(true);
            s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
            Assert.NotSame(previous, rig.Flow.Run);
            previous = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
            Assert.Equal((cycle, cycle + 1), (rig.Flow.Settlements, rig.Flow.Runs));
        }

        s.KillAllAndWaitForSettlement().CompleteChoice(SettlementChoice.ReturnToTown);

        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal((3, 3), (rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal(
            new[]
            {
                AfterFirstEntry[0],
                AfterFirstEntry[1],
                AfterFirstEntry[2],
                "#4 Dungeon -> Dungeon (online enter)",
                "#5 Dungeon -> Dungeon (online enter)",
                "#6 Dungeon -> Town (online settlement ReturnToTown)",
            },
            HistoryOf(rig.Flow));
        Assert.Equal(2, rig.World.CreatedPlaces.Count);
    }

    [Fact]
    public void GivingUpOnTheNewRunAfterAGuardedRetryGoesHomeAsUsual()
    {
        // 保护:守卫清掉之后新局的放弃回城照常:真实入口 RequestGiveUpDungeon 在握手期间(Entering)被控制器静默忽略,
        // 新局进入 InDungeon 之后才生效——发"放弃"命令(1/45),服务端回城镇区域通知,控制器放权后 CompleteOnlineGiveUp:回城镇、放弃记一次、
        // 结算仍只有选再次挑战那一次,历史原因 "online giveup"。
        using var s = new RetryScenario();
        Rig4 rig = s.Rig;
        HostedRoomRun oldRun = s.EnterFirstRun();
        OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();
        TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);
        for (int i = 0; i < WindowSteps; i++)
        {
            s.Step();
        }
        rig.Ctrl.RequestGiveUpDungeon();                        // 握手期间:被忽略
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        gate.SetResult(true);
        s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
        Assert.NotSame(oldRun, rig.Flow.Run);
        Assert.DoesNotContain(GameStage.Town, s.Trace);

        rig.Ctrl.RequestGiveUpDungeon();
        Assert.Equal(OnlineDungeonFlowPhase.GivingUp, rig.Ctrl.Phase);
        s.StepUntil(() => rig.Flow.Stage == GameStage.Town);

        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal((1, 2, 1), (rig.Flow.Settlements, rig.Flow.Runs, rig.Flow.GiveUps));
        Assert.Equal("#5 Dungeon -> Town (online giveup)", rig.Flow.History[^1].ToString());
        Assert.Equal(1, rig.Port.Commands.Count(c => c.Class == PacketClass.Cmd && c.PacketId == SessionLifecyclePayloads.GiveUpGamePacketId));
        Assert.Equal(2, rig.World.CreatedPlaces.Count);
    }

    // ------------------------------------------------------------------ 有过渡图(对照,修前就绿)

    [Fact]
    public void WithASplashTheRetryGoesThroughLoadingAndNeverTouchesTheOldRunsStaleChoice()
    {
        // 对照(修前就绿,修后一字不变):同一个场景,但流程带副本过渡图:点"再次挑战"后控制器调 BeginOnlineDungeonLoading,流程立刻进加载阶段,
        // 旧局不再是当前局,握手卡多久都不会读到它的残留选择;握手完成才回到副本。历史是 Loading 夹着的两段,阶段轨迹里没有城镇。
        using var s = new RetryScenario(withSplash: true);
        Rig4 rig = s.Rig;
        HostedRoomRun oldRun = s.EnterFirstRun();
        Assert.Equal(
            new[]
            {
                "#1 Town -> Town (boot)",
                "#2 Town -> DungeonSelect (gate)",
                "#3 DungeonSelect -> Loading (online enter)",
                "#4 Loading -> Dungeon (online enter)",
            },
            HistoryOf(rig.Flow));
        OnlineDungeonSettlementReady ready = s.KillAllAndWaitForSettlement();

        TaskCompletionSource<bool> gate = s.ClickRetryWithGatedHandshake(ready);
        Assert.Equal((GameStage.Loading, 1), (rig.Flow.Stage, rig.Flow.Settlements));
        Assert.Null(rig.Flow.Run);
        for (int i = 0; i < WindowSteps; i++)
        {
            s.Step();
        }
        Assert.Equal(Enumerable.Repeat(GameStage.Loading, WindowSteps), s.Trace);
        Assert.Equal((GameStage.Loading, 1, 1), (rig.Flow.Stage, rig.Flow.Settlements, rig.Flow.Runs));

        gate.SetResult(true);
        s.StepUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        Assert.DoesNotContain(GameStage.Town, s.Trace);
        Assert.NotSame(oldRun, rig.Flow.Run);
        Assert.Equal((GameStage.Dungeon, 1, 2), (rig.Flow.Stage, rig.Flow.Settlements, rig.Flow.Runs));
        Assert.Equal(
            new[]
            {
                "#1 Town -> Town (boot)",
                "#2 Town -> DungeonSelect (gate)",
                "#3 DungeonSelect -> Loading (online enter)",
                "#4 Loading -> Dungeon (online enter)",
                "#5 Dungeon -> Loading (online enter)",
                "#6 Loading -> Dungeon (online enter)",
            },
            HistoryOf(rig.Flow));
        Assert.Single(rig.World.CreatedPlaces);
    }
}
