using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [SUMMON MONSTER]、[SET HP] 与 [IS CHILD](M41,证据 E-134):编译出来的字段、位置、朝向与等级的规则(客户端 0x010b683d 起的执行块)、
/// 值每次执行只取一次、对死亡目标不动、按创建者过滤。脚本内容全是合成的,数是测试用的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>[SUMMON MONSTER] 块:编号、等级、偏移 (x, y, z),再接标志子标签。</summary>
    private static ScriptLine[] SummonBlock(int index, int level, int x, int y, int z, params ScriptLine[] flags)
    {
        var list = new List<ScriptLine> { L("[SUMMON MONSTER]"), L("[INDEX]", index), L("[LEVEL]", level), L("[POS]", x, y, z) };
        list.AddRange(flags);
        list.Add(L("[/SUMMON MONSTER]"));
        return list.ToArray();
    }

    private static ActBehavior FirstBehavior(ActProgram p) => p.Behaviors[0].Items[0];

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void SummonMonsterCompilesItsValuesAndFlags()
    {
        ActProgram p = P(Me(SummonBlock(7, 3, 40, 10, 5,
            L("[USE TARGET TEAM]"), L("[USE MY DIRECTION]"), L("[USE MY BASEPOS]"), L("[NOT USE OBJECT DIRECTION]"), L("[USE MAP POS]"),
            L("[USE OBJECT ZPOS]"), L("[TEAM]", 5), L("[SUMMON TIME]", 1500), L("[SUMMON MARK]", 2, 3), L("[COLLISION GROUP]", 4), L("[NO EFFECT]"))));
        ActBehavior b = FirstBehavior(p);
        Assert.Equal(ActBehaviorKind.Summon, b.Kind);
        Assert.Equal(new[] { 7, 3, 40, 10, 5 }, new[] { b.Index.First, b.Level.First, b.PosX.First, b.PosY.First, b.PosZ.First });
        Assert.True(b.UseTargetTeam);
        Assert.True(b.UseMyDirection);
        Assert.True(b.UseMyBasePos);
        Assert.True(b.NotUseDirection);
        Assert.True(b.UseMapPos);
        Assert.True(b.UseObjectZ);
        Assert.Equal(5, b.Team);
        Assert.Equal(1500, b.SummonTimeMs);
        Assert.Equal(ActSummonEffect.None, b.SummonEffect);

        // 什么标志也不写:全是缺省(没有队伍、不限时、魔法阵效果)
        ActBehavior plain = FirstBehavior(P(Me(SummonBlock(1, 0, 0, 0, 0))));
        Assert.False(plain.UseTargetTeam || plain.UseMyDirection || plain.UseMyBasePos || plain.NotUseDirection || plain.UseMapPos || plain.UseObjectZ);
        Assert.Equal((-1, 0, ActSummonEffect.MagicCircle), (plain.Team, plain.SummonTimeMs, plain.SummonEffect));

        // [JUMP OUT] 后面跟着跳出用的几个数:都被跳过,后面的标志照常读,也不会当成出生位置
        ActBehavior jump = FirstBehavior(P(Me(SummonBlock(1, 0, 0, 0, 0, L("[JUMP OUT]", 9, 9, 9, 1, 1), L("[SUMMON TIME]", 80)))));
        Assert.Equal(ActSummonEffect.JumpOut, jump.SummonEffect);
        Assert.Equal(80, jump.SummonTimeMs);
        Assert.Equal(0, jump.PosX.First);
    }

    [Fact]
    public void SummonWithoutAnIndexIsUnsupportedAndATypoInTheCloserStillClosesTheBlock()
    {
        ActProgram bare = P(Me(L("[SUMMON MONSTER]"), L("[POS]", 1, 2, 3), L("[/SUMMON MONSTER]")));
        Assert.Equal(ActBehaviorKind.Unsupported, FirstBehavior(bare).Kind);

        // 原版数据里有把结束标签拼错的写法([/SUMMON MONSETER]):照样关掉这个块,后面的行为不被吞
        ActProgram typo = P(Me(L("[SUMMON MONSTER]"), L("[INDEX]", 3), L("[POS]", 1, 2, 3), L("[/SUMMON MONSETER]"), L("[SET HP]", 0)));
        Assert.Equal(new[] { ActBehaviorKind.Summon, ActBehaviorKind.SetHp }, typo.Behaviors[0].Items.Select(i => i.Kind).ToArray());
        Assert.Equal(1, typo.Behaviors[0].Items[0].PosX.First);
    }

    [Fact]
    public void SetHpAndIsChildCompileAndAnUnreadableValueIsUnsupported()
    {
        ActBehavior hp = FirstBehavior(P(Me(L("[SET HP]", 25))));
        Assert.Equal(ActBehaviorKind.SetHp, hp.Kind);
        Assert.Equal(25, hp.Hp.First);
        Assert.Equal(ActBehaviorKind.Unsupported, FirstBehavior(P(Me(L("[SET HP]")))).Kind);      // 没有值:读不懂

        ActProgram child = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKUP]"), L("[IS CHILD]", 1), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET HP]", 0))));
        Assert.True(child.Triggers[0].Supported);
    }

    // ------------------------------------------------------------------ 召唤:位置、朝向与等级

    [Fact]
    public void SummonPlacesTheMonsterByTheTargetsFacingAndAddsTheTargetsLevel()
    {
        ActProgram p = P(Me(SummonBlock(7, 3, 40, 10, 5)));

        var left = new Entity { X = 100, Y = 50, Z = 8, FacingLeft = true, Level = 20 };
        var host = new Host();
        Run(p, left, host);
        var s = Assert.Single(host.Summons);
        Assert.Same(left, s.master);
        Assert.Equal((7, 23), (s.index, s.level));           // [LEVEL] 3 + 目标的等级 20
        Assert.Equal((60f, 60f, 5f), (s.x, s.y, s.z));       // 朝左:X 偏移取反;Y 直接加;Z 只有偏移(没写 [USE OBJECT ZPOS])
        Assert.True(s.left);
        Assert.Equal(0, s.lifeMs);

        var right = new Entity { X = 100, Y = 50, FacingLeft = false, Level = 20 };
        host = new Host();
        Run(p, right, host);
        s = Assert.Single(host.Summons);
        Assert.Equal((140f, 60f, 5f), (s.x, s.y, s.z));
        Assert.False(s.left);
    }

    [Fact]
    public void SummonFlagsChangeTheDirectionTheBaseAndTheHeight()
    {
        // 以拥有者为基准、用拥有者的朝向、带基准的高度;作用对象是另一个人
        var owner = new Entity { X = 100, Y = 50, Z = 12, FacingLeft = false, Level = 5 };
        var other = new Entity { Kind = ActEntityKind.Character, X = 500, Y = 400, Z = 90, FacingLeft = true, Level = 9 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        Run(P(OnEnemies(SummonBlock(1, 0, 40, 10, 5, L("[USE MY BASEPOS]"), L("[USE MY DIRECTION]"), L("[USE OBJECT ZPOS]")))), owner, host);
        var s = Assert.Single(host.Summons);
        Assert.Same(other, s.master);                        // 召唤者是被作用的对象
        Assert.Equal(9, s.level);                            // 等级取目标的(0 + 9)
        Assert.Equal((140f, 60f, 17f), (s.x, s.y, s.z));     // 拥有者朝右、在 (100, 50, 12)
        Assert.False(s.left);

        // 不用拥有者的基准:以目标为基准,目标朝左;[USE OBJECT ZPOS] 带上目标的高度
        host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        Run(P(OnEnemies(SummonBlock(1, 0, 40, 10, 5, L("[USE OBJECT ZPOS]")))), owner, host);
        s = Assert.Single(host.Summons);
        Assert.Equal((460f, 410f, 95f), (s.x, s.y, s.z));
        Assert.True(s.left);

        // [NOT USE OBJECT DIRECTION]:固定朝右,不管目标朝哪
        host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        Run(P(OnEnemies(SummonBlock(1, 0, 40, 10, 5, L("[NOT USE OBJECT DIRECTION]")))), owner, host);
        s = Assert.Single(host.Summons);
        Assert.Equal(540f, s.x);
        Assert.False(s.left);

        // [USE MAP POS]:三个数原样是绝对坐标,朝向与基准都不参与
        host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        Run(P(OnEnemies(SummonBlock(1, 0, 40, 10, 5, L("[USE MAP POS]"), L("[USE OBJECT ZPOS]")))), owner, host);
        s = Assert.Single(host.Summons);
        Assert.Equal((40f, 10f, 5f), (s.x, s.y, s.z));
    }

    [Fact]
    public void SummonLevelFollowsTheKindOfTheTargetAndIsAtLeastOne()
    {
        static (int level, int count) Level(int levelValue, ActEntityKind kind, int targetLevel)
        {
            var host = new Host();
            var owner = new Entity { Kind = kind, Level = targetLevel };
            Run(P(Me(SummonBlock(1, levelValue, 0, 0, 0))), owner, host);
            return (host.Summons[0].level, host.Summons.Count);
        }

        Assert.Equal((12, 1), Level(2, ActEntityKind.Monster, 10));        // 怪物:写的等级加自己的
        Assert.Equal((12, 1), Level(2, ActEntityKind.Character, 10));      // 主角同理
        Assert.Equal((2, 1), Level(2, ActEntityKind.Passive, 10));         // 被动对象:写的是绝对等级
        Assert.Equal((20, 1), Level(-1, ActEntityKind.Monster, 10));       // -1 先取目标的等级,再加上目标的
        Assert.Equal((10, 1), Level(-1, ActEntityKind.Passive, 10));       // 被动对象:只有取到的那一份
        Assert.Equal((1, 1), Level(0, ActEntityKind.Passive, 0));          // 至少 1 级
        Assert.Equal((1, 1), Level(-30, ActEntityKind.Monster, 5));
    }

    [Fact]
    public void SummonValuesAreEvaluatedOncePerFiringAndDeadTargetsAreSkipped()
    {
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character, X = 100, FacingLeft = false };
        var b = new Entity { Kind = ActEntityKind.Character, X = 300, FacingLeft = false };
        var dead = new Entity { Kind = ActEntityKind.Character, X = 700, IsAlive = false };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b, dead };
        host.Randoms.Enqueue(2);          // [RANDOM SELECT] 编号:第三项
        host.Randoms.Enqueue(5);          // [RANDOM] 10 20:10 + 5
        host.Randoms.Enqueue(99);         // 多取就会被后面的断言发现
        Run(P(OnEnemies(
            L("[SUMMON MONSTER]"), L("[INDEX]"), L("[RANDOM SELECT]", 4, 8, 15), L("[/RANDOM SELECT]"), L("[POS]"), L("[RANDOM]", 10, 20), L("[/SUMMON MONSTER]"))), owner, host);

        Assert.Equal(2, host.Summons.Count);                          // 死掉的目标不召唤
        Assert.Equal(new[] { 15, 15 }, host.Summons.Select(x => x.index).ToArray());
        Assert.Equal(new[] { 115f, 315f }, host.Summons.Select(x => x.x).ToArray());   // 同一个偏移 15 用在每个目标上
        Assert.Single(host.Randoms);                                  // 只取了两次随机数
    }

    [Fact]
    public void SummonTimeGoesToTheHostAndTheHostSeesTheMasterObject()
    {
        var owner = new Entity { X = 5 };
        var host = new Host();
        Run(P(Me(SummonBlock(3, 0, 0, 0, 0, L("[SUMMON TIME]", 2500)))), owner, host);
        var s = Assert.Single(host.Summons);
        Assert.Equal(2500, s.lifeMs);
        Assert.Same(owner, s.master);
    }

    // ------------------------------------------------------------------ [SET HP]

    [Fact]
    public void SetHpEvaluatesOnceAndReachesAliveTargetsOnly()
    {
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character, Hp = 50, HpMax = 100 };
        var b = new Entity { Kind = ActEntityKind.Character, Hp = 80, HpMax = 100 };
        var dead = new Entity { Kind = ActEntityKind.Character, IsAlive = false };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b, dead };
        host.Randoms.Enqueue(3);
        host.Randoms.Enqueue(50);
        Run(P(OnEnemies(L("[SET HP]"), L("[RANDOM]", 10, 20))), owner, host);
        Assert.Equal(new[] { "sethp:13" }, a.Log.ToArray());          // [RANDOM] 10 20:10 + 3,两个目标同一个值
        Assert.Equal(new[] { "sethp:13" }, b.Log.ToArray());
        Assert.Empty(dead.Log);
        Assert.Single(host.Randoms);

        var killed = new Entity();
        Run(P(Me(L("[SET HP]", 0))), killed);
        Assert.Equal(new[] { "sethp:0" }, killed.Log.ToArray());
        Assert.False(killed.IsAlive);
    }

    // ------------------------------------------------------------------ [IS CHILD]

    [Fact]
    public void IsChildKeepsTheObjectsCreatedByTheOwnerOrTheOthers()
    {
        var owner = new Entity();
        var stranger = new Entity { Kind = ActEntityKind.Monster };
        var child1 = new Entity { Kind = ActEntityKind.Monster, Master = owner };
        var child2 = new Entity { Kind = ActEntityKind.Passive, Master = owner };
        var other = new Entity { Kind = ActEntityKind.Monster, Master = stranger };

        ActProgram Which(int flag) => P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKUP]"), L("[IS CHILD]", flag), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET HP]", 0))));

        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { stranger, child1, child2, other };
        Run(Which(1), owner, host);                       // 参数非 0:只留创建者是拥有者的
        Assert.Equal(new[] { true, false, false, true }, new[] { stranger.IsAlive, child1.IsAlive, child2.IsAlive, other.IsAlive });

        var stranger2 = new Entity { Kind = ActEntityKind.Monster };
        var child3 = new Entity { Kind = ActEntityKind.Monster, Master = owner };
        host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { stranger2, child3 };
        Run(Which(0), owner, host);                       // 参数为 0:留下不是的
        Assert.False(stranger2.IsAlive);
        Assert.True(child3.IsAlive);
    }

    // ------------------------------------------------------------------ [RESTORE]

    private static ActBehavior Compiled(params ScriptLine[] behavior) => FirstBehavior(P(Me(behavior)));

    [Fact]
    public void RestoreCompilesTheKindTheAmountAndThePercentFlag()
    {
        ActBehavior pct = Compiled(L("[RESTORE]"), L("[HP]", -20), L("[%]"));
        Assert.Equal(ActBehaviorKind.Restore, pct.Kind);
        Assert.Equal((false, true, -20), (pct.RestoreMp, pct.RestorePercent, pct.RestoreAmount.First));

        ActBehavior pts = Compiled(L("[RESTORE]"), L("[HP]", 30), L("[POINT]"));
        Assert.Equal((false, false, 30), (pts.RestoreMp, pts.RestorePercent, pts.RestoreAmount.First));

        ActBehavior mp = Compiled(L("[RESTORE]"), L("[MP]", 5));
        Assert.Equal((ActBehaviorKind.Restore, true, false), (mp.Kind, mp.RestoreMp, mp.RestorePercent));

        // 别的种类、缺数量:读不懂,什么都不做
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[RESTORE]"), L("[SP]", 5)).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[RESTORE]"), L("[HP]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[RESTORE]")).Kind);

        // 后面紧跟着别的行为:[%] 只归 [RESTORE],后一个行为照常读出来
        ActProgram two = P(Me(L("[RESTORE]"), L("[HP]", 5), L("[SET HP]", 9)));
        Assert.Equal(new[] { ActBehaviorKind.Restore, ActBehaviorKind.SetHp }, two.Behaviors[0].Items.Select(i => i.Kind).ToArray());
        Assert.False(two.Behaviors[0].Items[0].RestorePercent);
    }

    [Fact]
    public void RestoreChangesTheHealthByPointsOrByPercentOfTheTargetsOwnMaximum()
    {
        var owner = new Entity { Hp = 100, HpMax = 200 };
        Run(P(Me(L("[RESTORE]"), L("[HP]", -20), L("[%]"))), owner);       // 200 的 20% = 40
        Assert.Equal(new[] { "sethp:60" }, owner.Log.ToArray());

        var points = new Entity { Hp = 100, HpMax = 200 };
        Run(P(Me(L("[RESTORE]"), L("[HP]", -15))), points);
        Assert.Equal(85, points.Hp);

        // 每个目标按自己的最大生命算百分比;死掉的目标不动
        var small = new Entity { Kind = ActEntityKind.Character, Hp = 10, HpMax = 50 };
        var large = new Entity { Kind = ActEntityKind.Character, Hp = 10, HpMax = 1000 };
        var dead = new Entity { Kind = ActEntityKind.Character, Hp = 0, HpMax = 100, IsAlive = false };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { small, large, dead };
        Run(P(OnEnemies(L("[RESTORE]"), L("[HP]", 10), L("[%]"))), new Entity(), host);
        Assert.Equal(new[] { 15, 110 }, new[] { small.Hp, large.Hp });       // +5 与 +100
        Assert.Empty(dead.Log);
    }

    [Fact]
    public void RestoreStopsAtTheMaximumAndAtZeroWhichIsDeathAndTruncatesTowardZero()
    {
        var full = new Entity { Hp = 190, HpMax = 200 };
        Run(P(Me(L("[RESTORE]"), L("[HP]", 50))), full);
        Assert.Equal(200, full.Hp);                                          // 加到最大生命为止

        var dying = new Entity { Hp = 30, HpMax = 200 };
        Run(P(Me(L("[RESTORE]"), L("[HP]", -20), L("[%]"))), dying);         // -40:降到 0 以下按 0,也就是死
        Assert.Equal(new[] { "sethp:0" }, dying.Log.ToArray());
        Assert.False(dying.IsAlive);

        // 百分比结果向零截断:7 的 -10% 是 -0.7 -> 0,什么都不做;7 的 +10% 同理
        var tiny = new Entity { Hp = 5, HpMax = 7 };
        Run(P(Me(L("[RESTORE]"), L("[HP]", -10), L("[%]"))), tiny);
        Assert.Empty(tiny.Log);
        Run(P(Me(L("[RESTORE]"), L("[HP]", 10), L("[%]"))), tiny);
        Assert.Empty(tiny.Log);
    }

    [Fact]
    public void RestoreIgnoresMpAndObjectsWithoutAMaximumHealthAndEvaluatesTheAmountOnce()
    {
        var mp = new Entity { Hp = 10, HpMax = 100 };
        Run(P(Me(L("[RESTORE]"), L("[MP]", -50))), mp);
        Assert.Empty(mp.Log);

        var passive = new Entity { Kind = ActEntityKind.Passive, Hp = 0, HpMax = 0 };
        Run(P(Me(L("[RESTORE]"), L("[HP]", -20), L("[%]"))), passive);
        Assert.Empty(passive.Log);                                           // 没有最大生命:被动对象不会被 [RESTORE] 弄没

        var a = new Entity { Kind = ActEntityKind.Character, Hp = 50, HpMax = 100 };
        var b = new Entity { Kind = ActEntityKind.Character, Hp = 50, HpMax = 100 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        host.Randoms.Enqueue(4);
        host.Randoms.Enqueue(50);
        Run(P(OnEnemies(L("[RESTORE]"), L("[HP]"), L("[RANDOM]", 10, 20))), new Entity(), host);
        Assert.Equal(new[] { 64, 64 }, new[] { a.Hp, b.Hp });                // 数量只取一次:10 + 4 = 14,两个目标同一个数
        Assert.Single(host.Randoms);
    }

    // ------------------------------------------------------------------ 触发器开关的持久副本

    [Fact]
    public void ASharedSwitchArrayKeepsAnOffAcrossRunnersAndAcrossBegin()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Cat(Speed(5), new[] { L("[TRIGGER]", 0), L("[OFF]") }))));
        bool[] state = ActRunner.NewEnabledState(p);
        Assert.Equal(new[] { true }, state);

        var first = new Entity();
        var run1 = new ActRunner(p, first, new Host(), state);
        run1.Update(0, 0.016f);
        Assert.Single(first.Log);
        Assert.Equal(new[] { false }, state);                                // 行为改的就是共享的这一份

        var second = new Entity();
        var run2 = new ActRunner(p, second, new Host(), state);
        Assert.False(run2.IsEnabled(0));                                     // 新建运行器不重置开关
        run2.Update(0, 0.016f);
        run2.Begin();                                                        // 动作重新开始也不重置共享的开关
        run2.Update(0, 0.016f);
        Assert.Empty(second.Log);
        Assert.False(run2.IsEnabled(0));

        // 对照:没给共享数组的运行器每次回到脚本里的初值
        var third = new Entity();
        var fresh = new ActRunner(p, third, new Host());
        Assert.True(fresh.IsEnabled(0));
        fresh.Update(0, 0.016f);
        Assert.Single(third.Log);
        Assert.False(fresh.IsEnabled(0));
        fresh.Begin();
        Assert.True(fresh.IsEnabled(0));
    }

    [Fact]
    public void ASwitchArrayOfTheWrongLengthIsIgnoredAndTheInitialValuesComeFromTheScript()
    {
        ActProgram p = P(Cat(
            Trigger(L("[ENABLE]"), L("[OFF]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(5))));
        Assert.Equal(new[] { false, true }, ActRunner.NewEnabledState(p));    // [ENABLE] [OFF] 写在脚本里,没写的算开

        var wrong = new bool[3];
        var runner = new ActRunner(p, new Entity(), new Host(), wrong);
        Assert.Equal(new[] { false, true }, new[] { runner.IsEnabled(0), runner.IsEnabled(1) });   // 长度对不上:当没给,自己一份
        Assert.Equal(new[] { false, false, false }, wrong);                   // 传进来的数组原样不动

        Assert.Throws<ArgumentNullException>(() => ActRunner.NewEnabledState(null));
    }
}
