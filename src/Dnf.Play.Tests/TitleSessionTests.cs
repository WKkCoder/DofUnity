using Dnf.Game;
using Dnf.Game.Tests;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 称号会话(本项目自己的宿主层设计,规则取自服务端静态读码):称号的穿戴与属性汇总(穿上加、脱下扣、与装备/时装互不干扰且与次序无关)、
/// 图鉴与背包/装备栏之间的搬运(放入、放回、直接穿)、成就触发与核对接主角进度、收集加成的开关、以及图鉴与成就进度写进存档与读回。
/// 称号 901 起、成就 9001 起、任务 8001 起、数值是随手写的小整数(属性按十分之一单位存,所以 +10 在属性块里是 100);数据全是合成的(见 <see cref="TitleFixtures"/>)。
/// </summary>
public class TitleSessionTests
{
    private sealed class Rig
    {
        public HeroProgress Hero { get; init; }

        public TitleSession Session { get; init; }

        public Dictionary<int, ItemInfo> Items { get; init; }

        public AvatarSlots Avatar { get; } = new AvatarSlots();

        public ItemInfo Item(int code) => Items[code];

        public int[] BagCodes() => Hero.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code).ToArray();

        /// <summary>背包里第一件这个编号的物品所在的装备页下标。</summary>
        public int BagIndexOf(int code) => Array.IndexOf(BagCodes(), code);
    }

    private static Rig Make(int level = 10)
    {
        HeroProgress hero = Progress(level: level);
        Dictionary<int, ItemInfo> items = TitleFixtures.Items();
        var session = new TitleSession(hero, TitleFixtures.NewSystem(items), TitleFixtures.Lookup(items));
        return new Rig { Hero = hero, Session = session, Items = items };
    }

    private static (long, long, long, long, long, long) Fields(StatBlock s)
    {
        return (s.HpMax, s.MpMax, s.PhysicalAttack, s.PhysicalDefense, s.MagicalAttack, s.MagicalDefense);
    }

    private static ItemInfo Ring(int code, double hp, double physicalAttack)
    {
        var item = new ItemInfo(code, "equipment/test/r" + code + ".equ", ItemKind.Equipment)
        {
            Part = EquipmentParts.Ring,
            MinimumLevel = 1,
            Stats = EquipStats.Empty.With(EquipStat.HpMax, hp).With(EquipStat.PhysicalAttack, physicalAttack),
        };
        item.UsableJobs.Add("[all]");
        return item;
    }

    private static ItemInfo Hat(int code)
    {
        var item = new ItemInfo(code, "equipment/test/a" + code + ".equ", ItemKind.Equipment) { Part = EquipmentParts.HatAvatar, MinimumLevel = 1 };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    // ------------------------------------------------------------------ 穿戴与汇总

    [Fact]
    public void WearingATitleAddsItsStatsToTheSummaryAndTakingItOffRemovesThemExactly()
    {
        Rig r = Make();
        StatBlock b = r.Hero.BaseStats;
        r.Hero.AddItem(r.Item(TitleFixtures.General0));                                                  // 生命 +10
        Assert.Equal(Fields(b), Fields(r.Session.Summary().Total));                                      // 在背包里不算

        Assert.Equal(EquipResult.Ok, r.Session.Wear(0));
        TitleSummary s = r.Session.Summary();
        Assert.Equal(b.HpMax + 100, s.Total.HpMax);
        Assert.Equal(b.HpMax + 100, s.Hero.HpMax);
        Assert.Equal((b.MpMax, b.PhysicalAttack), (s.Total.MpMax, s.Total.PhysicalAttack));              // 别的项不动
        Assert.Equal((10.0, 10.0), (s.WornTitle[EquipStat.HpMax], s.Equipment[EquipStat.HpMax]));
        Assert.Same(r.Item(TitleFixtures.General0), r.Session.WornTitle);
        Assert.Empty(r.BagCodes());

        Assert.True(r.Session.Unwear());
        Assert.Equal(Fields(b), Fields(r.Session.Summary().Total));
        Assert.True(r.Session.Summary().WornTitle.IsEmpty);
        Assert.Null(r.Session.WornTitle);
        Assert.Equal(new[] { TitleFixtures.General0 }, r.BagCodes());
        Assert.False(r.Session.Unwear());                                                                // 脱第二次什么也不会扣
        Assert.Equal(Fields(b), Fields(r.Session.Summary().Total));
    }

    [Fact]
    public void ATitleAddsToTheHeroStatsThroughTheExistingEquipmentSlot()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.General1));                                                  // 物攻 +5
        StatBlock before = r.Hero.Stats;
        int version = r.Hero.Version;
        r.Session.Wear(0);
        Assert.Equal(before.PhysicalAttack + 50, r.Hero.Stats.PhysicalAttack);                           // 战斗会话读的 HeroProgress.Stats 里就有称号
        Assert.Equal(5.0, r.Hero.WornStats[EquipStat.PhysicalAttack]);
        Assert.True(r.Hero.Version > version);
        Assert.Equal(EquipmentParts.TitleName, r.Session.WornTitle.Part);
    }

    [Fact]
    public void StackingWithEquipmentIsTheSumOfTheParts()
    {
        Rig r = Make();
        ItemInfo ring = Ring(950, 20, 5);
        r.Hero.AddItem(ring);
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        StatBlock b = r.Hero.BaseStats;
        r.Hero.Equip(0);
        r.Session.Wear(0);
        Assert.Equal(b.HpMax + 200 + 100, r.Session.Summary().Total.HpMax);
        Assert.Equal(b.PhysicalAttack + 50, r.Session.Summary().Total.PhysicalAttack);
        Assert.Equal(Fields(b + ring.Stats.ToStatBlock() + r.Item(TitleFixtures.General0).Stats.ToStatBlock()), Fields(r.Session.Summary().Total));
        Assert.Equal((20.0 + 10.0, 5.0), (r.Session.Summary().Equipment[EquipStat.HpMax], r.Session.Summary().Equipment[EquipStat.PhysicalAttack]));
    }

    [Fact]
    public void TheOrderOfWearingTheTitleAndOtherEquipmentDoesNotChangeTheSum()
    {
        Rig ringFirst = Make();
        ringFirst.Hero.AddItem(Ring(950, 20, 5));
        ringFirst.Hero.AddItem(ringFirst.Item(TitleFixtures.General0));
        Assert.Equal(EquipResult.Ok, ringFirst.Hero.Equip(0));
        Assert.Equal(EquipResult.Ok, ringFirst.Session.Wear(0));

        Rig titleFirst = Make();
        titleFirst.Hero.AddItem(Ring(950, 20, 5));
        titleFirst.Hero.AddItem(titleFirst.Item(TitleFixtures.General0));
        Assert.Equal(EquipResult.Ok, titleFirst.Session.Wear(1));
        Assert.Equal(EquipResult.Ok, titleFirst.Hero.Equip(0));

        Assert.Equal(Fields(ringFirst.Session.Summary().Total), Fields(titleFirst.Session.Summary().Total));
        Assert.Equal(Fields(ringFirst.Hero.Stats), Fields(titleFirst.Hero.Stats));

        // 先脱谁也一样:各脱掉一件,剩下的都是另一件的账
        ringFirst.Hero.Unequip(EquipmentParts.Ring);
        titleFirst.Session.Unwear();
        Assert.Equal(ringFirst.Hero.BaseStats.HpMax + 100, ringFirst.Session.Summary().Total.HpMax);   // 只剩称号
        Assert.Equal(titleFirst.Hero.BaseStats.HpMax + 200, titleFirst.Session.Summary().Total.HpMax);  // 只剩戒指
    }

    [Fact]
    public void WearingATitleLeavesTheOtherSlotsAndTheAvatarSlotsAlone()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        r.Hero.AddItem(Ring(950, 20, 5));
        r.Hero.AddItem(Hat(960));
        r.Hero.Equip(1);                                                                                 // 戒指
        Assert.Equal(EquipResult.Ok, AvatarActions.Wear(r.Hero.Bag, r.Avatar, 1, r.Hero.Job, r.Hero.Level));   // 时装(帽子)
        int avatarVersion = r.Avatar.Version;
        Assert.Equal(EquipResult.Ok, r.Session.Wear(0));
        Assert.Equal((2, true), (r.Hero.EquippedCount, r.Hero.Worn.Get(EquipmentParts.Ring) != null));    // 戒指与称号各占各的格
        Assert.Equal(960, r.Avatar.Get(EquipmentParts.HatAvatar).Code);
        Assert.Equal(avatarVersion, r.Avatar.Version);                                                   // 称号不碰时装栏
        Assert.True(r.Session.Unwear());
        Assert.Equal(1, r.Hero.EquippedCount);                                                           // 脱称号不影响戒指
        Assert.NotNull(r.Avatar.Get(EquipmentParts.HatAvatar));
        Assert.Equal(avatarVersion, r.Avatar.Version);
    }

    [Fact]
    public void WearingAnotherTitleReplacesTheWornOneAndPutsItBackInTheSameBagSlot()
    {
        Rig r = Make();
        StatBlock b = r.Hero.BaseStats;
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        r.Hero.AddItem(r.Item(TitleFixtures.General1));
        Assert.Equal(EquipResult.Ok, r.Session.Wear(0));
        Assert.Equal(EquipResult.Ok, r.Session.Wear(0));                                                 // 背包里剩的是 902(下标 0)
        Assert.Equal(TitleFixtures.General1, r.Session.WornTitle.Code);
        Assert.Equal(new[] { TitleFixtures.General0 }, r.BagCodes());
        Assert.Equal((b.HpMax, b.PhysicalAttack + 50), (r.Session.Summary().Total.HpMax, r.Session.Summary().Total.PhysicalAttack));   // 只留新称号的账
    }

    [Fact]
    public void OnlyTitleItemsCanBeWornThroughTheTitleSession()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.WeaponCode));
        r.Hero.AddItem(Hat(960));
        int version = r.Hero.Version;
        Assert.Equal(EquipResult.WrongSlot, r.Session.Wear(0));
        Assert.Equal(EquipResult.WrongSlot, r.Session.Wear(1));
        Assert.Equal(EquipResult.NoItem, r.Session.Wear(9));
        Assert.Equal(EquipResult.NoItem, r.Session.Wear(-1));
        Assert.Equal(version, r.Hero.Version);
        Assert.Equal(2, r.BagCodes().Length);
        Assert.False(TitleSession.IsTitleItem(null));
        Assert.False(TitleSession.IsTitleItem(r.Item(TitleFixtures.StackableCode)));
        Assert.False(TitleSession.IsTitleItem(r.Item(TitleFixtures.WeaponCode)));
        Assert.True(TitleSession.IsTitleItem(r.Item(TitleFixtures.General0)));
    }

    [Fact]
    public void ARefusedWearForJobOrLevelChangesNothing()
    {
        Rig r = Make();
        ItemInfo wrongJob = TitleFixtures.TitleItem(980);
        wrongJob.UsableJobs.Clear();
        wrongJob.UsableJobs.Add("[priest]");
        ItemInfo tooHigh = TitleFixtures.TitleItem(981, null, level: 50);
        r.Hero.AddItem(wrongJob);
        r.Hero.AddItem(tooHigh);
        Assert.Equal(EquipResult.WrongJob, r.Session.Wear(0));
        Assert.Equal(EquipResult.LevelTooLow, r.Session.Wear(1));
        Assert.Null(r.Session.WornTitle);
        Assert.Equal(2, r.BagCodes().Length);
    }

    [Fact]
    public void TheWornTitleAndATitleInTheBookDoNotExcludeEachOther()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.General5));
        Assert.Equal(TitleBookResult.Ok, r.Session.PutFromBag(0, TitleCategory.General, 5));            // 图鉴里放一件 905
        r.Hero.AddItem(TitleFixtures.TitleItem(TitleFixtures.General5, EquipStats.Empty.With(EquipStat.MagicalAttack, 6)));
        Assert.Equal(EquipResult.Ok, r.Session.Wear(0));                                                 // 身上再穿另一件同编号的:服务端读码里两边没有互斥
        Assert.Equal((TitleFixtures.General5, TitleFixtures.General5), (r.Session.Book.CodeAt(TitleCategory.General, 5), r.Session.WornTitle.Code));
        Assert.Equal(r.Hero.BaseStats.MagicalAttack + 60, r.Session.Summary().Total.MagicalAttack);      // 图鉴里的那件不加属性,只算穿着的
    }

    [Fact]
    public void TheWornAppearanceComesFromTheCatalogWhenOneIsAttached()
    {
        Fixture fx = new Fixture()
            .List(ItemCatalog.EquipmentListPath, (901, "character/common/title/synth901.equ"))
            .Tokens("equipment/character/common/title/synth901.equ", w => w.Tag("[equipment type]").Str("[title name]").Tag("[custom animation]").Str("a.ani").Tag("[usable period]").Int(30));
        HeroProgress hero = Progress();
        Dictionary<int, ItemInfo> items = TitleFixtures.Items();
        var withCatalog = new TitleSession(hero, TitleFixtures.NewSystem(items), TitleFixtures.Lookup(items), new TitleCatalog(new GameData(fx.Build())));
        Assert.Null(withCatalog.WornAppearance);                                                         // 没穿
        hero.AddItem(items[TitleFixtures.General0]);
        withCatalog.Wear(0);
        Assert.Equal(("a.ani", 30, true), (withCatalog.WornAppearance.AnimationPath, withCatalog.WornAppearance.UsablePeriodDays, withCatalog.WornAppearance.IsTitle));

        Rig plain = Make();
        plain.Hero.AddItem(plain.Item(TitleFixtures.General0));
        plain.Session.Wear(0);
        Assert.Null(plain.Session.WornAppearance);                                                       // 没接目录
    }

    // ------------------------------------------------------------------ 放入图鉴

    [Fact]
    public void PuttingFromTheBagMovesTheTitleIntoTheBookAndTheDisplacedOneTakesItsBagSlot()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.General5));
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        Assert.Equal(TitleBookResult.Ok, r.Session.PutFromBag(0, TitleCategory.General, 5));            // 905 进第 5 格(不绑成就,什么都收)
        Assert.Equal(new[] { TitleFixtures.General0 }, r.BagCodes());
        Assert.Equal(TitleFixtures.General5, r.Session.Book.CodeAt(TitleCategory.General, 5));

        Assert.Equal(TitleBookResult.Ok, r.Session.PutFromBag(0, TitleCategory.General, 5));            // 901 换掉 905:905 回到原来 901 的位置
        Assert.Equal(new[] { TitleFixtures.General5 }, r.BagCodes());
        Assert.Equal(TitleFixtures.General0, r.Session.Book.CodeAt(TitleCategory.General, 5));
    }

    [Fact]
    public void ARefusedPutKeepsTheBagAndTheBookAsTheyWere()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.General1));                                                  // 902:不属于第 0 格
        r.Hero.AddItem(r.Item(TitleFixtures.WeaponCode));
        int version = r.Session.Version;
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromBag(0, TitleCategory.General, 0));
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromBag(1, TitleCategory.Event, 0));         // 武器:规则层的活动格什么都收,会话层只收称号物品
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromBag(5, TitleCategory.General, 0));       // 背包里没有这一格
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromBag(-1, TitleCategory.General, 0));
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromBag(0, TitleCategory.General, 70));      // 超出格数上限
        Assert.Equal(2, r.BagCodes().Length);
        Assert.Equal((0, version), (r.Session.Book.TotalCount, r.Session.Version));
    }

    [Fact]
    public void APutOntoASlotWhoseCurrentTitleCannotBeLookedUpIsRefusedAndChangesNothing()
    {
        Rig r = Make();
        r.Session.Book.Restore(TitleCategory.General, 5, 4242);                                          // 物品库里没有的编号占着这一格:换下来的放不回去
        r.Hero.AddItem(r.Item(TitleFixtures.General5));
        int version = r.Session.Version;
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromBag(0, TitleCategory.General, 5));
        Assert.Equal(4242, r.Session.Book.CodeAt(TitleCategory.General, 5));
        Assert.Equal(new[] { TitleFixtures.General5 }, r.BagCodes());
        Assert.Equal(version, r.Session.Version);

        Assert.Equal(EquipResult.Ok, r.Session.Wear(0));                                                 // 身上的那件同样放不进这一格
        int heroVersion = r.Hero.Version;
        Assert.Equal(TitleBookResult.Mismatch, r.Session.PutFromWorn(TitleCategory.General, 5));
        Assert.Equal(4242, r.Session.Book.CodeAt(TitleCategory.General, 5));
        Assert.Equal(TitleFixtures.General5, r.Session.WornTitle.Code);
        Assert.Equal((version, heroVersion), (r.Session.Version, r.Hero.Version));
    }

    [Fact]
    public void APutIsRefusedForALockedPageAndForTheLevelLines()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.Specific0));
        Assert.Equal(TitleBookResult.SectionLocked, r.Session.PutFromBag(0, TitleCategory.Specific, 0));

        Rig low = Make(level: 9);
        low.Hero.AddItem(low.Item(TitleFixtures.General0));
        Assert.Equal(TitleBookResult.LevelTooLow, low.Session.PutFromBag(0, TitleCategory.General, 0));
        low.Hero.SetLevel(10);
        Assert.Equal(TitleBookResult.Ok, low.Session.PutFromBag(0, TitleCategory.General, 0));

        Rig mid = Make(level: 69);
        mid.Hero.AddItem(mid.Item(TitleFixtures.Despair0));
        Assert.Equal(TitleBookResult.DespairLevel, mid.Session.PutFromBag(0, TitleCategory.Despair, 0));
        mid.Hero.SetLevel(70);
        Assert.Equal(TitleBookResult.Ok, mid.Session.PutFromBag(0, TitleCategory.Despair, 0));
    }

    [Fact]
    public void PuttingTheWornTitleClearsItsSlotAndBringsTheDisplacedOneOntoTheBody()
    {
        Rig r = Make();
        StatBlock b = r.Hero.BaseStats;
        r.Hero.AddItem(r.Item(TitleFixtures.General5));
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        r.Session.PutFromBag(0, TitleCategory.General, 5);                                               // 图鉴第 5 格里放着 905(魔攻 +6)
        r.Session.Wear(0);                                                                               // 穿上 901(生命 +10)
        Assert.Equal(b.HpMax + 100, r.Session.Summary().Total.HpMax);

        Assert.Equal(TitleBookResult.Ok, r.Session.PutFromWorn(TitleCategory.General, 5));              // 901 进图鉴,905 换到身上
        Assert.Equal(TitleFixtures.General0, r.Session.Book.CodeAt(TitleCategory.General, 5));
        Assert.Equal(TitleFixtures.General5, r.Session.WornTitle.Code);
        Assert.Equal((b.HpMax, b.MagicalAttack + 60), (r.Session.Summary().Total.HpMax, r.Session.Summary().Total.MagicalAttack));

        Rig plain = Make();
        plain.Hero.AddItem(plain.Item(TitleFixtures.General0));
        plain.Session.Wear(0);
        Assert.Equal(TitleBookResult.Ok, plain.Session.PutFromWorn(TitleCategory.General, 0));          // 空格:身上腾空,属性扣掉
        Assert.Null(plain.Session.WornTitle);
        Assert.Equal(Fields(plain.Hero.BaseStats), Fields(plain.Session.Summary().Total));
        Assert.Equal(TitleFixtures.General0, plain.Session.Book.CodeAt(TitleCategory.General, 0));
    }

    [Fact]
    public void PuttingTheWornTitleKeepsTheOtherWornSlotsAndRefusesWhenNothingIsWorn()
    {
        Rig r = Make();
        r.Hero.AddItem(Ring(950, 20, 5));
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        r.Hero.Equip(0);
        r.Session.Wear(0);
        StatBlock withBoth = r.Hero.Stats;
        Assert.Equal(TitleBookResult.Ok, r.Session.PutFromWorn(TitleCategory.General, 0));
        Assert.Equal(950, r.Hero.Worn.Get(EquipmentParts.Ring).Code);                                    // 戒指还在
        Assert.Equal(withBoth.HpMax - 100, r.Hero.Stats.HpMax);                                          // 只扣了称号的那一份

        Rig none = Make();
        Assert.Equal(TitleBookResult.Mismatch, none.Session.PutFromWorn(TitleCategory.General, 0));
    }

    // ------------------------------------------------------------------ 取出图鉴

    [Fact]
    public void TakingToTheBagIsRefusedForAnAchievementSlotAndAllowedForOneWithout()
    {
        Rig r = Make();
        r.Hero.AddItem(r.Item(TitleFixtures.General0));
        r.Hero.AddItem(r.Item(TitleFixtures.General5));
        r.Session.PutFromBag(0, TitleCategory.General, 0);                                               // 绑成就的格
        r.Session.PutFromBag(0, TitleCategory.General, 5);                                               // 不绑成就的格
        Assert.Equal(TitleBookResult.NotMovable, r.Session.TakeToBag(TitleCategory.General, 0));
        Assert.Equal(TitleFixtures.General0, r.Session.Book.CodeAt(TitleCategory.General, 0));
        Assert.Equal(TitleBookResult.Ok, r.Session.TakeToBag(TitleCategory.General, 5));
        Assert.Equal(new[] { TitleFixtures.General5 }, r.BagCodes());
        Assert.Equal(0, r.Session.Book.CodeAt(TitleCategory.General, 5));
    }

    [Fact]
    public void TakingToTheBagChecksTheRangeAndTheSlotBeforeTheBag()
    {
        Rig r = Make();
        Assert.Equal(TitleBookResult.NoData, r.Session.TakeToBag(TitleCategory.General, 70));
        Assert.Equal(TitleBookResult.NoData, r.Session.TakeToBag((TitleCategory)9, 0));
        Assert.Equal(TitleBookResult.Mismatch, r.Session.TakeToBag(TitleCategory.General, 5));           // 空格
        r.Session.Book.Restore(TitleCategory.General, 5, 4242);                                          // 物品库里没有的编号
        Assert.Equal(TitleBookResult.Mismatch, r.Session.TakeToBag(TitleCategory.General, 5));
        Assert.Equal(4242, r.Session.Book.CodeAt(TitleCategory.General, 5));
    }

    [Fact]
    public void TakingToAFullBagReportsBagFullAndKeepsTheBook()
    {
        Rig r = Make();
        r.Session.Book.Restore(TitleCategory.General, 5, TitleFixtures.General5);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.True(r.Hero.AddItem(TitleFixtures.TitleItem(5000 + i)));
        }
        Assert.Equal(TitleBookResult.BagFull, r.Session.TakeToBag(TitleCategory.General, 5));
        Assert.Equal(TitleFixtures.General5, r.Session.Book.CodeAt(TitleCategory.General, 5));
        Assert.Equal(Inventory.SlotsPerTab, r.BagCodes().Length);
    }

    [Fact]
    public void TakingAndWearingPutsAnAchievementSlotsTitleStraightOnTheBody()
    {
        Rig r = Make();
        StatBlock b = r.Hero.BaseStats;
        r.Session.Book.Restore(TitleCategory.General, 0, TitleFixtures.General0);                        // 绑成就的格:不能回背包,但能直接穿
        Assert.Equal(TitleBookResult.Ok, r.Session.TakeAndWear(TitleCategory.General, 0));
        Assert.Equal(TitleFixtures.General0, r.Session.WornTitle.Code);
        Assert.Equal(0, r.Session.Book.CodeAt(TitleCategory.General, 0));
        Assert.Equal(b.HpMax + 100, r.Session.Summary().Total.HpMax);
        Assert.Empty(r.BagCodes());
    }

    [Fact]
    public void TakingAndWearingNeedsAnEmptyTitleSlotAndAnItemTheHeroCanWear()
    {
        Rig r = Make();
        r.Session.Book.Restore(TitleCategory.General, 0, TitleFixtures.General0);
        r.Session.Book.Restore(TitleCategory.General, 5, TitleFixtures.General5);
        r.Hero.AddItem(r.Item(TitleFixtures.General1));
        r.Session.Wear(0);
        Assert.Equal(TitleBookResult.SlotUnavailable, r.Session.TakeAndWear(TitleCategory.General, 0));  // 身上已经穿着一件
        Assert.Equal(TitleFixtures.General0, r.Session.Book.CodeAt(TitleCategory.General, 0));
        Assert.Equal(TitleFixtures.General1, r.Session.WornTitle.Code);

        Assert.Equal(TitleBookResult.NoData, r.Session.TakeAndWear(TitleCategory.General, 70));
        Assert.Equal(TitleBookResult.Mismatch, r.Session.TakeAndWear(TitleCategory.General, 1));         // 空格

        Rig weak = Make(level: 10);
        ItemInfo needsLevel = TitleFixtures.TitleItem(TitleFixtures.General2, null, level: 50);
        weak.Items[TitleFixtures.General2] = needsLevel;
        weak.Session.Book.Restore(TitleCategory.General, 2, TitleFixtures.General2);
        Assert.Equal(TitleBookResult.NotEquipable, weak.Session.TakeAndWear(TitleCategory.General, 2));
        Assert.Equal(TitleFixtures.General2, weak.Session.Book.CodeAt(TitleCategory.General, 2));       // 失败不动图鉴
        Assert.Null(weak.Session.WornTitle);
        weak.Hero.SetLevel(50);
        Assert.Equal(TitleBookResult.Ok, weak.Session.TakeAndWear(TitleCategory.General, 2));
    }

    [Fact]
    public void BookMovesLeaveTheOtherWornSlotsAndTheSessionVersionMoves()
    {
        Rig r = Make();
        r.Hero.AddItem(Ring(950, 20, 5));
        r.Hero.Equip(0);
        r.Session.Book.Restore(TitleCategory.General, 0, TitleFixtures.General0);
        int version = r.Session.Version;
        int heroVersion = r.Hero.Version;
        Assert.Equal(TitleBookResult.Ok, r.Session.TakeAndWear(TitleCategory.General, 0));
        Assert.Equal(950, r.Hero.Worn.Get(EquipmentParts.Ring).Code);
        Assert.True(r.Session.Version > version);
        Assert.True(r.Hero.Version > heroVersion);
    }

    // ------------------------------------------------------------------ 成就接主角进度

    [Fact]
    public void TriggersUseTheHeroLevelAsTheCharacterLevel()
    {
        Rig r = Make(level: 9);
        Assert.Equal(AchievementTriggerCode.Invalid, r.Session.Trigger(9002, 3, 0, 0).Code);            // 9002 要 10 级
        r.Hero.SetLevel(10);
        TitleTriggerOutcome outcome = r.Session.Trigger(9002, 3, 0, 0);
        Assert.Equal((AchievementTriggerCode.Ok, true, TitleFixtures.General1, true), (outcome.Code, outcome.Completed, outcome.TitleCode, outcome.TitlePlaced));
        Assert.Equal(TitleFixtures.General1, r.Session.Book.CodeAt(TitleCategory.General, 1));
        Assert.Equal(AchievementTriggerCode.Rejected, r.Session.Trigger(9002, 3, 0, 0).Code);           // 已领取:不能重复
        Assert.Equal(1, r.Session.Book.TotalCount);
    }

    [Fact]
    public void VerifyAndAdjustReadTheHeroQuestLogAndCountTheWornTitleAsPresent()
    {
        Rig r = Make();
        Assert.Equal(0, r.Session.Verify());                                                             // 任务还没完成
        r.Hero.Quests.MarkCleared(8001);
        Assert.Equal(1, r.Session.Verify());
        Assert.True(r.Session.Achievements.IsCleared(9001));
        Assert.Equal(TitleFixtures.General0, r.Session.Book.CodeAt(TitleCategory.General, 0));
        Assert.Equal(0, r.Session.Verify());

        Assert.Equal(TitleBookResult.Ok, r.Session.TakeAndWear(TitleCategory.General, 0));              // 称号穿在身上、图鉴格空着
        Assert.Equal(0, r.Session.Verify());                                                             // 穿着的算在:不补发
        Assert.Equal(0, r.Session.Book.CodeAt(TitleCategory.General, 0));

        Assert.True(r.Session.Unwear());                                                                 // 回到背包:服务端既不看背包也不看装备栏以外的地方
        Assert.Equal(1, r.Session.Verify());                                                             // 于是在图鉴里补一件
        Assert.Equal(TitleFixtures.General0, r.Session.Book.CodeAt(TitleCategory.General, 0));

        Rig adjust = Make();
        Assert.Equal(0, adjust.Session.Adjust());
        adjust.Hero.Quests.MarkCleared(8004);
        Assert.Equal(1, adjust.Session.Adjust());
        Assert.Equal(TitleFixtures.General4, adjust.Session.Book.CodeAt(TitleCategory.General, 4));
    }

    [Fact]
    public void TheCollectionBonusIsOffByDefaultAndAddsToTheTotalOnlyWhenSwitchedOn()
    {
        Rig r = Make();
        r.Session.Trigger(9001, 1, 0, 0);
        r.Session.Trigger(9002, 3, 0, 0);
        r.Session.Trigger(9003, 2, 1, 3);                                                                // 普通分类第 0 页收齐
        TitleSummary off = r.Session.Summary();
        Assert.False(r.Session.CollectionBonusEnabled);
        Assert.True(off.Collection.Stats.IsEmpty);
        Assert.Equal(Fields(off.Hero), Fields(off.Total));

        r.Session.CollectionBonusEnabled = true;
        TitleSummary on = r.Session.Summary();
        Assert.Equal((130.0, 50.0, 1), (on.Collection.Stats[EquipStat.HpMax], on.Collection.Stats[EquipStat.MpMax], on.Collection.PagesCounted));   // 默认奖励 100/50 + 本页 30
        Assert.Equal((off.Hero.HpMax + 1300, off.Hero.MpMax + 500), (on.Total.HpMax, on.Total.MpMax));
        Assert.Equal(Fields(off.Hero), Fields(on.Hero));                                                 // 加成只在汇总里,不进主角属性
        Assert.Equal(Fields(r.Hero.Stats), Fields(on.Hero));
        Assert.Equal(130.0, on.Combined[EquipStat.HpMax]);
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void CaptureAndRestoreRoundTripTheBookTheProgressAndThroughTheTextFormat()
    {
        Rig a = Make();
        a.Session.Trigger(9001, 1, 0, 0);                                                                // 完成:901 进第 0 格
        a.Session.Trigger(9002, 1, 0, 0);                                                                // 进行中:还差 2
        a.Session.Book.Restore(TitleCategory.Event, 3, TitleFixtures.General5);
        a.Hero.AddItem(a.Item(TitleFixtures.General1));
        a.Session.Wear(0);

        HeroSave save = a.Hero.Snapshot();
        a.Session.Capture(save);
        Assert.Equal(2, save.Titles.Count);
        Assert.Equal(2, save.Achievements.Count);
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);

        Rig b = Make();
        b.Hero.Restore(parsed, TitleFixtures.Lookup(b.Items));
        TitleRestoreReport report = b.Session.Restore(parsed);
        Assert.Equal((2, 0, 2, 0), (report.Titles, report.TitlesDropped, report.Achievements, report.AchievementsDropped));
        Assert.Equal(a.Session.Book.Entries().Select(e => (e.Category, e.Slot, e.Code)).ToArray(), b.Session.Book.Entries().Select(e => (e.Category, e.Slot, e.Code)).ToArray());
        Assert.Equal(a.Session.Achievements.Entries().ToArray(), b.Session.Achievements.Entries().ToArray());
        Assert.True(b.Session.Achievements.IsCleared(9001));
        Assert.Equal(2, b.Session.Achievements.ProgressOf(9002).Remaining0);
        Assert.Equal(TitleFixtures.General1, b.Session.WornTitle.Code);                                  // 穿着的称号随装备栏的 worn 行存
        Assert.Equal(Fields(a.Session.Summary().Total), Fields(b.Session.Summary().Total));

        // 读回来的进度继续算:再触发两次 9002 就完成
        Assert.False(b.Session.Trigger(9002, 1, 0, 0).Completed);
        Assert.True(b.Session.Trigger(9002, 1, 0, 0).Completed);
    }

    [Fact]
    public void CaptureReplacesWhateverTitleFieldsTheSaveAlreadyHad()
    {
        Rig r = Make();
        r.Session.Trigger(9001, 1, 0, 0);
        HeroSave save = r.Hero.Snapshot();
        save.Titles.Add(new SavedTitle(0, 9, 777));
        save.Achievements.Add(new SavedAchievement(1, 1, 1, 1));
        r.Session.Capture(save);
        r.Session.Capture(save);                                                                         // 写两次不会翻倍
        Assert.Equal(new[] { (0, 0, TitleFixtures.General0) }, save.Titles.Select(t => (t.Category, t.Slot, t.Code)).ToArray());
        Assert.Equal(new[] { (9001, 0, 0, 0) }, save.Achievements.Select(x => (x.Id, x.Remaining0, x.Remaining1, x.Remaining2)).ToArray());
    }

    [Fact]
    public void RestoreDropsWhatNoLongerFitsAndReportsHowMany()
    {
        Rig r = Make();
        r.Session.Trigger(9001, 1, 0, 0);                                                                // 先放点东西,读档会整个替换
        var save = new HeroSave();
        save.Titles.Add(new SavedTitle(0, 0, TitleFixtures.General0));                                   // 好的
        save.Titles.Add(new SavedTitle(0, 1, 4242));                                                     // 物品库里没有
        save.Titles.Add(new SavedTitle(0, 2, TitleFixtures.WeaponCode));                                 // 不是称号物品
        save.Titles.Add(new SavedTitle(9, 0, TitleFixtures.General0));                                   // 分类越界
        save.Titles.Add(new SavedTitle(0, 70, TitleFixtures.General0));                                  // 格越界
        save.Titles.Add(new SavedTitle(3, 3, TitleFixtures.General5));                                   // 好的(格定义外也收:服务端读档原样)
        save.Achievements.Add(new SavedAchievement(9002, 2, 0, 0));                                      // 好的
        save.Achievements.Add(new SavedAchievement(777, 1, 0, 0));                                       // 数据里没有这个成就
        save.Achievements.Add(new SavedAchievement(9003, 70000, 0, 0));                                  // 计数越界
        TitleRestoreReport report = r.Session.Restore(save);
        Assert.Equal((2, 4, 1, 2), (report.Titles, report.TitlesDropped, report.Achievements, report.AchievementsDropped));
        Assert.Equal(2, r.Session.Book.TotalCount);
        Assert.Equal(TitleFixtures.General5, r.Session.Book.CodeAt(TitleCategory.Event, 3));
        Assert.False(r.Session.Achievements.IsCleared(9001));                                            // 原来的进度被替换掉
        Assert.Equal(1, r.Session.Achievements.EntryCount);
        Assert.Empty(r.Session.Core.Outbox);
    }

    [Fact]
    public void RestoringAnOldSaveWithoutTitleFieldsEmptiesTheSession()
    {
        Rig r = Make();
        r.Session.Trigger(9001, 1, 0, 0);
        TitleRestoreReport report = r.Session.Restore(new HeroSave());
        Assert.Equal((0, 0, 0, 0), (report.Titles, report.TitlesDropped, report.Achievements, report.AchievementsDropped));
        Assert.Equal((0, 0), (r.Session.Book.TotalCount, r.Session.Achievements.EntryCount));
    }

    [Fact]
    public void ARepeatedAchievementRecordInTheSaveLetsTheLaterOneWin()
    {
        Rig r = Make();
        var save = new HeroSave();
        save.Achievements.Add(new SavedAchievement(9002, 2, 0, 0));
        save.Achievements.Add(new SavedAchievement(9002, 1, 0, 0));
        r.Session.Restore(save);
        Assert.Equal(1, r.Session.Achievements.ProgressOf(9002).Remaining0);
    }

    [Fact]
    public void ConstructionAndCaptureRejectNulls()
    {
        Rig r = Make();
        Assert.Throws<ArgumentNullException>(() => new TitleSession(null, r.Session.Core, TitleFixtures.Lookup()));
        Assert.Throws<ArgumentNullException>(() => new TitleSession(r.Hero, null, TitleFixtures.Lookup()));
        Assert.Throws<ArgumentNullException>(() => new TitleSession(r.Hero, r.Session.Core, null));
        Assert.Throws<ArgumentNullException>(() => r.Session.Capture(null));
        Assert.Throws<ArgumentNullException>(() => r.Session.Restore(null));
    }
}
