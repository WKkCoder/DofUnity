using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 背包装扮页(<see cref="BagAvatarPage"/>)的画法(<b>本项目自己的占位版式</b>,与物品页同一套版面与配色):左边装扮栏 10 格(穿着的画图标,空的写部位缩写)、
    /// 左下几行统计、右边 8 × 7 的时装格子(现在穿不上的盖一层半透明的黑)、底下三行说明。外壳(压暗、边框、标题条、金币)由 <see cref="BagWindowArt"/> 先画,再接着画这一页。
    /// 标签:<c>bag-page-button</c>/<c>bag-page-button-selected</c>/<c>bag-page-label</c>(标题条里切页的按钮,两页都有)、
    /// <c>avatar-caption</c>、<c>avatar-slot</c>/<c>avatar-slot-selected</c>/<c>avatar-slot-label</c>/<c>avatar-slot-icon</c>/<c>avatar-slot-rarity</c>、<c>avatar-stat</c>、
    /// <c>avatar-item-name</c>/<c>avatar-item-explain</c>(有字体与文字表时)、<c>avatar-tab-selected</c>/<c>avatar-tab-label</c>、
    /// <c>avatar-cell</c>/<c>avatar-cell-selected</c>、<c>avatar-icon</c>/<c>bag-icon-block</c>/<c>avatar-icon-dim</c>、<c>avatar-rarity</c>、<c>avatar-info</c>、<c>avatar-message</c>、<c>avatar-hint</c>。
    /// </summary>
    public static class BagAvatarArt
    {
        /// <summary>底部按键提示(占位英文)。</summary>
        public const string HintText = "ARROWS MOVE   ENTER WEAR OR TAKE OFF   V ITEMS   I CLOSE";

        /// <summary>左上角装扮栏的标题与右边页签位置上那个选中的"页签"的字。</summary>
        public const string CaptionText = "AVATAR";

        /// <summary>切页按钮上的字:在物品页写去装扮页,在装扮页写回物品页(按键 V)。</summary>
        public static string PageButtonLabel(BagPage page)
        {
            return page == BagPage.Avatars ? "V ITEMS" : "V AVATAR";
        }

        /// <summary>标题条里的切页按钮;窗口没接装扮页(<see cref="BagWindow.Avatars"/> 为 null)时什么也不画。</summary>
        public static void PlacePageButton(List<SceneSprite> into, ref int order, OverlayArt art, BagWindow window)
        {
            if (window.Avatars == null)
            {
                return;
            }
            (int x, int y, int w, int h) = BagWindow.PageButtonRect;
            bool onAvatars = window.Page == BagPage.Avatars;
            into.Add(OverlayArt.Place(art.Solid(w, h, onAvatars ? BagWindowArt.TabSelectedColor : BagWindowArt.TabColor), x, y, order++, onAvatars ? "bag-page-button-selected" : "bag-page-button"));
            SpriteFrame label = art.Text(PageButtonLabel(window.Page), 2, onAvatars ? OverlayArt.Yellow : OverlayArt.White);
            into.Add(OverlayArt.Place(label, x + (w - label.Width) / 2, y + (h - label.Height) / 2, order++, "bag-page-label"));
        }

        /// <summary>把装扮页的精灵追加到 <paramref name="into"/>(外壳之后);窗口没接装扮页时什么也不加。<paramref name="sprites"/> 取物品图标(可为 null,那就全用色块)。</summary>
        public static void Build(List<SceneSprite> into, ref int order, BagWindow window, ISpriteSource sprites, OverlayArt art, ItemTexts texts = null)
        {
            BagAvatarPage page = window.Avatars;
            if (page == null)
            {
                return;
            }
            AvatarBagView view = page.Refresh();
            AvatarSlots slots = page.Session.Slots;

            // ---- 装扮栏
            SpriteFrame caption = art.Text(CaptionText, 2, OverlayArt.White);
            into.Add(OverlayArt.Place(caption, BagWindow.SlotX, BagWindow.CaptionY, order++, "avatar-caption"));
            for (int i = 0; i < AvatarSlots.Count; i++)
            {
                (int sx, int sy, int sw, int sh) = BagWindow.SlotRect(i);
                if (page.OnSlots && page.SlotCursor == i)
                {
                    into.Add(OverlayArt.Place(art.Solid(sw + 4, sh + 4, BagWindowArt.CursorColor), sx - 2, sy - 2, order++, "avatar-slot-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(sw, sh, BagWindowArt.SlotColor), sx, sy, order++, "avatar-slot"));
                int part = AvatarSlots.First + i;
                ItemInfo worn = slots.Get(part);
                if (worn != null)
                {
                    BagWindowArt.PlaceIcon(into, ref order, sprites, art, worn, sx, sy, sw, sh, "avatar-slot-icon");
                    BagWindowArt.PlaceRarity(into, ref order, art, worn, sx, sy, sw, sh, "avatar-slot-rarity");
                }
                else
                {
                    SpriteFrame label = art.Text(BagAvatarPage.SlotLabel(part), 1, BagWindowArt.DimText);
                    if (label != null)
                    {
                        into.Add(OverlayArt.Place(label, sx + (sw - label.Width) / 2, sy + (sh - label.Height) / 2, order++, "avatar-slot-label"));
                    }
                }
            }

            // ---- 统计(左下):穿着几件、装备页里有几件时装、其中现在穿得上几件
            int line = BagWindowArt.StatsY;
            foreach (string text in StatLines(slots.WornCount, view.Avatars.Count, view.WearableCount))
            {
                BagWindowArt.AddLine(into, ref order, art, text, BagWindow.SlotX, line, OverlayArt.White, "avatar-stat");
                line += 20;
            }

            // ---- 光标处物品的名字与说明(装了字体、字符串表取得到名字时才有)
            ItemDetailArt.Build(into, ref order, art, texts, page.SelectedItem, BagWindow.SlotX, line + 8, BagWindowArt.DetailWidth, BagWindowArt.InfoY - 6, "avatar-item");

            // ---- 页签位置上一个选中的"页签"(装扮页只有这一页)
            (int tx, int ty, int tw, int th) = BagWindow.TabRect(0);
            into.Add(OverlayArt.Place(art.Solid(tw, th, BagWindowArt.TabSelectedColor), tx, ty, order++, "avatar-tab-selected"));
            SpriteFrame tabLabel = art.Text(CaptionText, 2, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(tabLabel, tx + (tw - tabLabel.Width) / 2, ty + (th - tabLabel.Height) / 2, order++, "avatar-tab-label"));

            // ---- 时装格子
            for (int i = 0; i < Inventory.SlotsPerTab; i++)
            {
                (int cx, int cy, int cw, int ch) = BagWindow.CellRect(i);
                if (!page.OnSlots && page.Cursor == i)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw + 4, ch + 4, BagWindowArt.CursorColor), cx - 2, cy - 2, order++, "avatar-cell-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(cw, ch, BagWindowArt.CellColor), cx, cy, order++, "avatar-cell"));
                if (i >= view.Avatars.Count)
                {
                    continue;
                }
                AvatarBagEntry entry = view.Avatars[i];
                BagWindowArt.PlaceIcon(into, ref order, sprites, art, entry.Item, cx, cy, cw, ch, "avatar-icon");
                if (!entry.CanWear)
                {
                    into.Add(OverlayArt.Place(art.Solid(cw, ch, OverlayArt.Black), cx, cy, order++, "avatar-icon-dim", BagWindowArt.UnusableAlpha));
                }
                BagWindowArt.PlaceRarity(into, ref order, art, entry.Item, cx, cy, cw, ch, "avatar-rarity");
            }

            // ---- 说明
            BagWindowArt.AddLine(into, ref order, art, InfoText(page, page.SelectedItem), BagWindow.SlotX, BagWindowArt.InfoY, OverlayArt.White, "avatar-info");
            string detail = !string.IsNullOrEmpty(page.Message) ? page.Message : FitText(page);
            if (!string.IsNullOrEmpty(detail))
            {
                BagWindowArt.AddLine(into, ref order, art, detail, BagWindow.SlotX, BagWindowArt.MessageY, !string.IsNullOrEmpty(page.Message) ? OverlayArt.Yellow : OverlayArt.Orange, "avatar-message");
            }
            SpriteFrame hint = art.Text(HintText, 1, BagWindowArt.DimText);
            into.Add(OverlayArt.Place(hint, BagWindow.SlotX, BagWindowArt.HintY, order++, "avatar-hint"));
        }

        /// <summary>左下角的三行统计:穿着几件(WORN n/10)、装备页里有几件时装、其中现在穿得上几件。</summary>
        public static string[] StatLines(int worn, int inBag, int usable)
        {
            return new[]
            {
                "WORN " + worn.ToString(CultureInfo.InvariantCulture) + "/" + AvatarSlots.Count.ToString(CultureInfo.InvariantCulture),
                "IN BAG " + inBag.ToString(CultureInfo.InvariantCulture),
                "USABLE " + usable.ToString(CultureInfo.InvariantCulture),
            };
        }

        /// <summary>第一行说明:物品的最低等级、品级、稀有度与价格(占位英文);没有物品时装扮栏写 EMPTY SLOT,时装格子是空串。</summary>
        public static string InfoText(BagAvatarPage page, ItemInfo item)
        {
            if (item == null)
            {
                return page.OnSlots ? "EMPTY SLOT" : string.Empty;
            }
            return "LV " + item.MinimumLevel.ToString(CultureInfo.InvariantCulture)
                + "  GRADE " + item.Grade.ToString(CultureInfo.InvariantCulture)
                + "  RARITY " + item.Rarity.ToString(CultureInfo.InvariantCulture)
                + "  PRICE " + item.Price.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 第二行(没有操作提示时):光标在时装格子上,说这位主角穿不穿得了——直接取规则层给这一格的结果(<see cref="AvatarBagEntry.Result"/>);
        /// 规则层放行的,再问一句画面上穿不穿得出来(<see cref="AvatarSession.CanShow"/>,穿的时候问的是同一个钩子)。
        /// </summary>
        public static string FitText(BagAvatarPage page)
        {
            AvatarBagEntry? entry = page.SelectedEntry;
            if (!entry.HasValue)
            {
                return string.Empty;
            }
            switch (entry.Value.Result)
            {
                case EquipResult.Ok:
                    return page.Session.CanShow(entry.Value.Item) ? "CAN WEAR" : BagWindow.MessageOf(EquipResult.NotShowable);
                case EquipResult.LevelTooLow:
                    return "NEED LEVEL " + entry.Value.Item.MinimumLevel.ToString(CultureInfo.InvariantCulture);
                default:
                    return BagWindow.MessageOf(entry.Value.Result);
            }
        }
    }
}
