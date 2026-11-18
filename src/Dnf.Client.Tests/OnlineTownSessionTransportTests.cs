using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网城镇的会话适配器(<see cref="OnlineTownSessionTransport"/>,把联网会话的帧事件与命令发送口接到 Play 层的中性通道)的逐项测试:
/// 入站映射(0/23、0/24、0/199 与 (1,38) 错误回复的每个字段,包类与包号不对的帧、过短或解析失败的载荷不产生事件也不抛异常)、
/// 发送编码(包号 38、11 字节载荷逐字节、非 Ok 状态抛 <see cref="InvalidOperationException"/>、取消令牌原样透传)、
/// 断开口(只是关闭到频道的连接,不发 <c>1/3</c>,真服怎么看待这种收尾未验证)与释放(订阅摘除、之后到达的帧与断线通知不再转发)。
/// 载荷全是手搭的合成字节(小端),期望值是手算的字面量;不连任何网络:
/// 逐项行为跑在假的会话接缝(<see cref="FakeTownSessionPort"/>)上,默认接线(适配器接在真会话上)用一个什么都没连的真会话核对。
/// </summary>
public sealed class OnlineTownSessionTransportTests
{
    // 0/23 USER_AREA:u16 uid=1203(0x04B3), u8 village=37, u8 area=61, u16 x=1507, u16 y=1119, u8 direction=1, u8 visible=1
    private static readonly byte[] UserAreaPayload = { 0xB3, 0x04, 0x25, 0x3D, 0xE3, 0x05, 0x5F, 0x04, 0x01, 0x01 };

    // 0/24 AREA_USERS:u8 village=44, u8 area=18, u16 count=3,再三条 { u16 uid, u16 x, u16 y, u8 direction, u8 visible }
    private static readonly byte[] AreaUsersPayload =
    {
        0x2C, 0x12, 0x03, 0x00,
        0x15, 0x05, 0xF2, 0x03, 0xC2, 0x06, 0x00, 0x01,   // uid 1301, x 1010, y 1730
        0x89, 0x05, 0xA2, 0x08, 0x29, 0x04, 0x01, 0x01,   // uid 1417, x 2210, y 1065
        0x57, 0x04, 0x89, 0x03, 0xAE, 0x08, 0x00, 0x00,   // uid 1111, x 905,  y 2222
    };

    // 0/199 DISCONNECT_REASON:i32 reason=1305(0x0519)
    private static readonly byte[] KickPayload = { 0x19, 0x05, 0x00, 0x00 };

    // (1,38) 错误回复:普通 3 字节 u8 0, u8 errorCode=0x5A, u8 arg=7
    private static readonly byte[] PlainErrorPayload = { 0x00, 0x5A, 0x07 };

    // (1,38) 错误 0x44:u8 0, u8 0x44, u8 arg=7, i32 minutes=1530(0x05FA)
    private static readonly byte[] ForbiddenErrorPayload = { 0x00, 0x44, 0x07, 0xFA, 0x05, 0x00, 0x00 };

    /// <summary>假的会话接缝:两个事件可以手动触发,订阅数可数,发命令与关连接都记下参数。</summary>
    private sealed class FakeTownSessionPort : OnlineTownSessionTransport.ITownSessionPort
    {
        private Action<OnlineReceivedFrame> _frame;
        private Action<OnlineDisconnectReason> _disconnected;

        public Func<ushort, byte[], CancellationToken, Task<FrameStatus>> OnSend { get; set; } = (packetId, payload, token) => Task.FromResult(FrameStatus.Ok);

        public Func<CancellationToken, Task> OnDisconnect { get; set; } = token => Task.CompletedTask;

        /// <summary>两个事件的订阅/退订被调用的次数(只在测试线程上改,不并发)。</summary>
        public int FrameAdds { get; private set; }

        public int FrameRemoves { get; private set; }

        public int DisconnectedAdds { get; private set; }

        public int DisconnectedRemoves { get; private set; }

        public event Action<OnlineReceivedFrame> FrameReceived
        {
            add { _frame += value; FrameAdds++; }
            remove { _frame -= value; FrameRemoves++; }
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add { _disconnected += value; DisconnectedAdds++; }
            remove { _disconnected -= value; DisconnectedRemoves++; }
        }

        public Action<OnlineReceivedFrame>[] FrameHandlers() => _frame == null ? new Action<OnlineReceivedFrame>[0] : Array.ConvertAll(_frame.GetInvocationList(), d => (Action<OnlineReceivedFrame>)d);

        public Action<OnlineDisconnectReason>[] DisconnectedHandlers() => _disconnected == null ? new Action<OnlineDisconnectReason>[0] : Array.ConvertAll(_disconnected.GetInvocationList(), d => (Action<OnlineDisconnectReason>)d);

        public void RaiseFrame(PacketClass packetClass, int packetId, byte[] payload) => _frame?.Invoke(NetObjects.Frame(packetClass, packetId, payload));

        public void RaiseFrame(OnlineReceivedFrame frame) => _frame?.Invoke(frame);

        public void RaiseDisconnected(OnlineDisconnectReason reason) => _disconnected?.Invoke(reason);

        public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken) => OnSend(packetId, payload, cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken) => OnDisconnect(cancellationToken);
    }

    /// <summary>接在假接缝上的适配器,事件记在列表里。</summary>
    private sealed class Probe : IDisposable
    {
        public Probe()
        {
            Transport = new OnlineTownSessionTransport(Port);
            Transport.InboundReceived += ev => Events.Add(ev);
            Transport.Disconnected += () => DisconnectedCount++;
        }

        public FakeTownSessionPort Port { get; } = new();

        public OnlineTownSessionTransport Transport { get; }

        public List<TownInboundEvent> Events { get; } = new();

        public int DisconnectedCount;

        public void Dispose() => Transport.Dispose();
    }

    /// <summary>接在真会话(什么都没连)上的适配器,走默认接线。</summary>
    private sealed class RealProbe : IDisposable
    {
        public RealProbe()
        {
            Session = SessionProbe.NewUnconnected();
            Transport = new OnlineTownSessionTransport(Session);
            Transport.InboundReceived += ev => Events.Add(ev);
            Transport.Disconnected += () => DisconnectedCount++;
        }

        public OnlineClientSession Session { get; }

        public OnlineTownSessionTransport Transport { get; }

        public List<TownInboundEvent> Events { get; } = new();

        public int DisconnectedCount;

        public void Dispose()
        {
            Transport.Dispose();
            Session.Dispose();
        }
    }

    private static void AssertEvent(TownInboundEvent ev, TownInboundKind kind, int village = 0, int area = 0, int uniqueId = 0,
        int errorCode = 0, int minutes = 0, int reason = 0, int users = 0)
    {
        Assert.Equal(kind, ev.Kind);
        Assert.Equal((byte)village, ev.Village);
        Assert.Equal((byte)area, ev.Area);
        Assert.Equal((ushort)uniqueId, ev.UniqueId);
        Assert.Equal((byte)errorCode, ev.ErrorCode);
        Assert.Equal(minutes, ev.RemainingMinutes);
        Assert.Equal(reason, ev.ReasonCode);
        Assert.NotNull(ev.AreaUsers);
        Assert.Equal(users, ev.AreaUsers.Count);
    }

    // ------------------------------------------------------------------ 入站映射

    [Fact]
    public void User_area_0_23_maps_village_area_and_unique_id_and_nothing_else()
    {
        using var probe = new Probe();

        probe.Port.RaiseFrame(PacketClass.Noti, 23, UserAreaPayload);

        TownInboundEvent ev = Assert.Single(probe.Events);
        AssertEvent(ev, TownInboundKind.UserArea, village: 37, area: 61, uniqueId: 1203);
    }

    [Fact]
    public void Area_users_0_24_maps_village_area_and_every_record_in_order_with_x_and_y_kept_apart()
    {
        using var probe = new Probe();

        probe.Port.RaiseFrame(PacketClass.Noti, 24, AreaUsersPayload);

        TownInboundEvent ev = Assert.Single(probe.Events);
        AssertEvent(ev, TownInboundKind.AreaUsers, village: 44, area: 18, users: 3);
        Assert.Equal((ushort)1301, ev.AreaUsers[0].UniqueId);
        Assert.Equal((ushort)1010, ev.AreaUsers[0].X);
        Assert.Equal((ushort)1730, ev.AreaUsers[0].Y);
        Assert.Equal((ushort)1417, ev.AreaUsers[1].UniqueId);
        Assert.Equal((ushort)2210, ev.AreaUsers[1].X);
        Assert.Equal((ushort)1065, ev.AreaUsers[1].Y);
        Assert.Equal((ushort)1111, ev.AreaUsers[2].UniqueId);
        Assert.Equal((ushort)905, ev.AreaUsers[2].X);
        Assert.Equal((ushort)2222, ev.AreaUsers[2].Y);
    }

    [Fact]
    public void Area_users_with_a_single_record_and_with_none_map_to_lists_of_exactly_that_length()
    {
        using var probe = new Probe();
        byte[] single = { 0x2C, 0x12, 0x01, 0x00, 0x89, 0x05, 0xA2, 0x08, 0x29, 0x04, 0x01, 0x01 };
        byte[] empty = { 0x2C, 0x12, 0x00, 0x00 };

        probe.Port.RaiseFrame(PacketClass.Noti, 24, single);
        probe.Port.RaiseFrame(PacketClass.Noti, 24, empty);

        Assert.Equal(2, probe.Events.Count);
        AssertEvent(probe.Events[0], TownInboundKind.AreaUsers, village: 44, area: 18, users: 1);
        Assert.Equal((ushort)1417, probe.Events[0].AreaUsers[0].UniqueId);
        Assert.Equal((ushort)2210, probe.Events[0].AreaUsers[0].X);
        Assert.Equal((ushort)1065, probe.Events[0].AreaUsers[0].Y);
        AssertEvent(probe.Events[1], TownInboundKind.AreaUsers, village: 44, area: 18, users: 0);
    }

    [Fact]
    public void Kick_0_199_maps_the_reason_code_and_ignores_trailing_bytes()
    {
        using var probe = new Probe();
        byte[] withTrail = { 0x19, 0x05, 0x00, 0x00, 0xAA, 0xBB };

        probe.Port.RaiseFrame(PacketClass.Noti, 199, KickPayload);
        probe.Port.RaiseFrame(PacketClass.Noti, 199, withTrail);

        Assert.Equal(2, probe.Events.Count);
        AssertEvent(probe.Events[0], TownInboundKind.Kicked, reason: 1305);
        AssertEvent(probe.Events[1], TownInboundKind.Kicked, reason: 1305);
    }

    [Fact]
    public void Plain_set_user_area_error_maps_the_error_code_and_reports_zero_minutes()
    {
        using var probe = new Probe();

        probe.Port.RaiseFrame(PacketClass.Cmd, 38, PlainErrorPayload);

        TownInboundEvent ev = Assert.Single(probe.Events);
        AssertEvent(ev, TownInboundKind.SetUserAreaError, errorCode: 0x5A, minutes: 0);
    }

    [Fact]
    public void Forbidden_set_user_area_error_0x44_carries_the_remaining_minutes()
    {
        using var probe = new Probe();

        probe.Port.RaiseFrame(PacketClass.Cmd, 38, ForbiddenErrorPayload);

        TownInboundEvent ev = Assert.Single(probe.Events);
        AssertEvent(ev, TownInboundKind.SetUserAreaError, errorCode: 0x44, minutes: 1530);
    }

    [Fact]
    public void Forbidden_error_without_a_complete_minutes_field_and_error_replies_with_a_nonzero_status_produce_no_event()
    {
        using var probe = new Probe();
        byte[][] payloads =
        {
            new byte[] { 0x00, 0x44, 0x07 },                     // 0x44 缺分钟
            new byte[] { 0x00, 0x44, 0x07, 0xFA },               // 分钟只有 1 字节
            new byte[] { 0x00, 0x44, 0x07, 0xFA, 0x05, 0x00 },   // 分钟只有 3 字节
            new byte[] { 0x01, 0x5A, 0x07 },                     // 状态字节不是 0
            new byte[] { 0x00, 0x5A },                           // 缺参数字节
            new byte[] { 0x00 },
            Array.Empty<byte>(),
        };

        foreach (byte[] payload in payloads)
        {
            probe.Port.RaiseFrame(PacketClass.Cmd, 38, payload);
        }

        Assert.Empty(probe.Events);
    }

    [Fact]
    public void Frames_with_the_wrong_packet_class_or_packet_id_produce_no_event()
    {
        using var probe = new Probe();
        var frames = new List<(PacketClass Class, int Id, byte[] Payload)>
        {
            (PacketClass.Cmd, 23, UserAreaPayload),               // 类 1 的 23 号
            (PacketClass.Cmd, 24, AreaUsersPayload),
            (PacketClass.Cmd, 199, KickPayload),
            (PacketClass.Noti, 38, PlainErrorPayload),            // 类 0 的 38 号
            (PacketClass.Noti, 38, ForbiddenErrorPayload),
            (PacketClass.Cmd, 37, PlainErrorPayload),             // 命令类里别的包号
            (PacketClass.Cmd, 39, PlainErrorPayload),
            (PacketClass.Cmd, 0, PlainErrorPayload),
            ((PacketClass)2, 38, PlainErrorPayload),              // 既不是通知也不是命令的包类
            ((PacketClass)2, 23, UserAreaPayload),
            ((PacketClass)2, 24, AreaUsersPayload),
            ((PacketClass)2, 199, KickPayload),
            (PacketClass.Noti, 22, UserAreaPayload),              // 通知类里别的包号
            (PacketClass.Noti, 25, UserAreaPayload),
            (PacketClass.Noti, 25, AreaUsersPayload),
            (PacketClass.Noti, 198, KickPayload),
            (PacketClass.Noti, 200, KickPayload),
            (PacketClass.Noti, 0, KickPayload),
        };

        foreach ((PacketClass cls, int id, byte[] payload) in frames)
        {
            probe.Port.RaiseFrame(cls, id, payload);
        }

        Assert.Empty(probe.Events);
        Assert.Equal(0, probe.DisconnectedCount);
    }

    [Fact]
    public void Short_or_unparseable_payloads_produce_no_event_and_never_throw()
    {
        using var probe = new Probe();
        var frames = new List<(PacketClass Class, int Id, byte[] Payload)>
        {
            (PacketClass.Noti, 23, new byte[] { 0xB3, 0x04, 0x25, 0x3D, 0xE3, 0x05, 0x5F, 0x04, 0x01 }),  // 少 1 字节
            (PacketClass.Noti, 23, Array.Empty<byte>()),
            (PacketClass.Noti, 24, new byte[] { 0x2C, 0x12, 0x03 }),                                      // 头不全
            (PacketClass.Noti, 24, new byte[] { 0x2C, 0x12, 0x03, 0x00, 0x15, 0x05, 0xF2, 0x03, 0xC2, 0x06, 0x00, 0x01 }),  // 声明 3 条只给 1 条
            (PacketClass.Noti, 24, new byte[] { 0x2C, 0x12, 0xFF, 0xFF }),                                // 声明 65535 条一条也没有
            (PacketClass.Noti, 24, Array.Empty<byte>()),
            (PacketClass.Noti, 199, new byte[] { 0x19, 0x05, 0x00 }),                                     // 少 1 字节
            (PacketClass.Noti, 199, Array.Empty<byte>()),
            (PacketClass.Cmd, 38, new byte[] { 0x00, 0x5A }),
            (PacketClass.Cmd, 38, Array.Empty<byte>()),
        };

        foreach ((PacketClass cls, int id, byte[] payload) in frames)
        {
            probe.Port.RaiseFrame(cls, id, payload);
        }

        Assert.Empty(probe.Events);
    }

    [Fact]
    public void A_null_frame_is_ignored_without_throwing()
    {
        using var probe = new Probe();

        probe.Port.RaiseFrame((OnlineReceivedFrame)null);
        Assert.Empty(probe.Events);

        // 之后照常工作
        probe.Port.RaiseFrame(PacketClass.Noti, 23, UserAreaPayload);
        Assert.Single(probe.Events);
    }

    [Fact]
    public void A_valid_frame_with_nobody_listening_is_ignored_without_throwing()
    {
        var port = new FakeTownSessionPort();
        var transport = new OnlineTownSessionTransport(port);

        port.RaiseFrame(PacketClass.Noti, 23, UserAreaPayload);
        port.RaiseFrame(PacketClass.Noti, 24, AreaUsersPayload);
        port.RaiseFrame(PacketClass.Noti, 199, KickPayload);
        port.RaiseFrame(PacketClass.Cmd, 38, PlainErrorPayload);
        port.RaiseDisconnected(OnlineDisconnectReason.PeerClosed);
        transport.Dispose();
    }

    [Fact]
    public void A_disconnect_notification_raises_disconnected_once_each_time_and_no_event()
    {
        using var probe = new Probe();

        probe.Port.RaiseDisconnected(OnlineDisconnectReason.ReadError);
        Assert.Equal(1, probe.DisconnectedCount);

        probe.Port.RaiseDisconnected(OnlineDisconnectReason.HeartbeatTimeout);
        Assert.Equal(2, probe.DisconnectedCount);
        Assert.Empty(probe.Events);
    }

    // ------------------------------------------------------------------ 订阅与释放

    [Fact]
    public void Construction_subscribes_once_to_each_session_event_and_dispose_removes_exactly_those_handlers()
    {
        var port = new FakeTownSessionPort();
        int sentinelFrames = 0;
        int sentinelDisconnects = 0;
        Action<OnlineReceivedFrame> sentinelFrame = _ => sentinelFrames++;
        Action<OnlineDisconnectReason> sentinelDisconnect = _ => sentinelDisconnects++;
        port.FrameReceived += sentinelFrame;
        port.Disconnected += sentinelDisconnect;
        Assert.Single(port.FrameHandlers());
        Assert.Single(port.DisconnectedHandlers());

        var transport = new OnlineTownSessionTransport(port);

        Assert.Equal(2, port.FrameHandlers().Length);
        Assert.Equal(2, port.DisconnectedHandlers().Length);

        transport.Dispose();

        Assert.Equal(new[] { sentinelFrame }, port.FrameHandlers());
        Assert.Equal(new[] { sentinelDisconnect }, port.DisconnectedHandlers());
        Assert.Equal(0, sentinelFrames);
        Assert.Equal(0, sentinelDisconnects);
    }

    [Fact]
    public void Dispose_is_idempotent_and_removes_each_subscription_exactly_once()
    {
        var port = new FakeTownSessionPort();
        var transport = new OnlineTownSessionTransport(port);
        Assert.Equal(1, port.FrameAdds);
        Assert.Equal(1, port.DisconnectedAdds);

        transport.Dispose();
        transport.Dispose();

        Assert.Empty(port.FrameHandlers());
        Assert.Empty(port.DisconnectedHandlers());
        // 第二次释放什么也不做:每个事件恰好退订一次,订阅与退订成对
        Assert.Equal(1, port.FrameRemoves);
        Assert.Equal(1, port.DisconnectedRemoves);
    }

    [Fact]
    public void Frames_and_disconnect_notifications_already_in_flight_when_the_transport_is_disposed_are_not_forwarded()
    {
        using var probe = new Probe();
        // 订阅列表在释放之前被取走的快照:模拟"事件已经在路上、订阅才被摘掉"
        Action<OnlineReceivedFrame> inFlightFrame = Assert.Single(probe.Port.FrameHandlers());
        Action<OnlineDisconnectReason> inFlightDisconnect = Assert.Single(probe.Port.DisconnectedHandlers());

        // 对照:释放之前同样的输入都会转发
        inFlightFrame(NetObjects.Frame(PacketClass.Noti, 23, UserAreaPayload));
        probe.Port.RaiseFrame(PacketClass.Noti, 199, KickPayload);
        inFlightDisconnect(OnlineDisconnectReason.WriteError);
        Assert.Equal(2, probe.Events.Count);
        Assert.Equal(1, probe.DisconnectedCount);

        probe.Transport.Dispose();

        inFlightFrame(NetObjects.Frame(PacketClass.Noti, 23, UserAreaPayload));
        inFlightFrame(NetObjects.Frame(PacketClass.Noti, 24, AreaUsersPayload));
        inFlightFrame(NetObjects.Frame(PacketClass.Noti, 199, KickPayload));
        inFlightFrame(NetObjects.Frame(PacketClass.Cmd, 38, PlainErrorPayload));
        inFlightFrame(null);
        inFlightDisconnect(OnlineDisconnectReason.PeerClosed);

        Assert.Equal(2, probe.Events.Count);
        Assert.Equal(1, probe.DisconnectedCount);
    }

    [Fact]
    public void A_null_session_port_or_session_is_rejected()
    {
        Assert.Equal("session", Assert.Throws<ArgumentNullException>(() => new OnlineTownSessionTransport((OnlineClientSession)null)).ParamName);
        Assert.Equal("session", Assert.Throws<ArgumentNullException>(() => new OnlineTownSessionTransport((OnlineTownSessionTransport.ITownSessionPort)null)).ParamName);
    }

    // ------------------------------------------------------------------ 发送编码

    [Fact]
    public async Task Send_uses_packet_id_38_and_the_eleven_byte_payload_in_wire_order_with_the_token_passed_through()
    {
        using var probe = new Probe();
        var calls = new List<(ushort PacketId, byte[] Payload, CancellationToken Token)>();
        probe.Port.OnSend = (packetId, payload, token) =>
        {
            calls.Add((packetId, payload, token));
            return Task.FromResult(FrameStatus.Ok);
        };
        using var cts = new CancellationTokenSource();

        // 目标村 49(0x31)、目标区 23(0x17)、x 2619(0x0A3B)、y 1481(0x05C9)、来源村 850(0x0352)、来源区 1921(0x0781)、标志 110(0x6E)
        await Waits.Bounded(probe.Transport.SendSetUserAreaAsync(49, 23, 2619, 1481, 850, 1921, 110, cts.Token));

        var call = Assert.Single(calls);
        Assert.Equal((ushort)38, call.PacketId);
        Assert.Equal(new byte[] { 0x31, 0x17, 0x3B, 0x0A, 0xC9, 0x05, 0x6E, 0x52, 0x03, 0x81, 0x07 }, call.Payload);
        Assert.Equal(cts.Token, call.Token);
        Assert.True(call.Token.CanBeCanceled);
    }

    [Fact]
    public async Task Send_encodes_each_argument_into_its_own_slot_when_the_values_change()
    {
        using var probe = new Probe();
        byte[] seen = null;
        probe.Port.OnSend = (packetId, payload, token) =>
        {
            seen = payload;
            return Task.FromResult(FrameStatus.Ok);
        };

        // 目标村 0x71、目标区 0x1B、x 0x1234、y 0x5678、来源村 0x9ABC、来源区 0x0DE0、标志 0x2F
        await Waits.Bounded(probe.Transport.SendSetUserAreaAsync(0x71, 0x1B, 0x1234, 0x5678, 0x9ABC, 0x0DE0, 0x2F, CancellationToken.None));

        Assert.Equal(new byte[] { 0x71, 0x1B, 0x34, 0x12, 0x78, 0x56, 0x2F, 0xBC, 0x9A, 0xE0, 0x0D }, seen);
    }

    [Fact]
    public async Task Send_succeeds_only_for_an_ok_status_and_throws_invalid_operation_for_every_other_status()
    {
        foreach (FrameStatus status in Enum.GetValues(typeof(FrameStatus)))
        {
            using var probe = new Probe();
            probe.Port.OnSend = (packetId, payload, token) => Task.FromResult(status);

            Task send = probe.Transport.SendSetUserAreaAsync(49, 23, 2619, 1481, 850, 1921, 110, CancellationToken.None);

            if (status == FrameStatus.Ok)
            {
                await Waits.Bounded(send);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Waits.Bounded(send));
            }
        }
    }

    [Fact]
    public async Task Send_failures_from_the_session_propagate_unchanged()
    {
        using var probe = new Probe();
        probe.Port.OnSend = (packetId, payload, token) => Task.FromException<FrameStatus>(new IOException("synthetic write failure"));

        IOException ex = await Assert.ThrowsAsync<IOException>(() =>
            Waits.Bounded(probe.Transport.SendSetUserAreaAsync(49, 23, 2619, 1481, 850, 1921, 110, CancellationToken.None)));

        Assert.Equal("synthetic write failure", ex.Message);
    }

    [Fact]
    public async Task Send_after_dispose_throws_object_disposed_and_never_reaches_the_session()
    {
        using var probe = new Probe();
        int calls = 0;
        probe.Port.OnSend = (packetId, payload, token) =>
        {
            calls++;
            return Task.FromResult(FrameStatus.Ok);
        };
        probe.Transport.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            Waits.Bounded(probe.Transport.SendSetUserAreaAsync(49, 23, 2619, 1481, 850, 1921, 110, CancellationToken.None)));

        Assert.Equal(0, calls);
    }

    // ------------------------------------------------------------------ 断开口

    [Fact]
    public async Task Disconnect_forwards_to_the_session_close_with_the_same_token_and_completes_with_it()
    {
        using var probe = new Probe();
        var tokens = new List<CancellationToken>();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Port.OnDisconnect = token =>
        {
            tokens.Add(token);
            return gate.Task;
        };
        using var cts = new CancellationTokenSource();

        Task disconnect = probe.Transport.DisconnectAsync(cts.Token);

        Assert.Equal(cts.Token, Assert.Single(tokens));
        Assert.False(disconnect.IsCompleted);
        gate.SetResult(true);
        await Waits.Bounded(disconnect);
    }

    [Fact]
    public async Task Disconnect_failures_from_the_session_propagate_unchanged()
    {
        using var probe = new Probe();
        probe.Port.OnDisconnect = token => Task.FromException(new IOException("synthetic close failure"));

        IOException ex = await Assert.ThrowsAsync<IOException>(() => Waits.Bounded(probe.Transport.DisconnectAsync(CancellationToken.None)));

        Assert.Equal("synthetic close failure", ex.Message);
    }

    // ------------------------------------------------------------------ 默认接线(真会话)

    [Fact]
    public void On_a_real_session_construction_subscribes_once_to_each_event_and_dispose_removes_them_without_releasing_the_session()
    {
        using var session = SessionProbe.NewUnconnected();
        int sentinelFrames = 0;
        int sentinelDisconnects = 0;
        Action<OnlineReceivedFrame> sentinelFrame = _ => sentinelFrames++;
        Action<OnlineDisconnectReason> sentinelDisconnect = _ => sentinelDisconnects++;
        session.FrameReceived += sentinelFrame;
        session.Disconnected += sentinelDisconnect;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));

        var transport = new OnlineTownSessionTransport(session);

        Assert.Equal(2, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(2, SessionProbe.DisconnectedCount(session));

        transport.Dispose();
        transport.Dispose();

        Assert.Equal((Delegate)sentinelFrame, Assert.Single(SessionProbe.Handlers(session, "FrameReceived")));
        Assert.Equal((Delegate)sentinelDisconnect, Assert.Single(SessionProbe.Handlers(session, "Disconnected")));
        Assert.Equal(0, sentinelFrames);
        Assert.Equal(0, sentinelDisconnects);
        Assert.NotEqual(OnlineClientSessionState.Disposed, session.State);
    }

    [Fact]
    public void On_a_real_session_the_event_handlers_forward_frames_and_disconnects_and_stop_after_dispose()
    {
        using var probe = new RealProbe();
        var onFrame = (Action<OnlineReceivedFrame>)Assert.Single(SessionProbe.Handlers(probe.Session, "FrameReceived"));
        var onDisconnected = (Action<OnlineDisconnectReason>)Assert.Single(SessionProbe.Handlers(probe.Session, "Disconnected"));

        onFrame(null);
        onFrame(NetObjects.Frame(PacketClass.Noti, 23, UserAreaPayload));
        onFrame(NetObjects.Frame(PacketClass.Cmd, 38, ForbiddenErrorPayload));
        onDisconnected(OnlineDisconnectReason.ReadError);

        Assert.Equal(2, probe.Events.Count);
        AssertEvent(probe.Events[0], TownInboundKind.UserArea, village: 37, area: 61, uniqueId: 1203);
        AssertEvent(probe.Events[1], TownInboundKind.SetUserAreaError, errorCode: 0x44, minutes: 1530);
        Assert.Equal(1, probe.DisconnectedCount);

        probe.Transport.Dispose();
        onFrame(NetObjects.Frame(PacketClass.Noti, 23, UserAreaPayload));
        onDisconnected(OnlineDisconnectReason.PeerClosed);

        Assert.Equal(2, probe.Events.Count);
        Assert.Equal(1, probe.DisconnectedCount);
    }

    [Fact]
    public async Task By_default_send_goes_to_the_real_session_which_rejects_commands_before_character_selection()
    {
        using var probe = new RealProbe();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Waits.Bounded(probe.Transport.SendSetUserAreaAsync(49, 23, 2619, 1481, 850, 1921, 110, CancellationToken.None)));

        // 不是本适配器自己的"编码失败"(那是非 Ok 状态才有的),而是会话在选角之前拒绝发命令
        Assert.DoesNotContain("编码失败", ex.Message);
    }

    [Fact]
    public async Task By_default_disconnect_goes_to_the_real_session_and_honours_its_cancellation_token()
    {
        using var probe = new RealProbe();

        await Waits.Bounded(probe.Transport.DisconnectAsync(CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Waits.Bounded(probe.Transport.DisconnectAsync(cts.Token)));
    }
}
