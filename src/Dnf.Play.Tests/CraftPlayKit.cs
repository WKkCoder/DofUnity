using System.Text;
using Dnf.Game;
using Dnf.Game.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 制作线在 Play 层测试用的合成数据与小工具(与 Game 层测试里的那套各自独立,避免共享项目文件)。编号全是自造的:
/// 配方物品 900(矿×3 + 草×1 → 药水×2,类别 handwork)、940(金币 50 + 矿×1 → 药水×1)、
/// 941(矿×1 → 药水×1,成功率 50,失败给草×1)、942(草×1 → 药水×1);材料矿 901、草 902;产物药水 910。
/// </summary>
internal static class CraftPlayKit
{
    public const int Std = 900;
    public const int Ore = 901;
    public const int Herb = 902;
    public const int Potion = 910;
    public const int Gold50 = 940;
    public const int Risky = 941;
    public const int Cheap = 942;

    public static Fixture BuildFixture()
    {
        return new Fixture()
            .List(ItemCatalog.StackableListPath,
                (Std, "recipe/r900.stk"), (Ore, "mat/ore.stk"), (Herb, "mat/herb.stk"), (Potion, "use/potion.stk"),
                (Gold50, "recipe/r940.stk"), (Risky, "recipe/r941.stk"), (Cheap, "recipe/r942.stk"))
            .Tokens("stackable/recipe/r900.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0)
                .Tag("[int data]").Ints(2, Ore, 3, Herb, 1, 1, Potion, 2, 0).Tag("[/int data]")
                .Tag("[string data]").Str("handwork").Tag("[/string data]"))
            .Tokens("stackable/recipe/r940.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0)
                .Tag("[int data]").Ints(2, 0, 50, Ore, 1, 1, Potion, 1, 0).Tag("[/int data]"))
            .Tokens("stackable/recipe/r941.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0)
                .Tag("[int data]").Ints(1, Ore, 1, 1, Potion, 1, 0).Tag("[/int data]")
                .Tag("[success rate]").Int(50).Tag("[fail output]").Ints(Herb, 1).Tag("[/fail output]"))
            .Tokens("stackable/recipe/r942.stk", w => w.Tag("[stackable type]").Str("[recipe]").Int(0)
                .Tag("[int data]").Ints(1, Herb, 1, 1, Potion, 1, 0).Tag("[/int data]"))
            .Tokens("stackable/mat/ore.stk", w => w.Tag("[stackable type]").Str("[material]").Int(0))
            .Tokens("stackable/mat/herb.stk", w => w.Tag("[stackable type]").Str("[material]").Int(0))
            .Tokens("stackable/use/potion.stk", w => w.Tag("[stackable type]").Str("[waste]").Int(0));
    }

    public static CraftSession NewSession(ICraftRoll roll = null, ICraftGate gate = null)
    {
        var data = new GameData(BuildFixture().Build());
        return new CraftSession(new RecipeCatalog(data, new ItemCatalog(data)), roll, gate);
    }

    /// <summary>按编号与数量建一个背包(物品信息取自会话的目录;全放得下才算数)。</summary>
    public static Inventory BagWith(CraftSession session, params (int Code, int Count)[] stacks)
    {
        var bag = new Inventory();
        Fill(session, bag, stacks);
        return bag;
    }

    public static void Fill(CraftSession session, Inventory bag, params (int Code, int Count)[] stacks)
    {
        foreach ((int code, int count) in stacks)
        {
            Assert.Equal(count, bag.Add(session.Catalog.Items.Get(code), count));
        }
    }

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
}

/// <summary>按预先给定的数一个一个掷出来的骰子;给多了(没给够)就报错。</summary>
internal sealed class QueueRoll : ICraftRoll
{
    private readonly Queue<int> _values;

    public QueueRoll(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    public int Calls { get; private set; }

    public int Below(int n)
    {
        Calls++;
        if (_values.Count == 0)
        {
            throw new InvalidOperationException("掷骰次数比预期多");
        }
        return _values.Dequeue();
    }
}

/// <summary>数着被问了几次、记下每次制作完成通知的闸门(默认都放行,可以单独关掉学习或制作)。</summary>
internal sealed class CountingGate : ICraftGate
{
    public bool AllowLearn { get; set; } = true;

    public bool AllowCraft { get; set; } = true;

    public List<CraftReport> Crafted { get; } = new();

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
        Crafted.Add(report);
    }
}
