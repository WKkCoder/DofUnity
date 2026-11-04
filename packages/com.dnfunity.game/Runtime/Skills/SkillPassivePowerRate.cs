using System;
using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 被动技能对主动技能威力列的倍率加成(证据 E-181;客户端 0x00684510 静态读码,非 REA)。
    /// 观察:遍历主角 +0x5938;项 +0xc 主动号、+0x10 static 下标;被动已学时 0x0090dd10(查被动 [executable states] 是否含当前 native 状态);
    /// 0x009129d0 读 static 整数 × 0x011a0de0 读出的受保护浮点(初始化自 0x01565cc0,模拟器测得 0.01,非 REA)后 FADD;
    /// 项 +0x15 非 0 时终止整表(0x6845ed→0x684603),不是跳过单项(Ghidra 0x00684510 与汇编一致)。
    /// 观察:+0x5938 由 Squirrel 原生接口 sq_AddPassiveSkillAttackBonusRate(0x00689f70,登记 0x0061040c)写入,非 .skl 加载器直写;默认表无脚本执行故仍惰性。
    /// </summary>
    public static class SkillPassivePowerRate
    {
        /// <summary>受保护浮点乘子(观察:0x01565cc0→0x01a6fbd4,passive_power_oracle.py 模拟器,非 REA)。</summary>
        public const float ProtectedRateScale = 0.01f;

        /// <summary>旧名;保留以免外部引用编译失败,值与 <see cref="ProtectedRateScale"/> 相同。</summary>
        public const float StaticScale = ProtectedRateScale;

        /// <summary>空中攻击强化 native 状态号(观察 E-150,静态读码,非 REA);JumpStrike 取列时传入 0x00684510 的 state 参数。</summary>
        public const int JumpAttackNativeState = 7;

        /// <summary>一条被动→主动威力加成(对应 +0x5938 表项)。</summary>
        public readonly struct Link
        {
            public Link(int passiveSkillId, int activeSkillId, int staticIndex, bool terminatesChain = false)
            {
                PassiveSkillId = passiveSkillId;
                ActiveSkillId = activeSkillId;
                StaticIndex = staticIndex;
                TerminatesChain = terminatesChain;
            }

            public int PassiveSkillId { get; }
            public int ActiveSkillId { get; }
            public int StaticIndex { get; }
            /// <summary>表项 +0x15 非 0:处理完本项后终止遍历(0x6845ed)。</summary>
            public bool TerminatesChain { get; }
        }

        public static IReadOnlyList<Link> LinksFromRecipes(IEnumerable<SkillPassivePowerLink> recipeLinks, int activeSkillId)
        {
            if (recipeLinks == null)
            {
                return Array.Empty<Link>();
            }
            var list = new List<Link>();
            foreach (SkillPassivePowerLink item in recipeLinks)
            {
                list.Add(new Link(item.PassiveSkillId, activeSkillId, item.StaticIndex, item.TerminatesChain));
            }
            return list;
        }

        /// <summary>
        /// 0x0090dd10:被动技能的 [executable states] 须包含 <paramref name="nativeState"/>;空表则永不加成(与 isExcutableState 脚本的"空=不限"不同,以 0x90dd10 为准)。
        /// </summary>
        public static bool PassiveAppliesInNativeState(SkillDef passive, int nativeState)
        {
            if (passive == null)
            {
                return false;
            }
            int[] states = passive.ExecutableStates;
            if (states == null || states.Length == 0)
            {
                return false;
            }
            return Array.IndexOf(states, nativeState) >= 0;
        }

        /// <summary>在基础倍率上累加被动贡献; <paramref name="nativeState"/> 为客户端传入 0x00684510 的当前 native 状态。</summary>
        public static float Accumulate(float baseRate, int activeSkillId, int nativeState, Func<int, int> skillLevelOf, Func<int, SkillDef> resolveSkill,
            IReadOnlyList<Link> links)
        {
            if (links == null || links.Count == 0 || skillLevelOf == null || resolveSkill == null)
            {
                return baseRate;
            }
            float rate = baseRate;
            foreach (Link link in links)
            {
                if (link.ActiveSkillId == activeSkillId && link.PassiveSkillId != activeSkillId && skillLevelOf(link.PassiveSkillId) > 0)
                {
                    SkillDef passive = resolveSkill(link.PassiveSkillId);
                    if (passive != null && IsPassiveType(passive) && PassiveAppliesInNativeState(passive, nativeState) && link.StaticIndex >= 0)
                    {
                        int raw = passive.GetStatic(link.StaticIndex, false);
                        rate += raw * ProtectedRateScale;
                    }
                }
                if (link.TerminatesChain)
                {
                    break;
                }
            }
            return rate;
        }

        internal static bool IsPassiveType(SkillDef def)
        {
            return def.Type != null && def.Type.IndexOf("passive", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>主动技能配方上可选的一条被动威力加成规则。</summary>
    public readonly struct SkillPassivePowerLink
    {
        public SkillPassivePowerLink(int passiveSkillId, int staticIndex, bool terminatesChain = false)
        {
            PassiveSkillId = passiveSkillId;
            StaticIndex = staticIndex;
            TerminatesChain = terminatesChain;
        }

        public int PassiveSkillId { get; }
        public int StaticIndex { get; }
        /// <summary>对应表项 +0x15:非 0 时本项处理后终止整表。</summary>
        public bool TerminatesChain { get; }
    }
}
