using System;
using System.Collections.Generic;
using Dnf.Game.Act;

// SUB ANI 线:没有触发器、只有叠加层与声音的出招(以及自定义动作),事件流不经运行器,由这里单独放。
namespace Dnf.Play
{
    /// <summary>
    /// 怪物出招的 [MOTION] 事件流(<see cref="ActMotionPlayer"/>)。
    /// <para>
    /// 有触发器的招照旧由 <see cref="ActRunner"/> 跑,事件流是运行器每一步顺带出的(<see cref="ActRunner.Update"/> 一开头),这里不重复;
    /// 没有触发器的招过去不建运行器(出招阶段不为它们多做任何事),所以事件流单独挂在这里:出招开始时起、每一步按当前动画帧推进、离开出招阶段时收口。
    /// 两条路径给宿主的是同一种事件(<see cref="IActHost.MotionEvent"/>),同一帧的先后顺序也一样:叠加层先、声音后。
    /// </para>
    /// <para>
    /// 只读不写:不动速度、攻击框、行为标签、随机数,怪物的行为、命中与伤害都不会因为这一段而变(没有宿主、或程序里没有事件时这里什么都不做)。
    /// 没有覆盖的:没有触发器的非出招动作族(站立、行走、受击、倒地等)与被动对象的动作——它们没有运行器,事件流这一版不出(有触发器的那些经运行器照出)。
    /// </para>
    /// </summary>
    public sealed partial class MonsterActor
    {
        private ActProgram[] _motionPrograms;
        private IActHost _motionHost;
        private ActMotionPlayer _motionPlayer;
        private IActHost _motionPlayerHost;
        private List<ActMotionEvent> _motionBuffer;

        /// <summary>这一段单独放(没经运行器)出去的事件个数,测试与审计用。</summary>
        public int MotionEventsFed { get; private set; }

        /// <summary>此刻有一条单独放的事件流在跑(没有触发器的招正在出,且它的 [MOTION] 里有叠加层或声音)。</summary>
        public bool MotionStreamActive => _motionPlayer != null;

        /// <summary>
        /// 装上"没有触发器、但 [MOTION] 里有叠加层或声音"的各招:<paramref name="programs"/> 与 <see cref="MonsterModel.Attacks"/> 同下标,其余的招给 null;
        /// 传 null(或没有宿主)清掉。有触发器的招不放在这里(运行器自己出事件)。
        /// </summary>
        public void SetMotionActs(ActProgram[] programs, IActHost host)
        {
            EndMotionEvents();
            _motionPrograms = null;
            _motionHost = null;
            if (programs == null || host == null)
            {
                return;
            }
            foreach (ActProgram p in programs)
            {
                if (p != null && p.HasMotionEvents)
                {
                    _motionPrograms = programs;
                    _motionHost = host;
                    return;
                }
            }
        }

        /// <summary>[CHANGE ACTION FILE] 换了第 <paramref name="index"/> 招:这一招是不是"只有事件流"随新文件更新(换成有触发器的、或没有事件的就撤掉)。</summary>
        private void InstallMotionAttack(int index, PassiveAction action, IActHost host)
        {
            bool feed = !action.HasTriggers && action.Program.HasMotionEvents;
            if (_motionPrograms == null)
            {
                if (!feed || host == null)
                {
                    return;
                }
                _motionPrograms = new ActProgram[Math.Max(_anis.Attacks.Length, index + 1)];
                _motionHost = host;
            }
            else if (index >= _motionPrograms.Length)
            {
                Array.Resize(ref _motionPrograms, index + 1);
            }
            _motionPrograms[index] = feed ? action.Program : null;
        }

        /// <summary>进出招阶段(<see cref="BeginActs"/>):这一招(或自定义动作)是"只有事件流"的就起一条;不是就什么都不起。</summary>
        private void BeginMotionEvents()
        {
            EndMotionEvents();
            ActProgram program = null;
            IActHost host = null;
            if (_customIndex >= 0)
            {
                PassiveAction custom = _customs != null && (uint)_customIndex < (uint)_customs.Length ? _customs[_customIndex] : null;
                if (custom != null && !custom.HasTriggers)
                {
                    program = custom.Program;
                    host = _customHost;
                }
            }
            else if (_motionPrograms != null && (uint)_attackIndex < (uint)_motionPrograms.Length)
            {
                program = _motionPrograms[_attackIndex];
                host = _motionHost;
            }
            if (program == null || host == null || !program.HasMotionEvents)
            {
                return;
            }
            _motionPlayer = new ActMotionPlayer(program.Motions[0]);
            _motionPlayerHost = host;
            _motionPlayer.Begin();
        }

        /// <summary>出招阶段每一步:按当前动画帧推进事件流(帧被跳过的部分补报,见 <see cref="ActMotionPlayer.Advance"/>)。</summary>
        private void TickMotionEvents()
        {
            ActMotionPlayer player = _motionPlayer;
            if (player == null)
            {
                return;
            }
            List<ActMotionEvent> buffer = _motionBuffer ?? (_motionBuffer = new List<ActMotionEvent>());
            buffer.Clear();
            if (player.Advance(CurrentFrame, buffer) > 0)
            {
                DeliverMotionEvents(buffer);
            }
        }

        /// <summary>离开出招阶段、换招、重来:已经开始的叠加层各出一个"结束",然后撤掉这条事件流。</summary>
        private void EndMotionEvents()
        {
            ActMotionPlayer player = _motionPlayer;
            if (player == null)
            {
                return;
            }
            _motionPlayer = null;
            List<ActMotionEvent> buffer = _motionBuffer ?? (_motionBuffer = new List<ActMotionEvent>());
            buffer.Clear();
            if (player.End(buffer) > 0)
            {
                DeliverMotionEvents(buffer);
            }
            _motionPlayerHost = null;
        }

        private void DeliverMotionEvents(List<ActMotionEvent> events)
        {
            IActHost host = _motionPlayerHost;
            if (host == null)
            {
                return;
            }
            // 宿主的处理可能再动这只怪物(把事件流撤掉等):先取一份快照再逐个交
            ActMotionEvent[] snapshot = events.ToArray();
            events.Clear();
            IActEntity self = ActSelf;
            foreach (ActMotionEvent e in snapshot)
            {
                MotionEventsFed++;
                host.MotionEvent(self, e);
            }
        }
    }
}
