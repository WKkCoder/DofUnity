using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 条件批一(M68,证据 E-166;客户端条件求值函数 0x010d0000 一带与事件运行器 0x010a8c30 的静态读码):
/// [RANDOM CHECK]、[BACKSIDE]、[FACE TO FACE]、[WITHIN SIGHT]、[OUT OF SIGHT]、[IS TEAM]、[ZSPEED]、[IS END MAP]、[IS END MAP VERTICALLY]、
/// [IS TARGETACTIVESTATUS]、[IS FIRE/WATER/DARK/LIGHT/NONE DAMAGE]、[IS FRONTPOS]、[IS BACKPOS];这些以前全在"读不懂"里,带它们的触发器永远不触发。
/// 脚本内容全是合成的,坐标与速度都是测试用的数。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>覆盖接口默认实现的实体:自己的队号、速度、异常状态集合。</summary>
    private sealed class Rich : Entity, IActEntity
    {
        public int TeamId { get; set; }
        public float Vx { get; set; }
        public float Vy { get; set; }
        public float Vz { get; set; }
        public HashSet<int> StatusSet { get; } = new();

        int IActEntity.Team => TeamId;

        float IActEntity.SpeedX => Vx;

        float IActEntity.SpeedY => Vy;

        float IActEntity.SpeedZ => Vz;

        bool IActEntity.HasActiveStatus(int status) => StatusSet.Contains(status);
    }

    /// <summary>[WHICH] [ALL ENEMY] 之后接一串过滤条件,[DO BEHAVIOR] [CHECKUP OBJECT] 对留下的对象设 X 速度 1。</summary>
    private static ActProgram Filter(params ScriptLine[] conditions) => P(Cat(
        Trigger(Cat(
            new[] { L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKUP]") },
            conditions,
            new[] { L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0) })),
        Behavior(Speed(1))));

    /// <summary>跑一遍,返回每个对象有没有被行为选中(留在工作集里)。</summary>
    private static bool[] Kept(ActProgram p, Entity owner, Host host, params Entity[] items)
    {
        host.Sets[ActSelector.AllEnemy] = items.Cast<IActEntity>().ToList();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return items.Select(e => e.Log.Count > 0).ToArray();
    }

    private static bool[] Kept(ActProgram p, Entity owner, params Entity[] items) => Kept(p, owner, new Host(), items);

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void TheNewConditionTagsCompileToTheirOwnKindsWithTheirFields()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[BACKSIDE]"), L("[FACE TO FACE]"), L("[WITHIN SIGHT]"), L("[OUT OF SIGHT]"), L("[IS END MAP]"),
                L("[IS END MAP VERTICALLY]"), L("[ZSPEED]"), L("[<]", 5), L("[IS TARGETACTIVESTATUS]", 9), L("[IS FRONTPOS]"), L("[IS BACKPOS]"),
                L("[IS FIRE DAMAGE]"), L("[IS WATER DAMAGE]"), L("[IS DARK DAMAGE]"), L("[IS LIGHT DAMAGE]"), L("[IS NONE DAMAGE]"),
                L("[RANDOM CHECK]", 30, 200), L("[RANDOM CHECK]", 7)),
            Behavior(Speed(1))));
        ActTrigger t = p.Triggers[0];
        Assert.True(t.Supported);
        Assert.Equal(new[]
        {
            ActRecordKind.Frame, ActRecordKind.BackSide, ActRecordKind.FaceToFace, ActRecordKind.WithinSight, ActRecordKind.OutOfSight,
            ActRecordKind.IsEndMap, ActRecordKind.IsEndMapVertically, ActRecordKind.ZSpeed, ActRecordKind.IsTargetActiveStatus,
            ActRecordKind.FrontPos, ActRecordKind.FrontPos, ActRecordKind.ElementDamage, ActRecordKind.ElementDamage, ActRecordKind.ElementDamage,
            ActRecordKind.ElementDamage, ActRecordKind.ElementDamage, ActRecordKind.RandomCheck, ActRecordKind.RandomCheck,
        }, t.Records.Select(r => r.Kind).ToArray());
        ActRecord speed = t.Records[7];
        Assert.Equal((ActCompare.Less, 5), (speed.Sign, speed.Number));
        Assert.Equal(9, t.Records[8].Number);
        Assert.False(t.Records[9].Flag);          // [IS FRONTPOS]
        Assert.True(t.Records[10].Flag);          // [IS BACKPOS]
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, t.Records.Skip(11).Take(5).Select(r => r.Number).ToArray());
        Assert.Equal((30, 200), (t.Records[16].Number, t.Records[16].Denominator));
        Assert.Equal((7, 0), (t.Records[17].Number, t.Records[17].Denominator));      // 分母没写记 0,运行时按 100
    }

    [Fact]
    public void IsTeamEatsItsSubTagsUpToTheCloseTagAndTheNextRecordStillReads()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IS TEAM]"), L("[MY TEAM]"), L("[ENEMY TEAM]"), L("[CHARCTER TEAM]"), L("[MONSTER TEAM]"), L("[/IS TEAM]"),
                L("[IS LEFT]")),
            Behavior(Speed(1))));
        ActTrigger t = p.Triggers[0];
        Assert.True(t.Supported);
        Assert.Equal(new[] { ActRecordKind.Frame, ActRecordKind.IsTeam, ActRecordKind.IsLeft }, t.Records.Select(r => r.Kind).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, t.Records[1].Ints);
    }

    [Fact]
    public void AnIsTargetActiveStatusWithoutItsNumberIsUnreadable()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IS TARGETACTIVESTATUS]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(1))));
        Assert.False(p.Triggers[0].Supported);
        Assert.Contains("[IS TARGETACTIVESTATUS]", p.Triggers[0].UnsupportedTags);
    }

    // ------------------------------------------------------------------ [RANDOM CHECK]

    [Fact]
    public void RandomCheckRollsOncePerObjectAndKeepsRollsNotAboveTheNumeratorWithDenominator100ByDefault()
    {
        ActProgram p = Filter(L("[RANDOM CHECK]", 30));
        var host = new Host();
        foreach (int roll in new[] { 30, 31, 0, 99 })
        {
            host.Randoms.Enqueue(roll);
        }
        Entity[] items = Enumerable.Range(0, 4).Select(_ => new Entity { Kind = ActEntityKind.Character }).ToArray();

        Assert.Equal(new[] { true, false, true, false }, Kept(p, new Entity(), host, items));      // 不大于分子就留下:30 留、31 不留
        Assert.Equal(new[] { 100, 100, 100, 100 }, host.RandomBounds.ToArray());                  // 每个对象各掷一次,分母没写按 100
    }

    [Fact]
    public void RandomCheckUsesTheGivenDenominatorAndTreatsZeroAs100()
    {
        var host = new Host();
        host.Randoms.Enqueue(3);
        host.Randoms.Enqueue(4);
        var a = new Entity { Kind = ActEntityKind.Character };
        var b = new Entity { Kind = ActEntityKind.Character };
        Assert.Equal(new[] { true, false }, Kept(Filter(L("[RANDOM CHECK]", 3, 7)), new Entity(), host, a, b));
        Assert.Equal(new[] { 7, 7 }, host.RandomBounds.ToArray());

        var zero = new Host();
        zero.Randoms.Enqueue(5);
        var c = new Entity { Kind = ActEntityKind.Character };
        Assert.Equal(new[] { true }, Kept(Filter(L("[RANDOM CHECK]", 5, 0)), new Entity(), zero, c));
        Assert.Equal(new[] { 100 }, zero.RandomBounds.ToArray());
    }

    [Fact]
    public void RandomCheckWithAnEmptySetRollsNothing()
    {
        var host = new Host();
        Assert.Empty(Kept(Filter(L("[RANDOM CHECK]", 50)), new Entity(), host));
        Assert.Empty(host.RandomBounds);
    }

    // ------------------------------------------------------------------ 面向与视线

    /// <summary>拥有者在 X=100;被测对象依次是 (50 朝左, 50 朝右, 100 朝左, 100 朝右, 150 朝左, 150 朝右),外加两个已死的(50 处,一个朝左一个朝右)。</summary>
    private static (Entity[] items, Entity[] dead) FacingItems()
    {
        var items = new[]
        {
            new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = true },
            new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = false },
            new Entity { Kind = ActEntityKind.Character, X = 100, FacingLeft = true },
            new Entity { Kind = ActEntityKind.Character, X = 100, FacingLeft = false },
            new Entity { Kind = ActEntityKind.Character, X = 150, FacingLeft = true },
            new Entity { Kind = ActEntityKind.Character, X = 150, FacingLeft = false },
        };
        var dead = new[]
        {
            new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = true, IsAlive = false },
            new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = false, IsAlive = false },
        };
        return (items, dead);
    }

    /// <summary>
    /// 宿主连死了的也给,条件之后用 [CHECKED NO] [=] 0 看工作集是不是空了:空了才触发,行为落在拥有者自己身上。
    /// 只看"哪个对象被行为选中"看不出存活检查——行为本来就不动已死的对象,所以要数工作集。
    /// </summary>
    private static bool EmptyAfter(string tag, bool ownerFacingLeft, params Entity[] dead)
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L(tag), L("[CHECKED NO]"), L("[=]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(1))));
        var owner = new Entity { X = 100, FacingLeft = ownerFacingLeft };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllEnemy] = dead.Cast<IActEntity>().ToList();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return owner.Log.Count > 0;
    }

    private static bool[] FacingKept(string tag, bool ownerFacingLeft)
    {
        (Entity[] items, Entity[] dead) = FacingItems();
        Assert.True(EmptyAfter(tag, ownerFacingLeft, dead), tag + " 不留已死的(工作集里一个也不剩)");
        return Kept(Filter(L(tag)), new Entity { X = 100, FacingLeft = ownerFacingLeft }, items);
    }

    [Fact]
    public void BackSideKeepsTheTargetsThatTurnTheirBackToTheOwner()
    {
        // 拥有者在目标右边或同位置:目标要朝左;在左边:目标要朝右(与朝向无关的只看目标朝向)
        bool[] expected = { true, false, true, false, false, true };
        Assert.Equal(expected, FacingKept("[BACKSIDE]", false));
        Assert.Equal(expected, FacingKept("[BACKSIDE]", true));
        Assert.Equal(new[] { false, true, false, true, true, false }, FacingKept("[FRONTSIDE]", false));      // 正面恰是背面的反面
        Assert.Equal(new[] { false, true, false, true, true, false }, FacingKept("[FRONTSIDE]", true));
    }

    [Fact]
    public void FaceToFaceNeedsBothSidesToFaceEachOther()
    {
        Assert.Equal(new[] { false, true, false, true, false, false }, FacingKept("[FACE TO FACE]", true));      // 拥有者朝左:要目标在左边或同位置且朝右
        Assert.Equal(new[] { false, false, false, false, true, false }, FacingKept("[FACE TO FACE]", false));    // 拥有者朝右:要目标在右边且朝左
    }

    [Fact]
    public void WithinSightAndOutOfSightLookOnlyAtTheOwnersFacing()
    {
        Assert.Equal(new[] { true, true, true, true, false, false }, FacingKept("[WITHIN SIGHT]", true));
        Assert.Equal(new[] { false, false, false, false, true, true }, FacingKept("[WITHIN SIGHT]", false));
        Assert.Equal(new[] { false, false, false, false, true, true }, FacingKept("[OUT OF SIGHT]", true));
        Assert.Equal(new[] { true, true, true, true, false, false }, FacingKept("[OUT OF SIGHT]", false));
    }

    [Fact]
    public void FrontSideAndTheFacingFamilyCompareIntegerXCoordinates()
    {
        // 拥有者 X=100.4、目标 X=100.9:取整都是 100,算"拥有者在目标右边或同位置"——目标要朝右才是正面(浮点比会反过来)
        var facingRight = new Entity { Kind = ActEntityKind.Character, X = 100.9f, FacingLeft = false };
        var facingLeft = new Entity { Kind = ActEntityKind.Character, X = 100.9f, FacingLeft = true };
        Assert.Equal(new[] { true, false }, Kept(Filter(L("[FRONTSIDE]")), new Entity { X = 100.4f }, facingRight, facingLeft));
        facingRight.Log.Clear();
        facingLeft.Log.Clear();
        Assert.Equal(new[] { false, true }, Kept(Filter(L("[BACKSIDE]")), new Entity { X = 100.4f }, facingRight, facingLeft));
    }

    // ------------------------------------------------------------------ [IS TEAM]

    [Fact]
    public void IsTeamDefaultsByKindAndPassiveObjectsTakeTheirCreatorsTeam()
    {
        var owner = new Entity { Kind = ActEntityKind.Monster };
        var hero = new Entity { Kind = ActEntityKind.Character };
        var monster = new Entity { Kind = ActEntityKind.Monster };
        var ownShot = new Entity { Kind = ActEntityKind.Passive, Master = owner };
        var heroShot = new Entity { Kind = ActEntityKind.Passive, Master = hero };
        var orphan = new Entity { Kind = ActEntityKind.Passive };
        Entity[] items = { hero, monster, ownShot, heroShot, orphan };

        Assert.Equal(new[] { false, true, true, false, true }, Kept(Filter(L("[IS TEAM]"), L("[MY TEAM]"), L("[/IS TEAM]")), owner, items));
        Clear(items);
        Assert.Equal(new[] { true, false, false, true, false }, Kept(Filter(L("[IS TEAM]"), L("[ENEMY TEAM]"), L("[/IS TEAM]")), owner, items));
        Clear(items);
        Assert.Equal(new[] { true, false, false, true, false }, Kept(Filter(L("[IS TEAM]"), L("[CHARCTER TEAM]"), L("[/IS TEAM]")), owner, items));
        Clear(items);
        Assert.Equal(new[] { false, true, true, false, true }, Kept(Filter(L("[IS TEAM]"), L("[MONSTER TEAM]"), L("[/IS TEAM]")), owner, items));
        Clear(items);
        Assert.Equal(new[] { true, true, true, true, true }, Kept(Filter(L("[IS TEAM]"), L("[MY TEAM]"), L("[CHARCTER TEAM]"), L("[/IS TEAM]")), owner, items));      // 命中任何一项
        Clear(items);
        Assert.Equal(new[] { false, false, false, false, false }, Kept(Filter(L("[IS TEAM]"), L("[/IS TEAM]")), owner, items));                                      // 码表为空一个也留不下
    }

    private static void Clear(IEnumerable<Entity> items)
    {
        foreach (Entity e in items)
        {
            e.Log.Clear();
        }
    }

    [Fact]
    public void IsTeamUsesTheObjectsOwnTeamNumberWithPlayerTeamsZeroToEleven()
    {
        var owner = new Rich { TeamId = 5 };
        Rich[] items = new[] { 5, 6, 11, 12, 100, -1 }.Select(t => new Rich { Kind = ActEntityKind.Character, TeamId = t }).ToArray();

        Assert.Equal(new[] { true, false, false, false, false, false }, Kept(Filter(L("[IS TEAM]"), L("[MY TEAM]"), L("[/IS TEAM]")), owner, items));
        Clear(items);
        Assert.Equal(new[] { false, true, true, true, true, true }, Kept(Filter(L("[IS TEAM]"), L("[ENEMY TEAM]"), L("[/IS TEAM]")), owner, items));
        Clear(items);
        Assert.Equal(new[] { true, true, true, false, false, false }, Kept(Filter(L("[IS TEAM]"), L("[CHARCTER TEAM]"), L("[/IS TEAM]")), owner, items));    // 0 到 11
        Clear(items);
        Assert.Equal(new[] { false, false, false, false, true, false }, Kept(Filter(L("[IS TEAM]"), L("[MONSTER TEAM]"), L("[/IS TEAM]")), owner, items));    // 恰好 100
    }

    // ------------------------------------------------------------------ [ZSPEED]

    [Fact]
    public void ZSpeedComparesTheTargetsVerticalSpeedAsAFloat()
    {
        var down = new Rich { Kind = ActEntityKind.Character, Vz = -5f };
        var still = new Rich { Kind = ActEntityKind.Character, Vz = 0f };
        var drift = new Rich { Kind = ActEntityKind.Character, Vz = 0.5f };
        var up = new Rich { Kind = ActEntityKind.Character, Vz = 5f };
        Rich[] items = { down, still, drift, up };

        void Check(string op, int n, params bool[] expected)
        {
            Clear(items);
            Assert.Equal(expected, Kept(Filter(L("[ZSPEED]"), L(op, n)), new Entity(), items));
        }

        Check("[<]", 0, true, false, false, false);
        Check("[>=]", 0, false, true, true, true);
        Check("[==]", 0, false, true, false, false);
        Check("[!=]", 0, true, false, true, true);
        Check("[>]", 0, false, false, true, true);           // 0.5 大于 0:浮点比较,不取整
        Check("[<=]", 0, true, true, false, false);
        Check("[>]", 4, false, false, false, true);
    }

    [Fact]
    public void ZSpeedOfAnObjectWithoutSpeedKnowledgeIsZero()
    {
        var plain = new Entity { Kind = ActEntityKind.Character };
        Assert.Equal(new[] { true }, Kept(Filter(L("[ZSPEED]"), L("[==]", 0)), new Entity(), plain));
        plain.Log.Clear();
        Assert.Equal(new[] { false }, Kept(Filter(L("[ZSPEED]"), L("[<]", 0)), new Entity(), plain));
    }

    // ------------------------------------------------------------------ [IS END MAP]

    private static ActProgram OwnerCondition(params ScriptLine[] conditions) => P(Cat(
        Trigger(Cat(new[] { L("[FRAME]", 0, 0) }, conditions, new[] { L("[DO BEHAVIOR]"), L("[ME]", 0) })),
        Behavior(Speed(1))));

    private static bool OwnerPasses(ActProgram p, Rich owner, int width = 2000, int height = 800)
    {
        new ActRunner(p, owner, new Host { MapWidth = width, MapHeight = height }).Update(0, 0.016f);
        return owner.Log.Count > 0;
    }

    [Fact]
    public void IsEndMapPassesNearEitherEndOfTheStageOnlyWhenNotMovingTowardsTheMiddle()
    {
        ActProgram p = OwnerCondition(L("[IS END MAP]"));
        bool At(float x, float vx, int width = 2000) => OwnerPasses(p, new Rich { X = x, Vx = vx }, width);

        Assert.True(At(150, 0));         // 左端:X ≤ 150 且速度不向右
        Assert.True(At(150, -3));
        Assert.True(At(0, 0));
        Assert.True(At(-20, -1));
        Assert.False(At(150, 3));        // 在左端却往右走:不算到头
        Assert.False(At(151, 0));
        Assert.False(At(151, -3));
        Assert.True(At(1851, 0));        // 右端:X > 宽 − 150 且速度不向左
        Assert.True(At(1851, 3));
        Assert.True(At(1999, 0));
        Assert.False(At(1851, -3));
        Assert.False(At(1850, 0));       // 恰好等于 宽 − 150 不算
        Assert.False(At(1850, 5));
        Assert.True(At(150.9f, 0));      // 整数 X 比较:150.9 取整是 150
        Assert.False(At(1850.9f, 0));
        Assert.False(At(50, 0, 0));      // 取不到舞台尺寸:不通过
    }

    [Fact]
    public void IsEndMapVerticallyUsesDepthMarginsOf200AndOf50AndTheMapHeight()
    {
        ActProgram p = OwnerCondition(L("[IS END MAP VERTICALLY]"));
        bool At(float y, float vy, int height = 800) => OwnerPasses(p, new Rich { Y = y, Vy = vy }, 2000, height);

        Assert.True(At(200, 0));
        Assert.True(At(200, -1));
        Assert.True(At(0, 0));
        Assert.False(At(200, 1));
        Assert.False(At(201, 0));
        Assert.True(At(751, 0));         // 高 − 50 = 750 之外
        Assert.True(At(751, 1));
        Assert.False(At(751, -1));
        Assert.False(At(750, 0));
        Assert.False(At(100, 0, 0));     // 取不到舞台尺寸
    }

    [Fact]
    public void EndMapConditionsReadTheOwnersSpeedNotTheTargets()
    {
        // 拥有者在左端不动、通过;[WHICH] 之后的目标速度与位置不影响整体判断
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[IS END MAP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1))));
        var target = new Rich { Kind = ActEntityKind.Character, X = 1000, Vx = 9 };
        var host = new Host { MapWidth = 2000, MapHeight = 800 };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { target };
        new ActRunner(p, new Rich { X = 10, Vx = 0 }, host).Update(0, 0.016f);
        Assert.Single(target.Log);

        var far = new Rich { Kind = ActEntityKind.Character, X = 10, Vx = 0 };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { far };
        new ActRunner(p, new Rich { X = 1000, Vx = 0 }, host).Update(0, 0.016f);
        Assert.Empty(far.Log);
    }

    // ------------------------------------------------------------------ 元素条件

    private static ActProgram OnDamageWhen(string condition, bool onDamage = true) => P(Cat(
        Trigger(onDamage ? new[] { L("[ON DAMAGE]"), L(condition), L("[DO BEHAVIOR]"), L("[ME]", 0) } : new[] { L("[FRAME]", 0, 0), L(condition), L("[DO BEHAVIOR]"), L("[ME]", 0) }),
        Behavior(Speed(1))));

    private static bool FiresOnHit(string condition, int mask)
    {
        var owner = new Entity();
        new ActRunner(OnDamageWhen(condition), owner, new Host()).OnDamage(new Entity { Kind = ActEntityKind.Character }, mask);
        return owner.Log.Count > 0;
    }

    [Fact]
    public void ElementConditionsLookAtTheElementFlagsOfTheHitBeingProcessed()
    {
        const int fire = 1, water = 2, dark = 4, light = 8;
        Assert.True(FiresOnHit("[IS FIRE DAMAGE]", fire));
        Assert.True(FiresOnHit("[IS FIRE DAMAGE]", fire | water));         // 带火就算
        Assert.False(FiresOnHit("[IS FIRE DAMAGE]", water));
        Assert.False(FiresOnHit("[IS FIRE DAMAGE]", 0));
        Assert.True(FiresOnHit("[IS WATER DAMAGE]", water));
        Assert.False(FiresOnHit("[IS WATER DAMAGE]", fire | dark | light));
        Assert.True(FiresOnHit("[IS DARK DAMAGE]", dark));
        Assert.False(FiresOnHit("[IS DARK DAMAGE]", light));
        Assert.True(FiresOnHit("[IS LIGHT DAMAGE]", light | fire));
        Assert.False(FiresOnHit("[IS LIGHT DAMAGE]", dark));
    }

    [Fact]
    public void IsNoneDamageNeedsTheHitToCarryNoElementAtAll()
    {
        Assert.True(FiresOnHit("[IS NONE DAMAGE]", 0));
        Assert.False(FiresOnHit("[IS NONE DAMAGE]", 1));
        Assert.False(FiresOnHit("[IS NONE DAMAGE]", 2 | 8));
    }

    [Fact]
    public void WithoutAHitInHandEveryElementConditionPasses()
    {
        // 没带元素的 OnDamage(attacker) 与非事件的触发器都没有"这一击":客户端取不到伤害信息时条件直接通过
        foreach (string tag in new[] { "[IS FIRE DAMAGE]", "[IS WATER DAMAGE]", "[IS DARK DAMAGE]", "[IS LIGHT DAMAGE]", "[IS NONE DAMAGE]" })
        {
            var legacy = new Entity();
            new ActRunner(OnDamageWhen(tag), legacy, new Host()).OnDamage(new Entity());
            Assert.Single(legacy.Log);

            var frame = new Entity();
            new ActRunner(OnDamageWhen(tag, onDamage: false), frame, new Host()).Update(0, 0.016f);
            Assert.Single(frame.Log);
        }
    }

    [Fact]
    public void TheHitsElementsAreForgottenOnceTheEventHasBeenDispatched()
    {
        // 触发器 0 等挨打且是水;触发器 1 是帧触发器带火元素条件。先挨一下水,再走一帧:帧触发器看不到上一击的元素,照样通过
        ActProgram p = P(Cat(
            Trigger(L("[ON DAMAGE]"), L("[IS WATER DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[IS FIRE DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(1)),
            Behavior(Speed(2))));
        var owner = new Entity();
        var runner = new ActRunner(p, owner, new Host());
        runner.OnDamage(new Entity(), 2);
        runner.Update(0, 0.016f);
        Assert.Equal(new[] { "speed:1,-,-", "speed:2,-,-" }, owner.Log.ToArray());
    }

    // ------------------------------------------------------------------ [IS TARGETACTIVESTATUS]

    [Fact]
    public void IsTargetActiveStatusKeepsTheObjectsThatCarryThatStatus()
    {
        var burning = new Rich { Kind = ActEntityKind.Character };
        burning.StatusSet.Add(9);
        var frozen = new Rich { Kind = ActEntityKind.Character };
        frozen.StatusSet.Add(1);
        var clean = new Rich { Kind = ActEntityKind.Character };
        var plain = new Entity { Kind = ActEntityKind.Monster };      // 没有状态系统的对象:默认没有
        Assert.Equal(new[] { true, false, false, false }, Kept(Filter(L("[IS TARGETACTIVESTATUS]", 9)), new Entity(), burning, frozen, clean, plain));
    }

    // ------------------------------------------------------------------ [IS FRONTPOS] / [IS BACKPOS]

    [Fact]
    public void FrontPosAndBackPosAskTheOwnerWhetherAnXIsOnTheSideBehindItsFacingAsRead()
    {
        Entity[] At(params int[] xs) => xs.Select(x => new Entity { Kind = ActEntityKind.Character, X = x }).ToArray();

        // 拥有者朝右:X 比自己小算"是"(客户端虚槽 0x110 的读法);FRONTPOS 留"是"的,BACKPOS 留"否"的
        Entity[] a = At(50, 100, 150);
        Assert.Equal(new[] { true, false, false }, Kept(Filter(L("[IS FRONTPOS]")), new Entity { X = 100, FacingLeft = false }, a));
        Clear(a);
        Assert.Equal(new[] { false, true, true }, Kept(Filter(L("[IS BACKPOS]")), new Entity { X = 100, FacingLeft = false }, a));
        // 拥有者朝左:X 比自己大算"是"
        Entity[] b = At(50, 100, 150);
        Assert.Equal(new[] { false, false, true }, Kept(Filter(L("[IS FRONTPOS]")), new Entity { X = 100, FacingLeft = true }, b));
        Clear(b);
        Assert.Equal(new[] { true, true, false }, Kept(Filter(L("[IS BACKPOS]")), new Entity { X = 100, FacingLeft = true }, b));
        // 整数 X 比较:100.9 与 100.2 取整相同,不算"是"
        var near = new Entity { Kind = ActEntityKind.Character, X = 100.2f };
        Assert.Equal(new[] { false }, Kept(Filter(L("[IS FRONTPOS]")), new Entity { X = 100.9f, FacingLeft = false }, near));
    }

    // ------------------------------------------------------------------ 接口默认实现

    [Fact]
    public void TheInterfaceDefaultsCoverTeamSpeedStatusAndMapExtent()
    {
        IActEntity hero = new Entity { Kind = ActEntityKind.Character };
        IActEntity monster = new Entity { Kind = ActEntityKind.Monster };
        IActEntity orphan = new Entity { Kind = ActEntityKind.Passive };
        IActEntity heroShot = new Entity { Kind = ActEntityKind.Passive, Master = hero };
        IActEntity chained = new Entity { Kind = ActEntityKind.Passive, Master = heroShot };
        Assert.Equal(new[] { 0, 100, 100, 0, 0 }, new[] { hero.Team, monster.Team, orphan.Team, heroShot.Team, chained.Team });
        Assert.Equal((0f, 0f, 0f), (monster.SpeedX, monster.SpeedY, monster.SpeedZ));
        Assert.False(monster.HasActiveStatus(9));
        IActHost host = new CountingHost();
        Assert.Equal((0, 0), (host.MapWidth, host.MapHeight));
    }
}
