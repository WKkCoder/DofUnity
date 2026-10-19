using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 角色 .chr 的 [weapon hit info](证据 E-077):每种武器一项,依次是两个关键字、一个整数、三个浮点。
/// 数值全是合成的,与原版数据无关。
/// </summary>
public class WeaponHitInfoTests
{
    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static CharacterDef Chr(params ScriptValue[] weaponHitInfo)
    {
        return new CharacterDef(0, "character/test/mini.chr", new[]
        {
            new ScriptLine("[job]", new[] { S("[mini]") }),
            new ScriptLine("[weapon hit info]", weaponHitInfo),
            new ScriptLine("[/weapon hit info]", Array.Empty<ScriptValue>()),
        });
    }

    [Fact]
    public void EachSixValuesFormOneWeaponEntryIndexedBySubType()
    {
        CharacterDef def = Chr(
            S("[cut]"), S("[blood]"), I(11), F(0.5f), F(-0.25f), F(0f),
            S("[blow]"), S("[no blood]"), I(22), F(1.5f), F(0.125f), F(-1f));

        Assert.Equal(2, def.WeaponHitInfos.Length);
        Assert.True(def.TryGetWeaponHitInfo(0, out WeaponHitInfo a));
        Assert.Equal("[cut]", a.HitKind);
        Assert.True(a.Blood);
        Assert.Equal(11, a.StunMs);
        Assert.Equal(0.5f, a.DelayRate);
        Assert.Equal(-0.25f, a.Extra1);
        Assert.Equal(0f, a.Extra2);

        Assert.True(def.TryGetWeaponHitInfo(1, out WeaponHitInfo b));
        Assert.Equal("[blow]", b.HitKind);
        Assert.False(b.Blood);
        Assert.Equal(22, b.StunMs);
        Assert.Equal(1.5f, b.DelayRate);
        Assert.Equal(-1f, b.Extra2);

        Assert.False(def.TryGetWeaponHitInfo(2, out _));
        Assert.False(def.TryGetWeaponHitInfo(-1, out _));
    }

    [Fact]
    public void WholeNumbersInTheFloatPositionsAreAccepted()
    {
        // 文件里的浮点 1 与 0 可能存成整数标记
        CharacterDef def = Chr(S("[cut]"), S("[blood]"), I(33), I(1), I(0), I(0));
        Assert.True(def.TryGetWeaponHitInfo(0, out WeaponHitInfo a));
        Assert.Equal(33, a.StunMs);
        Assert.Equal(1f, a.DelayRate);
        Assert.Equal(0f, a.Extra1);
    }

    [Fact]
    public void ATrailingIncompleteEntryIsDroppedAndAMissingTagGivesAnEmptyTable()
    {
        CharacterDef partial = Chr(S("[cut]"), S("[blood]"), I(44), F(1f), F(0f), F(0f), S("[cut]"), S("[blood]"), I(55));
        Assert.Single(partial.WeaponHitInfos);

        var none = new CharacterDef(0, "character/test/none.chr", new[] { new ScriptLine("[job]", new[] { S("[mini]") }) });
        Assert.Empty(none.WeaponHitInfos);
        Assert.False(none.TryGetWeaponHitInfo(0, out _));
    }

    [Fact]
    public void ParsingStopsAtAnEntryWhoseKeywordsAreNotStrings()
    {
        CharacterDef def = Chr(S("[cut]"), S("[blood]"), I(66), F(1f), F(0f), F(0f), I(1), I(2), I(3), F(1f), F(0f), F(0f));
        Assert.Single(def.WeaponHitInfos);
    }
}
