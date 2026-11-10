using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的设计(r15-storage):仓库窗口要画的数据——只给数据,不画图(界面素材与布局由宿主与以后的界面层负责)。
// 数字与规则的来源见 Game 层各文件的头注释(StorageRules、AccountCargoScript、GoldLimitTable)。
namespace Dnf.Play
{
    /// <summary>"下一步要花钱的事"的种类。</summary>
    public enum StorageStepKind
    {
        /// <summary>没有下一步(个人仓库已经最大、账号金库已经是最高一档、或者没读到档位数据)。</summary>
        None = 0,

        /// <summary>开通账号金库。</summary>
        CreateAccount,

        /// <summary>账号金库升到下一档。</summary>
        UpgradeAccount,

        /// <summary>用扩容道具扩个人仓库。</summary>
        ExpandPersonal,
    }

    /// <summary>下一步要花什么、够不够(给窗口上"开通/升级/扩充"按钮用)。</summary>
    public sealed class StorageStep
    {
        public StorageStep(StorageStepKind kind, int newCapacity, int newSlots, long newGoldLimit, int requiredLevel, int materialItem, int materialCount,
            long priceGold, bool meetsLevel, bool hasMaterial, bool hasGold)
        {
            Kind = kind;
            NewCapacity = newCapacity;
            NewSlots = newSlots;
            NewGoldLimit = newGoldLimit;
            RequiredLevel = requiredLevel;
            MaterialItem = materialItem;
            MaterialCount = materialCount;
            PriceGold = priceGold;
            MeetsLevel = meetsLevel;
            HasMaterial = hasMaterial;
            HasGold = hasGold;
        }

        public StorageStepKind Kind { get; }

        /// <summary>做完之后的容量值(账号金库是档位表里的键)。</summary>
        public int NewCapacity { get; }

        /// <summary>做完之后开放的格数。</summary>
        public int NewSlots { get; }

        /// <summary>做完之后账号金库的金币上限(个人仓库没有,是 0)。</summary>
        public long NewGoldLimit { get; }

        /// <summary>要求的角色等级(只有开通账号金库有,其余是 0)。</summary>
        public int RequiredLevel { get; }

        /// <summary>要交的材料物品编号;个人仓库扩容要的是"任意一件扩容道具",编号是 0。</summary>
        public int MaterialItem { get; }

        /// <summary>要交几件材料(0 是不要)。</summary>
        public int MaterialCount { get; }

        /// <summary>要交多少金币(0 是不要)。</summary>
        public long PriceGold { get; }

        public bool MeetsLevel { get; }

        public bool HasMaterial { get; }

        public bool HasGold { get; }

        /// <summary>等级、材料、金币都够。</summary>
        public bool CanAfford => MeetsLevel && HasMaterial && HasGold;
    }

    /// <summary>窗口里的一格。</summary>
    public readonly struct StorageSlotView
    {
        public StorageSlotView(int slot, bool isOpen, ItemInfo info, int count)
        {
            Slot = slot;
            IsOpen = isOpen;
            Info = info;
            Count = count;
        }

        public int Slot { get; }

        /// <summary>这一格开放了没有(没开放的画成锁住的)。</summary>
        public bool IsOpen { get; }

        /// <summary>格子里的物品;空格或没开放是 null。</summary>
        public ItemInfo Info { get; }

        /// <summary>件数(空格是 0)。</summary>
        public int Count { get; }

        public bool IsEmpty => Info == null;
    }

    /// <summary>一个仓库窗口此刻要画的全部数据(一次拍照,不跟着仓库变;<see cref="Version"/> 变了就重新拍)。</summary>
    public sealed class StorageWindowData
    {
        public StorageWindowData(StorageKind kind, bool hasVault, int capacity, int maxSlots, IReadOnlyList<StorageSlotView> slots, long vaultGold,
            long vaultGoldLimit, long heroGold, long heroGoldLimit, StorageStep step, int version)
        {
            Kind = kind;
            HasVault = hasVault;
            Capacity = capacity;
            MaxSlots = maxSlots;
            Slots = slots;
            VaultGold = vaultGold;
            VaultGoldLimit = vaultGoldLimit;
            HeroGold = heroGold;
            HeroGoldLimit = heroGoldLimit;
            Step = step;
            Version = version;
        }

        public StorageKind Kind { get; }

        /// <summary>仓库存在吗:个人仓库总是有,账号金库要开通。没有的话窗口只显示"开通"。</summary>
        public bool HasVault { get; }

        /// <summary>开放的格数。</summary>
        public int Capacity { get; }

        /// <summary>这种仓库最多的格数(<see cref="Slots"/> 的长度)。</summary>
        public int MaxSlots { get; }

        /// <summary>从第 0 格到最后一格,没开放的也在里面。</summary>
        public IReadOnlyList<StorageSlotView> Slots { get; }

        /// <summary>仓库里的金币(个人仓库没有金币,是 0)。</summary>
        public long VaultGold { get; }

        /// <summary>这一档账号金库最多放多少金币(没有的是 0)。</summary>
        public long VaultGoldLimit { get; }

        /// <summary>角色身上的金币。</summary>
        public long HeroGold { get; }

        /// <summary>角色现在的等级能带多少金币;没有读到上限表是 0。</summary>
        public long HeroGoldLimit { get; }

        /// <summary>下一步开通、升级或扩充要花什么;没有下一步是 null。</summary>
        public StorageStep Step { get; }

        /// <summary>数据版本(<see cref="AccountStorage.Version"/>)。</summary>
        public int Version { get; }
    }

    /// <summary>从会话拍仓库窗口的数据。</summary>
    public static class StorageWindow
    {
        public static StorageWindowData Build(StorageSession session, StorageKind kind)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            AccountStorage storage = session.Storage;
            StorageVault vault = session.VaultOf(kind);
            var slots = new StorageSlotView[vault.MaxSlots];
            for (int i = 0; i < slots.Length; i++)
            {
                ItemStack stack = vault.SlotAt(i);
                slots[i] = new StorageSlotView(i, vault.IsOpen(i), stack?.Info, stack == null ? 0 : stack.Count);
            }
            bool account = kind == StorageKind.Account;
            GoldLimitTable limits = storage.Settings.GoldLimits;
            return new StorageWindowData(
                kind,
                !account || storage.HasAccountVault,
                vault.Capacity,
                vault.MaxSlots,
                slots,
                account ? storage.AccountGold : 0,
                account ? storage.AccountGoldLimit : 0,
                session.Hero.Gold,
                limits == null ? 0 : limits.LimitOf(session.Hero.Level),
                account ? AccountStep(session) : PersonalStep(session),
                storage.Version);
        }

        private static StorageStep PersonalStep(StorageSession session)
        {
            int capacity = session.Personal.Capacity;
            if (!StorageRules.CanExpandPersonal(capacity))
            {
                return null;
            }
            int next = capacity + StorageRules.PersonalExpandStep;
            return new StorageStep(StorageStepKind.ExpandPersonal, next, next, 0, 0, 0, 1, 0, true, HasExpansionItem(session), true);
        }

        private static bool HasExpansionItem(StorageSession session)
        {
            StorageSettings settings = session.Storage.Settings;
            for (int t = 0; t < Inventory.TabCount; t++)
            {
                foreach (ItemStack stack in session.Hero.Bag.Tab((InventoryTab)t))
                {
                    if (stack.Info.IsStackable && settings.FactsOf(stack.Info).UpgradesCargo)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static StorageStep AccountStep(StorageSession session)
        {
            AccountStorage storage = session.Storage;
            AccountCargoScript script = storage.Settings.AccountScript;
            if (script == null)
            {
                return null;
            }
            bool created = storage.HasAccountVault;
            AccountCargoTier tier = created ? script.Next(storage.AccountTierCapacity) : script.Create;
            if (tier == null)
            {
                return null;
            }
            int level = created ? 0 : script.RequiredLevel;
            bool material = tier.MaterialCount <= 0 || session.Hero.Bag.CountOf(script.RequiredItem) >= tier.MaterialCount;
            bool gold = tier.PriceGold <= 0 || session.Hero.Gold >= tier.PriceGold;
            return new StorageStep(
                created ? StorageStepKind.UpgradeAccount : StorageStepKind.CreateAccount,
                tier.Capacity,
                Math.Min(tier.Capacity, StorageRules.AccountMaxSlots),
                tier.GoldLimit,
                level,
                script.RequiredItem,
                Math.Max(0, tier.MaterialCount),
                Math.Max(0, tier.PriceGold),
                session.Hero.Level >= level,
                material,
                gold);
        }
    }
}
