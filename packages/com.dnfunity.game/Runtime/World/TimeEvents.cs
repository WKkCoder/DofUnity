using System.Collections.Generic;

// 证据:最小世界里对象的计时器与计时事件的语义见 E-036、E-037;每个接口的核对状态见 docs/native-audit.tsv(E-046、E-048)。
namespace Dnf.Game
{
    /// <summary>一个计时事件:每隔 IntervalMs 触发一次,最多触发 Count 次(setTimeEvent(index, 间隔, 次数, 是否立刻触发))。</summary>
    public sealed class TimeEvent
    {
        public int Index;
        public int IntervalMs;
        public int RemainingCount;
        public int ElapsedMs;
        public int Fired;
    }

    /// <summary>
    /// 对象上的计时事件集合。按编号区分,同一编号再次设置会替换旧的。推进时按编号升序给出到期的事件(同一帧里一个事件可以到期多次)。
    /// 到期事件的"第几次触发"从 1 数起(推断:onTimeEvent 的第三个参数 timeEventCount;CrystalAttack 把 timeEventCount-1 当数组下标、
    /// 用 timeEventCount > 总数 判断结束,只有第一次为 1 才自洽,**未验证**)。
    /// </summary>
    public sealed class TimeEventSet
    {
        private readonly SortedDictionary<int, TimeEvent> _events = new SortedDictionary<int, TimeEvent>();

        public int Count => _events.Count;

        public bool Exists(int index)
        {
            return _events.ContainsKey(index);
        }

        public void Set(int index, int intervalMs, int count, bool fireImmediately)
        {
            var e = new TimeEvent
            {
                Index = index,
                IntervalMs = intervalMs < 1 ? 1 : intervalMs,
                // 次数 0(或负数)按"不限次数"处理(推断):全库只有 5 个脚本用 0,个个都在回调里 return true 或 stopTimeEvent 自己收尾,
                // 其中冰轮和暗黑权能的多段命中计时器只有"一直重复"才说得通;**未验证**
                RemainingCount = count <= 0 ? int.MaxValue : count,
                // 立刻触发:把已经过的时间设成一个间隔,下次推进时马上到期
                ElapsedMs = fireImmediately ? (intervalMs < 1 ? 1 : intervalMs) : 0,
            };
            _events[index] = e;
        }

        public void Remove(int index)
        {
            _events.Remove(index);
        }

        public void Clear()
        {
            _events.Clear();
        }

        /// <summary>
        /// 推进 dtMs 毫秒,对每次到期调用 fire(事件编号, 第几次触发,从 1 起);fire 返回 true 表示脚本要求中止这个事件(从集合里移除)。
        /// fire 里可以设置或移除事件。
        /// </summary>
        public void Advance(int dtMs, System.Func<int, int, bool> fire)
        {
            if (_events.Count == 0)
            {
                return;
            }
            var due = new List<int>(_events.Keys);
            foreach (int index in due)
            {
                int guard = 0;
                // 先把这一段时间加给该事件;fire 可能把它替换成新的(新的从 0 开始计时),所以每次到期后重新取
                if (!_events.TryGetValue(index, out TimeEvent e))
                {
                    continue;
                }
                e.ElapsedMs += dtMs;
                while (guard++ < 10000 && _events.TryGetValue(index, out TimeEvent cur) && ReferenceEquals(cur, e) && e.ElapsedMs >= e.IntervalMs && e.RemainingCount > 0)
                {
                    e.ElapsedMs -= e.IntervalMs;
                    e.RemainingCount--;
                    int n = ++e.Fired;
                    bool stop = fire(index, n);
                    if (stop)
                    {
                        if (_events.TryGetValue(index, out TimeEvent now) && ReferenceEquals(now, e))
                        {
                            _events.Remove(index);
                        }
                        break;
                    }
                }
                if (_events.TryGetValue(index, out TimeEvent last) && ReferenceEquals(last, e) && e.RemainingCount <= 0)
                {
                    _events.Remove(index);
                }
            }
        }
    }
}
