using Dnf.Data;

namespace Dnf.Data.Tests;

public class PvfPathTests
{
    [Theory]
    [InlineData("Npc\\Seria.NPC", "npc/seria.npc")]
    [InlineData("map/./elvengard//(r)gate.map", "map/elvengard/(r)gate.map")]
    [InlineData("a/b/../c/./d.ani", "a/c/d.ani")]
    [InlineData("/leading/slash.ani", "leading/slash.ani")]
    [InlineData("../../above/root.ani", "above/root.ani")]
    [InlineData("", "")]
    public void Normalize_LowercasesAndFoldsDots(string input, string expected)
    {
        Assert.Equal(expected, PvfPath.Normalize(input));
    }

    [Theory]
    [InlineData("npc/postbox.npc", "Animation/PostBox.ani", "npc/animation/postbox.ani")]
    [InlineData("npc/postbox.npc", "Animation/BlueMarble/off_dice_dot.ani", "npc/animation/bluemarble/off_dice_dot.ani")]
    [InlineData("monster/x/y.mob", "../animation/z.ani", "monster/animation/z.ani")]
    [InlineData("npc/postbox.npc", "/map/villageattack/animation/postbox.ani", "map/villageattack/animation/postbox.ani")]
    [InlineData("top.npc", "A.ani", "a.ani")]
    public void ResolveRelativeTo_UsesTheScriptDirectory(string script, string relative, string expected)
    {
        Assert.Equal(expected, PvfPath.ResolveRelativeTo(script, relative));
    }
}
