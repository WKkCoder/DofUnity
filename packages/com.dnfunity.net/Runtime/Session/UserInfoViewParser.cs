using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 用户信息通知(类 0、包号 2)的模式 0 / 模式 1 装备视图解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// <b>与既有 <see cref="UserInfoParser"/> 的分工</b>:后者只认模式 2(角色选择列表),本解析器只认模式 0 与 1,互不越界;
    /// 模式字节是别的值一律 <see cref="UserInfoViewStatus.UnsupportedMode"/>。包号 2 在线上是 zlib 压缩的,
    /// <see cref="ServerFrameDecoder"/> 解密之后已解压,这里拿到的就是从模式字节起的明文,末尾仍带 4 字节包尾校验。
    /// </para>
    /// <para>
    /// 模式 0(公共视图,客户端 0x00514080,服务端 <c>make_basic_info</c> 0x0865a673):
    /// <c>u8 0, u16 个数(必须为 1), u16 uid, u32 名字长度 + 名字, 6 个 u8, 紧凑装备列表(u8 n + n × 22), 1 个尾字节, …非物品字段</c>。
    /// </para>
    /// <para>
    /// 模式 1(本人详细视图,客户端条目函数 0x00514c30,服务端 0x0865b007..0x0865b6d3):
    /// <c>u8 1, u16 个数(必须为 1), u16 X, u32 Y, u32 统计块长度 + 统计块, u8, 详细装备列表(u8 n + n × 27 字节起), …非物品字段</c>。
    /// 本解析器只<b>定位</b>装备列表(偏移 = 14 + 统计块长度),统计块与列表之后的非物品字段不解释(笔记 §2.2.5)。
    /// </para>
    /// <para>
    /// 个数必须为 1:本机观察到的两个视图都是单条目,条目个数大于 1 时第二条的起点取决于第一条的"列表之后的非物品字段"长度,
    /// 而那一段不解释,所以不去猜。
    /// </para>
    /// </summary>
    public static class UserInfoViewParser
    {
        /// <summary>用户信息通知的包号(类 0)。</summary>
        public const int PacketId = 2;

        /// <summary>公共视图的模式字节。</summary>
        public const int PublicViewMode = 0;

        /// <summary>本人详细视图的模式字节。</summary>
        public const int SelfViewMode = 1;

        private const int HeaderByteCount = 6;

        /// <summary>解析(无物品类型回调:详细装备记录的装扮块、宠物 u32 按槽位近似判定)。</summary>
        public static bool TryParse(byte[] payload, out UserInfoViewNotice notice)
        {
            return TryParse(payload, null, out notice, out _);
        }

        /// <summary>解析并给出结论,无物品类型回调。</summary>
        public static bool TryParse(byte[] payload, out UserInfoViewNotice notice, out UserInfoViewStatus status)
        {
            return TryParse(payload, null, out notice, out status);
        }

        /// <summary>
        /// 解析并给出结论。<paramref name="kindResolver"/> 只对模式 1 的详细装备记录有用(见 <see cref="ItemEntryShape.EquipDetail"/>):
        /// 回调为 null 或回答 Unknown 时按槽位近似判定(槽 ≤ 9 装扮、槽 22 宠物,样本没触发过、未经验证)。
        /// </summary>
        public static bool TryParse(byte[] payload, IItemKindResolver kindResolver, out UserInfoViewNotice notice, out UserInfoViewStatus status)
        {
            notice = null;
            status = UserInfoViewStatus.Malformed;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte mode))
            {
                return false;
            }
            if (mode != PublicViewMode && mode != SelfViewMode)
            {
                status = UserInfoViewStatus.UnsupportedMode;
                return false;
            }
            if (!reader.TryGetUShort(out ushort count) || count != 1)
            {
                return false;
            }

            bool ok = mode == PublicViewMode
                ? TryParsePublic(payload, reader, out notice)
                : TryParseSelf(payload, reader, kindResolver, out notice);
            if (!ok)
            {
                notice = null;
                return false;
            }
            status = UserInfoViewStatus.Ok;
            return true;
        }

        private static bool TryParsePublic(byte[] payload, PacketReader reader, out UserInfoViewNotice notice)
        {
            notice = null;
            if (!reader.TryGetUShort(out ushort uid) || !reader.TryGetUInt(out uint nameLength))
            {
                return false;
            }
            // 名字长度只对剩余字节做上限(客户端的 1 到 30 限制与失步不模拟);按无符号比较,高位为 1 时不会被当成负数放过去。
            if (nameLength > (uint)reader.Remaining)
            {
                return false;
            }
            byte[] name = new byte[0];
            if (nameLength > 0 && !reader.TryGetBinary((int)nameLength, out name))
            {
                return false;
            }
            byte[] headerBytes = new byte[HeaderByteCount];
            for (int i = 0; i < HeaderByteCount; i++)
            {
                if (!reader.TryGetByte(out headerBytes[i]))
                {
                    return false;
                }
            }

            int listOffset = reader.Position;
            if (!EquipListReader.TryReadCompact(reader, true, out EquipCompactRecord[] records, out byte trailingByte))
            {
                return false;
            }

            notice = new UserInfoViewNotice(
                PublicViewMode,
                uid,
                name,
                headerBytes,
                records,
                trailingByte,
                0,
                0,
                0,
                0,
                null,
                listOffset,
                reader.Position - listOffset,
                reader.Position,
                reader.Remaining);
            return true;
        }

        private static bool TryParseSelf(byte[] payload, PacketReader reader, IItemKindResolver kindResolver, out UserInfoViewNotice notice)
        {
            notice = null;
            if (!reader.TryGetUShort(out ushort x)
                || !reader.TryGetUInt(out uint y)
                || !reader.TryGetUInt(out uint statLength))
            {
                return false;
            }
            // 统计块长度前缀没有上限守卫,所以只对剩余字节做上限(无符号比较);块内容不解释,不拷贝,直接跳过。
            if (statLength > (uint)reader.Remaining)
            {
                return false;
            }
            PacketReader afterStat = new PacketReader(payload, reader.Position + (int)statLength, payload.Length);
            if (!afterStat.TryGetByte(out byte selfByte))
            {
                return false;
            }

            int listOffset = afterStat.Position;
            if (!EquipListReader.TryReadDetailed(afterStat, kindResolver, 0, EquipListReader.SelfDetailMaxSlot, out ItemEntry[] entries))
            {
                return false;
            }

            notice = new UserInfoViewNotice(
                SelfViewMode,
                0,
                null,
                null,
                null,
                0,
                x,
                y,
                (int)statLength,
                selfByte,
                entries,
                listOffset,
                afterStat.Position - listOffset,
                afterStat.Position,
                afterStat.Remaining);
            return true;
        }
    }
}
