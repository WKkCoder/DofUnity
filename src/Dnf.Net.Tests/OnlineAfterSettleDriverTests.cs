using System.IO.Compression;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 无头结算后菜单驱动(<see cref="OnlineAfterSettleDriver"/>)测试:
/// 覆盖 1/72 → 1/73 → (1/74) → 1/75 → 回城信号(0/2 或 0/23+0/24)、无有权槽跳过 1/74、多有权槽取第一个、
/// flag=2 UI 同步忽略、命令号不匹配忽略、失败码、各步超时(5s/6s/8s)、畸形回包、多余帧计数、心跳、561 信封与真实会话管道。
/// 所有数值均为合成编号(900+ / 9100+)。
/// </summary>
public class OnlineAfterSettleDriverTests
{
    private const ushort Pkt72 = 72;
    private const ushort Pkt73 = 73;
    private const ushort Pkt74 = 74;
    private const ushort Pkt75 = 75;

    private static CombatKit.ServerFrame Reply72Ok(byte status = 1) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt72, new[] { status });

    private static CombatKit.ServerFrame Reply73(short s0, short s1, short s2, short s3, byte status = 1) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt73, new NoticePayloadBuilder()
            .U8(status)
            .U16(unchecked((ushort)s0))
            .U16(unchecked((ushort)s1))
            .U16(unchecked((ushort)s2))
            .U16(unchecked((ushort)s3))
            .ToArray());

    private static CombatKit.ServerFrame Reply75Ok(byte flag = 1, byte cmd = 2) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt75, new byte[] { 1, flag, cmd });

    private static CombatKit.ServerFrame Reply75Fail(byte errorCode, byte cmd = 2) =>
        new CombatKit.ServerFrame(CombatKit.Cmd, Pkt75, new byte[] { 0, errorCode, cmd });

    private static byte[] RawNoti2Payload() =>
        new NoticePayloadBuilder().U8(2).U8(0).U8(0).U8(0).U32(0).U16(0).ToArray();

    private static CombatKit.ServerFrame Noti2() =>
        new CombatKit.ServerFrame(CombatKit.Noti, 2, ZlibCompress(RawNoti2Payload()));

    private static CombatKit.ServerFrame Noti23(ushort uid = 9101, byte village = 9, byte area = 2) =>
        new CombatKit.ServerFrame(CombatKit.Noti, 23, new NoticePayloadBuilder()
            .U16(uid).U8(village).U8(area).U16(300).U16(200).U8(1).U8(1).ToArray());

    private static CombatKit.ServerFrame Noti24(byte village = 9, byte area = 2) =>
        new CombatKit.ServerFrame(CombatKit.Noti, 24, new NoticePayloadBuilder()
            .U8(village).U8(area).U16(0).ToArray());

    private static OnlineAfterSettleOptions FastOptions(ManualFightClock clock = null, EplpCommand cmd = EplpCommand.ReturnToTown)
    {
        return new OnlineAfterSettleOptions
        {
            Command = cmd,
            PollInterval = TimeSpan.FromMilliseconds(50),
            Clock = clock ?? new ManualFightClock(),
        };
    }

    // ======== 选项与单次运行检查 ========

    [Fact]
    public void Options_and_constructor_validate_defaults_and_bounds()
    {
        var o = new OnlineAfterSettleOptions();
        Assert.Equal(EplpCommand.ReturnToTown, o.Command);
        Assert.Equal(TimeSpan.FromSeconds(5), o.AdvanceReplyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), o.EplpReplyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(6), o.TownReturnWaitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), o.ReturnWaitTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(50), o.PollInterval);
        Assert.Null(o.Clock);

        var session = new CombatFakeSession();
        Assert.Throws<ArgumentNullException>(() => new OnlineAfterSettleDriver((IOnlineDungeonSession)null));
        Assert.Throws<ArgumentNullException>(() => new OnlineAfterSettleDriver((GameClientSession)null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { Command = (EplpCommand)4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { AdvanceReplyTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { EplpReplyTimeout = TimeSpan.FromDays(40) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { TownReturnWaitTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { ReturnWaitTimeout = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { PollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { AdvanceReplyTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1) }));

        // 最大已知命令 OpenSecretShop(3) 与 int.MaxValue 毫秒边界均合法
        Assert.NotNull(new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions
        {
            Command = EplpCommand.OpenSecretShop,
            AdvanceReplyTimeout = TimeSpan.FromMilliseconds(int.MaxValue),
        }));
    }

    [Fact]
    public async Task Driver_cannot_be_run_twice()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, 0, -1, -1) },
            Pkt75 => new[] { Reply75Ok(), Noti2() },
            _ => null,
        };
        var driver = new OnlineAfterSettleDriver(session, FastOptions());
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);
        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync());
    }

    // ======== 正常流程与选牌槽位选择 ========

    [Theory]
    [InlineData(new short[] { 1, 1, 1, 1 }, (byte)0)]
    [InlineData(new short[] { 0, 1, 1, 0 }, (byte)1)]
    [InlineData(new short[] { -1, 0, 1, 1 }, (byte)2)]
    [InlineData(new short[] { -1, 0, 0, 1 }, (byte)3)]
    public async Task Happy_path_picks_first_eligible_slot_advances_immediately_on_74_and_returns_on_noti2(short[] slots, byte expectedSlot)
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(slots[0], slots[1], slots[2], slots[3]) },
            // 故意不回 CMD/74: 1/74 发成功即过, 不等回包
            Pkt75 => new[] { Reply75Ok(1, 2), Noti2() },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.True(s.ReturnedToTown);
        Assert.False(s.CardSelectSkipped);
        Assert.Equal(expectedSlot, s.SelectedCardIndex);
        Assert.Null(s.EplpErrorCode);

        // 每条命令只发一次: 72, 73, 74, 75
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, session.Sent.Select(x => x.Id).ToArray());
        Assert.Equal(Array.Empty<byte>(), session.Sent[0].Payload);
        Assert.Equal(Array.Empty<byte>(), session.Sent[1].Payload);
        Assert.Equal(new byte[] { 0, expectedSlot }, session.Sent[2].Payload);
        Assert.Equal(new byte[] { 1, 2 }, session.Sent[3].Payload);

        // 逐字核对 5 条日志行
        Assert.Equal(
            new[]
            {
                "PROBE_FIGHT stage=settle-menu step=72 sent=1 reply=ok elapsedMs=0 ignored=0",
                "PROBE_FIGHT stage=settle-menu step=73 sent=1 reply=ok elapsedMs=0 ignored=0",
                "PROBE_FIGHT stage=settle-menu step=74 sent=1 reply=ok elapsedMs=0 ignored=0",
                "PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=ok elapsedMs=0 ignored=0",
                "PROBE_FIGHT stage=settle-menu step=return sent=0 reply=ok elapsedMs=0 ignored=0",
            },
            log.Lines);
    }

    [Fact]
    public async Task No_eligible_card_slot_skips_74_logs_skipped_and_still_sends_75_and_returns()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(-1, 0, 0, -1) },
            Pkt75 => new[] { Reply75Ok(), Noti2() },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.True(s.CardSelectSkipped);
        Assert.Null(s.SelectedCardIndex);
        Assert.Equal(new ushort[] { 72, 73, 75 }, session.Sent.Select(x => x.Id).ToArray());
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=skipped elapsedMs=0 ignored=0", log.Lines[2]);
    }

    // ======== 回城信号组合:0/2 与 0/23+0/24 ========

    [Fact]
    public async Task Return_succeeds_on_noti23_and_noti24_pair_in_either_order()
    {
        foreach (bool reverseOrder in new[] { false, true })
        {
            var session = new CombatFakeSession();
            session.Respond = (cls, id, _) => id switch
            {
                Pkt72 => new[] { Reply72Ok() },
                Pkt73 => new[] { Reply73(1, 0, 0, 0) },
                Pkt75 => reverseOrder
                    ? new[] { Reply75Ok(), Noti24(), Noti23() }
                    : new[] { Reply75Ok(), Noti23(), Noti24() },
                _ => null,
            };
            var driver = new OnlineAfterSettleDriver(session, FastOptions());

            OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);

            Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
            Assert.Equal(0, s.IgnoredFrames);
        }
    }

    [Fact]
    public async Task Only_noti23_or_only_noti24_without_the_other_times_out_at_town_return_window()
    {
        foreach (CombatKit.ServerFrame partialFrame in new[] { Noti23(), Noti24() })
        {
            var clock = new ManualFightClock();
            var session = new CombatFakeSession();
            session.Respond = (cls, id, _) => id switch
            {
                Pkt72 => new[] { Reply72Ok() },
                Pkt73 => new[] { Reply73(1, -1, -1, -1) },
                Pkt75 => new[] { Reply75Ok(), partialFrame },
                _ => null,
            };
            var log = new HostTestKit.LogSink();
            var driver = new OnlineAfterSettleDriver(session, FastOptions(clock));

            Task<OnlineAfterSettleSummary> run = Task.Run(() => driver.RunAsync(log.Add));
            await CombatKit.WaitUntil(() => clock.HasPending(), "停在等待回城");

            // 差 1ms 不到 6 秒:不能超时
            clock.Advance(TimeSpan.FromMilliseconds(5950));
            await CombatKit.WaitUntil(() => clock.HasPending(), "还差 50ms 到 6 秒");
            Assert.False(run.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(50));
            OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(run, 10);

            Assert.Equal(OnlineAfterSettleOutcome.ReturnTimeout, s.Outcome);
            Assert.Equal(1, session.Commands(Pkt75)); // 绝不自动重发
            Assert.Equal("PROBE_FIGHT stage=settle-menu step=return sent=0 reply=timeout elapsedMs=6000 ignored=0", log.Lines[4]);
        }
    }

    [Fact]
    public async Task Non_town_command_uses_8_second_return_timeout_and_sends_matching_command()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, -1, -1, -1) },
            Pkt75 => new[] { Reply75Ok(1, 0) }, // Retry = 0
            _ => null,
        };
        var driver = new OnlineAfterSettleDriver(session, FastOptions(clock, EplpCommand.Retry));

        Task<OnlineAfterSettleSummary> run = Task.Run(() => driver.RunAsync());
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等待回城");

        clock.Advance(TimeSpan.FromSeconds(6));
        await CombatKit.WaitUntil(() => clock.HasPending(), "非 town 用 8 秒上限, 6 秒时不超时");
        Assert.False(run.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(2));
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnTimeout, s.Outcome);
        Assert.Equal(new byte[] { 1, 0 }, session.Sent.Last().Payload);
    }

    // ======== 1/75 Flag=2 UI 同步、命令不匹配与失败码 ========

    [Fact]
    public async Task Eplp_flag2_ui_sync_and_other_command_replies_are_ignored_until_execute_reply_arrives()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(0, 1, -1, -1) },
            Pkt75 => new[]
            {
                Reply75Ok(flag: 2, cmd: 2), // UI 光标同步回显:必须忽略
                Reply75Ok(flag: 1, cmd: 0), // 别的命令的回包:必须忽略
                Reply75Ok(flag: 1, cmd: 2), // 真正的执行确认
                Noti2(),
            },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal(2, s.IgnoredFrames);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=ok elapsedMs=0 ignored=2", log.Lines[3]);
    }

    [Fact]
    public async Task Eplp_flag2_only_without_flag1_times_out_at_5_seconds()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, 0, 0, 0) },
            Pkt75 => new[] { Reply75Ok(flag: 2, cmd: 2) },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions(clock));

        Task<OnlineAfterSettleSummary> run = Task.Run(() => driver.RunAsync(log.Add));
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等 75 执行确认");

        clock.Advance(TimeSpan.FromSeconds(5));
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineAfterSettleOutcome.EplpTimeout, s.Outcome);
        Assert.Equal(1, s.IgnoredFrames);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=timeout elapsedMs=5000 ignored=1", log.Lines[3]);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0x85)]
    [InlineData(1)]
    public async Task Eplp_failure_reply_returns_ReplyFailed_without_retrying(byte errorCode)
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, -1, -1, -1) },
            Pkt75 => new[] { Reply75Fail(errorCode, cmd: 2) },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReplyFailed, s.Outcome);
        Assert.Equal(errorCode, s.EplpErrorCode);
        Assert.Equal(1, session.Commands(Pkt75));
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=failed elapsedMs=0 ignored=0", log.Lines[3]);
        Assert.Equal(4, log.Lines.Length);
    }

    // ======== 72 / 73 超时与畸形回包 ========

    [Fact]
    public async Task Score_scroll_72_timeout_at_5_seconds_and_single_send()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions(clock));

        Task<OnlineAfterSettleSummary> run = Task.Run(() => driver.RunAsync(log.Add));
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等 72");

        clock.Advance(TimeSpan.FromMilliseconds(4950));
        await CombatKit.WaitUntil(() => clock.HasPending(), "4950ms 还没超时");
        Assert.False(run.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineAfterSettleOutcome.ScoreScrollTimeout, s.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Elapsed);
        Assert.Equal(1, session.Commands(Pkt72));
        Assert.Single(log.Lines);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=72 sent=1 reply=timeout elapsedMs=5000 ignored=0", log.Lines[0]);
    }

    [Fact]
    public async Task Card_right_73_timeout_at_5_seconds_and_single_send()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : null;
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions(clock));

        Task<OnlineAfterSettleSummary> run = Task.Run(() => driver.RunAsync(log.Add));
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等 73");

        clock.Advance(TimeSpan.FromSeconds(5));
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineAfterSettleOutcome.CardRightTimeout, s.Outcome);
        Assert.Equal(1, session.Commands(Pkt72));
        Assert.Equal(1, session.Commands(Pkt73));
        Assert.Equal(0, session.Commands(Pkt74));
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=73 sent=1 reply=timeout elapsedMs=5000 ignored=0", log.Lines[1]);
    }

    [Theory]
    [InlineData(72, new byte[0])]
    [InlineData(72, new byte[] { 0 })]
    [InlineData(72, new byte[] { 2 })]
    [InlineData(73, new byte[] { 1, 1, 0 })]
    [InlineData(73, new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(75, new byte[] { 1, 1 })]
    [InlineData(75, new byte[] { 2, 1, 2 })]
    public async Task Malformed_reply_on_any_step_returns_ReplyMalformed(ushort badStep, byte[] badPayload)
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) =>
        {
            if (id == badStep)
            {
                return new[] { new CombatKit.ServerFrame(CombatKit.Cmd, badStep, badPayload) };
            }
            return id switch
            {
                Pkt72 => new[] { Reply72Ok() },
                Pkt73 => new[] { Reply73(1, -1, -1, -1) },
                Pkt75 => new[] { Reply75Ok() },
                _ => null,
            };
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReplyMalformed, s.Outcome);
        string stepName = badStep.ToString();
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=" + stepName + " sent=1 reply=malformed elapsedMs=0 ignored=0", log.Lines.Last());
    }

    // ======== 多余帧与心跳 ========

    [Fact]
    public async Task Extra_frames_and_heartbeats_are_handled_across_steps_without_leaking_payloads()
    {
        var session = new CombatFakeSession();
        session.Push(
            new CombatKit.ServerFrame(CombatKit.Noti, 901, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
            Noti2()); // 75 还没发出去之前来的 0/2 不算回城信号, 当无关帧忽略
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { CombatKit.Heartbeat(), Reply72Ok(), CombatKit.Unrelated(902) },
            Pkt73 => new[] { CombatKit.Unrelated(903), CombatKit.Heartbeat(), Reply73(0, 0, 1, -1) },
            Pkt75 => new[] { CombatKit.Unrelated(904), Reply75Ok(), CombatKit.Heartbeat(), CombatKit.Unrelated(905), Noti2() },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal(3, s.HeartbeatsAnswered);
        Assert.Equal(6, s.IgnoredFrames);
        Assert.Equal(3, session.Commands(OnlineHeartbeat.ReplyPacketId));
        foreach ((PacketClass cls, ushort id, byte[] payload) sent in session.Sent.Where(x => x.Id == OnlineHeartbeat.ReplyPacketId && x.Class == PacketClass.Cmd))
        {
            Assert.Equal(OnlineHeartbeat.ReplyPayload, sent.payload);
        }

        Assert.Equal(
            new[]
            {
                "PROBE_FIGHT stage=settle-menu step=72 sent=1 reply=ok elapsedMs=0 ignored=2",
                "PROBE_FIGHT stage=settle-menu step=73 sent=1 reply=ok elapsedMs=0 ignored=2",
                "PROBE_FIGHT stage=settle-menu step=74 sent=1 reply=ok elapsedMs=0 ignored=0",
                "PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=ok elapsedMs=0 ignored=1",
                "PROBE_FIGHT stage=settle-menu step=return sent=0 reply=ok elapsedMs=0 ignored=1",
            },
            log.Lines);
        Assert.DoesNotContain("901", string.Join("\n", log.Lines));
        Assert.DoesNotContain("DE", string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task Early_noti23_before_75_reply_plus_noti24_in_return_completes_return()
    {
        // 服务端在回复 CMD/75 之前/同批先塞了 0/23, 然后在 return 阶段推 0/24
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, 0, 0, 0) },
            Pkt75 => new[] { Noti23(), Reply75Ok(), Noti24() },
            _ => null,
        };
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal(0, s.IgnoredFrames);
    }

    // ======== 发送失败、断线、切帧错误、异常与取消 ========

    [Fact]
    public async Task SelectCard_send_failure_returns_SelectCardSendFailed()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(0, 1, -1, -1) },
            _ => null,
        };
        // 第 0 次发 72, 第 1 次发 73, 第 2 次发 74 编码失败
        session.StatusOfSend = index => index == 2 ? FrameStatus.EncryptFailed : FrameStatus.Ok;
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.SelectCardSendFailed, s.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=failed elapsedMs=0 ignored=0", log.Lines[2]);
        Assert.Equal(0, session.Commands(Pkt75));
    }

    [Theory]
    [InlineData(0, "72")]
    [InlineData(1, "73")]
    [InlineData(3, "75")]
    public async Task Send_status_failure_on_72_73_or_75_returns_Faulted(int failSendIndex, string expectedStep)
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, -1, -1, -1) },
            _ => null,
        };
        session.StatusOfSend = index => index == failSendIndex ? FrameStatus.NoCipher : FrameStatus.Ok;
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.Faulted, s.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=" + expectedStep + " sent=0 reply=failed elapsedMs=0 ignored=0", log.Lines.Last());
    }

    [Fact]
    public async Task Heartbeat_send_failure_returns_Faulted()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id == Pkt72 ? new[] { CombatKit.Heartbeat(), Reply72Ok() } : null;
        // index 0 = 72, index 1 = heartbeat
        session.StatusOfSend = index => index == 1 ? FrameStatus.EncryptFailed : FrameStatus.Ok;
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);

        Assert.Equal(OnlineAfterSettleOutcome.Faulted, s.Outcome);
    }

    [Fact]
    public async Task Send_exception_returns_Faulted_with_type_name_only_and_hides_message()
    {
        var session = new CombatFakeSession();
        session.FailureOfSend = _ => new InvalidOperationException("secret-endpoint-123");
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.Faulted, s.Outcome);
        Assert.Equal("InvalidOperationException", s.FaultType);
        Assert.DoesNotContain("secret", string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task Session_closed_or_framing_fault_or_io_exception_returns_SessionClosed()
    {
        // 1. 开始前已关闭
        var closedBefore = new CombatFakeSession { Closed = true };
        var log1 = new HostTestKit.LogSink();
        OnlineAfterSettleSummary s1 = await GatewayTestSupport.Bounded(
            new OnlineAfterSettleDriver(closedBefore, FastOptions()).RunAsync(log1.Add), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, s1.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=72 sent=0 reply=closed elapsedMs=0 ignored=0", log1.Lines[0]);

        // 2. 等待 return 时对端关闭
        var closedDuringReturn = new CombatFakeSession();
        closedDuringReturn.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(-1, -1, -1, -1) },
            Pkt75 => new[] { Reply75Ok() },
            _ => null,
        };
        var clock = new ManualFightClock();
        var log2 = new HostTestKit.LogSink();
        Task<OnlineAfterSettleSummary> run2 = Task.Run(() =>
            new OnlineAfterSettleDriver(closedDuringReturn, FastOptions(clock)).RunAsync(log2.Add));
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等 return");
        closedDuringReturn.Closed = true;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        OnlineAfterSettleSummary s2 = await GatewayTestSupport.Bounded(run2, 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, s2.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=return sent=0 reply=closed elapsedMs=50 ignored=0", log2.Lines.Last());

        // 3. 切帧出错
        var framingFault = new CombatFakeSession { Fault = FrameError.LengthAboveMaximum };
        OnlineAfterSettleSummary s3 = await GatewayTestSupport.Bounded(
            new OnlineAfterSettleDriver(framingFault, FastOptions()).RunAsync(), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, s3.Outcome);

        // 4. SendAsync 抛 IOException
        var ioErr = new CombatFakeSession();
        ioErr.FailureOfSend = _ => new IOException("pipe broken");
        OnlineAfterSettleSummary s4 = await GatewayTestSupport.Bounded(
            new OnlineAfterSettleDriver(ioErr, FastOptions()).RunAsync(), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, s4.Outcome);
    }

    [Fact]
    public async Task Cancellation_stops_promptly_and_returns_Cancelled()
    {
        var clock = new ManualFightClock();
        var session = new CombatFakeSession();
        using var cts = new CancellationTokenSource();
        var driver = new OnlineAfterSettleDriver(session, FastOptions(clock));

        Task<OnlineAfterSettleSummary> run = Task.Run(() => driver.RunAsync(null, cts.Token));
        await CombatKit.WaitUntil(() => clock.HasPending(), "停在等 72");

        cts.Cancel();
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(run, 10);

        Assert.Equal(OnlineAfterSettleOutcome.Cancelled, s.Outcome);
        Assert.Equal(0, clock.Pending);
    }

    [Fact]
    public async Task Throwing_log_callback_does_not_abort_the_driver()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(1, 0, 0, 0) },
            Pkt75 => new[] { Reply75Ok(), Noti2() },
            _ => null,
        };
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(
            driver.RunAsync(_ => throw new InvalidOperationException("broken sink")), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
    }

    // ======== 561 信封与真实 GameClientSession 管道 ========

    [Fact]
    public async Task Enveloped_replies_with_appended_inner_copies_are_unwrapped_and_deduped()
    {
        var session = new CombatFakeSession();
        CombatKit.ServerFrame[] frames =
        {
            Reply72Ok(),
            Reply73(0, 1, 0, 0),
            Reply75Ok(),
            Noti23(),
            Noti24(),
        };
        DecodedFrame Envelope(CombatKit.ServerFrame f)
        {
            byte[] wire = HostTestKit.RealEnvelope(f.Class, f.Id, f.Payload);
            var decoder = new ServerFrameDecoder();
            Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame envFrame));
            return envFrame;
        }
        DecodedFrame Plain(CombatKit.ServerFrame f) => CombatKit.DecodedPlain(f.Class, f.Id, f.Payload);

        session.Respond = (cls, id, _) =>
        {
            if (id == Pkt72)
            {
                session.PushUnit(Envelope(frames[0]));
            }
            else if (id == Pkt73)
            {
                session.PushUnit(Envelope(frames[1]));
            }
            else if (id == Pkt75)
            {
                session.PushUnit(Envelope(frames[2]));
                session.PushUnit(Envelope(frames[3]), Plain(frames[3]));
                session.PushUnit(Envelope(frames[4]), Plain(frames[4]));
            }
            return null;
        };

        var driver = new OnlineAfterSettleDriver(session, FastOptions());
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal(5, s.EnvelopesOpened);
        Assert.Equal(2, s.InnerCopiesSkipped);
        Assert.Equal(0, s.EnvelopesUnopened);
        Assert.Equal(0, s.IgnoredFrames);
    }

    [Fact]
    public async Task Full_sequence_runs_over_real_GameClientSession_with_561_and_compressed_noti2()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd)
            {
                return null;
            }
            if (frame.Id == Pkt72)
            {
                return new[] { HostTestKit.RealEnvelope((byte)PacketClass.Cmd, Pkt72, new byte[] { 1 }) };
            }
            if (frame.Id == Pkt73)
            {
                return new[] { DungeonEntryWire.Reply(Pkt73, Reply73(-1, 1, 0, 1).Payload) };
            }
            if (frame.Id == Pkt75)
            {
                // 测试 561 里套压缩后的 NOTI/2(包号 2 属于压缩包号集合)也能正常被真会话展开并识别回城
                byte[] compressedNoti2 = ZlibCompress(RawNoti2Payload());
                return new[]
                {
                    HostTestKit.RealEnvelope((byte)PacketClass.Cmd, Pkt75, new byte[] { 1, 1, 2 }),
                    HostTestKit.RealEnvelope((byte)PacketClass.Noti, 2, compressedNoti2),
                };
            }
            return null;
        };

        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session);
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 20);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal((byte)1, s.SelectedCardIndex);
        Assert.False(s.CardSelectSkipped);
        Assert.Equal(0, s.IgnoredFrames);
        Assert.Equal(2, s.EnvelopesOpened);     // 72 与 75 是非压缩内层
        Assert.Equal(1, s.EnvelopesUnopened);   // 压缩的 NOTI/2 留给真会话解压并在下一帧交付
        Assert.Equal(2, s.InnerCopiesSkipped);

        DungeonEntryClientFrame[] sentCmds = pipe.ClientFrames.Where(f => f.Class == PacketClass.Cmd).ToArray();
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, sentCmds.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task Notifications_sharing_command_ids_and_commands_sharing_return_ids_are_ignored()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { new CombatKit.ServerFrame(CombatKit.Noti, Pkt72, new byte[] { 1 }), Reply72Ok() },
            Pkt73 => new[] { new CombatKit.ServerFrame(CombatKit.Noti, Pkt73, Reply73(1, 0, 0, 0).Payload), Reply73(1, 0, 0, 0) },
            Pkt75 => new[]
            {
                new CombatKit.ServerFrame(CombatKit.Noti, Pkt75, new byte[] { 1, 1, 2 }),
                Reply75Ok(),
                new CombatKit.ServerFrame(CombatKit.Cmd, 2, ZlibCompress(new byte[] { 0 })),
                new CombatKit.ServerFrame(CombatKit.Cmd, 23, new byte[] { 0 }),
                new CombatKit.ServerFrame(CombatKit.Cmd, 24, new byte[] { 0 }),
                Noti2(),
            },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal(6, s.IgnoredFrames);
        Assert.Equal(
            new[]
            {
                "PROBE_FIGHT stage=settle-menu step=72 sent=1 reply=ok elapsedMs=0 ignored=1",
                "PROBE_FIGHT stage=settle-menu step=73 sent=1 reply=ok elapsedMs=0 ignored=1",
                "PROBE_FIGHT stage=settle-menu step=74 sent=1 reply=ok elapsedMs=0 ignored=0",
                "PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=ok elapsedMs=0 ignored=1",
                "PROBE_FIGHT stage=settle-menu step=return sent=0 reply=ok elapsedMs=0 ignored=3",
            },
            log.Lines);
    }

    [Fact]
    public async Task Early_noti2_arriving_before_75_reply_completes_return_with_sent_zero()
    {
        var session = new CombatFakeSession();
        session.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(-1, -1, -1, -1) },
            Pkt75 => new[] { Noti2(), Reply75Ok() },
            _ => null,
        };
        var log = new HostTestKit.LogSink();
        var driver = new OnlineAfterSettleDriver(session, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(log.Add), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=return sent=0 reply=ok elapsedMs=0 ignored=0", log.Lines.Last());
    }

    [Fact]
    public async Task Pre_send_closed_and_step74_and_return_failure_logs_report_exact_sent_and_reply_fields()
    {
        // 1. step 73 前对端已关闭 -> sent=0 reply=closed
        var s73Closed = new CombatFakeSession();
        s73Closed.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : null;
        var log73 = new HostTestKit.LogSink();
        OnlineAfterSettleSummary r73 = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(s73Closed, FastOptions()).RunAsync(line =>
        {
            log73.Add(line);
            if (line.Contains("step=72 ")) s73Closed.Closed = true;
        }), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, r73.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=73 sent=0 reply=closed elapsedMs=0 ignored=0", log73.Lines.Last());

        // 2. step 74 前对端已关闭 -> sent=0 reply=closed ignored=0
        var s74Closed = new CombatFakeSession();
        s74Closed.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, 0, 0, 0) } : null);
        var log74c = new HostTestKit.LogSink();
        OnlineAfterSettleSummary r74c = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(s74Closed, FastOptions()).RunAsync(line =>
        {
            log74c.Add(line);
            if (line.Contains("step=73 ")) s74Closed.Closed = true;
        }), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, r74c.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=closed elapsedMs=0 ignored=0", log74c.Lines.Last());

        // 3. step 74 发时抛 IOException -> sent=0 reply=closed ignored=0
        var s74Io = new CombatFakeSession();
        s74Io.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, 0, 0, 0) } : null);
        s74Io.FailureOfSend = index => index == 2 ? new IOException("closed-74") : null;
        var log74io = new HostTestKit.LogSink();
        OnlineAfterSettleSummary r74io = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(s74Io, FastOptions()).RunAsync(log74io.Add), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, r74io.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=closed elapsedMs=0 ignored=0", log74io.Lines.Last());

        // 4. step 74 发时抛 Exception -> sent=0 reply=failed ignored=0
        var s74Ex = new CombatFakeSession();
        s74Ex.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, 0, 0, 0) } : null);
        s74Ex.FailureOfSend = index => index == 2 ? new InvalidOperationException("fault-74") : null;
        var log74ex = new HostTestKit.LogSink();
        OnlineAfterSettleSummary r74ex = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(s74Ex, FastOptions()).RunAsync(log74ex.Add), 10);
        Assert.Equal(OnlineAfterSettleOutcome.Faulted, r74ex.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=failed elapsedMs=0 ignored=0", log74ex.Lines.Last());

        // 5. step 74 取消 -> sent=0 reply=failed ignored=0
        using var cts74 = new CancellationTokenSource();
        var s74Cancel = new CombatFakeSession();
        s74Cancel.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(1, 0, 0, 0) } : null);
        s74Cancel.StatusOfSend = index => { if (index == 2) { cts74.Cancel(); throw new OperationCanceledException(cts74.Token); } return FrameStatus.Ok; };
        var log74can = new HostTestKit.LogSink();
        OnlineAfterSettleSummary r74can = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(s74Cancel, FastOptions()).RunAsync(log74can.Add, cts74.Token), 10);
        Assert.Equal(OnlineAfterSettleOutcome.Cancelled, r74can.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=failed elapsedMs=0 ignored=0", log74can.Lines.Last());

        // 6. step 75 前对端已关闭 -> sent=0 reply=closed
        var s75Closed = new CombatFakeSession();
        s75Closed.Respond = (cls, id, _) => id == Pkt72 ? new[] { Reply72Ok() } : (id == Pkt73 ? new[] { Reply73(-1, -1, -1, -1) } : null);
        var log75c = new HostTestKit.LogSink();
        OnlineAfterSettleSummary r75c = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(s75Closed, FastOptions()).RunAsync(line =>
        {
            log75c.Add(line);
            if (line.Contains("step=74 ")) s75Closed.Closed = true;
        }), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, r75c.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=75 sent=0 reply=closed elapsedMs=0 ignored=0", log75c.Lines.Last());

        // 7. return 阶段取消、IOException、Exception -> sent=0 与对应 reply=
        var clockRet = new ManualFightClock();
        using var ctsRet = new CancellationTokenSource();
        var sRetCancel = new CombatFakeSession();
        sRetCancel.Respond = (cls, id, _) => id switch { Pkt72 => new[] { Reply72Ok() }, Pkt73 => new[] { Reply73(-1, -1, -1, -1) }, Pkt75 => new[] { Reply75Ok() }, _ => null };
        var logRetCan = new HostTestKit.LogSink();
        Task<OnlineAfterSettleSummary> runRetCan = Task.Run(() => new OnlineAfterSettleDriver(sRetCancel, FastOptions(clockRet)).RunAsync(logRetCan.Add, ctsRet.Token));
        await CombatKit.WaitUntil(() => clockRet.HasPending(), "停在 return");
        ctsRet.Cancel();
        OnlineAfterSettleSummary rRetCan = await GatewayTestSupport.Bounded(runRetCan, 10);
        Assert.Equal(OnlineAfterSettleOutcome.Cancelled, rRetCan.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=return sent=0 reply=failed elapsedMs=0 ignored=0", logRetCan.Lines.Last());

        var sRetIo = new ReceiveFaultSession(new IOException("ret-io"));
        var logRetIo = new HostTestKit.LogSink();
        OnlineAfterSettleSummary rRetIo = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(sRetIo, FastOptions()).RunAsync(logRetIo.Add), 10);
        Assert.Equal(OnlineAfterSettleOutcome.SessionClosed, rRetIo.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=return sent=0 reply=closed elapsedMs=0 ignored=0", logRetIo.Lines.Last());

        var sRetEx = new ReceiveFaultSession(new InvalidOperationException("ret-ex"));
        var logRetEx = new HostTestKit.LogSink();
        OnlineAfterSettleSummary rRetEx = await GatewayTestSupport.Bounded(new OnlineAfterSettleDriver(sRetEx, FastOptions()).RunAsync(logRetEx.Add), 10);
        Assert.Equal(OnlineAfterSettleOutcome.Faulted, rRetEx.Outcome);
        Assert.Equal("PROBE_FIGHT stage=settle-menu step=return sent=0 reply=failed elapsedMs=0 ignored=0", logRetEx.Lines.Last());
    }

    [Fact]
    public async Task Envelope_boundaries_and_inner_copy_mismatch_rules_are_enforced()
    {
        var session = new CombatFakeSession();
        // 1. 内层帧总长边界: 14(<15) 未拆开, 16(>15) 未拆开, 15(空载荷 NOTI/901) 拆开并计 ignored, NOTI/561 嵌套未递归拆
        byte[] len14 = new byte[15];
        len14[0] = 0; len14[1] = 72; len14[3] = 14;
        byte[] len16 = new byte[15];
        len16[0] = 0; len16[1] = 72; len16[3] = 16;
        byte[] empty901 = DungeonEntryWire.Inner(CombatKit.Noti, 901, Array.Empty<byte>());
        byte[] nested561 = DungeonEntryWire.Inner(CombatKit.Noti, 561, Array.Empty<byte>());
        int step = 0;
        Func<byte[], byte[]> customUnwrap = payload =>
        {
            step++;
            if (step == 1) return len14;
            if (step == 2) return len16;
            if (step == 3) return empty901;
            if (step == 4) return nested561;
            return OnlineDungeonRunner.UnwrapEnvelope(payload);
        };

        // 2. InnerCopy 错配测试:
        //    - 类不同(Cmd/23 紧跟在 NOTI/23 信封之后) -> 不能当副本跳过, 算 ignored
        //    - 首字节不同(修改 NOTI/901 的 byte[0]) -> 不能当副本跳过, 算 ignored
        //    - 尾字节不同(修改 NOTI/901 的 byte[2]) -> 不能当副本跳过, 算 ignored
        CombatKit.ServerFrame noti23 = Noti23();
        CombatKit.ServerFrame u901 = CombatKit.Unrelated(901);
        byte[] diffByte0 = (byte[])u901.Payload.Clone();
        diffByte0[0] ^= 0x01;
        byte[] diffByte2 = (byte[])u901.Payload.Clone();
        diffByte2[2] ^= 0x01;
        DecodedFrame Env(CombatKit.ServerFrame f)
        {
            byte[] wire = HostTestKit.RealEnvelope(f.Class, f.Id, f.Payload);
            var decoder = new ServerFrameDecoder();
            Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame env));
            return env;
        }

        session.Respond = (cls, id, _) =>
        {
            if (id == Pkt72)
            {
                // 先推 4 个测试信封, 再推带非 Plain(Session 帧)副本的 CMD/72 信封: 副本不被跳过, 在 73 里被忽略
                session.Push(
                    new CombatKit.ServerFrame(CombatKit.Noti, 561, new byte[] { 1 }),
                    new CombatKit.ServerFrame(CombatKit.Noti, 561, new byte[] { 2 }),
                    new CombatKit.ServerFrame(CombatKit.Noti, 561, new byte[] { 3 }),
                    new CombatKit.ServerFrame(CombatKit.Noti, 561, new byte[] { 4 }));
                session.PushUnit(Env(Reply72Ok()), Reply72Ok().Decoded());
                return null;
            }
            if (id == Pkt73)
            {
                return new[] { Reply73(-1, -1, -1, -1) };
            }
            if (id == Pkt75)
            {
                session.PushUnit(Env(noti23), CombatKit.DecodedPlain(CombatKit.Cmd, 23, noti23.Payload));
                session.PushUnit(Env(noti23), CombatKit.DecodedPlain(CombatKit.Noti, 901, noti23.Payload));
                session.PushUnit(Env(u901), CombatKit.DecodedPlain(CombatKit.Noti, 901, diffByte0));
                session.PushUnit(Env(u901), CombatKit.DecodedPlain(CombatKit.Noti, 901, diffByte2));
                session.Push(Reply75Ok(), Noti24());
            }
            return null;
        };

        var driver = new OnlineAfterSettleDriver(session, customUnwrap, FastOptions());
        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.Equal(2, s.EnvelopesUnopened); // len14, len16
        Assert.Equal(7, s.EnvelopesOpened);   // empty901, nested561, cmd72, 2*noti23, 2*u901
        Assert.Equal(0, s.InnerCopiesSkipped);
        // empty901(1) + Reply72Ok(Session,1) + Cmd/23(1) + Noti/901(1) + Env(u901)*2(2) + diffByte0(1) + diffByte2(1) = 8
        Assert.Equal(8, s.IgnoredFrames);
    }

    [Fact]
    public async Task Receive_loop_requests_one_frame_at_a_time()
    {
        var inner = new CombatFakeSession();
        inner.Respond = (cls, id, _) => id switch
        {
            Pkt72 => new[] { Reply72Ok() },
            Pkt73 => new[] { Reply73(-1, -1, -1, -1) },
            Pkt75 => new[] { Reply75Ok(), Noti2() },
            _ => null,
        };
        var spy = new StopAfterSpySession(inner);
        var driver = new OnlineAfterSettleDriver(spy, FastOptions());

        OnlineAfterSettleSummary s = await GatewayTestSupport.Bounded(driver.RunAsync(), 10);

        Assert.Equal(OnlineAfterSettleOutcome.ReturnedToTown, s.Outcome);
        Assert.NotEmpty(spy.StopAfterValues);
        Assert.All(spy.StopAfterValues, v => Assert.Equal(1, v));
    }

    private sealed class StopAfterSpySession : IOnlineDungeonSession
    {
        private readonly IOnlineDungeonSession _inner;
        public List<int> StopAfterValues { get; } = new List<int>();

        public StopAfterSpySession(IOnlineDungeonSession inner) => _inner = inner;
        public bool IsReady => _inner.IsReady;
        public bool PeerClosed => _inner.PeerClosed;
        public FrameError LastFrameError => _inner.LastFrameError;

        public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
            => _inner.SendAsync(packetClass, packetId, payload, cancellationToken);

        public Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
        {
            StopAfterValues.Add(stopAfterFrames);
            return _inner.ReceiveAsync(window, stopAfterFrames, cancellationToken);
        }
    }

    private sealed class ReceiveFaultSession : IOnlineDungeonSession
    {
        private readonly Exception _faultOnReturn;
        private readonly Queue<DecodedFrame> _q = new Queue<DecodedFrame>();

        public ReceiveFaultSession(Exception faultOnReturn)
        {
            _faultOnReturn = faultOnReturn;
        }

        public bool IsReady => true;
        public bool PeerClosed => false;
        public FrameError LastFrameError => FrameError.None;

        public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
        {
            if (packetId == Pkt72) _q.Enqueue(Reply72Ok().Decoded());
            else if (packetId == Pkt73) _q.Enqueue(Reply73(-1, -1, -1, -1).Decoded());
            else if (packetId == Pkt75) _q.Enqueue(Reply75Ok().Decoded());
            return Task.FromResult(FrameStatus.Ok);
        }

        public Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
        {
            if (_q.Count > 0)
            {
                return Task.FromResult(new List<DecodedFrame> { _q.Dequeue() });
            }
            throw _faultOnReturn;
        }
    }

    private static byte[] ZlibCompress(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain, 0, plain.Length);
        }
        return ms.ToArray();
    }
}
