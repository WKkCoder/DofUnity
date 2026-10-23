using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 商店窗口(<see cref="ShopWindow"/>)的画法:整屏压暗、居中的深色面板带一圈边、标题与金币;左边页签与 7 × 7(买入)或 8 × 7(出售)的物品格子,
    /// 买入页商品多于一屏时右侧带一条滚动条;右边买入/出售按钮和光标处物品的详情;底下三行说明;数量框开着时再压暗一层、中间弹出输入框。
    /// 图标取物品脚本 <c>[icon]</c> 指的那一帧(素材库里没有就画一个按稀有度着色的小方块,与背包窗口共用 <see cref="BagWindowArt"/> 的画法),
    /// 买不起的商品与商店不收的物品盖一层半透明的黑。<b>本项目自己的占位版式</b>(原版商店界面没有读到),文字是占位英文,不依赖图包里的界面素材。
    /// </summary>
    public static class ShopWindowArt
    {
        /// <summary>压暗的不透明度(0 到 255);数量框开着时在窗口上再盖一层。</summary>
        public const int DimAlpha = 150;
        public const int PromptDimAlpha = 120;

        /// <summary>格子上盖的黑色的不透明度(买不起的商品、商店不收的物品)。</summary>
        public const int UnusableAlpha = 150;

        /// <summary>走到商店 NPC 旁的提示条:画布 y 与底色不透明度。</summary>
        public const int NearHintY = 452;
        public const int NearHintAlpha = 210;

        public const uint ButtonColor = 0x2A2A38u;
        public const uint ButtonSelectedColor = 0x7A3A10u;
        public const uint TrackColor = 0x2C2C44u;
        public const uint ThumbColor = 0x8A7A5Au;

        /// <summary>说明行(每行一个 y,画布像素;和背包窗口同一组)。</summary>
        public const int InfoY = BagWindowArt.InfoY;
        public const int MessageY = BagWindowArt.MessageY;
        public const int HintY = BagWindowArt.HintY;

        /// <summary>
        /// 把窗口的精灵追加到 <paramref name="into"/>,<paramref name="order"/> 逐个加一;窗口没开时什么也不加。
        /// <paramref name="sprites"/> 取物品图标(可为 null,那就全用色块)。
        /// 标签:<c>shop-dim</c>、<c>shop-frame</c>、<c>shop-panel</c>、<c>shop-title-bar</c>、<c>shop-title</c>、<c>shop-gold</c>、
        /// <c>shop-mode</c>/<c>shop-mode-selected</c>/<c>shop-mode-label</c>、<c>shop-tab</c>/<c>shop-tab-selected</c>/<c>shop-tab-label</c>、
        /// <c>shop-cell</c>/<c>shop-cell-selected</c>、<c>shop-icon-dim</c>、<c>shop-count</c>、<c>shop-track</c>/<c>shop-thumb</c>、
        /// <c>shop-detail</c>/<c>shop-detail-slot</c>/<c>shop-detail-line</c>、<c>shop-info</c>、<c>shop-message</c>、<c>shop-hint</c>、
        /// 数量框:<c>shop-prompt-dim</c>、<c>shop-prompt-frame</c>、<c>shop-prompt-panel</c>、<c>shop-prompt-title</c>、<c>shop-prompt-count</c>、
        /// <c>shop-prompt-price</c>、<c>shop-prompt-button</c>/<c>shop-prompt-label</c>。
        /// (图标与稀有度条沿用背包窗口的标签 <c>bag-icon</c>、<c>bag-icon-block</c>、<c>bag-rarity</c>。)
        /// </summary>
        public static void Build(List<SceneSprite> into, ref int order, ShopWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art)
        {
            if (window == null || !window.Open)
            {
                return;
            }
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            int x = ShopWindow.WindowX, y = ShopWindow.WindowY, w = ShopWindow.WindowWidth, h = ShopWindow.WindowHeight;
            SpriteFrame black = art.Solid(ShopWindow.ScreenWidth, ShopWindow.ScreenHeight, OverlayArt.Black);
            into.Add(OverlayArt.PlaceDim(black, DimAlpha, order++, "shop-dim"));
            into.Add(OverlayArt.Place(art.Solid(w + 4, h + 4, BagWindowArt.FrameColor), x - 2, y - 2, order++, "shop-frame"));
            into.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "shop-panel"));
            into.Add(OverlayArt.Place(art.Solid(w, ShopWindow.TitleHeight, BagWindowArt.TitleColor), x, y, order++, "shop-title-bar"));
            SpriteFrame title = art.Text(TitleText(window.Shop), 3, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, x + 16, y + (ShopWindow.TitleHeight - title.Height) / 2, order++, "shop-title"));
            SpriteFrame gold = art.Text("GOLD " + progress.Gold.ToString(CultureInfo.InvariantCulture), 2, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(gold, x + w - 16 - gold.Width, y + (ShopWindow.TitleHeight - gold.Height) / 2, order++, "shop-gold"));

            // ---- 模式按钮
            for (int i = 0; i < 2; i++)
            {
                var mode = (ShopMode)i;
                (int mx, int my, int mw, int mh) = ShopWindow.ModeRect(mode);
                bool selected = window.Mode == mode;
                into.Add(OverlayArt.Place(art.Solid(mw, mh, selected ? ButtonSelectedColor : ButtonColor), mx, my, order++, selected ? "shop-mode-selected" : "shop-mode"));
                SpriteFrame label = art.Text(mode == ShopMode.Buy ? "BUY" : "SELL", 2, selected ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, mx + (mw - label.Width) / 2, my + (mh - label.Height) / 2, order++, "shop-mode-label"));
            }

            // ---- 页签
            int tabs = window.TabCount;
            int currentTab = window.Mode == ShopMode.Sell ? (int)window.SellTab : window.ShopTab;
            for (int i = 0; i < tabs; i++)
            {
                (int tx, int ty, int tw, int th) = ShopWindow.TabRect(window.Mode, i);
                bool selected = currentTab == i;
                into.Add(OverlayArt.Place(art.Solid(tw, th, selected ? BagWindowArt.TabSelectedColor : BagWindowArt.TabColor), tx, ty, order++, selected ? "shop-tab-selected" : "shop-tab"));
                SpriteFrame label = art.Text(TabLabel(window.Mode, i), 2, selected ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, tx + (tw - label.Width) / 2, ty + (th - label.Height) / 2, order++, "shop-tab-label"));
            }

            // ---- 物品格子
            if (window.Mode == ShopMode.Buy)
            {
                BuildBuyGrid(into, ref order, window, progress, sprites, art);
            }
            else
            {
                BuildSellGrid(into, ref order, window, progress, sprites, art);
            }

            // ---- 详情与说明
            ItemInfo item = window.ItemAtCursor(progress);
            BuildDetail(into, ref order, window, progress, sprites, art, item);
            BagWindowArt.AddLine(into, ref order, art, InfoText(item), ShopWindow.GridX, InfoY, OverlayArt.White, "shop-info");
            bool hasMessage = !string.IsNullOrEmpty(window.Message);
            string detail = hasMessage ? window.Message : FitText(item, progress);
            if (!string.IsNullOrEmpty(detail))
            {
                BagWindowArt.AddLine(into, ref order, art, detail, ShopWindow.GridX, MessageY, hasMessage ? OverlayArt.Yellow : OverlayArt.Orange, "shop-message");
            }
            SpriteFrame hint = art.Text(HintText(window), 1, BagWindowArt.DimText);
            into.Add(OverlayArt.Place(hint, ShopWindow.GridX, HintY, order++, "shop-hint"));

            if (window.IsPromptOpen)
            {
                BuildPrompt(into, ref order, window, sprites, art);
            }
        }

        /// <summary>同上,返回新建的列表(窗口没开时是空表)。</summary>
        public static List<SceneSprite> Build(ShopWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            Build(list, ref order, window, progress, sprites, art);
            return list;
        }

        /// <summary>
        /// 城镇里主角走到商店 NPC 身边时画面下方的一行提示(占位英文,深色底条加黄字;宿主在没有窗口开着、能开商店时才叠上去)。
        /// 标签:<c>shop-near-bg</c>、<c>shop-near-hint</c>。
        /// </summary>
        public static List<SceneSprite> BuildNearHint(OverlayArt art)
        {
            var list = new List<SceneSprite>();
            SpriteFrame text = art.Text("SPACE SHOP", 2, OverlayArt.Yellow);
            int w = text.Width + 24, h = text.Height + 14;
            int x = (ShopWindow.ScreenWidth - w) / 2, y = NearHintY;
            int order = 0;
            list.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "shop-near-bg", NearHintAlpha));
            list.Add(OverlayArt.Place(text, x + 12, y + 7, order++, "shop-near-hint"));
            return list;
        }

        private static void BuildBuyGrid(List<SceneSprite> into, ref int order, ShopWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art)
        {
            ItemShopTab tab = window.CurrentTab;
            for (int v = 0; v < ShopWindow.VisibleRows * ShopWindow.BuyColumns; v++)
            {
                int cell = window.ScrollRow * ShopWindow.BuyColumns + v;
                (int cx, int cy, int cw, int ch) = ShopWindow.BuyCellRect(v);
                if (window.Cursor == cell)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw + 4, ch + 4, BagWindowArt.CursorColor), cx - 2, cy - 2, order++, "shop-cell-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(cw, ch, BagWindowArt.CellColor), cx, cy, order++, "shop-cell"));
                int code = tab == null ? -1 : tab.CodeAt(cell);
                if (code < 0)
                {
                    continue;
                }
                ItemInfo info = window.Book.Item(code);
                if (info == null)
                {
                    // 数据里读不出这件商品:画一个灰色问号位
                    into.Add(OverlayArt.Place(art.Solid(cw / 2, ch / 2, BagWindowArt.DimText), cx + cw / 4, cy + ch / 4, order++, "bag-icon-block"));
                    continue;
                }
                BagWindowArt.PlaceIcon(into, ref order, sprites, art, info, cx, cy, cw, ch, "bag-icon");
                if (ShopRules.MaxBuyable(info, progress.Gold) < 1)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw, ch, OverlayArt.Black), cx, cy, order++, "shop-icon-dim", UnusableAlpha));
                }
                BagWindowArt.PlaceRarity(into, ref order, art, info, cx, cy, cw, ch, "bag-rarity");
            }
            int rows = window.BuyRows;
            if (rows > ShopWindow.VisibleRows)
            {
                int trackX = ShopWindow.GridX + ShopWindow.BuyColumns * (ShopWindow.CellSize + ShopWindow.CellGap) + 2;
                int trackH = ShopWindow.VisibleRows * (ShopWindow.CellSize + ShopWindow.CellGap) - ShopWindow.CellGap;
                into.Add(OverlayArt.Place(art.Solid(6, trackH, TrackColor), trackX, ShopWindow.GridY, order++, "shop-track"));
                int thumbH = Math.Max(12, trackH * ShopWindow.VisibleRows / rows);
                int range = rows - ShopWindow.VisibleRows;
                int thumbY = ShopWindow.GridY + (range <= 0 ? 0 : (trackH - thumbH) * window.ScrollRow / range);
                into.Add(OverlayArt.Place(art.Solid(6, thumbH, ThumbColor), trackX, thumbY, order++, "shop-thumb"));
            }
        }

        private static void BuildSellGrid(List<SceneSprite> into, ref int order, ShopWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art)
        {
            IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(window.SellTab);
            for (int i = 0; i < Inventory.SlotsPerTab; i++)
            {
                (int cx, int cy, int cw, int ch) = ShopWindow.SellCellRect(i);
                if (window.Cursor == i)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw + 4, ch + 4, BagWindowArt.CursorColor), cx - 2, cy - 2, order++, "shop-cell-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(cw, ch, BagWindowArt.CellColor), cx, cy, order++, "shop-cell"));
                if (i >= stacks.Count)
                {
                    continue;
                }
                ItemStack stack = stacks[i];
                BagWindowArt.PlaceIcon(into, ref order, sprites, art, stack.Info, cx, cy, cw, ch, "bag-icon");
                if (!ShopRules.IsSellable(stack.Info))
                {
                    into.Add(OverlayArt.Place(art.Solid(cw, ch, OverlayArt.Black), cx, cy, order++, "shop-icon-dim", UnusableAlpha));
                }
                BagWindowArt.PlaceRarity(into, ref order, art, stack.Info, cx, cy, cw, ch, "bag-rarity");
                if (stack.Count > 1)
                {
                    SpriteFrame count = art.Text(stack.Count.ToString(CultureInfo.InvariantCulture), 1, OverlayArt.White);
                    into.Add(OverlayArt.Place(count, cx + cw - 3 - count.Width, cy + ch - 8 - count.Height, order++, "shop-count"));
                }
            }
        }

        private static void BuildDetail(List<SceneSprite> into, ref int order, ShopWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art, ItemInfo item)
        {
            int dx = ShopWindow.DetailX, dy = ShopWindow.DetailY, dw = ShopWindow.DetailWidth;
            const int boxHeight = 190;
            into.Add(OverlayArt.Place(art.Solid(dw, boxHeight, BagWindowArt.CellColor), dx, dy, order++, "shop-detail"));
            if (item == null)
            {
                return;
            }
            const int slot = 56;
            int sx = dx + (dw - slot) / 2, sy = dy + 8;
            into.Add(OverlayArt.Place(art.Solid(slot, slot, BagWindowArt.SlotColor), sx, sy, order++, "shop-detail-slot"));
            BagWindowArt.PlaceIcon(into, ref order, sprites, art, item, sx, sy, slot, slot, "bag-icon");
            BagWindowArt.PlaceRarity(into, ref order, art, item, sx, sy, slot, slot, "bag-rarity");
            int line = sy + slot + 10;
            foreach (string text in DetailLines(window, progress, item))
            {
                SpriteFrame frame = art.Text(text, 2, OverlayArt.White);
                if (frame != null)
                {
                    into.Add(OverlayArt.Place(frame, dx + 8, line, order++, "shop-detail-line"));
                }
                line += 20;
            }
        }

        /// <summary>详情栏的几行(占位英文):最低等级、买入价或收购价、身上有几件。</summary>
        public static List<string> DetailLines(ShopWindow window, HeroProgress progress, ItemInfo item)
        {
            var lines = new List<string>();
            if (item == null)
            {
                return lines;
            }
            lines.Add("LV " + item.MinimumLevel.ToString(CultureInfo.InvariantCulture));
            if (window.Mode == ShopMode.Buy)
            {
                lines.Add("BUY " + ItemPricing.BuyPrice(item, 1).ToString(CultureInfo.InvariantCulture));
            }
            else if (ShopRules.IsSellable(item))
            {
                lines.Add("SELL " + ItemPricing.SellPrice(item, 1, window.Book?.Rates).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                lines.Add("NO SALE");
            }
            lines.Add("OWN " + progress.Bag.CountOf(item.Code).ToString(CultureInfo.InvariantCulture));
            return lines;
        }

        private static void BuildPrompt(List<SceneSprite> into, ref int order, ShopWindow window, ISpriteSource sprites, OverlayArt art)
        {
            SpriteFrame black = art.Solid(ShopWindow.ScreenWidth, ShopWindow.ScreenHeight, OverlayArt.Black);
            into.Add(OverlayArt.Place(black, 0, 0, order++, "shop-prompt-dim", PromptDimAlpha));
            int x = ShopWindow.PromptX, y = ShopWindow.PromptY, w = ShopWindow.PromptWidth, h = ShopWindow.PromptHeight;
            into.Add(OverlayArt.Place(art.Solid(w + 4, h + 4, BagWindowArt.FrameColor), x - 2, y - 2, order++, "shop-prompt-frame"));
            into.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "shop-prompt-panel"));
            bool buy = window.PromptKind == ShopPromptKind.Buy;
            SpriteFrame title = art.Text(buy ? "BUY HOW MANY" : "SELL HOW MANY", 2, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, x + 12, y + 10, order++, "shop-prompt-title"));
            const int slot = 40;
            int sx = x + w - 12 - slot, sy = y + 8;
            into.Add(OverlayArt.Place(art.Solid(slot, slot, BagWindowArt.SlotColor), sx, sy, order++, "shop-detail-slot"));
            if (window.PromptItem != null)
            {
                BagWindowArt.PlaceIcon(into, ref order, sprites, art, window.PromptItem, sx, sy, slot, slot, "bag-icon");
                BagWindowArt.PlaceRarity(into, ref order, art, window.PromptItem, sx, sy, slot, slot, "bag-rarity");
            }
            SpriteFrame count = art.Text(window.PromptCount.ToString(CultureInfo.InvariantCulture), 3, OverlayArt.White);
            into.Add(OverlayArt.Place(count, x + 12, y + 40, order++, "shop-prompt-count"));
            SpriteFrame max = art.Text("MAX " + window.PromptMax.ToString(CultureInfo.InvariantCulture), 1, BagWindowArt.DimText);
            into.Add(OverlayArt.Place(max, x + 24 + Math.Max(count.Width, 36), y + 52, order++, "shop-prompt-max"));
            string price = "UNIT " + window.PromptUnit.ToString(CultureInfo.InvariantCulture) + "  " + (buy ? "COST " : "GET ")
                + window.PromptTotal.ToString(CultureInfo.InvariantCulture);
            int scale = OverlayArt.TextWidth(price, 2) <= w - 24 ? 2 : 1;
            SpriteFrame priceFrame = art.Text(price, scale, OverlayArt.Orange);
            into.Add(OverlayArt.Place(priceFrame, x + 12, y + 74, order++, "shop-prompt-price"));
            string[] labels = { "-", "+", "MAX", "OK", "CANCEL" };
            for (int i = 0; i < labels.Length; i++)
            {
                (int bx, int by, int bw, int bh) = ShopWindow.PromptButtonRect(i);
                bool primary = i == ShopWindow.ButtonOk;
                into.Add(OverlayArt.Place(art.Solid(bw, bh, primary ? ButtonSelectedColor : ButtonColor), bx, by, order++, "shop-prompt-button"));
                SpriteFrame label = art.Text(labels[i], 2, primary ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, bx + (bw - label.Width) / 2, by + (bh - label.Height) / 2, order++, "shop-prompt-label"));
            }
        }

        // ------------------------------------------------------------------ 文字

        /// <summary>标题(占位英文):按商店的 <c>[type]</c> 区分武器店与一般商店。</summary>
        public static string TitleText(ItemShopDef shop)
        {
            if (shop == null)
            {
                return "SHOP";
            }
            if (string.Equals(shop.TypeTag, "[weapon shop]", StringComparison.OrdinalIgnoreCase))
            {
                return "WEAPON SHOP";
            }
            return string.Equals(shop.TypeTag, "[etc shop]", StringComparison.OrdinalIgnoreCase) ? "GENERAL SHOP" : "SHOP";
        }

        /// <summary>页签的占位英文名:买入页是 PAGE 加序号(原版页名是字符串键,要等文字系统),出售页沿用背包的名字。</summary>
        public static string TabLabel(ShopMode mode, int index)
        {
            return mode == ShopMode.Sell ? BagWindow.TabLabel((InventoryTab)index) : "PAGE " + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>第一行说明:物品的最低等级、品级、稀有度与价格;没有物品是空串。</summary>
        public static string InfoText(ItemInfo item)
        {
            if (item == null)
            {
                return string.Empty;
            }
            return "LV " + item.MinimumLevel.ToString(CultureInfo.InvariantCulture)
                + "  GRADE " + item.Grade.ToString(CultureInfo.InvariantCulture)
                + "  RARITY " + item.Rarity.ToString(CultureInfo.InvariantCulture)
                + "  PRICE " + item.Price.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>第二行(没有操作提示时):装备类物品说这位主角穿不穿得了;其余是空串。</summary>
        public static string FitText(ItemInfo item, HeroProgress progress)
        {
            if (item == null || !item.IsEquipment)
            {
                return string.Empty;
            }
            EquipResult r = EquipRules.Check(item, progress.Job, progress.Level);
            switch (r)
            {
                case EquipResult.Ok:
                    return "CAN EQUIP";
                case EquipResult.LevelTooLow:
                    return "NEED LEVEL " + item.MinimumLevel.ToString(CultureInfo.InvariantCulture);
                default:
                    return BagWindow.MessageOf(r);
            }
        }

        /// <summary>底部按键提示(占位英文),随模式与数量框变。</summary>
        public static string HintText(ShopWindow window)
        {
            if (window.IsPromptOpen)
            {
                return "UP DOWN +1 -1   LEFT RIGHT -10 +10   ENTER OK   ESC CANCEL";
            }
            return window.Mode == ShopMode.Buy
                ? "ARROWS MOVE   ENTER BUY   Q E PAGE   TAB SELL MODE   ESC CLOSE"
                : "ARROWS MOVE   ENTER SELL   Q E PAGE   TAB BUY MODE   ESC CLOSE";
        }
    }
}
