using System.Text;
using Dnf.Data;

namespace Dnf.Data.Tests;

public class ScriptDocumentTests
{
    // 字符串池:0 "[NPC]"  1 "[/NPC]"  2 "[field animation]"  3 "Animation/PostBox.ani"  4 "name_4115"  5 "(UP)"
    private static PvfStringTable Pool()
    {
        string[] items = { "[NPC]", "[/NPC]", "[field animation]", "Animation/PostBox.ani", "name_4115", "(UP)" };
        var list = new List<byte[]>();
        foreach (string s in items)
        {
            list.Add(Encoding.ASCII.GetBytes(s));
        }
        return new PvfStringTable(TestData.BuildStringTable(list));
    }

    private const byte Tag = (byte)TokenType.Tag;
    private const byte Int = (byte)TokenType.Int;
    private const byte Str = (byte)TokenType.String;

    [Fact]
    public void Parse_GroupsValuesUnderTheirTag()
    {
        byte[] data = TestData.BuildTokenScript(new (byte, int)[]
        {
            (Tag, 0), (Int, 2), (Int, 477), (Int, 160), (Int, 0), (Int, 32), (Int, 691), (Int, 163), (Int, 0), (Tag, 1),
            (Tag, 2), (Str, 3),
        });
        ScriptLine[] lines = ScriptDocument.Parse(data, Pool());

        Assert.Equal(3, lines.Length);
        Assert.True(lines[0].TagIs("[npc]"));                      // 标签比较不分大小写
        Assert.Equal(new[] { 2, 477, 160, 0, 32, 691, 163, 0 }, lines[0].Ints().ToArray());
        Assert.Equal("[/NPC]", lines[1].Tag);
        Assert.Empty(lines[1].Values);
        Assert.Equal("[field animation]", lines[2].Tag);
        Assert.Equal("Animation/PostBox.ani", lines[2].FirstString());
    }

    [Fact]
    public void Parse_KeepsValuesBeforeTheFirstTagOnATaglessLine()
    {
        byte[] data = TestData.BuildTokenScript(new (byte, int)[] { (Int, 1), (Str, 3), (Int, 2), (Str, 4) });
        ScriptLine[] lines = ScriptDocument.Parse(data, Pool());
        Assert.Single(lines);
        Assert.Null(lines[0].Tag);
        Assert.Equal(new[] { 1, 2 }, lines[0].Ints().ToArray());
        Assert.Equal("Animation/PostBox.ani", lines[0].FirstString());
    }

    [Fact]
    public void Parse_ReadsFloatsHexParensAndKeyedStrings()
    {
        int floatBits = BitConverter.SingleToInt32Bits(1.5f);
        byte[] data = TestData.BuildTokenScript(new (byte, int)[]
        {
            (Tag, 0), (4, floatBits), (3, 0x1F), (6, 5), (9, 8), (10, 4), (8, 0),
        });
        ScriptLine[] lines = ScriptDocument.Parse(data, Pool());
        ScriptValue[] v = lines[0].Values;
        Assert.Equal(5, v.Length);
        Assert.Equal(ScriptValueKind.Float, v[0].Kind);
        Assert.Equal(1.5f, v[0].Float);
        Assert.Equal(ScriptValueKind.Hex, v[1].Kind);
        Assert.Equal(0x1F, v[1].Int);
        Assert.Equal(ScriptValueKind.Paren, v[2].Kind);
        Assert.Equal("(UP)", v[2].Text);
        Assert.Equal(ScriptValueKind.Keyed, v[3].Kind);       // 9 + 10 合成一个值
        Assert.Equal(8, v[3].Int);
        Assert.Equal("name_4115", v[3].Text);
        Assert.Equal(ScriptValueKind.Separator, v[4].Kind);
        Assert.Equal(new[] { 0x1F }, lines[0].Ints().ToArray()); // Hex 也算数字,Float 不算
    }

    [Fact]
    public void Parse_OutOfRangeStringIndexGivesNullText()
    {
        byte[] data = TestData.BuildTokenScript(new (byte, int)[] { (Tag, 99), (Str, 100) });
        ScriptLine[] lines = ScriptDocument.Parse(data, Pool());
        Assert.Null(lines[0].Tag);
        Assert.Null(lines[0].Values[0].Text);
    }

    [Fact]
    public void Parse_RejectsDataThatIsNotATokenScript()
    {
        Assert.Throws<DnfFormatException>(() => ScriptDocument.Parse(new byte[] { 1, 2, 3 }, Pool()));
    }
}
