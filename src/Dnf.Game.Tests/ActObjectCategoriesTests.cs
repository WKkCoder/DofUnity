using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 动作脚本运行时类别名表(M71,E-171;与客户端 0x0129c140 / 0x01293f10 一致,静态读码,非 REA)。
/// </summary>
public sealed class ActObjectCategoriesTests
{
    [Fact]
    public void KnownNamesResolveToTheirTableIndex()
    {
        Assert.Equal(0, ActObjectCategories.Resolve("human"));
        Assert.Equal(10, ActObjectCategories.Resolve("[dragon]"));
        Assert.Equal(ActObjectCategories.AnchorCategoryId, ActObjectCategories.Resolve("fixture"));
        Assert.Equal(ActObjectCategories.InvalidId, ActObjectCategories.Resolve("not-a-category"));
    }

    [Fact]
    public void ValidIdsExcludeTheSentinel()
    {
        Assert.True(ActObjectCategories.IsValidId(0));
        Assert.True(ActObjectCategories.IsValidId(43));
        Assert.False(ActObjectCategories.IsValidId(ActObjectCategories.InvalidId));
        Assert.False(ActObjectCategories.IsValidId(-1));
    }
}
