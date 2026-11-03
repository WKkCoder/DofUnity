using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [CHANGE ATTACKINFO] 与程序一级的 [DEFAULT ATTACKINFO](M69,证据 E-167):编译(路径、[DEFAULT] 子标签、读不全的写法、程序默认只取第一条)
/// 与运行器执行(只对行为拥有者、不看目标集合,路径原样交给宿主)。客户端执行块在 0x010b775e(行为码 0x67);怪物与被动对象怎么用换上的攻击信息见 Play 层的测试。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    [Fact]
    public void ChangeAttackInfoWithAPathCompilesToTheWrittenPath()
    {
        ActProgram p = P(Behavior(L("[CHANGE ATTACKINFO]", "../AttackInfo/x.atk")));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.ChangeAttackInfo, b.Kind);
        Assert.Equal("../AttackInfo/x.atk", b.FilePath);
        Assert.Empty(p.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void ChangeAttackInfoWithTheDefaultSubTagCompilesToANullPath()
    {
        // 全库唯一的 [DEFAULT] 写法:子标签在下一行
        ActProgram p = P(Behavior(L("[CHANGE ATTACKINFO]"), L("[DEFAULT]")));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.ChangeAttackInfo, b.Kind);
        Assert.Null(b.FilePath);
        Assert.Empty(p.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void ChangeAttackInfoWithNothingToChangeToIsLeftUndone()
    {
        // 没写参数、空串:没有东西可换,按没做处理并记下标签
        ActProgram none = P(Behavior(L("[CHANGE ATTACKINFO]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(none).Kind);
        Assert.Contains("[CHANGE ATTACKINFO]", none.Behaviors[0].UnsupportedTags);

        ActProgram empty = P(Behavior(L("[CHANGE ATTACKINFO]", "")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(empty).Kind);

        // 别的子标签不算 [DEFAULT]
        ActProgram other = P(Behavior(L("[CHANGE ATTACKINFO]"), L("[RANDOM]")));
        Assert.Equal(ActBehaviorKind.Unsupported, other.Behaviors[0].Items[0].Kind);
    }

    [Fact]
    public void SeveralAttackInfoRecordsAndTheirNeighboursCompileSideBySide()
    {
        ActProgram p = P(Behavior(Cat(
            Speed(5),
            new[] { L("[CHANGE ATTACKINFO]", "../a.atk") },
            new[] { L("[CHANGE ATTACKINFO]"), L("[DEFAULT]") },
            new[] { L("[CHANGE ATTACKINFO]", "../b.atk") },
            Speed(7))));
        ActBehaviorGroup group = Assert.Single(p.Behaviors);
        Assert.Equal(
            new[] { ActBehaviorKind.SetSpeed, ActBehaviorKind.ChangeAttackInfo, ActBehaviorKind.ChangeAttackInfo, ActBehaviorKind.ChangeAttackInfo, ActBehaviorKind.SetSpeed },
            group.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { "../a.atk", null, "../b.atk" }, group.Items.Skip(1).Take(3).Select(i => i.FilePath).ToArray());
        Assert.Empty(group.UnsupportedTags);
    }

    [Fact]
    public void TheProgramLevelDefaultAttackInfoIsKeptAndTheFirstOneWins()
    {
        ActProgram p = P(Cat(
            new[] { L("[DEFAULT ATTACKINFO]", "../first.atk") },
            Trigger(L("[ON SET ACTION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            new[] { L("[DEFAULT ATTACKINFO]", "../second.atk") },
            Behavior(Speed(1))));
        Assert.Equal("../first.atk", p.DefaultAttackInfo);
        Assert.Single(p.Triggers);                              // 其余记录照常编
        Assert.Single(p.Behaviors);
    }

    [Fact]
    public void AProgramWithoutAUsableDefaultAttackInfoHasNone()
    {
        Assert.Null(P(Behavior(Speed(1))).DefaultAttackInfo);
        Assert.Null(ActProgram.Empty.DefaultAttackInfo);
        Assert.Null(P(L("[DEFAULT ATTACKINFO]", "")).DefaultAttackInfo);      // 空路径当没写
        Assert.Null(P(L("[DEFAULT ATTACKINFO]")).DefaultAttackInfo);
        // 空的在前、真的在后:取第一条"能用的"
        Assert.Equal("../late.atk", P(L("[DEFAULT ATTACKINFO]", ""), L("[DEFAULT ATTACKINFO]", "../late.atk")).DefaultAttackInfo);
    }

    [Fact]
    public void TheRunnerHandsTheHostTheOwnerTheWrittenPathAndTheProgramPath()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(
                L("[CHANGE ATTACKINFO]", "../AttackInfo/x.atk"),
                L("[CHANGE ATTACKINFO]"), L("[DEFAULT]"))));
        var host = new Host();
        var e = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, e, host).Update(0, 0.01f);

        Assert.Equal(2, host.AttackChanges.Count);
        Assert.Same(e, host.AttackChanges[0].owner);
        Assert.Equal(("../AttackInfo/x.atk", "test.act"), (host.AttackChanges[0].path, host.AttackChanges[0].from));
        Assert.Same(e, host.AttackChanges[1].owner);
        Assert.Equal((null, "test.act"), (host.AttackChanges[1].path, host.AttackChanges[1].from));
        Assert.Empty(e.Log);                                    // 换攻击信息不动对象的动作、速度、帧
    }

    [Fact]
    public void TheRunnerAppliesItToTheOwnerEvenWhenTheBehaviorTargetsSomeoneElse()
    {
        // 客户端执行块直接对行为拥有者做,不循环目标集合:[DO BEHAVIOR] 指向主角也只落在拥有者身上
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[TARGET]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CHANGE ATTACKINFO]", "../x.atk"))));
        var host = new Host();
        var hero = new Entity { Kind = ActEntityKind.Character };
        var owner = new Entity { Kind = ActEntityKind.Monster, Target = hero };
        new ActRunner(p, owner, host).Update(0, 0.01f);

        Assert.Single(host.AttackChanges);                      // 目标集合里只有主角,也只调一次
        Assert.Same(owner, host.AttackChanges[0].owner);
    }

    [Fact]
    public void AnOnSetActionTriggerChangesTheAttackInfoOnlyOnce()
    {
        ActProgram p = P(Cat(
            Trigger(L("[ON SET ACTION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[CHANGE ATTACKINFO]", "../x.atk"))));
        var host = new Host();
        var runner = new ActRunner(p, new Entity(), host);
        runner.OnSetAction();
        runner.Update(0, 0.01f);
        runner.Update(0, 0.01f);
        Assert.Single(host.AttackChanges);                      // [ON SET ACTION] 只在进入动作时触发一次
    }
}
