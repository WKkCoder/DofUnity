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

        void SetFacing(bool left);

        /// <summary>设速度(世界坐标/秒);X 的符号已经按朝向取过。</summary>
        void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz);

        /// <summary>切动作;<paramref name="index"/> 对 Attack/Custom 是序号;<paramref name="now"/> 立即切,<paramref name="atEnd"/> 等当前动作结束再切。</summary>
        void SetAction(ActActionKind kind, int index, bool now, bool atEnd);

        void Destroy(bool force);

        void AttackRect(ActAttackRectMode mode);

        void SetFrame(int frame);
    }

    /// <summary>运行器对世界的请求:随机数、生成被动对象、解析选择器。</summary>
    public interface IActHost
    {
        /// <summary>[0, n) 的随机整数(n &gt;= 1)。</summary>
        int Random(int n);

        /// <summary>生成一个被动对象。(x, y, z) 已经是世界坐标,<paramref name="facingLeft"/> 是它的朝向。</summary>
        void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft);

        /// <summary>
        /// 解析运行器自己不认识的选择器(队伍、全体敌人、某类对象等),把存活的结果追加到 <paramref name="into"/>。
        /// 运行器自己处理 ME、TARGET、SUMMON MASTER、最近的攻击者与被攻击者。
        /// </summary>
        void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into);
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
        /// <summary>"这条触发器不带事件记录"的标记(与 <see cref="ActRecordKind.Unsupported"/> 共用同一个值,事件数组里不会出现真正的 Unsupported)。</summary>
        private const ActRecordKind NoEvent = ActRecordKind.Unsupported;

        private readonly ActProgram _program;
        private readonly IActEntity _owner;
        private readonly IActHost _host;
        private readonly Func<int, int> _rand;
        private readonly bool[] _enabled;
        private readonly int[] _fired;
        private readonly bool[] _oneShot;
        private readonly float[] _clock;
        private readonly ActRecordKind[] _event;
        private readonly bool[] _inert;
        private readonly List<IActEntity> _set = new List<IActEntity>();
        private readonly List<IActEntity> _targets = new List<IActEntity>();
        private float _time;
        private int _lastFrame = -1;
        private bool _loopReset;
        private int _frame;
        private bool _aborted;
        private IActEntity _victim;
        private IActEntity _attacker;

        public ActRunner(ActProgram program, IActEntity owner, IActHost host)
        {
            _program = program ?? throw new ArgumentNullException(nameof(program));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _rand = host.Random;
            int n = program.Triggers.Count;
            _enabled = new bool[n];
            _fired = new int[n];
            _oneShot = new bool[n];
            _clock = new float[n];
            _event = new ActRecordKind[n];
            _inert = new bool[n];
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
                }
            }
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

        /// <summary>动作(重新)开始:开关回到脚本里写的初值,次数、单次位、计时清零。</summary>
        public void Begin()
        {
            for (int i = 0; i < _enabled.Length; i++)
            {
                _enabled[i] = _program.Triggers[i].Enabled;
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
        }

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
                default:
                    _host.Resolve(_owner, selector, partyIndex, into);
                    break;
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
                    default:
                        break;     // 没做的行为按空操作处理(ActBehaviorGroup.UnsupportedTags 供审计)
                }
                if (_aborted)
                {
                    return;
                }
            }
        }

        private void SwitchTrigger(ActBehavior b)
        {
            int n = b.TriggerIndex;
            if (n < 0 || n >= _enabled.Length)
            {
                return;
            }
            switch (b.Command)
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
