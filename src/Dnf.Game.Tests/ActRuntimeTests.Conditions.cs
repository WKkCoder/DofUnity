using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// M61-c(证据 E-156,客户端静态读码):判定函数 0x010d3630 里的三个条件。
/// [CHECK DISTANCE](码 0x3e):整体真假——拥有者要是生物类,场上有敌对的角色类对象落在横向、纵深两个范围内才通过,不改工作集;
/// [IS GRABABLE](码 0x2d):逐对象过滤——被动对象剔除,其余按"能不能被抓"与参数比较;
/// [TRIGGER CHECK](码 0x2e):没写族是整体真假(自己的第 n 个触发器),写了族是逐对象过滤(取那一族动作的触发器开关)。
/// 脚本内容全是合成的。观察办法:条件之后接 [DO BEHAVIOR] [ME] 的设速度,日志里有没有 speed 就是触发器有没有触发。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>条件们 + [DO BEHAVIOR] [ME] 0(行为组 0 是设速度 4)。</summary>
    private static ActProgram CondProgram(params ScriptLine[] conditions)
    {
        var body = new List<ScriptLine>(conditions) { L("[DO BEHAVIOR]"), L("[ME]", 0) };
        return P(Cat(Trigger(body.ToArray()), Behavior(Speed(4))));
    }

    private static bool Fires(ActProgram p, Entity owner, Host host = null)
    {
        owner.Log.Clear();
        new ActRunner(p, owner, host ?? new Host()).Update(0, 0.016f);
        return owner.Log.Contains("speed:4,-,-");
    }

    private static Entity HeroAt(float x, float y) => new Entity { Kind = ActEntityKind.Character, X = x, Y = y };

    private static Host EnemyHost(params Entity[] enemies)
    {
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = enemies.Cast<IActEntity>().ToList();
        return host;
    }

    // ------------------------------------------------------------------ [CHECK DISTANCE] 编译

    [Fact]
    public void CheckDistanceReadsTwoRangesAndLeavesTheNextConditionAlone()
    {
        ActProgram p = CondProgram(L("[CHECK DISTANCE]", 300, 80), L("[ZPOS]"), L("[<=]", 5));
        ActTrigger t = Assert.Single(p.Triggers);

        Assert.True(t.Supported);
        Assert.Equal(new[] { ActRecordKind.CheckDistance, ActRecordKind.ZPos, ActRecordKind.DoBehavior }, t.Records.Select(r => r.Kind).ToArray());
        Assert.Equal(new[] { 300, 80 }, t.Records[0].Ints);
        Assert.Equal(5, t.Records[1].Number);
    }

    [Fact]
    public void CheckDistanceWithoutTwoNumbersIsUnsupported()
    {
        ActTrigger one = Assert.Single(CondProgram(L("[CHECK DISTANCE]", 300)).Triggers);
        Assert.False(one.Supported);
        Assert.Contains("[CHECK DISTANCE]", one.UnsupportedTags);

        ActTrigger none = Assert.Single(CondProgram(L("[CHECK DISTANCE]")).Triggers);
        Assert.False(none.Supported);
    }

    // ------------------------------------------------------------------ [CHECK DISTANCE] 运行

    [Fact]
    public void CheckDistancePassesWhenAnEnemyCharacterIsInsideBothRanges()
    {
        var owner = new Entity { X = 100, Y = 50 };

        Assert.True(Fires(CondProgram(L("[CHECK DISTANCE]", 300, 80)), owner, EnemyHost(HeroAt(350, 120))));
    }

    [Fact]
    public void CheckDistanceRangesAreInclusiveAndUseAbsoluteValues()
    {
        var owner = new Entity { X = 100, Y = 50 };
        ActProgram p = CondProgram(L("[CHECK DISTANCE]", 300, 80));

        Assert.True(Fires(p, owner, EnemyHost(HeroAt(400, 130))));      // 恰好等于两个范围
        Assert.True(Fires(p, owner, EnemyHost(HeroAt(-200, -30))));     // 在左边与上边,取绝对值
        Assert.False(Fires(p, owner, EnemyHost(HeroAt(401, 130))));     // 横向多 1
        Assert.False(Fires(p, owner, EnemyHost(HeroAt(400, 131))));     // 纵深多 1
        Assert.False(Fires(p, owner, EnemyHost(HeroAt(-201, 50))));
    }

    [Fact]
    public void CheckDistanceComparesPositionsAsIntegers()
    {
        // 客户端的位置读出来是整数:300.9 截成 300
        var owner = new Entity { X = 0, Y = 0 };

        Assert.True(Fires(CondProgram(L("[CHECK DISTANCE]", 300, 10)), owner, EnemyHost(HeroAt(300.9f, 9.9f))));
    }

    [Fact]
    public void CheckDistanceTruncatesTheOwnersPositionToo()
    {
        // 拥有者在 10.6:按整数是 10;敌人在 -10:横向差 20,恰好在范围里(拥有者四舍五入成 11 就是 21,出范围)
        var owner = new Entity { X = 10.6f, Y = 0 };

        Assert.True(Fires(CondProgram(L("[CHECK DISTANCE]", 20, 10)), owner, EnemyHost(HeroAt(-10, 0))));
    }

    [Fact]
    public void CheckDistanceDoesNotTrustTheHostToDropTheDead()
    {
        // 客户端的判定函数自己排除死亡状态的对象,所以运行器也不能只靠宿主给的列表
        var owner = new Entity { X = 0, Y = 0 };
        var dead = HeroAt(10, 10);
        dead.IsAlive = false;
        Host host = EnemyHost(dead);
        host.ListDeadToo = true;

        Assert.False(Fires(CondProgram(L("[CHECK DISTANCE]", 300, 80)), owner, host));
    }

    [Fact]
    public void CheckDistanceNeedsALivingCharacter()
    {
        var owner = new Entity { X = 0, Y = 0 };
        ActProgram p = CondProgram(L("[CHECK DISTANCE]", 300, 80));

        var dead = HeroAt(10, 10);
        dead.IsAlive = false;
        Assert.False(Fires(p, owner, EnemyHost(dead)));

        var monster = new Entity { Kind = ActEntityKind.Monster, X = 10, Y = 10 };
        Assert.False(Fires(p, owner, EnemyHost(monster)));

        Assert.False(Fires(p, owner, EnemyHost()));
    }

    [Fact]
    public void CheckDistanceAnyOneOfSeveralEnemiesIsEnough()
    {
        var owner = new Entity { X = 0, Y = 0 };

        Assert.True(Fires(CondProgram(L("[CHECK DISTANCE]", 100, 100)), owner, EnemyHost(HeroAt(900, 0), HeroAt(50, 50))));
    }

    [Fact]
    public void CheckDistanceWorksForAPassiveObjectOwnerToo()
    {
        // 客户端把拥有者转成的是碰撞对象类(0x010d44fd,描述符 0x019d91e8),被动对象与怪物一样转得过去(E-166 更正 E-156:之前误读成"生物类")
        var owner = new Entity { Kind = ActEntityKind.Passive, X = 0, Y = 0 };
        ActProgram p = CondProgram(L("[CHECK DISTANCE]", 100, 100));

        Assert.True(Fires(p, owner, EnemyHost(HeroAt(50, 50))));
        Assert.False(Fires(p, owner, EnemyHost(HeroAt(101, 50))));      // 范围照样生效
    }

    [Fact]
    public void CheckDistanceDoesNotChangeTheWorkingSet()
    {
        var owner = new Entity { X = 0, Y = 0 };
        var a = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 0 };
        var b = new Entity { Kind = ActEntityKind.Character, X = 20, Y = 0 };
        ActProgram p = GetTargetProgram(L("[CHECK DISTANCE]", 15, 10));

        // [WHICH] [ALL ENEMY] 得到 a、b 两个,[CHECK DISTANCE] 通过,工作集还是 a、b
        Assert.Equal(new[] { At(a), At(b) }, RunGetTarget(p, owner, a, b));
    }

    // ------------------------------------------------------------------ [IS GRABABLE]

    [Fact]
    public void IsGrabbableReadsOneNumber()
    {
        ActTrigger t = Assert.Single(CondProgram(L("[IS GRABABLE]", 1)).Triggers);
        Assert.True(t.Supported);
        Assert.Equal(new[] { ActRecordKind.IsGrabbable, ActRecordKind.DoBehavior }, t.Records.Select(r => r.Kind).ToArray());
        Assert.Equal(1, t.Records[0].Number);

        ActTrigger missing = Assert.Single(CondProgram(L("[IS GRABABLE]")).Triggers);
        Assert.False(missing.Supported);
        Assert.Contains("[IS GRABABLE]", missing.UnsupportedTags);
    }

    [Fact]
    public void IsGrabbableOneKeepsOnlyGrabbableCreatures()
    {
        var yes = new Entity { Kind = ActEntityKind.Character, X = 1 };
        var no = new Entity { Kind = ActEntityKind.Character, X = 2, Grabbable = false };
        var monster = new Entity { Kind = ActEntityKind.Monster, X = 3 };
        var passive = new Entity { Kind = ActEntityKind.Passive, X = 4 };

        Assert.Equal(new[] { At(yes), At(monster) }, RunGetTarget(GetTargetProgram(L("[IS GRABABLE]", 1)), new Entity(), yes, no, monster, passive));
    }

    [Fact]
    public void IsGrabbableZeroKeepsUngrabbableCreaturesAndPassiveObjectsAlike()
    {
        // 被动对象走碰撞对象基类的虚槽 0x380,恒返回假(0x00d26320),不看对象自己的"能不能抓"属性:参数不是 1 时留下(E-166 更正 E-156)
        var yes = new Entity { Kind = ActEntityKind.Character, X = 1 };
        var no = new Entity { Kind = ActEntityKind.Character, X = 2, Grabbable = false };
        var passive = new Entity { Kind = ActEntityKind.Passive, X = 3 };
        var passiveYes = new Entity { Kind = ActEntityKind.Passive, X = 4, Grabbable = true };

        Assert.Equal(new[] { At(no), At(passive), At(passiveYes) }, RunGetTarget(GetTargetProgram(L("[IS GRABABLE]", 0)), new Entity(), yes, no, passive, passiveYes));
    }

    [Fact]
    public void IsGrabbableAnyNumberOtherThanOneMeansUngrabbable()
    {
        // 客户端把参数比成 (参数 == 1):2 与 0 一样留下"不能抓"的
        var yes = new Entity { Kind = ActEntityKind.Character, X = 1 };
        var no = new Entity { Kind = ActEntityKind.Character, X = 2, Grabbable = false };

        Assert.Equal(new[] { At(no) }, RunGetTarget(GetTargetProgram(L("[IS GRABABLE]", 2)), new Entity(), yes, no));
    }

    [Fact]
    public void IsGrabbableDefaultsToAliveForEntitiesWithoutAStatusModel()
    {
        var dead = new Entity { Kind = ActEntityKind.Monster, X = 1, IsAlive = false };

        Assert.False(dead.IsGrabbable);
        Assert.True(new Entity().IsGrabbable);
    }

    // ------------------------------------------------------------------ [TRIGGER CHECK] 编译

    private static ActRecord OneRecord(params ScriptLine[] condition)
    {
        ActTrigger t = Assert.Single(CondProgram(condition).Triggers);
        Assert.True(t.Supported);
        Assert.Equal(ActRecordKind.DoBehavior, t.Records[^1].Kind);
        return Assert.Single(t.Records, r => r.Kind == ActRecordKind.TriggerCheck);
    }

    [Fact]
    public void TriggerCheckWithoutAKindMeansTheActItself()
    {
        ActRecord on = OneRecord(L("[TRIGGER CHECK]", 3), L("[ON]"));
        Assert.Equal(ActTriggerPool.Own, on.TriggerPool);
        Assert.Equal(3, on.Number);
        Assert.True(on.Flag);
        Assert.Equal(-1, on.TriggerSlot);

        Assert.False(OneRecord(L("[TRIGGER CHECK]", 0), L("[OFF]")).Flag);
    }

    [Fact]
    public void TriggerCheckReadsEveryKindWithItsActionNumberWhereOneIsWritten()
    {
        ActRecord attack = OneRecord(L("[TRIGGER CHECK]"), L("[ATTACK]", 2, 5), L("[OFF]"));
        Assert.Equal(ActTriggerPool.Attack, attack.TriggerPool);
        Assert.Equal(2, attack.TriggerSlot);
        Assert.Equal(5, attack.Number);
        Assert.False(attack.Flag);

        ActRecord custom = OneRecord(L("[TRIGGER CHECK]"), L("[CUSTOM]", 1, 4), L("[ON]"));
        Assert.Equal(ActTriggerPool.Custom, custom.TriggerPool);
        Assert.Equal(1, custom.TriggerSlot);
        Assert.Equal(4, custom.Number);
        Assert.True(custom.Flag);

        (string tag, ActTriggerPool pool)[] single =
        {
            ("[MOVE]", ActTriggerPool.Move), ("[STAY]", ActTriggerPool.Stay), ("[SIT]", ActTriggerPool.Sit),
            ("[DOWN]", ActTriggerPool.Down), ("[PROC]", ActTriggerPool.Proc),
        };
        foreach ((string tag, ActTriggerPool pool) in single)
        {
            ActRecord r = OneRecord(L("[TRIGGER CHECK]"), L(tag, 7), L("[ON]"));
            Assert.Equal(pool, r.TriggerPool);
            Assert.Equal(-1, r.TriggerSlot);
            Assert.Equal(7, r.Number);
        }
    }

    [Fact]
    public void TriggerCheckDoesNotAcceptDefaultOrAMissingSwitch()
    {
        // 加载块的种类表里没有 [DEFAULT](与 [TRIGGER] 行为不同),客户端载入报错
        Assert.False(Assert.Single(CondProgram(L("[TRIGGER CHECK]"), L("[DEFAULT]", 1), L("[ON]")).Triggers).Supported);
        // 没有 [ON]/[OFF],或写了 [RESET]
        Assert.False(Assert.Single(CondProgram(L("[TRIGGER CHECK]"), L("[ATTACK]", 1, 2)).Triggers).Supported);
        Assert.False(Assert.Single(CondProgram(L("[TRIGGER CHECK]"), L("[ATTACK]", 1, 2), L("[RESET]")).Triggers).Supported);
        // [ATTACK] 缺动作序号或触发器序号
        Assert.False(Assert.Single(CondProgram(L("[TRIGGER CHECK]"), L("[ATTACK]", 1), L("[ON]")).Triggers).Supported);
        // 种类不认得
        Assert.False(Assert.Single(CondProgram(L("[TRIGGER CHECK]"), L("[NOPE]", 1), L("[ON]")).Triggers).Supported);
    }

    [Fact]
    public void TriggerCheckFollowedByAnotherTriggerCheckReadsBoth()
    {
        ActTrigger t = Assert.Single(CondProgram(
            L("[TRIGGER CHECK]"), L("[ATTACK]", 1, 2), L("[OFF]"),
            L("[TRIGGER CHECK]"), L("[ATTACK]", 1, 3), L("[ON]")).Triggers);

        Assert.True(t.Supported);
        Assert.Equal(2, t.Records.Count(r => r.Kind == ActRecordKind.TriggerCheck));
    }

    // ------------------------------------------------------------------ [TRIGGER CHECK] 运行

    /// <summary>两个触发器:0 号带条件(并设速度 4),1 号自己的开关由 [ENABLE] 初值决定。</summary>
    private static ActProgram OwnCheckProgram(bool secondEnabled, string onOff)
    {
        var second = new List<ScriptLine> { L("[TRIGGER]"), L("[ENABLE]") };
        second.Add(L(secondEnabled ? "[ON]" : "[OFF]"));
        second.Add(L("[FRAME]", 99, 99));
        second.Add(L("[/TRIGGER]"));
        return P(Cat(
            Trigger(L("[TRIGGER CHECK]", 1), L(onOff), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            second.ToArray(),
            Behavior(Speed(4))));
    }

    [Fact]
    public void TriggerCheckOnTheActItselfIsAWholeTriggerTruthValue()
    {
        Assert.True(Fires(OwnCheckProgram(true, "[ON]"), new Entity()));
        Assert.False(Fires(OwnCheckProgram(false, "[ON]"), new Entity()));
        Assert.True(Fires(OwnCheckProgram(false, "[OFF]"), new Entity()));
        Assert.False(Fires(OwnCheckProgram(true, "[OFF]"), new Entity()));
    }

    [Fact]
    public void TriggerCheckOnTheActItselfFailsWhenTheIndexIsOutOfRange()
    {
        ActProgram p = CondProgram(L("[TRIGGER CHECK]", 9), L("[OFF]"));

        Assert.False(Fires(p, new Entity()));
    }

    [Fact]
    public void TriggerCheckOnTheActItselfFailsWhenTheIndexEqualsTheTriggerCount()
    {
        // 这个程序只有一个触发器:序号 1 正好越界一格(客户端判的是"个数不大于序号")
        ActProgram p = CondProgram(L("[TRIGGER CHECK]", 1), L("[ON]"));

        Assert.False(Fires(p, new Entity()));
        Assert.False(Fires(CondProgram(L("[TRIGGER CHECK]", 1), L("[OFF]")), new Entity()));
    }

    [Fact]
    public void TriggerCheckOnTheActItselfFollowsALaterSwitchWrite()
    {
        // 触发器 0 有 [FRAME] 0..0 和 [TRIGGER CHECK] 1 [ON];触发器 1 初值关;第 0 帧后另一个动作把 1 号打开
        ActProgram p = P(Cat(
            Trigger(L("[TRIGGER CHECK]", 1), L("[ON]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ENABLE]"), L("[OFF]"), L("[FRAME]", 99, 99)),
            Behavior(Speed(4))));
        var owner = new Entity();
        var runner = new ActRunner(p, owner, new Host());

        runner.Update(0, 0.016f);
        Assert.DoesNotContain("speed:4,-,-", owner.Log);

        runner.ApplyTrigger(1, ActTriggerCommand.On);
        runner.Update(0, 0.016f);
        Assert.Contains("speed:4,-,-", owner.Log);
    }

    [Fact]
    public void TriggerCheckOnAnActionFamilyFiltersTheWorkingSetByTheirSwitches()
    {
        var on = new Entity { X = 1 };
        on.ActionTriggerStates[(ActTriggerPool.Attack, 2, 5)] = true;
        var off = new Entity { X = 2 };
        off.ActionTriggerStates[(ActTriggerPool.Attack, 2, 5)] = false;
        var none = new Entity { X = 3 };                                // 没有这一族动作
        var wrongSlot = new Entity { X = 4 };
        wrongSlot.ActionTriggerStates[(ActTriggerPool.Attack, 1, 5)] = true;

        ActProgram wantOn = GetTargetProgram(L("[TRIGGER CHECK]"), L("[ATTACK]", 2, 5), L("[ON]"));
        ActProgram wantOff = GetTargetProgram(L("[TRIGGER CHECK]"), L("[ATTACK]", 2, 5), L("[OFF]"));

        Assert.Equal(new[] { At(on) }, RunGetTarget(wantOn, new Entity(), on, off, none, wrongSlot));
        Assert.Equal(new[] { At(off) }, RunGetTarget(wantOff, new Entity(), on, off, none, wrongSlot));
    }

    [Fact]
    public void TriggerCheckOnAnActionFamilyEmptiesTheSetWhenNobodyHasThatAction()
    {
        var a = new Entity { X = 1 };
        var b = new Entity { X = 2 };

        Assert.Empty(RunGetTarget(GetTargetProgram(L("[TRIGGER CHECK]"), L("[MOVE]", 0), L("[ON]")), new Entity(), a, b));
    }

    [Fact]
    public void TriggerCheckWithACountCheckLetsAnEmptiedSetPass()
    {
        // 与 [CHECKED NO] 配合(全库里最常见的写法):没有对象满足时个数为 0,[CHECKED NO] [=] 0 通过并触发
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ME]"), L("[TRIGGER CHECK]"), L("[CUSTOM]", 0, 1), L("[ON]"), L("[CHECKED NO]"), L("[=]", 0),
                L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(4))));

        Assert.True(Fires(p, new Entity()));
    }
}
