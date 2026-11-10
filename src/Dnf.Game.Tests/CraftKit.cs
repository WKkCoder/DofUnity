using System.Text;

namespace Dnf.Game.Tests;

/// <summary>
/// 制作线测试的合成物品与小工具:物品编号 900 起,全是自造的(没有任何原版内容)。
/// 900 设计图(配方物品);901 矿、902 草、903 水晶(一格最多 5 个);910 药水、913 药剂、914 补剂、915 以太(都是消耗品页);
 /// 911 剑(装备)、912 粉(材料页)、920 废料(材料页)、930 帽子(时装)、931 斧(装备)、940 第二个配方物品。
/// </summary>
internal static class CraftKit
{
    public const int Blueprint = 900;
    public const int Ore = 901;
    public const int Herb = 902;
    public const int Crystal = 903;
    public const int Potion = 910;
    public const int Blade = 911;
    public const int Dust = 912;
    public const int Elixir = 913;
    public const int Tonic = 914;
    public const int Ether = 915;
    public const int Scrap = 920;
    public const int Hat = 930;
    public const int Axe = 931;
    public const int Blueprint2 = 940;

    /// <summary>被测配方自己的编号(也是它的配方物品编号:测试里让配方物品就是 900)。</summary>
    public const int RecipeCode = Blueprint;

    private static readonly Dictionary<int, ItemInfo> Items = Make();

    private static Dictionary<int, ItemInfo> Make()
    {
        ItemInfo Stack(int code, int type, int limit = 100) =>
            new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = type, StackLimit = limit };
        ItemInfo Equip(int code, int part) =>
            new ItemInfo(code, "equipment/test/e" + code + ".equ", ItemKind.Equipment) { Part = part };
        var items = new Dictionary<int, ItemInfo>
        {
            [Blueprint] = Stack(Blueprint, StackableTypes.Recipe, 20),
            [Ore] = Stack(Ore, StackableTypes.Material),
            [Herb] = Stack(Herb, StackableTypes.Material),
            [Crystal] = Stack(Crystal, StackableTypes.Material, 5),
            [Potion] = Stack(Potion, StackableTypes.Waste),
            [Blade] = Equip(Blade, EquipmentParts.Weapon),
            [Dust] = Stack(Dust, StackableTypes.Material),
            [Elixir] = Stack(Elixir, StackableTypes.Waste),
            [Tonic] = Stack(Tonic, StackableTypes.Waste),
            [Ether] = Stack(Ether, StackableTypes.Waste),
            [Scrap] = Stack(Scrap, StackableTypes.Material),
            [Hat] = Equip(Hat, EquipmentParts.HatAvatar),
            [Axe] = Equip(Axe, EquipmentParts.Weapon),
            [Blueprint2] = Stack(Blueprint2, StackableTypes.Recipe, 20),
        };
        return items;
    }

    public static ItemInfo Item(int code) => Items[code];

    public static ItemInfo Lookup(int code) => Items.TryGetValue(code, out ItemInfo item) ? item : null;

    /// <summary>按编号与数量建一个背包(全放得下才算数)。<paramref name="slotsPerTab"/> 小一点好造"满包"。</summary>
    public static Inventory Bag(int slotsPerTab, params (int Code, int Count)[] stacks)
    {
        var bag = new Inventory(slotsPerTab);
        foreach ((int code, int count) in stacks)
        {
            Assert.Equal(count, bag.Add(Item(code), count));
        }
        return bag;
    }

    public static Inventory Bag(params (int Code, int Count)[] stacks) => Bag(Inventory.SlotsPerTab, stacks);

    /// <summary>背包的完整布局(页签、格子次序、编号与数量),用来断言"什么也没动"。</summary>
    public static string Dump(Inventory bag)
    {
        var sb = new StringBuilder();
        for (int t = 0; t < Inventory.TabCount; t++)
        {
            sb.Append('[').Append(t).Append(':');
            foreach (ItemStack stack in bag.Tab((InventoryTab)t))
            {
                sb.Append(stack.Info.Code).Append('x').Append(stack.Count).Append(' ');
            }
            sb.Append(']');
        }
        return sb.ToString();
    }

    public static CraftBench Bench(ICraftRoll roll = null, ICraftGate gate = null) => new CraftBench(Lookup, roll, gate);

    public static RecipeBuilder Recipe(int code = RecipeCode) => new RecipeBuilder(code);
}

/// <summary>合成配方的小构造器。</summary>
internal sealed class RecipeBuilder
{
    private readonly int _code;
    private readonly List<RecipeItem> _in = new();
    private readonly List<RecipeItem> _out = new();
    private readonly List<RecipeItem> _bead = new();
    private readonly List<RecipeItem> _fail = new();
    private int _rate;
    private RecipeKind _kind;
    private bool _malformed;

    public RecipeBuilder(int code)
    {
        _code = code;
    }

    public RecipeBuilder In(int code, int count)
    {
        _in.Add(new RecipeItem(code, count));
        return this;
    }

    public RecipeBuilder Out(int code, int count)
    {
        _out.Add(new RecipeItem(code, count));
        return this;
    }

    public RecipeBuilder Bead(int code, int count)
    {
        _bead.Add(new RecipeItem(code, count));
        return this;
    }

    public RecipeBuilder Fail(int code, int count)
    {
        _fail.Add(new RecipeItem(code, count));
        return this;
    }

    public RecipeBuilder Rate(int percent)
    {
        _rate = percent;
        return this;
    }

    public RecipeBuilder Kind(RecipeKind kind)
    {
        _kind = kind;
        return this;
    }

    public RecipeBuilder Malformed()
    {
        _malformed = true;
        return this;
    }

    public RecipeDef Build()
    {
        return new RecipeDef(_code, _in, _out, _kind, null, _bead, _rate, _fail, malformed: _malformed);
    }
}

/// <summary>按预先给定的数一个一个掷出来的骰子;掷骰次数与每次的范围都记下来,给多了(没给够)就报错。</summary>
internal sealed class ScriptedRoll : ICraftRoll
{
    private readonly Queue<int> _values;

    public ScriptedRoll(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    public int Calls { get; private set; }

    public int LastRange { get; private set; }

    public int Below(int n)
    {
        Calls++;
        LastRange = n;
        if (_values.Count == 0)
        {
            throw new InvalidOperationException("掷骰次数比预期多");
        }
        return _values.Dequeue();
    }
}

/// <summary>可以关掉学习或制作的闸门,并记下每次制作完成的通知。</summary>
internal sealed class RecordingGate : ICraftGate
{
    public bool AllowLearn { get; set; } = true;

    public bool AllowCraft { get; set; } = true;

    public List<(RecipeDef Recipe, CraftReport Report)> Crafted { get; } = new();

    public int LearnAsked { get; private set; }

    public int CraftAsked { get; private set; }

    public bool CanLearn(RecipeDef recipe)
    {
        LearnAsked++;
        return AllowLearn;
    }

    public bool CanCraft(RecipeDef recipe)
    {
        CraftAsked++;
        return AllowCraft;
    }

    public void OnCrafted(RecipeDef recipe, CraftReport report)
    {
        Crafted.Add((recipe, report));
    }
}
