using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>按顺序吐出预设数的随机源:记下每次被问的上界,数用完或数超过上界就报错(说明测试写错了)。</summary>
internal sealed class ScriptedRandom : IDropRandom
{
    private readonly Queue<int> _values;

    public ScriptedRandom(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    /// <summary>每次 <see cref="NextInclusive"/> 被问到的上界。</summary>
    public List<int> Maxes { get; } = new();

    public int Remaining => _values.Count;

    public int NextInclusive(int max)
    {
        Maxes.Add(max);
        if (_values.Count == 0)
        {
            throw new InvalidOperationException("预设的随机数用完了");
        }
        int v = _values.Dequeue();
        if (v < 0 || v > max)
        {
            throw new InvalidOperationException("预设的随机数 " + v + " 超出上界 " + max);
        }
        return v;
    }

    public int Range(int lo, int hi) => throw new NotSupportedException();
}

/// <summary>强化表的合成构造器:数值全是随手写的小数,与原版无关。一行 = 失败阈值、惩罚类型、惩罚降级数、材料编号、材料数。</summary>
internal sealed class TableBuilder
{
    private readonly List<ScriptValue> _rows = new();
    private readonly List<ScriptLine> _lines = new();

    public static ScriptValue I(int value) => new(ScriptValueKind.Int, value, null);

    public static ScriptValue F(float value) => new(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    public static ScriptValue S(string text) => new(ScriptValueKind.String, 0, text);

    public int RowCount { get; private set; }

    /// <summary>加一行(A f1..f5 | i1..i5 | B g1..g5):属性项 A 组统一用 <paramref name="a"/>(整数项)与 <paramref name="f"/>(五个浮点项),B 组全 0。</summary>
    public TableBuilder Row(int fail, int penaltyType, int penaltyLevels, int materialCode, int materialCount, int a = 0, float f = 0f)
    {
        _rows.Add(I(a));
        for (int i = 0; i < 5; i++)
        {
            _rows.Add(F(f));
        }
        _rows.Add(I(fail));
        _rows.Add(I(penaltyType));
        _rows.Add(I(penaltyLevels));
        _rows.Add(I(materialCode));
        _rows.Add(I(materialCount));
        _rows.Add(I(0));
        for (int i = 0; i < 5; i++)
        {
            _rows.Add(F(0f));
        }
        RowCount++;
        return this;
    }

    /// <summary>
    /// 加一行并分别指定两组的各格(文件里的次序 A f1 f2 f3 f4 f5 | … | B g1 g2 g3 g4 g5);失败阈值 0、惩罚类型 1、材料 910 要 1 个。
    /// 稀有度权重与档位默认都是 1,所以装备等级 0 时取行结果的 [0]=A [1]=f1 [5]=f2 [2]=f3 [6]=f4 [3]=f5 原样不变。
    /// </summary>
    public TableBuilder StatRow(int a, float f1, float f2, float f3, float f4, float f5, int b = 0, float g1 = 0f, float g2 = 0f, float g3 = 0f, float g4 = 0f, float g5 = 0f)
    {
        _rows.Add(I(a));
        foreach (float f in new[] { f1, f2, f3, f4, f5 })
        {
            _rows.Add(F(f));
        }
        _rows.Add(I(0));
        _rows.Add(I(1));
        _rows.Add(I(0));
        _rows.Add(I(910));
        _rows.Add(I(1));
        _rows.Add(I(b));
        foreach (float g in new[] { g1, g2, g3, g4, g5 })
        {
            _rows.Add(F(g));
        }
        RowCount++;
        return this;
    }

    /// <summary>加 <paramref name="count"/> 行同样的。</summary>
    public TableBuilder Rows(int count, int fail, int penaltyType, int penaltyLevels, int materialCode, int materialCount)
    {
        for (int i = 0; i < count; i++)
        {
            Row(fail, penaltyType, penaltyLevels, materialCode, materialCount);
        }
        return this;
    }

    public TableBuilder Line(string tag, params ScriptValue[] values)
    {
        _lines.Add(new ScriptLine(tag, values));
        return this;
    }

    /// <summary>[cost]:100 项,第 i 项 = <paramref name="basePrice"/> + i。</summary>
    public TableBuilder CostByGrade(int basePrice)
    {
        var v = new ScriptValue[UpgradeTable.CostSlots];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = I(basePrice + i);
        }
        return Line("[cost]", v);
    }

    /// <summary>[cost] 每一项都是同一个数。</summary>
    public TableBuilder FlatCost(int price)
    {
        var v = new ScriptValue[UpgradeTable.CostSlots];
        Array.Fill(v, I(price));
        return Line("[cost]", v);
    }

    public TableBuilder Destroy(string rarity, int level) => Line("[destroy level by rarity]", S(rarity), I(level));

    public TableBuilder Max(string rarity, int level) => Line("[max upgrade level by rarity]", S(rarity), I(level));

    public UpgradeTable Build()
    {
        var lines = new List<ScriptLine> { new ScriptLine("[table]", _rows.ToArray()) };
        lines.AddRange(_lines);
        Assert.True(UpgradeTable.TryParse(lines.ToArray(), out UpgradeTable table));
        return table;
    }
}

/// <summary>合成的物品与设置。物品编号:装备 900 起,材料 910 起,票据 920 起,卡 930 起,套装件 940 起。</summary>
internal static class EnhanceFixtures
{
    public const int Material = 910;
    public const int Gold100 = 100;

    public static ItemInfo Gear(int code, int part = EquipmentParts.Coat, int rarity = 0, int grade = 1, EnhanceData enhance = null, int level = 1)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment)
        {
            Part = part,
            Rarity = rarity,
            Grade = grade,
            MinimumLevel = level,
            Enhance = enhance,
        };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    public static ItemInfo Stack(int code, EnhanceData enhance = null)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { Enhance = enhance };
    }

    /// <summary>带状态的个体拷贝。</summary>
    public static ItemInfo With(ItemInfo gear, int upgrade, bool amplified = false, bool identified = true, int enchant = 0, int grade = 0, bool damaged = false)
    {
        return gear.WithInstance(new EquipInstance(upgrade, amplified, identified, enchant, grade, damaged));
    }

    public static EquipStats Stats(EquipStat stat, double value) => new EquipStats.Builder().Set(stat, value).Build();

    /// <summary>12 行的普通表:每行失败 20000(20%),惩罚类型 1,降 2 级,材料 910 要 3 个;[cost] 全 100。</summary>
    public static UpgradeTable PlainTable(int fail = 20000, int penaltyType = 1, int rows = 12, int materialCount = 3)
    {
        return new TableBuilder().Rows(rows, fail, penaltyType, 2, Material, materialCount).FlatCost(Gold100).Build();
    }

    public static UpgradeSettings Settings(UpgradeTable normal, IDropRandom random, UpgradeTable amplify = null)
    {
        return new UpgradeSettings { NormalTable = normal, AmplifyTable = amplify, Random = random };
    }

    public static UpgradeRequest Request(ItemInfo equip, int materialCount = 10, long gold = 1000, int level = 50, bool amplify = false)
    {
        return new UpgradeRequest
        {
            Equipment = equip,
            Material = Stack(Material),
            MaterialCount = materialCount,
            Gold = gold,
            CharacterLevel = level,
            RequestAmplify = amplify,
        };
    }
}
