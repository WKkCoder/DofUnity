using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;
using static Dnf.Net.Tests.SettlementTestPayloads;

namespace Dnf.Net.Tests;

/// <summary>
/// 通关奖励通知(类 0、包号 0x23)的明文载荷解析器(纯函数)。载荷全是手工拼的合成字节(手写字节数组或 <see cref="SettlementTestPayloads"/> 的独立写入器),
/// 编号取 900 以上的合成值,不读任何真实帧文件,不含任何真实数值。
/// 读序依据 settlenoti_NOTES §2.4(服务端写序;客户端处理器是虚拟化桩,读不出读序,§3.3):
/// <c>i32 × 4, u8, i32 × 12, u8 物品个数, 个数 × 15 字节物品条目, 4 × 奖励信息, i32, u8 × 4, 4 × 奖励信息, i32, u8 × 3</c>;
/// 奖励信息按首字节状态分三种长度(0:1 字节;1:9 字节;2:17 字节)。载荷最短 89 字节。读完最后一个标志即停,尾巴只报告个数。
/// </summary>
public class SettlementNoticeBParserTests
{
    // 规则里的包号写字面量,不引用产品常量:产品常量被改动时,测试要能发现。
    private const ushort RewardId = 0x23;

    /// <summary>
    /// 手写的标准载荷(逐字段注释,由独立的 Python 写入器按服务端写序生成后粘贴):2 条任务物品,第一组奖励信息的状态依次 2、1、0、1,
    /// 第二组依次 0、2、0、0,三个标志 1、0、1。长度 167。
    /// </summary>
    private static readonly byte[] LiteralPayload =
    {
        0xE8, 0x03, 0x00, 0x00,           // [0..3] ExpValues[0] = 1000
        0xFA, 0x00, 0x00, 0x00,           // [4..7] ExpValues[1] = 250
        0x4D, 0x00, 0x00, 0x00,           // [8..11] ExpValues[2] = 77
        0x0C, 0x00, 0x00, 0x00,           // [12..15] ExpValues[3] = 12
        0x02,                             // [16..16] ExpKind = 2
        0x59, 0x01, 0x00, 0x00,           // [17..20] ExpValues[4] = 345
        0x15, 0x00, 0x00, 0x00,           // [21..24] ExpValues[5] = 21
        0x16, 0x00, 0x00, 0x00,           // [25..28] ExpValues[6] = 22
        0x17, 0x00, 0x00, 0x00,           // [29..32] ExpValues[7] = 23
        0x18, 0x00, 0x00, 0x00,           // [33..36] ExpValues[8] = 24
        0x19, 0x00, 0x00, 0x00,           // [37..40] ExpValues[9] = 25
        0x1A, 0x00, 0x00, 0x00,           // [41..44] ExpValues[10] = 26
        0x1B, 0x00, 0x00, 0x00,           // [45..48] ExpValues[11] = 27
        0x1C, 0x00, 0x00, 0x00,           // [49..52] ExpValues[12] = 28
        0x1D, 0x00, 0x00, 0x00,           // [53..56] ExpValues[13] = 29
        0x1E, 0x00, 0x00, 0x00,           // [57..60] ExpValues[14] = 30
        0x1F, 0x00, 0x00, 0x00,           // [61..64] ExpValues[15] = 31
        0x02,                             // [65..65] itemCount = 2
        0x07, 0x00,                       // [66..67] item0.Slot = 7
        0x85, 0x03, 0x00, 0x00,           // [68..71] item0.ItemId = 901
        0x03, 0x00, 0x00, 0x00,           // [72..75] item0.Data = 3
        0x64, 0x00,                       // [76..77] item0.Durability = 100
        0x02,                             // [78..78] item0.AmplifyType = 2
        0x0F, 0x00,                       // [79..80] item0.AmplifyValue = 15
        0x02, 0x01,                       // [81..82] item1.Slot = 258
        0x40, 0x9C, 0x00, 0x00,           // [83..86] item1.ItemId = 40000
        0x0C, 0x00, 0x00, 0x00,           // [87..90] item1.Data = 12
        0x32, 0x00,                       // [91..92] item1.Durability = 50
        0x00,                             // [93..93] item1.AmplifyType = 0
        0x00, 0x00,                       // [94..95] item1.AmplifyValue = 0
        0x02,                             // [96..96] reward0.Status = 2
        0x00, 0x00, 0x00, 0x00,           // [97..100] reward0.Reserved = 0
        0x88, 0x13, 0x00, 0x00,           // [101..104] reward0.Value = 5000
        0x85, 0x03, 0x00, 0x00,           // [105..108] reward0.ItemId = 901
        0x04, 0x00, 0x00, 0x00,           // [109..112] reward0.ItemData = 4
        0x01,                             // [113..113] reward1.Status = 1
        0x00, 0x00, 0x00, 0x00,           // [114..117] reward1.Reserved = 0
        0x70, 0x17, 0x00, 0x00,           // [118..121] reward1.Value = 6000
        0x00,                             // [122..122] reward2.Status = 0
        0x01,                             // [123..123] reward3.Status = 1
        0x00, 0x00, 0x00, 0x00,           // [124..127] reward3.Reserved = 0
        0x58, 0x1B, 0x00, 0x00,           // [128..131] reward3.Value = 7000
        0x32, 0x79, 0x06, 0x00,           // [132..135] ReceiverValue = 424242
        0x00,                             // [136..136] reserved0 = 0
        0x00,                             // [137..137] reserved1 = 0
        0x00,                             // [138..138] reserved2 = 0
        0x00,                             // [139..139] reserved3 = 0
        0x00,                             // [140..140] premium0.Status = 0
        0x02,                             // [141..141] premium1.Status = 2
        0x00, 0x00, 0x00, 0x00,           // [142..145] premium1.Reserved = 0
        0x28, 0x23, 0x00, 0x00,           // [146..149] premium1.Value = 9000
        0x40, 0x9C, 0x00, 0x00,           // [150..153] premium1.ItemId = 40000
        0x05, 0x00, 0x00, 0x00,           // [154..157] premium1.ItemData = 5
        0x00,                             // [158..158] premium2.Status = 0
        0x00,                             // [159..159] premium3.Status = 0
        0x55, 0x00, 0x00, 0x00,           // [160..163] Percent = 85
        0x01,                             // [164..164] IsReturnUserParty = 1
        0x00,                             // [165..165] IsEventCharacParty = 0
        0x01,                             // [166..166] HasDeadUserPenalty = 1
    };

    /// <summary>
    /// 字节所有权表(由独立的 Python 生成器按写序给出):(起始, 长度, 签名下标)。结构性字节(物品个数与各奖励信息状态字节)不在表里,改它们会改变结构。
    /// 签名下标 0..16 是经验分项与活动类型(0..3 前四项、4 活动类型、5..16 后十二项),17..28 两条物品各 6 个字段,29..32 是奖励 0 的四个数,
    /// 33..34 奖励 1,35..36 奖励 3,37 接收者数组项,38..41 保留字节,42..45 第二组的奖励 1,46 比例,47..49 三个标志。
    /// </summary>
    private static readonly (int Start, int Length, int Sig)[] Ownership =
    {
        (0, 4, 0), (4, 4, 1), (8, 4, 2), (12, 4, 3), (16, 1, 4), (17, 4, 5), (21, 4, 6), (25, 4, 7), (29, 4, 8), (33, 4, 9),
        (37, 4, 10), (41, 4, 11), (45, 4, 12), (49, 4, 13), (53, 4, 14), (57, 4, 15), (61, 4, 16),
        (66, 2, 17), (68, 4, 18), (72, 4, 19), (76, 2, 20), (78, 1, 21), (79, 2, 22),
        (81, 2, 23), (83, 4, 24), (87, 4, 25), (91, 2, 26), (93, 1, 27), (94, 2, 28),
        (97, 4, 29), (101, 4, 30), (105, 4, 31), (109, 4, 32), (114, 4, 33), (118, 4, 34), (124, 4, 35), (128, 4, 36),
        (132, 4, 37), (136, 1, 38), (137, 1, 39), (138, 1, 40), (139, 1, 41),
        (142, 4, 42), (146, 4, 43), (150, 4, 44), (154, 4, 45), (160, 4, 46), (164, 1, 47), (165, 1, 48), (166, 1, 49),
    };

    // ---- 助手 ----

    private static SettlementNoticeB ParseOk(byte[] payload)
    {
        Assert.True(SettlementNoticeBParser.TryParse(payload, out SettlementNoticeB notice));
        Assert.NotNull(notice);
        return notice;
    }

    /// <summary>被拒:不抛异常、返回 false、notice 为 null。</summary>
    private static void AssertRejected(byte[] payload)
    {
        SettlementNoticeB notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = SettlementNoticeBParser.TryParse(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    private static void AssertReward(RewardSpec s, SettlementRewardInfo r)
    {
        Assert.Equal((byte)s.Status, r.Status);
        Assert.Equal(s.Status >= 1 ? s.Reserved : 0, r.Reserved);
        Assert.Equal(s.Status >= 1 ? s.Value : 0, r.Value);
        Assert.Equal(s.Status == 2, r.HasItem);
        Assert.Equal(s.Status == 2 ? s.ItemId : 0, r.ItemId);
        Assert.Equal(s.Status == 2 ? s.ItemData : 0, r.ItemData);
    }

    /// <summary>逐字段核对:解析结果与构造它的合成规格一致。</summary>
    private static void AssertMatches(BSpec spec, SettlementNoticeB n)
    {
        Assert.Equal(SettlementNoticeB.ExpValueCount, n.ExpValues.Count);
        for (int i = 0; i < spec.Exp.Length; i++)
        {
            Assert.Equal(spec.Exp[i], n.ExpValues[i]);
        }
        Assert.Equal((byte)spec.Kind, n.ExpKind);
        Assert.Equal(spec.Exp[0], n.BaseExp);
        Assert.Equal(spec.Exp[1], n.RankBonusExp);

        Assert.Equal(spec.Items.Count, n.QuestItems.Count);
        for (int i = 0; i < spec.Items.Count; i++)
        {
            Assert.Equal((ushort)spec.Items[i].Slot, n.QuestItems[i].Slot);
            Assert.Equal(spec.Items[i].ItemId, n.QuestItems[i].ItemId);
            Assert.Equal(spec.Items[i].Data, n.QuestItems[i].Data);
            Assert.Equal((ushort)spec.Items[i].Durability, n.QuestItems[i].Durability);
            Assert.Equal((byte)spec.Items[i].AmplifyType, n.QuestItems[i].AmplifyType);
            Assert.Equal((ushort)spec.Items[i].AmplifyValue, n.QuestItems[i].AmplifyValue);
        }

        Assert.Equal(4, n.Rewards.Count);
        Assert.Equal(4, n.PremiumRewards.Count);
        for (int i = 0; i < 4; i++)
        {
            AssertReward(spec.Rewards[i], n.Rewards[i]);
            AssertReward(spec.Premium[i], n.PremiumRewards[i]);
        }

        Assert.Equal(spec.ReceiverValue, n.ReceiverValue);
        Assert.Equal(4, n.ReservedBytes.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((byte)spec.Reserved[i], n.ReservedBytes[i]);
        }
        Assert.Equal(spec.Percent, n.Percent);
        Assert.Equal(spec.Flags[0] != 0, n.IsReturnUserParty);
        Assert.Equal(spec.Flags[1] != 0, n.IsEventCharacParty);
        Assert.Equal(spec.Flags[2] != 0, n.HasDeadUserPenalty);
    }

    /// <summary>签名:全部非结构字段按写序排成数组(下标同 <see cref="Ownership"/>),用来核对"只变一个字段"。</summary>
    private static object[] Signature(SettlementNoticeB n)
    {
        var s = new List<object>();
        for (int i = 0; i < 4; i++) { s.Add(n.ExpValues[i]); }
        s.Add(n.ExpKind);
        for (int i = 4; i < 16; i++) { s.Add(n.ExpValues[i]); }
        foreach (SettlementItemEntry it in n.QuestItems)
        {
            s.Add(it.Slot); s.Add(it.ItemId); s.Add(it.Data); s.Add(it.Durability); s.Add(it.AmplifyType); s.Add(it.AmplifyValue);
        }
        foreach (int k in new[] { 0, 1, 3 })
        {
            s.Add(n.Rewards[k].Reserved);
            s.Add(n.Rewards[k].Value);
            if (n.Rewards[k].HasItem) { s.Add(n.Rewards[k].ItemId); s.Add(n.Rewards[k].ItemData); }
        }
        s.Add(n.ReceiverValue);
        for (int i = 0; i < 4; i++) { s.Add(n.ReservedBytes[i]); }
        s.Add(n.PremiumRewards[1].Reserved);
        s.Add(n.PremiumRewards[1].Value);
        s.Add(n.PremiumRewards[1].ItemId);
        s.Add(n.PremiumRewards[1].ItemData);
        s.Add(n.Percent);
        s.Add(n.IsReturnUserParty);
        s.Add(n.IsEventCharacParty);
        s.Add(n.HasDeadUserPenalty);
        return s.ToArray();
    }

    private static List<ItemSpec> SyntheticItems(int count, int variant = 0)
    {
        var list = new List<ItemSpec>();
        for (int i = 0; i < count; i++)
        {
            list.Add(new ItemSpec((3 * i + variant) & 0xFFFF, 900 + (11 * i) + variant, 1 + i + variant, (100 + i) & 0xFFFF, (i + variant) & 0xFF, (7 * i + variant) & 0xFFFF));
        }
        return list;
    }

    // ---- 常量与长度 ----

    [Fact]
    public void Production_constant_matches_the_documented_id()
    {
        Assert.Equal(0x23, SettlementNoticeBParser.PacketId);
        Assert.Equal(35, SettlementNoticeBParser.PacketId);
        Assert.Equal(16, SettlementNoticeB.ExpValueCount);
        Assert.Equal(4, SettlementNoticeB.RewardSlotCount);
    }

    [Fact]
    public void The_length_arithmetic_of_the_documented_layout_holds()
    {
        // 最短载荷:65(经验块)+ 1(个数)+ 4(第一组状态字节)+ 4(接收者数组项)+ 4(保留字节)+ 4(第二组状态字节)+ 4(比例)+ 3(标志)= 89
        var minimal = new BSpec
        {
            Items = new List<ItemSpec>(),
            Rewards = new List<RewardSpec> { new RewardSpec(0), new RewardSpec(0), new RewardSpec(0), new RewardSpec(0) },
            Premium = new List<RewardSpec> { new RewardSpec(0), new RewardSpec(0), new RewardSpec(0), new RewardSpec(0) },
        };
        Assert.Equal(89, minimal.Build().Length);
        Assert.Equal(89, minimal.ExpectedLength());

        // 标准载荷:89 + 15 × 2 + (17 - 1) + (9 - 1) + (9 - 1) + (17 - 1) = 167
        Assert.Equal(167, LiteralPayload.Length);
        Assert.Equal(167, new BSpec().ExpectedLength());
        Assert.Equal(89 + (15 * 2) + 16 + 8 + 8 + 16, LiteralPayload.Length);
    }

    // ---- 逐字段(手写字节,不经写入器) ----

    [Fact]
    public void A_literal_payload_is_read_field_by_field()
    {
        SettlementNoticeB n = ParseOk(LiteralPayload);

        Assert.Equal(new[] { 1000, 250, 77, 12, 345, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 }, n.ExpValues.ToArray());
        Assert.Equal(1000, n.BaseExp);
        Assert.Equal(250, n.RankBonusExp);
        Assert.Equal(2, n.ExpKind);

        Assert.Equal(2, n.QuestItems.Count);
        Assert.Equal(7, n.QuestItems[0].Slot);
        Assert.Equal(901, n.QuestItems[0].ItemId);
        Assert.Equal(3, n.QuestItems[0].Data);
        Assert.Equal(100, n.QuestItems[0].Durability);
        Assert.Equal(2, n.QuestItems[0].AmplifyType);
        Assert.Equal(15, n.QuestItems[0].AmplifyValue);
        Assert.Equal(258, n.QuestItems[1].Slot);
        Assert.Equal(40000, n.QuestItems[1].ItemId);
        Assert.Equal(12, n.QuestItems[1].Data);
        Assert.Equal(50, n.QuestItems[1].Durability);
        Assert.Equal(0, n.QuestItems[1].AmplifyType);
        Assert.Equal(0, n.QuestItems[1].AmplifyValue);

        Assert.Equal(new byte[] { 2, 1, 0, 1 }, n.Rewards.Select(r => r.Status).ToArray());
        Assert.Equal(5000, n.Rewards[0].Value);
        Assert.Equal(901, n.Rewards[0].ItemId);
        Assert.Equal(4, n.Rewards[0].ItemData);
        Assert.True(n.Rewards[0].HasItem);
        Assert.Equal(6000, n.Rewards[1].Value);
        Assert.False(n.Rewards[1].HasItem);
        Assert.Equal(0, n.Rewards[2].Value);
        Assert.Equal(7000, n.Rewards[3].Value);

        Assert.Equal(424242, n.ReceiverValue);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, n.ReservedBytes.ToArray());

        Assert.Equal(new byte[] { 0, 2, 0, 0 }, n.PremiumRewards.Select(r => r.Status).ToArray());
        Assert.Equal(9000, n.PremiumRewards[1].Value);
        Assert.Equal(40000, n.PremiumRewards[1].ItemId);
        Assert.Equal(5, n.PremiumRewards[1].ItemData);

        Assert.Equal(85, n.Percent);
        Assert.True(n.IsReturnUserParty);
        Assert.False(n.IsEventCharacParty);
        Assert.True(n.HasDeadUserPenalty);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void The_independent_writer_reproduces_the_literal_payload_byte_for_byte()
    {
        Assert.Equal(LiteralPayload, new BSpec().Build());
    }

    [Fact]
    public void Every_non_structural_byte_belongs_to_exactly_one_field_and_changes_only_that_field()
    {
        object[] before = Signature(ParseOk(LiteralPayload));
        Assert.Equal(50, before.Length);
        Assert.Equal(50, Ownership.Length);

        int covered = 0;
        foreach ((int start, int length, int sig) in Ownership)
        {
            for (int pos = start; pos < start + length; pos++)
            {
                covered++;
                byte[] changed = (byte[])LiteralPayload.Clone();
                changed[pos] ^= pos >= 164 ? (byte)0x01 : (byte)0x5A;       // 三个标志翻 0 与 1,别的字节翻几位
                object[] after = Signature(ParseOk(changed));

                Assert.Equal(before.Length, after.Length);
                for (int s = 0; s < before.Length; s++)
                {
                    Assert.Equal(s != sig, Equals(before[s], after[s]));
                }
            }
        }

        // 表外只有 9 个结构性字节:物品个数(65)与 8 个奖励信息的状态字节
        Assert.Equal(LiteralPayload.Length - 9, covered);
    }

    [Fact]
    public void Each_field_takes_independent_values_from_the_independent_writer()
    {
        var spec = new BSpec
        {
            Exp = new[] { 11, 22, 33, 44, 55, 66, 77, 88, 99, 111, 222, 333, 444, 555, 666, 777 },
            Kind = 4,
            Items = SyntheticItems(3, variant: 5),
            Rewards = new List<RewardSpec> { new RewardSpec(1, 5, 6), new RewardSpec(2, 7, 8, 902, 9), new RewardSpec(2, 10, 11, 903, 12), new RewardSpec(0) },
            ReceiverValue = 31337,
            Reserved = new[] { 1, 2, 3, 4 },
            Premium = new List<RewardSpec> { new RewardSpec(2, 13, 14, 904, 15), new RewardSpec(1, 16, 17), new RewardSpec(0), new RewardSpec(2, 18, 19, 905, 20) },
            Percent = 99,
            Flags = new[] { 0, 1, 0 },
        };

        AssertMatches(spec, ParseOk(spec.Build()));
    }

    // ---- 无符号与带符号的取法 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x100)]
    [InlineData(0x10000)]
    [InlineData(0x01000000)]
    [InlineData(0x7FFFFFFF)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void Every_i32_field_is_signed_little_endian(int value)
    {
        var spec = new BSpec
        {
            Items = new List<ItemSpec> { new ItemSpec(1, value, ~value, 1, 1, 1) },
            Rewards = new List<RewardSpec> { new RewardSpec(2, value, ~value, value, ~value), new RewardSpec(1, ~value, value), new RewardSpec(0), new RewardSpec(0) },
            Premium = new List<RewardSpec> { new RewardSpec(0), new RewardSpec(0), new RewardSpec(2, ~value, value, ~value, value), new RewardSpec(0) },
            ReceiverValue = value,
            Percent = ~value,
        };
        spec.Exp[0] = value;
        spec.Exp[3] = ~value;
        spec.Exp[4] = value;
        spec.Exp[15] = ~value;

        SettlementNoticeB n = ParseOk(spec.Build());

        AssertMatches(spec, n);
        Assert.Equal(value, n.BaseExp);
        Assert.Equal(value, n.ReceiverValue);
        Assert.Equal(~value, n.Percent);
        Assert.Equal(value, n.QuestItems[0].ItemId);
        Assert.Equal(~value, n.QuestItems[0].Data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x0100)]
    [InlineData(0x7FFF)]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public void The_item_slot_durability_and_amplify_value_are_unsigned_16_bit(int value)
    {
        var spec = new BSpec { Items = new List<ItemSpec> { new ItemSpec(value, 901, 1, value, 3, value) } };

        SettlementItemEntry it = ParseOk(spec.Build()).QuestItems[0];

        Assert.Equal(value, (int)it.Slot);
        Assert.Equal(value, (int)it.Durability);
        Assert.Equal(value, (int)it.AmplifyValue);
        Assert.Equal(typeof(ushort), typeof(SettlementItemEntry).GetProperty(nameof(SettlementItemEntry.Slot)).PropertyType);
        Assert.Equal(typeof(ushort), typeof(SettlementItemEntry).GetProperty(nameof(SettlementItemEntry.Durability)).PropertyType);
        Assert.Equal(typeof(ushort), typeof(SettlementItemEntry).GetProperty(nameof(SettlementItemEntry.AmplifyValue)).PropertyType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void The_exp_kind_amplify_type_and_reserved_bytes_are_unsigned_bytes(int value)
    {
        var spec = new BSpec
        {
            Kind = value,
            Items = new List<ItemSpec> { new ItemSpec(1, 901, 1, 1, value, 1) },
            Reserved = new[] { value, 0, value, 1 },
        };

        SettlementNoticeB n = ParseOk(spec.Build());

        Assert.Equal(value, n.ExpKind);
        Assert.Equal(value, n.QuestItems[0].AmplifyType);
        Assert.Equal(new[] { value, 0, value, 1 }, n.ReservedBytes.Select(b => (int)b).ToArray());
    }

    // ---- 标志:非 0 为 true ----

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0x80, true)]
    [InlineData(0xFF, true)]
    public void Each_flag_is_true_for_any_nonzero_byte_and_independent_of_the_others(int wire, bool expected)
    {
        for (int which = 0; which < 3; which++)
        {
            int[] flags = new int[3];
            flags[which] = wire;

            SettlementNoticeB n = ParseOk(new BSpec { Flags = flags }.Build());

            Assert.Equal(which == 0 && expected, n.IsReturnUserParty);
            Assert.Equal(which == 1 && expected, n.IsEventCharacParty);
            Assert.Equal(which == 2 && expected, n.HasDeadUserPenalty);
        }
    }

    // ---- 物品个数 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(255)]
    public void Item_counts_at_the_boundaries_are_read_exactly(int count)
    {
        var spec = new BSpec { Items = SyntheticItems(count, variant: count % 5) };
        byte[] payload = spec.Build();
        Assert.Equal(spec.ExpectedLength(), payload.Length);
        Assert.Equal(167 - 30 + (15 * count), payload.Length);

        SettlementNoticeB n = ParseOk(payload);

        AssertMatches(spec, n);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Zero_items_is_a_valid_empty_list_and_never_null()
    {
        SettlementNoticeB n = ParseOk(new BSpec { Items = new List<ItemSpec>() }.Build());

        Assert.NotNull(n.QuestItems);
        Assert.Empty(n.QuestItems);
        AssertReadOnlyWrapper(n.QuestItems);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(255, 0)]
    [InlineData(255, 100)]
    [InlineData(255, 250)]
    public void A_declared_item_count_far_above_the_actual_items_is_rejected(int declared, int actualItems)
    {
        // 物品个数字节之后固定尾部(各组状态都是缺省的)是 71 字节,最小的合法尾部是 23 字节。
        // 声明比实际多 4 条以上时,物品段会吃掉 60 字节以上的尾部,剩下不足 23 字节,必拒(条数只差 1 到 3 时错位解读有可能凑巧读完,本测试不断言)。
        var spec = new BSpec { Items = SyntheticItems(actualItems), DeclaredItemCount = declared };

        AssertRejected(spec.Build());
        AssertRejected(spec.Build(0, 0, 0));
    }

    [Fact]
    public void A_declared_item_count_that_disagrees_with_the_bytes_never_throws_and_an_accepted_parse_echoes_the_declared_count()
    {
        for (int declared = 0; declared <= 40; declared++)
        {
            byte[] payload = new BSpec { Items = SyntheticItems(2, variant: 3), DeclaredItemCount = declared }.Build();

            SettlementNoticeB n = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = SettlementNoticeBParser.TryParse(payload, out n); });

            Assert.Null(ex);
            if (ok)
            {
                Assert.Equal(declared, n.QuestItems.Count);
            }
            else
            {
                Assert.Null(n);
            }

            if (declared == 2)
            {
                // 只有声明与实际一致时,才恰好读完且第二条物品的编号是合成值 900 + 11 + 3
                Assert.True(ok);
                Assert.Equal(0, n.TrailingByteCount);
                Assert.Equal(914, n.QuestItems[1].ItemId);
            }
        }
    }

    // ---- 奖励信息的三种长度 ----

    private static List<RewardSpec> RewardsWithStatuses(int combination, int salt)
    {
        // combination 取 0..80,按三进制拆成四个状态;各数值互不相同,看得出谁读成了谁
        return Enumerable.Range(0, 4)
            .Select(i => new RewardSpec((combination / (int)Math.Pow(3, i)) % 3, salt + i + combination, salt + 100 + (3 * i) + combination, salt + 900 + i + combination, salt + 400 + i))
            .ToList();
    }

    [Fact]
    public void Every_status_combination_of_the_first_group_is_read_with_the_documented_lengths()
    {
        for (int a = 0; a < 81; a++)
        {
            var spec = new BSpec
            {
                Items = SyntheticItems(a % 3, variant: a),
                Rewards = RewardsWithStatuses(a, salt: 1000),
                ReceiverValue = 5000 + a,
                Percent = a,
            };
            byte[] payload = spec.Build();
            Assert.Equal(spec.ExpectedLength(), payload.Length);

            SettlementNoticeB n = ParseOk(payload);

            AssertMatches(spec, n);
            Assert.Equal(0, n.TrailingByteCount);
        }
    }

    [Fact]
    public void Every_status_combination_of_the_second_group_is_read_with_the_documented_lengths()
    {
        for (int a = 0; a < 81; a++)
        {
            var spec = new BSpec
            {
                Items = SyntheticItems(a % 3, variant: a),
                Premium = RewardsWithStatuses(a, salt: 2000),
                ReceiverValue = 6000 + a,
                Percent = a,
            };
            byte[] payload = spec.Build();
            Assert.Equal(spec.ExpectedLength(), payload.Length);

            SettlementNoticeB n = ParseOk(payload);

            AssertMatches(spec, n);
            Assert.Equal(0, n.TrailingByteCount);
        }
    }

    [Fact]
    public void Status_combinations_of_both_groups_together_keep_the_two_groups_apart()
    {
        for (int a = 0; a < 81; a++)
        {
            int b = (a * 7 + 5) % 81;
            var spec = new BSpec
            {
                Rewards = RewardsWithStatuses(a, salt: 1000),
                Premium = RewardsWithStatuses(b, salt: 2000),
                ReceiverValue = 7000 + a,
            };
            byte[] payload = spec.Build();
            Assert.Equal(spec.ExpectedLength(), payload.Length);

            AssertMatches(spec, ParseOk(payload));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void A_reward_info_status_other_than_zero_one_two_is_rejected_in_every_slot(int status)
    {
        for (int slot = 0; slot < 8; slot++)
        {
            var spec = new BSpec();
            RewardSpec bad = new RewardSpec(status, 1, 2, 903, 4);
            if (slot < 4)
            {
                spec.Rewards[slot] = bad;
            }
            else
            {
                spec.Premium[slot - 4] = bad;
            }

            AssertRejected(spec.Build());
            AssertRejected(spec.Build(0, 0, 0, 0));        // 后面再多几个字节也不改变:状态非法就停
        }
    }

    [Fact]
    public void The_two_groups_are_not_swapped_and_the_receiver_value_sits_between_them()
    {
        var spec = new BSpec
        {
            Rewards = new List<RewardSpec> { new RewardSpec(1, 0, 11), new RewardSpec(1, 0, 12), new RewardSpec(1, 0, 13), new RewardSpec(1, 0, 14) },
            Premium = new List<RewardSpec> { new RewardSpec(1, 0, 21), new RewardSpec(1, 0, 22), new RewardSpec(1, 0, 23), new RewardSpec(1, 0, 24) },
            ReceiverValue = 999,
            Reserved = new[] { 9, 8, 7, 6 },
        };

        SettlementNoticeB n = ParseOk(spec.Build());

        Assert.Equal(new[] { 11, 12, 13, 14 }, n.Rewards.Select(r => r.Value).ToArray());
        Assert.Equal(new[] { 21, 22, 23, 24 }, n.PremiumRewards.Select(r => r.Value).ToArray());
        Assert.Equal(999, n.ReceiverValue);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, n.ReservedBytes.ToArray());
    }

    // ---- 截断 ----

    [Fact]
    public void Every_proper_prefix_of_a_payload_is_rejected()
    {
        for (int len = 0; len < LiteralPayload.Length; len++)
        {
            AssertRejected(LiteralPayload.Take(len).ToArray());
        }
        Assert.True(SettlementNoticeBParser.TryParse(LiteralPayload, out _));
    }

    [Fact]
    public void A_truncation_inside_any_field_of_a_payload_with_every_part_present_is_rejected()
    {
        var spec = new BSpec
        {
            Items = SyntheticItems(4, variant: 2),
            Rewards = Enumerable.Range(0, 4).Select(i => new RewardSpec(2, i, 10 + i, 900 + i, 20 + i)).ToList(),
            Premium = Enumerable.Range(0, 4).Select(i => new RewardSpec(2, i, 30 + i, 910 + i, 40 + i)).ToList(),
        };
        byte[] payload = spec.Build();
        Assert.Equal(89 + 60 + (8 * 16), payload.Length);

        for (int len = 0; len < payload.Length; len++)
        {
            AssertRejected(payload.Take(len).ToArray());
        }
        Assert.True(SettlementNoticeBParser.TryParse(payload, out _));
    }

    [Fact]
    public void Null_and_tiny_payloads_are_rejected_without_throwing()
    {
        AssertRejected(null);
        AssertRejected(new byte[0]);
        for (int len = 1; len < 89; len++)
        {
            AssertRejected(new byte[len]);
        }

        // 恰好 89 个零字节:0 条物品、全部状态 0、标志全 0,是最短的合法载荷
        SettlementNoticeB zero = ParseOk(new byte[89]);
        Assert.Empty(zero.QuestItems);
        Assert.All(zero.Rewards, r => Assert.Equal(0, r.Status));
        Assert.All(zero.PremiumRewards, r => Assert.Equal(0, r.Status));
        Assert.Equal(0, zero.TrailingByteCount);
        Assert.False(zero.IsReturnUserParty);
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
        var spec = new BSpec();

        SettlementNoticeB n = ParseOk(spec.Build(tail));

        AssertMatches(spec, n);
        Assert.Equal(tailLength, n.TrailingByteCount);
    }

    [Fact]
    public void A_server_style_fold_hash_tail_and_encryption_zero_padding_are_both_just_trailing_bytes()
    {
        var spec = new BSpec();
        byte[] withHash = PacketIntegrity.AppendFoldHash(spec.Build());
        Assert.Equal(171, withHash.Length);                 // 载荷长度在 1 到 0x190 之间,服务端追加 4 字节折叠哈希

        SettlementNoticeB hashed = ParseOk(withHash);
        AssertMatches(spec, hashed);
        Assert.Equal(4, hashed.TrailingByteCount);

        SettlementNoticeB padded = ParseOk(withHash.Concat(new byte[5]).ToArray());
        AssertMatches(spec, padded);
        Assert.Equal(9, padded.TrailingByteCount);
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void Record_types_expose_exactly_the_documented_members_and_types()
    {
        string[] notice = typeof(SettlementNoticeB).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "BaseExp", "ExpKind", "ExpValues", "HasDeadUserPenalty", "IsEventCharacParty", "IsReturnUserParty", "Percent", "PremiumRewards",
                "QuestItems", "RankBonusExp", "ReceiverValue", "ReservedBytes", "Rewards", "TrailingByteCount",
            },
            notice);

        Assert.Equal(
            new[] { "AmplifyType", "AmplifyValue", "Data", "Durability", "ItemId", "Slot" },
            typeof(SettlementItemEntry).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[] { "HasItem", "ItemData", "ItemId", "Reserved", "Status", "Value" },
            typeof(SettlementRewardInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(typeof(IReadOnlyList<int>), typeof(SettlementNoticeB).GetProperty("ExpValues").PropertyType);
        Assert.Equal(typeof(byte), typeof(SettlementNoticeB).GetProperty("ExpKind").PropertyType);
        Assert.Equal(typeof(int), typeof(SettlementNoticeB).GetProperty("Percent").PropertyType);
        Assert.Equal(typeof(int), typeof(SettlementNoticeB).GetProperty("ReceiverValue").PropertyType);
        Assert.Equal(typeof(IReadOnlyList<byte>), typeof(SettlementNoticeB).GetProperty("ReservedBytes").PropertyType);
        Assert.Equal(typeof(byte), typeof(SettlementRewardInfo).GetProperty("Status").PropertyType);
        Assert.Equal(typeof(int), typeof(SettlementItemEntry).GetProperty("ItemId").PropertyType);
    }

    [Fact]
    public void Record_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        Type[] types = { typeof(SettlementNoticeB), typeof(SettlementItemEntry), typeof(SettlementRewardInfo) };
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

        Assert.Empty(typeof(SettlementNoticeB).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(SettlementItemEntry).IsValueType);
        Assert.True(typeof(SettlementRewardInfo).IsValueType);

        // 实例字段只许简单值与只读列表接口:不留载荷数组或可写集合给外部
        Type[] allowed =
        {
            typeof(byte), typeof(ushort), typeof(int), typeof(bool),
            typeof(IReadOnlyList<int>), typeof(IReadOnlyList<byte>), typeof(IReadOnlyList<SettlementItemEntry>), typeof(IReadOnlyList<SettlementRewardInfo>),
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
    public void Every_list_rejects_every_write_even_through_IList()
    {
        SettlementNoticeB n = ParseOk(LiteralPayload);

        AssertReadOnlyWrapper(n.ExpValues);
        AssertReadOnlyWrapper(n.QuestItems);
        AssertReadOnlyWrapper(n.Rewards);
        AssertReadOnlyWrapper(n.PremiumRewards);
        AssertReadOnlyWrapper(n.ReservedBytes);
        Assert.Equal(1000, n.ExpValues[0]);
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer_and_parsing_leaves_the_input_untouched()
    {
        byte[] payload = new BSpec { Items = SyntheticItems(3, variant: 9) }.Build(1, 2, 3);
        byte[] before = (byte[])payload.Clone();

        SettlementNoticeB first = ParseOk(payload);
        SettlementNoticeB second = ParseOk(payload);
        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.NotSame(first.QuestItems, second.QuestItems);
        Assert.NotSame(first.ExpValues, second.ExpValues);

        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        AssertMatches(new BSpec { Items = SyntheticItems(3, variant: 9) }, first);
        Assert.Equal(3, first.TrailingByteCount);
    }

    // ---- 真实解码路径 ----

    [Fact]
    public void A_decoded_frame_carries_the_documented_header_and_its_payload_parses_with_the_fold_hash_tail()
    {
        var spec = new BSpec { Items = SyntheticItems(2, variant: 4) };
        byte[] body = PacketIntegrity.AppendFoldHash(spec.Build());

        DecodedFrame frame = PktImplTestKit.Frame(Noti, RewardId, body);

        Assert.Equal(PacketClass.Noti, frame.Header.Class);
        Assert.Equal(RewardId, frame.Header.PacketId);
        Assert.Equal(SettlementNoticeBParser.PacketId, frame.Header.PacketId);
        SettlementNoticeB n = ParseOk(frame.Payload);
        AssertMatches(spec, n);
        Assert.Equal(4, n.TrailingByteCount);
    }

    // ---- 随机对拍:布局规则独立推出 ----

    /// <summary>独立的布局规则(显式游标,不复用产品代码):返回是否读得下,以及读完时的游标。</summary>
    private static bool IndependentLayout(byte[] b, out int consumed)
    {
        consumed = 0;
        int p = 65 + 1;                                   // 经验块 65 字节 + 物品个数
        if (b.Length < p)
        {
            return false;
        }
        p += 15 * b[65];
        if (p > b.Length)
        {
            return false;
        }
        for (int group = 0; group < 2; group++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                if (p >= b.Length)
                {
                    return false;
                }
                int status = b[p];
                p += 1;
                if (status > 2)
                {
                    return false;
                }
                p += status == 0 ? 0 : status == 1 ? 8 : 16;
                if (p > b.Length)
                {
                    return false;
                }
            }
            if (group == 0)
            {
                p += 4 + 4;                               // 接收者数组项 + 4 个保留字节
                if (p > b.Length)
                {
                    return false;
                }
            }
        }
        p += 4 + 3;                                       // 比例 + 三个标志
        if (p > b.Length)
        {
            return false;
        }
        consumed = p;
        return true;
    }

    [Fact]
    public void Random_payloads_never_throw_and_agree_with_the_independent_layout_rule()
    {
        uint seed = 0x00C0FFEE;
        int accepted = 0;
        int rejected = 0;
        for (int round = 0; round < 3000; round++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            uint r = seed >> 8;
            var spec = new BSpec
            {
                Items = SyntheticItems((int)(r % 5), variant: (int)(r % 13)),
                Rewards = Enumerable.Range(0, 4).Select(i => new RewardSpec((int)((r >> (2 * i)) % 3), i, 10 + i, 900 + i, 20 + i)).ToList(),
                Premium = Enumerable.Range(0, 4).Select(i => new RewardSpec((int)((r >> (9 + (2 * i))) % 3), i, 30 + i, 910 + i, 40 + i)).ToList(),
                ReceiverValue = (int)r,
                Percent = (int)(r >> 3),
            };
            byte[] payload = spec.Build();

            switch (round % 4)
            {
                case 1:
                    payload = payload.Take((int)((r >> 5) % (uint)payload.Length)).ToArray();           // 截断
                    break;
                case 2:
                    payload = payload.Concat(Enumerable.Range(0, (int)(r % 7)).Select(i => (byte)(r >> i))).ToArray();    // 加尾巴
                    break;
                case 3:
                    if (payload.Length > 70)
                    {
                        payload[70 + (int)((r >> 4) % (uint)(payload.Length - 70))] = (byte)(r >> 11);    // 改一个字节,可能打坏状态
                    }
                    break;
            }

            bool expectedOk = IndependentLayout(payload, out int consumed);

            SettlementNoticeB n = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = SettlementNoticeBParser.TryParse(payload, out n); });
            Assert.Null(ex);
            Assert.Equal(expectedOk, ok);
            if (!expectedOk)
            {
                Assert.Null(n);
                rejected++;
                continue;
            }

            accepted++;
            Assert.Equal(payload.Length - consumed, n.TrailingByteCount);
            Assert.Equal(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24), n.BaseExp);
            Assert.Equal(payload[16], n.ExpKind);
            Assert.Equal(payload[65], n.QuestItems.Count);
            Assert.Equal(payload[consumed - 7] | (payload[consumed - 6] << 8) | (payload[consumed - 5] << 16) | (payload[consumed - 4] << 24), n.Percent);
            Assert.Equal(payload[consumed - 3] != 0, n.IsReturnUserParty);
            Assert.Equal(payload[consumed - 2] != 0, n.IsEventCharacParty);
            Assert.Equal(payload[consumed - 1] != 0, n.HasDeadUserPenalty);
        }

        Assert.True(accepted > 500);
        Assert.True(rejected > 500);
    }
}
