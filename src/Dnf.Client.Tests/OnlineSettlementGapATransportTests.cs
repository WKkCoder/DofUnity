using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算传输适配器(<see cref="RealOnlineSettlementTransport"/>)包着真会话(<see cref="OnlineClientSession"/>,构造不做网络动作)的行为测试:
/// "是否已断线"的各个来源、帧订阅的桥接与幂等、断线订阅的幂等与释放时全部摘除、释放之后帧不再转给订阅者。
/// 全程没有套接字、没有线程等待:游戏会话(<see cref="GameClientSession"/>)读内存流,读立即完成。
/// <para>
/// 依赖的私有/内部名字(都在联网层,没有别的办法造出"有频道连接的真会话"与"底层会话上的订阅者"):
/// ① <see cref="OnlineClientSession"/> 的私有字段 <c>_link</c>,值是 internal 类 <c>Dnf.Net.Session.OnlineChannelLink</c>,
///    用它的 internal 构造 <c>(GameClientSession, IDisposable, uint, OnlineObservedStream)</c> 经反射建出再塞进去(与 <c>OnlineDungeonAdapterGapPortTests</c> 同一做法,这里复制成自己的辅助);
/// ② <see cref="OnlineClientSession"/> 上字段式事件 <c>FrameReceived</c> / <c>Disconnected</c> 的底层委托字段(经既有的 <c>SessionProbe.Handlers</c> 读取订阅者列表,
///    并用 <c>DynamicInvoke</c> 触发其中的桥接委托,模拟"底层会话来了一帧");
/// ③ <see cref="OnlineReceivedFrame"/> 的 internal 构造(经既有的 <c>NetObjects.Frame</c> 建)。
/// </para>
/// </summary>
public sealed class OnlineSettlementGapATransportTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>收包窗口的上界(从不真的等:内存流的读立即完成,收包循环一读到结果就退出)。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>把一条以给定游戏会话为底的频道连接塞进会话(私有字段 <c>_link</c>)。</summary>
    private static void AttachLink(OnlineClientSession session, GameClientSession game)
    {
        Type linkType = typeof(OnlineClientSession).Assembly.GetType("Dnf.Net.Session.OnlineChannelLink");
        Assert.NotNull(linkType);
        object link = Activator.CreateInstance(linkType, Any, null, new object[] { game, null, 0u, null }, null);
        Assert.NotNull(link);
        FieldInfo field = typeof(OnlineClientSession).GetField("_link", Any);
        Assert.NotNull(field);
        field.SetValue(session, link);
    }

    /// <summary>读空的内存流:第一次读到 0 字节,游戏会话记下"对端已关闭",帧层没出错。</summary>
    private static async Task<GameClientSession> ClosedGameAsync()
    {
        var game = new GameClientSession(new MemoryStream());
        await game.ReceiveAsync(Window);
        Assert.True(game.PeerClosed);
        Assert.Equal(FrameError.None, game.LastFrameError);
        return game;
    }

    /// <summary>读到 15 个零字节:帧头里的总长度 0 小于最小帧长,帧层记下切帧错误(长度低于下限),对端并没有关闭。</summary>
    private static async Task<GameClientSession> FaultedGameAsync()
    {
        var game = new GameClientSession(new MemoryStream(new byte[15]));
        await game.ReceiveAsync(Window);
        Assert.False(game.PeerClosed);
        Assert.Equal(FrameError.LengthBelowMinimum, game.LastFrameError);
        return game;
    }

    [Fact]
    public async Task The_transport_is_disconnected_for_each_broken_source_and_connected_only_when_every_source_is_healthy()
    {
        // 保护:是否已断线由四个来源各自独立决定——会话已释放、没有频道连接、对端已关闭、帧层出过错,任何一个成立都算断线;
        // 四个都不成立(会话未释放、有连接、对端没关、帧层没错)才算在线。
        // 没有频道连接
        using (OnlineClientSession noLink = SessionProbe.NewUnconnected())
        {
            var transport = new RealOnlineSettlementTransport(noLink);
            Assert.Null(noLink.GameSession);
            Assert.True(transport.IsDisconnected);
        }

        // 健康的连接:在线(会话未释放、对端没关、帧层没错)
        using (OnlineClientSession healthy = SessionProbe.NewUnconnected())
        {
            var game = new GameClientSession(new MemoryStream());
            AttachLink(healthy, game);
            var transport = new RealOnlineSettlementTransport(healthy);
            Assert.NotEqual(OnlineClientSessionState.Disposed, healthy.State);
            Assert.False(game.PeerClosed);
            Assert.Equal(FrameError.None, game.LastFrameError);
            Assert.False(transport.IsDisconnected);
        }

        // 只有对端已关闭这一个来源
        using (OnlineClientSession closed = SessionProbe.NewUnconnected())
        {
            AttachLink(closed, await ClosedGameAsync());
            Assert.True(new RealOnlineSettlementTransport(closed).IsDisconnected);
        }

        // 只有帧层出过错这一个来源(对端没关闭)
        using (OnlineClientSession faulted = SessionProbe.NewUnconnected())
        {
            AttachLink(faulted, await FaultedGameAsync());
            Assert.True(new RealOnlineSettlementTransport(faulted).IsDisconnected);
        }

        // 会话释放:释放之前同一条健康连接在线,释放之后断线
        OnlineClientSession disposable = SessionProbe.NewUnconnected();
        AttachLink(disposable, new GameClientSession(new MemoryStream()));
        var disposableTransport = new RealOnlineSettlementTransport(disposable);
        Assert.False(disposableTransport.IsDisconnected);
        disposable.Dispose();
        Assert.Equal(OnlineClientSessionState.Disposed, disposable.State);
        Assert.True(disposableTransport.IsDisconnected);
    }

    [Fact]
    public async Task A_session_that_is_disposed_stays_reported_as_disconnected_whatever_its_connection_was()
    {
        // 保护:会话释放之后一直算断线,不管释放前有没有频道连接、连接是不是已经关了;
        // 创建出来但没有连接、直接释放的会话同样断线。
        OnlineClientSession neverConnected = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(neverConnected);
        Assert.True(transport.IsDisconnected);   // 还没释放:没有连接,断线
        neverConnected.Dispose();
        Assert.Equal(OnlineClientSessionState.Disposed, neverConnected.State);
        Assert.True(transport.IsDisconnected);

        // 对端已关闭的连接,释放之后依然断线
        OnlineClientSession closed = SessionProbe.NewUnconnected();
        AttachLink(closed, await ClosedGameAsync());
        var closedTransport = new RealOnlineSettlementTransport(closed);
        Assert.True(closedTransport.IsDisconnected);
        closed.Dispose();
        Assert.True(closedTransport.IsDisconnected);
    }

    [Fact]
    public void A_frame_from_the_underlying_session_reaches_the_subscriber_with_the_same_class_id_and_payload()
    {
        // 保护:订阅帧事件时在底层会话上挂一个桥接委托;底层来了一帧(未释放),订阅者恰好收到一次,包类、包号、载荷与送入的逐项相同。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        var received = new List<(PacketClass Class, int Id, byte[] Payload)>();
        transport.FrameReceived += (cls, id, payload) => received.Add((cls, id, payload));

        Delegate[] bridges = SessionProbe.Handlers(session, "FrameReceived");
        Assert.Single(bridges);

        bridges[0].DynamicInvoke(NetObjects.Frame(PacketClass.Noti, 23, new byte[] { 1, 2, 3 }));
        Assert.Single(received);
        Assert.Equal(PacketClass.Noti, received[0].Class);
        Assert.Equal(23, received[0].Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, received[0].Payload);

        bridges[0].DynamicInvoke(NetObjects.Frame(PacketClass.Cmd, 75, new byte[] { 1, 1, 2 }));
        Assert.Equal(2, received.Count);
        Assert.Equal(PacketClass.Cmd, received[1].Class);
        Assert.Equal(75, received[1].Id);
        Assert.Equal(new byte[] { 1, 1, 2 }, received[1].Payload);
    }

    [Fact]
    public void A_null_frame_from_the_underlying_session_is_dropped_without_reaching_the_subscriber()
    {
        // 保护:底层送来的空帧被丢弃——订阅者不被调用、也不抛异常;随后的正常帧仍然照常转发(空帧没有把桥接弄坏)。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        int calls = 0;
        transport.FrameReceived += (_, _, _) => calls++;
        Delegate bridge = Assert.Single(SessionProbe.Handlers(session, "FrameReceived"));

        bridge.DynamicInvoke(new object[] { null });
        Assert.Equal(0, calls);

        bridge.DynamicInvoke(NetObjects.Frame(PacketClass.Noti, 2, new byte[] { 0, 1, 0 }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_frame_arriving_through_an_old_bridge_after_the_transport_is_disposed_is_dropped()
    {
        // 保护:传输适配器释放之后,桥接已从底层会话摘掉;即使有一帧已经拿着旧桥接在途(这里手动调用留存的旧桥接),
        // 订阅者也不再被调用(释放之前同一座桥接是通的,证明差别来自释放)。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        int calls = 0;
        transport.FrameReceived += (_, _, _) => calls++;
        Delegate bridge = Assert.Single(SessionProbe.Handlers(session, "FrameReceived"));

        bridge.DynamicInvoke(NetObjects.Frame(PacketClass.Noti, 24, new byte[] { 1, 2, 0, 0 }));
        Assert.Equal(1, calls);

        transport.Dispose();
        Assert.Empty(SessionProbe.Handlers(session, "FrameReceived"));

        bridge.DynamicInvoke(NetObjects.Frame(PacketClass.Noti, 24, new byte[] { 1, 2, 0, 0 }));
        bridge.DynamicInvoke(new object[] { null });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Subscribing_the_same_frame_handler_twice_bridges_it_once_and_one_unsubscribe_removes_the_bridge()
    {
        // 保护:同一个帧处理委托订阅两次只桥接一次(底层会话上只有一个订阅者,一帧只转发一次);退订一次就把桥接摘干净(不留孤儿桥接);
        // 退订一个没订阅过的委托是空操作;空委托的订阅被忽略。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        int calls = 0;
        Action<PacketClass, int, byte[]> handler = (_, _, _) => calls++;

        transport.FrameReceived += handler;
        transport.FrameReceived += handler;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        SessionProbe.Handlers(session, "FrameReceived")[0].DynamicInvoke(NetObjects.Frame(PacketClass.Cmd, 72, new byte[] { 1 }));
        Assert.Equal(1, calls);

        transport.FrameReceived -= (_, _, _) => { };   // 没订阅过的委托
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));

        transport.FrameReceived -= handler;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));

        transport.FrameReceived += null;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));

        // 退订之后可以重新订阅
        transport.FrameReceived += handler;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
    }

    [Fact]
    public void Subscribing_the_same_disconnect_handler_twice_registers_it_once_on_the_session()
    {
        // 保护:同一个断线处理委托订阅两次只在底层会话上登记一份;底层触发断线时它只被调用一次且收到原因;
        // 退订一次就摘干净;不同的委托各自登记。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        var reasons = new List<OnlineDisconnectReason>();
        Action<OnlineDisconnectReason> first = reasons.Add;
        var otherReasons = new List<OnlineDisconnectReason>();
        Action<OnlineDisconnectReason> second = otherReasons.Add;

        transport.Disconnected += first;
        transport.Disconnected += first;
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));

        transport.Disconnected += second;
        Assert.Equal(2, SessionProbe.DisconnectedCount(session));

        foreach (Delegate d in SessionProbe.Handlers(session, "Disconnected"))
        {
            d.DynamicInvoke(OnlineDisconnectReason.PeerClosed);
        }
        Assert.Equal(new[] { OnlineDisconnectReason.PeerClosed }, reasons);
        Assert.Equal(new[] { OnlineDisconnectReason.PeerClosed }, otherReasons);

        transport.Disconnected -= first;
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));
        transport.Disconnected -= first;   // 已退订:空操作
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));
        transport.Disconnected -= second;
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
    }

    [Fact]
    public void Disposing_the_transport_removes_every_frame_and_disconnect_subscription_but_keeps_the_session_alive()
    {
        // 保护:释放传输适配器时摘掉它挂在底层会话上的全部订阅(多个帧订阅、多个断线订阅,包括先订阅的那一个),
        // 但不释放底层会话;重复释放是空操作。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        transport.FrameReceived += (_, _, _) => { };
        transport.FrameReceived += (_, _, _) => { };
        transport.Disconnected += _ => { };
        transport.Disconnected += _ => { };
        transport.Disconnected += _ => { };
        Assert.Equal(2, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(3, SessionProbe.DisconnectedCount(session));

        transport.Dispose();
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
        Assert.NotEqual(OnlineClientSessionState.Disposed, session.State);

        transport.Dispose();
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
    }

    [Fact]
    public void Subscriptions_made_after_the_transport_is_disposed_are_ignored()
    {
        // 保护:传输适配器释放之后再订阅帧或断线事件无效——底层会话上不会多出订阅者。
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var transport = new RealOnlineSettlementTransport(session);
        transport.Dispose();

        transport.FrameReceived += (_, _, _) => { };
        transport.Disconnected += _ => { };

        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
    }
}
