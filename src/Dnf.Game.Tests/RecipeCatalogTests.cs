namespace Dnf.Game.Tests;

/// <summary>
/// 配方目录:从 stackable.lst 找出配方物品、按需读出并缓存配方定义。文件全是合成的(编号 900 起)。
/// </summary>
public class RecipeCatalogTests
{
    private static Fixture BaseFixture()
    {
        return new Fixture()
            // 故意把列表写成倒序;903 不是配方(材料),904 指向不存在的文件,905 是配方但脚本不是标记流,906 是配方。
            .List(ItemCatalog.StackableListPath,
                (906, "recipe/r906.stk"), (905, "recipe/r905.stk"), (904, "recipe/missing.stk"), (903, "material/m903.stk"),
                (902, "recipe/r902.stk"), (901, "recipe/r901.stk"))
            .Tokens("stackable/recipe/r901.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0)
                .Tag("[int data]").Ints(1, 910, 3, 1, 911, 1, 0).Tag("[/int data]")
                .Tag("[success rate]").Int(30))
            .Tokens("stackable/recipe/r902.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0))
            .Tokens("stackable/material/m903.stk", w => w.Tag("[stackable type]").Str("[material]").Int(0)
                .Tag("[int data]").Ints(1, 910, 3, 1, 911, 1, 0).Tag("[/int data]"))
            .Nut("stackable/recipe/r905.stk", "not a token stream")
            .Tokens("stackable/recipe/r906.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0)
                .Tag("[int data]").Ints(0, 1, 912, 2, 0, 1).Tag("[/int data]"));
    }

    private static RecipeCatalog CatalogOf(Fixture fx)
    {
        var data = new GameData(fx.Build());
        return new RecipeCatalog(data, new ItemCatalog(data));
    }

    [Fact]
    public void CodesAreTheReadableRecipeItemsInAscendingOrder()
    {
        RecipeCatalog catalog = CatalogOf(BaseFixture());
        // 903 是材料、904 文件不存在、905 脚本读不了:都不算。
        Assert.Equal(new[] { 901, 902, 906 }, catalog.Codes());
        Assert.Same(catalog.Codes(), catalog.Codes());
    }

    [Fact]
    public void GetReadsTheItemScriptAndCachesTheResult()
    {
        RecipeCatalog catalog = CatalogOf(BaseFixture());
        RecipeDef def = catalog.Get(901);
        Assert.NotNull(def);
        Assert.Equal(901, def.Code);
        Assert.Equal("stackable/recipe/r901.stk", def.Path);
        Assert.Equal(new[] { new RecipeItem(910, 3) }, def.Inputs);
        Assert.Equal(new[] { new RecipeItem(911, 1) }, def.Outputs);
        Assert.Equal(30, def.SuccessRate);
        Assert.Same(def, catalog.Get(901));
        Assert.Equal(912, catalog.Get(906).Outputs[0].Code);
        Assert.Empty(catalog.Get(902).Inputs);
    }

    [Fact]
    public void ItemsThatAreNotReadableRecipesGiveNull()
    {
        RecipeCatalog catalog = CatalogOf(BaseFixture());
        Assert.Null(catalog.Get(903));   // 材料
        Assert.Null(catalog.Get(904));   // 文件不存在
        Assert.Null(catalog.Get(905));   // 不是标记流
        Assert.Null(catalog.Get(999));   // 不在列表里
        Assert.Null(catalog.Get(0));
        Assert.Null(catalog.Get(-5));
    }

    [Fact]
    public void WithoutAnyFilesEverythingIsEmptyOrNull()
    {
        RecipeCatalog catalog = CatalogOf(new Fixture());
        Assert.Empty(catalog.Codes());
        Assert.Null(catalog.Get(901));
    }

    [Fact]
    public void TheCatalogExposesItsItemCatalogForLookups()
    {
        var data = new GameData(BaseFixture().Build());
        var items = new ItemCatalog(data);
        var catalog = new RecipeCatalog(data, items);
        Assert.Same(items, catalog.Items);
        Assert.Equal(StackableTypes.Recipe, catalog.Items.Get(901).StackableType);
    }

    [Fact]
    public void ConstructorArgumentsMustNotBeNull()
    {
        var data = new GameData(new Fixture().Build());
        Assert.Throws<ArgumentNullException>(() => new RecipeCatalog(null, new ItemCatalog(data)));
        Assert.Throws<ArgumentNullException>(() => new RecipeCatalog(data, null));
    }
}
