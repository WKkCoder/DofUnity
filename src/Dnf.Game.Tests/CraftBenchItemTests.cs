using static Dnf.Game.Tests.CraftKit;

namespace Dnf.Game.Tests;

/// <summary>
/// 按背包里的配方物品制作(服务端普通配方的路径:不查"已学"、每做一份消耗一件配方物品;规则取自服务端 compound_item,见交接文档)。
/// 与按已学配方制作共用同一套材料与产物规则,这里只测配方物品这一层的差别。编号全是合成的。
/// </summary>
public class CraftBenchItemTests
{
    private static RecipeDef Standard() => Recipe().In(Ore, 3).In(Herb, 1).Out(Potion, 2).Build();

    [Fact]
    public void EachCopyConsumesOneRecipeItemAndNoLearningIsNeeded()
    {
        Inventory bag = Bag((Blueprint, 3), (Ore, 6), (Herb, 2));
        CraftReport report = Bench().CraftFromItem(Standard(), bag, new CraftPurse(), 2);
        Assert.True(report.Ok);
        Assert.Equal(1, bag.CountOf(Blueprint));
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
        Assert.Equal(4, bag.CountOf(Potion));
        Assert.Equal(2, report.RecipeItemsConsumed);
    }

    [Fact]
    public void WithoutTheRecipeItemNothingCanBeCrafted()
    {
        Inventory bag = Bag((Ore, 3), (Herb, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NoRecipeItem, Bench().CraftFromItem(Standard(), bag, new CraftPurse(), 1).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void FewerRecipeItemsThanCopiesIsRefusedAndNothingIsDeducted()
    {
        Inventory bag = Bag((Blueprint, 1), (Ore, 9), (Herb, 3));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NoRecipeItem, Bench().CraftFromItem(Standard(), bag, new CraftPurse(), 2).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void MissingMaterialsLeaveTheRecipeItemAlone()
    {
        Inventory bag = Bag((Blueprint, 1), (Ore, 2), (Herb, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.MissingMaterials, Bench().CraftFromItem(Standard(), bag, new CraftPurse(), 1).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ACraftByItemDoesNotTeachTheRecipe()
    {
        var book = new RecipeBook();
        Inventory bag = Bag((Blueprint, 2), (Ore, 3), (Herb, 1));
        Assert.True(Bench().Craft(Standard(), CraftSource.RecipeItem, book, bag, new CraftPurse(), 1).Ok);
        Assert.False(book.IsLearned(RecipeCode));
    }

    [Fact]
    public void AFailedRollStillConsumesTheRecipeItems()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Rate(30).Fail(Scrap, 1).Build();
        Inventory bag = Bag((Blueprint, 2), (Ore, 2));
        CraftReport report = Bench(new ScriptedRoll(80)).CraftFromItem(recipe, bag, new CraftPurse(), 2);
        Assert.True(report.Ok);
        Assert.False(report.Success);
        Assert.Equal(0, bag.CountOf(Blueprint));
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(1, bag.CountOf(Scrap));
        Assert.Equal(0, bag.CountOf(Potion));
    }

    [Fact]
    public void TheRecipeItemSlotFreedByTheCraftCanHoldTheProduct()
    {
        // 材料页三格:设计图×1、矿×5、草×5;做粉(材料页)只用掉一部分矿(矿那格不腾空),但设计图整格用掉,腾出的格子放粉。
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Dust, 1).Build();
        Inventory bag = Bag(3, (Blueprint, 1), (Ore, 5), (Herb, 5));
        Assert.True(Bench().CraftFromItem(recipe, bag, new CraftPurse(), 1).Ok);
        Assert.Equal(1, bag.CountOf(Dust));
        Assert.Equal(0, bag.CountOf(Blueprint));
        Assert.Equal(4, bag.CountOf(Ore));

        // 同样的背包用已学配方做(设计图不消耗)就没有格子:设计图那一格还占着。
        var book = new RecipeBook();
        book.Learn(recipe.Code);
        Inventory same = Bag(3, (Blueprint, 1), (Ore, 5), (Herb, 5));
        string before = Dump(same);
        Assert.Equal(CraftResult.BagFull, Bench().CraftLearned(recipe, book, same, new CraftPurse(), 1).Result);
        Assert.Equal(before, Dump(same));
    }

    [Fact]
    public void SeveralRecipeItemsConsumedTogetherFreeTheirWholeSlotForTheProduct()
    {
        // 设计图×2 整格用掉(份数 2),腾出的格子放下粉×2;只当作用掉一件的话,设计图那格还占着,就放不下。
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Dust, 1).Build();
        Inventory bag = Bag(3, (Blueprint, 2), (Ore, 5), (Herb, 5));
        CraftReport report = Bench().CraftFromItem(recipe, bag, new CraftPurse(), 2);
        Assert.True(report.Ok);
        Assert.Equal(2, report.RecipeItemsConsumed);
        Assert.Equal(2, bag.CountOf(Dust));
        Assert.Equal(0, bag.CountOf(Blueprint));
        Assert.Equal(3, bag.CountOf(Ore));
    }

    [Fact]
    public void RefusalsAreAtomicOnThisPathToo()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(999, 1).Build();
        Inventory bag = Bag((Blueprint, 1), (Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NoProduct, Bench().CraftFromItem(recipe, bag, new CraftPurse(), 1).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ALockedGateAlsoStopsTheItemPath()
    {
        var gate = new RecordingGate { AllowCraft = false };
        Inventory bag = Bag((Blueprint, 1), (Ore, 3), (Herb, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NotAllowed, Bench(gate: gate).CraftFromItem(Standard(), bag, new CraftPurse(), 1).Result);
        Assert.Equal(before, Dump(bag));
    }
}
