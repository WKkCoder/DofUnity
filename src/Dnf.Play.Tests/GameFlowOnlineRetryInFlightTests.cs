using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r20fix-gameflow-retry(产品侧缺口):联网"再次挑战"(<see cref="SettlementChoice.Retry"/>)交给 <see cref="GameFlow.OnlineStartDungeon"/> 之后、
/// 握手还没完成之前,<b>没有过渡图</b>的副本上流程仍停在 <see cref="GameStage.Dungeon"/>、<see cref="GameFlow.Run"/> 仍是<b>旧局</b>
/// (有过渡图时 <see cref="GameFlow.BeginOnlineDungeonLoading"/> 会切到加载阶段,旧局不再是当前局)。这段时间里旧局的 <see cref="IRunSession.Choice"/> 已经是
/// <see cref="SettlementChoice.ReturnToTown"/>(或在握手期间被宿主继续推进而变成它),<see cref="GameFlow.Poll"/> 一读到就会 <c>Settlements++</c> 并闪回城镇,
/// 同一次选择被记两次,握手完成后再切回副本。修法是流程自己记着"联网开局在途":在途期间 <c>PollDungeon</c> 不读旧局的任何选择,
/// 直到握手的终结点(<see cref="GameFlow.CompleteOnlineDungeonStart"/>、<see cref="GameFlow.FailOnlineDungeonStart"/>、<see cref="GameFlow.CompleteOnlineGiveUp"/>、
/// <see cref="GameFlow.CompleteOnlineDungeonAbort"/>)或任何别的阶段切换到来。
/// 本文件钉:① 在途期间旧局的残留选择一个字不被读(A1、A6、A7、A8);② 各终结点之后新局照常被轮询、标志不残留(A2 至 A4、E1);
/// ③ 有过渡图与离线路径行为不变(A5、E3);④ 钩子抛异常时不留下守卫(E2);⑤ 被门槛挡下的分支不卡住(E4)。
/// 会话是假的、数据是合成的,时间只用流程自己的逻辑推进,不依赖墙钟;辅助类复制自 <c>GameFlowOnlineGapTests</c>(那边是私有嵌套类),改了名并加了 <c>Finished</c> 与钩子的"之后"挂点。
/// </summary>
public class GameFlowOnlineRetryInFlightTests
{
    private sealed class InFlightHero : IHeroSession
    {
        public string Label { get; }
        public InFlightHero(string label) => Label = label;
        public string MapPath => "fake/" + Label;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    /// <summary>一局副本的替身:<see cref="Finished"/> 只是让用例读起来像真的联网房间局(流程不读它,只读 <see cref="Choice"/> 与 <see cref="Failed"/>)。</summary>
    private sealed class InFlightRun : IRunSession
    {
        public InFlightRun(string path) => MapPath = path;
        public string MapPath { get; }
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
        public bool Finished { get; set; }
        public SettlementChoice Choice { get; set; }
        public bool Failed { get; set; }
    }

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

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly DungeonEntry B = new DungeonEntry("dungeon/test/b.dgn", "BBB");
    private static readonly DungeonEntry C = new DungeonEntry("dungeon/test/c.dgn", "CCC");

    /// <summary>
    /// 一条流程加上它的记录:造过几个城镇、离线开过哪些局、联网钩子被叫了几次(参数是什么)。
    /// <paramref name="controllerLike"/> 为真时钩子照真实接线(<c>OnlineDungeonFlowController.RequestEnterDungeon</c>)那样在记录之后调 <see cref="GameFlow.BeginOnlineDungeonLoading"/>;
    /// <see cref="AfterHook"/> 是钩子最后再执行的动作(同步收尾、抛异常的变体用)。
    /// </summary>
    private sealed class Rig
    {
        public GameFlow Flow { get; }
        public List<InFlightHero> Towns { get; } = new List<InFlightHero>();
        public List<(string Path, int Run)> OfflineStarts { get; } = new List<(string Path, int Run)>();
        public List<(DungeonEntry Entry, int Difficulty)> OnlineStarts { get; } = new List<(DungeonEntry Entry, int Difficulty)>();
        public Action<DungeonEntry, int> AfterHook { get; set; }

        public Rig(bool withSplash = false, bool controllerLike = false, bool onlineHook = true)
        {
            SplashScene splash = new SplashScene("test", Coded(800, 600, 77), null, null, 0, 0, 500.0);
            Flow = new GameFlow(
                new LegacyTownWorld(() =>
                {
                    var town = new InFlightHero("town#" + Towns.Count);
                    Towns.Add(town);
                    return town;
                }),
                (path, run) =>
                {
                    OfflineStarts.Add((path, run));
                    return new InFlightRun(path);
                },
                new[] { A, B, C },
                dungeonSplash: withSplash ? (Func<string, SplashScene>)(_ => splash) : null);
            if (onlineHook)
            {
                Flow.OnlineStartDungeon = (entry, difficulty) =>
                {
                    OnlineStarts.Add((entry, difficulty));
                    if (controllerLike)
                    {
                        Flow.BeginOnlineDungeonLoading(entry, difficulty, "online enter");
                    }
                    AfterHook?.Invoke(entry, difficulty);
                };
            }
        }
    }

    private static string[] HistoryOf(GameFlow flow) => flow.History.Select(e => e.ToString()).ToArray();

    private static readonly string[] AfterEnteringTheOldRun =
    {
        "#1 Town -> Town (boot)",
        "#2 Town -> Dungeon (online enter 1)",
    };

    /// <summary>
    /// 旧局:城镇 → 联网进 B 副本、难度 2(流程序号 2、局数 1),旧局替身已 <c>Finished</c>、残留选择是 <paramref name="stale"/>;
    /// <paramref name="holding"/> 为真时再把"联网出口停留"置真(模拟结算界面停留)。
    /// </summary>
    private static InFlightRun EnterOldRun(Rig rig, SettlementChoice stale, bool holding = true)
    {
        GameFlow flow = rig.Flow;
        flow.DifficultyLimit = _ => 3;
        flow.EnterTown("boot");
        var old = new InFlightRun(B.Path) { Finished = true, Choice = stale };
        flow.CompleteOnlineDungeonStart(B.Path, 2, old, "online enter 1");
        flow.OnlineHoldDungeonExit = holding;
        return old;
    }

    // ------------------------------------------------------------------ A1:Retry 之后旧局的残留选择不被读(修前红)

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(10, true)]
    public void AnOldRunsStaleReturnToTownIsNotReadWhileTheRetryHandshakeIsOpenWhenThereIsNoSplash(int polls, bool controllerLike)
    {
        // 保护(任务书 A1):没有过渡图,旧局替身 Finished 且 Choice 已是 ReturnToTown,结算停留中(出口被扣着)。选 Retry 之后钩子恰好被叫一次、
        // 阶段仍是副本、结算只记一次;之后无论 Poll 一次、两次还是十次,流程都不能读旧局的残留选择——不闪回城镇、不再记结算、不换阶段、历史不多一条。
        // 钩子两种写法都要成立:只记录的(bare)与照真实接线继续调 BeginOnlineDungeonLoading 的(controllerLike,它自己也会把出口扣置回 false)。
        var rig = new Rig(controllerLike: controllerLike);
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, SettlementChoice.ReturnToTown);
        Assert.Equal(AfterEnteringTheOldRun, HistoryOf(flow));
        Assert.Equal((GameStage.Dungeon, 2, 1, 0), (flow.Stage, flow.StageSerial, flow.Runs, flow.Settlements));
        Assert.True(flow.OnlineHoldDungeonExit);

        flow.CompleteOnlineSettlement(SettlementChoice.Retry);

        (DungeonEntry entry, int difficulty) = Assert.Single(rig.OnlineStarts);
        Assert.Same(B, entry);
        Assert.Equal(2, difficulty);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(old, flow.Run);
        Assert.Equal((2, 1, 1), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.False(flow.OnlineHoldDungeonExit);

        for (int i = 0; i < polls; i++)
        {
            Assert.False(flow.Poll(), "第 " + (i + 1) + " 次 Poll 不该切阶段");
        }

        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(old, flow.Run);
        Assert.Equal((2, 1, 1), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal(AfterEnteringTheOldRun, HistoryOf(flow));
        Assert.Single(rig.OnlineStarts);
        Assert.Single(rig.Towns);                               // 没有为"闪回城镇"再造过城镇会话
    }

    [Theory]
    [InlineData(SettlementChoice.SelectDungeon, false)]
    [InlineData(SettlementChoice.Retry, false)]
    [InlineData(SettlementChoice.QuickParty, false)]
    [InlineData(SettlementChoice.SelectDungeon, true)]
    [InlineData(SettlementChoice.Retry, true)]
    [InlineData(SettlementChoice.QuickParty, true)]
    public void EveryKindOfStaleChoiceOnTheOldRunIsIgnoredWhileTheRetryHandshakeIsOpen(SettlementChoice stale, bool controllerLike)
    {
        // 保护:守卫扣住的是旧局的"整个选择",不只是返回城镇:残留的选择其它地下城、再次挑战(会再交一次钩子、再记一次结算)、快速组队同样不读。
        var rig = new Rig(controllerLike: controllerLike);
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, stale);

        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        for (int i = 0; i < 3; i++)
        {
            Assert.False(flow.Poll(), "第 " + (i + 1) + " 次 Poll 不该切阶段");
        }

        Assert.Single(rig.OnlineStarts);
        Assert.Equal((GameStage.Dungeon, 2, 1, 1), (flow.Stage, flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Same(old, flow.Run);
        Assert.Equal(AfterEnteringTheOldRun, HistoryOf(flow));
        Assert.Null(flow.Select);
    }

    // ------------------------------------------------------------------ A2:握手成功,新局照常被轮询

    [Fact]
    public void CompletingTheOnlineStartAfterAGuardedRetryEntersTheNewRunWhichIsThenPolledNormally()
    {
        // 保护(任务书 A2):握手完成交新局(Choice 为 None):阶段仍是副本、当前局换成新局、局数加一、序号加一;之后 Poll 几次都不动;
        // 新局自己选定返回城镇时照常处理(结算记第二次)——标志没有卡在"等握手"上,旧局残留的 ReturnToTown 也早与流程无关。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, SettlementChoice.ReturnToTown);
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.False(flow.Poll());

        var fresh = new InFlightRun(B.Path) { Choice = SettlementChoice.None };
        flow.CompleteOnlineDungeonStart(B.Path, 2, fresh, "online enter 2");

        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(fresh, flow.Run);
        Assert.NotSame(old, flow.Run);
        Assert.Equal((3, 2, 1), (flow.StageSerial, flow.Runs, flow.Settlements));
        Assert.Equal("#3 Dungeon -> Dungeon (online enter 2)", flow.History[^1].ToString());
        for (int i = 0; i < 3; i++)
        {
            Assert.False(flow.Poll(), "新局没选:第 " + (i + 1) + " 次 Poll 不该切阶段");
        }
        Assert.Equal(3, flow.History.Count);
        Assert.Single(rig.Towns);

        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((4, 2, 2, 2), (flow.StageSerial, flow.Settlements, flow.Runs, rig.Towns.Count));
        Assert.Equal("#4 Dungeon -> Town (choice ReturnToTown)", flow.History[^1].ToString());
        Assert.Single(rig.OnlineStarts);
    }

    [Fact]
    public void ANewRunAfterAGuardedRetryThatPicksSelectDungeonOpensTheSelectScreenOnTheSameEntryAndDifficulty()
    {
        // 保护:守卫清掉之后新局选"选择其它地下城"照常处理:选择界面停在刚打的 B(序号 1)与难度 2,原因原样,结算记第二次。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        EnterOldRun(rig, SettlementChoice.ReturnToTown);
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        var fresh = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, fresh, "online enter 2");

        fresh.Choice = SettlementChoice.SelectDungeon;
        Assert.True(flow.Poll());

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal((1, 2), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Same(B, flow.Select.Selected);
        Assert.Equal((4, 2, 2), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal("#4 Dungeon -> DungeonSelect (choice SelectDungeon)", flow.History[^1].ToString());
    }

    [Fact]
    public void ANewRunAfterAGuardedRetryThatPicksRetryHandsOverAgainAndIsGuardedAgain()
    {
        // 保护:守卫清掉之后新局再选"再次挑战"(经 Poll):钩子第二次被叫(B、难度 2)、结算记第二次、Poll 返回 true;
        // 随后又进入新一轮在途——旧局(此刻是第二局)的选择再次不被读,标志每一轮都重新置位。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        EnterOldRun(rig, SettlementChoice.ReturnToTown);
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        var second = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, second, "online enter 2");

        second.Choice = SettlementChoice.Retry;
        Assert.True(flow.Poll());

        Assert.Equal(2, rig.OnlineStarts.Count);
        Assert.Equal((B, 2), rig.OnlineStarts[1]);
        Assert.Equal((GameStage.Dungeon, 3, 2, 2), (flow.Stage, flow.StageSerial, flow.Settlements, flow.Runs));
        for (int i = 0; i < 3; i++)
        {
            Assert.False(flow.Poll(), "第二轮在途:第 " + (i + 1) + " 次 Poll 不该再读第二局的选择");
        }
        Assert.Equal(2, rig.OnlineStarts.Count);
        Assert.Equal((3, 2), (flow.StageSerial, flow.Settlements));
        Assert.Same(second, flow.Run);
    }

    // ------------------------------------------------------------------ A3:握手失败

    [Fact]
    public void AFailedHandshakeAfterAGuardedRetryReopensTheSelectScreenOnTheSameEntryAndDifficultyAndTheNextEntryIsNotStuck()
    {
        // 保护(任务书 A3):在途的 Retry 握手失败(FailOnlineDungeonStart):回选择界面,停在原条目 B(序号 1)与难度 2,不忙、带传入提示;
        // 结算仍只记一次;之后在选择界面再确认进一局不被残留状态卡住——钩子第二次被叫,握手成功后新局照常被轮询。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        EnterOldRun(rig, SettlementChoice.ReturnToTown);
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.False(flow.Poll());

        var notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);
        flow.FailOnlineDungeonStart(B, 2, notice, "online enter failed");

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Null(flow.Run);
        Assert.Equal((1, 2), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Same(B, flow.Select.Selected);
        Assert.False(flow.Select.Busy);
        Assert.Same(notice, flow.Select.Notice);
        Assert.Equal((3, 1, 1, 0), (flow.StageSerial, flow.Settlements, flow.Runs, flow.EntryBlocks));
        Assert.Equal("#3 Dungeon -> DungeonSelect (online enter failed)", flow.History[^1].ToString());
        Assert.Single(rig.Towns);

        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal(2, rig.OnlineStarts.Count);
        Assert.Equal((B, 2), rig.OnlineStarts[1]);
        var fresh = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, fresh, "online enter 2");
        Assert.Equal((GameStage.Dungeon, 4, 2), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.False(flow.Poll());
        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((5, 2), (flow.StageSerial, flow.Settlements));
    }

    // ------------------------------------------------------------------ A4:放弃与中止

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGiveUpOrAbortThatEndsAGuardedRetryGoesHomeAndTheSameFlowRunsAnotherEnterSettleRetryRound(bool giveUp)
    {
        // 保护(任务书 A4):在途期间走放弃回城(GiveUps 加一)或中途中止(Failures 加一)都回城、历史原因原样、不记结算;
        // 之后同一个 flow 再走一轮"进图 → 结算停留 → Retry"依旧被守卫(旧局的 ReturnToTown 不被读)、握手完成后新局照常——证明守卫没有因为这两个终结点而残留。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        EnterOldRun(rig, SettlementChoice.ReturnToTown);
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.False(flow.Poll());

        if (giveUp)
        {
            flow.CompleteOnlineGiveUp("online giveup x");
        }
        else
        {
            flow.CompleteOnlineDungeonAbort("online abort x");
        }

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(giveUp ? (1, 0) : (0, 1), (flow.GiveUps, flow.Failures));
        Assert.Equal((3, 1, 1), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal(giveUp ? "#3 Dungeon -> Town (online giveup x)" : "#3 Dungeon -> Town (online abort x)", flow.History[^1].ToString());
        Assert.Equal(2, rig.Towns.Count);
        Assert.False(flow.Poll());

        // 同一个 flow 再走一轮:进图(旧局又是 Finished + ReturnToTown)→ 结算停留 → Retry
        var round2 = new InFlightRun(B.Path) { Finished = true, Choice = SettlementChoice.ReturnToTown };
        flow.CompleteOnlineDungeonStart(B.Path, 2, round2, "online enter 2");
        flow.OnlineHoldDungeonExit = true;
        Assert.False(flow.Poll());                              // 停留期间:出口被扣着
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.Equal(2, rig.OnlineStarts.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.False(flow.Poll(), "第二轮在途:第 " + (i + 1) + " 次 Poll 不该读旧局的选择");
        }
        Assert.Equal((GameStage.Dungeon, 4, 2, 2), (flow.Stage, flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Same(round2, flow.Run);

        var round3 = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, round3, "online enter 3");
        Assert.False(flow.Poll());
        round3.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((6, 3, 3), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal("#6 Dungeon -> Town (choice ReturnToTown)", flow.History[^1].ToString());
    }

    // ------------------------------------------------------------------ A5:有过渡图(对照,修前就绿)

    [Fact]
    public void WithASplashTheRetryMovesTheFlowToLoadingSoTheOldRunsStaleChoiceIsNotReadAtAll()
    {
        // 对照(任务书 A5,修前就绿,修后一字不变):有过渡图 + 旧局残留 ReturnToTown:Retry 之后(钩子照真实接线调 BeginOnlineDungeonLoading)流程进加载阶段,
        // 旧局不再是当前局;Poll 不动、加载画面走完也不自己出去、结算只记一次;握手完成后才进新局,新局照常被轮询。
        var rig = new Rig(withSplash: true, controllerLike: true);
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, SettlementChoice.ReturnToTown);

        flow.CompleteOnlineSettlement(SettlementChoice.Retry);

        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Null(flow.Run);
        Assert.Equal((3, 1, 1), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal("#3 Dungeon -> Loading (online enter)", flow.History[^1].ToString());
        Assert.Single(rig.OnlineStarts);
        for (int i = 0; i < 3; i++)
        {
            Assert.False(flow.Poll());
        }
        flow.Splash.Advance(5000);
        Assert.True(flow.Splash.Finished);
        Assert.False(flow.Poll());
        Assert.Equal((GameStage.Loading, 3, 1), (flow.Stage, flow.StageSerial, flow.Settlements));
        Assert.Single(rig.Towns);

        var fresh = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, fresh, "online enter 2");
        Assert.Equal((GameStage.Dungeon, 4, 2), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.NotSame(old, flow.Run);
        Assert.False(flow.Poll());
        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Town, 2), (flow.Stage, flow.Settlements));
    }

    // ------------------------------------------------------------------ A6:旧局没选(对照,修前就绿)

    [Fact]
    public void AnOldRunWithNoChoiceStaysCurrentAndUnpolledWhileTheRetryHandshakeIsOpen()
    {
        // 对照(任务书 A6,修前就绿,修后一字不变):没有过渡图 + 旧局 Choice 为 None:Retry 之后阶段仍是副本、仍是旧局,Poll 不动。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, SettlementChoice.None);

        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        for (int i = 0; i < 3; i++)
        {
            Assert.False(flow.Poll());
        }

        Assert.Single(rig.OnlineStarts);
        Assert.Equal((GameStage.Dungeon, 2, 1, 1), (flow.Stage, flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Same(old, flow.Run);
        Assert.Equal(AfterEnteringTheOldRun, HistoryOf(flow));
    }

    // ------------------------------------------------------------------ A7:Poll 里旧局报 Retry 的同形入口

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOldRunReportingRetryHandsTheEntryOverExactlyOnceWhenThereIsNoSplash(bool controllerLike)
    {
        // 保护(任务书 A7,修前红:量出的现状是没有过渡图时旧局 Choice 仍是 Retry,每一帧 Poll 都再进一次、结算再记一次):
        // 旧局(替身,联网用的 HostedRoomRun 自己不会报 Retry)报 Retry 时 Poll 返回 true、把(C、难度 3)交给钩子恰好一次、结算记一次;
        // 之后 Poll 十次都返回 false,不再交钩子、不再记结算、阶段与序号不动。
        var rig = new Rig(controllerLike: controllerLike);
        GameFlow flow = rig.Flow;
        flow.DifficultyLimit = _ => 3;
        flow.EnterTown("boot");
        var old = new InFlightRun(C.Path) { Finished = true, Choice = SettlementChoice.Retry };
        flow.CompleteOnlineDungeonStart(C.Path, 3, old, "online enter 1");
        Assert.Equal((GameStage.Dungeon, 2, 1, 0), (flow.Stage, flow.StageSerial, flow.Runs, flow.Settlements));

        Assert.True(flow.Poll());

        Assert.Equal((C, 3), Assert.Single(rig.OnlineStarts));
        Assert.Equal((GameStage.Dungeon, 2, 1), (flow.Stage, flow.StageSerial, flow.Settlements));
        Assert.Same(old, flow.Run);

        int trues = 0;
        for (int i = 0; i < 10; i++)
        {
            if (flow.Poll())
            {
                trues++;
            }
        }
        Assert.Equal(0, trues);
        Assert.Single(rig.OnlineStarts);
        Assert.Equal((GameStage.Dungeon, 2, 1, 1), (flow.Stage, flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal(new[] { "#1 Town -> Town (boot)", "#2 Town -> Dungeon (online enter 1)" }, HistoryOf(flow));
    }

    [Fact]
    public void AnOldRunReportingRetryWithASplashMovesToLoadingAndIsNotPolledAgain()
    {
        // 对照(任务书 A7 的有过渡图一侧,修前就绿):钩子照真实接线调 BeginOnlineDungeonLoading 且有过渡图时,旧局报 Retry 的那次 Poll 就把流程带进加载阶段,
        // 旧局不再是当前局,之后 Poll 不重复交钩子、不重复记结算。
        var rig = new Rig(withSplash: true, controllerLike: true);
        GameFlow flow = rig.Flow;
        flow.DifficultyLimit = _ => 3;
        flow.EnterTown("boot");
        var old = new InFlightRun(C.Path) { Finished = true, Choice = SettlementChoice.Retry };
        flow.CompleteOnlineDungeonStart(C.Path, 3, old, "online enter 1");

        Assert.True(flow.Poll());
        for (int i = 0; i < 5; i++)
        {
            Assert.False(flow.Poll());
        }

        Assert.Equal((C, 3), Assert.Single(rig.OnlineStarts));
        Assert.Equal((GameStage.Loading, 3, 1), (flow.Stage, flow.StageSerial, flow.Settlements));
        Assert.Equal("#3 Dungeon -> Loading (online enter)", flow.History[^1].ToString());
    }

    // ------------------------------------------------------------------ A8:握手期间旧局的选择才翻成 ReturnToTown(真接线里竞态的触发方式)

    [Fact]
    public void AnOldRunWhoseChoiceFlipsToReturnToTownWhileTheRetryHandshakeIsOpenStillDoesNotLeaveTheDungeon()
    {
        // 保护:真实接线里,联网房间局在结算停留期间被"可结束"闸门扣着(Choice 一直是 None),Retry 选定之后闸门才放开,宿主继续推进旧局,
        // 过了回城延迟它才变成 ReturnToTown——此时握手多半还没完成。这里按这个顺序造:Retry(旧局 None)→ Poll 不动 → 旧局翻成 Finished + ReturnToTown → Poll 多次仍不动;
        // 中途再扣/放"联网出口停留"也不影响守卫(守卫与出口停留各管各的);握手完成后新局照常。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, SettlementChoice.None);
        old.Finished = false;
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.False(flow.Poll());

        old.Finished = true;
        old.Choice = SettlementChoice.ReturnToTown;
        for (int i = 0; i < 5; i++)
        {
            Assert.False(flow.Poll(), "旧局翻选择之后第 " + (i + 1) + " 次 Poll 不该切阶段");
        }
        flow.OnlineHoldDungeonExit = true;
        Assert.False(flow.Poll());
        flow.OnlineHoldDungeonExit = false;
        Assert.False(flow.Poll());

        Assert.Equal((GameStage.Dungeon, 2, 1, 1), (flow.Stage, flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Same(old, flow.Run);
        Assert.Equal(AfterEnteringTheOldRun, HistoryOf(flow));
        Assert.Single(rig.Towns);

        var fresh = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, fresh, "online enter 2");
        Assert.Equal((GameStage.Dungeon, 3, 2), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.False(flow.Poll());
        Assert.Equal(3, flow.History.Count);
    }

    // ------------------------------------------------------------------ E1:钩子里同步收尾

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHookThatFinishesTheStartSynchronouslyLeavesTheNewRunUnguarded(bool viaPoll)
    {
        // 保护:钩子在被叫的这一下里就同步收尾(瞬时握手;真实接线里"副本被禁用/缺编号"会在钩子里同步 FailOnlineDungeonStart,
        // 这里用同步 CompleteOnlineDungeonStart 造"收尾之后仍在副本阶段"的情形):收尾已经切了阶段,守卫不能在钩子返回之后再置位,
        // 否则新局的选择会被永远扣住。两个入口(结算交接的 Retry、Poll 里旧局报 Retry)都要成立。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, viaPoll ? SettlementChoice.Retry : SettlementChoice.ReturnToTown, holding: !viaPoll);
        var fresh = new InFlightRun(B.Path);
        rig.AfterHook = (entry, difficulty) => flow.CompleteOnlineDungeonStart(entry.Path, difficulty, fresh, "online enter 2");

        if (viaPoll)
        {
            Assert.True(flow.Poll());
        }
        else
        {
            flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        }

        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(fresh, flow.Run);
        Assert.NotSame(old, flow.Run);
        Assert.Equal((3, 2, 1), (flow.StageSerial, flow.Runs, flow.Settlements));
        Assert.Equal("#3 Dungeon -> Dungeon (online enter 2)", flow.History[^1].ToString());
        Assert.False(flow.Poll());                              // 新局没选
        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());                               // 新局选定:照常回城,没有被扣住
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((4, 2), (flow.StageSerial, flow.Settlements));
        Assert.Single(rig.OnlineStarts);
    }

    [Fact]
    public void AHookThatFailsTheStartSynchronouslyReopensTheSelectScreenAndTheNextEntryIsNotStuck()
    {
        // 保护:钩子里同步失败(控制器对"副本被禁用/缺编号"就是这样做的):回选择界面停在原条目与难度上;守卫不残留;
        // 在选择界面再确认一次,钩子第二次被叫并同步收尾进新局,新局照常被轮询。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        EnterOldRun(rig, SettlementChoice.ReturnToTown);
        var notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Failed);
        rig.AfterHook = (entry, difficulty) => flow.FailOnlineDungeonStart(entry, difficulty, notice, "online missing id");

        flow.CompleteOnlineSettlement(SettlementChoice.Retry);

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal((1, 2), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Same(notice, flow.Select.Notice);
        Assert.Equal((3, 1, 1), (flow.StageSerial, flow.Settlements, flow.Runs));
        Assert.Equal("#3 Dungeon -> DungeonSelect (online missing id)", flow.History[^1].ToString());
        Assert.False(flow.Poll());

        var fresh = new InFlightRun(B.Path);
        rig.AfterHook = (entry, difficulty) => flow.CompleteOnlineDungeonStart(entry.Path, difficulty, fresh, "online enter 2");
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal(2, rig.OnlineStarts.Count);
        Assert.Equal((GameStage.Dungeon, 4, 2), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.Same(fresh, flow.Run);
        Assert.False(flow.Poll());
        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Town, 5, 2), (flow.Stage, flow.StageSerial, flow.Settlements));
    }

    // ------------------------------------------------------------------ E2:钩子抛异常

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHookThatThrowsLeavesNoGuardBehindSoTheOldRunIsStillReadAsBefore(bool viaPoll)
    {
        // 保护:钩子抛异常(没有任何握手在途)时异常原样传给调用方,守卫不置位:流程不会卡在"等一个永远不来的终结点",
        // 旧局的选择照常被读(与改动前一致)。两个入口都要成立;Poll 入口里结算已在钩子之前记了一次。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, viaPoll ? SettlementChoice.Retry : SettlementChoice.ReturnToTown, holding: !viaPoll);
        rig.AfterHook = (_, _) => throw new InvalidOperationException("hook failed");

        if (viaPoll)
        {
            Assert.Throws<InvalidOperationException>(() => flow.Poll());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => flow.CompleteOnlineSettlement(SettlementChoice.Retry));
        }
        Assert.Equal((GameStage.Dungeon, 2, 1), (flow.Stage, flow.StageSerial, flow.Settlements));
        Assert.Single(rig.OnlineStarts);

        old.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((3, 2), (flow.StageSerial, flow.Settlements));
        Assert.Equal("#3 Dungeon -> Town (choice ReturnToTown)", flow.History[^1].ToString());
    }

    // ------------------------------------------------------------------ E3:离线路径逐字不变

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutAnOnlineHookTheRetryStartsAnOfflineRunAndNothingIsGuarded(bool viaPoll)
    {
        // 保护:没挂联网钩子(离线)时 Retry 走离线开局:局序号接着数、难度沿用,新局由工厂造;守卫不置位——新局一选定就被读,
        // 回城照常。两个入口(结算交接、Poll 里旧局报 Retry)都要成立。
        var rig = new Rig(onlineHook: false);
        GameFlow flow = rig.Flow;
        InFlightRun old = EnterOldRun(rig, viaPoll ? SettlementChoice.Retry : SettlementChoice.ReturnToTown, holding: !viaPoll);

        if (viaPoll)
        {
            Assert.True(flow.Poll());
        }
        else
        {
            flow.CompleteOnlineSettlement(SettlementChoice.Retry, "again");
        }

        Assert.Equal(new[] { (B.Path, 1) }, rig.OfflineStarts);
        Assert.Equal((GameStage.Dungeon, 3, 2, 1), (flow.Stage, flow.StageSerial, flow.Runs, flow.Settlements));
        Assert.Equal(2, flow.Difficulty);
        Assert.NotSame(old, flow.Run);
        InFlightRun fresh = Assert.IsType<InFlightRun>(flow.Run);
        Assert.False(flow.Poll());
        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Town, 4, 2), (flow.Stage, flow.StageSerial, flow.Settlements));
    }

    // ------------------------------------------------------------------ E4:被门槛挡下的分支

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARetryBlockedByTheEntryGateOpensTheSelectScreenWithoutAnyGuardAndTheNextEntryIsNotStuck(bool viaPoll)
    {
        // 保护:被进副本门槛挡下的 Retry(改去选择界面,不交钩子)不置守卫:阶段切到选择界面、结算与挡下各记一次;
        // 之后选择界面确认进一局、握手完成,新局照常被轮询、选定时照常处理。两个入口都要成立。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EntryGate = _ => EntryVerdict.FatigueShort;
        EnterOldRun(rig, viaPoll ? SettlementChoice.Retry : SettlementChoice.ReturnToTown, holding: !viaPoll);

        if (viaPoll)
        {
            Assert.True(flow.Poll());
        }
        else
        {
            flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        }

        Assert.Empty(rig.OnlineStarts);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal((1, 2), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Equal((3, 1, 1, EntryVerdict.FatigueShort), (flow.StageSerial, flow.Settlements, flow.EntryBlocks, flow.LastBlock));
        Assert.Equal("#3 Dungeon -> DungeonSelect (choice Retry blocked FatigueShort)", flow.History[^1].ToString());

        flow.EntryGate = _ => EntryVerdict.Allowed;
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal((B, 2), Assert.Single(rig.OnlineStarts));
        var fresh = new InFlightRun(B.Path);
        flow.CompleteOnlineDungeonStart(B.Path, 2, fresh, "online enter 2");
        Assert.False(flow.Poll());
        fresh.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Town, 2), (flow.Stage, flow.Settlements));
    }

    // ------------------------------------------------------------------ E5:被门槛挡下、而选择界面造不出来

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABlockedRetryWhoseSelectScreenCannotBeBuiltLeavesNoGuardBehindSoTheOldRunIsStillReadAsBefore(bool viaPoll)
    {
        // 保护:被进副本门槛挡下的 Retry 要改去选择界面,而宿主的 ConfigureSelect 在造界面时抛了异常(阶段没切成):异常原样传给调用方,
        // 守卫不置位(没有握手在途、联网钩子也没被叫)——旧局的选择照常被读,与改动前一致。两个入口都要成立;
        // 两个入口里结算与挡下都在异常之前各记了一次。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EntryGate = _ => EntryVerdict.FatigueShort;
        InFlightRun old = EnterOldRun(rig, viaPoll ? SettlementChoice.Retry : SettlementChoice.ReturnToTown, holding: !viaPoll);
        flow.ConfigureSelect = _ => throw new InvalidOperationException("select failed");

        if (viaPoll)
        {
            Assert.Throws<InvalidOperationException>(() => flow.Poll());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => flow.CompleteOnlineSettlement(SettlementChoice.Retry));
        }
        Assert.Empty(rig.OnlineStarts);
        Assert.Same(old, flow.Run);
        Assert.Equal((GameStage.Dungeon, 2, 1, 1, EntryVerdict.FatigueShort), (flow.Stage, flow.StageSerial, flow.Settlements, flow.EntryBlocks, flow.LastBlock));

        old.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((3, 2), (flow.StageSerial, flow.Settlements));
        Assert.Equal("#3 Dungeon -> Town (choice ReturnToTown)", flow.History[^1].ToString());
    }
}
