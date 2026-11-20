using System.Text;
using Dnf.Data;
using Dnf.Play;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本选择策略的显示名解析(<see cref="OnlineDungeonSelectPolicy.ResolveScriptDisplayName"/>)第 8 轮缺口测试:
/// 脚本 [name] 行里"只有普通字符串值才按默认文字种类去查",括号词/分隔符/种类对不上的"种类 + 键"值即使带着一个恰好存在的键也不算名字。
/// 现有覆盖(<c>OnlineDungeonSelectTests</c> 与 <c>OnlineDungeonTypesGapR3AssetsTests</c>)里 [name] 行只出现过 Keyed 与 String 两种值,
/// 带文字的非字符串值(Paren / Separator)从来没出现过。全部用合成脚本行与内存文字表,不碰真实数据。
/// </summary>
public class OnlineDungeonTypesGapR8NameTests
{
    /// <summary>
    /// 内存文字表:副本种类里 str_key → FromStringKey、paren_key → FromParenKey、sep_key → FromSeparatorKey、keyed_key → FromKeyedKey;
    /// 世界地图种类里只有 map_key → FromMapKey(所以 keyed_key 在世界地图种类里查不到)。
    /// </summary>
    private static WorldTexts SyntheticTexts()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dungeon/dungeon.kor.str"] = Encoding.ASCII.GetBytes(
                "str_key>FromStringKey\r\nparen_key>FromParenKey\r\nsep_key>FromSeparatorKey\r\nkeyed_key>FromKeyedKey\r\n"),
            ["worldmap/worldmap.kor.str"] = Encoding.ASCII.GetBytes("map_key>FromMapKey\r\n"),
        };
        return new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));
    }

    private static ScriptLine NameLine(params ScriptValue[] values) => new ScriptLine("[name]", values);

    /// <summary>
    /// 保护:显示名回退只认"普通字符串值"——[name] 行里的括号词、分隔符、种类对不上的 Keyed 值,即使 Text 恰好是文字表里存在的键,也不会被拿去按默认种类查;
    /// 它们之前出现时被跳过,真正的字符串值才决定结果;空串/空引用的字符串值同样被跳过。
    /// 推导:A/B/C 三行都只有一个非字符串值 → 没有任何值可查 → null;D 行前面三个非字符串值(各带存在的键)被跳过,
    /// 取第四个字符串值 str_key → FromStringKey;E 行前两个字符串值 Text 为空 → 跳过,取第三个;F 行字符串值的键不在表里 → null。
    /// </summary>
    [Fact]
    public void DisplayNameFallbackLooksUpOnlyPlainStringValuesWithTheDefaultKind()
    {
        WorldTexts texts = SyntheticTexts();
        const int defaultKind = StrLibrary.KindDungeon;

        // 基线:字符串值 str_key 在默认种类(副本)里有文字
        Assert.Equal("FromStringKey", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[] { NameLine(new ScriptValue(ScriptValueKind.String, 0, "str_key")) }, defaultKind, texts));

        // A:只有括号词,Text 是副本种类里存在的键 → 不是名字
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[] { NameLine(new ScriptValue(ScriptValueKind.Paren, 0, "paren_key")) }, defaultKind, texts));

        // B:只有分隔符,Text 是副本种类里存在的键 → 不是名字
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[] { NameLine(new ScriptValue(ScriptValueKind.Separator, 0, "sep_key")) }, defaultKind, texts));

        // C:Keyed 值自带种类(世界地图),键 keyed_key 在世界地图里查不到;它在默认种类里虽然有文字,但 Keyed 值不走默认种类 → null
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[] { NameLine(new ScriptValue(ScriptValueKind.Keyed, StrLibrary.KindWorldMap, "keyed_key")) }, defaultKind, texts));

        // D:整数、括号词、分隔符、查不到的 Keyed 值都排在字符串值前面 → 全部跳过,取字符串值
        Assert.Equal("FromStringKey", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[]
            {
                NameLine(
                    new ScriptValue(ScriptValueKind.Int, 7, null),
                    new ScriptValue(ScriptValueKind.Paren, 0, "paren_key"),
                    new ScriptValue(ScriptValueKind.Separator, 0, "sep_key"),
                    new ScriptValue(ScriptValueKind.Keyed, StrLibrary.KindWorldMap, "keyed_key"),
                    new ScriptValue(ScriptValueKind.String, 0, "str_key")),
            },
            defaultKind,
            texts));

        // E:空串、空引用的字符串值被跳过,第三个才是有效值
        Assert.Equal("FromStringKey", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[]
            {
                NameLine(
                    new ScriptValue(ScriptValueKind.String, 0, string.Empty),
                    new ScriptValue(ScriptValueKind.String, 0, null),
                    new ScriptValue(ScriptValueKind.String, 0, "str_key")),
            },
            defaultKind,
            texts));

        // F:字符串值的键不在文字表里 → 没有名字
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(
            new[] { NameLine(new ScriptValue(ScriptValueKind.String, 0, "no_such_key")) }, defaultKind, texts));
    }
}
