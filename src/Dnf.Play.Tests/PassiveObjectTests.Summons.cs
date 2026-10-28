using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物 .act 里的 [SUMMON MONSTER] 与 [SET HP] 进到房间里(M41,证据 E-134):脚本按帧召唤一只怪物,位置、朝向、等级按客户端执行块的规则算,
/// 召唤者记在新怪物身上;[SUMMON TIME] 到点就消失,[SET HP] 0 对 [SUMMON OBJECT] 清场;请求被拒绝的几种情形(没工厂、没有活着的怪物、超过上限、找不到落点)。
/// 召唤出来的是合成的迷你怪物(<see cref="AvatarFixture.BlobMobPath"/>),由测试里的工厂造;出招 .act 见 <see cref="AvatarFixture.ActMobPath"/>:summon、summontimed、summonkill。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>测试用的召唤工厂:每次召唤造一只迷你怪物,记下收到的 (编号, 等级)。</summary>
    private static RoomCombatSession.SummonFactory BlobFactory(DnfAssets assets, List<(int code, int level)> calls = null)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        return (code, level, index) =>
        {
            calls?.Add((code, level));
            var spawn = new RoomMonsterSpawn(code, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]");
            return new MonsterActor(index, spawn, model, level, 100, 222.5, 1500, 0);
        };
    }

    [Fact]
    public void AnAttackScriptSummonsAMonsterAtTheScriptedFrameWithItsMasterLevelAndPlace()
    {
        using var arena = new ActArena("summon", useVitals: false);
        RoomCombatSession s = arena.Session;
        var calls = new List<(int code, int level)>();
        s.UseSummons(BlobFactory(arena.Assets, calls));
        MonsterActor owner = arena.Monster;
        Assert.Equal((0, 0), (s.SummonsSpawned, s.SummonedAlive));

        int steps = arena.Until(() => s.SummonsSpawned > 0);
        Assert.InRange(steps, 8, 13);                              // 帧 1 从 80 毫秒起
        Assert.Equal(2, s.Monsters.Count);
        MonsterActor child = s.Monsters[1];
        Assert.Equal(new[] { (AvatarFixture.SummonedCode, 3) }, calls.ToArray());   // [LEVEL] 2 加召唤者的 1 级
        Assert.Equal((1, 3), (child.Index, child.Level));
        Assert.True(child.IsSummoned);
        Assert.Same(owner.ActSelf, child.ActMaster);
        Assert.False(owner.IsSummoned);
        Assert.Null(owner.ActMaster);
        Assert.True(child.FacingLeft);                              // 朝向取召唤者的(召唤者朝左)
        Assert.InRange(child.X, owner.X - 31.0, owner.X - 29.0);    // 偏移 30,朝左取反
        Assert.InRange(child.Y, owner.Y - 0.5, owner.Y + 0.5);
        Assert.Equal(0.0, child.Z);
        Assert.Equal(0.0, child.LifeLeftMs);                        // 没写 [SUMMON TIME]:不限
        Assert.Equal((2, 1, 0), (s.AliveCount, s.SummonedAlive, s.SummonsRefused));
        Assert.Equal((0, 0), (s.Kills, s.SummonedKills));
        Assert.Same(arena.Session.HeroEntity, child.ActTarget);     // 接上程序库之后装的目标
    }

    [Fact]
    public void ASummonedMonsterWithASummonTimeDisappearsWhenItRunsOut()
    {
        using var arena = new ActArena("summontimed", useVitals: false);
        RoomCombatSession s = arena.Session;
        s.UseSummons(BlobFactory(arena.Assets));
        Assert.True(arena.Until(() => s.SummonsSpawned > 0) > 0);
        MonsterActor child = s.Monsters[1];
        Assert.Equal((double)AvatarFixture.SummonLifeMs, child.LifeLeftMs);   // 放出来的那一步没有走
        Assert.True(child.IsAlive);

        int steps = arena.Until(() => !child.IsAlive);
        Assert.InRange(steps, 29, 31);                              // 300 毫秒
        Assert.Equal(0.0, child.LifeLeftMs);
        Assert.True(child.Phase == MonsterPhase.Dying || child.Phase == MonsterPhase.Gone);
        Assert.Equal((0, 0, 0), (s.Kills, s.SummonedKills, s.SummonedAlive));   // 时间到不算主角的击杀
        Assert.True(arena.Monster.IsAlive);
        arena.Steps(200);
        Assert.Equal(MonsterPhase.Gone, child.Phase);               // 白闪淡出之后消失
    }

    [Fact]
    public void TheScriptCanKillItsSummonsThroughTheSummonObjectSelector()
    {
        using var arena = new ActArena("summonkill", useVitals: false);
        RoomCombatSession s = arena.Session;
        s.UseSummons(BlobFactory(arena.Assets));
        Assert.True(arena.Until(() => s.SummonsSpawned > 0) > 0);
        MonsterActor child = s.Monsters[1];
        Assert.True(child.IsAlive);

        int steps = arena.Until(() => !child.IsAlive);               // 帧 3(240 毫秒):对 [SUMMON OBJECT] 执行 [SET HP] 0
        Assert.InRange(steps, 10, 20);
        Assert.True(arena.Monster.IsAlive);                         // 召唤者自己不在 [SUMMON OBJECT] 里
        Assert.Equal(MonsterPhase.Dying, child.Phase);
        Assert.Equal((0, 0, 0, 1), (s.Kills, s.SummonedKills, s.SummonedAlive, s.SummonsSpawned));
        Assert.Equal(1, s.AliveCount);
    }

    [Fact]
    public void SummonRequestsAreRefusedWithoutAFactoryWithoutLivingMonstersAndBeyondTheCap()
    {
        using var arena = new ActArena("plain", useVitals: false);
        RoomCombatSession s = arena.Session;
        IActEntity owner = arena.Monster.ActSelf;

        s.ActHost.SummonMonster(owner, 7, 1, 340, 100, 0, true, 0);            // 没装工厂
        arena.Steps(1);
        Assert.Equal((0, 1, 1), (s.SummonsSpawned, s.SummonsRefused, s.Monsters.Count));

        s.UseSummons((code, level, index) => null);                            // 工厂造不出来(缺数据)
        s.ActHost.SummonMonster(owner, 7, 1, 340, 100, 0, true, 0);
        arena.Steps(1);
        Assert.Equal((0, 2, 1), (s.SummonsSpawned, s.SummonsRefused, s.Monsters.Count));

        s.UseSummons(BlobFactory(arena.Assets));
        for (int i = 0; i < RoomCombatSession.MaxSummoned + 5; i++)
        {
            s.ActHost.SummonMonster(owner, 7, 1, 340 + i, 100, 0, true, 0);
        }
        arena.Steps(1);
        Assert.Equal(RoomCombatSession.MaxSummoned, s.SummonsSpawned);          // 同时活着的召唤物有上限,多的排队就被拒
        Assert.Equal(RoomCombatSession.MaxSummoned, s.SummonedAlive);
        Assert.Equal(2 + 5, s.SummonsRefused);

        s.ActHost.SummonMonster(owner, 7, 1, 340, 100, 0, true, 0);            // 满员:再要也拒绝
        arena.Steps(1);
        Assert.Equal((RoomCombatSession.MaxSummoned, 2 + 6), (s.SummonsSpawned, s.SummonsRefused));
        s.Monsters[1].ActSelf.SetHp(0);                                        // 死一只就又有名额
        s.ActHost.SummonMonster(owner, 7, 1, 340, 100, 0, true, 0);
        arena.Steps(1);
        Assert.Equal((RoomCombatSession.MaxSummoned + 1, 2 + 6), (s.SummonsSpawned, s.SummonsRefused));
    }

    [Fact]
    public void NoSummonsAreMadeOnceNothingInTheRoomIsAlive()
    {
        using var arena = new ActArena("plain", useVitals: false);
        RoomCombatSession s = arena.Session;
        s.UseSummons(BlobFactory(arena.Assets));
        IActEntity owner = arena.Monster.ActSelf;
        owner.SetHp(0);                                                    // 房间里最后一只也死了:战斗结束,残留的对象不再召唤
        Assert.Equal(0, s.AliveCount);
        s.ActHost.SummonMonster(owner, 7, 1, 340, 100, 0, true, 0);
        arena.Steps(1);
        Assert.Equal((0, 1, 1), (s.SummonsSpawned, s.SummonsRefused, s.Monsters.Count));
    }

    [Fact]
    public void AnUnstandableSpotFallsBackToTheNearestStandingPointOfTheMasterAndIsRefusedWhenThereIsNone()
    {
        // 可走区只有主角脚下一小块(x 在 295 到 305):召唤点 (900, 900) 200 像素内没有能站的点,改试召唤者脚下 (340, 100),
        // 它 200 像素内有主角那一块,于是落在那一块的边上
        using (var near = new ActArena("plain", useVitals: false, frozenHero: true))
        {
            RoomCombatSession s = near.Session;
            s.UseSummons(BlobFactory(near.Assets));
            s.ActHost.SummonMonster(near.Monster.ActSelf, 7, 1, 900, 900, 0, true, 0);
            near.Steps(1);
            Assert.Equal((1, 0, 2), (s.SummonsSpawned, s.SummonsRefused, s.Monsters.Count));
            MonsterActor child = s.Monsters[1];
            Assert.InRange(child.X, 294.0, 306.0);
            Assert.InRange(child.Y, 94.0, 106.0);
        }

        // 召唤者站得离那一块太远(超过 200 像素):两处都找不到,请求被拒绝
        using (var far = new ActArena("plain", monsterX: 700, useVitals: false, frozenHero: true))
        {
            RoomCombatSession s = far.Session;
            s.UseSummons(BlobFactory(far.Assets));
            s.ActHost.SummonMonster(far.Monster.ActSelf, 7, 1, 900, 900, 0, true, 0);
            far.Steps(1);
            Assert.Equal((0, 1, 1), (s.SummonsSpawned, s.SummonsRefused, s.Monsters.Count));
        }
    }

    [Fact]
    public void TheSummonObjectSelectorListsOnlyTheMastersOwnLivingMonstersAndObjects()
    {
        using var arena = new ActArena("plain", useVitals: false);
        RoomCombatSession s = arena.Session;
        s.UseSummons(BlobFactory(arena.Assets));
        IActEntity owner = arena.Monster.ActSelf;
        IActEntity hero = s.HeroEntity;

        s.ActHost.SummonMonster(owner, 7, 1, 340, 100, 0, true, 0);
        s.ActHost.SummonMonster(owner, 7, 1, 360, 100, 0, true, 0);
        s.ActHost.SummonMonster(hero, 7, 1, 380, 100, 0, true, 0);             // 主角召唤的
        s.ActHost.SpawnPassive(owner, AvatarFixture.ActForeverId, 1, 330, 100, 0, true);
        arena.Steps(1);
        Assert.Equal(4, s.Monsters.Count);

        var mine = new List<IActEntity>();
        s.ActHost.Resolve(owner, ActSelector.SummonObject, 0, mine);
        Assert.Equal(3, mine.Count);                                           // 两只怪物与一个被动对象
        Assert.Contains(s.Monsters[1].ActSelf, mine);
        Assert.Contains(s.Monsters[2].ActSelf, mine);
        Assert.DoesNotContain(s.Monsters[3].ActSelf, mine);
        Assert.Contains(mine, e => e.Kind == ActEntityKind.Passive);

        var heros = new List<IActEntity>();
        s.ActHost.Resolve(hero, ActSelector.SummonObject, 0, heros);
        Assert.Equal(new[] { s.Monsters[3].ActSelf }, heros.ToArray());

        s.Monsters[1].ActSelf.SetHp(0);                                        // 死了的不再算
        mine.Clear();
        s.ActHost.Resolve(owner, ActSelector.SummonObject, 0, mine);
        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(s.Monsters[1].ActSelf, mine);
        Assert.Same(owner, s.Monsters[2].ActMaster);
        Assert.Same(hero, s.Monsters[3].ActMaster);
    }

    [Fact]
    public void AttachingTheLibraryPreloadsEachSummonedMonsterCodeOnce()
    {
        using var arena = new ActArena("summon", useVitals: false, attach: false);
        RoomCombatSession s = arena.Session;
        var seen = new List<int>();
        s.UseSummons(BlobFactory(arena.Assets), seen.Add);
        Assert.Empty(seen);                                                    // 还没接程序库:不知道脚本里写了哪些怪物
        s.AttachActs(new ActLibrary(arena.Assets, DataOf(arena.Assets)));
        Assert.Equal(new[] { AvatarFixture.SummonedCode }, seen.ToArray());    // 怪物出招 .act 的召唤行为里写到的编号
        Assert.True(arena.Until(() => s.SummonsSpawned > 0) > 0);
        Assert.Equal(new[] { AvatarFixture.SummonedCode }, seen.ToArray());    // 召唤物自己的程序里没有新编号:不重复预读
    }

    [Fact]
    public void PlaceSummonedKeepsAStandingPointMovesToTheNearestOneOrFails()
    {
        using var arena = new ActArena("plain", useVitals: false);
        MonsterActor m = BlobFactory(arena.Assets)(7, 1, 5);
        Assert.True(m.PlaceSummoned(100, 100, 30, true, FreeArea, null, 0));   // 没有限制:原样,高度保留
        Assert.Equal((100.0, 100.0, 30.0), (m.X, m.Y, m.Z));
        Assert.True(m.IsSummoned);
        Assert.Null(m.ActMaster);

        var tight = new RectUnionArea(new List<WorldRect> { new WorldRect(200, 90, 60, 20) });
        MonsterActor n = BlobFactory(arena.Assets)(7, 1, 6);
        Assert.True(n.PlaceSummoned(100, 100, 0, false, tight, arena.Monster.ActSelf, 450));
        Assert.InRange(n.X, 199.0, 215.0);                                     // 被拉到可走区的左缘附近
        Assert.False(n.FacingLeft);
        Assert.Equal(450.0, n.LifeLeftMs);
        Assert.Same(arena.Monster.ActSelf, n.ActMaster);

        MonsterActor far = BlobFactory(arena.Assets)(7, 1, 7);
        Assert.False(far.PlaceSummoned(900, 900, 0, true, tight, null, 0));    // 200 像素内没有:失败,不动
        Assert.False(far.IsSummoned);
        Assert.Equal(0.0, far.LifeLeftMs);
    }
}
