namespace Dnf.Game.Tests;

/// <summary>制作线对外的接缝:金币钱包、有种子的骰子、默认闸门。</summary>
public class CraftSeamTests
{
    // ------------------------------------------------------------------ 钱包

    [Fact]
    public void ThePurseSpendsOnlyWhatItHas()
    {
        var purse = new CraftPurse(100);
        Assert.False(purse.TrySpend(101));
        Assert.Equal(100, purse.Gold);
        Assert.True(purse.TrySpend(40));
        Assert.Equal(60, purse.Gold);
        Assert.True(purse.TrySpend(60));
        Assert.Equal(0, purse.Gold);
        Assert.False(purse.TrySpend(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ASpendOfNothingOrLessIsRefused(long amount)
    {
        var purse = new CraftPurse(100);
        Assert.False(purse.TrySpend(amount));
        Assert.Equal(100, purse.Gold);
    }

    [Fact]
    public void ANegativeStartingBalanceIsTreatedAsEmpty()
    {
        Assert.Equal(0, new CraftPurse(-3).Gold);
        Assert.Equal(0, new CraftPurse().Gold);
    }

    // ------------------------------------------------------------------ 骰子

    [Fact]
    public void TheSeededRollStaysInRangeAndReachesBothEnds()
    {
        var roll = new SeededCraftRoll(12345);
        var seen = new HashSet<int>();
        for (int i = 0; i < 20000; i++)
        {
            int v = roll.Below(100);
            Assert.InRange(v, 0, 99);
            seen.Add(v);
        }
        // 种子固定,序列是确定的:0 到 99 每个数都出现过(上界 99 能掷到、也不会少掷一位)。
        Assert.Contains(0, seen);
        Assert.Contains(99, seen);
        Assert.Equal(100, seen.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void ASmallRangeIsFullyCoveredToo(int n)
    {
        var roll = new SeededCraftRoll(99);
        var seen = new HashSet<int>();
        for (int i = 0; i < 2000; i++)
        {
            int v = roll.Below(n);
            Assert.InRange(v, 0, n - 1);
            seen.Add(v);
        }
        Assert.Equal(n, seen.Count);
    }

    [Fact]
    public void TheSameSeedGivesTheSameSequenceAndADifferentSeedDoesNot()
    {
        var a = new SeededCraftRoll(7);
        var b = new SeededCraftRoll(7);
        var c = new SeededCraftRoll(8);
        bool differs = false;
        for (int i = 0; i < 50; i++)
        {
            int x = a.Below(100);
            Assert.Equal(x, b.Below(100));
            differs |= x != c.Below(100);
        }
        Assert.True(differs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-4)]
    public void ARangeOfOneOrLessAlwaysGivesZero(int n)
    {
        var roll = new SeededCraftRoll(1);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0, roll.Below(n));
        }
    }

    // ------------------------------------------------------------------ 默认闸门

    [Fact]
    public void TheOpenGateAllowsEverythingAndRecordsNothing()
    {
        ICraftGate gate = OpenCraftGate.Instance;
        RecipeDef recipe = CraftKit.Recipe().In(CraftKit.Ore, 1).Out(CraftKit.Potion, 1).Build();
        Assert.True(gate.CanLearn(recipe));
        Assert.True(gate.CanCraft(recipe));
        gate.OnCrafted(recipe, null);
        Assert.Same(OpenCraftGate.Instance, OpenCraftGate.Instance);
    }

    [Fact]
    public void AnOpenGateSubclassCanClosePartOfTheDoorOnly()
    {
        var gate = new NoCraftGate();
        RecipeDef recipe = CraftKit.Recipe().In(CraftKit.Ore, 1).Out(CraftKit.Potion, 1).Build();
        Assert.True(gate.CanLearn(recipe));
        Assert.False(gate.CanCraft(recipe));
    }

    private sealed class NoCraftGate : OpenCraftGate
    {
        public override bool CanCraft(RecipeDef recipe) => false;
    }
}
