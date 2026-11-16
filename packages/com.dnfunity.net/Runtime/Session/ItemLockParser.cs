using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>物品锁表的一项:键是(空间号,槽号),再加锁类型,锁类型为 2 时多一个 u32。</summary>
    public readonly struct ItemLockEntry
    {
        internal ItemLockEntry(byte space, ushort slot, byte lockType, bool hasExtra, uint extra)
        {
            Space = space;
            Slot = slot;
            LockType = lockType;
            HasExtra = hasExtra;
            Extra = extra;
        }

        /// <summary>空间号(与物品空间包同一套编号,0 背包等;[推断] 来自服务端按槽查背包 <c>GetCurCharacInventoryRef</c> 的用法,没有非空样本)。</summary>
        public byte Space { get; }

        /// <summary>槽号。</summary>
        public ushort Slot { get; }

        /// <summary>锁类型(u8 原值,语义未验证)。</summary>
        public byte LockType { get; }

        /// <summary>是否带额外的 u32(锁类型恰为 2)。</summary>
        public bool HasExtra { get; }

        /// <summary>额外的 u32(<see cref="HasExtra"/> 为假时为 0)。服务端写入点 0x085429a2 的值与 <c>CSystemTime::getCurSec</c> 有关,[推断] 是时间量;未验证。</summary>
        public uint Extra { get; }
    }

    /// <summary>物品锁表(类 0、包号 251)的解析结果。不可变;构造入口只给 <see cref="ItemLockParser"/>。</summary>
    public sealed class ItemLockNotice
    {
        private readonly IReadOnlyList<ItemLockEntry> _entries;

        internal ItemLockNotice(ItemLockEntry[] entries, int consumedByteCount, int trailingByteCount)
        {
            _entries = Array.AsReadOnly(entries ?? new ItemLockEntry[0]);
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>各个锁定项,按线上顺序。</summary>
        public IReadOnlyList<ItemLockEntry> Entries => _entries;

        /// <summary>解析消耗的字节数。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>读完之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 物品锁表(类 0、包号 251)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// 布局(小端;客户端处理器 0x0073ddb0,服务端 <c>CItemLock::SendItemLockList</c> 0x085427a0,笔记 §2.6):
    /// <c>u16 个数 n + n × (u8 空间号, u16 槽号, u8 锁类型, [u32,仅锁类型恰为 2])</c>。
    /// 条件字段的判据来自逐指令对账:服务端写锁类型之后把一个局部量与 2 比较(0x08542964),相等才写 u32(0x085429a2),
    /// 客户端读锁类型之后同样比较并条件读 u32(0x0073de43..0x0073de4c)。
    /// <b>本机抓帧只有空表样本</b>(个数 0),所以非空路径只有读码依据,没有字节样本核对。
    /// </para>
    /// <para>个数先对剩余字节(每项至少 4 字节,防谎报);读完即停,之后的字节只计数。</para>
    /// </summary>
    public static class ItemLockParser
    {
        /// <summary>物品锁表的包号(类 0)。</summary>
        public const int PacketId = 251;

        /// <summary>带额外 u32 的锁类型。</summary>
        public const byte ExtraLockType = 2;

        private const int MinEntryLength = 4;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out ItemLockNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort count) || (long)count * MinEntryLength > reader.Remaining)
            {
                return false;
            }

            ItemLockEntry[] list = new ItemLockEntry[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryGetByte(out byte space)
                    || !reader.TryGetUShort(out ushort slot)
                    || !reader.TryGetByte(out byte lockType))
                {
                    return false;
                }
                uint extra = 0;
                bool hasExtra = lockType == ExtraLockType;
                if (hasExtra && !reader.TryGetUInt(out extra))
                {
                    return false;
                }
                list[i] = new ItemLockEntry(space, slot, lockType, hasExtra, extra);
            }
            notice = new ItemLockNotice(list, reader.Position, reader.Remaining);
            return true;
        }
    }
}
