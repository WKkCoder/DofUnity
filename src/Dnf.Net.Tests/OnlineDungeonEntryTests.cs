using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Failure = Dnf.Net.Session.OnlineDungeonFailure;
using Note = Dnf.Net.Session.OnlineDungeonFrameNote;
using Stage = Dnf.Net.Session.OnlineDungeonStage;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网进副本编排的合成测试。真会话(<see cref="GameClientSession"/>)接在内存管道上,由脚本化的"服务端"在收到客户端命令帧时
/// 回放合成帧,整条链 15→27→16→28→29 在进程内走完,不联网;帧层编码失败、发送时流已断这类真会话造不出的情形,
/// 用会话接缝(<see cref="IOnlineDungeonSession"/>)的假实现。信封解包一律用测试里的假委托(恒等或固定映射),没有任何解密。
/// 数据全是合成的:副本号 901 起、地图号 905 起、种子 90001 起,无关帧的包号也取 900 以上;只有 15、16、27、28、29、561 是协议规定的包号。
/// 下面反复用到的几个手算值:901 = 0x0385(小端字节 85 03)、906 = 0x038A(8A 03)、1000 = 0x03E8(E8 03)、4660 = 0x1234(34 12)。
/// </summary>
public class OnlineDungeonEntryTests
{
    private const PacketClass Noti = PacketClass.Noti;
    private const PacketClass Cmd = PacketClass.Cmd;
    private const ushort Dungeon = 901;

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Roomy = TimeSpan.FromSeconds(5);
    private static readonly byte[] Success8 = { 1, 0, 0, 0, 0, 0, 0, 0 };

    // ======== 组装与小工具 ========

    private static async Task<(GameClientSession Session, DungeonEntryPipe Pipe)> ReadySession()
    {
        var pipe = new DungeonEntryPipe();
        pipe.Push(DungeonEntryWire.BootstrapFrame());
        var session = new GameClientSession(pipe, s => new SliceXorCipher(s));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        return (session, pipe);
    }

    private static byte[][] Frames(params byte[][] frames)
    {
        return frames;
    }

    /// <summary>脚本:收到类 1 包号 15 的命令就回放 <paramref name="after15"/>,收到包号 16 就回放 <paramref name="after16"/>,别的不理。</summary>
    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Chain(byte[][] after15, byte[][] after16)
    {
        return frame =>
        {
            if (frame.Class != Cmd)
            {
                return null;
            }
            if (frame.Id == 15)
            {
                return after15;
            }
            if (frame.Id == 16)
            {
                return after16;
            }
            return null;
        };
    }

    /// <summary>
    /// 跑一次进副本,外加一道保险:被测实现被改坏(变异)后若不返回,8 秒后让测试失败,而不是挂住整个测试进程。
    /// 没指定的窗口给 5 秒:正常路径从不等满,只有期望超时的用例才显式给短窗口。
    /// </summary>
    private static Task<OnlineDungeonResult> Enter(OnlineDungeonEntry entry, byte mode = 3, TimeSpan? loading = null, TimeSpan? info = null,
        TimeSpan? map = null, Action<Stage> onStage = null, CancellationToken cancellationToken = default)
    {
        return GatewayTestSupport.Bounded(
            entry.EnterAsync(Dungeon, mode, 2, 0, loading ?? Roomy, info ?? Roomy, map ?? Roomy, onStage, cancellationToken), 8);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int milliseconds = 4000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds)
            {
                Assert.Fail("等待超时:" + what);
            }
            await Task.Delay(10);
        }
    }

    private static List<(PacketClass, int, bool, Note)> Rows(OnlineDungeonResult result)
    {
        return result.Frames.Select(f => (f.PacketClass, f.PacketId, f.FromEnvelope, f.Note)).ToList();
    }

    private static (PacketClass, int, bool, Note) Row(PacketClass cls, int id, Note note, bool inEnvelope = false)
    {
        return (cls, id, inEnvelope, note);
    }

    /// <summary>期望的字节在前,后面只许是会话加密补的 0x00。</summary>
    private static void AssertPaddedPrefix(byte[] expected, byte[] actual)
    {
        Assert.True(actual.Length >= expected.Length, "实际载荷比期望的短");
        Assert.Equal(expected, actual.Take(expected.Length).ToArray());
        Assert.All(actual.Skip(expected.Length), b => Assert.Equal((byte)0, b));
    }

    private static byte[] Noise(int length = 8)
    {
        return GatewayTestSupport.Pattern(length, 0x40);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(data, 0, data.Length);
        }
        return buffer.ToArray();
    }

    // ======== 合成载荷自检 ========

    /// <summary>
    /// 手拼的 28、29 载荷先过一道独立核对:长度按读序手算(28 = 13 + 2n 字节,29 的短形 = 12 字节),并且产品解析器读得下来、字段是写进去的值。
    /// 这样后面"解析结果对得上"的断言才有依据。
    /// </summary>
    [Fact]
    public void Synthetic_payload_builders_follow_the_documented_layouts()
    {
        byte[] info = DungeonEntryWire.Info28();
        Assert.Equal(13 + 2 * 2, info.Length);
        Assert.True(DungeonInfoParser.TryParse(info, out DungeonInfoNotice notice));
        Assert.Equal(901, notice.DungeonId);
        Assert.Equal(2, notice.Pairs.Count);
        Assert.Equal(0, notice.TrailingByteCount);

        byte[] map = DungeonEntryWire.Map29(90001);
        Assert.Equal(12, map.Length);
        Assert.True(StartMapParser.TryParse(map, out StartMapNotice start));
        Assert.Equal(90001, start.Seed);
        Assert.Equal(3, start.CellX);
        Assert.Equal(4, start.CellY);
        Assert.Equal(0, start.TrailingByteCount);

        Assert.False(DungeonInfoParser.TryParse(DungeonEntryWire.BrokenInfo28(), out _));
        Assert.False(StartMapParser.TryParse(DungeonEntryWire.BrokenMap29(), out _));

        // 27 的载荷长度是 48:槽 13 的块长 16 的整数倍,会话加密不补字节,直接收到的 27 与载荷逐字节一致
        Assert.Equal(0, DungeonEntryWire.Loading27().Length % SessionCipher.BlockSizeOf(27));
    }

    [Fact]
    public void Production_constants_match_the_documented_protocol()
    {
        // 协议规定的包号与长度写成字面量:产品常量被改动时,测试要能发现
        Assert.Equal(15, OnlineDungeonEntry.StartGamePacketId);
        Assert.Equal(16, OnlineDungeonEntry.SelectDungeonPacketId);
        Assert.Equal(27, OnlineDungeonEntry.LoadingNoticePacketId);
        Assert.Equal(561, OnlineDungeonEntry.ServerEnvelopePacketId);
        Assert.Equal(5, OnlineDungeonEntry.SelectDungeonPayloadLength);
        Assert.Equal(4, OnlineDungeonEntry.MaxMode);
        Assert.Equal(2, OnlineDungeonEntry.DefaultThirdByte);
        Assert.Equal(0, OnlineDungeonEntry.DefaultSafeByte);
        Assert.Equal(TimeSpan.FromSeconds(6), OnlineDungeonEntry.DefaultLoadingNoticeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), OnlineDungeonEntry.DefaultDungeonInfoTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), OnlineDungeonEntry.DefaultStartMapTimeout);
        Assert.Equal(28, DungeonInfoParser.PacketId);
        Assert.Equal(29, StartMapParser.PacketId);
    }

    // ======== 整条链 ========

    /// <summary>15 → 27 → 16 → 28 → 29 全走通:客户端只发两帧(15 空载荷、16 五字节),收到的三个通知全部入账,帧日志按到达顺序。</summary>
    [Fact]
    public async Task Full_chain_sends_15_then_16_and_collects_27_28_and_29_in_order()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(905, Noise()), DungeonEntryWire.Notice(27, p27)),
            Frames(DungeonEntryWire.Reply(16, Success8), DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()),
                DungeonEntryWire.Notice(911, Noise()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.True(result.Entered);
        Assert.Equal(Stage.Done, result.Stage);
        Assert.Equal(Failure.None, result.Failure);
        Assert.Null(result.SendStatus);
        Assert.Null(result.RejectedPacketId);
        Assert.Null(result.RejectionCode);
        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(2, result.DungeonInfo.Pairs.Count);
        Assert.Equal(90001, result.StartMap.Seed);
        Assert.Equal(3, result.StartMap.CellX);
        Assert.Equal(0, result.UnopenedEnvelopeCount);
        Assert.Equal(0, result.OutOfOrderCount);
        Assert.Equal(0, result.DuplicateCount);

        IReadOnlyList<DungeonEntryClientFrame> sent = pipe.ClientFrames;
        Assert.Equal(2, sent.Count);
        Assert.Equal(Cmd, sent[0].Class);
        Assert.Equal(15, sent[0].Id);
        Assert.Equal(FrameLayout.ClientToServer.HeaderLength, sent[0].WireLength);   // 空载荷:整帧只有 13 字节头
        Assert.Empty(sent[0].Plain);
        Assert.Equal(Cmd, sent[1].Class);
        Assert.Equal(16, sent[1].Id);
        Assert.Equal(FrameLayout.ClientToServer.HeaderLength + SessionCipher.BlockSizeOf(16), sent[1].WireLength);
        AssertPaddedPrefix(new byte[] { 0x85, 0x03, 0x03, 0x02, 0x00 }, sent[1].Plain);   // 901 = 0x0385,模式 3,第三字节 2,安全字节 0

        Assert.Equal(
            new[]
            {
                Row(Noti, 905, Note.None),
                Row(Noti, 27, Note.Accepted),
                Row(Cmd, 16, Note.Acknowledged),
                Row(Noti, 28, Note.Accepted),
                Row(Noti, 911, Note.None),
                Row(Noti, 29, Note.Accepted),
            },
            Rows(result));
        Assert.Equal(
            new[] { Stage.WaitLoadingNotice, Stage.WaitLoadingNotice, Stage.WaitDungeonInfo, Stage.WaitDungeonInfo, Stage.WaitStartMap, Stage.WaitStartMap },
            result.Frames.Select(f => f.Stage).ToArray());
    }

    /// <summary>每进入一个阶段回调一次,顺序固定,终局不回调。</summary>
    [Fact]
    public async Task The_stage_callback_reports_every_stage_in_order()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));
        var stages = new List<Stage>();

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), onStage: stages.Add);

        Assert.True(result.Entered);
        Assert.Equal(
            new[] { Stage.SendStartGame, Stage.WaitLoadingNotice, Stage.SendSelectDungeon, Stage.WaitDungeonInfo, Stage.WaitStartMap },
            stages.ToArray());
    }

    /// <summary>三个通知挤在同一次读到的字节里也一帧一帧按阶段处理:28、29 不会在等 27 的阶段被一口气吞掉。</summary>
    [Fact]
    public async Task Frames_arriving_in_one_chunk_are_handled_one_at_a_time_by_stage()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        byte[] burst = DungeonEntryWire.Join(
            DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()),
            DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()),
            DungeonEntryWire.Notice(29, DungeonEntryWire.Map29()));
        pipe.Script = Chain(Frames(burst), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.True(result.Entered);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(new ushort[] { 15, 16 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        Assert.Equal(0, result.OutOfOrderCount);
    }

    /// <summary>编排对象不存状态:同一个会话上连着跑两次,各自走完整条链。</summary>
    [Fact]
    public async Task The_same_entry_object_can_run_twice_on_one_session()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));
        var entry = new OnlineDungeonEntry(session);

        OnlineDungeonResult first = await Enter(entry);
        OnlineDungeonResult second = await Enter(entry);

        Assert.True(first.Entered);
        Assert.True(second.Entered);
        Assert.NotSame(first, second);
        Assert.Equal(new ushort[] { 15, 16, 15, 16 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    // ======== 选副本载荷 ========

    /// <summary>
    /// 载荷 = u16 副本号小端 + 模式 + 第三字节 + 安全字节。期望的十六进制全是手算的(见类说明);
    /// 每个用例的副本号高低字节都不相同,小端写成大端会被发现。
    /// </summary>
    [Theory]
    [InlineData(901, 0, 2, 0, "8503000200")]
    [InlineData(1000, 4, 2, 0, "E803040200")]
    [InlineData(4660, 2, 7, 9, "3412020709")]
    [InlineData(65535, 1, 0, 255, "FFFF0100FF")]
    [InlineData(256, 3, 2, 0, "0001030200")]
    [InlineData(906, 4, 255, 1, "8A0304FF01")]
    public async Task Select_dungeon_payload_is_little_endian_id_then_mode_third_and_safe_byte(int dungeonId, int mode, int third, int safe, string expectedHex)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())), Frames());

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            new OnlineDungeonEntry(session).EnterAsync((ushort)dungeonId, (byte)mode, (byte)third, (byte)safe, Roomy, Quick, Quick), 8);

        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);     // 没有 28:停在等 28,选副本已经发出
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(2, pipe.ClientFrames.Count);
        byte[] plain = pipe.ClientFrames[1].Plain;
        Assert.Equal(16, pipe.ClientFrames[1].Id);
        Assert.Equal(expectedHex, Convert.ToHexString(plain.Take(5).ToArray()));
        Assert.All(plain.Skip(5), b => Assert.Equal((byte)0, b));
    }

    /// <summary>不给第三字节与安全字节时,分别是 2 与 0;模式 4 是合法上界。</summary>
    [Fact]
    public async Task Third_byte_defaults_to_2_and_safe_byte_to_0()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())), Frames());

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            new OnlineDungeonEntry(session).EnterAsync(Dungeon, 4, dungeonInfoTimeout: Quick), 8);

        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        AssertPaddedPrefix(new byte[] { 0x85, 0x03, 0x04, 0x02, 0x00 }, pipe.ClientFrames[1].Plain);
    }

    // ======== 顺序纪律:27 之前不发 16 ========

    /// <summary>27 没到,选副本命令就一直扣着;无关帧不放行;27 一到,16 紧跟着发出。</summary>
    [Fact]
    public async Task Select_dungeon_is_held_back_until_27_arrives_and_goes_out_right_after_it()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        Task<OnlineDungeonResult> run = Enter(new OnlineDungeonEntry(session));

        await WaitUntil(() => pipe.ClientFrames.Count == 1, "开始游戏命令发出");
        await Task.Delay(400);
        Assert.Single(pipe.ClientFrames);
        Assert.False(run.IsCompleted);

        pipe.Push(DungeonEntryWire.Notice(905, Noise()));
        await Task.Delay(150);
        Assert.Single(pipe.ClientFrames);

        pipe.Push(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()));
        await WaitUntil(() => pipe.ClientFrames.Count == 2, "27 到了之后选副本命令发出");
        Assert.Equal(16, pipe.ClientFrames[1].Id);

        pipe.Push(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()));
        pipe.Push(DungeonEntryWire.Notice(29, DungeonEntryWire.Map29()));
        OnlineDungeonResult result = await run;
        Assert.True(result.Entered);
    }

    /// <summary>等 27 超时:停在等 27,16 从没发出,要等够窗口才算超时,没有任何通知入账。</summary>
    [Fact]
    public async Task Waiting_for_27_times_out_and_16_is_never_sent()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        var entry = new OnlineDungeonEntry(session);

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(entry, loading: Quick);
        watch.Stop();

        Assert.False(result.Entered);
        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Null(result.LoadingNoticePayload);
        Assert.Null(result.DungeonInfo);
        Assert.Null(result.StartMap);
        Assert.Empty(result.Frames);
        ClientFrameShape.AssertOnlyStartGame(pipe);
        Assert.InRange(watch.ElapsedMilliseconds, 180L, 3000L);     // 等够了窗口(留出计时误差),又没有拖到默认的 6 秒
    }

    /// <summary>28 或 29 先于 27 到,不能当作 27:选副本不放行,两个都记乱序,最终等 27 超时。</summary>
    [Fact]
    public async Task Notices_28_and_29_cannot_stand_in_for_27()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29()),
                DungeonEntryWire.Notice(905, Noise())),
            Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), loading: Quick);

        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(2, result.OutOfOrderCount);
        Assert.Null(result.DungeonInfo);
        Assert.Null(result.StartMap);
        ClientFrameShape.AssertOnlyStartGame(pipe);
        Assert.Equal(
            new[] { Row(Noti, 28, Note.OutOfOrder), Row(Noti, 29, Note.OutOfOrder), Row(Noti, 905, Note.None) },
            Rows(result));
    }

    /// <summary>三个窗口各算各的:等 28 的窗口从发出 16 起算,不受等 27 的长窗口影响。</summary>
    [Fact]
    public async Task The_dungeon_info_window_is_its_own()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())), Frames());

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), loading: TimeSpan.FromSeconds(6), info: Quick, map: TimeSpan.FromSeconds(6));
        watch.Stop();

        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.InRange(watch.ElapsedMilliseconds, 180L, 3000L);
        Assert.Equal(2, pipe.ClientFrames.Count);
    }

    /// <summary>等 29 的窗口从收到 28 起算,同样独立;停在等 29 时,27 与 28 的成果都还在结果里。</summary>
    [Fact]
    public async Task A_missing_29_times_out_in_wait_start_map_and_keeps_the_partial_results()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, p27)), Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28())));

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), loading: TimeSpan.FromSeconds(6), info: TimeSpan.FromSeconds(6), map: Quick);
        watch.Stop();

        Assert.False(result.Entered);
        Assert.Equal(Stage.WaitStartMap, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Null(result.StartMap);
        Assert.InRange(watch.ElapsedMilliseconds, 180L, 3000L);
    }

    /// <summary>
    /// 窗口在处理上一帧的时候就用完了(这里是解包委托慢了一拍,比等 27 的窗口还久):会话里已经排着的 27 仍然要被"最后看一眼"收下,
    /// 到点前已经到达的帧不能因为处理得慢而算成超时。
    /// </summary>
    [Fact]
    public async Task A_frame_already_queued_when_the_window_runs_out_still_counts()
    {
        var book = new DungeonEntryEnvelopeBook();
        byte[] sealedNoise = book.Seal(DungeonEntryWire.Inner(0, 905, Noise()), 1);
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Envelope(sealedNoise), DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));
        var entry = new OnlineDungeonEntry(session, payload =>
        {
            Thread.Sleep(450);                                  // 比下面的等 27 窗口(300 毫秒)还久
            return book.Unwrap(payload);
        });

        OnlineDungeonResult result = await Enter(entry, loading: Quick);

        Assert.True(result.Entered);
        Assert.Equal(
            new[]
            {
                Row(Noti, 561, Note.EnvelopeOpened),
                Row(Noti, 905, Note.None, inEnvelope: true),
                Row(Noti, 27, Note.Accepted),
                Row(Noti, 28, Note.Accepted),
                Row(Noti, 29, Note.Accepted),
            },
            Rows(result));
    }

    // ======== 顺序纪律:28 先于 29 ========

    /// <summary>29 先到而 28 一直没来:29 记乱序并忽略,停在等 28(而不是误判成功),没有起始地图。</summary>
    [Fact]
    public async Task A_29_ahead_of_28_is_out_of_order_and_the_missing_28_ends_in_a_wait_dungeon_info_timeout()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, p27)), Frames(DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), info: Quick);

        Assert.False(result.Entered);
        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(1, result.OutOfOrderCount);
        Assert.Null(result.DungeonInfo);
        Assert.Null(result.StartMap);
        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(
            new[] { Row(Noti, 27, Note.Accepted), Row(Noti, 29, Note.OutOfOrder) },
            Rows(result));
    }

    /// <summary>
    /// 早到的 29 是"忽略,不缓存":28 之后到的另一个 29 才是入账的那个(种子不同,缓存了就会拿到第一个)。
    /// 帧日志里依次是乱序的 29、被接受的 28、被接受的 29。
    /// </summary>
    [Fact]
    public async Task An_early_29_is_ignored_not_cached_and_the_29_after_28_is_the_one_kept()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Notice(29, DungeonEntryWire.Map29(90001)), DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()),
                DungeonEntryWire.Notice(29, DungeonEntryWire.Map29(90002))));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.True(result.Entered);
        Assert.Equal(90002, result.StartMap.Seed);
        Assert.Equal(1, result.OutOfOrderCount);
        Assert.Equal(
            new[]
            {
                Row(Noti, 27, Note.Accepted),
                Row(Noti, 29, Note.OutOfOrder),
                Row(Noti, 28, Note.Accepted),
                Row(Noti, 29, Note.Accepted),
            },
            Rows(result));
    }

    /// <summary>27、28 只认第一次:后到的重复(也包括别的阶段里再来的 27)记 Duplicate 并忽略,结果里是第一次的内容。29 收下就进终局,同批重复的 29 见下面的整批测试。</summary>
    [Fact]
    public async Task Duplicate_27_and_28_are_recorded_and_the_first_of_each_wins()
    {
        byte[] first27 = DungeonEntryWire.Loading27();
        byte[] second27 = GatewayTestSupport.Pattern(48, 0x30);
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, first27), DungeonEntryWire.Notice(27, second27)),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28(901)), DungeonEntryWire.Notice(28, DungeonEntryWire.Info28(902)),
                DungeonEntryWire.Notice(27, second27), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29(90001))));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.True(result.Entered);
        Assert.Equal(first27, result.LoadingNoticePayload);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(3, result.DuplicateCount);
        Assert.Equal(
            new[]
            {
                Row(Noti, 27, Note.Accepted),
                Row(Noti, 27, Note.Duplicate),
                Row(Noti, 28, Note.Accepted),
                Row(Noti, 28, Note.Duplicate),
                Row(Noti, 27, Note.Duplicate),
                Row(Noti, 29, Note.Accepted),
            },
            Rows(result));
    }

    /// <summary>
    /// 端口一次交出一整批帧(真会话每次只交一帧,别的实现不一定):多出的帧一帧不丢,留到后面的阶段按序处理,
    /// 每一帧按处理它那一刻的阶段对待——所以与 28 同批的 29 在 28 被收下之后仍然算数,批里排在 28 前面的 29 仍是乱序。
    /// </summary>
    [Fact]
    public async Task Frames_returned_together_by_a_port_are_kept_and_judged_at_the_stage_they_are_handled()
    {
        byte[] first27 = DungeonEntryWire.Loading27();
        var port = new DungeonEntryFakePort { ReturnWholeInbox = true };
        port.OnSent = (index, id) => index == 0
            ? new[]
            {
                DungeonEntryWire.Decoded(0, 27, first27),
                DungeonEntryWire.Decoded(0, 27, GatewayTestSupport.Pattern(48, 0x30)),
            }
            : new[]
            {
                DungeonEntryWire.Decoded(0, 29, DungeonEntryWire.Map29(90002)),
                DungeonEntryWire.Decoded(0, 28, DungeonEntryWire.Info28(901)),
                DungeonEntryWire.Decoded(0, 28, DungeonEntryWire.Info28(902)),
                DungeonEntryWire.Decoded(0, 29, DungeonEntryWire.Map29(90003)),
                DungeonEntryWire.Decoded(0, 29, DungeonEntryWire.Map29(90004)),
            };

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port));

        Assert.True(result.Entered);
        Assert.Equal(first27, result.LoadingNoticePayload);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(90003, result.StartMap.Seed);
        Assert.Equal(1, result.OutOfOrderCount);
        Assert.Equal(2, result.DuplicateCount);
        Assert.Equal(
            new[]
            {
                Row(Noti, 27, Note.Accepted),
                Row(Noti, 27, Note.Duplicate),
                Row(Noti, 29, Note.OutOfOrder),
                Row(Noti, 28, Note.Accepted),
                Row(Noti, 28, Note.Duplicate),
                Row(Noti, 29, Note.Accepted),        // 与 28 同批到的 29:处理它时已是等 29 的阶段
            },                                        // 最后一个 29(90004)收下 29 之后不再读,不入日志
            Rows(result));
        Assert.Equal(
            new[] { Stage.WaitLoadingNotice, Stage.WaitDungeonInfo, Stage.WaitDungeonInfo, Stage.WaitDungeonInfo, Stage.WaitStartMap, Stage.WaitStartMap },
            result.Frames.Select(f => f.Stage).ToArray());
    }

    /// <summary>27 的原始载荷是收下时拷贝的:之后改动原始帧的缓冲,结果里的内容不变。</summary>
    [Fact]
    public async Task The_raw_27_payload_is_a_copy_not_an_alias_of_the_frame_buffer()
    {
        DecodedFrame frame27 = DungeonEntryWire.Decoded(0, 27, new byte[] { 1, 2, 3, 4 });
        var port = new DungeonEntryFakePort();
        port.Enqueue(frame27);

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port), info: Quick);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.LoadingNoticePayload);
        frame27.Payload[0] = 0xFF;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.LoadingNoticePayload);
    }

    // ======== 无关帧与日志格式 ========

    /// <summary>
    /// 用假会话把每帧的载荷长度钉死(没有会话加密补字节):无关帧只记录,不影响流程;
    /// 日志每行只有类、包号、载荷长度、信封标记与处理结果,不含载荷内容。
    /// </summary>
    [Fact]
    public async Task Unrelated_frames_are_only_logged_and_the_log_has_no_payload_content()
    {
        byte[] marked27 = Encoding.ASCII.GetBytes("MARK27-" + new string('x', 41));
        var port = new DungeonEntryFakePort();
        port.Enqueue(DungeonEntryWire.Decoded(0, 0, Array.Empty<byte>()), DungeonEntryWire.Decoded(0, 905, new byte[16]), DungeonEntryWire.Decoded(1, 990, new byte[3]));
        port.OnSent = (index, id) => index == 0
            ? new[] { DungeonEntryWire.Decoded(0, 27, marked27) }
            : new[]
            {
                DungeonEntryWire.Decoded(1, 16, Success8),
                DungeonEntryWire.Decoded(0, 28, DungeonEntryWire.Info28()),
                DungeonEntryWire.Decoded(0, 29, DungeonEntryWire.Map29()),
            };

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port));

        Assert.True(result.Entered);
        string[] lines = result.Frames.Select(f => f.ToString()).ToArray();
        Assert.Equal(
            new[] { "NOTI/0(0)", "NOTI/905(16)", "CMD/990(3)", "NOTI/27(48):Accepted", "CMD/16(8):Acknowledged", "NOTI/28(17):Accepted", "NOTI/29(12):Accepted" },
            lines);
        Assert.DoesNotContain("MARK27", string.Join(" ", lines) + result);
        Assert.Equal(marked27, result.LoadingNoticePayload);
        Assert.Equal(new ushort[] { 15, 16 }, port.Sent.Select(s => s.Id).ToArray());
    }

    // ======== 信封:经假委托解包 ========

    /// <summary>
    /// 恒等委托:561 帧的载荷本身就是内层帧。27、28、29 都封在信封里也能走完整条链;委托收到的是信封载荷(不含外层 15 字节头);
    /// 日志里信封帧与内层帧各一条,内层的 FromEnvelope 为真。
    /// </summary>
    [Fact]
    public async Task Envelope_frames_go_through_the_unwrap_delegate_and_the_inner_frames_drive_the_chain()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        byte[] inner27 = DungeonEntryWire.Inner(0, 27, p27);
        byte[] inner28 = DungeonEntryWire.Inner(0, 28, DungeonEntryWire.Info28());
        byte[] inner29 = DungeonEntryWire.Inner(0, 29, DungeonEntryWire.Map29());
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Envelope(inner27)), Frames(DungeonEntryWire.Envelope(inner28), DungeonEntryWire.Envelope(inner29)));
        var seen = new List<byte[]>();

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, payload =>
        {
            seen.Add(payload);
            return payload;
        }));

        Assert.True(result.Entered);
        Assert.Equal(p27, result.LoadingNoticePayload);        // 内层帧的载荷,没有补字节
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(90001, result.StartMap.Seed);
        Assert.Equal(new[] { inner27, inner28, inner29 }, seen);   // 委托拿到的是 561 帧头之后的字节
        Assert.Equal(0, result.UnopenedEnvelopeCount);
        Assert.Equal(
            new[]
            {
                Row(Noti, 561, Note.EnvelopeOpened),
                Row(Noti, 27, Note.Accepted, inEnvelope: true),
                Row(Noti, 561, Note.EnvelopeOpened),
                Row(Noti, 28, Note.Accepted, inEnvelope: true),
                Row(Noti, 561, Note.EnvelopeOpened),
                Row(Noti, 29, Note.Accepted, inEnvelope: true),
            },
            Rows(result));
        Assert.Equal(p27.Length, result.Frames[1].PayloadLength);
        Assert.Equal(inner27.Length, result.Frames[0].PayloadLength);
        Assert.Equal(new ushort[] { 15, 16 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    /// <summary>固定映射委托:信封载荷是看不出内容的不透明字节,由测试里的映射表换成内层帧;表里没有的返回 null,算没解开,继续等。</summary>
    [Fact]
    public async Task A_fixed_mapping_delegate_opens_opaque_envelopes_and_unknown_ones_stay_unopened()
    {
        var book = new DungeonEntryEnvelopeBook();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(
                DungeonEntryWire.Envelope(GatewayTestSupport.Pattern(33, 0xE0, 3)),      // 表里没有
                DungeonEntryWire.Envelope(book.Seal(DungeonEntryWire.Inner(0, 27, DungeonEntryWire.Loading27()), 1))),
            Frames(
                DungeonEntryWire.Envelope(book.Seal(DungeonEntryWire.Inner(0, 28, DungeonEntryWire.Info28()), 2)),
                DungeonEntryWire.Envelope(book.Seal(DungeonEntryWire.Inner(0, 29, DungeonEntryWire.Map29()), 3))));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, book.Unwrap));

        Assert.True(result.Entered);
        Assert.Equal(1, result.UnopenedEnvelopeCount);
        Assert.Equal(4, book.Seen.Count);
        Assert.Equal(Row(Noti, 561, Note.EnvelopeUnopened), Rows(result)[0]);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
    }

    /// <summary>没注入委托(null):561 帧记成没解开,27 封在里面就永远等不到,16 不发,停在等 27 超时。</summary>
    [Fact]
    public async Task A_null_delegate_leaves_envelopes_unopened_and_a_sealed_27_never_unlocks_16()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        byte[] sealed27 = DungeonEntryWire.Envelope(DungeonEntryWire.Inner(0, 27, DungeonEntryWire.Loading27()));
        pipe.Script = Chain(Frames(sealed27), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), loading: Quick);

        Assert.False(result.Entered);
        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Null(result.LoadingNoticePayload);
        Assert.Equal(1, result.UnopenedEnvelopeCount);
        Assert.Equal(new[] { Row(Noti, 561, Note.EnvelopeUnopened) }, Rows(result));
        ClientFrameShape.AssertOnlyStartGame(pipe);
    }

    /// <summary>27 直接到、28 与 29 封在信封里:没委托就解不开,停在等 28(16 已发)。</summary>
    [Fact]
    public async Task A_null_delegate_also_blocks_sealed_28_and_29_after_a_direct_27()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Envelope(DungeonEntryWire.Inner(0, 28, DungeonEntryWire.Info28())),
                DungeonEntryWire.Envelope(DungeonEntryWire.Inner(0, 29, DungeonEntryWire.Map29()))));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), info: Quick);

        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(2, result.UnopenedEnvelopeCount);
        Assert.Null(result.DungeonInfo);
        Assert.Null(result.StartMap);
        Assert.NotNull(result.LoadingNoticePayload);
        Assert.Equal(new ushort[] { 15, 16 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    /// <summary>委托返回 null 或抛异常都按没解开处理(异常不外泄),继续等后面的信封。</summary>
    [Fact]
    public async Task A_delegate_that_returns_null_or_throws_counts_as_unopened_and_the_wait_goes_on()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        byte[] envelope = DungeonEntryWire.Envelope(DungeonEntryWire.Inner(0, 27, p27));
        pipe.Script = Chain(Frames(envelope, envelope, envelope), Frames());
        int calls = 0;

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, payload =>
        {
            calls++;
            if (calls == 1)
            {
                return null;
            }
            if (calls == 2)
            {
                throw new InvalidOperationException("合成的解包故障");
            }
            return payload;
        }), info: Quick);

        Assert.Equal(3, calls);
        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(2, result.UnopenedEnvelopeCount);
        Assert.Equal(
            new[]
            {
                Row(Noti, 561, Note.EnvelopeUnopened),
                Row(Noti, 561, Note.EnvelopeFaulted),
                Row(Noti, 561, Note.EnvelopeOpened),
                Row(Noti, 27, Note.Accepted, inEnvelope: true),
            },
            Rows(result));
        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);   // 27 入账、16 发出,之后没有 28
    }

    /// <summary>内层帧头不成样子的(空、短、整帧长度小于头长、大于给回的字节数)一律记 InnerMalformed 并忽略,不抛,后面的好帧照常入账。</summary>
    [Fact]
    public async Task Malformed_inner_frames_are_recorded_and_ignored()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        byte[] good = DungeonEntryWire.Inner(0, 27, p27);
        byte[] lengthBelowHeader = DungeonEntryWire.Inner(0, 27, p27);
        DungeonEntryWire.PutU32(lengthBelowHeader, 3, 14);
        byte[] lengthBeyondData = DungeonEntryWire.Inner(0, 27, p27);
        DungeonEntryWire.PutU32(lengthBeyondData, 3, (uint)lengthBeyondData.Length + 1);
        var book = new DungeonEntryEnvelopeBook();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(
                DungeonEntryWire.Envelope(book.Seal(Array.Empty<byte>(), 1)),
                DungeonEntryWire.Envelope(book.Seal(new byte[10], 2)),
                DungeonEntryWire.Envelope(book.Seal(lengthBelowHeader, 3)),
                DungeonEntryWire.Envelope(book.Seal(lengthBeyondData, 4)),
                DungeonEntryWire.Envelope(book.Seal(good, 5))),
            Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, book.Unwrap), info: Quick);

        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(
            new[]
            {
                Row(Noti, 561, Note.EnvelopeOpened), Row(Noti, -1, Note.InnerMalformed, inEnvelope: true),
                Row(Noti, 561, Note.EnvelopeOpened), Row(Noti, -1, Note.InnerMalformed, inEnvelope: true),
                Row(Noti, 561, Note.EnvelopeOpened), Row(Noti, 27, Note.InnerMalformed, inEnvelope: true),
                Row(Noti, 561, Note.EnvelopeOpened), Row(Noti, 27, Note.InnerMalformed, inEnvelope: true),
                Row(Noti, 561, Note.EnvelopeOpened), Row(Noti, 27, Note.Accepted, inEnvelope: true),
            },
            Rows(result));
        Assert.Equal(new[] { 0, 10, lengthBelowHeader.Length, lengthBeyondData.Length }, result.Frames.Where(f => f.Note == Note.InnerMalformed).Select(f => f.PayloadLength).ToArray());
    }

    /// <summary>内层帧比头里的整帧长度多出的尾巴(分组密码补齐)被丢掉,载荷只取到整帧长度为止。</summary>
    [Fact]
    public async Task Bytes_after_the_declared_inner_length_are_not_part_of_the_payload()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        byte[] padded = DungeonEntryWire.Join(DungeonEntryWire.Inner(0, 27, p27), new byte[] { 9, 9, 9 });
        var book = new DungeonEntryEnvelopeBook();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Envelope(book.Seal(padded, 1))), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, book.Unwrap), info: Quick);

        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(p27.Length, result.Frames[1].PayloadLength);
    }

    /// <summary>
    /// 压缩的内层帧(包号 2、9、0x57,与既有解码器认的压缩包号一致)不解压也不解析:日志标 InnerCompressed,载荷长度是压缩态的;
    /// 同样大小的非压缩包号(8)只是无关帧。
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(9, true)]
    [InlineData(0x57, true)]
    [InlineData(8, false)]
    [InlineData(0x58, false)]
    public async Task Compressed_inner_frames_are_neither_inflated_nor_parsed(int innerId, bool compressed)
    {
        byte[] original = new byte[200];
        byte[] zipped = Zlib(original);
        Assert.True(zipped.Length < original.Length);
        var book = new DungeonEntryEnvelopeBook();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Envelope(book.Seal(DungeonEntryWire.Inner(0, (ushort)innerId, zipped), 1))), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, book.Unwrap), loading: Quick);

        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(2, result.Frames.Count);
        OnlineDungeonFrame inner = result.Frames[1];
        Assert.Equal(innerId, inner.PacketId);
        Assert.True(inner.FromEnvelope);
        Assert.Equal(compressed ? Note.InnerCompressed : Note.None, inner.Note);
        Assert.Equal(zipped.Length, inner.PayloadLength);     // 压缩态的长度:没有解压
    }

    /// <summary>信封里再套信封不递归拆:内层的 561 记没解开,委托只被叫一次(外层那次)。</summary>
    [Fact]
    public async Task A_nested_envelope_is_not_opened_again()
    {
        var book = new DungeonEntryEnvelopeBook();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Envelope(book.Seal(DungeonEntryWire.Inner(0, 561, Noise(30)), 1))), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, book.Unwrap), loading: Quick);

        Assert.Single(book.Seen);
        Assert.Equal(1, result.UnopenedEnvelopeCount);
        Assert.Equal(
            new[] { Row(Noti, 561, Note.EnvelopeOpened), Row(Noti, 561, Note.EnvelopeUnopened, inEnvelope: true) },
            Rows(result));
    }

    /// <summary>信封里解出来的内层帧与直接收到的走同一条处理路径:内层是类 1 的命令回包、又是失败形状,照样算被拒,记录里带信封标记。</summary>
    [Fact]
    public async Task An_inner_command_reply_in_an_envelope_is_judged_like_a_direct_one()
    {
        var book = new DungeonEntryEnvelopeBook();
        byte[] sealedReply = book.Seal(DungeonEntryWire.Inner(1, 15, new byte[] { 0, 0x23, 0, 0 }), 1);
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Envelope(sealedReply)), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session, book.Unwrap));

        Assert.False(result.Entered);
        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Rejected, result.Failure);
        Assert.Equal((ushort)15, result.RejectedPacketId);
        Assert.Equal((byte)0x23, result.RejectionCode);
        Assert.Equal(
            new[] { Row(Noti, 561, Note.EnvelopeOpened), Row(Cmd, 15, Note.Rejected, inEnvelope: true) },
            Rows(result));
        ClientFrameShape.AssertOnlyStartGame(pipe);
    }

    // ======== 被拒 ========

    /// <summary>"开始游戏"的回包是失败形状([0]=0、[1]=码):被拒,16 不发,码入结果。</summary>
    [Fact]
    public async Task A_failure_shaped_reply_to_start_game_is_a_rejection_and_16_is_never_sent()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Reply(15, new byte[] { 0, 0x11 })), Frames());

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));
        watch.Stop();

        Assert.False(result.Entered);
        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Rejected, result.Failure);
        Assert.Equal((ushort)15, result.RejectedPacketId);
        Assert.Equal((byte)0x11, result.RejectionCode);
        Assert.Null(result.LoadingNoticePayload);
        Assert.Equal(new[] { Row(Cmd, 15, Note.Rejected) }, Rows(result));
        ClientFrameShape.AssertOnlyStartGame(pipe);
        Assert.True(watch.ElapsedMilliseconds < 3000, "被拒应当立即返回,而不是等满窗口");
    }

    /// <summary>"选副本"的回包是失败形状:被拒,停在等 28,27 的载荷仍在。</summary>
    [Fact]
    public async Task A_failure_shaped_reply_to_select_dungeon_is_a_rejection()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, p27)), Frames(DungeonEntryWire.Reply(16, new byte[] { 0, 7 })));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(Failure.Rejected, result.Failure);
        Assert.Equal((ushort)16, result.RejectedPacketId);
        Assert.Equal((byte)7, result.RejectionCode);
        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(Row(Cmd, 16, Note.Rejected), Rows(result)[1]);
    }

    /// <summary>28 之后迟到的"选副本"失败回包同样算被拒(等 29 的阶段里被等的仍是 16 的回包),28 的成果保留。</summary>
    [Fact]
    public async Task A_failure_shaped_reply_to_16_is_still_a_rejection_while_waiting_for_the_start_map()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Reply(16, new byte[] { 0, 9 })));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.Equal(Stage.WaitStartMap, result.Stage);
        Assert.Equal(Failure.Rejected, result.Failure);
        Assert.Equal((ushort)16, result.RejectedPacketId);
        Assert.Equal((byte)9, result.RejectionCode);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
    }

    /// <summary>接受阶段的拒绝帧(类 1、包号 1、整帧 21 字节的明文帧)也是被拒:来源记 1,码取载荷第二字节。</summary>
    [Fact]
    public async Task The_refusal_frame_is_a_rejection_with_its_code()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Refusal()), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.Rejected, result.Failure);
        Assert.Equal((ushort)1, result.RejectedPacketId);
        Assert.Equal((byte)4, result.RejectionCode);
        Assert.Equal(new[] { Row(Cmd, 1, Note.Rejected) }, Rows(result));
        ClientFrameShape.AssertOnlyStartGame(pipe);
    }

    /// <summary>成功形状、空载荷、孤零零的 [0]、首字节不是 0 的回包都不是拒绝:记 Acknowledged,继续等(最终等 28 超时)。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("0105")]
    [InlineData("0200")]
    public async Task Success_shaped_or_verdictless_replies_are_acknowledged_not_rejected(string replyHex)
    {
        byte[] reply = replyHex.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(replyHex);
        var port = new DungeonEntryFakePort();
        port.OnSent = (index, id) => index == 0
            ? new[] { DungeonEntryWire.Decoded(1, 15, reply), DungeonEntryWire.Decoded(0, 27, new byte[4]) }
            : Array.Empty<DecodedFrame>();

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port), info: Quick);

        Assert.Equal(Failure.Timeout, result.Failure);
        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(Row(Cmd, 15, Note.Acknowledged), Rows(result)[0]);
        Assert.Null(result.RejectedPacketId);
    }

    /// <summary>不是当前在等的命令的回包只记录:发 16 之前来的 16 回包、27 之后迟到的 15 回包,哪怕是失败形状也不判被拒。</summary>
    [Fact]
    public async Task A_reply_to_a_command_that_is_not_being_awaited_is_only_logged()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Reply(16, new byte[] { 0, 5 }), DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()),
                DungeonEntryWire.Reply(15, new byte[] { 0, 9 })),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.True(result.Entered);
        Assert.Equal(
            new[]
            {
                Row(Cmd, 16, Note.None),
                Row(Noti, 27, Note.Accepted),
                Row(Cmd, 15, Note.None),
                Row(Noti, 28, Note.Accepted),
                Row(Noti, 29, Note.Accepted),
            },
            Rows(result));
    }

    // ======== 通知读不下来 ========

    /// <summary>28 载荷读不下来:立即失败(不是傻等到超时),停在等 28。</summary>
    [Fact]
    public async Task A_malformed_28_fails_at_once_instead_of_waiting_for_the_window()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())), Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.BrokenInfo28())));

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));
        watch.Stop();

        Assert.Equal(Stage.WaitDungeonInfo, result.Stage);
        Assert.Equal(Failure.NoticeMalformed, result.Failure);
        Assert.Null(result.DungeonInfo);
        Assert.Equal(Row(Noti, 28, Note.NoticeMalformed), Rows(result)[1]);
        Assert.True(watch.ElapsedMilliseconds < 3000);
    }

    /// <summary>29 载荷读不下来:立即失败,停在等 29,28 的成果保留。</summary>
    [Fact]
    public async Task A_malformed_29_fails_at_once_and_keeps_the_28()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(
            Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.BrokenMap29())));

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));
        watch.Stop();

        Assert.Equal(Stage.WaitStartMap, result.Stage);
        Assert.Equal(Failure.NoticeMalformed, result.Failure);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Null(result.StartMap);
        Assert.True(watch.ElapsedMilliseconds < 3000);
    }

    // ======== 断连与切帧错误 ========

    /// <summary>对端在任一等待阶段关了连接:如实报连接断开(停在哪个阶段看 Stage),不是等满窗口再报超时。</summary>
    [Theory]
    [InlineData(Stage.WaitLoadingNotice)]
    [InlineData(Stage.WaitDungeonInfo)]
    [InlineData(Stage.WaitStartMap)]
    public async Task A_peer_that_closes_while_waiting_is_a_connection_failure_not_a_timeout(Stage closesIn)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Id == 15)
            {
                if (closesIn == Stage.WaitLoadingNotice)
                {
                    pipe.CloseServer();
                }
                else
                {
                    pipe.Push(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()));
                }
            }
            else if (frame.Id == 16)
            {
                if (closesIn == Stage.WaitStartMap)
                {
                    pipe.Push(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()));
                }
                pipe.CloseServer();
            }
            return null;
        };

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));
        watch.Stop();

        Assert.Equal(closesIn, result.Stage);
        Assert.Equal(Failure.ConnectionClosed, result.Failure);
        Assert.True(watch.ElapsedMilliseconds < 3000, "断连要立即报告,不能等满窗口");
    }

    /// <summary>最后两个通知与连接关闭一起到:帧先于关闭被处理,照样走完全程。</summary>
    [Fact]
    public async Task Frames_that_arrive_together_with_the_close_are_still_handled()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Id == 15)
            {
                pipe.Push(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()));
            }
            else if (frame.Id == 16)
            {
                pipe.Push(DungeonEntryWire.Join(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));
                pipe.CloseServer();
            }
            return null;
        };

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.True(result.Entered);
        Assert.Equal(90001, result.StartMap.Seed);
    }

    /// <summary>入站帧头里的整帧长度小于下限:切帧出错,如实报切帧错误,不等满窗口。</summary>
    [Fact]
    public async Task A_framing_fault_stops_the_wait_with_framing_fault()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        byte[] bad = new byte[15];
        bad[1] = 27;
        DungeonEntryWire.PutU32(bad, 3, 14);        // 整帧长度 14 < 下限 15
        pipe.Script = Chain(Frames(bad), Frames());

        Stopwatch watch = Stopwatch.StartNew();
        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));
        watch.Stop();

        Assert.Equal(Stage.WaitLoadingNotice, result.Stage);
        Assert.Equal(Failure.FramingFault, result.Failure);
        Assert.Equal(FrameError.LengthBelowMinimum, session.LastFrameError);
        Assert.True(watch.ElapsedMilliseconds < 3000);
        ClientFrameShape.AssertOnlyStartGame(pipe);
    }

    /// <summary>调用前对端就已经关了:什么都不发,直接报连接断开。</summary>
    [Fact]
    public async Task A_peer_that_is_already_closed_fails_before_anything_is_sent()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.CloseServer();
        await session.ReceiveAsync(TimeSpan.FromMilliseconds(200));    // 读到流结束,会话记下对端已关
        Assert.True(session.PeerClosed);

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.Equal(Stage.SendStartGame, result.Stage);
        Assert.Equal(Failure.ConnectionClosed, result.Failure);
        Assert.Empty(pipe.ClientFrames);
    }

    // ======== 发送失败 ========

    /// <summary>
    /// 发"开始游戏"时帧层给了非 Ok 状态:如实记在阶段与状态里,后面什么都不做(不收帧、不发 16)。
    /// 真会话对 15、16 造不出这个状态,所以用会话接缝的假实现。
    /// </summary>
    [Theory]
    [InlineData(FrameStatus.EncryptFailed)]
    [InlineData(FrameStatus.FrameTooLarge)]
    public async Task A_failed_start_game_send_is_recorded_with_the_frame_status_and_nothing_else_happens(FrameStatus status)
    {
        var port = new DungeonEntryFakePort { StatusOfSend = index => status };

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port));

        Assert.False(result.Entered);
        Assert.Equal(Stage.SendStartGame, result.Stage);
        Assert.Equal(Failure.SendFailed, result.Failure);
        Assert.Equal(status, result.SendStatus);
        Assert.Single(port.Sent);
        Assert.Equal((Cmd, (ushort)15), (port.Sent[0].Class, port.Sent[0].Id));
        Assert.Equal(0, port.ReceiveCalls);
        Assert.Empty(result.Frames);
    }

    /// <summary>发"选副本"时帧层失败:停在发 16 那一步,状态如实,27 已经入账的成果保留,之后不再收帧。</summary>
    [Fact]
    public async Task A_failed_select_dungeon_send_is_recorded_and_keeps_the_27()
    {
        var port = new DungeonEntryFakePort { StatusOfSend = index => index == 1 ? FrameStatus.FrameTooLarge : FrameStatus.Ok };
        port.OnSent = (index, id) => index == 0 ? new[] { DungeonEntryWire.Decoded(0, 27, new byte[] { 7, 7, 7 }) } : null;

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port));

        Assert.Equal(Stage.SendSelectDungeon, result.Stage);
        Assert.Equal(Failure.SendFailed, result.Failure);
        Assert.Equal(FrameStatus.FrameTooLarge, result.SendStatus);
        Assert.Equal(new byte[] { 7, 7, 7 }, result.LoadingNoticePayload);
        Assert.Equal(new ushort[] { 15, 16 }, port.Sent.Select(s => s.Id).ToArray());
        Assert.Equal(1, port.ReceiveCalls);          // 只有等 27 时收过帧
    }

    /// <summary>发送时流已不可写(IO 异常、对象已释放)不外泄,按连接断开如实记录;第二次发送同样。</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task A_stream_that_fails_on_send_is_a_connection_failure(int failingSend, bool disposed)
    {
        var port = new DungeonEntryFakePort();
        port.FailureOfSend = index => index == failingSend
            ? (disposed ? new ObjectDisposedException("pipe") : new IOException("合成的写失败"))
            : null;
        port.OnSent = (index, id) => index == 0 ? new[] { DungeonEntryWire.Decoded(0, 27, new byte[4]) } : null;

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(port));

        Assert.Equal(failingSend == 0 ? Stage.SendStartGame : Stage.SendSelectDungeon, result.Stage);
        Assert.Equal(Failure.ConnectionClosed, result.Failure);
        Assert.Null(result.SendStatus);
        Assert.Equal(failingSend + 1, port.Sent.Count);
    }

    /// <summary>真会话上写流失败(IO 异常)同样报连接断开,什么都没有收到。</summary>
    [Fact]
    public async Task A_real_session_whose_stream_refuses_writes_reports_a_connection_failure()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.WriteFailure = new IOException("合成的写失败");

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session));

        Assert.Equal(Stage.SendStartGame, result.Stage);
        Assert.Equal(Failure.ConnectionClosed, result.Failure);
        Assert.Empty(pipe.ClientFrames);
    }

    // ======== 入参与前置 ========

    /// <summary>模式字节只许 0 到 4:超出的返回 BadRequest,什么都不发、也不回调阶段。</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(255)]
    public async Task A_mode_above_4_is_a_bad_request_and_nothing_is_sent(int mode)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        var stages = new List<Stage>();

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), mode: (byte)mode, onStage: stages.Add);

        Assert.False(result.Entered);
        Assert.Equal(Stage.SendStartGame, result.Stage);
        Assert.Equal(Failure.BadRequest, result.Failure);
        Assert.Empty(pipe.ClientFrames);
        Assert.Empty(stages);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public void Constructors_reject_a_null_session()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonEntry((GameClientSession)null));
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonEntry((IOnlineDungeonSession)null));
    }

    /// <summary>会话还没收到启动包(不可能已进城)是调用方的错:抛 InvalidOperationException,什么都不发。</summary>
    [Fact]
    public async Task A_session_that_is_not_ready_throws_and_sends_nothing()
    {
        var session = new GameClientSession(new MemoryStream(), s => new SliceXorCipher(s));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OnlineDungeonEntry(session).EnterAsync(Dungeon, 3));

        var port = new DungeonEntryFakePort { Ready = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OnlineDungeonEntry(port).EnterAsync(Dungeon, 3));
        Assert.Empty(port.Sent);
    }

    /// <summary>阶段窗口必须大于 0 且不超过 int.MaxValue 毫秒:零、负数、过大都抛 ArgumentOutOfRangeException,参数名对得上,什么都不发。</summary>
    [Theory]
    [InlineData("loadingNoticeTimeout", 0L)]
    [InlineData("loadingNoticeTimeout", -1L)]
    [InlineData("loadingNoticeTimeout", 2147483648L)]
    [InlineData("dungeonInfoTimeout", 0L)]
    [InlineData("dungeonInfoTimeout", -5L)]
    [InlineData("dungeonInfoTimeout", 2147483648L)]
    [InlineData("startMapTimeout", 0L)]
    [InlineData("startMapTimeout", -300L)]
    [InlineData("startMapTimeout", 2147483648L)]
    public async Task A_non_positive_or_oversized_window_is_rejected_by_name(string parameter, long milliseconds)
    {
        var port = new DungeonEntryFakePort();
        TimeSpan bad = TimeSpan.FromMilliseconds(milliseconds);
        var entry = new OnlineDungeonEntry(port);

        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => entry.EnterAsync(
            Dungeon, 3, 2, 0,
            parameter == "loadingNoticeTimeout" ? bad : Roomy,
            parameter == "dungeonInfoTimeout" ? bad : Roomy,
            parameter == "startMapTimeout" ? bad : Roomy));

        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(port.Sent);
    }

    /// <summary>取消令牌取消时抛 OperationCanceledException(与 OnlineTownEntry 同约定),而不是返回结果。</summary>
    [Fact]
    public async Task Cancelling_while_waiting_throws_operation_canceled()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        using var cancel = new CancellationTokenSource();
        Task<OnlineDungeonResult> run = Enter(new OnlineDungeonEntry(session), cancellationToken: cancel.Token);
        await WaitUntil(() => pipe.ClientFrames.Count == 1, "开始游戏命令发出");

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ======== 结果对象的文字形式 ========

    [Fact]
    public async Task Result_and_frame_text_forms_are_one_line_without_payload_content()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await ReadySession();
        pipe.Script = Chain(Frames(DungeonEntryWire.Envelope(DungeonEntryWire.Inner(0, 27, DungeonEntryWire.Loading27()))), Frames());

        OnlineDungeonResult result = await Enter(new OnlineDungeonEntry(session), loading: Quick);

        Assert.Equal("阶段=WaitLoadingNotice 失败=Timeout 帧=1", result.ToString());
        Assert.Equal("NOTI/561(63):EnvelopeUnopened", result.Frames[0].ToString());
    }
}

/// <summary>客户端发出帧的形状断言。</summary>
internal static class ClientFrameShape
{
    /// <summary>只发过一帧:类 1、包号 15、空载荷(整帧 13 字节头)。</summary>
    public static void AssertOnlyStartGame(DungeonEntryPipe pipe)
    {
        IReadOnlyList<DungeonEntryClientFrame> sent = pipe.ClientFrames;
        Assert.Single(sent);
        Assert.Equal(PacketClass.Cmd, sent[0].Class);
        Assert.Equal(15, sent[0].Id);
        Assert.Equal(FrameLayout.ClientToServer.HeaderLength, sent[0].WireLength);
        Assert.Empty(sent[0].Plain);
    }
}
