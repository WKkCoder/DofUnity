using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>装备等级上限通知(类 0、包号 438)的解析结果。不可变;构造入口只给 <see cref="MaxEquipLevelParser"/>。</summary>
    public sealed class MaxEquipLevelNotice
    {
        internal MaxEquipLevelNotice(ushort level, int trailingByteCount)
        {
            Level = level;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>装备等级上限(u16 原值,服务端 <c>GetCurCharacMaxEquipLevel</c>;[推断] 与物品穿戴限制相关,未验证)。</summary>
        public ushort Level { get; }

        /// <summary>读完之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 装备等级上限通知(类 0、包号 438)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// 布局(小端;客户端处理器 0x00738f10,服务端 <c>CUser::SendMaxEquipLevel</c> 0x086910b2,笔记 §2.6):<c>u16 装备等级上限</c>,正文 2 字节。
    /// 尾部容忍:读完即停,之后的字节只计数。
    /// </summary>
    public static class MaxEquipLevelParser
    {
        /// <summary>装备等级上限通知的包号(类 0)。</summary>
        public const int PacketId = 438;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out MaxEquipLevelNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort level))
            {
                return false;
            }
            notice = new MaxEquipLevelNotice(level, reader.Remaining);
            return true;
        }
    }
}
