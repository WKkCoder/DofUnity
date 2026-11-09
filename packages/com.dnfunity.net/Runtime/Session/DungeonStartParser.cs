using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本开始通知(类 0、包号 <see cref="PacketId"/>)的明文载荷解析器:把客户端静态读码定案的读序落成只读记录 <see cref="DungeonStartNotice"/>。
    /// 真实客户端进副本靠服务端依次推 27、28、29 三个通知;27 最先到,处理器在读第一个字段之前就把 core 置为 state 2
    /// (装载态,0x0074080e..0x00740817【观】),装载界面与阶段机随后才启动。包名表里它叫 START_GAME。
    /// <para>
    /// 范围:纯明文解析。这批通知在探针的明文流里被封在 561 信封内,信封内层读通之后,把内层帧的载荷直接喂给 <see cref="TryParse"/>;
    /// 解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="TownEntryParser"/> 同风格):短包、读越界一律返回 false 且 notice 为 null,绝不抛异常(包括 null 载荷);
    /// 读完最后一个字段(flag)即停,容忍尾部多余字节(只在 <see cref="DungeonStartNotice.TrailingByteCount"/> 报告个数,不核对);
    /// 字段取值不做范围检查(语义未定,没有可核对的范围)。
    /// </para>
    /// <para>
    /// 读序(小端;clire_NOTES §1.4、§2.5 的【观】,客户端处理器 0x007407d0..0x0074127b):
    /// <c>i32 A, u8 n, n × u16, u8 cnt1, cnt1 × {u16 memberId, u8 cnt2, cnt2 × {u16, i32, i32, i32}}, i32 B, u8 flag</c>,
    /// 最短(n = 0、cnt1 = 0)11 字节;总长 = 11 + 2n + Σ(3 + 14 × cnt2)。
    /// </para>
    /// <para>
    /// 【客户端行为,解析器都不模拟】共两处,都与"不做范围检查、按结构直读"的约定一致:
    /// (1) n 个 u16 在客户端里读进栈上的局部数组,这个数组只够放 4 个(n 应不超过 4,clire_NOTES §1.4【推】);
    /// 解析器不做这个上限检查,n 声明多少就读多少个(上限只有 u8 的 255)。
    /// (2) 每个成员组里,客户端拿 memberId 去队伍里查成员(0x00924dd0);查不到时它跳过每项 4 个字段的读取、读游标却不前移
    /// (0x00740cb4..0x00740d43 一带,这个分支存在是【观】),后面的字节于是被错当成别的字段,整个流错位。
    /// 那是客户端自己的行为:解析器不查队伍、不看 memberId 的取值,总是按结构把每项 14 字节读完。
    /// 所以"解析成功"只表示布局读得下来,不表示客户端会按同一个字节边界消费这个包。
    /// </para>
    /// <para>
    /// 尾巴:客户端读完 flag 就不再读,27 不在包完整性监视表里(监视表从 28 起,clire_NOTES §2.13【观】),所以没有"处理器之后另读的 4 字节尾值"。
    /// 真实帧里尾部若还有字节(折叠哈希、会话加密补零,或服务端自行追加的完整性尾值),解析器同样不消费、不核对,
    /// 只在 <see cref="DungeonStartNotice.TrailingByteCount"/> 报告个数,留待信封解开后与真实帧对账。
    /// </para>
    /// </summary>
    public static class DungeonStartParser
    {
        /// <summary>副本开始通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 27;

        /// <summary>
        /// 解析一份载荷(从第一个字段起)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>读完全部字段为 true;载荷为 null、过短或某个个数声明超出载荷为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out DungeonStartNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetInt(out int a) || !TryReadValues(reader, out ushort[] values))
            {
                return false;
            }

            if (!TryReadMembers(reader, out DungeonStartMember[] members))
            {
                return false;
            }

            if (!reader.TryGetInt(out int b) || !reader.TryGetByte(out byte flag))
            {
                return false;
            }

            notice = new DungeonStartNotice(a, values, members, b, flag, reader.Remaining);
            return true;
        }

        // u8 个数 n + n 个 u16(客户端的局部数组只够 4 个,这里不设上限,见类说明)。
        private static bool TryReadValues(PacketReader reader, out ushort[] values)
        {
            values = null;
            if (!reader.TryGetByte(out byte valueCount))
            {
                return false;
            }

            var list = new ushort[valueCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort value))
                {
                    return false;
                }
                list[i] = value;
            }

            values = list;
            return true;
        }

        // u8 个数 cnt1 + cnt1 个成员组,每组 u16 memberId、u8 cnt2、cnt2 项(成员查不到时客户端会错位,解析器不模拟,见类说明)。
        private static bool TryReadMembers(PacketReader reader, out DungeonStartMember[] members)
        {
            members = null;
            if (!reader.TryGetByte(out byte memberCount))
            {
                return false;
            }

            var list = new DungeonStartMember[memberCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort memberId) || !TryReadEntries(reader, out DungeonStartEntry[] entries))
                {
                    return false;
                }
                list[i] = new DungeonStartMember(memberId, entries);
            }

            members = list;
            return true;
        }

        // u8 个数 cnt2 + cnt2 项 {u16 A, i32 B, i32 C, i32 D},每项 14 字节。
        private static bool TryReadEntries(PacketReader reader, out DungeonStartEntry[] entries)
        {
            entries = null;
            if (!reader.TryGetByte(out byte entryCount))
            {
                return false;
            }

            var list = new DungeonStartEntry[entryCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort a)
                    || !reader.TryGetInt(out int b)
                    || !reader.TryGetInt(out int c)
                    || !reader.TryGetInt(out int d))
                {
                    return false;
                }
                list[i] = new DungeonStartEntry(a, b, c, d);
            }

            entries = list;
            return true;
        }
    }
}
