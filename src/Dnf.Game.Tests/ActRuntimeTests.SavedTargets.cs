using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [SAVE TARGET OBJECT] 与 [LOAD TARGET](M61-b,证据 E-157,客户端静态读码):
/// 存是每帧运行器 0x010ad580 里的记录码 0x3d,存的函数 0x010d1f40 把选择结果的第一个对象存进拥有者的槽表(收集到的个数为 0 不动槽);
/// 取是选择器码 0x15(0x010d25e0 里的块),按槽号取出存下的对象。槽表挂在拥有者对象上(存取函数的 this 是运行器的 +0x94),不随运行器结束而清掉。
/// 脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ActRecord TheSaveRecord(params ScriptLine[] save)
    {
        ActTrigger t = Assert.Single(P(Cat(Trigger(Cat(save, new[] { L("[DO BEHAVIOR]"), L("[ME]", 0) })), Behavior(Speed(1)))).Triggers);
        Assert.True(t.Supported);
        return Assert.Single(t.Records, r => r.Kind == ActRecordKind.SaveTarget);
    }

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void SaveTargetObjectWithoutASelectorSavesTheWorkingSet()
    {
        ActRecord r = TheSaveRecord(L("[SAVE TARGET OBJECT]", 0));

        Assert.Equal(ActSelector.WorkingSet, r.Selector);
        Assert.Equal(0, r.Number);
    }

    [Fact]
    public void SaveTargetObjectReadsItsSixSelectorsAndTheSlot()
    {
        (string tag, ActSelector selector)[] cases =
        {
            ("[TARGET]", ActSelector.Target),
            ("[SUMMON MASTER]", ActSelector.SummonMaster),
            ("[LAST ATTACKSUCCESS]", ActSelector.LastAttackSuccess),
            ("[CHARACTER ATTACKSUCCESS]", ActSelector.CharacterAttackSuccess),
            ("[LAST ATTACKER]", ActSelector.LastAttacker),
            ("[LAST ACTIVE ATTACKER]", ActSelector.LastActiveAttacker),
        };
        foreach ((string tag, ActSelector selector) in cases)
        {
            ActRecord r = TheSaveRecord(L("[SAVE TARGET OBJECT]"), L(tag, 10));
            Assert.Equal(selector, r.Selector);
            Assert.Equal(10, r.Number);
        }
    }

    [Fact]
    public void SaveTargetObjectTreatsAnyOtherSelectorTagAsTheDefaultWorkingSet()
    {
        // 客户端加载块只认上面六个;别的标签(含 [CHECKUP OBJECT] 与全库 4 处笔误 [LAST ATTACKSUUCCESS])被吃掉后落到默认的工作集
        Assert.Equal(ActSelector.WorkingSet, TheSaveRecord(L("[SAVE TARGET OBJECT]"), L("[CHECKUP OBJECT]", 20)).Selector);
        ActRecord typo = TheSaveRecord(L("[SAVE TARGET OBJECT]"), L("[LAST ATTACKSUUCCESS]", 4));
        Assert.Equal(ActSelector.WorkingSet, typo.Selector);
        Assert.Equal(4, typo.Number);
    }

    [Fact]
    public void SaveTargetObjectWithoutASlotIsUnsupportedAndDoesNotEatTheNextRecord()
    {
        ActTrigger t = Assert.Single(P(Cat(
            Trigger(L("[SAVE TARGET OBJECT]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(1)))).Triggers);

        Assert.False(t.Supported);
        Assert.Contains("[SAVE TARGET OBJECT]", t.UnsupportedTags);
    }

    [Fact]
    public void SaveTargetObjectWithoutASlotLeavesAFollowingSaveRecordAlone()
    {
        ActTrigger t = Assert.Single(P(Cat(
            Trigger(L("[SAVE TARGET OBJECT]"), L("[SAVE TARGET OBJECT]", 3)),
            Behavior(Speed(1)))).Triggers);

        Assert.False(t.Supported);                                              // 第一条缺槽号
        ActRecord later = Assert.Single(t.Records, r => r.Kind == ActRecordKind.SaveTarget);
        Assert.Equal(3, later.Number);                                          // 第二条没被当成第一条的选择器标签吃掉
    }

    [Fact]
    public void LoadTargetReadsItsSlotInWhichAndDoBehavior()
    {
        ActTrigger which = Assert.Single(P(Cat(
            Trigger(L("[WHICH]"), L("[LOAD TARGET]", 3), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1)))).Triggers);
        Assert.True(which.Supported);
        Assert.Equal(ActSelector.LoadTarget, which.Records[0].Selector);
        Assert.Equal(3, which.Records[0].PartyIndex);

        ActTrigger doBehavior = Assert.Single(P(Cat(
            Trigger(L("[DO BEHAVIOR]"), L("[LOAD TARGET]", 3, 0)),
            Behavior(Speed(1)))).Triggers);
        Assert.True(doBehavior.Supported);
        Assert.Equal(ActSelector.LoadTarget, doBehavior.Records[0].Selector);
        Assert.Equal(3, doBehavior.Records[0].PartyIndex);
        Assert.Equal(0, doBehavior.Records[0].Behavior.First);
    }

    [Fact]
    public void LoadTargetWithoutASlotOrAGroupIsUnsupported()
    {
        Assert.False(Assert.Single(P(Cat(Trigger(L("[WHICH]"), L("[LOAD TARGET]"), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(Speed(1)))).Triggers).Supported);
        Assert.False(Assert.Single(P(Cat(Trigger(L("[DO BEHAVIOR]"), L("[LOAD TARGET]")), Behavior(Speed(1)))).Triggers).Supported);
        Assert.False(Assert.Single(P(Cat(Trigger(L("[DO BEHAVIOR]"), L("[LOAD TARGET]", 3)), Behavior(Speed(1)))).Triggers).Supported);
    }

    // ------------------------------------------------------------------ 运行

    private static ActProgram SaveProgram(int slot) =>
        P(Cat(Trigger(L("[WHICH]"), L("[ALL ENEMY]"), L("[SAVE TARGET OBJECT]", slot)), Behavior(Speed(1))));

    private static ActProgram LoadProgram(int slot) => P(Cat(
        Trigger(L("[WHICH]"), L("[LOAD TARGET]", slot), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
        Behavior(CreateObject(7))));

    private static List<(float x, float y)> Spawned(Host host) => host.Spawns.Select(s => (s.x, s.y)).ToList();

    [Fact]
    public void ASavedTargetIsLoadedByALaterTriggerInTheSameRunner()
    {
        var a = new Entity { X = 10, Y = 1 };
        var b = new Entity { X = 20, Y = 2 };
        var owner = new Entity();
        Host host = EnemyHost(a, b);
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ALL ENEMY]"), L("[SAVE TARGET OBJECT]", 0)),
            Trigger(L("[WHICH]"), L("[LOAD TARGET]", 0), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(CreateObject(7))));

        new ActRunner(p, owner, host).Update(0, 0.016f);

        Assert.Same(a, owner.SavedTargets[0]);                     // 工作集里的第一个
        Assert.Equal(new[] { At(a) }, Spawned(host));
    }

    [Fact]
    public void TheSlotTableBelongsToTheOwnerAndSurvivesANewRunner()
    {
        var a = new Entity { X = 10, Y = 1 };
        var owner = new Entity();

        new ActRunner(SaveProgram(5), owner, EnemyHost(a)).Update(0, 0.016f);
        Host later = new Host();
        new ActRunner(LoadProgram(5), owner, later).Update(0, 0.016f);          // 另一个运行器(另一招)取出同一槽

        Assert.Equal(new[] { At(a) }, Spawned(later));
    }

    [Fact]
    public void SlotsAreIndependent()
    {
        var a = new Entity { X = 10, Y = 1 };
        var b = new Entity { X = 20, Y = 2 };
        var owner = new Entity();

        new ActRunner(SaveProgram(1), owner, EnemyHost(a)).Update(0, 0.016f);
        new ActRunner(SaveProgram(2), owner, EnemyHost(b)).Update(0, 0.016f);

        Assert.Same(a, owner.SavedTargets[1]);
        Assert.Same(b, owner.SavedTargets[2]);
    }

    [Fact]
    public void SavingAnEmptySelectionLeavesTheSlotAlone()
    {
        var a = new Entity { X = 10, Y = 1 };
        var owner = new Entity();

        new ActRunner(SaveProgram(0), owner, EnemyHost(a)).Update(0, 0.016f);
        new ActRunner(SaveProgram(0), owner, EnemyHost()).Update(0, 0.016f);        // 这次没有敌人:不动槽

        Assert.Same(a, owner.SavedTargets[0]);
    }

    [Fact]
    public void SavingOverwritesAnEarlierValueOfTheSameSlot()
    {
        var a = new Entity { X = 10, Y = 1 };
        var b = new Entity { X = 20, Y = 2 };
        var owner = new Entity();

        new ActRunner(SaveProgram(0), owner, EnemyHost(a)).Update(0, 0.016f);
        new ActRunner(SaveProgram(0), owner, EnemyHost(b)).Update(0, 0.016f);

        Assert.Same(b, owner.SavedTargets[0]);
    }

    [Fact]
    public void SaveTargetObjectIsNotAConditionSoAnEmptySetStillLetsLaterRecordsRun()
    {
        // 空工作集:存不进去,但后面的 [CHECKED NO] [=] 0 与 [DO BEHAVIOR] 照常
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ALL ENEMY]"), L("[SAVE TARGET OBJECT]", 0), L("[CHECKED NO]"), L("[=]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(4))));

        Assert.True(Fires(p, new Entity(), EnemyHost()));
    }

    [Fact]
    public void ALoadedTargetThatHasDiedIsNotSelected()
    {
        var a = new Entity { X = 10, Y = 1 };
        var owner = new Entity();
        owner.SavedTargets[0] = a;
        a.IsAlive = false;
        var host = new Host();

        new ActRunner(LoadProgram(0), owner, host).Update(0, 0.016f);

        Assert.Empty(host.Spawns);
    }

    [Fact]
    public void LoadingAnEmptySlotSelectsNothingAndACountCheckStillPasses()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[LOAD TARGET]", 7), L("[CHECKED NO]"), L("[=]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(4))));

        Assert.True(Fires(p, new Entity()));
    }

    [Fact]
    public void DoBehaviorLoadTargetRunsTheGroupOnTheSavedObject()
    {
        var saved = new Entity { X = 10 };
        var owner = new Entity();
        owner.SavedTargets[3] = saved;
        ActProgram p = P(Cat(Trigger(L("[DO BEHAVIOR]"), L("[LOAD TARGET]", 3, 0)), Behavior(Speed(4))));

        new ActRunner(p, owner, new Host()).Update(0, 0.016f);

        Assert.Contains("speed:4,-,-", saved.Log);
        Assert.Empty(owner.Log);

        // 槽里没有(或已经死了)就不触发
        var empty = new Entity();
        new ActRunner(p, empty, new Host()).Update(0, 0.016f);
        Assert.Empty(empty.Log);
    }

    [Fact]
    public void SaveTargetObjectWithOtherSelectorsStoresTheirFirstObject()
    {
        var target = new Entity { X = 1 };
        var master = new Entity { X = 2 };
        var victim = new Entity { X = 3 };
        var attacker = new Entity { X = 4 };
        var owner = new Entity { Target = target, Master = master };
        ActProgram p = P(Cat(
            Trigger(L("[SAVE TARGET OBJECT]"), L("[TARGET]", 1)),
            Trigger(L("[SAVE TARGET OBJECT]"), L("[SUMMON MASTER]", 2)),
            Trigger(L("[ON ATTACKSUCCESS]"), L("[SAVE TARGET OBJECT]"), L("[LAST ATTACKSUCCESS]", 3)),
            Trigger(L("[ON DAMAGE]"), L("[SAVE TARGET OBJECT]"), L("[LAST ATTACKER]", 4)),
            Behavior(Speed(1))));
        var runner = new ActRunner(p, owner, new Host());

        runner.Update(0, 0.016f);
        runner.OnAttackSuccess(victim);
        runner.OnDamage(attacker);

        Assert.Same(target, owner.SavedTargets[1]);
        Assert.Same(master, owner.SavedTargets[2]);
        Assert.Same(victim, owner.SavedTargets[3]);
        Assert.Same(attacker, owner.SavedTargets[4]);
    }

    [Fact]
    public void SaveTargetObjectWithADeadTargetStoresNothing()
    {
        var target = new Entity { X = 1, IsAlive = false };
        var owner = new Entity { Target = target };
        ActProgram p = P(Cat(Trigger(L("[SAVE TARGET OBJECT]"), L("[TARGET]", 1)), Behavior(Speed(1))));

        new ActRunner(p, owner, new Host()).Update(0, 0.016f);

        Assert.False(owner.SavedTargets.ContainsKey(1));
    }
}
