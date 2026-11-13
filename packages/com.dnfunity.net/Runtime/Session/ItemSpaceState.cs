using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 归约之后某个物品空间的状态(不可变):由 <see cref="ItemSyncParser"/> 按帧序把物品空间包(整份状态)与物品增量包(逐槽补丁)依次叠出来的结果。
    /// 槽按槽号升序给出,空槽不在列表里(清空一个槽就是把它从列表里去掉)。背包的货币伪条目不在这里,单独在 <see cref="ItemSyncSnapshot.Currency"/>。
    /// 构造入口只给解析器。
    /// </summary>
    public sealed class ItemSpaceState
    {
        private readonly Dictionary<ushort, ItemEntry> _bySlot;
        private readonly IReadOnlyList<ItemEntry> _entries;

        internal ItemSpaceState(
            ItemSpace space,
            bool hasFullState,
            bool hasCapacity,
            ushort capacity,
            bool hasAccountCargoHeader,
            ushort accountCargoWord,
            uint accountCargoDword,
            Dictionary<ushort, ItemEntry> slots,
            int fullPacketCount,
            int updatePacketCount)
        {
            Space = space;
            HasFullState = hasFullState;
            HasCapacity = hasCapacity;
            Capacity = capacity;
            HasAccountCargoHeader = hasAccountCargoHeader;
            AccountCargoWord = accountCargoWord;
            AccountCargoDword = accountCargoDword;
            FullPacketCount = fullPacketCount;
            UpdatePacketCount = updatePacketCount;

            _bySlot = new Dictionary<ushort, ItemEntry>(slots);
            ushort[] keys = new ushort[slots.Count];
            slots.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            ItemEntry[] sorted = new ItemEntry[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                sorted[i] = slots[keys[i]];
            }
            _entries = Array.AsReadOnly(sorted);
        }

        /// <summary>空间号。</summary>
        public ItemSpace Space { get; }

        /// <summary>
        /// 是否收到过这个空间的物品空间包(整份状态)。为假说明状态只由增量包叠成,是<b>不完整</b>的补丁结果
        /// (身上装备空间恒为假:它只有增量包,没有空间包的写入者,笔记 §2.3.1)。
        /// </summary>
        public bool HasFullState { get; }

        /// <summary>最近一个物品空间包是否带容量(背包、仓库带)。</summary>
        public bool HasCapacity { get; }

        /// <summary>最近一个物品空间包里的容量原值(<see cref="HasCapacity"/> 为假时为 0)。增量包不改容量。</summary>
        public ushort Capacity { get; }

        /// <summary>最近一个物品空间包是否带账号仓库的两个额外头字段(只有账号仓库带)。</summary>
        public bool HasAccountCargoHeader { get; }

        /// <summary>账号仓库头里的 u16(语义未验证,按读序命名)。</summary>
        public ushort AccountCargoWord { get; }

        /// <summary>账号仓库头里的 u32(语义未验证,按读序命名)。</summary>
        public uint AccountCargoDword { get; }

        /// <summary>已应用的物品空间包个数(整份状态)。</summary>
        public int FullPacketCount { get; }

        /// <summary>已应用的物品增量包个数(逐槽补丁)。</summary>
        public int UpdatePacketCount { get; }

        /// <summary>非空槽的个数。</summary>
        public int Count => _entries.Count;

        /// <summary>全部非空槽的条目,按槽号升序。</summary>
        public IReadOnlyList<ItemEntry> Entries => _entries;

        /// <summary>取某个槽的条目;空槽返回 null。</summary>
        public ItemEntry GetEntry(ushort slot)
        {
            return _bySlot.TryGetValue(slot, out ItemEntry entry) ? entry : null;
        }

        /// <summary>某个槽是否有物品。</summary>
        public bool IsOccupied(ushort slot)
        {
            return _bySlot.ContainsKey(slot);
        }
    }
}
