using Dnf.Game;
using static Dnf.Play.Tests.StorageKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 仓库会话(本项目自己的设计,没有原版证据;规则本身的证据在 Game 层):主角的背包、金币、等级接到账号级的仓库数据上,快捷栏随背包对齐,
/// 快照与还原。主角用 ExpTests 的合成等级进度,物品编号 900 起,数值手算。
/// </summary>
public class StorageSessionTests
{
    // 把 911 号药水(能进快捷栏)当作扣材料/扩容道具用的设置:这样"做完之后快捷栏随背包对齐"能被看见。
    private static StorageSettings PotionIsTheMaterial(bool asExpansionItem = false)
    {
        return new StorageSettings
        {
            AccountScript = new AccountCargoScript(10, 911, new[]
            {
                new AccountCargoTier(8, 1000, 2, 0),
                new AccountCargoTier(16, 5000, 2, 0),
            }),
            GoldLimits = Limits(),
            RequirePackagable = false,
            Facts = info => asExpansionItem && info.Code == 911 ? new StorageItemFacts(false, false, true) : StorageItemFacts.None,
        };
    }

    // ------------------------------------------------------------------ 构造

    [Fact]
    public void TheSessionNeedsAHeroAKeyAndStorage()
    {
        HeroProgress hero = ExpTests.Progress(level: 5);
        var storage = new AccountStorage(Settings());
        Assert.Throws<ArgumentNullException>(() => new StorageSession(null, HeroKey, storage));
        Assert.Throws<ArgumentNullException>(() => new StorageSession(null, "has space", storage));      // 主角为空先于键不合法被拒
        Assert.Throws<ArgumentNullException>(() => new StorageSession(hero, HeroKey, null));
        Assert.Throws<ArgumentException>(() => new StorageSession(hero, "has space", storage));
        Assert.Throws<ArgumentException>(() => new StorageSession(hero, null, storage));
        Assert.Throws<ArgumentException>(() => new StorageSession(hero, "", storage));

        var session = new StorageSession(hero, "wanderer", storage);
        Assert.Same(hero, session.Hero);
        Assert.Same(storage, session.Storage);
        Assert.Equal("wanderer", session.HeroKey);
        Assert.Same(storage.PersonalVault("wanderer"), session.Personal);
        Assert.Same(session.Personal, session.VaultOf(StorageKind.Personal));
        Assert.Same(storage.AccountVault, session.VaultOf(StorageKind.Account));
    }

    [Fact]
    public void TheHeroPurseMirrorsTheHeroGold()
    {
        HeroProgress hero = ExpTests.Progress(level: 5);
        hero.AddGold(300);
        var purse = new HeroPurse(hero);
        Assert.Equal(300, purse.Gold);
        Assert.False(purse.TrySpend(0));
        Assert.False(purse.TrySpend(-1));
        Assert.False(purse.TrySpend(301));
        Assert.Equal(300, hero.Gold);
        Assert.True(purse.TrySpend(300));
        Assert.Equal(0, hero.Gold);
        purse.Receive(0);
        Assert.Equal(0, hero.Gold);
        purse.Receive(50);
        Assert.Equal(50, purse.Gold);
        Assert.Throws<ArgumentNullException>(() => new HeroPurse(null));
    }

    // ------------------------------------------------------------------ 存取与快捷栏

    [Fact]
    public void StoreAndTakeMoveItemsBetweenTheHeroBagAndTheVault()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Gear(900));
        session.Hero.AddItem(Potion(911), 50);
        var start = Dump(session.Hero.Bag);

        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out int gearSlot));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Consumable, 0, 20, 5, out int potionSlot));
        Assert.Equal(0, gearSlot);
        Assert.Equal(5, potionSlot);
        Assert.Equal(30, session.Hero.Bag.CountOf(911));
        Assert.Equal(20, session.Personal.CountOf(911));
        Assert.Equal(2, session.Storage.Version);

        Assert.Equal(StorageResult.Ok, session.Take(StorageKind.Personal, gearSlot, 1));
        Assert.Equal(StorageResult.Ok, session.Take(StorageKind.Personal, potionSlot, 20));
        Assert.Equal(start, Dump(session.Hero.Bag));
        Assert.Equal(0, session.Personal.UsedSlots);
    }

    [Fact]
    public void AFailedStoreOrTakeLeavesTheHeroAlone()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Potion(911), 5);
        var start = Dump(session.Hero.Bag);
        Assert.Equal(StorageResult.BadQuantity, session.Store(StorageKind.Personal, InventoryTab.Consumable, 0, 6, -1, out int slot));
        Assert.Equal(-1, slot);
        Assert.Equal(StorageResult.NoAccountVault, session.Store(StorageKind.Account, InventoryTab.Consumable, 0, 1, -1, out _));
        Assert.Equal(StorageResult.NoItem, session.Take(StorageKind.Personal, 0, 1));
        Assert.Equal(StorageResult.BadSlot, session.Take(StorageKind.Personal, 99, 1));
        Assert.Equal(start, Dump(session.Hero.Bag));
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(0, session.Storage.Version);
    }

    [Fact]
    public void ThePotionsOnTheQuickBarFollowTheBag()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Potion(911), 30);
        session.Hero.AddItem(Potion(912), 10);
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(912, session.Hero.QuickCodeAt(1));

        // 只存走一部分:药水还在背包里,快捷栏不动
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Consumable, 0, 10, -1, out _));
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(20, session.Hero.QuickCountAt(0));

        // 全存走:那一格腾空,别的格不动
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Consumable, 0, 20, -1, out _));
        Assert.Equal(0, session.Hero.QuickCodeAt(0));
        Assert.Equal(912, session.Hero.QuickCodeAt(1));

        // 取回来:药水自动进第一个空格
        Assert.Equal(StorageResult.Ok, session.Take(StorageKind.Personal, 0, 30));
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(30, session.Hero.QuickCountAt(0));
        Assert.Equal(2, session.Hero.QuickFilled);
    }

    [Fact]
    public void EveryQuickSlotFollowsTheBagIncludingTheLastOne()
    {
        StorageSession session = Session();
        for (int i = 0; i < QuickSlots.Count; i++)
        {
            Assert.True(session.Hero.AddItem(Potion(920 + i), 5));
        }
        Assert.Equal(QuickSlots.Count, session.Hero.QuickFilled);
        Assert.Equal(925, session.Hero.QuickCodeAt(QuickSlots.Count - 1));

        int lastPotion = -1;
        var cells = session.Hero.Bag.Tab(InventoryTab.Consumable);
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].Info.Code == 925)
            {
                lastPotion = i;
            }
        }
        Assert.True(lastPotion >= 0);
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Consumable, lastPotion, 5, -1, out _));
        Assert.Equal(0, session.Hero.QuickCodeAt(QuickSlots.Count - 1));
        Assert.Equal(QuickSlots.Count - 1, session.Hero.QuickFilled);
        Assert.Equal(920, session.Hero.QuickCodeAt(0));
    }

    [Fact]
    public void TakingBackNonPotionsDoesNotTouchTheQuickBar()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Gear(900));
        session.Hero.AddItem(Stack(913, 100, StackableTypes.Material), 5);
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out _));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Material, 0, 5, -1, out _));
        Assert.Equal(StorageResult.Ok, session.Take(StorageKind.Personal, 0, 1));
        Assert.Equal(StorageResult.Ok, session.Take(StorageKind.Personal, 1, 5));
        Assert.Equal(0, session.Hero.QuickFilled);
    }

    [Fact]
    public void ASwapGoesThroughTheSession()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Gear(900));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out _));
        Assert.Equal(StorageResult.Ok, session.Swap(StorageKind.Personal, 0, 6));
        Assert.Null(session.Personal.SlotAt(0));
        Assert.Equal(900, session.Personal.SlotAt(6).Info.Code);
        Assert.Equal(StorageResult.NoItem, session.Swap(StorageKind.Personal, 0, 1));
        Assert.Equal(StorageResult.NoAccountVault, session.Swap(StorageKind.Account, 0, 1));
    }

    [Fact]
    public void TwoHeroesShareTheAccountVaultButNotThePersonalOne()
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(8, 0);
        StorageSession first = Session(storage: storage, heroKey: "first");
        StorageSession second = Session(storage: storage, heroKey: "second");

        first.Hero.AddItem(Gear(900));
        first.Hero.AddItem(Gear(901));
        Assert.Equal(StorageResult.Ok, first.Store(StorageKind.Account, InventoryTab.Equipment, 0, 1, -1, out int accountSlot));
        Assert.Equal(StorageResult.Ok, first.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out int personalSlot));

        Assert.Equal(StorageResult.Ok, second.Take(StorageKind.Account, accountSlot, 1));
        Assert.Equal(1, second.Hero.Bag.CountOf(900));
        Assert.Equal(StorageResult.NoItem, second.Take(StorageKind.Personal, personalSlot, 1));
        Assert.Equal(StorageResult.Ok, first.Take(StorageKind.Personal, personalSlot, 1));
        Assert.Equal(new[] { "first", "second" }, storage.PersonalKeys.ToArray());
    }

    // ------------------------------------------------------------------ 开通、升级、扩容

    [Fact]
    public void CreatingTheVaultUsesTheHeroLevelBagAndGold()
    {
        StorageSession young = Session(level: 9, gold: 500);
        young.Hero.AddItem(Material(), 2);
        Assert.Equal(StorageResult.LevelTooLow, young.CreateAccountVault());
        Assert.Equal(500, young.Hero.Gold);
        Assert.Equal(2, young.Hero.Bag.CountOf(MaterialCode));

        StorageSession session = Session(level: 10, gold: 700);
        session.Hero.AddItem(Material(), 3);
        Assert.Equal(StorageResult.Ok, session.CreateAccountVault());
        Assert.True(session.Storage.HasAccountVault);
        Assert.Equal(200, session.Hero.Gold);
        Assert.Equal(1, session.Hero.Bag.CountOf(MaterialCode));
        Assert.Equal(StorageResult.AccountVaultExists, session.CreateAccountVault());
    }

    [Fact]
    public void UpgradingTheVaultChargesTheNextTier()
    {
        StorageSession session = Session(level: 10, gold: 2000);
        session.Hero.AddItem(Material(), 5);
        Assert.Equal(StorageResult.NoAccountVault, session.UpgradeAccountVault());
        Assert.Equal(StorageResult.Ok, session.CreateAccountVault());                 // 材料 2、金币 500
        Assert.Equal(StorageResult.Ok, session.UpgradeAccountVault());                // 材料 3、金币 1500
        Assert.Equal(16, session.Storage.AccountTierCapacity);
        Assert.Equal(0, session.Hero.Gold);
        Assert.Equal(0, session.Hero.Bag.CountOf(MaterialCode));
        Assert.Equal(StorageResult.Ok, session.UpgradeAccountVault());                // 24 档不要材料不要钱
        Assert.Equal(24, session.Storage.AccountTierCapacity);
        Assert.Equal(StorageResult.NoMaterial, session.UpgradeAccountVault());        // 60 档要 5 件材料
    }

    [Fact]
    public void PayingForTheVaultWithAPotionClearsItsQuickSlot()
    {
        StorageSession session = Session(level: 12, storage: new AccountStorage(PotionIsTheMaterial()));
        session.Hero.AddItem(Potion(911), 2);
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(StorageResult.Ok, session.CreateAccountVault());
        Assert.Equal(0, session.Hero.QuickCodeAt(0));

        session.Hero.AddItem(Potion(911), 2);
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(StorageResult.Ok, session.UpgradeAccountVault());
        Assert.Equal(0, session.Hero.QuickCodeAt(0));
        Assert.Equal(16, session.Storage.AccountTierCapacity);
    }

    [Fact]
    public void ExpandingThePersonalVaultSpendsOneExpansionItem()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Pass(), 2);
        Assert.Equal(StorageResult.Ok, session.ExpandPersonal(InventoryTab.Consumable, 0));
        Assert.Equal(24, session.Personal.Capacity);
        Assert.Equal(1, session.Hero.Bag.CountOf(PassCode));
        Assert.Equal(StorageResult.NoItem, Session().ExpandPersonal(InventoryTab.Consumable, 0));
    }

    [Fact]
    public void ExpandingWithAPotionClearsItsQuickSlot()
    {
        StorageSession session = Session(storage: new AccountStorage(PotionIsTheMaterial(asExpansionItem: true)));
        session.Hero.AddItem(Potion(911), 1);
        Assert.Equal(911, session.Hero.QuickCodeAt(0));
        Assert.Equal(StorageResult.Ok, session.ExpandPersonal(InventoryTab.Consumable, 0));
        Assert.Equal(0, session.Hero.QuickCodeAt(0));
        Assert.Equal(24, session.Personal.Capacity);

        session.Hero.AddItem(Potion(912), 1);
        Assert.Equal(StorageResult.NotExpansionItem, session.ExpandPersonal(InventoryTab.Consumable, 0));
        Assert.Equal(912, session.Hero.QuickCodeAt(0));
    }

    // ------------------------------------------------------------------ 金币

    [Fact]
    public void DepositAndWithdrawMoveTheHeroGold()
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(8, 0);
        StorageSession session = Session(level: 12, gold: 5000, storage: storage);

        Assert.Equal(StorageResult.Ok, session.DepositGold(600));
        Assert.Equal(4400, session.Hero.Gold);
        Assert.Equal(600, storage.AccountGold);
        Assert.Equal(StorageResult.OverVaultGoldLimit, session.DepositGold(401));
        Assert.Equal(StorageResult.Ok, session.WithdrawGold(250));
        Assert.Equal(4650, session.Hero.Gold);
        Assert.Equal(350, storage.AccountGold);
    }

    [Fact]
    public void WithdrawFollowsTheHeroLevelLimit()
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(8, 800);
        StorageSession session = Session(level: 5, gold: 4800, storage: storage);          // 5 级上限 5000
        Assert.Equal(StorageResult.OverHeroGoldLimit, session.WithdrawGold(201));
        Assert.Equal(4800, session.Hero.Gold);
        Assert.Equal(StorageResult.Ok, session.WithdrawGold(200));
        Assert.Equal(5000, session.Hero.Gold);
        Assert.Equal(600, storage.AccountGold);
    }

    [Fact]
    public void WithdrawnGoldIsCountedAsGainedUntilTheHeroCanReceiveWithoutCounting()
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(8, 500);
        StorageSession session = Session(level: 12, gold: 100, storage: storage);
        Assert.Equal(100, session.Hero.GoldGained);
        Assert.Equal(StorageResult.Ok, session.WithdrawGold(500));
        Assert.Equal(600, session.Hero.Gold);
        Assert.Equal(600, session.Hero.GoldGained);                    // 已知的小毛病:取出来的钱也算进累计进账,见交接文档
    }

    // ------------------------------------------------------------------ 快照

    [Fact]
    public void ALookDoesNotChangeTheSave()
    {
        StorageSession session = Session();
        Assert.Equal("DNFUNITY-STORAGE 1\n", session.Snapshot().ToText());
        Assert.Equal(StorageRules.PersonalInitialCapacity, session.Personal.Capacity);           // 打开过个人仓库
        Assert.Equal("DNFUNITY-STORAGE 1\n", session.Snapshot().ToText());
        Assert.Equal(0, session.Snapshot().AccountCapacity);
    }

    [Fact]
    public void TheSnapshotHoldsTheVaultsInASetOrder()
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(16, 345);
        StorageSession session = Session(storage: storage, heroKey: "zed");
        session.Hero.AddItem(Gear(900));
        session.Hero.AddItem(Gear(901));
        session.Hero.AddItem(Potion(911), 40);
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Account, InventoryTab.Equipment, 0, 1, 9, out _));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Account, InventoryTab.Equipment, 0, 1, 2, out _));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Consumable, 0, 40, 7, out _));
        storage.RestorePersonal("amy", 16).RestoreSlot(3, Gear(902), 1);
        storage.RestorePersonal("idle", 8);                                        // 初始容量又没有东西:不写
        storage.RestorePersonal("roomy", 24);                                      // 只扩了容量:要写

        const string expected =
            "DNFUNITY-STORAGE 1\n" +
            "acap 16\n" +
            "agold 345\n" +
            "aslot 2 901 1\n" +
            "aslot 9 900 1\n" +
            "pcap amy 16\n" +
            "pslot amy 3 902 1\n" +
            "pcap roomy 24\n" +
            "pcap zed 8\n" +
            "pslot zed 7 911 40\n";
        Assert.Equal(expected, session.Snapshot().ToText());
    }

    // ------------------------------------------------------------------ 还原

    [Fact]
    public void ASnapshotRestoresIntoAnotherSessionExactly()
    {
        StorageSession a = Session(level: 20, gold: 10000);
        a.Hero.AddItem(Material(), 5);
        Assert.Equal(StorageResult.Ok, a.CreateAccountVault());
        a.Hero.AddItem(Gear(900));
        a.Hero.AddItem(Gear(901));
        Assert.Equal(StorageResult.Ok, a.Store(StorageKind.Account, InventoryTab.Equipment, 0, 1, 3, out _));
        Assert.Equal(StorageResult.Ok, a.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out _));
        Assert.Equal(StorageResult.Ok, a.DepositGold(700));
        a.Hero.AddItem(Pass(), 1);
        Assert.Equal(StorageResult.Ok, a.ExpandPersonal(InventoryTab.Consumable, 0));
        a.Hero.AddItem(Potion(911), 40);
        Assert.Equal(StorageResult.Ok, a.Store(StorageKind.Personal, InventoryTab.Consumable, 0, 40, 10, out _));

        string text = a.Snapshot().ToText();
        Assert.True(StorageSave.TryParse(text, out StorageSave parsed, out string error), error);

        StorageSession b = Session(level: 20);
        AccountStorage before = b.Storage;
        StorageApplyReport report = b.Restore(parsed, Lookup);
        Assert.Equal(1, report.AccountSlots);
        Assert.Equal(2, report.PersonalSlots);
        Assert.Equal(0, report.Dropped);
        Assert.NotSame(before, b.Storage);
        Assert.Same(before.Settings, b.Storage.Settings);
        Assert.Equal(text, b.Snapshot().ToText());
        Assert.True(b.Storage.HasAccountVault);
        Assert.Equal(8, b.Storage.AccountTierCapacity);
        Assert.Equal(700, b.Storage.AccountGold);
        Assert.Equal(1000, b.Storage.AccountGoldLimit);
        Assert.Equal(24, b.Personal.Capacity);
        Assert.Equal(900, b.Storage.AccountVault.SlotAt(3).Info.Code);
        Assert.Equal(40, b.Personal.SlotAt(10).Count);
        Assert.Equal(0, b.Storage.Version);
    }

    [Fact]
    public void WhatCannotBePlacedIsDroppedAndCounted()
    {
        var save = new StorageSave { AccountCapacity = 8 };
        save.AccountSlots.Add(new SavedVaultSlot(0, 900, 1));          // 放得下
        save.AccountSlots.Add(new SavedVaultSlot(1, 999, 1));          // 数据里没有这个编号
        save.AccountSlots.Add(new SavedVaultSlot(0, 901, 1));          // 格号重复
        save.AccountSlots.Add(new SavedVaultSlot(8, 902, 1));          // 格子没开放
        save.AccountSlots.Add(new SavedVaultSlot(2, 911, 101));        // 超出一格上限
        var mine = new SavedPersonalVault(HeroKey, 8);
        mine.Slots.Add(new SavedVaultSlot(0, 900, 2));                 // 装备一格只能一件
        mine.Slots.Add(new SavedVaultSlot(1, 911, 100));               // 恰好一格上限:放得下
        save.Personal.Add(mine);
        var bad = new SavedPersonalVault("bad key", 8);                // 手工拼出来的存档里角色键不合法
        bad.Slots.Add(new SavedVaultSlot(0, 900, 1));
        bad.Slots.Add(new SavedVaultSlot(1, 901, 1));
        save.Personal.Add(bad);

        StorageSession session = Session();
        StorageApplyReport report = session.Restore(save, Lookup);
        Assert.Equal(1, report.AccountSlots);
        Assert.Equal(1, report.PersonalSlots);
        Assert.Equal(7, report.Dropped);
        Assert.Equal(900, session.Storage.AccountVault.SlotAt(0).Info.Code);
        Assert.Equal(1, session.Storage.AccountVault.UsedSlots);
        Assert.Equal(100, session.Personal.SlotAt(1).Count);
        Assert.Equal(new[] { HeroKey }, session.Storage.PersonalKeys.ToArray());
    }

    [Fact]
    public void SlotsOfAnAccountVaultThatIsNotOpenAreDropped()
    {
        var save = new StorageSave { AccountCapacity = 0, AccountGold = 50 };
        save.AccountSlots.Add(new SavedVaultSlot(0, 900, 1));
        save.AccountSlots.Add(new SavedVaultSlot(1, 901, 1));
        StorageSession session = Session();
        StorageApplyReport report = session.Restore(save, Lookup);
        Assert.Equal(0, report.AccountSlots);
        Assert.Equal(2, report.Dropped);
        Assert.False(session.Storage.HasAccountVault);
        Assert.Equal(0, session.Storage.AccountGold);
    }

    [Fact]
    public void RestoreKeepsAnAccountVaultWhoseTierIsNotInTheTableAndItsGold()
    {
        var save = new StorageSave { AccountCapacity = 30, AccountGold = 5 };      // 档位表里没有 30(数据换过)
        save.AccountSlots.Add(new SavedVaultSlot(29, 900, 1));
        StorageSession session = Session();
        StorageApplyReport report = session.Restore(save, Lookup);
        Assert.Equal(1, report.AccountSlots);
        Assert.Equal(30, session.Storage.AccountVault.Capacity);
        Assert.Equal(5, session.Storage.AccountGold);
        Assert.Equal(0, session.Storage.AccountGoldLimit);
    }

    [Fact]
    public void RestoreRefusesMissingArguments()
    {
        StorageSession session = Session();
        Assert.Throws<ArgumentNullException>(() => session.Restore(null, Lookup));
        Assert.Throws<ArgumentNullException>(() => session.Restore(new StorageSave(), null));
    }

    [Fact]
    public void RestoreReplacesWhatWasThereBefore()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Gear(900));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out _));
        session.Restore(new StorageSave(), Lookup);
        Assert.Equal(0, session.Personal.UsedSlots);
        Assert.Equal("DNFUNITY-STORAGE 1\n", session.Snapshot().ToText());
    }
}
