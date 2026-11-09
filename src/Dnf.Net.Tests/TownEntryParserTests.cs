using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 进城明文帧解析器(纯函数,E-358/E-359)。每一帧都是手工拼的合成字节:15 字节头(类、包号、整帧长度、两个相同的 crc32N)
/// 加载荷,再经 <see cref="ServerFrameDecoder"/>(不加密的替身密码)解码成 <see cref="DecodedFrame"/>,走的是真实的解码路径。
/// 不读任何真实帧文件;时、分与载荷字节全是合成值,不含真实角色名、地图名或数值表。
/// </summary>
public class TownEntryParserTests
{
    // 规则里的包号在这里写成字面量,不引用产品常量:产品常量被改动时,测试要能发现。
    private const ushort EnterCompleteId = 124;
    private const ushort TimeSyncId = 300;
    private const byte Noti = 0;
    private const byte Cmd = 1;

    /// <summary>手工拼一个服务端整帧:没有发送钩子,两个校验值都是载荷的 crc32N。</summary>
    private static byte[] Wire(byte cls, ushort id, byte[] body)
    {
        uint crc = Crc32N.Compute(body);
        byte[] f = new byte[15 + body.Length];
        f[0] = cls;
        f[1] = (byte)id;
        f[2] = (byte)(id >> 8);
        BitConverter.GetBytes((uint)f.Length).CopyTo(f, 3);
        BitConverter.GetBytes(crc).CopyTo(f, 7);
        BitConverter.GetBytes(crc).CopyTo(f, 11);
        body.CopyTo(f, 15);
        return f;
    }

    /// <summary>拼一帧并解码。别用包号 1(启动包)、2、9、87(压缩包)与类 1 的 21 字节包号 1(拒绝帧),它们走特殊解码路径。</summary>
    private static DecodedFrame Frame(byte cls, ushort id, params byte[] body)
    {
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        Assert.Equal(FrameStatus.Ok, decoder.Decode(Wire(cls, id, body), out DecodedFrame frame));
        return frame;
    }

    /// <summary>时间同步载荷:u16 时 + u16 分,小端。</summary>
    private static byte[] TimeBody(int hour, int minute)
    {
        return new[] { (byte)hour, (byte)(hour >> 8), (byte)minute, (byte)(minute >> 8) };
    }

    private static TownEntrySnapshot Parse(params DecodedFrame[] frames)
    {
        return TownEntryParser.Parse(frames);
    }

    /// <summary>没有任何明文字段被填上(槽号不在此列:它来自参数)。</summary>
    private static void AssertNoFieldsFilled(TownEntrySnapshot s)
    {
        Assert.False(s.EnterComplete);
        Assert.Null(s.ServerHour);
        Assert.Null(s.ServerMinute);
        Assert.Null(s.Level);
        Assert.Null(s.Job);
        Assert.Null(s.GrowType);
        Assert.Null(s.AreaId);
    }

    private static void AssertCounts(TownEntrySnapshot s, int seen, int parsed, int malformed)
    {
        Assert.Equal(seen, s.FramesSeen);
        Assert.Equal(parsed, s.FramesParsed);
        Assert.Equal(malformed, s.FramesMalformed);
    }

    // ---- 空输入与常量 ----

    [Fact]
    public void Null_empty_and_Empty_snapshot_have_nothing_filled()
    {
        TownEntrySnapshot[] snapshots =
        {
            TownEntryParser.Parse(null),
            TownEntryParser.Parse(new DecodedFrame[0]),
            TownEntryParser.Parse(new List<DecodedFrame>()),
            TownEntrySnapshot.Empty,
        };
        foreach (TownEntrySnapshot s in snapshots)
        {
            Assert.NotNull(s);
            Assert.Null(s.Slot);
            AssertNoFieldsFilled(s);
            AssertCounts(s, 0, 0, 0);
        }
    }

    [Fact]
    public void Production_constants_match_the_documented_ids()
    {
        Assert.Equal(124, SlotJudge.EnterCompletePacketId);
        Assert.Equal(300, TownEntryParser.TimeSyncPacketId);
    }

    // ---- 进城完成(类 0、包号 124) ----

    [Fact]
    public void Enter_complete_noti_is_recognised_by_class_and_id_only()
    {
        // 空载荷是实测形态;带任意尾巴也认:只看类与包号,不看载荷
        TownEntrySnapshot empty = Parse(Frame(Noti, EnterCompleteId));
        Assert.True(empty.EnterComplete);
        AssertCounts(empty, 1, 1, 0);

        TownEntrySnapshot withTail = Parse(Frame(Noti, EnterCompleteId, 0xAA, 0xBB, 0, 0, 0));
        Assert.True(withTail.EnterComplete);
        Assert.Null(withTail.ServerHour);
        AssertCounts(withTail, 1, 1, 0);
    }

    [Fact]
    public void Repeated_enter_complete_counts_each_frame_but_stays_true()
    {
        TownEntrySnapshot s = Parse(Frame(Noti, EnterCompleteId), Frame(Noti, EnterCompleteId));
        Assert.True(s.EnterComplete);
        AssertCounts(s, 2, 2, 0);
    }

    // ---- 包类与包号的筛选 ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void Frames_of_other_classes_are_ignored_whatever_their_id(int cls)
    {
        TownEntrySnapshot s = Parse(Frame((byte)cls, EnterCompleteId), Frame((byte)cls, TimeSyncId, TimeBody(5, 6)));
        AssertNoFieldsFilled(s);
        AssertCounts(s, 2, 0, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(123)]
    [InlineData(125)]
    [InlineData(299)]
    [InlineData(301)]
    [InlineData(355)]
    [InlineData(561)]
    [InlineData(65535)]
    public void Unknown_noti_ids_are_skipped_even_when_the_payload_looks_like_a_known_one(int id)
    {
        // 载荷刻意拼成"合法的时间同步"形态:认错包号就会读出时分(或错位计数),测试能发现
        TownEntrySnapshot s = Parse(Frame(Noti, (ushort)id, TimeBody(5, 6)));
        AssertNoFieldsFilled(s);
        AssertCounts(s, 1, 0, 0);
    }

    [Fact]
    public void Fake_envelope_frame_is_skipped_without_peeking_inside()
    {
        // 561 在默认解码器里是不加密的帧,载荷原样;里面哪怕藏着合法时间同步与"进城完成"形态也不碰
        DecodedFrame envelope = Frame(Noti, 561, TimeBody(5, 6));
        Assert.Equal(DecodedKind.Plain, envelope.Kind);
        TownEntrySnapshot s = Parse(envelope, Frame(Noti, 561, 0));
        AssertNoFieldsFilled(s);
        AssertCounts(s, 2, 0, 0);
    }

    // ---- 时间同步(类 0、包号 300):u16 时 + u16 分,小端 ----

    [Fact]
    public void Time_sync_reads_hour_then_minute_little_endian()
    {
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, 7, 0, 9, 0));
        Assert.Equal((int?)7, s.ServerHour);
        Assert.Equal((int?)9, s.ServerMinute);
        Assert.False(s.EnterComplete);
        AssertCounts(s, 1, 1, 0);

        // 时与分对调后读出的是另一对值,证明顺序是"时在前"
        TownEntrySnapshot swapped = Parse(Frame(Noti, TimeSyncId, 9, 0, 7, 0));
        Assert.Equal((int?)9, swapped.ServerHour);
        Assert.Equal((int?)7, swapped.ServerMinute);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(12, 30, true)]
    [InlineData(23, 0, true)]
    [InlineData(0, 59, true)]
    [InlineData(23, 59, true)]
    [InlineData(24, 0, false)]
    [InlineData(0, 60, false)]
    [InlineData(24, 60, false)]
    [InlineData(255, 0, false)]
    [InlineData(0, 255, false)]
    [InlineData(256, 0, false)]
    [InlineData(0, 256, false)]
    [InlineData(65535, 59, false)]
    [InlineData(23, 65535, false)]
    public void Time_sync_value_ranges(int hour, int minute, bool valid)
    {
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, TimeBody(hour, minute)));
        if (valid)
        {
            Assert.Equal((int?)hour, s.ServerHour);
            Assert.Equal((int?)minute, s.ServerMinute);
            AssertCounts(s, 1, 1, 0);
        }
        else
        {
            Assert.Null(s.ServerHour);
            Assert.Null(s.ServerMinute);
            AssertCounts(s, 1, 0, 1);
        }
    }

    [Fact]
    public void Time_sync_single_byte_corruption_sweep_matches_the_documented_ranges()
    {
        // 合法载荷的 4 个字节逐个换成 0..255:期望值由规则(小端、时 ≤ 23、分 ≤ 59)独立推出
        for (int pos = 0; pos < 4; pos++)
        {
            for (int value = 0; value <= 255; value++)
            {
                byte[] body = TimeBody(5, 6);
                body[pos] = (byte)value;
                int hour = body[0] | (body[1] << 8);
                int minute = body[2] | (body[3] << 8);
                bool valid = hour <= 23 && minute <= 59;

                TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, body));
                if (valid)
                {
                    Assert.Equal((int?)hour, s.ServerHour);
                    Assert.Equal((int?)minute, s.ServerMinute);
                    AssertCounts(s, 1, 1, 0);
                }
                else
                {
                    Assert.Null(s.ServerHour);
                    Assert.Null(s.ServerMinute);
                    AssertCounts(s, 1, 0, 1);
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Short_time_sync_payloads_are_malformed_and_skipped(int length)
    {
        byte[] body = TimeBody(5, 6).Take(length).ToArray();
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, body));
        Assert.Null(s.ServerHour);
        Assert.Null(s.ServerMinute);
        AssertCounts(s, 1, 0, 1);
    }

    [Fact]
    public void Misaligned_time_sync_payloads_are_malformed()
    {
        byte[] good = TimeBody(5, 6);
        byte[] extraPrefix = new byte[] { 0xEE }.Concat(good).ToArray();
        byte[] lostFirstByte = good.Skip(1).ToArray();
        byte[] bigEndian = { 0, 5, 0, 6 };
        foreach (byte[] body in new[] { extraPrefix, lostFirstByte, bigEndian })
        {
            TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, body));
            Assert.Null(s.ServerHour);
            Assert.Null(s.ServerMinute);
            AssertCounts(s, 1, 0, 1);
        }
    }

    [Fact]
    public void Time_sync_tolerates_trailing_hash_and_padding()
    {
        // 载荷后面实际还有 4 字节折叠哈希与会话加密补的零;读完 4 个字节就停,不核对也不要求恰好 4 字节
        byte[] body = TimeBody(5, 6).Concat(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }).Concat(new byte[7]).ToArray();
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, body));
        Assert.Equal((int?)5, s.ServerHour);
        Assert.Equal((int?)6, s.ServerMinute);
        AssertCounts(s, 1, 1, 0);
    }

    // ---- 多帧的合并规则 ----

    [Fact]
    public void Later_valid_time_sync_overrides_the_earlier_one()
    {
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, TimeBody(5, 6)), Frame(Noti, TimeSyncId, TimeBody(7, 8)));
        Assert.Equal((int?)7, s.ServerHour);
        Assert.Equal((int?)8, s.ServerMinute);
        AssertCounts(s, 2, 2, 0);
    }

    [Fact]
    public void Malformed_time_sync_does_not_clear_an_earlier_valid_value()
    {
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, TimeBody(5, 6)), Frame(Noti, TimeSyncId, 1, 2));
        Assert.Equal((int?)5, s.ServerHour);
        Assert.Equal((int?)6, s.ServerMinute);
        AssertCounts(s, 2, 1, 1);

        // 越界值同样不覆盖
        TownEntrySnapshot outOfRange = Parse(Frame(Noti, TimeSyncId, TimeBody(5, 6)), Frame(Noti, TimeSyncId, TimeBody(24, 0)));
        Assert.Equal((int?)5, outOfRange.ServerHour);
        Assert.Equal((int?)6, outOfRange.ServerMinute);
        AssertCounts(outOfRange, 2, 1, 1);
    }

    [Fact]
    public void Valid_time_sync_after_a_malformed_one_is_still_taken()
    {
        TownEntrySnapshot s = Parse(Frame(Noti, TimeSyncId, 1), Frame(Noti, TimeSyncId, TimeBody(5, 6)));
        Assert.Equal((int?)5, s.ServerHour);
        Assert.Equal((int?)6, s.ServerMinute);
        AssertCounts(s, 2, 1, 1);
    }

    [Fact]
    public void Time_sync_and_enter_complete_can_arrive_in_either_order()
    {
        TownEntrySnapshot timeFirst = Parse(Frame(Noti, TimeSyncId, TimeBody(5, 6)), Frame(Noti, EnterCompleteId));
        TownEntrySnapshot enterFirst = Parse(Frame(Noti, EnterCompleteId), Frame(Noti, TimeSyncId, TimeBody(5, 6)));
        foreach (TownEntrySnapshot s in new[] { timeFirst, enterFirst })
        {
            Assert.True(s.EnterComplete);
            Assert.Equal((int?)5, s.ServerHour);
            Assert.Equal((int?)6, s.ServerMinute);
            AssertCounts(s, 2, 2, 0);
        }
    }

    [Fact]
    public void Null_list_entries_are_ignored_and_not_counted()
    {
        TownEntrySnapshot s = Parse(null, Frame(Noti, EnterCompleteId), null, Frame(Noti, TimeSyncId, TimeBody(5, 6)), null);
        Assert.True(s.EnterComplete);
        Assert.Equal((int?)5, s.ServerHour);
        AssertCounts(s, 2, 2, 0);

        TownEntrySnapshot onlyNulls = Parse(null, null);
        AssertNoFieldsFilled(onlyNulls);
        AssertCounts(onlyNulls, 0, 0, 0);
    }

    // ---- 槽号:来自参数,不是解析所得 ----

    [Fact]
    public void Slot_is_taken_from_the_argument_and_never_from_frames()
    {
        DecodedFrame[] frames = { Frame(Noti, EnterCompleteId), Frame(Noti, TimeSyncId, TimeBody(5, 6)) };
        Assert.Null(TownEntryParser.Parse(frames).Slot);
        Assert.Null(TownEntryParser.Parse(frames, -1).Slot);
        Assert.Null(TownEntryParser.Parse(frames, -2).Slot);
        Assert.Null(TownEntryParser.Parse(frames, int.MinValue).Slot);
        Assert.Equal((int?)0, TownEntryParser.Parse(frames, 0).Slot);
        Assert.Equal((int?)3, TownEntryParser.Parse(frames, 3).Slot);
        Assert.Equal((int?)int.MaxValue, TownEntryParser.Parse(frames, int.MaxValue).Slot);

        // 没有帧、帧全不认得时槽号也照样带上
        Assert.Equal((int?)2, TownEntryParser.Parse(null, 2).Slot);
        Assert.Equal((int?)2, TownEntryParser.Parse(new[] { Frame(Noti, 3) }, 2).Slot);
    }

    // ---- 缺口字段:明文里没有承载帧,恒为空 ----

    [Fact]
    public void Fields_without_plaintext_evidence_stay_empty_whatever_the_frames_say()
    {
        // 等级、职业、成长类型、区域编号:明文帧里没有承载包,给什么形态的帧都不许"猜"出值
        TownEntrySnapshot s = TownEntryParser.Parse(new[]
        {
            Frame(Noti, 561, 1, 2, 3, 4, 5, 6),
            Frame(Cmd, 4, 1, 3, 3, 3, 3),
            Frame(Noti, 23, 1, 2, 3, 4, 5, 6),
            Frame(Noti, 355, 2, 0, 0, 0, 1, 0),
            Frame(Noti, TimeSyncId, TimeBody(11, 22)),
            Frame(Noti, EnterCompleteId),
        }, 4);
        Assert.Null(s.Level);
        Assert.Null(s.Job);
        Assert.Null(s.GrowType);
        Assert.Null(s.AreaId);
        Assert.Equal((int?)4, s.Slot);
        Assert.True(s.EnterComplete);
    }

    // ---- 容错:任何载荷都不抛 ----

    [Fact]
    public void Arbitrary_payloads_never_throw_and_counters_stay_consistent()
    {
        // 确定性伪随机字节(固定种子):认得与不认得的 (类, 包号) 组合 × 0..40 字节的载荷
        uint seed = 0x2468ACE1;
        foreach (byte cls in new byte[] { 0, 1, 2, 255 })
        {
            foreach (ushort id in new ushort[] { 0, 3, 123, 124, 125, 299, 300, 301, 355, 561, 65535 })
            {
                for (int len = 0; len <= 40; len++)
                {
                    byte[] body = new byte[len];
                    for (int i = 0; i < len; i++)
                    {
                        seed = (seed * 1664525u) + 1013904223u;
                        body[i] = (byte)(seed >> 24);
                    }

                    DecodedFrame frame = Frame(cls, id, body);
                    TownEntrySnapshot s = null;
                    Exception ex = Record.Exception(() => { s = TownEntryParser.Parse(new[] { frame }, 1); });
                    Assert.Null(ex);
                    Assert.Equal(1, s.FramesSeen);
                    Assert.True(s.FramesParsed + s.FramesMalformed <= 1);
                    if (cls != Noti || (id != EnterCompleteId && id != TimeSyncId))
                    {
                        Assert.Equal(0, s.FramesParsed + s.FramesMalformed);
                    }
                    else if (id == EnterCompleteId)
                    {
                        Assert.Equal(1, s.FramesParsed);
                    }
                    else
                    {
                        Assert.Equal(1, s.FramesParsed + s.FramesMalformed);
                        Assert.Equal(s.FramesParsed == 1, s.ServerHour.HasValue && s.ServerMinute.HasValue);
                    }
                }
            }
        }
    }

    // ---- 快照本身 ----

    [Fact]
    public void Parsing_does_not_modify_the_input_frames_and_is_repeatable()
    {
        DecodedFrame time = Frame(Noti, TimeSyncId, TimeBody(5, 6));
        DecodedFrame enter = Frame(Noti, EnterCompleteId, 9, 9);
        byte[] timeBefore = (byte[])time.Payload.Clone();
        byte[] enterBefore = (byte[])enter.Payload.Clone();
        DecodedFrame[] frames = { time, enter };

        TownEntrySnapshot first = TownEntryParser.Parse(frames, 1);
        TownEntrySnapshot second = TownEntryParser.Parse(frames, 1);

        Assert.Equal(timeBefore, time.Payload);
        Assert.Equal(enterBefore, enter.Payload);
        Assert.Equal(first.ServerHour, second.ServerHour);
        Assert.Equal(first.ServerMinute, second.ServerMinute);
        Assert.Equal(first.EnterComplete, second.EnterComplete);
        Assert.Equal(first.Slot, second.Slot);
        Assert.Equal(first.FramesParsed, second.FramesParsed);
    }

    [Fact]
    public void Snapshot_is_read_only_and_holds_no_frame_data()
    {
        Type type = typeof(TownEntrySnapshot);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.Null(property.GetSetMethod(true));
        }

        // 外部只能经解析器或 Empty 拿到快照
        Assert.Empty(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public));

        // 实例字段全是只读的简单值,不留帧、载荷或列表的引用
        Type[] allowed = { typeof(int), typeof(bool), typeof(int?) };
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.True(field.IsInitOnly);
            Assert.Contains(field.FieldType, allowed);
        }
    }

    [Fact]
    public void Synthetic_town_entry_sequence_gives_the_expected_snapshot()
    {
        // 形态仿照"选角窗口 + 尾窗口"的明文回程:信封、选角回包形状、列表类通知、时间同步、进城完成;内容全是合成值
        DecodedFrame[] window =
        {
            Frame(Noti, 561, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
            Frame(Cmd, 4, 1, 7, 7, 7),
            Frame(Noti, 355, 2, 0, 0, 0, 1, 0),
            Frame(Noti, TimeSyncId, 5, 0, 6, 0),
        };
        DecodedFrame[] tail =
        {
            Frame(Noti, EnterCompleteId),
            Frame(Noti, 561, 0x66, 0x55, 0x44, 0x33),
        };
        List<DecodedFrame> all = new List<DecodedFrame>(window);
        all.AddRange(tail);

        TownEntrySnapshot s = TownEntryParser.Parse(all, 2);

        Assert.Equal((int?)2, s.Slot);
        Assert.True(s.EnterComplete);
        Assert.Equal((int?)5, s.ServerHour);
        Assert.Equal((int?)6, s.ServerMinute);
        Assert.Null(s.Level);
        Assert.Null(s.Job);
        Assert.Null(s.GrowType);
        Assert.Null(s.AreaId);
        AssertCounts(s, 6, 2, 0);
    }
}
