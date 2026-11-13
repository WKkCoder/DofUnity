using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>分阶段会话测试的内存环境：假网关、确定性会话密码与记录原始出站帧的假频道。</summary>
internal sealed class OnlineFlowRig
{
    internal const string Account = "test900";
    internal const string Password = "pw900";

    internal OnlineFlowRig()
    {
        byte[] key = GatewayTestSupport.SynthKey(93);
        FakeGateway = new FakeGateway(key);
        FakeGateway.Accounts[Account] = new FakeGateway.Account
        {
            Md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Password))).ToLowerInvariant(),
            Qq = string.Empty,
        };
        Settings = HostTestKit.Settings();
        Settings.Account = Account;
        Settings.Password = Password;
        Settings.Gateway = new GatewayOptions("http://" + GatewayTestSupport.Dots(127, 0, 0, 1) + ":18080",
            GatewayTestSupport.HexLower(key));
        Hooks = new OnlineClientSessionHooks
        {
            GatewayFactory = options => new GatewayClient(options, FakeGateway),
            Windows = new OnlineTownWindows(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(30)),
            AutoHeartbeat = false,
            InlineCallbacks = true,
            HeartbeatPoll = TimeSpan.FromMilliseconds(20),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
        };
        Hooks.ChannelFactory = CreateChannel;
    }

    internal OnlineEntrySettings Settings { get; }
    internal FakeGateway FakeGateway { get; }
    internal OnlineClientSessionHooks Hooks { get; }
    internal List<OnlineFlowChannel> Channels { get; } = new();
    internal List<int> RequestedPorts { get; } = new();
    internal Func<int, byte[]> BootstrapFactory { get; set; } = _ => DungeonEntryWire.BootstrapFrame();
    internal Func<int, bool> CloseBeforeBootstrap { get; set; } = _ => false;
    internal Func<int, bool> FailChannel { get; set; } = _ => false;
    internal Func<int, Func<DungeonEntryClientFrame, IEnumerable<byte[]>>> ScriptFactory { get; set; }

    /// <summary>
    /// 新建会话。给了 <paramref name="dispatcher"/> 就把事件(与进度)交给它派发并关掉"内联回调";
    /// 传一个同步派发器,断线事件就在触发它的那个线程上同步执行,测试可以不靠睡眠直接数事件。
    /// </summary>
    internal OnlineClientSession CreateSession(Action<OnlineStage> onStage = null, bool autoHeartbeat = false,
        IOnlineDispatcher dispatcher = null)
    {
        Hooks.AutoHeartbeat = autoHeartbeat;
        if (dispatcher != null)
        {
            Hooks.InlineCallbacks = false;
        }
        return new OnlineClientSession(Settings, onStage, dispatcher, Hooks);
    }

    internal async Task<OnlineClientSession> LoggedIn(Action<OnlineStage> onStage = null, bool autoHeartbeat = false,
        IOnlineDispatcher dispatcher = null)
    {
        OnlineClientSession client = CreateSession(onStage, autoHeartbeat, dispatcher);
        OnlineLoginResult login = await client.LoginAsync(Account, Password);
        Assert.True(login.IsSuccess, login.Failure.ToString());
        return client;
    }

    internal async Task<(OnlineClientSession Client, OnlineConnectResult Connected)> Connected(
        Action<OnlineStage> onStage = null, bool autoHeartbeat = false, IOnlineDispatcher dispatcher = null)
    {
        OnlineClientSession client = await LoggedIn(onStage, autoHeartbeat, dispatcher);
        OnlineConnectResult connected = await GatewayTestSupport.Bounded(client.ConnectAsync(9010), 5);
        Assert.True(connected.IsSuccess, connected.Failure.ToString());
        return (client, connected);
    }

    internal async Task<(OnlineClientSession Client, OnlineSelectCharacterResult Selected)> Selected(
        int slot = 0, bool autoHeartbeat = false, IOnlineDispatcher dispatcher = null)
    {
        (OnlineClientSession client, _) = await Connected(autoHeartbeat: autoHeartbeat, dispatcher: dispatcher);
        OnlineSelectCharacterResult selected = await GatewayTestSupport.Bounded(client.SelectCharacterAsync(slot), 5);
        Assert.True(selected.Entered, selected.Failure.ToString());
        return (client, selected);
    }

    internal OnlineTownEntry CreateTownEntry()
    {
        return new OnlineTownEntry(Settings, Hooks);
    }

    private Task<OnlineChannelLink> CreateChannel(IPAddress address, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int index = Channels.Count;
        RequestedPorts.Add(port);
        if (FailChannel(index))
        {
            return Task.FromResult<OnlineChannelLink>(null);
        }
        var pipe = new DungeonEntryPipe();
        pipe.Script = ScriptFactory?.Invoke(index) ?? DefaultScript(index);
        var recording = new OnlineFlowRecordingStream(pipe);
        var observer = new OnlineObservedStream(recording);
        var session = new GameClientSession(observer, slot => new SliceXorCipher(slot), () => 0x5a);
        var connection = new OnlineFlowConnection();
        var link = new OnlineChannelLink(session, connection, 0, observer);
        var channel = new OnlineFlowChannel(pipe, recording, link, connection);
        Channels.Add(channel);
        byte[] bootstrap = BootstrapFactory(index);
        if (bootstrap != null)
        {
            pipe.Push(bootstrap);
        }
        if (CloseBeforeBootstrap(index))
        {
            pipe.CloseServer();
        }
        return Task.FromResult(link);
    }

    private Func<DungeonEntryClientFrame, IEnumerable<byte[]>> DefaultScript(int channel)
    {
        return HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot => HostTestKit.EnterComplete());
    }
}

internal sealed class OnlineFlowChannel
{
    internal OnlineFlowChannel(DungeonEntryPipe pipe, OnlineFlowRecordingStream recording,
        OnlineChannelLink link, OnlineFlowConnection connection)
    {
        Pipe = pipe;
        Recording = recording;
        Link = link;
        Connection = connection;
    }

    internal DungeonEntryPipe Pipe { get; }
    internal OnlineFlowRecordingStream Recording { get; }
    internal OnlineChannelLink Link { get; }
    internal OnlineFlowConnection Connection { get; }
}

/// <summary>
/// 假链路的"底层连接":数着被释放几次(链路被关的标志),并可挂一个释放时的回调——
/// 回调在 <see cref="OnlineClientSession"/> 持门闩释放链路的那一刻、由释放它的线程同步执行,测试借此在释放窗口内做事。
/// </summary>
internal sealed class OnlineFlowConnection : IDisposable
{
    private int _disposeCount;

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>每次释放时调用(在计数之后);null 为不做事。</summary>
    internal Action OnDispose { get; set; }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        OnDispose?.Invoke();
    }
}

/// <summary>线程安全的断线事件记录:事件可能在后台派发线程或心跳线程上到达。</summary>
internal sealed class OnlineDisconnectLog
{
    private readonly object _gate = new();
    private readonly List<OnlineDisconnectReason> _reasons = new();

    internal void Add(OnlineDisconnectReason reason)
    {
        lock (_gate) _reasons.Add(reason);
    }

    internal OnlineDisconnectReason[] Reasons
    {
        get { lock (_gate) return _reasons.ToArray(); }
    }

    internal int Count => Reasons.Length;
}

/// <summary>记录写出线上的原始字节，同时把 I/O 交给脚本化管道。</summary>
internal sealed class OnlineFlowRecordingStream : Stream
{
    private readonly Stream _inner;
    private readonly List<byte[]> _writes = new();

    internal OnlineFlowRecordingStream(Stream inner)
    {
        _inner = inner;
    }

    internal IReadOnlyList<byte[]> Writes
    {
        get { lock (_writes) return _writes.ToArray(); }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        byte[] copy = buffer.Skip(offset).Take(count).ToArray();
        lock (_writes) _writes.Add(copy);
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }
}

internal sealed class OnlineInlineDispatcher : IOnlineDispatcher
{
    public int Calls { get; private set; }
    public void Dispatch(Action callback)
    {
        Calls++;
        callback();
    }
}
