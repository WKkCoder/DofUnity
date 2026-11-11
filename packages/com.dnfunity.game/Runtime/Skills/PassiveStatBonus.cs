using System;
using System.Collections.Generic;

// 证据与边界:r15-growth(服务端 df_game_r 与本机技能数据的静态核对;观察/推断/未知分开写):
// 观察 ① 服务端没有"被动技能 → 属性"的计算。角色属性(_Additioal_info)的组成只有:职业基础块 + 按等级段累加的成长块
//        (CUser::calc_lev_stat 0x08664ae8 与 WongWork::CCalcCharacStat::CalcStat 0x0831ec4a:1 到 14 级逐级加"基础成长块"、15 到 49 级逐级加第一成长类型块、
//        50 级起逐级加第二成长类型块;取块 CCharacter::get_growtype_stat 0x0834883e)+ 任务药剂(CUser::calc_quest_stat 0x08664c50 → ApplyElixerItem 0x0831e85c)
//        + 成长类型切换时整块减旧加新(CUser::_update_charac_stat 0x086646c8)。CParty::check_passive_skill 0x085a932a 是空函数。
// 观察 ② 本机技能数据里没有"这个被动改哪一项属性"的字段:类型为被动的 .skl 里,面板九项属性标签(与 StatBlock 同一批)一个都没出现;
//        能读到的只有 [level info] 的按级数列,列的含义不在 .skl 里(计数见 out/growth/NOTES.md)。
// 观察 ③ 脚本侧的被动属性只出现在三个职业的 ProcPassiveSkill 里,走"附加物 + ChangeStatus"(属于增益系统 HeroBuffs 的范围,不是属性块);
//        其余职业的被动在客户端原生代码里,本线没有读(主角职业用的是后者)。
// 推断:因此"哪个被动改哪项属性、按哪一列怎么折算"对主角职业目前没有证据,不能编。这里只做机制:登记表里一条规则 = 技能 → 属性块的一项 ← [level info] 的一列 × 折算,
//        默认登记表是空的(叠加量恒为 0,行为不变);有了读码证据的映射由宿主或后续线登记。
// 时机:按 r9-ps 合入的刷新时机表(PassiveAttackBonus 的说明:学、退、重置、转职、读档都会刷新被动登记),技能簿一变就重算;叠在"等级 + 装备"之后(属性块逐项相加,与次序无关)。
// 未知:原版把取值折成属性的取整方式(这里把整数取值乘一个整数折算,没有取整问题);按百分比增减的被动(ChangeStatus 的"按比例"形式)这里不支持。
namespace Dnf.Game
{
    /// <summary>
    /// 一条被动属性规则:技能 <see cref="SkillId"/> 学到 L 级时,属性块的 <see cref="StatTag"/> 那一项加上
    /// "[level info] 第 L 级第 <see cref="Column"/> 列的值 × <see cref="TenthsPerValue"/>"(十分之一单位,与 <see cref="StatBlock"/> 一致)。
    /// </summary>
    public readonly struct PassiveStatRule
    {
        public PassiveStatRule(int skillId, string statTag, int column, long tenthsPerValue = 10)
        {
            SkillId = skillId;
            StatTag = statTag;
            Column = column;
            TenthsPerValue = tenthsPerValue;
        }

        public int SkillId { get; }

        /// <summary>属性块里的项,用 .chr 里的标签写法(<c>[hp max]</c>、<c>[physical attack]</c>……九项之一)。</summary>
        public string StatTag { get; }

        /// <summary><c>[level info]</c> 的列下标。</summary>
        public int Column { get; }

        /// <summary>取值 1 折成属性块的几个十分之一(默认 10:取值就是显示值)。</summary>
        public long TenthsPerValue { get; }
    }

    /// <summary>
    /// 被动属性规则的登记表:同一个技能可以有多条(各改一项);同一技能、同一项再登记只覆盖。
    /// 默认是空的。<see cref="Version"/> 随登记变化加一,算被动属性的一方据此决定要不要重算。
    /// </summary>
    public sealed class PassiveStatRegistry
    {
        private readonly List<PassiveStatRule> _rules = new List<PassiveStatRule>();

        public int Version { get; private set; }

        public int Count => _rules.Count;

        public IReadOnlyList<PassiveStatRule> Rules => _rules;

        /// <summary>登记一条规则;属性项不是属性块的九项之一时返回 false、不登记。</summary>
        public bool Register(PassiveStatRule rule)
        {
            if (rule.StatTag == null || !default(StatBlock).TrySet(rule.StatTag, 0, out _))
            {
                return false;
            }
            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].SkillId == rule.SkillId && string.Equals(_rules[i].StatTag, rule.StatTag, StringComparison.OrdinalIgnoreCase))
                {
                    _rules[i] = rule;
                    Version++;
                    return true;
                }
            }
            _rules.Add(rule);
            Version++;
            return true;
        }

        /// <summary>删掉某个技能的全部规则;返回删了几条。</summary>
        public int Remove(int skillId)
        {
            int removed = _rules.RemoveAll(r => r.SkillId == skillId);
            if (removed > 0)
            {
                Version++;
            }
            return removed;
        }

        public void Clear()
        {
            if (_rules.Count > 0)
            {
                _rules.Clear();
                Version++;
            }
        }
    }

    /// <summary>已学被动技能折成属性块(规则与边界见文件头)。</summary>
    public static class PassiveStatBonus
    {
        /// <summary>
        /// 把登记表里每条规则按技能现在的等级折成属性块并逐项相加。未学(等级 ≤ 0)、技能数据取不到、取值表没有这一级的规则不加;
        /// 取值按 <see cref="PassiveAttackBonus.LevelColumn"/>(等级超出取值表的级数为 0)。
        /// </summary>
        public static StatBlock Compute(PassiveStatRegistry registry, Func<int, SkillDef> resolveSkill, Func<int, int> levelOf, bool pvp = false)
        {
            StatBlock total = default;
            if (registry == null || registry.Count == 0 || resolveSkill == null || levelOf == null)
            {
                return total;
            }
            foreach (PassiveStatRule rule in registry.Rules)
            {
                int level = levelOf(rule.SkillId);
                if (level <= 0)
                {
                    continue;
                }
                // 技能数据取不到(目录里没有这个技能)时 LevelColumn 返回 0,加 0 不改变总和,所以不必单独跳过
                SkillDef def = resolveSkill(rule.SkillId);
                long value = (long)PassiveAttackBonus.LevelColumn(def, level, rule.Column, pvp);
                if (default(StatBlock).TrySet(rule.StatTag, value * rule.TenthsPerValue, out StatBlock part))
                {
                    total = total + part;
                }
            }
            return total;
        }
    }
}
