using static Dnf.Game.Tests.CraftKit;

namespace Dnf.Game.Tests;

/// <summary>制作预览:不动背包、不掷骰、不通知闸门;材料行、产物、按材料最多做几份,并且与真正制作的结果一致。编号全是合成的。</summary>
public class CraftBenchPreviewTests
{
    private static RecipeDef Standard() => Recipe().In(Ore, 3).In(Herb, 1).Out(Potion, 2).Build();

    private static RecipeBook Learned(RecipeDef recipe)
    {
        var book = new RecipeBook();
        book.Learn(recipe.Code);
        return book;
    }

    private static CraftPreview Preview(RecipeDef recipe, Inventory bag, ICraftPurse purse = null, int count = 1)
    {
        return Bench().Preview(recipe, CraftSource.Learned, Learned(recipe), bag, purse ?? new CraftPurse(), count);
    }

    [Fact]
    public void EveryMaterialIsListedWithWhatIsNeededAndWhatIsOwned()
    {
        CraftPreview preview = Preview(Standard(), Bag((Ore, 5)));
        Assert.Equal(CraftResult.MissingMaterials, preview.Result);
        Assert.False(preview.CanCraft);
        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(Ore, preview.Lines[0].Code);
        Assert.Equal(3, preview.Lines[0].Need);
        Assert.Equal(5, preview.Lines[0].Have);
        Assert.True(preview.Lines[0].Enough);
        Assert.Equal(Herb, preview.Lines[1].Code);
        Assert.Equal(1, preview.Lines[1].Need);
        Assert.Equal(0, preview.Lines[1].Have);
        Assert.False(preview.Lines[1].Enough);
        Assert.Equal(new[] { new RecipeItem(Potion, 2) }, preview.Outputs);
    }

    [Fact]
    public void WhenEverythingIsThereThePreviewSaysCanCraft()
    {
        CraftPreview preview = Preview(Standard(), Bag((Ore, 3), (Herb, 1)));
        Assert.True(preview.CanCraft);
        Assert.Equal(CraftResult.Ok, preview.Result);
    }

    [Fact]
    public void TheGoldLineComesFirstAndReadsThePurse()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).In(RecipeItem.GoldCode, 500).Out(Potion, 1).Build();
        CraftPreview preview = Preview(recipe, Bag((Ore, 1)), new CraftPurse(120));
        Assert.Equal(2, preview.Lines.Count);
        Assert.True(preview.Lines[0].IsGold);
        Assert.Equal(500, preview.Lines[0].Need);
        Assert.Equal(120, preview.Lines[0].Have);
        Assert.False(preview.Lines[0].Enough);
        Assert.False(preview.Lines[1].IsGold);
        Assert.Equal(CraftResult.MissingMaterials, preview.Result);
    }

    [Fact]
    public void ACountScalesTheLinesAndTheOutputs()
    {
        CraftPreview preview = Preview(Standard(), Bag((Ore, 12), (Herb, 4)), count: 4);
        Assert.Equal(12, preview.Lines[0].Need);
        Assert.Equal(4, preview.Lines[1].Need);
        Assert.Equal(new[] { new RecipeItem(Potion, 8) }, preview.Outputs);
        Assert.True(preview.CanCraft);
    }

    [Fact]
    public void PreviewingChangesNothingAndNeverRollsOrNotifiesTheGate()
    {
        var gate = new RecordingGate();
        var roll = new ScriptedRoll();
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Rate(50).Fail(Scrap, 1).Build();
        Inventory bag = Bag((Ore, 3));
        string before = Dump(bag);
        var purse = new CraftPurse(70);
        CraftPreview preview = Bench(roll, gate).Preview(recipe, CraftSource.Learned, Learned(recipe), bag, purse, 2);
        Assert.True(preview.CanCraft);
        Assert.Equal(before, Dump(bag));
        Assert.Equal(70, purse.Gold);
        Assert.Equal(0, roll.Calls);
        Assert.Empty(gate.Crafted);
    }

    [Fact]
    public void AnUnlearnedRecipeIsNotLearnedButItsMaterialsAreStillListed()
    {
        CraftPreview preview = Bench().Preview(Standard(), CraftSource.Learned, new RecipeBook(), Bag((Ore, 3), (Herb, 1)), new CraftPurse(), 1);
        Assert.Equal(CraftResult.NotLearned, preview.Result);
        Assert.Equal(2, preview.Lines.Count);
        Assert.True(preview.Lines[0].Enough);
    }

    [Fact]
    public void AMalformedOrBadCountPreviewHasNoLines()
    {
        Assert.Equal(CraftResult.UnknownRecipe, Bench().Preview(null, CraftSource.Learned, new RecipeBook(), Bag(), null, 1).Result);
        CraftPreview bad = Preview(Standard(), Bag((Ore, 9), (Herb, 9)), count: 0);
        Assert.Equal(CraftResult.InvalidCount, bad.Result);
        Assert.Empty(bad.Lines);
    }

    [Fact]
    public void FromARecipeItemTheItemCountLimitsHowManyCanBeMade()
    {
        CraftPreview preview = Bench().Preview(Standard(), CraftSource.RecipeItem, null, Bag((Blueprint, 2), (Ore, 30), (Herb, 30)), new CraftPurse(), 1);
        Assert.True(preview.CanCraft);
        Assert.Equal(2, preview.MaxByMaterials);
        CraftPreview none = Bench().Preview(Standard(), CraftSource.RecipeItem, null, Bag((Ore, 30), (Herb, 30)), new CraftPurse(), 1);
        Assert.Equal(CraftResult.NoRecipeItem, none.Result);
        Assert.Equal(0, none.MaxByMaterials);
    }

    [Theory]
    [InlineData(10, 2, 2)]   // 草是瓶颈
    [InlineData(9, 9, 3)]    // 矿是瓶颈
    [InlineData(2, 9, 0)]    // 矿不够做一份
    [InlineData(0, 0, 0)]
    public void MaxByMaterialsIsTheTightestMaterial(int ore, int herb, int expected)
    {
        Assert.Equal(expected, Preview(Standard(), Bag((Ore, ore), (Herb, herb))).MaxByMaterials);
    }

    [Fact]
    public void MaxByMaterialsCountsGoldAndIsCappedAtTheCountLimit()
    {
        RecipeDef gold = Recipe().In(RecipeItem.GoldCode, 50).In(Ore, 1).Out(Potion, 1).Build();
        Assert.Equal(2, Preview(gold, Bag((Ore, 9)), new CraftPurse(120)).MaxByMaterials);
        RecipeDef free = Recipe().Out(Potion, 1).Build();
        Assert.Equal(CraftBench.MaxCount, Preview(free, Bag()).MaxByMaterials);
    }

    [Fact]
    public void PreviewAndCraftAgreeOnTheResultInEveryScenario()
    {
        var scenarios = new List<(RecipeDef Recipe, Func<Inventory> Bag, long Gold, int Count)>
        {
            (Standard(), () => Bag((Ore, 3), (Herb, 1)), 0, 1),
            (Standard(), () => Bag((Ore, 2), (Herb, 1)), 0, 1),
            (Standard(), () => Bag((Ore, 9), (Herb, 3)), 0, 3),
            (Standard(), () => Bag((Ore, 9), (Herb, 3)), 0, 4),
            (Standard(), () => Bag((Ore, 9), (Herb, 3)), 0, 0),
            (Recipe().In(Ore, 1).Out(999, 1).Build(), () => Bag((Ore, 1)), 0, 1),
            (Recipe().In(Ore, 1).Build(), () => Bag((Ore, 1)), 0, 1),
            (Recipe().In(Hat, 1).Out(Potion, 1).Build(), () => Bag((Hat, 1)), 0, 1),
            (Recipe().In(Ore, 1).Out(Blade, 1).Build(), () => Bag((Ore, 5)), 0, 2),
            (Recipe().In(Ore, 1).Out(Ether, 1).Build(), () => Bag(3, (Potion, 5), (Elixir, 5), (Tonic, 5), (Ore, 1)), 0, 1),
            (Recipe().In(Ore, 5).Out(Dust, 1).Build(), () => Bag(3, (Ore, 5), (Herb, 5), (Crystal, 5)), 0, 1),
            (Recipe().In(Ore, 3).Out(Dust, 1).Build(), () => Bag(3, (Ore, 5), (Herb, 5), (Crystal, 5)), 0, 1),
            (Recipe().In(RecipeItem.GoldCode, 100).In(Ore, 1).Out(Potion, 1).Build(), () => Bag((Ore, 1)), 99, 1),
            (Recipe().In(RecipeItem.GoldCode, 100).In(Ore, 1).Out(Potion, 1).Build(), () => Bag((Ore, 1)), 100, 1),
            (Recipe().In(Ore, 1).Out(Potion, 1).Malformed().Build(), () => Bag((Ore, 1)), 0, 1),
        };
        foreach ((RecipeDef recipe, Func<Inventory> makeBag, long gold, int count) in scenarios)
        {
            RecipeBook book = Learned(recipe);
            CraftPreview preview = Bench().Preview(recipe, CraftSource.Learned, book, makeBag(), new CraftPurse(gold), count);
            CraftReport report = Bench().CraftLearned(recipe, book, makeBag(), new CraftPurse(gold), count);
            Assert.Equal(report.Result, preview.Result);
        }
    }
}
