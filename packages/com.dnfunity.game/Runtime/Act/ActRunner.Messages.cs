using System.Collections.Generic;

namespace Dnf.Game.Act
{
    /// <summary>
    /// 消息族行为的执行([SEND DO BEHAVIOR]、[SEND MESSAGE]、[CHANGE AI]、[CAMOUFLAGE]、[APPEND HIT]);
    /// 客户端执行块在 0x010b56c0 里的 0x71、0x79、0xec、0xae、0x82 分支(静态读码,非 REA)。
    /// </summary>
    public sealed partial class ActRunner
    {
        /// <summary>
        /// 别的对象(或自己)发来的"执行第 n 组"消息的信箱。客户端是写进网络包、收包再按对象编号找到动作实例去执行(0x010d1370 → 0x00e664a2),
        /// 中间隔着一次消息往返,所以这里同样不当场执行,放到运行器下一次 <see cref="Update"/> 的开头;动作重来(<see cref="Begin"/>)时清空。
        /// </summary>
        private readonly List<int> _remote = new List<int>();

        /// <summary>信箱里还没执行的消息条数(测试与审计用)。</summary>
        public int PendingRemote => _remote.Count;

        /// <summary>
        /// 收一条"执行第 <paramref name="group"/> 组、目标列表只含拥有者自己"的消息(<see cref="IActGroupReceiver.PostActGroup"/> 的落点)。
        /// 组号不在范围内的消息收下了也什么都不执行(<see cref="RunGroup"/> 的范围检查)。
        /// </summary>
        public void EnqueueRemoteGroup(int group)
        {
            _remote.Add(group);
        }

        /// <summary>每次更新开头:把信箱里已有的消息按到达顺序执行;执行中新到的留到下一次更新(自己发给自己不会原地死循环)。</summary>
        private void RunRemote()
        {
            if (_remote.Count == 0)
            {
                return;
            }
            int[] batch = _remote.ToArray();
            _remote.Clear();
            for (int i = 0; i < batch.Length; i++)
            {
                if (_aborted)
                {
                    // 前一条切了动作:没执行的消息放回信箱,新动作重来时 Begin 会清掉
                    for (int j = i; j < batch.Length; j++)
                    {
                        _remote.Add(batch[j]);
                    }
                    return;
                }
                RunGroup(batch[i], new List<IActEntity> { _owner });
            }
        }

        private void ExecuteMessage(ActBehavior b, List<IActEntity> targets)
        {
            ActMessageSpec spec = b.Msg;
            switch (b.Kind)
            {
                case ActBehaviorKind.SendDoBehavior:
                    SendDoBehavior(spec, targets);
                    break;
                case ActBehaviorKind.SendMessage:
                    SendMessage(spec, targets);
                    break;
                case ActBehaviorKind.ChangeAi:
                    ChangeAi(spec, targets);
                    break;
                case ActBehaviorKind.Camouflage:
                    Camouflage(spec, targets);
                    break;
                case ActBehaviorKind.AppendHit:
                    AppendHit(b, targets);
                    break;
            }
        }

        /// <summary>
        /// [SEND DO BEHAVIOR](客户端 0x010b9483 起):组号只求一次(循环之前);对目标集合里每个对象各发一条——列表里只放这一个对象,
        /// 收件人是它自己当前动作的运行器;没有当前动作的对象(虚槽 0x148 返回空)跳过,不影响后面的对象。
        /// </summary>
        private void SendDoBehavior(ActMessageSpec spec, List<IActEntity> targets)
        {
            int group = spec.GroupValue.Eval(_rand);
            foreach (IActEntity t in targets)
            {
                if (t is IActGroupReceiver receiver)
                {
                    receiver.PostActGroup(group);
                }
            }
        }

        /// <summary>
        /// [SEND MESSAGE](客户端 0x010bb716 起):目标须是主动对象(虚槽 0x30(0x11) 与 cast 到 0x19d98fc,被动对象跳过)且有 AI 从属(虚槽 0x1b0 非空);
        /// ETC 族还要求行为拥有者是 AICharacter 类(0x010bb938 起的 cast 到 0x19db264,不是就整个目标跳过)。
        /// 本机控制的检查在移植里恒真(单机,所有对象都是本机的)。
        /// </summary>
        private void SendMessage(ActMessageSpec spec, List<IActEntity> targets)
        {
            ActMessageFamily family = spec.Family;
            if (family == ActMessageFamily.None)
            {
                return;
            }
            foreach (IActEntity t in targets)
            {
                if (t.Kind == ActEntityKind.Passive || !(t is IActAiPeer peer) || !peer.HasAiSlave)
                {
                    continue;
                }
                int[] p = spec.Params;
                switch (family)
                {
                    case ActMessageFamily.Attack:
                    case ActMessageFamily.Die:
                        peer.ReceiveAiMessage(new ActAiMessage(family, new[] { p[0] }, null));
                        break;
                    case ActMessageFamily.Move:
                        peer.ReceiveAiMessage(new ActAiMessage(family, new[] { p[0], p[1], p[2], p[3] }, null));
                        break;
                    case ActMessageFamily.Rise:
                        peer.ReceiveAiMessage(new ActAiMessage(family, null, null));
                        break;
                    case ActMessageFamily.Etc:
                    {
                        if (!(_owner is IActAiPeer sender) || !sender.IsAiCharacter)
                        {
                            break;
                        }
                        // 第 2 个数恰好等于 1 才附带发送者 AI 的当前目标(0x00d1be76 的比较);载荷先写命令号、再写第 3 个数
                        IActEntity attached = p[1] == 1 ? _owner.Target : null;
                        peer.ReceiveAiMessage(new ActAiMessage(family, new[] { p[0], p[2] }, attached));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// [CHANGE AI](客户端 0x010c9719 起):只有带 AI 控制的角色(种类码 0x111)和 AICharacter 类怪物(0x211 且 cast 到 0x19db264)有 AI 宿主,
        /// 其余(普通怪物、被动对象)整个目标跳过;宿主为空也跳过。同一个组里几条按顺序各生效一次,同一槽后者覆盖前者。
        /// </summary>
        private void ChangeAi(ActMessageSpec spec, List<IActEntity> targets)
        {
            foreach (IActEntity t in targets)
            {
                if (!(t is IActAiPeer peer))
                {
                    continue;
                }
                bool eligible = t.Kind == ActEntityKind.Character ? peer.IsAiControlledCharacter : t.Kind == ActEntityKind.Monster && peer.IsAiCharacter;
                if (eligible && peer.HasAiHost)
                {
                    peer.ChangeAi(spec.AiRole, spec.AiPath, _program.Path);
                }
            }
        }

        /// <summary>[CAMOUFLAGE](客户端 0x010c68da 起):主动对象才有(被动对象 cast 失败跳过);第一个数恰好等于 1 是开,带第二个数当时长,其余都是关。</summary>
        private void Camouflage(ActMessageSpec spec, List<IActEntity> targets)
        {
            bool on = spec.CamouflageFlag == 1;
            foreach (IActEntity t in targets)
            {
                if (t.Kind != ActEntityKind.Passive && t is IActCamouflageTarget target)
                {
                    target.SetCamouflage(on, on ? spec.CamouflageMs : 0);
                }
            }
        }

        /// <summary>
        /// [APPEND HIT](客户端 0x010bdcac 起):三个值表达式各取一次(迭代器建立之前);落点 = 拥有者的位置加偏移,X 偏移朝拥有者面朝的一侧
        /// (朝向槽 0xf8 返回 0 是向左,docs/hit-pipeline.md;向右加、向左减),位置取整数(槽 0xa4/0xa8/0xac 返回整数);
        /// 对每个还活着的目标强行打一击(死目标不打是推断:applyHit 里受击方的检查在受保护区)。宿主不实现 <see cref="IActMessageHost"/> 就按没做的行为通报。
        /// </summary>
        private void AppendHit(ActBehavior b, List<IActEntity> targets)
        {
            ActMessageSpec spec = b.Msg;
            int forward = spec.HitForward.Eval(_rand);
            int depth = spec.HitDepth.Eval(_rand);
            int height = spec.HitHeight.Eval(_rand);
            if (!(_host is IActMessageHost world))
            {
                _host.NoteUnsupportedBehavior(b.Tag);
                return;
            }
            int x = (int)_owner.X + (_owner.FacingLeft ? -forward : forward);
            int y = (int)_owner.Y + depth;
            int z = (int)_owner.Z + height;
            foreach (IActEntity t in targets)
            {
                if (t.IsAlive)
                {
                    world.AppendHit(_owner, t, x, y, z);
                }
            }
        }
    }
}
