using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 宿主进副本薄封装(<see cref="OnlineDungeonRunner"/>)的测试:入参检查、解包委托、日志行、保活的先后次序。
/// 协议状态机的细节在 <c>OnlineDungeonEntryTests</c> 里测;这里只管封装自己加的东西。真会话接内存管道用来走"真信封 + 真委托"的路,
/// 会话接缝的假实现(<see cref="DungeonEntryFakePort"/>)用来造"只有委托才打得开的信封"、发送失败、会话未就绪这类真会话造不出的情形。
/// 数据全是合成的:副本号 901 起、种子 90001 起、无关帧包号 900 以上。
/// </summary>
public class OnlineDungeonRunnerTests
{
    private const PacketClass Noti = PacketClass.Noti;
    private const string Prefix = "DNF_NET stage=dungeon";

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Roomy = TimeSpan.FromSeconds(5);

    // ======== 组装与小工具 ========

    private static Task<OnlineDungeonResult> Enter(OnlineDungeonRunner runner, int dungeon, int mode, HostTestKit.LogSink log,
        TimeSpan? loading = null, TimeSpan? info = null, TimeSpan? map = null, CancellationToken cancellationToken = default)
    {
        return GatewayTestSupport.Bounded(runner.EnterAsync(dungeon, mode, log.Add, cancellationToken, loading ?? Roomy, info ?? Roomy, map ?? Roomy), 15);
    }

    private static Task<OnlineDungeonResult> Run(OnlineDungeonRunner runner, int dungeon, int mode, HostTestKit.LogSink log,
        TimeSpan? loading = null, TimeSpan? info = null, TimeSpan? map = null, CancellationToken cancellationToken = default)
    {
        return GatewayTestSupport.Bounded(runner.RunAsync(dungeon, mode, log.Add, cancellationToken, loading ?? Roomy, info ?? Roomy, map ?? Roomy), 15);
    }

    /// <summary>假端口:发出 15 后收到 27,发出 16 后收到 28 与 29(都是原样载荷,长度 48、17、12)。</summary>
    private static DungeonEntryFakePort ChainPort()
    {
        return new DungeonEntryFakePort
        {
            OnSent = (index, id) =>
                id == 15 ? new[] { DungeonEntryWire.Decoded(0, 27, DungeonEntryWire.Loading27()) }
                : id == 16 ? new[] { DungeonEntryWire.Decoded(0, 28, DungeonEntryWire.Info28()), DungeonEntryWire.Decoded(0, 29, DungeonEntryWire.Map29()) }
                : null,
        };
    }

    private static string Line(string rest)
    {
        return Prefix + " " + rest;
    }

    // ======== 解包委托 ========

    /// <summary>委托把真信封的载荷换回完整的内层帧(15 字节头加载荷),与独立拼出的内层帧逐字节一致。</summary>
    [Fact]
    public void The_unwrap_delegate_returns_the_inner_frame_of_a_real_envelope()
    {
        byte[] p27 = DungeonEntryWire.Loading27();
        byte[] inner = DungeonEntryWire.Inner(0, 27, p27);
        byte[] envelope = HostTestKit.RealEnvelopePayload(0, 27, p27);

        byte[] opened = OnlineDungeonRunner.UnwrapEnvelope(envelope);

        Assert.Equal(inner, opened);
        Assert.True(AntibotEnvelope.TryUnwrap(envelope, out byte[] direct));
        Assert.Equal(direct, opened);
    }

    /// <summary>不是合法信封的东西(空、残缺、乱字节、密文被改过的)一律给回 null,不抛。</summary>
    [Fact]
    public void The_unwrap_delegate_returns_null_for_anything_that_is_not_a_valid_envelope()
    {
        Assert.Null(OnlineDungeonRunner.UnwrapEnvelope(null));
        Assert.Null(OnlineDungeonRunner.UnwrapEnvelope(Array.Empty<byte>()));
        Assert.Null(OnlineDungeonRunner.UnwrapEnvelope(new byte[10]));
        Assert.Null(OnlineDungeonRunner.UnwrapEnvelope(GatewayTestSupport.Pattern(64, 0x31, 7)));

        byte[] tampered = HostTestKit.RealEnvelopePayload(0, 27, DungeonEntryWire.Loading27());
        tampered[tampered.Length - 1] ^= 0xFF;
        Assert.False(AntibotEnvelope.TryUnwrap(tampered, out _));       // 改动确实让信封失效,下面的断言才有意义
        Assert.Null(OnlineDungeonRunner.UnwrapEnvelope(tampered));
    }

    // ======== 入参 ========

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(901, 3, true)]
    [InlineData(65535, 4, true)]
    [InlineData(-1, 0, false)]
    [InlineData(65536, 0, false)]
    [InlineData(901, -1, false)]
    [InlineData(901, 5, false)]
    [InlineData(int.MaxValue, 3, false)]
    [InlineData(901, int.MinValue, false)]
    public void Request_values_are_checked_against_the_protocol_ranges(int dungeon, int mode, bool valid)
    {
        Assert.Equal(valid, OnlineDungeonRunner.IsValidRequest(dungeon, mode));
    }

    /// <summary>宿主命令行两个参数:纯十进制数字且在范围内才收,别的一律拒绝并把输出清零(含缺参数、越界、带符号或空白、全角数字、溢出)。</summary>
    [Theory]
    [InlineData("901", "3", true, 901, 3)]
    [InlineData("0", "0", true, 0, 0)]
    [InlineData("65535", "4", true, 65535, 4)]
    [InlineData("007", "02", true, 7, 2)]
    [InlineData("65536", "0", false, 0, 0)]
    [InlineData("901", "5", false, 0, 0)]
    [InlineData("-1", "0", false, 0, 0)]
    [InlineData("901", "-1", false, 0, 0)]
    [InlineData("+901", "3", false, 0, 0)]
    [InlineData(" 901", "3", false, 0, 0)]
    [InlineData("901", "3 ", false, 0, 0)]
    [InlineData("9,01", "3", false, 0, 0)]
    [InlineData("9.5", "3", false, 0, 0)]
    [InlineData("1e3", "3", false, 0, 0)]
    [InlineData("0x385", "3", false, 0, 0)]
    [InlineData("９０１", "3", false, 0, 0)]
    [InlineData("99999999999", "3", false, 0, 0)]
    [InlineData("901", "", false, 0, 0)]
    [InlineData("", "3", false, 0, 0)]
    [InlineData("abc", "3", false, 0, 0)]
    [InlineData("901", "-dnfquitafter", false, 0, 0)]
    [InlineData(null, "3", false, 0, 0)]
    [InlineData("901", null, false, 0, 0)]
    [InlineData(null, null, false, 0, 0)]
    public void The_host_arguments_are_parsed_strictly(string dungeonText, string modeText, bool ok, int dungeon, int mode)
    {
        bool result = OnlineDungeonRunner.TryParseArguments(dungeonText, modeText, out int parsedDungeon, out int parsedMode);

        Assert.Equal(ok, result);
        Assert.Equal(dungeon, parsedDungeon);
        Assert.Equal(mode, parsedMode);
    }

    /// <summary>不合规的入参:什么都不发、不收,带原因返回,日志只有开头一行与失败一行(没有阶段行)。</summary>
    [Theory]
    [InlineData(901, 5)]
    [InlineData(901, -1)]
    [InlineData(70000, 3)]
    [InlineData(-1, 0)]
    public async Task An_invalid_request_is_refused_without_sending_or_receiving_anything(int dungeon, int mode)
    {
        var port = ChainPort();
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(port), dungeon, mode, log);

        Assert.False(result.Entered);
        Assert.Equal(OnlineDungeonStage.SendStartGame, result.Stage);
        Assert.Equal(OnlineDungeonFailure.BadRequest, result.Failure);
        Assert.Empty(port.Sent);
        Assert.Equal(0, port.ReceiveCalls);
        Assert.Equal(
            new[]
            {
                Line("begin dungeon=" + dungeon + " mode=" + mode),
                Line("failed step=SendStartGame failure=BadRequest send=- rejected=- code=- frames=0 unopened=0 outOfOrder=0 duplicate=0 loading27=- info28=0 map29=0"),
            },
            log.Lines);
    }

    /// <summary>边界值照样发出去:副本号 0 与 65535、模式 0 与 4,选副本的载荷是 u16 小端、模式、第三字节 2、安全字节 0。</summary>
    [Theory]
    [InlineData(0, 0, new byte[] { 0, 0, 0, 2, 0 })]
    [InlineData(65535, 4, new byte[] { 0xFF, 0xFF, 4, 2, 0 })]
    [InlineData(901, 3, new byte[] { 0x85, 0x03, 3, 2, 0 })]
    public async Task Boundary_requests_are_sent_with_the_documented_payload(int dungeon, int mode, byte[] expectedPayload)
    {
        var port = ChainPort();
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(port), dungeon, mode, log);

        Assert.True(result.Entered);
        Assert.Equal(new ushort[] { 15, 16 }, port.Sent.Select(s => s.Id).ToArray());
        Assert.Empty(port.Sent[0].Payload);
        Assert.Equal(expectedPayload, port.Sent[1].Payload);
    }

    [Fact]
    public void Constructors_refuse_a_null_session()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonRunner((GameClientSession)null));
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonRunner((IOnlineDungeonSession)null));
    }

    /// <summary>进副本本来会抛的异常原样抛出(会话没就绪、窗口不合规):封装不吞,吞是 <see cref="OnlineDungeonRunner.RunAsync"/> 的事。</summary>
    [Fact]
    public async Task EnterAsync_lets_the_entrys_exceptions_through()
    {
        var log = new HostTestKit.LogSink();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new OnlineDungeonRunner(new DungeonEntryFakePort { Ready = false }).EnterAsync(901, 3, log.Add));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new OnlineDungeonRunner(ChainPort()).EnterAsync(901, 3, log.Add, default, TimeSpan.Zero));
    }

    // ======== 日志行 ========

    /// <summary>
    /// 整条链走通的日志:开头、五个阶段、三条收帧记录(记录里带到达时的阶段)、一行完成。
    /// 假端口给的载荷长度是确定的(48、17、12),所以整份日志可以逐行断言。
    /// </summary>
    [Fact]
    public async Task The_log_of_a_full_run_lists_the_stages_the_frames_and_the_done_line()
    {
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(ChainPort()), 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(
            new[]
            {
                Line("begin dungeon=901 mode=3"),
                Line("step=SendStartGame"),
                Line("step=WaitLoadingNotice"),
                Line("step=SendSelectDungeon"),
                Line("step=WaitDungeonInfo"),
                Line("step=WaitStartMap"),
                Line("frame n=0 step=WaitLoadingNotice NOTI/27(48):Accepted"),
                Line("frame n=1 step=WaitDungeonInfo NOTI/28(17):Accepted"),
                Line("frame n=2 step=WaitStartMap NOTI/29(12):Accepted"),
                Line("done step=Done frames=3 unopened=0 outOfOrder=0 duplicate=0 loading27=48 info28=1 map29=1"),
            },
            log.Lines);
    }

    /// <summary>等 27 超时:失败行写明停在哪一步与原因,什么都没收到时各项为空。</summary>
    [Fact]
    public async Task A_timeout_is_logged_with_the_step_and_the_reason()
    {
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(new DungeonEntryFakePort()), 901, 3, log, loading: Quick);

        Assert.Equal(OnlineDungeonStage.WaitLoadingNotice, result.Stage);
        Assert.Equal(OnlineDungeonFailure.Timeout, result.Failure);
        Assert.Equal(
            Line("failed step=WaitLoadingNotice failure=Timeout send=- rejected=- code=- frames=0 unopened=0 outOfOrder=0 duplicate=0 loading27=- info28=0 map29=0"),
            log.Lines.Last());
        Assert.DoesNotContain(log.Lines, l => l.Contains("step=SendSelectDungeon"));
    }

    /// <summary>被服务端拒绝:失败行带被拒命令的包号与失败码,收帧记录里有那一帧。</summary>
    [Fact]
    public async Task A_rejection_is_logged_with_the_command_and_the_code()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(HostTestKit.Frames(DungeonEntryWire.Reply(15, new byte[] { 0, 0x11 })), HostTestKit.Frames());
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log);

        Assert.Equal(OnlineDungeonFailure.Rejected, result.Failure);
        string last = log.Lines.Last();
        Assert.StartsWith(Line("failed step=WaitLoadingNotice failure=Rejected send=- rejected=15 code=17 frames=1 "), last);
        Assert.Contains(log.Lines, l => l.StartsWith(Line("frame n=0 step=WaitLoadingNotice CMD/15(")) && l.EndsWith("):Rejected"));
    }

    /// <summary>发送被帧层拒绝:失败行里是帧层状态名。</summary>
    [Fact]
    public async Task A_send_failure_is_logged_with_the_frame_status()
    {
        var port = new DungeonEntryFakePort { StatusOfSend = index => FrameStatus.FrameTooLarge };
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(port), 901, 3, log);

        Assert.Equal(OnlineDungeonFailure.SendFailed, result.Failure);
        Assert.Equal(
            Line("failed step=SendStartGame failure=SendFailed send=FrameTooLarge rejected=- code=- frames=0 unopened=0 outOfOrder=0 duplicate=0 loading27=- info28=0 map29=0"),
            log.Lines.Last());
    }

    /// <summary>28 读不下来:失败行写明通知读不通,27 的成果(载荷长度)仍在行里。</summary>
    [Fact]
    public async Task A_malformed_notice_is_logged_and_keeps_what_was_already_received()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(
            HostTestKit.Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            HostTestKit.Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.BrokenInfo28())));
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log);

        Assert.Equal(OnlineDungeonStage.WaitDungeonInfo, result.Stage);
        Assert.Equal(OnlineDungeonFailure.NoticeMalformed, result.Failure);
        Assert.StartsWith(Line("failed step=WaitDungeonInfo failure=NoticeMalformed send=- rejected=- code=- frames=2 "), log.Lines.Last());
        Assert.EndsWith(" loading27=48 info28=0 map29=0", log.Lines.Last());
    }

    /// <summary>对端在中途关了连接:失败行写连接断开。</summary>
    [Fact]
    public async Task A_closed_connection_is_logged()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class == PacketClass.Cmd && frame.Id == 15)
            {
                pipe.CloseServer();
            }
            return null;
        };
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log);

        Assert.Equal(OnlineDungeonFailure.ConnectionClosed, result.Failure);
        Assert.StartsWith(Line("failed step=WaitLoadingNotice failure=ConnectionClosed "), log.Lines.Last());
    }

    /// <summary>
    /// 起始地图先于副本信息到:忽略,进不了终局(不能把 29 提前收下);行里的乱序计数与 29 的"没收到"都如实。
    /// </summary>
    [Fact]
    public async Task A_start_map_that_precedes_the_dungeon_info_never_completes_the_entry()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(
            HostTestKit.Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            HostTestKit.Frames(DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log, info: Quick);

        Assert.False(result.Entered);
        Assert.Equal(OnlineDungeonStage.WaitDungeonInfo, result.Stage);
        Assert.Equal(OnlineDungeonFailure.Timeout, result.Failure);
        Assert.Null(result.StartMap);
        Assert.Null(result.DungeonInfo);
        Assert.Equal(1, result.OutOfOrderCount);
        Assert.Equal(
            Line("failed step=WaitDungeonInfo failure=Timeout send=- rejected=- code=- frames=2 unopened=0 outOfOrder=1 duplicate=0 loading27=48 info28=0 map29=0"),
            log.Lines.Last());
    }

    /// <summary>先到的 29 被忽略,28 之后再到的那个 29 才算数:用的是后一个的种子。</summary>
    [Fact]
    public async Task An_early_start_map_is_ignored_and_the_one_after_the_dungeon_info_wins()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(
            HostTestKit.Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27())),
            HostTestKit.Frames(
                DungeonEntryWire.Notice(29, DungeonEntryWire.Map29(90001)),
                DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()),
                DungeonEntryWire.Notice(29, DungeonEntryWire.Map29(90002))));
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log);

        Assert.True(result.Entered);
        Assert.NotNull(result.DungeonInfo);
        Assert.Equal(90002, result.StartMap.Seed);
        Assert.Equal(1, result.OutOfOrderCount);
        Assert.StartsWith(Line("done step=Done frames=4 unopened=0 outOfOrder=1 "), log.Lines.Last());
    }

    /// <summary>日志里没有载荷内容也没有异常消息:每一行都以固定前缀开头,载荷里的标记串与十六进制片段不出现。</summary>
    [Fact]
    public async Task No_log_line_carries_payload_content_or_exception_messages()
    {
        byte[] marked27 = Encoding.ASCII.GetBytes("MARK27-PAYLOAD-SECRET-").Concat(GatewayTestSupport.Pattern(26, 0xA0)).ToArray();
        Assert.Equal(48, marked27.Length);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(
            HostTestKit.Frames(DungeonEntryWire.Notice(27, marked27)),
            HostTestKit.Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29())));
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Run(
            new OnlineDungeonRunner(session, token => throw new System.IO.IOException("SECRET-EXCEPTION-MESSAGE-905")), 901, 3, log);

        Assert.True(result.Entered);
        string all = string.Join("\n", log.Lines);
        Assert.Contains("DNF_NET heartbeat ended=IOException", all);
        Assert.DoesNotContain("SECRET", all);
        Assert.DoesNotContain("MARK27", all);
        Assert.DoesNotContain(Convert.ToHexString(marked27, 0, 6), all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(DungeonEntryWire.Info28(), 0, 6), all, StringComparison.OrdinalIgnoreCase);
        Assert.All(log.Lines, l => Assert.True(l.StartsWith(Prefix) || l.StartsWith("DNF_NET heartbeat "), "行首不合规:" + l));
    }

    // ======== 信封:委托真的接上了 ========

    /// <summary>
    /// 真会话 + 真信封(27、28、29 各封在一个信封里):封装注入的委托打开每个信封,没有"没解开"的;
    /// 日志里信封帧记为已解开,客户端只发了 15 与 16(载荷与入参一致)。
    /// </summary>
    [Fact]
    public async Task Real_envelopes_on_a_real_session_are_all_opened_through_the_runners_delegate()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] p27 = DungeonEntryWire.Loading27();
        pipe.Script = HostTestKit.DungeonChain(
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 27, p27)),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 28, DungeonEntryWire.Info28()), HostTestKit.RealEnvelope(0, 29, DungeonEntryWire.Map29())));
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(0, result.UnopenedEnvelopeCount);
        Assert.Equal(p27, result.LoadingNoticePayload);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(90001, result.StartMap.Seed);
        Assert.Equal(3, result.Frames.Count(f => f.PacketId == 561 && f.Note == OnlineDungeonFrameNote.EnvelopeOpened));
        Assert.Contains(log.Lines, l => l.StartsWith(Line("frame n=0 step=WaitLoadingNotice NOTI/561(")) && l.EndsWith("):EnvelopeOpened"));
        Assert.StartsWith(Line("done step=Done "), log.Lines.Last());
        Assert.Contains(" unopened=0 ", log.Lines.Last());
        Assert.Equal(new ushort[] { 15, 16 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        Assert.Equal(new byte[] { 0x85, 0x03, 3, 2, 0 }, pipe.ClientFrames[1].Plain.Take(5).ToArray());
    }

    /// <summary>
    /// 假端口上只有信封、没有会话层替它拆封:27、28、29 的信封只有封装注入的委托能打开,
    /// 所以走通且没有"没解开"就证明委托接上了真的信封解包,不是 null。
    /// </summary>
    [Fact]
    public async Task Envelopes_on_a_bare_port_can_only_be_opened_by_the_runners_delegate()
    {
        var port = new DungeonEntryFakePort
        {
            OnSent = (index, id) =>
                id == 15 ? new[] { DungeonEntryWire.Decoded(0, 561, HostTestKit.RealEnvelopePayload(0, 27, DungeonEntryWire.Loading27())) }
                : id == 16 ? new[]
                {
                    DungeonEntryWire.Decoded(0, 561, HostTestKit.RealEnvelopePayload(0, 28, DungeonEntryWire.Info28())),
                    DungeonEntryWire.Decoded(0, 561, HostTestKit.RealEnvelopePayload(0, 29, DungeonEntryWire.Map29())),
                }
                : null,
        };
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(port), 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(0, result.UnopenedEnvelopeCount);
        Assert.Equal(901, result.DungeonInfo.DungeonId);
        Assert.Equal(90001, result.StartMap.Seed);
        Assert.Equal(
            new[] { OnlineDungeonFrameNote.EnvelopeOpened, OnlineDungeonFrameNote.Accepted, OnlineDungeonFrameNote.EnvelopeOpened, OnlineDungeonFrameNote.Accepted,
                OnlineDungeonFrameNote.EnvelopeOpened, OnlineDungeonFrameNote.Accepted },
            result.Frames.Select(f => f.Note).ToArray());
        Assert.Equal(new[] { false, true, false, true, false, true }, result.Frames.Select(f => f.FromEnvelope).ToArray());
    }

    /// <summary>一个打不开的 561(合成乱字节)不会被误当成内层:记为没解开,继续等,后面的信封照常打开。</summary>
    [Fact]
    public async Task An_envelope_that_cannot_be_opened_is_counted_and_the_wait_goes_on()
    {
        var port = new DungeonEntryFakePort
        {
            OnSent = (index, id) =>
                id == 15 ? new[]
                {
                    DungeonEntryWire.Decoded(0, 561, GatewayTestSupport.Pattern(40, 0x11, 3)),
                    DungeonEntryWire.Decoded(0, 561, HostTestKit.RealEnvelopePayload(0, 27, DungeonEntryWire.Loading27())),
                }
                : id == 16 ? new[] { DungeonEntryWire.Decoded(0, 28, DungeonEntryWire.Info28()), DungeonEntryWire.Decoded(0, 29, DungeonEntryWire.Map29()) }
                : null,
        };
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(port), 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(1, result.UnopenedEnvelopeCount);
        Assert.Contains(" unopened=1 ", log.Lines.Last());
    }

    // ======== 会话事件汇总行 ========

    /// <summary>
    /// 真会话上走通:汇总行在收帧记录之后、完成行之前(完成行仍是进副本的最后一行),数的是整个会话到此刻的累计:
    /// 收到 7 个(启动包、三个信封帧、各带一个内层帧)、发出 2 个(两条命令),共 9 个事件。
    /// </summary>
    [Fact]
    public async Task A_real_session_run_logs_the_session_digest_between_the_frames_and_the_done_line()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 27, DungeonEntryWire.Loading27())),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 28, DungeonEntryWire.Info28()), HostTestKit.RealEnvelope(0, 29, DungeonEntryWire.Map29())));
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log);

        Assert.True(result.Entered);
        string[] lines = log.Lines;
        Assert.StartsWith(Line("done step=Done "), lines[lines.Length - 1]);
        Assert.Equal(
            Line("session events=9 received=7 envelopes=3 opened=3 unwrapFailed=0 unwrapReasons=- innerShort=0 innerIds=27,28,29 id2Direct=0 id2Inner=0"),
            lines[lines.Length - 2]);
        Assert.StartsWith(Line("frame n="), lines[lines.Length - 3]);
    }

    /// <summary>
    /// 失败时也有汇总行,位置同样在失败行之前:超时的典型症状("信封到了没拆开所以永远等不到 27")能在里面直接看到原因。
    /// 合成的垃圾信封载荷头四个字节对不上长度,拆包状态是 LengthPrefixMismatch。
    /// </summary>
    [Fact]
    public async Task A_failed_real_session_run_logs_the_digest_too_and_shows_why_an_envelope_stayed_closed()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.DungeonChain(HostTestKit.Frames(DungeonEntryWire.Envelope(GatewayTestSupport.Pattern(40, 0x55))), HostTestKit.Frames());
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Enter(new OnlineDungeonRunner(session), 901, 3, log, loading: Quick);

        Assert.Equal(OnlineDungeonFailure.Timeout, result.Failure);
        string[] lines = log.Lines;
        Assert.StartsWith(Line("failed step=WaitLoadingNotice failure=Timeout "), lines[lines.Length - 1]);
        Assert.Contains(" unopened=1 ", lines[lines.Length - 1]);
        Assert.Equal(
            Line("session events=4 received=3 envelopes=1 opened=0 unwrapFailed=1 unwrapReasons=LengthPrefixMismatch:1 innerShort=0 innerIds=- id2Direct=0 id2Inner=0"),
            lines[lines.Length - 2]);
    }

    /// <summary>不合规的入参根本没进副本:没有汇总行,日志只有开头与失败两行。</summary>
    [Fact]
    public async Task A_refused_request_logs_no_session_digest()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        var log = new HostTestKit.LogSink();

        await Enter(new OnlineDungeonRunner(session), 901, 9, log);

        Assert.Equal(2, log.Lines.Length);
        Assert.DoesNotContain(log.Lines, l => l.Contains(" session "));
        Assert.Empty(pipe.ClientFrames);
    }

    // ======== 保活的先后次序与取舍 ========

    /// <summary>
    /// 保活只在进副本这一步结束、完成行已写出之后才起;起的时候两条命令都已发完(与进副本不并发):
    /// 日志依次是 完成行、保活开始、保活结束(回了几次心跳)。
    /// </summary>
    [Fact]
    public async Task The_keepalive_starts_only_after_the_entry_is_done_and_never_alongside_it()
    {
        var port = ChainPort();
        var log = new HostTestKit.LogSink();
        string[] linesWhenStarted = null;
        int sentWhenStarted = -1;
        var runner = new OnlineDungeonRunner(port, token =>
        {
            linesWhenStarted = log.Lines;
            sentWhenStarted = port.Sent.Count;
            return Task.FromResult(7);
        });

        OnlineDungeonResult result = await Run(runner, 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(2, sentWhenStarted);                                   // 15 与 16 都已发完
        Assert.Equal(Line("keepalive=start"), linesWhenStarted.Last());     // 泵被调用时,"开始"行刚写出,在它之前是完成行
        Assert.StartsWith(Line("done step=Done "), linesWhenStarted[linesWhenStarted.Length - 2]);
        Assert.DoesNotContain(linesWhenStarted, l => l.Contains("heartbeat"));
        string[] lines = log.Lines;
        Assert.Equal(new[] { Line("keepalive=start"), "DNF_NET heartbeat answered=7" }, lines.Skip(lines.Length - 2).ToArray());
        Assert.StartsWith(Line("done step=Done "), lines[lines.Length - 3]);
    }

    /// <summary>
    /// 真会话:进副本走完后保活泵接手同一个会话,收到心跳通知就回一帧类 1、包号 0、八个零字节;对端关流后泵结束,日志里是回了一次。
    /// 客户端发出的命令依次是 进城的 1、8、4,进副本的 15、16,保活的 0。
    /// </summary>
    [Fact]
    public async Task On_a_real_session_the_heartbeat_pump_takes_over_after_the_entry_and_answers_heartbeats()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, HostTestKit.ListPayload())),
            slot => HostTestKit.EnterComplete(),
            HostTestKit.DungeonChain(
                HostTestKit.Frames(HostTestKit.RealEnvelope(0, 27, DungeonEntryWire.Loading27())),
                HostTestKit.Frames(HostTestKit.RealEnvelope(0, 28, DungeonEntryWire.Info28()), HostTestKit.RealEnvelope(0, 29, DungeonEntryWire.Map29()))));
        OnlineEntryResult town = await HostTestKit.EnterTown(session);
        Assert.True(town.Entered);
        Assert.Equal(61, town.Snapshot.Level);
        var log = new HostTestKit.LogSink();

        Task<OnlineDungeonResult> run = new OnlineDungeonRunner(town.Session).RunAsync(901, 3, log.Add, default, Roomy, Roomy, Roomy);
        await log.WaitFor(l => l == Line("keepalive=start"), "保活开始");
        pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));        // 服务端心跳
        pipe.CloseServer();
        OnlineDungeonResult result = await GatewayTestSupport.Bounded(run, 15);

        Assert.True(result.Entered);
        Assert.Equal("DNF_NET heartbeat answered=1", log.Lines.Last());
        Assert.Equal(new ushort[] { 1, 8, 4, 15, 16, 0 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        DungeonEntryClientFrame reply = pipe.ClientFrames.Last();
        Assert.Equal(PacketClass.Cmd, reply.Class);
        Assert.Equal(new byte[8], reply.Plain.Take(8).ToArray());
    }

    /// <summary>进副本中途连接断了:不起保活泵(日志写明跳过及原因),泵根本没被调用。</summary>
    [Fact]
    public async Task The_keepalive_is_skipped_when_the_connection_closed_during_the_entry()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class == PacketClass.Cmd && frame.Id == 15)
            {
                pipe.CloseServer();
            }
            return null;
        };
        int started = 0;
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Run(new OnlineDungeonRunner(session, token => { started++; return Task.FromResult(0); }), 901, 3, log);

        Assert.Equal(OnlineDungeonFailure.ConnectionClosed, result.Failure);
        Assert.Equal(0, started);
        Assert.Equal(Line("keepalive=skip reason=ConnectionClosed"), log.Lines.Last());
    }

    /// <summary>
    /// 进副本是成功的,但在保活起来之前连接断了(完成行已经写出):同样不起泵,保活行写明跳过与原因。
    /// 这一条只有"会话当前是否可用"的检查能拦住,光看进副本的结局拦不住。
    /// </summary>
    [Fact]
    public async Task The_keepalive_is_skipped_when_the_connection_dropped_right_after_a_successful_entry()
    {
        DungeonEntryFakePort port = ChainPort();
        Func<int, ushort, DecodedFrame[]> chain = port.OnSent;
        port.OnSent = (index, id) =>
        {
            DecodedFrame[] frames = chain(index, id);
            if (id == 16)
            {
                port.Closed = true;         // 28 与 29 已经排进收件队,发完 16 之后对端关了
            }
            return frames;
        };
        int started = 0;
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Run(new OnlineDungeonRunner(port, token => { started++; return Task.FromResult(0); }), 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(0, started);
        string[] lines = log.Lines;
        Assert.StartsWith(Line("done step=Done "), lines[lines.Length - 2]);
        Assert.Equal(Line("keepalive=skip reason=ConnectionClosed"), lines.Last());
    }

    /// <summary>入站字节流切帧出错:同样不起泵(泵遇到切帧错误会空转),原因写切帧错误。</summary>
    [Fact]
    public async Task The_keepalive_is_skipped_after_a_framing_fault()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] bad = new byte[15];
        bad[1] = 27;
        DungeonEntryWire.PutU32(bad, 3, 14);        // 整帧长度 14 < 下限
        pipe.Script = HostTestKit.DungeonChain(HostTestKit.Frames(bad), HostTestKit.Frames());
        int started = 0;
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Run(new OnlineDungeonRunner(session, token => { started++; return Task.FromResult(0); }), 901, 3, log);

        Assert.Equal(OnlineDungeonFailure.FramingFault, result.Failure);
        Assert.Equal(0, started);
        Assert.Equal(Line("keepalive=skip reason=FramingFault"), log.Lines.Last());
    }

    /// <summary>
    /// 进副本超时、被拒、入参不合规这类失败连接都还活着:照样接上保活,与"没带副本参数时进城后就保活"的既有行为一致,
    /// 连接不会因为副本这一步没成就被放着不管。
    /// </summary>
    [Theory]
    [InlineData(901, 3, "Timeout")]
    [InlineData(901, 9, "BadRequest")]
    public async Task The_keepalive_still_runs_after_a_failure_that_leaves_the_connection_alive(int dungeon, int mode, string failure)
    {
        int started = 0;
        var log = new HostTestKit.LogSink();
        var runner = new OnlineDungeonRunner(new DungeonEntryFakePort(), token => { started++; return Task.FromResult(3); });

        OnlineDungeonResult result = await Run(runner, dungeon, mode, log, loading: Quick);

        Assert.Equal(failure, result.Failure.ToString());
        Assert.Equal(1, started);
        string[] lines = log.Lines;
        Assert.Equal(new[] { Line("keepalive=start"), "DNF_NET heartbeat answered=3" }, lines.Skip(lines.Length - 2).ToArray());
    }

    [Fact]
    public async Task Without_a_keepalive_the_log_says_so()
    {
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult result = await Run(new OnlineDungeonRunner(ChainPort()), 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(Line("keepalive=skip reason=NoKeepAlive"), log.Lines.Last());
    }

    [Theory]
    [InlineData(OnlineDungeonFailure.None, true)]
    [InlineData(OnlineDungeonFailure.BadRequest, true)]
    [InlineData(OnlineDungeonFailure.SendFailed, true)]
    [InlineData(OnlineDungeonFailure.Timeout, true)]
    [InlineData(OnlineDungeonFailure.Rejected, true)]
    [InlineData(OnlineDungeonFailure.NoticeMalformed, true)]
    [InlineData(OnlineDungeonFailure.ConnectionClosed, false)]
    [InlineData(OnlineDungeonFailure.FramingFault, false)]
    public void The_keepalive_decision_depends_only_on_whether_the_connection_is_still_usable(OnlineDungeonFailure failure, bool keepAlive)
    {
        var result = new OnlineDungeonResult(OnlineDungeonStage.WaitLoadingNotice, failure, null, null, null, null, null, null, new OnlineDungeonFrame[0]);
        Assert.Equal(keepAlive, OnlineDungeonRunner.ShouldKeepAlive(result));
        Assert.False(OnlineDungeonRunner.ShouldKeepAlive(null));
    }

    // ======== 异常与取消:RunAsync 从不抛 ========

    /// <summary>取消:安静地返回 null,不再打失败行,也不起保活。</summary>
    [Fact]
    public async Task Cancellation_ends_the_run_quietly()
    {
        using var cts = new CancellationTokenSource();
        int started = 0;
        var log = new HostTestKit.LogSink();
        var runner = new OnlineDungeonRunner(new DungeonEntryFakePort(), token => { started++; return Task.FromResult(0); });

        Task<OnlineDungeonResult> run = runner.RunAsync(901, 3, log.Add, cts.Token, TimeSpan.FromSeconds(30), Roomy, Roomy);
        await log.WaitFor(l => l == Line("step=WaitLoadingNotice"), "进入等 27");
        cts.Cancel();
        OnlineDungeonResult result = await GatewayTestSupport.Bounded(run, 10);

        Assert.Null(result);
        Assert.Equal(0, started);
        Assert.DoesNotContain(log.Lines, l => l.Contains("failed") || l.Contains("keepalive") || l.Contains("heartbeat"));
    }

    /// <summary>会话没就绪:只打一行失败行,里面只有异常的类型名,没有异常消息;返回 null。</summary>
    [Fact]
    public async Task An_unexpected_exception_is_reported_by_type_name_only()
    {
        var log = new HostTestKit.LogSink();
        var runner = new OnlineDungeonRunner(new DungeonEntryFakePort { Ready = false }, token => Task.FromResult(0));

        OnlineDungeonResult result = await Run(runner, 901, 3, log);

        Assert.Null(result);
        Assert.Equal(
            new[] { Line("begin dungeon=901 mode=3"), Line("failed exception=InvalidOperationException") },
            log.Lines);
    }

    /// <summary>保活本身出错:一行 heartbeat ended=类型名(与宿主原来的保活日志同文),不抛、不带消息,进副本的结果照常返回。</summary>
    [Fact]
    public async Task A_failing_keepalive_is_logged_by_type_name_and_the_result_is_still_returned()
    {
        var log = new HostTestKit.LogSink();
        var runner = new OnlineDungeonRunner(ChainPort(), token => throw new System.IO.IOException("SECRET-905"));

        OnlineDungeonResult result = await Run(runner, 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal("DNF_NET heartbeat ended=IOException", log.Lines.Last());
        Assert.DoesNotContain("SECRET", string.Join("\n", log.Lines));
    }

    /// <summary>保活被取消:不打任何保活结束行。</summary>
    [Fact]
    public async Task A_cancelled_keepalive_logs_nothing_more()
    {
        var log = new HostTestKit.LogSink();
        var runner = new OnlineDungeonRunner(ChainPort(), token => throw new OperationCanceledException());

        OnlineDungeonResult result = await Run(runner, 901, 3, log);

        Assert.True(result.Entered);
        Assert.Equal(Line("keepalive=start"), log.Lines.Last());
    }

    /// <summary>日志回调自己抛异常也不会漏出 RunAsync(调用方是后台线程里的"发后不管")。</summary>
    [Fact]
    public async Task A_throwing_log_callback_never_escapes_the_run()
    {
        var runner = new OnlineDungeonRunner(ChainPort(), token => Task.FromResult(0));

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 3, line => throw new InvalidOperationException("日志坏了"), default, Roomy, Roomy, Roomy), 15);

        Assert.Null(result);
    }

    // ======== 宿主放行回调:进副本有了结局就通知一次,保活照旧在后台接着跑 ========

    /// <summary>
    /// 进副本成功:回调恰好一次,带着与返回值同一个结果;发生在保活泵被调用之前、完成行已经写出之后
    /// (宿主据此放行城镇流程,保活之后才起,所以宿主放行时看得到完整的进副本日志)。
    /// </summary>
    [Fact]
    public async Task The_entered_callback_fires_once_with_the_result_before_the_keepalive_starts()
    {
        var log = new HostTestKit.LogSink();
        var calls = new List<OnlineDungeonResult>();
        string[] linesAtCallback = null;
        int startedAtCallback = -1;
        int started = 0;
        var runner = new OnlineDungeonRunner(ChainPort(), token => { started++; return Task.FromResult(2); });

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(runner.RunAsync(901, 3, log.Add, default, Roomy, Roomy, Roomy, entered: r =>
        {
            calls.Add(r);
            linesAtCallback = log.Lines;
            startedAtCallback = started;
        }), 15);

        Assert.True(result.Entered);
        Assert.Same(result, Assert.Single(calls));
        Assert.Equal(0, startedAtCallback);                                         // 回调的时候保活还没起
        Assert.StartsWith(Line("done step=Done "), linesAtCallback.Last());         // 完成行已写出,保活行还没有
        Assert.Equal(1, started);                                                    // 回调之后保活照常起了
        Assert.Equal("DNF_NET heartbeat answered=2", log.Lines.Last());
    }

    /// <summary>进副本失败(超时、入参不合规)也通知一次,带着失败的结果;失败行已经写出;保活照样接上。</summary>
    [Theory]
    [InlineData(901, 3, "Timeout")]
    [InlineData(901, 9, "BadRequest")]
    public async Task The_entered_callback_also_fires_for_a_failed_entry(int dungeon, int mode, string failure)
    {
        var log = new HostTestKit.LogSink();
        var calls = new List<OnlineDungeonResult>();
        string[] linesAtCallback = null;
        int started = 0;
        var runner = new OnlineDungeonRunner(new DungeonEntryFakePort(), token => { started++; return Task.FromResult(0); });

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(runner.RunAsync(dungeon, mode, log.Add, default, Quick, Roomy, Roomy, entered: r =>
        {
            calls.Add(r);
            linesAtCallback = log.Lines;
        }), 15);

        Assert.Equal(failure, result.Failure.ToString());
        Assert.Same(result, Assert.Single(calls));
        Assert.StartsWith(Line("failed step="), linesAtCallback.Last());
        Assert.Equal(1, started);
    }

    /// <summary>意外异常(会话没就绪):回调恰好一次,参数是 null,宿主不会卡在等放行上;失败行只有异常类型名。</summary>
    [Fact]
    public async Task The_entered_callback_fires_with_null_when_the_entry_throws()
    {
        var log = new HostTestKit.LogSink();
        var calls = new List<OnlineDungeonResult>();
        var runner = new OnlineDungeonRunner(new DungeonEntryFakePort { Ready = false }, token => Task.FromResult(0));

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 3, log.Add, default, Roomy, Roomy, Roomy, entered: calls.Add), 15);

        Assert.Null(result);
        Assert.Null(Assert.Single(calls));
        Assert.Equal(Line("failed exception=InvalidOperationException"), log.Lines.Last());
    }

    /// <summary>取消:回调恰好一次,参数是 null;之后不起保活。</summary>
    [Fact]
    public async Task The_entered_callback_fires_with_null_when_the_run_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var log = new HostTestKit.LogSink();
        var calls = new List<OnlineDungeonResult>();
        int started = 0;
        var runner = new OnlineDungeonRunner(new DungeonEntryFakePort(), token => { started++; return Task.FromResult(0); });

        Task<OnlineDungeonResult> run = runner.RunAsync(901, 3, log.Add, cts.Token, TimeSpan.FromSeconds(30), Roomy, Roomy, calls.Add);
        await log.WaitFor(l => l == Line("step=WaitLoadingNotice"), "进入等 27");
        cts.Cancel();
        OnlineDungeonResult result = await GatewayTestSupport.Bounded(run, 10);

        Assert.Null(result);
        Assert.Null(Assert.Single(calls));
        Assert.Equal(0, started);
    }

    /// <summary>宿主的回调自己抛异常:吞掉,不影响保活与返回值。</summary>
    [Fact]
    public async Task A_throwing_entered_callback_does_not_stop_the_keepalive()
    {
        var log = new HostTestKit.LogSink();
        int started = 0;
        var runner = new OnlineDungeonRunner(ChainPort(), token => { started++; return Task.FromResult(4); });

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 3, log.Add, default, Roomy, Roomy, Roomy, entered: r => throw new InvalidOperationException("宿主回调坏了")), 15);

        Assert.True(result.Entered);
        Assert.Equal(1, started);
        Assert.Equal("DNF_NET heartbeat answered=4", log.Lines.Last());
    }
}
