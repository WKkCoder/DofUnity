using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>世界坐标里的矩形(x、y 是最小角,宽、高为正)。</summary>
    public readonly struct WorldRect
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public WorldRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>闭区间:边界上的点算在里面(主角能走到 [virtual movable area] 的左边界,证据 E-066)。</summary>
        public bool Contains(double x, double y)
        {
            return x >= X && x <= X + Width && y >= Y && y <= Y + Height;
        }
    }

    /// <summary>主角能站的地方。城镇用矩形的并集([virtual movable area]),副本房间用瓦片通行表(<see cref="RoomMap"/>)。</summary>
    public interface IWalkArea
    {
        /// <summary>为 true 时不限制移动(例如地图没有写可走区域)。</summary>
        bool IsUnrestricted { get; }

        bool Contains(double x, double y);
    }

    /// <summary>矩形并集(闭区间)构成的可走区域;没有矩形时不限制。</summary>
    public sealed class RectUnionArea : IWalkArea
    {
        private readonly IReadOnlyList<WorldRect> _rects;

        public RectUnionArea(IReadOnlyList<WorldRect> rects)
        {
            _rects = rects ?? new List<WorldRect>();
        }

        public bool IsUnrestricted => _rects.Count == 0;

        public bool Contains(double x, double y)
        {
            foreach (WorldRect r in _rects)
            {
                if (r.Contains(x, y))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>副本房间的可走区域:房间所有瓦片的通行表(证据 E-067),再扣掉不可穿过的障碍物的占地(证据 E-068,占地的取法是假设)。</summary>
    public sealed class RoomWalkArea : IWalkArea
    {
        private readonly RoomMap _map;
        private readonly IReadOnlyList<RoomObject> _obstacles;

        public RoomWalkArea(RoomMap map, IReadOnlyList<RoomObject> obstacles = null)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _obstacles = obstacles ?? new List<RoomObject>();
        }

        public bool IsUnrestricted => false;

        public bool Contains(double x, double y)
        {
            if (!_map.CanWalk(x, y))
            {
                return false;
            }
            foreach (RoomObject o in _obstacles)
            {
                if (o.Blocks(x, y))
                {
                    return false;
                }
            }
            return true;
        }
    }

    public enum HeroMotion
    {
        /// <summary>站立(城镇里用 [rest motion],不是战斗用的 [waiting motion],证据 E-065)。</summary>
        Rest,

        /// <summary>行走([move motion])。</summary>
        Move,

        /// <summary>普通攻击连招里的一击(.chr 的 [attack motion] 第 <see cref="TownHero.AttackStage"/> 项,证据 E-069)。</summary>
        Attack,

        /// <summary>奔跑([dash motion]):在地面上双击左或右方向键进入,按住方向键保持,松开就停(证据 E-072;双击的时间窗没有量过)。</summary>
        Dash,

        /// <summary>奔跑中挥击([dashattack motion]):向前滑出一段,挥完回到站立。</summary>
        DashAttack,

        /// <summary>跳跃([jump motion]):腾空,空中可以改变水平与深度方向,落地后回到站立。</summary>
        Jump,

        /// <summary>空中挥击([jumpattack motion]):每次跳跃一击,落地即结束。</summary>
        JumpAttack,
    }

    /// <summary>当前这一击属于哪一类:连招里的普攻、奔跑攻击、跳跃攻击。</summary>
    public enum HeroAttackKind
    {
        Normal,
        Dash,
        Jump,
    }

    /// <summary>主角的障碍物(出口的门框与墙):给出主角占位盒中心在 (x, y) 时压到的障碍(没有则 null),调用方用返回值判断顶着的是哪一个。</summary>
    public interface IHeroBlocker
    {
        object BlockedBy(double x, double y);
    }

    /// <summary>
    /// 城镇里的镜头(证据 E-066):死区跟随。主角在屏幕上 x 位于 [中心 − 半宽, 中心 + 半宽](默认 350 到 450)时镜头不动;
    /// 走出这个窗口,镜头就跟着走,把主角留在窗口边缘;镜头 x 再夹在 [最小, 最大] 之间(到头后主角才继续往那边走出窗口)。
    /// 依据两组原版测量(赛丽亚房间 <c>map/elvengard/(r)gate.map</c>):向左行走的 40 帧连拍里,镜头 x 在主角到达屏幕 350 之前一直是 96,
    /// 之后每帧跟着主角降到 0 为止;进城后的第一张截图里主角在屏幕 450、镜头 24。两个端点 0 与 96 是这张地图的实测值,
    /// 怎么由地图数据算出来没有研究;初始镜头按"主角居中再夹住"取(主角在 615 出生时得 96,与实测一致)。
    /// </summary>
    public sealed class TownCamera
    {
        public const int DefaultCenterX = 400;
        public const int DefaultHalfWidth = 50;

        public int CenterX { get; }
        public int HalfWidth { get; }
        public int MinX { get; }
        public int MaxX { get; }

        /// <summary>镜头在世界里的 x(屏幕左边缘对应的世界 x)。</summary>
        public double X { get; private set; }

        public int IntX => (int)Math.Floor(X);

        public TownCamera(int minX, int maxX, double heroX, int centerX = DefaultCenterX, int halfWidth = DefaultHalfWidth)
        {
            MinX = minX;
            MaxX = maxX;
            CenterX = centerX;
            HalfWidth = halfWidth;
            X = Clamp(heroX - centerX);
        }

        /// <summary>主角移动之后调用:按死区规则更新镜头。</summary>
        public void Follow(double heroX)
        {
            double screen = heroX - X;
            if (screen < CenterX - HalfWidth)
            {
                X = heroX - (CenterX - HalfWidth);
            }
            else if (screen > CenterX + HalfWidth)
            {
                X = heroX - (CenterX + HalfWidth);
            }
            X = Clamp(X);
        }

        /// <summary>直接放到某个镜头 x(用来对照原版截图)。</summary>
        public void Place(double x)
        {
            X = Clamp(x);
        }

        private double Clamp(double x)
        {
            return x < MinX ? MinX : (x > MaxX ? MaxX : x);
        }
    }

    /// <summary>
    /// 城镇里可以用键盘操作的主角:状态(站立/行走/奔跑/跳跃/挥击、朝向)、位置、在可走区域里的移动,以及按状态选 .ani 帧并用换装合成成精灵。
    /// 移动与跳跃的规则来自对客户端代码的静态读取(证据 E-073):各状态的速度表(行走 / 奔跑 / 空中 × 直走 / 斜走)、
    /// 起跳初速度与重力的公式、起跳前的蹲姿、落地帧、双击奔跑的时间窗、奔跑的延续与退出条件、每次跳跃只能出一次空中攻击。
    /// 速度表是引擎里的常量,实际速度还要乘一个按轴的速度系数(角色的移动速度属性、装备与增益);
    /// 这个系数目前没有来源,用原版连拍里量到的行走速度标定(水平 222.5、竖直 90 像素/秒,证据 E-066),标定的两个系数见 <see cref="DefaultSpeedFactorX"/>、
    /// <see cref="DefaultSpeedFactorY"/>。竖直方向的标定值只有引擎常量预测的一半,原因没有查到(E-073 的未知项),所以两个轴各用各的系数。
    /// 连按方向键的时间窗(300 毫秒)、起跳公式、重力、空中速度、落地帧时长都是代码读出来的,没有用原版录像逐帧核对。
    /// </summary>
    public sealed class TownHero
    {
        public const int ObjectId = -2;

        /// <summary>行走速度表(像素/秒,引擎常量,证据 E-073):直走的水平速度。</summary>
        public const double EngineWalkX = 143.0;

        /// <summary>斜着走(水平与竖直方向同时有输入)时的水平速度。</summary>
        public const double EngineWalkDiagonalX = 119.0;

        /// <summary>直走的竖直速度。</summary>
        public const double EngineWalkY = 114.0;

        /// <summary>斜着走时的竖直速度。</summary>
        public const double EngineWalkDiagonalY = 95.0;

        /// <summary>奔跑速度表:直跑的水平速度。</summary>
        public const double EngineDashX = 286.0;

        /// <summary>斜着跑时的水平速度。</summary>
        public const double EngineDashDiagonalX = 238.0;

        /// <summary>奔跑时的竖直速度。</summary>
        public const double EngineDashY = 136.0;

        /// <summary>斜着跑时的竖直速度。</summary>
        public const double EngineDashDiagonalY = 114.0;

        /// <summary>空中的竖直(深度)速度:地面行走值的一半(整数除),直走。水平方向空中速度与地面行走相同。</summary>
        public const double EngineAirY = 57.0;

        /// <summary>空中斜着动时的竖直速度(95 / 2,整数除)。</summary>
        public const double EngineAirDiagonalY = 47.0;

        /// <summary>水平行走速度(像素/秒),标定值。在赛丽亚房间向左走的 11 个点上线性回归得到 222.5。</summary>
        public const double DefaultWalkSpeedX = 222.5;

        /// <summary>竖直行走速度(像素/秒),标定值。在副本领主房里向下走的 4 个点回归得 92.7,向上走的 2 点得 87.4,取 90。</summary>
        public const double DefaultWalkSpeedY = 90.0;

        /// <summary>水平速度系数的标定值 = 标定的行走速度 / 引擎的直走水平速度。</summary>
        public const double DefaultSpeedFactorX = DefaultWalkSpeedX / EngineWalkX;

        /// <summary>竖直速度系数的标定值 = 标定的行走速度 / 引擎的直走竖直速度。</summary>
        public const double DefaultSpeedFactorY = DefaultWalkSpeedY / EngineWalkY;

        /// <summary>
        /// 行走动画的播放倍率(证据 E-066):.ani 里每帧 100 毫秒,但原版连拍里行走一帧只有约 83 毫秒
        /// (在 23 个点上联合拟合起步时间与每帧时长,可行范围 82.4 到 84.3 毫秒,即倍率 1.19 到 1.21);站立动画按 .ani 的原速(倍率 1.0,30 张都对得上)。
        /// 倍率与角色脚本 [move speed] 的关系没有研究,取 1.2。
        /// </summary>
        public const double DefaultMoveAnimationRate = 1.2;

        /// <summary>直跑的水平速度(像素/秒),按标定的水平速度系数折算(引擎表的 286 × <see cref="DefaultSpeedFactorX"/>)。</summary>
        public const double DefaultDashSpeedX = EngineDashX * DefaultSpeedFactorX;

        /// <summary>奔跑时的竖直速度(像素/秒),按标定的竖直速度系数折算(引擎表的 136 × <see cref="DefaultSpeedFactorY"/>)。</summary>
        public const double DefaultDashSpeedY = EngineDashY * DefaultSpeedFactorY;

        /// <summary>
        /// 双击方向键算"奔跑"的时间窗(毫秒,从第一次按下到第二次按下):客户端的按键序列定义里,
        /// 左或右的"按下、松开、再按下"三步之间最大间隔 300 毫秒、最小间隔 0(证据 E-073,静态读出)。
        /// </summary>
        public const double DoubleTapWindowMs = 300.0;

        /// <summary>地面物体的重力基数(引擎常量,单位 1/100 像素每秒²,证据 E-073):实际重力 = 跳跃速度属性 × 这个数 / 100。</summary>
        public const double EngineGroundGravity = 1500.0;

        /// <summary>起跳初速度公式的两个常数(证据 E-073):分母 = (重量×100 / 跳跃速度 − 10000) × 0.0121 + 2100,分子 = 跳跃力 × 4000。</summary>
        public const double EngineTakeoffK1 = 0.0121;

        /// <summary>见 <see cref="EngineTakeoffK1"/>。</summary>
        public const double EngineTakeoffK2 = 2100.0;

        /// <summary>角色脚本缺 [jump power] 时的默认跳跃力(随手取的同量级整数,不是原版的数值)。</summary>
        public const double DefaultJumpPower = 400.0;

        /// <summary>角色脚本缺 [weight] 时的默认重量(随手取的同量级整数,不是原版的数值)。</summary>
        public const int DefaultWeight = 60000;

        /// <summary>角色脚本缺 [jump speed] 时的默认跳跃速度属性(随手取的同量级整数,不是原版的数值)。</summary>
        public const int DefaultJumpSpeedStat = 100;

        /// <summary>落地帧时长的兜底(毫秒):跳跃动画没有可识别的落地段时用它(假设)。</summary>
        public const double FallbackLandingMs = 50.0;

        /// <summary>延时不短于它的帧是"停留帧"(跳跃动画里最高点与落地前各有一个延时极长的帧,由游戏逻辑决定什么时候往下走)。</summary>
        public const int HoldDelayMs = 5000;

        private readonly AniFile _rest;
        private readonly AniFile _move;
        private readonly string _restPath;
        private readonly string _movePath;
        private readonly AniTimeline _restTimeline;
        private readonly AniTimeline _moveTimeline;
        private readonly IReadOnlyList<WorldRect> _area;
        private readonly IWalkArea _walk;
        private double _motionMs;
        private AniFile[] _attacks = new AniFile[0];
        private string[] _attackPaths = new string[0];
        private AniTimeline[] _attackTimelines = new AniTimeline[0];
        private bool _attackQueued;
        private AniFile _battleRest;
        private string _battleRestPath;
        private AniTimeline _battleRestTimeline;
        private bool _battleStance;

        // 奔跑与跳跃的动作(角色脚本没写就是 null,对应的操作不生效)
        private AniFile _dash;
        private string _dashPath;
        private AniTimeline _dashTimeline;
        private AniFile _dashAttack;
        private string _dashAttackPath;
        private AniTimeline _dashAttackTimeline;
        private AniFile _jump;
        private string _jumpPath;
        private AniTimeline _jumpTimeline;
        private int _jumpHold1 = -1;
        private int _jumpHold2 = -1;
        private AniFile _jumpAttack;
        private string _jumpAttackPath;
        private AniTimeline _jumpAttackTimeline;

        // 双击检测:上一帧的水平方向、最近一次按下的方向与距今的时间;本次更新里刚好完成了一次双击(只在这一次更新里有效)
        private int _prevDirX;
        private int _tapDir;
        private double _sinceTapMs = double.MaxValue;
        private bool _dashTrigger;

        // 最近一次地面移动的水平速度(像素/秒,带符号):奔跑攻击以它的大小为滑行初速;奔跑攻击起手时的滑行初速
        private double _groundVx;
        private double _dashAttackV0;

        // 跳跃:起跳前蹲姿的计时(负数表示不在蹲姿)、竖直速度(像素/秒)、腾空时间、下降段时间、落地帧计时(负数表示没在落地)
        private double _prepMs = -1;
        private double _vz;
        private double _airMs;
        private double _descendMs;
        private bool _descending;
        private double _landingMs = -1;
        private bool _airAttackUsed;

        /// <summary>攻击动画的播放倍率。取 1.0(.ani 原速);原版连拍里没有量过攻击一击的时长(证据 E-069),角色脚本的 [attack speed] 与它的关系没有研究。</summary>
        public const double DefaultAttackAnimationRate = 1.0;

        public AvatarOutfit Outfit { get; set; }

        /// <summary>可走区域(矩形的并集);空表示不限制。用 <see cref="Walk"/> 取一般意义上的可走区域(副本房间不是矩形)。</summary>
        public IReadOnlyList<WorldRect> Area => _area;

        /// <summary>一般意义上的可走区域。</summary>
        public IWalkArea Walk => _walk;

        public double X { get; private set; }
        public double Y { get; private set; }
        public bool FacingLeft { get; private set; }
        public HeroMotion Motion { get; private set; }
        /// <summary>水平速度系数(速度表的水平数值乘它才是像素/秒)。默认是用原版连拍标定的 <see cref="DefaultSpeedFactorX"/>;角色的移动速度属性、装备与增益都折在这里。</summary>
        public double SpeedFactorX { get; set; } = DefaultSpeedFactorX;

        /// <summary>竖直速度系数,同上,默认 <see cref="DefaultSpeedFactorY"/>。</summary>
        public double SpeedFactorY { get; set; } = DefaultSpeedFactorY;

        /// <summary>直走的水平行走速度(像素/秒)= 引擎表 × <see cref="SpeedFactorX"/>。</summary>
        public double WalkSpeedX => EngineWalkX * SpeedFactorX;

        /// <summary>直走的竖直行走速度(像素/秒)。</summary>
        public double WalkSpeedY => EngineWalkY * SpeedFactorY;

        /// <summary>直跑的水平速度(像素/秒)。</summary>
        public double DashSpeedX => EngineDashX * SpeedFactorX;

        /// <summary>奔跑时直跑的竖直速度(像素/秒)。</summary>
        public double DashSpeedY => EngineDashY * SpeedFactorY;

        public double MoveAnimationRate { get; set; } = DefaultMoveAnimationRate;
        public double AttackAnimationRate { get; set; } = DefaultAttackAnimationRate;

        /// <summary>离地高度(像素;0 在地面上)。画面上的位置往上抬这么多,深度排序仍按 <see cref="Y"/>。</summary>
        public double Z { get; private set; }

        /// <summary>角色脚本的 [jump power](跳跃力,起跳初速度公式的分子);没写时 <see cref="DefaultJumpPower"/>。</summary>
        public double JumpPower { get; set; } = DefaultJumpPower;

        /// <summary>角色脚本的 [weight](重量,起跳初速度公式里的分母项);没写时 <see cref="DefaultWeight"/>。</summary>
        public int WeightStat { get; set; } = DefaultWeight;

        /// <summary>
        /// 角色脚本的 [jump speed](跳跃速度属性):不是百分比,也不影响空中的水平速度;它参与起跳初速度公式,并且乘以重力基数决定重力
        /// (证据 E-073)。没写时 <see cref="DefaultJumpSpeedStat"/>。
        /// </summary>
        public int JumpSpeedStat { get; set; } = DefaultJumpSpeedStat;

        /// <summary>
        /// 地图附加的重力修正,加在(负的)重力基数 −1500 上(客户端里这一项来自地图数据;证据 E-073 的未知项:有没有非零的地图没查过,取 0)。
        /// 正数让重力变小。
        /// </summary>
        public double MapGravityAdjust { get; set; }

        /// <summary>起跳初速度(像素/秒),用 <see cref="TakeoffSpeed"/> 由跳跃力、重量与跳跃速度属性算出。</summary>
        public double JumpTakeoffSpeed => TakeoffSpeed(JumpPower, WeightStat, JumpSpeedStat);

        /// <summary>重力加速度的大小(像素/秒²)= 跳跃速度属性 × (重力基数 + 地图修正) / 100。</summary>
        public double JumpGravity => JumpSpeedStat * (EngineGroundGravity - MapGravityAdjust) / 100.0;

        /// <summary>跳起到落回地面的腾空时间(毫秒,不含起跳前的蹲姿与落地帧)。</summary>
        public double JumpAirtimeMs => JumpGravity > 0 ? 2.0 * JumpTakeoffSpeed / JumpGravity * 1000.0 : 0.0;

        /// <summary>一跳能升到的最大高度(像素)。</summary>
        public double JumpApexHeight => JumpGravity > 0 ? JumpTakeoffSpeed * JumpTakeoffSpeed / (2.0 * JumpGravity) : 0.0;

        /// <summary>
        /// 起跳初速度(像素/秒)= 跳跃力 × 4000 / (((重量 × 100) / 跳跃速度 − 10000) × 0.0121 + 2100),其中"(重量 × 100) / 跳跃速度"是整数除法
        /// (客户端 0x008327c0,证据 E-073)。跳跃速度属性不是正数时按 1 处理(原版会除零)。
        /// </summary>
        public static double TakeoffSpeed(double jumpPower, int weight, int jumpSpeed)
        {
            int js = jumpSpeed > 0 ? jumpSpeed : 1;
            int ratio = (int)((long)weight * 100 / js);
            double denominator = (ratio - 10000) * EngineTakeoffK1 + EngineTakeoffK2;
            return denominator > 0 ? jumpPower * 4000.0 / denominator : 0.0;
        }

        /// <summary>起跳前的蹲姿时长(毫秒):跳跃动画从第 0 帧播到"起跳帧"(第 1 帧)所需的时间;没有可识别的相位时为 0。</summary>
        public double JumpPrepareMs => PhasedJump ? _jumpTimeline.StartMs(JumpLiftoffFrame) : 0.0;

        /// <summary>落地段时长(毫秒):从落地帧播到动画结束;没有可识别的落地段时为 <see cref="FallbackLandingMs"/>。</summary>
        public double JumpLandingMs => PhasedJump ? Math.Max(0.0, _jumpTimeline.TotalMilliseconds - _jumpTimeline.StartMs(JumpLandFrame)) : FallbackLandingMs;

        /// <summary>
        /// 起跳帧:蹲姿结束后从这一帧起播升空的动画。客户端里各职业的跳跃帧三元组(起跳帧、下落帧、落地帧)第一项都是 1(证据 E-073)。
        /// </summary>
        public int JumpLiftoffFrame => 1;

        /// <summary>下落帧:过了最高点从这一帧起播下落动画。取第一个停留帧的下一帧(与客户端里三个职业的三元组对得上,证据 E-073)。没有两个停留帧时为 -1。</summary>
        public int JumpFallFrame => _jumpHold1 >= 0 ? _jumpHold1 + 1 : -1;

        /// <summary>落地帧:落地之后从这一帧播到动画结束。取第二个停留帧的下一帧。没有两个停留帧时为 -1。</summary>
        public int JumpLandFrame => _jumpHold2 >= 0 ? _jumpHold2 + 1 : -1;

        /// <summary>跳跃动画有两个停留帧,且停留帧的位置允许分出起跳、下落、落地三个相位。</summary>
        private bool PhasedJump => _jumpTimeline != null && _jumpHold1 >= 1 && _jumpHold2 > _jumpHold1 + 1 && _jumpHold2 + 1 < _jumpTimeline.FrameCount;

        /// <summary>有奔跑动作([dash motion])。</summary>
        public bool CanDash => _dash != null;

        /// <summary>有奔跑攻击动作([dashattack motion])。</summary>
        public bool CanDashAttack => _dashAttack != null;

        /// <summary>有跳跃动作([jump motion])。</summary>
        public bool CanJump => _jump != null;

        /// <summary>有跳跃攻击动作([jumpattack motion])。</summary>
        public bool CanJumpAttack => _jumpAttack != null;

        /// <summary>处在奔跑或奔跑攻击里(双击方向键进入,按客户端的规则延续与退出)。</summary>
        public bool RunArmed => Motion == HeroMotion.Dash || Motion == HeroMotion.DashAttack;

        /// <summary>腾空中(跳跃或空中挥击)。</summary>
        public bool IsAirborne => Motion == HeroMotion.Jump || Motion == HeroMotion.JumpAttack;

        /// <summary>出口的碰撞体等障碍;为 null 时只受可走区域限制。</summary>
        public IHeroBlocker Blocker { get; set; }

        /// <summary>最近一次 <see cref="Update(double, int, int, bool, bool)"/> 里,水平移动被哪个障碍挡住了(<see cref="IHeroBlocker.BlockedBy"/> 的返回值);没被挡住为 null。</summary>
        public object PushedInto { get; private set; }

        /// <summary>最近一次更新里,向上走被可走区域(或障碍)挡住了。</summary>
        public bool PushedUp { get; private set; }

        /// <summary>最近一次更新里,向下走被可走区域(或障碍)挡住了。</summary>
        public bool PushedDown { get; private set; }

        /// <summary>角色脚本 [attack motion] 里的连招段数(没载入攻击动作时为 0)。</summary>
        public int AttackCount => _attacks.Length;

        /// <summary>
        /// 角色脚本的 [move speed](没有则 0)。与实测的行走速度 <see cref="WalkSpeedX"/>(像素/秒)的比,用来把怪物脚本的 [move speed] 换算成像素/秒
        /// (证据 E-069):两者是同一个单位只是假设,原版的换算没有读出来。
        /// </summary>
        public int MoveSpeedStat { get; private set; }

        /// <summary>正在挥的是连招第几段(从 0 数);不在攻击时为 0。</summary>
        public int AttackStage { get; private set; }

        /// <summary>从开始玩起累计挥出的次数(每开始一击加 1)。命中判定用它给"同一击只打中同一目标一次"去重。</summary>
        public long SwingNumber { get; private set; }

        /// <summary>是否正在挥击(连招、奔跑攻击或空中攻击)。</summary>
        public bool IsAttacking => Motion == HeroMotion.Attack || Motion == HeroMotion.DashAttack || Motion == HeroMotion.JumpAttack;

        /// <summary>正在挥的这一击的种类;不在攻击时为 <see cref="HeroAttackKind.Normal"/>。</summary>
        public HeroAttackKind AttackKind => Motion == HeroMotion.DashAttack ? HeroAttackKind.Dash : (Motion == HeroMotion.JumpAttack ? HeroAttackKind.Jump : HeroAttackKind.Normal);

        /// <summary>当前这一击的动画文件名(小写,形如 attack1.ani);不在攻击时为 null。</summary>
        public string AttackMotionFile
        {
            get
            {
                string path;
                switch (Motion)
                {
                    case HeroMotion.Attack:
                        if ((uint)AttackStage >= (uint)_attackPaths.Length)
                        {
                            return null;
                        }
                        path = _attackPaths[AttackStage];
                        break;
                    case HeroMotion.DashAttack:
                        path = _dashAttackPath;
                        break;
                    case HeroMotion.JumpAttack:
                        path = _jumpAttackPath;
                        break;
                    default:
                        return null;
                }
                return path == null ? null : path.Substring(path.LastIndexOf('/') + 1).ToLowerInvariant();
            }
        }

        /// <summary>当前这一击已经过去的动画毫秒数(已乘播放倍率)。</summary>
        public long AttackAnimationMs => (long)(_motionMs * AttackAnimationRate);

        /// <summary>当前动作已经播放的毫秒数(状态一变就从 0 重新数)。</summary>
        public long MotionMs => (long)_motionMs;

        public TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IReadOnlyList<WorldRect> area, double x, double y, bool facingLeft)
            : this(outfit, rest, restPath, move, movePath, area ?? new List<WorldRect>(), null, x, y, facingLeft)
        {
        }

        public TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IWalkArea walk, double x, double y, bool facingLeft)
            : this(outfit, rest, restPath, move, movePath, new List<WorldRect>(), walk ?? throw new ArgumentNullException(nameof(walk)), x, y, facingLeft)
        {
        }

        private TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IReadOnlyList<WorldRect> area, IWalkArea walk, double x, double y, bool facingLeft)
        {
            Outfit = outfit ?? throw new ArgumentNullException(nameof(outfit));
            _rest = rest ?? throw new ArgumentNullException(nameof(rest));
            _move = move ?? throw new ArgumentNullException(nameof(move));
            _restPath = restPath;
            _movePath = movePath;
            _restTimeline = new AniTimeline(rest);
            _moveTimeline = new AniTimeline(move);
            _area = area;
            _walk = walk ?? new RectUnionArea(area);
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = HeroMotion.Rest;
        }

        /// <summary>城镇里站立用的角色脚本标签(证据 E-065)。</summary>
        public const string TownRestTag = "[rest motion]";

        /// <summary>副本里站立用的标签:战斗待机动作(证据 E-067)。</summary>
        public const string DungeonRestTag = "[waiting motion]";

        /// <summary>
        /// 按角色脚本的 <paramref name="restTag"/>(默认 [rest motion])与 [move motion] 取站立与行走动画(路径相对角色脚本所在目录),并创建主角。
        /// </summary>
        public static TownHero Create(DnfAssets assets, string chrPath, AvatarOutfit outfit, IReadOnlyList<WorldRect> area,
            double x, double y, bool facingLeft, string restTag = TownRestTag)
        {
            LoadMotions(assets, chrPath, restTag, out string restPath, out string movePath, out AniFile rest, out AniFile move);
            var hero = new TownHero(outfit, rest, restPath, move, movePath, area, x, y, facingLeft);
            hero.LoadAttackMotions(assets, chrPath);
            hero.LoadMoveSpeedStat(assets, chrPath);
            hero.LoadDashMotions(assets, chrPath);
            hero.LoadJumpMotions(assets, chrPath);
            return hero;
        }

        /// <summary>同 <see cref="Create(DnfAssets, string, AvatarOutfit, IReadOnlyList{WorldRect}, double, double, bool, string)"/>,可走区域用一般的 <see cref="IWalkArea"/>(副本房间的瓦片通行表)。</summary>
        public static TownHero Create(DnfAssets assets, string chrPath, AvatarOutfit outfit, IWalkArea walk,
            double x, double y, bool facingLeft, string restTag = TownRestTag)
        {
            LoadMotions(assets, chrPath, restTag, out string restPath, out string movePath, out AniFile rest, out AniFile move);
            var hero = new TownHero(outfit, rest, restPath, move, movePath, walk, x, y, facingLeft);
            hero.LoadAttackMotions(assets, chrPath);
            hero.LoadMoveSpeedStat(assets, chrPath);
            hero.LoadDashMotions(assets, chrPath);
            hero.LoadJumpMotions(assets, chrPath);
            return hero;
        }

        private static void LoadMotions(DnfAssets assets, string chrPath, string restTag, out string restPath, out string movePath,
            out AniFile rest, out AniFile move)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            string restRel = null, moveRel = null;
            foreach (ScriptLine line in doc)
            {
                if (restRel == null && line.TagIs(restTag))
                {
                    restRel = line.FirstString();
                }
                else if (moveRel == null && line.TagIs("[move motion]"))
                {
                    moveRel = line.FirstString();
                }
            }
            if (restRel == null || moveRel == null)
            {
                throw new DnfFormatException("角色脚本缺少 " + restTag + " 或 [move motion]: " + chrPath);
            }
            restPath = PvfPath.ResolveRelativeTo(chrPath, restRel);
            movePath = PvfPath.ResolveRelativeTo(chrPath, moveRel);
            rest = assets.LoadAni(restPath) ?? throw new DnfFormatException("缺少站立动画: " + restPath);
            move = assets.LoadAni(movePath) ?? throw new DnfFormatException("缺少行走动画: " + movePath);
        }

        /// <summary>把主角直接摆到指定的位置、朝向与动作进度(用来与原版截图逐帧对照,以及读档式的初始化)。</summary>
        public void Place(double x, double y, bool facingLeft, HeroMotion motion, long motionMs)
        {
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            switch (motion)
            {
                case HeroMotion.Attack:
                    Motion = _attacks.Length == 0 ? HeroMotion.Rest : motion;
                    break;
                case HeroMotion.Dash:
                    Motion = _dash == null ? HeroMotion.Rest : motion;
                    break;
                case HeroMotion.DashAttack:
                    Motion = _dashAttack == null ? HeroMotion.Rest : motion;
                    break;
                case HeroMotion.Jump:
                case HeroMotion.JumpAttack:
                    Motion = HeroMotion.Rest;   // 腾空状态有速度与相位,不能直接摆出来
                    break;
                default:
                    Motion = motion;
                    break;
            }
            _motionMs = motionMs < 0 ? 0 : motionMs;
            AttackStage = 0;
            _attackQueued = false;
            _dashTrigger = false;
            if (RunArmed)
            {
                // 摆成奔跑:把"上一帧按着的方向"也摆成朝向,这样紧接着的更新里同方向的按住不算新的一次按下
                _prevDirX = FacingLeft ? -1 : 1;
                _tapDir = _prevDirX;
            }
            ResetAir();
            PushedInto = null;
            PushedUp = false;
            PushedDown = false;
        }

        private void ResetAir()
        {
            Z = 0;
            _prepMs = -1;
            _vz = 0;
            _airMs = 0;
            _descendMs = 0;
            _descending = false;
            _landingMs = -1;
            _airAttackUsed = false;
        }

        /// <summary>摆成"正在挥连招第 <paramref name="stage"/> 段、已播放 <paramref name="motionMs"/> 毫秒(按墙钟)"。没载入攻击动作时无效。</summary>
        public void PlaceAttack(double x, double y, bool facingLeft, int stage, long motionMs)
        {
            if (_attacks.Length == 0)
            {
                Place(x, y, facingLeft, HeroMotion.Rest, motionMs);
                return;
            }
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = HeroMotion.Attack;
            AttackStage = stage < 0 ? 0 : (stage >= _attacks.Length ? _attacks.Length - 1 : stage);
            _motionMs = motionMs < 0 ? 0 : motionMs;
            _attackQueued = false;
            ResetAir();
        }

        /// <summary>
        /// 载入战斗站立动作 [waiting motion] 作为"战斗姿态"的站立动画(<see cref="BattleStance"/> 为真时用它,否则用创建时的站立动画)。
        /// 缺这个标签或动画时返回 false,姿态保持不变。证据 E-070:房间里还有活着的怪物时主角站的是 [waiting motion],怪物都死光之后站的是 [rest motion]。
        /// </summary>
        public bool LoadBattleRest(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs(DungeonRestTag))
                {
                    continue;
                }
                string rel = line.FirstString();
                string path = rel == null ? null : PvfPath.ResolveRelativeTo(chrPath, rel);
                AniFile ani = path == null ? null : assets.LoadAni(path);
                if (ani == null || ani.Frames.Length == 0)
                {
                    return false;
                }
                _battleRest = ani;
                _battleRestPath = path;
                _battleRestTimeline = new AniTimeline(ani);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 战斗姿态:为真时站立用 <see cref="LoadBattleRest"/> 载入的动作。没载入时恒为假。
        /// 站着的时候切换姿态,站立动作从头播放。
        /// </summary>
        public bool BattleStance
        {
            get => _battleStance;
            set
            {
                bool next = value && _battleRest != null;
                if (next != _battleStance)
                {
                    _battleStance = next;
                    if (Motion == HeroMotion.Rest)
                    {
                        _motionMs = 0;
                    }
                }
            }
        }

        /// <summary>读 .chr 的 [move speed](第一个数,真实脚本里是浮点标记;取整);没有这个标签就是 0。</summary>
        public void LoadMoveSpeedStat(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            MoveSpeedStat = 0;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[move speed]"))
                {
                    double? n = line.FirstNumber();
                    MoveSpeedStat = n.HasValue ? (int)Math.Round(n.Value) : 0;
                    break;
                }
            }
        }

        /// <summary>
        /// 读 .chr 的 [attack motion](相对角色脚本所在目录的一串 .ani),作为连招的各段。读不到就保持没有攻击。
        /// </summary>
        public void LoadAttackMotions(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            var anis = new List<AniFile>();
            var paths = new List<string>();
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[attack motion]"))
                {
                    continue;
                }
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind != ScriptValueKind.String || string.IsNullOrEmpty(v.Text))
                    {
                        continue;
                    }
                    string path = PvfPath.ResolveRelativeTo(chrPath, v.Text);
                    AniFile ani = assets.LoadAni(path);
                    if (ani == null || ani.Frames.Length == 0)
                    {
                        continue;
                    }
                    anis.Add(ani);
                    paths.Add(path);
                }
                break;
            }
            _attacks = anis.ToArray();
            _attackPaths = paths.ToArray();
            _attackTimelines = new AniTimeline[_attacks.Length];
            for (int i = 0; i < _attacks.Length; i++)
            {
                _attackTimelines[i] = new AniTimeline(_attacks[i]);
            }
        }

        /// <summary>读 .chr 的 [dash motion] 与 [dashattack motion](相对角色脚本所在目录)。缺哪个,对应的操作就不生效。</summary>
        public void LoadDashMotions(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = ReadChr(assets, chrPath);
            LoadMotion(assets, chrPath, doc, "[dash motion]", out _dash, out _dashPath, out _dashTimeline);
            LoadMotion(assets, chrPath, doc, "[dashattack motion]", out _dashAttack, out _dashAttackPath, out _dashAttackTimeline);
        }

        /// <summary>
        /// 读 .chr 的 [jump motion]、[jumpattack motion]、[jump power](跳跃力)、[weight](重量)与 [jump speed](跳跃速度属性);
        /// 后三个进起跳初速度与重力的公式(证据 E-073)。
        /// 跳跃动画里延时 ≥ <see cref="HoldDelayMs"/> 的"停留帧"标出最高点与落地前的两个相位:第一个停留帧的下一帧是下落帧,第二个停留帧的下一帧是落地帧
        /// (原版的跳跃动画就是这样做的,证据 E-072;客户端里职业的跳跃帧三元组与之对得上,证据 E-073)。
        /// </summary>
        public void LoadJumpMotions(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = ReadChr(assets, chrPath);
            LoadMotion(assets, chrPath, doc, "[jump motion]", out _jump, out _jumpPath, out _jumpTimeline);
            LoadMotion(assets, chrPath, doc, "[jumpattack motion]", out _jumpAttack, out _jumpAttackPath, out _jumpAttackTimeline);
            JumpPower = DefaultJumpPower;
            WeightStat = DefaultWeight;
            JumpSpeedStat = DefaultJumpSpeedStat;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[jump power]"))
                {
                    double? n = line.FirstNumber();
                    if (n.HasValue && n.Value > 0)
                    {
                        JumpPower = n.Value;
                    }
                }
                else if (line.TagIs("[weight]"))
                {
                    double? n = line.FirstNumber();
                    if (n.HasValue && n.Value > 0)
                    {
                        WeightStat = (int)Math.Round(n.Value);
                    }
                }
                else if (line.TagIs("[jump speed]"))
                {
                    double? n = line.FirstNumber();
                    if (n.HasValue && n.Value > 0)
                    {
                        JumpSpeedStat = (int)Math.Round(n.Value);
                    }
                }
            }
            _jumpHold1 = -1;
            _jumpHold2 = -1;
            if (_jump != null)
            {
                for (int i = 0; i < _jump.Frames.Length; i++)
                {
                    if (_jump.Frames[i].DelayMs < HoldDelayMs)
                    {
                        continue;
                    }
                    if (_jumpHold1 < 0)
                    {
                        _jumpHold1 = i;
                    }
                    else
                    {
                        _jumpHold2 = i;
                        break;
                    }
                }
            }
        }

        private static ScriptLine[] ReadChr(DnfAssets assets, string chrPath)
        {
            return assets.ReadScript(chrPath) ?? throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
        }

        private static void LoadMotion(DnfAssets assets, string chrPath, ScriptLine[] doc, string tag, out AniFile ani, out string path, out AniTimeline timeline)
        {
            ani = null;
            path = null;
            timeline = null;
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs(tag))
                {
                    continue;
                }
                string rel = line.FirstString();
                if (string.IsNullOrEmpty(rel))
                {
                    return;
                }
                string resolved = PvfPath.ResolveRelativeTo(chrPath, rel);
                AniFile loaded = assets.LoadAni(resolved);
                if (loaded == null || loaded.Frames.Length == 0)
                {
                    return;
                }
                ani = loaded;
                path = resolved;
                timeline = new AniTimeline(loaded);
                return;
            }
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。<paramref name="dirX"/>、<paramref name="dirY"/> 是方向键状态(取符号:负为左/上,正为右/下)。
        /// 有任何方向就进入行走,否则站立;状态变化时动作从头播放。水平方向非 0 时朝向跟着变,只按上下时保持原朝向。
        /// 移动被可走区域挡住时停在边界上,但动作仍按行走播放(与原版一致:撞墙后脚还在迈,证据 E-066)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false);
        }

        /// <summary>
        /// 同 <see cref="Update(double, int, int)"/>,多一个攻击键:站立或行走时按下就起手连招第 0 段(起手那一刻可以转身,之后朝向锁定,不能走动);
        /// 挥击中再按一次,挥完这一击后接下一段(最后一段之后回到站立)。连按缓冲的时间窗没有样本,按"挥击中任何时刻按下都算"处理(证据 E-069)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, false);
        }

        /// <summary>
        /// 同 <see cref="Update(double, int, int, bool)"/>,多一个跳跃键(刚按下的边沿):在地面上站立、行走或奔跑时按下就起跳;
        /// 在地面奔跑时按攻击键出奔跑攻击;在空中按攻击键出空中攻击(每次跳跃一击)。
        /// 奔跑:地面上双击左或右(两次按下相隔不超过 <see cref="DoubleTapWindowMs"/>)就进入;方向全松开、或水平方向按了与朝向相反的键才退出
        /// (只留着上下方向键时奔跑继续,水平速度为 0)。角色脚本没有 [dash motion] 时没有奔跑。
        /// 跳跃:先蹲姿(<see cref="JumpPrepareMs"/>)再离地;离地时按着的水平方向决定朝向,腾空中可以继续改变水平与深度方向与朝向(挥击中不能)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            dirX = Math.Sign(dirX);
            dirY = Math.Sign(dirY);
            PushedInto = null;
            PushedUp = false;
            PushedDown = false;
            TrackTap(deltaMs, dirX);
            switch (Motion)
            {
                case HeroMotion.Attack:
                    AdvanceAttack(deltaMs, attackPressed);
                    return;
                case HeroMotion.DashAttack:
                    AdvanceDashAttack(deltaMs);
                    return;
                case HeroMotion.Jump:
                case HeroMotion.JumpAttack:
                    AdvanceAir(deltaMs, dirX, dirY, attackPressed);
                    return;
            }
            if (jumpPressed && _jump != null)
            {
                if (dirX != 0)
                {
                    FacingLeft = dirX < 0;
                }
                BeginJump();
                AdvanceAir(deltaMs, dirX, dirY, false);
                return;
            }
            if (attackPressed)
            {
                if (Motion == HeroMotion.Dash && _dashAttack != null)
                {
                    BeginDashAttack();
                    AdvanceDashAttack(deltaMs);
                    return;
                }
                if (_attacks.Length > 0)
                {
                    if (dirX != 0)
                    {
                        FacingLeft = dirX < 0;
                    }
                    BeginAttack(0, 0.0);
                    AdvanceAttack(deltaMs, false);
                    return;
                }
            }
            GroundMove(deltaMs, dirX, dirY);
        }

        /// <summary>
        /// 双击检测:记下水平方向的按下。一次"按下"= 水平方向从无变成有(或直接换了方向);
        /// 与上一次按下同方向、中间松开过、间隔不超过时间窗,这次更新里就完成了一次双击(<see cref="_dashTrigger"/>),
        /// 能不能进入奔跑由当时的状态决定(只有站立或行走时,而且有奔跑动作)。
        /// </summary>
        private void TrackTap(double deltaMs, int dirX)
        {
            _dashTrigger = false;
            if (_sinceTapMs < 1e12)
            {
                _sinceTapMs += deltaMs;
            }
            if (dirX != _prevDirX && dirX != 0)
            {
                _dashTrigger = _prevDirX == 0 && dirX == _tapDir && _sinceTapMs <= DoubleTapWindowMs;
                _tapDir = dirX;
                _sinceTapMs = 0;
            }
            _prevDirX = dirX;
        }

        /// <summary>
        /// 地面上的站立、行走与奔跑。速度表按"直走 / 斜走(水平与竖直方向同时有输入)"取值,再乘按轴的速度系数。
        /// 奔跑由双击进入;奔跑中方向全空、或水平方向按了与朝向相反的键就退出,只按着上下时奔跑继续(此时水平速度为 0),证据 E-073。
        /// </summary>
        private void GroundMove(double deltaMs, int dirX, int dirY)
        {
            bool dashing = Motion == HeroMotion.Dash;
            if (dashing)
            {
                bool opposite = dirX != 0 && (dirX < 0) != FacingLeft;
                if ((dirX == 0 && dirY == 0) || opposite)
                {
                    dashing = false;
                }
            }
            else if (_dashTrigger && _dash != null && dirX != 0)
            {
                dashing = true;
            }
            bool moving = dirX != 0 || dirY != 0;
            HeroMotion next = !moving ? HeroMotion.Rest : (dashing ? HeroMotion.Dash : HeroMotion.Move);
            if (dirX != 0)
            {
                FacingLeft = dirX < 0;
            }
            if (next != Motion)
            {
                Motion = next;
                _motionMs = 0;
            }
            _motionMs += deltaMs;
            bool diagonal = dirX != 0 && dirY != 0;
            double vx = 0, vy = 0;
            if (moving)
            {
                double straightX = dashing ? EngineDashX : EngineWalkX;
                double diagonalX = dashing ? EngineDashDiagonalX : EngineWalkDiagonalX;
                double straightY = dashing ? EngineDashY : EngineWalkY;
                double diagonalY = dashing ? EngineDashDiagonalY : EngineWalkDiagonalY;
                vx = dirX * (diagonal ? diagonalX : straightX) * SpeedFactorX;
                vy = dirY * (diagonal ? diagonalY : straightY) * SpeedFactorY;
                double seconds = deltaMs / 1000.0;
                X = MoveAxis(X, vx * seconds, true);
                Y = MoveAxis(Y, vy * seconds, false);
            }
            _groundVx = vx;
        }

        private void BeginAttack(int stage, double carryMs)
        {
            Motion = HeroMotion.Attack;
            AttackStage = stage;
            _motionMs = carryMs;
            _attackQueued = false;
            SwingNumber++;
        }

        private void BeginDashAttack()
        {
            Motion = HeroMotion.DashAttack;
            AttackStage = 0;
            _motionMs = 0;
            _attackQueued = false;
            _dashAttackV0 = Math.Abs(_groundVx);   // 滑行的初速 = 起手时的水平速度(竖直方向单独按着奔跑时它是 0)
            SwingNumber++;
        }

        /// <summary>奔跑攻击一击的墙钟时长(毫秒);没有这个动作为 0。</summary>
        public double DashAttackDurationMs
        {
            get
            {
                if (_dashAttackTimeline == null)
                {
                    return 0.0;
                }
                double rate = AttackAnimationRate > 0.0 ? AttackAnimationRate : 1.0;
                return Math.Ceiling(_dashAttackTimeline.TotalMilliseconds / rate);
            }
        }

        /// <summary>
        /// 奔跑攻击:动画播放期间沿朝向向前滑,速度从起手时的水平速度线性减到 0(证据 E-073:客户端把加速度设成 −1000 × 初速 / 动画毫秒数),
        /// 滑行受可走区域与障碍限制;播完回到站立,奔跑状态结束(要再双击才能继续跑)。
        /// </summary>
        private void AdvanceDashAttack(double deltaMs)
        {
            double duration = DashAttackDurationMs;
            double from = _motionMs;
            _motionMs += deltaMs;
            if (duration > 0)
            {
                double t0 = Math.Min(from, duration);
                double t1 = Math.Min(_motionMs, duration);
                if (t1 > t0)
                {
                    double v0 = _dashAttackV0 * (1.0 - t0 / duration);
                    double v1 = _dashAttackV0 * (1.0 - t1 / duration);
                    double distance = (v0 + v1) * 0.5 * (t1 - t0) / 1000.0;
                    X = MoveAxis(X, (FacingLeft ? -1.0 : 1.0) * distance, true);
                }
            }
            if (_motionMs >= duration)
            {
                Motion = HeroMotion.Rest;
                AttackStage = 0;
                _motionMs = 0;
            }
        }

        /// <summary>
        /// 起跳:先进入起跳前的蹲姿(水平与深度速度为 0,动画播第 0 帧,时长见 <see cref="JumpPrepareMs"/>),蹲姿结束才离地(证据 E-073)。
        /// 奔跑中起跳与站立起跳用同一套空中速度(客户端的输入跳跃两种来源都发同一条跳跃指令)。
        /// </summary>
        private void BeginJump()
        {
            Motion = HeroMotion.Jump;
            _motionMs = 0;
            _prepMs = 0;
            _airMs = 0;
            _descendMs = 0;
            _descending = false;
            _landingMs = -1;
            _airAttackUsed = false;
            _vz = 0;
            Z = 0;
        }

        /// <summary>落地段播完:回到站立。</summary>
        private void FinishLandingIfDone()
        {
            if (_landingMs >= JumpLandingMs)
            {
                Motion = HeroMotion.Rest;
                _motionMs = 0;
                _landingMs = -1;
            }
        }

        /// <summary>
        /// 腾空的一步。三个相位(证据 E-073):蹲姿(不动)→ 离地(竖直方向按起跳初速度与重力走,最高点之后进入下降段;
        /// 水平速度与地面行走相同、深度速度减半,方向可以随时改,按住的水平方向决定朝向,挥击中不能转身)→ 落地(速度归零,播落地段,播完回到站立)。
        /// 空中攻击的动画播完回到跳跃动作;落地时还在挥就被打断。落地与最高点的时刻按步内精确时间折算,与帧长无关。
        /// </summary>
        private void AdvanceAir(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            if (_landingMs >= 0)
            {
                _landingMs += deltaMs;
                FinishLandingIfDone();
                return;
            }
            if (_prepMs >= 0)
            {
                double prepare = JumpPrepareMs;
                double elapsed = _prepMs + deltaMs;
                if (elapsed < prepare)
                {
                    _prepMs = elapsed;
                    return;
                }
                deltaMs = elapsed - prepare;   // 蹲姿结束:多出来的时间算在空中
                _prepMs = -1;
                _vz = JumpTakeoffSpeed;
            }
            if (attackPressed && Motion == HeroMotion.Jump && !_airAttackUsed && _jumpAttack != null)
            {
                Motion = HeroMotion.JumpAttack;
                AttackStage = 0;
                _motionMs = 0;
                _airAttackUsed = true;
                SwingNumber++;
            }
            _airMs += deltaMs;
            if (_descending)
            {
                _descendMs += deltaMs;
            }
            if (Motion == HeroMotion.JumpAttack)
            {
                _motionMs += deltaMs;
            }
            double seconds = deltaMs / 1000.0;
            if (dirX != 0 && Motion == HeroMotion.Jump)
            {
                FacingLeft = dirX < 0;
            }
            bool diagonal = dirX != 0 && dirY != 0;
            double vx = dirX * (diagonal ? EngineWalkDiagonalX : EngineWalkX) * SpeedFactorX;
            double vy = dirY * (diagonal ? EngineAirDiagonalY : EngineAirY) * SpeedFactorY;
            X = MoveAxis(X, vx * seconds, true);
            Y = MoveAxis(Y, vy * seconds, false);
            double g = JumpGravity;
            double z0 = Z, v0 = _vz;
            Z += v0 * seconds - 0.5 * g * seconds * seconds;
            _vz -= g * seconds;
            if (!_descending && _vz <= 0)
            {
                _descending = true;
                _descendMs = g > 0 ? -_vz / g * 1000.0 : 0.0;   // 最高点之后已经过去的时间
            }
            if (Z <= 0 && _vz < 0)
            {
                // 步内精确的触地时刻:z0 + v0 t − g t² / 2 = 0 的正根;之后的时间算在落地段里
                double leftover = 0.0;
                if (g > 0)
                {
                    double touch = (v0 + Math.Sqrt(v0 * v0 + 2.0 * g * Math.Max(z0, 0.0))) / g;
                    leftover = Math.Max(0.0, seconds - touch) * 1000.0;
                }
                Z = 0;
                _vz = 0;
                _descending = false;
                _landingMs = leftover;
                Motion = HeroMotion.Jump;
                AttackStage = 0;
                FinishLandingIfDone();
                return;
            }
            if (Motion == HeroMotion.JumpAttack && _motionMs >= JumpAttackDurationMs)
            {
                Motion = HeroMotion.Jump;
            }
        }

        /// <summary>空中攻击一击的墙钟时长(毫秒);没有这个动作为 0。</summary>
        public double JumpAttackDurationMs
        {
            get
            {
                if (_jumpAttackTimeline == null)
                {
                    return 0.0;
                }
                double rate = AttackAnimationRate > 0.0 ? AttackAnimationRate : 1.0;
                return Math.Ceiling(_jumpAttackTimeline.TotalMilliseconds / rate);
            }
        }

        /// <summary>这一击的墙钟时长(毫秒):动画总时长除以播放倍率,向上取整。</summary>
        public double AttackDurationMs(int stage)
        {
            if ((uint)stage >= (uint)_attackTimelines.Length)
            {
                return 0.0;
            }
            double rate = AttackAnimationRate > 0.0 ? AttackAnimationRate : 1.0;
            return Math.Ceiling(_attackTimelines[stage].TotalMilliseconds / rate);
        }

        private void AdvanceAttack(double deltaMs, bool attackPressed)
        {
            if (attackPressed && AttackStage + 1 < _attacks.Length)
            {
                _attackQueued = true;
            }
            _motionMs += deltaMs;
            double duration = AttackDurationMs(AttackStage);
            if (_motionMs < duration)
            {
                return;
            }
            double carry = _motionMs - duration;
            if (_attackQueued && AttackStage + 1 < _attacks.Length)
            {
                BeginAttack(AttackStage + 1, carry);
                return;
            }
            Motion = HeroMotion.Rest;
            AttackStage = 0;
            _attackQueued = false;
            _motionMs = 0;
        }

        /// <summary>
        /// 沿一个轴走 <paramref name="delta"/>,每次最多一像素地检查可走区域与障碍,走到边界就停在边界上。
        /// 被挡住的那一步会记下:水平方向被障碍挡住记在 <see cref="PushedInto"/>,竖直方向被挡住记在 <see cref="PushedUp"/> 或 <see cref="PushedDown"/>。
        /// 主角出生时就压在障碍里(例如摆放不当)的话,这一步忽略障碍,让他能走出来。
        /// </summary>
        private double MoveAxis(double from, double delta, bool horizontal)
        {
            if (delta == 0 || _walk.IsUnrestricted)
            {
                return from + delta;
            }
            _ignoreBlocker = Blocker != null && Blocker.BlockedBy(X, Y) != null;
            double pos = from;
            double remaining = Math.Abs(delta);
            double sign = delta < 0 ? -1.0 : 1.0;
            while (remaining > 0)
            {
                double step = remaining < 1.0 ? remaining : 1.0;
                double candidate = pos + sign * step;
                if (!InArea(horizontal ? candidate : X, horizontal ? Y : candidate))
                {
                    NotePush(horizontal, sign, horizontal ? candidate : X, horizontal ? Y : candidate);
                    // 这一步出界:往前挪到刚好还在区域内的最远处(边界是整数,先试到边界上)
                    double edge = sign < 0 ? Math.Ceiling(candidate) : Math.Floor(candidate);
                    if (sign < 0 ? edge > pos : edge < pos)
                    {
                        edge = pos;
                    }
                    if (InArea(horizontal ? edge : X, horizontal ? Y : edge))
                    {
                        pos = edge;
                    }
                    else
                    {
                        // 整数边界本身不在区域内(瓦片通行格是半开区间):二分到边界,停在区域内的一侧
                        double inside = pos, outside = candidate;
                        for (int i = 0; i < 24; i++)
                        {
                            double mid = (inside + outside) * 0.5;
                            if (InArea(horizontal ? mid : X, horizontal ? Y : mid))
                            {
                                inside = mid;
                            }
                            else
                            {
                                outside = mid;
                            }
                        }
                        pos = inside;
                    }
                    break;
                }
                pos = candidate;
                remaining -= step;
            }
            return pos + 0.0; // 把 -0.0 变成 0.0(碰到 x = 0 的边界时会出现)
        }

        private bool _ignoreBlocker;

        private bool InArea(double x, double y)
        {
            if (!_walk.Contains(x, y))
            {
                return false;
            }
            return Blocker == null || _ignoreBlocker || Blocker.BlockedBy(x, y) == null;
        }

        /// <summary>某一步被挡住了:记下是被哪个障碍挡的(只记水平方向),或竖直方向上是向上还是向下被挡。</summary>
        private void NotePush(bool horizontal, double sign, double candidateX, double candidateY)
        {
            if (horizontal)
            {
                if (Blocker != null && !_ignoreBlocker)
                {
                    object by = Blocker.BlockedBy(candidateX, candidateY);
                    if (by != null)
                    {
                        PushedInto = by;
                    }
                }
            }
            else if (sign < 0)
            {
                PushedUp = true;
            }
            else
            {
                PushedDown = true;
            }
        }

        /// <summary>当前动画与帧:站立、行走、奔跑、跳跃或挥击的 .ani、它的路径、此刻显示的帧号。</summary>
        public AniFile CurrentAni
        {
            get
            {
                switch (Motion)
                {
                    case HeroMotion.Attack:
                        return _attacks[AttackStage];
                    case HeroMotion.Move:
                        return _move;
                    case HeroMotion.Dash:
                        return _dash;
                    case HeroMotion.DashAttack:
                        return _dashAttack;
                    case HeroMotion.Jump:
                        return _jump;
                    case HeroMotion.JumpAttack:
                        return _jumpAttack;
                    default:
                        return _battleStance ? _battleRest : _rest;
                }
            }
        }

        public string CurrentAnimationPath
        {
            get
            {
                switch (Motion)
                {
                    case HeroMotion.Attack:
                        return _attackPaths[AttackStage];
                    case HeroMotion.Move:
                        return _movePath;
                    case HeroMotion.Dash:
                        return _dashPath;
                    case HeroMotion.DashAttack:
                        return _dashAttackPath;
                    case HeroMotion.Jump:
                        return _jumpPath;
                    case HeroMotion.JumpAttack:
                        return _jumpAttackPath;
                    default:
                        return _battleStance ? _battleRestPath : _restPath;
                }
            }
        }

        public int CurrentFrame
        {
            get
            {
                switch (Motion)
                {
                    case HeroMotion.Attack:
                        return _attackTimelines[AttackStage].FrameAt(AttackAnimationMs);
                    case HeroMotion.Move:
                        return _moveTimeline.FrameAt((long)(_motionMs * MoveAnimationRate));
                    case HeroMotion.Dash:
                        return _dashTimeline.FrameAt((long)(_motionMs * MoveAnimationRate));
                    case HeroMotion.DashAttack:
                        return _dashAttackTimeline.FrameAt(AttackAnimationMs);
                    case HeroMotion.JumpAttack:
                        return _jumpAttackTimeline.FrameAt(AttackAnimationMs);
                    case HeroMotion.Jump:
                        return JumpFrame();
                    default:
                        return (_battleStance ? _battleRestTimeline : _restTimeline).FrameAt(MotionMs);
                }
            }
        }

        /// <summary>
        /// 跳跃动画的帧(证据 E-073 读出的相位切换):蹲姿只播第 0 帧;离地后从起跳帧(第 1 帧)起按腾空时间播,到第一个停留帧停住;
        /// 过了最高点就跳到下落帧(第一个停留帧的下一帧),在下落帧到第二个停留帧之前循环播(客户端在播到落地帧的前一帧时折回下落帧);
        /// 落地时跳到落地帧,播到动画结束。动画没有可识别的相位时(没有两个停留帧)按腾空时间一路播。
        /// </summary>
        private int JumpFrame()
        {
            AniTimeline t = _jumpTimeline;
            if (!PhasedJump)
            {
                return t.FrameAt(_landingMs >= 0 ? t.TotalMilliseconds : (long)_airMs);
            }
            if (_prepMs >= 0)
            {
                return t.FrameAt((long)_prepMs);
            }
            if (_landingMs >= 0)
            {
                return t.FrameAt(t.StartMs(JumpLandFrame) + (long)_landingMs);
            }
            if (!_descending)
            {
                return t.FrameAt(Math.Min(t.StartMs(JumpLiftoffFrame) + (long)_airMs, t.StartMs(_jumpHold1)));
            }
            long loopStart = t.StartMs(JumpFallFrame);
            long loopLength = t.StartMs(_jumpHold2) - loopStart;
            long into = loopLength > 0 ? (long)_descendMs % loopLength : 0;
            return t.FrameAt(loopStart + into);
        }

        /// <summary>主角的精灵(按换装图层顺序),位置模型与 NPC 相同;腾空时整体往上抬 <see cref="Z"/> 像素,深度排序仍按脚下的 <see cref="Y"/>。</summary>
        public List<SceneSprite> Sprites(DnfAssets assets, TownProjection projection)
        {
            int wx = (int)Math.Floor(X);
            int wy = (int)Math.Floor(Y);
            int wz = (int)Math.Round(Z);
            return Outfit.SpritesAt(assets, CurrentAni, CurrentAnimationPath, CurrentFrame, projection, wx, wy, wz, FacingLeft, wy, ObjectId);
        }
    }

    /// <summary>带攻击键与跳跃键的场景游戏:每帧多两个"刚按下"的输入(副本房间里的战斗,证据 E-069、E-072)。</summary>
    public interface IActionSession : IHeroSession
    {
        void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed);
    }

    /// <summary>
    /// 带一个可操作主角与跟随镜头的场景游戏(城镇或副本房间)。视图层(Unity 的 TownView)只依赖这个接口:
    /// 每帧 <see cref="Update"/> 推进主角与镜头,再用 <see cref="Build"/> 取绘制列表。
    /// </summary>
    public interface IHeroSession
    {
        string MapPath { get; }
        TownHero Hero { get; }
        TownCamera Camera { get; }
        int LayerCount { get; }
        int NpcCount { get; }
        void Update(double deltaMs, int dirX, int dirY);
        List<SceneSprite> Build(long timeMs);
    }

    /// <summary>
    /// 一场城镇游戏:地图图层、NPC、主角与镜头。每帧先 <see cref="Update"/> 推进主角,再 <see cref="Build"/> 取绘制列表
    /// (地图图层与 NPC 的动画按墙钟时间 <c>timeMs</c> 播放,主角按自己的状态计时)。
    /// </summary>
    public sealed class TownSession : IHeroSession
    {
        /// <summary>纵向常数,E-024 在网关地图上拟合出的 200。</summary>
        public const int DefaultGroundY = 200;

        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedLayer> _layers;
        private readonly List<TownScene.ResolvedNpc> _npcs;

        public string MapPath { get; }
        public TownHero Hero { get; }
        public TownCamera Camera { get; }
        public int GroundY { get; }
        public int NpcCount => _npcs.Count;
        public int LayerCount => _layers.Count;

        /// <summary>此刻的投影:镜头 x 由主角位置按 <see cref="Camera"/> 算出。</summary>
        public TownProjection Projection => new TownProjection(Camera.IntX, GroundY);

        public TownSession(DnfAssets assets, string mapPath, TownHero hero, TownCamera camera, int groundY = DefaultGroundY)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            MapPath = mapPath ?? throw new ArgumentNullException(nameof(mapPath));
            Hero = hero ?? throw new ArgumentNullException(nameof(hero));
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            GroundY = groundY;
            _layers = TownScene.ResolveLayers(assets, mapPath);
            _npcs = TownScene.Resolve(assets, TownScene.ReadMapNpcs(assets, mapPath));
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Hero.Update(deltaMs, dirX, dirY);
            Camera.Follow(Hero.X);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            TownProjection projection = Projection;
            List<SceneSprite> hero = Hero.Sprites(_assets, projection);
            return TownScene.BuildResolvedScene(_assets, _layers, _npcs, projection, timeMs, hero);
        }
    }

    /// <summary>某张城镇地图上主角的出生点与镜头范围(都是在原版连拍上量出来的,证据 E-066;没有量过的地图没有这张表)。</summary>
    public readonly struct TownSlice
    {
        public double SpawnX { get; }
        public double SpawnY { get; }
        public bool SpawnFacingLeft { get; }
        public int CameraMinX { get; }
        public int CameraMaxX { get; }

        public TownSlice(double spawnX, double spawnY, bool spawnFacingLeft, int cameraMinX, int cameraMaxX)
        {
            SpawnX = spawnX;
            SpawnY = spawnY;
            SpawnFacingLeft = spawnFacingLeft;
            CameraMinX = cameraMinX;
            CameraMaxX = cameraMaxX;
        }
    }

    /// <summary>
    /// 演示用的默认主角(鬼剑士):用游戏数据自带的 *_default 装备加一件起始武器,站立与行走动画取自 swordman.chr。
    /// 这是一个"能在城镇里走动"的最小角色,不是角色创建、装备栏或存档;武器是演示选择。
    /// </summary>
    public static class DemoHero
    {
        public const string SwordmanChrPath = "character/swordman/swordman.chr";
        public const string SwordmanJobTag = "[swordman]";
        public const string SwordmanEquipRoot = "equipment/character/swordman";

        /// <summary>起始武器(铁匠铺新手刀,最低等级 1),相对 <see cref="SwordmanEquipRoot"/>。</summary>
        public const string StarterWeapon = "weapon/katana/kat_damaged.equ";

        /// <summary>赛丽亚房间(网关地图)的出生点与镜头范围:出生在 (615, 234) 朝右,镜头 x 在 0 到 96 之间。</summary>
        public static readonly TownSlice SeriaRoom = new TownSlice(615, 234, false, 0, 96);

        /// <summary>地图是否有量过的出生点与镜头范围。</summary>
        public static bool TryGetSlice(string mapPath, out TownSlice slice)
        {
            if (string.Equals(mapPath, "map/elvengard/(r)gate.map", StringComparison.OrdinalIgnoreCase))
            {
                slice = SeriaRoom;
                return true;
            }
            slice = default;
            return false;
        }

        /// <summary>造一个默认外观的鬼剑士,可走区域取自地图的 [virtual movable area]。</summary>
        public static TownHero CreateSwordman(DnfAssets assets, string mapPath, double x, double y, bool facingLeft, string weaponEqu = StarterWeapon,
            string restTag = TownHero.TownRestTag)
        {
            AvatarClass cls = AvatarClass.FromChr(assets, SwordmanChrPath, SwordmanJobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, SwordmanEquipRoot, weaponEqu);
            return TownHero.Create(assets, SwordmanChrPath, outfit, MapScript.ReadVirtualMovableArea(assets, mapPath), x, y, facingLeft, restTag);
        }

        /// <summary>造出地图上的主角与镜头并组成一场城镇游戏(出生点与镜头范围取自 <paramref name="slice"/>)。</summary>
        public static TownSession CreateSession(DnfAssets assets, string mapPath, TownSlice slice, string weaponEqu = StarterWeapon)
        {
            TownHero hero = CreateSwordman(assets, mapPath, slice.SpawnX, slice.SpawnY, slice.SpawnFacingLeft, weaponEqu);
            var camera = new TownCamera(slice.CameraMinX, slice.CameraMaxX, hero.X);
            return new TownSession(assets, mapPath, hero, camera);
        }
    }
}
