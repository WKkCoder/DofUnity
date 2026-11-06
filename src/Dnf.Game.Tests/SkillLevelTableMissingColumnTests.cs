using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// "没有这一列"(列号 -1,M105 的对象伤害缺列语义)在按等级取值表上的读法:负列号一律读 0,绝不滑到相邻的列或上一级的最后一列。
/// 表是合成的:3 列 2 级,第 1 级 (11, 12, 13)、第 2 级 (21, 22, 23)。
/// </summary>
public class SkillLevelTableMissingColumnTests
{
    private static SkillLevelTable Table()
    {
        return new SkillLevelTable(3, new[] { 11, 12, 13, 21, 22, 23 });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]       // 等级小于 1 取第 1 级那一行
    [InlineData(99)]      // 等级超出表长取最后一级那一行
    public void ANegativeColumnReadsZeroAtEveryLevelNeverANeighbouringColumn(int level)
    {
        SkillLevelTable t = Table();
        Assert.Equal(0, t.Get(level, -1));
        Assert.Equal(0, t.Get(level, -7));
        Assert.Equal(0, t.Get(level, int.MinValue));
    }

    [Fact]
    public void TheRealColumnsAroundTheMissingOneStillReadTheirOwnLevel()
    {
        // 反例:负列号读 0 不能带坏正常的读法——第 0 列与最后一列取本级的数,超出列数的也是 0
        SkillLevelTable t = Table();
        Assert.Equal((11, 13), (t.Get(1, 0), t.Get(1, 2)));
        Assert.Equal((21, 23), (t.Get(2, 0), t.Get(2, 2)));
        Assert.Equal(0, t.Get(1, 3));
        Assert.Equal(0, SkillLevelTable.Empty.Get(1, 0));
    }
}
