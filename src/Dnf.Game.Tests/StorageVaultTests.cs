using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 仓库格子的规则(证据:服务端 CCargo::IsOperatorable 0x0850d39c、get_empty_slot 0x0850c406、check_item_exist 0x0850bc14、_tryAddStackItem 0x0850b4b0 / _AddStackItem 0x0850c546、
/// insert_item_special_slot 0x0850b672、delete_item 0x0850b794、move_item 0x0850b904,账号金库的 GetSpecificItemSlot 0x0828a61a / CheckStackLimit 0x0828a670;
/// CUser::exchange_interspace 0x0865f7b0 的选格次序)。物品全是合成的;叠加上限用 100,期望值是手算。
/// </summary>
public class StorageVaultTests
{
    private static StorageVault Vault(int capacity = 8) => new(StorageKind.Personal, capacity);

    private static void Fill(StorageVault vault, params (int Slot, ItemInfo Info, int Count)[] cells)
    {
        foreach ((int slot, ItemInfo info, int count) in cells)
        {
            Assert.True(vault.RestoreSlot(slot, info, count));
        }
    }

    // ------------------------------------------------------------------ 容量与开放的格子

    [Fact]
    public void CapacityIsClampedToTheKindsMaximum()
    {
        Assert.Equal(0, new StorageVault(StorageKind.Personal, -3).Capacity);
        Assert.Equal(8, new StorageVault(StorageKind.Personal, 8).Capacity);
        Assert.Equal(152, new StorageVault(StorageKind.Personal, 152).Capacity);
        Assert.Equal(152, new StorageVault(StorageKind.Personal, 500).Capacity);
        Assert.Equal(56, new StorageVault(StorageKind.Account, 60).Capacity);
        Assert.Equal(152, new StorageVault(StorageKind.Personal, 8).MaxSlots);
        Assert.Equal(56, new StorageVault(StorageKind.Account, 8).MaxSlots);
        Assert.Equal(StorageKind.Account, new StorageVault(StorageKind.Account, 8).Kind);
    }

    [Fact]
    public void OnlyTheFirstCapacitySlotsAreOpen()
    {
        StorageVault vault = Vault(8);
        Assert.True(vault.IsOpen(0));
        Assert.True(vault.IsOpen(7));
        Assert.False(vault.IsOpen(8));
        Assert.False(vault.IsOpen(-1));
        Assert.False(vault.IsOpen(152));
        Fill(vault, (7, StorageFixtures.Equip(900), 1));
        Assert.NotNull(vault.SlotAt(7));
        Assert.Null(vault.SlotAt(8));
        Assert.Null(vault.SlotAt(-1));
        Assert.Null(vault.SlotAt(3));
    }

    [Fact]
    public void CountersFollowTheContents()
    {
        StorageVault vault = Vault(4);
        Assert.Equal(0, vault.UsedSlots);
        Assert.Equal(4, vault.FreeSlots);
        Assert.Equal(0, vault.TotalCount);
        Assert.Equal(0, vault.FirstEmptySlot());
        Assert.Equal(-1, vault.FindStack(911));
        Fill(vault, (0, StorageFixtures.Stack(911), 30), (1, StorageFixtures.Equip(900), 1), (3, StorageFixtures.Stack(911), 5));
        Assert.Equal(3, vault.UsedSlots);
        Assert.Equal(1, vault.FreeSlots);
        Assert.Equal(36, vault.TotalCount);
        Assert.Equal(35, vault.CountOf(911));
        Assert.Equal(1, vault.CountOf(900));
        Assert.Equal(0, vault.CountOf(999));
        Assert.Equal(0, vault.FindStack(911));
        Assert.Equal(1, vault.FindStack(900));
        Assert.Equal(2, vault.FirstEmptySlot());
        Fill(vault, (2, StorageFixtures.Equip(901), 1));
        Assert.Equal(-1, vault.FirstEmptySlot());
        Assert.Equal(0, vault.FreeSlots);
    }

    // ------------------------------------------------------------------ 存入

    [Fact]
    public void PutRejectsABadQuantityBeforeLookingForRoom()
    {
        StorageVault vault = Vault();
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        Assert.Equal(StorageResult.NoItem, vault.PlanPut(null, 1, -1).Result);
        Assert.Equal(StorageResult.BadQuantity, vault.PlanPut(potion, 0, -1).Result);
        Assert.Equal(StorageResult.BadQuantity, vault.PlanPut(potion, -1, -1).Result);
        Assert.Equal(StorageResult.BadQuantity, vault.PlanPut(potion, 101, -1).Result);
        Assert.Equal(StorageResult.Ok, vault.PlanPut(potion, 100, -1).Result);
        Assert.Equal(StorageResult.Ok, vault.PlanPut(potion, 1, -1).Result);
        ItemInfo gear = StorageFixtures.Equip(900);
        Assert.Equal(StorageResult.BadQuantity, vault.PlanPut(gear, 2, -1).Result);
        Assert.Equal(StorageResult.Ok, vault.PlanPut(gear, 1, -1).Result);
        Assert.Equal(-1, vault.PlanPut(potion, 0, -1).Slot);
    }

    [Fact]
    public void PutUsesThePreferredEmptySlotElseTheFirstEmptyOne()
    {
        StorageVault vault = Vault(8);
        ItemInfo gear = StorageFixtures.Equip(900);
        Assert.Equal(0, vault.PlanPut(gear, 1, -1).Slot);
        Assert.Equal(5, vault.PlanPut(gear, 1, 5).Slot);
        Assert.Equal(7, vault.PlanPut(gear, 1, 7).Slot);
        Assert.Equal(0, vault.PlanPut(gear, 1, 8).Slot);          // 想放的格没开放
        Assert.Equal(0, vault.PlanPut(gear, 1, -2).Slot);
        Assert.False(vault.PlanPut(gear, 1, 5).Merge);
        Fill(vault, (0, StorageFixtures.Equip(901), 1), (1, StorageFixtures.Equip(902), 1), (5, StorageFixtures.Equip(903), 1));
        Assert.Equal(2, vault.PlanPut(gear, 1, 5).Slot);          // 想放的格有东西 → 第一个空格
        Assert.Equal(6, vault.PlanPut(gear, 1, 6).Slot);
        Assert.Equal(2, vault.PlanPut(gear, 1, 0).Slot);
    }

    [Fact]
    public void AStackableGoesIntoTheFirstStackOfTheSameCodeEvenWhenAnotherSlotIsPreferred()
    {
        StorageVault vault = Vault(8);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        Fill(vault, (2, potion, 60), (4, potion, 10));
        StoragePlacement plan = vault.PlanPut(potion, 20, 6);
        Assert.True(plan.IsOk);
        Assert.True(plan.Merge);
        Assert.Equal(2, plan.Slot);
        Assert.Equal(StorageResult.Ok, vault.PlanPut(potion, 40, -1).Result);       // 60 + 40 = 100:等于上限可以
    }

    [Fact]
    public void AnOverflowingMergeIsRefusedWithoutSpillingToAnotherSlot()
    {
        StorageVault vault = Vault(8);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        Fill(vault, (2, potion, 60), (4, potion, 10));
        StoragePlacement plan = vault.PlanPut(potion, 41, -1);                     // 60 + 41 = 101;第二叠 10 + 41 放得下也不去
        Assert.Equal(StorageResult.VaultFull, plan.Result);
        Assert.Equal(-1, plan.Slot);
        Assert.False(plan.Merge);
    }

    [Fact]
    public void AFullVaultStillMergesButTakesNothingNew()
    {
        StorageVault vault = Vault(3);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        Fill(vault, (0, StorageFixtures.Equip(900), 1), (1, StorageFixtures.Equip(901), 1), (2, potion, 10));
        Assert.Equal(StorageResult.VaultFull, vault.PlanPut(StorageFixtures.Equip(902), 1, -1).Result);
        Assert.Equal(StorageResult.VaultFull, vault.PlanPut(StorageFixtures.Stack(912), 1, -1).Result);
        StoragePlacement merge = vault.PlanPut(potion, 5, -1);
        Assert.True(merge.IsOk);
        Assert.Equal(2, merge.Slot);
        Assert.Equal(StorageResult.VaultFull, new StorageVault(StorageKind.Personal, 0).PlanPut(potion, 1, -1).Result);
    }

    [Fact]
    public void EquipmentNeverMergesAndAOneLimitStackableNeverDoes()
    {
        StorageVault vault = Vault(4);
        ItemInfo gear = StorageFixtures.Equip(900);
        Fill(vault, (0, gear, 1));
        StoragePlacement second = vault.PlanPut(gear, 1, -1);
        Assert.True(second.IsOk);
        Assert.False(second.Merge);
        Assert.Equal(1, second.Slot);

        ItemInfo single = StorageFixtures.Stack(913, 1);
        Fill(vault, (2, single, 1));
        Assert.Equal(StorageResult.VaultFull, vault.PlanPut(single, 1, -1).Result);   // 同编号那叠已满(1 + 1 > 1),哪怕还有空格
    }

    [Fact]
    public void CommitAppliesAMergeAndANewStack()
    {
        StorageVault vault = Vault(8);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        StoragePlacement first = vault.PlanPut(potion, 30, -1);
        Assert.Equal(0, vault.CommitPut(potion, 30, first));
        Assert.Equal(30, vault.SlotAt(0).Count);
        StoragePlacement second = vault.PlanPut(potion, 70, 3);
        Assert.True(second.Merge);
        Assert.Equal(0, vault.CommitPut(potion, 70, second));
        Assert.Equal(100, vault.SlotAt(0).Count);
        Assert.Equal(1, vault.UsedSlots);
        StoragePlacement gear = vault.PlanPut(StorageFixtures.Equip(900), 1, 4);
        Assert.Equal(4, vault.CommitPut(StorageFixtures.Equip(900), 1, gear));
        Assert.Equal(900, vault.SlotAt(4).Info.Code);
    }

    [Fact]
    public void CommitRefusesAPlacementThatDoesNotMatchTheVault()
    {
        StorageVault vault = Vault(8);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        ItemInfo other = StorageFixtures.Stack(912, 100);
        Fill(vault, (1, potion, 90), (2, StorageFixtures.Equip(900), 1));
        var before = StorageFixtures.Dump(vault);

        Assert.Equal(-1, vault.CommitPut(potion, 1, StoragePlacement.Fail(StorageResult.VaultFull)));
        Assert.Equal(-1, vault.CommitPut(null, 1, new StoragePlacement(StorageResult.Ok, 3, false)));
        Assert.Equal(-1, vault.CommitPut(potion, 0, new StoragePlacement(StorageResult.Ok, 3, false)));
        Assert.Equal(-1, vault.CommitPut(potion, 1, new StoragePlacement(StorageResult.Ok, 99, false)));      // 没开放的格
        Assert.Equal(-1, vault.CommitPut(potion, 1, new StoragePlacement(StorageResult.Ok, -1, false)));
        Assert.Equal(-1, vault.CommitPut(potion, 1, new StoragePlacement(StorageResult.Ok, 3, true)));        // 并进空格
        Assert.Equal(-1, vault.CommitPut(other, 1, new StoragePlacement(StorageResult.Ok, 1, true)));         // 并进别的编号
        Assert.Equal(-1, vault.CommitPut(potion, 11, new StoragePlacement(StorageResult.Ok, 1, true)));       // 90 + 11 超过上限
        Assert.Equal(-1, vault.CommitPut(potion, 1, new StoragePlacement(StorageResult.Ok, 1, false)));       // 占已经有东西的格
        Assert.Equal(-1, vault.CommitPut(potion, 101, new StoragePlacement(StorageResult.Ok, 3, false)));     // 新叠放不下这么多
        Assert.Equal(before, StorageFixtures.Dump(vault));

        Assert.Equal(1, vault.CommitPut(potion, 10, new StoragePlacement(StorageResult.Ok, 1, true)));        // 90 + 10 = 100:等于可以
        Assert.Equal(100, vault.SlotAt(1).Count);
        Assert.Equal(3, vault.CommitPut(potion, 100, new StoragePlacement(StorageResult.Ok, 3, false)));      // 新叠恰好一格上限
        Assert.Equal(100, vault.SlotAt(3).Count);
    }

    [Fact]
    public void CommitRefusesAPlacementThatIsNotOkEvenWhenItsSlotIsOpenAndEmpty()
    {
        StorageVault vault = Vault(8);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        Assert.Equal(-1, vault.CommitPut(potion, 5, new StoragePlacement(StorageResult.VaultFull, 3, false)));
        Assert.Equal(-1, vault.CommitPut(potion, 5, new StoragePlacement(StorageResult.BadQuantity, 3, false)));
        Assert.Equal(0, vault.UsedSlots);
        Assert.Equal(3, vault.CommitPut(potion, 5, new StoragePlacement(StorageResult.Ok, 3, false)));
    }

    [Fact]
    public void AHugeStackLimitCannotOverflowTheMergeChecks()
    {
        StorageVault vault = Vault(4);
        ItemInfo bulk = StorageFixtures.Stack(911, int.MaxValue);
        Assert.True(vault.RestoreSlot(0, bulk, int.MaxValue - 1));

        StoragePlacement tooMuch = vault.PlanPut(bulk, 5, -1);                    // 比上限多 4;用 int 加会绕成负数而放行
        Assert.Equal(StorageResult.VaultFull, tooMuch.Result);
        Assert.Equal(-1, vault.CommitPut(bulk, 5, new StoragePlacement(StorageResult.Ok, 0, true)));
        Assert.Equal(int.MaxValue - 1, vault.SlotAt(0).Count);

        StoragePlacement exact = vault.PlanPut(bulk, 1, -1);                      // 恰好到上限
        Assert.True(exact.IsOk);
        Assert.Equal(0, vault.CommitPut(bulk, 1, exact));
        Assert.Equal(int.MaxValue, vault.SlotAt(0).Count);
        Assert.Equal(StorageResult.VaultFull, vault.PlanPut(bulk, 1, -1).Result);
    }

    // ------------------------------------------------------------------ 取出与互换

    [Fact]
    public void TakeChecksTheSlotThenTheQuantity()
    {
        StorageVault vault = Vault(8);
        Fill(vault, (1, StorageFixtures.Stack(911), 40), (2, StorageFixtures.Equip(900), 1));
        Assert.Equal(StorageResult.BadSlot, vault.PlanTake(-1, 1));
        Assert.Equal(StorageResult.BadSlot, vault.PlanTake(8, 1));
        Assert.Equal(StorageResult.NoItem, vault.PlanTake(0, 1));
        Assert.Equal(StorageResult.BadQuantity, vault.PlanTake(1, 0));
        Assert.Equal(StorageResult.BadQuantity, vault.PlanTake(1, -1));
        Assert.Equal(StorageResult.BadQuantity, vault.PlanTake(1, 41));
        Assert.Equal(StorageResult.Ok, vault.PlanTake(1, 40));
        Assert.Equal(StorageResult.Ok, vault.PlanTake(1, 1));
        Assert.Equal(StorageResult.BadQuantity, vault.PlanTake(2, 2));
        Assert.Equal(StorageResult.Ok, vault.PlanTake(2, 1));
    }

    [Fact]
    public void TakeSubtractsAndAnEmptiedSlotIsCleared()
    {
        StorageVault vault = Vault(8);
        Fill(vault, (1, StorageFixtures.Stack(911), 40), (2, StorageFixtures.Equip(900), 1));
        Assert.Equal(StorageResult.Ok, vault.Take(1, 15));
        Assert.Equal(25, vault.SlotAt(1).Count);
        Assert.Equal(StorageResult.Ok, vault.Take(1, 25));
        Assert.Null(vault.SlotAt(1));
        Assert.Equal(StorageResult.Ok, vault.Take(2, 1));
        Assert.Null(vault.SlotAt(2));
        Assert.Equal(0, vault.UsedSlots);
    }

    [Fact]
    public void AFailedTakeChangesNothing()
    {
        StorageVault vault = Vault(8);
        Fill(vault, (1, StorageFixtures.Stack(911), 40));
        var before = StorageFixtures.Dump(vault);
        Assert.Equal(StorageResult.BadQuantity, vault.Take(1, 41));
        Assert.Equal(StorageResult.BadQuantity, vault.Take(1, 0));
        Assert.Equal(StorageResult.NoItem, vault.Take(0, 1));
        Assert.Equal(StorageResult.BadSlot, vault.Take(9, 1));
        Assert.Equal(before, StorageFixtures.Dump(vault));
    }

    [Fact]
    public void SwapExchangesTwoSlotsAndMovesIntoAnEmptyOne()
    {
        StorageVault vault = Vault(8);
        Fill(vault, (1, StorageFixtures.Stack(911), 40), (2, StorageFixtures.Equip(900), 1));
        Assert.Equal(StorageResult.Ok, vault.Swap(1, 2));
        Assert.Equal(900, vault.SlotAt(1).Info.Code);
        Assert.Equal(911, vault.SlotAt(2).Info.Code);
        Assert.Equal(StorageResult.Ok, vault.Swap(2, 6));
        Assert.Null(vault.SlotAt(2));
        Assert.Equal(40, vault.SlotAt(6).Count);
        Assert.Equal(StorageResult.Ok, vault.Swap(3, 1));
        Assert.Equal(900, vault.SlotAt(3).Info.Code);
    }

    [Fact]
    public void SwapRejectsClosedSlotsAndTwoEmptyOnesAndAllowsASlotWithItself()
    {
        StorageVault vault = Vault(8);
        Fill(vault, (1, StorageFixtures.Stack(911), 40));
        Assert.Equal(StorageResult.BadSlot, vault.Swap(1, 8));
        Assert.Equal(StorageResult.BadSlot, vault.Swap(-1, 1));
        Assert.Equal(StorageResult.BadSlot, vault.Swap(8, 9));
        Assert.Equal(StorageResult.NoItem, vault.Swap(0, 3));
        Assert.Equal(StorageResult.NoItem, vault.Swap(3, 3));
        Assert.Equal(StorageResult.Ok, vault.Swap(1, 1));
        Assert.Equal(40, vault.SlotAt(1).Count);
    }

    // ------------------------------------------------------------------ 容量变化与读档

    [Fact]
    public void GrowToOnlyGrowsAndStopsAtTheMaximum()
    {
        StorageVault vault = Vault(8);
        Assert.True(vault.GrowTo(24));
        Assert.Equal(24, vault.Capacity);
        Assert.False(vault.GrowTo(24));
        Assert.False(vault.GrowTo(10));
        Assert.False(vault.GrowTo(-5));
        Assert.Equal(24, vault.Capacity);
        Assert.True(vault.GrowTo(500));
        Assert.Equal(152, vault.Capacity);
        Assert.False(vault.GrowTo(500));
        var account = new StorageVault(StorageKind.Account, 0);
        Assert.True(account.GrowTo(60));
        Assert.Equal(56, account.Capacity);
        Assert.False(account.GrowTo(60));
    }

    [Fact]
    public void ANewlyOpenedSlotIsUsableRightAway()
    {
        StorageVault vault = Vault(8);
        ItemInfo gear = StorageFixtures.Equip(900);
        Assert.Equal(StorageResult.VaultFull, new StorageVault(StorageKind.Personal, 0).PlanPut(gear, 1, 0).Result);
        Assert.False(vault.RestoreSlot(8, gear, 1));
        vault.GrowTo(24);
        Assert.True(vault.RestoreSlot(8, gear, 1));
        Assert.Equal(StorageResult.Ok, vault.PlanTake(8, 1));
    }

    [Fact]
    public void RestoreSlotValidatesTheCellAndTheQuantity()
    {
        StorageVault vault = Vault(8);
        ItemInfo potion = StorageFixtures.Stack(911, 100);
        ItemInfo gear = StorageFixtures.Equip(900);
        Assert.False(vault.RestoreSlot(-1, potion, 1));
        Assert.False(vault.RestoreSlot(8, potion, 1));
        Assert.False(vault.RestoreSlot(0, null, 1));
        Assert.False(vault.RestoreSlot(0, potion, 0));
        Assert.False(vault.RestoreSlot(0, potion, -4));
        Assert.False(vault.RestoreSlot(0, potion, 101));
        Assert.False(vault.RestoreSlot(0, gear, 2));
        Assert.Equal(0, vault.UsedSlots);
        Assert.True(vault.RestoreSlot(0, potion, 100));
        Assert.True(vault.RestoreSlot(1, potion, 1));        // 不并进同编号的格:存档里的布局原样回来
        Assert.True(vault.RestoreSlot(2, gear, 1));
        Assert.False(vault.RestoreSlot(0, potion, 5));       // 已经有东西
        Assert.Equal(3, vault.UsedSlots);
        Assert.Equal(101, vault.CountOf(911));
    }
}
