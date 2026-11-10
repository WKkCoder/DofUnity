using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.ExpTests;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 穿着装备的基础属性汇总进主角的属性出口:<see cref="HeroProgress.BaseStats"/>(只有等级)、
/// <see cref="HeroProgress.WornStats"/>(穿着的装备汇总,全部项)、<see cref="HeroProgress.Stats"/>(两者合起来,战斗会话读的就是它),
/// <see cref="HeroProgress.Version"/> 随装备栏一起变,会话据此换生命上限、防御与攻击面板。数据全是合成的(物品编号 901 起,数值是随手写的小整数):
/// 成长表见 <see cref="ExpTests"/>(1 级生命 1000、魔法 500、物攻 50、魔攻 40、物防 20、魔防 10……,十分之一单位的整数存 10 倍)。
/// </summary>
public class HeroEquipStatsTests
{
    private const string RingPath = AvatarFixture.EquipRoot + "/" + AvatarFixture.SecondWeaponEqu;      // 借夹具里带攻击力的脚本当路径,给面板的旧读法用

    private static ItemInfo Gear(int code, int part, EquipStats stats, string job = "[swordman]", int level = 1)
    {
        var item = new ItemInfo(code, RingPath, ItemKind.Equipment) { Part = part, MinimumLevel = level, Stats = stats };
        item.UsableJobs.Add(job);
        return item;
    }

    private static (long, long, long, long, long, long, long, long, long) Fields(StatBlock s)
    {
        return (s.HpMax, s.MpMax, s.PhysicalAttack, s.PhysicalDefense, s.MagicalAttack, s.MagicalDefense, s.InventoryLimit, s.MpRegenSpeed, s.HitRecovery);
    }

    /// <summary>一个带"九项里的几项"的戒指:生命 +20、魔法 +7、物攻 +5、魔攻 +6、物防 +3、装备魔防 +4(第二个数 1),外加一项不在九项里的移动速度 +9。</summary>
    private static ItemInfo Ring(int code = 901)
    {
        return Gear(code, EquipmentParts.Ring, EquipStats.Empty
            .With(EquipStat.HpMax, 20).With(EquipStat.MpMax, 7).With(EquipStat.PhysicalAttack, 5).With(EquipStat.MagicalAttack, 6)
            .With(EquipStat.PhysicalDefense, 3).With(EquipStat.EquipmentMagicalDefense, 4, 1).With(EquipStat.MoveSpeed, 9));
    }

    private static StatBlock RingBlock()
    {
        return Ring().Stats.ToStatBlock();
    }

    // ------------------------------------------------------------------ HeroProgress

    [Fact]
    public void NothingWornMeansTheStatsAreTheLevelStats()
    {
        HeroProgress progress = Progress();
        Assert.Equal(Fields(progress.BaseStats), Fields(progress.Stats));
        Assert.True(progress.WornStats.IsEmpty);
        Assert.Equal((10000L, 5000L, 500L, 400L), (progress.Stats.HpMax, progress.Stats.MpMax, progress.Stats.PhysicalAttack, progress.Stats.MagicalAttack));   // 1 级:1000、500、50、40
    }

    [Fact]
    public void WearingAddsTheSameNameStatsAndTheEquipmentDefenseAndExposesTheRestThroughWornStats()
    {
        HeroProgress progress = Progress();
        StatBlock b = progress.BaseStats;
        progress.AddItem(Ring());
        Assert.Equal(Fields(b), Fields(progress.Stats));                                        // 在背包里不算
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        Assert.Equal((b.HpMax + 200, b.MpMax + 70, b.PhysicalAttack + 50, b.PhysicalDefense + 30, b.MagicalAttack + 60, b.MagicalDefense + 40),
            (progress.Stats.HpMax, progress.Stats.MpMax, progress.Stats.PhysicalAttack, progress.Stats.PhysicalDefense, progress.Stats.MagicalAttack, progress.Stats.MagicalDefense));
        Assert.Equal((b.InventoryLimit, b.MpRegenSpeed, b.HitRecovery), (progress.Stats.InventoryLimit, progress.Stats.MpRegenSpeed, progress.Stats.HitRecovery));
        Assert.Equal(Fields(b), Fields(progress.BaseStats));                                    // 等级属性不动
        Assert.Equal((9.0, 7), (progress.WornStats[EquipStat.MoveSpeed], progress.WornStats.Count));   // 九项以外的项从 WornStats 取
        Assert.Equal((4.0, 1.0), (progress.WornStats[EquipStat.EquipmentMagicalDefense], progress.WornStats.SecondOf(EquipStat.EquipmentMagicalDefense)));
    }

    [Fact]
    public void TakingOffDeductsExactlyWhatWearingAdded()
    {
        HeroProgress progress = Progress();
        StatBlock b = progress.BaseStats;
        progress.AddItem(Ring());
        progress.Equip(0);
        Assert.NotEqual(Fields(b), Fields(progress.Stats));
        Assert.True(progress.Unequip(EquipmentParts.Ring));
        Assert.Equal(Fields(b), Fields(progress.Stats));
        Assert.True(progress.WornStats.IsEmpty);
        Assert.False(progress.Unequip(EquipmentParts.Ring));                                    // 脱第二次什么也不会扣
        Assert.Equal(Fields(b), Fields(progress.Stats));
    }

    [Fact]
    public void ReplacingTheItemInASlotKeepsOnlyTheNewOnesStats()
    {
        HeroProgress progress = Progress();
        StatBlock b = progress.BaseStats;
        ItemInfo first = Gear(901, EquipmentParts.Ring, EquipStats.Empty.With(EquipStat.HpMax, 10).With(EquipStat.AttackSpeed, 4));
        ItemInfo second = Gear(902, EquipmentParts.Ring, EquipStats.Empty.With(EquipStat.HpMax, 7).With(EquipStat.MpMax, 3));
        progress.AddItem(first);
        progress.AddItem(second);
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        Assert.Equal((b.HpMax + 100, 4.0), (progress.Stats.HpMax, progress.WornStats[EquipStat.AttackSpeed]));
        Assert.Equal(EquipResult.Ok, progress.Equip(0));                                        // 换成 second;first 回背包
        Assert.Equal((b.HpMax + 70, b.MpMax + 30, 0.0, false), (progress.Stats.HpMax, progress.Stats.MpMax, progress.WornStats[EquipStat.AttackSpeed], progress.WornStats.Has(EquipStat.AttackSpeed)));
        Assert.Equal(1, progress.EquippedCount);
    }

    [Fact]
    public void ARefusedEquipChangesNeitherTheStatsNorTheVersion()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Gear(901, EquipmentParts.Ring, EquipStats.Empty.With(EquipStat.HpMax, 99), job: "[priest]"));
        progress.AddItem(Gear(902, EquipmentParts.Shoes, EquipStats.Empty.With(EquipStat.HpMax, 88), level: 50));
        StatBlock before = progress.Stats;
        int version = progress.Version;
        Assert.Equal(EquipResult.WrongJob, progress.Equip(0));
        Assert.Equal(EquipResult.LevelTooLow, progress.Equip(1));
        Assert.Equal(EquipResult.NoItem, progress.Equip(7));
        Assert.False(progress.Unequip(EquipmentParts.Coat));
        Assert.Equal(Fields(before), Fields(progress.Stats));
        Assert.True(progress.WornStats.IsEmpty);
        Assert.Equal(version, progress.Version);
    }

    [Fact]
    public void TheVersionMovesWithTheLevelAndWithTheWornItemsAndNeverGoesBack()
    {
        HeroProgress progress = Progress();
        int v = progress.Version;
        progress.AddItem(Ring());
        Assert.Equal(v, progress.Version);                                                      // 拾取不算
        progress.Equip(0);
        Assert.Equal(v + 1, progress.Version);
        progress.Gain(150);                                                                     // 升到 2 级
        Assert.Equal(v + 2, progress.Version);
        progress.Unequip(EquipmentParts.Ring);
        Assert.Equal(v + 3, progress.Version);
        progress.SetLevel(5);
        Assert.Equal(v + 4, progress.Version);
        progress.SetLevel(5);                                                                   // 没变
        Assert.Equal(v + 4, progress.Version);
    }

    [Fact]
    public void LevelAndEquipmentComposeInTheStats()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Ring());
        progress.Equip(0);
        progress.SetLevel(4);                                                                   // 4 级:每升一级加 100、50、10……,3 级的差
        StatBlock b = progress.BaseStats;
        Assert.Equal((10000L + 3 * 1000, 5000L + 3 * 500, 500L + 3 * 100), (b.HpMax, b.MpMax, b.PhysicalAttack));
        Assert.Equal((b.HpMax + 200, b.MpMax + 70, b.PhysicalAttack + 50), (progress.Stats.HpMax, progress.Stats.MpMax, progress.Stats.PhysicalAttack));
    }

    [Fact]
    public void RestoringASaveBringsTheWornStatsBackAndABlankSaveTakesThemAway()
    {
        HeroProgress progress = Progress(level: 3);
        ItemInfo ring = Ring(901);
        ItemInfo weapon = Gear(902, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.PhysicalAttack, 12).With(EquipStat.EquipmentPhysicalAttack, 30, 20));
        progress.AddItem(ring);
        progress.AddItem(weapon);
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        HeroSave save = progress.Snapshot();
        var items = new Dictionary<int, ItemInfo> { [901] = ring, [902] = weapon };
        Func<int, ItemInfo> lookup = code => items.TryGetValue(code, out ItemInfo found) ? found : null;

        HeroProgress other = Progress();
        SaveApplyReport report = other.Restore(save, lookup);
        Assert.Equal((2, 0, 0), (report.WornItems, report.Moved, report.Dropped));
        Assert.True(other.WornStats.Equals(progress.WornStats));
        Assert.Equal(Fields(progress.Stats), Fields(other.Stats));

        int version = other.Version;
        int equipVersion = other.EquipVersion;
        other.Restore(Progress().Snapshot(), lookup);                                           // 空存档:脱光
        Assert.True(other.WornStats.IsEmpty);
        Assert.Equal(Fields(other.BaseStats), Fields(other.Stats));
        Assert.True(other.Version > version);
        Assert.True(other.EquipVersion > equipVersion);                                         // 宿主靠它重新换武器外观
    }

    [Fact]
    public void RestoringASaveMovesTheVersionEvenWhenNothingIsWornBeforeOrAfter()
    {
        HeroProgress progress = Progress();
        int version = progress.Version;
        progress.Restore(Progress(level: 3).Snapshot(), code => null);                          // 等级变了、装备栏一直是空的
        Assert.Equal(3, progress.Level);
        Assert.True(progress.Version > version);
        Assert.Equal(0, progress.EquipVersion);
        Assert.Equal(Fields(progress.BaseStats), Fields(progress.Stats));
    }

    [Fact]
    public void ChangingTheGrowtypeMovesTheVersionAndTheSameGrowtypeDoesNot()
    {
        HeroProgress progress = Progress();
        int version = progress.Version;
        Assert.True(progress.ChangeGrowtype(1));
        Assert.Equal(version + 1, progress.Version);
        Assert.False(progress.ChangeGrowtype(1));
        Assert.Equal(version + 1, progress.Version);
    }

    [Fact]
    public void ASaveWhoseItemIsNoLongerWearableKeepsItOutOfTheStats()
    {
        HeroProgress progress = Progress(level: 3);
        ItemInfo ring = Ring(901);
        progress.AddItem(ring);
        progress.Equip(0);
        HeroSave save = progress.Snapshot();
        HeroProgress other = Progress();
        ItemInfo changed = Gear(901, EquipmentParts.Ring, ring.Stats, job: "[priest]");          // 数据变了:剑魂现在穿不了
        SaveApplyReport report = other.Restore(save, code => code == 901 ? changed : null);
        Assert.Equal((0, 1), (report.WornItems, report.Moved));                                  // 放回了背包
        Assert.True(other.WornStats.IsEmpty);
        Assert.Equal(Fields(other.BaseStats), Fields(other.Stats));
    }

    // ------------------------------------------------------------------ 战斗会话

    private static DamageConstants Constants()
    {
        return new DamageConstants
        {
            PhysicalDamageRate = 100f,
            MagicalDamageRate = 100f,
            PhysicalReduceRate = 20f,
            MagicalReduceRate = 20f,
            CommonElementBase = 100f,
        };
    }

    private static Arena ArenaWith(HeroProgress progress)
    {
        HeroPanel panel = HeroPanel.FromStats(progress.Level, progress.Stats, new EquipmentStats(AvatarFixture.WeaponStartPhysical, AvatarFixture.WeaponStartMagical));
        return new Arena(new[] { (340, 100) }, hp: 1000, heroPanel: panel, damageConstants: Constants(), progress: progress,
            vitals: HeroVitals.FromStats(progress.Level, progress.Stats));
    }

    [Fact]
    public void ASessionSwapsInTheStatsOnTheNextUpdateWhenAnItemIsWornAndBackWhenItIsTakenOff()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        HeroVitals vitals = s.Vitals;
        (int hpMax, int mpMax, double pDef, double mDef) = (vitals.HpMax, vitals.MpMax, vitals.PhysicalDefense, vitals.MagicalDefense);
        (int pAtk, int mAtk) = (s.Panel.PhysicalAttack, s.Panel.MagicalAttack);
        vitals.Damage(300);
        int hp = vitals.Hp;

        progress.AddItem(Ring());
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        Assert.Equal(hpMax, vitals.HpMax);                                                      // 还没更新:没变
        s.Update(10, 0, 0, false);
        Assert.Equal((hpMax + 20, mpMax + 7, pDef + 3.0, mDef + 4.0), (vitals.HpMax, vitals.MpMax, vitals.PhysicalDefense, vitals.MagicalDefense));
        Assert.Equal((pAtk + 5, mAtk + 6), (s.Panel.PhysicalAttack, s.Panel.MagicalAttack));
        Assert.Equal(hp, vitals.Hp);                                                            // 换上限不回复也不扣当前生命

        Assert.True(progress.Unequip(EquipmentParts.Ring));
        s.Update(10, 0, 0, false);
        Assert.Equal((hpMax, mpMax, pDef, mDef), (vitals.HpMax, vitals.MpMax, vitals.PhysicalDefense, vitals.MagicalDefense));
        Assert.Equal((pAtk, mAtk), (s.Panel.PhysicalAttack, s.Panel.MagicalAttack));
    }

    [Fact]
    public void AnotherRoomsSessionPicksUpWhatWasWornInTheSharedProgress()
    {
        HeroProgress progress = Progress();
        using Arena a = ArenaWith(progress);
        using Arena b = ArenaWith(progress);
        int hpMax = b.Session.Vitals.HpMax;
        progress.AddItem(Ring());
        progress.Equip(0);
        a.Session.Update(10, 0, 0, false);
        Assert.Equal(hpMax + 20, a.Session.Vitals.HpMax);
        Assert.Equal(hpMax, b.Session.Vitals.HpMax);                                            // 另一个房间的会话还没同步
        b.Session.SyncProgress();
        Assert.Equal(hpMax + 20, b.Session.Vitals.HpMax);
    }

    [Fact]
    public void VitalsAndPanelBuiltFromTheOutletCarryTheWornStats()
    {
        // 会话创建时(RoomCombat 的工厂)与换面板时都是 FromStats(进度的等级, 进度的 Stats):穿着装备时造出来的就是带着装备属性的
        HeroProgress progress = Progress();
        HeroVitals bare = HeroVitals.FromStats(progress.Level, progress.Stats);
        progress.AddItem(Ring());
        progress.Equip(0);
        HeroVitals worn = HeroVitals.FromStats(progress.Level, progress.Stats);
        Assert.Equal((bare.HpMax + 20, bare.MpMax + 7, bare.PhysicalDefense + 3.0, bare.MagicalDefense + 4.0), (worn.HpMax, worn.MpMax, worn.PhysicalDefense, worn.MagicalDefense));
        Assert.Equal((55, 46), (HeroPanel.FromStats(progress.Level, progress.Stats, default).PhysicalAttack, HeroPanel.FromStats(progress.Level, progress.Stats, default).MagicalAttack));
    }

    [Fact]
    public void WearingAnItemWithoutStatsLeavesTheLimitsAndThePanelAlone()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        (int hpMax, int mpMax, double pDef) = (s.Vitals.HpMax, s.Vitals.MpMax, s.Vitals.PhysicalDefense);
        (int pAtk, int mAtk) = (s.Panel.PhysicalAttack, s.Panel.MagicalAttack);
        progress.AddItem(Gear(901, EquipmentParts.Ring, null));
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        s.Update(10, 0, 0, false);
        Assert.Equal((hpMax, mpMax, pDef, pAtk, mAtk), (s.Vitals.HpMax, s.Vitals.MpMax, s.Vitals.PhysicalDefense, s.Panel.PhysicalAttack, s.Panel.MagicalAttack));
    }
}
