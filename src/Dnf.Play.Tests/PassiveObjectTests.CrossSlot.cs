using Dnf.Game.Ai;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 按动作族写的 [TRIGGER](M52,证据 E-145):第 0 招的脚本去改别的招的触发器开关。开关属于怪物的招式条目(每招一份、出招之间保留,E-134),
/// 所以改完之后再出那一招,被打开的触发器照常触发、被关掉的不再触发,.ai 的 is attack action enable 读到的也是改过的值。
/// 怪物与 .act 都是合成的:xoff / xon / xonctl / xself / xselfctl / xmiss(见 <see cref="AvatarFixture"/> 的 ActMobs)。
/// </summary>
public partial class PassiveObjectTests
{
    [Fact]
    public void AnAttackCanSwitchOffAnotherAttacksTrigger()
    {
        using var arena = new ActArena("xoff", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(true, m.ActTriggerState(1, 0));                // 出生:第 1 招的触发器 [ENABLE] 初值(没写算开)
        Assert.Equal(true, m.ActTriggerState(0, 0));

        Assert.True(arena.Until(() => m.ActTriggerState(1, 0) == false) > 0);
        Assert.Equal(true, m.ActTriggerState(0, 0));                // 自己这一招的开关没动
        Assert.Equal(1, m.ActSwitchApplied);
        Assert.Equal((0, 0), (m.ActSwitchMissed, m.ActSwitchUnhosted));

        arena.Steps(300);                                           // 之后再出几次招:被关掉的开关保持关着(跨出招保留)
        Assert.Equal(false, m.ActTriggerState(1, 0));
    }

    [Fact]
    public void AnAttackCanSwitchOnAnotherAttacksTriggerAndThatAttackThenFiresIt()
    {
        using var arena = new ActArena("xon", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(false, m.ActTriggerState(1, 0));               // 出生:[ENABLE] [OFF]
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);     // 第 0 招帧 1 先开第 1 招的触发器,再切过去
        Assert.Equal(true, m.ActTriggerState(1, 0));
        Assert.Equal(0, arena.Session.PassivesSpawned);

        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);      // 第 1 招自己的帧 1:被打开的触发器放出被动对象
        Assert.Equal(1, m.ActSwitchApplied);
    }

    [Fact]
    public void WithoutTheSwitchOnTheSecondAttacksOffTriggerNeverFires()
    {
        using var arena = new ActArena("xonctl", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);     // 对照:同样切到第 1 招,但没人打开它的触发器
        arena.Steps(150);
        Assert.Equal(false, m.ActTriggerState(1, 0));
        Assert.Equal(0, arena.Session.PassivesSpawned);
        Assert.Equal(0, m.ActSwitchApplied);
    }

    [Fact]
    public void NamingTheRunningAttackWithTheKindFormWorksLikeTheOwnForm()
    {
        using var arena = new ActArena("xself", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(false, m.ActTriggerState(0, 1));               // 触发器 1 出生时关着
        Assert.True(arena.Until(() => m.ActTriggerState(0, 1) == true) > 0);        // 帧 1:[ATTACK] 0 1 [ON] 点名自己这一招
        Assert.Equal(1, m.ActSwitchApplied);
        Assert.NotNull(m.CurrentActRunner);
        Assert.True(m.CurrentActRunner.IsEnabled(1));               // 正在跑的运行器与怪物共用同一份开关
        Assert.Equal(0, arena.Session.PassivesSpawned);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);      // 帧 3:刚被打开的触发器触发
    }

    [Fact]
    public void ControlTheSameAttackWithoutTheSwitchNeverFiresItsOffTrigger()
    {
        using var arena = new ActArena("xselfctl", useVitals: false);
        arena.Steps(400);
        Assert.Equal(false, arena.Monster.ActTriggerState(0, 1));
        Assert.Equal(0, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void SwitchesAimedAtMissingAttacksOutOfRangeTriggersOrOtherFamiliesDoNothingAndAreCounted()
    {
        using var arena = new ActArena("xmiss", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.ActSwitchMissed > 0) > 0);
        Assert.Equal((0, 2, 1), (m.ActSwitchApplied, m.ActSwitchMissed, m.ActSwitchUnhosted));     // 没有第 5 招、第 0 招没有第 7 个触发器、[MOVE] 还没有宿主
        Assert.Equal(true, m.ActTriggerState(0, 0));                // 越界的开关没有误伤别的触发器
        Assert.Null(m.ActTriggerState(5, 0));
    }

    [Fact]
    public void TheAiScriptReadsTheSwitchesTheOtherAttackChanged()
    {
        using var arena = new ActArena("xoff", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.ActTriggerState(1, 0) == false) > 0);
        var script = new MonsterScript(_ => null, new string[MonsterScript.RoleCount], new AiScript[MonsterScript.RoleCount], null, 1, () => 0)
        {
            TriggerEnabled = (action, trigger) => true,             // .act 里 [ENABLE] 的初值:开
        };
        m.SetScript(script);
        Assert.Equal(false, script.LiveTrigger(1, 0));              // .ai 的 is attack action enable(1, 0) 读到第 0 招关掉的开关,盖过静态初值
        Assert.Equal(true, script.LiveTrigger(0, 0));
    }
}
