using System;
using System.Collections.Generic;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>出招决策里"目标"的几何信息:整数位置、碰撞宽度与进深、是否倒在地上。</summary>
    public readonly struct AttackTarget
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Depth { get; }
        public bool IsDown { get; }

        public AttackTarget(int x, int y, int width, int depth, bool isDown = false)
        {
            X = x;
            Y = y;
            Width = width;
            Depth = depth;
            IsDown = isDown;
        }
    }

    /// <summary>
    /// 怪物"这一刻出哪一招"的决策(证据 E-100、E-101,取自客户端怪物基类的出招决策一族函数;规则与常数见 <see cref="MonsterAttackRules"/>)。
    /// <para>
    /// 节奏:怪物站着(待机)时才决策;出生满 <see cref="MonsterAttackRules.SpawnLockoutMs"/> 之后,到点就抽一次签,
    /// 下一次定在 [attack delay] 加一个 ±10% 的随机抖动之后(不管这次有没有出招)。
    /// 抽签:在"可用"的记录里,逐条判断"这一招现在能不能用"——目标在这一招的射程里并通过 <see cref="AttackKindRecord.ChanceInRange"/> 的掷骰,
    /// 或者(没有目标通过时)通过 <see cref="AttackKindRecord.ChanceAnywhere"/> 的不论远近的掷骰;能用的招里随机挑一个。
    /// 进入敌对状态时每条记录的射程里概率乘 <see cref="MonsterAttackRules.HostileChanceScale"/>。
    /// </para>
    /// <para>
    /// 推断(没有在客户端里找到依据,模型里单独标出):<b>没有射程范围的记录</b>(全 0,有的基础攻击这样写;客户端的射程判断对它回答"不在")
    /// 靠什么触发还没读通,这里交给调用方给的几何判断(<c>zeroExtentReach</c>:这一招的攻击框够得着目标),够得着就算能用、不抽签。
    /// </para>
    /// </summary>
    public sealed class MonsterAttackChooser
    {
        private readonly AttackKindRecord[] _records;
        private readonly float[] _baseChance;
        private readonly bool[] _ready;
        private readonly bool[] _enabled;
        private readonly Func<int> _random;
        private readonly int _delayMs;
        private readonly List<int> _picks = new List<int>();
        private long _nextDecisionMs;

        /// <param name="records">[attack kind] 的各条记录(第 i 条对应第 i 个 [attack motion])。</param>
        /// <param name="attackDelayMs">[attack delay](毫秒)。</param>
        /// <param name="random">客户端 rand() 的替身:返回非负整数(见 <see cref="Dnf.Render.ClientRandom"/>)。</param>
        public MonsterAttackChooser(IReadOnlyList<AttackKindRecord> records, int attackDelayMs, Func<int> random)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _delayMs = attackDelayMs;
            _records = new AttackKindRecord[records.Count];
            _baseChance = new float[records.Count];
            _ready = new bool[records.Count];
            _enabled = new bool[records.Count];
            for (int i = 0; i < records.Count; i++)
            {
                _records[i] = records[i];
                _baseChance[i] = records[i].ChanceInRange;
                _ready[i] = records[i].InitiallyReady;
                _enabled[i] = true;
            }
        }

        public int Count => _records.Length;

        /// <summary>已经进入敌对状态(概率已乘过一次)。</summary>
        public bool Hostile { get; private set; }

        /// <summary>下一次决策的时刻(怪物自己的时钟,毫秒);出生后 0。</summary>
        public long NextDecisionMs => _nextDecisionMs;

        /// <summary>第 <paramref name="index"/> 条记录(已经含敌对状态的放大)。</summary>
        public AttackKindRecord Record(int index)
        {
            return _records[index];
        }

        public bool IsReady(int index)
        {
            return _ready[index];
        }

        public bool IsEnabled(int index)
        {
            return _enabled[index];
        }

        /// <summary>改"可用"标志(原版有按 [cooltime] 恢复的机制,还没有接进来)。</summary>
        public void SetReady(int index, bool ready)
        {
            _ready[index] = ready;
        }

        /// <summary>这一招没有动画(或不想让它被选中):永远不参与决策。</summary>
        public void Disable(int index)
        {
            _enabled[index] = false;
        }

        /// <summary>
        /// 进入敌对状态(客户端进入"主动出击"的模式 9、10 时,把每条记录的射程里概率乘 <see cref="MonsterAttackRules.HostileChanceScale"/>,0x0088a6e0)。
        /// 只放大一次(已经在敌对状态里再调用不叠加);离开时用 <see cref="LeaveHostile"/> 还原(客户端的模式退出函数 0x00894fb0 乘约 1/1.3,M22 读到)。
        /// </summary>
        public void EnterHostile()
        {
            if (Hostile)
            {
                return;
            }
            Hostile = true;
            for (int i = 0; i < _records.Length; i++)
            {
                _records[i] = _records[i].WithChanceInRange(_records[i].ChanceInRange * MonsterAttackRules.HostileChanceScale);
            }
        }

        /// <summary>
        /// 离开敌对状态:把每条记录的射程里概率还原成进入之前的值(客户端乘 0.7692,与 1.3 的积是 0.99996,这里直接还原)。没在敌对状态里什么也不做。
        /// </summary>
        public void LeaveHostile()
        {
            if (!Hostile)
            {
                return;
            }
            Hostile = false;
            for (int i = 0; i < _records.Length; i++)
            {
                _records[i] = _records[i].WithChanceInRange(_baseChance[i]);
            }
        }

        /// <summary>
        /// 有没有哪一条可用的记录此刻"够得着"目标(移动逻辑用:够得着就停下、不再靠近)。不抽签、不耗随机数。
        /// </summary>
        public bool AnyInRange(int selfX, int selfY, bool facingLeft, in AttackTarget target, Func<int, bool> zeroExtentReach)
        {
            for (int i = 0; i < _records.Length; i++)
            {
                if (_enabled[i] && _ready[i] && Reaches(i, selfX, selfY, facingLeft, target, zeroExtentReach))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 做一次出招决策。返回要出的招(记录下标),不出招返回 −1。
        /// <paramref name="aiClockMs"/> 是怪物出生以来的时间;<paramref name="hasTarget"/> 为假(没有可打的目标)时只剩"不论远近"的那一次掷骰。
        /// 调用方负责只在怪物站着(待机)时调用。
        /// </summary>
        public int Decide(long aiClockMs, int selfX, int selfY, bool facingLeft, bool hasTarget, in AttackTarget target, Func<int, bool> zeroExtentReach)
        {
            if (_records.Length == 0 || aiClockMs < MonsterAttackRules.SpawnLockoutMs || aiClockMs < _nextDecisionMs)
            {
                return -1;
            }
            _nextDecisionMs = MonsterAttackRules.NextDecisionMs(aiClockMs, _delayMs, _random());
            _picks.Clear();
            for (int i = 0; i < _records.Length; i++)
            {
                if (_enabled[i] && _ready[i] && CanUse(i, selfX, selfY, facingLeft, hasTarget, target, zeroExtentReach))
                {
                    _picks.Add(i);
                }
            }
            if (_picks.Count == 0)
            {
                return -1;
            }
            return _picks[_random() % _picks.Count];
        }

        private bool Reaches(int i, int selfX, int selfY, bool facingLeft, in AttackTarget target, Func<int, bool> zeroExtentReach)
        {
            AttackKindRecord r = _records[i];
            if (r.HasExtents)
            {
                return MonsterAttackRules.IsInRange(r, selfX, selfY, facingLeft, target.X, target.Y, target.Width, target.Depth);
            }
            return zeroExtentReach != null && zeroExtentReach(i);
        }

        /// <summary>"这一招现在能不能用"(客户端同名函数的结构):先看目标在不在射程里(再过倒地门槛与概率掷骰),都不行再掷不论远近的那一次。</summary>
        private bool CanUse(int i, int selfX, int selfY, bool facingLeft, bool hasTarget, in AttackTarget target, Func<int, bool> zeroExtentReach)
        {
            AttackKindRecord r = _records[i];
            if (hasTarget && Reaches(i, selfX, selfY, facingLeft, target, zeroExtentReach))
            {
                bool pass = true;
                if (target.IsDown)
                {
                    pass = MonsterAttackRules.Roll(_random(), r.ChanceVsDown);
                }
                if (pass)
                {
                    if (!r.HasExtents)
                    {
                        return true;
                    }
                    if (MonsterAttackRules.Roll(_random(), r.ChanceInRange))
                    {
                        return true;
                    }
                }
            }
            return MonsterAttackRules.Roll(_random(), r.ChanceAnywhere);
        }
    }
}
