using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 副本选择界面(<see cref="DungeonSelectScreen"/>)的联网增强与文本系统接入(r18-dungeonflow):
    /// 离线且无字体时不介入,保证离屏渲染与既有对拍逐像素不变。
    /// </summary>
    public static class DungeonSelectOverlay
    {
        public const int MaxNoticeWidth = 560;
        public const int NoticeY = 48;
        public const int DisabledOverlayAlpha = 150;

        /// <summary>
        /// 在 <see cref="WorldMapArt.Build"/> 输出的精灵列表上叠入:
        /// 1. 有字体时的系统字体文字替换(地区名、选中卡片等级文字、占位卡片中文名);
        /// 2. 不可进入条目的卡片灰显遮罩与状态角标;
        /// 3. 顶部非阻塞状态/拒绝原因提示条。
        /// </summary>
        public static void EnhanceWorldMap(
            List<SceneSprite> sprites,
            ref int order,
            OverlayArt art,
            IReadOnlyList<DungeonEntry> entries,
            int selected,
            long ageMs,
            long timeMs,
            Func<DungeonEntry, DungeonEntryStatus> entryStatus,
            Func<DungeonEntry, string> entryDisplayName,
            string regionDisplayName,
            DungeonSelectNotice notice)
        {
            if (sprites == null || art == null)
            {
                return;
            }
            var paint = new ShellPaint(art);
            bool hasFonts = art.HasFonts;

            if (hasFonts)
            {
                ApplyWorldMapTextSystem(sprites, paint, entries, selected, entryDisplayName, regionDisplayName);
            }

            int balloonAlpha = WorldMapArt.BalloonAlpha(ageMs);
            if (balloonAlpha > 0 && entryStatus != null && entries != null)
            {
                AddDisabledCardOverlays(sprites, art, paint, entries, selected, balloonAlpha, entryStatus, hasFonts);
            }

            DungeonEntry selectedEntry = entries != null && selected >= 0 && selected < entries.Count ? entries[selected] : null;
            DungeonEntryStatus selectedStatus = selectedEntry != null && entryStatus != null
                ? entryStatus(selectedEntry)
                : DungeonEntryStatus.Allowed;

            order = sprites.Count;
            AddNoticeBar(sprites, ref order, paint, notice, selectedStatus, timeMs, "select-online-notice");
        }

        /// <summary>画选择界面的顶部状态或选中项不可进入原因条。</summary>
        public static void AddNoticeBar(
            List<SceneSprite> sprites,
            ref int order,
            ShellPaint paint,
            DungeonSelectNotice notice,
            DungeonEntryStatus selectedStatus,
            long timeMs,
            string tag = "select-online-notice")
        {
            if (sprites == null || paint == null)
            {
                return;
            }
            bool hasFonts = paint.HasFonts;
            string text = null;
            ShellTone tone = ShellTone.Info;
            if (notice != null && notice.Kind != DungeonSelectNoticeKind.None)
            {
                text = notice.Message(hasFonts);
                tone = notice.Tone;
            }
            else if (selectedStatus.Disabled)
            {
                text = selectedStatus.ReasonText(hasFonts);
                tone = ShellTone.Error;
            }
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            int width = OnlineShellArt.NoticeBarWidth(paint, text, MaxNoticeWidth, icon: true);
            if (width <= 0)
            {
                return;
            }
            int x = (DungeonSelectScreen.ScreenWidth - width) / 2;
            OnlineShellArt.AddNoticeBar(sprites, ref order, paint, tone, text, x, NoticeY, width, timeMs, tag, icon: true);
        }

        private static void ApplyWorldMapTextSystem(
            List<SceneSprite> sprites,
            ShellPaint paint,
            IReadOnlyList<DungeonEntry> entries,
            int selected,
            Func<DungeonEntry, string> entryDisplayName,
            string regionDisplayName)
        {
            if (!string.IsNullOrEmpty(regionDisplayName))
            {
                int idx = FindIndex(sprites, "map-region-name");
                if (idx >= 0)
                {
                    SpriteFrame line = paint.Line(regionDisplayName, 14, WorldMapArt.TextColor, OverlayArt.Black, 160);
                    if (line != null)
                    {
                        int x = WorldMapArt.RegionTextRight - line.Width;
                        if (x < WorldMapArt.PartyIconX + 17)
                        {
                            x = WorldMapArt.PartyIconX - 3 - line.Width;
                        }
                        SceneSprite old = sprites[idx];
                        sprites[idx] = OverlayArt.Place(line, x, WorldMapArt.RegionTextY, old.SortKey, old.AnimationPath, old.TintAlpha);
                    }
                }
            }

            if (entries != null && selected >= 0 && selected < entries.Count && entries[selected].Card != null)
            {
                int idx = FindIndex(sprites, "map-level");
                if (idx >= 0)
                {
                    SceneSprite old = sprites[idx];
                    string text = WorldMapArt.LevelText(entries[selected].Card.MinimumLevel);
                    SpriteFrame line = paint.Line(text, 12, WorldMapArt.TextColor, OverlayArt.Black);
                    if (line != null)
                    {
                        sprites[idx] = OverlayArt.Place(line, old.ScreenX, old.ScreenY, old.SortKey, old.AnimationPath, old.TintAlpha);
                    }
                }
            }

            if (entryDisplayName != null && entries != null)
            {
                for (int s = 0; s < sprites.Count; s++)
                {
                    SceneSprite sprite = sprites[s];
                    if (sprite.AnimationPath != "map-card-name")
                    {
                        continue;
                    }
                    int matched = FindCardForPoint(entries, selected, sprite.ScreenX, sprite.ScreenY);
                    if (matched < 0)
                    {
                        continue;
                    }
                    string localized = entryDisplayName(entries[matched]);
                    if (string.IsNullOrEmpty(localized))
                    {
                        continue;
                    }
                    HitRect rect = WorldMapArt.CardRect(entries[matched].Card, matched == selected);
                    SpriteFrame line = paint.Line(localized, matched == selected ? 14 : 12, WorldMapArt.TextColor, OverlayArt.Black, rect.Width - 10);
                    if (line != null)
                    {
                        int y = rect.Y + (rect.Height - line.Height) / 2;
                        sprites[s] = OverlayArt.Place(line, rect.X + 6, y, sprite.SortKey, sprite.AnimationPath, sprite.TintAlpha);
                    }
                }
            }
        }

        private static void AddDisabledCardOverlays(
            List<SceneSprite> sprites,
            OverlayArt art,
            ShellPaint paint,
            IReadOnlyList<DungeonEntry> entries,
            int selected,
            int balloonAlpha,
            Func<DungeonEntry, DungeonEntryStatus> entryStatus,
            bool hasFonts)
        {
            var overlays = new List<SceneSprite>();
            int overlayDim = (int)Math.Round(DisabledOverlayAlpha * (balloonAlpha / 255.0), MidpointRounding.AwayFromZero);
            for (int i = 0; i < entries.Count; i++)
            {
                DungeonEntry entry = entries[i];
                if (entry == null || entry.Card == null || !entryStatus(entry).Disabled)
                {
                    continue;
                }
                bool isSelected = i == selected;
                HitRect rect = WorldMapArt.CardRect(entry.Card, isSelected);
                if (overlayDim > 0)
                {
                    overlays.Add(OverlayArt.Place(
                        art.Solid(rect.Width, rect.Height, OverlayArt.Black),
                        rect.X,
                        rect.Y,
                        0,
                        isSelected ? "map-card-disabled-selected" : "map-card-disabled",
                        overlayDim));
                }
                SpriteFrame badge = paint.Line(hasFonts ? "不可进入" : "LOCKED", isSelected ? 12 : 10, ShellPaint.Red, OverlayArt.Black, rect.Width - 8);
                if (badge != null)
                {
                    overlays.Add(OverlayArt.Place(
                        badge,
                        rect.X + (rect.Width - badge.Width) / 2,
                        rect.Y + (rect.Height - badge.Height) / 2,
                        0,
                        "map-card-disabled-tag",
                        balloonAlpha));
                }
            }
            if (overlays.Count == 0)
            {
                return;
            }
            int insertAt = FindIndex(sprites, "map-edges");
            if (insertAt < 0)
            {
                insertAt = sprites.Count;
            }
            sprites.InsertRange(insertAt, overlays);
            ReindexSortKeys(sprites);
        }

        private static int FindIndex(List<SceneSprite> sprites, string animationPath)
        {
            for (int i = 0; i < sprites.Count; i++)
            {
                if (string.Equals(sprites[i].AnimationPath, animationPath, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindCardForPoint(IReadOnlyList<DungeonEntry> entries, int selected, int x, int y)
        {
            if (selected >= 0 && selected < entries.Count && entries[selected].Card != null
                && WorldMapArt.CardRect(entries[selected].Card, true).Contains(x, y))
            {
                return selected;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (i != selected && entries[i].Card != null && WorldMapArt.CardRect(entries[i].Card, false).Contains(x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void ReindexSortKeys(List<SceneSprite> sprites)
        {
            for (int i = 0; i < sprites.Count; i++)
            {
                SceneSprite s = sprites[i];
                if (s.SortKey != i)
                {
                    sprites[i] = new SceneSprite(
                        s.Frame,
                        s.ScreenX,
                        s.ScreenY,
                        i,
                        s.NpcId,
                        s.AnimationPath,
                        s.AniFrame,
                        s.Mirrored,
                        s.Blend,
                        s.TintAlpha,
                        s.LayerRank);
                }
            }
        }
    }

    /// <summary>进出联网副本时的非阻塞叠层提示(加载期、放弃回城中、回城超时/中止后短暂停留)。</summary>
    public static class OnlineDungeonOverlay
    {
        public const int MaxNoticeWidth = 560;
        public const int DefaultNoticeY = 48;
        public const int TownNoticeY = 88;

        /// <summary>生成进出副本提示条精灵;没有需要展示的提示返回 null。</summary>
        public static List<SceneSprite> Build(OverlayArt art, DungeonSelectNotice notice, long timeMs, bool inTown = false)
        {
            if (art == null || notice == null || notice.Kind == DungeonSelectNoticeKind.None)
            {
                return null;
            }
            var paint = new ShellPaint(art);
            string text = notice.Message(paint.HasFonts);
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            int width = OnlineShellArt.NoticeBarWidth(paint, text, MaxNoticeWidth, icon: true);
            if (width <= 0)
            {
                return null;
            }
            var sprites = new List<SceneSprite>(6);
            int order = 0;
            int x = (DungeonSelectScreen.ScreenWidth - width) / 2;
            int y = inTown ? TownNoticeY : DefaultNoticeY;
            OnlineShellArt.AddNoticeBar(sprites, ref order, paint, notice.Tone, text, x, y, width, timeMs, "onlinedungeon-notice", icon: true);
            return sprites.Count == 0 ? null : sprites;
        }
    }
}
