using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 选角回包(类 1、包号 4)的明文载荷解析器(纯函数)。载荷全是手工拼的合成字节(手写字节数组,或 <see cref="SelectCharacterSpec"/> 的独立写入器),
/// u16/u32 字段取 900 以上的合成值(901、903、1000、1234、86400 等),u8 字段装不下 900,取互不相同的小值;宽度与符号的边界用例另用 0、255、65535、int.MinValue 等纯宽度值。
/// 不读任何真实帧文件,不含真实编号或数值。
/// 读序依据 E-382 / packetread_NOTES §3.3:<c>u8 flag</c>;flag 为 0 时 <c>u8 err</c>,失败体到此为止;成功体
/// <c>u32, u32, u16 × 4, u8 N, N × {u8, u32}, u32, 20 × {u16, u32}, 5 × u32, u8, u8, u8 b, b × u8, u16, u16, u8</c>,总长 = 1 + 169 + 5N + b。
/// 读完最后一个字段即停,尾巴(折叠哈希、加密补零)只报告个数。
/// 失败码:本地服务端会送达客户端的只有 0x13、4、0x9f、8;客户端另对 0x60 特判,本地服务端没有它的发送路径。
/// 与既有选角判定(<see cref="SlotJudge"/>)的衔接测试在 <c>SelectCharacterWiringTests</c>;这里只测解析器本身。
/// </summary>
public class SelectCharacterResultParserTests
{
    // ---- 助手 ----

    private static SelectCharacterResult ParseOk(byte[] payload)
    {
        Assert.True(SelectCharacterResultParser.TryParse(payload, out SelectCharacterResult result));
        Assert.NotNull(result);
        return result;
    }

    /// <summary>被拒:不抛异常、返回 false、result 为 null。</summary>
    private static void AssertRejected(byte[] payload)
    {
        SelectCharacterResult result = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = SelectCharacterResultParser.TryParse(payload, out result); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(result);
    }

    private static int Le16(byte[] p, int at)
    {
        return p[at] | (p[at + 1] << 8);
    }

    private static uint Le32(byte[] p, int at)
    {
        return (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24));
    }

    private static SelectCharacterSpec Sized(int variant, int premiums, int tutorial)
    {
        return SelectCharacterSpec.Variant(variant, premiums, tutorial);
    }

    // ---- 常量 ----

    [Fact]
    public void Production_constants_match_the_documented_values()
    {
        Assert.Equal(4, SelectCharacterResultParser.PacketId);
        Assert.Equal(20, SelectCharacterResultParser.QuestSlotCount);
        Assert.Equal(5, SelectCharacterResultParser.QuestNotifyValueCount);
        Assert.Equal(0xFFFF, SelectCharacterQuestSlot.EmptyKey);
    }

    // ---- 逐字段(手写字节;小端、字段顺序都由字面字节钉死,不经写入器) ----

    /// <summary>
    /// 手写的标准成功体,N = 2、b = 3,总长 1 + 169 + 10 + 3 = 183。每个多字节字段的各字节互不相同,核对小端与字段顺序;
    /// 20 个槽里第 3、7、8、19 个是空槽(键 0xFFFF),其中第 7 个和第 19 个的值非 0;其余键按 901 + 3 × j,值按 0x00010000 + j + 1。
    /// </summary>
    private static byte[] LiteralSuccess(params byte[] tail)
    {
        var b = new NoticePayloadBuilder();
        b.Raw(0x01);                                  // flag:非 0 即成功
        b.Raw(0x01, 0x02, 0x03, 0x04);                // u32 = 0x04030201
        b.Raw(0xF1, 0xE2, 0xD3, 0xC4);                // u32 = 0xC4D3E2F1(大于 2^31:按无符号读)
        b.Raw(0x85, 0x03);                            // u16 = 901
        b.Raw(0x87, 0x03);                            // u16 = 903
        b.Raw(0xE8, 0x03);                            // u16 = 1000
        b.Raw(0xD2, 0x04);                            // u16 = 1234
        b.Raw(0x02);                                  // N = 2
        b.Raw(0x21, 0x80, 0x51, 0x01, 0x00);          // 第 0 项:类型 0x21,0x00015180 = 86400
        b.Raw(0x0C, 0xF0, 0xF1, 0xFF, 0xFF);          // 第 1 项:类型 0x0C,0xFFFFF1F0 = -3600
        b.Raw(0x9C, 0xFF, 0xFF, 0xFF);                // 金额型:0xFFFFFF9C = -100
        for (int j = 0; j < 20; j++)                  // 20 个 {u16 键, u32 值}
        {
            bool empty = j == 3 || j == 7 || j == 8 || j == 19;
            int key = empty ? 0xFFFF : 901 + (3 * j);
            uint value = j == 7 ? 0xA1B2C3D4u : j == 19 ? 5u : empty ? 0u : 0x00010000u + (uint)j + 1;
            b.Raw((byte)key, (byte)(key >> 8));
            b.Raw((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
        }
        b.Raw(0xE9, 0x03, 0x00, 0x00);                // w0 = 1001
        b.Raw(0x00, 0x00, 0x00, 0x00);                // w1 = 0
        b.Raw(0xEB, 0x03, 0x00, 0x00);                // w2 = 1003
        b.Raw(0xFF, 0xFF, 0xFF, 0xFF);                // w3 = 0xFFFFFFFF
        b.Raw(0xED, 0x03, 0x00, 0x00);                // w4 = 1005
        b.Raw(0x37);                                  // u8 = 0x37
        b.Raw(0x02);                                  // u8 = 2
        b.Raw(0x03);                                  // b = 3
        b.Raw(0x04, 0x4E, 0x7F);                      // 3 个 u8
        b.Raw(0x91, 0x03);                            // u16 = 913
        b.Raw(0xA2, 0x03);                            // u16 = 930
        b.Raw(0x01);                                  // u8 = 1
        return b.Raw(tail).ToArray();
    }

    [Fact]
    public void A_literal_success_payload_is_read_field_by_field()
    {
        byte[] payload = LiteralSuccess();
        Assert.Equal(183, payload.Length);

        SelectCharacterResult r = ParseOk(payload);

        Assert.True(r.IsSuccess);
        Assert.Equal(1, r.Flag);
        Assert.Equal(SelectCharacterFailure.None, r.Failure);
        Assert.Equal(0, r.FailureCode);
        Assert.Equal(0, r.TrailingByteCount);

        SelectCharacterLoadInfo l = r.Load;
        Assert.NotNull(l);
        Assert.Equal(0x04030201u, l.RegTime);
        Assert.Equal(0xC4D3E2F1u, l.CreateTime);
        Assert.Equal(901, l.UniqueId);
        Assert.Equal(903, l.TotalFatigue);
        Assert.Equal(1000, l.TotalMaxFatigue);
        Assert.Equal(1234, l.UsedFatigue);

        Assert.Equal(2, l.Premiums.Count);
        Assert.Equal(0x21, l.Premiums[0].Id);
        Assert.Equal(86400, l.Premiums[0].RemainingSeconds);
        Assert.Equal(0x0C, l.Premiums[1].Id);
        Assert.Equal(-3600, l.Premiums[1].RemainingSeconds);

        Assert.Equal(-100, l.Cera);

        Assert.Equal(20, l.QuestSlots.Count);
        for (int j = 0; j < 20; j++)
        {
            bool empty = j == 3 || j == 7 || j == 8 || j == 19;
            Assert.Equal(empty, l.QuestSlots[j].IsEmpty);
            Assert.Equal(empty ? 0xFFFF : 901 + (3 * j), l.QuestSlots[j].Key);
            uint expected = j == 7 ? 0xA1B2C3D4u : j == 19 ? 5u : empty ? 0u : 0x00010000u + (uint)j + 1;
            Assert.Equal(expected, l.QuestSlots[j].Value);
        }

        Assert.Equal(new uint[] { 1001, 0, 1003, 0xFFFFFFFFu, 1005 }, l.QuestNotifyValues);
        Assert.Equal(0x37, l.Village);
        Assert.Equal(2, l.TutorialSkipable);
        Assert.Equal(new byte[] { 0x04, 0x4E, 0x7F }, l.TutorialFlagIndices);
        Assert.Equal(913, l.FatigueBatteryCharging);
        Assert.Equal(930, l.FatigueGrownUpBuff);
        Assert.Equal(1, l.TradePunishOverThirtyDays);
    }

    [Fact]
    public void Each_byte_of_the_literal_payload_belongs_to_exactly_one_field()
    {
        // 每个字节单独变:恰好一个字段变,别的字段不动(排除字段重叠与步长错位)。
        // 两个计数字节(偏移 17 的 N、偏移 174 的 b)例外:它们变了,后面的布局整体改变,这份 183 字节的载荷不够读,整包被拒。
        byte[] payload = LiteralSuccess();
        SelectCharacterLoadInfo baseline = ParseOk(payload).Load;
        for (int pos = 1; pos < payload.Length; pos++)
        {
            byte[] changed = (byte[])payload.Clone();
            changed[pos] ^= 0x5A;
            if (pos == 17 || pos == 174)
            {
                AssertRejected(changed);
                continue;
            }
            SelectCharacterResult r = ParseOk(changed);
            Assert.True(r.IsSuccess);
            Assert.Equal(1, FieldDifferences(baseline, r.Load));
        }
    }

    /// <summary>两份载入字段里取值不同的"字段个数"(列表按元素算,每个元素一个字段;槽的键与值各算一个)。</summary>
    private static int FieldDifferences(SelectCharacterLoadInfo a, SelectCharacterLoadInfo b)
    {
        int diff = 0;
        void Cmp<T>(T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y))
            {
                diff++;
            }
        }

        Cmp(a.RegTime, b.RegTime);
        Cmp(a.CreateTime, b.CreateTime);
        Cmp(a.UniqueId, b.UniqueId);
        Cmp(a.TotalFatigue, b.TotalFatigue);
        Cmp(a.TotalMaxFatigue, b.TotalMaxFatigue);
        Cmp(a.UsedFatigue, b.UsedFatigue);
        Assert.Equal(a.Premiums.Count, b.Premiums.Count);
        for (int i = 0; i < a.Premiums.Count; i++)
        {
            Cmp(a.Premiums[i].Id, b.Premiums[i].Id);
            Cmp(a.Premiums[i].RemainingSeconds, b.Premiums[i].RemainingSeconds);
        }
        Cmp(a.Cera, b.Cera);
        Assert.Equal(20, a.QuestSlots.Count);
        Assert.Equal(20, b.QuestSlots.Count);
        for (int j = 0; j < 20; j++)
        {
            Cmp(a.QuestSlots[j].Key, b.QuestSlots[j].Key);
            Cmp(a.QuestSlots[j].Value, b.QuestSlots[j].Value);
        }
        for (int k = 0; k < 5; k++)
        {
            Cmp(a.QuestNotifyValues[k], b.QuestNotifyValues[k]);
        }
        Cmp(a.Village, b.Village);
        Cmp(a.TutorialSkipable, b.TutorialSkipable);
        Assert.Equal(a.TutorialFlagIndices.Count, b.TutorialFlagIndices.Count);
        for (int m = 0; m < a.TutorialFlagIndices.Count; m++)
        {
            Cmp(a.TutorialFlagIndices[m], b.TutorialFlagIndices[m]);
        }
        Cmp(a.FatigueBatteryCharging, b.FatigueBatteryCharging);
        Cmp(a.FatigueGrownUpBuff, b.FatigueGrownUpBuff);
        Cmp(a.TradePunishOverThirtyDays, b.TradePunishOverThirtyDays);
        return diff;
    }

    // ---- 偏移表(E-382 / packetread_NOTES §3.3):每个字段都在表里的偏移上,变长计数不同也一样 ----

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 3)]
    [InlineData(3, 5, 0)]
    [InlineData(4, 7, 17)]
    public void Every_field_sits_at_its_documented_offset_for_any_variable_counts(int variant, int premiums, int tutorial)
    {
        SelectCharacterSpec spec = Sized(variant, premiums, tutorial);
        byte[] p = spec.Build();
        int n = premiums;
        int b = tutorial;
        Assert.Equal(170 + (5 * n) + b, p.Length);

        // 先用偏移表直接从字节里读出每个字段(与产品读取代码无关),再要求解析结果与之相同
        SelectCharacterResult r = ParseOk(p);
        SelectCharacterLoadInfo l = r.Load;

        Assert.Equal(p[0], r.Flag);                                     // flag @ 0
        Assert.Equal(Le32(p, 1), l.RegTime);                            // u32 @ 1
        Assert.Equal(Le32(p, 5), l.CreateTime);                         // u32 @ 5
        Assert.Equal(Le16(p, 9), l.UniqueId);                           // u16 @ 9
        Assert.Equal(Le16(p, 11), l.TotalFatigue);                      // u16 @ 11
        Assert.Equal(Le16(p, 13), l.TotalMaxFatigue);                   // u16 @ 13
        Assert.Equal(Le16(p, 15), l.UsedFatigue);                       // u16 @ 15
        Assert.Equal(n, p[17]);                                         // N @ 17
        Assert.Equal(n, l.Premiums.Count);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(p[18 + (5 * i)], l.Premiums[i].Id);                       // u8 @ 18 + 5i
            Assert.Equal((int)Le32(p, 19 + (5 * i)), l.Premiums[i].RemainingSeconds);   // u32 @ 19 + 5i
        }
        Assert.Equal((int)Le32(p, 18 + (5 * n)), l.Cera);               // u32 @ 18 + 5N
        for (int j = 0; j < 20; j++)
        {
            Assert.Equal(Le16(p, 22 + (5 * n) + (6 * j)), l.QuestSlots[j].Key);        // u16 @ 22 + 5N + 6j
            Assert.Equal(Le32(p, 24 + (5 * n) + (6 * j)), l.QuestSlots[j].Value);      // u32 @ 24 + 5N + 6j
        }
        for (int k = 0; k < 5; k++)
        {
            Assert.Equal(Le32(p, 142 + (5 * n) + (4 * k)), l.QuestNotifyValues[k]);    // u32 @ 142 + 5N + 4k
        }
        Assert.Equal(p[162 + (5 * n)], l.Village);                      // u8 @ 162 + 5N
        Assert.Equal(p[163 + (5 * n)], l.TutorialSkipable);             // u8 @ 163 + 5N
        Assert.Equal(b, p[164 + (5 * n)]);                              // b @ 164 + 5N
        Assert.Equal(b, l.TutorialFlagIndices.Count);
        for (int m = 0; m < b; m++)
        {
            Assert.Equal(p[165 + (5 * n) + m], l.TutorialFlagIndices[m]);            // u8 @ 165 + 5N + m
        }
        Assert.Equal(Le16(p, 165 + (5 * n) + b), l.FatigueBatteryCharging);          // u16 @ 165 + 5N + b
        Assert.Equal(Le16(p, 167 + (5 * n) + b), l.FatigueGrownUpBuff);              // u16 @ 167 + 5N + b
        Assert.Equal(p[169 + (5 * n) + b], l.TradePunishOverThirtyDays);             // u8 @ 169 + 5N + b
        Assert.Equal(0, r.TrailingByteCount);
        Assert.Equal(170 + (5 * n) + b, p.Length);                      // 总长
    }

    // ---- 每个字段变着取值 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(37)]
    [InlineData(121)]
    [InlineData(255)]
    [InlineData(998)]
    public void Every_field_takes_varied_values_and_comes_back_unchanged(int variant)
    {
        SelectCharacterSpec spec = Sized(variant, premiums: (variant % 5) + 1, tutorial: (variant * 3) % 9);

        SelectCharacterResult r = ParseOk(spec.Build());

        Assert.True(r.IsSuccess);
        Assert.Equal(SelectCharacterFailure.None, r.Failure);
        Assert.Equal(0, r.TrailingByteCount);
        AssertLoad(spec, r.Load);
    }

    [Fact]
    public void Variants_really_differ_field_by_field()
    {
        // 防止"每个字段变着取值"名存实亡:相邻两个变体的每一类字段都至少有一处取值不同
        SelectCharacterLoadInfo a = ParseOk(Sized(1, 3, 4).Build()).Load;
        SelectCharacterLoadInfo b = ParseOk(Sized(2, 3, 4).Build()).Load;

        Assert.NotEqual(a.RegTime, b.RegTime);
        Assert.NotEqual(a.CreateTime, b.CreateTime);
        Assert.NotEqual(a.UniqueId, b.UniqueId);
        Assert.NotEqual(a.TotalFatigue, b.TotalFatigue);
        Assert.NotEqual(a.TotalMaxFatigue, b.TotalMaxFatigue);
        Assert.NotEqual(a.UsedFatigue, b.UsedFatigue);
        Assert.NotEqual(a.Premiums[0].Id, b.Premiums[0].Id);
        Assert.NotEqual(a.Premiums[0].RemainingSeconds, b.Premiums[0].RemainingSeconds);
        Assert.NotEqual(a.Cera, b.Cera);
        Assert.NotEqual(a.QuestSlots.Select(s => s.Key).ToArray(), b.QuestSlots.Select(s => s.Key).ToArray());
        Assert.NotEqual(a.QuestSlots.Select(s => s.Value).ToArray(), b.QuestSlots.Select(s => s.Value).ToArray());
        Assert.NotEqual(a.QuestNotifyValues.ToArray(), b.QuestNotifyValues.ToArray());
        Assert.NotEqual(a.Village, b.Village);
        Assert.NotEqual(a.TutorialSkipable, b.TutorialSkipable);
        Assert.NotEqual(a.TutorialFlagIndices.ToArray(), b.TutorialFlagIndices.ToArray());
        Assert.NotEqual(a.FatigueBatteryCharging, b.FatigueBatteryCharging);
        Assert.NotEqual(a.FatigueGrownUpBuff, b.FatigueGrownUpBuff);
        Assert.NotEqual(a.TradePunishOverThirtyDays, b.TradePunishOverThirtyDays);
    }

    // ---- 变长计数的边界:条目为 0、个数的上限,以及教程下标个数 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 78)]       // b = 0x4e:客户端读的上限
    [InlineData(2, 79)]       // b = 0x4f:超过客户端上限一个
    [InlineData(5, 128)]      // 服务端最多写 0..0x7f 共 128 个
    [InlineData(0, 255)]
    [InlineData(255, 0)]
    [InlineData(255, 255)]
    public void Variable_counts_at_the_boundaries_are_read_in_full_and_exactly(int premiums, int tutorial)
    {
        SelectCharacterSpec spec = Sized(premiums + tutorial, premiums, tutorial);
        byte[] payload = spec.Build();
        Assert.Equal(170 + (5 * premiums) + tutorial, payload.Length);
        Assert.Equal(spec.BodyLength, payload.Length);

        SelectCharacterResult r = ParseOk(payload);

        AssertLoad(spec, r.Load);
        Assert.Equal(premiums, r.Load.Premiums.Count);
        Assert.Equal(tutorial, r.Load.TutorialFlagIndices.Count);
        Assert.Equal(0, r.TrailingByteCount);
    }

    [Fact]
    public void Zero_entries_give_empty_read_only_lists_and_never_null()
    {
        SelectCharacterSpec spec = Sized(3, 0, 0);

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.NotNull(l.Premiums);
        Assert.NotNull(l.TutorialFlagIndices);
        Assert.Empty(l.Premiums);
        Assert.Empty(l.TutorialFlagIndices);
        AssertListIsReadOnly(l.Premiums);
        AssertListIsReadOnly(l.TutorialFlagIndices);
        Assert.Equal(20, l.QuestSlots.Count);        // 固定长度的两张表不受变长计数影响
        Assert.Equal(5, l.QuestNotifyValues.Count);
    }

    [Fact]
    public void A_tutorial_count_above_the_client_limit_is_read_in_full_and_later_fields_stay_aligned()
    {
        // 原版客户端只读 min(b, 0x4e) 个,b 更大时它自己的后续字段会错位;解析器按服务端语义读满 b 个,后面的字段不错位
        SelectCharacterSpec spec = Sized(7, 2, 0x4f + 30);
        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.Equal(0x4f + 30, l.TutorialFlagIndices.Count);
        AssertLoad(spec, l);
        Assert.Equal((ushort)spec.FatigueBatteryCharging, l.FatigueBatteryCharging);
        Assert.Equal((ushort)spec.FatigueGrownUpBuff, l.FatigueGrownUpBuff);
        Assert.Equal((byte)spec.TradePunishOverThirtyDays, l.TradePunishOverThirtyDays);
    }

    // ---- 20 槽任务表与 5 个提醒值 ----

    [Fact]
    public void The_quest_table_is_always_twenty_slots_and_empty_slots_keep_their_values()
    {
        // 20 个槽全是空槽键,但值各不相同且非 0:空槽的值照读,不跳过、不清零
        SelectCharacterSpec spec = Sized(4, 1, 1);
        for (int j = 0; j < 20; j++)
        {
            spec.QuestSlots[j] = (0xFFFF, 905 + (31 * j));
        }

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.Equal(20, l.QuestSlots.Count);
        for (int j = 0; j < 20; j++)
        {
            Assert.True(l.QuestSlots[j].IsEmpty);
            Assert.Equal(0xFFFF, l.QuestSlots[j].Key);
            Assert.Equal((uint)(905 + (31 * j)), l.QuestSlots[j].Value);
        }
        AssertLoad(spec, l);       // 空槽之后的字段没有被吃掉
    }

    [Fact]
    public void Only_the_key_ffff_marks_an_empty_slot()
    {
        SelectCharacterSpec spec = Sized(5, 0, 0);
        spec.QuestSlots[0] = (0, 0);
        spec.QuestSlots[1] = (0xFFFE, 7);
        spec.QuestSlots[2] = (0xFFFF, 0);
        spec.QuestSlots[3] = (1, 0xFFFFFFFFL);
        spec.QuestSlots[4] = (0x8000, 0x80000000L);

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.False(l.QuestSlots[0].IsEmpty);       // 键 0 不是空槽
        Assert.False(l.QuestSlots[1].IsEmpty);
        Assert.True(l.QuestSlots[2].IsEmpty);
        Assert.False(l.QuestSlots[3].IsEmpty);
        Assert.False(l.QuestSlots[4].IsEmpty);
        Assert.Equal(0xFFFFFFFFu, l.QuestSlots[3].Value);      // 值按 u32 原样保存,不拆那三个 9 位计数
        Assert.Equal(0x80000000u, l.QuestSlots[4].Value);
        AssertListIsReadOnly(l.QuestSlots);
    }

    [Fact]
    public void Notify_values_are_five_raw_u32_kept_even_when_zero_or_duplicated()
    {
        SelectCharacterSpec spec = Sized(6, 1, 1);
        spec.QuestNotifyValues = new long[] { 0, 0, 0xFFFFFFFFL, 905, 905 };

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.Equal(new uint[] { 0, 0, 0xFFFFFFFFu, 905, 905 }, l.QuestNotifyValues);
        AssertListIsReadOnly(l.QuestNotifyValues);
    }

    // ---- 符号与宽度 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-90013)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void The_money_like_field_is_given_signed_and_never_clamped(int cera)
    {
        SelectCharacterSpec spec = Sized(8, 1, 1);
        spec.Cera = cera;

        Assert.Equal(cera, ParseOk(spec.Build()).Load.Cera);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(86400)]
    [InlineData(-86400)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Remaining_seconds_are_given_signed_with_no_expiry_decision(int remaining)
    {
        SelectCharacterSpec spec = Sized(9, 3, 1);
        spec.Premiums[1] = (0x2A, remaining);

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.Equal(remaining, l.Premiums[1].RemainingSeconds);
        Assert.Equal(0x2A, l.Premiums[1].Id);
        AssertLoad(spec, l);
    }

    [Fact]
    public void The_premium_entry_has_no_expired_flag_because_the_boundary_is_not_defined_by_the_notes()
    {
        // 剩余秒为 0 或负数算不算到期,读码没有给出依据,所以条目里只有读序里的两个值,没有 IsExpired 一类成员
        string[] names = typeof(SelectCharacterPremium).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Id", "RemainingSeconds" }, names);
        Assert.DoesNotContain(
            typeof(SelectCharacterPremium).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            m => !m.IsSpecialName);
    }

    [Fact]
    public void Wide_values_keep_their_full_width_and_unsigned_reading()
    {
        SelectCharacterSpec spec = Sized(10, 1, 1);
        spec.RegTime = 0xFFFFFFFFL;
        spec.CreateTime = 0x80000000L;
        spec.UniqueId = 0xFFFF;
        spec.TotalFatigue = 0xFFFF;
        spec.TotalMaxFatigue = 0x8000;
        spec.UsedFatigue = 0;
        spec.Village = 0xFF;
        spec.TutorialSkipable = 0xFF;
        spec.FatigueBatteryCharging = 0xFFFF;
        spec.FatigueGrownUpBuff = 0x8000;
        spec.TradePunishOverThirtyDays = 0xFF;

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.Equal(0xFFFFFFFFu, l.RegTime);
        Assert.Equal(0x80000000u, l.CreateTime);
        Assert.Equal((ushort)0xFFFF, l.UniqueId);
        Assert.Equal((ushort)0x8000, l.TotalMaxFatigue);
        Assert.Equal((byte)0xFF, l.Village);
        Assert.Equal((byte)0xFF, l.TutorialSkipable);
        Assert.Equal((byte)0xFF, l.TradePunishOverThirtyDays);
        AssertLoad(spec, l);
    }

    // ---- 最短成功体与截断 ----

    [Fact]
    public void The_shortest_success_body_is_170_bytes_and_one_byte_less_is_rejected()
    {
        // N = 0、b = 0:1 + 169 = 170 字节;槽的键为 0 不是空槽
        byte[] shortest = new byte[170];
        shortest[0] = 1;

        SelectCharacterResult r = ParseOk(shortest);

        Assert.True(r.IsSuccess);
        Assert.Empty(r.Load.Premiums);
        Assert.Empty(r.Load.TutorialFlagIndices);
        Assert.Equal(20, r.Load.QuestSlots.Count);
        Assert.All(r.Load.QuestSlots, s => Assert.False(s.IsEmpty));
        Assert.Equal(0, r.TrailingByteCount);

        AssertRejected(shortest.Take(169).ToArray());
    }

    [Fact]
    public void Every_proper_prefix_of_a_success_body_is_rejected()
    {
        byte[] payload = LiteralSuccess();
        for (int len = 0; len < payload.Length; len++)
        {
            AssertRejected(payload.Take(len).ToArray());
        }
        Assert.True(SelectCharacterResultParser.TryParse(payload, out _));
    }

    [Fact]
    public void Every_proper_prefix_is_rejected_for_the_largest_counts_too()
    {
        byte[] payload = Sized(12, 255, 255).Build();
        Assert.Equal(170 + (5 * 255) + 255, payload.Length);
        for (int len = 0; len < payload.Length; len++)
        {
            AssertRejected(payload.Take(len).ToArray());
        }
        Assert.True(SelectCharacterResultParser.TryParse(payload, out _));
    }

    [Fact]
    public void A_count_one_above_the_entries_actually_written_is_rejected_when_no_tail_follows()
    {
        // N 声明比实际多 1:客户端式读法要多读 5 字节,载荷不够就整包拒收
        byte[] payload = Sized(13, 2, 3).Build();
        payload[17] = 3;
        AssertRejected(payload);

        // b 声明比实际多 1:多读 1 字节,同样不够
        byte[] payload2 = Sized(14, 2, 3).Build();
        payload2[164 + (5 * 2)] = 4;
        AssertRejected(payload2);
    }

    [Fact]
    public void A_count_below_the_entries_actually_written_follows_the_layout_rule_and_never_throws()
    {
        // N 声明比实际少 1:后面的字段整体前移 5 字节,读到的是别的字节,"解析成功"与否取决于错位后读到的 b;
        // 不论哪种,都按偏移表的长度规则(独立推出)判定,不抛异常
        byte[] payload = Sized(15, 2, 3).Build();
        payload[17] = 1;
        int consumed = ExpectedConsumed(payload);

        SelectCharacterResult r = null;
        bool ok = false;
        Exception ex = Record.Exception(() => { ok = SelectCharacterResultParser.TryParse(payload, out r); });

        Assert.Null(ex);
        Assert.Equal(consumed >= 0, ok);
        if (ok)
        {
            Assert.Equal(payload.Length - consumed, r.TrailingByteCount);
            Assert.Single(r.Load.Premiums);
        }
        else
        {
            Assert.Null(r);
        }
    }

    [Fact]
    public void A_count_below_the_entries_actually_written_reads_misaligned_values_when_the_layout_still_fits()
    {
        // 把错位后落在 b 位置的那个字节(偏移 169)改成 0,让错位后的布局恰好读得下来:N = 1、b = 0,总长 175,尾巴 8 字节;
        // 说明"解析成功"只表示布局读得下来,不保证字段对得上(与客户端读法同样会错位的那类坏包,调用方靠计数声明自己保证)
        byte[] payload = Sized(15, 2, 3).Build();
        payload[17] = 1;
        payload[169] = 0;
        Assert.Equal(175, ExpectedConsumed(payload));

        SelectCharacterResult r = ParseOk(payload);

        Assert.Single(r.Load.Premiums);
        Assert.Empty(r.Load.TutorialFlagIndices);
        Assert.Equal(8, r.TrailingByteCount);
    }

    [Fact]
    public void Null_empty_and_success_looking_short_payloads_are_rejected()
    {
        AssertRejected(null);
        AssertRejected(new byte[0]);
        AssertRejected(new byte[] { 0x01 });
        AssertRejected(new byte[] { 0x01, 0x09, 0x09 });
        AssertRejected(new byte[] { 0x01, 0x00 });
        AssertRejected(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00 });
        AssertRejected(new byte[] { 0x00 });                 // 失败体缺失败码
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
            tail[i] = (byte)(0xC0 + (i * 11));
        }
        SelectCharacterSpec spec = Sized(16 + tailLength, 2, 4);

        SelectCharacterResult r = ParseOk(spec.Build(tail));

        AssertLoad(spec, r.Load);
        Assert.Equal(tailLength, r.TrailingByteCount);
    }

    [Fact]
    public void A_proper_prefix_that_cuts_only_into_the_tail_is_still_a_success_with_a_shorter_tail()
    {
        SelectCharacterSpec spec = Sized(17, 2, 3);
        byte[] withTail = spec.Build(0xA1, 0xA2, 0xA3, 0xA4);
        for (int cut = 0; cut <= 4; cut++)
        {
            SelectCharacterResult r = ParseOk(withTail.Take(spec.BodyLength + cut).ToArray());
            AssertLoad(spec, r.Load);
            Assert.Equal(cut, r.TrailingByteCount);
        }
    }

    [Fact]
    public void A_server_style_fold_hash_tail_leaves_exactly_four_bytes_and_they_are_the_fold_hash()
    {
        // C05 的样本结论:(1,4) 读序走完恰剩 4 字节,且等于对前面全部字节的折叠哈希。用服务端同一算法给合成载荷追加尾巴后做同样的核对。
        foreach (int variant in new[] { 20, 21, 22 })
        {
            SelectCharacterSpec spec = Sized(variant, variant % 4, variant % 7);
            byte[] body = spec.Build();
            Assert.True(PacketIntegrity.HasFoldHash(body.Length));     // 成功体不超过 0x190 时才带尾
            byte[] onWire = PacketIntegrity.AppendFoldHash(body);

            SelectCharacterResult r = ParseOk(onWire);

            AssertLoad(spec, r.Load);
            Assert.Equal(4, r.TrailingByteCount);
            Assert.Equal(FoldHashStatus.Match, new PacketReader(onWire, onWire.Length - r.TrailingByteCount, onWire.Length).CheckFoldHash(0));
        }
    }

    [Fact]
    public void A_body_above_the_fold_hash_limit_carries_no_tail_and_is_consumed_exactly()
    {
        SelectCharacterSpec spec = Sized(23, 60, 20);         // 170 + 300 + 20 = 490 > 0x190
        byte[] body = spec.Build();
        Assert.True(body.Length > PacketIntegrity.FoldHashMaxPayload);
        Assert.Equal(body, PacketIntegrity.AppendFoldHash(body));

        SelectCharacterResult r = ParseOk(body);

        AssertLoad(spec, r.Load);
        Assert.Equal(0, r.TrailingByteCount);
    }

    [Fact]
    public void Encryption_zero_padding_after_the_fold_hash_is_just_more_trailing_bytes()
    {
        SelectCharacterSpec spec = Sized(24, 2, 3);
        byte[] onWire = PacketIntegrity.AppendFoldHash(spec.Build()).Concat(new byte[7]).ToArray();

        SelectCharacterResult r = ParseOk(onWire);

        AssertLoad(spec, r.Load);
        Assert.Equal(11, r.TrailingByteCount);
    }

    // ---- 成功标志 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void Any_nonzero_flag_is_success_with_the_raw_byte_kept(int flag)
    {
        SelectCharacterSpec spec = Sized(25, 1, 2);
        spec.Flag = flag;

        SelectCharacterResult r = ParseOk(spec.Build());

        Assert.True(r.IsSuccess);
        Assert.Equal(flag, r.Flag);
        Assert.Equal(SelectCharacterFailure.None, r.Failure);
        Assert.Equal(0, r.FailureCode);
        AssertLoad(spec, r.Load);
    }

    // ---- 失败体与失败码映射 ----

    /// <summary>独立写出的映射表:本地服务端会送达的 {0x13, 4, 0x9f, 8},加客户端特判的 0x60;其余都是 Unknown。</summary>
    private static SelectCharacterFailure ExpectedFailure(int code)
    {
        switch (code)
        {
            case 0x13:
                return SelectCharacterFailure.CreationLocked;
            case 4:
                return SelectCharacterFailure.SlotOutOfRange;
            case 0x9f:
                return SelectCharacterFailure.NameHasSlang;
            case 8:
                return SelectCharacterFailure.NamePrefixForbidden;
            case 0x60:
                return SelectCharacterFailure.ClientSpecial;
            default:
                return SelectCharacterFailure.Unknown;
        }
    }

    [Fact]
    public void Every_failure_code_is_read_and_mapped()
    {
        for (int code = 0; code < 256; code++)
        {
            SelectCharacterResult r = ParseOk(SelectCharacterSpec.Failure(code));

            Assert.False(r.IsSuccess);
            Assert.Equal(0, r.Flag);
            Assert.Equal((byte)code, r.FailureCode);
            Assert.Equal(ExpectedFailure(code), r.Failure);
            Assert.Null(r.Load);
            Assert.Equal(0, r.TrailingByteCount);
            Assert.Equal(ExpectedFailure(code), SelectCharacterResultParser.MapFailureCode((byte)code));
        }
    }

    [Fact]
    public void The_four_codes_the_local_server_can_deliver_map_to_named_failures_and_nothing_else_does()
    {
        // C10:送达客户端的失败 err 只有 {0x13, 4, 0x9f, 8}
        SelectCharacterFailure[] named =
        {
            SelectCharacterFailure.CreationLocked,
            SelectCharacterFailure.SlotOutOfRange,
            SelectCharacterFailure.NameHasSlang,
            SelectCharacterFailure.NamePrefixForbidden,
        };
        var mapped = new List<int>();
        for (int code = 0; code < 256; code++)
        {
            if (named.Contains(SelectCharacterResultParser.MapFailureCode((byte)code)))
            {
                mapped.Add(code);
            }
        }
        Assert.Equal(new[] { 4, 8, 0x13, 0x9f }, mapped);

        // 各自映射到各自的成员(不互相混用)
        Assert.Equal(SelectCharacterFailure.CreationLocked, SelectCharacterResultParser.MapFailureCode(0x13));
        Assert.Equal(SelectCharacterFailure.SlotOutOfRange, SelectCharacterResultParser.MapFailureCode(4));
        Assert.Equal(SelectCharacterFailure.NameHasSlang, SelectCharacterResultParser.MapFailureCode(0x9f));
        Assert.Equal(SelectCharacterFailure.NamePrefixForbidden, SelectCharacterResultParser.MapFailureCode(8));
    }

    [Fact]
    public void The_client_special_code_60_is_its_own_member_and_not_one_of_the_deliverable_four()
    {
        // C11:客户端特判 0x60,本地服务端没有发送路径;映射里单列,不并入那四个,也不当 Unknown
        Assert.Equal(SelectCharacterFailure.ClientSpecial, SelectCharacterResultParser.MapFailureCode(0x60));
        Assert.NotEqual(SelectCharacterFailure.Unknown, SelectCharacterResultParser.MapFailureCode(0x60));

        SelectCharacterResult r = ParseOk(SelectCharacterSpec.Failure(0x60));
        Assert.Equal(SelectCharacterFailure.ClientSpecial, r.Failure);
        Assert.Equal(0x60, r.FailureCode);
    }

    [Fact]
    public void The_failure_enum_has_exactly_the_documented_members()
    {
        string[] names = Enum.GetNames(typeof(SelectCharacterFailure)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "ClientSpecial", "CreationLocked", "NameHasSlang", "NamePrefixForbidden", "None", "SlotOutOfRange", "Unknown" },
            names);
        Assert.Equal(0, (int)SelectCharacterFailure.None);
    }

    [Fact]
    public void A_failure_body_is_never_read_further_than_its_two_bytes()
    {
        // 失败体后面哪怕跟着一份合法的成功体,也只读 flag 与 err:不偷看,尾巴全部算尾巴
        byte[] success = Sized(26, 2, 3).Build();
        byte[] payload = new byte[] { 0x00, 0x08 }.Concat(success).ToArray();

        SelectCharacterResult r = ParseOk(payload);

        Assert.False(r.IsSuccess);
        Assert.Equal(SelectCharacterFailure.NamePrefixForbidden, r.Failure);
        Assert.Null(r.Load);
        Assert.Equal(success.Length, r.TrailingByteCount);
    }

    [Fact]
    public void A_failure_body_with_a_fold_hash_tail_has_four_trailing_bytes()
    {
        byte[] onWire = PacketIntegrity.AppendFoldHash(SelectCharacterSpec.Failure(0x9f));
        Assert.Equal(6, onWire.Length);

        SelectCharacterResult r = ParseOk(onWire);

        Assert.Equal(SelectCharacterFailure.NameHasSlang, r.Failure);
        Assert.Equal(4, r.TrailingByteCount);
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void Record_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        Type[] types =
        {
            typeof(SelectCharacterResult),
            typeof(SelectCharacterLoadInfo),
            typeof(SelectCharacterPremium),
            typeof(SelectCharacterQuestSlot),
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
                Assert.False(field.IsPublic);
            }
        }

        Assert.Empty(typeof(SelectCharacterResult).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(SelectCharacterLoadInfo).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(SelectCharacterPremium).IsValueType);
        Assert.True(typeof(SelectCharacterQuestSlot).IsValueType);

        // 实例字段只许简单值、枚举、记录类自己与只读列表接口:不留载荷数组或可写集合的引用
        Type[] allowed =
        {
            typeof(byte),
            typeof(ushort),
            typeof(uint),
            typeof(int),
            typeof(SelectCharacterFailure),
            typeof(SelectCharacterLoadInfo),
            typeof(IReadOnlyList<SelectCharacterPremium>),
            typeof(IReadOnlyList<SelectCharacterQuestSlot>),
            typeof(IReadOnlyList<uint>),
            typeof(IReadOnlyList<byte>),
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
    public void The_four_lists_reject_every_write_even_through_IList()
    {
        SelectCharacterSpec spec = Sized(27, 3, 4);
        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        AssertListIsReadOnly(l.Premiums);
        AssertListIsReadOnly(l.QuestSlots);
        AssertListIsReadOnly(l.QuestNotifyValues);
        AssertListIsReadOnly(l.TutorialFlagIndices);

        // 写都失败了,内容没变
        AssertLoad(spec, l);
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer()
    {
        SelectCharacterSpec spec = Sized(28, 3, 4);
        byte[] payload = spec.Build(1, 2, 3);
        SelectCharacterResult r = ParseOk(payload);

        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        AssertLoad(spec, r.Load);
        Assert.Equal(3, r.TrailingByteCount);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_is_repeatable()
    {
        SelectCharacterSpec spec = Sized(29, 3, 4);
        byte[] payload = spec.Build(7, 8);
        byte[] before = (byte[])payload.Clone();

        SelectCharacterResult first = ParseOk(payload);
        SelectCharacterResult second = ParseOk(payload);

        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Load, second.Load);
        Assert.NotSame(first.Load.QuestSlots, second.Load.QuestSlots);
        AssertLoad(spec, first.Load);
        AssertLoad(spec, second.Load);
    }

    // ---- 随机对拍:长度规则独立推出,字段与构造值对照 ----

    /// <summary>
    /// 按偏移表独立推出一份载荷应读掉多少字节(读不下返回 -1);不复用产品里的读取代码。
    /// 失败体 2 字节;成功体:N 在偏移 17,b 在偏移 164 + 5N,总长 170 + 5N + b。
    /// </summary>
    private static int ExpectedConsumed(byte[] p)
    {
        if (p.Length < 1)
        {
            return -1;
        }
        if (p[0] == 0)
        {
            return p.Length >= 2 ? 2 : -1;
        }
        if (p.Length <= 17)                 // 读不到 N
        {
            return -1;
        }
        int n = p[17];
        int bAt = 164 + (5 * n);
        if (p.Length <= bAt)                // 读不到 b
        {
            return -1;
        }
        int total = 170 + (5 * n) + p[bAt];
        return p.Length >= total ? total : -1;
    }

    [Fact]
    public void Random_specs_roundtrip_and_random_truncations_are_rejected()
    {
        uint seed = 0x5EED0014;
        uint Next()
        {
            seed = (seed * 1664525u) + 1013904223u;
            return seed;
        }

        for (int round = 0; round < 1500; round++)
        {
            int premiums = (int)(Next() >> 24) % 7;
            int tutorial = (int)(Next() >> 24) % 21;
            var spec = new SelectCharacterSpec
            {
                Flag = 1 + ((int)(Next() >> 24) % 255),
                RegTime = Next(),
                CreateTime = Next(),
                UniqueId = (int)(Next() >> 16),
                TotalFatigue = (int)(Next() >> 16),
                TotalMaxFatigue = (int)(Next() >> 16),
                UsedFatigue = (int)(Next() >> 16),
                Cera = (int)Next(),
                Village = (int)(Next() >> 24),
                TutorialSkipable = (int)(Next() >> 24),
                FatigueBatteryCharging = (int)(Next() >> 16),
                FatigueGrownUpBuff = (int)(Next() >> 16),
                TradePunishOverThirtyDays = (int)(Next() >> 24),
            };
            spec.Premiums = new (int, int)[premiums];
            for (int i = 0; i < premiums; i++)
            {
                spec.Premiums[i] = ((int)(Next() >> 24), (int)Next());
            }
            for (int j = 0; j < 20; j++)
            {
                spec.QuestSlots[j] = ((int)(Next() >> 16), Next());
            }
            for (int k = 0; k < 5; k++)
            {
                spec.QuestNotifyValues[k] = Next();
            }
            spec.TutorialFlagIndices = new int[tutorial];
            for (int m = 0; m < tutorial; m++)
            {
                spec.TutorialFlagIndices[m] = (int)(Next() >> 24);
            }

            int tailLength = (round % 4 == 0) ? 0 : (round % 4 == 1) ? 4 : (int)(Next() >> 24) % 9;
            byte[] tail = new byte[tailLength];
            for (int i = 0; i < tail.Length; i++)
            {
                tail[i] = (byte)(Next() >> 24);
            }

            byte[] payload = spec.Build(tail);
            Assert.Equal(spec.BodyLength + tailLength, payload.Length);
            Assert.Equal(spec.BodyLength, ExpectedConsumed(payload));

            SelectCharacterResult r = ParseOk(payload);
            Assert.Equal(spec.Flag, r.Flag);
            AssertLoad(spec, r.Load);
            Assert.Equal(tailLength, r.TrailingByteCount);

            // 随机截断:落在成功体里面一律拒收
            int cut = (int)(Next() % (uint)spec.BodyLength);
            AssertRejected(payload.Take(cut).ToArray());
        }
    }

    [Fact]
    public void Random_bytes_never_throw_and_agree_with_the_independent_length_rule()
    {
        uint seed = 0x00BADA55;
        int accepted = 0;
        int acceptedSuccess = 0;
        for (int round = 0; round < 6000; round++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            int len = (int)((seed >> 16) % 320);
            byte[] body = new byte[len];
            for (int i = 0; i < len; i++)
            {
                seed = (seed * 1664525u) + 1013904223u;
                body[i] = (byte)(seed >> 24);
            }
            if (round % 3 != 0 && len > 17)
            {
                body[0] = 1;                                  // 三分之二的样本做成成功体形状,并把 N、b 压小,让成功分支有足够样本
                body[17] = (byte)(body[17] % 4);
                int bAt = 164 + (5 * body[17]);
                if (bAt < len)
                {
                    body[bAt] = (byte)(body[bAt] % 8);
                }
            }

            int consumed = ExpectedConsumed(body);
            SelectCharacterResult r = null;
            bool ok = false;
            Exception ex = Record.Exception(() => { ok = SelectCharacterResultParser.TryParse(body, out r); });
            Assert.Null(ex);
            Assert.Equal(consumed >= 0, ok);
            if (consumed < 0)
            {
                Assert.Null(r);
                continue;
            }

            accepted++;
            Assert.NotNull(r);
            Assert.Equal(len - consumed, r.TrailingByteCount);
            Assert.Equal(body[0], r.Flag);
            if (body[0] == 0)
            {
                Assert.Null(r.Load);
                Assert.Equal(body[1], r.FailureCode);
                Assert.Equal(ExpectedFailure(body[1]), r.Failure);
            }
            else
            {
                acceptedSuccess++;
                Assert.NotNull(r.Load);
                Assert.Equal(Le32(body, 1), r.Load.RegTime);
                Assert.Equal(body[17], r.Load.Premiums.Count);
                Assert.Equal(body[164 + (5 * body[17])], r.Load.TutorialFlagIndices.Count);
            }
        }

        Assert.True(accepted > 500);
        Assert.True(acceptedSuccess > 300);
    }

    // ---- 与 packetread 自验收清单(C01 到 C14)的对读 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 78)]
    [InlineData(4, 10)]
    public void C05_the_sample_length_formula_holds_with_the_four_byte_tail(int premiums, int tutorial)
    {
        // C05:(1,4) = 1 + 169 + 5N + min(b, 0x4e) + 4;b 不超过 0x4e 时与解析器读掉的长度加 4 字节尾巴一致
        SelectCharacterSpec spec = Sized(30 + premiums, premiums, tutorial);
        byte[] onWire = PacketIntegrity.AppendFoldHash(spec.Build());
        Assert.Equal(1 + 169 + (5 * premiums) + Math.Min(tutorial, 0x4e) + 4, onWire.Length);

        SelectCharacterResult r = ParseOk(onWire);

        Assert.Equal(4, r.TrailingByteCount);
        AssertLoad(spec, r.Load);
    }

    [Fact]
    public void C05_the_sample_structure_facts_hold_for_a_synthetic_body()
    {
        // 样本结构事实(只看布尔):20 槽表里空槽是后缀、空槽键 0xFFFF 时值为 0、非空键互异;教程下标严格升序;b 不超过 0x4e;
        // B2 不大于 B3,B4 不大于 B3。这些是服务端写序的性质,不是解析的合法性条件:解析器对违反它们的载荷同样照收(见别的用例);
        // 这里只确认按这些性质造出的合成载荷被原样读出,性质在读出的值上依然成立。
        SelectCharacterSpec spec = Sized(40, 2, 5);
        for (int j = 0; j < 20; j++)
        {
            spec.QuestSlots[j] = j < 12 ? (901 + j, 0x00020000L + j) : (0xFFFF, 0);
        }
        spec.TutorialFlagIndices = new[] { 3, 9, 27, 40, 77 };
        spec.TotalFatigue = 905;
        spec.UsedFatigue = 901;
        spec.TotalMaxFatigue = 1400;

        SelectCharacterLoadInfo l = ParseOk(spec.Build()).Load;

        Assert.True(l.TotalFatigue <= l.TotalMaxFatigue);
        Assert.True(l.UsedFatigue <= l.TotalMaxFatigue);
        Assert.True(l.TutorialFlagIndices.Count <= 0x4e);
        for (int m = 1; m < l.TutorialFlagIndices.Count; m++)
        {
            Assert.True(l.TutorialFlagIndices[m] > l.TutorialFlagIndices[m - 1]);
        }
        bool seenEmpty = false;
        var keys = new HashSet<ushort>();
        foreach (SelectCharacterQuestSlot slot in l.QuestSlots)
        {
            if (slot.IsEmpty)
            {
                seenEmpty = true;
                Assert.Equal(0u, slot.Value);
            }
            else
            {
                Assert.False(seenEmpty);
                Assert.True(keys.Add(slot.Key));
            }
        }
        AssertLoad(spec, l);
    }

    [Fact]
    public void C05_properties_of_the_server_write_order_are_not_validity_conditions()
    {
        // 空槽夹在中间、空槽带非 0 值、非空键重复、教程下标乱序重复、B2 大于 B3:全部照收
        SelectCharacterSpec spec = Sized(41, 1, 4);
        spec.QuestSlots[0] = (0xFFFF, 900);
        spec.QuestSlots[1] = (901, 1);
        spec.QuestSlots[2] = (901, 2);
        spec.QuestSlots[3] = (0xFFFF, 0);
        spec.QuestSlots[4] = (902, 3);
        spec.TutorialFlagIndices = new[] { 77, 3, 3, 0xFF };
        spec.TotalFatigue = 1500;
        spec.TotalMaxFatigue = 901;

        SelectCharacterResult r = ParseOk(spec.Build());

        AssertLoad(spec, r.Load);
    }

    [Fact]
    public void C09_C10_the_flag_and_failure_code_read_order_is_one_byte_each()
    {
        // 派发层先读 u8 flag,flag 为 0 时再读 u8 err(C09 的读原语个数、C10 的 check_error 返回码表):失败体恰好 2 字节
        Assert.Equal(2, SelectCharacterSpec.Failure(0x13).Length);
        SelectCharacterResult r = ParseOk(SelectCharacterSpec.Failure(0x13));
        Assert.Equal(0x13, r.FailureCode);
        Assert.Equal(0, r.TrailingByteCount);
    }

    [Fact]
    public void C05_success_is_the_nonzero_test_the_client_uses_not_equals_one()
    {
        // 客户端判的是 flag 非 0;服务端只写 1。解析器与客户端同口径
        SelectCharacterSpec one = Sized(42, 0, 0);
        SelectCharacterSpec seven = Sized(42, 0, 0);
        seven.Flag = 7;

        Assert.True(ParseOk(one.Build()).IsSuccess);
        Assert.True(ParseOk(seven.Build()).IsSuccess);
        Assert.False(ParseOk(SelectCharacterSpec.Failure(8)).IsSuccess);
    }
}
