using System;
using System.Collections.Generic;

// 证据:E-103(投掷的逐帧判断、出招开始时清标志、站立时每帧把剩余次数重置成 [attack number]、还剩 2 次以上就重新出招,取自客户端的"投掷"函数与状态机)。
namespace Dnf.Game
{
    /// <summary>一次发射里的一个投射物:第几条记录的第几个、与出招者是否反向。起点偏移在记录里(<see cref="ThrowAttackRecord.StartOffset"/>)。</summary>
    public readonly struct ThrowShot
    {
        public int Record { get; }
        public int Slot { get; }

        /// <summary>[direction] 的该项非 0:投射物朝与出招者相反的方向飞(起点偏移也按反向镜像)。</summary>
        public bool Reversed { get; }

        public ThrowShot(int record, int slot, bool reversed)
        {
            Record = record;
            Slot = slot;
            Reversed = reversed;
        }
    }

    /// <summary>一次逐帧判断的结果。<see cref="Shots"/>、<see cref="Sounds"/> 只在下一次调用 <see cref="ThrowAttackRunner.Tick"/> 之前有效。</summary>
    public readonly struct ThrowTick
    {
        public IReadOnlyList<ThrowShot> Shots { get; }

        /// <summary>这一帧该放的发射声音标签(每个触发的记录至多一个)。</summary>
        public IReadOnlyList<string> Sounds { get; }

        /// <summary>这一次出招还要再来一遍(记录的剩余次数在 2 以上):调用方应让出招从头再放一次。</summary>
        public bool RepeatAttack { get; }

        public ThrowTick(IReadOnlyList<ThrowShot> shots, IReadOnlyList<string> sounds, bool repeatAttack)
        {
            Shots = shots;
            Sounds = sounds;
            RepeatAttack = repeatAttack;
        }

        public bool Any => Shots.Count > 0 || Sounds.Count > 0 || RepeatAttack;
    }

    /// <summary>
    /// 一只怪物的投掷状态机(证据 E-103),与客户端的执行顺序一致:
    /// <list type="number">
    /// <item>进入出招状态时清掉全部"已发射"标志(<see cref="EnterAttack"/>);</item>
    /// <item>站立状态的每一帧把每条记录的剩余次数重置成 [attack number](<see cref="Idle"/>);</item>
    /// <item>出招状态的每一帧(<see cref="Tick"/>)逐条记录判断:属于这一招(<c>[attack index]</c> 与当前招相同)、还没发射、动画当前帧大于 [throw frame]、
    /// [passive object index] 不为 0、剩余次数不为 0 ——满足就一次放出全部投射物、记已发射、剩余次数大于 1 时要求重新出招、放发射声音、剩余次数减 1。</item>
    /// </list>
    /// 缺省的 [attack number] 是 -1:重置后剩余次数是 -1,不为 0,可以发射一次,发射后变 -2(不再重复)。
    /// </summary>
    public sealed class ThrowAttackRunner
    {
        private readonly ThrowAttackRecord[] _records;
        private readonly bool[] _thrown;
        private readonly int[] _remaining;
        private readonly List<ThrowShot> _shots = new List<ThrowShot>();
        private readonly List<string> _sounds = new List<string>();

        public ThrowAttackRunner(IReadOnlyList<ThrowAttackRecord> records)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }
            _records = new ThrowAttackRecord[records.Count];
            for (int i = 0; i < records.Count; i++)
            {
                _records[i] = records[i];
            }
            _thrown = new bool[_records.Length];
            _remaining = new int[_records.Length];
            Idle();
        }

        public int Count => _records.Length;

        public ThrowAttackRecord Record(int index) => _records[index];

        /// <summary>第 <paramref name="index"/> 条记录当前的剩余次数。</summary>
        public int Remaining(int index) => _remaining[index];

        /// <summary>第 <paramref name="index"/> 条记录这次出招里是否已经发射过。</summary>
        public bool Thrown(int index) => _thrown[index];

        /// <summary>进入出招状态(包括"重新出招"):清掉全部"已发射"标志。</summary>
        public void EnterAttack()
        {
            Array.Clear(_thrown, 0, _thrown.Length);
        }

        /// <summary>站立状态的一帧:每条记录的剩余次数回到 [attack number]。</summary>
        public void Idle()
        {
            for (int i = 0; i < _records.Length; i++)
            {
                _remaining[i] = _records[i].AttackNumber;
            }
        }

        /// <summary>
        /// 出招状态的一帧。<paramref name="attackIndex"/> 是正在放的这一招(<c>[attack motion]</c> 的下标),<paramref name="frame"/> 是出招动画的当前帧号。
        /// </summary>
        public ThrowTick Tick(int attackIndex, int frame)
        {
            _shots.Clear();
            _sounds.Clear();
            bool repeat = false;
            for (int i = 0; i < _records.Length; i++)
            {
                ThrowAttackRecord r = _records[i];
                if (r.AttackIndex != attackIndex || _thrown[i] || frame <= r.ThrowFrame || r.PassiveObjectIndex == 0 || _remaining[i] == 0)
                {
                    continue;
                }
                int shots = r.ShotCount;
                for (int k = 0; k < shots; k++)
                {
                    _shots.Add(new ThrowShot(i, k, r.IsReversed(k)));
                }
                _thrown[i] = true;
                if (_remaining[i] > 1)
                {
                    repeat = true;
                }
                if (r.ThrowSound != null)
                {
                    _sounds.Add(r.ThrowSound);
                }
                _remaining[i]--;
            }
            return new ThrowTick(_shots, _sounds, repeat);
        }
    }
}
