using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 真后端接真会话(<see cref="OnlineClientSession"/>)的本地失败路径:不需要任何服务器就能走通的那几条——
/// 本地输入不合规(没发出请求)、网关连不上(本机一个刚关掉的端口,连接被拒)、会话的更替与释放、顺序错误。
/// 不连任何真实服务器:唯一的网络动作是连本机一个没人监听的端口。
/// </summary>
public sealed class OnlineShellRealSessionTests
{
    private static OnlineEntrySettings Template(int gatewayPort)
    {
        Assert.True(GatewayOptions.TryCreate(
            "http://localhost:" + gatewayPort,
            GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey()),
            TimeSpan.FromSeconds(2),
            out GatewayOptions options,
            out _));
        return new OnlineEntrySettings
        {
            Gateway = options,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = 7,
            ChannelPort = 31000,
        };
    }

    /// <summary>取一个刚被关掉的本机端口:连它一定被拒绝。</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task A_real_login_with_an_invalid_username_is_a_local_failure_with_its_own_category()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.LoginAsync("ab", "synthetic-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.InvalidUsername, result.Failure);
        Assert.Equal(0, result.RetryAfterSeconds);
        backend.Dispose();
    }

    [Fact]
    public async Task A_real_login_to_a_closed_port_is_a_network_failure_without_a_countdown()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        Assert.Equal(0, result.RetryAfterSeconds);
        backend.Dispose();
    }

    [Fact]
    public async Task A_real_register_through_the_http_transport_to_a_closed_port_is_a_network_failure()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.RegisterAsync("new_player", "synthetic-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        Assert.False(backend.HasSession);
        backend.Dispose();
    }

    [Fact]
    public async Task A_real_change_password_to_a_closed_port_is_a_network_failure()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(backend.ChangePasswordAsync("player_01", "synthetic-pw", "another-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        backend.Dispose();
    }

    [Fact]
    public async Task The_real_session_stays_reachable_after_a_failed_login_and_is_released_by_disconnect()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession session = backend.Session;
        Assert.NotNull(session);
        Assert.True(backend.HasSession);

        backend.Disconnect();

        Assert.Null(backend.Session);
        Assert.False(backend.HasSession);
        Assert.True(SpinWait.SpinUntil(() => session.State == OnlineClientSessionState.Disposed, 5000));
        backend.Dispose();
    }

    [Fact]
    public async Task Connecting_on_a_session_that_never_logged_in_faults_instead_of_pretending_to_work()
    {
        // 顺序错误是编程错误:真会话抛 InvalidOperationException,外壳把任何意外异常都显示成"意外错误"
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ConnectChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SwitchChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SelectCharacterAsync(0, default));
        backend.Dispose();
    }

    [Fact]
    public async Task Disconnect_never_raises_the_disconnected_event_for_a_real_session()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        int lost = 0;
        backend.Disconnected += () => Interlocked.Increment(ref lost);
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession session = backend.Session;

        backend.Disconnect();
        Assert.True(SpinWait.SpinUntil(() => session.State == OnlineClientSessionState.Disposed, 5000));

        Assert.Equal(0, Volatile.Read(ref lost));
        backend.Dispose();
    }

    [Fact]
    public async Task A_second_real_login_replaces_the_session_and_releases_the_first()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession first = backend.Session;

        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession second = backend.Session;

        Assert.NotSame(first, second);
        Assert.True(SpinWait.SpinUntil(() => first.State == OnlineClientSessionState.Disposed, 5000));
        Assert.NotEqual(OnlineClientSessionState.Disposed, second.State);
        backend.Dispose();
        Assert.Equal(OnlineClientSessionState.Disposed, second.State);
    }

    [Fact]
    public async Task Disposing_the_backend_releases_the_real_session_before_returning()
    {
        var backend = new OnlineShellBackend(Template(ClosedPort()));
        await GatewayTestSupport.Bounded(backend.LoginAsync("player_01", "synthetic-pw", default));
        OnlineClientSession session = backend.Session;

        backend.Dispose();

        Assert.Equal(OnlineClientSessionState.Disposed, session.State);
        Assert.Null(backend.Session);
    }

    // ------------------------------------------------------------------ 真回环套接字:RealOnlineSessionPort 与外壳联合验证

    private static byte[] SyntheticLoginOk(string name = "n900", int accountId = 905)
    {
        byte[] payload = new byte[5 + name.Length + 8 + 4 + 7];
        payload[0] = 1;
        BitConverter.TryWriteBytes(payload.AsSpan(1, 4), name.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(payload, 5);
        BitConverter.TryWriteBytes(payload.AsSpan(5 + name.Length + 8, 4), accountId);
        return payload;
    }

    private static string Md5Lower(string text)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static (LoopbackServer Http, OnlineEntrySettings Settings) StartLoopbackGateway(int channelPort)
    {
        byte[] gwKey = GatewayTestSupport.SynthKey(77);
        var fakeGateway = new FakeGateway(gwKey)
        {
            GameServerIp = IPAddress.Loopback.ToString(),
        };
        fakeGateway.Accounts["player_01"] = new FakeGateway.Account
        {
            Md5 = Md5Lower("synthetic-pw"),
            Qq = string.Empty,
        };
        var http = new LoopbackServer(async (req, stream, ct) =>
        {
            req.Headers.TryGetValue("Content-Type", out string contentType);
            string body = req.Body.Length == 0 ? null : Encoding.UTF8.GetString(req.Body);
            GatewayHttpResponse r = fakeGateway.Handle(
                req.Method,
                "http://" + IPAddress.Loopback + req.Target,
                contentType,
                body);
            string[] extra = r.RetryAfter.HasValue
                ? new[] { "Retry-After: " + (int)Math.Ceiling(r.RetryAfter.Value.TotalSeconds) }
                : null;
            await LoopbackServer.WriteResponseAsync(stream, r.StatusCode, Encoding.UTF8.GetBytes(r.Text ?? string.Empty), extra, ct);
        });
        Assert.True(GatewayOptions.TryCreate(
            http.BaseUrl,
            GatewayTestSupport.HexLower(gwKey),
            TimeSpan.FromSeconds(5),
            out GatewayOptions options,
            out _));
        var settings = new OnlineEntrySettings
        {
            Gateway = options,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = 7,
            ChannelPort = channelPort,
        };
        return (http, settings);
    }

    private sealed class LoopbackChannelServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, LoopbackChannelConn, CancellationToken, Task> _handler;
        private readonly Task _acceptLoop;
        private readonly List<TcpClient> _clients = new();
        private readonly object _lock = new();
        private readonly byte[] _bootstrap;
        private readonly byte[] _keyBlock;
        private int _connIndex;

        public LoopbackChannelServer(Func<int, LoopbackChannelConn, CancellationToken, Task> handler)
        {
            _handler = handler;
            _keyBlock = GatewayTestSupport.Pattern(ChannelInfo.KeyBlockLength, 0x31, 7);
            byte[] channelName = Encoding.UTF8.GetBytes("syn01");
            byte[] plain = new NoticePayloadBuilder()
                .Raw(_keyBlock)
                .I32(channelName.Length).Raw(channelName)
                .I32(0).I32(1)
                .U8(0).U8(0).U8(0)
                .I32(1000).I32(0)
                .I32(100).I32(100)
                .ToArray();
            _bootstrap = DungeonEntryWire.Frame(0, 1, BootstrapTransform.Encode(plain));

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public int AcceptedCount => Volatile.Read(ref _connIndex);

        public void CloseAllClients()
        {
            lock (_lock)
            {
                foreach (TcpClient client in _clients)
                {
                    try { client.Dispose(); } catch { }
                }
                _clients.Clear();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }
                lock (_lock)
                {
                    _clients.Add(client);
                }
                int idx = Interlocked.Increment(ref _connIndex) - 1;
                var conn = new LoopbackChannelConn(client, _bootstrap, _keyBlock);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _handler(idx, conn, _stop.Token);
                    }
                    catch
                    {
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            CloseAllClients();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private sealed class LoopbackChannelConn
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly byte[] _bootstrap;
        private readonly SessionCipher _cipher;

        public LoopbackChannelConn(TcpClient tcp, byte[] bootstrap, byte[] keyBlock)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _bootstrap = bootstrap;
            _cipher = new SessionCipher(keyBlock);
        }

        public Task SendBootstrapAsync(CancellationToken ct) => WriteRawAsync(_bootstrap, ct);

        public Task SendRefusalAsync(CancellationToken ct) => WriteRawAsync(DungeonEntryWire.Refusal(), ct);

        public Task SendEncryptedAsync(byte cls, ushort packetId, byte[] payload, CancellationToken ct)
        {
            byte[] encrypted = _cipher.Encrypt(packetId, payload);
            return WriteRawAsync(DungeonEntryWire.Frame(cls, packetId, encrypted), ct);
        }

        public async Task SendHarmlessPaddingAsync(int count, CancellationToken ct)
        {
            for (int i = 0; i < count; i++)
            {
                await WriteRawAsync(DungeonEntryWire.Frame(0, 900, Array.Empty<byte>()), ct);
            }
        }

        public async Task<ushort?> ReadNextCommandIdAsync(CancellationToken ct)
        {
            byte[] header = new byte[13];
            if (!await ReadExactAsync(header, ct))
            {
                return null;
            }
            uint total = BitConverter.ToUInt32(header, 3);
            if (total > 13)
            {
                byte[] rest = new byte[total - 13];
                if (!await ReadExactAsync(rest, ct))
                {
                    return null;
                }
            }
            return (ushort)(header[1] | (header[2] << 8));
        }

        public void Close()
        {
            try { _tcp.Dispose(); } catch { }
        }

        private async Task WriteRawAsync(byte[] bytes, CancellationToken ct)
        {
            await _stream.WriteAsync(bytes, 0, bytes.Length, ct);
            await _stream.FlushAsync(ct);
        }

        private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = await _stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
                if (n == 0)
                {
                    return false;
                }
                offset += n;
            }
            return true;
        }
    }

    private static OnlineShellFlow StartFlowAndLogin(OnlineShellBackend backend, int channelPort)
    {
        var options = new OnlineShellOptions
        {
            Configured = true,
            DefaultChannelPort = channelPort,
            AutoPassPlaceholders = true,
            TimeoutMs = 20000,
        };
        var flow = new OnlineShellFlow(backend, options);
        flow.Type("player_01");
        flow.Key(OnlineShellKey.Tab);
        flow.Type("synthetic-pw");
        flow.Key(OnlineShellKey.Enter);
        return flow;
    }

    private static void PumpUntil(OnlineShellFlow flow, Func<bool> condition, int timeoutMs = 15000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        long now = 1000;
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("flow did not satisfy condition within " + timeoutMs + "ms (stage=" + flow.Stage + ")");
            }
            flow.Poll(now += 20);
            Thread.Sleep(10);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_real_channel_connect_and_peer_close_keeps_flow_on_channel_select_without_firing_disconnected(bool bootstrapRefusal)
    {
        using var chan = new LoopbackChannelServer(async (_, conn, ct) =>
        {
            if (bootstrapRefusal)
            {
                await conn.SendRefusalAsync(ct);
                conn.Close();
                return;
            }
            await conn.SendBootstrapAsync(ct);
            ushort? cmd = await conn.ReadNextCommandIdAsync(ct);
            if (cmd == 1)
            {
                await conn.SendEncryptedAsync(1, 1, new byte[] { 0, 0x17, 0, 0 }, ct);
            }
            conn.Close();
        });
        (LoopbackServer http, OnlineEntrySettings settings) = StartLoopbackGateway(chan.Port);
        using LoopbackServer _http = http;
        using var backend = new OnlineShellBackend(settings);
        int lost = 0;
        backend.Disconnected += () => Interlocked.Increment(ref lost);

        OnlineShellFlow flow = StartFlowAndLogin(backend, chan.Port);
        PumpUntil(flow, () => flow.Stage == OnlineShellStage.ChannelSelect && !flow.Busy && flow.Notice != null);

        // 等待服务端关闭套接字的效果充分传播,确保不会误报 Disconnected 把外壳踢回 Login
        for (int i = 0; i < 10; i++)
        {
            flow.Poll(5000 + i * 20);
            await Task.Delay(15);
        }

        Assert.Equal(OnlineShellStage.ChannelSelect, flow.Stage);
        Assert.Equal(bootstrapRefusal ? OnlineNoticeKind.Rejected : OnlineNoticeKind.GameLoginRejected, flow.Notice.Kind);
        Assert.True(backend.HasSession);
        Assert.Equal(0, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task CharacterListMissing_with_peer_close_resets_real_session_to_logged_in_so_retry_succeeds()
    {
        using var chan = new LoopbackChannelServer(async (idx, conn, ct) =>
        {
            await conn.SendBootstrapAsync(ct);
            if (await conn.ReadNextCommandIdAsync(ct) != 1)
            {
                conn.Close();
                return;
            }
            await conn.SendEncryptedAsync(1, 1, SyntheticLoginOk(), ct);
            await conn.SendHarmlessPaddingAsync(7, ct); // 凑足 windows.Login 的 8 帧上限,立刻发 CMD 8

            if (await conn.ReadNextCommandIdAsync(ct) != 8)
            {
                conn.Close();
                return;
            }

            if (idx == 0)
            {
                // 第 0 次故意不发角色列表直接关掉连接 -> CharacterListMissing + PeerClosed
                conn.Close();
                return;
            }

            byte[] compressedList = UserInfoTestData.Zlib(UserInfoTestData.Packet(UserInfoTestData.Standard(0)));
            await conn.SendEncryptedAsync(0, 2, compressedList, ct);
            await conn.SendHarmlessPaddingAsync(11, ct); // 凑足 windows.CharacterList 的 12 帧上限
            while (!ct.IsCancellationRequested)
            {
                ushort? next = await conn.ReadNextCommandIdAsync(ct);
                if (!next.HasValue)
                {
                    break;
                }
            }
        });

        (LoopbackServer http, OnlineEntrySettings settings) = StartLoopbackGateway(chan.Port);
        using LoopbackServer _http = http;
        using var backend = new OnlineShellBackend(settings);
        int lost = 0;
        backend.Disconnected += () => Interlocked.Increment(ref lost);

        OnlineShellFlow flow = StartFlowAndLogin(backend, chan.Port);
        PumpUntil(flow, () => flow.Stage == OnlineShellStage.ChannelSelect && !flow.Busy && flow.Notice != null);

        Assert.Equal(OnlineNoticeKind.CharacterListMissing, flow.Notice.Kind);
        Assert.Equal(0, Volatile.Read(ref lost));
        Assert.Equal(OnlineClientSessionState.LoggedIn, backend.Session.State);

        // 回车直接重试 ConnectChannelAsync:不能因为底层残留在 Connected 状态而抛顺序异常
        flow.Key(OnlineShellKey.Enter);
        PumpUntil(flow, () => flow.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(2, chan.AcceptedCount);
        Assert.NotNull(flow.Characters);
        Assert.Single(flow.Characters.Entries);
        Assert.Equal(0, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task Peer_close_after_real_channel_and_character_list_are_established_raises_disconnected_once()
    {
        using var chan = new LoopbackChannelServer(async (_, conn, ct) =>
        {
            await conn.SendBootstrapAsync(ct);
            if (await conn.ReadNextCommandIdAsync(ct) != 1) return;
            await conn.SendEncryptedAsync(1, 1, SyntheticLoginOk(), ct);
            await conn.SendHarmlessPaddingAsync(7, ct);

            if (await conn.ReadNextCommandIdAsync(ct) != 8) return;
            byte[] compressedList = UserInfoTestData.Zlib(UserInfoTestData.Packet(UserInfoTestData.Standard(0)));
            await conn.SendEncryptedAsync(0, 2, compressedList, ct);
            await conn.SendHarmlessPaddingAsync(11, ct);

            if (await conn.ReadNextCommandIdAsync(ct) != 4) return;
            await conn.SendEncryptedAsync(0, 124, new byte[] { 0 }, ct);
            await conn.SendHarmlessPaddingAsync(63, ct); // 凑足 windows.Slot 的 64 帧上限

            while (!ct.IsCancellationRequested)
            {
                ushort? cmd = await conn.ReadNextCommandIdAsync(ct);
                if (!cmd.HasValue) break;
            }
        });

        (LoopbackServer http, OnlineEntrySettings settings) = StartLoopbackGateway(chan.Port);
        using LoopbackServer _http = http;
        using var backend = new OnlineShellBackend(settings);
        int lost = 0;
        backend.Disconnected += () => Interlocked.Increment(ref lost);

        OnlineShellFlow flow = StartFlowAndLogin(backend, chan.Port);
        PumpUntil(flow, () => flow.Stage == OnlineShellStage.CharacterSelect);
        Assert.Equal(0, Volatile.Read(ref lost));

        flow.Key(OnlineShellKey.Enter);
        PumpUntil(flow, () => flow.Stage == OnlineShellStage.InGame, timeoutMs: 20000);

        Assert.Equal(OnlineClientSessionState.CharacterSelected, backend.Session.State);
        chan.CloseAllClients();

        PumpUntil(flow, () => flow.Stage == OnlineShellStage.Login);
        Assert.Equal(OnlineNoticeKind.Disconnected, flow.Notice.Kind);
        Assert.Equal(1, Volatile.Read(ref lost));
    }
}
