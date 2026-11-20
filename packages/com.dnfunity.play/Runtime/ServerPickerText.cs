using System.Globalization;
using System.Text;

namespace Dnf.Play
{
    /// <summary>选服务器、选频道两张界面上程序写的字(图里已经画好的字不在这里)。</summary>
    public enum PickerLabel
    {
        /// <summary>选服务器标题下面那一行说明(基线上是繁体用词"伺服器")。</summary>
        ServerSubtitle,

        ServerListLoading,
        ServerListEmpty,
        ServerListFailed,
        ChannelListLoading,
        ChannelListEmpty,
        ChannelListFailed,
        ChannelConnecting,
        ChannelRefreshing,

        /// <summary>点了一个满载的频道。</summary>
        ChannelFull,

        /// <summary>点了一个没有可用端口的频道。</summary>
        ChannelUnavailable,

        /// <summary>目录读不到时的两个出口。</summary>
        RetryButton,

        DirectButton,

        /// <summary>选频道界面底下两个小按钮。</summary>
        ChangeServer,

        QuitGame,

        /// <summary>服务器没有任何频道(入口牌上的小字)。</summary>
        ServerNoChannels,
    }

    /// <summary>
    /// 选服务器与选频道的文字表:中文,没有字体集时退回英文(点阵字库只认大写字母、数字与几个符号,所以英文占位只用这些)。
    /// 频道名、拥挤档位、推荐等级这类"数据文字"也在这里拼。
    /// </summary>
    public static class ServerPickerText
    {
        public static string Label(PickerLabel label, bool cjk)
        {
            switch (label)
            {
                case PickerLabel.ServerSubtitle: return cjk ? "请选择进行游戏的伺服器。" : "SELECT A SERVER.";
                case PickerLabel.ServerListLoading: return cjk ? "正在获取服务器列表…" : "LOADING SERVER LIST...";
                case PickerLabel.ServerListEmpty: return cjk ? "没有可用的服务器" : "NO SERVER AVAILABLE";
                case PickerLabel.ServerListFailed: return cjk ? "无法获取服务器列表" : "CANNOT LOAD SERVER LIST";
                case PickerLabel.ChannelListLoading: return cjk ? "正在获取频道列表…" : "LOADING CHANNEL LIST...";
                case PickerLabel.ChannelListEmpty: return cjk ? "这个服务器没有可用的频道" : "NO CHANNEL AVAILABLE";
                case PickerLabel.ChannelListFailed: return cjk ? "无法获取频道列表" : "CANNOT LOAD CHANNEL LIST";
                case PickerLabel.ChannelConnecting: return cjk ? "正在连接频道…" : "CONNECTING...";
                case PickerLabel.ChannelRefreshing: return cjk ? "正在刷新频道列表…" : "REFRESHING...";
                case PickerLabel.ChannelFull: return cjk ? "该频道已满员,请选择其他频道" : "CHANNEL FULL";
                case PickerLabel.ChannelUnavailable: return cjk ? "该频道暂不可用" : "CHANNEL UNAVAILABLE";
                case PickerLabel.RetryButton: return cjk ? "重试" : "RETRY";
                case PickerLabel.DirectButton: return cjk ? "默认频道" : "DEFAULT";
                case PickerLabel.ChangeServer: return cjk ? "变更伺服器" : "SERVERS";
                case PickerLabel.QuitGame: return cjk ? "结束游戏" : "EXIT";
                case PickerLabel.ServerNoChannels: return cjk ? "无频道" : "NONE";
                default: return string.Empty;
            }
        }

        /// <summary>拥挤档位的词(基线上"顺畅"是图外写的字)。</summary>
        public static string Congestion(ChannelCongestion congestion, bool cjk)
        {
            switch (congestion)
            {
                case ChannelCongestion.Smooth: return cjk ? "顺畅" : "SMOOTH";
                case ChannelCongestion.Good: return cjk ? "良好" : "GOOD";
                case ChannelCongestion.Normal: return cjk ? "普通" : "NORMAL";
                case ChannelCongestion.Crowded: return cjk ? "拥挤" : "CROWDED";
                default: return cjk ? "满载" : "FULL";
            }
        }

        /// <summary>
        /// 拥挤档位的字色(0xRRGGBB)。顺畅是基线上量到的纯绿;其余四档的来源是联网层从客户端抄下来的四个颜色值
        /// (<c>ChannelCongestionTier</c> 的注释),这里按"字节序是 BGR"读(这样从绿到粉红依次是 琥珀、橙、红、粉,和档位的轻重一致;
        /// 按 RGB 读会是 天蓝、蔚蓝、纯蓝,纯蓝字压在深蓝底上看不清)。基线上只有顺畅,其余是推断(见 NOTES)。
        /// </summary>
        public static uint CongestionColor(ChannelCongestion congestion)
        {
            switch (congestion)
            {
                case ChannelCongestion.Smooth: return 0x00FF00u;
                case ChannelCongestion.Good: return 0xFFB100u;
                case ChannelCongestion.Normal: return 0xFF8000u;
                case ChannelCongestion.Crowded: return 0xFF0000u;
                default: return 0xFF00F0u;
            }
        }

        /// <summary>
        /// 频道项第一行:"ch%02d.%s"(原版 <c>0x010f46d0</c>,如 "ch11.格兰之森"、"ch01.亡者峡谷")。没有字体集时只留 ASCII 部分;无名字时不留悬空句点。
        /// </summary>
        public static string ChannelTitle(DirectoryChannel channel, bool cjk)
        {
            string title = channel.Title ?? string.Empty;
            if (!cjk)
            {
                title = AsciiOnly(title);
            }
            string num = channel.Number.ToString("D2", CultureInfo.InvariantCulture);
            return title.Length > 0 ? "ch" + num + "." + title : "ch" + num;
        }

        /// <summary>频道项第二行:推荐等级 "(Lv.%02d~%02d)"(原版 <c>0x010f699a</c>);没有推荐等级返回空串。</summary>
        public static string Level(DirectoryChannel channel)
        {
            if (!channel.HasRecommendedLevel)
            {
                return string.Empty;
            }
            string min = channel.RecommendedMinLevel.ToString("D2", CultureInfo.InvariantCulture);
            string max = channel.RecommendedMaxLevel.ToString("D2", CultureInfo.InvariantCulture);
            return "(Lv." + min + "~" + max + ")";
        }

        /// <summary>页码 "第几页/共几页"。</summary>
        public static string Page(int page, int pages)
        {
            return (page + 1) + "/" + pages;
        }

        /// <summary>没有字体集又画不出名字时,服务器入口牌上的占位名:SERVER 加序号(从 1 起)。</summary>
        public static string ServerPlaceholder(int index)
        {
            return "SERVER " + (index + 1);
        }

        /// <summary>只留 ASCII 可见字符(去掉点阵字库画不出来的中文)。</summary>
        public static string AsciiOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= 0x20 && c < 0x7F)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim();
        }
    }
}
