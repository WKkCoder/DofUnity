using System;
using System.Collections.Generic;

// 证据:E-127(服务端 CSkillList::init:按职业的技能列表逐个读 .skl,前置技能在读完后建反向表,用来查"谁依赖我")。
namespace Dnf.Game
{
    /// <summary>一个技能的后续依赖:<see cref="SkillId"/> 要求前置技能至少学到 <see cref="RequiresLevel"/> 级。</summary>
    public readonly struct SkillDependent
    {
        public SkillDependent(int skillId, int requiresLevel)
        {
            SkillId = skillId;
            RequiresLevel = requiresLevel;
        }

        public int SkillId { get; }

        public int RequiresLevel { get; }
    }

    /// <summary>
    /// 一个职业的全部技能(按编号排序)与"谁依赖谁"的反向表。学技能、退技能时 <see cref="SkillBook"/> 靠它找技能定义与后续依赖。
    /// 只读;<see cref="Load"/> 从游戏数据读,也可以直接用技能定义列表构造(测试用合成数据)。
    /// </summary>
    public sealed class SkillCatalog
    {
        private static readonly SkillDependent[] NoDependents = new SkillDependent[0];

        private readonly Dictionary<int, SkillDef> _byId = new Dictionary<int, SkillDef>();
        private readonly Dictionary<int, List<SkillDependent>> _dependents = new Dictionary<int, List<SkillDependent>>();
        private readonly List<SkillDef> _skills = new List<SkillDef>();

        public SkillCatalog(int job, IEnumerable<SkillDef> skills)
        {
            Job = job;
            if (skills == null)
            {
                throw new ArgumentNullException(nameof(skills));
            }
            foreach (SkillDef def in skills)
            {
                if (def != null && !_byId.ContainsKey(def.Id))
                {
                    _byId.Add(def.Id, def);
                    _skills.Add(def);
                }
            }
            _skills.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (SkillDef def in _skills)
            {
                foreach (SkillPrereq pre in def.PreRequired)
                {
                    if (!_dependents.TryGetValue(pre.SkillId, out List<SkillDependent> list))
                    {
                        list = new List<SkillDependent>();
                        _dependents.Add(pre.SkillId, list);
                    }
                    list.Add(new SkillDependent(def.Id, pre.Level));
                }
            }
        }

        /// <summary>什么技能都没有的目录(还没接上数据时用)。</summary>
        public static SkillCatalog Empty { get; } = new SkillCatalog(0, new SkillDef[0]);

        public int Job { get; }

        /// <summary>全部技能,按编号从小到大。</summary>
        public IReadOnlyList<SkillDef> Skills => _skills;

        public int Count => _skills.Count;

        /// <summary>按编号找技能;没有返回 null。</summary>
        public SkillDef Find(int skillId)
        {
            return _byId.TryGetValue(skillId, out SkillDef def) ? def : null;
        }

        /// <summary>要求 <paramref name="skillId"/> 作前置的那些技能(与各自要求的等级);没有返回空表。</summary>
        public IReadOnlyList<SkillDependent> DependentsOf(int skillId)
        {
            return _dependents.TryGetValue(skillId, out List<SkillDependent> list) ? list : NoDependents;
        }

        /// <summary>某个成长类型学得了的技能(该成长类型的最高级大于 0),按编号从小到大。</summary>
        public List<SkillDef> ForGrowtype(int growtype)
        {
            return ForGrowtype(growtype, 0);
        }

        /// <summary>某个成长类型觉醒到 <paramref name="awakening"/> 以后学得了的技能(觉醒后的最高级大于 0,<see cref="SkillRules.TypeMaxLevel(SkillDef, int, int)"/>),按编号从小到大。</summary>
        public List<SkillDef> ForGrowtype(int growtype, int awakening)
        {
            var result = new List<SkillDef>();
            foreach (SkillDef def in _skills)
            {
                if (SkillRules.TypeMaxLevel(def, growtype, awakening) > 0)
                {
                    result.Add(def);
                }
            }
            return result;
        }

        /// <summary>读职业 <paramref name="job"/> 的技能列表里的全部技能(经 skill/skilllist.lst);读不到列表得到空目录。</summary>
        public static SkillCatalog Load(GameData data, int job)
        {
            var defs = new List<SkillDef>();
            if (data != null)
            {
                foreach (int id in data.GetSkillIds(job))
                {
                    SkillDef def = data.GetSkill(job, id);
                    if (def != null)
                    {
                        defs.Add(def);
                    }
                }
            }
            return new SkillCatalog(job, defs);
        }
    }
}
