using System;
using System.Collections.Generic;
using System.Text;

namespace Dnf.Play
{
    /// <summary>
    /// 一个服务器在选服务器、选频道两张界面上用到的原版素材(帧号;图在 <see cref="ServerArt"/> 的几个常量里)。
    /// 帧号为 -1 表示图包里没有这一张(界面改用程序画的底板与文字)。
    /// </summary>
    public sealed class ServerArtEntry
    {
        internal ServerArtEntry(string id, int slotFrame, int slotGrayFrame, int backgroundFrame, int channelIllustrationFrame, int channelLabelFrame,
            params string[] aliases)
        {
            Id = id;
            SlotFrame = slotFrame;
            SlotGrayFrame = slotGrayFrame;
            BackgroundFrame = backgroundFrame;
            ChannelIllustrationFrame = channelIllustrationFrame;
            ChannelLabelFrame = channelLabelFrame;
            Aliases = aliases;
        }

        /// <summary>稳定的短名(测试与排错用)。</summary>
        public string Id { get; }

        /// <summary>入口牌(serverslot.img):彩色那一张;没有为 -1。</summary>
        public int SlotFrame { get; }

        /// <summary>入口牌的灰色版(不可选时用);图包里没有为 -1(此时用彩色版加暗)。</summary>
        public int SlotGrayFrame { get; }

        /// <summary>选服务器界面左边的大插画(server_bg.img);没有为 -1。</summary>
        public int BackgroundFrame { get; }

        /// <summary>选频道界面左边的竖插画(channelbackground.img 的偶数帧);没有为 -1。</summary>
        public int ChannelIllustrationFrame { get; }

        /// <summary>选频道界面横幅上的服务器名牌(channelbackground.img 的奇数帧);没有为 -1。</summary>
        public int ChannelLabelFrame { get; }

        internal string[] Aliases { get; }
    }

    /// <summary>
    /// 服务器名 → 原版素材的对照表(帧号是拿图包里各张图逐个看出来的:每个服务器在 serverslot、server_bg、channelbackground 里各有一张,名字画在图上)。
    /// 目录给的服务器名(组标签)和图上的名字按"归一化后相等或包含"对:不分大小写与空白,去掉"服务器/伺服器/服务/伺服/server/服"字样,
    /// 简体、繁体、英文各登记了写法;找不到就返回 null,界面改用程序画的底板与文字(未知名字的服务器照样能选)。
    /// </summary>
    public static class ServerArt
    {
        public const string BaseImage = "sprite/interface2/channel/serverselect/base/server_base.img";
        public const string BackgroundImage = "sprite/interface2/channel/serverselect/server_bg/server_bg.img";
        public const string SlotImage = "sprite/interface2/channel/serverselect/serverslot/serverslot.img";
        public const string ChannelBaseImage = "sprite/interface2/channel/channelselect/channelbase/channelbase.img";
        public const string ChannelBackgroundImage = "sprite/interface2/channel/channelselect/channelbackground/channelbackground.img";
        public const string ChannelSlotImage = "sprite/interface2/channel/channelselect/channelslot/channelslot.img";

        private static readonly ServerArtEntry[] Entries =
        {
            //                id          slot gray bg  chIllus chLabel  别名(归一化后的写法)
            new ServerArtEntry("first-a", 0, 1, 0, 0, 1, "体验a", "體驗a", "firsta", "experiencea", "体验", "體驗", "first", "experience"),
            new ServerArtEntry("first-b", 2, 3, 1, 0, 1, "体验b", "體驗b", "firstb", "experienceb"),
            new ServerArtEntry("cain", 4, -1, 2, 2, 3, "卡恩", "cain"),
            new ServerArtEntry("diregie", 6, -1, 3, 4, 5, "狄瑞吉", "diregie"),
            new ServerArtEntry("siroco", 8, 9, 4, 6, 7, "希洛克", "siroco"),
            new ServerArtEntry("prey", 10, 11, 5, 8, 9, "普雷", "prey", "pray"),
            new ServerArtEntry("casillas", 12, 13, 6, 10, 11, "卡西利亚斯", "卡西利亞斯", "卡西利爾斯", "casillas", "cassillas"),
            new ServerArtEntry("hilder", 14, 15, 7, 12, 13, "赫尔德", "赫爾德", "hilder"),
            new ServerArtEntry("luke", 16, 17, 8, 14, 15, "卢克", "盧克", "luke", "ruke"),
            new ServerArtEntry("cerria", -1, -1, -1, 16, 17, "赛丽亚", "賽麗亞", "cerria", "seria"),
            new ServerArtEntry("anton", 20, 21, 10, 18, 19, "安徒恩", "安图恩", "安圖恩", "anton"),
            new ServerArtEntry("lukse", -1, -1, -1, 20, 21, "卢克西", "盧克西", "lukse", "roxy"),
            new ServerArtEntry("bakal", 24, 25, 12, 22, 23, "巴卡尔", "巴卡爾", "bakal"),
            new ServerArtEntry("ozma", 26, 27, 13, 24, 25, "奥兹玛", "奧茲瑪", "奥兹", "ozma"),
            new ServerArtEntry("kazan", 28, 29, 14, 26, 27, "卡赞", "卡贊", "kazan", "khazan"),
            new ServerArtEntry("event-1", 30, 31, 15, 28, 29, "活动", "活動", "event"),
            new ServerArtEntry("event-2", 32, 33, 16, 30, 31, "活动2", "活動2", "event2"),
            new ServerArtEntry("event-3", 34, 35, 17, 32, 33, "活动3", "活動3", "event3"),
            new ServerArtEntry("start", 36, 37, 18, 34, 35, "start", "스타트", "新手", "starter"),
        };

        private static readonly string[] Noise = { "服务器", "伺服器", "服务", "伺服", "server", "服" };

        /// <summary>登记过的服务器素材条数(测试用)。</summary>
        public static int Count => Entries.Length;

        /// <summary>
        /// 按服务器名找素材;名字为空或认不出返回 null。归一化后先找相等的写法,再找"名字里包含的最长的写法"(写法至少两个字符)。
        /// </summary>
        public static ServerArtEntry Find(string serverName)
        {
            string name = Normalize(serverName);
            if (name.Length == 0)
            {
                return null;
            }
            for (int i = 0; i < Entries.Length; i++)
            {
                for (int a = 0; a < Entries[i].Aliases.Length; a++)
                {
                    if (string.Equals(Entries[i].Aliases[a], name, StringComparison.Ordinal))
                    {
                        return Entries[i];
                    }
                }
            }
            ServerArtEntry best = null;
            int bestLength = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                for (int a = 0; a < Entries[i].Aliases.Length; a++)
                {
                    string alias = Entries[i].Aliases[a];
                    if (alias.Length >= 2 && alias.Length > bestLength && name.IndexOf(alias, StringComparison.Ordinal) >= 0)
                    {
                        best = Entries[i];
                        bestLength = alias.Length;
                    }
                }
            }
            return best;
        }

        /// <summary>归一化:小写、去空白与分隔符、去掉"服务器"之类的字样。</summary>
        public static string Normalize(string serverName)
        {
            if (string.IsNullOrEmpty(serverName))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(serverName.Length);
            foreach (char c in serverName)
            {
                if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '·' || c == '・' || c == '.')
                {
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            string text = sb.ToString();
            foreach (string noise in Noise)
            {
                text = text.Replace(noise, string.Empty);
            }
            return text;
        }

        /// <summary>所有登记的素材(测试用:检查帧号都在图包范围内)。</summary>
        public static IReadOnlyList<ServerArtEntry> All => Entries;
    }
}
