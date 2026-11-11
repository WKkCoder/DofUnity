using Dnf.Game;
using static Dnf.Play.Tests.StorageKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 仓库窗口的数据(本项目自己的设计,只给数据不画图):每一格的开放与内容、两边的金币与上限、下一步(开通、升级、扩容)要花什么、够不够。
/// 数值见 <see cref="StorageKit"/> 的说明(建立要 10 级与 2 件材料 931、500 金币;下一档 16 格要 3 件、1500 金币;24 格免费;60 档封顶 56 格)。
/// </summary>
public class StorageWindowTests
{
    private static StorageSession WithVault(int tier, int level = 12, long gold = 0)
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(tier, 0);
        return Session(level, gold, storage);
    }

    // ------------------------------------------------------------------ 账号金库

    [Fact]
    public void AnUnopenedAccountVaultOffersToCreateIt()
    {
        StorageSession session = Session(level: 12, gold: 40);
        StorageWindowData window = StorageWindow.Build(session, StorageKind.Account);

        Assert.Equal(StorageKind.Account, window.Kind);
        Assert.False(window.HasVault);
        Assert.Equal(0, window.Capacity);
        Assert.Equal(56, window.MaxSlots);
        Assert.Equal(56, window.Slots.Count);
        Assert.All(window.Slots, s => Assert.False(s.IsOpen));
        Assert.All(window.Slots, s => Assert.True(s.IsEmpty));
        Assert.Equal(0, window.VaultGold);
        Assert.Equal(0, window.VaultGoldLimit);
        Assert.Equal(40, window.HeroGold);
        Assert.Equal(12000, window.HeroGoldLimit);

        StorageStep step = window.Step;
        Assert.Equal(StorageStepKind.CreateAccount, step.Kind);
        Assert.Equal(8, step.NewCapacity);
        Assert.Equal(8, step.NewSlots);
        Assert.Equal(1000, step.NewGoldLimit);
        Assert.Equal(10, step.RequiredLevel);
        Assert.Equal(MaterialCode, step.MaterialItem);
        Assert.Equal(2, step.MaterialCount);
        Assert.Equal(500, step.PriceGold);
        Assert.True(step.MeetsLevel);
        Assert.False(step.HasMaterial);
        Assert.False(step.HasGold);
        Assert.False(step.CanAfford);
    }

    [Fact]
    public void TheCreateStepFlagsFollowTheHeroAtEveryBoundary()
    {
        StorageSession level9 = Session(level: 9, gold: 500);
        level9.Hero.AddItem(Material(), 2);
        StorageStep low = StorageWindow.Build(level9, StorageKind.Account).Step;
        Assert.False(low.MeetsLevel);
        Assert.True(low.HasMaterial);
        Assert.True(low.HasGold);
        Assert.False(low.CanAfford);

        StorageSession oneMaterial = Session(level: 10, gold: 500);
        oneMaterial.Hero.AddItem(Material(), 1);
        StorageStep fewMaterial = StorageWindow.Build(oneMaterial, StorageKind.Account).Step;
        Assert.True(fewMaterial.MeetsLevel);
        Assert.False(fewMaterial.HasMaterial);
        Assert.True(fewMaterial.HasGold);
        Assert.False(fewMaterial.CanAfford);

        StorageSession poor = Session(level: 10, gold: 499);
        poor.Hero.AddItem(Material(), 2);
        StorageStep noGold = StorageWindow.Build(poor, StorageKind.Account).Step;
        Assert.True(noGold.HasMaterial);
        Assert.False(noGold.HasGold);
        Assert.False(noGold.CanAfford);

        StorageSession exact = Session(level: 10, gold: 500);
        exact.Hero.AddItem(Material(), 2);
        StorageStep ready = StorageWindow.Build(exact, StorageKind.Account).Step;
        Assert.True(ready.MeetsLevel);
        Assert.True(ready.HasMaterial);
        Assert.True(ready.HasGold);
        Assert.True(ready.CanAfford);
        Assert.Equal(StorageResult.Ok, exact.CreateAccountVault());                 // 窗口说够,真做也成
    }

    [Fact]
    public void AnOpenedAccountVaultShowsItsSlotsGoldAndTheNextTier()
    {
        StorageSession session = WithVault(8, level: 12, gold: 3000);
        session.Hero.AddItem(Gear(900));
        session.Hero.AddItem(Potion(911), 40);
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Account, InventoryTab.Equipment, 0, 1, 2, out _));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Account, InventoryTab.Consumable, 0, 40, 7, out _));
        Assert.Equal(StorageResult.Ok, session.DepositGold(600));
        StorageWindowData window = StorageWindow.Build(session, StorageKind.Account);

        Assert.True(window.HasVault);
        Assert.Equal(8, window.Capacity);
        Assert.Equal(56, window.Slots.Count);
        for (int i = 0; i < 56; i++)
        {
            Assert.Equal(i, window.Slots[i].Slot);
            Assert.Equal(i < 8, window.Slots[i].IsOpen);
        }
        Assert.Equal(900, window.Slots[2].Info.Code);
        Assert.Equal(1, window.Slots[2].Count);
        Assert.Equal(911, window.Slots[7].Info.Code);
        Assert.Equal(40, window.Slots[7].Count);
        Assert.True(window.Slots[0].IsEmpty);
        Assert.Equal(0, window.Slots[0].Count);
        Assert.False(window.Slots[2].IsEmpty);
        Assert.Equal(600, window.VaultGold);
        Assert.Equal(1000, window.VaultGoldLimit);
        Assert.Equal(2400, window.HeroGold);
        Assert.Equal(session.Storage.Version, window.Version);

        StorageStep step = window.Step;
        Assert.Equal(StorageStepKind.UpgradeAccount, step.Kind);
        Assert.Equal(16, step.NewCapacity);
        Assert.Equal(16, step.NewSlots);
        Assert.Equal(5000, step.NewGoldLimit);
        Assert.Equal(0, step.RequiredLevel);
        Assert.Equal(MaterialCode, step.MaterialItem);
        Assert.Equal(3, step.MaterialCount);
        Assert.Equal(1500, step.PriceGold);
        Assert.True(step.MeetsLevel);
        Assert.False(step.HasMaterial);
        Assert.True(step.HasGold);
        Assert.False(step.CanAfford);
    }

    [Fact]
    public void AClosedSlotNeverShowsAnItem()
    {
        StorageSession session = WithVault(8);
        StorageWindowData window = StorageWindow.Build(session, StorageKind.Account);
        StorageSlotView closed = window.Slots[8];
        Assert.False(closed.IsOpen);
        Assert.Null(closed.Info);
        Assert.Equal(0, closed.Count);
        Assert.True(closed.IsEmpty);
    }

    [Fact]
    public void AFreeTierIsAlwaysAffordableAndTheLastTierIsCappedAtFiftySixSlots()
    {
        StorageWindowData free = StorageWindow.Build(WithVault(16, gold: 0), StorageKind.Account);
        Assert.Equal(24, free.Step.NewCapacity);
        Assert.Equal(0, free.Step.MaterialCount);
        Assert.Equal(0, free.Step.PriceGold);
        Assert.True(free.Step.HasMaterial);
        Assert.True(free.Step.HasGold);
        Assert.True(free.Step.CanAfford);

        StorageWindowData last = StorageWindow.Build(WithVault(24, gold: 8000), StorageKind.Account);
        Assert.Equal(StorageStepKind.UpgradeAccount, last.Step.Kind);
        Assert.Equal(60, last.Step.NewCapacity);
        Assert.Equal(56, last.Step.NewSlots);                            // 档位写 60,格子最多 56
        Assert.Equal(99999, last.Step.NewGoldLimit);
        Assert.Equal(5, last.Step.MaterialCount);
        Assert.Equal(8000, last.Step.PriceGold);
        Assert.True(last.Step.HasGold);
        Assert.False(last.Step.HasMaterial);
        Assert.Equal(20000, last.VaultGoldLimit);

        StorageWindowData top = StorageWindow.Build(WithVault(60), StorageKind.Account);
        Assert.Null(top.Step);
        Assert.Equal(56, top.Capacity);
        Assert.Equal(99999, top.VaultGoldLimit);
    }

    [Fact]
    public void WithoutAnAccountScriptThereIsNoStepAndNoLimit()
    {
        var storage = new AccountStorage(new StorageSettings());
        StorageSession session = Session(level: 12, gold: 0, storage);
        StorageWindowData window = StorageWindow.Build(session, StorageKind.Account);
        Assert.False(window.HasVault);
        Assert.Null(window.Step);
        Assert.Equal(0, window.HeroGoldLimit);

        storage.RestoreAccount(8, 5);
        StorageWindowData opened = StorageWindow.Build(session, StorageKind.Account);
        Assert.True(opened.HasVault);
        Assert.Null(opened.Step);
        Assert.Equal(0, opened.VaultGoldLimit);
        Assert.Equal(5, opened.VaultGold);
    }

    [Fact]
    public void ATierThatIsNotInTheTableHasNoNextStep()
    {
        StorageWindowData window = StorageWindow.Build(WithVault(30), StorageKind.Account);
        Assert.True(window.HasVault);
        Assert.Equal(30, window.Capacity);
        Assert.Null(window.Step);
        Assert.Equal(0, window.VaultGoldLimit);
    }

    // ------------------------------------------------------------------ 个人仓库

    [Fact]
    public void ThePersonalWindowOffersTheNextExpansion()
    {
        StorageSession session = Session(level: 12, gold: 123);
        session.Hero.AddItem(Gear(900));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, 4, out _));
        StorageWindowData window = StorageWindow.Build(session, StorageKind.Personal);

        Assert.Equal(StorageKind.Personal, window.Kind);
        Assert.True(window.HasVault);
        Assert.Equal(8, window.Capacity);
        Assert.Equal(152, window.MaxSlots);
        Assert.Equal(152, window.Slots.Count);
        Assert.Equal(900, window.Slots[4].Info.Code);
        Assert.True(window.Slots[7].IsOpen);
        Assert.False(window.Slots[8].IsOpen);
        Assert.Equal(0, window.VaultGold);
        Assert.Equal(0, window.VaultGoldLimit);
        Assert.Equal(123, window.HeroGold);

        StorageStep step = window.Step;
        Assert.Equal(StorageStepKind.ExpandPersonal, step.Kind);
        Assert.Equal(24, step.NewCapacity);
        Assert.Equal(24, step.NewSlots);
        Assert.Equal(0, step.NewGoldLimit);
        Assert.Equal(0, step.RequiredLevel);
        Assert.Equal(0, step.MaterialItem);
        Assert.Equal(1, step.MaterialCount);
        Assert.Equal(0, step.PriceGold);
        Assert.True(step.MeetsLevel);
        Assert.False(step.HasMaterial);
        Assert.True(step.HasGold);
        Assert.False(step.CanAfford);
    }

    [Fact]
    public void ThePersonalWindowNeverShowsTheAccountVaultGold()
    {
        var storage = new AccountStorage(Settings());
        storage.RestoreAccount(8, 700);
        StorageSession session = Session(level: 12, gold: 5, storage);

        StorageWindowData personal = StorageWindow.Build(session, StorageKind.Personal);
        Assert.True(personal.HasVault);
        Assert.Equal(0, personal.VaultGold);
        Assert.Equal(0, personal.VaultGoldLimit);
        Assert.Equal(StorageStepKind.ExpandPersonal, personal.Step.Kind);

        StorageWindowData account = StorageWindow.Build(session, StorageKind.Account);
        Assert.Equal(700, account.VaultGold);
        Assert.Equal(1000, account.VaultGoldLimit);
        Assert.Equal(StorageStepKind.UpgradeAccount, account.Step.Kind);
    }

    [Fact]
    public void TheExpansionStepSeesAnExpansionItemOnAnyBagPage()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Potion(911), 5);
        Assert.False(StorageWindow.Build(session, StorageKind.Personal).Step.HasMaterial);
        session.Hero.AddItem(Pass(), 1);
        StorageStep step = StorageWindow.Build(session, StorageKind.Personal).Step;
        Assert.True(step.HasMaterial);
        Assert.True(step.CanAfford);
        Assert.Equal(StorageResult.Ok, session.ExpandPersonal(InventoryTab.Consumable, 1));        // 窗口说有,真用也成
        Assert.False(StorageWindow.Build(session, StorageKind.Personal).Step.HasMaterial);
    }

    [Fact]
    public void AGearThatCarriesTheFlagIsNotAnExpansionItem()
    {
        var storage = new AccountStorage(new StorageSettings { Facts = info => new StorageItemFacts(false, false, true) });
        StorageSession session = Session(storage: storage);
        session.Hero.AddItem(Gear(900));
        Assert.False(StorageWindow.Build(session, StorageKind.Personal).Step.HasMaterial);
        session.Hero.AddItem(Stack(913, 100, StackableTypes.Material), 1);
        Assert.True(StorageWindow.Build(session, StorageKind.Personal).Step.HasMaterial);
    }

    [Fact]
    public void ThereIsNoExpansionStepWhenTheNextStepWouldNotFit()
    {
        StorageSession last = Session();
        last.Storage.RestorePersonal(HeroKey, 136);
        StorageStep fits = StorageWindow.Build(last, StorageKind.Personal).Step;
        Assert.Equal(152, fits.NewCapacity);
        Assert.Equal(152, fits.NewSlots);

        StorageSession over = Session();
        over.Storage.RestorePersonal(HeroKey, 137);
        Assert.Null(StorageWindow.Build(over, StorageKind.Personal).Step);

        StorageSession max = Session();
        max.Storage.RestorePersonal(HeroKey, 152);
        Assert.Null(StorageWindow.Build(max, StorageKind.Personal).Step);
    }

    // ------------------------------------------------------------------ 其他

    [Fact]
    public void TheHeroGoldLimitIsTheOneOfTheCurrentLevelAndZeroWithoutATable()
    {
        Assert.Equal(1000, StorageWindow.Build(Session(level: 1), StorageKind.Personal).HeroGoldLimit);
        Assert.Equal(50000, StorageWindow.Build(Session(level: 50), StorageKind.Personal).HeroGoldLimit);
        Assert.Equal(60000, StorageWindow.Build(Session(level: 60), StorageKind.Account).HeroGoldLimit);

        StorageSettings noTable = Settings();
        noTable.GoldLimits = null;
        Assert.Equal(0, StorageWindow.Build(Session(storage: new AccountStorage(noTable)), StorageKind.Account).HeroGoldLimit);
    }

    [Fact]
    public void AWindowIsAPhotographAndTheVersionTellsWhenToTakeANewOne()
    {
        StorageSession session = Session();
        session.Hero.AddItem(Gear(900));
        StorageWindowData before = StorageWindow.Build(session, StorageKind.Personal);
        Assert.Equal(0, before.Version);
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Equipment, 0, 1, -1, out _));
        Assert.True(before.Slots[0].IsEmpty);                           // 旧照片不变
        StorageWindowData after = StorageWindow.Build(session, StorageKind.Personal);
        Assert.Equal(1, after.Version);
        Assert.False(after.Slots[0].IsEmpty);
    }

    [Fact]
    public void TheWindowNeedsASession()
    {
        Assert.Throws<ArgumentNullException>(() => StorageWindow.Build(null, StorageKind.Personal));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void AffordingNeedsLevelMaterialAndGoldAllTogether(bool level, bool material, bool gold, bool expected)
    {
        var step = new StorageStep(StorageStepKind.CreateAccount, 8, 8, 1000, 10, 931, 2, 500, level, material, gold);
        Assert.Equal(expected, step.CanAfford);
    }
}
