using Dnf.Game;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 角色进度存档(M26,本项目自己的设计,没有原版证据):文本格式、快照与还原、文件与内存存放、存档员。物品全是合成的,存档里只有编号。
/// </summary>
public class HeroSaveTests
{
    private static ItemInfo Gear(int code, int part = EquipmentParts.Weapon, int level = 1, string job = "[swordman]")
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level, Grade = 10, Rarity = 2, Price = 300 };
        item.UsableJobs.Add(job);
        return item;
    }

    private static ItemInfo Potion(int code, int limit = 99)
    {
        return new ItemInfo(code, "stackable/p" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste, StackLimit = limit };
    }

    private static ItemInfo Ore(int code)
    {
        return new ItemInfo(code, "stackable/o" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Material, StackLimit = 50 };
    }

    /// <summary>编号 1 到 59 是装备(部位按编号轮流,等级 1)、编号 100 是药水、101 是材料,其余不认得。</summary>
    private static ItemInfo Lookup(int code)
    {
        if (code >= 1 && code <= 99)
        {
            return Gear(code, EquipmentSlots.First + (code - 1) % EquipmentSlots.Count);
        }
        if (code == 100)
        {
            return Potion(100);
        }
        return code == 101 ? Ore(101) : null;
    }

    private static HeroSave Full()
    {
        var save = new HeroSave
        {
            Job = 0,
            Level = 12,
            Exp = 3456,
            SkillPoints = 7,
            AwakeningPoints = 2,
            LevelUps = 11,
            TotalGained = 9_000_000_000L,
            Gold = 123456789012L,
            GoldGained = 223456789012L,
            ItemsGained = 31,
        };
        save.Bag.Add(new SavedStack(5, 1));
        save.Bag.Add(new SavedStack(100, 60));
        save.Bag.Add(new SavedStack(100, 40));
        save.Bag.Add(new SavedStack(101, 3));
        save.Worn.Add(new SavedWorn(EquipmentSlots.First, 1));
        save.Worn.Add(new SavedWorn(EquipmentSlots.First + 2, 3));
        return save;
    }

    private static void AssertSame(HeroSave a, HeroSave b)
    {
        Assert.Equal(a.ToText(), b.ToText());
        Assert.Equal(a.Bag.Select(s => (s.Code, s.Count)), b.Bag.Select(s => (s.Code, s.Count)));
        Assert.Equal(a.Worn.Select(w => (w.Part, w.Code)), b.Worn.Select(w => (w.Part, w.Code)));
    }

    // ------------------------------------------------------------------ 文本格式

    [Fact]
    public void ToTextWritesTheHeaderThenTheKeysInAFixedOrder()
    {
        string expected = "DNFUNITY-SAVE 1\n" + "job 0\n" + "level 12\n" + "exp 3456\n" + "sp 7\n" + "ap 2\n" + "levelups 11\n" + "gained 9000000000\n"
            + "gold 123456789012\n" + "goldgained 223456789012\n" + "itemsgained 31\n" + "bag 5 1\n" + "bag 100 60\n" + "bag 100 40\n" + "bag 101 3\n" + "worn 10 1\n" + "worn 12 3\n";
        Assert.Equal(expected, Full().ToText());
        Assert.Equal("DNFUNITY-SAVE 1\njob 0\nlevel 1\nexp 0\nsp 0\nap 0\nlevelups 0\ngained 0\ngold 0\ngoldgained 0\nitemsgained 0\n", new HeroSave().ToText());
    }

    [Fact]
    public void TextRoundTripKeepsEveryFieldAndTheOrderOfTheLists()
    {
        HeroSave original = Full();
        Assert.True(HeroSave.TryParse(original.ToText(), out HeroSave back, out string error), error);
        Assert.Null(error);
        AssertSame(original, back);
        Assert.Equal((0, 12, 3456, 7, 2, 11), (back.Job, back.Level, back.Exp, back.SkillPoints, back.AwakeningPoints, back.LevelUps));
        Assert.Equal((9_000_000_000L, 123456789012L, 223456789012L, 31), (back.TotalGained, back.Gold, back.GoldGained, back.ItemsGained));
    }

    [Fact]
    public void ParseIgnoresBlankLinesCommentsUnknownKeysAndWindowsLineEndings()
    {
        string text = "\r\n# 手改过的存档\r\nDNFUNITY-SAVE 1\r\n\r\nlevel 9\r\nfuture 1 2 3\r\n  # 缩进的注释\r\nexp 77\r\nbag 100 5\r\n";
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        Assert.Equal((9, 77), (save.Level, save.Exp));
        Assert.Equal(new[] { (100, 5) }, save.Bag.Select(s => (s.Code, s.Count)));
    }

    [Fact]
    public void ParseTakesTheLastValueOfARepeatedScalarKeyAndKeepsEveryBagLine()
    {
        Assert.True(HeroSave.TryParse("DNFUNITY-SAVE 1\nlevel 3\nlevel 4\nbag 5 1\nbag 5 1\n", out HeroSave save, out _));
        Assert.Equal(4, save.Level);
        Assert.Equal(2, save.Bag.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n# 只有注释\n")]
    [InlineData("level 5\n")]
    [InlineData("SOMETHING 1\nlevel 5\n")]
    [InlineData("DNFUNITY-SAVE\nlevel 5\n")]
    [InlineData("DNFUNITY-SAVE x\nlevel 5\n")]
    [InlineData("DNFUNITY-SAVE 0\nlevel 5\n")]
    [InlineData("DNFUNITY-SAVE 1 2\nlevel 5\n")]
    public void ParseRejectsAMissingOrBrokenHeader(string text)
    {
        Assert.False(HeroSave.TryParse(text, out HeroSave save, out string error));
        Assert.Null(save);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ParseRefusesASaveFromANewerVersion()
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE " + (HeroSave.CurrentVersion + 1) + "\nlevel 5\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("新", error);
    }

    [Theory]
    [InlineData("level x", 2)]
    [InlineData("level 0", 2)]
    [InlineData("level", 2)]
    [InlineData("level 5 6", 2)]
    [InlineData("exp -1", 2)]
    [InlineData("exp 3000000000", 2)]
    [InlineData("gold -5", 2)]
    [InlineData("gold 9223372036854775808", 2)]
    [InlineData("gold 1.5", 2)]
    [InlineData("job -1", 2)]
    [InlineData("bag 5", 2)]
    [InlineData("bag 5 0", 2)]
    [InlineData("bag 5 1 1", 2)]
    [InlineData("bag a 1", 2)]
    [InlineData("worn 10", 2)]
    [InlineData("worn 10 -1", 2)]
    [InlineData("sp 1 1", 2)]
    public void ParseRejectsAKnownKeyWithABrokenValueAndNamesTheLine(string line, int lineNumber)
    {
        string text = "DNFUNITY-SAVE 1\n" + line + "\nlevel 5\n";
        Assert.False(HeroSave.TryParse(text, out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("第 " + lineNumber + " 行", error);
    }

    [Fact]
    public void ParseAcceptsTheLargestValuesTheFieldsHold()
    {
        string text = "DNFUNITY-SAVE 1\ngold 9223372036854775807\nexp 2147483647\nbag 2147483647 2147483647\n";
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        Assert.Equal((long.MaxValue, int.MaxValue), (save.Gold, save.Exp));
        Assert.Equal((int.MaxValue, int.MaxValue), (save.Bag[0].Code, save.Bag[0].Count));
    }

    // ------------------------------------------------------------------ 快照与还原

    [Fact]
    public void SnapshotListsTheBagTabByTabAndTheWornPartsInOrder()
    {
        HeroProgress p = Progress(level: 6);
        p.AddGold(500);
        p.AddItem(Potion(100), 20);
        p.AddItem(Ore(101), 7);
        p.AddItem(Gear(3, EquipmentSlots.First + 2));
        p.AddItem(Gear(1, EquipmentSlots.First));
        p.AddItem(Gear(8, EquipmentSlots.First + 7));
        Assert.Equal(EquipResult.Ok, p.Equip(1));                 // 装备页现在是 [3, 1, 8]:第二格是武器(编号 1),穿上后剩 [3, 8]
        Assert.Equal(EquipResult.Ok, p.Equip(1));                 // 第二格是部位 17 的编号 8,穿上后剩 [3]
        HeroSave save = p.Snapshot();
        Assert.Equal((0, 6), (save.Job, save.Level));
        Assert.Equal(500L, save.Gold);
        // 背包:装备页在前(剩下没穿的),再消耗品页、材料页
        Assert.Equal(new[] { 3, 100, 101 }, save.Bag.Select(s => s.Code));
        Assert.Equal(new[] { 1, 20, 7 }, save.Bag.Select(s => s.Count));
        // 装备栏:部位从小到大
        Assert.Equal(new[] { (EquipmentSlots.First, 1), (EquipmentSlots.First + 7, 8) }, save.Worn.Select(w => (w.Part, w.Code)));
    }

    [Fact]
    public void RestoreBringsBackTheLevelExpPointsGoldAndCounters()
    {
        HeroProgress p = Progress();
        var save = new HeroSave { Level = 4, Exp = Table().LevelExp(4) + 25, SkillPoints = 9, AwakeningPoints = 1, LevelUps = 3, TotalGained = 1234, Gold = 800, GoldGained = 1500, ItemsGained = 12 };
        int versionBefore = p.Version;
        SaveApplyReport report = p.Restore(save, Lookup);
        Assert.Equal((4, Table().LevelExp(4) + 25, 9, 1, 3), (p.Level, p.Exp, p.SkillPoints, p.AwakeningPoints, p.LevelUps));
        Assert.Equal((1234L, 800L, 1500L, 12), (p.TotalGained, p.Gold, p.GoldGained, p.ItemsGained));
        Assert.True(p.Version > versionBefore);                   // 其它房间的战斗会话据此重取等级
        Assert.Equal((0, 0, 0, 0), (report.BagStacks, report.WornItems, report.Moved, report.Dropped));
    }

    [Fact]
    public void RestoreKeepsTheBagLayoutIncludingTwoPartialStacksOfTheSameItem()
    {
        HeroProgress p = Progress();
        HeroSave save = Full();
        SaveApplyReport report = p.Restore(save, Lookup);
        IReadOnlyList<ItemStack> use = p.Bag.Tab(InventoryTab.Consumable);
        Assert.Equal(new[] { 60, 40 }, use.Select(s => s.Count));           // 没有并成 99 + 1
        Assert.Equal(new[] { 100, 100 }, use.Select(s => s.Info.Code));
        Assert.Equal(new[] { 101 }, p.Bag.Tab(InventoryTab.Material).Select(s => s.Info.Code));
        Assert.Equal(new[] { 5 }, p.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        Assert.Equal(4, report.BagStacks);
        Assert.Equal(0, report.Dropped);
    }

    [Fact]
    public void RestoreSplitsAStackThatNowExceedsTheStackLimit()
    {
        var p = Progress();
        var save = new HeroSave();
        save.Bag.Add(new SavedStack(101, 120));                    // 材料一格最多 50
        SaveApplyReport report = p.Restore(save, Lookup);
        Assert.Equal(new[] { 50, 50, 20 }, p.Bag.Tab(InventoryTab.Material).Select(s => s.Count));
        Assert.Equal((1, 0), (report.BagStacks, report.Dropped));
    }

    [Fact]
    public void RestoreWearsTheSavedEquipmentAndBumpsTheEquipVersion()
    {
        HeroProgress p = Progress(level: 5);
        int versionBefore = p.EquipVersion;
        SaveApplyReport report = p.Restore(Full(), Lookup);
        Assert.Equal(2, p.EquippedCount);
        Assert.Equal("equipment/test/g1.equ", p.WeaponPath);
        Assert.Equal(3, p.Worn.Get(EquipmentSlots.First + 2).Code);
        Assert.True(p.EquipVersion > versionBefore);
        Assert.Equal((2, 0), (report.WornItems, report.Moved));
        Assert.Single(p.Bag.Tab(InventoryTab.Equipment));                   // 穿着的不在背包里,背包里只剩编号 5 的
    }

    [Fact]
    public void RestoreMovesWornItemsThatCannotBeWornAnymoreBackIntoTheBag()
    {
        var save = new HeroSave { Level = 10 };
        save.Worn.Add(new SavedWorn(EquipmentSlots.First, 1));                       // 编号 1:武器(合成物品 1 级、剑魂)
        save.Worn.Add(new SavedWorn(EquipmentSlots.First + 1, 2));                   // 编号 2:部位 11,放在这里没问题
        save.Worn.Add(new SavedWorn(EquipmentSlots.First + 5, 2));                   // 部位对不上(编号 2 是部位 11):不能穿
        save.Worn.Add(new SavedWorn(EquipmentSlots.First, 4));                       // 武器位已经有一件了:放回背包
        HeroProgress p = Progress();
        SaveApplyReport report = p.Restore(save, code => code == 4 ? Gear(4, EquipmentSlots.First) : Lookup(code));
        Assert.Equal((2, 2, 0), (p.EquippedCount, report.Moved, report.Dropped));
        Assert.Equal(new[] { 2, 4 }, p.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));

        // 等级不够:存档里写 5 级却穿着 20 级的武器(手改或数据变了)
        var low = new HeroSave { Level = 5 };
        low.Worn.Add(new SavedWorn(EquipmentSlots.First, 9));
        p = Progress();
        report = p.Restore(low, code => Gear(code, EquipmentSlots.First, 20));
        Assert.Equal((0, 1), (p.EquippedCount, report.Moved));
        Assert.Equal(new[] { 9 }, p.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
    }

    [Fact]
    public void RestoreDropsWhatItCannotFindAndCountsIt()
    {
        var save = new HeroSave();
        save.Bag.Add(new SavedStack(100, 5));
        save.Bag.Add(new SavedStack(777, 5));                   // 数据里没有
        save.Worn.Add(new SavedWorn(EquipmentSlots.First, 888));
        HeroProgress p = Progress();
        SaveApplyReport report = p.Restore(save, Lookup);
        Assert.Equal((1, 0, 0, 2), (report.BagStacks, report.WornItems, report.Moved, report.Dropped));
        Assert.Equal(5, p.Bag.CountOf(100));
    }

    [Fact]
    public void RestoreDropsWhatDoesNotFitInAFullTab()
    {
        var save = new HeroSave();
        for (int code = 1; code <= 60; code++)
        {
            save.Bag.Add(new SavedStack(code, 1));
        }
        HeroProgress p = Progress();
        SaveApplyReport report = p.Restore(save, Lookup);
        Assert.Equal(Inventory.SlotsPerTab, p.Bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal((Inventory.SlotsPerTab, 60 - Inventory.SlotsPerTab), (report.BagStacks, report.Dropped));
    }

    [Fact]
    public void RestoreClampsTheLevelAndRaisesTheExpToTheFloorOfTheLevel()
    {
        HeroProgress p = Progress();
        p.Restore(new HeroSave { Level = 5000, Exp = 0 }, Lookup);
        Assert.Equal(Leveling.MaxLevel, p.Level);
        Assert.True(p.Exp >= p.Table.LevelExp(Leveling.MaxLevel));
        p.Restore(new HeroSave { Level = 7, Exp = 1 }, Lookup);                // 经验比这一级的门槛还低:抬到门槛
        Assert.Equal((7, p.Table.LevelExp(7)), (p.Level, p.Exp));
        p.Restore(new HeroSave { Job = 99 }, Lookup);                          // 职业越界:回到剑魂
        Assert.Equal(EquipRules.SwordmanJob, p.Job);
    }

    [Fact]
    public void RestoreKeepsGoldGainedAtLeastTheGoldInThePurse()
    {
        HeroProgress p = Progress();
        p.Restore(new HeroSave { Gold = 900, GoldGained = 100 }, Lookup);      // 手改成了"赚的比身上的还少"
        Assert.Equal((900L, 900L), (p.Gold, p.GoldGained));
    }

    [Fact]
    public void RestoreReplacesWhateverWasThereBefore()
    {
        HeroProgress p = Progress(level: 3);
        p.AddGold(10);
        p.AddItem(Potion(100), 9);
        p.AddItem(Gear(1));
        p.Equip(0);
        Assert.Equal(1, p.EquippedCount);
        p.Restore(new HeroSave { Level = 2 }, Lookup);
        Assert.Equal((2, 0L, 0, 0), (p.Level, p.Gold, p.EquippedCount, p.BagItems));
        Assert.Null(p.WeaponPath);
    }

    [Fact]
    public void SnapshotTextRestoreSnapshotGivesTheSameText()
    {
        HeroProgress a = Progress(level: 8);
        a.Gain(5000);
        a.AddGold(4242);
        a.AddItem(Potion(100), 130);                                           // 两格:99 + 31
        a.AddItem(Ore(101), 4);
        a.AddItem(Gear(5, EquipmentSlots.First + 4));
        a.AddItem(Gear(7, EquipmentSlots.First + 6));
        a.AddItem(Gear(2, EquipmentSlots.First + 1));
        Assert.Equal(EquipResult.Ok, a.Equip(1));
        Assert.Equal(EquipResult.Ok, a.Equip(0));
        string text = a.Snapshot().ToText();

        Assert.True(HeroSave.TryParse(text, out HeroSave parsed, out string error), error);
        HeroProgress b = Progress();
        SaveApplyReport report = b.Restore(parsed, Lookup);
        Assert.Equal(0, report.Dropped);
        Assert.Equal(text, b.Snapshot().ToText());
        Assert.Equal((a.Level, a.Exp, a.Gold, a.EquippedCount, a.BagItems, a.BagSlots), (b.Level, b.Exp, b.Gold, b.EquippedCount, b.BagItems, b.BagSlots));
    }

    [Fact]
    public void RestoreRejectsMissingArguments()
    {
        HeroProgress p = Progress();
        Assert.Throws<ArgumentNullException>(() => p.Restore(null, Lookup));
        Assert.Throws<ArgumentNullException>(() => p.Restore(new HeroSave(), null));
    }

    [Fact]
    public void InventoryClearAndRestoreStackAreExactAndStopAtAFullTab()
    {
        var bag = new Inventory(slotsPerTab: 3);
        Assert.Equal(5, bag.RestoreStack(Potion(100, limit: 99), 5));
        Assert.Equal(5, bag.RestoreStack(Potion(100, limit: 99), 5));          // 同编号也开新格
        Assert.Equal(2, bag.Tab(InventoryTab.Consumable).Count);
        Assert.Equal(0, bag.RestoreStack(null, 5));
        Assert.Equal(0, bag.RestoreStack(Potion(100), 0));
        Assert.Equal(10, bag.RestoreStack(Potion(100, limit: 10), 20));        // 只剩 1 格、一格最多 10:只放得下 10
        Assert.Equal(0, bag.RestoreStack(Potion(101, limit: 10), 25));         // 这一页满了
        Assert.Equal(3, bag.Tab(InventoryTab.Consumable).Count);
        bag.Clear();
        Assert.Equal(0, bag.StackCount);
    }

    [Fact]
    public void EquipmentSlotsClearAndRestoreCountAsChangesOnlyWhenSomethingChanges()
    {
        var slots = new EquipmentSlots();
        int v0 = slots.Version;
        slots.Clear();
        Assert.Equal(v0, slots.Version);
        Assert.False(slots.Restore(0, Gear(1)));                               // 部位 0 没有格子
        Assert.False(slots.Restore(EquipmentSlots.First, null));
        Assert.True(slots.Restore(EquipmentSlots.First, Gear(1)));
        Assert.Equal(v0 + 1, slots.Version);
        slots.Clear();
        Assert.Equal((v0 + 2, 0), (slots.Version, slots.EquippedCount));
    }

    // ------------------------------------------------------------------ 存放与存档员

    [Fact]
    public void MemoryStoreLoadsWhatWasSavedAndReportsBrokenText()
    {
        var store = new MemoryHeroSaveStore();
        Assert.Null(store.Load(out string note));
        Assert.Null(note);
        Assert.True(store.Save(Full(), out string error));
        Assert.Null(error);
        Assert.Equal(1, store.Writes);
        AssertSame(Full(), store.Load(out note));
        Assert.Null(note);
        store.Text = "不是存档";
        Assert.Null(store.Load(out note));
        Assert.False(string.IsNullOrEmpty(note));
        store.FailWrites = true;
        Assert.False(store.Save(Full(), out error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnfunity-save-" + Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    [Fact]
    public void FileStoreRoundTripsCreatesTheDirectoryAndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        string file = System.IO.Path.Combine(dir.Path, "sub", "hero.sav");
        var store = new FileHeroSaveStore(file);
        Assert.Null(store.Load(out string note));                              // 没有文件:没有存档、没有提示
        Assert.Null(note);
        Assert.True(store.Save(Full(), out string error), error);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".tmp"));
        AssertSame(Full(), store.Load(out note));
        Assert.Null(note);

        HeroSave second = Full();
        second.Gold = 5;
        Assert.True(store.Save(second, out error), error);                     // 第二次:换掉第一次的
        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal(5, store.Load(out note).Gold);
        Assert.Equal(new[] { "hero.sav" }, Directory.GetFiles(System.IO.Path.GetDirectoryName(file)!).Select(f => System.IO.Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void FileStoreCopiesABrokenFileToBadKeepsTheFirstOneAndTreatsItAsNoSave()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string file = System.IO.Path.Combine(dir.Path, "hero.sav");
        File.WriteAllText(file, "DNFUNITY-SAVE 1\nlevel abc\n");
        var store = new FileHeroSaveStore(file);
        Assert.Null(store.Load(out string note));
        Assert.Contains(".bad", note);
        Assert.Equal("DNFUNITY-SAVE 1\nlevel abc\n", File.ReadAllText(file + ".bad"));

        File.WriteAllText(file, "另一份坏的");
        Assert.Null(store.Load(out note));
        Assert.Equal("DNFUNITY-SAVE 1\nlevel abc\n", File.ReadAllText(file + ".bad"));      // 留下最早坏掉的那份
    }

    [Fact]
    public void FileStoreRejectsAnEmptyPathAndANullSave()
    {
        Assert.Throws<ArgumentException>(() => new FileHeroSaveStore(string.Empty));
        using var dir = new TempDir();
        var store = new FileHeroSaveStore(System.IO.Path.Combine(dir.Path, "x.sav"));
        Assert.False(store.Save(null, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void FileStoreReportsAWriteThatCannotHappenInsteadOfThrowing()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string blocker = System.IO.Path.Combine(dir.Path, "file");
        File.WriteAllText(blocker, "占着名字");
        var store = new FileHeroSaveStore(System.IO.Path.Combine(blocker, "hero.sav"));     // "目录"其实是个文件
        Assert.False(store.Save(Full(), out string error));
        Assert.Contains("失败", error);
    }

    [Fact]
    public void SaverFlushWritesOnlyWhenSomethingChanged()
    {
        HeroProgress p = Progress();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, p);
        Assert.True(saver.Flush(0));                                           // 第一次:存档里还没有这个状态
        Assert.False(saver.Flush(1));                                          // 没变:不写
        p.AddGold(10);
        Assert.True(saver.Flush(2));
        Assert.Equal((2, 2), (saver.Saves, store.Writes));
        Assert.Null(saver.LastError);
        Assert.Equal(10, store.Load(out _).Gold);
    }

    [Fact]
    public void SaverTreatsTheLoadedSaveAsAlreadySavedUntilSomethingChanges()
    {
        HeroProgress p = Progress();
        p.Restore(Full(), Lookup);
        var store = new MemoryHeroSaveStore { Text = p.Snapshot().ToText() };
        var saver = new HeroSaver(store, p, store.Load(out _));
        Assert.False(saver.Flush(0));
        Assert.Equal(0, store.Writes);
        p.Gain(10);
        Assert.True(saver.Flush(1));
    }

    [Fact]
    public void SaverTickChecksOnlyOncePerIntervalAndSkipsUnchangedState()
    {
        HeroProgress p = Progress();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, p) { IntervalSeconds = 10 };
        Assert.True(saver.Tick(100));                                          // 第一次一定查
        p.AddGold(1);
        Assert.False(saver.Tick(105));                                         // 没到间隔:不查
        Assert.True(saver.Tick(110));                                          // 到了:有变化,写
        Assert.False(saver.Tick(125));                                         // 到了:没变化,不写
        p.AddGold(1);
        Assert.False(saver.Tick(126));
        Assert.True(saver.Tick(135));
        Assert.Equal(3, store.Writes);
    }

    [Fact]
    public void SaverKeepsTheChangeForTheNextTryWhenAWriteFails()
    {
        HeroProgress p = Progress();
        var store = new MemoryHeroSaveStore { FailWrites = true };
        var saver = new HeroSaver(store, p);
        p.AddGold(5);
        Assert.False(saver.Flush(0));
        Assert.False(string.IsNullOrEmpty(saver.LastError));
        Assert.Equal(0, saver.Saves);
        store.FailWrites = false;
        Assert.True(saver.Flush(1));                                           // 下一次还会再试
        Assert.Null(saver.LastError);
        Assert.Equal(1, saver.Saves);
    }

    [Fact]
    public void SaverRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new HeroSaver(null, Progress()));
        Assert.Throws<ArgumentNullException>(() => new HeroSaver(new MemoryHeroSaveStore(), null));
    }

    // ------------------------------------------------------------------ 选角名册

    [Fact]
    public void TheLocalRosterShowsTheSavedLevel()
    {
        Assert.Equal(1, CharacterRoster.Local()[0].Level);
        CharacterRoster roster = CharacterRoster.Local(37);
        Assert.Equal(1, roster.Count);
        Assert.Equal(37, roster[0].Level);
        Assert.Equal("LV37 HERO", roster[0].Caption);
        Assert.Equal(1, CharacterRoster.Local(0)[0].Level);                    // 等级至少 1
    }
}
