namespace Dnf.Game.Tests;

/// <summary>
/// 装备栏与穿脱规则(证据 E-118):职业标志位的写法、槽/职业/等级的检查次序、穿上与脱下在背包与装备栏之间搬东西。数据全是合成的。
/// </summary>
public class EquipTests
{
    private static ItemInfo Gear(int code, int part = EquipmentParts.Weapon, int level = 1, params string[] jobs)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level };
        item.UsableJobs.AddRange(jobs);
        return item;
    }

    private static ItemInfo Swordman(int code, int part = EquipmentParts.Weapon, int level = 1)
    {
        return Gear(code, part, level, "[swordman]");
    }

    private static ItemInfo Stack(int code)
    {
        return new ItemInfo(code, "stackable/s" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Material };
    }

    // ------------------------------------------------------------------ 职业标志位

    [Fact]
    public void TheElevenJobNamesRoundTripAndUnknownNamesMeanEveryJob()
    {
        for (int job = 0; job < EquipRules.JobCount; job++)
        {
            Assert.Equal(job, EquipRules.JobOf(EquipRules.JobTag(job)));
        }
        Assert.Null(EquipRules.JobTag(EquipRules.JobCount));
        Assert.Null(EquipRules.JobTag(-1));
        Assert.Equal("[swordman]", EquipRules.JobTag(EquipRules.SwordmanJob));
        Assert.Equal(EquipRules.AllJobs, EquipRules.JobOf("[all]"));
        Assert.Equal(EquipRules.AllJobs, EquipRules.JobOf(null));
        Assert.Equal(EquipRules.SwordmanJob, EquipRules.JobOf("[SwordMan]"));
    }

    [Fact]
    public void AnItemWithoutAUsableJobListFitsNobody()
    {
        ItemInfo item = Gear(1);
        for (int job = 0; job < EquipRules.JobCount; job++)
        {
            Assert.False(EquipRules.UsableBy(item, job));
        }
        Assert.False(EquipRules.UsableBy(null, 0));
    }

    [Fact]
    public void AJobListOnlyFitsTheJobsItNames()
    {
        ItemInfo item = Gear(1, EquipmentParts.Weapon, 1, "[swordman]", "[fighter]");
        Assert.True(EquipRules.UsableBy(item, 0));
        Assert.True(EquipRules.UsableBy(item, 1));
        Assert.False(EquipRules.UsableBy(item, 2));
        Assert.False(EquipRules.UsableBy(item, EquipRules.JobCount - 1));
    }

    [Fact]
    public void AnUnknownNameInTheListSetsEveryJobButNotOutOfRangeOnes()
    {
        ItemInfo item = Gear(1, EquipmentParts.Weapon, 1, "[all]");
        for (int job = 0; job < EquipRules.JobCount; job++)
        {
            Assert.True(EquipRules.UsableBy(item, job));
        }
        Assert.False(EquipRules.UsableBy(item, EquipRules.JobCount));
        Assert.False(EquipRules.UsableBy(item, -1));
    }

    // ------------------------------------------------------------------ 检查次序

    [Fact]
    public void CheckGoesThroughItsReasonsInTheServersOrder()
    {
        Assert.Equal(EquipResult.NoItem, EquipRules.Check(null, 10, 0, 1));
        Assert.Equal(EquipResult.NotEquipment, EquipRules.Check(Stack(1), 10, 0, 1));
        Assert.Equal(EquipResult.NotWearable, EquipRules.Check(Swordman(1), 9, 0, 1));                    // 槽不在 10 到 21
        Assert.Equal(EquipResult.WrongSlot, EquipRules.Check(Swordman(1, EquipmentParts.Coat), 10, 0, 1));
        Assert.Equal(EquipResult.WrongJob, EquipRules.Check(Gear(1, EquipmentParts.Weapon, 1, "[mage]"), 10, 0, 1));
        Assert.Equal(EquipResult.LevelTooLow, EquipRules.Check(Swordman(1, EquipmentParts.Weapon, 5), 10, 0, 4));
        Assert.Equal(EquipResult.Ok, EquipRules.Check(Swordman(1), 10, 0, 1));

        // 同时不满足几条时报次序靠前的:类型不对压过职业与等级,职业压过等级
        Assert.Equal(EquipResult.WrongSlot, EquipRules.Check(Gear(1, EquipmentParts.Coat, 99, "[mage]"), 10, 0, 1));
        Assert.Equal(EquipResult.WrongJob, EquipRules.Check(Gear(1, EquipmentParts.Weapon, 99, "[mage]"), 10, 0, 1));
    }

    [Fact]
    public void TheLevelLimitIsInclusive()
    {
        ItemInfo item = Swordman(1, EquipmentParts.Weapon, 20);
        Assert.Equal(EquipResult.LevelTooLow, EquipRules.Check(item, 0, 19));
        Assert.Equal(EquipResult.Ok, EquipRules.Check(item, 0, 20));
        Assert.Equal(EquipResult.Ok, EquipRules.Check(item, 0, 85));
    }

    [Fact]
    public void ThePartlessOverloadUsesTheItemsOwnPartAndAvatarOrCreatureItemsHaveNoSlot()
    {
        Assert.Equal(EquipResult.Ok, EquipRules.Check(Swordman(1, EquipmentParts.Coat), 0, 1));
        Assert.Equal(EquipResult.NotWearable, EquipRules.Check(Swordman(1, EquipmentParts.HatAvatar), 0, 1));
        Assert.Equal(EquipResult.NotWearable, EquipRules.Check(Swordman(1, EquipmentParts.Creature), 0, 1));
        Assert.Equal(EquipResult.NoItem, EquipRules.Check(null, 0, 1));
    }

    // ------------------------------------------------------------------ 装备栏

    [Fact]
    public void TheSlotsAreTheTwelveEnchantableParts()
    {
        Assert.Equal(12, EquipmentSlots.Count);
        Assert.False(EquipmentSlots.IsSlot(9));
        Assert.True(EquipmentSlots.IsSlot(10));
        Assert.True(EquipmentSlots.IsSlot(21));
        Assert.False(EquipmentSlots.IsSlot(22));
        var slots = new EquipmentSlots();
        Assert.Null(slots.Get(9));
        Assert.Null(slots.Get(10));
        Assert.Null(slots.Get(22));
        Assert.Equal((0, 0), (slots.EquippedCount, slots.Version));
        Assert.Empty(slots.Items);
    }

    // ------------------------------------------------------------------ 穿

    [Fact]
    public void EquippingIntoAnEmptySlotTakesTheItemOutOfTheBag()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo a = Swordman(1), b = Swordman(2, EquipmentParts.Coat), c = Swordman(3, EquipmentParts.Pants);
        bag.Add(a);
        bag.Add(b);
        bag.Add(c);
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 1, 0, 1));
        Assert.Same(b, slots.Get(EquipmentParts.Coat));
        Assert.Equal(new[] { 1, 3 }, bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));          // 后面的往前补
        Assert.Equal((1, 1), (slots.EquippedCount, slots.Version));
    }

    [Fact]
    public void EquippingOverAWornItemSwapsItBackIntoTheSameBagCell()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo first = Swordman(1), other = Swordman(2, EquipmentParts.Coat), second = Swordman(3);
        bag.Add(first);
        bag.Add(other);
        bag.Add(second);
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, 0, 1));                       // 武器 1 号穿上,格子补位
        Assert.Equal(new[] { 2, 3 }, bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 1, 0, 1));                       // 武器 3 号换上:1 号回到 3 号原来的格
        Assert.Same(second, slots.Get(EquipmentParts.Weapon));
        Assert.Equal(new[] { 2, 1 }, bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        Assert.Equal((1, 2), (slots.EquippedCount, slots.Version));
    }

    [Fact]
    public void ARefusedEquipChangesNothing()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        bag.Add(Gear(1, EquipmentParts.Weapon, 1, "[mage]"));
        bag.Add(Swordman(2, EquipmentParts.Weapon, 30));
        bag.Add(Swordman(3, EquipmentParts.HatAvatar));
        Assert.Equal(EquipResult.WrongJob, EquipActions.Equip(bag, slots, 0, 0, 10));
        Assert.Equal(EquipResult.LevelTooLow, EquipActions.Equip(bag, slots, 1, 0, 10));
        Assert.Equal(EquipResult.NotWearable, EquipActions.Equip(bag, slots, 2, 0, 10));
        Assert.Equal(EquipResult.NoItem, EquipActions.Equip(bag, slots, 3, 0, 10));
        Assert.Equal(EquipResult.NoItem, EquipActions.Equip(bag, slots, -1, 0, 10));
        Assert.Equal(3, bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal((0, 0), (slots.EquippedCount, slots.Version));
    }

    [Fact]
    public void TheShowCheckComesAfterTheRulesAndCanRefuse()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo ok = Swordman(1), wrongJob = Gear(2, EquipmentParts.Weapon, 1, "[mage]");
        bag.Add(ok);
        bag.Add(wrongJob);
        int asked = 0;
        Func<ItemInfo, bool> no = item => { asked++; return false; };
        Assert.Equal(EquipResult.WrongJob, EquipActions.Equip(bag, slots, 1, 0, 1, no));
        Assert.Equal(0, asked);                                                                         // 规则不过就不问
        Assert.Equal(EquipResult.NotShowable, EquipActions.Equip(bag, slots, 0, 0, 1, no));
        Assert.Equal(1, asked);
        Assert.Equal((2, 0, 0), (bag.Tab(InventoryTab.Equipment).Count, slots.EquippedCount, slots.Version));
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, 0, 1, item => true));
        Assert.Same(ok, slots.Get(EquipmentParts.Weapon));
    }

    // ------------------------------------------------------------------ 脱

    [Fact]
    public void UnequippingPutsTheItemBackIntoTheBag()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo item = Swordman(1, EquipmentParts.Ring);
        bag.Add(item);
        EquipActions.Equip(bag, slots, 0, 0, 1);
        Assert.True(EquipActions.Unequip(bag, slots, EquipmentParts.Ring));
        Assert.Null(slots.Get(EquipmentParts.Ring));
        Assert.Same(item, bag.Tab(InventoryTab.Equipment)[0].Info);
        Assert.Equal((0, 2), (slots.EquippedCount, slots.Version));
    }

    [Fact]
    public void UnequippingAnEmptyOrNonexistentSlotDoesNothing()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        Assert.False(EquipActions.Unequip(bag, slots, EquipmentParts.Weapon));
        Assert.False(EquipActions.Unequip(bag, slots, 9));
        Assert.False(EquipActions.Unequip(bag, slots, 99));
        Assert.Equal(0, slots.Version);
    }

    [Fact]
    public void UnequippingNeedsARoomInTheEquipmentTab()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo worn = Swordman(1);
        bag.Add(worn);
        EquipActions.Equip(bag, slots, 0, 0, 1);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.Equal(1, bag.Add(Swordman(100 + i, EquipmentParts.Coat)));
        }
        Assert.False(EquipActions.Unequip(bag, slots, EquipmentParts.Weapon));                           // 装备页满了
        Assert.Same(worn, slots.Get(EquipmentParts.Weapon));
        Assert.Equal(Inventory.SlotsPerTab, bag.Tab(InventoryTab.Equipment).Count);
        bag.Add(Stack(7));                                                                                // 别的页有空没用
        Assert.False(EquipActions.Unequip(bag, slots, EquipmentParts.Weapon));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => EquipActions.Equip(null, new EquipmentSlots(), 0, 0, 1));
        Assert.Throws<ArgumentNullException>(() => EquipActions.Equip(new Inventory(), null, 0, 0, 1));
        Assert.Throws<ArgumentNullException>(() => EquipActions.Unequip(null, new EquipmentSlots(), 10));
        Assert.Throws<ArgumentNullException>(() => EquipActions.Unequip(new Inventory(), null, 10));
    }

    // ------------------------------------------------------------------ 背包的两个新动作

    [Fact]
    public void TakeAtRemovesACellAndShiftsTheRest()
    {
        var bag = new Inventory();
        bag.Add(Swordman(1));
        bag.Add(Swordman(2));
        Assert.Null(bag.TakeAt(InventoryTab.Equipment, 2));
        Assert.Null(bag.TakeAt(InventoryTab.Equipment, -1));
        Assert.Null(bag.TakeAt(InventoryTab.Material, 0));
        ItemStack taken = bag.TakeAt(InventoryTab.Equipment, 0);
        Assert.Equal(1, taken.Info.Code);
        Assert.Equal(new[] { 2 }, bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
    }

    [Fact]
    public void ReplaceAtOnlyReplacesAnEquipmentCellWithAnEquipmentItem()
    {
        var bag = new Inventory();
        bag.Add(Swordman(1));
        bag.Add(Stack(9));
        Assert.True(bag.ReplaceAt(InventoryTab.Equipment, 0, Swordman(5)));
        Assert.Equal(5, bag.Tab(InventoryTab.Equipment)[0].Info.Code);
        Assert.Equal(1, bag.Tab(InventoryTab.Equipment)[0].Count);
        Assert.False(bag.ReplaceAt(InventoryTab.Equipment, 1, Swordman(6)));                          // 没有这一格
        Assert.False(bag.ReplaceAt(InventoryTab.Equipment, 0, null));
        Assert.False(bag.ReplaceAt(InventoryTab.Equipment, 0, Stack(8)));                             // 不是装备
        Assert.False(bag.ReplaceAt(InventoryTab.Material, 0, Swordman(7)));                           // 不属于那一页
        Assert.Equal(5, bag.Tab(InventoryTab.Equipment)[0].Info.Code);
        Assert.Equal(9, bag.Tab(InventoryTab.Material)[0].Info.Code);
    }
}
