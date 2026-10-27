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

        /// <summary>[PARTY TARGET] 的队伍位置编号;没有是 -1。</summary>
        public int PartyIndex { get; internal set; } = -1;

        /// <summary>[DO BEHAVIOR] 的行为组编号(值表达式)。</summary>
        public ActValue Behavior { get; internal set; }

        /// <summary>[IS INDEX]、[IS NOT INDEX] 的编号表;[IS OBJECT TYPE] 的对象种类(<see cref="ActEntityKind"/> 的整数);[TRIGGER CHECK] 的触发器编号;[IS IN POS] 的 X、Y、Z。</summary>
        public int[] Ints { get; internal set; } = Array.Empty<int>();

        /// <summary>[TRIGGER CHECK]:要求目标触发器开着(true)还是关着(false)。</summary>
        public bool Flag { get; internal set; }

        /// <summary>[IF] 的左、右表达式。</summary>
        public ActExpression Left { get; internal set; }

        public ActExpression Right { get; internal set; }

        /// <summary>[HP]:阈值是最大生命的百分比([%])而不是绝对点数([POINT])。[DELAY DO BEHAVIOR] 的延迟毫秒放在 <see cref="Number"/>。</summary>
        public bool Percent { get; internal set; }

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
        ChangeActionFile,

        /// <summary>[DIM] "名字" [=] 表达式:给运行器的变量赋值(E-132)。</summary>
        SetVariable,

        /// <summary>[SET DAMAGE BOX] [ON]/[OFF]/[RESET]:开关受击盒(E-133)。</summary>
        SetDamageBox,

        /// <summary>[CHANGE FLOATING HEIGHT] n:浮空高度,0 以下结束浮空(E-133)。</summary>
        SetFloatingHeight,

        /// <summary>[SHAKING] 强度 毫秒:屏幕震动,不分目标,由行为拥有者所在的场景执行(E-133)。</summary>
        Shake,
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

        // ---- [TRIGGER] n [ON]/[OFF]/[RESET]
        public int TriggerIndex { get; internal set; }
        public ActTriggerCommand Command { get; internal set; }

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
