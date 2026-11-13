using System.Collections.Generic;

// 动作的叠加层与声音事件流在运行器与宿主之间的接线。事件本身由 ActMotionPlayer 按主层帧号产出(见 ActMotionTypes.cs 的结构与对齐说明);
// 运行器只负责在每次 Update 时用主层帧号推它一步、把出来的事件交给宿主。叠加层的合成顺序是数据结论(ActOverlayLayer.Rank),宿主现在不必显示。
namespace Dnf.Game.Act
{
    /// <summary>运行器对世界的请求:叠加层与声音事件流。</summary>
    public partial interface IActHost
    {
        /// <summary>
        /// 动作的叠加层与声音事件(<see cref="ActMotionEvent"/>):[MOTION] 里的 [SUB ANI*] 与 [SOUND*] 登记按主层帧号到点时通报,
        /// 叠加层起、声音是登记的那一帧(补帧时按登记的帧报),叠加层止是动作结束时(<see cref="ActRunner.EndMotion"/> 或重新 <see cref="ActRunner.Begin"/>)。
        /// 声音的概率(<see cref="ActSoundEvent.Percent"/>)只是随事件带来的数,谁掷骰由宿主定。
        /// 与触发器无关:程序没有触发器也出。<paramref name="owner"/> 是动作的拥有者。默认什么都不做(宿主不显示叠加层、没有声音出口就不用管)。
        /// 宿主接这条事件流的两个接点:叠加层 = 起时按 <see cref="ActOverlayLayer.Path"/> 开一路叠加动画、按 <see cref="ActOverlayLayer.Rank"/> 排画的先后、止时关掉;
        /// 声音 = 按 <see cref="ActSoundEvent.Key"/>(audio.xml 的 ID)交给声音出口。
        /// </summary>
        void MotionEvent(IActEntity owner, ActMotionEvent e)
        {
        }
    }

    public sealed partial class ActRunner
    {
        private ActMotionPlayer _motion;
        private readonly List<ActMotionEvent> _motionBuf = new List<ActMotionEvent>();

        /// <summary>程序的第一个 [MOTION] 带叠加层或声音登记,这个运行器会出事件流。</summary>
        public bool HasMotionEvents => _motion != null;

        /// <summary>已经交给宿主的事件条数(测试与审计用)。</summary>
        public int MotionEventsSent { get; private set; }

        /// <summary>构造时调用:程序的第一个 [MOTION](与主动画同一个)有登记才建事件播放器。</summary>
        private void InitMotion()
        {
            if (_program.HasMotionEvents)
            {
                _motion = new ActMotionPlayer(_program.Motions[0]);
            }
        }

        /// <summary>动作重来:上一轮还挂着的叠加层先收尾,再从头开始。</summary>
        private void BeginMotion()
        {
            if (_motion == null)
            {
                return;
            }
            if (_motion.ActiveLayers > 0)
            {
                _motion.End(_motionBuf);
                DeliverMotion();
            }
            _motion.Begin();
        }

        /// <summary>每次更新:用主层帧号推事件播放器一步,出来的事件按顺序交给宿主。</summary>
        private void PumpMotion(int frame)
        {
            if (_motion == null)
            {
                return;
            }
            _motion.Advance(frame, _motionBuf);
            DeliverMotion();
        }

        /// <summary>
        /// 动作结束(宿主要换掉或丢掉这个运行器之前调):已经起了的叠加层各出一条 LayerEnd。只收一次;之后更新不再出事件,直到重新 <see cref="Begin"/>。
        /// 没有叠加层与声音登记的程序什么都不做。
        /// </summary>
        public void EndMotion()
        {
            if (_motion == null)
            {
                return;
            }
            _motion.End(_motionBuf);
            DeliverMotion();
        }

        private void DeliverMotion()
        {
            if (_motionBuf.Count == 0)
            {
                return;
            }
            // 先拷出来再交:宿主在回调里若又推动了这个运行器,缓冲不会被改在手上
            ActMotionEvent[] batch = _motionBuf.ToArray();
            _motionBuf.Clear();
            foreach (ActMotionEvent e in batch)
            {
                MotionEventsSent++;
                _host.MotionEvent(_owner, e);
            }
        }
    }
}
