using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Game.Ai;
using Dnf.Render;

namespace Dnf.Play
{
    public enum MonsterPhase
    {
        /// <summary>站着(待机动作)。</summary>
        Wait,

        /// <summary>朝主角走。</summary>
        Walk,

        /// <summary>出招(.mob 的第一个攻击动作);动画里带攻击框的那几帧能打中主角(<see cref="MonsterActor.AttackBoxesAt"/>)。</summary>
        Attack,

        /// <summary>受击硬直(受击动作一或二,被击倒时是倒地动作)。</summary>
        Hurt,

        /// <summary>
        /// 死亡(DIE):停在躺着的那一帧。有倒地动作的怪物被打死时先走倒地流程(腾空、颠一下,仍是 <see cref="Hurt"/>),躺下的那一刻才进入这个阶段
        /// (证据 E-082、E-084)。白闪、淡出与消失从血量归零算起(<see cref="MonsterTuning.DeathMs"/>),与倒地流程同时进行,不等它走完。
        /// </summary>
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

        /// <summary>
        /// 怪物每点 [move speed] 的水平步速(像素/秒)。量法:基线连拍里一只怪物匀速走向主角,
        /// 左缘在 23 帧(间隔 200 毫秒)里的线性回归斜率除以它的 [move speed],误差约 0.3%(证据 E-114,非 REA 的测量)。
        /// 旧换算把怪物的数按主角的实测步速折算,比基线量到的偏大不少:基线主角的实际速度属性不一定是脚本里的数(装备等会加),所以主角的步速不能当成换算的基准。
        /// 只有一种怪物一个样本,线性关系是假设。
        /// </summary>
        public const double WalkPixelsPerStat = 0.1573;

        /// <summary>主角进入这个范围(世界像素)怪物才有反应;原版 .ai 里的视野数没有接入,这是近似。</summary>
        public const double SightX = 320;

        public const double SightY = 160;

        /// <summary>进入这个距离就停下出招(未量)。</summary>
        public const double AttackRangeX = 70;

        public const double AttackRangeY = 16;

        /// <summary>
        /// 按攻击框判断"够得着就出招"时(<see cref="MonsterActor.Update"/> 给了主角受击框),怪物最近走到离主角这么远(水平像素)就不再靠近,
        /// 免得叠在主角身上(未量,随手取的数)。
        /// </summary>
        public const double MinSeparationX = 8;

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
        /// 普通受击硬直的占位时长(命中停顿之后才开始计,见 <see cref="MonsterActor.Pause"/>)。**E-080 起,读得到 Etc/EtcParameter.etc 的三张表时
        /// 受击硬直改按客户端的公式算(<see cref="HitStun"/>,从被打中那一刻起算、含停顿),这个值只在读不到表(或倒地以外的占位路径)时用。**
        /// 量法:基线连拍 S07 里两只一级地精各挨一下,
        /// 受击帧分别连续出现 4 次与 3 次(间隔 100 毫秒),之后恢复站立,所以受击姿势在画面上的总时长在 300 到 500 毫秒之间(分辨率 100 毫秒);
        /// 这个总时长里已经含着主角武器的那一次命中停顿(E-077),所以这里取区间偏下的 350,停顿在它之前另外计。
        /// 只有两个样本,没有算上 [hit recovery] 与攻击信息里的 [force hit stun time];换武器时停顿不同,总时长会跟着变(原版的硬直怎么算还没读)。
        /// </summary>
        public const double HurtHoldMs = 350;

        /// <summary>被打倒(.atk 的 [damage reaction] 是倒地)时在地上停的时长(未量,取个看得过去的数)。</summary>
        public const double KnockDownHoldMs = 900;

        /// <summary>
        /// 从血量归零(被打死)到消失的时长,倒地流程没走完也一样。量法:基线连拍 S09 里最后一击后地精约 0.5 秒内消失;
        /// 逐帧对照倒地动画的帧号:死亡的白色剪影在腾空的姿势上就出现了,不是落地之后(E-084);只有一个样本。碎片与血的粒子特效(.ptl)我们没有做。
        /// </summary>
        public const double DeathMs = 500;

        /// <summary>
        /// 被打死后精灵是纯白剪影的时长,之后线性淡出到消失。量法:基线连拍 S09 里被打死的地精,
        /// 被打后 100、200 毫秒的两张是纯白剪影,300 毫秒时已淡到一半,400 毫秒只剩一点影子,500 毫秒没了(一个样本,分辨率 100 毫秒;E-084)。
        /// </summary>
        public const double DeathWhiteMs = 250;

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
    public sealed partial class MonsterActor
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
        private double _hurtClockMs;
        private bool _hurtOnClock;
        private double _pushEaseMs = MonsterTuning.PushEaseMs;

        // 倒地状态机(证据 E-082、E-083;与主角共用 KnockdownFlight,E-105):阶段 0 首次腾空、1 落地后颠一下、2 躺着;类型见 Knockdown.TypeOf
        private readonly int _weight;
        private KnockdownFlight _flight;          // 倒地动画换了([CHANGE ACTION FILE])后,下一次开始倒地时重建(SyncFlightAnimation)
        private AniFile _flightAni;               // 现在这个倒地状态机是按哪个倒地动画建的
        private readonly MonsterAnis _anis;       // 这一只自己的一套动画(开头是模型的,脚本换文件只改这一份,M57)
        private bool _dyingFromDown;
        private readonly Dictionary<int, List<AniBox>> _strikeBoxes = new Dictionary<int, List<AniBox>>();

        // 默认 AI 的出招决策(证据 E-100、E-101):有 [attack kind] 记录时由 MonsterAttackChooser 决定出哪一招、什么时候出;没有时走旧的"够得着就出招 + 冷却"占位
        private MonsterAttackChooser _chooser;
        private MonsterBrain _brain;
        private int _stuckX = int.MinValue;                   // 卡住检测:上一次看到的整数位置与"位置最后一次变过"的时刻(E-148)
        private int _stuckY = int.MinValue;
        private long _stuckStampMs;
        private long _lastBrainThinkMs = long.MinValue / 2;
        private double _aiClockMs;
        private int _attackIndex;
        private HeroStrikeInfo _strike;
        private HeroStrikeInfo[] _strikes;
        private HeroStrikeInfo?[] _actDefaultStrikes;          // 各招 .act 自带的 [DEFAULT ATTACKINFO](M69,E-167;没有的项是 null)
        private HeroStrikeInfo? _strikeOverride;               // 出招脚本 [CHANGE ATTACKINFO] 换上的;下一次出招开始清掉
        private Func<int, bool> _reachTest;

        // .ai 脚本驱动(证据 E-128):脚本怪物的出招、走位都由 MonsterScript 的四个脚本决定;没装时走上面的默认 AI 或占位
        private MonsterScript _script;
        private readonly MonsterAiView _aiView = new MonsterAiView();
        private bool _aware;
        private bool _hasDestination;
        private double _destinationX;
        private double _destinationY;
        private int _moveMode;

        // 投掷(证据 E-103):.mob 的 [throw attack] 记录与它们的投射物模板;发射出来的投射物与声音先存在这里,由房间会话取走
        private MonsterThrows _throws;
        private readonly List<MonsterProjectile> _thrownProjectiles = new List<MonsterProjectile>();
        private readonly List<string> _throwSounds = new List<string>();
        private double _reachDx;
        private double _reachDy;
        private IReadOnlyList<Aabb> _reachBoxes;

        // .act 触发器与行为(证据 E-130):每一招的 .act 程序(与 Attacks 同下标,没有触发器的为 null)、这一招的运行器(每次出招新建,
        // 触发器里再切招时旧运行器还在执行,不能复用)、脚本给的速度与攻击框开关;出招阶段每一步由 TickActs 推进
        private ActProgram[] _actPrograms;
        private bool[][] _actEnabled;            // 每一招一份触发器开关(跨出招保留,E-134)
        private ActRunner _actRunner;
        private IActHost _actHost;
        private ActBody _actBody;
        private bool _actSetPending;
        private double _actVx;
        private double _actVy;
        private double _actVz;
        private bool _actFalling;
        private bool _attackRectOff;
        private int _motionVersion;
        private int _deferredAttack = -1;

        // 脚本召唤(证据 E-134)
        private double _lifeLeftMs;

        // 受击盒开关、浮空悬停与瞬移(证据 E-133)
        private bool _damageBoxOff;
        private bool _hovering;
        private double _baseHover;
        private double _hoverHeight;
        private double _hoverVz;
        private bool _hoverFalling;
        private IWalkArea _lastWalk;

        /// <summary>血已经没了、但还在走倒地流程(腾空、颠一下);躺下的那一刻转入死亡(证据 E-079 第 3 步、E-082、E-084)。</summary>
        private bool _dead;

        /// <summary>血量归零之后过去的毫秒数(命中停顿里不走);白闪、淡出、消失都按它算,不管这时还在倒地流程里还是已经进了死亡(证据 E-084)。</summary>
        private double _deathMs;

        public int Index { get; }
        public int Code { get; }
        public RoomMonsterSpawn Spawn { get; }
        public bool IsBoss { get; }
        public int Level { get; }
        public int HpMax { get; private set; }
        public int Hp { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public bool FacingLeft { get; private set; } = true;
        public MonsterPhase Phase { get; private set; }
        public double SpeedX { get; private set; }
        public double SpeedY { get; private set; }
        public int AttackDelayMs { get; }

        /// <summary>出招动画的播放倍率(.mob 的 [attack speed] / 1000;没有该项时为 1)。</summary>
        public double AttackRate { get; private set; }

        /// <summary>这一次受击硬直的总时长(毫秒);不在受击中时是上一次的值。</summary>
        public double HurtSpanMs => _hurtSpanMs;

        /// <summary>
        /// 怪物的"受击恢复"属性(<see cref="MonsterStats.HitRecovery"/>):受击硬直公式里的 R(证据 E-080);越大硬直越短。
        /// 没有公式(没传硬直表)时不起作用。
        /// </summary>
        public int HitRecovery { get; }

        /// <summary>
        /// 这一次受击的硬直是不是按客户端的公式算的(<see cref="HitStun"/>,证据 E-080)。为真时硬直从被打中那一刻起算、
        /// 命中停顿也算在里面(秒表不被停顿冻结,推断,E-080);为假时是占位值,停顿之后才开始计。
        /// </summary>
        public bool HurtFromFormula => _hurtOnClock;

        /// <summary>怪物的体重属性(<see cref="MonsterStats.Weight"/>):倒地的击飞量按它缩放(证据 E-083)。</summary>
        public int Weight => _weight;

        /// <summary>离地高度(像素)。倒地腾空时 &gt; 0,受击框、精灵与伤害数字都带着它(证据 E-082)。</summary>
        public double Z { get; private set; }

        /// <summary>正在倒地状态里(腾空、颠一下、躺着)。</summary>
        public bool IsKnockedDown => _flight.Active;

        /// <summary>倒地阶段:0 首次腾空、1 落地后颠一下、2 躺着;不在倒地里是 -1。</summary>
        public int DownStage => _flight.Stage;

        /// <summary>z 方向的速度(像素/秒,向上为正);只在腾空时非 0。</summary>
        public double VerticalSpeed => _flight.VerticalSpeed;

        /// <summary>水平速度(像素/秒,向右为正)。</summary>
        public double HorizontalSpeed => _flight.HorizontalSpeed;

        /// <summary>命中停顿还剩多少毫秒(证据 E-077):停顿期间动画、行走、出招、击退、冷却的时间都不走。</summary>
        public double PauseMs { get; private set; }

        public bool IsAlive => Hp > 0;
        public bool IsGone => Phase == MonsterPhase.Gone;

        /// <summary>.mob 的 [superarmor on attack] 第一个数非 0:出招期间带档位 1 的霸体(E-143)。由 <see cref="RoomMonsterBuilder"/> 从定义里接过来。</summary>
        public bool SuperArmorOnAttack { get; set; }

        /// <summary>
        /// "朝向跟着目标走"的总开关(客户端怪物字段 +0x4f7c,E-169):出生时取 .mob 的 [change direction by target](没写是开),出招脚本的 [CHANGE DIRECTION BY TARGET] 中途会改。
        /// 关着的时候怪物应用状态不采用 AI 给的朝向(客户端 0x00836945 与 0x00836ff9),所以 AI 的转向——出招前转向主角、站立面对主角、走路转向行进方向——全都不做;
        /// 脚本自己的 [SET DIRECTION]、倒地飞行的朝向不受它管。由 <see cref="RoomMonsterBuilder"/> 从定义里接过来。
        /// </summary>
        public bool FacesTarget { get; set; } = true;

        /// <summary>
        /// 出招里逐帧面朝目标(客户端字段 +0x4fb8 非 0,E-169):出生时取 .mob 的 [is direction to move](非 0),脚本的 [IS DIRECTION TO MOVE] 中途会改。
        /// 客户端的自动转向函数 0x00d1cb00 在出招状态里每次调用都把怪物转向目标(横向位置比较,目标在右边就朝右、一样就朝左),要 <see cref="FacesTarget"/> 也开着;
        /// 调用的时机在保护区里读不到(推断:逐帧)。
        /// </summary>
        public bool AttackFacing { get; set; }

        /// <summary>
        /// AI 的指定目标(客户端 AI 对象 +0x39c,E-169):脚本的 [CHANGE MY TARGET] 设的,永久,优先于缺省目标(<see cref="ActTarget"/>);没有是 null。
        /// 指定目标不在场了(死了)就当没有,回到缺省目标。
        /// </summary>
        public IActEntity DesignatedTarget { get; private set; }

        /// <summary>
        /// 怪物此刻的 AI 目标(客户端 AI 对象的取目标函数 0x00839410:指定目标优先、否则缺省目标):脚本的 [MOVE TO TARGET]、[SET DIRECTION] [TO TARGET]、[TARGET] 选择器取它。
        /// </summary>
        public IActEntity AiTarget => DesignatedTarget != null && DesignatedTarget.IsAlive ? DesignatedTarget : ActTarget;

        /// <summary>
        /// 脚本换 AI 目标(E-169):<paramref name="designated"/> 为真([CHANGE MY TARGET])直接设成指定目标;为假([SET SAVED TARGET])是客户端的"软"设置——
        /// 现有目标(指定的或缺省的)有效时不替换,只有目标丢了(没有或已不在场)才换成缺省目标(推断:客户端 0x008e22f0 看现有目标是不是在场的活动对象)。
        /// </summary>
        internal void SetAiTarget(IActEntity target, bool designated)
        {
            if (target == null)
            {
                return;
            }
            if (designated)
            {
                DesignatedTarget = target;
                return;
            }
            IActEntity current = AiTarget;
            if (current == null || !current.IsAlive)
            {
                ActTarget = target;
            }
        }

        /// <summary>AI 的转向:总开关 <see cref="FacesTarget"/> 开着才转(E-169);倒地飞行、脚本的 [SET DIRECTION] 不走这里。</summary>
        private void TurnTo(bool left)
        {
            if (FacesTarget)
            {
                FacingLeft = left;
            }
        }

        /// <summary>
        /// 现在带着霸体(档位 1):有 <see cref="SuperArmorOnAttack"/> 而且正在出招(客户端进入出招状态 STATE_ATTACK = 8 时打开、离开时关闭),
        /// 或者强悍、凶猛类别的怪物处在靠近模式里(<see cref="MonsterBrain.ApproachSuperArmor"/>,E-148);已经死了没有。
        /// </summary>
        public bool IsSuperArmored => IsAlive && ((SuperArmorOnAttack && Phase == MonsterPhase.Attack) || (_brain != null && _brain.ApproachSuperArmor));

        /// <summary>血量归零之后到消失之前,精灵画成纯白剪影(白闪;腾空的尸体也是白的,证据 E-084 的基线逐帧)。</summary>
        public bool DeathSilhouette => !IsAlive && Phase != MonsterPhase.Gone;

        /// <summary>
        /// 整张精灵的不透明度(0 到 255):血量归零后前 <see cref="MonsterTuning.DeathWhiteMs"/> 毫秒不透明,之后线性淡到消失(证据 E-084);活着时 255。
        /// </summary>
        public int DeathTintAlpha
        {
            get
            {
                if (IsAlive || _deathMs <= MonsterTuning.DeathWhiteMs)
                {
                    return 255;
                }
                double fade = (MonsterTuning.DeathMs - _deathMs) / (MonsterTuning.DeathMs - MonsterTuning.DeathWhiteMs);
                return (int)Math.Round(255.0 * Math.Max(0.0, Math.Min(1.0, fade)));
            }
        }

        /// <summary>血量归零之后过去的毫秒数;活着时 0。</summary>
        public long DeathElapsedMs => (long)_deathMs;

        /// <summary>当前阶段已经过去的毫秒数。</summary>
        public long PhaseMs => (long)_phaseMs;

        /// <param name="speedX">水平行走速度,像素/秒(房间里由 <see cref="RoomCombatSession.Create"/> 把怪物的 [move speed] 按主角的速度比例换算,同一比例套在怪物上是假设)。</param>
        /// <param name="hitRecovery">怪物的"受击恢复"属性(<see cref="MonsterStats.HitRecovery"/>),受击硬直公式里的 R;没有就传 0。</param>
        /// <param name="weight">怪物的体重属性(<see cref="MonsterStats.Weight"/>),倒地的击飞量按它缩放(证据 E-083);没有就传 0。</param>
        public MonsterActor(int index, RoomMonsterSpawn spawn, MonsterModel model, int level, int hpMax, double speedX, int attackDelayMs,
            int attackSpeed = 0, int hitRecovery = 0, int weight = 0)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _anis = new MonsterAnis(model);
            HitRecovery = hitRecovery;
            _weight = weight;
            _flight = new KnockdownFlight(weight, new DownAnimation(model.Down));
            _flightAni = model.Down;
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
            _reachTest = ReachesTarget;
        }

        /// <summary>
        /// 装上 .mob 的 [attack kind] 记录(第 i 条对应第 i 个 [attack motion]),之后出招改由 <see cref="MonsterAttackChooser"/> 决定(证据 E-100、E-101)。
        /// 没有动画的那几招不参与;<paramref name="records"/> 为空时仍走旧的占位(够得着就出招 + 冷却)。<paramref name="seed"/> 决定这只怪物的随机数序列。
        /// </summary>
        public void SetAttackKinds(IReadOnlyList<AttackKindRecord> records, uint seed)
        {
            if (records == null || records.Count == 0)
            {
                _chooser = null;
                return;
            }
            uint state = seed;
            var chooser = new MonsterAttackChooser(records, AttackDelayMs, () => ClientRandom.Next(ref state));
            bool any = false;
            for (int i = 0; i < records.Count; i++)
            {
                if (i >= _anis.Attacks.Length || _anis.Attacks[i] == null)
                {
                    chooser.Disable(i);
                }
                else
                {
                    any = true;
                }
            }
            _chooser = any ? chooser : null;           // 一招动画都没有:不出招,走旧的占位(够近就站住)
        }

        /// <summary>出招决策器;没有装 [attack kind] 记录时为 null(旧的占位逻辑)。</summary>
        public MonsterAttackChooser AttackChooser => _chooser;

        /// <summary>
        /// 装上 .ai 脚本驱动(证据 E-128):装了之后站着与行走时的决策完全由它决定(动作脚本选招、选目的地脚本选方向、移动方式脚本定走不走),
        /// 不再用 [attack kind] 的出招决策器、走位大脑与占位逻辑。传 null 回到原来的行为。
        /// </summary>
        public void SetScript(MonsterScript script)
        {
            _script = script;
            BindScriptTriggers();
            _aware = false;
            _hasDestination = false;
        }

        /// <summary>.ai 脚本驱动;没装时为 null。</summary>
        public MonsterScript Script => _script;

        /// <summary>目标(主角)现在的状态号(OperatingValue.lst 的写法:0 站、3 受击、4 倒地、5 死亡、6 跳、8 出招、11 冲刺……),脚本里的 [target state] 读它;房间会话每帧给。</summary>
        public int TargetAiState { get; set; }

        /// <summary>怪物自己的状态号(同上):站着与行走是 0,出招 8,受击 3,倒地 4,死亡 5。</summary>
        public int AiState
        {
            get
            {
                switch (Phase)
                {
                    case MonsterPhase.Attack:
                        return 8;
                    case MonsterPhase.Hurt:
                        return IsKnockedDown ? 4 : 3;
                    case MonsterPhase.Dying:
                    case MonsterPhase.Gone:
                        return 5;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>
        /// 装上 .ai 脚本驱动(证据 E-128)的怪物是否已经发现了主角(进过视野,之后一直盯着,主角不在了才重置)。
        /// </summary>
        public bool IsAware => _aware;

        /// <summary>
        /// 装上默认 AI 的走位(<see cref="MonsterBrain"/>,证据 E-114):游荡、发现主角、按节拍朝主角走。装了之后 <see cref="Think"/> 不再用固定视野框与每帧直线追击的占位;
        /// 传 null 回到占位。没有走位大脑的怪物(没有 [melee combat]、[range combat] 类别的脚本怪等)仍走占位。
        /// </summary>
        public void SetDefaultAi(MonsterBrain brain)
        {
            _brain = brain;
        }

        /// <summary>默认 AI 的走位大脑;没装时为 null(占位逻辑)。</summary>
        public MonsterBrain Brain => _brain;

        /// <summary>
        /// 装上 .mob 的 [throw attack] 记录与投射物模板(证据 E-103):出招动画的当前帧超过某条记录的 [throw frame] 时,在身边放出它的投射物。
        /// 传 null 表示这只怪物不投掷。
        /// </summary>
        public void SetThrows(MonsterThrows throws)
        {
            _throws = throws;
        }

        public MonsterThrows Throws => _throws;

        /// <summary>
        /// 装上各招的 .act 程序(证据 E-130):<paramref name="programs"/> 与 <see cref="MonsterModel.Attacks"/> 同下标,没有 .act 或没有触发器的招给 null。
        /// 之后出招阶段的每一步让当前这一招的触发器跑一遍(放被动对象、设速度、切招、开关攻击框……);<paramref name="host"/> 提供随机数与生成对象。
        /// 全是 null(或 <paramref name="programs"/> 为 null)表示这只怪物不用脚本触发器。
        /// </summary>
        public void SetActs(ActProgram[] programs, IActHost host)
        {
            _actRunner = null;
            _actPrograms = null;
            _actEnabled = null;
            _actHost = host;
            if (programs != null && host != null)
            {
                foreach (ActProgram p in programs)
                {
                    if (p != null && p.Triggers.Count > 0)
                    {
                        _actPrograms = programs;
                        break;
                    }
                }
            }
            if (_actPrograms != null)
            {
                // 每一招的触发器开关从脚本里的 [ENABLE] 起步,之后只被 [TRIGGER] 行为改(客户端的开关字节属于怪物的招式条目,不随出招复位,E-134)
                _actEnabled = new bool[_actPrograms.Length][];
                for (int i = 0; i < _actPrograms.Length; i++)
                {
                    if (_actPrograms[i] != null && _actPrograms[i].Triggers.Count > 0)
                    {
                        _actEnabled[i] = ActRunner.NewEnabledState(_actPrograms[i]);
                    }
                }
            }
            BindScriptTriggers();
        }

        /// <summary>第 <paramref name="attack"/> 招的第 <paramref name="trigger"/> 个触发器现在开着吗;这一招没有装触发器程序(或下标越界)返回 null。测试与 .ai 的 is attack action enable() 用。</summary>
        public bool? ActTriggerState(int attack, int trigger)
        {
            if (_actEnabled == null || (uint)attack >= (uint)_actEnabled.Length)
            {
                return null;
            }
            bool[] state = _actEnabled[attack];
            if (state == null || (uint)trigger >= (uint)state.Length)
            {
                return null;
            }
            return state[trigger];
        }

        /// <summary>脚本的按动作族 [TRIGGER] 写法落到了存在的招与触发器上的次数(E-145;测试与审计用)。</summary>
        public int ActSwitchApplied { get; private set; }

        /// <summary>写法指到的招或触发器不存在(没有这一招、这一招没装触发器、序号越界)的次数:客户端同样跳过(触发器个数不大于序号就不动,E-145)。</summary>
        public int ActSwitchMissed { get; private set; }

        /// <summary>指向还没有宿主的动作族([DEFAULT])的次数:只记数、不落实。站立、行走、坐下、倒地、过程已有宿主(M53,E-146),自定义动作也有了(M62-b,E-159),落空的算 <see cref="ActSwitchMissed"/>。</summary>
        public int ActSwitchUnhosted { get; private set; }

        /// <summary>
        /// 脚本的 [TRIGGER] 按动作族写法(E-145):[ATTACK] 序号 触发器序号 [ON]/[OFF]/[RESET]。开关属于怪物的招式条目(每招一份,出招之间保留,E-134),
        /// 所以直接写 <c>_actEnabled</c>;写的正是这一招(正在跑的运行器共用同一份数组)时复位也一并作用到运行器的计数与计时。
        /// 别的招此刻没有运行器,下次出招从头开始,复位对它没有可见的效果。
        /// </summary>
        private void ActSwitchTrigger(ActTriggerPool pool, int slot, int trigger, ActTriggerCommand command)
        {
            if (pool != ActTriggerPool.Attack)
            {
                switch (pool)
                {
                    case ActTriggerPool.Stay:
                        ActSwitchFamilyTrigger(MonsterActFamily.Stay, trigger, command);
                        break;
                    case ActTriggerPool.Move:
                        ActSwitchFamilyTrigger(MonsterActFamily.Move, trigger, command);
                        break;
                    case ActTriggerPool.Sit:
                        ActSwitchFamilyTrigger(MonsterActFamily.Sit, trigger, command);
                        break;
                    case ActTriggerPool.Down:
                        ActSwitchFamilyTrigger(MonsterActFamily.Down, trigger, command);
                        break;
                    case ActTriggerPool.Proc:
                        ActSwitchFamilyTrigger(MonsterActFamily.Proc, trigger, command);
                        break;
                    case ActTriggerPool.Custom:
                        ActSwitchCustomTrigger(slot, trigger, command);       // 自定义动作的开关板(M62-b,E-159)
                        break;
                    default:
                        ActSwitchUnhosted++;
                        break;
                }
                return;
            }
            bool[] state = _actEnabled != null && (uint)slot < (uint)_actEnabled.Length ? _actEnabled[slot] : null;
            if (state == null || (uint)trigger >= (uint)state.Length)
            {
                ActSwitchMissed++;
                return;
            }
            ActSwitchApplied++;
            if (slot == _attackIndex && _actRunner != null)
            {
                _actRunner.ApplyTrigger(trigger, command);
                return;
            }
            switch (command)
            {
                case ActTriggerCommand.On:
                    state[trigger] = true;
                    break;
                case ActTriggerCommand.Off:
                    state[trigger] = false;
                    break;
            }
        }

        /// <summary>把怪物跑着的开关接给 .ai 脚本;没装触发器程序时撤掉,脚本回到 .act 里 [ENABLE] 的初值。</summary>
        private void BindScriptTriggers()
        {
            if (_script != null)
            {
                _script.LiveTrigger = _actEnabled != null ? (Func<int, int, bool?>)ActTriggerState : null;
            }
        }

        /// <summary>装了 .act 触发器(至少一招有)。</summary>
        public bool HasActs => _actPrograms != null;

        /// <summary>怪物在 .act 脚本里的目标(主角);房间会话装。</summary>
        public IActEntity ActTarget { get; set; }

        /// <summary>
        /// 召唤者(创建这只怪物的脚本对象,被作用的那个目标);地图出生点的怪物没有。脚本的 [IS CHILD]、[SUMMON MASTER]、[SUMMON OBJECT] 取它(E-134)。
        /// </summary>
        public IActEntity ActMaster { get; private set; }

        /// <summary>
        /// 是脚本召唤出来的(不是地图出生点的):客户端只发创建请求、怪物由服务端造,那一半没读,所以击杀给不给经验与掉落、算不算战绩是推断——
        /// 按"不给、不算"处理,免得召唤关系被刷(E-134)。
        /// </summary>
        public bool IsSummoned { get; private set; }

        /// <summary>召唤物的存活时间还剩多少毫秒(命中停顿里照走);0 是不限(没有 [SUMMON TIME],或不是召唤物)。</summary>
        public double LifeLeftMs => _lifeLeftMs;

        /// <summary>怪物在 .act 脚本里的身份(被动对象的创建者、触发器的拥有者)。</summary>
        public IActEntity ActSelf => _actBody ?? (_actBody = new ActBody(this));

        /// <summary>脚本里的 <paramref name="entity"/> 属于哪只怪物(怪物自己,或被动对象链条上最早的那只);不是这两种返回 null。</summary>
        public static MonsterActor From(IActEntity entity)
        {
            if (entity is ActBody body)
            {
                return body._m;
            }
            return (entity as PassiveObject)?.Owner;
        }

        /// <summary>怪物的动画与动作路径(.mob 读出来的模型)。</summary>
        public MonsterModel Model => _model;

        /// <summary>当前这一招的运行器;没有(不在出招、这一招没有触发器)时为 null。测试与审计用。</summary>
        public ActRunner CurrentActRunner => _actRunner;

        /// <summary>这一次出招里脚本给的速度(像素/秒;X 已按朝向取过符号)。出招结束清零。</summary>
        public double ActSpeedX => _actVx;

        public double ActSpeedY => _actVy;

        public double ActSpeedZ => _actVz;

        /// <summary>脚本把攻击框关了([ATTACKRECT] [OFF]):出招动画里的攻击框不再判定,直到脚本再开或重新出招。</summary>
        public bool AttackRectOff => _attackRectOff;

        /// <summary>脚本把受击盒关了([SET DAMAGE BOX] [OFF]):没有受击框,主角打不到;脚本再开或怪物离开出招阶段时恢复(E-133)。</summary>
        public bool DamageBoxOff => _damageBoxOff;

        /// <summary>正悬停在 <see cref="HoverHeight"/> 上(.mob 的 [floating height] 或脚本 [CHANGE FLOATING HEIGHT],E-133)。</summary>
        public bool IsHovering => _hovering;

        /// <summary>悬停高度(像素);没悬停时 0。</summary>
        public double HoverHeight => _hovering ? _hoverHeight : 0.0;

        /// <summary>
        /// 出生时的悬停高度(.mob 的 [floating height],全库约 13% 的怪物带它):怪物直接停在这个高度上,画面、受击框与攻击框都按它抬升(E-133;
        /// 推断:这个标签是出生时的浮空高度,与脚本 [CHANGE FLOATING HEIGHT] 改的是同一个量)。小于 1 什么都不做。
        /// </summary>
        public void SetBaseFloatingHeight(int height)
        {
            if (height < 1)
            {
                return;
            }
            _baseHover = height;
            _hovering = true;
            _hoverHeight = height;
            _hoverVz = 0;
            _hoverFalling = false;
            Z = height;
        }

        /// <summary>.act 的触发器通知:这只怪物打中了主角(<paramref name="victim"/>),唤醒带 [ON ATTACKSUCCESS] 的触发器。</summary>
        public void NotifyAttackSuccess(IActEntity victim)
        {
            if (Phase == MonsterPhase.Attack && _actRunner != null)
            {
                _actRunner.OnAttackSuccess(victim);
            }
            FamilyOnAttackSuccess(victim);
            _brain?.NotifyHitHero();                           // 绕背冲刺或靠近途中打中了主角:转追击(E-148)
        }

        /// <summary>
        /// 出招里逐帧面朝目标(E-169;客户端自动转向函数 0x00d1cb00 的出招分支):<see cref="AttackFacing"/> 与 <see cref="FacesTarget"/> 都开着、
        /// 是正常的出招(不是自定义动作)时,每一步按横向位置比较转向——目标在右边(自己的 X 小于目标的 X)就朝右,一样大就朝左。
        /// 目标取 AI 目标(<see cref="AiTarget"/>:脚本指定的优先),没有就用主角的位置。
        /// </summary>
        private void TrackTargetWhileAttacking(double heroX, bool heroPresent)
        {
            if (!AttackFacing || !FacesTarget || _customIndex >= 0)
            {
                return;
            }
            IActEntity aim = AiTarget;
            if (aim != null && aim.IsAlive)
            {
                FacingLeft = !(X < aim.X);
            }
            else if (heroPresent)
            {
                FacingLeft = !(X < heroX);
            }
        }

        /// <summary>
        /// 出招阶段每一步:先让这一招的触发器跑(刚进出招时先唤醒 [ON SET ACTION]),再按脚本给的速度移动、Z 方向受重力;
        /// 触发器里切了招或结束了出招就不再往下做(新的一招从下一步起算)。
        /// </summary>
        private void TickActs(double deltaMs, IWalkArea walk)
        {
            ActRunner runner = _actRunner;
            if (runner == null)
            {
                return;
            }
            if (_actSetPending)
            {
                _actSetPending = false;
                runner.OnSetAction();
                if (_actRunner != runner || Phase != MonsterPhase.Attack)
                {
                    return;
                }
            }
            runner.Update(CurrentFrame, (float)(deltaMs / 1000.0));
            if (_actRunner != runner || Phase != MonsterPhase.Attack)
            {
                return;
            }
            ApplyActMotion(deltaMs, walk);
        }

        /// <summary>进入出招阶段时挂上这一招的运行器:动作从头开始,触发器开关沿用上次出招留下的样子(出生时是 [ENABLE] 的初值,E-134),下一步先唤醒 [ON SET ACTION]。</summary>
        private void BeginActs()
        {
            _actVx = 0;
            _actVy = 0;
            _actVz = 0;
            _actFalling = false;
            _attackRectOff = false;
            _deferredAttack = -1;
            _deferredCustom = -1;
            _motionVersion++;
            _actRunner?.EndMotion();        // 同一个出招阶段里切招、重来:上一段留着的叠加层先收口(事件流,MonsterActor.MotionEvents.cs)
            _actRunner = null;
            BeginMotionEvents();            // 没有触发器、只有叠加层与声音的招:事件流单独放,不建运行器
            if (_customIndex >= 0)
            {
                // 自定义动作(M62-b):自己的动画、自己的触发器程序与开关板;没有触发器的只放动画
                PassiveAction custom = _customs[_customIndex];
                if (custom.HasTriggers && _customHost != null)
                {
                    _actRunner = new ActRunner(custom.Program, ActSelf, _customHost, _customEnabled[_customIndex]);
                    _actSetPending = true;
                }
                return;
            }
            _strikeOverride = null;     // 新的一招从这一招本来的攻击信息开始(.act 默认或 .mob 表;上一招脚本换上的不带过来,推断,E-167)
            if (_actPrograms == null || (uint)_attackIndex >= (uint)_actPrograms.Length || _actPrograms[_attackIndex] == null)
            {
                return;
            }
            _actRunner = new ActRunner(_actPrograms[_attackIndex], ActSelf, _actHost, _actEnabled?[_attackIndex]);
            _actSetPending = true;
        }

        /// <summary>离开出招阶段:脚本的速度与攻击框开关清零。离开的原因是被打倒(倒地的飞行自己接管 Z)时不碰高度。</summary>
        private void EndActs()
        {
            _actRunner?.EndMotion();    // 事件流收口:开始过的叠加层各出一个"结束"(MonsterActor.MotionEvents.cs)
            EndMotionEvents();
            _damageBoxOff = false;      // 受击盒关着出了出招阶段:当作脚本忘了开,别让怪物永远打不到而清不掉房间(推断,E-133)
            RestoreHover();
            DropIfAirborne();
            if (_actRunner == null && !_actFalling && _actVx == 0 && _actVy == 0 && _actVz == 0 && !_attackRectOff)
            {
                return;
            }
            _actVx = 0;
            _actVy = 0;
            _actVz = 0;
            _actFalling = false;
            _attackRectOff = false;
            _deferredAttack = -1;
            _deferredCustom = -1;
            _actRunner = null;
        }

        /// <summary>
        /// 行走里的脚本把怪物抛到半空、动作又被换掉了:不悬停的怪物从这里受重力落回地面(推断,E-146:不然怪物永远停在主角够不着的高度,房间清不掉)。
        /// 倒地飞行与死亡自己管 Z;出招里的高度由出招自己的规则管。
        /// </summary>
        private void DropIfAirborne()
        {
            if (Phase == MonsterPhase.Attack || Phase == MonsterPhase.Dying || Phase == MonsterPhase.Gone || Z <= 0 || _hovering || _hoverFalling || _flight.Active)
            {
                return;
            }
            _hoverFalling = true;
            _hoverVz = 0;
        }

        /// <summary>
        /// 离开出招阶段:脚本用 [CHANGE FLOATING HEIGHT] 改过的悬停高度回到基准高度(.mob 的 [floating height],没设就是地面)。
        /// 推断(E-133):原版的悬停保持逻辑(虚槽 0x274)没读到;用到 [CHANGE FLOATING HEIGHT] 的 18 种怪物里有 7 种只升不降(汇总数,本机脚本扫描,非 REA),
        /// 它们的待机、行走 .act 里没有降落的行为——所以按"出招结束回到基准"处理;不这么做怪物升空后永远停在主角够不着的高度,房间清不掉。
        /// 倒地飞行与死亡自己管 Z,不在这里动。
        /// </summary>
        private void RestoreHover()
        {
            if (_flight.Active || Phase == MonsterPhase.Dying || Phase == MonsterPhase.Gone)
            {
                return;
            }
            if (_baseHover >= 1)
            {
                if (!_hovering || _hoverHeight != _baseHover)
                {
                    ActSetFloatingHeight((int)_baseHover);
                }
            }
            else if (_hovering)
            {
                ActSetFloatingHeight(0);
            }
        }

        /// <summary>出完一招(动画播完,或脚本 [SET ACTION] [STAND]):进冷却、回待机;脚本抛起来还没落地的落回地面(推断)。</summary>
        private void FinishAttack()
        {
            if (_actFalling && !_hovering)
            {
                Z = 0;
            }
            _cooldownMs = AttackDelayMs;
            _script?.AttackFinished();
            EnterPhase(MonsterPhase.Wait);
        }

        private void ActSetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
            if (Phase == MonsterPhase.Gone)
            {
                return;
            }
            if (hasX)
            {
                _actVx = vx;
            }
            if (hasY)
            {
                _actVy = vy;
            }
            if (hasZ)
            {
                _actVz = vz;
                // 出招里给了 Z 方向的速度才受重力,落到地面就停(推断,E-130);出招以外的动作族(行走里的跳跃之类)在半空里把 Z 速度设成 0 只是"不再上升",
                // 之后照样受重力落回地面(推断:不然怪物每走一圈升一截,永远打不到,E-146)
                _actFalling = vz != 0 || (Phase != MonsterPhase.Attack && Z > 0 && !_hovering);
            }
        }

        /// <summary>脚本的 [SET ACTION]:出招阶段里切到另一招([ATTACK] n)或回待机;别的动作族(走、受击、倒地……)怪物这一版不接。</summary>
        private void ActSetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone)
            {
                return;
            }
            if (Phase != MonsterPhase.Attack)
            {
                ActSetActionOutsideAttack(kind, index);
                return;
            }
            switch (kind)
            {
                case ActActionKind.Attack:
                    if ((uint)index >= (uint)_anis.Attacks.Length || _anis.Attacks[index] == null)
                    {
                        return;
                    }
                    if (atEnd && !now)
                    {
                        _deferredAttack = index;
                        _deferredCustom = -1;
                        return;
                    }
                    _script?.AttackStarted(index);
                    _attackIndex = index;
                    EnterPhase(MonsterPhase.Attack);
                    break;
                case ActActionKind.Custom:
                    if (!CustomExists(index))
                    {
                        CustomActionsRejected++;
                        return;
                    }
                    if (atEnd && !now)
                    {
                        _deferredCustom = index;
                        _deferredAttack = -1;
                        return;
                    }
                    EnterCustom(index);
                    break;
                case ActActionKind.Stand:
                case ActActionKind.Default:
                    if (!(atEnd && !now))
                    {
                        // [END] 的回待机等动画播完,本来就是这样
                        if (_customIndex >= 0)
                        {
                            FinishCustom();
                        }
                        else
                        {
                            FinishAttack();
                        }
                    }
                    break;
            }
        }

        private void ActAttackRect(ActAttackRectMode mode)
        {
            switch (mode)
            {
                case ActAttackRectMode.On:
                    _attackRectOff = false;
                    break;
                case ActAttackRectMode.Off:
                    _attackRectOff = true;
                    break;
                default:
                    // [RESET]:再次允许打中,并让当前帧的攻击框重新判一遍
                    HitHeroThisSwing = false;
                    AttackEvalFrame = Math.Min(AttackEvalFrame, CurrentFrame - 1);
                    break;
            }
        }

        private void ActSetFrame(int frame)
        {
            AniFile ani = CurrentAni;
            if (Phase != MonsterPhase.Attack || ani == null || ani.Frames.Length == 0)
            {
                return;
            }
            AniTimeline timeline = Timeline(ani);
            int f = Math.Max(0, Math.Min(frame, timeline.FrameCount - 1));
            _phaseMs = timeline.StartMs(f) / PhaseRate;
            AttackEvalFrame = f - 1;          // 跳过去的帧不判,从目标帧起判
            _motionVersion++;
        }

        // ------------------------------------------------------------------ 受击盒、浮空与瞬移(E-133)

        private void ActSetDamageBox(ActDamageBoxMode mode)
        {
            switch (mode)
            {
                case ActDamageBoxMode.On:
                    _damageBoxOff = false;
                    break;
                case ActDamageBoxMode.Off:
                    _damageBoxOff = true;
                    break;
            }
        }

        /// <summary>
        /// [CHANGE FLOATING HEIGHT] n(客户端 0x010bdfb8 起):n ≥ 1 记下悬停高度并给一个刚好够到的垂直初速度(±sqrt(2·g·|差|),0x008b25c0),
        /// n ≤ 0 结束悬停、垂直速度清零、之后受重力落回地面。重力取与被动对象同值(推断);到了高度之后"停住不动"也是推断(悬停的物理在虚槽 0x274 里,没读)。
        /// </summary>
        private void ActSetFloatingHeight(int height)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone)
            {
                return;
            }
            if (height >= 1)
            {
                _hovering = true;
                _hoverHeight = height;
                _hoverFalling = false;
                double delta = height - Z;
                _hoverVz = delta == 0 ? 0.0 : Math.Sign(delta) * Math.Sqrt(2.0 * PassiveObject.Gravity * Math.Abs(delta));
            }
            else
            {
                _hovering = false;
                _hoverVz = 0;
                _hoverFalling = Z > 0;
            }
        }

        /// <summary>悬停的一步:升到(或降到)悬停高度后停住;不悬停但离地的(结束悬停、被瞬移到半空)受重力落回地面。</summary>
        private void StepHover(double seconds)
        {
            if (_hovering)
            {
                if (Z == _hoverHeight && _hoverVz == 0)
                {
                    return;
                }
                bool rising = _hoverVz > 0 || (_hoverVz == 0 && Z < _hoverHeight);
                Z += _hoverVz * seconds;
                _hoverVz -= PassiveObject.Gravity * seconds;
                if (rising ? (Z >= _hoverHeight || _hoverVz <= 0) : Z <= _hoverHeight)
                {
                    Z = _hoverHeight;
                    _hoverVz = 0;
                }
                return;
            }
            if (_hoverFalling)
            {
                Z += _hoverVz * seconds;
                _hoverVz -= PassiveObject.Gravity * seconds;
                if (Z <= 0)
                {
                    Z = 0;
                    _hoverVz = 0;
                    _hoverFalling = false;
                }
            }
        }

        /// <summary>
        /// 把脚本召唤出来的怪物摆到 (x, y, z)(E-134):落点按可走区与门体检查,不合法就在 <see cref="Placement.SearchRadius"/> 像素内找最近的能站的点,
        /// 找不到返回 false(调用方改试召唤者的位置或放弃)。成功后记下召唤者与存活时间(<paramref name="lifetimeMs"/> 大于 0 才有限);
        /// 摆在半空的怪物受重力落回地面(与瞬移一致)。
        /// </summary>
        public bool PlaceSummoned(double x, double y, double z, bool facingLeft, IWalkArea walk, IActEntity master, int lifetimeMs)
        {
            _lastWalk = walk;
            if (!Placement.TryResolve((px, py) => CanStandAt(px, py, walk, false, false), x, y, out double rx, out double ry))
            {
                return false;
            }
            X = rx;
            Y = ry;
            Z = Math.Max(0.0, z);
            FacingLeft = facingLeft;
            _hoverVz = 0;
            _hoverFalling = Z > 0;
            ActMaster = master;
            IsSummoned = true;
            _lifeLeftMs = lifetimeMs > 0 ? lifetimeMs : 0;
            return true;
        }

        /// <summary>
        /// [SET HP](客户端 0x010b95c9 起,E-134):小于等于 0 杀死(见 <see cref="ScriptKill"/>),否则直接设成这个值(不超过生命上限,推断:
        /// 客户端的 setHp 在虚槽 0x2a0 里,没读它对上限的处理)。倒下与消失的怪物不理。
        /// </summary>
        private void ActSetHp(int hp)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone)
            {
                return;
            }
            if (hp <= 0)
            {
                ScriptKill();
                return;
            }
            Hp = Math.Min(hp, HpMax);
        }

        /// <summary>
        /// 不经过命中的死亡(脚本把血设成 0、召唤物存活时间到了):与"没有倒地动作时被打死"同一条路——停掉倒地飞行、进入死亡,
        /// 白闪、淡出与消失照常(<see cref="MonsterTuning.DeathMs"/>);不算主角的击杀,没有经验与掉落(E-134)。
        /// </summary>
        private void ScriptKill()
        {
            Hp = 0;
            _dyingFromDown = _flight.Active;
            _flight.Stop(true);
            EnterPhase(MonsterPhase.Dying);
        }

        /// <summary>
        /// [TELEPORT] 摆位置(客户端 0x010b71a4 起):落点在可走区里、没压在门体上就直接摆;不然在 <see cref="Placement.SearchRadius"/> 像素内找最近的能站的点,找不到就不动。
        /// 高度直接取传入的值(小于 0 当 0);摆到半空而又不在悬停的怪物受重力落下(推断)。倒地飞行中、死亡的怪物不动。
        /// </summary>
        private bool ActTeleport(float x, float y, float z)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone || Phase == MonsterPhase.Dying || _flight.Active)
            {
                return false;
            }
            IWalkArea walk = _lastWalk;
            if (!Placement.TryResolve((px, py) => CanStandAt(px, py, walk, false, false), x, y, out double rx, out double ry))
            {
                return false;
            }
            X = rx;
            Y = ry;
            Z = Math.Max(0.0, z);
            SettleHeight();
            _motionVersion++;
            return true;
        }

        /// <summary>
        /// 高度被脚本直接改过([TELEPORT]、[SET POS FORCE])之后的善后:悬停中的怪物重新向悬停高度靠拢(比悬停高度低就给够得着的初速,高就落下来);
        /// 不悬停而被摆到半空的怪物受重力落下(推断)。
        /// </summary>
        private void SettleHeight()
        {
            if (_hovering)
            {
                _hoverVz = Z < _hoverHeight ? Math.Sqrt(2.0 * PassiveObject.Gravity * (_hoverHeight - Z)) : 0.0;
            }
            else
            {
                _hoverVz = 0;
                _hoverFalling = Z > 0;
            }
        }

        /// <summary>
        /// [SET POS FORCE] 每帧摆位置(客户端 0x010a58d9 起,E-152):(x, y) 能站(在可走区里、没压在门体上,与 [TELEPORT] 同一条规则)就整个摆,
        /// 不能就只改高度,X、Y 留在原处;高度直接取传入的值(小于 0 当 0)。与 [TELEPORT] 的区别:不在附近找落点,
        /// 也不改"动画帧变了"的版本号(每帧都调,不能打断同一次更新里后面的触发器)。倒地飞行中、死亡的怪物不动。
        /// </summary>
        private void ActForcePosition(float x, float y, float z)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone || Phase == MonsterPhase.Dying || _flight.Active)
            {
                return;
            }
            if (CanStandAt(x, y, _lastWalk, false, false))
            {
                X = x;
                Y = y;
            }
            Z = Math.Max(0.0, z);
            SettleHeight();
        }

        /// <summary>
        /// 当前动画的总时长(真实毫秒):出招阶段动画按倍速放,总时长也按倍速折算,与脚本计时(真实时间)对得上;没有动画是 0。
        /// [SET POS FORCE] 的起止插值拿它当"全程"(E-152)。
        /// </summary>
        private int ActMotionDurationMs()
        {
            double total = Phase == MonsterPhase.Attack && _customIndex >= 0 ? CustomPassMs() : Duration(CurrentAni);
            if (Phase == MonsterPhase.Attack && PhaseRate > 0)
            {
                total /= PhaseRate;
            }
            return (int)total;
        }

        /// <summary>怪物在 .act 脚本里的身份:位置、朝向取自怪物,脚本的"作用"落到怪物自己的方法上。</summary>
        private sealed partial class ActBody : IActEntity
        {
            internal readonly MonsterActor _m;

            public ActBody(MonsterActor monster)
            {
                _m = monster;
            }

            public ActEntityKind Kind => ActEntityKind.Monster;

            public int ObjectIndex => _m.Code;

            public float X => (float)_m.X;

            public float Y => (float)_m.Y;

            public float Z => (float)_m.Z;

            public bool FacingLeft => _m.FacingLeft;

            public bool IsAlive => (_m.IsAlive && _m.Phase != MonsterPhase.Gone) || _m.LastActRunning;

            public IActEntity Target => _m.AiTarget;

            public IActEntity Master => _m.ActMaster;

            public int MotionVersion => _m._motionVersion;

            public int Hp => _m.Hp;

            public int HpMax => _m.HpMax;

            public int Level => _m.Level;

            /// <summary>当前速度:脚本设的速度([SET SPEED],像素/秒;[ZSPEED]、[IS END MAP] 用,E-166);行走等其余运动不在里面。</summary>
            public float SpeedX => (float)_m.ActSpeedX;

            public float SpeedY => (float)_m.ActSpeedY;

            public float SpeedZ => (float)_m.ActSpeedZ;

            public void SetHp(int hp)
            {
                _m.ActSetHp(hp);
            }

            public void SetFacing(bool left)
            {
                _m.FacingLeft = left;
            }

            public void SetFacingFollowsTarget(bool on)
            {
                _m.FacesTarget = on;
            }

            public void SetAttackFacing(bool on)
            {
                _m.AttackFacing = on;
            }

            public void SetAiTarget(IActEntity target, bool designated)
            {
                _m.SetAiTarget(target, designated);
            }

            public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
            {
                _m.ActSetSpeed(hasX, vx, hasY, vy, hasZ, vz);
            }

            public void SetAction(ActActionKind kind, int index, bool now, bool atEnd)
            {
                _m.ActSetAction(kind, index, now, atEnd);
            }

            public void Destroy(bool force)
            {
                // 怪物被脚本销毁(召唤物自毁之类)这一版不接:怪物只会被打死
            }

            public void AttackRect(ActAttackRectMode mode)
            {
                _m.ActAttackRect(mode);
            }

            public void SetFrame(int frame)
            {
                _m.ActSetFrame(frame);
            }

            public void SetDamageBox(ActDamageBoxMode mode)
            {
                _m.ActSetDamageBox(mode);
            }

            public void SetFloatingHeight(int height)
            {
                _m.ActSetFloatingHeight(height);
            }

            public bool Teleport(float x, float y, float z)
            {
                return _m.ActTeleport(x, y, z);
            }

            public int MotionDurationMs => _m.ActMotionDurationMs();

            public void ForcePosition(float x, float y, float z)
            {
                _m.ActForcePosition(x, y, z);
            }

            public void SetActionTrigger(ActTriggerPool pool, int slot, int trigger, ActTriggerCommand command)
            {
                _m.ActSwitchTrigger(pool, slot, trigger, command);
            }

            public bool? GetActionTrigger(ActTriggerPool pool, int slot, int trigger)
            {
                return _m.ActReadTrigger(pool, slot, trigger);
            }

            private Dictionary<int, IActEntity> _saved;

            /// <summary>[SAVE TARGET OBJECT] 的槽表:挂在怪物身上,各招各族共用,不随出招结束清掉(E-157)。</summary>
            public void SetSavedTarget(int slot, IActEntity target)
            {
                (_saved ?? (_saved = new Dictionary<int, IActEntity>()))[slot] = target;
            }

            public IActEntity GetSavedTarget(int slot)
            {
                return _saved != null && _saved.TryGetValue(slot, out IActEntity target) ? target : null;
            }

            public void AddStatAppendage(StatChange change, int durationMs, IActEntity source)
            {
                _m.AddStatAppendage(change, durationMs, source);
            }

            public void RemoveStatAppendage(StatChange change, IActEntity source)
            {
                _m.RemoveStatAppendage(change, source);
            }

            public bool HasActCategory(int categoryId) => _m.HasActCategory(categoryId);

            public void AddActCategory(int categoryId) => _m.AddActCategory(categoryId);

            public void RemoveActCategory(int categoryId) => _m.RemoveActCategory(categoryId);

            public int ActAiState => _m.AiState;

            public int ActTargetStateHint => _m.TargetAiState;
        }

        /// <summary>
        /// 取走自上次以来发射出来的投射物与发射声音标签(各追加到传入的表里,取走后清空);没有投掷或什么都没发射时返回 false。
        /// 房间会话每一步调一次:投射物从此归会话管(移动、命中、绘制)。
        /// </summary>
        public bool TakeThrown(List<MonsterProjectile> projectiles, List<string> sounds)
        {
            if (_thrownProjectiles.Count == 0 && _throwSounds.Count == 0)
            {
                return false;
            }
            projectiles.AddRange(_thrownProjectiles);
            sounds.AddRange(_throwSounds);
            _thrownProjectiles.Clear();
            _throwSounds.Clear();
            return true;
        }

        /// <summary>
        /// 出招状态的一帧里做投掷的判断(证据 E-103):满足条件的记录放出投射物(起点 = 怪物位置 + 起点偏移,x 偏移随投射物的朝向镜像),
        /// 发射声音存起来;要求"再来一遍"时返回 true,调用方让出招从头开始。
        /// </summary>
        private bool TickThrows()
        {
            ThrowTick tick = _throws.Runner.Tick(_attackIndex, CurrentFrame);
            if (!tick.Any)
            {
                return false;
            }
            foreach (ThrowShot shot in tick.Shots)
            {
                ProjectileTemplate template = _throws.TemplateFor(shot.Record, shot.Slot);
                if (template == null)
                {
                    continue;
                }
                (int ox, int oy, int oz) = _throws.Runner.Record(shot.Record).StartOffset(shot.Slot);
                bool left = FacingLeft ^ shot.Reversed;
                _thrownProjectiles.Add(new MonsterProjectile(this, template, X + (left ? -ox : ox), Y + oy, Z + oz, left));
            }
            foreach (string sound in tick.Sounds)
            {
                _throwSounds.Add(sound);
            }
            return tick.RepeatAttack;
        }

        /// <summary>这一次(或最近一次)出的是第几招(<see cref="MonsterModel.Attacks"/> 的下标)。</summary>
        public int AttackIndex => _attackIndex;

        /// <summary>怪物自己的时钟:出生以来过去的毫秒数(命中停顿里不走);出招决策的"出生后 3 秒内不决策"按它算。</summary>
        public long AiClockMs => (long)_aiClockMs;

        /// <summary>出招决策里目标(主角)的碰撞宽度与进深;射程判断把目标的半边按它们的四分之一放宽(证据 E-101)。缺省 40 与 20。</summary>
        public int TargetWidth { get; set; } = 40;

        public int TargetDepth { get; set; } = 20;

        /// <summary>当前阶段播放的动画。</summary>
        public AniFile CurrentAni
        {
            get
            {
                AniFile ani;
                switch (Phase)
                {
                    case MonsterPhase.Walk:
                        ani = _anis.Move;
                        break;
                    case MonsterPhase.Attack:
                        ani = _customIndex >= 0 ? _customs[_customIndex].Ani : (_attackIndex < _anis.Attacks.Length ? _anis.Attacks[_attackIndex] : null);
                        break;
                    case MonsterPhase.Hurt:
                        ani = _hurtAni;
                        break;
                    case MonsterPhase.Dying:
                        ani = _anis.Down ?? _anis.Damage1;
                        break;
                    default:
                        ani = _anis.Waiting;
                        break;
                }
                return ani ?? _anis.Waiting ?? _anis.Move;
            }
        }

        public string CurrentAnimationPath => _anis.PathOf(CurrentAni);

        public int CurrentFrame
        {
            get
            {
                AniFile ani = CurrentAni;
                if (ani == null)
                {
                    return -1;
                }
                // 倒地时动画自己有时钟:长停留帧在腾空到最高点、落地时才放行,状态切换时还会跳帧(E-082)
                double ms = _flight.Active || _dyingFromDown ? _flight.AniMs : (Phase == MonsterPhase.Attack ? _phaseMs * PhaseRate : _phaseMs);
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
        public void Update(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk, IReadOnlyList<Aabb> heroBoxes = null)
        {
            if (Phase == MonsterPhase.Gone)
            {
                TickGhostFamilies(deltaMs);        // 身体没了,最后一幕的脚本还可以跑完(E-146)
                return;
            }
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            _lastWalk = walk;
            AdvanceStatAppendages(deltaMs);        // 属性改变附加物的时限按墙钟走(M63,E-161)
            AdvanceCamouflage(deltaMs);            // 伪装的时限同样按墙钟走(推断)
            if (_lifeLeftMs > 0 && IsAlive)
            {
                // 召唤物的存活时间按墙钟走(命中停顿里不冻结,推断);到点按脚本杀死处理(E-134)
                _lifeLeftMs -= deltaMs;
                if (_lifeLeftMs <= 0)
                {
                    _lifeLeftMs = 0;
                    ScriptKill();
                }
            }
            if (_hurtOnClock && Phase == MonsterPhase.Hurt)
            {
                // 按公式算的硬直是一块秒表:命中停顿冻结动画、行走与击退,但不冻结它(推断,E-080)
                _hurtClockMs += deltaMs;
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
            _aiClockMs += deltaMs;
            _script?.Advance(deltaMs);
            if (Hp <= 0)
            {
                _deathMs += deltaMs;
            }
            if (_cooldownMs > 0)
            {
                _cooldownMs -= deltaMs;
            }
            switch (Phase)
            {
                case MonsterPhase.Hurt:
                    if (_flight.Active)
                    {
                        UpdateDown(deltaMs, walk);
                        break;
                    }
                    ApplyPush(deltaMs, walk);
                    if ((_hurtOnClock ? _hurtClockMs : _phaseMs) >= _hurtSpanMs)
                    {
                        EnterPhase(MonsterPhase.Wait);
                    }
                    break;
                case MonsterPhase.Dying:
                    FallWhileDying(deltaMs, walk);
                    ApplyPush(deltaMs, walk);
                    break;
                case MonsterPhase.Attack:
                    if (_customIndex < 0 && _throws != null && TickThrows())
                    {
                        EnterPhase(MonsterPhase.Attack);          // 记录的剩余次数还在 2 以上:这一招从头再来一遍(证据 E-103)
                        break;
                    }
                    TrackTargetWhileAttacking(heroX, heroPresent);
                    if (_actRunner != null)
                    {
                        TickActs(deltaMs, walk);
                        if (Phase != MonsterPhase.Attack)
                        {
                            break;
                        }
                    }
                    else if (_motionPlayer != null)
                    {
                        TickMotionEvents();         // 没有触发器的招:叠加层与声音的事件流(只读,不动行为)
                    }
                    if (_phaseMs * PhaseRate >= AttackSpanMs())
                    {
                        if (_deferredCustom >= 0)
                        {
                            EnterCustom(_deferredCustom);               // 脚本用 [END] 切到自定义动作:这一段播完再接(M62-b,E-159)
                            break;
                        }
                        if (_deferredAttack >= 0)
                        {
                            int next = _deferredAttack;
                            _script?.AttackStarted(next);
                            _attackIndex = next;
                            EnterPhase(MonsterPhase.Attack);          // 脚本用 [END] 切招:这一招播完再接下一招(E-130)
                            break;
                        }
                        if (_customIndex >= 0)
                        {
                            FinishCustom();
                        }
                        else
                        {
                            FinishAttack();
                        }
                    }
                    break;
                default:
                    if (Phase == MonsterPhase.Wait)
                    {
                        _throws?.Runner.Idle();                    // 站立状态的每一帧把投掷的剩余次数重置(证据 E-103)
                    }
                    Think(deltaMs, heroX, heroY, heroPresent, walk, heroBoxes);
                    break;
            }
            if (Phase != MonsterPhase.Dying && Phase != MonsterPhase.Gone && !_flight.Active)
            {
                StepHover(deltaMs / 1000.0);
            }
            TickFamilies(deltaMs, walk);
            if (Hp <= 0 && Phase != MonsterPhase.Gone && _deathMs >= MonsterTuning.DeathMs)
            {
                // 从血量归零算起 DeathMs 后消失:倒地流程还没走完(尸体还在空中)也一样(证据 E-084 的基线逐帧)
                EnterPhase(MonsterPhase.Gone);
            }
        }

        private void EnterPhase(MonsterPhase next)
        {
            _customIndex = next == MonsterPhase.Attack ? _nextCustom : -1;       // 进的是自定义动作(M62-b,E-159):出招阶段里放别的动画与程序
            _nextCustom = -1;
            if (_pendingFamily != null || _pendingAttack != null)
            {
                ApplyPendingFor(next);       // [CHANGE ACTION FILE] 换了当时正在跑的那一族:这一次进入才换上(M57,E-151)
            }
            Phase = next;
            _phaseMs = 0;
            if (next == MonsterPhase.Attack)
            {
                if (_customIndex < 0)
                {
                    SwingNumber++;                  // 自定义动作不是一次"出招":不掷出招语音、不重置投掷
                    _throws?.Runner.EnterAttack();
                }
                AttackEvalFrame = -1;
                HitHeroThisSwing = false;
                BeginActs();
            }
            else
            {
                EndActs();
            }
        }

        /// <summary>从开始玩起出招的次数(每次进入出招阶段加 1)。命中判定用它给"同一次出招只打中主角一次"去重。</summary>
        public long SwingNumber { get; private set; }

        /// <summary>这一次出招已经判定到第几帧(-1 表示还没有);会话按"上次判到哪里"补判中间跳过的帧,保证每一帧的攻击框都被检查过。</summary>
        public int AttackEvalFrame { get; set; } = -1;

        /// <summary>这一次出招已经打中过主角(同一次出招的多个攻击框、多帧只算一次)。</summary>
        public bool HitHeroThisSwing { get; set; }

        /// <summary>
        /// 怪物这次出招用的攻击信息:出招脚本 [CHANGE ATTACKINFO] 换上的优先(M69,E-167),其次是这一招 .act 自带的 [DEFAULT ATTACKINFO](<see cref="SetActDefaultStrikes"/>),
        /// 再其次是 <see cref="MonsterModel.AttackInfoPaths"/> 里与当前这一招 <see cref="AttackIndex"/> 对应的那一个;都没有时是全默认值。
        /// 设置它等于设置第 0 招的攻击信息;各招分别的攻击信息用 <see cref="SetStrikes"/> 给。
        /// </summary>
        public HeroStrikeInfo Strike
        {
            get
            {
                if (_strikeOverride.HasValue)
                {
                    return _strikeOverride.Value;
                }
                if (_actDefaultStrikes != null && (uint)_attackIndex < (uint)_actDefaultStrikes.Length && _actDefaultStrikes[_attackIndex].HasValue)
                {
                    return _actDefaultStrikes[_attackIndex].Value;
                }
                return _strikes != null && _attackIndex < _strikes.Length ? _strikes[_attackIndex] : _strike;
            }
            set => _strike = value;
        }

        /// <summary>
        /// 各招出招 .act 自带的默认攻击信息(程序一级的 [DEFAULT ATTACKINFO],与 <see cref="MonsterModel.Attacks"/> 同下标,没写的项是 null;M69,E-167)。
        /// 写了的那一招,默认攻击信息盖过 .mob 表里的(推断:客户端的程序默认在动作开始时套上)。
        /// </summary>
        public void SetActDefaultStrikes(HeroStrikeInfo?[] strikes)
        {
            _actDefaultStrikes = strikes;
        }

        /// <summary>
        /// [CHANGE ATTACKINFO](M69,E-167):出招脚本把当前的攻击信息换成 <paramref name="strike"/>;null 是 [DEFAULT],回到这一招 .act 的默认(没有就是 .mob 表里的)。
        /// 换上的一直用到下一次出招开始。
        /// </summary>
        public void ChangeStrike(HeroStrikeInfo? strike)
        {
            _strikeOverride = strike;
        }

        /// <summary>各招的攻击信息(与 <see cref="MonsterModel.Attacks"/> 同下标);下标越界的招回退到 <see cref="Strike"/> 里设置的那一个。</summary>
        public void SetStrikes(HeroStrikeInfo[] strikes)
        {
            _strikes = strikes;
        }

        /// <summary>怪物的物理攻击力(<see cref="MonsterStats.PhysicalAttack"/>):打主角的占位伤害用;没给就是 0。</summary>
        public int PhysicalAttack { get; set; }

        /// <summary>怪物的魔法攻击力(<see cref="MonsterStats.MagicalAttack"/>):魔法攻击信息打主角时用;没给就是 0。</summary>
        public int MagicalAttack { get; set; }

        /// <summary>
        /// 怪物挨打时的防御面板(<see cref="DefenderPanel.FromMonster"/>:防御力与元素抗性,证据 E-110);没给就按零防御、零抗性、怪物自己的等级算。
        /// </summary>
        public DefenderPanel Defense
        {
            get => _defensePanel;
            set
            {
                _defensePanel = value;
                SyncDefenseCategoryIds();
            }
        }

        private DefenderPanel _defensePanel;

        /// <summary>
        /// 最近一次打中这只怪物的攻击者记号(0 表示还没人打过;主角是 <see cref="RoomCombatSession.HeroAttackerId"/>)。
        /// 客户端在"受击后"槽里把攻击者记在受击方身上、对象初始化时清零;下一次命中算额外命中时拿它与新的攻击者比(证据 E-110)。
        /// </summary>
        public int LastAttackerId { get; set; }

        /// <summary>.mob 里的出场、受击、死亡、出招、环境五种声音标签(证据 E-089);没写的为 null。会话在对应的时刻发声音提示。</summary>
        public MonsterSounds Sounds { get; set; }

        /// <summary>目标血条窗口要的数据(种类、头像、类别、血条总长;证据 E-106);没给就没有血条窗口信息(窗口按普通怪物的默认值画)。</summary>
        public MonsterPanelInfo Panel { get; set; }

        /// <summary>
        /// 出招动画第 <paramref name="frame"/> 帧的攻击框(世界坐标,类型 15,跟着怪物的朝向镜像、跟着离地高度抬升)。
        /// 不在出招阶段、已经死了或这一帧没有攻击框时返回空表。
        /// </summary>
        public List<Aabb> AttackBoxesAt(int frame)
        {
            var result = new List<Aabb>();
            if (!IsAlive || Phase != MonsterPhase.Attack || _attackRectOff)
            {
                return result;
            }
            AniFile ani = CurrentAni;
            if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
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

        /// <summary>出招动画(第 <paramref name="attackIndex"/> 个 [attack motion])所有帧里的攻击框(原始坐标,朝右):判断"这一击能不能够到主角"用。</summary>
        private List<AniBox> StrikeBoxes(int attackIndex = 0)
        {
            if (_strikeBoxes.TryGetValue(attackIndex, out List<AniBox> cached))
            {
                return cached;
            }
            var boxes = new List<AniBox>();
            AniFile ani = attackIndex >= 0 && attackIndex < _anis.Attacks.Length ? _anis.Attacks[attackIndex] : null;
            if (ani != null)
            {
                foreach (AniFrame frame in ani.Frames)
                {
                    foreach (AniBox box in frame.Boxes)
                    {
                        if (box.Type == AniBoxType.Attack)
                        {
                            boxes.Add(box);
                        }
                    }
                }
            }
            _strikeBoxes[attackIndex] = boxes;
            return boxes;
        }

        /// <summary>
        /// 按此刻的位置与朝向出招,攻击框(任意一帧的)会不会碰到主角此刻的受击框。这是个几何近似:原版的怪物(C++ 默认 AI,
        /// 读 [attack kind] 等标签)是按什么距离出招的还没读通,取"挥出去能打中"作为靠近的标准,走到够得着就停(出招条件的推断,P5.M5)。
        /// </summary>
        private bool CanReach(IReadOnlyList<Aabb> heroBoxes, int attackIndex = 0)
        {
            int direction = FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(X), wy = (int)Math.Floor(Y), wz = (int)Math.Floor(Z);
            foreach (AniBox box in StrikeBoxes(attackIndex))
            {
                Aabb strike = Combat.ToWorld(box, wx, wy, wz, direction);
                for (int i = 0; i < heroBoxes.Count; i++)
                {
                    if (strike.Overlaps(heroBoxes[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 出招决策里"没有射程范围的那一招够不够得着目标"(推断,证据 E-101):只对第 0 招(基础攻击)用旧的几何判断——
        /// 给了主角的受击框、而且这一招的动画有攻击框时看攻击框能不能碰到(<see cref="CanReach"/>),否则用固定距离;其余招不靠它触发。
        /// </summary>
        private bool ReachesTarget(int attackIndex)
        {
            if (attackIndex != 0)
            {
                return false;
            }
            bool geometric = _reachBoxes != null && _reachBoxes.Count > 0 && StrikeBoxes(0).Count > 0;
            return geometric
                ? CanReach(_reachBoxes, 0)
                : Math.Abs(_reachDx) <= MonsterTuning.AttackRangeX && Math.Abs(_reachDy) <= MonsterTuning.AttackRangeY;
        }

        private static int RoundToInt(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 待机与行走时的决策:够近就出招,在视野里就走过去,否则站着。
        /// 没装 [attack kind] 记录时(旧占位):给了主角的受击框、而且出招动画有攻击框时,"够近"= 这一击能打到(<see cref="CanReach"/>),走到够得着为止
        /// (最近停在 <see cref="MonsterTuning.MinSeparationX"/> 之外),够近且冷却完了就出招;否则用固定的距离(<see cref="MonsterTuning.AttackRangeX"/>、<see cref="MonsterTuning.AttackRangeY"/>)。
        /// 装了记录时(证据 E-100、E-101):站着时由 <see cref="MonsterAttackChooser"/> 按 [attack delay] 的节奏决定出不出、出哪一招,
        /// "够近"= 有一条可用的记录够得着目标(射程范围或第 0 招的几何判断)。移动与视野仍是占位(原版的移动与朝目标走的细节没有读通)。
        /// </summary>
        private void Think(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk, IReadOnlyList<Aabb> heroBoxes)
        {
            if (_script != null)
            {
                ThinkScripted(deltaMs, heroX, heroY, heroPresent, walk);
                return;
            }
            if (_brain != null)
            {
                ThinkWithBrain(deltaMs, heroX, heroY, heroPresent, walk, heroBoxes);
                return;
            }
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
                TurnTo(dx < 0);
            }
            bool geometric = heroBoxes != null && heroBoxes.Count > 0 && StrikeBoxes().Count > 0;
            bool near;
            if (_chooser != null)
            {
                _reachDx = dx;
                _reachDy = dy;
                _reachBoxes = heroBoxes;
                _chooser.EnterHostile();                       // 看到主角就算进入敌对状态(推断:原版是进入主动出击的状态时放大概率)
                var target = new AttackTarget(RoundToInt(heroX), RoundToInt(heroY), TargetWidth, TargetDepth);
                int selfX = RoundToInt(X), selfY = RoundToInt(Y);
                int pick = Phase == MonsterPhase.Wait
                    ? _chooser.Decide(AiClockMs, selfX, selfY, FacingLeft, true, target, _reachTest)
                    : -1;
                if (pick >= 0)
                {
                    _attackIndex = pick;
                    EnterPhase(MonsterPhase.Attack);
                    return;
                }
                near = _chooser.AnyInRange(selfX, selfY, FacingLeft, target, _reachTest);
            }
            else
            {
                near = geometric
                    ? CanReach(heroBoxes)
                    : Math.Abs(dx) <= MonsterTuning.AttackRangeX && Math.Abs(dy) <= MonsterTuning.AttackRangeY;
            }
            if (near)
            {
                if (_chooser == null && _cooldownMs <= 0 && _anis.Attacks.Length > 0 && _anis.Attacks[0] != null)
                {
                    _attackIndex = 0;
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
            if (Math.Abs(dx) > (geometric || _chooser != null ? MonsterTuning.MinSeparationX : MonsterTuning.AttackRangeX * 0.8))
            {
                stepX = Math.Sign(dx) * Math.Min(SpeedX * seconds, Math.Abs(dx));
            }
            if (Math.Abs(dy) > 3.0)
            {
                stepY = Math.Sign(dy) * Math.Min(SpeedY * seconds, Math.Abs(dy));
            }
            TryMove(stepX, stepY, walk);
        }

        /// <summary>
        /// 装了 .ai 脚本驱动(<see cref="MonsterScript"/>)时待机与行走的决策(证据 E-128):
        /// 主角进了视野才有反应(发现之后一直盯着);每个思考节拍先问动作脚本"出哪一招"(冷却、有没有动画由驱动判断),出了就面朝主角开始出招;
        /// 没出招再按重选间隔问选目的地脚本"往哪走",并问移动方式脚本"走不走";两次思考之间朝上一次选的目的地走(走到 <see cref="MonsterTuning.MinSeparationX"/> 内就站住),
        /// 站着时面朝主角。移动与朝向的细节(方案 1 以外的各种选点、移动方式的快慢)是占位。
        /// </summary>
        private void ThinkScripted(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk)
        {
            MonsterScript script = _script;
            double dx = heroX - X;
            double dy = heroY - Y;
            if (!heroPresent)
            {
                _aware = false;
                _hasDestination = false;
                if (Phase != MonsterPhase.Wait)
                {
                    EnterPhase(MonsterPhase.Wait);
                }
                return;
            }
            if (!_aware && script.Notices(dx))
            {
                _aware = true;
            }
            if (!_aware)
            {
                if (Phase != MonsterPhase.Wait)
                {
                    EnterPhase(MonsterPhase.Wait);
                }
                return;
            }
            if (script.TakeThinkTurn())
            {
                FillAiView(heroX, heroY);
                int attack = script.DecideAttack(_aiView);
                if (attack >= 0)
                {
                    if (Math.Abs(dx) > 1.0)
                    {
                        TurnTo(dx < 0);
                    }
                    script.AttackStarted(attack);
                    _attackIndex = attack;
                    _hasDestination = false;
                    EnterPhase(MonsterPhase.Attack);
                    return;
                }
                if (script.TakeDestinationTurn())
                {
                    AiDestination destination = script.DecideDestination(_aiView);
                    switch (destination.Kind)
                    {
                        case AiDestinationKind.MoveTo:
                            _hasDestination = true;
                            _destinationX = destination.X;
                            _destinationY = destination.Y;
                            break;
                        case AiDestinationKind.Hold:
                            _hasDestination = false;
                            break;
                    }
                }
                _moveMode = script.DecideMove(_aiView);
            }
            bool moving = false;
            if (_hasDestination && _moveMode >= 0)
            {
                double mx = _destinationX - X;
                double my = _destinationY - Y;
                double seconds = deltaMs / 1000.0;
                double stepX = 0, stepY = 0;
                if (Math.Abs(mx) > MonsterTuning.MinSeparationX)
                {
                    stepX = Math.Sign(mx) * Math.Min(SpeedX * seconds, Math.Abs(mx));
                }
                if (Math.Abs(my) > 3.0)
                {
                    stepY = Math.Sign(my) * Math.Min(SpeedY * seconds, Math.Abs(my));
                }
                moving = stepX != 0 || stepY != 0;
                if (moving)
                {
                    if (stepX != 0)
                    {
                        TurnTo(stepX < 0);
                    }
                    TryMove(stepX, stepY, walk);
                }
            }
            if (moving)
            {
                if (Phase != MonsterPhase.Walk)
                {
                    EnterPhase(MonsterPhase.Walk);
                }
                return;
            }
            if (Phase != MonsterPhase.Wait)
            {
                EnterPhase(MonsterPhase.Wait);
            }
            if (Math.Abs(dx) > 1.0)
            {
                TurnTo(dx < 0);                                   // 站着(含走到了目的地)面朝主角
            }
        }

        /// <summary>
        /// 卡住检测(0x0088a4c0):整数位置变了就重新计时;不在走路的动作里也重新计时;在走路的动作里位置超过 <see cref="MonsterBrain.StuckMs"/> 毫秒没变,
        /// 就通知大脑转反向模式。两次思考隔得久(中间出过招、受过击)不算——客户端那时的动作不是走路,时刻一直在刷新。
        /// </summary>
        private void TrackStuck()
        {
            long now = AiClockMs;
            int px = (int)Math.Floor(X), py = (int)Math.Floor(Y);
            bool gap = now - _lastBrainThinkMs > 250;
            _lastBrainThinkMs = now;
            if (gap || px != _stuckX || py != _stuckY || Phase != MonsterPhase.Walk)
            {
                _stuckX = px;
                _stuckY = py;
                _stuckStampMs = now;
                return;
            }
            if (now - _stuckStampMs > MonsterBrain.StuckMs)
            {
                _stuckStampMs = now;
                _brain.NotifyStuck();
            }
        }

        private void FillAiView(double heroX, double heroY)
        {
            MonsterAiView v = _aiView;
            v.X = X;
            v.Y = Y;
            v.FacingLeft = FacingLeft;
            v.Hp = Hp;
            v.HpMax = HpMax;
            v.Level = Level;
            v.State = AiState;
            v.Free = Phase == MonsterPhase.Wait || Phase == MonsterPhase.Walk;
            v.HasTarget = true;
            v.TargetX = heroX;
            v.TargetY = heroY;
            v.TargetState = TargetAiState;
        }

        /// <summary>
        /// 装了走位大脑(<see cref="MonsterBrain"/>)时待机与行走的决策(证据 E-114、E-148):
        /// 先让大脑推进(找目标、切模式、到点选方向);站着<b>或走着</b>时按 [attack kind] 的节奏出招——客户端的出招决策(0x0088d7a0)只要求状态号是 0,
        /// 而怪物走路用的就是状态 0(STATE_STAND;没有"走路"状态),所以边走边出招;决策逐个敌对角色判断射程,与怪物有没有目标、
        /// 是不是敌对模式无关;没有 [attack kind] 的怪物走旧的"够得着 + 冷却"(只在站着时)。不出招就按大脑选的方向走(横向朝哪边就面朝哪边),方向一直保持到大脑下一次选择;
        /// 盯着目标(敌对、绕背、靠近、追击、迟疑)而且站着时面朝主角(推断:出招前转身)。走路的动作里位置一秒没变就通知大脑"被挡住"(0x0088a4c0)。
        /// </summary>
        private void ThinkWithBrain(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk, IReadOnlyList<Aabb> heroBoxes)
        {
            TrackStuck();
            _brain.Update(AiClockMs, heroPresent, X, Y, heroX, heroY);
            if (_chooser != null)
            {
                if (_brain.IsHostile)
                {
                    _chooser.EnterHostile();                   // 进入敌对模式(9、10)时射程里的概率乘 1.3,离开时还原
                }
                else
                {
                    _chooser.LeaveHostile();
                }
            }
            double dx = heroX - X;
            double dy = heroY - Y;
            if (heroPresent)
            {
                if (!_brain.Moving && _brain.IsEngaged && Math.Abs(dx) > 1.0)
                {
                    TurnTo(dx < 0);
                }
                if (Phase == MonsterPhase.Wait || (Phase == MonsterPhase.Walk && _chooser != null))
                {
                    if (_chooser != null)
                    {
                        if (_brain.IsStationary)
                        {
                            return;                           // [stay] 的怪物:客户端的出招决策函数(0x0088d7a0)一开头就按类别返回,不抽签
                        }
                        _reachDx = dx;
                        _reachDy = dy;
                        _reachBoxes = heroBoxes;
                        var target = new AttackTarget(RoundToInt(heroX), RoundToInt(heroY), TargetWidth, TargetDepth);
                        int pick = _chooser.Decide(AiClockMs, RoundToInt(X), RoundToInt(Y), FacingLeft, true, target, _reachTest);
                        if (pick >= 0)
                        {
                            _attackIndex = pick;
                            EnterPhase(MonsterPhase.Attack);
                            return;
                        }
                    }
                    else if (_cooldownMs <= 0 && _anis.Attacks.Length > 0 && _anis.Attacks[0] != null)
                    {
                        bool geometric = heroBoxes != null && heroBoxes.Count > 0 && StrikeBoxes().Count > 0;
                        bool near = geometric
                            ? CanReach(heroBoxes)
                            : Math.Abs(dx) <= MonsterTuning.AttackRangeX && Math.Abs(dy) <= MonsterTuning.AttackRangeY;
                        if (near)
                        {
                            _attackIndex = 0;
                            EnterPhase(MonsterPhase.Attack);
                            return;
                        }
                    }
                }
            }
            if (!_brain.Moving)
            {
                if (Phase != MonsterPhase.Wait)
                {
                    EnterPhase(MonsterPhase.Wait);
                }
                return;
            }
            if (Phase != MonsterPhase.Walk)
            {
                EnterPhase(MonsterPhase.Walk);
            }
            if (_brain.StepX != 0)
            {
                TurnTo(_brain.StepX < 0);
            }
            double seconds = deltaMs / 1000.0;
            TryMove(_brain.StepX * SpeedX * seconds, _brain.StepY * SpeedY * seconds, walk);
        }

        /// <summary>
        /// 房间里挡路的东西(关着的出口门体):和主角共用同一个障碍表,怪物也走不进去——否则游荡的怪物会走到主角够不着的门洞里,房间清不掉(E-114)。
        /// 由房间会话每帧从主角身上取来;已经压在障碍里的怪物(出生点落在门体上)不受它挡,免得永远出不来(与主角的处理一致)。
        /// </summary>
        public IHeroBlocker Blocker { get; set; }

        private bool CanStandAt(double x, double y, IWalkArea walk, bool ignoreBlocker, bool ignoreWalk)
        {
            if (!ignoreWalk && walk != null && !walk.IsUnrestricted && !walk.Contains(x, y))
            {
                return false;
            }
            return ignoreBlocker || Blocker == null || Blocker.BlockedBy(x, y) == null;
        }

        /// <summary>
        /// 走一步。脚本驱动的怪物(M36)与默认 AI 走位大脑的怪物已经站在可走区之外时(出生点落在墙里,常见于首领从屏幕外登场)不受可走区限制,
        /// 走到区里为止,否则它哪一步都迈不出去,房间永远清不掉(推断,E-128、E-148;与上面对障碍物的处理一致)。没有这两种驱动的旧式怪物保持原样:出生在区外就原地不动。
        /// </summary>
        private void TryMove(double stepX, double stepY, IWalkArea walk)
        {
            bool ignoreBlocker = Blocker != null && Blocker.BlockedBy(X, Y) != null;
            bool ignoreWalk = (_script != null || _brain != null) && walk != null && !walk.IsUnrestricted && !walk.Contains(X, Y);
            if (stepX != 0 && CanStandAt(X + stepX, Y, walk, ignoreBlocker, ignoreWalk))
            {
                X += stepX;
            }
            if (stepY != 0 && CanStandAt(X, Y + stepY, walk, ignoreBlocker, ignoreWalk))
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
            double t = _pushEaseMs <= 0 ? 1.0 : Math.Min(1.0, _pushMs / _pushEaseMs);
            double target = _pushTotal * (1.0 - (1.0 - t) * (1.0 - t));
            double step = target - _pushDone;
            if (step <= 0)
            {
                return;
            }
            _pushDone = target;
            TryMove(_pushDir * step, 0, walk);
        }

        // ------------------------------------------------------------------ 倒地状态机(证据 E-082、E-083;运动与动画时钟在 KnockdownFlight,与主角共用,E-105)

        /// <summary>
        /// 开始(或再次开始)倒地:位置与朝向交给 <see cref="KnockdownFlight"/> 算(<see cref="Knockdown.Begin"/>、记录帧跳转),算完读回来;
        /// 怪物一侧只负责阶段(受击)、已死时的躺下转死亡(0x008360e0,E-082、E-083)。
        /// </summary>
        private void BeginDown(KnockdownRequest request, int rollHorizontal, int rollHeight, bool pushToLeft)
        {
            SyncFlightAnimation();
            _hovering = false;                 // 被打得倒地:悬停结束,从当前高度起飞行(E-133)
            _hoverFalling = false;
            _hoverVz = 0;
            _flight.DieOnLying = _dead;
            _flight.Place(X, Y, Z, FacingLeft);
            _flight.Begin(request, rollHorizontal, rollHeight, pushToLeft);
            TakeFlightPose();
            _hurtAni = _anis.Down;
            _hurtOnClock = false;
            _hurtSpanMs = 0;
            EnterPhase(MonsterPhase.Hurt);
            if (_flight.TakeDied())
            {
                EnterDie();
            }
        }

        /// <summary>把飞行状态机算出的位置、离地高度、朝向读回来。</summary>
        private void TakeFlightPose()
        {
            X = _flight.X;
            Z = _flight.Z;
            FacingLeft = _flight.FacingLeft;
        }

        /// <summary>
        /// 死亡(DIE)开始(0x00837612,E-084):水平速度清零,停在刚跳到的躺着那一帧(<see cref="KnockdownFlight"/> 已经处理)。
        /// 原版的 DIE 开始还会换死亡动画、停掉附着的效果;DIE 的每帧函数几乎是空的(只对一类对象清 z 速度),白闪、淡出与消失的时机在别处,没读到,
        /// 这里按基线录像从血量归零算起(<see cref="MonsterTuning.DeathWhiteMs"/>、<see cref="MonsterTuning.DeathMs"/>),与进入 DIE 的早晚无关。
        /// </summary>
        private void EnterDie()
        {
            Z = 0;
            _dyingFromDown = true;
            EnterPhase(MonsterPhase.Dying);
        }

        /// <summary>
        /// 推进倒地状态机 <paramref name="deltaMs"/> 毫秒(运动细节见 <see cref="KnockdownFlight.Update"/>):每次"再请求"都让阶段计时归零(原版的受击阶段重新开始),
        /// 躺完回到待机;已死的怪物躺下就转入死亡。
        /// </summary>
        private void UpdateDown(double deltaMs, IWalkArea walk)
        {
            int before = _flight.BeginCount;
            _flight.DieOnLying = _dead;
            _flight.Place(X, Y, Z, FacingLeft);
            _flight.Update(deltaMs, walk);
            TakeFlightPose();
            if (_flight.BeginCount != before)
            {
                _phaseMs = 0;
            }
            if (_flight.TakeDied())
            {
                EnterDie();
            }
            else if (_flight.TakeEnded())
            {
                EnterPhase(MonsterPhase.Wait);
            }
        }

        /// <summary>在倒地中(含腾空)被打死:带着当前速度落到地上再停,动画停在被打死时的那一帧。</summary>
        private void FallWhileDying(double deltaMs, IWalkArea walk)
        {
            _flight.Place(X, Y, Z, FacingLeft);
            _flight.FallWhileDying(deltaMs, walk);
            TakeFlightPose();
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
            return ApplyHit(damage, knockDown, pushPixels, pushToLeft, default);
        }

        /// <summary>
        /// 同 <see cref="ApplyHit(int, bool, double, bool)"/>,另外带上受击硬直公式的输入(<see cref="HitStunInput"/>;证据 E-080)。
        /// 输入有效、而且这一下进的是受击(不是倒地)时:硬直 = <see cref="HitStun.Damage"/> 算出的毫秒数(从被打中那一刻起算,命中停顿也算在里面),
        /// 击退 = 公式给出的初速与持续(默认 30 像素左右、150 毫秒,与 [push aside] 无关,所以 <paramref name="pushPixels"/> 此时不用);
        /// 否则走占位值(倒地的停留与击退、以及没有硬直表时)。
        /// </summary>
        public bool ApplyHit(int damage, bool knockDown, double pushPixels, bool pushToLeft, HitStunInput stun)
        {
            return ApplyHit(damage, knockDown, pushPixels, pushToLeft, stun, default);
        }

        /// <summary>
        /// 同上,另外带上倒地输入(<see cref="KnockdownInput"/>;证据 E-082、E-083)。输入有效、怪物有倒地动作、而且这一击是倒地攻击
        /// (或是普通受击但怪物已在空中、已在倒地中,E-079 第 6' 步)时,进入倒地状态机:按体重缩放的击飞、z 抛物线、落地后颠一下再躺下;
        /// 这时 <paramref name="pushPixels"/> 是 [push aside](水平初速的来源,像素/秒的量纲,不再是总位移)。
        /// 死亡的那一击:怪物有倒地动作而且给了倒地输入时,走倒地流程(请求用这一击自己的推力与抬升,E-079 第 3 步),躺下的那一刻转入死亡、
        /// 白闪淡出后消失(E-082、E-084);否则直接死亡(在空中被打死时带着当前速度落地)。
        /// <paramref name="elementMask"/> 是这一击的元素标志(位 0 火、1 水、2 暗、3 光,<see cref="HitInput.ElementMask"/>),
        /// 唤醒 [ON DAMAGE] 触发器时给元素条件([IS FIRE DAMAGE] 等)判定用(E-166)。
        /// </summary>
        public bool ApplyHit(int damage, bool knockDown, double pushPixels, bool pushToLeft, HitStunInput stun, KnockdownInput down, bool noReaction = false,
            int elementMask = 0)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone)
            {
                return false;
            }
            ApplyPendingHurt();                                // 每一次挨打都是重新进入受击、倒地几族:记成待换的文件此刻换上(M57)
            if (Phase == MonsterPhase.Attack && _actRunner != null)
            {
                _actRunner.OnDamage(ActTarget, elementMask);   // 出招时挨了打:唤醒带 [ON DAMAGE] 的触发器(E-130;挨打者当作攻击者就是主角;元素条件看这一击的元素,E-166)
            }
            FamilyOnDamage(elementMask);                       // 当前动作(非出招族)与过程动作也收到(E-146)
            _brain?.NotifyDamaged();                           // 挨了打:马上找目标、不等发现迟滞(E-114)
            Hp = Math.Max(0, Hp - Math.Max(0, damage));
            int oldPushDir = _pushDir;
            double oldPushTotal = _pushTotal, oldPushDone = _pushDone, oldPushMs = _pushMs;
            _pushDir = pushToLeft ? -1 : 1;
            _pushTotal = Math.Max(0.0, pushPixels);
            _pushDone = 0;
            _pushMs = 0;
            if (Hp == 0)
            {
                if (down.IsSet && _anis.Down != null)
                {
                    // 已死而且有倒地动作:请求倒地(E-079 第 3 步),列表是这一击自己的推力与抬升,阶段 0;
                    // 倒地流程照常走(腾空、颠一下),躺下的那一刻已死 → 进入死亡(E-082 阶段 2),白闪淡出后消失(E-084)
                    _dead = true;
                    _flight.SeedRolls(unchecked(down.RollHorizontal * 1000 + down.RollHeight + 7));
                    _pushTotal = 0;
                    BeginDown(new KnockdownRequest(0, Knockdown.TypeOf(down.Bounce, down.IgnoreWeight, 0), (int)pushPixels, down.LiftUp),
                        down.RollHorizontal, down.RollHeight, pushToLeft);
                    return true;
                }
                // 没有倒地动作(或调用方没给倒地输入):直接死亡;倒地中(含腾空)被打死时动画停在当前帧,带着当前的速度落到地上
                _dyingFromDown = _flight.Active;
                _flight.Stop(true);
                EnterPhase(MonsterPhase.Dying);
                return true;
            }
            if (noReaction)
            {
                // 决策函数第 1、2、4 步(E-079、E-143):这一击的 [damage reaction] 是 none,或怪物带着霸体而这一击不无视它 →
                // 不请求受击也不请求倒地,只扣了血(上面已扣);没有反应就没有新的击退,当前动作与还没走完的击退照旧
                _pushDir = oldPushDir;
                _pushTotal = oldPushTotal;
                _pushDone = oldPushDone;
                _pushMs = oldPushMs;
                return false;
            }
            if (down.IsSet && _anis.Down != null && (knockDown || (down.ReactionDamage && (_flight.Active || Z > 0))))
            {
                // 输出二:已经落过地(阶段 ≥ 1)的倒地中、又挨了不弹起的一击 → 1(E-079 第 7 步)
                int outputTwo = _flight.Active && _flight.Stage >= 1 && !down.Bounce ? 1 : 0;
                int type = Knockdown.TypeOf(down.Bounce, down.IgnoreWeight, outputTwo);
                _flight.SeedRolls(unchecked(down.RollHorizontal * 1000 + down.RollHeight + 7));
                _pushTotal = 0;
                BeginDown(new KnockdownRequest(outputTwo, type, (int)pushPixels, down.LiftUp), down.RollHorizontal, down.RollHeight, pushToLeft);
                return false;
            }
            // 占位路径:不在倒地状态机里(落到地上;悬停中的怪物挨一下普通受击留在原高度,E-133)
            _flight.Stop(false);
            if (!_hovering)
            {
                Z = 0;
            }
            AniFile ani;
            if (knockDown && _anis.Down != null)
            {
                ani = _anis.Down;
            }
            else
            {
                ani = (_hurtCount++ % 2 == 0 ? _anis.Damage1 : _anis.Damage2) ?? _anis.Damage1 ?? _anis.Damage2 ?? _anis.Waiting;
                knockDown = false;
            }
            _hurtAni = ani;
            _hurtClockMs = 0;
            _pushEaseMs = MonsterTuning.PushEaseMs;
            if (!knockDown && stun.IsSet)
            {
                HitStunResult r = HitStun.Damage(stun.Table, HitRecovery, 0, stun.Rate, stun.ForceStunMs, stun.KnockType, stun.NoKnock);
                _hurtOnClock = true;
                _hurtSpanMs = Math.Max(0, r.StunMs);
                _pushTotal = r.KnockPixels;
                _pushEaseMs = r.KnockMs;
            }
            else
            {
                _hurtOnClock = false;
                _hurtSpanMs = HurtSpan(ani, knockDown);
            }
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
            if (!IsAlive || Phase == MonsterPhase.Gone || Phase == MonsterPhase.Dying || _damageBoxOff)
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
            int wx = (int)Math.Floor(X), wy = (int)Math.Floor(Y), wz = (int)Math.Floor(Z);
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Damage)
                {
                    result.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
            return result;
        }

        /// <summary>
        /// 这只怪物的任何一个动画里有没有受击框。没有的话主角永远打不到它(占位、机关或要靠别的脚本才会现身的东西):
        /// 房间清空的判定不等它(推断:原版的清图条件没有读到,打不到的东西不可能被要求打死,E-128),自动机也不当它是目标。
        /// </summary>
        public bool CanEverBeHit => _model.HasAnyDamageBox;

        /// <summary>当前帧有没有受击框(没有就是打不到:藏着、变身中或别的脚本控制的无敌状态)。不分配内存,自动机每步都要问。</summary>
        public bool HasDamageBox
        {
            get
            {
                if (!IsAlive || Phase == MonsterPhase.Gone || Phase == MonsterPhase.Dying || _damageBoxOff)
                {
                    return false;
                }
                AniFile ani = CurrentAni;
                int frame = CurrentFrame;
                if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
                {
                    return false;
                }
                foreach (AniBox box in ani.Frames[frame].Boxes)
                {
                    if (box.Type == AniBoxType.Damage)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>头顶的世界高度:受击框的最高点(没有受击框时用站立动画第 0 帧的);用来摆伤害数字。</summary>
        public int HeadHeight()
        {
            int top = 0;
            AniFile wait = _anis.Waiting;
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
            return top + (int)Math.Floor(Z);
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
            return MonsterSprites.SpriteAt(assets, ani, _anis.PathOf(ani), frame, projection, (int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Floor(Z),
                !FacingLeft, MonsterSprites.ObjectIdBase + Index, DeathSilhouette, DeathTintAlpha);
        }
    }
}
