using System;
using Dnf.Data;

// 证据:E-119(服务端 getItemSellPrice 0x0898c86e;价格倍率表 itemPriceRates_ 由 InitEquipmentScript 0x08991601 从 equipment/pricetable.tbl 的 [percent] 读进去;
// 收购价的调用方 Store::GetSellItemPrice 0x08618ea0 与 0x08619008)。
namespace Dnf.Game
{
    /// <summary>
    /// 价格倍率表(<c>equipment/pricetable.tbl</c> 里 <c>[percent]</c> 后面的三个整数,单位千分之一)。服务端把它读进数组 <c>itemPriceRates_</c>;
    /// 商店收购只用到下标 0 与 2(<see cref="ItemInfo.KiriProtectable"/> 的物品用下标 2,其余用下标 0),下标 1 没有被收购用到,原样留着。
    /// 原版的数值不入库;表读不出来时 <see cref="Neutral"/> 让收购价就等于物品的 <c>[value]</c>。
    /// </summary>
    public sealed class ItemPriceRates
    {
        public const string Path = "equipment/pricetable.tbl";

        /// <summary>表里一共几个数。</summary>
        public const int Count = 3;

        /// <summary>表读不出来时的替身:三档都是 1000(收购价 = <c>[value]</c>)。只在没有原版数据的环境里用到。</summary>
        public static readonly ItemPriceRates Neutral = new ItemPriceRates(1000, 1000, 1000);

        public ItemPriceRates(int normal, int middle, int kiriProtectable)
        {
            Normal = normal;
            Middle = middle;
            KiriProtectable = kiriProtectable;
        }

        /// <summary>下标 0:一般物品的收购倍率(千分比)。</summary>
        public int Normal { get; }

        /// <summary>下标 1:收购用不到,原样留着。</summary>
        public int Middle { get; }

        /// <summary>下标 2:带 <c>[possible kiri protect]</c> 的装备的收购倍率(千分比)。</summary>
        public int KiriProtectable { get; }

        /// <summary>某件物品该用哪一档(千分比)。</summary>
        public int RateFor(ItemInfo item)
        {
            return item != null && item.KiriProtectable ? KiriProtectable : Normal;
        }

        /// <summary>
        /// 从倍率表脚本里取 <c>[percent]</c> 后的前三个整数(服务端按这个顺序存进数组);标签缺失、不足三个数或脚本为空时返回 null。
        /// </summary>
        public static ItemPriceRates FromScript(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return null;
            }
            foreach (ScriptLine line in lines)
            {
                if (!line.TagIs("[percent]"))
                {
                    continue;
                }
                System.Collections.Generic.List<int> ints = line.Ints();
                return ints.Count >= Count ? new ItemPriceRates(ints[0], ints[1], ints[2]) : null;
            }
            return null;
        }

        /// <summary>读倍率表;读不到返回 null(调用方可以退回 <see cref="Neutral"/>)。</summary>
        public static ItemPriceRates Read(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return FromScript(data.ReadScript(Path));
        }
    }

    /// <summary>
    /// 商店的价格公式。买入:单价是物品脚本的 <c>[price]</c>,可叠加物品乘件数,装备恒为一件。
    /// 收购:服务端 <c>getItemSellPrice(value, 当前耐久, 最大耐久, 倍率档)</c> ——
    /// 先 <c>倍率 × value / 1000</c>(整数相除、向零取整);最大耐久不为 0 时再乘当前耐久(不超过最大耐久)除以最大耐久。
    /// 可叠加物品的耐久都按 0 算,总价是单价乘件数。耐久目前没有做,装备按满耐久收购(见 <see cref="UnitSellPrice"/> 的两个耐久参数默认 0)。
    /// </summary>
    public static class ItemPricing
    {
        /// <summary>
        /// 收购单价(服务端 <c>getItemSellPrice</c> 的整数运算,32 位回绕与服务端一致)。
        /// <paramref name="maxDurability"/> 为 0 时耐久不参与;不为 0 时当前耐久先夹到不超过最大耐久。
        /// </summary>
        public static int UnitSellPrice(int value, int ratePerMille, int durability = 0, int maxDurability = 0)
        {
            int price = unchecked(ratePerMille * value) / 1000;
            if (maxDurability == 0)
            {
                return price;
            }
            int current = durability < maxDurability ? durability : maxDurability;
            return unchecked(price * current) / maxDurability;
        }

        /// <summary>
        /// 收购总价:装备按单价(一件),可叠加物品按单价乘 <paramref name="count"/>(服务端的总价是 32 位乘法,这里用 64 位,不会回绕)。
        /// <paramref name="rates"/> 为 null 时用 <see cref="ItemPriceRates.Neutral"/>。物品为空或件数不大于 0 返回 0。
        /// </summary>
        public static long SellPrice(ItemInfo item, int count, ItemPriceRates rates)
        {
            if (item == null || count <= 0)
            {
                return 0;
            }
            ItemPriceRates r = rates ?? ItemPriceRates.Neutral;
            int unit = UnitSellPrice(item.Value, r.RateFor(item));
            return item.IsEquipment ? unit : (long)unit * count;
        }

        /// <summary>买入总价:单价乘件数(装备恒为一件);物品为空或件数不大于 0 返回 0。</summary>
        public static long BuyPrice(ItemInfo item, int count)
        {
            if (item == null || count <= 0)
            {
                return 0;
            }
            return item.IsEquipment ? item.Price : (long)item.Price * count;
        }
    }
}
