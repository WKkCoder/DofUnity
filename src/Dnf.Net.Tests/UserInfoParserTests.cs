using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.UserInfoTestData;

namespace Dnf.Net.Tests;

/// <summary>
/// 用户信息通知(类 0、包号 2)模式 2(角色选择列表)明文解析器:纯函数。载荷全是手工拼的合成字节
/// (名字是不成文本的字节序列,装备编号 ≥ 900,其余字段是任意取值),读序以任务给定的偏移表为准、在测试里用字面量写死,
/// 不引用产品常量(产品常量被改动时测试要能发现)。其中一份整包是逐字节手写的,拼包函数也要与它逐字节一致。
/// 不读任何真实帧文件;不含真实角色名或数值。
/// </summary>
public class UserInfoParserTests
{
    private static UserInfoNotice ParseOk(byte[] payload)
    {
        bool ok = UserInfoParser.TryParse(payload, out UserInfoNotice notice, out UserInfoParseStatus status);
        Assert.True(ok);
        Assert.Equal(UserInfoParseStatus.Ok, status);
        Assert.NotNull(notice);

        // 两个参数的重载与三个参数的重载同结论
        Assert.True(UserInfoParser.TryParse(payload, out UserInfoNotice again));
        Assert.NotNull(again);
        Assert.Equal(notice.Entries.Count, again.Entries.Count);
        Assert.Equal(notice.TrailingByteCount, again.TrailingByteCount);
        return notice;
    }

    private static void AssertRejected(byte[] payload, UserInfoParseStatus expected)
    {
        bool ok = true;
        UserInfoNotice notice = null;
        UserInfoParseStatus status = UserInfoParseStatus.Ok;
        Exception ex = Record.Exception(() => { ok = UserInfoParser.TryParse(payload, out notice, out status); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
        Assert.Equal(expected, status);

        Assert.False(UserInfoParser.TryParse(payload, out UserInfoNotice twoArguments));
        Assert.Null(twoArguments);
    }

    private static void AssertListIsReadOnly<T>(IReadOnlyList<T> readOnly)
    {
        IList<T> list = Assert.IsAssignableFrom<IList<T>>(readOnly);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => { list.Add(default); });
        Assert.Throws<NotSupportedException>(() => { list.Insert(0, default); });
        Assert.Throws<NotSupportedException>(() => { list[0] = default; });
        Assert.Throws<NotSupportedException>(() => { list.RemoveAt(0); });
        Assert.Throws<NotSupportedException>(() => { list.Clear(); });
    }

    /// <summary>
    /// 逐字节手写的整包(不经任何拼包函数):头 10 字节 + 1 个条目(名字 5 字节、装备 2 条)= 10 + (32 + 5 + 22 × 2) = 91 字节。
    /// 每个字段的值都互不相同;u16、u32 按小端写出。
    /// </summary>
    private static byte[] HandLaidPacket()
    {
        return new byte[]
        {
            0x02,                                           // mode
            0x18, 0x06, 0x0B,                               // a, b, c
            0x0D, 0x0C, 0x0B, 0x0A,                         // d = 0x0A0B0C0D
            0x01, 0x00,                                     // count = 1
            0x01, 0x02,                                     // uid = 0x0201
            0x05, 0x00, 0x00, 0x00,                         // 名字长度 L = 5
            0xC1, 0xC2, 0xC3, 0xC4, 0xC5,                   // 名字原始字节
            0x21,                                           // X1
            0x32,                                           // job
            0x25,                                           // growType 字节
            0x3D,                                           // level
            0x44,                                           // D
            0x04,                                           // status
            0x02,                                           // 装备条数 n = 2
            0x03, 0x85, 0x03, 0x01, 0x00,                   // 装备 0:slot、itemId = 0x00010385
            0x04, 0x00, 0x00, 0x00, 0xB1, 0xB2, 0xB3, 0xB4, //   blobLen = 4、blob
            0x2D,                                           //   packed
            0x68, 0x24, 0x57, 0x13,                         //   clearAvatar = 0x13572468
            0xE0, 0xAC, 0x68, 0x24,                         //   extra = 0x2468ACE0
            0x0A, 0xE8, 0x03, 0x00, 0x00,                   // 装备 1:slot、itemId = 1000
            0x04, 0x00, 0x00, 0x00, 0xD1, 0xD2, 0xD3, 0xD4, //   blobLen = 4、blob
            0x5E,                                           //   packed
            0x00, 0x00, 0x00, 0x00,                         //   clearAvatar = 0
            0xFF, 0xFF, 0xFF, 0xFF,                         //   extra = 0xFFFFFFFF
            0x57,                                           // G
            0x68,                                           // H
            0x44, 0x33, 0x22, 0x11,                         // I = 0x11223344
            0x01,                                           // J
            0x02,                                           // premium
            0x88, 0x77, 0x66, 0x55,                         // K = 0x55667788
            0x7A, 0x7B, 0x7C,                               // a2, b2, c2
            0xCC, 0xBB, 0xAA, 0x99,                         // d2 = 0x99AABBCC
        };
    }

    private static EntrySpec HandLaidSpec()
    {
        var e = new EntrySpec();
        e.Equips.Add(new EquipSpec());
        e.Equips.Add(new EquipSpec
        {
            Slot = 0x0A,
            ItemId = 1000,
            Blob = new byte[] { 0xD1, 0xD2, 0xD3, 0xD4 },
            Packed = 0x5E,
            ClearAvatar = 0,
            Extra = 0xFFFFFFFF,
        });
        return e;
    }

    // ---- 常量 ----

    [Fact]
    public void Production_constants_match_the_documented_ids()
    {
        Assert.Equal(2, UserInfoParser.PacketId);
        Assert.Equal(2, UserInfoParser.CharacterListMode);
    }

    // ---- 模式 2 全字段 ----

    [Fact]
    public void Hand_laid_bytes_read_every_field_from_its_own_offset()
    {
        byte[] bytes = HandLaidPacket();
        Assert.Equal(91, bytes.Length);

        UserInfoNotice n = ParseOk(bytes);

        Assert.Equal(0x18, n.A);
        Assert.Equal(0x06, n.B);
        Assert.Equal(0x0B, n.C);
        Assert.Equal(0x0A0B0C0Du, n.D);
        UserInfoEntry e = Assert.Single(n.Entries);
        Assert.Equal(0x0201, e.Uid);
        Assert.Equal(new byte[] { 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 }, e.NameBytes.ToArray());
        Assert.Equal(0x21, e.X1);
        Assert.Equal(0x32, e.Job);
        Assert.Equal(0x25, e.GrowTypeByte);
        Assert.Equal(5, e.FirstGrowType);
        Assert.Equal(2, e.SecondGrowType);
        Assert.Equal(0x3D, e.Level);
        Assert.Equal(0x44, e.D);
        Assert.Equal(0x04, e.Status);
        Assert.Equal(2, e.Equips.Count);

        UserInfoEquip first = e.Equips[0];
        Assert.Equal(0x03, first.Slot);
        Assert.Equal(0x00010385u, first.ItemId);
        Assert.Equal(0xB4B3B2B1u, first.Blob);
        Assert.Equal(0x2D, first.Packed);
        Assert.Equal(0x13572468u, first.ClearAvatar);
        Assert.Equal(0x2468ACE0u, first.Extra);

        UserInfoEquip second = e.Equips[1];
        Assert.Equal(0x0A, second.Slot);
        Assert.Equal(1000u, second.ItemId);
        Assert.Equal(0xD4D3D2D1u, second.Blob);
        Assert.Equal(0x5E, second.Packed);
        Assert.Equal(0u, second.ClearAvatar);
        Assert.Equal(0xFFFFFFFFu, second.Extra);

        Assert.Equal(0x57, e.G);
        Assert.Equal(0x68, e.H);
        Assert.Equal(0x11223344u, e.I);
        Assert.Equal(0x01, e.J);
        Assert.Equal(0x02, e.Premium);
        Assert.Equal(0x55667788u, e.K);
        Assert.Equal(0x7A, e.A2);
        Assert.Equal(0x7B, e.B2);
        Assert.Equal(0x7C, e.C2);
        Assert.Equal(0x99AABBCCu, e.D2);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void The_packet_builder_agrees_with_the_hand_laid_bytes()
    {
        // 其余测试都靠拼包函数拼载荷:先证明它与逐字节手写的整包完全一致
        Assert.Equal(HandLaidPacket(), Packet(HandLaidSpec()));
    }

    [Fact]
    public void Entry_built_from_spec_matches_every_field()
    {
        EntrySpec spec = HandLaidSpec();
        UserInfoNotice n = ParseOk(Packet(spec));
        AssertEntry(spec, Assert.Single(n.Entries));
    }

    // ---- 0 个条目 ----

    [Fact]
    public void Zero_entries_gives_an_empty_read_only_list_and_no_trailing_bytes()
    {
        byte[] payload = Packet();
        Assert.Equal(10, payload.Length);

        UserInfoNotice n = ParseOk(payload);
        Assert.NotNull(n.Entries);
        Assert.Empty(n.Entries);
        Assert.Equal(0x18, n.A);
        Assert.Equal(0x06, n.B);
        Assert.Equal(0x0B, n.C);
        Assert.Equal(0x0A0B0C0Du, n.D);
        Assert.Equal(0, n.TrailingByteCount);
        AssertListIsReadOnly(n.Entries);
    }

    [Fact]
    public void Count_zero_leaves_everything_after_the_header_as_trailing_bytes()
    {
        // 声明 0 个条目:后面哪怕跟着一个完整条目,也不当条目读
        byte[] entry = Entry(Standard(0, 5, 1));
        byte[] payload = WithTail(Header(2, 1, 2, 3, 4, 0), entry);

        UserInfoNotice n = ParseOk(payload);
        Assert.Empty(n.Entries);
        Assert.Equal(entry.Length, n.TrailingByteCount);
    }

    // ---- 多个条目 ----

    [Fact]
    public void Multiple_entries_are_read_in_wire_order_each_from_its_own_boundary()
    {
        EntrySpec[] specs =
        {
            Standard(0, 0, 0),
            Standard(1, 7, 1),
            Standard(2, 30, 3),
            Standard(3, 1, 0),
        };
        byte[] payload = Packet(specs);
        Assert.Equal(10 + EntryLength(0, 0) + EntryLength(7, 1) + EntryLength(30, 3) + EntryLength(1, 0), payload.Length);

        UserInfoNotice n = ParseOk(payload);
        Assert.Equal(specs.Length, n.Entries.Count);
        for (int i = 0; i < specs.Length; i++)
        {
            AssertEntry(specs[i], n.Entries[i]);
        }
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void The_entry_count_is_a_u16_not_a_byte()
    {
        // 300 = 0x012C:只读低字节会得到 44
        EntrySpec[] specs = new EntrySpec[300];
        for (int i = 0; i < specs.Length; i++)
        {
            specs[i] = Standard(i, i % 4, 0);
        }

        UserInfoNotice n = ParseOk(Packet(specs));
        Assert.Equal(300, n.Entries.Count);
        AssertEntry(specs[0], n.Entries[0]);
        AssertEntry(specs[255], n.Entries[255]);
        AssertEntry(specs[256], n.Entries[256]);
        AssertEntry(specs[299], n.Entries[299]);
    }

    [Fact]
    public void A_full_character_slot_list_is_read()
    {
        // 栏位容量 36 个、每条名字与装备条数不一
        EntrySpec[] specs = new EntrySpec[36];
        for (int i = 0; i < specs.Length; i++)
        {
            specs[i] = Standard(i, i % 31, i % 4);
        }

        UserInfoNotice n = ParseOk(Packet(specs));
        Assert.Equal(36, n.Entries.Count);
        for (int i = 0; i < specs.Length; i++)
        {
            AssertEntry(specs[i], n.Entries[i]);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(100, 5)]
    [InlineData(255, 255)]
    public void Entry_length_is_32_plus_name_bytes_plus_22_per_equip(int nameLength, int equipCount)
    {
        byte[] oneEntry = Packet(Standard(0, nameLength, equipCount));
        Assert.Equal(10 + 32 + nameLength + (22 * equipCount), oneEntry.Length);

        UserInfoNotice exact = ParseOk(oneEntry);
        Assert.Equal(0, exact.TrailingByteCount);
        Assert.Equal(nameLength, Assert.Single(exact.Entries).NameBytes.Count);
        Assert.Equal(equipCount, Assert.Single(exact.Entries).Equips.Count);

        // 读完恰好停在公式给出的位置:多一个字节就是一个字节的尾巴
        Assert.Equal(1, ParseOk(WithTail(oneEntry, 0xEE)).TrailingByteCount);
    }

    [Fact]
    public void The_minimal_entry_is_exactly_32_bytes_and_a_count_of_one_accepts_it()
    {
        EntrySpec minimal = Standard(0, 0, 0);
        byte[] entry = Entry(minimal);
        Assert.Equal(32, entry.Length);

        UserInfoNotice n = ParseOk(WithTail(Header(2, 1, 2, 3, 4, 1), entry));
        AssertEntry(minimal, Assert.Single(n.Entries));
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- 装备子表 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(255)]
    public void Equip_sub_table_reads_that_many_22_byte_records_and_the_fields_after_it_stay_aligned(int equipCount)
    {
        EntrySpec spec = Standard(2, 6, equipCount);
        UserInfoNotice n = ParseOk(Packet(spec, Standard(3, 4, 0)));

        Assert.Equal(2, n.Entries.Count);
        AssertEntry(spec, n.Entries[0]);
        AssertEntry(Standard(3, 4, 0), n.Entries[1]);
    }

    [Fact]
    public void Equip_fields_each_come_from_their_own_offset_and_are_little_endian()
    {
        // 记录里 9 个字节宽度各异的字段取值互不相同;一个字段错位,后面全部跟着错
        EntrySpec spec = Standard(0, 3, 1);
        spec.Equips[0] = new EquipSpec
        {
            Slot = 0x11,
            ItemId = 0x01020384,
            Blob = new byte[] { 0x0A, 0x0B, 0x0C, 0x0D },
            Packed = 0x77,
            ClearAvatar = 0x05060708,
            Extra = 0x090A0B0C,
        };

        UserInfoEquip q = Assert.Single(Assert.Single(ParseOk(Packet(spec)).Entries).Equips);
        Assert.Equal(0x11, q.Slot);
        Assert.Equal(0x01020384u, q.ItemId);
        Assert.Equal(0x0D0C0B0Au, q.Blob);
        Assert.Equal(0x77, q.Packed);
        Assert.Equal(0x05060708u, q.ClearAvatar);
        Assert.Equal(0x090A0B0Cu, q.Extra);
    }

    [Fact]
    public void Equip_slots_are_not_range_checked_ordered_or_deduplicated()
    {
        EntrySpec spec = Standard(0, 2, 0);
        foreach (int slot in new[] { 200, 26, 0, 26, 255, 1 })
        {
            spec.Equips.Add(new EquipSpec { Slot = slot, ItemId = 900 + slot });
        }

        UserInfoEntry e = Assert.Single(ParseOk(Packet(spec)).Entries);
        Assert.Equal(new[] { 200, 26, 0, 26, 255, 1 }, e.Equips.Select(q => (int)q.Slot).ToArray());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    [InlineData(5L)]
    [InlineData(8L)]
    [InlineData(22L)]
    [InlineData(0x04000000L)]
    [InlineData(0x00000104L)]
    [InlineData(0xFFFFFFFFL)]
    public void A_blob_length_other_than_4_rejects_the_whole_packet(long blobLength)
    {
        // 有的取值让"按 blobLen 变长读"的实现也读得下来(记录里真放了那么多字节):解析器仍必须拒绝
        EntrySpec spec = Standard(0, 2, 1);
        spec.Equips[0].BlobLength = blobLength;
        spec.Equips[0].Blob = new byte[blobLength <= 64 ? blobLength : 4];
        byte[] single = Packet(spec);
        AssertRejected(single, UserInfoParseStatus.Malformed);

        // 末尾带余量(真实帧有 4 字节 PI 尾):"不管 blobLen 是几都读 4 字节"的宽松实现,
        // 在 blobLen 小于 4 时靠这点余量也能把整包读完,所以光测贴合的包分不出它和严格实现;带余量的包解析器仍必须拒绝
        AssertRejected(WithTail(single, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8), UserInfoParseStatus.Malformed);

        // 同一个条目里只要有一条记录的 blobLen 不对,整包都拒绝,前后记录正常也一样
        EntrySpec middle = Standard(0, 2, 3);
        middle.Equips[1].BlobLength = blobLength;
        middle.Equips[1].Blob = new byte[blobLength <= 64 ? blobLength : 4];
        byte[] several = Packet(Standard(1, 1, 0), middle, Standard(2, 3, 2));
        AssertRejected(several, UserInfoParseStatus.Malformed);
        AssertRejected(WithTail(several, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8), UserInfoParseStatus.Malformed);
    }

    [Fact]
    public void An_equip_count_over_the_payload_is_rejected()
    {
        foreach (int real in new[] { 0, 1, 3 })
        {
            EntrySpec spec = Standard(0, 2, real);
            spec.EquipCountPrefix = real + 1;
            AssertRejected(Packet(spec), UserInfoParseStatus.Malformed);
        }

        EntrySpec huge = Standard(0, 2, 1);
        huge.EquipCountPrefix = 255;
        AssertRejected(Packet(huge), UserInfoParseStatus.Malformed);
    }

    // ---- 名字:原始字节拷贝 ----

    [Fact]
    public void Name_bytes_are_copied_raw_without_decoding_trimming_or_validation()
    {
        // 含 NUL、非法 UTF-8、4 字节序列的首字节、结尾 NUL:一个字节都不许改、不许截
        byte[] name = { 0xFF, 0x00, 0xF0, 0x9F, 0x80, 0x00, 0xC0, 0xAF, 0x00 };
        EntrySpec spec = Standard(0, 0, 1);
        spec.Name = name;

        EntrySpec next = Standard(1, 2, 0);
        UserInfoNotice n = ParseOk(Packet(spec, next));
        UserInfoEntry e = n.Entries[0];
        Assert.Equal(name, e.NameBytes.ToArray());
        Assert.Equal(name.Length, e.NameBytes.Count);
        // 名字之后的字段不受影响(没有被 NUL 提前截断名字而错位),下一个条目也对得上
        AssertEntry(spec, e);
        AssertEntry(next, n.Entries[1]);
    }

    [Fact]
    public void An_empty_name_is_an_empty_list_and_the_stream_stays_aligned()
    {
        EntrySpec empty = Standard(0, 0, 2);
        EntrySpec next = Standard(1, 4, 1);

        UserInfoNotice n = ParseOk(Packet(empty, next));
        Assert.NotNull(n.Entries[0].NameBytes);
        Assert.Empty(n.Entries[0].NameBytes);
        AssertEntry(empty, n.Entries[0]);
        AssertEntry(next, n.Entries[1]);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    public void Name_lengths_beyond_the_client_limit_of_30_are_read_by_wire_structure(int nameLength)
    {
        // 客户端对 L ≥ 31 读完 u32 就不读字节并失步;解析器按线上结构直读,不套 30 的上限、不模拟失步
        EntrySpec spec = Standard(0, nameLength, 1);
        EntrySpec next = Standard(1, 3, 0);

        UserInfoNotice n = ParseOk(Packet(spec, next));
        Assert.Equal(nameLength, n.Entries[0].NameBytes.Count);
        AssertEntry(spec, n.Entries[0]);
        AssertEntry(next, n.Entries[1]);
    }

    [Fact]
    public void The_name_length_prefix_is_a_four_byte_little_endian_integer()
    {
        // 前缀 0x00000102 = 258:只读低 2 字节得 258 也对,只读低 1 字节得 2 就错;高 2 字节非 0 的取值另测越界
        EntrySpec spec = Standard(0, 258, 0);
        UserInfoNotice n = ParseOk(Packet(spec));
        Assert.Equal(258, Assert.Single(n.Entries).NameBytes.Count);

        // 高字节非 0 的前缀(0x01000005)远超载荷,必须拒绝:只读低 1 字节的实现会读成 5 而放行
        EntrySpec liar = Standard(0, 5, 0);
        liar.NameLengthPrefix = 0x01000005;
        AssertRejected(Packet(liar), UserInfoParseStatus.Malformed);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(1000L)]
    [InlineData(0x7FFFFFFFL)]
    [InlineData(0x80000000L)]
    [InlineData(0xFFFFFFFEL)]
    [InlineData(0xFFFFFFFFL)]
    public void A_name_length_beyond_the_remaining_bytes_is_rejected(long overshoot)
    {
        // 名字前缀 = (名字起点之后的全部剩余字节数 + 超出量):总是比载荷多
        EntrySpec spec = Standard(0, 4, 0);
        byte[] honest = Packet(spec);
        int remainingAfterPrefix = honest.Length - 10 - 6;
        spec.NameLengthPrefix = overshoot < 0x7FFFFFFFL ? remainingAfterPrefix + overshoot : overshoot;
        AssertRejected(Packet(spec), UserInfoParseStatus.Malformed);
    }

    [Fact]
    public void A_name_length_exactly_filling_the_remaining_bytes_still_fails_at_the_next_field()
    {
        // 前缀恰好等于剩余字节数:名字吃光载荷,后面的字段没有字节可读,必须拒绝而不是越界
        EntrySpec spec = Standard(0, 4, 0);
        byte[] honest = Packet(spec);
        spec.NameLengthPrefix = honest.Length - 10 - 6;
        AssertRejected(Packet(spec), UserInfoParseStatus.Malformed);
    }

    [Fact]
    public void Name_bytes_do_not_alias_the_payload_array()
    {
        byte[] payload = Packet(Standard(0, 5, 0));
        UserInfoEntry e = Assert.Single(ParseOk(payload).Entries);
        byte[] nameBefore = e.NameBytes.ToArray();

        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] ^= 0xFF;
        }

        Assert.Equal(nameBefore, e.NameBytes.ToArray());
        Assert.Equal(SyntheticName(0, 5), e.NameBytes.ToArray());
    }

    // ---- 成长类型、等级、职业与其余字段 ----

    [Fact]
    public void Grow_type_byte_is_kept_raw_and_split_into_low_nibble_and_bits_4_to_6_for_all_256_values()
    {
        for (int value = 0; value < 256; value++)
        {
            EntrySpec spec = Standard(0, 3, 1);
            spec.GrowType = value;
            UserInfoEntry e = Assert.Single(ParseOk(Packet(spec)).Entries);

            Assert.Equal(value, e.GrowTypeByte);
            Assert.Equal(value & 0x0F, e.FirstGrowType);
            Assert.Equal((value >> 4) & 0x07, e.SecondGrowType);
            // 其余字段不受影响
            AssertEntry(spec, e);
        }
    }

    [Theory]
    [InlineData(0x00, 0, 0)]
    [InlineData(0x01, 1, 0)]
    [InlineData(0x06, 6, 0)]
    [InlineData(0x10, 0, 1)]
    [InlineData(0x26, 6, 2)]
    [InlineData(0x70, 0, 7)]
    [InlineData(0x80, 0, 0)]
    [InlineData(0x8F, 15, 0)]
    [InlineData(0xF5, 5, 7)]
    [InlineData(0xFF, 15, 7)]
    public void Grow_type_split_examples_bit_7_belongs_to_neither_segment(int value, int first, int second)
    {
        EntrySpec spec = Standard(0, 1, 0);
        spec.GrowType = value;
        UserInfoEntry e = Assert.Single(ParseOk(Packet(spec)).Entries);
        Assert.Equal(first, e.FirstGrowType);
        Assert.Equal(second, e.SecondGrowType);
        Assert.Equal(value, e.GrowTypeByte);
    }

    public static IEnumerable<object[]> ByteFieldNames()
    {
        foreach (string name in new[] { "X1", "Job", "GrowType", "Level", "D", "Status", "G", "H", "J", "Premium", "A2", "B2", "C2" })
        {
            yield return new object[] { name };
        }
    }

    private static void SetByteField(EntrySpec e, string name, int value)
    {
        switch (name)
        {
            case "X1": e.X1 = value; break;
            case "Job": e.Job = value; break;
            case "GrowType": e.GrowType = value; break;
            case "Level": e.Level = value; break;
            case "D": e.D = value; break;
            case "Status": e.Status = value; break;
            case "G": e.G = value; break;
            case "H": e.H = value; break;
            case "J": e.J = value; break;
            case "Premium": e.Premium = value; break;
            case "A2": e.A2 = value; break;
            case "B2": e.B2 = value; break;
            case "C2": e.C2 = value; break;
            default: throw new ArgumentException(name);
        }
    }

    [Theory]
    [MemberData(nameof(ByteFieldNames))]
    public void Every_single_byte_field_takes_all_256_values_without_disturbing_the_others(string field)
    {
        for (int value = 0; value < 256; value++)
        {
            EntrySpec spec = Standard(1, 4, 2);
            SetByteField(spec, field, value);
            EntrySpec neighbour = Standard(2, 1, 0);

            UserInfoNotice n = ParseOk(Packet(spec, neighbour));
            AssertEntry(spec, n.Entries[0]);
            AssertEntry(neighbour, n.Entries[1]);
        }
    }

    [Fact]
    public void Level_and_job_are_distinct_fields_at_distinct_offsets()
    {
        // 等级与职业取值各异、且与相邻的 growType、D 不同:互换或错位一位都会被看出来
        EntrySpec spec = Standard(0, 5, 0);
        spec.Job = 0x91;
        spec.GrowType = 0x32;
        spec.Level = 0x5C;
        spec.D = 0x11;
        spec.Status = 0x08;

        UserInfoEntry e = Assert.Single(ParseOk(Packet(spec)).Entries);
        Assert.Equal(0x91, e.Job);
        Assert.Equal(0x32, e.GrowTypeByte);
        Assert.Equal(0x5C, e.Level);
        Assert.Equal(0x11, e.D);
        Assert.Equal(0x08, e.Status);
    }

    public static IEnumerable<object[]> BoundaryValues()
    {
        foreach (long value in new long[] { 0, 1, 0xFF, 0x100, 0xFFFF, 0x10000, 0xFFFFFF, 0x1000000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF })
        {
            yield return new object[] { value };
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryValues))]
    public void Every_u32_field_is_little_endian_at_the_boundaries(long value)
    {
        EntrySpec spec = Standard(0, 2, 1);
        spec.I = value;
        spec.K = value;
        spec.D2 = value;
        spec.Equips[0].ItemId = ItemIdFor(value);
        spec.Equips[0].ClearAvatar = value;
        spec.Equips[0].Extra = value;
        spec.Equips[0].Blob = new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

        UserInfoNotice n = ParseOk(Packet(1, 2, 3, value, spec));
        Assert.Equal(value, (long)n.D);
        UserInfoEntry e = Assert.Single(n.Entries);
        AssertEntry(spec, e);
        Assert.Equal(value, (long)e.Equips[0].Blob);
    }

    [Fact]
    public void Each_u32_field_is_read_independently_of_the_others()
    {
        // 一次只动一个 u32 字段,别的字段保持默认值:读错位置的字段会把值显示在别处
        foreach (long value in new long[] { 0, 0xA1B2C3D4, 0xFFFFFFFF })
        {
            foreach (int which in new[] { 0, 1, 2, 3, 4, 5, 6 })
            {
                EntrySpec spec = Standard(0, 2, 1);
                switch (which)
                {
                    case 0: spec.I = value; break;
                    case 1: spec.K = value; break;
                    case 2: spec.D2 = value; break;
                    case 3: spec.Equips[0].ItemId = ItemIdFor(value); break;
                    case 4: spec.Equips[0].ClearAvatar = value; break;
                    case 5: spec.Equips[0].Extra = value; break;
                    default: spec.Equips[0].Blob = new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) }; break;
                }
                AssertEntry(spec, Assert.Single(ParseOk(Packet(spec)).Entries));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0xFF)]
    [InlineData(0x100)]
    [InlineData(0x1234)]
    [InlineData(0x7FFF)]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public void Uid_is_a_little_endian_u16(int uid)
    {
        EntrySpec spec = Standard(0, 3, 0);
        spec.Uid = uid;
        UserInfoEntry e = Assert.Single(ParseOk(Packet(spec)).Entries);
        Assert.Equal(uid, e.Uid);
        AssertEntry(spec, e);
    }

    [Fact]
    public void Header_bytes_a_b_c_take_all_256_values_independently()
    {
        for (int value = 0; value < 256; value++)
        {
            UserInfoNotice a = ParseOk(Packet(value, 0x22, 0x33, 0x44556677));
            Assert.Equal(value, a.A);
            Assert.Equal(0x22, a.B);
            Assert.Equal(0x33, a.C);
            Assert.Equal(0x44556677u, a.D);

            UserInfoNotice b = ParseOk(Packet(0x11, value, 0x33, 0x44556677));
            Assert.Equal(0x11, b.A);
            Assert.Equal(value, b.B);
            Assert.Equal(0x33, b.C);

            UserInfoNotice c = ParseOk(Packet(0x11, 0x22, value, 0x44556677));
            Assert.Equal(0x11, c.A);
            Assert.Equal(0x22, c.B);
            Assert.Equal(value, c.C);
        }
    }

    [Fact]
    public void Field_values_are_not_range_checked()
    {
        // 头部 b 大于 a、count 大于 a、X1 为 1、status 为 8、等级 0、槽号越界:客户端会拦的或语义上可疑的取值,解析器不判
        EntrySpec odd = Standard(0, 2, 1);
        odd.X1 = 1;
        odd.Status = 8;
        odd.Level = 0;
        odd.Job = 255;
        odd.GrowType = 0xFF;
        odd.Equips[0].Slot = 200;

        UserInfoNotice n = ParseOk(Packet(2, 200, 0, 0, odd, Standard(1, 1, 0), Standard(2, 1, 0)));
        Assert.Equal(2, n.A);
        Assert.Equal(200, n.B);
        Assert.Equal(3, n.Entries.Count);
        AssertEntry(odd, n.Entries[0]);
    }

    // ---- 截断 ----

    [Fact]
    public void Every_proper_prefix_of_a_valid_packet_is_rejected_without_throwing()
    {
        EntrySpec first = Standard(0, 7, 2);
        EntrySpec second = Standard(1, 0, 1);
        EntrySpec third = Standard(2, 3, 0);
        byte[] payload = Packet(first, second, third);

        // 对照:完整载荷读得通
        Assert.Equal(3, ParseOk(payload).Entries.Count);

        for (int length = 0; length < payload.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(payload, prefix, length);
            AssertRejected(prefix, UserInfoParseStatus.Malformed);
        }
    }

    [Fact]
    public void Every_proper_prefix_of_a_single_entry_packet_is_rejected_at_every_field_boundary()
    {
        byte[] payload = HandLaidPacket();
        for (int length = 0; length < payload.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(payload, prefix, length);
            AssertRejected(prefix, UserInfoParseStatus.Malformed);
        }
        Assert.Single(ParseOk(payload).Entries);
    }

    [Fact]
    public void Null_and_empty_payloads_are_malformed_not_unsupported()
    {
        AssertRejected(null, UserInfoParseStatus.Malformed);
        AssertRejected(new byte[0], UserInfoParseStatus.Malformed);
    }

    [Fact]
    public void A_lone_mode_2_byte_and_a_short_header_are_malformed()
    {
        AssertRejected(new byte[] { 2 }, UserInfoParseStatus.Malformed);
        AssertRejected(new byte[] { 2, 1 }, UserInfoParseStatus.Malformed);
        AssertRejected(new byte[] { 2, 1, 2, 3, 4, 5, 6, 7, 8 }, UserInfoParseStatus.Malformed);
    }

    // ---- 个数声明越界与不足 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void A_count_one_above_the_real_entries_is_rejected(int real)
    {
        EntrySpec[] specs = new EntrySpec[real];
        for (int i = 0; i < real; i++)
        {
            specs[i] = Standard(i, 3, 1);
        }
        AssertRejected(PacketWithCount(1, 2, 3, 4, real + 1, specs), UserInfoParseStatus.Malformed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(40000)]
    [InlineData(65535)]
    public void A_count_far_beyond_the_payload_is_rejected_quickly(int count)
    {
        // 头之后没有任何条目,或只有 1 个条目,而声明的个数(≥ 2)远超载荷
        AssertRejected(PacketWithCount(1, 2, 3, 4, count), UserInfoParseStatus.Malformed);
        AssertRejected(PacketWithCount(1, 2, 3, 4, count, Standard(0, 3, 1)), UserInfoParseStatus.Malformed);
    }

    [Fact]
    public void A_count_of_one_with_no_entry_bytes_is_rejected_and_with_one_entry_is_accepted()
    {
        AssertRejected(PacketWithCount(1, 2, 3, 4, 1), UserInfoParseStatus.Malformed);
        Assert.Single(ParseOk(PacketWithCount(1, 2, 3, 4, 1, Standard(0, 3, 1))).Entries);
    }

    [Fact]
    public void A_count_whose_last_entry_is_one_byte_short_is_rejected()
    {
        // 声明 2 条:第 2 条只剩 31 字节(最短条目是 32)
        byte[] first = Entry(Standard(0, 4, 0));
        byte[] shortSecond = new byte[31];
        Array.Copy(Entry(Standard(1, 0, 0)), shortSecond, 31);
        byte[] payload = WithTail(WithTail(Header(2, 1, 2, 3, 4, 2), first), shortSecond);
        AssertRejected(payload, UserInfoParseStatus.Malformed);

        // 补足最后一个字节就读得通(对照)
        byte[] complete = WithTail(WithTail(Header(2, 1, 2, 3, 4, 2), first), Entry(Standard(1, 0, 0)));
        Assert.Equal(2, ParseOk(complete).Entries.Count);
    }

    [Fact]
    public void A_count_below_the_real_entries_reads_only_that_many_and_reports_the_rest_as_trailing()
    {
        EntrySpec[] specs = { Standard(0, 4, 1), Standard(1, 6, 2), Standard(2, 2, 0) };
        byte[] payload = PacketWithCount(1, 2, 3, 4, 2, specs);

        UserInfoNotice n = ParseOk(payload);
        Assert.Equal(2, n.Entries.Count);
        AssertEntry(specs[0], n.Entries[0]);
        AssertEntry(specs[1], n.Entries[1]);
        Assert.Equal(Entry(specs[2]).Length, n.TrailingByteCount);
    }

    // ---- 尾部多余字节 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(400)]
    public void Trailing_bytes_are_counted_not_consumed(int tailLength)
    {
        EntrySpec[] specs = { Standard(0, 4, 1), Standard(1, 2, 0) };
        byte[] tail = new byte[tailLength];
        for (int i = 0; i < tail.Length; i++)
        {
            tail[i] = (byte)(0xE0 + i);
        }

        UserInfoNotice n = ParseOk(WithTail(Packet(specs), tail));
        Assert.Equal(tailLength, n.TrailingByteCount);
        Assert.Equal(2, n.Entries.Count);
        AssertEntry(specs[0], n.Entries[0]);
        AssertEntry(specs[1], n.Entries[1]);
    }

    [Fact]
    public void A_four_byte_pi_style_tail_is_reported_as_four()
    {
        // 真实帧里常见的尾巴:4 字节折叠 PI 尾。这里是 4 个任意字节,不核对内容
        byte[] payload = WithTail(Packet(Standard(0, 5, 1)), 0x81, 0x78, 0x1A, 0xBF);
        UserInfoNotice n = ParseOk(payload);
        Assert.Equal(4, n.TrailingByteCount);
        Assert.Single(n.Entries);
    }

    [Fact]
    public void Trailing_bytes_that_look_like_a_whole_entry_are_not_read_as_one()
    {
        byte[] extraEntry = Entry(Standard(5, 6, 2));
        UserInfoNotice n = ParseOk(WithTail(Packet(Standard(0, 4, 1)), extraEntry));
        Assert.Single(n.Entries);
        Assert.Equal(extraEntry.Length, n.TrailingByteCount);
    }

    [Fact]
    public void The_trailing_count_is_zero_when_the_last_field_ends_exactly_at_the_payload_end()
    {
        Assert.Equal(0, ParseOk(Packet(Standard(0, 0, 0))).TrailingByteCount);
        Assert.Equal(0, ParseOk(HandLaidPacket()).TrailingByteCount);
        Assert.Equal(0, ParseOk(Packet()).TrailingByteCount);
    }

    // ---- 非模式 2:不支持,绝不误读 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Modes_0_1_and_3_are_unsupported_even_when_the_body_has_the_mode_2_shape(int mode)
    {
        // 把一份合法的模式 2 整包的首字节换成别的模式:不看模式的实现会照样读成功
        byte[] payload = Packet(Standard(0, 5, 1), Standard(1, 3, 0));
        payload[0] = (byte)mode;
        AssertRejected(payload, UserInfoParseStatus.UnsupportedMode);
    }

    [Fact]
    public void Every_mode_value_other_than_2_is_unsupported()
    {
        for (int mode = 0; mode < 256; mode++)
        {
            byte[] payload = Packet(Standard(0, 5, 1));
            payload[0] = (byte)mode;
            if (mode == 2)
            {
                ParseOk(payload);
            }
            else
            {
                AssertRejected(payload, UserInfoParseStatus.UnsupportedMode);
            }
        }
    }

    [Fact]
    public void A_non_mode_2_payload_is_unsupported_whatever_follows_the_mode_byte()
    {
        // 模式字节之后是什么都不影响结论:空、1 字节、随机字节
        var rng = new Lcg(0x13579BDF);
        foreach (int mode in new[] { 0, 1, 3, 4, 7, 0x78, 0xFF })
        {
            AssertRejected(new byte[] { (byte)mode }, UserInfoParseStatus.UnsupportedMode);
            for (int length = 1; length < 80; length++)
            {
                byte[] payload = new byte[1 + length];
                payload[0] = (byte)mode;
                Array.Copy(rng.Bytes(length), 0, payload, 1, length);
                AssertRejected(payload, UserInfoParseStatus.UnsupportedMode);
            }
        }
    }

    [Fact]
    public void Type_level_shaped_bodies_of_modes_0_1_and_3_are_not_read_as_a_character_list()
    {
        // 模式 0/1/3 的包是 u16 count + 条目(恒单条目);拼成 [mode][u16 1][一个条目的字节],不许被当成模式 2 的头去读
        foreach (int mode in new[] { 0, 1, 3 })
        {
            byte[] payload = WithTail(new byte[] { (byte)mode, 1, 0 }, Entry(Standard(0, 6, 2)));
            AssertRejected(payload, UserInfoParseStatus.UnsupportedMode);
        }
    }

    [Fact]
    public void A_still_compressed_payload_is_unsupported_and_never_read_as_a_list()
    {
        byte[] plain = Packet(Standard(0, 5, 1), Standard(1, 3, 2));
        byte[] zipped = Zlib(plain);

        // zlib 流的首字节低 4 位恒为 8(deflate),所以永远不会等于模式 2
        Assert.Equal(8, zipped[0] & 0x0F);
        AssertRejected(zipped, UserInfoParseStatus.UnsupportedMode);
        AssertRejected(Zlib(zipped), UserInfoParseStatus.UnsupportedMode);
        Assert.Equal(2, ParseOk(plain).Entries.Count);
    }

    // ---- 既有解码管线给到的载荷形态 ----

    [Fact]
    public void A_class_0_id_2_frame_through_the_decoder_hands_over_inflated_plaintext_starting_at_the_mode_byte()
    {
        EntrySpec[] specs = { Standard(0, 5, 2), Standard(1, 8, 0) };
        byte[] tail = { 0x81, 0x78, 0x1A, 0xBF };
        byte[] plaintext = WithTail(Packet(specs), tail);

        DecodedFrame frame = DecodedFrameOf(plaintext);
        Assert.Equal(2, frame.Header.PacketId);
        Assert.True(frame.WasCompressed);
        Assert.Equal(2, frame.Payload[0]);

        UserInfoNotice n = ParseOk(frame.Payload);
        Assert.Equal(2, n.Entries.Count);
        AssertEntry(specs[0], n.Entries[0]);
        AssertEntry(specs[1], n.Entries[1]);
        Assert.Equal(4, n.TrailingByteCount);
    }

    [Fact]
    public void The_decoder_inflates_id_2_whatever_the_class_so_the_payload_shape_is_the_same()
    {
        // 压缩层按包号判定、不看包类:类 1 的包号 2 同样被解压
        byte[] plaintext = Packet(Standard(0, 5, 1));
        DecodedFrame asCommandReply = DecodedFrameOf(plaintext, 1);
        Assert.True(asCommandReply.WasCompressed);
        Assert.Single(ParseOk(asCommandReply.Payload).Entries);
    }

    // ---- 只读、互不牵连、可重复 ----

    [Fact]
    public void Records_have_no_public_setters_or_constructors_and_only_readonly_fields()
    {
        foreach (Type type in new[] { typeof(UserInfoNotice), typeof(UserInfoEntry), typeof(UserInfoEquip) })
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
        Assert.Empty(typeof(UserInfoNotice).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(UserInfoEntry).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Every_list_in_a_record_is_read_only_including_empty_ones()
    {
        UserInfoNotice n = ParseOk(Packet(Standard(0, 5, 2), Standard(1, 0, 0)));
        AssertListIsReadOnly(n.Entries);
        AssertListIsReadOnly(n.Entries[0].NameBytes);
        AssertListIsReadOnly(n.Entries[0].Equips);
        AssertListIsReadOnly(n.Entries[1].NameBytes);
        AssertListIsReadOnly(n.Entries[1].Equips);
        AssertListIsReadOnly(ParseOk(Packet()).Entries);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_is_repeatable()
    {
        byte[] payload = WithTail(Packet(Standard(0, 5, 2), Standard(1, 9, 1)), 1, 2, 3, 4);
        byte[] before = (byte[])payload.Clone();

        UserInfoNotice first = ParseOk(payload);
        UserInfoNotice second = ParseOk(payload);

        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.Equal(Serialize(first), Serialize(second));
        Assert.Equal(first.TrailingByteCount, second.TrailingByteCount);
    }

    [Fact]
    public void Records_do_not_alias_the_input_array()
    {
        byte[] payload = Packet(Standard(0, 5, 2), Standard(1, 9, 1));
        UserInfoNotice n = ParseOk(payload);
        byte[] snapshot = Serialize(n);

        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0;
        }

        Assert.Equal(snapshot, Serialize(n));
    }

    // ---- 性质测试(固定种子,可复现) ----

    [Fact]
    public void Random_valid_packets_round_trip_field_by_field()
    {
        var rng = new Lcg(0x2468ACE1);
        for (int round = 0; round < 400; round++)
        {
            int entryCount = rng.Next(6);
            EntrySpec[] specs = new EntrySpec[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                specs[i] = RandomEntry(rng);
            }
            int a = rng.NextByte();
            int b = rng.NextByte();
            int c = rng.NextByte();
            long d = rng.NextUInt();
            byte[] tail = rng.Bytes(rng.Next(7));

            UserInfoNotice n = ParseOk(WithTail(Packet(a, b, c, d, specs), tail));
            Assert.Equal(a, n.A);
            Assert.Equal(b, n.B);
            Assert.Equal(c, n.C);
            Assert.Equal(d, (long)n.D);
            Assert.Equal(entryCount, n.Entries.Count);
            for (int i = 0; i < entryCount; i++)
            {
                AssertEntry(specs[i], n.Entries[i]);
            }
            Assert.Equal(tail.Length, n.TrailingByteCount);
        }
    }

    [Fact]
    public void Corrupted_and_truncated_packets_never_throw_and_every_success_round_trips_the_consumed_prefix()
    {
        var rng = new Lcg(0x0BADF00D);
        int successes = 0;
        int rejections = 0;
        for (int round = 0; round < 3000; round++)
        {
            int entryCount = 1 + rng.Next(3);
            EntrySpec[] specs = new EntrySpec[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                specs[i] = RandomEntry(rng);
            }
            byte[] payload = Packet(rng.NextByte(), rng.NextByte(), rng.NextByte(), rng.NextUInt(), specs);

            // 随机改 0 到 3 个字节,再随机截断或补尾
            int flips = rng.Next(4);
            for (int f = 0; f < flips; f++)
            {
                payload[rng.Next(payload.Length)] = rng.NextByte();
            }
            int mode = rng.Next(4);
            if (mode == 0)
            {
                Array.Resize(ref payload, rng.Next(payload.Length + 1));
            }
            else if (mode == 1)
            {
                payload = WithTail(payload, rng.Bytes(rng.Next(9)));
            }

            bool ok = false;
            UserInfoNotice n = null;
            Exception ex = Record.Exception(() => { ok = UserInfoParser.TryParse(payload, out n); });
            Assert.Null(ex);
            if (!ok)
            {
                Assert.Null(n);
                rejections++;
                continue;
            }

            successes++;
            Assert.NotNull(n);
            int consumed = payload.Length - n.TrailingByteCount;
            Assert.True(consumed >= 10);
            byte[] prefix = new byte[consumed];
            Array.Copy(payload, prefix, consumed);
            Assert.Equal(prefix, Serialize(n));
        }

        // 两种结局都真的出现过,免得这个测试空转
        Assert.True(successes > 500, "成功解析的次数太少:" + successes);
        Assert.True(rejections > 500, "被拒绝的次数太少:" + rejections);
    }

    [Fact]
    public void Arbitrary_bytes_with_a_mode_2_prefix_never_throw()
    {
        var rng = new Lcg(0x5EEDBEEF);
        for (int round = 0; round < 4000; round++)
        {
            int length = rng.Next(160);
            byte[] payload = rng.Bytes(length);
            if (length > 0 && rng.Next(4) != 0)
            {
                payload[0] = 2;
            }
            if (length > 9 && rng.Next(2) == 0)
            {
                // 把 count 压小,让解析走得更深
                payload[8] = (byte)(rng.Next(3));
                payload[9] = 0;
            }

            bool ok = false;
            UserInfoNotice n = null;
            Exception ex = Record.Exception(() => { ok = UserInfoParser.TryParse(payload, out n); });
            Assert.Null(ex);
            Assert.Equal(ok, n != null);
            if (ok)
            {
                int consumed = payload.Length - n.TrailingByteCount;
                byte[] prefix = new byte[consumed];
                Array.Copy(payload, prefix, consumed);
                Assert.Equal(prefix, Serialize(n));
            }
        }
    }
}
