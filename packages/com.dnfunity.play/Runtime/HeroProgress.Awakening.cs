using System.Collections.Generic;
using Dnf.Game;

// 证据:r15-awaken 读码(服务端 CUser::set_grow_type 0x086787fc、CUser::quest_reward 0x0866ea92 的奖励种类 2、CCharacter::get_give_skill* 0x08348798 一族、
// CCharacter::get_growtype_stat 0x0834883e);规则见 AwakeningRules,白送列表的读法见 SkillGrants,觉醒后的技能最高级见 SkillRules.TypeMaxLevel(def, growtype, awakening)。
namespace Dnf.Play
{
    /// <summary>
    /// 觉醒(第二成长类型):主角的 <see cref="Awakening"/> 从 0 变成 1(一觉)再变成 2(二觉)。
    /// 觉醒时三件事一起发生:① 属性换成这个成长类型的觉醒块的增量(<see cref="BaseStats"/> 按 <see cref="Awakening"/> 取块,
    /// 50 级以后才用,<see cref="Version"/> 加一,战斗会话据此换面板);② 白送这次觉醒的技能(并入技能簿的白送下限,已学的技能与点数一律不动);
    /// ③ 技能的最高级改看觉醒后的([second growtype maximum level],没写的沿用成长类型本身的)。
    /// 觉醒点照旧只随等级累计(<see cref="Dnf.Game.SkillRules.TotalSfp"/>),觉醒本身不给也不扣。
    /// </summary>
    public sealed partial class HeroProgress
    {
        /// <summary>现在白送的技能:开局的与成长类型的,觉醒了再并上这次觉醒那一份(<see cref="SkillGrants"/>)。</summary>
        private List<SkillGrant> GivenSkills()
        {
            return SkillGrants.Read(_chrLines, Growtype, Awakening);
        }

        /// <summary>现在能不能觉醒到 <paramref name="target"/>:不动任何状态,规则与结果见 <see cref="AwakeningRules.Check"/>(用主角现在的等级、成长类型与觉醒)。</summary>
        public AwakeningCheck CheckAwaken(int target)
        {
            return AwakeningRules.Check(Level, Growtype, Awakening, target);
        }

        /// <summary>
        /// 觉醒到 <paramref name="target"/>(1 一觉、2 二觉):通过 <see cref="CheckAwaken"/> 才动手,返回 <see cref="AwakeningCheck.Ok"/>;
        /// 不通过什么都不变,返回挡住的原因。动手时:换觉醒、并入这次觉醒白送的技能(新得到的主动技能放进快捷栏的空格)、<see cref="Version"/> 加一。
        /// 服务端对应 set_grow_type(第一成长类型不变,第二成长类型 = target):它只给这次的白送列表,不清已学的技能、不动点数。
        /// </summary>
        public AwakeningCheck Awaken(int target)
        {
            AwakeningCheck check = CheckAwaken(target);
            if (check != AwakeningCheck.Ok)
            {
                return check;
            }
            Awakening = target;
            Skills.GrantGiven(GivenSkills());
            _levelVersion++;
            return AwakeningCheck.Ok;
        }
    }
}
