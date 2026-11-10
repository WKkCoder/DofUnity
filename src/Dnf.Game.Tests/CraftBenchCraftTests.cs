using static Dnf.Game.Tests.CraftKit;

namespace Dnf.Game.Tests;

/// <summary>
/// 制作执行(按已学配方,配方都是合成的):材料够才做、差一件被拒且什么都不扣、数量与金币、产物入包与满包、装备产物与装备材料、闸门。
/// 编号全是合成的(见 <see cref="CraftKit"/>):标准配方用 矿×3 + 草×1 做 药水×2。
/// </summary>
public class CraftBenchCraftTests
{
    private static RecipeDef Standard() => Recipe().In(Ore, 3).In(Herb, 1).Out(Potion, 2).Build();

    private static RecipeBook Learned(RecipeDef recipe)
    {
        var book = new RecipeBook();
        book.Learn(recipe.Code);
        return book;
    }

    private static CraftReport Craft(RecipeDef recipe, Inventory bag, ICraftPurse purse = null, int count = 1, CraftBench bench = null)
    {
        return (bench ?? Bench()).CraftLearned(recipe, Learned(recipe), bag, purse ?? new CraftPurse(), count);
    }

    private sealed class LyingPurse : ICraftPurse
    {
        public long Gold => 1000000;

        public bool TrySpend(long amount) => false;
    }

    private static int StacksOf(Inventory bag, InventoryTab tab, int code)
    {
        int n = 0;
        foreach (ItemStack stack in bag.Tab(tab))
        {
            n += stack.Info.Code == code ? 1 : 0;
        }
        return n;
    }

    // ------------------------------------------------------------------ 材料

    [Fact]
    public void ExactMaterialsSucceedAndLeaveNothingBehind()
    {
        Inventory bag = Bag((Ore, 3), (Herb, 1));
        CraftReport report = Craft(Standard(), bag);
        Assert.Equal(CraftResult.Ok, report.Result);
        Assert.True(report.Ok);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
        Assert.Equal(2, bag.CountOf(Potion));
        Assert.Equal(1, bag.StackCount);
    }

    [Fact]
    public void ExtraMaterialsAreKept()
    {
        Inventory bag = Bag((Ore, 10), (Herb, 5));
        Assert.True(Craft(Standard(), bag).Ok);
        Assert.Equal(7, bag.CountOf(Ore));
        Assert.Equal(4, bag.CountOf(Herb));
        Assert.Equal(2, bag.CountOf(Potion));
    }

    [Theory]
    [InlineData(2, 1)]   // 矿差一件
    [InlineData(3, 0)]   // 草一件也没有
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    public void OneMaterialShortIsRefusedAndNothingIsDeducted(int ore, int herb)
    {
        Inventory bag = Bag((Ore, ore), (Herb, herb));
        string before = Dump(bag);
        CraftReport report = Craft(Standard(), bag);
        Assert.Equal(CraftResult.MissingMaterials, report.Result);
        Assert.Equal(before, Dump(bag));
        Assert.Equal(0, bag.CountOf(Potion));
    }

    [Fact]
    public void MaterialsSpreadOverSeveralStacksAreAddedUp()
    {
        // 矿分在两格里(2 + 1),合起来正好三件。
        var bag = new Inventory();
        bag.Add(Item(Ore), 2);
        bag.Add(Item(Herb), 1);
        bag.RestoreStack(Item(Ore), 1);
        Assert.Equal(2, StacksOf(bag, InventoryTab.Material, Ore));
        Assert.True(Craft(Standard(), bag).Ok);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(2, bag.CountOf(Potion));
    }

    [Fact]
    public void BeadItemsAreExtraMaterialsThatAreConsumedToo()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Bead(Herb, 2).Out(Potion, 1).Build();
        Inventory shortBag = Bag((Ore, 1), (Herb, 1));
        string before = Dump(shortBag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, shortBag).Result);
        Assert.Equal(before, Dump(shortBag));

        Inventory bag = Bag((Ore, 1), (Herb, 2));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
    }

    [Fact]
    public void RepeatedInputCodesAreAddedUpBeforeCheckingAndTaking()
    {
        RecipeDef recipe = Recipe().In(Ore, 2).In(Ore, 3).Out(Potion, 1).Build();
        Inventory shortBag = Bag((Ore, 4));
        string before = Dump(shortBag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, shortBag).Result);
        Assert.Equal(before, Dump(shortBag));

        Inventory bag = Bag((Ore, 6));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(1, bag.CountOf(Ore));
    }

    [Fact]
    public void RepeatedInputCodesScaleWithTheCountToo()
    {
        // 2+3 = 5 个矿一份,做 3 份要 15 个:每一项都要乘份数,再合并。
        RecipeDef recipe = Recipe().In(Ore, 2).In(Ore, 3).Out(Potion, 1).Build();
        Inventory shortBag = Bag((Ore, 14));
        string before = Dump(shortBag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, shortBag, count: 3).Result);
        Assert.Equal(before, Dump(shortBag));

        Inventory bag = Bag((Ore, 15));
        CraftReport report = Craft(recipe, bag, count: 3);
        Assert.True(report.Ok);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(3, bag.CountOf(Potion));
        Assert.Equal(new[] { new RecipeItem(Ore, 15) }, report.Consumed);
    }

    [Fact]
    public void AZeroCountInputNeedsNothing()
    {
        RecipeDef recipe = Recipe().In(Ore, 0).In(Herb, 1).Out(Potion, 1).Build();
        Inventory bag = Bag((Herb, 1));
        CraftReport report = Craft(recipe, bag);
        Assert.True(report.Ok);
        Assert.Equal(new[] { new RecipeItem(Herb, 1) }, report.Consumed);
    }

    [Fact]
    public void PvpPointInputsAreNotSupportedSoTheRecipeCannotBeCrafted()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.PvpPointCode, 5).Out(Potion, 1).Build();
        Inventory bag = Bag((Ore, 5));
        string before = Dump(bag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void PvpPointInputsStayUnsupportedEvenIfTheBagHoldsAnItemWithThatCode()
    {
        // 物品表里恰好有个编号 2 的东西、背包里也有足够多:按编号数得够,但"编号 2"在服务端是对战点数,这里不支持,仍然拒绝。
        var two = new ItemInfo(RecipeItem.PvpPointCode, "stackable/test/s2.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material };
        var bench = new CraftBench(code => code == RecipeItem.PvpPointCode ? two : Lookup(code));
        RecipeDef recipe = Recipe().In(RecipeItem.PvpPointCode, 5).Out(Potion, 1).Build();
        Inventory bag = Bag((Ore, 5));
        Assert.Equal(5, bag.Add(two, 5));
        string before = Dump(bag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, bag, bench: bench).Result);
        Assert.Equal(before, Dump(bag));
    }

    // ------------------------------------------------------------------ 数量

    [Fact]
    public void CountMultipliesInputsAndOutputs()
    {
        Inventory bag = Bag((Ore, 9), (Herb, 3));
        CraftReport report = Craft(Standard(), bag, count: 3);
        Assert.True(report.Ok);
        Assert.Equal(3, report.Count);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
        Assert.Equal(6, bag.CountOf(Potion));
    }

    [Fact]
    public void CountShortByOneCopyIsRefusedAndNothingIsDeducted()
    {
        Inventory bag = Bag((Ore, 9), (Herb, 2));
        string before = Dump(bag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(Standard(), bag, count: 3).Result);
        Assert.Equal(before, Dump(bag));
        Inventory bag2 = Bag((Ore, 8), (Herb, 3));
        string before2 = Dump(bag2);
        Assert.Equal(CraftResult.MissingMaterials, Craft(Standard(), bag2, count: 3).Result);
        Assert.Equal(before2, Dump(bag2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ACountOutsideOneToSixtyFiveThousandIsRefused(int count)
    {
        Inventory bag = Bag((Ore, 50), (Herb, 50));
        string before = Dump(bag);
        Assert.Equal(CraftResult.InvalidCount, Craft(Standard(), bag, count: count).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void TheUpperLimitCountIsStillACountAndOnlyRunsOutOfMaterials()
    {
        Assert.Equal(65535, CraftBench.MaxCount);
        Inventory bag = Bag((Ore, 50), (Herb, 50));
        Assert.Equal(CraftResult.MissingMaterials, Craft(Standard(), bag, count: CraftBench.MaxCount).Result);
    }

    [Fact]
    public void ScalingThatOverflowsAnIntegerIsRefused()
    {
        RecipeDef huge = Recipe().In(Ore, 40000).Out(Potion, 1).Build();
        Inventory bag = Bag((Ore, 50));
        Assert.Equal(CraftResult.InvalidCount, Craft(huge, bag, count: CraftBench.MaxCount).Result);
        Assert.Equal(50, bag.CountOf(Ore));
    }

    [Fact]
    public void AnAmountExactlyAtTheIntegerMaximumIsNotAnOverflow()
    {
        // 恰好 int.MaxValue 不算溢出:输入只是材料不够,产物只是放不下;多一份才是数量不合法。
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        RecipeDef needsMax = Recipe().In(Ore, int.MaxValue).Out(Potion, 1).Build();
        Assert.Equal(CraftResult.MissingMaterials, Craft(needsMax, bag).Result);
        RecipeDef givesMax = Recipe().In(Ore, 1).Out(Potion, int.MaxValue).Build();
        Assert.Equal(CraftResult.BagFull, Craft(givesMax, bag).Result);
        Assert.Equal(before, Dump(bag));

        Assert.Equal(CraftResult.InvalidCount, Craft(needsMax, bag, count: 2).Result);
        Assert.Equal(CraftResult.InvalidCount, Craft(givesMax, bag, count: 2).Result);
        Assert.Equal(before, Dump(bag));
    }

    // ------------------------------------------------------------------ 金币

    [Fact]
    public void GoldInputIsChargedFromThePurse()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.GoldCode, 500).In(Ore, 1).Out(Potion, 1).Build();
        var purse = new CraftPurse(1200);
        Inventory bag = Bag((Ore, 1));
        CraftReport report = Craft(recipe, bag, purse);
        Assert.True(report.Ok);
        Assert.Equal(700, purse.Gold);
        Assert.Equal(500, report.GoldSpent);
        Assert.Equal(1, bag.CountOf(Potion));
    }

    [Fact]
    public void ShortGoldIsRefusedAndNothingIsSpentOrTaken()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.GoldCode, 500).In(Ore, 1).Out(Potion, 1).Build();
        var purse = new CraftPurse(499);
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, bag, purse).Result);
        Assert.Equal(499, purse.Gold);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void GoldIsMultipliedByTheCountToo()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.GoldCode, 500).In(Ore, 1).Out(Potion, 1).Build();
        var tooLittle = new CraftPurse(1499);
        Inventory bag = Bag((Ore, 3));
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, bag, tooLittle, count: 3).Result);
        Assert.Equal(1499, tooLittle.Gold);
        var enough = new CraftPurse(1500);
        Assert.True(Craft(recipe, bag, enough, count: 3).Ok);
        Assert.Equal(0, enough.Gold);
        Assert.Equal(3, bag.CountOf(Potion));
    }

    [Fact]
    public void APurseThatRefusesAfterAllLeavesTheBagAlone()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.GoldCode, 500).In(Ore, 1).Out(Potion, 1).Build();
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.MissingMaterials, Craft(recipe, bag, new LyingPurse()).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void WithoutAPurseGoldRecipesCannotBeCraftedButOthersCan()
    {
        RecipeDef gold = Recipe().In(RecipeItem.GoldCode, 5).In(Ore, 1).Out(Potion, 1).Build();
        Inventory bag = Bag((Ore, 1));
        Assert.Equal(CraftResult.MissingMaterials, Bench().CraftLearned(gold, Learned(gold), bag, null, 1).Result);
        Assert.Equal(1, bag.CountOf(Ore));
        RecipeDef plain = Recipe().In(Ore, 1).Out(Potion, 1).Build();
        Assert.True(Bench().CraftLearned(plain, Learned(plain), bag, null, 1).Ok);
    }

    // ------------------------------------------------------------------ 产物入包与满包

    [Fact]
    public void ProductsMergeIntoAnExistingStack()
    {
        Inventory bag = Bag((Ore, 3), (Herb, 1), (Potion, 5));
        Assert.True(Craft(Standard(), bag).Ok);
        Assert.Equal(7, bag.CountOf(Potion));
        Assert.Equal(1, StacksOf(bag, InventoryTab.Consumable, Potion));
    }

    [Fact]
    public void ProductsSplitAcrossSlotsAtTheStackLimit()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Crystal, 7).Build();
        Inventory bag = Bag((Ore, 1));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(7, bag.CountOf(Crystal));
        Assert.Equal(2, StacksOf(bag, InventoryTab.Material, Crystal));
    }

    [Fact]
    public void AFullTabRefusesAProductThatHasNoRoomAndNothingIsDeducted()
    {
        // 消耗品页三格被药水、药剂、补剂占满,以太没有格子可放。
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Ether, 1).Build();
        Inventory bag = Bag(3, (Potion, 5), (Elixir, 5), (Tonic, 5), (Ore, 1));
        string before = Dump(bag);
        CraftReport report = Craft(recipe, bag);
        Assert.Equal(CraftResult.BagFull, report.Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void AFullTabStillTakesAProductThatMergesIntoAnExistingStack()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Build();
        Inventory bag = Bag(3, (Potion, 5), (Elixir, 5), (Tonic, 5), (Ore, 1));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(6, bag.CountOf(Potion));
    }

    [Fact]
    public void AStackAtItsLimitHasNoRoomEvenIfItsTabHasAStack()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 1).Build();
        Inventory bag = Bag(3, (Potion, 100), (Elixir, 5), (Tonic, 5), (Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.BagFull, Craft(recipe, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ConsumingAWholeStackFreesItsSlotForTheProduct()
    {
        // 材料页三格全满(矿、草、水晶);做粉要把矿整格用掉,腾出的格子放粉。
        RecipeDef recipe = Recipe().In(Ore, 5).Out(Dust, 1).Build();
        Inventory bag = Bag(3, (Ore, 5), (Herb, 5), (Crystal, 5));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(1, bag.CountOf(Dust));
        Assert.Equal(0, bag.CountOf(Ore));
    }

    [Fact]
    public void ConsumingPartOfAStackFreesNoSlotSoTheProductHasNoRoom()
    {
        RecipeDef recipe = Recipe().In(Ore, 3).Out(Dust, 1).Build();
        Inventory bag = Bag(3, (Ore, 5), (Herb, 5), (Crystal, 5));
        string before = Dump(bag);
        Assert.Equal(CraftResult.BagFull, Craft(recipe, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    // ------------------------------------------------------------------ 装备

    [Fact]
    public void AnEquipmentProductLandsInTheEquipmentTab()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Blade, 1).Build();
        Inventory bag = Bag((Ore, 1));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(1, bag.CountOf(Blade));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
    }

    [Fact]
    public void MoreThanOneEquipmentIsRefusedEvenWithRoomToSpare()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Blade, 1).Build();
        Inventory bag = Bag((Ore, 5));
        string before = Dump(bag);
        Assert.Equal(CraftResult.TooManyEquipment, Craft(recipe, bag, count: 2).Result);
        Assert.Equal(before, Dump(bag));
        RecipeDef two = Recipe().In(Ore, 1).Out(Blade, 2).Build();
        Assert.Equal(CraftResult.TooManyEquipment, Craft(two, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void TwoSeparateEntriesOfTheSameEquipmentAreEachCheckedOnTheirOwn()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Blade, 1).Out(Blade, 1).Build();
        Inventory bag = Bag((Ore, 1));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(2, bag.CountOf(Blade));
        Assert.Equal(2, StacksOf(bag, InventoryTab.Equipment, Blade));
    }

    [Fact]
    public void EquipmentMaterialsAreTakenByCode()
    {
        RecipeDef recipe = Recipe().In(Axe, 1).Out(Potion, 1).Build();
        Inventory bag = Bag((Axe, 2));
        Assert.True(Craft(recipe, bag).Ok);
        Assert.Equal(1, bag.CountOf(Axe));
    }

    [Fact]
    public void EquipmentProductsNeedAFreeSlotUnlessAnEquipmentMaterialFreesOne()
    {
        Inventory full = Bag(2, (Axe, 2), (Ore, 1));
        string before = Dump(full);
        RecipeDef byOre = Recipe().In(Ore, 1).Out(Blade, 1).Build();
        Assert.Equal(CraftResult.BagFull, Craft(byOre, full).Result);
        Assert.Equal(before, Dump(full));

        RecipeDef byAxe = Recipe().In(Axe, 1).Out(Blade, 1).Build();
        Assert.True(Craft(byAxe, full).Ok);
        Assert.Equal(1, full.CountOf(Axe));
        Assert.Equal(1, full.CountOf(Blade));
    }

    // ------------------------------------------------------------------ 产物与配方本身有问题

    [Fact]
    public void AnUnknownProductCodeIsRefusedAndNothingIsDeducted()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(999, 1).Build();
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NoProduct, Craft(recipe, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void GoldCannotBeAProduct()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(RecipeItem.GoldCode, 100).Build();
        Inventory bag = Bag((Ore, 1));
        Assert.Equal(CraftResult.NoProduct, Craft(recipe, bag).Result);
        Assert.Equal(1, bag.CountOf(Ore));
    }

    [Fact]
    public void AZeroCountOutputGivesNothingAndIsNotListedAsGranted()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Out(Potion, 0).Out(Elixir, 2).Build();
        Inventory bag = Bag((Ore, 1));
        CraftReport report = Craft(recipe, bag);
        Assert.True(report.Ok);
        Assert.Equal(new[] { new RecipeItem(Elixir, 2) }, report.Granted);
        Assert.Equal(0, bag.CountOf(Potion));
        Assert.Equal(2, bag.CountOf(Elixir));
    }

    [Fact]
    public void GoldStaysRefusedAsAProductEvenIfTheItemTableHappensToHaveACodeZeroItem()
    {
        var zero = new ItemInfo(0, "stackable/test/s0.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material };
        var bench = new CraftBench(code => code == 0 ? zero : Lookup(code));
        RecipeDef recipe = Recipe().In(Ore, 1).Out(RecipeItem.GoldCode, 100).Build();
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NoProduct, Craft(recipe, bag, bench: bench).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ARecipeWithoutAnyProductIsRefusedAndNothingIsDeducted()
    {
        RecipeDef recipe = Recipe().In(Ore, 1).Build();
        Inventory bag = Bag((Ore, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NoProduct, Craft(recipe, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ATimeWornMaterialMeansTheAvatarPathWhichIsNotDoneHere()
    {
        RecipeDef recipe = Recipe().In(Hat, 1).Out(Potion, 1).Build();
        Inventory bag = Bag((Hat, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.AvatarInput, Craft(recipe, bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void NullMalformedAndNegativeCountRecipesAreUnknown()
    {
        Inventory bag = Bag((Ore, 5));
        string before = Dump(bag);
        Assert.Equal(CraftResult.UnknownRecipe, Bench().CraftLearned(null, new RecipeBook(), bag, null, 1).Result);
        Assert.Equal(CraftResult.UnknownRecipe, Craft(Recipe().In(Ore, 1).Out(Potion, 1).Malformed().Build(), bag).Result);
        Assert.Equal(CraftResult.UnknownRecipe, Craft(Recipe().In(Ore, -1).Out(Potion, 1).Build(), bag).Result);
        Assert.Equal(CraftResult.UnknownRecipe, Craft(Recipe().In(Ore, 1).Out(Potion, -1).Build(), bag).Result);
        Assert.Equal(CraftResult.UnknownRecipe, Craft(Recipe().In(Ore, 1).Out(Potion, 1).Rate(50).Fail(Scrap, -2).Build(), bag).Result);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void CraftingNeedsABag()
    {
        Assert.Throws<ArgumentNullException>(() => Bench().CraftLearned(Standard(), Learned(Standard()), null, new CraftPurse(), 1));
    }

    // ------------------------------------------------------------------ 闸门与报告

    [Fact]
    public void ALockedCraftGateRefusesAndChangesNothing()
    {
        var gate = new RecordingGate { AllowCraft = false };
        Inventory bag = Bag((Ore, 3), (Herb, 1));
        string before = Dump(bag);
        CraftReport report = Craft(Standard(), bag, bench: Bench(gate: gate));
        Assert.Equal(CraftResult.NotAllowed, report.Result);
        Assert.Equal(before, Dump(bag));
        Assert.Equal(1, gate.CraftAsked);
        Assert.Empty(gate.Crafted);
    }

    [Fact]
    public void TheGateIsToldOncePerFinishedCraftWithTheSameReport()
    {
        var gate = new RecordingGate();
        Inventory bag = Bag((Ore, 6), (Herb, 2));
        RecipeDef recipe = Standard();
        CraftReport first = Craft(recipe, bag, bench: Bench(gate: gate));
        Assert.True(first.Ok);
        Assert.Single(gate.Crafted);
        Assert.Same(recipe, gate.Crafted[0].Recipe);
        Assert.Same(first, gate.Crafted[0].Report);
        // 被拒的不通知。
        Craft(Standard(), Bag(), bench: Bench(gate: gate));
        Assert.Single(gate.Crafted);
    }

    [Fact]
    public void TheGateIsNotAskedWhenTheRecipeIsNotEvenLearned()
    {
        var gate = new RecordingGate();
        Inventory bag = Bag((Ore, 3), (Herb, 1));
        Assert.Equal(CraftResult.NotLearned, Bench(gate: gate).CraftLearned(Standard(), new RecipeBook(), bag, new CraftPurse(), 1).Result);
        Assert.Equal(0, gate.CraftAsked);
    }

    [Fact]
    public void TheReportListsWhatWasConsumedAndGranted()
    {
        RecipeDef recipe = Recipe().In(RecipeItem.GoldCode, 50).In(Ore, 2).In(Herb, 1).Bead(Dust, 1).Out(Potion, 3).Out(Elixir, 1).Build();
        Inventory bag = Bag((Ore, 4), (Herb, 1), (Dust, 1));
        var purse = new CraftPurse(80);
        CraftReport report = Craft(recipe, bag, purse);
        Assert.True(report.Ok);
        Assert.True(report.Success);
        Assert.Equal(1, report.Count);
        Assert.Equal(-1, report.Roll);
        Assert.Equal(50, report.GoldSpent);
        Assert.Equal(0, report.RecipeItemsConsumed);
        Assert.Equal(new[] { new RecipeItem(Ore, 2), new RecipeItem(Herb, 1), new RecipeItem(Dust, 1) }, report.Consumed);
        Assert.Equal(new[] { new RecipeItem(Potion, 3), new RecipeItem(Elixir, 1) }, report.Granted);
        Assert.Equal(30, purse.Gold);
    }

    [Fact]
    public void ARefusedCraftReportsNothingElse()
    {
        CraftReport report = Craft(Standard(), Bag((Ore, 1)));
        Assert.Equal(CraftResult.MissingMaterials, report.Result);
        Assert.False(report.Ok);
        Assert.Empty(report.Consumed);
        Assert.Empty(report.Granted);
        Assert.Equal(0, report.GoldSpent);
        Assert.Equal(-1, report.Roll);
    }
}
