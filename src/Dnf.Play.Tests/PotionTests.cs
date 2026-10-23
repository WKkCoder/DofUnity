using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 消耗品(药水)在 Play 层的接线(证据 E-120):主角的生命魔法按"总量 + 持续毫秒"线性发放、使用规则与快捷栏、背包里使用、存档里的快捷栏、
/// 底栏的物品槽、流程里的 1 到 6 键。物品、数值全是合成的,期望值是手算。
/// </summary>
public class PotionTests
{
    // ------------------------------------------------------------------ 工具

    private static RecoveryEffect Flat(int amount, int ms) => new RecoveryEffect(false, amount, ms);

    private static RecoveryEffect Pct(int amount, int ms) => new RecoveryEffect(true, amount, ms);

    private static ItemInfo Potion(int code, RecoveryEffect hp = null, RecoveryEffect mp = null, int coolMs = 0, int group = -1, int minLevel = 0,
        int type = StackableTypes.Waste)
    {
        return new ItemInfo(code, "stackable/test/p" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = type,
            HpRecovery = hp,
            MpRecovery = mp,
            CoolTimeMs = coolMs,
            CoolGroup = group,
            MinimumLevel = minLevel,
            StackLimit = 99,
            Price = 10,
            Value = 4,
            Icon = "Item/Test.img",
            IconFrame = 1,
        };
    }

    private static HeroVitals Vitals(int hp = 1000, int mp = 500) => new HeroVitals(1, hp, mp, 0, 0, 0);

    // ------------------------------------------------------------------ 生命值对象:发放

    [Fact]
    public void FlatRecoveryIsDeliveredLinearlyAndFinishes()
    {
        HeroVitals v = Vitals();
        v.Damage(600);                                         // 400
        Assert.True(v.StartRecovery(Flat(300, 3000), null));
        Assert.True(v.Recovering);
        Assert.Equal((400, 300), (v.Hp, v.PendingHp));
        v.AdvanceRecovery(1000);
        Assert.Equal(500, v.Hp);
        v.AdvanceRecovery(1000);
        Assert.Equal(600, v.Hp);
        v.AdvanceRecovery(1000);
        Assert.Equal(700, v.Hp);
        Assert.False(v.Recovering);
        Assert.Equal(0, v.PendingHp);
    }

    [Fact]
    public void PercentRecoveryUsesTheMaximumAndInstantOnesLandAtOnce()
    {
        HeroVitals v = Vitals(1000, 200);
        v.Damage(500);
        Assert.True(v.TrySpendMp(200));                        // 魔法值 0
        Assert.True(v.StartRecovery(Pct(10, 0), Pct(50, 0)));
        Assert.Equal((600, 100), (v.Hp, v.Mp));                // 1000 × 10% 与 200 × 50%,持续时间 0:当场发完
        Assert.False(v.Recovering);
    }

    [Fact]
    public void RecoveryStopsAtTheMaximumAndLeavesNothingPending()
    {
        HeroVitals v = Vitals();
        v.Damage(10);                                          // 990
        v.StartRecovery(Flat(300, 1000), null);
        v.AdvanceRecovery(500);                                // 发了 150,夹在上限 1000
        Assert.Equal(1000, v.Hp);
        v.AdvanceRecovery(500);
        Assert.Equal(1000, v.Hp);
        Assert.False(v.Recovering);
    }

    [Fact]
    public void ADrainingEffectNeverKillsAndMpStopsAtZero()
    {
        HeroVitals v = Vitals(100, 50);
        v.Damage(50);                                          // 50
        v.TrySpendMp(20);                                      // 30
        Assert.True(v.StartRecovery(Flat(-200, 0), Flat(-100, 0)));
        Assert.Equal((1, 0), (v.Hp, v.Mp));
        Assert.True(v.IsAlive);
    }

    [Fact]
    public void ABlankStartOrADeadHeroStartsNothing()
    {
        HeroVitals v = Vitals();
        Assert.False(v.StartRecovery(null, null));
        Assert.False(v.StartRecovery(Flat(0, 100), Flat(0, 100)));
        Assert.False(v.Recovering);
        v.Damage(5000);
        Assert.False(v.IsAlive);
        Assert.False(v.StartRecovery(Flat(100, 0), null));
        Assert.Equal(0, v.Hp);
    }

    [Fact]
    public void FallingDownDropsTheUnfinishedEffectsAndNothingRevivesTheHero()
    {
        HeroVitals v = Vitals();
        v.Damage(900);                                         // 100
        v.StartRecovery(Flat(500, 5000), Flat(100, 5000));
        Assert.True(v.Recovering);
        Assert.True(v.Damage(5000));                           // 倒下
        Assert.False(v.Recovering);
        v.AdvanceRecovery(10000);
        Assert.Equal(0, v.Hp);
    }

    [Fact]
    public void TwoPotionsRunAtTheSameTimeEachOnItsOwnClock()
    {
        HeroVitals v = Vitals();
        v.Damage(800);                                         // 200
        v.StartRecovery(Flat(100, 1000), null);
        v.AdvanceRecovery(500);                                // +50
        v.StartRecovery(Flat(40, 400), null);
        v.AdvanceRecovery(400);                                // 第一瓶已过 900 毫秒累计 90(+40),第二瓶发完(+40)
        Assert.Equal(330, v.Hp);
        v.AdvanceRecovery(100);                                // 第一瓶发完(+10)
        Assert.Equal(340, v.Hp);
        Assert.False(v.Recovering);
    }

    [Fact]
    public void OnePotionCanRestoreBothBarsOnDifferentClocks()
    {
        HeroVitals v = Vitals();
        v.Damage(500);
        v.TrySpendMp(400);                                     // 500 / 100
        v.StartRecovery(Flat(100, 1000), Flat(200, 2000));
        v.AdvanceRecovery(1000);
        Assert.Equal((600, 200), (v.Hp, v.Mp));
        v.AdvanceRecovery(1000);
        Assert.Equal((600, 300), (v.Hp, v.Mp));
    }

    [Fact]
    public void CopyingTheStateCarriesTheUnfinishedEffectAndRestoreDropsIt()
    {
        HeroVitals a = Vitals();
        a.Damage(700);                                         // 300
        a.StartRecovery(Flat(400, 2000), null);
        a.AdvanceRecovery(500);                                // +100 → 400
        HeroVitals b = Vitals();
        b.CopyStateFrom(a);
        Assert.Equal((400, true, 300), (b.Hp, b.Recovering, b.PendingHp));
        b.AdvanceRecovery(1500);
        Assert.Equal(700, b.Hp);
        Assert.Equal(400, a.Hp);                               // 原来那份不受影响
        a.Restore();
        Assert.Equal((1000, 500, false), (a.Hp, a.Mp, a.Recovering));
    }

    [Fact]
    public void CopyingFromADeadHeroCarriesNoEffect()
    {
        HeroVitals a = Vitals();
        a.Damage(900);
        a.StartRecovery(Flat(100, 1000), null);
        a.Damage(1000);
        HeroVitals b = Vitals();
        b.CopyStateFrom(a);
        Assert.Equal((0, false), (b.Hp, b.Recovering));
    }

    [Fact]
    public void DirectHealAndMpClampAndIgnoreDeadOrNegative()
    {
        HeroVitals v = Vitals(100, 50);
        v.Damage(60);
        v.TrySpendMp(30);                                      // 40 / 20
        Assert.Equal(30, v.Heal(30));
        Assert.Equal(70, v.Hp);
        Assert.Equal(30, v.Heal(1000));                        // 夹在上限 100
        Assert.Equal(0, v.Heal(-5));
        Assert.Equal(30, v.AddMp(1000));                       // 20 → 50
        v.Damage(1000);
        Assert.Equal(0, v.Heal(10));
        Assert.Equal(0, v.AddMp(10));
    }

    // ------------------------------------------------------------------ 进度:使用规则

    [Fact]
    public void UsingAPotionTakesOneStartsTheCooldownAndTheRecovery()
    {
        HeroProgress p = Progress();
        HeroVitals v = Vitals();
        v.Damage(500);
        ItemInfo potion = Potion(10, Flat(200, 2000), coolMs: 5000);
        Assert.True(p.AddItem(potion, 3));
        Assert.Equal((1, 10, 3), (p.QuickFilled, p.QuickCodeAt(0), p.QuickCountAt(0)));

        Assert.Equal(UseResult.Ok, p.UseQuickSlot(0, v));
        Assert.Equal((2, 1, 1), (p.QuickCountAt(0), p.ItemsUsed, p.CooldownsActive));
        Assert.True(p.Cooldowns.IsCooling(potion));
        Assert.True(v.Recovering);
        v.AdvanceRecovery(2000);
        Assert.Equal(700, v.Hp);
    }

    [Fact]
    public void ACoolingPotionIsRefusedWithoutTakingAnything()
    {
        HeroProgress p = Progress();
        HeroVitals v = Vitals();
        v.Damage(500);
        ItemInfo potion = Potion(10, Flat(200, 0), coolMs: 5000);
        p.AddItem(potion, 3);
        Assert.Equal(UseResult.Ok, p.UseQuickSlot(0, v));
        Assert.Equal(UseResult.Cooling, p.UseQuickSlot(0, v));
        Assert.Equal(UseResult.Cooling, p.UseConsumable(potion, v));
        Assert.Equal((2, 1, 700), (p.QuickCountAt(0), p.ItemsUsed, v.Hp));
        p.Cooldowns.Advance(4999);
        Assert.Equal(UseResult.Cooling, p.UseQuickSlot(0, v));
        p.Cooldowns.Advance(1);
        Assert.Equal(UseResult.Ok, p.UseQuickSlot(0, v));
        Assert.Equal((1, 2, 900), (p.QuickCountAt(0), p.ItemsUsed, v.Hp));
    }

    [Fact]
    public void PotionsOfTheSameCooldownGroupShareTheCooldown()
    {
        HeroProgress p = Progress();
        HeroVitals v = Vitals();
        v.Damage(900);
        ItemInfo small = Potion(10, Flat(10, 0), coolMs: 3000, group: 0);
        ItemInfo large = Potion(11, Flat(50, 0), coolMs: 3000, group: 0);
        ItemInfo mana = Potion(12, null, Flat(10, 0), coolMs: 3000, group: 1);
        p.AddItem(small, 2);
        p.AddItem(large, 2);
        p.AddItem(mana, 2);
        Assert.Equal(UseResult.Ok, p.UseQuickSlot(0, v));
        Assert.Equal(UseResult.Cooling, p.UseQuickSlot(1, v));                 // 同组
        Assert.Equal(UseResult.Ok, p.UseQuickSlot(2, v));                      // 另一组
        Assert.Equal((1, 2, 1), (p.QuickCountAt(0), p.QuickCountAt(1), p.QuickCountAt(2)));
    }

    [Fact]
    public void TheLastPotionEmptiesItsQuickSlotAndAnEmptySlotIsReported()
    {
        HeroProgress p = Progress();
        HeroVitals v = Vitals();
        ItemInfo potion = Potion(10, Flat(10, 0));
        p.AddItem(potion, 1);
        Assert.Equal(UseResult.Ok, p.UseQuickSlot(0, v));
        Assert.Equal((0, 0), (p.QuickFilled, p.Bag.CountOf(10)));
        Assert.Equal(UseResult.EmptySlot, p.UseQuickSlot(0, v));
        Assert.Equal(UseResult.EmptySlot, p.UseQuickSlot(5, v));
        Assert.Equal(UseResult.EmptySlot, p.UseQuickSlot(-1, v));
        Assert.Equal(UseResult.EmptySlot, p.UseQuickSlot(6, v));
        Assert.Equal(UseResult.NotInBag, p.UseConsumable(potion, v));
    }

    [Fact]
    public void LevelJobAndKindAreCheckedBeforeAnythingIsTaken()
    {
        HeroProgress p = Progress(level: 1);
        HeroVitals v = Vitals();
        ItemInfo high = Potion(10, Flat(10, 0), minLevel: 10);
        ItemInfo mageOnly = Potion(11, Flat(10, 0));
        mageOnly.UsableJobs.Add("[mage]");
        ItemInfo plain = new ItemInfo(12, "stackable/test/w.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste };
        ItemInfo gear = new ItemInfo(13, "equipment/test/g.equ", ItemKind.Equipment) { Part = EquipmentParts.Coat };
        foreach (ItemInfo item in new[] { high, mageOnly, plain, gear })
        {
            p.AddItem(item, 2);
        }
        Assert.Equal(UseResult.LevelTooLow, p.UseConsumable(high, v));
        Assert.Equal(UseResult.WrongJob, p.UseConsumable(mageOnly, v));
        Assert.Equal(UseResult.NoEffect, p.UseConsumable(plain, v));
        Assert.Equal(UseResult.NotConsumable, p.UseConsumable(gear, v));
        Assert.Equal(UseResult.NotConsumable, p.UseConsumable(null, v));
        Assert.Equal((2, 2, 2, 2, 0), (p.Bag.CountOf(10), p.Bag.CountOf(11), p.Bag.CountOf(12), p.Bag.CountOf(13), p.ItemsUsed));
        p.SetLevel(10);
        Assert.Equal(UseResult.Ok, p.UseConsumable(high, v));
    }

    [Fact]
    public void WithoutALivingHeroNothingIsUsed()
    {
        HeroProgress p = Progress();
        ItemInfo potion = Potion(10, Flat(10, 0), coolMs: 1000);
        p.AddItem(potion, 2);
        Assert.Equal(UseResult.Unavailable, p.UseQuickSlot(0, null));          // 城镇:没有生命魔法值
        HeroVitals dead = Vitals();
        dead.Damage(5000);
        Assert.Equal(UseResult.Unavailable, p.UseQuickSlot(0, dead));
        Assert.Equal((2, 0, 0), (p.QuickCountAt(0), p.ItemsUsed, p.CooldownsActive));
    }

    [Fact]
    public void ANegativeRecoveryPotionStillConsumesAndDoesNotKill()
    {
        HeroProgress p = Progress();
        HeroVitals v = Vitals(100, 50);
        ItemInfo bad = Potion(10, Flat(-500, 0));
        p.AddItem(bad, 1);
        Assert.Equal(UseResult.Ok, p.UseConsumable(bad, v));
        Assert.Equal((1, true), (v.Hp, v.IsAlive));
    }

    // ------------------------------------------------------------------ 进度:快捷栏

    [Fact]
    public void PotionsGoToTheFirstEmptyQuickSlotAndOnlyEligibleOnesDo()
    {
        HeroProgress p = Progress();
        p.AddItem(Potion(10, Flat(1, 0)), 3);
        p.AddItem(Potion(11, null, Flat(1, 0)), 1);
        p.AddItem(Potion(10, Flat(1, 0)), 2);                                  // 同一种再加:不占第二格
        p.AddItem(new ItemInfo(12, "stackable/test/m.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material, HpRecovery = Flat(1, 0) });
        p.AddItem(new ItemInfo(13, "stackable/test/w.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste });
        p.AddItem(new ItemInfo(14, "equipment/test/g.equ", ItemKind.Equipment) { Part = EquipmentParts.Coat });
        Assert.Equal((2, 10, 11, 5, 1), (p.QuickFilled, p.QuickCodeAt(0), p.QuickCodeAt(1), p.QuickCountAt(0), p.QuickCountAt(1)));
        Assert.Equal(0, p.QuickCodeAt(2));
        Assert.Equal(0, p.QuickCodeAt(9));
    }

    [Fact]
    public void ASeventhKindStaysInTheBagOnly()
    {
        HeroProgress p = Progress();
        for (int i = 0; i < QuickSlots.Count + 1; i++)
        {
            p.AddItem(Potion(100 + i, Flat(1, 0)), 1);
        }
        Assert.Equal(QuickSlots.Count, p.QuickFilled);
        Assert.Equal(1, p.Bag.CountOf(100 + QuickSlots.Count));
        Assert.Equal(0, p.Quick.IndexOf(100));
        Assert.Equal(-1, p.Quick.IndexOf(100 + QuickSlots.Count));
    }

    [Fact]
    public void BuyingPlacesAndSellingEmptiesTheSlotOnlyWhenNoneAreLeft()
    {
        HeroProgress p = Progress();
        p.AddGold(1000);
        ItemInfo potion = Potion(10, Flat(1, 0));
        Assert.Equal(ShopResult.Ok, p.Buy(null, potion, 4));
        Assert.Equal((1, 4), (p.QuickFilled, p.QuickCountAt(0)));

        Assert.Equal(ShopResult.Ok, p.Sell(InventoryTab.Consumable, 0, 3, null, out long _));
        Assert.Equal((1, 1), (p.QuickFilled, p.QuickCountAt(0)));              // 还剩一件:快捷栏还在
        Assert.Equal(ShopResult.Ok, p.Sell(InventoryTab.Consumable, 0, 1, null, out long _));
        Assert.Equal((0, 0), (p.QuickFilled, p.Bag.CountOf(10)));
    }

    [Fact]
    public void TheQuickBarRoundTripsThroughTheSaveAndRefillsFromTheBag()
    {
        ItemInfo a = Potion(10, Flat(1, 0));
        ItemInfo b = Potion(11, null, Flat(1, 0));
        HeroProgress p = Progress();
        p.AddItem(a, 5);
        p.AddItem(b, 2);
        Assert.Equal((10, 11), (p.QuickCodeAt(0), p.QuickCodeAt(1)));

        HeroSave save = p.Snapshot();
        Assert.Equal(new[] { (0, 10), (1, 11) }, save.Quick.Select(q => (q.Slot, q.Code)).ToArray());
        Assert.Contains("quick 0 10\n", save.ToText());
        Assert.Contains("quick 1 11\n", save.ToText());
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);
        Assert.Equal(save.ToText(), parsed.ToText());

        HeroProgress restored = Progress();
        Dictionary<int, ItemInfo> items = new() { [10] = a, [11] = b };
        restored.Restore(parsed, code => items.TryGetValue(code, out ItemInfo item) ? item : null);
        Assert.Equal((10, 11, 5, 2), (restored.QuickCodeAt(0), restored.QuickCodeAt(1), restored.QuickCountAt(0), restored.QuickCountAt(1)));
    }

    [Fact]
    public void ARestoredQuickSlotKeepsItsPlaceAndAMissingItemIsDropped()
    {
        ItemInfo a = Potion(10, Flat(1, 0));
        ItemInfo b = Potion(11, null, Flat(1, 0));
        var save = new HeroSave();
        save.Bag.Add(new SavedStack(10, 3));
        save.Bag.Add(new SavedStack(11, 1));
        save.Quick.Add(new SavedQuick(3, 11));                                 // 11 号放在第 3 格
        save.Quick.Add(new SavedQuick(4, 99));                                 // 数据里没有 99 号
        save.Quick.Add(new SavedQuick(5, 10));
        HeroProgress p = Progress();
        Dictionary<int, ItemInfo> items = new() { [10] = a, [11] = b };
        p.Restore(save, code => items.TryGetValue(code, out ItemInfo item) ? item : null);
        Assert.Equal((0, 0, 0, 11, 0, 10), (p.QuickCodeAt(0), p.QuickCodeAt(1), p.QuickCodeAt(2), p.QuickCodeAt(3), p.QuickCodeAt(4), p.QuickCodeAt(5)));
    }

    [Fact]
    public void AnOldSaveWithoutQuickLinesGetsItsPotionsPlacedFromTheBag()
    {
        ItemInfo a = Potion(10, Flat(1, 0));
        ItemInfo gear = new ItemInfo(20, "equipment/test/g.equ", ItemKind.Equipment) { Part = EquipmentParts.Coat };
        var save = new HeroSave();
        save.Bag.Add(new SavedStack(20, 1));
        save.Bag.Add(new SavedStack(10, 4));
        HeroProgress p = Progress();
        Dictionary<int, ItemInfo> items = new() { [10] = a, [20] = gear };
        p.Restore(save, code => items.TryGetValue(code, out ItemInfo item) ? item : null);
        Assert.Equal((1, 10, 4), (p.QuickFilled, p.QuickCodeAt(0), p.QuickCountAt(0)));
    }

    [Fact]
    public void RestoreClearsOldCooldownsAndOldQuickSlots()
    {
        ItemInfo a = Potion(10, Flat(1, 0), coolMs: 5000);
        HeroProgress p = Progress();
        p.AddItem(a, 2);
        p.UseConsumable(a, Vitals());
        Assert.Equal(1, p.CooldownsActive);
        p.Restore(new HeroSave(), code => null);
        Assert.Equal((0, 0), (p.QuickFilled, p.CooldownsActive));
    }

    [Theory]
    [InlineData("quick 6 10")]
    [InlineData("quick -1 10")]
    [InlineData("quick 0 0")]
    [InlineData("quick 0")]
    [InlineData("quick 0 10 4")]
    [InlineData("quick a b")]
    public void BadQuickLinesMakeTheWholeSaveUnreadable(string line)
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\nlevel 3\n" + line + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("第 3 行", error);
    }

    // ------------------------------------------------------------------ 战斗会话里

    [Fact]
    public void ASessionPotionRestoresTheHeroOverTimeAndTheCooldownRunsOnTheSessionClock()
    {
        HeroProgress progress = Progress();
        ItemInfo potion = Potion(10, Flat(300, 3000), coolMs: 4000);
        progress.AddItem(potion, 2);
        HeroVitals vitals = Vitals();
        vitals.Damage(600);                                                    // 400
        using var arena = new RoomCombatTests.Arena(new (int, int)[0], vitals: vitals, progress: progress);
        RoomCombatSession s = arena.Session;

        Assert.Equal(UseResult.Ok, s.TryUseQuickSlot(0));
        Assert.Equal((1, UseResult.Ok, 1), (s.ItemsUsed, s.LastUse, progress.Bag.CountOf(10)));
        arena.Run(1000);
        Assert.Equal(500, vitals.Hp);                                          // 300 × 1000 / 3000
        arena.Run(2000);
        Assert.Equal((700, false), (vitals.Hp, vitals.Recovering));
        Assert.True(progress.Cooldowns.IsCooling(potion));                     // 冷却 4000 毫秒:已过 3000
        Assert.Equal(UseResult.Cooling, s.TryUseQuickSlot(0));
        Assert.Equal((1, UseResult.Cooling), (s.ItemsUsed, s.LastUse));
        arena.Run(1000);
        Assert.False(progress.Cooldowns.IsCooling(potion));
        Assert.Equal(UseResult.Ok, s.TryUseItem(potion));
        Assert.Equal((2, 0), (s.ItemsUsed, progress.Bag.CountOf(10)));
        Assert.Equal(0, progress.QuickFilled);                                 // 用光了:快捷栏腾空
    }

    [Fact]
    public void TheHudStatesFollowTheQuickBarTheBagCountAndTheCooldown()
    {
        HeroProgress progress = Progress();
        ItemInfo hp = Potion(10, Flat(100, 0), coolMs: 2000);
        ItemInfo mp = Potion(11, null, Flat(100, 0));
        progress.AddItem(hp, 3);
        progress.AddItem(mp, 5);
        using var arena = new RoomCombatTests.Arena(new (int, int)[0], vitals: Vitals(), progress: progress);
        RoomCombatSession s = arena.Session;

        IReadOnlyList<HudItemState> before = s.ItemSlotStates();
        Assert.Equal(2, before.Count);
        Assert.Equal((0, "sprite/item/test.img", 1, 3, 0.0), (before[0].Slot, before[0].IconKey, before[0].IconFrame, before[0].Count, before[0].CooldownFraction));
        Assert.Equal((1, 5), (before[1].Slot, before[1].Count));

        Assert.Equal(UseResult.Ok, s.TryUseQuickSlot(0));
        arena.Run(500);
        IReadOnlyList<HudItemState> after = s.ItemSlotStates();
        Assert.Equal((2, 0.75), (after[0].Count, Math.Round(after[0].CooldownFraction, 6)));   // 冷却 2000 毫秒,已过 500
        Assert.Equal(0.0, after[1].CooldownFraction);
    }

    [Fact]
    public void ADeadOrAbsentHeroCannotDrinkInASession()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Potion(10, Flat(100, 0), coolMs: 1000), 2);
        HeroVitals dead = Vitals();
        dead.Damage(5000);
        using (var arena = new RoomCombatTests.Arena(new (int, int)[0], vitals: dead, progress: progress))
        {
            Assert.Equal(UseResult.Unavailable, arena.Session.TryUseQuickSlot(0));
        }
        using (var noVitals = new RoomCombatTests.Arena(new (int, int)[0], vitals: null, progress: progress))
        {
            Assert.Equal(UseResult.Unavailable, noVitals.Session.TryUseQuickSlot(0));   // 无伤害模式没有底栏,也没有能回复的生命值对象
        }
        using (var noProgress = new RoomCombatTests.Arena(new (int, int)[0], vitals: Vitals(), progress: null))
        {
            Assert.Equal(UseResult.Unavailable, noProgress.Session.TryUseQuickSlot(0));
            Assert.Empty(noProgress.Session.ItemSlotStates());
        }
        Assert.Equal((2, 0, 0), (progress.Bag.CountOf(10), progress.ItemsUsed, progress.CooldownsActive));
    }

    [Fact]
    public void ThePotionEffectDiesWithTheHeroInASession()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Potion(10, Flat(500, 5000)), 1);
        HeroVitals vitals = Vitals();
        vitals.Damage(900);
        using var arena = new RoomCombatTests.Arena(new (int, int)[0], vitals: vitals, progress: progress);
        Assert.Equal(UseResult.Ok, arena.Session.TryUseQuickSlot(0));
        arena.Run(500);
        Assert.True(vitals.Recovering);
        Assert.True(vitals.Damage(5000));
        arena.Run(500);
        Assert.Equal((0, false), (vitals.Hp, vitals.Recovering));
    }

    // ------------------------------------------------------------------ 底栏物品槽

    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    private static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = (byte)(y % 256);
                rgba[o + 1] = (byte)(x % 256);
                rgba[o + 2] = tag;
                rgba[o + 3] = 255;
            }
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    private static HudArt Hud(out FakeSprites sprites)
    {
        sprites = new FakeSprites();
        sprites.Frames[(HudArt.HudImage, HudArt.BarFrame)] = Coded(800, 91, 1, 0, 1);
        sprites.Frames[(HudArt.HudImage, HudArt.HpFillFrame)] = Coded(56, 56, 2);
        sprites.Frames[(HudArt.HudImage, HudArt.MpFillFrame)] = Coded(56, 56, 3);
        sprites.Frames[(HudArt.HudImage, HudArt.KeyLabelStripFrame)] = Coded(448, 7, 4, 107, 461);
        sprites.Frames[("sprite/item/test.img", 1)] = Coded(28, 28, 70);
        return HudArt.TryCreate(sprites)!;
    }

    private static List<SceneSprite> Build(HudArt hud, IReadOnlyList<HudItemState> items)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        hud.Build(list, ref order, 1, 100, 100, 50, 50, null, null, null, items);
        return list;
    }

    [Fact]
    public void TheSixNumberLabelsAreCutFromTheKeyStripAtTheMeasuredPlaces()
    {
        HudArt hud = Hud(out _);
        List<SceneSprite> list = Build(hud, null);
        List<SceneSprite> labels = list.Where(s => s.AnimationPath == "hud-item-key").ToList();
        Assert.Equal(HudArt.ItemSlotCount, labels.Count);
        int[] columns = { 0, 28, 58, 88, 118, 148 };
        int[] widths = { 4, 6, 6, 6, 6, 6 };
        for (int i = 0; i < labels.Count; i++)
        {
            Assert.Equal((109 + columns[i], 579), (labels[i].ScreenX, labels[i].ScreenY));
            Assert.Equal((widths[i], 7), (labels[i].Frame.Width, labels[i].Frame.Height));
            Assert.Equal(columns[i] % 256, labels[i].Frame.Rgba[1]);            // 第一列的列号 = 在条里的起始列
        }
    }

    [Fact]
    public void AnItemSlotShowsTheIconTheCountAndNoShadeWhenReady()
    {
        HudArt hud = Hud(out _);
        List<SceneSprite> list = Build(hud, new[] { new HudItemState(2, "sprite/item/test.img", 1, 12) });
        SceneSprite icon = list.Single(s => s.AnimationPath == "hud-item");
        Assert.Equal((85 + 60, 558), (icon.ScreenX, icon.ScreenY));
        Assert.Equal(70, icon.Frame.Rgba[2]);
        SceneSprite count = list.Single(s => s.AnimationPath == "hud-item-count");
        Assert.Equal(85 + 60 + 28, count.ScreenX + count.Frame.Width);          // 右端对齐槽内 x = 26 的填充再加一圈描边
        Assert.Equal(558 + HudArt.ItemCountTop, count.ScreenY);
        Assert.DoesNotContain(list, s => s.AnimationPath == "hud-item-cooldown");
        Assert.True(list.IndexOf(icon) < list.IndexOf(count));
        Assert.True(list.IndexOf(count) < list.IndexOf(list.First(s => s.AnimationPath == "hud-item-key")));
    }

    [Fact]
    public void ACoolingItemSlotGetsAShadeBetweenTheCountAndTheNumberLabel()
    {
        HudArt hud = Hud(out _);
        List<SceneSprite> list = Build(hud, new[] { new HudItemState(0, "sprite/item/test.img", 1, 3, 0.5) });
        SceneSprite shade = list.Single(s => s.AnimationPath == "hud-item-cooldown");
        Assert.Equal((85, 558), (shade.ScreenX, shade.ScreenY));
        Assert.Equal((28, 14), (shade.Frame.Width, shade.Frame.Height));
        Assert.Equal(HudArt.CooldownShadeAlpha, shade.TintAlpha);
        Assert.True(list.IndexOf(shade) > list.IndexOf(list.Single(s => s.AnimationPath == "hud-item-count")));
        Assert.True(list.IndexOf(shade) < list.IndexOf(list.First(s => s.AnimationPath == "hud-item-key")));
    }

    [Fact]
    public void ItemSlotsWithoutAnIconStillShowTheCountAndBadSlotsOrCountsAreSkipped()
    {
        HudArt hud = Hud(out _);
        List<SceneSprite> list = Build(hud, new[]
        {
            new HudItemState(1, "sprite/item/missing.img", 9, 7),
            new HudItemState(-1, "sprite/item/test.img", 1, 5),
            new HudItemState(HudArt.ItemSlotCount, "sprite/item/test.img", 1, 5),
            new HudItemState(3, null!, -1, 0),
        });
        Assert.DoesNotContain(list, s => s.AnimationPath == "hud-item");
        Assert.Single(list, s => s.AnimationPath == "hud-item-count");
    }

    [Fact]
    public void TheCountTextIsCachedPerNumberAndDrawnWhiteWithABlackOutline()
    {
        HudArt hud = Hud(out _);
        SpriteFrame a = Build(hud, new[] { new HudItemState(0, "sprite/item/test.img", 1, 12) }).Single(s => s.AnimationPath == "hud-item-count").Frame;
        SpriteFrame b = Build(hud, new[] { new HudItemState(4, "sprite/item/test.img", 1, 12) }).Single(s => s.AnimationPath == "hud-item-count").Frame;
        SpriteFrame c = Build(hud, new[] { new HudItemState(0, "sprite/item/test.img", 1, 13) }).Single(s => s.AnimationPath == "hud-item-count").Frame;
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        // 白色填充(255,255,255)与黑色描边:帧里同时有两种不透明颜色
        var colors = new HashSet<(byte, byte, byte)>();
        for (int i = 0; i < a.Rgba.Length; i += 4)
        {
            if (a.Rgba[i + 3] == 255)
            {
                colors.Add((a.Rgba[i], a.Rgba[i + 1], a.Rgba[i + 2]));
            }
        }
        Assert.Equal(new HashSet<(byte, byte, byte)> { (255, 255, 255), (0, 0, 0) }, colors);
    }

    // ------------------------------------------------------------------ 流程:1 到 6 键与背包里使用

    private sealed class ItemRun : GameFlowTests.FakeHero, IRunSession, IItemSession
    {
        public ItemRun(string label) : base(label)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }

        public List<int> Slots { get; } = new List<int>();

        public List<int> Items { get; } = new List<int>();

        public UseResult Next { get; set; } = UseResult.Ok;

        public UseResult TryUseQuickSlot(int slot)
        {
            Slots.Add(slot);
            return Next;
        }

        public UseResult TryUseItem(ItemInfo item)
        {
            Items.Add(item.Code);
            return Next;
        }
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static GameFlow NewFlow()
    {
        return new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new ItemRun("run " + path + " #" + run), new[] { A });
    }

    [Fact]
    public void TheNumberKeysOnlyReachTheDungeonSessionWhenNoWindowIsOpen()
    {
        GameFlow flow = NewFlow();
        Assert.Equal(ItemUse.UnavailableName, flow.UseQuickSlot(0));           // 还没开始
        flow.EnterTown("start");
        Assert.Equal(ItemUse.UnavailableName, flow.UseQuickSlot(0));           // 城镇
        flow.StartDungeon(A.Path, "start");
        var run = (ItemRun)flow.Run;
        Assert.Equal(ItemUse.OkName, flow.UseQuickSlot(2));
        Assert.Equal(new[] { 2 }, run.Slots);
        run.Next = UseResult.Cooling;
        Assert.Equal("Cooling", flow.UseQuickSlot(4));
        Assert.Equal(new[] { 2, 4 }, run.Slots);

        flow.ToggleBag();
        Assert.Equal(ItemUse.UnavailableName, flow.UseQuickSlot(0));           // 背包开着
        flow.ToggleBag();
        flow.ToggleMenu();
        Assert.Equal(ItemUse.UnavailableName, flow.UseQuickSlot(0));           // 菜单开着
        Assert.Equal(2, run.Slots.Count);
    }

    [Fact]
    public void ASessionWithoutItemsAnswersUnavailable()
    {
        Assert.Equal(ItemUse.UnavailableName, ItemUse.QuickSlot(new GameFlowTests.FakeHero("town"), 0));
        Assert.Equal(ItemUse.UnavailableName, ItemUse.QuickSlot(null!, 0));
    }

    [Fact]
    public void EnterInTheBagUsesAConsumableThroughTheSessionAndReportsTheOutcome()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path, "start");
        var run = (ItemRun)flow.Run;
        HeroProgress p = Progress();
        p.AddItem(Potion(10, Flat(1, 0)), 2);
        Assert.True(flow.ToggleBag());
        flow.Bag.NextTab(1);                                                   // 消耗品页
        Assert.True(flow.Bag.Activate(p));
        Assert.Equal(new[] { 10 }, run.Items);
        Assert.Equal(("USED", UseResult.Ok, 1), (flow.Bag.Message, flow.Bag.LastUse, flow.Bag.Uses));

        run.Next = UseResult.Cooling;
        Assert.False(flow.Bag.Activate(p));
        Assert.Equal(("COOLING DOWN", UseResult.Cooling, 1), (flow.Bag.Message, flow.Bag.LastUse, flow.Bag.Uses));
    }

    [Fact]
    public void InTownTheBagCannotUseAConsumable()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        HeroProgress p = Progress();
        p.AddItem(Potion(10, Flat(1, 0)), 2);
        flow.ToggleBag();
        flow.Bag.NextTab(1);
        Assert.False(flow.Bag.Activate(p));
        Assert.Equal(("CANNOT USE HERE", UseResult.Unavailable), (flow.Bag.Message, flow.Bag.LastUse));
        Assert.Equal(2, p.Bag.CountOf(10));
    }

    [Theory]
    [InlineData(UseResult.Ok, "USED")]
    [InlineData(UseResult.Cooling, "COOLING DOWN")]
    [InlineData(UseResult.LevelTooLow, "LEVEL TOO LOW")]
    [InlineData(UseResult.WrongJob, "WRONG CLASS")]
    [InlineData(UseResult.Unavailable, "CANNOT USE HERE")]
    [InlineData(UseResult.NotInBag, "NONE LEFT")]
    [InlineData(UseResult.NoEffect, "CANNOT USE YET")]
    [InlineData(UseResult.NotConsumable, "CANNOT USE YET")]
    [InlineData(UseResult.EmptySlot, "CANNOT USE YET")]
    public void UseMessagesArePlaceholderEnglishAndAllInTheFont(UseResult result, string expected)
    {
        string message = BagWindow.MessageOf(result);
        Assert.Equal(expected, message);
        Assert.All(message, c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
    }

    [Fact]
    public void TheBagPageTellsWhetherAConsumableCanBeUsed()
    {
        var bag = new BagWindow();
        HeroProgress p = Progress(level: 1);
        Assert.Equal("CAN USE", BagWindowArt.FitText(bag, Potion(10, Flat(1, 0)), p));
        Assert.Equal("NEED LEVEL 9", BagWindowArt.FitText(bag, Potion(11, Flat(1, 0), minLevel: 9), p));
        ItemInfo mageOnly = Potion(12, Flat(1, 0));
        mageOnly.UsableJobs.Add("[mage]");
        Assert.Equal("WRONG CLASS", BagWindowArt.FitText(bag, mageOnly, p));
        Assert.Equal(string.Empty, BagWindowArt.FitText(bag, Potion(13), p));           // 没有这个版本做了的效果
        Assert.Equal(string.Empty, BagWindowArt.FitText(bag, Potion(14, Flat(1, 0), type: StackableTypes.Material), p));
        Assert.All(BagWindowArt.HintText, c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
    }
}
