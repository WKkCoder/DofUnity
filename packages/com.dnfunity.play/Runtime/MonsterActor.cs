using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    public enum MonsterPhase
    {
        /// <summary>站着(待机动作)。</summary>
        Wait,

        /// <summary>朝主角走。</summary>
        Walk,

        /// <summary>出招(.mob 的第一个攻击动作);目前不伤害主角。</summary>
        Attack,

        /// <summary>受击硬直(受击动作一或二,被击倒时是倒地动作)。</summary>
        Hurt,

        /// <summary>死亡:播倒地动作,<see cref="MonsterTuning.DeathMs"/> 之后消失。</summary>
        Dying,

        /// <summary>已经消失。</summary>
        Gone,
    }

    /// <summary>
    /// 怪物行为里我们自己的近似值。除了生命值(取自 <see cref="MonsterAbility"/>,与原版一致)和动画帧(取自 .mob 的动作脚本),
    /// 这里的数都没有在原版上量过:视野、攻击距离、受击后的停留与击退距离怎么换算,是按基线录像看上去的样子取的,证据 E-069 里逐条标了"未量"。
    /// </summary>
    public static class MonsterTuning
    {
        /// <summary>竖直速度与水平速度之比(90 / 222.5,证据 E-066)。</summary>
        public const double VerticalRatio = 90.0 / 222.5;

        /// <summary>主角进入这个范围(世界像素)怪物才有反应;原版 .ai 里的视野数没有接入,这是近似。</summary>
        public const double SightX = 320;

        public const double SightY = 160;

        /// <summary>进入这个距离就停下出招(未量)。</summary>
        public const double AttackRangeX = 70;

        public const double AttackRangeY = 16;

        /// <summary>两次出招之间的间隔缺省值:.mob 没有 [attack delay] 时用。</summary>
        public const int DefaultAttackDelayMs = 1500;

        /// <summary>受击硬直的下限:受击动画有真实的(较短的)时长时,硬直不会比它更短(未量)。</summary>
        public const double MinHurtMs = 280;

        /// <summary>
        /// 动画总时长超过这个数,就认为它含有"停到游戏逻辑结束为止"的帧(原版的受击动画只有一帧,延时 10000 毫秒,倒地动画的中间两帧同理:
        /// 停多久不在 .ani 里)。这时硬直时长改用下面的取值。
        /// </summary>
        public const double HoldFrameThresholdMs = 3000;

        /// <summary>
        /// 普通受击硬直的时长(命中停顿之后才开始计,见 <see cref="MonsterActor.Pause"/>)。量法:基线连拍 S07 里两只一级地精各挨一下,
        /// 受击帧分别连续出现 4 次与 3 次(间隔 100 毫秒),之后恢复站立,所以受击姿势在画面上的总时长在 300 到 500 毫秒之间(分辨率 100 毫秒);
        /// 这个总时长里已经含着主角武器的那一次命中停顿(E-077),所以这里取区间偏下的 350,停顿在它之前另外计。
        /// 只有两个样本,没有算上 [hit recovery] 与攻击信息里的 [force hit stun time];换武器时停顿不同,总时长会跟着变(原版的硬直怎么算还没读)。
        /// </summary>
        public const double HurtHoldMs = 350;

        /// <summary>被打倒(.atk 的 [damage reaction] 是倒地)时在地上停的时长(未量,取个看得过去的数)。</summary>
        public const double KnockDownHoldMs = 900;

        /// <summary>
        /// 从被杀到消失的时长。量法:基线连拍 S09 里最后一击后地精约 0.5 秒内消失(白闪加碎片特效,我们没有做特效);只有一个样本。
        /// </summary>
        public const double DeathMs = 500;

        /// <summary>
        /// 击退按"先快后慢"走完:位移比例 = 1 − (1 − t / 时长)²。依据:S07 里一只地精被打后的位置依次相差 46、25、2、0 像素(间隔 100 毫秒),
        /// 即大部分位移在前 200 毫秒里,之后停住。总位移与 [push aside] 的换算没有量(总位移大约 73 像素,但那只地精挨了几下、之前还在走,没法拆开)。
        /// </summary>
        public const double PushEaseMs = 300;

        /// <summary>
        /// 出招动画的播放倍率 = .mob 的 [attack speed] / 1000。依据:基线连拍 S07 里地精的出招从第 0 帧到最后一帧约 1.3 到 1.4 秒,
        /// 与 .ani 的名义总时长除以这个倍率(地精的 [attack speed] 约为 1000 的 0.4 倍)相符;逐帧的停留并不均匀,只有总时长对得上(一个怪物的样本)。
        /// </summary>
        public const double AttackSpeedUnit = 1000.0;
    }

    /// <summary>一个房间里的怪物:位置、朝向、血量,按阶段播放 .mob 里的动作,并给出受击框。</summary>
    public sealed class MonsterActor
    {
        private readonly MonsterModel _model;
        private readonly Dictionary<AniFile, AniTimeline> _timelines = new Dictionary<AniFile, AniTimeline>();
        private double _phaseMs;
        private double _cooldownMs;
        private double _pushTotal;
        private double _pushMs;
        private double _pushDone;
        private int _pushDir;
        private int _hurtCount;
        private AniFile _hurtAni;
        private double _hurtSpanMs;

        public int Index { get; }
        public int Code { get; }
        public RoomMonsterSpawn Spawn { get; }
        public bool IsBoss { get; }
        public int Level { get; }
        public int HpMax { get; }
        public int Hp { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public bool FacingLeft { get; private set; } = true;
        public MonsterPhase Phase { get; private set; }
        public double SpeedX { get; }
        public double SpeedY { get; }
        public int AttackDelayMs { get; }

        /// <summary>出招动画的播放倍率(.mob 的 [attack speed] / 1000;没有该项时为 1)。</summary>
        public double AttackRate { get; }

        /// <summary>这一次受击硬直的总时长(毫秒);不在受击中时是上一次的值。</summary>
        public double HurtSpanMs => _hurtSpanMs;

        /// <summary>命中停顿还剩多少毫秒(证据 E-077):停顿期间动画、行走、出招、击退、冷却的时间都不走。</summary>
        public double PauseMs { get; private set; }

        public bool IsAlive => Hp > 0;
        public bool IsGone => Phase == MonsterPhase.Gone;

        /// <summary>当前阶段已经过去的毫秒数。</summary>
        public long PhaseMs => (long)_phaseMs;

        /// <param name="speedX">水平行走速度,像素/秒(房间里由 <see cref="RoomCombatSession.Create"/> 把怪物的 [move speed] 按主角的速度比例换算,同一比例套在怪物上是假设)。</param>
        public MonsterActor(int index, RoomMonsterSpawn spawn, MonsterModel model, int level, int hpMax, double speedX, int attackDelayMs,
            int attackSpeed = 0)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            Index = index;
            Spawn = spawn;
            Code = spawn.Code;
            IsBoss = spawn.IsBoss;
            Level = level;
            HpMax = hpMax < 1 ? 1 : hpMax;
            Hp = HpMax;
            X = spawn.X;
            Y = spawn.Y;
            SpeedX = speedX < 0 ? 0 : speedX;
            SpeedY = SpeedX * MonsterTuning.VerticalRatio;
            AttackDelayMs = attackDelayMs > 0 ? attackDelayMs : MonsterTuning.DefaultAttackDelayMs;
            AttackRate = attackSpeed > 0 ? attackSpeed / MonsterTuning.AttackSpeedUnit : 1.0;
            Phase = MonsterPhase.Wait;
            _cooldownMs = 0;
        }

        /// <summary>当前阶段播放的动画。</summary>
        public AniFile CurrentAni
        {
            get
            {
                AniFile ani;
                switch (Phase)
                {
                    case MonsterPhase.Walk:
                        ani = _model.Move;
                        break;
                    case MonsterPhase.Attack:
                        ani = _model.Attacks.Count > 0 ? _model.Attacks[0] : null;
                        break;
                    case MonsterPhase.Hurt:
                        ani = _hurtAni;
                        break;
                    case MonsterPhase.Dying:
                        ani = _model.Down ?? _model.Damage1;
                        break;
                    default:
                        ani = _model.Waiting;
                        break;
                }
                return ani ?? _model.Waiting ?? _model.Move;
            }
        }

        public string CurrentAnimationPath => _model.PathOf(CurrentAni);

        public int CurrentFrame
        {
            get
            {
                AniFile ani = CurrentAni;
                if (ani == null)
                {
                    return -1;
                }
                double ms = Phase == MonsterPhase.Attack ? _phaseMs * AttackRate : _phaseMs;
                return Timeline(ani).FrameAt((long)ms);
            }
        }

        private AniTimeline Timeline(AniFile ani)
        {
            if (!_timelines.TryGetValue(ani, out AniTimeline t))
            {
                t = new AniTimeline(ani);
                _timelines[ani] = t;
            }
            return t;
        }

        private double Duration(AniFile ani)
        {
            return ani == null ? 0.0 : Timeline(ani).TotalMilliseconds;
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。<paramref name="heroX"/>、<paramref name="heroY"/> 是主角位置,<paramref name="walk"/> 是怪物能站的地方。
        /// 主角死亡或不存在时传 <paramref name="heroPresent"/> = false,怪物就待机。
        /// </summary>
        public void Update(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk)
        {
            if (Phase == MonsterPhase.Gone)
            {
                return;
            }
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            if (PauseMs > 0)
            {
                // 定住的时间先用掉;这一小步里停顿结束的话,剩下的时间照常推进
                double used = Math.Min(PauseMs, deltaMs);
                PauseMs -= used;
                deltaMs -= used;
                if (deltaMs <= 0)
                {
                    return;
                }
            }
            _phaseMs += deltaMs;
            if (_cooldownMs > 0)
            {
                _cooldownMs -= deltaMs;
            }
            switch (Phase)
            {
                case MonsterPhase.Hurt:
                    ApplyPush(deltaMs, walk);
                    if (_phaseMs >= _hurtSpanMs)
                    {
                        EnterPhase(MonsterPhase.Wait);
                    }
                    break;
                case MonsterPhase.Dying:
                    ApplyPush(deltaMs, walk);
                    if (_phaseMs >= MonsterTuning.DeathMs)
                    {
                        EnterPhase(MonsterPhase.Gone);
                    }
                    break;
                case MonsterPhase.Attack:
                    if (_phaseMs * AttackRate >= Duration(CurrentAni))
                    {
                        _cooldownMs = AttackDelayMs;
                        EnterPhase(MonsterPhase.Wait);
                    }
                    break;
                default:
                    Think(deltaMs, heroX, heroY, heroPresent, walk);
                    break;
            }
        }

        private void EnterPhase(MonsterPhase next)
        {
            Phase = next;
            _phaseMs = 0;
        }

        /// <summary>待机与行走时的决策:够近就出招,在视野里就走过去,否则站着。</summary>
        private void Think(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk)
        {
            double dx = heroX - X;
            double dy = heroY - Y;
            if (!heroPresent || Math.Abs(dx) > MonsterTuning.SightX || Math.Abs(dy) > MonsterTuning.SightY)
            {
                if (Phase != MonsterPhase.Wait)
                {
                    EnterPhase(MonsterPhase.Wait);
                }
                return;
            }
            if (Math.Abs(dx) > 1.0)
            {
                FacingLeft = dx < 0;
            }
            bool near = Math.Abs(dx) <= MonsterTuning.AttackRangeX && Math.Abs(dy) <= MonsterTuning.AttackRangeY;
            if (near)
            {
                if (_cooldownMs <= 0 && _model.Attacks.Count > 0 && _model.Attacks[0] != null)
                {
                    EnterPhase(MonsterPhase.Attack);
                }
                else if (Phase != MonsterPhase.Wait)
                {
                    EnterPhase(MonsterPhase.Wait);
                }
                return;
            }
            if (Phase != MonsterPhase.Walk)
            {
                EnterPhase(MonsterPhase.Walk);
            }
            double seconds = deltaMs / 1000.0;
            double stepX = 0, stepY = 0;
            if (Math.Abs(dx) > MonsterTuning.AttackRangeX * 0.8)
            {
                stepX = Math.Sign(dx) * Math.Min(SpeedX * seconds, Math.Abs(dx));
            }
            if (Math.Abs(dy) > 3.0)
            {
                stepY = Math.Sign(dy) * Math.Min(SpeedY * seconds, Math.Abs(dy));
            }
            TryMove(stepX, stepY, walk);
        }

        private void TryMove(double stepX, double stepY, IWalkArea walk)
        {
            if (stepX != 0 && (walk == null || walk.IsUnrestricted || walk.Contains(X + stepX, Y)))
            {
                X += stepX;
            }
            if (stepY != 0 && (walk == null || walk.IsUnrestricted || walk.Contains(X, Y + stepY)))
            {
                Y += stepY;
            }
        }

        /// <summary>击退:位移比例 1 − (1 − t / <see cref="MonsterTuning.PushEaseMs"/>)²(先快后慢),走到总位移就停。</summary>
        private void ApplyPush(double deltaMs, IWalkArea walk)
        {
            if (_pushDone >= _pushTotal)
            {
                return;
            }
            _pushMs += deltaMs;
            double t = Math.Min(1.0, _pushMs / MonsterTuning.PushEaseMs);
            double target = _pushTotal * (1.0 - (1.0 - t) * (1.0 - t));
            double step = target - _pushDone;
            if (step <= 0)
            {
                return;
            }
            _pushDone = target;
            TryMove(_pushDir * step, 0, walk);
        }

        /// <summary>
        /// 一个受击动画对应的硬直时长:动画自己有真实时长就取它(但不短于下限),含有"停到逻辑结束"的长停留帧时用量到的硬直
        /// (倒地时用倒地的停留)。
        /// </summary>
        private double HurtSpan(AniFile ani, bool knockDown)
        {
            double total = Duration(ani);
            if (total > MonsterTuning.HoldFrameThresholdMs)
            {
                return knockDown ? MonsterTuning.KnockDownHoldMs : MonsterTuning.HurtHoldMs;
            }
            return Math.Max(MonsterTuning.MinHurtMs, total);
        }

        /// <summary>
        /// 挨一下:扣血;血没了就进入死亡,否则进入受击硬直(被击倒时放倒地动作,否则交替放受击动作一、二)。
        /// <paramref name="pushPixels"/> 是水平击退的总像素,往 <paramref name="pushToLeft"/> 的方向,先快后慢地推完(见 <see cref="MonsterTuning.PushEaseMs"/>)。
        /// 连续挨打时硬直与击退都重新计时。返回这一下是否把它打死。
        /// </summary>
        public bool ApplyHit(int damage, bool knockDown, double pushPixels, bool pushToLeft)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone)
            {
                return false;
            }
            Hp = Math.Max(0, Hp - Math.Max(0, damage));
            _pushDir = pushToLeft ? -1 : 1;
            _pushTotal = Math.Max(0.0, pushPixels);
            _pushDone = 0;
            _pushMs = 0;
            if (Hp == 0)
            {
                EnterPhase(MonsterPhase.Dying);
                return true;
            }
            AniFile ani;
            if (knockDown && _model.Down != null)
            {
                ani = _model.Down;
            }
            else
            {
                ani = (_hurtCount++ % 2 == 0 ? _model.Damage1 : _model.Damage2) ?? _model.Damage1 ?? _model.Damage2 ?? _model.Waiting;
                knockDown = false;
            }
            _hurtAni = ani;
            _hurtSpanMs = HurtSpan(ani, knockDown);
            EnterPhase(MonsterPhase.Hurt);
            return false;
        }

        /// <summary>挨打时被定住 <paramref name="ms"/> 毫秒;已经在停顿的取剩余更长的那个(长者胜,不叠加,E-077)。</summary>
        public void Pause(double ms)
        {
            if (ms > PauseMs)
            {
                PauseMs = ms;
            }
        }

        /// <summary>当前帧的受击框(世界坐标)。死亡后的怪物没有受击框。</summary>
        public List<Aabb> DamageBoxes()
        {
            var result = new List<Aabb>();
            if (!IsAlive || Phase == MonsterPhase.Gone || Phase == MonsterPhase.Dying)
            {
                return result;
            }
            AniFile ani = CurrentAni;
            int frame = CurrentFrame;
            if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
            {
                return result;
            }
            int direction = FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(X), wy = (int)Math.Floor(Y);
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Damage)
                {
                    result.Add(Combat.ToWorld(box, wx, wy, 0, direction));
                }
            }
            return result;
        }

        /// <summary>头顶的世界高度:受击框的最高点(没有受击框时用站立动画第 0 帧的);用来摆伤害数字。</summary>
        public int HeadHeight()
        {
            int top = 0;
            AniFile wait = _model.Waiting;
            if (wait != null && wait.Frames.Length > 0)
            {
                foreach (AniBox box in wait.Frames[0].Boxes)
                {
                    if (box.Type == AniBoxType.Damage)
                    {
                        top = Math.Max(top, box.V2 + box.V5);
                    }
                }
            }
            return top;
        }

        /// <summary>这一帧的精灵;当前帧没有图时返回 null。</summary>
        public SceneSprite Sprite(DnfAssets assets, TownProjection projection)
        {
            if (Phase == MonsterPhase.Gone)
            {
                return null;
            }
            AniFile ani = CurrentAni;
            int frame = CurrentFrame;
            if (ani == null || frame < 0)
            {
                return null;
            }
            return MonsterSprites.SpriteAt(assets, ani, _model.PathOf(ani), frame, projection, (int)Math.Floor(X), (int)Math.Floor(Y), 0,
                !FacingLeft, MonsterSprites.ObjectIdBase + Index);
        }
    }
}
