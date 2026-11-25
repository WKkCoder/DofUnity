using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 客户端版本号在联网外壳里的整条路(台账 E-453):本机配置(开发版的 local.env、正式分发的配置文件)→ 启动设置的会话入参模板 →
/// 后端每次登录新建会话时复制的模板 → 会话入参。以前两处复制各抄一张固定的字段表,E-441 加版本号时都漏了,版本号在外壳里永远是 0。
/// 文件全是临时目录里的合成内容,从不碰真实的 local.env、机密文件或版本取值。
/// </summary>
public sealed class OnlineShellClientVersionTests : IDisposable
{
    private const string Mouse = "0102030405060708";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dnf-shell-client-version-" + Guid.NewGuid().ToString("N"));

    public OnlineShellClientVersionTests()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(RepoDir);
        Directory.CreateDirectory(GameDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    private string DataDir => Path.Combine(_root, "data");

    private string RepoDir => Path.Combine(_root, "repo");

    private string GameDir => Path.Combine(_root, "game");

    private static string KeyHex => GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey());

    private void WriteConfig(params string[] extra)
    {
        var lines = new List<string> { "server_url = http://localhost", "aes_key = " + KeyHex, "mouse_cipher = " + Mouse };
        lines.AddRange(extra);
        File.WriteAllLines(OnlineConnectionConfig.PathUnder(DataDir), lines);
    }

    private void WriteDev(params string[] extraLocalEnvLines)
    {
        File.WriteAllLines(Path.Combine(RepoDir, "local.env"), new[] { "DNF_DIR=" + GameDir }.Concat(extraLocalEnvLines));
        File.WriteAllLines(Path.Combine(GameDir, "Config.toml"), new[] { "server_url = \"http://localhost\"", "aes_key = \"" + KeyHex + "\"" });
        File.WriteAllLines(Path.Combine(RepoDir, "local.secrets.env"),
            new[] { "DNF_ACCOUNT=dev_account_1", "DNF_PASSWORD=dev-pass-synthetic", "DNF_MOUSE_CIPHER_HEX=" + Mouse });
    }

    // ---- 启动设置:配置 → 会话入参模板 ----

    [Fact]
    public void The_config_file_client_version_reaches_the_session_template_and_defaults_to_zero()
    {
        WriteConfig("client_version = 424242");
        OnlineShellSetup configured = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);
        Assert.True(configured.Configured, configured.ConfigDetail);
        Assert.Equal(424242u, configured.CreateSettings().ClientVersion);

        WriteConfig();
        OnlineShellSetup defaulted = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);
        Assert.True(defaulted.Configured, defaulted.ConfigDetail);
        Assert.Equal(0u, defaulted.CreateSettings().ClientVersion);
    }

    [Fact]
    public void The_dev_local_env_client_version_reaches_the_session_template_and_defaults_to_zero()
    {
        WriteDev("DNF_CLIENT_VERSION=424242");
        OnlineShellSetup configured = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);
        Assert.True(configured.Configured, configured.ConfigDetail);
        Assert.Equal(OnlineShellConfigSource.LocalDev, configured.Source);
        Assert.Equal(424242u, configured.CreateSettings().ClientVersion);

        WriteDev();
        OnlineShellSetup defaulted = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);
        Assert.True(defaulted.Configured, defaulted.ConfigDetail);
        Assert.Equal(0u, defaulted.CreateSettings().ClientVersion);
    }

    [Fact]
    public void A_bad_client_version_leaves_the_setup_unconfigured_and_names_only_the_item()
    {
        const string secretValue = "ver-secret-7";

        WriteConfig("client_version = " + secretValue);
        OnlineShellSetup config = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);
        Assert.False(config.Configured);
        Assert.Equal(OnlineConnectionConfig.Describe(OnlineConnectionProblem.ClientVersionInvalid), config.ConfigDetail);
        Assert.DoesNotContain(secretValue, config.ConfigDetail);
        Assert.Null(config.CreateSettings());

        WriteDev("DNF_CLIENT_VERSION=" + secretValue);
        OnlineShellSetup dev = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);
        Assert.False(dev.Configured);
        Assert.Contains("DNF_CLIENT_VERSION", dev.ConfigDetail);
        Assert.DoesNotContain(secretValue, dev.ConfigDetail);
        Assert.Null(dev.CreateSettings());
    }

    [Fact]
    public void Every_call_to_create_settings_returns_a_fresh_copy_that_carries_the_version_and_never_the_credentials()
    {
        WriteDev("DNF_CLIENT_VERSION=424242");
        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);

        OnlineEntrySettings first = setup.CreateSettings();
        first.ClientVersion = 1;
        first.Account = "changed";
        OnlineEntrySettings second = setup.CreateSettings();

        Assert.NotSame(first, second);
        Assert.Equal(424242u, second.ClientVersion);
        Assert.Equal(string.Empty, second.Account);
        Assert.Equal(string.Empty, second.Password);
    }

    // ---- 后端:模板 → 每次登录新建会话的入参 ----

    [Fact]
    public async Task The_backend_copies_the_template_client_version_into_every_session_it_builds()
    {
        var rig = new BackendRig(clientVersion: 42435);

        OnlineAuthOutcome first = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(first.Success);
        OnlineAuthOutcome second = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(second.Success);

        FakeSessionPort[] ports = rig.PortsSnapshot();
        Assert.Equal(2, ports.Length);
        foreach (FakeSessionPort port in ports)
        {
            Assert.Equal(42435u, port.Settings.ClientVersion);
            Assert.Equal(7u, port.Settings.DataVersion);                // 其余设置项照旧
            Assert.Equal(31000, port.Settings.ChannelPort);
        }
        Assert.Equal(42435u, rig.Template.ClientVersion);                // 模板没被动
    }

    [Fact]
    public async Task Changing_the_version_on_a_session_does_not_leak_back_into_the_template_or_other_sessions()
    {
        var rig = new BackendRig(clientVersion: 42435);
        Assert.True((await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default))).Success);
        FakeSessionPort first = Assert.Single(rig.PortsSnapshot());

        first.Settings.ClientVersion = 9;

        Assert.Equal(42435u, rig.Template.ClientVersion);
        Assert.True((await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default))).Success);
        Assert.Equal(42435u, rig.PortsSnapshot().Last().Settings.ClientVersion);
    }

    [Fact]
    public async Task Without_a_version_in_the_template_the_sessions_get_zero()
    {
        var rig = new BackendRig();
        Assert.True((await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default))).Success);
        Assert.Equal(0u, Assert.Single(rig.PortsSnapshot()).Settings.ClientVersion);
    }
}
