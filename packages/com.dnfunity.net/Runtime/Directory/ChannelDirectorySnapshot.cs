using System;
using System.Collections.Generic;

namespace Dnf.Net.ChannelDirectory
{
    /// <summary>频道条目派生标记位。</summary>
    [Flags]
    public enum ChannelEntryFlags
    {
        /// <summary>无特殊标记。</summary>
        None = 0,

        /// <summary>当前负载率超过满载阈值。</summary>
        Full = 1 << 0,

        /// <summary>匹配到 <c>ChannelRecommendLevel</c> 推荐等级范围。</summary>
        HasRecommendLevel = 1 << 1,

        // 1 << 2 曾给已删除的 PVP 标记用;空位保留不重排,其余成员的数值保持不变。

        /// <summary>具备有效正整数端口(&gt; 0 且 &lt;= 65535)。</summary>
        HasValidPort = 1 << 3,
    }

    /// <summary>
    /// 面向应用/UI 消费的只读频道条目(对应客户端 240B 槽位语义:<c>DNF.exe</c> <c>0x010fa800..0x010fb4b0</c>)。
    /// </summary>
    public sealed class ChannelEntry
    {
        /// <summary>构造频道条目。</summary>
        public ChannelEntry(
            string groupTag,
            int channelNo,
            string name,
            string displayPrefix,
            int maxCapacity,
            int currentLoad,
            string host,
            int port,
            ChannelCongestionEvaluation congestion,
            ChannelEntryFlags flags,
            int? recommendedMinLevel,
            int? recommendedMaxLevel)
            : this(groupTag, channelNo, name, displayPrefix, maxCapacity, currentLoad, host, port,
                congestion, flags, recommendedMinLevel, recommendedMaxLevel, 0, null)
        {
        }

        /// <summary>构造频道条目（含线缆解析出的真实频道号与 <c>[server]</c> 槽位定义）。</summary>
        public ChannelEntry(
            string groupTag,
            int channelNo,
            string name,
            string displayPrefix,
            int maxCapacity,
            int currentLoad,
            string host,
            int port,
            ChannelCongestionEvaluation congestion,
            ChannelEntryFlags flags,
            int? recommendedMinLevel,
            int? recommendedMaxLevel,
            int parsedChannelNo,
            ChannelServerSlotRule slotRule)
        {
            GroupTag = groupTag ?? string.Empty;
            ChannelNo = channelNo;
            Name = name ?? string.Empty;
            DisplayPrefix = displayPrefix ?? string.Empty;
            MaxCapacity = maxCapacity;
            CurrentLoad = currentLoad;
            Host = host ?? string.Empty;
            Port = port;
            Congestion = congestion;
            Flags = flags;
            RecommendedMinLevel = recommendedMinLevel;
            RecommendedMaxLevel = recommendedMaxLevel;
            ParsedChannelNo = parsedChannelNo;
            SlotRule = slotRule;
        }

        /// <summary>所属频道组标签(服务器名/组标签)。</summary>
        public string GroupTag { get; }

        /// <summary>客户端分配的 1-based 频道显示序号(<c>0x010fb021</c>)。</summary>
        public int ChannelNo { get; }

        /// <summary>从线缆频道名中解析出的数字编号（如 <c>#ch.11</c> 解析为 11；不含数字时为 0）。</summary>
        public int ParsedChannelNo { get; }

        /// <summary>匹配到的 <c>channel_info.etc</c> 槽位定义；未匹配时为 null。</summary>
        public ChannelServerSlotRule SlotRule { get; }

        /// <summary>线缆里的频道原始名称(去 NUL)。</summary>
        public string Name { get; }

        /// <summary>去掉 ASCII 数字后的显示前缀(<c>0x010f4780</c>)。</summary>
        public string DisplayPrefix { get; }

        /// <summary>频道容量上限(<c>0x010fac87</c> <c>+0x14</c>)。</summary>
        public int MaxCapacity { get; }

        /// <summary>当前在线负载(<c>0x010faca2</c> <c>+0x18</c>)。</summary>
        public int CurrentLoad { get; }

        /// <summary>目标游戏频道服务器地址(<c>+0x1c</c>)。</summary>
        public string Host { get; }

        /// <summary>目标游戏频道服务器端口(<c>+0x2c</c>)。</summary>
        public int Port { get; }

        /// <summary>拥挤度与满载评估结果。</summary>
        public ChannelCongestionEvaluation Congestion { get; }

        /// <summary>状态档位(等同 <see cref="Congestion"/>.<see cref="ChannelCongestionEvaluation.Tier"/>)。</summary>
        public ChannelCongestionTier Status => Congestion.Tier;

        /// <summary>派生标志位。</summary>
        public ChannelEntryFlags Flags { get; }

        /// <summary>推荐最低等级(未匹配时为 null)。</summary>
        public int? RecommendedMinLevel { get; }

        /// <summary>推荐最高等级(未匹配时为 null)。</summary>
        public int? RecommendedMaxLevel { get; }
    }

    /// <summary>一个服务器频道分组及组内峰值负载。</summary>
    public sealed class ChannelGroup
    {
        /// <summary>构造频道组。</summary>
        public ChannelGroup(string tag, int maxCurrentLoad, IReadOnlyList<ChannelEntry> channels)
        {
            Tag = tag ?? string.Empty;
            MaxCurrentLoad = maxCurrentLoad;
            Channels = channels ?? Array.Empty<ChannelEntry>();
        }

        /// <summary>组标签。</summary>
        public string Tag { get; }

        /// <summary>组内所有频道的最大 <see cref="ChannelEntry.CurrentLoad"/>(<c>0x010fb2c3</c>)。</summary>
        public int MaxCurrentLoad { get; }

        /// <summary>组内频道列表。</summary>
        public IReadOnlyList<ChannelEntry> Channels { get; }
    }

    /// <summary>一次完整的 Channel Service(7001)只读拉取快照。</summary>
    public sealed class ChannelDirectorySnapshot
    {
        /// <summary>构造目录快照。</summary>
        public ChannelDirectorySnapshot(
            string scriptVersion,
            bool scriptUpToDate,
            ChannelScriptData scriptData,
            IReadOnlyList<ChannelGroup> groups,
            IReadOnlyList<ChannelEntry> channels)
        {
            ScriptVersion = scriptVersion ?? ChannelServiceConstants.DefaultScriptVersion;
            ScriptUpToDate = scriptUpToDate;
            ScriptData = scriptData ?? ChannelScriptData.Default;
            Groups = groups ?? Array.Empty<ChannelGroup>();
            Channels = channels ?? Array.Empty<ChannelEntry>();
        }

        /// <summary>生效的 <c>ChannelScript</c> 版本号。</summary>
        public string ScriptVersion { get; }

        /// <summary>服务端是否返回 <c>upToDate == 1</c>(未重新下发脚本)。</summary>
        public bool ScriptUpToDate { get; }

        /// <summary>本次使用的脚本参数。</summary>
        public ChannelScriptData ScriptData { get; }

        /// <summary>按服务器分组的频道。</summary>
        public IReadOnlyList<ChannelGroup> Groups { get; }

        /// <summary>按接收顺序展平的全体频道。</summary>
        public IReadOnlyList<ChannelEntry> Channels { get; }

        /// <summary>由线缆解析结果与脚本规则组装快照并计算组内拥挤度。</summary>
        public static ChannelDirectorySnapshot Build(
            string scriptVersion,
            bool scriptUpToDate,
            ChannelScriptData scriptData,
            IReadOnlyList<ChannelWireGroup> wireGroups)
        {
            ChannelScriptData effectiveScript = scriptData ?? ChannelScriptData.Default;
            var groups = new List<ChannelGroup>(wireGroups == null ? 0 : wireGroups.Count);
            var flat = new List<ChannelEntry>();
            int nextChannelNo = 1;

            if (wireGroups != null)
            {
                for (int g = 0; g < wireGroups.Count; g++)
                {
                    ChannelWireGroup wg = wireGroups[g];
                    int groupMax = 0;
                    for (int c = 0; c < wg.Channels.Count; c++)
                    {
                        if (wg.Channels[c].CurrentLoad > groupMax)
                        {
                            groupMax = wg.Channels[c].CurrentLoad;
                        }
                    }

                    var entries = new List<ChannelEntry>(wg.Channels.Count);
                    for (int c = 0; c < wg.Channels.Count; c++)
                    {
                        ChannelWireEntry wc = wg.Channels[c];
                        ChannelCongestionEvaluation eval = ChannelCongestionEvaluator.Evaluate(
                            wc.CurrentLoad,
                            wc.MaxCapacity,
                            groupMax,
                            effectiveScript.FullThresholdPercent);

                        ChannelEntryFlags flags = ChannelEntryFlags.None;
                        if (eval.IsFull)
                        {
                            flags |= ChannelEntryFlags.Full;
                        }

                        int parsedNo = ChannelScriptData.SplitNameDigits(wc.Name, out string prefix);
                        ChannelServerSlotRule slotRule = parsedNo > 0 ? effectiveScript.FindServerSlot(wg.Tag, parsedNo) : null;

                        int? minLv = null;
                        int? maxLv = null;
                        if (effectiveScript.TryMatchRecommendLevel(wc.Name, out int matchedMin, out int matchedMax)
                            || (slotRule != null && !IsNoneAreaTag(slotRule.AreaTag)
                                && effectiveScript.TryMatchRecommendLevel(slotRule.AreaTag, out matchedMin, out matchedMax)))
                        {
                            minLv = matchedMin;
                            maxLv = matchedMax;
                            flags |= ChannelEntryFlags.HasRecommendLevel;
                        }

                        if (wc.Port > 0 && wc.Port <= 65535)
                        {
                            flags |= ChannelEntryFlags.HasValidPort;
                        }

                        var entry = new ChannelEntry(
                            wg.Tag,
                            nextChannelNo++,
                            wc.Name,
                            prefix,
                            wc.MaxCapacity,
                            wc.CurrentLoad,
                            wc.Host,
                            wc.Port,
                            eval,
                            flags,
                            minLv,
                            maxLv,
                            parsedNo,
                            slotRule);
                        entries.Add(entry);
                        flat.Add(entry);
                    }
                    groups.Add(new ChannelGroup(wg.Tag, groupMax, entries));
                }
            }

            return new ChannelDirectorySnapshot(scriptVersion, scriptUpToDate, effectiveScript, groups, flat);
        }

        private static bool IsNoneAreaTag(string areaTag)
        {
            if (string.IsNullOrWhiteSpace(areaTag))
            {
                return true;
            }
            string t = areaTag.Trim();
            return string.Equals(t, "[none]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "none", StringComparison.OrdinalIgnoreCase);
        }
    }
}
