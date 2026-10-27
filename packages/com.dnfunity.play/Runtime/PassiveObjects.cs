using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;

// 证据:E-130。对象的速度、动作切换、攻击框开关由 .act 的行为改写(行为语义取自客户端的行为卡,运行器见 ActRunner);
// 寿命与消失条件取 .obj 的 [object destroy condition](E-036 起读过的两项:动画播完、时限);
// 下面三处是推断:Z 方向的速度才有重力(1500 像素/秒²,与倒地同值)、整个对象一生只打中主角一次(直到脚本 [ATTACKRECT] [RESET])、没有任何消失条件的对象最长活 MaxLifeMs。
namespace Dnf.Play
{
    /// <summary>
    /// 房间里由 .act 脚本放出来的被动对象:位置与朝向、当前动作(基础动作或 [etc action] 的自定义动作)、自己的触发器运行器与速度,
    /// 当前帧的攻击框与主角的受击框相交就算打中。所有者是怪物(<see cref="Owner"/>,算伤害用它的等级与攻击力),
    /// 也可以是另一个被动对象放出来的(<see cref="Master"/> 指向直接的创建者,<see cref="Owner"/> 始终是链条上最早的那只怪物)。
    /// </summary>
    public sealed class PassiveObject : IActEntity
    {
        /// <summary>没有任何消失条件(时限、动画播完)的对象最长的寿命(毫秒):不让漏写条件的对象永远留在房间里(推断)。</summary>
        public const int MaxLifeMs = 12000;

        /// <summary>Z 方向速度的重力(像素/秒²):与倒地、主角跳跃用的引擎地面重力同值(推断:被动对象是否共用这个值没有读到)。</summary>
        public const double Gravity = 1500.0;

        private readonly PassiveTemplate _template;
        private readonly IActHost _host;
        private PassiveAction _action;
        private ActRunner _runner;
        private PassiveAction _deferred;
        private double _ageMs;
        private double _actionMs;
        private double _vx;
        private double _vy;
        private double _vz;
        private bool _falling;
        private bool _setActionPending;
        private bool _rectOn = true;
        private bool _hitDone;
        private int _motionVersion;
        private AnimatedEffect _effect;
        private AniFile _effectAni;
        private bool _effectLeft;
        private double _effectMs;

        public PassiveObject(PassiveTemplate template, IActEntity master, MonsterActor owner, IActHost host, double x, double y, double z, bool facingLeft)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));
            Master = master;
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            X = x;
            Y = y;
            Z = z;
            FacingLeft = facingLeft;
            SwitchAction(template.Basic);
        }

        public PassiveTemplate Template => _template;

        /// <summary>链条上最早的那只怪物:攻击信息打中主角时取它的等级与攻击力。</summary>
        public MonsterActor Owner { get; }

        /// <summary>直接的创建者(怪物或另一个被动对象)。</summary>
        public IActEntity Master { get; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public double Z { get; private set; }

        public bool FacingLeft { get; private set; }

        /// <summary>已经消失(寿命到了、脚本销毁)。</summary>
        public bool Finished { get; private set; }

        public double AgeMs => _ageMs;

        /// <summary>当前动作已经放了多少毫秒(换动作清零)。</summary>
        public double ActionMs => _actionMs;

        /// <summary>这一个对象执行过的行为触发次数(当前动作的运行器的;审计与测试用)。</summary>
        public int TriggersFired => _runner == null ? 0 : _runner.TotalFired;

        public PassiveAction Action => _action;

        public double VelocityX => _vx;

        public double VelocityY => _vy;

        public double VelocityZ => _vz;

        public bool AttackRectOn => _rectOn;

        /// <summary>当前动作的当前帧(动画从这个动作开始那一刻起放;不循环的停在最后一帧)。</summary>
        public int CurrentFrame => _action.Timeline.FrameAt((long)_actionMs);

        /// <summary>当前动作的动画播完了(循环的动画永远不算播完)。</summary>
        public bool AnimationEnded => !_action.Timeline.Loops && _actionMs >= _action.Timeline.TotalMilliseconds;

        // ------------------------------------------------------------------ IActEntity

        ActEntityKind IActEntity.Kind => ActEntityKind.Passive;

        int IActEntity.ObjectIndex => _template.Index;

        float IActEntity.X => (float)X;

        float IActEntity.Y => (float)Y;

        float IActEntity.Z => (float)Z;

        bool IActEntity.IsAlive => !Finished;

        /// <summary>被动对象盯着创建者盯着的目标(主角)。</summary>
        IActEntity IActEntity.Target => Master?.Target;

        int IActEntity.MotionVersion => _motionVersion;

        void IActEntity.SetFacing(bool left)
        {
            FacingLeft = left;
        }

        void IActEntity.SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
            if (hasX)
            {
                _vx = vx;
            }
            if (hasY)
            {
                _vy = vy;
            }
            if (hasZ)
            {
                _vz = vz;
                _falling = vz != 0;     // 给了 Z 方向的速度才受重力,落到地面就停(推断)
            }
        }

        void IActEntity.SetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
            PassiveAction target;
            switch (kind)
            {
                case ActActionKind.Custom:
                    target = _template.Etc(index);
                    break;
                case ActActionKind.Stand:
                case ActActionKind.Default:
                    target = _template.Basic;
                    break;
                default:
                    return;     // 被动对象没有走、受击、倒地这些动作
            }
            if (target == null)
            {
                return;
            }
            if (atEnd && !now)
            {
                _deferred = target;
                return;
            }
            _deferred = null;
            SwitchAction(target);
        }

        void IActEntity.Destroy(bool force)
        {
            Finished = true;
        }

        void IActEntity.AttackRect(ActAttackRectMode mode)
        {
            switch (mode)
            {
                case ActAttackRectMode.On:
                    _rectOn = true;
                    break;
                case ActAttackRectMode.Off:
                    _rectOn = false;
                    break;
                default:
                    _hitDone = false;     // [RESET]:再次允许打中
                    break;
            }
        }

        void IActEntity.SetFrame(int frame)
        {
            AniTimeline timeline = _action.Timeline;
            int f = Math.Max(0, Math.Min(frame, timeline.FrameCount - 1));
            _actionMs = timeline.StartMs(f);
            _motionVersion++;
        }

        // ------------------------------------------------------------------ 推进

        private void SwitchAction(PassiveAction next)
        {
            _action = next;
            _actionMs = 0;
            _rectOn = true;
            _hitDone = false;
            _motionVersion++;
            _runner = next.HasTriggers ? new ActRunner(next.Program, this, _host) : null;
            _setActionPending = _runner != null;
        }

        /// <summary>推进 <paramref name="deltaMs"/> 毫秒:先让触发器跑(含刚切动作时的 [ON SET ACTION]),再按速度挪位置,最后判断消失。</summary>
        public void Update(double deltaMs)
        {
            if (Finished || deltaMs <= 0)
            {
                return;
            }
            _ageMs += deltaMs;
            _actionMs += deltaMs;
            ActRunner runner = _runner;
            if (runner != null)
            {
                if (_setActionPending)
                {
                    _setActionPending = false;
                    runner.OnSetAction();
                }
                if (!Finished && _runner == runner)
                {
                    runner.Update(CurrentFrame, (float)(deltaMs / 1000.0));
                }
            }
            if (Finished)
            {
                return;
            }
            Move(deltaMs / 1000.0);
            CheckLife();
        }

        private void Move(double seconds)
        {
            X += _vx * seconds;
            Y += _vy * seconds;
            if (_falling)
            {
                Z += _vz * seconds;
                _vz -= Gravity * seconds;
                if (Z <= 0)
                {
                    Z = 0;
                    _vz = 0;
                    _falling = false;
                }
            }
        }

        private void CheckLife()
        {
            bool ended = AnimationEnded;
            if (_deferred != null && ended)
            {
                PassiveAction next = _deferred;
                _deferred = null;
                SwitchAction(next);
                return;
            }
            bool bounded = false;
            if (_template.TimeLimitMs > 0)
            {
                bounded = true;
                if (_ageMs >= _template.TimeLimitMs)
                {
                    Finished = true;
                    return;
                }
            }
            if (_template.DestroyOnAnimationEnd)
            {
                bounded = true;
                if (ended)
                {
                    Finished = true;
                    return;
                }
            }
            if (!bounded && _ageMs >= MaxLifeMs)
            {
                Finished = true;
            }
        }

        // ------------------------------------------------------------------ 命中

        /// <summary>
        /// 当前帧的攻击框(世界坐标,类型 15,跟着朝向镜像、跟着高度抬升)。已经消失、攻击框被脚本关了、这一轮已经打中过(没有 [RESET])、
        /// 或这一帧没有攻击框时返回空表。
        /// </summary>
        public List<Aabb> AttackBoxes()
        {
            var result = new List<Aabb>();
            if (Finished || !_rectOn || _hitDone || _action.Ani == null)
            {
                return result;
            }
            int frame = CurrentFrame;
            AniFile ani = _action.Ani;
            if ((uint)frame >= (uint)ani.Frames.Length)
            {
                return result;
            }
            int direction = FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(X), wy = (int)Math.Floor(Y), wz = (int)Math.Floor(Z);
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Attack)
                {
                    result.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
            return result;
        }

        /// <summary>打中了主角:这一轮不再打(直到脚本 [ATTACKRECT] [RESET]),并唤醒带 [ON ATTACKSUCCESS] 的触发器。</summary>
        public void NotifyHit(IActEntity victim)
        {
            _hitDone = true;
            _runner?.OnAttackSuccess(victim);
        }

        // ------------------------------------------------------------------ 绘制

        /// <summary>此刻的精灵;当前帧没有图或已经消失时返回 null。换了动作、朝向变了或脚本把动画拨回去时重新建特效。</summary>
        public SceneSprite Sprite(DnfAssets assets, TownProjection projection)
        {
            if (Finished || _action.Ani == null)
            {
                return null;
            }
            if (_effect == null || _effectAni != _action.Ani || _effectLeft != FacingLeft || _actionMs < _effectMs)
            {
                _effect = AnimatedEffect.CreateFromAni(_action.Ani, _action.AniPath, X, Y, Z, FacingLeft, 1, true);
                _effectAni = _action.Ani;
                _effectLeft = FacingLeft;
                _effectMs = 0;
                if (_effect == null)
                {
                    return null;
                }
            }
            _effect.Advance(_actionMs - _effectMs);
            _effectMs = _actionMs;
            _effect.MoveTo(X, Y, Z);
            return _effect.Sprite(assets, projection);
        }
    }

    /// <summary>
    /// 主角在 .act 脚本眼里的样子:位置、朝向与是否活着取自 <see cref="TownHero"/>;脚本对主角的"作用"(设速度、切动作、销毁……)一律不执行
    /// (这一版只让怪物与被动对象互相作用,主角被抓取、被推走之类的行为没做)。
    /// </summary>
    public sealed class HeroActEntity : IActEntity
    {
        private readonly TownHero _hero;
        private readonly Func<bool> _alive;

        public HeroActEntity(TownHero hero, Func<bool> alive)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _alive = alive ?? (() => true);
        }

        public ActEntityKind Kind => ActEntityKind.Character;

        public int ObjectIndex => 0;

        public float X => (float)_hero.X;

        public float Y => (float)_hero.Y;

        public float Z => (float)_hero.Z;

        public bool FacingLeft => _hero.FacingLeft;

        public bool IsAlive => _alive();

        public IActEntity Target => null;

        public IActEntity Master => null;

        public int MotionVersion => 0;

        public void SetFacing(bool left)
        {
        }

        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
        }

        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
        }

        public void Destroy(bool force)
        {
        }

        public void AttackRect(ActAttackRectMode mode)
        {
        }

        public void SetFrame(int frame)
        {
        }
    }
}
