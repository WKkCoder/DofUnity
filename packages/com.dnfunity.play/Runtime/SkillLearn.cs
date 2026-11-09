using System;
using System.Collections.Generic;
using Dnf.Game;

// 宿主层入口(同 ItemUse 的做法):Unity 的宿主程序集不引用 Dnf.Game,够不着 SkillOpResult,所以结果用名字串(枚举名)与基本类型交接。
namespace Dnf.Play
{
    /// <summary>技能学习服务(<see cref="SkillLearning"/>)的宿主入口:结果是 <see cref="SkillOpResult"/> 或 <see cref="SkillReach"/> 的枚举名,批量用并行数组。</summary>
    public static class SkillLearn
    {
        /// <summary>成功的结果名(<see cref="SkillOpResult.Ok"/>)。</summary>
        public const string OkName = nameof(SkillOpResult.Ok);

        /// <summary>学高 <paramref name="levels"/> 级,返回结果名。</summary>
        public static string Learn(SkillLearning learning, int skillId, int levels)
        {
            return Need(learning).Learn(skillId, levels, out _).ToString();
        }

        /// <summary>退掉 <paramref name="levels"/> 级,返回结果名。</summary>
        public static string Forget(SkillLearning learning, int skillId, int levels)
        {
            return Need(learning).Forget(skillId, levels, out _).ToString();
        }

        /// <summary>一批学法(<paramref name="skillIds"/> 与 <paramref name="levels"/> 一一对应):返回结果名;不成时 <paramref name="failedIndex"/> 是第一个不成的步,成了是 -1。长度不一致抛异常。</summary>
        public static string LearnAll(SkillLearning learning, int[] skillIds, int[] levels, out int failedIndex)
        {
            SkillBatchResult result = Need(learning).LearnAll(Steps(skillIds, levels));
            failedIndex = result.FailedIndex;
            return result.ResultName;
        }

        /// <summary>一批退法(参数与结果同 <see cref="LearnAll"/>)。</summary>
        public static string ForgetAll(SkillLearning learning, int[] skillIds, int[] levels, out int failedIndex)
        {
            SkillBatchResult result = Need(learning).ForgetAll(Steps(skillIds, levels));
            failedIndex = result.FailedIndex;
            return result.ResultName;
        }

        /// <summary>这个技能现在能靠什么放出来的名字(<see cref="SkillReach"/> 的枚举名)。</summary>
        public static string Reach(SkillLearning learning, int skillId)
        {
            return Need(learning).ReachOf(skillId).ToString();
        }

        /// <summary>
        /// 日志用的一行状态:技能点与觉醒点余额、学会几个技能、快捷栏放了几格、几个技能只靠指令放、几个只能靠快捷键却没上栏、两个开关;只有数,不带技能名与编号。
        /// </summary>
        public static string Status(SkillLearning learning)
        {
            SkillLearning service = Need(learning);
            SkillBook book = service.Hero.Skills;
            int onBar = 0;
            for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
            {
                if (book.QuickAt(slot) != 0)
                {
                    onBar++;
                }
            }
            int commandOnly = 0;
            foreach (KeyValuePair<int, int> learned in book.Learned)
            {
                if (service.ReachOf(learned.Key) == SkillReach.CommandOnly)
                {
                    commandOnly++;
                }
            }
            return "sp=" + book.Sp + " sfp=" + book.Sfp + " learned=" + book.LearnedCount + " bar=" + onBar + " command=" + commandOnly
                + " stranded=" + service.Stranded() + " aware=" + (service.CommandAware ? 1 : 0) + " auto=" + (service.AutoLearn ? 1 : 0);
        }

        private static SkillLearning Need(SkillLearning learning)
        {
            return learning ?? throw new ArgumentNullException(nameof(learning));
        }

        private static List<SkillStep> Steps(int[] skillIds, int[] levels)
        {
            if (skillIds == null)
            {
                throw new ArgumentNullException(nameof(skillIds));
            }
            if (levels == null)
            {
                throw new ArgumentNullException(nameof(levels));
            }
            if (skillIds.Length != levels.Length)
            {
                throw new ArgumentException("技能编号与级数的个数不一致", nameof(levels));
            }
            var steps = new List<SkillStep>(skillIds.Length);
            for (int i = 0; i < skillIds.Length; i++)
            {
                steps.Add(new SkillStep(skillIds[i], levels[i]));
            }
            return steps;
        }
    }
}
