using System.Reflection;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.NetFake;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 会话薄皮"通道已建立"标志在创建/删除角色之后的去向(r20fix-createcharui,簇 2 的相邻点补测):
/// 薄皮只在"通道成功建立之后掉线"时向外壳报一次 <c>Lost</c>;创建/删除调用以"取消"或"断线"收场时,
/// 这条通道已经没了,标志必须当场清掉,之后会话上迟到的那一声断线事件不能再被当成新的掉线报给外壳(否则外壳会在已经处理过结局之后又被踢一次)。
/// 真实路径里标志要连接成功才会置位,而连接需要套接字;这里不绑端口、不开线程:
/// 标志用反射直接置位(私有字段,与本程序集其它白盒测试的做法一致),会话上的断线事件用反射读同名私有字段后手动触发,
/// 断线结局则靠"以内存流为底、一写就抛 IO 异常"的合成频道连接造出来。全部是合成数据,不触及任何真实服务,也不会发出任何真实创建或删除。
/// </summary>
public sealed class OnlineShellBackendManageChannelFlagTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly byte[] Name = Encoding.UTF8.GetBytes("Hero909");

    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    /// <summary>把"通道已建立"标志直接置位或清掉(私有字段;真实置位发生在连接成功之后)。</summary>
    private static void SetEstablished(RealOnlineSessionPort port, bool value)
    {
        FieldInfo field = typeof(RealOnlineSessionPort).GetField("_channelEstablished", Any);
        Assert.NotNull(field);
        field.SetValue(port, value);
    }

    /// <summary>手动触发会话上的断线事件(字段式事件,同名私有字段就是订阅列表;薄皮在构造时订阅了它)。</summary>
    private static void RaiseDisconnected(OnlineClientSession session, OnlineDisconnectReason reason)
    {
        FieldInfo field = typeof(OnlineClientSession).GetField("Disconnected", Any);
        Assert.NotNull(field);
        var handler = (Action<OnlineDisconnectReason>)field.GetValue(session);
        Assert.NotNull(handler);
        handler(reason);
    }

    /// <summary>
    /// 把一条"一写就失败"的频道连接塞进会话并把会话置为选角态:游戏会话读到一帧合成启动包后就绪,写则抛 IO 异常,
    /// 创建/删除命令发到这条连接上会得到"断线"结局。链路事先被标成"断线已通知过",联网层就不会再派发它自己的断线事件
    /// (那个事件是后台线程异步派发的,什么时候到、和调用返回谁先谁后都不确定);之后由测试手动补发同样的事件,固定成"事件晚于结果"的顺序。
    /// </summary>
    private static async Task InstallBrokenChannelAsync(OnlineClientSession session)
    {
        byte[] bootstrap = FakeWireCodec.BuildBootstrapFrame(FakeWireCodec.MakeSyntheticKeyBlock());
        var game = new GameClientSession(new WriteFailsStream(bootstrap));
        Assert.True(await GatewayTestSupport.Bounded(game.ReceiveBootstrapAsync(TimeSpan.FromSeconds(30)), 5));
        Assert.True(game.IsReady);

        Type linkType = typeof(OnlineClientSession).Assembly.GetType("Dnf.Net.Session.OnlineChannelLink");
        Assert.NotNull(linkType);
        object link = Activator.CreateInstance(linkType, Any, null, new object[] { game, null, 0u, null }, null);
        Assert.NotNull(link);
        MethodInfo mark = linkType.GetMethod("TryMarkDisconnectNotified", Any);
        Assert.NotNull(mark);
        Assert.True((bool)mark.Invoke(link, null));

        FieldInfo linkField = typeof(OnlineClientSession).GetField("_link", Any);
        Assert.NotNull(linkField);
        linkField.SetValue(session, link);
        FieldInfo stateField = typeof(OnlineClientSession).GetField("<State>k__BackingField", Any);
        Assert.NotNull(stateField);
        stateField.SetValue(session, OnlineClientSessionState.Connected);
        Assert.Equal(OnlineClientSessionState.Connected, session.State);
    }

    // ------------------------------------------------------------------ 对照:事件通路本身

    /// <summary>
    /// 对照(证明下面几个测试的"没有报 Lost"不是因为事件根本没送到薄皮):通道没建立时断线事件什么也不报;
    /// 置位之后同一个事件报恰好一次,并把标志清掉,再来一次不重复报。
    /// </summary>
    [Fact]
    public void A_disconnect_event_reports_lost_once_and_only_while_the_channel_is_established()
    {
        var rig = new BackendRig();
        using var port = new RealOnlineSessionPort(rig.Template);
        int lost = 0;
        port.Lost += () => lost++;

        RaiseDisconnected(port.Real, OnlineDisconnectReason.PeerClosed);
        Assert.Equal(0, lost);

        SetEstablished(port, true);
        RaiseDisconnected(port.Real, OnlineDisconnectReason.ReadError);
        Assert.Equal(1, lost);

        RaiseDisconnected(port.Real, OnlineDisconnectReason.ReadError);
        Assert.Equal(1, lost);

        SetEstablished(port, true);
        RaiseDisconnected(port.Real, OnlineDisconnectReason.HeartbeatTimeout);
        Assert.Equal(2, lost);
    }

    // ------------------------------------------------------------------ 取消收场

    /// <summary>
    /// 创建/删除被取消时(令牌在调用前就已取消,会话在拿门闩时抛取消异常),薄皮把"通道已建立"清掉再把异常原样抛出;
    /// 之后会话上再来一声断线事件,不能报 <c>Lost</c>(这条通道是被取消流程关掉的,外壳已经按取消处理了)。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancelled_manage_call_forgets_the_established_channel(bool create)
    {
        var rig = new BackendRig();
        using var port = new RealOnlineSessionPort(rig.Template);
        int lost = 0;
        port.Lost += () => lost++;
        SetEstablished(port, true);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Bounded(Assert.ThrowsAnyAsync<OperationCanceledException>(() => create
            ? port.CreateCharacterAsync(3, Name, 0, true, cancelled.Token)
            : port.DeleteCharacterAsync(1, Name, true, cancelled.Token)));

        Assert.Equal(0, lost);
        RaiseDisconnected(port.Real, OnlineDisconnectReason.PeerClosed);
        Assert.Equal(0, lost);
    }

    // ------------------------------------------------------------------ 断线收场

    /// <summary>
    /// 创建/删除以"断线"结局收场时(写失败),薄皮同样把"通道已建立"清掉;外壳拿到的是断线结局,
    /// 之后迟到的那一声会话断线事件不能再报 <c>Lost</c>。结局本身要先确认确实是"断线"、且操作种类对得上,
    /// 免得"没报"其实来自别的失败路径。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_disconnected_manage_outcome_forgets_the_established_channel(bool create)
    {
        var rig = new BackendRig();
        using var port = new RealOnlineSessionPort(rig.Template);
        int lost = 0;
        port.Lost += () => lost++;
        await InstallBrokenChannelAsync(port.Real);
        SetEstablished(port, true);

        OnlineCharacterManageReport report = await Bounded(create
            ? port.CreateCharacterAsync(3, Name, 0, true, default)
            : port.DeleteCharacterAsync(1, Name, true, default));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Disconnected, report.Outcome);
        Assert.Equal(create ? OnlineCharacterManageKind.Create : OnlineCharacterManageKind.Delete, report.Operation);
        Assert.Equal(0, lost);
        RaiseDisconnected(port.Real, OnlineDisconnectReason.WriteError);
        Assert.Equal(0, lost);
    }

    /// <summary>读侧给一段预置字节、读完是流结束;写侧一写就抛 IO 异常:合成"写失败"的频道连接。</summary>
    private sealed class WriteFailsStream : Stream
    {
        private readonly MemoryStream _reads;

        public WriteFailsStream(byte[] toRead)
        {
            _reads = new MemoryStream(toRead, false);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _reads.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(_reads.Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("synthetic write failure");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("synthetic write failure");
    }
}
