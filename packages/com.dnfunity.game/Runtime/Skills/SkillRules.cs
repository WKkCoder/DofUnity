using System;

// 证据:E-127(学技能的规则,服务端静态读码:技能槽的"学新技能"、技能的"能不能学到这一级"、技能表的花费补齐与界面分页)。
namespace Dnf.Game
{
    /// <summary>学技能、退技能的结果(对应服务端的几个失败码,拆得更细一点)。</summary>
    public enum SkillOpResult
    {
        /// <summary>成功。</summary>
        Ok = 0,

        /// <summary>没有这个技能(技能表里找不到)。</summary>
        Unknown,

        /// <summary>等级不够、这个成长类型学不了、已到最高级,或前置技能没学够。</summary>
        NotAllowed,

        /// <summary>技能点(特殊技能是觉醒点)不够。</summary>
        NotEnoughPoints,

        /// <summary>没有花费表,或这一级超出了花费表。</summary>
        InvalidCost,

        /// <summary>退技能:这个技能还没学。</summary>
        NotLearned,

        /// <summary>退技能:不能低于开局或转职时给的等级。</summary>
        BelowGiven,

        /// <summary>退技能:已经学了的别的技能要求它至少这么多级。</summary>
        NeededByOther,
    }

    /// <summary>
    /// 学技能的纯规则(证据 E-127;不含状态,状态在 <see cref="SkillBook"/>)。全部取自 .skl 脚本里的标签,公式是服务端的写法,用自己的话与命名。
    /// </summary>
    public static class SkillRules
    {
        /// <summary>成长类型的个数([growtype maximum level] 的项数;下标 0 是转职前)。</summary>
        public const int GrowtypeCount = 6;

        /// <summary>界面分页的页数(不含服务端用的"无效"值)。</summary>
        public const int UiGroupCount = 4;

        /// <summary>觉醒(第二成长类型)的个数:1 = 一觉、2 = 二觉(0 = 没觉醒)。</summary>
        public const int AwakeningCount = CharacterGrowth.AwakeningCount;

        /// <summary>某个成长类型能学到的最高级;越界、没有标签为 0(这个成长类型学不了)。</summary>
        public static int TypeMaxLevel(SkillDef def, int growtype)
        {
            if (def == null || growtype < 0 || growtype >= def.GrowtypeMaxLevels.Length)
            {
                return 0;
            }
            return def.GrowtypeMaxLevels[growtype];
        }

        /// <summary>
        /// 成长类型 <paramref name="growtype"/> 觉醒到 <paramref name="awakening"/> 以后,这个技能自己写的最高级([second growtype maximum level] 的第
        /// 成长类型 × 2 + 觉醒 − 1 项,服务端 get_second_growtype_max_level 0x08350658);0 = 没写,越界或没有标签也是 0。
        /// </summary>
        public static int SecondMaxLevel(SkillDef def, int growtype, int awakening)
        {
            if (def == null || growtype < 0 || growtype >= GrowtypeCount || awakening < 1 || awakening > AwakeningCount)
            {
                return 0;
            }
            int index = growtype * AwakeningCount + (awakening - 1);
            return index < def.SecondGrowtypeMaxLevels.Length ? def.SecondGrowtypeMaxLevels[index] : 0;
        }

        /// <summary>
        /// 觉醒后能学到的最高级(服务端 can_learn 与 getMaxSkillLevel 的取法):没觉醒(0)或越界看成长类型本身的最高级;觉醒了就取技能自己为这次觉醒写的最高级,
        /// 它是 0(没写)时回到成长类型本身的最高级。所以觉醒可以让一个技能学得更高、更低,或者(成长类型本身学不了的)才开始学得了。
        /// </summary>
        public static int TypeMaxLevel(SkillDef def, int growtype, int awakening)
        {
            if (awakening >= 1 && awakening <= AwakeningCount)
            {
                int second = SecondMaxLevel(def, growtype, awakening);
                if (second != 0)
                {
                    return second;
                }
            }
            return TypeMaxLevel(def, growtype);
        }

        /// <summary>学到第 <paramref name="level"/> 级需要的主角等级:[required level] + [required level range] × (级数 − 1)。</summary>
        public static int RequiredHeroLevel(SkillDef def, int level)
        {
            if (def == null)
            {
                return int.MaxValue;
            }
            return def.RequiredLevel + def.RequiredLevelRange * (level - 1);
        }

        /// <summary>
        /// 主角等级 <paramref name="heroLevel"/>、成长类型 <paramref name="growtype"/> 能不能学到 <paramref name="target"/> 级(服务端 can_learn):
        /// 目标在 1 到该成长类型的最高级之内,目标不超过"主角等级 − 要求等级 + 1"与最高级里较小的一个,并且第 target 级的要求等级不高于主角等级。
        /// </summary>
        public static bool CanLearn(SkillDef def, int heroLevel, int growtype, int target)
        {
            return CanLearn(def, heroLevel, growtype, 0, target);
        }

        /// <summary>同 <see cref="CanLearn(SkillDef, int, int, int)"/>,最高级按觉醒后的算(<see cref="TypeMaxLevel(SkillDef, int, int)"/>)。</summary>
        public static bool CanLearn(SkillDef def, int heroLevel, int growtype, int awakening, int target)
        {
            if (def == null)
            {
                return false;
            }
            int max = TypeMaxLevel(def, growtype, awakening);
            if (target < 1 || target > max)
            {
                return false;
            }
            int allowed = Math.Min(max, heroLevel - def.RequiredLevel + 1);
            if (allowed < target)
            {
                return false;
            }
            return RequiredHeroLevel(def, target) <= heroLevel;
        }

        /// <summary>
        /// 此刻最多能学到几级(服务端 getMaxSkillLevel):成长类型的最高级、"主角等级 − 要求等级 + 1"、"(主角等级 − 要求等级) ÷ 等级间隔 + 1"三者最小,
        /// 小于 0 取 0;这个成长类型学不了(最高级为 0)返回 -1。要求等级间隔为 0 的(只有一级的)技能不受第三项限制。
        /// </summary>
        public static int MaxLearnable(SkillDef def, int heroLevel, int growtype)
        {
            return MaxLearnable(def, heroLevel, growtype, 0);
        }

        /// <summary>同 <see cref="MaxLearnable(SkillDef, int, int)"/>,最高级按觉醒后的算(服务端 getMaxSkillLevel 的第二成长类型实参)。</summary>
        public static int MaxLearnable(SkillDef def, int heroLevel, int growtype, int awakening)
        {
            int max = TypeMaxLevel(def, growtype, awakening);
            if (max <= 0)
            {
                return -1;
            }
            int a = Math.Min(max, heroLevel - def.RequiredLevel + 1);
            int result = a;
            if (def.RequiredLevelRange > 0)
            {
                int b = (heroLevel - def.RequiredLevel) / def.RequiredLevelRange + 1;
                if (b < result)
                {
                    result = b;
                }
            }
            return result < 0 ? 0 : result;
        }

        /// <summary>
        /// 升到第 <paramref name="level"/> 级要花的点数:花费表([purchase cost],特殊技能用 [special purchase cost])的第 level 项;
        /// 服务端载入时把这张表用最后一个数补到有效最高级(<see cref="SkillDef.EffectiveMaxLevel"/>)那么长,所以只写一个数的技能每级同价。
        /// 没有花费表、级数小于 1 或超出补齐后的长度返回 -1。
        /// </summary>
        public static int CostOfLevel(SkillDef def, int level)
        {
            if (def == null || level < 1)
            {
                return -1;
            }
            int[] table = def.IsSpecial ? def.SpecialPurchaseCost : def.PurchaseCost;
            if (table.Length == 0)
            {
                return -1;
            }
            int padded = Math.Max(table.Length, def.EffectiveMaxLevel);
            if (level > padded)
            {
                return -1;
            }
            return level <= table.Length ? table[level - 1] : table[table.Length - 1];
        }

        /// <summary>
        /// 从 <paramref name="from"/> 级升到 <paramref name="to"/> 级一共要花的点数(第 from+1 到 to 级的花费之和)。
        /// 任何一级没有花费返回 -1;<paramref name="to"/> 不高于 <paramref name="from"/> 返回 0(服务端这种情况返回的是第 to 级的单价,没有调用方用到)。
        /// </summary>
        public static int NeedPoints(SkillDef def, int from, int to)
        {
            if (def == null || from < 0 || to < 1)
            {
                return -1;
            }
            if (to <= from)
            {
                return 0;
            }
            long sum = 0;
            for (int level = from + 1; level <= to; level++)
            {
                int cost = CostOfLevel(def, level);
                if (cost < 0)
                {
                    return -1;
                }
                sum += cost;
            }
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>
        /// 升到 <paramref name="level"/> 级为止累计应得的技能点(服务端重置技能时的总额:从第 1 级到本级每级给的点数之和,<see cref="SpTable.AtLevelUp"/>);
        /// 任务、道具另给的点数这里没有。
        /// </summary>
        public static int TotalSp(SpTable table, int level)
        {
            if (table == null)
            {
                return 0;
            }
            long sum = 0;
            for (int l = 1; l <= level; l++)
            {
                sum += table.AtLevelUp(l);
            }
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>升到 <paramref name="level"/> 级为止累计应得的特殊点(觉醒点):50 级起每级一点,即 级数 − 49,不到 50 级为 0。</summary>
        public static int TotalSfp(int level)
        {
            return level >= Leveling.AwakeningFirstLevel ? level - Leveling.AwakeningFirstLevel + 1 : 0;
        }

        /// <summary>
        /// 界面分页(服务端 reform_ui_group_no):特殊技能一律第 3 页;其余按 [skill class]:0 到 3 看"适合的成长类型"个数,少于 3 个是第 1 页(各转职方向自己的技能)、
        /// 否则第 0 页(共用),4 是第 2 页(基础与被动),别的值原样不动。
        /// </summary>
        public static int UiGroup(SkillDef def)
        {
            if (def == null)
            {
                return 0;
            }
            if (def.IsSpecial)
            {
                return 3;
            }
            int cls = def.SkillClass;
            if (cls >= 0 && cls < 4)
            {
                return def.FitGrowtypes.Length < 3 ? 1 : 0;
            }
            return cls == 4 ? 2 : cls;
        }
    }
}
