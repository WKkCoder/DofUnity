using Dnf.Game;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 仓库会话、存档、窗口测试共用的合成夹具。编号全是合成的(900 起),数值是手工挑的,与原版数据无关。
/// 账号金库:建立要 10 级、材料 931;四档 (容量, 金币上限, 材料, 价钱) = (8, 1000, 2, 500)(建立)、(16, 5000, 3, 1500)、(24, 20000, 0, 0)(免费升)、(60, 99999, 5, 8000)(最后一档,容量超过 56 格)。
/// 每级金币上限:等级 L(0 到 99)是 L × 1000。扩容道具:编号 960(药水式叠加,脚本里写了"升级仓库")。
/// </summary>
internal static class StorageKit
{
    public const string HeroKey = "hero1";
    public const int MaterialCode = 931;
    public const int PassCode = 960;

    public static ItemInfo Gear(int code, int part = EquipmentParts.Coat)
    {
        return new ItemInfo(code, "equipment/test/e" + code + ".equ", ItemKind.Equipment) { Part = part, AttachType = "[free]" };
    }

    public static ItemInfo Stack(int code, int limit = 100, int type = StackableTypes.Waste)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = type, StackLimit = limit, AttachType = "[free]" };
    }

    /// <summary>能进快捷栏的药水(消耗品、有生命回复)。</summary>
    public static ItemInfo Potion(int code, int limit = 100)
    {
        ItemInfo potion = Stack(code, limit);
        potion.HpRecovery = new RecoveryEffect(false, 100, 0);
        return potion;
    }

    public static ItemInfo Material(int limit = 100) => Stack(MaterialCode, limit, StackableTypes.Material);

    public static ItemInfo Pass() => Stack(PassCode, 100);

    /// <summary>认得的编号:装备 900 到 909、药水 911 与 912、材料 931、扩容道具 960;其余不认得。</summary>
    public static ItemInfo Lookup(int code)
    {
        if (code >= 900 && code <= 909)
        {
            return Gear(code);
        }
        switch (code)
        {
            case 911:
            case 912:
                return Potion(code);
            case MaterialCode:
                return Material();
            case PassCode:
                return Pass();
            default:
                return null;
        }
    }

    public static AccountCargoScript Script()
    {
        return new AccountCargoScript(10, MaterialCode, new[]
        {
            new AccountCargoTier(8, 1000, 2, 500),
            new AccountCargoTier(16, 5000, 3, 1500),
            new AccountCargoTier(24, 20000, 0, 0),
            new AccountCargoTier(60, 99999, 5, 8000),
        });
    }

    public static GoldLimitTable Limits()
    {
        var limits = new int[GoldLimitTable.Entries];
        for (int level = 0; level < limits.Length; level++)
        {
            limits[level] = level * 1000;
        }
        return new GoldLimitTable(limits);
    }

    public static StorageSettings Settings()
    {
        return new StorageSettings
        {
            AccountScript = Script(),
            GoldLimits = Limits(),
            RequirePackagable = false,
            Facts = info => info.Code == PassCode ? new StorageItemFacts(false, false, true) : StorageItemFacts.None,
        };
    }

    /// <summary>一个角色的仓库会话:<paramref name="level"/> 级、带着 <paramref name="gold"/> 金币;<paramref name="storage"/> 不给就新建一份。</summary>
    public static StorageSession Session(int level = 12, long gold = 0, AccountStorage storage = null, string heroKey = HeroKey)
    {
        HeroProgress hero = Progress(level: level);
        hero.AddGold(gold);
        return new StorageSession(hero, heroKey, storage ?? new AccountStorage(Settings()));
    }

    /// <summary>背包里每一页每一格的 (编号, 数量)。</summary>
    public static List<(int Code, int Count)> Dump(Inventory bag)
    {
        var rows = new List<(int Code, int Count)>();
        for (int t = 0; t < Inventory.TabCount; t++)
        {
            foreach (ItemStack stack in bag.Tab((InventoryTab)t))
            {
                rows.Add((stack.Info.Code, stack.Count));
            }
        }
        return rows;
    }

    /// <summary>仓库每一格的 (格号, 编号, 数量)。</summary>
    public static List<(int Slot, int Code, int Count)> Dump(StorageVault vault)
    {
        var rows = new List<(int Slot, int Code, int Count)>();
        for (int i = 0; i < vault.Capacity; i++)
        {
            ItemStack stack = vault.SlotAt(i);
            if (stack != null)
            {
                rows.Add((i, stack.Info.Code, stack.Count));
            }
        }
        return rows;
    }
}
