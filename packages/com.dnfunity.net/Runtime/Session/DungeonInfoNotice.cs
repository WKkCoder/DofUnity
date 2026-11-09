using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本信息通知(类 0、包号 28)明文载荷解出的只读记录,由 <see cref="DungeonInfoParser"/> 生成。
    /// 没有任何可写成员,也不持有载荷数组:字段是值拷贝,<see cref="Pairs"/> 是只读包装。
    /// <para>
    /// 命名:客户端静态读码只定下了读序与宽度。除副本编号外,其余字段客户端读了、存进副本对象,但业务含义没读通
    /// (clire_NOTES §4 未知 9),所以一律按读序中性命名(B1..B4、C1、C2、D、Pairs、F、G),不臆造业务名。
    /// 各字段在客户端里的"去向"见 clire_NOTES §2.5(该笔记被 .gitignore 忽略,不入库)。
    /// </para>
    /// </summary>
    public sealed class DungeonInfoNotice
    {
        internal DungeonInfoNotice(ushort dungeonId, byte b1, byte b2, byte b3, byte b4, byte c1, byte c2, byte d,
            DungeonInfoPair[] pairs, ushort f, byte g, int trailingByteCount)
        {
            DungeonId = dungeonId;
            B1 = b1;
            B2 = b2;
            B3 = b3;
            B4 = b4;
            C1 = c1;
            C2 = c2;
            D = d;
            Pairs = Array.AsReadOnly(pairs);
            F = f;
            G = g;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// 副本编号(u16)。客户端拿它到自带的副本表里取副本对象,查不到就在读完 B4 之后退出处理器、后面的字段都不读
        /// (clire_NOTES §2.5【观】)。这是唯一语义确定的字段。解析器没有副本表,不核对取值。
        /// </summary>
        public ushort DungeonId { get; }

        /// <summary>B1(u8,读序第 1 个单字节;语义未定)。</summary>
        public byte B1 { get; }

        /// <summary>B2(u8;语义未定)。</summary>
        public byte B2 { get; }

        /// <summary>B3(u8;语义未定)。</summary>
        public byte B3 { get; }

        /// <summary>B4(u8;语义未定)。</summary>
        public byte B4 { get; }

        /// <summary>C1(u8;语义未定)。</summary>
        public byte C1 { get; }

        /// <summary>C2(u8;语义未定)。</summary>
        public byte C2 { get; }

        /// <summary>D(u8;语义未定)。</summary>
        public byte D { get; }

        /// <summary>
        /// 一组组 {E1, E2}(客户端先读一个 u8 个数 n,再读 n 组,个数就是这个列表的长度;语义未定)。按线上顺序;没有时为空列表,不为 null。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<DungeonInfoPair> Pairs { get; }

        /// <summary>F(u16;语义未定)。</summary>
        public ushort F { get; }

        /// <summary>G(u8,读序最后一个字段;语义未定)。</summary>
        public byte G { get; }

        /// <summary>
        /// 读完 G 之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 客户端在处理器之后另读 4 字节尾值(包完整性监视表的槽 7,clire_NOTES §1.4、§2.5);真实帧里还可能有折叠哈希与会话加密补零。
        /// 所以这个数只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>副本信息通知里 n 组 {u8, u8} 的一组(读序 E1、E2;语义未定)。只读值。</summary>
    public readonly struct DungeonInfoPair
    {
        internal DungeonInfoPair(byte e1, byte e2)
        {
            E1 = e1;
            E2 = e2;
        }

        /// <summary>E1(u8,组内第 1 个字节)。</summary>
        public byte E1 { get; }

        /// <summary>E2(u8,组内第 2 个字节)。</summary>
        public byte E2 { get; }
    }
}
