using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本许可通知(类 0、包号 5)明文载荷解出的只读记录,由 <see cref="DungeonPermissionParser"/> 生成。
    /// 没有任何可写成员,也不持有载荷数组:条目是值拷贝,<see cref="Entries"/> 是只读包装。
    /// <para>
    /// 内容只有一张"逐副本的许可档位表":线上顺序的 <see cref="DungeonPermissionEntry"/>(副本号、档位)。
    /// 载荷里<b>没有</b>到期时间或有效期字段(读序只有 u16 个数与每条 u16 + u8,见 <see cref="DungeonPermissionParser"/>),所以记录里也没有。
    /// </para>
    /// <para>
    /// 条目照线上顺序、照线上个数保留(包括同一副本号出现多次的情形,不去重):客户端对每一条无条件 upsert,同键后者覆盖前者(packetread_NOTES §2.3【观】),
    /// "合并成一张表"是 <see cref="DungeonPermissionSnapshot"/> 的事,这里只忠实记录载荷。
    /// </para>
    /// </summary>
    public sealed class DungeonPermissionNotice
    {
        internal DungeonPermissionNotice(DungeonPermissionEntry[] entries, int trailingByteCount)
        {
            Entries = Array.AsReadOnly(entries);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// 条目,按线上顺序(载荷先给 u16 个数,再给这么多条,个数就是列表长度)。个数为 0 时是空列表,不为 null。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<DungeonPermissionEntry> Entries { get; }

        /// <summary>
        /// 读完最后一条之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 真实帧里尾部可能有:折叠哈希(服务端对载荷长度 1 到 0x190 追加 4 字节)、会话加密补零,
        /// 以及单条路径(<c>CParty::DungeonPermission</c>、<c>CUser::quest_reward</c>)把第三字段写成 2 字节而多出的 1 字节
        /// (客户端按 u8 读,少读这 1 字节,无害,packetread_NOTES §2.4【观】)。这些都只体现在这个数里,只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 副本许可通知里的一条:读序 u16 副本号、u8 档位(共 3 字节)。只读值,字段取值不做范围检查。
    /// </summary>
    public readonly struct DungeonPermissionEntry
    {
        internal DungeonPermissionEntry(ushort dungeonId, byte tier)
        {
            DungeonId = dungeonId;
            Tier = tier;
        }

        /// <summary>
        /// 副本号(u16)。它是客户端那张映射的键(无条件 upsert,packetread_NOTES §2.3【观】)。
        /// </summary>
        public ushort DungeonId { get; }

        /// <summary>
        /// 档位(u8)。取值域由服务端写点决定【观】:整表路径是库里存的"已通关难度档"1、2、3,英雄任务已完成的副本被覆盖成 4;
        /// 单条路径是 <c>get_dungeon_diff() + 1</c> 或固定 4。"已通关的最高难度档,1 起;第 3 档只给终极副本"是推断【推】,
        /// 实网样本里这个字节只有单一取值,检验不了多值语义【未】。解析器只给原始字节:不做范围检查,
        /// 也不复刻客户端"3 但名单里没有就降成 2"的钳制(名单在磁盘上恒空,packetread_NOTES §2.3 / §5 U2)。
        /// </summary>
        public byte Tier { get; }
    }
}
