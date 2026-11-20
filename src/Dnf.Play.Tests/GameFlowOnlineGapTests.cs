using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 测试补强(区域 C):<see cref="GameFlow"/> 的联网副本钩子
/// (<see cref="GameFlow.BeginOnlineDungeonLoading"/>、<see cref="GameFlow.CompleteOnlineDungeonStart"/>、<see cref="GameFlow.FailOnlineDungeonStart"/>、
/// <see cref="GameFlow.CompleteOnlineSettlement"/> 与 <see cref="GameFlow.Poll"/> 里的联网重试)。
/// 每个场景都断言调用前后的阶段、序号、历史、选择界面状态(选中项、难度、忙等、提示)、难度夹取的上下沿、回调是否触发与参数。
/// 会话是假的、数据是合成的,时间只用流程自己的逻辑推进,不依赖墙钟。
/// </summary>
public class GameFlowOnlineGapTests
{
    private sealed class StubHero : IHeroSession
    {
        public string Label { get; }
        public StubHero(string label) => Label = label;
        public string MapPath => "fake/" + Label;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    private sealed class StubRun : IRunSession
    {
        public StubRun(string path) => MapPath = path;
        public string MapPath { get; }
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
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

    private const string Unlisted = "dungeon/test/unlisted.dgn";

    /// <summary>一条流程加上它的各种记录:造过几个城镇、离线开过哪些局、联网钩子被叫了几次(参数是什么)。</summary>
    private sealed class Rig
    {
        public GameFlow Flow { get; }
        public List<StubHero> Towns { get; } = new List<StubHero>();
        public List<(string Path, int Run)> OfflineStarts { get; } = new List<(string Path, int Run)>();
        public List<(DungeonEntry Entry, int Difficulty)> OnlineStarts { get; } = new List<(DungeonEntry Entry, int Difficulty)>();

        public Rig(bool withSplash = false, bool onlineHook = true)
        {
            SplashScene splash = new SplashScene("test", Coded(800, 600, 77), null, null, 0, 0, 500.0);
            Flow = new GameFlow(
                new LegacyTownWorld(() =>
                {
                    var town = new StubHero("town#" + Towns.Count);
                    Towns.Add(town);
                    return town;
                }),
                (path, run) =>
                {
                    OfflineStarts.Add((path, run));
                    return new StubRun(path);
                },
                new[] { A, B, C },
                dungeonSplash: withSplash ? (Func<string, SplashScene>)(_ => splash) : null);
            if (onlineHook)
            {
                Flow.OnlineStartDungeon = (entry, difficulty) => OnlineStarts.Add((entry, difficulty));
            }
        }
    }

    // ------------------------------------------------------------------ BeginOnlineDungeonLoading

    [Fact]
    public void BeginningOnlineLoadingOnTheSelectScreenRecordsPathAndClampedDifficultyAndMarksTheScreenBusy()
    {
        // 保护:没有过渡图时,联网请求进副本只在当前选择界面上标忙并挂"正在请求进入"提示,不切阶段;
        // 同时记下副本路径与夹在 0 到 DifficultyCount-1(=4)里的难度,并清掉遗留的"暂缓退出"标记。
        Assert.Equal(5, DungeonSession.DifficultyCount);        // 下面的上限 4 = 5 - 1
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        DungeonSelectScreen select = flow.Select;
        Assert.Equal(2, flow.StageSerial);
        Assert.Equal(2, flow.History.Count);

        flow.OnlineHoldDungeonExit = true;
        flow.BeginOnlineDungeonLoading(B, 2);

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(2, flow.StageSerial);                      // 没有切阶段
        Assert.Equal(2, flow.History.Count);                    // 也没有新的历史
        Assert.Same(select, flow.Select);                       // 还是同一块界面
        Assert.Null(flow.Splash);
        Assert.True(select.Busy);
        Assert.NotNull(select.Notice);
        Assert.Equal(DungeonSelectNoticeKind.Entering, select.Notice.Kind);
        Assert.Equal(B.Path, flow.DungeonPath);
        Assert.Equal(2, flow.Difficulty);
        Assert.False(flow.OnlineHoldDungeonExit);

        // 难度夹取:下沿 0(0 与负数都是 0,不能被抬到 1)、区间内原样、上沿 4(5 与更大都夹到 4)
        flow.BeginOnlineDungeonLoading(A, 0);
        Assert.Equal((A.Path, 0), (flow.DungeonPath, flow.Difficulty));
        flow.BeginOnlineDungeonLoading(A, -3);
        Assert.Equal(0, flow.Difficulty);
        flow.BeginOnlineDungeonLoading(A, 3);
        Assert.Equal(3, flow.Difficulty);
        flow.BeginOnlineDungeonLoading(A, 4);
        Assert.Equal(4, flow.Difficulty);
        flow.BeginOnlineDungeonLoading(A, 5);
        Assert.Equal(4, flow.Difficulty);
        flow.BeginOnlineDungeonLoading(C, 99);
        Assert.Equal((C.Path, 4), (flow.DungeonPath, flow.Difficulty));
        Assert.Equal(2, flow.StageSerial);                      // 全程没有切阶段
        Assert.Same(select, flow.Select);
    }

    [Fact]
    public void BeginningOnlineLoadingOutsideTheSelectScreenOnlyRecordsTheTarget()
    {
        // 保护:不在选择界面(这里是城镇)且没有过渡图时,没有界面可标忙,也不会因此切阶段或抛异常。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");
        flow.OnlineHoldDungeonExit = true;

        flow.BeginOnlineDungeonLoading(C, 1);

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(1, flow.StageSerial);
        Assert.Null(flow.Select);
        Assert.Equal((C.Path, 1), (flow.DungeonPath, flow.Difficulty));
        Assert.False(flow.OnlineHoldDungeonExit);

        // 参数校验在改任何状态之前:空条目与空路径都抛异常,路径与难度保持原样
        Assert.Throws<ArgumentNullException>(() => flow.BeginOnlineDungeonLoading(null, 3));
        Assert.Throws<ArgumentException>(() => flow.BeginOnlineDungeonLoading(new DungeonEntry(string.Empty, "EMPTY"), 3));
        Assert.Equal((C.Path, 1), (flow.DungeonPath, flow.Difficulty));
    }

    [Fact]
    public void BeginningOnlineLoadingWithASplashSwitchesToLoadingAndRecordsTargetAndReason()
    {
        // 保护:有过渡图时切到加载阶段(带调用方给的原因),同样记路径与夹取后的难度并清暂缓标记;选择界面随之消失。
        var rig = new Rig(withSplash: true);
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        flow.OnlineHoldDungeonExit = true;

        flow.BeginOnlineDungeonLoading(B, 99, "online enter x");

        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Equal(3, flow.StageSerial);
        Assert.NotNull(flow.Splash);
        Assert.Null(flow.Select);
        Assert.Null(flow.Session);
        Assert.Equal((GameStage.DungeonSelect, GameStage.Loading, "online enter x"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
        Assert.Equal((B.Path, 4), (flow.DungeonPath, flow.Difficulty));
        Assert.False(flow.OnlineHoldDungeonExit);

        // 下沿也走同一条夹取:0 与负数都是 0
        flow.BeginOnlineDungeonLoading(A, -7);
        Assert.Equal((A.Path, 0), (flow.DungeonPath, flow.Difficulty));
        flow.BeginOnlineDungeonLoading(A, 0);
        Assert.Equal(0, flow.Difficulty);
    }

    // ------------------------------------------------------------------ CompleteOnlineDungeonStart

    [Fact]
    public void CompletingTheOnlineStartClampsTheDifficultyBothWaysAndSwitchesIntoTheDungeon()
    {
        // 保护:握手与建房完成后进副本阶段,计一局,难度夹在 0 到 4(越上界 4、越下界 0),清暂缓标记、原因原样进历史。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");
        flow.OnlineHoldDungeonExit = true;
        var run = new StubRun(A.Path);

        flow.CompleteOnlineDungeonStart(A.Path, 99, run, "enter 1");

        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(run, flow.Session);
        Assert.Same(run, flow.Run);
        Assert.Equal(1, flow.Runs);
        Assert.Equal(A.Path, flow.DungeonPath);
        Assert.Equal(4, flow.Difficulty);                       // 99 夹到 DifficultyCount-1 = 4
        Assert.False(flow.OnlineHoldDungeonExit);
        Assert.Equal((GameStage.Town, GameStage.Dungeon, "enter 1"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));

        flow.CompleteOnlineDungeonStart(B.Path, -3, new StubRun(B.Path));
        Assert.Equal((B.Path, 0, 2), (flow.DungeonPath, flow.Difficulty, flow.Runs));   // -3 夹到 0(不是 -3)
        Assert.Equal("online enter", flow.History[^1].Reason);                          // 默认原因

        flow.CompleteOnlineDungeonStart(C.Path, 3, new StubRun(C.Path));
        Assert.Equal((C.Path, 3, 3), (flow.DungeonPath, flow.Difficulty, flow.Runs));
        flow.CompleteOnlineDungeonStart(C.Path, 4, new StubRun(C.Path));
        Assert.Equal(4, flow.Difficulty);
        flow.CompleteOnlineDungeonStart(C.Path, 5, new StubRun(C.Path));
        Assert.Equal((4, 5), (flow.Difficulty, flow.Runs));     // 刚好越界 1 也夹到 4
        flow.CompleteOnlineDungeonStart(C.Path, 0, new StubRun(C.Path));
        Assert.Equal(0, flow.Difficulty);
    }

    [Fact]
    public void CompletingTheOnlineStartRejectsMissingPartsWithoutTouchingTheFlow()
    {
        // 保护:空路径与空会话在改状态之前就抛异常,局序号、阶段、路径都不动。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");

        Assert.Throws<ArgumentException>(() => flow.CompleteOnlineDungeonStart(string.Empty, 1, new StubRun(A.Path)));
        Assert.Throws<ArgumentNullException>(() => flow.CompleteOnlineDungeonStart(A.Path, 1, null));
        Assert.Equal((GameStage.Town, 0, 1), (flow.Stage, flow.Runs, flow.StageSerial));
        Assert.Null(flow.DungeonPath);
    }

    // ------------------------------------------------------------------ FailOnlineDungeonStart

    [Fact]
    public void FailingTheOnlineStartReopensTheSelectScreenOnTheMatchingEntryByPathOrReferenceWithDifficultyAndNotice()
    {
        // 保护:联网进副本失败后选择界面停在原条目与难度上:条目按"同一对象或同路径"匹配(控制器按路径重建的副本条目也要找得回来),
        // 找不到或为空回到第 0 项;界面解除忙等并换上传入的提示;暂缓标记被清掉;原因进历史。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.DifficultyLimit = _ => 3;                          // 让选择界面能承载难度 1 到 3
        int configured = 0;
        flow.ConfigureSelect = _ => configured++;
        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        Assert.Equal(1, configured);
        flow.OnlineHoldDungeonExit = true;
        var notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);

        // 路径相同、但不是列表里的那个对象(B 是序号 1)
        flow.FailOnlineDungeonStart(new DungeonEntry(B.Path, "COPY"), 2, notice, "failed by path");
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(3, flow.StageSerial);
        Assert.Equal(1, flow.Select.Index);
        Assert.Same(B, flow.Select.Selected);
        Assert.Equal(2, flow.Select.Difficulty);
        Assert.False(flow.Select.Busy);
        Assert.Same(notice, flow.Select.Notice);
        Assert.False(flow.OnlineHoldDungeonExit);
        Assert.Equal(2, configured);                            // 选择界面是重新造的,挂钩再执行一次
        Assert.Equal((GameStage.DungeonSelect, GameStage.DungeonSelect, "failed by path"),
            (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));

        // 同一个对象(C 是序号 2),默认原因
        flow.FailOnlineDungeonStart(C, 1, notice);
        Assert.Equal((2, 1), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Equal("online enter failed", flow.History[^1].Reason);

        // 列表里没有这条路径:回到第 0 项
        flow.FailOnlineDungeonStart(new DungeonEntry("dungeon/test/zzz.dgn", "ZZZ"), 1, notice);
        Assert.Equal(0, flow.Select.Index);

        // 条目为空、提示为空:第 0 项、无提示、不忙
        flow.Select.Busy = true;
        flow.FailOnlineDungeonStart(null, 0, null);
        Assert.Equal((0, 0), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Null(flow.Select.Notice);
        Assert.False(flow.Select.Busy);
    }

    [Fact]
    public void FailingTheOnlineStartFromTheLoadingStageLeavesTheSplashAndCancelsItsPendingEntry()
    {
        // 保护:加载画面期间联网失败,界面退出加载阶段回到选择界面(Splash 清空、阶段是选择),
        // 之后加载画面即使"走完"也不会把流程再带进副本。
        var rig = new Rig(withSplash: true);
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        flow.BeginOnlineDungeonLoading(B, 1);
        Assert.Equal(GameStage.Loading, flow.Stage);
        flow.Splash.Advance(5000);
        Assert.True(flow.Splash.Finished);

        flow.FailOnlineDungeonStart(B, 1, new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected));

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Null(flow.Splash);
        Assert.Equal(1, flow.Select.Index);
        Assert.Equal(DungeonSelectNoticeKind.Rejected, flow.Select.Notice.Kind);
        Assert.False(flow.Poll());                              // 选择界面没有结果:什么也不发生
        Assert.Equal(0, flow.Runs);
        Assert.Empty(rig.OfflineStarts);
    }

    // ------------------------------------------------------------------ 结算交接与联网重试

    [Fact]
    public void OnlineRetryRebuildsAnEntryFromThePathWhenItIsNotListedAndPassesTheCurrentDifficulty()
    {
        // 保护:联网"再次挑战"(经结算交接或 Poll)把当前路径对应的条目与当前难度交给 OnlineStartDungeon:
        // 路径在列表里就给列表里那个对象;不在列表里按路径现造一个(名字取路径主干大写、没有卡片);
        // Poll 在联网重试时返回 true、结算计数加一、阶段不动(由宿主握手完成后切)。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");

        flow.CompleteOnlineDungeonStart(Unlisted, 2, new StubRun(Unlisted), "enter");
        int serial = flow.StageSerial;
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);

        Assert.Single(rig.OnlineStarts);
        DungeonEntry rebuilt = rig.OnlineStarts[0].Entry;
        Assert.Equal(Unlisted, rebuilt.Path);
        Assert.Equal("UNLISTED", rebuilt.Name);
        Assert.Null(rebuilt.Card);
        Assert.DoesNotContain(rebuilt, flow.Entries);
        Assert.Equal(2, rig.OnlineStarts[0].Difficulty);
        Assert.Equal(1, flow.Settlements);
        Assert.Equal((GameStage.Dungeon, serial), (flow.Stage, flow.StageSerial));
        Assert.Empty(rig.OfflineStarts);

        // 经 Poll:路径不在列表里也走同一套重建,返回 true
        var run = new StubRun(Unlisted) { Choice = SettlementChoice.Retry };
        flow.CompleteOnlineDungeonStart(Unlisted, 3, run);
        Assert.True(flow.Poll());
        Assert.Equal(2, rig.OnlineStarts.Count);
        Assert.Equal((Unlisted, "UNLISTED", 3), (rig.OnlineStarts[1].Entry.Path, rig.OnlineStarts[1].Entry.Name, rig.OnlineStarts[1].Difficulty));
        Assert.Equal(2, flow.Settlements);
        Assert.Equal(GameStage.Dungeon, flow.Stage);

        // 路径在列表里:给的是列表里那个对象本身
        var listedRun = new StubRun(B.Path) { Choice = SettlementChoice.Retry };
        flow.CompleteOnlineDungeonStart(B.Path, 1, listedRun);
        Assert.True(flow.Poll());
        Assert.Same(B, rig.OnlineStarts[2].Entry);
        Assert.Equal(1, rig.OnlineStarts[2].Difficulty);
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.Same(B, rig.OnlineStarts[3].Entry);
        Assert.Equal(4, rig.OnlineStarts.Count);
        Assert.Equal(4, flow.Settlements);
    }

    [Fact]
    public void OnlineRetryBlockedByTheEntryGateReopensTheSelectScreenInsteadOfStarting()
    {
        // 保护:联网重试也要先过 EntryGate:被挡下就记 EntryBlocks / LastBlock、去选择界面(停在刚打的那座、原因写 blocked),
        // 不调用联网开局钩子也不走离线开局;放行时才调钩子。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        var asked = new List<string>();
        EntryVerdict verdict = EntryVerdict.FatigueShort;
        flow.EntryGate = path =>
        {
            asked.Add(path);
            return verdict;
        };
        flow.DifficultyLimit = _ => 3;
        flow.EnterTown("boot");
        flow.CompleteOnlineDungeonStart(B.Path, 2, new StubRun(B.Path));

        flow.CompleteOnlineSettlement(SettlementChoice.Retry, "online settlement");

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal("choice Retry blocked FatigueShort", flow.History[^1].Reason);
        Assert.Equal((1, EntryVerdict.FatigueShort, 1), (flow.EntryBlocks, flow.LastBlock, flow.Settlements));
        Assert.Empty(rig.OnlineStarts);
        Assert.Empty(rig.OfflineStarts);
        Assert.Equal(new[] { B.Path }, asked);
        Assert.Equal(1, flow.Select.Index);
        Assert.Equal(2, flow.Select.Difficulty);

        // 经 Poll 的联网重试同样被挡:返回 true(发生了切换)、历史原因相同
        var run = new StubRun(C.Path) { Choice = SettlementChoice.Retry };
        flow.CompleteOnlineDungeonStart(C.Path, 1, run);
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal("choice Retry blocked FatigueShort", flow.History[^1].Reason);
        Assert.Equal(2, flow.Select.Index);
        Assert.Equal((2, 2), (flow.EntryBlocks, flow.Settlements));
        Assert.Empty(rig.OnlineStarts);

        // 放行:问的是当前路径,调用联网钩子(而不是离线开局),不记挡下
        verdict = EntryVerdict.Allowed;
        flow.CompleteOnlineDungeonStart(A.Path, 3, new StubRun(A.Path));
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.Equal(A.Path, asked[^1]);
        Assert.Single(rig.OnlineStarts);
        Assert.Same(A, rig.OnlineStarts[0].Entry);
        Assert.Equal(3, rig.OnlineStarts[0].Difficulty);
        Assert.Equal(2, flow.EntryBlocks);
        Assert.Empty(rig.OfflineStarts);
    }

    [Fact]
    public void OnlineSettlementSelectDungeonOpensTheSelectScreenOnTheJustPlayedDungeonAndTownGoesHome()
    {
        // 保护:联网结算选"选择其它地下城"回到选择界面(不是城镇):选中刚打的那座、难度沿用、原因原样、会话清空、计一次结算;
        // 选"返回城镇"去新造的城镇会话;选快速组队没有对应去向,什么也不切;每次都清暂缓标记。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.DifficultyLimit = _ => 3;
        flow.EnterTown("boot");
        flow.CompleteOnlineDungeonStart(C.Path, 3, new StubRun(C.Path));
        flow.OnlineHoldDungeonExit = true;

        flow.CompleteOnlineSettlement(SettlementChoice.SelectDungeon, "online pick");

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(2, flow.Select.Index);
        Assert.Same(C, flow.Select.Selected);
        Assert.Equal(3, flow.Select.Difficulty);
        Assert.Null(flow.Session);
        Assert.Null(flow.Run);
        Assert.Equal((GameStage.Dungeon, GameStage.DungeonSelect, "online pick"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
        Assert.Equal((1, 1), (flow.Settlements, rig.Towns.Count));
        Assert.False(flow.OnlineHoldDungeonExit);

        // 再进一局、选返回城镇
        flow.CompleteOnlineDungeonStart(A.Path, 1, new StubRun(A.Path));
        flow.OnlineHoldDungeonExit = true;
        flow.CompleteOnlineSettlement(SettlementChoice.ReturnToTown, "online home");
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Same(rig.Towns[1], flow.Session);
        Assert.Equal(2, rig.Towns.Count);
        Assert.Equal((GameStage.Dungeon, GameStage.Town, "online home"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
        Assert.Equal(2, flow.Settlements);
        Assert.False(flow.OnlineHoldDungeonExit);

        // 快速组队:没有去向
        flow.CompleteOnlineDungeonStart(A.Path, 1, new StubRun(A.Path));
        int serial = flow.StageSerial;
        flow.OnlineHoldDungeonExit = true;
        flow.CompleteOnlineSettlement(SettlementChoice.QuickParty);
        Assert.Equal((GameStage.Dungeon, serial, 2), (flow.Stage, flow.StageSerial, flow.Settlements));
        Assert.False(flow.OnlineHoldDungeonExit);
    }

    [Fact]
    public void OnlineSettlementRetryWithoutAHookFallsBackToAnOfflineStartWithTheSameDifficulty()
    {
        // 保护:没挂联网开局钩子时,结算交接的"再次挑战"退回离线开局:局序号接着数、难度沿用、原因原样、阶段仍是副本。
        var rig = new Rig(onlineHook: false);
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");
        flow.CompleteOnlineDungeonStart(B.Path, 2, new StubRun(B.Path));

        flow.CompleteOnlineSettlement(SettlementChoice.Retry, "again");

        Assert.Equal(new[] { (B.Path, 1) }, rig.OfflineStarts);     // 联网开的那局算第 0 局,这是第 1 局
        Assert.Equal((GameStage.Dungeon, 2, 2, 1), (flow.Stage, flow.Runs, flow.Difficulty, flow.Settlements));
        Assert.Equal((GameStage.Dungeon, GameStage.Dungeon, "again"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
    }

    // ------------------------------------------------------------------ 选择界面确认与联网失败

    [Fact]
    public void ConfirmingOnTheSelectScreenHandsTheEntryAndDifficultyToTheOnlineHookAndKeepsTheScreen()
    {
        // 保护:联网模式下选择界面确认进入时,只把(选中条目, 选中难度)交给 OnlineStartDungeon,Poll 返回 true,
        // 阶段与界面保持原样(等宿主握手后再切),局数不加;被 EntryGate 挡下时重开选择界面且不调钩子。
        var rig = new Rig();
        GameFlow flow = rig.Flow;
        flow.DifficultyLimit = _ => 3;
        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        DungeonSelectScreen select = flow.Select;
        select.Move(1);
        select.MoveDifficulty(1);
        select.MoveDifficulty(1);
        select.Confirm();

        Assert.True(flow.Poll());
        Assert.Single(rig.OnlineStarts);
        Assert.Same(B, rig.OnlineStarts[0].Entry);
        Assert.Equal(2, rig.OnlineStarts[0].Difficulty);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Same(select, flow.Select);
        Assert.Equal((0, 2), (flow.Runs, flow.StageSerial));
        Assert.Empty(rig.OfflineStarts);

        // 挡下:重开选择界面(新对象),停在同一项与同一难度,不调钩子
        flow.EntryGate = _ => EntryVerdict.FatigueShort;
        Assert.True(flow.Poll());
        Assert.Single(rig.OnlineStarts);
        Assert.NotSame(select, flow.Select);
        Assert.Equal((1, 2), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Equal("select BBB blocked FatigueShort", flow.History[^1].Reason);
        Assert.Equal(1, flow.EntryBlocks);
    }

    [Fact]
    public void AFailedOnlineRunWaitsForTheControllerInsteadOfLeavingByItself()
    {
        // 保护:联网副本里 run.Failed 时 Poll 不自己回城、不记 Failures(要等控制器收尾后调 CompleteOnlineDungeonAbort);
        // 离线(没有联网钩子)才由 Poll 记失败并回城。
        var online = new Rig();
        online.Flow.EnterTown("boot");
        var run = new StubRun(A.Path);
        online.Flow.CompleteOnlineDungeonStart(A.Path, 0, run);
        run.Failed = true;
        Assert.False(online.Flow.Poll());
        Assert.Equal((GameStage.Dungeon, 0), (online.Flow.Stage, online.Flow.Failures));
        online.Flow.CompleteOnlineDungeonAbort("online abort x");
        Assert.Equal((GameStage.Town, 1, "online abort x"), (online.Flow.Stage, online.Flow.Failures, online.Flow.History[^1].Reason));

        var offline = new Rig(onlineHook: false);
        offline.Flow.EnterTown("boot");
        var offlineRun = new StubRun(A.Path);
        offline.Flow.CompleteOnlineDungeonStart(A.Path, 0, offlineRun);
        offlineRun.Failed = true;
        Assert.True(offline.Flow.Poll());
        Assert.Equal((GameStage.Town, 1, "dungeon fail"), (offline.Flow.Stage, offline.Flow.Failures, offline.Flow.History[^1].Reason));
    }
}
