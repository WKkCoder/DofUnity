using System;
using System.Collections.Generic;

namespace Dnf.Play
{
    /// <summary>联网名册里一个角色为什么不能进入游戏(能进入时是 <see cref="None"/>)。</summary>
    public enum CharacterBlock
    {
        /// <summary>可以进入。</summary>
        None = 0,

        /// <summary>
        /// 待删除:列表条目末尾的剩余天数(<see cref="OnlineCharacterEntry.D2"/>)不为 0。客户端点"游戏开始"的入口先过这一关,真就不往下走【观,charre 证据 03 的 0x10fe811】;
        /// 它接着弹什么(恢复角色的确认?)没读到,这里只拦住并提示。
        /// </summary>
        PendingDelete = 1,

        /// <summary>
        /// 名字标记(<see cref="OnlineCharacterEntry.X1"/> 等于 1):客户端只弹提示、不发选角命令【观】;服务端写的是名字特殊字符检查的结果【观】,
        /// "1 表示名字有特殊字符"是推断。
        /// </summary>
        NameFlag = 2,

        /// <summary>状态字节(<see cref="OnlineCharacterEntry.Status"/>)等于 8:同样只弹提示、不发选角命令【观】;含义没读到。</summary>
        Status = 3,
    }

    /// <summary>
    /// 联网名册(<see cref="OnlineCharacterRoster"/>)里一个角色比离线占位多出来的信息:它在服务端列表里的位置、拆开的职业与成长类型、
    /// 能不能进入,以及服务端给的原始条目(装备记录、<see cref="OnlineCharacterEntry.Uid"/> 等字段一个不丢,供形象组装与以后的进城接线取用)。
    /// 只读。
    /// </summary>
    public sealed class CharacterSlotOnline
    {
        public CharacterSlotOnline(int slot, OnlineCharacterEntry entry, int job, int growType, int awakening, CharacterBlock block)
        {
            if (slot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), "槽号不能是负数");
            }
            Slot = slot;
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Job = job;
            GrowType = growType;
            Awakening = awakening;
            Block = block;
        }

        /// <summary>
        /// 选角命令用的槽号:这个角色在服务端列表里的位置(从 0 起),也是它在名册里的序号。
        /// <b>不是</b> <see cref="OnlineCharacterEntry.Uid"/>(那是服务端的"运行序号",客户端回传的是列表位置【观】)。
        /// </summary>
        public int Slot { get; }

        /// <summary>服务端给的原始条目(只读)。名字字节在里面,日志与诊断输出里不要带它。</summary>
        public OnlineCharacterEntry Entry { get; }

        /// <summary>职业号(<see cref="OnlineCharacterEntry.Job"/> 原样)。</summary>
        public int Job { get; }

        /// <summary>一转(第一成长类型,0 到 15):<see cref="OnlineCharacterEntry.GrowTypeByte"/> 的低 4 位;0 是没转职的基础职业。</summary>
        public int GrowType { get; }

        /// <summary>二转/觉醒阶段(0 到 7,实际只见 0 到 2):<see cref="OnlineCharacterEntry.GrowTypeByte"/> 的 bit4 到 bit6;0 是没觉醒。</summary>
        public int Awakening { get; }

        /// <summary>不能进入的原因;能进入是 <see cref="CharacterBlock.None"/>。</summary>
        public CharacterBlock Block { get; }

        /// <summary>能不能用这个角色开始游戏。</summary>
        public bool CanEnter => Block == CharacterBlock.None;

        /// <summary>服务端给的装备记录(列表里这个角色的 <see cref="OnlineCharacterEntry.Equips"/>,原样)。</summary>
        public IReadOnlyList<OnlineCharacterEquip> Equips => Entry.Equips;
    }
}
