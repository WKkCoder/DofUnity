using System;
using System.Collections.Generic;

// 服务端静态读码(df_game_r.orig)的副职业等级与经验:
//   CUser::GetCurExpertJobLevel 0x0868bc7c(带角色等级闸门的当前等级)、STExpertJobScript::GetLevel 0x08693ba0、isBoundaryExpValue 0x0849f420、
//   STExpertJobEtcScript::isExpertJobLimitLevel 0x0849f47c、CExpertJob::IncreaseExpertJobExp 0x0849ae40(参数是新的经验总值,不是增量)。
// 观察:
//   · 经验阈值是各级的"封顶点":经验停在阈值上,要等角色等级够 [expertjob level limit] 才越过去。
//   · 升级判定里有一个写死的 11("等级不等于 11"),不是读表长;四个 .exj 的表长恰好都是 11。
//   · 经验不变小:同一级内经验照常增长,新经验落在边界上而角色等级又恰好是某个上限值时,即使等级没变也算"升级"(会重新授予技能、不封顶)。这是服务端的写法,照搬。
namespace Dnf.Game
{
    /// <summary>一次增加经验的结果。</summary>
    public sealed class ExpertExpResult
    {
        private static readonly IReadOnlyList<ExpertSkillGrant> NoGrants = new ExpertSkillGrant[0];

        internal ExpertExpResult(bool changed, int newExp, bool leveledUp, int level, IReadOnlyList<ExpertSkillGrant> growth)
        {
            Changed = changed;
            NewExp = newExp;
            LeveledUp = leveledUp;
            Level = level;
            SkillGrowth = growth ?? NoGrants;
        }

        /// <summary>经验有没有写进去(服务端没有改动就直接返回,不发包)。</summary>
        public bool Changed { get; }

        /// <summary>写入后的经验总值(没改动时是原值)。</summary>
        public int NewExp { get; }

        /// <summary>服务端的"升级"标志:写日志 <c>ExpertJobLv+</c>、广播、重新授予技能、重发技能与任务信息。</summary>
        public bool LeveledUp { get; }

        /// <summary>写入后的当前副职业等级(带闸门)。</summary>
        public int Level { get; }

        /// <summary>升级时要重新授予的技能:每个 [skill] 项的技能号配上<b>当前副职业等级</b>(不是项里的第二个数);宿主对每一项授予普通与觉醒两种技能树(种类 0 与 1)。</summary>
        public IReadOnlyList<ExpertSkillGrant> SkillGrowth { get; }

        /// <summary>什么都没改的结果:经验原样、没有升级、没有技能要重新授予;<paramref name="level"/> 是此刻的副职业等级。</summary>
        public static ExpertExpResult Unchanged(int exp, int level) => new ExpertExpResult(false, exp, false, level, null);
    }

    /// <summary>副职业等级与经验规则。</summary>
    public static class ExpertJobLevels
    {
        /// <summary><c>IncreaseExpertJobExp</c> 里写死的"最高等级"(11),用于"等级相同时只有不是它才可能升级"。</summary>
        public const int LevelLiteral = 11;

        /// <summary>
        /// 当前副职业等级(<c>CUser::GetCurExpertJobLevel</c>):没有副职业为 0;经验不在边界上就是脚本等级;
        /// 恰在边界上时,<c>[expertjob level limit]</c> 里有这一级、且角色等级不低于它才算这一级,否则退一级。脚本为空按没有副职业算。
        /// </summary>
        public static int CurrentLevel(ExpertJobKind kind, ExpertJobScript script, ExpertJobEtc etc, int exp, int charLevel)
        {
            if (kind == ExpertJobKind.None || script == null)
            {
                return 0;
            }
            ExpertJobEtc e = etc ?? ExpertJobEtc.Empty;
            bool boundary = script.IsBoundaryExp(exp);
            int level = script.GetLevel(exp);
            if (boundary)
            {
                if (e.TryGetLevelLimit(level, out int limit) && limit <= charLevel)
                {
                    return level;
                }
                level--;
            }
            return level;
        }

        /// <summary>
        /// 增加经验(<c>CExpertJob::IncreaseExpertJobExp</c>):<paramref name="requestedExp"/> 是希望变成的新经验总值。
        /// 先夹到不超过最后一级的阈值;算改前改后的当前等级;等级相同 → 新经验在边界上、角色等级是某个上限值且改前等级不是 11 才算升级;
        /// 等级不同 → <c>[expertjob level limit]</c> 里有改后那一级且角色等级够才算升级。没升级就把新经验夹到"改前等级对应的阈值"以内;没升级且经验没变就什么都不做。
        /// </summary>
        public static ExpertExpResult Increase(ExpertJobKind kind, ExpertJobScript script, ExpertJobEtc etc, int curExp, int requestedExp, int charLevel)
        {
            int before = CurrentLevel(kind, script, etc, curExp, charLevel);
            if (kind == ExpertJobKind.None || script == null || script.MaxLevel == 0)
            {
                return ExpertExpResult.Unchanged(curExp, before);
            }
            ExpertJobEtc e = etc ?? ExpertJobEtc.Empty;
            int last = script.Levels[script.MaxLevel - 1].Threshold;
            int newExp = requestedExp;
            if (last < newExp)
            {
                newExp = last;
            }
            int after = CurrentLevel(kind, script, e, newExp, charLevel);
            bool leveledUp = false;
            if (before == after)
            {
                if (script.IsBoundaryExp(newExp) && e.IsLimitLevel(charLevel) && before != LevelLiteral)
                {
                    leveledUp = true;
                }
            }
            else if (e.TryGetLevelLimit(after, out int limit) && limit <= charLevel)
            {
                leveledUp = true;
            }
            int index = before - 1;
            if (index >= 0 && index < script.MaxLevel)
            {
                int cap = script.Levels[index].Threshold;
                if (!leveledUp && cap < newExp)
                {
                    newExp = cap;
                }
            }
            if (!leveledUp && curExp == newExp)
            {
                return ExpertExpResult.Unchanged(curExp, before);
            }
            int level = CurrentLevel(kind, script, e, newExp, charLevel);
            List<ExpertSkillGrant> growth = null;
            if (leveledUp)
            {
                growth = new List<ExpertSkillGrant>(script.Skills.Count);
                foreach (ExpertSkillGrant s in script.Skills)
                {
                    growth.Add(new ExpertSkillGrant(s.SkillId, level));
                }
            }
            return new ExpertExpResult(true, newExp, leveledUp, level, growth);
        }
    }
}
