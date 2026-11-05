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
    public sealed partial class PassiveObject : IActEntity, IHomingBody
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
        private PassiveHoming _homing;
        private float _rotation;
        private HeroStrikeInfo? _strikeOverride;

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
            // 追踪(M67,E-165):.obj 的 [homing] 块写了 [homing use] 非 0 的,出生就开追踪、定初始方向、取目标(客户端对象初始化 0x00805cd0)
            HomingDef homing = template.Def?.Homing;
            if (homing != null && homing.Use)
            {
                _homing = new PassiveHoming(homing);
                _homing.Start(this, facingLeft, _ageMs, ResolveHomingTarget(homing));
            }
        }

        public PassiveTemplate Template => _template;

        /// <summary>
        /// 此刻打中主角用的攻击信息(M69,E-167):动作脚本 [CHANGE ATTACKINFO] 换上的优先,其次是当前动作 .act 自带的 [DEFAULT ATTACKINFO],
        /// 最后才是 .obj 的 [attack info](没有时全是默认值)。
        /// </summary>
        public HeroStrikeInfo Strike => _strikeOverride ?? _action.DefaultStrike ?? _template.Strike;

        /// <summary>
        /// [CHANGE ATTACKINFO](M69,E-167):换上 <paramref name="strike"/>;null 是 [DEFAULT],清掉脚本换上的、回到当前动作的默认(没有就是 .obj 原本的)。
        /// 换上的跨动作保留,直到再次改换或进入一个自带默认攻击信息的动作。
        /// </summary>
        public void ChangeStrike(HeroStrikeInfo? strike)
        {
            _strikeOverride = strike;
        }

        /// <summary>追踪控制器(.obj 带 [homing] 块、[homing use] 非 0 时有);没有就是 null。</summary>
        public PassiveHoming Homing => _homing;

        /// <summary>[sync animation rotation] 的对象的画面旋转(弧度);没有就是 0。渲染这一版还不画旋转的被动对象。</summary>
        public float RotationRadians => _rotation;

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

        /// <summary>当前速度(世界坐标/秒;[ZSPEED]、[IS END MAP] 用,E-166)。</summary>
        float IActEntity.SpeedX => (float)_vx;

        float IActEntity.SpeedY => (float)_vy;

        float IActEntity.SpeedZ => (float)_vz;

        // 被动对象没有生命(这一版不做可打碎的被动对象):不满足任何 [HP] 条件
        int IActEntity.Hp => 0;

        int IActEntity.HpMax => 0;

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

        void IActEntity.SetDamageBox(ActDamageBoxMode mode)
        {
            // 被动对象没有受击盒(这一版不做可打碎的被动对象)
        }

        void IActEntity.SetFloatingHeight(int height)
        {
            // 被动对象的高度由 Z 方向的速度与重力管,没有"悬停"这个概念(全库被动对象的 .act 里也没有 [CHANGE FLOATING HEIGHT])
        }

        /// <summary>[TELEPORT] 作用到被动对象:直接摆过去,不查可走区(被动对象本来就在可走区之外飞行;推断)。</summary>
        bool IActEntity.Teleport(float x, float y, float z)
        {
            if (Finished)
            {
                return false;
            }
            X = x;
            Y = y;
            Z = Math.Max(0.0, z);
            _motionVersion++;
            return true;
        }

        /// <summary>当前动作动画的总时长([SET POS FORCE] 的起止插值拿它当"全程",E-152)。</summary>
        int IActEntity.MotionDurationMs => (int)_action.Timeline.TotalMilliseconds;

        /// <summary>
        /// [SET POS FORCE] 作用到被动对象([MOVE ME] 由被动对象自己当拥有者时):直接摆过去,不查可走区(与 [TELEPORT] 同一条推断),
        /// 不改版本号(每帧调用)。
        /// </summary>
        void IActEntity.ForcePosition(float x, float y, float z)
        {
            if (Finished)
            {
                return;
            }
            X = x;
            Y = y;
            Z = Math.Max(0.0, z);
        }

        /// <summary>被动对象没有自己的等级:取链条上最早那只怪物的([SUMMON MONSTER] 里 [LEVEL] -1 之类的写法落到创建者的等级,推断,E-134)。</summary>
        int IActEntity.Level => Owner.Level;

        /// <summary>
        /// [SET HP]:被动对象没有生命条;小于等于 0 就销毁它(推断:全库 [SET HP] 的值几乎都是 0,客户端对被动对象直接调 setHp,
        /// 没读这个槽对 0 的处理,E-134),大于 0 什么也不做。
        /// </summary>
        void IActEntity.SetHp(int hp)
        {
            if (hp <= 0)
            {
                Finished = true;
            }
        }

        private Dictionary<int, IActEntity> _savedTargets;

        /// <summary>[SAVE TARGET OBJECT] 的槽表:挂在被动对象身上,切动作(换运行器)也保留(客户端的槽表属于拥有者对象,E-157)。</summary>
        void IActEntity.SetSavedTarget(int slot, IActEntity target)
        {
            (_savedTargets ?? (_savedTargets = new Dictionary<int, IActEntity>()))[slot] = target;
        }

        IActEntity IActEntity.GetSavedTarget(int slot)
        {
            return _savedTargets != null && _savedTargets.TryGetValue(slot, out IActEntity target) ? target : null;
        }

        // ------------------------------------------------------------------ 各动作的触发器开关板(E-158)

        private readonly Dictionary<PassiveAction, bool[]> _boards = new Dictionary<PassiveAction, bool[]>();

        /// <summary>脚本读别的动作的开关([TRIGGER CHECK] [CUSTOM] 等)的次数,以及其中落空的次数(没有这一个动作、没装触发器、序号越界;审计与测试用)。</summary>
        public int BoardReads { get; private set; }

        public int BoardReadMisses { get; private set; }

        /// <summary>脚本写别的动作的开关([TRIGGER] [CUSTOM] 等)的次数,以及其中落空的次数。</summary>
        public int BoardWrites { get; private set; }

        public int BoardWriteMisses { get; private set; }

        /// <summary>
        /// 这个动作的触发器开关板:对象一生一份、换动作也保留(客户端的动作池属于对象,开关字节只有 [TRIGGER] 行为才改,出招式的复位函数不碰它,E-134、E-158),
        /// 第一次用到时取脚本里 [ENABLE] 写的初值(推断:客户端对象出生时就把全部动作的触发器池建好,所以没进过的动作也有初值)。没有触发器的动作没有开关板。
        /// </summary>
        private bool[] BoardOf(PassiveAction action)
        {
            if (action == null || !action.HasTriggers)
            {
                return null;
            }
            if (!_boards.TryGetValue(action, out bool[] board))
            {
                board = ActRunner.NewEnabledState(action.Program);
                _boards.Add(action, board);
            }
            return board;
        }

        /// <summary>
        /// 脚本按动作族点名的动作(客户端:被动对象这一类对象只认 [CUSTOM] a(虚槽 0x450)、[DEFAULT]、[PROC],E-158):
        /// [CUSTOM] a 是 [etc action] 第 a 项,[DEFAULT] 是基础动作(推断:对象字段 +0x130 就是默认动作的池);被动对象没有 [PROC] 动作,
        /// 别的族(出招、站立、行走、坐下、倒地)客户端转不成那个类,取不到池。
        /// </summary>
        private PassiveAction PoolAction(ActTriggerPool pool, int slot)
        {
            switch (pool)
            {
                case ActTriggerPool.Custom:
                    return _template.EtcAction(slot);
                case ActTriggerPool.Default:
                    return _template.Basic;
                default:
                    return null;
            }
        }

        /// <summary>[TRIGGER CHECK] 的读侧:这一族动作上第 <paramref name="trigger"/> 个触发器现在开着吗;取不到池或序号不小于个数返回 null(对象被剔除)。</summary>
        bool? IActEntity.GetActionTrigger(ActTriggerPool pool, int slot, int trigger)
        {
            BoardReads++;
            bool[] board = BoardOf(PoolAction(pool, slot));
            if (board == null || (uint)trigger >= (uint)board.Length)
            {
                BoardReadMisses++;
                return null;
            }
            return board[trigger];
        }

        /// <summary>
        /// [TRIGGER] 的按动作族写法落到被动对象自己的动作上:写那个动作的开关板;写的正是正在跑的这一个动作时(运行器共用同一份数组)
        /// 复位还要清它的次数、单次位与计时(经 <see cref="ActRunner.ApplyTrigger"/>);别的动作此刻没有运行器,下次进入从头开始,复位没有可见的效果。
        /// </summary>
        void IActEntity.SetActionTrigger(ActTriggerPool pool, int slot, int trigger, ActTriggerCommand command)
        {
            PassiveAction action = PoolAction(pool, slot);
            bool[] board = BoardOf(action);
            if (board == null || (uint)trigger >= (uint)board.Length)
            {
                BoardWriteMisses++;
                return;
            }
            BoardWrites++;
            if (action == _action && _runner != null)
            {
                _runner.ApplyTrigger(trigger, command);
                return;
            }
            switch (command)
            {
                case ActTriggerCommand.On:
                    board[trigger] = true;
                    break;
                case ActTriggerCommand.Off:
                    board[trigger] = false;
                    break;
            }
        }

        // ------------------------------------------------------------------ 追踪(E-165)

        /// <summary>[SET USE HOMING]:开关追踪(没有追踪控制器的对象什么都不做)。</summary>
        void IActEntity.SetUseHoming(bool on)
        {
            _homing?.SetOn(on);
        }

        void IHomingBody.SetPlanarVelocity(double vx, double vy)
        {
            _vx = vx;
            _vy = vy;
        }

        void IHomingBody.SetVerticalVelocity(double vz)
        {
            _vz = vz;
            _falling = vz != 0;
        }

        void IHomingBody.MoveTo(double x, double y)
        {
            X = x;
            Y = y;
        }

        void IHomingBody.SetHomingFacing(bool left)
        {
            FacingLeft = left;
        }

        void IHomingBody.SetRotation(float radians)
        {
            _rotation = radians;
        }

        /// <summary>
        /// 出生时取追踪对象(客户端 0x00801150):[ME] 是直接的创建者(没有就是自己);[ENEMY] 是创建者盯着的目标,没有就取主角;
        /// [MONSTER] 编号是场上第一只活着的这个编号的怪物;[CHARACTER] 是主角(这一版只有一个)。取不到返回 null(这样的对象不追)。
        /// </summary>
        private IActEntity ResolveHomingTarget(HomingDef homing)
        {
            switch (homing.Follow)
            {
                case HomingFollow.Me:
                    return Master ?? this;
                case HomingFollow.Enemy:
                {
                    IActEntity enemy = ((IActEntity)this).Target;
                    return enemy != null && enemy.IsAlive ? enemy : FirstResolved(ActSelector.Character, -1);
                }
                case HomingFollow.Monster:
                    return FirstResolved(ActSelector.Monster, homing.FollowParam);
                case HomingFollow.Character:
                    return FirstResolved(ActSelector.Character, -1);
                default:
                    return null;
            }
        }

        /// <summary>让宿主按选择器解析一批对象,取第一个活着的(不是自己的);<paramref name="monsterCode"/> 不小于 0 时只认这个编号的怪物。</summary>
        private IActEntity FirstResolved(ActSelector selector, int monsterCode)
        {
            var found = new List<IActEntity>();
            _host.Resolve(Owner.ActSelf, selector, 0, found);
            foreach (IActEntity e in found)
            {
                if (e == null || !e.IsAlive || ReferenceEquals(e, this))
                {
                    continue;
                }
                if (monsterCode < 0 || (e.Kind == ActEntityKind.Monster && e.ObjectIndex == monsterCode))
                {
                    return e;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ 推进

        private void SwitchAction(PassiveAction next)
        {
            _action = next;
            _actionMs = 0;
            _rectOn = true;
            _hitDone = false;
            if (next.DefaultStrike.HasValue)
            {
                _strikeOverride = null;     // 进入自带默认攻击信息的动作:默认在动作开始时套上,盖掉之前脚本换的(推断,E-167)
            }
            _motionVersion++;
            _runner = next.HasTriggers ? new ActRunner(next.Program, this, _host, BoardOf(next)) : null;
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
            _homing?.Step(this, deltaMs, _ageMs);       // 先按旧速度走一步,再转向(客户端控制器更新的顺序,E-165)
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
        private readonly Func<int> _hp;
        private readonly Func<int> _hpMax;
        private readonly Func<int> _level;
        private readonly Action<ActiveStatusEntry, IActEntity> _status;
        private readonly Func<bool> _grabbable;
        private readonly Action<StatChange, int, IActEntity> _statAdd;
        private readonly Action<StatChange, IActEntity> _statRemove;
        private readonly Func<int, bool> _hasStatus;
        private readonly Func<int> _aiState;

        /// <param name="grabbable">[IS GRABABLE] 要的"此刻能不能被抓"(会话按冰冻、石化等异常状态给,E-156);没给就只看活着。</param>
        /// <param name="statAdd">[APPENDAGE] 落到主角的一项属性改变(会话管附加物集合与速度倍率,E-161);没给就什么都不做。</param>
        /// <param name="statRemove">[DELETE APPENDAGE] 落到主角的一项。</param>
        /// <param name="hasStatus">[IS TARGETACTIVESTATUS] 要的"身上此刻带着异常状态 n"(会话问主角的异常状态集合,E-166);没给就一律没有。</param>
        public HeroActEntity(TownHero hero, Func<bool> alive, Func<int> hp = null, Func<int> hpMax = null, Func<int> level = null,
            Action<ActiveStatusEntry, IActEntity> status = null, Func<bool> grabbable = null,
            Action<StatChange, int, IActEntity> statAdd = null, Action<StatChange, IActEntity> statRemove = null,
            Func<int, bool> hasStatus = null, Func<int> aiState = null, Func<int> targetStateFocus = null)
        {
            _targetStateFocus = targetStateFocus;
            _aiState = aiState;
            _hasStatus = hasStatus;
            _statAdd = statAdd;
            _statRemove = statRemove;
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _alive = alive ?? (() => true);
            _hp = hp ?? (() => 0);
            _hpMax = hpMax ?? (() => 0);
            _level = level ?? (() => 1);
            _status = status;
            _grabbable = grabbable;
        }

        /// <summary>主角身上此刻带着异常状态 <paramref name="status"/> 吗([IS TARGETACTIVESTATUS],E-166);没接状态系统一律没有。</summary>
        public bool HasActiveStatus(int status) => _hasStatus != null && _hasStatus(status);

        /// <summary>OperatingValue 状态号([IS TARGET STATE] 的 getState,E-172);没给就 0。</summary>
        public int ActAiState => _aiState != null ? _aiState() : 0;

        private readonly Func<int> _targetStateFocus;

        /// <summary>主角 +0x36b0(0x10d92c7);没接回调时 -1,走 getState 与 -1 比较(E-172)。</summary>
        public int ActTargetStateFocus => _targetStateFocus != null ? _targetStateFocus() : -1;

        /// <summary>已废弃(E-172):客户端不替 param -1 换怪物侧 hint。</summary>
        public int ActTargetStateHint => -1;

        /// <summary>主角此刻能不能被抓(E-156):活着,且没有被冰冻或石化(客户端基类 0x0081ecb0 的两项状态检查;其余检查没读出来,推断)。</summary>
        public bool IsGrabbable => IsAlive && (_grabbable == null || _grabbable());

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

        public int Hp => _hp();

        public int HpMax => _hpMax();

        /// <summary>主角的等级(召唤物按"召唤者的等级加偏移"定等级时,召唤者是主角的写法也能算,E-134)。</summary>
        public int Level => _level();

        /// <summary>[SET HP] 作用到主角:不执行(这一版脚本对主角的作用都不执行,与设速度、切动作一致)。</summary>
        public void SetHp(int hp)
        {
        }

        /// <summary>
        /// [ACTIVE STATUS] 作用到主角(E-144):交给会话的状态系统,走和 .atk 状态槽同一条规则(概率与等级差修正、抗性、时长、持续伤害、失去控制);没装状态系统(没有生命条的模式)什么都不做。
        /// </summary>
        public void ApplyStatus(ActiveStatusEntry entry, IActEntity source)
        {
            _status?.Invoke(entry, source);
        }

        /// <summary>[APPENDAGE] 作用到主角(E-161):交给会话,它管主角的附加物集合与速度倍率。</summary>
        public void AddStatAppendage(StatChange change, int durationMs, IActEntity source)
        {
            _statAdd?.Invoke(change, durationMs, source);
        }

        /// <summary>[DELETE APPENDAGE] 作用到主角(E-161)。</summary>
        public void RemoveStatAppendage(StatChange change, IActEntity source)
        {
            _statRemove?.Invoke(change, source);
        }

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

        public void SetDamageBox(ActDamageBoxMode mode)
        {
        }

        public void SetFloatingHeight(int height)
        {
        }

        /// <summary>
        /// [TELEPORT] 作用到主角(怪物把主角拉到自己身边之类的写法,[MOVE TARGET]):落点在可走区里就直接摆,不在就找 <see cref="Placement.SearchRadius"/> 像素内最近的能站的点,找不到不动。
        /// 只改 X、Y(高度与动作不动:主角的腾空与硬直状态机各管各的)。
        /// </summary>
        public bool Teleport(float x, float y, float z)
        {
            IWalkArea walk = _hero.Walk;
            if (!Placement.TryResolve((px, py) => walk == null || walk.IsUnrestricted || walk.Contains(px, py), x, y, out double rx, out double ry))
            {
                return false;
            }
            _hero.Teleport(rx, ry);
            return true;
        }

        /// <summary>
        /// [SET POS FORCE] 作用到主角(怪物把主角抓住拖到身边之类,E-152):落点在可走区里就把 X、Y 摆过去,不在就不动(客户端不合法时只改高度,
        /// 而主角的高度归腾空状态机管,这一版不动它;主角被脚本强行抬到半空、藏到画面外的写法没做)。每帧调用,和 <see cref="Teleport"/> 一样不动作与朝向。
        /// </summary>
        public void ForcePosition(float x, float y, float z)
        {
            IWalkArea walk = _hero.Walk;
            if (walk == null || walk.IsUnrestricted || walk.Contains(x, y))
            {
                _hero.Teleport(x, y);
            }
        }
    }
}
