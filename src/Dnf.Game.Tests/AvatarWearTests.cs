namespace Dnf.Game.Tests;

/// <summary>
/// 装扮栏与时装穿脱规则(r14-avatar):10 个槽与部位 0 到 9 一一对应、与普通装备同一条规则链(槽、职业、等级)、
/// 换穿原位交换、与普通装备栏互不干扰。物品全是合成的(编号 900 起)。
/// </summary>
public class AvatarWearTests
{
    private static ItemInfo Avatar(int code, int part, int level = 1, params string[] jobs)
    {
        var item = new ItemInfo(code, "equipment/test/a" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level };
        item.UsableJobs.AddRange(jobs);
        return item;
    }

    private static ItemInfo Swordman(int code, int part, int level = 1)
    {
        return Avatar(code, part, level, "[swordman]");
    }

    private static ItemInfo Stack(int code)
    {
        return new ItemInfo(code, "stackable/s" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Material };
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

    private static int[] Codes(Inventory bag)
    {
        return bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code).ToArray();
    }

    // ------------------------------------------------------------------ 装扮栏

    [Fact]
    public void TheTenSlotsAreTheAvatarPartsZeroToNine()
    {
        Assert.Equal(10, AvatarSlots.Count);
        Assert.Equal(EquipmentParts.HatAvatar, AvatarSlots.First);
        Assert.Equal(EquipmentParts.AuroraAvatar, AvatarSlots.Last);
        Assert.False(AvatarSlots.IsSlot(-1));
        Assert.True(AvatarSlots.IsSlot(0));
        Assert.True(AvatarSlots.IsSlot(9));
        Assert.False(AvatarSlots.IsSlot(10));
        for (int part = -3; part <= 30; part++)
        {
            Assert.Equal(EquipmentParts.IsAvatar(part), AvatarSlots.IsSlot(part));
        }
        var slots = new AvatarSlots();
        Assert.Null(slots.Get(-1));
        Assert.Null(slots.Get(10));
        Assert.Equal(0, slots.WornCount);
        Assert.Empty(slots.Items);
        Assert.Equal(0, slots.Version);
    }

    [Fact]
    public void RestorePlacesByPartAndTracksVersionAndOrder()
    {
        var slots = new AvatarSlots();
        ItemInfo shoes = Swordman(900, EquipmentParts.HatAvatar + 5);
        ItemInfo hair = Swordman(901, EquipmentParts.HatAvatar + 1);
        ItemInfo coat = Swordman(902, EquipmentParts.HatAvatar + 3);
        Assert.True(slots.Restore(5, shoes));
        Assert.True(slots.Restore(1, hair));
        Assert.True(slots.Restore(3, coat));
        Assert.Equal(3, slots.Version);
        Assert.Equal(3, slots.WornCount);
        Assert.Same(hair, slots.Get(1));
        Assert.Same(coat, slots.Get(3));
        Assert.Same(shoes, slots.Get(5));
        Assert.Equal(new[] { 901, 902, 900 }, slots.Items.Select(i => i.Code).ToArray());   // 按部位号从小到大

        // 同一件再放一次不算变化
        Assert.True(slots.Restore(3, coat));
        Assert.Equal(3, slots.Version);

        // 清空:有东西算一次变化,空的不算
        slots.Clear();
        Assert.Equal(4, slots.Version);
        Assert.Equal(0, slots.WornCount);
        slots.Clear();
        Assert.Equal(4, slots.Version);
    }

    [Fact]
    public void RestoreOnlyTakesAnEquipmentOfThatPart()
    {
        var slots = new AvatarSlots();
        Assert.False(slots.Restore(3, null));
        Assert.False(slots.Restore(3, Swordman(900, 2)));                         // 部位与槽不一致
        Assert.False(slots.Restore(3, Stack(901)));                              // 不是装备
        ItemInfo stackOfThatPart = Stack(904);
        stackOfThatPart.Part = 3;                                                // 就算 Part 字段碰巧对得上,不是装备也放不进去
        Assert.False(slots.Restore(3, stackOfThatPart));
        Assert.False(slots.Restore(10, Swordman(902, EquipmentParts.Weapon)));    // 槽不在 0 到 9
        Assert.False(slots.Restore(-1, Swordman(903, 0)));
        Assert.Equal(0, slots.Version);
        Assert.Equal(0, slots.WornCount);
    }

    // ------------------------------------------------------------------ 规则

    [Fact]
    public void EachPartFitsOnlyItsOwnSlot()
    {
        for (int part = 0; part <= 9; part++)
        {
            ItemInfo item = Swordman(900 + part, part);
            for (int slot = 0; slot <= 9; slot++)
            {
                Assert.Equal(part == slot ? EquipResult.Ok : EquipResult.WrongSlot, AvatarRules.Check(item, slot, 0, 99));
            }
            Assert.Equal(EquipResult.Ok, AvatarRules.Check(item, 0, 99));   // 不指定槽:放进自己那个槽
        }
    }

    [Fact]
    public void CheckGoesThroughItsReasonsInTheSameOrderAsNormalEquipment()
    {
        Assert.Equal(EquipResult.NoItem, AvatarRules.Check(null, 3, 0, 1));
        Assert.Equal(EquipResult.NoItem, AvatarRules.Check(null, 99, 0, 1));                  // 没有物品压过槽
        Assert.Equal(EquipResult.NotEquipment, AvatarRules.Check(Stack(900), 3, 0, 1));
        Assert.Equal(EquipResult.NotEquipment, AvatarRules.Check(Stack(900), 99, 0, 1));      // 不是装备压过槽
        Assert.Equal(EquipResult.NotWearable, AvatarRules.Check(Swordman(901, 3), 10, 0, 1)); // 槽不在 0 到 9
        Assert.Equal(EquipResult.NotWearable, AvatarRules.Check(Swordman(901, 3), -1, 0, 1));
        Assert.Equal(EquipResult.WrongSlot, AvatarRules.Check(Swordman(902, 3), 4, 0, 1));
        Assert.Equal(EquipResult.WrongJob, AvatarRules.Check(Avatar(903, 3, 1, "[mage]"), 3, 0, 1));
        Assert.Equal(EquipResult.LevelTooLow, AvatarRules.Check(Swordman(904, 3, 5), 3, 0, 4));
        Assert.Equal(EquipResult.Ok, AvatarRules.Check(Swordman(905, 3), 3, 0, 1));

        // 同时不满足几条时报次序靠前的:槽压过职业与等级,职业压过等级
        Assert.Equal(EquipResult.WrongSlot, AvatarRules.Check(Avatar(906, 3, 99, "[mage]"), 4, 0, 1));
        Assert.Equal(EquipResult.WrongJob, AvatarRules.Check(Avatar(907, 3, 99, "[mage]"), 3, 0, 1));
    }

    [Fact]
    public void TheJobFlagsFollowTheNormalEquipmentConvention()
    {
        ItemInfo none = Avatar(900, 3);                                           // 没写 [usable job]:谁也穿不了
        ItemInfo all = Avatar(901, 3, 1, "[all]");                                // 不是职业名:全部职业
        ItemInfo two = Avatar(902, 3, 1, "[swordman]", "[fighter]");
        for (int job = 0; job < EquipRules.JobCount; job++)
        {
            Assert.Equal(EquipResult.WrongJob, AvatarRules.Check(none, job, 99));
            Assert.Equal(EquipResult.Ok, AvatarRules.Check(all, job, 99));
            Assert.Equal(job <= 1 ? EquipResult.Ok : EquipResult.WrongJob, AvatarRules.Check(two, job, 99));
        }
        Assert.Equal(EquipResult.WrongJob, AvatarRules.Check(all, EquipRules.JobCount, 99));   // 越界的职业下标谁也不是
        Assert.Equal(EquipResult.WrongJob, AvatarRules.Check(all, -1, 99));
    }

    [Fact]
    public void TheLevelLimitIsInclusiveForEveryAvatarPart()
    {
        for (int part = 0; part <= 9; part++)
        {
            ItemInfo item = Swordman(900 + part, part, 20);
            Assert.Equal(EquipResult.LevelTooLow, AvatarRules.Check(item, 0, 19));
            Assert.Equal(EquipResult.Ok, AvatarRules.Check(item, 0, 20));
            Assert.Equal(EquipResult.Ok, AvatarRules.Check(item, 0, 85));
        }
    }

    [Fact]
    public void CreatureItemsGetNoSlotExemptionHere()
    {
        ItemInfo pet = Swordman(900, EquipmentParts.Creature);
        Assert.Equal(EquipResult.WrongSlot, AvatarRules.Check(pet, 3, 0, 99));
        Assert.Equal(EquipResult.NotWearable, AvatarRules.Check(pet, 0, 99));
    }

    [Fact]
    public void ThePartlessOverloadUsesTheItemsOwnPartAndNonAvatarItemsHaveNoSlot()
    {
        Assert.Equal(EquipResult.Ok, AvatarRules.Check(Swordman(900, EquipmentParts.HatAvatar), 0, 1));
        Assert.Equal(EquipResult.Ok, AvatarRules.Check(Swordman(901, EquipmentParts.AuroraAvatar), 0, 1));
        Assert.Equal(EquipResult.NotWearable, AvatarRules.Check(Swordman(902, EquipmentParts.Weapon), 0, 1));
        Assert.Equal(EquipResult.NotWearable, AvatarRules.Check(Swordman(903, EquipmentParts.MagicStone), 0, 1));
        Assert.Equal(EquipResult.NotWearable, AvatarRules.Check(Swordman(904, EquipmentParts.Unknown), 0, 1));
        Assert.Equal(EquipResult.NotEquipment, AvatarRules.Check(Stack(905), 0, 1));
        Assert.Equal(EquipResult.NoItem, AvatarRules.Check(null, 0, 1));
    }

    [Fact]
    public void EveryEquipmentIsWearableThroughAtMostOneOfTheTwoRuleSets()
    {
        for (int part = -1; part <= EquipmentParts.Unknown; part++)
        {
            ItemInfo item = Swordman(900, part);
            bool asAvatar = AvatarRules.Check(item, 0, 99) == EquipResult.Ok;
            bool asGear = EquipRules.Check(item, 0, 99) == EquipResult.Ok;
            Assert.False(asAvatar && asGear);
            Assert.Equal(EquipmentParts.IsAvatar(part), asAvatar);
            Assert.Equal(EquipmentParts.IsEnchantable(part), asGear);
        }
    }

    // ------------------------------------------------------------------ 穿与脱

    [Fact]
    public void WearingIntoAnEmptySlotTakesTheCellAndFillsTheSlot()
    {
        ItemInfo hat = Swordman(900, 0);
        ItemInfo coat = Swordman(901, 3);
        ItemInfo weapon = Swordman(902, EquipmentParts.Weapon);
        Inventory bag = BagWith(hat, coat, weapon);
        var slots = new AvatarSlots();

        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 1, 0, 1));
        Assert.Same(coat, slots.Get(3));
        Assert.Equal(new[] { 900, 902 }, Codes(bag));            // 那一格被腾空、后面的往前补
        Assert.Equal(1, slots.Version);
        Assert.Equal(1, slots.WornCount);
    }

    [Fact]
    public void WearingOverAWornItemSwapsItBackIntoTheSameCell()
    {
        ItemInfo coatA = Swordman(900, 3);
        ItemInfo hat = Swordman(901, 0);
        ItemInfo coatB = Swordman(902, 3);
        ItemInfo weapon = Swordman(903, EquipmentParts.Weapon);
        Inventory bag = BagWith(coatA, hat, coatB, weapon);
        var slots = new AvatarSlots();
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 0, 0, 1));    // 先穿 A,A 的那一格腾空
        Assert.Equal(new[] { 901, 902, 903 }, Codes(bag));
        int total = bag.TotalCount + slots.WornCount;

        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 1, 0, 1));    // 现在第 1 格是 B
        Assert.Same(coatB, slots.Get(3));
        Assert.Equal(new[] { 901, 900, 903 }, Codes(bag));                         // A 回到 B 原来的那一格,别的格不动
        Assert.Equal(total, bag.TotalCount + slots.WornCount);                    // 一件不多一件不少
        Assert.Equal(2, slots.Version);
    }

    [Fact]
    public void ARefusedWearChangesNothing()
    {
        ItemInfo wrongJob = Avatar(900, 3, 1, "[mage]");
        ItemInfo tooHigh = Swordman(901, 4, 50);
        ItemInfo weapon = Swordman(902, EquipmentParts.Weapon);
        ItemInfo stackable = Stack(903);
        Inventory bag = BagWith(wrongJob, tooHigh, weapon, stackable);
        var slots = new AvatarSlots();

        Assert.Equal(EquipResult.WrongJob, AvatarActions.Wear(bag, slots, 0, 0, 10));
        Assert.Equal(EquipResult.LevelTooLow, AvatarActions.Wear(bag, slots, 1, 0, 10));
        Assert.Equal(EquipResult.NotWearable, AvatarActions.Wear(bag, slots, 2, 0, 10));      // 普通装备不是时装
        Assert.Equal(EquipResult.NoItem, AvatarActions.Wear(bag, slots, 3, 0, 10));           // 装备页只有三格
        Assert.Equal(EquipResult.NoItem, AvatarActions.Wear(bag, slots, -1, 0, 10));
        Assert.Equal(EquipResult.NoItem, AvatarActions.Wear(bag, slots, 99, 0, 10));

        Assert.Equal(new[] { 900, 901, 902 }, Codes(bag));
        Assert.Equal(1, bag.CountOf(903));                                                    // 消耗页的东西没被动
        Assert.Equal(0, slots.WornCount);
        Assert.Equal(0, slots.Version);
    }

    [Fact]
    public void CanShowIsAskedOnlyAfterTheRulesPass()
    {
        ItemInfo coat = Swordman(900, 3);
        ItemInfo mageHat = Avatar(901, 0, 1, "[mage]");
        Inventory bag = BagWith(coat, mageHat);
        var slots = new AvatarSlots();
        var asked = new List<int>();

        // 规则不通过:不问
        Assert.Equal(EquipResult.WrongJob, AvatarActions.Wear(bag, slots, 1, 0, 10, item => { asked.Add(item.Code); return true; }));
        Assert.Empty(asked);

        // 规则通过但画面上穿不出来:NotShowable,什么都不动
        Assert.Equal(EquipResult.NotShowable, AvatarActions.Wear(bag, slots, 0, 0, 10, item => { asked.Add(item.Code); return false; }));
        Assert.Equal(new[] { 900 }, asked);
        Assert.Equal(new[] { 900, 901 }, Codes(bag));
        Assert.Equal(0, slots.Version);

        // 通过
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 0, 0, 10, item => true));
        Assert.Same(coat, slots.Get(3));
    }

    [Fact]
    public void TakingOffReturnsTheItemToTheBag()
    {
        ItemInfo coat = Swordman(900, 3);
        Inventory bag = BagWith(coat);
        var slots = new AvatarSlots();
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 0, 0, 1));
        Assert.Empty(bag.Tab(InventoryTab.Equipment));

        Assert.True(AvatarActions.TakeOff(bag, slots, 3));
        Assert.Null(slots.Get(3));
        Assert.Equal(new[] { 900 }, Codes(bag));
        Assert.Equal(1, bag.CountOf(900));
        Assert.Equal(2, slots.Version);
    }

    [Fact]
    public void TakingOffNeedsAWornItemAndRoomInTheBag()
    {
        ItemInfo coat = Swordman(900, 3);
        ItemInfo filler = Swordman(901, EquipmentParts.Weapon);
        Inventory bag = BagWith(coat, filler);
        var slots = new AvatarSlots();
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 0, 0, 1));

        Assert.False(AvatarActions.TakeOff(bag, slots, 4));        // 空槽
        Assert.False(AvatarActions.TakeOff(bag, slots, 10));       // 没有这个槽
        Assert.False(AvatarActions.TakeOff(bag, slots, -1));
        Assert.Equal(1, slots.Version);

        // 装备页只有一格且已被填满:放不下,什么都不动
        var tiny = new Inventory(1);
        Assert.Equal(1, tiny.Add(filler));
        var slots2 = new AvatarSlots();
        Assert.True(slots2.Restore(3, coat));
        Assert.False(AvatarActions.TakeOff(tiny, slots2, 3));
        Assert.Same(coat, slots2.Get(3));
        Assert.Equal(new[] { 901 }, Codes(tiny));
        Assert.Equal(1, slots2.Version);
    }

    [Fact]
    public void WearingAndTakingOffEverySlotRoundTrips()
    {
        var items = new ItemInfo[10];
        for (int part = 0; part < 10; part++)
        {
            items[part] = Swordman(900 + part, part);
        }
        Inventory bag = BagWith(items);
        var slots = new AvatarSlots();
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, slots, 0, 0, 1));   // 每次穿最前面一格,后面的往前补
        }
        Assert.Empty(bag.Tab(InventoryTab.Equipment));
        Assert.Equal(10, slots.WornCount);
        for (int part = 0; part < 10; part++)
        {
            Assert.Same(items[part], slots.Get(part));
        }
        for (int part = 0; part < 10; part++)
        {
            Assert.True(AvatarActions.TakeOff(bag, slots, part));
        }
        Assert.Equal(0, slots.WornCount);
        Assert.Equal(10, bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(20, slots.Version);
        for (int part = 0; part < 10; part++)
        {
            Assert.Equal(1, bag.CountOf(900 + part));
        }
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        var bag = new Inventory();
        var slots = new AvatarSlots();
        Assert.Throws<ArgumentNullException>(() => AvatarActions.Wear(null, slots, 0, 0, 1));
        Assert.Throws<ArgumentNullException>(() => AvatarActions.Wear(bag, null, 0, 0, 1));
        Assert.Throws<ArgumentNullException>(() => AvatarActions.TakeOff(null, slots, 0));
        Assert.Throws<ArgumentNullException>(() => AvatarActions.TakeOff(bag, null, 0));
    }

    // ------------------------------------------------------------------ 与普通装备栏并存

    [Fact]
    public void TheNormalEquipmentActionsCannotTouchTheAvatarBarAndViceVersa()
    {
        ItemInfo coatAvatar = Swordman(900, 3);
        ItemInfo weapon = Swordman(901, EquipmentParts.Weapon);
        Inventory bag = BagWith(coatAvatar, weapon);
        var gearSlots = new EquipmentSlots();
        var avatarSlots = new AvatarSlots();

        // 普通装备的穿法对时装是"没有格子";时装的穿法对普通装备也是
        Assert.Equal(EquipResult.NotWearable, EquipActions.Equip(bag, gearSlots, 0, 0, 99));
        Assert.Equal(EquipResult.NotWearable, AvatarActions.Wear(bag, avatarSlots, 1, 0, 99));
        Assert.Equal(new[] { 900, 901 }, Codes(bag));
        Assert.Equal(0, gearSlots.Version);
        Assert.Equal(0, avatarSlots.Version);

        // 各穿各的:互不改对方的版本号与内容
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(bag, avatarSlots, 0, 0, 99));
        Assert.Equal(0, gearSlots.Version);
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, gearSlots, 0, 0, 99));
        Assert.Equal(1, avatarSlots.Version);
        Assert.Equal(1, gearSlots.Version);
        Assert.Same(coatAvatar, avatarSlots.Get(3));
        Assert.Same(weapon, gearSlots.Get(EquipmentParts.Weapon));
        Assert.Null(gearSlots.Get(3));
        Assert.Null(avatarSlots.Get(EquipmentParts.Weapon));

        // 脱:部位号决定归谁
        Assert.False(EquipActions.Unequip(bag, gearSlots, 3));                  // 普通栏没有 3 号槽
        Assert.False(AvatarActions.TakeOff(bag, avatarSlots, EquipmentParts.Weapon));
        Assert.Same(coatAvatar, avatarSlots.Get(3));
        Assert.Same(weapon, gearSlots.Get(EquipmentParts.Weapon));
        Assert.True(AvatarActions.TakeOff(bag, avatarSlots, 3));
        Assert.Equal(1, gearSlots.Version);                                      // 脱时装没动普通栏
        Assert.Same(weapon, gearSlots.Get(EquipmentParts.Weapon));
        Assert.True(EquipActions.Unequip(bag, gearSlots, EquipmentParts.Weapon));
        Assert.Equal(2, avatarSlots.Version);                                    // 脱武器没动装扮栏
        Assert.Equal(2, bag.Tab(InventoryTab.Equipment).Count);
    }

    [Fact]
    public void AllTwentyTwoBodySlotsCanBeWornAtOnce()
    {
        var items = new List<ItemInfo>();
        for (int part = 0; part <= EquipmentParts.MagicStone; part++)
        {
            items.Add(Swordman(900 + part, part));
        }
        Inventory bag = BagWith(items.ToArray());
        var gearSlots = new EquipmentSlots();
        var avatarSlots = new AvatarSlots();
        for (int step = 0; step < items.Count; step++)                           // 有界循环:穿错了会在这里断言失败,而不是原地打转
        {
            ItemInfo first = bag.Tab(InventoryTab.Equipment)[0].Info;
            EquipResult result = AvatarSlots.IsSlot(first.Part)
                ? AvatarActions.Wear(bag, avatarSlots, 0, 0, 99)
                : EquipActions.Equip(bag, gearSlots, 0, 0, 99);
            Assert.Equal(EquipResult.Ok, result);
            Assert.Equal(items.Count - step - 1, bag.Tab(InventoryTab.Equipment).Count);
        }
        Assert.Equal(10, avatarSlots.WornCount);
        Assert.Equal(12, gearSlots.EquippedCount);
        for (int part = 0; part <= EquipmentParts.MagicStone; part++)
        {
            ItemInfo worn = AvatarSlots.IsSlot(part) ? avatarSlots.Get(part) : gearSlots.Get(part);
            Assert.NotNull(worn);
            Assert.Equal(part, worn.Part);
        }
    }
}
