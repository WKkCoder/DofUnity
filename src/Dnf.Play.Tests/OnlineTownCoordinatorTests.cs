using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇命令协作者(严格单在途 1/38)的逐条件测试:每个拒绝条件单独成立、踢下线与掉线、无在途时的事件丢弃、
/// 目标村/区核对、发送任务异常/取消/为空、令牌取消、超时默认与边界、释放后的轮询。
/// 全部走合成的假传输层,数据是合成值(自身唯一号 9302、其他玩家 9401…),不依赖等待。
/// </summary>
public sealed class OnlineTownCoordinatorTests
{
    private const ushort Self = 9302;

    private sealed class Rig : IDisposable
    {
        public Rig(bool areaSync = true, long timeoutMs = 0)
        {
            Atlas = TownKit.Atlas((1, new[]
            {
                new KitArea(0, "[gate]", new[] { 233, 177 }),
                new KitArea(1),
                new KitArea(2),
                new KitArea(3),
            }));
            Coord = new OnlineTownCommandCoordinator(Transport, Self, areaSync, timeoutMs);
        }

        public TownAtlas Atlas { get; }

        public TownKitTransport Transport { get; } = new TownKitTransport();

        public OnlineTownCommandCoordinator Coord { get; }

        public TownPlace From { get; } = new TownPlace(1, 1);

        public TownPlace To(int area) => new TownPlace(1, area);

        /// <summary>从 1/1 起走向 1/<paramref name="area"/>(目标村 1、目标区 area)。</summary>
        public bool Begin(long nowMs, int area = 2) =>
            Coord.BeginAreaChange(From, To(area), Atlas.GetArea(1, area), 1, (byte)area, 54, 168, 1, 1, nowMs);

        public void ConfirmNormal(byte area = 2)
        {
            Transport.Push(KitEvents.UserArea(1, area, Self));
            Transport.Push(KitEvents.AreaUsers(1, area, Self));
        }

        public void Dispose() => Coord.Dispose();
    }

    // ───────────────────────── 构造与释放 ─────────────────────────

    [Fact]
    public void Constructor_without_a_transport_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineTownCommandCoordinator(null, Self));
    }

    [Fact]
    public void Constructor_subscribes_both_events_and_dispose_unsubscribes_both_once_and_disposes_the_transport()
    {
        var transport = new TownKitTransport();
        var coord = new OnlineTownCommandCoordinator(transport, Self);

        Assert.Equal(2, transport.SubscribeCount);
        Assert.Equal(0, transport.UnsubscribeCount);
        Assert.False(transport.Disposed);

        coord.Dispose();
        coord.Dispose();

        Assert.Equal(2, transport.UnsubscribeCount);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void Default_arguments_enable_area_sync_and_time_out_after_exactly_five_seconds()
    {
        var transport = new TownKitTransport();
        using var coord = new OnlineTownCommandCoordinator(transport, Self);
        TownAtlas atlas = TownKit.Atlas((1, new[] { new KitArea(1), new KitArea(2) }));

        Assert.True(coord.AreaSyncEnabled);
        Assert.Equal(5000L, coord.TimeoutMs);
        Assert.True(coord.BeginAreaChange(new TownPlace(1, 1), new TownPlace(1, 2), atlas.GetArea(1, 2), 1, 2, 54, 168, 1, 1, 1000));

        Assert.Equal(AreaChangeStatus.None, coord.Poll(5999).Status);   // 过去 4999 毫秒:还在等
        Assert.True(coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.Timeout, coord.Poll(6000).Status); // 过去 5000 毫秒:超时
        Assert.False(coord.HasInFlight);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-5000L)]
    public void A_non_positive_timeout_means_the_five_second_default(long given)
    {
        using var r = new Rig(timeoutMs: given);

        Assert.Equal(5000L, r.Coord.TimeoutMs);
        Assert.True(r.Begin(100));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(5099).Status);
        Assert.Equal(AreaChangeStatus.Timeout, r.Coord.Poll(5100).Status);
    }

    [Fact]
    public void A_positive_timeout_is_kept_and_counted_from_the_time_the_request_began()
    {
        using var r = new Rig(timeoutMs: 400);

        Assert.Equal(400L, r.Coord.TimeoutMs);
        Assert.True(r.Begin(1000));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(1399).Status);
        AreaChangeResult timeout = r.Coord.Poll(1400);

        Assert.Equal(AreaChangeStatus.Timeout, timeout.Status);
        Assert.Equal(r.From, timeout.From);
        Assert.Equal(r.To(2), timeout.To);
        Assert.Equal((byte)0, timeout.ErrorCode);
        Assert.Equal(0, timeout.RemainingMinutes);
        Assert.False(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.Timeout, r.Coord.LastResult.Status);
    }

    [Fact]
    public void A_timeout_of_one_millisecond_is_kept_as_is()
    {
        using var r = new Rig(timeoutMs: 1);

        Assert.Equal(1L, r.Coord.TimeoutMs);
        Assert.True(r.Begin(10));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(10).Status);
        Assert.Equal(AreaChangeStatus.Timeout, r.Coord.Poll(11).Status);
    }

    // ───────────────────────── 发起与单在途 ─────────────────────────

    [Fact]
    public void Begin_hands_every_argument_to_the_transport_in_order_with_a_live_token()
    {
        using var r = new Rig();
        TownAreaInfo info = r.Atlas.GetArea(1, 3);

        bool begun = r.Coord.BeginAreaChange(new TownPlace(1, 1), new TownPlace(1, 3), info, 4, 5, 1234, 4321, 2, 3, 700);

        Assert.True(begun);
        KitSend sent = Assert.Single(r.Transport.Sent);
        Assert.Equal((byte)4, sent.DstV);
        Assert.Equal((byte)5, sent.DstA);
        Assert.Equal((ushort)1234, sent.X);
        Assert.Equal((ushort)4321, sent.Y);
        Assert.Equal((ushort)2, sent.SrcV);
        Assert.Equal((ushort)3, sent.SrcA);
        Assert.Equal((byte)5, sent.Flags);
        Assert.False(sent.Ct.IsCancellationRequested);
        Assert.True(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.Pending, r.Coord.LastResult.Status);
        Assert.Equal(new TownPlace(1, 1), r.Coord.LastResult.From);
        Assert.Equal(new TownPlace(1, 3), r.Coord.LastResult.To);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("disposed")]
    [InlineData("syncoff")]
    [InlineData("kicked")]
    [InlineData("inflight")]
    public void Begin_is_rejected_by_each_blocking_condition_alone_and_sends_nothing_more(string condition)
    {
        using var r = new Rig(areaSync: condition != "syncoff");
        switch (condition)
        {
            case "lost":
                r.Transport.RaiseDisconnected();
                r.Coord.Poll(0);
                Assert.True(r.Coord.IsLost);
                Assert.False(r.Coord.IsKicked);
                break;
            case "disposed":
                r.Coord.Dispose();
                break;
            case "kicked":
                r.Transport.Push(KitEvents.Kicked(5));
                r.Coord.Poll(0);
                Assert.True(r.Coord.IsKicked);
                break;
            case "inflight":
                Assert.True(r.Begin(0));
                break;
        }
        int sentBefore = condition == "inflight" ? 1 : 0;

        Assert.False(r.Begin(10));

        Assert.Equal(sentBefore, r.Transport.Sent.Count);
        Assert.Equal(condition == "inflight", r.Coord.HasInFlight);
        Assert.Equal(condition == "inflight" ? AreaChangeStatus.Pending : AreaChangeStatus.None, r.Coord.LastResult.Status);
    }

    [Fact]
    public void Area_sync_can_be_switched_off_and_on_while_running()
    {
        using var r = new Rig();

        r.Coord.AreaSyncEnabled = false;
        Assert.False(r.Begin(0));
        Assert.Empty(r.Transport.Sent);

        r.Coord.AreaSyncEnabled = true;
        Assert.True(r.Begin(10));
        Assert.Single(r.Transport.Sent);
    }

    [Fact]
    public void A_second_request_is_possible_once_the_first_one_is_done()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));
        r.ConfirmNormal();
        Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.Poll(16).Status);

        Assert.True(r.Begin(32, area: 3));

        Assert.Equal(2, r.Transport.Sent.Count);
        Assert.Equal((byte)3, r.Transport.Sent[1].DstA);
    }

    // ───────────────────────── 掉线与踢下线 ─────────────────────────

    [Fact]
    public void A_disconnect_is_only_noticed_by_the_next_poll_and_fails_the_request_in_flight()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));

        r.Transport.RaiseDisconnected();
        Assert.False(r.Coord.IsLost);
        AreaChangeResult result = r.Coord.Poll(16);

        Assert.True(r.Coord.IsLost);
        Assert.False(r.Coord.IsKicked);
        Assert.Null(r.Coord.KickReasonCode);
        Assert.Equal(AreaChangeStatus.SendFailed, result.Status);
        Assert.Equal(r.From, result.From);
        Assert.Equal(r.To(2), result.To);
        Assert.Equal((byte)0, result.ErrorCode);
        Assert.Equal(0, result.RemainingMinutes);
        Assert.False(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.LastResult.Status);
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(32).Status);
        Assert.True(r.Coord.IsLost);
    }

    [Fact]
    public void A_disconnect_without_a_request_in_flight_just_marks_the_channel_lost()
    {
        using var r = new Rig();

        r.Transport.RaiseDisconnected();
        AreaChangeResult result = r.Coord.Poll(0);

        Assert.True(r.Coord.IsLost);
        Assert.Equal(AreaChangeStatus.None, result.Status);
        Assert.Equal(AreaChangeStatus.None, r.Coord.LastResult.Status);
    }

    [Fact]
    public void A_kick_marks_kicked_and_lost_keeps_the_reason_and_fails_the_request_in_flight()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));

        r.Transport.Push(KitEvents.Kicked(7));
        Assert.False(r.Coord.IsKicked);
        AreaChangeResult result = r.Coord.Poll(16);

        Assert.True(r.Coord.IsKicked);
        Assert.True(r.Coord.IsLost);
        Assert.Equal(7, r.Coord.KickReasonCode);
        Assert.Equal(AreaChangeStatus.SendFailed, result.Status);
        Assert.Equal(r.From, result.From);
        Assert.Equal(r.To(2), result.To);
        Assert.False(r.Coord.HasInFlight);
        Assert.False(r.Begin(32));
    }

    [Fact]
    public void A_kick_without_a_request_in_flight_returns_nothing_but_is_remembered()
    {
        using var r = new Rig();

        r.Transport.Push(KitEvents.Kicked(11));
        AreaChangeResult result = r.Coord.Poll(0);

        Assert.Equal(AreaChangeStatus.None, result.Status);
        Assert.True(r.Coord.IsKicked);
        Assert.True(r.Coord.IsLost);
        Assert.Equal(11, r.Coord.KickReasonCode);
    }

    [Fact]
    public void A_kick_queued_behind_matching_confirmations_still_wins_over_them_and_later_events_are_dropped()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));
        r.Transport.Push(KitEvents.Kicked(3));
        r.ConfirmNormal();

        Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.Poll(16).Status);
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(32).Status);
        Assert.True(r.Coord.IsKicked);
    }

    // ───────────────────────── 无在途时的事件 ─────────────────────────

    [Fact]
    public void Events_arriving_without_a_request_in_flight_are_discarded_and_never_count_for_a_later_request()
    {
        using var r = new Rig();
        r.ConfirmNormal();
        r.Transport.Push(KitEvents.Error(0x44, 9));
        r.Transport.Push(KitEvents.Error(0x11));

        AreaChangeResult idle = r.Coord.Poll(0);
        Assert.Equal(AreaChangeStatus.None, idle.Status);
        Assert.Equal(AreaChangeStatus.None, r.Coord.LastResult.Status);

        Assert.True(r.Begin(100));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(116).Status);
        Assert.True(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.Pending, r.Coord.LastResult.Status);
    }

    // ───────────────────────── 目标核对与完成条件 ─────────────────────────

    [Fact]
    public void A_normal_target_needs_both_messages_for_its_own_village_and_area_and_the_own_unique_id()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));

        // 0/24 里村对、区错;村错、区对;其他玩家:都不算
        r.Transport.Push(KitEvents.UserArea(1, 2, Self));
        r.Transport.Push(KitEvents.AreaUsers(1, 3, Self));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(16).Status);
        r.Transport.Push(KitEvents.AreaUsers(2, 2, Self));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(32).Status);
        r.Transport.Push(KitEvents.AreaUsers(1, 2, 9401, 9402));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(48).Status);
        r.Transport.Push(KitEvents.AreaUsers(1, 2));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(64).Status);

        // 名单里自己排在最后也要认
        r.Transport.Push(KitEvents.AreaUsers(1, 2, 9401, 9402, Self));
        AreaChangeResult done = r.Coord.Poll(80);

        Assert.Equal(AreaChangeStatus.Confirmed, done.Status);
        Assert.Equal(r.From, done.From);
        Assert.Equal(r.To(2), done.To);
        Assert.Equal((byte)0, done.ErrorCode);
        Assert.Equal(0, done.RemainingMinutes);
        Assert.False(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.LastResult.Status);
    }

    [Fact]
    public void A_normal_target_ignores_0_23_for_another_user_village_or_area()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));

        r.Transport.Push(KitEvents.AreaUsers(1, 2, Self));
        r.Transport.Push(KitEvents.UserArea(1, 2, 9401));   // 别人的 0/23
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(16).Status);
        r.Transport.Push(KitEvents.UserArea(2, 2, Self));   // 村错
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(32).Status);
        r.Transport.Push(KitEvents.UserArea(1, 3, Self));   // 区错
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(48).Status);

        r.Transport.Push(KitEvents.UserArea(1, 2, Self));
        Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.Poll(64).Status);
    }

    [Fact]
    public void A_normal_target_is_not_complete_with_only_0_23_or_only_0_24()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));
        r.Transport.Push(KitEvents.UserArea(1, 2, Self));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(16).Status);

        Assert.True(r.Coord.HasInFlight);
        r.Transport.Push(KitEvents.AreaUsers(1, 2, Self));
        Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.Poll(32).Status);
    }

    [Fact]
    public void A_gate_target_completes_on_0_24_alone_for_its_own_village_and_area()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0, area: 0));

        r.Transport.Push(KitEvents.AreaUsers(1, 5, Self));       // 区错
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(16).Status);
        r.Transport.Push(KitEvents.AreaUsers(2, 0, Self));       // 村错
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(32).Status);
        r.Transport.Push(KitEvents.AreaUsers(1, 0, 9401));       // 名单里没有自己
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(48).Status);
        r.Transport.Push(KitEvents.UserArea(1, 0, Self));        // 关口区单靠 0/23 不算
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(64).Status);

        r.Transport.Push(KitEvents.AreaUsers(1, 0, Self));
        AreaChangeResult done = r.Coord.Poll(80);

        Assert.Equal(AreaChangeStatus.Confirmed, done.Status);
        Assert.Equal(r.To(0), done.To);
    }

    [Fact]
    public void Confirmation_beats_a_timeout_that_has_also_elapsed()
    {
        using var r = new Rig(timeoutMs: 400);
        Assert.True(r.Begin(1000));
        r.ConfirmNormal();

        Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.Poll(9000).Status);
    }

    // ───────────────────────── 错误回包 ─────────────────────────

    [Theory]
    [InlineData(0x44, 7, AreaChangeStatus.Forbidden, 7)]
    [InlineData(0x44, 0, AreaChangeStatus.Forbidden, 0)]
    [InlineData(0x43, 0, AreaChangeStatus.RolledBack, 0)]
    [InlineData(0x45, 0, AreaChangeStatus.RolledBack, 0)]
    [InlineData(0x11, 0, AreaChangeStatus.RolledBack, 0)]
    [InlineData(0x01, 0, AreaChangeStatus.RolledBack, 0)]
    public void An_error_reply_ends_the_request_with_its_code_and_the_remaining_minutes(
        int code, int minutes, AreaChangeStatus status, int expectedMinutes)
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));

        r.Transport.Push(KitEvents.Error((byte)code, minutes));
        AreaChangeResult result = r.Coord.Poll(16);

        Assert.Equal(status, result.Status);
        Assert.Equal((byte)code, result.ErrorCode);
        Assert.Equal(expectedMinutes, result.RemainingMinutes);
        Assert.Equal(r.From, result.From);
        Assert.Equal(r.To(2), result.To);
        Assert.False(r.Coord.HasInFlight);
        Assert.Equal(status, r.Coord.LastResult.Status);
    }

    [Fact]
    public void An_error_reply_ahead_of_matching_confirmations_wins_and_the_rest_is_dropped_later()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));
        r.Transport.Push(KitEvents.Error(0x11));
        r.ConfirmNormal();

        Assert.Equal(AreaChangeStatus.RolledBack, r.Coord.Poll(16).Status);
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(32).Status);
    }

    // ───────────────────────── 发送任务本身 ─────────────────────────

    [Fact]
    public void A_transport_that_throws_synchronously_fails_the_request_without_leaving_it_in_flight()
    {
        using var r = new Rig();
        r.Transport.ThrowOnSend = new InvalidOperationException("测试故障");

        bool begun = r.Begin(0);

        Assert.False(begun);
        Assert.Single(r.Transport.Sent);
        Assert.False(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.LastResult.Status);
        Assert.Equal(r.From, r.Coord.LastResult.From);
        Assert.Equal(r.To(2), r.Coord.LastResult.To);
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(16).Status);

        r.Transport.ThrowOnSend = null;
        Assert.True(r.Begin(32));
        Assert.Equal(AreaChangeStatus.Pending, r.Coord.LastResult.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_send_task_counts_as_a_failed_send_even_with_matching_confirmations(bool withConfirmations)
    {
        using var r = new Rig();
        r.Transport.OnSend = _ => null;

        Assert.True(r.Begin(0));
        Assert.True(r.Coord.HasInFlight);
        if (withConfirmations)
        {
            r.ConfirmNormal();
        }
        AreaChangeResult result = r.Coord.Poll(16);

        Assert.Equal(AreaChangeStatus.SendFailed, result.Status);
        Assert.Equal(r.From, result.From);
        Assert.Equal(r.To(2), result.To);
        Assert.False(r.Coord.HasInFlight);
        Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.LastResult.Status);
    }

    [Fact]
    public void A_cancelled_send_task_fails_the_request()
    {
        using var r = new Rig();
        r.Transport.OnSend = _ => Task.FromCanceled(new CancellationToken(true));

        Assert.True(r.Begin(0));
        AreaChangeResult result = r.Coord.Poll(16);

        Assert.Equal(AreaChangeStatus.SendFailed, result.Status);
        Assert.False(r.Coord.HasInFlight);
    }

    [Fact]
    public void A_faulted_send_task_fails_the_request_when_it_faults_after_the_request_began()
    {
        using var r = new Rig();
        var send = new TaskCompletionSource();
        r.Transport.OnSend = _ => send.Task;
        Assert.True(r.Begin(0));
        Assert.Equal(AreaChangeStatus.None, r.Coord.Poll(16).Status);

        send.SetException(new IOException("测试故障"));
        AreaChangeResult result = r.Coord.Poll(32);

        Assert.Equal(AreaChangeStatus.SendFailed, result.Status);
        Assert.False(r.Coord.HasInFlight);
    }

    [Fact]
    public void A_send_task_that_is_still_running_does_not_block_the_confirmation()
    {
        using var r = new Rig();
        r.Transport.OnSend = _ => new TaskCompletionSource().Task;
        Assert.True(r.Begin(0));
        r.ConfirmNormal();

        Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.Poll(16).Status);
    }

    // ───────────────────────── 令牌取消 ─────────────────────────

    [Theory]
    [InlineData("confirmed")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("kick")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public void The_token_of_a_send_that_has_not_finished_is_cancelled_when_the_request_ends(string ending)
    {
        using var r = new Rig(timeoutMs: 400);
        r.Transport.OnSend = _ => new TaskCompletionSource().Task;
        Assert.True(r.Begin(0));
        KitSend sent = Assert.Single(r.Transport.Sent);
        Assert.False(sent.Ct.IsCancellationRequested);

        End(r, ending);

        Assert.True(sent.Ct.IsCancellationRequested);
        Assert.False(r.Coord.HasInFlight);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("kick")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public void The_token_of_a_send_that_already_finished_is_left_alone_when_the_request_ends(string ending)
    {
        using var r = new Rig(timeoutMs: 400);
        Assert.True(r.Begin(0));
        KitSend sent = Assert.Single(r.Transport.Sent);

        End(r, ending);

        Assert.False(sent.Ct.IsCancellationRequested);
        Assert.False(r.Coord.HasInFlight);
    }

    private static void End(Rig r, string ending)
    {
        switch (ending)
        {
            case "confirmed":
                r.ConfirmNormal();
                Assert.Equal(AreaChangeStatus.Confirmed, r.Coord.Poll(16).Status);
                break;
            case "error":
                r.Transport.Push(KitEvents.Error(0x11));
                Assert.Equal(AreaChangeStatus.RolledBack, r.Coord.Poll(16).Status);
                break;
            case "timeout":
                Assert.Equal(AreaChangeStatus.Timeout, r.Coord.Poll(400).Status);
                break;
            case "kick":
                r.Transport.Push(KitEvents.Kicked(4));
                Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.Poll(16).Status);
                break;
            case "disconnect":
                r.Transport.RaiseDisconnected();
                Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.Poll(16).Status);
                break;
            default:
                r.Coord.Dispose();
                break;
        }
    }

    // ───────────────────────── 释放之后 ─────────────────────────

    [Fact]
    public void Poll_after_dispose_returns_nothing_and_does_not_process_what_was_queued_before()
    {
        var transport = new TownKitTransport();
        var coord = new OnlineTownCommandCoordinator(transport, Self);
        transport.RaiseDisconnected();
        transport.Push(KitEvents.Kicked(9));

        coord.Dispose();
        AreaChangeResult result = coord.Poll(100);

        Assert.Equal(AreaChangeStatus.None, result.Status);
        Assert.False(coord.IsLost);
        Assert.False(coord.IsKicked);
        Assert.Null(coord.KickReasonCode);
    }

    [Fact]
    public void Dispose_with_a_request_in_flight_ends_it_and_cancels_its_token()
    {
        var transport = new TownKitTransport();
        transport.OnSend = _ => new TaskCompletionSource().Task;
        var coord = new OnlineTownCommandCoordinator(transport, Self);
        TownAtlas atlas = TownKit.Atlas((1, new[] { new KitArea(1), new KitArea(2) }));
        Assert.True(coord.BeginAreaChange(new TownPlace(1, 1), new TownPlace(1, 2), atlas.GetArea(1, 2), 1, 2, 54, 168, 1, 1, 0));
        KitSend sent = Assert.Single(transport.Sent);
        Assert.True(coord.HasInFlight);

        coord.Dispose();

        Assert.False(coord.HasInFlight);
        Assert.True(sent.Ct.IsCancellationRequested);
        Assert.True(transport.Disposed);
        Assert.Equal(AreaChangeStatus.None, coord.Poll(16).Status);
    }

    // ───────────────────────── 结果的默认字段 ─────────────────────────

    [Fact]
    public void Results_that_do_not_come_from_an_error_reply_carry_no_error_code_and_no_minutes()
    {
        using var r = new Rig();
        Assert.True(r.Begin(0));
        AreaChangeResult pending = r.Coord.LastResult;
        Assert.Equal(AreaChangeStatus.Pending, pending.Status);
        Assert.Equal((byte)0, pending.ErrorCode);
        Assert.Equal(0, pending.RemainingMinutes);

        r.ConfirmNormal();
        AreaChangeResult confirmed = r.Coord.Poll(16);
        Assert.Equal((byte)0, confirmed.ErrorCode);
        Assert.Equal(0, confirmed.RemainingMinutes);
        Assert.Equal((byte)0, r.Coord.LastResult.ErrorCode);
        Assert.Equal(0, r.Coord.LastResult.RemainingMinutes);

        r.Transport.ThrowOnSend = new InvalidOperationException("测试故障");
        Assert.False(r.Begin(32));
        Assert.Equal(AreaChangeStatus.SendFailed, r.Coord.LastResult.Status);
        Assert.Equal((byte)0, r.Coord.LastResult.ErrorCode);
        Assert.Equal(0, r.Coord.LastResult.RemainingMinutes);
    }

    [Fact]
    public void The_result_struct_defaults_are_no_error_code_and_no_minutes()
    {
        var result = new AreaChangeResult(AreaChangeStatus.Unmapped, new TownPlace(1, 1), new TownPlace(1, 2));

        Assert.Equal(AreaChangeStatus.Unmapped, result.Status);
        Assert.Equal((byte)0, result.ErrorCode);
        Assert.Equal(0, result.RemainingMinutes);
        Assert.Equal(AreaChangeStatus.None, default(AreaChangeResult).Status);
    }
}
