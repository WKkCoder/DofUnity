using System.Reflection;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 副本开始通知(类 0、包号 27)的明文载荷解析器(纯函数)。载荷全是手工拼的合成字节(手写字节数组或 <see cref="NoticePayloadBuilder"/>),
/// 成员编号与各项取值等 u16、i32 全是 900 以上的合成值(911、921、931、90210 等),不读任何真实帧文件,不含真实编号或名字。
/// u8 的个数字段(n、cnt1、cnt2)装不下 900,取 0..6 的小值与边界 255;宽度与符号的边界用例另用 0、65535、int.MinValue、int.MaxValue 等纯宽度值。
/// 读序依据 clire_NOTES §1.4、§2.5:i32 A;u8 n 与 n 个 u16;u8 cnt1 与 cnt1 组 {u16 memberId, u8 cnt2, cnt2 × {u16, i32, i32, i32}};i32 B;u8 flag。
/// 最短(n = 0、cnt1 = 0)11 字节;每项 14 字节;总长 = 11 + 2n + Σ(3 + 14 × cnt2)。读完 flag 即停,其后的尾巴解析器不消费,只报告个数。
/// 客户端的两处行为不模拟,各有用例钉住:n 不设上限(客户端局部数组只够 4 个);memberId 不查队伍、不看取值(客户端查不到成员时会跳过读取而错位)。
/// </summary>
public class DungeonStartParserTests
{
    // ---- 合成值:构造与断言共用同一份数据,避免两边手误分叉 ----

    private const int StandardA = 90210;   // 0x00016062:三个字节互不相同,另一个是 0
    private const int StandardB = -90310;  // 0xFFFE9F3A:负数,核对有符号
    private const int StandardFlag = 0x01;

    // u16 列表
    private static readonly int[] StandardValues = { 911, 912, 913 };

    // 成员组:memberId 与它的各项 {u16 A, i32 B, i32 C, i32 D};中间一个成员没有项
    private static readonly (int Id, int[][] Entries)[] StandardMembers =
    {
        (921, new[] { new[] { 931, -932, 933, 934000 }, new[] { 935, 936, -937, 938000 } }),
        (922, new int[0][]),
        (923, new[] { new[] { 941, 942, 943, 944 }, new[] { 945, -946, 947, 948 }, new[] { 949, 950, 951, -952 } }),
    };

    private static readonly (int Id, int[][] Entries)[] NoMembers = new (int Id, int[][] Entries)[0];

    // 每项取互不相同、含负数的值:字段对调或步长错位后读出的值会变(u16 的 A 不超过 65535)
    private static int[] EntryRow(int i)
    {
        return new[] { 900 + i, -(1000 + i), 2000 + i, 3000000 + i };
    }

    // ---- 合成载荷 ----

    private static NoticePayloadBuilder AppendMembers(NoticePayloadBuilder b, (int Id, int[][] Entries)[] members)
    {
        b.U8(members.Length);
        foreach ((int id, int[][] entries) in members)
        {
            b.U16(id).U8(entries.Length);
            foreach (int[] e in entries)
            {
                b.U16(e[0]).I32(e[1]).I32(e[2]).I32(e[3]);
            }
        }
        return b;
    }

    /// <summary>按读序拼载荷:A、n 与 n 个 u16、cnt1 与各成员组、B、flag,再接 <paramref name="tail"/>。</summary>
    private static byte[] Build(int a, int[] values, (int Id, int[][] Entries)[] members, int bValue, int flag, params byte[] tail)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().I32(a).U8(values.Length);
        foreach (int value in values)
        {
            b.U16(value);
        }
        AppendMembers(b, members);
        return b.I32(bValue).U8(flag).Raw(tail).ToArray();
    }

    /// <summary>
    /// 标准载荷:3 个 u16、3 个成员组(2 项、0 项、3 项)、B、flag,再接 <paramref name="tail"/>。
    /// 长度:4 + 1 + 6 + 1 + (3 + 28) + 3 + (3 + 42) + 4 + 1 = 96(不含 tail)。
    /// </summary>
    private static byte[] Standard(params byte[] tail)
    {
        return Build(StandardA, StandardValues, StandardMembers, StandardB, StandardFlag, tail);
    }

    /// <summary>最小载荷:n = 0、cnt1 = 0,只有 A、B、flag,11 字节,再接 <paramref name="tail"/>。</summary>
    private static byte[] Minimal(params byte[] tail)
    {
        return Build(StandardA, new int[0], NoMembers, StandardB, StandardFlag, tail);
    }

    /// <summary>标准载荷,并记下每个字段的(名字, 起点, 宽度),供"在每个字段里面截断"的用例使用。</summary>
    private static byte[] StandardWithFieldMarks(List<(string Name, int Start, int Width)> marks)
    {
        var b = new NoticePayloadBuilder();
        void Mark(string name, int width)
        {
            marks.Add((name, b.Length, width));
        }

        Mark("A", 4);
        b.I32(StandardA);
        Mark("n", 1);
        b.U8(StandardValues.Length);
        for (int i = 0; i < StandardValues.Length; i++)
        {
            Mark("values[" + i + "]", 2);
            b.U16(StandardValues[i]);
        }
        Mark("cnt1", 1);
        b.U8(StandardMembers.Length);
        for (int m = 0; m < StandardMembers.Length; m++)
        {
            (int id, int[][] entries) = StandardMembers[m];
            Mark("members[" + m + "].memberId", 2);
            b.U16(id);
            Mark("members[" + m + "].cnt2", 1);
            b.U8(entries.Length);
            for (int e = 0; e < entries.Length; e++)
            {
                string at = "members[" + m + "].entries[" + e + "]";
                Mark(at + ".A", 2);
                b.U16(entries[e][0]);
                Mark(at + ".B", 4);
                b.I32(entries[e][1]);
                Mark(at + ".C", 4);
                b.I32(entries[e][2]);
                Mark(at + ".D", 4);
                b.I32(entries[e][3]);
            }
        }
        Mark("B", 4);
        b.I32(StandardB);
        Mark("flag", 1);
        b.U8(StandardFlag);
        return b.ToArray();
    }

    // ---- 解析与断言助手 ----

    private static DungeonStartNotice ParseOk(byte[] payload)
    {
        Assert.True(DungeonStartParser.TryParse(payload, out DungeonStartNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    /// <summary>被拒:不抛异常、返回 false、notice 为 null。</summary>
    private static void AssertRejected(byte[] payload, string because = null)
    {
        DungeonStartNotice notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = DungeonStartParser.TryParse(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok, because);
        Assert.Null(notice);
    }

    private static void AssertEntry(DungeonStartEntry actual, int[] row)
    {
        Assert.Equal(row[0], actual.A);
        Assert.Equal(row[1], actual.B);
        Assert.Equal(row[2], actual.C);
        Assert.Equal(row[3], actual.D);
    }

    private static void AssertMember(DungeonStartMember actual, (int Id, int[][] Entries) expected)
    {
        Assert.Equal(expected.Id, actual.MemberId);
        Assert.Equal(expected.Entries.Length, actual.Entries.Count);
        for (int i = 0; i < expected.Entries.Length; i++)
        {
            AssertEntry(actual.Entries[i], expected.Entries[i]);
        }
    }

    /// <summary>标准载荷里每个字段的期望值,对应 <see cref="Standard"/>。</summary>
    private static void AssertStandard(DungeonStartNotice n, int trailing = 0)
    {
        Assert.Equal(StandardA, n.A);
        Assert.Equal(StandardValues.Length, n.Values.Count);
        for (int i = 0; i < StandardValues.Length; i++)
        {
            Assert.Equal(StandardValues[i], n.Values[i]);
        }
        Assert.Equal(StandardMembers.Length, n.Members.Count);
        for (int i = 0; i < StandardMembers.Length; i++)
        {
            AssertMember(n.Members[i], StandardMembers[i]);
        }
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(trailing, n.TrailingByteCount);
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
        Assert.Equal(27, DungeonStartParser.PacketId);
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
    public void Reads_the_minimal_literal_payload_in_client_order_little_endian()
    {
        byte[] payload =
        {
            0x62, 0x60, 0x01, 0x00, // A = 90210(合成值)= 0x00016062,小端
            0x00,                   // n = 0
            0x00,                   // cnt1 = 0
            0x3A, 0x9F, 0xFE, 0xFF, // B = -90310 = 0xFFFE9F3A,小端
            0x01,                   // flag
        };
        Assert.Equal(11, payload.Length);

        DungeonStartNotice n = ParseOk(payload);
        Assert.Equal(90210, n.A);
        Assert.Empty(n.Values);
        Assert.Empty(n.Members);
        Assert.Equal(-90310, n.B);
        Assert.Equal(0x01, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Reads_a_literal_payload_with_values_and_nested_members_in_client_order()
    {
        byte[] payload =
        {
            0x62, 0x60, 0x01, 0x00, // A = 90210
            0x02,                   // n = 2
            0x8F, 0x03,             // u16 = 911(合成值)= 0x038F,小端
            0x90, 0x03,             // u16 = 912
            0x02,                   // cnt1 = 2
            0x99, 0x03,             // 成员 0:memberId = 921
            0x02,                   //   cnt2 = 2
            0xA3, 0x03,             //   项 0:A(u16)= 931
            0x5C, 0xFC, 0xFF, 0xFF, //         B(i32)= -932
            0xA5, 0x03, 0x00, 0x00, //         C(i32)= 933
            0x70, 0x40, 0x0E, 0x00, //         D(i32)= 934000
            0xA7, 0x03,             //   项 1:A(u16)= 935
            0xA8, 0x03, 0x00, 0x00, //         B(i32)= 936
            0x57, 0xFC, 0xFF, 0xFF, //         C(i32)= -937
            0x10, 0x50, 0x0E, 0x00, //         D(i32)= 938000
            0x9A, 0x03,             // 成员 1:memberId = 922
            0x00,                   //   cnt2 = 0
            0x3A, 0x9F, 0xFE, 0xFF, // B = -90310
            0x01,                   // flag
        };
        Assert.Equal(49, payload.Length); // 11 + 2 * 2 + (3 + 28) + 3

        DungeonStartNotice n = ParseOk(payload);
        Assert.Equal(90210, n.A);
        Assert.Equal(2, n.Values.Count);
        Assert.Equal(911, n.Values[0]);
        Assert.Equal(912, n.Values[1]);

        Assert.Equal(2, n.Members.Count);
        DungeonStartMember first = n.Members[0];
        Assert.Equal(921, first.MemberId);
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(931, first.Entries[0].A);
        Assert.Equal(-932, first.Entries[0].B);
        Assert.Equal(933, first.Entries[0].C);
        Assert.Equal(934000, first.Entries[0].D);
        Assert.Equal(935, first.Entries[1].A);
        Assert.Equal(936, first.Entries[1].B);
        Assert.Equal(-937, first.Entries[1].C);
        Assert.Equal(938000, first.Entries[1].D);

        DungeonStartMember second = n.Members[1];
        Assert.Equal(922, second.MemberId);
        Assert.Empty(second.Entries);

        Assert.Equal(-90310, n.B);
        Assert.Equal(0x01, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Standard_payload_with_several_members_and_entries_reads_every_field()
    {
        byte[] payload = Standard();
        Assert.Equal(96, payload.Length);

        AssertStandard(ParseOk(payload));
    }

    [Fact]
    public void Minimal_payloads_have_the_documented_lengths()
    {
        // n = 0、cnt1 = 0:A 4 + n 1 + cnt1 1 + B 4 + flag 1 = 11 字节
        byte[] minimal = Minimal();
        Assert.Equal(11, minimal.Length);
        DungeonStartNotice a = ParseOk(minimal);
        Assert.Equal(StandardA, a.A);
        Assert.Empty(a.Values);
        Assert.Empty(a.Members);
        Assert.Equal(StandardB, a.B);
        Assert.Equal(StandardFlag, a.Flag);
        Assert.Equal(0, a.TrailingByteCount);

        // 多一个 u16:13 字节
        byte[] oneValue = Build(StandardA, new[] { 911 }, NoMembers, StandardB, StandardFlag);
        Assert.Equal(13, oneValue.Length);
        Assert.Equal(911, Assert.Single(ParseOk(oneValue).Values));

        // 多一个没有项的成员组:memberId 2 + cnt2 1 = 3 字节
        byte[] emptyMember = Build(StandardA, new int[0], new[] { (921, new int[0][]) }, StandardB, StandardFlag);
        Assert.Equal(14, emptyMember.Length);
        DungeonStartMember member = Assert.Single(ParseOk(emptyMember).Members);
        Assert.Equal(921, member.MemberId);
        Assert.Empty(member.Entries);

        // 多一个带一项的成员组:3 + 14 字节
        byte[] oneEntry = Build(StandardA, new int[0], new[] { (921, new[] { EntryRow(1) }) }, StandardB, StandardFlag);
        Assert.Equal(28, oneEntry.Length);
        AssertEntry(Assert.Single(Assert.Single(ParseOk(oneEntry).Members).Entries), EntryRow(1));
    }

    [Fact]
    public void Member_groups_with_different_entry_counts_keep_their_boundaries()
    {
        // 项数依次为 0、2、0、1、3、0:空组夹在非空组之间,最后一个空组紧挨着 B
        int[] entryCounts = { 0, 2, 0, 1, 3, 0 };
        var members = new (int Id, int[][] Entries)[entryCounts.Length];
        int seed = 0;
        for (int m = 0; m < entryCounts.Length; m++)
        {
            int[][] entries = new int[entryCounts[m]][];
            for (int e = 0; e < entries.Length; e++)
            {
                entries[e] = EntryRow(seed++);
            }
            members[m] = (950 + m, entries);
        }

        byte[] payload = Build(StandardA, StandardValues, members, StandardB, StandardFlag);
        Assert.Equal(11 + 6 + (6 * 3) + (14 * 6), payload.Length);

        DungeonStartNotice n = ParseOk(payload);
        Assert.Equal(members.Length, n.Members.Count);
        for (int m = 0; m < members.Length; m++)
        {
            AssertMember(n.Members[m], members[m]);
        }
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- 个数字段:u8,边界与"不设上限" ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Value_count_up_to_the_client_local_array_size_reads_exactly_n_values(int count)
    {
        int[] values = Enumerable.Range(0, count).Select(i => 900 + i).ToArray();
        DungeonStartNotice n = ParseOk(Build(StandardA, values, StandardMembers, StandardB, StandardFlag));

        Assert.Equal(count, n.Values.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(900 + i, n.Values[i]);
        }

        // 成员组、B、flag 都紧接在 n 个 u16 之后
        Assert.Equal(StandardMembers.Length, n.Members.Count);
        for (int m = 0; m < StandardMembers.Length; m++)
        {
            AssertMember(n.Members[m], StandardMembers[m]);
        }
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(16)]
    [InlineData(255)]
    public void Value_count_beyond_the_client_local_array_of_four_is_not_limited(int count)
    {
        // 客户端的局部数组只够放 4 个 u16(clire_NOTES §1.4【推】:n 应不超过 4),但解析器不做范围检查,n 声明多少就读多少个
        int[] values = Enumerable.Range(0, count).Select(i => 900 + i).ToArray();
        DungeonStartNotice n = ParseOk(Build(StandardA, values, StandardMembers, StandardB, StandardFlag));

        Assert.Equal(count, n.Values.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(900 + i, n.Values[i]);
        }
        Assert.Equal(StandardMembers.Length, n.Members.Count);
        AssertMember(n.Members[2], StandardMembers[2]);
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    public void Member_count_boundaries_read_exactly_cnt1_groups(int count)
    {
        // 第 i 组有 i % 3 项,各组取值互不相同
        var members = new (int Id, int[][] Entries)[count];
        int seed = 0;
        for (int m = 0; m < count; m++)
        {
            int[][] entries = new int[m % 3][];
            for (int e = 0; e < entries.Length; e++)
            {
                entries[e] = EntryRow(seed++ % 200);
            }
            members[m] = (900 + m, entries);
        }

        DungeonStartNotice n = ParseOk(Build(StandardA, StandardValues, members, StandardB, StandardFlag));
        Assert.Equal(count, n.Members.Count);
        for (int m = 0; m < count; m++)
        {
            AssertMember(n.Members[m], members[m]);
        }
        Assert.Equal(StandardA, n.A);
        Assert.Equal(StandardValues.Length, n.Values.Count);
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    public void Entry_count_boundaries_read_exactly_cnt2_entries(int count)
    {
        // 前面放一个没有项的成员组,后面再放一个带一项的:cnt2 读错或步长错,后面的组、B、flag 都会错位
        int[][] entries = new int[count][];
        for (int e = 0; e < count; e++)
        {
            entries[e] = EntryRow(e);
        }
        var members = new (int Id, int[][] Entries)[]
        {
            (921, new int[0][]),
            (922, entries),
            (923, new[] { EntryRow(200) }),
        };

        DungeonStartNotice n = ParseOk(Build(StandardA, StandardValues, members, StandardB, StandardFlag));
        Assert.Equal(3, n.Members.Count);
        for (int m = 0; m < members.Length; m++)
        {
            AssertMember(n.Members[m], members[m]);
        }
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void All_three_counts_at_their_one_byte_maximum_are_read()
    {
        // 255 个 u16;255 个成员组,第 i 组 i % 4 项(合计 381 项),B 与 flag 紧随其后
        int[] values = Enumerable.Range(0, 255).Select(i => 900 + i).ToArray();
        var members = new (int Id, int[][] Entries)[255];
        int seed = 0;
        for (int m = 0; m < members.Length; m++)
        {
            int[][] entries = new int[m % 4][];
            for (int e = 0; e < entries.Length; e++)
            {
                entries[e] = EntryRow(seed++ % 250);
            }
            members[m] = (900 + m, entries);
        }

        DungeonStartNotice n = ParseOk(Build(StandardA, values, members, StandardB, StandardFlag));
        Assert.Equal(255, n.Values.Count);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], n.Values[i]);
        }
        Assert.Equal(255, n.Members.Count);
        for (int m = 0; m < members.Length; m++)
        {
            AssertMember(n.Members[m], members[m]);
        }
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- 客户端的成员查找:解析器不模拟 ----

    [Fact]
    public void Member_ids_are_never_looked_up_so_every_id_is_read_with_the_same_layout()
    {
        // 客户端拿 memberId 去队伍里查成员,查不到就跳过每项的读取而不跳字节,流因此错位(clire_NOTES §1.4【观】分支存在)。
        // 解析器不查队伍、不看取值:对 0..65535 的每个 memberId,每项照读 14 字节,B 与 flag 紧接其后
        for (int id = 0; id <= 0xFFFF; id++)
        {
            byte[] payload = Build(StandardA, new int[0], new[] { (id, new[] { EntryRow(7), EntryRow(8) }) }, StandardB, StandardFlag);
            DungeonStartNotice n = ParseOk(payload);

            DungeonStartMember member = Assert.Single(n.Members);
            Assert.Equal(id, member.MemberId);
            Assert.Equal(2, member.Entries.Count);
            AssertEntry(member.Entries[0], EntryRow(7));
            AssertEntry(member.Entries[1], EntryRow(8));
            Assert.Equal(StandardB, n.B);
            Assert.Equal(StandardFlag, n.Flag);
            Assert.Equal(0, n.TrailingByteCount);
        }
    }

    // ---- 多字节字段的宽度与符号 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(0x12345678)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(-90210)]
    public void A_and_B_are_signed_little_endian_i32_each_in_its_own_place(int value)
    {
        // A、B 各取 value 与 ~value(两者恒不相等),并对调一次:读序里的位置与赋值的去向都会被核对
        DungeonStartNotice first = ParseOk(Build(value, StandardValues, StandardMembers, ~value, StandardFlag));
        Assert.Equal(value, first.A);
        Assert.Equal(~value, first.B);
        Assert.Equal(StandardFlag, first.Flag);
        Assert.Equal(0, first.TrailingByteCount);

        DungeonStartNotice second = ParseOk(Build(~value, StandardValues, StandardMembers, value, StandardFlag));
        Assert.Equal(~value, second.A);
        Assert.Equal(value, second.B);
        Assert.Equal(StandardFlag, second.Flag);
        Assert.Equal(0, second.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(911)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(65535)]
    public void List_values_and_member_ids_are_unsigned_little_endian_u16(int value)
    {
        // 列表元素与 memberId 各取 value;另给每处一个不同的值,核对位置与赋值的去向
        int other = (value + 1000) & 0xFFFF;
        var members = new (int Id, int[][] Entries)[] { (value, new int[0][]), (other, new int[0][]) };
        DungeonStartNotice n = ParseOk(Build(StandardA, new[] { value, other }, members, StandardB, StandardFlag));

        Assert.Equal(2, n.Values.Count);
        Assert.Equal(value, n.Values[0]);
        Assert.Equal(other, n.Values[1]);
        Assert.Equal(2, n.Members.Count);
        Assert.Equal(value, n.Members[0].MemberId);
        Assert.Equal(other, n.Members[1].MemberId);
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Entry_values_have_the_documented_widths_and_signs_and_order()
    {
        // 项里依次是 u16、i32、i32、i32:u16 取 0xFFFF(不能被当成有符号),三个 i32 取互不相同的边界值
        int[][] rows =
        {
            new[] { 0xFFFF, int.MinValue, int.MaxValue, -1 },
            new[] { 0, -1, int.MinValue, int.MaxValue },
            new[] { 0x8000, 0x01020304, -0x01020304, 0 },
        };
        DungeonStartNotice n = ParseOk(Build(StandardA, new int[0], new[] { (921, rows) }, StandardB, StandardFlag));

        DungeonStartMember member = Assert.Single(n.Members);
        Assert.Equal(rows.Length, member.Entries.Count);
        for (int i = 0; i < rows.Length; i++)
        {
            AssertEntry(member.Entries[i], rows[i]);
        }
        Assert.Equal(StandardB, n.B);
        Assert.Equal(StandardFlag, n.Flag);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void Flag_is_the_raw_last_byte_with_no_special_casing(int value)
    {
        // 客户端只关心 flag 是否非 0,但解析器只给原始字节,不做布尔化
        DungeonStartNotice n = ParseOk(Build(StandardA, StandardValues, StandardMembers, StandardB, value));
        Assert.Equal(value, n.Flag);
        Assert.Equal(StandardB, n.B);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- 尾巴:不消费、不核对,只报告个数 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(16)]
    public void Trailing_bytes_are_counted_but_never_consumed_or_checked(int tailLength)
    {
        // 尾巴刻意拼成"又一个 B 与 flag"、"又一项"的形状:被错吞就会改变 B、flag、各列表的个数
        byte[] tail = new byte[tailLength];
        for (int i = 0; i < tailLength; i++)
        {
            tail[i] = (byte)(0xB0 + i);
        }

        DungeonStartNotice standard = ParseOk(Standard(tail));
        AssertStandard(standard, tailLength);

        DungeonStartNotice minimal = ParseOk(Minimal(tail));
        Assert.Equal(StandardA, minimal.A);
        Assert.Empty(minimal.Values);
        Assert.Empty(minimal.Members);
        Assert.Equal(StandardB, minimal.B);
        Assert.Equal(StandardFlag, minimal.Flag);
        Assert.Equal(tailLength, minimal.TrailingByteCount);
    }

    [Fact]
    public void Trailing_hash_and_padding_shaped_tail_is_tolerated()
    {
        // 尾巴形如"4 字节校验 + 会话加密补的零":读完 flag 就停,不要求恰好读完,也不核对
        byte[] tail = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }.Concat(new byte[7]).ToArray();
        DungeonStartNotice n = ParseOk(Standard(tail));
        AssertStandard(n, 11);
    }

    [Fact]
    public void A_four_byte_integrity_tail_after_flag_is_reported_as_four_and_not_read()
    {
        // 27 不在客户端的包完整性监视表里(clire_NOTES §2.13),但服务端若仍追加 4 字节尾值,也只体现在 TrailingByteCount 上
        DungeonStartNotice n = ParseOk(Standard(0xA1, 0xB2, 0xC3, 0xD4));
        AssertStandard(n, 4);
    }

    // ---- 短包与个数超出载荷:一律拒绝,不抛 ----

    [Fact]
    public void Every_strict_prefix_of_a_valid_payload_is_rejected()
    {
        byte[][] shapes =
        {
            Minimal(),
            Build(StandardA, StandardValues, NoMembers, StandardB, StandardFlag),
            Build(StandardA, new int[0], new[] { (921, new int[0][]) }, StandardB, StandardFlag),
            Build(StandardA, new int[0], new[] { (921, new[] { EntryRow(1) }) }, StandardB, StandardFlag),
            Standard(),
        };
        foreach (byte[] full in shapes)
        {
            ParseOk(full);
            for (int len = 0; len < full.Length; len++)
            {
                AssertRejected(full.Take(len).ToArray(), "长度 " + len + "(全长 " + full.Length + ")");
            }
        }
    }

    [Fact]
    public void A_payload_cut_before_or_inside_any_field_is_rejected_with_the_field_named()
    {
        var marks = new List<(string Name, int Start, int Width)>();
        byte[] full = StandardWithFieldMarks(marks);
        Assert.Equal(Standard(), full); // 两条构造路径逐字节一致
        ParseOk(full);

        // 对每个字段:字段整个缺失、只缺最后一个字节、缺到只剩一个字节……每个中间长度都要被拒
        int cuts = 0;
        foreach ((string name, int start, int width) in marks)
        {
            for (int cut = start; cut < start + width; cut++)
            {
                AssertRejected(full.Take(cut).ToArray(), name + " 被截断在第 " + cut + " 字节(字段起点 " + start + ",宽 " + width + ")");
                cuts++;
            }
        }
        Assert.Equal(full.Length, cuts); // 各字段首尾相接,盖住了载荷的每一个字节位置
    }

    [Fact]
    public void Counts_that_exceed_the_data_are_rejected_for_each_list()
    {
        // n 声明 255,只给 20 字节数据
        AssertRejected(new NoticePayloadBuilder().I32(StandardA).U8(255).Raw(new byte[20]).ToArray());

        // cnt1 声明 255,只给两个空成员组再加 B 与 flag
        AssertRejected(new NoticePayloadBuilder().I32(StandardA).U8(0).U8(255)
            .U16(921).U8(0).U16(922).U8(0).I32(StandardB).U8(StandardFlag).ToArray());

        // cnt2 声明 255,只给两项再加 B 与 flag
        AssertRejected(new NoticePayloadBuilder().I32(StandardA).U8(0).U8(1)
            .U16(921).U8(255).U16(931).I32(1).I32(2).I32(3).U16(932).I32(4).I32(5).I32(6).I32(StandardB).U8(StandardFlag).ToArray());
    }

    [Fact]
    public void A_count_one_larger_than_the_data_is_rejected_at_every_level()
    {
        // 每一例的载荷字节数都小于"按声明个数至少需要的字节数",所以无论内容如何都必须拒绝

        // n 声明 4,只给 3 个 u16(再加 cnt1 = 0、B、flag):至少需要 5 + 8 + 1 + 4 + 1 = 19 字节,只有 17
        byte[] values = new NoticePayloadBuilder().I32(StandardA).U8(4)
            .U16(911).U16(912).U16(913).U8(0).I32(StandardB).U8(StandardFlag).ToArray();
        Assert.Equal(17, values.Length);
        AssertRejected(values);

        // cnt1 声明 2,只给一个空成员组(再加 B、flag):至少需要 6 + 2 * 3 + 5 = 17 字节,只有 14
        byte[] members = new NoticePayloadBuilder().I32(StandardA).U8(0).U8(2)
            .U16(921).U8(0).I32(StandardB).U8(StandardFlag).ToArray();
        Assert.Equal(14, members.Length);
        AssertRejected(members);

        // cnt2 声明 2,只给一项(再加 B、flag):至少需要 6 + 3 + 28 + 5 = 42 字节,只有 28
        byte[] entries = new NoticePayloadBuilder().I32(StandardA).U8(0).U8(1)
            .U16(921).U8(2).U16(931).I32(1).I32(2).I32(3).I32(StandardB).U8(StandardFlag).ToArray();
        Assert.Equal(28, entries.Length);
        AssertRejected(entries);
    }

    // ---- 容错:任意载荷都不抛,成败与长度规则一致,字段与字节对得上 ----

    [Fact]
    public void Generated_payloads_with_random_cuts_and_tails_match_the_length_rule_and_the_bytes()
    {
        // 确定性伪随机(固定种子):先随机挑结构(n 取 0..6,成员数取 0..3,每组项数取 0..3),填随机字节,再随机截断或补尾巴
        uint state = 0x2468ACE1;
        byte NextByte()
        {
            state = (state * 1664525u) + 1013904223u;
            return (byte)(state >> 24);
        }

        int accepted = 0;
        int rejected = 0;
        for (int round = 0; round < 2000; round++)
        {
            int valueCount = NextByte() % 7;
            int memberCount = NextByte() % 4;

            var b = new NoticePayloadBuilder();
            for (int i = 0; i < 4; i++)
            {
                b.U8(NextByte()); // A
            }
            b.U8(valueCount);
            for (int i = 0; i < 2 * valueCount; i++)
            {
                b.U8(NextByte());
            }
            b.U8(memberCount);
            for (int m = 0; m < memberCount; m++)
            {
                int entryCount = NextByte() % 4;
                b.U8(NextByte()).U8(NextByte()); // memberId
                b.U8(entryCount);
                for (int i = 0; i < 14 * entryCount; i++)
                {
                    b.U8(NextByte());
                }
            }
            for (int i = 0; i < 5; i++)
            {
                b.U8(NextByte()); // B、flag
            }
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
            DungeonStartNotice n = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = DungeonStartParser.TryParse(payload, out n); });
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
        int accepted = 0;
        for (int len = 0; len <= 110; len++)
        {
            for (int round = 0; round < 30; round++)
            {
                byte[] body = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    seed = (seed * 1664525u) + 1013904223u;
                    body[i] = (byte)(seed >> 24);
                }
                if (round % 3 != 0)
                {
                    ClampCounts(body); // 三分之二的样本把各个个数压小,让成功分支有足够样本
                }

                int consumed = ExpectedConsumed(body);
                DungeonStartNotice n = null;
                bool ok = false;
                Exception ex = Record.Exception(() => { ok = DungeonStartParser.TryParse(body, out n); });
                Assert.Null(ex);
                Assert.Equal(consumed >= 0, ok);
                if (consumed < 0)
                {
                    Assert.Null(n);
                    continue;
                }

                accepted++;
                Assert.NotNull(n);
                AssertMatchesBytes(body, n, consumed);
            }
        }

        Assert.True(accepted > 100);
    }

    /// <summary>
    /// 按字节数规则独立推出载荷应读掉多少字节(读不下返回 -1);不复用产品里的读取代码。
    /// A 4 字节;n 1 字节与 2n 字节;cnt1 1 字节;每个成员组 memberId 2 字节、cnt2 1 字节与 14 × cnt2 字节;最后 B 4 字节、flag 1 字节。
    /// </summary>
    private static int ExpectedConsumed(byte[] p)
    {
        int pos = 4;
        if (p.Length <= pos)           // 读不到 n
        {
            return -1;
        }
        pos += 1 + (2 * p[pos]);       // n 与 n 个 u16
        if (p.Length <= pos)           // 读不到 cnt1
        {
            return -1;
        }
        int members = p[pos];
        pos += 1;
        for (int i = 0; i < members; i++)
        {
            pos += 2;                  // memberId
            if (p.Length <= pos)       // 读不到 cnt2
            {
                return -1;
            }
            pos += 1 + (14 * p[pos]);  // cnt2 与各项
        }
        pos += 5;                      // B 与 flag
        return p.Length >= pos ? pos : -1;
    }

    /// <summary>沿着读序把能读到的个数字节压小(n 取 0..3,cnt1 与各 cnt2 取 0..2),其余字节不动。</summary>
    private static void ClampCounts(byte[] p)
    {
        int pos = 4;
        if (p.Length <= pos)
        {
            return;
        }
        p[pos] = (byte)(p[pos] % 4);
        pos += 1 + (2 * p[pos]);
        if (p.Length <= pos)
        {
            return;
        }
        p[pos] = (byte)(p[pos] % 3);
        int members = p[pos];
        pos += 1;
        for (int i = 0; i < members; i++)
        {
            pos += 2;
            if (p.Length <= pos)
            {
                return;
            }
            p[pos] = (byte)(p[pos] % 3);
            pos += 1 + (14 * p[pos]);
        }
    }

    private static int Le16(byte[] p, int at)
    {
        return p[at] | (p[at + 1] << 8);
    }

    private static int Le32(byte[] p, int at)
    {
        return p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24);
    }

    /// <summary>按偏移直接从字节里读出期望值(与产品读取代码无关),核对每个字段、各个数、读掉的字节数与尾巴个数。</summary>
    private static void AssertMatchesBytes(byte[] p, DungeonStartNotice n, int consumed)
    {
        Assert.Equal(Le32(p, 0), n.A);

        int valueCount = p[4];
        Assert.Equal(valueCount, n.Values.Count);
        for (int i = 0; i < valueCount; i++)
        {
            Assert.Equal(Le16(p, 5 + (2 * i)), n.Values[i]);
        }

        int pos = 5 + (2 * valueCount);
        int memberCount = p[pos];
        Assert.Equal(memberCount, n.Members.Count);
        pos += 1;
        for (int m = 0; m < memberCount; m++)
        {
            Assert.Equal(Le16(p, pos), n.Members[m].MemberId);
            int entryCount = p[pos + 2];
            Assert.Equal(entryCount, n.Members[m].Entries.Count);
            pos += 3;
            for (int e = 0; e < entryCount; e++)
            {
                Assert.Equal(Le16(p, pos), n.Members[m].Entries[e].A);
                Assert.Equal(Le32(p, pos + 2), n.Members[m].Entries[e].B);
                Assert.Equal(Le32(p, pos + 6), n.Members[m].Entries[e].C);
                Assert.Equal(Le32(p, pos + 10), n.Members[m].Entries[e].D);
                pos += 14;
            }
        }

        Assert.Equal(Le32(p, pos), n.B);
        Assert.Equal(p[pos + 4], n.Flag);
        Assert.Equal(pos + 5, consumed);
        Assert.Equal(p.Length - consumed, n.TrailingByteCount);
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void Notice_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        Type[] types = { typeof(DungeonStartNotice), typeof(DungeonStartMember), typeof(DungeonStartEntry) };
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
        Assert.Empty(typeof(DungeonStartNotice).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(DungeonStartMember).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(DungeonStartEntry).IsValueType);

        // 实例字段只许简单值与只读列表接口:不留载荷数组或可写集合的引用
        Type[] allowed =
        {
            typeof(byte),
            typeof(ushort),
            typeof(int),
            typeof(IReadOnlyList<ushort>),
            typeof(IReadOnlyList<DungeonStartMember>),
            typeof(IReadOnlyList<DungeonStartEntry>),
        };
        foreach (Type type in types)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.Contains(field.FieldType, allowed);
            }
        }
    }

    [Fact]
    public void The_three_lists_reject_every_write_even_through_IList()
    {
        DungeonStartNotice n = ParseOk(Standard());
        AssertListIsReadOnly(n.Values);
        AssertListIsReadOnly(n.Members);
        AssertListIsReadOnly(n.Members[0].Entries);

        // 写都失败了,内容没变
        AssertStandard(n);
    }

    [Fact]
    public void Empty_lists_are_empty_read_only_lists_and_never_null()
    {
        DungeonStartNotice n = ParseOk(Minimal());
        Assert.NotNull(n.Values);
        Assert.NotNull(n.Members);
        Assert.Empty(n.Values);
        Assert.Empty(n.Members);
        AssertListIsReadOnly(n.Values);
        AssertListIsReadOnly(n.Members);

        // 没有项的成员组:Entries 也是空的只读列表
        DungeonStartNotice withEmptyMember = ParseOk(Standard());
        DungeonStartMember empty = withEmptyMember.Members[1];
        Assert.NotNull(empty.Entries);
        Assert.Empty(empty.Entries);
        AssertListIsReadOnly(empty.Entries);
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer()
    {
        byte[] payload = Standard(1, 2, 3);
        DungeonStartNotice n = ParseOk(payload);

        // 解析之后把输入整个改写:结果不能跟着变
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        AssertStandard(n, 3);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_is_repeatable()
    {
        byte[] payload = Standard(1, 2, 3);
        byte[] before = (byte[])payload.Clone();

        DungeonStartNotice first = ParseOk(payload);
        DungeonStartNotice second = ParseOk(payload);

        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Members, second.Members);
        AssertStandard(first, 3);
        AssertStandard(second, 3);
    }
}
