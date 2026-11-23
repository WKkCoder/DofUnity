using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>联网结算画面的绘制输入快照(纯数据,便于无头与像素级测试)。</summary>
    public sealed class OnlineSettlementRenderState
    {
        public OnlineSettlementSummary Summary { get; set; }
        public OnlineSettlementNotice Notice { get; set; }
        public double AgeMs { get; set; }
        public int HoverRow { get; set; } = -1;
        public Func<SettlementChoice, bool> IsChoiceEnabled { get; set; }
        public long RemainingCountdownMs { get; set; }
        public bool ShowCountdown { get; set; }
        public ClearCamp Camp { get; set; }
        public TownProjection? Projection { get; set; }
        public DnfAssets Assets { get; set; }
        public ISpriteSource Sprites { get; set; }
    }

    /// <summary>
    /// 联网结算画面构建器(r18-settleui):在 <see cref="HostedRoomRun.Build"/> 期间输出与离线同层次的精灵列表:
    /// 两层黑幕(<see cref="OverlayArt.BackDimRank"/> 与 <see cref="OverlayArt.DimRank"/>)、营火与德利拉(<see cref="ClearCamp"/>)、
    /// 右上角原版“是否继续?”菜单(<see cref="SettlementArt"/> + 灰显神秘商店占位 + 自动回城倒计时)、
    /// 左侧本地评分字母(<see cref="ResultArt"/>)与服务端战果明细、以及顶部非阻塞提示条。
    /// 绝不绘制标为 <see cref="SettlementFieldConfidence.Unknown"/> 的字段。
    /// </summary>
    public static class OnlineSettlementOverlay
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;
        public const int ResultLeft = 24;
        public const int ResultTop = 24;
        public const int MaxNoticeWidth = 560;
        public const int NoticeY = 48;
        public const int MaxDropsDisplayed = 5;
        public const int MaxBonusExpDisplayed = 4;

        /// <summary>根据结算停留时长计算单层压暗不透明度(沿用离线 500ms 线性淡入至 56)。</summary>
        public static int DimAlpha(double ageMs)
        {
            if (ageMs <= 0.0)
            {
                return 0;
            }
            double t = ageMs / DungeonSession.SettlementDimMs;
            return (int)Math.Round(DungeonSession.SettlementDimAlpha * (t >= 1.0 ? 1.0 : t), MidpointRounding.AwayFromZero);
        }

        /// <summary>把毫秒格式化为 <c>MM:SS</c>;负值夹到 0。</summary>
        public static string FormatTimeMs(int ms)
        {
            int safeMs = ms < 0 ? 0 : ms;
            int totalSeconds = safeMs / 1000;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>构建左侧战果文案行(纯函数,便于单元测试逐行核对“服务端优先/未知不画/经验不相加/占位”)。</summary>
        public static List<string> BuildResultLines(OnlineSettlementSummary summary, bool hasFonts, bool rankLetterDrawn)
        {
            var lines = new List<string>(14);
            if (summary == null)
            {
                return lines;
            }
            var inv = CultureInfo.InvariantCulture;

            if (summary.HasRank && !rankLetterDrawn)
            {
                lines.Add((hasFonts ? "评分 " : "RANK ") + summary.RankLetter);
            }

            if (summary.ClearTimeMs.ShouldDisplay)
            {
                lines.Add((hasFonts ? "通关用时 " : "CLEAR TIME ") + FormatTimeMs(summary.ClearTimeMs.Value));
            }
            else
            {
                lines.Add(hasFonts ? "通关用时 --:--" : "CLEAR TIME --:--");
            }

            if (summary.PersonalBestTimeMs.ShouldDisplay)
            {
                string best = (hasFonts ? "个人最佳 " : "BEST TIME ") + FormatTimeMs(summary.PersonalBestTimeMs.Value);
                if (summary.IsNewRecord.ShouldDisplay && summary.IsNewRecord.Value)
                {
                    best += hasFonts ? " [新纪录!]" : " [NEW RECORD!]";
                }
                lines.Add(best);
            }

            if (summary.Difficulty > 0)
            {
                lines.Add((hasFonts ? "难度 " : "DIFFICULTY ") + DifficultyNames.Of(summary.Difficulty));
            }

            if (summary.Kills.ShouldDisplay)
            {
                string killLine = (hasFonts ? "击杀 " : "KILLS ") + summary.Kills.Value.ToString(inv);
                if (summary.DroppedKills > 0)
                {
                    killLine += hasFonts
                        ? " (未计 " + summary.DroppedKills.ToString(inv) + ")"
                        : " (DROPPED " + summary.DroppedKills.ToString(inv) + ")";
                }
                lines.Add(killLine);
            }

            if (summary.BaseExp.ShouldDisplay)
            {
                lines.Add((hasFonts ? "通关基础经验 +" : "CLEAR EXP +") + summary.BaseExp.Value.ToString(inv));
            }
            else if (summary.RewardParseState == OnlineSettlementParseState.Failed || summary.IsTimedOut)
            {
                lines.Add(hasFonts ? "通关基础经验 --" : "CLEAR EXP --");
            }

            if (summary.RankBonusExp.ShouldDisplay && summary.RankBonusExp.Value > 0)
            {
                lines.Add((hasFonts ? "评分加成经验 +" : "RANK BONUS +") + summary.RankBonusExp.Value.ToString(inv));
            }

            if (summary.NamedBonusExpLines != null && summary.NamedBonusExpLines.Count > 0)
            {
                int shown = 0;
                for (int i = 0; i < summary.NamedBonusExpLines.Count && shown < MaxBonusExpDisplayed; i++)
                {
                    OnlineSettlementExpLine expLine = summary.NamedBonusExpLines[i];
                    if (expLine.ShouldDisplay)
                    {
                        lines.Add(expLine.DisplayLabel(hasFonts));
                        shown++;
                    }
                }
            }

            if (summary.HasDeadUserPenalty.ShouldDisplay && summary.HasDeadUserPenalty.Value)
            {
                lines.Add(hasFonts ? "已计死亡经验惩罚" : "DEAD PENALTY APPLIED");
            }

            if (summary.IsReturnUserParty.ShouldDisplay && summary.IsReturnUserParty.Value)
            {
                lines.Add(hasFonts ? "回归用户队伍" : "RETURN USER PARTY");
            }

            if (summary.RewardGold.ShouldDisplay)
            {
                lines.Add((hasFonts ? "奖励金币(推) +" : "REWARD GOLD(EST) +") + summary.RewardGold.Value.ToString(inv));
            }

            if (summary.Drops != null && summary.Drops.Count > 0)
            {
                int shown = Math.Min(summary.Drops.Count, MaxDropsDisplayed);
                for (int i = 0; i < shown; i++)
                {
                    OnlineSettlementDropItem drop = summary.Drops[i];
                    string prefix = drop.IsQuestItem
                        ? (hasFonts ? "任务道具: " : "QUEST ITEM: ")
                        : (hasFonts ? "奖励物品: " : "DROP: ");
                    lines.Add(prefix + drop.DisplayLabel(hasFonts));
                }
                if (summary.Drops.Count > shown)
                {
                    int more = summary.Drops.Count - shown;
                    lines.Add(hasFonts
                        ? "其余掉落 +" + more.ToString(inv) + " 项"
                        : "MORE DROPS +" + more.ToString(inv));
                }
            }
            else if (summary.RewardParseState == OnlineSettlementParseState.Succeeded)
            {
                lines.Add(hasFonts ? "掉落物品: 无" : "DROPS: NONE");
            }
            else
            {
                lines.Add(hasFonts ? "掉落物品: --" : "DROPS: --");
            }

            if (summary.LevelsGained > 0)
            {
                lines.Add((hasFonts ? "升级至 Lv." : "LEVELED UP TO ") + summary.LevelAfter.ToString(inv));
            }

            lines.Add(hasFonts ? "提示: 物品/金币暂未同步至本地" : "NOTE: ITEMS/GOLD UNSYNCED");
            return lines;
        }

        /// <summary>构建完整的联网结算画面精灵列表(供 <see cref="HostedRoomRun.HudOverlay"/> 注入)。</summary>
        public static List<SceneSprite> Build(OverlayArt art, OnlineSettlementRenderState state, long timeMs)
        {
            if (art == null || state == null || state.Summary == null)
            {
                return null;
            }
            var hud = new List<SceneSprite>(48);
            int order = 0;
            var paint = new ShellPaint(art);
            bool hasFonts = art.HasFonts;

            // 1. 两层黑幕(与离线 DungeonSession.AddSettlement 同一层次)
            int dim = DimAlpha(state.AgeMs);
            if (dim > 0)
            {
                SpriteFrame black = art.Solid(ScreenWidth, ScreenHeight, OverlayArt.Black);
                hud.Add(OverlayArt.PlaceDim(black, dim, order++, "settle-dim-back", OverlayArt.BackDimRank));
                hud.Add(OverlayArt.PlaceDim(black, dim, order++, "settle-dim"));
            }

            // 2. 营火与德利拉(在两层黑幕之间/之上,与离线一致)
            if (state.Camp != null && state.Assets != null && state.Projection.HasValue)
            {
                state.Camp.AddSprites(hud, state.Assets, state.Projection.Value);
                order = hud.Count;
            }

            // 3. 右上角原版结算菜单面板(使用 SettlementArt 数据注入重载)
            ISpriteSource sprites = state.Sprites ?? state.Assets;
            SettlementArt panel = SettlementArt.For(sprites);
            if (panel != null)
            {
                panel.Build(hud, ref order, art, state.HoverRow, state.IsChoiceEnabled);
            }
            else
            {
                SettlementArt.BuildFallback(hud, ref order, art, state.HoverRow, state.IsChoiceEnabled);
            }

            // 4. 菜单下方:灰显神秘商店(本线不做)与自动回城倒计时
            AddMenuExtras(hud, ref order, art, paint, state, hasFonts);

            // 5. 左侧战果面板(本地评分字母 + 服务端战果)
            AddResultPanel(hud, ref order, art, paint, sprites, state.Summary, hasFonts);

            // 6. 顶部非阻塞状态/拒绝/超时提示条
            if (state.Notice != null && state.Notice.Kind != OnlineSettlementNoticeKind.None)
            {
                string msg = state.Notice.Message(hasFonts);
                int width = OnlineShellArt.NoticeBarWidth(paint, msg, MaxNoticeWidth, icon: true);
                if (width > 0)
                {
                    int x = (ScreenWidth - width) / 2;
                    OnlineShellArt.AddNoticeBar(hud, ref order, paint, state.Notice.Tone, msg, x, NoticeY, width, timeMs, "onlinesettle-notice", icon: true);
                }
            }

            return hud;
        }

        private static void AddMenuExtras(
            List<SceneSprite> hud,
            ref int order,
            OverlayArt art,
            ShellPaint paint,
            OnlineSettlementRenderState state,
            bool hasFonts)
        {
            int y = SettlementArt.FooterY + SettlementArt.FooterHeight + 6;
            const int BarHeight = 24;

            // 神秘商店按钮(灰显禁用,本线不做)
            hud.Add(OverlayArt.Place(art.Solid(SettlementArt.PanelWidth, BarHeight, 0x161A22u), SettlementArt.PanelX, y, order++, "settle-secretshop-bg", 190));
            string shopText = hasFonts ? "神秘商店 (本阶段未开放)" : "SECRET SHOP (LOCKED)";
            SpriteFrame shopFrame = DrawLine(art, paint, shopText, 12, 0x7A8290u, SettlementArt.PanelWidth - 16, hasFonts);
            if (shopFrame != null)
            {
                hud.Add(OverlayArt.Place(shopFrame, SettlementArt.PanelX + 10, y + (BarHeight - shopFrame.Height) / 2, order++, "settle-secretshop-disabled"));
            }
            y += BarHeight + 4;

            // 60s 内自动回城倒计时条
            if (state.ShowCountdown)
            {
                long remainMs = state.RemainingCountdownMs < 0 ? 0 : state.RemainingCountdownMs;
                int remainSec = (int)((remainMs + 999) / 1000);
                string countText = hasFonts
                    ? "自动返回城镇倒计时: " + remainSec.ToString(CultureInfo.InvariantCulture) + " 秒"
                    : "AUTO RETURN TO TOWN: " + remainSec.ToString(CultureInfo.InvariantCulture) + "S";
                uint color = remainSec <= 10 ? 0xFFB060u : 0xFFE9A0u;
                hud.Add(OverlayArt.Place(art.Solid(SettlementArt.PanelWidth, BarHeight, 0x101826u), SettlementArt.PanelX, y, order++, "settle-countdown-bg", 200));
                SpriteFrame countFrame = DrawLine(art, paint, countText, 12, color, SettlementArt.PanelWidth - 16, hasFonts);
                if (countFrame != null)
                {
                    hud.Add(OverlayArt.Place(countFrame, SettlementArt.PanelX + 10, y + (BarHeight - countFrame.Height) / 2, order++, "settle-countdown"));
                }
            }
        }

        private static void AddResultPanel(
            List<SceneSprite> hud,
            ref int order,
            OverlayArt art,
            ShellPaint paint,
            ISpriteSource sprites,
            OnlineSettlementSummary summary,
            bool hasFonts)
        {
            int y = ResultTop;
            bool rankDrawn = false;
            if (summary.HasRank)
            {
                SceneSprite letter = ResultArt.PlaceRankLetter(sprites, summary.RankFrame, ResultLeft, order);
                if (letter != null)
                {
                    hud.Add(letter);
                    order++;
                    y = ResultArt.TextTop;
                    rankDrawn = true;
                }
            }

            List<string> lines = BuildResultLines(summary, hasFonts, rankDrawn);
            for (int i = 0; i < lines.Count; i++)
            {
                uint color = i == lines.Count - 1 ? 0xB8C6DAu : OverlayArt.White;
                SpriteFrame line = DrawLine(art, paint, lines[i], 14, color, 460, hasFonts);
                if (line != null)
                {
                    hud.Add(OverlayArt.Place(line, ResultLeft, y, order++, "online-settle-reward"));
                    y += line.Height + 5;
                }
            }
        }

        private static SpriteFrame DrawLine(OverlayArt art, ShellPaint paint, string text, int fontSize, uint color, int maxWidth, bool hasFonts)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            if (hasFonts)
            {
                return paint.Line(text, fontSize, color, OverlayArt.Black, maxWidth);
            }
            return art.Text(text, fontSize >= 14 ? 2 : 1, color);
        }
    }
}
