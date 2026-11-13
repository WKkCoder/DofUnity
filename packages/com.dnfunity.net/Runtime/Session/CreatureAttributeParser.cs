using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 宠物属性表包(类 0、包号 105)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// 布局(小端;客户端处理器 0x0072e4c0,服务端写入器 0x083409e0..0x08340c82,读写序逐项一致,笔记 §2.5):
    /// <c>u8 个数 n</c>(0 时到此为止),随后 n 条:
    /// <c>u32 uid, u8 饱腹度, u8 成长标志, u32 经验, [仅成长标志为 0: u8, u8], u8 等级, u32 名字长度 + 名字, u8 免费标志</c>。
    /// 名字长度只对剩余字节做上限(客户端对长度 ≥ 0x1f 不读字节的失步不模拟,与既有 <see cref="UserInfoParser"/> 同口径)。
    /// </para>
    /// <para>尾部容忍:读完最后一只即停,之后的字节(包尾校验与补零)只计数。</para>
    /// </summary>
    public static class CreatureAttributeParser
    {
        /// <summary>宠物属性表包的包号(类 0)。</summary>
        public const int PacketId = 105;

        // 每条最短:uid 4 + 饱腹 1 + 标志 1 + 经验 4 + 等级 1 + 名字长度 4 + 免费 1 = 16(非成长分支、名字 0 字节)。
        private const int MinEntryLength = 16;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out CreatureAttributeNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte count))
            {
                return false;
            }
            // 防谎报:个数 × 每条最短长度超过剩余字节,就不去分配。
            if (count * MinEntryLength > reader.Remaining)
            {
                return false;
            }

            CreatureAttributeEntry[] list = new CreatureAttributeEntry[count];
            for (int i = 0; i < count; i++)
            {
                if (!TryReadEntry(reader, out CreatureAttributeEntry entry))
                {
                    return false;
                }
                list[i] = entry;
            }

            notice = new CreatureAttributeNotice(list, reader.Position, reader.Remaining);
            return true;
        }

        private static bool TryReadEntry(PacketReader reader, out CreatureAttributeEntry entry)
        {
            entry = null;
            if (!reader.TryGetUInt(out uint uid)
                || !reader.TryGetByte(out byte stomach)
                || !reader.TryGetByte(out byte flag)
                || !reader.TryGetUInt(out uint exp))
            {
                return false;
            }

            byte growthByte1 = 0;
            byte growthByte2 = 0;
            if (flag == 0 && (!reader.TryGetByte(out growthByte1) || !reader.TryGetByte(out growthByte2)))
            {
                return false;
            }

            if (!reader.TryGetByte(out byte level) || !reader.TryGetUInt(out uint nameLength))
            {
                return false;
            }
            // 名字长度按无符号与剩余字节比较:u32 高位为 1 时不会被当成负数放过去。
            if (nameLength > (uint)reader.Remaining)
            {
                return false;
            }
            byte[] name = new byte[0];
            if (nameLength > 0 && !reader.TryGetBinary((int)nameLength, out name))
            {
                return false;
            }

            if (!reader.TryGetByte(out byte noCharge))
            {
                return false;
            }
            entry = new CreatureAttributeEntry(uid, stomach, flag, exp, growthByte1, growthByte2, level, name, noCharge);
            return true;
        }
    }
}
