using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本许可通知(类 0、包号 <see cref="PacketId"/>)的明文载荷解析器:把静态读码定案的读序落成只读记录 <see cref="DungeonPermissionNotice"/>,
    /// 并能把一串帧里的这类通知合并成 <see cref="DungeonPermissionSnapshot"/>。包名表里它叫 DUNGEON_PERMISSION(台账 E-382)。
    /// 载荷是一张"逐副本的许可档位表",<b>不含</b>到期时间或有效期字段,也不是背包。
    /// <para>
    /// 范围:纯明文解析。进城后这个包在探针的明文流里被封在 561 信封内,信封内层读通之后(会话层把内层帧紧跟在信封帧后面交付),
    /// 把内层帧的载荷直接喂给 <see cref="TryParse"/>;解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封。
    /// <see cref="Collect"/> 才看帧头:只认类 0、包号 <see cref="PacketId"/> 的帧。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="DungeonStartParser"/>、<see cref="TownEntryParser"/> 同风格):短包、读越界一律返回 false 且 notice 为 null,绝不抛异常(包括 null 载荷);
    /// 读完最后一条即停,容忍尾部多余字节(只在 <see cref="DungeonPermissionNotice.TrailingByteCount"/> 报告个数,不核对,不要求"读完恰好等于载荷末尾");
    /// 字段取值不做范围检查。
    /// </para>
    /// <para>
    /// 读序(小端;packetread_NOTES §2.2 的【观】,客户端处理器 0x72ced0..0x72cfd0,个数读点 0x72cee0,副本号读点 0x72cf04,档位读点 0x72cf0f;
    /// 该笔记被 .gitignore 忽略,不入库):<c>u16 count, count × { u16 副本号, u8 档位 }</c>,总长 = 2 + 3 × count,没有任何尾部变长段。
    /// 客户端对 count 为 0 直接返回,对每一条无条件 upsert,处理器之后不再读任何字节,也没有"读完"检查。
    /// </para>
    /// <para>
    /// 尾巴(都不消费、不核对):整表路径(<c>CUser::SendDungeonClearList</c>,0x86781b4)逐字节吻合,只可能有折叠哈希(载荷长度 1 到 0x190 时追加 4 字节)与会话加密补零;
    /// 单条路径(<c>CParty::DungeonPermission</c>、<c>CUser::quest_reward</c>)服务端把第三字段写成 2 字节而客户端按 u8 读,所以单条载荷多 1 字节再加折叠哈希,
    /// 取值不超过 255 时小端低字节在前,读到的档位就是该值(packetread_NOTES §2.4【观】/【推】)。
    /// 也因此"载荷长度恰好等于 2 + 3 × count"不是合法性条件;键升序是服务端 <c>std::map</c> 遍历的性质,同样不是合法性条件。
    /// </para>
    /// <para>
    /// 【客户端行为,解析器都不模拟】(1)"档位 3 但该副本不在\"有第 3 档的副本\"名单里就降成 2":名单在磁盘上的明文代码里恒为空,
    /// 客户端因此恒降,这是原版的行为而不是协议的含义,解析器保存线上原值(packetread_NOTES §2.3、§5 U2);
    /// (2)档位变大时客户端弹"新解锁难度"提示、读原语越界时只记日志并继续读垃圾:解析器不做 UI,越界一律整包丢弃。
    /// 所以"解析成功"只表示布局读得下来,不表示客户端会按同一个字节边界消费这个包。
    /// </para>
    /// <para>
    /// 【到期】任务背景里"许可清单与有效期"的说法与读序对不上:这个包里没有到期字段(读序只有副本号与档位,台账 E-382 也只写"逐副本的许可档位表")。
    /// 带"剩余秒"的是选角成功回包里的特权列表(见 <see cref="SelectCharacterLoadInfo.Premiums"/>),与副本许可无关。
    /// 这里不编造到期语义,也就没有"恰好到期算哪边"这类边界。
    /// </para>
    /// </summary>
    public static class DungeonPermissionParser
    {
        /// <summary>副本许可通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 5;

        // 每条 3 字节:u16 副本号 + u8 档位。
        private const int EntryLength = 3;

        /// <summary>
        /// 解析一份载荷(从第一个字段起)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>读完全部条目为 true;载荷为 null、过短或个数声明超出载荷为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out DungeonPermissionNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort count))
            {
                return false;
            }

            // 个数声明超出载荷时直接失败,不先按声明分配(u16 个数最多 65535 条,几个字节的坏包不该换来一大块内存)。
            if ((long)count * EntryLength > reader.Remaining)
            {
                return false;
            }

            var entries = new DungeonPermissionEntry[count];
            for (int i = 0; i < entries.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort dungeonId) || !reader.TryGetByte(out byte tier))
                {
                    return false;
                }
                entries[i] = new DungeonPermissionEntry(dungeonId, tier);
            }

            notice = new DungeonPermissionNotice(entries, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 把一串帧里的副本许可通知(类 0、包号 <see cref="PacketId"/>)按到达顺序合并成快照:每一条 upsert,同一副本号后到的覆盖先到的。
        /// 读不通的通知整包丢弃并计数,不影响别的通知;别的帧一律跳过。
        /// </summary>
        /// <param name="frames">按到达顺序排列的解码帧;可以为 null 或空,元素可以为 null(跳过)。</param>
        /// <returns>没有任何 (0,5) 帧时为 null;否则是快照(即使所有通知都读不通)。不抛异常。</returns>
        public static DungeonPermissionSnapshot Collect(IReadOnlyList<DecodedFrame> frames)
        {
            if (frames == null)
            {
                return null;
            }

            var order = new List<ushort>();
            var tiers = new Dictionary<ushort, byte>();
            bool seen = false;
            int notices = 0;
            int malformed = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                DecodedFrame frame = frames[i];
                if (frame == null || frame.Header.Class != PacketClass.Noti || frame.Header.PacketId != PacketId)
                {
                    continue;
                }
                seen = true;
                if (!TryParse(frame.Payload, out DungeonPermissionNotice notice))
                {
                    malformed++;
                    continue;
                }
                notices++;
                foreach (DungeonPermissionEntry entry in notice.Entries)
                {
                    if (!tiers.ContainsKey(entry.DungeonId))
                    {
                        order.Add(entry.DungeonId);
                    }
                    tiers[entry.DungeonId] = entry.Tier;
                }
            }

            if (!seen)
            {
                return null;
            }

            var merged = new DungeonPermissionEntry[order.Count];
            for (int i = 0; i < merged.Length; i++)
            {
                merged[i] = new DungeonPermissionEntry(order[i], tiers[order[i]]);
            }
            return new DungeonPermissionSnapshot(merged, notices, malformed);
        }
    }
}
