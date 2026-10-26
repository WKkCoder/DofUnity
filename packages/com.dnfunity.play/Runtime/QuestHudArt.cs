using System;
using System.Collections.Generic;
using Dnf.Render;

// 本项目自己的占位版式(M31):原版的任务提示、追踪与 NPC 头顶标记的版式没有量过,下面的位置、颜色是自己定的。
// 感叹号、问号用点阵字库画(没有字体也看得见);提示条与追踪里的任务名是数据文字,只在有字体时画出来。
namespace Dnf.Play
{
    /// <summary>
    /// 任务相关的画面小件:NPC 头顶的标记(<see cref="AddNpcMarks"/>:黄色感叹号可接、黄色问号可交、灰色问号进行中,轻轻上下浮动)、
    /// 屏幕上方居中的提示条(<see cref="BuildToasts"/>:接了、有进展、可以交了、完成了、拿到任务物品、背包满了)、
    /// 右上角的任务追踪(<see cref="BuildTracker"/>:最多三个任务与进度)。
    /// 标签:<c>quest-mark</c>、<c>quest-toast-bg</c>/<c>quest-toast-title</c>/<c>quest-toast-detail</c>/<c>quest-toast-extra</c>、
    /// <c>quest-tracker-bg</c>/<c>quest-tracker-title</c>/<c>quest-tracker-name</c>/<c>quest-tracker-progress</c>/<c>quest-tracker-dot</c>。
    /// </summary>
    public static class QuestHudArt
    {
        /// <summary>追踪的标题(占位英文,和任务窗口同一个词)。</summary>
        public const string TrackerTitle = "QUESTS";

        /// <summary>标记离名牌上沿的空隙、名牌占的高度(有字体时名牌画在精灵上方 5 行之上,见 <see cref="NamePlates"/>)与标记的字号。</summary>
        public const int MarkGap = 3;
        public const int PlateHeight = 14;
        public const int MarkScale = 3;

        /// <summary>标记上下浮动的幅度(像素)与周期(毫秒)。</summary>
        public const int BobAmplitude = 2;
        public const double BobPeriodMs = 1400.0;

        public const uint MarkAvailableColor = OverlayArt.Yellow;
        public const uint MarkReadyColor = OverlayArt.Orange;
        public const uint MarkInProgressColor = BagWindowArt.DimText;

        /// <summary>提示条:顶边 y、条与条的间隔、底色不透明度、内边距。</summary>
        public const int ToastY = 92;
        public const int ToastGap = 6;
        public const int ToastAlpha = 200;
        public const int ToastPadX = 14;
        public const int ToastPadY = 6;

        /// <summary>追踪:右边距、顶边 y、宽、每行高、底色不透明度。</summary>
        public const int TrackerRight = 792;
        public const int TrackerY = 132;
        public const int TrackerWidth = 232;
        public const int TrackerRowHeight = 20;
        public const int TrackerAlpha = 150;

        // ------------------------------------------------------------------ NPC 头顶的标记

        /// <summary>标记用的字(感叹号可接、问号其余)。</summary>
        public static string MarkGlyph(QuestMark mark)
        {
            return mark == QuestMark.Available ? "!" : "?";
        }

        /// <summary>标记的颜色。</summary>
        public static uint MarkColor(QuestMark mark)
        {
            switch (mark)
            {
                case QuestMark.Available:
                    return MarkAvailableColor;
                case QuestMark.Ready:
                    return MarkReadyColor;
                default:
                    return MarkInProgressColor;
            }
        }

        /// <summary>
        /// 给城镇里每个有任务标记的 NPC 加头顶标记:水平居中在 NPC 的世界 x(同名牌),底边在名牌上沿再往上 <see cref="MarkGap"/> 行
        /// (没有字体、没画名牌时紧贴精灵上沿上方);<paramref name="timeMs"/> 用来算上下浮动。<paramref name="markOf"/> 按 NPC 编号给标记。
        /// 画面里没有精灵的 NPC 跳过。返回加了几个。
        /// </summary>
        public static int AddNpcMarks(List<SceneSprite> into, ref int order, OverlayArt art, Func<int, QuestMark> markOf, IReadOnlyList<SceneSprite> scene,
            IReadOnlyList<TownNpc> npcs, int cameraX, double timeMs)
        {
            if (art == null || markOf == null || scene == null || npcs == null)
            {
                return 0;
            }
            int bob = (int)Math.Round(Math.Sin(timeMs / BobPeriodMs * 2.0 * Math.PI) * BobAmplitude);
            int lift = NamePlates.NpcInkAboveSprite + (art.HasFonts ? PlateHeight : 0) + MarkGap;
            int added = 0;
            foreach (TownNpc npc in npcs)
            {
                QuestMark mark = markOf(npc.Id);
                if (mark == QuestMark.None)
                {
                    continue;
                }
                SceneSprite sprite = NamePlates.SpriteOf(scene, npc, cameraX);
                if (sprite == null)
                {
                    continue;
                }
                SpriteFrame frame = art.Text(MarkGlyph(mark), MarkScale, MarkColor(mark));
                int anchorX = npc.X - cameraX + NamePlates.NpcCenterShift;
                into.Add(OverlayArt.Place(frame, anchorX - frame.Width / 2, sprite.ScreenY - lift - frame.Height + bob, order++, "quest-mark"));
                added++;
            }
            return added;
        }

        // ------------------------------------------------------------------ 提示条

        /// <summary>提示的标题(占位英文;<see cref="QuestNotice.Title"/> 里带的就是这些词,这里给没有通知对象时用)。</summary>
        public static string ToastTitle(QuestNoticeKind kind)
        {
            switch (kind)
            {
                case QuestNoticeKind.Accepted:
                    return "QUEST ACCEPTED";
                case QuestNoticeKind.Progress:
                    return "QUEST";
                case QuestNoticeKind.Ready:
                    return "QUEST READY";
                case QuestNoticeKind.Completed:
                    return "QUEST COMPLETE";
                case QuestNoticeKind.Abandoned:
                    return "QUEST ABANDONED";
                case QuestNoticeKind.ItemFound:
                    return "QUEST ITEM";
                default:
                    return "BAG FULL";
            }
        }

        /// <summary>标题的颜色。</summary>
        public static uint ToastColor(QuestNoticeKind kind)
        {
            switch (kind)
            {
                case QuestNoticeKind.Ready:
                case QuestNoticeKind.Completed:
                case QuestNoticeKind.Accepted:
                    return OverlayArt.Yellow;
                case QuestNoticeKind.BagFull:
                    return OverlayArt.Orange;
                default:
                    return OverlayArt.White;
            }
        }

        /// <summary>把正在显示的提示条追加到 <paramref name="into"/>(从上往下,最老的在最上面)。没有提示什么也不加。</summary>
        public static void BuildToasts(List<SceneSprite> into, ref int order, QuestHud hud, OverlayArt art)
        {
            if (hud == null || art == null || hud.Toasts.Count == 0)
            {
                return;
            }
            int y = ToastY;
            foreach (QuestHud.Toast toast in hud.Toasts)
            {
                int alpha = toast.Alpha;
                if (alpha <= 0)
                {
                    continue;
                }
                QuestNotice n = toast.Notice;
                SpriteFrame title = art.Text(n.Title.Length > 0 ? n.Title : ToastTitle(n.Kind), 2, ToastColor(n.Kind));
                SpriteFrame detail = DetailFrame(art, n.Detail);
                SpriteFrame extra = n.Extra.Length == 0 ? null : art.Text(n.Extra, 2, OverlayArt.Orange);
                int width = title.Width;
                int height = title.Height;
                if (detail != null)
                {
                    width = Math.Max(width, detail.Width);
                    height += detail.Height + 2;
                }
                if (extra != null)
                {
                    width = Math.Max(width, extra.Width);
                    height += extra.Height + 2;
                }
                int w = width + ToastPadX * 2, h = height + ToastPadY * 2;
                int x = (QuestWindow.ScreenWidth - w) / 2;
                into.Add(OverlayArt.Place(art.Solid(w, h, BagWindowArt.PanelColor), x, y, order++, "quest-toast-bg", Math.Min(alpha, ToastAlpha)));
                int ty = y + ToastPadY;
                into.Add(OverlayArt.Place(title, x + (w - title.Width) / 2, ty, order++, "quest-toast-title", alpha));
                ty += title.Height + 2;
                if (detail != null)
                {
                    into.Add(OverlayArt.Place(detail, x + (w - detail.Width) / 2, ty, order++, "quest-toast-detail", alpha));
                    ty += detail.Height + 2;
                }
                if (extra != null)
                {
                    into.Add(OverlayArt.Place(extra, x + (w - extra.Width) / 2, ty, order++, "quest-toast-extra", alpha));
                }
                y += h + ToastGap;
            }
        }

        /// <summary>提示里的任务名或物品名(数据文字):没有字体时画不出来(返回 null),不画这一行。</summary>
        private static SpriteFrame DetailFrame(OverlayArt art, string detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return null;
            }
            return art.HasFonts ? art.Text(detail, 2, OverlayArt.White) : null;
        }

        // ------------------------------------------------------------------ 追踪

        /// <summary>
        /// 把右上角的任务追踪追加到 <paramref name="into"/>:一个小标题加每个任务一行(名字在左、进度在右;可以交的名字是黄色)。
        /// 没有在追踪的任务什么也不加。
        /// </summary>
        public static void BuildTracker(List<SceneSprite> into, ref int order, QuestHud hud, OverlayArt art)
        {
            if (hud == null || art == null || hud.Tracked.Count == 0)
            {
                return;
            }
            int x = TrackerRight - TrackerWidth;
            SpriteFrame title = art.Text(TrackerTitle, 1, OverlayArt.Yellow);
            int height = title.Height + 4 + hud.Tracked.Count * TrackerRowHeight + 4;
            into.Add(OverlayArt.Place(art.Solid(TrackerWidth, height, BagWindowArt.PanelColor), x, TrackerY, order++, "quest-tracker-bg", TrackerAlpha));
            into.Add(OverlayArt.Place(title, x + 6, TrackerY + 3, order++, "quest-tracker-title"));
            int y = TrackerY + 3 + title.Height + 4;
            foreach (QuestRow row in hud.Tracked)
            {
                bool ready = row.State == QuestRowState.Ready;
                uint color = ready ? OverlayArt.Yellow : OverlayArt.White;
                SpriteFrame progress = string.IsNullOrEmpty(row.Progress) ? null : art.Text(row.Progress, 2, OverlayArt.Orange);
                int nameWidth = TrackerWidth - 12 - (progress == null ? 0 : progress.Width + 8);
                SpriteFrame name = QuestWindowArt.NameFrame(art, row, 1, color, nameWidth, 1);
                if (name != null)
                {
                    into.Add(OverlayArt.Place(name, x + 6, y + Math.Max(0, (TrackerRowHeight - name.Height) / 2), order++, "quest-tracker-name"));
                }
                if (progress != null)
                {
                    into.Add(OverlayArt.Place(progress, x + TrackerWidth - 6 - progress.Width, y + Math.Max(0, (TrackerRowHeight - progress.Height) / 2), order++, "quest-tracker-progress"));
                }
                y += TrackerRowHeight;
            }
        }

        /// <summary>提示条加追踪,各自没有内容时什么也不加(宿主一次叠上去)。</summary>
        public static List<SceneSprite> Build(QuestHud hud, OverlayArt art, bool tracker)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            if (tracker)
            {
                BuildTracker(list, ref order, hud, art);
            }
            BuildToasts(list, ref order, hud, art);
            return list;
        }
    }
}
