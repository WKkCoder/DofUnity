using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [CHANGE ACTION FILE](M57,证据 E-151):编译(族关键字、序号、路径)与运行器执行(只对怪物、原样交给宿主)。
/// 客户端执行块在 0x010ca404,族码 0 到 13;怪物那一侧怎么换动作文件见 Play 层的测试。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ScriptLine[] FileLines(string slotTag, int? index, string path)
    {
        var values = new List<object>();
        if (index.HasValue)
        {
            values.Add(index.Value);
        }
        if (path != null)
        {
            values.Add(path);
        }
        return new[] { L("[CHANGE ACTION FILE]"), L(slotTag, values.ToArray()) };
    }

    private static ActBehavior OnlyBehavior(ActProgram p) => Assert.Single(Assert.Single(p.Behaviors).Items);

    [Theory]
    [InlineData("[STAY]", ActFileSlot.Stay, false)]
    [InlineData("[MOVE]", ActFileSlot.Move, false)]
    [InlineData("[MOVEBACK]", ActFileSlot.MoveBack, false)]
    [InlineData("[DASH]", ActFileSlot.Dash, false)]
    [InlineData("[DASHATTACK]", ActFileSlot.DashAttack, false)]
    [InlineData("[SIT]", ActFileSlot.Sit, false)]
    [InlineData("[DOWN]", ActFileSlot.Down, false)]
    [InlineData("[OVERTURN]", ActFileSlot.Overturn, false)]
    [InlineData("[JUMP]", ActFileSlot.Jump, false)]
    [InlineData("[JUMPATTACK]", ActFileSlot.JumpAttack, false)]
    [InlineData("[DAMAGE]", ActFileSlot.Damage, true)]
    [InlineData("[ATTACK]", ActFileSlot.Attack, true)]
    [InlineData("[CUSTOM]", ActFileSlot.Custom, true)]
    public void EveryFamilyKeywordCompilesToItsSlotWithTheWrittenPath(string keyword, ActFileSlot slot, bool indexed)
    {
        ActProgram p = P(Behavior(FileLines(keyword, indexed ? 3 : null, "x/new.act")));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.ChangeActionFile, b.Kind);
        Assert.Equal(slot, b.FileSlot);
        Assert.Equal(indexed ? 3 : 0, b.FileIndex);
        Assert.Equal("x/new.act", b.FilePath);
        Assert.Empty(p.Behaviors[0].UnsupportedTags);
    }

    [Theory]
    [InlineData("[DAMAGE]", ActFileSlot.Damage)]
    [InlineData("[ATTACK]", ActFileSlot.Attack)]
    [InlineData("[CUSTOM]", ActFileSlot.Custom)]
    public void AnIndexedFamilyWithoutAnIndexMeansIndexZero(string keyword, ActFileSlot slot)
    {
        ActBehavior b = OnlyBehavior(P(Behavior(FileLines(keyword, null, "n.act"))));
        Assert.Equal(ActBehaviorKind.ChangeActionFile, b.Kind);
        Assert.Equal(slot, b.FileSlot);
        Assert.Equal(0, b.FileIndex);
        Assert.Equal("n.act", b.FilePath);
    }

    [Fact]
    public void TheSlotCodesFollowTheClientFamilyNumbers()
    {
        // 族码取自客户端执行块(E-151);代码里按名字用,这里钉住数值,免得枚举顺序被改乱后文档与证据对不上
        Assert.Equal(0, (int)ActFileSlot.Stay);
        Assert.Equal(1, (int)ActFileSlot.Move);
        Assert.Equal(2, (int)ActFileSlot.MoveBack);
        Assert.Equal(3, (int)ActFileSlot.Dash);
        Assert.Equal(4, (int)ActFileSlot.DashAttack);
        Assert.Equal(5, (int)ActFileSlot.Sit);
        Assert.Equal(6, (int)ActFileSlot.Damage);
        Assert.Equal(8, (int)ActFileSlot.Down);
        Assert.Equal(9, (int)ActFileSlot.Overturn);
        Assert.Equal(10, (int)ActFileSlot.Jump);
        Assert.Equal(11, (int)ActFileSlot.JumpAttack);
        Assert.Equal(12, (int)ActFileSlot.Attack);
        Assert.Equal(13, (int)ActFileSlot.Custom);
    }

    [Fact]
    public void AWritingWithoutAFamilyCodeOrWithoutAPathIsLeftUndone()
    {
        // [LAST] 不在客户端的族码表里;没写路径的写法没有东西可换
        ActProgram last = P(Behavior(FileLines("[LAST]", null, "x.act")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(last).Kind);
        Assert.Contains("[CHANGE ACTION FILE]", last.Behaviors[0].UnsupportedTags);

        ActProgram noPath = P(Behavior(FileLines("[STAY]", null, null)));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(noPath).Kind);

        ActProgram noFamily = P(Behavior(L("[CHANGE ACTION FILE]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(noFamily).Kind);
    }

    [Fact]
    public void SeveralRecordsInOneGroupCompileSideBySideAndTheNextBehaviorStillParses()
    {
        ActProgram p = P(Behavior(
            FileLines("[STAY]", null, "a.act")
                .Concat(FileLines("[DAMAGE]", 1, "b.act"))
                .Concat(FileLines("[ATTACK]", 2, "c.act"))
                .Concat(Speed(5))
                .ToArray()));
        ActBehaviorGroup group = Assert.Single(p.Behaviors);
        Assert.Equal(
            new[] { ActBehaviorKind.ChangeActionFile, ActBehaviorKind.ChangeActionFile, ActBehaviorKind.ChangeActionFile, ActBehaviorKind.SetSpeed },
            group.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { ActFileSlot.Stay, ActFileSlot.Damage, ActFileSlot.Attack }, group.Items.Take(3).Select(i => i.FileSlot).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, group.Items.Take(3).Select(i => i.FileIndex).ToArray());
        Assert.Equal(new[] { "a.act", "b.act", "c.act" }, group.Items.Take(3).Select(i => i.FilePath).ToArray());
    }

    [Fact]
    public void TheRunnerHandsEveryMonsterTargetTheRecordAsWrittenWithTheProgramPath()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(FileLines("[DAMAGE]", 1, "sub/rage.act").Concat(FileLines("[STAY]", null, "stay2.act")).ToArray())));
        var host = new Host();
        var e = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, e, host).Update(0, 0.01f);

        Assert.Equal(2, host.FileChanges.Count);
        Assert.Same(e, host.FileChanges[0].target);
        Assert.Equal((ActFileSlot.Damage, 1, "sub/rage.act", "test.act"), (host.FileChanges[0].slot, host.FileChanges[0].index, host.FileChanges[0].path, host.FileChanges[0].from));
        Assert.Equal((ActFileSlot.Stay, 0, "stay2.act", "test.act"), (host.FileChanges[1].slot, host.FileChanges[1].index, host.FileChanges[1].path, host.FileChanges[1].from));
        Assert.Empty(e.Log);                           // 换文件不动怪物当前的动作、速度、帧
    }

    [Theory]
    [InlineData(ActEntityKind.Character)]
    [InlineData(ActEntityKind.Passive)]
    public void CharactersAndPassiveObjectsHaveNoActionFileTable(ActEntityKind kind)
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(FileLines("[STAY]", null, "a.act"))));
        var host = new Host();
        new ActRunner(p, new Entity { Kind = kind }, host).Update(0, 0.01f);
        Assert.Empty(host.FileChanges);
    }

    [Fact]
    public void ARecordIsAppliedToEveryMonsterInTheTargetSetAndSkipsTheOthers()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ALL ENEMY]", 0)),
            Behavior(FileLines("[MOVE]", null, "m.act"))));
        var host = new Host();
        var m1 = new Entity { Kind = ActEntityKind.Monster };
        var m2 = new Entity { Kind = ActEntityKind.Monster };
        var hero = new Entity { Kind = ActEntityKind.Character };
        var passive = new Entity { Kind = ActEntityKind.Passive };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { m1, hero, passive, m2 };
        new ActRunner(p, new Entity(), host).Update(0, 0.01f);

        Assert.Equal(new IActEntity[] { m1, m2 }, host.FileChanges.Select(c => c.target).ToArray());
    }

    [Fact]
    public void ARecordDoesNotAbortTheGroupAndAHostThatDoesNotCareIsNotCountedAsUnsupported()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(FileLines("[STAY]", null, "a.act").Concat(Speed(9)).ToArray())));
        var host = new CountingHost();
        var e = new Entity();
        new ActRunner(p, e, host).Update(0, 0.01f);

        Assert.Equal(new[] { "speed:9,-,-" }, e.Log.ToArray());
        Assert.Empty(host.Behaviors);                  // 行为是做了的(宿主没接而已),不记作"没做的行为"
    }
}
