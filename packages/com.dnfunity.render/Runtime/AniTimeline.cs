using System;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>某一时刻 .ani 的播放状态:正在显示的帧、插值目标帧,以及当前帧内已经过去的比例。</summary>
    public readonly struct AniSample
    {
        /// <summary>正在显示的帧号;动画没有帧时为 -1。</summary>
        public int Frame { get; }

        /// <summary>插值的目标帧(下一帧);当前帧不插值时等于 <see cref="Frame"/>。</summary>
        public int NextFrame { get; }

        /// <summary>当前帧内已经过去的比例,0 到 1;时长为 0 的帧、停在末尾的动画为 0。</summary>
        public double Progress { get; }

        /// <summary>当前帧带"与下一帧插值"开关并且存在下一帧(证据 E-030,只核对过染色 alpha)。</summary>
        public bool Interpolates { get; }

        public AniSample(int frame, int nextFrame, double progress, bool interpolates)
        {
            Frame = frame;
            NextFrame = nextFrame;
            Progress = progress;
            Interpolates = interpolates;
        }
    }

    /// <summary>
    /// 把时间映射到 .ani 的帧号。每一帧的时长取帧属性 12(毫秒,已核对);没有该属性的帧时长为 0。
    /// 循环与否看全局属性 0(<see cref="AniFile.Loops"/>,推断,证据 E-025)。
    /// 不循环的动画停在最后一帧;循环但总时长为 0 的动画停在第 0 帧。
    /// 带"与下一帧插值"开关的帧(帧属性 10,推断,证据 E-030)在 <see cref="Sample"/> 里给出插值目标与进度。
    /// </summary>
    public sealed class AniTimeline
    {
        private readonly AniFile _ani;
        private readonly long[] _starts;
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
            _ani = ani;
            FrameCount = ani.Frames.Length;
            _starts = new long[FrameCount];
            _ends = new long[FrameCount];
            long t = 0;
            for (int i = 0; i < FrameCount; i++)
            {
                _starts[i] = t;
                if (ani.Frames[i].TryGetDelayMs(out int ms) && ms > 0)
                {
                    t += ms;
                }
                _ends[i] = t;
            }
            TotalMilliseconds = t;
            Loops = ani.Loops;
        }

        /// <summary>第 <paramref name="frame"/> 帧的起始时刻(毫秒,从动画开始算);帧号越界时夹在 [0, 最后一帧]。</summary>
        public long StartMs(int frame)
        {
            if (FrameCount == 0)
            {
                return 0;
            }
            return _starts[frame < 0 ? 0 : (frame >= FrameCount ? FrameCount - 1 : frame)];
        }

        /// <summary>时刻 <paramref name="ms"/>(从动画开始算)正在显示的帧号;没有帧时返回 -1。</summary>
        public int FrameAt(long ms)
        {
            return Sample(ms).Frame;
        }

        /// <summary>
        /// 时刻 <paramref name="ms"/> 的播放状态。帧号的选法与 <see cref="FrameAt"/> 相同(它就是调用这里);
        /// 插值信息只在帧带插值开关、且有下一帧时才有意义。
        /// </summary>
        public AniSample Sample(long ms)
        {
            if (FrameCount == 0)
            {
                return new AniSample(-1, -1, 0, false);
            }
            if (ms < 0)
            {
                ms = 0;
            }
            if (TotalMilliseconds == 0)
            {
                // 没有任何延时:时间推不动它,固定显示第 0 帧
                return new AniSample(0, 0, 0, false);
            }
            if (Loops)
            {
                ms %= TotalMilliseconds;
            }
            else if (ms >= TotalMilliseconds)
            {
                return new AniSample(FrameCount - 1, FrameCount - 1, 0, false);
            }
            // 第一个"结束时刻 > ms"的帧;时长为 0 的帧不会被选中
            int frame = FrameCount - 1;
            for (int i = 0; i < FrameCount; i++)
            {
                if (ms < _ends[i])
                {
                    frame = i;
                    break;
                }
            }
            long length = _ends[frame] - _starts[frame];
            double progress = length > 0 ? (double)(ms - _starts[frame]) / length : 0.0;
            int next = frame + 1 < FrameCount ? frame + 1 : frame;
            bool interpolates = next != frame && _ani.Frames[frame].InterpolatesToNext;
            return new AniSample(frame, interpolates ? next : frame, progress, interpolates);
        }

        /// <summary>
        /// 时刻 <paramref name="ms"/> 的染色 alpha(0 到 255,四舍五入)。当前帧带插值开关时,在当前帧与下一帧的染色 alpha 之间线性插值,
        /// 否则就是当前帧的染色 alpha(没有染色属性按 255)。线性插值已用城镇的三个光效层核对(证据 E-030)。
        /// </summary>
        public int TintAlphaAt(long ms)
        {
            AniSample s = Sample(ms);
            if (s.Frame < 0)
            {
                return 255;
            }
            int a0 = _ani.Frames[s.Frame].TintAlpha;
            if (!s.Interpolates)
            {
                return a0;
            }
            int a1 = _ani.Frames[s.NextFrame].TintAlpha;
            return (int)Math.Round(a0 + (a1 - a0) * s.Progress, MidpointRounding.AwayFromZero);
        }
    }
}
