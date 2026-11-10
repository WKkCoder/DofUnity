using static Dnf.Game.Tests.CraftKit;

namespace Dnf.Game.Tests;

/// <summary>
/// 学会配方与"学前不能做、学后能做"(本项目自己的设计:服务端只有副职业配方要学,规则取自服务端读码;依据与偏差见交接文档)。
/// 编号全是合成的:设计图(配方物品)900,材料矿 901、草 902,产物药水 910。
/// </summary>
public class CraftBenchLearnTests
{
    private static RecipeDef Standard() => Recipe().In(Ore, 3).In(Herb, 1).Out(Potion, 2).Build();

    // ------------------------------------------------------------------ 学习

    [Fact]
    public void LearningConsumesExactlyOneRecipeItemAndRecordsTheRecipe()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 3), (Ore, 4));
        Assert.Equal(LearnResult.Ok, Bench().Learn(Standard(), book, bag));
        Assert.True(book.IsLearned(RecipeCode));
        Assert.Equal(1, book.Count);
        Assert.Equal(2, bag.CountOf(Blueprint));
        Assert.Equal(4, bag.CountOf(Ore));
    }

    [Fact]
    public void LearningTheLastRecipeItemEmptiesItsSlot()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 1));
        Assert.Equal(LearnResult.Ok, Bench().Learn(Standard(), book, bag));
        Assert.Equal(0, bag.CountOf(Blueprint));
        Assert.Equal(0, bag.StackCount);
    }

    [Fact]
    public void LearningARecipeAlreadyKnownRefusesAndKeepsTheItem()
    {
        var book = new RecipeBook();
        book.Learn(RecipeCode);
        Inventory bag = Bag((Blueprint, 2));
        Assert.Equal(LearnResult.AlreadyLearned, Bench().Learn(Standard(), book, bag));
        Assert.Equal(2, bag.CountOf(Blueprint));
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void LearningWithoutTheRecipeItemIsRefused()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Ore, 9));
        string before = Dump(bag);
        Assert.Equal(LearnResult.NoRecipeItem, Bench().Learn(Standard(), book, bag));
        Assert.False(book.IsLearned(RecipeCode));
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ALockedLearnGateRefusesAndKeepsTheItem()
    {
        var gate = new RecordingGate { AllowLearn = false };
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 1));
        Assert.Equal(LearnResult.NotAllowed, Bench(gate: gate).Learn(Standard(), book, bag));
        Assert.False(book.IsLearned(RecipeCode));
        Assert.Equal(1, bag.CountOf(Blueprint));
        Assert.Equal(1, gate.LearnAsked);
        Assert.Equal(0, gate.CraftAsked);
    }

    [Fact]
    public void AMissingOrMalformedRecipeCannotBeLearned()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 1));
        Assert.Equal(LearnResult.UnknownRecipe, Bench().Learn(null, book, bag));
        Assert.Equal(LearnResult.UnknownRecipe, Bench().Learn(Recipe().In(Ore, 1).Out(Potion, 1).Malformed().Build(), book, bag));
        Assert.Equal(1, bag.CountOf(Blueprint));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void LearningTwoDifferentRecipesTakesOneItemEach()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 1), (Blueprint2, 1), (Ore, 2));
        RecipeDef first = Standard();
        RecipeDef second = Recipe(Blueprint2).In(Herb, 1).Out(Potion, 1).Build();
        Assert.Equal(LearnResult.Ok, Bench().Learn(first, book, bag));
        Assert.Equal(1, bag.CountOf(Blueprint2));
        Assert.Equal(LearnResult.Ok, Bench().Learn(second, book, bag));
        Assert.Equal(new[] { RecipeCode, Blueprint2 }, book.Codes);
        Assert.Equal(0, bag.CountOf(Blueprint));
        Assert.Equal(0, bag.CountOf(Blueprint2));
        Assert.Equal(2, bag.CountOf(Ore));
    }

    [Fact]
    public void LearnArgumentsMustNotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => Bench().Learn(Standard(), null, Bag()));
        Assert.Throws<ArgumentNullException>(() => Bench().Learn(Standard(), new RecipeBook(), null));
        Assert.Throws<ArgumentNullException>(() => new CraftBench(null));
    }

    // ------------------------------------------------------------------ 学前不能做、学后能做

    [Fact]
    public void BeforeLearningTheRecipeCannotBeCraftedEvenWithEveryMaterial()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 1), (Ore, 9), (Herb, 9));
        string before = Dump(bag);
        CraftReport report = Bench().CraftLearned(Standard(), book, bag, new CraftPurse(), 1);
        Assert.Equal(CraftResult.NotLearned, report.Result);
        Assert.False(report.Ok);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void AfterLearningTheSameCraftSucceedsAndTakesExactlyTheMaterials()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 1), (Ore, 9), (Herb, 9));
        CraftBench bench = Bench();
        Assert.Equal(CraftResult.NotLearned, bench.CraftLearned(Standard(), book, bag, new CraftPurse(), 1).Result);
        Assert.Equal(LearnResult.Ok, bench.Learn(Standard(), book, bag));

        CraftReport report = bench.CraftLearned(Standard(), book, bag, new CraftPurse(), 1);
        Assert.Equal(CraftResult.Ok, report.Result);
        Assert.Equal(6, bag.CountOf(Ore));
        Assert.Equal(8, bag.CountOf(Herb));
        Assert.Equal(2, bag.CountOf(Potion));
        Assert.Equal(0, bag.CountOf(Blueprint));
    }

    [Fact]
    public void ACraftByLearnedRecipeNeverTouchesTheRecipeItem()
    {
        var book = new RecipeBook();
        book.Learn(RecipeCode);
        Inventory bag = Bag((Blueprint, 2), (Ore, 3), (Herb, 1));
        Assert.True(Bench().CraftLearned(Standard(), book, bag, new CraftPurse(), 1).Ok);
        Assert.Equal(2, bag.CountOf(Blueprint));
    }

    [Fact]
    public void LearningOneRecipeDoesNotUnlockAnother()
    {
        var book = new RecipeBook();
        book.Learn(RecipeCode);
        RecipeDef other = Recipe(Blueprint2).In(Ore, 1).Out(Potion, 1).Build();
        Inventory bag = Bag((Ore, 5));
        Assert.Equal(CraftResult.NotLearned, Bench().CraftLearned(other, book, bag, new CraftPurse(), 1).Result);
        Assert.Equal(5, bag.CountOf(Ore));
    }

    [Fact]
    public void CraftingWithoutABookIsTheSameAsNotLearned()
    {
        Inventory bag = Bag((Ore, 3), (Herb, 1));
        Assert.Equal(CraftResult.NotLearned, Bench().CraftLearned(Standard(), null, bag, new CraftPurse(), 1).Result);
        Assert.Equal(3, bag.CountOf(Ore));
    }
}
