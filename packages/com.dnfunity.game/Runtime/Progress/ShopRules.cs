using System;
using System.Collections.Generic;

// 证据:E-119(买入:Store::user_buy_item 0x08618a44 的检查与次序——找不到物品 0x11、钱不够 10、称号类装备 0x17、放不进背包 4,放进去之后才扣钱;
// 收购:Store::GetSellItemPrice 0x08619008 与 0x08618ea0——[finish point price] 不为 0 或 [trade delete] 拒收 0x17、装备部位小于 10(时装)或等于 11(称号)拒收 0x17、
// 可叠加物品的件数要在 1 到所持件数之间否则 0x11)。
namespace Dnf.Game
{
    /// <summary>一次买卖的结果(本项目自己的枚举;服务端对应的错误码见各项说明)。</summary>
    public enum ShopResult
    {
        Ok = 0,

        /// <summary>没有这件物品(编号读不到、格子是空的;服务端 0x11)。</summary>
        NoItem,

        /// <summary>这家店不卖这件(服务端 <c>checkValidShopItem</c> 不通过)。</summary>
        NotSold,

        /// <summary>件数不合法(不大于 0、超过所持件数;服务端 0x11)。</summary>
        BadCount,

        /// <summary>称号类装备不能买(服务端 0x17)。</summary>
        NotBuyable,

        /// <summary>钱不够(服务端 10)。</summary>
        NotEnoughGold,

        /// <summary>背包放不下(服务端 4)。</summary>
        BagFull,

        /// <summary>商店不收这件东西(时装、称号、<c>[trade delete]</c>、<c>[finish point price]</c> 不为 0;服务端 0x17)。</summary>
        NotSellable,
    }

    /// <summary>商店买卖的规则(纯逻辑,不动背包与钱;动手的是 <c>HeroProgress.Buy/Sell</c>)。</summary>
    public static class ShopRules
    {
        /// <summary>窗口里一次最多买几件(自己的设计;服务端对单次超过 999 件的购买会记一条跟踪日志,说明那已经不是正常买法)。</summary>
        public const int MaxBuyCount = 999;

        /// <summary>这件东西商店收不收(只看物品本身)。时装(部位小于 10)、称号(部位 11)、<c>[trade delete]</c>、<c>[finish point price]</c> 不为 0 的不收。</summary>
        public static bool IsSellable(ItemInfo item)
        {
            if (item == null)
            {
                return false;
            }
            if (item.IsEquipment && (item.Part < EquipmentParts.Weapon || item.Part == EquipmentParts.TitleName))
            {
                return false;
            }
            return !item.IsTradeDelete && item.FinishPointPrice == 0;
        }

        /// <summary>这件东西能不能从商店买(称号类装备不能)。</summary>
        public static bool IsBuyable(ItemInfo item)
        {
            return item != null && !(item.IsEquipment && item.Part == EquipmentParts.TitleName);
        }

        /// <summary>
        /// 钱和背包都允许的情况下最多能买几件:装备最多 1 件;可叠加物品按"钱 ÷ 单价",单价为 0 时只受 <see cref="MaxBuyCount"/> 限制;
        /// 不超过 <see cref="MaxBuyCount"/>。钱买不起一件、物品为空或不能买返回 0。
        /// </summary>
        public static int MaxBuyable(ItemInfo item, long gold)
        {
            if (!IsBuyable(item) || gold < 0)
            {
                return 0;
            }
            long price = item.Price;
            if (item.IsEquipment)
            {
                return price <= gold ? 1 : 0;
            }
            if (price <= 0)
            {
                return MaxBuyCount;
            }
            long n = gold / price;
            return n > MaxBuyCount ? MaxBuyCount : (int)n;
        }

        /// <summary>
        /// 买 <paramref name="count"/> 件 <paramref name="item"/> 行不行。次序(服务端 <c>user_buy_item</c> 的次序,前面加上"这家店卖不卖"与件数两项):
        /// 物品存在 → 这家店卖 → 件数 → 钱够 → 称号不能买 → 背包放得下
        /// (<paramref name="shop"/> 为 null 时跳过"这家店卖"一项;<paramref name="bag"/> 为 null 时跳过背包一项)。装备的件数按 1 算。
        /// </summary>
        public static ShopResult CheckBuy(ItemShopDef shop, ItemInfo item, int count, long gold, Inventory bag)
        {
            if (item == null)
            {
                return ShopResult.NoItem;
            }
            if (shop != null && !shop.Sells(item.Code))
            {
                return ShopResult.NotSold;
            }
            int n = item.IsEquipment ? 1 : count;
            if (n < 1)
            {
                return ShopResult.BadCount;
            }
            if (gold < ItemPricing.BuyPrice(item, n))
            {
                return ShopResult.NotEnoughGold;
            }
            if (!IsBuyable(item))
            {
                return ShopResult.NotBuyable;
            }
            if (bag != null && !bag.CanAdd(item, n))
            {
                return ShopResult.BagFull;
            }
            return ShopResult.Ok;
        }

        /// <summary>
        /// 卖 <paramref name="count"/> 件(<paramref name="owned"/> 是这一格的件数)行不行。次序:物品存在 → 收不收 → 件数在 1 到所持件数之间。
        /// 装备的件数按 1 算(一格一件)。
        /// </summary>
        public static ShopResult CheckSell(ItemInfo item, int count, int owned)
        {
            if (item == null)
            {
                return ShopResult.NoItem;
            }
            if (!IsSellable(item))
            {
                return ShopResult.NotSellable;
            }
            int n = item.IsEquipment ? 1 : count;
            if (n < 1 || n > owned)
            {
                return ShopResult.BadCount;
            }
            return ShopResult.Ok;
        }

        /// <summary>结果对应的占位英文提示(窗口底部一行)。</summary>
        public static string MessageOf(ShopResult result)
        {
            switch (result)
            {
                case ShopResult.Ok:
                    return "DONE";
                case ShopResult.NoItem:
                    return "NOTHING THERE";
                case ShopResult.NotSold:
                    return "NOT SOLD HERE";
                case ShopResult.BadCount:
                    return "BAD COUNT";
                case ShopResult.NotBuyable:
                    return "CANNOT BUY THIS";
                case ShopResult.NotEnoughGold:
                    return "NOT ENOUGH GOLD";
                case ShopResult.BagFull:
                    return "BAG IS FULL";
                case ShopResult.NotSellable:
                    return "SHOP WILL NOT TAKE THIS";
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// 商店窗口用的数据本:按编号取商店、按物品编号取物品、价格倍率表。用函数做来源,所以测试可以用合成数据,
    /// 运行时用 <see cref="FromData"/> 从 PVF 读(<see cref="ItemShopCatalog"/>、<see cref="ItemCatalog"/>、<see cref="ItemPriceRates"/>)。
    /// </summary>
    public sealed class ShopBook
    {
        private readonly Func<int, ItemShopDef> _shops;
        private readonly Func<int, ItemInfo> _items;
        private readonly Func<IReadOnlyList<int>> _indices;

        public ShopBook(Func<int, ItemShopDef> shops, Func<int, ItemInfo> items, ItemPriceRates rates = null, Func<IReadOnlyList<int>> indices = null)
        {
            _shops = shops ?? throw new ArgumentNullException(nameof(shops));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            Rates = rates ?? ItemPriceRates.Neutral;
            _indices = indices;
        }

        /// <summary>收购用的倍率表(读不到时是 <see cref="ItemPriceRates.Neutral"/>)。</summary>
        public ItemPriceRates Rates { get; }

        /// <summary>按商店编号取商店;没有返回 null。</summary>
        public ItemShopDef Shop(int index) => _shops(index);

        /// <summary>按物品编号取物品;没有返回 null。</summary>
        public ItemInfo Item(int code) => _items(code);

        /// <summary>已知的商店编号(升序);来源没给时是空表。</summary>
        public IReadOnlyList<int> ShopIndices() => _indices == null ? (IReadOnlyList<int>)new int[0] : _indices();

        /// <summary>第一家有商品的商店(探针与检查用);没有返回 null。</summary>
        public ItemShopDef FirstShopWithItems()
        {
            foreach (int index in ShopIndices())
            {
                ItemShopDef def = Shop(index);
                if (def != null && def.ItemCount > 0)
                {
                    return def;
                }
            }
            return null;
        }

        /// <summary>从游戏数据造:商店目录、物品目录(都按需读、带缓存)与倍率表(读不到用替身)。</summary>
        public static ShopBook FromData(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            var shops = new ItemShopCatalog(data);
            var items = new ItemCatalog(data);
            return new ShopBook(shops.Get, items.Get, ItemPriceRates.Read(data), shops.Indices);
        }
    }
}
