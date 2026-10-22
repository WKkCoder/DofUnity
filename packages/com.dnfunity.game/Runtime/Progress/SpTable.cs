using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-111(服务端 importSpPerLevelReferenceTable、CDataManager::set_reward_sp 与 GetSpAtLevelUp)。
namespace Dnf.Game
{
    /// <summary>
    /// 每升一级给的技能点(etc/sptable.etc 的 [sp table];证据 E-111)。块里是"等级 技能点"成对的整数;
    /// 服务端的导入函数把每一对的第二个数依次放进数组(下标 0 先放一个 0,等级那一列读了就扔,不用它定位),
    /// 所以第 k 对(从 1 数)的技能点就是下标 k 的项——文件里的等级列恰好是 1、2、3……时与"按等级取"等价。
    /// 取法(GetSpAtLevelUp):等级在 1 到 95 之间取该下标的项,其余是 0。数值是原版数据,不入库。
    /// </summary>
    public sealed class SpTable
    {
        public const string Path = "etc/sptable.etc";

        /// <summary>服务端取技能点的最大等级(含)。</summary>
        public const int MaxLevel = 95;

        private readonly int[] _byLevel;

        public SpTable(IReadOnlyList<int> byLevel)
        {
            if (byLevel == null)
            {
                throw new ArgumentNullException(nameof(byLevel));
            }
            _byLevel = new int[byLevel.Count];
            for (int i = 0; i < _byLevel.Length; i++)
            {
                _byLevel[i] = byLevel[i];
            }
        }

        /// <summary>数组长度(含下标 0 的那个 0);没有表时是 0。</summary>
        public int Count => _byLevel.Length;

        /// <summary>升到 <paramref name="level"/> 级时给的技能点(服务端 GetSpAtLevelUp);1 到 95 之外或表里没写到是 0。</summary>
        public int AtLevelUp(int level)
        {
            if (level < 1 || level > MaxLevel || level >= _byLevel.Length)
            {
                return 0;
            }
            return _byLevel[level];
        }

        /// <summary>读 etc/sptable.etc:找 [sp table] 块,每一对 (等级, 技能点) 取第二个数。没有该块返回空表。</summary>
        public static SpTable Parse(ScriptLine[] lines)
        {
            var values = new List<int> { 0 };
            bool found = false;
            if (lines != null)
            {
                foreach (ScriptLine line in lines)
                {
                    if (!line.TagIs("[sp table]"))
                    {
                        continue;
                    }
                    found = true;
                    List<int> ints = line.Ints();
                    for (int i = 0; i + 1 < ints.Count; i += 2)
                    {
                        values.Add(ints[i + 1]);
                    }
                    break;
                }
            }
            return found ? new SpTable(values) : new SpTable(new int[0]);
        }
    }
}
