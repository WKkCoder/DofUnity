using System;
using System.Collections.Generic;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 自动机的技能加点策略(M35,本项目自己的占位策略,不是原版行为——原版的点数由玩家花):把主角手里的技能点与觉醒点花光。
    /// 每一轮按"主动技能在前、再按要求等级与编号"的次序给每个技能尝试加一级(规则与玩家在窗口里点学习完全一样,走 <see cref="HeroProgress.LearnSkill"/>),
    /// 能加就加;一整轮都没加成(点数花光、等级不够、前置技能没满足、到顶)就停。所以点数是平均撒在各个技能上的,前置技能随着轮次自然先满足。
    /// 新学会的主动技能由技能簿自动放进快捷栏的空格(<see cref="SkillBook.Learn"/>),所以学完之后快捷栏也满了。
    /// </summary>
    public static class SkillPlanner
    {
        /// <summary>一轮里技能的次序:主动技能在前,其次按要求等级,最后按编号。</summary>
        public static List<SkillDef> Order(SkillCatalog catalog, int growtype)
        {
            return Order(catalog, growtype, 0);
        }

        /// <summary>同 <see cref="Order(SkillCatalog, int)"/>,技能范围按觉醒 <paramref name="awakening"/> 以后的算(觉醒后新开放的技能也在里面)。</summary>
        public static List<SkillDef> Order(SkillCatalog catalog, int growtype, int awakening)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }
            List<SkillDef> list = catalog.ForGrowtype(growtype, awakening);
            list.Sort((a, b) =>
            {
                int byActive = b.IsActive.CompareTo(a.IsActive);
                if (byActive != 0)
                {
                    return byActive;
                }
                int byLevel = a.RequiredLevel.CompareTo(b.RequiredLevel);
                return byLevel != 0 ? byLevel : a.Id.CompareTo(b.Id);
            });
            return list;
        }

        /// <summary>
        /// 把 <paramref name="hero"/> 的点数花光,返回一共加了多少级;没有技能数据(<see cref="HeroProgress.HasSkillData"/> 为假)返回 0。
        /// 结果只取决于主角的等级、成长类型、点数与已学的技能,没有随机。
        /// </summary>
        public static int SpendAll(HeroProgress hero)
        {
            if (hero == null)
            {
                throw new ArgumentNullException(nameof(hero));
            }
            if (!hero.HasSkillData)
            {
                return 0;
            }
            List<SkillDef> order = Order(hero.Skills.Catalog, hero.Growtype, hero.Awakening);
            int levels = 0;
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (SkillDef def in order)
                {
                    if (hero.LearnSkill(def.Id, 1, out _) == SkillOpResult.Ok)
                    {
                        levels++;
                        progress = true;
                    }
                }
            }
            return levels;
        }
    }
}
