using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>物品脚本里的文字引用(种类编号 + 键,证据 E-121):合成的令牌流,键与编号是虚构的。</summary>
public class ItemTextKeyTests
{
    private static ItemInfo Parse(TokenStreamBuilder b, ItemKind kind)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, kind, 7, "equipment/x.equ");
    }

    [Fact]
    public void NameExplainBasicExplainAndFlavorKeysAreRead()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[name]").Keyed(3, "name_1");
        b.Tag("[explain]").Keyed(3, "explain_1");
        b.Tag("[basic explain]").Keyed(3, "basic_explain_1");
        b.Tag("[flavor text]").Keyed(3, "flavor_text_1");

        ItemInfo item = Parse(b, ItemKind.Equipment);

        Assert.Equal("name_1", item.NameKey);
        Assert.Equal("explain_1", item.ExplainKey);
        Assert.Equal("basic_explain_1", item.BasicExplainKey);
        Assert.Equal("flavor_text_1", item.FlavorKey);
        Assert.Equal(3, item.TextKind);
    }

    [Fact]
    public void MissingKeysStayNull()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[name]").Keyed(13, "name_2");

        ItemInfo item = Parse(b, ItemKind.Stackable);

        Assert.Equal("name_2", item.NameKey);
        Assert.Null(item.ExplainKey);
        Assert.Null(item.BasicExplainKey);
        Assert.Null(item.FlavorKey);
        Assert.Equal(13, item.TextKind);
    }

    [Fact]
    public void TheKindComesFromTheNameReferenceEvenWhenAnExplainComesFirst()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[explain]").Keyed(3, "explain_3");
        b.Tag("[name]").Keyed(13, "name_3");

        ItemInfo item = Parse(b, ItemKind.Stackable);

        Assert.Equal(13, item.TextKind);
    }

    [Fact]
    public void WithoutANameTheFirstReferenceDecidesTheKind()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[explain]").Keyed(13, "explain_4");
        b.Tag("[flavor text]").Keyed(3, "flavor_4");

        ItemInfo item = Parse(b, ItemKind.Stackable);

        Assert.Null(item.NameKey);
        Assert.Equal(13, item.TextKind);
    }

    [Fact]
    public void NoTextReferencesLeaveTheKindUnset()
    {
        ItemInfo item = Parse(new TokenStreamBuilder().Tag("[grade]").Int(1), ItemKind.Equipment);
        Assert.Equal(-1, item.TextKind);
        Assert.Null(item.NameKey);
    }
}
