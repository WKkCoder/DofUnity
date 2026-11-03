using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [CASTING](M69b,证据 E-168):编译(两个值表达式、读不全的写法)与运行器执行(单槽读条、到期在更新末尾跑指定行为组、只作用在排上时的目标集合、
/// 随动作重来作废、组号越界不算执行)。客户端执行块在 0x010bd088(行为码 0x7e),到期的发送在每次更新的末尾;读条条的画法在 UI 一侧,这里不做。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>第 0 帧让拥有者自己跑第 0 组(<paramref name="first"/>);其后依次是 <paramref name="rest"/> 里的行为组。</summary>
    private static ActProgram CastProgram(ScriptLine[] first, params ScriptLine[][] rest)
    {
        var parts = new List<ScriptLine[]>
        {
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(first),
        };
        parts.AddRange(rest.Select(group => Behavior(group)));
        return P(Cat(parts.ToArray()));
    }

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void CastingCompilesToItsTimeAndItsGroupNumber()
    {
        ActProgram p = P(Behavior(L("[CASTING]", 700, 2)));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.Casting, b.Kind);
        Assert.True(b.CastMs.IsConstant);
        Assert.Equal(700, b.CastMs.First);
        Assert.Equal(2, b.CastGroup.First);
        Assert.Empty(p.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void CastingValuesMayBeRandomExpressions()
    {
        ActProgram p = P(Behavior(L("[CASTING]"), L("[RANDOM]", 100, 200), L("[RANDOM SELECT]", 1, 2), L("[/RANDOM SELECT]")));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.Casting, b.Kind);
        Assert.False(b.CastMs.IsConstant);
        Assert.False(b.CastGroup.IsConstant);
        Assert.Equal(new[] { 1, 2 }, b.CastGroup.Candidates().OrderBy(x => x).ToArray());
        Assert.Empty(p.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void CastingWithoutBothValuesIsLeftUndoneAndItsTagIsRecorded()
    {
        ActProgram none = P(Behavior(L("[CASTING]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(none).Kind);
        Assert.Contains("[CASTING]", none.Behaviors[0].UnsupportedTags);

        ActProgram one = P(Behavior(L("[CASTING]", 500)));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(one).Kind);
        Assert.Contains("[CASTING]", one.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void CastingSitsBesideItsNeighboursInTheSameGroup()
    {
        ActProgram p = P(Behavior(Cat(Speed(5), new[] { L("[CASTING]", 100, 1) }, Speed(7))));
        ActBehaviorGroup group = Assert.Single(p.Behaviors);
        Assert.Equal(new[] { ActBehaviorKind.SetSpeed, ActBehaviorKind.Casting, ActBehaviorKind.SetSpeed }, group.Items.Select(i => i.Kind).ToArray());
        Assert.Empty(group.UnsupportedTags);
    }

    // ------------------------------------------------------------------ 运行

    [Fact]
    public void ACastRunsItsGroupWhenItsTimeIsUpAndNotBefore()
    {
        ActProgram p = CastProgram(new[] { L("[CASTING]", 700, 1) }, new[] { L("[SET FRAME]", 4) });
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0f);                                    // 第 0 帧:排上 700 毫秒的读条
        (IActEntity owner, int ms, int group) armed = Assert.Single(host.CastsArmed);
        Assert.Same(e, armed.owner);
        Assert.Equal((700, 1), (armed.ms, armed.group));
        Assert.Empty(host.CastsFired);
        Assert.Empty(e.Log);

        runner.Update(0, 0.69f);                                 // 690 毫秒:还没到
        Assert.Empty(e.Log);
        Assert.Empty(host.CastsFired);

        runner.Update(0, 0.02f);                                 // 710 毫秒:到期,第 1 组跑起来
        Assert.Equal(new[] { "frame:4" }, e.Log.ToArray());
        (IActEntity owner, int group) fired = Assert.Single(host.CastsFired);
        Assert.Same(e, fired.owner);
        Assert.Equal(1, fired.group);
    }

    [Fact]
    public void ACastFiresOnTheExactDueTime()
    {
        ActProgram p = CastProgram(new[] { L("[CASTING]", 700, 1) }, new[] { L("[SET FRAME]", 4) });
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        runner.Update(0, 0f);
        runner.Update(0, 0.7f);                                  // 正好 700 毫秒:到点就跑(≥,不是 >)
        Assert.Equal(new[] { "frame:4" }, e.Log.ToArray());
    }

    [Fact]
    public void ACastGroupRunsOnlyOnce()
    {
        ActProgram p = CastProgram(new[] { L("[CASTING]", 200, 1) }, new[] { L("[SET FRAME]", 4) });
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0f);
        runner.Update(0, 0.25f);
        runner.Update(0, 1f);
        runner.Update(0, 1f);
        Assert.Equal(new[] { "frame:4" }, e.Log.ToArray());
        Assert.Single(host.CastsFired);
        Assert.Single(host.CastsArmed);
    }

    [Fact]
    public void AZeroLengthCastRunsAtTheEndOfTheSameUpdateAfterTheTriggers()
    {
        // 读条不为负就排(0 也排),到期的检查在整个更新的末尾:同一次更新里,别的触发器的行为先于读条到期的行为组
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 2)),
            Behavior(L("[CASTING]", 0, 1)),
            Behavior(L("[SET FRAME]", 4)),
            Behavior(L("[SET FRAME]", 7))));
        var e = new Entity();
        new ActRunner(p, e, new Host()).Update(0, 0f);
        Assert.Equal(new[] { "frame:7", "frame:4" }, e.Log.ToArray());
    }

    [Fact]
    public void ACastActsOnTheTargetSetOfTheMomentItWasArmed()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CASTING]", 300, 1)),
            Behavior(Cat(Speed(9), new[] { L("[SET FRAME]", 3) }))));
        var owner = new Entity();
        var stays = new Entity { Kind = ActEntityKind.Character };
        var dies = new Entity { Kind = ActEntityKind.Character };
        var late = new Entity { Kind = ActEntityKind.Character };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { stays, dies };
        var runner = new ActRunner(p, owner, host);

        runner.Update(0, 0f);                                    // 排上:目标集合是 stays 与 dies
        Assert.Single(host.CastsArmed);                          // 读条记在拥有者的运行器上,不按目标各排一条
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { dies, late };
        dies.IsAlive = false;                                    // 排上之后一个死了、舞台上换了一批
        runner.Update(0, 0.31f);

        Assert.Equal(new[] { "speed:9,-,-", "frame:3" }, stays.Log.ToArray());
        Assert.Empty(dies.Log);                                  // 到期时已经不在场的不再作用(拨帧这类行为自己不看死活,靠到期时剔除)
        Assert.Empty(late.Log);                                  // 排上之后才进集合的不算
        Assert.Empty(owner.Log);                                 // 作用的是排上时的目标集合,不是拥有者
    }

    [Fact]
    public void ACastOverANeverFilledTargetSetNeverFires()
    {
        // [CHECKED NO] [=] 0 让工作集为空的触发器照样触发:读条排上了(读条条照走),到期时目标列表是空的,不执行
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKED NO]"), L("[=]", 0), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CASTING]", 200, 1)),
            Behavior(L("[SET FRAME]", 4))));
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0f);
        Assert.Single(host.CastsArmed);
        runner.Update(0, 1f);
        Assert.Empty(host.CastsFired);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void ASecondCastReplacesTheFirst()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[CASTING]", 500, 2)),
            Behavior(L("[CASTING]", 200, 3)),
            Behavior(L("[SET FRAME]", 11)),
            Behavior(L("[SET FRAME]", 22))));
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0f);                                    // 排 500 毫秒、第 2 组
        runner.Update(1, 0.1f);                                  // 100 毫秒:再排 200 毫秒、第 3 组,盖掉前一条(到 300 毫秒)
        runner.Update(1, 0.15f);                                 // 250 毫秒
        Assert.Empty(e.Log);
        runner.Update(1, 0.06f);                                 // 310 毫秒:后一条到期
        Assert.Equal(new[] { "frame:22" }, e.Log.ToArray());
        runner.Update(1, 1f);                                    // 前一条再也不会跑
        Assert.Equal(new[] { "frame:22" }, e.Log.ToArray());
        Assert.Equal(2, host.CastsArmed.Count);
        Assert.Equal(3, Assert.Single(host.CastsFired).group);
    }

    [Fact]
    public void ACastGroupMayArmTheNextCastAndItIsNotClearedAfterwards()
    {
        // 连着读两次条(全库 13 处):到期的行为组里再排一条,先清后跑,所以新排的不会被清掉
        ActProgram p = CastProgram(
            new[] { L("[CASTING]", 300, 1) },
            new[] { L("[CASTING]", 300, 2) },
            new[] { L("[SET FRAME]", 9) });
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0f);
        runner.Update(0, 0.31f);                                 // 第一条到期,第 1 组排上第二条(到 610 毫秒)
        Assert.Empty(e.Log);
        Assert.Equal(2, host.CastsArmed.Count);
        runner.Update(0, 0.29f);                                 // 600 毫秒
        Assert.Empty(e.Log);
        runner.Update(0, 0.02f);                                 // 620 毫秒:第二条到期
        Assert.Equal(new[] { "frame:9" }, e.Log.ToArray());
        Assert.Equal(new[] { 1, 2 }, host.CastsFired.Select(f => f.group).ToArray());
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void ANegativeTimeOrAGroupNumberBelowOneArmsNothing(int ms, int group)
    {
        ActProgram p = CastProgram(new[] { L("[CASTING]", ms, group) }, new[] { L("[SET FRAME]", 4) });
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0f);
        runner.Update(0, 5f);
        Assert.Empty(host.CastsArmed);
        Assert.Empty(host.CastsFired);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void ANegativeTimeArmsNothingButStillReplacesTheTargetListOfAPendingCast()
    {
        // 客户端先改写目标列表、再检查参数:时间为负的写法不排新的,已经排着的那条还在,到期时按旧的组号、新换的目标列表执行
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 1, 1), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Behavior(L("[CASTING]", 400, 2)),
            Behavior(L("[CASTING]", -1, 2)),
            Behavior(Speed(6))));
        var host = new Host();
        var owner = new Entity();
        var enemy = new Entity { Kind = ActEntityKind.Character };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { enemy };
        var runner = new ActRunner(p, owner, host);

        runner.Update(0, 0f);                                    // 拥有者自己排上 400 毫秒、第 2 组
        runner.Update(1, 0.1f);                                  // 第二条写法(目标是 enemy)不排,只把目标列表换成 enemy
        Assert.Single(host.CastsArmed);
        runner.Update(1, 0.31f);                                 // 410 毫秒:第一条到期,作用在 enemy 身上
        Assert.Equal(new[] { "speed:6,-,-" }, enemy.Log.ToArray());
        Assert.Empty(owner.Log);
        Assert.Equal(2, Assert.Single(host.CastsFired).group);
    }

    [Fact]
    public void ABadGroupNumberDoesNothingWhenItsTimeIsUpAndDoesNotCountAsExecuted()
    {
        ActProgram p = CastProgram(new[] { L("[CASTING]", 100, 9) }, new[] { L("[SET FRAME]", 4) });
        var host = new Host();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0f);
        Assert.Single(host.CastsArmed);                          // 读条照排(读条条在走)
        runner.Update(0, 1f);
        Assert.Empty(host.CastsFired);                           // 找不到第 9 组:没有执行
        Assert.Empty(e.Log);
    }

    [Fact]
    public void AnActionRestartDropsAPendingCast()
    {
        ActProgram p = P(Cat(
            Trigger(L("[ON DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[CASTING]", 300, 1)),
            Behavior(L("[SET FRAME]", 5))));
        var attacker = new Entity { Kind = ActEntityKind.Character };

        // 对照:不重来,读条到期就跑
        var keepEntity = new Entity();
        var keep = new ActRunner(p, keepEntity, new Host());
        keep.Update(0, 0f);
        keep.OnDamage(attacker);
        keep.Update(0, 0.31f);
        Assert.Equal(new[] { "frame:5" }, keepEntity.Log.ToArray());

        // 排上之后动作重来:读条作废
        var dropEntity = new Entity();
        var host = new Host();
        var drop = new ActRunner(p, dropEntity, host);
        drop.Update(0, 0f);
        drop.OnDamage(attacker);
        Assert.Single(host.CastsArmed);
        drop.Begin();
        drop.Update(0, 1f);
        Assert.Empty(host.CastsFired);
        Assert.Empty(dropEntity.Log);
    }

    [Fact]
    public void ARandomCastDrawsItsTimeAndGroupOnceWhenArmedNotWhenItFires()
    {
        ActProgram p = CastProgram(
            new[] { L("[CASTING]"), L("[RANDOM]", 100, 200), L("[RANDOM SELECT]", 1, 2), L("[/RANDOM SELECT]") },
            new[] { L("[SET FRAME]", 1) },
            new[] { L("[SET FRAME]", 2) });
        var host = new Host();
        host.Randoms.Enqueue(30);                                // 时间:100 + 30 % 100 = 130 毫秒
        host.Randoms.Enqueue(1);                                 // 组号:候选 {1, 2} 里取下标 1
        var e = new Entity();
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0f);
        (IActEntity owner, int ms, int group) armed = Assert.Single(host.CastsArmed);
        Assert.Equal((130, 2), (armed.ms, armed.group));
        runner.Update(0, 0.12f);
        Assert.Empty(e.Log);
        runner.Update(0, 0.02f);                                 // 140 毫秒
        Assert.Equal(new[] { "frame:2" }, e.Log.ToArray());
        Assert.Equal(new[] { 100, 2 }, host.RandomBounds.ToArray());   // 只在排的时候抽了两次,到期时没有再抽
    }
}
