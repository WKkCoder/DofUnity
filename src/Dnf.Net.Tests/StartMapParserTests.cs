using System.Reflection;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 起始地图通知(类 0、包号 29)的明文载荷解析器(纯函数)。载荷全是手工拼的合成字节(手写字节数组或 <see cref="NoticePayloadBuilder"/>),
/// 地图编号、怪物与物品编号等取值全是合成值(902、910、9101 等),不读任何真实帧文件,不含真实编号或名字。
/// 读序依据 clire_NOTES §1.4、§2.6 与客户端反汇编的分支核对:
/// 头部 u8 格X、u8 格Y、u8 flagA、i32 种子、u8 全局1、u8 全局2、u8 mapType(10 字节,mapType 在下标 9);
/// 仅 mapType 为 1 或 2:u16 mapId、u8 N、N × 16 字节怪记录、u8 M、M × 16 字节物品记录、u8 全局3;
/// 之后恒读:u8 K、K × 16 字节(四个 i32)、u8 playerIndex。
/// 全局 3 在地图体里而不在恒读尾部,是与最初任务描述不同的一处,按反汇编(mapType 门直接跳到读 K 之前)落成,见相应用例。
/// </summary>
public class StartMapParserTests
{
    // ---- 合成值:构造与断言共用同一份数据,避免两边手误分叉 ----

    // 怪记录:A(u16) B(i32) C(u16) D(i32) E F G H(u8)
    private static readonly int[][] MonsterRows =
    {
        new[] { 910, -20, 911, 123456, 0x71, 0x72, 0x73, 0x74 },
        new[] { 65535, int.MinValue, 0x0102, int.MaxValue, 0xFF, 0x00, 0x80, 0x7F },
    };

    // 物品记录:A(u8) B(u16) C(i32) D(i32) E(u16) F(u8) G(u16)
    private static readonly int[][] ItemRows =
    {
        new[] { 0x81, 9101, -1, 77, 0xFFFF, 0x85, 9102 },
        new[] { 0xFF, 0, int.MaxValue, int.MinValue, 1, 0, 0xABCD },
    };

    // 四元组:四个 i32
    private static readonly int[][] QuadRows =
    {
        new[] { 1, -2, 3, -4 },
        new[] { int.MaxValue, int.MinValue, 0, 0x01020304 },
    };

    // ---- 合成载荷 ----

    /// <summary>头部 10 字节:格 X = 3、格 Y = 5、flagA = 7、种子、全局 1 = 0x61、全局 2 = 0x62、mapType。</summary>
    private static NoticePayloadBuilder Header(int mapType, int seed = 0x12345678)
    {
        return new NoticePayloadBuilder()
            .U8(3).U8(5).U8(7)
            .I32(seed)
            .U8(0x61).U8(0x62)
            .U8(mapType);
    }

    private static NoticePayloadBuilder Monster(NoticePayloadBuilder b, int[] r)
    {
        return b.U16(r[0]).I32(r[1]).U16(r[2]).I32(r[3]).U8(r[4]).U8(r[5]).U8(r[6]).U8(r[7]);
    }

    private static NoticePayloadBuilder Item(NoticePayloadBuilder b, int[] r)
    {
        return b.U8(r[0]).U16(r[1]).I32(r[2]).I32(r[3]).U16(r[4]).U8(r[5]).U16(r[6]);
    }

    private static NoticePayloadBuilder Quad(NoticePayloadBuilder b, int[] r)
    {
        return b.I32(r[0]).I32(r[1]).I32(r[2]).I32(r[3]);
    }

    /// <summary>
    /// 带地图体的标准载荷:mapId = 902,2 条怪、2 条物品,全局 3 = 0x63,2 组四元组,playerIndex = 4,再接 <paramref name="tail"/>。
    /// 长度:10 + 2 + 1 + 32 + 1 + 32 + 1 + 1 + 32 + 1 = 113(不含 tail)。
    /// </summary>
    private static byte[] FullPayload(int mapType, params byte[] tail)
    {
        NoticePayloadBuilder b = Header(mapType).U16(902).U8(MonsterRows.Length);
        foreach (int[] row in MonsterRows)
        {
            Monster(b, row);
        }
        b.U8(ItemRows.Length);
        foreach (int[] row in ItemRows)
        {
            Item(b, row);
        }
        b.U8(0x63).U8(QuadRows.Length);
        foreach (int[] row in QuadRows)
        {
            Quad(b, row);
        }
        return b.U8(0x04).Raw(tail).ToArray();
    }

    /// <summary>mapType 为 0 的载荷:没有地图体与全局 3,头部之后直接是 K = 2 组四元组与 playerIndex = 4(10 + 1 + 32 + 1 = 44 字节)。</summary>
    private static byte[] NoBodyPayload(int mapType, params byte[] tail)
    {
        NoticePayloadBuilder b = Header(mapType).U8(QuadRows.Length);
        foreach (int[] row in QuadRows)
        {
            Quad(b, row);
        }
        return b.U8(0x04).Raw(tail).ToArray();
    }

    // ---- 解析与断言助手 ----

    private static StartMapNotice ParseOk(byte[] payload)
    {
        Assert.True(StartMapParser.TryParse(payload, out StartMapNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    /// <summary>被拒:不抛异常、返回 false、notice 为 null。</summary>
    private static void AssertRejected(byte[] payload)
    {
        StartMapNotice notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = StartMapParser.TryParse(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    private static void AssertMonster(StartMapMonsterRecord m, int[] r)
    {
        Assert.Equal(r[0], m.A);
        Assert.Equal(r[1], m.B);
        Assert.Equal(r[2], m.C);
        Assert.Equal(r[3], m.D);
        Assert.Equal(r[4], m.E);
        Assert.Equal(r[5], m.F);
        Assert.Equal(r[6], m.G);
        Assert.Equal(r[7], m.H);
    }

    private static void AssertItem(StartMapItemRecord i, int[] r)
    {
        Assert.Equal(r[0], i.A);
        Assert.Equal(r[1], i.B);
        Assert.Equal(r[2], i.C);
        Assert.Equal(r[3], i.D);
        Assert.Equal(r[4], i.E);
        Assert.Equal(r[5], i.F);
        Assert.Equal(r[6], i.G);
    }

    private static void AssertQuad(StartMapQuadRecord q, int[] r)
    {
        Assert.Equal(r[0], q.A);
        Assert.Equal(r[1], q.B);
        Assert.Equal(r[2], q.C);
        Assert.Equal(r[3], q.D);
    }

    /// <summary>头部各字段(不含 mapType 与之后)的期望值,对应 <see cref="Header"/>。</summary>
    private static void AssertStandardHeader(StartMapNotice n, int seed = 0x12345678)
    {
        Assert.Equal(3, n.CellX);
        Assert.Equal(5, n.CellY);
        Assert.Equal(7, n.FlagA);
        Assert.Equal(seed, n.Seed);
        Assert.Equal(0x61, n.Global1);
        Assert.Equal(0x62, n.Global2);
    }

    private static void AssertListIsReadOnly<T>(IReadOnlyList<T> readOnly)
    {
        // 数组即使经 IList 也允许按下标写,所以结果不能是数组
        Assert.False(readOnly is Array);
        IList<T> list = Assert.IsAssignableFrom<IList<T>>(readOnly);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => { list.Add(default); });
        Assert.Throws<NotSupportedException>(() => { list.Insert(0, default); });
        Assert.Throws<NotSupportedException>(() => { list[0] = default; });
        Assert.Throws<NotSupportedException>(() => { list.RemoveAt(0); });
        Assert.Throws<NotSupportedException>(() => { list.Clear(); });
    }

    // ---- 常量与空输入 ----

    [Fact]
    public void Production_constant_matches_the_documented_id()
    {
        // 包号写成字面量:产品常量被改动时,测试要能发现
        Assert.Equal(29, StartMapParser.PacketId);
    }

    [Fact]
    public void Null_payload_is_rejected_without_throwing()
    {
        AssertRejected(null);
    }

    [Fact]
    public void Empty_payload_is_rejected()
    {
        AssertRejected(new byte[0]);
    }

    // ---- 读序与字节序(手写字节,逐字段标注) ----

    [Fact]
    public void Reads_a_literal_payload_with_map_body_but_no_records_in_client_order()
    {
        byte[] payload =
        {
            0x03, 0x05, 0x07,       // 格 X、格 Y、flagA
            0x78, 0x56, 0x34, 0x12, // 种子 0x12345678,小端
            0x61, 0x62,             // 全局 1、全局 2
            0x01,                   // mapType = 1
            0x86, 0x03,             // mapId = 902(合成值)= 0x0386,小端
            0x00,                   // N = 0
            0x00,                   // M = 0
            0x63,                   // 全局 3
            0x01,                   // K = 1
            0x04, 0x03, 0x02, 0x01, // 四元组 A = 0x01020304
            0xFF, 0xFF, 0xFF, 0xFF, // B = -1
            0x00, 0x00, 0x00, 0x80, // C = int.MinValue
            0xFF, 0xFF, 0xFF, 0x7F, // D = int.MaxValue
            0x04,                   // playerIndex
        };
        Assert.Equal(33, payload.Length);

        StartMapNotice n = ParseOk(payload);
        AssertStandardHeader(n);
        Assert.Equal(1, n.MapType);
        Assert.Equal((ushort?)902, n.MapId);
        Assert.Empty(n.Monsters);
        Assert.Empty(n.Items);
        Assert.Equal((byte?)0x63, n.Global3);
        StartMapQuadRecord quad = Assert.Single(n.Quads);
        Assert.Equal(0x01020304, quad.A);
        Assert.Equal(-1, quad.B);
        Assert.Equal(int.MinValue, quad.C);
        Assert.Equal(int.MaxValue, quad.D);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Reads_literal_monster_and_item_records_in_client_field_order()
    {
        byte[] payload =
        {
            0x03, 0x05, 0x07,       // 格 X、格 Y、flagA
            0x78, 0x56, 0x34, 0x12, // 种子
            0x61, 0x62,             // 全局 1、全局 2
            0x02,                   // mapType = 2
            0x86, 0x03,             // mapId = 902
            0x01,                   // N = 1
            0x8E, 0x03,             // 怪 A(u16)= 910
            0xEC, 0xFF, 0xFF, 0xFF, // B(i32)= -20
            0x8F, 0x03,             // C(u16)= 911
            0x40, 0xE2, 0x01, 0x00, // D(i32)= 123456
            0x71, 0x72, 0x73, 0x74, // E、F、G、H(u8)
            0x01,                   // M = 1
            0x81,                   // 物品 A(u8)= 0x81
            0x8D, 0x23,             // B(u16)= 9101
            0xFF, 0xFF, 0xFF, 0xFF, // C(i32)= -1
            0x4D, 0x00, 0x00, 0x00, // D(i32)= 77
            0xFF, 0xFF,             // E(u16)= 0xFFFF
            0x85,                   // F(u8)= 0x85
            0x8E, 0x23,             // G(u16)= 9102
            0x63,                   // 全局 3
            0x00,                   // K = 0
            0x04,                   // playerIndex
        };
        Assert.Equal(49, payload.Length); // 10 + 2 + 1 + 16 + 1 + 16 + 1 + 1 + 1

        StartMapNotice n = ParseOk(payload);
        AssertStandardHeader(n);
        Assert.Equal(2, n.MapType);
        Assert.Equal((ushort?)902, n.MapId);

        StartMapMonsterRecord monster = Assert.Single(n.Monsters);
        Assert.Equal(910, monster.A);
        Assert.Equal(-20, monster.B);
        Assert.Equal(911, monster.C);
        Assert.Equal(123456, monster.D);
        Assert.Equal(0x71, monster.E);
        Assert.Equal(0x72, monster.F);
        Assert.Equal(0x73, monster.G);
        Assert.Equal(0x74, monster.H);

        StartMapItemRecord item = Assert.Single(n.Items);
        Assert.Equal(0x81, item.A);
        Assert.Equal(9101, item.B);
        Assert.Equal(-1, item.C);
        Assert.Equal(77, item.D);
        Assert.Equal(0xFFFF, item.E);
        Assert.Equal(0x85, item.F);
        Assert.Equal(9102, item.G);

        Assert.Equal((byte?)0x63, n.Global3);
        Assert.Empty(n.Quads);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Reads_a_literal_payload_without_map_body_straight_into_the_tail()
    {
        // mapType = 0:没有 mapId、N、M、全局 3,头部之后第一个字节就是 K
        byte[] payload =
        {
            0x03, 0x05, 0x07,       // 格 X、格 Y、flagA
            0x78, 0x56, 0x34, 0x12, // 种子
            0x61, 0x62,             // 全局 1、全局 2
            0x00,                   // mapType = 0
            0x01,                   // K = 1(紧接 mapType)
            0x04, 0x03, 0x02, 0x01, // 四元组 A
            0xFF, 0xFF, 0xFF, 0xFF, // B
            0x00, 0x00, 0x00, 0x80, // C
            0xFF, 0xFF, 0xFF, 0x7F, // D
            0x04,                   // playerIndex
        };
        Assert.Equal(28, payload.Length);

        StartMapNotice n = ParseOk(payload);
        AssertStandardHeader(n);
        Assert.Equal(0, n.MapType);
        Assert.Null(n.MapId);
        Assert.Empty(n.Monsters);
        Assert.Empty(n.Items);
        Assert.Null(n.Global3);
        StartMapQuadRecord quad = Assert.Single(n.Quads);
        Assert.Equal(0x01020304, quad.A);
        Assert.Equal(-1, quad.B);
        Assert.Equal(int.MinValue, quad.C);
        Assert.Equal(int.MaxValue, quad.D);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Full_payload_reads_every_field_for_map_type_1_and_2(int mapType)
    {
        byte[] payload = FullPayload(mapType);
        Assert.Equal(113, payload.Length);

        StartMapNotice n = ParseOk(payload);
        AssertStandardHeader(n);
        Assert.Equal(mapType, n.MapType);
        Assert.Equal((ushort?)902, n.MapId);
        Assert.Equal(2, n.Monsters.Count);
        AssertMonster(n.Monsters[0], MonsterRows[0]);
        AssertMonster(n.Monsters[1], MonsterRows[1]);
        Assert.Equal(2, n.Items.Count);
        AssertItem(n.Items[0], ItemRows[0]);
        AssertItem(n.Items[1], ItemRows[1]);
        Assert.Equal((byte?)0x63, n.Global3);
        Assert.Equal(2, n.Quads.Count);
        AssertQuad(n.Quads[0], QuadRows[0]);
        AssertQuad(n.Quads[1], QuadRows[1]);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- mapType 门:只有 1 与 2 才带地图体 ----

    [Fact]
    public void Map_body_exists_only_for_map_type_1_and_2_over_every_possible_map_type()
    {
        for (int mapType = 0; mapType <= 255; mapType++)
        {
            bool hasBody = mapType == 1 || mapType == 2; // 独立写出的规则,与产品里的条件分开
            NoticePayloadBuilder b = Header(mapType);
            if (hasBody)
            {
                b.U16(902).U8(1);
                Monster(b, MonsterRows[0]);
                b.U8(1);
                Item(b, ItemRows[0]);
                b.U8(0x63);
            }
            b.U8(1);
            Quad(b, QuadRows[0]);
            b.U8(0x04);

            StartMapNotice n = ParseOk(b.ToArray());
            AssertStandardHeader(n);
            Assert.Equal(mapType, n.MapType);
            if (hasBody)
            {
                Assert.Equal((ushort?)902, n.MapId);
                AssertMonster(Assert.Single(n.Monsters), MonsterRows[0]);
                AssertItem(Assert.Single(n.Items), ItemRows[0]);
                Assert.Equal((byte?)0x63, n.Global3);
            }
            else
            {
                Assert.Null(n.MapId);
                Assert.Empty(n.Monsters);
                Assert.Empty(n.Items);
                Assert.Null(n.Global3);
            }

            // 尾部不论 mapType 都在
            AssertQuad(Assert.Single(n.Quads), QuadRows[0]);
            Assert.Equal(0x04, n.PlayerIndex);
            Assert.Equal(0, n.TrailingByteCount);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(255)]
    public void Global3_belongs_to_the_map_body_and_the_always_read_tail_starts_at_K(int mapType)
    {
        // 依据:客户端 0x00749fa0 的 mapType 门直接跳到读 K 之前,越过读全局 3 的 0x0074a418(clire_NOTES §2.6 表第 7 行)。
        // 所以 mapType 不是 1、2 时,头部之后的第一个字节就是 K;若把全局 3 当成恒读,这里会把 K 吃成全局 3 而错位
        byte[] payload = NoBodyPayload(mapType);
        Assert.Equal(44, payload.Length);

        StartMapNotice n = ParseOk(payload);
        Assert.Null(n.Global3);
        Assert.Null(n.MapId);
        Assert.Equal(2, n.Quads.Count);
        AssertQuad(n.Quads[0], QuadRows[0]);
        AssertQuad(n.Quads[1], QuadRows[1]);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Global3_comes_after_the_items_and_before_K_when_the_map_body_exists(int mapType)
    {
        // 物品数 M = 0 时全局 3 紧接在 M 之后;再往后才是 K
        byte[] payload = Header(mapType).U16(902).U8(0).U8(0).U8(0x63).U8(0).U8(0x04).ToArray();
        Assert.Equal(17, payload.Length); // 10 + 2 + 1 + 1 + 1 + 1 + 1

        StartMapNotice n = ParseOk(payload);
        Assert.Equal((byte?)0x63, n.Global3);
        Assert.Empty(n.Quads);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Minimal_payloads_have_the_documented_lengths()
    {
        // mapType 1:头 10 + mapId 2 + N 1 + M 1 + 全局 3 1 + K 1 + playerIndex 1 = 17 字节,全部个数为 0
        byte[] withBody = Header(1).U16(902).U8(0).U8(0).U8(0x63).U8(0).U8(0x04).ToArray();
        Assert.Equal(17, withBody.Length);
        StartMapNotice a = ParseOk(withBody);
        Assert.Empty(a.Monsters);
        Assert.Empty(a.Items);
        Assert.Empty(a.Quads);
        Assert.Equal((ushort?)902, a.MapId);
        Assert.Equal((byte?)0x63, a.Global3);
        Assert.Equal(0x04, a.PlayerIndex);
        Assert.Equal(0, a.TrailingByteCount);

        // mapType 0:头 10 + K 1 + playerIndex 1 = 12 字节
        byte[] noBody = Header(0).U8(0).U8(0x04).ToArray();
        Assert.Equal(12, noBody.Length);
        StartMapNotice b = ParseOk(noBody);
        Assert.Null(b.MapId);
        Assert.Null(b.Global3);
        Assert.Empty(b.Quads);
        Assert.Equal(0x04, b.PlayerIndex);
        Assert.Equal(0, b.TrailingByteCount);
    }

    // ---- 记录步长与字段顺序 ----

    [Fact]
    public void Monster_records_use_the_client_stride_of_16_bytes()
    {
        // 未决点(见产品代码里 StartMapParser 的说明):服务端侧描述每条怪记录 17 字节,客户端读序是 16 字节。
        // 本任务以客户端读序为准,这里把 16 钉死;真实帧对账留待信封解开后另行进行
        const int count = 3;
        NoticePayloadBuilder b = Header(1).U16(902).U8(count);
        int before = b.Length;
        for (int i = 0; i < count; i++)
        {
            Monster(b, MonsterRow(i));
        }
        Assert.Equal(count * 16, b.Length - before); // 手工拼出的记录就是 16 字节一条
        b.U8(1);
        Item(b, ItemRows[0]);
        b.U8(0x63).U8(0).U8(0x04);

        StartMapNotice n = ParseOk(b.ToArray());
        Assert.Equal(count, n.Monsters.Count);
        for (int i = 0; i < count; i++)
        {
            AssertMonster(n.Monsters[i], MonsterRow(i));
        }

        // 怪记录之后的物品、全局 3、尾部都接在 count * 16 字节之后:步长错了这里会整体错位
        AssertItem(Assert.Single(n.Items), ItemRows[0]);
        Assert.Equal((byte?)0x63, n.Global3);
        Assert.Empty(n.Quads);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Item_records_use_a_stride_of_16_bytes_in_the_documented_field_order()
    {
        const int count = 3;
        NoticePayloadBuilder b = Header(2).U16(902).U8(0).U8(count);
        int before = b.Length;
        for (int i = 0; i < count; i++)
        {
            Item(b, ItemRow(i));
        }
        Assert.Equal(count * 16, b.Length - before);
        b.U8(0x63).U8(1);
        Quad(b, QuadRows[0]);
        b.U8(0x04);

        StartMapNotice n = ParseOk(b.ToArray());
        Assert.Empty(n.Monsters);
        Assert.Equal(count, n.Items.Count);
        for (int i = 0; i < count; i++)
        {
            AssertItem(n.Items[i], ItemRow(i));
        }
        Assert.Equal((byte?)0x63, n.Global3);
        AssertQuad(Assert.Single(n.Quads), QuadRows[0]);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Quad_groups_are_four_i32_each_and_read_in_wire_order(int count)
    {
        NoticePayloadBuilder b = Header(0).U8(count);
        int before = b.Length;
        for (int i = 0; i < count; i++)
        {
            Quad(b, QuadRow(i));
        }
        Assert.Equal(count * 16, b.Length - before);
        b.U8(0x04);

        StartMapNotice n = ParseOk(b.ToArray());
        Assert.Equal(count, n.Quads.Count);
        for (int i = 0; i < count; i++)
        {
            AssertQuad(n.Quads[i], QuadRow(i));
        }
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // 每条记录取互不相同、含负数的值:字段对调或步长错位后读出的值会变
    private static int[] MonsterRow(int i)
    {
        return new[] { 1000 + i, -(2000 + i), 3000 + i, 4000 + i, 0x10 + i, 0x20 + i, 0x30 + i, 0x40 + i };
    }

    private static int[] ItemRow(int i)
    {
        return new[] { 0x10 + i, 1000 + i, -(2000 + i), 3000 + i, 40000 + i, 0x50 + i, 5000 + i };
    }

    private static int[] QuadRow(int i)
    {
        return new[] { 100 + i, -(200 + i), 300 + i, -(400 + i) };
    }

    [Fact]
    public void Count_fields_are_one_byte_and_255_records_of_each_kind_are_read()
    {
        NoticePayloadBuilder b = Header(2).U16(902).U8(255);
        for (int i = 0; i < 255; i++)
        {
            Monster(b, new[] { i, -i, 60000 + i, i * 1000, i, 255 - i, i ^ 0x55, i ^ 0xAA });
        }
        b.U8(255);
        for (int i = 0; i < 255; i++)
        {
            Item(b, new[] { i, 1000 + i, -i, i * 1000, 40000 + i, 255 - i, 30000 + i });
        }
        b.U8(0x63).U8(255);
        for (int i = 0; i < 255; i++)
        {
            Quad(b, new[] { i, -i, i * 1000, 7 - i });
        }
        b.U8(0x04);

        StartMapNotice n = ParseOk(b.ToArray());
        Assert.Equal(255, n.Monsters.Count);
        Assert.Equal(255, n.Items.Count);
        Assert.Equal(255, n.Quads.Count);
        for (int i = 0; i < 255; i++)
        {
            AssertMonster(n.Monsters[i], new[] { i, -i, 60000 + i, i * 1000, i, 255 - i, i ^ 0x55, i ^ 0xAA });
            AssertItem(n.Items[i], new[] { i, 1000 + i, -i, i * 1000, 40000 + i, 255 - i, 30000 + i });
            AssertQuad(n.Quads[i], new[] { i, -i, i * 1000, 7 - i });
        }
        Assert.Equal((byte?)0x63, n.Global3);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- 多字节字段的宽度与符号 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(0x12345678)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(-20240517)]
    public void Seed_is_a_signed_little_endian_i32(int seed)
    {
        StartMapNotice n = ParseOk(Header(0, seed).U8(0).U8(0x04).ToArray());
        AssertStandardHeader(n, seed);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(902)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(65535)]
    public void Map_id_is_an_unsigned_little_endian_u16(int mapId)
    {
        StartMapNotice n = ParseOk(Header(1).U16(mapId).U8(0).U8(0).U8(0x63).U8(0).U8(0x04).ToArray());
        Assert.Equal(mapId, n.MapId.Value);
        Assert.Equal((byte?)0x63, n.Global3);
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void Player_index_is_the_raw_last_byte_with_no_special_casing(int value)
    {
        // 0xFF 在客户端侧是"没有指定"的初值,但解析器只给原始字节,不解释
        StartMapNotice n = ParseOk(Header(0).U8(0).U8(value).ToArray());
        Assert.Equal(value, n.PlayerIndex);
    }

    // ---- 尾巴:不消费、不核对,只报告个数 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(11)]
    [InlineData(16)]
    public void Trailing_bytes_are_counted_but_never_consumed_or_checked(int tailLength)
    {
        // 尾巴拼成"又一组四元组加 playerIndex"的形状:被错吞就会改变 Quads 的个数或 playerIndex
        byte[] tail = new byte[tailLength];
        for (int i = 0; i < tailLength; i++)
        {
            tail[i] = (byte)(0xB0 + i);
        }

        StartMapNotice withBody = ParseOk(FullPayload(1, tail));
        Assert.Equal(2, withBody.Quads.Count);
        AssertQuad(withBody.Quads[1], QuadRows[1]);
        Assert.Equal(0x04, withBody.PlayerIndex);
        Assert.Equal(tailLength, withBody.TrailingByteCount);

        StartMapNotice noBody = ParseOk(NoBodyPayload(0, tail));
        Assert.Equal(2, noBody.Quads.Count);
        Assert.Equal(0x04, noBody.PlayerIndex);
        Assert.Equal(tailLength, noBody.TrailingByteCount);
    }

    [Fact]
    public void Trailing_hash_and_padding_shaped_tail_is_tolerated()
    {
        // 尾巴形如"4 字节校验 + 会话加密补的零":读完 playerIndex 就停,不要求恰好读完,也不核对
        byte[] tail = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }.Concat(new byte[7]).ToArray();
        StartMapNotice n = ParseOk(FullPayload(2, tail));
        Assert.Equal(0x04, n.PlayerIndex);
        Assert.Equal(11, n.TrailingByteCount);
    }

    // ---- 短包与个数超出载荷:一律拒绝,不抛 ----

    [Fact]
    public void Every_strict_prefix_of_a_valid_payload_is_rejected()
    {
        byte[][] shapes =
        {
            FullPayload(1),
            FullPayload(2),
            NoBodyPayload(0),
            Header(1).U16(902).U8(0).U8(0).U8(0x63).U8(0).U8(0x04).ToArray(),
            Header(0).U8(0).U8(0x04).ToArray(),
        };
        foreach (byte[] full in shapes)
        {
            ParseOk(full);
            for (int len = 0; len < full.Length; len++)
            {
                AssertRejected(full.Take(len).ToArray());
            }
        }
    }

    [Fact]
    public void Counts_that_exceed_the_data_are_rejected_for_each_list()
    {
        // 怪数声明 255,数据只给两条的量
        AssertRejected(Header(1).U16(902).U8(255).Raw(new byte[32]).ToArray());

        // 物品数声明 255,数据只给两条的量
        AssertRejected(Header(1).U16(902).U8(0).U8(255).Raw(new byte[32]).ToArray());

        // K 声明 255,数据只给两条的量
        AssertRejected(Header(0).U8(255).Raw(new byte[32]).ToArray());
        AssertRejected(Header(1).U16(902).U8(0).U8(0).U8(0x63).U8(255).Raw(new byte[32]).ToArray());
    }

    [Fact]
    public void A_truncated_last_record_fails_the_whole_payload_even_when_the_leftover_looks_like_the_tail()
    {
        // 物品记录只读到一半:u8 A、u16 B 已读,i32 C 缺一个字节。剩下的 3 个字节恰好是"全局 3、K = 0、playerIndex"的形状。
        // 若把读不下的记录当成"列表到此为止"(跳出循环)而不是整包失败,这份载荷会被错收成功
        byte[] truncatedItem = Header(1).U16(902).U8(0).U8(1).U8(0x81).U16(9101).Raw(0x63, 0x00, 0x04).ToArray();
        AssertRejected(truncatedItem);

        // 四元组只读到 3 个 i32(12 字节),第 4 个 i32 只剩 1 个字节:这 1 个字节恰好是 playerIndex 的形状
        byte[] truncatedQuad = Header(0).U8(1).I32(1).I32(2).I32(3).Raw(0x04).ToArray();
        AssertRejected(truncatedQuad);
    }

    // ---- 容错:任意载荷都不抛,成败与长度规则一致,字段与字节对得上 ----

    [Fact]
    public void Generated_payloads_with_random_cuts_and_tails_match_the_length_rule_and_the_bytes()
    {
        // 确定性伪随机(固定种子):先随机挑结构(mapType 取 0/1/2/3/255,三个个数各取 0..3),填随机字节,再随机截断或补尾巴
        uint state = 0x2468ACE1;
        byte NextByte()
        {
            state = (state * 1664525u) + 1013904223u;
            return (byte)(state >> 24);
        }

        int[] mapTypes = { 0, 1, 2, 3, 255 };
        int accepted = 0;
        int rejected = 0;
        for (int round = 0; round < 2000; round++)
        {
            int mapType = mapTypes[NextByte() % mapTypes.Length];
            int monsterCount = NextByte() % 4;
            int itemCount = NextByte() % 4;
            int quadCount = NextByte() % 4;

            var b = new NoticePayloadBuilder();
            for (int i = 0; i < 9; i++)
            {
                b.U8(NextByte());
            }
            b.U8(mapType);
            if (mapType == 1 || mapType == 2)
            {
                b.U8(NextByte()).U8(NextByte());
                b.U8(monsterCount);
                for (int i = 0; i < 16 * monsterCount; i++)
                {
                    b.U8(NextByte());
                }
                b.U8(itemCount);
                for (int i = 0; i < 16 * itemCount; i++)
                {
                    b.U8(NextByte());
                }
                b.U8(NextByte());
            }
            b.U8(quadCount);
            for (int i = 0; i < 16 * quadCount; i++)
            {
                b.U8(NextByte());
            }
            b.U8(NextByte());
            byte[] payload = b.ToArray();

            switch (NextByte() % 4)
            {
                case 1: // 少 1..3 个字节
                    payload = payload.Take(Math.Max(0, payload.Length - (1 + (NextByte() % 3)))).ToArray();
                    break;
                case 2: // 多 1..6 个字节
                    payload = payload.Concat(Enumerable.Range(0, 1 + (NextByte() % 6)).Select(_ => NextByte())).ToArray();
                    break;
                case 3: // 随机截断(可能恰好是全长)
                    payload = payload.Take(NextByte() % (payload.Length + 1)).ToArray();
                    break;
            }

            int consumed = ExpectedConsumed(payload);
            StartMapNotice n = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = StartMapParser.TryParse(payload, out n); });
            Assert.Null(ex);
            Assert.Equal(consumed >= 0, ok);
            if (consumed < 0)
            {
                Assert.Null(n);
                rejected++;
                continue;
            }

            accepted++;
            Assert.NotNull(n);
            AssertMatchesBytes(payload, n, consumed);
        }

        // 成功与失败两个分支都被大量覆盖(防止这个用例退化)
        Assert.True(accepted > 500);
        Assert.True(rejected > 100);
    }

    [Fact]
    public void Raw_random_payloads_never_throw_and_agree_with_the_length_rule()
    {
        uint seed = 0x0BADF00D;
        for (int len = 0; len <= 70; len++)
        {
            for (int round = 0; round < 30; round++)
            {
                byte[] body = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    seed = (seed * 1664525u) + 1013904223u;
                    body[i] = (byte)(seed >> 24);
                }
                if (len > 9 && round % 3 == 0)
                {
                    body[9] = (byte)(1 + (body[9] % 2)); // 让三分之一的样本 mapType 落在 1 或 2
                }

                int consumed = ExpectedConsumed(body);
                StartMapNotice n = null;
                bool ok = false;
                Exception ex = Record.Exception(() => { ok = StartMapParser.TryParse(body, out n); });
                Assert.Null(ex);
                Assert.Equal(consumed >= 0, ok);
                if (consumed < 0)
                {
                    Assert.Null(n);
                    continue;
                }

                Assert.NotNull(n);
                AssertMatchesBytes(body, n, consumed);
            }
        }
    }

    /// <summary>
    /// 按字节数规则独立推出载荷应读掉多少字节(读不下返回 -1);不复用产品里的读取代码。
    /// 头部 10 字节(mapType 在下标 9);mapType 为 1 或 2 时 + mapId 2 字节、N 与 16N、M 与 16M、全局 3 一字节;之后 K 与 16K、playerIndex 一字节。
    /// </summary>
    private static int ExpectedConsumed(byte[] p)
    {
        if (p.Length < 10)
        {
            return -1;
        }

        int pos = 10;
        if (p[9] == 1 || p[9] == 2)
        {
            pos += 2;                  // mapId
            if (p.Length <= pos)       // 读不到 N
            {
                return -1;
            }
            pos += 1 + (16 * p[pos]);  // N 与 N 条怪记录
            if (p.Length <= pos)       // 读不到 M
            {
                return -1;
            }
            pos += 1 + (16 * p[pos]);  // M 与 M 条物品记录
            pos += 1;                  // 全局 3
        }

        if (p.Length <= pos)           // 读不到 K
        {
            return -1;
        }
        pos += 1 + (16 * p[pos]);      // K 与 K 组
        pos += 1;                      // playerIndex
        return p.Length >= pos ? pos : -1;
    }

    private static int Le16(byte[] p, int at)
    {
        return p[at] | (p[at + 1] << 8);
    }

    private static int Le32(byte[] p, int at)
    {
        return p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24);
    }

    /// <summary>按偏移直接从字节里读出期望值(与产品读取代码无关),核对头部、各个数、每条记录首尾字段、playerIndex 与尾巴个数。</summary>
    private static void AssertMatchesBytes(byte[] p, StartMapNotice n, int consumed)
    {
        Assert.Equal(p[0], n.CellX);
        Assert.Equal(p[1], n.CellY);
        Assert.Equal(p[2], n.FlagA);
        Assert.Equal(Le32(p, 3), n.Seed);
        Assert.Equal(p[7], n.Global1);
        Assert.Equal(p[8], n.Global2);
        Assert.Equal(p[9], n.MapType);

        int pos = 10;
        if (p[9] == 1 || p[9] == 2)
        {
            Assert.Equal(Le16(p, 10), n.MapId.Value);
            int monsterCount = p[12];
            Assert.Equal(monsterCount, n.Monsters.Count);
            pos = 13;
            for (int i = 0; i < monsterCount; i++)
            {
                int at = pos + (16 * i);
                Assert.Equal(Le16(p, at), n.Monsters[i].A);
                Assert.Equal(Le32(p, at + 2), n.Monsters[i].B);
                Assert.Equal(Le16(p, at + 6), n.Monsters[i].C);
                Assert.Equal(Le32(p, at + 8), n.Monsters[i].D);
                Assert.Equal(p[at + 12], n.Monsters[i].E);
                Assert.Equal(p[at + 13], n.Monsters[i].F);
                Assert.Equal(p[at + 14], n.Monsters[i].G);
                Assert.Equal(p[at + 15], n.Monsters[i].H);
            }
            pos += 16 * monsterCount;
            int itemCount = p[pos];
            Assert.Equal(itemCount, n.Items.Count);
            pos += 1;
            for (int i = 0; i < itemCount; i++)
            {
                int at = pos + (16 * i);
                Assert.Equal(p[at], n.Items[i].A);
                Assert.Equal(Le16(p, at + 1), n.Items[i].B);
                Assert.Equal(Le32(p, at + 3), n.Items[i].C);
                Assert.Equal(Le32(p, at + 7), n.Items[i].D);
                Assert.Equal(Le16(p, at + 11), n.Items[i].E);
                Assert.Equal(p[at + 13], n.Items[i].F);
                Assert.Equal(Le16(p, at + 14), n.Items[i].G);
            }
            pos += 16 * itemCount;
            Assert.Equal(p[pos], n.Global3.Value);
            pos += 1;
        }
        else
        {
            Assert.Null(n.MapId);
            Assert.Empty(n.Monsters);
            Assert.Empty(n.Items);
            Assert.Null(n.Global3);
        }

        int quadCount = p[pos];
        Assert.Equal(quadCount, n.Quads.Count);
        pos += 1;
        for (int i = 0; i < quadCount; i++)
        {
            int at = pos + (16 * i);
            Assert.Equal(Le32(p, at), n.Quads[i].A);
            Assert.Equal(Le32(p, at + 4), n.Quads[i].B);
            Assert.Equal(Le32(p, at + 8), n.Quads[i].C);
            Assert.Equal(Le32(p, at + 12), n.Quads[i].D);
        }
        pos += 16 * quadCount;
        Assert.Equal(p[pos], n.PlayerIndex);
        Assert.Equal(pos + 1, consumed);
        Assert.Equal(p.Length - consumed, n.TrailingByteCount);
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void Notice_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        Type[] types =
        {
            typeof(StartMapNotice),
            typeof(StartMapMonsterRecord),
            typeof(StartMapItemRecord),
            typeof(StartMapQuadRecord),
        };
        foreach (Type type in types)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.Null(property.GetSetMethod(true));
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(field.IsInitOnly);
            }
        }

        // 外部只能经解析器拿到记录
        Assert.Empty(typeof(StartMapNotice).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(StartMapMonsterRecord).IsValueType);
        Assert.True(typeof(StartMapItemRecord).IsValueType);
        Assert.True(typeof(StartMapQuadRecord).IsValueType);

        // 实例字段只许简单值与只读列表接口:不留载荷数组或可写集合的引用
        Type[] allowed =
        {
            typeof(byte),
            typeof(int),
            typeof(ushort?),
            typeof(byte?),
            typeof(IReadOnlyList<StartMapMonsterRecord>),
            typeof(IReadOnlyList<StartMapItemRecord>),
            typeof(IReadOnlyList<StartMapQuadRecord>),
        };
        foreach (FieldInfo field in typeof(StartMapNotice).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.Contains(field.FieldType, allowed);
        }
    }

    [Fact]
    public void The_three_lists_reject_every_write_even_through_IList()
    {
        StartMapNotice n = ParseOk(FullPayload(1));
        AssertListIsReadOnly(n.Monsters);
        AssertListIsReadOnly(n.Items);
        AssertListIsReadOnly(n.Quads);

        // 写都失败了,内容没变
        Assert.Equal(2, n.Monsters.Count);
        AssertMonster(n.Monsters[0], MonsterRows[0]);
        AssertItem(n.Items[1], ItemRows[1]);
        AssertQuad(n.Quads[1], QuadRows[1]);
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer()
    {
        byte[] payload = FullPayload(1);
        StartMapNotice n = ParseOk(payload);

        // 解析之后把输入整个改写:结果不能跟着变
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        AssertStandardHeader(n);
        Assert.Equal((ushort?)902, n.MapId);
        AssertMonster(n.Monsters[1], MonsterRows[1]);
        AssertItem(n.Items[0], ItemRows[0]);
        Assert.Equal((byte?)0x63, n.Global3);
        AssertQuad(n.Quads[1], QuadRows[1]);
        Assert.Equal(0x04, n.PlayerIndex);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_is_repeatable()
    {
        byte[] payload = FullPayload(2, 1, 2, 3);
        byte[] before = (byte[])payload.Clone();

        StartMapNotice first = ParseOk(payload);
        StartMapNotice second = ParseOk(payload);

        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.Equal(first.CellX, second.CellX);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.MapType, second.MapType);
        Assert.Equal(first.MapId, second.MapId);
        Assert.Equal(first.Monsters.Count, second.Monsters.Count);
        Assert.Equal(first.Monsters[1].B, second.Monsters[1].B);
        Assert.Equal(first.Items[1].G, second.Items[1].G);
        Assert.Equal(first.Global3, second.Global3);
        Assert.Equal(first.Quads[1].D, second.Quads[1].D);
        Assert.Equal(first.PlayerIndex, second.PlayerIndex);
        Assert.Equal(first.TrailingByteCount, second.TrailingByteCount);
    }
}
