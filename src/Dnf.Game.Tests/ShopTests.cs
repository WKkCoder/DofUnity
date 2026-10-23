using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// NPC 商店的数据与规则(证据 E-119,服务端静态读码):物品脚本的出售字段、价格倍率表与收购价公式、.shp 的页与格子、NPC 的 [role]、买卖的检查次序。
/// 数值与名字全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class ShopTests
{
    // ------------------------------------------------------------------ 工具

    private static ScriptLine[] Lines(TokenStreamBuilder b)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ScriptDocument.Parse(b.Bytes(), strings);
    }

    private static ItemInfo ParseItem(TokenStreamBuilder b, ItemKind kind = ItemKind.Equipment)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, kind, 7, "equipment/x.equ");
    }

    private static ItemInfo Equip(int code, int part = EquipmentParts.Coat, int price = 100, int value = 40)
    {
        return new ItemInfo(code, "equipment/e" + code + ".equ", ItemKind.Equipment) { Part = part, Price = price, Value = value };
    }

    private static ItemInfo Stack(int code, int price = 10, int value = 4, int limit = 100)
    {
        return new ItemInfo(code, "stackable/s" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = StackableTypes.Waste,
            Price = price,
            Value = value,
            StackLimit = limit,
        };
    }

    private static ItemShopDef Shop(params int[] firstTabCells)
    {
        return new ItemShopDef(3, "itemshop/a.shp", 5, "[etc shop]", null, new[] { new ItemShopTab(firstTabCells, null) });
    }

    // ------------------------------------------------------------------ 物品脚本的出售字段

    [Fact]
    public void ItemScriptReadsValueFinishPointPriceAndKiriFlag()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[price]").Int(900);
        b.Tag("[value]").Int(360);
        b.Tag("[finish point price]").Int(15);
        b.Tag("[possible kiri protect]");
        b.Tag("[attach type]").Str("[trade delete]");
        ItemInfo item = ParseItem(b);

        Assert.Equal(900, item.Price);
        Assert.Equal(360, item.Value);
        Assert.Equal(15, item.FinishPointPrice);
        Assert.True(item.KiriProtectable);
        Assert.True(item.IsTradeDelete);
    }

    [Fact]
    public void ItemScriptDefaultsForShopFieldsAreZeroAndFalse()
    {
        ItemInfo item = ParseItem(new TokenStreamBuilder().Tag("[price]").Int(5).Tag("[attach type]").Str("[trade]"));
        Assert.Equal(0, item.Value);
        Assert.Equal(0, item.FinishPointPrice);
        Assert.False(item.KiriProtectable);
        Assert.False(item.IsTradeDelete);
    }

    [Fact]
    public void TradeDeleteTagIsMatchedIgnoringCaseAndOnlyExactly()
    {
        Assert.True(new ItemInfo(1, "a", ItemKind.Equipment) { AttachType = "[TRADE DELETE]" }.IsTradeDelete);
        Assert.False(new ItemInfo(1, "a", ItemKind.Equipment) { AttachType = "[trade]" }.IsTradeDelete);
        Assert.False(new ItemInfo(1, "a", ItemKind.Equipment) { AttachType = null }.IsTradeDelete);
    }

    [Fact]
    public void RepeatedValueTagTakesTheLastOne()
    {
        ItemInfo item = ParseItem(new TokenStreamBuilder().Tag("[value]").Int(1).Tag("[value]").Int(2));
        Assert.Equal(2, item.Value);
    }

    // ------------------------------------------------------------------ 价格倍率表与收购价公式

    [Fact]
    public void RatesAreTheFirstThreeIntegersOfPercent()
    {
        var b = new TokenStreamBuilder().Tag("[percent]").Ints(300, 200, 50, 99);
        ItemPriceRates rates = ItemPriceRates.FromScript(Lines(b));
        Assert.NotNull(rates);
        Assert.Equal((300, 200, 50), (rates.Normal, rates.Middle, rates.KiriProtectable));
        Assert.Equal(3, ItemPriceRates.Count);
    }

    [Fact]
    public void RatesWithTooFewNumbersOrNoTagAreNull()
    {
        Assert.Null(ItemPriceRates.FromScript(Lines(new TokenStreamBuilder().Tag("[percent]").Ints(1, 2))));
        Assert.Null(ItemPriceRates.FromScript(Lines(new TokenStreamBuilder().Tag("[other]").Ints(1, 2, 3))));
        Assert.Null(ItemPriceRates.FromScript(null));
    }

    [Fact]
    public void RatesAreReadFromTheTablePath()
    {
        var b = new TokenStreamBuilder().Tag("[percent]").Ints(250, 150, 25);
        var source = new MemoryScriptSource()
            .AddBytes(GameData.StringTablePath, b.StringTableBytes())
            .AddBytes(ItemPriceRates.Path, b.Bytes());
        ItemPriceRates rates = ItemPriceRates.Read(new GameData(source));
        Assert.Equal(250, rates.Normal);
        Assert.Null(ItemPriceRates.Read(new GameData(new MemoryScriptSource())));
        Assert.Throws<ArgumentNullException>(() => ItemPriceRates.Read(null));
    }

    [Fact]
    public void RateFollowsTheKiriFlag()
    {
        var rates = new ItemPriceRates(300, 200, 50);
        Assert.Equal(300, rates.RateFor(new ItemInfo(1, "a", ItemKind.Equipment)));
        Assert.Equal(50, rates.RateFor(new ItemInfo(1, "a", ItemKind.Equipment) { KiriProtectable = true }));
        Assert.Equal(300, rates.RateFor(null));
        Assert.Equal(1000, ItemPriceRates.Neutral.Normal);
    }

    [Theory]
    [InlineData(1000, 250, 250)]          // 250 × 1000 / 1000
    [InlineData(7, 333, 2)]               // 2331 / 1000 向零取整
    [InlineData(999, 1, 0)]               // 不到 1 就是 0
    [InlineData(0, 500, 0)]
    public void UnitSellPriceIsRateTimesValueOverThousand(int value, int rate, int expected)
    {
        Assert.Equal(expected, ItemPricing.UnitSellPrice(value, rate));
    }

    [Fact]
    public void DurabilityScalesThePriceOnlyWhenMaxIsNotZero()
    {
        Assert.Equal(125, ItemPricing.UnitSellPrice(1000, 250, 5, 10));       // 一半耐久
        Assert.Equal(250, ItemPricing.UnitSellPrice(1000, 250, 20, 10));      // 当前耐久夹到最大耐久
        Assert.Equal(0, ItemPricing.UnitSellPrice(1000, 250, 0, 10));         // 耐久为 0
        Assert.Equal(250, ItemPricing.UnitSellPrice(1000, 250, 5, 0));        // 最大耐久为 0:耐久不参与
        Assert.Equal(250, ItemPricing.UnitSellPrice(1000, 250));
        Assert.Equal(83, ItemPricing.UnitSellPrice(1000, 250, 1, 3));         // 250 × 1 / 3 向零取整
    }

    [Fact]
    public void UnitSellPriceWrapsLike32BitCArithmetic()
    {
        int wrapped = unchecked(1000 * 1_073_741_823) / 1000;
        Assert.Equal(wrapped, ItemPricing.UnitSellPrice(1_073_741_823, 1000));
        Assert.True(wrapped < 1_073_741_823);   // 确实回绕过
    }

    [Fact]
    public void SellPriceMultipliesStackablesByCountAndNotEquipment()
    {
        var rates = new ItemPriceRates(500, 0, 100);
        ItemInfo stack = Stack(1, value: 40);
        Assert.Equal(20, ItemPricing.UnitSellPrice(40, 500));
        Assert.Equal(60, ItemPricing.SellPrice(stack, 3, rates));
        ItemInfo equip = Equip(2, value: 40);
        Assert.Equal(20, ItemPricing.SellPrice(equip, 1, rates));
        Assert.Equal(20, ItemPricing.SellPrice(equip, 5, rates));              // 装备一格一件,件数不乘
        equip.KiriProtectable = true;
        Assert.Equal(4, ItemPricing.SellPrice(equip, 1, rates));               // 第三档倍率
    }

    [Fact]
    public void SellPriceUsesNeutralRatesWhenMissingAndZeroForNothing()
    {
        Assert.Equal(40, ItemPricing.SellPrice(Equip(1, value: 40), 1, null));
        Assert.Equal(0, ItemPricing.SellPrice(null, 1, null));
        Assert.Equal(0, ItemPricing.SellPrice(Stack(1), 0, null));
        Assert.Equal(0, ItemPricing.SellPrice(Stack(1), -3, null));
    }

    [Fact]
    public void SellPriceDoesNotWrapForLargeStacks()
    {
        ItemInfo stack = Stack(1, value: 2_000_000_000);
        var rates = new ItemPriceRates(1, 0, 0);                        // 单价 = 2_000_000_000 × 1 / 1000
        Assert.Equal(2_000_000L * 5000, ItemPricing.SellPrice(stack, 5000, rates));        // 总价超过 32 位也不回绕
    }

    [Fact]
    public void BuyPriceIsUnitPriceTimesCountForStackablesOnly()
    {
        Assert.Equal(30, ItemPricing.BuyPrice(Stack(1, price: 10), 3));
        Assert.Equal(100, ItemPricing.BuyPrice(Equip(2, price: 100), 7));
        Assert.Equal(0, ItemPricing.BuyPrice(null, 1));
        Assert.Equal(0, ItemPricing.BuyPrice(Stack(1), 0));
        Assert.Equal(10L * 999, ItemPricing.BuyPrice(Stack(1, price: 10), 999));
    }

    // ------------------------------------------------------------------ 商店脚本

    [Fact]
    public void ShopScriptReadsHeaderTabsAndCells()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[NPC]").Int(77);
        b.Tag("[type]").Str("[weapon shop]");
        b.Tag("[message]").Keyed(5, "message_9");
        b.Tag("[tab name]").Keyed(5, "tab_a").Keyed(5, "tab_b").Tag("[/tab name]");
        b.Tag("[sell item]").Ints(10, 11, -1, -1, -2, 20, -1, 21, -2, 30).Tag("[/sell item]");
        ItemShopDef shop = ItemShopScript.Parse(Lines(b), 4, "itemshop/x.shp");

        Assert.Equal((4, "itemshop/x.shp", 77, "[weapon shop]", "message_9"), (shop.Index, shop.Path, shop.NpcId, shop.TypeTag, shop.MessageKey));
        Assert.Equal(3, shop.Tabs.Count);
        Assert.Equal(new[] { 10, 11, -1, -1 }, shop.Tabs[0].Cells.ToArray());
        Assert.Equal(new[] { 20, -1, 21 }, shop.Tabs[1].Cells.ToArray());
        Assert.Equal(new[] { 30 }, shop.Tabs[2].Cells.ToArray());
        Assert.Equal("tab_a", shop.Tabs[0].NameKey);
        Assert.Equal("tab_b", shop.Tabs[1].NameKey);
        Assert.Null(shop.Tabs[2].NameKey);                 // 名字比页少
        Assert.Equal(5, shop.ItemCount);                    // 10 11 20 21 30
        Assert.Equal(2, shop.Tabs[0].ItemCount);
    }

    [Fact]
    public void ShopWithoutSeparatorHasOneTabAndEmptyShopHasNone()
    {
        var one = new TokenStreamBuilder().Tag("[sell item]").Ints(1, 2, 3).Tag("[/sell item]");
        ItemShopDef shop = ItemShopScript.Parse(Lines(one), 1, "p");
        Assert.Single(shop.Tabs);
        Assert.Equal(3, shop.ItemCount);

        var none = new TokenStreamBuilder().Tag("[NPC]").Int(2).Tag("[sell item]").Tag("[/sell item]");
        ItemShopDef empty = ItemShopScript.Parse(Lines(none), 2, "p");
        Assert.Empty(empty.Tabs);
        Assert.Equal(0, empty.ItemCount);
        Assert.Equal(-1, empty.FirstCode());
        Assert.Null(ItemShopScript.Parse(null, 1, "p"));
    }

    [Fact]
    public void ShopScriptDefaultsAndNegativeCellsAreNormalised()
    {
        var b = new TokenStreamBuilder().Tag("[sell item]").Ints(5, -7, 6).Tag("[/sell item]");
        ItemShopDef shop = ItemShopScript.Parse(Lines(b), 1, "p");
        Assert.Equal(-1, shop.NpcId);
        Assert.Null(shop.TypeTag);
        Assert.Null(shop.MessageKey);
        Assert.Equal(new[] { 5, -1, 6 }, shop.Tabs[0].Cells.ToArray());          // −2 之外的负数都当空格
    }

    [Fact]
    public void ShopSellsOnlyListedItemsAndTabGeometryFollowsSevenColumns()
    {
        var tab = new ItemShopTab(Enumerable.Range(0, 15).ToArray(), null);
        Assert.Equal(3, tab.RowCount);                                              // 15 格,每行 7 个
        Assert.Equal(7, ItemShopDef.Columns);
        Assert.Equal(-1, tab.CodeAt(-1));
        Assert.Equal(-1, tab.CodeAt(15));
        Assert.Equal(4, tab.CodeAt(4));
        Assert.Equal(0, new ItemShopTab(new int[0], null).RowCount);

        ItemShopDef shop = Shop(100, -1, 101);
        Assert.True(shop.Sells(100));
        Assert.True(shop.Sells(101));
        Assert.False(shop.Sells(102));
        Assert.False(shop.Sells(-1));                                               // 空格不是商品
        Assert.Equal(100, shop.FirstCode());
        Assert.Equal(-1, new ItemShopTab(new[] { -1, 5 }, null).CodeAt(0));
    }

    [Fact]
    public void CatalogResolvesTheListAndCachesShops()
    {
        var pool = new List<string>();
        var list = new TokenStreamBuilder(pool).Int(1).Str("a.shp").Int(4).Str("b.shp").Int(9).Str("missing.shp");
        var a = new TokenStreamBuilder(pool).Tag("[NPC]").Int(11).Tag("[sell item]").Ints(1, 2).Tag("[/sell item]");
        var b = new TokenStreamBuilder(pool).Tag("[NPC]").Int(12).Tag("[sell item]").Ints(3, -2, 4).Tag("[/sell item]");
        var source = new MemoryScriptSource()
            .AddBytes(GameData.StringTablePath, a.StringTableBytes())
            .AddBytes(ItemShopCatalog.ListPath, list.Bytes())
            .AddBytes("itemshop/a.shp", a.Bytes())
            .AddBytes("itemshop/b.shp", b.Bytes());
        var catalog = new ItemShopCatalog(new GameData(source));

        Assert.Equal(3, catalog.ListedCount);
        Assert.Equal(new[] { 1, 4, 9 }, catalog.Indices().ToArray());
        ItemShopDef one = catalog.Get(1);
        Assert.Equal((1, "itemshop/a.shp", 11), (one.Index, one.Path, one.NpcId));
        Assert.Equal(2, catalog.Get(4).Tabs.Count);
        Assert.Same(one, catalog.Get(1));
        Assert.Null(catalog.Get(9));              // 脚本缺失
        Assert.Null(catalog.Get(2));              // 列表里没有
        Assert.Throws<ArgumentNullException>(() => new ItemShopCatalog(null));
    }

    [Fact]
    public void CatalogWithoutListIsEmpty()
    {
        var catalog = new ItemShopCatalog(new GameData(new MemoryScriptSource()));
        Assert.Equal(0, catalog.ListedCount);
        Assert.Empty(catalog.Indices());
        Assert.Null(catalog.Get(1));
    }

    // ------------------------------------------------------------------ NPC 的角色

    [Fact]
    public void RolesAreStringIntegerPairs()
    {
        var b = new TokenStreamBuilder().Tag("[role]").Str("[item shop]").Int(15).Str("[move town]").Int(2).Tag("[/role]");
        List<NpcRole> roles = NpcRoles.FromScript(Lines(b));
        Assert.Equal(2, roles.Count);
        Assert.Equal(("[item shop]", 15), (roles[0].Name, roles[0].Argument));
        Assert.Equal(("[move town]", 2), (roles[1].Name, roles[1].Argument));
        Assert.Equal(15, NpcRoles.ItemShopIndex(Lines(b)));
    }

    [Fact]
    public void RoleWithoutArgumentGetsZeroAndMissingRoleIsMinusOne()
    {
        var b = new TokenStreamBuilder().Tag("[role]").Str("[plain]").Str("[item shop]").Int(3).Str("[tail]").Tag("[/role]");
        List<NpcRole> roles = NpcRoles.FromScript(Lines(b));
        Assert.Equal(3, roles.Count);
        Assert.Equal(("[plain]", 0), (roles[0].Name, roles[0].Argument));
        Assert.Equal(("[tail]", 0), (roles[2].Name, roles[2].Argument));
        Assert.Equal(3, NpcRoles.ItemShopIndex(Lines(b)));

        var none = new TokenStreamBuilder().Tag("[role]").Tag("[/role]");
        Assert.Empty(NpcRoles.FromScript(Lines(none)));
        Assert.Equal(-1, NpcRoles.ItemShopIndex(Lines(none)));
        Assert.Equal(-1, NpcRoles.ItemShopIndex(null));
        Assert.Empty(NpcRoles.FromScript(null));
    }

    [Fact]
    public void RoleNameMatchIgnoresCase()
    {
        var b = new TokenStreamBuilder().Tag("[role]").Str("[ITEM SHOP]").Int(8).Tag("[/role]");
        Assert.Equal(8, NpcRoles.ItemShopIndex(Lines(b)));
    }

    // ------------------------------------------------------------------ 规则

    [Fact]
    public void SellabilityFollowsTheServerChecks()
    {
        Assert.True(ShopRules.IsSellable(Equip(1, EquipmentParts.Weapon)));
        Assert.True(ShopRules.IsSellable(Equip(1, EquipmentParts.Coat)));
        Assert.True(ShopRules.IsSellable(Equip(1, EquipmentParts.Creature)));         // 宠物类部位不在拒收范围
        Assert.True(ShopRules.IsSellable(Equip(1, EquipmentParts.Unknown)));
        Assert.False(ShopRules.IsSellable(Equip(1, EquipmentParts.HatAvatar)));       // 时装
        Assert.False(ShopRules.IsSellable(Equip(1, EquipmentParts.AuroraAvatar)));
        Assert.False(ShopRules.IsSellable(Equip(1, EquipmentParts.TitleName)));       // 称号
        Assert.False(ShopRules.IsSellable(new ItemInfo(1, "a", ItemKind.Equipment) { Part = EquipmentParts.Coat, AttachType = "[trade delete]" }));
        Assert.False(ShopRules.IsSellable(new ItemInfo(1, "a", ItemKind.Stackable) { FinishPointPrice = 3 }));
        Assert.True(ShopRules.IsSellable(Stack(1)));
        Assert.False(ShopRules.IsSellable(null));
    }

    [Fact]
    public void OnlyTitlesCannotBeBought()
    {
        Assert.True(ShopRules.IsBuyable(Equip(1, EquipmentParts.Weapon)));
        Assert.True(ShopRules.IsBuyable(Equip(1, EquipmentParts.HatAvatar)));         // 时装能买(只是不能卖)
        Assert.True(ShopRules.IsBuyable(Stack(1)));
        Assert.False(ShopRules.IsBuyable(Equip(1, EquipmentParts.TitleName)));
        Assert.False(ShopRules.IsBuyable(null));
    }

    [Fact]
    public void MaxBuyableIsGoldOverPriceCappedAt999()
    {
        Assert.Equal(7, ShopRules.MaxBuyable(Stack(1, price: 10), 79));
        Assert.Equal(0, ShopRules.MaxBuyable(Stack(1, price: 10), 9));
        Assert.Equal(ShopRules.MaxBuyCount, ShopRules.MaxBuyable(Stack(1, price: 10), 1_000_000));
        Assert.Equal(ShopRules.MaxBuyCount, ShopRules.MaxBuyable(Stack(1, price: 0), 0));          // 白送的只受上限限制
        Assert.Equal(1, ShopRules.MaxBuyable(Equip(2, price: 100), 100));
        Assert.Equal(1, ShopRules.MaxBuyable(Equip(2, price: 100), 1_000_000));                     // 装备最多 1 件
        Assert.Equal(0, ShopRules.MaxBuyable(Equip(2, price: 100), 99));
        Assert.Equal(0, ShopRules.MaxBuyable(Equip(3, EquipmentParts.TitleName, price: 1), 1_000_000));
        Assert.Equal(0, ShopRules.MaxBuyable(null, 100));
        Assert.Equal(0, ShopRules.MaxBuyable(Stack(1), -5));
    }

    [Fact]
    public void BuyChecksRunInTheServerOrder()
    {
        ItemShopDef shop = Shop(1, 2, 3);
        var bag = new Inventory(1);
        ItemInfo stack = Stack(1, price: 10);

        Assert.Equal(ShopResult.NoItem, ShopRules.CheckBuy(shop, null, 1, 1000, bag));
        Assert.Equal(ShopResult.NotSold, ShopRules.CheckBuy(shop, Stack(99), 1, 1000, bag));
        Assert.Equal(ShopResult.BadCount, ShopRules.CheckBuy(shop, stack, 0, 1000, bag));
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuy(Shop(5), Equip(5, EquipmentParts.TitleName, price: 5000), 1, 1000, bag));     // 钱的检查在称号之前
        Assert.Equal(ShopResult.NotBuyable, ShopRules.CheckBuy(Shop(5), Equip(5, EquipmentParts.TitleName, price: 1), 1, 1000, bag));
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuy(shop, stack, 5, 49, bag));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuy(shop, stack, 5, 50, bag));
        // 钱够了但背包放不下
        var full = new Inventory(1);
        full.Add(Stack(2));
        Assert.Equal(ShopResult.BagFull, ShopRules.CheckBuy(shop, stack, 1, 1000, full));
        // 先查"这家店卖不卖"再查钱
        Assert.Equal(ShopResult.NotSold, ShopRules.CheckBuy(shop, Stack(99, price: 10_000), 1, 0, bag));
        // 钱不够排在背包满之前
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuy(shop, stack, 1, 0, full));
    }

    [Fact]
    public void BuyingEquipmentIgnoresTheRequestedCountAndNullShopSkipsTheShopCheck()
    {
        ItemInfo equip = Equip(5, price: 100);
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuy(null, equip, 0, 100, null));            // 件数按 1
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuy(null, equip, 9, 99, null));  // 价钱不乘件数,但 99 不够 100
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuy(null, equip, 9, 100, null));
    }

    [Fact]
    public void SellChecksRunInTheServerOrder()
    {
        Assert.Equal(ShopResult.NoItem, ShopRules.CheckSell(null, 1, 1));
        Assert.Equal(ShopResult.NotSellable, ShopRules.CheckSell(Equip(1, EquipmentParts.TitleName), 1, 1));
        Assert.Equal(ShopResult.BadCount, ShopRules.CheckSell(Stack(1), 0, 5));
        Assert.Equal(ShopResult.BadCount, ShopRules.CheckSell(Stack(1), 6, 5));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckSell(Stack(1), 5, 5));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckSell(Equip(2), 9, 1));                 // 装备件数按 1
        Assert.Equal(ShopResult.NotSellable, ShopRules.CheckSell(WithAttach(Stack(1), "[trade delete]"), 1, 1));
    }

    private static ItemInfo WithAttach(ItemInfo item, string attach)
    {
        item.AttachType = attach;
        return item;
    }

    [Fact]
    public void EveryResultHasAMessageExceptUnknownOnes()
    {
        foreach (ShopResult r in Enum.GetValues<ShopResult>())
        {
            Assert.False(string.IsNullOrEmpty(ShopRules.MessageOf(r)), r.ToString());
        }
        Assert.Equal(string.Empty, ShopRules.MessageOf((ShopResult)99));
    }

    // ------------------------------------------------------------------ 背包拿走几件

    [Fact]
    public void InventoryRemoveAtTakesPartOfAStackAndDropsEmptySlots()
    {
        var bag = new Inventory();
        bag.Add(Stack(1), 30);
        bag.Add(Stack(2), 5);
        Assert.True(bag.RemoveAt(InventoryTab.Consumable, 0, 12));
        Assert.Equal(18, bag.Tab(InventoryTab.Consumable)[0].Count);
        Assert.False(bag.RemoveAt(InventoryTab.Consumable, 0, 19));            // 超过这一格:不动
        Assert.Equal(18, bag.Tab(InventoryTab.Consumable)[0].Count);
        Assert.False(bag.RemoveAt(InventoryTab.Consumable, 0, 0));
        Assert.False(bag.RemoveAt(InventoryTab.Consumable, 5, 1));             // 没有这一格
        Assert.False(bag.RemoveAt(InventoryTab.Consumable, -1, 1));
        Assert.True(bag.RemoveAt(InventoryTab.Consumable, 0, 18));             // 拿空整格移除,后面的补上来
        Assert.Single(bag.Tab(InventoryTab.Consumable));
        Assert.Equal(2, bag.Tab(InventoryTab.Consumable)[0].Info.Code);
    }

    // ------------------------------------------------------------------ 数据本

    [Fact]
    public void ShopBookFromDataReadsShopsItemsAndRates()
    {
        var pool = new List<string>();
        var shopList = new TokenStreamBuilder(pool).Int(2).Str("s.shp");
        var shpS = new TokenStreamBuilder(pool).Tag("[NPC]").Int(1).Tag("[sell item]").Ints(10, 500).Tag("[/sell item]");
        var equipList = new TokenStreamBuilder(pool).Int(10).Str("w/a.equ");
        var stackList = new TokenStreamBuilder(pool).Int(500).Str("r/c.stk");
        var a = new TokenStreamBuilder(pool).Tag("[price]").Int(300).Tag("[value]").Int(100).Tag("[equipment type]").Str("[weapon]").Int(1);
        var c = new TokenStreamBuilder(pool).Tag("[price]").Int(20).Tag("[value]").Int(8).Tag("[stackable type]").Str("[waste]").Int(1);
        var table = new TokenStreamBuilder(pool).Tag("[percent]").Ints(500, 100, 50);
        var source = new MemoryScriptSource()
            .AddBytes(GameData.StringTablePath, a.StringTableBytes())
            .AddBytes(ItemShopCatalog.ListPath, shopList.Bytes())
            .AddBytes("itemshop/s.shp", shpS.Bytes())
            .AddBytes(ItemCatalog.EquipmentListPath, equipList.Bytes())
            .AddBytes(ItemCatalog.StackableListPath, stackList.Bytes())
            .AddBytes("equipment/w/a.equ", a.Bytes())
            .AddBytes("stackable/r/c.stk", c.Bytes())
            .AddBytes(ItemPriceRates.Path, table.Bytes());
        ShopBook book = ShopBook.FromData(new GameData(source));

        Assert.Equal(new[] { 2 }, book.ShopIndices().ToArray());
        ItemShopDef shop = book.Shop(2);
        Assert.True(shop.Sells(10));
        Assert.Equal(300, book.Item(10).Price);
        Assert.Equal(500, book.Rates.Normal);
        Assert.Same(shop, book.FirstShopWithItems());
        Assert.Null(book.Shop(3));
        Assert.Null(book.Item(99));
        Assert.Equal(50, ItemPricing.SellPrice(book.Item(10), 1, book.Rates));
        Assert.Equal(12, ItemPricing.SellPrice(book.Item(500), 3, book.Rates));
        Assert.Throws<ArgumentNullException>(() => ShopBook.FromData(null));
    }

    [Fact]
    public void ShopBookWithoutDataFallsBackToNeutralRates()
    {
        ShopBook book = ShopBook.FromData(new GameData(new MemoryScriptSource()));
        Assert.Same(ItemPriceRates.Neutral, book.Rates);
        Assert.Empty(book.ShopIndices());
        Assert.Null(book.FirstShopWithItems());
    }

    [Fact]
    public void ShopBookFromDelegatesWorksWithoutIndices()
    {
        var shop = Shop(1);
        var book = new ShopBook(i => i == 3 ? shop : null, c => Stack(c), null);
        Assert.Same(shop, book.Shop(3));
        Assert.Equal(9, book.Item(9).Code);
        Assert.Empty(book.ShopIndices());
        Assert.Null(book.FirstShopWithItems());
        Assert.Throws<ArgumentNullException>(() => new ShopBook(null, c => null));
        Assert.Throws<ArgumentNullException>(() => new ShopBook(i => null, null));
    }
}
