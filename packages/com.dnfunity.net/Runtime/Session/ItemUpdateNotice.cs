using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品增量包里的一次槽操作:要么"把该槽换成这个条目"(upsert),要么"清空该槽"(物品号 <see cref="ItemEntryReader.DeleteMarker"/>)。
    /// 货币槽(空间 0 的槽 0、1、2)不在这里,见 <see cref="ItemUpdateNotice.Currency"/>。
    /// </summary>
    public readonly struct ItemUpdateOperation
    {
        internal ItemUpdateOperation(ushort slot, ItemEntry entry)
        {
            Slot = slot;
            Entry = entry;
        }

        /// <summary>槽号。</summary>
        public ushort Slot { get; }

        /// <summary>要放进该槽的条目;清空该槽时为 null。</summary>
        public ItemEntry Entry { get; }

        /// <summary>是不是清空该槽(物品号为删除标记)。</summary>
        public bool IsDelete => Entry == null;
    }

    /// <summary>
    /// 物品增量包(类 0、包号 14,服务端 <c>SendUpdateItem</c> 一族)的解析结果:某个物品空间里若干槽的<b>补丁</b>,
    /// 叠在已有状态上,不是全量(调用方按"逐槽 Apply"归约,不能"后到者覆盖整个空间")。
    /// 操作按线上顺序给出,同一槽在一个包里出现多次时以后者为准。不可变;构造入口只给解析器。
    /// </summary>
    public sealed class ItemUpdateNotice
    {
        private readonly IReadOnlyList<ItemUpdateOperation> _operations;

        internal ItemUpdateNotice(
            ItemSpace space,
            int declaredEntryCount,
            ItemUpdateOperation[] operations,
            ItemCurrency currency,
            int consumedByteCount,
            int trailingByteCount)
        {
            Space = space;
            DeclaredEntryCount = declaredEntryCount;
            _operations = Array.AsReadOnly(operations ?? new ItemUpdateOperation[0]);
            Currency = currency ?? ItemCurrency.Empty;
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>空间号(线上第一个字节)。空间 3 是身上装备(穿脱分支),槽号是装备部位号。</summary>
        public ItemSpace Space { get; }

        /// <summary>包头声明的条目个数(含货币槽与删除条目)。</summary>
        public int DeclaredEntryCount { get; }

        /// <summary>槽操作(不含货币槽),按线上顺序。</summary>
        public IReadOnlyList<ItemUpdateOperation> Operations => _operations;

        /// <summary>
        /// 货币刷新(只有空间 0 的槽 0、1、2 产生):包里出现的货币槽有值,其余为 null。
        /// 服务端在进城后用 <c>SendCoin</c> 经这条路径刷新一次金币类数额(本机抓帧里唯一的一个增量包即是槽 1 的刷新,笔记 §2.3.3)。
        /// 没有货币槽时是 <see cref="ItemCurrency.Empty"/>。
        /// </summary>
        public ItemCurrency Currency { get; }

        /// <summary>解析消耗的字节数(从载荷起点到最后一个条目末尾)。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>最后一个条目之后还剩的字节数(包尾校验与补零,只计数,不当条目解析)。</summary>
        public int TrailingByteCount { get; }

        /// <summary>是否有任何槽操作或货币刷新。</summary>
        public bool IsEmpty => _operations.Count == 0 && Currency.IsEmpty;
    }
}
