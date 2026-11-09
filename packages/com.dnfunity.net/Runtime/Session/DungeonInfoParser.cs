using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本信息通知(类 0、包号 <see cref="PacketId"/>)的明文载荷解析器:把客户端静态读码定案的读序落成只读记录 <see cref="DungeonInfoNotice"/>。
    /// 真实客户端进副本靠服务端依次推 27、28、29 三个通知,28 带的是副本对象的参数。
    /// <para>
    /// 范围:纯明文解析。这批通知在探针的明文流里被封在 561 信封内,信封内层读通之后,把内层帧的载荷直接喂给 <see cref="TryParse"/>;
    /// 解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="TownEntryParser"/> 同风格):短包、读越界一律返回 false 且 notice 为 null,绝不抛异常(包括 null 载荷);
    /// 读完最后一个字段(G)即停,容忍尾部多余字节(只在 <see cref="DungeonInfoNotice.TrailingByteCount"/> 报告个数,不核对);
    /// 字段取值不做范围检查(语义未定,没有可核对的范围)。
    /// </para>
    /// <para>
    /// 读序(小端;clire_NOTES §1.4、§2.5 的【观】,客户端处理器逐个读取点核对过,读取点之间没有跳过读取的条件分支):
    /// <c>u16 副本编号, u8 B1, u8 B2, u8 B3, u8 B4, u8 C1, u8 C2, u8 D, u8 n, n × {u8 E1, u8 E2}, u16 F, u8 G</c>,共 13 + 2n 字节。
    /// 这之后客户端的另一段代码还会读 4 字节尾值(包完整性监视表的槽 7);解析器不消费它。
    /// </para>
    /// <para>
    /// 没有模拟的客户端行为:客户端读完 B4 就拿副本编号去自带的副本表里取对象,取不到就直接退出、后面的字段都不读。
    /// 解析器没有副本表,总是要求完整读序;所以"成功"只表示布局读得下来,不表示客户端会处理这个副本。
    /// </para>
    /// </summary>
    public static class DungeonInfoParser
    {
        /// <summary>副本信息通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 28;

        /// <summary>
        /// 解析一份载荷(从第一个字段起)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>读完全部字段为 true;载荷为 null、过短或某个个数声明超出载荷为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out DungeonInfoNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort dungeonId)
                || !reader.TryGetByte(out byte b1)
                || !reader.TryGetByte(out byte b2)
                || !reader.TryGetByte(out byte b3)
                || !reader.TryGetByte(out byte b4)
                || !reader.TryGetByte(out byte c1)
                || !reader.TryGetByte(out byte c2)
                || !reader.TryGetByte(out byte d)
                || !reader.TryGetByte(out byte pairCount))
            {
                return false;
            }

            var pairs = new DungeonInfoPair[pairCount];
            for (int i = 0; i < pairs.Length; i++)
            {
                if (!reader.TryGetByte(out byte e1) || !reader.TryGetByte(out byte e2))
                {
                    return false;
                }
                pairs[i] = new DungeonInfoPair(e1, e2);
            }

            if (!reader.TryGetUShort(out ushort f) || !reader.TryGetByte(out byte g))
            {
                return false;
            }

            notice = new DungeonInfoNotice(dungeonId, b1, b2, b3, b4, c1, c2, d, pairs, f, g, reader.Remaining);
            return true;
        }
    }
}
