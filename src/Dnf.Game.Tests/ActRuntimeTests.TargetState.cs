using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>[IS TARGET STATE]、[IS NOT TARGET STATE](M72,E-172):对齐 0x107b2e0 与 0x010d91bd。</summary>
public sealed partial class ActRuntimeTests
{
    [Fact]
    public void TargetStateTagsCompileWithTheirIntegerLists()
    {
        ActProgram p = P(Cat(
            Trigger(L("[IS TARGET STATE]", 8, 3), L("[IS NOT TARGET STATE]", 0)),
            Behavior(Speed(1))));
        Assert.Equal(new[] { ActRecordKind.IsTargetState, ActRecordKind.IsNotTargetState }, p.Triggers[0].Records.Select(r => r.Kind).ToArray());
        Assert.Equal(new[] { 8, 3 }, p.Triggers[0].Records[0].Ints);
        Assert.Equal(new[] { 0 }, p.Triggers[0].Records[1].Ints);
    }

    [Fact]
    public void IsTargetStateKeepsOnlyMatchingAliveCreatures()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", 8));
        var hit = new Entity { Kind = ActEntityKind.Monster, ActAiState = 8 };
        var miss = new Entity { Kind = ActEntityKind.Monster, ActAiState = 0 };
        var passive = new Entity { Kind = ActEntityKind.Passive, ActAiState = 8 };
        Assert.Equal(new[] { true, false, false }, Kept(p, new Entity(), hit, miss, passive));
    }

    [Fact]
    public void IsNotTargetStateInvertsTheMatch()
    {
        ActProgram p = Filter(L("[IS NOT TARGET STATE]", 3));
        var a = new Entity { Kind = ActEntityKind.Character, ActAiState = 3 };
        var b = new Entity { Kind = ActEntityKind.Character, ActAiState = 0 };
        Assert.Equal(new[] { false, true }, Kept(p, new Entity(), a, b));
    }

    /// <summary>0x107b2e0:空向量读下标 0 得 -1;与 getState 比较(E-R005)。</summary>
    [Fact]
    public void EmptyParamListReadsMinusOneAndMatchesThatState()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]"));
        ActRecord r = p.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsTargetState);
        Assert.Empty(r.Ints);
        var hit = new Entity { Kind = ActEntityKind.Monster, ActAiState = -1 };
        var miss = new Entity { Kind = ActEntityKind.Monster, ActAiState = 0 };
        Assert.Equal(new[] { true, false }, Kept(p, new Entity(), hit, miss));
    }

    /// <summary>0x10d92c7:param=-1 且目标 Character +0x36b0≠-1 时无条件保留(与 getState 无关,E-R006)。</summary>
    [Fact]
    public void MinusOneWithCharacterFocusKeepsRegardlessOfEntityState()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", -1));
        var focused = new Entity { Kind = ActEntityKind.Character, ActTargetStateFocus = 8, ActAiState = 0 };
        Assert.Equal(new[] { true }, Kept(p, new Entity(), focused));
    }

    /// <summary>0x10d92d0:param=-1、目标 +0x36b0=-1 时只保留 getState==-1 的对象(E-R006)。</summary>
    [Fact]
    public void MinusOneWithCharacterFocusMinusOneUsesStateEquality()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", -1));
        var hit = new Entity { Kind = ActEntityKind.Monster, ActAiState = -1, ActTargetStateFocus = -1 };
        var miss = new Entity { Kind = ActEntityKind.Monster, ActAiState = 8, ActTargetStateFocus = -1 };
        Assert.Equal(new[] { true, false }, Kept(p, new Entity(), hit, miss));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(8)]
    public void MinusOneParamKeepsCharacterWhenTargetFocusIsNotMinusOne(int focus)
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", -1));
        var c = new Entity { Kind = ActEntityKind.Character, ActTargetStateFocus = focus, ActAiState = 99 };
        Assert.Equal(new[] { true }, Kept(p, new Entity(), c));
    }

    /// <summary>末行 return match: param=-1、Focus=-1、IS TARGET STATE 比 state==-1。</summary>
    [Fact]
    public void MinusOneFocusMinusOneIsTargetStateUsesFinalMatchLine()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", -1));
        var hit = new Entity { Kind = ActEntityKind.Monster, ActAiState = -1 };
        var miss = new Entity { Kind = ActEntityKind.Monster, ActAiState = 0 };
        Assert.Equal(new[] { true, false }, Kept(p, new Entity(), hit, miss));
    }

    /// <summary>末行 return match: param=-1、Focus=-1、IS NOT TARGET STATE 比 state!=-1。</summary>
    [Fact]
    public void MinusOneFocusMinusOneIsNotTargetStateUsesFinalMatchLine()
    {
        ActProgram p = Filter(L("[IS NOT TARGET STATE]", -1));
        var hit = new Entity { Kind = ActEntityKind.Monster, ActAiState = 0 };
        var miss = new Entity { Kind = ActEntityKind.Monster, ActAiState = -1 };
        Assert.Equal(new[] { true, false }, Kept(p, new Entity(), hit, miss));
    }

    /// <summary>0x010d9203:PassiveObject 作动作拥有者时 cast 失败,目标全剔除。</summary>
    [Fact]
    public void PassiveActOwnerDropsAllTargetStateCandidates()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", 8));
        var owner = new Entity { Kind = ActEntityKind.Passive };
        var hit = new Entity { Kind = ActEntityKind.Monster, ActAiState = 8 };
        Assert.Equal(new[] { false }, Kept(p, owner, hit));
    }

    /// <summary>0x010d91bd 无存活闸;ListDeadToo 把死者放进工作集后仍过 IS TARGET STATE(SET SPEED 会跳过死者,用 DESTROY 观测)。</summary>
    [Fact]
    public void TargetStateFilterKeepsDeadWhenStateMatches()
    {
        ActProgram p = P(Cat(
            Trigger(Cat(
                new[] { L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKUP]") },
                new[] { L("[IS TARGET STATE]", 3) },
                new[] { L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0) })),
            Behavior(L("[DESTROY]"))));
        var host = new Host { ListDeadToo = true };
        var dead = new Entity { Kind = ActEntityKind.Monster, ActAiState = 3, IsAlive = false };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { dead };
        new ActRunner(p, new Entity(), host).Update(0, 0.016f);
        Assert.Single(dead.Log);
    }

    /// <summary>ReadActIntParam 越界必须 -1;若改成 0 则空表会错误匹配 state 0(E-R005)。</summary>
    [Fact]
    public void EmptyParamNeverMatchesStateZero()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]"));
        var zero = new Entity { Kind = ActEntityKind.Monster, ActAiState = 0 };
        Assert.Equal(new[] { false }, Kept(p, new Entity(), zero));
    }

    [Fact]
    public void MonsterTargetWithFocusDoesNotUseFocusBranch()
    {
        ActProgram p = Filter(L("[IS TARGET STATE]", -1));
        var m = new Entity { Kind = ActEntityKind.Monster, ActTargetStateFocus = 8, ActAiState = 0 };
        Assert.Equal(new[] { false }, Kept(p, new Entity(), m));
    }
}
