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

    /// <summary>由 <c>ChannelScript.pvf</c> 与 <c>Etc/ChannelRecommendLevel.etc</c> 提取的脚本参数。</summary>
    public sealed class ChannelScriptData
    {
        /// <summary>构造脚本快照。</summary>
        public ChannelScriptData(
            string rawText,
            float fullThresholdPercent,
            IReadOnlyList<ChannelRecommendLevelRule> recommendLevels,
            IReadOnlyCollection<string> pvpChannelNames)
        {
            RawText = rawText ?? string.Empty;
            FullThresholdPercent = fullThresholdPercent;
            RecommendLevels = recommendLevels ?? Array.Empty<ChannelRecommendLevelRule>();
            PvpChannelNames = pvpChannelNames ?? Array.Empty<string>();
        }

        /// <summary>默认空脚本(满载阈值 95.0)。</summary>
        public static ChannelScriptData Default { get; } = new ChannelScriptData(
            string.Empty,
            ChannelServiceConstants.DefaultFullThresholdPercent,
            Array.Empty<ChannelRecommendLevelRule>(),
            Array.Empty<string>());

        /// <summary>解密并去除尾部零填充后的原文。</summary>
        public string RawText { get; }

        /// <summary><c>[channel rate full]</c> 满载阈值(未配置时取 95.0f)。</summary>
        public float FullThresholdPercent { get; }

        /// <summary>推荐等级规则列表。</summary>
        public IReadOnlyList<ChannelRecommendLevelRule> RecommendLevels { get; }

        /// <summary>PVP 频道名集合(若有)。</summary>
        public IReadOnlyCollection<string> PvpChannelNames { get; }

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

    /// <summary>解析 <c>ChannelScript</c> / <c>ChannelRecommendLevel.etc</c> 中的阈值与推荐等级配置。</summary>
    public static class ChannelScriptParser
    {
        /// <summary>解析脚本明文;无法识别的行与 <c>comfortable/harmony/normal</c> 等客户端丢弃项安全跳过。</summary>
        public static ChannelScriptData Parse(string scriptText)
        {
            if (string.IsNullOrWhiteSpace(scriptText))
            {
                return ChannelScriptData.Default;
            }

            float fullThreshold = ChannelServiceConstants.DefaultFullThresholdPercent;
            var rules = new List<ChannelRecommendLevelRule>();
            var pvpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] lines = scriptText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#"))
                {
                    continue;
                }

                if (TryReadAfterTag(line, "[channel rate full]", out string fullRest))
                {
                    if (float.TryParse(FirstToken(fullRest), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedFull) && parsedFull > 0f)
                    {
                        fullThreshold = parsedFull;
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

                if (TryReadAfterTag(line, "[pvp channel]", out string pvpRest))
                {
                    string name = Unquote(FirstToken(pvpRest));
                    if (!string.IsNullOrEmpty(name))
                    {
                        pvpNames.Add(name);
                    }
                }
            }

            return new ChannelScriptData(scriptText, fullThreshold, rules, pvpNames);
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

        private static string Unquote(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }
            return value ?? string.Empty;
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
                if (text[i] == '"')
                {
                    int end = text.IndexOf('"', i + 1);
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
