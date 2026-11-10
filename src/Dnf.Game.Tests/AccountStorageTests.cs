using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 背包与仓库之间存取物品、互换、个人仓库扩容、读档还原(证据:服务端 CUser::exchange_interspace 0x0865f7b0、Dispatcher_UpgradeCargo 0x0822a0a4、CCargo::isUpgradable 0x0850c698)。
/// 个人仓库与账号金库的存取规则相同,所以大部分用例对两种仓库各跑一遍;每个失败用例都核对"背包、仓库、改动次数原样"。
/// </summary>
public class AccountStorageTests
{
    private const string Hero = "hero1";
    private static readonly ItemInfo Potion = StorageFixtures.Stack(911, 100);
    private static readonly ItemInfo Gear = StorageFixtures.Equip(900);

    private static AccountStorage Storage(StorageKind kind, StorageSettings settings = null)
    {
        var storage = new AccountStorage(settings ?? StorageFixtures.Settings());
        if (kind == StorageKind.Account)
        {
            storage.RestoreAccount(8, 0);
        }
        return storage;
    }

    private static StorageVault VaultOf(AccountStorage storage, StorageKind kind)
    {
        return kind == StorageKind.Account ? storage.AccountVault : storage.PersonalVault(Hero);
    }

    private static Inventory Bag(params (ItemInfo Info, int Count)[] items)
    {
        var bag = new Inventory();
        foreach ((ItemInfo info, int count) in items)
        {
            Assert.Equal(count, bag.Add(info, count));
        }
        return bag;
    }

    // ------------------------------------------------------------------ 存与取

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void StoreThenTakeBringsEverythingBack(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Inventory bag = Bag((Potion, 50), (Gear, 1));
        var start = StorageFixtures.Dump(bag);

        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 50, -1, out int potionSlot));
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, -1, out int gearSlot));
        Assert.Equal(0, potionSlot);
        Assert.Equal(1, gearSlot);
        Assert.Equal(0, bag.TotalCount);
        Assert.Equal(50, vault.CountOf(911));
        Assert.Equal(1, vault.CountOf(900));
        Assert.Equal(2, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, potionSlot, 50, bag));
        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, gearSlot, 1, bag));
        Assert.Equal(0, vault.TotalCount);
        Assert.Equal(0, vault.UsedSlots);
        Assert.Equal(start, StorageFixtures.Dump(bag));
        Assert.Equal(4, storage.Version);
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void PartialMovesKeepTheTotalAndMergeIntoOneStack(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Inventory bag = Bag((Potion, 50));

        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 20, -1, out int first));
        Assert.Equal(0, first);
        Assert.Equal(30, bag.CountOf(911));
        Assert.Equal(20, vault.CountOf(911));

        // 想放第 7 格,但仓库里已经有这个编号的一叠 → 并进去
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 10, 7, out int second));
        Assert.Equal(0, second);
        Assert.Equal(30, vault.SlotAt(0).Count);
        Assert.Equal(1, vault.UsedSlots);
        Assert.Equal(20, bag.CountOf(911));

        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, 0, 5, bag));
        Assert.Equal(25, vault.CountOf(911));
        Assert.Equal(25, bag.CountOf(911));
        Assert.Equal(50, vault.CountOf(911) + bag.CountOf(911));
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void StoreQuantityBoundaries(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Inventory bag = Bag((Potion, 5));
        var bagBefore = StorageFixtures.Dump(bag);
        int version = storage.Version;

        foreach (int count in new[] { 0, -1, 6, int.MinValue, int.MaxValue })
        {
            Assert.Equal(StorageResult.BadQuantity, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, count, -1, out int slot));
            Assert.Equal(-1, slot);
        }
        Assert.Equal(bagBefore, StorageFixtures.Dump(bag));
        Assert.Equal(0, vault.UsedSlots);
        Assert.Equal(version, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 1, -1, out _));
        Assert.Equal(4, bag.CountOf(911));
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 4, -1, out _));      // 恰好是这一格剩下的全部
        Assert.Equal(0, bag.TotalCount);
        Assert.Equal(5, vault.CountOf(911));
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void StoringAnEquipmentNeedsExactlyOne(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        Inventory bag = Bag((Gear, 1));
        Assert.Equal(StorageResult.BadQuantity, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 2, -1, out _));
        Assert.Equal(StorageResult.BadQuantity, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 0, -1, out _));
        Assert.Equal(1, bag.TotalCount);
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, -1, out _));
        Assert.Equal(0, bag.TotalCount);
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void StoreFromAMissingBagCellIsNoItem(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        Inventory bag = Bag((Potion, 5));
        foreach (int index in new[] { -1, 1, 55, int.MaxValue })
        {
            Assert.Equal(StorageResult.NoItem, storage.Store(kind, Hero, bag, InventoryTab.Consumable, index, 1, -1, out int slot));
            Assert.Equal(-1, slot);
        }
        Assert.Equal(StorageResult.NoItem, storage.Store(kind, Hero, bag, InventoryTab.Quest, 0, 1, -1, out _));
        Assert.Equal(5, bag.TotalCount);
        Assert.Equal(0, storage.Version);
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void TheChosenSlotIsHonouredWhenItIsEmptyAndOpen(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        Inventory bag = Bag((Gear, 1), (StorageFixtures.Equip(901), 1), (StorageFixtures.Equip(902), 1));
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, 5, out int a));
        Assert.Equal(5, a);
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, 5, out int b));      // 5 已经有东西 → 第一个空格
        Assert.Equal(0, b);
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, 8, out int c));      // 8 没开放 → 第一个空格
        Assert.Equal(1, c);
        Assert.Equal(900, VaultOf(storage, kind).SlotAt(5).Info.Code);
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void AFullVaultRejectsANewItemWithoutLosingAnything(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        for (int i = 0; i < 8; i++)
        {
            Assert.True(vault.RestoreSlot(i, StorageFixtures.Equip(940 + i), 1));
        }
        Inventory bag = Bag((Gear, 1), (Potion, 10));
        var bagBefore = StorageFixtures.Dump(bag);
        var vaultBefore = StorageFixtures.Dump(vault);

        Assert.Equal(StorageResult.VaultFull, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, -1, out int slot));
        Assert.Equal(-1, slot);
        Assert.Equal(StorageResult.VaultFull, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 10, 3, out _));
        Assert.Equal(StorageResult.VaultFull, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 1, -1, out _));
        Assert.Equal(bagBefore, StorageFixtures.Dump(bag));
        Assert.Equal(vaultBefore, StorageFixtures.Dump(vault));
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, 3, 1, bag));            // 取走一件腾出一格,背包里多一件
        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Equipment, 0, 1, -1, out slot));
        Assert.Equal(3, slot);
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void AStackThatWouldOverflowIsRefusedAndTheExactFitIsAccepted(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Assert.True(vault.RestoreSlot(0, Potion, 95));
        Inventory bag = Bag((Potion, 10));
        var bagBefore = StorageFixtures.Dump(bag);

        Assert.Equal(StorageResult.VaultFull, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 6, -1, out _));       // 95 + 6 = 101,还有空格也不拆
        Assert.Equal(bagBefore, StorageFixtures.Dump(bag));
        Assert.Equal(95, vault.SlotAt(0).Count);
        Assert.Equal(1, vault.UsedSlots);
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 5, -1, out int slot));       // 95 + 5 = 100
        Assert.Equal(0, slot);
        Assert.Equal(100, vault.SlotAt(0).Count);
        Assert.Equal(5, bag.CountOf(911));

        Assert.Equal(StorageResult.VaultFull, storage.Store(kind, Hero, bag, InventoryTab.Consumable, 0, 1, -1, out _));       // 100 + 1
        Assert.Equal(5, bag.CountOf(911));
        Assert.Equal(1, storage.Version);
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void AFullBagRejectsATakeWithoutLosingAnything(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Assert.True(vault.RestoreSlot(0, Gear, 1));
        Assert.True(vault.RestoreSlot(1, Potion, 60));
        var bag = new Inventory(2);                                         // 每页只有两格
        Assert.Equal(1, bag.Add(StorageFixtures.Equip(950)));
        Assert.Equal(1, bag.Add(StorageFixtures.Equip(951)));               // 装备页满
        Assert.Equal(95, bag.Add(Potion, 95));
        Assert.Equal(1, bag.Add(StorageFixtures.Stack(912), 1));            // 消耗品页满,药水那格还能再叠 5 件
        var bagBefore = StorageFixtures.Dump(bag);
        var vaultBefore = StorageFixtures.Dump(vault);

        Assert.Equal(StorageResult.BagFull, storage.Take(kind, Hero, 0, 1, bag));
        Assert.Equal(StorageResult.BagFull, storage.Take(kind, Hero, 1, 6, bag));          // 只放得下 5 件
        Assert.Equal(bagBefore, StorageFixtures.Dump(bag));
        Assert.Equal(vaultBefore, StorageFixtures.Dump(vault));
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, 1, 5, bag));               // 恰好放满
        Assert.Equal(100, bag.CountOf(911));
        Assert.Equal(55, vault.CountOf(911));
        Assert.Equal(StorageResult.BagFull, storage.Take(kind, Hero, 1, 1, bag));          // 那一格已经满了
        Assert.Equal(55, vault.CountOf(911));

        bag.RemoveAt(InventoryTab.Equipment, 0, 1);                                         // 腾出一格
        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, 0, 1, bag));
        Assert.Equal(1, bag.CountOf(900));
        Assert.Null(vault.SlotAt(0));
    }

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void TakeChecksTheSlotThenTheQuantity(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Assert.True(vault.RestoreSlot(1, Potion, 40));
        var bag = new Inventory();
        var vaultBefore = StorageFixtures.Dump(vault);

        Assert.Equal(StorageResult.BadSlot, storage.Take(kind, Hero, -1, 1, bag));
        Assert.Equal(StorageResult.BadSlot, storage.Take(kind, Hero, 8, 1, bag));
        Assert.Equal(StorageResult.NoItem, storage.Take(kind, Hero, 0, 1, bag));
        Assert.Equal(StorageResult.BadQuantity, storage.Take(kind, Hero, 1, 0, bag));
        Assert.Equal(StorageResult.BadQuantity, storage.Take(kind, Hero, 1, -1, bag));
        Assert.Equal(StorageResult.BadQuantity, storage.Take(kind, Hero, 1, 41, bag));
        Assert.Equal(vaultBefore, StorageFixtures.Dump(vault));
        Assert.Equal(0, bag.TotalCount);
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Take(kind, Hero, 1, 40, bag));
        Assert.Null(vault.SlotAt(1));
        Assert.Equal(40, bag.CountOf(911));
    }

    [Fact]
    public void TheQuantityIsCheckedBeforeRoomIsLookedFor()
    {
        AccountStorage storage = Storage(StorageKind.Personal);
        StorageVault vault = storage.PersonalVault(Hero);
        for (int i = 0; i < 8; i++)
        {
            Assert.True(vault.RestoreSlot(i, StorageFixtures.Equip(940 + i), 1));
        }
        Inventory bag = Bag((Gear, 1));
        Assert.Equal(StorageResult.BadQuantity, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Equipment, 0, 0, -1, out _));
        var full = new Inventory(1);
        full.Add(StorageFixtures.Equip(950));
        Assert.Equal(StorageResult.BadQuantity, storage.Take(StorageKind.Personal, Hero, 0, 0, full));
        Assert.Equal(StorageResult.BagFull, storage.Take(StorageKind.Personal, Hero, 0, 1, full));
    }

    // ------------------------------------------------------------------ 资格

    [Fact]
    public void PersonalStoreRefusesWhatTheRulesForbidAndLeavesTheBagAlone()
    {
        ItemInfo noCargo = StorageFixtures.Equip(900);
        ItemInfo quest = StorageFixtures.Stack(970, 100, StackableTypes.Quest);
        ItemInfo creature = StorageFixtures.Equip(971, EquipmentParts.Creature);
        AccountStorage storage = Storage(StorageKind.Personal, StorageFixtures.Settings(new Dictionary<int, StorageItemFacts>
        {
            [900] = new StorageItemFacts(true, true, false),
        }));
        Inventory bag = Bag((noCargo, 1), (quest, 3), (creature, 1), (Potion, 2));
        var before = StorageFixtures.Dump(bag);

        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Equipment, 0, 1, -1, out int slot));
        Assert.Equal(-1, slot);
        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Quest, 0, 3, -1, out _));
        Assert.Equal(before, StorageFixtures.Dump(bag));
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Equipment, 1, 1, -1, out _));          // 宠物装备个人仓库收
        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Consumable, 0, 2, -1, out _));
    }

    [Fact]
    public void AccountStoreRefusesUnpackagableAndSpecialCategoriesAndLeavesTheBagAlone()
    {
        ItemInfo packed = StorageFixtures.Equip(900);
        ItemInfo quest = StorageFixtures.Stack(970, 100, StackableTypes.Quest);
        ItemInfo creature = StorageFixtures.Equip(971, EquipmentParts.Creature);
        ItemInfo tradable = StorageFixtures.Equip(972, EquipmentParts.Coat, "[trade]");
        var facts = new Dictionary<int, StorageItemFacts>
        {
            [900] = new StorageItemFacts(true, false, false),
            [970] = new StorageItemFacts(true, false, false),
            [971] = new StorageItemFacts(true, false, false),
            [972] = new StorageItemFacts(true, false, false),
        };
        AccountStorage storage = Storage(StorageKind.Account, StorageFixtures.Settings(facts, requirePackagable: true));
        Inventory bag = Bag((packed, 1), (quest, 3), (creature, 1), (tradable, 1), (Potion, 2));
        var before = StorageFixtures.Dump(bag);

        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Account, null, bag, InventoryTab.Quest, 0, 3, -1, out _));       // 类别 4
        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Account, null, bag, InventoryTab.Equipment, 1, 1, -1, out _));   // 类别 5
        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Account, null, bag, InventoryTab.Equipment, 2, 1, -1, out _));   // [trade]
        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Account, null, bag, InventoryTab.Consumable, 0, 2, -1, out _));  // 没有 [packagable]
        Assert.Equal(before, StorageFixtures.Dump(bag));
        Assert.Equal(0, storage.AccountVault.UsedSlots);

        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Account, null, bag, InventoryTab.Equipment, 0, 1, -1, out int slot));
        Assert.Equal(0, slot);
    }

    [Fact]
    public void TheChecksRunInTheDocumentedOrder()
    {
        var facts = new Dictionary<int, StorageItemFacts> { [900] = new StorageItemFacts(true, true, false) };
        AccountStorage storage = Storage(StorageKind.Personal, StorageFixtures.Settings(facts));
        Inventory bag = Bag((Gear, 1));

        // 没有账号金库先于一切
        Assert.Equal(StorageResult.NoAccountVault, storage.Store(StorageKind.Account, null, new Inventory(), InventoryTab.Consumable, 5, 0, -1, out _));
        // 背包格不存在先于资格
        Assert.Equal(StorageResult.NoItem, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Equipment, 3, 0, -1, out _));
        // 资格先于件数
        Assert.Equal(StorageResult.NotAllowed, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Equipment, 0, 0, -1, out _));
    }

    [Fact]
    public void AccountOperationsNeedAnOpenedVaultButPersonalOnesDoNot()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = Bag((Potion, 5));
        var before = StorageFixtures.Dump(bag);

        Assert.Equal(StorageResult.NoAccountVault, storage.Store(StorageKind.Account, null, bag, InventoryTab.Consumable, 0, 5, -1, out int slot));
        Assert.Equal(-1, slot);
        Assert.Equal(StorageResult.NoAccountVault, storage.Take(StorageKind.Account, null, 0, 1, bag));
        Assert.Equal(StorageResult.NoAccountVault, storage.Swap(StorageKind.Account, null, 0, 1));
        Assert.Equal(before, StorageFixtures.Dump(bag));
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Consumable, 0, 5, -1, out _));
    }

    // ------------------------------------------------------------------ 互换

    [Theory]
    [InlineData(StorageKind.Personal)]
    [InlineData(StorageKind.Account)]
    public void SwapMovesAStackAndOnlyCountsWhenSomethingChanged(StorageKind kind)
    {
        AccountStorage storage = Storage(kind);
        StorageVault vault = VaultOf(storage, kind);
        Assert.True(vault.RestoreSlot(1, Potion, 40));

        Assert.Equal(StorageResult.Ok, storage.Swap(kind, Hero, 1, 4));
        Assert.Null(vault.SlotAt(1));
        Assert.Equal(40, vault.SlotAt(4).Count);
        Assert.Equal(1, storage.Version);

        Assert.Equal(StorageResult.NoItem, storage.Swap(kind, Hero, 0, 3));
        Assert.Equal(StorageResult.BadSlot, storage.Swap(kind, Hero, 4, 8));
        Assert.Equal(StorageResult.BadSlot, storage.Swap(kind, Hero, -1, 4));
        Assert.Equal(1, storage.Version);
        Assert.Equal(40, vault.SlotAt(4).Count);
    }

    // ------------------------------------------------------------------ 个人仓库按角色分开

    [Fact]
    public void EachHeroHasItsOwnPersonalVault()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Assert.Empty(storage.PersonalKeys);
        Inventory bag = Bag((Potion, 10));

        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Personal, "b", bag, InventoryTab.Consumable, 0, 5, -1, out _));
        Assert.Equal(5, storage.PersonalVault("b").CountOf(911));
        Assert.Equal(0, storage.PersonalVault("a").CountOf(911));
        Assert.Equal(StorageRules.PersonalInitialCapacity, storage.PersonalVault("a").Capacity);
        storage.PersonalVault("Z");
        Assert.Equal(new[] { "Z", "a", "b" }, storage.PersonalKeys);
        Assert.Same(storage.PersonalVault("b"), storage.PersonalVault("b"));
        Assert.NotSame(storage.PersonalVault("a"), storage.PersonalVault("b"));
    }

    [Fact]
    public void HeroKeysAreComparedExactlySoKeysThatDifferOnlyInCaseAreDifferentHeroes()
    {
        // 角色键是宿主起的名字,逐字比较(与存档里按键分组的写法一致);大小写不同的是两个角色,不能合到同一个仓库里。
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = Bag((Potion, 10));

        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Personal, "Hero", bag, InventoryTab.Consumable, 0, 4, -1, out _));
        Assert.Equal(4, storage.PersonalVault("Hero").CountOf(911));
        Assert.Equal(0, storage.PersonalVault("hero").CountOf(911));
        Assert.NotSame(storage.PersonalVault("Hero"), storage.PersonalVault("hero"));
        Assert.Equal(new[] { "Hero", "hero" }, storage.PersonalKeys);
    }

    [Theory]
    [InlineData("hero1", true)]
    [InlineData("a", true)]
    [InlineData("A-b_c.d:é", true)]
    [InlineData("a#b", true)]
    [InlineData("#a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("tab\there", false)]
    [InlineData("line\nbreak", false)]
    [InlineData(" lead", false)]
    [InlineData("trail ", false)]
    public void HeroKeysMustBeSingleTokens(string key, bool valid)
    {
        Assert.Equal(valid, AccountStorage.IsValidHeroKey(key));
    }

    [Fact]
    public void HeroKeyLengthIsLimitedAndABadKeyIsRefusedByEveryEntryPoint()
    {
        Assert.True(AccountStorage.IsValidHeroKey(new string('a', AccountStorage.MaxHeroKeyLength)));
        Assert.False(AccountStorage.IsValidHeroKey(new string('a', AccountStorage.MaxHeroKeyLength + 1)));
        Assert.Equal(64, AccountStorage.MaxHeroKeyLength);

        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = Bag((Potion, 5));
        Assert.Throws<ArgumentException>(() => storage.PersonalVault("bad key"));
        Assert.Throws<ArgumentException>(() => storage.PersonalVault(null));
        Assert.Throws<ArgumentException>(() => storage.Store(StorageKind.Personal, "", bag, InventoryTab.Consumable, 0, 1, -1, out _));
        Assert.Throws<ArgumentException>(() => storage.Take(StorageKind.Personal, "#x", 0, 1, bag));
        Assert.Throws<ArgumentException>(() => storage.Swap(StorageKind.Personal, " ", 0, 1));
        Assert.Throws<ArgumentException>(() => storage.ExpandPersonal("a b", bag, InventoryTab.Consumable, 0));
        Assert.Throws<ArgumentException>(() => storage.RestorePersonal("", 8));
        Assert.Empty(storage.PersonalKeys);
    }

    [Fact]
    public void TheAccountVaultIgnoresTheHeroKey()
    {
        AccountStorage storage = Storage(StorageKind.Account);
        Inventory bag = Bag((Potion, 5));
        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Account, null, bag, InventoryTab.Consumable, 0, 5, -1, out _));
        Assert.Equal(StorageResult.Ok, storage.Take(StorageKind.Account, "anything at all", 0, 5, bag));
        Assert.Empty(storage.PersonalKeys);
    }

    // ------------------------------------------------------------------ 个人仓库扩容

    private static readonly ItemInfo Pass = StorageFixtures.Stack(960, 100);

    private static StorageSettings Expandable()
    {
        return StorageFixtures.Settings(new Dictionary<int, StorageItemFacts>
        {
            [960] = new StorageItemFacts(false, false, true),
            [961] = new StorageItemFacts(false, false, true),
        });
    }

    [Fact]
    public void NineExpansionsReachTheMaximumAndTheTenthKeepsTheItem()
    {
        var storage = new AccountStorage(Expandable());
        Inventory bag = Bag((Pass, 12));
        for (int i = 1; i <= 9; i++)
        {
            int version = storage.Version;
            Assert.Equal(StorageResult.Ok, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 0));
            Assert.Equal(8 + 16 * i, storage.PersonalVault(Hero).Capacity);
            Assert.Equal(12 - i, bag.CountOf(960));
            Assert.Equal(version + 1, storage.Version);
        }
        Assert.Equal(152, storage.PersonalVault(Hero).Capacity);

        Assert.Equal(StorageResult.AtMaxCapacity, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 0));
        Assert.Equal(3, bag.CountOf(960));
        Assert.Equal(152, storage.PersonalVault(Hero).Capacity);
        Assert.Equal(9, storage.Version);
    }

    [Fact]
    public void TheExpansionBoundaryIsTheLastStepThatStillFits()
    {
        var storage = new AccountStorage(Expandable());
        Inventory bag = Bag((Pass, 3));
        storage.RestorePersonal("a", 136);
        storage.RestorePersonal("b", 137);
        storage.RestorePersonal("c", 144);

        Assert.Equal(StorageResult.Ok, storage.ExpandPersonal("a", bag, InventoryTab.Consumable, 0));
        Assert.Equal(152, storage.PersonalVault("a").Capacity);
        Assert.Equal(StorageResult.AtMaxCapacity, storage.ExpandPersonal("b", bag, InventoryTab.Consumable, 0));
        Assert.Equal(137, storage.PersonalVault("b").Capacity);
        Assert.Equal(StorageResult.AtMaxCapacity, storage.ExpandPersonal("c", bag, InventoryTab.Consumable, 0));
        Assert.Equal(144, storage.PersonalVault("c").Capacity);
        Assert.Equal(2, bag.CountOf(960));
    }

    [Fact]
    public void OnlyAnExpansionItemExpandsAndTheChecksRunInOrder()
    {
        ItemInfo gearWithTheFlag = StorageFixtures.Equip(961);
        var storage = new AccountStorage(Expandable());
        Inventory bag = Bag((Potion, 2), (gearWithTheFlag, 1), (Pass, 1));
        var before = StorageFixtures.Dump(bag);

        Assert.Equal(StorageResult.NotExpansionItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 0));       // 药水
        Assert.Equal(StorageResult.NotExpansionItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Equipment, 0));        // 装备不算,哪怕脚本写了
        Assert.Equal(StorageResult.NoItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 2));
        Assert.Equal(StorageResult.NoItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, -1));
        Assert.Equal(StorageResult.NoItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Quest, 0));
        Assert.Equal(before, StorageFixtures.Dump(bag));
        Assert.Equal(8, storage.PersonalVault(Hero).Capacity);
        Assert.Equal(0, storage.Version);

        // 已到上限时,不是扩容道具还是先报不是道具
        storage.RestorePersonal(Hero, 152);
        Assert.Equal(StorageResult.NotExpansionItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 0));
        Assert.Equal(StorageResult.AtMaxCapacity, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 1));
        Assert.Equal(before, StorageFixtures.Dump(bag));
    }

    [Fact]
    public void TheLastExpansionItemLeavesTheBagAndTheNewSlotsWork()
    {
        var storage = new AccountStorage(Expandable());
        Inventory bag = Bag((Pass, 1), (Gear, 1));
        Assert.Equal(StorageResult.Ok, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 0));
        Assert.Empty(bag.Tab(InventoryTab.Consumable));
        Assert.Equal(StorageResult.NoItem, storage.ExpandPersonal(Hero, bag, InventoryTab.Consumable, 0));

        // 新开的格子(8 到 23)马上能用
        Assert.Equal(StorageResult.Ok, storage.Store(StorageKind.Personal, Hero, bag, InventoryTab.Equipment, 0, 1, 20, out int slot));
        Assert.Equal(20, slot);
    }

    // ------------------------------------------------------------------ 读档还原

    [Fact]
    public void RestoreAccountSetsTheTierGoldAndCapacityWithoutCharging()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        storage.RestoreAccount(24, 777);
        Assert.True(storage.HasAccountVault);
        Assert.Equal(24, storage.AccountTierCapacity);
        Assert.Equal(777, storage.AccountGold);
        Assert.Equal(24, storage.AccountVault.Capacity);
        Assert.Equal(20000, storage.AccountGoldLimit);
        Assert.Equal(0, storage.Version);

        var none = new AccountStorage(StorageFixtures.Settings());
        none.RestoreAccount(0, 500);
        Assert.False(none.HasAccountVault);
        Assert.Equal(0, none.AccountGold);
        Assert.Equal(0, none.AccountVault.Capacity);
        Assert.Equal(0, none.AccountGoldLimit);
        none.RestoreAccount(-3, 500);
        Assert.False(none.HasAccountVault);
        Assert.Equal(0, none.AccountTierCapacity);        // 负容量归零,不是原样留着一个负数
        Assert.Equal(0, none.AccountGold);

        var negative = new AccountStorage(StorageFixtures.Settings());
        negative.RestoreAccount(8, -5);
        Assert.True(negative.HasAccountVault);
        Assert.Equal(0, negative.AccountGold);
    }

    [Fact]
    public void RestoreAccountKeepsTheTierNumberButCapsTheSlotsAndKeepsUnknownTiers()
    {
        var last = new AccountStorage(StorageFixtures.Settings());
        last.RestoreAccount(60, 1);
        Assert.Equal(60, last.AccountTierCapacity);
        Assert.Equal(56, last.AccountVault.Capacity);
        Assert.Equal(99999, last.AccountGoldLimit);

        var unknown = new AccountStorage(StorageFixtures.Settings());
        unknown.RestoreAccount(30, 5);                       // 档位表里没有 30(数据换过)
        Assert.True(unknown.HasAccountVault);
        Assert.Equal(30, unknown.AccountVault.Capacity);
        Assert.Equal(0, unknown.AccountGoldLimit);

        var noScript = new AccountStorage(new StorageSettings());
        noScript.RestoreAccount(8, 5);
        Assert.Equal(0, noScript.AccountGoldLimit);
        Assert.Equal(5, noScript.AccountGold);
    }

    [Fact]
    public void RestorePersonalOnlyGrowsAndNeverBeyondTheMaximum()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Assert.Equal(8, storage.RestorePersonal("a", 4).Capacity);
        Assert.Equal(8, storage.RestorePersonal("a", 8).Capacity);
        Assert.Equal(40, storage.RestorePersonal("a", 40).Capacity);
        Assert.Equal(40, storage.RestorePersonal("a", 24).Capacity);
        Assert.Equal(152, storage.RestorePersonal("b", 200).Capacity);
        Assert.Same(storage.PersonalVault("a"), storage.RestorePersonal("a", 8));
        Assert.Equal(0, storage.Version);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = Bag((Potion, 5));
        var purse = new StorageFixtures.Purse(100);

        Assert.Throws<ArgumentNullException>(() => storage.Store(StorageKind.Personal, Hero, null, InventoryTab.Consumable, 0, 1, -1, out _));
        Assert.Throws<ArgumentNullException>(() => storage.Take(StorageKind.Personal, Hero, 0, 1, null));
        Assert.Throws<ArgumentNullException>(() => storage.ExpandPersonal(Hero, null, InventoryTab.Consumable, 0));
        Assert.Throws<ArgumentNullException>(() => storage.CreateAccountVault(10, null, purse));
        Assert.Throws<ArgumentNullException>(() => storage.CreateAccountVault(10, bag, null));
        Assert.Throws<ArgumentNullException>(() => storage.UpgradeAccountVault(null, purse));
        Assert.Throws<ArgumentNullException>(() => storage.UpgradeAccountVault(bag, null));
        Assert.Throws<ArgumentNullException>(() => storage.DepositGold(null, 1));
        Assert.Throws<ArgumentNullException>(() => storage.WithdrawGold(null, 1, 10));
        Assert.NotNull(new AccountStorage(null).Settings);
    }
}
