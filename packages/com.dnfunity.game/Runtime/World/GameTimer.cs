using System;

namespace Dnf.Game
{
    /// <summary>
    /// 脚本里的计时器对象:obj.getTimer() / obj.sq_timer_ / appendage.getTimer(),以及变量表里的计时器向量(push_ct_vector)。
    /// 方法:Reset()、Start(上限, 起点)、Restart(起点)、Get()(已计毫秒)、setParameter(间隔, 次数)(次数 -1 表示不限)、
    /// isOnEvent(当前时间)(按间隔周期性返回真,每个周期一次)、resetInstant(标志)。
    /// 语义是依据 Get() 与 isOnEvent(currentT) 在多段攻击脚本里的用法推断的,各方法的细节(起点、上限、立即触发)**未验证**。
    /// </summary>
    public sealed class GameTimer : NativeObject
    {
        private long _startedAt;
        private long _offset;
        private long _max;
        private bool _running;
        private long _interval;
        private int _remainingEvents;
        private long _nextEvent;

        public override string TypeName => "Timer";

        public GameTimer(GameWorld world)
        {
            World = world;
            Reset();
        }

        private long Now => World.TimeMs;

        /// <summary>周期事件的间隔(毫秒);没设置过为 0。</summary>
        public long Interval => _interval;

        /// <summary>计时是否已到上限(Start 的第一个参数;没设置上限为否)。</summary>
        public bool IsEnd => _max > 0 && Get() >= _max;

        public void Reset()
        {
            _startedAt = Now;
            _offset = 0;
            _running = true;
            _nextEvent = _interval;
        }

        public void Start(long max, long offset)
        {
            _startedAt = Now;
            _offset = offset;
            _max = max;
            _running = true;
            _nextEvent = _interval;
        }

        public void Restart(long offset)
        {
            Start(_max, offset);
        }

        public long Get()
        {
            if (!_running)
            {
                return _offset;
            }
            long t = Now - _startedAt + _offset;
            return _max > 0 && t > _max ? _max : t;
        }

        /// <summary>周期事件是否在起点(时间 0)就触发一次(setEventOnStart);默认否,与 setParameter 之后第一次事件在一个间隔之后一致(推断)。</summary>
        public bool EventOnStart { get; set; }

        public void SetParameter(long interval, int count)
        {
            _interval = Math.Max(1, interval);
            _remainingEvents = count;
            _nextEvent = EventOnStart ? 0 : _interval;
        }

        public void ResetInstant()
        {
            Reset();
            _nextEvent = 0;
        }

        public bool IsOnEvent(long currentTime)
        {
            if (_interval <= 0 || _remainingEvents == 0 || currentTime < _nextEvent)
            {
                return false;
            }
            while (_nextEvent <= currentTime)
            {
                _nextEvent += _interval;
            }
            if (_remainingEvents > 0)
            {
                _remainingEvents--;
            }
            return true;
        }
    }
}
