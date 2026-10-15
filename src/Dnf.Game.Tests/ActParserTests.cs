using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// .act 语法树解析器(ActParser)的测试。规则依据 E-050(全库统计 + 两份独立实现对拍);
/// 脚本内容全是合成的,不含原版内容。
/// </summary>
public sealed class ActParserTests
{
    private static ScriptValue I(int v) => new(ScriptValueKind.Int, v, null);

    private static ScriptValue S(string s) => new(ScriptValueKind.String, 0, s);

    /// <summary>一行:标签加值;值可以是 int、string(当字符串值)或现成的 ScriptValue。</summary>
    private static ScriptLine L(string tag, params object[] values) =>
        new(tag, Array.ConvertAll(values, v => v switch
        {
            int i => I(i),
            string text => S(text),
            ScriptValue sv => sv,
            _ => throw new ArgumentException("不支持的值类型"),
        }));

    [Fact]
    public void BlocksNestAndLeavesStayLeaves()
    {
        ActScript s = ActParser.Parse(new[]
        {
            L("[MOTION]"), L("[BASE ANI]", S("a.ani")), L("[/MOTION]"),
            L("[TRIGGER]"), L("[FRAME]", 2, 2), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET SPEED]"), L("[X AXIS]", 400), L("[/SET SPEED]"), L("[/BEHAVIOR]"),
        });

        Assert.True(s.Clean);
        Assert.Equal(3, s.Roots.Count);
        Assert.All(s.Roots, r => Assert.True(r.IsBlock));
        ActNode trigger = s.Roots[1];
        Assert.Equal(new[] { "[FRAME]", "[DO BEHAVIOR]", "[ME]" }, trigger.Children.Select(c => c.Tag).ToArray());
        Assert.Equal(2, trigger.Find("[FRAME]")!.IntAt(1));
        ActNode speed = s.Roots[2].Find("[SET SPEED]")!;
        Assert.True(speed.IsBlock);
        Assert.Equal("[/SET SPEED]", speed.Closer);
        Assert.Equal(400, speed.Find("[X AXIS]")!.IntAt(0));
        Assert.Equal("a.ani", s.Roots[0].Find("[BASE ANI]")!.TextAt(0));
    }

    [Fact]
    public void ABlockTagWithoutACloserIsALeaf()
    {
        // [TRIGGER] 带值、又没有闭合:是行为里的叶子命令;外层 [BEHAVIOR] 照常闭合。
        ActScript s = ActParser.Parse(new[]
        {
            L("[BEHAVIOR]"), L("[TRIGGER]", 3), L("[ON]"), L("[/BEHAVIOR]"),
        });

        ActNode behavior = Assert.Single(s.Roots);
        Assert.True(behavior.IsBlock);
        Assert.Equal(2, behavior.Children.Count);
        Assert.False(behavior.Children[0].IsBlock);
        Assert.Equal("[TRIGGER]", behavior.Children[0].Tag);
        Assert.Equal(3, behavior.Children[0].IntAt(0));
    }

    [Fact]
    public void ClosingAnOuterBlockSwallowsUnclosedInnerTags()
    {
        ActScript s = ActParser.Parse(new[]
        {
            L("[BEHAVIOR]"), L("[SET ACTION]"), L("[STAND]"), L("[NOW]"), L("[/BEHAVIOR]"), L("[MOTION]"), L("[/MOTION]"),
        });

        Assert.Equal(2, s.Roots.Count);
        Assert.Equal(new[] { "[SET ACTION]", "[STAND]", "[NOW]" }, s.Roots[0].Children.Select(c => c.Tag).ToArray());
        Assert.All(s.Roots[0].Children, c => Assert.False(c.IsBlock));
    }

    [Fact]
    public void NestedSameNameBlocksPairWithTheNearestOpener()
    {
        ActScript s = ActParser.Parse(new[]
        {
            L("[IF]", S("a")), L("[=]", 1), L("[IF]", S("b")), L("[<]", 2), L("[/IF]"), L("[>]", 3), L("[/IF]"),
        });

        ActNode outer = Assert.Single(s.Roots);
        Assert.Equal(new[] { "[=]", "[IF]", "[>]" }, outer.Children.Select(c => c.Tag).ToArray());
        Assert.True(outer.Children[1].IsBlock);
        Assert.Equal(new[] { "[<]" }, outer.Children[1].Children.Select(c => c.Tag).ToArray());
    }

    [Fact]
    public void AMisspelledCloserPairsWithTheInnermostBlockCapableTag()
    {
        // [/INDEX] 与任何开标签都对不上,名字本身也不是块标签:关闭最内层能做块的 [IS INDEX](叶子 [STAND] 不算)。
        ActScript s = ActParser.Parse(new[]
        {
            L("[CHECKUP]"), L("[IS INDEX]", 5, 6), L("[/INDEX]"), L("[HP]", 1), L("[/CHECKUP]"),
        });

        Assert.Equal(1, s.RepairedClosers);
        Assert.Equal(0, s.StrayClosers);
        Assert.False(s.Clean);
        ActNode checkup = Assert.Single(s.Roots);
        Assert.Equal(new[] { "[IS INDEX]", "[HP]" }, checkup.Children.Select(c => c.Tag).ToArray());
        Assert.True(checkup.Children[0].IsBlock);
        Assert.Equal("[/INDEX]", checkup.Children[0].Closer);
        Assert.Empty(checkup.Children[0].Children);
    }

    [Fact]
    public void AnExtraCloserWithAKnownBlockNameIsDropped()
    {
        ActScript s = ActParser.Parse(new[]
        {
            L("[BEHAVIOR]"), L("[DESTROY]"), L("[/BEHAVIOR]"), L("[/BEHAVIOR]"),
        });

        Assert.Equal(1, s.StrayClosers);
        Assert.Equal(0, s.RepairedClosers);
        ActNode behavior = Assert.Single(s.Roots);
        Assert.Equal(new[] { "[DESTROY]" }, behavior.Children.Select(c => c.Tag).ToArray());
    }

    [Fact]
    public void MisspelledCloserWithNoBlockToCloseIsStray()
    {
        ActScript s = ActParser.Parse(new[] { L("[SET ACTION]"), L("[STAND]"), L("[/NOWHERE]") });

        Assert.Equal(1, s.StrayClosers);
        Assert.Equal(2, s.Roots.Count);
    }

    [Fact]
    public void TagsAreCaseSensitive()
    {
        ActScript s = ActParser.Parse(new[]
        {
            L("[BEHAVIOR]"), L("[HOLD]"), L("[hold]"), L("[/BEHAVIOR]"),
        });

        ActNode behavior = Assert.Single(s.Roots);
        Assert.NotNull(behavior.Find("[HOLD]"));
        Assert.NotNull(behavior.Find("[hold]"));
        Assert.Single(behavior.FindAll("[HOLD]"));
        Assert.False(behavior.Children[0].Is("[hold]"));
    }

    [Fact]
    public void ValuesBeforeTheFirstTagAreCountedNotLost()
    {
        ActScript s = ActParser.Parse(new[] { L(null!, 7, 8), L("[MOTION]"), L("[/MOTION]") });

        Assert.Equal(2, s.LeadingValues);
        Assert.Single(s.Roots);
        Assert.False(s.Clean);
    }

    [Fact]
    public void IntAtTruncatesFloatsAndFallsBackWhenMissing()
    {
        var floatValue = new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(-2.9f), null);
        ActScript s = ActParser.Parse(new[] { L("[X]", floatValue, I(5)) });

        ActNode x = Assert.Single(s.Roots);
        Assert.Equal(-2, x.IntAt(0));
        Assert.Equal(5, x.IntAt(1));
        Assert.Equal(99, x.IntAt(2, 99));
        Assert.Null(x.TextAt(2));
    }

    [Fact]
    public void BlockCapableListHasTheSixtyEightKnownTags()
    {
        Assert.True(ActParser.IsBlockCapable("[TRIGGER]"));
        Assert.True(ActParser.IsBlockCapable("[SET MOVABLE AREA]"));
        Assert.False(ActParser.IsBlockCapable("[SET ACTION]"));
        Assert.False(ActParser.IsBlockCapable(null));
    }
}
