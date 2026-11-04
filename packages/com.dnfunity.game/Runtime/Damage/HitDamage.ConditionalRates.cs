using System;
using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 三个条件伤害率开关(E-110、E-212):客户端 0x0087b870 按<strong>防御者</strong>对象上的类别 id 选开关,不是 ObjectType。
    /// 观察(静态读码,非 REA):
    /// <list type="bullet">
    /// <item>+0x278 类别容器:对 id 0x14/0x15 调 0x007c4340 与 0x01176170(IS CATEGORY),迭代器不等则开关 0/1 为真(0x0087c948、0x0087c9de)。</item>
    /// <item>+0x27c..+0x280 类别 id 向量:循环 0x0087ca1c 计数 id∈{0x14,0x15,0x16};计数为 0 则开关 2 为真(0x0087ca49–0x0087ca55)。</item>
    /// </list>
    /// id 0x14/0x15/0x16 与全局类别表 human/fort/divine armor 一致(推断,E-171)。
    /// </summary>
    public static class ConditionalDamageRateFlags
    {
        public const int HumanArmorCategoryId = 0x14;
        public const int FortArmorCategoryId = 0x15;
        public const int DivineArmorCategoryId = 0x16;

        /// <summary>按读码写入三个开关与攻击信息里的条件率百分数。</summary>
        public static void Apply(DamageVariables p, HitInput input, IReadOnlyList<int> defenderCategoryIds)
        {
            if (p == null)
            {
                throw new ArgumentNullException(nameof(p));
            }
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            p.SetConditionalPercent(0, input.HumanRate);
            p.SetConditionalPercent(1, input.FortRate);
            p.SetConditionalPercent(2, input.MonsterRate);
            (bool f0, bool f1, bool f2) = ComputeFlags(defenderCategoryIds);
            p.SetConditionalFlag(0, f0);
            p.SetConditionalFlag(1, f1);
            p.SetConditionalFlag(2, f2);
        }

        /// <summary>无类别列表时等同空向量 + 空 IS CATEGORY → (false,false,true)。</summary>
        public static void ApplyDefault(DamageVariables p, HitInput input)
        {
            Apply(p, input, null);
        }

        /// <summary>手算依据:0x0087c948/0x0087c9de 与 0x0087ca1c 循环(见 Golden 文件注释)。</summary>
        public static (bool Human, bool Fort, bool Monster) ComputeFlags(IReadOnlyList<int> defenderCategoryIds)
        {
            bool human = ContainsCategoryId(defenderCategoryIds, HumanArmorCategoryId);
            bool fort = ContainsCategoryId(defenderCategoryIds, FortArmorCategoryId);
            bool monster = !VectorListsArmorCategoryId(defenderCategoryIds);
            return (human, fort, monster);
        }

        internal static bool ContainsCategoryId(IReadOnlyList<int> ids, int categoryId)
        {
            if (ids == null || ids.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == categoryId)
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool VectorListsArmorCategoryId(IReadOnlyList<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                if (id == HumanArmorCategoryId || id == FortArmorCategoryId || id == DivineArmorCategoryId)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
