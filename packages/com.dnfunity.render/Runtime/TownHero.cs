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

        /// <summary>
        /// 技能动作:鬼斩播放 .chr [etc motion] 里自己的动画(原地不动,<see cref="TownHero.BeginSkillMotion"/>);
        /// 上挑(持短剑以外的武器时)就是连招第三段的动画与攻击信息整段打一遍(<see cref="TownHero.BeginSkillStrike"/>);后跳是跳跃状态的特殊参数,不走这个动作(<see cref="TownHero.BeginBackStep"/>)。
        /// 另有"先挥普攻、播到指定帧再换成技能动画、可以滑行"的通用写法(<see cref="TownHero.BeginSkillWindup"/>、<see cref="TownHero.SkillSlideSpeed"/>),留给持别的武器时的上挑和以后的技能。
        /// </summary>
        Skill,

        /// <summary>
        /// 受击硬直(.chr 的 [damage motion 1]、[damage motion 2],交替播):被怪物打中后定在原地,硬直时间由调用方按受击公式算好后给
        /// (<see cref="TownHero.BeginHurt"/>),期间被往后推一小段,硬直一到回到站立。
        /// </summary>
        Hurt,

        /// <summary>
        /// 倒地(.chr 的 [down motion]):被击飞、落地颠一下、躺着、起身。运动(位置、离地高度、动画时钟)不由主角自己算,
        /// 而是由外面的倒地状态机(<c>Dnf.Play.KnockdownFlight</c>,与怪物共用)用 <see cref="TownHero.SetDownPose"/> 一帧一帧摆过来
        /// (证据 E-079、E-082、E-083、E-105);<see cref="TownHero.EndDown"/> 起身回到站立。倒地期间不接受任何操作。
        /// </summary>
        Down,
    }

    /// <summary>当前这一击属于哪一类:连招里的普攻、奔跑攻击、跳跃攻击、技能。</summary>
    public enum HeroAttackKind
    {
        Normal,
        Dash,
        Jump,
        Skill,
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

        /// <summary>
        /// 纵向死区(客户端舞台对象初始化时的默认值,证据 E-113):主角的世界 y 减镜头 y 落在 [160, 240) 时镜头不动,越出就跟着走,镜头 y 夹在 [0, 最大值] 之间。
        /// 只有带 [extended tile] 的地图(最大值 &gt; 0)才会纵向滚动。
        /// </summary>
        public const int DefaultCenterY = 200;
        public const int DefaultHalfHeight = 40;

        public int CenterX { get; }
        public int HalfWidth { get; }
        public int MinX { get; }
        public int MaxX { get; }

        /// <summary>镜头 y 的最大值(最小值恒为 0);没有扩展瓦片的地图为 0,镜头不纵向滚动。</summary>
        public int MaxY { get; }

        /// <summary>镜头在世界里的 x(屏幕左边缘对应的世界 x)。</summary>
        public double X { get; private set; }

        /// <summary>镜头在世界里的 y(屏幕上边缘对应的世界 y,不含地面常数)。</summary>
        public double Y { get; private set; }

        public int IntX => (int)Math.Floor(X);

        public int IntY => (int)Math.Floor(Y);

        public TownCamera(int minX, int maxX, double heroX, int centerX = DefaultCenterX, int halfWidth = DefaultHalfWidth, int maxY = 0)
        {
            MinX = minX;
            MaxX = maxX;
            CenterX = centerX;
            HalfWidth = halfWidth;
            MaxY = Math.Max(0, maxY);
            X = Clamp(heroX - centerX);
            Y = MaxY;   // 客户端载入地图时镜头 y 取最大值(0x007DC770),主角站稳后再按死区追上来
        }

        /// <summary>
        /// 进区域时的镜头(证据 E-095):先放在最小值,再按死区规则追一次主角(主角在屏幕死区的边缘,而不是屏幕中央)。
        /// 两次进区域(登录进赛丽亚房间、从房间走回城镇)的原版第一张截图上,镜头位置都与这个规则一致,与"居中再夹住"不一致。
        /// </summary>
        public static TownCamera AtArrival(int minX, int maxX, double heroX, int centerX = DefaultCenterX, int halfWidth = DefaultHalfWidth)
        {
            var camera = new TownCamera(minX, maxX, minX + centerX, centerX, halfWidth);
            camera.Follow(heroX);
            return camera;
        }

        /// <summary>主角移动之后调用:按死区规则更新镜头 x。</summary>
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

        /// <summary>主角移动之后调用:按死区规则更新镜头 x 与 y(y 只在 <see cref="MaxY"/> 大于 0 时才会动)。</summary>
        public void Follow(double heroX, double heroY)
        {
            Follow(heroX);
            if (MaxY <= 0)
            {
                return;
            }
            double screen = heroY - Y;
            if (screen < DefaultCenterY - DefaultHalfHeight)
            {
                Y = heroY - (DefaultCenterY - DefaultHalfHeight);
            }
            else if (screen >= DefaultCenterY + DefaultHalfHeight)
            {
                Y = heroY - (DefaultCenterY + DefaultHalfHeight);
            }
            Y = Y < 0 ? 0 : (Y > MaxY ? MaxY : Y);
        }

        /// <summary>
        /// 进房间:镜头 x 放在最小值、y 放在最大值(客户端载入地图时的初值),再按死区规则追一次主角。
        /// </summary>
        public void Restart(double heroX, double heroY)
        {
            X = MinX;
            Y = MaxY;
            Follow(heroX, heroY);
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

        // 技能动作:.chr 的 [etc motion] 里按需载入的几段动画(键是小写文件名),与当前正在播的那一段
        private sealed class EtcMotion
        {
            public AniFile Ani;
            public string Path;
            public AniTimeline Timeline;
        }

        private readonly Dictionary<string, EtcMotion> _etcMotions = new Dictionary<string, EtcMotion>(StringComparer.Ordinal);
        private EtcMotion _skillMotion;
        private int _skillWindupStage = -1;
        private int _skillWindupUntilFrame;
        private EtcMotion _skillThen;
        private double _skillSlideV0;
        private double _skillSlideAccel;
        private double _skillSlideV;
        private double _skillEndMs = -1.0;

        // 蓄力跳跃类技能(M45,证据 E-138):动画速度系数、脚本跳跃与命中窗口重武装的进度
        private double _skillRateScale = 1.0;
        private SkillHopPlan _hop;
        private double _hopStartX;
        private int _hopRiseEndMs;
        private int _hopLandMs;
        private int _hopNextRearmMs;
        private int _hopInterval;
        private int _hopRearmsMax;
        private int _hopRearmsDone;
        private bool _hopLanded;

        // 腾空悬停俯冲类技能(M55,证据 E-149):计划、悬停与俯冲的进度、放出来时的离地高度、水平速度;动画定住的帧;落地后的坐姿动画
        private SkillDivePlan _dive;
        private double _diveMs;
        private bool _diveStarted;
        private double _diveStartZ;
        private double _diveVx;
        private int _holdFrame = -1;
        private int _strikeFirstFrame;
        private AniFile _sit;
        private string _sitPath;
        private AniTimeline _sitTimeline;

        // 多段命中(证据 E-139):间隔 = 整段动画时长 / 次数的重武装计时器;与脚本跳跃的计时器互不相干
        private int _stageRearmInterval;
        private int _stageRearmMax;
        private int _stageRearmDone;
        private int _stageRearmNextMs = -1;

        // 受击:[damage motion 1]、[damage motion 2](没写就是 null,对应的受击不生效),当前播的那一段,硬直与击退的进度
        private AniFile _damage1;
        private string _damage1Path;
        private AniTimeline _damage1Timeline;
        private AniFile _damage2;
        private string _damage2Path;
        private AniTimeline _damage2Timeline;
        private AniFile _hurtAni;
        private string _hurtPath;
        private AniTimeline _hurtTimeline;
        private int _hurtCount;
        private double _hurtSpanMs;
        private double _hurtClockMs;
        private double _hurtHoldEndMs;
        private int _hurtPushDir;
        private double _hurtPushTotal;
        private double _hurtPushDone;
        private double _hurtPushMs;
        private double _hurtPushEaseMs;

        // 倒地:[down motion](没写就是 null,倒地不生效),外面的倒地状态机每帧摆过来的动画时钟(毫秒)
        private AniFile _down;
        private string _downPath;
        private AniTimeline _downTimeline;
        private double _downAniMs;

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

        // 后跳(BackStep):跳跃状态的另一种参数,没有蹲姿、水平速度恒定朝后、重力与起跳速度按跳跃速度属性 100 取、不能空中攻击
        private bool _backStep;
        private double _backStepVx;

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

        /// <summary>
        /// 增益附加体加在跳跃力上的数(E-141:客户端的"合计跳跃力"= 加和 × 倍率,属性增益把数加进加和里);没有增益时 0。
        /// 由外面的战斗会话每步按生效中的增益摆进来,起跳初速度 <see cref="JumpTakeoffSpeed"/> 用 <see cref="JumpPower"/> 加上它。后跳的起跳力是技能自己的参数,不受它影响。
        /// </summary>
        public double JumpPowerBonus { get; set; }

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

        /// <summary>起跳初速度(像素/秒),用 <see cref="TakeoffSpeed"/> 由跳跃力(加上增益 <see cref="JumpPowerBonus"/>)、重量与跳跃速度属性算出。</summary>
        public double JumpTakeoffSpeed => TakeoffSpeed(JumpPower + JumpPowerBonus, WeightStat, JumpSpeedStat);

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

        /// <summary>最近一次更新里,向左走被可走区域(或障碍)挡住了。</summary>
        public bool PushedLeft { get; private set; }

        /// <summary>最近一次更新里,向右走被可走区域(或障碍)挡住了。</summary>
        public bool PushedRight { get; private set; }

        /// <summary>最近一次更新里,有过向上走的动作(不管有没有被挡住)。</summary>
        public bool TriedUp { get; private set; }

        /// <summary>最近一次更新里,有过向下走的动作(不管有没有被挡住)。</summary>
        public bool TriedDown { get; private set; }

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

        /// <summary>
        /// 命中窗口重武装的累计次数(蓄力跳跃类技能:计时器每到点加 1,证据 E-138)。命中判定看到它变了,就清掉"这一击已打中的目标",
        /// 让同一个目标在新的窗口里再挨一下。其它技能永远不变。
        /// </summary>
        public long HitRearmNumber { get; private set; }

        /// <summary>这次脚本跳跃里已经重武装的次数(第一个窗口是 0);不在放脚本跳跃的技能时为 0。</summary>
        public int HopRearmsDone => Motion == HeroMotion.Skill && _hop != null ? _hopRearmsDone : 0;

        /// <summary>这次脚本跳跃一共的命中窗口数 N(第一个窗口加上 N−1 次重武装);不在放脚本跳跃的技能时为 0。</summary>
        public int HopHits => Motion == HeroMotion.Skill && _hop != null ? _hop.Hits : 0;

        /// <summary>这一段的多段命中计时器已经响过的次数(证据 E-139);没有多段命中、或不在放技能时为 0。</summary>
        public int StageRearmsDone => Motion == HeroMotion.Skill ? _stageRearmDone : 0;

        /// <summary>这一段的多段命中计时器最多响几次(证据 E-139);没有多段命中、或不在放技能时为 0。</summary>
        public int StageRearmsMax => Motion == HeroMotion.Skill ? _stageRearmMax : 0;

        /// <summary>脚本跳跃的落地计数(每落地一次加 1,永远递增);会话据此放落地的震屏。</summary>
        public int HopLandings { get; private set; }

        /// <summary>最近一次落地的那次跳跃的命中窗口数(落地那一步技能可能同时结束,所以单独记一份)。</summary>
        public int HopLandingHits { get; private set; }

        /// <summary>正在放脚本跳跃的技能而且还没落地。</summary>
        public bool HopAirborne => Motion == HeroMotion.Skill && _hop != null && !_hopLanded;

        /// <summary>正在放腾空悬停俯冲类技能的空中部分(悬停或俯冲,还没落地;证据 E-149)。</summary>
        public bool DiveAirborne => Motion == HeroMotion.Skill && _dive != null;

        /// <summary>空中部分里已经开始俯冲(悬停结束);不在空中部分时为 false。</summary>
        public bool IsDiving => DiveAirborne && _diveStarted;

        /// <summary>俯冲开始的累计次数(每次悬停结束开始俯冲加 1,永远递增);会话据此在那一刻放预载特效。</summary>
        public int DiveStarts { get; private set; }

        /// <summary>俯冲落地的累计次数(永远递增);会话据此在落地那一刻放冲击对象。悬停期间落地(放得太低)也算一次。</summary>
        public int DiveLandings { get; private set; }

        /// <summary>最近一次俯冲落地时,放出来那一刻的离地高度够不够放冲击对象(落地那一步技能可能同时结束,所以单独记一份)。</summary>
        public bool DiveLandingImpact { get; private set; }

        /// <summary>最近一次俯冲落地的技能编号。</summary>
        public int DiveLandingSkillId { get; private set; }

        /// <summary>最近一次开始俯冲的技能编号(开始俯冲那一步技能可能同时落地结束,所以单独记一份)。</summary>
        public int DiveStartSkillId { get; private set; }

        /// <summary>动画此刻是定在某一帧上的(腾空悬停俯冲的空中部分);那一帧的攻击框一直有效,命中判定每步都判(证据 E-149)。</summary>
        public bool FrameHeld => Motion == HeroMotion.Skill && _holdFrame >= 0;

        /// <summary>最近一次放出俯冲时的离地高度(像素)。</summary>
        public double DiveStartHeight => _diveStartZ;

        /// <summary>
        /// 这一击从动画第几帧起判命中:放腾空悬停俯冲类技能时动画是直接定在最后一帧的,前面的帧没有播过,不能算命中过(证据 E-149);其它情况是 0。
        /// 命中判定在新的一击开始时用它当"已判到的帧"的起点。
        /// </summary>
        public int StrikeFirstFrame => Motion == HeroMotion.Skill ? _strikeFirstFrame : 0;

        /// <summary>有坐姿动作([sit motion]):俯冲落地后才播得出坐姿,否则落地直接回到站立。</summary>
        public bool CanSit => _sit != null;

        /// <summary>是否正在挥击(连招、奔跑攻击、空中攻击或技能)。</summary>
        public bool IsAttacking => Motion == HeroMotion.Attack || Motion == HeroMotion.DashAttack || Motion == HeroMotion.JumpAttack || Motion == HeroMotion.Skill;

        /// <summary>
        /// 主角此刻腾得出手放技能:没有腾空、挥击(含放技能)、受伤、倒地。<see cref="BeginSkillMotion"/> 等放技能的入口共用这个前提;
        /// 界面的技能槽也据此变灰(证据 E-112:基线里挥击、放技能与被击中的那几帧,三个槽的图标与小字同时变灰)。
        /// </summary>
        public bool CanBeginSkill => !(IsAirborne || IsAttacking || IsHurt || IsDown);

        /// <summary>
        /// 先转向再出手:按着方向键放技能时,主角这一步先转向那个方向(与走路、跳跃、普攻按着方向键时先转身一致)。
        /// 只在腾得出手时转(<see cref="CanBeginSkill"/>),别的状态里不能转身。
        /// </summary>
        public void TurnTo(bool facingLeft)
        {
            if (CanBeginSkill)
            {
                FacingLeft = facingLeft;
            }
        }

        /// <summary>正在挥的这一击的种类;不在攻击时为 <see cref="HeroAttackKind.Normal"/>。</summary>
        public HeroAttackKind AttackKind => Motion == HeroMotion.DashAttack ? HeroAttackKind.Dash
            : (Motion == HeroMotion.JumpAttack ? HeroAttackKind.Jump : (Motion == HeroMotion.Skill ? HeroAttackKind.Skill : HeroAttackKind.Normal));

        /// <summary>正在放的技能编号(客户端的技能序号,例如上挑是 46);不在放技能时为 0。</summary>
        public int SkillId { get; private set; }

        /// <summary>
        /// 正在放的多段技能处于第几段(从 0 数):起手是第 0 段,每次 <see cref="ChainSkillMotion"/> 换一段加 1;不在放技能或单段技能时为 0(证据 E-129)。
        /// </summary>
        public int SkillStage => Motion == HeroMotion.Skill ? _skillStage : 0;

        private int _skillStage;

        /// <summary>技能还在起手阶段(先播普攻的第 <see cref="AttackStage"/> 段,播到指定帧才换成技能自己的动画)。</summary>
        public bool SkillWindingUp => Motion == HeroMotion.Skill && _skillWindupStage >= 0;

        /// <summary>当前技能动画的文件名(小写,形如 upperslashafter.ani);起手阶段与不在放技能时为 null。</summary>
        public string SkillMotionName => Motion == HeroMotion.Skill && _skillWindupStage < 0 && _skillMotion != null ? FileNameOf(_skillMotion.Path) : null;

        /// <summary>技能的水平滑行速度(像素/秒,沿朝向,已减速到此刻的值)。</summary>
        public double SkillSlideSpeed => Motion == HeroMotion.Skill && _skillWindupStage < 0 ? _skillSlideV : 0.0;

        private static string FileNameOf(string path)
        {
            return path.Substring(path.LastIndexOf('/') + 1).ToLowerInvariant();
        }

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
                    case HeroMotion.Skill:
                        path = _skillWindupStage >= 0 ? _attackPaths[_skillWindupStage] : _skillMotion.Path;
                        break;
                    default:
                        return null;
                }
                return path == null ? null : path.Substring(path.LastIndexOf('/') + 1).ToLowerInvariant();
            }
        }

        /// <summary>当前这一击已经过去的动画毫秒数(已乘播放倍率)。</summary>
        public long AttackAnimationMs => (long)(_motionMs * AttackAnimationRate * (Motion == HeroMotion.Skill ? _skillRateScale : 1.0));

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
            hero.LoadBodySize(assets, chrPath);
            hero.LoadDashMotions(assets, chrPath);
            hero.LoadJumpMotions(assets, chrPath);
            hero.LoadEtcMotions(assets, chrPath);
            hero.LoadHurtMotions(assets, chrPath);
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
            hero.LoadBodySize(assets, chrPath);
            hero.LoadDashMotions(assets, chrPath);
            hero.LoadJumpMotions(assets, chrPath);
            hero.LoadEtcMotions(assets, chrPath);
            hero.LoadHurtMotions(assets, chrPath);
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
                case HeroMotion.Skill:
                    Motion = HeroMotion.Rest;   // 技能要指明是哪一段动画,见 PlaceSkillMotion
                    break;
                case HeroMotion.Hurt:
                    Motion = HeroMotion.Rest;   // 受击带着硬直与击退的进度,要用 BeginHurt 进入
                    break;
                case HeroMotion.Down:
                    Motion = HeroMotion.Rest;   // 倒地的位置与时钟由倒地状态机给,要用 BeginDown 进入
                    break;
                default:
                    Motion = motion;
                    break;
            }
            _motionMs = motionMs < 0 ? 0 : motionMs;
            AttackStage = 0;
            SkillId = 0;
            _skillWindupStage = -1;
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
            PushedLeft = false;
            PushedRight = false;
            TriedUp = false;
            TriedDown = false;
        }

        /// <summary>
        /// 把主角瞬间挪到 (<paramref name="x"/>, <paramref name="y"/>),动作、朝向与高度都不动(脚本 [TELEPORT] 作用到主角,E-133)。
        /// 落点合不合法由调用方查。
        /// </summary>
        public void Teleport(double x, double y)
        {
            X = x;
            Y = y;
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
            _backStep = false;
            _backStepVx = 0.0;
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

        // ---- 技能动作(.chr 的 [etc motion],证据 E-076) ----

        private DnfAssets _etcAssets;
        private string[] _etcPaths = Array.Empty<string>();
        private string _buffPath;

        /// <summary>
        /// 记下 .chr 的 [etc motion](一串 .ani,技能与特殊动作用,按序号取)。动画本身在第一次用到时才读,所以这里很便宜。
        /// 没有这个标签就是空表。
        /// </summary>
        public void LoadEtcMotions(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = ReadChr(assets, chrPath);
            var paths = new List<string>();
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[etc motion]"))
                {
                    continue;
                }
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text))
                    {
                        paths.Add(PvfPath.ResolveRelativeTo(chrPath, v.Text));
                    }
                }
                break;
            }
            _etcAssets = assets;
            _etcPaths = paths.ToArray();
            _etcMotions.Clear();
            _buffPath = null;
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[buff motion]"))
                {
                    continue;
                }
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text))
                    {
                        _buffPath = PvfPath.ResolveRelativeTo(chrPath, v.Text);
                        break;
                    }
                }
                break;
            }
        }

        /// <summary>角色脚本有 [buff motion](增益施法的动作,引擎状态 0x11 用的那一个,E-141)。没有它,增益类技能放不出来。</summary>
        public bool HasBuffMotion => _buffPath != null;

        /// <summary>
        /// 放一个增益类技能(E-141):原地播 [buff motion],不滑行、不命中(这个动作里只有受击框,没有攻击框),播完回到站立。
        /// 和别的技能一样用 <see cref="HeroMotion.Skill"/> 表示"正在施法",技能编号是 <paramref name="skillId"/>;施法期间不能接别的技能。
        /// 引擎里增益只能从站立与奔跑状态发出,这里取 <see cref="CanBeginSkill"/>(没有腾空、挥击、受伤、倒地)。读不到动作时返回 false,不改变状态。
        /// 增益本身(附加体)由外面的战斗会话在施法正常结束那一刻挂上(状态结束钩子里才挂,中途被打断就没有)。
        /// </summary>
        public bool BeginBuffMotion(int skillId)
        {
            if (!CanBeginSkill)
            {
                return false;
            }
            EtcMotion m = EtcByPath(_buffPath);
            if (m == null)
            {
                return false;
            }
            StartSkill(skillId, -1, 0, m, 0.0, 0.0);
            return true;
        }

        /// <summary>[etc motion] 的条目数。</summary>
        public int EtcMotionCount => _etcPaths.Length;

        /// <summary>[etc motion] 第 <paramref name="index"/> 项的 PVF 路径;越界返回 null。</summary>
        public string EtcMotionPath(int index)
        {
            return (uint)index < (uint)_etcPaths.Length ? _etcPaths[index] : null;
        }

        /// <summary>按文件名(不分大小写,如 "hardattack.ani")找 [etc motion] 里的下标;没有返回 -1。技能按名字找动画,不依赖各角色的排列顺序(证据 E-076)。</summary>
        public int EtcMotionIndexOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return -1;
            }
            for (int i = 0; i < _etcPaths.Length; i++)
            {
                if (string.Equals(FileNameOf(_etcPaths[i]), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private EtcMotion EtcAt(int index)
        {
            if ((uint)index >= (uint)_etcPaths.Length || _etcAssets == null)
            {
                return null;
            }
            return EtcByPath(_etcPaths[index]);
        }

        private EtcMotion EtcByPath(string path)
        {
            if (_etcAssets == null || string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (_etcMotions.TryGetValue(path, out EtcMotion cached))
            {
                return cached;
            }
            AniFile ani = _etcAssets.LoadAni(path);
            EtcMotion made = ani == null || ani.Frames.Length == 0 ? null : new EtcMotion { Ani = ani, Path = path, Timeline = new AniTimeline(ani) };
            _etcMotions[path] = made;
            return made;
        }

        /// <summary>
        /// [etc motion] 第 <paramref name="index"/> 项第 <paramref name="frame"/> 帧自己的时长(动画毫秒,不按播放倍率折算);读不到动画或帧号越界为 0。
        /// 波系技能算蓄力用:施法时间减去第 1 到 3 帧的时长(证据 E-135)。
        /// </summary>
        public int EtcFrameDelayMs(int index, int frame)
        {
            EtcMotion m = EtcAt(index);
            return m == null ? 0 : (int)m.Timeline.FrameLengthMs(frame);
        }

        /// <summary>[etc motion] 第 <paramref name="index"/> 项的墙钟时长(毫秒,已按播放倍率折算,向上取整);读不到返回 0。</summary>
        public double EtcMotionDurationMs(int index)
        {
            EtcMotion m = EtcAt(index);
            if (m == null)
            {
                return 0.0;
            }
            double rate = AttackAnimationRate > 0.0 ? AttackAnimationRate : 1.0;
            return Math.Ceiling(m.Timeline.TotalMilliseconds / rate);
        }

        /// <summary>
        /// 直接放一个技能:播 [etc motion] 第 <paramref name="etcIndex"/> 项,沿朝向以 <paramref name="slideSpeed"/>(像素/秒)起滑,
        /// 加速度 <paramref name="slideAccel"/>(像素/秒²,通常为负),速度减到 0 为止。读不到动画时返回 false,不改变状态。
        /// 只在地面站立、行走、奔跑时能放。<paramref name="holdFirstFrameMs"/> 不小于 0 时,动画第 0 帧的停留换成这个毫秒数(施法蓄力,证据 E-135)。
        /// <paramref name="endFrame"/> 不小于 0 时,动画播到这一帧(从 0 数)的开头技能状态就结束、回到站立(证据 E-137:状态码在帧号到数时直接请求站立状态,
        /// 不等动画播完);小于 0 就播到动画结束。
        /// <paramref name="rearmWindows"/> 大于 0 时开多段命中计时器(证据 E-139):间隔 = 整段动画时长(按播放倍率折算)/ 这个数,最多响这么多次,
        /// 每次响就让命中名单重新武装(<see cref="HitRearmNumber"/> 加 1);0 表示没有。
        /// </summary>
        public bool BeginSkillMotion(int skillId, int etcIndex, double slideSpeed, double slideAccel, int holdFirstFrameMs = -1, int endFrame = -1,
            int rearmWindows = 0)
        {
            if (!CanBeginSkill)
            {
                return false;
            }
            EtcMotion m = WithHold(EtcAt(etcIndex), holdFirstFrameMs);
            if (m == null)
            {
                return false;
            }
            StartSkill(skillId, -1, 0, m, slideSpeed, slideAccel);
            _skillEndMs = EndMsOf(m, endFrame);
            StartStageRearm(m, rearmWindows);
            return true;
        }

        /// <summary>
        /// 开(或清除)这一段的多段命中计时器(证据 E-139):间隔 = 整段动画时长(按这一段的播放倍率折算,整数毫秒)/ <paramref name="windows"/>(整数除法),
        /// 最多响 <paramref name="windows"/> 次;计时器在动画开始后第一次更新时才起算(之后每次响铃都从"此刻"重新数一个间隔,与客户端计时器的"到点重置为现在"一致)。
        /// </summary>
        private void StartStageRearm(EtcMotion m, int windows)
        {
            _stageRearmDone = 0;
            _stageRearmNextMs = -1;
            if (windows <= 0 || m == null)
            {
                _stageRearmMax = 0;
                _stageRearmInterval = 0;
                return;
            }
            double rate = SkillRate;
            int total = (int)(m.Timeline.TotalMilliseconds * 100.0 / (rate * 100.0));
            _stageRearmMax = windows;
            _stageRearmInterval = total / windows;
        }

        /// <summary>多段命中这一步:<paramref name="e"/> 是这一段动画已经过去的墙钟毫秒;到点就重武装一次(一步最多一次),次数用完不再响。</summary>
        private void StepStageRearm(int e)
        {
            if (_stageRearmDone >= _stageRearmMax)
            {
                return;
            }
            if (_stageRearmNextMs < 0)
            {
                _stageRearmNextMs = e + _stageRearmInterval;
                return;
            }
            if (e >= _stageRearmNextMs)
            {
                _stageRearmDone++;
                _stageRearmNextMs = e + _stageRearmInterval;
                HitRearmNumber++;
            }
        }

        /// <summary>
        /// 动画 <paramref name="m"/> 播到第 <paramref name="endFrame"/> 帧开头的毫秒数(按攻击速度折算);<paramref name="endFrame"/> 小于 0、没有动画、
        /// 或动画根本没有这么多帧(帧号永远到不了)时返回 -1(播到结束)。
        /// </summary>
        private double EndMsOf(EtcMotion m, int endFrame)
        {
            if (m == null || endFrame < 0 || endFrame >= m.Timeline.FrameCount)
            {
                return -1.0;
            }
            return Math.Ceiling(m.Timeline.StartMs(endFrame) / SkillRate);
        }

        /// <summary>同一段动画换上"第 0 帧停留 <paramref name="holdFirstFrameMs"/> 毫秒"的时间线;不需要换(小于 0)或没有动画时原样返回。</summary>
        private static EtcMotion WithHold(EtcMotion m, int holdFirstFrameMs)
        {
            if (m == null || holdFirstFrameMs < 0)
            {
                return m;
            }
            return new EtcMotion { Ani = m.Ani, Path = m.Path, Timeline = new AniTimeline(m.Ani, holdFirstFrameMs) };
        }

        /// <summary>
        /// 放一个先挥普攻再换动画的技能(例如上挑):先播连招第 <paramref name="windupStage"/> 段,播到第 <paramref name="untilFrame"/> 帧
        /// (动画帧号,从 0 数)就换成 [etc motion] 第 <paramref name="thenEtcIndex"/> 项,换的时候开始滑行。
        /// 起手段的动画先播完而没到那一帧就直接结束。读不到动画时返回 false,不改变状态。
        /// </summary>
        public bool BeginSkillWindup(int skillId, int windupStage, int untilFrame, int thenEtcIndex, double slideSpeed, double slideAccel)
        {
            if (!CanBeginSkill || (uint)windupStage >= (uint)_attacks.Length)
            {
                return false;
            }
            EtcMotion then = EtcAt(thenEtcIndex);
            if (then == null)
            {
                return false;
            }
            StartSkill(skillId, windupStage, untilFrame, then, slideSpeed, slideAccel);
            return true;
        }

        /// <summary>
        /// 把连招第 <paramref name="stage"/> 段整段当成一个技能打出来(上挑:持短剑以外的武器时,上挑就是第三击的动画与攻击信息,证据 E-076):
        /// 朝向锁定、不接连招,播完回到站立。命中用这一段的攻击框与攻击信息(与普攻同一份)。
        /// 只在地面站立、行走、奔跑时能放;没有这一段时返回 false,不改变状态。
        /// </summary>
        public bool BeginSkillStrike(int skillId, int stage)
        {
            if (!CanBeginSkill || (uint)stage >= (uint)_attacks.Length)
            {
                return false;
            }
            StartSkill(skillId, stage, int.MaxValue, null, 0.0, 0.0);
            return true;
        }

        /// <summary>
        /// 多段技能进下一段(证据 E-129):正在放技能 <paramref name="skillId"/> 的主体阶段时,换成 [etc motion] 第 <paramref name="etcIndex"/> 项,
        /// 新动画从头播,按 <paramref name="slideSpeed"/>、<paramref name="slideAccel"/> 重新起滑,算新的一击(<see cref="SwingNumber"/> 加 1,
        /// 命中判定重新数帧、同一个目标又能被打一次),<see cref="SkillStage"/> 加 1。
        /// 不在放这个技能的主体阶段(没在放、放的是别的技能、还在起手阶段),或者读不到动画时返回 false,不改变状态。
        /// <paramref name="hop"/> 不为空时这一段是脚本跳跃(蓄力跳跃类技能,证据 E-138):位移由动画时间脚本化,命中窗口由计时器重武装;
        /// <paramref name="rateScale"/> 是这一段动画的变速系数(乘在攻击速度的播放倍率上),技能结束或换段时恢复 1。
        /// </summary>
        public bool ChainSkillMotion(int skillId, int etcIndex, double slideSpeed, double slideAccel, int holdFirstFrameMs = -1, int endFrame = -1,
            SkillHopPlan hop = null, double rateScale = 1.0, int rearmWindows = 0)
        {
            if (Motion != HeroMotion.Skill || SkillId != skillId || _skillWindupStage >= 0 || _skillMotion == null)
            {
                return false;
            }
            EtcMotion m = WithHold(EtcAt(etcIndex), holdFirstFrameMs);
            if (m == null)
            {
                return false;
            }
            _skillMotion = m;
            _skillThen = m;
            _skillRateScale = rateScale > 0.0 ? rateScale : 1.0;
            _skillEndMs = EndMsOf(m, endFrame);
            _skillSlideV0 = slideSpeed;
            _skillSlideAccel = slideAccel;
            _skillSlideV = slideSpeed;
            _motionMs = 0;
            _skillStage++;
            SwingNumber++;
            StartHop(m, hop);
            StartStageRearm(m, rearmWindows);
            return true;
        }

        /// <summary>
        /// 腾空悬停后俯冲类技能起手(证据 E-149,客户端状态 0x19 的起手函数与进入块):只在腾空的跳跃状态里放——起跳蹲姿(还没离地)、落地段、空中挥击、
        /// 后跳与别的状态都不行(返回 false,不改变状态)。放出来:换成技能动作(<see cref="HeroMotion.Skill"/>,新的一击),动画直接定在空中攻击动作的最后一帧
        /// (整个空中部分都是这一帧,命中从这一帧起判,<see cref="StrikeFirstFrame"/>),水平与竖直速度清零、记下此刻的离地高度;
        /// 之后每步由悬停(<see cref="SkillDivePlan.HoverMs"/>)、俯冲、落地、坐姿四个相位接着走。没有空中攻击动作时返回 false。
        /// </summary>
        public bool BeginSkillDive(int skillId, SkillDivePlan plan)
        {
            if (plan == null || Motion != HeroMotion.Jump || _backStep || _prepMs >= 0 || _landingMs >= 0 || _jumpAttack == null || _jumpAttackTimeline == null
                || _jumpAttack.Frames.Length == 0)
            {
                return false;
            }
            StartSkill(skillId, -1, 0, new EtcMotion { Ani = _jumpAttack, Path = _jumpAttackPath, Timeline = _jumpAttackTimeline }, 0.0, 0.0);
            _dive = plan;
            _diveMs = 0.0;
            _diveStarted = false;
            _diveVx = 0.0;
            _diveStartZ = Z;
            _vz = 0.0;
            _holdFrame = _jumpAttack.Frames.Length - 1;
            _strikeFirstFrame = _holdFrame;
            return true;
        }

        /// <summary>俯冲的水平速度(像素/秒,不带朝向的符号):计划里的引擎速度表的数乘移动速度属性的千分比(与后跳同一个换算,<see cref="BackStepSpeedX"/>);没有计划时为 0。</summary>
        public double DiveSpeedX => _dive == null ? 0.0 : _dive.SpeedX * (MoveSpeedStat > 0 ? MoveSpeedStat / 1000.0 : SpeedFactorX);

        /// <summary>俯冲的竖直速度(像素/秒,向下为负):计划里的数乘移动速度属性的千分比 / 1000;没有计划时为 0。</summary>
        public double DiveSpeedZ => _dive == null ? 0.0 : _dive.SpeedZ * (MoveSpeedStat > 0 ? MoveSpeedStat / 1000.0 : 1.0);

        /// <summary>
        /// 腾空悬停俯冲的空中部分走一步(证据 E-149):悬停期间水平不动、竖直只受重力(速度从 0 起);悬停结束那一刻(状态计时器到 <see cref="SkillDivePlan.HoverMs"/>)
        /// 水平速度设成 <see cref="DiveSpeedX"/>(沿朝向)、竖直速度一次设成 <see cref="DiveSpeedZ"/>,之后照常受重力;碰到地面就落地(<see cref="LandDive"/>)。
        /// 一步里跨过悬停结束或落地的时刻按步内精确时间拆开算。
        /// </summary>
        private void AdvanceDive(double deltaMs)
        {
            double left = deltaMs;
            if (!_diveStarted)
            {
                double toStart = Math.Max(0.0, _dive.HoverMs - _diveMs);
                double chunk = Math.Min(left, toStart);
                if (chunk > 0.0)
                {
                    _diveMs += chunk;
                    if (DiveFall(chunk, out double after))
                    {
                        LandDive(after + (left - chunk));
                        return;
                    }
                    left -= chunk;
                }
                if (_diveMs < _dive.HoverMs)
                {
                    return;                              // 还在悬停
                }
                StartDive();
            }
            if (left > 0.0)
            {
                _diveMs += left;
                if (DiveFall(left, out double after))
                {
                    LandDive(after);
                }
            }
        }

        /// <summary>悬停结束:开始俯冲(记一次,设水平与竖直速度,水平沿朝向)。</summary>
        private void StartDive()
        {
            _diveStarted = true;
            DiveStarts++;
            DiveStartSkillId = SkillId;
            _diveVx = (FacingLeft ? -1.0 : 1.0) * DiveSpeedX;
            _vz = DiveSpeedZ;
        }

        /// <summary>
        /// 俯冲的物理走 <paramref name="ms"/> 毫秒:水平按当前水平速度走(受可走区域与障碍限制),竖直按重力积分;这一段里触地就返回 true,
        /// 水平只走到触地的时刻,<paramref name="afterMs"/> 是触地之后这一段里剩下的毫秒数(触地时刻是 z0 + v0 t − g t² / 2 = 0 的正根)。
        /// </summary>
        private bool DiveFall(double ms, out double afterMs)
        {
            afterMs = 0.0;
            double seconds = ms / 1000.0;
            double g = JumpGravity;
            double z0 = Z, v0 = _vz;
            double z1 = z0 + v0 * seconds - 0.5 * g * seconds * seconds;
            bool lands = z1 <= 0.0;
            double moveSeconds = seconds;
            if (lands)
            {
                double touch = g > 0.0 ? (v0 + Math.Sqrt(v0 * v0 + 2.0 * g * Math.Max(z0, 0.0))) / g : seconds;
                moveSeconds = Math.Min(Math.Max(touch, 0.0), seconds);
                afterMs = (seconds - moveSeconds) * 1000.0;
            }
            if (_diveVx != 0.0 && moveSeconds > 0.0)
            {
                X = MoveAxis(X, _diveVx * moveSeconds, true);
            }
            if (lands)
            {
                Z = 0.0;
                _vz = 0.0;
                return true;
            }
            Z = z1;
            _vz = v0 - g * seconds;
            return false;
        }

        /// <summary>
        /// 俯冲落地(证据 E-149):记一次落地,放出来那一刻的离地高度(取整)不小于计划里的最小高度就记"能放冲击对象"(由会话放),
        /// 然后进入坐姿——动画换成 [sit motion] 从头播,播完回到站立;没有坐姿动作就直接回到站立。<paramref name="carryMs"/> 是落地之后这一步里剩下的时间,算在坐姿里。
        /// </summary>
        private void LandDive(double carryMs)
        {
            SkillDivePlan plan = _dive;
            DiveLandings++;
            DiveLandingImpact = (int)_diveStartZ >= plan.ImpactMinHeight;
            DiveLandingSkillId = SkillId;
            ClearDive();
            Z = 0.0;
            _vz = 0.0;
            if (_sit == null)
            {
                FinishSkill();
                return;
            }
            var sit = new EtcMotion { Ani = _sit, Path = _sitPath, Timeline = _sitTimeline };
            _skillMotion = sit;
            _skillThen = sit;
            _skillEndMs = -1.0;
            _skillSlideV0 = 0.0;
            _skillSlideAccel = 0.0;
            _skillSlideV = 0.0;
            _motionMs = 0.0;
            if (carryMs > 0.0)
            {
                AdvanceSkill(carryMs);
            }
        }

        /// <summary>后跳时"跳跃速度属性"的取值:客户端的后跳参数(模式 1)把它当 100 算(证据 E-076)。</summary>
        public const int BackStepJumpSpeedStat = 100;

        /// <summary>后跳的重力加速度(像素/秒²):跳跃速度属性取 <see cref="BackStepJumpSpeedStat"/> 时的重力。</summary>
        public double BackStepGravity => BackStepJumpSpeedStat * (EngineGroundGravity - MapGravityAdjust) / 100.0;

        /// <summary>
        /// 后跳的水平速度(像素/秒)= 奔跑速度表的水平值 × 角色脚本 [move speed](千分比,证据 E-076:副本里量到的后跳水平速度与它吻合,
        /// 而城镇里标定的行走系数 <see cref="DefaultSpeedFactorX"/> 套上去快了近一倍);没有这个属性时退回 <see cref="SpeedFactorX"/>。
        /// </summary>
        public double BackStepSpeedX => EngineDashX * (MoveSpeedStat > 0 ? MoveSpeedStat / 1000.0 : SpeedFactorX);

        /// <summary>正在后跳(跳跃状态的特殊参数,见 <see cref="BeginBackStep"/>)。</summary>
        public bool IsBackStep => _backStep && Motion == HeroMotion.Jump;

        /// <summary>
        /// 后跳(技能 169,证据 E-076):客户端把它做成跳跃状态的一组特殊参数(阶段 1 腾空、模式 1 固定方向,第三个分量是 .skl 的 [static data] 第 0 个数,即起跳力 <paramref name="power"/>):
        /// 没有起跳前的蹲姿,一放就腾空;起跳速度与重力都按"跳跃速度属性取 <see cref="BackStepJumpSpeedStat"/>"算;
        /// 水平方向沿朝向的反方向匀速退(<see cref="BackStepSpeedX"/>),深度方向不动,腾空中不改方向、不能空中攻击;动画停在下落帧(最后一个停留帧);
        /// 落地直接回到站立(基线里落地后下一张截图就是站立动画的第一帧,没有落地段)。
        /// 只在地面站立、行走、奔跑时能放;没有跳跃动作时返回 false,不改变状态。
        /// </summary>
        public bool BeginBackStep(int skillId, double power)
        {
            if (_jump == null || !CanBeginSkill)
            {
                return false;
            }
            Motion = HeroMotion.Jump;
            SkillId = skillId;
            AttackStage = 0;
            _motionMs = 0;
            _attackQueued = false;
            _prepMs = -1;
            _airMs = 0;
            _descendMs = 0;
            _descending = false;
            _landingMs = -1;
            _airAttackUsed = true;
            _backStep = true;
            _backStepVx = (FacingLeft ? 1.0 : -1.0) * BackStepSpeedX;
            _vz = TakeoffSpeed(power, WeightStat, BackStepJumpSpeedStat);
            Z = 0;
            return true;
        }

        /// <summary>后跳落地:直接回到站立。</summary>
        private void FinishBackStep()
        {
            Motion = HeroMotion.Rest;
            SkillId = 0;
            Z = 0;
            _vz = 0;
            _descending = false;
            _landingMs = -1;
            _backStep = false;
            _backStepVx = 0.0;
            _motionMs = 0;
        }

        private void StartSkill(int skillId, int windupStage, int untilFrame, EtcMotion motion, double slideSpeed, double slideAccel)
        {
            Motion = HeroMotion.Skill;
            SkillId = skillId;
            _skillStage = 0;
            AttackStage = windupStage < 0 ? 0 : windupStage;
            _skillWindupStage = windupStage;
            _skillWindupUntilFrame = untilFrame;
            _skillMotion = windupStage < 0 ? motion : null;
            _skillThen = motion;
            _skillSlideV0 = slideSpeed;
            _skillSlideAccel = slideAccel;
            _skillSlideV = windupStage < 0 ? slideSpeed : 0.0;
            _skillEndMs = -1.0;
            _skillRateScale = 1.0;
            _hop = null;
            _stageRearmMax = 0;
            _stageRearmDone = 0;
            _motionMs = 0;
            _attackQueued = false;
            ClearDive();
            SwingNumber++;
        }

        /// <summary>清掉腾空悬停俯冲的状态(换技能、技能结束、摆姿势时);计数与最近一次落地的记录保留。</summary>
        private void ClearDive()
        {
            _dive = null;
            _diveMs = 0.0;
            _diveStarted = false;
            _diveVx = 0.0;
            _holdFrame = -1;
            _strikeFirstFrame = 0;
        }

        /// <summary>摆成"正在放技能的主体阶段,动画是 [etc motion] 第 <paramref name="etcIndex"/> 项,已播放 <paramref name="motionMs"/> 毫秒(按墙钟)"。读不到动画时摆成站立。</summary>
        public void PlaceSkillMotion(double x, double y, bool facingLeft, int skillId, int etcIndex, long motionMs)
        {
            PlaceSkillMotion(x, y, facingLeft, skillId, EtcAt(etcIndex), motionMs);
        }

        /// <summary>同 <see cref="PlaceSkillMotion(double, double, bool, int, int, long)"/>,动画直接按 PVF 路径给(分析用:在全部动画里找与截图吻合的那一个)。</summary>
        public void PlaceSkillMotion(double x, double y, bool facingLeft, int skillId, string aniPath, long motionMs)
        {
            PlaceSkillMotion(x, y, facingLeft, skillId, EtcByPath(aniPath), motionMs);
        }

        /// <summary>同上,按帧号给:摆成这个动画的第 <paramref name="frame"/> 帧刚开始(分析用)。</summary>
        public void PlaceSkillFrame(double x, double y, bool facingLeft, int skillId, string aniPath, int frame)
        {
            EtcMotion m = EtcByPath(aniPath);
            long ms = m == null ? 0 : (long)Math.Ceiling(m.Timeline.StartMs(Math.Max(0, Math.Min(frame, m.Timeline.FrameCount - 1))) * (AttackAnimationRate > 0.0 ? 1.0 / AttackAnimationRate : 1.0));
            PlaceSkillMotion(x, y, facingLeft, skillId, m, ms);
        }

        private void PlaceSkillMotion(double x, double y, bool facingLeft, int skillId, EtcMotion m, long motionMs)
        {
            if (m == null)
            {
                Place(x, y, facingLeft, HeroMotion.Rest, motionMs);
                return;
            }
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = HeroMotion.Skill;
            SkillId = skillId;
            _skillStage = 0;
            AttackStage = 0;
            _skillWindupStage = -1;
            _skillMotion = m;
            _skillThen = m;
            _skillEndMs = -1.0;
            _skillSlideV0 = 0;
            _skillSlideAccel = 0;
            _skillSlideV = 0;
            _skillRateScale = 1.0;
            _hop = null;
            ClearDive();
            _stageRearmMax = 0;
            _stageRearmDone = 0;
            _motionMs = motionMs < 0 ? 0 : motionMs;
            _attackQueued = false;
            ResetAir();
        }

        /// <summary>
        /// 技能这一步:起手阶段播普攻动画,播到指定帧就换成技能自己的动画(新动画从头播,换动画那一步不带余量);
        /// 主体阶段沿朝向滑行,速度按 v(t) = max(0, v0 + a·t) 变化,受可走区域与障碍限制;动画播完回到站立。
        /// </summary>
        private void AdvanceSkill(double deltaMs)
        {
            if (_dive != null)
            {
                AdvanceDive(deltaMs);
                return;
            }
            if (_skillWindupStage >= 0)
            {
                _motionMs += deltaMs;
                int frame = _attackTimelines[_skillWindupStage].FrameAt(AttackAnimationMs);
                if (frame >= _skillWindupUntilFrame)
                {
                    _skillMotion = _skillThen;
                    _skillWindupStage = -1;
                    _skillSlideV = _skillSlideV0;
                    _motionMs = 0;
                    SwingNumber++;
                    return;
                }
                if (_motionMs >= AttackDurationMs(_skillWindupStage))
                {
                    FinishSkill();
                }
                return;
            }
            double from = _motionMs;
            _motionMs += deltaMs;
            double duration = EtcDurationMs(_skillMotion);
            if (_skillEndMs >= 0.0 && _skillEndMs < duration)
            {
                duration = _skillEndMs;    // 状态码在帧号到数时直接请求站立状态,不等动画播完(证据 E-137)
            }
            double t0 = Math.Min(from, duration);
            double t1 = Math.Min(_motionMs, duration);
            if (t1 > t0 && _skillSlideV0 != 0.0)
            {
                double distance = SlideDistance(_skillSlideV0, _skillSlideAccel, t0, t1);
                X = MoveAxis(X, (FacingLeft ? -1.0 : 1.0) * distance, true);
            }
            _skillSlideV = SlideSpeedAt(_skillSlideV0, _skillSlideAccel, t1);
            if (_hop != null)
            {
                StepHop((int)_motionMs);
            }
            if (_stageRearmMax > 0)
            {
                StepStageRearm((int)_motionMs);
            }
            if (_motionMs >= duration)
            {
                FinishSkill();
            }
        }

        /// <summary>
        /// 开始(或清除)脚本跳跃(证据 E-138):记下起跳位置,把动画的关键帧折成墙钟毫秒(按这一段的播放倍率,与客户端的"帧起始时刻按动画速度折算"一致),
        /// 排好命中窗口的重武装计时:从命中起始帧开始,每隔 (命中结束帧 − 命中起始帧) / N 响一次,共 N − 1 次。
        /// </summary>
        private void StartHop(EtcMotion m, SkillHopPlan hop)
        {
            _hop = hop;
            _hopLanded = false;
            _hopRearmsDone = 0;
            _hopRearmsMax = 0;
            if (hop == null)
            {
                return;
            }
            _hopStartX = X;
            double rate = SkillRate;
            _hopRiseEndMs = HopFrameMs(m, hop.RiseFrame, rate);
            _hopLandMs = HopFrameMs(m, hop.LandFrame, rate);
            int hitStart = HopFrameMs(m, hop.HitStartFrame, rate);
            int hitEnd = HopFrameMs(m, hop.HitEndFrame, rate);
            int hits = hop.Hits < 1 ? 1 : hop.Hits;
            _hopInterval = (hitEnd - hitStart) / hits;
            _hopRearmsMax = hits - 1;
            _hopNextRearmMs = hitStart + _hopInterval;
        }

        private static int HopFrameMs(EtcMotion m, int frame, double rate)
        {
            return (int)(m.Timeline.StartMs(frame) * 100.0 / (rate * 100.0));
        }

        /// <summary>
        /// 脚本跳跃这一步(证据 E-138),<paramref name="e"/> 是这一段动画已经过去的墙钟毫秒:
        /// 先看重武装计时器(到点就让命中判定重新来一轮,一步最多响一次),再按时间摆位置——
        /// 上升段(到上升结束帧开头)水平走全程的 <see cref="SkillHopPlan.NearShare"/>、高度走缓出弧到最大高度;
        /// 下落段(到落地帧开头)走完余下的水平距离、高度按缓入弧回到 0;落地那一步高度归零、记一次落地,之后不再摆位置。
        /// 水平位置受可走区域与障碍限制(客户端取带碰撞的位置),高度是整数像素。
        /// </summary>
        private void StepHop(int e)
        {
            if (_hopRearmsDone < _hopRearmsMax && e >= _hopNextRearmMs)
            {
                _hopRearmsDone++;
                _hopNextRearmMs += _hopInterval;
                HitRearmNumber++;
            }
            if (_hopLanded)
            {
                return;
            }
            int near = (int)((float)_hop.Distance * SkillHopPlan.NearShare);
            int dx;
            int z;
            if (e < _hopRiseEndMs)
            {
                dx = SkillHopPlan.IntLerp(0, near, e, _hopRiseEndMs);
                z = SkillHopPlan.EasedLerp(0, _hop.Height, e, _hopRiseEndMs, true);
            }
            else if (e < _hopLandMs)
            {
                dx = SkillHopPlan.IntLerp(near, _hop.Distance, e - _hopRiseEndMs, _hopLandMs - _hopRiseEndMs);
                z = SkillHopPlan.EasedLerp(_hop.Height, 0, e - _hopRiseEndMs, _hopLandMs - _hopRiseEndMs, false);
            }
            else
            {
                Z = 0;
                _hopLanded = true;
                HopLandingHits = _hop.Hits;
                HopLandings++;
                return;
            }
            double target = _hopStartX + (FacingLeft ? -dx : dx);
            X = MoveAxis(X, target - X, true);
            Z = z;
        }

        private double EtcDurationMs(EtcMotion m)
        {
            return Math.Ceiling(m.Timeline.TotalMilliseconds / SkillRate);
        }

        /// <summary>放技能时动画的播放倍率:攻击速度的倍率再乘技能自己给的系数(蓄力跳跃类技能的跳跃段按蓄力档位变速,证据 E-138)。</summary>
        private double SkillRate => (AttackAnimationRate > 0.0 ? AttackAnimationRate : 1.0) * _skillRateScale;

        private void FinishSkill()
        {
            if (_hop != null)
            {
                Z = 0;               // 脚本跳跃的技能结束时一定落回地面(状态提前结束的保险)
                _hop = null;
            }
            if (_dive != null)
            {
                Z = 0;               // 悬停俯冲的空中部分被提前结束时同样落回地面(保险)
                _vz = 0;
            }
            ClearDive();
            Motion = HeroMotion.Rest;
            AttackStage = 0;
            SkillId = 0;
            _skillStage = 0;
            _skillWindupStage = -1;
            _skillMotion = null;
            _skillSlideV = 0.0;
            _skillRateScale = 1.0;
            _stageRearmMax = 0;
            _stageRearmDone = 0;
            _attackQueued = false;
            _motionMs = 0;
        }

        /// <summary>滑行速度 v(t) = max(0, v0 + a·t)(t 为毫秒,a 为像素/秒²,v 为像素/秒);v0 与 a 同向减速到 0 就停住,不会反向。</summary>
        public static double SlideSpeedAt(double v0, double accel, double ms)
        {
            double v = v0 + accel * ms / 1000.0;
            if (v0 >= 0.0)
            {
                return v < 0.0 ? 0.0 : v;
            }
            return v > 0.0 ? 0.0 : v;
        }

        /// <summary>从 <paramref name="ms0"/> 到 <paramref name="ms1"/> 毫秒之间滑行的距离(像素,带符号,与 v0 同号),速度减到 0 就不再增加。</summary>
        public static double SlideDistance(double v0, double accel, double ms0, double ms1)
        {
            if (ms1 <= ms0)
            {
                return 0.0;
            }
            if (accel != 0.0 && (v0 > 0.0) == (accel < 0.0) && v0 != 0.0)
            {
                double stopMs = -v0 / accel * 1000.0;   // 速度减到 0 的时刻
                if (ms0 >= stopMs)
                {
                    return 0.0;
                }
                if (ms1 > stopMs)
                {
                    ms1 = stopMs;
                }
            }
            double s0 = ms0 / 1000.0, s1 = ms1 / 1000.0;
            return v0 * (s1 - s0) + 0.5 * accel * (s1 * s1 - s0 * s0);
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

        /// <summary>占位盒宽的缺省值(像素):角色脚本没有可读的 [width] 时的兜底;真实的宽读自角色脚本(<see cref="LoadBodySize"/>,证据 E-074)。</summary>
        public const int DefaultBodyWidth = 40;

        /// <summary>占位盒深的缺省值(像素):角色脚本没有可读的 [width] 时的兜底;真实的深读自角色脚本(<see cref="LoadBodySize"/>,证据 E-074)。</summary>
        public const int DefaultBodyDepth = 10;

        /// <summary>
        /// 占位盒的宽(横向,像素):.chr 的 [width] 第一个数。客户端用它做进房间的落点与瓦片格足迹测试(证据 E-074);
        /// 以脚下原点为中心,左右各取一半。没有读过 [width] 时为 <see cref="DefaultBodyWidth"/>。
        /// </summary>
        public int BodyWidth { get; private set; } = DefaultBodyWidth;

        /// <summary>占位盒的深(纵深方向,像素):.chr 的 [width] 第二个数,上下各取一半。没有读过 [width] 时为 <see cref="DefaultBodyDepth"/>。</summary>
        public int BodyDepth { get; private set; } = DefaultBodyDepth;

        /// <summary>读 .chr 的 [width](两个整数:宽与深);没有这个标签或数不够两个就保持缺省值。</summary>
        public void LoadBodySize(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            BodyWidth = DefaultBodyWidth;
            BodyDepth = DefaultBodyDepth;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[width]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 2 && n[0] >= 0 && n[1] >= 0)
                    {
                        BodyWidth = n[0];
                        BodyDepth = n[1];
                    }
                    break;
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
        /// 读 .chr 的 [damage motion 1] 与 [damage motion 2](相对角色脚本所在目录)。两个都没有时 <see cref="CanBeHurt"/> 为假。
        /// 同时读 [down motion](倒地,<see cref="CanBeKnockedDown"/>;证据 E-105)。
        /// </summary>
        public void LoadHurtMotions(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = ReadChr(assets, chrPath);
            LoadMotion(assets, chrPath, doc, "[damage motion 1]", out _damage1, out _damage1Path, out _damage1Timeline);
            LoadMotion(assets, chrPath, doc, "[damage motion 2]", out _damage2, out _damage2Path, out _damage2Timeline);
            LoadMotion(assets, chrPath, doc, "[down motion]", out _down, out _downPath, out _downTimeline);
        }

        /// <summary>有受击动作([damage motion 1] 或 [damage motion 2])。</summary>
        public bool CanBeHurt => _damage1 != null || _damage2 != null;

        /// <summary>正在受击硬直里。</summary>
        public bool IsHurt => Motion == HeroMotion.Hurt;

        /// <summary>这一次受击硬直的总毫秒数;不在受击中时是上一次的值。</summary>
        public double HurtSpanMs => _hurtSpanMs;

        /// <summary>这一次受击硬直已经过去的毫秒数(命中停顿里也在走,见 <see cref="AddHurtClock"/>)。</summary>
        public double HurtClockMs => _hurtClockMs;

        /// <summary>从开始玩起受击的次数(每次 <see cref="BeginHurt"/> 成功加 1)。</summary>
        public int HurtCount => _hurtCount;

        /// <summary>
        /// 被打中:放受击动作(第一次放 [damage motion 1],之后与 [damage motion 2] 交替),硬直 <paramref name="stunMs"/> 毫秒,
        /// 期间往 <paramref name="pushToLeft"/> 的方向被推 <paramref name="pushPixels"/> 像素(先快后慢,<paramref name="pushMs"/> 毫秒内推完,走不过可走区域与障碍)。
        /// 正在挥击、放技能、奔跑里被打会被打断;已经在受击里再被打,换另一段动作、硬直与击退重新计时(与怪物挨打一致)。
        /// 离地(z &gt; 0)、倒地中或者没有受击动作时什么也不做并返回 false(腾空被打走倒地流程,见 <see cref="BeginDown"/>,E-105);
        /// 跳跃的蹲姿与落地硬直还在地面上(z = 0),被打会打断它们、回到站立。受击不转向:朝向保持不变。
        /// </summary>
        public bool BeginHurt(double stunMs, double pushPixels, double pushMs, bool pushToLeft)
        {
            if (((IsAirborne || Motion == HeroMotion.Skill) && Z > 0) || IsDown || !CanBeHurt)
            {
                return false;           // 脚本跳跃的技能在空中时也一样(只有倒地流程能打断,E-138)
            }
            if (IsAirborne)
            {
                ResetAir();         // 跳跃的蹲姿与落地硬直还在地面上(z = 0):被打断,回到地面状态
            }
            bool first = _hurtCount++ % 2 == 0;
            if ((first && _damage1 != null) || _damage2 == null)
            {
                _hurtAni = _damage1;
                _hurtPath = _damage1Path;
                _hurtTimeline = _damage1Timeline;
            }
            else
            {
                _hurtAni = _damage2;
                _hurtPath = _damage2Path;
                _hurtTimeline = _damage2Timeline;
            }
            Motion = HeroMotion.Hurt;
            _motionMs = 0;
            AttackStage = 0;
            SkillId = 0;
            _skillWindupStage = -1;
            _attackQueued = false;
            _dashTrigger = false;
            _hurtSpanMs = stunMs < 0 ? 0 : stunMs;
            _hurtClockMs = 0;
            _hurtHoldEndMs = 0;
            _hurtPushDir = pushToLeft ? -1 : 1;
            _hurtPushTotal = pushPixels < 0 ? 0 : pushPixels;
            _hurtPushDone = 0;
            _hurtPushMs = 0;
            _hurtPushEaseMs = pushMs;
            return true;
        }

        /// <summary>异常状态把主角留在受击姿势里的截止时刻(受击秒表上的毫秒数,见 <see cref="HoldHurt"/>);没有被留住是 0。</summary>
        public double HurtHoldEndMs => _hurtHoldEndMs;

        /// <summary>
        /// 异常状态(眩晕、冰冻等,M43)把主角留在受击姿势里直到"此刻起 <paramref name="spanMs"/> 毫秒之后":和 <see cref="BeginHurt"/> 给的硬直各算各的,
        /// 两个都到点才回到站立。每次调用重新定,所以可以缩短(被摇脱、状态提前结束);<paramref name="spanMs"/> 不是正数就放开(返回 false)。
        /// 已经在受击里不重放动作、不改击退;不在受击里就开一次零硬直、不带击退的受击(<see cref="BeginHurt"/>,朝向 <paramref name="pushToLeft"/> 只用来分左右)。
        /// 离地、倒地中或没有受击动作时什么也不做并返回 false(落地、起身之后下一次调用再接上)。新的一击(<see cref="BeginHurt"/>)会放开它,调用方随后再留。
        /// </summary>
        public bool HoldHurt(double spanMs, bool pushToLeft)
        {
            if (spanMs <= 0)
            {
                _hurtHoldEndMs = 0;
                return false;
            }
            if (Motion == HeroMotion.Hurt)
            {
                _hurtHoldEndMs = _hurtClockMs + spanMs;
                return true;
            }
            if (!BeginHurt(0, 0, 0, pushToLeft))
            {
                return false;
            }
            _hurtHoldEndMs = spanMs;       // 刚开始,秒表是 0
            return true;
        }

        /// <summary>立刻结束受击硬直、回到站立(复活用:没有倒地动作的占位死亡会一直停在受击姿势上)。不在受击里时什么也不做。</summary>
        public void EndHurt()
        {
            if (Motion != HeroMotion.Hurt)
            {
                return;
            }
            Motion = HeroMotion.Rest;
            _motionMs = 0;
            _hurtHoldEndMs = 0;
        }

        /// <summary>
        /// 命中停顿里主角不推进(<see cref="Update(double, int, int, bool, bool)"/> 不被调用),但硬直是一块不被停顿冻结的秒表(与怪物一样,证据 E-080):
        /// 停顿期间调用方用它把这段时间记进硬直。不在受击里时什么也不做。
        /// </summary>
        public void AddHurtClock(double ms)
        {
            if (Motion == HeroMotion.Hurt && ms > 0)
            {
                _hurtClockMs += ms;
            }
        }

        /// <summary>推进受击:动画播放、被击退(先快后慢)、硬直时间到就回到站立。</summary>
        private void AdvanceHurt(double deltaMs)
        {
            _hurtClockMs += deltaMs;
            _motionMs += deltaMs;
            if (_hurtPushDone < _hurtPushTotal)
            {
                _hurtPushMs += deltaMs;
                double t = _hurtPushEaseMs <= 0 ? 1.0 : Math.Min(1.0, _hurtPushMs / _hurtPushEaseMs);
                double target = _hurtPushTotal * (1.0 - (1.0 - t) * (1.0 - t));
                double step = target - _hurtPushDone;
                if (step > 0)
                {
                    _hurtPushDone = target;
                    X = MoveAxis(X, _hurtPushDir * step, true);
                    // 被打退撞上出口的碰撞体或边界不算"走向出口":出口只由主角自己的移动触发
                    PushedInto = null;
                    PushedUp = false;
                    PushedDown = false;
                    PushedLeft = false;
                    PushedRight = false;
                }
            }
            if (_hurtClockMs >= _hurtSpanMs && _hurtClockMs >= _hurtHoldEndMs)
            {
                Motion = HeroMotion.Rest;
                _motionMs = 0;
                _hurtHoldEndMs = 0;
            }
        }

        // ---- 倒地(.chr 的 [down motion],证据 E-079、E-082、E-083、E-105) ----

        /// <summary>有倒地动作([down motion])。没有时被打中只走普通受击(与旧行为一致)。</summary>
        public bool CanBeKnockedDown => _down != null;

        /// <summary>倒地动画(没有时为 null)。倒地状态机据它的长停留帧识别"最高点换帧"与"躺着的那一帧"。</summary>
        public AniFile DownAni => _down;

        /// <summary>正在倒地里(腾空、颠一下、躺着;已死的主角躺着不动也算)。</summary>
        public bool IsDown => Motion == HeroMotion.Down;

        /// <summary>倒地动画的时钟(毫秒);不在倒地里是最近一次的值。</summary>
        public double DownAnimationMs => _downAniMs;

        /// <summary>从开始玩起进入倒地的次数(每次 <see cref="BeginDown"/> 成功加 1;倒地中再被击飞不算新的一次)。</summary>
        public int DownCount { get; private set; }

        /// <summary>
        /// 进入倒地:打断当前的挥击、技能、奔跑、受击与跳跃(离地高度保留,由倒地状态机接着算),放倒地动画。
        /// 没有倒地动作时什么也不做并返回 false。已经在倒地里时不重新开始,返回 true。位置、离地高度与动画时钟之后由
        /// <see cref="SetDownPose"/> 一帧一帧给。
        /// </summary>
        public bool BeginDown()
        {
            if (_down == null)
            {
                return false;
            }
            if (Motion == HeroMotion.Down)
            {
                return true;
            }
            double z = Z;
            Motion = HeroMotion.Down;
            _motionMs = 0;
            AttackStage = 0;
            SkillId = 0;
            _skillWindupStage = -1;
            _attackQueued = false;
            _dashTrigger = false;
            ResetAir();
            ClearDive();             // 俯冲的空中部分被击倒打断:高度交给倒地流程,不再自己走物理(E-149)
            Z = z;
            _downAniMs = 0;
            DownCount++;
            return true;
        }

        /// <summary>
        /// 把倒地中的主角摆到倒地状态机算出来的位置:水平位置 <paramref name="x"/>、离地高度 <paramref name="z"/>、朝向与倒地动画的时钟。
        /// 不在倒地里时什么也不做。
        /// </summary>
        public void SetDownPose(double x, double z, bool facingLeft, double animationMs)
        {
            if (Motion != HeroMotion.Down)
            {
                return;
            }
            X = x;
            Z = z < 0 ? 0 : z;
            FacingLeft = facingLeft;
            _downAniMs = animationMs < 0 ? 0 : animationMs;
        }

        /// <summary>起身:落回地面回到站立。不在倒地里时什么也不做。</summary>
        public void EndDown()
        {
            if (Motion != HeroMotion.Down)
            {
                return;
            }
            Motion = HeroMotion.Rest;
            _motionMs = 0;
            Z = 0;
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
            LoadMotion(assets, chrPath, doc, "[sit motion]", out _sit, out _sitPath, out _sitTimeline);   // 俯冲落地后的坐姿(E-149)
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
            PushedLeft = false;
            PushedRight = false;
            TriedUp = false;
            TriedDown = false;
            TrackTap(deltaMs, dirX);
            switch (Motion)
            {
                case HeroMotion.Attack:
                    AdvanceAttack(deltaMs, attackPressed);
                    return;
                case HeroMotion.DashAttack:
                    AdvanceDashAttack(deltaMs);
                    return;
                case HeroMotion.Skill:
                    AdvanceSkill(deltaMs);
                    return;
                case HeroMotion.Hurt:
                    AdvanceHurt(deltaMs);
                    return;
                case HeroMotion.Down:
                    return;                       // 倒地的运动由外面的倒地状态机算(SetDownPose);倒地期间不接受操作
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
            if (dirX != 0 && Motion == HeroMotion.Jump && !_backStep)
            {
                FacingLeft = dirX < 0;
            }
            bool diagonal = dirX != 0 && dirY != 0;
            double vx = dirX * (diagonal ? EngineWalkDiagonalX : EngineWalkX) * SpeedFactorX;
            double vy = dirY * (diagonal ? EngineAirDiagonalY : EngineAirY) * SpeedFactorY;
            if (_backStep)
            {
                vx = _backStepVx;   // 后跳:方向在起跳时定下,腾空中不受方向键影响
                vy = 0.0;
            }
            X = MoveAxis(X, vx * seconds, true);
            Y = MoveAxis(Y, vy * seconds, false);
            double g = _backStep ? BackStepGravity : JumpGravity;
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
                if (_backStep)
                {
                    FinishBackStep();
                    return;
                }
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
        /// 被挡住的那一步会记下:水平方向被挡住记在 <see cref="PushedLeft"/> 或 <see cref="PushedRight"/>(被障碍挡住时还记下是哪个,<see cref="PushedInto"/>),
        /// 竖直方向被挡住记在 <see cref="PushedUp"/> 或 <see cref="PushedDown"/>。
        /// 主角出生时就压在障碍里(例如摆放不当)的话,这一步忽略障碍,让他能走出来。
        /// </summary>
        private double MoveAxis(double from, double delta, bool horizontal)
        {
            if (delta == 0 || _walk.IsUnrestricted)
            {
                return from + delta;
            }
            if (!horizontal)
            {
                if (delta < 0)
                {
                    TriedUp = true;
                }
                else
                {
                    TriedDown = true;
                }
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
                if (sign < 0)
                {
                    PushedLeft = true;
                }
                else
                {
                    PushedRight = true;
                }
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
                    case HeroMotion.Skill:
                        return _skillWindupStage >= 0 ? _attacks[_skillWindupStage] : _skillMotion.Ani;
                    case HeroMotion.Jump:
                        return _jump;
                    case HeroMotion.JumpAttack:
                        return _jumpAttack;
                    case HeroMotion.Hurt:
                        return _hurtAni;
                    case HeroMotion.Down:
                        return _down;
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
                    case HeroMotion.Skill:
                        return _skillWindupStage >= 0 ? _attackPaths[_skillWindupStage] : _skillMotion.Path;
                    case HeroMotion.Jump:
                        return _jumpPath;
                    case HeroMotion.JumpAttack:
                        return _jumpAttackPath;
                    case HeroMotion.Hurt:
                        return _hurtPath;
                    case HeroMotion.Down:
                        return _downPath;
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
                    case HeroMotion.Skill:
                        if (_holdFrame >= 0)
                        {
                            return _holdFrame;                 // 腾空悬停俯冲的空中部分:动画定在最后一帧(E-149)
                        }
                        return (_skillWindupStage >= 0 ? _attackTimelines[_skillWindupStage] : _skillMotion.Timeline).FrameAt(AttackAnimationMs);
                    case HeroMotion.JumpAttack:
                        return _jumpAttackTimeline.FrameAt(AttackAnimationMs);
                    case HeroMotion.Jump:
                        return JumpFrame();
                    case HeroMotion.Hurt:
                        return _hurtTimeline.FrameAt(MotionMs);
                    case HeroMotion.Down:
                        return _downTimeline.FrameAt((long)_downAniMs);
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
            if (_backStep)
            {
                // 后跳的姿势:基线里整个腾空期间都是同一张图,与跳跃动画的末尾停留帧(下落帧一路播到的最后一帧)是同一张
                return PhasedJump ? _jumpHold2 : Math.Max(0, t.FrameCount - 1);
            }
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
    /// 技能快捷键(原版默认键位里 A、S、D、F、G、H 是六个技能快捷槽,证据 E-072、E-076;底栏画面六个槽的键名一致):一次更新里"刚按下"的键。
    /// 槽里放什么技能由角色的技能簿决定(<c>HeroSkills</c>;M35 之前只有 A、S、D 三个槽,取自初始技能表)。
    /// </summary>
    [Flags]
    public enum SkillKeys
    {
        None = 0,
        A = 1,
        S = 2,
        D = 4,
        F = 8,
        G = 16,
        H = 32,

        /// <summary>
        /// 出招指令里的"技能"键(原版热键表里"技能"一格,默认 Z,E-140)。不是快捷槽:它只在指令里当扳机用,
        /// 放哪个技能由方向键加上各技能自己的 [command] 决定(<c>CommandInput</c>、<c>HeroSkills.TryCastCommand</c>)。
        /// </summary>
        CommandSkill = 64,

        /// <summary>出招指令里的"增益"键(热键表里"技能2"一格,默认空格;推断,E-140)。</summary>
        CommandBuff = 128,

        /// <summary>六个快捷槽的键(A S D F G H)。</summary>
        Quick = A | S | D | F | G | H,
    }

    /// <summary>带技能快捷键的场景游戏(副本房间里的战斗):同 <see cref="IActionSession"/>,每帧多一组"刚按下"的技能键。</summary>
    public interface ISkillSession : IActionSession
    {
        void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed);
    }

    /// <summary>
    /// 还要知道技能键"此刻是不是按着"的会话(蓄力类技能,证据 E-138:状态码每帧查快捷键是否还按住,松开或蓄满才出招)。
    /// 视图层(或时间表)在每次 <see cref="ISkillSession.Update(double, int, int, bool, bool, SkillKeys)"/> 之前把这一帧还按着的技能键写进来;不写就当全都松开。
    /// </summary>
    public interface ISkillHoldSession : ISkillSession
    {
        SkillKeys SkillsHeld { get; set; }
    }

    /// <summary>
    /// 带出招指令开关的会话(M47,证据 E-140):开着时,技能键 <see cref="SkillKeys.CommandSkill"/>、<see cref="SkillKeys.CommandBuff"/> 与方向键、跳跃键
    /// 一起按出各技能 [command] 里的键序列就放那个技能;关着时这两个键没有用(旧行为,自动机与旧脚本不变)。
    /// </summary>
    public interface ICommandSession : ISkillHoldSession
    {
        bool CommandsEnabled { get; set; }
    }

    /// <summary>
    /// 腾空悬停俯冲的参数(证据 E-149):悬停毫秒数 <see cref="HoverMs"/>(状态计时器到这个数才开始俯冲),
    /// 俯冲的水平速度 <see cref="SpeedX"/> 与竖直速度 <see cref="SpeedZ"/>(引擎速度表里的数,实际速度再乘移动速度属性的千分比,见 <see cref="TownHero.DiveSpeedX"/>),
    /// 落地放冲击对象所需的最小离地高度 <see cref="ImpactMinHeight"/>(放出技能那一刻的高度不小于它才放)。
    /// </summary>
    public sealed class SkillDivePlan
    {
        public SkillDivePlan(int hoverMs, int speedX, int speedZ, int impactMinHeight)
        {
            HoverMs = hoverMs < 0 ? 0 : hoverMs;
            SpeedX = speedX;
            SpeedZ = speedZ;
            ImpactMinHeight = impactMinHeight;
        }

        public int HoverMs { get; }
        public int SpeedX { get; }
        public int SpeedZ { get; }
        public int ImpactMinHeight { get; }
    }

    /// <summary>
    /// 脚本跳跃的参数(蓄力跳跃类技能,证据 E-138):水平距离 <see cref="Distance"/> 与最大高度 <see cref="Height"/>(像素),
    /// 这一段动画里的几个关键帧(上升结束 <see cref="RiseFrame"/>、落地 <see cref="LandFrame"/>、命中起止 <see cref="HitStartFrame"/> 与 <see cref="HitEndFrame"/>),
    /// 以及命中窗口数 <see cref="Hits"/>。位移完全由动画时间脚本化、不走跳跃物理:
    /// 上升段水平走 <see cref="NearShare"/> 的距离、高度走缓出弧,下落段走完余下的距离、高度走缓入弧回到 0(见 <see cref="TownHero"/>)。
    /// 三个插值函数是客户端的取整规则(逐个照读码复刻,E-138)。
    /// </summary>
    public sealed class SkillHopPlan
    {
        /// <summary>上升段走掉的水平距离占全程的比例(客户端常量)。</summary>
        public const float NearShare = 0.8f;

        public SkillHopPlan(int distance, int height, int hits, int riseFrame, int landFrame, int hitStartFrame, int hitEndFrame)
        {
            Distance = distance;
            Height = height;
            Hits = hits < 1 ? 1 : hits;
            RiseFrame = riseFrame;
            LandFrame = landFrame;
            HitStartFrame = hitStartFrame;
            HitEndFrame = hitEndFrame;
        }

        public int Distance { get; }
        public int Height { get; }
        public int Hits { get; }
        public int RiseFrame { get; }
        public int LandFrame { get; }
        public int HitStartFrame { get; }
        public int HitEndFrame { get; }

        /// <summary>夹住的整数线性插值(0x11b98c0):x ≥ range 取 b,x ≤ 0 取 a,否则按单精度算 (b − a) × x / range + a 后向零取整。</summary>
        public static int IntLerp(int a, int b, int x, int range)
        {
            if (x >= range)
            {
                return (int)(float)b;
            }
            if (x <= 0)
            {
                return (int)(float)a;
            }
            float d = (float)b - (float)a;
            d *= x;
            d /= range;
            d += a;
            return (int)d;
        }

        /// <summary>夹住的单精度线性插值(0x125a7a0):边界同 <see cref="IntLerp"/>,中间 (b − a) × x / range + a,不取整。</summary>
        public static float FloatLerp(float a, float b, int x, int range)
        {
            if (x >= range)
            {
                return b;
            }
            if (x <= 0)
            {
                return a;
            }
            float d = b - a;
            d *= x;
            d /= range;
            return d + a;
        }

        /// <summary>
        /// 带缓动的整数插值(0x11b9ef0):x ≥ range 取 b,x ≤ 0 取 a;缓出(<paramref name="easeOut"/> 为真)是从 a 出发的 a + (b − a)(2s − s²),s = x / range;
        /// 缓入是对称的 a + (b − a) s²。结果向零取整。
        /// </summary>
        public static int EasedLerp(int a, int b, int x, int range, bool easeOut)
        {
            if (x >= range)
            {
                return b;
            }
            if (x <= 0)
            {
                return a;
            }
            float start = a;
            float end = b;
            int xp = x;
            if (!easeOut)
            {
                start = b;
                end = a;
                xp = range - x;
            }
            float delta = Math.Abs(end - start);
            float linear = delta * 2f / range;
            float curve = -(linear / range);
            float v = xp * (curve * 0.5f * xp + linear);
            return (int)(end <= start ? start - v : start + v);
        }
    }

    /// <summary>
    /// 带一个可操作主角与跟随镜头的场景游戏(城镇或副本房间)。视图层(Unity 的 TownView)只依赖这个接口:
    /// 每帧 <see cref="Update"/> 推进主角与镜头,再用 <see cref="Build"/> 取绘制列表。
    /// </summary>
    public interface IHeroSession : ISceneSource
    {
        string MapPath { get; }
        TownHero Hero { get; }
        TownCamera Camera { get; }
        int LayerCount { get; }
        int NpcCount { get; }
        void Update(double deltaMs, int dirX, int dirY);
    }

    /// <summary>
    /// 一场城镇游戏:地图图层、NPC、主角与镜头。每帧先 <see cref="Update"/> 推进主角,再 <see cref="Build"/> 取绘制列表
    /// (地图图层与 NPC 的动画按墙钟时间 <c>timeMs</c> 播放,主角按自己的状态计时)。
    /// </summary>
    public sealed class TownSession : IHeroSession, ITownExitSource, IMapSoundSource, ITownNpcSource
    {
        /// <summary>纵向常数,E-024 在网关地图上拟合出的 200。</summary>
        public const int DefaultGroundY = 200;

        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedLayer> _layers;
        private readonly List<TownScene.ResolvedNpc> _npcs;
        private readonly RoomContent _room;
        private readonly TownExitTracker _exits;
        private TownExit? _pending;

        public string MapPath { get; }
        public TownHero Hero { get; }
        public TownCamera Camera { get; }
        public int GroundY { get; }
        public int NpcCount => _npcs.Count;

        /// <summary>地图里摆着的 NPC(<see cref="ITownNpcSource"/>):只含脚本与动画都读得出来的那些,和画面上看到的一致。</summary>
        public IReadOnlyList<TownNpc> Npcs { get; private set; }

        public int LayerCount => _room != null ? _room.Layers.Count : _layers.Count;

        /// <summary>主角走进、还没被流程处理的出口;没有出口表、或还没走进时为 null。</summary>
        public TownExit? PendingExit => _pending;

        /// <summary>出口触发器;没有出口表(演示用的单张地图)时为 null。</summary>
        public TownExitTracker Exits => _exits;

        /// <summary>
        /// 这块区域地图 [sound] 里的声音标签(背景音乐、环境声,证据 E-097)。只有"房间式内容"的会话(城镇图册里的区域)读了地图,
        /// 老式的单张地图会话(<c>--legacy-town</c> 的赛丽亚房间)没读,是空列表,所以那条路径仍然没有声音。
        /// </summary>
        public IReadOnlyList<string> MapSounds => _room != null && _room.Map != null ? _room.Map.Sounds : (IReadOnlyList<string>)Array.Empty<string>();

        public double HeroX => Hero.X;

        public double HeroY => Hero.Y;

        public bool HeroFacingLeft => Hero.FacingLeft;

        public void OutwardOf(TownExit exit, out int dx, out int dy)
        {
            if (_exits == null)
            {
                dx = 0;
                dy = 0;
                return;
            }
            _exits.OutwardOf(exit, out dx, out dy);
        }

        /// <summary>此刻的投影:镜头 x 由主角位置按 <see cref="Camera"/> 算出。</summary>
        public TownProjection Projection => new TownProjection(Camera.IntX, GroundY);

        public TownSession(DnfAssets assets, string mapPath, TownHero hero, TownCamera camera, int groundY = DefaultGroundY)
            : this(assets, mapPath, hero, camera, groundY, null, null)
        {
        }

        /// <summary>
        /// 带"房间式内容"与出口的城镇会话(证据 E-095):<paramref name="room"/> 不为空时按副本房间的方式合成地图
        /// (远景/中景视差背景、地面瓦片、[animation] 图层与被动物件,<see cref="RoomScene.Build"/>),再把 NPC 与主角放进同一次深度排序;
        /// 艾尔文防线这样的大区域需要它(背景与地面瓦片);赛丽亚房间的瓦片是空的,画出来与只画图层的结果逐像素相同
        /// (本机用真实数据核对过:同一出生点与镜头、含 NPC 与主角,四个时刻零差异)。
        /// <paramref name="exits"/> 不为空时 <see cref="Update"/> 会检测主角走进出口(<see cref="PendingExit"/>)。
        /// </summary>
        public TownSession(DnfAssets assets, string mapPath, TownHero hero, TownCamera camera, int groundY, RoomContent room, TownExitTracker exits)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            MapPath = mapPath ?? throw new ArgumentNullException(nameof(mapPath));
            Hero = hero ?? throw new ArgumentNullException(nameof(hero));
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            GroundY = groundY;
            _room = room;
            _exits = exits;
            _layers = room != null ? new List<TownScene.ResolvedLayer>() : TownScene.ResolveLayers(assets, mapPath);
            _npcs = TownScene.Resolve(assets, TownScene.ReadMapNpcs(assets, mapPath));
            var placed = new List<TownNpc>(_npcs.Count);
            foreach (TownScene.ResolvedNpc r in _npcs)
            {
                placed.Add(r.Npc);
            }
            Npcs = placed;
            _exits?.Arrive(hero.X, hero.Y);
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Hero.Update(deltaMs, dirX, dirY);
            Camera.Follow(Hero.X);
            if (_exits != null && !_pending.HasValue)
            {
                _pending = _exits.Step(Hero.X, Hero.Y, dirX, dirY);
            }
        }

        public List<SceneSprite> Build(long timeMs)
        {
            TownProjection projection = Projection;
            List<SceneSprite> hero = Hero.Sprites(_assets, projection);
            if (_room == null)
            {
                return TownScene.BuildResolvedScene(_assets, _layers, _npcs, projection, timeMs, hero);
            }
            var extra = new List<SceneSprite>(_npcs.Count + hero.Count);
            foreach (TownScene.ResolvedNpc r in _npcs)
            {
                SceneSprite s = TownScene.SpriteAt(_assets, r, r.Timeline.FrameAt(timeMs), projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            extra.AddRange(hero);
            return RoomScene.Build(_assets, _room, projection.CameraX, projection.GroundY, timeMs, extra);
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

        /// <summary>默认外观的鬼剑士换装(游戏数据自带的 *_default 装备加一件起始武器)。要造多个主角(换区域)时先造一次共用。</summary>
        public static AvatarOutfit CreateOutfit(DnfAssets assets, string weaponEqu = StarterWeapon)
        {
            AvatarClass cls = AvatarClass.FromChr(assets, SwordmanChrPath, SwordmanJobTag);
            return AvatarOutfit.CreateDefault(assets, cls, SwordmanEquipRoot, weaponEqu);
        }

        /// <summary>造一个默认外观的鬼剑士,可走区域取自地图的 [virtual movable area]。</summary>
        public static TownHero CreateSwordman(DnfAssets assets, string mapPath, double x, double y, bool facingLeft, string weaponEqu = StarterWeapon,
            string restTag = TownHero.TownRestTag)
        {
            AvatarOutfit outfit = CreateOutfit(assets, weaponEqu);
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
