using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 紧凑装备记录(线上恒 22 字节):<c>u8 槽号, u32 物品号, u32 长度(必须 4) + 4 字节扩展信息, u8 packed, u32 清除装扮, u32 附魔索引</c>。
    /// 用于用户信息的公共视图(NOTI/2 模式 0);服务端 <c>CInventory::MakeEquipList</c> 的 <c>detailed=0</c> 分支写入(0x084fe571..0x084fe8ba),
    /// 客户端读取器 0x00512890(笔记 §2.2.3)。公共视图只给"外观级"信息:没有耐久、封装、增幅数值、随机选项、C3 与 Data。
    /// </summary>
    public readonly struct EquipCompactRecord
    {
        internal EquipCompactRecord(byte slot, uint itemId, uint expansionInfo, byte packed, uint clearAvatar, uint enchantIndex)
        {
            Slot = slot;
            ItemId = itemId;
            ExpansionInfo = expansionInfo;
            Packed = packed;
            ClearAvatar = clearAvatar;
            EnchantIndex = enchantIndex;
        }

        /// <summary>槽号(部位号;公共视图只含 0 到 11)。</summary>
        public byte Slot { get; }

        /// <summary>物品号。</summary>
        public uint ItemId { get; }

        /// <summary>
        /// 装扮扩展信息:线上是 u32 长度 4 加 4 个字节,这里是那 4 个字节按小端读成的 u32(无损)。
        /// 服务端写 <c>stAvatarExpansionInfo_t</c>,非装扮槽写"空"值(0x084fe651 / 0x084fe704);含义未验证。
        /// </summary>
        public uint ExpansionInfo { get; }

        /// <summary>packed 字节原值:<c>((强化等级 &amp; 0x7F) &lt;&lt; 1) | (增幅类型 != 0)</c>(服务端 0x084fe7d9 / 0x084fe851)。</summary>
        public byte Packed { get; }

        /// <summary>强化等级:packed 的高 7 位(与同槽详细记录 <see cref="ItemEntry.UpgradeLevel"/> 在样本里逐条相等,笔记 §2.2.6)。</summary>
        public int UpgradeLevel => Packed >> 1;

        /// <summary>是否增幅:packed 的最低位(与同槽详细记录的"增幅类型非 0"在样本里逐条相等)。[推断] 语义仅凭服务端写入式与样本对照。</summary>
        public bool IsAmplified => (Packed & 1) != 0;

        /// <summary>"清除装扮"u32(与详细记录同名字段;语义仅凭函数名 <c>GetClearAvatar</c> 推测,未验证)。</summary>
        public uint ClearAvatar { get; }

        /// <summary>附魔索引(服务端 0x084fe896 / 0x084fe8b5 写入;与同槽详细记录的附魔索引在样本里逐条相等)。</summary>
        public uint EnchantIndex { get; }
    }

    /// <summary>
    /// 装备子表读取器:紧凑列表(用户信息公共视图)与详细列表(用户信息自身视图、备用装备组 NOTI/339)共用。
    /// 纯函数,不抛异常;失败返回假,失败后读取器游标位置未定义,调用方应放弃整个包。
    /// <para>
    /// 槽号范围检查:紧凑列表只认槽 0 到 11(服务端循环上界 12,0x084fe333);详细列表由调用方给出允许的范围
    /// (自身视图当前装备组 0 到 25,备用装备组 10 到 21)。范围外的槽号不是这个写入器写得出来的,整表拒绝;
    /// 不核对槽号是否严格递增或互异(客户端也不核对)。
    /// </para>
    /// </summary>
    public static class EquipListReader
    {
        /// <summary>紧凑记录定长:1 + 4 + 4 + 4 + 1 + 4 + 4 = 22。</summary>
        public const int CompactRecordLength = 22;

        /// <summary>紧凑记录里扩展信息的长度前缀(唯一合法值)。</summary>
        public const uint CompactExpansionLength = 4;

        /// <summary>紧凑列表允许的最大槽号(公共视图只含槽 0 到 11)。</summary>
        public const int CompactMaxSlot = 11;

        /// <summary>自身视图当前装备组的详细列表允许的最大槽号(槽 0 到 25)。</summary>
        public const int SelfDetailMaxSlot = 25;

        /// <summary>备用装备组详细列表的最小槽号(线上槽号 = 循环下标 + 10,0x084fe321)。</summary>
        public const int ExpandGroupMinSlot = 10;

        /// <summary>备用装备组详细列表的最大槽号(10 + 11)。</summary>
        public const int ExpandGroupMaxSlot = 21;

        /// <summary>
        /// 读紧凑列表:<c>u8 个数 + 个数 × 22 字节</c>;<paramref name="readTrailingByte"/> 为真时再读列表之后的 1 个尾字节
        /// (服务端仅 <c>detailed=0</c> 时在列表之后写 <c>UpgradeSeparate</c>,0x084feba1 / 0x084febc8;客户端把它读成列表之后的第一个 u8)。
        /// 个数先对照剩余字节(防谎报);扩展信息的长度前缀必须恰为 4;槽号必须在 0 到 11。
        /// </summary>
        public static bool TryReadCompact(PacketReader reader, bool readTrailingByte, out EquipCompactRecord[] records, out byte trailingByte)
        {
            records = null;
            trailingByte = 0;
            if (reader == null || !reader.TryGetByte(out byte count))
            {
                return false;
            }
            if (count * CompactRecordLength > reader.Remaining)
            {
                return false;
            }

            EquipCompactRecord[] list = new EquipCompactRecord[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryGetByte(out byte slot)
                    || !reader.TryGetUInt(out uint itemId)
                    || !reader.TryGetUInt(out uint expansionLength)
                    || expansionLength != CompactExpansionLength
                    || !reader.TryGetUInt(out uint expansionInfo)
                    || !reader.TryGetByte(out byte packed)
                    || !reader.TryGetUInt(out uint clearAvatar)
                    || !reader.TryGetUInt(out uint enchantIndex))
                {
                    return false;
                }
                if (slot > CompactMaxSlot)
                {
                    return false;
                }
                list[i] = new EquipCompactRecord(slot, itemId, expansionInfo, packed, clearAvatar, enchantIndex);
            }

            if (readTrailingByte && !reader.TryGetByte(out trailingByte))
            {
                return false;
            }
            records = list;
            return true;
        }

        /// <summary>
        /// 读详细列表:<c>u8 个数 + 个数 × 详细记录(27 字节起)</c>,没有尾字节。记录走 <see cref="ItemEntryReader"/> 的装备详细记录形;
        /// 槽号必须落在 [<paramref name="minSlot"/>, <paramref name="maxSlot"/>]。装扮块与宠物 u32 的判定见 <see cref="ItemEntryShape.EquipDetail"/>
        /// (回调为 null 或回答 Unknown 时按槽位近似,<b>样本没触发过、未经验证</b>)。
        /// </summary>
        public static bool TryReadDetailed(PacketReader reader, IItemKindResolver kindResolver, int minSlot, int maxSlot, out ItemEntry[] entries)
        {
            entries = null;
            if (reader == null || !reader.TryGetByte(out byte count))
            {
                return false;
            }
            if ((long)count * ItemEntryReader.EquipDetailMinLength > reader.Remaining)
            {
                return false;
            }

            ItemEntry[] list = new ItemEntry[count];
            for (int i = 0; i < count; i++)
            {
                if (!ItemEntryReader.TryRead(reader, ItemEntryShape.EquipDetail, kindResolver, out ItemEntry entry, out _))
                {
                    return false;
                }
                if (entry.Slot < minSlot || entry.Slot > maxSlot)
                {
                    return false;
                }
                list[i] = entry;
            }
            entries = list;
            return true;
        }
    }
}
