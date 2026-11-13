using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本许可表的只读快照:一串副本许可通知(类 0、包号 5)按到达顺序合并成"副本号 → 档位"的一张表,由 <see cref="DungeonPermissionParser.Collect"/> 生成。
    /// 合并规则照客户端:每一条无条件 upsert,同一副本号后到的覆盖先到的(packetread_NOTES §2.3【观】),档位保存线上原值,不做任何降级。
    /// 没有任何可写成员,也不持有帧或载荷:字段是值拷贝,<see cref="Entries"/> 是只读包装。
    /// <para>
    /// 快照只回答"服务端告诉过客户端什么":某副本号有没有条目、条目的档位是几。它<b>不</b>回答"这个副本现在能不能进":
    /// 没有条目只表示没收到过(客户端那张映射里取不到时当作 0),不表示"不许可";档位怎样换算成"允许进入的难度"、入口要不要据此把关,
    /// 是推断【推】且尚未定案,交给后续线决定(档位的取值域与推断依据见 <see cref="DungeonPermissionEntry.Tier"/>;读序与客户端落点见 packetread_NOTES §2,该笔记被 .gitignore 忽略,不入库)。
    /// 载荷里没有到期时间或有效期字段,所以快照里也没有"到期"。
    /// </para>
    /// <para>
    /// 区分三种情形(调用方拿到的是 null 还是快照):没收到过 (0,5) 帧 → <see cref="DungeonPermissionParser.Collect"/> 返回 null;
    /// 收到了但全部读不通 → 快照存在、<see cref="Count"/> 为 0、<see cref="NoticeCount"/> 为 0、<see cref="MalformedNoticeCount"/> 大于 0;
    /// 收到了个数为 0 的表 → 快照存在、<see cref="Count"/> 为 0、<see cref="NoticeCount"/> 大于 0。
    /// </para>
    /// </summary>
    public sealed class DungeonPermissionSnapshot
    {
        private readonly Dictionary<ushort, byte> _tiers;

        internal DungeonPermissionSnapshot(DungeonPermissionEntry[] entries, int noticeCount, int malformedNoticeCount)
        {
            _tiers = new Dictionary<ushort, byte>(entries.Length);
            foreach (DungeonPermissionEntry entry in entries)
            {
                _tiers[entry.DungeonId] = entry.Tier;
            }
            Entries = Array.AsReadOnly(entries);
            NoticeCount = noticeCount;
            MalformedNoticeCount = malformedNoticeCount;
        }

        /// <summary>
        /// 合并后的表,每个副本号只出现一次:按该副本号第一次出现的先后排列,档位是最后一次 upsert 的值。没有条目时是空列表,不为 null。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<DungeonPermissionEntry> Entries { get; }

        /// <summary>表里有几个副本号。</summary>
        public int Count => Entries.Count;

        /// <summary>合并进来的 (0,5) 通知个数(读得通的)。</summary>
        public int NoticeCount { get; }

        /// <summary>读不通、整包丢弃的 (0,5) 通知个数(载荷为 null、过短或个数声明超出载荷)。</summary>
        public int MalformedNoticeCount { get; }

        /// <summary>取某副本号的档位(原始字节);表里没有这个副本号返回 false。</summary>
        public bool TryGetTier(ushort dungeonId, out byte tier)
        {
            return _tiers.TryGetValue(dungeonId, out tier);
        }

        /// <summary>表里是否有这个副本号的条目。</summary>
        public bool Contains(ushort dungeonId)
        {
            return _tiers.ContainsKey(dungeonId);
        }
    }
}
