using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 副本许可通知(类 0、包号 5)的明文载荷解析器与许可表快照(纯函数)。载荷全是手工拼的合成字节(手写字节数组或 <see cref="PktImplTestKit"/> 的独立写入器),
/// 副本号取 900 以上的合成值(901、1357、40000……,u16),档位是 u8 的小值(1 到 4 是服务端写点的取值域,另有 0、5、255 等纯宽度边界值);
/// 不读任何真实帧文件,不含真实编号或数值。
/// 读序依据 E-382 / packetread_NOTES §2.2:<c>u16 count, count × { u16 副本号, u8 档位 }</c>,总长 = 2 + 3 × count,没有尾部变长段,没有到期字段。
/// 读完最后一条即停,尾巴(折叠哈希、加密补零、单条路径多出的 1 字节)只报告个数。
/// 到期:载荷里没有这个字段,所以没有"恰好到期算哪边"的边界可测;<see cref="The_records_carry_no_expiry_member"/> 把这一点钉成断言,
/// 将来若有人给记录加了到期成员,这条会失败,逼着去对读序。
/// </summary>
public class DungeonPermissionParserTests
{
    // 规则里的包号写字面量,不引用产品常量:产品常量被改动时,测试要能发现。
    private const ushort PermissionId = 5;

    // ---- 助手 ----

    private static DungeonPermissionNotice ParseOk(byte[] payload)
    {
        Assert.True(DungeonPermissionParser.TryParse(payload, out DungeonPermissionNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    /// <summary>被拒:不抛异常、返回 false、notice 为 null。</summary>
    private static void AssertRejected(byte[] payload)
    {
        DungeonPermissionNotice notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = DungeonPermissionParser.TryParse(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    /// <summary>第 i 条的合成取值:副本号 900 以上且互不相同(不递增:每隔一条往回跳),档位在 0..4 与 255 之间轮换。</summary>
    private static (int DungeonId, int Tier) SyntheticEntry(int i, int variant = 0)
    {
        int id = (i % 2 == 0) ? 901 + (13 * i) + variant : 40000 - (7 * i) + variant;
        int[] tiers = { 1, 2, 3, 4, 0, 255 };
        return (id & 0xFFFF, tiers[(i + variant) % tiers.Length]);
    }

    private static (int DungeonId, int Tier)[] SyntheticEntries(int count, int variant = 0)
    {
        var list = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            list[i] = SyntheticEntry(i, variant);
        }
        return list;
    }

    private static DecodedFrame PermissionFrame(byte[] payload)
    {
        return PktImplTestKit.Frame(Noti, PermissionId, payload);
    }

    // ---- 常量 ----

    [Fact]
    public void Production_constant_matches_the_documented_id()
    {
        Assert.Equal(5, DungeonPermissionParser.PacketId);
    }

    // ---- 逐字段(手写字节,不经写入器) ----

    /// <summary>
    /// 手写的标准载荷:4 条,副本号不递增(901、1357、40000、912),档位 1、2、3、4。每个 u16 的两个字节互不相同,核对小端与字段顺序。
    /// 长度 2 + 3 × 4 = 14。
    /// </summary>
    private static readonly byte[] LiteralPayload =
    {
        0x04, 0x00,             // count = 4
        0x85, 0x03, 0x01,       // 副本号 0x0385 = 901,档位 1
        0x4D, 0x05, 0x02,       // 副本号 0x054D = 1357,档位 2
        0x40, 0x9C, 0x03,       // 副本号 0x9C40 = 40000,档位 3
        0x90, 0x03, 0x04,       // 副本号 0x0390 = 912,档位 4
    };

    [Fact]
    public void A_literal_payload_is_read_field_by_field()
    {
        DungeonPermissionNotice n = ParseOk(LiteralPayload);

        Assert.Equal(4, n.Entries.Count);
        Assert.Equal(new[] { (901, 1), (1357, 2), (40000, 3), (912, 4) }, Pairs(n));
        Assert.Equal(0, n.TrailingByteCount);
        Assert.Equal(2 + (3 * 4), LiteralPayload.Length);
    }

    [Fact]
    public void Every_field_is_independent_of_its_neighbours()
    {
        // 每个字段单独变:只改一个字节,恰好一个属性变,别的不动
        for (int pos = 2; pos < LiteralPayload.Length; pos++)
        {
            byte[] changed = (byte[])LiteralPayload.Clone();
            changed[pos] ^= 0x5A;
            DungeonPermissionNotice a = ParseOk(LiteralPayload);
            DungeonPermissionNotice b = ParseOk(changed);

            int entryIndex = (pos - 2) / 3;
            int within = (pos - 2) % 3;
            for (int e = 0; e < 4; e++)
            {
                bool idSame = a.Entries[e].DungeonId == b.Entries[e].DungeonId;
                bool tierSame = a.Entries[e].Tier == b.Entries[e].Tier;
                if (e == entryIndex)
                {
                    Assert.Equal(within >= 2, idSame);     // 第 0、1 个字节是副本号,第 2 个是档位
                    Assert.Equal(within < 2, tierSame);
                }
                else
                {
                    Assert.True(idSame);
                    Assert.True(tierSame);
                }
            }
        }
    }

    // ---- 个数边界 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(65535)]
    public void Entry_counts_at_the_boundaries_are_read_exactly(int count)
    {
        (int DungeonId, int Tier)[] entries = SyntheticEntries(count, variant: count % 17);
        byte[] payload = Permission(entries);
        Assert.Equal(2 + (3 * count), payload.Length);

        DungeonPermissionNotice n = ParseOk(payload);

        Assert.Equal(count, n.Entries.Count);
        Assert.Equal(entries, Pairs(n));
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Zero_entries_is_a_valid_empty_table_and_never_null()
    {
        DungeonPermissionNotice n = ParseOk(new byte[] { 0x00, 0x00 });

        Assert.NotNull(n.Entries);
        Assert.Empty(n.Entries);
        Assert.Equal(0, n.TrailingByteCount);
        AssertListIsReadOnly(n.Entries);

        // 个数为 0 时后面跟什么都不读
        DungeonPermissionNotice withTail = ParseOk(new byte[] { 0x00, 0x00, 0x85, 0x03, 0x01, 0xEE });
        Assert.Empty(withTail.Entries);
        Assert.Equal(4, withTail.TrailingByteCount);
    }

    // ---- 截断与个数越界 ----

    [Fact]
    public void Every_proper_prefix_of_a_table_is_rejected()
    {
        for (int len = 0; len < LiteralPayload.Length; len++)
        {
            byte[] prefix = new byte[len];
            Array.Copy(LiteralPayload, prefix, len);
            AssertRejected(prefix);
        }
    }

    [Fact]
    public void A_truncation_inside_any_field_is_rejected()
    {
        // 标准载荷里每个字段的每个内部位置:个数 2 字节,每条的副本号 2 字节与档位 1 字节
        byte[] payload = Permission(SyntheticEntries(5, variant: 3));
        for (int len = 0; len < payload.Length; len++)
        {
            AssertRejected(payload.Take(len).ToArray());
        }
        Assert.True(DungeonPermissionParser.TryParse(payload, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(256, 255)]
    [InlineData(65535, 3)]
    [InlineData(65535, 0)]
    public void A_declared_count_beyond_the_payload_is_rejected(int declared, int actualEntries)
    {
        AssertRejected(PermissionWithDeclaredCount(declared, SyntheticEntries(actualEntries)));
    }

    [Fact]
    public void A_declared_count_one_below_the_actual_entries_reads_only_the_declared_ones()
    {
        // 个数声明比实际条数少 1:读完声明的条数就停,多出的一条整条算尾巴(3 字节)
        (int DungeonId, int Tier)[] entries = SyntheticEntries(4, variant: 5);
        DungeonPermissionNotice n = ParseOk(PermissionWithDeclaredCount(3, entries));

        Assert.Equal(entries.Take(3), Pairs(n));
        Assert.Equal(3, n.TrailingByteCount);
    }

    [Fact]
    public void Null_and_tiny_payloads_are_rejected_without_throwing()
    {
        AssertRejected(null);
        AssertRejected(new byte[0]);
        AssertRejected(new byte[] { 0x01 });
        AssertRejected(new byte[] { 0x01, 0x00 });                   // 声明 1 条,没有条目
        AssertRejected(new byte[] { 0x01, 0x00, 0x85 });
        AssertRejected(new byte[] { 0x01, 0x00, 0x85, 0x03 });       // 差档位这一个字节
        Assert.True(DungeonPermissionParser.TryParse(new byte[] { 0x01, 0x00, 0x85, 0x03, 0x01 }, out _));
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

        DungeonPermissionNotice n = ParseOk(Permission(SyntheticEntries(3, variant: 2), tail));

        Assert.Equal(SyntheticEntries(3, variant: 2), Pairs(n));
        Assert.Equal(tailLength, n.TrailingByteCount);
    }

    [Fact]
    public void A_server_style_fold_hash_tail_and_encryption_zero_padding_are_both_just_trailing_bytes()
    {
        byte[] body = Permission(SyntheticEntries(6, variant: 1));
        byte[] withHash = PacketIntegrity.AppendFoldHash(body);
        Assert.Equal(body.Length + 4, withHash.Length);     // 载荷长度在 1 到 0x190 之间,服务端追加 4 字节折叠哈希

        DungeonPermissionNotice hashed = ParseOk(withHash);
        Assert.Equal(SyntheticEntries(6, variant: 1), Pairs(hashed));
        Assert.Equal(4, hashed.TrailingByteCount);

        // 会话加密的补零接在后面
        byte[] padded = withHash.Concat(new byte[5]).ToArray();
        DungeonPermissionNotice paddedNotice = ParseOk(padded);
        Assert.Equal(SyntheticEntries(6, variant: 1), Pairs(paddedNotice));
        Assert.Equal(9, paddedNotice.TrailingByteCount);
    }

    /// <summary>
    /// 单条路径(<c>CParty::DungeonPermission</c>、<c>CUser::quest_reward</c>):服务端把第三字段写成 2 字节,客户端按 u8 读。
    /// 解析器只读低字节,多出的那个字节(再加折叠哈希)进尾巴。
    /// </summary>
    [Theory]
    [InlineData(901, 1)]
    [InlineData(1357, 2)]
    [InlineData(40000, 3)]
    [InlineData(912, 4)]
    [InlineData(65535, 255)]
    public void The_single_entry_path_with_a_two_byte_third_field_reads_the_low_byte(int dungeonId, int tier)
    {
        byte[] body = new NoticePayloadBuilder().U16(1).U16(dungeonId).U16(tier).ToArray();
        Assert.Equal(6, body.Length);                  // 2 字节个数 + 2 字节副本号 + 2 字节第三字段:比读序里的 2 + 3 多 1 字节

        DungeonPermissionNotice plain = ParseOk(body);
        Assert.Equal(new[] { (dungeonId, tier) }, Pairs(plain));
        Assert.Equal(1, plain.TrailingByteCount);

        DungeonPermissionNotice hashed = ParseOk(PacketIntegrity.AppendFoldHash(body));
        Assert.Equal(new[] { (dungeonId, tier) }, Pairs(hashed));
        Assert.Equal(5, hashed.TrailingByteCount);
    }

    [Fact]
    public void A_two_byte_third_field_with_a_nonzero_high_byte_still_reads_only_the_low_byte()
    {
        // 档位 0x0103:低字节 3 进条目,高字节 0x01 进尾巴
        byte[] body = new NoticePayloadBuilder().U16(1).U16(901).U16(0x0103).ToArray();
        DungeonPermissionNotice n = ParseOk(body);

        Assert.Equal(new[] { (901, 3) }, Pairs(n));
        Assert.Equal(1, n.TrailingByteCount);
    }

    // ---- 取值不做检查、不改写 ----

    [Fact]
    public void Values_are_never_range_checked_reordered_or_deduplicated_in_the_notice()
    {
        (int DungeonId, int Tier)[] entries =
        {
            (65535, 255), (0, 0), (901, 5), (901, 127), (40000, 128), (1, 4), (65535, 1), (902, 3),
        };

        DungeonPermissionNotice n = ParseOk(Permission(entries));

        Assert.Equal(entries, Pairs(n));
    }

    [Fact]
    public void Tiers_are_kept_as_written_with_no_downgrade()
    {
        // 客户端会把"档位 3 但副本不在名单里"降成 2;解析器保存线上原值,不降级
        DungeonPermissionNotice n = ParseOk(Permission(new[] { (901, 3), (902, 3), (903, 2) }));
        Assert.Equal(new[] { (901, 3), (902, 3), (903, 2) }, Pairs(n));
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void The_records_carry_no_expiry_member()
    {
        // 载荷里没有到期/有效期字段:记录与快照只有读序里有的成员(副本号、档位),没有任何时间类成员
        string[] noticeProperties = typeof(DungeonPermissionNotice).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Entries", "TrailingByteCount" }, noticeProperties);

        string[] entryProperties = typeof(DungeonPermissionEntry).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "DungeonId", "Tier" }, entryProperties);

        string[] snapshotProperties = typeof(DungeonPermissionSnapshot).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Count", "Entries", "MalformedNoticeCount", "NoticeCount" }, snapshotProperties);

        foreach (Type type in new[] { typeof(DungeonPermissionNotice), typeof(DungeonPermissionEntry), typeof(DungeonPermissionSnapshot) })
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.DoesNotContain("Expire", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Valid", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Time", property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Record_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        Type[] types = { typeof(DungeonPermissionNotice), typeof(DungeonPermissionEntry), typeof(DungeonPermissionSnapshot) };
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

        Assert.Empty(typeof(DungeonPermissionNotice).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(DungeonPermissionSnapshot).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(DungeonPermissionEntry).IsValueType);

        // 实例字段只许简单值、只读列表接口与记录自己的私有查找表:不留载荷数组或可写集合给外部
        Type[] allowed =
        {
            typeof(byte),
            typeof(ushort),
            typeof(int),
            typeof(IReadOnlyList<DungeonPermissionEntry>),
            typeof(Dictionary<ushort, byte>),
        };
        foreach (Type type in types)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.Contains(field.FieldType, allowed);
                Assert.False(field.IsPublic);
            }
        }
    }

    [Fact]
    public void The_entry_list_rejects_every_write_even_through_IList()
    {
        DungeonPermissionNotice n = ParseOk(LiteralPayload);
        AssertListIsReadOnly(n.Entries);
        Assert.Equal(new[] { (901, 1), (1357, 2), (40000, 3), (912, 4) }, Pairs(n));
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer()
    {
        byte[] payload = Permission(SyntheticEntries(4, variant: 9), 1, 2, 3);
        DungeonPermissionNotice n = ParseOk(payload);

        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        Assert.Equal(SyntheticEntries(4, variant: 9), Pairs(n));
        Assert.Equal(3, n.TrailingByteCount);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_is_repeatable()
    {
        byte[] payload = Permission(SyntheticEntries(4, variant: 4), 7, 8);
        byte[] before = (byte[])payload.Clone();

        DungeonPermissionNotice first = ParseOk(payload);
        DungeonPermissionNotice second = ParseOk(payload);

        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Entries, second.Entries);
        Assert.Equal(Pairs(first), Pairs(second));
        Assert.Equal(first.TrailingByteCount, second.TrailingByteCount);
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
            int len = (int)((seed >> 16) % 40);
            byte[] body = new byte[len];
            for (int i = 0; i < len; i++)
            {
                seed = (seed * 1664525u) + 1013904223u;
                body[i] = (byte)(seed >> 24);
            }
            if (round % 3 != 0 && len >= 2)
            {
                // 三分之二的样本把个数压小,让成功分支有足够样本
                body[0] = (byte)(body[0] % 8);
                body[1] = 0;
            }

            // 独立规则:个数 = 前两个字节的小端值;能读下 2 + 3 × count 字节才算成功
            int expectedLength = len >= 2 ? 2 + (3 * (body[0] | (body[1] << 8))) : -1;
            bool expectedOk = len >= 2 && len >= expectedLength;

            DungeonPermissionNotice n = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = DungeonPermissionParser.TryParse(body, out n); });
            Assert.Null(ex);
            Assert.Equal(expectedOk, ok);
            if (!expectedOk)
            {
                Assert.Null(n);
                continue;
            }

            accepted++;
            int count = body[0] | (body[1] << 8);
            Assert.Equal(count, n.Entries.Count);
            for (int e = 0; e < count; e++)
            {
                int at = 2 + (3 * e);
                Assert.Equal(body[at] | (body[at + 1] << 8), n.Entries[e].DungeonId);
                Assert.Equal(body[at + 2], n.Entries[e].Tier);
            }
            Assert.Equal(len - expectedLength, n.TrailingByteCount);
        }

        Assert.True(accepted > 300);
    }

    // ---- 合并成快照(Collect) ----

    [Fact]
    public void Collect_returns_null_when_there_is_no_permission_frame()
    {
        Assert.Null(DungeonPermissionParser.Collect(null));
        Assert.Null(DungeonPermissionParser.Collect(new DecodedFrame[0]));

        DecodedFrame[] unrelated =
        {
            PktImplTestKit.Frame(Noti, 300, 1, 0, 2, 0),                                    // 别的类 0 通知
            PktImplTestKit.Frame(Noti, 4, 0, 0),                             // 类 0 的包号 4:不是 5
            PktImplTestKit.Frame(Cmd, PermissionId, 1, 0, 0x85, 0x03, 0x01), // 类 1 的包号 5:类不对
            PktImplTestKit.Frame(Cmd, 4, 1, 9, 9),                           // 选角回包
        };
        Assert.Null(DungeonPermissionParser.Collect(unrelated));
    }

    [Fact]
    public void Collect_merges_with_client_upsert_semantics_and_first_seen_order()
    {
        DecodedFrame[] frames =
        {
            PermissionFrame(Permission(new[] { (901, 1), (902, 2), (903, 3) })),
            PktImplTestKit.Frame(Noti, 300, 1, 0, 2, 0),
            PermissionFrame(Permission(new[] { (904, 4), (902, 3) })),               // 902 被覆盖成 3,904 是新键
            PermissionFrame(Permission(new[] { (901, 2), (901, 4) })),               // 同一条里同键:后者胜
        };

        DungeonPermissionSnapshot s = DungeonPermissionParser.Collect(frames);

        Assert.NotNull(s);
        Assert.Equal(new[] { (901, 4), (902, 3), (903, 3), (904, 4) }, Pairs(s));
        Assert.Equal(4, s.Count);
        Assert.Equal(3, s.NoticeCount);
        Assert.Equal(0, s.MalformedNoticeCount);
        AssertListIsReadOnly(s.Entries);
    }

    [Fact]
    public void Collect_lookups_report_the_raw_stored_tier_and_absence()
    {
        DungeonPermissionSnapshot s = DungeonPermissionParser.Collect(new[]
        {
            PermissionFrame(Permission(new[] { (901, 3), (65535, 0), (0, 255) })),
        });

        Assert.True(s.Contains(901));
        Assert.True(s.Contains(65535));
        Assert.True(s.Contains(0));
        Assert.False(s.Contains(902));

        Assert.True(s.TryGetTier(901, out byte tier901));
        Assert.Equal(3, tier901);                       // 不降级
        Assert.True(s.TryGetTier(65535, out byte tierMax));
        Assert.Equal(0, tierMax);                       // 档位 0 也是"有条目",与"没有条目"不同
        Assert.True(s.TryGetTier(0, out byte tier0));
        Assert.Equal(255, tier0);

        Assert.False(s.TryGetTier(902, out byte absent));
        Assert.Equal(0, absent);
    }

    [Fact]
    public void Collect_counts_malformed_notices_and_still_merges_the_good_ones()
    {
        DecodedFrame[] frames =
        {
            PermissionFrame(PermissionWithDeclaredCount(3, SyntheticEntries(2))),    // 个数越界:整包丢弃
            PermissionFrame(Permission(new[] { (901, 2) })),
            PermissionFrame(new byte[] { 0x01 }),                                    // 连个数都读不全
            PermissionFrame(new byte[0]),
        };

        DungeonPermissionSnapshot s = DungeonPermissionParser.Collect(frames);

        Assert.NotNull(s);
        Assert.Equal(new[] { (901, 2) }, Pairs(s));
        Assert.Equal(1, s.NoticeCount);
        Assert.Equal(3, s.MalformedNoticeCount);
    }

    [Fact]
    public void Only_malformed_notices_give_a_non_null_empty_snapshot()
    {
        DungeonPermissionSnapshot s = DungeonPermissionParser.Collect(new[] { PermissionFrame(new byte[] { 0x05, 0x00, 0x01 }) });

        Assert.NotNull(s);
        Assert.Empty(s.Entries);
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.NoticeCount);
        Assert.Equal(1, s.MalformedNoticeCount);
        AssertListIsReadOnly(s.Entries);
    }

    [Fact]
    public void A_zero_entry_table_gives_a_non_null_empty_snapshot_unlike_no_frame_at_all()
    {
        DungeonPermissionSnapshot s = DungeonPermissionParser.Collect(new[] { PermissionFrame(Permission(new (int, int)[0])) });

        Assert.NotNull(s);
        Assert.Empty(s.Entries);
        Assert.Equal(1, s.NoticeCount);
        Assert.Equal(0, s.MalformedNoticeCount);
        Assert.False(s.Contains(901));
    }

    [Fact]
    public void Collect_skips_null_elements_and_accepts_any_read_only_list()
    {
        DecodedFrame[] frames =
        {
            null,
            PermissionFrame(Permission(new[] { (901, 1) })),
            null,
        };

        DungeonPermissionSnapshot fromArray = DungeonPermissionParser.Collect(frames);
        DungeonPermissionSnapshot fromList = DungeonPermissionParser.Collect(new List<DecodedFrame>(frames));

        Assert.Equal(new[] { (901, 1) }, Pairs(fromArray));
        Assert.Equal(new[] { (901, 1) }, Pairs(fromList));
    }

    [Fact]
    public void Collect_reads_the_payload_of_a_decoded_frame_with_trailing_bytes()
    {
        // 真实帧的载荷尾部有折叠哈希:Collect 同样不要求读完恰好等于末尾
        byte[] withHash = PacketIntegrity.AppendFoldHash(Permission(SyntheticEntries(5, variant: 6)));
        DungeonPermissionSnapshot s = DungeonPermissionParser.Collect(new[] { PermissionFrame(withHash) });

        Assert.Equal(SyntheticEntries(5, variant: 6), Pairs(s));
        Assert.Equal(1, s.NoticeCount);
    }

    [Fact]
    public void Snapshot_does_not_alias_the_frames_and_collect_is_deterministic()
    {
        DecodedFrame frame = PermissionFrame(Permission(SyntheticEntries(4, variant: 8)));
        byte[] payloadBefore = (byte[])frame.Payload.Clone();

        DungeonPermissionSnapshot first = DungeonPermissionParser.Collect(new[] { frame });
        DungeonPermissionSnapshot second = DungeonPermissionParser.Collect(new[] { frame });

        Assert.NotSame(first, second);
        Assert.Equal(Pairs(first), Pairs(second));
        Assert.Equal(payloadBefore, frame.Payload);

        // 把帧的载荷改写,快照不跟着变
        Array.Fill(frame.Payload, (byte)0xEE);
        Assert.Equal(SyntheticEntries(4, variant: 8), Pairs(first));
    }

    // ---- 与 packetread 自验收清单(C01 到 C14)的对读 ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(25)]
    [InlineData(200)]
    public void C05_the_sample_length_formula_two_plus_three_n_holds_and_leaves_no_tail_when_no_hash_is_appended(int count)
    {
        // C05 的样本公式:(0,5) 载荷长度 = 2 + 3n,读序走完剩余 0 字节(样本载荷大于 0x190,服务端不追加折叠哈希)。
        // 这里用同样的公式造载荷:长度大于 0x190 时不追加哈希,解析后剩余 0。
        byte[] body = Permission(SyntheticEntries(count, variant: count));
        Assert.Equal(2 + (3 * count), body.Length);
        byte[] onTheWire = PacketIntegrity.AppendFoldHash(body);

        DungeonPermissionNotice n = ParseOk(onTheWire);

        Assert.Equal(count, n.Entries.Count);
        Assert.Equal(PacketIntegrity.HasFoldHash(body.Length) ? 4 : 0, n.TrailingByteCount);
    }

    [Fact]
    public void C05_a_payload_over_the_fold_hash_limit_carries_no_tail_and_is_consumed_exactly()
    {
        byte[] body = Permission(SyntheticEntries(140, variant: 2));      // 2 + 420 = 422 > 0x190
        Assert.True(body.Length > PacketIntegrity.FoldHashMaxPayload);
        Assert.Equal(body, PacketIntegrity.AppendFoldHash(body));

        DungeonPermissionNotice n = ParseOk(body);

        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void C05_the_table_key_ascending_order_is_not_a_validity_condition()
    {
        // 键严格升序是服务端 std::map 遍历的性质(样本里键严格升序),解析器不把它当合法性条件:乱序、重复照收
        DungeonPermissionNotice descending = ParseOk(Permission(new[] { (40000, 2), (1357, 2), (901, 2) }));
        Assert.Equal(new[] { (40000, 2), (1357, 2), (901, 2) }, Pairs(descending));
    }

    [Fact]
    public void C05_tier_values_other_than_one_to_three_are_kept_four_is_the_hero_quest_special_case()
    {
        // 值域 {1, 2, 3},英雄任务特例 4;解析器不检查范围
        DungeonPermissionNotice n = ParseOk(Permission(new[] { (901, 1), (902, 2), (903, 3), (904, 4), (905, 0), (906, 9) }));
        Assert.Equal(new[] { (901, 1), (902, 2), (903, 3), (904, 4), (905, 0), (906, 9) }, Pairs(n));
    }
}
