using System;
using System.Collections.Generic;

// 证据:每次更新的动作节拍 0x010ade50(第 0 帧清单次位、补跑跳过的帧、再以当前帧跑一遍)、逐帧运行器 0x010ad580
// (触发器闸:开关与次数、[FRAME] 先看单次位再看区间、[DO BEHAVIOR] 的触发条件)与事件运行器 0x010ab080 / 0x010aefc0 / 0x010a8c30
// 的静态读码(E-130,非 REA 自动化);行为的执行语义见各自的行为卡(CREATE PASSIVEOBJECT、SET SPEED 等)。
namespace Dnf.Game.Act
{
    /// <summary>对象大类(客户端按虚函数槽 +0x34/+0x38 取的种类码在这里压成三种)。</summary>
    public enum ActEntityKind : byte
    {
        Character = 1,
        Monster = 2,
        Passive = 3,
    }

    /// <summary>
    /// 动作脚本里能被选中、被作用的对象(主角、怪物、被动对象)。坐标是世界坐标:X 横向、Y 纵深、Z 离地高度;朝向只有左右两种。
    /// 行为的"效果"由对象自己实现(怎么切动作、怎么设速度取决于它是怪物还是被动对象),运行器只负责"何时对谁做什么"。
    /// </summary>
    public interface IActEntity
    {
        ActEntityKind Kind { get; }

        /// <summary>怪物编号或被动对象编号(主角是 0)。</summary>
        int ObjectIndex { get; }

        float X { get; }
        float Y { get; }
        float Z { get; }
        bool FacingLeft { get; }
        bool IsAlive { get; }

        /// <summary>此对象当前的目标(怪物是它盯着的主角);没有是 null。</summary>
        IActEntity Target { get; }

        /// <summary>召唤者或创建者(被动对象是创建它的怪物);没有是 null。</summary>
        IActEntity Master { get; }

        /// <summary>每次切换动作或设置帧时加一;运行器用它判断"这条行为改变了动画帧"。</summary>
        int MotionVersion { get; }

        /// <summary>当前生命与最大生命([HP] 条件取用);没有生命的对象(被动对象等)最大生命为 0,不满足任何 [HP] 条件。</summary>
        int Hp { get; }

        int HpMax { get; }

        void SetFacing(bool left);

        /// <summary>设速度(世界坐标/秒);X 的符号已经按朝向取过。</summary>
        void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz);

        /// <summary>切动作;<paramref name="index"/> 对 Attack/Custom 是序号;<paramref name="now"/> 立即切,<paramref name="atEnd"/> 等当前动作结束再切。</summary>
        void SetAction(ActActionKind kind, int index, bool now, bool atEnd);

        void Destroy(bool force);

        void AttackRect(ActAttackRectMode mode);

        void SetFrame(int frame);

        /// <summary>[SET DAMAGE BOX]:开关受击盒(E-133)。没有受击盒的对象(被动对象、主角这一版)什么都不做。</summary>
        void SetDamageBox(ActDamageBoxMode mode);

        /// <summary>[CHANGE FLOATING HEIGHT] n:n ≥ 1 升到(或降到)这个高度悬停,n ≤ 0 结束悬停、落回地面(E-133)。没有高度概念的对象什么都不做。</summary>
        void SetFloatingHeight(int height);

        /// <summary>
        /// [SET USE HOMING] n:开关对象身上的追踪(E-165)。只有出生时按 [homing] 块配好了追踪的被动对象有反应,别的对象什么都不做。
        /// </summary>
        void SetUseHoming(bool on)
        {
        }

        /// <summary>
        /// [TELEPORT]:把对象摆到世界坐标 (x, y, z)(E-133)。落点合不合法由对象自己查(落点不合法就在附近找一个,找不到就不动),
        /// 返回是否真的摆过去了。
        /// </summary>
        bool Teleport(float x, float y, float z);

        /// <summary>
        /// 当前动作(动画)的总时长,毫秒(按真实时间,已除掉对象自己的动作倍速);不知道是 0。[SET POS FORCE] 的起止插值拿它当"全程"
        /// (客户端 0x010a5410 起:动作计时对象播放区间两端之差,E-152)。
        /// </summary>
        int MotionDurationMs => 0;

        /// <summary>
        /// [SET POS FORCE] 每帧把对象摆到 (x, y, z)(E-152;客户端 0x010a58d9 起):先问 (x, y, 0) 是不是合法位置(虚槽 0xe0),合法才整个摆(虚槽 0x98);
        /// 不合法只改高度,X 与 Y 留在原处。合法与否、高度怎么落实由对象自己决定;默认什么都不做。
        /// </summary>
        void ForcePosition(float x, float y, float z)
        {
        }

        /// <summary>
        /// 等级([SUMMON MONSTER] 的 [LEVEL] 取它:怪物与主角各自的等级,被动对象取创建它的怪物的;没有等级概念的返回 0,E-134)。
        /// </summary>
        int Level { get; }

        /// <summary>
        /// [SET HP]:把生命设成 <paramref name="hp"/>(E-134)。小于等于 0 是杀死;对象自己决定怎么处理超过上限的值、
        /// 没有生命的对象(被动对象、主角这一版)怎么回应。
        /// </summary>
        void SetHp(int hp);

        /// <summary>
        /// [ACTIVE STATUS]:对这个对象挂一个异常状态槽(E-144)。<paramref name="source"/> 是行为的拥有者(施加者)。
        /// 概率掷骰、等级差修正、抗性与"挂不上"的各种情形由对象自己的状态系统处理(主角走 E-136 的那一套);
        /// 没有状态系统的对象(这一版的怪物、被动对象)什么都不做,所以给了默认的空实现。
        /// </summary>
        void ApplyStatus(ActiveStatusEntry entry, IActEntity source)
        {
        }

        /// <summary>
        /// [TRIGGER] 的按动作族写法([ATTACK] n 触发器序号 [ON]/[OFF]/[RESET] 等,E-145):把行为拥有者另一个动作上的触发器开关打开、关掉或复位。
        /// 动作与它的触发器开关属于拥有者(怪物的每一招一份,出招之间保留),所以由拥有者自己落实;没有这种动作容器的对象什么都不做。
        /// </summary>
        void SetActionTrigger(ActTriggerPool pool, int slot, int trigger, ActTriggerCommand command)
        {
        }

        /// <summary>
        /// [TRIGGER CHECK] 的读侧(E-156):这一族动作上第 <paramref name="trigger"/> 个触发器现在开着吗。
        /// 没有这一族动作、没装触发器、序号越界(客户端:取不到触发器池,或个数不大于序号)返回 null,这个对象会被剔出工作集。
        /// 开关属于拥有者的动作容器(与 <see cref="SetActionTrigger"/> 同一份),没有动作容器的对象(主角、被动对象)默认就是 null。
        /// </summary>
        bool? GetActionTrigger(ActTriggerPool pool, int slot, int trigger) => null;

        /// <summary>
        /// [IS GRABABLE] 要的"此刻能不能被抓"(客户端对生物类对象调虚槽 0x380;基类实现 0x0081ecb0 要求没有冰冻(异常状态 1)与石化(异常状态 7)等,
        /// 主角各职业的外层实现(0x00ca7e00、0x00ce5110)在个别技能状态里再返回不能抓;E-156)。
        /// 抓取等级、免疫计数等其余几项没读出来,默认只看活着(推断);有异常状态的对象(主角)覆盖它。
        /// </summary>
        bool IsGrabbable => IsAlive;

        /// <summary>
        /// [SAVE TARGET OBJECT] 的落点(E-157):把 <paramref name="target"/> 存进这个对象(行为拥有者)的槽表第 <paramref name="slot"/> 格。
        /// 客户端的槽表是拥有者对象身上的一张映射(存取函数 0x007ff0b0 与 0x007fe6e0 的 this 是运行器的 +0x94,即拥有者),
        /// 不随某一招或某一个运行器结束清掉;没有槽表的对象(主角)什么都不存,默认空实现。
        /// </summary>
        void SetSavedTarget(int slot, IActEntity target)
        {
        }

        /// <summary>[LOAD TARGET] 的来源(E-157):槽表第 <paramref name="slot"/> 格存下的对象;没有存过或没有槽表返回 null。</summary>
        IActEntity GetSavedTarget(int slot) => null;

        /// <summary>
        /// [APPENDAGE](E-161):给这个对象挂一项属性改变附加物,<paramref name="durationMs"/> 不大于 0 是没有时限,<paramref name="source"/> 是行为拥有者。
        /// 同一项(类型、比例标志、数值都相同)已经挂着就先去掉旧的(不看来源),所以相同的项不叠加。数值怎么落到对象的速度、生命、防御上由对象自己决定;
        /// 没有属性记录的对象默认什么都不做。
        /// </summary>
        void AddStatAppendage(StatChange change, int durationMs, IActEntity source)
        {
        }

        /// <summary>[DELETE APPENDAGE](E-161):去掉来源是 <paramref name="source"/>、且与 <paramref name="change"/> 相同的属性改变附加物。</summary>
        void RemoveStatAppendage(StatChange change, IActEntity source)
        {
        }
    }

    /// <summary>运行器对世界的请求:随机数、生成被动对象、解析选择器。</summary>
    public interface IActHost
    {
        /// <summary>[0, n) 的随机整数(n &gt;= 1)。</summary>
        int Random(int n);

        /// <summary>单调递增的毫秒时钟:[GET TIME] 与 [DELAY DO BEHAVIOR] 用它(客户端取游戏时间,不随单个对象的命中停顿冻结)。</summary>
        long NowMs { get; }

        /// <summary>生成一个被动对象。(x, y, z) 已经是世界坐标,<paramref name="facingLeft"/> 是它的朝向。</summary>
        void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft);

        /// <summary>
        /// 解析运行器自己不认识的选择器(队伍、全体敌人、某类对象等),把存活的结果追加到 <paramref name="into"/>。
        /// 运行器自己处理 ME、TARGET、SUMMON MASTER、最近的攻击者与被攻击者。
        /// </summary>
        void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into);

        /// <summary>屏幕震动:[SHAKING] 的强度(像素)与持续毫秒(E-133)。整个画面一份,不分目标。</summary>
        void Shake(int strength, int durationMs);

        /// <summary>
        /// 召唤怪物([SUMMON MONSTER],E-134):<paramref name="master"/> 是被作用的对象(召唤者,新怪物的创建者),<paramref name="index"/> 是怪物编号,
        /// <paramref name="level"/> 是已经按规则算好的等级(至少 1;难度表的行数上限由场景来夹),(x, y, z) 是世界坐标,<paramref name="lifetimeMs"/> 大于 0 时是存活毫秒。
        /// 场景可以拒绝(数据缺失、同时存活的召唤物太多、落点找不到)。
        /// </summary>
        void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs);

        /// <summary>
        /// 运行器执行到一条还没做的行为(按空操作处理)时通报一次,<paramref name="tag"/> 是行为标签(E-147)。
        /// 场景拿它统计"实际打到的没做行为",只用来排优先级,不影响运行;默认什么都不做。
        /// </summary>
        void NoteUnsupportedBehavior(string tag)
        {
        }

        /// <summary>
        /// 运行器每次(重新)开始时,脚本里每个"读不懂"的触发器的每种读不懂的标签通报一次(带这种标签的触发器永远不触发,E-147)。
        /// 一次开始 = 一次出招、一次出生或一次进入某个动作族,所以数字按"这个触发器本来该有机会跑几回"加权;默认什么都不做。
        /// </summary>
        void NoteUnsupportedTrigger(string tag)
        {
        }

        /// <summary>
        /// [CHANGE ACTION FILE](E-151):把怪物 <paramref name="target"/> 的一族动作(<paramref name="slot"/>,受击、出招、自定义三族再带 <paramref name="index"/>)
        /// 换成 <paramref name="path"/> 这个 .act;<paramref name="path"/> 是脚本里的原样写法,相对写这条行为的程序 <paramref name="fromProgram"/> 所在的目录。
        /// 路径读不到、序号越界、这一族怪物没有的情形由场景自己决定怎么办(客户端:序号越界跳过);默认什么都不做。
        /// </summary>
        void ChangeActionFile(IActEntity target, ActFileSlot slot, int index, string path, string fromProgram)
        {
        }
    }

    /// <summary>
    /// 一个对象在一个动作上跑触发器的运行器。每次更新调用 <see cref="Update"/> 一次(传当前动画帧与经过的秒数),
    /// 命中与受击等事件另行通知。语义(E-130):
    /// 触发器先过开关与次数闸;逐条记录求值,条件失败整条放弃,[WHICH] 重选工作集,过滤条件缩小工作集(空集合也算通过),
    /// [DO BEHAVIOR] 在目标集合非空(或前面有 [CHECKED NO] 通过)时执行行为组并记一次触发;
    /// [FRAME] 与事件触发器还受"单次位"约束:执行完一次后置位,直到动画回到第 0 帧才复位(行为里动画帧变了则不置位)。
    /// 读不懂的记录让整个触发器不触发。
    /// </summary>
    public sealed class ActRunner
    {
        /// <summary>排队等待的 [DELAY DO BEHAVIOR]:到期的毫秒时钟、行为组号、排队那一刻定下的目标。</summary>
        private sealed class Delayed
        {
            public long DueMs;
            public int Group;
            public List<IActEntity> Targets;
        }

        /// <summary>
        /// 一条强制位移记录(E-152):对应客户端 0x38 字节的记录——目标、三个轴的起点与终点和"写了几个值"、基准点来源、朝向来源、被移动的是谁。
        /// 值在执行时求定,之后每次更新按目标当时的位置重新算落点。
        /// </summary>
        private sealed class ForcedMove
        {
            public IActEntity Target;
            public readonly int[] Start = new int[3];
            public readonly int[] End = new int[3];
            public readonly int[] Count = new int[3];
            public ActForceOrigin Origin;
            public ActForceDirection Direction;
            public bool MoveMe;
        }

        /// <summary>"这条触发器不带事件记录"的标记(与 <see cref="ActRecordKind.Unsupported"/> 共用同一个值,事件数组里不会出现真正的 Unsupported)。</summary>
        private const ActRecordKind NoEvent = ActRecordKind.Unsupported;

        private readonly ActProgram _program;
        private readonly IActEntity _owner;
        private readonly IActHost _host;
        private readonly Func<int, int> _rand;
        private readonly bool[] _enabled;
        private readonly bool _enabledShared;
        private readonly int[] _fired;
        private readonly bool[] _oneShot;
        private readonly float[] _clock;
        private readonly ActRecordKind[] _event;
        private readonly bool[] _inert;
        private readonly string[] _unsupportedTriggerTags;
        private readonly List<IActEntity> _set = new List<IActEntity>();
        private readonly List<IActEntity> _targets = new List<IActEntity>();
        private readonly Dictionary<string, int> _vars = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Delayed> _delayed = new List<Delayed>();

        /// <summary>[SET POS FORCE] 的强制位移表(客户端:动作上偏移 0x43c 的记录队列,每次执行这条行为先清空;动作重来时也清空,E-152)。</summary>
        private readonly List<ForcedMove> _forced = new List<ForcedMove>();

        private float _time;
        private int _lastFrame = -1;
        private bool _loopReset;
        private int _frame;
        private bool _aborted;
        private IActEntity _victim;
        private IActEntity _attacker;

        /// <summary>
        /// <paramref name="sharedEnabled"/>:触发器开关的持久副本(长度要等于触发器个数,用 <see cref="NewEnabledState"/> 建)。
        /// 客户端里 [TRIGGER] n [ON]/[OFF] 改的是这一招的触发器条目上的开关字节(+0x1c),那个条目属于怪物、跨出招保留,
        /// [ENABLE] 的初值只在载入时写一次,出招开始时的复位函数(0x010a3fe0)只清单次位与计数,不碰开关(E-134);
        /// 所以怪物把同一份数组交给它每一次出招的运行器,开关就跨出招保留,.ai 的 is attack action enable() 也读同一份。
        /// 不给(或长度对不上)时运行器自己一份,每次 <see cref="Begin"/> 回到脚本里写的初值(被动对象走这条:它们每次都是新建的)。
        /// </summary>
        public ActRunner(ActProgram program, IActEntity owner, IActHost host, bool[] sharedEnabled = null)
        {
            _program = program ?? throw new ArgumentNullException(nameof(program));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _rand = host.Random;
            int n = program.Triggers.Count;
            _enabledShared = sharedEnabled != null && sharedEnabled.Length == n;
            _enabled = _enabledShared ? sharedEnabled : new bool[n];
            _fired = new int[n];
            _oneShot = new bool[n];
            _clock = new float[n];
            _event = new ActRecordKind[n];
            _inert = new bool[n];
            List<string> unsupportedTags = null;
            for (int i = 0; i < n; i++)
            {
                ActTrigger t = program.Triggers[i];
                _event[i] = NoEvent;
                foreach (ActRecord r in t.Records)
                {
                    if (IsEvent(r.Kind))
                    {
                        _event[i] = r.Kind;
                        if (r.Kind == ActRecordKind.OnOtherEvent)
                        {
                            _inert[i] = true;      // 这一类事件没有接线:触发器永远不会被唤醒
                        }
                        break;
                    }
                }
                if (!t.Supported)
                {
                    _inert[i] = true;
                    if (unsupportedTags == null)
                    {
                        unsupportedTags = new List<string>();
                    }
                    foreach (string tag in t.UnsupportedTags)
                    {
                        unsupportedTags.Add(tag);
                    }
                }
            }
            _unsupportedTriggerTags = unsupportedTags == null ? Array.Empty<string>() : unsupportedTags.ToArray();
            Begin();
        }

        public ActProgram Program => _program;

        public IActEntity Owner => _owner;

        /// <summary>已经执行过的触发次数(测试与审计用)。</summary>
        public int TotalFired
        {
            get
            {
                int total = 0;
                foreach (int f in _fired)
                {
                    total += f;
                }
                return total;
            }
        }

        public int FiredCount(int trigger) => trigger >= 0 && trigger < _fired.Length ? _fired[trigger] : 0;

        public bool IsEnabled(int trigger) => trigger >= 0 && trigger < _enabled.Length && _enabled[trigger];

        private static bool IsEvent(ActRecordKind kind)
        {
            return kind == ActRecordKind.OnSetAction || kind == ActRecordKind.OnAttackSuccess || kind == ActRecordKind.OnDamage || kind == ActRecordKind.OnOtherEvent;
        }

        /// <summary>一份触发器开关数组,每个触发器取脚本里 [ENABLE] 写的初值(没写算开);交给 <see cref="ActRunner(ActProgram, IActEntity, IActHost, bool[])"/> 当持久开关。</summary>
        public static bool[] NewEnabledState(ActProgram program)
        {
            if (program == null)
            {
                throw new ArgumentNullException(nameof(program));
            }
            var state = new bool[program.Triggers.Count];
            for (int i = 0; i < state.Length; i++)
            {
                state[i] = program.Triggers[i].Enabled;
            }
            return state;
        }

        /// <summary>
        /// 动作(重新)开始:次数、单次位、计时清零;开关回到脚本里写的初值——除非开关是调用方给的持久副本(见构造函数),那种不动,
        /// 留着上一次出招里 [TRIGGER] 行为改过的样子。
        /// </summary>
        public void Begin()
        {
            for (int i = 0; i < _enabled.Length; i++)
            {
                if (!_enabledShared)
                {
                    _enabled[i] = _program.Triggers[i].Enabled;
                }
                _fired[i] = 0;
                _oneShot[i] = false;
                _clock[i] = 0f;
            }
            _time = 0f;
            _lastFrame = -1;
            _loopReset = false;
            _frame = 0;
            _aborted = false;
            _victim = null;
            _attacker = null;
            _delayed.Clear();      // 排队的延迟行为随动作重来作废(推断);变量不清,留到下一次(E-132)
            _forced.Clear();       // 强制位移表属于动作,动作重来时清空(E-152)
            foreach (string tag in _unsupportedTriggerTags)
            {
                _host.NoteUnsupportedTrigger(tag);
            }
        }

        /// <summary>变量当前值([DIM] 赋的;没赋过读 0,推断);测试与审计用。</summary>
        public bool TryGetVariable(string name, out int value) => _vars.TryGetValue(name, out value);

        /// <summary>排队中的延迟行为条数(测试用)。</summary>
        public int PendingDelayed => _delayed.Count;

        /// <summary>
        /// 每次更新调用一次。<paramref name="frame"/> 是此刻动画的当前帧;帧号比上次大一以上时先按顺序补跑被跳过的帧(客户端的做法),
        /// 再以当前帧跑一遍(每次更新都跑,单次位挡住重复)。
        /// </summary>
        public void Update(int frame, float dt)
        {
            if (_inert.Length == 0)
            {
                return;
            }
            _aborted = false;
            _time += dt;
            RunDelayed();
            if (frame != 0)
            {
                _loopReset = false;
            }
            if ((frame == 0 && !_loopReset) || frame < _lastFrame)
            {
                // 走到第 0 帧:这一圈复位一次(客户端 0x010ade50 的做法:在第 0 帧且"这一圈复位过"的标志还没立起来时才复位,离开第 0 帧才清标志,
                // 所以停在第 0 帧的好几次更新里只复位一次);循环动画绕了一圈却没踩到第 0 帧(更新比帧长),帧号变小同样算(推断,客户端没有这一条)
                for (int i = 0; i < _oneShot.Length; i++)
                {
                    _oneShot[i] = false;
                }
                _loopReset = frame == 0;
            }
            if (frame > _lastFrame + 1)
            {
                for (int f = _lastFrame + 1; f < frame && !_aborted; f++)
                {
                    RunFrame(f);
                }
            }
            if (!_aborted)
            {
                RunFrame(frame);
            }
            _lastFrame = frame;
            _frame = frame;
            ApplyForcedMoves();
        }

        /// <summary>主角被此对象的攻击命中(<paramref name="victim"/>);唤醒带 [ON ATTACKSUCCESS] 的触发器。</summary>
        public void OnAttackSuccess(IActEntity victim)
        {
            _victim = victim;
            Dispatch(ActRecordKind.OnAttackSuccess);
        }

        /// <summary>此对象受到了 <paramref name="attacker"/> 的攻击;唤醒带 [ON DAMAGE] 的触发器。</summary>
        public void OnDamage(IActEntity attacker)
        {
            _attacker = attacker;
            Dispatch(ActRecordKind.OnDamage);
        }

        /// <summary>此对象的动作被切换了;唤醒带 [ON SET ACTION] 的触发器。</summary>
        public void OnSetAction()
        {
            Dispatch(ActRecordKind.OnSetAction);
        }

        private void Dispatch(ActRecordKind evt)
        {
            if (_inert.Length == 0)
            {
                return;
            }
            _aborted = false;
            for (int i = 0; i < _inert.Length && !_aborted; i++)
            {
                if (_event[i] == evt)
                {
                    Evaluate(i, _frame, evt);
                }
            }
        }

        private void RunFrame(int frame)
        {
            _frame = frame;
            for (int i = 0; i < _inert.Length && !_aborted; i++)
            {
                if (_event[i] != NoEvent)
                {
                    continue;      // 带事件记录的触发器只被事件唤醒
                }
                Evaluate(i, frame, NoEvent);
            }
        }

        private void Evaluate(int index, int frame, ActRecordKind evt)
        {
            if (_inert[index] || !_enabled[index])
            {
                return;
            }
            ActTrigger t = _program.Triggers[index];
            if (t.Limit > 0 && _fired[index] >= t.Limit)
            {
                return;
            }
            if (evt != NoEvent && _oneShot[index])
            {
                return;
            }
            _set.Clear();
            _set.Add(_owner);
            bool allowEmpty = false;
            IReadOnlyList<ActRecord> records = t.Records;
            for (int r = 0; r < records.Count; r++)
            {
                ActRecord rec = records[r];
                switch (rec.Kind)
                {
                    case ActRecordKind.Frame:
                        if (_oneShot[index] || frame < rec.A || frame > rec.B)
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.CheckTime:
                        if (!CheckTime(index, rec.Number))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.Which:
                        Reselect(rec);
                        break;
                    case ActRecordKind.CheckedNo:
                        if (!ActCompares.Test(_set.Count, rec.Sign, rec.Number))
                        {
                            return;
                        }
                        allowEmpty = true;
                        break;
                    case ActRecordKind.ZPos:
                    case ActRecordKind.Distance:
                    case ActRecordKind.IsIndex:
                    case ActRecordKind.IsNotIndex:
                    case ActRecordKind.IsObjectType:
                    case ActRecordKind.IsLeft:
                    case ActRecordKind.IsRight:
                    case ActRecordKind.FrontSide:
                    case ActRecordKind.IsInPos:
                    case ActRecordKind.IsChild:
                    case ActRecordKind.IsGrabbable:
                        FilterSet(rec);
                        break;
                    case ActRecordKind.IsDirectionLeft:
                        if (!_owner.FacingLeft)
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.IsDirectionRight:
                        if (_owner.FacingLeft)
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.GetTarget:
                        if (!ApplyGetTarget(rec))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.CheckDistance:
                        if (!CheckDistance(rec))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.SaveTarget:
                        ApplySaveTarget(rec);
                        break;
                    case ActRecordKind.TriggerCheck:
                        if (rec.TriggerPool == ActTriggerPool.Own)
                        {
                            // 种类 3 是整体真假(客户端 0x010d8564 起):自己这个动作的第 n 个触发器开着且要 [ON],或关着且要 [OFF] 才通过;序号越界不通过(推断)
                            if ((uint)rec.Number >= (uint)_enabled.Length || _enabled[rec.Number] != rec.Flag)
                            {
                                return;
                            }
                        }
                        else
                        {
                            FilterSet(rec);
                        }
                        break;
                    case ActRecordKind.If:
                        if (!ActCompares.Test(Eval(rec.Left), rec.Sign, Eval(rec.Right)))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.Hp:
                        FilterSet(rec);
                        break;
                    case ActRecordKind.OnSetAction:
                    case ActRecordKind.OnAttackSuccess:
                    case ActRecordKind.OnDamage:
                        if (rec.Kind != evt)
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.DoBehavior:
                    case ActRecordKind.DirectDoBehavior:
                        if (t.Limit > 0 && _fired[index] >= t.Limit)
                        {
                            return;
                        }
                        if (!Fire(index, rec, allowEmpty))
                        {
                            return;
                        }
                        if (_aborted)
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.DelayDoBehavior:
                        if (t.Limit > 0 && _fired[index] >= t.Limit)
                        {
                            return;
                        }
                        if (!Enqueue(index, rec, allowEmpty))
                        {
                            return;
                        }
                        break;
                    default:
                        return;
                }
            }
        }

        private bool CheckTime(int index, int milliseconds)
        {
            // 周期性基准:自上一次通过(或动作开始)起经过的时间达到 milliseconds 才通过,通过时基准前移到现在(E-130 推断)
            if (_time - _clock[index] >= milliseconds / 1000f)
            {
                _clock[index] = _time;
                return true;
            }
            return false;
        }

        private static float Distance(IActEntity a, IActEntity b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>过滤条件:只留下满足的对象(空集合照样通过,是否触发由后面的 [CHECKED NO] 与 [DO BEHAVIOR] 决定)。</summary>
        private void FilterSet(ActRecord rec)
        {
            int w = 0;
            for (int i = 0; i < _set.Count; i++)
            {
                if (Keep(rec, _set[i]))
                {
                    _set[w++] = _set[i];
                }
            }
            _set.RemoveRange(w, _set.Count - w);
        }

        private bool Keep(ActRecord rec, IActEntity e)
        {
            switch (rec.Kind)
            {
                case ActRecordKind.ZPos:
                    return ActCompares.Test((int)e.Z, rec.Sign, rec.Number);
                case ActRecordKind.Distance:
                    return ActCompares.Test((int)Distance(_owner, e), rec.Sign, rec.Number);
                case ActRecordKind.IsIndex:
                    return Array.IndexOf(rec.Ints, e.ObjectIndex) >= 0;
                case ActRecordKind.IsNotIndex:
                    return Array.IndexOf(rec.Ints, e.ObjectIndex) < 0;
                case ActRecordKind.IsObjectType:
                    return Array.IndexOf(rec.Ints, (int)e.Kind) >= 0;
                case ActRecordKind.IsLeft:
                    return e.X <= _owner.X;
                case ActRecordKind.IsRight:
                    return e.X > _owner.X;
                case ActRecordKind.Hp:
                    return KeepHp(rec, e);
                case ActRecordKind.FrontSide:
                    // 目标正面朝向拥有者(客户端 0x010d5186 起的块,只比 X 坐标与朝向,E-132):目标在拥有者左边或同位置就要朝右,在右边就要朝左;
                    // 客户端还要求目标的 +0x36c 槽返回 1(语义不明,按"活着"处理,推断)
                    return e.IsAlive && (e.X <= _owner.X ? !e.FacingLeft : e.FacingLeft);
                case ActRecordKind.IsInPos:
                    return (int)e.X == rec.Ints[0] && (int)e.Y == rec.Ints[1] && (int)e.Z == rec.Ints[2];
                case ActRecordKind.IsChild:
                {
                    // 客户端 0x010d8bbb 起的块:目标的父对象(虚槽 0x1f4)与拥有者比较;参数非 0 留下相等的,0 留下不相等的(E-134)
                    bool child = ReferenceEquals(e.Master, _owner);
                    return rec.Number != 0 ? child : !child;
                }
                case ActRecordKind.IsGrabbable:
                    // 客户端 0x010d7bd0 起的块(E-156):对象要转得成生物类(被动对象转不过去,剔除),再取虚槽 0x380 的"能否被抓";
                    // 参数为 1 留下能抓的,别的值留下不能抓的
                    return e.Kind != ActEntityKind.Passive && e.IsGrabbable == (rec.Number == 1);
                case ActRecordKind.TriggerCheck:
                {
                    // 非自身的写法逐对象过滤(客户端 0x010d85bb 起):取不到那一族动作的触发器池或序号不小于个数就剔除;
                    // 开关与要求一致([ON] 要开着、[OFF] 要关着)才留下
                    bool? on = e.GetActionTrigger(rec.TriggerPool, rec.TriggerSlot, rec.Number);
                    return on.HasValue && on.Value == rec.Flag;
                }
                default:
                    return true;
            }
        }

        private void Reselect(ActRecord rec)
        {
            _set.Clear();
            Select(rec.Selector, rec.PartyIndex, _set);
        }

        private void Select(ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            switch (selector)
            {
                case ActSelector.Me:
                    into.Add(_owner);
                    break;
                case ActSelector.Target:
                    if (_owner.Target != null && _owner.Target.IsAlive)
                    {
                        into.Add(_owner.Target);
                    }
                    break;
                case ActSelector.SummonMaster:
                    if (_owner.Master != null && _owner.Master.IsAlive)
                    {
                        into.Add(_owner.Master);
                    }
                    break;
                case ActSelector.LastAttackSuccess:
                case ActSelector.LastAttackSuccesses:
                case ActSelector.LastActiveAttackSuccess:
                    if (_victim != null)
                    {
                        into.Add(_victim);
                    }
                    break;
                case ActSelector.CharacterAttackSuccess:
                    if (_victim != null && _victim.Kind == ActEntityKind.Character)
                    {
                        into.Add(_victim);
                    }
                    break;
                case ActSelector.LastAttacker:
                case ActSelector.LastActiveAttacker:
                case ActSelector.LastAttackers:
                    if (_attacker != null)
                    {
                        into.Add(_attacker);
                    }
                    break;
                case ActSelector.WorkingSet:
                    break;
                case ActSelector.LoadTarget:
                {
                    // 取拥有者槽表里存下的那个对象(客户端选择器码 0x15;E-157);已经死掉的不算(推断:客户端槽里是指针,销毁时的清理没读出来)
                    IActEntity saved = _owner.GetSavedTarget(partyIndex);
                    if (saved != null && saved.IsAlive)
                    {
                        into.Add(saved);
                    }
                    break;
                }
                default:
                    _host.Resolve(_owner, selector, partyIndex, into);
                    break;
            }
        }

        /// <summary>
        /// [HP] 条件(客户端 0x010d3f66 起的求值块,E-132):取对象的 getHp,[%] 写法的阈值是 最大生命 × 数 / 100(32 位乘法、截断除法,
        /// 大血量会溢出,与客户端一致),[POINT] 写法直接用数;没有生命的对象(最大生命 0)被剔除。
        /// </summary>
        private static bool KeepHp(ActRecord rec, IActEntity e)
        {
            int max = e.HpMax;
            if (max <= 0)
            {
                return false;
            }
            int limit = rec.Percent ? unchecked(max * rec.Number) / 100 : rec.Number;
            return ActCompares.Test(e.Hp, rec.Sign, limit);
        }

        private int[] _keys = new int[8];

        private readonly List<IActEntity> _scratch = new List<IActEntity>();

        /// <summary>
        /// [SAVE TARGET OBJECT](客户端记录码 0x3d,每帧运行器 0x010ad580 里直接处理,存的函数 0x010d1f40,E-157):
        /// 把选择结果里的第一个对象存进拥有者的槽表;结果为空不动槽(函数结尾只在收集到的个数不为 0 时才调存槽函数 0x007ff0b0)。
        /// 选择器是工作集就取当前的工作集(客户端选择码 9),别的选择器各取它的结果。这一步不是条件,不影响触发器通不通过。
        /// </summary>
        private void ApplySaveTarget(ActRecord rec)
        {
            IActEntity first = null;
            if (rec.Selector == ActSelector.WorkingSet)
            {
                first = _set.Count > 0 ? _set[0] : null;
            }
            else
            {
                _scratch.Clear();
                Select(rec.Selector, -1, _scratch);
                first = _scratch.Count > 0 ? _scratch[0] : null;
                _scratch.Clear();
            }
            if (first != null)
            {
                _owner.SetSavedTarget(rec.Number, first);
            }
        }

        /// <summary>
        /// [CHECK DISTANCE] x 范围 y 范围(客户端条件码 0x3e,块在 0x010d44f4,E-156):整体真假,不改工作集。
        /// 拥有者要转得成生物类(被动对象转不过去,条件不通过);它的虚槽 0x64 要返回非零(语义没读出来,按成立,推断);
        /// 再调 0x008791a0(拥有者, x, y):场上角色类对象(类型码 0x11)里,有一个与拥有者敌对(0x00872c60)、不在 0xc8–0xcc 号状态(死亡一类,按"活着")、
        /// 横向差的绝对值不大于 x 且纵深差的绝对值不大于 y 的,就通过;位置按整数比较。判定函数的返回值在条件块里是真正被用的(之前读成"丢掉返回值"是反编译的错觉)。
        /// </summary>
        private bool CheckDistance(ActRecord rec)
        {
            if (_owner.Kind == ActEntityKind.Passive)
            {
                return false;
            }
            _scratch.Clear();
            _host.Resolve(_owner, ActSelector.AllEnemy, -1, _scratch);
            int ox = (int)_owner.X;
            int oy = (int)_owner.Y;
            bool found = false;
            for (int i = 0; i < _scratch.Count && !found; i++)
            {
                IActEntity e = _scratch[i];
                found = e.IsAlive && e.Kind == ActEntityKind.Character
                    && Math.Abs((int)e.X - ox) <= rec.Ints[0] && Math.Abs((int)e.Y - oy) <= rec.Ints[1];
            }
            _scratch.Clear();
            return found;
        }

        /// <summary>
        /// [GET TARGET] n 键 [HIGH]/[LOW](客户端判定函数 0x010d3630 里条件码 0x2c 的块,E-155):工作集非空时按键整理顺序,再只留前 n 个(从尾巴往外删);
        /// 工作集一开始就是空的,这条条件当场不通过(块开头 0x010d7ccb 测个数为零就跳到"返回假"的出口 0x010d3ed3),整个触发器不触发,返回假。键:[RANDOM] 洗牌(模式 9);[DISTANCE] 对拥有者的整数距离(模式 5,按距离入多重映射再从头 [LOW] 或从尾 [HIGH] 取,
        /// 距离相同的 [HIGH] 与插入顺序相反);[LEVEL]、[HP] 走稳定排序的比较函数(模式 6、7):[LOW] 小的在前,[LEVEL] [HIGH] 大的在前,
        /// 而 [HP] [HIGH] 的比较函数(0x010d1160)两次都取同一个对象的生命,恒为"不小于",等于不排序——原版自己的笔误,照搬;
        /// [LAST ATTACKSUCCESS](模式 10)把"拥有者最近命中的对象"里与工作集重合的那个挪到最前([HIGH])或最后([LOW])。
        /// 个数 n 为负的写法客户端行为未定义(要从空数组里删),这里当作不截断(推断)。
        /// </summary>
        private bool ApplyGetTarget(ActRecord rec)
        {
            int count = _set.Count;
            if (count == 0)
            {
                return false;
            }
            switch (rec.TargetKey)
            {
                case ActTargetKey.Random:
                    for (int i = count - 1; i > 0; i--)
                    {
                        int j = _rand(i + 1);
                        IActEntity tmp = _set[i];
                        _set[i] = _set[j];
                        _set[j] = tmp;
                    }
                    break;
                case ActTargetKey.Distance:
                    SortSetByKey(rec.TargetKey, false);
                    if (rec.Descending)
                    {
                        _set.Reverse();
                    }
                    break;
                case ActTargetKey.Level:
                    SortSetByKey(rec.TargetKey, rec.Descending);
                    break;
                case ActTargetKey.Hp:
                    if (!rec.Descending)
                    {
                        SortSetByKey(rec.TargetKey, false);
                    }
                    break;
                case ActTargetKey.LastAttackSuccess:
                    if (_victim != null)
                    {
                        int at = _set.IndexOf(_victim);
                        if (at >= 0)
                        {
                            _set.RemoveAt(at);
                            if (rec.Descending)
                            {
                                _set.Insert(0, _victim);
                            }
                            else
                            {
                                _set.Add(_victim);
                            }
                        }
                    }
                    break;
            }
            int max = rec.Number;
            if (max >= 0 && max < _set.Count)
            {
                _set.RemoveRange(max, _set.Count - max);
            }
            return true;
        }

        private int TargetKeyOf(ActTargetKey key, IActEntity e)
        {
            switch (key)
            {
                case ActTargetKey.Distance: return (int)Distance(_owner, e);
                case ActTargetKey.Hp: return e.Hp;
                case ActTargetKey.Level: return e.Level;
                default: return 0;
            }
        }

        /// <summary>稳定的插入排序(集合很小):<paramref name="descending"/> 为假小的在前,为真大的在前;键相同的保持原有顺序。</summary>
        private void SortSetByKey(ActTargetKey key, bool descending)
        {
            int n = _set.Count;
            if (_keys.Length < n)
            {
                _keys = new int[n * 2];
            }
            for (int i = 0; i < n; i++)
            {
                _keys[i] = TargetKeyOf(key, _set[i]);
            }
            for (int i = 1; i < n; i++)
            {
                IActEntity item = _set[i];
                int k = _keys[i];
                int j = i;
                while (j > 0 && (descending ? k > _keys[j - 1] : k < _keys[j - 1]))
                {
                    _set[j] = _set[j - 1];
                    _keys[j] = _keys[j - 1];
                    j--;
                }
                _set[j] = item;
                _keys[j] = k;
            }
        }

        /// <summary>求表达式:整数、变量(没赋过读 0)、[GET TIME](宿主毫秒时钟的低 32 位,溢出回绕,差值照样对);加减按 32 位回绕。</summary>
        private int Eval(ActExpression e)
        {
            int a = Term(e.First);
            switch (e.Op)
            {
                case ActArith.Add:
                    return unchecked(a + Term(e.Second));
                case ActArith.Subtract:
                    return unchecked(a - Term(e.Second));
                default:
                    return a;
            }
        }

        private int Term(ActTerm t)
        {
            switch (t.Kind)
            {
                case ActTermKind.Number:
                    return t.Number;
                case ActTermKind.GetTime:
                    return unchecked((int)_host.NowMs);
                default:
                    return _vars.TryGetValue(t.Name, out int v) ? v : 0;
            }
        }

        /// <summary>
        /// 执行 [DELAY DO BEHAVIOR]:和 [DO BEHAVIOR] 一样选目标、记一次触发、置单次位,但行为组不立刻执行,
        /// 而是把"到期时刻 + 行为组号 + 此刻的目标"排进队列,由之后的 <see cref="Update"/> 到期执行(E-132)。
        /// </summary>
        private bool Enqueue(int index, ActRecord rec, bool allowEmpty)
        {
            _targets.Clear();
            if (rec.Selector == ActSelector.WorkingSet)
            {
                _targets.AddRange(_set);
            }
            else
            {
                Select(rec.Selector, rec.PartyIndex, _targets);
            }
            if (_targets.Count == 0 && !allowEmpty)
            {
                return false;
            }
            _fired[index]++;
            int group = rec.Behavior.Eval(_rand);
            if (group >= 0 && rec.Number >= 0)
            {
                // 行为组号或延迟是负数就不排队(客户端 0x010adaed 起的两个符号判断),次数照加
                _delayed.Add(new Delayed
                {
                    DueMs = _host.NowMs + rec.Number,
                    Group = group,
                    Targets = new List<IActEntity>(_targets),
                });
            }
            _oneShot[index] = true;       // 排队本身不动动画,与 [DO BEHAVIOR] 的"没动画变化就置位"一致(0x010adc08 起)
            return true;
        }

        /// <summary>每次更新开头:把到期的延迟行为按排队顺序执行(目标里已经死掉的不再作用)。</summary>
        private void RunDelayed()
        {
            if (_delayed.Count == 0)
            {
                return;
            }
            long now = _host.NowMs;
            int i = 0;
            while (i < _delayed.Count && !_aborted)
            {
                Delayed d = _delayed[i];
                if (d.DueMs > now)
                {
                    i++;
                    continue;
                }
                _delayed.RemoveAt(i);
                d.Targets.RemoveAll(t => !t.IsAlive);
                RunGroup(d.Group, d.Targets);
            }
        }

        private void RunGroup(int k, List<IActEntity> targets)
        {
            if (k < 0 || k >= _program.Behaviors.Count)
            {
                return;
            }
            int version = _owner.MotionVersion;
            Execute(_program.Behaviors[k], targets);
            if (_owner.MotionVersion != version)
            {
                _aborted = true;       // 延迟行为切了动作或改了帧:同一次更新里后面的触发器不再求值(与 [DO BEHAVIOR] 一致)
            }
        }

        /// <summary>执行 [DO BEHAVIOR]。返回是否真的触发(目标集合为空且没有 [CHECKED NO] 担保时不触发)。</summary>
        private bool Fire(int index, ActRecord rec, bool allowEmpty)
        {
            _targets.Clear();
            if (rec.Selector == ActSelector.WorkingSet)
            {
                _targets.AddRange(_set);
            }
            else
            {
                Select(rec.Selector, rec.PartyIndex, _targets);
            }
            if (_targets.Count == 0 && !allowEmpty)
            {
                return false;
            }
            _fired[index]++;
            int k = rec.Behavior.Eval(_rand);
            int version = _owner.MotionVersion;
            if (k >= 0 && k < _program.Behaviors.Count)
            {
                // 行为里的 [DESTROY] 等可能改动对象列表,先复制一份目标
                Execute(_program.Behaviors[k], new List<IActEntity>(_targets));
            }
            if (_owner.MotionVersion == version)
            {
                _oneShot[index] = true;
            }
            else
            {
                _aborted = true;       // 行为切了动作或改了帧:这个动作上的后续触发器不再求值
            }
            return true;
        }

        private void Execute(ActBehaviorGroup group, List<IActEntity> targets)
        {
            foreach (ActBehavior b in group.Items)
            {
                switch (b.Kind)
                {
                    case ActBehaviorKind.CreatePassive:
                        foreach (IActEntity t in targets)
                        {
                            Create(b, t);
                        }
                        break;
                    case ActBehaviorKind.SetSpeed:
                        foreach (IActEntity t in targets)
                        {
                            Speed(b, t);
                        }
                        break;
                    case ActBehaviorKind.SetAction:
                        foreach (IActEntity t in targets)
                        {
                            if (t.IsAlive)
                            {
                                t.SetAction(b.Action, b.ActionIndex.Eval(_rand), b.Now, b.AtEnd);
                            }
                        }
                        break;
                    case ActBehaviorKind.Destroy:
                        foreach (IActEntity t in targets)
                        {
                            t.Destroy(b.Force);
                        }
                        break;
                    case ActBehaviorKind.AttackRect:
                        foreach (IActEntity t in targets)
                        {
                            t.AttackRect(b.Rect);
                        }
                        break;
                    case ActBehaviorKind.SetDirection:
                        foreach (IActEntity t in targets)
                        {
                            Direction(b, t);
                        }
                        break;
                    case ActBehaviorKind.SetFrame:
                        foreach (IActEntity t in targets)
                        {
                            if (b.Frame > 0)
                            {
                                t.SetFrame(b.Frame);
                            }
                        }
                        break;
                    case ActBehaviorKind.TriggerSwitch:
                        SwitchTrigger(b);
                        break;
                    case ActBehaviorKind.SetVariable:
                        _vars[b.VariableName] = Eval(b.Expression);      // 变量属于运行器(每个对象每个动作一份),与目标集合无关
                        break;
                    case ActBehaviorKind.SetDamageBox:
                        foreach (IActEntity t in targets)
                        {
                            t.SetDamageBox(b.DamageBox);
                        }
                        break;
                    case ActBehaviorKind.SetFloatingHeight:
                        foreach (IActEntity t in targets)
                        {
                            if (t.IsAlive)
                            {
                                t.SetFloatingHeight(b.Height);
                            }
                        }
                        break;
                    case ActBehaviorKind.SetUseHoming:
                        foreach (IActEntity t in targets)
                        {
                            t.SetUseHoming(b.UseHoming);
                        }
                        break;
                    case ActBehaviorKind.Shake:
                        if (b.ShakeMs >= 0)
                        {
                            _host.Shake(b.ShakeStrength, b.ShakeMs);     // 第二个参数为负时客户端不震(0x010c5f22 起的条件);不分目标,整个画面只震一次
                        }
                        break;
                    case ActBehaviorKind.Teleport:
                        foreach (IActEntity t in targets)
                        {
                            Teleport(b, t);
                        }
                        break;
                    case ActBehaviorKind.Summon:
                        Summon(b, targets);
                        break;
                    case ActBehaviorKind.SetPosForce:
                        SetPosForce(b, targets);
                        break;
                    case ActBehaviorKind.SetHp:
                    {
                        // 值先取一次(客户端 0x010b95c9:hp 在目标循环之前求值),再对每个活着的目标设置(E-134)
                        int hp = b.Hp.Eval(_rand);
                        foreach (IActEntity t in targets)
                        {
                            if (t.IsAlive)
                            {
                                t.SetHp(hp);
                            }
                        }
                        break;
                    }
                    case ActBehaviorKind.Restore:
                        Restore(b, targets);
                        break;
                    case ActBehaviorKind.ChangeActionFile:
                        // 客户端 0x010ca404 起的块:对集合里每个怪物(主角没有 .mob 式的动作表,被动对象也没有)各换一次;怪物自己决定正在跑的那一族怎么收尾
                        foreach (IActEntity t in targets)
                        {
                            if (t.Kind == ActEntityKind.Monster)
                            {
                                _host.ChangeActionFile(t, b.FileSlot, b.FileIndex, b.FilePath, _program.Path);
                            }
                        }
                        break;
                    case ActBehaviorKind.ActiveStatus:
                        // 客户端 0x010bbec6 起的块:对集合里每个"活动对象"(类型位 0x11,主角与怪物;被动对象没有)调一次挂状态,源头是行为拥有者(E-144)
                        foreach (IActEntity t in targets)
                        {
                            if (t.Kind != ActEntityKind.Passive && t.IsAlive)
                            {
                                t.ApplyStatus(b.Status, _owner);
                            }
                        }
                        break;
                    case ActBehaviorKind.Appendage:
                        // 客户端行为码 0x84 的块(E-161):对集合里每个"活动对象"(主角与怪物;被动对象没有附加物表)把每一项属性改变各挂成一个附加物,
                        // 来源是行为拥有者;死了的目标不挂(推断:客户端这一块的目标检查没有逐条读)
                        foreach (IActEntity t in targets)
                        {
                            if (t.Kind == ActEntityKind.Passive || !t.IsAlive)
                            {
                                continue;
                            }
                            foreach (StatChange change in b.StatChanges)
                            {
                                t.AddStatAppendage(change, b.StatDurationMs, _owner);
                            }
                        }
                        break;
                    case ActBehaviorKind.DeleteAppendage:
                        // 客户端行为码 0xad 的块(E-161):对每一项、对集合里每个活动对象,作废来源是行为拥有者且与这一项相同的附加物
                        foreach (IActEntity t in targets)
                        {
                            if (t.Kind == ActEntityKind.Passive)
                            {
                                continue;
                            }
                            foreach (StatChange change in b.StatChanges)
                            {
                                t.RemoveStatAppendage(change, _owner);
                            }
                        }
                        break;
                    default:
                        _host.NoteUnsupportedBehavior(b.Tag);     // 没做的行为按空操作处理(ActBehaviorGroup.UnsupportedTags 供审计;实际打到几次由场景统计,E-147)
                        break;
                }
                if (_aborted)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// [SET POS FORCE](客户端执行块 0x010bfa89,E-152):先清空这个动作的强制位移表,再把记号按出现顺序读一遍(值表达式此刻求定,所有目标共用;
        /// 每个轴的"写了几个值"逐个累加,终点写在起点前面或单独写终点时起点同取这个值),最后给每个活动对象(主角与怪物,被动对象不收;
        /// 客户端问虚槽 0x30 的类型位 0x11,再问"可抓取"虚槽 0x380,后一条这一版不查)登记一条记录。落点每次更新重新算,见 <see cref="ApplyForcedMoves"/>。
        /// 缺省:基准取目标自己、朝向取目标的、被移动的是目标、线性插值。
        /// </summary>
        private void SetPosForce(ActBehavior b, List<IActEntity> targets)
        {
            _forced.Clear();
            var template = new ForcedMove();
            foreach (ActForceToken token in b.ForceTokens)
            {
                switch (token.Code)
                {
                    case ActForceCode.XStart:
                    case ActForceCode.YStart:
                    case ActForceCode.ZStart:
                    {
                        int axis = (int)token.Code;
                        template.Start[axis] = token.Value.Eval(_rand);
                        template.Count[axis]++;
                        break;
                    }
                    case ActForceCode.XEnd:
                    case ActForceCode.YEnd:
                    case ActForceCode.ZEnd:
                    {
                        int axis = (int)token.Code - (int)ActForceCode.XEnd;
                        int value = token.Value.Eval(_rand);
                        template.End[axis] = value;
                        if (template.Count[axis]++ == 0)
                        {
                            template.Start[axis] = value;
                        }
                        break;
                    }
                    case ActForceCode.UseMyDirection:
                        template.Direction = ActForceDirection.Owner;
                        break;
                    case ActForceCode.NotUseObjectDirection:
                        template.Direction = ActForceDirection.Right;
                        break;
                    case ActForceCode.UseTargetPos:
                        template.Origin = ActForceOrigin.Target;
                        break;
                    case ActForceCode.UseMyBasePos:
                        template.Origin = ActForceOrigin.Owner;
                        break;
                    case ActForceCode.UseMapPos:
                        template.Origin = ActForceOrigin.Map;
                        break;
                    case ActForceCode.MoveTarget:
                        template.MoveMe = false;
                        break;
                    case ActForceCode.MoveMe:
                        template.MoveMe = true;
                        break;
                }
            }
            foreach (IActEntity t in targets)
            {
                if (t.Kind == ActEntityKind.Passive)
                {
                    continue;
                }
                var move = new ForcedMove { Target = t, Origin = template.Origin, Direction = template.Direction, MoveMe = template.MoveMe };
                for (int axis = 0; axis < 3; axis++)
                {
                    move.Start[axis] = template.Start[axis];
                    move.End[axis] = template.End[axis];
                    move.Count[axis] = template.Count[axis];
                }
                _forced.Add(move);
            }
        }

        /// <summary>
        /// 每次更新对强制位移表里每条记录重新算落点并摆过去(客户端动作更新里 0x010a5042 起的消费者,E-152):
        /// 基准点取目标当时的位置、拥有者当时的位置或地图原点(0,0,0);朝向取目标的、拥有者的或恒朝右,朝左时 X 的偏移取反(Y、Z 不取反);
        /// 每个轴按"写了几个值"取:0 个取目标自己此刻的坐标(不管被移动的是谁),1 个是基准加这个值,2 个是基准加起点到终点的线性插值
        /// (进度 = 动作已过的时间 / 动作总时长,到时间或总时长不明就取终点;浮点按单精度算、向零截断),多于 2 个落 0(客户端没给这个分支赋值)。
        /// 被移动的是拥有者([MOVE ME])或目标;落点交给对象的 <see cref="IActEntity.ForcePosition"/>。
        /// </summary>
        private void ApplyForcedMoves()
        {
            if (_forced.Count == 0)
            {
                return;
            }
            int elapsed = (int)(_time * 1000f);
            int duration = _owner.MotionDurationMs;
            for (int i = 0; i < _forced.Count; i++)
            {
                ForcedMove f = _forced[i];
                IActEntity target = f.Target;
                if (!target.IsAlive)
                {
                    continue;
                }
                IActEntity origin = f.Origin == ActForceOrigin.Owner ? _owner : f.Origin == ActForceOrigin.Target ? target : null;
                bool right;
                switch (f.Direction)
                {
                    case ActForceDirection.Owner:
                        right = !_owner.FacingLeft;
                        break;
                    case ActForceDirection.Right:
                        right = true;
                        break;
                    default:
                        right = !target.FacingLeft;
                        break;
                }
                float x = ForceAxis(f, 0, origin == null ? 0f : origin.X, right, elapsed, duration, target.X);
                float y = ForceAxis(f, 1, origin == null ? 0f : origin.Y, right, elapsed, duration, target.Y);
                float z = ForceAxis(f, 2, origin == null ? 0f : origin.Z, right, elapsed, duration, target.Z);
                (f.MoveMe ? _owner : target).ForcePosition(x, y, z);
            }
        }

        private static float ForceAxis(ForcedMove f, int axis, float origin, bool right, int elapsed, int duration, float current)
        {
            int offset;
            switch (f.Count[axis])
            {
                case 0:
                    return current;
                case 1:
                    offset = f.Start[axis];
                    break;
                case 2:
                    offset = ForceLerp(f.Start[axis], f.End[axis], elapsed, duration);
                    break;
                default:
                    return 0f;
            }
            return axis == 0 && !right ? origin - offset : origin + offset;
        }

        /// <summary>客户端的线性插值函数 0x011b98c0:到时间(含总时长为 0)取终点,时间不大于 0 取起点,其余 起点 + (终点 - 起点) × 已过 / 总长,单精度,向零截断。</summary>
        internal static int ForceLerp(int start, int end, int elapsed, int duration)
        {
            if (elapsed >= duration)
            {
                return end;
            }
            if (elapsed <= 0)
            {
                return start;
            }
            float s = start;
            float e = end;
            return (int)((e - s) * elapsed / duration + s);
        }

        /// <summary>
        /// [RESTORE] [HP]:数量先取一次(客户端 0x010b8c89 起的块里 valueOf 在目标循环之前),按百分比时每个目标算 数量 × 最大生命 / 100(有符号、向零截断,
        /// 最大生命取目标自己的),再给每个活着的目标加上;客户端只发"恢复请求"、由服务端真正改生命(那一半没读),
        /// 所以结果怎么夹是推断:上不超过最大生命,降到 0 或以下就是死(与 [SET HP] 同一条路)。没有最大生命的对象(被动对象等)不动;[MP] 没有对应的量,空操作。
        /// </summary>
        private void Restore(ActBehavior b, List<IActEntity> targets)
        {
            int amount = b.RestoreAmount.Eval(_rand);
            if (b.RestoreMp)
            {
                return;
            }
            foreach (IActEntity t in targets)
            {
                int max = t.HpMax;
                if (!t.IsAlive || max <= 0)
                {
                    continue;
                }
                int delta = b.RestorePercent ? (int)((long)amount * max / 100) : amount;
                if (delta == 0)
                {
                    continue;
                }
                long hp = (long)t.Hp + delta;
                t.SetHp((int)Math.Max(0L, Math.Min(max, hp)));
            }
        }

        private void SwitchTrigger(ActBehavior b)
        {
            if (b.TriggerPool != ActTriggerPool.Own)
            {
                // 客户端的执行块只看行为拥有者,不看目标集合:从拥有者取那一族动作的触发器池,再按序号写开关(0x010b861b,E-145)
                _owner.SetActionTrigger(b.TriggerPool, b.TriggerSlot, b.TriggerIndex, b.Command);
                return;
            }
            ApplyTrigger(b.TriggerIndex, b.Command);
        }

        /// <summary>
        /// 本运行器这个动作自己的第 <paramref name="n"/> 个触发器:开、关,或复位(清触发次数与单次位、计时重新起算;复位的客户端实现在受保护区,这一条是推断)。
        /// 拥有者把"另一个动作上的开关"落实到正在跑的这一个动作时也走这里(E-145)。
        /// </summary>
        public void ApplyTrigger(int n, ActTriggerCommand command)
        {
            if (n < 0 || n >= _enabled.Length)
            {
                return;
            }
            switch (command)
            {
                case ActTriggerCommand.On:
                    _enabled[n] = true;
                    break;
                case ActTriggerCommand.Off:
                    _enabled[n] = false;
                    break;
                case ActTriggerCommand.Reset:
                    _fired[n] = 0;
                    _oneShot[n] = false;
                    _clock[n] = _time;
                    break;
            }
        }

        private void Create(ActBehavior b, IActEntity target)
        {
            IActEntity baseObject = b.UseMyBasePos ? _owner : target;
            int index = b.Index.Eval(_rand);
            int level = b.Level != null ? b.Level.Eval(_rand) : 0;
            int px = b.PosX.Eval(_rand);
            int py = b.PosY.Eval(_rand);
            int pz = b.PosZ.Eval(_rand);

            // 朝向码:0 左,1 右,4 中立,5 随机;没有固定就取 [USE MY DIRECTION](创建者的)或被作用对象的(客户端汇编)
            int dir;
            if (b.FixDirection >= 0)
            {
                dir = b.FixDirection == 5 ? _host.Random(2) : b.FixDirection;
            }
            else
            {
                IActEntity facing = b.UseMyDirection ? _owner : target;
                dir = facing.FacingLeft ? 0 : 1;
            }
            bool left = dir != 1;      // X 偏移在朝向不是 1 时取反(0x010b6091),地图坐标写法除外

            float x = b.UseMapPos ? px : baseObject.X + (left ? -px : px);
            float y = b.UseMapPos ? py : baseObject.Y + py;
            float z = b.UseObjectZ ? baseObject.Z + pz : pz;
            _host.SpawnPassive(_owner, index, level, x, y, z, left);
        }

        /// <summary>
        /// [SUMMON MONSTER](客户端执行块 0x010b683d 起,E-134)。编号、等级、三个偏移在目标循环之前各取一次(所有目标共用同一组随机数);
        /// 每个活着的目标:
        /// 等级 = [LEVEL](-1 取目标的等级)加上目标的等级(目标是被动对象时不加),至少 1;
        /// 朝向取目标自己的,[USE MY DIRECTION] 取拥有者的,否则 [NOT USE OBJECT DIRECTION] 固定朝右(客户端的朝向 1);
        /// 位置:[USE MAP POS] 三个值原样是绝对坐标;否则以目标为基准([USE MY BASEPOS] 以拥有者为基准),X 偏移在朝左时取反,Y 直接加,
        /// Z 是偏移本身、[USE OBJECT ZPOS] 时再加基准的高度。创建请求交给场景(客户端是发一个创建请求包,由服务端造怪)。
        /// </summary>
        private void Summon(ActBehavior b, List<IActEntity> targets)
        {
            int index = b.Index.Eval(_rand);
            int level = b.Level != null ? b.Level.Eval(_rand) : 0;
            int ox = b.PosX.Eval(_rand);
            int oy = b.PosY.Eval(_rand);
            int oz = b.PosZ.Eval(_rand);
            foreach (IActEntity t in targets)
            {
                if (!t.IsAlive)
                {
                    continue;
                }
                int lv = level == -1 ? t.Level : level;
                if (t.Kind != ActEntityKind.Passive)
                {
                    lv += t.Level;
                }
                if (lv < 1)
                {
                    lv = 1;
                }
                bool left = t.FacingLeft;
                if (b.UseMyDirection)
                {
                    left = _owner.FacingLeft;
                }
                else if (b.NotUseDirection)
                {
                    left = false;
                }
                float x = ox;
                float y = oy;
                float z = oz;
                if (!b.UseMapPos)
                {
                    IActEntity baseObject = b.UseMyBasePos ? _owner : t;
                    x = baseObject.X + (left ? -ox : ox);
                    y = baseObject.Y + oy;
                    z = (b.UseObjectZ ? baseObject.Z : 0f) + oz;
                }
                _host.SummonMonster(t, index, lv, x, y, z, left, b.SummonTimeMs);
            }
        }

        /// <summary>
        /// [TELEPORT](客户端执行块 0x010b71a4 起,E-133):三个值取出来之后,位置按下面的顺序定,朝向 0(左)时 X 偏移取反——
        /// 朝向取对象自己的,[USE MY DIRECTION] 时取拥有者的;[USE MAP POS] 是绝对坐标;[USE MY BASEPOS] 以拥有者为基准,否则以被作用的对象为基准
        /// (X 偏移取反的规则相同,[NOT USE OBJECT DIRECTION] 时不取反;Z 偏移默认加基准的高度,[NOT USE OBJECT ZPOS] 时不加);
        /// 没有 [USE MY BASEPOS] 却写了 [NOT USE OBJECT DIRECTION] 时三个值原样当绝对坐标。被移动的是被作用的对象,[MOVE ME] 时是拥有者。
        /// </summary>
        private void Teleport(ActBehavior b, IActEntity target)
        {
            int ox = b.PosX.Eval(_rand);
            int oy = b.PosY.Eval(_rand);
            int oz = b.PosZ.Eval(_rand);
            bool left = (b.UseMyDirection ? _owner : target).FacingLeft;
            float x = ox;
            float y = oy;
            float z = oz;
            if (!b.UseMapPos)
            {
                IActEntity baseObject = null;
                if (b.UseMyBasePos)
                {
                    baseObject = _owner;
                }
                else if (!b.NotUseDirection)
                {
                    baseObject = target;
                }
                if (baseObject != null)
                {
                    x = baseObject.X + (left && !b.NotUseDirection ? -ox : ox);
                    y = baseObject.Y + oy;
                    z = (b.NotUseZ ? 0f : baseObject.Z) + oz;
                }
            }
            if (b.AllowFixture)
            {
                return;
            }
            IActEntity mover = b.MoveMe ? _owner : target;
            mover.Teleport(x, y, z);
        }

        private void Speed(ActBehavior b, IActEntity target)
        {
            if (!target.IsAlive)
            {
                return;
            }
            bool hasX = b.SpeedX != null;
            bool hasY = b.SpeedY != null;
            bool hasZ = b.SpeedZ != null;
            float vx = hasX ? b.SpeedX.Eval(_rand) : 0f;
            float vy = hasY ? b.SpeedY.Eval(_rand) : 0f;
            float vz = hasZ ? b.SpeedZ.Eval(_rand) : 0f;
            if (hasX && target.FacingLeft)
            {
                vx = -vx;      // X 轴速度随被作用对象的朝向取反(0x010b853a)
            }
            target.SetSpeed(hasX, vx, hasY, vy, hasZ, vz);
        }

        private void Direction(ActBehavior b, IActEntity target)
        {
            switch (b.Direction)
            {
                case ActDirectionMode.Left:
                    target.SetFacing(true);
                    break;
                case ActDirectionMode.Right:
                    target.SetFacing(false);
                    break;
                case ActDirectionMode.Random:
                    target.SetFacing(_host.Random(2) == 0);
                    break;
                case ActDirectionMode.Reverse:
                    target.SetFacing(!target.FacingLeft);
                    break;
                case ActDirectionMode.MyDirection:
                    target.SetFacing(_owner.FacingLeft);
                    break;
                case ActDirectionMode.MyOppositeDirection:
                    target.SetFacing(!_owner.FacingLeft);
                    break;
                case ActDirectionMode.ToTarget:
                {
                    IActEntity reference = target.Target ?? _owner.Target;
                    if (reference != null)
                    {
                        target.SetFacing(!(reference.X > target.X));     // 参照物在右边就朝右(0x010b9b5f)
                    }
                    break;
                }
            }
        }
    }
}
