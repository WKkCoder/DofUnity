using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;
using static Dnf.Net.Tests.SettlementTestPayloads;

namespace Dnf.Net.Tests;

/// <summary>
/// 通关结果通知(类 0、包号 0x22)的明文载荷解析器(纯函数)。载荷全是手工拼的合成字节(手写字节数组或 <see cref="SettlementTestPayloads"/> 的独立写入器),
/// 唯一号取 900 以上的合成值,不读任何真实帧文件,不含任何真实数值。
/// 读序依据 settlenoti_NOTES §2.3(服务端写序)与 §3.2(客户端读序,逐项吻合):
/// <c>u8 评分值, i32 通关用时, i8 用时分, i8 基础分, u8 任务迷宫标志, u8 条数, 条数 × { u16 唯一号, i32 最佳用时, u8 新纪录 }</c>,总长 = 9 + 7 × 条数;
/// 另有 7 字节全零形(<see cref="SettlementNoticeAParser.TryParseTimeoutFallback"/>;"超时兜底"是早期叫法,标识符与下面这组测试名沿用,
/// 真实服务端是否会发这个形状没有读通,见 settlenoti_NOTES §2.5)。
/// 读完最后一个条目即停,尾巴(折叠哈希、加密补零)只报告个数。
/// </summary>
public class SettlementNoticeAParserTests
{
    // 规则里的包号写字面量,不引用产品常量:产品常量被改动时,测试要能发现。
    private const ushort ResultId = 0x22;

    /// <summary>
    /// 手写的标准载荷(逐字段注释,由独立的 Python 写入器按服务端写序生成后粘贴):2 条,每个多字节字段的各字节互不相同,核对小端与字段顺序。
    /// 评分值 0xE7 与用时分 0xF6、基础分 0x9C 都大于 0x7F,专门核对无符号与带符号的取法。长度 9 + 7 × 2 = 23。
    /// </summary>
    private static readonly byte[] LiteralPayload =
    {
        0xE7,                             // [0..0] RankValue (u8) = 231
        0x78, 0x56, 0x34, 0x12,           // [1..4] ClearTime (i32) = 0x12345678
        0xF6,                             // [5..5] TimeBonus (s8) = -10
        0x9C,                             // [6..6] BaseScore (s8) = -100
        0x01,                             // [7..7] QuestMazeFlag (u8) = 1
        0x02,                             // [8..8] count (u8) = 2
        0x85, 0x03,                       // [9..10] entry0.UniqueId (u16) = 901
        0xCD, 0x81, 0x01, 0x00,           // [11..14] entry0.BestTime (i32) = 98765
        0x01,                             // [15..15] entry0.IsNewRecord (u8) = 1
        0x40, 0x9C,                       // [16..17] entry1.UniqueId (u16) = 40000
        0x40, 0xE2, 0x01, 0x00,           // [18..21] entry1.BestTime (i32) = 123456
        0x00,                             // [22..22] entry1.IsNewRecord (u8) = 0
    };

    // ---- 助手 ----

    private static SettlementNoticeA ParseOk(byte[] payload)
    {
        Assert.True(SettlementNoticeAParser.TryParse(payload, out SettlementNoticeA notice));
        Assert.NotNull(notice);
        return notice;
    }

    /// <summary>被拒:不抛异常、返回 false、notice 为 null。</summary>
    private static void AssertRejected(byte[] payload)
    {
        SettlementNoticeA notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = SettlementNoticeAParser.TryParse(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    private static SettlementNoticeA FallbackOk(byte[] payload)
    {
        Assert.True(SettlementNoticeAParser.TryParseTimeoutFallback(payload, out SettlementNoticeA notice));
        Assert.NotNull(notice);
        return notice;
    }

    private static void AssertFallbackRejected(byte[] payload)
    {
        SettlementNoticeA notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = SettlementNoticeAParser.TryParseTimeoutFallback(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    /// <summary>逐字段核对:解析结果与构造它的合成规格一致(带符号字段的符号按服务端语义)。</summary>
    private static void AssertMatches(ASpec spec, SettlementNoticeA n)
    {
        Assert.Equal((byte)spec.Rank, n.RankValue);
        Assert.Equal(spec.ClearTime, n.ClearTime);
        Assert.Equal(unchecked((sbyte)spec.TimeBonus), n.TimeBonus);
        Assert.Equal(unchecked((sbyte)spec.BaseScore), n.BaseScore);
        Assert.Equal((byte)spec.Maze, n.QuestMazeFlag);
        Assert.Equal(spec.Maze == 1, n.IsQuestMaze);
        Assert.True(n.HasBestTimeBlock);
        Assert.Equal(spec.Entries.Count, n.Entries.Count);
        for (int i = 0; i < spec.Entries.Count; i++)
        {
            Assert.Equal((ushort)spec.Entries[i].UniqueId, n.Entries[i].UniqueId);
            Assert.Equal(spec.Entries[i].BestTime, n.Entries[i].BestTime);
            Assert.Equal(spec.Entries[i].NewRecord == 1, n.Entries[i].IsNewRecord);
        }
    }

    /// <summary>第 i 条的合成取值:唯一号 900 以上、最佳用时互不相同、新纪录标志 0 与 1 轮换。</summary>
    private static List<BestTimeSpec> SyntheticEntries(int count, int variant = 0)
    {
        var list = new List<BestTimeSpec>();
        for (int i = 0; i < count; i++)
        {
            int uid = (i % 2 == 0) ? 901 + (13 * i) + variant : 40000 - (7 * i) + variant;
            list.Add(new BestTimeSpec(uid & 0xFFFF, 100000 + (977 * i) + variant, (i + variant) % 2));
        }
        return list;
    }

    // ---- 常量 ----

    [Fact]
    public void Production_constant_matches_the_documented_id()
    {
        Assert.Equal(0x22, SettlementNoticeAParser.PacketId);
        Assert.Equal(34, SettlementNoticeAParser.PacketId);
    }

    // ---- 逐字段(手写字节,不经写入器) ----

    [Fact]
    public void A_literal_payload_is_read_field_by_field()
    {
        SettlementNoticeA n = ParseOk(LiteralPayload);

        Assert.Equal(231, n.RankValue);
        Assert.Equal(0x12345678, n.ClearTime);
        Assert.Equal(-10, n.TimeBonus);
        Assert.Equal(-100, n.BaseScore);
        Assert.Equal(1, n.QuestMazeFlag);
        Assert.True(n.IsQuestMaze);
        Assert.True(n.HasBestTimeBlock);
        Assert.Equal(2, n.Entries.Count);
        Assert.Equal(901, n.Entries[0].UniqueId);
        Assert.Equal(98765, n.Entries[0].BestTime);
        Assert.True(n.Entries[0].IsNewRecord);
        Assert.Equal(40000, n.Entries[1].UniqueId);
        Assert.Equal(123456, n.Entries[1].BestTime);
        Assert.False(n.Entries[1].IsNewRecord);
        Assert.Equal(0, n.TrailingByteCount);
        Assert.Equal(9 + (7 * 2), LiteralPayload.Length);
    }

    [Fact]
    public void The_independent_writer_reproduces_the_literal_payload_byte_for_byte()
    {
        Assert.Equal(LiteralPayload, new ASpec().Build());
    }

    [Fact]
    public void Every_byte_belongs_to_exactly_one_field_and_changes_only_that_field()
    {
        // 字节位置 -> 签名下标:签名 = [评分值, 用时, 用时分, 基础分, 迷宫标志, e0.唯一号, e0.最佳, e0.新纪录, e1.唯一号, e1.最佳, e1.新纪录]
        int[] owner = new int[LiteralPayload.Length];
        owner[0] = 0;
        for (int i = 1; i <= 4; i++) { owner[i] = 1; }
        owner[5] = 2;
        owner[6] = 3;
        owner[7] = 4;
        owner[8] = -1;                                  // 个数字节:改了会改结构,另有专测
        owner[9] = 5; owner[10] = 5;
        for (int i = 11; i <= 14; i++) { owner[i] = 6; }
        owner[15] = 7;
        owner[16] = 8; owner[17] = 8;
        for (int i = 18; i <= 21; i++) { owner[i] = 9; }
        owner[22] = 10;

        object[] before = Signature(ParseOk(LiteralPayload));
        for (int pos = 0; pos < LiteralPayload.Length; pos++)
        {
            if (owner[pos] < 0)
            {
                continue;
            }

            byte[] changed = (byte[])LiteralPayload.Clone();
            changed[pos] ^= (pos == 15 || pos == 22) ? (byte)0x01 : (byte)0x5A;      // 标志字节翻 0 与 1,别的字节翻几位
            object[] after = Signature(ParseOk(changed));

            for (int s = 0; s < before.Length; s++)
            {
                Assert.Equal(s != owner[pos], Equals(before[s], after[s]));
            }
        }
    }

    private static object[] Signature(SettlementNoticeA n)
    {
        return new object[]
        {
            n.RankValue, n.ClearTime, n.TimeBonus, n.BaseScore, n.QuestMazeFlag,
            n.Entries[0].UniqueId, n.Entries[0].BestTime, n.Entries[0].IsNewRecord,
            n.Entries[1].UniqueId, n.Entries[1].BestTime, n.Entries[1].IsNewRecord,
        };
    }

    [Fact]
    public void Each_field_takes_independent_values_from_the_independent_writer()
    {
        var spec = new ASpec
        {
            Rank = 0x11,
            ClearTime = 0x01020304,
            TimeBonus = 0x2B,
            BaseScore = 0x4D,
            Maze = 0,
            Entries = SyntheticEntries(3, variant: 5),
        };

        AssertMatches(spec, ParseOk(spec.Build()));
    }

    // ---- 无符号与带符号的取法 ----

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x7F, 127)]
    [InlineData(0x80, 128)]
    [InlineData(0xE7, 231)]
    [InlineData(0xFF, 255)]
    public void The_rank_value_is_unsigned(int wire, int expected)
    {
        SettlementNoticeA n = ParseOk(new ASpec { Rank = wire, Entries = new List<BestTimeSpec>() }.Build());

        Assert.Equal(expected, n.RankValue);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x7F, 127)]
    [InlineData(0x80, -128)]
    [InlineData(0xF6, -10)]
    [InlineData(0xFF, -1)]
    public void The_time_bonus_is_a_signed_byte_as_the_server_sign_extends_it(int wire, int expected)
    {
        SettlementNoticeA n = ParseOk(new ASpec { TimeBonus = wire, Entries = new List<BestTimeSpec>() }.Build());

        Assert.Equal(expected, n.TimeBonus);
        Assert.Equal(typeof(sbyte), typeof(SettlementNoticeA).GetProperty(nameof(SettlementNoticeA.TimeBonus)).PropertyType);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x7F, 127)]
    [InlineData(0x80, -128)]
    [InlineData(0x9C, -100)]
    [InlineData(0xFF, -1)]
    public void The_base_score_is_a_signed_byte_as_the_server_sign_extends_it(int wire, int expected)
    {
        SettlementNoticeA n = ParseOk(new ASpec { BaseScore = wire, Entries = new List<BestTimeSpec>() }.Build());

        Assert.Equal(expected, n.BaseScore);
        Assert.Equal(typeof(sbyte), typeof(SettlementNoticeA).GetProperty(nameof(SettlementNoticeA.BaseScore)).PropertyType);
    }

    [Fact]
    public void The_rank_point_arithmetic_uses_the_signed_readings_like_the_server()
    {
        // 服务端 calRankPoint(用时分, 基础分):两个都是带符号字节扩展后相加。用时分 0xF6 = -10,基础分 0x64 = 100 → 90;
        // 若误按无符号读,会得到 246 + 100 = 346。
        SettlementNoticeA n = ParseOk(new ASpec { TimeBonus = 0xF6, BaseScore = 0x64, Entries = new List<BestTimeSpec>() }.Build());

        Assert.Equal(90, n.TimeBonus + n.BaseScore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x100)]
    [InlineData(0x10000)]
    [InlineData(0x01000000)]
    [InlineData(0x7FFFFFFF)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void The_clear_time_and_best_times_are_signed_32_bit_little_endian(int value)
    {
        var spec = new ASpec { ClearTime = value, Entries = new List<BestTimeSpec> { new BestTimeSpec(901, value, 1), new BestTimeSpec(902, ~value, 0) } };

        SettlementNoticeA n = ParseOk(spec.Build());

        Assert.Equal(value, n.ClearTime);
        Assert.Equal(value, n.Entries[0].BestTime);
        Assert.Equal(~value, n.Entries[1].BestTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x0100)]
    [InlineData(0x7FFF)]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public void The_unique_id_is_unsigned_16_bit(int value)
    {
        SettlementNoticeA n = ParseOk(new ASpec { Entries = new List<BestTimeSpec> { new BestTimeSpec(value, 5, 0) } }.Build());

        Assert.Equal(value, (int)n.Entries[0].UniqueId);
        Assert.Equal(typeof(ushort), typeof(SettlementBestTimeEntry).GetProperty(nameof(SettlementBestTimeEntry.UniqueId)).PropertyType);
    }

    // ---- "等于 1"的判定(与客户端 CMP byte,1 同形) ----

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(0x80, false)]
    [InlineData(0xFF, false)]
    public void The_new_record_flag_and_the_quest_maze_flag_are_true_only_when_the_byte_equals_one(int wire, bool expected)
    {
        var spec = new ASpec { Maze = wire, Entries = new List<BestTimeSpec> { new BestTimeSpec(901, 5, wire) } };

        SettlementNoticeA n = ParseOk(spec.Build());

        Assert.Equal(expected, n.IsQuestMaze);
        Assert.Equal(wire, n.QuestMazeFlag);                 // 原始字节另有保留
        Assert.Equal(expected, n.Entries[0].IsNewRecord);
    }

    // ---- 条数边界 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(255)]
    public void Entry_counts_at_the_boundaries_are_read_exactly(int count)
    {
        var spec = new ASpec { Entries = SyntheticEntries(count, variant: count % 7) };
        byte[] payload = spec.Build();
        Assert.Equal(9 + (7 * count), payload.Length);

        SettlementNoticeA n = ParseOk(payload);

        AssertMatches(spec, n);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Zero_entries_is_a_valid_empty_table_and_never_null()
    {
        byte[] head = new ASpec { Entries = new List<BestTimeSpec>() }.Build();
        Assert.Equal(9, head.Length);

        SettlementNoticeA n = ParseOk(head);

        Assert.NotNull(n.Entries);
        Assert.Empty(n.Entries);
        Assert.Equal(0, n.TrailingByteCount);
        Assert.True(n.HasBestTimeBlock);
        AssertReadOnlyWrapper(n.Entries);

        // 条数为 0 时后面跟什么都不读
        SettlementNoticeA withTail = ParseOk(new ASpec { Entries = new List<BestTimeSpec>() }.Build(0x85, 0x03, 0x01, 0xEE));
        Assert.Empty(withTail.Entries);
        Assert.Equal(4, withTail.TrailingByteCount);
    }

    [Fact]
    public void More_than_four_entries_are_all_read_the_four_entry_cap_is_a_client_behaviour_not_modelled()
    {
        var spec = new ASpec { Entries = SyntheticEntries(6, variant: 2) };

        SettlementNoticeA n = ParseOk(spec.Build());

        AssertMatches(spec, n);
        Assert.Equal(6, n.Entries.Count);
    }

    // ---- 截断与条数越界 ----

    [Fact]
    public void Every_proper_prefix_of_a_payload_is_rejected()
    {
        for (int len = 0; len < LiteralPayload.Length; len++)
        {
            AssertRejected(LiteralPayload.Take(len).ToArray());
        }
        Assert.True(SettlementNoticeAParser.TryParse(LiteralPayload, out _));
    }

    [Fact]
    public void A_truncation_inside_any_field_is_rejected()
    {
        byte[] payload = new ASpec { Entries = SyntheticEntries(5, variant: 3) }.Build();
        Assert.Equal(9 + 35, payload.Length);
        for (int len = 0; len < payload.Length; len++)
        {
            AssertRejected(payload.Take(len).ToArray());
        }
        Assert.True(SettlementNoticeAParser.TryParse(payload, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(255, 0)]
    [InlineData(255, 1)]
    [InlineData(255, 254)]
    public void A_declared_count_beyond_the_payload_is_rejected(int declared, int actualEntries)
    {
        var spec = new ASpec { Entries = SyntheticEntries(actualEntries), DeclaredCount = declared };

        AssertRejected(spec.Build());
    }

    [Fact]
    public void A_declared_count_one_above_or_one_below_the_actual_entries_is_handled_exactly()
    {
        // 声明比实际多 1:整包拒绝;声明比实际少 1:读完声明的条数就停,多出的一条整条(7 字节)算尾巴
        List<BestTimeSpec> entries = SyntheticEntries(4, variant: 5);
        AssertRejected(new ASpec { Entries = entries, DeclaredCount = 5 }.Build());

        var below = new ASpec { Entries = entries, DeclaredCount = 3 };
        SettlementNoticeA n = ParseOk(below.Build());
        Assert.Equal(3, n.Entries.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((ushort)entries[i].UniqueId, n.Entries[i].UniqueId);
            Assert.Equal(entries[i].BestTime, n.Entries[i].BestTime);
        }
        Assert.Equal(7, n.TrailingByteCount);
    }

    [Fact]
    public void Null_and_tiny_payloads_are_rejected_without_throwing()
    {
        AssertRejected(null);
        AssertRejected(new byte[0]);
        for (int len = 1; len < 9; len++)
        {
            AssertRejected(new byte[len]);                    // 头不足 9 字节
        }

        // 恰好 9 个零字节:头读完、条数为 0,是合法的空表
        SettlementNoticeA zero = ParseOk(new byte[9]);
        Assert.Empty(zero.Entries);
        Assert.Equal(0, zero.TrailingByteCount);
        Assert.True(zero.HasBestTimeBlock);
    }

    // ---- 尾巴 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(255)]
    [InlineData(1000)]
    public void Trailing_bytes_of_any_length_are_tolerated_and_counted_not_consumed(int tailLength)
    {
        byte[] tail = new byte[tailLength];
        for (int i = 0; i < tail.Length; i++)
        {
            tail[i] = (byte)(0xA0 + (i * 7));
        }
        var spec = new ASpec { Entries = SyntheticEntries(3, variant: 2) };

        SettlementNoticeA n = ParseOk(spec.Build(tail));

        AssertMatches(spec, n);
        Assert.Equal(tailLength, n.TrailingByteCount);
    }

    [Fact]
    public void A_server_style_fold_hash_tail_and_encryption_zero_padding_are_both_just_trailing_bytes()
    {
        var spec = new ASpec { Entries = SyntheticEntries(1, variant: 1) };
        byte[] body = spec.Build();
        Assert.Equal(16, body.Length);
        byte[] withHash = PacketIntegrity.AppendFoldHash(body);
        Assert.Equal(20, withHash.Length);                  // 载荷长度在 1 到 0x190 之间,服务端追加 4 字节折叠哈希

        SettlementNoticeA hashed = ParseOk(withHash);
        AssertMatches(spec, hashed);
        Assert.Equal(4, hashed.TrailingByteCount);

        SettlementNoticeA padded = ParseOk(withHash.Concat(new byte[4]).ToArray());      // 补到 8 字节的倍数一类的加密补零
        AssertMatches(spec, padded);
        Assert.Equal(8, padded.TrailingByteCount);
    }

    // ---- 取值不做检查、不改写 ----

    [Fact]
    public void Values_are_never_range_checked_reordered_or_deduplicated()
    {
        var spec = new ASpec
        {
            Rank = 0xFF,
            ClearTime = int.MinValue,
            TimeBonus = 0x80,
            BaseScore = 0x80,
            Maze = 0xFF,
            Entries = new List<BestTimeSpec>
            {
                new BestTimeSpec(65535, int.MaxValue, 0xFF), new BestTimeSpec(0, 0, 0), new BestTimeSpec(901, 1, 1), new BestTimeSpec(901, 2, 1), new BestTimeSpec(65535, -1, 2),
            },
        };

        AssertMatches(spec, ParseOk(spec.Build()));
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void Record_types_expose_exactly_the_documented_members_and_types()
    {
        string[] noticeProperties = typeof(SettlementNoticeA).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[] { "BaseScore", "ClearTime", "Entries", "HasBestTimeBlock", "IsQuestMaze", "QuestMazeFlag", "RankValue", "TimeBonus", "TrailingByteCount" },
            noticeProperties);

        string[] entryProperties = typeof(SettlementBestTimeEntry).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "BestTime", "IsNewRecord", "UniqueId" }, entryProperties);

        Assert.Equal(typeof(byte), typeof(SettlementNoticeA).GetProperty("RankValue").PropertyType);
        Assert.Equal(typeof(int), typeof(SettlementNoticeA).GetProperty("ClearTime").PropertyType);
        Assert.Equal(typeof(byte), typeof(SettlementNoticeA).GetProperty("QuestMazeFlag").PropertyType);
        Assert.Equal(typeof(int), typeof(SettlementBestTimeEntry).GetProperty("BestTime").PropertyType);
    }

    [Fact]
    public void Record_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        Type[] types = { typeof(SettlementNoticeA), typeof(SettlementBestTimeEntry) };
        foreach (Type type in types)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.Null(property.GetSetMethod(true));
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(field.IsInitOnly);
                Assert.False(field.IsPublic);
            }
        }

        Assert.Empty(typeof(SettlementNoticeA).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(SettlementBestTimeEntry).IsValueType);

        // 实例字段只许简单值与只读列表接口:不留载荷数组或可写集合给外部
        Type[] allowed =
        {
            typeof(byte), typeof(sbyte), typeof(ushort), typeof(int), typeof(bool), typeof(IReadOnlyList<SettlementBestTimeEntry>),
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
    public void The_entry_list_rejects_every_write_even_through_IList()
    {
        SettlementNoticeA n = ParseOk(LiteralPayload);

        AssertReadOnlyWrapper(n.Entries);
        Assert.Equal(901, n.Entries[0].UniqueId);
        Assert.Equal(40000, n.Entries[1].UniqueId);
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer_and_parsing_leaves_the_input_untouched()
    {
        byte[] payload = new ASpec { Entries = SyntheticEntries(4, variant: 9) }.Build(1, 2, 3);
        byte[] before = (byte[])payload.Clone();

        SettlementNoticeA first = ParseOk(payload);
        SettlementNoticeA second = ParseOk(payload);
        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Entries, second.Entries);

        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        AssertMatches(new ASpec { Entries = SyntheticEntries(4, variant: 9) }, first);
        Assert.Equal(3, first.TrailingByteCount);
    }

    // ---- 真实解码路径 ----

    [Fact]
    public void A_decoded_frame_carries_the_documented_header_and_its_payload_parses_with_the_fold_hash_tail()
    {
        var spec = new ASpec { Entries = SyntheticEntries(2, variant: 4) };
        byte[] body = PacketIntegrity.AppendFoldHash(spec.Build());

        DecodedFrame frame = PktImplTestKit.Frame(Noti, ResultId, body);

        Assert.Equal(PacketClass.Noti, frame.Header.Class);
        Assert.Equal(ResultId, frame.Header.PacketId);
        Assert.Equal(SettlementNoticeAParser.PacketId, frame.Header.PacketId);
        SettlementNoticeA n = ParseOk(frame.Payload);
        AssertMatches(spec, n);
        Assert.Equal(4, n.TrailingByteCount);
    }

    // ---- 随机对拍:长度规则独立推出 ----

    [Fact]
    public void Random_bytes_never_throw_and_agree_with_the_independent_length_rule()
    {
        uint seed = 0x00C0FFEE;
        int accepted = 0;
        for (int round = 0; round < 4000; round++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            int len = (int)((seed >> 16) % 60);
            byte[] body = new byte[len];
            for (int i = 0; i < len; i++)
            {
                seed = (seed * 1664525u) + 1013904223u;
                body[i] = (byte)(seed >> 24);
            }
            if (round % 3 != 0 && len >= 9)
            {
                // 三分之二的样本把条数压小,让成功分支有足够样本
                body[8] = (byte)(body[8] % 6);
            }

            // 独立规则:第 9 个字节(下标 8)是条数;能读下 9 + 7 × 条数字节才算成功
            int expectedLength = len >= 9 ? 9 + (7 * body[8]) : -1;
            bool expectedOk = len >= 9 && len >= expectedLength;

            SettlementNoticeA n = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = SettlementNoticeAParser.TryParse(body, out n); });
            Assert.Null(ex);
            Assert.Equal(expectedOk, ok);
            if (!expectedOk)
            {
                Assert.Null(n);
                continue;
            }

            accepted++;
            Assert.Equal(body[0], n.RankValue);
            Assert.Equal(body[1] | (body[2] << 8) | (body[3] << 16) | (body[4] << 24), n.ClearTime);
            Assert.Equal(unchecked((sbyte)body[5]), n.TimeBonus);
            Assert.Equal(unchecked((sbyte)body[6]), n.BaseScore);
            Assert.Equal(body[7], n.QuestMazeFlag);
            Assert.Equal(body[8], n.Entries.Count);
            for (int e = 0; e < body[8]; e++)
            {
                int at = 9 + (7 * e);
                Assert.Equal(body[at] | (body[at + 1] << 8), n.Entries[e].UniqueId);
                Assert.Equal(body[at + 2] | (body[at + 3] << 8) | (body[at + 4] << 16) | (body[at + 5] << 24), n.Entries[e].BestTime);
                Assert.Equal(body[at + 6] == 1, n.Entries[e].IsNewRecord);
            }
            Assert.Equal(len - expectedLength, n.TrailingByteCount);
        }

        Assert.True(accepted > 300);
    }

    // ---- 7 字节全零形(测试名里的 timeout_fallback 是早期叫法) ----

    [Fact]
    public void The_timeout_fallback_of_seven_zero_bytes_is_its_own_shape()
    {
        SettlementNoticeA n = FallbackOk(new byte[7]);

        Assert.False(n.HasBestTimeBlock);
        Assert.Equal(0, n.RankValue);
        Assert.Equal(0, n.ClearTime);
        Assert.Equal(0, n.TimeBonus);
        Assert.Equal(0, n.BaseScore);
        Assert.Equal(0, n.QuestMazeFlag);
        Assert.False(n.IsQuestMaze);
        Assert.NotNull(n.Entries);
        Assert.Empty(n.Entries);
        AssertReadOnlyWrapper(n.Entries);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(100)]
    public void The_timeout_fallback_tolerates_a_tail_and_counts_it(int tailLength)
    {
        byte[] tail = Enumerable.Range(0, tailLength).Select(i => (byte)(0x40 + i)).ToArray();

        SettlementNoticeA n = FallbackOk(new byte[7].Concat(tail).ToArray());

        Assert.False(n.HasBestTimeBlock);
        Assert.Equal(tailLength, n.TrailingByteCount);
    }

    [Fact]
    public void The_timeout_fallback_with_a_fold_hash_tail_has_four_trailing_bytes()
    {
        byte[] withHash = PacketIntegrity.AppendFoldHash(new byte[7]);
        Assert.Equal(11, withHash.Length);

        SettlementNoticeA n = FallbackOk(withHash);

        Assert.Equal(4, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(0, 0xFF)]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x80)]
    [InlineData(3, 0x5A)]
    [InlineData(4, 0x01)]
    [InlineData(4, 0xFF)]
    [InlineData(5, 0x01)]
    [InlineData(5, 0xF6)]
    [InlineData(6, 0x01)]
    [InlineData(6, 0x9C)]
    public void The_timeout_fallback_requires_all_seven_head_bytes_to_be_zero(int position, int value)
    {
        byte[] body = new byte[7];
        body[position] = (byte)value;

        AssertFallbackRejected(body);
        AssertFallbackRejected(body.Concat(new byte[] { 0, 0 }).ToArray());
    }

    [Fact]
    public void The_timeout_fallback_rejects_short_and_null_payloads()
    {
        AssertFallbackRejected(null);
        for (int len = 0; len < 7; len++)
        {
            AssertFallbackRejected(new byte[len]);
        }
    }

    [Fact]
    public void The_two_entry_points_are_independent_and_the_caller_chooses()
    {
        // 7 个零字节:严格形读不下(头要 9 字节),7 字节形读得下
        AssertRejected(new byte[7]);
        Assert.True(SettlementNoticeAParser.TryParseTimeoutFallback(new byte[7], out _));

        // 一份正常载荷:严格形读得下,7 字节形因头不是全零拒绝
        Assert.True(SettlementNoticeAParser.TryParse(LiteralPayload, out _));
        AssertFallbackRejected(LiteralPayload);

        // 9 个零字节:两个入口都成功,但形状不同——这是文档里说明的歧义,由调用方按场景选
        SettlementNoticeA strict = ParseOk(new byte[9]);
        SettlementNoticeA fallback = FallbackOk(new byte[9]);
        Assert.True(strict.HasBestTimeBlock);
        Assert.False(fallback.HasBestTimeBlock);
        Assert.Equal(0, strict.TrailingByteCount);
        Assert.Equal(2, fallback.TrailingByteCount);
    }

    [Fact]
    public void The_fallback_result_is_read_only_and_does_not_alias_the_input()
    {
        byte[] payload = new byte[9];
        SettlementNoticeA n = FallbackOk(payload);
        payload[0] = 0xEE;
        payload[8] = 0xEE;

        Assert.Equal(0, n.RankValue);
        Assert.Equal(2, n.TrailingByteCount);
        Assert.NotSame(n, FallbackOk(new byte[9]));
    }
}
