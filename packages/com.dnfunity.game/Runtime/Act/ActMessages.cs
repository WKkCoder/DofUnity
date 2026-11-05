using System;
using System.Collections.Generic;

// 证据:行为执行器 0x010b56c0 里 0x71([SEND DO BEHAVIOR],块 0x010b9483)、0x79([SEND MESSAGE],块 0x010bb716)、0xec([CHANGE AI],块 0x010c9719)、
// 0xae([CAMOUFLAGE],块 0x010c68da)、0x82([APPEND HIT],块 0x010bdcac)的静态读码(非 REA 自动化);"执行第 n 组"的发送函数 0x010d1370 与收包 0x00e664a2(E-168 起已读,
// 包类型表 E-056)、AI 消息的发送函数 0x00d1bdc0 与 AICharacter 的接收函数 0x00d1ced0、加载块 0x0133ebb0(消息五族)与 0x0133714c(换 AI)、命中应用 0x00875de0(E-057)。
namespace Dnf.Game.Act
{
    /// <summary>
    /// [SEND MESSAGE] 的五族命令(加载块 0x0133ebb0 起按子关键字逐个比字符串,族码 1 到 5 写进记录的第 0 个整数)。
    /// 没写子关键字或不认识的,记录没有任何参数(0x0133edd1 直接入表),运行时什么都不发。
    /// </summary>
    public enum ActMessageFamily : byte
    {
        None = 0,

        /// <summary>[ATTACK_COMMAND] 数:让对方立刻按这个数出招(接收端 AICharacter:本机控制且空闲才进入出招状态)。</summary>
        Attack = 1,

        /// <summary>[MOVE_COMMAND] 四个数:发送侧把四个整数原样写进包;AICharacter 的接收函数跳表里这一族直接返回,什么都不做。</summary>
        Move = 2,

        /// <summary>[ETC_COMMAND] 命令号 开关 参数:设给对方 AI 宿主的"命令"寄存器(.ai 的 [command] 条件读它);只有发送者是 AICharacter 才发。</summary>
        Etc = 3,

        /// <summary>[RISE_COMMAND](没有参数):接收端置位起身标志。</summary>
        Rise = 4,

        /// <summary>[DIE_COMMAND] 数:接收端数值为 0 走一条路径、否则走另一条路径把自己弄死。</summary>
        Die = 5,
    }

    /// <summary>送到一个对象 AI 上的一条消息(包类型 0x34 的内容)。</summary>
    public readonly struct ActAiMessage
    {
        public ActAiMessage(ActMessageFamily family, int[] values, IActEntity commandTarget)
        {
            Family = family;
            Values = values ?? Array.Empty<int>();
            CommandTarget = commandTarget;
        }

        public ActMessageFamily Family { get; }

        /// <summary>
        /// 按族:Attack 与 Die 是 [数值];Move 是四个整数;Etc 是 [命令号, 参数](参数是记录里的第三个数,第二个数是"带目标"开关,不在这里);Rise 为空。
        /// </summary>
        public IReadOnlyList<int> Values { get; }

        /// <summary>只有 Etc 带:发送者此刻 AI 的目标(开关等于 1 且发送者有目标才有);没有是 null。</summary>
        public IActEntity CommandTarget { get; }
    }

    /// <summary>
    /// [SEND DO BEHAVIOR] 的收件口(E-168 的发送函数 0x010d1370 把"第 n 组、目标列表"写进包,收包按对象编号找到对象再找动作执行)。
    /// 对象自己的当前动作运行器收一条"执行第 n 组、目标列表只含对象自己"的消息;对象此刻没有动作运行器就返回 false。
    /// 实现者只负责把消息交给当前运行器的 <see cref="ActRunner.EnqueueRemoteGroup"/>,不要立刻执行。
    /// </summary>
    public interface IActGroupReceiver
    {
        bool PostActGroup(int group);
    }

    /// <summary>
    /// 对象在 AI 一侧的能力(客户端虚槽 0x1b0 取 AI 从属、AICharacter 类与 AI 宿主 [obj+0x50f4]);实现者决定怎么落地,
    /// 没有这些能力的对象不实现它,运行器当作"不是 AI 对象"跳过。
    /// </summary>
    public interface IActAiPeer
    {
        /// <summary>虚槽 0x1b0 返回非空(主动对象的 AI 从属 [this+0x718],0x00818a30)。[SEND MESSAGE] 的五族都要求它。</summary>
        bool HasAiSlave { get; }

        /// <summary>AI 宿主存在(AICharacter 的 [obj+0x50f4],带 AI 控制的角色的 [obj+0x58bc])。[CHANGE AI] 要求它。</summary>
        bool HasAiHost { get; }

        /// <summary>对象是 AICharacter 类(类 0x19db264,怪物的子类,种类码仍是 0x211)。</summary>
        bool IsAiCharacter { get; }

        /// <summary>对象是带 AI 控制的角色(类 0x19d9938 且虚槽 0x38 为真)。</summary>
        bool IsAiControlledCharacter { get; }

        /// <summary>收到一条 AI 消息(接收函数是对象虚槽 0x218;AICharacter 的实现 0x00d1ced0 已读,普通怪物的实现在受保护区)。</summary>
        void ReceiveAiMessage(ActAiMessage message);

        /// <summary>
        /// [CHANGE AI]:把 AI 宿主上第 <paramref name="role"/> 号角色槽换成 <paramref name="path"/> 这份 .ai(客户端 0x008e4490 setAi;路径是脚本里的原样写法,
        /// 相对写这条行为的程序 <paramref name="fromProgram"/> 所在的目录,由对象自己解析)。
        /// </summary>
        void ChangeAi(int role, string path, string fromProgram);
    }

    /// <summary>[CAMOUFLAGE] 的落点:主动对象(类 0x19d98fc)的虚槽 0x678(0x008267f0)。</summary>
    public interface IActCamouflageTarget
    {
        /// <summary><paramref name="on"/> 为真:伪装 <paramref name="durationMs"/> 毫秒;为假:立刻解除(durationMs 为 0)。</summary>
        void SetCamouflage(bool on, int durationMs);
    }

    /// <summary>宿主在消息族之外要提供的世界能力(运行器按 is 判断,不实现就当没有)。</summary>
    public interface IActMessageHost
    {
        /// <summary>
        /// [APPEND HIT](0x00878f50 → applyHit 0x00875de0):<paramref name="owner"/> 对 <paramref name="target"/> 强行打一击,落点 (x, y, z) 已是世界坐标。
        /// 不经过命中判定的几何、已命中登记、最大目标数与队伍这几道门;伤害按拥有者当前的攻击信息结算。
        /// </summary>
        void AppendHit(IActEntity owner, IActEntity target, float x, float y, float z);
    }

    /// <summary>一条消息族行为的参数(挂在 <see cref="ActBehavior.Msg"/> 上,按 <see cref="ActBehavior.Kind"/> 取用)。</summary>
    public sealed class ActMessageSpec
    {
        /// <summary>[SEND DO BEHAVIOR] 的组号(值表达式,每次执行取一次,在目标循环之前)。</summary>
        public ActValue GroupValue { get; internal set; }

        /// <summary>[SEND MESSAGE] 的族;没写或不认识是 <see cref="ActMessageFamily.None"/>。</summary>
        public ActMessageFamily Family { get; internal set; }

        /// <summary>[SEND MESSAGE] 子关键字后面的整数,按文件顺序(Attack 1 个、Move 4 个、Etc 3 个、Rise 0 个、Die 1 个)。</summary>
        public int[] Params { get; internal set; } = Array.Empty<int>();

        /// <summary>[CHANGE AI] 的角色槽号。</summary>
        public int AiRole { get; internal set; }

        /// <summary>[CHANGE AI] 的 .ai 路径(脚本原样写法);没写是空串。</summary>
        public string AiPath { get; internal set; } = string.Empty;

        /// <summary>[CAMOUFLAGE] 的第一个整数:恰好等于 1 才是开,其余都是关。</summary>
        public int CamouflageFlag { get; internal set; }

        /// <summary>[CAMOUFLAGE] 的第二个整数:开的时长,毫秒。</summary>
        public int CamouflageMs { get; internal set; }

        /// <summary>[APPEND HIT] 的前移、纵深偏移、高度偏移(值表达式,每次执行各取一次,在目标循环之前)。</summary>
        public ActValue HitForward { get; internal set; }

        public ActValue HitDepth { get; internal set; }

        public ActValue HitHeight { get; internal set; }
    }

    /// <summary>消息族五个行为的编译(接入点在 <c>ActCompiler.ParseBehavior</c>)。</summary>
    internal static class ActMessageCompiler
    {
        public static ActBehavior Parse(ActCursor cur, string tag, Func<string, bool> isBehaviorHead)
        {
            switch (tag)
            {
                case "[SEND DO BEHAVIOR]":
                    return SendDoBehavior(cur, tag, isBehaviorHead);
                case "[SEND MESSAGE]":
                    return SendMessage(cur, tag, isBehaviorHead);
                case "[CHANGE AI]":
                    return ChangeAi(cur, tag, isBehaviorHead);
                case "[CAMOUFLAGE]":
                    return Camouflage(cur, tag);
                case "[APPEND HIT]":
                    return AppendHit(cur, tag);
                default:
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
        }

        private static ActBehavior Unsupported(string tag) => new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };

        /// <summary>
        /// [SEND DO BEHAVIOR] 选择器标记 组号:客户端执行块先读掉记录第 0 个整数(选择器)、游标 +1,再从第 1 个起读值表达式(0x010b9492 起);
        /// 选择器不参与执行,所以没写选择器也读得进。组号读不到的整条不做。
        /// </summary>
        private static ActBehavior SendDoBehavior(ActCursor cur, string tag, Func<string, bool> isBehaviorHead)
        {
            if (cur.AtOpen(out string selector) && selector != "[RANDOM]" && selector != "[RANDOM SELECT]" && !isBehaviorHead(selector))
            {
                cur.Skip();
            }
            ActValue group = cur.Value();
            if (group == null)
            {
                return Unsupported(tag);
            }
            return new ActBehavior { Kind = ActBehaviorKind.SendDoBehavior, Tag = tag, Msg = new ActMessageSpec { GroupValue = group } };
        }

        private static ActMessageFamily FamilyOf(string keyword)
        {
            switch (keyword)
            {
                case "[ATTACK_COMMAND]":
                    return ActMessageFamily.Attack;
                case "[MOVE_COMMAND]":
                    return ActMessageFamily.Move;
                case "[ETC_COMMAND]":
                    return ActMessageFamily.Etc;
                case "[RISE_COMMAND]":
                    return ActMessageFamily.Rise;
                case "[DIE_COMMAND]":
                    return ActMessageFamily.Die;
                default:
                    return ActMessageFamily.None;
            }
        }

        /// <summary>加载块逐族读的整数个数(0x133ec11 一个、0x133ec63 起四个、0x133ecf4 起三个、起身一个都不读、0x133edb1 一个)。</summary>
        private static int ParamCount(ActMessageFamily family)
        {
            switch (family)
            {
                case ActMessageFamily.Attack:
                    return 1;
                case ActMessageFamily.Move:
                    return 4;
                case ActMessageFamily.Etc:
                    return 3;
                case ActMessageFamily.Die:
                    return 1;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// [SEND MESSAGE] 子关键字 整数…:子关键字是五个字符串之一(客户端按字符串比较,不是字典标签;[ETC_COMMAND] 因此不会单独成一条行为);
        /// 读完这一族要的整数,后面多写的数不读(全库有 6 处 [ATTACK_COMMAND] 写了三个数);子关键字缺失或不认识,记录不带任何参数。
        /// </summary>
        private static ActBehavior SendMessage(ActCursor cur, string tag, Func<string, bool> isBehaviorHead)
        {
            ActMessageFamily family = ActMessageFamily.None;
            if (cur.AtOpen(out string keyword))
            {
                family = FamilyOf(keyword);
                if (family != ActMessageFamily.None || !isBehaviorHead(keyword))
                {
                    cur.Skip();      // 不认识的子关键字也被扫描函数吃掉(0x133ebce),只是没有参数
                }
            }
            var values = new int[ParamCount(family)];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = cur.Int(0);
            }
            return new ActBehavior
            {
                Kind = ActBehaviorKind.SendMessage,
                Tag = tag,
                Msg = new ActMessageSpec { Family = family, Params = values },
            };
        }

        /// <summary>
        /// [CHANGE AI] 难度关键字 角色槽号 "路径":加载块 0x0133714c 扫一个关键字(难度名,文本随后被读路径的缓冲覆盖,不参与比较)、读一个整数、读一个字符串。
        /// </summary>
        private static ActBehavior ChangeAi(ActCursor cur, string tag, Func<string, bool> isBehaviorHead)
        {
            if (cur.AtOpen(out string keyword) && !isBehaviorHead(keyword))
            {
                cur.Skip();
            }
            int role = cur.Int(0);
            string path = cur.TakeString() ?? string.Empty;
            return new ActBehavior
            {
                Kind = ActBehaviorKind.ChangeAi,
                Tag = tag,
                Msg = new ActMessageSpec { AiRole = role, AiPath = path },
            };
        }

        /// <summary>[CAMOUFLAGE] 开关 毫秒:两个整数(加载块 act-tags 登记 2 个整数);读不到按 0 记(0 不等于 1,等于"关")。</summary>
        private static ActBehavior Camouflage(ActCursor cur, string tag)
        {
            int flag = cur.Int(0);
            int ms = cur.Int(0);
            return new ActBehavior
            {
                Kind = ActBehaviorKind.Camouflage,
                Tag = tag,
                Msg = new ActMessageSpec { CamouflageFlag = flag, CamouflageMs = ms },
            };
        }

        /// <summary>[APPEND HIT] 前移 纵深 高度:三个值表达式(执行块用值表达式读取函数 0x010a3310 读它们);第一个读不到整条不做,后两个缺了按 0。</summary>
        private static ActBehavior AppendHit(ActCursor cur, string tag)
        {
            ActValue forward = cur.Value();
            if (forward == null)
            {
                return Unsupported(tag);
            }
            ActValue depth = cur.Value() ?? ActValue.Literal(0);
            ActValue height = cur.Value() ?? ActValue.Literal(0);
            return new ActBehavior
            {
                Kind = ActBehaviorKind.AppendHit,
                Tag = tag,
                Msg = new ActMessageSpec { HitForward = forward, HitDepth = depth, HitHeight = height },
            };
        }
    }
}
