using Dnf.Data;
using Xunit;

namespace Dnf.Game.Tests;

/// <summary>
/// r19-shopprice 商店价格来源单元测试(全合成编号 900+ 与手算期望值):
/// 覆盖 <c>[value]</c> 省略/-1 回落到 <c>[price]</c>、<c>[need material]</c> 解析与边界、
/// <see cref="ItemPricing"/> 买/卖/分类计算，以及 <see cref="ShopRules"/> 材料兑换与可选零价无材料开关。
/// </summary>
public class ShopPriceSourceTests
{
    private static ItemInfo ParseItem(ItemKind kind, TokenStreamBuilder body, int code = 901)
    {
        (ScriptToken[] _, PvfStringTable strings) = body.Build();
        return ItemScript.Parse(body.Bytes(), strings, kind, code, "synthetic/" + code + (kind == ItemKind.Equipment ? ".equ" : ".stk"));
    }

    private static ItemInfo MakeEquip(int code, int part = EquipmentParts.Weapon, int price = 0, int value = 0, int matId = -1, int matCount = 0)
    {
        return new ItemInfo(code, "synthetic/e" + code + ".equ", ItemKind.Equipment)
        {
            Part = part,
            Price = price,
            Value = value,
            NeedMaterialId = matId,
            NeedMaterialCount = matCount,
        };
    }

    private static ItemInfo MakeStack(int code, int stackableType = StackableTypes.Material, int price = 0, int value = 0, int matId = -1, int matCount = 0, int limit = 100)
    {
        return new ItemInfo(code, "synthetic/s" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = stackableType,
            Price = price,
            Value = value,
            NeedMaterialId = matId,
            NeedMaterialCount = matCount,
            StackLimit = limit,
        };
    }

    private static ItemShopDef MakeShop(params int[] soldCodes)
    {
        return new ItemShopDef(950, "synthetic/shop.shp", 960, "[etc shop]", null, new[] { new ItemShopTab(soldCodes, null) });
    }

    private static Inventory MakeBagWith(ItemInfo item, int count)
    {
        var bag = new Inventory(8);
        if (count > 0)
        {
            bag.Add(item, count);
        }
        return bag;
    }

    // ------------------------------------------------------------------ ItemInfo / ItemScript.Parse

    [Fact]
    public void ItemScriptDefaultsForShopFieldsAndNeedMaterialBoundaries()
    {
        var empty = new ItemInfo(900, "synthetic/900.stk", ItemKind.Stackable);
        Assert.Equal(-1, empty.NeedMaterialId);
        Assert.Equal(0, empty.NeedMaterialCount);
        Assert.False(empty.HasNeedMaterial);

        Assert.True(MakeStack(901, matId: 0, matCount: 1).HasNeedMaterial);
        Assert.False(MakeStack(902, matId: -1, matCount: 3).HasNeedMaterial);
        Assert.False(MakeStack(903, matId: 904, matCount: 0).HasNeedMaterial);
        Assert.False(MakeStack(905, matId: 906, matCount: -2).HasNeedMaterial);
    }

    [Theory]
    [InlineData(ItemKind.Equipment)]
    [InlineData(ItemKind.Stackable)]
    public void OmittedOrMinusOneValueFallsBackToPriceForBothKinds(ItemKind kind)
    {
        ItemInfo omitted = ParseItem(kind, new TokenStreamBuilder().Tag("[price]").Int(375));
        Assert.Equal(375, omitted.Price);
        Assert.Equal(375, omitted.Value);

        ItemInfo minusOneBefore = ParseItem(kind, new TokenStreamBuilder().Tag("[value]").Int(-1).Tag("[price]").Int(615));
        Assert.Equal(615, minusOneBefore.Price);
        Assert.Equal(615, minusOneBefore.Value);

        ItemInfo lastMinusOne = ParseItem(kind, new TokenStreamBuilder().Tag("[price]").Int(410).Tag("[value]").Int(95).Tag("[grade]").Int(2).Tag("[value]").Int(-1));
        Assert.Equal(410, lastMinusOne.Value);
    }

    [Fact]
    public void ExplicitZeroOrPositiveValueIsPreservedOverPrice()
    {
        ItemInfo zeroVal = ParseItem(ItemKind.Equipment, new TokenStreamBuilder().Tag("[price]").Int(480).Tag("[value]").Int(0));
        Assert.Equal(480, zeroVal.Price);
        Assert.Equal(0, zeroVal.Value);

        ItemInfo posVal = ParseItem(ItemKind.Stackable, new TokenStreamBuilder().Tag("[price]").Int(250).Tag("[value]").Int(97));
        Assert.Equal(250, posVal.Price);
        Assert.Equal(97, posVal.Value);

        ItemInfo secondPosVal = ParseItem(ItemKind.Stackable, new TokenStreamBuilder().Tag("[price]").Int(100).Tag("[value]").Int(42).Tag("[grade]").Int(2).Tag("[value]").Int(88));
        Assert.Equal(88, secondPosVal.Value);
    }

    [Fact]
    public void NeedMaterialParsesPairIgnoresNoiseAndResetsOnIncompleteOverride()
    {
        ItemInfo normal = ParseItem(ItemKind.Equipment, new TokenStreamBuilder().Tag("[NEED MATERIAL]").Str("noise").Ints(911, 7, 55));
        Assert.Equal(911, normal.NeedMaterialId);
        Assert.Equal(7, normal.NeedMaterialCount);
        Assert.True(normal.HasNeedMaterial);

        ItemInfo overwrittenIncomplete = ParseItem(ItemKind.Stackable, new TokenStreamBuilder()
            .Tag("[need material]").Ints(912, 11)
            .Tag("[price]").Int(25)
            .Tag("[need material]").Int(913));
        Assert.Equal(913, overwrittenIncomplete.NeedMaterialId);
        Assert.Equal(0, overwrittenIncomplete.NeedMaterialCount);
        Assert.False(overwrittenIncomplete.HasNeedMaterial);
    }

    // ------------------------------------------------------------------ ItemPricing

    [Fact]
    public void BuyMaterialCountScalesStackableAndClampsInvalid()
    {
        ItemInfo equip = MakeEquip(920, price: 30, matId: 910, matCount: 13);
        ItemInfo stack = MakeStack(921, price: 0, matId: 910, matCount: 70000000);
        ItemInfo noMat = MakeStack(922, price: 50, matId: -1, matCount: 0);

        Assert.Equal(13, ItemPricing.BuyMaterialCount(equip, 1));
        Assert.Equal(13, ItemPricing.BuyMaterialCount(equip, 4));
        Assert.Equal(0, ItemPricing.BuyMaterialCount(equip, 0));
        Assert.Equal(0, ItemPricing.BuyMaterialCount(equip, -2));

        Assert.Equal(70000000L, ItemPricing.BuyMaterialCount(stack, 1));
        Assert.Equal(3500000000L, ItemPricing.BuyMaterialCount(stack, 50));
        Assert.Equal(0, ItemPricing.BuyMaterialCount(noMat, 3));
        Assert.Equal(0, ItemPricing.BuyMaterialCount(null, 3));
    }

    [Fact]
    public void ClassifyBuyPriceDistinguishesAllFourCategories()
    {
        Assert.Equal(0, (int)ShopPriceKind.FreeOrUnknown);
        Assert.Equal(1, (int)ShopPriceKind.Gold);
        Assert.Equal(2, (int)ShopPriceKind.Material);
        Assert.Equal(3, (int)ShopPriceKind.GoldAndMaterial);

        Assert.Equal(ShopPriceKind.FreeOrUnknown, ItemPricing.ClassifyBuyPrice(null));
        Assert.Equal(ShopPriceKind.FreeOrUnknown, ItemPricing.ClassifyBuyPrice(MakeStack(923, price: 0, matId: -1, matCount: 0)));
        Assert.Equal(ShopPriceKind.FreeOrUnknown, ItemPricing.ClassifyBuyPrice(MakeStack(924, price: 0, matId: 910, matCount: 0)));
        Assert.Equal(ShopPriceKind.Gold, ItemPricing.ClassifyBuyPrice(MakeEquip(925, price: 1, matId: -1, matCount: 0)));
        Assert.Equal(ShopPriceKind.Material, ItemPricing.ClassifyBuyPrice(MakeEquip(926, price: 0, matId: 910, matCount: 4)));
        Assert.Equal(ShopPriceKind.GoldAndMaterial, ItemPricing.ClassifyBuyPrice(MakeStack(927, price: 250, matId: 910, matCount: 6)));
    }

    [Fact]
    public void SellPriceAndUnitSellPriceFollowBinaryTableAndDurabilityRules()
    {
        Assert.Equal(61, ItemPricing.UnitSellPrice(123, 500));
        Assert.Equal(61, ItemPricing.UnitSellPrice(123, 500, durability: 35, maxDurability: 30));
        Assert.Equal(18, ItemPricing.UnitSellPrice(123, 500, durability: 9, maxDurability: 30));

        var rates = new ItemPriceRates(200, 100, 50);
        ItemInfo normalEquip = MakeEquip(928, value: 1000);
        ItemInfo kiriEquip = MakeEquip(929, value: 1000);
        kiriEquip.KiriProtectable = true;
        Assert.Equal(200, ItemPricing.SellPrice(normalEquip, 5, rates));
        Assert.Equal(50, ItemPricing.SellPrice(kiriEquip, 1, rates));

        ItemInfo bigStack = MakeStack(930, value: 100000);
        Assert.Equal(5000000000L, ItemPricing.SellPrice(bigStack, 50000, ItemPriceRates.Neutral));
        Assert.Equal(5000000000L, ItemPricing.SellPrice(bigStack, 50000, null));
        Assert.Equal(0, ItemPricing.SellPrice(bigStack, 10, new ItemPriceRates(0, 0, 0)));
        Assert.Equal(0, ItemPricing.SellPrice(bigStack, 0, rates));
        Assert.Equal(0, ItemPricing.SellPrice(null, 1, rates));

        Assert.Equal(0L, ItemPricing.BuyPrice(MakeStack(948, price: 0), 5));
        Assert.Equal(3L, ItemPricing.BuyPrice(MakeStack(949, price: 1), 3));
        Assert.Equal(1L, ItemPricing.BuyPrice(MakeEquip(951, price: 1), 3));
        Assert.Equal(6442450941L, ItemPricing.BuyPrice(MakeStack(952, price: int.MaxValue), 3));
    }

    // ------------------------------------------------------------------ ShopRules

    [Fact]
    public void NotEnoughMaterialHasKnownPlaceholderMessage()
    {
        Assert.Equal("CANNOT BUY THIS", ShopRules.MessageOf(ShopResult.NotEnoughMaterial));
    }

    [Fact]
    public void MaxBuyableWithMaterialsTakesBottleneckBetweenGoldMaterialsAndCap()
    {
        ItemInfo mat = MakeStack(931, limit: 5000);
        ItemInfo comboStack = MakeStack(932, price: 100, matId: 931, matCount: 3);
        ItemInfo freeMatStack = MakeStack(933, price: 0, matId: 931, matCount: 2);
        ItemInfo comboEquip = MakeEquip(934, EquipmentParts.Weapon, price: 100, matId: 931, matCount: 5);
        ItemInfo titleEquip = MakeEquip(935, EquipmentParts.TitleName, price: 10, matId: 931, matCount: 1);

        Assert.Equal(4, ShopRules.MaxBuyableWithMaterials(comboStack, 1000, MakeBagWith(mat, 14)));
        Assert.Equal(2, ShopRules.MaxBuyableWithMaterials(comboStack, 250, MakeBagWith(mat, 30)));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(comboStack, 250, null));
        Assert.Equal(ShopRules.MaxBuyCount, ShopRules.MaxBuyableWithMaterials(freeMatStack, 0, MakeBagWith(mat, 3000)));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(freeMatStack, 0, MakeBagWith(mat, 1)));

        Assert.Equal(1, ShopRules.MaxBuyableWithMaterials(comboEquip, 100, MakeBagWith(mat, 5)));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(comboEquip, 100, MakeBagWith(mat, 4)));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(MakeEquip(935, EquipmentParts.Weapon, price: 0, matId: 931, matCount: 1), 0, null));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(titleEquip, 1000, MakeBagWith(mat, 10)));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(comboStack, -1, MakeBagWith(mat, 10)));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(null, 1000, MakeBagWith(mat, 10), allowFreeWithoutMaterial: false));
    }

    [Fact]
    public void MaxBuyableWithMaterialsFreeWithoutMaterialToggleOnlyBlocksZeroGoldNoMaterial()
    {
        ItemInfo freeNoMatStack = MakeStack(936, price: 0);
        ItemInfo freeNoMatEquip = MakeEquip(937, price: 0);
        ItemInfo freeWithMatStack = MakeStack(938, price: 0, matId: 931, matCount: 2);
        ItemInfo oneGoldNoMatStack = MakeStack(939, price: 1);
        Inventory bag = MakeBagWith(MakeStack(931), 7);

        Assert.Equal(ShopRules.MaxBuyCount, ShopRules.MaxBuyableWithMaterials(freeNoMatStack, 0, bag));
        Assert.Equal(ShopRules.MaxBuyCount, ShopRules.MaxBuyableWithMaterials(freeNoMatStack, 0, bag, allowFreeWithoutMaterial: true));
        Assert.Equal(1, ShopRules.MaxBuyableWithMaterials(freeNoMatEquip, 0, bag, allowFreeWithoutMaterial: true));

        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(freeNoMatStack, 1000, bag, allowFreeWithoutMaterial: false));
        Assert.Equal(0, ShopRules.MaxBuyableWithMaterials(freeNoMatEquip, 1000, bag, allowFreeWithoutMaterial: false));
        Assert.Equal(3, ShopRules.MaxBuyableWithMaterials(freeWithMatStack, 0, bag, allowFreeWithoutMaterial: false));
        Assert.Equal(2, ShopRules.MaxBuyableWithMaterials(oneGoldNoMatStack, 2, bag, allowFreeWithoutMaterial: false));
    }

    [Fact]
    public void CheckBuyWithMaterialsEnforcesOrderMaterialsToggleAndLegacyParity()
    {
        ItemInfo mat = MakeStack(940, limit: 100);
        ItemInfo matOnlyEquip = MakeEquip(941, EquipmentParts.Coat, price: 0, matId: 940, matCount: 5);
        ItemInfo comboStack = MakeStack(942, price: 40, matId: 940, matCount: 3);
        ItemInfo freeNoMat = MakeStack(943, price: 0);
        ItemInfo titleWithMat = MakeEquip(944, EquipmentParts.TitleName, price: 10, matId: 940, matCount: 2);
        ItemInfo oneGoldNoMat = MakeStack(947, price: 1);
        ItemShopDef shop = MakeShop(941, 942, 943, 944, 947);

        Inventory bagFourMats = MakeBagWith(mat, 4);
        Inventory bagFiveMats = MakeBagWith(mat, 5);
        Inventory bagTwelveMats = MakeBagWith(mat, 12);

        // 基础校验次序与边界
        Assert.Equal(ShopResult.NoItem, ShopRules.CheckBuyWithMaterials(shop, null, 1, 1000, bagFiveMats));
        Assert.Equal(ShopResult.NotSold, ShopRules.CheckBuyWithMaterials(shop, MakeStack(999), 1, 1000, bagFiveMats));
        Assert.Equal(ShopResult.BadCount, ShopRules.CheckBuyWithMaterials(shop, comboStack, 0, 1000, bagFiveMats));
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuyWithMaterials(shop, comboStack, 3, 119, bagTwelveMats));
        Assert.Equal(ShopResult.NotBuyable, ShopRules.CheckBuyWithMaterials(shop, titleWithMat, 1, 100, new Inventory(8)));

        // 材料不足 / null 背包 / 恰好足够
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, matOnlyEquip, 1, 0, null));
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, matOnlyEquip, 1, 0, bagFourMats));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, matOnlyEquip, 2, 0, bagFiveMats));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, comboStack, 4, 160, bagTwelveMats));

        // 背包装备栏已满且缺材料时先报材料不够;材料够时再报背包已满
        var fullBagNoMat = new Inventory(1);
        fullBagNoMat.Add(MakeEquip(945), 1);
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, matOnlyEquip, 1, 0, fullBagNoMat));

        var fullBagEnoughMat = new Inventory(1);
        fullBagEnoughMat.Add(MakeEquip(946), 1);
        fullBagEnoughMat.Add(mat, 5);
        Assert.Equal(ShopResult.BagFull, ShopRules.CheckBuyWithMaterials(shop, matOnlyEquip, 1, 0, fullBagEnoughMat));

        // 零价无材料开关只拦截 Price<=0 且无材料的物品
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, freeNoMat, 1, 0, bagFiveMats));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, freeNoMat, 1, 0, bagFiveMats, allowFreeWithoutMaterial: true));
        Assert.Equal(ShopResult.NotBuyable, ShopRules.CheckBuyWithMaterials(shop, freeNoMat, 1, 0, bagFiveMats, allowFreeWithoutMaterial: false));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, matOnlyEquip, 1, 0, bagFiveMats, allowFreeWithoutMaterial: false));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, oneGoldNoMat, 1, 1, bagFiveMats, allowFreeWithoutMaterial: false));

        // 旧签名 CheckBuy 保持向后兼容(不校验 [need material],也不拦截纯零价)
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuy(shop, matOnlyEquip, 1, 0, new Inventory(8)));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuy(shop, freeNoMat, 1, 0, bagFiveMats));
    }

    // ------------------------------------------------------------------ 补测:一次买多件时所需材料要乘件数(变异 H25)

    /// <summary>
    /// 可叠加(非装备)的兑换物品:<c>[price]</c> 为 0、每件需要 2 个材料。一次买 n 件要 2 × n 个材料,
    /// 背包里的材料少于 2 × n 个就必须是 <see cref="ShopResult.NotEnoughMaterial"/>,恰好够或更多才通过。
    /// 现有用例只买 1 件或只买装备(装备恒按 1 件算),"所需材料没乘件数"原先测不出来。
    /// </summary>
    [Theory]
    [InlineData(5, 3, ShopResult.NotEnoughMaterial)]   // 3 件要 2 × 3 = 6 个,只有 5 个
    [InlineData(6, 3, ShopResult.Ok)]                  // 3 件要 6 个,恰好 6 个
    [InlineData(5, 2, ShopResult.Ok)]                  // 2 件要 2 × 2 = 4 个,5 个有余
    [InlineData(3, 2, ShopResult.NotEnoughMaterial)]   // 2 件要 4 个,只有 3 个
    [InlineData(1, 1, ShopResult.NotEnoughMaterial)]   // 1 件要 2 个,只有 1 个
    [InlineData(2, 1, ShopResult.Ok)]                  // 1 件要 2 个,恰好 2 个
    [InlineData(0, 3, ShopResult.NotEnoughMaterial)]   // 空背包:3 件要 6 个,有 0 个
    public void StackableExchangeNeedsMaterialsTimesCount(int owned, int count, ShopResult expected)
    {
        ItemInfo mat = MakeStack(970);
        ItemInfo swap = MakeStack(971, price: 0, matId: 970, matCount: 2);
        ItemShopDef shop = MakeShop(971);

        Assert.Equal(expected, ShopRules.CheckBuyWithMaterials(shop, swap, count, 0, MakeBagWith(mat, owned)));
    }

    /// <summary>金币加材料复合标价的可叠加物品:买 3 件时金币和材料都乘件数;钱的检查先于材料,材料差一个就不通过。</summary>
    [Fact]
    public void StackableGoldAndMaterialExchangeScalesMaterialsAndChecksGoldFirst()
    {
        ItemInfo mat = MakeStack(972);
        ItemInfo swap = MakeStack(973, price: 7, matId: 972, matCount: 2);
        ItemShopDef shop = MakeShop(973);

        // 买 3 件:金币 7 × 3 = 21,材料 2 × 3 = 6
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, swap, 3, 21, MakeBagWith(mat, 5)));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, swap, 3, 21, MakeBagWith(mat, 6)));
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuyWithMaterials(shop, swap, 3, 20, MakeBagWith(mat, 6)));
        Assert.Equal(ShopResult.NotEnoughGold, ShopRules.CheckBuyWithMaterials(shop, swap, 3, 20, MakeBagWith(mat, 5)));
    }

    /// <summary>
    /// 对照:同样 <c>[price]</c> 为 0、每件 2 个材料,但这件是装备——一次恒按 1 件算,
    /// 请求"买 3 件"也只要 2 个材料(不是 2 × 3 = 6 个)。
    /// </summary>
    [Fact]
    public void EquipmentExchangeCountsAsOnePieceSoItNeedsOnlyOneRoundOfMaterials()
    {
        ItemInfo mat = MakeStack(974);
        ItemInfo swapEquip = MakeEquip(975, EquipmentParts.Coat, price: 0, matId: 974, matCount: 2);
        ItemShopDef shop = MakeShop(975);

        // 请求 3 件,装备按 1 件算:需要 2 个
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, swapEquip, 3, 0, MakeBagWith(mat, 2)));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, swapEquip, 3, 0, MakeBagWith(mat, 5)));
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, swapEquip, 3, 0, MakeBagWith(mat, 1)));
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, swapEquip, 3, 0, MakeBagWith(mat, 0)));
        // 请求 1 件与请求 3 件的结果一致
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, swapEquip, 1, 0, MakeBagWith(mat, 2)));
    }

    /// <summary>买入材料总数:可叠加 = 单件数量 × 件数,装备恒一件(同一组 <c>[need material]</c> 数值)。</summary>
    [Fact]
    public void BuyMaterialCountMultipliesStackableByCountAndKeepsEquipmentAtOnePiece()
    {
        ItemInfo stack = MakeStack(976, price: 0, matId: 974, matCount: 2);
        ItemInfo equip = MakeEquip(977, price: 0, matId: 974, matCount: 2);

        Assert.Equal(6L, ItemPricing.BuyMaterialCount(stack, 3));   // 2 × 3
        Assert.Equal(2L, ItemPricing.BuyMaterialCount(equip, 3));   // 装备恒一件:2
        Assert.Equal(2L, ItemPricing.BuyMaterialCount(stack, 1));   // 2 × 1
        Assert.Equal(2L, ItemPricing.BuyMaterialCount(equip, 1));
    }

    // ------------------------------------------------------------------ 补测:材料号/数量无效时买入不要求材料(变异 H14)

    /// <summary>
    /// <see cref="ItemInfo.HasNeedMaterial"/> = 材料号 &gt;= 0 且数量 &gt; 0;不满足时买入材料总数必须是 0,与另一个字段写了什么无关
    /// (现有 <c>noMat</c> 夹具数量也是 0,守卫去掉后乘出来还是 0,测不出来)。0 是合法材料号,只有 -1 才是"没设"。
    /// 列:材料号、单件数量、件数、可叠加的期望、装备的期望(装备恒一件)。
    /// </summary>
    [Theory]
    [InlineData(-1, 3, 2, 0L, 0L)]    // 材料号没设(默认 -1)而数量写了 3:没有材料要求,不是 3 × 2 = 6(装备也不是 3)
    [InlineData(-1, 3, 1, 0L, 0L)]    // 买 1 件也一样,不是 3
    [InlineData(-2, 3, 2, 0L, 0L)]    // 其它负材料号同样无效
    [InlineData(0, 3, 2, 6L, 3L)]     // 0 是合法材料号:可叠加 3 × 2 = 6,装备恒一件 = 3
    [InlineData(5, 0, 2, 0L, 0L)]     // 数量为 0:没有材料要求
    [InlineData(5, -4, 2, 0L, 0L)]    // 数量为负:没有材料要求,不是 -4 × 2 = -8(装备也不是 -4)
    [InlineData(5, 3, 2, 6L, 3L)]     // 对照:材料号与数量都有效,可叠加 3 × 2 = 6,装备 3
    public void BuyMaterialCountIsZeroWithoutValidNeedMaterial(int matId, int matCount, int count, long expectedStack, long expectedEquip)
    {
        ItemInfo stack = MakeStack(978, price: 0, matId: matId, matCount: matCount);
        ItemInfo equip = MakeEquip(979, price: 0, matId: matId, matCount: matCount);

        Assert.Equal(expectedStack, ItemPricing.BuyMaterialCount(stack, count));
        Assert.Equal(expectedEquip, ItemPricing.BuyMaterialCount(equip, count));
    }

    /// <summary>材料号没设(-1)而数量写了 3:买入不要求材料,空背包与 null 背包都不报 NotEnoughMaterial。</summary>
    [Fact]
    public void CheckBuyWithMaterialsDoesNotRequireMaterialsWhenMaterialIdIsUnset()
    {
        ItemInfo stack = MakeStack(980, price: 0, matId: -1, matCount: 3);
        ItemInfo equip = MakeEquip(981, EquipmentParts.Coat, price: 0, matId: -1, matCount: 3);
        ItemShopDef shop = MakeShop(980, 981);

        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, stack, 2, 0, new Inventory(8)));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, stack, 2, 0, null));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, equip, 1, 0, new Inventory(8)));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, equip, 1, 0, null));
        // 只有数量没有材料号不算材料:仍是"零价无材料",关掉开关时被拦下
        Assert.Equal(ShopResult.NotBuyable, ShopRules.CheckBuyWithMaterials(shop, stack, 2, 0, new Inventory(8), allowFreeWithoutMaterial: false));
    }

    /// <summary>材料号 0 是合法材料:每件 3 个材料 0,买 2 件要 3 × 2 = 6 个,不足就报 NotEnoughMaterial。</summary>
    [Fact]
    public void MaterialIdZeroIsAValidMaterialForCheckBuyWithMaterials()
    {
        ItemInfo mat = MakeStack(0);
        ItemInfo swap = MakeStack(982, price: 0, matId: 0, matCount: 3);
        ItemShopDef shop = MakeShop(982);

        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, swap, 2, 0, new Inventory(8)));
        Assert.Equal(ShopResult.NotEnoughMaterial, ShopRules.CheckBuyWithMaterials(shop, swap, 2, 0, MakeBagWith(mat, 5)));
        Assert.Equal(ShopResult.Ok, ShopRules.CheckBuyWithMaterials(shop, swap, 2, 0, MakeBagWith(mat, 6)));
    }
}
