using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 技能的"按等级取值"表:[level info] 的第一个数是每级的列数 N,后面每 N 个数是一级(第 1 级在最前)。
    /// 结构在全库核对过:整数个数减 1 能被 N 整除(见 E-036 的核对);第 N 列叫什么、怎么用由技能脚本决定。
    /// </summary>
    public sealed class SkillLevelTable
    {
        private readonly int[] _values;

        public int Columns { get; }
        public int Levels { get; }

        public SkillLevelTable(int columns, int[] values)
        {
            Columns = columns;
            _values = values;
            Levels = columns > 0 ? values.Length / columns : 0;
        }

        public static readonly SkillLevelTable Empty = new SkillLevelTable(0, Array.Empty<int>());

        /// <summary>取第 level 级(从 1 开始)第 column 列(从 0 开始)的值;超出范围时返回 0。等级超出表长时取最后一级。</summary>
        public int Get(int level, int column)
        {
            if (Levels == 0 || column < 0 || column >= Columns)
            {
                return 0;
            }
            int row = level - 1;
            if (row < 0)
            {
                row = 0;
            }
            if (row >= Levels)
            {
                row = Levels - 1;
            }
            return _values[row * Columns + column];
        }

        /// <summary>整数个数是否能被列数整除(结构自洽)。</summary>
        public bool IsConsistent => Columns > 0 && _values.Length % Columns == 0;
    }

    /// <summary>
    /// 一个 .skl 技能脚本里与运行时有关的数据:静态数据([static data])、按等级取值表([level info]),
    /// 以及决斗场([pvp])里的同名两项、可以使用的状态、冷却与耗蓝的原始数字。
    /// 数字的单位与换算(耗蓝两个数、冷却两个数分别是什么)**未验证**,这里按原样保存。
    /// </summary>
    public sealed class SkillDef
    {
        public int Id { get; }

        /// <summary>规范化的 PVF 路径,例如 skill/atmage/icecrash.skl。</summary>
        public string Path { get; }

        public int[] StaticData { get; }
        public SkillLevelTable LevelInfo { get; }
        public int[] PvpStaticData { get; }
        public SkillLevelTable PvpLevelInfo { get; }

        /// <summary>[executable states]:技能可以从哪些状态编号发出(没有这个标签时为空)。</summary>
        public int[] ExecutableStates { get; }

        public int[] ConsumeMp { get; }
        public int[] CoolTime { get; }
        public int RequiredLevel { get; }
        public int MaximumLevel { get; }

        /// <summary>[type] 的取值,如 "[active]"、"[passive]";没有时为 null。</summary>
        public string Type { get; }

        public SkillDef(int id, string path, ScriptLine[] lines)
        {
            Id = id;
            Path = path;
            var table = new TagTable(lines);
            ExecutableStates = table.Ints("[executable states]");
            ConsumeMp = table.Ints("[consume MP]");
            CoolTime = table.Ints("[cool time]");
            RequiredLevel = table.IntOr("[required level]", 0);
            MaximumLevel = table.IntOr("[maximum level]", 0);
            Type = table.FirstString("[type]");

            // 按出现顺序走一遍:[pvp] ... [/pvp] 之间的 [static data]/[level info] 属于决斗场,其余属于普通模式
            int[] pveStatic = Array.Empty<int>();
            int[] pvpStatic = Array.Empty<int>();
            SkillLevelTable pveLevels = SkillLevelTable.Empty;
            SkillLevelTable pvpLevels = SkillLevelTable.Empty;
            bool inPvp = false;
            bool haveStatic = false;
            bool haveLevels = false;
            bool havePvpStatic = false;
            bool havePvpLevels = false;
            foreach (ScriptLine line in lines)
            {
                if (line.TagIs("[pvp]"))
                {
                    inPvp = true;
                }
                else if (line.TagIs("[/pvp]"))
                {
                    inPvp = false;
                }
                else if (line.TagIs("[static data]"))
                {
                    int[] values = line.Ints().ToArray();
                    if (inPvp && !havePvpStatic)
                    {
                        pvpStatic = values;
                        havePvpStatic = true;
                    }
                    else if (!inPvp && !haveStatic)
                    {
                        pveStatic = values;
                        haveStatic = true;
                    }
                }
                else if (line.TagIs("[level info]"))
                {
                    List<int> all = line.Ints();
                    if (all.Count == 0)
                    {
                        continue;
                    }
                    int columns = all[0];
                    int[] rest = all.GetRange(1, all.Count - 1).ToArray();
                    var levels = new SkillLevelTable(columns, rest);
                    if (inPvp && !havePvpLevels)
                    {
                        pvpLevels = levels;
                        havePvpLevels = true;
                    }
                    else if (!inPvp && !haveLevels)
                    {
                        pveLevels = levels;
                        haveLevels = true;
                    }
                }
            }
            StaticData = pveStatic;
            PvpStaticData = pvpStatic;
            LevelInfo = pveLevels;
            PvpLevelInfo = pvpLevels;
        }

        public int GetStatic(int index, bool pvp)
        {
            int[] data = pvp && PvpStaticData.Length > 0 ? PvpStaticData : StaticData;
            return (uint)index < (uint)data.Length ? data[index] : 0;
        }

        public int GetLevel(int level, int column, bool pvp)
        {
            SkillLevelTable t = pvp && PvpLevelInfo.Levels > 0 ? PvpLevelInfo : LevelInfo;
            return t.Get(level, column);
        }
    }
}
