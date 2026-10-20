using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 角色 .chr 的 [weapon wav] 块、怪物 .mob 的五种声音标签、鬼剑士的固定语音标签(证据 E-089)。声音标签与数值全是合成的。
/// </summary>
public class WeaponSoundTests
{
    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptLine Block(params string[] keys)
    {
        var values = new ScriptValue[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            values[i] = S(keys[i]);
        }
        return new ScriptLine("[weapon wav]", values);
    }

    private static ScriptLine End() => new ScriptLine("[/weapon wav]", Array.Empty<ScriptValue>());

    private static CharacterDef Chr(params ScriptLine[] extra)
    {
        var lines = new List<ScriptLine> { new ScriptLine("[job]", new[] { S("[mini]") }) };
        lines.AddRange(extra);
        return new CharacterDef(0, "character/test/mini.chr", lines.ToArray());
    }

    [Fact]
    public void EachBlockIsOneWeaponWithFourKeysInOrder()
    {
        CharacterDef def = Chr(
            Block("R_TEST_SWING_A", "TEST_SWING_B", "R_TEST_HIT_A", "TEST_HIT_B"), End(),
            Block("R_OTHER_SWING_A", "OTHER_SWING_B", "R_OTHER_HIT_A", "OTHER_HIT_B"), End());

        Assert.Equal(2, def.WeaponSounds.Length);
        Assert.True(def.TryGetWeaponSound(0, out WeaponSound a));
        Assert.Equal(("R_TEST_SWING_A", "TEST_SWING_B", "R_TEST_HIT_A", "TEST_HIT_B"), (a.SwingA, a.SwingB, a.HitA, a.HitB));
        Assert.True(def.TryGetWeaponSound(1, out WeaponSound b));
        Assert.Equal("OTHER_HIT_B", b.HitB);
        Assert.False(def.TryGetWeaponSound(2, out _));
        Assert.False(def.TryGetWeaponSound(-1, out _));
    }

    [Fact]
    public void AnEmptyBlockKeepsItsSlotButHasNoSound()
    {
        // 第三种武器没有声音:块是空的,但它占着下标,后面的武器不能往前挪
        CharacterDef def = Chr(
            Block("A0", "B0", "H0", "J0"), End(),
            Block(), End(),
            Block("A2", "B2", "H2", "J2"), End());

        Assert.Equal(3, def.WeaponSounds.Length);
        Assert.False(def.TryGetWeaponSound(1, out WeaponSound none));
        Assert.True(none.IsEmpty);
        Assert.True(def.TryGetWeaponSound(2, out WeaponSound c));
        Assert.Equal("A2", c.SwingA);
    }

    [Fact]
    public void ShortBlocksLeaveTheMissingKeysNullAndNumbersAreIgnored()
    {
        var withNumber = new ScriptLine("[weapon wav]", new[] { S("ONLY_SWING"), I(7) });
        CharacterDef def = Chr(withNumber, End());
        Assert.True(def.TryGetWeaponSound(0, out WeaponSound s));
        Assert.Equal("ONLY_SWING", s.SwingA);
        Assert.Null(s.SwingB);
        Assert.Null(s.HitA);
        Assert.Null(s.HitB);
        Assert.False(s.IsEmpty);
    }

    [Fact]
    public void WithoutTheTagTheTableIsEmpty()
    {
        CharacterDef def = Chr();
        Assert.Empty(def.WeaponSounds);
        Assert.False(def.TryGetWeaponSound(0, out _));
        Assert.Empty(WeaponSound.ParseAll(null));
    }

    [Fact]
    public void MonsterSoundTagsAreReadAsFirstStrings()
    {
        var def = new MonsterDef(1, "monster/test/mini.mob", new[]
        {
            new ScriptLine("[name]", new[] { S("mini") }),
            new ScriptLine("[appear sound]", new[] { S("MINI_APPEAR") }),
            new ScriptLine("[damage sound]", new[] { S("R_MINI_DMG") }),
            new ScriptLine("[die sound]", new[] { S("R_MINI_DIE") }),
            new ScriptLine("[attack sound]", new[] { S("MINI_ATK") }),
            new ScriptLine("[ambient sound]", new[] { S("MINI_AMB") }),
        });
        Assert.Equal(("MINI_APPEAR", "R_MINI_DMG", "R_MINI_DIE", "MINI_ATK", "MINI_AMB"),
            (def.AppearSound, def.DamageSound, def.DieSound, def.AttackSound, def.AmbientSound));

        var bare = new MonsterDef(2, "monster/test/bare.mob", new[] { new ScriptLine("[name]", new[] { S("bare") }) });
        Assert.Null(bare.AppearSound);
        Assert.Null(bare.DamageSound);
        Assert.Null(bare.DieSound);
        Assert.Null(bare.AttackSound);
        Assert.Null(bare.AmbientSound);
    }

    [Fact]
    public void SwordmanVoiceKeysAreTheFixedClientTags()
    {
        // 这些标签写死在客户端里;测试钉住拼写,免得改错
        Assert.Equal("R_SM_DMG", SwordmanVoice.Hurt);
        Assert.Equal("SM_DIE", SwordmanVoice.Die);
        Assert.Equal("R_SM_ATK", SwordmanVoice.Attack);
        Assert.Equal("R_SM_JUMP_ATK", SwordmanVoice.JumpAttack);
        Assert.Equal("SM_JUMP", SwordmanVoice.Jump);
        Assert.Equal("SM_BACKSTEP", SwordmanVoice.Backstep);
        Assert.Equal("SM_THRUST", SwordmanVoice.Thrust);
        Assert.Equal("SM_NOMANA", SwordmanVoice.NoMana);
        Assert.Equal("SM_COOLTIME", SwordmanVoice.CoolTime);
        Assert.Equal("UPPER_SLASH_02", SwordmanVoice.UpperSlash);
        Assert.Equal("JUMP_START", SwordmanVoice.JumpStart);
        Assert.Equal("JUMP_END", SwordmanVoice.JumpEnd);
    }
}
