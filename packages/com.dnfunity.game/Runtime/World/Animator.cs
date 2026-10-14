using System;
using System.Collections.Generic;
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

        // 脚本改写的帧延时(sq_SetFrameDelayTime):帧号 -> 毫秒,只作用于这个播放器;换动画时清空
        private System.Collections.Generic.Dictionary<int, int> _delayOverrides;

        public AniFile Clip { get; private set; }
        public string Path { get; private set; }
        public int FrameIndex { get; private set; }

        /// <summary>当前帧已经播放的毫秒数。</summary>
        public int FrameElapsedMs { get; private set; }

        public bool Ended { get; private set; }

        /// <summary>
        /// 自动画开始(或最近一次跳帧)起累计的毫秒数:循环动画跨过一圈、动画播完之后都继续累加。
        /// sq_GetCurrentTime 取这个值(推断:ChainLightning 等持续型技能在动画播完后用 currentT > 持续时间 结束)。
        /// </summary>
        public long TotalPlayedMs { get; private set; }

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

        private List<Animator> _layers;

        /// <summary>叠在这个动画上的图层动画(脚本的 addLayerAnimation);随宿主一起推进(推断),宿主换动画时丢掉。</summary>
        public int LayerCount => _layers == null ? 0 : _layers.Count;

        public void AddLayer(Animator layer)
        {
            if (layer == null || ReferenceEquals(layer, this))
            {
                return;
            }
            if (_layers == null)
            {
                _layers = new List<Animator>();
            }
            if (!_layers.Contains(layer))
            {
                _layers.Add(layer);
            }
        }

        public void RemoveLayer(Animator layer)
        {
            _layers?.Remove(layer);
        }

        /// <summary>开始播放(clip 为 null 表示没有动画)。</summary>
        public void Set(AniFile clip, string path)
        {
            _layers = null;
            _loopLeft = null;
            _version++;
            _delayOverrides = null;
            Clip = clip;
            Path = path;
            FrameIndex = 0;
            FrameElapsedMs = 0;
            TotalPlayedMs = 0;
            Ended = clip == null || clip.Frames.Length == 0;
            _entryPending = !Ended;
        }

        /// <summary>
        /// 跳到第 index 帧(越界取端点)并从该帧起继续播放(已结束的动画复活);该帧的关键帧标志在下一次 <see cref="Advance"/> 时报告。
        /// 对应脚本的 setCurrentFrameWithChildLayer(依据 PieceOfIce 等脚本'跳到最后几帧让它消失'的用法推断)。
        /// </summary>
        public void SetFrame(int index)
        {
            if (Clip == null || Clip.Frames.Length == 0)
            {
                return;
            }
            _version++;
            FrameIndex = Math.Max(0, Math.Min(index, Clip.Frames.Length - 1));
            FrameElapsedMs = 0;
            TotalPlayedMs = FrameStartMs(FrameIndex);
            Ended = false;
            _entryPending = true;
        }

        /// <summary>第 frame 帧的延时(毫秒):脚本改写过的取改写值,否则取 .ani 里的值。</summary>
        public int DelayOf(int frame)
        {
            if (Clip == null || (uint)frame >= (uint)Clip.Frames.Length)
            {
                return 0;
            }
            return _delayOverrides != null && _delayOverrides.TryGetValue(frame, out int d) ? d : Clip.Frames[frame].DelayMs;
        }

        /// <summary>改写第 frame 帧的延时(毫秒,不小于 0);帧号越界忽略。</summary>
        public void SetFrameDelay(int frame, int delayMs)
        {
            if (Clip == null || (uint)frame >= (uint)Clip.Frames.Length)
            {
                return;
            }
            if (_delayOverrides == null)
            {
                _delayOverrides = new System.Collections.Generic.Dictionary<int, int>();
            }
            _delayOverrides[frame] = Math.Max(0, delayMs);
        }

        /// <summary>第 frame 帧开始的时刻(毫秒,原速):前面所有帧的延时之和。</summary>
        public int FrameStartMs(int frame)
        {
            return DelaySumMs(0, frame);
        }

        /// <summary>从第 from 帧(含)到第 to 帧(不含)的延时之和(毫秒,原速);to 超出帧数时取到末尾。</summary>
        public int DelaySumMs(int from, int to)
        {
            if (Clip == null)
            {
                return 0;
            }
            int sum = 0;
            int end = Math.Min(to, Clip.Frames.Length);
            for (int i = Math.Max(0, from); i < end; i++)
            {
                sum += DelayOf(i);
            }
            return sum;
        }

        /// <summary>动画的总时长(毫秒,原速)。</summary>
        public int TotalMs => Clip == null ? 0 : DelaySumMs(0, Clip.Frames.Length);

        /// <summary>
        /// 推进 dtMs 毫秒。onFlag(帧标志)在每次进入带标志的帧时调用;onEnd 在动画播完时调用一次。
        /// 回调里可以再调用 <see cref="Set"/>,此时本次推进立即结束(新动画从下一次 Advance 开始)。
        /// </summary>
        public void Advance(int dtMs, Action<int> onFlag, Action onEnd)
        {
            AdvanceSelf(dtMs, onFlag, onEnd);
            // 图层动画随宿主推进;回调里宿主可能换了动画(图层被丢掉),所以按下标取
            for (int i = 0; _layers != null && i < _layers.Count; i++)
            {
                _layers[i].Advance(dtMs, null, null);
            }
        }

        private void AdvanceSelf(int dtMs, Action<int> onFlag, Action onEnd)
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
                Enter(clip.Frames[Math.Min(FrameIndex, clip.Frames.Length - 1)], onFlag);
                if (version != _version)
                {
                    return;
                }
            }
            if (Ended)
            {
                // 播完以后时间照常往前走(sq_GetCurrentTime 是"自这个动画开始的时间",见 TotalPlayedMs)
                TotalPlayedMs += (long)dtMs * SpeedPermille / 1000;
                return;
            }
            long remaining = (long)dtMs * SpeedPermille / 1000;
            for (int guard = 0; guard < 100000; guard++)
            {
                int delay = DelayOf(FrameIndex);
                long left = Math.Max(0, delay - FrameElapsedMs);
                if (remaining < left)
                {
                    FrameElapsedMs += (int)remaining;
                    TotalPlayedMs += remaining;
                    return;
                }
                remaining -= left;
                TotalPlayedMs += left;
                FrameElapsedMs = 0;
                int jump = LoopJump(clip);
                if (jump >= 0)
                {
                    FrameIndex = jump;
                }
                else if (FrameIndex + 1 < clip.Frames.Length)
                {
                    FrameIndex++;
                }
                else if (clip.Loops && clip.TotalDurationMs > 0)
                {
                    FrameIndex = 0;
                    if (_loopLeft != null)
                    {
                        // 整段动画重新开始时,各个循环标记的次数重新计(推断:没有证据说明原版怎么处理)
                        _loopLeft.Clear();
                    }
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

        // 每个循环终点帧还剩几次重复(按帧号);换动画时清空
        private Dictionary<int, int> _loopLeft;

        /// <summary>
        /// 当前帧播完时的循环跳转(推断,证据 E-039):当前帧带循环终点标记(帧属性 26,负载是重复次数)且还有剩余次数时,
        /// 回到它之前最近的循环起点帧(帧属性 25;找不到就回第 0 帧),并把剩余次数减一;否则返回 -1。
        /// 次数的含义('再重复几次')与跳回的帧**未验证**;99999 一类的大数等于一直重复。
        /// </summary>
        private int LoopJump(AniFile clip)
        {
            if (!clip.Frames[FrameIndex].TryGetLoopEnd(out int repeat))
            {
                return -1;
            }
            if (_loopLeft == null)
            {
                _loopLeft = new Dictionary<int, int>();
            }
            int left = _loopLeft.TryGetValue(FrameIndex, out int l) ? l : repeat;
            if (left <= 0)
            {
                return -1;
            }
            _loopLeft[FrameIndex] = left - 1;
            for (int i = FrameIndex; i >= 0; i--)
            {
                if (clip.Frames[i].IsLoopStart)
                {
                    return i;
                }
            }
            return 0;
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
