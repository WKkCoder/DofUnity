using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品空间包(类 0、包号 13,服务端 <c>send_itemspace</c>)的解析结果:某个物品空间的<b>全量</b>内容。
    /// 同一空间的后到包覆盖先到包(调用方按这个规则归约,见 <c>ItemSyncSnapshot</c>)。
    /// 背包的货币伪条目(槽 0、1、2)已拆到 <see cref="Currency"/>,不在 <see cref="Entries"/> 里。
    /// 不可变;构造入口只给解析器。
    /// </summary>
    public sealed class ItemListNotice
    {
        private readonly IReadOnlyList<ItemEntry> _entries;

        internal ItemListNotice(
            ItemSpace space,
            bool hasCapacity,
            ushort capacity,
            bool hasAccountCargoHeader,
            ushort accountCargoWord,
            uint accountCargoDword,
            int declaredEntryCount,
            ItemEntry[] entries,
            ItemCurrency currency,
            int consumedByteCount,
            int trailingByteCount)
        {
            Space = space;
            HasCapacity = hasCapacity;
            Capacity = capacity;
            HasAccountCargoHeader = hasAccountCargoHeader;
            AccountCargoWord = accountCargoWord;
            AccountCargoDword = accountCargoDword;
            DeclaredEntryCount = declaredEntryCount;
            _entries = Array.AsReadOnly(entries ?? new ItemEntry[0]);
            Currency = currency;
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>空间号(线上第一个字节)。</summary>
        public ItemSpace Space { get; }

        /// <summary>本空间的包头里是否带容量(背包、仓库带;装扮背包、宠物栏、账号仓库不带)。</summary>
        public bool HasCapacity { get; }

        /// <summary>
        /// 容量原值(u16)。背包的容量是"可扩展槽位档":服务端 <c>CSlotBoundChecker</c> 只建 0、8、16 三档(0x08373674,笔记 §2.5),
        /// 但解析器不据此拒包,原值如实给出。<see cref="HasCapacity"/> 为假时为 0。
        /// </summary>
        public ushort Capacity { get; }

        /// <summary>是否带账号仓库的两个额外头字段(只有空间 0xC 带)。</summary>
        public bool HasAccountCargoHeader { get; }

        /// <summary>账号仓库头里的 u16(客户端 0x752bec 读;服务端 0x0828a8de 写 <c>[this+0xd60]</c>,语义未验证,按读序命名)。</summary>
        public ushort AccountCargoWord { get; }

        /// <summary>账号仓库头里的 u32(客户端 0x752c15 读;服务端 0x0828a8f6 写 <c>[this+0xd5c]</c>,语义未验证,按读序命名)。</summary>
        public uint AccountCargoDword { get; }

        /// <summary>包头声明的条目个数(背包包含三个货币伪条目)。</summary>
        public int DeclaredEntryCount { get; }

        /// <summary>真正的物品条目(按线上顺序;背包不含货币伪条目)。</summary>
        public IReadOnlyList<ItemEntry> Entries => _entries;

        /// <summary>
        /// 背包的货币;非背包空间为 null。背包包里槽 0、1、2 缺席的槽在其中为 null。
        /// </summary>
        public ItemCurrency Currency { get; }

        /// <summary>解析消耗的字节数(从载荷起点到最后一个条目末尾)。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>
        /// 最后一个条目之后还剩的字节数。直连帧的载荷 = 包体 + 4 字节包尾校验 + 补零到槽块长(笔记 §1.1),
        /// 所以这里的数值不会是 0;这些字节只计数,绝不当条目解析。
        /// </summary>
        public int TrailingByteCount { get; }
    }
}
