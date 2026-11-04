using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>[SET TEAM]、[IS VISIBLE]、[IS DAMAGEBOX ENABLE](M74,E-221–E-223)。</summary>
public sealed partial class ActRuntimeTests
{
    [Fact]
    public void SetTeamCompilesValueExpressionOrStaysUndone()
    {
        ActProgram ok = P(Behavior(L("[SET TEAM]", 5)));
        ActBehavior b = OnlyBehavior(ok);
        Assert.Equal(ActBehaviorKind.SetTeam, b.Kind);
        Assert.Equal(5, b.TeamValue.Eval(_ => 0));

        ActProgram bad = P(Behavior(L("[SET TEAM]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(bad).Kind);
    }

    [Fact]
    public void SetTeamAppliesToAliveNonPassiveTargets()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET TEAM]", 7))));
        var host = new Host();
        var live = new Entity { Kind = ActEntityKind.Monster };
        var dead = new Entity { Kind = ActEntityKind.Monster, IsAlive = false };
        var passive = new Entity { Kind = ActEntityKind.Passive };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { live, dead, passive };
        new ActRunner(p, new Entity(), host).Update(0, 0.01f);
        Assert.Equal(7, live.OverrideActTeam);
        Assert.Null(dead.OverrideActTeam);
        Assert.Null(passive.OverrideActTeam);
        Assert.Contains("team:7", live.Log);
    }

    [Fact]
    public void SetTeamChangesIsTeamFiltering()
    {
        ActProgram p = Filter(L("[IS TEAM]"), L("[CHARCTER TEAM]"), L("[/IS TEAM]"));
        var owner = new Entity { Kind = ActEntityKind.Monster };
        var hero = new Entity { Kind = ActEntityKind.Character };
        var mob = new Entity { Kind = ActEntityKind.Monster };
        mob.SetActTeam(0);
        Assert.Equal(new[] { true, true, false }, Kept(p, owner, new Host(), hero, mob, new Entity { Kind = ActEntityKind.Monster }));
    }

    [Fact]
    public void IsVisibleCompilesIntegerOrStaysUndone()
    {
        ActProgram ok = Filter(L("[IS VISIBLE]", 2), L("[/IS VISIBLE]"));
        ActRecord r = ok.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsVisible);
        Assert.Equal(2, r.Number);

        ActProgram bad = Filter(L("[IS VISIBLE]"), L("[/IS VISIBLE]"));
        Assert.Contains(ActRecordKind.Unsupported, bad.Triggers[0].Records.Select(x => x.Kind));
    }

    [Fact]
    public void IsVisibleFiltersByLowByte()
    {
        ActProgram p = Filter(L("[IS VISIBLE]", 3), L("[/IS VISIBLE]"));
        var a = new Entity { ActVisibilityByte = 3 };
        var b = new Entity { ActVisibilityByte = 0x103 };
        var c = new Entity { ActVisibilityByte = 4 };
        var d = new Entity { ActVisibilityByte = 0x100 };
        Assert.Equal(new[] { true, true, false, false }, Kept(p, new Entity(), new Host(), a, b, c, d));
    }

    [Fact]
    public void IsDamageBoxEnableCompilesIntegerOrStaysUndone()
    {
        ActProgram ok = Filter(L("[IS DAMAGEBOX ENABLE]", 1), L("[/IS DAMAGEBOX ENABLE]"));
        ActRecord r = ok.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsDamageBoxEnable);
        Assert.Equal(1, r.Number);
    }

    [Fact]
    public void IsDamageBoxEnableMatchesEnabledFlagAndDropsPassive()
    {
        ActProgram on = Filter(L("[IS DAMAGEBOX ENABLE]", 1), L("[/IS DAMAGEBOX ENABLE]"));
        ActProgram off = Filter(L("[IS DAMAGEBOX ENABLE]", 0), L("[/IS DAMAGEBOX ENABLE]"));
        ActRecord offRec = off.Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsDamageBoxEnable);
        Assert.Equal(0, offRec.Number);
        var enabled = new Entity { Kind = ActEntityKind.Monster, IsActDamageBoxEnabled = true };
        var disabled = new Entity { Kind = ActEntityKind.Monster, IsActDamageBoxEnabled = false };
        var passive = new Entity { Kind = ActEntityKind.Passive, IsActDamageBoxEnabled = true };
        Assert.Equal(new[] { true, false, false }, Kept(on, new Entity(), new Host(), enabled, disabled, passive));
        var enabledOff = new Entity { Kind = ActEntityKind.Monster, IsActDamageBoxEnabled = true };
        var disabledOff = new Entity { Kind = ActEntityKind.Monster, IsActDamageBoxEnabled = false };
        var passiveOff = new Entity { Kind = ActEntityKind.Passive, IsActDamageBoxEnabled = true };
        Assert.Equal(new[] { false, true, false }, Kept(off, new Entity(), new Host(), enabledOff, disabledOff, passiveOff));
    }
}
