namespace Dnf.Net.Session
{
    /// <summary>
    /// 背包的货币:服务端把货币做成背包槽 0、1、2 的三个"伪条目"发出(物品号等于槽号,金额放在 Data 位,E-410、笔记 §2.1.7),
    /// 它们不是物品,解析器把它们从条目列表里拆出来单独存放。
    /// 槽 0 = 金币(服务端 <c>CInventory</c> 偏移 +4 的数额,0x084fd8ab);槽 1 = 三项硬币类字段之和(0x084fd97e;字段名没有符号,这里只叫"硬币合计");
    /// 槽 2 = 胜点(<c>GetWinPoint</c>,0x084fda84)。金额一律按无符号原值给出,不做业务解释(上限、负数口径未验证)。
    /// 每个槽可能缺席(例如一个增量包只刷新了其中一个),缺席的槽为 null。
    /// </summary>
    public sealed class ItemCurrency
    {
        /// <summary>金币所在的背包槽。</summary>
        public const ushort GoldSlot = 0;

        /// <summary>硬币合计所在的背包槽。</summary>
        public const ushort CoinTotalSlot = 1;

        /// <summary>胜点所在的背包槽。</summary>
        public const ushort WinPointSlot = 2;

        /// <summary>三个槽都缺席的货币。</summary>
        public static readonly ItemCurrency Empty = new ItemCurrency(null, null, null);

        internal ItemCurrency(uint? gold, uint? coinTotal, uint? winPoint)
        {
            Gold = gold;
            CoinTotal = coinTotal;
            WinPoint = winPoint;
        }

        /// <summary>金币(槽 0);缺席为 null。</summary>
        public uint? Gold { get; }

        /// <summary>硬币合计(槽 1);缺席为 null。</summary>
        public uint? CoinTotal { get; }

        /// <summary>胜点(槽 2);缺席为 null。</summary>
        public uint? WinPoint { get; }

        /// <summary>三个槽都缺席。</summary>
        public bool IsEmpty => !Gold.HasValue && !CoinTotal.HasValue && !WinPoint.HasValue;

        /// <summary>背包槽号是不是货币槽(0、1、2)。</summary>
        public static bool IsCurrencySlot(ushort slot)
        {
            return slot <= WinPointSlot;
        }

        /// <summary>把较新的货币叠到本货币上:较新者有值的槽取较新者,其余保持不变(纯函数,返回新对象)。</summary>
        public ItemCurrency Overlay(ItemCurrency newer)
        {
            if (newer == null)
            {
                return this;
            }
            return new ItemCurrency(
                newer.Gold ?? Gold,
                newer.CoinTotal ?? CoinTotal,
                newer.WinPoint ?? WinPoint);
        }

        /// <summary>返回把某个货币槽设成给定金额的新货币;槽号不是货币槽时原样返回本对象。</summary>
        internal ItemCurrency WithSlot(ushort slot, uint amount)
        {
            switch (slot)
            {
                case GoldSlot:
                    return new ItemCurrency(amount, CoinTotal, WinPoint);
                case CoinTotalSlot:
                    return new ItemCurrency(Gold, amount, WinPoint);
                case WinPointSlot:
                    return new ItemCurrency(Gold, CoinTotal, amount);
                default:
                    return this;
            }
        }
    }
}
