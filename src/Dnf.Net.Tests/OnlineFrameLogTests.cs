using System.Text.RegularExpressions;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.GiveUpSignalKit;

namespace Dnf.Net.Tests;

/// <summary>
/// r22-giveup-signal:逐帧日志(<see cref="OnlineDungeonCombatOptions.FrameLog"/>,命令行 <c>--frame-log</c>,缺省关)的跑器级测试。
/// 关闭时输出与没有这个选项的旧行为逐字一致;打开时每帧一行、每个 561 信封再一行,格式逐字核对(含 <c>innerCopySkipped</c>、五种 <c>reason</c>、
/// <c>innerCompressed</c> 带 <c>inner=</c>、<c>opened inner=</c>),只含包类、包号与整帧总长、绝不含载荷内容;行数有上限(前 400 帧),超出的只计数、收场前补一行 <c>frame omitted=N</c>。
/// 合成帧(包号 900 起),不连真服。
/// </summary>
public class OnlineFrameLogTests
{
    private const string P = "DNF_NET stage=combat";

    // ---------------- 夹具 ----------------

    private sealed class Run
    {
        public OnlineDungeonCombatResult Result;
        public string[] Lines;
    }

    private static CombatKit.ServerFrame Plain(int id, byte[] payload) => new CombatKit.ServerFrame(CombatKit.Noti, (ushort)id, payload);

    private static CombatKit.ServerFrame Env(byte[] sealedBody = null) => new CombatKit.ServerFrame(CombatKit.Noti, 561, sealedBody ?? GatewayTestSupport.Pattern(20, 0x21, 1));

    /// <summary>字符串形式的"整帧总长":15 字节帧头加载荷。</summary>
    private static string Total(int payloadLength) => (GiveUpSignalKit.HeaderLength + payloadLength).ToString();

    /// <summary>
    /// 在假会话上跑一遍:先把各个帧单元灌进收件箱,再标记断开,跑器收完所有帧单元后以 ConnectionClosed 收场(确定、不靠时序)。
    /// <paramref name="frameLog"/> 为 null 表示压根不碰这个属性(缺省路径)。
    /// </summary>
    private static async Task<Run> RunFake(Action<CombatFakeSession> feed, Func<byte[], byte[]> unwrap, bool? frameLog)
    {
        var session = new CombatFakeSession();
        feed(session);
        session.Closed = true;
        OnlineDungeonCombatOptions options = CombatKit.Options();
        if (frameLog.HasValue)
        {
            options.FrameLog = frameLog.Value;
        }
        var runner = new OnlineDungeonCombatRunner(session, unwrap, options);
        var lines = new List<string>();
        OnlineDungeonCombatResult result = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(CombatKit.Sequential(2)), lines.Add), 20);
        Assert.Equal(OnlineDungeonCombatOutcome.ConnectionClosed, result.Outcome);
        return new Run { Result = result, Lines = lines.ToArray() };
    }

    private static Task<Run> RunFake(Action<CombatFakeSession> feed, bool frameLog = true)
    {
        return RunFake(feed, null, frameLog);
    }

    /// <summary>只留逐帧日志新增的那两类行(帧行与信封行),不含 <c>frame omitted=</c>。</summary>
    private static string[] Observed(IEnumerable<string> lines)
    {
        return lines.Where(l => l.StartsWith(P + " frame n=", StringComparison.Ordinal) || l.StartsWith(P + " envelope n=", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>去掉逐帧日志新增的所有行(帧行、信封行、省略行),剩下的就是旧行为的输出。</summary>
    private static string[] WithoutFrameLog(IEnumerable<string> lines)
    {
        return lines.Where(l => !l.StartsWith(P + " frame ", StringComparison.Ordinal) && !l.StartsWith(P + " envelope ", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>用假会话收下的"信封加它的内层副本"一个单元(副本是会话附在信封之后的那份明文帧)。</summary>
    private static void PushEnvelopeUnit(CombatFakeSession session, byte cls, int id, byte[] innerPayload)
    {
        session.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(cls, (ushort)id, innerPayload));
    }

    // ================= 关闭:与旧行为逐字一致 =================

    [Fact]
    public void The_option_is_off_by_default()
    {
        Assert.False(new OnlineDungeonCombatOptions().FrameLog);
        Assert.False(CombatKit.Options().FrameLog);
        Assert.Equal(400, OnlineDungeonCombatRunner.MaxFrameLogFrames);
    }

    private static void FeedMixedSequence(CombatFakeSession session)
    {
        session.Push(CombatKit.Unrelated(901));
        PushEnvelopeUnit(session, CombatKit.Noti, 902, GatewayTestSupport.Pattern(6, 0x31, 1));
        session.Push(CombatKit.Heartbeat());
        session.Push(Plain(903, GatewayTestSupport.Pattern(9, 0x41, 1)));
        session.Push(Env());                                            // 这个信封开不了(下面的委托第二次返回 null)
        session.Push(CombatKit.Unrelated(904));
    }

    private static Func<byte[], byte[]> MixedUnwrap()
    {
        int calls = 0;
        return _ => ++calls == 1 ? DungeonEntryWire.Inner(CombatKit.Noti, 902, GatewayTestSupport.Pattern(6, 0x31, 1)) : null;
    }

    [Fact]
    public async Task With_the_option_off_or_never_touched_the_output_is_exactly_the_old_lines_and_has_no_frame_or_envelope_line()
    {
        Run untouched = await RunFake(FeedMixedSequence, MixedUnwrap(), null);
        Run off = await RunFake(FeedMixedSequence, MixedUnwrap(), false);

        // 旧行为的输出(逐字):开始、收场两行,没有任何新增的行
        string[] old =
        {
            P + " begin monsters=2 duplicateNumbers=0",
            P + " ended outcome=ConnectionClosed phase=Fighting monsters=2 sent=0 confirmed=0 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
                "passthrough=7 heartbeats=1 envelopes=1/1 innerSkipped=1 frames=7 passIds=NOTI/0:1,NOTI/561:2,NOTI/901:1,NOTI/902:1,NOTI/903:1,NOTI/904:1",
        };
        Assert.Equal(old, untouched.Lines);
        Assert.Equal(old, off.Lines);
        Assert.Empty(Observed(untouched.Lines));
        Assert.DoesNotContain(untouched.Lines, l => l.Contains(" frame ") || l.Contains(" envelope ") || l.Contains("omitted"));
    }

    [Fact]
    public async Task With_the_option_on_every_other_line_and_every_count_are_exactly_what_they_are_with_it_off()
    {
        Run off = await RunFake(FeedMixedSequence, MixedUnwrap(), false);
        Run on = await RunFake(FeedMixedSequence, MixedUnwrap(), true);

        Assert.NotEmpty(Observed(on.Lines));
        Assert.Equal(off.Lines, WithoutFrameLog(on.Lines));              // 纯观测:新增的行之外一字不差
        Assert.Equal(off.Result.FramesSeen, on.Result.FramesSeen);
        Assert.Equal(off.Result.PassthroughCount, on.Result.PassthroughCount);
        Assert.Equal(off.Result.EnvelopesOpened, on.Result.EnvelopesOpened);
        Assert.Equal(off.Result.EnvelopesUnopened, on.Result.EnvelopesUnopened);
        Assert.Equal(off.Result.InnerCopiesSkipped, on.Result.InnerCopiesSkipped);
        Assert.Equal(off.Result.HeartbeatsAnswered, on.Result.HeartbeatsAnswered);
        Assert.Equal(off.Result.Duplicates, on.Result.Duplicates);
        Assert.Equal(off.Result.Malformed, on.Result.Malformed);
        Assert.Equal(off.Result.Outcome, on.Result.Outcome);
    }

    // ================= 打开:行格式逐字 =================

    [Fact]
    public async Task A_frame_without_an_envelope_logs_one_line_with_class_id_and_total_length_counting_from_one()
    {
        Run run = await RunFake(s =>
        {
            s.Push(CombatKit.Unrelated(901));                                                       // 载荷 7 → 总长 22
            s.Push(new CombatKit.ServerFrame(CombatKit.Cmd, 902, GatewayTestSupport.Pattern(4, 0x11, 1)));    // 类 1 → CMD
            s.Push(CombatKit.Heartbeat());                                                          // 空载荷 → 总长 15
            s.Push(Plain(903, GatewayTestSupport.Pattern(30, 0x51, 1)));
        });

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/901(" + Total(7) + ")",
            P + " frame n=2 CMD/902(" + Total(4) + ")",
            P + " frame n=3 NOTI/0(" + Total(0) + ")",
            P + " frame n=4 NOTI/903(" + Total(30) + ")",
        }, Observed(run.Lines));
        // 总长是整帧长度(15 字节帧头加载荷),不是载荷长度:载荷 7 的帧写 22、空载荷的心跳写 15
        Assert.Equal("DNF_NET stage=combat frame n=1 NOTI/901(22)", Observed(run.Lines)[0]);
        Assert.Equal("DNF_NET stage=combat frame n=3 NOTI/0(15)", Observed(run.Lines)[2]);
    }

    [Fact]
    public async Task An_opened_envelope_logs_the_inner_identity_and_the_attached_copy_is_marked_skipped_once()
    {
        byte[] inner = GatewayTestSupport.Pattern(6, 0x31, 1);
        Run run = await RunFake(s =>
        {
            s.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 902, inner));
            s.Push(CombatKit.Unrelated(901));
        }, _ => DungeonEntryWire.Inner(CombatKit.Noti, 902, inner), true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 opened inner=NOTI/902(" + Total(6) + ")",
            P + " frame n=2 NOTI/902(" + Total(6) + ") innerCopySkipped",
            P + " frame n=3 NOTI/901(" + Total(7) + ")",
        }, Observed(run.Lines));
        Assert.Equal(1, run.Result.InnerCopiesSkipped);
        Assert.Equal(1, run.Result.EnvelopesOpened);
    }

    [Fact]
    public async Task Only_the_frame_that_matches_the_inner_frame_is_marked_and_the_marker_neither_repeats_nor_crosses_a_batch()
    {
        byte[] inner = GatewayTestSupport.Pattern(6, 0x31, 1);
        byte[] other = GatewayTestSupport.Pattern(6, 0x32, 1);
        Run run = await RunFake(s =>
        {
            // 一批里:信封、与内层相同的副本、再来一帧同样内容的(第二份不是"紧跟信封的副本",不该被跳过也不该带标记)
            s.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 902, inner), CombatKit.DecodedPlain(CombatKit.Noti, 902, inner));
            // 一批里:信封之后跟的是别的内容(同包号不同载荷):不是副本
            s.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 902, other));
            // 信封与副本分在两批:标记不跨批,副本当普通帧
            s.PushUnit(Env().Decoded());
            s.PushUnit(CombatKit.DecodedPlain(CombatKit.Noti, 902, inner));
        }, _ => DungeonEntryWire.Inner(CombatKit.Noti, 902, inner), true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 opened inner=NOTI/902(" + Total(6) + ")",
            P + " frame n=2 NOTI/902(" + Total(6) + ") innerCopySkipped",
            P + " frame n=3 NOTI/902(" + Total(6) + ")",
            P + " frame n=4 NOTI/561(" + Total(20) + ")",
            P + " envelope n=4 opened inner=NOTI/902(" + Total(6) + ")",
            P + " frame n=5 NOTI/902(" + Total(6) + ")",
            P + " frame n=6 NOTI/561(" + Total(20) + ")",
            P + " envelope n=6 opened inner=NOTI/902(" + Total(6) + ")",
            P + " frame n=7 NOTI/902(" + Total(6) + ")",
        }, Observed(run.Lines));
        Assert.Equal(1, run.Result.InnerCopiesSkipped);
        Assert.Equal(7, run.Result.FramesSeen);
    }

    [Fact]
    public async Task An_envelope_around_a_command_frame_names_the_inner_class_in_capitals()
    {
        Run run = await RunFake(s => s.Push(Env()), _ => DungeonEntryWire.Inner(CombatKit.Cmd, 905, GatewayTestSupport.Pattern(3, 0x61, 1)), true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 opened inner=CMD/905(" + Total(3) + ")",
        }, Observed(run.Lines));
    }

    [Fact]
    public async Task An_envelope_inside_an_envelope_is_logged_as_opened_and_not_unwrapped_again()
    {
        int calls = 0;
        Run run = await RunFake(s => s.Push(Env()), _ =>
        {
            calls++;
            return DungeonEntryWire.Inner(CombatKit.Noti, 561, GatewayTestSupport.Pattern(5, 0x71, 1));
        }, true);

        Assert.Equal(1, calls);
        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 opened inner=NOTI/561(" + Total(5) + ")",
        }, Observed(run.Lines));
    }

    // ---- 五种没拆开的原因,逐字 ----

    [Fact]
    public async Task Without_an_unwrap_delegate_the_envelope_is_unopened_with_reason_noUnwrap()
    {
        Run run = await RunFake(s => s.Push(Env()), null, true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 unopened reason=noUnwrap",
        }, Observed(run.Lines));
        Assert.Equal(1, run.Result.EnvelopesUnopened);
    }

    [Fact]
    public async Task A_throwing_unwrap_delegate_is_unopened_with_reason_unwrapThrew_and_the_message_is_not_printed()
    {
        Run run = await RunFake(s => s.Push(Env()), _ => throw new InvalidOperationException("synthetic-secret-message"), true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 unopened reason=unwrapThrew",
        }, Observed(run.Lines));
        Assert.DoesNotContain(run.Lines, l => l.Contains("synthetic-secret-message") || l.Contains("InvalidOperation"));
        Assert.Equal(1, run.Result.EnvelopesUnopened);
    }

    [Fact]
    public async Task An_unwrap_delegate_returning_null_is_unopened_with_reason_unwrapNull()
    {
        Run run = await RunFake(s => s.Push(Env()), _ => null, true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 unopened reason=unwrapNull",
        }, Observed(run.Lines));
        Assert.Equal(1, run.Result.EnvelopesUnopened);
    }

    public static TheoryData<string> BadInnerHeaders()
    {
        return new TheoryData<string> { "too-short", "total-below-header", "total-above-length" };
    }

    [Theory]
    [MemberData(nameof(BadInnerHeaders))]
    public async Task An_inner_frame_with_an_unreadable_header_or_a_bad_total_is_unopened_with_reason_innerHeaderBad(string kind)
    {
        byte[] bad;
        switch (kind)
        {
            case "too-short":
                bad = GatewayTestSupport.Pattern(10, 0xC9, 1);                               // 不够一个帧头
                break;
            case "total-below-header":
                bad = DungeonEntryWire.Inner(CombatKit.Noti, 902, GatewayTestSupport.Pattern(6, 0x31, 1));
                BitConverter.GetBytes(10u).CopyTo(bad, 3);                                    // 声明总长小于帧头
                break;
            default:
                bad = DungeonEntryWire.Inner(CombatKit.Noti, 902, GatewayTestSupport.Pattern(6, 0x31, 1));
                BitConverter.GetBytes((uint)bad.Length + 1).CopyTo(bad, 3);                   // 声明总长大于实际字节数
                break;
        }

        Run run = await RunFake(s => s.Push(Env()), _ => bad, true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 unopened reason=innerHeaderBad",
        }, Observed(run.Lines));
        Assert.Equal(1, run.Result.EnvelopesUnopened);
        Assert.Equal(0, run.Result.EnvelopesOpened);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(87)]
    public async Task A_compressed_inner_frame_is_unopened_with_reason_innerCompressed_and_names_the_inner_frame(int id)
    {
        byte[] payload = GatewayTestSupport.Pattern(13, 0x81, 1);
        Run run = await RunFake(s => s.Push(Env()), _ => DungeonEntryWire.Inner(CombatKit.Noti, (ushort)id, payload), true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/561(" + Total(20) + ")",
            P + " envelope n=1 unopened reason=innerCompressed inner=NOTI/" + id + "(" + Total(13) + ")",
        }, Observed(run.Lines));
        Assert.Equal(1, run.Result.EnvelopesUnopened);
        Assert.Equal(0, run.Result.InnerCopiesSkipped);
    }

    [Fact]
    public async Task The_envelope_line_carries_the_number_of_its_own_frame_across_batches()
    {
        int calls = 0;
        Run run = await RunFake(s =>
        {
            s.Push(CombatKit.Unrelated(901));
            s.Push(CombatKit.Unrelated(902));
            s.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 903, GatewayTestSupport.Pattern(4, 0x11, 1)));
            s.Push(CombatKit.Unrelated(904));
            s.Push(Env());
        }, _ => ++calls == 1 ? DungeonEntryWire.Inner(CombatKit.Noti, 903, GatewayTestSupport.Pattern(4, 0x11, 1)) : null, true);

        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/901(" + Total(7) + ")",
            P + " frame n=2 NOTI/902(" + Total(7) + ")",
            P + " frame n=3 NOTI/561(" + Total(20) + ")",
            P + " envelope n=3 opened inner=NOTI/903(" + Total(4) + ")",
            P + " frame n=4 NOTI/903(" + Total(4) + ") innerCopySkipped",
            P + " frame n=5 NOTI/904(" + Total(7) + ")",
            P + " frame n=6 NOTI/561(" + Total(20) + ")",
            P + " envelope n=6 unopened reason=unwrapNull",
        }, Observed(run.Lines));
    }

    // ================= 不含载荷 =================

    // 一串带可识别载荷字节的合成帧:每个载荷的字节都 ≥ 200(十进制三位),且互不相同的起点,方便在输出里找
    private sealed class Marked
    {
        public byte[][] Payloads;
        public Queue<object> Unwraps;
    }

    private static Marked MarkedPayloads(Func<int, int, byte[]> make)
    {
        // 载荷:0 普通帧 901、1 命令帧 902、2 信封一的封内体、3 信封一的内层载荷(可拆开)、4 信封二的封内体、5 信封二的内层载荷(压缩包号 9,不拆)、
        //       6 信封三的封内体、7 信封三交出的坏内层(不够一个帧头)
        int[] lengths = { 11, 9, 10, 8, 10, 10, 10, 9 };
        var payloads = new byte[lengths.Length][];
        for (int i = 0; i < lengths.Length; i++)
        {
            payloads[i] = make(i, lengths[i]);
        }
        var unwraps = new Queue<object>();
        unwraps.Enqueue(DungeonEntryWire.Inner(CombatKit.Noti, 903, payloads[3]));
        unwraps.Enqueue(DungeonEntryWire.Inner(CombatKit.Noti, 9, payloads[5]));
        unwraps.Enqueue(payloads[7]);
        return new Marked { Payloads = payloads, Unwraps = unwraps };
    }

    private static byte[] Identifiable(int index, int length) => GatewayTestSupport.Pattern(length, 0xC8 + index * 2, 3);       // 200 起、步长 3、最长 11 字节:最大 244,不回绕

    private static byte[] Zeros(int index, int length) => new byte[length];

    private static Task<Run> RunMarked(Marked m)
    {
        return RunFake(s =>
        {
            s.Push(Plain(901, m.Payloads[0]));
            s.Push(new CombatKit.ServerFrame(CombatKit.Cmd, 902, m.Payloads[1]));
            s.PushUnit(Env(m.Payloads[2]).Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 903, m.Payloads[3]));
            s.Push(Env(m.Payloads[4]));
            s.Push(Env(m.Payloads[6]));
        }, _ => (byte[])m.Unwraps.Dequeue(), true);
    }

    [Fact]
    public async Task No_payload_content_reaches_the_output_in_any_decimal_or_hex_spelling()
    {
        Marked m = MarkedPayloads(Identifiable);
        Run run = await RunMarked(m);

        // 先确认帧都走到了(否则没有东西可漏):帧行与信封行俱全
        Assert.Equal(new[]
        {
            P + " frame n=1 NOTI/901(" + Total(11) + ")",
            P + " frame n=2 CMD/902(" + Total(9) + ")",
            P + " frame n=3 NOTI/561(" + Total(10) + ")",
            P + " envelope n=3 opened inner=NOTI/903(" + Total(8) + ")",
            P + " frame n=4 NOTI/903(" + Total(8) + ") innerCopySkipped",
            P + " frame n=5 NOTI/561(" + Total(10) + ")",
            P + " envelope n=5 unopened reason=innerCompressed inner=NOTI/9(" + Total(10) + ")",
            P + " frame n=6 NOTI/561(" + Total(10) + ")",
            P + " envelope n=6 unopened reason=innerHeaderBad",
        }, Observed(run.Lines));

        string all = string.Join("\n", run.Lines);
        string[] tokens = Regex.Split(all, "[^0-9A-Za-z]+").Where(t => t.Length > 0).ToArray();
        foreach (byte[] payload in m.Payloads)
        {
            Assert.All(payload, b => Assert.True(b >= 200, "夹具的载荷字节应当 ≥ 200,好与帧号、总长区分"));
            // 整段载荷的各种常见写法:十六进制(大小写、带不带分隔)、十进制(逗号、空格、连写、方括号)、0x 前缀
            string[] spellings =
            {
                Convert.ToHexString(payload), Convert.ToHexString(payload).ToLowerInvariant(),
                BitConverter.ToString(payload), BitConverter.ToString(payload).ToLowerInvariant(), BitConverter.ToString(payload).Replace("-", " "),
                string.Join(",", payload), string.Join(", ", payload), string.Join(" ", payload), string.Join("", payload), "[" + string.Join(",", payload) + "]",
                string.Join(", ", payload.Select(b => "0x" + b.ToString("X2"))), string.Join(",", payload.Select(b => "0x" + b.ToString("x2"))),
            };
            foreach (string spelling in spellings)
            {
                Assert.DoesNotContain(spelling, all, StringComparison.Ordinal);
            }
            // 单个字节:作为独立的十进制记号或两位十六进制记号都不该出现
            foreach (byte b in payload)
            {
                Assert.DoesNotContain(tokens, t => t == b.ToString() || string.Equals(t, b.ToString("X2"), StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task The_output_does_not_depend_on_the_payload_bytes_only_on_their_lengths()
    {
        Run identifiable = await RunMarked(MarkedPayloads(Identifiable));
        Run zeros = await RunMarked(MarkedPayloads(Zeros));
        Run otherBytes = await RunMarked(MarkedPayloads((i, n) => GatewayTestSupport.Pattern(n, 0x90 + i, 11)));

        Assert.NotEmpty(Observed(identifiable.Lines));
        Assert.Equal(identifiable.Lines, zeros.Lines);
        Assert.Equal(identifiable.Lines, otherBytes.Lines);
    }

    // ================= 行数上限 =================

    private static void FeedPlain(CombatFakeSession session, int count)
    {
        for (int i = 0; i < count; i++)
        {
            session.Push(CombatKit.Unrelated(901));
        }
    }

    [Fact]
    public async Task Exactly_400_frames_are_all_logged_and_no_omitted_line_is_added()
    {
        Run run = await RunFake(s => FeedPlain(s, 400));

        string[] frames = Observed(run.Lines);
        Assert.Equal(400, frames.Length);
        Assert.Equal(P + " frame n=1 NOTI/901(" + Total(7) + ")", frames[0]);
        Assert.Equal(P + " frame n=400 NOTI/901(" + Total(7) + ")", frames[399]);
        Assert.DoesNotContain(run.Lines, l => l.Contains("omitted"));
        Assert.Equal(400, run.Result.FramesSeen);
        Assert.StartsWith(P + " ended ", run.Lines[^1], StringComparison.Ordinal);
        Assert.Contains(" frames=400 ", run.Lines[^1]);
    }

    [Fact]
    public async Task The_401st_frame_is_counted_but_not_logged_and_one_omitted_line_comes_right_before_the_end_line()
    {
        Run run = await RunFake(s => FeedPlain(s, 401));

        string[] frames = Observed(run.Lines);
        Assert.Equal(400, frames.Length);
        Assert.Equal(P + " frame n=400 NOTI/901(" + Total(7) + ")", frames[399]);
        Assert.DoesNotContain(run.Lines, l => l.Contains("frame n=401"));
        Assert.Equal(P + " frame omitted=1", run.Lines[^2]);
        Assert.Equal(1, run.Lines.Count(l => l.Contains("omitted")));
        Assert.StartsWith(P + " ended ", run.Lines[^1], StringComparison.Ordinal);
        Assert.Contains(" frames=401 ", run.Lines[^1]);
        Assert.Equal(401, run.Result.FramesSeen);
    }

    [Fact]
    public async Task Frames_far_beyond_the_cap_all_count_into_the_omitted_line()
    {
        Run run = await RunFake(s => FeedPlain(s, 450));

        Assert.Equal(400, Observed(run.Lines).Length);
        Assert.Equal(P + " frame omitted=50", run.Lines[^2]);
        Assert.Contains(" frames=450 ", run.Lines[^1]);
        Assert.Equal(450, run.Result.FramesSeen);
        Assert.Equal(450, run.Result.PassthroughCount);                 // 上限只管日志,计数照旧
    }

    [Fact]
    public async Task An_envelope_on_the_400th_frame_keeps_its_envelope_line_while_its_copy_on_the_401st_frame_is_only_counted()
    {
        byte[] inner = GatewayTestSupport.Pattern(6, 0x31, 1);
        Run run = await RunFake(s =>
        {
            FeedPlain(s, 399);
            s.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 902, inner));
        }, _ => DungeonEntryWire.Inner(CombatKit.Noti, 902, inner), true);

        string[] observed = Observed(run.Lines);
        Assert.Equal(P + " frame n=400 NOTI/561(" + Total(20) + ")", observed[^2]);
        Assert.Equal(P + " envelope n=400 opened inner=NOTI/902(" + Total(6) + ")", observed[^1]);
        Assert.DoesNotContain(run.Lines, l => l.Contains("n=401"));
        Assert.Equal(P + " frame omitted=1", run.Lines[^2]);
        Assert.Equal(1, run.Result.InnerCopiesSkipped);                 // 第 401 帧照样被认作副本跳过,只是不写行
        Assert.Equal(401, run.Result.FramesSeen);
    }

    [Fact]
    public async Task An_envelope_past_the_cap_writes_no_envelope_line_and_only_its_frames_count_as_omitted()
    {
        byte[] inner = GatewayTestSupport.Pattern(6, 0x31, 1);
        Run run = await RunFake(s =>
        {
            FeedPlain(s, 400);
            s.PushUnit(Env().Decoded(), CombatKit.DecodedPlain(CombatKit.Noti, 902, inner));
        }, _ => DungeonEntryWire.Inner(CombatKit.Noti, 902, inner), true);

        Assert.Equal(400, Observed(run.Lines).Length);
        Assert.DoesNotContain(run.Lines, l => l.Contains(" envelope "));
        Assert.Equal(P + " frame omitted=2", run.Lines[^2]);           // 信封帧与它的副本,两帧
        Assert.Equal(1, run.Result.EnvelopesOpened);                    // 拆信封照做,只是不写行
        Assert.Equal(1, run.Result.InnerCopiesSkipped);
        Assert.Equal(402, run.Result.FramesSeen);
    }

    [Fact]
    public async Task The_omitted_line_stays_before_the_end_line_and_the_other_lines_keep_their_order()
    {
        Run run = await RunFake(s => FeedPlain(s, 405));

        string[] withoutFrames = WithoutFrameLog(run.Lines);
        Assert.Equal(P + " begin monsters=2 duplicateNumbers=0", run.Lines[0]);
        Assert.Equal(2, withoutFrames.Length);                           // 开始、收场
        Assert.Equal(P + " frame omitted=5", run.Lines[^2]);
        Assert.StartsWith(P + " ended ", run.Lines[^1], StringComparison.Ordinal);
    }

    // ================= 真会话上的重放:放弃之后真服推来的那一串帧 =================

    private static async Task<Run> RunReal(byte[][] window, bool? frameLog)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame => frame.Class == PacketClass.Cmd && frame.Id == GiveUpKit.GiveUpId ? window : null;
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.GiveUpTimeout = TimeSpan.FromSeconds(8);
        if (frameLog.HasValue)
        {
            options.FrameLog = frameLog.Value;
        }
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, options);
        Assert.True(runner.PostGiveUp());
        var log = new HostTestKit.LogSink();
        OnlineDungeonCombatResult result = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(CombatKit.Sequential(2)), log.Add), 20);
        return new Run { Result = result, Lines = log.Lines };
    }

    public static TheoryData<AreaUsersShape> Shapes()
    {
        return new TheoryData<AreaUsersShape> { AreaUsersShape.Unparseable, AreaUsersShape.Parseable };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_real_window_logs_the_nine_frames_and_the_four_envelopes_exactly_as_the_server_sent_them(AreaUsersShape shape)
    {
        byte[][] wire = RealWire(AreaUsers(shape));            // 信封 0/9、普通 0/24、信封 0/3、信封 0/36、信封 0/2
        Run run = await RunReal(wire, true);

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, run.Result.Outcome);
        string[] expected =
        {
            P + " begin monsters=2 duplicateNumbers=0",
            P + " event=GiveUpSent",
            P + " frame n=1 NOTI/561(" + wire[0].Length + ")",
            P + " envelope n=1 unopened reason=innerCompressed inner=NOTI/9(" + Total9 + ")",
            P + " frame n=2 NOTI/9(" + Total9 + ")",
            P + " frame n=3 NOTI/24(" + Total24 + ")",
            P + " frame n=4 NOTI/561(" + wire[2].Length + ")",
            P + " envelope n=4 opened inner=NOTI/3(" + Total3 + ")",
            P + " frame n=5 NOTI/3(" + Total3 + ") innerCopySkipped",
            P + " frame n=6 NOTI/561(" + wire[3].Length + ")",
            P + " envelope n=6 opened inner=NOTI/36(" + Total36 + ")",
            P + " frame n=7 NOTI/36(" + Total36 + ") innerCopySkipped",
            P + " frame n=8 NOTI/561(" + wire[4].Length + ")",
            P + " envelope n=8 unopened reason=innerCompressed inner=NOTI/2(" + Total2 + ")",
            P + " frame n=9 NOTI/2(" + Total2 + ")",
            P + " event=ReturnedToTown userArea=0 areaUsers=" + (shape == AreaUsersShape.Unparseable ? "-" : "1"),
            P + " ended outcome=GaveUp phase=Fighting monsters=2 sent=0 confirmed=0 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
                "passthrough=8 heartbeats=0 envelopes=2/2 innerSkipped=2 frames=9 passIds=NOTI/2:1,NOTI/3:1,NOTI/9:1,NOTI/36:1,NOTI/561:4",
        };
        Assert.Equal(expected, run.Lines);
    }

    [Fact]
    public async Task The_real_window_without_the_option_prints_the_four_old_lines_and_the_switch_changes_nothing_else()
    {
        byte[][] wire = RealWire(AreaUsers(AreaUsersShape.Unparseable));
        Run off = await RunReal(wire, null);
        Run on = await RunReal(RealWire(AreaUsers(AreaUsersShape.Unparseable)), true);

        Assert.Equal(4, off.Lines.Length);
        Assert.Empty(Observed(off.Lines));
        Assert.Equal(off.Lines, WithoutFrameLog(on.Lines));
        Assert.Equal(off.Result.Outcome, on.Result.Outcome);
        Assert.Equal(off.Result.FramesSeen, on.Result.FramesSeen);
        Assert.Equal(off.Result.PassthroughCount, on.Result.PassthroughCount);
        Assert.Equal(off.Result.EnvelopesOpened, on.Result.EnvelopesOpened);
        Assert.Equal(off.Result.EnvelopesUnopened, on.Result.EnvelopesUnopened);
        Assert.Equal(off.Result.InnerCopiesSkipped, on.Result.InnerCopiesSkipped);
        Assert.Equal(off.Result.UserArea == null, on.Result.UserArea == null);
        Assert.Equal(off.Result.AreaUsers == null, on.Result.AreaUsers == null);
    }

    // ================= 行的拼法(纯函数) =================

    [Fact]
    public void The_line_builders_produce_exactly_the_documented_formats()
    {
        Assert.Equal(P + " frame n=1 NOTI/9(32)", OnlineCombatLog.Frame(1, PacketClass.Noti, 9, 32, false));
        Assert.Equal(P + " frame n=12 CMD/45(13) innerCopySkipped", OnlineCombatLog.Frame(12, PacketClass.Cmd, 45, 13, true));
        Assert.Equal(P + " frame n=3 类7/901(22)", OnlineCombatLog.Frame(3, (PacketClass)7, 901, 22, false));
        Assert.Equal(P + " envelope n=4 opened inner=NOTI/3(23)", OnlineCombatLog.EnvelopeOpened(4, PacketClass.Noti, 3, 23));
        Assert.Equal(P + " envelope n=5 unopened reason=noUnwrap", OnlineCombatLog.EnvelopeUnopened(5, OnlineCombatLog.ReasonNoUnwrap));
        Assert.Equal(P + " envelope n=5 unopened reason=unwrapThrew", OnlineCombatLog.EnvelopeUnopened(5, OnlineCombatLog.ReasonUnwrapThrew));
        Assert.Equal(P + " envelope n=5 unopened reason=unwrapNull", OnlineCombatLog.EnvelopeUnopened(5, OnlineCombatLog.ReasonUnwrapNull));
        Assert.Equal(P + " envelope n=5 unopened reason=innerHeaderBad", OnlineCombatLog.EnvelopeUnopened(5, OnlineCombatLog.ReasonInnerHeaderBad));
        Assert.Equal(P + " envelope n=8 unopened reason=innerCompressed inner=NOTI/2(72)", OnlineCombatLog.EnvelopeUnopenedCompressed(8, PacketClass.Noti, 2, 72));
        Assert.Equal(P + " frame omitted=7", OnlineCombatLog.FramesOmitted(7));
        Assert.Equal("innerCompressed", OnlineCombatLog.ReasonInnerCompressed);
        Assert.StartsWith(OnlineCombatLog.Prefix + " ", OnlineCombatLog.Frame(1, PacketClass.Noti, 9, 32, false), StringComparison.Ordinal);
    }
}
