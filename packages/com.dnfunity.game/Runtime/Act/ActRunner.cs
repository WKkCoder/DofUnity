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
    public partial interface IActEntity
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
        /// [IS GRABABLE] 要的"此刻能不能被抓"(客户端对碰撞对象调虚槽 0x380;被动对象走碰撞对象基类的实现,恒返回假 0x00d26320,不看这个属性;
        /// 生物类的基类实现 0x0081ecb0 要求没有冰冻(异常状态 1)与石化(异常状态 7)等,
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

        /// <summary>非 null 时 <see cref="Team"/> 用此值而非种类默认([SET TEAM],E-221)。</summary>
        int? OverrideActTeam => null;

        /// <summary>
        /// 队伍号([IS TEAM] 要的,客户端对象的 +0x644 槽,虚槽 0x22c 读;E-166)。主角队是 0 到 11(单机的主角 0),怪物队是 100。
        /// 默认按种类给:主角 0、怪物 100、被动对象取创建者的队(没有创建者当怪物队,推断);对象自己有别的队(被 [SET TEAM] 改过等)时覆盖它。
        /// </summary>
        int Team
        {
            get
            {
                int? o = OverrideActTeam;
                if (o.HasValue)
                {
                    return o.Value;
                }
                return Kind == ActEntityKind.Character ? 0 : Kind == ActEntityKind.Monster ? 100 : Master != null ? Master.Team : 100;
            }
        }

        /// <summary>
        /// 当前速度的 X 分量(世界坐标/秒,[ZSPEED] 与 [IS END MAP] 要的;客户端取对象速度向量的第 0 轴,E-166)。
        /// 速度由脚本设([SET SPEED])时就是设的值;不知道速度的对象默认 0。
        /// </summary>
        float SpeedX => 0f;

        /// <summary>当前速度的 Y(纵深)分量,见 <see cref="SpeedX"/>。</summary>
        float SpeedY => 0f;

        /// <summary>当前速度的 Z(高度)分量,见 <see cref="SpeedX"/>。</summary>
        float SpeedZ => 0f;

        /// <summary>
        /// [IS TARGETACTIVESTATUS] 要的:身上此刻带着异常状态 <paramref name="status"/>(与 .atk 的 [active status] 同一套编号)吗(客户端问对象的虚槽 0x540;E-166)。
        /// 没有状态系统的对象(这一版的怪物、被动对象)默认 false;主角覆盖它,问自己的异常状态集合。
        /// </summary>
        bool HasActiveStatus(int status) => false;

        /// <summary>OperatingValue 状态号(getState 虚槽 +0xb8;E-172):0 站/走、3 受击、8 出招等;被动对象这一版恒 0。</summary>
        int ActAiState => 0;

        /// <summary>Character +0x36b0(0x10d92ae cast @019d9acc,E-172):param=-1 且该字段非 -1 时无条件保留该目标。</summary>
        int ActTargetStateFocus => -1;

        /// <summary>0x010d9203 cast @019d91e8:动作 +0x94 须为 CollisionObject,否则整元素剔除。</summary>
        bool ActTargetStateOwnerEligible => Kind != ActEntityKind.Passive;

        /// <summary>0x010d9230 cast @019d98fc:目标须为 ActiveObject(怪物/角色),被动对象剔除。</summary>
        bool ActTargetStateSubjectEligible => Kind != ActEntityKind.Passive;

        /// <summary>已废弃(E-172):客户端 0x107b2e0 不替 -1 换怪物 hint。</summary>
        int ActTargetStateHint => -1;

        /// <summary>[SET TEAM](0x010cb440,E-221):cast @019d91e8 后调 setTeam(虚槽 0x228)。</summary>
        void SetActTeam(int teamId)
        {
        }

        /// <summary>[IS VISIBLE](0x010d787f,E-222):vtbl+0x1d4 低 8 位与参数比较。</summary>
        int ActVisibilityByte => 0;

        /// <summary>[IS DAMAGEBOX ENABLE](0x010d7761,E-223):怪物 cast @019d991c 后 +0x36c 布尔与参数 0/1 比较。</summary>
        bool IsActDamageBoxEnabled => true;

        /// <summary>[IS CATEGORY] / ADD / REMOVE:是否带着脚本类别编号(客户端对象上的类别 bitset,与 <see cref="ActObjectCategories"/> 一致;E-171)。</summary>
        bool HasActCategory(int categoryId) => false;

        /// <summary>[ADD CATEGORY]:挂上类别;编号无效时不做(客户端查表返回 0x2c 时跳过)。</summary>
        void AddActCategory(int categoryId)
        {
        }

        /// <summary>[REMOVE CATEGORY]:去掉类别;没有时不做。</summary>
        void RemoveActCategory(int categoryId)
        {
        }

        /// <summary>
        /// [CHANGE DIRECTION BY TARGET] n(行为码 0xab,E-169):拥有者(怪物)上"朝向跟着目标走"的总开关。关着的时候怪物应用状态不采用 AI 给的朝向、
        /// 也不逐帧自动转向(脚本自己的 [SET DIRECTION] 照样有效)。不是怪物的对象(主角、被动对象)什么都不做,客户端对非怪物同样落空。
        /// </summary>
        void SetFacingFollowsTarget(bool on)
        {
        }

        /// <summary>
        /// [IS DIRECTION TO MOVE] n(行为码 0xc5,E-169):拥有者(怪物)出招状态里逐帧面朝 AI 目标的开关(客户端自动转向函数 0x00d1cb00 的出招分支看它)。
        /// 不是怪物的对象什么都不做。
        /// </summary>
        void SetAttackFacing(bool on)
        {
        }

        /// <summary>
        /// 换这个对象(行为拥有者,怪物)的 AI 目标(E-169)。<paramref name="designated"/> 为假是 [SET SAVED TARGET]:设缺省目标,AI 现有目标有效时不换(客户端软设置);
        /// 为真是 [CHANGE MY TARGET]:设指定目标(永久,优先于缺省目标)。之后 <see cref="Target"/> 返回新目标。没有 AI 的对象什么都不做。
        /// </summary>
        void SetAiTarget(IActEntity target, bool designated)
        {
        }
    }

    /// <summary>运行器对世界的请求:随机数、生成被动对象、解析选择器。</summary>
    public partial interface IActHost
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

        /// <summary>
        /// [CHANGE ATTACKINFO](E-167):把动作的拥有者 <paramref name="owner"/>(怪物或被动对象;不看目标集合)当前用的攻击信息换成 <paramref name="path"/> 这个 .atk;
        /// <paramref name="path"/> 是脚本里的原样写法,相对写这条行为的程序 <paramref name="fromProgram"/> 所在的目录,为 null 就是 [DEFAULT](恢复这个程序的默认攻击信息、没有就回到对象原本的)。
        /// 路径读不到由场景自己决定(客户端:找不到下标就什么都不做);默认什么都不做。
        /// </summary>
        void ChangeAttackInfo(IActEntity owner, string path, string fromProgram)
        {
        }

        /// <summary>
        /// [CASTING](E-168):<paramref name="owner"/> 排上了一条读条,<paramref name="milliseconds"/> 毫秒后执行第 <paramref name="group"/> 个行为组。只为统计(场景数一数),
        /// 读条本身由运行器自己计时与执行;默认什么都不做。
        /// </summary>
        void CastArmed(IActEntity owner, int milliseconds, int group)
        {
        }

        /// <summary>[CASTING](E-168):读条到期、行为组 <paramref name="group"/> 已执行(目标集合此刻还在场的)。只为统计;默认什么都不做。</summary>
        void CastFired(IActEntity owner, int group)
        {
        }

        /// <summary>
        /// [MOVE TO TARGET](E-169):<paramref name="mover"/> 按速度 <paramref name="speed"/> 朝 AI 目标冲了出去,计时 <paramref name="durationMs"/> 毫秒。只为统计;默认什么都不做。
        /// </summary>
        void MoveToTargetStarted(IActEntity mover, int speed, int durationMs)
        {
        }

        /// <summary>[MOVE TO TARGET](E-169):计时到了,<paramref name="mover"/> 的平面速度被清零(别的行为已经改过它的速度就不动)。只为统计;默认什么都不做。</summary>
        void MoveToTargetStopped(IActEntity mover)
        {
        }

        /// <summary>条件 0x10d92a2 主角对象(0x19d9acc);无则 null(E-172)。</summary>
        IActEntity ResolvePlayableCharacter(IActEntity owner) => null;

        /// <summary>
        /// 舞台的横向总宽(世界坐标,像素;[IS END MAP] 要的,客户端 0x0046ef10 取地图的格数乘格宽;E-166)。
        /// 不知道舞台(没有地图的试验场)是 0,这时 [IS END MAP] 不通过(客户端取不到地图时也不通过)。
        /// </summary>
        int MapWidth => 0;

        /// <summary>舞台的纵深总高(世界坐标,像素;[IS END MAP VERTICALLY] 要的,客户端 0x0062d080;E-166)。不知道是 0,见 <see cref="MapWidth"/>。</summary>
        int MapHeight => 0;

        /// <summary>[REALDUNGEON CHECKUP](E-173):表 0..2 按 key 取值;缺键返回 0(0x007ddb70 等)。</summary>
        int RealDungeonLookup(int table, int key) => 0;

        /// <summary>[IS OURTEAMATTACKED](E-173):对象管理器里是否存在满足 0x010d5dc1–0x010d5e0c 的受击对象(由 Play 层登记,测时用合成表)。</summary>
        bool MatchesOurTeamAttacked(IActEntity owner, IActEntity candidate) => false;
    }

    /// <summary>
    /// 一个对象在一个动作上跑触发器的运行器。每次更新调用 <see cref="Update"/> 一次(传当前动画帧与经过的秒数),
    /// 命中与受击等事件另行通知。语义(E-130):
    /// 触发器先过开关与次数闸;逐条记录求值,条件失败整条放弃,[WHICH] 重选工作集,过滤条件缩小工作集(空集合也算通过),
    /// [DO BEHAVIOR] 在目标集合非空(或前面有 [CHECKED NO] 通过)时执行行为组并记一次触发;
    /// [FRAME] 与事件触发器还受"单次位"约束:执行完一次后置位,直到动画回到第 0 帧才复位(行为里动画帧变了则不置位)。
    /// 读不懂的记录让整个触发器不触发。
    /// </summary>
    public sealed partial class ActRunner
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

        /// <summary>
        /// [CASTING] 的那一条读条(客户端:动作运行器上偏移 0x40c 起的三个字段,E-168):有没有排着、到期时刻(运行器自己的计时,毫秒;
        /// 客户端读拥有者的对象时钟,会随它停住,运行器只在拥有者走动时才被更新,等价)、到期要执行的行为组号、排它时的目标集合。同一时刻只有一条,后写的顶掉先写的。
        /// </summary>
        private bool _castArmed;
        private long _castDueMs;
        private int _castGroup;
        private readonly List<IActEntity> _castTargets = new List<IActEntity>();

        /// <summary>一个被 [MOVE TO TARGET] 推出去的对象:登记的是当时设下的速度,收尾时速度没被别的行为改过才清零。</summary>
        private sealed class MovedEntity
        {
            public IActEntity Entity;
            public float Vx;
            public float Vy;
        }

        /// <summary>
        /// [MOVE TO TARGET] 的收尾计时(客户端:运行器上偏移 0x494 的一个计时对象,每次执行先重置再按"距离 ÷ 速度"的毫秒数启动,所以只有一个,后来的顶掉先前的,E-169)。
        /// 有没有排着、到期时刻(运行器自己的计时,毫秒)、这一次推出去的对象。
        /// </summary>
        private bool _moveArmed;
        private long _moveDueMs;
        private readonly List<MovedEntity> _moved = new List<MovedEntity>();

        /// <summary>[SET POS FORCE] 的强制位移表(客户端:动作上偏移 0x43c 的记录队列,每次执行这条行为先清空;动作重来时也清空,E-152)。</summary>
        private readonly List<ForcedMove> _forced = new List<ForcedMove>();

        private float _time;
        private int _lastFrame = -1;
        private bool _loopReset;
        private int _frame;
        private bool _aborted;
        private IActEntity _victim;
        private IActEntity _attacker;

        /// <summary>正在处理的这一击的元素标志(位 0 火、1 水、2 暗、3 光);不在 [ON DAMAGE] 的唤醒里是 -1(没有"这一击",元素条件一律通过,E-166)。</summary>
        private int _damageElements = -1;

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
            _remote.Clear();       // 别的对象发来、还没执行的"执行第 n 组"消息同样作废(收件的动作实例已经不是原来那一个)
            _castArmed = false;    // 读条随动作结束作废(客户端:动作结束时重置 +0x40c 并让拥有者停读条,0x010ace2f,E-168)
            _castTargets.Clear();
            _moveArmed = false;    // 朝目标冲的计时随动作重来作废(与读条同一个理由,推断)
            _moved.Clear();
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
            RunRemote();
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
            RunMoveStop();
            RunCast();
        }

        /// <summary>主角被此对象的攻击命中(<paramref name="victim"/>);唤醒带 [ON ATTACKSUCCESS] 的触发器。</summary>
        public void OnAttackSuccess(IActEntity victim)
        {
            _victim = victim;
            Dispatch(ActRecordKind.OnAttackSuccess);
        }

        /// <summary>此对象受到了 <paramref name="attacker"/> 的攻击;唤醒带 [ON DAMAGE] 的触发器。不带这一击的元素,元素条件([IS FIRE DAMAGE] 等)一律通过。</summary>
        public void OnDamage(IActEntity attacker)
        {
            _attacker = attacker;
            Dispatch(ActRecordKind.OnDamage);
        }

        /// <summary>
        /// 同上,另外带上这一击的元素标志(<paramref name="elementMask"/>:位 0 火、1 水、2 暗、3 光,没有元素是 0),
        /// 唤醒期间元素条件([IS FIRE DAMAGE]、[IS WATER DAMAGE]、[IS DARK DAMAGE]、[IS LIGHT DAMAGE]、[IS NONE DAMAGE])按它判定(E-166)。
        /// </summary>
        public void OnDamage(IActEntity attacker, int elementMask)
        {
            _attacker = attacker;
            _damageElements = elementMask & 0xF;
            try
            {
                Dispatch(ActRecordKind.OnDamage);
            }
            finally
            {
                _damageElements = -1;
            }
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
                    case ActRecordKind.BackSide:
                    case ActRecordKind.FaceToFace:
                    case ActRecordKind.WithinSight:
                    case ActRecordKind.OutOfSight:
                    case ActRecordKind.FrontPos:
                    case ActRecordKind.IsTeam:
                    case ActRecordKind.ZSpeed:
                    case ActRecordKind.IsTargetActiveStatus:
                    case ActRecordKind.RandomCheck:
                    case ActRecordKind.IsInPos:
                    case ActRecordKind.IsChild:
                    case ActRecordKind.IsGrabbable:
                    case ActRecordKind.IsCategory:
                    case ActRecordKind.IsTargetState:
                    case ActRecordKind.IsNotTargetState:
                    case ActRecordKind.IsOurTeamAttacked:
                    case ActRecordKind.IsVisible:
                    case ActRecordKind.IsDamageBoxEnable:
                    case ActRecordKind.CheckSymbol:
                    case ActRecordKind.Area:
                        FilterSet(rec);
                        break;
                    case ActRecordKind.IsInArea:
                        if (!ApplyIsInArea(rec))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.IsDamageEnable:
                        if (!ApplyIsDamageEnable())
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.CheckPartySymbol:
                    case ActRecordKind.CheckPartySlot:
                    case ActRecordKind.CheckPlayerNum:
                    case ActRecordKind.CheckPartyMembersState:
                        if (!PartyConditionPasses(rec))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.RealDungeonCheckup:
                        if (!RealDungeonCheckupPasses(rec))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.IsEndMap:
                    case ActRecordKind.IsEndMapVertically:
                        if (!AtMapEnd(rec.Kind == ActRecordKind.IsEndMapVertically))
                        {
                            return;
                        }
                        break;
                    case ActRecordKind.ElementDamage:
                        if (!ElementDamagePasses(rec.Number))
                        {
                            return;
                        }
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
                    // 目标正面朝向拥有者(客户端 0x010d517d 起的块,只比 X 坐标与朝向,E-132):目标在拥有者左边或同位置就要朝右,在右边就要朝左;
                    // 客户端还要求目标的 +0x36c 槽返回 1(语义不明,按"活着"处理,推断);X 坐标按整数比(客户端取的是整数坐标,E-166 更正)
                    return e.IsAlive && (OwnerAtOrRightOf(e) ? !e.FacingLeft : e.FacingLeft);
                case ActRecordKind.BackSide:
                    // 目标背朝拥有者(0x010d5384 起,E-166):拥有者在目标右边或同位置,目标要朝左;在左边,目标要朝右
                    return e.IsAlive && (OwnerAtOrRightOf(e) ? e.FacingLeft : !e.FacingLeft);
                case ActRecordKind.FaceToFace:
                    // 面对面(0x010d59b8 起,E-166):拥有者在目标右边或同位置,拥有者朝左而目标朝右;在左边,拥有者朝右而目标朝左
                    return e.IsAlive && (OwnerAtOrRightOf(e) ? _owner.FacingLeft && !e.FacingLeft : !_owner.FacingLeft && e.FacingLeft);
                case ActRecordKind.WithinSight:
                    // 目标在拥有者视线前方(0x010d55a5 起,E-166):只看拥有者的朝向——目标在左边或同位置要朝左,在右边要朝右
                    return e.IsAlive && (OwnerAtOrRightOf(e) ? _owner.FacingLeft : !_owner.FacingLeft);
                case ActRecordKind.OutOfSight:
                    // 目标在拥有者视线后方(0x010d57b1 起,E-166):与 WithinSight 刚好相反
                    return e.IsAlive && (OwnerAtOrRightOf(e) ? !_owner.FacingLeft : _owner.FacingLeft);
                case ActRecordKind.FrontPos:
                {
                    // 0x010d4f0b 起的块(E-166):问拥有者的虚槽 0x110(朝右时目标 X 比自己小、朝左时比自己大算"是"),FRONTPOS 留"是",BACKPOS 留"否"
                    int ox = (int)_owner.X;
                    int tx = (int)e.X;
                    bool yes = _owner.FacingLeft ? tx > ox : tx < ox;
                    return yes != rec.Flag;
                }
                case ActRecordKind.IsTeam:
                    return TeamMatches(rec.Ints, e);
                case ActRecordKind.ZSpeed:
                    return ActCompares.TestFloat(e.SpeedZ, rec.Sign, rec.Number);
                case ActRecordKind.IsTargetActiveStatus:
                    return e.HasActiveStatus(rec.Number);
                case ActRecordKind.RandomCheck:
                {
                    // 0x010d68a2 起的块(E-166):每个对象各掷一次,[0, 分母) 的随机数不大于分子才留下;分母没写(或 0)按 100
                    int denominator = rec.Denominator == 0 ? 100 : Math.Abs(rec.Denominator);
                    return _rand(denominator) <= rec.Number;
                }
                case ActRecordKind.IsInPos:
                    return (int)e.X == rec.Ints[0] && (int)e.Y == rec.Ints[1] && (int)e.Z == rec.Ints[2];
                case ActRecordKind.IsChild:
                {
                    // 客户端 0x010d8bbb 起的块:目标的父对象(虚槽 0x1f4)与拥有者比较;参数非 0 留下相等的,0 留下不相等的(E-134)
                    bool child = ReferenceEquals(e.Master, _owner);
                    return rec.Number != 0 ? child : !child;
                }
                case ActRecordKind.IsGrabbable:
                    // 客户端 0x010d7bd0 起的块(E-156,E-166 更正):先把对象转成碰撞对象类(0x019d91e8,被动对象也转得过去),再取虚槽 0x380 的"能否被抓";
                    // 被动对象用的是碰撞对象基类的实现,恒返回假(0x00d26320)——参数为 1 留下能抓的,所以被动对象被剔除;别的值留下不能抓的,被动对象留下
                    return (e.Kind != ActEntityKind.Passive && e.IsGrabbable) == (rec.Number == 1);
                case ActRecordKind.TriggerCheck:
                {
                    // 非自身的写法逐对象过滤(客户端 0x010d85bb 起):取不到那一族动作的触发器池或序号不小于个数就剔除;
                    // 开关与要求一致([ON] 要开着、[OFF] 要关着)才留下
                    bool? on = e.GetActionTrigger(rec.TriggerPool, rec.TriggerSlot, rec.Number);
                    return on.HasValue && on.Value == rec.Flag;
                }
                case ActRecordKind.IsCategory:
                {
                    // 0x010d6d01 起(E-171):参数表里任一类别编号命中就留
                    if (rec.Ints == null || rec.Ints.Length == 0)
                    {
                        return false;
                    }
                    for (int i = 0; i < rec.Ints.Length; i++)
                    {
                        if (e.HasActCategory(rec.Ints[i]))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                case ActRecordKind.IsTargetState:
                case ActRecordKind.IsNotTargetState:
                    return KeepTargetState(rec, e);
                case ActRecordKind.IsOurTeamAttacked:
                    return KeepOurTeamAttacked(e);
                case ActRecordKind.IsVisible:
                    return (e.ActVisibilityByte & 0xFF) == rec.Number;
                case ActRecordKind.IsDamageBoxEnable:
                    if (e.Kind == ActEntityKind.Passive)
                    {
                        return false;
                    }
                    return e.IsActDamageBoxEnabled == (rec.Number != 0);
                case ActRecordKind.CheckSymbol:
                    return KeepSymbol(rec, e);
                case ActRecordKind.Area:
                    return KeepArea(rec, e);
                default:
                    return true;
            }
        }

        /// <summary>拥有者在目标右边或同位置(整数 X 坐标比,客户端条件块都先问这个,E-166)。</summary>
        private bool OwnerAtOrRightOf(IActEntity e) => (int)_owner.X >= (int)e.X;

        /// <summary>
        /// [IS TEAM] 的逐码判断(客户端 0x010d6b50 起的块,E-166):命中任何一项就留下。
        /// 0 与拥有者同队,1 与拥有者不同队,2 主角队(队号 0 到 11,函数 0x007dd440),3 怪物队(队号 100)。
        /// </summary>
        private bool TeamMatches(int[] codes, IActEntity e)
        {
            for (int i = 0; i < codes.Length; i++)
            {
                switch (codes[i])
                {
                    case 0:
                        if (e.Team == _owner.Team)
                        {
                            return true;
                        }
                        break;
                    case 1:
                        if (e.Team != _owner.Team)
                        {
                            return true;
                        }
                        break;
                    case 2:
                        if ((uint)e.Team <= 11u)
                        {
                            return true;
                        }
                        break;
                    case 3:
                        if (e.Team == 100)
                        {
                            return true;
                        }
                        break;
                }
            }
            return false;
        }

        /// <summary>
        /// [IS END MAP] / [IS END MAP VERTICALLY]:拥有者到了舞台的端头(客户端 0x010d75c0 与 0x010d769d 起的块,E-166)。
        /// 横向:X ≤ 150 且速度向左(或不动),或 X &gt; 宽 − 150 且速度向右(或不动);纵深:Y ≤ 200 且速度向后(或不动),或 Y &gt; 高 − 50 且速度向前(或不动)。
        /// 取不到舞台尺寸(<see cref="IActHost.MapWidth"/> 等不大于 0)不通过。
        /// </summary>
        private bool AtMapEnd(bool vertical)
        {
            int extent = vertical ? _host.MapHeight : _host.MapWidth;
            if (extent <= 0)
            {
                return false;
            }
            int position = (int)(vertical ? _owner.Y : _owner.X);
            float speed = vertical ? _owner.SpeedY : _owner.SpeedX;
            int near = vertical ? 200 : 150;
            int far = vertical ? 50 : 150;
            if (position <= near && speed <= 0f)
            {
                return true;
            }
            return position > extent - far && speed >= 0f;
        }

        /// <summary>
        /// 元素条件(客户端事件运行器 0x010a8c30,条件码 0x30 到 0x34,E-166)。没有"这一击"(不在 [ON DAMAGE] 的唤醒里)一律通过;
        /// 元素号 0 到 3 看对应的元素位,4([IS NONE DAMAGE])要这一击一个元素也没有。
        /// </summary>
        private bool ElementDamagePasses(int element)
        {
            if (_damageElements < 0)
            {
                return true;
            }
            return element == 4 ? _damageElements == 0 : (_damageElements & (1 << element)) != 0;
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
        /// 拥有者要转得成碰撞对象类(0x019d91e8;怪物与被动对象都转得过去,E-166 更正 E-156 里"被动对象转不过去"的误读);
        /// 它的虚槽 0x64(读字段 +0xb8,推断是场上对象表的指针——0x008791a0 就遍历它;各类共用一个实现 0x004062e0)要返回非零(按成立:对象在场上);
        /// 再调 0x008791a0(拥有者, x, y):场上角色类对象(类型码 0x11)里,有一个与拥有者敌对(0x00872c60)、不在 0xc8–0xcc 号状态(死亡一类,按"活着")、
        /// 横向差的绝对值不大于 x 且纵深差的绝对值不大于 y 的,就通过;位置按整数比较。判定函数的返回值在条件块里是真正被用的(之前读成"丢掉返回值"是反编译的错觉)。
        /// </summary>
        private bool CheckDistance(ActRecord rec)
        {
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

        /// <summary>
        /// 执行 [CASTING] 毫秒 n(客户端 0x010bd088 起的块,行为码 0x7e;E-168):先把读条的目标列表清空、再装进此刻的目标集合(客户端的顺序:参数不合格也已经换了列表),
        /// 然后毫秒小于 0 或行为组号小于 1 的什么都不排;合格的把到期时刻记成"此刻计时 + 毫秒"、行为组号记下,顶掉之前那一条。
        /// </summary>
        private void Cast(ActBehavior b, List<IActEntity> targets)
        {
            int ms = b.CastMs.Eval(_rand);
            int group = b.CastGroup.Eval(_rand);
            _castTargets.Clear();
            _castTargets.AddRange(targets);
            if (ms < 0 || group < 1)
            {
                return;
            }
            _castArmed = true;
            _castDueMs = ElapsedMs() + ms;
            _castGroup = group;
            _host.CastArmed(_owner, ms, group);
        }

        /// <summary>运行器自己的计时(毫秒;每次更新累加传进来的间隔)。</summary>
        private long ElapsedMs() => (long)Math.Round(_time * 1000.0);

        /// <summary>
        /// 每次更新的末尾:读条到期了就执行它的行为组(客户端 0x010ae2af 起,在整个更新函数的最后;E-168)。
        /// 先清读条再执行,行为组里再排一条 [CASTING](连着读两次条,全库 13 处)才不会被清掉;目标集合空了(排的时候就是空的)只清不执行;目标里已经不在场的不再作用。
        /// 客户端(推断:0x010d1370 把"执行第 n 组、目标列表"写进一个网络包,要有本机的控制权、列表非空才写)到点之后到真正执行之间隔一段消息往返,这里同一次更新里直接执行。
        /// </summary>
        private void RunCast()
        {
            if (!_castArmed || ElapsedMs() < _castDueMs)
            {
                return;
            }
            _castArmed = false;
            if (_castTargets.Count == 0)
            {
                return;
            }
            var targets = new List<IActEntity>(_castTargets);
            _castTargets.Clear();
            targets.RemoveAll(t => !t.IsAlive);
            int group = _castGroup;
            if (group >= _program.Behaviors.Count)
            {
                return;                // 组号越界(全库 13 处):找不到这一组,什么都不执行也不算"到期执行"(推断:接收一边按组号取不到)
            }
            _host.CastFired(_owner, group);
            RunGroup(group, targets);
        }

        /// <summary>
        /// 执行 [MOVE TO TARGET] n(客户端 0x010c744a 起的块,行为码 0xf6;E-169)。目标是拥有者 AI 此刻的目标(没有、已不在场就什么都不做);
        /// 对集合里每个在场的对象:角度取整数度(<see cref="ActGeometry.Heading"/> 的模式 1,纵深不还原),速度向量 = (cos 角 × n, sin 角 × n) 各向零截断
        /// (角度先乘 π/180 进双精度、正余弦落回单精度再乘 n;客户端的写法),Z 方向客户端写 0,这一版不动 Z(怪物在半空时不冻在空中);
        /// 计时毫秒 = 距离(模式 0,纵深还原)÷ n × 1000(单精度,向零截断),每个对象都重启一次,最后一个的毫秒数留下。
        /// n 不大于 0 的什么都不做(客户端对 0 会除出无穷、负数的含义没读;全库 22 处都是正数写法)。
        /// 速度向量不翻转纵深:Y 为正是往画面下方(与目标的纵深差同号),所以是朝目标去;但距离里纵深被还原过而速度没有,
        /// 目标与自己纵深有差时会冲过头(客户端的写法,照搬)。
        /// </summary>
        private void MoveToTarget(ActBehavior b, List<IActEntity> targets)
        {
            IActEntity aim = _owner.Target;
            if (aim == null || !aim.IsAlive || b.MoveSpeed <= 0)
            {
                return;
            }
            float speed = b.MoveSpeed;
            int aimX = (int)aim.X;
            int aimY = (int)aim.Y;
            bool first = true;
            foreach (IActEntity t in targets)
            {
                if (!t.IsAlive)
                {
                    continue;
                }
                if (first)
                {
                    _moved.Clear();        // 客户端只有一个计时:这一次真的推了对象才顶掉上一次的登记
                    first = false;
                }
                int fromX = (int)t.X;
                int fromY = (int)t.Y;
                int angle = ActGeometry.Heading(fromX, fromY, aimX, aimY, false);
                int distance = ActGeometry.Distance(fromX, fromY, aimX, aimY, true);
                ActGeometry.Velocity(angle, speed, out int stepX, out int stepY);
                float vx = stepX;
                float vy = stepY;
                t.SetSpeed(true, vx, true, vy, false, 0f);
                int ms = (int)((float)distance / speed * 1000f);
                _moveArmed = true;
                _moveDueMs = ElapsedMs() + ms;
                _moved.Add(new MovedEntity { Entity = t, Vx = vx, Vy = vy });
                _host.MoveToTargetStarted(t, b.MoveSpeed, ms);
            }
        }

        /// <summary>
        /// 每次更新里(读条到期之前):[MOVE TO TARGET] 的计时到了,把这次推出去、速度还停在当初设下的值上的对象的平面速度清零。
        /// 到点后谁来收尾读不到(客户端读计时的代码在保护区里;推断:冲刺到"距离 ÷ 速度"的时间就停,否则躲开的目标会让怪物一路冲出舞台);
        /// 速度已被别的行为改过的对象不碰,免得抹掉后来的位移。
        /// </summary>
        private void RunMoveStop()
        {
            if (!_moveArmed || ElapsedMs() < _moveDueMs)
            {
                return;
            }
            _moveArmed = false;
            foreach (MovedEntity m in _moved)
            {
                IActEntity t = m.Entity;
                if (t.IsAlive && t.SpeedX == m.Vx && t.SpeedY == m.Vy)
                {
                    t.SetSpeed(true, 0f, true, 0f, false, 0f);
                    _host.MoveToTargetStopped(t);
                }
            }
            _moved.Clear();
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
                    case ActBehaviorKind.ChangeAttackInfo:
                        // 客户端 0x010b775e 起的块(行为码 0x67):直接对行为拥有者(执行上下文里的拥有者字段)设攻击信息,不循环目标集合,所以 [DO BEHAVIOR] 的目标写什么都一样(E-167)
                        _host.ChangeAttackInfo(_owner, b.FilePath, _program.Path);
                        break;
                    case ActBehaviorKind.Casting:
                        // 客户端 0x010bd088 起的块(行为码 0x7e):记在拥有者的运行器上,不分目标地执行一次(E-168)
                        Cast(b, targets);
                        break;
                    case ActBehaviorKind.SetFacingByTarget:
                        // 客户端 0x010c6159 起的块:对行为拥有者(必须是怪物)写一个字节,不循环目标集合(E-169)
                        _owner.SetFacingFollowsTarget(b.FlagOn);
                        break;
                    case ActBehaviorKind.SetFacingInAttack:
                        // 客户端 0x010c61c2 起的块:同上,写另一个开关(E-169)
                        _owner.SetAttackFacing(b.FlagOn);
                        break;
                    case ActBehaviorKind.UseSavedTarget:
                    {
                        // 客户端 0x010c73c2 起的块:槽是空的就什么都不做;否则让拥有者的 AI 软设置缺省目标(E-169)
                        IActEntity saved = _owner.GetSavedTarget(b.TargetSlot);
                        if (saved != null)
                        {
                            _owner.SetAiTarget(saved, false);
                        }
                        break;
                    }
                    case ActBehaviorKind.ChangeMyTarget:
                        // 客户端 0x010be590 起的块:集合里每个对象依次设成拥有者 AI 的指定目标,最后一个胜出(E-169)
                        foreach (IActEntity t in targets)
                        {
                            _owner.SetAiTarget(t, true);
                        }
                        break;
                    case ActBehaviorKind.MoveToTarget:
                        MoveToTarget(b, targets);
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
                    case ActBehaviorKind.AddCategory:
                    case ActBehaviorKind.RemoveCategory:
                        if (ActObjectCategories.IsValidId(b.CategoryId))
                        {
                            bool add = b.Kind == ActBehaviorKind.AddCategory;
                            foreach (IActEntity t in targets)
                            {
                                if (t.IsAlive)
                                {
                                    if (add)
                                    {
                                        t.AddActCategory(b.CategoryId);
                                    }
                                    else
                                    {
                                        t.RemoveActCategory(b.CategoryId);
                                    }
                                }
                            }
                        }
                        break;
                    case ActBehaviorKind.SetTeam:
                        if (b.TeamValue != null)
                        {
                            int team = b.TeamValue.Eval(_rand);
                            foreach (IActEntity t in targets)
                            {
                                if (t.IsAlive && t.Kind != ActEntityKind.Passive)
                                {
                                    t.SetActTeam(team);
                                }
                            }
                        }
                        break;
                    case ActBehaviorKind.SendDoBehavior:
                    case ActBehaviorKind.SendMessage:
                    case ActBehaviorKind.ChangeAi:
                    case ActBehaviorKind.Camouflage:
                    case ActBehaviorKind.AppendHit:
                        ExecuteMessage(b, targets);
                        break;
                    case ActBehaviorKind.SetSymbol:
                    case ActBehaviorKind.SetSymbolOption:
                    case ActBehaviorKind.SetPartySymbol:
                    case ActBehaviorKind.AbsCasting:
                        ExecuteSymbolBehavior(b, targets);
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
