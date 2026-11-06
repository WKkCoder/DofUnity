using System;
using Dnf.Game.Act;

// 证据:E-168(别的对象发来的"执行第 n 组"在客户端是写包、收包再找动作实例执行,0x010d1370 → 0x00e664a2)、E-056(AI 消息包 0x34/2)。
// 客户端静态读码(非 REA):[SEND DO BEHAVIOR] 块 0x010b9483、[SEND MESSAGE] 块 0x010bb716、[CAMOUFLAGE] 块 0x010c68da、伪装设置 0x008267f0。
namespace Dnf.Play
{
    /// <summary>
    /// 消息族行为落到怪物身上的部分。
    /// <para>
    /// 观察:"执行第 n 组"交给目标当前动作的实例(出招时是这一招的运行器,其余时候是当前动作族的运行器);AI 消息与 [CHANGE AI] 只有 AICharacter 类的怪物才接,
    /// [CAMOUFLAGE] 是主动对象的虚槽 0x678,里面建了一个附着的效果对象。
    /// </para>
    /// <para>
    /// 观察:AICharacter 由怪物工厂 0x00d38910 按怪物编号造出——分派树的默认分支(0x00d42f8e)对一部分编号造它,其余编号走各自专用的类;
    /// 全库里自己的出招带这些行为的怪物都是 AICharacter 系(与"其他命令只认 AICharacter 发送者"吻合)。
    /// 未知(不据此改行为):移植里没有从怪物编号推出"是不是 AICharacter"的规则(要走完整棵分派树,个别编号的构造函数还在受保护区),
    /// 所以不把任何怪物当 AICharacter,[SEND MESSAGE]、[CHANGE AI] 到了怪物身上只计数、不改 AI;普通怪物对 0x34 包的处理在受保护区,同样不做。
    /// [CAMOUFLAGE] 只记状态与时限(效果对象的画面没读通):时限为 0 的开当作不限时(推断,没有读取者)。
    /// </para>
    /// </summary>
    public sealed partial class MonsterActor
    {
        private bool _camouflaged;
        private double _camouflageLeftMs;

        /// <summary>别的对象发来的"执行第 n 组"消息成功交给了运行器的条数。</summary>
        public int ActGroupMessagesPosted { get; private set; }

        /// <summary>此刻没有可接的运行器(不在出招、当前动作族没有触发器程序、已经消失)而没送出去的"执行第 n 组"消息条数。</summary>
        public int ActGroupMessagesDropped { get; private set; }

        /// <summary>送到这只怪物 AI 上的消息条数(按 <see cref="ActMessageFamily"/> 的数值下标分:0 号位不用)。</summary>
        public int[] AiMessagesByFamily { get; } = new int[6];

        /// <summary>送到这只怪物 AI 上的消息总数(不改变 AI,只计数)。</summary>
        public int AiMessagesReceived { get; private set; }

        /// <summary>现在是伪装着的(脚本 [CAMOUFLAGE] 开了、时限没到、没被关)。</summary>
        public bool Camouflaged => _camouflaged;

        /// <summary>伪装还剩多少毫秒;0 是没有伪装或不限时。</summary>
        public double CamouflageLeftMs => _camouflageLeftMs;

        /// <summary>脚本开过伪装的次数(重复开也算)。</summary>
        public int CamouflagesStarted { get; private set; }

        /// <summary>伪装到时自己解除的次数(脚本关的不算)。</summary>
        public int CamouflagesExpired { get; private set; }

        /// <summary>
        /// 把"执行第 <paramref name="group"/> 组"交给这只怪物当前动作的运行器(下一次更新开头执行,见 <see cref="ActRunner.EnqueueRemoteGroup"/>)。
        /// 出招阶段只认这一招的运行器,不退到别的族上(出招时当前动作就是这一招);其余阶段取正在当前的族。
        /// </summary>
        private bool PostActGroup(int group)
        {
            ActRunner runner = Phase == MonsterPhase.Attack ? _actRunner : _famActive?.Runner;
            if (runner == null)
            {
                ActGroupMessagesDropped++;
                return false;
            }
            runner.EnqueueRemoteGroup(group);
            ActGroupMessagesPosted++;
            return true;
        }

        private void NoteAiMessage(ActAiMessage message)
        {
            AiMessagesReceived++;
            int family = (int)message.Family;
            if ((uint)family < (uint)AiMessagesByFamily.Length)
            {
                AiMessagesByFamily[family]++;
            }
        }

        /// <summary>
        /// 脚本的 [CAMOUFLAGE]:<paramref name="on"/> 为真开伪装并以 <paramref name="durationMs"/> 重新计时(再开一次就重新计,推断);为假立刻解除。
        /// </summary>
        private void ActSetCamouflage(bool on, int durationMs)
        {
            if (!on)
            {
                _camouflaged = false;
                _camouflageLeftMs = 0;
                return;
            }
            _camouflaged = true;
            _camouflageLeftMs = durationMs > 0 ? durationMs : 0;
            CamouflagesStarted++;
        }

        /// <summary>过了 <paramref name="deltaMs"/> 毫秒(墙钟,命中停顿里不冻结,推断):有时限的伪装倒计时,到点解除。</summary>
        private void AdvanceCamouflage(double deltaMs)
        {
            if (!_camouflaged || _camouflageLeftMs <= 0)
            {
                return;
            }
            _camouflageLeftMs -= deltaMs;
            if (_camouflageLeftMs <= 0)
            {
                _camouflageLeftMs = 0;
                _camouflaged = false;
                CamouflagesExpired++;
            }
        }

        private sealed partial class ActBody : IActGroupReceiver, IActAiPeer, IActCamouflageTarget
        {
            public bool PostActGroup(int group) => _m.PostActGroup(group);

            /// <summary>怪物有 AI(.ai 脚本或默认走位大脑)就算有 AI 从属;没有 AI 的怪物(固定摆件之类)不接 AI 消息(推断:虚槽 0x1b0 的从属只在装了 AI 时才有)。</summary>
            public bool HasAiSlave => _m._script != null || _m._brain != null;

            /// <summary>见类说明:没有判定怪物是不是 AICharacter 类的规则,不把任何怪物当 AI 宿主,[CHANGE AI] 因此不会落到怪物上。</summary>
            public bool HasAiHost => false;

            public bool IsAiCharacter => false;

            public bool IsAiControlledCharacter => false;

            public void ReceiveAiMessage(ActAiMessage message) => _m.NoteAiMessage(message);

            public void ChangeAi(int role, string path, string fromProgram)
            {
                // 不会被调到:HasAiHost 恒假(移植没有判定怪物是不是 AICharacter 类的规则)
            }

            public void SetCamouflage(bool on, int durationMs) => _m.ActSetCamouflage(on, durationMs);
        }
    }
}
