namespace Dnf.Game.Tests;

/// <summary>
/// 宠物格(22 号放宠物、23 到 25 号放神器)的穿脱规则、与普通装备栏和时装栏互不串扰、神器的经验加成。
/// 数据全是合成的(宠物实例号 1 起、物品编号 960 起):物品只是带部位号的壳,宠物脚本与经验表现造。
/// </summary>
public class PetEquipmentTests
{
    private const PetArtifactColors All = PetArtifactColors.Red | PetArtifactColors.Blue | PetArtifactColors.Green;

    private static readonly PetExpTable Table = new PetExpTable(10, 30, 60, 100);

    private static ItemInfo Gear(int code, int part)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = 1 };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    private static ItemInfo Potion(int code)
    {
        return new ItemInfo(code, "stackable/p" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste };
    }

    private static PetInstance Pet(int uid, PetArtifactColors colors = All)
    {
        var def = new PetDef(900 + uid, "creature/test/p" + uid + ".cre") { MaxLevel = 5, ArtifactColors = colors };
        return PetInstance.Hatch(uid, 950 + uid, def, Table, 0);
    }

    private static PetRoster RosterOf(params PetInstance[] pets)
    {
        var roster = new PetRoster();
        foreach (PetInstance pet in pets)
        {
            Assert.True(roster.Add(pet));
        }
        return roster;
    }

    // ------------------------------------------------------------------ 三套格子互不相交

    [Fact]
    public void TheThreeSlotSetsCoverDisjointPartRanges()
    {
        for (int part = EquipmentParts.HatAvatar; part <= EquipmentParts.AuroraAvatar; part++)
        {
            Assert.False(EquipmentSlots.IsSlot(part));
            Assert.False(PetEquipment.IsSlot(part));
        }
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            Assert.True(EquipmentSlots.IsSlot(part));
            Assert.False(PetEquipment.IsSlot(part));
            Assert.False(PetEquipment.IsArtifactSlot(part));
        }
        Assert.True(PetEquipment.IsSlot(PetEquipment.PetPart));
        Assert.False(PetEquipment.IsArtifactSlot(PetEquipment.PetPart));
        Assert.False(EquipmentSlots.IsSlot(PetEquipment.PetPart));
        for (int part = PetEquipment.FirstArtifact; part <= PetEquipment.LastArtifact; part++)
        {
            Assert.True(PetEquipment.IsSlot(part));
            Assert.True(PetEquipment.IsArtifactSlot(part));
            Assert.False(EquipmentSlots.IsSlot(part));
        }
        Assert.False(PetEquipment.IsSlot(EquipmentParts.Unknown));
        Assert.False(EquipmentSlots.IsSlot(EquipmentParts.Unknown));
        Assert.Equal(22, PetEquipment.PetPart);
        Assert.Equal(23, PetEquipment.FirstArtifact);
        Assert.Equal(25, PetEquipment.LastArtifact);
        Assert.Equal(3, PetEquipment.ArtifactCount);
    }

    [Theory]
    [InlineData(EquipmentParts.HatAvatar, WearTarget.Regular)]
    [InlineData(EquipmentParts.AuroraAvatar, WearTarget.Regular)]
    [InlineData(EquipmentParts.Weapon, WearTarget.Regular)]
    [InlineData(EquipmentParts.MagicStone, WearTarget.Regular)]
    [InlineData(EquipmentParts.Creature, WearTarget.PetSlot)]
    [InlineData(EquipmentParts.ArtifactRed, WearTarget.ArtifactSlot)]
    [InlineData(EquipmentParts.ArtifactBlue, WearTarget.ArtifactSlot)]
    [InlineData(EquipmentParts.ArtifactGreen, WearTarget.ArtifactSlot)]
    [InlineData(EquipmentParts.Unknown, WearTarget.None)]
    [InlineData(-1, WearTarget.None)]
    public void RouteSendsEachItemToItsOwnSlotSet(int part, WearTarget expected)
    {
        Assert.Equal(expected, PetEquipRules.Route(Gear(960, part)));
    }

    [Fact]
    public void RouteGivesNoneForNothingAndForStackables()
    {
        Assert.Equal(WearTarget.None, PetEquipRules.Route(null));
        Assert.Equal(WearTarget.None, PetEquipRules.Route(Potion(970)));
    }

    [Theory]
    [InlineData(EquipmentParts.Weapon)]
    [InlineData(EquipmentParts.Creature)]
    [InlineData(EquipmentParts.ArtifactRed)]
    [InlineData(EquipmentParts.ArtifactGreen)]
    public void AStackableGoesNowhereEvenIfItsPartFieldHoldsAWearableNumber(int part)
    {
        // 可叠加物品的部位本来是 -1;数据出错把它写成了能穿的部位号时,是不是装备仍要先判,不能被部位号带去任何一套格子。
        var item = new ItemInfo(970, "stackable/p970.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste, Part = part };
        Assert.Equal(WearTarget.None, PetEquipRules.Route(item));
    }

    // ------------------------------------------------------------------ 不串扰

    [Theory]
    [InlineData(EquipmentParts.Creature)]
    [InlineData(EquipmentParts.ArtifactRed)]
    [InlineData(EquipmentParts.ArtifactBlue)]
    [InlineData(EquipmentParts.ArtifactGreen)]
    public void PetClassItemsAreRefusedByTheRegularEquipPathAndLeftInTheBag(int part)
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo item = Gear(960, part);
        bag.Add(item);
        Assert.Equal(EquipResult.NotWearable, EquipActions.Equip(bag, slots, 0, 0, 99));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
        Assert.Same(item, bag.Tab(InventoryTab.Equipment)[0].Info);
        Assert.Equal(0, slots.EquippedCount);
        Assert.Equal(0, slots.Version);
    }

    [Theory]
    [InlineData(EquipmentParts.Weapon)]
    [InlineData(EquipmentParts.Coat)]
    [InlineData(EquipmentParts.MagicStone)]
    [InlineData(EquipmentParts.Creature)]
    public void OnlyArtifactItemsCanBePlacedIntoTheArtifactSlots(int part)
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        equipment.RestorePet(Pet(1));
        ItemInfo item = Gear(960, part);
        bag.Add(item);
        Assert.Equal(PetEquipResult.NotArtifactSlot, PetEquipActions.EquipArtifact(bag, equipment, 0));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
        Assert.Equal(0, equipment.ArtifactCountEquipped);
    }

    [Fact]
    public void PetSlotsAndRegularSlotsWorkSideBySideWithoutTouchingEachOther()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        var equipment = new PetEquipment();
        PetInstance pet = Pet(1);
        PetRoster roster = RosterOf(pet);
        ItemInfo weapon = Gear(960, EquipmentParts.Weapon);
        ItemInfo coat = Gear(961, EquipmentParts.Coat);
        ItemInfo red = Gear(962, EquipmentParts.ArtifactRed);
        ItemInfo blue = Gear(963, EquipmentParts.ArtifactBlue);
        bag.Add(weapon);
        bag.Add(red);
        bag.Add(coat);
        bag.Add(blue);

        // 装备栏:武器与外套。神器和宠物不动。
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, 0, 1));
        Assert.Equal(EquipResult.NotWearable, EquipActions.Equip(bag, slots, 0, 0, 1));    // 现在第 0 格是红神器
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 1, 0, 1));             // 外套
        Assert.Equal(2, slots.EquippedCount);
        Assert.Equal(0, equipment.Version);
        Assert.Null(equipment.Pet);
        Assert.Equal(0, equipment.ArtifactCountEquipped);

        // 宠物格:宠物与两个神器。装备栏不动。
        int slotsVersion = slots.Version;
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipPet(roster, equipment, 1, 0, false));
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipArtifact(bag, equipment, 0));
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipArtifact(bag, equipment, 0));
        Assert.Equal(slotsVersion, slots.Version);
        Assert.Equal(2, slots.EquippedCount);
        Assert.Same(weapon, slots.Get(EquipmentParts.Weapon));
        Assert.Same(coat, slots.Get(EquipmentParts.Coat));
        Assert.Null(slots.Get(EquipmentParts.Creature));
        Assert.Null(slots.Get(EquipmentParts.ArtifactRed));
        Assert.Same(pet, equipment.Pet);
        Assert.Same(red, equipment.Artifact(EquipmentParts.ArtifactRed));
        Assert.Same(blue, equipment.Artifact(EquipmentParts.ArtifactBlue));
        Assert.Null(equipment.Artifact(EquipmentParts.ArtifactGreen));
        Assert.Null(equipment.Artifact(EquipmentParts.Weapon));
        Assert.Empty(bag.Tab(InventoryTab.Equipment));

        // 再脱下装备栏的武器:宠物格不受影响。
        int petVersion = equipment.Version;
        Assert.True(EquipActions.Unequip(bag, slots, EquipmentParts.Weapon));
        Assert.Equal(petVersion, equipment.Version);
        Assert.Same(pet, equipment.Pet);
        Assert.Equal(2, equipment.ArtifactCountEquipped);

        // 卸下宠物:装备栏与神器都不动(神器归用户,不归宠物)。
        int slotsAfter = slots.Version;
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.UnequipPet(equipment, 0));
        Assert.Equal(slotsAfter, slots.Version);
        Assert.Equal(1, slots.EquippedCount);
        Assert.Equal(2, equipment.ArtifactCountEquipped);
    }

    // ------------------------------------------------------------------ 神器的穿脱规则

    [Fact]
    public void AnArtifactNeedsAnEquippedPet()
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        bag.Add(red);
        Assert.Equal(PetEquipResult.NoPetEquipped, PetEquipActions.EquipArtifact(bag, equipment, 0));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
        Assert.Null(equipment.Artifact(EquipmentParts.ArtifactRed));
        Assert.Equal(0, equipment.Version);
    }

    [Fact]
    public void AnArtifactNeedsItsColorToBeOpenOnThePetsScript()
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        equipment.RestorePet(Pet(1, PetArtifactColors.Blue));
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        ItemInfo blue = Gear(961, EquipmentParts.ArtifactBlue);
        bag.Add(red);
        bag.Add(blue);
        Assert.Equal(PetEquipResult.SlotUnavailable, PetEquipActions.EquipArtifact(bag, equipment, 0));
        Assert.Equal(2, bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipArtifact(bag, equipment, 1));
        Assert.Same(blue, equipment.Artifact(EquipmentParts.ArtifactBlue));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
        Assert.Same(red, bag.Tab(InventoryTab.Equipment)[0].Info);
    }

    [Fact]
    public void ReplacingAnArtifactPutsTheOldOneBackInTheSameBagCell()
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        equipment.RestorePet(Pet(1));
        ItemInfo first = Gear(960, EquipmentParts.ArtifactRed);
        ItemInfo second = Gear(961, EquipmentParts.ArtifactRed);
        ItemInfo filler = Gear(962, EquipmentParts.Weapon);
        bag.Add(filler);
        bag.Add(first);
        bag.Add(second);
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipArtifact(bag, equipment, 1));
        Assert.Same(first, equipment.Artifact(EquipmentParts.ArtifactRed));
        // 背包现在是 [filler, second];把 second(下标 1)换进去。
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipArtifact(bag, equipment, 1));
        Assert.Same(second, equipment.Artifact(EquipmentParts.ArtifactRed));
        Assert.Equal(2, bag.Tab(InventoryTab.Equipment).Count);
        Assert.Same(filler, bag.Tab(InventoryTab.Equipment)[0].Info);
        Assert.Same(first, bag.Tab(InventoryTab.Equipment)[1].Info);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    public void ABagIndexWithoutAnItemIsNoItem(int index)
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        equipment.RestorePet(Pet(1));
        Assert.Equal(PetEquipResult.NoItem, PetEquipActions.EquipArtifact(bag, equipment, index));
    }

    [Fact]
    public void RemovingAnArtifactWorksWithoutAPetAndPutsItBackInTheBag()
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        ItemInfo green = Gear(960, EquipmentParts.ArtifactGreen);
        Assert.True(equipment.RestoreArtifact(EquipmentParts.ArtifactGreen, green));
        Assert.Null(equipment.Pet);
        int version = equipment.Version;
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.UnequipArtifact(bag, equipment, EquipmentParts.ArtifactGreen));
        Assert.Null(equipment.Artifact(EquipmentParts.ArtifactGreen));
        Assert.Same(green, bag.Tab(InventoryTab.Equipment)[0].Info);
        Assert.Equal(version + 1, equipment.Version);
    }

    [Fact]
    public void RemovingFromAnEmptyOrNonArtifactSlotChangesNothing()
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        Assert.Equal(PetEquipResult.NoItem, PetEquipActions.UnequipArtifact(bag, equipment, EquipmentParts.ArtifactRed));
        Assert.Equal(PetEquipResult.NotArtifactSlot, PetEquipActions.UnequipArtifact(bag, equipment, EquipmentParts.Creature));
        Assert.Equal(PetEquipResult.NotArtifactSlot, PetEquipActions.UnequipArtifact(bag, equipment, EquipmentParts.Weapon));
        Assert.Equal(PetEquipResult.NotArtifactSlot, PetEquipActions.UnequipArtifact(bag, equipment, EquipmentParts.ArtifactGreen + 1));
        Assert.Empty(bag.Tab(InventoryTab.Equipment));
        Assert.Equal(0, equipment.Version);
    }

    [Fact]
    public void RemovingAnArtifactIntoAFullBagIsRefusedAndKeepsTheArtifact()
    {
        var bag = new Inventory(1);
        var equipment = new PetEquipment();
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        equipment.RestoreArtifact(EquipmentParts.ArtifactRed, red);
        bag.Add(Gear(961, EquipmentParts.Weapon));
        Assert.Equal(PetEquipResult.BagFull, PetEquipActions.UnequipArtifact(bag, equipment, EquipmentParts.ArtifactRed));
        Assert.Same(red, equipment.Artifact(EquipmentParts.ArtifactRed));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
    }

    // ------------------------------------------------------------------ 纯规则判断

    [Fact]
    public void ArtifactItemChecksComeInTheDocumentedOrder()
    {
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        Assert.Equal(PetEquipResult.NotArtifactSlot, PetEquipRules.CheckArtifactItem(null, EquipmentParts.Weapon));
        Assert.Equal(PetEquipResult.NotArtifactSlot, PetEquipRules.CheckArtifactItem(red, EquipmentParts.Creature));
        Assert.Equal(PetEquipResult.NoItem, PetEquipRules.CheckArtifactItem(null, EquipmentParts.ArtifactRed));
        Assert.Equal(PetEquipResult.NotEquipment, PetEquipRules.CheckArtifactItem(Potion(970), EquipmentParts.ArtifactRed));
        Assert.Equal(PetEquipResult.Ok, PetEquipRules.CheckArtifactItem(red, EquipmentParts.ArtifactRed));
    }

    [Theory]
    [InlineData(EquipmentParts.ArtifactRed, EquipmentParts.ArtifactBlue)]
    [InlineData(EquipmentParts.ArtifactRed, EquipmentParts.ArtifactGreen)]
    [InlineData(EquipmentParts.ArtifactBlue, EquipmentParts.ArtifactRed)]
    [InlineData(EquipmentParts.ArtifactGreen, EquipmentParts.ArtifactBlue)]
    public void AnArtifactOnlyFitsTheSlotOfItsOwnColor(int itemPart, int slot)
    {
        Assert.Equal(PetEquipResult.WrongSlot, PetEquipRules.CheckArtifactItem(Gear(960, itemPart), slot));
        Assert.Equal(PetEquipResult.WrongSlot, PetEquipRules.CheckArtifactMove(Gear(960, itemPart), slot, Pet(1)));
    }

    [Fact]
    public void AMoveAddsThePetAndColorChecksAfterTheStructuralOnes()
    {
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        Assert.Equal(PetEquipResult.NoPetEquipped, PetEquipRules.CheckArtifactMove(red, EquipmentParts.ArtifactRed, null));
        Assert.Equal(PetEquipResult.SlotUnavailable, PetEquipRules.CheckArtifactMove(red, EquipmentParts.ArtifactRed, Pet(1, PetArtifactColors.Green)));
        Assert.Equal(PetEquipResult.SlotUnavailable, PetEquipRules.CheckArtifactMove(red, EquipmentParts.ArtifactRed, Pet(1, PetArtifactColors.None)));
        Assert.Equal(PetEquipResult.Ok, PetEquipRules.CheckArtifactMove(red, EquipmentParts.ArtifactRed, Pet(1, PetArtifactColors.Red)));
        // 结构问题优先于"没有宠物"。
        Assert.Equal(PetEquipResult.NoItem, PetEquipRules.CheckArtifactMove(null, EquipmentParts.ArtifactRed, null));
        Assert.Equal(PetEquipResult.WrongSlot, PetEquipRules.CheckArtifactMove(red, EquipmentParts.ArtifactBlue, null));
    }

    // ------------------------------------------------------------------ 宠物的穿脱

    [Fact]
    public void EquippingAPetUnequipsTheOldOneFirst()
    {
        PetInstance first = Pet(1);
        PetInstance second = Pet(2);
        PetRoster roster = RosterOf(first, second);
        var equipment = new PetEquipment();

        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipPet(roster, equipment, 1, 0, false));
        Assert.Same(first, equipment.Pet);
        Assert.Equal(PetState.Charging, first.State);
        Assert.Equal(1, equipment.Version);

        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipPet(roster, equipment, 2, 0, true));
        Assert.Same(second, equipment.Pet);
        Assert.Equal(PetState.Stored, first.State);
        Assert.Equal(PetState.Draining, second.State);
        Assert.Equal(2, equipment.Version);
    }

    [Fact]
    public void EquippingTheSamePetAgainOrAnUnknownOneChangesNothing()
    {
        PetInstance pet = Pet(1);
        PetRoster roster = RosterOf(pet);
        var equipment = new PetEquipment();
        EquipFirstPet(roster, equipment);
        Assert.Equal(PetEquipResult.AlreadyEquipped, PetEquipActions.EquipPet(roster, equipment, 1, 500, true));
        Assert.Equal(PetState.Charging, pet.State);
        Assert.Equal(1, equipment.Version);
        Assert.Equal(PetEquipResult.NoItem, PetEquipActions.EquipPet(roster, equipment, 99, 0, false));
        Assert.Same(pet, equipment.Pet);
    }

    private static void EquipFirstPet(PetRoster roster, PetEquipment equipment)
    {
        Assert.Equal(PetEquipResult.Ok, PetEquipActions.EquipPet(roster, equipment, 1, 0, false));
    }

    [Fact]
    public void UnequippingAPetSendsItToStoredAndKeepsTheArtifacts()
    {
        PetInstance pet = Pet(1);
        PetRoster roster = RosterOf(pet);
        var equipment = new PetEquipment();
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        equipment.RestoreArtifact(EquipmentParts.ArtifactRed, red);
        EquipFirstPet(roster, equipment);

        Assert.Equal(PetEquipResult.Ok, PetEquipActions.UnequipPet(equipment, 0));
        Assert.Null(equipment.Pet);
        Assert.Equal(PetState.Stored, pet.State);
        Assert.Same(pet, roster.Find(1));
        Assert.Same(red, equipment.Artifact(EquipmentParts.ArtifactRed));
        Assert.Equal(PetEquipResult.NoItem, PetEquipActions.UnequipPet(equipment, 0));
    }

    [Fact]
    public void EquippingAStarvedPetOutOfBattleRevivesIt()
    {
        PetInstance starved = PetInstance.Load(1, 951, new PetDef(901, "creature/test/p1.cre") { MaxLevel = 5 }, Table, 0, 0, 0);
        PetRoster roster = RosterOf(starved);
        var equipment = new PetEquipment();
        Assert.True(starved.IsStarved(0));
        PetEquipActions.EquipPet(roster, equipment, 1, 0, false);
        Assert.False(starved.IsStarved(0));
        Assert.Equal(1, starved.Stomach(0));
    }

    [Fact]
    public void ActionsRejectMissingArguments()
    {
        var bag = new Inventory();
        var equipment = new PetEquipment();
        var roster = new PetRoster();
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.EquipPet(null, equipment, 1, 0, false));
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.EquipPet(roster, null, 1, 0, false));
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.UnequipPet(null, 0));
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.EquipArtifact(null, equipment, 0));
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.EquipArtifact(bag, null, 0));
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.UnequipArtifact(null, equipment, 23));
        Assert.Throws<ArgumentNullException>(() => PetEquipActions.UnequipArtifact(bag, null, 23));
    }

    // ------------------------------------------------------------------ 直接还原与版本号

    [Fact]
    public void RestoringRejectsBadInputAndVersionsCountRealChangesOnly()
    {
        var equipment = new PetEquipment();
        Assert.False(equipment.RestorePet(null));
        Assert.False(equipment.RestoreArtifact(EquipmentParts.Weapon, Gear(960, EquipmentParts.Weapon)));
        Assert.False(equipment.RestoreArtifact(EquipmentParts.Creature, Gear(960, EquipmentParts.Creature)));
        Assert.False(equipment.RestoreArtifact(EquipmentParts.ArtifactRed, null));
        Assert.Equal(0, equipment.Version);

        PetInstance pet = Pet(1);
        ItemInfo red = Gear(960, EquipmentParts.ArtifactRed);
        Assert.True(equipment.RestorePet(pet));
        Assert.True(equipment.RestorePet(pet));
        Assert.True(equipment.RestoreArtifact(EquipmentParts.ArtifactRed, red));
        Assert.True(equipment.RestoreArtifact(EquipmentParts.ArtifactRed, red));
        Assert.Equal(2, equipment.Version);

        equipment.Clear();
        Assert.Equal(3, equipment.Version);
        Assert.Null(equipment.Pet);
        Assert.Equal(0, equipment.ArtifactCountEquipped);
        equipment.Clear();
        Assert.Equal(3, equipment.Version);
    }

    [Fact]
    public void ClearingCountsAsAChangeWhenOnlyAPetIsEquipped()
    {
        var equipment = new PetEquipment();
        Assert.True(equipment.RestorePet(Pet(1)));
        Assert.Equal(1, equipment.Version);
        equipment.Clear();
        Assert.Null(equipment.Pet);
        Assert.Equal(2, equipment.Version);
    }

    [Fact]
    public void ClearingCountsAsAChangeWhenOnlyAnArtifactIsEquipped()
    {
        var equipment = new PetEquipment();
        Assert.True(equipment.RestoreArtifact(EquipmentParts.ArtifactBlue, Gear(960, EquipmentParts.ArtifactBlue)));
        Assert.Equal(1, equipment.Version);
        equipment.Clear();
        Assert.Equal(0, equipment.ArtifactCountEquipped);
        Assert.Equal(2, equipment.Version);
    }

    [Theory]
    [InlineData(EquipmentParts.Creature)]
    [InlineData(EquipmentParts.Weapon)]
    [InlineData(EquipmentParts.ArtifactGreen + 1)]
    [InlineData(-1)]
    public void PlacingAnArtifactDirectlyOutsideTheArtifactSlotsDoesNothing(int part)
    {
        // 内部入口的约定:部位号不是神器格时什么也不做(不抛越界异常、版本号不动)。
        var equipment = new PetEquipment();
        equipment.SetArtifact(part, Gear(960, part));
        Assert.Equal(0, equipment.Version);
        Assert.Equal(0, equipment.ArtifactCountEquipped);
    }

    [Fact]
    public void AnArtifactQueryOutsideTheArtifactSlotsIsNull()
    {
        var equipment = new PetEquipment();
        equipment.RestoreArtifact(EquipmentParts.ArtifactRed, Gear(960, EquipmentParts.ArtifactRed));
        Assert.Null(equipment.Artifact(EquipmentParts.Creature));
        Assert.Null(equipment.Artifact(EquipmentParts.ArtifactGreen + 1));
        Assert.Null(equipment.Artifact(-1));
        Assert.NotNull(equipment.Artifact(EquipmentParts.ArtifactRed));
    }

    // ------------------------------------------------------------------ 经验加成

    private static Func<ItemInfo, PetItemData> Data(params (int Code, int MinLevel, float Rate)[] rows)
    {
        return item =>
        {
            foreach ((int code, int minLevel, float rate) in rows)
            {
                if (code == item.Code)
                {
                    return new PetItemData { MinimumLevel = minLevel, ExperienceRate = rate };
                }
            }
            return null;
        };
    }

    private static PetEquipment Worn(PetInstance pet, params (int Part, int Code)[] artifacts)
    {
        var equipment = new PetEquipment();
        if (pet != null)
        {
            equipment.RestorePet(pet);
        }
        foreach ((int part, int code) in artifacts)
        {
            Assert.True(equipment.RestoreArtifact(part, Gear(code, part)));
        }
        return equipment;
    }

    [Fact]
    public void TheBonusIsTheSumOfTheThreeSlots()
    {
        PetEquipment equipment = Worn(Pet(1), (23, 960), (24, 961), (25, 962));
        Func<ItemInfo, PetItemData> data = Data((960, 0, 10f), (961, 0, 20f), (962, 0, 30f));
        Assert.Equal(60f, equipment.ExperienceBonus(data));
    }

    [Fact]
    public void EmptySlotsAndMissingDataContributeNothing()
    {
        Assert.Equal(0f, Worn(Pet(1)).ExperienceBonus(Data()));
        PetEquipment equipment = Worn(Pet(1), (23, 960), (25, 962));
        Assert.Equal(30f, equipment.ExperienceBonus(Data((962, 0, 30f))));
        Assert.Equal(0f, equipment.ExperienceBonus(null));
        Assert.Equal(0f, equipment.ExperienceBonus(Data()));
    }

    [Fact]
    public void WithoutAPetThereIsNoBonusEvenWithArtifactsOnTheSlots()
    {
        PetEquipment equipment = Worn(null, (23, 960), (24, 961));
        Assert.Equal(0f, equipment.ExperienceBonus(Data((960, 0, 10f), (961, 0, 20f))));
    }

    [Fact]
    public void ASlotTheScriptDoesNotOpenAddsNothing()
    {
        PetEquipment equipment = Worn(Pet(1, PetArtifactColors.Red | PetArtifactColors.Green), (23, 960), (24, 961), (25, 962));
        Assert.Equal(40f, equipment.ExperienceBonus(Data((960, 0, 10f), (961, 0, 20f), (962, 0, 30f))));
    }

    [Fact]
    public void AnArtifactNeedsThePetToReachItsMinimumLevel()
    {
        // 新宠物 1 级:最低等级 0 与 1 的算,2 的不算;宠物升到 2 级(经验 10)以后"等于"也算。
        PetEquipment low = Worn(Pet(1), (23, 960), (24, 961), (25, 962));
        Func<ItemInfo, PetItemData> data = Data((960, 0, 10f), (961, 1, 20f), (962, 2, 30f));
        Assert.Equal(30f, low.ExperienceBonus(data));

        PetInstance leveled = Pet(1);
        leveled.GainExp(10, 0, 0f);
        Assert.Equal(2, leveled.Level);
        PetEquipment high = Worn(leveled, (23, 960), (24, 961), (25, 962));
        Assert.Equal(60f, high.ExperienceBonus(data));
    }

    [Fact]
    public void ABonusAffectsTheExpActuallyGained()
    {
        PetInstance pet = Pet(1);
        PetEquipment equipment = Worn(pet, (23, 960), (24, 961));
        float bonus = equipment.ExperienceBonus(Data((960, 0, 10f), (961, 0, 40f)));
        Assert.Equal(50f, bonus);
        Assert.Equal(15, pet.GainExp(10, 0, bonus).Added);
    }
}
