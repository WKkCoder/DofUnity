using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 账号金库的开通、升档与金币存取(证据:服务端 CAccountCargo::CreateAccountCargo 0x0828ad72、UpgradeAccountCargo 0x0828b0b2、DepositMoney 0x0828a12a、WithdrawMoney 0x0828a2f6、CheckMoneyLimit 0x0828a4ca)。
/// 夹具的档位表:建立要 10 级与材料 931;(容量, 金币上限, 材料, 价钱) = (8, 1000, 2, 500)、(16, 5000, 3, 1500)、(24, 20000, 0, 0)、(60, 99999, 5, 8000);等级 L 的携带上限是 L × 1000(L 为 0 到 99)。
/// </summary>
public class AccountVaultTests
{
    private static readonly ItemInfo Mat = StorageFixtures.Stack(StorageFixtures.MaterialCode, 100, StackableTypes.Material);

    private static Inventory BagWithMaterial(int count)
    {
        var bag = new Inventory();
        if (count > 0)
        {
            Assert.Equal(count, bag.Add(Mat, count));
        }
        return bag;
    }

    private static StorageSettings With(AccountCargoScript script)
    {
        return new StorageSettings { AccountScript = script, GoldLimits = StorageFixtures.Limits(), RequirePackagable = false };
    }

    // ------------------------------------------------------------------ 开通

    [Fact]
    public void CreatingTheVaultChargesTheFirstTierAndOpensItsSlots()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = BagWithMaterial(5);
        var purse = new StorageFixtures.Purse(1000);

        Assert.Equal(StorageResult.Ok, storage.CreateAccountVault(10, bag, purse));
        Assert.True(storage.HasAccountVault);
        Assert.Equal(8, storage.AccountTierCapacity);
        Assert.Equal(8, storage.AccountVault.Capacity);
        Assert.Equal(0, storage.AccountGold);
        Assert.Equal(1000, storage.AccountGoldLimit);
        Assert.Equal(500, purse.Gold);
        Assert.Equal(3, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(1, storage.Version);
    }

    [Fact]
    public void TheLevelRequirementIsInclusive()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = BagWithMaterial(2);
        var purse = new StorageFixtures.Purse(500);

        foreach (int level in new[] { 9, 0, -1, int.MinValue })
        {
            Assert.Equal(StorageResult.LevelTooLow, storage.CreateAccountVault(level, bag, purse));
        }
        Assert.False(storage.HasAccountVault);
        Assert.Equal(500, purse.Gold);
        Assert.Equal(2, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.CreateAccountVault(10, bag, purse));
        Assert.True(storage.HasAccountVault);
    }

    [Fact]
    public void MaterialAndGoldBoundariesAreInclusiveAndAFailureCostsNothing()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());

        Inventory oneMaterial = BagWithMaterial(1);
        var rich = new StorageFixtures.Purse(100000);
        Assert.Equal(StorageResult.NoMaterial, storage.CreateAccountVault(10, oneMaterial, rich));
        Assert.Equal(100000, rich.Gold);
        Assert.Equal(1, oneMaterial.CountOf(StorageFixtures.MaterialCode));

        Inventory enough = BagWithMaterial(2);
        var poor = new StorageFixtures.Purse(499);
        Assert.Equal(StorageResult.NoGold, storage.CreateAccountVault(10, enough, poor));
        Assert.Equal(499, poor.Gold);
        Assert.Equal(2, enough.CountOf(StorageFixtures.MaterialCode));

        var neither = new StorageFixtures.Purse(0);
        Assert.Equal(StorageResult.NoMaterial, storage.CreateAccountVault(10, new Inventory(), neither));       // 材料先于金币
        Assert.False(storage.HasAccountVault);
        Assert.Equal(0, storage.Version);

        var exact = new StorageFixtures.Purse(500);
        Assert.Equal(StorageResult.Ok, storage.CreateAccountVault(10, enough, exact));
        Assert.Equal(0, exact.Gold);
        Assert.Equal(0, enough.CountOf(StorageFixtures.MaterialCode));
    }

    [Fact]
    public void MaterialsMaySitInSeveralStacks()
    {
        ItemInfo single = StorageFixtures.Stack(StorageFixtures.MaterialCode, 1, StackableTypes.Material);   // 一格只能放一件
        var bag = new Inventory();
        bag.Add(single, 2);
        Assert.Equal(2, bag.Tab(InventoryTab.Material).Count);
        var storage = new AccountStorage(StorageFixtures.Settings());
        Assert.Equal(StorageResult.Ok, storage.CreateAccountVault(10, bag, new StorageFixtures.Purse(500)));
        Assert.Equal(0, bag.CountOf(StorageFixtures.MaterialCode));
    }

    [Fact]
    public void AVaultThatAlreadyExistsIsNotChargedAgain()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = BagWithMaterial(4);
        var purse = new StorageFixtures.Purse(1000);
        Assert.Equal(StorageResult.Ok, storage.CreateAccountVault(10, bag, purse));
        Assert.Equal(StorageResult.AccountVaultExists, storage.CreateAccountVault(10, bag, purse));
        Assert.Equal(StorageResult.AccountVaultExists, storage.CreateAccountVault(1, new Inventory(), new StorageFixtures.Purse(0)));   // 已有先于等级与材料
        Assert.Equal(500, purse.Gold);
        Assert.Equal(2, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(1, storage.Version);
    }

    [Fact]
    public void ALyingPurseCannotMakeTheVaultFreeAndNoMaterialIsLost()
    {
        var storage = new AccountStorage(StorageFixtures.Settings());
        Inventory bag = BagWithMaterial(2);
        var purse = new StorageFixtures.Purse(1000) { RefuseSpend = true };
        Assert.Equal(StorageResult.NoGold, storage.CreateAccountVault(10, bag, purse));
        Assert.False(storage.HasAccountVault);
        Assert.Equal(2, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(1000, purse.Gold);
        Assert.Equal(0, storage.Version);
    }

    [Fact]
    public void WithoutUsableDataTheVaultCannotBeCreated()
    {
        Inventory bag = BagWithMaterial(2);
        var purse = new StorageFixtures.Purse(1000);
        Assert.Equal(StorageResult.NoData, new AccountStorage(new StorageSettings()).CreateAccountVault(10, bag, purse));
        Assert.Equal(StorageResult.NoData, new AccountStorage(With(new AccountCargoScript(10, 931, Array.Empty<AccountCargoTier>()))).CreateAccountVault(10, bag, purse));
        Assert.Equal(StorageResult.NoData, new AccountStorage(With(new AccountCargoScript(10, 931, new[] { new AccountCargoTier(0, 100, 0, 0) }))).CreateAccountVault(10, bag, purse));
        Assert.Equal(2, bag.CountOf(931));
        Assert.Equal(1000, purse.Gold);
    }

    [Fact]
    public void AFreeFirstTierAndAMissingMaterialItemFollowTheTierNumbers()
    {
        var free = new AccountStorage(With(new AccountCargoScript(1, 0, new[] { new AccountCargoTier(8, 100, 0, 0) })));
        var purse = new StorageFixtures.Purse(0);
        Assert.Equal(StorageResult.Ok, free.CreateAccountVault(1, new Inventory(), purse));
        Assert.Equal(0, purse.Gold);
        Assert.Equal(100, free.AccountGoldLimit);

        // 档位要材料、脚本却没写材料编号:没有东西可收,按没有材料处理
        var noItem = new AccountStorage(With(new AccountCargoScript(1, 0, new[] { new AccountCargoTier(8, 100, 2, 0) })));
        Assert.Equal(StorageResult.NoMaterial, noItem.CreateAccountVault(1, BagWithMaterial(5), new StorageFixtures.Purse(0)));

        // 件数或价钱不大于 0 都不收
        var odd = new AccountStorage(With(new AccountCargoScript(1, 931, new[] { new AccountCargoTier(8, 100, -2, -50) })));
        Inventory bag = BagWithMaterial(1);
        var coins = new StorageFixtures.Purse(10);
        Assert.Equal(StorageResult.Ok, odd.CreateAccountVault(1, bag, coins));
        Assert.Equal(10, coins.Gold);
        Assert.Equal(1, bag.CountOf(931));
    }

    // ------------------------------------------------------------------ 升档

    [Fact]
    public void UpgradingMovesToTheNextTierAndChargesIt()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        Inventory bag = BagWithMaterial(4);
        var purse = new StorageFixtures.Purse(2000);

        Assert.Equal(StorageResult.Ok, storage.UpgradeAccountVault(bag, purse));
        Assert.Equal(16, storage.AccountTierCapacity);
        Assert.Equal(16, storage.AccountVault.Capacity);
        Assert.Equal(5000, storage.AccountGoldLimit);
        Assert.Equal(500, purse.Gold);
        Assert.Equal(1, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(1, storage.Version);
    }

    [Fact]
    public void UpgradeBoundariesAreInclusiveAndAFailureCostsNothing()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);

        Inventory fewMaterial = BagWithMaterial(2);
        var rich = new StorageFixtures.Purse(100000);
        Assert.Equal(StorageResult.NoMaterial, storage.UpgradeAccountVault(fewMaterial, rich));
        Assert.Equal(100000, rich.Gold);
        Assert.Equal(2, fewMaterial.CountOf(StorageFixtures.MaterialCode));

        Inventory enough = BagWithMaterial(3);
        var poor = new StorageFixtures.Purse(1499);
        Assert.Equal(StorageResult.NoGold, storage.UpgradeAccountVault(enough, poor));
        Assert.Equal(1499, poor.Gold);
        Assert.Equal(3, enough.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(8, storage.AccountTierCapacity);
        Assert.Equal(0, storage.Version);

        var exact = new StorageFixtures.Purse(1500);
        Assert.Equal(StorageResult.Ok, storage.UpgradeAccountVault(enough, exact));
        Assert.Equal(0, exact.Gold);
        Assert.Equal(0, enough.CountOf(StorageFixtures.MaterialCode));
    }

    [Fact]
    public void ALyingPurseCannotMakeAnUpgradeFreeAndNoMaterialIsLost()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        Inventory bag = BagWithMaterial(3);
        var purse = new StorageFixtures.Purse(5000) { RefuseSpend = true };
        Assert.Equal(StorageResult.NoGold, storage.UpgradeAccountVault(bag, purse));
        Assert.Equal(8, storage.AccountTierCapacity);
        Assert.Equal(3, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(0, storage.Version);
    }

    [Fact]
    public void AFreeTierCostsNothingAndTheLastTierCapsAtFiftySixSlots()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(16);
        Inventory empty = new Inventory();
        var nothing = new StorageFixtures.Purse(0);
        Assert.Equal(StorageResult.Ok, storage.UpgradeAccountVault(empty, nothing));         // 24 档不要材料不要钱
        Assert.Equal(24, storage.AccountVault.Capacity);
        Assert.Equal(20000, storage.AccountGoldLimit);

        Inventory bag = BagWithMaterial(5);
        var purse = new StorageFixtures.Purse(8000);
        Assert.Equal(StorageResult.Ok, storage.UpgradeAccountVault(bag, purse));
        Assert.Equal(60, storage.AccountTierCapacity);
        Assert.Equal(56, storage.AccountVault.Capacity);                                      // 档位写 60,格子最多 56
        Assert.Equal(99999, storage.AccountGoldLimit);
        Assert.Equal(0, purse.Gold);
        Assert.Equal(0, bag.CountOf(StorageFixtures.MaterialCode));

        Assert.Equal(StorageResult.AtMaxCapacity, storage.UpgradeAccountVault(BagWithMaterial(9), new StorageFixtures.Purse(99999)));
        Assert.Equal(60, storage.AccountTierCapacity);
        Assert.Equal(2, storage.Version);
    }

    [Fact]
    public void UpgradeNeedsAnOpenedVaultAndATierThatIsInTheTable()
    {
        Inventory bag = BagWithMaterial(9);
        var purse = new StorageFixtures.Purse(99999);
        var none = new AccountStorage(StorageFixtures.Settings());
        Assert.Equal(StorageResult.NoAccountVault, none.UpgradeAccountVault(bag, purse));

        AccountStorage unknown = StorageFixtures.WithAccountVault(30);                         // 档位表里没有 30
        Assert.Equal(StorageResult.AtMaxCapacity, unknown.UpgradeAccountVault(bag, purse));

        var noScript = new AccountStorage(new StorageSettings());
        noScript.RestoreAccount(8, 0);
        Assert.Equal(StorageResult.NoData, noScript.UpgradeAccountVault(bag, purse));
        Assert.Equal(9, bag.CountOf(StorageFixtures.MaterialCode));
        Assert.Equal(99999, purse.Gold);
    }

    // ------------------------------------------------------------------ 存金币

    [Fact]
    public void DepositMovesGoldAndTheVaultLimitIsInclusive()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        var purse = new StorageFixtures.Purse(5000);

        Assert.Equal(StorageResult.OverVaultGoldLimit, storage.DepositGold(purse, 1001));
        Assert.Equal(5000, purse.Gold);
        Assert.Equal(0, storage.AccountGold);
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.DepositGold(purse, 600));
        Assert.Equal(StorageResult.Ok, storage.DepositGold(purse, 400));                         // 600 + 400 = 1000:等于上限可以
        Assert.Equal(1000, storage.AccountGold);
        Assert.Equal(4000, purse.Gold);
        Assert.Equal(2, storage.Version);

        Assert.Equal(StorageResult.OverVaultGoldLimit, storage.DepositGold(purse, 1));
        Assert.Equal(1000, storage.AccountGold);
        Assert.Equal(4000, purse.Gold);
        Assert.Equal(2, storage.Version);
    }

    [Fact]
    public void DepositAmountChecksRunBeforeTheLimit()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        var purse = new StorageFixtures.Purse(100);

        Assert.Equal(StorageResult.BadAmount, storage.DepositGold(purse, -1));
        Assert.Equal(StorageResult.BadAmount, storage.DepositGold(purse, long.MinValue));
        Assert.Equal(StorageResult.NoGold, storage.DepositGold(purse, 101));                    // 比钱包里多先于超出金库上限
        Assert.Equal(StorageResult.NoGold, storage.DepositGold(purse, 5000));
        Assert.Equal(100, purse.Gold);
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.DepositGold(purse, 100));                        // 恰好是钱包里全部的钱
        Assert.Equal(0, purse.Gold);
        Assert.Equal(100, storage.AccountGold);
    }

    [Fact]
    public void DepositingNothingChangesNothing()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        var purse = new StorageFixtures.Purse(100);
        Assert.Equal(StorageResult.Ok, storage.DepositGold(purse, 0));
        Assert.Equal(100, purse.Gold);
        Assert.Equal(0, storage.AccountGold);
        Assert.Equal(0, storage.Version);
    }

    [Fact]
    public void ALyingPurseDoesNotCreditTheVault()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        var purse = new StorageFixtures.Purse(500) { RefuseSpend = true };
        Assert.Equal(StorageResult.NoGold, storage.DepositGold(purse, 100));
        Assert.Equal(0, storage.AccountGold);
        Assert.Equal(0, storage.Version);
    }

    [Fact]
    public void DepositNeedsAnOpenedVaultAndATierWithALimit()
    {
        var none = new AccountStorage(StorageFixtures.Settings());
        var purse = new StorageFixtures.Purse(500);
        Assert.Equal(StorageResult.NoAccountVault, none.DepositGold(purse, 1));
        Assert.Equal(StorageResult.NoAccountVault, none.DepositGold(purse, -1));                 // 没有金库先于金额检查

        AccountStorage unknown = StorageFixtures.WithAccountVault(30);                           // 档位表里没有 30:没有上限数据,一律不收
        Assert.Equal(StorageResult.OverVaultGoldLimit, unknown.DepositGold(purse, 1));
        var noScript = new AccountStorage(new StorageSettings());
        noScript.RestoreAccount(8, 0);
        Assert.Equal(StorageResult.OverVaultGoldLimit, noScript.DepositGold(purse, 1));
        Assert.Equal(500, purse.Gold);
    }

    [Fact]
    public void ADepositFollowsTheTierAfterAnUpgrade()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8);
        var purse = new StorageFixtures.Purse(100000);
        Assert.Equal(StorageResult.OverVaultGoldLimit, storage.DepositGold(purse, 1001));
        Assert.Equal(StorageResult.Ok, storage.UpgradeAccountVault(BagWithMaterial(3), new StorageFixtures.Purse(1500)));
        Assert.Equal(StorageResult.OverVaultGoldLimit, storage.DepositGold(purse, 5001));
        Assert.Equal(StorageResult.Ok, storage.DepositGold(purse, 5000));
        Assert.Equal(5000, storage.AccountGold);
    }

    [Fact]
    public void AHugeSavedBalanceCannotOverflowTheDepositCheck()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8, 1);
        var purse = new StorageFixtures.Purse(long.MaxValue);
        Assert.Equal(StorageResult.OverVaultGoldLimit, storage.DepositGold(purse, long.MaxValue));
        Assert.Equal(long.MaxValue, purse.Gold);
        Assert.Equal(1, storage.AccountGold);
    }

    // ------------------------------------------------------------------ 取金币

    [Fact]
    public void WithdrawMovesGoldToThePurse()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8, 800);
        var purse = new StorageFixtures.Purse(100);

        Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 300, 5));
        Assert.Equal(400, purse.Gold);
        Assert.Equal(500, storage.AccountGold);
        Assert.Equal(1, purse.Received);
        Assert.Equal(1, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 500, 5));                    // 恰好取光
        Assert.Equal(900, purse.Gold);
        Assert.Equal(0, storage.AccountGold);
    }

    [Fact]
    public void WithdrawAmountChecksAndTheEmptyWithdraw()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8, 800);
        var purse = new StorageFixtures.Purse(0);

        Assert.Equal(StorageResult.BadAmount, storage.WithdrawGold(purse, -1, 5));
        Assert.Equal(StorageResult.NoGold, storage.WithdrawGold(purse, 801, 5));
        Assert.Equal(StorageResult.NoGold, storage.WithdrawGold(purse, 99999, 1));               // 比金库里多先于超出角色上限
        Assert.Equal(800, storage.AccountGold);
        Assert.Equal(0, purse.Gold);
        Assert.Equal(0, storage.Version);

        Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 0, 5));
        Assert.Equal(0, purse.Received);
        Assert.Equal(800, storage.AccountGold);
        Assert.Equal(0, storage.Version);
    }

    [Fact]
    public void TheHeroGoldLimitFollowsTheLevelAndIsInclusive()
    {
        // 1 级上限 1000
        AccountStorage storage = StorageFixtures.WithAccountVault(8, 800);
        var purse = new StorageFixtures.Purse(500);
        Assert.Equal(StorageResult.OverHeroGoldLimit, storage.WithdrawGold(purse, 501, 1));
        Assert.Equal(500, purse.Gold);
        Assert.Equal(800, storage.AccountGold);
        Assert.Equal(0, storage.Version);
        Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 500, 1));                    // 500 + 500 = 1000:等于上限可以
        Assert.Equal(1000, purse.Gold);

        // 50 级上限 50000
        AccountStorage fresh = StorageFixtures.WithAccountVault(8, 800);
        var rich = new StorageFixtures.Purse(49500);
        Assert.Equal(StorageResult.OverHeroGoldLimit, fresh.WithdrawGold(rich, 501, 50));       // 49500 + 501 > 50000
        Assert.Equal(StorageResult.Ok, fresh.WithdrawGold(rich, 500, 50));
        Assert.Equal(50000, rich.Gold);
    }

    [Fact]
    public void TheHeroLevelIsClampedIntoTheTable()
    {
        // 等级 0 与负数按 1 级(上限 1000);100 级以上用表里最后一项(99000)
        foreach (int level in new[] { 0, -5 })
        {
            AccountStorage storage = StorageFixtures.WithAccountVault(8, 800);
            var purse = new StorageFixtures.Purse(500);
            Assert.Equal(StorageResult.OverHeroGoldLimit, storage.WithdrawGold(purse, 501, level));
            Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 500, level));
        }
        foreach (int level in new[] { 100, 150, 250, int.MaxValue })
        {
            AccountStorage storage = StorageFixtures.WithAccountVault(8, 800);
            var purse = new StorageFixtures.Purse(98300);
            Assert.Equal(StorageResult.OverHeroGoldLimit, storage.WithdrawGold(purse, 701, level));       // 98300 + 701 > 99000
            Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 700, level));
            Assert.Equal(99000, purse.Gold);
        }
    }

    [Fact]
    public void WithoutALimitTableTheHeroGoldIsNotChecked()
    {
        StorageSettings settings = StorageFixtures.Settings();
        settings.GoldLimits = null;
        AccountStorage storage = StorageFixtures.WithAccountVault(8, 800, settings);
        var purse = new StorageFixtures.Purse(5_000_000_000L);
        Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 800, 1));
        Assert.Equal(5_000_000_800L, purse.Gold);
    }

    [Fact]
    public void WithdrawNeedsAnOpenedVault()
    {
        var none = new AccountStorage(StorageFixtures.Settings());
        var purse = new StorageFixtures.Purse(0);
        Assert.Equal(StorageResult.NoAccountVault, none.WithdrawGold(purse, 1, 10));
        Assert.Equal(StorageResult.NoAccountVault, none.WithdrawGold(purse, -1, 10));
        Assert.Equal(0, none.Version);
    }

    [Fact]
    public void AHugeSavedBalanceCannotOverflowTheWithdrawCheck()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(8, long.MaxValue);
        var purse = new StorageFixtures.Purse(1);
        Assert.Equal(StorageResult.OverHeroGoldLimit, storage.WithdrawGold(purse, long.MaxValue, 10));
        Assert.Equal(1, purse.Gold);
        Assert.Equal(long.MaxValue, storage.AccountGold);
    }

    [Fact]
    public void GoldDepositedComesBackUnchangedWhenWithdrawn()
    {
        AccountStorage storage = StorageFixtures.WithAccountVault(16);
        var purse = new StorageFixtures.Purse(3000);
        Assert.Equal(StorageResult.Ok, storage.DepositGold(purse, 2500));
        Assert.Equal(500, purse.Gold);
        Assert.Equal(StorageResult.Ok, storage.WithdrawGold(purse, 2500, 40));
        Assert.Equal(3000, purse.Gold);
        Assert.Equal(0, storage.AccountGold);
    }
}
