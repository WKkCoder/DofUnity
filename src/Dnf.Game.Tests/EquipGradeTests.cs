using Dnf.Data;
using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>
/// 品级:样本表的读取、倍率表的取法、加权抽取与品级值的生成。数据全是合成的(五档样本随手写的小整数,倍率表随手写)。
/// 抽取的次序与边界来自服务端 generateEquipGrade 的静态读码(见 EquipGrade.cs 头部)。
/// </summary>
public class EquipGradeTests
{
    private static ScriptValue N(int value) => new(ScriptValueKind.Int, value, null);

    private static ScriptLine Band(int band, params int[] values) => new("[grade_" + band + "]", values.Select(N).ToArray());

    private static EquipGradeSamples Samples()
    {
        var lines = new[]
        {
            Band(0, 111, 5, 6), Band(1, 222, 7), Band(2, 333), Band(3, 444, 8, 9, 10), Band(4, 555, 1),
        };
        Assert.True(EquipGradeSamples.TryParse(lines, out EquipGradeSamples samples));
        return samples;
    }

    // ------------------------------------------------------------------ 样本表

    [Fact]
    public void FiveBandsAreReadWithTheirSamplesInOrder()
    {
        EquipGradeSamples s = Samples();
        Assert.Equal((5, EquipGradeSamples.Bands), (s.BandCount, 5));
        Assert.Equal(new[] { 111, 5, 6 }, s.Samples(0));
        Assert.Equal(new[] { 444, 8, 9, 10 }, s.Samples(3));
        Assert.Equal(new[] { 111, 222, 333, 444, 555 }, Enumerable.Range(0, 5).Select(s.Representative));
    }

    [Fact]
    public void AnOutOfRangeBandHasNoSamplesAndRepresentsZero()
    {
        EquipGradeSamples s = Samples();
        Assert.Empty(s.Samples(5));
        Assert.Empty(s.Samples(-1));
        Assert.Equal(0, s.Representative(5));
        Assert.Equal(0, s.Representative(-1));
    }

    [Fact]
    public void ARepresentativeMapsBackToItsBand()
    {
        EquipGradeSamples s = Samples();
        Assert.Equal(0, s.BandOf(111));
        Assert.Equal(4, s.BandOf(555));
        Assert.Equal(-1, s.BandOf(5));      // 只认每档的第一个样本
        Assert.Equal(-1, s.BandOf(0));
    }

    [Fact]
    public void ABandWithNoSamplesRepresentsZeroAndMapsToNothing()
    {
        Assert.True(EquipGradeSamples.TryParse(new[] { Band(0, 10), Band(1) }, out EquipGradeSamples s));
        Assert.Equal(2, s.BandCount);
        Assert.Equal(0, s.Representative(1));
        Assert.Equal(-1, s.BandOf(0));
    }

    [Fact]
    public void BandsMustStartAtZeroAndBeContiguous()
    {
        Assert.False(EquipGradeSamples.TryParse(new[] { Band(1, 10), Band(2, 20) }, out EquipGradeSamples none));
        Assert.Null(none);
        Assert.False(EquipGradeSamples.TryParse(new[] { Band(0, 10), Band(2, 20) }, out _));
        Assert.False(EquipGradeSamples.TryParse(new ScriptLine[0], out _));
    }

    [Fact]
    public void OtherTagsAndMalformedBandTagsAreIgnored()
    {
        var lines = new[]
        {
            new ScriptLine("[something]", new[] { N(1) }),
            Band(0, 10),
            new ScriptLine("[grade_x]", new[] { N(99) }),
            new ScriptLine("[grade_]", new[] { N(98) }),
            new ScriptLine("[grade_-1]", new[] { N(97) }),
            new ScriptLine("[grade_1", new[] { N(96) }),
            new ScriptLine("[/grade_0]", new ScriptValue[0]),
            new ScriptLine("[GRADE_1]", new[] { N(20) }),
        };
        Assert.True(EquipGradeSamples.TryParse(lines, out EquipGradeSamples s));
        Assert.Equal(2, s.BandCount);
        Assert.Equal(new[] { 10, 20 }, Enumerable.Range(0, 2).Select(s.Representative));
    }

    [Fact]
    public void OnlyNumbersCountAsSamples()
    {
        var line = new ScriptLine("[grade_0]", new[] { N(10), new ScriptValue(ScriptValueKind.String, 0, "text"), N(20) });
        Assert.True(EquipGradeSamples.TryParse(new[] { line }, out EquipGradeSamples s));
        Assert.Equal(new[] { 10, 20 }, s.Samples(0));
    }

    [Fact]
    public void ALaterLineForTheSameBandReplacesTheEarlierOne()
    {
        Assert.True(EquipGradeSamples.TryParse(new[] { Band(0, 1, 2), Band(0, 3) }, out EquipGradeSamples s));
        Assert.Equal(new[] { 3 }, s.Samples(0));
    }

    [Fact]
    public void NullLinesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => EquipGradeSamples.TryParse(null, out _));
    }

    // ------------------------------------------------------------------ 倍率表

    private static readonly KeyValuePair<int, float>[] Table =
    {
        new(30, 4f), new(20, 3f), new(10, 2f),
    };

    [Theory]
    [InlineData(35, 4f)]     // 比第一项的键大:取第一项
    [InlineData(31, 4f)]
    [InlineData(30, 4f)]     // 键相等
    [InlineData(25, 1f)]     // 落在两项之间:没有相等的键,保持 1
    [InlineData(20, 3f)]
    [InlineData(15, 1f)]
    [InlineData(10, 2f)]
    [InlineData(9, 2f)]      // 比最后一项的键小:取最后一项
    [InlineData(0, 2f)]
    [InlineData(-5, 2f)]
    public void TheMultiplierIsLookedUpTheWayTheServerDoes(int level, float expected)
    {
        Assert.Equal(expected, EquipGradeGenerator.MultiplierFor(Table, level));
    }

    [Fact]
    public void AnEmptyTableMultipliesByOne()
    {
        Assert.Equal(1f, EquipGradeGenerator.MultiplierFor(null, 50));
        Assert.Equal(1f, EquipGradeGenerator.MultiplierFor(new KeyValuePair<int, float>[0], 50));
    }

    [Fact]
    public void ARepeatedKeyTakesTheLastOne()
    {
        var table = new KeyValuePair<int, float>[] { new(30, 4f), new(20, 3f), new(20, 5f), new(10, 2f) };
        Assert.Equal(5f, EquipGradeGenerator.MultiplierFor(table, 20));
    }

    [Fact]
    public void ALevelEqualToTheFirstKeyDoesNotTakeTheFirstBranch()
    {
        // 第一项的键"小于"等级才直接取第一项;键相等时走后面的分支(有重复键取最后一个)
        var duplicated = new KeyValuePair<int, float>[] { new(30, 4f), new(30, 6f), new(10, 2f) };
        Assert.Equal(6f, EquipGradeGenerator.MultiplierFor(duplicated, 30));
        // 不是从大到小排的表:等级等于第一项的键、又小于最后一项的键,取最后一项
        var ascending = new KeyValuePair<int, float>[] { new(10, 2f), new(20, 3f) };
        Assert.Equal(3f, EquipGradeGenerator.MultiplierFor(ascending, 10));
    }

    [Fact]
    public void ASingleEntryTableUsesItsValueAboveAndBelowAndOneOnlyWhenEqual()
    {
        var table = new KeyValuePair<int, float>[] { new(10, 2.5f) };
        Assert.Equal(2.5f, EquipGradeGenerator.MultiplierFor(table, 11));   // 比键大:第一项
        Assert.Equal(2.5f, EquipGradeGenerator.MultiplierFor(table, 9));    // 比键小:最后一项(同一项)
        Assert.Equal(2.5f, EquipGradeGenerator.MultiplierFor(table, 10));   // 相等
    }

    // ------------------------------------------------------------------ 加权抽取

    private static readonly int[] Equal5 = { 1000, 1000, 1000, 1000, 1000 };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 0)]    // 累计值本身归当前这一档
    [InlineData(1001, 1)]
    [InlineData(2000, 1)]
    [InlineData(2001, 2)]
    [InlineData(3000, 2)]
    [InlineData(3001, 3)]
    [InlineData(4000, 3)]
    [InlineData(4001, 4)]
    [InlineData(5000, 4)]    // 上界含在内
    public void TheRollPicksTheFirstBandWhoseRunningTotalIsNotBelowIt(int roll, int expected)
    {
        var random = new ScriptedRandom(roll);
        Assert.Equal(expected, EquipGradeGenerator.Pick(Equal5, 1f, random));
        Assert.Equal(new[] { 5000 }, random.Maxes);
    }

    [Theory]
    [InlineData(2f, 6000, 2000, 0)]
    [InlineData(2f, 6000, 2001, 1)]
    [InlineData(2f, 6000, 6000, 4)]
    [InlineData(0.5f, 4500, 500, 0)]
    [InlineData(0.5f, 4500, 501, 1)]
    [InlineData(3f, 7000, 3000, 0)]
    [InlineData(3f, 7000, 3001, 1)]
    public void OnlyTheFirstBandsWeightIsScaledAndTheTotalFollows(float multiplier, int total, int roll, int expected)
    {
        var random = new ScriptedRandom(roll);
        Assert.Equal(expected, EquipGradeGenerator.Pick(Equal5, multiplier, random));
        Assert.Equal(new[] { total }, random.Maxes);
    }

    [Fact]
    public void AZeroMultiplierRemovesBandZeroExceptForARollOfZero()
    {
        // 第 0 档权重变 0:累计值 0,抽到 0 仍然"不大于累计值"——服务端同样(推断自比较方向,没有运行)
        Assert.Equal(0, EquipGradeGenerator.Pick(Equal5, 0f, new ScriptedRandom(0)));
        Assert.Equal(1, EquipGradeGenerator.Pick(Equal5, 0f, new ScriptedRandom(1)));
        var random = new ScriptedRandom(4000);
        Assert.Equal(4, EquipGradeGenerator.Pick(Equal5, 0f, random));
        Assert.Equal(new[] { 4000 }, random.Maxes);
    }

    [Fact]
    public void TheScaledWeightIsTruncatedNotRounded()
    {
        var a = new ScriptedRandom(0);
        EquipGradeGenerator.Pick(new[] { 1000, 1000 }, 1.9999f, a);
        Assert.Equal(new[] { 2999 }, a.Maxes);    // 1000 * 1.9999 = 1999.9 → 1999,总权重 1999 + 1000
        var b = new ScriptedRandom(0);
        EquipGradeGenerator.Pick(new[] { 1000, 1000 }, 1.0005f, b);
        Assert.Equal(new[] { 2000 }, b.Maxes);    // 1000.49996 → 1000
    }

    [Fact]
    public void NoWeightsOrANonPositiveTotalPicksNothing()
    {
        Assert.Equal(-1, EquipGradeGenerator.Pick(null, 1f, new ScriptedRandom()));
        Assert.Equal(-1, EquipGradeGenerator.Pick(new int[0], 1f, new ScriptedRandom()));
        Assert.Equal(-1, EquipGradeGenerator.Pick(new[] { 0, 0 }, 1f, new ScriptedRandom()));
        Assert.Equal(-1, EquipGradeGenerator.Pick(new[] { -5, 3 }, 1f, new ScriptedRandom()));
        Assert.Equal(-1, EquipGradeGenerator.Pick(new[] { int.MaxValue, 5 }, 1f, new ScriptedRandom()));
    }

    [Fact]
    public void ARandomSourceIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => EquipGradeGenerator.Pick(Equal5, 1f, null));
    }

    [Fact]
    public void ASingleWeightAlwaysPicksIt()
    {
        Assert.Equal(0, EquipGradeGenerator.Pick(new[] { 7 }, 1f, new ScriptedRandom(7)));
        Assert.Equal(0, EquipGradeGenerator.Pick(new[] { 7 }, 1f, new ScriptedRandom(0)));
    }

    // ------------------------------------------------------------------ 生成品级值

    [Fact]
    public void AnEmptyMultiplierTableGivesBandZeroWithoutRolling()
    {
        var random = new ScriptedRandom();
        Assert.Equal(111, EquipGradeGenerator.Generate(Samples(), random, null, 50));
        Assert.Equal(111, EquipGradeGenerator.Generate(Samples(), random, new KeyValuePair<int, float>[0], 50));
        Assert.Equal(111, EquipGradeGenerator.Generate(Samples(), null, null, 50));   // 不抽签就不需要随机源
        Assert.Empty(random.Maxes);
    }

    [Fact]
    public void TheRolledBandsRepresentativeIsTheGradeValue()
    {
        var table = new KeyValuePair<int, float>[] { new(10, 1f) };
        Assert.Equal(111, EquipGradeGenerator.Generate(Samples(), new ScriptedRandom(1000), table, 50));
        Assert.Equal(222, EquipGradeGenerator.Generate(Samples(), new ScriptedRandom(1001), table, 50));
        Assert.Equal(555, EquipGradeGenerator.Generate(Samples(), new ScriptedRandom(5000), table, 50));
    }

    [Fact]
    public void TheLevelsMultiplierScalesBandZero()
    {
        var table = new KeyValuePair<int, float>[] { new(10, 3f) };
        var random = new ScriptedRandom(3000);
        Assert.Equal(111, EquipGradeGenerator.Generate(Samples(), random, table, 50));   // 50 比 10 大:取第一项 3 倍
        Assert.Equal(new[] { 7000 }, random.Maxes);
    }

    [Fact]
    public void CustomWeightsReplaceTheEqualOnes()
    {
        var table = new KeyValuePair<int, float>[] { new(10, 1f) };
        var random = new ScriptedRandom(5);
        Assert.Equal(222, EquipGradeGenerator.Generate(Samples(), random, table, 50, new[] { 0, 10, 0, 0, 0 }));
        Assert.Equal(new[] { 10 }, random.Maxes);
    }

    [Fact]
    public void WhenNothingCanBePickedTheBandZeroValueIsGiven()
    {
        var table = new KeyValuePair<int, float>[] { new(10, 1f) };
        Assert.Equal(111, EquipGradeGenerator.Generate(Samples(), new ScriptedRandom(), table, 50, new[] { 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void ASampleTableIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => EquipGradeGenerator.Generate(null, new ScriptedRandom(), null, 1));
    }

    [Fact]
    public void WithARealGeneratorEveryBandShowsUpAtItsShare()
    {
        var table = new KeyValuePair<int, float>[] { new(10, 1f) };
        EquipGradeSamples s = Samples();
        var random = new DropRandom(13);
        var counts = new int[5];
        const int Trials = 25000;
        for (int i = 0; i < Trials; i++)
        {
            counts[s.BandOf(EquipGradeGenerator.Generate(s, random, table, 50))]++;
        }
        foreach (int c in counts)
        {
            Assert.InRange(c / (double)Trials, 0.18, 0.22);
        }
    }
}
