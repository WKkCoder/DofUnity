using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dnf.Net.ChannelDirectory
{
    /// <summary>
    /// 频道拥挤度档位(<c>DNF.exe</c> <c>0x010f5870..0x010f599c</c> 与 <c>SC01</c> 字符串表)。
    /// </summary>
    public enum ChannelCongestionTier
    {
        /// <summary>顺畅:pct &lt; 3,颜色 0xff00ff00,字符串 ID 0x536c。</summary>
        Comfortable = 0,

        /// <summary>良好:3 &lt;= pct &lt; 5,颜色 0xff00b1ff,字符串 ID 0x536d。</summary>
        Harmony = 1,

        /// <summary>普通:5 &lt;= pct &lt; 25,颜色 0xff0080ff,字符串 ID 0x536e。</summary>
        Normal = 2,

        /// <summary>拥挤:pct &gt;= 25 且未满载,颜色 0xff0000ff,字符串 ID 0x536f。</summary>
        Crowded = 3,

        /// <summary>满载:(float)load*100/(float)max &gt; fullThreshold,颜色 0xfff000ff,字符串 ID 0x5370。</summary>
        Full = 4,
    }

    /// <summary>频道推荐等级规则(<c>[channel] "&lt;tag&gt;" minLevel maxLevel</c>)。</summary>
    public sealed class ChannelRecommendLevelRule
    {
        /// <summary>构造推荐等级条目。</summary>
        public ChannelRecommendLevelRule(string tag, int minLevel, int maxLevel)
        {
            Tag = tag ?? string.Empty;
            MinLevel = minLevel;
            MaxLevel = maxLevel;
        }

        /// <summary>频道类型标签。</summary>
        public string Tag { get; }

        /// <summary>推荐最低等级。</summary>
        public int MinLevel { get; }

        /// <summary>推荐最高等级。</summary>
        public int MaxLevel { get; }
    }

    /// <summary>
    /// 选频道界面依据 <c>channel_info.etc</c> 频道类型对频道进行的显示分流（对应 <c>DNF.exe</c> <c>0x010faf30..0x010faf72</c> 与 <c>0x010f8e10..0x010f9105</c>）。
    /// </summary>
    public enum ChannelSelectPlacement
    {
        /// <summary>常规地下城频道（类型 0）：显示在选频道主网格（每页 3×5）。</summary>
        MainGrid = 0,

        /// <summary>亡者峡谷频道（类型 11）：显示在底部分隔线下方 1×3 特殊频道栏（优先顺位）。</summary>
        SpecialTower = 1,

        /// <summary>交易/拍卖行频道（类型 3）：显示在底部分隔线下方 1×3 特殊频道栏（次席顺位）。</summary>
        SpecialTrade = 2,

        /// <summary>决斗场与自由练习场频道（类型 4..10、13..16，<c>0x010faf36..0x010faf6d</c>）：归入决斗场专用桶，不在选频道页开放。</summary>
        PvpArena = 3,

        /// <summary>其它不在选频道页展示的特殊、受限或未知类型频道。</summary>
        Hidden = 4,
    }

    /// <summary>服务端 <c>channel_info.etc</c> 中 <c>[server]</c> 块的单条频道槽位定义。</summary>
    public sealed class ChannelServerSlotRule
    {
        /// <summary>构造频道槽位定义。</summary>
        public ChannelServerSlotRule(
            int serverGroupId,
            int channelNo,
            int titleStringKind,
            string titleStringKey,
            string literalTitle,
            int channelKind,
            string areaTag)
        {
            ServerGroupId = serverGroupId;
            ChannelNo = channelNo;
            TitleStringKind = titleStringKind;
            TitleStringKey = titleStringKey ?? string.Empty;
            LiteralTitle = literalTitle ?? string.Empty;
            ChannelKind = channelKind;
            AreaTag = areaTag ?? string.Empty;
        }

        /// <summary>服务端组编号（1..14，如卡恩=1、狄瑞吉=2、希洛克=3 等）。</summary>
        public int ServerGroupId { get; }

        /// <summary>频道号（与 <c>#ch.XX</c> 里的数字对应）。</summary>
        public int ChannelNo { get; }

        /// <summary>标题为 <c>&lt;kind::key&gt;</c> 引用时的种类编号（如 4）；字面名称时为 -1。</summary>
        public int TitleStringKind { get; }

        /// <summary>标题为 <c>&lt;kind::key&gt;</c> 引用时的字符串键；否则为空串。</summary>
        public string TitleStringKey { get; }

        /// <summary>非文字列引用时的字面名称；否则为空串。</summary>
        public string LiteralTitle { get; }

        /// <summary>频道类型编号。</summary>
        public int ChannelKind { get; }

        /// <summary>关联区域/地下城标签（如 <c>[dragonroad]</c>、<c>[none]</c>）。</summary>
        public string AreaTag { get; }

        /// <summary>当前槽位在选频道界面中的展示归类。</summary>
        public ChannelSelectPlacement SelectPlacement => ClassifyChannelKind(ChannelKind);

        /// <summary>判断频道类型编号是否属于决斗场/自由练习场专用桶（<c>0x010faf36..0x010faf6d</c>：4..10 与 13..16）。</summary>
        public static bool IsPvpChannelKind(int channelKind)
        {
            return (channelKind >= 4 && channelKind <= 10) || (channelKind >= 13 && channelKind <= 16);
        }

        /// <summary>将 <c>channel_info.etc</c> 频道类型编号映射为选频道界面展示归类。</summary>
        public static ChannelSelectPlacement ClassifyChannelKind(int channelKind)
        {
            if (channelKind == 0)
            {
                return ChannelSelectPlacement.MainGrid;
            }
            if (channelKind == 11)
            {
                return ChannelSelectPlacement.SpecialTower;
            }
            if (channelKind == 3)
            {
                return ChannelSelectPlacement.SpecialTrade;
            }
            return IsPvpChannelKind(channelKind) ? ChannelSelectPlacement.PvpArena : ChannelSelectPlacement.Hidden;
        }
    }

    /// <summary>由 <c>ChannelScript.pvf</c> 与 <c>Etc/ChannelRecommendLevel.etc</c> 提取的脚本参数。</summary>
    public sealed class ChannelScriptData
    {
        /// <summary>构造脚本快照。</summary>
        public ChannelScriptData(
            string rawText,
            float fullThresholdPercent,
            IReadOnlyList<ChannelRecommendLevelRule> recommendLevels)
            : this(rawText, fullThresholdPercent, recommendLevels, null, false)
        {
        }

        /// <summary>构造含服务端 <c>[server]</c> 槽位表与显式满载标记的脚本快照。</summary>
        public ChannelScriptData(
            string rawText,
            float fullThresholdPercent,
            IReadOnlyList<ChannelRecommendLevelRule> recommendLevels,
            IReadOnlyList<ChannelServerSlotRule> serverSlots,
            bool hasExplicitFullThreshold = false)
        {
            RawText = rawText ?? string.Empty;
            FullThresholdPercent = fullThresholdPercent;
            RecommendLevels = recommendLevels ?? Array.Empty<ChannelRecommendLevelRule>();
            ServerSlots = serverSlots ?? Array.Empty<ChannelServerSlotRule>();
            HasExplicitFullThreshold = hasExplicitFullThreshold;
        }

        /// <summary>默认空脚本(满载阈值 95.0)。</summary>
        public static ChannelScriptData Default { get; } = new ChannelScriptData(
            string.Empty,
            ChannelServiceConstants.DefaultFullThresholdPercent,
            Array.Empty<ChannelRecommendLevelRule>(),
            Array.Empty<ChannelServerSlotRule>(),
            false);

        /// <summary>解密并去除尾部零填充后的原文。</summary>
        public string RawText { get; }

        /// <summary><c>[channel rate full]</c> 满载阈值(未配置时取 95.0f)。</summary>
        public float FullThresholdPercent { get; }

        /// <summary>脚本原文是否显式给出了有效的 <c>[channel rate full]</c>。</summary>
        public bool HasExplicitFullThreshold { get; }

        /// <summary>推荐等级规则列表。</summary>
        public IReadOnlyList<ChannelRecommendLevelRule> RecommendLevels { get; }

        /// <summary>服务端 <c>[server]</c> 块里的频道槽位定义列表。</summary>
        public IReadOnlyList<ChannelServerSlotRule> ServerSlots { get; }

        /// <summary>将线缆组标签映射到 <c>DNF.exe</c> <c>0x1af9af8</c> 的 14 个标准服务器组编号（1..14），兼容简繁体中文大区名。</summary>
        public static bool TryMapServerGroupId(string groupTag, out int serverGroupId)
        {
            serverGroupId = 0;
            if (string.IsNullOrWhiteSpace(groupTag))
            {
                return false;
            }
            string key = groupTag.Trim().ToLowerInvariant();
            if (TryMapNormalizedServerGroupId(key, out serverGroupId))
            {
                return true;
            }
            string trimmed = StripGroupTagNoise(key);
            return trimmed.Length > 0
                && !string.Equals(trimmed, key, StringComparison.Ordinal)
                && TryMapNormalizedServerGroupId(trimmed, out serverGroupId);
        }

        private static bool TryMapNormalizedServerGroupId(string key, out int serverGroupId)
        {
            switch (key)
            {
                case "cain":
                case "卡恩": serverGroupId = 1; return true;
                case "diregie":
                case "狄瑞吉": serverGroupId = 2; return true;
                case "siroco":
                case "希洛克": serverGroupId = 3; return true;
                case "prey":
                case "pray":
                case "普雷": serverGroupId = 4; return true;
                case "casillas":
                case "cassillas":
                case "卡西利亚斯":
                case "卡西利亞斯":
                case "卡西利爾斯": serverGroupId = 5; return true;
                case "hilder":
                case "赫尔德":
                case "赫爾德": serverGroupId = 6; return true;
                case "ruke":
                case "luke":
                case "卢克":
                case "盧克": serverGroupId = 7; return true;
                case "seria":
                case "cerria":
                case "赛丽亚":
                case "賽麗亞": serverGroupId = 8; return true;
                case "anton":
                case "安徒恩":
                case "安图恩":
                case "安圖恩": serverGroupId = 9; return true;
                case "roxy":
                case "lukse":
                case "卢克西":
                case "盧克西": serverGroupId = 10; return true;
                case "bakal":
                case "巴卡尔":
                case "巴卡爾": serverGroupId = 11; return true;
                case "ozma":
                case "奥兹玛":
                case "奧茲瑪": serverGroupId = 12; return true;
                case "khazan":
                case "kazan":
                case "卡赞":
                case "卡贊": serverGroupId = 13; return true;
                case "starter_server":
                case "starter":
                case "start":
                case "first":
                case "新手": serverGroupId = 14; return true;
                default:
                    serverGroupId = 0;
                    return false;
            }
        }

        private static string StripGroupTagNoise(string key)
        {
            string s = key;
            string[] suffixes = { "服务器", "伺服器", "server", "服务", "伺服", "服" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (s.EndsWith(suffixes[i], StringComparison.Ordinal))
                {
                    s = s.Substring(0, s.Length - suffixes[i].Length).Trim();
                    break;
                }
            }
            return s.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        }

        /// <summary>判断脚本 <c>[server]</c> 槽位表是否包含给定服务器组的定义（用于区分已建表组里的未知频道与未配表测试目录）。</summary>
        public bool HasServerSlotsForGroup(string groupTag)
        {
            if (ServerSlots.Count == 0 || !TryMapServerGroupId(groupTag, out int groupId))
            {
                return false;
            }
            for (int i = 0; i < ServerSlots.Count; i++)
            {
                if (ServerSlots[i].ServerGroupId == groupId)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>按服务器组标签与解析出的频道号查找 <c>[server]</c> 槽位定义。</summary>
        public ChannelServerSlotRule FindServerSlot(string groupTag, int channelNo)
        {
            if (channelNo <= 0 || ServerSlots.Count == 0)
            {
                return null;
            }

            bool hasGroupEntries = false;
            if (TryMapServerGroupId(groupTag, out int groupId))
            {
                for (int i = 0; i < ServerSlots.Count; i++)
                {
                    ChannelServerSlotRule slot = ServerSlots[i];
                    if (slot.ServerGroupId == groupId)
                    {
                        hasGroupEntries = true;
                        if (slot.ChannelNo == channelNo)
                        {
                            return slot;
                        }
                    }
                }
            }

            // 当前组标签已命中已知组且脚本中有该组条目、只是缺少这个频道号时,不拿别的组同号条目顶替;
            // 若脚本未收录该组(如自定义私服组名或单组模板),回退取首个同频道号条目。
            if (hasGroupEntries)
            {
                return null;
            }

            for (int i = 0; i < ServerSlots.Count; i++)
            {
                if (ServerSlots[i].ChannelNo == channelNo)
                {
                    return ServerSlots[i];
                }
            }
            return null;
        }

        /// <summary>合并本地回退脚本（如真服 0x09 只下发了 <c>[server]</c> 时补上本地的推荐等级与满载阈值）。</summary>
        public ChannelScriptData MergeFallback(ChannelScriptData fallback)
        {
            if (fallback == null)
            {
                return this;
            }
            float full = HasExplicitFullThreshold ? FullThresholdPercent : fallback.FullThresholdPercent;
            bool hasFull = HasExplicitFullThreshold || fallback.HasExplicitFullThreshold;
            IReadOnlyList<ChannelRecommendLevelRule> recommend = RecommendLevels.Count > 0 ? RecommendLevels : fallback.RecommendLevels;
            IReadOnlyList<ChannelServerSlotRule> slots = ServerSlots.Count > 0 ? ServerSlots : fallback.ServerSlots;
            if (full == FullThresholdPercent
                && hasFull == HasExplicitFullThreshold
                && ReferenceEquals(recommend, RecommendLevels)
                && ReferenceEquals(slots, ServerSlots))
            {
                return this;
            }
            return new ChannelScriptData(RawText, full, recommend, slots, hasFull);
        }

        /// <summary>按客户端 <c>0x010f4780</c> 规则去掉频道名内 ASCII 数字后,匹配推荐等级范围。</summary>
        public bool TryMatchRecommendLevel(string channelName, out int minLevel, out int maxLevel)
        {
            minLevel = 0;
            maxLevel = 0;
            if (string.IsNullOrEmpty(channelName) || RecommendLevels.Count == 0)
            {
                return false;
            }
            string normalized = StripAsciiDigits(channelName).Trim();
            for (int i = 0; i < RecommendLevels.Count; i++)
            {
                ChannelRecommendLevelRule rule = RecommendLevels[i];
                if (string.Equals(rule.Tag, normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.IndexOf(rule.Tag, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    minLevel = rule.MinLevel;
                    maxLevel = rule.MaxLevel;
                    return true;
                }
            }
            return false;
        }

        /// <summary>复刻 <c>0x010f4780</c>:把输入里的 ASCII '0'..'9' 分离掉,只保留非数字字符并解析数字。</summary>
        public static int SplitNameDigits(string input, out string nonDigitPrefix)
        {
            if (string.IsNullOrEmpty(input))
            {
                nonDigitPrefix = string.Empty;
                return 0;
            }
            StringBuilder prefix = new StringBuilder(input.Length);
            StringBuilder digits = new StringBuilder();
            for (int i = 0; i < input.Length; i++)
            {
                char ch = input[i];
                if (ch >= '0' && ch <= '9')
                {
                    digits.Append(ch);
                }
                else
                {
                    prefix.Append(ch);
                }
            }
            nonDigitPrefix = prefix.ToString();
            return digits.Length > 0 && int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int num)
                ? num
                : 0;
        }

        private static string StripAsciiDigits(string input)
        {
            SplitNameDigits(input, out string nonDigits);
            return nonDigits;
        }
    }

    /// <summary>
    /// 解析 <c>ChannelScript</c> / <c>ChannelRecommendLevel.etc</c> / <c>channel_info.etc</c> 中的阈值、推荐等级与频道槽位配置。
    /// </summary>
    public static class ChannelScriptParser
    {
        private const int ServerSlotFieldCount = 15;

        /// <summary>解析脚本明文;无法识别的行与 <c>comfortable/harmony/normal</c> 等客户端丢弃项安全跳过。</summary>
        public static ChannelScriptData Parse(string scriptText)
        {
            if (string.IsNullOrWhiteSpace(scriptText))
            {
                return ChannelScriptData.Default;
            }

            float fullThreshold = ChannelServiceConstants.DefaultFullThresholdPercent;
            bool hasExplicitFull = false;
            var rules = new List<ChannelRecommendLevelRule>();
            var serverSlots = new List<ChannelServerSlotRule>();
            bool inServerBlock = false;
            var serverTokens = new List<string>();

            string[] lines = scriptText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#"))
                {
                    continue;
                }

                if (inServerBlock)
                {
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        FlushServerBlock(serverTokens, serverSlots);
                        inServerBlock = false;
                        if (string.Equals(line, "[/server]", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        AppendServerTokens(Tokenize(line), serverTokens, serverSlots, ref inServerBlock);
                        continue;
                    }
                }

                if (TryReadAfterTag(line, "[channel rate full]", out string fullRest))
                {
                    if (float.TryParse(FirstToken(fullRest), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedFull) && parsedFull > 0f)
                    {
                        fullThreshold = parsedFull;
                        hasExplicitFull = true;
                    }
                    continue;
                }

                if (TryReadAfterTag(line, "[channel]", out string chanRest))
                {
                    if (TryParseRecommendRule(chanRest, out ChannelRecommendLevelRule rule))
                    {
                        rules.Add(rule);
                    }
                    continue;
                }

                if (TryReadAfterTag(line, "[server]", out string serverRest))
                {
                    inServerBlock = true;
                    serverTokens.Clear();
                    AppendServerTokens(Tokenize(serverRest), serverTokens, serverSlots, ref inServerBlock);
                    continue;
                }
            }

            if (inServerBlock)
            {
                FlushServerBlock(serverTokens, serverSlots);
            }

            return new ChannelScriptData(scriptText, fullThreshold, rules, serverSlots, hasExplicitFull);
        }

        private static bool TryReadAfterTag(string line, string tag, out string rest)
        {
            rest = null;
            if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            rest = line.Substring(tag.Length).Trim();
            return true;
        }

        private static void AppendServerTokens(
            List<string> lineTokens,
            List<string> serverTokens,
            List<ChannelServerSlotRule> serverSlots,
            ref bool inServerBlock)
        {
            for (int i = 0; i < lineTokens.Count; i++)
            {
                string tok = lineTokens[i];
                if (string.Equals(tok, "[/server]", StringComparison.OrdinalIgnoreCase))
                {
                    FlushServerBlock(serverTokens, serverSlots);
                    inServerBlock = false;
                    return;
                }
                serverTokens.Add(tok);
            }
        }

        private static void FlushServerBlock(List<string> tokens, List<ChannelServerSlotRule> into)
        {
            if (tokens.Count == 0)
            {
                return;
            }
            if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int serverGroupId) || serverGroupId <= 0)
            {
                tokens.Clear();
                return;
            }

            for (int idx = 1; idx + ServerSlotFieldCount <= tokens.Count; idx += ServerSlotFieldCount)
            {
                if (!int.TryParse(tokens[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int channelNo)
                    || !int.TryParse(tokens[idx + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int channelKind))
                {
                    break;
                }
                ParseTitleReference(tokens[idx + 1], out int titleKind, out string titleKey, out string literalTitle);
                string areaTag = tokens[idx + 3] ?? string.Empty;
                into.Add(new ChannelServerSlotRule(serverGroupId, channelNo, titleKind, titleKey, literalTitle, channelKind, areaTag));
            }
            tokens.Clear();
        }

        private static void ParseTitleReference(string token, out int kind, out string key, out string literal)
        {
            kind = -1;
            key = string.Empty;
            literal = string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                return;
            }
            if (token.Length >= 5 && token[0] == '<' && token[token.Length - 1] == '>')
            {
                int sep = token.IndexOf("::", StringComparison.Ordinal);
                if (sep > 1 && sep + 2 < token.Length - 1)
                {
                    string kindPart = token.Substring(1, sep - 1);
                    string keyPart = token.Substring(sep + 2, token.Length - 1 - (sep + 2));
                    if (int.TryParse(kindPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedKind)
                        && parsedKind >= 0
                        && !string.IsNullOrWhiteSpace(keyPart))
                    {
                        kind = parsedKind;
                        key = keyPart;
                        return;
                    }
                }
            }
            literal = token;
        }

        private static bool TryParseRecommendRule(string text, out ChannelRecommendLevelRule rule)
        {
            rule = null;
            List<string> tokens = Tokenize(text);
            if (tokens.Count < 3)
            {
                return false;
            }
            if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minLv)
                || !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxLv))
            {
                return false;
            }
            rule = new ChannelRecommendLevelRule(tokens[0], minLv, maxLv);
            return true;
        }

        private static string FirstToken(string text)
        {
            List<string> tokens = Tokenize(text);
            return tokens.Count > 0 ? tokens[0] : string.Empty;
        }

        private static List<string> Tokenize(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                if (text[i] == '"' || text[i] == '`')
                {
                    char quote = text[i];
                    int end = text.IndexOf(quote, i + 1);
                    if (end > i)
                    {
                        result.Add(text.Substring(i + 1, end - i - 1));
                        i = end + 1;
                    }
                    else
                    {
                        result.Add(text.Substring(i + 1));
                        break;
                    }
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                    result.Add(text.Substring(start, i - start));
                }
            }
            return result;
        }
    }

    /// <summary>拥挤度计算结果(对应 <c>DNF.exe</c> <c>0x010fad4e</c> 与 <c>0x010f5870</c>)。</summary>
    public readonly struct ChannelCongestionEvaluation
    {
        /// <summary>构造评估结果。</summary>
        public ChannelCongestionEvaluation(ChannelCongestionTier tier, bool isFull, int effectivePercent, uint argbColor)
        {
            Tier = tier;
            IsFull = isFull;
            EffectivePercent = effectivePercent;
            ArgbColor = argbColor;
        }

        /// <summary>档位。</summary>
        public ChannelCongestionTier Tier { get; }

        /// <summary>是否超过满载阈值。</summary>
        public bool IsFull { get; }

        /// <summary>按同组最大值归一化后的百分比(满载时仍记录整数计算值)。</summary>
        public int EffectivePercent { get; }

        /// <summary>客户端渲染用的 ARGB 颜色。</summary>
        public uint ArgbColor { get; }
    }

    /// <summary>频道拥挤度计算器(严格复刻 <c>DNF.exe</c> 浮点截断与分组封顶算法)。</summary>
    public static class ChannelCongestionEvaluator
    {
        /// <summary>
        /// 按 <c>DNF.exe</c> <c>0x010fad4e</c> 与 <c>0x010f5870</c> 计算频道拥挤档位:
        /// 1) 满载判定: <c>(float)currentLoad * 100.0f / (float)maxCapacity &gt; fullThresholdPercent</c>;
        /// 2) 否则: <c>effectiveMax = min((int)((float)maxCapacity * 0.7f), groupMaxLoad)</c>,
        ///    <c>pct = (int)((float)currentLoad * 100.0f / (float)effectiveMax)</c>,
        ///    再按 &lt;3(顺畅) / &lt;5(良好) / &lt;25(普通) / &gt;=25(拥挤) 分档。
        /// 边界防呆:当分母 &lt;= 0 时若 load &gt; 0 判满载/拥挤,load &lt;= 0 判顺畅,不抛出除以零异常。
        /// </summary>
        public static ChannelCongestionEvaluation Evaluate(
            int currentLoad,
            int maxCapacity,
            int groupMaxLoad,
            float fullThresholdPercent = ChannelServiceConstants.DefaultFullThresholdPercent)
        {
            if (currentLoad <= 0)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, 0, 0xff00ff00u);
            }
            if (maxCapacity <= 0)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Full, true, 100, 0xfff000ffu);
            }

            float rawPercent = ((float)currentLoad * 100.0f) / (float)maxCapacity;
            if (rawPercent > fullThresholdPercent)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Full, true, (int)rawPercent, 0xfff000ffu);
            }

            int scaledCap = (int)((float)maxCapacity * ChannelServiceConstants.EffectiveCapacityFactor);
            int effectiveMax = groupMaxLoad > 0 ? Math.Min(scaledCap, groupMaxLoad) : scaledCap;
            if (effectiveMax <= 0)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Crowded, false, 100, 0xff0000ffu);
            }

            int pct = (int)(((float)currentLoad * 100.0f) / (float)effectiveMax);
            if (pct < 3)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Comfortable, false, pct, 0xff00ff00u);
            }
            if (pct < 5)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Harmony, false, pct, 0xff00b1ffu);
            }
            if (pct < 25)
            {
                return new ChannelCongestionEvaluation(ChannelCongestionTier.Normal, false, pct, 0xff0080ffu);
            }
            return new ChannelCongestionEvaluation(ChannelCongestionTier.Crowded, false, pct, 0xff0000ffu);
        }
    }
}
