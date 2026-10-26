using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-125(服务端 upgradeAbilityByDifficultyOfDungeon 的三个重载 0x088b8c37 / 0x088b85cf / 0x088b813a;InitMonsterScript 0x08a3b0ad 读
// monster/monsterapcdifficultybonus.tbl;ImportDungeonScript 0x088e5faa 读 [monster difficulty bonus];set_monster_ability 0x083026da 的调用次序;
// getToleranceActiveStatus 0x08891b2a)。三种算法都用 Unicorn 把服务端机器码跑一遍对拍过(tools/py/mob_difficulty_oracle.py)。
namespace Dnf.Game
{
    /// <summary>
    /// 副本自带的怪物难度加成:.dgn 里的 <c>[monster difficulty bonus]</c>,13 组 × 5 个难度的浮点数(与公共表的全局块同形)。
    /// 有它时每组的系数在公共表的全局系数之外再乘一次(服务端 upgradeAbilityByDifficultyOfDungeon 的第三个重载);没有它就按怪物等级查公共表的行。
    /// </summary>
    public sealed class DungeonDifficultyBonus
    {
        public const string Tag = "[monster difficulty bonus]";

        private readonly float[] _values;

        public DungeonDifficultyBonus(float[] values)
        {
            if (values == null || values.Length != MonsterDifficultyTable.Groups * MonsterDifficultyTable.Difficulties)
            {
                throw new ArgumentException("副本自带的难度加成必须是 13 组 × 5 个难度", nameof(values));
            }
            _values = (float[])values.Clone();
        }

        /// <summary>第 <paramref name="group"/> 组、难度 <paramref name="difficulty"/> 的系数。</summary>
        public float Value(int group, int difficulty)
        {
            return _values[group * MonsterDifficultyTable.Difficulties + difficulty];
        }

        /// <summary>
        /// 读 .dgn 脚本里的 <c>[monster difficulty bonus]</c>:前 65 个数(按组、再按难度排);没有这个标签或不足 65 个数返回 null
        /// (服务端遇到不足的写法会让整个副本读不进来,这里当作没有)。
        /// </summary>
        public static DungeonDifficultyBonus Parse(ScriptLine[] dungeonScript)
        {
            if (dungeonScript == null)
            {
                return null;
            }
            foreach (ScriptLine line in dungeonScript)
            {
                if (!line.TagIs(Tag))
                {
                    continue;
                }
                List<float> numbers = MonsterDifficultyTable.Numbers(line.Values);
                int need = MonsterDifficultyTable.Groups * MonsterDifficultyTable.Difficulties;
                if (numbers.Count < need)
                {
                    return null;
                }
                return new DungeonDifficultyBonus(numbers.GetRange(0, need).ToArray());
            }
            return null;
        }
    }

    /// <summary>
    /// 副本难度对怪物数值的加成表(monster/monsterapcdifficultybonus.tbl)与应用它的算术。难度五档,下标 0 到 4,
    /// 对应文件里 <c>[easy] [medium] [hard] [ultimate] [hero]</c> 五节的次序(客户端界面只有前四档的字母图,见 Dnf.Play 的难度界面)。
    /// 文件里与本类有关的两块:
    /// <list type="number">
    /// <item>开头没有标签的数:13 组 × 5 个难度的"全局系数"(<see cref="Global"/>);后面还有两块同样没有标签的数(死亡之塔、血系统的加成),这里没有读;</item>
    /// <item><c>[easy]</c> 等五节:每节是一串"等级 + 13 个系数"的行(<see cref="RowCount"/>、<see cref="Row"/>),等级数只是行号标记,取用按行的先后(第 n 行给 n 级怪物)。</item>
    /// </list>
    /// 服务端给怪物加难度的次序(<c>CBattle_Field::set_monster_ability</c>):按等级算基础数值 → 按 [ability category] 修正 → <b>难度加成</b> → 首领或精英特性。
    /// 难度加成只动下面这些槽(组号是全局块里的行号;"乘"是取整后的新值 = 截断(系数 × 旧值),"加"是旧值 + 截断(系数)):
    /// <list type="bullet">
    /// <item>组 4:生命值基数,乘;</item>
    /// <item>组 3:硬直抗性,加;组 10:受击恢复,加;</item>
    /// <item>组 7:异常状态抗性,加(17 项里跳过第 12、13 项——第 13 项就是硬直抗性本身,见 <see cref="StatusSlot"/>);</item>
    /// <item>组 5:攻击速度,乘;组 6:移动速度,旧值不为 0 时加;</item>
    /// <item>组 8:装备物理攻击与装备魔法攻击,乘;组 9:装备物理防御与装备魔法防御,乘。</item>
    /// </list>
    /// 三种取系数的办法:
    /// <list type="number">
    /// <item><b>副本自带加成</b>(.dgn 有 <see cref="DungeonDifficultyBonus"/>):系数 = 副本的 × 全局的;</item>
    /// <item><b>按等级的行</b>(默认):这个难度这一节的行数不少于怪物等级时,系数 = 第"等级 − 1"行的 × 全局的;</item>
    /// <item><b>只有全局</b>:行数不够(或等级小于 1)时只用全局系数。</item>
    /// </list>
    /// 取整的细节(x87 的中间结果留在 80 位寄存器里,只有存进 float 临时变量才舍入到 24 位):"乘"在全局算法里是 截断(旧值 × 全局),
    /// 在带系数的两种算法里是 截断(行系数 × float(旧值 × 全局));"加"在全局算法里是 截断(全局),带系数时是 截断(行系数 × 全局)。
    /// </summary>
    public sealed class MonsterDifficultyTable
    {
        public const string Path = "monster/monsterapcdifficultybonus.tbl";

        /// <summary>难度档数。</summary>
        public const int Difficulties = 5;

        /// <summary>全局块的组数,也是每个等级行里的系数个数。</summary>
        public const int Groups = 13;

        public const int GroupStuck = 3;
        public const int GroupHp = 4;
        public const int GroupAttackSpeed = 5;
        public const int GroupMoveSpeed = 6;
        public const int GroupStatus = 7;
        public const int GroupEquipAttack = 8;
        public const int GroupEquipDefense = 9;
        public const int GroupHitRecovery = 10;

        /// <summary>异常状态抗性的项数(从 <see cref="MonsterStats.IxStatusResistance"/> 起连续存放)。</summary>
        public const int StatusCount = 0x11;

        private static readonly string[] SectionTags = { "[easy]", "[medium]", "[hard]", "[ultimate]", "[hero]" };

        private readonly float[] _global;
        private readonly List<float[]>[] _rows;

        /// <summary>什么也没有:<see cref="Apply"/> 什么也不改(服务端没有这张表就起不来,这里是移植者的退路)。</summary>
        public static MonsterDifficultyTable Empty => new MonsterDifficultyTable(null, null);

        /// <param name="global">65 个数(按组、再按难度),或 null 表示没有表。</param>
        /// <param name="rows">五个难度各一串行(每行 13 个系数);null 表示没有行。</param>
        public MonsterDifficultyTable(float[] global, List<float[]>[] rows)
        {
            if (global != null && global.Length != Groups * Difficulties)
            {
                throw new ArgumentException("全局块必须是 13 组 × 5 个难度", nameof(global));
            }
            _global = global == null ? null : (float[])global.Clone();
            _rows = new List<float[]>[Difficulties];
            for (int d = 0; d < Difficulties; d++)
            {
                _rows[d] = new List<float[]>();
                if (rows != null && d < rows.Length && rows[d] != null)
                {
                    foreach (float[] row in rows[d])
                    {
                        if (row == null || row.Length != Groups)
                        {
                            throw new ArgumentException("每个等级行必须是 13 个系数", nameof(rows));
                        }
                        _rows[d].Add((float[])row.Clone());
                    }
                }
            }
        }

        /// <summary>有全局块才算载入了表。</summary>
        public bool IsLoaded => _global != null;

        /// <summary>全局块第 <paramref name="group"/> 组、难度 <paramref name="difficulty"/> 的系数;没有表返回 0。</summary>
        public float Global(int group, int difficulty)
        {
            return _global == null ? 0f : _global[group * Difficulties + difficulty];
        }

        /// <summary>难度 <paramref name="difficulty"/> 那一节的行数。</summary>
        public int RowCount(int difficulty)
        {
            return (uint)difficulty < (uint)Difficulties ? _rows[difficulty].Count : 0;
        }

        /// <summary>难度 <paramref name="difficulty"/> 那一节里给 <paramref name="level"/> 级怪物用的行(第 level − 1 行);没有返回 null。</summary>
        public float[] Row(int difficulty, int level)
        {
            if ((uint)difficulty >= (uint)Difficulties || level < 1 || level > _rows[difficulty].Count)
            {
                return null;
            }
            return _rows[difficulty][level - 1];
        }

        /// <summary>
        /// 第 <paramref name="i"/> 项异常状态抗性(0 到 16)要不要加难度的"异常状态抗性"系数:服务端把项号映射一下,映射不到(第 12、13 项)就跳过。
        /// 返回映射后的号(用来对照原版,没有别的用处);跳过返回 -1。
        /// </summary>
        public static int StatusSlot(int i)
        {
            switch (i)
            {
                case 12:
                case 13:
                    return -1;
                case 14:
                    return 15;
                case 15:
                    return 16;
                case 16:
                    return 17;
                default:
                    return (uint)i < 12u ? i : -1;
            }
        }

        /// <summary>读 .tbl:开头 65 个数是全局块;五个难度节各读成行(每 14 个数一行:等级号丢掉,后 13 个是系数;末尾不满一行的丢掉)。缺文件或开头数不够是 <see cref="Empty"/>。</summary>
        public static MonsterDifficultyTable Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return Empty;
            }
            float[] global = null;
            var rows = new List<float[]>[Difficulties];
            for (int d = 0; d < Difficulties; d++)
            {
                rows[d] = new List<float[]>();
            }
            bool sawHead = false;
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    if (sawHead)
                    {
                        continue;
                    }
                    sawHead = true;
                    List<float> head = Numbers(line.Values);
                    if (head.Count >= Groups * Difficulties)
                    {
                        global = head.GetRange(0, Groups * Difficulties).ToArray();
                    }
                    continue;
                }
                for (int d = 0; d < Difficulties; d++)
                {
                    if (!line.TagIs(SectionTags[d]))
                    {
                        continue;
                    }
                    List<float> numbers = Numbers(line.Values);
                    const int rowLength = 1 + Groups;
                    for (int start = 0; start + rowLength <= numbers.Count; start += rowLength)
                    {
                        rows[d].Add(numbers.GetRange(start + 1, Groups).ToArray());
                    }
                }
            }
            return global == null ? Empty : new MonsterDifficultyTable(global, rows);
        }

        /// <summary>一行里所有数值(整数按 float 转,浮点数照原样),按出现顺序;其他种类的值跳过。(服务端的 ScanFloat 对整数与浮点数都认。)</summary>
        internal static List<float> Numbers(ScriptValue[] values)
        {
            var list = new List<float>(values.Length);
            foreach (ScriptValue v in values)
            {
                if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
                else if (v.Kind == ScriptValueKind.Float)
                {
                    list.Add(v.Float);
                }
            }
            return list;
        }

        /// <summary>
        /// 给怪物数值块加上难度加成(接在 [ability category] 修正之后)。<paramref name="level"/> 是怪物等级(地图里这只怪物的等级);
        /// <paramref name="dungeon"/> 为 null 走"按等级的行"(行数不够退到只用全局系数)。没载入表或难度不在 0 到 4 时什么也不改
        /// (服务端对越界难度是读到表外的垃圾,这里不模仿)。
        /// </summary>
        public void Apply(MonsterStats stats, int difficulty, int level, DungeonDifficultyBonus dungeon = null)
        {
            if (stats == null)
            {
                throw new ArgumentNullException(nameof(stats));
            }
            if (!IsLoaded || (uint)difficulty >= (uint)Difficulties)
            {
                return;
            }
            float[] scale = null;
            if (dungeon != null)
            {
                scale = new float[Groups];
                for (int k = 0; k < Groups; k++)
                {
                    scale[k] = dungeon.Value(k, difficulty);
                }
            }
            else
            {
                scale = Row(difficulty, level);
            }

            int[] r = stats.Raw;
            r[MonsterStats.IxHpBase] = Multiply(r[MonsterStats.IxHpBase], GroupHp, difficulty, scale);
            r[MonsterStats.IxStuckResistance] = unchecked(r[MonsterStats.IxStuckResistance] + Add(GroupStuck, difficulty, scale));
            r[MonsterStats.IxHitRecovery] = unchecked(r[MonsterStats.IxHitRecovery] + Add(GroupHitRecovery, difficulty, scale));
            for (int i = 0; i < StatusCount; i++)
            {
                if (StatusSlot(i) >= 0)
                {
                    r[MonsterStats.IxStatusResistance + i] = unchecked(r[MonsterStats.IxStatusResistance + i] + Add(GroupStatus, difficulty, scale));
                }
            }
            r[MonsterStats.IxAttackSpeed] = Multiply(r[MonsterStats.IxAttackSpeed], GroupAttackSpeed, difficulty, scale);
            if (r[MonsterStats.IxMoveSpeed] != 0)
            {
                r[MonsterStats.IxMoveSpeed] = unchecked(r[MonsterStats.IxMoveSpeed] + Add(GroupMoveSpeed, difficulty, scale));
            }
            r[MonsterStats.IxEquipPhysicalAttack] = Multiply(r[MonsterStats.IxEquipPhysicalAttack], GroupEquipAttack, difficulty, scale);
            r[MonsterStats.IxEquipMagicalAttack] = Multiply(r[MonsterStats.IxEquipMagicalAttack], GroupEquipAttack, difficulty, scale);
            r[MonsterStats.IxEquipPhysicalDefense] = Multiply(r[MonsterStats.IxEquipPhysicalDefense], GroupEquipDefense, difficulty, scale);
            r[MonsterStats.IxEquipMagicalDefense] = Multiply(r[MonsterStats.IxEquipMagicalDefense], GroupEquipDefense, difficulty, scale);
        }

        /// <summary>"乘":见类说明里取整的细节。<paramref name="scale"/> 为 null 是只用全局系数。</summary>
        private int Multiply(int value, int group, int difficulty, float[] scale)
        {
            float global = _global[group * Difficulties + difficulty];
            X87Value product = X87.Mul(X87.FromInt(value), X87.FromSingle(global));
            if (scale == null)
            {
                return X87.TruncateToInt(product);
            }
            // 乘积先存进 float 临时变量(舍入到 24 位),再乘行系数
            float stored = X87.ToSingle(product);
            return X87.TruncateToInt(X87.Mul(X87.FromSingle(scale[group]), X87.FromSingle(stored)));
        }

        /// <summary>"加"要加上的整数:见类说明里取整的细节。</summary>
        private int Add(int group, int difficulty, float[] scale)
        {
            float global = _global[group * Difficulties + difficulty];
            if (scale == null)
            {
                return X87.TruncateToInt(X87.FromSingle(global));
            }
            return X87.TruncateToInt(X87.Mul(X87.FromSingle(scale[group]), X87.FromSingle(global)));
        }
    }
}
