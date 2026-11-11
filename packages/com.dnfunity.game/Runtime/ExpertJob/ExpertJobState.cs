using System;
using System.Collections.Generic;

// 服务端静态读码(df_game_r.orig)的副职业角色状态。
//   存档:charac_info.expert_job(种类)、charac_stat.expert_job_exp(经验)、charac_expert_job 表(放弃次数 expert_job_giveup_cnt、
//         机器耐久 expert_job_info、机器档 expert_job_info_ex、配方位图 recipe);内存里是 CCharacExpertJob(+6 放弃次数 16 位、+8 耐久、+0xc 机器档、+0x10 起 30 字节配方位图)。
//   就职:CUser::quest_reward 0x0866ea92(任务奖励种类 20)与 GM 命令 0x084a9900 → SetCurCharacExpertJobType + 授予 [skill] 技能 + CCharacExpertJob::InitScript 0x0832b36c + 经验清零。
//   放弃:CExpertJobMgr::OnGiveup 0x0849e60c;重置:CCharacExpertJob::_reset 0x0832b064;配方:set_recipe 0x0832b264。
namespace Dnf.Game
{
    /// <summary>就职、放弃这类带返回的操作的结果。</summary>
    public sealed class ExpertJobChange
    {
        internal ExpertJobChange(ExpertJobError error, IReadOnlyList<ExpertSkillGrant> grants, IReadOnlyList<int> skillsToRemove, IReadOnlyList<int> questsToReset, int goldCost)
        {
            Error = error;
            SkillsToGrant = grants ?? new ExpertSkillGrant[0];
            SkillsToRemove = skillsToRemove ?? new int[0];
            QuestsToReset = questsToReset ?? new int[0];
            GoldCost = goldCost;
        }

        /// <summary>没有任何副作用的失败结果。</summary>
        public static ExpertJobChange Failed(ExpertJobError error)
        {
            return new ExpertJobChange(error, null, null, null, 0);
        }

        public ExpertJobError Error { get; }

        public bool Ok => Error == ExpertJobError.Ok;

        /// <summary>就职:要授予的技能(技能号 + 初始等级);宿主对每一项授予普通与觉醒两种技能树(种类 0 与 1)。</summary>
        public IReadOnlyList<ExpertSkillGrant> SkillsToGrant { get; }

        /// <summary>放弃:要删除的技能号(同样两种技能树)。</summary>
        public IReadOnlyList<int> SkillsToRemove { get; }

        /// <summary>放弃:要重置为"未完成"的任务号(<c>[clear quests]</c>;任务簿归宿主管,这一线不改)。</summary>
        public IReadOnlyList<int> QuestsToReset { get; }

        /// <summary>放弃:扣掉的金币。</summary>
        public int GoldCost { get; }
    }

    /// <summary>
    /// 一个角色的副职业状态:种类、经验、放弃次数、机器耐久与机器档、配方位图。
    /// 只管状态与状态之间的转移;金币、背包、任务簿归宿主(<c>Dnf.Play</c> 的 <c>ExpertJobSession</c> 串起来)。
    /// </summary>
    public sealed class ExpertJobState
    {
        /// <summary>配方位图的字节数(服务端 30 字节,所以配方序号 0 到 239)。</summary>
        public const int RecipeBytes = 30;

        /// <summary>配方序号的个数。</summary>
        public const int RecipeCount = RecipeBytes * 8;

        private readonly byte[] _recipes = new byte[RecipeBytes];

        public ExpertJobKind Kind { get; private set; }

        /// <summary>经验总值;放弃后是 −1(服务端写的放弃标记),再就职时清零。</summary>
        public int Exp { get; private set; }

        /// <summary>放弃次数(服务端 16 位)。</summary>
        public int GiveupCount { get; private set; }

        /// <summary>分解机的耐久。</summary>
        public int MachineEndurance { get; private set; }

        /// <summary>分解机的档位(0 起)。</summary>
        public int MachineGrade { get; private set; }

        public bool HasJob => Kind != ExpertJobKind.None;

        /// <summary>空状态(没有副职业)。注意:新角色的 <see cref="Exp"/> 为 0(不是放弃后的 −1)。</summary>
        public ExpertJobState()
        {
            Kind = ExpertJobKind.None;
        }

        public bool HasRecipe(int index)
        {
            if (index < 0)
            {
                return false;
            }
            int byteIndex = index / 8;
            return byteIndex < RecipeBytes && (_recipes[byteIndex] & (1 << (index % 8))) != 0;
        }

        /// <summary>
        /// 记下学会了某个配方(<c>CCharacExpertJob::set_recipe</c> 0x0832b264):序号越界或已经学过返回 false。
        /// 负序号一律返回 false(服务端对负序号的位运算是未定义的写法,不照搬)。哪些配方能学、怎么学归制作线。
        /// </summary>
        public bool TrySetRecipe(int index)
        {
            if (index < 0)
            {
                return false;
            }
            int byteIndex = index / 8;
            if (byteIndex >= RecipeBytes)
            {
                return false;
            }
            int bit = 1 << (index % 8);
            if ((_recipes[byteIndex] & bit) != 0)
            {
                return false;
            }
            _recipes[byteIndex] = (byte)(_recipes[byteIndex] | bit);
            return true;
        }

        /// <summary>已学配方的序号,从小到大。</summary>
        public List<int> LearnedRecipes()
        {
            var list = new List<int>();
            for (int i = 0; i < RecipeCount; i++)
            {
                if (HasRecipe(i))
                {
                    list.Add(i);
                }
            }
            return list;
        }

        /// <summary>配方位图的拷贝(30 字节)。</summary>
        public byte[] RecipeBitmap() => (byte[])_recipes.Clone();

        /// <summary>
        /// 就职(<c>quest_reward</c> 的 <c>[expert job]</c>、GM 命令):设种类、授予 [skill] 的技能、<c>InitScript</c>(耐久设成 <c>[endurance initial value]</c>、机器档 0、配方清空;放弃次数不动)、经验清零。
        /// 已经有副职业时拒绝(<see cref="ExpertJobError.NotAvailable"/>;服务端靠任务的 <c>[collision quest]</c> 挡,这里在状态层挡一次)。
        /// </summary>
        public ExpertJobChange Acquire(ExpertJobKind kind, ExpertJobScript script)
        {
            if (!ExpertJobKinds.IsJob(kind) || script == null || script.Kind != kind)
            {
                return new ExpertJobChange(ExpertJobError.NotAvailable, null, null, null, 0);
            }
            if (HasJob)
            {
                return new ExpertJobChange(ExpertJobError.NotAvailable, null, null, null, 0);
            }
            Kind = kind;
            InitScript(script);
            Exp = 0;
            var grants = new List<ExpertSkillGrant>(script.Skills);
            return new ExpertJobChange(ExpertJobError.Ok, grants, null, null, 0);
        }

        /// <summary><c>CCharacExpertJob::InitScript</c>:耐久设成脚本初值、机器档 0、配方清空;放弃次数不动。</summary>
        public void InitScript(ExpertJobScript script)
        {
            MachineEndurance = script != null ? script.EnduranceInitial : 0;
            MachineGrade = 0;
            Array.Clear(_recipes, 0, _recipes.Length);
        }

        /// <summary>
        /// 放弃的状态检查(<c>OnGiveup</c> 0x0849e60c,按服务端次序):没有副职业 → 8;脚本没有 → 8;放弃次数已到费用表长 → 1;
        /// 金币不够 → 0x15;<c>[clear quests]</c> 里有没完成的任务 → 8。通过时返回要扣的金币、要删的技能与要重置的任务,<b>不改状态</b>;
        /// 宿主扣金币、删技能、重置任务后调用 <see cref="ApplyGiveUp"/>。
        /// </summary>
        public ExpertJobChange PlanGiveUp(ExpertJobScript script, long gold, Func<int, bool> questCleared)
        {
            if (!HasJob || script == null)
            {
                return new ExpertJobChange(ExpertJobError.NotAvailable, null, null, null, 0);
            }
            if (GiveupCount >= script.GiveupCosts.Count)
            {
                return new ExpertJobChange(ExpertJobError.LimitReached, null, null, null, 0);
            }
            int cost = script.GiveupCosts[GiveupCount];
            if (gold < cost)
            {
                return new ExpertJobChange(ExpertJobError.NotEnoughGold, null, null, null, 0);
            }
            foreach (int quest in script.ClearQuests)
            {
                if (questCleared == null || !questCleared(quest))
                {
                    return new ExpertJobChange(ExpertJobError.NotAvailable, null, null, null, 0);
                }
            }
            var skills = new List<int>(script.Skills.Count);
            foreach (ExpertSkillGrant s in script.Skills)
            {
                skills.Add(s.SkillId);
            }
            return new ExpertJobChange(ExpertJobError.Ok, null, skills, new List<int>(script.ClearQuests), cost);
        }

        /// <summary>
        /// 放弃的状态转移:种类清空、经验写 −1、放弃次数加 1 但不超过"费用表长 − 1"、机器档与耐久与配方清空。
        /// 调用前应已用 <see cref="PlanGiveUp"/> 检查通过。
        /// </summary>
        public void ApplyGiveUp(ExpertJobScript script)
        {
            int size = script != null ? script.GiveupCosts.Count : 0;
            int newCount = GiveupCount + 1;
            if (size <= newCount)
            {
                newCount = size - 1;
            }
            Kind = ExpertJobKind.None;
            Exp = -1;
            MachineEndurance = 0;
            MachineGrade = 0;
            Array.Clear(_recipes, 0, _recipes.Length);
            GiveupCount = newCount < 0 ? 0 : newCount;
        }

        /// <summary>写入经验总值(经验规则在 <see cref="ExpertJobLevels"/>,这里只存)。</summary>
        public void SetExp(int exp)
        {
            Exp = exp;
        }

        /// <summary>写入机器档与耐久(升档、整修、分解后调用;规则在 <see cref="DisjointMachine"/>)。</summary>
        public void SetMachine(int grade, int endurance)
        {
            MachineGrade = grade;
            MachineEndurance = endurance;
        }

        public ExpertJobState Clone()
        {
            var copy = new ExpertJobState
            {
                Kind = Kind,
                Exp = Exp,
                GiveupCount = GiveupCount,
                MachineEndurance = MachineEndurance,
                MachineGrade = MachineGrade,
            };
            Array.Copy(_recipes, copy._recipes, RecipeBytes);
            return copy;
        }

        /// <summary>
        /// 从存档的字段还原(不检查脚本;种类不认得的当没有副职业,放弃次数夹到 0 到 65535,配方位图长度不对就当空)。
        /// 返回 false 表示有字段被改成了合法值(用于存档解析报告)。
        /// </summary>
        public bool Load(int kind, int exp, int giveupCount, int endurance, int grade, byte[] recipes)
        {
            bool clean = true;
            Kind = ExpertJobKinds.IsJob((ExpertJobKind)kind) ? (ExpertJobKind)kind : ExpertJobKind.None;
            if (kind != 0 && Kind == ExpertJobKind.None)
            {
                clean = false;
            }
            Exp = exp;
            if (giveupCount < 0 || giveupCount > ushort.MaxValue)
            {
                giveupCount = giveupCount < 0 ? 0 : ushort.MaxValue;
                clean = false;
            }
            GiveupCount = giveupCount;
            MachineEndurance = endurance;
            MachineGrade = grade < 0 ? 0 : grade;
            if (grade < 0)
            {
                clean = false;
            }
            Array.Clear(_recipes, 0, _recipes.Length);
            if (recipes != null)
            {
                if (recipes.Length == RecipeBytes)
                {
                    Array.Copy(recipes, _recipes, RecipeBytes);
                }
                else
                {
                    clean = false;
                }
            }
            return clean;
        }
    }
}
