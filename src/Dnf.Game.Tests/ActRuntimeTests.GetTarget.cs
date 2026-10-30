using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [GET TARGET] n 键 [HIGH]/[LOW](M61,证据 E-155,客户端静态读码):加载函数 0x0134b27c 起的块把键读成模式号,
/// 判定函数 0x010d3630 里条件码 0x2c 的块按键整理工作集顺序再从尾巴截到 n 个。键后面的 [DISTANCE]、[HP] 同时是触发器的条件标签,
/// 以前的实现把它们当成条件(又不认 [LOW]/[HIGH] 这个比较符),让整条触发器读不懂。脚本内容全是合成的。
/// 观察办法:对工作集里每个对象放一个被动对象,摆放位置就是对象自己的坐标(偏移 0),所以宿主收到的 Spawns 按处理顺序列出了留下的对象。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ScriptLine[] CreateObject(int index) => new[] { L("[CREATE PASSIVEOBJECT]"), L("[INDEX]", index), L("[POS]", 0, 0, 0), L("[/CREATE PASSIVEOBJECT]") };

    /// <summary>[WHICH] [ALL ENEMY] 再接 [GET TARGET](或别的记录),然后对工作集里每个对象放一个被动对象。</summary>
    private static ActProgram GetTargetProgram(params ScriptLine[] getTarget)
    {
        var records = new List<ScriptLine> { L("[WHICH]"), L("[ALL ENEMY]") };
        records.AddRange(getTarget);
        records.Add(L("[DO BEHAVIOR]"));
        records.Add(L("[CHECKUP OBJECT]", 0));
        return P(Cat(Trigger(records.ToArray()), Behavior(CreateObject(7))));
    }

    private static (float x, float y) At(Entity e) => (e.X, e.Y);

    /// <summary>跑一次,返回被留下并处理的对象坐标(按处理顺序)。</summary>
    private static List<(float x, float y)> RunGetTarget(ActProgram p, Entity owner, params Entity[] enemies)
    {
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = enemies.Cast<IActEntity>().ToList();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return host.Spawns.Select(s => (s.x, s.y)).ToList();
    }

    private static ScriptLine[] GT(int n, string key, string direction = null) =>
        direction == null
            ? new[] { L("[GET TARGET]", n), L(key) }
            : new[] { L("[GET TARGET]", n), L(key), L(direction) };

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void GetTargetReadsCountKeyAndDirectionAndDoesNotLeakTheKeyAsACondition()
    {
        ActProgram p = GetTargetProgram(GT(2, "[DISTANCE]", "[LOW]"));
        ActTrigger t = Assert.Single(p.Triggers);

        Assert.True(t.Supported);
        Assert.Equal(new[] { ActRecordKind.Which, ActRecordKind.GetTarget, ActRecordKind.DoBehavior }, t.Records.Select(r => r.Kind).ToArray());
        ActRecord g = t.Records[1];
        Assert.Equal(2, g.Number);
        Assert.Equal(ActTargetKey.Distance, g.TargetKey);
        Assert.False(g.Descending);
    }

    [Fact]
    public void GetTargetHighSetsDescendingAndRandomHasNoDirection()
    {
        ActRecord high = GetTargetProgram(GT(1, "[HP]", "[HIGH]")).Triggers[0].Records[1];
        Assert.Equal(ActTargetKey.Hp, high.TargetKey);
        Assert.True(high.Descending);

        ActTrigger random = GetTargetProgram(GT(3, "[RANDOM]")).Triggers[0];
        Assert.True(random.Supported);
        Assert.Equal(ActTargetKey.Random, random.Records[1].TargetKey);
        Assert.Equal(3, random.Records[1].Number);

        ActRecord level = GetTargetProgram(GT(1, "[LEVEL]", "[HIGH]")).Triggers[0].Records[1];
        Assert.Equal(ActTargetKey.Level, level.TargetKey);

        ActRecord last = GetTargetProgram(GT(1, "[LAST ATTACKSUCCESS]", "[LOW]")).Triggers[0].Records[1];
        Assert.Equal(ActTargetKey.LastAttackSuccess, last.TargetKey);
    }

    [Fact]
    public void AMissingDirectionTagReadsAsLowAndTheNextRecordIsStillRead()
    {
        // 客户端对没写方向的写法报错并按"不是 [HIGH]"处理;键之后的记录照常读
        ActTrigger t = GetTargetProgram(new[] { L("[GET TARGET]", 1), L("[HP]") }).Triggers[0];

        Assert.True(t.Supported);
        Assert.False(t.Records[1].Descending);
        Assert.Equal(ActRecordKind.DoBehavior, t.Records[2].Kind);
    }

    [Fact]
    public void AGenuineDistanceConditionRightAfterAGetTargetIsStillACondition()
    {
        ActTrigger t = GetTargetProgram(Cat(GT(2, "[DISTANCE]", "[LOW]"), new[] { L("[DISTANCE]"), L("[<=]", 100) })).Triggers[0];

        Assert.Equal(
            new[] { ActRecordKind.Which, ActRecordKind.GetTarget, ActRecordKind.Distance, ActRecordKind.DoBehavior },
            t.Records.Select(r => r.Kind).ToArray());
        Assert.Equal(100, t.Records[2].Number);

        ActTrigger r = GetTargetProgram(Cat(GT(1, "[RANDOM]"), new[] { L("[DISTANCE]"), L("[<=]", 50) })).Triggers[0];
        Assert.Equal(new[] { ActRecordKind.Which, ActRecordKind.GetTarget, ActRecordKind.Distance, ActRecordKind.DoBehavior }, r.Records.Select(x => x.Kind).ToArray());
    }

    [Fact]
    public void GetTargetWithAnUnknownKeyIsUnsupportedAndNamesTheKey()
    {
        ActTrigger t = GetTargetProgram(GT(1, "[MP]", "[LOW]")).Triggers[0];

        Assert.False(t.Supported);
        Assert.Contains("[GET TARGET] [MP]", t.UnsupportedTags);
    }

    // ------------------------------------------------------------------ 运行:距离

    [Fact]
    public void GetTargetDistanceLowKeepsTheNearestNInOrder()
    {
        var far = new Entity { X = 300 };
        var near = new Entity { X = 100 };
        var mid = new Entity { X = 200 };

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(2, "[DISTANCE]", "[LOW]")), new Entity(), far, near, mid);

        Assert.Equal(new[] { At(near), At(mid) }, picked);
    }

    [Fact]
    public void GetTargetDistanceHighKeepsTheFarthestNFarthestFirst()
    {
        var far = new Entity { X = 300 };
        var near = new Entity { X = 100 };
        var mid = new Entity { X = 200 };

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(2, "[DISTANCE]", "[HIGH]")), new Entity(), near, far, mid);

        Assert.Equal(new[] { At(far), At(mid) }, picked);
    }

    [Fact]
    public void GetTargetDistanceUsesThePlaneDistanceAndIgnoresHeight()
    {
        var nearOnGround = new Entity { X = 30, Y = 40 };      // 距离 50
        var nearButHigh = new Entity { X = 60, Z = 500 };      // 平面距离 60,高度不算
        var tiny = new Entity { X = 5, Z = 900 };

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(2, "[DISTANCE]", "[LOW]")), new Entity(), nearButHigh, nearOnGround, tiny);

        Assert.Equal(new[] { At(tiny), At(nearOnGround) }, picked);
    }

    [Fact]
    public void GetTargetDistanceIsMeasuredFromTheOwner()
    {
        var owner = new Entity { X = 1000 };
        var west = new Entity { X = 0 };         // 距离 1000
        var east = new Entity { X = 1200 };      // 距离 200

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(1, "[DISTANCE]", "[LOW]")), owner, west, east);

        Assert.Equal(new[] { At(east) }, picked);
    }

    [Fact]
    public void GetTargetDistanceTiesKeepInsertionOrderForLowAndReverseItForHigh()
    {
        var a = new Entity { X = 100 };
        var b = new Entity { Y = 100 };
        var c = new Entity { Y = -100 };       // 三个对象到拥有者的距离都是 100

        // 多重映射:[LOW] 从头取(插入顺序),[HIGH] 从尾取(与插入顺序相反)
        Assert.Equal(new[] { At(a) }, RunGetTarget(GetTargetProgram(GT(1, "[DISTANCE]", "[LOW]")), new Entity(), a, b, c));
        Assert.Equal(new[] { At(c) }, RunGetTarget(GetTargetProgram(GT(1, "[DISTANCE]", "[HIGH]")), new Entity(), a, b, c));
        Assert.Equal(new[] { At(c), At(b) }, RunGetTarget(GetTargetProgram(GT(2, "[DISTANCE]", "[HIGH]")), new Entity(), a, b, c));
    }

    // ------------------------------------------------------------------ 运行:随机

    [Fact]
    public void GetTargetRandomShufflesWithTheHostRandomThenTruncates()
    {
        var a = new Entity { X = 1 };
        var b = new Entity { X = 2 };
        var c = new Entity { X = 3 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b, c };
        host.Randoms.Enqueue(0);       // i = 2:与第 0 个换 → c b a
        host.Randoms.Enqueue(0);       // i = 1:与第 0 个换 → b c a

        new ActRunner(GetTargetProgram(GT(2, "[RANDOM]")), new Entity(), host).Update(0, 0.016f);

        Assert.Equal(new[] { At(b), At(c) }, host.Spawns.Select(s => (s.x, s.y)).ToArray());
    }

    [Fact]
    public void GetTargetRandomDrawsOneNumberPerSwapWithBoundsFromTheSetSizeDownToTwo()
    {
        // 洗牌:i 从 n-1 到 1,每轮向宿主要一个 [0, i] 里的数,所以上界是 n、n-1、…、2,共 n-1 次(与客户端的随机洗牌次数一致)
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = Enumerable.Range(1, 5).Select(i => (IActEntity)new Entity { X = i }).ToList();

        new ActRunner(GetTargetProgram(GT(5, "[RANDOM]")), new Entity(), host).Update(0, 0.016f);

        Assert.Equal(new[] { 5, 4, 3, 2 }, host.RandomBounds.ToArray());
        Assert.Equal(5, host.Spawns.Count);
    }

    [Fact]
    public void GetTargetRandomDoesNotTakeADirectionTag()
    {
        // [RANDOM] 没有方向:后面跟着的 [HIGH] 不归它(主循环把它当游离标签跳过),所以记录的方向仍是"不是 [HIGH]"
        ActTrigger t = Assert.Single(GetTargetProgram(GT(1, "[RANDOM]", "[HIGH]")).Triggers);

        Assert.True(t.Supported);
        Assert.Equal(ActRecordKind.GetTarget, t.Records[1].Kind);
        Assert.False(t.Records[1].Descending);
    }

    [Fact]
    public void GetTargetRandomWithMoreRoomThanTargetsKeepsEveryone()
    {
        var a = new Entity { X = 1 };
        var b = new Entity { X = 2 };

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(5, "[RANDOM]")), new Entity(), a, b);

        Assert.Equal(2, picked.Count);
        Assert.Contains(At(a), picked);
        Assert.Contains(At(b), picked);
    }

    // ------------------------------------------------------------------ 运行:生命、等级

    [Fact]
    public void GetTargetHpLowKeepsTheLowestHealthFirst()
    {
        var healthy = new Entity { X = 1, Hp = 90, HpMax = 100 };
        var weak = new Entity { X = 2, Hp = 10, HpMax = 100 };
        var hurt = new Entity { X = 3, Hp = 50, HpMax = 100 };

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(2, "[HP]", "[LOW]")), new Entity(), healthy, weak, hurt);

        Assert.Equal(new[] { At(weak), At(hurt) }, picked);
    }

    [Fact]
    public void GetTargetHpHighDoesNotSortBecauseTheClientComparatorComparesAnObjectWithItself()
    {
        // 客户端 [HP] [HIGH] 的比较函数两次取同一个对象的生命(0x010d1160),恒不小于:原集合顺序不变,只截断
        var first = new Entity { X = 1, Hp = 10, HpMax = 100 };
        var second = new Entity { X = 2, Hp = 90, HpMax = 100 };
        var third = new Entity { X = 3, Hp = 50, HpMax = 100 };

        List<(float x, float y)> picked = RunGetTarget(GetTargetProgram(GT(2, "[HP]", "[HIGH]")), new Entity(), first, second, third);

        Assert.Equal(new[] { At(first), At(second) }, picked);
    }

    [Fact]
    public void GetTargetLevelSortsBothWays()
    {
        var l5 = new Entity { X = 1, Level = 5 };
        var l30 = new Entity { X = 2, Level = 30 };
        var l12 = new Entity { X = 3, Level = 12 };

        Assert.Equal(new[] { At(l30), At(l12) }, RunGetTarget(GetTargetProgram(GT(2, "[LEVEL]", "[HIGH]")), new Entity(), l5, l30, l12));
        Assert.Equal(new[] { At(l5), At(l12) }, RunGetTarget(GetTargetProgram(GT(2, "[LEVEL]", "[LOW]")), new Entity(), l5, l30, l12));
    }

    [Fact]
    public void GetTargetSortsAreStableForEqualKeys()
    {
        var a = new Entity { X = 1, Level = 10 };
        var b = new Entity { X = 2, Level = 10 };
        var c = new Entity { X = 3, Level = 10 };

        Assert.Equal(new[] { At(a), At(b), At(c) }, RunGetTarget(GetTargetProgram(GT(3, "[LEVEL]", "[LOW]")), new Entity(), a, b, c));
        Assert.Equal(new[] { At(a), At(b), At(c) }, RunGetTarget(GetTargetProgram(GT(3, "[LEVEL]", "[HIGH]")), new Entity(), a, b, c));
    }

    // ------------------------------------------------------------------ 运行:上次命中

    private static ActProgram LastHitProgram(string direction, int n) => P(Cat(
        Trigger(
            L("[ON ATTACKSUCCESS]"), L("[WHICH]"), L("[ALL ENEMY]"), L("[GET TARGET]", n), L("[LAST ATTACKSUCCESS]"), L(direction),
            L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
        Behavior(CreateObject(7))));

    private static List<(float x, float y)> RunLastHit(ActProgram p, Entity victim, params Entity[] enemies)
    {
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = enemies.Cast<IActEntity>().ToList();
        new ActRunner(p, new Entity(), host).OnAttackSuccess(victim);
        return host.Spawns.Select(s => (s.x, s.y)).ToList();
    }

    [Fact]
    public void GetTargetLastAttackSuccessHighMovesTheLastVictimFirstAndLowMovesItLast()
    {
        var a = new Entity { X = 1 };
        var b = new Entity { X = 2 };
        var c = new Entity { X = 3 };

        Assert.Equal(new[] { At(b) }, RunLastHit(LastHitProgram("[HIGH]", 1), b, a, b, c));
        Assert.Equal(new[] { At(b), At(a) }, RunLastHit(LastHitProgram("[HIGH]", 2), b, a, b, c));
        Assert.Equal(new[] { At(a) }, RunLastHit(LastHitProgram("[LOW]", 1), b, a, b, c));
        Assert.Equal(new[] { At(a), At(c) }, RunLastHit(LastHitProgram("[LOW]", 2), b, a, b, c));
    }

    [Fact]
    public void GetTargetLastAttackSuccessWithoutAMatchingVictimLeavesTheOrder()
    {
        var a = new Entity { X = 1 };
        var b = new Entity { X = 2 };
        var c = new Entity { X = 3 };
        var outsider = new Entity { X = 99 };

        Assert.Equal(new[] { At(a), At(b) }, RunLastHit(LastHitProgram("[LOW]", 2), outsider, a, b, c));
    }

    // ------------------------------------------------------------------ 运行:边界

    [Fact]
    public void GetTargetZeroEmptiesTheSetSoNothingFires()
    {
        var a = new Entity { X = 10 };

        Assert.Empty(RunGetTarget(GetTargetProgram(GT(0, "[DISTANCE]", "[LOW]")), new Entity(), a));
    }

    [Fact]
    public void GetTargetOnAnEmptySetFailsTheWholeTrigger()
    {
        // 客户端 0x010d7ccb:块开头测工作集个数为零就跳到"返回假"的出口(0x010d3ed3)——后面即便有 [CHECKED NO] [<] 1 也轮不到
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ALL ENEMY]"), L("[GET TARGET]", 1), L("[RANDOM]"), L("[CHECKED NO]"), L("[<]", 1),
                L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(4))));
        var owner = new Entity();

        new ActRunner(p, owner, new Host()).Update(0, 0.016f);

        Assert.Empty(owner.Log);

        // 对照:同样的触发器不带 [GET TARGET],空集合下 [CHECKED NO] [<] 1 照样通过并触发
        ActProgram control = P(Cat(
            Trigger(L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKED NO]"), L("[<]", 1), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(4))));
        var owner2 = new Entity();

        new ActRunner(control, owner2, new Host()).Update(0, 0.016f);

        Assert.Equal(new[] { "speed:4,-,-" }, owner2.Log.ToArray());
    }

    [Fact]
    public void GetTargetANegativeCountDoesNotTruncate()
    {
        var a = new Entity { X = 1 };
        var b = new Entity { X = 2 };

        Assert.Equal(new[] { At(a), At(b) }, RunGetTarget(GetTargetProgram(GT(-1, "[DISTANCE]", "[LOW]")), new Entity(), a, b));
    }

    [Fact]
    public void GetTargetTriggerThatUsedToBeUnreadableIsNowSupportedAndFires()
    {
        // E-155 之前:键后的 [DISTANCE] 被当成条件,[LOW] 又不是比较符,整条触发器读不懂、永远不触发
        ActProgram p = GetTargetProgram(GT(1, "[DISTANCE]", "[LOW]"));
        var counting = new CountingHost();
        _ = new ActRunner(p, new Entity(), counting);

        Assert.Empty(counting.Triggers);
        Assert.True(p.Triggers[0].Supported);

        var near = new Entity { X = 10 };
        Assert.Equal(new[] { At(near) }, RunGetTarget(p, new Entity(), near, new Entity { X = 99 }));
    }

    [Fact]
    public void GetTargetFollowedByADistanceFilterFiltersTheTruncatedSet()
    {
        // 先取最近的 2 个,再留下距离 <= 150 的:顺序就是先排序截断、后过滤
        var d100 = new Entity { X = 100 };
        var d120 = new Entity { X = 120 };
        var d200 = new Entity { X = 200 };
        ActProgram p = GetTargetProgram(Cat(GT(2, "[DISTANCE]", "[LOW]"), new[] { L("[DISTANCE]"), L("[<=]", 110) }));

        Assert.Equal(new[] { At(d100) }, RunGetTarget(p, new Entity(), d200, d120, d100));
    }
}
