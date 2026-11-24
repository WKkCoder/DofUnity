using System.Collections;
using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算传输适配器(<see cref="RealOnlineSettlementTransport"/>)第二批边界测试:
/// ① 发命令时空载荷按"空字节数组"交给底层会话(底层对 null 载荷会拒绝,适配器必须先换成空数组),包号、载荷、取消令牌原样交下去;
/// ② 帧订阅与断线订阅在"退订之后再订阅同一个委托"时重新生效(退订要同时从适配器自己的登记里摘掉,不能只摘底层会话上的那一份);
/// ③ 空委托与空会话、释放之后的发送。
/// <para>
/// 依赖的私有/内部名字(联网层没有对测试开放内部成员,没有别的办法造出"处于选角成功、心跳泵运行"的真会话):
/// <see cref="OnlineClientSession"/> 的属性 <c>State</c> / <c>HeartbeatState</c>(私有 set)、私有字段 <c>_activeMailbox</c>,
/// 以及私有嵌套类 <c>CommandMailbox</c>(默认构造、私有队列字段 <c>_queue</c>)与队列元素 <c>PendingCommand</c> 的公开属性 <c>PacketId</c> / <c>Payload</c>。
/// 发命令的真实路径是"把命令放进信箱,由心跳泵取走写出";这里不启动泵,只看信箱里排了什么(命令留在信箱里,会话释放时才以"已释放"收尾)。
/// 订阅数与底层订阅者经既有的 <c>SessionProbe</c> 读取。
/// </para>
/// </summary>
public sealed class OnlineSettlementGapA2TransportTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>把未连接的真会话拨成"选角成功 + 心跳泵运行 + 有命令信箱"(发命令的前置条件),返回信箱对象。</summary>
    private static object PrepareCommandMailbox(OnlineClientSession session)
    {
        Type sessionType = typeof(OnlineClientSession);
        PropertyInfo state = sessionType.GetProperty("State", Any);
        Assert.NotNull(state);
        state.SetValue(session, OnlineClientSessionState.CharacterSelected);
        PropertyInfo heartbeat = sessionType.GetProperty("HeartbeatState", Any);
        Assert.NotNull(heartbeat);
        heartbeat.SetValue(session, OnlineHeartbeatState.Running);

        Type mailboxType = sessionType.GetNestedType("CommandMailbox", BindingFlags.NonPublic);
        Assert.NotNull(mailboxType);
        object mailbox = Activator.CreateInstance(mailboxType, true);
        Assert.NotNull(mailbox);
        FieldInfo field = sessionType.GetField("_activeMailbox", Any);
        Assert.NotNull(field);
        field.SetValue(session, mailbox);
        return mailbox;
    }

    /// <summary>信箱里按入队顺序排着的命令(包号, 载荷)。</summary>
    private static List<(ushort PacketId, byte[] Payload)> Queued(object mailbox)
    {
        FieldInfo queue = mailbox.GetType().GetField("_queue", Any);
        Assert.NotNull(queue);
        var result = new List<(ushort, byte[])>();
        foreach (object command in (IEnumerable)queue.GetValue(mailbox))
        {
            Type type = command.GetType();
            var id = (ushort)type.GetProperty("PacketId", Any).GetValue(command);
            var payload = (byte[])type.GetProperty("Payload", Any).GetValue(command);
            result.Add((id, payload));
        }
        return result;
    }

    [Fact]
    public async Task A_null_payload_reaches_the_session_as_an_empty_non_null_array_and_other_arguments_pass_through()
    {
        // 保护:适配器发命令时空载荷换成空字节数组再交给底层会话——底层对 null 载荷会返回"参数为空"的失败任务,
        // 所以适配器自己的返回任务既不能已失败、也不能抛;信箱里排到的那条命令包号是 902、载荷是非空引用的 0 长度数组。
        // 同一个适配器接着发一条带载荷的命令(包号 901,载荷 {1,2,3}),原样排在后面;已取消的令牌原样交下去(返回已取消的任务)。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        object mailbox = PrepareCommandMailbox(session);
        var transport = new RealOnlineSettlementTransport(session);

        Task<FrameStatus> nullPayload = transport.SendCommandAsync(902, null, CancellationToken.None);
        Assert.False(nullPayload.IsCompleted);
        Assert.False(nullPayload.IsFaulted);
        List<(ushort PacketId, byte[] Payload)> queued = Queued(mailbox);
        Assert.Single(queued);
        Assert.Equal((ushort)902, queued[0].PacketId);
        Assert.NotNull(queued[0].Payload);
        Assert.Empty(queued[0].Payload);

        Task<FrameStatus> withPayload = transport.SendCommandAsync(901, new byte[] { 1, 2, 3 }, CancellationToken.None);
        Assert.False(withPayload.IsCompleted);
        queued = Queued(mailbox);
        Assert.Equal(2, queued.Count);
        Assert.Equal((ushort)902, queued[0].PacketId);
        Assert.Empty(queued[0].Payload);
        Assert.Equal((ushort)901, queued[1].PacketId);
        Assert.Equal(new byte[] { 1, 2, 3 }, queued[1].Payload);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Task<FrameStatus> notSent = transport.SendCommandAsync(903, Array.Empty<byte>(), cancelled.Token);
        Assert.True(notSent.IsCanceled);
        Assert.Equal(2, Queued(mailbox).Count);   // 已取消的命令没有排进信箱

        // 会话释放时,信箱里还没写出的两条命令以"已释放"收尾(顺便把两个任务观察掉)
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => nullPayload);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => withPayload);
    }

    [Fact]
    public async Task Sending_through_a_disposed_transport_throws_and_never_touches_the_session()
    {
        // 保护:适配器释放之后再发命令:抛"已释放"(对象名是适配器的类型名;是调用时直接抛、不是返回失败任务),不会去碰底层会话——信箱里什么都没排。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        object mailbox = PrepareCommandMailbox(session);
        var transport = new RealOnlineSettlementTransport(session);
        transport.Dispose();

        ObjectDisposedException error = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.SendCommandAsync(902, new byte[] { 7 }, CancellationToken.None));
        Assert.Equal("RealOnlineSettlementTransport", error.ObjectName);
        Assert.Empty(Queued(mailbox));
    }

    [Fact]
    public void A_disconnect_handler_unsubscribed_and_subscribed_again_is_registered_again_on_the_session()
    {
        // 保护:断线处理委托退订后再订阅同一个委托要重新生效——适配器退订时必须把它从自己的登记里摘掉(不能只摘底层会话上的那份),
        // 否则再订阅会被当成"已订阅过"而吞掉。三轮"订阅→退订"之后再订阅一次,底层会话上恰好一份;触发底层断线,委托收到原因一次。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        var reasons = new List<OnlineDisconnectReason>();
        Action<OnlineDisconnectReason> handler = reasons.Add;

        for (int round = 0; round < 3; round++)
        {
            transport.Disconnected += handler;
            Assert.Equal(1, SessionProbe.DisconnectedCount(session));
            transport.Disconnected -= handler;
            Assert.Equal(0, SessionProbe.DisconnectedCount(session));
        }

        transport.Disconnected += handler;
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));
        Delegate registered = Assert.Single(SessionProbe.Handlers(session, "Disconnected"));
        Assert.Equal(handler, registered);
        registered.DynamicInvoke(OnlineDisconnectReason.HeartbeatTimeout);
        Assert.Equal(new[] { OnlineDisconnectReason.HeartbeatTimeout }, reasons);

        // 重复订阅仍然只登记一份
        transport.Disconnected += handler;
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));
    }

    [Fact]
    public void Removing_one_of_two_disconnect_handlers_and_adding_it_back_restores_both()
    {
        // 保护:两个不同的断线处理委托,退订其中一个只摘它自己(另一个原样留着、仍被调用);把它再订阅回来,两个都登记、都被调用,订阅顺序是先留下的、后补回的。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        var calls = new List<string>();
        Action<OnlineDisconnectReason> first = r => calls.Add("first:" + r);
        Action<OnlineDisconnectReason> second = r => calls.Add("second:" + r);

        transport.Disconnected += first;
        transport.Disconnected += second;
        transport.Disconnected -= first;
        Delegate[] afterRemove = SessionProbe.Handlers(session, "Disconnected");
        Assert.Single(afterRemove);
        afterRemove[0].DynamicInvoke(OnlineDisconnectReason.ReadError);
        Assert.Equal(new[] { "second:ReadError" }, calls);

        transport.Disconnected += first;
        Delegate[] afterAdd = SessionProbe.Handlers(session, "Disconnected");
        Assert.Equal(2, afterAdd.Length);
        calls.Clear();
        foreach (Delegate d in afterAdd)
        {
            d.DynamicInvoke(OnlineDisconnectReason.WriteError);
        }
        Assert.Equal(new[] { "second:WriteError", "first:WriteError" }, calls);

        // 释放把剩下的都摘干净,之后再订阅无效
        transport.Dispose();
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
        transport.Disconnected += first;
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
    }

    [Fact]
    public void A_frame_handler_unsubscribed_and_subscribed_again_is_bridged_again()
    {
        // 保护:帧处理委托退订后再订阅同一个委托要重新生效(底层会话上的桥接订阅数回到 1,来一帧转发一次);
        // 两个不同委托:退订一个只摘它自己的桥接,补回之后两个都被转发。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        var calls = new List<string>();
        Action<PacketClass, int, byte[]> first = (c, id, p) => calls.Add("first:" + c + "/" + id + "/" + p.Length);
        Action<PacketClass, int, byte[]> second = (c, id, p) => calls.Add("second:" + c + "/" + id + "/" + p.Length);

        for (int round = 0; round < 3; round++)
        {
            transport.FrameReceived += first;
            Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
            transport.FrameReceived -= first;
            Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        }

        transport.FrameReceived += first;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        SessionProbe.Handlers(session, "FrameReceived")[0].DynamicInvoke(NetObjects.Frame(PacketClass.Cmd, 72, new byte[] { 1 }));
        Assert.Equal(new[] { "first:Cmd/72/1" }, calls);

        transport.FrameReceived += second;
        transport.FrameReceived -= first;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        calls.Clear();
        SessionProbe.Handlers(session, "FrameReceived")[0].DynamicInvoke(NetObjects.Frame(PacketClass.Noti, 2, new byte[] { 0, 1, 0 }));
        Assert.Equal(new[] { "second:Noti/2/3" }, calls);

        transport.FrameReceived += first;
        Delegate[] bridges = SessionProbe.Handlers(session, "FrameReceived");
        Assert.Equal(2, bridges.Length);
        calls.Clear();
        foreach (Delegate bridge in bridges)
        {
            bridge.DynamicInvoke(NetObjects.Frame(PacketClass.Noti, 24, new byte[] { 1, 2, 0, 0 }));
        }
        Assert.Equal(new[] { "second:Noti/24/4", "first:Noti/24/4" }, calls);
    }

    [Fact]
    public void Null_handlers_and_a_null_session_are_rejected_or_ignored_without_side_effects()
    {
        // 保护:空会话构造抛"参数为空"(参数名 session);帧与断线两个事件上,订阅/退订空委托都是无副作用的空操作(不抛、不多出订阅者);
        // 释放之后退订原先订阅过的委托也不抛。
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => new RealOnlineSettlementTransport(null));
        Assert.Equal("session", error.ParamName);

        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        transport.FrameReceived += null;
        transport.FrameReceived -= null;
        transport.Disconnected += null;
        transport.Disconnected -= null;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));

        Action<PacketClass, int, byte[]> onFrame = (_, _, _) => { };
        Action<OnlineDisconnectReason> onDisconnect = _ => { };
        transport.FrameReceived += onFrame;
        transport.Disconnected += onDisconnect;
        transport.FrameReceived -= null;
        transport.Disconnected -= null;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));

        transport.Dispose();
        transport.FrameReceived -= onFrame;
        transport.Disconnected -= onDisconnect;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
    }
}
