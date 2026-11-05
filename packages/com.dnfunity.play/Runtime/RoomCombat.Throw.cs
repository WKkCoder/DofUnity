using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 投掷类技能(客户端状态 0x0d 一族,<see cref="ThrowRecipe"/>)在房间里的运行:放出的那一步起,阶段机(<see cref="ThrowRun"/>)跟着主角自己的时间走
    /// (命中停顿期间不走,与主角的动画一致);阶段 0 开始时放出全部对象,阶段 0 里的霸体挡住受击反应,阶段 1 起关掉;
    /// 主角被打进受击、倒地或死亡时状态被打断,已经放出的对象留在场上(客户端里对象是独立的);时间到了回站立。
    /// </summary>
    public sealed partial class RoomCombatSession
    {
        private ThrowRun _throwRun;
        private HeroSkillSlot _throwSlot;
        private bool _throwObjectsPending;
        private int _throwPosePhase = -1;

        /// <summary>放出过的投掷类技能次数。</summary>
        public int ThrowCasts { get; private set; }

        /// <summary>投掷类技能被打断的次数(受击、倒地、死亡)。</summary>
        public int ThrowCancels { get; private set; }

        /// <summary>有一次投掷类技能正在进行(阶段 0 或阶段 1)。</summary>
        public bool ThrowActive => _throwRun != null;

        /// <summary>正在进行的投掷类技能处于哪个阶段(0 或 1);没有进行中的为 -1。</summary>
        public int ThrowPhase => _throwRun == null ? -1 : _throwRun.Phase;

        /// <summary>
        /// 投掷类技能此刻给主角的霸体档位:带霸体的技能在阶段 0 里为 1(进入块 setSuperArmor(1,1,-1),档位 1 挡不住带 [ignore super armor] 的攻击,E-142),其余为 0。
        /// </summary>
        private int ThrowArmorLevel => _throwRun != null && _throwRun.ArmorActive ? 1 : 0;

        /// <summary>刚放出一个投掷类技能:建阶段机,放对象的事记下来,留到这一步结束时统一放(与别的被动对象一样,新放的对象从下一步起才走)。</summary>
        private void BeginThrowRun(HeroSkillSlot cast)
        {
            _throwSlot = cast;
            _throwRun = cast.NewThrowRun();
            _throwObjectsPending = _throwRun != null;
            _throwPosePhase = 0;
            ThrowCasts++;
        }

        /// <summary>阶段 0 开始那一刻在主角位置放出全部对象(按配方里的顺序,朝向同主角),只放一次。</summary>
        private void SpawnThrowObjects()
        {
            if (!_throwObjectsPending)
            {
                return;
            }
            _throwObjectsPending = false;
            TownHero hero = _room.Hero;
            for (int i = 0; i < _throwSlot.Throw.Objects.Count; i++)
            {
                AddSkillObject(_throwSlot, i, _throwSlot.Throw.Objects[i], hero);
            }
        }

        /// <summary>
        /// 主角自己的时间走了 <paramref name="heroDelta"/> 毫秒:阶段机前进;主角已被打断就收掉;结束就回站立;
        /// 换了阶段或主角的动画先播完回到了站立,就按此刻的阶段重新摆姿势(客户端的状态靠计时结束,不看动画有没有播完,动画停在最后一帧)。
        /// </summary>
        private void StepThrow(double heroDelta)
        {
            if (_throwRun == null)
            {
                return;
            }
            TownHero hero = _room.Hero;
            if (HeroDead || hero.IsHurt || hero.IsDown || _heroDown.Active)
            {
                ThrowCancels++;
                _throwRun = null;
                return;
            }
            _throwRun.Advance(heroDelta);
            if (_throwRun.Finished)
            {
                hero.Place(hero.X, hero.Y, hero.FacingLeft, HeroMotion.Rest, 0);
                _throwRun = null;
                return;
            }
            int phase = _throwRun.Phase;
            if (phase != _throwPosePhase || hero.Motion != HeroMotion.Skill || hero.SkillId != _throwSlot.SkillId)
            {
                _throwPosePhase = phase;
                hero.PlaceSkillMotion(hero.X, hero.Y, hero.FacingLeft, _throwSlot.SkillId, _throwSlot.ThrowPoses[phase], (long)_throwRun.PhaseMs);
            }
        }
    }
}
