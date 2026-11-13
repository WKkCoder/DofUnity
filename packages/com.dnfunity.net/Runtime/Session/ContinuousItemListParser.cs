using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 持续型物品信息的一项:两个 u32(服务端 <c>ContinuousItemInfo</c> 的两个 int 字段)。
    /// [推断] 按名字:物品号 + 剩余冷却/剩余效果时间;未经样本核对(本机抓帧里两个包都是空表),所以只按读序给原值,不解释。
    /// </summary>
    public readonly struct ContinuousItemEntry
    {
        internal ContinuousItemEntry(uint first, uint second)
        {
            First = first;
            Second = second;
        }

        /// <summary>第一个 u32。</summary>
        public uint First { get; }

        /// <summary>第二个 u32。</summary>
        public uint Second { get; }
    }

    /// <summary>持续型物品信息表(冷却恢复 172 与效果恢复 174 共用的形状)的解析结果。不可变;构造入口只给 <see cref="ContinuousItemListParser"/>。</summary>
    public sealed class ContinuousItemListNotice
    {
        private readonly IReadOnlyList<ContinuousItemEntry> _entries;

        internal ContinuousItemListNotice(ContinuousItemEntry[] entries, int consumedByteCount, int trailingByteCount)
        {
            _entries = Array.AsReadOnly(entries ?? new ContinuousItemEntry[0]);
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>各项,按线上顺序。</summary>
        public IReadOnlyList<ContinuousItemEntry> Entries => _entries;

        /// <summary>解析消耗的字节数。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>读完之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 物品冷却恢复(类 0、包号 172)与物品效果恢复(类 0、包号 174)的解析器:两个包同一个形状,
    /// <c>u8 个数 n + n × (u32, u32)</c>(客户端处理器 0x00750970 / 0x00750b50,服务端 <c>SendCoolTimeItemInfo</c> 0x086591fe / <c>SendEffectItemInfo</c> 0x08659366,笔记 §2.6)。
    /// 纯函数,任何畸形输入都只返回假,不抛异常;个数先对剩余字节(防谎报);读完即停,之后的字节只计数。
    /// 哪个包号对应哪份表由调用方(<see cref="ItemSyncParser"/>)按帧号分派,本解析器不关心。
    /// </summary>
    public static class ContinuousItemListParser
    {
        /// <summary>物品冷却恢复的包号(类 0)。</summary>
        public const int CoolTimePacketId = 172;

        /// <summary>物品效果恢复的包号(类 0)。</summary>
        public const int EffectPacketId = 174;

        private const int EntryLength = 8;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out ContinuousItemListNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte count) || count * EntryLength > reader.Remaining)
            {
                return false;
            }

            ContinuousItemEntry[] list = new ContinuousItemEntry[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryGetUInt(out uint first) || !reader.TryGetUInt(out uint second))
                {
                    return false;
                }
                list[i] = new ContinuousItemEntry(first, second);
            }
            notice = new ContinuousItemListNotice(list, reader.Position, reader.Remaining);
            return true;
        }
    }
}
