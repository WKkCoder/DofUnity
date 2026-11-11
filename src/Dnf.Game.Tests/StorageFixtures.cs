using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 仓库测试共用的合成夹具:物品、账号金库档位表、每级金币上限、钱包。编号全是合成的(900 起),数值是手工挑的,与原版数据无关。
/// 账号金库:建立要 10 级、材料 931;四档 (容量, 金币上限, 材料, 价钱) = (8, 1000, 2, 500)(建立)、(16, 5000, 3, 1500)、(24, 20000, 0, 0)(免费升)、(60, 99999, 5, 8000)(最后一档,容量超过 56 格)。
/// 每级金币上限:等级 L(0 到 99)是 L × 1000。
/// </summary>
internal static class StorageFixtures
{
    public const int RequiredLevel = 10;
    public const int MaterialCode = 931;

    public static ItemInfo Equip(int code, int part = EquipmentParts.Coat, string attach = "[free]")
    {
        return new ItemInfo(code, "equipment/test/e" + code + ".equ", ItemKind.Equipment) { Part = part, AttachType = attach };
    }

    public static ItemInfo Stack(int code, int limit = 100, int type = StackableTypes.Waste, string attach = "[free]")
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = type, StackLimit = limit, AttachType = attach };
    }

    public static AccountCargoScript Script(int requiredLevel = RequiredLevel, int requiredItem = MaterialCode)
    {
        return new AccountCargoScript(requiredLevel, requiredItem, new[]
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

    /// <summary>默认设置:有档位表与每级上限;账号金库不要求 [packagable](测试存取时用),没有物品事实。</summary>
    public static StorageSettings Settings(Dictionary<int, StorageItemFacts> facts = null, bool requirePackagable = false)
    {
        return new StorageSettings
        {
            AccountScript = Script(),
            GoldLimits = Limits(),
            RequirePackagable = requirePackagable,
            Facts = info => facts != null && facts.TryGetValue(info.Code, out StorageItemFacts f) ? f : StorageItemFacts.None,
        };
    }

    /// <summary>已经开通了账号金库(经 RestoreAccount 直接设档,不收费用)。</summary>
    public static AccountStorage WithAccountVault(int tier = 8, long gold = 0, StorageSettings settings = null)
    {
        var storage = new AccountStorage(settings ?? Settings());
        storage.RestoreAccount(tier, gold);
        return storage;
    }

    /// <summary>可以让它拒绝扣钱的钱包(测试"钱包说谎"时不会丢东西)。</summary>
    public sealed class Purse : IStoragePurse
    {
        public Purse(long gold = 0)
        {
            Gold = gold;
        }

        public long Gold { get; set; }

        public bool RefuseSpend { get; set; }

        public int Received { get; private set; }

        public bool TrySpend(long amount)
        {
            if (RefuseSpend || amount <= 0 || amount > Gold)
            {
                return false;
            }
            Gold -= amount;
            return true;
        }

        public void Receive(long amount)
        {
            Gold += amount;
            Received++;
        }
    }

    /// <summary>背包里每一页每一格的 (编号, 数量),用来比较"失败时背包原样"。</summary>
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
