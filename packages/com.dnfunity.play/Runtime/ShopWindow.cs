using System;
using System.Collections.Generic;
using Dnf.Game;

// 证据:E-119(买卖的规则与价格来自服务端静态读码:Store::user_buy_item、Store::user_sell_item、getItemSellPrice;窗口的版式是本项目自己的设计)。
namespace Dnf.Play
{
    /// <summary>商店窗口的两种模式。</summary>
    public enum ShopMode
    {
        /// <summary>买入:格子是这家店 <c>[sell item]</c> 里的商品(每页 7 列,行数不限,超过一屏时滚动)。</summary>
        Buy = 0,

        /// <summary>出售:格子是背包某一页的物品(8 列 × 7 行,和背包窗口一样)。</summary>
        Sell = 1,
    }

    /// <summary>数量输入框的种类(<see cref="ShopWindow.PromptKind"/>)。</summary>
    public enum ShopPromptKind
    {
        None = 0,
        Buy = 1,
        Sell = 2,
    }

    /// <summary>
    /// NPC 商店窗口(城镇里走到商店 NPC 旁按空格打开):左边页签与物品格子,右边买入/出售切换和光标处物品的价格,底下几行说明。
    /// 方向键移动光标,Q、E 换页,Tab 在买入与出售之间切换,回车买入或卖出(可叠加物品先弹数量输入框:上下 ±1、左右 ±10,回车确认,Esc 取消),
    /// Esc 关上;鼠标点模式按钮、页签、格子(点已选中的格子再点一下等于回车)、数量框的按钮。
    /// 开着的时候游戏暂停(宿主负责,见 <see cref="GameFlow.Shop"/>)。
    /// <b>本项目自己的占位设计</b>:原版商店窗口的界面没有读到(客户端界面脚本里有它的定义,还没解);文字是占位英文,物品名要等中文文字系统。
    /// 窗口只管状态、判定与点击判定,画面由 <see cref="ShopWindowArt"/> 画,物品与商店数据在 <see cref="ShopBook"/> 里,钱与背包在 <see cref="HeroProgress"/> 里。
    /// </summary>
    public sealed class ShopWindow
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        /// <summary>窗口的位置与大小(画布像素,居中,和背包窗口同一块)。</summary>
        public const int WindowX = 98;
        public const int WindowY = 30;
        public const int WindowWidth = 604;
        public const int WindowHeight = 540;

        /// <summary>标题条的高。</summary>
        public const int TitleHeight = 44;

        /// <summary>买入页的列数(<see cref="ItemShopDef.Columns"/>)、出售页的列数(和背包一样)、一屏看到的行数。</summary>
        public const int BuyColumns = ItemShopDef.Columns;
        public const int SellColumns = 8;
        public const int VisibleRows = 7;

        /// <summary>物品格子的边长与间隔,以及第一格左上角。</summary>
        public const int CellSize = 48;
        public const int CellGap = 4;
        public const int GridX = 114;
        public const int GridY = 124;

        /// <summary>页签条:第一个页签的左上角、高、间隔,以及买入页与出售页各自的页签宽。</summary>
        public const int TabX = 114;
        public const int TabY = 86;
        public const int TabHeight = 28;
        public const int TabGap = 6;
        public const int BuyTabWidth = 76;
        public const int SellTabWidth = 96;

        /// <summary>右边的模式按钮(买入、出售)与光标处物品的详情栏。</summary>
        public const int ModeX = 548;
        public const int ModeY = 86;
        public const int ModeWidth = 66;
        public const int ModeHeight = 28;
        public const int ModeGap = 6;
        public const int DetailX = 548;
        public const int DetailY = 124;
        public const int DetailWidth = 138;

        /// <summary>数量输入框的位置与大小,以及五个按钮(减一、加一、最大、确定、取消)的宽。</summary>
        public const int PromptX = 238;
        public const int PromptY = 224;
        public const int PromptWidth = 324;
        public const int PromptHeight = 152;
        public const int PromptButtonHeight = 32;
        public const int PromptButtonGap = 6;

        private static readonly int[] PromptButtonWidths = { 44, 44, 56, 56, 84 };

        /// <summary>数量框按钮的序号。</summary>
        public const int ButtonMinus = 0;
        public const int ButtonPlus = 1;
        public const int ButtonMax = 2;
        public const int ButtonOk = 3;
        public const int ButtonCancel = 4;

        private ShopBook _book;

        /// <summary>开着。</summary>
        public bool Open { get; private set; }

        /// <summary>现在开着的(或最近打开过的)商店;一次也没打开过为 null。</summary>
        public ItemShopDef Shop { get; private set; }

        public ShopMode Mode { get; private set; }

        /// <summary>买入模式的页签序号。</summary>
        public int ShopTab { get; private set; }

        /// <summary>出售模式的页签(背包的页)。</summary>
        public InventoryTab SellTab { get; private set; }

        /// <summary>
        /// 格子上的光标。买入模式是这一页里的格子序号(从 0 起,按 <see cref="BuyColumns"/> 列一行排,空格子上也可以停);
        /// 出售模式是背包这一页的格子序号(0 到 55)。
        /// </summary>
        public int Cursor { get; private set; }

        /// <summary>买入模式一屏最上面一行是第几行(光标出了屏就跟着滚)。</summary>
        public int ScrollRow { get; private set; }

        /// <summary>最近一次操作的结果文字(占位英文;显示在窗口底部)。</summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>最近一次买卖的结果(没买卖过是 <see cref="ShopResult.Ok"/>;日志与探针用)。</summary>
        public ShopResult LastResult { get; private set; }

        /// <summary>数量输入框:没开是 <see cref="ShopPromptKind.None"/>。开着的时候方向键与回车、Esc 都只管这个框。</summary>
        public ShopPromptKind PromptKind { get; private set; }

        public ItemInfo PromptItem { get; private set; }

        public int PromptCount { get; private set; }

        public int PromptMax { get; private set; }

        private InventoryTab _promptTab;
        private int _promptIndex;

        /// <summary>累计(窗口对象一直在,不随每次打开清零)成功买入的次数、件数与花掉的钱。</summary>
        public int Purchases { get; private set; }

        public int ItemsBought { get; private set; }

        public long Spent { get; private set; }

        /// <summary>累计成功卖出的次数、件数与卖得的钱。</summary>
        public int Sales { get; private set; }

        public int ItemsSold { get; private set; }

        public long Earned { get; private set; }

        public bool IsPromptOpen => PromptKind != ShopPromptKind.None;

        /// <summary>接上数据本(商店、物品、价格倍率);没接就打不开。</summary>
        public ShopBook Book => _book;

        /// <summary>现在开着的商店在 <c>itemshop.lst</c> 里的编号(没开是 −1;宿主的日志用,宿主不直接碰 Game 层的类型)。</summary>
        public int ShopIndex => Open && Shop != null ? Shop.Index : -1;

        /// <summary>现在开着的商店一共有几件商品(不数空格;没开是 0)。</summary>
        public int ShopItemCount => Open && Shop != null ? Shop.ItemCount : 0;

        /// <summary>数据本里第一家有商品的商店的编号(探针用,免得去走到某个 NPC 旁边);没接数据本或没有这样的店返回 −1。</summary>
        public int FirstStockedShop()
        {
            ItemShopDef def = _book?.FirstShopWithItems();
            return def == null ? -1 : def.Index;
        }

        public void Use(ShopBook book)
        {
            _book = book ?? throw new ArgumentNullException(nameof(book));
        }

        /// <summary>
        /// 用游戏素材接上数据本(<see cref="ShopBook.FromData"/>:商店目录、物品目录、价格倍率表,都按需读)。
        /// 宿主(客户端)只认识渲染层的类型,所以从这里接,不直接碰 Game 层的 <see cref="ShopBook"/>。
        /// </summary>
        public void UseAssets(Dnf.Render.DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            Use(ShopBook.FromData(new GameData(new Dnf.Script.PvfScriptSource(assets.Pvf))));
        }

        /// <summary>
        /// 按阶段与商店编号(NPC 的 <c>[item shop]</c> 参数,<c>itemshop.lst</c> 里的编号)打开:只有城镇里有商店窗口;
        /// 没接数据本或编号读不出商店时不打开。返回有没有打开。每次打开都回到买入模式、第一页、光标在第一格。
        /// </summary>
        public bool Show(GameStage stage, int shopIndex)
        {
            if (stage != GameStage.Town || _book == null)
            {
                return false;
            }
            ItemShopDef shop = _book.Shop(shopIndex);
            if (shop == null)
            {
                return false;
            }
            Shop = shop;
            Open = true;
            Mode = ShopMode.Buy;
            ShopTab = 0;
            SellTab = InventoryTab.Equipment;
            Cursor = 0;
            ScrollRow = 0;
            Message = string.Empty;
            CancelPrompt();
            return true;
        }

        public void Close()
        {
            Open = false;
            CancelPrompt();
        }

        // ------------------------------------------------------------------ 版面(画与点击判定共用)

        /// <summary>买入模式一屏里第 <paramref name="visibleIndex"/> 个格子(0 到 48,从屏上最上面一行数起)的矩形(x、y、宽、高)。</summary>
        public static (int X, int Y, int W, int H) BuyCellRect(int visibleIndex)
        {
            int col = visibleIndex % BuyColumns, row = visibleIndex / BuyColumns;
            return (GridX + col * (CellSize + CellGap), GridY + row * (CellSize + CellGap), CellSize, CellSize);
        }

        /// <summary>出售模式第 <paramref name="index"/> 个格子(0 到 55)的矩形。</summary>
        public static (int X, int Y, int W, int H) SellCellRect(int index)
        {
            int col = index % SellColumns, row = index / SellColumns;
            return (GridX + col * (CellSize + CellGap), GridY + row * (CellSize + CellGap), CellSize, CellSize);
        }

        /// <summary>模式按钮的矩形。</summary>
        public static (int X, int Y, int W, int H) ModeRect(ShopMode mode)
        {
            return (ModeX + (int)mode * (ModeWidth + ModeGap), ModeY, ModeWidth, ModeHeight);
        }

        /// <summary>第 <paramref name="index"/> 个页签的矩形(买入与出售页签宽不同)。</summary>
        public static (int X, int Y, int W, int H) TabRect(ShopMode mode, int index)
        {
            int w = mode == ShopMode.Buy ? BuyTabWidth : SellTabWidth;
            return (TabX + index * (w + TabGap), TabY, w, TabHeight);
        }

        /// <summary>数量框第 <paramref name="index"/> 个按钮(<see cref="ButtonMinus"/> 起)的矩形。</summary>
        public static (int X, int Y, int W, int H) PromptButtonRect(int index)
        {
            int x = PromptX + 8;
            for (int i = 0; i < index; i++)
            {
                x += PromptButtonWidths[i] + PromptButtonGap;
            }
            return (x, PromptY + PromptHeight - PromptButtonHeight - 10, PromptButtonWidths[index], PromptButtonHeight);
        }

        private static bool Inside((int X, int Y, int W, int H) r, int x, int y)
        {
            return x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H;
        }

        /// <summary>点在数量框的哪个按钮上(没点中返回 −1)。</summary>
        public static int HitPromptButton(int x, int y)
        {
            for (int i = 0; i < PromptButtonWidths.Length; i++)
            {
                if (Inside(PromptButtonRect(i), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>点在哪个模式按钮上(没点中返回 −1)。</summary>
        public static int HitMode(int x, int y)
        {
            for (int i = 0; i < 2; i++)
            {
                if (Inside(ModeRect((ShopMode)i), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>点在哪个页签上(没点中返回 −1);<paramref name="count"/> 是这个模式的页签数。</summary>
        public static int HitTab(ShopMode mode, int count, int x, int y)
        {
            for (int i = 0; i < count; i++)
            {
                if (Inside(TabRect(mode, i), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        // ------------------------------------------------------------------ 状态

        /// <summary>买入模式当前页(没有页时是 null,格子全是空的)。</summary>
        public ItemShopTab CurrentTab => Shop != null && ShopTab < Shop.Tabs.Count ? Shop.Tabs[ShopTab] : null;

        /// <summary>当前模式的页签数:买入是商店的页数(至少 1,没有页的店也画一页空格),出售是背包的 4 页。</summary>
        public int TabCount => Mode == ShopMode.Sell ? Inventory.TabCount : Shop == null ? 0 : Math.Max(1, Shop.Tabs.Count);

        /// <summary>当前模式的列数。</summary>
        public int Columns => Mode == ShopMode.Sell ? SellColumns : BuyColumns;

        /// <summary>买入页的行数(至少 <see cref="VisibleRows"/> 行,商品更多就取实际行数)。</summary>
        public int BuyRows => Math.Max(VisibleRows, CurrentTab == null ? 0 : CurrentTab.RowCount);

        /// <summary>当前模式下光标能到的格子总数:买入是整行补满的格子数,出售是 <see cref="Inventory.SlotsPerTab"/>。</summary>
        public int CellTotal => Mode == ShopMode.Sell ? Inventory.SlotsPerTab : BuyRows * BuyColumns;

        /// <summary>买入模式:光标处的商品编号(空格子是 −1)。</summary>
        public int CodeAtCursor => Mode == ShopMode.Buy && CurrentTab != null ? CurrentTab.CodeAt(Cursor) : -1;

        /// <summary>光标指着的物品:买入模式是商品(经数据本取),出售模式是背包这一格的物品;没有返回 null。</summary>
        public ItemInfo ItemAtCursor(HeroProgress progress)
        {
            return ItemAtCell(Cursor, progress);
        }

        /// <summary>第 <paramref name="cell"/> 格(当前模式的格子序号)上的物品;没有返回 null。</summary>
        public ItemInfo ItemAtCell(int cell, HeroProgress progress)
        {
            if (Mode == ShopMode.Buy)
            {
                int code = CurrentTab == null ? -1 : CurrentTab.CodeAt(cell);
                return code < 0 || _book == null ? null : _book.Item(code);
            }
            if (progress == null)
            {
                return null;
            }
            IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(SellTab);
            return (uint)cell < (uint)stacks.Count ? stacks[cell].Info : null;
        }

        /// <summary>数量框里这一笔的总价(买入是花的钱,出售是得到的钱);框没开是 0。</summary>
        public long PromptTotal
        {
            get
            {
                switch (PromptKind)
                {
                    case ShopPromptKind.Buy:
                        return ItemPricing.BuyPrice(PromptItem, PromptCount);
                    case ShopPromptKind.Sell:
                        return ItemPricing.SellPrice(PromptItem, PromptCount, _book?.Rates);
                    default:
                        return 0;
                }
            }
        }

        /// <summary>数量框里的单价(买入是 <c>[price]</c>,出售是一件的收购价);框没开是 0。</summary>
        public long PromptUnit
        {
            get
            {
                switch (PromptKind)
                {
                    case ShopPromptKind.Buy:
                        return ItemPricing.BuyPrice(PromptItem, 1);
                    case ShopPromptKind.Sell:
                        return ItemPricing.SellPrice(PromptItem, 1, _book?.Rates);
                    default:
                        return 0;
                }
            }
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>切换买入与出售(光标回到第一格、清掉提示与数量框)。</summary>
        public void ToggleMode()
        {
            SetMode(Mode == ShopMode.Buy ? ShopMode.Sell : ShopMode.Buy);
        }

        public void SetMode(ShopMode mode)
        {
            if (!Open)
            {
                return;
            }
            CancelPrompt();
            if (mode != Mode)
            {
                Mode = mode;
                Cursor = 0;
                ScrollRow = 0;
            }
            Message = string.Empty;
        }

        /// <summary>
        /// 移动光标:在格子里夹在边界内(买入模式的格子行数不限,光标出了屏就滚);数量框开着时改成调数量(上下 ±1、左右 ±10,注意屏幕方向:向上是加)。
        /// </summary>
        public void Move(int dx, int dy)
        {
            if (!Open)
            {
                return;
            }
            if (IsPromptOpen)
            {
                PromptAdjust(dx * 10 - dy);
                return;
            }
            int columns = Columns;
            int rows = CellTotal / columns;
            int c = Cursor % columns, r = Cursor / columns;
            c = Clamp(c + dx, 0, columns - 1);
            r = Clamp(r + dy, 0, rows - 1);
            Cursor = r * columns + c;
            FollowCursor();
        }

        /// <summary>买入模式:让光标所在的行落在一屏之内。</summary>
        private void FollowCursor()
        {
            if (Mode != ShopMode.Buy)
            {
                ScrollRow = 0;
                return;
            }
            int row = Cursor / BuyColumns;
            if (row < ScrollRow)
            {
                ScrollRow = row;
            }
            else if (row >= ScrollRow + VisibleRows)
            {
                ScrollRow = row - VisibleRows + 1;
            }
            ScrollRow = Clamp(ScrollRow, 0, Math.Max(0, BuyRows - VisibleRows));
        }

        /// <summary>换页(+1 下一页、−1 上一页,循环);光标回到第一格。</summary>
        public void NextTab(int delta)
        {
            if (!Open)
            {
                return;
            }
            CancelPrompt();
            int count = TabCount;
            if (count <= 0)
            {
                return;
            }
            int t = ((Mode == ShopMode.Sell ? (int)SellTab : ShopTab) + delta) % count;
            if (t < 0)
            {
                t += count;
            }
            SelectTab(t);
        }

        private void SelectTab(int tab)
        {
            if (Mode == ShopMode.Sell)
            {
                SellTab = (InventoryTab)Clamp(tab, 0, Inventory.TabCount - 1);
            }
            else
            {
                ShopTab = Clamp(tab, 0, Math.Max(0, TabCount - 1));
            }
            Cursor = 0;
            ScrollRow = 0;
        }

        /// <summary>
        /// 回车。数量框开着就确认(买或卖那一笔);否则:买入模式买光标处的商品(装备买 1 件,可叠加物品弹数量框,钱买不起一件直接提示),
        /// 出售模式卖光标处的物品(装备、只剩一件的物品直接卖,多件的弹数量框)。返回这一次有没有真的成交。
        /// </summary>
        public bool Activate(HeroProgress progress)
        {
            if (!Open || progress == null)
            {
                return false;
            }
            if (IsPromptOpen)
            {
                return PromptConfirm(progress);
            }
            return Mode == ShopMode.Buy ? ActivateBuy(progress) : ActivateSell(progress);
        }

        private bool ActivateBuy(HeroProgress progress)
        {
            int code = CodeAtCursor;
            if (code < 0)
            {
                Message = ShopRules.MessageOf(ShopResult.NoItem);
                return false;
            }
            ItemInfo item = _book.Item(code);
            if (item == null)
            {
                Message = ShopRules.MessageOf(ShopResult.NoItem);
                return false;
            }
            int max = ShopRules.MaxBuyable(item, progress.Gold);
            if (item.IsEquipment || max <= 1)
            {
                // 装备一件一件买;可叠加物品钱只够一件(或一件也买不起)也不必弹框,买不起时由规则给出提示
                return ExecuteBuy(progress, item, 1);
            }
            OpenPrompt(ShopPromptKind.Buy, item, max, InventoryTab.Equipment, 0);
            return false;
        }

        private bool ActivateSell(HeroProgress progress)
        {
            IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(SellTab);
            if ((uint)Cursor >= (uint)stacks.Count)
            {
                Message = ShopRules.MessageOf(ShopResult.NoItem);
                return false;
            }
            ItemStack stack = stacks[Cursor];
            if (!ShopRules.IsSellable(stack.Info))
            {
                LastResult = ShopResult.NotSellable;
                Message = ShopRules.MessageOf(ShopResult.NotSellable);
                return false;
            }
            if (stack.Info.IsEquipment || stack.Count <= 1)
            {
                return ExecuteSell(progress, SellTab, Cursor, 1);
            }
            OpenPrompt(ShopPromptKind.Sell, stack.Info, stack.Count, SellTab, Cursor);
            return false;
        }

        private void OpenPrompt(ShopPromptKind kind, ItemInfo item, int max, InventoryTab tab, int index)
        {
            PromptKind = kind;
            PromptItem = item;
            PromptMax = max;
            PromptCount = 1;
            _promptTab = tab;
            _promptIndex = index;
            Message = string.Empty;
        }

        /// <summary>数量框里加减 <paramref name="delta"/> 件(夹在 1 到上限之间);框没开什么也不做。</summary>
        public void PromptAdjust(int delta)
        {
            if (!IsPromptOpen)
            {
                return;
            }
            PromptCount = Clamp(PromptCount + delta, 1, Math.Max(1, PromptMax));
        }

        /// <summary>数量框里直接取最大值。</summary>
        public void PromptSetMax()
        {
            if (IsPromptOpen)
            {
                PromptCount = Math.Max(1, PromptMax);
            }
        }

        /// <summary>确认数量框里的这一笔;返回有没有真的成交。框没开返回 false。</summary>
        public bool PromptConfirm(HeroProgress progress)
        {
            if (!IsPromptOpen || progress == null)
            {
                return false;
            }
            ShopPromptKind kind = PromptKind;
            ItemInfo item = PromptItem;
            int count = PromptCount;
            InventoryTab tab = _promptTab;
            int index = _promptIndex;
            CancelPrompt();
            return kind == ShopPromptKind.Buy ? ExecuteBuy(progress, item, count) : ExecuteSell(progress, tab, index, count);
        }

        /// <summary>关掉数量框(不成交)。</summary>
        public void CancelPrompt()
        {
            PromptKind = ShopPromptKind.None;
            PromptItem = null;
            PromptCount = 0;
            PromptMax = 0;
        }

        private bool ExecuteBuy(HeroProgress progress, ItemInfo item, int count)
        {
            long before = progress.Gold;
            ShopResult result = progress.Buy(Shop, item, count);
            LastResult = result;
            if (result != ShopResult.Ok)
            {
                Message = ShopRules.MessageOf(result);
                return false;
            }
            int n = item.IsEquipment ? 1 : count;
            Purchases++;
            ItemsBought += n;
            Spent += before - progress.Gold;
            Message = "BOUGHT " + n + " FOR " + (before - progress.Gold);
            return true;
        }

        private bool ExecuteSell(HeroProgress progress, InventoryTab tab, int index, int count)
        {
            ItemStack stack = (uint)index < (uint)progress.Bag.Tab(tab).Count ? progress.Bag.Tab(tab)[index] : null;
            int n = stack != null && stack.Info.IsEquipment ? 1 : count;
            ShopResult result = progress.Sell(tab, index, count, _book?.Rates, out long earned);
            LastResult = result;
            if (result != ShopResult.Ok)
            {
                Message = ShopRules.MessageOf(result);
                return false;
            }
            Sales++;
            ItemsSold += n;
            Earned += earned;
            Message = "SOLD " + n + " FOR " + earned;
            return true;
        }

        /// <summary>
        /// 鼠标点击(画布坐标):数量框开着时只认框里的按钮(点别处被吞掉);否则点模式按钮切换模式,点页签换页,
        /// 点格子先选中、再点一下已选中的等于回车。返回点中的是不是窗口里的某个控件(点在别处返回 false,窗口不关)。
        /// </summary>
        public bool Click(int x, int y, HeroProgress progress)
        {
            if (!Open)
            {
                return false;
            }
            if (IsPromptOpen)
            {
                switch (HitPromptButton(x, y))
                {
                    case ButtonMinus:
                        PromptAdjust(-1);
                        break;
                    case ButtonPlus:
                        PromptAdjust(1);
                        break;
                    case ButtonMax:
                        PromptSetMax();
                        break;
                    case ButtonOk:
                        PromptConfirm(progress);
                        break;
                    case ButtonCancel:
                        CancelPrompt();
                        break;
                }
                return true;
            }
            int mode = HitMode(x, y);
            if (mode >= 0)
            {
                SetMode((ShopMode)mode);
                return true;
            }
            int tab = HitTab(Mode, TabCount, x, y);
            if (tab >= 0)
            {
                SelectTab(tab);
                Message = string.Empty;
                return true;
            }
            int cell = CellAt(x, y);
            if (cell >= 0)
            {
                bool again = Cursor == cell;
                Cursor = cell;
                FollowCursor();
                if (again)
                {
                    Activate(progress);
                }
                return true;
            }
            return false;
        }

        /// <summary>画布上的点落在当前模式的哪个格子里(返回格子序号,买入模式含滚动);不在任何格子上返回 −1。</summary>
        public int CellAt(int x, int y)
        {
            if (Mode == ShopMode.Sell)
            {
                for (int i = 0; i < Inventory.SlotsPerTab; i++)
                {
                    if (Inside(SellCellRect(i), x, y))
                    {
                        return i;
                    }
                }
                return -1;
            }
            for (int v = 0; v < VisibleRows * BuyColumns; v++)
            {
                if (Inside(BuyCellRect(v), x, y))
                {
                    return ScrollRow * BuyColumns + v;
                }
            }
            return -1;
        }

        /// <summary>
        /// 无人值守地买卖一遍(宿主的探针用):买入模式从第一页第一格起找第一件买得起的商品,买 1 件;再切到出售,
        /// 把刚买的那件卖掉(卖不了的东西也算试过)。返回 (买了吗, 卖了吗);窗口没开或没有进度什么也不做。
        /// </summary>
        public (bool Bought, bool Sold) TryTradeAny(HeroProgress progress, out int tried)
        {
            tried = 0;
            if (!Open || progress == null)
            {
                return (false, false);
            }
            SetMode(ShopMode.Buy);
            bool bought = false;
            int boughtCode = -1;
            int tabs = TabCount;
            for (int t = 0; t < tabs && !bought; t++)
            {
                SelectTab(t);
                ItemShopTab tab = CurrentTab;
                if (tab == null)
                {
                    continue;
                }
                for (int i = 0; i < tab.Cells.Count && !bought; i++)
                {
                    int code = tab.CodeAt(i);
                    if (code < 0)
                    {
                        continue;
                    }
                    tried++;
                    ItemInfo item = _book.Item(code);
                    if (item == null || ShopRules.CheckBuy(Shop, item, 1, progress.Gold, progress.Bag) != ShopResult.Ok)
                    {
                        continue;
                    }
                    Cursor = i;
                    FollowCursor();
                    if (ExecuteBuy(progress, item, 1))
                    {
                        bought = true;
                        boughtCode = code;
                    }
                }
            }
            bool sold = false;
            if (bought)
            {
                SetMode(ShopMode.Sell);
                ItemInfo item = _book.Item(boughtCode);
                InventoryTab tab = Inventory.TabOf(item);
                SelectTab((int)tab);
                IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(tab);
                for (int i = 0; i < stacks.Count && !sold; i++)
                {
                    if (stacks[i].Info.Code != boughtCode)
                    {
                        continue;
                    }
                    Cursor = i;
                    sold = ExecuteSell(progress, tab, i, 1);
                    break;
                }
            }
            return (bought, sold);
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
