using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 副职业测试的合成数据:一份分解师脚本、等级上限表、旧版分解表,以及物品构造器。
/// 编号全是合成的(物品 900 起:装备 900 到 939、产物 948 到 970、便携分解器 980 起),数值是为了让期望值能手算而随手写的,不含原版内容。
/// </summary>
internal static class ExpertJobTestData
{
    public static ScriptValue I(int v) => new(ScriptValueKind.Int, v, null);

    public static ScriptValue F(float v) => new(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(v), null);

    public static ScriptValue S(string v) => new(ScriptValueKind.String, 0, v);

    /// <summary>一行:标签加值;值可以是 int、float、string(字符串值)或现成的 ScriptValue。</summary>
    public static ScriptLine L(string tag, params object[] values) =>
        new(tag, Array.ConvertAll(values, v => v switch
        {
            int i => I(i),
            float f => F(f),
            string text => S(text),
            ScriptValue sv => sv,
            _ => throw new ArgumentException("不支持的值类型"),
        }));

    /// <summary>经验表:每个阈值一组 "阈值 0 名字"。</summary>
    public static ScriptLine ExpTable(params int[] thresholds)
    {
        var values = new List<object>();
        foreach (int t in thresholds)
        {
            values.Add(t);
            values.Add(0);
            values.Add("lv" + t);
        }
        return L("[expertness exp]", values.ToArray());
    }

    /// <summary>附加产物行:组号、最低等级、最高等级、物品、权重、除数。</summary>
    public static object[] Rate(int group, int min, int max, int item, int weight, float divisor) => new object[] { group, min, max, item, weight, divisor };

    /// <summary>
    /// 分解师脚本(4 级,阈值 100、250、450、700):
    /// 基数 100;附加组 1(951 权重 6000 除数 10;952 权重 4000 除数 5)、组 2(953)、组 3(等级 10 到 20 的 954、30 到 40 的 955)、组 4(950 与 951);
    /// 大成功组 1(960)、组 2(961 权重 3000 除数 1;962 权重 7000 除数 4)、组 4(951);
    /// 结果行(机器档, 稀有度, 未鉴定):(0,1)→950×1.5 附加 1 大成功 1 百分数 10;(0,2)→950×2.0 附加 2 大成功 2 百分数 20;(0,3)→950×2.5 附加 3;
    /// (0,4)→950×1.0 附加 4 大成功 4;(1,1)→950×3.0 附加 1 大成功 1 百分数 30;(0,1,未鉴定)→949×0.5;重复的 (0,1) 行 948×9.0 不生效。
    /// </summary>
    public static ScriptLine[] DisjointerLines() => new[]
    {
        ExpTable(100, 250, 450, 700),
        L("[skill]", 901, 1, 902, 1),
        L("[clear quests]", 901, 902),
        L("[giveup cost]", 1000, 2000, 3000),
        L("[base const]", 100),
        L("[additional const]", 7),
        L("[additional result]", Concat(
            Rate(1, 1, 50, 951, 6000, 10.0f), Rate(1, 1, 50, 952, 4000, 5.0f),
            Rate(2, 1, 50, 953, 10000, 2.0f),
            Rate(3, 10, 20, 954, 5000, 4.0f), Rate(3, 30, 40, 955, 5000, 8.0f),
            Rate(4, 1, 50, 950, 5000, 2.0f), Rate(4, 1, 50, 951, 5000, 2.0f))),
        L("[big win result]", Concat(
            Rate(1, 1, 50, 960, 10000, 2.0f),
            Rate(2, 1, 50, 961, 3000, 1.0f), Rate(2, 1, 50, 962, 7000, 4.0f),
            Rate(4, 1, 50, 951, 10000, 2.0f))),
        L("[disjoint result]", Concat(
            Row(0, 1, 0, 950, 1.5f, 1, 1, 10),
            Row(0, 2, 0, 950, 2.0f, 2, 2, 20),
            Row(0, 3, 0, 950, 2.5f, 3, 0, 0),
            Row(0, 4, 0, 950, 1.0f, 4, 4, 0),
            Row(1, 1, 0, 950, 3.0f, 1, 1, 30),
            Row(0, 1, 1, 949, 0.5f, 0, 0, 0),
            Row(0, 1, 0, 948, 9.0f, 0, 0, 0))),
        L("[endurance reduce]", 1, 3),
        L("[endurance initial value]", 5),
        L("[gain exp]", 2, 4),
        L("[endurance repair cost]", 10000, 300, 15000, 300, 20000, 400),
        L("[upgrade cost]", 1, 0, 2, 30000, 3, 50000),
        L("[portable disjoint info]", 0, 30, 1, 60),
        L("[disjoint self service]", 10, 970, 2),
        L("[limit disjoint gold]", 5000),
        L("[compound rate]", 1, 2, 3),
        L("[/compound rate]"),
    };

    public static object[] Row(int grade, int rarity, int unidentified, int item, float factor, int additional, int bigWin, int percent)
        => new object[] { grade, rarity, unidentified, item, factor, additional, bigWin, percent };

    public static object[] Concat(params object[][] parts) => parts.SelectMany(p => p).ToArray();

    public static ExpertJobScript Disjointer() => ExpertJobScript.Parse(ExpertJobKind.Disjointer, DisjointerLines());

    /// <summary>等级上限:副职业等级 1 到 4 要求角色等级 10、10、20、30;分解大成功加成:键 1 为 0、键 2 为 5、键 3 为 10(键 = 机器档 + 1)。</summary>
    public static ExpertJobEtc Etc() => ExpertJobEtc.Parse(new[]
    {
        L("[expertjob level limit]", 1, 10, 2, 10, 3, 20, 4, 30),
        L("[/expertjob level limit]"),
        L("[disjointer big win rate]", 1, 0, 2, 5, 3, 10),
    });

    /// <summary>
    /// 旧版分解表:主产物 950,基数 100,六档系数 1.0、1.5、2.0、2.5、3.0、3.5;
    /// 附加产物组:稀有度 1 → 951、952;稀有度 2 → 953;稀有度 4 → 960;其余为空;
    /// 附加产物常数(每档:大成功除数、普通除数、大成功百分数、没用到的一个数;大成功除数为 0 的档只有这一个数):
    /// 稀有度 1 → 10.0、5.0、20;稀有度 2 → 2.0、1.0、0;稀有度 4 → 4.0、8.0、100。
    /// </summary>
    public static ScriptLine[] LegacyLines() => new[]
    {
        L("[cube index]", "[no element]", 950, 950, 950),
        L("[/cube index]"),
        L("[cube index]", "[fire element]", 957, 957),
        L("[/cube index]"),
        L("[cube creation const]", 100, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.5f),
        L("[additional result]", 0, 2, 951, 952, 1, 953, 0, 1, 960, 0),
        L("[additional result const]", 0, 10.0f, 5.0f, 20, 0, 2.0f, 1.0f, 0, 0, 0, 4.0f, 8.0f, 100, 0, 0),
        L("[skill info]", 179, "[swordman]", 2),
        L("[avatar disjoint info]"),
    };

    public static LegacyDisjointScript Legacy() => LegacyDisjointScript.Parse(LegacyLines());

    public static ExpertJobCatalog Catalog(ItemPriceRates rates = null) => new(Etc(), new[] { Disjointer() }, Legacy(), rates ?? ItemPriceRates.Neutral);

    // ---- 候选与环境(规则层用,不带物品数据) ----

    /// <summary>规则层的候选装备:已鉴定、武器部位、可分解;其余字段按需给。</summary>
    public static DisjointItem Candidate(int value = 1000, int grade = 20, int rarity = 1, bool identified = true, bool randomOption = false,
        int part = EquipmentParts.Weapon, int attach = AttachTypes.Free, bool impossible = false, int usableLevel = 1, int code = 900)
    {
        return new DisjointItem
        {
            Code = code,
            IsEquipment = true,
            Part = part,
            AttachType = attach,
            ImpossibleDisjoint = impossible,
            Identified = identified,
            Rarity = rarity,
            Grade = grade,
            UsableLevel = usableLevel,
            Value = value,
            RandomOption = randomOption,
        };
    }

    public static DisjointContext Mine(int machineGrade = 0, bool active = false, float bonus = 0f, float variation = 0f, ItemPriceRates rates = null)
    {
        return new DisjointContext { MachineGrade = machineGrade, IsMine = true, OwnerActive = active, OwnerBigWinBonus = bonus, OwnerResultVariation = variation, PriceRates = rates };
    }

    public static DisjointContext Others(int machineGrade = 0, bool active = false, float bonus = 0f, float variation = 0f, ItemPriceRates rates = null)
    {
        return new DisjointContext { MachineGrade = machineGrade, IsMine = false, OwnerActive = active, OwnerBigWinBonus = bonus, OwnerResultVariation = variation, PriceRates = rates };
    }

    // ---- 物品 ----

    /// <summary>一件装备(物品编号 900 起)。</summary>
    public static ItemInfo Gear(int code, int part = EquipmentParts.Weapon, int level = 1, int grade = 20, int rarity = 1, int value = 1000, string attach = null)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment)
        {
            Part = part,
            MinimumLevel = level,
            Grade = grade,
            Rarity = rarity,
            Value = value,
            AttachType = attach,
        };
        return item;
    }

    /// <summary>一种材料(可叠加,[material expert job] 类型 2 → 材料页),叠加上限默认。</summary>
    public static ItemInfo Material(int code, int limit = 0)
    {
        return new ItemInfo(code, "stackable/test/m" + code + ".stk", ItemKind.Stackable) { StackableType = 2, StackLimit = limit };
    }

    /// <summary>一个便携分解器(可叠加杂物)。</summary>
    public static ItemInfo Portable(int code)
    {
        return new ItemInfo(code, "stackable/test/p" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Etc };
    }
}

/// <summary>
/// 脚本化的随机数来源:按次序给出事先写好的值,同时记下每次问的范围,用来钉死"抽几次、每次范围多大、先后次序"。
/// 给出的值必须落在这次问的范围里(否则是测试自己写错了,直接断言失败);值用完了再要就抛异常。
/// </summary>
internal sealed class ScriptedRandom : IDisjointRandom
{
    private readonly Queue<int> _values;

    public ScriptedRandom(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    /// <summary>每次 <see cref="Next"/> 被问到的上界(不含),按次序。</summary>
    public List<int> Ranges { get; } = new();

    public int Remaining => _values.Count;

    public int Next(int exclusiveMax)
    {
        Ranges.Add(exclusiveMax);
        if (_values.Count == 0)
        {
            throw new InvalidOperationException("随机数脚本用完了,但又被要了一个(范围 " + exclusiveMax + ")");
        }
        int value = _values.Dequeue();
        Assert.InRange(value, 0, Math.Max(0, exclusiveMax - 1));
        return value;
    }
}
