using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// NPC 商店窗口(<see cref="ShopWindow"/>、<see cref="ShopWindowArt"/>、<see cref="TownShopNpcs"/>,本项目自己的占位设计,证据 E-119)与它在流程里的开关
/// (<see cref="GameFlow.OpenShop"/>)。商店、物品、等级进度都是合成的;窗口只管状态与点击判定,画法只看精灵的组成。
/// </summary>
public class ShopWindowTests
{
    // ------------------------------------------------------------------ 合成数据

    private static ItemInfo Equip(int code, int part = EquipmentParts.Coat, int price = 100, int value = 400)
    {
        var item = new ItemInfo(code, "equipment/test/e" + code + ".equ", ItemKind.Equipment) { Part = part, Price = price, Value = value, Grade = 10, Rarity = 2, MinimumLevel = 1 };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    private static ItemInfo Stack(int code, int price = 10, int value = 40, int limit = 99)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = StackableTypes.Waste,
            Price = price,
            Value = value,
            StackLimit = limit,
        };
    }

    /// <summary>
    /// 商店 3:第一页 { 1 号装备(价格 100)、2 号消耗品(价格 10)、空格、3 号称号、4 号消耗品(价格 5000) },第二页 { 5 号消耗品(价格 20) };
    /// 商店 4 没有页;商店 5 一页 20 行(只有最后一格有商品)。收购倍率 500 / 0 / 100。
    /// </summary>
    internal static ShopBook Book()
    {
        var items = new Dictionary<int, ItemInfo>
        {
            [1] = Equip(1),
            [2] = Stack(2),
            [3] = Equip(3, EquipmentParts.TitleName, price: 1),
            [4] = Stack(4, price: 5000),
            [5] = Stack(5, price: 20, value: 100),
        };
        var shops = new Dictionary<int, ItemShopDef>
        {
            [3] = new ItemShopDef(3, "itemshop/a.shp", 11, "[etc shop]", null, new[]
            {
                new ItemShopTab(new[] { 1, 2, -1, 3, 4 }, null),
                new ItemShopTab(new[] { 5 }, null),
            }),
            [4] = new ItemShopDef(4, "itemshop/b.shp", 12, "[weapon shop]", null, new ItemShopTab[0]),
            [5] = new ItemShopDef(5, "itemshop/c.shp", 13, null, null, new[] { new ItemShopTab(Enumerable.Range(0, 140).Select(i => i == 139 ? 5 : -1).ToArray(), null) }),
        };
        return new ShopBook(i => shops.TryGetValue(i, out ItemShopDef s) ? s : null, c => items.TryGetValue(c, out ItemInfo it) ? it : null,
            new ItemPriceRates(500, 0, 100), () => shops.Keys.OrderBy(k => k).ToList());
    }

    private static ShopWindow Opened(int shop = 3)
    {
        var w = new ShopWindow();
        w.Use(Book());
        Assert.True(w.Show(GameStage.Town, shop));
        return w;
    }

    private static HeroProgress Rich(long gold)
    {
        HeroProgress p = Progress();
        p.AddGold(gold);
        return p;
    }

    private static (int X, int Y) Center((int X, int Y, int W, int H) r) => (r.X + r.W / 2, r.Y + r.H / 2);

    private static bool Click(ShopWindow w, (int X, int Y, int W, int H) rect, HeroProgress p)
    {
        (int x, int y) = Center(rect);
        return w.Click(x, y, p);
    }

    // ------------------------------------------------------------------ 开关与光标

    [Fact]
    public void ShowNeedsABookATownAndAKnownShop()
    {
        var w = new ShopWindow();
        Assert.False(w.Show(GameStage.Town, 3));                                // 没接数据本
        Assert.Throws<ArgumentNullException>(() => w.Use(null));
        w.Use(Book());
        foreach (GameStage stage in new[] { GameStage.Dungeon, GameStage.Loading, GameStage.DungeonSelect, GameStage.CharacterSelect })
        {
            Assert.False(w.Show(stage, 3));
            Assert.False(w.Open);
        }
        Assert.False(w.Show(GameStage.Town, 99));                                // 读不出这家店
        Assert.True(w.Show(GameStage.Town, 3));
        Assert.Equal((true, ShopMode.Buy, 0, 0, 0, string.Empty), (w.Open, w.Mode, w.ShopTab, w.Cursor, w.ScrollRow, w.Message));
        Assert.Equal(3, w.Shop.Index);
        Assert.Equal(ShopPromptKind.None, w.PromptKind);
    }

    [Fact]
    public void ShowAgainResetsModePageAndCursor()
    {
        ShopWindow w = Opened();
        w.NextTab(1);
        w.Move(1, 1);
        w.ToggleMode();
        w.Close();
        Assert.False(w.Open);
        Assert.True(w.Show(GameStage.Town, 3));
        Assert.Equal((ShopMode.Buy, 0, InventoryTab.Equipment, 0), (w.Mode, w.ShopTab, w.SellTab, w.Cursor));
    }

    [Fact]
    public void AClosedWindowIgnoresEveryInput()
    {
        var w = new ShopWindow();
        w.Use(Book());
        HeroProgress p = Rich(1000);
        w.Move(1, 1);
        w.NextTab(1);
        w.ToggleMode();
        w.SetMode(ShopMode.Sell);
        Assert.False(w.Activate(p));
        Assert.False(Click(w, ShopWindow.ModeRect(ShopMode.Sell), p));
        Assert.False(w.PromptConfirm(p));
        Assert.Equal((ShopMode.Buy, 0, 0, 1000L), (w.Mode, w.Cursor, w.ShopTab, p.Gold));
        Assert.Equal((0, ShopResult.Ok), (w.Purchases, w.LastResult));
        Assert.Equal((false, false), w.TryTradeAny(p, out int tried));
        Assert.Equal(0, tried);
    }

    [Fact]
    public void TheBuyCursorStaysInsideSevenColumnsAndAtLeastSevenRows()
    {
        ShopWindow w = Opened();
        Assert.Equal((7, 7, 49), (ShopWindow.BuyColumns, w.BuyRows, w.CellTotal));
        w.Move(0, -1);
        Assert.Equal(0, w.Cursor);
        w.Move(1, 0);
        w.Move(0, 1);
        Assert.Equal(ShopWindow.BuyColumns + 1, w.Cursor);
        for (int i = 0; i < 20; i++)
        {
            w.Move(1, 1);
        }
        Assert.Equal(48, w.Cursor);                                               // 右下角夹住
        Assert.Equal(0, w.ScrollRow);
    }

    [Fact]
    public void TheBuyGridScrollsWhenThereAreMoreRowsThanTheScreenShows()
    {
        ShopWindow w = Opened(5);
        Assert.Equal((20, 140), (w.BuyRows, w.CellTotal));
        for (int i = 0; i < 6; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal((6 * 7, 0), (w.Cursor, w.ScrollRow));                        // 第 7 行还在屏内
        w.Move(0, 1);
        Assert.Equal((7 * 7, 1), (w.Cursor, w.ScrollRow));                        // 第 8 行:往下滚一行
        for (int i = 0; i < 30; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal((19 * 7, 13), (w.Cursor, w.ScrollRow));                      // 最后一行:滚到底
        for (int i = 0; i < 30; i++)
        {
            w.Move(0, -1);
        }
        Assert.Equal((0, 0), (w.Cursor, w.ScrollRow));
    }

    [Fact]
    public void CellHitTestingFollowsTheScroll()
    {
        ShopWindow w = Opened(5);
        (int x, int y) = Center(ShopWindow.BuyCellRect(0));
        Assert.Equal(0, w.CellAt(x, y));
        for (int i = 0; i < 8; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal((8 * 7, 2), (w.Cursor, w.ScrollRow));                        // 光标在第 9 行:屏上最上面一行是第 3 行
        Assert.Equal(2 * 7, w.CellAt(x, y));                                      // 屏上第一格是第 3 行第一个
        (int x2, int y2) = Center(ShopWindow.BuyCellRect(48));
        Assert.Equal(2 * 7 + 48, w.CellAt(x2, y2));
        Assert.Equal(-1, w.CellAt(0, 0));
        Assert.Equal(-1, w.CellAt(ShopWindow.GridX - 1, ShopWindow.GridY));
    }

    [Fact]
    public void PagesCycleAndSellPagesAreTheBagsFour()
    {
        ShopWindow w = Opened();
        Assert.Equal(2, w.TabCount);
        w.Move(2, 1);
        w.NextTab(1);
        Assert.Equal((1, 0), (w.ShopTab, w.Cursor));                               // 换页回到第一格
        w.NextTab(1);
        Assert.Equal(0, w.ShopTab);
        w.NextTab(-1);
        Assert.Equal(1, w.ShopTab);
        w.ToggleMode();
        Assert.Equal((ShopMode.Sell, 4, 8, 56), (w.Mode, w.TabCount, w.Columns, w.CellTotal));
        w.NextTab(-1);
        Assert.Equal(InventoryTab.Quest, w.SellTab);                              // 往回绕
        w.NextTab(1);
        Assert.Equal(InventoryTab.Equipment, w.SellTab);

        ShopWindow empty = Opened(4);
        Assert.Equal(1, empty.TabCount);                                           // 没有页的店也画一页空格
        empty.NextTab(1);
        Assert.Equal(0, empty.ShopTab);
        Assert.Equal(-1, empty.CodeAtCursor);
    }

    [Fact]
    public void TogglingTheModeResetsTheCursorAndTheMessage()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        w.Move(2, 0);
        w.Activate(p);                                                             // 空格:有提示
        Assert.NotEqual(string.Empty, w.Message);
        w.ToggleMode();
        Assert.Equal((ShopMode.Sell, 0, string.Empty), (w.Mode, w.Cursor, w.Message));
        w.Move(3, 3);
        w.SetMode(ShopMode.Sell);                                                  // 同一模式:光标不动
        Assert.Equal(3 * 8 + 3, w.Cursor);
        w.ToggleMode();
        Assert.Equal((ShopMode.Buy, 0), (w.Mode, w.Cursor));
    }

    // ------------------------------------------------------------------ 买入

    [Fact]
    public void BuyingEquipmentTakesOneImmediatelyAndPaysAfterTheItemIsInTheBag()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(1000);
        Assert.Equal(1, w.CodeAtCursor);
        Assert.True(w.Activate(p));
        Assert.Equal(900L, p.Gold);
        Assert.Equal(1, p.Bag.CountOf(1));
        Assert.Equal((1, 1, 100L, ShopResult.Ok), (w.Purchases, w.ItemsBought, w.Spent, w.LastResult));
        Assert.Equal("BOUGHT 1 FOR 100", w.Message);
        Assert.Equal(0L, p.ItemsGained);                                            // 买到的不算"拾到"
    }

    [Fact]
    public void BuyingAStackableAsksForTheCountFirst()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(100);
        w.Move(1, 0);
        Assert.Equal(2, w.CodeAtCursor);
        Assert.False(w.Activate(p));                                               // 先弹数量框,还没成交
        Assert.Equal((ShopPromptKind.Buy, 1, 10), (w.PromptKind, w.PromptCount, w.PromptMax));
        Assert.Equal(100L, p.Gold);
        w.PromptAdjust(3);
        Assert.Equal((4, 10L, 40L), (w.PromptCount, w.PromptUnit, w.PromptTotal));
        Assert.True(w.Activate(p));                                                // 框开着时回车 = 确认
        Assert.Equal(ShopPromptKind.None, w.PromptKind);
        Assert.Equal((60L, 4), (p.Gold, p.Bag.CountOf(2)));
        Assert.Equal((1, 4, 40L), (w.Purchases, w.ItemsBought, w.Spent));
        Assert.Equal("BOUGHT 4 FOR 40", w.Message);
    }

    [Fact]
    public void ThePromptCountIsClampedBetweenOneAndTheMaximum()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(100);
        w.Move(1, 0);
        w.Activate(p);
        w.PromptAdjust(100);
        Assert.Equal(10, w.PromptCount);
        w.PromptAdjust(-100);
        Assert.Equal(1, w.PromptCount);
        w.PromptSetMax();
        Assert.Equal(10, w.PromptCount);
        Assert.Equal(100L, w.PromptTotal);
        // 方向键:向上 +1、向下 −1、向右 +10、向左 −10(屏幕方向)
        w.PromptAdjust(-100);
        w.Move(0, -1);
        Assert.Equal(2, w.PromptCount);
        w.Move(1, 0);
        Assert.Equal(10, w.PromptCount);                                           // 12 夹到上限 10
        w.Move(-1, 0);
        Assert.Equal(1, w.PromptCount);
        w.Move(0, 1);
        Assert.Equal(1, w.PromptCount);
        Assert.Equal(1, w.Cursor);                                                 // 框开着时方向键不动格子光标
    }

    [Fact]
    public void CancellingThePromptBuysNothing()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(100);
        w.Move(1, 0);
        w.Activate(p);
        w.CancelPrompt();
        Assert.Equal((ShopPromptKind.None, 100L, 0), (w.PromptKind, p.Gold, w.Purchases));
        Assert.False(w.PromptConfirm(p));                                          // 框没开:确认无效
        w.PromptAdjust(5);
        Assert.Equal(0, w.PromptCount);
    }

    [Fact]
    public void ChangingTheModeOrThePageClosesThePrompt()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(100);
        w.Move(1, 0);
        w.Activate(p);
        w.NextTab(1);
        Assert.False(w.IsPromptOpen);
        w.NextTab(-1);
        w.Move(1, 0);
        w.Activate(p);
        w.ToggleMode();
        Assert.False(w.IsPromptOpen);
    }

    [Fact]
    public void NotEnoughGoldGivesAMessageAndNoPrompt()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(50);
        Assert.False(w.Activate(p));                                               // 1 号装备要 100
        Assert.Equal((ShopResult.NotEnoughGold, "NOT ENOUGH GOLD", 50L, 0), (w.LastResult, w.Message, p.Gold, w.Purchases));
        Assert.Equal(0, p.Bag.TotalCount);
        w.Move(1, 0);
        HeroProgress broke = Rich(0);
        Assert.False(w.Activate(broke));                                           // 2 号一件也买不起:不弹框
        Assert.Equal((ShopPromptKind.None, ShopResult.NotEnoughGold), (w.PromptKind, w.LastResult));
    }

    [Fact]
    public void AStackableThatOnlyOneCanBeAffordedIsBoughtDirectly()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(10);
        w.Move(1, 0);
        Assert.True(w.Activate(p));
        Assert.Equal((ShopPromptKind.None, 0L, 1), (w.PromptKind, p.Gold, p.Bag.CountOf(2)));
    }

    [Fact]
    public void EmptyCellsAndMissingItemsSayNothingThere()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(1000);
        w.Move(2, 0);
        Assert.Equal(-1, w.CodeAtCursor);
        Assert.False(w.Activate(p));
        Assert.Equal("NOTHING THERE", w.Message);
        ShopWindow noShop = Opened(4);
        Assert.False(noShop.Activate(p));
        Assert.Equal("NOTHING THERE", noShop.Message);
    }

    [Fact]
    public void TitlesCannotBeBoughtEvenWithEnoughGold()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(1000);
        w.Move(3, 0);
        Assert.Equal(3, w.CodeAtCursor);
        Assert.False(w.Activate(p));
        Assert.Equal((ShopResult.NotBuyable, "CANNOT BUY THIS", 1000L), (w.LastResult, w.Message, p.Gold));
    }

    [Fact]
    public void ABagWithNoRoomRefusesBeforeAnyMoneyChangesHands()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(1000);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.True(p.AddItem(Equip(1000 + i)));
        }
        Assert.False(w.Activate(p));
        Assert.Equal((ShopResult.BagFull, "BAG IS FULL", 1000L), (w.LastResult, w.Message, p.Gold));
        Assert.Equal(0, p.Bag.CountOf(1));
    }

    // ------------------------------------------------------------------ 出售

    [Fact]
    public void SellingEquipmentPaysTheRatedValueAndRemovesTheItem()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        p.AddItem(Equip(1));                                                       // 价值 400,倍率 500 → 200
        w.ToggleMode();
        Assert.Equal(1, w.ItemAtCursor(p).Code);
        Assert.True(w.Activate(p));
        Assert.Equal((200L, 0), (p.Gold, p.Bag.TotalCount));
        Assert.Equal((1, 1, 200L, ShopResult.Ok), (w.Sales, w.ItemsSold, w.Earned, w.LastResult));
        Assert.Equal("SOLD 1 FOR 200", w.Message);
        Assert.Equal(200L, p.GoldGained);                                          // 卖得的钱也算进账
    }

    [Fact]
    public void TheKiriProtectableRateIsUsedForFlaggedEquipment()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        ItemInfo flagged = Equip(1);
        flagged.KiriProtectable = true;                                            // 倍率 100 → 40
        p.AddItem(flagged);
        w.ToggleMode();
        Assert.True(w.Activate(p));
        Assert.Equal(40L, p.Gold);
    }

    [Fact]
    public void SellingPartOfAStackAsksForTheCountAndKeepsTheRest()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        p.AddItem(Stack(2), 30);                                                   // 价值 40,倍率 500 → 20 一个
        w.ToggleMode();
        w.NextTab(1);
        Assert.False(w.Activate(p));
        Assert.Equal((ShopPromptKind.Sell, 1, 30), (w.PromptKind, w.PromptCount, w.PromptMax));
        w.PromptAdjust(11);
        Assert.Equal((12, 20L, 240L), (w.PromptCount, w.PromptUnit, w.PromptTotal));
        Assert.True(w.PromptConfirm(p));
        Assert.Equal((240L, 18), (p.Gold, p.Bag.CountOf(2)));
        Assert.Equal((1, 12, 240L), (w.Sales, w.ItemsSold, w.Earned));
        Assert.Equal("SOLD 12 FOR 240", w.Message);
    }

    [Fact]
    public void SellingTheLastOneOfAStackNeedsNoPromptAndFreesTheSlot()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        p.AddItem(Stack(2), 1);
        w.ToggleMode();
        w.NextTab(1);
        Assert.True(w.Activate(p));
        Assert.Equal((ShopPromptKind.None, 20L, 0), (w.PromptKind, p.Gold, p.Bag.StackCount));
    }

    [Fact]
    public void TheShopRefusesAvatarsTitlesAndEmptyCells()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        p.AddItem(Equip(6, EquipmentParts.HatAvatar));
        p.AddItem(Equip(7, EquipmentParts.TitleName));
        w.ToggleMode();
        Assert.False(w.Activate(p));
        Assert.Equal((ShopResult.NotSellable, "SHOP WILL NOT TAKE THIS"), (w.LastResult, w.Message));
        w.Move(1, 0);
        Assert.False(w.Activate(p));
        Assert.Equal(ShopResult.NotSellable, w.LastResult);
        w.Move(1, 0);                                                              // 第三格是空的
        Assert.False(w.Activate(p));
        Assert.Equal("NOTHING THERE", w.Message);
        Assert.Equal((0L, 2, 0), (p.Gold, p.Bag.TotalCount, w.Sales));
    }

    [Fact]
    public void TradeDeleteAndFinishPointItemsCannotBeSold()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        ItemInfo bound = Equip(1);
        bound.AttachType = "[trade delete]";
        p.AddItem(bound);
        ItemInfo special = Stack(2);
        special.FinishPointPrice = 5;
        p.AddItem(special, 3);
        w.ToggleMode();
        Assert.False(w.Activate(p));
        Assert.Equal(ShopResult.NotSellable, w.LastResult);
        w.NextTab(1);
        Assert.False(w.Activate(p));
        Assert.False(w.IsPromptOpen);
        Assert.Equal((0L, 4), (p.Gold, p.Bag.TotalCount));
    }

    // ------------------------------------------------------------------ 鼠标

    [Fact]
    public void ClickingTheModeButtonsAndPagesSwitchesThem()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        Assert.True(Click(w, ShopWindow.ModeRect(ShopMode.Sell), p));
        Assert.Equal(ShopMode.Sell, w.Mode);
        Assert.True(Click(w, ShopWindow.TabRect(ShopMode.Sell, 2), p));
        Assert.Equal(InventoryTab.Material, w.SellTab);
        Assert.True(Click(w, ShopWindow.ModeRect(ShopMode.Buy), p));
        Assert.Equal(ShopMode.Buy, w.Mode);
        Assert.True(Click(w, ShopWindow.TabRect(ShopMode.Buy, 1), p));
        Assert.Equal(1, w.ShopTab);
        Assert.False(Click(w, ShopWindow.TabRect(ShopMode.Buy, 2), p));            // 这家店只有两页:第三个位置不是页签
        Assert.False(w.Click(0, 0, p));
    }

    [Fact]
    public void ClickingACellSelectsItAndClickingTheSelectedOneAgainActivates()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(500);
        Assert.True(Click(w, ShopWindow.BuyCellRect(1), p));
        Assert.Equal((1, 0, 500L), (w.Cursor, w.Purchases, p.Gold));               // 第一下只选中
        Assert.True(Click(w, ShopWindow.BuyCellRect(1), p));
        Assert.Equal((ShopPromptKind.Buy, 50), (w.PromptKind, w.PromptMax));       // 2 号是消耗品(单价 10):再点一下弹数量框,最多 500 ÷ 10 件
        // 数量框开着:框外的点击被吞掉,不改光标
        Assert.True(Click(w, ShopWindow.BuyCellRect(0), p));
        Assert.Equal((1, ShopPromptKind.Buy), (w.Cursor, w.PromptKind));
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonPlus), p));
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonPlus), p));
        Assert.Equal(3, w.PromptCount);
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonMinus), p));
        Assert.Equal(2, w.PromptCount);
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonMax), p));
        Assert.Equal(50, w.PromptCount);
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonCancel), p));
        Assert.Equal((ShopPromptKind.None, 500L), (w.PromptKind, p.Gold));
        Assert.True(Click(w, ShopWindow.BuyCellRect(1), p));                       // 光标还在这一格:点一下就重新弹出
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonMax), p));
        Assert.True(Click(w, ShopWindow.PromptButtonRect(ShopWindow.ButtonOk), p));
        Assert.Equal((0L, 50), (p.Gold, p.Bag.CountOf(2)));
    }

    [Fact]
    public void ClickingTheAlreadySelectedEquipmentCellBuysIt()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(1000);
        Assert.Equal(0, w.Cursor);
        Assert.True(Click(w, ShopWindow.BuyCellRect(0), p));                       // 光标本来就在第一格:这一下等于回车
        Assert.Equal((900L, 1), (p.Gold, p.Bag.CountOf(1)));
    }

    [Fact]
    public void ClickingInsideThePromptButNotOnAButtonChangesNothing()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(500);
        w.Move(1, 0);
        w.Activate(p);
        Assert.True(w.Click(ShopWindow.PromptX + 2, ShopWindow.PromptY + 2, p));
        Assert.Equal((ShopPromptKind.Buy, 1, 500L), (w.PromptKind, w.PromptCount, p.Gold));
        Assert.Equal(-1, ShopWindow.HitPromptButton(ShopWindow.PromptX + 2, ShopWindow.PromptY + 2));
        Assert.Equal(ShopWindow.ButtonOk, ShopWindow.HitPromptButton(ShopWindow.PromptButtonRect(ShopWindow.ButtonOk).X + 1, ShopWindow.PromptButtonRect(ShopWindow.ButtonOk).Y + 1));
    }

    // ------------------------------------------------------------------ 无人值守演习

    [Fact]
    public void TheProbeBuysTheFirstAffordableItemAndSellsItBack()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(1000);
        (bool bought, bool sold) = w.TryTradeAny(p, out int tried);
        Assert.True(bought);
        Assert.True(sold);
        Assert.Equal(1, tried);
        Assert.Equal(1100L, p.Gold);                                                // −100 + 200
        Assert.Equal((1, 1, 0), (w.Purchases, w.Sales, p.Bag.TotalCount));
    }

    [Fact]
    public void TheProbeGivesUpWhenNothingIsAffordable()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        (bool bought, bool sold) = w.TryTradeAny(p, out int tried);
        Assert.False(bought);
        Assert.False(sold);
        Assert.Equal(5, tried);                                                     // 第一页 4 件加第二页 1 件
        Assert.Equal(0L, p.Gold);
    }

    [Fact]
    public void TheProbeCanSellAStackableItBought()
    {
        // 只有消耗品买得起(装备价格高):买 1 件再卖回去
        var shops = new Dictionary<int, ItemShopDef> { [1] = new ItemShopDef(1, "p", 1, null, null, new[] { new ItemShopTab(new[] { 1, 2 }, null) }) };
        var items = new Dictionary<int, ItemInfo> { [1] = Equip(1, price: 9999), [2] = Stack(2) };
        var w = new ShopWindow();
        w.Use(new ShopBook(i => shops.TryGetValue(i, out ItemShopDef s) ? s : null, c => items.TryGetValue(c, out ItemInfo it) ? it : null, new ItemPriceRates(500, 0, 100)));
        Assert.True(w.Show(GameStage.Town, 1));
        HeroProgress p = Rich(50);
        (bool bought, bool sold) = w.TryTradeAny(p, out int tried);
        Assert.Equal((true, true, 2), (bought, sold, tried));
        Assert.Equal(60L, p.Gold);                                                  // −10 + 20
    }

    // ------------------------------------------------------------------ 画法

    private static List<SceneSprite> Draw(ShopWindow w, HeroProgress p, ISpriteSource sprites = null)
    {
        return ShopWindowArt.Build(w, p, sprites, new OverlayArt());
    }

    private static int Count(List<SceneSprite> sprites, string tag)
    {
        return sprites.Count(s => s.AnimationPath == tag);
    }

    /// <summary>只数格子区里(详情栏的左边)的精灵:详情栏里光标处的物品也会画一个图标与稀有度条。</summary>
    private static int CountInGrid(List<SceneSprite> sprites, string tag)
    {
        return sprites.Count(s => s.AnimationPath == tag && s.ScreenX < ShopWindow.DetailX);
    }

    [Fact]
    public void AClosedWindowDrawsNothingAndAnOpenOneNeedsAProgress()
    {
        var w = new ShopWindow();
        Assert.Empty(Draw(w, Progress()));
        Assert.Empty(Draw(w, null));
        w = Opened();
        Assert.Throws<ArgumentNullException>(() => Draw(w, null));
    }

    [Fact]
    public void TheBuyViewDrawsTheChromeTwoModeButtonsPagesAndFortyNineCells()
    {
        ShopWindow w = Opened();
        List<SceneSprite> sprites = Draw(w, Rich(0));
        foreach (string tag in new[] { "shop-dim", "shop-frame", "shop-panel", "shop-title-bar", "shop-title", "shop-gold", "shop-detail", "shop-hint" })
        {
            Assert.Equal(1, Count(sprites, tag));
        }
        Assert.Equal((1, 1, 2), (Count(sprites, "shop-mode"), Count(sprites, "shop-mode-selected"), Count(sprites, "shop-mode-label")));
        Assert.Equal((1, 1, 2), (Count(sprites, "shop-tab"), Count(sprites, "shop-tab-selected"), Count(sprites, "shop-tab-label")));
        Assert.Equal(49, Count(sprites, "shop-cell"));
        Assert.Equal(1, Count(sprites, "shop-cell-selected"));
        Assert.Equal(0, Count(sprites, "shop-track") + Count(sprites, "shop-thumb"));            // 7 行以内不要滚动条
        Assert.Equal(0, Count(sprites, "shop-prompt-dim"));
        Assert.Equal(sprites.Select((s, i) => i).ToList(), sprites.Select(s => s.SortKey).ToList());
    }

    [Fact]
    public void UnaffordableAndUnbuyableGoodsAreDimmed()
    {
        ShopWindow w = Opened();
        List<SceneSprite> poor = Draw(w, Rich(0));
        Assert.Equal((4, 4, 4), (CountInGrid(poor, "bag-icon-block"), CountInGrid(poor, "bag-rarity"), Count(poor, "shop-icon-dim")));
        Assert.Equal((5, 5), (Count(poor, "bag-icon-block"), Count(poor, "bag-rarity")));                // 加上详情栏里光标处物品的那一个
        List<SceneSprite> some = Draw(w, Rich(100));
        Assert.Equal(2, Count(some, "shop-icon-dim"));                                           // 称号永远买不了;5000 的买不起
        List<SceneSprite> rich = Draw(w, Rich(100000));
        Assert.Equal(1, Count(rich, "shop-icon-dim"));                                           // 只剩称号
    }

    [Fact]
    public void ALongTabDrawsAScrollBarWhoseThumbFollowsTheScroll()
    {
        ShopWindow w = Opened(5);
        HeroProgress p = Rich(0);
        List<SceneSprite> top = Draw(w, p);
        Assert.Equal((1, 1), (Count(top, "shop-track"), Count(top, "shop-thumb")));
        SceneSprite thumbTop = top.First(s => s.AnimationPath == "shop-thumb");
        for (int i = 0; i < 25; i++)
        {
            w.Move(0, 1);
        }
        List<SceneSprite> bottom = Draw(w, p);
        SceneSprite thumbBottom = bottom.First(s => s.AnimationPath == "shop-thumb");
        SceneSprite track = bottom.First(s => s.AnimationPath == "shop-track");
        Assert.True(thumbBottom.ScreenY > thumbTop.ScreenY);
        Assert.True(thumbBottom.ScreenY + thumbBottom.Frame.Height <= track.ScreenY + track.Frame.Height);
        Assert.Equal(track.ScreenY, thumbTop.ScreenY);
    }

    [Fact]
    public void TheSellViewDrawsFiftySixCellsDimsWhatTheShopWillNotTakeAndShowsCounts()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        p.AddItem(Equip(1));
        p.AddItem(Equip(6, EquipmentParts.HatAvatar));
        w.ToggleMode();
        List<SceneSprite> sprites = Draw(w, p);
        Assert.Equal((56, 1, 0), (Count(sprites, "shop-cell"), Count(sprites, "shop-cell-selected"), Count(sprites, "shop-count")));
        Assert.Equal((2, 1), (CountInGrid(sprites, "bag-icon-block"), Count(sprites, "shop-icon-dim")));   // 时装被压暗
        Assert.Equal((3, 1), (Count(sprites, "shop-tab"), Count(sprites, "shop-tab-selected")));
        p.AddItem(Stack(2), 12);
        w.NextTab(1);
        sprites = Draw(w, p);
        Assert.Equal(1, Count(sprites, "shop-count"));
    }

    [Fact]
    public void AnIconComesFromTheItemScriptsImageAndFrame()
    {
        ShopWindow w = Opened();
        ItemInfo sword = Equip(8);
        sword.Icon = "Item/Icons.img";
        sword.IconFrame = 3;
        HeroProgress p = Rich(0);
        p.AddItem(sword);
        w.ToggleMode();
        var sprites = new StubSprites();
        sprites.Frames[("sprite/item/icons.img", 3)] = Opaque(20, 20);
        List<SceneSprite> drawn = Draw(w, p, sprites);
        Assert.Equal(2, Count(drawn, "bag-icon"));                                                // 格子里一个,右边详情栏一个
        Assert.Equal(0, Count(drawn, "bag-icon-block"));
    }

    [Fact]
    public void TheDetailPanelListsLevelPriceAndOwnedCount()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(0);
        p.AddItem(Equip(1), 1);
        Assert.Equal(new[] { "LV 1", "BUY 100", "OWN 1" }, ShopWindowArt.DetailLines(w, p, w.ItemAtCursor(p)).ToArray());
        w.ToggleMode();
        Assert.Equal(new[] { "LV 1", "SELL 200", "OWN 1" }, ShopWindowArt.DetailLines(w, p, w.ItemAtCursor(p)).ToArray());
        p.AddItem(Equip(6, EquipmentParts.HatAvatar));
        w.Move(1, 0);
        Assert.Equal(new[] { "LV 1", "NO SALE", "OWN 1" }, ShopWindowArt.DetailLines(w, p, w.ItemAtCursor(p)).ToArray());
        Assert.Empty(ShopWindowArt.DetailLines(w, p, null));
    }

    [Fact]
    public void ThePromptDrawsADimBoxAndFiveButtons()
    {
        ShopWindow w = Opened();
        HeroProgress p = Rich(100);
        w.Move(1, 0);
        w.Activate(p);
        List<SceneSprite> sprites = Draw(w, p);
        foreach (string tag in new[] { "shop-prompt-dim", "shop-prompt-frame", "shop-prompt-panel", "shop-prompt-title", "shop-prompt-count", "shop-prompt-price" })
        {
            Assert.Equal(1, Count(sprites, tag));
        }
        Assert.Equal((5, 5), (Count(sprites, "shop-prompt-button"), Count(sprites, "shop-prompt-label")));
        SceneSprite dim = sprites.First(s => s.AnimationPath == "shop-prompt-dim");
        Assert.True(dim.TintAlpha < 255);
        // 框画在窗口之上:数量框的精灵都排在窗口的精灵之后
        int lastWindow = sprites.FindLastIndex(s => s.AnimationPath == "shop-hint");
        Assert.True(sprites.FindIndex(s => s.AnimationPath == "shop-prompt-dim") > lastWindow);
    }

    [Fact]
    public void TheTextsFollowTheShopTypeAndTheMode()
    {
        Assert.Equal("SHOP", ShopWindowArt.TitleText(null));
        Assert.Equal("WEAPON SHOP", ShopWindowArt.TitleText(new ItemShopDef(1, "p", 1, "[weapon shop]", null, new ItemShopTab[0])));
        Assert.Equal("GENERAL SHOP", ShopWindowArt.TitleText(new ItemShopDef(1, "p", 1, "[ETC SHOP]", null, new ItemShopTab[0])));
        Assert.Equal("SHOP", ShopWindowArt.TitleText(new ItemShopDef(1, "p", 1, "[unknown]", null, new ItemShopTab[0])));
        Assert.Equal("PAGE 3", ShopWindowArt.TabLabel(ShopMode.Buy, 2));
        Assert.Equal("USE", ShopWindowArt.TabLabel(ShopMode.Sell, 1));
        ShopWindow w = Opened();
        Assert.Contains("ENTER BUY", ShopWindowArt.HintText(w));
        w.ToggleMode();
        Assert.Contains("ENTER SELL", ShopWindowArt.HintText(w));
        w.ToggleMode();
        w.Move(1, 0);
        w.Activate(Rich(100));
        Assert.Contains("ESC CANCEL", ShopWindowArt.HintText(w));
        Assert.Equal(string.Empty, ShopWindowArt.InfoText(null));
        Assert.Equal("LV 1  GRADE 10  RARITY 2  PRICE 100", ShopWindowArt.InfoText(Equip(1)));
        Assert.Equal(string.Empty, ShopWindowArt.FitText(Stack(2), Progress()));
        Assert.Equal("CAN EQUIP", ShopWindowArt.FitText(Equip(1), Progress()));
        ItemInfo high = Equip(9);
        high.MinimumLevel = 50;
        Assert.Equal("NEED LEVEL 50", ShopWindowArt.FitText(high, Progress()));
    }

    // ------------------------------------------------------------------ 流程里的开关

    private sealed class FakeTown : GameFlowTests.FakeHero, ITownNpcSource
    {
        public FakeTown(params TownNpc[] npcs) : base("town")
        {
            Npcs = npcs;
        }

        public IReadOnlyList<TownNpc> Npcs { get; }

        public double HeroX { get; set; }

        public double HeroY { get; set; }
    }

    private sealed class FakeRun : GameFlowTests.FakeHero, IRunSession
    {
        public FakeRun(string path, int run) : base("run " + path + " #" + run)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static GameFlow NewFlow(FakeTown town, bool withShops = true)
    {
        var flow = new GameFlow(() => town, (path, run) => new FakeRun(path, run), new[] { A });
        if (withShops)
        {
            flow.Shop.Use(Book());
            flow.UseShopNpcs(new TownShopNpcs(id => id == 7 ? 3 : id == 8 ? 4 : -1));
        }
        flow.EnterTown();
        return flow;
    }

    [Fact]
    public void TheFlowOpensTheShopOfTheNpcNextToTheHero()
    {
        var town = new FakeTown(new TownNpc(7, 500, 100, 0), new TownNpc(9, 520, 100, 0)) { HeroX = 480, HeroY = 100 };
        GameFlow flow = NewFlow(town);
        Assert.Equal(3, flow.FindNearbyShop());
        Assert.True(flow.TryOpenNearbyShop());
        Assert.True(flow.Shop.Open);
        Assert.Equal(3, flow.Shop.Shop.Index);
        Assert.False(flow.TryOpenNearbyShop());                                   // 已经开着
        Assert.False(flow.OpenShop(3));
    }

    [Fact]
    public void NothingOpensWhenTheHeroIsFarAwayOrTheNpcSellsNothing()
    {
        var town = new FakeTown(new TownNpc(7, 300, 100, 0), new TownNpc(9, 560, 100, 0)) { HeroX = 800, HeroY = 100 };
        GameFlow flow = NewFlow(town);
        Assert.Equal(-1, flow.FindNearbyShop());                                  // 离得远
        Assert.False(flow.TryOpenNearbyShop());
        Assert.False(flow.Shop.Open);
        town.HeroX = 580;                                                         // 紧挨着 9 号:它不开商店,7 号太远(隔着 280,比横向范围 240 还远)
        Assert.Equal(-1, flow.FindNearbyShop());
        Assert.False(flow.TryOpenNearbyShop());
        town.HeroX = 330;                                                         // 走到 7 号旁边
        Assert.Equal(3, flow.FindNearbyShop());
        town.HeroY = 100 + TownShopNpcs.RangeY + 1;                               // 纵深方向差得多,出了椭圆
        Assert.Equal(-1, flow.FindNearbyShop());
    }

    [Fact]
    public void WithoutTheNpcMapOrOutsideTownThereIsNoNearbyShop()
    {
        var town = new FakeTown(new TownNpc(7, 500, 100, 0)) { HeroX = 500, HeroY = 100 };
        GameFlow bare = NewFlow(town, withShops: false);
        Assert.Equal(-1, bare.FindNearbyShop());
        Assert.False(bare.TryOpenNearbyShop());
        Assert.Throws<ArgumentNullException>(() => bare.UseShopNpcs(null));

        var plain = new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { A });
        plain.Shop.Use(Book());
        plain.UseShopNpcs(new TownShopNpcs(id => 3));
        plain.EnterTown();
        Assert.Equal(-1, plain.FindNearbyShop());                                 // 会话没有 NPC 列表
        Assert.True(plain.OpenShop(3));                                           // 直接给编号照样能开
        plain.EnterSelect();
        Assert.Equal(-1, plain.FindNearbyShop());                                 // 不在城镇
        Assert.False(plain.OpenShop(3));
        var notStarted = new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { A });
        Assert.False(notStarted.OpenShop(3));
        Assert.Equal(-1, notStarted.FindNearbyShop());
    }

    [Fact]
    public void TheShopExcludesTheOtherWindowsAndEscClosesItFirst()
    {
        var town = new FakeTown(new TownNpc(7, 500, 100, 0)) { HeroX = 500, HeroY = 100 };
        GameFlow flow = NewFlow(town);
        Assert.True(flow.ToggleBag());
        Assert.False(flow.OpenShop(3));                                           // 背包开着:不能开商店
        flow.ToggleBag();
        Assert.True(flow.ToggleMenu());
        Assert.False(flow.OpenShop(3));                                           // 菜单开着:不能开商店
        flow.ToggleMenu();
        Assert.True(flow.OpenShop(3));
        Assert.False(flow.ToggleBag());                                           // 商店开着:I 键无效
        Assert.False(flow.Bag.Open);
        Assert.False(flow.Poll());                                                // 暂停:不处理阶段结果
        Assert.True(flow.ToggleMenu());                                           // Esc:先关商店,不开菜单
        Assert.False(flow.Shop.Open);
        Assert.False(flow.Menu.Open);
    }

    [Fact]
    public void ChangingTheStageClosesTheShop()
    {
        var town = new FakeTown(new TownNpc(7, 500, 100, 0)) { HeroX = 500, HeroY = 100 };
        GameFlow flow = NewFlow(town);
        Assert.True(flow.OpenShop(3));
        flow.EnterSelect();
        Assert.False(flow.Shop.Open);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        flow.EnterTown();
        Assert.True(flow.OpenShop(4));
        flow.EnterTown();                                                         // 同样是换阶段(回城重造会话)
        Assert.False(flow.Shop.Open);
    }

    // ------------------------------------------------------------------ 商店 NPC 的判定

    [Fact]
    public void ShopOfIsCachedAndNormalisesNegatives()
    {
        int calls = 0;
        var npcs = new TownShopNpcs(id =>
        {
            calls++;
            return id == 1 ? 12 : id == 2 ? -5 : -1;
        });
        Assert.Equal(12, npcs.ShopOf(1));
        Assert.Equal(12, npcs.ShopOf(1));
        Assert.Equal(-1, npcs.ShopOf(2));                                         // 负数一律当没有商店
        Assert.Equal(-1, npcs.ShopOf(2));
        Assert.Equal(2, calls);
        Assert.Throws<ArgumentNullException>(() => new TownShopNpcs(null));
        Assert.Throws<ArgumentNullException>(() => TownShopNpcs.FromAssets(null));
    }

    [Fact]
    public void TheNearestShopNpcInsideTheEllipseWins()
    {
        var npcs = new TownShopNpcs(id => id >= 10 ? id : -1);
        var list = new List<TownNpc>
        {
            new TownNpc(5, 1000, 100, 0),                                          // 没有商店,但最近
            new TownNpc(11, 1060, 100, 0),                                         // 相距 60
            new TownNpc(12, 1040, 100, 0),                                         // 相距 40:更近
        };
        Assert.True(npcs.TryFindNear(list, 1000, 100, out TownNpc npc, out int shop));
        Assert.Equal((12, 12), (npc.Id, shop));
        // 纵深方向的范围比横向小:同样的 60 个像素在纵深上已经出了椭圆
        var far = new List<TownNpc> { new TownNpc(11, 1000, 100 + TownShopNpcs.RangeY + 1, 0) };
        Assert.False(npcs.TryFindNear(far, 1000, 100, out _, out int none));
        Assert.Equal(-1, none);
        var edgeX = new List<TownNpc> { new TownNpc(11, 1000 + TownShopNpcs.RangeX, 100, 0) };
        Assert.True(npcs.TryFindNear(edgeX, 1000, 100, out _, out _));              // 刚好在椭圆上算范围内
        var outX = new List<TownNpc> { new TownNpc(11, 1000 + TownShopNpcs.RangeX + 1, 100, 0) };
        Assert.False(npcs.TryFindNear(outX, 1000, 100, out _, out _));
        // 椭圆不是矩形:横向和纵深各在边上的位置合起来就出去了
        var corner = new List<TownNpc> { new TownNpc(11, 1000 + TownShopNpcs.RangeX, 100 + TownShopNpcs.RangeY, 0) };
        Assert.False(npcs.TryFindNear(corner, 1000, 100, out _, out _));
        Assert.False(npcs.TryFindNear(null, 0, 0, out _, out _));
        Assert.False(npcs.TryFindNear(new List<TownNpc>(), 0, 0, out _, out _));
    }

    [Fact]
    public void NpcsWhoseShopCannotBeReadAreTreatedAsHavingNoShop()
    {
        var npcs = new TownShopNpcs(id => id, shop => shop != 12);
        Assert.Equal(11, npcs.ShopOf(11));
        Assert.Equal(-1, npcs.ShopOf(12));                                        // 角色里写了 12 号店,可是这家店读不出来
        var list = new List<TownNpc> { new TownNpc(12, 1000, 100, 0), new TownNpc(11, 1100, 100, 0) };
        Assert.True(npcs.TryFindNear(list, 1000, 100, out TownNpc npc, out int shop));
        Assert.Equal((11, 11), (npc.Id, shop));                                   // 12 号虽然更近,但不算商店 NPC
        var unchecked_ = new TownShopNpcs(id => id);
        Assert.Equal(12, unchecked_.ShopOf(12));                                  // 不给核对函数就不核对
        Assert.Equal(-1, new TownShopNpcs(id => -5, shop => true).ShopOf(1));     // 负数照旧是"没有商店"
    }

    // ------------------------------------------------------------------ 给宿主用的几个数

    [Fact]
    public void TheHostCanReadTheOpenShopsNumbersAndFindTheFirstStockedShop()
    {
        var w = new ShopWindow();
        Assert.Equal(-1, w.FirstStockedShop());                                   // 没接数据本
        Assert.Equal((-1, 0), (w.ShopIndex, w.ShopItemCount));
        w.Use(Book());
        Assert.Equal(3, w.FirstStockedShop());                                    // 商店 3 排在最前面而且有商品
        Assert.Equal((-1, 0), (w.ShopIndex, w.ShopItemCount));                    // 还没打开
        Assert.True(w.Show(GameStage.Town, 3));
        Assert.Equal((3, 5), (w.ShopIndex, w.ShopItemCount));                     // 第一页 4 件、第二页 1 件(空格不数)
        w.Close();
        Assert.Equal((-1, 0), (w.ShopIndex, w.ShopItemCount));
        Assert.True(w.Show(GameStage.Town, 4));
        Assert.Equal((4, 0), (w.ShopIndex, w.ShopItemCount));
    }

    [Fact]
    public void TheStockedShopSearchSkipsShopsWithoutGoods()
    {
        var shops = new Dictionary<int, ItemShopDef>
        {
            [1] = new ItemShopDef(1, "a", 1, null, null, new ItemShopTab[0]),
            [2] = new ItemShopDef(2, "b", 1, null, null, new[] { new ItemShopTab(new[] { -1, -1 }, null) }),
            [5] = new ItemShopDef(5, "c", 1, null, null, new[] { new ItemShopTab(new[] { -1, 4 }, null) }),
        };
        var w = new ShopWindow();
        w.Use(new ShopBook(i => shops.TryGetValue(i, out ItemShopDef s) ? s : null, c => Stack(c), null, () => new[] { 1, 2, 5 }));
        Assert.Equal(5, w.FirstStockedShop());
        w.Use(new ShopBook(i => null, c => null));
        Assert.Equal(-1, w.FirstStockedShop());                                   // 没给编号表:找不到
    }

    [Fact]
    public void TheFlowCountsTheTownNpcsThatRunShops()
    {
        var town = new FakeTown(new TownNpc(7, 500, 100, 0), new TownNpc(8, 600, 100, 0), new TownNpc(9, 700, 100, 0)) { HeroX = 0, HeroY = 0 };
        GameFlow flow = NewFlow(town);
        Assert.Equal(2, flow.CountShopNpcs(out int npcs));
        Assert.Equal(3, npcs);

        GameFlow bare = NewFlow(town, withShops: false);
        Assert.Equal(0, bare.CountShopNpcs(out int bareNpcs));                    // 没接对应:没有商店 NPC,但 NPC 总数照给
        Assert.Equal(3, bareNpcs);

        var plain = new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { A });
        plain.UseShopNpcs(new TownShopNpcs(id => 3));
        plain.EnterTown();
        Assert.Equal(0, plain.CountShopNpcs(out int plainNpcs));                  // 会话没有 NPC 列表
        Assert.Equal(0, plainNpcs);
        plain.EnterSelect();
        Assert.Equal(0, plain.CountShopNpcs(out int selectNpcs));                 // 不在城镇
        Assert.Equal(0, selectNpcs);
    }

    [Fact]
    public void TheNearHintIsACenteredDarkBarWithOneLineOfText()
    {
        List<SceneSprite> sprites = ShopWindowArt.BuildNearHint(new OverlayArt());
        Assert.Equal(2, sprites.Count);
        SceneSprite bar = sprites.Single(s => s.AnimationPath == "shop-near-bg");
        SceneSprite text = sprites.Single(s => s.AnimationPath == "shop-near-hint");
        Assert.Equal(ShopWindowArt.NearHintAlpha, bar.TintAlpha);
        Assert.Equal(ShopWindowArt.NearHintY, bar.ScreenY);
        Assert.True(Math.Abs(2 * bar.ScreenX + bar.Frame.Width - ShopWindow.ScreenWidth) <= 1);       // 水平居中
        Assert.True(text.ScreenX >= bar.ScreenX && text.ScreenX + text.Frame.Width <= bar.ScreenX + bar.Frame.Width);
        Assert.True(text.ScreenY >= bar.ScreenY && text.ScreenY + text.Frame.Height <= bar.ScreenY + bar.Frame.Height);
        Assert.True(text.SortKey > bar.SortKey);
        Assert.True(bar.ScreenY + bar.Frame.Height < ShopWindow.ScreenHeight);
    }

    // ------------------------------------------------------------------ 素材桩

    private sealed class StubSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            return Frames.TryGetValue((imageName, frame), out sprite);
        }
    }

    private static SpriteFrame Opaque(int w, int h)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = 255;
        }
        return new SpriteFrame(w, h, 0, 0, w, h, rgba);
    }
}
