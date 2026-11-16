using System.Diagnostics;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 内存里的假会话(不连任何网络):联网层的结果类没有公开构造函数,所以假会话只造"报告"。
/// 记下被调用的次数与参数、释放发生在哪个线程,释放可以被一道门卡住(模拟真会话 Dispose 会等在途操作)。
/// </summary>
internal sealed class FakeSessionPort : IOnlineSessionPort
{
    private readonly object _sync = new();
    private Action _lost;
    private int _disposeCount;
    private int _disposeThreadId = -1;

    public FakeSessionPort(OnlineEntrySettings settings)
    {
        Settings = settings;
        OnLogin = (user, password, ct) => Task.FromResult(new OnlineLoginReport(OnlineFailure.None, GatewayOutcome.Success, GatewayErrorCode.None, 0));
        OnConnect = (port, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, FakeRoster(2)));
        OnSwitch = (port, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, FakeRoster(2)));
        OnSelect = (slot, ct) => Task.FromResult(new OnlineSelectReport(true, false, "DNF_NET snapshot slot=" + slot));
    }

    public OnlineEntrySettings Settings { get; }

    public Func<string, string, CancellationToken, Task<OnlineLoginReport>> OnLogin { get; set; }
    public Func<int, CancellationToken, Task<OnlineConnectReport>> OnConnect { get; set; }
    public Func<int, CancellationToken, Task<OnlineConnectReport>> OnSwitch { get; set; }
    public Func<int, CancellationToken, Task<OnlineSelectReport>> OnSelect { get; set; }

    /// <summary>非 null 时 <see cref="Dispose"/> 会卡在它上面,直到被放行。</summary>
    public ManualResetEventSlim DisposeGate { get; set; }

    /// <summary>为 true 时取消订阅不生效:模拟"事件在被摘掉之前就已经在路上"的过期事件。</summary>
    public bool KeepHandlersOnUnsubscribe { get; set; }

    public int LoginCalls;
    public int ConnectCalls;
    public int SwitchCalls;
    public int SelectCalls;
    public string LastUser;
    public string LastPassword;
    public int LastPort;
    public int LastSlot;
    public CancellationToken LastToken;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public int DisposeThreadId => Volatile.Read(ref _disposeThreadId);

    public bool HasLostSubscribers
    {
        get
        {
            lock (_sync)
            {
                return _lost != null;
            }
        }
    }

    public event Action Lost
    {
        add
        {
            lock (_sync)
            {
                _lost += value;
            }
        }
        remove
        {
            lock (_sync)
            {
                if (!KeepHandlersOnUnsubscribe)
                {
                    _lost -= value;
                }
            }
        }
    }

    public OnlineClientSession Real => null;

    public void RaiseLost()
    {
        Action handler;
        lock (_sync)
        {
            handler = _lost;
        }
        handler?.Invoke();
    }

    public Task<OnlineLoginReport> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref LoginCalls);
        LastUser = username;
        LastPassword = password;
        LastToken = cancellationToken;
        return OnLogin(username, password, cancellationToken);
    }

    public Task<OnlineConnectReport> ConnectAsync(int channelPort, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ConnectCalls);
        LastPort = channelPort;
        LastToken = cancellationToken;
        return OnConnect(channelPort, cancellationToken);
    }

    public Task<OnlineConnectReport> SwitchAsync(int channelPort, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SwitchCalls);
        LastPort = channelPort;
        LastToken = cancellationToken;
        return OnSwitch(channelPort, cancellationToken);
    }

    public Task<OnlineSelectReport> SelectAsync(int slot, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SelectCalls);
        LastSlot = slot;
        LastToken = cancellationToken;
        return OnSelect(slot, cancellationToken);
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposeThreadId, Environment.CurrentManagedThreadId);
        DisposeGate?.Wait();
        Interlocked.Increment(ref _disposeCount);
    }

    public bool WaitDisposed(int milliseconds = 5000)
    {
        return SpinWait.SpinUntil(() => DisposeCount > 0, milliseconds);
    }

    /// <summary>合成的角色列表(经真解析器,所以与线上解出来的对象一样)。</summary>
    public static UserInfoNotice FakeRoster(int count)
    {
        var entries = new UserInfoTestData.EntrySpec[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = UserInfoTestData.Standard(i, nameLength: 4 + i, equipCount: i % 3);
        }
        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(entries), out UserInfoNotice notice));
        return notice;
    }
}

/// <summary>包住假网关的传输,记下它有没有被释放。</summary>
internal sealed class TrackingTransport : IGatewayTransport, IDisposable
{
    private readonly IGatewayTransport _inner;
    private int _disposed;

    public TrackingTransport(IGatewayTransport inner)
    {
        _inner = inner;
    }

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public Task<GatewayHttpResponse> GetTextAsync(string url, CancellationToken cancellationToken = default) => _inner.GetTextAsync(url, cancellationToken);

    public Task<GatewayHttpResponse> PostTextAsync(string url, string body, CancellationToken cancellationToken = default) => _inner.PostTextAsync(url, body, cancellationToken);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}

/// <summary>造一个接了假会话与假网关的真后端。全部是合成数据。</summary>
internal sealed class BackendRig
{
    public const string User = "player_01";
    public const string Password = "synthetic-pw";

    private readonly object _sync = new();

    public BackendRig(bool configured = true, Action<FakeSessionPort> onPort = null)
    {
        Key = GatewayTestSupport.SynthKey();
        Assert.True(GatewayOptions.TryCreate("http://localhost", GatewayTestSupport.HexLower(Key), TimeSpan.FromSeconds(2), out GatewayOptions options, out _));
        Options = options;
        Template = configured
            ? new OnlineEntrySettings
            {
                Gateway = options,
                MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                DataVersion = 7,
                ChannelPort = 31000,
            }
            : null;
        Gateway = new FakeGateway(Key);
        Gateway.Accounts[User] = new FakeGateway.Account { Md5 = GatewayCommands.Md5Hex(Password) };
        Backend = new OnlineShellBackend(
            Template,
            line =>
            {
                lock (_sync)
                {
                    Logs.Add(line);
                }
            },
            settings =>
            {
                var port = new FakeSessionPort(settings);
                onPort?.Invoke(port);
                lock (_sync)
                {
                    Ports.Add(port);
                }
                return port;
            },
            gatewayOptions =>
            {
                var transport = new TrackingTransport(Gateway);
                lock (_sync)
                {
                    Transports.Add(transport);
                }
                return transport;
            });
    }

    public byte[] Key { get; }
    public GatewayOptions Options { get; }
    public OnlineEntrySettings Template { get; }
    public FakeGateway Gateway { get; }
    public OnlineShellBackend Backend { get; }
    public List<FakeSessionPort> Ports { get; } = new();
    public List<TrackingTransport> Transports { get; } = new();
    public List<string> Logs { get; } = new();

    public FakeSessionPort[] PortsSnapshot()
    {
        lock (_sync)
        {
            return Ports.ToArray();
        }
    }

    public FakeSessionPort Port(int index)
    {
        lock (_sync)
        {
            return Ports[index];
        }
    }

    public int PortCount
    {
        get
        {
            lock (_sync)
            {
                return Ports.Count;
            }
        }
    }

    public string[] LogsSnapshot()
    {
        lock (_sync)
        {
            return Logs.ToArray();
        }
    }
}

/// <summary>跑主线程轮询的小工具:每一轮 Tick 一次,等后台线程上的任务完成,超时就让测试失败而不是挂住。</summary>
internal static class HostPump
{
    public static void Until(OnlineShellHost host, ref long now, Func<bool> done, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            host.Tick(default, 16, now += 16);
            if (watch.Elapsed.TotalSeconds > seconds)
            {
                Assert.Fail(
                    "the condition was not reached within " + seconds + " seconds (stage " + host.Stage
                    + ", busy " + host.Flow.Busy + ", notice " + (host.Flow.Notice == null ? "none" : host.Flow.Notice.Kind.ToString()) + ")");
            }
            Thread.Sleep(1);
        }
    }
}
