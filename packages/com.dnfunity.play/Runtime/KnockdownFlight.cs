using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 倒地动画的"形状":延时超过 <see cref="MonsterTuning.HoldFrameThresholdMs"/> 的长停留帧(原版的"停到逻辑放行为止"的帧)与躺着的那一帧。
    /// 原版把这些帧序号写在各派生类初始化函数里(五个整数:阶段 0 与阶段 1 在最高点换帧的两对记录、阶段 2 的躺地帧;
    /// 鬼剑士一族的初始化函数写的是 2、2、2、3、4,按"躺地帧是最后一个长停留帧的下一帧"推出来的帧号与主角的倒地动画核对是一致的,证据 E-105),
    /// 这里按动画的结构推出来,怪物与主角共用(证据 E-082、E-083、E-105)。
    /// </summary>
    public sealed class DownAnimation
    {
        private readonly AniTimeline _timeline;
        private readonly int[] _holds;

        /// <param name="ani">倒地动画;为 null 表示没有(所有查询返回"没有")。</param>
        public DownAnimation(AniFile ani)
        {
            Ani = ani;
            if (ani == null)
            {
                _holds = new int[0];
                return;
            }
            _timeline = new AniTimeline(ani);
            var list = new List<int>();
            for (int i = 0; i < ani.Frames.Length; i++)
            {
                if (ani.Frames[i].TryGetDelayMs(out int ms) && ms > MonsterTuning.HoldFrameThresholdMs)
                {
                    list.Add(i);
                }
            }
            _holds = list.ToArray();
        }

        public AniFile Ani { get; }

        public bool HasAnimation => _timeline != null;

        /// <summary>长停留帧的帧号(从小到大)。</summary>
        public IReadOnlyList<int> HoldFrames => _holds;

        /// <summary>动画的名义总时长(毫秒,长停留帧按它自己的延时算);没有动画是 0。</summary>
        public long TotalMs => _timeline == null ? 0 : _timeline.TotalMilliseconds;

        /// <summary>
        /// 躺着的那一帧:最后一个长停留帧的下一帧(原版是各派生类的立即数,E-082;看过的两个类是 2、2、2、3 与 2、2、2、3、4——后者就是鬼剑士一族,
        /// 按这个结构推出的帧号与主角的倒地动画核对是一致的,E-105)。没有长停留帧返回 -1;后面没有帧时取最后一帧。
        /// </summary>
        public int LyingFrame
        {
            get
            {
                if (_holds.Length == 0 || _timeline == null)
                {
                    return -1;
                }
                return Math.Min(_holds[_holds.Length - 1] + 1, Ani.Frames.Length - 1);
            }
        }

        public bool IsHold(int frame)
        {
            return Array.IndexOf(_holds, frame) >= 0;
        }

        /// <summary>第 <paramref name="frame"/> 帧在动画里的起点(毫秒)。</summary>
        public double StartMs(int frame)
        {
            return _timeline == null ? 0 : _timeline.StartMs(frame);
        }

        /// <summary>动画时钟 <paramref name="ms"/> 毫秒时的帧号;没有动画是 -1。</summary>
        public int FrameAt(double ms)
        {
            return _timeline == null ? -1 : _timeline.FrameAt((long)ms);
        }
    }

    /// <summary>
    /// 倒地(DOWN)的运动与动画时钟:基类状态函数的实现,怪物与主角共用同一套(证据 E-082、E-083、E-085、E-105):
    /// 三个阶段(0 首次腾空、1 落地后颠一下、2 躺着),靠"再请求一次 DOWN"串起来;阶段 0、1 按重力走 z 抛物线、水平匀速,
    /// 落地后按 <see cref="Knockdown.AfterLanding"/> 再请求;阶段 2 躺着,动画放完起身。
    /// 位置、朝向由宿主在每次 <see cref="Begin"/>、<see cref="Update"/> 之前用 <see cref="Place"/> 交进来,算完再读回去。
    /// 另有两个宿主设的开关:<see cref="EarlyEnd"/>(只用于主角,E-085:落地或躺着时就地起身)与 <see cref="DieOnLying"/>(已死:躺下的那一刻转入死亡,E-082、E-084)。
    /// </summary>
    public sealed class KnockdownFlight
    {
        private readonly int _weight;
        private readonly DownAnimation _anim;
        private bool _active;
        private int _stage;
        private int _type;
        private double _vz;
        private double _vx;
        private double _maxZ;
        private double _wallX;
        private int _horizontalRecord;
        private int _storedPush;
        private int _storedLift;
        private double _aniMs;
        private double _stage2Ms;
        private int _rollState;
        private bool _ended;
        private bool _died;
        private double _quickStandLeftMs = -1.0;

        /// <param name="weight">体重属性(怪物是 [weight],主角是 .chr 的 [weight]:原版里都是 <c>0x007692b0</c> 读出来的合计体重,证据 E-105)。</param>
        public KnockdownFlight(int weight, DownAnimation animation)
        {
            _weight = weight;
            _anim = animation ?? new DownAnimation(null);
        }

        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public bool FacingLeft { get; private set; }

        /// <summary>正在倒地里(腾空、颠一下、躺着)。</summary>
        public bool Active => _active;

        /// <summary>倒地阶段:0 首次腾空、1 落地后颠一下、2 躺着;不在倒地里是 -1。</summary>
        public int Stage => _active ? _stage : -1;

        /// <summary>最近一次请求的倒地类型(<see cref="Knockdown.TypeOf"/>)。</summary>
        public int Type => _type;

        /// <summary>z 方向的速度(像素/秒,向上为正)。</summary>
        public double VerticalSpeed => _vz;

        /// <summary>水平速度(像素/秒,向右为正)。</summary>
        public double HorizontalSpeed => _vx;

        /// <summary>倒地动画的时钟(毫秒):长停留帧在最高点、落地时才放行,状态切换时还会跳帧(E-082)。</summary>
        public double AniMs => _aniMs;

        public DownAnimation Animation => _anim;

        /// <summary>正在"倒地起身"(引擎状态 0x12,E-140)里:躺着时按了倒地起身技能的指令,倒计时走完就起身。</summary>
        public bool QuickStanding => _active && _quickStandLeftMs >= 0.0;

        /// <summary>倒地起身还剩多少毫秒;不在倒地起身里是 -1。</summary>
        public double QuickStandLeftMs => QuickStanding ? _quickStandLeftMs : -1.0;

        /// <summary>现在能不能倒地起身:只在阶段 2(躺着)、没有已死、还没在起身里(引擎里这个技能只在 DOWN 状态的躺着阶段可用,E-140)。</summary>
        public bool CanQuickStand => _active && _stage == 2 && !DieOnLying && _quickStandLeftMs < 0.0;

        /// <summary>
        /// 倒地起身(E-140,静态读码):躺着时进入状态 0x12,停留 <paramref name="durationMs"/> 毫秒(技能 [static data] 的第 0 项)之后起身站定;
        /// 进入时不换动画(推断:倒地动画照常放),这期间仍算倒地(起身时有没有无敌没有读到,不做;被再次击倒就取消起身)。时长为 0 的话下一步就起身。
        /// 不能起身(<see cref="CanQuickStand"/> 为假)返回 false。
        /// </summary>
        public bool BeginQuickStand(double durationMs)
        {
            if (!CanQuickStand)
            {
                return false;
            }
            _quickStandLeftMs = Math.Max(0.0, durationMs);
            return true;
        }

        /// <summary>从创建起 <see cref="Begin"/> 的次数(包括落地后的再请求);宿主据此知道这一步里状态被"重新开始"过(怪物的阶段计时在每次开始时归零)。</summary>
        public int BeginCount { get; private set; }

        /// <summary>
        /// 提前结束(证据 E-085,只用于主角):为真时,阶段 0、1 落地、或阶段 2 躺着的下一步就地结束倒地(跳过颠一下与躺地);已死(<see cref="DieOnLying"/>)时不起作用。
        /// 谁来置位、什么时候置位由宿主决定(累计伤害超过最大血量的 15%)。
        /// </summary>
        public bool EarlyEnd { get; set; }

        /// <summary>已死:倒地照常走(腾空、颠一下),躺下的那一刻转入死亡(<see cref="TakeDied"/> 取走通知),不再起身(证据 E-082 阶段 2、E-084)。</summary>
        public bool DieOnLying { get; set; }

        /// <summary>宿主把当前的位置、离地高度与朝向交进来(<see cref="Begin"/>、<see cref="Update"/> 之前)。</summary>
        public void Place(double x, double y, double z, bool facingLeft)
        {
            X = x;
            Y = y;
            Z = z;
            FacingLeft = facingLeft;
        }

        /// <summary>落地后再请求用的随机数种子(水平量 ±20%、高度 ±10% 的抖动;宿主在每次被打中时给)。</summary>
        public void SeedRolls(int seed)
        {
            _rollState = seed;
        }

        private int NextRoll()
        {
            _rollState = unchecked(_rollState * 1103515245 + 12345);
            return (int)(((uint)_rollState >> 16) % 1000);
        }

        /// <summary>取走"躺完了、该起身"的通知(落地时提前结束也算)。</summary>
        public bool TakeEnded()
        {
            bool e = _ended;
            _ended = false;
            return e;
        }

        /// <summary>取走"已死的倒地躺下了、转入死亡"的通知。</summary>
        public bool TakeDied()
        {
            bool d = _died;
            _died = false;
            return d;
        }

        /// <summary>
        /// 开始(或再次开始)倒地:算出水平初速与 z 初速(<see cref="Knockdown.Begin"/>),不是接续飞行时转向攻击方,
        /// 动画重新放,阶段 1、2(以及砸地后的再飞)跳到记录的帧(0x008360e0,E-082、E-083)。
        /// <paramref name="pushToLeft"/> 为真:攻击方在受击方的右边、把受击方往左推(受击方转向攻击方,速度背离攻击方)。
        /// 已死(<see cref="DieOnLying"/>)而且这一次请求的阶段已经是躺着时,不再倒地、直接转入死亡(<see cref="TakeDied"/>)。
        /// </summary>
        public void Begin(KnockdownRequest request, int rollHorizontal, int rollHeight, bool pushToLeft)
        {
            BeginCount++;
            bool wasDown = _active;
            int prevStage = wasDown ? _stage : 0;
            int prevType = wasDown ? _type : 0;
            var body = new KnockdownBody(_weight, Knockdown.Gravity, wasDown, prevStage, prevType, Z);
            KnockdownLaunch launch = Knockdown.Begin(request, body, rollHorizontal, rollHeight);
            if (!launch.Continuing)
            {
                // 受击方转向攻击方(推力为负则相反),水平速度沿朝向的反方向(0x008360e0:朝向取自请求第一项,速度方向取它的反向)
                bool faceLeft = !pushToLeft;
                if (launch.FlipSide)
                {
                    faceLeft = !faceLeft;
                }
                FacingLeft = faceLeft;
            }
            _vx = (FacingLeft ? 1.0 : -1.0) * launch.Horizontal;
            _vz = launch.ZSpeed;
            _horizontalRecord = launch.Horizontal;
            _maxZ = 0;
            _wallX = X;
            if (launch.StoresBounce)
            {
                _storedLift = launch.StoredLift;
                _storedPush = launch.StoredPush;
            }
            _stage = launch.Stage;
            _type = launch.Type;
            _stage2Ms = 0;
            _quickStandLeftMs = -1.0;
            int jump = wasDown && (prevType == 2 || prevType == 3) ? 1 : (launch.Stage == 1 || launch.Stage == 2 ? launch.Stage : 0);
            IReadOnlyList<int> holds = _anim.HoldFrames;
            if (jump != 0 && holds.Count > 0)
            {
                // 动画重新放,再跳到记录的帧(原版的帧号是各类的立即数,这里取长停留帧的结构,E-082、E-083)
                _aniMs = _anim.StartMs(jump == 1 ? holds[0] : _anim.LyingFrame);
            }
            else if (!(wasDown && launch.Continuing))
            {
                _aniMs = 0;
            }
            _active = true;
            _ended = false;
            if (DieOnLying && launch.Stage >= 2)
            {
                Die();
            }
        }

        /// <summary>
        /// 死亡(DIE)开始(0x00837612,E-084):水平速度清零、停在刚跳到的躺着那一帧;不再在倒地状态里。
        /// </summary>
        private void Die()
        {
            _vx = 0;
            _vz = 0;
            Z = 0;
            _active = false;
            _died = true;
        }

        /// <summary>
        /// 不再倒地(宿主换了别的表现:已死怪物没有倒地动作、占位的受击等):水平与 z 速度清零、高度落到地面。
        /// <paramref name="keepVelocity"/> 为真时保留速度与高度,让 <see cref="FallWhileDying"/> 带着它们落地。
        /// </summary>
        public void Stop(bool keepVelocity)
        {
            _active = false;
            _ended = false;
            _quickStandLeftMs = -1.0;
            if (!keepVelocity)
            {
                Z = 0;
                _vz = 0;
                _vx = 0;
            }
        }

        /// <summary>
        /// 复活(只用于主角,证据 E-115):取消"已死"标记。还在倒地的飞行里时它照常落地、颠一下、躺着、起身;已经躺平死了(<see cref="Die"/> 之后)就回到阶段 2 躺着,
        /// 躺满 <see cref="MonsterTuning.KnockDownHoldMs"/>(动画放完)再起身。客户端复活时的起身表现没有读到,这是按"躺着的流程接着走完"的推断。
        /// </summary>
        public void Resurrect()
        {
            DieOnLying = false;
            _died = false;
            if (_active)
            {
                return;
            }
            _active = true;
            _ended = false;
            _quickStandLeftMs = -1.0;
            _stage = 2;
            _stage2Ms = 0;
            _vx = 0;
            _vz = 0;
            Z = 0;
            IReadOnlyList<int> holds = _anim.HoldFrames;
            if (_anim.HasAnimation && holds.Count > 0 && _anim.LyingFrame >= 0)
            {
                _aniMs = _anim.StartMs(_anim.LyingFrame);
            }
        }

        /// <summary>
        /// 推进倒地状态机 <paramref name="deltaMs"/> 毫秒:阶段 0、1 按重力走 z 抛物线(与主角跳跃同一个步内精确触地算法),
        /// 水平匀速(没有摩擦:每次落地后的再请求马上改写水平速度,阶段 2 是 0,E-083);落地后按 <see cref="Knockdown.AfterLanding"/> 再请求;
        /// 阶段 2 躺着,动画放完(或提前结束)就起身(<see cref="TakeEnded"/>)。
        /// </summary>
        public void Update(double deltaMs, IWalkArea walk)
        {
            double left = deltaMs / 1000.0;
            for (int guard = 0; guard < 16 && _active && left > 1e-9; guard++)
            {
                if (_stage >= 2)
                {
                    LieDown(left * 1000.0);
                    return;
                }
                double g = Knockdown.Gravity;
                double used = left;
                bool landed = false;
                if (Z <= 0 && _vz <= 0)
                {
                    // 在地面上而且没有向上的速度:这一帧里重力把速度压成负的、位置夹在地面,落地被这一帧的状态函数发现(原版的帧序);
                    // 这一整步水平速度照常走
                    Z = 0;
                    landed = true;
                }
                else
                {
                    double z0 = Z, v0 = _vz;
                    double z1 = z0 + v0 * left - 0.5 * g * left * left;
                    double v1 = v0 - g * left;
                    if (z1 <= 0 && v1 < 0)
                    {
                        double touch = (v0 + Math.Sqrt(v0 * v0 + 2.0 * g * Math.Max(z0, 0.0))) / g;
                        used = Math.Min(left, Math.Max(0.0, touch));
                        Z = 0;
                        _vz = v0 - g * used;
                        landed = true;
                    }
                    else
                    {
                        Z = z1;
                        _vz = v1;
                    }
                }
                if (used > 0 && _vx != 0)
                {
                    MoveDown(_vx * used, walk);
                }
                if (Z > _maxZ)
                {
                    _maxZ = Z;
                }
                AdvanceClock(used * 1000.0);
                if (!landed && _vz <= 0)
                {
                    SyncApexFrame();
                }
                left -= used;
                if (landed)
                {
                    Land();
                }
            }
        }

        /// <summary>落地:提前结束(E-085)就地起身;否则按阶段与类型再请求倒地(弹起类用存下的推力与抬升再飞一次,否则颠一下;阶段 1 落地躺下)。</summary>
        private void Land()
        {
            if (EarlyEnd && !DieOnLying)
            {
                End();
                return;
            }
            KnockdownRequest next = Knockdown.AfterLanding(_stage, _type, _horizontalRecord, (int)Math.Floor(_maxZ), _storedPush, _storedLift);
            Begin(next, NextRoll(), NextRoll(), !FacingLeft);
        }

        /// <summary>"结束"(0x008337f0):不再倒地,水平速度清零,通知宿主起身。</summary>
        private void End()
        {
            _active = false;
            _vx = 0;
            _ended = true;
            _quickStandLeftMs = -1.0;
        }

        /// <summary>阶段 2:躺着不动;动画放完(或没有可放的帧时停够占位时长、或提前结束)就起身。</summary>
        private void LieDown(double ms)
        {
            Z = 0;
            _vz = 0;
            _vx = 0;
            AdvanceClock(ms);
            _stage2Ms += ms;
            if (_quickStandLeftMs >= 0.0)
            {
                // 倒地起身里:只看自己的倒计时(提前起身、躺地占位时长与动画放完都不再起作用)
                _quickStandLeftMs -= ms;
                if (_quickStandLeftMs <= 0.0)
                {
                    End();
                }
                return;
            }
            int lying = _anim.LyingFrame;
            bool stuckOnHold = lying >= 0 && _anim.IsHold(lying);
            bool done = stuckOnHold ? _stage2Ms >= MonsterTuning.KnockDownHoldMs : _aniMs >= _anim.TotalMs;
            if (EarlyEnd && !DieOnLying)
            {
                done = true;
            }
            if (done)
            {
                End();
            }
        }

        /// <summary>动画时钟前进;走进长停留帧就停在它的起点,等逻辑放行(最高点换帧、落地后的跳帧)。</summary>
        private void AdvanceClock(double ms)
        {
            if (!_anim.HasAnimation)
            {
                return;
            }
            _aniMs += ms;
            int frame = _anim.FrameAt(_aniMs);
            if (frame >= 0 && _anim.IsHold(frame))
            {
                _aniMs = _anim.StartMs(frame);
            }
        }

        /// <summary>
        /// 腾空到最高点(z 速度 ≤ 0):阶段 1 的颠起把动画从第一个长停留帧换到第二个(记录帧 0x3300 → 0x330c,E-082);
        /// 阶段 0 的两个记录帧相同(鬼剑士一族与看过的怪物类都是,E-105),不换。
        /// </summary>
        private void SyncApexFrame()
        {
            if (_stage != 1)
            {
                return;
            }
            IReadOnlyList<int> holds = _anim.HoldFrames;
            if (holds.Count < 2)
            {
                return;
            }
            if (_anim.FrameAt(_aniMs) == holds[0])
            {
                _aniMs = _anim.StartMs(holds[1]);
            }
        }

        /// <summary>倒地中的水平位移;撞到可走区域边界:水平速度反向并缩到五分之一(E-082),贴着起飞点撞墙则直接停。</summary>
        private void MoveDown(double dx, IWalkArea walk)
        {
            double nx = X + dx;
            if (walk == null || walk.IsUnrestricted || walk.Contains(nx, Y))
            {
                X = nx;
                return;
            }
            _vx = Math.Abs(X - _wallX) >= 1.0 ? -Math.Truncate(_vx / 5.0) : 0.0;
        }

        /// <summary>在倒地中(含腾空)被打死:带着当前速度落到地上再停,动画停在被打死时的那一帧(宿主先 <see cref="Stop"/>(true))。</summary>
        public void FallWhileDying(double deltaMs, IWalkArea walk)
        {
            if (Z <= 0 && _vz <= 0 && _vx == 0)
            {
                Z = 0;
                return;
            }
            double left = deltaMs / 1000.0;
            double g = Knockdown.Gravity;
            double used = left;
            if (Z > 0 || _vz > 0)
            {
                double z0 = Z, v0 = _vz;
                double z1 = z0 + v0 * left - 0.5 * g * left * left;
                double v1 = v0 - g * left;
                if (z1 <= 0 && v1 < 0)
                {
                    double touch = (v0 + Math.Sqrt(v0 * v0 + 2.0 * g * Math.Max(z0, 0.0))) / g;
                    used = Math.Min(left, Math.Max(0.0, touch));
                    Z = 0;
                    _vz = 0;
                }
                else
                {
                    Z = z1;
                    _vz = v1;
                }
            }
            if (_vx != 0 && used > 0)
            {
                MoveDown(_vx * used, walk);
            }
            if (Z <= 0)
            {
                Z = 0;
                _vz = 0;
                _vx = 0;
            }
        }
    }
}
