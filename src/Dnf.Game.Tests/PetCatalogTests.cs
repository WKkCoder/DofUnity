namespace Dnf.Game.Tests;

/// <summary>
/// 宠物目录:从 creature.lst 找脚本、按需读出并缓存定义,全局经验表与通用配置的装载,以及宠物物品到宠物定义的对应。
/// 文件全是合成的(宠物编号 901 起、物品编号 950 起)。
/// </summary>
public class PetCatalogTests
{
    private static Fixture BaseFixture()
    {
        return new Fixture()
            // 故意把列表写成倒序,并让 902 指向一个不存在的文件。
            .List(PetCatalog.ListPath, (903, "p903.cre"), (902, "missing.cre"), (901, "p901.cre"))
            .Tokens("creature/p901.cre", w => w.Tag("[name]").Str("Pet One").Tag("[max level]").Int(5)
                .Tag("[artifact slot]").Str("red").Str("blue").Tag("[/artifact slot]"))
            .Tokens("creature/p903.cre", w => w.Tag("[max level]").Int(9))
            .Tokens("equipment/test/pet901.equ", w => w.Tag("[creature species]").Int(901))
            .Tokens("equipment/test/pet903.equ", w => w.Tag("[creature species]").Int(903))
            .Tokens("equipment/test/pet902.equ", w => w.Tag("[creature species]").Int(902))
            .Tokens("equipment/test/pet999.equ", w => w.Tag("[creature species]").Int(999))
            .Tokens("equipment/test/nospecies.equ", w => w.Tag("[creature minimum level]").Int(3))
            .Tokens("equipment/test/weaponspecies.equ", w => w.Tag("[creature species]").Int(901))
            .Tokens("equipment/test/artifact.equ", w => w.Tag("[creature minimum level]").Int(4).Tag("[creature experience amount rate]").Float(15f));
    }

    private static PetCatalog CatalogOf(Fixture fx)
    {
        return new PetCatalog(new GameData(fx.Build()));
    }

    private static ItemInfo Equip(int code, string path, int part)
    {
        return new ItemInfo(code, path, ItemKind.Equipment) { Part = part };
    }

    [Fact]
    public void IdsAreSortedAndPathsAreResolvedAgainstTheListDirectory()
    {
        PetCatalog catalog = CatalogOf(BaseFixture());
        Assert.Equal(new[] { 901, 902, 903 }, catalog.Ids);
        Assert.Equal("creature/p901.cre", catalog.PathOf(901));
        Assert.Equal("creature/missing.cre", catalog.PathOf(902));
        Assert.Null(catalog.PathOf(999));
    }

    [Fact]
    public void GetReadsTheScriptByItsListKeyAndCachesTheResult()
    {
        PetCatalog catalog = CatalogOf(BaseFixture());
        PetDef def = catalog.Get(901);
        Assert.NotNull(def);
        Assert.Equal(901, def.Id);
        Assert.Equal("creature/p901.cre", def.Path);
        Assert.Equal("Pet One", def.Name);
        Assert.Equal(5, def.MaxLevel);
        Assert.Equal(PetArtifactColors.Red | PetArtifactColors.Blue, def.ArtifactColors);
        Assert.Same(def, catalog.Get(901));
        Assert.NotNull(catalog.ReportOf(901));
        Assert.Equal(9, catalog.Get(903).MaxLevel);
        Assert.Equal(903, catalog.Get(903).Id);
    }

    [Fact]
    public void MissingFilesAndUnlistedIdsGiveNull()
    {
        PetCatalog catalog = CatalogOf(BaseFixture());
        Assert.Null(catalog.Get(902));
        Assert.Null(catalog.ReportOf(902));
        Assert.Null(catalog.Get(999));
        Assert.Null(catalog.Get(0));
        Assert.Null(catalog.ReportOf(999));
    }

    [Fact]
    public void ANonTokenScriptFileGivesNull()
    {
        PetCatalog catalog = CatalogOf(new Fixture().List(PetCatalog.ListPath, (904, "p904.cre")).Nut("creature/p904.cre", "not a token stream"));
        Assert.Equal(new[] { 904 }, catalog.Ids);
        Assert.Null(catalog.Get(904));
    }

    [Fact]
    public void WithoutAnyFilesEverythingIsEmptyOrNull()
    {
        PetCatalog catalog = CatalogOf(new Fixture());
        Assert.Empty(catalog.Ids);
        Assert.Null(catalog.Get(901));
        Assert.Null(catalog.ExpTable);
        Assert.Null(catalog.Common);
        Assert.Null(catalog.ItemData(Equip(950, "equipment/test/pet901.equ", EquipmentParts.Creature)));
        Assert.Null(catalog.DefOf(Equip(950, "equipment/test/pet901.equ", EquipmentParts.Creature)));
    }

    [Fact]
    public void TheExpTableAndCommonConfigComeFromTheirFixedPaths()
    {
        Fixture fx = BaseFixture()
            .Tokens(PetCatalog.ExpTablePath, w => w.Ints(10, 30, 60))
            .Tokens(PetCatalog.CommonPath, w => w.Tag("[MAX fill point]").Int(44));
        PetCatalog catalog = CatalogOf(fx);
        Assert.NotNull(catalog.ExpTable);
        Assert.Equal(3, catalog.ExpTable.ReadCount);
        Assert.Equal(30, catalog.ExpTable.ExpToReach(3));
        Assert.Same(catalog.ExpTable, catalog.ExpTable);
        Assert.NotNull(catalog.Common);
        Assert.Equal(44, catalog.Common.MaxFillPoint);
        Assert.Same(catalog.Common, catalog.Common);
    }

    [Fact]
    public void ItemDataIsReadFromTheItemsOwnScriptAndCachedByCode()
    {
        PetCatalog catalog = CatalogOf(BaseFixture());
        ItemInfo artifact = Equip(960, "equipment/test/artifact.equ", EquipmentParts.ArtifactRed);
        PetItemData data = catalog.ItemData(artifact);
        Assert.NotNull(data);
        Assert.Equal(4, data.MinimumLevel);
        Assert.Equal(15f, data.ExperienceRate);
        Assert.Equal(0, data.Species);
        Assert.Same(data, catalog.ItemData(artifact));
        Assert.Null(catalog.ItemData(null));
        Assert.Null(catalog.ItemData(Equip(961, "equipment/test/not-there.equ", EquipmentParts.ArtifactRed)));
        Assert.Null(catalog.ItemData(new ItemInfo(962, null, ItemKind.Equipment) { Part = EquipmentParts.ArtifactRed }));
    }

    [Fact]
    public void ADefinitionIsFoundThroughThePetItemsSpeciesField()
    {
        PetCatalog catalog = CatalogOf(BaseFixture());
        PetDef def = catalog.DefOf(Equip(950, "equipment/test/pet901.equ", EquipmentParts.Creature));
        Assert.NotNull(def);
        Assert.Equal(901, def.Id);
        Assert.Same(catalog.Get(901), def);
        Assert.Equal(903, catalog.DefOf(Equip(951, "equipment/test/pet903.equ", EquipmentParts.Creature)).Id);
    }

    [Fact]
    public void ItemsThatAreNotUsablePetItemsHaveNoDefinition()
    {
        PetCatalog catalog = CatalogOf(BaseFixture());
        Assert.Null(catalog.DefOf(null));
        // 物种指向的脚本文件不存在 / 物种不在列表里 / 没写物种。
        Assert.Null(catalog.DefOf(Equip(952, "equipment/test/pet902.equ", EquipmentParts.Creature)));
        Assert.Null(catalog.DefOf(Equip(953, "equipment/test/pet999.equ", EquipmentParts.Creature)));
        Assert.Null(catalog.DefOf(Equip(954, "equipment/test/nospecies.equ", EquipmentParts.Creature)));
        // 写了物种但部位不是宠物格:武器、神器都不算宠物物品。
        Assert.Null(catalog.DefOf(Equip(955, "equipment/test/weaponspecies.equ", EquipmentParts.Weapon)));
        Assert.Null(catalog.DefOf(Equip(956, "equipment/test/weaponspecies.equ", EquipmentParts.ArtifactRed)));
        // 可叠加物品。
        Assert.Null(catalog.DefOf(new ItemInfo(957, "equipment/test/pet901.equ", ItemKind.Stackable) { Part = EquipmentParts.Creature }));
    }

    [Fact]
    public void AnItemWhoseScriptCannotBeReadHasNoDefinitionEvenIfTheListHasAPetWithKeyZero()
    {
        // 读不到物品脚本就不知道物种,绝不能落到编号 0 上去(列表里恰好有 0 号时最容易看出来)。
        Fixture fx = BaseFixture()
            .List(PetCatalog.ListPath, (0, "p000.cre"), (901, "p901.cre"))
            .Tokens("creature/p000.cre", w => w.Tag("[max level]").Int(7));
        PetCatalog catalog = CatalogOf(fx);
        Assert.Equal(7, catalog.Get(0).MaxLevel);
        Assert.Null(catalog.DefOf(Equip(950, "equipment/test/not-there.equ", EquipmentParts.Creature)));
        Assert.Null(catalog.DefOf(new ItemInfo(951, null, ItemKind.Equipment) { Part = EquipmentParts.Creature }));
        Assert.Equal(901, catalog.DefOf(Equip(952, "equipment/test/pet901.equ", EquipmentParts.Creature)).Id);
    }

    [Fact]
    public void AnUnlistedIdReadsNoFileAtAllNotEvenOneWithAnEmptyPath()
    {
        // 编号不在列表里就根本没有路径可读;资源包里哪怕恰好有个路径为空的条目,也不能被当成这只宠物的脚本。
        Fixture fx = BaseFixture().Tokens(string.Empty, w => w.Tag("[max level]").Int(7));
        PetCatalog catalog = CatalogOf(fx);
        Assert.Null(catalog.Get(999));
        Assert.Null(catalog.ReportOf(999));
    }
}
