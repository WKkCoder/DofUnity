// 证据:r15-awaken 读码(服务端 CUser::set_grow_type 0x086787fc、CUser::quest_reward 0x0866ea92 的奖励种类 2、CCharacter::get_give_skill* 0x08348798 一族、
// CCharacter::get_growtype_stat 0x0834883e;.chr 觉醒块 ImportCharacterScript 0x08a0ce06)。觉醒在服务端就是"第二成长类型"从 0 变成 1 或 2。
namespace Dnf.Game
{
    /// <summary>觉醒申请的结果:通过,或第一条挡住的原因(按检查的先后)。</summary>
    public enum AwakeningCheck
    {
        Ok = 0,

        /// <summary>觉醒号不在 1 到 <see cref="AwakeningRules.Count"/> 之内。</summary>
        BadTarget = 1,

        /// <summary>还没转职:成长类型 0 没有觉醒块与觉醒技能(.chr 里 [awakening 1]、[awakening 2] 只在 [growtype 2] 到 [growtype 6] 之下),成长类型越界也算。</summary>
        NotTransferred = 2,

        /// <summary>等级不到 <see cref="AwakeningRules.MinLevel"/>。</summary>
        LevelTooLow = 3,

        /// <summary>不是紧接着的下一次觉醒:要从现在的觉醒号加一,不能跳级(没一觉先二觉)、不能重复、不能退回。</summary>
        OutOfOrder = 4,
    }

    /// <summary>
    /// 觉醒(第二成长类型)的纯规则,不含状态。
    /// 观察:服务端把觉醒做成"第二成长类型"(0 没觉醒、1 一觉、2 二觉),由任务奖励种类 <c>[awakening type]</c> 触发,<c>quest_reward</c> 对它只做
    /// <c>set_grow_type(第一成长类型不变, 第二成长类型 = [reward int data] 第一个数)</c>,没有等级、次序或转职与否的检查——
    /// 挡人的只有任务脚本自己的 <c>[level]</c> 区间(真实数据里 50 到 99)、前置任务链与 <c>[job]</c>/<c>[grow type]</c> 目标。
    /// 推断:下面三条是这里自己加的防线,各有依据——等级 50 是觉醒块开始生效(<see cref="CharacterGrowth.AwakeningFromLevel"/>)与觉醒点开始累计
    /// (<see cref="Leveling.AwakeningFirstLevel"/>)的等级;觉醒块只在转职后的成长类型之下;一觉与二觉各有自己的属性块与白送技能列表,跳级会让二觉的技能没有一觉打底。
    /// </summary>
    public static class AwakeningRules
    {
        /// <summary>觉醒的最少等级(= 觉醒属性块开始生效的等级,也是觉醒点开始累计的等级)。</summary>
        public const int MinLevel = CharacterGrowth.AwakeningFromLevel;

        /// <summary>觉醒的次数上限(一觉、二觉)。</summary>
        public const int Count = CharacterGrowth.AwakeningCount;

        /// <summary>任务标志 <c>[job change quest]</c> 的"觉醒任务链"值(1 是转职任务;真实数据里标志 2 的任务全是 50 级觉醒任务链,服务端 isDoingJobChangeQuest 把 1 与 2 都当转职任务)。</summary>
        public const int QuestFlag = 2;

        /// <summary>
        /// 等级 <paramref name="level"/>、成长类型 <paramref name="growtype"/>(下标,0 是转职前)、现在的觉醒 <paramref name="current"/> 的角色,能不能觉醒到 <paramref name="target"/>。
        /// 按 <see cref="AwakeningCheck"/> 的次序检查,第一条挡住的就是结果。
        /// </summary>
        public static AwakeningCheck Check(int level, int growtype, int current, int target)
        {
            if (target < 1 || target > Count)
            {
                return AwakeningCheck.BadTarget;
            }
            if (growtype < 1 || growtype >= CharacterGrowth.GrowtypeCount)
            {
                return AwakeningCheck.NotTransferred;
            }
            if (level < MinLevel)
            {
                return AwakeningCheck.LevelTooLow;
            }
            if (target != current + 1)
            {
                return AwakeningCheck.OutOfOrder;
            }
            return AwakeningCheck.Ok;
        }
    }
}
