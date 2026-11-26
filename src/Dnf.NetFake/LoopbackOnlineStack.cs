using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;

namespace Dnf.NetFake;

/// <summary>
/// 组合本地回环三件套: HTTP 假网关(真 AES-256-GCM) + 频道目录 TCP 假服(真 AES-128-ECB/zlib) + 游戏频道 TCP 假服(真 14 槽 SessionCipher / 561 信封)。
/// 全部使用 0 端口分配与合成凭据,不依赖任何真实配置或外部网络。
/// </summary>
public sealed class LoopbackOnlineStack : IDisposable
{
    public const string DefaultAccount = "player_01";
    public const string DefaultPassword = "synthetic-pw";
    public const string DefaultMouseCipherHex = "0102030405060708";
    public const uint DefaultDataVersion = 7;

    private readonly byte[] _gatewayKey;
    private readonly FakeGateway _gateway;
    private readonly LoopbackServer _http;
    private readonly FakeGameChannelServer _channel;
    private readonly FakeChannelDirectoryServer _directory;

    public LoopbackOnlineStack(FakeChannelScenario scenario = null, string account = DefaultAccount, string password = DefaultPassword)
    {
        Scenario = scenario ?? new FakeChannelScenario();
        Account = account;
        Password = password;
        _gatewayKey = FakeWireCodec.MakeSyntheticAesKey(77);
        _gateway = new FakeGateway(_gatewayKey)
        {
            GameServerIp = IPAddress.Loopback.ToString(),
        };
        if (Scenario.Fault != FakeFaultMode.AuthFail)
        {
            _gateway.Accounts[Account] = new FakeGateway.Account
            {
                Md5 = Md5Lower(Password),
                Qq = string.Empty,
            };
        }
        _http = new LoopbackServer(async (req, stream, ct) =>
        {
            req.Headers.TryGetValue("Content-Type", out string contentType);
            string body = req.Body.Length == 0 ? null : Encoding.UTF8.GetString(req.Body);
            GatewayHttpResponse r = _gateway.Handle(
                req.Method,
                "http://" + IPAddress.Loopback + req.Target,
                contentType,
                body);
            string[] extra = r.RetryAfter.HasValue
                ? new[] { "Retry-After: " + (int)Math.Ceiling(r.RetryAfter.Value.TotalSeconds) }
                : null;
            await LoopbackServer.WriteResponseAsync(stream, r.StatusCode, Encoding.UTF8.GetBytes(r.Text ?? string.Empty), extra, ct).ConfigureAwait(false);
        });
        _channel = new FakeGameChannelServer(Scenario);
        _directory = new FakeChannelDirectoryServer(_channel.Port, "siroco", Scenario.ChannelName);
    }

    public FakeChannelScenario Scenario { get; }
    public string Account { get; }
    public string Password { get; }
    public int GatewayPort => _http.Port;
    public string GatewayUrl => _http.BaseUrl;
    public string GatewayAesKeyHex => FakeWireCodec.HexLower(_gatewayKey);
    public int DirectoryPort => _directory.Port;
    public int ChannelPort => _channel.Port;
    public int DirectoryRequestsServed => _directory.RequestsServed;
    public int ChannelAcceptedConnections => _channel.AcceptedConnections;
    public int ChannelActiveConnections => _channel.ActiveConnections;
    public IReadOnlyList<FakeReceivedCommand> ReceivedCommands => _channel.ReceivedCommands;

    public OnlineEntrySettings CreateEntrySettings()
    {
        if (!GatewayOptions.TryCreate(GatewayUrl, GatewayAesKeyHex, TimeSpan.FromSeconds(5), out GatewayOptions gw, out string err))
        {
            throw new InvalidOperationException("无法构造回环网关选项: " + err);
        }
        return new OnlineEntrySettings
        {
            Gateway = gw,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = DefaultDataVersion,
            ChannelPort = ChannelPort,
            ChannelDirectoryPort = DirectoryPort,
        };
    }

    /// <summary>在临时目录下写出供开发版播放器 -dnfnetroot 使用的合成配置(仅指向 127.0.0.1 与合成凭据)。</summary>
    public void WriteSyntheticDevRoot(string devRootDir)
    {
        Directory.CreateDirectory(devRootDir);
        string fakeDnfDir = Path.Combine(devRootDir, "fake_dnf");
        Directory.CreateDirectory(fakeDnfDir);

        File.WriteAllText(Path.Combine(devRootDir, "local.env"),
            "DNF_DIR=\"" + fakeDnfDir + "\"\n");
        File.WriteAllText(Path.Combine(fakeDnfDir, "Config.toml"),
            "server_url = \"" + GatewayUrl + "\"\n" +
            "aes_key = \"" + GatewayAesKeyHex + "\"\n");
        string secretsPath = Path.Combine(devRootDir, "local.secrets.env");
        File.WriteAllText(secretsPath,
            "DNF_ACCOUNT=\"" + Account + "\"\n" +
            "DNF_PASSWORD=\"" + Password + "\"\n" +
            "DNF_MOUSE_CIPHER_HEX=\"" + DefaultMouseCipherHex + "\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secretsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void CloseAllChannelClients()
    {
        _channel.CloseAllClients();
    }

    private static string Md5Lower(string text)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public void Dispose()
    {
        _directory.Dispose();
        _channel.Dispose();
        _http.Dispose();
    }
}
