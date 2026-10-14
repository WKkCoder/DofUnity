using System;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 播放一个 .ani:按每帧延时推进,帧进入时报告关键帧标志(帧属性 23),播完时报告结束(循环动画不报告结束)。
    /// 设置新动画后,第 0 帧的标志在下一次 <see cref="Advance"/> 时才报告(避免在脚本调用 sq_SetCurrentAnimation 的当场重入脚本)。
    /// 结束与循环的判定:循环 = 全局属性 0(<see cref="AniFile.Loops"/>,推断 E-025);结束 = 最后一帧的延时走完。
    /// 延时为 0 的帧瞬间走过。
    /// </summary>
    public sealed class Animator
    {
        private bool _entryPending;
        private int _version;

        public AniFile Clip { get; private set; }
        public string Path { get; private set; }
        public int FrameIndex { get; private set; }

        /// <summary>当前帧已经播放的毫秒数。</summary>
        public int FrameElapsedMs { get; private set; }

        public bool Ended { get; private set; }

        /// <summary>播放速度,千分比(1000 = 原速)。</summary>
        public int SpeedPermille { get; set; } = 1000;

        public AniFrame CurrentFrame
        {
            get
            {
                if (Clip == null || Clip.Frames.Length == 0)
                {
                    return null;
                }
                return Clip.Frames[Math.Min(FrameIndex, Clip.Frames.Length - 1)];
            }
        }

        /// <summary>开始播放(clip 为 null 表示没有动画)。</summary>
        public void Set(AniFile clip, string path)
        {
            _version++;
            Clip = clip;
            Path = path;
            FrameIndex = 0;
            FrameElapsedMs = 0;
            Ended = clip == null || clip.Frames.Length == 0;
            _entryPending = !Ended;
        }

        /// <summary>
        /// 推进 dtMs 毫秒。onFlag(帧标志)在每次进入带标志的帧时调用;onEnd 在动画播完时调用一次。
        /// 回调里可以再调用 <see cref="Set"/>,此时本次推进立即结束(新动画从下一次 Advance 开始)。
        /// </summary>
        public void Advance(int dtMs, Action<int> onFlag, Action onEnd)
        {
            AniFile clip = Clip;
            if (clip == null || clip.Frames.Length == 0)
            {
                return;
            }
            int version = _version;
            if (_entryPending)
            {
                _entryPending = false;
                Enter(clip.Frames[0], onFlag);
                if (version != _version)
                {
                    return;
                }
            }
            if (Ended)
            {
                return;
            }
            long remaining = (long)dtMs * SpeedPermille / 1000;
            for (int guard = 0; guard < 100000; guard++)
            {
                int delay = clip.Frames[FrameIndex].DelayMs;
                long left = Math.Max(0, delay - FrameElapsedMs);
                if (remaining < left)
                {
                    FrameElapsedMs += (int)remaining;
                    return;
                }
                remaining -= left;
                FrameElapsedMs = 0;
                if (FrameIndex + 1 < clip.Frames.Length)
                {
                    FrameIndex++;
                }
                else if (clip.Loops && clip.TotalDurationMs > 0)
                {
                    FrameIndex = 0;
                }
                else
                {
                    Ended = true;
                    onEnd?.Invoke();
                    return;
                }
                Enter(clip.Frames[FrameIndex], onFlag);
                if (version != _version)
                {
                    return;
                }
            }
        }

        private static void Enter(AniFrame frame, Action<int> onFlag)
        {
            if (onFlag != null && frame.TryGetKeyFrameFlag(out int flag))
            {
                onFlag(flag);
            }
        }
    }
}
