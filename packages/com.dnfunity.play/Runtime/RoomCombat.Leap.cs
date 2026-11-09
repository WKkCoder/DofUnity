using Dnf.Render;

namespace Dnf.Play
{
    // 脚本前冲(状态 0x2d 的模式 1,规格与读码见 SkillLeapSpec):要看每一步主角位置与时间,放在房间会话里;
    // 与 RoomCombat.BodyHit.cs 一样用公开的 Teleport 摆位置(MoveHeroTo 只查可走区)。
    // 离地高度的弧线(SkillLeapRules.Height)已有纯规则,但主角的高度(TownHero.Z)只有渲染层能改,这里没接线。
    public sealed partial class RoomCombatSession
    {
        private long _leapSwing = -1;
        private double _leapStartX;

        /// <summary>脚本前冲在空中摆过位置的步数(检查用)。</summary>
        public int LeapSteps { get; private set; }

        /// <summary>
        /// 每一步开头(命中判定之前)对放着脚本前冲的主角办的事:换了一击(<see cref="TownHero.SwingNumber"/> 变了)就记下这一段开头的横坐标(客户端进入块 0x00cd4e58 记的起点),
        /// 还在空中就把横坐标摆到 <see cref="HeroSkills.TryLeapX"/> 算出的位置;落地后不再动。
        /// </summary>
        private void StepLeap(TownHero hero)
        {
            if (Skills == null || hero.Motion != HeroMotion.Skill)
            {
                _leapSwing = -1;
                return;
            }
            if (hero.SwingNumber != _leapSwing)
            {
                _leapSwing = hero.SwingNumber;
                _leapStartX = hero.X;
            }
            if (Skills.TryLeapX(hero, _leapStartX, out double x))
            {
                MoveHeroTo(hero, x);
                LeapSteps++;
            }
        }
    }
}
