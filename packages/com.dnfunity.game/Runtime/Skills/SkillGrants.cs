using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-127(服务端 ImportCharacterScript 的 [skill] 处理与 CCharacter 的"给技能"查询;开局给的技能与转职给的技能)。
namespace Dnf.Game
{
    /// <summary>角色脚本给的一个技能:技能编号与给到的等级。</summary>
    public readonly struct SkillGrant
    {
        public SkillGrant(int skillId, int level)
        {
            SkillId = skillId;
            Level = level;
        }

        public int SkillId { get; }

        public int Level { get; }
    }

    /// <summary>
    /// 角色脚本(.chr)里"白送"的技能(证据 E-127)。[initial value] 块里的 [skill] 是新建角色就有的("编号 等级"成对);
    /// [growtype N] 块里的 [skill] 是转到第 N−1 号成长类型时再给的(下标 0 是转职前,没有这一份)。
    /// 一个成长类型拥有的白送技能 = 开局的并上自己那一块,同一个技能取较高的等级、次序是先开局后转职。
    /// 白送的等级就是退技能的下限(<see cref="SkillBook"/>:技能不能低于白送的等级)。
    /// </summary>
    public static class SkillGrants
    {
        /// <summary>读 <paramref name="chrLines"/> 里成长类型 <paramref name="growtype"/> 拥有的白送技能;<paramref name="chrLines"/> 为 null 得到空表。</summary>
        public static List<SkillGrant> Read(ScriptLine[] chrLines, int growtype)
        {
            var result = new List<SkillGrant>();
            if (chrLines == null)
            {
                return result;
            }
            // 当前块:0 = 还没遇到块,1 = 初始值,2 = 成长类型 N,3 = 觉醒块(不读)
            int mode = 0;
            int block = -1;
            foreach (ScriptLine line in chrLines)
            {
                string tag = line.Tag;
                if (tag == null)
                {
                    continue;
                }
                if (line.TagIs("[initial value]"))
                {
                    mode = 1;
                    continue;
                }
                if (tag.StartsWith("[growtype ", StringComparison.OrdinalIgnoreCase) && tag.EndsWith("]", StringComparison.Ordinal))
                {
                    string number = tag.Substring("[growtype ".Length, tag.Length - "[growtype ".Length - 1);
                    mode = int.TryParse(number, out int n) && n >= 1 ? 2 : 0;
                    block = mode == 2 ? n - 1 : -1;
                    continue;
                }
                if (tag.StartsWith("[awakening ", StringComparison.OrdinalIgnoreCase))
                {
                    mode = 3;
                    continue;
                }
                if (!line.TagIs("[skill]") || (mode != 1 && !(mode == 2 && block == growtype)))
                {
                    continue;
                }
                List<int> pairs = line.Ints();
                for (int i = 0; i + 1 < pairs.Count; i += 2)
                {
                    Add(result, pairs[i], pairs[i + 1]);
                }
            }
            return result;
        }

        private static void Add(List<SkillGrant> list, int skillId, int level)
        {
            if (level < 1)
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].SkillId == skillId)
                {
                    if (level > list[i].Level)
                    {
                        list[i] = new SkillGrant(skillId, level);
                    }
                    return;
                }
            }
            list.Add(new SkillGrant(skillId, level));
        }
    }
}
