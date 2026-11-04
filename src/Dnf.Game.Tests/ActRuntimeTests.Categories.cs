using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [ADD CATEGORY]、[REMOVE CATEGORY]、[IS CATEGORY](M71,E-171):查表、逐对象过滤与目标集合上的增删。
/// </summary>
public sealed partial class ActRuntimeTests
{
    [Fact]
    public void CategoryBehaviorsCompileTheResolvedIdOrStayUndone()
    {
        ActProgram add = P(Behavior(L("[ADD CATEGORY]"), L("[dragon]")));
        ActBehavior a = OnlyBehavior(add);
        Assert.Equal(ActBehaviorKind.AddCategory, a.Kind);
        Assert.Equal(10, a.CategoryId);

        ActProgram rem = P(Behavior(L("[REMOVE CATEGORY]"), L("[fixture]")));
        Assert.Equal(ActObjectCategories.AnchorCategoryId, OnlyBehavior(rem).CategoryId);

        ActProgram bad = P(Behavior(L("[ADD CATEGORY]"), L("[nope]")));
        Assert.Equal(ActObjectCategories.InvalidId, OnlyBehavior(bad).CategoryId);

        ActProgram empty = P(Behavior(L("[ADD CATEGORY]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(empty).Kind);
    }

    [Fact]
    public void IsCategoryCollectsEveryListedNameOrNumber()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKUP]"), L("[IS CATEGORY]"), L("[dragon]"), L("[human]"), L("[/IS CATEGORY]"), L("[/CHECKUP]"),
                L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1))));
        ActRecord r = p.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsCategory);
        Assert.Equal(new[] { 10, 0 }, r.Ints);
    }

    [Fact]
    public void IsCategoryFiltersWithOrAcrossListedIds()
    {
        ActProgram p = Filter(L("[IS CATEGORY]"), L("[dragon]"), L("[human]"), L("[/IS CATEGORY]"));
        var a = new Entity { Kind = ActEntityKind.Monster };
        a.AddActCategory(10);
        var b = new Entity { Kind = ActEntityKind.Monster };
        b.AddActCategory(0);
        var c = new Entity { Kind = ActEntityKind.Monster };
        Assert.Equal(new[] { true, true, false }, Kept(p, new Entity(), a, b, c));
    }

    [Fact]
    public void AddCategorySkipsDeadEnemiesEvenWhenHostListsDead()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[ADD CATEGORY]"), L("[dragon]"))));
        var host = new Host { ListDeadToo = true };
        var dead = new Entity { Kind = ActEntityKind.Monster, IsAlive = false };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { dead };
        new ActRunner(p, new Entity { Kind = ActEntityKind.Monster }, host).Update(0, 0.01f);
        Assert.Empty(dead.ActCategories);
    }

    [Fact]
    public void AddAndRemoveCategoryHitEveryAliveTarget()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[ADD CATEGORY]"), L("[dragon]"), L("[REMOVE CATEGORY]"), L("[human]"))));
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity>
        {
            new Entity { Kind = ActEntityKind.Monster },
            new Entity { Kind = ActEntityKind.Monster, IsAlive = false },
        };
        var owner = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, owner, host).Update(0, 0.01f);

        var live = (Entity)host.Sets[ActSelector.AllEnemy][0];
        Assert.True(live.HasActCategory(10));
        Assert.False(live.HasActCategory(0));
        Assert.Empty(((Entity)host.Sets[ActSelector.AllEnemy][1]).ActCategories);
    }

    [Fact]
    public void UnknownCategoryNamesAreIgnoredAtRuntime()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[ADD CATEGORY]"), L("[nope]"))));
        var e = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, e, new Host()).Update(0, 0.01f);
        Assert.Empty(e.ActCategories);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void IsCategoryWithNoRecognizedNamesLeavesAnEmptyIdList()
    {
        ActProgram p = P(Trigger(L("[IS CATEGORY]"), L("[nope]"), L("[/IS CATEGORY]"), L("[DO BEHAVIOR]"), L("[ME]", 0)));
        ActRecord r = p.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsCategory);
        Assert.Empty(r.Ints);
    }

    [Fact]
    public void IsCategoryWithEmptyIdListFiltersEveryoneOut()
    {
        ActProgram p = Filter(L("[IS CATEGORY]"), L("[nope]"), L("[/IS CATEGORY]"));
        var tagged = new Entity { Kind = ActEntityKind.Monster };
        tagged.AddActCategory(0);
        tagged.Log.Clear();
        var plain = new Entity { Kind = ActEntityKind.Monster };
        Assert.Equal(new[] { false, false }, Kept(p, new Entity(), tagged, plain));
    }

    [Fact]
    public void IsCategoryNumericTenCompilesListedIdOnOpenLine()
    {
        ActProgram p = Filter(L("[IS CATEGORY]", 10), L("[/IS CATEGORY]"));
        ActRecord r = p.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsCategory);
        Assert.Equal(new[] { 10 }, r.Ints);
    }

    [Fact]
    public void IsCategoryNumericTenRequiresExactCategory()
    {
        ActProgram p = Filter(L("[IS CATEGORY]", 10), L("[/IS CATEGORY]"));
        var hit = new Entity { Kind = ActEntityKind.Monster };
        hit.AddActCategory(10);
        var miss = new Entity { Kind = ActEntityKind.Monster };
        miss.AddActCategory(0);
        hit.Log.Clear();
        miss.Log.Clear();
        Assert.Equal(new[] { true, false }, Kept(p, new Entity(), hit, miss));
    }

    [Fact]
    public void IsCategoryRequiresAnyListedIdNotJustTheFirst()
    {
        ActProgram p = Filter(L("[IS CATEGORY]"), L("[dragon]"), L("[/IS CATEGORY]"));
        var dragon = new Entity { Kind = ActEntityKind.Monster };
        dragon.AddActCategory(10);
        var plain = new Entity { Kind = ActEntityKind.Monster };
        Assert.Equal(new[] { true, false }, Kept(p, new Entity(), dragon, plain));
    }

    [Fact]
    public void IsCategoryOrListMatchesSecondNameNotOnlyTheFirst()
    {
        ActProgram p = Filter(L("[IS CATEGORY]"), L("[dragon]"), L("[goblin]"), L("[/IS CATEGORY]"));
        var goblin = new Entity { Kind = ActEntityKind.Monster };
        goblin.AddActCategory(ActObjectCategories.Resolve("goblin"));
        Assert.Equal(new[] { true }, Kept(p, new Entity(), goblin));
    }

    [Fact]
    public void AddCategoryAloneAddsWithoutNeedingRemoveInTheSameBehavior()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[ADD CATEGORY]"), L("[dragon]"))));
        var host = new Host();
        var target = new Entity { Kind = ActEntityKind.Monster };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { target };
        new ActRunner(p, new Entity { Kind = ActEntityKind.Monster }, host).Update(0, 0.01f);
        Assert.True(target.HasActCategory(10));
    }

    [Fact]
    public void RemoveCategoryAloneRemovesWithoutAddInTheSameBehavior()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[REMOVE CATEGORY]"), L("[human]"))));
        var host = new Host();
        var target = new Entity { Kind = ActEntityKind.Monster };
        target.AddActCategory(0);
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { target };
        new ActRunner(p, new Entity { Kind = ActEntityKind.Monster }, host).Update(0, 0.01f);
        Assert.False(target.HasActCategory(0));
    }

    [Fact]
    public void DuplicateAddCategoryIsIdempotent()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[ADD CATEGORY]"), L("[dragon]"), L("[ADD CATEGORY]"), L("[dragon]"))));
        var self = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, self, new Host()).Update(0, 0.01f);
        Assert.True(self.HasActCategory(10));
        Assert.Equal(new[] { 10 }, self.ActCategories.ToArray());
    }

    [Fact]
    public void RemoveUnknownCategoryDoesNotTouchExistingTags()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[REMOVE CATEGORY]"), L("[nope]"))));
        var self = new Entity { Kind = ActEntityKind.Monster };
        self.AddActCategory(0);
        new ActRunner(p, self, new Host()).Update(0, 0.01f);
        Assert.True(self.HasActCategory(0));
    }

    [Fact]
    public void InvalidCategoryNameSkipsRemoveButLaterBehaviorLinesStillRun()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[REMOVE CATEGORY]"), L("[nope]"), L("[SET SPEED]"), L("[X AXIS]", 9), L("[/SET SPEED]"))));
        var host = new Host();
        var target = new Entity { Kind = ActEntityKind.Monster };
        target.AddActCategory(0);
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { target };
        new ActRunner(p, new Entity { Kind = ActEntityKind.Monster }, host).Update(0, 0.01f);
        Assert.True(target.HasActCategory(0));
        Assert.Contains("speed:9,-,-", target.Log);
    }
}
