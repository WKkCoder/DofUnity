using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品增量包(类 0、包号 14)解析器。纯函数:字节进、不可变通知出,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// 布局(小端;客户端处理器 0x00753720,服务端写入器 <c>CInventory::MakeItemPacket</c> 0x084fc6bc 一族,笔记 §2.3):
    /// <c>u8 空间号 + u16 个数 + 个数个条目</c>。条目与物品空间包同源(<see cref="ItemEntryReader"/>),形状按空间:
    /// 空间 0、2、0xC 是背包形;空间 1 是装扮背包形;空间 7 是宠物栏形(需要类型回调);空间 3(身上装备)是身上装备形(需要类型回调)。
    /// 其它空间号整包拒绝(<see cref="ItemParseStatus.UnsupportedSpace"/>)。
    /// </para>
    /// <para>
    /// 物品号 <see cref="ItemEntryReader.DeleteMarker"/> 的条目是"清空该槽":主干完整但没有可选块(也不需要类型回调),
    /// 输出成删除操作。空间 0 的槽 0、1、2 是货币伪条目:金额进 <see cref="ItemUpdateNotice.Currency"/>,不当背包格;
    /// 货币槽的物品号不等于槽号(包括删除标记)超出了模型,整包拒绝。
    /// </para>
    /// <para>尾部容忍同物品空间包:最后一个条目之后的字节只计数,不当条目解析。</para>
    /// </summary>
    public static class ItemUpdateParser
    {
        /// <summary>物品增量包的包号(类 0)。</summary>
        public const int PacketId = 14;

        /// <summary>解析,无物品类型回调(空间 3、7 里的非删除条目会因此被拒绝)。</summary>
        public static bool TryParse(byte[] payload, out ItemUpdateNotice notice)
        {
            return TryParse(payload, null, out notice, out _);
        }

        /// <summary>解析并给出结论,无物品类型回调。</summary>
        public static bool TryParse(byte[] payload, out ItemUpdateNotice notice, out ItemParseStatus status)
        {
            return TryParse(payload, null, out notice, out status);
        }

        /// <summary>
        /// 解析并给出结论。空间 3(身上装备)与 7(宠物栏)的非删除条目需要物品类型,没有回调(或回调回答 Unknown)就整包拒绝
        /// (<see cref="ItemParseStatus.KindUnresolved"/>);条数为 0 的包、只含删除标记的包不需要回调。
        /// </summary>
        public static bool TryParse(byte[] payload, IItemKindResolver kindResolver, out ItemUpdateNotice notice, out ItemParseStatus status)
        {
            notice = null;
            status = ItemParseStatus.Malformed;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte spaceByte))
            {
                return false;
            }

            ItemSpace space;
            ItemEntryShape shape;
            switch (spaceByte)
            {
                case (byte)ItemSpace.Bag:
                    space = ItemSpace.Bag;
                    shape = ItemEntryShape.Bag;
                    break;
                case (byte)ItemSpace.AvatarBag:
                    space = ItemSpace.AvatarBag;
                    shape = ItemEntryShape.AvatarBag;
                    break;
                case (byte)ItemSpace.Cargo:
                    space = ItemSpace.Cargo;
                    shape = ItemEntryShape.Bag;
                    break;
                case (byte)ItemSpace.BodyEquip:
                    space = ItemSpace.BodyEquip;
                    shape = ItemEntryShape.BodyEquip;
                    break;
                case (byte)ItemSpace.PetBag:
                    space = ItemSpace.PetBag;
                    shape = ItemEntryShape.PetBag;
                    break;
                case (byte)ItemSpace.AccountCargo:
                    space = ItemSpace.AccountCargo;
                    shape = ItemEntryShape.Bag;
                    break;
                default:
                    status = ItemParseStatus.UnsupportedSpace;
                    return false;
            }

            if (!reader.TryGetUShort(out ushort count))
            {
                return false;
            }

            // 防谎报:个数 × 该形状的最短条目长度超过剩余字节,就不去分配。
            if ((long)count * ItemEntryReader.MinLength(shape) > reader.Remaining)
            {
                return false;
            }

            List<ItemUpdateOperation> operations = new List<ItemUpdateOperation>(count);
            ItemCurrency currency = ItemCurrency.Empty;
            for (int i = 0; i < count; i++)
            {
                if (!ItemEntryReader.TryRead(reader, shape, kindResolver, out ItemEntry entry, out ItemEntryFault fault))
                {
                    status = fault == ItemEntryFault.KindUnresolved ? ItemParseStatus.KindUnresolved : ItemParseStatus.Malformed;
                    return false;
                }

                if (space == ItemSpace.Bag && ItemCurrency.IsCurrencySlot(entry.Slot))
                {
                    if (entry.ItemId != entry.Slot)
                    {
                        return false;
                    }
                    currency = currency.WithSlot(entry.Slot, entry.Data);
                    continue;
                }

                operations.Add(entry.ItemId == ItemEntryReader.DeleteMarker
                    ? new ItemUpdateOperation(entry.Slot, null)
                    : new ItemUpdateOperation(entry.Slot, entry));
            }

            notice = new ItemUpdateNotice(
                space,
                count,
                operations.ToArray(),
                currency,
                reader.Position,
                reader.Remaining);
            status = ItemParseStatus.Ok;
            return true;
        }
    }
}
