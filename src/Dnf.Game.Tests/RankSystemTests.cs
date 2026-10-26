using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 副本评分的算术(证据 E-124):用时分、评分点、评分值、评分档位、字母图的帧、文件解析。数值全是合成的(门槛、平均用时、奖励档都是随手定的),与原版数据无关。
/// </summary>
public class RankSystemTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptLine Line(string tag, params int[] values) => new ScriptLine(tag, Array.ConvertAll(values, I));

    private static readonly int[] Thresholds = { 90, 80, 70, 60, 50, 40, 30, 20 };

    private static RankSystem Synthetic()
    {
        var steps = new[] { new TimeBonusStep(0, 1), new TimeBonusStep(10000, 2), new TimeBonusStep(20000, 3), new TimeBonusStep(40000, 5) };
        var averages = new[]
        {
            (7, 1, new[] { 100000, 110000, 120000, 130000, 140000 }),
            (7, 2, new[] { 90000, 91000, 92000, 93000, 94000 }),
        };
        return new RankSystem(Thresholds, steps, averages);
    }

    [Fact]
    public void RankValueIsTheFirstThresholdTheScoreReaches()
    {
        RankSystem rank = Synthetic();
        Assert.Equal(90, rank.RankValue(95));
        Assert.Equal(90, rank.RankValue(90));
        Assert.Equal(80, rank.RankValue(89));
        Assert.Equal(80, rank.RankValue(80));
        Assert.Equal(70, rank.RankValue(79));
        Assert.Equal(20, rank.RankValue(20));
        Assert.Equal(0, rank.RankValue(19));
        Assert.Equal(0, rank.RankValue(0));
        Assert.Equal(0, rank.RankValue(-5));
    }

    [Fact]
    public void BonusIndexIsFourMinusThePositionOfTheFirstTopFiveThresholdReached()
    {
        RankSystem rank = Synthetic();
        Assert.Equal(4, rank.BonusIndex(90));
        Assert.Equal(3, rank.BonusIndex(80));
        Assert.Equal(2, rank.BonusIndex(70));
        Assert.Equal(1, rank.BonusIndex(60));
        Assert.Equal(0, rank.BonusIndex(50));
        Assert.Equal(-1, rank.BonusIndex(40));         // 第六个门槛起没有加成
        Assert.Equal(-1, rank.BonusIndex(0));
        Assert.Equal(4, rank.BonusIndex(rank.RankValue(99)));
        Assert.Equal(0, rank.BonusIndex(rank.RankValue(55)));
    }

    [Fact]
    public void FrameIsTheIndexOfTheEqualThresholdAndEightWhenNoneIsEqual()
    {
        RankSystem rank = Synthetic();
        Assert.Equal(0, rank.FrameOf(90));
        Assert.Equal(1, rank.FrameOf(80));
        Assert.Equal(7, rank.FrameOf(20));
        Assert.Equal(8, rank.FrameOf(0));
        Assert.Equal(8, rank.FrameOf(55));             // 评分值总是某个门槛本身;不等于任何门槛的值按 F
        Assert.Equal(8, rank.GradeOf(90));
        Assert.Equal("SSS", RankSystem.LetterOf(rank.GradeOf(90)));
        Assert.Equal("SS", RankSystem.LetterOf(rank.GradeOf(80)));
        Assert.Equal("S", RankSystem.LetterOf(rank.GradeOf(70)));
        Assert.Equal("A", RankSystem.LetterOf(rank.GradeOf(60)));
        Assert.Equal("B", RankSystem.LetterOf(rank.GradeOf(50)));
        Assert.Equal("C", RankSystem.LetterOf(rank.GradeOf(40)));
        Assert.Equal("D", RankSystem.LetterOf(rank.GradeOf(30)));
        Assert.Equal("E", RankSystem.LetterOf(rank.GradeOf(20)));
        Assert.Equal("F", RankSystem.LetterOf(rank.GradeOf(0)));
        Assert.Equal("?", RankSystem.LetterOf(9));
        Assert.Equal("?", RankSystem.LetterOf(-1));
    }

    [Fact]
    public void NineGradesFromFToSssAreCoveredByTheEightThresholds()
    {
        RankSystem rank = Synthetic();
        var letters = new List<string>();
        foreach (int point in new[] { 0, 20, 30, 40, 50, 60, 70, 80, 90 })
        {
            letters.Add(RankSystem.LetterOf(rank.GradeOf(rank.RankValue(point))));
        }
        Assert.Equal(new[] { "F", "E", "D", "C", "B", "A", "S", "SS", "SSS" }, letters);
        Assert.Equal(RankSystem.GradeCount, letters.Count);
    }

    [Fact]
    public void MissingThresholdsDoNotTakePart()
    {
        RankSystem none = RankSystem.Empty;
        Assert.Equal(0, none.ThresholdsGiven);
        Assert.Equal(0, none.RankValue(1000));
        Assert.Equal(-1, none.BonusIndex(1000));
        Assert.Equal(8, none.FrameOf(0));
        Assert.Equal(0, none.GradeOf(0));
        var three = new RankSystem(new[] { 90, 80, 70 }, null, null);
        Assert.Equal(3, three.ThresholdsGiven);
        Assert.Equal(70, three.RankValue(75));
        Assert.Equal(0, three.RankValue(10));
        Assert.Equal(2, three.BonusIndex(70));
        Assert.Equal(-1, three.BonusIndex(10));
        Assert.Equal(90, three.Threshold(0));
        Assert.Equal(0, three.Threshold(5));
    }

    [Fact]
    public void ThresholdsAreStoredAsUnsigned16Bit()
    {
        var rank = new RankSystem(new[] { 70000, -1, 5 }, null, null);
        Assert.Equal(70000 & 0xffff, rank.Threshold(0));
        Assert.Equal(0xffff, rank.Threshold(1));
        Assert.Equal(5, rank.Threshold(2));
    }

    [Fact]
    public void TimeBonusStopsAtTheFirstStepTheDifferenceFallsShortOf()
    {
        RankSystem rank = Synthetic();
        Assert.Equal(1, rank.TimeBonus(100000, 100000));     // 差 0:过第一档(门槛 0),第二档(10000)不够
        Assert.Equal(1, rank.TimeBonus(100000, 95000));      // 差 5000
        Assert.Equal(2, rank.TimeBonus(100000, 90000));      // 差 10000:恰好过第二档
        Assert.Equal(2, rank.TimeBonus(100000, 85000));      // 差 15000
        Assert.Equal(3, rank.TimeBonus(100000, 80000));      // 差 20000
        Assert.Equal(3, rank.TimeBonus(100000, 61000));      // 差 39000
        Assert.Equal(5, rank.TimeBonus(100000, 60000));      // 差 40000:过了全部档,给最后一档
        Assert.Equal(5, rank.TimeBonus(100000, 1));
    }

    [Fact]
    public void TimeBonusIsZeroWhenSlowerThanAverageOrWithoutAnAverageOrClearTime()
    {
        RankSystem rank = Synthetic();
        Assert.Equal(0, rank.TimeBonus(100000, 100001));     // 差 −1 < 第一档门槛 0
        Assert.Equal(0, rank.TimeBonus(100000, 250000));
        Assert.Equal(0, rank.TimeBonus(0, 50000));           // 没有平均用时
        Assert.Equal(0, rank.TimeBonus(100000, 0));          // 没有实际用时
        Assert.Equal(0, RankSystem.Empty.TimeBonus(100000, 50000));   // 没有奖励档
    }

    [Fact]
    public void TimeBonusFirstStepWithANegativeThresholdAlsoPaysSlowRuns()
    {
        var rank = new RankSystem(null, new[] { new TimeBonusStep(-5000, 1), new TimeBonusStep(0, 2) }, null);
        Assert.Equal(1, rank.TimeBonus(100000, 103000));     // 差 −3000 ≥ −5000
        Assert.Equal(0, rank.TimeBonus(100000, 110000));     // 差 −10000
        Assert.Equal(2, rank.TimeBonus(100000, 90000));
    }

    [Fact]
    public void RankPointAddsTheSignedBaseScoreToTheTimeBonus()
    {
        Assert.Equal(95, RankSystem.RankPoint(5, 90));
        Assert.Equal(0, RankSystem.RankPoint(0, 0));
        Assert.Equal(-10, RankSystem.RankPoint(0, -10));     // 客户端发的是有符号字节
        Assert.Equal(3, RankSystem.RankPoint(13, -10));
    }

    [Fact]
    public void AverageTimeFindsTheRecordByDungeonAndPartySizeAndTakesDifficultyZeroToThree()
    {
        RankSystem rank = Synthetic();
        Assert.Equal(100000, rank.AverageTime(7, 1, 0));
        Assert.Equal(110000, rank.AverageTime(7, 1, 1));
        Assert.Equal(120000, rank.AverageTime(7, 1, 2));
        Assert.Equal(130000, rank.AverageTime(7, 1, 3));
        Assert.Equal(0, rank.AverageTime(7, 1, 4));          // 第五个数读进来、不被取用
        Assert.Equal(0, rank.AverageTime(7, 1, -1));
        Assert.Equal(90000, rank.AverageTime(7, 2, 0));      // 同一副本不同人数是另一条记录
        Assert.Equal(0, rank.AverageTime(7, 3, 0));
        Assert.Equal(0, rank.AverageTime(8, 1, 0));
        Assert.Equal(2, rank.AverageRecordCount);
    }

    [Fact]
    public void TheFirstRecordOfADuplicateKeyWins()
    {
        var rank = new RankSystem(null, null, new[]
        {
            (3, 1, new[] { 1000, 0, 0, 0, 0 }),
            (3, 1, new[] { 2000, 0, 0, 0, 0 }),
        });
        Assert.Equal(1000, rank.AverageTime(3, 1, 0));
        Assert.Equal(1, rank.AverageRecordCount);
    }

    [Fact]
    public void ParseReadsSevenIntRecordsPairsAndGradesAndDropsPartialTails()
    {
        RankSystem rank = RankSystem.Parse(new[]
        {
            Line("[dungeon clear time average]",
                7, 1, 100000, 110000, 120000, 130000, 140000,
                7, 2, 90000, 91000, 92000, 93000, 94000,
                9, 4, 5000, 6000),                           // 最后一条不满七个:丢掉
            Line("[/dungeon clear time average]"),
            Line("[dungeon clear time reduction bonus]", 0, 1, 10000, 2, 20000, 3, 40000, 5, 99999),   // 末尾单出一个数:丢掉
            Line("[rank grade]", 90, 80, 70, 60, 50, 40, 30, 20, 10),                                // 只取前 8 个
            Line("[combo interval boundaries]", 1, 2, 3, 4),                                       // 客户端用的块:不读
            Line("[combo practice bonus inerval]", 500),
        });
        Assert.Equal(2, rank.AverageRecordCount);
        Assert.Equal(100000, rank.AverageTime(7, 1, 0));
        Assert.Equal(93000, rank.AverageTime(7, 2, 3));
        Assert.Equal(0, rank.AverageTime(9, 4, 0));
        Assert.Equal(4, rank.StepCount);
        Assert.Equal(new TimeBonusStep(40000, 5), rank.StepAt(3));
        Assert.Equal(default(TimeBonusStep), rank.StepAt(4));
        Assert.Equal(8, rank.ThresholdsGiven);
        Assert.Equal(90, rank.Threshold(0));
        Assert.Equal(20, rank.Threshold(7));
        Assert.Equal(0, rank.Threshold(8));
    }

    [Fact]
    public void ParseTakesTheFirstBlockOfEachKindAndNullGivesEmpty()
    {
        RankSystem rank = RankSystem.Parse(new[]
        {
            Line("[rank grade]", 90, 80),
            Line("[rank grade]", 10, 5, 1),
            Line("[dungeon clear time reduction bonus]", 0, 1),
            Line("[dungeon clear time reduction bonus]", 0, 9),
        });
        Assert.Equal(2, rank.ThresholdsGiven);
        Assert.Equal(90, rank.Threshold(0));
        Assert.Equal(1, rank.StepAt(0).Bonus);
        RankSystem empty = RankSystem.Parse(null);
        Assert.Equal(0, empty.AverageRecordCount);
        Assert.Equal(0, empty.StepCount);
        Assert.Equal(0, empty.ThresholdsGiven);
    }

    [Fact]
    public void TheServerPipelineGivesTheLetterAndTheBonusIndex()
    {
        // SetPlayResult:平均用时 → 用时分 → 评分点 → 评分值 → 评分档位
        RankSystem rank = Synthetic();
        int average = rank.AverageTime(7, 1, 0);
        int timeBonus = rank.TimeBonus(average, 85000);             // 差 15000 → 2 分
        int point = RankSystem.RankPoint(timeBonus, 90);            // 92
        int value = rank.RankValue(point);                          // 90
        Assert.Equal(2, timeBonus);
        Assert.Equal(92, point);
        Assert.Equal(90, value);
        Assert.Equal("SSS", RankSystem.LetterOf(rank.GradeOf(value)));
        Assert.Equal(4, rank.BonusIndex(value));
        // 没有这个副本的平均用时:只剩基础分
        int plain = RankSystem.RankPoint(rank.TimeBonus(rank.AverageTime(99, 1, 0), 85000), 65);
        Assert.Equal(65, plain);
        Assert.Equal(60, rank.RankValue(plain));
        Assert.Equal("A", RankSystem.LetterOf(rank.GradeOf(rank.RankValue(plain))));
        Assert.Equal(1, rank.BonusIndex(rank.RankValue(plain)));
    }
}
