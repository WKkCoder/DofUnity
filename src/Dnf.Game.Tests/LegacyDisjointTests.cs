using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 旧版分解(没有分解机时走的路线,便携分解器用)。依据:服务端 LoadDisJoint 0x0847300c → importDisjointScript 0x088e4d88(etc 目录下的 disjoint.etc)、
/// DisJoint::GetResult 0x084733f6、_GetAdditionalItem 0x084732b4、_GetAdditionalItemInfo 0x08473224、GetPredictResult 0x08473116(静态读码)。
/// 合成数据(见 <see cref="ExpertJobTestData.LegacyLines"/>):主产物 950,基数 100,六档系数 1.0、1.5、2.0、2.5、3.0、3.5;
/// 附加产物组:稀有度 1 → 951、952,稀有度 2 → 953,稀有度 4 → 960,其余为空。价值 1000 的物品:价值 ×1.1 = 1100 → 11000 → 乘系数除 1000 → 11、16、22、27、33、38 个。
/// </summary>
public class LegacyDisjointTests
{
    private static readonly LegacyDisjointScript Script = Legacy();

    private static DisjointOutcome Run(DisjointItem item, ScriptedDisjointRandom rng, ItemPriceRates rates = null) => Script.Compute(item, rates, rng);

    // ------------------------------------------------------------------ 读取

    [Fact]
    public void TheScriptIsReadFromTheCubeAndAdditionalLines()
    {
        Assert.Equal(950, Script.MainItem);
        Assert.Equal(100, Script.BaseConst);
        Assert.Equal(new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.5f }, Enumerable.Range(0, 6).Select(Script.Factor).ToArray());
        Assert.Equal(new[] { 0, 2, 1, 0, 1, 0 }, Enumerable.Range(0, 6).Select(Script.GroupCount).ToArray());
        Assert.Equal(new[] { 951, 952 }, Script.Group(1).ToArray());
        Assert.Equal(new[] { 953 }, Script.Group(2).ToArray());
        Assert.Equal(new[] { 960 }, Script.Group(4).ToArray());
        Assert.Empty(Script.Group(0));
        Assert.Equal(new[] { 0, 20, 0, 0, 100, 0 }, Enumerable.Range(0, 6).Select(Script.BigPercent).ToArray());
    }

    [Fact]
    public void OutOfRangeRaritiesReadAsEmpty()
    {
        Assert.Equal(0f, Script.Factor(6));
        Assert.Equal(0f, Script.Factor(-1));
        Assert.Equal(0, Script.BigPercent(6));
        Assert.Equal(0, Script.GroupCount(-1));
        Assert.Empty(Script.Group(9));
        Assert.True(LegacyDisjointScript.InRange(0));
        Assert.True(LegacyDisjointScript.InRange(5));
        Assert.False(LegacyDisjointScript.InRange(6));
        Assert.False(LegacyDisjointScript.InRange(-1));
        Assert.Equal(6, LegacyDisjointScript.Rarities);
    }

    [Fact]
    public void OnlyTheNoElementCubeLineGivesTheMainItem()
    {
        LegacyDisjointScript onlyFire = LegacyDisjointScript.Parse(new[] { L("[cube index]", "[fire element]", 957, 957), L("[/cube index]") });
        Assert.Equal(0, onlyFire.MainItem);

        // 后写的"其余"行替换前面的;四种元素的行不影响主产物。
        LegacyDisjointScript twoLines = LegacyDisjointScript.Parse(new[]
        {
            L("[cube index]", "[no element]", 950, 951),
            L("[cube index]", "[water element]", 958),
            L("[cube index]", "[no element]", 960),
        });
        Assert.Equal(960, twoLines.MainItem);

        // 没有元素名(第一个值就是数)按"其余"处理。
        LegacyDisjointScript bare = LegacyDisjointScript.Parse(new[] { L("[cube index]", 955) });
        Assert.Equal(955, bare.MainItem);
    }

    [Fact]
    public void TheMainItemIsTheFirstValueOfTheNoElementLine()
    {
        // 一行里写了好几个物品时,主产物取第一个(后面的不是主产物)。
        LegacyDisjointScript several = LegacyDisjointScript.Parse(new[] { L("[cube index]", "[no element]", 950, 951, 952) });
        Assert.Equal(950, several.MainItem);
    }

    [Fact]
    public void TagsOfOtherFeaturesAreRegisteredAndClosersAreIgnored()
    {
        Assert.Contains("[skill info]", Script.RegisteredTags);
        Assert.Contains("[avatar disjoint info]", Script.RegisteredTags);
        Assert.DoesNotContain("[/cube index]", Script.RegisteredTags);
        Assert.DoesNotContain("[cube index]", Script.RegisteredTags);
        Assert.Equal(2, Script.RegisteredTags.Count);
    }

    [Fact]
    public void ANullOrEmptyScriptHasNothingAndCannotDisjoint()
    {
        LegacyDisjointScript empty = LegacyDisjointScript.Parse(null);
        Assert.Equal(0, empty.MainItem);
        Assert.Equal(0, empty.BaseConst);
        var rng = new ScriptedDisjointRandom();
        Assert.Equal(ExpertJobError.NotPermitted, empty.Compute(Candidate(), null, rng).Error);
        Assert.Empty(rng.Ranges);
    }

    // ------------------------------------------------------------------ 产物

    [Theory]
    [InlineData(0, 11)]
    [InlineData(3, 27)]     // 27.5 向零取整
    [InlineData(5, 38)]     // 38.5
    public void ARarityWithoutAnAdditionalGroupGivesOnlyTheMainProductAndDrawsNothing(int rarity, int count)
    {
        var rng = new ScriptedDisjointRandom();
        DisjointOutcome o = Run(Candidate(rarity: rarity), rng);
        Assert.True(o.Ok);
        Assert.Equal(new[] { 950, 0, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { count, 0, 0, 0, 0 }, o.Counts);
        Assert.Empty(rng.Ranges);
    }

    [Theory]
    [InlineData(0, 1999, 951, 2)]       // 下标 = 抽值 ÷ 1000:0 → 951;大成功线 20% = 2000,抽值 1999 小于它 → 大成功除数 10.0 → 20 ÷ 10 = 2
    [InlineData(999, 2000, 951, 4)]     // 抽值 2000 不小于 2000 → 普通除数 5.0 → 4
    [InlineData(1000, 0, 952, 2)]       // 下标 1 → 952
    [InlineData(1999, 9999, 952, 4)]
    public void ARarityOneItemPicksAnAdditionalItemByIndexAndASeparateBigWinRoll(int indexRoll, int bigRoll, int item, int count)
    {
        var rng = new ScriptedDisjointRandom(indexRoll, bigRoll);
        DisjointOutcome o = Run(Candidate(rarity: 1, grade: 20), rng);
        Assert.Equal(new[] { 950, item, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 16, count, 0, 0, 0 }, o.Counts);
        Assert.Equal(new[] { 2000, 10000 }, rng.Ranges);      // 先在 2 × 1000 里抽下标,再抽万分比
    }

    [Theory]
    [InlineData(4, 1)]      // 4 ÷ 5.0 = 0.8 → 向零取整 0,至少给 1 个
    [InlineData(0, 1)]      // 0 ÷ 5.0 = 0
    [InlineData(5, 1)]      // 5 ÷ 5.0 = 1 正好
    [InlineData(9, 1)]      // 9 ÷ 5.0 = 1.8 → 1
    [InlineData(10, 2)]
    public void TheAdditionalCountIsAtLeastOneWhateverTheGrade(int grade, int count)
    {
        // 抽值 5000 不小于大成功线 2000 → 普通除数 5.0。
        DisjointOutcome o = Run(Candidate(rarity: 1, grade: grade), new ScriptedDisjointRandom(0, 5000));
        Assert.Equal(951, o.Items[1]);
        Assert.Equal(count, o.Counts[1]);
    }

    [Fact]
    public void ADivisorOfZeroStillGivesOneAdditionalItem()
    {
        LegacyDisjointScript script = LegacyDisjointScript.Parse(new[]
        {
            L("[cube index]", "[no element]", 950),
            L("[cube creation const]", 100, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f),
            L("[additional result]", 0, 1, 951),
            L("[additional result const]", 0, 5.0f, 0.0f, 0, 0),
        });
        DisjointOutcome o = script.Compute(Candidate(rarity: 1, grade: 20), null, new ScriptedDisjointRandom(0, 5000));
        Assert.Equal(951, o.Items[1]);
        Assert.Equal(1, o.Counts[1]);       // 20 ÷ 0 = 无穷大,转成整数是不定值,不大于 0 → 按 1 个
    }

    [Fact]
    public void ARarityTwoItemNeverBigWinsBecauseItsPercentIsZero()
    {
        var rng = new ScriptedDisjointRandom(0, 0);
        DisjointOutcome o = Run(Candidate(rarity: 2, grade: 20), rng);
        Assert.Equal(new[] { 950, 953, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 22, 20, 0, 0, 0 }, o.Counts);    // 普通除数 1.0 → 20
        Assert.Equal(new[] { 1000, 10000 }, rng.Ranges);
    }

    [Fact]
    public void ARarityFourItemAlwaysBigWinsBecauseItsPercentIsAHundred()
    {
        var rng = new ScriptedDisjointRandom(0, 9999);
        DisjointOutcome o = Run(Candidate(rarity: 4, grade: 20), rng);
        Assert.Equal(new[] { 950, 960, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 33, 5, 0, 0, 0 }, o.Counts);     // 大成功除数 4.0 → 5
    }

    [Fact]
    public void AGroupMemberOfZeroAddsNothingAndSkipsTheSecondDraw()
    {
        LegacyDisjointScript script = LegacyDisjointScript.Parse(new[]
        {
            L("[cube index]", "[no element]", 950),
            L("[cube creation const]", 100, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f),
            L("[additional result]", 0, 1, 0),
            L("[additional result const]", 0, 5.0f, 2.0f, 50, 0),
        });
        var rng = new ScriptedDisjointRandom(0);
        DisjointOutcome o = script.Compute(Candidate(rarity: 1), null, rng);
        Assert.Equal(new[] { 950, 0, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 1000 }, rng.Ranges);
    }

    [Fact]
    public void AnIndexBeyondTheListedMembersAddsNothing()
    {
        // 组大小写了 3 个,实际只有一个物品:下标抽到 2 时没有这一项。
        LegacyDisjointScript script = LegacyDisjointScript.Parse(new[]
        {
            L("[cube index]", "[no element]", 950),
            L("[cube creation const]", 100, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f),
            L("[additional result]", 0, 3, 951),
            L("[additional result const]", 0, 5.0f, 2.0f, 50, 0),
        });
        var rng = new ScriptedDisjointRandom(2500);
        DisjointOutcome o = script.Compute(Candidate(rarity: 1), null, rng);
        Assert.Equal(new[] { 950, 0, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 3000 }, rng.Ranges);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(100)]
    public void ARarityOutsideTheTableIsInvalid(int rarity)
    {
        var rng = new ScriptedDisjointRandom();
        Assert.Equal(ExpertJobError.InvalidItem, Run(Candidate(rarity: rarity), rng).Error);
        Assert.Empty(rng.Ranges);
    }

    [Fact]
    public void AScriptWithoutABaseConstantOrMainItemIsNotPermitted()
    {
        LegacyDisjointScript noBase = LegacyDisjointScript.Parse(new[] { L("[cube index]", "[no element]", 950), L("[cube creation const]", 0, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f) });
        Assert.Equal(ExpertJobError.NotPermitted, noBase.Compute(Candidate(), null, new ScriptedDisjointRandom()).Error);
        LegacyDisjointScript noMain = LegacyDisjointScript.Parse(new[] { L("[cube creation const]", 100, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f) });
        Assert.Equal(ExpertJobError.NotPermitted, noMain.Compute(Candidate(), null, new ScriptedDisjointRandom()).Error);
    }

    [Fact]
    public void ARandomOptionItemIsPricedAtTheThirdRate()
    {
        var rates = new ItemPriceRates(800, 900, 700);
        Assert.Equal(11, Run(Candidate(rarity: 1, randomOption: true), new ScriptedDisjointRandom(0, 5000), rates).Counts[0]);   // 700‰:770 → 7700 → ×1.5 ÷ 1000 = 11.55
        Assert.Equal(13, Run(Candidate(rarity: 1), new ScriptedDisjointRandom(0, 5000), rates).Counts[0]);                      // 800‰:880 → 8800 → 13.2
    }

    [Fact]
    public void TheMainCountIsAtLeastOne()
    {
        Assert.Equal(1, Run(Candidate(rarity: 0, value: 1), new ScriptedDisjointRandom()).Counts[0]);
    }

    [Fact]
    public void ComputeRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => Script.Compute(null, null, new ScriptedDisjointRandom()));
        Assert.Throws<ArgumentNullException>(() => Script.Compute(Candidate(), null, null));
    }

    // ------------------------------------------------------------------ 预览

    [Theory]
    [InlineData(0, new[] { 950 })]
    [InlineData(1, new[] { 950, 951, 952 })]
    [InlineData(2, new[] { 950, 953 })]
    [InlineData(3, new[] { 950 })]
    [InlineData(4, new[] { 950, 960 })]
    [InlineData(5, new[] { 950 })]
    [InlineData(6, new[] { 950 })]        // 超出范围时服务端返回第 0 张表
    [InlineData(-3, new[] { 950 })]
    public void ThePreviewIsTheMainProductPlusTheRarityGroup(int rarity, int[] expected)
    {
        Assert.Equal(expected, Script.Predict(rarity).ToArray());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-3)]
    [InlineData(int.MaxValue)]
    public void ThePreviewOfARarityOutsideTheTableIsTheFirstTableNotTheLastOne(int rarity)
    {
        // 第 0 张表的附加产物是 951,最后一张(稀有度 5)是 955:超出范围读第 0 张。
        LegacyDisjointScript script = LegacyDisjointScript.Parse(new[]
        {
            L("[cube index]", "[no element]", 950),
            L("[additional result]", 1, 951, 0, 0, 0, 0, 1, 955),
        });
        Assert.Equal(new[] { 950, 951 }, script.Predict(0).ToArray());
        Assert.Equal(new[] { 950, 955 }, script.Predict(5).ToArray());
        Assert.Equal(new[] { 950, 951 }, script.Predict(rarity).ToArray());
    }

    [Fact]
    public void ThePreviewWithoutAMainItemListsOnlyTheGroup()
    {
        LegacyDisjointScript script = LegacyDisjointScript.Parse(new[] { L("[additional result]", 0, 2, 951, 952) });
        Assert.Equal(new[] { 951, 952 }, script.Predict(1).ToArray());
        Assert.Empty(script.Predict(0));
    }
}
