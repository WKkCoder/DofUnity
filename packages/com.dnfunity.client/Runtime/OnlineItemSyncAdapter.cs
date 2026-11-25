using System;
using System.Collections.Generic;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client
{
    /// <summary>
    /// 纯适配器:把 Net 层的 <see cref="ItemSyncSnapshot"/> 转成 Play 层中立的 <see cref="OnlineItemSyncData"/>,
    /// 不接触本地存档,也不引用 <c>Dnf.Game</c>。
    /// </summary>
    public static class OnlineItemSyncAdapter
    {
        public const ushort QuickSlotFirst = 3;
        public const ushort QuickSlotLast = 8;
        public const ushort BagItemSlotFirst = 9;
        public const ushort WornEquipSlotFirst = 10;
        public const ushort WornEquipSlotLast = 21;

        public static OnlineItemSyncData ToPlayData(ItemSyncSnapshot snapshot, ushort? selfUid = null)
        {
            if (snapshot == null)
            {
                return new OnlineItemSyncData(false, 0L, null, null, null, false, false);
            }

            bool hasGold = snapshot.Currency != null && snapshot.Currency.Gold.HasValue;
            long gold = hasGold ? snapshot.Currency.Gold.Value : 0L;

            bool hasBagData = snapshot.Bag != null;
            var quickEntries = new List<OnlineItemSlotEntry>();
            var bagEntries = new List<OnlineItemSlotEntry>();
            if (snapshot.Bag != null)
            {
                IReadOnlyList<ItemEntry> entries = snapshot.Bag.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    ItemEntry entry = entries[i];
                    if (entry == null)
                    {
                        continue;
                    }
                    if (entry.Slot >= QuickSlotFirst && entry.Slot <= QuickSlotLast)
                    {
                        quickEntries.Add(ToSlotEntry(entry, isEquip: false));
                    }
                    else if (entry.Slot >= BagItemSlotFirst)
                    {
                        bagEntries.Add(ToSlotEntry(entry, isEquip: false));
                    }
                }
            }

            bool hasEquipData = snapshot.SelfView != null || snapshot.BodyEquip != null;
            var wornBySlot = new Dictionary<ushort, OnlineItemSlotEntry>();
            if (snapshot.SelfView != null)
            {
                IReadOnlyList<ItemEntry> equips = snapshot.SelfView.DetailedEquips;
                for (int i = 0; i < equips.Count; i++)
                {
                    ItemEntry entry = equips[i];
                    if (entry != null && IsWornEquipSlot(entry.Slot))
                    {
                        wornBySlot[entry.Slot] = ToSlotEntry(entry, isEquip: true);
                    }
                }
            }
            if (snapshot.BodyEquip != null)
            {
                IReadOnlyList<ItemEntry> body = snapshot.BodyEquip.Entries;
                for (int i = 0; i < body.Count; i++)
                {
                    ItemEntry entry = body[i];
                    if (entry != null && IsWornEquipSlot(entry.Slot))
                    {
                        wornBySlot[entry.Slot] = ToSlotEntry(entry, isEquip: true);
                    }
                }
            }

            OnlineItemSlotEntry[] wornEquips = SortedEntries(wornBySlot);

            return new OnlineItemSyncData(
                hasGold,
                gold,
                bagEntries,
                quickEntries,
                wornEquips,
                hasBagData,
                hasEquipData,
                unmodeledAvatarBagCount: snapshot.AvatarBag != null ? snapshot.AvatarBag.Count : 0,
                unmodeledCargoCount: snapshot.Cargo != null ? snapshot.Cargo.Count : 0,
                unmodeledPetBagCount: snapshot.PetBag != null ? snapshot.PetBag.Count : 0,
                unmodeledAccountCargoCount: snapshot.AccountCargo != null ? snapshot.AccountCargo.Count : 0,
                unmodeledExpandEquipCount: CountExpandEquips(snapshot.ExpandEquips, selfUid));
        }

        internal static bool IsWornEquipSlot(ushort slot)
        {
            return slot >= WornEquipSlotFirst && slot <= WornEquipSlotLast;
        }

        internal static OnlineItemSlotEntry ToSlotEntry(ItemEntry entry, bool isEquip)
        {
            int dataClamped = entry.Data > int.MaxValue ? int.MaxValue : (int)entry.Data;
            int count = isEquip ? 1 : dataClamped;
            int gradeValue = !entry.DataIsRemainingTime ? dataClamped : 0;
            return new OnlineItemSlotEntry(
                entry.Slot,
                entry.ItemId,
                count,
                entry.UpgradeLevel,
                entry.IsAmplified,
                entry.IsIdentified,
                entry.EnchantIndex,
                gradeValue);
        }

        internal static OnlineItemSlotEntry[] SortedEntries(Dictionary<ushort, OnlineItemSlotEntry> bySlot)
        {
            if (bySlot.Count == 0)
            {
                return Array.Empty<OnlineItemSlotEntry>();
            }
            ushort[] keys = new ushort[bySlot.Count];
            bySlot.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            var sorted = new OnlineItemSlotEntry[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                sorted[i] = bySlot[keys[i]];
            }
            return sorted;
        }

        internal static int CountExpandEquips(IReadOnlyList<ExpandEquipNotice> notices, ushort? selfUid)
        {
            if (notices == null || notices.Count == 0)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < notices.Count; i++)
            {
                ExpandEquipNotice notice = notices[i];
                if (notice == null)
                {
                    continue;
                }
                if (selfUid.HasValue && notice.Uid != selfUid.Value)
                {
                    continue;
                }
                IReadOnlyList<ExpandEquipGroup> groups = notice.Groups;
                for (int g = 0; g < groups.Count; g++)
                {
                    ExpandEquipGroup group = groups[g];
                    if (group != null)
                    {
                        count += group.Entries.Count;
                    }
                }
            }
            return count;
        }
    }
}
