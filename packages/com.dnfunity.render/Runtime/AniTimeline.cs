using System;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 把时间映射到 .ani 的帧号。每一帧的时长取帧属性 12(毫秒,已核对);没有该属性的帧时长为 0。
    /// 循环与否看全局属性 0(<see cref="AniFile.Loops"/>,推断,证据 E-025)。
    /// 不循环的动画停在最后一帧;循环但总时长为 0 的动画停在第 0 帧。
    /// </summary>
    public sealed class AniTimeline
    {
        private readonly long[] _ends;

        public int FrameCount { get; }
        public long TotalMilliseconds { get; }
        public bool Loops { get; }

        public AniTimeline(AniFile ani)
        {
            if (ani == null)
            {
                throw new ArgumentNullException(nameof(ani));
            }
            FrameCount = ani.Frames.Length;
            _ends = new long[FrameCount];
            long t = 0;
            for (int i = 0; i < FrameCount; i++)
            {
                if (ani.Frames[i].TryGetDelayMs(out int ms) && ms > 0)
                {
                    t += ms;
                }
                _ends[i] = t;
            }
            TotalMilliseconds = t;
            Loops = ani.Loops;
        }

        /// <summary>时刻 <paramref name="ms"/>(从动画开始算)正在显示的帧号;没有帧时返回 -1。</summary>
        public int FrameAt(long ms)
        {
            if (FrameCount == 0)
            {
                return -1;
            }
            if (ms < 0)
            {
                ms = 0;
            }
            if (TotalMilliseconds == 0)
            {
                // 没有任何延时:时间推不动它,固定显示第 0 帧
                return 0;
            }
            if (Loops)
            {
                ms %= TotalMilliseconds;
            }
            else if (ms >= TotalMilliseconds)
            {
                return FrameCount - 1;
            }
            // 第一个"结束时刻 > ms"的帧;时长为 0 的帧不会被选中
            for (int i = 0; i < FrameCount; i++)
            {
                if (ms < _ends[i])
                {
                    return i;
                }
            }
            return FrameCount - 1;
        }
    }
}
