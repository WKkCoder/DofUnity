using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.ExpTests;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 装备栏接进进度、面板、外观与战斗会话(证据 E-118):<see cref="HeroProgress"/> 的穿脱、<see cref="HeroPanel.WornEquipment(System.Func{string, EquipmentStats}, TownHero, HeroProgress)"/>
/// 的加总、<see cref="HeroLook"/> 换武器外观、战斗会话在下一步更新里同步。物品与数值是合成的(夹具里的两把武器脚本带合成的攻击力)。
/// </summary>
public class EquipSessionTests
{
    private const string StartPath = AvatarFixture.EquipRoot + "/" + AvatarFixture.WeaponEqu;
    private const string SecondPath = AvatarFixture.EquipRoot + "/" + AvatarFixture.SecondWeaponEqu;

    private static ItemInfo Gear(int code, string path, int part = EquipmentParts.Weapon, int level = 1, string job = "[swordman]")
    {
        var item = new ItemInfo(code, path, ItemKind.Equipment) { Part = part, MinimumLevel = level };
        item.UsableJobs.Add(job);
        return item;
    }

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

    private static EquipmentStats Read(string path)
    {
        string p = path.ToLowerInvariant();
        if (p.EndsWith("sw_start.equ"))
        {
            return new EquipmentStats(10, 4);
        }
        if (p.EndsWith("sw_digit.equ"))
        {
            return new EquipmentStats(25, 9);
        }
        if (p.EndsWith("ring.equ"))
        {
            return new EquipmentStats(3, 1, 2f, 1f);
        }
        return default;
    }

    // ------------------------------------------------------------------ HeroProgress

    [Fact]
    public void EquipMovesTheItemFromTheBagIntoTheSlotAndCountsAVersion()
    {
        HeroProgress progress = Progress();
        Assert.Equal((0, 0, null), (progress.EquipVersion, progress.EquippedCount, progress.WeaponPath));
        Assert.Equal(EquipResult.NoItem, progress.Equip(0));
        progress.AddItem(Gear(1, SecondPath));
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        Assert.Equal((1, 1, SecondPath), (progress.EquipVersion, progress.EquippedCount, progress.WeaponPath));
        Assert.Empty(progress.Bag.Tab(InventoryTab.Equipment));
        Assert.Equal(EquipResult.NoItem, progress.Equip(0));
        Assert.Equal(1, progress.EquipVersion);                                       // 没成功不算一次变化
    }

    [Fact]
    public void UnequipPutsTheItemBackAndCountsAnotherVersion()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1, SecondPath));
        progress.Equip(0);
        Assert.False(progress.Unequip(EquipmentParts.Coat));                          // 那一格是空的
        Assert.True(progress.Unequip(EquipmentParts.Weapon));
        Assert.Equal((2, 0, null, 1), (progress.EquipVersion, progress.EquippedCount, progress.WeaponPath, progress.Bag.Tab(InventoryTab.Equipment).Count));
    }

    [Fact]
    public void TheHeroesJobAndLevelDecideWhatCanBeWorn()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1, SecondPath, job: "[fighter]"));
        progress.AddItem(Gear(2, StartPath, level: 10));
        Assert.Equal(EquipResult.WrongJob, progress.Equip(0));
        Assert.Equal(EquipResult.LevelTooLow, progress.Equip(1));
        progress.Job = 1;                                                              // 换成格斗家:第一件能穿了
        Assert.Equal(EquipResult.Ok, progress.Equip(0));
        Assert.Equal(SecondPath, progress.WeaponPath);
        progress.Job = EquipRules.SwordmanJob;
        progress.SetLevel(10);
        Assert.Equal(EquipResult.Ok, progress.Equip(0));                               // 升到 10 级:第二件(现在是第 0 格)能穿了,把第一件换回背包
        Assert.Equal(StartPath, progress.WeaponPath);
        Assert.Equal(1, progress.Bag.Tab(InventoryTab.Equipment)[0].Info.Code);
    }

    [Fact]
    public void TheShowCheckIsPassedThroughToTheRules()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1, SecondPath));
        Assert.Equal(EquipResult.NotShowable, progress.Equip(0, item => false));
        Assert.Equal((0, 0), (progress.EquippedCount, progress.EquipVersion));
        Assert.Equal(EquipResult.Ok, progress.Equip(0, item => item.Code == 1));
    }

    // ------------------------------------------------------------------ 攻击面板的装备加成

    [Fact]
    public void WornEquipmentAddsEveryWornItemAndTheWornWeaponReplacesTheOutfitsOne()
    {
        using var arena = new Arena(new[] { (340, 100) });
        TownHero hero = arena.Hero;
        Assert.Equal((10, 4), Pair(HeroPanel.WornEquipment(Read, hero, null)));                  // 只看外观:起手武器
        Assert.Equal((10, 4), Pair(HeroPanel.WornEquipment(Read, hero, Progress())));            // 进度里什么都没穿:一样

        HeroProgress progress = Progress();
        progress.AddItem(Gear(1, SecondPath));
        progress.AddItem(Gear(2, "equipment/test/ring.equ", EquipmentParts.Ring));
        progress.Equip(0);
        Assert.Equal((25, 9), Pair(HeroPanel.WornEquipment(Read, hero, progress)));              // 装备栏的武器顶掉外观里的
        progress.Equip(0);
        EquipmentStats both = HeroPanel.WornEquipment(Read, hero, progress);
        Assert.Equal((28, 10, 2f, 1f), (both.PhysicalAttack, both.MagicalAttack, both.PhysicalCritical, both.MagicalCritical));
        Assert.Equal((28, 10), Pair(HeroPanel.WornEquipment(Read, null, progress)));             // 没有主角:只看装备栏
    }

    private static (int, int) Pair(EquipmentStats stats) => (stats.PhysicalAttack, stats.MagicalAttack);

    [Fact]
    public void WornEquipmentIsZeroWithNothingAndRejectsAMissingReader()
    {
        Assert.Equal((0, 0), Pair(HeroPanel.WornEquipment(Read, null, null)));
        Assert.Throws<ArgumentNullException>(() => HeroPanel.WornEquipment((Func<string, EquipmentStats>)null, null, null));
        Assert.Equal((0, 0), Pair(HeroPanel.WornEquipment(null, null)));
    }

    [Fact]
    public void WornEquipmentReadsTheEquipmentScriptsThroughTheAssets()
    {
        using var arena = new Arena(new[] { (340, 100) });
        EquipmentStats outfit = HeroPanel.WornEquipment(arena.Assets, arena.Hero);
        Assert.Equal((AvatarFixture.WeaponStartPhysical, AvatarFixture.WeaponStartMagical), Pair(outfit));
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1, SecondPath));
        progress.Equip(0);
        Assert.Equal((AvatarFixture.SecondWeaponPhysical, AvatarFixture.SecondWeaponMagical), Pair(HeroPanel.WornEquipment(arena.Assets, arena.Hero, progress)));
    }

    // ------------------------------------------------------------------ HeroLook

    [Fact]
    public void SameEquipPathIgnoresCaseSlashesAndTheSwordmanRootPrefix()
    {
        Assert.True(HeroLook.SameEquipPath(null, null));
        Assert.False(HeroLook.SameEquipPath(null, "equipment/a.equ"));
        Assert.False(HeroLook.SameEquipPath("equipment/a.equ", null));
        Assert.True(HeroLook.SameEquipPath("Equipment\\Character\\A.EQU", "equipment/character/a.equ"));
        Assert.True(HeroLook.SameEquipPath(DemoHero.StarterWeapon, DemoHero.SwordmanEquipRoot + "/" + DemoHero.StarterWeapon));
        Assert.False(HeroLook.SameEquipPath("equipment/a.equ", "equipment/b.equ"));
    }

    [Fact]
    public void WeaponPathOfIsTheOutfitsWeaponOrNull()
    {
        using var arena = new Arena(new[] { (340, 100) });
        Assert.Null(HeroLook.WeaponPathOf(null));
        Assert.True(HeroLook.SameEquipPath(StartPath, HeroLook.WeaponPathOf(arena.Hero.Outfit)));
        Assert.Null(HeroLook.WeaponPathOf(arena.Hero.Outfit.WithWeapon(null)));
    }

    [Fact]
    public void TrySwapWeaponReplacesTheWeaponLayersOnlyWhenTheScriptIsAWeaponForThisClass()
    {
        using var arena = new Arena(new[] { (340, 100) });
        TownHero hero = arena.Hero;
        AvatarOutfit before = hero.Outfit;
        Assert.False(HeroLook.TrySwapWeapon(arena.Assets, hero, AvatarFixture.EquipRoot + "/weapon/sword/missing.equ"));
        Assert.False(HeroLook.TrySwapWeapon(arena.Assets, hero, AvatarFixture.EquipRoot + "/avatar/coat/acoat_default.equ"));       // 不是武器
        Assert.False(HeroLook.TrySwapWeapon(arena.Assets, hero, null));
        Assert.False(HeroLook.TrySwapWeapon(arena.Assets, null, SecondPath));
        Assert.Same(before, hero.Outfit);

        Assert.True(HeroLook.TrySwapWeapon(arena.Assets, hero, SecondPath));
        Assert.NotSame(before, hero.Outfit);
        Assert.True(HeroLook.SameEquipPath(SecondPath, HeroLook.WeaponPathOf(hero.Outfit)));
        Assert.Contains(hero.Outfit.Layers, l => l.Name == "swordb1");
        Assert.DoesNotContain(hero.Outfit.Layers, l => l.Name == "swordb");
        Assert.Throws<ArgumentNullException>(() => HeroLook.TrySwapWeapon(null, hero, SecondPath));
    }

    [Fact]
    public void ApplyFollowsTheEquipmentBarAndMatchesSaysWhetherTheLookIsRight()
    {
        using var arena = new Arena(new[] { (340, 100) });
        TownHero hero = arena.Hero;
        HeroProgress progress = Progress();
        Assert.False(HeroLook.Apply(arena.Assets, hero, null));
        Assert.False(HeroLook.Apply(arena.Assets, null, progress));
        Assert.False(HeroLook.Matches(hero, progress));                               // 没穿武器要的是起手武器,夹具里的外观不是它
        Assert.False(HeroLook.Matches(null, progress));
        Assert.False(HeroLook.Matches(hero, null));

        progress.AddItem(Gear(1, SecondPath));
        progress.Equip(0);
        Assert.False(HeroLook.Matches(hero, progress));
        Assert.True(HeroLook.Apply(arena.Assets, hero, progress));
        Assert.True(HeroLook.Matches(hero, progress));
        Assert.False(HeroLook.Apply(arena.Assets, hero, progress));                   // 已经一致:什么也不做

        progress.Unequip(EquipmentParts.Weapon);                                      // 没穿:要退回起手武器,夹具里没有它,外观保持
        Assert.False(HeroLook.Apply(arena.Assets, hero, progress));
        Assert.True(HeroLook.SameEquipPath(SecondPath, HeroLook.WeaponPathOf(hero.Outfit)));
    }

    [Fact]
    public void CanShowNeedsEveryRequiredMotionInAtLeastOneWeaponLayer()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        ItemInfo start = Gear(1, StartPath), second = Gear(2, SecondPath), missing = Gear(3, AvatarFixture.EquipRoot + "/weapon/sword/missing.equ");
        ItemInfo coat = Gear(4, AvatarFixture.EquipRoot + "/avatar/coat/acoat_default.equ", EquipmentParts.Coat);

        // 夹具的第一把武器:swordb 有 attack1、attack2,swordc 有 attack1
        Assert.True(HeroLook.CanShow(assets, start, cls, new[] { "attack1.ani" }));
        Assert.True(HeroLook.CanShow(assets, start, cls, new[] { "attack1.ani", "attack2.ani" }));
        Assert.False(HeroLook.CanShow(assets, start, cls, new[] { "attack1.ani", "attack3.ani" }));
        Assert.True(HeroLook.CanShow(assets, start, cls, new string[0]));               // 不要求任何动作:能造出外观就行
        Assert.False(HeroLook.CanShow(assets, second, cls, new[] { "attack1.ani" }));   // 图层目录里一个动画也没有
        Assert.False(HeroLook.CanShow(assets, missing, cls, new[] { "attack1.ani" }));
        Assert.True(HeroLook.CanShow(assets, coat, cls, new[] { "attack3.ani" }));      // 不是武器:不问
        Assert.False(HeroLook.CanShow(assets, null, cls, new[] { "attack1.ani" }));
        Assert.False(HeroLook.CanShow(assets, start, null, new[] { "attack1.ani" }));
        Assert.False(HeroLook.CanShow(assets, start, cls, null));
        Assert.Throws<ArgumentNullException>(() => HeroLook.CanShow(null, start, cls, new string[0]));
    }

    [Fact]
    public void CanShowWithoutAClassFailsClosedForWeaponsAndPassesOtherItems()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);                              // 夹具里没有剑魂的角色脚本
        Assert.False(HeroLook.CanShow(assets, Gear(1, StartPath)));
        Assert.True(HeroLook.CanShow(assets, Gear(2, StartPath, EquipmentParts.Ring)));
        Assert.False(HeroLook.CanShow(assets, null));
        Assert.Throws<ArgumentNullException>(() => HeroLook.CanShow(null, Gear(1, StartPath)));
        Assert.Equal(new[] { "attack1.ani", "attack2.ani", "attack3.ani", "dashattack.ani", "jumpattack.ani" }, HeroLook.RequiredWeaponMotions);
    }

    [Fact]
    public void GrantWearableWeaponFindsNothingWithoutAnEquipmentList()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroProgress progress = Progress();
        Assert.False(HeroLook.GrantWearableWeapon(assets, progress));
        Assert.Equal(0, progress.BagItems);
        Assert.Throws<ArgumentNullException>(() => HeroLook.GrantWearableWeapon(null, progress));
        Assert.Throws<ArgumentNullException>(() => HeroLook.GrantWearableWeapon(assets, null));
    }

    // ------------------------------------------------------------------ 战斗会话里同步

    private static Arena ArenaWith(HeroProgress progress)
    {
        // 面板的攻击力取自进度的属性出口(与生产路径 RoomCombat 建会话时一致);穿脱之后进度的属性出口变了,会话据 Version 重新派生面板,
        // 夹具若手写一个与进度对不上的攻击力,穿一件没有属性的装备也会"变"(装备栏的变化原先不计入 Version,所以这个不一致没有暴露)
        HeroPanel panel = HeroPanel.FromStats(progress.Level, progress.Stats, new EquipmentStats(AvatarFixture.WeaponStartPhysical, AvatarFixture.WeaponStartMagical));
        return new Arena(new[] { (340, 100) }, hp: 1000, heroPanel: panel, damageConstants: Constants(), progress: progress,
            vitals: HeroVitals.FromStats(progress.Level, progress.Stats));
    }

    [Fact]
    public void AWeaponWornInTheBagChangesTheLookAndTheAttackPanelOnTheNextUpdate()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        Assert.Equal((10, 4), Pair(s.Panel.Equipment));
        Assert.Equal((50, 40, 1), (s.Panel.PhysicalAttack, s.Panel.MagicalAttack, s.Panel.Level));   // 1 级的面板攻击力:成长表的物攻 50、魔攻 40(进度的属性出口是十分之一单位的 500、400)
        progress.AddItem(Gear(1, SecondPath));
        progress.Equip(0);
        Assert.Equal((10, 4), Pair(s.Panel.Equipment));                                // 还没更新:没变
        s.Update(10, 0, 0, false);
        Assert.Equal((AvatarFixture.SecondWeaponPhysical, AvatarFixture.SecondWeaponMagical), Pair(s.Panel.Equipment));
        Assert.Equal((50, 40, 1), (s.Panel.PhysicalAttack, s.Panel.MagicalAttack, s.Panel.Level));          // 面板本身的攻击力不变(这件武器脚本没有基础属性)
        Assert.True(HeroLook.SameEquipPath(SecondPath, HeroLook.WeaponPathOf(s.Hero.Outfit)));
        Assert.True(s.UsesFormulaDamage);
    }

    [Fact]
    public void AnotherWornPartAddsToThePanelWithoutTouchingTheLook()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        AvatarOutfit before = s.Hero.Outfit;
        progress.AddItem(Gear(1, SecondPath, EquipmentParts.Ring));                    // 借用夹具里带攻击力的脚本当戒指
        progress.Equip(0);
        s.Update(10, 0, 0, false);
        Assert.Equal((AvatarFixture.WeaponStartPhysical + AvatarFixture.SecondWeaponPhysical, AvatarFixture.WeaponStartMagical + AvatarFixture.SecondWeaponMagical),
            Pair(s.Panel.Equipment));                                                  // 外观里的起手武器 + 戒指
        Assert.Same(before, s.Hero.Outfit);
    }

    [Fact]
    public void AWeaponThatCannotBeShownKeepsTheLookAndStillCounts()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        AvatarOutfit before = s.Hero.Outfit;
        progress.AddItem(Gear(1, AvatarFixture.EquipRoot + "/weapon/sword/missing.equ"));
        progress.Equip(0);
        s.Update(10, 0, 0, false);
        Assert.Same(before, s.Hero.Outfit);
        Assert.Equal((0, 0), Pair(s.Panel.Equipment));                                 // 读不到脚本:没有加成(规则允许了,面板按装备栏算)
    }

    [Fact]
    public void WithoutAnEquipmentChangeThePanelObjectIsLeftAlone()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        HeroPanel panel = s.Panel;
        s.Update(10, 0, 0, false);
        s.Update(10, 0, 0, false);
        Assert.Same(panel, s.Panel);
    }

    [Fact]
    public void ALevelUpAfterAWeaponSwapKeepsTheNewEquipmentInThePanel()
    {
        HeroProgress progress = Progress();
        using Arena arena = ArenaWith(progress);
        RoomCombatSession s = arena.Session;
        progress.AddItem(Gear(1, SecondPath));
        progress.Equip(0);
        s.Update(10, 0, 0, false);
        progress.Gain(150);                                                            // 升级:面板按新等级重建,装备加成留着
        s.SyncProgress();
        Assert.Equal((AvatarFixture.SecondWeaponPhysical, AvatarFixture.SecondWeaponMagical), Pair(s.Panel.Equipment));
        Assert.Equal(progress.Level, s.Panel.Level);
    }
}
