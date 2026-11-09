using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 起始地图通知(类 0、包号 29)明文载荷解出的只读记录,由 <see cref="StartMapParser"/> 生成。
    /// 它的字段决定起始房间:格子坐标、种子、地图类型与编号、怪记录、物品记录。
    /// 没有任何可写成员,也不持有载荷数组:字段是值拷贝,三个列表都是只读包装。
    /// <para>
    /// 命名:客户端静态读码只定下了读序与宽度。格 X/Y、种子、地图类型与编号、playerIndex 的用途读到了(clire_NOTES §2.6),
    /// 其余字段(flagA、全局 1/2/3、各条记录里的每一项)业务含义没读通,一律按读序中性命名,不臆造业务名。
    /// 该笔记被 .gitignore 忽略,不入库。
    /// </para>
    /// <para>
    /// "只在 mapType 为 1 或 2 时才有"的部分(<see cref="MapId"/>、<see cref="Monsters"/>、<see cref="Items"/>、<see cref="Global3"/>):
    /// mapType 为其它值时客户端根本不读它们,这里 <see cref="MapId"/> 与 <see cref="Global3"/> 为 null,两个列表为空。
    /// </para>
    /// </summary>
    public sealed class StartMapNotice
    {
        internal StartMapNotice(byte cellX, byte cellY, byte flagA, int seed, byte global1, byte global2, byte mapType,
            ushort? mapId, StartMapMonsterRecord[] monsters, StartMapItemRecord[] items, byte? global3,
            StartMapQuadRecord[] quads, byte playerIndex, int trailingByteCount)
        {
            CellX = cellX;
            CellY = cellY;
            FlagA = flagA;
            Seed = seed;
            Global1 = global1;
            Global2 = global2;
            MapType = mapType;
            MapId = mapId;
            Monsters = Array.AsReadOnly(monsters);
            Items = Array.AsReadOnly(items);
            Global3 = global3;
            Quads = Array.AsReadOnly(quads);
            PlayerIndex = playerIndex;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>起点格 X(u8)。客户端存成当前格 X,并连同 Y 交给装载模块(clire_NOTES §2.6【观】)。</summary>
        public byte CellX { get; }

        /// <summary>起点格 Y(u8)。</summary>
        public byte CellY { get; }

        /// <summary>
        /// flagA(u8;语义未定)。客户端据它分两条路:0 走"新建房间槽",非 0 走"取已有槽"(clire_NOTES §2.6【推】);
        /// 第一次进起始房应为 0 同属推断,没有指令直接证明。
        /// </summary>
        public byte FlagA { get; }

        /// <summary>种子(i32,有符号)。客户端存进副本对象,并参与怪生成器的房间种子和(clire_NOTES §2.14【观】)。</summary>
        public int Seed { get; }

        /// <summary>全局 1(u8;客户端存进一个全局字节,语义未定)。</summary>
        public byte Global1 { get; }

        /// <summary>
        /// 全局 2(u8;客户端存进另一个全局字节,装载结束后才有代码读它,且仅当全局 3 非 0 时,clire_NOTES §2.6 表第 6 行【观】;语义未定)。
        /// </summary>
        public byte Global2 { get; }

        /// <summary>
        /// 地图类型(u8)。只有 1 或 2 才带地图体,其它值时客户端跳过地图体直接读尾部;解析器不解释各取值的含义。
        /// </summary>
        public byte MapType { get; }

        /// <summary>地图编号(u16)。仅 <see cref="MapType"/> 为 1 或 2 时有,否则为 null。</summary>
        public ushort? MapId { get; }

        /// <summary>
        /// 怪记录,按线上顺序(客户端先读 u8 个数 N,再读 N 条,个数就是列表长度)。仅 <see cref="MapType"/> 为 1 或 2 时可能非空,否则为空列表,不为 null。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<StartMapMonsterRecord> Monsters { get; }

        /// <summary>物品记录,规则同 <see cref="Monsters"/>(u8 个数 M + M 条;仅 <see cref="MapType"/> 为 1 或 2 时可能非空)。</summary>
        public IReadOnlyList<StartMapItemRecord> Items { get; }

        /// <summary>
        /// 全局 3(u8;客户端存进一个全局字节,非 0 时置另一个全局标志,语义未定)。<b>仅 <see cref="MapType"/> 为 1 或 2 时有,否则为 null</b>:
        /// 它在地图体里、读完物品记录之后,不在恒读尾部(依据见 <see cref="StartMapParser"/> 的说明)。
        /// </summary>
        public byte? Global3 { get; }

        /// <summary>
        /// 尾部的 K 组 {i32, i32, i32, i32}(客户端无论 mapType 都读;语义未定,客户端只在"队长槽角色就是本地角色"时把每组交给副本对象)。
        /// 按线上顺序;没有时为空列表,不为 null。列表只读。
        /// </summary>
        public IReadOnlyList<StartMapQuadRecord> Quads { get; }

        /// <summary>
        /// playerIndex(u8,读序最后一个字段)。客户端以 0xFF 为初值,不等于 0xFF 且队伍里有这个下标的队员时取该队员的角色
        /// (clire_NOTES §2.6【观】)。解析器只给原始字节,不解释 0xFF。
        /// </summary>
        public byte PlayerIndex { get; }

        /// <summary>
        /// 读完 playerIndex 之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴;真实帧里可能有折叠哈希与会话加密补零,
        /// 所以这个数只供对账(例如核对下面怪记录步长的未决点),不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 起始地图通知里的一条怪记录:<b>客户端每条读 16 字节</b>,读序 u16 A、i32 B、u16 C、i32 D、u8 E、u8 F、u8 G、u8 H。只读值。
    /// 各字段的业务含义没读通(客户端把它们存成 0x40 字节记录,在怪生成器里按条件取用,clire_NOTES §2.14【观】调用结构 / 【未】含义),
    /// 所以按读序中性命名。步长与服务端描述差一个字节的未决点见 <see cref="StartMapParser"/> 的说明。
    /// </summary>
    public readonly struct StartMapMonsterRecord
    {
        internal StartMapMonsterRecord(ushort a, int b, ushort c, int d, byte e, byte f, byte g, byte h)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
            F = f;
            G = g;
            H = h;
        }

        /// <summary>A(u16,记录内第 1 项)。</summary>
        public ushort A { get; }

        /// <summary>B(i32,有符号)。</summary>
        public int B { get; }

        /// <summary>C(u16)。</summary>
        public ushort C { get; }

        /// <summary>D(i32,有符号)。</summary>
        public int D { get; }

        /// <summary>E(u8)。</summary>
        public byte E { get; }

        /// <summary>F(u8)。</summary>
        public byte F { get; }

        /// <summary>G(u8)。</summary>
        public byte G { get; }

        /// <summary>H(u8,记录内第 8 项,客户端读序里的最后一个字节)。</summary>
        public byte H { get; }
    }

    /// <summary>
    /// 起始地图通知里的一条物品记录:客户端每条读 16 字节,读序 u8 A、u16 B、i32 C、i32 D、u16 E、u8 F、u16 G。只读值。
    /// 各字段业务含义没读通(clire_NOTES §2.6),按读序中性命名。
    /// </summary>
    public readonly struct StartMapItemRecord
    {
        internal StartMapItemRecord(byte a, ushort b, int c, int d, ushort e, byte f, ushort g)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
            F = f;
            G = g;
        }

        /// <summary>A(u8,记录内第 1 项)。</summary>
        public byte A { get; }

        /// <summary>B(u16)。</summary>
        public ushort B { get; }

        /// <summary>C(i32,有符号)。</summary>
        public int C { get; }

        /// <summary>D(i32,有符号)。</summary>
        public int D { get; }

        /// <summary>E(u16)。</summary>
        public ushort E { get; }

        /// <summary>F(u8)。</summary>
        public byte F { get; }

        /// <summary>G(u16,记录内第 7 项)。</summary>
        public ushort G { get; }
    }

    /// <summary>
    /// 起始地图通知尾部的一组四个 i32(客户端读序 A、B、C、D,16 字节)。只读值。语义未定,按读序中性命名。
    /// </summary>
    public readonly struct StartMapQuadRecord
    {
        internal StartMapQuadRecord(int a, int b, int c, int d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        /// <summary>A(i32,有符号)。</summary>
        public int A { get; }

        /// <summary>B(i32,有符号)。</summary>
        public int B { get; }

        /// <summary>C(i32,有符号)。</summary>
        public int C { get; }

        /// <summary>D(i32,有符号)。</summary>
        public int D { get; }
    }
}
