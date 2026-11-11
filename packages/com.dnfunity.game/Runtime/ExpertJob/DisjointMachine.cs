using System;

// 服务端静态读码(df_game_r.orig)的分解机:耐久、整修、升档、每次分解后的耐久消耗与经验。
//   OnStartMission 0x085d2a16(开始分解前:耐久不大于 0 → 0xbd)、complete_disjoint 0x085d31a0(分解完成:扣耐久、加经验)、
//   OnRepairDisjointMachine 0x085d35ce、OnUpgradeDisjointMachine 0x085d37e8、STExpertJobScript::GetDisjointMachineRepairCost 0x085d3c74 /
//   GetDisjointMachineRepair 0x085d3cc8 / _GetEnduranceRepairCost 0x085d3c26 / GetUpgradeCost 0x085d3d3a / GetDisjointMachineMaxGrade 0x085d3c0e。
// 这一线只做"自己的分解机、自己用":摆摊给别人用(收费、他人付费)不在范围内。
namespace Dnf.Game
{
    /// <summary>整修的计划(<c>OnRepairDisjointMachine</c>):检查通过后宿主扣 <see cref="Cost"/> 金币,耐久设成 <see cref="NewEndurance"/>。</summary>
    public sealed class MachineRepairPlan
    {
        public MachineRepairPlan(ExpertJobError error, int cost, int newEndurance)
        {
            Error = error;
            Cost = cost;
            NewEndurance = newEndurance;
        }

        public ExpertJobError Error { get; }

        public bool Ok => Error == ExpertJobError.Ok;

        public int Cost { get; }

        public int NewEndurance { get; }
    }

    /// <summary>升档的计划(<c>OnUpgradeDisjointMachine</c>)。</summary>
    public sealed class MachineUpgradePlan
    {
        public MachineUpgradePlan(ExpertJobError error, int cost, int newGrade, int newEndurance)
        {
            Error = error;
            Cost = cost;
            NewGrade = newGrade;
            NewEndurance = newEndurance;
        }

        public ExpertJobError Error { get; }

        public bool Ok => Error == ExpertJobError.Ok;

        public int Cost { get; }

        public int NewGrade { get; }

        /// <summary>升档后的耐久:新档那一项的耐久上限(表里没有这一项时保持原样,由宿主取原耐久)。</summary>
        public int NewEndurance { get; }

        /// <summary>升档后是否要改耐久(新档在整修表里有一项)。</summary>
        public bool ResetsEndurance { get; internal set; }
    }

    /// <summary>一次分解完成后的结算(<c>complete_disjoint</c>)。</summary>
    public sealed class DisjointCompletion
    {
        internal DisjointCompletion(int enduranceLost, int newEndurance, int baseGain, bool bonusHit, int totalGain)
        {
            EnduranceLost = enduranceLost;
            NewEndurance = newEndurance;
            BaseGain = baseGain;
            BonusHit = bonusHit;
            TotalGain = totalGain;
        }

        /// <summary>这次扣掉的耐久(不会超过原耐久)。</summary>
        public int EnduranceLost { get; }

        public int NewEndurance { get; }

        /// <summary>分解机本身给的经验(<c>[gain exp]</c> 区间里抽的)。</summary>
        public int BaseGain { get; }

        /// <summary>穿戴的 <c>[prof additional gain exp]</c> 这次是否触发。</summary>
        public bool BonusHit { get; }

        /// <summary>要加的经验总数(为 0 或负时不加)。</summary>
        public int TotalGain { get; }

        /// <summary>耐久用完:服务端此时关闭摆摊任务;自己用的话下次分解会被 0xbd 挡住。</summary>
        public bool MachineBroken => NewEndurance < 1;
    }

    public static class DisjointMachine
    {
        /// <summary>开始分解前的检查(<c>OnStartMission</c>):耐久不大于 0 → 0xbd。</summary>
        public static ExpertJobError CheckStart(int endurance)
        {
            return endurance > 0 ? ExpertJobError.Ok : ExpertJobError.MachineBroken;
        }

        /// <summary>
        /// 整修全价(<c>GetDisjointMachineRepairCost</c>):机器档 <paramref name="grade"/>(0 起)在整修表里第 grade+1 项 (全价 a、耐久上限 b),
        /// 价格 = a − (a × 当前耐久) ÷ b(向零取整);没有这一项返回 0。耐久高于上限时价格为负,这里按 0 算。
        /// </summary>
        public static int RepairCost(ExpertJobScript script, int grade, int endurance)
        {
            if (script == null || !script.TryGetEnduranceRepair(grade + 1, out EnduranceRepairCost row) || row.MaxEndurance == 0)
            {
                return 0;
            }
            int cost = unchecked(row.Cost - unchecked(row.Cost * endurance) / row.MaxEndurance);
            return cost < 0 ? 0 : cost;
        }

        /// <summary>
        /// 整修(<c>OnRepairDisjointMachine</c>):金币够全价就把耐久修到这一档的上限;不够就按"每点耐久的价格"(a ÷ b,整除)买能买的点数,
        /// 实际花费向下取到整数倍。算出来的新耐久不到 1 → 0x16。
        /// </summary>
        public static MachineRepairPlan PlanRepair(ExpertJobScript script, int grade, int endurance, long gold)
        {
            int cost = RepairCost(script, grade, endurance);
            int newEndurance = 0;
            bool hasRow = script != null && script.TryGetEnduranceRepair(grade + 1, out EnduranceRepairCost row0) && row0.MaxEndurance != 0;
            if (gold < cost)
            {
                if (hasRow)
                {
                    script.TryGetEnduranceRepair(grade + 1, out EnduranceRepairCost row);
                    int unit = row.Cost / row.MaxEndurance;
                    if (unit >= 1)
                    {
                        int g = (int)Math.Min(gold, int.MaxValue);
                        cost = g - g % unit;
                        newEndurance = g / unit;
                    }
                }
                newEndurance += endurance;
            }
            else if (hasRow)
            {
                script.TryGetEnduranceRepair(grade + 1, out EnduranceRepairCost row);
                newEndurance = row.MaxEndurance;
            }
            if (newEndurance < 1)
            {
                return new MachineRepairPlan(ExpertJobError.Refused, 0, endurance);
            }
            if (gold < cost)
            {
                return new MachineRepairPlan(ExpertJobError.NotEnoughGold, cost, endurance);
            }
            return new MachineRepairPlan(ExpertJobError.Ok, cost, newEndurance);
        }

        /// <summary>
        /// 升档(<c>OnUpgradeDisjointMachine</c>,按服务端次序):新档(当前档 + 1)必须小于最高档数;新档必须小于副职业等级;
        /// <c>[expertjob level limit]</c> 里要有"当前档 + 2"这一项(没有 → 0x13),角色等级不够 → 0xe;费用取升档表第"新档"项,费用不大于 0 或金币不够 → 0x16。
        /// 成功后耐久设成新档在整修表里的耐久上限(表里没有这一项时耐久不变)。
        /// </summary>
        public static MachineUpgradePlan PlanUpgrade(ExpertJobScript script, ExpertJobEtc etc, int grade, int expertLevel, int charLevel, long gold)
        {
            if (script == null)
            {
                return new MachineUpgradePlan(ExpertJobError.NotPermitted, 0, grade, 0);
            }
            ExpertJobEtc e = etc ?? ExpertJobEtc.Empty;
            int newGrade = grade + 1;
            if (!(newGrade < script.MachineMaxGrade))
            {
                return new MachineUpgradePlan(ExpertJobError.NotPermitted, 0, grade, 0);
            }
            if (!(newGrade < expertLevel))
            {
                return new MachineUpgradePlan(ExpertJobError.NotPermitted, 0, grade, 0);
            }
            if (!e.TryGetLevelLimit(grade + 2, out int limit))
            {
                return new MachineUpgradePlan(ExpertJobError.NotPermitted, 0, grade, 0);
            }
            if (charLevel < limit)
            {
                return new MachineUpgradePlan(ExpertJobError.LevelTooLow, 0, grade, 0);
            }
            int cost = script.GetUpgradeCost(newGrade);
            if (cost <= 0 || gold < cost)
            {
                return new MachineUpgradePlan(ExpertJobError.Refused, cost, grade, 0);
            }
            var plan = new MachineUpgradePlan(ExpertJobError.Ok, cost, newGrade, 0);
            if (script.TryGetEnduranceRepair(newGrade + 1, out EnduranceRepairCost row))
            {
                plan = new MachineUpgradePlan(ExpertJobError.Ok, cost, newGrade, row.MaxEndurance) { ResetsEndurance = true };
            }
            return plan;
        }

        /// <summary>
        /// 分解完成后的结算(<c>complete_disjoint</c>)。随机数次序:耐久消耗、经验、额外经验是否触发(三个都抽,脚本区间反了时按单点处理)。
        /// 耐久消耗 = 抽到的值,不超过当前耐久;经验 = 区间抽值,穿戴的额外经验这次触发(<c>(float)抽值 &lt; 概率</c>)再加上额外值。
        /// 额外经验按<b>请求者</b>穿戴算(自己用时就是机主自己)。
        /// </summary>
        public static DisjointCompletion Complete(ExpertJobScript script, int endurance, float bonusChance, int bonusAmount, IDisjointRandom rng)
        {
            if (script == null)
            {
                throw new ArgumentNullException(nameof(script));
            }
            if (rng == null)
            {
                throw new ArgumentNullException(nameof(rng));
            }
            int lost = rng.Next(Span(script.EnduranceReduceMin, script.EnduranceReduceMax)) + script.EnduranceReduceMin;
            if (endurance < lost)
            {
                lost = endurance;
            }
            int gain = rng.Next(Span(script.GainExpMin, script.GainExpMax)) + script.GainExpMin;
            int baseGain = gain;
            bool hit = (float)rng.Next(100) < bonusChance;
            if (hit)
            {
                gain += bonusAmount;
            }
            return new DisjointCompletion(lost, endurance - lost, baseGain, hit, gain);
        }

        private static int Span(int min, int max)
        {
            int span = max - min + 1;
            return span < 1 ? 1 : span;
        }
    }
}
