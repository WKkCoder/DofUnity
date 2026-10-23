using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;
using static Dnf.Play.Tests.DungeonSessionTests;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 地上的物品(证据 E-117):与金币共用抛出、落地与拾取(<see cref="FieldDrop"/>),图是物品脚本 [field image] 的帧,声音是 [move wav];
/// 战斗会话里击杀 → 掷骰 → 抛出 → 主角走近放进背包,背包满了留在地上。数据全是合成的(一行概率覆盖副本等级 1 到 200,
/// 稀有度分界让每次都落在稀有度 0,物品等级窗口只含怪物自己的等级,所以只有等级恰好等于怪物等级的物品进候选)。
/// </summary>
public class ItemDropSessionTests
{
    private const string Touch = "TEST_TOUCH";

    /// <summary>
    /// 合成的掉落表:金币概率 <paramref name="gold"/>、类型 <paramref name="type"/>(1 到 4)的物品概率 <paramref name="probability"/>(万分之几),其余类型 0。
    /// </summary>
    internal static MonsterDropTable Table(int probability = 10000, int type = 1, int gold = 0)
    {
        var values = new int[5];
        values[0] = gold;
        values[type] = probability;
        var b = new TokenStreamBuilder();
        b.Tag("[drop prob count]").Int(1);
        b.Tag("[drop prob]").Ints(1, 200).Ints(values);
        b.Tag("[basis of rarity dicision]").Ints(Enumerable.Repeat(1000000, 24).ToArray());
        b.Tag("[item drop ref table]");
        for (int level = 1; level <= 200; level++)
        {
            b.Ints(level, 0, 1);                       // 窗口 [等级 − 0, 等级 + 1 − 1] 就是等级本身
        }
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        return table;
    }

    /// <summary>一件合成的可叠加物品(材料),地上图是金币图包的第 26 帧(<see cref="DropSprites"/> 里有)。</summary>
    internal static ItemInfo StackItem(int code = 101, int grade = 10, string moveSound = Touch)
    {
        return new ItemInfo(code, "stackable/" + code + ".stk", ItemKind.Stackable)
        {
            Grade = grade,
            CreationRate = 10,
            StackableType = StackableTypes.Material,
            FieldImage = "Item/FieldImage.img",
            FieldFrame = 26,
            MoveSound = moveSound,
        };
    }

    private static ItemPool PoolOf(MonsterDropTable table, params ItemInfo[] items)
    {
        return new ItemPool(items, table);
    }

    /// <summary>按下攻击键、每 10 毫秒一步推到击杀(最多 1 秒)。</summary>
    private static void SwingUntilKill(RoomCombatSession s)
    {
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 100 && s.Kills == 0; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(1, s.Kills);
    }

    /// <summary>把主角挪到远处,免得他站在原地、东西一落地就被捡走。</summary>
    private static void Away(RoomCombatSession s)
    {
        s.Hero.Place(60, 100, false, HeroMotion.Rest, 0);
    }

    private static void LandAll(RoomCombatSession s)
    {
        for (int i = 0; i < 300 && s.Drops.Any(d => !d.Landed); i++)
        {
            s.Update(10, 0, 0, false);
        }
    }

    // ------------------------------------------------------------------ FieldDrop 本身

    [Fact]
    public void LaunchItemKeepsTheItemAndFliesLikeGold()
    {
        ItemInfo item = StackItem();
        FieldDrop thing = FieldDrop.LaunchItem(3, item, 300, 100, new DropRandom(9));
        FieldDrop gold = FieldDrop.Launch(3, 10, 300, 100, new DropRandom(9));
        Assert.True(thing.IsItem);
        Assert.Same(item, thing.Item);
        Assert.Equal(1, thing.Amount);
        Assert.False(gold.IsItem);
        Assert.Null(gold.Item);
        thing.Advance(5000);
        gold.Advance(5000);
        Assert.Equal((gold.X, gold.Y, gold.Z), (thing.X, thing.Y, thing.Z));       // 同一个种子:落点一样
        Assert.True(thing.Landed && thing.Rested);
    }

    [Fact]
    public void LaunchItemRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => FieldDrop.LaunchItem(1, null, 0, 0, new DropRandom(1)));
    }

    [Fact]
    public void ThePickupSoundIsTheMoveWavForAnItemAndTheAmountSoundForGold()
    {
        Assert.Equal(Touch, FieldDrop.LaunchItem(1, StackItem(), 0, 0, new DropRandom(1)).PickupSound);
        Assert.Null(FieldDrop.LaunchItem(1, StackItem(moveSound: null), 0, 0, new DropRandom(1)).PickupSound);
        Assert.Equal("GOLD_DROP1", FieldDrop.Launch(1, 5, 0, 0, new DropRandom(1)).PickupSound);
        Assert.Equal("GOLD_DROP2", FieldDrop.Launch(1, 500, 0, 0, new DropRandom(1)).PickupSound);
    }

    [Fact]
    public void TheItemSpriteIsTheScriptFieldImageFrame()
    {
        FieldDrop d = FieldDrop.LaunchItem(1, StackItem(), 300, 100, new DropRandom(1));
        d.Advance(5000);
        SceneSprite s = d.Sprite(new DropSprites(), new TownProjection(50, 200));
        Assert.NotNull(s);
        Assert.Equal(26, s.Frame.Width);                      // 合成图:帧宽 = 帧号
        Assert.Equal(FieldDrop.ImagePath, s.AnimationPath);
    }

    [Fact]
    public void AnItemWithoutAFieldImageOrWithAMissingFrameHasNoSprite()
    {
        var projection = new TownProjection(0, 0);
        ItemInfo noImage = StackItem();
        noImage.FieldImage = null;
        noImage.FieldFrame = -1;
        Assert.Null(FieldDrop.LaunchItem(1, noImage, 0, 0, new DropRandom(1)).Sprite(new DropSprites(), projection));
        ItemInfo missingFrame = StackItem();
        missingFrame.FieldFrame = 99;
        Assert.Null(FieldDrop.LaunchItem(1, missingFrame, 0, 0, new DropRandom(1)).Sprite(new DropSprites(), projection));
        Assert.Null(FieldDrop.LaunchItem(1, StackItem(), 0, 0, new DropRandom(1)).Sprite(new DropSprites(field: false), projection));
        Assert.Null(FieldDrop.LaunchItem(1, StackItem(), 0, 0, new DropRandom(1)).Sprite(null, projection));
    }

    // ------------------------------------------------------------------ MonsterDrops

    [Fact]
    public void MonsterDropsKeepsAGivenPoolAndDefaultsToNone()
    {
        MonsterDropTable table = Table();
        GoldGenTable gold = GoldDropSessionTests.Gold();
        ItemPool pool = PoolOf(table, StackItem());
        var with = new MonsterDrops(table, gold, null, pool);
        Assert.True(with.Available);
        Assert.Same(pool, with.Pool);
        Assert.Same(pool, with.Pool);
        var without = new MonsterDrops(table, gold, null);
        Assert.True(without.Available);
        Assert.Null(without.Pool);
        var failed = new MonsterDrops(null, null, "没有表");
        Assert.False(failed.Available);
        Assert.Null(failed.Pool);
        Assert.Equal("没有表", failed.Error);
    }

    // ------------------------------------------------------------------ 战斗会话

    [Fact]
    public void AKillDropsAnItemAndTheHeroPicksItUpIntoTheBag()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 10);
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table();
        ItemInfo item = StackItem();
        s.UseDrops(table, null, 4, PoolOf(table, item));
        SwingUntilKill(s);
        Away(s);
        Assert.Equal((1, 0), (s.ItemsDropped, s.GoldDropped));
        FieldDrop drop = Assert.Single(s.Drops);
        Assert.Same(item, drop.Item);
        Assert.Equal(0, s.ItemsPicked);
        LandAll(s);
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        var cues = new List<SoundCue>();
        s.DrainSounds(cues);
        cues.Clear();
        s.Update(10, 0, 0, false);                               // 走到了、没按键:东西还在地上,只是被选中
        Assert.Equal((0, 1), (s.ItemsPicked, s.Drops.Count));
        Assert.Same(drop, s.SelectedDrop);
        s.Update(10, 0, 0, true);                                // 攻击键就是拾取键
        s.DrainSounds(cues);
        Assert.Empty(s.Drops);
        Assert.Null(s.SelectedDrop);
        Assert.Equal(1, s.ItemsPicked);
        Assert.Same(item, s.LastItemPickup);
        Assert.Equal(1, progress.Bag.CountOf(101));
        Assert.Equal(1, progress.ItemsGained);
        Assert.Contains(cues, c => c.Key == Touch);
        Assert.Empty(s.Gains);                                  // 物品没有金额飘字
        Assert.Equal((0, 0L), (s.GoldPicked, progress.Gold));
    }

    [Fact]
    public void TheItemPickupNeedsALandedDropAndTheHeroNearIt()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table();
        s.UseDrops(table, null, 4, PoolOf(table, StackItem()));
        SwingUntilKill(s);
        FieldDrop drop = s.Drops[0];
        Assert.False(drop.Landed);
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);          // 还在空中:站在它下面按键也捡不到
        s.Update(10, 0, 0, true);
        Assert.Equal(0, s.ItemsPicked);
        Assert.Null(s.SelectedDrop);
        Away(s);
        LandAll(s);
        Assert.True(drop.Landed);
        s.Hero.Place(drop.X + FieldDrop.PickupHalfWidth + 40, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, true);
        Assert.Equal((0, 1), (s.ItemsPicked, s.Drops.Count));
        Assert.Null(s.SelectedDrop);
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, true);
        Assert.Equal((1, 0), (s.ItemsPicked, s.Drops.Count));
    }

    [Fact]
    public void WithoutAProgressTheItemIsStillPickedUpButGoesNowhere()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table();
        s.UseDrops(table, null, 4, PoolOf(table, StackItem()));
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        s.Hero.Place(s.Drops[0].X, s.Drops[0].Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, true);
        Assert.Null(s.Progress);
        Assert.Equal((1, 0), (s.ItemsPicked, s.Drops.Count));
    }

    [Fact]
    public void AFullBagLeavesTheItemOnTheGroundAndCountsItOnce()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 10);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.Equal(1, progress.Bag.Add(StackItem(1000 + i)));               // 材料页占满
        }
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table();
        s.UseDrops(table, null, 4, PoolOf(table, StackItem(101)));
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        FieldDrop drop = s.Drops[0];
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        Assert.Same(drop, s.SelectedDrop);                                       // 放不下也先被选中
        s.Update(10, 0, 0, true);                                                // 第一下按键被吃掉:提示放不下
        Assert.False(s.Hero.IsAttacking);
        Assert.Equal((0, 1, 1), (s.ItemsPicked, s.ItemsBagFull, s.Drops.Count));
        Assert.True(drop.Blocked);
        Assert.Null(s.SelectedDrop);                                             // 之后不再被选中,攻击键照常出招
        s.Update(10, 0, 0, true);
        Assert.True(s.Hero.IsAttacking);
        for (int i = 0; i < 4; i++)
        {
            s.Update(10, 0, 0, true);
        }
        Assert.Equal((0, 1, 1), (s.ItemsPicked, s.ItemsBagFull, s.Drops.Count));
        Assert.Equal(0, progress.ItemsGained);
        Assert.True(progress.Bag.Remove(1000));                                   // 腾出一格:再按键就捡起来
        s.Update(10, 0, 0, false);
        Assert.Same(drop, s.SelectedDrop);
        s.Update(10, 0, 0, true);
        Assert.Equal((1, 1, 0), (s.ItemsPicked, s.ItemsBagFull, s.Drops.Count));
        Assert.Equal(1, progress.Bag.CountOf(101));
    }

    [Fact]
    public void WalkingOverAnItemDoesNotPickItUpOnlyThePressDoes()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table();
        s.UseDrops(table, null, 4, PoolOf(table, StackItem()));
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        FieldDrop drop = s.Drops[0];
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        for (int i = 0; i < 20; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal((0, 1), (s.ItemsPicked, s.Drops.Count));
        Assert.Same(drop, s.SelectedDrop);
    }

    [Fact]
    public void ThePressPicksTheItemInsteadOfSwingingAndTheNextPressSwings()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table();
        s.UseDrops(table, null, 4, PoolOf(table, StackItem()));
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        for (int i = 0; i < 200 && s.Hero.IsAttacking; i++)
        {
            s.Update(10, 0, 0, false);                          // 等上一下挥完
        }
        FieldDrop drop = s.Drops[0];
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        s.Update(10, 0, 0, true);
        Assert.Equal(1, s.ItemsPicked);
        Assert.False(s.Hero.IsAttacking);                       // 这一下按键用来捡东西了
        s.Update(10, 0, 0, true);
        Assert.True(s.Hero.IsAttacking);                        // 地上没东西了:攻击键照常出招
    }

    [Fact]
    public void TheSelectedItemIsTheNearestReachableOne()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        Away(s);
        FieldDrop a = s.SpawnItem(StackItem(101), 300, 100);
        FieldDrop b = s.SpawnItem(StackItem(102), 300, 100);
        s.SpawnItem(StackItem(103), 300, 100);
        LandAll(s);
        s.Hero.Place((a.X + b.X) / 2, a.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        FieldDrop first = s.SelectedDrop;
        Assert.NotNull(first);
        Assert.True(first.IsInReach(s.Hero.X, s.Hero.Y, s.Hero.Z));
        double HeroDistance(FieldDrop d) => Math.Abs(d.X - s.Hero.X) + 4.0 * Math.Abs(d.Y - s.Hero.Y);
        foreach (FieldDrop other in s.Drops.Where(d => d != first && d.IsInReach(s.Hero.X, s.Hero.Y, s.Hero.Z)))
        {
            Assert.True(HeroDistance(first) <= HeroDistance(other));
        }
        s.Update(10, 0, 0, true);                                // 捡走最近的,下一件接着被选中(如果还够得着)
        Assert.Equal(1, s.ItemsPicked);
        Assert.NotSame(first, s.SelectedDrop);
    }

    [Fact]
    public void WithoutAnItemPoolOrWithZeroProbabilityNothingDrops()
    {
        using (var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10))
        {
            arena.Session.UseDrops(Table(), null, 4);                 // 没给物品池
            SwingUntilKill(arena.Session);
            Assert.Null(arena.Session.ItemPool);
            Assert.Equal((0, 0), (arena.Session.ItemsDropped, arena.Session.Drops.Count));
        }
        using (var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10))
        {
            MonsterDropTable table = Table(probability: 0);
            arena.Session.UseDrops(table, null, 4, PoolOf(table, StackItem()));
            SwingUntilKill(arena.Session);
            Assert.Equal(0, arena.Session.ItemsDropped);
        }
    }

    [Fact]
    public void TheItemIsChosenByTheMonsterLevel()
    {
        // 物品等级 10;窗口为 0:20 级怪物的候选是空的(骰子照扔),10 级怪物才掉
        foreach ((int level, int dropped) in new[] { (10, 1), (20, 0) })
        {
            using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: level);
            MonsterDropTable table = Table();
            arena.Session.UseDrops(table, null, 4, PoolOf(table, StackItem(grade: 10)));
            SwingUntilKill(arena.Session);
            Assert.Equal(dropped, arena.Session.ItemsDropped);
        }
    }

    [Fact]
    public void GoldIsThrownBeforeTheItem()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        MonsterDropTable table = Table(gold: 10000);
        arena.Session.UseDrops(table, GoldDropSessionTests.Gold(), 4, PoolOf(table, StackItem()));
        SwingUntilKill(arena.Session);
        Assert.Equal((1, 1), (arena.Session.GoldDropped, arena.Session.ItemsDropped));
        Assert.Equal(new[] { false, true }, arena.Session.Drops.Select(d => d.IsItem).ToArray());
    }

    [Fact]
    public void ItemsAndGoldAreCountedApart()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 10);
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        MonsterDropTable table = Table(gold: 10000);
        s.UseDrops(table, GoldDropSessionTests.Gold(), 4, PoolOf(table, StackItem()));
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        foreach (FieldDrop d in s.Drops.ToList())
        {
            s.Hero.Place(d.X, d.Y, false, HeroMotion.Rest, 0);
            s.Update(10, 0, 0, d.IsItem);                       // 金币走到就捡,物品要按键
        }
        Assert.Empty(s.Drops);
        Assert.Equal((1, 1, 100L), (s.GoldPicked, s.ItemsPicked, progress.Gold));
        Assert.Equal(1, progress.Bag.CountOf(101));
    }

    // ------------------------------------------------------------------ 副本与自动机

    private static (int X, int Y)[] Monsters(int cx, int cy)
    {
        return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
            : cx == 1 && cy == 0 ? new[] { (300, 200) }
            : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
            : None();
    }

    private static Action<RoomCombatSession> GiveItems(int code = 101, int grade = 1)
    {
        return s =>
        {
            MonsterDropTable table = Table();
            s.UseDrops(table, null, 5, PoolOf(table, StackItem(code, grade)));
        };
    }

    [Fact]
    public void TheBotPicksEveryItemUpAndTheRunTotalsSumTheRooms()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 1);
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress, setup: GiveItems());
        var bot = new DungeonBot(true);
        bot.RunUntil(d.Session, 600_000, 12_000);
        DungeonSession s = d.Session;
        Assert.True(s.BossCleared);
        Assert.Equal(5, s.Kills);
        Assert.True(s.ItemsDropped >= 4, "每次击杀掷一次、概率 100%,5 次击杀至少掉 4 件(掷到上界的万分之一除外)");
        Assert.Equal(s.ItemsDropped, s.ItemsPicked);
        Assert.Equal(0, s.ItemsBagFull);
        Assert.Equal(s.ItemsPicked, progress.Bag.CountOf(101));
        Assert.Equal(s.ItemsPicked, progress.ItemsGained);
        foreach (RoomCombatSession room in d.Rooms.Values)
        {
            Assert.Empty(room.Drops);
        }
        Assert.Equal(d.Rooms.Values.Sum(r => r.ItemsPicked), s.ItemsPicked);
    }

    [Fact]
    public void TheBotGivesUpOnItemsThatDoNotFitInTheBag()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 1);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            progress.Bag.Add(StackItem(2000 + i));                                  // 材料页占满
        }
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress, setup: GiveItems());
        var bot = new DungeonBot(true);
        bot.RunUntil(d.Session, 600_000, 12_000);
        DungeonSession s = d.Session;
        Assert.True(s.BossCleared);
        Assert.True(s.ItemsDropped >= 4);
        Assert.Equal(0, s.ItemsPicked);
        Assert.True(s.ItemsBagFull > 0);
        Assert.False(bot.WantsToCollect(s), "背包放不下的东西,自动机不再追");
        Assert.Equal(0, progress.Bag.CountOf(101));
    }

    [Fact]
    public void TheSettlementPanelListsTheItemsPickedUp()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 1);
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress, setup: GiveItems());
        var bot = new DungeonBot(true);
        bot.RunUntil(d.Session, 600_000, 12_000);
        DungeonSession s = d.Session;
        for (int i = 0; i < 400 && !s.SettlementActive; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.True(s.SettlementActive);
        Assert.Equal(5, s.Build(0).Count(x => x.AnimationPath == "reward"));        // 用时、击杀、经验、金币、物品
    }
}
