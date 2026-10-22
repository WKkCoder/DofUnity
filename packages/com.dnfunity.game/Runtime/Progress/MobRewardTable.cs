using System;
using System.Collections.Generic;
using System.Globalization;

// 证据:E-111(服务端 CDataManager::get_mob_reward / set_mob_reward:数据来自数据库表 monster_reward_ref 的 (level, exp) 两列)。
namespace Dnf.Game
{
    /// <summary>
    /// 怪物的基础经验表(按怪物等级;证据 E-111)。服务端把数据库表 monster_reward_ref 的 (level, exp) 读进一个按等级下标的数组,
    /// 取法(get_mob_reward):等级在 1 到 199 之间取该项,其余是 0。<b>这张表不在客户端数据(Script.pvf)里</b>,所以不能从用户自备的客户端数据复原;
    /// 有两种来源:
    /// <list type="bullet">
    /// <item>用户自己服务端的导出——一份文本,每行 <c>等级,经验</c>(<see cref="ParseCsv"/>;逗号、分号、制表符或空格分隔,<c>#</c> 开头是注释,第一行是标题也行);</item>
    /// <item>没有导出时用 <see cref="Placeholder"/>:<b>占位值,不是原版数据</b>——取"本级经验区间的 1/25"(最少 2),让同级怪物大约 50 只升一级(乘上 /2 以后),只为让经验条动起来。
    /// 用了占位值的地方(<see cref="IsPlaceholder"/>)必须在日志和文档里说明。</item>
    /// </list>
    /// </summary>
    public sealed class MobRewardTable
    {
        /// <summary>服务端取基础经验的最大等级(含)。</summary>
        public const int MaxLevel = 199;

        /// <summary>占位表的"区间除数":基础经验 = 本级经验区间 / 25。</summary>
        public const int PlaceholderDivisor = 25;

        private readonly uint[] _byLevel = new uint[MaxLevel + 1];

        /// <summary>这是不是占位值(不是原版数据)。</summary>
        public bool IsPlaceholder { get; }

        /// <summary>来源说明(给日志用):"csv" 或 "placeholder"。</summary>
        public string Source { get; }

        /// <summary>表里有几个等级有非 0 的值。</summary>
        public int Count { get; }

        private MobRewardTable(uint[] byLevel, bool placeholder, string source)
        {
            Array.Copy(byLevel, _byLevel, Math.Min(byLevel.Length, _byLevel.Length));
            IsPlaceholder = placeholder;
            Source = source;
            int count = 0;
            for (int i = 1; i <= MaxLevel; i++)
            {
                if (_byLevel[i] != 0)
                {
                    count++;
                }
            }
            Count = count;
        }

        /// <summary>等级 <paramref name="level"/> 的怪物的基础经验(get_mob_reward);1 到 199 之外是 0。</summary>
        public uint BaseExp(int level)
        {
            return level < 1 || level > MaxLevel ? 0u : _byLevel[level];
        }

        /// <summary>按 (等级, 经验) 行造表;越界的等级与负的经验被忽略,同一等级后面的覆盖前面的。</summary>
        public static MobRewardTable FromRows(IEnumerable<KeyValuePair<int, long>> rows, string source = "csv")
        {
            var values = new uint[MaxLevel + 1];
            if (rows != null)
            {
                foreach (KeyValuePair<int, long> row in rows)
                {
                    if (row.Key >= 1 && row.Key <= MaxLevel && row.Value >= 0)
                    {
                        values[row.Key] = (uint)Math.Min(row.Value, (long)uint.MaxValue);
                    }
                }
            }
            return new MobRewardTable(values, false, source);
        }

        /// <summary>
        /// 读导出的文本:每行两个整数 等级 与 经验(分隔符:逗号、分号、制表符、空格);空行、<c>#</c> 开头的行、读不成两个整数的行(比如标题行)都跳过。
        /// </summary>
        public static MobRewardTable ParseCsv(string text)
        {
            var rows = new List<KeyValuePair<int, long>>();
            if (!string.IsNullOrEmpty(text))
            {
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }
                    string[] parts = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2
                        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
                        && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long exp))
                    {
                        rows.Add(new KeyValuePair<int, long>(level, exp));
                    }
                }
            }
            return FromRows(rows, "csv");
        }

        /// <summary>占位表(不是原版数据,见类说明):每级取 <c>max(2, 本级经验区间 / 25)</c>;经验表之外的等级是 0。</summary>
        public static MobRewardTable Placeholder(ExpTable table)
        {
            var values = new uint[MaxLevel + 1];
            if (table != null)
            {
                for (int level = 1; level <= MaxLevel; level++)
                {
                    int section = table.Section(level);
                    values[level] = section <= 0 ? 0u : (uint)Math.Max(2, section / PlaceholderDivisor);
                }
            }
            return new MobRewardTable(values, true, "placeholder");
        }
    }
}
