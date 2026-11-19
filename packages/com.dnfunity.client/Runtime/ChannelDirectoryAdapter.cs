using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Dnf.Data;
using Dnf.Net.ChannelDirectory;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 宿主从用户游戏数据中预读的频道目录参数与文本查询（<see cref="ChannelDirectoryCatalog.Create"/>，不碰引擎）：
    /// 为 <see cref="OnlineShellBackend"/> 提供本地推荐等级规则（<c>etc/channelrecommendlevel.etc</c>）、
    /// 频道槽位定义（<c>etc/channel_info.etc</c>）以及中文频道名解析（<c>StrLibrary</c>）。
    /// </summary>
    public sealed class ChannelDirectoryCatalog
    {
        internal ChannelDirectoryCatalog(ChannelScriptData fallbackScript, Func<int, string, string> resolveString)
        {
            FallbackScript = fallbackScript ?? ChannelScriptData.Default;
            ResolveString = resolveString;
        }

        /// <summary>本地脚本提取的推荐等级与槽位规则（作 <c>0x09</c> 回包缺失字段的回退/合并源）。</summary>
        public ChannelScriptData FallbackScript { get; }

        /// <summary>按文字列种类编号与键解析本地化名称；未装汉字字体或无文本表时为 null。</summary>
        public Func<int, string, string> ResolveString { get; }

        /// <summary>从游戏数据中读取推荐等级与频道槽位；只有在装好汉字字体时才加载文字列以解析中文频道名。</summary>
        public static ChannelDirectoryCatalog Create(DnfAssets assets, bool hasFonts)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            StrLibrary library = null;
            if (hasFonts)
            {
                PvfArchive pvf = assets.Pvf;
                library = new StrLibrary(path => pvf.TryGetEntry(path, out PvfEntry entry) ? pvf.Read(entry) : null);
            }
            return FromScripts(assets.ReadScript, library);
        }

        /// <summary>从已解析脚本行与可选文字列组装频道目录参数。</summary>
        public static ChannelDirectoryCatalog FromScripts(Func<string, ScriptLine[]> readScript, StrLibrary strings)
        {
            if (readScript == null)
            {
                throw new ArgumentNullException(nameof(readScript));
            }

            float fullThreshold = ChannelServiceConstants.DefaultFullThresholdPercent;
            bool hasExplicitFull = false;
            var recommendRules = new List<ChannelRecommendLevelRule>();
            var serverSlots = new List<ChannelServerSlotRule>();

            ScriptLine[] recommendLines = readScript("etc/channelrecommendlevel.etc");
            if (recommendLines != null)
            {
                for (int i = 0; i < recommendLines.Length; i++)
                {
                    ScriptLine line = recommendLines[i];
                    if (line.TagIs("[channel rate full]"))
                    {
                        double? val = line.FirstNumber();
                        if (val.HasValue && val.Value > 0d)
                        {
                            fullThreshold = (float)val.Value;
                            hasExplicitFull = true;
                        }
                    }
                    else if (line.TagIs("[channel]"))
                    {
                        string tag = line.FirstString();
                        List<int> levels = line.Ints();
                        if (!string.IsNullOrEmpty(tag) && levels.Count >= 2)
                        {
                            recommendRules.Add(new ChannelRecommendLevelRule(tag, levels[0], levels[1]));
                        }
                    }
                }
            }

            ScriptLine[] infoLines = readScript("etc/channel_info.etc");
            if (infoLines != null)
            {
                for (int i = 0; i < infoLines.Length; i++)
                {
                    ScriptLine line = infoLines[i];
                    if (!line.TagIs("[server]") || line.Values == null || line.Values.Length == 0 || !line.Values[0].IsNumber)
                    {
                        continue;
                    }
                    int groupId = line.Values[0].Int;
                    if (groupId <= 0)
                    {
                        continue;
                    }
                    const int fieldsPerSlot = 15;
                    for (int idx = 1; idx + fieldsPerSlot <= line.Values.Length; idx += fieldsPerSlot)
                    {
                        ScriptValue vNo = line.Values[idx];
                        ScriptValue vTitle = line.Values[idx + 1];
                        ScriptValue vKind = line.Values[idx + 2];
                        ScriptValue vArea = line.Values[idx + 3];
                        if (!vNo.IsNumber || !vKind.IsNumber)
                        {
                            break;
                        }
                        int titleKind = -1;
                        string titleKey = string.Empty;
                        string literalTitle = string.Empty;
                        if (vTitle.Kind == ScriptValueKind.Keyed)
                        {
                            titleKind = vTitle.Int;
                            titleKey = vTitle.Text ?? string.Empty;
                        }
                        else if (!string.IsNullOrEmpty(vTitle.Text))
                        {
                            literalTitle = vTitle.Text;
                        }
                        string areaTag = vArea.Text ?? string.Empty;
                        serverSlots.Add(new ChannelServerSlotRule(
                            groupId, vNo.Int, titleKind, titleKey, literalTitle, vKind.Int, areaTag));
                    }
                }
            }

            var script = new ChannelScriptData(string.Empty, fullThreshold, recommendRules, serverSlots, hasExplicitFull);
            Func<int, string, string> resolver = strings == null ? null : (Func<int, string, string>)strings.Get;
            return new ChannelDirectoryCatalog(script, resolver);
        }
    }

    /// <summary>
    /// 联网层的频道目录(<see cref="ChannelDirectorySnapshot"/>,E-423/E-424)→ Play 层中性目录(<see cref="ServerDirectory"/>)的翻译。全是纯函数,不碰网络也不碰引擎:
    /// 选服务器、选频道界面不引用 Net 程序集,"目录字段到底对应屏上哪个元素"只在这一处做,测试也只在这一处核对。
    /// <list type="table">
    /// <item><term>目录的一个频道组(<see cref="ChannelGroup"/>)</term><description>选服务器界面的一个入口(<see cref="DirectoryServer"/>;组标签就是服务器名;常规与特殊频道均为空时画成灰牌、不能选)</description></item>
    /// <item><term>组内频道按类型分流(<see cref="ChannelServerSlotRule.SelectPlacement"/>)</term><description>类型 0 进入 3×5 翻页主网格(<see cref="DirectoryServer.Channels"/>);类型 11(优先)与类型 3(次席)进入底部分隔线下方最多 3 格的特殊频道栏(<see cref="DirectoryServer.SpecialChannels"/>);决斗场与自由练习场(类型 4..10、13..16)及隐藏/未知频道不在选频道页开放</description></item>
    /// <item><term>频道序号 <see cref="ChannelEntry.ParsedChannelNo"/> / <see cref="ChannelEntry.ChannelNo"/></term><description>项里第一行 "ch" 后面的数字</description></item>
    /// <item><term>去掉数字的频道名 <see cref="ChannelEntry.DisplayPrefix"/> / <see cref="ChannelEntry.SlotRule"/></term><description>序号后面的名字</description></item>
    /// <item><term>推荐等级(<see cref="ChannelEntryFlags.HasRecommendLevel"/> 且非交易频道)</term><description>第二行 "(Lv.下限~上限)";交易频道或缺少推荐等级时不画这一行</description></item>
    /// <item><term>拥挤档位 <see cref="ChannelEntry.Status"/> 与满载标志 <see cref="ChannelEntryFlags.Full"/></term><description>右边的拥挤词与字色(顺畅/良好/普通/拥挤/满载);满载的频道不能选</description></item>
    /// <item><term>主机与端口 <see cref="ChannelEntry.Host"/>、<see cref="ChannelEntry.Port"/></term><description>点"游戏开始"时要连的目标(<see cref="Target"/>:主机回落规则见该方法);端口无效的频道没有目标、不能选</description></item>
    /// </list>
    /// 目录数据里没有"活动标记"与"服务器状态"字段(任务单与台账 E-423 都写了),所以服务器入口上的 EVENT 角标没有数据来源:这里一律不挂
    /// (<see cref="DirectoryServer.HasEventMark"/> 恒为 false;基线上那个角标是画在原图里的,来源未知、非对拍,见 NOTES)。
    /// </summary>
    public static class ChannelDirectoryAdapter
    {
        /// <summary>选频道界面分隔线下方特殊频道栏的最大容量（<c>DNF.exe</c> <c>0x010f8eb7</c> / <c>0x010f9050</c>）。</summary>
        public const int MaxSpecialChannels = 3;

        private static readonly IReadOnlyDictionary<string, int> AreaTagSlotFrames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "[granfloris]", 0 },
            { "[sky_catle]", 1 },
            { "[behemoth]", 2 },
            { "[Alfhlyra]", 3 },
            { "[elven_guard]", 4 },
            { "[north_myre]", 6 },
            { "[stormpass]", 7 },
            { "[deathtower]", 8 },
            { "[ancient]", 9 },
            { "[Antwer]", 10 },
            { "[Hall]", 11 },
            { "[Fortress]", 12 },
            { "[impossible]", 13 },
            { "[seatrain]", 14 },
            { "[dragonroad]", 16 },
            { "[timedoor]", 17 },
            { "[powerstation]", 18 },
        };
        /// <summary>读目录的结局 + 快照 → 外壳认的结果(成功带中性目录,失败带提示类别)。成功却没有快照按意外错误算。</summary>
        public static ServerDirectoryResult Result(
            ChannelDirectoryLoadOutcome outcome,
            ChannelDirectorySnapshot snapshot,
            Func<int, string, string> resolveString = null)
        {
            if (outcome == ChannelDirectoryLoadOutcome.Ok)
            {
                return snapshot == null
                    ? ServerDirectoryResult.Fail(OnlineNoticeKind.Unexpected)
                    : ServerDirectoryResult.Ok(Map(snapshot, resolveString));
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

        /// <summary>整份快照翻成中性目录(按原版频道类型分流到主网格、底部特殊频道栏或屏蔽决斗场/未知频道);快照为 null 当空目录。</summary>
        public static ServerDirectory Map(ChannelDirectorySnapshot snapshot, Func<int, string, string> resolveString = null)
        {
            if (snapshot == null || snapshot.Groups.Count == 0)
            {
                return ServerDirectory.Empty;
            }
            var servers = new List<DirectoryServer>(snapshot.Groups.Count);
            foreach (ChannelGroup group in snapshot.Groups)
            {
                bool hasGroupSlots = snapshot.ScriptData != null && snapshot.ScriptData.HasServerSlotsForGroup(group.Tag);
                var mainChannels = new List<DirectoryChannel>(group.Channels.Count);
                var towerChannels = new List<DirectoryChannel>();
                var tradeChannels = new List<DirectoryChannel>();

                foreach (ChannelEntry entry in group.Channels)
                {
                    if (entry.SlotRule == null)
                    {
                        if (!hasGroupSlots)
                        {
                            mainChannels.Add(Channel(entry, resolveString));
                        }
                        continue;
                    }
                    switch (entry.SlotRule.SelectPlacement)
                    {
                        case ChannelSelectPlacement.MainGrid:
                            mainChannels.Add(Channel(entry, resolveString));
                            break;
                        case ChannelSelectPlacement.SpecialTower:
                            towerChannels.Add(Channel(entry, resolveString));
                            break;
                        case ChannelSelectPlacement.SpecialTrade:
                            tradeChannels.Add(Channel(entry, resolveString));
                            break;
                    }
                }

                var specialChannels = new List<DirectoryChannel>(Math.Min(MaxSpecialChannels, towerChannels.Count + tradeChannels.Count));
                AppendLimited(specialChannels, towerChannels, MaxSpecialChannels);
                AppendLimited(specialChannels, tradeChannels, MaxSpecialChannels);
                servers.Add(new DirectoryServer(group.Tag, mainChannels, false, specialChannels));
            }
            return new ServerDirectory(servers);
        }

        private static void AppendLimited(List<DirectoryChannel> into, List<DirectoryChannel> source, int limit)
        {
            for (int i = 0; i < source.Count && into.Count < limit; i++)
            {
                into.Add(source[i]);
            }
        }

        /// <summary>一个频道条目 → 中性频道。</summary>
        public static DirectoryChannel Channel(ChannelEntry entry, Func<int, string, string> resolveString = null)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            int min = 0;
            int max = 0;
            bool suppressRecommendLevel = entry.SlotRule != null && entry.SlotRule.ChannelKind == 3;
            if (!suppressRecommendLevel
                && (entry.Flags & ChannelEntryFlags.HasRecommendLevel) != 0
                && entry.RecommendedMinLevel.HasValue
                && entry.RecommendedMaxLevel.HasValue)
            {
                min = entry.RecommendedMinLevel.Value;
                max = entry.RecommendedMaxLevel.Value;
            }
            int number = entry.ParsedChannelNo > 0 ? entry.ParsedChannelNo : Math.Max(0, entry.ChannelNo);
            string title = ResolveChannelTitle(entry, resolveString);
            int slotFrame = ResolveSlotFrame(entry.SlotRule);
            return new DirectoryChannel(number, title, Congestion(entry), Target(entry), min, max, slotFrame);
        }

        /// <summary>
        /// 根据频道槽位定义映射 <c>channelslot.img</c> 图标帧号（对应 <c>DNF.exe</c> <c>0x010f80a3..0x010f83ab</c>）;
        /// 无槽位定义时返回 <see cref="DirectoryChannel.DefaultSlotFrame"/>。
        /// </summary>
        public static int ResolveSlotFrame(ChannelServerSlotRule rule)
        {
            if (rule == null)
            {
                return DirectoryChannel.DefaultSlotFrame;
            }
            if (rule.ChannelKind == 2)
            {
                return 4;
            }
            if (rule.ChannelKind == 3)
            {
                return 5;
            }
            if (rule.ChannelKind == 11)
            {
                return 8;
            }
            string areaTag = rule.AreaTag == null ? string.Empty : rule.AreaTag.Trim();
            return areaTag.Length > 0 && AreaTagSlotFrames.TryGetValue(areaTag, out int frame) ? frame : 0;
        }

        private static string ResolveChannelTitle(ChannelEntry entry, Func<int, string, string> resolveString)
        {
            ChannelServerSlotRule rule = entry.SlotRule;
            if (rule != null)
            {
                if (rule.TitleStringKind >= 0 && !string.IsNullOrEmpty(rule.TitleStringKey) && resolveString != null)
                {
                    string resolved = resolveString(rule.TitleStringKind, rule.TitleStringKey);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved.Trim();
                    }
                }
                if (!string.IsNullOrWhiteSpace(rule.LiteralTitle))
                {
                    return rule.LiteralTitle.Trim();
                }
            }
            return CleanDisplayPrefix(entry.DisplayPrefix);
        }

        private static string CleanDisplayPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return string.Empty;
            }
            string trimmed = prefix.Trim();
            if (trimmed[0] == '#')
            {
                return string.Empty;
            }
            string collapsed = trimmed.Replace(".", string.Empty).Replace(" ", string.Empty);
            if (string.Equals(collapsed, "ch", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return prefix;
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
            ushort serverGroup = ChannelScriptData.TryMapServerGroupId(entry.GroupTag, out int gid)
                && gid >= 1
                && gid <= Dnf.Net.Session.LoginRequest.MaxServerGroup
                ? (ushort)gid
                : (ushort)0;
            string host = entry.Host == null ? string.Empty : entry.Host.Trim();
            if (host.Length == 0)
            {
                return new ChannelTarget(entry.Port, serverGroup: serverGroup);
            }
            if (!TryIpLiteral(host, out IPAddress address))
            {
                return null;
            }
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return new ChannelTarget(entry.Port, serverGroup: serverGroup);
            }
            return new ChannelTarget(entry.Port, host, serverGroup);
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
