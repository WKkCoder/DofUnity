using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>个体状态(强化等级、增幅、附魔、品级、耐久)与"定义共享、个体拷贝带状态"的规则。数据全是合成的。</summary>
public class EquipInstanceTests
{
    [Fact]
    public void ANewInstanceIsTheInitialState()
    {
        var s = new EquipInstance();
        Assert.True(s.IsDefault);
        Assert.Equal((0, false, true, 0, 0, false), (s.Upgrade, s.Amplified, s.Identified, s.EnchantCode, s.GradeValue, s.Damaged));
        Assert.Equal(31, EquipInstance.MaxUpgrade);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(255)]
    public void UpgradeAcceptsOneByte(int value)
    {
        Assert.Equal(value, new EquipInstance(value).Upgrade);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(1000)]
    public void UpgradeOutsideOneByteIsRejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EquipInstance(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EquipInstance().Upgrade = value);
    }

    [Fact]
    public void ANegativeEnchantCodeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EquipInstance(0, enchantCode: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EquipInstance().EnchantCode = -5);
        Assert.Equal(0, new EquipInstance(0, enchantCode: 0).EnchantCode);
    }

    [Fact]
    public void AnySingleFieldMakesItNonDefaultAndUnequal()
    {
        var baseline = new EquipInstance();
        EquipInstance[] changed =
        {
            new EquipInstance(1),
            new EquipInstance(0, amplified: true),
            new EquipInstance(0, identified: false),
            new EquipInstance(0, enchantCode: 931),
            new EquipInstance(0, gradeValue: 7),
            new EquipInstance(0, damaged: true),
        };
        foreach (EquipInstance s in changed)
        {
            Assert.False(s.IsDefault);
            Assert.NotEqual(baseline, s);
            Assert.False(baseline.Equals(s));
        }
        for (int i = 0; i < changed.Length; i++)
        {
            for (int j = 0; j < changed.Length; j++)
            {
                Assert.Equal(i == j, changed[i].Equals(changed[j]));
            }
        }
    }

    [Fact]
    public void EqualStatesHaveEqualHashCodes()
    {
        var a = new EquipInstance(5, true, false, 931, 3, true);
        var b = new EquipInstance(5, true, false, 931, 3, true);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals((object)null));
        Assert.False(a.Equals("text"));
        Assert.False(a.Equals((EquipInstance)null));
    }

    [Fact]
    public void ACloneIsIndependent()
    {
        var a = new EquipInstance(5, true, false, 931, 3, true);
        EquipInstance c = a.Clone();
        Assert.Equal(a, c);
        Assert.NotSame(a, c);
        c.Upgrade = 6;
        c.Amplified = false;
        c.Identified = true;
        c.EnchantCode = 0;
        c.GradeValue = 0;
        c.Damaged = false;
        Assert.Equal(new EquipInstance(5, true, false, 931, 3, true), a);
    }

    [Fact]
    public void TheTextNamesTheNonDefaultParts()
    {
        Assert.Equal("+0", new EquipInstance().ToString());
        Assert.Equal("+7", new EquipInstance(7).ToString());
        Assert.Contains("增幅", new EquipInstance(1, amplified: true).ToString());
        Assert.Contains("未鉴定", new EquipInstance(1, amplified: true, identified: false).ToString());
        Assert.Contains("附魔#931", new EquipInstance(0, enchantCode: 931).ToString());
        Assert.Contains("品级值3", new EquipInstance(0, gradeValue: 3).ToString());
        Assert.Contains("耐久未满", new EquipInstance(0, damaged: true).ToString());
    }

    // ------------------------------------------------------------------ 定义与个体拷贝

    [Fact]
    public void ADefinitionHasNoInstanceStateAndIsItsOwnDefinition()
    {
        ItemInfo gear = Gear(900);
        Assert.Null(gear.Instance);
        Assert.False(gear.IsInstance);
        Assert.Same(gear, gear.Definition);
        Assert.True(gear.StateOrDefault.IsDefault);
    }

    [Fact]
    public void ACopyCarriesTheStateAndPointsBackAtTheDefinition()
    {
        ItemInfo gear = Gear(900, EquipmentParts.Weapon, rarity: 2, grade: 4, level: 30);
        ItemInfo copy = gear.WithInstance(new EquipInstance(3));
        Assert.NotSame(gear, copy);
        Assert.True(copy.IsInstance);
        Assert.Same(gear, copy.Definition);
        Assert.Equal(3, copy.Instance.Upgrade);
        Assert.Null(gear.Instance);
        Assert.Equal((900, EquipmentParts.Weapon, 2, 4, 30), (copy.Code, copy.Part, copy.Rarity, copy.Grade, copy.MinimumLevel));
        Assert.Same(gear.Stats, copy.Stats);
        Assert.Equal(gear.Path, copy.Path);
    }

    [Fact]
    public void TheDefaultStateGoesBackToTheSharedDefinition()
    {
        ItemInfo gear = Gear(900);
        ItemInfo copy = gear.WithInstance(new EquipInstance(3));
        Assert.Same(gear, copy.WithInstance(null));
        Assert.Same(gear, copy.WithInstance(new EquipInstance()));
        Assert.Same(gear, gear.WithInstance(null));
        Assert.Same(gear, gear.WithInstance(new EquipInstance()));
    }

    [Fact]
    public void CopyingACopyStillPointsAtTheOriginalDefinition()
    {
        ItemInfo gear = Gear(900);
        ItemInfo first = gear.WithInstance(new EquipInstance(1));
        ItemInfo second = first.WithInstance(new EquipInstance(2));
        Assert.Same(gear, second.Definition);
        Assert.Equal(2, second.Instance.Upgrade);
        Assert.Equal(1, first.Instance.Upgrade);   // 前一份没被改
    }

    [Fact]
    public void ThePassedStateIsCopiedSoLaterEditsDoNotLeakIn()
    {
        ItemInfo gear = Gear(900);
        var state = new EquipInstance(4);
        ItemInfo copy = gear.WithInstance(state);
        state.Upgrade = 9;
        Assert.Equal(4, copy.Instance.Upgrade);
    }

    [Fact]
    public void StateOrDefaultHandsOutAFreshCopyEachTime()
    {
        ItemInfo copy = Gear(900).WithInstance(new EquipInstance(4));
        EquipInstance a = copy.StateOrDefault;
        a.Upgrade = 30;
        Assert.Equal(4, copy.Instance.Upgrade);
        Assert.Equal(4, copy.StateOrDefault.Upgrade);
        Assert.NotSame(copy.StateOrDefault, copy.StateOrDefault);
    }

    [Fact]
    public void StackableItemsNeverGetAnInstance()
    {
        ItemInfo stack = Stack(910);
        Assert.Same(stack, stack.WithInstance(new EquipInstance(3)));
        Assert.Null(stack.Instance);
    }

    [Fact]
    public void TwoCopiesOfTheSameStateAreDifferentObjectsWithEqualStates()
    {
        ItemInfo gear = Gear(900);
        ItemInfo a = gear.WithInstance(new EquipInstance(3));
        ItemInfo b = gear.WithInstance(new EquipInstance(3));
        Assert.NotSame(a, b);
        Assert.Equal(a.Instance, b.Instance);
        Assert.Equal(a.Code, b.Code);
    }

    [Fact]
    public void ACopyWorksInTheBagAsItsOwnStackAndKeepsItsCode()
    {
        var bag = new Inventory();
        ItemInfo gear = Gear(900);
        ItemInfo plus3 = gear.WithInstance(new EquipInstance(3));
        Assert.Equal(1, bag.Add(plus3, 1));
        Assert.Equal(1, bag.Add(gear, 1));
        Assert.Equal(2, bag.CountOf(900));
        IReadOnlyList<ItemStack> tab = bag.Tab(InventoryTab.Equipment);
        Assert.Equal(2, tab.Count);   // 装备堆叠上限 1:不会合并
        Assert.Same(plus3, tab[0].Info);
        Assert.Same(gear, tab[1].Info);
    }
}
