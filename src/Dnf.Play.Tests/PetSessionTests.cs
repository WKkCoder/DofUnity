using Dnf.Game;
using Dnf.Game.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 宠物会话(本项目自己的设计):把名册、宠物格、目录与时钟接成一个入口,以及存档的写入与还原。
/// 脚本、物品与编号全是合成的(宠物 901 到 903、物品 950 起),经验表 10、30、60、100,期望值手算。
/// </summary>
public class PetSessionTests
{
    private sealed class FixedSource : IPetExpSource
    {
        public int PetExpAmount { get; set; }
    }

    /// <summary>
    /// 901:上限 5 级、开放红蓝两色;902:选择成长型;903:上限 5 级、开放三色。
    /// 物品:950 → 901、951 → 903、952 → 902、953 → 编号 999(不在列表里);神器 960(红,加成 10)、961(蓝,加成 20)、962(绿,宠物至少 2 级,加成 30)。
    /// </summary>
    private static Fixture BuildFixture(bool withTable = true)
    {
        Fixture fx = new Fixture()
            .List(PetCatalog.ListPath, (901, "p901.cre"), (902, "p902.cre"), (903, "p903.cre"))
            .Tokens("creature/p901.cre", w => w.Tag("[max level]").Int(5).Tag("[artifact slot]").Str("red").Str("blue").Tag("[/artifact slot]"))
            .Tokens("creature/p902.cre", w => w.Tag("[max level]").Int(5)
                .Tag("[selective grow creature]").Tag("[using exp item]").Int(1).Tag("[grow Atype item index]").Int(1).Tag("[/selective grow creature]"))
            .Tokens("creature/p903.cre", w => w.Tag("[max level]").Int(5)
                .Tag("[artifact slot]").Str("red").Str("blue").Str("green").Tag("[/artifact slot]"))
            .Tokens("equipment/test/pet950.equ", w => w.Tag("[creature species]").Int(901))
            .Tokens("equipment/test/pet951.equ", w => w.Tag("[creature species]").Int(903))
            .Tokens("equipment/test/pet952.equ", w => w.Tag("[creature species]").Int(902))
            .Tokens("equipment/test/pet953.equ", w => w.Tag("[creature species]").Int(999))
            .Tokens("equipment/test/art960.equ", w => w.Tag("[creature experience amount rate]").Float(10f))
            .Tokens("equipment/test/art961.equ", w => w.Tag("[creature experience amount rate]").Float(20f))
            .Tokens("equipment/test/art962.equ", w => w.Tag("[creature minimum level]").Int(2).Tag("[creature experience amount rate]").Float(30f));
        if (withTable)
        {
            fx.Tokens(PetCatalog.ExpTablePath, w => w.Ints(10, 30, 60, 100));
        }
        return fx;
    }

    private static readonly Dictionary<int, ItemInfo> Items = MakeItems();

    private static Dictionary<int, ItemInfo> MakeItems()
    {
        ItemInfo Equip(int code, string file, int part) => new ItemInfo(code, "equipment/test/" + file + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = 1 };
        var items = new Dictionary<int, ItemInfo>
        {
            [950] = Equip(950, "pet950", EquipmentParts.Creature),
            [951] = Equip(951, "pet951", EquipmentParts.Creature),
            [952] = Equip(952, "pet952", EquipmentParts.Creature),
            [953] = Equip(953, "pet953", EquipmentParts.Creature),
            [960] = Equip(960, "art960", EquipmentParts.ArtifactRed),
            [961] = Equip(961, "art961", EquipmentParts.ArtifactBlue),
            [962] = Equip(962, "art962", EquipmentParts.ArtifactGreen),
            [970] = Equip(970, "weapon970", EquipmentParts.Weapon),
            [971] = new ItemInfo(971, "stackable/p971.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste },
        };
        return items;
    }

    private static ItemInfo Lookup(int code) => Items.TryGetValue(code, out ItemInfo item) ? item : null;

    private static PetSession NewSession(bool withTable = true)
    {
        return new PetSession(new PetCatalog(new GameData(BuildFixture(withTable).Build())));
    }

    private static Inventory BagWith(params int[] codes)
    {
        var bag = new Inventory();
        foreach (int code in codes)
        {
            Assert.Equal(1, bag.Add(Items[code]));
        }
        return bag;
    }

    // ------------------------------------------------------------------ 领养

    [Fact]
    public void AdoptingAPetItemHatchesAFreshPet()
    {
        PetSession session = NewSession();
        PetInstance first = session.Adopt(Items[950], 0);
        Assert.NotNull(first);
        Assert.Equal(1, first.Uid);
        Assert.Equal(950, first.ItemCode);
        Assert.Equal(901, first.Def.Id);
        Assert.Equal(1, first.Level);
        Assert.Equal(0, first.Exp);
        Assert.Equal(100, first.Stomach(0));
        Assert.Equal(PetState.Stored, first.State);

        PetInstance second = session.Adopt(Items[951], 0);
        Assert.Equal(2, second.Uid);
        Assert.Equal(903, second.Def.Id);
        Assert.Equal(2, session.Roster.Count);
        Assert.Null(session.Equipped);
    }

    [Theory]
    [InlineData(970)]   // 武器
    [InlineData(971)]   // 可叠加物品
    [InlineData(960)]   // 神器不是宠物物品
    [InlineData(953)]   // 物种不在列表里
    public void AdoptingSomethingThatIsNotAUsablePetItemAddsNothing(int code)
    {
        PetSession session = NewSession();
        Assert.Null(session.Adopt(Items[code], 0));
        Assert.Null(session.Adopt(null, 0));
        Assert.Equal(0, session.Roster.Count);
    }

    [Fact]
    public void AdoptingNeedsTheExpTable()
    {
        PetSession session = NewSession(withTable: false);
        Assert.Null(session.Adopt(Items[950], 0));
        Assert.Equal(0, session.Roster.Count);
    }

    // ------------------------------------------------------------------ 装备、喂食、加经验

    [Fact]
    public void EquippingAndUnequippingFollowTheEquipRules()
    {
        PetSession session = NewSession();
        session.Adopt(Items[950], 0);
        session.Adopt(Items[951], 0);
        Assert.Equal(PetEquipResult.Ok, session.Equip(1, 0, false));
        Assert.Equal(1, session.Equipped.Uid);
        Assert.Equal(PetState.Charging, session.Equipped.State);
        Assert.Equal(PetEquipResult.Ok, session.Equip(2, 0, true));
        Assert.Equal(2, session.Equipped.Uid);
        Assert.Equal(PetState.Stored, session.Roster.Find(1).State);
        Assert.Equal(PetState.Draining, session.Equipped.State);
        Assert.Equal(PetEquipResult.AlreadyEquipped, session.Equip(2, 0, false));
        Assert.Equal(PetEquipResult.NoItem, session.Equip(9, 0, false));
        Assert.Equal(PetEquipResult.Ok, session.Unequip(0));
        Assert.Null(session.Equipped);
        Assert.Equal(PetEquipResult.NoItem, session.Unequip(0));
    }

    [Fact]
    public void FeedingAndGainingExpNeedAnEquippedPet()
    {
        PetSession session = NewSession();
        session.Adopt(Items[950], 0);
        Assert.Equal(PetFeedResult.NoPet, session.Feed(0));
        Assert.Equal(PetGainOutcome.NoPet, session.GainExp(5, 0).Outcome);
        Assert.Equal(0, session.Roster.Find(1).Exp);

        session.Equip(1, 0, false);
        Assert.Equal(PetFeedResult.Fed, session.Feed(0));
        PetGainResult r = session.GainExp(5, 0);
        Assert.Equal(PetGainOutcome.Gained, r.Outcome);
        Assert.Equal(5, r.Added);
        Assert.Equal(5, session.Equipped.Exp);
    }

    [Fact]
    public void ArtifactBonusesFlowIntoTheExpGained()
    {
        PetSession session = NewSession();
        session.Adopt(Items[951], 0);
        session.Equip(1, 0, false);
        Inventory bag = BagWith(960, 961, 962);
        Assert.Equal(PetEquipResult.Ok, session.EquipArtifact(bag, 0));
        Assert.Equal(PetEquipResult.Ok, session.EquipArtifact(bag, 0));
        Assert.Equal(PetEquipResult.Ok, session.EquipArtifact(bag, 0));
        Assert.Empty(bag.Tab(InventoryTab.Equipment));

        // 1 级:红 10 + 蓝 20 = 30;绿(宠物至少 2 级)不算。基础 10 → 10 + 3 = 13,经验 13 升到 2 级。
        Assert.Equal(30f, session.Equipment.ExperienceBonus(session.Catalog.ItemData));
        PetGainResult first = session.GainExp(10, 0);
        Assert.Equal(13, first.Added);
        Assert.True(first.LeveledUp);
        Assert.Equal(2, session.Equipped.Level);

        // 2 级:三格都算,60%。基础 10 → 10 + 6 = 16,经验 29,仍是 2 级。
        Assert.Equal(60f, session.Equipment.ExperienceBonus(session.Catalog.ItemData));
        PetGainResult second = session.GainExp(10, 0);
        Assert.Equal(16, second.Added);
        Assert.False(second.LeveledUp);
        Assert.Equal(29, session.Equipped.Exp);

        // 基础 1 → 1 + 截断(0.6)= 1,经验 30,升到 3 级。
        PetGainResult third = session.GainExp(1, 0);
        Assert.Equal(1, third.Added);
        Assert.True(third.LeveledUp);
        Assert.Equal(3, session.Equipped.Level);
    }

    [Fact]
    public void AnArtifactOfAClosedColorCannotBePlacedAndAddsNothing()
    {
        PetSession session = NewSession();
        session.Adopt(Items[950], 0);    // 只开放红蓝
        session.Equip(1, 0, false);
        Inventory bag = BagWith(962);
        Assert.Equal(PetEquipResult.SlotUnavailable, session.EquipArtifact(bag, 0));
        Assert.Single(bag.Tab(InventoryTab.Equipment));
        Assert.Equal(0f, session.Equipment.ExperienceBonus(session.Catalog.ItemData));
    }

    [Fact]
    public void ExpCanComeFromAnySourceThatHasAnAmount()
    {
        PetSession session = NewSession();
        session.Adopt(Items[950], 0);
        session.Equip(1, 0, false);
        Assert.Equal(7, session.GainExp(new FixedSource { PetExpAmount = 7 }, 0).Added);
        Assert.Throws<ArgumentNullException>(() => session.GainExp((IPetExpSource)null, 0));
    }

    [Fact]
    public void TheFightingFlagSwitchesTheEquippedPetBetweenDrainingAndCharging()
    {
        PetSession session = NewSession();
        Assert.Equal(default(PetStateChange), session.SetFighting(true, 0));
        session.Adopt(Items[950], 0);
        session.Equip(1, 0, false);
        PetStateChange start = session.SetFighting(true, 0);
        Assert.Equal(PetState.Draining, session.Equipped.State);
        Assert.Equal(6000, start.DeathCheckDelay);
        session.SetFighting(false, 600);
        Assert.Equal(PetState.Charging, session.Equipped.State);
        Assert.Equal(90, session.Equipped.Stomach(600));
    }

    [Fact]
    public void ReleasingAPetUnequipsItAndKeepsTheArtifacts()
    {
        PetSession session = NewSession();
        session.Adopt(Items[951], 0);
        session.Adopt(Items[950], 0);
        session.Equip(1, 0, false);
        Inventory bag = BagWith(960);
        session.EquipArtifact(bag, 0);

        Assert.False(session.Release(99, 0));
        Assert.True(session.Release(1, 0));
        Assert.Null(session.Equipped);
        Assert.Null(session.Roster.Find(1));
        Assert.Equal(1, session.Roster.Count);
        Assert.NotNull(session.Equipment.Artifact(EquipmentParts.ArtifactRed));
        Assert.True(session.Release(2, 0));
        Assert.Equal(0, session.Roster.Count);
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void CaptureWritesTheRosterTheEquippedPetAndTheArtifacts()
    {
        PetSession session = NewSession();
        session.Adopt(Items[950], 0);
        session.Adopt(Items[951], 0);
        session.Equip(2, 0, true);                  // 战斗中装备:消耗中
        session.EquipArtifact(BagWith(961), 0);
        session.GainExp(10, 0);                     // 蓝神器 20%:10 + 2 = 12

        var save = new HeroSave();
        session.Capture(save, 600);                 // 600 秒掉 10 点:100 → 90
        Assert.Equal(new[] { new SavedPet(1, 950, 0, 100), new SavedPet(2, 951, 12, 90) }, save.Pets);
        Assert.Equal(new[] { new SavedPetSlot(22, 2), new SavedPetSlot(24, 961) }, save.PetSlots);
    }

    [Fact]
    public void CaptureReplacesWhatTheSaveHeldBefore()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(7, 950, 1, 1));
        save.PetSlots.Add(new SavedPetSlot(22, 7));
        NewSession().Capture(save, 0);
        Assert.Empty(save.Pets);
        Assert.Empty(save.PetSlots);

        PetSession session = NewSession();
        session.Adopt(Items[950], 0);
        session.Capture(save, 0);
        session.Capture(save, 0);
        Assert.Single(save.Pets);
    }

    [Fact]
    public void ASessionSurvivesASaveAndLoadThroughTheTextFormat()
    {
        PetSession a = NewSession();
        a.Adopt(Items[950], 0);
        a.Adopt(Items[951], 0);
        a.Equip(2, 0, false);
        a.EquipArtifact(BagWith(960, 961), 0);
        a.EquipArtifact(BagWith(961), 0);
        a.GainExp(10, 0);                           // 30% → 13
        a.SetFighting(true, 0);
        var save = new HeroSave { Level = 5 };
        a.Capture(save, 600);                       // 饱食 100 → 90
        string text = save.ToText();

        Assert.True(HeroSave.TryParse(text, out HeroSave loaded, out string error), error);
        PetSession b = NewSession();
        PetRestoreReport report = b.Restore(loaded, Lookup, 10_000, false);
        Assert.Equal(2, report.Pets);
        Assert.Equal(0, report.PetsDropped);
        Assert.True(report.PetEquipped);
        Assert.Equal(2, report.Artifacts);
        Assert.Equal(0, report.ArtifactsDropped);

        Assert.Equal(2, b.Roster.Count);
        PetInstance one = b.Roster.Find(1);
        PetInstance two = b.Roster.Find(2);
        Assert.Equal((950, 0, 1, 100), (one.ItemCode, one.Exp, one.Level, one.Stomach(10_000)));
        Assert.Equal((951, 13, 2, 90), (two.ItemCode, two.Exp, two.Level, two.Stomach(10_000)));
        Assert.Same(two, b.Equipped);
        Assert.Equal(PetState.Charging, two.State);
        Assert.Equal(PetState.Stored, one.State);
        Assert.Equal(960, b.Equipment.Artifact(EquipmentParts.ArtifactRed).Code);
        Assert.Equal(961, b.Equipment.Artifact(EquipmentParts.ArtifactBlue).Code);
        Assert.Null(b.Equipment.Artifact(EquipmentParts.ArtifactGreen));

        // 再存一次,内容与第一次一致(没有饿死的宠物时往返是幂等的)。
        var again = new HeroSave { Level = 5 };
        b.Capture(again, 10_000);
        Assert.Equal(text, again.ToText());
    }

    [Fact]
    public void RestoreDropsWhatCannotBePlacedAndCountsIt()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(1, 950, 30, 80));    // 好的
        save.Pets.Add(new SavedPet(1, 951, 0, 100));    // 实例号重复
        save.Pets.Add(new SavedPet(2, 999, 0, 100));    // 物品编号找不到
        save.Pets.Add(new SavedPet(3, 970, 0, 100));    // 不是宠物物品
        save.Pets.Add(new SavedPet(4, 953, 0, 100));    // 物种不在列表里
        save.Pets.Add(new SavedPet(5, 952, 0, 100));    // 选择成长型,好的
        save.PetSlots.Add(new SavedPetSlot(22, 9));     // 宠物格指向不存在的实例
        save.PetSlots.Add(new SavedPetSlot(23, 960));   // 好的
        save.PetSlots.Add(new SavedPetSlot(24, 960));   // 红神器放进蓝格
        save.PetSlots.Add(new SavedPetSlot(25, 999));   // 物品编号找不到
        save.PetSlots.Add(new SavedPetSlot(23, 960));   // 同一格第二次
        save.PetSlots.Add(new SavedPetSlot(25, 970));   // 武器放进神器格

        PetSession session = NewSession();
        PetRestoreReport report = session.Restore(save, Lookup, 0, false);
        Assert.Equal(2, report.Pets);
        Assert.Equal(5, report.PetsDropped);
        Assert.False(report.PetEquipped);
        Assert.Equal(1, report.Artifacts);
        Assert.Equal(4, report.ArtifactsDropped);
        Assert.Equal(new[] { 1, 5 }, session.Roster.Pets.Select(p => p.Uid).ToArray());
        Assert.Null(session.Equipped);
        Assert.Equal(PetKind.Grow, session.Roster.Find(5).Kind);
        Assert.Equal(1, session.Equipment.ArtifactCountEquipped);
    }

    [Fact]
    public void ARepeatedPetSlotRowKeepsTheFirstPetAndDropsTheLaterOne()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(1, 950, 0, 100));
        save.Pets.Add(new SavedPet(2, 951, 0, 100));
        save.PetSlots.Add(new SavedPetSlot(22, 1));
        save.PetSlots.Add(new SavedPetSlot(22, 2));

        PetSession session = NewSession();
        PetRestoreReport report = session.Restore(save, Lookup, 0, false);
        Assert.True(report.PetEquipped);
        Assert.Equal(1, session.Equipped.Uid);
        Assert.Equal(PetState.Charging, session.Equipped.State);
        Assert.Equal(PetState.Stored, session.Roster.Find(2).State);
        Assert.Equal(2, report.Pets);
        Assert.Equal(1, report.PetsDropped);
    }

    [Fact]
    public void ARowWithAnUnknownItemIsDroppedEvenIfTheCatalogHasAPetWithKeyZero()
    {
        // 找不到物品、或物品不是宠物物品时,这一行就丢;不能退而求其次拿目录里 0 号宠物的定义去还原。
        Fixture fx = BuildFixture()
            .List(PetCatalog.ListPath, (0, "p000.cre"), (901, "p901.cre"), (902, "p902.cre"), (903, "p903.cre"))
            .Tokens("creature/p000.cre", w => w.Tag("[max level]").Int(5));
        var session = new PetSession(new PetCatalog(new GameData(fx.Build())));
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(1, 999, 0, 100));   // 物品编号找不到
        save.Pets.Add(new SavedPet(2, 970, 0, 100));   // 武器,不是宠物物品
        save.Pets.Add(new SavedPet(3, 950, 0, 100));   // 好的

        PetRestoreReport report = session.Restore(save, Lookup, 0, false);
        Assert.Equal(1, report.Pets);
        Assert.Equal(2, report.PetsDropped);
        Assert.Equal(new[] { 3 }, session.Roster.Pets.Select(p => p.Uid).ToArray());
    }

    [Fact]
    public void AdoptingAfterARestoreTakesTheNumberAboveTheHighestEvenWhenThereAreGaps()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(7, 950, 0, 100));
        PetSession session = NewSession();
        session.Restore(save, Lookup, 0, false);

        PetInstance fresh = session.Adopt(Items[951], 0);
        Assert.NotNull(fresh);
        Assert.Equal(8, fresh.Uid);
        Assert.Equal(new[] { 7, 8 }, session.Roster.Pets.Select(p => p.Uid).ToArray());
    }

    [Fact]
    public void RestoreReplacesWhateverTheSessionHeld()
    {
        PetSession session = NewSession();
        session.Adopt(Items[950], 0);
        session.Equip(1, 0, false);
        session.EquipArtifact(BagWith(960), 0);

        PetRestoreReport report = session.Restore(new HeroSave(), Lookup, 0, false);
        Assert.Equal(0, report.Pets);
        Assert.Equal(0, session.Roster.Count);
        Assert.Null(session.Equipped);
        Assert.Equal(0, session.Equipment.ArtifactCountEquipped);
    }

    [Fact]
    public void ArtifactsAreRestoredEvenWithoutAnEquippedPet()
    {
        var save = new HeroSave();
        save.PetSlots.Add(new SavedPetSlot(24, 961));
        PetSession session = NewSession();
        PetRestoreReport report = session.Restore(save, Lookup, 0, false);
        Assert.Equal(1, report.Artifacts);
        Assert.Null(session.Equipped);
        Assert.NotNull(session.Equipment.Artifact(EquipmentParts.ArtifactBlue));
        Assert.Equal(0f, session.Equipment.ExperienceBonus(session.Catalog.ItemData));
    }

    [Fact]
    public void AStarvedEquippedPetIsRevivedAtLoadWhenTheUserIsNotFighting()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(1, 951, 40, 0));
        save.PetSlots.Add(new SavedPetSlot(22, 1));

        PetSession calm = NewSession();
        calm.Restore(save, Lookup, 0, false);
        Assert.Equal(3, calm.Equipped.Level);
        Assert.Equal(PetState.Charging, calm.Equipped.State);
        Assert.Equal(1, calm.Equipped.Stomach(0));
        Assert.False(calm.Equipped.IsStarved(0));

        // 在战斗中读档:进消耗中,不复活。
        PetSession fighting = NewSession();
        fighting.Restore(save, Lookup, 0, true);
        Assert.Equal(PetState.Draining, fighting.Equipped.State);
        Assert.True(fighting.Equipped.IsStarved(0));
        Assert.Equal(0, fighting.Equipped.Stomach(0));
    }

    [Fact]
    public void TheClockStartsAtTheRestoreMomentSoOfflineTimeIsNotCounted()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(1, 950, 0, 50));
        save.PetSlots.Add(new SavedPetSlot(22, 1));
        PetSession session = NewSession();
        session.Restore(save, Lookup, 5000, true);
        Assert.Equal(50, session.Equipped.Stomach(5000));
        Assert.Equal(40, session.Equipped.Stomach(5600));
    }

    [Fact]
    public void WithoutTheExpTableEveryPetRowIsDroppedWithoutFailing()
    {
        var save = new HeroSave();
        save.Pets.Add(new SavedPet(1, 950, 0, 50));
        save.Pets.Add(new SavedPet(2, 951, 0, 50));
        save.PetSlots.Add(new SavedPetSlot(22, 1));
        PetSession session = NewSession(withTable: false);
        PetRestoreReport report = session.Restore(save, Lookup, 0, false);
        Assert.Equal(0, report.Pets);
        Assert.Equal(3, report.PetsDropped);
        Assert.False(report.PetEquipped);
        Assert.Equal(0, session.Roster.Count);
    }

    [Fact]
    public void MissingArgumentsAreRejected()
    {
        PetSession session = NewSession();
        Assert.Throws<ArgumentNullException>(() => new PetSession(null));
        Assert.Throws<ArgumentNullException>(() => session.Capture(null, 0));
        Assert.Throws<ArgumentNullException>(() => session.Restore(null, Lookup, 0, false));
        Assert.Throws<ArgumentNullException>(() => session.Restore(new HeroSave(), null, 0, false));
    }
}
