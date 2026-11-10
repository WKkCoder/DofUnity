using System;
using Dnf.Data;

// 服务端静态读码:initCreatureScript 0x08a35d37(固定读 99 个整数进 ExpTable_)、get_creature_level_exp 0x0836053c、get_creature_exp_level 0x08360562。
namespace Dnf.Game
{
    /// <summary>
    /// 宠物全局经验表(<c>exptable.tbl</c>):第 <c>l</c> 级(2 到 99)要的累计经验是第 <c>l − 2</c> 项,1 级与表外是 0。
    /// <para>
    /// 装载照服务端:无论文件里有几个整数,固定读 99 次整数;文件读完以后再读得到 0,所以文件比 99 项短时后面的门槛全是 0
    /// (数据里的表确实比 99 项短)。这个怪癖要保留:表算出来的等级最高是 98(循环上界),经验超过最后一个有效门槛以后,
    /// 后面那些 0 门槛一路满足,表等级直接跳到 98;宠物脚本的 <c>[max level]</c> 会把它夹住,所以数据里不会外露。
    /// </para>
    /// </summary>
    public sealed class PetExpTable
    {
        private readonly int[] _thresholds = new int[PetRules.ExpTableEntries];

        /// <summary>用给定门槛构造(不足 99 项的位置补 0,多出的忽略);测试用。</summary>
        public PetExpTable(params int[] thresholds)
        {
            int n = Math.Min(thresholds == null ? 0 : thresholds.Length, _thresholds.Length);
            for (int i = 0; i < n; i++)
            {
                _thresholds[i] = thresholds[i];
            }
            ReadCount = n;
        }

        /// <summary>装载时有几次整数读取成功(读到文件结尾后的读取失败,值记 0)。</summary>
        public int ReadCount { get; }

        private PetExpTable(int[] values, int readCount)
        {
            Array.Copy(values, _thresholds, _thresholds.Length);
            ReadCount = readCount;
        }

        /// <summary>读一份经验表的原始字节;不是标记流脚本返回 null。</summary>
        public static PetExpTable Parse(ReadOnlySpan<byte> data, PvfStringTable strings)
        {
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings);
        }

        /// <summary>读一份已经拆成标记的经验表:固定读 99 次整数,读不到的记 0。</summary>
        public static PetExpTable Parse(ScriptToken[] tokens, PvfStringTable strings)
        {
            var cursor = new ScriptCursor(tokens, strings);
            var values = new int[PetRules.ExpTableEntries];
            int read = 0;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = cursor.ScanInt(out bool ok);
                if (ok)
                {
                    read++;
                }
            }
            return new PetExpTable(values, read);
        }

        /// <summary>
        /// 装载进来的第 <paramref name="index"/> 项原始值(0 到 98;读不到的是 0)。第 99 项(下标 98)服务端读了、但等级换算用不到——
        /// 第 <c>l</c> 级只查第 <c>l − 2</c> 项,最高查到下标 97;对拍装载结果时要逐项比,所以单独给出。
        /// </summary>
        public int Entry(int index)
        {
            return (uint)index < (uint)_thresholds.Length ? _thresholds[index] : 0;
        }

        /// <summary>到达 <paramref name="level"/> 级要的累计经验(服务端 <c>get_creature_level_exp</c>);1 级与 2 到 99 以外是 0。</summary>
        public int ExpToReach(int level)
        {
            return level < 2 || level > PetRules.ExpTableEntries ? 0 : _thresholds[level - 2];
        }

        /// <summary>
        /// 累计经验对应的表等级(服务端 <c>get_creature_exp_level</c>):从 1 开始,门槛不大于经验就升一级,到 99 为止,返回升到的级数减一。
        /// 经验为负时第一道门槛(1 级的 0)就不满足,得 0;最高 98。
        /// </summary>
        public int LevelOfExp(int exp)
        {
            int level = 1;
            while (level < PetRules.ExpTableEntries && !(ExpToReach(level) > exp))
            {
                level++;
            }
            return level - 1;
        }
    }
}
