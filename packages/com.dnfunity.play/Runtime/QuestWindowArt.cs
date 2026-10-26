using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 任务窗口(<see cref="QuestWindow"/>)的画法:整屏压暗、居中的深色面板带一圈边、标题(NPC 名字或"任务");左边任务列表(每行一个状态方块——感叹号可接、问号可交、灰色问号进行中——和折行的任务名),
    /// 右边详情栏(任务名、等级要求、进度、折行的对白、奖励:经验与金币、固定物品、多选一的物品),底下按钮、结果文字与按键提示。
    /// 任务名与对白是数据文字,只在有字体集时画(<see cref="OverlayArt.HasFonts"/>);没有字体时任务名退回占位的 <c>QUEST 编号</c>、不画对白,窗口照样能操作。
    /// <b>本项目自己的占位版式</b>(原版任务界面没有读到),界面词是占位英文(<see cref="UiText"/> 有中文)。
    /// 标签:<c>quest-dim</c>、<c>quest-frame</c>、<c>quest-panel</c>、<c>quest-title-bar</c>、<c>quest-title</c>、<c>quest-row</c>/<c>quest-row-selected</c>、
    /// <c>quest-row-mark</c>/<c>quest-row-glyph</c>/<c>quest-row-name</c>、<c>quest-track</c>/<c>quest-thumb</c>、<c>quest-detail</c>、<c>quest-name</c>、<c>quest-info</c>、<c>quest-progress</c>、
    /// <c>quest-message</c>(对白)、<c>quest-reward-head</c>/<c>quest-reward-gold</c>/<c>quest-reward-item</c>、<c>quest-choice-head</c>/<c>quest-choice</c>/<c>quest-choice-selected</c>/<c>quest-choice-item</c>、
    /// <c>quest-empty</c>、<c>quest-button</c>/<c>quest-button-primary</c>/<c>quest-button-label</c>、<c>quest-status</c>、<c>quest-hint</c>。
    /// </summary>
    public static class QuestWindowArt
    {
        /// <summary>压暗的不透明度(0 到 255)。</summary>
        public const int DimAlpha = 150;

        /// <summary>走到有任务的 NPC 旁的提示条:画布 y 与底色不透明度(和商店的提示同一个位置)。</summary>
        public const int NearHintY = ShopWindowArt.NearHintY;
        public const int NearHintAlpha = ShopWindowArt.NearHintAlpha;

        /// <summary>状态方块的底色。</summary>
        public const uint AvailableColor = 0x7A5A10u;
        public const uint ReadyColor = 0x2E6B34u;
        public const uint InProgressColor = 0x3A3A48u;

        public const uint ButtonColor = ShopWindowArt.ButtonColor;
        public const uint ButtonPrimaryColor = ShopWindowArt.ButtonSelectedColor;
        public const uint TrackColor = ShopWindowArt.TrackColor;
        public const uint ThumbColor = ShopWindowArt.ThumbColor;

        /// <summary>对白的字色(淡一点)与数据文字的字号(点阵字库放大倍数 1 对应 12 像素字)。</summary>
        public const uint MessageColor = ItemDetailArt.ExplainColor;
        public const int MessageScale = 1;
        public const int NameScale = 2;
        public const int LineScale = 2;

        /// <summary>详情栏里信息行(等级、进度、奖励)的行高。</summary>
        public const int LineHeight = 22;

        /// <summary>奖励区最多画几行固定物品与几行多选一物品(再多的省掉,多选一仍可用左右键选)。</summary>
        public const int MaxRewardLines = 5;
        public const int MaxChoiceLines = 6;

        /// <summary>说明行(每行一个 y,画布像素;和背包、商店窗口同一组)。</summary>
        public const int MessageY = BagWindowArt.MessageY;
        public const int HintY = BagWindowArt.HintY;

        /// <summary>详情栏奖励区的下沿(最后一行画到这里)。</summary>
        public const int DetailBottom = QuestWindow.DetailY + QuestWindow.DetailHeight - 8;

        /// <summary>
        /// 多选一奖励第一件的上沿(画与点击判定共用):奖励区从详情栏下沿往上排,最后一件贴着下沿。
        /// </summary>
        public static int ChoiceTop(QuestRow row)
        {
            int shown = Math.Min(row == null ? 0 : row.Choices.Count, MaxChoiceLines);
            return DetailBottom - shown * QuestWindow.ChoiceHeight;
        }

        /// <summary>把窗口的精灵追加到 <paramref name="into"/>,<paramref name="order"/> 逐个加一;窗口没开时什么也不加。</summary>
        public static void Build(List<SceneSprite> into, ref int order, QuestWindow window, OverlayArt art)
        {
            if (window == null || !window.Open)
            {
                return;
            }
            int x = QuestWindow.WindowX, y = QuestWindow.WindowY, w = QuestWindow.WindowWidth, h = QuestWindow.WindowHeight;
            SpriteFrame black = art.Solid(QuestWindow.ScreenWidth, QuestWindow.ScreenHeight, OverlayArt.Black);
            into.Add(OverlayArt.PlaceDim(black, DimAlpha, order++, "quest-dim"));
            into.Add(OverlayArt.Place(art.Solid(w + 4, h + 4, BagWindowArt.FrameColor), x - 2, y - 2, order++, "quest-frame"));
            into.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "quest-panel"));
            into.Add(OverlayArt.Place(art.Solid(w, QuestWindow.TitleHeight, BagWindowArt.TitleColor), x, y, order++, "quest-title-bar"));
            SpriteFrame title = art.Text(TitleText(window, art), 3, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, x + 16, y + (QuestWindow.TitleHeight - title.Height) / 2, order++, "quest-title"));

            BuildList(into, ref order, window, art);
            BuildDetail(into, ref order, window, art);
            BuildButtons(into, ref order, window, art);

            if (!string.IsNullOrEmpty(window.Message))
            {
                BagWindowArt.AddLine(into, ref order, art, window.Message, QuestWindow.ListX, MessageY, OverlayArt.Yellow, "quest-status");
            }
            SpriteFrame hint = art.Text(HintText(window), 1, BagWindowArt.DimText);
            into.Add(OverlayArt.Place(hint, QuestWindow.ListX, HintY, order++, "quest-hint"));
        }

        /// <summary>同上,返回新建的列表(窗口没开时是空表)。</summary>
        public static List<SceneSprite> Build(QuestWindow window, OverlayArt art)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            Build(list, ref order, window, art);
            return list;
        }

        /// <summary>
        /// 城镇里主角走到有任务的 NPC 身边时画面下方的一行提示(占位英文"按空格对话",深色底条加黄字;宿主在没有窗口开着、能说话时才叠上去)。
        /// 标签:<c>quest-near-bg</c>、<c>quest-near-hint</c>。
        /// </summary>
        public static List<SceneSprite> BuildNearHint(OverlayArt art)
        {
            var list = new List<SceneSprite>();
            SpriteFrame text = art.Text("SPACE TALK", 2, OverlayArt.Yellow);
            int w = text.Width + 24, h = text.Height + 14;
            int x = (QuestWindow.ScreenWidth - w) / 2, y = NearHintY;
            int order = 0;
            list.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "quest-near-bg", NearHintAlpha));
            list.Add(OverlayArt.Place(text, x + 12, y + 7, order++, "quest-near-hint"));
            return list;
        }

        // ------------------------------------------------------------------ 左边:任务列表

        private static void BuildList(List<SceneSprite> into, ref int order, QuestWindow window, OverlayArt art)
        {
            IReadOnlyList<QuestRow> rows = window.Rows;
            if (rows.Count == 0)
            {
                SpriteFrame empty = art.Text("NO QUESTS", 2, BagWindowArt.DimText);
                into.Add(OverlayArt.Place(empty, QuestWindow.ListX + 8, QuestWindow.ListY + 12, order++, "quest-empty"));
                return;
            }
            for (int v = 0; v < QuestWindow.VisibleRows; v++)
            {
                int index = window.ScrollRow + v;
                if (index >= rows.Count)
                {
                    break;
                }
                QuestRow row = rows[index];
                (int rx, int ry, int rw, int rh) = QuestWindow.RowRect(v);
                if (index == window.Selected)
                {
                    into.Add(OverlayArt.Place(art.Solid(rw + 4, rh + 4, BagWindowArt.CursorColor), rx - 2, ry - 2, order++, "quest-row-selected"));
                }
                into.Add(OverlayArt.Place(art.Solid(rw, rh, BagWindowArt.CellColor), rx, ry, order++, "quest-row"));
                const int mark = 32;
                int mx = rx + 6, my = ry + (rh - mark) / 2;
                into.Add(OverlayArt.Place(art.Solid(mark, mark, StateColor(row.State)), mx, my, order++, "quest-row-mark"));
                SpriteFrame glyph = art.Text(row.State == QuestRowState.Available ? "!" : "?", 3, row.State == QuestRowState.InProgress ? BagWindowArt.DimText : OverlayArt.Yellow);
                into.Add(OverlayArt.Place(glyph, mx + (mark - glyph.Width) / 2, my + (mark - glyph.Height) / 2, order++, "quest-row-glyph"));
                int textX = mx + mark + 8;
                int textW = rx + rw - 6 - textX;
                SpriteFrame name = NameFrame(art, row, 1, row.State == QuestRowState.InProgress ? BagWindowArt.DimText : OverlayArt.White, textW, 2);
                if (name != null)
                {
                    into.Add(OverlayArt.Place(name, textX, ry + Math.Max(2, (rh - name.Height) / 2), order++, "quest-row-name"));
                }
            }
            if (rows.Count > QuestWindow.VisibleRows)
            {
                int trackX = QuestWindow.ListX + QuestWindow.ListWidth + 2;
                int trackH = QuestWindow.VisibleRows * (QuestWindow.RowHeight + QuestWindow.RowGap) - QuestWindow.RowGap;
                into.Add(OverlayArt.Place(art.Solid(6, trackH, TrackColor), trackX, QuestWindow.ListY, order++, "quest-track"));
                int thumbH = Math.Max(12, trackH * QuestWindow.VisibleRows / rows.Count);
                int range = rows.Count - QuestWindow.VisibleRows;
                int thumbY = QuestWindow.ListY + (range <= 0 ? 0 : (trackH - thumbH) * window.ScrollRow / range);
                into.Add(OverlayArt.Place(art.Solid(6, thumbH, ThumbColor), trackX, thumbY, order++, "quest-thumb"));
            }
        }

        /// <summary>状态方块的底色。</summary>
        public static uint StateColor(QuestRowState state)
        {
            switch (state)
            {
                case QuestRowState.Ready:
                    return ReadyColor;
                case QuestRowState.InProgress:
                    return InProgressColor;
                default:
                    return AvailableColor;
            }
        }

        // ------------------------------------------------------------------ 右边:详情

        private static void BuildDetail(List<SceneSprite> into, ref int order, QuestWindow window, OverlayArt art)
        {
            int dx = QuestWindow.DetailX, dy = QuestWindow.DetailY, dw = QuestWindow.DetailWidth;
            into.Add(OverlayArt.Place(art.Solid(dw, QuestWindow.DetailHeight, BagWindowArt.CellColor), dx, dy, order++, "quest-detail"));
            QuestRow row = window.SelectedRow;
            if (row == null)
            {
                return;
            }
            int innerX = dx + 8, innerW = dw - 16;
            int y = dy + 8;

            // ---- 任务名
            SpriteFrame name = NameFrame(art, row, NameScale, OverlayArt.Yellow, innerW, 2);
            if (name != null)
            {
                into.Add(OverlayArt.Place(name, innerX, y, order++, "quest-name"));
                y += name.Height + 4;
            }

            // ---- 等级要求与进度
            SpriteFrame info = art.Text("LV " + row.MinLevel.ToString(CultureInfo.InvariantCulture), LineScale, OverlayArt.White);
            into.Add(OverlayArt.Place(info, innerX, y, order++, "quest-info"));
            y += LineHeight;
            if (!string.IsNullOrEmpty(row.Progress))
            {
                SpriteFrame progress = art.Text("PROGRESS " + row.Progress, LineScale, OverlayArt.Orange);
                into.Add(OverlayArt.Place(progress, innerX, y, order++, "quest-progress"));
                y += LineHeight;
            }

            // ---- 奖励区(从下往上排,先量出它占多高,对白用上面剩下的)
            int rewardTop = BuildRewards(into, ref order, window, row, art, innerX, innerW);

            // ---- 对白
            int lineHeight = art.GlyphLineHeight(MessageScale);
            if (art.HasFonts && !string.IsNullOrEmpty(row.Message) && lineHeight > 0)
            {
                int lines = (rewardTop - y - 6) / lineHeight;
                if (lines >= 1)
                {
                    SpriteFrame message = art.Wrapped(row.Message, MessageScale, MessageColor, innerW, lines);
                    if (message != null)
                    {
                        into.Add(OverlayArt.Place(message, innerX, y + 2, order++, "quest-message"));
                    }
                }
            }
        }

        /// <summary>画奖励区并返回它的上沿(对白不能越过)。没有奖励时返回 <see cref="DetailBottom"/>。</summary>
        private static int BuildRewards(List<SceneSprite> into, ref int order, QuestWindow window, QuestRow row, OverlayArt art, int innerX, int innerW)
        {
            int fixedShown = Math.Min(row.Rewards.Count, MaxRewardLines);
            int choiceShown = Math.Min(row.Choices.Count, MaxChoiceLines);
            bool hasGold = row.Exp > 0 || row.Gold > 0;
            if (fixedShown == 0 && choiceShown == 0 && !hasGold)
            {
                return DetailBottom;
            }
            int y = DetailBottom;
            if (choiceShown > 0)
            {
                y -= choiceShown * QuestWindow.ChoiceHeight;
                bool pick = row.State == QuestRowState.Ready && row.Choices.Count > 1;
                for (int i = 0; i < choiceShown; i++)
                {
                    (int cx, int cy, int cw, int ch) = QuestWindow.ChoiceRect(y, i);
                    bool selected = pick && window.ChoiceIndex == i;
                    if (selected)
                    {
                        into.Add(OverlayArt.Place(art.Solid(cw + 4, ch + 4, BagWindowArt.CursorColor), cx - 2, cy - 2, order++, "quest-choice-selected"));
                    }
                    into.Add(OverlayArt.Place(art.Solid(cw, ch, BagWindowArt.SlotColor), cx, cy, order++, "quest-choice"));
                    SpriteFrame line = art.Text(RewardText(row.Choices[i], art), LineScale, OverlayArt.White);
                    if (line != null)
                    {
                        into.Add(OverlayArt.Place(line, cx + 6, cy + Math.Max(0, (ch - line.Height) / 2), order++, "quest-choice-item"));
                    }
                }
                y -= LineHeight;
                SpriteFrame head = art.Text("CHOOSE ONE", LineScale, OverlayArt.Yellow);
                into.Add(OverlayArt.Place(head, innerX, y, order++, "quest-choice-head"));
                y -= 4;
            }
            if (fixedShown > 0)
            {
                y -= fixedShown * LineHeight;
                for (int i = 0; i < fixedShown; i++)
                {
                    SpriteFrame line = art.Text(RewardText(row.Rewards[i], art), LineScale, OverlayArt.White);
                    if (line != null)
                    {
                        into.Add(OverlayArt.Place(line, innerX + 6, y + i * LineHeight, order++, "quest-reward-item"));
                    }
                }
            }
            if (hasGold)
            {
                y -= LineHeight;
                SpriteFrame gold = art.Text(GoldText(row), LineScale, OverlayArt.Orange);
                into.Add(OverlayArt.Place(gold, innerX + 6, y, order++, "quest-reward-gold"));
            }
            y -= LineHeight;
            SpriteFrame reward = art.Text("REWARD", LineScale, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(reward, innerX, y, order++, "quest-reward-head"));
            return y - 4;
        }

        // ------------------------------------------------------------------ 底部按钮

        private static void BuildButtons(List<SceneSprite> into, ref int order, QuestWindow window, OverlayArt art)
        {
            IReadOnlyList<QuestButton> buttons = window.Buttons;
            for (int i = 0; i < buttons.Count; i++)
            {
                (int bx, int by, int bw, int bh) = QuestWindow.ButtonRect(i, buttons.Count);
                bool primary = buttons[i] == QuestButton.Accept || buttons[i] == QuestButton.Complete || (buttons[i] == QuestButton.Abandon && window.AbandonArmed);
                into.Add(OverlayArt.Place(art.Solid(bw, bh, primary ? ButtonPrimaryColor : ButtonColor), bx, by, order++, primary ? "quest-button-primary" : "quest-button"));
                SpriteFrame label = art.Text(ButtonText(buttons[i]), 2, primary ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, bx + (bw - label.Width) / 2, by + (bh - label.Height) / 2, order++, "quest-button-label"));
            }
        }

        // ------------------------------------------------------------------ 文字

        /// <summary>标题:和 NPC 说话时是 NPC 的名字(数据文字,有字体且读得到时),否则是占位的"QUESTS"(任务)。</summary>
        public static string TitleText(QuestWindow window, OverlayArt art)
        {
            if (window.Mode == QuestWindowMode.Npc && art.HasFonts && !string.IsNullOrEmpty(window.NpcName))
            {
                return window.NpcName;
            }
            return "QUESTS";
        }

        /// <summary>按钮上的字(占位英文)。</summary>
        public static string ButtonText(QuestButton button)
        {
            switch (button)
            {
                case QuestButton.Accept:
                    return "ACCEPT";
                case QuestButton.Complete:
                    return "COMPLETE";
                case QuestButton.Shop:
                    return "SHOP";
                case QuestButton.Abandon:
                    return "ABANDON";
                default:
                    return "CLOSE";
            }
        }

        /// <summary>底部按键提示(占位英文),随模式变。</summary>
        public static string HintText(QuestWindow window)
        {
            return HintText(window.Mode, window.ShopIndex >= 0);
        }

        /// <summary>同上:日志一种;和 NPC 说话时按这个 NPC 有没有商店分两种。</summary>
        public static string HintText(QuestWindowMode mode, bool hasShop)
        {
            if (mode == QuestWindowMode.Journal)
            {
                return "UP DOWN SELECT   DEL ABANDON   L ESC CLOSE";
            }
            return hasShop
                ? "UP DOWN SELECT   ENTER ACCEPT OR COMPLETE   LEFT RIGHT REWARD   TAB SHOP   ESC CLOSE"
                : "UP DOWN SELECT   ENTER ACCEPT OR COMPLETE   LEFT RIGHT REWARD   ESC CLOSE";
        }

        /// <summary>
        /// 任务名的精灵:有字体且读得到名字时用字体画(折行,最多 <paramref name="maxLines"/> 行);否则是占位的 <c>QUEST 编号</c>(点阵字库)。
        /// </summary>
        public static SpriteFrame NameFrame(OverlayArt art, QuestRow row, int scale, uint fill, int maxWidth, int maxLines)
        {
            if (art.HasFonts && !string.IsNullOrEmpty(row.Name))
            {
                SpriteFrame frame = art.Wrapped(row.Name, scale, fill, maxWidth, maxLines);
                if (frame != null)
                {
                    return frame;
                }
            }
            return art.Text("QUEST " + row.Id.ToString(CultureInfo.InvariantCulture), scale, fill);
        }

        /// <summary>奖励行的字:物品名(数据文字;读不到或没有字体时占位 <c>ITEM 编号</c>)加数量。</summary>
        public static string RewardText(QuestRewardLine line, OverlayArt art)
        {
            string name = art.HasFonts && !string.IsNullOrEmpty(line.Name) ? line.Name : "ITEM " + line.Code.ToString(CultureInfo.InvariantCulture);
            return line.Count > 1 ? name + (art.HasFonts && !string.IsNullOrEmpty(line.Name) ? " x" : " X") + line.Count.ToString(CultureInfo.InvariantCulture) : name;
        }

        /// <summary>经验与金币一行(占位英文;两项都写,有一项是 0 也写)。</summary>
        public static string GoldText(QuestRow row)
        {
            return "EXP " + row.Exp.ToString(CultureInfo.InvariantCulture) + "  GOLD " + row.Gold.ToString(CultureInfo.InvariantCulture);
        }
    }
}
