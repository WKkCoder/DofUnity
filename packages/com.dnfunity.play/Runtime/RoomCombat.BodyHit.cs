using System;
using Dnf.Render;

namespace Dnf.Play
{
    // 身体命中一族(状态 0x3c、0x3e、0x3f)里要看每一步时间与位置的几条规则:定间隔重武装、关键帧起滑、线性位移。
    // 渲染层的 TownHero 只会在一段开头起滑、整段匀变速,没有"中途起滑"与"按时间摆位置"的接口,所以这两样放在会话里,用公开的 Teleport 摆位置、可走区自己查。
    public sealed partial class RoomCombatSession
    {
        /// <summary>定间隔计时器响过的总次数(检查用)。</summary>
        public int BodyRearms { get; private set; }

        /// <summary>关键帧起滑起过的总次数(检查用)。</summary>
        public int BodySlides { get; private set; }

        private long _bodySwing = -1;
        private SkillRearmClock _bodyRearm;
        private double _bodyStartX;
        private double _bodySlideV0;
        private double _bodySlideAccel;
        private long _bodySlideStartMs = -1;
        private long _bodySlideLastMs;
        private int _bodyHeldX;

        /// <summary>
        /// 每一步开头(命中判定之前)对放着身体命中一族配方的主角办的事:换了一击就重新记起点与计时器,
        /// 计时器到点清命中名单,按时间摆线性位移,推进关键帧起滑。
        /// </summary>
        private void StepBodyHit(TownHero hero)
        {
            HeroSkillSlot slot = SyncBodyHit(hero);
            if (slot == null)
            {
                return;
            }
            SkillBodyHitSpec body = slot.Recipe.BodyHit;
            if (_bodyRearm != null && _bodyRearm.Poll(hero.MotionMs))
            {
                hero.RearmHits();
                BodyRearms++;
            }
            SkillTravelSpec travel = body.TravelOf(hero.SkillStage);
            if (travel != null)
            {
                int offset = SkillBodyHitRules.TravelOffset(slot.Def.GetStatic(travel.DistanceStaticIndex, false), slot.Def.GetStatic(travel.DurationStaticIndex, false), hero.MotionMs);
                MoveHeroTo(hero, _bodyStartX + (hero.FacingLeft ? -offset : offset));
            }
            if (_bodySlideStartMs >= 0 && hero.MotionMs > _bodySlideLastMs)
            {
                double distance = TownHero.SlideDistance(_bodySlideV0, _bodySlideAccel, _bodySlideLastMs - _bodySlideStartMs, hero.MotionMs - _bodySlideStartMs);
                _bodySlideLastMs = hero.MotionMs;
                if (distance != 0.0)
                {
                    MoveHeroTo(hero, hero.X + (hero.FacingLeft ? -distance : distance));
                }
            }
        }

        /// <summary>
        /// 对上主角此刻放的身体命中一族配方并同步"这一击"的状态;不是这一族返回 null(并清掉残留)。换了一击(<see cref="TownHero.SwingNumber"/> 变了)就重新取段起点、
        /// 停掉上一段的起滑、按这一段的配置造重武装计时器。可以重复调用,同一击里只办一次。
        /// </summary>
        private HeroSkillSlot SyncBodyHit(TownHero hero)
        {
            HeroSkillSlot slot = hero.Motion == HeroMotion.Skill && Skills != null ? Skills.SlotOfSkill(hero.SkillId) : null;
            if (slot?.Recipe?.BodyHit == null || slot.Def == null)
            {
                _bodySwing = -1;
                _bodyRearm = null;
                _bodySlideStartMs = -1;
                return null;
            }
            if (hero.SwingNumber != _bodySwing)
            {
                _bodySwing = hero.SwingNumber;
                _bodyStartX = hero.X;
                _bodySlideStartMs = -1;
                _bodyRearm = null;
                SkillRearmSpec rearm = slot.Recipe.BodyHit.RearmOf(hero.SkillStage);
                if (rearm != null)
                {
                    int motion = slot.Recipe.Stages[hero.SkillStage].MotionIndex;
                    int animationMs = 0;
                    for (int frame = 0; frame < rearm.StartFrame; frame++)
                    {
                        animationMs += hero.EtcFrameDelayMs(motion, frame);
                    }
                    _bodyRearm = new SkillRearmClock(SkillBodyHitRules.FrameStartWallMs(animationMs, hero.AttackAnimationRate),
                        slot.Def.GetStatic(rearm.IntervalStaticIndex, false), slot.Def.GetStatic(rearm.CountStaticIndex, false));
                }
            }
            return slot;
        }

        /// <summary>
        /// 这一步主角动画走到的关键帧标志里有配方登记了"起滑"的,按配方起一段滑行(没按反方向时):速度与加速度取 [static data],朝向前方,从此刻的状态计时器算起。
        /// 在 <see cref="ApplyKeyFlags"/> 消费标志之前调用。
        /// </summary>
        private void BodyKeyFlags(TownHero hero)
        {
            HeroSkillSlot slot = SyncBodyHit(hero);
            if (slot == null)
            {
                return;
            }
            foreach (SkillKeyFlag key in hero.KeyFlagsThisStep)
            {
                SkillScriptSlide slide = key.Stage == hero.SkillStage ? slot.Recipe.BodyHit.ScriptSlideAt(key.Stage, key.Flag) : null;
                if (slide == null || !SkillBodyHitRules.ScriptSlideStarts(_bodyHeldX, hero.FacingLeft))
                {
                    continue;
                }
                _bodySlideV0 = slot.Def.GetStatic(slide.SpeedStaticIndex, false);
                _bodySlideAccel = slot.Def.GetStatic(slide.AccelStaticIndex, false);
                _bodySlideStartMs = hero.MotionMs;
                _bodySlideLastMs = hero.MotionMs;
                BodySlides++;
            }
        }

        /// <summary>
        /// 把主角的 X 往 <paramref name="targetX"/> 挪,一次最多一像素,出了可走区就停在区内最后一个点(只查可走区,不查出口门体等阻挡;
        /// 渲染层带碰撞的 <c>MoveAxis</c> 是私有的)。位置没变不调 <see cref="TownHero.Teleport"/>。
        /// </summary>
        private static void MoveHeroTo(TownHero hero, double targetX)
        {
            IWalkArea walk = hero.Walk;
            double x = hero.X;
            double sign = targetX < x ? -1.0 : 1.0;
            while ((targetX - x) * sign > 0.0)
            {
                double next = Math.Abs(targetX - x) < 1.0 ? targetX : x + sign;
                if (walk != null && !walk.IsUnrestricted && !walk.Contains(next, hero.Y))
                {
                    break;
                }
                x = next;
            }
            if (x != hero.X)
            {
                hero.Teleport(x, hero.Y);
            }
        }
    }
}
