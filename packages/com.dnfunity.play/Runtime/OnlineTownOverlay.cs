using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网城镇的状态叠层(借用 <see cref="OnlineShellArt"/> 与 <see cref="ShellPaint"/>,零新素材路径):
    /// <list type="bullet">
    /// <item>左上角常驻小条:明确标出当前道具/装备/金币暂未同步,并在非鬼剑士时明示“暂按鬼剑士显示”;</item>
    /// <item>切区同步等待、被拒、剩余开放分钟数或未映射时的非阻塞提示条。</item>
    /// </list>
    /// </summary>
    public static class OnlineTownOverlay
    {
        public const int MaxBadgeWidth = 520;
        public const int MaxPromptWidth = 560;
        public const int BadgeX = 12;
        public const int BadgeY = 10;
        public const int PromptY = 48;

        /// <summary>生成联网城镇的叠层精灵;外部没有任何需要画的内容时返回 null。</summary>
        public static List<SceneSprite> Build(OverlayArt art, OnlineHeroProfile profile, OnlineTownPrompt prompt, long timeMs)
        {
            if (art == null || profile == null)
            {
                return null;
            }
            var paint = new ShellPaint(art);
            var sprites = new List<SceneSprite>(16);
            int order = 0;

            bool hasFonts = art.HasFonts;
            string badge = StatusBadgeText(profile, hasFonts);
            int badgeW = OnlineShellArt.NoticeBarWidth(paint, badge, MaxBadgeWidth, icon: true);
            if (badgeW > 0)
            {
                OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Info, badge, BadgeX, BadgeY, badgeW, timeMs, "onlinetown-status", icon: true);
            }

            if (prompt != null && prompt.Kind != OnlineTownPromptKind.None)
            {
                string promptText = prompt.Message(hasFonts);
                ShellTone tone = ToneOf(prompt.Kind);
                int promptW = OnlineShellArt.NoticeBarWidth(paint, promptText, MaxPromptWidth, icon: true);
                if (promptW > 0)
                {
                    int x = (OnlineShellArt.ScreenWidth - promptW) / 2;
                    OnlineShellArt.AddNoticeBar(sprites, ref order, paint, tone, promptText, x, PromptY, promptW, timeMs, "onlinetown-prompt", icon: true);
                }
            }

            return sprites.Count == 0 ? null : sprites;
        }

        /// <summary>返回明确的未同步/职业退回说明文本。</summary>
        public static string StatusBadgeText(OnlineHeroProfile profile, bool hasFonts)
        {
            bool jobFallback = profile != null && profile.JobFallback;
            bool invSynced = profile != null && profile.InventorySynced;
            bool eqSynced = profile != null && profile.EquipsSynced;
            bool goldSynced = profile != null && profile.GoldSynced;
            bool skillsSynced = profile != null && profile.SkillsSynced;

            if (!invSynced && !eqSynced && !goldSynced)
            {
                if (jobFallback)
                {
                    return hasFonts
                        ? "当前仅支持鬼剑士外观与技能，暂按鬼剑士显示 · 道具/装备/金币未同步"
                        : "SWORDMAN FALLBACK / ITEMS EQUIPS GOLD UNSYNCED";
                }
                return hasFonts
                    ? "联机城镇 · 道具/装备/金币暂未同步"
                    : "ONLINE TOWN / ITEMS EQUIPS GOLD UNSYNCED";
            }

            if (hasFonts)
            {
                string prefix = jobFallback
                    ? "当前仅支持鬼剑士外观与技能，暂按鬼剑士显示"
                    : "联机城镇";
                var parts = new List<string>(4);
                if (!invSynced) parts.Add("道具");
                if (!eqSynced) parts.Add("装备");
                if (!goldSynced) parts.Add("金币");
                if (!skillsSynced) parts.Add("技能");
                return parts.Count == 0
                    ? prefix + " · 全部已同步"
                    : prefix + " · " + string.Join("/", parts) + "未同步";
            }
            else
            {
                string prefix = jobFallback ? "SWORDMAN FALLBACK" : "ONLINE TOWN";
                var parts = new List<string>(4);
                if (!invSynced) parts.Add("ITEMS");
                if (!eqSynced) parts.Add("EQUIPS");
                if (!goldSynced) parts.Add("GOLD");
                if (!skillsSynced) parts.Add("SKILLS");
                return parts.Count == 0
                    ? prefix + " / ALL SYNCED"
                    : prefix + " / " + string.Join(" ", parts) + " UNSYNCED";
            }
        }

        private static ShellTone ToneOf(OnlineTownPromptKind kind)
        {
            switch (kind)
            {
                case OnlineTownPromptKind.Waiting:
                    return ShellTone.Wait;
                case OnlineTownPromptKind.ForbiddenMinutes:
                case OnlineTownPromptKind.Rejected:
                    return ShellTone.Error;
                default:
                    return ShellTone.Info;
            }
        }
    }
}
