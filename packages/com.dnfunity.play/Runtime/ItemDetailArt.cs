using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 背包与商店窗口里"光标处物品"的文字块:物品名(按稀有度着色,最多三行)和下面折行的说明(淡色,放不下的截掉补省略号)。
    /// 只在有字体集(<see cref="OverlayArt.HasFonts"/>)而且字符串表里取得到名字时才画;否则什么也不加,窗口保持占位英文的样子。
    /// 标签:<c>{tag}-name</c>、<c>{tag}-explain</c>。<b>本项目自己的占位版式</b>。
    /// </summary>
    public static class ItemDetailArt
    {
        /// <summary>说明文字的颜色(比正文淡一点)。</summary>
        public const uint ExplainColor = 0xC4C4D4u;

        public const int NameScale = 2;
        public const int ExplainScale = 1;
        public const int NameMaxLines = 3;
        public const int BlockGap = 6;

        /// <summary>
        /// 把文字块追加到 <paramref name="into"/>:左上角 (<paramref name="x"/>, <paramref name="y"/>),宽 <paramref name="width"/>,最低到 <paramref name="bottom"/>。
        /// 返回画了几个精灵(0 表示没有字体或没有文字)。
        /// </summary>
        public static int Build(List<SceneSprite> into, ref int order, OverlayArt art, ItemTexts texts, ItemInfo item, int x, int y, int width, int bottom, string tag)
        {
            if (into == null || art == null || texts == null || item == null || !art.HasFonts || width <= 0)
            {
                return 0;
            }
            string name = texts.NameOf(item);
            if (name == null)
            {
                return 0;
            }
            int added = 0;
            SpriteFrame nameFrame = art.Wrapped(name, NameScale, BagWindowArt.RarityColor(item.Rarity), width, NameMaxLines);
            if (nameFrame == null)
            {
                return 0;
            }
            into.Add(OverlayArt.Place(nameFrame, x, y, order++, tag + "-name"));
            added++;
            string explain = texts.ExplainOf(item);
            if (explain == null)
            {
                return added;
            }
            int top = y + nameFrame.Height + BlockGap;
            int lineHeight = art.GlyphLineHeight(ExplainScale);
            if (lineHeight <= 0)
            {
                return added;
            }
            int lines = (bottom - top) / lineHeight;
            if (lines < 1)
            {
                return added;
            }
            SpriteFrame explainFrame = art.Wrapped(explain, ExplainScale, ExplainColor, width, lines);
            if (explainFrame != null)
            {
                into.Add(OverlayArt.Place(explainFrame, x, top, order++, tag + "-explain"));
                added++;
            }
            return added;
        }
    }
}
