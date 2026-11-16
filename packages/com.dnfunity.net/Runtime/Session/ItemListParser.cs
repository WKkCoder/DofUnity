using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品空间包(类 0、包号 13)解析器。纯函数:字节进、不可变通知出,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// 布局(小端;客户端处理器 0x00752b70,服务端 <c>send_itemspace</c> 0x0865db6c 分派到各写入函数,笔记 §2.1):
    /// <c>u8 空间号</c>,随后按空间分四种头:
    /// 空间 0(背包)与 2(仓库)是 <c>u16 容量 + u16 个数</c>;空间 1(装扮背包)与 7(宠物栏)是 <c>u16 个数</c>;
    /// 空间 0xC(账号仓库)是 <c>u16 + u32 + u16 个数</c>;其它空间号整包拒绝(<see cref="ItemParseStatus.UnsupportedSpace"/>)。
    /// 头之后是个数个条目,条目形状见 <see cref="ItemEntryShape"/>。
    /// </para>
    /// <para>
    /// 背包(空间 0)的前三个条目是货币伪条目(槽 0、1、2,物品号等于槽号,金额在 Data 位),拆进 <see cref="ItemListNotice.Currency"/>,
    /// 不进条目列表;物品号不等于槽号的"货币槽条目"超出了这个模型,整包拒绝而不是猜。
    /// </para>
    /// <para>
    /// 尾部容忍:最后一个条目之后的字节(直连帧的 4 字节包尾校验、补零)只计入 <see cref="ItemListNotice.TrailingByteCount"/>,
    /// 绝不当条目解析;"读完恰好 0 字节"不是成功判据。
    /// </para>
    /// </summary>
    public static class ItemListParser
    {
        /// <summary>物品空间包的包号(类 0)。</summary>
        public const int PacketId = 13;

        /// <summary>解析;不需要物品类型的空间(0、1、2、0xC)与条数为 0 的宠物栏不需要回调。</summary>
        public static bool TryParse(byte[] payload, out ItemListNotice notice)
        {
            return TryParse(payload, null, out notice, out _);
        }

        /// <summary>解析并给出结论,无物品类型回调。</summary>
        public static bool TryParse(byte[] payload, out ItemListNotice notice, out ItemParseStatus status)
        {
            return TryParse(payload, null, out notice, out status);
        }

        /// <summary>
        /// 解析并给出结论。<paramref name="kindResolver"/> 只对宠物栏(空间 7)的条目有用:条目是宠物类时多一个 u32,
        /// 线上不带类型,没有回调(或回调回答 Unknown)就整包拒绝(<see cref="ItemParseStatus.KindUnresolved"/>)。
        /// </summary>
        public static bool TryParse(byte[] payload, IItemKindResolver kindResolver, out ItemListNotice notice, out ItemParseStatus status)
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

            // 头:容量(背包、仓库)、账号仓库的两个额外字段,然后是个数。
            bool hasCapacity = space == ItemSpace.Bag || space == ItemSpace.Cargo;
            ushort capacity = 0;
            if (hasCapacity && !reader.TryGetUShort(out capacity))
            {
                return false;
            }
            bool hasAccountHeader = space == ItemSpace.AccountCargo;
            ushort accountWord = 0;
            uint accountDword = 0;
            if (hasAccountHeader && (!reader.TryGetUShort(out accountWord) || !reader.TryGetUInt(out accountDword)))
            {
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

            List<ItemEntry> entries = new List<ItemEntry>(count);
            ItemCurrency currency = space == ItemSpace.Bag ? ItemCurrency.Empty : null;
            for (int i = 0; i < count; i++)
            {
                if (!ItemEntryReader.TryRead(reader, shape, kindResolver, out ItemEntry entry, out ItemEntryFault fault))
                {
                    status = fault == ItemEntryFault.KindUnresolved ? ItemParseStatus.KindUnresolved : ItemParseStatus.Malformed;
                    return false;
                }

                if (space == ItemSpace.Bag && ItemCurrency.IsCurrencySlot(entry.Slot))
                {
                    // 货币伪条目:物品号等于槽号才是模型里的形态;否则这个包不是我们认识的那种背包包,拒绝而不猜。
                    if (entry.ItemId != entry.Slot)
                    {
                        return false;
                    }
                    currency = currency.WithSlot(entry.Slot, entry.Data);
                    continue;
                }
                entries.Add(entry);
            }

            notice = new ItemListNotice(
                space,
                hasCapacity,
                capacity,
                hasAccountHeader,
                accountWord,
                accountDword,
                count,
                entries.ToArray(),
                currency,
                reader.Position,
                reader.Remaining);
            status = ItemParseStatus.Ok;
            return true;
        }
    }
}
