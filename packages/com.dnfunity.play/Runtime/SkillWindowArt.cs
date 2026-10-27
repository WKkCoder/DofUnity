using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 技能窗口(<see cref="SkillWindow"/>)的画法:整屏压暗、居中的深色面板带一圈边、标题与点数,上面四个分页,左边技能列表(图标、名字、等级、快捷栏键),
    /// 右边选中技能的详情(图标、名字、等级、下一级的花费与要求的主角等级、说明、前置技能),底下快捷栏六格与学习、退还、重置三个按钮、结果文字与按键提示。
    /// 技能名与说明是数据文字,只在有字体集且字符串表取得到时画(<see cref="OverlayArt.HasFonts"/>);否则名字退回占位的 <c>SKILL 编号</c>、不画说明,窗口照样能操作。
    /// 图标取技能脚本 <c>[icon]</c> 指的那一帧(学会了画亮的,没学画灰的;素材库里没有就画一个色块)。
    /// <b>本项目自己的占位版式</b>(原版技能界面没有读到),界面词是占位英文(<see cref="UiText"/> 有中文)。
    /// 标签:<c>skill-dim</c>、<c>skill-frame</c>、<c>skill-panel</c>、<c>skill-title-bar</c>、<c>skill-title</c>、<c>skill-points</c>、
    /// <c>skill-tab</c>/<c>skill-tab-selected</c>/<c>skill-tab-label</c>、<c>skill-row</c>/<c>skill-row-selected</c>/<c>skill-row-icon</c>/<c>skill-row-block</c>/<c>skill-row-name</c>/<c>skill-row-level</c>/<c>skill-row-key</c>、
    /// <c>skill-track</c>/<c>skill-thumb</c>、<c>skill-empty</c>、<c>skill-detail</c>、<c>skill-icon</c>/<c>skill-icon-block</c>、<c>skill-name</c>、<c>skill-info</c>、<c>skill-explain</c>、
    /// <c>skill-prereq-head</c>/<c>skill-prereq</c>、<c>skill-quick</c>/<c>skill-quick-selected</c>/<c>skill-quick-icon</c>/<c>skill-quick-key</c>、
    /// <c>skill-button</c>/<c>skill-button-primary</c>/<c>skill-button-label</c>、<c>skill-message</c>、<c>skill-hint</c>。
    /// </summary>
    public static class SkillWindowArt
    {
        /// <summary>压暗的不透明度(0 到 255)。</summary>
        public const int DimAlpha = 150;

        /// <summary>能再学一级的名字颜色(浅绿);学不了的用暗灰;满级的黄。</summary>
        public const uint LearnableColor = 0x8CF08Cu;
        public const uint LockedColor = BagWindowArt.DimText;
        public const uint UnmetColor = 0xE86060u;

        public const uint ButtonColor = ShopWindowArt.ButtonColor;
        public const uint ButtonPrimaryColor = ShopWindowArt.ButtonSelectedColor;
        public const uint TrackColor = ShopWindowArt.TrackColor;
        public const uint ThumbColor = ShopWindowArt.ThumbColor;

        /// <summary>图标的显示边长(列表行里、详情栏里)。</summary>
        public const int IconSize = 32;

        /// <summary>详情栏里信息行的行高、说明文字的字号倍数。</summary>
        public const int LineHeight = 22;
        public const int ExplainScale = 1;
        public const uint ExplainColor = ItemDetailArt.ExplainColor;

        /// <summary>说明行(每行一个 y,画布像素;和背包窗口同一组)。</summary>
        public const int MessageY = BagWindowArt.MessageY;
        public const int HintY = BagWindowArt.HintY;

        /// <summary>快捷栏六个键的字母(与 <see cref="SkillKeys"/> 的顺序一致)。</summary>
        public static readonly string[] SlotKeys = { "A", "S", "D", "F", "G", "H" };

        /// <summary>四个分页的占位英文标题(下标是 <see cref="SkillRules.UiGroup"/>)。</summary>
        public static readonly string[] PageNames = { "COMMON", "CLASS", "OTHER", "AWAKEN" };

        /// <summary>把窗口的精灵追加到 <paramref name="into"/>,<paramref name="order"/> 逐个加一;窗口没开时什么也不加。<paramref name="sprites"/> 取技能图标(可为 null,那就全用色块)。</summary>
        public static void Build(List<SceneSprite> into, ref int order, SkillWindow window, ISpriteSource sprites, OverlayArt art)
        {
            if (window == null || !window.Open)
            {
                return;
            }
            int x = SkillWindow.WindowX, y = SkillWindow.WindowY, w = SkillWindow.WindowWidth, h = SkillWindow.WindowHeight;
            SpriteFrame black = art.Solid(SkillWindow.ScreenWidth, SkillWindow.ScreenHeight, OverlayArt.Black);
            into.Add(OverlayArt.PlaceDim(black, DimAlpha, order++, "skill-dim"));
            into.Add(OverlayArt.Place(art.Solid(w + 4, h + 4, BagWindowArt.FrameColor), x - 2, y - 2, order++, "skill-frame"));
            into.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "skill-panel"));
            into.Add(OverlayArt.Place(art.Solid(w, SkillWindow.TitleHeight, BagWindowArt.TitleColor), x, y, order++, "skill-title-bar"));
            SpriteFrame title = art.Text("SKILLS", 3, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, x + 16, y + (SkillWindow.TitleHeight - title.Height) / 2, order++, "skill-title"));
            SpriteFrame points = art.Text(PointsText(window.SkillPoints, window.AwakeningPoints), 2, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(points, x + w - 16 - points.Width, y + (SkillWindow.TitleHeight - points.Height) / 2, order++, "skill-points"));

            BuildTabs(into, ref order, window, art);
            BuildList(into, ref order, window, sprites, art);
            BuildDetail(into, ref order, window, sprites, art);
            BuildQuickBar(into, ref order, window, sprites, art);
            BuildButtons(into, ref order, window, art);

            if (!string.IsNullOrEmpty(window.Message))
            {
                BagWindowArt.AddLine(into, ref order, art, window.Message, SkillWindow.ListX, MessageY, OverlayArt.Yellow, "skill-message");
            }
            SpriteFrame hint = art.Text(HintText, 1, BagWindowArt.DimText);
            into.Add(OverlayArt.Place(hint, SkillWindow.ListX, HintY, order++, "skill-hint"));
        }

        /// <summary>同上,返回新建的列表(窗口没开时是空表)。</summary>
        public static List<SceneSprite> Build(SkillWindow window, ISpriteSource sprites, OverlayArt art)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            Build(list, ref order, window, sprites, art);
            return list;
        }

        // ------------------------------------------------------------------ 分页

        private static void BuildTabs(List<SceneSprite> into, ref int order, SkillWindow window, OverlayArt art)
        {
            for (int i = 0; i < SkillWindow.PageCount; i++)
            {
                (int tx, int ty, int tw, int th) = SkillWindow.TabRect(i);
                bool selected = window.Page == i;
                into.Add(OverlayArt.Place(art.Solid(tw, th, selected ? BagWindowArt.TabSelectedColor : BagWindowArt.TabColor), tx, ty, order++, selected ? "skill-tab-selected" : "skill-tab"));
                SpriteFrame label = art.Text(PageNames[i], 2, selected ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, tx + (tw - label.Width) / 2, ty + (th - label.Height) / 2, order++, "skill-tab-label"));
            }
        }

        // ------------------------------------------------------------------ 左边:技能列表

        private static void BuildList(List<SceneSprite> into, ref int order, SkillWindow window, ISpriteSource sprites, OverlayArt art)
        {
            IReadOnlyList<SkillRow> rows = window.Rows;
            if (rows.Count == 0)
            {
                SpriteFrame empty = art.Text("NO SKILLS", 2, BagWindowArt.DimText);
                into.Add(OverlayArt.Place(empty, SkillWindow.ListX + 8, SkillWindow.ListY + 12, order++, "skill-empty"));
                return;
            }
            for (int v = 0; v < SkillWindow.VisibleRows; v++)
            {
                int index = window.ScrollRow + v;
                if (index >= rows.Count)
                {
                    break;
                }
                SkillRow row = rows[index];
                (int rx, int ry, int rw, int rh) = SkillWindow.RowRect(v);
                if (index == window.Selected)
                {
                    into.Add(OverlayArt.Place(art.Solid(rw + 4, rh + 4, BagWindowArt.CursorColor), rx - 2, ry - 2, order++, "skill-row-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(rw, rh, BagWindowArt.CellColor), rx, ry, order++, "skill-row"));
                PlaceIcon(into, ref order, sprites, art, row, rx + 4, ry + (rh - IconSize) / 2, IconSize, "skill-row-icon", "skill-row-block");
                uint color = NameColor(row);
                int textX = rx + 4 + IconSize + 8;
                SpriteFrame levelText = art.Text(LevelText(row), 2, color);
                int levelX = rx + rw - 6 - levelText.Width;
                SpriteFrame name = NameFrame(art, row, 1, color, levelX - 8 - textX, 2);
                if (name != null)
                {
                    into.Add(OverlayArt.Place(name, textX, ry + Math.Max(2, (rh - name.Height) / 2), order++, "skill-row-name"));
                }
                into.Add(OverlayArt.Place(levelText, levelX, ry + (rh - levelText.Height) / 2, order++, "skill-row-level"));
                if (row.Quick >= 0 && row.Quick < SlotKeys.Length)
                {
                    SpriteFrame key = art.Text(SlotKeys[row.Quick], 1, OverlayArt.Orange);
                    into.Add(OverlayArt.Place(key, rx + 4 + IconSize - key.Width, ry + rh - key.Height - 1, order++, "skill-row-key"));
                }
            }
            if (rows.Count > SkillWindow.VisibleRows)
            {
                int trackX = SkillWindow.ListX + SkillWindow.ListWidth + 2;
                int trackH = SkillWindow.VisibleRows * (SkillWindow.RowHeight + SkillWindow.RowGap) - SkillWindow.RowGap;
                into.Add(OverlayArt.Place(art.Solid(6, trackH, TrackColor), trackX, SkillWindow.ListY, order++, "skill-track"));
                int thumbH = Math.Max(12, trackH * SkillWindow.VisibleRows / rows.Count);
                int range = rows.Count - SkillWindow.VisibleRows;
                int thumbY = SkillWindow.ListY + (range <= 0 ? 0 : (trackH - thumbH) * window.ScrollRow / range);
                into.Add(OverlayArt.Place(art.Solid(6, thumbH, ThumbColor), trackX, thumbY, order++, "skill-thumb"));
            }
        }

        /// <summary>行里名字的颜色:满级黄、能再学的浅绿、学不了的暗灰;学会了但现在不能再升的(没点数、没等级)用白。</summary>
        public static uint NameColor(SkillRow row)
        {
            switch (row.State)
            {
                case SkillRowState.Maxed:
                    return OverlayArt.Yellow;
                case SkillRowState.Learnable:
                    return LearnableColor;
                default:
                    return row.Level > 0 ? OverlayArt.White : LockedColor;
            }
        }

        // ------------------------------------------------------------------ 右边:详情

        private static void BuildDetail(List<SceneSprite> into, ref int order, SkillWindow window, ISpriteSource sprites, OverlayArt art)
        {
            int dx = SkillWindow.DetailX, dy = SkillWindow.DetailY, dw = SkillWindow.DetailWidth, dh = SkillWindow.DetailHeight;
            into.Add(OverlayArt.Place(art.Solid(dw, dh, BagWindowArt.CellColor), dx, dy, order++, "skill-detail"));
            SkillRow row = window.SelectedRow;
            if (row == null)
            {
                return;
            }
            int innerX = dx + 8, innerW = dw - 16;
            int y = dy + 8;

            // ---- 图标与名字
            PlaceIcon(into, ref order, sprites, art, row, innerX, y, IconSize, "skill-icon", "skill-icon-block");
            SpriteFrame name = NameFrame(art, row, 2, OverlayArt.Yellow, innerW - IconSize - 8, 2);
            if (name != null)
            {
                into.Add(OverlayArt.Place(name, innerX + IconSize + 8, y, order++, "skill-name"));
            }
            y += Math.Max(IconSize, name == null ? 0 : name.Height) + 8;

            // ---- 等级、种类、下一级的花费与要求
            AddInfo(into, ref order, art, ref y, innerX, "LV " + row.Level.ToString(CultureInfo.InvariantCulture) + "/" + row.Cap.ToString(CultureInfo.InvariantCulture), OverlayArt.White);
            AddInfo(into, ref order, art, ref y, innerX, row.Special ? "AWAKENING SKILL" : row.Active ? "ACTIVE SKILL" : "PASSIVE SKILL", BagWindowArt.DimText);
            if (row.State != SkillRowState.Maxed)
            {
                AddInfo(into, ref order, art, ref y, innerX, NextCostText(row), OverlayArt.Orange);
                bool levelMet = window.Hero != null && window.Hero.Level >= row.NextHeroLevel;
                AddInfo(into, ref order, art, ref y, innerX, "NEED LEVEL " + row.NextHeroLevel.ToString(CultureInfo.InvariantCulture), levelMet ? OverlayArt.White : UnmetColor);
            }

            // ---- 前置技能(从下往上排,先量出它占多高,说明用上面剩下的)
            int bottom = dy + dh - 8;
            int prereqTop = bottom;
            if (row.Prereqs.Count > 0)
            {
                prereqTop = bottom - row.Prereqs.Count * LineHeight - LineHeight;
                int py = prereqTop;
                SpriteFrame head = art.Text("REQUIRES", 2, OverlayArt.Yellow);
                into.Add(OverlayArt.Place(head, innerX, py, order++, "skill-prereq-head"));
                py += LineHeight;
                foreach (SkillPrereqRow pre in row.Prereqs)
                {
                    SpriteFrame line = art.Text(PrereqText(art, pre), 2, pre.Met ? LearnableColor : UnmetColor);
                    if (line != null)
                    {
                        into.Add(OverlayArt.Place(line, innerX + 6, py, order++, "skill-prereq"));
                    }
                    py += LineHeight;
                }
            }

            // ---- 说明(数据文字;没有字体或取不到就不画)
            int lineHeight = art.GlyphLineHeight(ExplainScale);
            string explain = window.SelectedExplain;
            if (art.HasFonts && !string.IsNullOrEmpty(explain) && lineHeight > 0)
            {
                int lines = (prereqTop - y - 6) / lineHeight;
                if (lines >= 1)
                {
                    SpriteFrame text = art.Wrapped(explain, ExplainScale, ExplainColor, innerW, lines);
                    if (text != null)
                    {
                        into.Add(OverlayArt.Place(text, innerX, y + 2, order++, "skill-explain"));
                    }
                }
            }
        }

        private static void AddInfo(List<SceneSprite> into, ref int order, OverlayArt art, ref int y, int x, string text, uint color)
        {
            SpriteFrame frame = art.Text(text, 2, color);
            if (frame != null)
            {
                into.Add(OverlayArt.Place(frame, x, y, order++, "skill-info"));
            }
            y += LineHeight;
        }

        // ------------------------------------------------------------------ 快捷栏与按钮

        private static void BuildQuickBar(List<SceneSprite> into, ref int order, SkillWindow window, ISpriteSource sprites, OverlayArt art)
        {
            SkillRow selected = window.SelectedRow;
            for (int i = 0; i < SkillBook.QuickSlots; i++)
            {
                (int qx, int qy, int qw, int qh) = SkillWindow.QuickRect(i);
                if (selected != null && selected.Quick == i)
                {
                    into.Add(OverlayArt.Place(art.Solid(qw + 4, qh + 4, BagWindowArt.CursorColor), qx - 2, qy - 2, order++, "skill-quick-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(qw, qh, BagWindowArt.SlotColor), qx, qy, order++, "skill-quick"));
                int skillId = window.QuickAt(i);
                if (skillId > 0)
                {
                    SkillIconRef icon = window.IconOf(skillId);
                    if (icon != null)
                    {
                        PlaceFrame(into, ref order, sprites, icon.Image, icon.Frame, qx + (qw - IconSize) / 2, qy + (qh - IconSize) / 2, IconSize, "skill-quick-icon");
                    }
                }
                SpriteFrame key = art.Text(SlotKeys[i], 1, OverlayArt.Orange);
                into.Add(OverlayArt.Place(key, qx + 3, qy + 2, order++, "skill-quick-key"));
            }
        }

        private static void BuildButtons(List<SceneSprite> into, ref int order, SkillWindow window, OverlayArt art)
        {
            SkillRow row = window.SelectedRow;
            for (int i = 0; i < 3; i++)
            {
                (int bx, int by, int bw, int bh) = SkillWindow.ButtonRect(i);
                bool primary = (i == (int)SkillButton.Learn && row != null && row.State == SkillRowState.Learnable) || (i == (int)SkillButton.Reset && window.ResetArmed);
                into.Add(OverlayArt.Place(art.Solid(bw, bh, primary ? ButtonPrimaryColor : ButtonColor), bx, by, order++, primary ? "skill-button-primary" : "skill-button"));
                SpriteFrame label = art.Text(ButtonText((SkillButton)i), 2, primary ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, bx + (bw - label.Width) / 2, by + (bh - label.Height) / 2, order++, "skill-button-label"));
            }
        }

        // ------------------------------------------------------------------ 图标

        private static void PlaceIcon(List<SceneSprite> into, ref int order, ISpriteSource sprites, OverlayArt art, SkillRow row, int x, int y, int size, string tag, string blockTag)
        {
            // 学会了画亮图标;没学画灰图标(没有灰图标就画亮的)
            string image = row.Level > 0 ? row.IconImage : row.IconDisabledImage ?? row.IconImage;
            int frame = row.Level > 0 ? row.IconFrame : row.IconDisabledFrame >= 0 ? row.IconDisabledFrame : row.IconFrame;
            if (PlaceFrame(into, ref order, sprites, image, frame, x, y, size, tag))
            {
                return;
            }
            into.Add(OverlayArt.Place(art.Solid(size - 8, size - 8, row.Level > 0 ? OverlayArt.Orange : BagWindowArt.DimText), x + 4, y + 4, order++, blockTag));
        }

        /// <summary>把图标帧居中摆进 <paramref name="size"/> 见方的格子;取不到帧(没有素材库、没有这个图像)返回 false,什么也不加。</summary>
        private static bool PlaceFrame(List<SceneSprite> into, ref int order, ISpriteSource sprites, string image, int frameIndex, int x, int y, int size, string tag)
        {
            if (sprites == null || image == null || frameIndex < 0)
            {
                return false;
            }
            sprites.TryGetFrame("sprite/" + image.ToLowerInvariant(), frameIndex, out SpriteFrame frame);
            if (frame == null || frame.Width <= 0 || frame.Height <= 0 || frame.IsBlank)
            {
                return false;
            }
            into.Add(OverlayArt.Place(frame, x + (size - frame.CanvasWidth) / 2 + frame.OffsetX, y + (size - frame.CanvasHeight) / 2 + frame.OffsetY, order++, tag));
            return true;
        }

        // ------------------------------------------------------------------ 文字

        /// <summary>标题条右边的点数:"SP 技能点  AP 觉醒点"(占位英文;<see cref="UiText"/> 有中文)。</summary>
        public static string PointsText(int skillPoints, int awakeningPoints)
        {
            return "SP " + skillPoints.ToString(CultureInfo.InvariantCulture) + "  AP " + awakeningPoints.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>行里的等级:"学到几级/最高几级"。</summary>
        public static string LevelText(SkillRow row)
        {
            return row.Level.ToString(CultureInfo.InvariantCulture) + "/" + row.Cap.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>下一级的花费一行(占位英文):特殊技能花觉醒点(AP),其余花技能点(SP)。没有花费表写 "NEXT COST ?"。</summary>
        public static string NextCostText(SkillRow row)
        {
            string cost = row.NextCost < 0 ? "?" : row.NextCost.ToString(CultureInfo.InvariantCulture);
            return (row.Special ? "NEXT COST AP " : "NEXT COST SP ") + cost;
        }

        /// <summary>前置技能一行:技能名(数据文字;没有字体或取不到退回 <c>SKILL 编号</c>)加要求的等级。</summary>
        public static string PrereqText(OverlayArt art, SkillPrereqRow pre)
        {
            string name = art.HasFonts && !string.IsNullOrEmpty(pre.Name) ? pre.Name : "SKILL " + pre.SkillId.ToString(CultureInfo.InvariantCulture);
            return name + " LV " + pre.Level.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>按钮上的字(占位英文)。</summary>
        public static string ButtonText(SkillButton button)
        {
            switch (button)
            {
                case SkillButton.Learn:
                    return "LEARN";
                case SkillButton.Refund:
                    return "REFUND";
                default:
                    return "RESET";
            }
        }

        /// <summary>底部按键提示(占位英文)。</summary>
        public const string HintText = "UP DOWN SKILL   LEFT RIGHT PAGE   ENTER LEARN   BACK REFUND   R RESET   ASDFGH SLOT   DEL UNSLOT   K CLOSE";

        /// <summary>
        /// 技能名的精灵:有字体且读得到名字时用字体画(折行,最多 <paramref name="maxLines"/> 行);否则是占位的 <c>SKILL 编号</c>(点阵字库)。
        /// </summary>
        public static SpriteFrame NameFrame(OverlayArt art, SkillRow row, int scale, uint fill, int maxWidth, int maxLines)
        {
            if (art.HasFonts && !string.IsNullOrEmpty(row.Name))
            {
                SpriteFrame frame = art.Wrapped(row.Name, scale, fill, maxWidth, maxLines);
                if (frame != null)
                {
                    return frame;
                }
            }
            return art.Text("SKILL " + row.SkillId.ToString(CultureInfo.InvariantCulture), scale, fill);
        }
    }
}
