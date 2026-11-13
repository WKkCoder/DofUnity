using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品条目的共用读取器:物品空间包(NOTI/13)、物品增量包(NOTI/14)、装备详细列表(用户信息自身视图、NOTI/339)共用同一套主干读序,
    /// 槽号宽度、封装字节、"清除装扮"字段与可选块由 <see cref="ItemEntryShape"/> 决定(笔记 §2.1.4、§2.2.4、§2.3.2)。
    /// <para>
    /// 纪律:每个计数与长度先对照剩余字节再动手(防谎报:C3 个数乘 8、随机选项个数乘 3 超过剩余即截断失败,绝不按声明去分配);
    /// 取值不合的长度字段(镶嵌块不是 30、扩展块不是 4)直接判失败,不"将就着读"。失败后读取器的游标位置未定义,调用方应放弃整个包。
    /// </para>
    /// </summary>
    public static class ItemEntryReader
    {
        /// <summary>增量包里"清空该槽"的物品号(客户端 0x00753720 处理器对此号走清槽分支,笔记 §2.3.1)。</summary>
        public const uint DeleteMarker = 0xFFFFFFFFu;

        /// <summary>
        /// 装备详细记录缺省的装扮近似:槽号不大于此值视为装扮部位(服务端 <c>isAvatarItemType</c> 部位 0 到 9)。
        /// <b>样本没触发过这个近似、未经验证</b>(自身视图的装备里没有装扮与宠物,笔记 §2.2.4):只在回调不可用或回答 Unknown 时才用它。
        /// </summary>
        public const int ApproximateAvatarMaxSlot = 9;

        /// <summary>
        /// 装备详细记录缺省的宠物近似:槽 22 视为宠物槽。同样<b>样本没触发过、未经验证</b>。
        /// </summary>
        public const int ApproximatePetSlot = 22;

        /// <summary>背包 / 仓库 / 宠物栏 / 身上装备形的最短条目长度(C3 与随机选项为空):25 字节。</summary>
        public const int BagMinLength = 25;

        /// <summary>装扮背包形的最短条目长度:25 + 固定的两个块(4 + 30 与 4 + 4)= 67 字节。</summary>
        public const int AvatarBagMinLength = BagMinLength + (4 + ItemEntry.SocketBlockLength) + (4 + ItemEntry.ExpansionBlockLength);

        /// <summary>装备详细记录形的最短条目长度:槽号少 1 字节、没有封装字节、多"清除装扮"4 字节 = 27 字节。</summary>
        public const int EquipDetailMinLength = BagMinLength - 1 - 1 + 4;

        private const int C3RecordLength = 8;
        private const int RandomOptionFieldLength = 3;
        private const int MaxRandomOptionFields = 3;
        private const byte RandomOptionNoTail = 0xFF;
        private const int RandomOptionTailLength = 4;

        /// <summary>某形状的条目至少占多少字节;未知形状返回 <see cref="BagMinLength"/>(读取时会失败)。</summary>
        public static int MinLength(ItemEntryShape shape)
        {
            switch (shape)
            {
                case ItemEntryShape.AvatarBag:
                    return AvatarBagMinLength;
                case ItemEntryShape.EquipDetail:
                    return EquipDetailMinLength;
                default:
                    return BagMinLength;
            }
        }

        /// <summary>
        /// 读一个条目。成功时游标停在条目之后;失败返回假并给出原因(<paramref name="fault"/>),游标位置未定义。
        /// <paramref name="kindResolver"/> 只在需要物品类型的形状里被调用(宠物栏、身上装备、装备详细记录;删除标记条目不调用),可为 null:
        /// 此时宠物栏与身上装备的非删除条目失败(<see cref="ItemEntryFault.KindUnresolved"/>),装备详细记录按槽位近似。
        /// </summary>
        public static bool TryRead(PacketReader reader, ItemEntryShape shape, IItemKindResolver kindResolver, out ItemEntry entry, out ItemEntryFault fault)
        {
            entry = null;
            fault = ItemEntryFault.Truncated;
            if (reader == null)
            {
                return false;
            }
            bool detail = shape == ItemEntryShape.EquipDetail;
            if (!detail
                && shape != ItemEntryShape.Bag
                && shape != ItemEntryShape.AvatarBag
                && shape != ItemEntryShape.PetBag
                && shape != ItemEntryShape.BodyEquip)
            {
                return false;
            }

            int start = reader.Position;

            // 槽号:装备详细记录是 u8,其余是 u16。
            ushort slot;
            if (detail)
            {
                if (!reader.TryGetByte(out byte slotByte))
                {
                    return false;
                }
                slot = slotByte;
            }
            else if (!reader.TryGetUShort(out slot))
            {
                return false;
            }

            if (!reader.TryGetUInt(out uint itemId)
                || !reader.TryGetUInt(out uint data)
                || !reader.TryGetByte(out byte ext1)
                || !reader.TryGetUShort(out ushort durability))
            {
                return false;
            }

            // 耐久之后:详细记录是"清除装扮"u32,其余是封装字节。
            byte sealedByte = 0;
            uint clearAvatar = 0;
            if (detail)
            {
                if (!reader.TryGetUInt(out clearAvatar))
                {
                    return false;
                }
            }
            else if (!reader.TryGetByte(out sealedByte))
            {
                return false;
            }

            if (!reader.TryGetUInt(out uint enchant)
                || !reader.TryGetByte(out byte amplifyType)
                || !reader.TryGetUShort(out ushort amplifyValue))
            {
                return false;
            }

            if (!DecideOptionalBlocks(shape, slot, itemId, kindResolver, out bool hasAvatarBlocks, out bool hasPetTime))
            {
                fault = ItemEntryFault.KindUnresolved;
                return false;
            }

            // 装扮块:u32 长度(必须 30)+ 30 字节,再 u32 长度(必须 4)+ 4 字节。长度字段不合就整条拒绝。
            byte[] socketBlock = null;
            byte[] expansionBlock = null;
            if (hasAvatarBlocks)
            {
                if (!reader.TryGetUInt(out uint socketLength))
                {
                    return false;
                }
                if (socketLength != ItemEntry.SocketBlockLength)
                {
                    fault = ItemEntryFault.InvalidBlockLength;
                    return false;
                }
                if (!reader.TryGetBinary(ItemEntry.SocketBlockLength, out socketBlock))
                {
                    return false;
                }
                if (!reader.TryGetUInt(out uint expansionLength))
                {
                    return false;
                }
                if (expansionLength != ItemEntry.ExpansionBlockLength)
                {
                    fault = ItemEntryFault.InvalidBlockLength;
                    return false;
                }
                if (!reader.TryGetBinary(ItemEntry.ExpansionBlockLength, out expansionBlock))
                {
                    return false;
                }
            }

            uint petRemainTime = 0;
            if (hasPetTime && !reader.TryGetUInt(out petRemainTime))
            {
                return false;
            }

            // C3 块:u8 个数 + 个数 × 8 字节。个数先对照剩余字节(最大 255 × 8,不会溢出)。
            if (!reader.TryGetByte(out byte c3Count))
            {
                return false;
            }
            if (c3Count * C3RecordLength > reader.Remaining)
            {
                return false;
            }
            ItemC3Record[] c3Records = null;
            if (c3Count > 0)
            {
                c3Records = new ItemC3Record[c3Count];
                for (int i = 0; i < c3Count; i++)
                {
                    if (!reader.TryGetUInt(out uint c3Value)
                        || !reader.TryGetByte(out byte c3Byte1)
                        || !reader.TryGetByte(out byte c3Byte2)
                        || !reader.TryGetByte(out byte c3Byte3)
                        || !reader.TryGetByte(out byte c3Byte4))
                    {
                        return false;
                    }
                    c3Records[i] = new ItemC3Record(c3Value, c3Byte1, c3Byte2, c3Byte3, c3Byte4);
                }
            }

            // 随机选项块:u8 个数(0 到 3)+ 个数 × 3 字节;个数大于 0 时再读选择字节与尾部标记 X,X 不为 0xFF 时后面还有 4 字节。
            if (!reader.TryGetByte(out byte randomOptionCount))
            {
                return false;
            }
            if (randomOptionCount > MaxRandomOptionFields)
            {
                fault = ItemEntryFault.InvalidRandomOption;
                return false;
            }
            ItemRandomOptionField[] randomOptionFields = null;
            byte randomOptionSelector = 0;
            byte randomOptionTailMarker = 0;
            byte[] randomOptionTail = null;
            if (randomOptionCount > 0)
            {
                if (randomOptionCount * RandomOptionFieldLength > reader.Remaining)
                {
                    return false;
                }
                randomOptionFields = new ItemRandomOptionField[randomOptionCount];
                for (int i = 0; i < randomOptionCount; i++)
                {
                    if (!reader.TryGetByte(out byte first)
                        || !reader.TryGetByte(out byte second)
                        || !reader.TryGetByte(out byte third))
                    {
                        return false;
                    }
                    randomOptionFields[i] = new ItemRandomOptionField(first, second, third);
                }
                if (!reader.TryGetByte(out randomOptionSelector) || !reader.TryGetByte(out randomOptionTailMarker))
                {
                    return false;
                }
                if (randomOptionTailMarker != RandomOptionNoTail
                    && !reader.TryGetBinary(RandomOptionTailLength, out randomOptionTail))
                {
                    return false;
                }
            }

            if (!reader.TryGetByte(out byte separateUpgrade) || !reader.TryGetByte(out byte tradeRestriction))
            {
                return false;
            }

            bool dataIsRemainingTime = shape == ItemEntryShape.AvatarBag
                || (detail && slot <= ApproximateAvatarMaxSlot);
            entry = new ItemEntry(
                shape,
                slot,
                itemId,
                data,
                dataIsRemainingTime,
                ext1,
                durability,
                !detail,
                sealedByte,
                detail,
                clearAvatar,
                enchant,
                amplifyType,
                amplifyValue,
                hasAvatarBlocks,
                socketBlock,
                expansionBlock,
                hasPetTime,
                petRemainTime,
                c3Records,
                randomOptionFields,
                randomOptionSelector,
                randomOptionTailMarker,
                randomOptionTail,
                separateUpgrade,
                tradeRestriction,
                reader.Position - start);
            fault = ItemEntryFault.None;
            return true;
        }

        /// <summary>
        /// 判定增幅值之后有没有"装扮块"与"宠物 u32"。需要类型而得不到答案返回假(调用方记 <see cref="ItemEntryFault.KindUnresolved"/>)。
        /// </summary>
        private static bool DecideOptionalBlocks(
            ItemEntryShape shape,
            ushort slot,
            uint itemId,
            IItemKindResolver kindResolver,
            out bool hasAvatarBlocks,
            out bool hasPetTime)
        {
            hasAvatarBlocks = false;
            hasPetTime = false;
            switch (shape)
            {
                case ItemEntryShape.AvatarBag:
                    // 装扮背包:两个块固定存在(客户端对空间 1 无条件读),宠物 u32 不存在。
                    hasAvatarBlocks = true;
                    return true;

                case ItemEntryShape.PetBag:
                case ItemEntryShape.BodyEquip:
                {
                    // 删除标记条目保留完整主干但没有可选块(服务端空物品只写主干,客户端按物品号查表得空)。
                    if (itemId == DeleteMarker)
                    {
                        return true;
                    }
                    ResolvedItemKind kind = kindResolver == null ? ResolvedItemKind.Unknown : kindResolver.Resolve(itemId);
                    if (kind == ResolvedItemKind.Unknown)
                    {
                        return false;
                    }
                    // 宠物栏的服务端写入器只有宠物分支(0x084fe258),没有装扮块;身上装备两者都有(服务端 MakeItemPacket)。
                    hasAvatarBlocks = shape == ItemEntryShape.BodyEquip && kind == ResolvedItemKind.Avatar;
                    hasPetTime = kind == ResolvedItemKind.Pet;
                    return true;
                }

                case ItemEntryShape.EquipDetail:
                {
                    ResolvedItemKind kind = kindResolver == null ? ResolvedItemKind.Unknown : kindResolver.Resolve(itemId);
                    if (kind == ResolvedItemKind.Unknown)
                    {
                        // 缺省近似:槽 ≤ 9 视为装扮、槽 22 视为宠物。样本没触发过这个近似、未经验证(笔记 §2.2.4)。
                        hasAvatarBlocks = slot <= ApproximateAvatarMaxSlot;
                        hasPetTime = slot == ApproximatePetSlot;
                    }
                    else
                    {
                        hasAvatarBlocks = kind == ResolvedItemKind.Avatar;
                        hasPetTime = kind == ResolvedItemKind.Pet;
                    }
                    return true;
                }

                default:
                    // 背包 / 仓库 / 账号仓库:没有可选块。
                    return true;
            }
        }
    }
}
