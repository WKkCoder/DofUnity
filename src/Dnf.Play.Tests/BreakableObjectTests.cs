using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.ItemDropSessionTests;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 可破坏物件(证据 E-126)在战斗会话里的行为:哪些物件能被打、打几下、打中时的受击动画与声音、命中停顿只定住主角、
/// 打够后消失或只触发、触发 [item] 动作时按副本的掉落项掉物品、不算连击与伤害数字。
/// 主角站在 (300, 100) 朝右、普攻第 0 段在 200 毫秒处的攻击框覆盖世界 x ∈ [310, 360];物件的受击框是 x ∈ [X − 10, X + 30],站在 X = 340 时相交。
/// 数据全是合成的(见 <see cref="AvatarFixture.CrateObjectId"/> 一带)。
/// </summary>
public class BreakableObjectTests
{
    private const int ObjY = 100;
    private const int InReach = 340;
    private const int OutOfReach = 520;
    private static readonly (int X, int Y)[] NoMonsters = new (int X, int Y)[0];

    /// <summary>把一批物件摆进房间:(物件编号, x, 特殊项或 null);y 都是 <see cref="ObjY"/>。</summary>
    private static Func<DnfAssets, IReadOnlyList<RoomObject>> Room(params (int Id, int X, SpecialPlacement Special)[] placements)
    {
        return assets =>
        {
            PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
            var list = new List<RoomObject>();
            foreach ((int id, int x, SpecialPlacement special) in placements)
            {
                Dnf.Render.PassiveObjectDef def = Dnf.Render.PassiveObjectDef.Read(assets, catalog, id);
                Assert.NotNull(def);
                RoomObject o = RoomObject.Create(assets, def, new PassivePlacement(id, x, ObjY, 0), special);
                Assert.NotNull(o);
                list.Add(o);
            }
            return list;
        };
    }

    private static SpecialPlacement Special(int id, int x, int ordinal, params SpecialAction[] actions)
    {
        return new SpecialPlacement(ordinal, id, x, ObjY, 0, actions, actions.Any(a => a.Kind == SpecialActionKind.Item) ? 0 : -1);
    }

    /// <summary>[item] 动作:掉落项下标、指定物品件数、随机物品件数。</summary>
    private static SpecialAction ItemAction(int entry, int specific, int random = 0) => new(SpecialActionKind.Item, entry, specific, random, 0, 0);

    /// <summary>掉落项 0 只有一种指定物品(编号 <see cref="Dropped"/>,权重大到每次都中);物件掉落表与通用池都空。</summary>
    private static ObjectDropSetup Setup()
    {
        var entries = new[] { new SpecialItemEntry(10, new[] { new WeightedItem(Dropped, 20000) }) };
        var drops = new MonsterDrops(null, null, null, null, null, null, id => StackItem(id));
        return new ObjectDropSetup(new DungeonObjectItems(entries, new List<WeightedItem>()), 20, 0, drops);
    }

    private const int Dropped = 201;

    /// <summary>按下攻击键、等整个挥击(第 0 段 400 毫秒)结束再多等一会儿。</summary>
    private static void Swing(RoomCombatSession s)
    {
        s.Update(10, 0, 0, true);
        for (int t = 10; t < 800; t += 10)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.False(s.Hero.IsAttacking);
    }

    /// <summary>按下攻击键、每 10 毫秒一步推到第一次打中物件为止。</summary>
    private static void RunToObjectHit(RoomCombatSession s, int hits = 1)
    {
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 100 && s.ObjectsHit < hits; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(hits, s.ObjectsHit);
    }

    // ------------------------------------------------------------------ 哪些物件能被打

    [Fact]
    public void OnlyBreakableFolderObjectsWithAHurtBoxBecomeBreakables()
    {
        using var arena = new Arena(NoMonsters, objects: Room(
            (AvatarFixture.CrateObjectId, 340, null),
            (AvatarFixture.BarrelObjectId, 400, null),
            (AvatarFixture.PillarObjectId, 440, null),
            (AvatarFixture.GrassObjectId, 480, null),
            (AvatarFixture.GhostObjectId, 520, null),         // 目录对,但基础动画里没有受击框
            (AvatarFixture.RockObjectId, 560, null)));        // 有 [int data] 与受击框,但不在 breakableobject 目录里
        RoomCombatSession s = arena.Session;
        Assert.Equal(new[] { AvatarFixture.CrateObjectId, AvatarFixture.BarrelObjectId, AvatarFixture.PillarObjectId, AvatarFixture.GrassObjectId },
            s.Breakables.Select(b => b.Object.Def.Id).ToArray());
        Assert.Null(BreakableObject.TryCreate(null));
        BreakableObject crate = s.Breakables[0];
        Assert.Single(crate.HurtBoxes);
        Assert.Equal(2, crate.Data.HitsNeeded);
        Assert.Equal((330, 370), (crate.HurtBoxes[0].MinX, crate.HurtBoxes[0].MaxX));
        Assert.Equal((94, 106), (crate.HurtBoxes[0].MinY, crate.HurtBoxes[0].MaxY));
    }

    [Fact]
    public void AnObjectOutOfTheStrikeBoxIsNotHit()
    {
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.CrateObjectId, OutOfReach, null)));
        Swing(arena.Session);
        Assert.Equal((0, 0, 0), (arena.Session.ObjectsHit, arena.Session.ObjectsDestroyed, arena.Session.Breakables[0].HitsTaken));
    }

    // ------------------------------------------------------------------ 打几下

    [Fact]
    public void ACrateReactsOnTheFirstHitAndIsDestroyedOnTheSecond()
    {
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.CrateObjectId, InReach, null)));
        RoomCombatSession s = arena.Session;
        BreakableObject crate = Assert.Single(s.Breakables);
        var cues = new List<SoundCue>();

        RunToObjectHit(s);
        Assert.Equal(1, crate.HitsTaken);
        Assert.False(crate.Destroyed);
        Assert.True(crate.Object.Reacting);                                      // 第一下:换播第一个受击动画
        Assert.True(crate.Object.Blocks(InReach, ObjY));
        s.DrainSounds(cues);
        Assert.Single(cues, c => c.Key == AvatarFixture.CrateHitSound && c.Kind == SoundCueKind.Effect);
        Assert.DoesNotContain(cues, c => c.Key == AvatarFixture.CrateCrashSound);

        // 受击动画 160 毫秒,播完回到基础动画
        for (int i = 0; i < 40; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.False(crate.Object.Reacting);

        Swing(s);
        Assert.Equal(2, crate.HitsTaken);
        Assert.True(crate.Destroyed);
        Assert.False(crate.Object.Blocks(InReach, ObjY));
        Assert.Equal((2, 1), (s.ObjectsHit, s.ObjectsDestroyed));
        cues.Clear();
        s.DrainSounds(cues);
        Assert.Contains(cues, c => c.Key == AvatarFixture.CrateHitSound);
        Assert.Contains(cues, c => c.Key == AvatarFixture.CrateCrashSound && c.Kind == SoundCueKind.Effect);

        // 打碎了就不再挨打
        Swing(s);
        Assert.Equal((2, 2, 1), (s.ObjectsHit, crate.HitsTaken, s.ObjectsDestroyed));
    }

    [Fact]
    public void ABarrelWithoutReactionMotionsIsDestroyedByTheFirstHitWithBothSounds()
    {
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, null)));
        RoomCombatSession s = arena.Session;
        RunToObjectHit(s);
        BreakableObject barrel = s.Breakables[0];
        Assert.True(barrel.Destroyed);
        Assert.False(barrel.Object.Reacting);
        Assert.Equal((1, 1), (s.ObjectsHit, s.ObjectsDestroyed));
        var cues = new List<SoundCue>();
        s.DrainSounds(cues);
        Assert.Equal(new[] { AvatarFixture.CrateHitSound, AvatarFixture.CrateCrashSound },
            cues.Select(c => c.Key).Where(k => k == AvatarFixture.CrateHitSound || k == AvatarFixture.CrateCrashSound).ToArray());
    }

    [Fact]
    public void APillarTriggersOnceThenOnlyReactsAndStaysStanding()
    {
        // 石柱:打 2 下算打够(触发 [item] 动作),不消失;再打只做受击反应,不再触发
        SpecialPlacement special = Special(AvatarFixture.PillarObjectId, InReach, 0, ItemAction(0, 1));
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.PillarObjectId, InReach, special)));
        RoomCombatSession s = arena.Session;
        s.UseObjectDrops(Setup());
        BreakableObject pillar = s.Breakables[0];

        Swing(s);
        Assert.Equal((1, false, 0), (pillar.HitsTaken, pillar.Triggered, s.ObjectItemsDropped));
        Swing(s);
        Assert.Equal((2, true, 1), (pillar.HitsTaken, pillar.Triggered, s.ObjectItemsDropped));
        Assert.False(pillar.Destroyed);
        Assert.True(pillar.Object.Blocks(InReach, ObjY));
        Swing(s);
        Assert.Equal((3, 1, 0), (pillar.HitsTaken, s.ObjectItemsDropped, s.ObjectsDestroyed));
        Assert.Equal(3, s.ObjectsHit);
        Assert.Equal(3, s.Breakables.Sum(b => b.HitsTaken));
    }

    [Fact]
    public void GrassWithTwoIntsIsTriggeredByOneHitAndNeverDisappears()
    {
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.GrassObjectId, InReach, null)));
        RoomCombatSession s = arena.Session;
        BreakableObject grass = s.Breakables[0];
        Assert.Equal(BreakableOutcome.Triggered, grass.Hit(arena.Assets));
        Assert.Equal(BreakableOutcome.Reacted, grass.Hit(arena.Assets));         // 触发过了,再打只反应
        Assert.False(grass.Destroyed);
        Assert.Equal(2, grass.HitsTaken);
        Assert.False(grass.Object.Reacting);                                     // 草丛没有受击动画
    }

    [Fact]
    public void TheReactionIndexFollowsTheHitCountAndStopsAtTheLastMotion()
    {
        // 石柱只有一个受击动画:第 1、2、3 下都播它(第 n 下用第 n 项,项数不够用最后一项)
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.PillarObjectId, InReach, null)));
        BreakableObject pillar = arena.Session.Breakables[0];
        Assert.Equal(BreakableOutcome.Reacted, pillar.Hit(arena.Assets));
        Assert.True(pillar.Object.Reacting);
        pillar.Object.Advance(1000);
        Assert.False(pillar.Object.Reacting);
        Assert.Equal(BreakableOutcome.Triggered, pillar.Hit(arena.Assets));
        Assert.True(pillar.Object.Reacting);
        pillar.Object.Advance(1000);
        Assert.Equal(BreakableOutcome.Reacted, pillar.Hit(arena.Assets));
        Assert.True(pillar.Object.Reacting);                                     // 项数不够用最后一项
    }

    // ------------------------------------------------------------------ 命中停顿与结算

    [Fact]
    public void AHitOnAnObjectFreezesTheHeroButHasNoComboDamageNumbersOrMonsterStats()
    {
        var strikes = new[] { new HeroStrikeInfo(0, 0, false, hitValue: 100), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
        using var arena = new Arena(NoMonsters, strikes, objects: Room((AvatarFixture.CrateObjectId, InReach, null)));
        RoomCombatSession s = arena.Session;
        RunToObjectHit(s);
        Assert.Equal(100.0, s.HeroPauseMs, 9);                                   // 与打怪物同一条规则(E-077)
        long motion = s.Hero.MotionMs;
        for (int i = 0; i < 10; i++)
        {
            s.Update(10, 0, 0, false);
            Assert.Equal(motion, s.Hero.MotionMs);
        }
        s.Update(10, 0, 0, false);
        Assert.Equal(motion + 10, s.Hero.MotionMs);

        // 物件不进怪物的统计:不算连击、不出伤害数字、不记伤害
        Assert.Equal((0, 0, 0, 0L), (s.Hits, s.Combo, s.Popups.Count, s.TotalDamage));
        Assert.Equal(1, s.ObjectsHit);
        Assert.Empty(s.Drops);
    }

    [Fact]
    public void AMonsterAndAnObjectInTheSameStrikeBoxAreBothHitOnce()
    {
        using var arena = new Arena(new[] { (340, 100) }, objects: Room((AvatarFixture.CrateObjectId, InReach, null)));
        RoomCombatSession s = arena.Session;
        Swing(s);
        Assert.Equal((1, 1, 1), (s.Hits, s.ObjectsHit, s.Breakables[0].HitsTaken));
        Assert.Equal(1, s.Combo);                                                // 只有怪物那一下算连击
    }

    // ------------------------------------------------------------------ [item] 动作

    [Fact]
    public void TheFinalHitReleasesTheItemActionAndTheItemsLandNearTheObject()
    {
        SpecialPlacement special = Special(AvatarFixture.CrateObjectId, InReach, 0, ItemAction(0, 2));
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.CrateObjectId, InReach, special)));
        RoomCombatSession s = arena.Session;
        s.UseObjectDrops(Setup(), seed: 7);

        Swing(s);                                                                // 第一下:只反应,不掉
        Assert.Equal((0, 0), (s.ObjectItemsDropped, s.ItemsDropped));
        Assert.Empty(s.Drops);
        Swing(s);                                                                // 第二下打碎:指定物品 2 件
        Assert.Equal((2, 2), (s.ObjectItemsDropped, s.ItemsDropped));
        Assert.Equal(2, s.Drops.Count);
        Assert.All(s.Drops, d =>
        {
            Assert.True(d.IsItem);
            Assert.Equal(Dropped, d.Item.Code);
        });
        Swing(s);
        Assert.Equal(2, s.ObjectItemsDropped);                                   // 打碎之后不会再掉
    }

    [Fact]
    public void WithoutObjectDropDataAnObjectDropsNothingAndPlainObjectsAreFine()
    {
        SpecialPlacement special = Special(AvatarFixture.BarrelObjectId, InReach, 0, ItemAction(0, 3));
        using var withoutSetup = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, special)));
        Swing(withoutSetup.Session);
        Assert.True(withoutSetup.Session.Breakables[0].Destroyed);
        Assert.Equal(0, withoutSetup.Session.ObjectItemsDropped);

        // 普通摆放(没有特殊项)的物件:能打碎,没有动作可触发
        using var plain = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, null)));
        plain.Session.UseObjectDrops(Setup());
        Swing(plain.Session);
        Assert.True(plain.Session.Breakables[0].Destroyed);
        Assert.Equal(0, plain.Session.ObjectItemsDropped);
    }

    [Fact]
    public void OnlyItemActionsAreExecutedMonsterTrapAndQuestActionsAreKnownButNotRun()
    {
        SpecialPlacement special = Special(AvatarFixture.BarrelObjectId, InReach, 0,
            new SpecialAction(SpecialActionKind.Monster, 5, 20, 50, 100, 0),
            new SpecialAction(SpecialActionKind.Trap, 1, 1, 1, 1, 1),
            ItemAction(0, 1),
            new SpecialAction(SpecialActionKind.Quest, 1, 2, 3, 4, 5));
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, special)));
        arena.Session.UseObjectDrops(Setup());
        Swing(arena.Session);
        Assert.Equal(1, arena.Session.ObjectItemsDropped);
        Assert.Empty(arena.Session.Monsters);
    }

    [Fact]
    public void ADropEntryOutOfRangeOrWithNegativeCountsDropsNothing()
    {
        // 下标 5 没有这一项;个数 -1 是零次(有符号字节)
        SpecialPlacement missing = Special(AvatarFixture.BarrelObjectId, InReach, 0, ItemAction(5, 2));
        using var a = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, missing)));
        a.Session.UseObjectDrops(Setup());
        Swing(a.Session);
        Assert.Equal(0, a.Session.ObjectItemsDropped);

        SpecialPlacement negative = Special(AvatarFixture.BarrelObjectId, InReach, 0, ItemAction(0, -1));
        using var b = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, negative)));
        b.Session.UseObjectDrops(Setup());
        Swing(b.Session);
        Assert.Equal(0, b.Session.ObjectItemsDropped);
    }

    // ------------------------------------------------------------------ 房间里的物件会被画出来、打碎后不再画

    [Fact]
    public void ADestroyedObjectDisappearsFromTheRoomScene()
    {
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.BarrelObjectId, InReach, null)));
        RoomCombatSession s = arena.Session;
        RoomObject barrel = s.Breakables[0].Object;
        var projection = new TownProjection(0, 200);
        Assert.NotNull(barrel.SpriteAt(arena.Assets, projection, 0));
        Swing(s);
        Assert.Null(barrel.SpriteAt(arena.Assets, projection, 0));
    }
}
