using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 副本通关记录与难度门槛(M33,证据 E-125):进副本的门槛(<see cref="DungeonClears.CanEnter"/>)、通关后的记录(<see cref="DungeonClears.Record"/>)、
/// 有难度 3 的副本清单(<see cref="UltimateDungeonList"/>)。副本编号、等级与评分档全是合成的;规则的常量(评分档 S、SS、SSS 与被击次数 10、30、50)来自服务端代码。
/// </summary>
public class DungeonClearsTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptLine Line(string tag, params int[] values) => new ScriptLine(tag, Array.ConvertAll(values, I));

    private const int Dungeon = 7;
    private const int S = 6, SS = 7, SSS = 8;

    // ------------------------------------------------------------------ 记录的存取

    [Fact]
    public void ADungeonWithoutARecordReadsZero()
    {
        var clears = new DungeonClears();
        Assert.Equal(0, clears.Opened(Dungeon));
        Assert.Equal(0, clears.Count);
        Assert.Empty(clears.Entries());
    }

    [Fact]
    public void RaiseOnlyWritesWhenTheNewValueIsLarger()
    {
        var clears = new DungeonClears();
        Assert.True(clears.Raise(Dungeon, 2));
        Assert.Equal(2, clears.Opened(Dungeon));
        Assert.False(clears.Raise(Dungeon, 2));                    // 一样大:不写
        Assert.False(clears.Raise(Dungeon, 1));                    // 更小:不降
        Assert.Equal(2, clears.Opened(Dungeon));
        Assert.True(clears.Raise(Dungeon, 3));
        Assert.Equal(3, clears.Opened(Dungeon));
    }

    [Fact]
    public void RaiseClampsToTheDifficultyRangeAndZeroIsNotARecord()
    {
        var clears = new DungeonClears();
        Assert.False(clears.Raise(Dungeon, 0));
        Assert.False(clears.Raise(Dungeon, -5));
        Assert.Equal(0, clears.Count);
        Assert.True(clears.Raise(Dungeon, 99));
        Assert.Equal(DungeonClears.Difficulties - 1, clears.Opened(Dungeon));
    }

    [Fact]
    public void SetRestoresASavedValueAndIgnoresNonsense()
    {
        var clears = new DungeonClears();
        clears.Set(3, 2);
        clears.Set(4, 0);
        clears.Set(5, 5);
        clears.Set(6, -1);
        Assert.Equal(2, clears.Opened(3));
        Assert.Equal(1, clears.Count);
        clears.Set(3, 1);                                          // 还原是直接设,不受"只升不降"约束
        Assert.Equal(1, clears.Opened(3));
        clears.Clear();
        Assert.Equal(0, clears.Count);
    }

    [Fact]
    public void EntriesAreSortedByDungeonNumber()
    {
        var clears = new DungeonClears();
        clears.Raise(30, 1);
        clears.Raise(4, 3);
        clears.Raise(12, 2);
        Assert.Equal(new[] { 4, 12, 30 }, clears.Entries().Select(e => e.Key).ToArray());
        Assert.Equal(new[] { 3, 2, 1 }, clears.Entries().Select(e => e.Value).ToArray());
    }

    // ------------------------------------------------------------------ 进副本的门槛

    [Fact]
    public void DifficultyZeroIsAlwaysOpen()
    {
        var clears = new DungeonClears();
        Assert.True(clears.CanEnter(Dungeon, 0));
    }

    [Theory]
    [InlineData(0, 1, false)]       // 没有记录:难度 1 进不了
    [InlineData(1, 1, true)]        // 通关过难度 0(值 1):难度 1 开放
    [InlineData(1, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 1, true)]        // 高的开放了,低的也能进
    public void DifficultyOneToThreeNeedTheRecordToReachIt(int value, int difficulty, bool expected)
    {
        var clears = new DungeonClears();
        clears.Set(Dungeon, value);
        Assert.Equal(expected, clears.CanEnter(Dungeon, difficulty));
    }

    [Fact]
    public void DifficultyFourOnlyNeedsSomeRecordNotTheSameSize()
    {
        var clears = new DungeonClears();
        Assert.False(clears.CanEnter(Dungeon, 4));                 // 没有记录不行
        clears.Set(Dungeon, 1);
        Assert.True(clears.CanEnter(Dungeon, 4));                  // 有记录就行,不比较大小
    }

    [Fact]
    public void ExemptDungeonsSkipTheCheckAndOutOfRangeDifficultiesNeverEnter()
    {
        var clears = new DungeonClears();
        Assert.True(clears.CanEnter(Dungeon, 3, exempt: true));
        Assert.True(clears.CanEnter(Dungeon, 4, exempt: true));
        Assert.False(clears.CanEnter(Dungeon, -1));
        Assert.False(clears.CanEnter(Dungeon, 5));
        Assert.False(clears.CanEnter(Dungeon, 5, exempt: true));
    }

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(1, true, 1)]
    [InlineData(2, true, 2)]
    [InlineData(3, true, 3)]
    [InlineData(4, true, 3)]        // 记录值夹在界面能选的最高档以内
    [InlineData(3, false, 2)]       // 没有难度 3 的副本最多到 2
    [InlineData(2, false, 2)]
    [InlineData(1, false, 1)]
    public void TheSelectableCeilingFollowsTheRecordAndWhetherTheDungeonHasLevelThree(int value, bool hasUltimate, int expected)
    {
        var clears = new DungeonClears();
        clears.Set(Dungeon, value);
        Assert.Equal(expected, clears.HighestSelectable(Dungeon, hasUltimate));
    }

    // ------------------------------------------------------------------ 通关后的记录

    [Fact]
    public void ClearingDifficultyZeroOpensDifficultyOneWhateverTheGrade()
    {
        var clears = new DungeonClears();
        Assert.Equal(1, clears.Record(Dungeon, 0, grade: 0, hitsTaken: 99, coinsUsed: 5, heroLevel: 10, minLevel: 10, hasUltimate: false));
        Assert.Equal(1, clears.Opened(Dungeon));
        Assert.True(clears.CanEnter(Dungeon, 1));
        Assert.Equal(0, clears.Record(Dungeon, 0, 8, 0, 0, 10, 10, false));       // 再通关一次:没有变化,返回 0
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]      // A:不够
    [InlineData(6, 2)]      // S
    [InlineData(7, 2)]      // SS
    [InlineData(8, 2)]      // SSS
    public void ClearingDifficultyOneNeedsAtLeastAnSToOpenDifficultyTwo(int grade, int expected)
    {
        var clears = new DungeonClears();
        clears.Set(Dungeon, 1);
        Assert.Equal(expected, clears.Record(Dungeon, 1, grade, hitsTaken: 50, coinsUsed: 3, heroLevel: 20, minLevel: 1, hasUltimate: false));    // 这一档不看被击次数与复活币
        Assert.Equal(expected == 0 ? 1 : 2, clears.Opened(Dungeon));
    }

    [Fact]
    public void ClearingDifficultyOneWithoutAnyPriorRecordStillWritesTheNewValue()
    {
        // 服务端只在通关时按"这次通关的难度"写;没有先通关难度 0 也能写(命令行可以直接指定难度)
        var clears = new DungeonClears();
        Assert.Equal(2, clears.Record(Dungeon, 1, S, 0, 0, 10, 1, false));
        Assert.Equal(2, clears.Opened(Dungeon));
    }

    [Fact]
    public void DifficultyThreeOpensOnlyForDungeonsOnTheListAndOnlyWithAPerfectEnoughRun()
    {
        var clears = new DungeonClears();
        clears.Set(Dungeon, 2);
        // 不在清单里:怎么打都不开
        Assert.Equal(0, clears.Record(Dungeon, 2, SSS, 0, 0, 30, 1, hasUltimate: false));
        Assert.Equal(2, clears.Opened(Dungeon));
        // 在清单里,普通种类:要 SSS、被击不超过 10 次、没用复活币
        Assert.Equal(0, clears.Record(Dungeon, 2, SS, 0, 0, 30, 1, true));
        Assert.Equal(0, clears.Record(Dungeon, 2, SSS, 11, 0, 30, 1, true));
        Assert.Equal(0, clears.Record(Dungeon, 2, SSS, 0, 1, 30, 1, true));
        Assert.Equal(2, clears.Opened(Dungeon));
        Assert.Equal(3, clears.Record(Dungeon, 2, SSS, 10, 0, 30, 1, true));       // 恰好 10 次可以
        Assert.Equal(3, clears.Opened(Dungeon));
        Assert.True(clears.CanEnter(Dungeon, 3));
    }

    [Theory]
    [InlineData(DungeonClearKind.Flagged, 7, 30, true)]
    [InlineData(DungeonClearKind.Flagged, 7, 31, false)]
    [InlineData(DungeonClearKind.Flagged, 6, 0, false)]
    [InlineData(DungeonClearKind.Dimension, 7, 50, true)]
    [InlineData(DungeonClearKind.Dimension, 7, 51, false)]
    [InlineData(DungeonClearKind.Dimension, 8, 0, true)]
    [InlineData(DungeonClearKind.Standard, 7, 0, false)]
    [InlineData(DungeonClearKind.Standard, 8, 11, false)]
    [InlineData(DungeonClearKind.Other, 8, 0, false)]
    public void TheDungeonKindChoosesTheGradeAndTheHitLimitForDifficultyThree(DungeonClearKind kind, int grade, int hits, bool opens)
    {
        var clears = new DungeonClears();
        int got = clears.Record(Dungeon, 2, grade, hits, 0, 30, 1, true, kind);
        Assert.Equal(opens ? 3 : 0, got);
    }

    [Fact]
    public void HitLimitsAreTheServerConstants()
    {
        Assert.Equal(10, DungeonClears.HitLimit(DungeonClearKind.Standard));
        Assert.Equal(30, DungeonClears.HitLimit(DungeonClearKind.Flagged));
        Assert.Equal(50, DungeonClears.HitLimit(DungeonClearKind.Dimension));
        Assert.True(DungeonClears.HitLimit(DungeonClearKind.Other) < 0);
    }

    [Fact]
    public void ClearingDifficultyThreeOrFourChangesNothing()
    {
        var clears = new DungeonClears();
        clears.Set(Dungeon, 3);
        Assert.Equal(0, clears.Record(Dungeon, 3, SSS, 0, 0, 30, 1, true));
        Assert.Equal(0, clears.Record(Dungeon, 4, SSS, 0, 0, 30, 1, true));
        Assert.Equal(0, clears.Record(Dungeon, 9, SSS, 0, 0, 30, 1, true));
        Assert.Equal(0, clears.Record(Dungeon, -1, SSS, 0, 0, 30, 1, true));
        Assert.Equal(3, clears.Opened(Dungeon));
    }

    [Fact]
    public void AHeroBelowTheDungeonsMinimumLevelLeavesNoRecord()
    {
        var clears = new DungeonClears();
        Assert.Equal(0, clears.Record(Dungeon, 0, SSS, 0, 0, heroLevel: 9, minLevel: 10, hasUltimate: true));
        Assert.Equal(0, clears.Count);
        Assert.Equal(1, clears.Record(Dungeon, 0, SSS, 0, 0, heroLevel: 10, minLevel: 10, hasUltimate: true));   // 恰好够
    }

    [Fact]
    public void ARecordNeverGoesDown()
    {
        var clears = new DungeonClears();
        clears.Set(Dungeon, 3);
        Assert.Equal(0, clears.Record(Dungeon, 0, SSS, 0, 0, 30, 1, true));       // 已经是 3,通关难度 0 不会拉回 1
        Assert.Equal(0, clears.Record(Dungeon, 1, SSS, 0, 0, 30, 1, true));
        Assert.Equal(3, clears.Opened(Dungeon));
    }

    [Fact]
    public void TheFullLadderOpensOneStepPerClear()
    {
        var clears = new DungeonClears();
        Assert.False(clears.CanEnter(Dungeon, 1));
        Assert.Equal(1, clears.Record(Dungeon, 0, 0, 0, 0, 30, 1, true));
        Assert.True(clears.CanEnter(Dungeon, 1));
        Assert.False(clears.CanEnter(Dungeon, 2));
        Assert.Equal(2, clears.Record(Dungeon, 1, S, 0, 0, 30, 1, true));
        Assert.True(clears.CanEnter(Dungeon, 2));
        Assert.False(clears.CanEnter(Dungeon, 3));
        Assert.Equal(3, clears.Record(Dungeon, 2, SSS, 0, 0, 30, 1, true));
        Assert.True(clears.CanEnter(Dungeon, 3));
        Assert.Equal(3, clears.HighestSelectable(Dungeon, true));
    }

    // ------------------------------------------------------------------ 脚本里读最低等级

    [Fact]
    public void MinimumLevelReadsTheFirstNumberOfTheTag()
    {
        Assert.Equal(17, DungeonClears.MinimumLevel(new[] { Line("[basis level]", 20), Line("[minimum required level]", 17, 99) }));
        Assert.Equal(0, DungeonClears.MinimumLevel(new[] { Line("[basis level]", 20) }));
        Assert.Equal(0, DungeonClears.MinimumLevel(new[] { Line("[minimum required level]") }));
        Assert.Equal(0, DungeonClears.MinimumLevel(null));
    }

    // ------------------------------------------------------------------ 难度 3 的副本清单

    [Fact]
    public void TheUltimateListReadsEveryIntegerInTheTagLines()
    {
        UltimateDungeonList list = UltimateDungeonList.Parse(new[] { Line("[apply ultimate]", 3, 5, 9), Line("[/apply ultimate]") });
        Assert.Equal(3, list.Count);
        Assert.True(list.Contains(3) && list.Contains(5) && list.Contains(9));
        Assert.False(list.Contains(4));
        Assert.False(list.Contains(-1));
    }

    [Fact]
    public void SeveralBlocksAreMergedAndStrayLinesAreIgnored()
    {
        UltimateDungeonList list = UltimateDungeonList.Parse(new[] { Line("[other]", 1, 2), Line("[apply ultimate]", 3), Line("[apply ultimate]", 4, 3) });
        Assert.Equal(2, list.Count);
        Assert.True(list.Contains(3) && list.Contains(4));
        Assert.False(list.Contains(1));
    }

    [Fact]
    public void AMissingOrEmptyListMeansNoDungeonHasDifficultyThree()
    {
        Assert.Same(UltimateDungeonList.Empty, UltimateDungeonList.Parse(null));
        Assert.Same(UltimateDungeonList.Empty, UltimateDungeonList.Parse(new ScriptLine[0]));
        Assert.Same(UltimateDungeonList.Empty, UltimateDungeonList.Parse(new[] { Line("[apply ultimate]") }));
        Assert.Equal(0, UltimateDungeonList.Empty.Count);
        Assert.False(UltimateDungeonList.Empty.Contains(0));
    }
}
