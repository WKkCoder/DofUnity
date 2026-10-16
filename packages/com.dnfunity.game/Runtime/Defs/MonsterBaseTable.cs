using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 怪物基础属性表(*.tbl)里的一行:一个等级对应的基础数值。文件是一串整数,每 24 个一行,
    /// 服务端加载函数(ImportMonsterBaseTable / InitMonsterScript)按"文件列 → 结构偏移"存放,
    /// 按等级计算属性的函数(setBasicAbilityByLevel)再按偏移取用(证据 E-062)。
    /// 字段名由移植者按用途起(观察到的是列序、偏移与去向;名字属推断,例如 HpBase 去向是"生命值基数")。
    /// </summary>
    public sealed class MonsterBaseRow
    {
        /// <summary>每行的整数个数。</summary>
        public const int Columns = 24;

        private readonly int[] _file;

        public MonsterBaseRow(int[] fileColumns)
        {
            if (fileColumns == null || fileColumns.Length != Columns)
            {
                throw new ArgumentException("基础属性表一行必须是 24 个整数", nameof(fileColumns));
            }
            _file = (int[])fileColumns.Clone();
        }

        /// <summary>文件第 n 列(从 1 数起)的原值。</summary>
        public int Column(int n)
        {
            return _file[n - 1];
        }

        /// <summary>第 1 列:这一行对应的等级(查表时按它匹配,不要求连续)。</summary>
        public int Level => _file[0];

        /// <summary>第 6 列:生命值基数(结构偏移 +0x04)。</summary>
        public int HpBase => _file[5];

        /// <summary>第 7 列:魔法值基数(+0x08)。</summary>
        public int MpBase => _file[6];

        /// <summary>第 2 列:物理攻击(+0x0C)。</summary>
        public int PhysicalAttack => _file[1];

        /// <summary>第 3 列:物理防御(+0x10)。</summary>
        public int PhysicalDefense => _file[2];

        /// <summary>第 4 列:魔法攻击(+0x14)。</summary>
        public int MagicalAttack => _file[3];

        /// <summary>第 5 列:魔法防御(+0x18)。</summary>
        public int MagicalDefense => _file[4];

        /// <summary>第 8 列:"装备"物理攻击(+0x1C)。</summary>
        public int EquipPhysicalAttack => _file[7];

        /// <summary>第 9 列:"装备"物理防御(+0x20)。</summary>
        public int EquipPhysicalDefense => _file[8];

        /// <summary>第 10 列:"装备"魔法攻击(+0x24)。</summary>
        public int EquipMagicalAttack => _file[9];

        /// <summary>第 11 列:"装备"魔法防御(+0x28)。</summary>
        public int EquipMagicalDefense => _file[10];

        /// <summary>第 12 列(+0x2C):按等级属性计算时除以 100 加进一个浮点字段(去向的含义未确认)。</summary>
        public int Extra2C => _file[11];

        /// <summary>第 13 列(+0x30):除以 10 取整后加进"硬直抗性"(异常状态抗性的第 13 项)。</summary>
        public int StuckResistanceTenths => _file[12];

        /// <summary>第 14 至 17 列(+0x34..+0x40):加到火、水、暗、光元素抗性上的数值。</summary>
        public int ElementResistanceAdd(int element)
        {
            return _file[13 + element];
        }

        /// <summary>第 18 至 21 列(+0x44..+0x50):加到火、水、暗、光元素攻击上的数值。</summary>
        public int ElementAttackAdd(int element)
        {
            return _file[17 + element];
        }

        /// <summary>第 22 列(+0x54):加到"受击恢复"上的数值。</summary>
        public int HitRecoveryAdd => _file[21];

        /// <summary>第 23 列(+0x58):加到"硬直度"([rigidity] 一项)上的数值。</summary>
        public int RigidityAdd => _file[22];

        /// <summary>第 24 列(+0x5C):加到除硬直抗性外的 16 项异常状态抗性上的数值。</summary>
        public int StatusResistanceAdd => _file[23];
    }

    /// <summary>
    /// 一份怪物基础属性表(一串行)。行按文件顺序保存;按等级查找取第一个匹配的行(服务端是线性查找),
    /// 行号还用来对应怪物自带的 [base table](同一行号)。
    /// </summary>
    public sealed class MonsterBaseTable
    {
        public MonsterBaseTable(IReadOnlyList<MonsterBaseRow> rows)
        {
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        }

        public IReadOnlyList<MonsterBaseRow> Rows { get; }

        public int Count => Rows.Count;

        /// <summary>第一个等级匹配的行号;没有返回 -1。</summary>
        public int IndexOfLevel(int level)
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].Level == level)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 把一份 .tbl 脚本(整数流)解成行:所有整数按出现顺序,每 24 个一行;末尾不足一行的整数丢弃(原版读到文件尾就停)。
        /// </summary>
        public static MonsterBaseTable Parse(ScriptLine[] lines)
        {
            var ints = new List<int>();
            if (lines != null)
            {
                foreach (ScriptLine line in lines)
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.IsNumber)
                        {
                            ints.Add(v.Int);
                        }
                    }
                }
            }
            var rows = new List<MonsterBaseRow>(ints.Count / MonsterBaseRow.Columns);
            var buffer = new int[MonsterBaseRow.Columns];
            for (int start = 0; start + MonsterBaseRow.Columns <= ints.Count; start += MonsterBaseRow.Columns)
            {
                ints.CopyTo(start, buffer, 0, MonsterBaseRow.Columns);
                rows.Add(new MonsterBaseRow(buffer));
            }
            return new MonsterBaseTable(rows);
        }
    }

    /// <summary>
    /// 五张公共怪物基础属性表:普通(0)、有名(1)、首领(2)、召唤(3)、战争房(4)。
    /// 选哪一张见 <see cref="MonsterAbility.SelectTable"/>;路径取自服务端的文件名表(证据 E-062)。
    /// </summary>
    public sealed class MonsterBaseTables
    {
        public const int Common = 0;
        public const int Named = 1;
        public const int Boss = 2;
        public const int Summon = 3;
        public const int WarRoom = 4;
        public const int TableCount = 5;

        /// <summary>五张表的 PVF 路径,下标即表号。</summary>
        public static readonly string[] Paths =
        {
            "monster/commonmonsterbaseparameter.tbl",
            "monster/namedmonsterbaseparameter.tbl",
            "monster/bossmonsterbaseparameter.tbl",
            "monster/summonmonsterbaseparameter.tbl",
            "monster/warroommonsterbaseparameter.tbl",
        };

        private readonly MonsterBaseTable[] _tables;

        public MonsterBaseTables(MonsterBaseTable[] tables)
        {
            if (tables == null || tables.Length != TableCount)
            {
                throw new ArgumentException("需要五张基础属性表", nameof(tables));
            }
            _tables = tables;
        }

        public MonsterBaseTable this[int index] => _tables[index];

        /// <summary>从游戏数据读五张表;缺的表用空表代替(查不到行时按等级计算会得到全默认的属性)。</summary>
        public static MonsterBaseTables Load(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            var tables = new MonsterBaseTable[TableCount];
            for (int i = 0; i < TableCount; i++)
            {
                tables[i] = MonsterBaseTable.Parse(data.ReadScript(Paths[i]));
            }
            return new MonsterBaseTables(tables);
        }
    }
}
