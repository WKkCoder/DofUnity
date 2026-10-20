using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 游戏流程骨架(<see cref="GameFlow"/>、<see cref="DungeonSelectScreen"/>):阶段之间怎么切、结算菜单的三个选项各去哪里、局序号与记录。
/// 会话是假的(不带任何原版数据),验证的只是流程的规则。
/// </summary>
public class GameFlowTests
{
    internal class FakeHero : IHeroSession
    {
        public string Label { get; }

        public FakeHero(string label)
        {
            Label = label;
        }

        public string MapPath => "fake/" + Label;

        public TownHero Hero => null;

        public TownCamera Camera => null;

        public int LayerCount => 0;

        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
        }

        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    private sealed class FakeRun : FakeHero, IRunSession
    {
        public string Path { get; }

        public int Run { get; }

        public SettlementChoice Choice { get; set; }

        public FakeRun(string path, int run) : base("run " + path + " #" + run)
        {
            Path = path;
            Run = run;
        }
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly DungeonEntry B = new DungeonEntry("dungeon/test/b.dgn", "BBB");

    private static GameFlow NewFlow(List<(string Path, int Run)> started = null, List<FakeHero> towns = null)
    {
        return new GameFlow(
            () =>
            {
                var town = new FakeHero("town #" + (towns?.Count ?? 0));
                towns?.Add(town);
                return town;
            },
            (path, run) =>
            {
                started?.Add((path, run));
                return new FakeRun(path, run);
            },
            new[] { A, B });
    }

    [Fact]
    public void ANewFlowHasNotStartedAndPollingItDoesNothing()
    {
        GameFlow flow = NewFlow();
        Assert.Equal(0, flow.StageSerial);
        Assert.Null(flow.Scene);
        Assert.Null(flow.Run);
        Assert.False(flow.Poll());
        Assert.Empty(flow.History);
        Assert.Equal((0, 0), (flow.Runs, flow.Settlements));
    }

    [Fact]
    public void EnteringTheTownBuildsATownSessionAndRecordsTheStart()
    {
        var towns = new List<FakeHero>();
        GameFlow flow = NewFlow(towns: towns);
        flow.EnterTown("start");

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(1, flow.StageSerial);
        Assert.Same(towns[0], flow.Session);
        Assert.Same(towns[0], flow.Scene);
        Assert.Null(flow.Run);
        Assert.Null(flow.Select);
        Assert.False(flow.Poll());                       // 城镇本身没有出口;进副本选择由宿主调 EnterSelect
        Assert.Single(flow.History);
        Assert.Equal(1, flow.History[0].Serial);
        Assert.Equal("start", flow.History[0].Reason);
    }

    [Fact]
    public void ConfirmingADungeonOnTheSelectScreenStartsItAsRunZero()
    {
        var started = new List<(string Path, int Run)>();
        GameFlow flow = NewFlow(started);
        flow.EnterTown();
        flow.EnterSelect();

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Null(flow.Session);
        Assert.Same(flow.Select, flow.Scene);
        Assert.Equal(new[] { A, B }, flow.Select.Entries);
        Assert.Equal(0, flow.Select.Index);                          // 没打过任何副本:选第一个
        Assert.False(flow.Poll());                                   // 还没确认

        flow.Select.Move(1);
        flow.Select.Confirm();
        Assert.True(flow.Poll());

        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(B.Path, flow.DungeonPath);
        Assert.Equal(1, flow.Runs);
        Assert.Equal(new[] { (B.Path, 0) }, started);
        FakeRun run = Assert.IsType<FakeRun>(flow.Run);
        Assert.Same(run, flow.Session);
        Assert.Null(flow.Select);
        Assert.Contains("select BBB", flow.History[^1].Reason);
        Assert.False(flow.Poll());                                   // 结算还没选:什么也不发生
    }

    [Fact]
    public void GoingBackFromTheSelectScreenReturnsToANewTownSession()
    {
        var towns = new List<FakeHero>();
        GameFlow flow = NewFlow(towns: towns);
        flow.EnterTown();
        flow.EnterSelect();
        flow.Select.Back();
        Assert.True(flow.Poll());

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(2, towns.Count);
        Assert.Same(towns[1], flow.Session);
        Assert.Equal(3, flow.StageSerial);
        Assert.Equal(0, flow.Runs);
        Assert.Equal("select Back", flow.History[^1].Reason);
    }

    [Fact]
    public void RetryRestartsTheSameDungeonAsTheNextRunAndTheOldRunIsReplaced()
    {
        var started = new List<(string Path, int Run)>();
        GameFlow flow = NewFlow(started);
        flow.StartDungeon(A.Path, "start");
        FakeRun first = (FakeRun)flow.Run;
        first.Choice = SettlementChoice.Retry;

        Assert.True(flow.Poll());
        FakeRun second = (FakeRun)flow.Run;
        Assert.NotSame(first, second);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(2, flow.StageSerial);                          // 同一座副本重开也算换了一次(宿主要换视图)
        Assert.Equal(2, flow.Runs);
        Assert.Equal(1, flow.Settlements);
        Assert.Equal(new[] { (A.Path, 0), (A.Path, 1) }, started);
        Assert.Equal(SettlementChoice.None, second.Choice);
        Assert.False(flow.Poll());                                  // 新的一局还没结算:选择不会被重复执行
        Assert.Equal(2, flow.StageSerial);
    }

    [Fact]
    public void SelectDungeonOpensTheSelectScreenWithTheJustPlayedDungeonHighlighted()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(B.Path);
        ((FakeRun)flow.Run).Choice = SettlementChoice.SelectDungeon;

        Assert.True(flow.Poll());
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(1, flow.Select.Index);
        Assert.Equal(B, flow.Select.Selected);
        Assert.Equal(1, flow.Settlements);
        Assert.Null(flow.Session);
        Assert.Null(flow.Run);

        flow.Select.Move(-1);
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal(A.Path, flow.DungeonPath);
        Assert.Equal(2, flow.Runs);
    }

    [Fact]
    public void ReturnToTownLeavesTheDungeonAndKeepsTheCounts()
    {
        var towns = new List<FakeHero>();
        GameFlow flow = NewFlow(towns: towns);
        flow.StartDungeon(A.Path);
        ((FakeRun)flow.Run).Choice = SettlementChoice.ReturnToTown;

        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Same(towns[0], flow.Session);
        Assert.Null(flow.Run);
        Assert.Equal((1, 1), (flow.Runs, flow.Settlements));
        Assert.Equal(A.Path, flow.DungeonPath);                       // 记着最近打的那座,再进选择界面时选中它
        flow.EnterSelect();
        Assert.Equal(0, flow.Select.Index);
    }

    [Fact]
    public void QuickPartyAndNoChoiceDoNothing()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path);
        FakeRun run = (FakeRun)flow.Run;

        Assert.False(flow.Poll());
        run.Choice = SettlementChoice.QuickParty;                     // 原版没有队伍时是灰的;流程没有对应的去向
        Assert.False(flow.Poll());
        Assert.Equal((GameStage.Dungeon, 1, 0), (flow.Stage, flow.StageSerial, flow.Settlements));
    }

    [Fact]
    public void AFullTourRecordsEveryTransitionInOrder()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        flow.EnterSelect("gate");
        flow.Select.Confirm();
        Assert.True(flow.Poll());                                     // A,第 0 局
        ((FakeRun)flow.Run).Choice = SettlementChoice.Retry;
        Assert.True(flow.Poll());                                     // A,第 1 局
        ((FakeRun)flow.Run).Choice = SettlementChoice.SelectDungeon;
        Assert.True(flow.Poll());
        flow.Select.Move(1);
        flow.Select.Confirm();
        Assert.True(flow.Poll());                                     // B,第 2 局
        ((FakeRun)flow.Run).Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());

        Assert.Equal(
            new[]
            {
                (GameStage.Town, GameStage.Town, "start"),
                (GameStage.Town, GameStage.DungeonSelect, "gate"),
                (GameStage.DungeonSelect, GameStage.Dungeon, "select AAA"),
                (GameStage.Dungeon, GameStage.Dungeon, "choice Retry"),
                (GameStage.Dungeon, GameStage.DungeonSelect, "choice SelectDungeon"),
                (GameStage.DungeonSelect, GameStage.Dungeon, "select BBB"),
                (GameStage.Dungeon, GameStage.Town, "choice ReturnToTown"),
            },
            flow.History.Select(e => (e.From, e.To, e.Reason)).ToArray());
        Assert.Equal(Enumerable.Range(1, 7), flow.History.Select(e => e.Serial));
        Assert.Equal((3, 3, 7), (flow.Runs, flow.Settlements, flow.StageSerial));
    }

    [Fact]
    public void TheFlowRejectsMissingPartsAndAnEmptyDungeonPath()
    {
        Assert.Throws<ArgumentNullException>(() => new GameFlow(null, (p, r) => null, new DungeonEntry[0]));
        Assert.Throws<ArgumentNullException>(() => new GameFlow(() => null, null, new DungeonEntry[0]));
        Assert.Throws<ArgumentNullException>(() => new GameFlow(() => null, (p, r) => null, null));
        GameFlow flow = NewFlow();
        Assert.Throws<ArgumentException>(() => flow.StartDungeon(""));
    }

    // ---- 副本选择界面 ----

    [Fact]
    public void TheSelectionMovesWithinTheListWithoutWrappingAndAConfirmedOrCancelledScreenStopsResponding()
    {
        var screen = new DungeonSelectScreen(new[] { A, B });
        Assert.Equal((0, SelectOutcome.None), (screen.Index, screen.Outcome));
        screen.Move(-1);
        Assert.Equal(0, screen.Index);                                // 到头不循环
        screen.Move(1);
        screen.Move(1);
        Assert.Equal(1, screen.Index);
        Assert.Equal(B, screen.Selected);

        screen.Confirm();
        Assert.Equal(SelectOutcome.Enter, screen.Outcome);
        screen.Back();                                                // 已经有结果,不改
        screen.Move(-1);
        Assert.Equal((SelectOutcome.Enter, 1), (screen.Outcome, screen.Index));

        var cancelled = new DungeonSelectScreen(new[] { A, B });
        cancelled.Back();
        cancelled.Confirm();
        Assert.Equal(SelectOutcome.Back, cancelled.Outcome);
    }

    [Fact]
    public void AnEmptyListHasNothingToSelectOrEnterButCanStillGoBack()
    {
        var screen = new DungeonSelectScreen(new DungeonEntry[0]);
        Assert.Equal(-1, screen.Index);
        Assert.Null(screen.Selected);
        screen.Move(1);
        screen.Confirm();
        Assert.Equal(SelectOutcome.None, screen.Outcome);
        Assert.Contains(screen.Build(0), s => s.AnimationPath == "select-empty");
        screen.Back();
        Assert.Equal(SelectOutcome.Back, screen.Outcome);
    }

    [Fact]
    public void TheStartingIndexIsClampedIntoTheList()
    {
        Assert.Equal(1, new DungeonSelectScreen(new[] { A, B }, 7).Index);
        Assert.Equal(0, new DungeonSelectScreen(new[] { A, B }, -3).Index);
    }

    [Fact]
    public void TheDungeonNameDefaultsToTheUpperCasedFileNameWithoutItsExtension()
    {
        Assert.Equal("LORIEN", DungeonEntry.FromPath("dungeon/act1/lorien.dgn"));
        Assert.Equal("LORIEN", DungeonEntry.FromPath("dungeon\\act1\\lorien.dgn"));
        Assert.Equal("X", DungeonEntry.FromPath("x"));
        Assert.Equal(string.Empty, DungeonEntry.FromPath(""));
        Assert.Equal("LORIEN", new DungeonEntry("dungeon/act1/lorien.dgn", null).Name);
    }

    [Fact]
    public void TheSelectScreenDrawsABackdropATitleOneNamePerDungeonTheHighlightOnTheSelectedOneAndTheHint()
    {
        var screen = new DungeonSelectScreen(new[] { A, B }, 1);
        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal("select-backdrop", sprites[0].AnimationPath);
        Assert.Equal((800, 600), (sprites[0].Frame.Width, sprites[0].Frame.Height));
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(2, sprites.Count(s => s.AnimationPath == "select-name"));
        SceneSprite highlight = Assert.Single(sprites, s => s.AnimationPath == "select-highlight");
        SceneSprite selectedName = sprites.Where(s => s.AnimationPath == "select-name").ElementAt(1);
        Assert.True(highlight.ScreenY < selectedName.ScreenY + selectedName.Frame.Height && selectedName.ScreenY < highlight.ScreenY + highlight.Frame.Height);   // 高亮框罩住选中的那一行
        Assert.Contains(sprites, s => s.AnimationPath == "select-title");
        Assert.Contains(sprites, s => s.AnimationPath == "select-hint");

        // 画的次序:底、标题、(高亮、名字)……、提示;选中项变了,高亮跟着走
        Assert.Equal(sprites.Select(s => s.SortKey).OrderBy(k => k).ToArray(), sprites.Select(s => s.SortKey).ToArray());
        screen.Move(-1);
        SceneSprite moved = Assert.Single(screen.Build(0), s => s.AnimationPath == "select-highlight");
        Assert.True(moved.ScreenY < highlight.ScreenY);

        // 同样的内容总是同一个帧对象(GPU 按对象缓存纹理);放在 UI 大类里,所有画面一致
        Assert.Same(sprites[0].Frame, screen.Build(5).First().Frame);
    }
}
