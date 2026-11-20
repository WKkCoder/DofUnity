using System.Text;
using Dnf.Data;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本选择策略的第三轮"缺口"测试(接在 <c>OnlineDungeonTypesGapRulesTests</c> 之后,不改上一轮文件):
/// 补"没有资源包时的默认行为"(是否有难度 3 的默认判定、显示名取不到)、"有没有难度 3"委托必须按解析出的编号被询问,
/// 以及 <see cref="OnlineDungeonSelectPolicy.ResolveScriptDisplayName"/> 对空入参、空元素与非 [name] 行的防御。
/// 全部用合成数据(内存里的文字表与脚本行),不碰真实脚本、编号或路径。
/// </summary>
public class OnlineDungeonTypesGapR3PolicyTests
{
    private static KeyValuePair<int, byte> Tier(int dungeonId, byte tier) => new KeyValuePair<int, byte>(dungeonId, tier);

    // 从上一轮 Rules 测试复制来的辅助(上一轮的类里是 private,按任务要求不改原文件,所以复制)。
    private static DungeonEntry CardEntry(int dungeonId, string path = null)
    {
        return new DungeonEntry(
            path ?? ("dungeon/test/gapr3-" + dungeonId + ".dgn"),
            "GAPR3" + dungeonId,
            new DungeonCard(dungeonId, 10, 10, 0, null, 0, 1));
    }

    /// <summary>内存文字表:键 right_key → RightName,wrong_key → WrongName(都登记在"副本"种类的文本表里)。</summary>
    private static WorldTexts SyntheticTexts()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dungeon/dungeon.kor.str"] = Encoding.ASCII.GetBytes("right_key>RightName\r\nwrong_key>WrongName\r\n"),
        };
        return new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));
    }

    private static ScriptValue Str(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    // ------------------------------------------------------------------------------------------
    // "有没有难度 3":默认判定与委托
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:既没有资源包、也没有显式"是否有难度 3"委托时,默认把每个副本都当作有难度 3——
    /// 许可档位 3 与 255 都能选到 3,档位 2/1 原样,表里没有条目为 0。
    /// 推导:cap = 有难度 3 ? 3 : 2;DifficultyLimitForTier = tier ≤ 0 ? 0 : min(tier, cap)。
    /// 默认若被改成"都没有难度 3",档位 3 与 255 都会被夹成 2。
    /// </summary>
    [Fact]
    public void DefaultUltimateCheckWithoutAssetsTreatsEveryDungeonAsHavingDifficultyThree()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { Tier(31, 3), Tier(32, 2), Tier(33, 1), Tier(34, 255), Tier(35, 0) }, hasSnapshot: true);
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasLocalData: _ => true);

        Assert.Equal(3, policy.DifficultyLimit(CardEntry(31))); // 档位 3,cap 3
        Assert.Equal(2, policy.DifficultyLimit(CardEntry(32)));
        Assert.Equal(1, policy.DifficultyLimit(CardEntry(33)));
        Assert.Equal(3, policy.DifficultyLimit(CardEntry(34))); // 档位 255 夹到 cap 3
        Assert.Equal(0, policy.DifficultyLimit(CardEntry(35))); // 显式 0 档
        Assert.Equal(0, policy.DifficultyLimit(CardEntry(36))); // 表里没有条目
        Assert.Equal(0, policy.DifficultyLimit(null));
    }

    /// <summary>
    /// 保护:最高可选难度要按"该副本自己"有没有难度 3 来夹——委托收到的是解析出的副本编号(不是条目、不是常量),
    /// 有难度 3 的副本档位 3 可选到 3,没有的被夹到 2;编号无效(负数/超 u16/空条目)时直接得 0,根本不去问委托。
    /// 推导:委托 id == 41 才有难度 3;41 → tier 3 → 3;42 → tier 3 夹到 2;43 → tier 2 → 2;44 → tier 1 → 1。
    /// </summary>
    [Fact]
    public void DifficultyLimitAsksTheUltimateDelegateWithTheResolvedDungeonId()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { Tier(41, 3), Tier(42, 3), Tier(43, 2), Tier(44, 1) }, hasSnapshot: true);
        var asked = new List<int>();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
            table,
            hasLocalData: _ => true,
            hasUltimate: id =>
            {
                asked.Add(id);
                return id == 41;
            });

        Assert.Equal(3, policy.DifficultyLimit(CardEntry(41)));
        Assert.Equal(2, policy.DifficultyLimit(CardEntry(42)));
        Assert.Equal(2, policy.DifficultyLimit(CardEntry(43)));
        Assert.Equal(1, policy.DifficultyLimit(CardEntry(44)));
        Assert.Equal(new[] { 41, 42, 43, 44 }, asked);

        Assert.Equal(0, policy.DifficultyLimit(CardEntry(-1)));
        Assert.Equal(0, policy.DifficultyLimit(CardEntry(65536)));
        Assert.Equal(0, policy.DifficultyLimit(null));
        Assert.Equal(new[] { 41, 42, 43, 44 }, asked); // 无效编号不触发委托
    }

    /// <summary>
    /// 保护:显式给的委托始终优先于默认判定——委托说"都没有难度 3"时,档位 3 与 255 也只能选到 2。
    /// (与上一个用例互补:那里默认是"都有",这里显式委托是"都没有"。)
    /// </summary>
    [Fact]
    public void ExplicitUltimateDelegateOverridesTheDefaultAndCapsAtTwo()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { Tier(51, 3), Tier(52, 255), Tier(53, 1) }, hasSnapshot: true);
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasLocalData: _ => true, hasUltimate: _ => false);

        Assert.Equal(2, policy.DifficultyLimit(CardEntry(51)));
        Assert.Equal(2, policy.DifficultyLimit(CardEntry(52)));
        Assert.Equal(1, policy.DifficultyLimit(CardEntry(53)));
    }

    // ------------------------------------------------------------------------------------------
    // 显示名:没有资源包时取不到
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:有文字表但没有资源包(读不到副本脚本)时,副本显示名与地区显示名都是 null,而不是抛空引用;
    /// 空条目同样是 null。
    /// </summary>
    [Fact]
    public void DisplayNamesAreNullWithoutAssetsEvenWhenTextsArePresent()
    {
        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
            table,
            texts: SyntheticTexts(),
            worldMapPath: "worldmap/test/gapr3.wld",
            hasLocalData: _ => true,
            hasUltimate: _ => true);

        Assert.Null(policy.EntryDisplayName(CardEntry(61)));
        Assert.Null(policy.EntryDisplayName(CardEntry(62, path: string.Empty)));
        Assert.Null(policy.EntryDisplayName(null));
        Assert.Null(policy.RegionDisplayName);
    }

    // ------------------------------------------------------------------------------------------
    // ResolveScriptDisplayName:入参防御、空元素、非 [name] 行
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:脚本行或文字表任一为空都直接返回 null(两个条件是"或",不是"且")——
    /// 只给脚本不给文字表、只给文字表不给脚本,都不能抛空引用。
    /// </summary>
    [Fact]
    public void ResolveScriptDisplayNameReturnsNullWhenEitherScriptOrTextsIsMissing()
    {
        WorldTexts texts = SyntheticTexts();
        ScriptLine[] doc = { new ScriptLine("[name]", new[] { Str("right_key") }) };

        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(null, StrLibrary.KindDungeon, texts));
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(doc, StrLibrary.KindDungeon, null));
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(null, StrLibrary.KindDungeon, null));
        Assert.Equal("RightName", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(doc, StrLibrary.KindDungeon, texts));
    }

    /// <summary>
    /// 保护:逐行找 [name] 时,空元素被跳过(不抛异常),非 [name] 的行即便带有能查到文字的字符串也不能被当成名字,
    /// 标签比较不分大小写;同一行里不是字符串的值、空字符串与查不到的键都被跳过,取第一个能查到文字的字符串;
    /// "种类 + 键"写法(Keyed)优先于普通字符串写法。
    /// </summary>
    [Fact]
    public void ResolveScriptDisplayNameSkipsNullLinesAndNonNameLinesAndPicksFirstResolvableString()
    {
        WorldTexts texts = SyntheticTexts();

        // 第 0 行是空元素:必须跳过而不是抛 NullReferenceException
        ScriptLine[] withNullLine =
        {
            null,
            new ScriptLine("[name]", new[] { Str("right_key") }),
        };
        Assert.Equal("RightName", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(withNullLine, StrLibrary.KindDungeon, texts));

        // [summary] 行里的 wrong_key 能查到文字,但这一行不是 [name],不能被取走
        ScriptLine[] otherTagFirst =
        {
            new ScriptLine("[summary]", new[] { Str("wrong_key") }),
            new ScriptLine("[name]", new[] { Str("right_key") }),
        };
        Assert.Equal("RightName", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(otherTagFirst, StrLibrary.KindDungeon, texts));

        // 只有非 [name] 行 → 没有名字
        ScriptLine[] noNameLine =
        {
            new ScriptLine("[summary]", new[] { Str("wrong_key") }),
            new ScriptLine(null, new[] { Str("wrong_key") }),
        };
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(noNameLine, StrLibrary.KindDungeon, texts));

        // 标签不分大小写
        ScriptLine[] upperTag = { new ScriptLine("[NAME]", new[] { Str("right_key") }) };
        Assert.Equal("RightName", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(upperTag, StrLibrary.KindDungeon, texts));

        // 同一行里:整数值、空字符串、查不到的键都跳过,取第一个能查到的字符串
        ScriptLine[] mixedValues =
        {
            new ScriptLine("[name]", new[]
            {
                new ScriptValue(ScriptValueKind.Int, 7, null),
                Str(string.Empty),
                Str("missing_key"),
                Str("right_key"),
                Str("wrong_key"),
            }),
        };
        Assert.Equal("RightName", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(mixedValues, StrLibrary.KindDungeon, texts));

        // 只有查不到的字符串 → null
        ScriptLine[] unresolvable = { new ScriptLine("[name]", new[] { Str("missing_key") }) };
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(unresolvable, StrLibrary.KindDungeon, texts));

        // Keyed 写法优先:同一行里普通字符串在前、Keyed 在后,仍取 Keyed 的文字
        ScriptLine[] keyedWins =
        {
            new ScriptLine("[name]", new[]
            {
                Str("wrong_key"),
                new ScriptValue(ScriptValueKind.Keyed, StrLibrary.KindDungeon, "right_key"),
            }),
        };
        Assert.Equal("RightName", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(keyedWins, StrLibrary.KindDungeon, texts));

        // 空文档 → null
        Assert.Null(OnlineDungeonSelectPolicy.ResolveScriptDisplayName(Array.Empty<ScriptLine>(), StrLibrary.KindDungeon, texts));
    }
}
