using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>[REALDUNGEON CHECKUP]、[IS OURTEAMATTACKED](M73,E-173)。</summary>
public sealed partial class ActRuntimeTests
{
    [Fact]
    public void RealDungeonCheckupCompilesThreeIntegers()
    {
        ActProgram p = P(Trigger(L("[REALDUNGEON CHECKUP]", 1, 42, 5), L("[DO BEHAVIOR]"), L("[ME]", 0)));
        ActRecord r = Assert.Single(p.Triggers[0].Records, x => x.Kind == ActRecordKind.RealDungeonCheckup);
        Assert.Equal(new[] { 1, 42, 5 }, r.Ints);
    }

    [Theory]
    [InlineData(0, 7, 0, 0, true)]
    [InlineData(0, 7, 0, 3, false)]
    [InlineData(2, 7, 0, 0, false)]
    [InlineData(2, 7, 0, 5, true)]
    [InlineData(1, 3, 10, 10, true)]
    [InlineData(1, 3, 10, 11, true)]
    [InlineData(1, 3, 10, 9, false)]
    [InlineData(2, 3, 10, 9, true)]
    [InlineData(2, 3, 10, 10, false)]
    public void RealDungeonCheckupTruthTableMatchesClientBranches(int mode, int key, int compare, int value, bool pass)
    {
        bool ok = ActRunner.RealDungeonCheckupPasses(mode, key, compare, (_, k) => k == key ? value : 0);
        Assert.Equal(pass, ok);
    }

    [Fact]
    public void RealDungeonCheckupTriggerFailsWhenLookupIsTooLow()
    {
        ActProgram p = P(Cat(
            Trigger(L("[REALDUNGEON CHECKUP]", 0, 5, 10), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(7))));
        var host = new Host();
        host.RealDungeon[(0, 5)] = 9;
        var self = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, self, host).Update(0, 0.01f);
        Assert.DoesNotContain("speed:", self.Log);
    }

    [Fact]
    public void RealDungeonCheckupTriggerRunsBehaviorWhenComparePasses()
    {
        ActProgram p = P(Cat(
            Trigger(L("[REALDUNGEON CHECKUP]", 0, 5, 10), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(7))));
        var host = new Host();
        host.RealDungeon[(0, 5)] = 10;
        var self = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(p, self, host).Update(0, 0.01f);
        Assert.Contains("speed:7,-,-", self.Log);
    }

    [Fact]
    public void IsOurTeamAttackedKeepsOnlyMarkedVictims()
    {
        ActProgram p = Filter(L("[IS OURTEAMATTACKED]"));
        var owner = new Entity { Kind = ActEntityKind.Monster };
        var hero = new Entity { Kind = ActEntityKind.Character };
        var ally = new Entity { Kind = ActEntityKind.Character };
        var attacker = new Entity { Kind = ActEntityKind.Character, ActAiState = 3, Target = hero };
        var host = new Host();
        host.OurTeamAttackMarks.Add((hero, attacker, 3));
        Assert.Equal(new[] { true, false }, Kept(p, owner, host, hero, ally));
    }
}
