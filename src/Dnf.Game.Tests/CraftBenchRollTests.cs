using static Dnf.Game.Tests.CraftKit;

namespace Dnf.Game.Tests;

/// <summary>
/// 成功率与失败消耗(规则取自服务端 _ProcCompoundCore / isSuccessCompound,见交接文档):成功率为 0 不掷骰必成;一次制作只掷一次;
/// 掷出的数小于成功率才成功;失败时材料照扣、产物换成失败产物表(不乘数量,表为空什么也得不到)。骰子用预先给定的数代替,编号全是合成的。
/// </summary>
public class CraftBenchRollTests
{
    /// <summary>矿×1 做 药水×1;成功率 <paramref name="rate"/>;失败给 废料×2。</summary>
    private static RecipeDef WithRate(int rate) => Recipe().In(Ore, 1).Out(Potion, 1).Rate(rate).Fail(Scrap, 2).Build();

    private static RecipeBook Learned(RecipeDef recipe)
    {
        var book = new RecipeBook();
        book.Learn(recipe.Code);
        return book;
    }

    private static CraftReport Craft(RecipeDef recipe, Inventory bag, ICraftRoll roll, int count = 1, ICraftGate gate = null, ICraftPurse purse = null)
    {
        return Bench(roll, gate).CraftLearned(recipe, Learned(recipe), bag, purse ?? new CraftPurse(), count);
    }

    // ------------------------------------------------------------------ 要不要掷骰

    [Fact]
    public void ASuccessRateOfZeroNeverRollsAndAlwaysSucceeds()
    {
        var roll = new ScriptedRoll();
        Inventory bag = Bag((Ore, 1));
        CraftReport report = Craft(WithRate(0), bag, roll);
        Assert.True(report.Ok);
        Assert.True(report.Success);
        Assert.Equal(0, roll.Calls);
        Assert.Equal(-1, report.Roll);
        Assert.Equal(1, bag.CountOf(Potion));
        Assert.Equal(0, bag.CountOf(Scrap));
    }

    [Fact]
    public void ARecipeWithoutSuccessRateNeverRolls()
    {
        var roll = new ScriptedRoll();
        RecipeDef plain = Recipe().In(Ore, 1).Out(Potion, 1).Fail(Scrap, 2).Build();
        Assert.True(Craft(plain, Bag((Ore, 1)), roll).Success);
        Assert.Equal(0, roll.Calls);
    }

    [Fact]
    public void TheRollIsOverZeroToNinetyNine()
    {
        var roll = new ScriptedRoll(10);
        Craft(WithRate(50), Bag((Ore, 1)), roll);
        Assert.Equal(1, roll.Calls);
        Assert.Equal(CraftBench.RollRange, roll.LastRange);
        Assert.Equal(100, CraftBench.RollRange);
    }

    // ------------------------------------------------------------------ 成败的分界

    [Theory]
    [InlineData(0, true)]
    [InlineData(49, true)]
    [InlineData(50, false)]
    [InlineData(99, false)]
    public void ARollBelowTheRateSucceedsAndOneAtOrAboveItFails(int rolled, bool success)
    {
        Inventory bag = Bag((Ore, 1));
        CraftReport report = Craft(WithRate(50), bag, new ScriptedRoll(rolled));
        Assert.True(report.Ok);
        Assert.Equal(success, report.Success);
        Assert.Equal(rolled, report.Roll);
        Assert.Equal(success ? 1 : 0, bag.CountOf(Potion));
        Assert.Equal(success ? 0 : 2, bag.CountOf(Scrap));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ARateOfOnePercentOnlySucceedsOnAZeroRoll(int rolled, bool success)
    {
        Assert.Equal(success, Craft(WithRate(1), Bag((Ore, 1)), new ScriptedRoll(rolled)).Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void ARateOfOneHundredPercentNeverFailsButStillRolls(int rolled)
    {
        // 成功率不为 0 就掷骰(isSuccessCompound 只对 0 豁免),哪怕 100 这个骰子不可能掷输。
        var roll = new ScriptedRoll(rolled);
        CraftReport report = Craft(WithRate(100), Bag((Ore, 1)), roll);
        Assert.True(report.Success);
        Assert.Equal(1, roll.Calls);
        Assert.Equal(rolled, report.Roll);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    public void ARateAboveNinetyNineStillRollsOnceAndSucceedsOnEveryRealRoll(int rate)
    {
        var roll = new ScriptedRoll(99);
        Assert.True(Craft(WithRate(rate), Bag((Ore, 1)), roll).Success);
        Assert.Equal(1, roll.Calls);
    }

    // ------------------------------------------------------------------ 失败的代价

    [Fact]
    public void AFailureStillConsumesEveryMaterialAndGivesTheFailOutputsInstead()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.GoldCode, 100).In(Ore, 2).Bead(Herb, 1).Out(Potion, 5).Rate(40).Fail(Scrap, 2).Build();
        var purse = new CraftPurse(300);
        Inventory bag = Bag((Ore, 3), (Herb, 1));
        CraftReport report = Craft(recipe, bag, new ScriptedRoll(70), purse: purse);
        Assert.True(report.Ok);
        Assert.False(report.Success);
        Assert.Equal(1, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
        Assert.Equal(200, purse.Gold);
        Assert.Equal(100, report.GoldSpent);
        Assert.Equal(0, bag.CountOf(Potion));
        Assert.Equal(2, bag.CountOf(Scrap));
        Assert.Equal(new[] { new RecipeItem(Scrap, 2) }, report.Granted);
        Assert.Equal(new[] { new RecipeItem(Ore, 2), new RecipeItem(Herb, 1) }, report.Consumed);
    }

    [Fact]
    public void ASuccessGivesTheProductsAndNoFailOutputs()
    {
        Inventory bag = Bag((Ore, 1));
        CraftReport report = Craft(WithRate(40), bag, new ScriptedRoll(39));
        Assert.True(report.Success);
        Assert.Equal(1, bag.CountOf(Potion));
        Assert.Equal(0, bag.CountOf(Scrap));
        Assert.Equal(new[] { new RecipeItem(Potion, 1) }, report.Granted);
    }

    [Fact]
    public void AFailureWithNoFailOutputsGivesNothingButStillConsumes()
    {
        RecipeDef recipe = Recipe().In(Ore, 2).Out(Potion, 1).Rate(10).Build();
        Inventory bag = Bag((Ore, 2));
        CraftReport report = Craft(recipe, bag, new ScriptedRoll(50));
        Assert.True(report.Ok);
        Assert.False(report.Success);
        Assert.Equal(0, bag.TotalCount);
        Assert.Empty(report.Granted);
    }

    // ------------------------------------------------------------------ 一次制作只掷一次

    [Fact]
    public void SeveralCopiesAreOneRollNotOneRollEach()
    {
        var roll = new ScriptedRoll(10);
        Inventory bag = Bag((Ore, 5));
        CraftReport report = Craft(WithRate(50), bag, roll, count: 5);
        Assert.True(report.Success);
        Assert.Equal(1, roll.Calls);
        Assert.Equal(5, bag.CountOf(Potion));
        Assert.Equal(0, bag.CountOf(Ore));
    }

    [Fact]
    public void AFailureOnSeveralCopiesTakesAllTheMaterialsAndGivesTheFailOutputsOnlyOnce()
    {
        var roll = new ScriptedRoll(90);
        Inventory bag = Bag((Ore, 5));
        CraftReport report = Craft(WithRate(50), bag, roll, count: 5);
        Assert.True(report.Ok);
        Assert.False(report.Success);
        Assert.Equal(1, roll.Calls);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Potion));
        Assert.Equal(2, bag.CountOf(Scrap));
    }

    // ------------------------------------------------------------------ 背包放不下

    [Fact]
    public void ASuccessThatFitsButAFailureThatDoesNotIsRefusedOnlyWhenTheRollFails()
    {
        // 消耗品页三格全满,药水能并进去(成功放得下),以太放不下(失败产物放不下)。
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Rate(50).Fail(Ether, 1).Build();
        Inventory failing = Bag(3, (Potion, 5), (Elixir, 5), (Tonic, 5), (Ore, 1));
        string before = Dump(failing);
        CraftReport refused = Craft(recipe, failing, new ScriptedRoll(80));
        Assert.Equal(CraftResult.BagFull, refused.Result);
        Assert.Equal(before, Dump(failing));

        Inventory succeeding = Bag(3, (Potion, 5), (Elixir, 5), (Tonic, 5), (Ore, 1));
        CraftReport ok = Craft(recipe, succeeding, new ScriptedRoll(10));
        Assert.True(ok.Success);
        Assert.Equal(6, succeeding.CountOf(Potion));
    }

    [Fact]
    public void ASuccessThatDoesNotFitIsRefusedWithoutRolling()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Ether, 1).Rate(50).Fail(Scrap, 1).Build();
        var roll = new ScriptedRoll();
        Inventory bag = Bag(3, (Potion, 5), (Elixir, 5), (Tonic, 5), (Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.BagFull, Craft(recipe, bag, roll).Result);
        Assert.Equal(0, roll.Calls);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void AnUnknownFailOutputCodeRefusesTheCraftBeforeRolling()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Rate(50).Fail(999, 1).Build();
        var roll = new ScriptedRoll();
        Inventory bag = Bag((Ore, 1));
        Assert.Equal(CraftResult.NoProduct, Craft(recipe, bag, roll).Result);
        Assert.Equal(0, roll.Calls);
        Assert.Equal(1, bag.CountOf(Ore));
    }

    [Fact]
    public void AGoldFailOutputRefusesTheCraftBeforeRollingEvenIfTheItemTableHasACodeZeroItem()
    {
        var zero = new ItemInfo(0, "stackable/test/s0.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material };
        var roll = new ScriptedRoll();
        var bench = new CraftBench(code => code == 0 ? zero : Lookup(code), roll);
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Rate(50).Fail(RecipeItem.GoldCode, 10).Build();
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        CraftReport report = bench.CraftLearned(recipe, Learned(recipe), bag, new CraftPurse(), 1);
        Assert.Equal(CraftResult.NoProduct, report.Result);
        Assert.Equal(0, roll.Calls);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void MissingMaterialsNeverReachTheRoll()
    {
        var roll = new ScriptedRoll();
        Assert.Equal(CraftResult.MissingMaterials, Craft(WithRate(50), Bag(), roll).Result);
        Assert.Equal(0, roll.Calls);
    }

    // ------------------------------------------------------------------ 闸门

    [Fact]
    public void TheGateIsToldAboutAFailedRollToo()
    {
        var gate = new RecordingGate();
        CraftReport report = Craft(WithRate(50), Bag((Ore, 1)), new ScriptedRoll(99), gate: gate);
        Assert.False(report.Success);
        Assert.Single(gate.Crafted);
        Assert.Same(report, gate.Crafted[0].Report);
    }
}
