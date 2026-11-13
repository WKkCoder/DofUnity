using System;
using System.Collections.Generic;

// 动作的叠加层与声音事件流:按主层帧号推进一个 [MOTION],到点就出事件。纯数据推进,不碰触发器、不要宿主、不要 .ani。
// 对齐方式(本机样本统计出来的推断,汇总数,非 REA):叠加层以主层帧号对齐起点——主层放到登记的起始帧时叠加层开始,之后用自己 .ani 的延时独立走(时间驱动,
// 不是逐帧锁步:主动画与叠加动画都读得出的 19569 个层里,帧数相同的占 60.5%、逐帧延时序列相同的占 54.9%);声音以主层帧号对齐——主层放到登记的帧时响。
// 没有读通:主层循环绕圈时叠加层是否跟着重起(这里不重起)、登记的帧不小于主层帧数的声音(36 条,这里永不响)、叠加层自己播完之后的去向。
namespace Dnf.Game.Act
{
    /// <summary>
    /// 一个 [MOTION] 的事件播放器。每次更新调 <see cref="Advance"/> 传主层当前帧号:
    /// 帧号比上次大一以上时按顺序补跑被跳过的帧(与 <see cref="ActRunner"/> 补帧一致),每一帧里先出叠加层起(按合成名次从小到大),再出声音(按文件顺序);
    /// 帧号变小(循环动画绕了一圈)时声音重新装填、叠加层不重起;同一帧重复调用不重复出事件。动作结束或重来时调 <see cref="End"/> 让已起的叠加层收尾,
    /// 再 <see cref="Begin"/> 重新开始。
    /// </summary>
    public sealed class ActMotionPlayer
    {
        private readonly ActMotion _motion;
        private readonly bool[] _started;
        private readonly bool[] _fired;
        private int _last = -1;
        private bool _ended;

        public ActMotionPlayer(ActMotion motion)
        {
            _motion = motion ?? throw new ArgumentNullException(nameof(motion));
            _started = new bool[motion.Layers.Count];
            _fired = new bool[motion.Sounds.Count];
        }

        public ActMotion Motion => _motion;

        /// <summary>已经处理到的主层帧号(还没开始是 -1)。</summary>
        public int LastFrame => _last;

        /// <summary><see cref="End"/> 之后到 <see cref="Begin"/> 之前为真;这段时间 <see cref="Advance"/> 什么都不出。</summary>
        public bool Ended => _ended;

        /// <summary>已经起了、还没收的叠加层个数。</summary>
        public int ActiveLayers
        {
            get
            {
                int n = 0;
                foreach (bool s in _started)
                {
                    if (s)
                    {
                        n++;
                    }
                }
                return _ended ? 0 : n;
            }
        }

        /// <summary>动作(重新)开始:所有叠加层与声音回到没起、没响的状态。上一轮没收的叠加层不会补出 LayerEnd(要收先调 <see cref="End"/>)。</summary>
        public void Begin()
        {
            Array.Clear(_started, 0, _started.Length);
            Array.Clear(_fired, 0, _fired.Length);
            _last = -1;
            _ended = false;
        }

        /// <summary>
        /// 主层放到第 <paramref name="frame"/> 帧:把这一次新到点的事件追加到 <paramref name="into"/>,返回追加了几条。
        /// 负帧号(还没有动画)与 <see cref="End"/> 之后的调用什么都不出。
        /// </summary>
        public int Advance(int frame, List<ActMotionEvent> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            if (_ended || frame < 0)
            {
                return 0;
            }
            int before = into.Count;
            if (frame < _last)
            {
                // 动画绕了一圈回到前面:声音每一圈各响一次,所以重新装填;叠加层是动作级的,已经起的不再起
                Array.Clear(_fired, 0, _fired.Length);
                _last = -1;
            }
            for (int f = _last + 1; f <= frame; f++)
            {
                StartLayers(f, into);
                FireSounds(f, into);
            }
            if (frame > _last)
            {
                _last = frame;
            }
            return into.Count - before;
        }

        /// <summary>
        /// 动作结束:已经起了的叠加层按合成名次各出一条 LayerEnd(帧号是主层停在的帧);只收一次,之后到 <see cref="Begin"/> 之前不再出事件。
        /// </summary>
        public int End(List<ActMotionEvent> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            if (_ended)
            {
                return 0;
            }
            _ended = true;
            int before = into.Count;
            int frame = _last < 0 ? 0 : _last;
            foreach (ActOverlayLayer layer in _motion.CompositeOrder)
            {
                if (_started[layer.Index])
                {
                    into.Add(new ActMotionEvent(ActMotionEventCode.LayerEnd, frame, layer, null));
                }
            }
            return into.Count - before;
        }

        private void StartLayers(int frame, List<ActMotionEvent> into)
        {
            // 起始帧写成负数(样本里没有)的层在第 0 帧起;起始帧在主层帧数以外的永远起不来
            foreach (ActOverlayLayer layer in _motion.CompositeOrder)
            {
                if (!_started[layer.Index] && layer.StartFrame <= frame)
                {
                    _started[layer.Index] = true;
                    into.Add(new ActMotionEvent(ActMotionEventCode.LayerStart, frame, layer, null));
                }
            }
        }

        private void FireSounds(int frame, List<ActMotionEvent> into)
        {
            IReadOnlyList<ActSoundEvent> sounds = _motion.Sounds;
            for (int i = 0; i < sounds.Count; i++)
            {
                ActSoundEvent s = sounds[i];
                if (!_fired[s.Index] && s.Frame == frame)
                {
                    _fired[s.Index] = true;
                    into.Add(new ActMotionEvent(ActMotionEventCode.Sound, frame, null, s));
                }
            }
        }
    }
}
