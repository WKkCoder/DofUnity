using Dnf.Game.Act;

// 证据:E-168(别的对象发来的"执行第 n 组"由收包 0x00e664a2 交给对象当前动作的实例,对象编号找不到、没有当前动作就丢)。
namespace Dnf.Play
{
    /// <summary>
    /// 被动对象收"执行第 n 组"消息:交给当前动作的运行器,下一次更新开头执行(见 <see cref="ActRunner.EnqueueRemoteGroup"/>)。
    /// 已经消失的对象、当前动作没有触发器程序(没有运行器)的对象收不下,返回 false 并计数。
    /// </summary>
    public sealed partial class PassiveObject : IActGroupReceiver
    {
        /// <summary>收下的"执行第 n 组"消息条数。</summary>
        public int ActGroupMessagesPosted { get; private set; }

        /// <summary>没收下的条数(已经消失、或当前动作没有运行器)。</summary>
        public int ActGroupMessagesDropped { get; private set; }

        public bool PostActGroup(int group)
        {
            if (Finished || _runner == null)
            {
                ActGroupMessagesDropped++;
                return false;
            }
            _runner.EnqueueRemoteGroup(group);
            ActGroupMessagesPosted++;
            return true;
        }
    }
}
