using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话层对 561 信封内层帧的解压:内层帧的包号是压缩包号(2、9、0x57,与直接帧同一份)时,载荷与直接帧同口径地处理——
/// 先认 zlib 头(CMF 低 4 位为 8 且 CMF*256+FLG 能被 31 整除),再走既有的严格解压,解开就以明文载荷交付;
/// 认得出头却解不开,原样交付、不丢帧,并在装着它的信封帧的事件备注上缀一次 ",内层解压失败:原因"(既有的事件条数与各项计数一概不变);
/// 包号不在集合里的内层帧、头不像 zlib 的载荷一律不碰,载荷恰好长得像 zlib 也一样。
/// 城镇快照因此能从压缩的内层 (0,2) 角色列表回填等级、职业、成长类型。
/// 全部合成:账号号 905、角色列表来自 <see cref="UserInfoTestData"/>、无关包号取 900 以上(紧挨着集合成员的 1、3、8、10、0x56、0x58 是查边界用的);
/// 信封用 <see cref="HostTestKit.RealEnvelope"/>(真信封:算法 16、合成序号)。用例编号 900 起,写在各用例说明的【】里;一个编号下的多行数据共用它。
/// </summary>
public class InnerFrameInflateTests
{
    /// <summary>失败标记里的固定字样(整串写在这里,与 <c>GameClientSession</c> 写的对不上就会被抓到)。</summary>
    private const string FailureMark = "内层解压失败";

    // ======== 合成数据与小工具 ========

    /// <summary>合成明文:确定性字节序列;首字节 0x10,低 4 位不是 8,不会被当成 zlib 头。</summary>
    private static byte[] SynthPlain(int length = 64)
    {
        return GatewayTestSupport.Pattern(length, 0x10, 3);
    }

    private static byte[] Zlib(byte[] plain)
    {
        return UserInfoTestData.Zlib(plain);
    }

    /// <summary>头合法(0x78 0x9C)、后面全是 0xFF 的"伪装":第一个 deflate 块头就是保留的块类型,谁也解不开。</summary>
    private static byte[] DisguisedGarbage()
    {
        byte[] bytes = new byte[32];
        Array.Fill(bytes, (byte)0xFF);
        bytes[0] = 0x78;
        bytes[1] = 0x9C;
        return bytes;
    }

    /// <summary>把 zlib 流的头两个字节换成指定值(deflate 流与 Adler-32 原样)。</summary>
    private static byte[] WithHeader(byte[] stream, int cmf, int flg)
    {
        byte[] copy = (byte[])stream.Clone();
        copy[0] = (byte)cmf;
        copy[1] = (byte)flg;
        return copy;
    }

    /// <summary>在启动包之后接上给定的帧,打开一个读自内存流的会话(流读完即视为对端关闭,所以收帧不会空等)。</summary>
    private static async Task<GameClientSession> OpenAsync(params byte[][] frames)
    {
        MemoryStream stream = new();
        byte[] bootstrap = DungeonEntryWire.BootstrapFrame();
        stream.Write(bootstrap, 0, bootstrap.Length);
        foreach (byte[] frame in frames)
        {
            stream.Write(frame, 0, frame.Length);
        }
        stream.Position = 0;
        GameClientSession session = new(stream, s => new SliceXorCipher(s));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        return session;
    }

    private static async Task<(GameClientSession Session, List<DecodedFrame> Frames)> Receive(params byte[][] frames)
    {
        GameClientSession session = await OpenAsync(frames);
        List<DecodedFrame> received = await GatewayTestSupport.Bounded(session.ReceiveAsync(TimeSpan.FromSeconds(5)), 20);
        return (session, received);
    }

    private static SessionEvent[] EnvelopeEvents(GameClientSession session)
    {
        return session.Events.Where(e => !e.Outbound && e.PacketClass == PacketClass.Noti && e.PacketId == 561).ToArray();
    }

    private static SessionEvent[] InnerEvents(GameClientSession session)
    {
        return session.Events.Where(e => e.Note.StartsWith("信封内层", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>失败标记在全部事件备注里一共出现了几次(按出现次数数,不是按"带标记的事件条数":同一条备注里缀了两遍也算两次)。</summary>
    private static int MarkCount(GameClientSession session)
    {
        int count = 0;
        foreach (SessionEvent e in session.Events)
        {
            for (int at = e.Note.IndexOf(FailureMark, StringComparison.Ordinal); at >= 0;
                 at = e.Note.IndexOf(FailureMark, at + FailureMark.Length, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 既有计数口径没被内层处理改动:事件条数是预期的(启动包 1 条,每个信封加它自己的内层事件各 1 条,直接帧各 1 条),
    /// 第二层不符次数只来自直接帧(备注里的 ",第二层不符" 恰好就是它的次数,内层帧不过第二层),最近解码失败与切帧错误没被改写。
    /// </summary>
    private static void AssertExistingCountsUntouched(GameClientSession session, int expectedEvents)
    {
        Assert.Equal(expectedEvents, session.Events.Count);
        Assert.Equal(session.Events.Count(e => e.Note.Contains(",第二层不符")), session.SecondLayerMismatches);
        Assert.Equal(FrameStatus.Ok, session.LastDecodeStatus);
        Assert.Equal(FrameError.None, session.LastFrameError);
    }

    // ======== 端到端:压缩的内层 (0,2) 角色列表,城镇快照回填 ========

    /// <summary>
    /// 【900】信封里封着内层 (0,2),载荷是 zlib 压缩过的合成角色列表(真服实测的形态):经会话与进城编排,快照的等级、职业、成长类型有值,
    /// 与合成值逐个一致(成长类型取字节低 4 位);看过与读出的帧数按各窗口的帧各计一次(信封帧自己只计看过),没有错位。
    /// 既有计数口径照旧:进城结果的帧数仍是会话事件条数,第二层不符次数仍取自会话。
    /// </summary>
    [Fact]
    public async Task A_zlib_compressed_inner_character_list_backfills_the_snapshot_with_the_synthetic_values()
    {
        UserInfoTestData.EntrySpec chosen = UserInfoTestData.Standard(0);
        chosen.Level = 90;
        chosen.Job = 91;
        chosen.GrowType = 0x4A;                     // 低 4 位 10
        byte[] list = UserInfoTestData.Packet(chosen);
        byte[] compressed = Zlib(list);
        Assert.NotEqual(list, compressed);

        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, compressed)),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.Equal(0, snapshot.Slot);
        Assert.Equal(90, snapshot.Level);
        Assert.Equal(91, snapshot.Job);
        Assert.Equal(10, snapshot.GrowType);
        Assert.Equal(0, snapshot.FramesMalformed);
        Assert.Equal(4, snapshot.FramesSeen);        // 登录回包、信封帧、内层帧、进城完成
        Assert.Equal(2, snapshot.FramesParsed);      // 内层列表、进城完成
        Assert.Equal(session.Events.Count, result.FramesSeen);
        Assert.Equal(session.SecondLayerMismatches, result.SecondLayerMismatches);
        Assert.Equal(0, MarkCount(session));
        SessionEvent inner = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", inner.Note);
        Assert.Equal(list.Length, inner.PayloadLength);
    }

    /// <summary>【901】压缩的内层列表也按槽号取同一位置的条目(与直接来的列表同一规则)。</summary>
    [Theory]
    [InlineData(0, 61, 50, 5)]
    [InlineData(1, 62, 51, 6)]
    [InlineData(2, 63, 52, 7)]
    public async Task The_compressed_inner_list_is_read_at_the_selected_slot(int enterAt, int level, int job, int growType)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, Zlib(HostTestKit.ListPayload()))),
            slot => slot == enterAt ? HostTestKit.EnterComplete() : HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 3);

        Assert.True(result.Entered);
        Assert.Equal(enterAt, result.Snapshot.Slot);
        Assert.Equal(level, result.Snapshot.Level);
        Assert.Equal(job, result.Snapshot.Job);
        Assert.Equal(growType, result.Snapshot.GrowType);
        Assert.Equal(0, result.Snapshot.FramesMalformed);
    }

    /// <summary>
    /// 【902】较大的列表(六个条目,带名字与装备)也解得开,选到最后一个槽时读到的是第六个条目。
    /// </summary>
    [Fact]
    public async Task A_larger_compressed_inner_list_is_inflated_and_read_at_the_last_slot()
    {
        UserInfoTestData.EntrySpec[] entries = Enumerable.Range(0, 6).Select(i => UserInfoTestData.Standard(i, nameLength: 20, equipCount: 3)).ToArray();
        byte[] list = UserInfoTestData.Packet(entries);
        byte[] compressed = Zlib(list);
        Assert.True(compressed.Length < list.Length);

        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, compressed)),
            slot => slot == 5 ? HostTestKit.EnterComplete() : HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 6);

        Assert.True(result.Entered);
        Assert.Equal(5, result.Snapshot.Slot);
        Assert.Equal(entries[5].Level, result.Snapshot.Level);
        Assert.Equal(entries[5].Job, result.Snapshot.Job);
        Assert.Equal(entries[5].GrowType & 0x0F, result.Snapshot.GrowType);
        Assert.Equal(0, result.Snapshot.FramesMalformed);
        Assert.Equal(list.Length, InnerEvents(session).Single().PayloadLength);
    }

    /// <summary>
    /// 【903】内层 (0,2) 载荷是明文(不压缩)时行为与改前一致:照样回填,帧数与错位计数不变,
    /// 内层帧没有经过解压(不带压缩标志、事件的载荷长度就是原长),信封帧的事件上没有任何失败标记。
    /// </summary>
    [Fact]
    public async Task A_plaintext_inner_character_list_behaves_exactly_as_before()
    {
        byte[] list = HostTestKit.ListPayload();
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, list)),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.Equal(61, snapshot.Level);
        Assert.Equal(50, snapshot.Job);
        Assert.Equal(5, snapshot.GrowType);
        Assert.Equal(0, snapshot.FramesMalformed);
        Assert.Equal(4, snapshot.FramesSeen);
        Assert.Equal(2, snapshot.FramesParsed);
        Assert.Equal(session.Events.Count, result.FramesSeen);
        Assert.Equal(0, MarkCount(session));
        SessionEvent inner = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", inner.Note);
        Assert.Equal(list.Length, inner.PayloadLength);
    }

    /// <summary>
    /// 【904】内层 (0,2) 载荷伪装成 zlib 头(0x78 0x9C)但内容解不开:原样交付,解析器读不通、计一次错位,快照三个角色字段仍为空(不猜、不丢帧);
    /// 看过与读出的帧数、进城结果的帧数都与明文内层时相同(不多不少),失败只在信封帧的事件备注上记一次,汇总行的各项计数不变。
    /// </summary>
    [Fact]
    public async Task A_zlib_looking_but_undecodable_inner_list_is_delivered_raw_counted_once_and_leaves_the_snapshot_empty()
    {
        byte[] disguised = DisguisedGarbage();
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, disguised)),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.Null(snapshot.Level);
        Assert.Null(snapshot.Job);
        Assert.Null(snapshot.GrowType);
        Assert.Equal(1, snapshot.FramesMalformed);   // 原样交付,解析器读不通
        Assert.Equal(1, snapshot.FramesParsed);      // 进城完成
        Assert.Equal(4, snapshot.FramesSeen);        // 与明文内层时一样:登录回包、信封帧、内层帧、进城完成
        Assert.Equal(session.Events.Count, result.FramesSeen);
        Assert.Equal(session.SecondLayerMismatches, result.SecondLayerMismatches);

        Assert.Equal(1, MarkCount(session));
        SessionEvent envelope = Assert.Single(EnvelopeEvents(session));
        Assert.EndsWith(",内层解压失败:InflateFailed", envelope.Note, StringComparison.Ordinal);
        SessionEvent inner = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", inner.Note);
        Assert.Equal(disguised.Length, inner.PayloadLength);
        Assert.Equal(
            "DNF_NET session events=" + session.Events.Count + " received=" + session.Events.Count(e => !e.Outbound)
                + " envelopes=1 opened=1 unwrapFailed=0 unwrapReasons=- innerShort=0 innerIds=2 id2Direct=0 id2Inner=1",
            OnlineNetLog.SessionDigest(session.Events));
    }

    // ======== 会话层:各压缩包号、各种载荷形态 ========

    public static IEnumerable<object[]> CompressedIdsAndClasses()
    {
        foreach (int cls in new[] { 0, 1 })
        {
            foreach (int id in new[] { 2, 9, 0x57 })
            {
                yield return new object[] { cls, id };
            }
        }
    }

    /// <summary>
    /// 【905】压缩包号 2、9、0x57 的内层帧(类 0 与类 1 都一样,与直接帧一样不看类)各解开一次:交付的是明文载荷,带压缩标志,
    /// 仍是明文种类、不过第二层;事件备注仍恰好是 "信封内层",载荷长度是解压后的长度,整帧长度是内层整帧长。
    /// </summary>
    [Theory]
    [MemberData(nameof(CompressedIdsAndClasses))]
    public async Task Compressed_inner_frames_of_each_compressed_id_are_inflated_to_the_plain_payload(int cls, int id)
    {
        byte[] plain = SynthPlain(80);
        byte[] compressed = Zlib(plain);

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope((byte)cls, (ushort)id, compressed));

        Assert.Equal(2, frames.Count);
        Assert.Equal(561, frames[0].Header.PacketId);
        DecodedFrame inner = frames[1];
        Assert.Equal((PacketClass)cls, inner.Header.Class);
        Assert.Equal(id, inner.Header.PacketId);
        Assert.Equal(DecodedKind.Plain, inner.Kind);
        Assert.Equal(plain, inner.Payload);
        Assert.True(inner.WasCompressed);
        Assert.False(inner.SecondLayerChecked);

        SessionEvent unwrapped = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", unwrapped.Note);
        Assert.Equal((PacketClass)cls, unwrapped.PacketClass);
        Assert.Equal(id, unwrapped.PacketId);
        Assert.Equal(15 + compressed.Length, unwrapped.FrameLength);
        Assert.Equal(plain.Length, unwrapped.PayloadLength);
        Assert.Equal(0, MarkCount(session));
        AssertExistingCountsUntouched(session, 3);
    }

    public static IEnumerable<object[]> IdsOutsideTheCompressedSet()
    {
        foreach (int cls in new[] { 0, 1 })
        {
            // 紧挨着集合成员的 1、3、8、10、0x56、0x58;低字节与成员相同的 0x102、0x209、0x157;合成的无关包号 900 起
            foreach (int id in new[] { 1, 3, 8, 10, 0x56, 0x58, 0x102, 0x209, 0x157, 900, 902 })
            {
                yield return new object[] { cls, id };
            }
        }
    }

    /// <summary>
    /// 【906】包号不在 {2, 9, 0x57} 里的内层帧一律不碰:载荷恰好是合法的 zlib 字节也原样交付,不带压缩标志,
    /// 事件的载荷长度是压缩态的长度,不记任何失败(根本没去解)。
    /// </summary>
    [Theory]
    [MemberData(nameof(IdsOutsideTheCompressedSet))]
    public async Task Inner_frames_outside_the_compressed_id_set_are_never_inflated_even_if_they_look_like_zlib(int cls, int id)
    {
        byte[] compressed = Zlib(SynthPlain(80));
        Assert.False(ServerFrameDecoder.IsCompressedId(id));

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope((byte)cls, (ushort)id, compressed));

        Assert.Equal(2, frames.Count);
        DecodedFrame inner = frames[1];
        Assert.Equal(id, inner.Header.PacketId);
        Assert.Equal(compressed, inner.Payload);
        Assert.False(inner.WasCompressed);
        SessionEvent unwrapped = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", unwrapped.Note);
        Assert.Equal(compressed.Length, unwrapped.PayloadLength);
        Assert.Equal(0, MarkCount(session));
        AssertExistingCountsUntouched(session, 3);
    }

    /// <summary>
    /// 【906】的另一半:集合本身没被改动(恰是 2、9、0x57),内层帧用的就是直接帧那一份。
    /// </summary>
    [Fact]
    public void The_compressed_id_set_is_the_one_direct_frames_use()
    {
        Assert.Equal(new[] { 2, 9, 0x57 }, Enumerable.Range(0, 0x10000).Where(ServerFrameDecoder.IsCompressedId).ToArray());
    }

    public static IEnumerable<object[]> PayloadsThatAreNotZlib()
    {
        // 不认作 zlib 头的载荷:不足两个字节(没有 FLG 可看)、方法位是 8 但 FCHECK 不对、FCHECK 对但方法位不是 8、全零(0 也能被 31 整除)、普通明文
        byte[][] shapes =
        {
            Array.Empty<byte>(),
            new byte[] { 0x78 },
            new byte[] { 0x78, 0x9D, 1, 2, 3, 4, 5, 6, 7, 8 },
            new byte[] { 0x79, 0x18, 1, 2, 3, 4, 5, 6, 7, 8 },
            new byte[16],
            GatewayTestSupport.Pattern(48, 0x10, 3),
        };
        string[] names = { "empty", "one-byte", "method-8-bad-fcheck", "good-fcheck-method-9", "zeros", "plain" };
        foreach (int id in new[] { 2, 9, 0x57 })
        {
            for (int i = 0; i < shapes.Length; i++)
            {
                yield return new object[] { id, names[i], shapes[i] };
            }
        }
    }

    /// <summary>
    /// 【907】压缩包号的内层帧,载荷的头不像 zlib(空、一个字节、方法位对但校验位不对、校验位对但方法位不对、全零、普通明文)时一律不碰:
    /// 原样交付、不带压缩标志、不记失败,也不会因为载荷太短而出错。
    /// </summary>
    [Theory]
    [MemberData(nameof(PayloadsThatAreNotZlib))]
    public async Task Payloads_without_a_zlib_header_in_compressed_ids_are_delivered_unchanged_and_not_counted(int id, string shape, byte[] payload)
    {
        Assert.NotNull(shape);

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope(0, (ushort)id, payload));

        Assert.Equal(2, frames.Count);
        Assert.Equal(id, frames[1].Header.PacketId);
        Assert.Equal(payload, frames[1].Payload);
        Assert.False(frames[1].WasCompressed);
        SessionEvent unwrapped = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", unwrapped.Note);
        Assert.Equal(payload.Length, unwrapped.PayloadLength);
        Assert.Equal(0, MarkCount(session));
        AssertExistingCountsUntouched(session, 3);
    }

    /// <summary>【907】用到的头字节的算术没算错:FCHECK 一对一错、方法位一个 8 一个 9。</summary>
    [Fact]
    public void The_disguised_headers_are_what_the_cases_claim()
    {
        Assert.Equal(8, 0x78 & 0x0F);
        Assert.NotEqual(0, ((0x78 << 8) | 0x9D) % 31);
        Assert.NotEqual(8, 0x79 & 0x0F);
        Assert.Equal(0, ((0x79 << 8) | 0x18) % 31);
        Assert.Equal(0, ((0x00 << 8) | 0x00) % 31);
        Assert.Equal(0, ((0x78 << 8) | 0x9C) % 31);
        Assert.Equal(new byte[] { 0x78, 0x9C }, Zlib(SynthPlain()).Take(2).ToArray());    // 合成压缩流的头就是真服用的 0x789C
    }

    public static IEnumerable<object[]> UndecodableZlibLookingPayloads()
    {
        byte[] plain = GatewayTestSupport.Pattern(80, 0x10, 3);
        byte[] good = UserInfoTestData.Zlib(plain);
        byte[] truncated = good.Take(good.Length - 3).ToArray();
        byte[] badAdler = (byte[])good.Clone();
        badAdler[badAdler.Length - 1] ^= 0xFF;
        byte[] trailingGarbage = good.Concat(new byte[] { 0x01 }).ToArray();
        byte[] flippedInside = (byte[])good.Clone();
        flippedInside[flippedInside.Length / 2] ^= 0xFF;
        (string, byte[])[] shapes =
        {
            ("garbage-behind-a-good-header", DisguisedGarbage()),
            ("header-only", new byte[] { 0x78, 0x9C }),
            ("six-bytes", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00 }),
            ("window-bits-too-big", WithHeader(good, 0x88, 0x1C)),
            ("preset-dictionary-flag", WithHeader(good, 0x78, 0xBB)),
            ("truncated", truncated),
            ("bad-adler", badAdler),
            ("trailing-garbage", trailingGarbage),
            ("flipped-inside", flippedInside),
        };
        foreach (int id in new[] { 2, 9, 0x57 })
        {
            foreach ((string name, byte[] bytes) in shapes)
            {
                yield return new object[] { id, name, bytes };
            }
        }
    }

    /// <summary>
    /// 【908】认得出 zlib 头却解不开(头后面是垃圾、只有头、太短、窗口位数超限、带预设字典标志、被截断、Adler-32 不符、尾巴多了非零字节、流中间被改):
    /// 原样交付、不丢帧、不带压缩标志,失败只在装着它的信封帧的事件备注上缀一次(原因是 InflateFailed),
    /// 内层事件备注仍恰好是 "信封内层",事件条数与各项既有计数跟明文内层时完全一样,最近解码失败状态不被改写。
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableZlibLookingPayloads))]
    public async Task Zlib_looking_inner_payloads_that_cannot_be_inflated_are_delivered_raw_and_counted_once(int id, string shape, byte[] payload)
    {
        Assert.NotNull(shape);

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope(0, (ushort)id, payload));

        Assert.Equal(2, frames.Count);
        Assert.Equal(561, frames[0].Header.PacketId);
        Assert.Equal(id, frames[1].Header.PacketId);
        Assert.Equal(payload, frames[1].Payload);
        Assert.False(frames[1].WasCompressed);
        Assert.Equal(DecodedKind.Plain, frames[1].Kind);

        Assert.Equal(1, MarkCount(session));
        SessionEvent envelope = Assert.Single(EnvelopeEvents(session));
        Assert.EndsWith(",内层解压失败:InflateFailed", envelope.Note, StringComparison.Ordinal);
        Assert.StartsWith("Plain", envelope.Note, StringComparison.Ordinal);
        SessionEvent unwrapped = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", unwrapped.Note);
        Assert.Equal(id, unwrapped.PacketId);
        Assert.Equal(payload.Length, unwrapped.PayloadLength);
        Assert.Equal(15 + payload.Length, unwrapped.FrameLength);
        AssertExistingCountsUntouched(session, 3);
    }

    /// <summary>
    /// 【909】解压上限与直接帧同一个(单包缓冲 95000 字节):恰好 95000 字节解得开;多一个字节解不开,原样交付,失败原因记 PayloadTooLarge。
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task The_inflated_size_is_capped_at_the_packet_buffer_like_for_direct_frames(int overBy, bool inflates)
    {
        byte[] plain = new byte[FrameLayout.PacketBufferSize + overBy];     // 全零:压缩后只有百来个字节
        byte[] compressed = Zlib(plain);
        Assert.True(compressed.Length < 1000);

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope(0, 2, compressed));

        Assert.Equal(2, frames.Count);
        SessionEvent unwrapped = Assert.Single(InnerEvents(session));
        Assert.Equal("信封内层", unwrapped.Note);
        if (inflates)
        {
            Assert.Equal(FrameLayout.PacketBufferSize, frames[1].Payload.Length);
            Assert.True(frames[1].WasCompressed);
            Assert.Equal(FrameLayout.PacketBufferSize, unwrapped.PayloadLength);
            Assert.Equal(0, MarkCount(session));
        }
        else
        {
            Assert.Equal(compressed, frames[1].Payload);
            Assert.False(frames[1].WasCompressed);
            Assert.Equal(compressed.Length, unwrapped.PayloadLength);
            Assert.Equal(1, MarkCount(session));
            Assert.EndsWith(",内层解压失败:PayloadTooLarge", Assert.Single(EnvelopeEvents(session)).Note, StringComparison.Ordinal);
        }
        AssertExistingCountsUntouched(session, 3);
    }

    /// <summary>
    /// 【910】与直接帧同口径:同一个 zlib 载荷,作为直接来的压缩包号帧与封在信封里的内层帧,交付的明文载荷逐字节相同,都带压缩标志。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(0x57)]
    public async Task An_inner_frame_inflates_to_the_same_bytes_as_a_direct_frame_of_the_same_id(int id)
    {
        byte[] plain = SynthPlain(100);
        byte[] compressed = Zlib(plain);

        (_, List<DecodedFrame> direct) = await Receive(DungeonEntryWire.Notice((ushort)id, compressed));
        (_, List<DecodedFrame> wrapped) = await Receive(HostTestKit.RealEnvelope(0, (ushort)id, compressed));

        DecodedFrame viaDirect = Assert.Single(direct);
        DecodedFrame viaEnvelope = wrapped[1];
        Assert.True(viaDirect.WasCompressed);
        Assert.True(viaEnvelope.WasCompressed);
        Assert.Equal(plain, viaDirect.Payload);
        Assert.Equal(viaDirect.Payload, viaEnvelope.Payload);
    }

    /// <summary>
    /// 【911】直接帧一行没动:直接来的压缩包号帧载荷解不开,仍然整帧丢弃并记 "解码失败:InflateFailed"、改写最近解码失败状态;
    /// 同样的载荷封在信封里则保留交付,最近解码失败状态不被改写(与上面直接帧的丢弃是两件事,各按各的口径)。
    /// </summary>
    [Fact]
    public async Task A_direct_frame_with_an_undecodable_stream_is_still_dropped_while_the_inner_one_is_kept()
    {
        byte[] bad = DisguisedGarbage();

        (GameClientSession direct, List<DecodedFrame> directFrames) = await Receive(DungeonEntryWire.Notice(2, bad));
        Assert.Empty(directFrames);
        Assert.Equal(FrameStatus.InflateFailed, direct.LastDecodeStatus);
        Assert.Contains(direct.Events, e => e.Note == "解码失败:InflateFailed");
        Assert.Equal(0, MarkCount(direct));

        (GameClientSession wrapped, List<DecodedFrame> wrappedFrames) = await Receive(HostTestKit.RealEnvelope(0, 2, bad));
        Assert.Equal(2, wrappedFrames.Count);
        Assert.Equal(bad, wrappedFrames[1].Payload);
        Assert.Equal(FrameStatus.Ok, wrapped.LastDecodeStatus);
        Assert.DoesNotContain(wrapped.Events, e => e.Note.StartsWith("解码失败", StringComparison.Ordinal));
        Assert.Equal(1, MarkCount(wrapped));
    }

    /// <summary>
    /// 【912】一次读到多个信封与直接帧时,各信封的内层各按各的结果交付(解开的、明文的、解不开的、不在集合里的),按到达顺序排;
    /// 失败标记只落在解不开的那一个信封的事件上,直接帧与其余信封的事件备注不动,事件条数是预期的。
    /// </summary>
    [Fact]
    public async Task Several_envelopes_in_one_read_each_keep_their_own_result_and_only_the_failed_one_is_marked()
    {
        byte[] plainA = SynthPlain(50);
        byte[] plainB = { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] bad = DisguisedGarbage();
        byte[] notInSet = Zlib(SynthPlain(30));

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(
            HostTestKit.RealEnvelope(0, 2, Zlib(plainA)),           // 压缩、解得开
            HostTestKit.DirectListNotice(),                         // 直接来的列表:与改前一致
            HostTestKit.RealEnvelope(0, 9, plainB),                 // 明文
            HostTestKit.RealEnvelope(0, 0x57, bad),                 // 认得出头、解不开
            HostTestKit.RealEnvelope(0, 902, notInSet));            // 不在集合里:不碰

        Assert.Equal(new[] { 561, 2, 2, 561, 9, 561, 0x57, 561, 902 }, frames.Select(f => (int)f.Header.PacketId).ToArray());
        Assert.Equal(plainA, frames[1].Payload);
        Assert.True(frames[1].WasCompressed);
        Assert.Equal(HostTestKit.ListPayload(), frames[2].Payload);      // 直接帧:会话解码时已解压
        Assert.Equal(plainB, frames[4].Payload);
        Assert.False(frames[4].WasCompressed);
        Assert.Equal(bad, frames[6].Payload);
        Assert.False(frames[6].WasCompressed);
        Assert.Equal(notInSet, frames[8].Payload);
        Assert.False(frames[8].WasCompressed);

        SessionEvent[] envelopes = EnvelopeEvents(session);
        Assert.Equal(4, envelopes.Length);
        Assert.Equal(new[] { false, false, true, false }, envelopes.Select(e => e.Note.Contains(FailureMark)).ToArray());
        Assert.Equal(1, MarkCount(session));
        Assert.Equal(new[] { 2, 9, 0x57, 902 }, InnerEvents(session).Select(e => e.PacketId).ToArray());
        Assert.All(InnerEvents(session), e => Assert.Equal("信封内层", e.Note));
        AssertExistingCountsUntouched(session, 1 + (4 * 2) + 1);     // 启动包、四个信封各两条、一个直接帧
    }

    /// <summary>
    /// 【913】汇总行(<see cref="OnlineNetLog.SessionDigest"/>)读到的计数与内层帧是解开的、原样保留的还是明文的无关:
    /// 事件数、收到数、信封数、拆开数、内层包号与 id2Inner 一模一样(失败标记不另起事件,也不改内层事件的备注)。
    /// </summary>
    [Fact]
    public async Task The_session_digest_reads_the_same_counts_for_an_inflated_a_kept_and_a_plain_inner_list_frame()
    {
        const string expected = "DNF_NET session events=3 received=3 envelopes=1 opened=1 unwrapFailed=0 unwrapReasons=- innerShort=0 innerIds=2 id2Direct=0 id2Inner=1";
        byte[] list = HostTestKit.ListPayload();

        foreach (byte[] payload in new[] { Zlib(list), DisguisedGarbage(), list })
        {
            (GameClientSession session, _) = await Receive(HostTestKit.RealEnvelope(0, 2, payload));
            Assert.Equal(expected, OnlineNetLog.SessionDigest(session.Events));
        }
    }

    /// <summary>
    /// 【914】zlib 流后面的 0x00 补齐(分组密码的补齐)照直接帧的口径容忍,仍解得开;补齐里夹了非零字节则不行(见【908】的 trailing-garbage)。
    /// </summary>
    [Fact]
    public async Task Zero_padding_after_the_zlib_stream_is_tolerated_like_for_direct_frames()
    {
        byte[] plain = SynthPlain(70);
        byte[] padded = Zlib(plain).Concat(new byte[5]).ToArray();

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope(0, 2, padded));

        Assert.Equal(plain, frames[1].Payload);
        Assert.True(frames[1].WasCompressed);
        Assert.Equal(0, MarkCount(session));
    }

    /// <summary>
    /// 【915】zlib 头的各种合法写法(压缩级别位 0、1、2、3 以及更小的窗口位数)都认得出、解得开:认头只看方法位与 FCHECK,不绑死 0x789C。
    /// </summary>
    [Theory]
    [InlineData(0x78, 0x01)]
    [InlineData(0x78, 0x5E)]
    [InlineData(0x78, 0x9C)]
    [InlineData(0x78, 0xDA)]
    [InlineData(0x68, 0x05)]
    [InlineData(0x58, 0x09)]
    [InlineData(0x48, 0x0D)]
    [InlineData(0x08, 0x1D)]
    public async Task Every_valid_zlib_header_spelling_is_recognized(int cmf, int flg)
    {
        Assert.Equal(8, cmf & 0x0F);
        Assert.Equal(0, ((cmf << 8) | flg) % 31);
        byte[] plain = SynthPlain(60);
        byte[] stream = WithHeader(Zlib(plain), cmf, flg);

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(HostTestKit.RealEnvelope(0, 2, stream));

        Assert.Equal(plain, frames[1].Payload);
        Assert.True(frames[1].WasCompressed);
        Assert.Equal(0, MarkCount(session));
    }

    /// <summary>
    /// 【916】只拆一层:信封里套信封(内层包号 561)照旧原样交付,不看作压缩帧,里面那层信封载荷不被碰。
    /// </summary>
    [Fact]
    public async Task A_nested_envelope_is_delivered_as_before_and_not_treated_as_a_compressed_frame()
    {
        byte[] nested = HostTestKit.RealEnvelope(0, 2, Zlib(SynthPlain()));     // 作为内层的一整帧:包号 561,载荷是另一个信封
        byte[] plain = AntibotTestSupport.Concat(AntibotTestSupport.AlgorithmPacket(8), nested);
        byte[] outer = DungeonEntryWire.Envelope(AntibotTestSupport.ValidEnvelope(16, AntibotTestSupport.SeqA, plain));

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(outer);

        Assert.Equal(new[] { 561, 561 }, frames.Select(f => (int)f.Header.PacketId).ToArray());
        Assert.False(frames[1].WasCompressed);
        Assert.Equal(0, MarkCount(session));
        AssertExistingCountsUntouched(session, 3);
    }

    /// <summary>
    /// 【917】内层不足一个 15 字节帧头(包号恰好是 2):照旧只在事件里记 "信封内层(帧头不足,未展开)",不交付内层帧,
    /// 也不会去解压、不记失败标记,更不会因为读不到载荷而出错。
    /// </summary>
    [Fact]
    public async Task An_inner_packet_shorter_than_a_frame_header_is_only_reported_in_the_event_as_before()
    {
        byte[] shortInner = AntibotTestSupport.InnerPacket(0, 2, 3, 9);          // 10 字节
        byte[] plain = AntibotTestSupport.Concat(AntibotTestSupport.AlgorithmPacket(8), shortInner);
        byte[] outer = DungeonEntryWire.Envelope(AntibotTestSupport.ValidEnvelope(16, AntibotTestSupport.SeqA, plain));

        (GameClientSession session, List<DecodedFrame> frames) = await Receive(outer);

        DecodedFrame only = Assert.Single(frames);
        Assert.Equal(561, only.Header.PacketId);
        SessionEvent note = Assert.Single(session.Events, e => e.Note.StartsWith("信封内层", StringComparison.Ordinal));
        Assert.Equal(2, note.PacketId);
        Assert.Contains("未展开", note.Note);
        Assert.Equal(0, MarkCount(session));
        AssertExistingCountsUntouched(session, 3);
    }
}
