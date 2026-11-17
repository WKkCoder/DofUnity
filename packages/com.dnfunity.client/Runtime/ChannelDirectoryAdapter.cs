using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Dnf.Net.ChannelDirectory;
using Dnf.Play;

namespace Dnf.Client
{
    /// <summary>
    /// 联网层的频道目录(<see cref="ChannelDirectorySnapshot"/>,E-423/E-424)→ Play 层中性目录(<see cref="ServerDirectory"/>)的翻译。全是纯函数,不碰网络也不碰引擎:
    /// 选服务器、选频道界面不引用 Net 程序集,"目录字段到底对应屏上哪个元素"只在这一处做,测试也只在这一处核对。
    /// <list type="table">
    /// <item><term>目录的一个频道组(<see cref="ChannelGroup"/>)</term><description>选服务器界面的一个入口(<see cref="DirectoryServer"/>;组标签就是服务器名;没有频道的组照样列出,画成灰牌、不能选)</description></item>
    /// <item><term>组里的一个频道(<see cref="ChannelEntry"/>)</term><description>选频道界面的一个频道项(<see cref="DirectoryChannel"/>),顺序就是目录给的顺序</description></item>
    /// <item><term>频道序号 <see cref="ChannelEntry.ChannelNo"/></term><description>项里第一行 "ch" 后面的数字(客户端本地从 1 累加、跨组连续,所以第二个服务器的第一个频道不是 ch1)</description></item>
    /// <item><term>去掉数字的频道名 <see cref="ChannelEntry.DisplayPrefix"/></term><description>序号后面的名字</description></item>
    /// <item><term>推荐等级(<see cref="ChannelEntryFlags.HasRecommendLevel"/> 且两头都有值)</term><description>第二行 "(Lv.下限~上限)";否则不画这一行</description></item>
    /// <item><term>拥挤档位 <see cref="ChannelEntry.Status"/> 与满载标志 <see cref="ChannelEntryFlags.Full"/></term><description>右边的拥挤词与字色(顺畅/良好/普通/拥挤/满载);满载的频道不能选</description></item>
    /// <item><term>主机与端口 <see cref="ChannelEntry.Host"/>、<see cref="ChannelEntry.Port"/></term><description>点"游戏开始"时要连的目标(<see cref="Target"/>:主机回落规则见该方法);端口无效的频道没有目标、不能选</description></item>
    /// </list>
    /// 目录数据里没有"活动标记"与"服务器状态"字段(任务单与台账 E-423 都写了),所以服务器入口上的 EVENT 角标没有数据来源:这里一律不挂
    /// (<see cref="DirectoryServer.HasEventMark"/> 恒为 false;基线上那个角标是画在原图里的,来源未知、非对拍,见 NOTES)。
    /// </summary>
    public static class ChannelDirectoryAdapter
    {
        /// <summary>读目录的结局 + 快照 → 外壳认的结果(成功带中性目录,失败带提示类别)。成功却没有快照按意外错误算。</summary>
        public static ServerDirectoryResult Result(ChannelDirectoryLoadOutcome outcome, ChannelDirectorySnapshot snapshot)
        {
            if (outcome == ChannelDirectoryLoadOutcome.Ok)
            {
                return snapshot == null
                    ? ServerDirectoryResult.Fail(OnlineNoticeKind.Unexpected)
                    : ServerDirectoryResult.Ok(Map(snapshot));
            }
            return ServerDirectoryResult.Fail(Failure(outcome));
        }

        /// <summary>
        /// 读目录失败的结局 → 提示类别(目录读取没有自己的提示类别,归到最接近的既有类别;玩家看到的是"网络错误"、"服务器返回异常"这样的话,细节只在日志的 reason 短码里):
        /// 网络读写或超时 → <see cref="OnlineNoticeKind.NetworkError"/>;帧头、密钥交换长度、解压解密、列表布局不对 → <see cref="OnlineNoticeKind.ProtocolError"/>;
        /// 密钥交换被拒、版本被拒 → <see cref="OnlineNoticeKind.Rejected"/>;调用参数不合法(本地的 bug,不该出现)与不认识的结局 → <see cref="OnlineNoticeKind.Unexpected"/>。
        /// </summary>
        public static OnlineNoticeKind Failure(ChannelDirectoryLoadOutcome outcome)
        {
            switch (outcome)
            {
                case ChannelDirectoryLoadOutcome.IoError:
                    return OnlineNoticeKind.NetworkError;
                case ChannelDirectoryLoadOutcome.FrameError:
                case ChannelDirectoryLoadOutcome.BadKeyExchangeLength:
                case ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed:
                case ChannelDirectoryLoadOutcome.ParseFailed:
                    return OnlineNoticeKind.ProtocolError;
                case ChannelDirectoryLoadOutcome.KeyExchangeRejected:
                case ChannelDirectoryLoadOutcome.VersionRejected:
                    return OnlineNoticeKind.Rejected;
                default:
                    return OnlineNoticeKind.Unexpected;
            }
        }

        /// <summary>整份快照翻成中性目录(组的顺序、组内频道的顺序都保持目录给的顺序);快照为 null 当空目录。</summary>
        public static ServerDirectory Map(ChannelDirectorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Groups.Count == 0)
            {
                return ServerDirectory.Empty;
            }
            var servers = new List<DirectoryServer>(snapshot.Groups.Count);
            foreach (ChannelGroup group in snapshot.Groups)
            {
                var channels = new List<DirectoryChannel>(group.Channels.Count);
                foreach (ChannelEntry entry in group.Channels)
                {
                    channels.Add(Channel(entry));
                }
                servers.Add(new DirectoryServer(group.Tag, channels, false));
            }
            return new ServerDirectory(servers);
        }

        /// <summary>一个频道条目 → 中性频道。</summary>
        public static DirectoryChannel Channel(ChannelEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            int min = 0;
            int max = 0;
            if ((entry.Flags & ChannelEntryFlags.HasRecommendLevel) != 0 && entry.RecommendedMinLevel.HasValue && entry.RecommendedMaxLevel.HasValue)
            {
                min = entry.RecommendedMinLevel.Value;
                max = entry.RecommendedMaxLevel.Value;
            }
            return new DirectoryChannel(Math.Max(0, entry.ChannelNo), entry.DisplayPrefix, Congestion(entry), Target(entry), min, max);
        }

        /// <summary>拥挤档位:满载标志(或档位本身是满载)→ <see cref="ChannelCongestion.Full"/>;其余按档位一一对应。</summary>
        public static ChannelCongestion Congestion(ChannelEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            if ((entry.Flags & ChannelEntryFlags.Full) != 0)
            {
                return ChannelCongestion.Full;
            }
            switch (entry.Status)
            {
                case ChannelCongestionTier.Comfortable: return ChannelCongestion.Smooth;
                case ChannelCongestionTier.Harmony: return ChannelCongestion.Good;
                case ChannelCongestionTier.Normal: return ChannelCongestion.Normal;
                case ChannelCongestionTier.Crowded: return ChannelCongestion.Crowded;
                default: return ChannelCongestion.Full;
            }
        }

        /// <summary>
        /// 频道要连的目标。端口无效(没有 <see cref="ChannelEntryFlags.HasValidPort"/>,或不在 1 到 65535)→ null(这个频道不能选)。
        /// <para>主机回落规则(真服从没验证过目录项里写的是什么主机,这是默认规则,理由见 NOTES):</para>
        /// <list type="bullet">
        /// <item>目录项的主机是空的(或只有空白)→ 目标不带主机,连的时候用网关登录给的游戏服地址(<see cref="ChannelTarget.Host"/> 为 null);</item>
        /// <item>是"本机所有地址"的绑定写法(0.0.0.0 或 ::)→ 不是可连的目标,当作没写,同上回落;</item>
        /// <item>是合法的 IP 字面量(点分四段的 IPv4,或 IPv6)→ 照用(原版客户端就是连目录项里写的地址),目标带这个主机;</item>
        /// <item>非空却不是 IP 字面量(联网层的连接入口不做域名解析;.NET 会把 "10.1.2"、"167772162" 这类简写也当 IPv4,这里不认)→ 这个频道不能选(目标 null):宁可明说连不上,也不悄悄连到另一台。</item>
        /// </list>
        /// </summary>
        public static ChannelTarget Target(ChannelEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            if ((entry.Flags & ChannelEntryFlags.HasValidPort) == 0 || entry.Port < 1 || entry.Port > 65535)
            {
                return null;
            }
            string host = entry.Host == null ? string.Empty : entry.Host.Trim();
            if (host.Length == 0)
            {
                return new ChannelTarget(entry.Port);
            }
            if (!TryIpLiteral(host, out IPAddress address))
            {
                return null;
            }
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return new ChannelTarget(entry.Port);
            }
            return new ChannelTarget(entry.Port, host);
        }

        /// <summary>IP 字面量:IPv6,或恰好四段的点分 IPv4(.NET 的解析器还认简写,会把 "10.1.2"、"167772162" 悄悄变成另一个地址,不能放过)。</summary>
        private static bool TryIpLiteral(string host, out IPAddress address)
        {
            if (!IPAddress.TryParse(host, out address))
            {
                return false;
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return true;
            }
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }
            int dots = 0;
            foreach (char c in host)
            {
                if (c == '.')
                {
                    dots++;
                }
            }
            return dots == 3;
        }
    }
}
