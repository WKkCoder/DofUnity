using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.DungeonSessionTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 副本难度在 Play 层的接线(M33,证据 E-125):难度名字、存档里的通关记录(dclear 行)、选择界面的 ←→ 选难度、流程把难度交给宿主工厂并在再次挑战时沿用、
/// 自动机爬难度、难度上限函数、通关后写进主角的记录与结算页的两行字、界面文字的翻译。怪物数值的算术另见 Dnf.Game.Tests 的 MonsterDifficultyTests,
/// 解锁规则本身另见 DungeonClearsTests。副本、评分门槛、等级全是合成的。
/// </summary>
public class DifficultyTests : IDisposable
{
    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly DungeonEntry B = new DungeonEntry("dungeon/test/b.dgn", "BBB");

    private readonly List<Dungeon> _dungeons = new List<Dungeon>();

    public void Dispose()
    {
        foreach (Dungeon d in _dungeons)
        {
            d.Dispose();
        }
    }

    // ------------------------------------------------------------------ 难度名字

    [Theory]
    [InlineData(0, "NORMAL")]
    [InlineData(1, "EXPERT")]
    [InlineData(2, "MASTER")]
    [InlineData(3, "KING")]
    [InlineData(4, "HERO")]
    [InlineData(-1, "?")]
    [InlineData(5, "?")]
    public void TheFiveNamesFollowTheDifficultyIndex(int difficulty, string name)
    {
        Assert.Equal(name, DifficultyNames.Of(difficulty));
    }

    [Fact]
    public void EveryNameAndTheNewTextsHaveAChineseTranslation()
    {
        for (int d = 0; d < DungeonSession.DifficultyCount; d++)
        {
            Assert.True(UiText.HasTranslation(DifficultyNames.Of(d)), DifficultyNames.Of(d));
            Assert.True(UiText.HasTranslation("DIFFICULTY " + DifficultyNames.Of(d)));
            Assert.True(UiText.HasTranslation("NEW LEVEL OPENED " + DifficultyNames.Of(d)));
        }
        Assert.True(UiText.HasTranslation("UP DOWN SELECT   LEFT RIGHT LEVEL   ENTER GO   ESC TOWN"));
        Assert.StartsWith("难度", UiText.Translate("DIFFICULTY EXPERT"));
        Assert.StartsWith("新开放难度", UiText.Translate("NEW LEVEL OPENED KING"));
    }

    // ------------------------------------------------------------------ 存档里的通关记录

    [Fact]
    public void TheClearRowsAreWrittenLastOnePerDungeonInTheOrderGiven()
    {
        var save = new HeroSave();
        save.Clears.Add(new SavedClear(4, 3));
        save.Clears.Add(new SavedClear(12, 1));
        string text = save.ToText();
        Assert.EndsWith("itemsgained 0\ndclear 4 3\ndclear 12 1\n", text);
        Assert.Equal(new HeroSave().ToText() + "dclear 4 3\ndclear 12 1\n", text);
    }

    [Fact]
    public void TheClearRowsRoundTripAndOldSavesWithoutThemHaveNone()
    {
        var save = new HeroSave { Level = 9 };
        save.Clears.Add(new SavedClear(7, 2));
        save.Clears.Add(new SavedClear(0, 4));
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave back, out string error), error);
        Assert.Equal(new[] { (7, 2), (0, 4) }, back.Clears.Select(c => (c.DungeonId, c.Opened)).ToArray());
        Assert.Equal(save.ToText(), back.ToText());

        Assert.True(HeroSave.TryParse("DNFUNITY-SAVE 1\nlevel 5\nbag 100 2\n", out HeroSave old, out _));
        Assert.Empty(old.Clears);
    }

    [Theory]
    [InlineData("dclear 4")]               // 少一个值
    [InlineData("dclear 4 1 2")]           // 多一个值
    [InlineData("dclear x 1")]
    [InlineData("dclear 4 x")]
    [InlineData("dclear -1 1")]            // 副本编号不能是负的
    [InlineData("dclear 4 0")]             // 记录值 0 等于没有记录,不存
    [InlineData("dclear 4 5")]             // 难度只有 0 到 4
    public void ABrokenClearRowRefusesTheWholeSaveAndNamesTheLine(string row)
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\n" + row + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("第 2 行", error);
        Assert.Contains("dclear", error);
    }

    [Fact]
    public void ASnapshotCarriesTheClearsSortedByDungeonAndRestoreReplacesThemWholesale()
    {
        HeroProgress p = ExpTests.Progress();
        p.Clears.Raise(30, 1);
        p.Clears.Raise(4, 3);
        HeroSave save = p.Snapshot();
        Assert.Equal(new[] { (4, 3), (30, 1) }, save.Clears.Select(c => (c.DungeonId, c.Opened)).ToArray());
        Assert.Equal(2, p.DungeonsCleared);

        HeroProgress other = ExpTests.Progress();
        other.Clears.Raise(99, 2);                                  // 还原是整个换掉:原来的记录不留
        other.Restore(save, code => null);
        Assert.Equal(new[] { (4, 3), (30, 1) }, other.Clears.Entries().Select(e => (e.Key, e.Value)).ToArray());
        Assert.Equal(0, other.Clears.Opened(99));
        Assert.Equal(save.ToText(), other.Snapshot().ToText());
    }

    [Fact]
    public void RestoringASaveWithoutClearsEmptiesTheRecord()
    {
        HeroProgress p = ExpTests.Progress();
        p.Clears.Raise(5, 2);
        p.Restore(new HeroSave(), code => null);
        Assert.Equal(0, p.DungeonsCleared);
    }

    [Fact]
    public void TheSaverWritesAgainWhenAClearIsRecorded()
    {
        HeroProgress p = ExpTests.Progress();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, p);
        Assert.True(saver.Flush(0));
        Assert.False(saver.Flush(1));
        p.Clears.Raise(7, 1);
        Assert.True(saver.Flush(2));
        Assert.Contains("dclear 7 1\n", store.Text);
        Assert.False(saver.Flush(3));                                // 同样的记录再抬一次不变
        p.Clears.Raise(7, 1);
        Assert.False(saver.Flush(4));
    }

    [Fact]
    public void TheHighestDifficultyIsTheRecordCappedByWhetherTheDungeonHasLevelThree()
    {
        HeroProgress p = ExpTests.Progress();
        p.Clears.Raise(7, 3);
        Assert.Equal(3, p.HighestDifficulty(7, true));
        Assert.Equal(2, p.HighestDifficulty(7, false));
        Assert.Equal(0, p.HighestDifficulty(8, true));
    }

    // ------------------------------------------------------------------ 难度上限函数

    private static DungeonEntry CardEntry(int dungeonId)
    {
        return new DungeonEntry("dungeon/test/d" + dungeonId + ".dgn", "D" + dungeonId, new DungeonCard(dungeonId, 100, 100, 0, null, 0, 0));
    }

    [Fact]
    public void TheGateReadsTheHeroRecordThroughTheCardsDungeonNumber()
    {
        HeroProgress p = ExpTests.Progress();
        p.Clears.Set(7, 3);
        p.Clears.Set(8, 1);
        Func<DungeonEntry, int> gate = DifficultyGate.For(() => p, null);
        Assert.Equal(2, gate(CardEntry(7)));                         // 没有 assets 就没有难度 3 的清单:最多到 2
        Assert.Equal(1, gate(CardEntry(8)));
        Assert.Equal(0, gate(CardEntry(9)));
    }

    [Fact]
    public void TheGateGivesDifficultyZeroWithoutProgressWithoutADungeonNumberOrForNothing()
    {
        HeroProgress p = ExpTests.Progress();
        p.Clears.Set(7, 2);
        Assert.Equal(0, DifficultyGate.For(() => null, null)(CardEntry(7)));
        Assert.Equal(0, DifficultyGate.For(null, null)(CardEntry(7)));
        Assert.Equal(0, DifficultyGate.For(() => p, null)(A));       // 没有卡片、又没有 assets 去按路径查编号
        Assert.Equal(0, DifficultyGate.For(() => p, null)(null));
    }

    [Fact]
    public void TheGateAsksForTheProgressEachTimeBecauseItMayBeCreatedAfterTheFlow()
    {
        HeroProgress p = null;
        Func<DungeonEntry, int> gate = DifficultyGate.For(() => p, null);
        Assert.Equal(0, gate(CardEntry(7)));
        p = ExpTests.Progress();
        p.Clears.Set(7, 1);
        Assert.Equal(1, gate(CardEntry(7)));
    }

    // ------------------------------------------------------------------ 选择界面

    private static Func<DungeonEntry, int> Limits(int a, int b) => e => e.Path == A.Path ? a : b;

    [Fact]
    public void WithoutALimitOnlyDifficultyZeroIsSelectable()
    {
        var screen = new DungeonSelectScreen(new[] { A, B });
        Assert.Equal((0, 0), (screen.Difficulty, screen.MaxDifficulty));
        screen.MoveDifficulty(1);
        Assert.Equal(0, screen.Difficulty);
    }

    [Fact]
    public void LeftAndRightMoveWithinTheLimitWithoutWrapping()
    {
        var screen = new DungeonSelectScreen(new[] { A, B }, 0, null, Limits(2, 0));
        Assert.Equal(2, screen.MaxDifficulty);
        screen.MoveDifficulty(-1);
        Assert.Equal(0, screen.Difficulty);                          // 到头不循环
        screen.MoveDifficulty(1);
        screen.MoveDifficulty(1);
        Assert.Equal(2, screen.Difficulty);
        screen.MoveDifficulty(1);
        Assert.Equal(2, screen.Difficulty);
        screen.MoveDifficulty(-9);
        Assert.Equal(0, screen.Difficulty);
        screen.MoveDifficulty(9);
        Assert.Equal(2, screen.Difficulty);                          // 一次跳多档也夹在上限里
    }

    [Fact]
    public void ALimitAboveTheScreenCeilingIsClampedToIt()
    {
        var screen = new DungeonSelectScreen(new[] { A }, 0, null, e => 9);
        Assert.Equal(DungeonSelectScreen.MaxDifficultyIndex, screen.MaxDifficulty);
        screen.MoveDifficulty(99);
        Assert.Equal(DungeonSelectScreen.MaxDifficultyIndex, screen.Difficulty);
        Assert.Equal(0, new DungeonSelectScreen(new[] { A }, 0, null, e => -3).MaxDifficulty);
    }

    [Fact]
    public void MovingToADungeonWithALowerLimitLowersTheDifficultyAndMovingBackDoesNotRaiseIt()
    {
        var screen = new DungeonSelectScreen(new[] { A, B }, 0, null, Limits(3, 1));
        screen.MoveDifficulty(3);
        Assert.Equal(3, screen.Difficulty);
        screen.Move(1);
        Assert.Equal((1, 1), (screen.Index, screen.Difficulty));
        screen.Move(-1);
        Assert.Equal((0, 1), (screen.Index, screen.Difficulty));     // 回去不自动升回 3
        Assert.Equal(3, screen.MaxDifficulty);
    }

    [Fact]
    public void TheStartingDifficultyIsClampedIntoTheSelectedDungeonsRange()
    {
        Assert.Equal(1, new DungeonSelectScreen(new[] { A }, 0, null, e => 1, 3).Difficulty);
        Assert.Equal(0, new DungeonSelectScreen(new[] { A }, 0, null, e => 1, -2).Difficulty);
        Assert.Equal(0, new DungeonSelectScreen(new[] { A }, 0, null, null, 2).Difficulty);       // 没有上限
        Assert.Equal(2, new DungeonSelectScreen(new[] { A, B }, 1, null, Limits(0, 3), 2).Difficulty);
    }

    [Fact]
    public void AnOutcomeOrAnEmptyListEndsTheResponseToLeftAndRight()
    {
        var screen = new DungeonSelectScreen(new[] { A }, 0, null, e => 3);
        screen.MoveDifficulty(2);
        screen.Confirm();
        screen.MoveDifficulty(1);
        screen.MoveDifficulty(-2);
        Assert.Equal((SelectOutcome.Enter, 2), (screen.Outcome, screen.Difficulty));

        var empty = new DungeonSelectScreen(new DungeonEntry[0], 0, null, e => 3);
        empty.MoveDifficulty(1);
        Assert.Equal((0, 0), (empty.Difficulty, empty.MaxDifficulty));
    }

    [Fact]
    public void ThePlaceholderScreenShowsTheDifficultyLineOnlyWhenSomethingIsSelected()
    {
        var screen = new DungeonSelectScreen(new[] { A, B }, 0, null, e => 2);
        Assert.Single(screen.Build(0), s => s.AnimationPath == "select-difficulty");
        Assert.DoesNotContain(new DungeonSelectScreen(new DungeonEntry[0]).Build(0), s => s.AnimationPath == "select-difficulty");
        // 难度改了画出来的字也跟着变(合成字体里每个字的宽度一样,所以看像素)
        SceneSprite before = screen.Build(0).First(s => s.AnimationPath == "select-difficulty");
        screen.MoveDifficulty(2);
        SceneSprite after = screen.Build(0).First(s => s.AnimationPath == "select-difficulty");
        Assert.False(before.Frame.Rgba.AsSpan().SequenceEqual(after.Frame.Rgba) && before.Frame.Width == after.Frame.Width);
    }

    // ------------------------------------------------------------------ 流程

    private sealed class Run : GameFlowTests.FakeHero, IRunSession
    {
        public int Difficulty { get; }

        public int Number { get; }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }

        public Run(string path, int number, int difficulty) : base("run " + path + " #" + number)
        {
            Number = number;
            Difficulty = difficulty;
        }
    }

    private static GameFlow NewFlow(List<(string Path, int Run, int Difficulty)> started, Func<DungeonEntry, int> limit = null, bool byDifficulty = true)
    {
        var flow = new GameFlow(
            new LegacyTownWorld(() => new GameFlowTests.FakeHero("town")),
            (path, run) =>
            {
                started.Add((path, run, -1));                       // 老式两参工厂:没有难度可记
                return new Run(path, run, 0);
            },
            new[] { A, B },
            null,
            null,
            null,
            byDifficulty
                ? (path, run, difficulty) =>
                {
                    started.Add((path, run, difficulty));
                    return new Run(path, run, difficulty);
                }
                : null);
        flow.DifficultyLimit = limit;
        return flow;
    }

    [Fact]
    public void ConfirmingOnTheSelectScreenHandsTheChosenDifficultyToTheHostFactory()
    {
        var started = new List<(string, int, int)>();
        GameFlow flow = NewFlow(started, Limits(2, 3));
        flow.EnterTown();
        flow.EnterSelect();
        Assert.Equal(0, flow.Select.Difficulty);
        Assert.Equal(2, flow.Select.MaxDifficulty);
        flow.Select.MoveDifficulty(1);
        flow.Select.MoveDifficulty(1);
        flow.Select.Confirm();
        Assert.True(flow.Poll());

        Assert.Equal(new[] { (A.Path, 0, 2) }, started);
        Assert.Equal(2, flow.Difficulty);
        Assert.Equal(2, Assert.IsType<Run>(flow.Run).Difficulty);
        Assert.Contains("select AAA", flow.History[^1].Reason);
    }

    [Fact]
    public void WithoutALimitTheFlowStartsDifficultyZeroAndTheLimitPropertyDefaultsToNothing()
    {
        var started = new List<(string, int, int)>();
        GameFlow flow = NewFlow(started);
        Assert.Null(flow.DifficultyLimit);
        flow.EnterTown();
        flow.EnterSelect();
        flow.Select.MoveDifficulty(3);
        flow.Select.Confirm();
        flow.Poll();
        Assert.Equal(new[] { (A.Path, 0, 0) }, started);
    }

    [Fact]
    public void RetryKeepsTheDifficultyAndTheNextSelectScreenOpensOnIt()
    {
        var started = new List<(string, int, int)>();
        GameFlow flow = NewFlow(started, e => 3);
        flow.EnterTown();
        flow.EnterSelect();
        flow.Select.MoveDifficulty(2);
        flow.Select.Confirm();
        flow.Poll();

        var run = (Run)flow.Run;
        run.Choice = SettlementChoice.Retry;
        Assert.True(flow.Poll());
        Assert.Equal(new[] { (A.Path, 0, 2), (A.Path, 1, 2) }, started);          // 再来一次沿用难度
        Assert.Equal(2, flow.Difficulty);

        ((Run)flow.Run).Choice = SettlementChoice.SelectDungeon;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(2, flow.Select.Difficulty);                                  // 回到选择界面还停在上次的难度上
    }

    [Fact]
    public void TheNextSelectScreenLowersARememberedDifficultyThatTheLimitNoLongerAllows()
    {
        var started = new List<(string, int, int)>();
        int cap = 3;
        GameFlow flow = NewFlow(started, e => cap);
        flow.EnterTown();
        flow.EnterSelect();
        flow.Select.MoveDifficulty(3);
        flow.Select.Confirm();
        flow.Poll();
        cap = 1;                                                                  // 比如换了角色:记录变少了
        ((Run)flow.Run).Choice = SettlementChoice.SelectDungeon;
        flow.Poll();
        Assert.Equal((1, 1), (flow.Select.Difficulty, flow.Select.MaxDifficulty));
    }

    [Fact]
    public void WithoutTheDifficultyFactoryTheTwoArgumentOneStillBuildsTheRunAndTheDifficultyIsKept()
    {
        var started = new List<(string, int, int)>();
        GameFlow flow = NewFlow(started, e => 3, byDifficulty: false);
        flow.EnterTown();
        flow.EnterSelect();
        flow.Select.MoveDifficulty(2);
        flow.Select.Confirm();
        flow.Poll();
        Assert.Equal(new[] { (A.Path, 0, -1) }, started);
        Assert.Equal(2, flow.Difficulty);                                          // 宿主可以自己读
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(99, 4)]
    public void StartDungeonClampsTheDifficultyToTheLegalRangeAndDoesNotCheckTheRecord(int asked, int expected)
    {
        var started = new List<(string, int, int)>();
        GameFlow flow = NewFlow(started);                                          // 没有上限:选择界面只有 0,但直接指定不受限
        flow.EnterTown();
        flow.StartDungeon(B.Path, "test", asked);
        Assert.Equal(new[] { (B.Path, 0, expected) }, started);
        Assert.Equal(expected, flow.Difficulty);
    }

    // ------------------------------------------------------------------ 自动机

    private static void DriveToDungeon(FlowBot bot, GameFlow flow)
    {
        for (int i = 0; i < 6000 && flow.Stage != GameStage.Dungeon; i++)
        {
            bot.Advance(flow, 1000.0 / 60);
        }
        Assert.Equal(GameStage.Dungeon, flow.Stage);
    }

    private static int BotDifficulty(bool raise, Func<DungeonEntry, int> limit)
    {
        var started = new List<(string, int, int)>();
        GameFlow flow = NewFlow(started, limit);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown }) { RaiseDifficulty = raise };
        flow.EnterTown("start");
        DriveToDungeon(bot, flow);
        return Assert.Single(started).Item3;
    }

    [Fact]
    public void TheBotRaisesTheDifficultyToTheHighestOpenOneOnlyWhenAsked()
    {
        Assert.Equal(0, BotDifficulty(false, e => 3));             // 默认关:难度 0
        Assert.Equal(2, BotDifficulty(true, e => 2));
        Assert.Equal(3, BotDifficulty(true, e => 9));              // 夹在界面的最高档
        Assert.Equal(0, BotDifficulty(true, null));                // 没有上限也就只有 0
        Assert.Equal(0, BotDifficulty(true, e => 0));
    }

    // ------------------------------------------------------------------ 通关记录与结算页

    private static RankSystem Rank(int threshold)
    {
        var steps = new[] { new TimeBonusStep(0, 1) };
        var averages = new[] { (7, 1, new[] { 100000, 110000, 120000, 130000, 140000 }) };
        return new RankSystem(new[] { threshold, threshold, threshold, threshold, threshold, threshold, threshold, threshold }, steps, averages);
    }

    /// <summary>门槛 0:任何评分点都够最高档(SSS)。</summary>
    private static RankSystem TopRank() => Rank(0);

    /// <summary>门槛高得够不到:评分永远是最低档(F)。</summary>
    private static RankSystem BottomRank() => Rank(100000);

    private static HeroProgress Hero(int level = 1) => ExpTests.Progress(level: level, mobExp: 400, mobLevel: 1);

    private static (int X, int Y)[] Monsters(int cx, int cy)
    {
        return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
            : cx == 1 && cy == 0 ? new[] { (300, 200) }
            : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
            : None();
    }

    /// <summary>用合成副本打到通关,返回会话;<paramref name="setup"/> 在打之前接评分与记录。</summary>
    private DungeonSession Clear(HeroProgress progress, Action<DungeonSession> setup)
    {
        var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        _dungeons.Add(d);
        DungeonSession s = d.Session;
        setup(s);
        new DungeonBot(true).RunUntil(s, 600_000);
        Assert.True(s.BossCleared);
        Assert.NotNull(s.Result);
        return s;
    }

    private int RewardLines(DungeonSession s)
    {
        for (int i = 0; i < 600 && !s.SettlementActive; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.True(s.SettlementActive);
        return s.Build(0).Count(x => x.AnimationPath == "reward");
    }

    [Fact]
    public void ClearingDifficultyZeroRecordsItOpensDifficultyOneAndSaysSo()
    {
        HeroProgress p = Hero();
        DungeonSession s = Clear(p, x =>
        {
            x.UseRanking(BottomRank(), 7, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(1, s.Result.OpenedDifficulty);
        Assert.Equal(1, p.Clears.Opened(7));
        Assert.Equal(1, p.HighestDifficulty(7, true));
        Assert.Equal(0, s.Difficulty);
    }

    [Fact]
    public void AnSOrBetterOnDifficultyOneOpensDifficultyTwoAndAWorseGradeDoesNot()
    {
        HeroProgress good = Hero();
        DungeonSession a = Clear(good, x =>
        {
            x.UseRanking(TopRank(), 7, 1, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(8, a.Result.Grade);
        Assert.Equal(2, a.Result.OpenedDifficulty);
        Assert.Equal(2, good.Clears.Opened(7));
        Assert.Equal(1, a.Difficulty);

        HeroProgress poor = Hero();
        DungeonSession b = Clear(poor, x =>
        {
            x.UseRanking(BottomRank(), 7, 1, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(0, b.Result.Grade);
        Assert.Equal(0, b.Result.OpenedDifficulty);
        Assert.Equal(0, poor.Clears.Opened(7));
    }

    [Fact]
    public void ARepeatClearOpensNothingNewAndTheRecordNeverGoesDown()
    {
        HeroProgress p = Hero();
        p.Clears.Set(7, 2);
        DungeonSession s = Clear(p, x =>
        {
            x.UseRanking(BottomRank(), 7, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(0, s.Result.OpenedDifficulty);
        Assert.Equal(2, p.Clears.Opened(7));
    }

    [Fact]
    public void WithoutUseClearRecordNothingIsRecorded()
    {
        HeroProgress p = Hero();
        DungeonSession s = Clear(p, x => x.UseRanking(TopRank(), 7, 1));
        Assert.Equal(0, s.Result.OpenedDifficulty);
        Assert.Equal(0, p.DungeonsCleared);
    }

    [Fact]
    public void ADungeonWithoutANumberAndAHeroBelowTheMinimumLevelLeaveNoRecord()
    {
        HeroProgress p = Hero();
        DungeonSession s = Clear(p, x =>
        {
            x.UseRanking(TopRank(), -1, 1);                        // 副本不在 dungeon.lst 里:没有编号
            x.UseClearRecord(1, true);
        });
        Assert.Equal(0, s.Result.OpenedDifficulty);
        Assert.Equal(0, p.DungeonsCleared);

        HeroProgress low = Hero();
        DungeonSession t = Clear(low, x =>
        {
            x.UseRanking(TopRank(), 7, 1);
            x.UseClearRecord(50, true);                           // 主角 1 级,副本要 50 级
        });
        Assert.Equal(0, t.Result.OpenedDifficulty);
        Assert.Equal(0, low.DungeonsCleared);
    }

    [Fact]
    public void ARunWithoutHeroProgressIsStillSettledAndRecordsNothing()
    {
        DungeonSession s = Clear(null, x =>
        {
            x.UseRanking(TopRank(), 7, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(0, s.Result.OpenedDifficulty);
    }

    [Fact]
    public void TheSettlementPanelAddsALineForTheDifficultyAndForANewlyOpenedOne()
    {
        // 基准:难度 0、没接记录:没有这两行
        DungeonSession plain = Clear(Hero(), x => x.UseRanking(BottomRank(), 7, 1));
        int baseLines = RewardLines(plain);

        // 难度 1:多一行"难度"(结算里没接记录,所以没有"新开放")
        DungeonSession hard = Clear(Hero(), x => x.UseRanking(BottomRank(), 7, 1, 1));
        Assert.Equal(baseLines + 1, RewardLines(hard));

        // 难度 0 + 记录:通关抬高了记录,多一行"新开放"
        DungeonSession opened = Clear(Hero(), x =>
        {
            x.UseRanking(BottomRank(), 7, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(baseLines + 1, RewardLines(opened));

        // 难度 1 + S 以上:两行都有
        DungeonSession both = Clear(Hero(), x =>
        {
            x.UseRanking(TopRank(), 7, 1, 1);
            x.UseClearRecord(1, true);
        });
        Assert.Equal(baseLines + 2, RewardLines(both));
    }
}
