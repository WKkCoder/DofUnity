using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:触发器记录与行为记录的语法(记录头、尾部子标签、值表达式)取自客户端的加载函数 0x01333530 与单条条件解析 0x013322f0(E-050、E-130);
// 运行语义(三道门、目标集合、补帧、单周期一次)见 ActRunner。本文件只做"语法树 → 类型化的程序",不含运行期状态。
namespace Dnf.Game.Act
{
    /// <summary>比较符:脚本里写成 [=]、[&gt;]、[&lt;]、[&gt;=]、[&lt;=]、[!=](含 [=&gt;]、[=&lt;] 两种笔误写法),客户端的符号码 0 到 5。</summary>
    public enum ActCompare : byte
    {
        Equal = 0,
        Greater = 1,
        Less = 2,
        GreaterEqual = 3,
        LessEqual = 4,
        NotEqual = 5,
    }

    public static class ActCompares
    {
        /// <summary><c>left 比较符 right</c>(例如 [HP] [&gt;=] 50 是 hp &gt;= 50)。</summary>
        public static bool Test(int left, ActCompare op, int right)
        {
            switch (op)
            {
                case ActCompare.Equal: return left == right;
                case ActCompare.Greater: return left > right;
                case ActCompare.Less: return left < right;
                case ActCompare.GreaterEqual: return left >= right;
                case ActCompare.LessEqual: return left <= right;
                case ActCompare.NotEqual: return left != right;
                default: return false;
            }
        }

        public static bool TryParse(string tag, out ActCompare op)
        {
            switch (tag)
            {
                case "[=]":
                case "[==]":
                    op = ActCompare.Equal;
                    return true;
                case "[>]":
                    op = ActCompare.Greater;
                    return true;
                case "[<]":
                    op = ActCompare.Less;
                    return true;
                case "[>=]":
                case "[=>]":
                    op = ActCompare.GreaterEqual;
                    return true;
                case "[<=]":
                case "[=<]":
                    op = ActCompare.LessEqual;
                    return true;
                case "[!=]":
                case "[<>]":
                    op = ActCompare.NotEqual;
                    return true;
                default:
                    op = ActCompare.Equal;
                    return false;
            }
        }
    }

    /// <summary>
    /// 值表达式(客户端的 valueOf,E-054):字面整数,[RANDOM] a b(取 <c>rand % |b-a| + min(a,b)</c>,不含上界),
    /// [RANDOM SELECT] v1 … vn(等概率取一个)。
    /// </summary>
    public sealed class ActValue
    {
        private enum Mode : byte
        {
            Literal,
            Range,
            Choice,
        }

        private readonly Mode _mode;
        private readonly int[] _v;

        private ActValue(Mode mode, int[] v)
        {
            _mode = mode;
            _v = v;
        }

        public static ActValue Literal(int value) => new ActValue(Mode.Literal, new[] { value });

        public static ActValue Range(int a, int b) => new ActValue(Mode.Range, new[] { a, b });

        public static ActValue Choice(IEnumerable<int> values)
        {
            var list = new List<int>(values);
            return new ActValue(Mode.Choice, list.Count == 0 ? new[] { 0 } : list.ToArray());
        }

        /// <summary>是常数(不用随机数)。</summary>
        public bool IsConstant => _mode == Mode.Literal || (_mode == Mode.Range && _v[0] == _v[1]) || (_mode == Mode.Choice && _v.Length == 1);

        /// <summary>常数的值;不是常数时取第一个可能值。</summary>
        public int First => _v[0];

        /// <summary>求值;<paramref name="random"/>(n) 给 [0, n) 的随机整数。</summary>
        public int Eval(Func<int, int> random)
        {
            switch (_mode)
            {
                case Mode.Range:
                {
                    int lo = Math.Min(_v[0], _v[1]);
                    int span = Math.Abs(_v[1] - _v[0]);
                    return span == 0 ? lo : lo + random(span);
                }
                case Mode.Choice:
                    return _v.Length == 1 ? _v[0] : _v[random(_v.Length)];
                default:
                    return _v[0];
            }
        }

        /// <summary>
        /// 可能取到的全部值(字面值是它自己,[RANDOM SELECT] 是各个选项,[RANDOM] a b 是 [min, max) 里最多 64 个):预读数据时用,不求随机数。
        /// </summary>
        public IEnumerable<int> Candidates()
        {
            if (_mode == Mode.Range)
            {
                int lo = Math.Min(_v[0], _v[1]);
                int hi = Math.Max(_v[0], _v[1]);
                for (int x = lo; x < Math.Max(hi, lo + 1) && x < lo + 64; x++)
                {
                    yield return x;
                }
                yield break;
            }
            foreach (int x in _v)
            {
                yield return x;
            }
        }

        public override string ToString() => _mode + "(" + string.Join(",", _v) + ")";
    }

    /// <summary>表达式里的一项:整数、变量名(字符串值)或 [GET TIME](毫秒时钟)。</summary>
    public enum ActTermKind : byte
    {
        Number,
        Variable,
        GetTime,
    }

    /// <summary>表达式里的一项(<see cref="ActTermKind"/>)。</summary>
    public readonly struct ActTerm
    {
        private ActTerm(ActTermKind kind, int number, string name)
        {
            Kind = kind;
            Number = number;
            Name = name;
        }

        public ActTermKind Kind { get; }

        /// <summary>整数项的值。</summary>
        public int Number { get; }

        /// <summary>变量项的名字。</summary>
        public string Name { get; }

        public static ActTerm OfNumber(int number) => new ActTerm(ActTermKind.Number, number, null);

        public static ActTerm OfVariable(string name) => new ActTerm(ActTermKind.Variable, 0, name);

        public static ActTerm Clock => new ActTerm(ActTermKind.GetTime, 0, null);

        public override string ToString() => Kind == ActTermKind.Number ? Number.ToString() : Kind == ActTermKind.Variable ? "\"" + Name + "\"" : "[GET TIME]";
    }

    /// <summary>项之间的运算:没有(只有一项)、加、减。</summary>
    public enum ActArith : byte
    {
        None,
        Add,
        Subtract,
    }

    /// <summary>
    /// [IF] 与 [DIM] 里的小表达式(E-132;客户端求值函数 0x010ae520 的读法,全库只出现 <c>项</c>、<c>项 [+] 项</c>、<c>项 [-] 项</c> 三种):
    /// 项是整数、变量名或 [GET TIME];变量取同一个运行器的变量表(没赋过值读 0),[GET TIME] 取宿主的毫秒时钟。
    /// </summary>
    public sealed class ActExpression
    {
        public ActExpression(ActTerm first, ActArith op, ActTerm second)
        {
            First = first;
            Op = op;
            Second = second;
        }

        public ActTerm First { get; }

        public ActArith Op { get; }

        public ActTerm Second { get; }

        public override string ToString() => Op == ActArith.None ? First.ToString() : First + (Op == ActArith.Add ? " + " : " - ") + Second;
    }

    /// <summary>[WHICH] 之后与 [DO BEHAVIOR] 目标处用的选择器(<c>docs/act-tags.tsv</c> 的 selector 一节),加上"当前工作集"。</summary>
    public enum ActSelector : byte
    {
        Me,
        LastAttacker,
        LastActiveAttacker,
        LastAttackSuccess,
        CharacterAttackSuccess,
        LastAttackSuccesses,
        LastActiveAttackSuccess,
        LastAttackers,
        Target,
        SummonMaster,
        AllOurTeam,
        AllEnemy,
        AllCharacterTeam,
        AllMonsterTeam,
        Character,
        Monster,
        AiCharacter,
        CreateObject,
        DestroyObject,
        Passive,
        LoadTarget,
        PartyTarget,
        Player,
        SummonObject,

        /// <summary>[DO BEHAVIOR] [CHECKUP OBJECT]:对此刻筛过的目标集合执行。</summary>
        WorkingSet,
    }

    /// <summary>记录的种类。运行器认得的才有专门的种类,其余记成 <see cref="Unsupported"/>(包含它的触发器永远不会触发)。</summary>
    public enum ActRecordKind : byte
    {
        Unsupported,
        Frame,
        CheckTime,
        Which,
        CheckedNo,
        DoBehavior,
        DirectDoBehavior,
        OnSetAction,
        OnAttackSuccess,
        OnDamage,

        /// <summary>别的事件类([ON CREATE OBJECT] 等):这一类事件的运行器没有接,触发器在其余运行器里直接跳过(与客户端一致)。</summary>
        OnOtherEvent,
        ZPos,
        IsIndex,
        IsNotIndex,
        IsObjectType,
        Distance,
        IsLeft,
        IsRight,
        IsDirectionLeft,
        IsDirectionRight,
        RandomCheck,
        TriggerCheck,
        GetTarget,

        /// <summary>[IF] 左表达式 比较符 右表达式:整体真假,不改工作集(E-132)。</summary>
        If,

        /// <summary>[HP] 比较符 数 [%] 或 [POINT]:逐对象过滤(E-132)。</summary>
        Hp,

        /// <summary>[DELAY DO BEHAVIOR] 选择器 行为组号 延迟毫秒:把行为组排进延迟队列(E-132)。</summary>
        DelayDoBehavior,

        /// <summary>[FRONTSIDE]:逐对象过滤,留下"正面朝向拥有者"的(E-132)。</summary>
        FrontSide,

        /// <summary>[IS IN POS] x y z:逐对象过滤,X、Y、Z 三个整数坐标全等(E-132)。</summary>
        IsInPos,

        /// <summary>[IS CHILD] n:逐对象过滤,n 非 0 留下"创建者(召唤者)就是拥有者"的,n 为 0 留下不是的(E-134)。</summary>
        IsChild,

        /// <summary>
        /// [CHECK DISTANCE] x 范围 y 范围:整体真假,不改工作集(客户端条件码 0x3e,块在 0x010d44f4;E-156)。
        /// 拥有者要是生物类对象;场上有一个敌对的角色类对象,横向与纵深的差都在范围内才通过。
        /// </summary>
        CheckDistance,

        /// <summary>[IS GRABABLE] n:逐对象过滤(客户端条件码 0x2d;E-156)。n 为 1 留下"能被抓"的生物类对象,否则留下"不能被抓"的生物类对象;被动对象一律剔除。</summary>
        IsGrabbable,

        /// <summary>
        /// [SAVE TARGET OBJECT] [选择器] 槽号:把选择结果里的第一个对象存进拥有者的槽表(客户端记录码 0x3d,存的函数 0x010d1f40,E-157)。
        /// 选择器没写(或写了认不得的标签,含 [CHECKUP OBJECT])是工作集;结果为空不动槽。不是条件,不会让触发器不通过。
        /// </summary>
        SaveTarget,
    }

    /// <summary>
    /// [GET TARGET] 的排序键(客户端加载函数 0x0134b27c 起的块给每种键一个模式号:RANDOM 9、DISTANCE 5、LEVEL 6、HP 7、MP 8、LAST ATTACKSUCCESS 10;E-155)。
    /// MP 这一版没有对象接口,不认(全库没有用到)。
    /// </summary>
    public enum ActTargetKey : byte
    {
        None,
        Random,
        Distance,
        Hp,
        Level,
        LastAttackSuccess,
    }

    /// <summary>触发器里的一条记录(条件、事件或 [DO BEHAVIOR])。字段按种类取用。</summary>
    public sealed class ActRecord
    {
        public ActRecordKind Kind { get; internal set; }

        /// <summary>原标签(审计与调试用)。</summary>
        public string Tag { get; internal set; }

        /// <summary>[FRAME] 的下限;[CHECK TIME] 的毫秒数放在 <see cref="Number"/>。</summary>
        public int A { get; internal set; }

        /// <summary>[FRAME] 的上限。</summary>
        public int B { get; internal set; }

        public ActCompare Sign { get; internal set; }

        /// <summary>比较的数([ZPOS]、[DISTANCE]、[CHECKED NO]),或 [CHECK TIME] 的毫秒数,或 [RANDOM CHECK] 的分子。</summary>
        public int Number { get; internal set; }

        /// <summary>[RANDOM CHECK] 的分母。</summary>
        public int Denominator { get; internal set; }

        /// <summary>[WHICH] 的选择器或 [DO BEHAVIOR] 的目标。</summary>
        public ActSelector Selector { get; internal set; }

        /// <summary>[PARTY TARGET] 的队伍位置编号,或 [LOAD TARGET] 的槽号(都是"选择器的参数");没有是 -1。</summary>
        public int PartyIndex { get; internal set; } = -1;

        /// <summary>[DO BEHAVIOR] 的行为组编号(值表达式)。</summary>
        public ActValue Behavior { get; internal set; }

        /// <summary>[IS INDEX]、[IS NOT INDEX] 的编号表;[IS OBJECT TYPE] 的对象种类(<see cref="ActEntityKind"/> 的整数);[IS IN POS] 的 X、Y、Z;[CHECK DISTANCE] 的横向与纵深范围。</summary>
        public int[] Ints { get; internal set; } = Array.Empty<int>();

        /// <summary>[TRIGGER CHECK]:要求目标触发器开着([ON],true)还是关着([OFF],false)。触发器序号放在 <see cref="Number"/>。</summary>
        public bool Flag { get; internal set; }

        /// <summary>[TRIGGER CHECK] 看哪一族动作的触发器池(没写种类是 <see cref="ActTriggerPool.Own"/>:当前 .act 自己,整体真假;其余逐对象过滤,E-156)。</summary>
        public ActTriggerPool TriggerPool { get; internal set; }

        /// <summary>[TRIGGER CHECK] [ATTACK] 与 [CUSTOM] 的动作序号;别的写法是 -1。</summary>
        public int TriggerSlot { get; internal set; } = -1;

        /// <summary>[IF] 的左、右表达式。</summary>
        public ActExpression Left { get; internal set; }

        public ActExpression Right { get; internal set; }

        /// <summary>[HP]:阈值是最大生命的百分比([%])而不是绝对点数([POINT])。[DELAY DO BEHAVIOR] 的延迟毫秒放在 <see cref="Number"/>。</summary>
        public bool Percent { get; internal set; }

        /// <summary>[GET TARGET] n 键 [HIGH]/[LOW]:取的排序键;个数 n 放在 <see cref="Number"/>(E-155)。</summary>
        public ActTargetKey TargetKey { get; internal set; }

        /// <summary>[GET TARGET]:写了 [HIGH]("大的在前")为真;[LOW]、没写、[RANDOM] 为假。</summary>
        public bool Descending { get; internal set; }

        public override string ToString() => Kind + " " + Tag;
    }

    /// <summary>一个 [TRIGGER] 块:开关、次数上限与按文件顺序的记录(.act 里 [CHECKUP] 块的内容已内联进来,客户端的加载函数也是这样摊平的)。</summary>
    public sealed class ActTrigger
    {
        internal ActTrigger(int index, bool enabled, int limit, List<ActRecord> records, List<string> unsupportedTags)
        {
            Index = index;
            Enabled = enabled;
            Limit = limit;
            Records = records;
            UnsupportedTags = unsupportedTags;
            Supported = unsupportedTags.Count == 0;
            foreach (ActRecord r in records)
            {
                if (r.Kind == ActRecordKind.Unsupported)
                {
                    Supported = false;
                }
                if (r.Kind == ActRecordKind.Frame)
                {
                    HasFrame = true;
                }
            }
        }

        /// <summary>第几个顶层 [TRIGGER] 块(从 0 起;[TRIGGER] n [ON/OFF] 行为里的 n 就是它)。</summary>
        public int Index { get; }

        /// <summary>[ENABLE] 初值:没写算开。</summary>
        public bool Enabled { get; }

        /// <summary>[LIMIT]:最多触发几次;小于 1 不限。</summary>
        public int Limit { get; }

        public IReadOnlyList<ActRecord> Records { get; }

        /// <summary>记录里全是运行器认得的种类(否则这条触发器永远不触发)。</summary>
        public bool Supported { get; }

        public bool HasFrame { get; }

        /// <summary>读不懂的标签名(去重,审计用)。</summary>
        public IReadOnlyList<string> UnsupportedTags { get; }
    }

    /// <summary>行为的种类。</summary>
    public enum ActBehaviorKind : byte
    {
        Unsupported,
        CreatePassive,
        SetSpeed,
        SetAction,
        Destroy,
        AttackRect,
        SetDirection,
        TriggerSwitch,
        SetFrame,
        Teleport,

        /// <summary>
        /// [CHANGE ACTION FILE] 族 [序号] "路径":把怪物某一族动作(待机、行走、受击、倒地……)或某一招的 .act 换成另一个文件(E-151)。
        /// 客户端执行块在 0x010ca404(行为码 0xb5):族码 0 到 13,按路径新建一个动作对象换进怪物的动作表,旧的稍后销毁;
        /// 被换的动作此刻正在跑时它继续跑完,新动作从下一次进入这一族(或脚本再次 [SET ACTION] 到它)起生效。
        /// </summary>
        ChangeActionFile,

        /// <summary>[DIM] "名字" [=] 表达式:给运行器的变量赋值(E-132)。</summary>
        SetVariable,

        /// <summary>[SET DAMAGE BOX] [ON]/[OFF]/[RESET]:开关受击盒(E-133)。</summary>
        SetDamageBox,

        /// <summary>[CHANGE FLOATING HEIGHT] n:浮空高度,0 以下结束浮空(E-133)。</summary>
        SetFloatingHeight,

        /// <summary>[SHAKING] 强度 毫秒:屏幕震动,不分目标,由行为拥有者所在的场景执行(E-133)。</summary>
        Shake,

        /// <summary>[SUMMON MONSTER]:让每个目标召唤一只怪物,由房间场景造出来(E-134)。</summary>
        Summon,

        /// <summary>[SET HP] 值:把每个活着的目标的生命设成这个值,小于等于 0 就是杀死(E-134)。</summary>
        SetHp,

        /// <summary>
        /// [RESTORE] [HP] 数量 [%]:给每个活着的目标加(负数就是减)生命,带 [%] 时数量是目标最大生命的百分之几(客户端执行块 0x010b8c89;E-134)。
        /// [MP] 这一版没有对应的量,当空操作。
        /// </summary>
        Restore,

        /// <summary>
        /// [ACTIVE STATUS] [种类] 概率 等级 持续毫秒 附加整数…:对每个目标(主角与怪物这类"活动对象",被动对象不算)挂一个异常状态。
        /// 客户端执行块在 0x010bbec6(行为码 0x7b):按种类码分支把参数装成向量,调目标的"挂状态"虚槽 0x530 = 0x008184e0——
        /// 和受击处理对每个 .atk 状态槽调的是同一个函数(E-144),所以挂上的规则就是 E-136 那一套。
        /// </summary>
        ActiveStatus,

        /// <summary>
        /// [SET POS FORCE] 一串轴与旗标记号:把每个目标(主角与怪物这类"活动对象")登记进运行器的"强制位移表",之后每次更新由运行器按表摆位置(E-152)。
        /// 客户端执行块在 0x010bfa89(行为码 0x83):先清空这个动作上的位移表(0x010a7560),再按记号顺序读值装成 0x38 字节的记录、每个目标一条;
        /// 每帧的消费者在动作更新里(0x010a5042 起)。
        /// </summary>
        SetPosForce,

        /// <summary>
        /// [APPENDAGE] 持续毫秒 { [属性标签] [%]|[+]|[-] 数值 }…:给每个目标(主角与怪物这类"活动对象")的每一项属性改变各挂一个附加物(E-161)。
        /// 客户端执行块在 0x010b56c0 的行为码 0x84(加载块 0x013406f6,[NO BUFF APPENDAGE] 是 0xd4,同一块):同一项已有就先作废旧的再挂新的;
        /// 持续毫秒不大于 0 是没有时限。
        /// </summary>
        Appendage,

        /// <summary>
        /// [DELETE APPENDAGE] 一个数 { [属性标签] [%]|[+]|[-] 数值 }…:对工作集里每个目标,把"来源是写这条行为的对象、且与某一项相同"的属性改变附加物作废(E-161)。
        /// 客户端执行块在行为码 0xad(加载块 0x01341cea);开头那个数装载后不参与匹配(推断:全库里它多半与被删的附加物的持续毫秒一致)。
        /// </summary>
        DeleteAppendage,
    }

    /// <summary>
    /// [SET POS FORCE] 块里一个记号的码(与客户端装载函数 0x01340140 推进记号向量的码一致,E-152):0 到 5 是三个轴的起点与终点,后面带一个值表达式;
    /// 7 到 16 是旗标。码 6 在装载函数里没有对应的标签(执行块里把方向设成"取目标的",等于缺省),不出现。
    /// </summary>
    public enum ActForceCode : byte
    {
        XStart = 0,
        YStart = 1,
        ZStart = 2,
        XEnd = 3,
        YEnd = 4,
        ZEnd = 5,
        UseMyDirection = 7,
        NotUseObjectDirection = 8,
        UseTargetPos = 9,
        UseMyBasePos = 10,
        UseMapPos = 11,

        /// <summary>[MOVE UNIFORM] 与 [MOVE REDUCE] 在客户端走同一个线性插值函数(0x011b98c0,记录偏移 0x28 不等于 1 的都走它)。</summary>
        MoveUniform = 12,

        /// <summary>[MOVE ACCELATE] 走另一个插值函数(0x011b9ef0 → 0x011b9a50,没读),全库没有这个写法;含它的块这一版记成不支持。</summary>
        MoveAccelate = 13,

        MoveReduce = 14,
        MoveTarget = 15,
        MoveMe = 16,
    }

    /// <summary>[SET POS FORCE] 里按出现顺序排下来的一个记号;只有 0 到 5 号带值。</summary>
    public readonly struct ActForceToken
    {
        public ActForceToken(ActForceCode code, ActValue value)
        {
            Code = code;
            Value = value;
        }

        public ActForceCode Code { get; }

        public ActValue Value { get; }
    }

    /// <summary>
    /// [SET POS FORCE] 的基准点:默认取目标自己([USE TARGET POS]),[USE MY BASEPOS] 取行为拥有者,[USE MAP POS] 是地图绝对坐标(0,0,0)。
    /// 数值是客户端记录里的取值(记录偏移 0x2c)。
    /// </summary>
    public enum ActForceOrigin : byte
    {
        Target = 0,
        Owner = 1,
        Map = 2,
    }

    /// <summary>
    /// [SET POS FORCE] 的朝向来源(决定 X 偏移取正取负):默认取目标的朝向,[USE MY DIRECTION] 取拥有者的,[NOT USE OBJECT DIRECTION] 恒当朝右。
    /// 数值是客户端记录里的取值(记录偏移 0x30)。
    /// </summary>
    public enum ActForceDirection : byte
    {
        Target = 0,
        Owner = 1,
        Right = 2,
    }

    /// <summary>
    /// [SUMMON MONSTER] 的出场效果(客户端执行块里的"模式"整数,缺省 2):没写效果标签是魔法阵,[NO EFFECT] 直接出现,[JUMP OUT] 从召唤点跳出来(E-134)。
    /// 这一版只记下,三种都是原地直接出现。
    /// </summary>
    public enum ActSummonEffect : byte
    {
        None = 0,
        JumpOut = 1,
        MagicCircle = 2,
    }

    /// <summary>
    /// [CHANGE ACTION FILE] 换的是哪一族动作。数值是客户端执行块的族码(E-151,与 .mob 里各 action 标签在怪物动作表里的顺序一致:
    /// 待机 0、行走 1、后退 2、冲刺 3、冲刺攻击 4、坐下 5、受击 6 与 7(同一族名,靠序号 0/1 分)、倒地 8、翻倒 9、跳跃 10、跳击 11、出招列表 12、自定义列表 13);
    /// 代码里只按名字用。受击、出招、自定义三族带序号。
    /// </summary>
    public enum ActFileSlot : byte
    {
        Stay = 0,
        Move = 1,
        MoveBack = 2,
        Dash = 3,
        DashAttack = 4,
        Sit = 5,
        Damage = 6,
        Down = 8,
        Overturn = 9,
        Jump = 10,
        JumpAttack = 11,
        Attack = 12,
        Custom = 13,
    }

    /// <summary>[SET DAMAGE BOX] 的三档:开、关、什么都不做。没写档位的行写成关(客户端的"其它档位只关盒"分支)。</summary>
    public enum ActDamageBoxMode : byte
    {
        On,
        Off,
        Reset,
    }

    /// <summary>[SET ACTION] 的动作族。</summary>
    public enum ActActionKind : byte
    {
        Unknown,
        Stand,
        Move,
        Attack,
        Custom,
        Damage,
        Down,
        Sit,
        Default,
        Jump,
        Hold,
        Guard,
    }

    public enum ActAttackRectMode : byte
    {
        On,
        Off,
        Reset,
    }

    /// <summary>[SET DIRECTION] 的方向模式。</summary>
    public enum ActDirectionMode : byte
    {
        Left,
        Right,
        ToTarget,
        MyDirection,
        Random,
        Reverse,
        MyOppositeDirection,
    }

    public enum ActTriggerCommand : byte
    {
        On,
        Off,
        Reset,
    }

    /// <summary>
    /// [TRIGGER] 行为作用在哪一族动作的触发器池上(客户端加载分支 0x0133b043 起、执行块 0x010b861b,E-145)。
    /// 没写种类是当前这个 .act 自己(客户端种类码 3);写了种类就取行为拥有者的那一族动作:[ATTACK] n 与 [CUSTOM] n 先给动作序号再给触发器序号,
    /// 其余([MOVE]、[STAY]、[SIT]、[DOWN]、[DEFAULT]、[PROC])每族只有一个动作,直接给触发器序号。
    /// 客户端种类码:MOVE 4、STAY 8、SIT 0xa、ATTACK 0xb、DOWN 0xc、CUSTOM 0xd、DEFAULT 0xe、PROC 0xf;[DAMAGE](9)加载与执行两头的整数个数对不上,全库也没有这种写法,当读不懂。
    /// </summary>
    public enum ActTriggerPool : byte
    {
        Own,
        Move,
        Stay,
        Sit,
        Down,
        Default,
        Proc,
        Attack,
        Custom,
    }

    /// <summary>行为组 [BEHAVIOR] 里的一条行为。字段按种类取用。</summary>
    public sealed class ActBehavior
    {
        public ActBehaviorKind Kind { get; internal set; }

        public string Tag { get; internal set; }

        // ---- [CREATE PASSIVEOBJECT]
        public ActValue Index { get; internal set; }
        public ActValue Level { get; internal set; }
        public ActValue PosX { get; internal set; }
        public ActValue PosY { get; internal set; }
        public ActValue PosZ { get; internal set; }

        /// <summary>[USE MY DIRECTION]:朝向取行为拥有者的(没有就取被作用的对象的)。</summary>
        public bool UseMyDirection { get; internal set; }

        /// <summary>[USE MAP POS]:位置是地图上的绝对坐标(X 与 Y 不加基准)。</summary>
        public bool UseMapPos { get; internal set; }

        /// <summary>[USE MY BASEPOS]:基准点取行为拥有者而不是被作用的对象。</summary>
        public bool UseMyBasePos { get; internal set; }

        /// <summary>[USE OBJECT ZPOS]:Z 偏移加基准对象的高度(否则 Z 是绝对的)。</summary>
        public bool UseObjectZ { get; internal set; }

        /// <summary>[FIX DIRECTION]:固定朝向,0 左、1 右、4 中立、5 随机;没写是 -1。</summary>
        public int FixDirection { get; internal set; } = -1;

        // ---- [SET SPEED]
        public ActValue SpeedX { get; internal set; }
        public ActValue SpeedY { get; internal set; }
        public ActValue SpeedZ { get; internal set; }

        // ---- [SET ACTION]
        public ActActionKind Action { get; internal set; }

        /// <summary>[ATTACK] n、[CUSTOM] n 的序号(值表达式,可以是 [RANDOM SELECT])。</summary>
        public ActValue ActionIndex { get; internal set; }

        /// <summary>[NOW]:立即切换。</summary>
        public bool Now { get; internal set; }

        /// <summary>[END]:当前动作结束后再切换(推断)。</summary>
        public bool AtEnd { get; internal set; }

        // ---- [DESTROY] / [DESTROY BY FORCE]
        public bool Force { get; internal set; }

        // ---- [ATTACKRECT]
        public ActAttackRectMode Rect { get; internal set; }

        // ---- [SET DIRECTION]
        public ActDirectionMode Direction { get; internal set; }

        // ---- [TRIGGER] [种类 [动作序号]] n [ON]/[OFF]/[RESET]
        public int TriggerIndex { get; internal set; }
        public ActTriggerCommand Command { get; internal set; }

        /// <summary>开关落在哪一族动作上;没写种类是 <see cref="ActTriggerPool.Own"/>(当前 .act 自己,E-145)。</summary>
        public ActTriggerPool TriggerPool { get; internal set; }

        /// <summary>[ATTACK] n、[CUSTOM] n 的动作序号;其余种类是 -1。</summary>
        public int TriggerSlot { get; internal set; } = -1;

        // ---- [SET FRAME]
        public int Frame { get; internal set; }

        // ---- [DIM]
        public string VariableName { get; internal set; }

        public ActExpression Expression { get; internal set; }

        // ---- [SET DAMAGE BOX]
        public ActDamageBoxMode DamageBox { get; internal set; }

        // ---- [CHANGE FLOATING HEIGHT]
        public int Height { get; internal set; }

        // ---- [SHAKING]
        public int ShakeStrength { get; internal set; }

        public int ShakeMs { get; internal set; }

        // ---- [TELEPORT](位置三元组沿用 PosX/PosY/PosZ;[USE MY DIRECTION]、[USE MAP POS]、[USE MY BASEPOS] 沿用上面的三个标志)

        /// <summary>[NOT USE OBJECT DIRECTION]:X 偏移不随朝向取反;没有 [USE MY BASEPOS] 与 [USE MAP POS] 时位置整个是绝对的(客户端汇编的写法)。</summary>
        public bool NotUseDirection { get; internal set; }

        /// <summary>[NOT USE OBJECT ZPOS]:Z 偏移不加基准对象的高度(缺省是加)。</summary>
        public bool NotUseZ { get; internal set; }

        /// <summary>[MOVE ME]:被移动的是行为拥有者而不是被作用的对象;[MOVE TARGET] 把它取消(后写的算)。</summary>
        public bool MoveMe { get; internal set; }

        /// <summary>[ALLOW FIXTURE]:客户端走"场景物件"的另一条路,不直接摆位置;怪物与主角不是那类对象,这一版不移动。</summary>
        public bool AllowFixture { get; internal set; }

        /// <summary>[USE EFFECT]:传送特效(创建记录 0x24f9);这一版不放特效。</summary>
        public bool UseEffect { get; internal set; }

        // ---- [SUMMON MONSTER](Index、Level、PosX/Y/Z 与 UseMyDirection、UseMapPos、UseMyBasePos、UseObjectZ、NotUseDirection 沿用上面的字段)

        /// <summary>[USE TARGET TEAM]:召唤物的队伍取被作用对象的(缺省取拥有者的,没有拥有者是 200)。这一版没有队伍概念,只记下(E-134)。</summary>
        public bool UseTargetTeam { get; internal set; }

        /// <summary>[TEAM] n:指定队伍;没写是 -1。只记下。</summary>
        public int Team { get; internal set; } = -1;

        /// <summary>
        /// [SUMMON TIME] 毫秒:召唤物的存活时间,0(没写)是不限。语义是推断:客户端只是把它放进创建请求,请求由服务端处理,服务端那一半没读(E-134)。
        /// </summary>
        public int SummonTimeMs { get; internal set; }

        /// <summary>出场效果:没写效果标签是魔法阵。</summary>
        public ActSummonEffect SummonEffect { get; internal set; } = ActSummonEffect.MagicCircle;

        // ---- [SET HP]

        /// <summary>目标生命值(值表达式,每次执行取一次,对所有目标相同)。</summary>
        public ActValue Hp { get; internal set; }

        // ---- [RESTORE]

        /// <summary>数量(值表达式,每次执行取一次,可以是负数)。</summary>
        public ActValue RestoreAmount { get; internal set; }

        /// <summary>恢复的是魔法值([MP]):没有对应的量,什么都不做;false 是生命([HP])。</summary>
        public bool RestoreMp { get; internal set; }

        /// <summary>数量按百分比([%],客户端的符号码 7):目标最大生命 × 数量 / 100,向零截断。</summary>
        public bool RestorePercent { get; internal set; }

        // ---- [ACTIVE STATUS]

        /// <summary>
        /// 要挂的状态槽(种类、概率、状态等级、持续毫秒、附加整数),读法与 .atk 的 [active status] 槽一致(参数布局取自客户端执行块:
        /// 种类码 0 到 17 与 <see cref="ActiveStatusKind"/> 同序,概率是浮点、其后依次是等级、持续时间与按种类定的附加整数;E-144)。
        /// </summary>
        public ActiveStatusEntry Status { get; internal set; }

        /// <summary>这条 [ACTIVE STATUS] 比"概率、等级、持续时间加该种类的附加整数"多写的整数个数(审计用:应当总是 0,否则说明附加整数的个数与 .atk 的读法对不上)。</summary>
        public int StatusSpill { get; internal set; }

        /// <summary>这条 [ACTIVE STATUS] 比应有的少写的整数个数(缺的按 0 处理;审计用,应当总是 0,否则同样说明个数对不上)。</summary>
        public int StatusMissing { get; internal set; }

        // ---- [SET POS FORCE]

        /// <summary>块里的记号,按文件里出现的顺序(执行时也按这个顺序求值,值表达式里的随机数就按这个顺序抽,与客户端执行块一致)。</summary>
        public IReadOnlyList<ActForceToken> ForceTokens { get; internal set; }

        // ---- [CHANGE ACTION FILE]

        /// <summary>换哪一族动作。</summary>
        public ActFileSlot FileSlot { get; internal set; }

        /// <summary>受击(0、1)、出招、自定义三族的序号;没写序号是 0,其余族没有序号也是 0。</summary>
        public int FileIndex { get; internal set; }

        /// <summary>新 .act 的路径,原样(相对写这条行为的 .act 所在目录,E-151:全库 98% 的写法按这个规则能找到文件)。</summary>
        public string FilePath { get; internal set; }

        // ---- [APPENDAGE] / [DELETE APPENDAGE]

        /// <summary>块里的属性改变项,按文件里的顺序(E-161);没写任何项就是空表。</summary>
        public IReadOnlyList<StatChange> StatChanges { get; internal set; }

        /// <summary>[APPENDAGE] 的持续毫秒(不大于 0 没有时限);[DELETE APPENDAGE] 开头那个数也放这里,执行时不用。</summary>
        public int StatDurationMs { get; internal set; }

        public override string ToString() => Kind + " " + Tag;
    }

    /// <summary>一个 [BEHAVIOR] 块(行为组):[DO BEHAVIOR] 的编号 k 指它在文件里的顺序(从 0 起)。</summary>
    public sealed class ActBehaviorGroup
    {
        internal ActBehaviorGroup(List<ActBehavior> items, List<string> unsupportedTags)
        {
            Items = items;
            UnsupportedTags = unsupportedTags;
        }

        public IReadOnlyList<ActBehavior> Items { get; }

        /// <summary>读不懂、按空操作处理的行为标签(去重,审计用)。</summary>
        public IReadOnlyList<string> UnsupportedTags { get; }
    }

    /// <summary>一个 [MOTION] 块:主动画与叠加动画的路径(相对 .act 所在目录,原样)。</summary>
    public sealed class ActMotion
    {
        internal ActMotion(string baseAni, List<string> subAnis)
        {
            BaseAni = baseAni;
            SubAnis = subAnis;
        }

        public string BaseAni { get; }

        public IReadOnlyList<string> SubAnis { get; }
    }

    /// <summary>
    /// 一个 .act 编成的程序:动作(动画)、触发器与行为组。数据一律来自用户自备的 PVF,本类不含任何原版内容;
    /// 读不懂的记录按"触发器不触发、行为空操作"处理,并把标签名记下来供审计。
    /// </summary>
    public sealed class ActProgram
    {
        private ActProgram(string path, List<ActMotion> motions, List<ActTrigger> triggers, List<ActBehaviorGroup> behaviors)
        {
            Path = path;
            Motions = motions;
            Triggers = triggers;
            Behaviors = behaviors;
        }

        public string Path { get; }

        public IReadOnlyList<ActMotion> Motions { get; }

        public IReadOnlyList<ActTrigger> Triggers { get; }

        public IReadOnlyList<ActBehaviorGroup> Behaviors { get; }

        /// <summary>第一个 [MOTION] 的 [BASE ANI] 路径;没有是 null。</summary>
        public string BaseAniPath => Motions.Count > 0 ? Motions[0].BaseAni : null;

        public static readonly ActProgram Empty = new ActProgram(null, new List<ActMotion>(), new List<ActTrigger>(), new List<ActBehaviorGroup>());

        public static ActProgram Compile(string path, ActScript script)
        {
            if (script == null)
            {
                throw new ArgumentNullException(nameof(script));
            }
            var motions = new List<ActMotion>();
            var triggers = new List<ActTrigger>();
            var behaviors = new List<ActBehaviorGroup>();
            foreach (ActNode root in script.Roots)
            {
                if (root.Is("[MOTION]"))
                {
                    motions.Add(CompileMotion(root));
                }
                else if (root.Is("[TRIGGER]"))
                {
                    triggers.Add(ActCompiler.Trigger(triggers.Count, root));
                }
                else if (root.Is("[BEHAVIOR]"))
                {
                    behaviors.Add(ActCompiler.Behavior(root));
                }
            }
            return new ActProgram(path, motions, triggers, behaviors);
        }

        private static ActMotion CompileMotion(ActNode motion)
        {
            string baseAni = null;
            var subs = new List<string>();
            foreach (ActNode c in motion.Children)
            {
                if (c.Is("[BASE ANI]") && baseAni == null)
                {
                    baseAni = c.TextAt(0);
                }
                else if (c.Is("[SUB ANI]") || c.Is("[SUB ANI WITH XYZ]") || c.Is("[SUB ANI WITH XY]"))
                {
                    foreach (ScriptValue v in c.Values)
                    {
                        if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text))
                        {
                            subs.Add(v.Text);
                        }
                    }
                }
            }
            return new ActMotion(baseAni, subs);
        }
    }
}
