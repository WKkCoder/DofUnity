using Dnf.Game;
using static Dnf.Play.Tests.CraftPlayKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 制作会话(本项目自己的设计):把配方簿、配方目录与制作规则接成一个入口,以及存档的写入与还原、金币钱包对角色进度的接线。
/// 脚本、物品与编号全是合成的:设计图 900 = 矿×3 + 草×1 → 药水×2;设计图 940 = 金币 50 + 矿×1 → 药水×1;
/// 设计图 941 = 矿×1 → 药水×1(成功率 50,失败给草×1)。矿 901、草 902、药水 910。
/// </summary>
public class CraftSessionTests
{
    // ------------------------------------------------------------------ 学与做

    [Fact]
    public void BeforeLearningNothingCanBeCraftedAfterLearningItCanBe()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Std, 1), (Ore, 3), (Herb, 1));
        string before = Dump(bag);

        Assert.Equal(CraftResult.NotLearned, session.Craft(Std, bag, new CraftPurse()).Result);
        Assert.Equal(before, Dump(bag));

        Assert.Equal(LearnResult.Ok, session.Learn(Std, bag));
        Assert.True(session.Book.IsLearned(Std));
        Assert.Equal(0, bag.CountOf(Std));

        CraftReport report = session.Craft(Std, bag, new CraftPurse());
        Assert.Equal(CraftResult.Ok, report.Result);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
        Assert.Equal(2, bag.CountOf(Potion));
    }

    [Fact]
    public void LearningNeedsTheRecipeItemAndDoesNotRepeat()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Ore, 1));
        Assert.Equal(LearnResult.NoRecipeItem, session.Learn(Std, bag));
        Assert.Equal(0, session.Book.Count);

        Inventory two = BagWith(session, (Std, 2));
        Assert.Equal(LearnResult.Ok, session.Learn(Std, two));
        Assert.Equal(LearnResult.AlreadyLearned, session.Learn(Std, two));
        Assert.Equal(1, two.CountOf(Std));
    }

    [Fact]
    public void CodesThatAreNotRecipesAreUnknown()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Ore, 5), (Herb, 5));
        string before = Dump(bag);
        Assert.Equal(LearnResult.UnknownRecipe, session.Learn(Ore, bag));
        Assert.Equal(LearnResult.UnknownRecipe, session.Learn(999, bag));
        Assert.Equal(CraftResult.UnknownRecipe, session.Craft(Ore, bag, new CraftPurse()).Result);
        Assert.Equal(CraftResult.UnknownRecipe, session.Craft(999, bag, new CraftPurse()).Result);
        Assert.Equal(before, Dump(bag));
        Assert.Equal(0, session.Book.Count);
    }

    [Fact]
    public void RollAndGateGivenToTheSessionReachTheRules()
    {
        var roll = new QueueRoll(80);
        var gate = new CountingGate();
        CraftSession session = NewSession(roll, gate);
        Inventory bag = BagWith(session, (Risky, 1), (Ore, 1));
        Assert.Equal(LearnResult.Ok, session.Learn(Risky, bag));
        CraftReport report = session.Craft(Risky, bag, new CraftPurse());
        Assert.True(report.Ok);
        Assert.False(report.Success);
        Assert.Equal(1, roll.Calls);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Potion));
        Assert.Equal(1, bag.CountOf(Herb));
        Assert.Equal(1, gate.LearnAsked);
        Assert.Single(gate.Crafted);
    }

    [Fact]
    public void APreviewThroughTheSessionReadsTheBookAndChangesNothing()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Ore, 3), (Herb, 1));
        string before = Dump(bag);
        Assert.Equal(CraftResult.NotLearned, session.Preview(Std, bag, new CraftPurse()).Result);
        session.Book.Learn(Std);
        CraftPreview preview = session.Preview(Std, bag, new CraftPurse());
        Assert.True(preview.CanCraft);
        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(before, Dump(bag));
    }

    // ------------------------------------------------------------------ 角色进度

    [Fact]
    public void TheHeroPurseReadsAndSpendsTheHeroGold()
    {
        HeroProgress hero = ExpTests.Progress();
        hero.AddGold(300);
        var purse = new HeroCraftPurse(hero);
        Assert.Equal(300, purse.Gold);
        Assert.False(purse.TrySpend(301));
        Assert.Equal(300, hero.Gold);
        Assert.True(purse.TrySpend(100));
        Assert.Equal(200, hero.Gold);
        Assert.False(purse.TrySpend(0));
        Assert.Throws<ArgumentNullException>(() => new HeroCraftPurse(null));
    }

    [Fact]
    public void CraftingThroughTheHeroUsesItsBagAndItsGold()
    {
        CraftSession session = NewSession();
        HeroProgress hero = ExpTests.Progress();
        hero.AddGold(120);
        hero.Bag.Add(session.Catalog.Items.Get(Gold50), 1);
        hero.Bag.Add(session.Catalog.Items.Get(Ore), 2);
        Assert.Equal(LearnResult.Ok, session.Learn(Gold50, hero.Bag));

        CraftReport first = session.Craft(Gold50, hero);
        Assert.True(first.Ok);
        Assert.Equal(70, hero.Gold);
        Assert.Equal(1, hero.Bag.CountOf(Ore));
        Assert.Equal(1, hero.Bag.CountOf(Potion));

        // 金币只够一次:第二次做 2 份要 100,70 不够,什么也不扣。
        hero.Bag.Add(session.Catalog.Items.Get(Ore), 1);
        string before = Dump(hero.Bag);
        Assert.Equal(CraftResult.MissingMaterials, session.Craft(Gold50, hero, 2).Result);
        Assert.Equal(70, hero.Gold);
        Assert.Equal(before, Dump(hero.Bag));
    }

    [Fact]
    public void CraftingThroughTheHeroNeedsAHero()
    {
        Assert.Throws<ArgumentNullException>(() => NewSession().Craft(Std, (HeroProgress)null));
        Assert.Throws<ArgumentNullException>(() => new CraftSession(null));
    }

    // ------------------------------------------------------------------ 背包里的配方物品

    [Fact]
    public void RecipeItemsInTheBagAreListedOnceEachInBagOrder()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Gold50, 2), (Ore, 1), (Std, 1));
        bag.RestoreStack(session.Catalog.Items.Get(Gold50), 1);   // 同一个配方物品占第二格
        IReadOnlyList<ItemInfo> items = session.RecipeItemsIn(bag);
        Assert.Equal(new[] { Gold50, Std }, items.Select(i => i.Code).ToArray());
        Assert.Empty(session.RecipeItemsIn(new Inventory()));
        Assert.Throws<ArgumentNullException>(() => session.RecipeItemsIn(null));
    }

    [Fact]
    public void ARecipeItemWithoutAReadableRecipeIsNotListed()
    {
        CraftSession session = NewSession();
        var bag = new Inventory();
        bag.Add(new ItemInfo(960, "stackable/nothing.stk", ItemKind.Stackable) { StackableType = StackableTypes.Recipe }, 1);
        Assert.Empty(session.RecipeItemsIn(bag));
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void CaptureWritesTheBookAscendingAndRestoreBringsItBackThroughText()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Risky);
        session.Book.Learn(Std);
        var save = new HeroSave();
        session.Capture(save);
        Assert.Equal(new[] { Std, Risky }, save.Recipes);

        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);
        CraftSession other = NewSession();
        CraftRestoreReport report = other.Restore(parsed);
        Assert.Equal(2, report.Recipes);
        Assert.Equal(0, report.Dropped);
        Assert.Equal(new[] { Std, Risky }, other.Book.Codes);
    }

    [Fact]
    public void CaptureReplacesWhateverRecipeLinesTheSaveAlreadyHad()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        var save = new HeroSave();
        save.Recipes.AddRange(new[] { 1, 2, 3 });
        session.Capture(save);
        Assert.Equal(new[] { Std }, save.Recipes);
        new CraftSession(session.Catalog).Capture(save);
        Assert.Empty(save.Recipes);
    }

    [Fact]
    public void RestoreReplacesTheWholeBookAndDropsUnknownAndRepeatedCodes()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Risky);   // 读档前已有的会被替换掉
        var save = new HeroSave();
        save.Recipes.AddRange(new[] { Std, 999, Ore, Std, Gold50 });   // 999 不存在、901 不是配方、900 重复
        CraftRestoreReport report = session.Restore(save);
        Assert.Equal(2, report.Recipes);
        Assert.Equal(3, report.Dropped);
        Assert.Equal(new[] { Std, Gold50 }, session.Book.Codes);
        Assert.False(session.Book.IsLearned(Risky));
    }

    [Fact]
    public void ARestoredRecipeCanBeCraftedWithoutTheRecipeItem()
    {
        CraftSession session = NewSession();
        var save = new HeroSave();
        save.Recipes.Add(Std);
        session.Restore(save);
        Inventory bag = BagWith(session, (Ore, 3), (Herb, 1));
        Assert.True(session.Craft(Std, bag, new CraftPurse()).Ok);
        Assert.Equal(2, bag.CountOf(Potion));
    }

    [Fact]
    public void CaptureAndRestoreNeedASave()
    {
        CraftSession session = NewSession();
        Assert.Throws<ArgumentNullException>(() => session.Capture(null));
        Assert.Throws<ArgumentNullException>(() => session.Restore(null));
    }
}
