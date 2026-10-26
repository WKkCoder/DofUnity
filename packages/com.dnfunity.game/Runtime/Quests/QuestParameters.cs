using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-123(服务端静态读码,非 REA)。importQuestParameterScript 0x08a6b660 读 n_quest/questparameter.etc;
// QuestParameterScript::getRewardWeight 0x08a6bd1c(字母不在表里按 100)、getRewardExp 0x08a6bd86 与 getRewardGold 0x08a6bda6(按下标直接取表);
// CUser::compute_level_penalty 0x0866e3a8(等级差 7 到 11 用"绿色惩罚"、12 以上用"灰色惩罚"、其余 100)。
namespace Dnf.Game
{
    /// <summary>
    /// 任务奖励的参数表(<c>n_quest/questparameter.etc</c>):难度字母对应的经验权重、等级差惩罚百分数、按等级的经验表与金币表。
    /// 没读的:碎片奖励表与碎片属性(<c>[piece reward table]</c>、<c>[piece reward]</c>)、<c>[bal kun penalty]</c>。
    /// </summary>
    public sealed class QuestParameters
    {
        /// <summary>参数脚本的路径。</summary>
        public const string ScriptPath = "n_quest/questparameter.etc";

        /// <summary>等级差(角色等级减任务最低等级)达到这个值起,用"绿色惩罚"。</summary>
        public const int GreenFromDifference = 7;

        /// <summary>等级差达到这个值起,用"灰色惩罚"。</summary>
        public const int GreyFromDifference = 12;

        private readonly Dictionary<char, int> _weights = new Dictionary<char, int>();
        private int[] _exp = Array.Empty<int>();
        private int[] _gold = Array.Empty<int>();

        /// <summary>绿色惩罚的百分数(默认 100 = 不惩罚)。</summary>
        public int GreenPenalty { get; private set; } = 100;

        /// <summary>灰色惩罚的百分数(默认 100)。</summary>
        public int GreyPenalty { get; private set; } = 100;

        /// <summary>经验表的长度。</summary>
        public int ExpTableLength => _exp.Length;

        /// <summary>金币表的长度。</summary>
        public int GoldTableLength => _gold.Length;

        /// <summary>解脚本;<paramref name="lines"/> 为 null 得到全默认的空参数(权重都是 100、表为空)。</summary>
        public static QuestParameters Parse(ScriptLine[] lines)
        {
            var p = new QuestParameters();
            if (lines == null)
            {
                return p;
            }
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    continue;
                }
                switch (line.Tag.ToLowerInvariant())
                {
                    case "[difficulty]":
                        p.ReadWeights(line);
                        break;
                    case "[green level penalty]":
                        p.GreenPenalty = FirstInt(line, p.GreenPenalty);
                        break;
                    case "[grey level penalty]":
                        p.GreyPenalty = FirstInt(line, p.GreyPenalty);
                        break;
                    case "[exp reward table]":
                        p._exp = line.Ints().ToArray();
                        break;
                    case "[gold reward table]":
                        p._gold = line.Ints().ToArray();
                        break;
                }
            }
            return p;
        }

        /// <summary>难度字母的经验权重(百分数);字母不在表里(包括没写字母 '\0')是 100。</summary>
        public int Weight(char letter)
        {
            return _weights.TryGetValue(letter, out int w) ? w : 100;
        }

        /// <summary>经验表按等级取(下标就是等级;越界按最近的一端,空表为 0)。</summary>
        public int ExpAt(int level)
        {
            return At(_exp, level);
        }

        /// <summary>金币表按等级取(规则同 <see cref="ExpAt"/>)。</summary>
        public int GoldAt(int level)
        {
            return At(_gold, level);
        }

        /// <summary>
        /// 等级差惩罚的百分数:差(角色等级减任务最低等级)小于 7 为 100(角色比任务低也是 100),
        /// 7 到 11 用绿色惩罚,12 及以上用灰色惩罚。
        /// </summary>
        public int LevelPenalty(int difference)
        {
            if (difference < GreenFromDifference)
            {
                return 100;
            }
            return difference < GreyFromDifference ? GreenPenalty : GreyPenalty;
        }

        private static int At(int[] table, int level)
        {
            if (table.Length == 0)
            {
                return 0;
            }
            if (level < 0)
            {
                level = 0;
            }
            if (level >= table.Length)
            {
                level = table.Length - 1;
            }
            return table[level];
        }

        private static int FirstInt(ScriptLine line, int fallback)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    return v.Int;
                }
            }
            return fallback;
        }

        /// <summary><c>[difficulty]</c> 是"字母 权重"成对地排。</summary>
        private void ReadWeights(ScriptLine line)
        {
            ScriptValue[] values = line.Values;
            for (int i = 0; i + 1 < values.Length; i++)
            {
                if (values[i].Kind == ScriptValueKind.String && !string.IsNullOrEmpty(values[i].Text) && values[i + 1].IsNumber)
                {
                    _weights[values[i].Text[0]] = values[i + 1].Int;
                    i++;
                }
            }
        }
    }
}
