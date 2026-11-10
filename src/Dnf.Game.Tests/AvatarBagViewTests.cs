namespace Dnf.Game.Tests;

/// <summary>时装背包的识别与分类视图(r14-avatar):装备页里的时装与普通装备分成两堆,时装再按部位分十类。物品全是合成的(编号 900 起)。</summary>
public class AvatarBagViewTests
{
    private static ItemInfo Gear(int code, int part, int level = 1, params string[] jobs)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level };
        item.UsableJobs.AddRange(jobs);
        return item;
    }

    private static ItemInfo Swordman(int code, int part, int level = 1)
    {
        return Gear(code, part, level, "[swordman]");
    }

    private static Inventory BagWith(params ItemInfo[] items)
    {
        var bag = new Inventory();
        foreach (ItemInfo item in items)
        {
            Assert.Equal(1, bag.Add(item));
        }
        return bag;
    }

    [Fact]
    public void AnEmptyBagGivesTenEmptyCategories()
    {
        AvatarBagView view = AvatarBagView.Build(new Inventory(), new AvatarSlots(), 0, 1);
        Assert.Empty(view.Avatars);
        Assert.Empty(view.GearIndexes);
        Assert.Equal(0, view.WearableCount);
        Assert.Equal(10, view.Categories.Count);
        for (int part = 0; part < 10; part++)
        {
            AvatarCategory category = view.Category(part);
            Assert.Same(view.Categories[part], category);
            Assert.Equal(part, category.Part);
            Assert.Equal(EquipmentParts.TagOf(part), category.Tag);
            Assert.Equal(0, category.Count);
            Assert.Null(category.Worn);
        }
        Assert.Null(view.Category(-1));
        Assert.Null(view.Category(10));
    }

    [Fact]
    public void AvatarsAndNormalGearSplitTheEquipmentPage()
    {
        // 装备页的格子次序:武器、帽子、上衣、戒指、第二件上衣、宠物、徽章类的非装备(消耗页,不在装备页)
        ItemInfo weapon = Swordman(900, EquipmentParts.Weapon);
        ItemInfo hat = Swordman(901, 0);
        ItemInfo coatA = Swordman(902, 3);
        ItemInfo ring = Swordman(903, EquipmentParts.Ring);
        ItemInfo coatB = Swordman(904, 3);
        ItemInfo pet = Swordman(905, EquipmentParts.Creature);
        var stack = new ItemInfo(906, "stackable/s906.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste };
        Inventory bag = BagWith(weapon, hat, coatA, ring, coatB, pet, stack);
        Assert.Equal(6, bag.Tab(InventoryTab.Equipment).Count);

        AvatarBagView view = AvatarBagView.Build(bag, new AvatarSlots(), 0, 99);
        Assert.Equal(new[] { 1, 2, 4 }, view.Avatars.Select(e => e.BagIndex).ToArray());
        Assert.Equal(new[] { 0, 3, 5 }, view.GearIndexes.ToArray());
        Assert.Equal(new[] { 901, 902, 904 }, view.Avatars.Select(e => e.Item.Code).ToArray());

        // 两堆不相交、合起来正好是装备页的每一格
        var all = view.Avatars.Select(e => e.BagIndex).Concat(view.GearIndexes).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, all);
    }

    [Fact]
    public void CategoriesAreTheTenPartsWithTheirOwnItemsInCellOrder()
    {
        ItemInfo coatA = Swordman(900, 3);
        ItemInfo hat = Swordman(901, 0);
        ItemInfo coatB = Swordman(902, 3);
        ItemInfo aura = Swordman(903, 9);
        Inventory bag = BagWith(coatA, hat, coatB, aura);

        AvatarBagView view = AvatarBagView.Build(bag, new AvatarSlots(), 0, 99);
        Assert.Equal(new[] { 1, 0, 0, 2, 0, 0, 0, 0, 0, 1 }, view.Categories.Select(c => c.Count).ToArray());
        Assert.Equal(new[] { 0, 2 }, view.Category(3).Entries.Select(e => e.BagIndex).ToArray());
        Assert.Equal(new[] { 900, 902 }, view.Category(3).Entries.Select(e => e.Item.Code).ToArray());
        Assert.Equal(901, view.Category(0).Entries[0].Item.Code);
        Assert.Equal(1, view.Category(0).Entries[0].BagIndex);
        Assert.Equal(903, view.Category(9).Entries[0].Item.Code);
        foreach (AvatarCategory category in view.Categories)
        {
            Assert.All(category.Entries, e => Assert.Equal(category.Part, e.Part));
        }
        Assert.Equal(4, view.Avatars.Count);
    }

    [Fact]
    public void EachEntryReportsWhetherTheHeroCouldWearItNow()
    {
        ItemInfo ok = Swordman(900, 3, 10);
        ItemInfo tooHigh = Swordman(901, 4, 40);
        ItemInfo wrongJob = Gear(902, 0, 1, "[mage]");
        ItemInfo nobody = Gear(903, 1, 1);
        Inventory bag = BagWith(ok, tooHigh, wrongJob, nobody);

        AvatarBagView view = AvatarBagView.Build(bag, new AvatarSlots(), 0, 30);
        Assert.Equal(new[] { EquipResult.Ok, EquipResult.LevelTooLow, EquipResult.WrongJob, EquipResult.WrongJob },
            view.Avatars.Select(e => e.Result).ToArray());
        Assert.Equal(new[] { true, false, false, false }, view.Avatars.Select(e => e.CanWear).ToArray());
        Assert.Equal(1, view.WearableCount);

        // 换职业与等级,同一个背包的结论跟着变
        AvatarBagView mage = AvatarBagView.Build(bag, new AvatarSlots(), 3, 60);
        Assert.Equal(new[] { EquipResult.WrongJob, EquipResult.WrongJob, EquipResult.Ok, EquipResult.WrongJob },
            mage.Avatars.Select(e => e.Result).ToArray());
        Assert.Equal(1, mage.WearableCount);
    }

    [Fact]
    public void WornItemsShowPerCategoryAndLeaveTheBagEntries()
    {
        ItemInfo coatA = Swordman(900, 3);
        ItemInfo coatB = Swordman(901, 3);
        ItemInfo hat = Swordman(902, 0);
        Inventory bag = BagWith(coatA, coatB, hat);
        var slots = new AvatarSlots();
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 0, 0, 99));

        AvatarBagView view = AvatarBagView.Build(bag, slots, 0, 99);
        Assert.Same(coatA, view.Category(3).Worn);
        Assert.Equal(new[] { 901 }, view.Category(3).Entries.Select(e => e.Item.Code).ToArray());   // 穿着的不在背包条目里
        Assert.Null(view.Category(0).Worn);
        Assert.Equal(1, view.Category(0).Count);
        Assert.Equal(2, view.Avatars.Count);
    }

    [Fact]
    public void TheViewIsASnapshotAndABagIndexFeedsWearDirectly()
    {
        ItemInfo hat = Swordman(900, 0);
        ItemInfo coat = Swordman(901, 3);
        Inventory bag = BagWith(hat, coat);
        var slots = new AvatarSlots();
        AvatarBagView before = AvatarBagView.Build(bag, slots, 0, 99);

        AvatarBagEntry entry = before.Category(3).Entries[0];
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, entry.BagIndex, 0, 99));
        Assert.Same(entry.Item, slots.Get(3));

        // 旧视图不变,新视图反映变化
        Assert.Equal(2, before.Avatars.Count);
        Assert.Null(before.Category(3).Worn);
        AvatarBagView after = AvatarBagView.Build(bag, slots, 0, 99);
        Assert.Single(after.Avatars);
        Assert.Same(coat, after.Category(3).Worn);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AvatarBagView.Build(null, new AvatarSlots(), 0, 1));
        Assert.Throws<ArgumentNullException>(() => AvatarBagView.Build(new Inventory(), null, 0, 1));
    }
}
