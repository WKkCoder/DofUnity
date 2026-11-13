using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 备用装备组包(类 0、包号 339)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// 布局(小端;客户端处理器 0x00eab4e0,服务端写入器 0x0849a57c,读写序逐项一致,笔记 §2.4):
    /// <c>u16 唯一号</c>,然后循环 3 次:<c>u8 组号 + 详细装备列表(u8 n + n × 记录,槽号 10 到 21)</c>。
    /// 详细记录走 <see cref="ItemEntryReader"/> 的装备详细记录形;装扮块与宠物 u32 的判定见 <see cref="ItemEntryShape.EquipDetail"/>
    /// (无回调时按槽位近似;备用组只含槽 10 到 21,近似下不会读装扮块,也不会读宠物 u32)。
    /// </para>
    /// <para>
    /// 过滤"是不是本人"是消费方的事:客户端只在唯一号等于本机角色时才处理,解析器只交出唯一号。
    /// 尾部容忍:读完第三组即停,之后的字节(包尾校验与补零)只计数。
    /// </para>
    /// </summary>
    public static class ExpandEquipParser
    {
        /// <summary>备用装备组包的包号(类 0)。</summary>
        public const int PacketId = 339;

        /// <summary>组数:线上恒 3 组。</summary>
        public const int GroupCount = 3;

        /// <summary>解析,无物品类型回调。</summary>
        public static bool TryParse(byte[] payload, out ExpandEquipNotice notice)
        {
            return TryParse(payload, null, out notice);
        }

        /// <summary>解析;<paramref name="kindResolver"/> 可为 null。</summary>
        public static bool TryParse(byte[] payload, IItemKindResolver kindResolver, out ExpandEquipNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort uid))
            {
                return false;
            }

            ExpandEquipGroup[] groups = new ExpandEquipGroup[GroupCount];
            for (int i = 0; i < GroupCount; i++)
            {
                if (!reader.TryGetByte(out byte groupId)
                    || !EquipListReader.TryReadDetailed(reader, kindResolver, EquipListReader.ExpandGroupMinSlot, EquipListReader.ExpandGroupMaxSlot, out ItemEntry[] entries))
                {
                    return false;
                }
                groups[i] = new ExpandEquipGroup(groupId, entries);
            }

            notice = new ExpandEquipNotice(uid, groups, reader.Position, reader.Remaining);
            return true;
        }
    }
}
