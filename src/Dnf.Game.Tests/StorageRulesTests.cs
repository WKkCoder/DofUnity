using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 仓库的固定数值与扩容边界(证据:服务端 CUser::SetInventory 0x0864d160、Dispatcher_UpgradeCargo 0x0822a0a4、CCargo::isUpgradable 0x0850c698、CAccountCargo::CheckValidSlot 0x0828a554 的静态读码,
/// 见交付笔记)。期望值是手算:个人仓库 8 格起、每次扩 16、最多 152,所以 8 → 24 → 40 → 56 → 72 → 88 → 104 → 120 → 136 → 152 共 9 次;账号金库最多 56 格。
/// </summary>
public class StorageRulesTests
{
    [Fact]
    public void FixedNumbersAreTheServerOnes()
    {
        Assert.Equal(8, StorageRules.PersonalInitialCapacity);
        Assert.Equal(16, StorageRules.PersonalExpandStep);
        Assert.Equal(152, StorageRules.PersonalMaxSlots);
        Assert.Equal(56, StorageRules.AccountMaxSlots);
        Assert.Equal(152, StorageRules.MaxSlotsOf(StorageKind.Personal));
        Assert.Equal(56, StorageRules.MaxSlotsOf(StorageKind.Account));
    }

    [Theory]
    [InlineData(0, false)]          // 容量为 0 的仓库从来没有初始化,不能扩(isUpgradable 要求现有容量不为 0)
    [InlineData(-8, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(24, true)]
    [InlineData(135, true)]
    [InlineData(136, true)]         // 136 + 16 = 152:正好到上限,允许
    [InlineData(137, false)]        // 137 + 16 = 153:超过上限一格,不许
    [InlineData(144, false)]
    [InlineData(152, false)]
    [InlineData(200, false)]
    public void PersonalExpansionBoundaries(int capacity, bool expected)
    {
        Assert.Equal(expected, StorageRules.CanExpandPersonal(capacity));
    }

    [Fact]
    public void NineExpansionsTakeEightTo152()
    {
        int capacity = StorageRules.PersonalInitialCapacity;
        int expansions = 0;
        while (StorageRules.CanExpandPersonal(capacity))
        {
            capacity += StorageRules.PersonalExpandStep;
            expansions++;
        }
        Assert.Equal(9, expansions);
        Assert.Equal(152, capacity);
    }

    [Fact]
    public void EveryResultHasItsOwnMessageAndUnknownThrows()
    {
        var seen = new HashSet<string>();
        foreach (StorageResult result in Enum.GetValues<StorageResult>())
        {
            string message = StorageRules.MessageOf(result);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.True(seen.Add(message), "说明重复:" + result);
        }
        Assert.Equal("完成。", StorageRules.MessageOf(StorageResult.Ok));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageRules.MessageOf((StorageResult)999));
    }
}
