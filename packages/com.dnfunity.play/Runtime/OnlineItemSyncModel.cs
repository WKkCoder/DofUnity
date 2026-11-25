using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    /// <summary>
    /// 中立的联网物品槽位条目(不引用 <c>Dnf.Net</c> 与 <c>Dnf.Game</c> 类型,供 Client 适配器写入、Play 主角进城应用消费)。
    /// </summary>
    public sealed class OnlineItemSlotEntry
    {
        public OnlineItemSlotEntry(
            ushort slot,
            uint itemCode,
            int count = 1,
            int upgradeLevel = 0,
            bool isAmplified = false,
            bool isIdentified = true,
            uint enchantCode = 0,
            int gradeValue = 0)
        {
            Slot = slot;
            ItemCode = itemCode;
            Count = count;
            UpgradeLevel = upgradeLevel;
            IsAmplified = isAmplified;
            IsIdentified = isIdentified;
            EnchantCode = enchantCode;
            GradeValue = gradeValue;
        }

        public ushort Slot { get; }
        public uint ItemCode { get; }
        public int Count { get; }
        public int UpgradeLevel { get; }
        public bool IsAmplified { get; }
        public bool IsIdentified { get; }
        public uint EnchantCode { get; }
        public int GradeValue { get; }
    }

    /// <summary>
    /// 中立的联网物品同步数据包:由 Client 层从 <c>ItemSyncSnapshot</c> 或实时增量影子状态投影生成,
    /// 再交给 <see cref="HeroProgress.ApplyOnlineItems"/> 落到内存中的金币、背包、快捷栏与装备栏。
    /// </summary>
    public sealed class OnlineItemSyncData
    {
        private static readonly OnlineItemSlotEntry[] EmptyEntries = new OnlineItemSlotEntry[0];

        public OnlineItemSyncData(
            bool hasGold,
            long gold,
            IEnumerable<OnlineItemSlotEntry> bagEntries,
            IEnumerable<OnlineItemSlotEntry> quickEntries,
            IEnumerable<OnlineItemSlotEntry> wornEquips,
            bool hasBagData,
            bool hasEquipData,
            int unmodeledAvatarBagCount = 0,
            int unmodeledCargoCount = 0,
            int unmodeledPetBagCount = 0,
            int unmodeledAccountCargoCount = 0,
            int unmodeledExpandEquipCount = 0)
        {
            HasGold = hasGold;
            Gold = gold;
            BagEntries = CopyReadOnly(bagEntries);
            QuickEntries = CopyReadOnly(quickEntries);
            WornEquips = CopyReadOnly(wornEquips);
            HasBagData = hasBagData;
            HasEquipData = hasEquipData;
            UnmodeledAvatarBagCount = unmodeledAvatarBagCount < 0 ? 0 : unmodeledAvatarBagCount;
            UnmodeledCargoCount = unmodeledCargoCount < 0 ? 0 : unmodeledCargoCount;
            UnmodeledPetBagCount = unmodeledPetBagCount < 0 ? 0 : unmodeledPetBagCount;
            UnmodeledAccountCargoCount = unmodeledAccountCargoCount < 0 ? 0 : unmodeledAccountCargoCount;
            UnmodeledExpandEquipCount = unmodeledExpandEquipCount < 0 ? 0 : unmodeledExpandEquipCount;
        }

        public bool HasGold { get; }
        public long Gold { get; }
        public IReadOnlyList<OnlineItemSlotEntry> BagEntries { get; }
        public IReadOnlyList<OnlineItemSlotEntry> QuickEntries { get; }
        public IReadOnlyList<OnlineItemSlotEntry> WornEquips { get; }
        public bool HasBagData { get; }
        public bool HasEquipData { get; }
        public int UnmodeledAvatarBagCount { get; }
        public int UnmodeledCargoCount { get; }
        public int UnmodeledPetBagCount { get; }
        public int UnmodeledAccountCargoCount { get; }
        public int UnmodeledExpandEquipCount { get; }

        private static IReadOnlyList<OnlineItemSlotEntry> CopyReadOnly(IEnumerable<OnlineItemSlotEntry> source)
        {
            if (source == null)
            {
                return Array.AsReadOnly(EmptyEntries);
            }
            var list = new List<OnlineItemSlotEntry>();
            foreach (OnlineItemSlotEntry entry in source)
            {
                if (entry != null)
                {
                    list.Add(entry);
                }
            }
            return list.Count == 0 ? Array.AsReadOnly(EmptyEntries) : Array.AsReadOnly(list.ToArray());
        }
    }

    /// <summary>
    /// 一次 <see cref="HeroProgress.ApplyOnlineItems"/> 的应用报告(不含任何真实编号或金额,仅同步标志与计数)。
    /// </summary>
    public sealed class OnlineItemSyncReport
    {
        public OnlineItemSyncReport(
            bool goldSynced,
            bool inventorySynced,
            bool equipsSynced,
            int bagApplied,
            int quickApplied,
            int equipsApplied,
            int unknownItemCount,
            int overflowItemCount,
            int invalidEquipSlotCount,
            int unmodeledAvatarBagCount,
            int unmodeledCargoCount,
            int unmodeledPetBagCount,
            int unmodeledAccountCargoCount,
            int unmodeledExpandEquipCount)
        {
            GoldSynced = goldSynced;
            InventorySynced = inventorySynced;
            EquipsSynced = equipsSynced;
            BagApplied = bagApplied;
            QuickApplied = quickApplied;
            EquipsApplied = equipsApplied;
            UnknownItemCount = unknownItemCount;
            OverflowItemCount = overflowItemCount;
            InvalidEquipSlotCount = invalidEquipSlotCount;
            UnmodeledAvatarBagCount = unmodeledAvatarBagCount;
            UnmodeledCargoCount = unmodeledCargoCount;
            UnmodeledPetBagCount = unmodeledPetBagCount;
            UnmodeledAccountCargoCount = unmodeledAccountCargoCount;
            UnmodeledExpandEquipCount = unmodeledExpandEquipCount;
        }

        public bool GoldSynced { get; }
        public bool InventorySynced { get; }
        public bool EquipsSynced { get; }
        public int BagApplied { get; }
        public int QuickApplied { get; }
        public int EquipsApplied { get; }
        public int UnknownItemCount { get; }
        public int OverflowItemCount { get; }
        public int InvalidEquipSlotCount { get; }
        public int UnmodeledAvatarBagCount { get; }
        public int UnmodeledCargoCount { get; }
        public int UnmodeledPetBagCount { get; }
        public int UnmodeledAccountCargoCount { get; }
        public int UnmodeledExpandEquipCount { get; }
    }

    /// <summary>
    /// 供 <c>Dnf.Client</c> 使用的物品类型查询入口(签名不暴露任何 <c>Dnf.Game</c> 类型,避免严格模式下 CS0012):
    /// 返回 <c>0=Unknown, 1=Other, 2=Avatar, 3=Pet</c>,永不抛异常。
    /// </summary>
    public static class OnlineItemKindLookup
    {
        public const int KindUnknown = 0;
        public const int KindOther = 1;
        public const int KindAvatar = 2;
        public const int KindPet = 3;

        private sealed class CatalogBox
        {
            public readonly object Sync = new object();
            public readonly ItemCatalog Catalog;

            public CatalogBox(DnfAssets assets)
            {
                Catalog = new ItemCatalog(new GameData(new PvfScriptSource(assets.Pvf)));
            }
        }

        private static readonly ConditionalWeakTable<DnfAssets, CatalogBox> Cache = new ConditionalWeakTable<DnfAssets, CatalogBox>();

        /// <summary>
        /// 按 <paramref name="assets"/> 查询 <paramref name="itemId"/> 对应的线格式类型编码:
        /// <c>0=Unknown, 1=Other, 2=Avatar(Part 0..9), 3=Pet(Part == 22)</c>;缺失或异常时返回 0。
        /// </summary>
        public static int ResolveKindCode(DnfAssets assets, uint itemId)
        {
            if (assets == null || itemId > int.MaxValue)
            {
                return KindUnknown;
            }
            try
            {
                CatalogBox box = Cache.GetValue(assets, a => new CatalogBox(a));
                ItemInfo info;
                lock (box.Sync)
                {
                    info = box.Catalog.Get((int)itemId);
                }
                if (info == null)
                {
                    return KindUnknown;
                }
                if (info.IsEquipment)
                {
                    if (EquipmentParts.IsAvatar(info.Part))
                    {
                        return KindAvatar;
                    }
                    if (info.Part == EquipmentParts.Creature)
                    {
                        return KindPet;
                    }
                }
                return KindOther;
            }
            catch (Exception)
            {
                return KindUnknown;
            }
        }
    }
}
