using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 联网副本会话接缝真实现(<c>RealOnlineDungeonSessionPort</c>)的第三轮缺口测试(不改上一轮的 <c>OnlineDungeonAdapterGapPortTests</c>):
/// 上一轮只钉了"是否已断线"的判断;这里补接缝的其余成员——构造的空引用检查、原始会话访问、帧事件桥(转发的三元组、空帧不转发、
/// 空委托的订阅与退订被忽略、退订确实摘掉桥)、断线事件直通、独占收包两个调用向会话转发、创建副本跑者的前置条件与"不挂旧保活泵"。
/// 真会话由 <c>SessionProbe</c> 造(构造不做网络动作),事件经反射读会话的同名私有字段后手动触发,没有任何套接字、线程或墙钟。
/// </summary>
public sealed class OnlineDungeonAdapterGapR3PortTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>手动触发会话上字段式事件(同名私有字段就是订阅列表);没有订阅者时什么也不发生。</summary>
    private static void Raise<T>(OnlineClientSession session, string eventName, T argument)
    {
        FieldInfo field = typeof(OnlineClientSession).GetField(eventName, Any);
        Assert.NotNull(field);
        var handler = (Action<T>)field.GetValue(session);
        handler?.Invoke(argument);
    }

    // 从上一轮 Port 测试复制来的辅助:把一条以给定游戏会话为底的频道连接塞进会话(私有字段 _link,值是 internal 类 OnlineChannelLink)。
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

    /// <summary>
    /// 保护:接缝构造时会话不能为空(抛参数为空异常,参数名 session),原始会话访问器返回的就是传进去的那个会话对象。
    /// </summary>
    [Fact]
    public void PortRejectsNullSessionAndExposesTheRawSession()
    {
        Assert.Equal("session", Assert.Throws<ArgumentNullException>(() => new RealOnlineDungeonSessionPort(null)).ParamName);

        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var port = new RealOnlineDungeonSessionPort(session);
        Assert.Same(session, port.RawSession);
        Assert.IsAssignableFrom<IOnlineDungeonSessionPort>(port);
    }

    /// <summary>
    /// 保护:帧事件桥——订阅后会话上多一个订阅者;会话收到的帧被拆成 (包类, 包号, 载荷) 三元组原样转给订阅者(两帧用互不相同的类与包号,防串位),
    /// 载荷内容逐字节一致;空帧不转发(不抛异常);订阅或退订空委托被忽略(订阅者数不变);退订一个从没订阅过的委托没有影响;
    /// 退订自己的委托后会话上的订阅者摘掉、之后的帧不再送达,同一委托可再次订阅;两个委托各有各的桥,互不影响。
    /// </summary>
    [Fact]
    public void FrameBridgeForwardsTripleAndSkipsNullFramesAndUnsubscribesTheRightBridge()
    {
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var port = new RealOnlineDungeonSessionPort(session);
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));

        var first = new List<(PacketClass Class, int Id, byte[] Payload)>();
        var second = new List<(PacketClass Class, int Id, byte[] Payload)>();
        Action<PacketClass, int, byte[]> onFirst = (c, i, p) => first.Add((c, i, p));
        Action<PacketClass, int, byte[]> onSecond = (c, i, p) => second.Add((c, i, p));

        // 空委托:订阅与退订都被忽略
        port.FrameReceived += null;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        port.FrameReceived -= null;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));

        port.FrameReceived += onFirst;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));

        Raise(session, "FrameReceived", NetObjects.Frame(PacketClass.Noti, 77, new byte[] { 1, 2, 3 }));
        Raise(session, "FrameReceived", NetObjects.Frame(PacketClass.Cmd, 1234, new byte[] { 9 }));
        Raise(session, "FrameReceived", (OnlineReceivedFrame)null); // 空帧:不转发、不抛异常
        Assert.Equal(2, first.Count);
        Assert.Equal(PacketClass.Noti, first[0].Class);
        Assert.Equal(77, first[0].Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, first[0].Payload);
        Assert.Equal(PacketClass.Cmd, first[1].Class);
        Assert.Equal(1234, first[1].Id);
        Assert.Equal(new byte[] { 9 }, first[1].Payload);

        // 退订一个从没订阅过的委托:不影响已有的桥
        port.FrameReceived -= onSecond;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));

        // 第二个委托有自己的桥
        port.FrameReceived += onSecond;
        Assert.Equal(2, SessionProbe.FrameReceivedCount(session));
        Raise(session, "FrameReceived", NetObjects.Frame(PacketClass.Noti, 5, new byte[] { 4, 5 }));
        Assert.Equal(3, first.Count);
        Assert.Single(second);
        Assert.Equal(5, second[0].Id);

        // 退订第一个:只摘它自己的桥,第二个照常收;再退订一次是空操作
        port.FrameReceived -= onFirst;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        port.FrameReceived -= onFirst;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        Raise(session, "FrameReceived", NetObjects.Frame(PacketClass.Noti, 6, new byte[] { 6 }));
        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(6, second[1].Id);

        // 摘干净之后,同一委托可以重新订阅并重新收到帧
        port.FrameReceived -= onSecond;
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Raise(session, "FrameReceived", NetObjects.Frame(PacketClass.Noti, 7, new byte[] { 7 }));
        Assert.Equal(2, second.Count);
        port.FrameReceived += onSecond;
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        Raise(session, "FrameReceived", NetObjects.Frame(PacketClass.Cmd, 8, new byte[] { 8 }));
        Assert.Equal(3, second.Count);
        Assert.Equal((PacketClass.Cmd, 8), (second[2].Class, second[2].Id));
    }

    /// <summary>
    /// 保护:断线事件直通会话——订阅在会话上多一个订阅者、会话发出的断线原因原样送到订阅者(取不同原因各验一次)、退订后摘掉且不再收到。
    /// </summary>
    [Fact]
    public void DisconnectedEventIsForwardedStraightToTheSession()
    {
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        var port = new RealOnlineDungeonSessionPort(session);
        var reasons = new List<OnlineDisconnectReason>();
        Action<OnlineDisconnectReason> handler = reasons.Add;
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));

        port.Disconnected += handler;
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));

        Raise(session, "Disconnected", OnlineDisconnectReason.WriteError);
        Raise(session, "Disconnected", OnlineDisconnectReason.PeerClosed);
        Raise(session, "Disconnected", OnlineDisconnectReason.HeartbeatTimeout);
        Assert.Equal(
            new[] { OnlineDisconnectReason.WriteError, OnlineDisconnectReason.PeerClosed, OnlineDisconnectReason.HeartbeatTimeout },
            reasons.ToArray());

        port.Disconnected -= handler;
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
        Raise(session, "Disconnected", OnlineDisconnectReason.ReadError);
        Assert.Equal(3, reasons.Count);
    }

    /// <summary>
    /// 保护:进入/离开独占收包两个调用都是向会话转发(不是自己吞掉):未选角的会话上进入独占抛"只有选角成功后才能进入独占收包",
    /// 不处于独占状态时离开抛"只有处于独占收包状态时才能切回会话收包",会话释放后两者都抛已释放异常;
    /// 调用方给的取消令牌也原样带给会话(已取消的令牌在进门闩前就抛取消异常,而不是先落到状态检查)。
    /// </summary>
    [Fact]
    public async Task ExclusiveReceiveCallsAreForwardedToTheSessionWithTheCancellationToken()
    {
        OnlineClientSession session = SessionProbe.NewUnconnected();
        var port = new RealOnlineDungeonSessionPort(session);

        InvalidOperationException enter = await Assert.ThrowsAsync<InvalidOperationException>(() => port.EnterExclusiveReceiveAsync(CancellationToken.None));
        Assert.Equal("只有选角成功后才能进入独占收包", enter.Message);
        InvalidOperationException leave = await Assert.ThrowsAsync<InvalidOperationException>(() => port.LeaveExclusiveReceiveAsync(CancellationToken.None));
        Assert.Equal("只有处于独占收包状态时才能切回会话收包", leave.Message);

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.EnterExclusiveReceiveAsync(canceled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.LeaveExclusiveReceiveAsync(canceled.Token));
        }

        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => port.EnterExclusiveReceiveAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => port.LeaveExclusiveReceiveAsync(CancellationToken.None));
    }

    /// <summary>
    /// 保护:创建副本跑者需要会话已有底层连接——没有连接时抛"在线会话没有就绪的底层连接。";有连接时造出跑者,
    /// 且跑者不挂旧保活泵(保活委托为空):进副本返回后由会话自己的心跳泵接手,不能出现两个读者。
    /// </summary>
    [Fact]
    public void CreateDungeonRunnerNeedsALinkAndLeavesTheKeepAliveOff()
    {
        using (OnlineClientSession unlinked = SessionProbe.NewUnconnected())
        {
            var port = new RealOnlineDungeonSessionPort(unlinked);
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => port.CreateDungeonRunner());
            Assert.Equal("在线会话没有就绪的底层连接。", ex.Message);
        }

        using (OnlineClientSession linked = SessionProbe.NewUnconnected())
        {
            AttachLink(linked, new GameClientSession(new MemoryStream()));
            var port = new RealOnlineDungeonSessionPort(linked);

            OnlineDungeonRunner runner = port.CreateDungeonRunner();

            Assert.NotNull(runner);
            FieldInfo keepAlive = typeof(OnlineDungeonRunner).GetField("_keepAlive", Any);
            Assert.NotNull(keepAlive);
            Assert.Null(keepAlive.GetValue(runner));

            Assert.NotSame(runner, port.CreateDungeonRunner()); // 每次调用都是新跑者
        }
    }
}
