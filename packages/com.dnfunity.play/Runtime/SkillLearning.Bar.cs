using System;
using System.Collections.Generic;
using Dnf.Game;

// 靠指令放与快捷栏占用的次序(证据 E-140:学会、有出招指令、没放进快捷栏的主动技能只靠指令放,HeroSkills.Create 给它们建"指令槽";指令与快捷键共用同一个槽)。
// "只能靠快捷键的技能优先占栏、靠指令也放得出来的让位"是本项目自己的设计(推断,非 REA;原版快捷栏的规则没查)。
namespace Dnf.Play
{
    public sealed partial class SkillLearning
    {
        private Func<int, bool> _implemented = HeroSkills.IsImplemented;

        /// <summary>
        /// 快捷栏的占用次序开关(默认关)。关着时快捷栏完全是 <see cref="SkillBook"/> 自己的"新学会的主动技能先到先占第一个空格"——学会第七个以后只能靠快捷键的技能既不上栏、也放不出来,
        /// 退技能腾出来的格子也不会补给它。开着时每次学、退、重置、批量、自动学之后都调一次 <see cref="ArrangeBar"/>。
        /// </summary>
        public bool CommandAware { get; set; }

        /// <summary>
        /// 一个技能的放法有没有实现(没实现的,按快捷键或按指令都放不出东西)的判定;默认是 <see cref="HeroSkills.IsImplemented"/>(默认配方表等)。
        /// 合成数据的测试换成自己的;设成 null 回到默认。
        /// </summary>
        public Func<int, bool> Implemented
        {
            get => _implemented;
            set => _implemented = value ?? HeroSkills.IsImplemented;
        }

        /// <summary>这个技能靠出招指令放得出来吗:放法实现了,并且指令能由指令层放出(<see cref="HeroSkills.CommandCasts"/>:技能键或增益键当扳机,或跳跃、攻击键收尾的多键指令)。</summary>
        private bool CastsByCommand(SkillDef def)
        {
            return def != null && _implemented(def.Id) && HeroSkills.CommandCasts(def.Command);
        }

        /// <summary>
        /// 这个技能现在能靠什么放出来(规则见 <see cref="SkillReach"/>):没学是 <see cref="SkillReach.NotLearned"/>,被动是 <see cref="SkillReach.Passive"/>,
        /// 主动技能在快捷栏里是 <see cref="SkillReach.OnBar"/>;不在栏里的看指令放不放得出来。
        /// </summary>
        public SkillReach ReachOf(int skillId)
        {
            SkillBook book = _hero.Skills;
            SkillDef def = book.Catalog.Find(skillId);
            if (def == null || book.LevelOf(skillId) < 1)
            {
                return SkillReach.NotLearned;
            }
            if (!def.IsActive)
            {
                return SkillReach.Passive;
            }
            if (book.QuickSlotOf(skillId) >= 0)
            {
                return SkillReach.OnBar;
            }
            return CastsByCommand(def) ? SkillReach.CommandOnly : SkillReach.Unreachable;
        }

        /// <summary>只能靠快捷键放(放法实现了、指令放不出来)、却没有上栏的技能个数;<see cref="CommandAware"/> 就是要把它压到 0(六格都被这样的技能占满时除外)。</summary>
        public int Stranded()
        {
            return StrandedSkills().Count;
        }

        /// <summary>只靠快捷键放得出来、没上栏的学会了的主动技能,按编号从小到大。</summary>
        private List<SkillDef> StrandedSkills()
        {
            var list = new List<SkillDef>();
            SkillBook book = _hero.Skills;
            foreach (KeyValuePair<int, int> learned in book.Learned)
            {
                SkillDef def = book.Catalog.Find(learned.Key);
                if (def != null && def.IsActive && book.QuickSlotOf(def.Id) < 0 && _implemented(def.Id) && !HeroSkills.CommandCasts(def.Command))
                {
                    list.Add(def);
                }
            }
            return list;
        }

        /// <summary>
        /// 整理快捷栏:只能靠快捷键放的技能(<see cref="Stranded"/>)按编号从小到大上栏。先占空格;空格不够,就从占着格子的技能里挑让位的——
        /// "放法没实现的"在前、"靠指令也放得出来的"在后,同一档里越靠右越先让位;被顶掉的技能仍然学着,改靠指令放。
        /// 用到的格子(空格加让位的)从左到右依次给编号从小到大的技能;让位的格子都用完、六格还被只能靠快捷键的技能占着,剩下的就放不下。
        /// 没有这样的技能时一格也不动,玩家手摆的快捷栏原样保留。返回这一次新上栏了几个技能。
        /// </summary>
        public int ArrangeBar()
        {
            List<SkillDef> stranded = StrandedSkills();
            if (stranded.Count == 0)
            {
                return 0;
            }
            SkillBook book = _hero.Skills;
            var slots = new List<int>();
            for (int slot = 0; slot < SkillBook.QuickSlots && slots.Count < stranded.Count; slot++)
            {
                if (book.QuickAt(slot) == 0)
                {
                    slots.Add(slot);
                }
            }
            if (slots.Count < stranded.Count)
            {
                List<int> yielding = YieldingSlots(book);
                for (int i = 0; i < yielding.Count && slots.Count < stranded.Count; i++)
                {
                    slots.Add(yielding[i]);
                }
            }
            slots.Sort();
            int placed = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (book.AssignQuick(slots[i], stranded[i].Id))
                {
                    placed++;
                }
            }
            return placed;
        }

        /// <summary>占着格子、又该让位的格子,让位的先后:放法没实现的在前、靠指令也放得出来的在后,同一档里越靠右越前。</summary>
        private List<int> YieldingSlots(SkillBook book)
        {
            var unimplemented = new List<int>();
            var byCommand = new List<int>();
            for (int slot = SkillBook.QuickSlots - 1; slot >= 0; slot--)
            {
                SkillDef def = book.Catalog.Find(book.QuickAt(slot));
                if (def == null)
                {
                    continue;
                }
                if (!_implemented(def.Id))
                {
                    unimplemented.Add(slot);
                }
                else if (HeroSkills.CommandCasts(def.Command))
                {
                    byCommand.Add(slot);
                }
            }
            unimplemented.AddRange(byCommand);
            return unimplemented;
        }
    }
}
