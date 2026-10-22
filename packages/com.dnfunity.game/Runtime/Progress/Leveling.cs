using System;

// 证据:E-111(服务端 CUser::gain_exp_sp 与 CUser::_check_level_up 的逐行读法;封顶等级与技能点来源见台账)。
namespace Dnf.Game
{
    /// <summary>一次加经验的结果。</summary>
    public readonly struct ExpGainResult
    {
        /// <summary>加完以后的等级。</summary>
        public int Level { get; }

        /// <summary>加完以后的累计经验(升级时被设成新等级的累计经验门槛,多出来的经验继续滚进下一级;封顶时多余的丢掉)。</summary>
        public int Exp { get; }

        /// <summary>这次涨了几级。</summary>
        public int LevelsGained { get; }

        /// <summary>这次涨级给的技能点(每级一份,取自 <see cref="SpTable"/>)。</summary>
        public int SkillPoints { get; }

        /// <summary>这次涨级给的觉醒点(新等级 ≥ 50 的每一级给 1 点;服务端记为 sfp)。</summary>
        public int AwakeningPoints { get; }

        public bool LeveledUp => LevelsGained > 0;

        public ExpGainResult(int level, int exp, int levelsGained, int skillPoints, int awakeningPoints)
        {
            Level = level;
            Exp = exp;
            LevelsGained = levelsGained;
            SkillPoints = skillPoints;
            AwakeningPoints = awakeningPoints;
        }
    }

    /// <summary>
    /// 加经验与升级(证据 E-111):<c>CUser::gain_exp_sp</c> 只在等级低于 <see cref="MaxLevel"/> 时才调升级检查,
    /// <c>CUser::_check_level_up</c> 是下面这个循环——每一轮取本级门槛 bottom 与下一级门槛 top:
    /// 经验落在 [bottom, top] 之外就先校正成 bottom;top 与 bottom 相等或倒挂(表走到头了)就停;
    /// 经验加上待加的量还不到 top 就全加进去、结束;够了就升一级:给技能点(新等级的 <see cref="SpTable"/> 项),
    /// 新等级 ≥ 50 再给 1 个觉醒点,待加的量减去"补到 top 所需的经验",经验设成 top,进入下一轮。
    /// 下一级超过 <see cref="MaxLevel"/> 时循环结束(封顶那一级的多余经验丢掉)。
    /// 没做的部分(服务端在这里顺手做的、与单机无关的事):公会与监控同步、任务与成就、商城加成金币、"服务器环境 2"时 49 级以上不涨经验的限制。
    /// </summary>
    public static class Leveling
    {
        /// <summary>经验能把等级涨到的上限(0x55):下一级超过它就不再涨。</summary>
        public const int MaxLevel = 85;

        /// <summary>新等级达到它(含)就多给一个觉醒点(服务端判断 "0x31 &lt; 新等级")。</summary>
        public const int AwakeningFirstLevel = 50;

        /// <summary>
        /// 给等级 <paramref name="level"/>、累计经验 <paramref name="exp"/> 的角色加 <paramref name="gained"/> 点经验。
        /// 等级已经封顶(≥ <see cref="MaxLevel"/>)时什么都不变(经验也不累计);<paramref name="gained"/> 小于 0 当 0。
        /// </summary>
        public static ExpGainResult Gain(ExpTable table, SpTable sp, int level, int exp, int gained)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (gained < 0)
            {
                gained = 0;
            }
            int skillPoints = 0;
            int awakeningPoints = 0;
            int levels = 0;
            if (level >= MaxLevel)
            {
                return new ExpGainResult(level, exp, 0, 0, 0);
            }
            long remaining = gained;
            int next = level + 1;
            while (remaining >= 1 && next <= MaxLevel)
            {
                int bottom = table.LevelExp(level);
                int top = table.LevelExp(next);
                if (exp < bottom || top < exp)
                {
                    exp = bottom;   // "Level 与 Exp 不一致":服务端记日志并把经验拉回本级门槛
                }
                if (top <= bottom)
                {
                    break;   // 表走到头(没有下一级的门槛)或倒挂:不再涨
                }
                if (exp + remaining < top)
                {
                    exp = (int)(exp + remaining);
                    remaining = 0;
                }
                else
                {
                    skillPoints += sp == null ? 0 : sp.AtLevelUp(next);
                    if (next >= AwakeningFirstLevel)
                    {
                        awakeningPoints++;
                    }
                    remaining += exp - top;   // 减去补到 top 所需的 (top - exp)
                    level++;
                    next++;
                    levels++;
                    exp = top;
                }
            }
            return new ExpGainResult(level, exp, levels, skillPoints, awakeningPoints);
        }
    }
}
