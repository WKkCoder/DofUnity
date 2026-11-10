using Dnf.Game;
using static Dnf.Play.Tests.EnhanceKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 强化会话:把强化、附魔、套装的规则接到主角的背包、装备栏与金币上。数据全是合成的(见 EnhanceKit)。
/// 规则本身(次序、边界、惩罚)在 Dnf.Game.Tests 里钉住;这里只看接线:先判后扣、失败也扣材料、销毁拿掉装备放进产物、
/// 保护道具、附魔扣宝珠、套装与附魔进汇总、存档往返。
/// </summary>
public class EnhanceSessionTests
{
    private sealed class Rig
    {
        public HeroProgress Hero;
        public EnhanceSession Session;
        public EnhanceRoll Roll;
        public UpgradeSettings Settings;
        public Dictionary<int, ItemInfo> Defs;

        public ItemInfo Def(int code) => Defs[code];

        public ItemInfo Lookup(int code) => Defs.TryGetValue(code, out ItemInfo item) ? item : null;
    }

    private static Dictionary<int, ItemInfo> BuildDefs()
    {
        var defs = new Dictionary<int, ItemInfo>
        {
            [900] = Gear(900, EquipmentParts.Weapon, Stats(EquipStat.PhysicalAttack, 10)),
            [901] = Gear(901, EquipmentParts.Coat, Stats(EquipStat.HpMax, 40)),
            [902] = Gear(902, EquipmentParts.Ring),
            [910] = Stack(910),
            [911] = Stack(911),
            [920] = Stack(920),
            [930] = Stack(930, new EnhanceData(monsterCardId: 931)),
            [931] = Stack(931, new EnhanceData(enchantStats: Stats(EquipStat.PhysicalAttack, 7), enchantParts: new[] { EquipmentParts.Weapon, EquipmentParts.Ring })),
            [932] = Stack(932, new EnhanceData(enchantStats: Stats(EquipStat.HpMax, 30), enchantParts: new[] { EquipmentParts.Coat })),
            [933] = Stack(933, new EnhanceData(monsterCardId: 932)),
            [944] = Stack(944),
            [945] = Stack(945),
            [950] = Stack(950),
            [960] = Stack(960, new EnhanceData(reinforceTicket: new ReinforceTicket(7, 50))),
        };
        var master = new EnhanceData(
            setMembers: new[] { 940, 941, 942 },
            setFullBonus: Stats(EquipStat.HpMax, 100),
            setPieceBonuses: new[] { new SetPieceBonus(2, Stats(EquipStat.MpMax, 20)) });
        defs[940] = Gear(940, EquipmentParts.Coat, enhance: master);
        defs[941] = Gear(941, EquipmentParts.Shoulder, enhance: new EnhanceData(setMaster: 940));
        defs[942] = Gear(942, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 940));
        return defs;
    }

    private static Rig Make(int[] rolls = null, long gold = 1000, UpgradeTable table = null)
    {
        var rig = new Rig { Defs = BuildDefs(), Roll = new EnhanceRoll(rolls ?? new int[0]) };
        rig.Hero = ExpTests.Progress(level: 50);
        rig.Hero.AddGold(gold);
        rig.Settings = new UpgradeSettings
        {
            NormalTable = table ?? Table(),
            AmplifyTable = Table(),
            Random = rig.Roll,
            Tickets = new Dictionary<int, UpgradeTicketDef> { [920] = new UpgradeTicketDef(0, 31, 0) },
        };
        rig.Session = new EnhanceSession(rig.Hero, rig.Lookup, rig.Settings);
        return rig;
    }

    private static void Give(Rig rig, int code, int count = 1) => Assert.Equal(count, rig.Hero.Bag.Add(rig.Def(code), count));

    private static void Wear(Rig rig, int code) => Assert.True(rig.Hero.Worn.Restore(rig.Def(code).Part, rig.Def(code)));

    private static IReadOnlyList<ItemStack> Gears(Rig rig) => rig.Hero.Bag.Tab(InventoryTab.Equipment);

    private sealed class PerLevelBonus : IEquipInstanceBonus
    {
        public EquipStats BonusOf(ItemInfo worn)
        {
            return worn.Instance == null || worn.Instance.Upgrade == 0 ? null : Stats(EquipStat.PhysicalAttack, worn.Instance.Upgrade * 3);
        }
    }

    // ------------------------------------------------------------------ 构造与定位

    [Fact]
    public void TheSessionHooksItsOverlayOntoTheWornSlotsAndSeedsARandomSourceWhenNoneIsGiven()
    {
        HeroProgress hero = ExpTests.Progress(level: 5);
        var settings = new UpgradeSettings { NormalTable = Table() };
        var session = new EnhanceSession(hero, _ => null, settings);
        Assert.Same(session.Overlay, hero.Worn.Overlay);
        Assert.NotNull(settings.Random);
        Assert.Same(hero, session.Progress);
        Assert.Same(settings, session.Settings);

        var own = new EnhanceRoll();
        var given = new UpgradeSettings { NormalTable = Table() };
        new EnhanceSession(hero, _ => null, given, own);
        Assert.Same(own, given.Random);

        var kept = new UpgradeSettings { NormalTable = Table(), Random = own };
        new EnhanceSession(hero, _ => null, kept, new EnhanceRoll());
        Assert.Same(own, kept.Random);
    }

    [Fact]
    public void ConstructorArgumentsAreChecked()
    {
        HeroProgress hero = ExpTests.Progress();
        var settings = new UpgradeSettings();
        Assert.Throws<ArgumentNullException>(() => new EnhanceSession(null, _ => null, settings));
        Assert.Throws<ArgumentNullException>(() => new EnhanceSession(hero, null, settings));
        Assert.Throws<ArgumentNullException>(() => new EnhanceSession(hero, _ => null, null));
    }

    [Fact]
    public void ItemAtFindsBagSlotsAndWornParts()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 901);
        Wear(r, 902);
        Assert.Same(r.Def(900), r.Session.ItemAt(EnhanceTarget.InBag(0)));
        Assert.Same(r.Def(901), r.Session.ItemAt(EnhanceTarget.InBag(1)));
        Assert.Null(r.Session.ItemAt(EnhanceTarget.InBag(2)));
        Assert.Null(r.Session.ItemAt(EnhanceTarget.InBag(-1)));
        Assert.Same(r.Def(902), r.Session.ItemAt(EnhanceTarget.OnBody(EquipmentParts.Ring)));
        Assert.Null(r.Session.ItemAt(EnhanceTarget.OnBody(EquipmentParts.Weapon)));
        Assert.Null(r.Session.ItemAt(EnhanceTarget.OnBody(99)));
        Assert.Equal((EnhancePlace.Bag, 3), (EnhanceTarget.InBag(3).Place, EnhanceTarget.InBag(3).Index));
        Assert.Equal((EnhancePlace.Worn, 10), (EnhanceTarget.OnBody(10).Place, EnhanceTarget.OnBody(10).Index));
    }

    // ------------------------------------------------------------------ 预览

    [Fact]
    public void PreviewShowsCostsAndOddsWithoutTouchingAnything()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 910, 10);
        UpgradeWindow w = r.Session.Preview(EnhanceTarget.InBag(0), false, 910);
        Assert.True(w.Ready);
        Assert.Equal((0, 1, false, false), (w.Level, w.NextLevel, w.Amplified, w.Jump));
        Assert.Equal((100L, 1000L), (w.GoldCost, w.Gold));
        Assert.Equal((910, 3, 10), (w.MaterialCode, w.MaterialCount, w.MaterialOwned));
        Assert.Equal(20000L, w.FailThreshold);
        Assert.Equal(1.0 - 20000 / 100001.0, w.SuccessChance, 9);
        Assert.Equal(1, w.PenaltyType);
        Assert.Empty(r.Roll.Maxes);
        Assert.Equal(1000L, r.Hero.Gold);
        Assert.Equal(10, r.Hero.Bag.CountOf(910));
        Assert.Same(r.Def(900), r.Session.ItemAt(EnhanceTarget.InBag(0)));
    }

    [Fact]
    public void PreviewOfAZeroThresholdIsACertainSuccess()
    {
        Rig r = Make(table: Table(fail: 0));
        Give(r, 900);
        Give(r, 910, 3);
        UpgradeWindow w = r.Session.Preview(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal((0L, 1.0), (w.FailThreshold, w.SuccessChance));
    }

    [Fact]
    public void PreviewReportsTheRejectionAndStillFillsWhatItCan()
    {
        Rig r = Make();
        Give(r, 900);
        UpgradeWindow noMaterial = r.Session.Preview(EnhanceTarget.InBag(0), false, 910);
        Assert.False(noMaterial.Ready);
        Assert.Equal(UpgradeCodes.Material, noMaterial.Code);
        Assert.Equal(0, noMaterial.MaterialOwned);

        UpgradeWindow noEquip = r.Session.Preview(EnhanceTarget.InBag(5), false, 910);
        Assert.Equal(UpgradeCodes.NoEquipment, noEquip.Code);

        Give(r, 910, 2);
        UpgradeWindow few = r.Session.Preview(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal(UpgradeCodes.Material, few.Code);
        Assert.Equal((910, 3, 2, 100L), (few.MaterialCode, few.MaterialCount, few.MaterialOwned, few.GoldCost));
    }

    [Fact]
    public void PreviewOfAJumpMaterialShowsTheTargetAndTheTicketsPercent()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 960, 2);
        UpgradeWindow w = r.Session.Preview(EnhanceTarget.InBag(0), false, 960);
        Assert.True(w.Ready);
        Assert.Equal((true, 0, 7, 0L, 0.5, 0), (w.Jump, w.Level, w.NextLevel, w.GoldCost, w.SuccessChance, w.PenaltyType));
        Assert.Equal((960, 1, 2), (w.MaterialCode, w.MaterialCount, w.MaterialOwned));
    }

    [Fact]
    public void PreviewShowsTheDestroyTierAsPenaltyThree()
    {
        Rig r = Make(table: Table(penaltyType: 3));
        Give(r, 900);
        Give(r, 910, 3);
        Assert.Equal(3, r.Session.Preview(EnhanceTarget.InBag(0), false, 910).PenaltyType);
    }

    // ------------------------------------------------------------------ 强化

    [Fact]
    public void ASuccessSpendsGoldAndMaterialAndReplacesTheBagEquipment()
    {
        Rig r = Make(new[] { 100000 });
        Give(r, 901);
        Give(r, 900);
        Give(r, 910, 10);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(1), false, 910);
        Assert.Equal(0, o.Code);
        Assert.True(o.Upgrade.Success);
        Assert.Equal(900L, r.Hero.Gold);
        Assert.Equal(7, r.Hero.Bag.CountOf(910));
        Assert.Equal(1, Gears(r)[1].Info.Instance.Upgrade);
        Assert.Same(o.Equipment, Gears(r)[1].Info);
        Assert.Same(r.Def(901), Gears(r)[0].Info);                       // 另一件原样
        Assert.Null(r.Def(900).Instance);                                // 共享的定义没动
        Assert.Equal(new[] { 100000 }, r.Roll.Maxes);
    }

    [Fact]
    public void ASuccessOnTheBodyReplacesTheWornItemAndBumpsTheVersion()
    {
        Rig r = Make(new[] { 100000 });
        Wear(r, 900);
        Give(r, 910, 3);
        int worn = r.Hero.Worn.Version;
        int version = r.Hero.Version;
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.OnBody(EquipmentParts.Weapon), false, 910);
        Assert.Equal(0, o.Code);
        Assert.Equal(1, r.Hero.Worn.Get(EquipmentParts.Weapon).Instance.Upgrade);
        Assert.True(r.Hero.Worn.Version > worn);
        Assert.True(r.Hero.Version > version);
        Assert.Equal(0, r.Hero.Bag.CountOf(910));    // 材料正好用完
    }

    [Fact]
    public void TheUpgradeBonusProviderFeedsTheHeroStats()
    {
        Rig r = Make(new[] { 100000, 100000 });
        r.Session.Overlay.InstanceBonus = new PerLevelBonus();
        Wear(r, 900);
        Give(r, 910, 6);
        long baseAttack = r.Hero.BaseStats.PhysicalAttack;
        Assert.Equal(baseAttack + 100, r.Hero.Stats.PhysicalAttack);           // 装备自己的 10 点
        r.Session.Upgrade(EnhanceTarget.OnBody(EquipmentParts.Weapon), false, 910);
        Assert.Equal(13.0, r.Hero.WornStats[EquipStat.PhysicalAttack]);        // + 1 级 × 3
        Assert.Equal(baseAttack + 130, r.Hero.Stats.PhysicalAttack);
        r.Session.Upgrade(EnhanceTarget.OnBody(EquipmentParts.Weapon), false, 910);
        Assert.Equal(baseAttack + 160, r.Hero.Stats.PhysicalAttack);
    }

    [Fact]
    public void AFailureStillSpendsGoldAndMaterialButKeepsTheLevelUnderPenaltyOne()
    {
        Rig r = Make(new[] { 0 });
        Give(r, 900);
        Give(r, 910, 5);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal(0, o.Code);
        Assert.Equal(UpgradeOutcome.Failure, o.Upgrade.Outcome);
        Assert.Equal((900L, 2), (r.Hero.Gold, r.Hero.Bag.CountOf(910)));
        Assert.Same(r.Def(900), Gears(r)[0].Info);   // 没有等级也没有状态:回到共享的定义
    }

    [Fact]
    public void PenaltyTwoLowersTheLevelInThePlace()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 2, penaltyLevels: 2));
        Give(r, 910, 3);
        r.Hero.Bag.Add(r.Def(900).WithInstance(new EquipInstance(5)), 1);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal((2, 3), (o.Upgrade.Penalty, Gears(r)[0].Info.Instance.Upgrade));
    }

    [Fact]
    public void PenaltyThreeDestroysABagEquipmentAndLeavesTheOthersAlone()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        Give(r, 901);
        Give(r, 900);
        Give(r, 902);
        Give(r, 910, 3);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(1), false, 910);
        Assert.Equal(0, o.Code);
        Assert.True(o.Upgrade.Destroyed);
        Assert.Null(o.Equipment);
        Assert.Equal(new[] { 901, 902 }, Gears(r).Select(s => s.Info.Code));
        Assert.Equal(900L, r.Hero.Gold);
    }

    [Fact]
    public void PenaltyThreeOnTheBodyTakesTheItemOffAndItsStatsGoWithIt()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        Wear(r, 900);
        Give(r, 910, 3);
        long withWeapon = r.Hero.Stats.PhysicalAttack;
        int version = r.Hero.Version;
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.OnBody(EquipmentParts.Weapon), false, 910);
        Assert.True(o.Upgrade.Destroyed);
        Assert.Null(r.Hero.Worn.Get(EquipmentParts.Weapon));
        Assert.Equal(withWeapon - 100, r.Hero.Stats.PhysicalAttack);
        Assert.True(r.Hero.Version > version);
    }

    [Fact]
    public void DestroyedEquipmentHandsOverItsProducts()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        r.Settings.DestroyProducts = (equip, level) => new[] { new ItemStack(r.Def(950), 4) };
        Give(r, 900);
        Give(r, 910, 3);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal((4, 0), (o.ProductsAdded, o.ProductsLost));
        Assert.Equal(4, r.Hero.Bag.CountOf(950));
    }

    [Fact]
    public void ProductsThatDoNotFitAreCountedAsLost()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        ItemInfo extraA = Gear(970, EquipmentParts.Ring);
        ItemInfo extraB = Gear(971, EquipmentParts.Ring);
        r.Settings.DestroyProducts = (equip, level) => new[] { new ItemStack(extraA, 1), new ItemStack(extraB, 1) };
        Give(r, 900);
        for (int i = 1; i < r.Hero.Bag.Capacity; i++)
        {
            Assert.Equal(1, r.Hero.Bag.Add(Gear(2000 + i, EquipmentParts.Ring), 1));    // 装备页填满
        }
        Give(r, 910, 3);
        Assert.Equal(r.Hero.Bag.Capacity, Gears(r).Count);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.True(o.Upgrade.Destroyed);
        Assert.Equal((1, 1), (o.ProductsAdded, o.ProductsLost));   // 腾出来一格,放得下一件
        Assert.Equal(r.Hero.Bag.Capacity, Gears(r).Count);
    }

    [Fact]
    public void AProtectionItemSavesTheEquipmentAndIsSpent()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        r.Session.ProtectionCodes = new[] { 944 };
        r.Hero.Bag.Add(r.Def(900).WithInstance(new EquipInstance(8)), 1);
        Give(r, 910, 3);
        Give(r, 944, 2);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal(0, o.Code);
        Assert.Equal((UpgradeCodes.Protected, 944, false), (o.Upgrade.Code, o.Upgrade.ProtectionUsed, o.Upgrade.Destroyed));
        Assert.Equal(1, r.Hero.Bag.CountOf(944));
        Assert.Same(r.Def(900), Gears(r)[0].Info);   // 强化等级归零,没有别的状态:回到共享的定义
    }

    [Fact]
    public void WithoutTheProtectionItemInTheBagTheEquipmentIsDestroyedAnyway()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        r.Session.ProtectionCodes = new[] { 944 };
        Give(r, 900);
        Give(r, 910, 3);
        Assert.True(r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910).Upgrade.Destroyed);
    }

    [Fact]
    public void TheFirstProtectionCodeInTheBagIsTheOneUsed()
    {
        Rig r = Make(new[] { 0 }, table: Table(penaltyType: 3));
        r.Session.ProtectionCodes = new[] { 944, 945 };
        Give(r, 900);
        Give(r, 910, 3);
        Give(r, 944);
        Give(r, 945);
        r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal((0, 1), (r.Hero.Bag.CountOf(944), r.Hero.Bag.CountOf(945)));
    }

    [Fact]
    public void ARejectedAttemptChangesNothing()
    {
        Rig r = Make(new[] { 0 });
        Give(r, 900);
        Give(r, 910, 2);   // 不够 3 个
        int version = r.Hero.Version;
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal(UpgradeCodes.Material, o.Code);
        Assert.NotNull(o.Upgrade);
        Assert.Null(o.Equipment);
        Assert.Equal((1000L, 2, version), (r.Hero.Gold, r.Hero.Bag.CountOf(910), r.Hero.Version));
        Assert.Same(r.Def(900), Gears(r)[0].Info);
        Assert.Empty(r.Roll.Maxes);
    }

    [Fact]
    public void NotEnoughGoldIsARejection()
    {
        Rig r = Make(new[] { 0 }, gold: 99);
        Give(r, 900);
        Give(r, 910, 3);
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910);
        Assert.Equal(UpgradeCodes.NotEnoughGold, o.Code);
        Assert.Equal((99L, 3), (r.Hero.Gold, r.Hero.Bag.CountOf(910)));
    }

    [Fact]
    public void AnEmptyTargetOrMissingMaterialCodeIsRejected()
    {
        Rig r = Make();
        Give(r, 910, 3);
        Assert.Equal(UpgradeCodes.NoEquipment, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910).Code);
        Give(r, 900);
        Assert.Equal(UpgradeCodes.Material, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 0).Code);
        Assert.Equal(UpgradeCodes.Material, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 911).Code);   // 背包里没有 911
    }

    [Fact]
    public void ASecondItemIsSpentWhetherTheAttemptWinsOrLoses()
    {
        Rig r = Make(new[] { 100000, 0 });
        Give(r, 900);
        Give(r, 910, 6);
        Give(r, 920, 2);
        Assert.Equal(0, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910, 920).Code);
        Assert.Equal(1, r.Hero.Bag.CountOf(920));
        Assert.Equal(0, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910, 920).Code);
        Assert.Equal(0, r.Hero.Bag.CountOf(920));
    }

    [Fact]
    public void ASecondItemThatIsNotRegisteredIsRejected()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 910, 3);
        Give(r, 911);
        Assert.Equal(UpgradeCodes.TicketUnknown, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910, 911).Code);
        Assert.Equal(1, r.Hero.Bag.CountOf(911));
    }

    [Fact]
    public void AJumpMaterialRaisesTheLevelWithoutGoldAndFailsWithoutPenalty()
    {
        Rig r = Make(new[] { 49, 50 }, gold: 0);
        Give(r, 900);
        Give(r, 960, 2);
        EnhanceOutcome win = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 960);
        Assert.Equal(0, win.Code);
        Assert.Equal(7, Gears(r)[0].Info.Instance.Upgrade);
        Assert.Equal(1, r.Hero.Bag.CountOf(960));
        EnhanceOutcome lose = r.Session.Upgrade(EnhanceTarget.InBag(0), false, 960);
        Assert.Equal(UpgradeOutcome.Failure, lose.Upgrade.Outcome);
        Assert.Equal((7, 0, 0L), (Gears(r)[0].Info.Instance.Upgrade, r.Hero.Bag.CountOf(960), r.Hero.Gold));
        Assert.Equal(new[] { 99, 99 }, r.Roll.Maxes);
    }

    [Fact]
    public void AFullUpgradeRunThroughTheSessionWalksTheTable()
    {
        Rig r = Make(Enumerable.Repeat(100000, 12).ToArray(), gold: 1200, table: Table(materialCount: 1));
        Give(r, 900);
        Give(r, 910, 12);
        for (int level = 1; level <= 12; level++)
        {
            Assert.Equal(0, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910).Code);
            Assert.Equal(level, Gears(r)[0].Info.Instance.Upgrade);
        }
        Assert.Equal((0L, 0), (r.Hero.Gold, r.Hero.Bag.CountOf(910)));
        Give(r, 910, 1);
        r.Hero.AddGold(100);
        Assert.Equal(UpgradeCodes.MaxLevel, r.Session.Upgrade(EnhanceTarget.InBag(0), false, 910).Code);   // 表走完了
    }

    // ------------------------------------------------------------------ 附魔

    [Fact]
    public void ABeadEnchantsTheBagEquipmentAndIsConsumed()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 930, 2);
        EnhanceOutcome o = r.Session.Enchant(EnhanceTarget.InBag(0), 930);
        Assert.Equal(0, o.Code);
        Assert.True(o.Enchant.Success);
        Assert.Equal(931, Gears(r)[0].Info.Instance.EnchantCode);
        Assert.Same(o.Equipment, Gears(r)[0].Info);
        Assert.Equal(1, r.Hero.Bag.CountOf(930));
    }

    [Fact]
    public void AnEnchantedWornItemFeedsTheStatsAndTheVersion()
    {
        Rig r = Make();
        Wear(r, 900);
        Give(r, 930);
        long baseAttack = r.Hero.BaseStats.PhysicalAttack;
        int version = r.Hero.Version;
        Assert.Equal(0, r.Session.Enchant(EnhanceTarget.OnBody(EquipmentParts.Weapon), 930).Code);
        Assert.Equal(17.0, r.Hero.WornStats[EquipStat.PhysicalAttack]);     // 装备 10 + 卡 7
        Assert.Equal(baseAttack + 170, r.Hero.Stats.PhysicalAttack);
        Assert.True(r.Hero.Version > version);
    }

    [Fact]
    public void ANewCardOverwritesTheOldOneAndTheStatsFollow()
    {
        Rig r = Make();
        Wear(r, 900);
        r.Hero.Worn.Restore(EquipmentParts.Weapon, r.Def(900).WithInstance(new EquipInstance(0, enchantCode: 931)));
        Give(r, 930);
        long before = r.Hero.Stats.PhysicalAttack;
        EnhanceOutcome o = r.Session.Enchant(EnhanceTarget.OnBody(EquipmentParts.Weapon), 930);
        Assert.Equal((0, 931), (o.Code, o.Enchant.PreviousCardCode));
        Assert.Equal(before, r.Hero.Stats.PhysicalAttack);                  // 同一张卡再附:加成不叠加
    }

    [Fact]
    public void AWrongPartKeepsTheBeadAndTheEquipment()
    {
        Rig r = Make();
        Give(r, 901);
        Give(r, 930);
        EnhanceOutcome o = r.Session.Enchant(EnhanceTarget.InBag(0), 930);
        Assert.Equal(EnchantCodes.Part, o.Code);
        Assert.Null(o.Equipment);
        Assert.Equal(1, r.Hero.Bag.CountOf(930));
        Assert.Same(r.Def(901), Gears(r)[0].Info);
    }

    [Fact]
    public void ABeadNotInTheBagIsInvalid()
    {
        Rig r = Make();
        Give(r, 900);
        Assert.Equal(EnchantCodes.Invalid, r.Session.Enchant(EnhanceTarget.InBag(0), 930).Code);
        Assert.Equal(EnchantCodes.Invalid, r.Session.Enchant(EnhanceTarget.InBag(0), 0).Code);
        Assert.Equal(EnchantCodes.Invalid, r.Session.Enchant(EnhanceTarget.InBag(3), 930).Code);
    }

    [Fact]
    public void TheWorldCheckIsAskedAndCanRefuse()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 930);
        ItemInfo seen = null;
        r.Session.EnableWorld = (equip, bead) =>
        {
            seen = bead;
            return false;
        };
        EnhanceOutcome o = r.Session.Enchant(EnhanceTarget.InBag(0), 930);
        Assert.Equal(EnchantCodes.World, o.Code);
        Assert.Same(r.Def(930), seen);
        Assert.Equal(1, r.Hero.Bag.CountOf(930));
        r.Session.EnableWorld = null;
        Assert.Equal(0, r.Session.Enchant(EnhanceTarget.InBag(0), 930).Code);
    }

    // ------------------------------------------------------------------ 套装

    [Fact]
    public void AWornSetAddsItsBonusesAndOneMissingPieceDoesNotTrigger()
    {
        Rig r = Make();
        Wear(r, 940);
        Wear(r, 941);
        EquipSetState two = Assert.Single(r.Session.ActiveSets());
        Assert.Equal((940, 2, 3, false), (two.Master, two.Worn, two.Total, two.FullActive));
        Assert.Equal((20.0, 0.0), (r.Hero.WornStats[EquipStat.MpMax], r.Hero.WornStats[EquipStat.HpMax]));
        long hp = r.Hero.Stats.HpMax;

        Wear(r, 942);
        Assert.True(Assert.Single(r.Session.ActiveSets()).FullActive);
        Assert.Equal(100.0, r.Hero.WornStats[EquipStat.HpMax]);
        Assert.Equal(hp + 1000, r.Hero.Stats.HpMax);

        r.Hero.Worn.Take(EquipmentParts.Pants);
        Assert.False(Assert.Single(r.Session.ActiveSets()).FullActive);
        Assert.Equal(hp, r.Hero.Stats.HpMax);
    }

    [Fact]
    public void NoWornSetMeansNoActiveSets()
    {
        Rig r = Make();
        Wear(r, 900);
        Assert.Empty(r.Session.ActiveSets());
    }

    // ------------------------------------------------------------------ 存档

    private static HeroSave Reparse(HeroSave save)
    {
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave back, out string error), error);
        return back;
    }

    private static Rig Fresh(Rig like)
    {
        var rig = new Rig { Defs = like.Defs, Roll = new EnhanceRoll() };
        rig.Hero = ExpTests.Progress(level: 50);
        rig.Settings = new UpgradeSettings { NormalTable = Table(), Random = rig.Roll };
        rig.Session = new EnhanceSession(rig.Hero, rig.Lookup, rig.Settings);
        return rig;
    }

    [Fact]
    public void TheStatesComeBackAfterASaveAndLoadRoundTrip()
    {
        Rig r = Make();
        r.Hero.Bag.Add(r.Def(901), 1);                                                                   // 背包里一件普通的
        r.Hero.Bag.Add(r.Def(901).WithInstance(new EquipInstance(3, amplified: true, identified: false, gradeValue: 77)), 1);   // 同编号第二件带状态
        r.Hero.Bag.Add(r.Def(902).WithInstance(new EquipInstance(0, enchantCode: 931, damaged: true)), 1);
        r.Hero.Worn.Restore(EquipmentParts.Weapon, r.Def(900).WithInstance(new EquipInstance(5, enchantCode: 931)));
        r.Hero.Worn.Restore(EquipmentParts.Coat, r.Def(901));                                            // 身上的普通装备不写状态

        HeroSave save = r.Hero.Snapshot();
        r.Session.Capture(save);
        Assert.Equal(3, save.EquipStates.Count);
        HeroSave back = Reparse(save);
        Assert.Equal(3, back.EquipStates.Count);

        Rig n = Fresh(r);
        n.Hero.Restore(back, n.Lookup);
        EquipStateReport report = n.Session.Restore(back);
        Assert.Equal((3, 0), (report.Applied, report.Dropped));
        Assert.Equal(new EquipInstance(5, enchantCode: 931), n.Hero.Worn.Get(EquipmentParts.Weapon).Instance);
        Assert.Null(n.Hero.Worn.Get(EquipmentParts.Coat).Instance);
        IReadOnlyList<ItemStack> gears = Gears(n);
        Assert.Equal(new[] { 901, 901, 902 }, gears.Select(s => s.Info.Code));
        Assert.Null(gears[0].Info.Instance);
        Assert.Equal(new EquipInstance(3, true, false, 0, 77), gears[1].Info.Instance);
        Assert.Equal(new EquipInstance(0, enchantCode: 931, damaged: true), gears[2].Info.Instance);
        Assert.Equal(r.Hero.WornStats, n.Hero.WornStats);
        Assert.Equal(r.Hero.Stats.PhysicalAttack, n.Hero.Stats.PhysicalAttack);
    }

    [Fact]
    public void BagAndWornRowsKeepOnlyCodesSoOldProgramsReadTheSameSave()
    {
        Rig r = Make();
        r.Hero.Bag.Add(r.Def(900).WithInstance(new EquipInstance(3)), 1);
        HeroSave save = r.Hero.Snapshot();
        string before = save.ToText();
        r.Session.Capture(save);
        string after = save.ToText();
        Assert.DoesNotContain("equipstate", before, StringComparison.Ordinal);
        Assert.Contains("equipstate 0 0 900 3 0 0 0\n", after);
        Assert.Equal(before, after.Replace("equipstate 0 0 900 3 0 0 0\n", string.Empty));   // 去掉新行,与原来逐字相同
    }

    [Fact]
    public void ASaveWithoutStateRowsLoadsEveryEquipmentAsPlain()
    {
        Rig r = Make();
        Give(r, 900);
        Wear(r, 901);
        HeroSave old = Reparse(r.Hero.Snapshot());
        Assert.Empty(old.EquipStates);
        Rig n = Fresh(r);
        n.Hero.Restore(old, n.Lookup);
        EquipStateReport report = n.Session.Restore(old);
        Assert.Equal((0, 0), (report.Applied, report.Dropped));
        Assert.Same(r.Def(900), Gears(n)[0].Info);
        Assert.Same(r.Def(901), n.Hero.Worn.Get(EquipmentParts.Coat));
    }

    [Fact]
    public void RowsThatNoLongerMatchAnEquipmentAreDroppedAndCounted()
    {
        Rig r = Make();
        Give(r, 900);
        Wear(r, 901);
        HeroSave save = r.Hero.Snapshot();
        save.EquipStates.Add(new SavedEquipState(0, 0, 900, 4, 0, 0, 0));   // 对得上
        save.EquipStates.Add(new SavedEquipState(0, 1, 900, 4, 0, 0, 0));   // 背包里只有一件 900
        save.EquipStates.Add(new SavedEquipState(0, 0, 901, 4, 0, 0, 0));   // 第 0 件是 900 不是 901
        save.EquipStates.Add(new SavedEquipState(1, EquipmentParts.Coat, 901, 6, 0, 0, 0));   // 对得上
        save.EquipStates.Add(new SavedEquipState(1, EquipmentParts.Coat, 900, 6, 0, 0, 0));   // 部位上穿的不是 900
        save.EquipStates.Add(new SavedEquipState(1, EquipmentParts.Ring, 902, 6, 0, 0, 0));   // 部位上没东西
        Rig n = Fresh(r);
        n.Hero.Restore(save, n.Lookup);
        EquipStateReport report = n.Session.Restore(save);
        Assert.Equal((2, 4), (report.Applied, report.Dropped));
        Assert.Equal(4, Gears(n)[0].Info.Instance.Upgrade);
        Assert.Equal(6, n.Hero.Worn.Get(EquipmentParts.Coat).Instance.Upgrade);
    }

    [Fact]
    public void RestoringAStateNeverDisturbsAnotherItemWithTheSameCode()
    {
        Rig r = Make();
        Give(r, 900);
        Give(r, 900);
        HeroSave save = r.Hero.Snapshot();
        save.EquipStates.Add(new SavedEquipState(0, 1, 900, 9, 0, 0, 0));
        Rig n = Fresh(r);
        n.Hero.Restore(save, n.Lookup);
        n.Session.Restore(save);
        Assert.Null(Gears(n)[0].Info.Instance);
        Assert.Equal(9, Gears(n)[1].Info.Instance.Upgrade);
        Assert.Same(n.Def(900), Gears(n)[1].Info.Definition);
    }

    [Fact]
    public void CaptureReplacesWhateverWasInTheSaveBefore()
    {
        Rig r = Make();
        r.Hero.Bag.Add(r.Def(900).WithInstance(new EquipInstance(3)), 1);
        HeroSave save = r.Hero.Snapshot();
        save.EquipStates.Add(new SavedEquipState(0, 5, 999, 1, 0, 0, 0));
        r.Session.Capture(save);
        r.Session.Capture(save);
        Assert.Equal(new SavedEquipState(0, 0, 900, 3, 0, 0, 0), Assert.Single(save.EquipStates));
    }

    [Fact]
    public void SaveArgumentsAreChecked()
    {
        Rig r = Make();
        Assert.Throws<ArgumentNullException>(() => r.Session.Capture(null));
        Assert.Throws<ArgumentNullException>(() => r.Session.Restore(null));
    }

    [Fact]
    public void ARestoredWornStateUpdatesTheStatsThroughTheOverlay()
    {
        Rig r = Make();
        Wear(r, 900);
        HeroSave save = r.Hero.Snapshot();
        save.EquipStates.Add(new SavedEquipState(1, EquipmentParts.Weapon, 900, 0, 0, 931, 0));
        Rig n = Fresh(r);
        n.Hero.Restore(save, n.Lookup);
        Assert.Equal(10.0, n.Hero.WornStats[EquipStat.PhysicalAttack]);
        n.Session.Restore(save);
        Assert.Equal(17.0, n.Hero.WornStats[EquipStat.PhysicalAttack]);   // 附魔卡的 7 点接上了
    }
}
