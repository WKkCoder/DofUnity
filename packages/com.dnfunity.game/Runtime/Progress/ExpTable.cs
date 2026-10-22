using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-111(服务端 CDataManager::get_level_exp / get_level_up_exp / get_exp_level 与升级经验表的载入,逐函数读法见台账)。
namespace Dnf.Game
{
    /// <summary>
    /// 升级经验表(character/exptable.tbl;证据 E-111)。文件没有标签,只是一串整数:第 i 个(从 0 数)是"升到第 i + 2 级所需的累计经验"。
    /// 服务端的取法(get_level_exp):等级在 2 到 200 之间取表里第 (等级 - 2) 项,其余(含 1 级)是 0;表里没写到的项也是 0。
    /// 一级里要攒的经验(get_level_up_exp)= 下一级的累计经验 - 本级的累计经验;所以 1 级要攒的就是表的第一项。
    /// 表的数值是原版数据,不入库;这里只管取法。
    /// </summary>
    public sealed class ExpTable
    {
        public const string Path = "character/exptable.tbl";

        /// <summary>服务端取累计经验的等级下限(含):1 级固定是 0。</summary>
        public const int MinTableLevel = 2;

        /// <summary>服务端取累计经验的等级上限(含)。</summary>
        public const int MaxTableLevel = 200;

        private readonly int[] _thresholds;

        public ExpTable(IReadOnlyList<int> thresholds)
        {
            if (thresholds == null)
            {
                throw new ArgumentNullException(nameof(thresholds));
            }
            _thresholds = new int[thresholds.Count];
            for (int i = 0; i < _thresholds.Length; i++)
            {
                _thresholds[i] = thresholds[i];
            }
        }

        /// <summary>表里写了几项(没有表时是 0)。</summary>
        public int Count => _thresholds.Length;

        /// <summary>升到 <paramref name="level"/> 级所需的累计经验(服务端 get_level_exp);1 级与范围外是 0。</summary>
        public int LevelExp(int level)
        {
            if (level < MinTableLevel || level > MaxTableLevel)
            {
                return 0;
            }
            int index = level - MinTableLevel;
            return index < _thresholds.Length ? _thresholds[index] : 0;
        }

        /// <summary>
        /// 在 <paramref name="level"/> 级里要攒的经验(服务端 get_level_up_exp):<c>LevelExp(level + 1) - LevelExp(level)</c>,按 32 位整数相减。
        /// 表之外是 0(升级循环据此把"没有下一级"当成封顶)。
        /// </summary>
        public int Section(int level)
        {
            return unchecked(LevelExp(level + 1) - LevelExp(level));
        }

        /// <summary>
        /// 累计经验对应的等级(服务端 get_exp_level):从 1 级往上数,累计经验够的最高等级,最多 99。
        /// </summary>
        public int LevelForExp(int exp)
        {
            int level = 1;
            while (level < 100 && LevelExp(level) <= exp)
            {
                level++;
            }
            return level - 1;
        }

        /// <summary>读 character/exptable.tbl 的脚本行:所有整数按出现顺序就是表。<paramref name="lines"/> 为 null 返回空表。</summary>
        public static ExpTable Parse(ScriptLine[] lines)
        {
            var values = new List<int>();
            if (lines != null)
            {
                foreach (ScriptLine line in lines)
                {
                    values.AddRange(line.Ints());
                }
            }
            return new ExpTable(values);
        }
    }
}
