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
        private double _hurtClockMs;
        private bool _hurtOnClock;
        private double _pushEaseMs = MonsterTuning.PushEaseMs;

        // 倒地状态机(证据 E-082、E-083;与主角共用 KnockdownFlight,E-105):阶段 0 首次腾空、1 落地后颠一下、2 躺着;类型见 Knockdown.TypeOf
        private readonly int _weight;
        private readonly KnockdownFlight _flight;
        private bool _dyingFromDown;
        private readonly Dictionary<int, List<AniBox>> _strikeBoxes = new Dictionary<int, List<AniBox>>();

        // 默认 AI 的出招决策(证据 E-100、E-101):有 [attack kind] 记录时由 MonsterAttackChooser 决定出哪一招、什么时候出;没有时走旧的"够得着就出招 + 冷却"占位
        private MonsterAttackChooser _chooser;
        private MonsterBrain _brain;
        private double _aiClockMs;
        private int _attackIndex;
        private HeroStrikeInfo _strike;
        private HeroStrikeInfo[] _strikes;
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

        /// <summary>血已经没了、但还在走倒地流程(腾空、颠一下);躺下的那一刻转入死亡(证据 E-079 第 3 步、E-082、E-084)。</summary>
        private bool _dead;

        /// <summary>血量归零之后过去的毫秒数(命中停顿里不走);白闪、淡出、消失都按它算,不管这时还在倒地流程里还是已经进了死亡(证据 E-084)。</summary>
        private double _deathMs;

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
            HitRecovery = hitRecovery;
            _weight = weight;
            _flight = new KnockdownFlight(weight, new DownAnimation(model.Down));
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
                if (i >= _model.Attacks.Count || _model.Attacks[i] == null)
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
            _actHost = host;
            if (programs == null || host == null)
            {
                return;
            }
            foreach (ActProgram p in programs)
            {
                if (p != null && p.Triggers.Count > 0)
                {
                    _actPrograms = programs;
                    return;
                }
            }
        }

        /// <summary>装了 .act 触发器(至少一招有)。</summary>
        public bool HasActs => _actPrograms != null;

        /// <summary>怪物在 .act 脚本里的目标(主角);房间会话装。</summary>
        public IActEntity ActTarget { get; set; }

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

        /// <summary>.act 的触发器通知:这只怪物打中了主角(<paramref name="victim"/>),唤醒带 [ON ATTACKSUCCESS] 的触发器。</summary>
        public void NotifyAttackSuccess(IActEntity victim)
        {
            if (Phase == MonsterPhase.Attack && _actRunner != null)
            {
                _actRunner.OnAttackSuccess(victim);
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
            double seconds = deltaMs / 1000.0;
            if (_actVx != 0 || _actVy != 0)
            {
                TryMove(_actVx * seconds, _actVy * seconds, walk);
            }
            if (_actFalling)
            {
                Z += _actVz * seconds;
                _actVz -= PassiveObject.Gravity * seconds;
                if (Z <= 0)
                {
                    Z = 0;
                    _actVz = 0;
                    _actFalling = false;
                }
            }
        }

        /// <summary>进入出招阶段时挂上这一招的运行器:动作从头开始,触发器开关回到初值,下一步先唤醒 [ON SET ACTION]。</summary>
        private void BeginActs()
        {
            _actVx = 0;
            _actVy = 0;
            _actVz = 0;
            _actFalling = false;
            _attackRectOff = false;
            _deferredAttack = -1;
            _motionVersion++;
            _actRunner = null;
            if (_actPrograms == null || (uint)_attackIndex >= (uint)_actPrograms.Length || _actPrograms[_attackIndex] == null)
            {
                return;
            }
            _actRunner = new ActRunner(_actPrograms[_attackIndex], ActSelf, _actHost);
            _actSetPending = true;
        }

        /// <summary>离开出招阶段:脚本的速度与攻击框开关清零。离开的原因是被打倒(倒地的飞行自己接管 Z)时不碰高度。</summary>
        private void EndActs()
        {
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
            _actRunner = null;
        }

        /// <summary>出完一招(动画播完,或脚本 [SET ACTION] [STAND]):进冷却、回待机;脚本抛起来还没落地的落回地面(推断)。</summary>
        private void FinishAttack()
        {
            if (_actFalling)
            {
                Z = 0;
            }
            _cooldownMs = AttackDelayMs;
            _script?.AttackFinished();
            EnterPhase(MonsterPhase.Wait);
        }

        private void ActSetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
            if (Phase != MonsterPhase.Attack)
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
                _actFalling = vz != 0;     // 给了 Z 方向的速度才受重力,落到地面就停(推断,E-130)
            }
        }

        /// <summary>脚本的 [SET ACTION]:出招阶段里切到另一招([ATTACK] n)或回待机;别的动作族(走、受击、倒地……)怪物这一版不接。</summary>
        private void ActSetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
            if (!IsAlive || Phase != MonsterPhase.Attack)
            {
                return;
            }
            switch (kind)
            {
                case ActActionKind.Attack:
                    if ((uint)index >= (uint)_model.Attacks.Count || _model.Attacks[index] == null)
                    {
                        return;
                    }
                    if (atEnd && !now)
                    {
                        _deferredAttack = index;
                        return;
                    }
                    _script?.AttackStarted(index);
                    _attackIndex = index;
                    EnterPhase(MonsterPhase.Attack);
                    break;
                case ActActionKind.Stand:
                case ActActionKind.Default:
                    if (!(atEnd && !now))
                    {
                        FinishAttack();       // [END] 的回待机等动画播完,本来就是这样
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
            _phaseMs = timeline.StartMs(f) / AttackRate;
            AttackEvalFrame = f - 1;          // 跳过去的帧不判,从目标帧起判
            _motionVersion++;
        }

        /// <summary>怪物在 .act 脚本里的身份:位置、朝向取自怪物,脚本的"作用"落到怪物自己的方法上。</summary>
        private sealed class ActBody : IActEntity
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

            public bool IsAlive => _m.IsAlive && _m.Phase != MonsterPhase.Gone;

            public IActEntity Target => _m.ActTarget;

            public IActEntity Master => null;

            public int MotionVersion => _m._motionVersion;

            public void SetFacing(bool left)
            {
                _m.FacingLeft = left;
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
                        ani = _model.Move;
                        break;
                    case MonsterPhase.Attack:
                        ani = _attackIndex < _model.Attacks.Count ? _model.Attacks[_attackIndex] : null;
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
                // 倒地时动画自己有时钟:长停留帧在腾空到最高点、落地时才放行,状态切换时还会跳帧(E-082)
                double ms = _flight.Active || _dyingFromDown ? _flight.AniMs : (Phase == MonsterPhase.Attack ? _phaseMs * AttackRate : _phaseMs);
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
                return;
            }
            if (deltaMs < 0)
            {
                deltaMs = 0;
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
                    if (_throws != null && TickThrows())
                    {
                        EnterPhase(MonsterPhase.Attack);          // 记录的剩余次数还在 2 以上:这一招从头再来一遍(证据 E-103)
                        break;
                    }
                    if (_actRunner != null)
                    {
                        TickActs(deltaMs, walk);
                        if (Phase != MonsterPhase.Attack)
                        {
                            break;
                        }
                    }
                    if (_phaseMs * AttackRate >= Duration(CurrentAni))
                    {
                        if (_deferredAttack >= 0)
                        {
                            int next = _deferredAttack;
                            _script?.AttackStarted(next);
                            _attackIndex = next;
                            EnterPhase(MonsterPhase.Attack);          // 脚本用 [END] 切招:这一招播完再接下一招(E-130)
                            break;
                        }
                        FinishAttack();
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
            if (Hp <= 0 && Phase != MonsterPhase.Gone && _deathMs >= MonsterTuning.DeathMs)
            {
                // 从血量归零算起 DeathMs 后消失:倒地流程还没走完(尸体还在空中)也一样(证据 E-084 的基线逐帧)
                EnterPhase(MonsterPhase.Gone);
            }
        }

        private void EnterPhase(MonsterPhase next)
        {
            Phase = next;
            _phaseMs = 0;
            if (next == MonsterPhase.Attack)
            {
                SwingNumber++;
                AttackEvalFrame = -1;
                HitHeroThisSwing = false;
                _throws?.Runner.EnterAttack();
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
        /// 怪物这次出招用的攻击信息(<see cref="MonsterModel.AttackInfoPaths"/> 里与当前这一招 <see cref="AttackIndex"/> 对应的那一个);没有时是全默认值。
        /// 设置它等于设置第 0 招的攻击信息;各招分别的攻击信息用 <see cref="SetStrikes"/> 给。
        /// </summary>
        public HeroStrikeInfo Strike
        {
            get => _strikes != null && _attackIndex < _strikes.Length ? _strikes[_attackIndex] : _strike;
            set => _strike = value;
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
        public DefenderPanel Defense { get; set; }

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
            AniFile ani = attackIndex >= 0 && attackIndex < _model.Attacks.Count ? _model.Attacks[attackIndex] : null;
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
                FacingLeft = dx < 0;
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
                if (_chooser == null && _cooldownMs <= 0 && _model.Attacks.Count > 0 && _model.Attacks[0] != null)
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
                        FacingLeft = dx < 0;
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
                        FacingLeft = stepX < 0;
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
                FacingLeft = dx < 0;                              // 站着(含走到了目的地)面朝主角
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
        /// 装了走位大脑(<see cref="MonsterBrain"/>)时待机与行走的决策(证据 E-114):
        /// 先让大脑推进(找目标、切模式、到点选方向);站着时按 [attack kind] 的节奏出招——出招决策逐个敌对角色判断射程,与怪物有没有目标、
        /// 是不是敌对模式无关;没有 [attack kind] 的怪物走旧的"够得着 + 冷却"。不出招就按大脑选的方向走(横向朝哪边就面朝哪边),方向一直保持到大脑下一次选择;
        /// 敌对而且站着(已走到主角所在的格子)时面朝主角(推断:出招前转身)。
        /// </summary>
        private void ThinkWithBrain(double deltaMs, double heroX, double heroY, bool heroPresent, IWalkArea walk, IReadOnlyList<Aabb> heroBoxes)
        {
            _brain.Update(AiClockMs, heroPresent, X, Y, heroX, heroY);
            if (_chooser != null)
            {
                if (_brain.IsHostile)
                {
                    _chooser.EnterHostile();                   // 进入敌对模式时射程里的概率乘 1.3,离开时还原
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
                if (!_brain.Moving && _brain.IsHostile && _brain.HasTarget && Math.Abs(dx) > 1.0)
                {
                    FacingLeft = dx < 0;
                }
                if (Phase == MonsterPhase.Wait)
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
                    else if (_cooldownMs <= 0 && _model.Attacks.Count > 0 && _model.Attacks[0] != null)
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
                FacingLeft = _brain.StepX < 0;
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
        /// 走一步。脚本驱动的怪物(M36)已经站在可走区之外时(出生点落在墙里,常见于首领从屏幕外登场)不受可走区限制,走到区里为止,
        /// 否则它哪一步都迈不出去,房间永远清不掉(推断,E-128;与上面对障碍物的处理一致)。旧式怪物保持原样:出生在区外就原地不动。
        /// </summary>
        private void TryMove(double stepX, double stepY, IWalkArea walk)
        {
            bool ignoreBlocker = Blocker != null && Blocker.BlockedBy(X, Y) != null;
            bool ignoreWalk = _script != null && walk != null && !walk.IsUnrestricted && !walk.Contains(X, Y);
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
            _flight.DieOnLying = _dead;
            _flight.Place(X, Y, Z, FacingLeft);
            _flight.Begin(request, rollHorizontal, rollHeight, pushToLeft);
            TakeFlightPose();
            _hurtAni = _model.Down;
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
        /// </summary>
        public bool ApplyHit(int damage, bool knockDown, double pushPixels, bool pushToLeft, HitStunInput stun, KnockdownInput down)
        {
            if (!IsAlive || Phase == MonsterPhase.Gone)
            {
                return false;
            }
            if (Phase == MonsterPhase.Attack && _actRunner != null)
            {
                _actRunner.OnDamage(ActTarget);                // 出招时挨了打:唤醒带 [ON DAMAGE] 的触发器(E-130;挨打者当作攻击者就是主角)
            }
            _brain?.NotifyDamaged();                           // 挨了打:马上找目标、不等发现迟滞(E-114)
            Hp = Math.Max(0, Hp - Math.Max(0, damage));
            _pushDir = pushToLeft ? -1 : 1;
            _pushTotal = Math.Max(0.0, pushPixels);
            _pushDone = 0;
            _pushMs = 0;
            if (Hp == 0)
            {
                if (down.IsSet && _model.Down != null)
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
            if (down.IsSet && _model.Down != null && (knockDown || (down.ReactionDamage && (_flight.Active || Z > 0))))
            {
                // 输出二:已经落过地(阶段 ≥ 1)的倒地中、又挨了不弹起的一击 → 1(E-079 第 7 步)
                int outputTwo = _flight.Active && _flight.Stage >= 1 && !down.Bounce ? 1 : 0;
                int type = Knockdown.TypeOf(down.Bounce, down.IgnoreWeight, outputTwo);
                _flight.SeedRolls(unchecked(down.RollHorizontal * 1000 + down.RollHeight + 7));
                _pushTotal = 0;
                BeginDown(new KnockdownRequest(outputTwo, type, (int)pushPixels, down.LiftUp), down.RollHorizontal, down.RollHeight, pushToLeft);
                return false;
            }
            // 占位路径:不在倒地状态机里(落到地上)
            _flight.Stop(false);
            Z = 0;
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
                if (!IsAlive || Phase == MonsterPhase.Gone || Phase == MonsterPhase.Dying)
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
            return MonsterSprites.SpriteAt(assets, ani, _model.PathOf(ani), frame, projection, (int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Floor(Z),
                !FacingLeft, MonsterSprites.ObjectIdBase + Index, DeathSilhouette, DeathTintAlpha);
        }
    }
}
