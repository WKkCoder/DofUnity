using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 备用装备组:一个组号加该组的详细装备记录(只含线上槽 10 到 21,即 12 个装备部位,不含装扮部位 0..9 与槽 22..25)。
    /// 组号是线上 u8 原值:服务端 g = 1 且当前通道的"装备栏切换"为 1 时写 0,否则写 g(0x0849a66e..0x0849a69f);客户端要求组号小于 4 才切换。
    /// </summary>
    public sealed class ExpandEquipGroup
    {
        private readonly IReadOnlyList<ItemEntry> _entries;

        internal ExpandEquipGroup(byte groupId, ItemEntry[] entries)
        {
            GroupId = groupId;
            _entries = Array.AsReadOnly(entries ?? new ItemEntry[0]);
        }

        /// <summary>组号(线上 u8 原值,不解释)。</summary>
        public byte GroupId { get; }

        /// <summary>该组的装备记录(槽 10 到 21)。</summary>
        public IReadOnlyList<ItemEntry> Entries => _entries;
    }

    /// <summary>
    /// 备用装备组包(类 0、包号 339,服务端 <c>CExpandEquipslot::Send_Expand_Equip_Info</c> 0x0849a57c)的解析结果:
    /// <c>u16 唯一号 + 3 × (u8 组号 + 详细装备列表)</c>。
    /// <b>是不是本人的包由消费方判断</b>:客户端只在唯一号等于本机角色时才处理(0x00eab50b),解析器只把唯一号交出去,不过滤。
    /// 不可变;构造入口只给 <see cref="ExpandEquipParser"/>。
    /// </summary>
    public sealed class ExpandEquipNotice
    {
        private readonly IReadOnlyList<ExpandEquipGroup> _groups;

        internal ExpandEquipNotice(ushort uid, ExpandEquipGroup[] groups, int consumedByteCount, int trailingByteCount)
        {
            Uid = uid;
            _groups = Array.AsReadOnly(groups ?? new ExpandEquipGroup[0]);
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>包头的 u16 唯一号(服务端 <c>get_unique_id</c>;与同一角色的用户信息条目里的 uid 是同一个数,样本逐条相等)。</summary>
        public ushort Uid { get; }

        /// <summary>三个备用装备组,按线上顺序(恒为 3 个)。</summary>
        public IReadOnlyList<ExpandEquipGroup> Groups => _groups;

        /// <summary>三个组是否全部为空表(本机抓帧里就是这样)。</summary>
        public bool AllGroupsEmpty
        {
            get
            {
                foreach (ExpandEquipGroup group in _groups)
                {
                    if (group.Entries.Count != 0)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>解析消耗的字节数。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>最后一个组之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }
}
