using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本开始通知(类 0、包号 27)明文载荷解出的只读记录,由 <see cref="DungeonStartParser"/> 生成。
    /// 没有任何可写成员,也不持有载荷数组:字段是值拷贝,<see cref="Values"/>、<see cref="Members"/> 与每组的 <see cref="DungeonStartMember.Entries"/> 都是只读包装。
    /// <para>
    /// 命名:客户端静态读码只定下了读序与宽度。memberId 的用途读到了(客户端拿它去队伍里按编号查成员,clire_NOTES §1.4),
    /// 其余字段(A、u16 列表、每项里的每一个值、B、flag)客户端读了、存进装载模块或交给角色对象,但业务含义没读通(clire_NOTES §4 未知 9),
    /// 所以一律按读序中性命名,不臆造业务名。该笔记被 .gitignore 忽略,不入库。
    /// </para>
    /// </summary>
    public sealed class DungeonStartNotice
    {
        internal DungeonStartNotice(int a, ushort[] values, DungeonStartMember[] members, int b, byte flag, int trailingByteCount)
        {
            A = a;
            Values = Array.AsReadOnly(values);
            Members = Array.AsReadOnly(members);
            B = b;
            Flag = flag;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// A(i32,有符号,读序第 1 项;语义未定)。客户端把它存进 secure 全局(clire_NOTES §2.5【观】),
        /// 队伍人数不超过 1 时再把那个值清成 0。解析器只给原始值。
        /// </summary>
        public int A { get; }

        /// <summary>
        /// 一串 u16(客户端先读 u8 个数 n,再读 n 个,个数就是列表长度;语义未定)。按线上顺序;没有时为空列表,不为 null。
        /// 客户端把它们读进只够放 4 个的局部数组,解析器不做这个上限检查(见 <see cref="DungeonStartParser"/> 的说明)。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<ushort> Values { get; }

        /// <summary>
        /// 成员组,按线上顺序(客户端先读 u8 个数 cnt1,再读 cnt1 组,个数就是列表长度)。没有时为空列表,不为 null。
        /// 列表只读,规则同 <see cref="Values"/>。
        /// </summary>
        public IReadOnlyList<DungeonStartMember> Members { get; }

        /// <summary>
        /// B(i32,有符号,读序里最后一个 i32;语义未定)。客户端把它存进装载模块的一个字段(clire_NOTES §2.5【观】)。解析器只给原始值。
        /// </summary>
        public int B { get; }

        /// <summary>
        /// flag(u8,读序最后一个字段)。客户端取"是否非 0"交给装载模块的一个处理函数,另一个全局取值为 1 时改传 0(clire_NOTES §2.5【观】)。
        /// 解析器只给原始字节,不做布尔化。
        /// </summary>
        public byte Flag { get; }

        /// <summary>
        /// 读完 flag 之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 客户端读完 flag 就不再读:包完整性监视表(处理器之后校验 4 字节 PI 尾值的那张表)从 28 起,27 不在其中(clire_NOTES §2.13【观】),
        /// 所以 27 没有像 28 那样的"处理器之后另读的 4 字节尾值"。真实帧里尾部若还有字节(折叠哈希、会话加密补零,
        /// 或服务端自行追加的 4 字节完整性尾值),只会体现在这个数里。所以这个数只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 副本开始通知里的一个成员组:读序 u16 memberId、u8 cnt2、cnt2 项 <see cref="DungeonStartEntry"/>。只读。
    /// 客户端拿 memberId 去队伍里查成员;查到才读每项的 4 个字段,查不到就跳过读取、读游标却不前移,流会错位。
    /// 那是客户端行为,解析器不模拟:不查队伍、不看 memberId 的取值,总是按结构把每项读完(依据与后果见 <see cref="DungeonStartParser"/> 的说明)。
    /// </summary>
    public sealed class DungeonStartMember
    {
        internal DungeonStartMember(ushort memberId, DungeonStartEntry[] entries)
        {
            MemberId = memberId;
            Entries = Array.AsReadOnly(entries);
        }

        /// <summary>memberId(u16,组内第 1 项)。客户端拿它去队伍里按编号查成员(clire_NOTES §1.4【观】)。解析器不查队伍,不核对取值。</summary>
        public ushort MemberId { get; }

        /// <summary>
        /// 该成员的各项,按线上顺序(客户端先读 u8 个数 cnt2,再读 cnt2 项,个数就是列表长度)。没有时为空列表,不为 null。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<DungeonStartEntry> Entries { get; }
    }

    /// <summary>
    /// 副本开始通知里一个成员组的一项:读序 u16 A、i32 B、i32 C、i32 D(共 14 字节)。只读值。语义未定,按读序中性命名。
    /// </summary>
    public readonly struct DungeonStartEntry
    {
        internal DungeonStartEntry(ushort a, int b, int c, int d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        /// <summary>A(u16,项内第 1 个值)。</summary>
        public ushort A { get; }

        /// <summary>B(i32,有符号)。</summary>
        public int B { get; }

        /// <summary>C(i32,有符号)。</summary>
        public int C { get; }

        /// <summary>D(i32,有符号,项内最后一个值)。</summary>
        public int D { get; }
    }
}
