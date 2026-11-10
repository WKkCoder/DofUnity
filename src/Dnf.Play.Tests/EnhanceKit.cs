using Dnf.Data;
using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>按顺序吐出预设数的随机源(强化会话测试用):记下每次的上界,数用完或超过上界就报错,说明测试写错了。</summary>
internal sealed class EnhanceRoll : IDropRandom
{
    private readonly Queue<int> _values;

    public EnhanceRoll(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    public List<int> Maxes { get; } = new();

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

/// <summary>强化会话与存档测试的合成夹具:物品编号装备 900 起、材料 910 起、票据 920 起、卡 930 起、套装件 940 起、保护道具 944 起、产物 950 起、强化券 960 起。</summary>
internal static class EnhanceKit
{
    public const int Material = 910;

    private static ScriptValue I(int value) => new(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    /// <summary>每行相同的普通强化表:失败阈值、惩罚类型、惩罚降级数、材料 910 要几个;[cost] 每项都是 <paramref name="cost"/>。</summary>
    public static UpgradeTable Table(int rows = 12, int fail = 20000, int penaltyType = 1, int penaltyLevels = 2, int materialCount = 3, int cost = 100)
    {
        var values = new List<ScriptValue>();
        for (int r = 0; r < rows; r++)
        {
            values.Add(I(0));
            for (int i = 0; i < 5; i++)
            {
                values.Add(F(0f));
            }
            values.Add(I(fail));
            values.Add(I(penaltyType));
            values.Add(I(penaltyLevels));
            values.Add(I(Material));
            values.Add(I(materialCount));
            values.Add(I(0));
            for (int i = 0; i < 5; i++)
            {
                values.Add(F(0f));
            }
        }
        var costs = new ScriptValue[UpgradeTable.CostSlots];
        Array.Fill(costs, I(cost));
        var lines = new[] { new ScriptLine("[table]", values.ToArray()), new ScriptLine("[cost]", costs) };
        Assert.True(UpgradeTable.TryParse(lines, out UpgradeTable table));
        return table;
    }

    public static ItemInfo Gear(int code, int part = EquipmentParts.Weapon, EquipStats stats = null, EnhanceData enhance = null)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment)
        {
            Part = part, Grade = 1, MinimumLevel = 1, Stats = stats, Enhance = enhance,
        };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    public static ItemInfo Stack(int code, EnhanceData enhance = null)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Material, StackLimit = 999, Enhance = enhance };
    }

    public static EquipStats Stats(EquipStat stat, double value) => new EquipStats.Builder().Set(stat, value).Build();
}
