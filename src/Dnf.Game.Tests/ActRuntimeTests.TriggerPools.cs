using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [TRIGGER] 按动作族写的开关(M52,证据 E-145):[ATTACK] 序号 触发器序号 [ON]/[OFF]/[RESET] 与 [MOVE]、[STAY]、[SIT]、[DOWN]、[DEFAULT]、[PROC]、[CUSTOM] 的写法。
/// 客户端的加载分支按种类记一个池号,[ATTACK]、[CUSTOM] 先读动作序号;执行块只看行为拥有者,从拥有者取那一族动作的触发器池,序号不比池里的个数小就跳过。
/// 运行器不认识这些容器,交给拥有者落实;没写种类的老写法仍是当前 .act 自己。脚本内容全是合成的,数是测试用的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    // ------------------------------------------------------------------ 编译

    [Fact]
    public void ASwitchWithoutAKindStaysOnTheActItself()
    {
        ActBehavior b = Compiled(L("[TRIGGER]", 3), L("[ON]"));
        Assert.Equal(ActBehaviorKind.TriggerSwitch, b.Kind);
        Assert.Equal((ActTriggerPool.Own, -1, 3, ActTriggerCommand.On), (b.TriggerPool, b.TriggerSlot, b.TriggerIndex, b.Command));
    }

    [Fact]
    public void AttackAndCustomTakeTheActionNumberBeforeTheTriggerNumber()
    {
        ActBehavior attack = Compiled(L("[TRIGGER]"), L("[ATTACK]", 2, 1), L("[OFF]"));
        Assert.Equal(ActBehaviorKind.TriggerSwitch, attack.Kind);
        Assert.Equal((ActTriggerPool.Attack, 2, 1, ActTriggerCommand.Off), (attack.TriggerPool, attack.TriggerSlot, attack.TriggerIndex, attack.Command));

        ActBehavior custom = Compiled(L("[TRIGGER]"), L("[CUSTOM]", 0, 4), L("[RESET]"));
        Assert.Equal((ActTriggerPool.Custom, 0, 4, ActTriggerCommand.Reset), (custom.TriggerPool, custom.TriggerSlot, custom.TriggerIndex, custom.Command));
    }

    [Fact]
    public void TheOtherKindsHaveOneActionEachSoTheyTakeOnlyTheTriggerNumber()
    {
        var kinds = new[]
        {
            ("[MOVE]", ActTriggerPool.Move), ("[STAY]", ActTriggerPool.Stay), ("[SIT]", ActTriggerPool.Sit),
            ("[DOWN]", ActTriggerPool.Down), ("[DEFAULT]", ActTriggerPool.Default), ("[PROC]", ActTriggerPool.Proc),
        };
        foreach ((string tag, ActTriggerPool pool) in kinds)
        {
            ActBehavior b = Compiled(L("[TRIGGER]"), L(tag, 5), L("[ON]"));
            Assert.Equal(ActBehaviorKind.TriggerSwitch, b.Kind);
            Assert.Equal((pool, -1, 5, ActTriggerCommand.On), (b.TriggerPool, b.TriggerSlot, b.TriggerIndex, b.Command));
        }
    }

    [Fact]
    public void ASwitchThatIsCutShortOrNamesAnUnknownKindIsUnsupportedAndDoesNotSwallowTheNextBehavior()
    {
        // 缺动作序号、缺触发器序号、种类认不出来、[DAMAGE](客户端加载与执行的整数个数对不上)、命令认不出来:都是读不懂
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]"), L("[ATTACK]"), L("[OFF]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]"), L("[ATTACK]", 1), L("[OFF]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]"), L("[MOVE]"), L("[OFF]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]"), L("[NOT A KIND]", 1), L("[OFF]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]"), L("[DAMAGE]", 1), L("[OFF]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]", 1), L("[SIDEWAYS]")).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[TRIGGER]")).Kind);

        // 写完整的行后面紧跟着别的行为:开关行不吞它
        ActProgram two = P(Me(Cat(new[] { L("[TRIGGER]"), L("[ATTACK]", 1, 0), L("[ON]") }, new[] { L("[SET HP]", 9) })));
        Assert.Equal(new[] { ActBehaviorKind.TriggerSwitch, ActBehaviorKind.SetHp }, two.Behaviors[0].Items.Select(i => i.Kind).ToArray());
    }

    // ------------------------------------------------------------------ 执行

    [Fact]
    public void ASwitchOnAnotherActionFamilyGoesToTheOwnerExactlyOnceWhateverTheTargetSetIs()
    {
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character };
        var b = new Entity { Kind = ActEntityKind.Monster };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };

        var runner = new ActRunner(P(OnEnemies(L("[TRIGGER]"), L("[ATTACK]", 1, 2), L("[OFF]"))), owner, host);
        runner.Update(0, 0.016f);

        // 客户端的执行块只看拥有者,不循环目标;被作用的对象一个也没动
        Assert.Equal(new[] { (ActTriggerPool.Attack, 1, 2, ActTriggerCommand.Off) }, owner.ActionTriggers.ToArray());
        Assert.Empty(a.ActionTriggers);
        Assert.Empty(b.ActionTriggers);
        Assert.True(runner.IsEnabled(0));        // 这一招自己的触发器开关不受影响
    }

    [Fact]
    public void EveryKindIsHandedToTheOwnerWithItsSlotAndCommand()
    {
        var owner = new Entity();
        ActProgram p = P(Me(Cat(
            new[] { L("[TRIGGER]"), L("[MOVE]", 0), L("[OFF]") },
            new[] { L("[TRIGGER]"), L("[CUSTOM]", 3, 1), L("[ON]") },
            new[] { L("[TRIGGER]"), L("[ATTACK]", 0, 0), L("[RESET]") })));
        Run(p, owner);
        Assert.Equal(new[]
        {
            (ActTriggerPool.Move, -1, 0, ActTriggerCommand.Off),
            (ActTriggerPool.Custom, 3, 1, ActTriggerCommand.On),
            (ActTriggerPool.Attack, 0, 0, ActTriggerCommand.Reset),
        }, owner.ActionTriggers.ToArray());
    }

    [Fact]
    public void TheOwnFormStillWritesTheRunnersOwnSwitchesAndNeverCallsTheOwner()
    {
        var owner = new Entity();
        ActProgram p = P(Cat(
            Trigger(L("[ENABLE]"), L("[OFF]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[TRIGGER]", 0), L("[ON]")),
            Behavior(Speed(5))));
        var runner = new ActRunner(p, owner, new Host());
        runner.Update(0, 0.016f);
        Assert.True(runner.IsEnabled(0));
        Assert.Empty(owner.ActionTriggers);
    }

    [Fact]
    public void ApplyTriggerIsTheSameWriteTheOwnFormDoesAndResetRearmsCountsAndOneShot()
    {
        ActProgram p = P(Cat(
            Trigger(L("[LIMIT]", 1), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        runner.Update(0, 0.016f);
        Assert.Equal(1, runner.FiredCount(0));

        runner.ApplyTrigger(0, ActTriggerCommand.Off);
        Assert.False(runner.IsEnabled(0));
        runner.ApplyTrigger(0, ActTriggerCommand.On);
        Assert.True(runner.IsEnabled(0));

        runner.ApplyTrigger(0, ActTriggerCommand.Reset);
        Assert.Equal(0, runner.FiredCount(0));
        runner.ApplyTrigger(7, ActTriggerCommand.Reset);         // 越界的序号什么也不做
        runner.ApplyTrigger(-1, ActTriggerCommand.Off);
        Assert.True(runner.IsEnabled(0));
    }
}
