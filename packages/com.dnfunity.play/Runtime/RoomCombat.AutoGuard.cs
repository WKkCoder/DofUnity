using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 自动防御(<see cref="BuffStat.AutoGuardChance"/>):客户端主角受击前钩子(虚表槽 0x2d8,0x00cb20a0)在防御姿态分支之后的一段(0x00cb23a6 起;静态读码,非 REA,
    /// 当时 E-164 留着没做)。增益生效期间,主角没在姿态里、站着走着跑着时挨到一击能挡的攻击,每次掷一次概率;掷中就像自己按了防御姿态:
    /// 转向攻击方(0x00cb25fc 一段)、进姿态(借用防御姿态技能的动作与最短保持)、伤害乘那个技能的穿过百分比、没有受击反应、后滑(同 E-164 的挡下)。
    /// 近似与没做见 <see cref="AutoGuardSlotFor"/> 与默认表里技能的配方注释。
    /// </summary>
    public sealed partial class RoomCombatSession
    {
        /// <summary>自动防御挡下的次数(进了姿态的;同时也计在 <see cref="GuardBlocks"/> 里)。</summary>
        public int AutoGuardBlocks { get; private set; }

        /// <summary>
        /// 这一击触发自动防御吗:触发返回借用的防御姿态技能槽,否则返回 null(不耗随机数序列的条件放在掷骰之前)。
        /// 条件(客户端 0x00cb23a6 起的顺序):增益生效;防御姿态技能学会了(槽在);这一击能挡(黑盒 0x010ee67f,按 E-164 近似为"有受击反应");
        /// 然后掷骰——概率 × 10000 大于 [0, 10000) 的随机数(客户端:列值整数 × 100 大于掷出的数)。
        /// 客户端另外排除倒地(状态 4)与冰冻石化(状态 9,推断)里的主角:这里它们都表现为主角被留在受击或倒地姿势里,<see cref="TownHero.CanBeginSkill"/> 已经为假,不另写;
        /// 死了的主角同理。客户端最后还问一次防御姿态技能"能不能用"(脚本函数,没读通),这里没有。
        /// 进姿态的请求在客户端带优先级 0,出招、跳跃、受击硬直中能不能被顶掉没读通,这里只在主角腾得出手(站着、走着、跑着)
        /// 而且还不在姿态里时触发——主角已经在姿态里却被打到背面的那一击也不再挡。
        /// </summary>
        private HeroSkillSlot AutoGuardSlotFor(HeroStrikeInfo info, TownHero hero)
        {
            double chance = _buffs.Total(BuffStat.AutoGuardChance);
            if (chance <= 0.0 || info.ReactionNone || hero.Guarding || !hero.CanBeginSkill)
            {
                return null;
            }
            HeroSkillSlot guard = null;
            foreach (HeroBuff buff in _buffs.Active)
            {
                if (buff.Stat != BuffStat.AutoGuardChance)
                {
                    continue;
                }
                BuffRecipe recipe = Skills?.SlotOfSkill(buff.SkillId)?.Buff;
                HeroSkillSlot candidate = recipe == null ? null : Skills.SlotOfSkill(recipe.GuardSkillId);
                if (candidate?.Guard != null)
                {
                    guard = candidate;
                    break;
                }
            }
            if (guard == null || chance * 10000.0 <= _procRandom.Next(10000))
            {
                return null;
            }
            return guard;
        }

        /// <summary>
        /// 自动防御挡下之后让主角进姿态:先转向攻击方(攻击方在朝向那一侧就不转;投射物与被动对象没有怪物当攻击方,同 <see cref="GuardSlotFor"/> 按朝着主角飞来的方向近似),
        /// 再用防御姿态技能的动作与最短保持进姿态(姿态的维持与结束同手按的姿态一样:最短保持过了而且触发键松开)。进不了(读不到姿势动作)时只剩减伤。
        /// </summary>
        private void EnterAutoGuard(HeroSkillSlot guard, bool attackerFacingLeft, MonsterActor attacker, TownHero hero)
        {
            bool attackerOnRight = attacker == null ? attackerFacingLeft : AttackerIsOnRight(attacker.X, hero.X, attackerFacingLeft);
            hero.TurnTo(!attackerOnRight);
            if (hero.BeginGuard(guard.SkillId, guard.Guard.MotionIndex, guard.GuardMinHoldMs))
            {
                AutoGuardBlocks++;
            }
        }
    }
}
