using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 背包窗口(<see cref="BagWindow"/>)的画法:整屏压暗、居中的深色面板带一圈边、标题与金币、左边装备栏 12 格、右边页签与 8 × 7 的物品格子、
    /// 底下三行说明。图标取物品脚本 <c>[icon]</c> 指的那一帧(素材库里没有就画一个按稀有度着色的小方块),叠加数量用像素字,
    /// 主角穿不了的装备盖一层半透明的黑。<b>本项目自己的占位版式</b>(原版背包界面没有读到),文字是占位英文,不依赖图包里的界面素材。
    /// </summary>
    public static class BagWindowArt
    {
        /// <summary>压暗的不透明度(0 到 255)。</summary>
        public const int DimAlpha = 150;

        /// <summary>穿不了的装备上盖的黑色的不透明度。</summary>
        public const int UnusableAlpha = 150;

        public const uint FrameColor = 0x8A7A5Au;
        public const uint PanelColor = 0x16161Eu;
        public const uint TitleColor = 0x242434u;
        public const uint CellColor = 0x24243Au;
        public const uint SlotColor = 0x2C2C44u;
        public const uint TabColor = 0x2A2A38u;
        public const uint TabSelectedColor = 0x7A3A10u;
        public const uint CursorColor = 0xFFE23Cu;
        public const uint DimText = 0x8C8CA0u;

        /// <summary>稀有度 0 到 5 的占位颜色(格子底边的色条、没有图标时的方块);原版的稀有度配色没有读到。</summary>
        public static readonly uint[] RarityColors = { 0x9A9A9Au, 0xF2F2F2u, 0x4C8CFFu, 0xB060F0u, 0xFF70C0u, 0xFFA030u };

        /// <summary>说明行(每行一个 y,画布像素)。</summary>
        public const int InfoY = 496;
        public const int MessageY = 518;
        public const int HintY = 546;

        /// <summary>左下角的几行主角数据的起点 y。</summary>
        public const int StatsY = 324;

        /// <summary>
        /// 把窗口的精灵追加到 <paramref name="into"/>,<paramref name="order"/> 逐个加一;窗口没开时什么也不加。
        /// <paramref name="worn"/> 是穿着装备的攻击加成(左下角显示),<paramref name="sprites"/> 取物品图标(可为 null,那就全用色块)。
        /// 标签:<c>bag-dim</c>、<c>bag-frame</c>、<c>bag-panel</c>、<c>bag-title</c>、<c>bag-gold</c>、<c>bag-caption</c>、
        /// <c>bag-tab</c>/<c>bag-tab-selected</c>/<c>bag-tab-label</c>、<c>bag-cell</c>/<c>bag-cell-selected</c>、<c>bag-icon</c>/<c>bag-icon-block</c>/<c>bag-icon-dim</c>、
        /// <c>bag-rarity</c>、<c>bag-count</c>、<c>bag-slot</c>/<c>bag-slot-selected</c>/<c>bag-slot-label</c>/<c>bag-slot-icon</c>/<c>bag-slot-rarity</c>、<c>bag-stat</c>、<c>bag-info</c>、<c>bag-message</c>、<c>bag-hint</c>。
        /// </summary>
        public static void Build(List<SceneSprite> into, ref int order, BagWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art,
            EquipmentStats worn = default)
        {
            if (window == null || !window.Open)
            {
                return;
            }
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            SpriteFrame black = art.Solid(BagWindow.ScreenWidth, BagWindow.ScreenHeight, OverlayArt.Black);
            into.Add(OverlayArt.PlaceDim(black, DimAlpha, order++, "bag-dim"));
            int x = BagWindow.WindowX, y = BagWindow.WindowY, w = BagWindow.WindowWidth, h = BagWindow.WindowHeight;
            into.Add(OverlayArt.Place(art.Solid(w + 4, h + 4, FrameColor), x - 2, y - 2, order++, "bag-frame"));
            into.Add(OverlayArt.Place(art.Solid(w, h, PanelColor), x, y, order++, "bag-panel"));
            into.Add(OverlayArt.Place(art.Solid(w, BagWindow.TitleHeight, TitleColor), x, y, order++, "bag-title-bar"));
            SpriteFrame title = art.Text("INVENTORY", 3, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, x + 16, y + (BagWindow.TitleHeight - title.Height) / 2, order++, "bag-title"));
            SpriteFrame gold = art.Text("GOLD " + progress.Gold.ToString(CultureInfo.InvariantCulture), 2, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(gold, x + w - 16 - gold.Width, y + (BagWindow.TitleHeight - gold.Height) / 2, order++, "bag-gold"));

            // ---- 装备栏
            SpriteFrame caption = art.Text("EQUIPMENT", 2, OverlayArt.White);
            into.Add(OverlayArt.Place(caption, BagWindow.SlotX, BagWindow.CaptionY, order++, "bag-caption"));
            for (int i = 0; i < EquipmentSlots.Count; i++)
            {
                (int sx, int sy, int sw, int sh) = BagWindow.SlotRect(i);
                bool selected = window.OnEquipment && window.EquipCursor == i;
                if (selected)
                {
                    into.Add(OverlayArt.Place(art.Solid(sw + 4, sh + 4, CursorColor), sx - 2, sy - 2, order++, "bag-slot-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(sw, sh, SlotColor), sx, sy, order++, "bag-slot"));
                int part = EquipmentSlots.First + i;
                ItemInfo worn1 = progress.Worn.Get(part);
                if (worn1 != null)
                {
                    PlaceIcon(into, ref order, sprites, art, worn1, sx, sy, sw, sh, "bag-slot-icon");
                    PlaceRarity(into, ref order, art, worn1, sx, sy, sw, sh, "bag-slot-rarity");
                }
                else
                {
                    SpriteFrame label = art.Text(BagWindow.SlotLabel(part), 1, DimText);
                    if (label != null)
                    {
                        into.Add(OverlayArt.Place(label, sx + (sw - label.Width) / 2, sy + (sh - label.Height) / 2, order++, "bag-slot-label"));
                    }
                }
            }

            // ---- 主角数据(左下)
            int line = StatsY;
            AddLine(into, ref order, art, "LEVEL " + progress.Level.ToString(CultureInfo.InvariantCulture), BagWindow.SlotX, line, OverlayArt.White, "bag-stat");
            line += 20;
            AddLine(into, ref order, art, "ATK P +" + worn.PhysicalAttack.ToString(CultureInfo.InvariantCulture), BagWindow.SlotX, line, OverlayArt.White, "bag-stat");
            line += 20;
            AddLine(into, ref order, art, "ATK M +" + worn.MagicalAttack.ToString(CultureInfo.InvariantCulture), BagWindow.SlotX, line, OverlayArt.White, "bag-stat");

            // ---- 页签
            for (int i = 0; i < Inventory.TabCount; i++)
            {
                (int tx, int ty, int tw, int th) = BagWindow.TabRect(i);
                bool selected = (int)window.Tab == i;
                into.Add(OverlayArt.Place(art.Solid(tw, th, selected ? TabSelectedColor : TabColor), tx, ty, order++, selected ? "bag-tab-selected" : "bag-tab"));
                SpriteFrame label = art.Text(BagWindow.TabLabel((InventoryTab)i), 2, selected ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, tx + (tw - label.Width) / 2, ty + (th - label.Height) / 2, order++, "bag-tab-label"));
            }

            // ---- 物品格子
            IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(window.Tab);
            for (int i = 0; i < Inventory.SlotsPerTab; i++)
            {
                (int cx, int cy, int cw, int ch) = BagWindow.CellRect(i);
                if (!window.OnEquipment && window.Cursor == i)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw + 4, ch + 4, CursorColor), cx - 2, cy - 2, order++, "bag-cell-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(cw, ch, CellColor), cx, cy, order++, "bag-cell"));
                if (i >= stacks.Count)
                {
                    continue;
                }
                ItemStack stack = stacks[i];
                PlaceIcon(into, ref order, sprites, art, stack.Info, cx, cy, cw, ch, "bag-icon");
                if (stack.Info.IsEquipment && EquipRules.Check(stack.Info, progress.Job, progress.Level) != EquipResult.Ok)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw, ch, OverlayArt.Black), cx, cy, order++, "bag-icon-dim", UnusableAlpha));
                }
                PlaceRarity(into, ref order, art, stack.Info, cx, cy, cw, ch, "bag-rarity");
                if (stack.Count > 1)
                {
                    SpriteFrame count = art.Text(stack.Count.ToString(CultureInfo.InvariantCulture), 1, OverlayArt.White);
                    into.Add(OverlayArt.Place(count, cx + cw - 3 - count.Width, cy + ch - 8 - count.Height, order++, "bag-count"));
                }
            }

            // ---- 说明
            ItemInfo selectedItem = SelectedItem(window, progress);
            AddLine(into, ref order, art, InfoText(window, selectedItem), BagWindow.SlotX, InfoY, OverlayArt.White, "bag-info");
            string detail = !string.IsNullOrEmpty(window.Message) ? window.Message : FitText(window, selectedItem, progress);
            if (!string.IsNullOrEmpty(detail))
            {
                AddLine(into, ref order, art, detail, BagWindow.SlotX, MessageY, !string.IsNullOrEmpty(window.Message) ? OverlayArt.Yellow : OverlayArt.Orange, "bag-message");
            }
            SpriteFrame hint = art.Text(HintText, 1, DimText);
            into.Add(OverlayArt.Place(hint, BagWindow.SlotX, HintY, order++, "bag-hint"));
        }

        /// <summary>底部按键提示(占位英文)。</summary>
        public const string HintText = "ARROWS MOVE   ENTER EQUIP OR UNEQUIP   Q E PAGE   I CLOSE";

        /// <summary>同上,返回新建的列表(窗口没开时是空表)。</summary>
        public static List<SceneSprite> Build(BagWindow window, HeroProgress progress, ISpriteSource sprites, OverlayArt art, EquipmentStats worn = default)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            Build(list, ref order, window, progress, sprites, art, worn);
            return list;
        }

        /// <summary>光标指着的物品(装备栏上那一格穿的、或物品格子里那一格);没有返回 null。</summary>
        public static ItemInfo SelectedItem(BagWindow window, HeroProgress progress)
        {
            if (window.OnEquipment)
            {
                return progress.Worn.Get(EquipmentSlots.First + window.EquipCursor);
            }
            IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(window.Tab);
            return (uint)window.Cursor < (uint)stacks.Count ? stacks[window.Cursor].Info : null;
        }

        /// <summary>第一行说明:物品的最低等级、品级、稀有度与价格(占位英文);没有物品时装备栏写 EMPTY SLOT,物品格子是空串。</summary>
        public static string InfoText(BagWindow window, ItemInfo item)
        {
            if (item == null)
            {
                return window.OnEquipment ? "EMPTY SLOT" : string.Empty;
            }
            return "LV " + item.MinimumLevel.ToString(CultureInfo.InvariantCulture)
                + "  GRADE " + item.Grade.ToString(CultureInfo.InvariantCulture)
                + "  RARITY " + item.Rarity.ToString(CultureInfo.InvariantCulture)
                + "  PRICE " + item.Price.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>第二行(没有操作提示时):物品格子里的装备,说这位主角穿不穿得了。</summary>
        public static string FitText(BagWindow window, ItemInfo item, HeroProgress progress)
        {
            if (item == null || window.OnEquipment || !item.IsEquipment)
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

        private static void AddLine(List<SceneSprite> into, ref int order, OverlayArt art, string text, int x, int y, uint color, string tag)
        {
            SpriteFrame frame = art.Text(text, 2, color);
            if (frame != null)
            {
                into.Add(OverlayArt.Place(frame, x, y, order++, tag));
            }
        }

        private static void PlaceIcon(List<SceneSprite> into, ref int order, ISpriteSource sprites, OverlayArt art, ItemInfo info, int rx, int ry, int rw, int rh, string tag)
        {
            SpriteFrame frame = null;
            if (sprites != null && info.IconKey != null && info.IconFrame >= 0)
            {
                sprites.TryGetFrame(info.IconKey, info.IconFrame, out frame);
            }
            if (frame != null && frame.Width > 0 && frame.Height > 0 && !frame.IsBlank)
            {
                int x = rx + (rw - frame.CanvasWidth) / 2 + frame.OffsetX;
                int y = ry + (rh - frame.CanvasHeight) / 2 + frame.OffsetY;
                into.Add(OverlayArt.Place(frame, x, y, order++, tag));
                return;
            }
            int side = Math.Min(rw, rh) / 2;
            into.Add(OverlayArt.Place(art.Solid(side, side, RarityColor(info.Rarity)), rx + (rw - side) / 2, ry + (rh - side) / 2, order++, "bag-icon-block"));
        }

        private static void PlaceRarity(List<SceneSprite> into, ref int order, OverlayArt art, ItemInfo info, int rx, int ry, int rw, int rh, string tag)
        {
            into.Add(OverlayArt.Place(art.Solid(rw - 8, 3, RarityColor(info.Rarity)), rx + 4, ry + rh - 6, order++, tag));
        }

        /// <summary>稀有度对应的占位颜色(越界夹在 0 到 5 之间)。</summary>
        public static uint RarityColor(int rarity)
        {
            return RarityColors[rarity < 0 ? 0 : rarity >= RarityColors.Length ? RarityColors.Length - 1 : rarity];
        }
    }
}
