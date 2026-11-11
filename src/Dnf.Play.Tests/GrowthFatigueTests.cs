using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.DungeonSessionTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角的疲劳在 Play 层的接线(r15-growth;规则与证据见 Dnf.Game 的 Fatigue 与 DungeonFatigueRules 文件头):主角身上的疲劳与注入的时钟、存档的一行 fatigue、
/// 疲劳条数据、进副本门槛、流程在选择界面与再次挑战时的拦截、副本会话里进副本与每换一次房间的扣除。
/// 时间全是合成的固定时刻,上限与恢复整点用小数字;副本是合成的 2 × 2 的环(见 DungeonSessionTests),数据与原版无关。
/// </summary>
public class GrowthFatigueTests
{
    private static readonly DateTime Noon = new DateTime(2026, 3, 10, 12, 0, 0);

    private sealed class TestClock
    {
        public DateTime Now = Noon;
    }

    /// <summary>主角:上限 <paramref name="max"/>、6 点恢复,时钟用 <paramref name="clock"/>;已经刷新过一次(记下了上次玩的时刻),所以接着的扣除不会被"第一次刷新"清掉。</summary>
    private static HeroProgress Hero(TestClock clock, int max = 5, int level = 1)
    {
        HeroProgress hero = ExpTests.Progress(level: level);
        hero.ConfigureFatigue(new FatigueRules(max, 6));
        hero.Clock = () => clock.Now;
        hero.RefreshFatigue();
        return hero;
    }

    private static ScriptLine Flag(string tag) => new ScriptLine(tag, new ScriptValue[0]);

    private static ScriptLine Ints(string tag, params int[] values)
    {
        var v = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            v[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, v);
    }

    // ------------------------------------------------------------------ 主角身上的疲劳

    [Fact]
    public void TheCapIsTheSameAtEveryLevel()
    {
        foreach (int level in new[] { 1, 17, 50, Leveling.MaxLevel })
        {
            HeroProgress hero = ExpTests.Progress(level: level);
            Assert.Equal((FatigueRules.DefaultMax, FatigueRules.DefaultMax, 0), (hero.FatigueMax, hero.FatigueRemaining, hero.FatigueConsumed));
        }
    }

    [Fact]
    public void ChargingStopsAtTheCapAndReportsTheRealAmount()
    {
        HeroProgress hero = Hero(new TestClock(), max: 5);
        Assert.Equal(2, hero.ChargeFatigue(2));
        Assert.Equal(3, hero.ChargeFatigue(9));                   // 只剩 3 点空间
        Assert.Equal((5, 0, 5), (hero.FatigueConsumed, hero.FatigueRemaining, hero.FatigueMax));
        Assert.Equal(0, hero.ChargeFatigue(1));
        Assert.Equal(0, hero.ChargeFatigue(0));
        Assert.Equal(0, hero.ChargeFatigue(-3));
    }

    [Fact]
    public void JustEnoughPointsGetInAndJustShortDoNot()
    {
        HeroProgress hero = Hero(new TestClock(), max: 5);
        hero.ChargeFatigue(4);
        Assert.True(hero.CanEnterByFatigue());                    // 剩 1 点:恰好够
        hero.ChargeFatigue(1);
        Assert.False(hero.CanEnterByFatigue());                   // 剩 0 点:差一点
    }

    [Fact]
    public void TheDailyResetRecoversExactlyAtTheBoundary()
    {
        var clock = new TestClock { Now = new DateTime(2026, 3, 10, 20, 0, 0) };
        HeroProgress hero = Hero(clock, max: 5);
        hero.ChargeFatigue(5);
        clock.Now = new DateTime(2026, 3, 11, 5, 59, 59);
        Assert.False(hero.CanEnterByFatigue());                   // 恢复点前一秒
        clock.Now = new DateTime(2026, 3, 11, 6, 0, 0);
        Assert.True(hero.CanEnterByFatigue());                    // 恢复点上
        Assert.Equal((0, 5), (hero.FatigueConsumed, hero.FatigueRemaining));
    }

    [Fact]
    public void ChargingAfterTheBoundaryRecoversFirstAndThenCharges()
    {
        var clock = new TestClock { Now = new DateTime(2026, 3, 10, 20, 0, 0) };
        HeroProgress hero = Hero(clock, max: 5);
        hero.ChargeFatigue(5);
        clock.Now = new DateTime(2026, 3, 11, 7, 0, 0);
        Assert.Equal(1, hero.ChargeFatigue(1));                   // 先恢复成 0,再扣 1;而不是被封顶挡成 0
        Assert.Equal(1, hero.FatigueConsumed);
    }

    [Fact]
    public void TheHudDataFollowsTheStateAndTheFatigueVersionNeverTouchesTheStatsVersion()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        int stats = hero.Version;
        int fatigue = hero.FatigueVersion;
        hero.ChargeFatigue(4);
        Assert.Equal(fatigue + 1, hero.FatigueVersion);
        Assert.Equal(stats, hero.Version);                        // 扣疲劳不让战斗会话重算面板
        HudFatigue hud = hero.FatigueHud;
        Assert.Equal((6, 10, 4, 0.6, false), (hud.Remaining, hud.Max, hud.Consumed, hud.Ratio, hud.Empty));
        hero.ChargeFatigue(6);
        Assert.True(hero.FatigueHud.Empty);
    }

    [Fact]
    public void ConfigureFatigueClampsTheConsumedPointsAndKeepsTheStampAndTheCurrentCapIsTheNewOne()
    {
        var clock = new TestClock();
        HeroProgress hero = Hero(clock, max: 10);
        hero.ChargeFatigue(8);
        hero.ConfigureFatigue(new FatigueRules(5, 6));
        Assert.Equal((5, 0, 5), (hero.FatigueConsumed, hero.FatigueRemaining, hero.FatigueMax));
        Assert.Equal(Noon, hero.Fatigue.LastPlay);                // 上次玩的时刻留着,所以不会因为换规则就当作到点
        Assert.False(hero.RefreshFatigue());
        hero.ConfigureFatigue(new FatigueRules(20, 6));
        Assert.Equal((5, 15, 20), (hero.FatigueConsumed, hero.FatigueRemaining, hero.FatigueMax));
    }

    [Fact]
    public void WithoutAClockTheSystemClockIsUsed()
    {
        HeroProgress hero = ExpTests.Progress();
        hero.Clock = null;
        DateTime before = DateTime.Now;
        Assert.True(hero.RefreshFatigue());                       // 从没记过:到点
        DateTime stamp = hero.Fatigue.LastPlay.Value;
        Assert.InRange(stamp, before.AddMinutes(-1), DateTime.Now.AddMinutes(1));
    }

    // ------------------------------------------------------------------ 疲劳条数据

    [Theory]
    [InlineData(3, 10, 3, 10, 7, 0.3, false)]
    [InlineData(1, 10, 1, 10, 9, 0.1, false)]                     // 剩最后一点:还不算空
    [InlineData(0, 10, 0, 10, 10, 0.0, true)]
    [InlineData(10, 10, 10, 10, 0, 1.0, false)]
    [InlineData(15, 10, 10, 10, 0, 1.0, false)]                   // 超过上限夹到上限
    [InlineData(-2, 10, 0, 10, 10, 0.0, true)]
    [InlineData(5, 0, 0, 0, 0, 0.0, true)]                        // 上限为 0:比例是 0,不除零
    [InlineData(3, -4, 0, 0, 0, 0.0, true)]                       // 上限为负:当 0,不会出现负的已耗
    public void TheBarDataIsClampedAndTheRatioIsSafe(int remaining, int max, int expectedRemaining, int expectedMax, int expectedConsumed, double ratio, bool empty)
    {
        var hud = new HudFatigue(remaining, max);
        Assert.Equal((expectedRemaining, expectedMax, expectedConsumed, empty), (hud.Remaining, hud.Max, hud.Consumed, hud.Empty));
        Assert.Equal(ratio, hud.Ratio, 6);
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void ASaveOfAHeroThatNeverTouchedFatigueWritesNoFatigueLine()
    {
        HeroProgress hero = ExpTests.Progress();
        Assert.DoesNotContain("fatigue", hero.Snapshot().ToText());
    }

    [Fact]
    public void TheConsumedPointsAndTheLastPlayTimeSurviveTheTextRoundTrip()
    {
        var clock = new TestClock();
        HeroProgress hero = Hero(clock, max: 10);
        hero.ChargeFatigue(3);
        string text = hero.Snapshot().ToText();
        Assert.Contains("fatigue 3 20260310120000\n", text);
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);

        HeroProgress other = Hero(new TestClock { Now = new DateTime(2026, 3, 10, 13, 0, 0) }, max: 10);
        other.Restore(save, id => null);
        Assert.Equal((3, 7), (other.FatigueConsumed, other.FatigueRemaining));
        Assert.Equal(Noon, other.Fatigue.LastPlay);
        Assert.False(other.RefreshFatigue());                     // 同一个恢复周期里读回来:不会白送一次恢复
        Assert.Equal(3, other.FatigueConsumed);
    }

    [Fact]
    public void ARefreshedHeroWithNoPointsUsedStillWritesTheStampAndAHeroWithoutAStampWritesAZero()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);                      // 刷新过:记了时刻,一点都没扣
        string text = hero.Snapshot().ToText();
        Assert.Contains("fatigue 0 20260310120000\n", text);
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        Assert.Equal((0, (DateTime?)Noon), (save.FatigueConsumed, save.FatigueLastPlay));

        hero.Restore(new HeroSave { FatigueConsumed = 2, FatigueLastPlay = null }, id => null);
        Assert.Contains("fatigue 2 0\n", hero.Snapshot().ToText());
    }

    [Fact]
    public void ASubSecondStampIsWrittenAtSecondPrecision()
    {
        var clock = new TestClock { Now = Noon.AddMilliseconds(987) };
        HeroProgress hero = Hero(clock, max: 10);
        hero.ChargeFatigue(1);
        Assert.Equal(Noon, hero.Snapshot().FatigueLastPlay);
    }

    [Fact]
    public void AnOldSaveWithoutTheLineRestoresAFullStateThatIsDueForRecovery()
    {
        var clock = new TestClock();
        HeroProgress hero = Hero(clock, max: 10);
        hero.ChargeFatigue(7);
        string withoutLine = string.Join("\n", hero.Snapshot().ToText().Split('\n').Where(l => !l.StartsWith("fatigue"))) + "\n";
        Assert.DoesNotContain("fatigue", withoutLine);
        Assert.True(HeroSave.TryParse(withoutLine, out HeroSave save, out _));
        Assert.Equal(0, save.FatigueConsumed);
        Assert.Null(save.FatigueLastPlay);
        hero.Restore(save, id => null);
        Assert.Equal(0, hero.FatigueConsumed);
        Assert.Null(hero.Fatigue.LastPlay);
        Assert.True(hero.CanEnterByFatigue());
    }

    [Theory]
    [InlineData("fatigue 3")]                                     // 少一个值
    [InlineData("fatigue 3 0 0")]                                 // 多一个值
    [InlineData("fatigue x 0")]                                   // 不是整数
    [InlineData("fatigue 3 2026")]                                // 时刻写坏了
    [InlineData("fatigue 3 20261340250000")]                      // 不存在的日期
    [InlineData("fatigue -1 0")]                                  // 负数
    [InlineData("fatigue 65536 0")]                               // 超过 16 位
    public void AMalformedFatigueLineMakesTheWholeSaveUnreadable(string line)
    {
        Assert.False(HeroSave.TryParse(HeroSave.Magic + " 1\n" + line + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("fatigue", error);
    }

    [Theory]
    [InlineData("fatigue 0 0", 0, null)]
    [InlineData("fatigue 65535 0", 65535, null)]
    [InlineData("fatigue 12 20261231235959", 12, 20261231)]
    public void AWellFormedFatigueLineIsAccepted(string line, int consumed, int? day)
    {
        Assert.True(HeroSave.TryParse(HeroSave.Magic + " 1\n" + line + "\n", out HeroSave save, out string error), error);
        Assert.Equal(consumed, save.FatigueConsumed);
        Assert.Equal(day, save.FatigueLastPlay.HasValue ? save.FatigueLastPlay.Value.Year * 10000 + save.FatigueLastPlay.Value.Month * 100 + save.FatigueLastPlay.Value.Day : (int?)null);
    }

    [Fact]
    public void ASaveWithMoreConsumedThanTheCapIsClampedToTheCap()
    {
        var clock = new TestClock();
        HeroProgress hero = Hero(clock, max: 5);
        Assert.True(HeroSave.TryParse(HeroSave.Magic + " 1\nfatigue 900 20260310120000\n", out HeroSave save, out _));
        hero.Restore(save, id => null);
        Assert.Equal((5, 0), (hero.FatigueConsumed, hero.FatigueRemaining));
    }

    [Fact]
    public void RestoringReplacesTheFatigueWholesale()
    {
        var clock = new TestClock();
        HeroProgress hero = Hero(clock, max: 10);
        hero.ChargeFatigue(3);
        var save = new HeroSave { FatigueConsumed = 1, FatigueLastPlay = Noon };
        hero.Restore(save, id => null);
        Assert.Equal(1, hero.FatigueConsumed);
    }

    // ------------------------------------------------------------------ 进副本门槛

    [Fact]
    public void TheGateLetsTheLastPointInAndKeepsTheEmptyOneOut()
    {
        HeroProgress hero = Hero(new TestClock(), max: 3);
        hero.ChargeFatigue(2);
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.Check(hero, DungeonFatigueRules.Default));
        hero.ChargeFatigue(1);
        Assert.Equal(EntryVerdict.FatigueShort, FatigueGate.Check(hero, DungeonFatigueRules.Default));
    }

    [Fact]
    public void ANoFatigueDungeonIsNeverBlockedButABloodDungeonIsWhenTheFatigueIsGone()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.Check(hero, DungeonFatigueRules.Parse(new[] { Flag("[no fatigue]") })));
        // 血之副本进去不扣,但服务端的门槛只认免疲劳标志,所以疲劳用完照样被挡
        Assert.Equal(EntryVerdict.FatigueShort, FatigueGate.Check(hero, DungeonFatigueRules.Parse(new[] { Ints("[blood dungeon]", 1) })));
        Assert.Equal(EntryVerdict.FatigueShort, FatigueGate.Check(hero, null));      // 没有规则当默认规则
    }

    [Fact]
    public void TheCheckRefreshesFirstSoAPassedResetPointUnblocks()
    {
        var clock = new TestClock { Now = new DateTime(2026, 3, 10, 20, 0, 0) };
        HeroProgress hero = Hero(clock, max: 2);
        hero.ChargeFatigue(2);
        Assert.Equal(EntryVerdict.FatigueShort, FatigueGate.Check(hero, DungeonFatigueRules.Default));
        clock.Now = new DateTime(2026, 3, 11, 6, 0, 0);
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.Check(hero, DungeonFatigueRules.Default));
    }

    [Fact]
    public void ACheckWithoutAHeroIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => FatigueGate.Check(null, DungeonFatigueRules.Default));
    }

    [Fact]
    public void TheBuiltGateReadsEachDungeonOnceAndMatchesPathsWithoutRegardToCase()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        var reads = new List<string>();
        Func<string, EntryVerdict> gate = FatigueGate.For(() => hero, path =>
        {
            reads.Add(path);
            return path == "dungeon/test/free.dgn" ? new[] { Flag("[no fatigue]") } : null;
        });
        Assert.Equal(EntryVerdict.Allowed, gate("dungeon/test/free.dgn"));
        Assert.Equal(EntryVerdict.Allowed, gate("DUNGEON/TEST/FREE.DGN"));         // 同一座副本换了大小写:不再读
        Assert.Equal(EntryVerdict.FatigueShort, gate("dungeon/test/paid.dgn"));
        Assert.Equal(EntryVerdict.FatigueShort, gate("dungeon/test/paid.dgn"));
        Assert.Equal(new[] { "dungeon/test/free.dgn", "dungeon/test/paid.dgn" }, reads);
    }

    [Fact]
    public void TheBuiltGateAllowsWhenThereIsNoHeroOrNoPathAndChecksEverythingWhenThereIsNoScriptReader()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.For(() => null, (Func<string, ScriptLine[]>)null)("dungeon/test/a.dgn"));
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.For(null, (Func<string, ScriptLine[]>)null)("dungeon/test/a.dgn"));
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null)(""));
        Assert.Equal(EntryVerdict.Allowed, FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null)(null));
        Assert.Equal(EntryVerdict.FatigueShort, FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null)("dungeon/test/a.dgn"));
        Assert.Equal(EntryVerdict.FatigueShort, FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)(path => null))("dungeon/test/a.dgn"));
    }

    [Fact]
    public void TheAssetsOverloadWithoutAssetsChecksEveryDungeon()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        Func<string, EntryVerdict> gate = FatigueGate.For(() => hero, (DnfAssets)null);
        Assert.Equal(EntryVerdict.FatigueShort, gate("dungeon/test/a.dgn"));
    }

    [Fact]
    public void TheBuiltGateTakesTheHeroFreshOnEveryCallSoSwappingTheHeroTakesEffect()
    {
        HeroProgress empty = Hero(new TestClock(), max: 1);
        empty.ChargeFatigue(1);
        HeroProgress full = Hero(new TestClock(), max: 1);
        HeroProgress current = empty;
        Func<string, EntryVerdict> gate = FatigueGate.For(() => current, (Func<string, ScriptLine[]>)null);
        Assert.Equal(EntryVerdict.FatigueShort, gate("dungeon/test/a.dgn"));
        current = full;
        Assert.Equal(EntryVerdict.Allowed, gate("dungeon/test/a.dgn"));
    }

    // ------------------------------------------------------------------ 流程:选择界面与再次挑战

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly DungeonEntry B = new DungeonEntry("dungeon/test/b.dgn", "BBB");

    private sealed class Run : GameFlowTests.FakeHero, IRunSession
    {
        public Run(string path, int number) : base("run " + path + " #" + number)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }
    }

    private static GameFlow NewFlow(List<string> started)
    {
        return new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) =>
        {
            started.Add(path);
            return new Run(path, run);
        }, new[] { A, B });
    }

    private static void Confirm(GameFlow flow, int moveDown = 0, int difficulty = 0)
    {
        flow.EnterTown();
        flow.EnterSelect();
        flow.Select.Move(moveDown);
        flow.Select.MoveDifficulty(difficulty);
        flow.Select.Confirm();
    }

    [Fact]
    public void WithoutAGateTheFlowNeverBlocksEvenWhenTheFatigueIsGone()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        Assert.Null(flow.EntryGate);
        Confirm(flow);
        Assert.True(flow.Poll());
        Assert.Equal(new[] { A.Path }, started);
        Assert.Equal((0, EntryVerdict.Allowed), (flow.EntryBlocks, flow.LastBlock));
    }

    [Fact]
    public void ConfirmingWithEnoughFatigueStartsTheDungeon()
    {
        HeroProgress hero = Hero(new TestClock(), max: 2);
        hero.ChargeFatigue(1);                                    // 剩 1 点:恰好够
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        flow.EntryGate = FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null);
        Confirm(flow);
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Dungeon, 1, 0), (flow.Stage, flow.Runs, flow.EntryBlocks));
        Assert.Equal(new[] { A.Path }, started);
    }

    [Fact]
    public void ConfirmingWithNoFatigueLeftReopensTheSelectScreenOnTheSameDungeonAndDifficulty()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);                                    // 差一点
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        flow.DifficultyLimit = e => 3;
        flow.EntryGate = FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null);
        Confirm(flow, moveDown: 1, difficulty: 2);
        int serial = flow.StageSerial;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Empty(started);
        Assert.Equal((0, 1, EntryVerdict.FatigueShort), (flow.Runs, flow.EntryBlocks, flow.LastBlock));
        Assert.Equal(serial + 1, flow.StageSerial);               // 是一次阶段切换(日志里看得见)
        Assert.Contains("blocked FatigueShort", flow.History[^1].Reason);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);    // 新打开的界面,还能继续选
        Assert.Same(B, flow.Select.Selected);
        Assert.Equal(2, flow.Select.Difficulty);
    }

    [Fact]
    public void AfterTheResetPointTheSameConfirmGoesThrough()
    {
        var clock = new TestClock { Now = new DateTime(2026, 3, 10, 20, 0, 0) };
        HeroProgress hero = Hero(clock, max: 1);
        hero.ChargeFatigue(1);
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        flow.EntryGate = FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null);
        Confirm(flow);
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        clock.Now = new DateTime(2026, 3, 11, 6, 0, 0);
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Dungeon, 1, 1), (flow.Stage, flow.Runs, flow.EntryBlocks));
    }

    [Fact]
    public void TheGateIsAskedPerDungeonSoANoFatigueOneGetsInWhileTheOtherIsBlocked()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        flow.EntryGate = FatigueGate.For(() => hero, path => path == B.Path ? new[] { Flag("[no fatigue]") } : null);
        Confirm(flow, moveDown: 0);                               // A:收费,被挡
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.DungeonSelect, 1), (flow.Stage, flow.EntryBlocks));
        flow.Select.Move(1);                                      // B:免疲劳,放行
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Dungeon, 1), (flow.Stage, flow.Runs));
        Assert.Equal(new[] { B.Path }, started);
    }

    [Fact]
    public void RetryWithEnoughFatigueStartsAgainAndRetryWithoutGoesToTheSelectScreen()
    {
        HeroProgress hero = Hero(new TestClock(), max: 2);
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        flow.EntryGate = FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null);
        Confirm(flow);
        flow.Poll();
        Assert.Equal(1, flow.Runs);

        ((Run)flow.Run).Choice = SettlementChoice.Retry;         // 疲劳还有:再来一次
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Dungeon, 2, 1, 0), (flow.Stage, flow.Runs, flow.Settlements, flow.EntryBlocks));

        hero.ChargeFatigue(2);                                    // 用光了
        ((Run)flow.Run).Choice = SettlementChoice.Retry;
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.DungeonSelect, 2, 2, 1), (flow.Stage, flow.Runs, flow.Settlements, flow.EntryBlocks));
        Assert.Contains("choice Retry blocked FatigueShort", flow.History[^1].Reason);
        Assert.Equal(new[] { A.Path, A.Path }, started);
        Assert.Same(A, flow.Select.Selected);                     // 选择界面停在刚才那座
    }

    [Fact]
    public void StartingTheDungeonDirectlyIsNotGated()
    {
        HeroProgress hero = Hero(new TestClock(), max: 1);
        hero.ChargeFatigue(1);
        var started = new List<string>();
        GameFlow flow = NewFlow(started);
        flow.EntryGate = FatigueGate.For(() => hero, (Func<string, ScriptLine[]>)null);
        flow.EnterTown();
        flow.StartDungeon(A.Path);
        Assert.Equal((GameStage.Dungeon, 1, 0), (flow.Stage, flow.Runs, flow.EntryBlocks));
    }

    // ------------------------------------------------------------------ 副本会话:进副本与每换一次房间的扣除

    /// <summary>合成环里一直往 <paramref name="dirX"/> 走,直到 <paramref name="done"/>(没有怪物,出口一进来就是开的)。</summary>
    private static void WalkUntil(DungeonSession s, Func<bool> done, int dirX)
    {
        for (int t = 0; t < 20000 && !done(); t += 10)
        {
            s.Update(10, dirX, 0, false);
        }
        Assert.True(done(), "20 秒内没走到");
    }

    [Fact]
    public void TheStartIsChargedOnceAndEveryRoomChangeAfterwardsIncludingGoingBack()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        Assert.Equal(0, hero.FatigueConsumed);                    // 不接疲劳就不扣
        s.UseFatigue(hero, DungeonFatigueRules.Default);
        Assert.Equal((1, 1), (hero.FatigueConsumed, s.FatigueCharged));
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal((2, 2, 1), (hero.FatigueConsumed, s.FatigueCharged, s.Transitions));
        WalkUntil(s, () => s.CellX == 0, -1);                     // 走回去过的房间照样扣
        Assert.Equal((3, 3, 2), (hero.FatigueConsumed, s.FatigueCharged, s.Transitions));
    }

    [Fact]
    public void WithoutChargingTheStartOnlyTheRoomChangesCost()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, DungeonFatigueRules.Default, chargeStart: false);
        Assert.Equal((0, 0), (hero.FatigueConsumed, s.FatigueCharged));
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal((1, 1), (hero.FatigueConsumed, s.FatigueCharged));
    }

    [Fact]
    public void ANoFatigueDungeonChargesNothingAtAll()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, DungeonFatigueRules.Parse(new[] { Flag("[no fatigue]") }));
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal((0, 0, 1), (hero.FatigueConsumed, s.FatigueCharged, s.Transitions));
    }

    [Fact]
    public void ANoFatigueDungeonLeavesTheFatigueStateUntouchedEvenAfterTheResetPoint()
    {
        var clock = new TestClock { Now = new DateTime(2026, 3, 10, 20, 0, 0) };
        HeroProgress hero = Hero(clock, max: 10);
        hero.ChargeFatigue(5);
        clock.Now = new DateTime(2026, 3, 11, 7, 0, 0);                          // 过了恢复点,但还没有人刷新
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, DungeonFatigueRules.Parse(new[] { Flag("[no fatigue]") }));
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal(5, hero.FatigueConsumed);                                   // 免疲劳的副本不碰疲劳状态:没有刷新,也就没有恢复
        Assert.Equal(new DateTime(2026, 3, 10, 20, 0, 0), hero.Fatigue.LastPlay);
    }

    [Fact]
    public void ATournamentDungeonChargesItsRoundFatigueAtTheStartAndOnePerRoomChange()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, DungeonFatigueRules.Parse(new[] { Ints("[tournament dungeon]", 1), Ints("[tournament round fatigue]", 3) }));
        Assert.Equal(3, hero.FatigueConsumed);
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal(4, hero.FatigueConsumed);
    }

    [Fact]
    public void ARunningOutOfFatigueDoesNotStopTheDungeonItJustStopsCountingUp()
    {
        HeroProgress hero = Hero(new TestClock(), max: 2);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, DungeonFatigueRules.Default);          // 1
        WalkUntil(s, () => s.CellX == 1, 1);                      // 2(满)
        WalkUntil(s, () => s.CellX == 0, -1);                     // 再换房间也走得动,只是不再加
        Assert.Equal((2, 2, 2, 0), (hero.FatigueConsumed, s.FatigueCharged, s.Transitions, hero.FatigueRemaining));
    }

    [Fact]
    public void TheCreateWiringNeedsAHeroAndReadsTheTagsFromTheDungeonScript()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var none = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession.AttachFatigue(none.Session, null, new[] { Flag("[no fatigue]") });
        Assert.Equal((0, 0), (hero.FatigueConsumed, none.Session.FatigueCharged));      // 没给主角进度:什么也不做

        using var free = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession.AttachFatigue(free.Session, hero, new[] { Flag("[no fatigue]") });
        Assert.Equal((0, 0), (hero.FatigueConsumed, free.Session.FatigueCharged));      // 标签读到了:免疲劳

        using var paid = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession.AttachFatigue(paid.Session, hero, new ScriptLine[0]);
        Assert.Equal((1, 1), (hero.FatigueConsumed, paid.Session.FatigueCharged));      // 没有标签:1 点

        using var tournament = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession.AttachFatigue(tournament.Session, hero, new[] { Ints("[tournament dungeon]", 1), Ints("[tournament round fatigue]", 3) });
        Assert.Equal((4, 3), (hero.FatigueConsumed, tournament.Session.FatigueCharged));

        using var missing = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession.AttachFatigue(missing.Session, hero, null);                      // 读不到副本定义:按默认规则
        Assert.Equal((5, 1), (hero.FatigueConsumed, missing.Session.FatigueCharged));
    }

    [Fact]
    public void ChargesAreCancelledByAskingForNoHero()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, DungeonFatigueRules.Default);
        s.UseFatigue(null, DungeonFatigueRules.Default, chargeStart: false);
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal((1, 1), (hero.FatigueConsumed, s.FatigueCharged));
    }

    [Fact]
    public void AMissingRuleObjectMeansTheDefaultRules()
    {
        HeroProgress hero = Hero(new TestClock(), max: 10);
        using var d = new Dungeon(RingLayout(), (x, y) => None(), progress: hero);
        DungeonSession s = d.Session;
        s.UseFatigue(hero, null);
        Assert.Equal(1, hero.FatigueConsumed);
        WalkUntil(s, () => s.CellX == 1, 1);
        Assert.Equal(2, hero.FatigueConsumed);
    }
}
