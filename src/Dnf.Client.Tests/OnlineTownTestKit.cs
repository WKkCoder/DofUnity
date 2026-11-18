using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网层几个结果类型的合成工厂:它们的构造函数在联网层都是 internal(客户端测试程序集没有开放权限),
/// 这里用反射造出来,只用来喂合成数据;字段值全是测试里手写的字面量。
/// </summary>
internal static class NetObjects
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T Make<T>(params object[] args)
    {
        object made = Activator.CreateInstance(typeof(T), Any, null, args, null);
        Assert.NotNull(made);
        return (T)made;
    }

    /// <summary>联网层收到的非心跳帧(只读快照);测试里只用来验证帧事件处理器的转发。</summary>
    public static OnlineReceivedFrame Frame(PacketClass packetClass, int packetId, byte[] payload)
    {
        return Make<OnlineReceivedFrame>(packetClass, packetId, payload, false);
    }

    /// <summary>进城快照(槽号、等级、职业、成长类型与诊断计数)。</summary>
    public static TownEntrySnapshot Snapshot(int? slot, int? level, int? job, int? growType,
        bool enterComplete = false, int framesSeen = 0, int framesParsed = 0, int framesMalformed = 0)
    {
        return Make<TownEntrySnapshot>(slot, enterComplete, level, job, growType, (int?)null, (int?)null, framesSeen, framesParsed, framesMalformed);
    }

    /// <summary>副本许可快照:每个条目是 (副本号, 档位)。</summary>
    public static DungeonPermissionSnapshot Permissions(params (ushort Dungeon, byte Tier)[] entries)
    {
        Array array = Array.CreateInstance(typeof(DungeonPermissionEntry), entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            array.SetValue(Make<DungeonPermissionEntry>(entries[i].Dungeon, entries[i].Tier), i);
        }
        return Make<DungeonPermissionSnapshot>(array, entries.Length > 0 ? 1 : 0, 0);
    }

    /// <summary>联网会话的一次选角结果(成功或失败);<paramref name="slot"/> 是请求的槽号。</summary>
    public static OnlineSelectCharacterResult SelectResult(bool entered, bool mayTryAnotherSlot, int slot,
        SelectCharacterResult selectCharacter, TownEntrySnapshot snapshot, DungeonPermissionSnapshot permissions)
    {
        OnlineEntryResult entry = Make<OnlineEntryResult>(
            entered ? OnlineStage.Done : OnlineStage.SelectSlot,
            entered ? OnlineFailure.None : OnlineFailure.NoUsableSlot,
            (byte?)null,
            entered ? slot : -1,
            null,
            null,
            0,
            0,
            snapshot,
            permissions,
            selectCharacter);
        return Make<OnlineSelectCharacterResult>(slot, mayTryAnotherSlot, entry);
    }
}

/// <summary>未连接的真会话,以及数它上面两个事件的订阅数(联网层的两个事件都是字段式事件,同名私有字段就是订阅列表)。</summary>
internal static class SessionProbe
{
    /// <summary>造一个什么都没连的真会话(构造不做任何网络动作)。</summary>
    public static OnlineClientSession NewUnconnected()
    {
        Assert.True(GatewayOptions.TryCreate("http://127.0.0.1:18080", GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey()),
            TimeSpan.FromSeconds(1), out GatewayOptions options, out _));
        return new OnlineClientSession(new OnlineEntrySettings
        {
            Gateway = options,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = 7,
            ChannelPort = 31000,
        });
    }

    /// <summary>某个事件当前的订阅者(按订阅顺序);没有订阅者返回空数组。</summary>
    public static Delegate[] Handlers(OnlineClientSession session, string eventName)
    {
        FieldInfo field = typeof(OnlineClientSession).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var list = (Delegate)field.GetValue(session);
        return list == null ? Array.Empty<Delegate>() : list.GetInvocationList();
    }

    public static int FrameReceivedCount(OnlineClientSession session) => Handlers(session, "FrameReceived").Length;

    public static int DisconnectedCount(OnlineClientSession session) => Handlers(session, "Disconnected").Length;
}

/// <summary>有限时间的等待小工具(失败用断言,不挂住)。</summary>
internal static class Waits
{
    public static async Task Bounded(Task task, int seconds = 30)
    {
        using var stop = new CancellationTokenSource();
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token));
        if (finished != task)
        {
            Assert.Fail("the operation did not finish within " + seconds + " seconds");
        }
        stop.Cancel();
        await task;
    }
}

/// <summary>
/// 包着 <see cref="FakeSessionPort"/> 的会话皮,但 <see cref="Real"/> 可以指向一个真的(未连接的)<see cref="OnlineClientSession"/>,
/// 用来模拟"宿主手里有真会话"(<c>OnlineShellHost.Session</c> 非 null);<see cref="ThrowOnLostRemove"/> 为 true 时摘掉断线订阅会抛异常,
/// 用来让后端的优雅断线失败(那一步在后端自己的 try 之外)。
/// </summary>
internal sealed class TownPort : IOnlineSessionHostPort
{
    public TownPort(FakeSessionPort inner, OnlineClientSession real)
    {
        Inner = inner;
        Real = real;
    }

    public FakeSessionPort Inner { get; }

    public OnlineClientSession Real { get; }

    public bool ThrowOnLostRemove { get; set; }

    public event Action Lost
    {
        add => Inner.Lost += value;
        remove
        {
            if (ThrowOnLostRemove)
            {
                throw new InvalidOperationException("synthetic failure while detaching the lost handler");
            }
            Inner.Lost -= value;
        }
    }

    public Task<OnlineLoginReport> LoginAsync(string username, string password, CancellationToken cancellationToken) => Inner.LoginAsync(username, password, cancellationToken);

    public Task<OnlineConnectReport> ConnectAsync(int channelPort, CancellationToken cancellationToken) => Inner.ConnectAsync(channelPort, cancellationToken);

    public Task<OnlineConnectReport> SwitchAsync(int channelPort, CancellationToken cancellationToken) => Inner.SwitchAsync(channelPort, cancellationToken);

    public Task<OnlineConnectReport> ConnectAsync(string host, int channelPort, CancellationToken cancellationToken) => Inner.ConnectAsync(host, channelPort, cancellationToken);

    public Task<OnlineConnectReport> SwitchAsync(string host, int channelPort, CancellationToken cancellationToken) => Inner.SwitchAsync(host, channelPort, cancellationToken);

    public Task<OnlineDirectoryReport> LoadDirectoryAsync(CancellationToken cancellationToken) => Inner.LoadDirectoryAsync(cancellationToken);

    public Task<OnlineSelectReport> SelectAsync(int slot, CancellationToken cancellationToken) => Inner.SelectAsync(slot, cancellationToken);

    public void Dispose()
    {
        // 先放真会话、后放里面那个假皮:假皮的释放计数一变,真会话就一定已经放掉了(测试据此等待,不用睡眠轮询)
        Real?.Dispose();
        Inner.Dispose();
    }
}

/// <summary>
/// 接了 <see cref="TownPort"/> 的真后端、真宿主(外壳阶段机)与日志收集:联网城镇控制器与宿主退出路径的测试台。
/// 角色列表是合成的两个角色(槽 0:等级 55、职业 0、成长类型字节 0x12;槽 1:等级 32、职业 2、成长类型字节 0x01),
/// 选角回报由 <c>select</c> 回调给(默认:槽号无关、村 1、唯一号 1203、无快照无许可)。
/// <see cref="TownLines"/> 是传给 <c>OnlineTownController.Create</c> 的日志去处,<see cref="HostLines"/> 是外壳自己的日志。
/// </summary>
internal sealed class TownHostRig : IDisposable
{
    private readonly object _sync = new();
    private readonly List<string> _townLines = new();
    private readonly List<string> _hostLines = new();
    private readonly List<TownPort> _ports = new();

    public TownHostRig(Func<int, OnlineSelectReport> select = null, bool realSession = false)
    {
        var kit = new BackendRig(configured: true);
        Backend = new OnlineShellBackend(
            kit.Template,
            null,
            settings =>
            {
                var inner = new FakeSessionPort(settings);
                Func<Task<OnlineConnectReport>> roster = () => Task.FromResult(
                    new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, OnlineTownHostTests.CustomRoster()));
                inner.OnConnect = (channelPort, ct) => roster();
                inner.OnSwitch = (channelPort, ct) => roster();
                inner.OnConnectHost = (channelHost, channelPort, ct) => roster();
                inner.OnSwitchHost = (channelHost, channelPort, ct) => roster();
                inner.OnSelect = (slot, ct) => Task.FromResult(
                    select != null ? select(slot) : new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 1), null, null));
                var townPort = new TownPort(inner, realSession ? SessionProbe.NewUnconnected() : null);
                lock (_sync)
                {
                    _ports.Add(townPort);
                }
                return townPort;
            },
            gatewayOptions => new TrackingTransport(kit.Gateway));
        var setup = new OnlineShellSetup(kit.Template, OnlineShellConfigSource.ConfigFile, null, 31000, BackendRig.User, BackendRig.Password);
        Host = new OnlineShellHost(setup, Backend, null, line =>
        {
            lock (_sync)
            {
                _hostLines.Add(line);
            }
        }, null);
    }

    public OnlineShellBackend Backend { get; }

    public OnlineShellHost Host { get; }

    public long Now = 1000;

    /// <summary>外壳此刻为止建过的会话皮(按登录顺序)。</summary>
    public TownPort Port(int index)
    {
        lock (_sync)
        {
            return _ports[index];
        }
    }

    /// <summary>传给控制器的日志去处。</summary>
    public void Log(string line)
    {
        lock (_sync)
        {
            _townLines.Add(line);
        }
    }

    public string[] TownLines()
    {
        lock (_sync)
        {
            return _townLines.ToArray();
        }
    }

    public string[] HostLines()
    {
        lock (_sync)
        {
            return _hostLines.ToArray();
        }
    }

    public void Tick(OnlineShellFrameInput input = default) => Host.Tick(input, 16, Now += 16);

    public void Until(Func<bool> done) => HostPump.Until(Host, ref Now, done);

    /// <summary>从登录界面一路走到进游戏(选 <paramref name="slot"/> 号槽)。</summary>
    public void EnterInGame(int slot = 0)
    {
        if (Host.Stage == OnlineShellStage.Login)
        {
            Tick(new OnlineShellFrameInput { Enter = true });
        }
        Until(() => Host.Stage == OnlineShellStage.CharacterSelect);
        for (int i = 0; i < slot; i++)
        {
            Tick(new OnlineShellFrameInput { Right = true });
        }
        Tick(new OnlineShellFrameInput { Enter = true });
        Until(() => Host.InGame);
    }

    public void Dispose() => Host.Dispose();
}
