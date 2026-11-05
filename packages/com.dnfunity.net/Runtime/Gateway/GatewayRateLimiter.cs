using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Dnf.Net.Gateway
{
    /// <summary>单调时钟(返回自某个任意起点以来的时间)。限流只需要时间差,不用墙上时钟,免得用户改系统时间或校时把限流器卡死。</summary>
    public interface IGatewayClock
    {
        /// <summary>当前时间;必须单调不减。</summary>
        TimeSpan Now { get; }
    }

    /// <summary>基于 <see cref="Stopwatch"/> 的默认时钟。</summary>
    public sealed class StopwatchGatewayClock : IGatewayClock
    {
        private static readonly long Origin = Stopwatch.GetTimestamp();

        /// <summary>共享实例。</summary>
        public static readonly StopwatchGatewayClock Default = new StopwatchGatewayClock();

        /// <inheritdoc />
        public TimeSpan Now
        {
            get
            {
                long elapsed = Stopwatch.GetTimestamp() - Origin;
                long frequency = Stopwatch.Frequency;
                // 先整秒再余数,避免 elapsed * TicksPerSecond 溢出
                long ticks = (elapsed / frequency) * TimeSpan.TicksPerSecond + (elapsed % frequency) * TimeSpan.TicksPerSecond / frequency;
                return TimeSpan.FromTicks(ticks);
            }
        }
    }

    /// <summary>
    /// 客户端自我限流,口径与服务端一致:按窗口(默认 60 秒)最多放行若干次(默认 10 次)认证请求,成功失败都算一次。
    /// 用滑动窗口而不是固定窗口:服务端若是固定窗口,滑动窗口只会更保守;服务端若是滑动窗口,固定窗口会在窗口交界处连发 20 次而被拒。
    /// 一次请求在"放行后经过满一个窗口"的那一刻过期(<c>now - 放行时刻 >= 窗口</c>);窗口边界上服务端是否同刻放行未核实,多等一个时钟刻度无害。
    /// 被拒绝的尝试不入账(不会因为用户反复点击而把冷却期越推越长)。线程安全。
    /// </summary>
    public sealed class GatewayRateLimiter
    {
        private readonly object _lock = new object();
        private readonly Queue<TimeSpan> _stamps = new Queue<TimeSpan>();
        private readonly int _maxRequests;
        private readonly TimeSpan _window;
        private TimeSpan _blockedUntil = TimeSpan.MinValue;

        /// <summary>与服务端同口径:60 秒窗口、最多 10 次。</summary>
        public GatewayRateLimiter()
            : this(GatewayProtocol.RateLimitMaxRequests, GatewayProtocol.RateLimitWindow)
        {
        }

        /// <summary>自定义口径(测试或服务端口径变化时用)。</summary>
        public GatewayRateLimiter(int maxRequests, TimeSpan window)
        {
            if (maxRequests < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRequests));
            }
            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }
            _maxRequests = maxRequests;
            _window = window;
        }

        /// <summary>窗口内最多放行的次数。</summary>
        public int MaxRequests
        {
            get { return _maxRequests; }
        }

        /// <summary>窗口长度。</summary>
        public TimeSpan Window
        {
            get { return _window; }
        }

        /// <summary>尝试占用一个名额:放行则记账并返回 true;窗口已满或处于冷却期返回 false(不记账)。<paramref name="now"/> 必须单调不减。</summary>
        public bool TryAcquire(TimeSpan now)
        {
            lock (_lock)
            {
                Expire(now);
                if (now < _blockedUntil || _stamps.Count >= _maxRequests)
                {
                    return false;
                }
                _stamps.Enqueue(now);
                return true;
            }
        }

        /// <summary>现在再申请,还要等多久才会被放行;可以立即放行时为 <see cref="TimeSpan.Zero"/>。不改变状态。</summary>
        public TimeSpan RetryAfter(TimeSpan now)
        {
            lock (_lock)
            {
                Expire(now);
                TimeSpan wait = TimeSpan.Zero;
                if (now < _blockedUntil)
                {
                    wait = _blockedUntil - now;
                }
                if (_stamps.Count >= _maxRequests)
                {
                    // 队首是最老的一次;它过期后名额才空出来
                    TimeSpan untilFree = _stamps.Peek() + _window - now;
                    if (untilFree > wait)
                    {
                        wait = untilFree;
                    }
                }
                return wait;
            }
        }

        /// <summary>窗口内已占用的名额数。</summary>
        public int Used(TimeSpan now)
        {
            lock (_lock)
            {
                Expire(now);
                return _stamps.Count;
            }
        }

        /// <summary>
        /// 强制冷却:从 <paramref name="now"/> 起 <paramref name="duration"/> 内一律不放行(取与现有冷却期中较晚的结束时刻)。
        /// 服务端回 429 说明它那边的计数比我们本地的多(同一出口 IP 上还有别的客户端,或客户端重启过),此后立刻重试只会继续被拒。
        /// </summary>
        public void Penalize(TimeSpan now, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }
            lock (_lock)
            {
                TimeSpan until = now + duration;
                if (until > _blockedUntil)
                {
                    _blockedUntil = until;
                }
            }
        }

        /// <summary>清空所有记账与冷却。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _stamps.Clear();
                _blockedUntil = TimeSpan.MinValue;
            }
        }

        private void Expire(TimeSpan now)
        {
            while (_stamps.Count > 0 && now - _stamps.Peek() >= _window)
            {
                _stamps.Dequeue();
            }
        }
    }
}
