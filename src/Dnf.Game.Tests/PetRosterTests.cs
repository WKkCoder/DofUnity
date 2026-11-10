namespace Dnf.Game.Tests;

/// <summary>宠物名册:实例编号不重复、按编号查找与移除、下一个空闲编号。数据全是合成的。</summary>
public class PetRosterTests
{
    private static readonly PetExpTable Table = new PetExpTable(10, 30, 60, 100);

    private static PetInstance Pet(int uid)
    {
        return PetInstance.Hatch(uid, 950, new PetDef(901, "creature/test/p901.cre") { MaxLevel = 5 }, Table, 0);
    }

    [Fact]
    public void APetCanBeFoundByItsInstanceNumberAndOrderIsKept()
    {
        var roster = new PetRoster();
        PetInstance a = Pet(5);
        PetInstance b = Pet(2);
        Assert.True(roster.Add(a));
        Assert.True(roster.Add(b));
        Assert.Equal(2, roster.Count);
        Assert.Same(a, roster.Find(5));
        Assert.Same(b, roster.Find(2));
        Assert.Null(roster.Find(3));
        Assert.Equal(new[] { 5, 2 }, roster.Pets.Select(p => p.Uid).ToArray());
    }

    [Fact]
    public void ANullPetOrADuplicateInstanceNumberIsRefused()
    {
        var roster = new PetRoster();
        PetInstance a = Pet(1);
        Assert.False(roster.Add(null));
        Assert.True(roster.Add(a));
        Assert.False(roster.Add(Pet(1)));
        Assert.Equal(1, roster.Count);
        Assert.Same(a, roster.Find(1));
    }

    [Fact]
    public void RemovingTakesOnlyThePetWithThatNumber()
    {
        var roster = new PetRoster();
        roster.Add(Pet(1));
        roster.Add(Pet(2));
        roster.Add(Pet(3));
        Assert.True(roster.Remove(2));
        Assert.False(roster.Remove(2));
        Assert.False(roster.Remove(99));
        Assert.Equal(new[] { 1, 3 }, roster.Pets.Select(p => p.Uid).ToArray());
        roster.Clear();
        Assert.Equal(0, roster.Count);
    }

    [Fact]
    public void TheNextNumberIsOneAboveTheLargestAndAtLeastOne()
    {
        var roster = new PetRoster();
        Assert.Equal(1, roster.NextUid());
        roster.Add(Pet(4));
        Assert.Equal(5, roster.NextUid());
        roster.Add(Pet(2));
        Assert.Equal(5, roster.NextUid());
        roster.Remove(4);
        Assert.Equal(3, roster.NextUid());
    }

    [Fact]
    public void WhenTheLargestNumberIsTheIntegerLimitTheNextNumberIsTheFirstFreeOne()
    {
        var roster = new PetRoster();
        roster.Add(Pet(int.MaxValue));
        Assert.Equal(1, roster.NextUid());
        roster.Add(Pet(1));
        Assert.Equal(2, roster.NextUid());
        // 取到的号必须真的能加进去。
        Assert.True(roster.Add(Pet(roster.NextUid())));
    }
}
