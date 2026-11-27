using System.Reflection;
using Dnf.Net.Session;
using Dnf.Net.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// r20fix-e2e C-3:频道目录端口(<c>-dnfdirport</c>)在两层的边界——
/// ① <see cref="OnlineShellSetupLoader.Load(bool, string, int, string, string, string, int)"/> 把它写进设置模板(开发构建才生效,合法范围 1 到 65535,其余维持默认);
/// ② 会话薄皮 <c>RealOnlineSessionPort</c> 的构造按设置里的目录端口定下实际要连的端口(同一个合法范围,越界回落默认)。
/// 每层各一张表:int.MinValue、-1、0、1、2、18999、65534、65535、65536、65537、int.MaxValue 的接受或回落结果;期望值全是手写字面量,
/// 回落值就是默认目录端口 7001(真服运行依赖它,逐字钉死)。开发版与非开发版各一份(非开发版任何值都不得生效)。
/// 所有文件都是临时目录里的合成内容,从不碰真实的 local.env 或机密文件;不连任何网络。
/// </summary>
public sealed class OnlineDirectoryPortBoundaryGapTests : IDisposable
{
    private const string DevAccount = "dev_account_1";
    private const string DevPassword = "dev-pass-synthetic";
    private const string Mouse = "0102030405060708";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dnf-dirport-gap-" + Guid.NewGuid().ToString("N"));

    public OnlineDirectoryPortBoundaryGapTests()
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

    /// <summary>开发版读的合成本机凭据:仓库根里的 local.env 指向"游戏目录",游戏目录里放网关配置,账号口令放在 local.secrets.env。</summary>
    private void WriteDevRoot()
    {
        File.WriteAllLines(Path.Combine(RepoDir, "local.env"), new[] { "DNF_DIR=" + GameDir });
        File.WriteAllLines(
            Path.Combine(GameDir, "Config.toml"),
            new[] { "server_url = \"http://localhost\"", "aes_key = \"" + KeyHex + "\"" });
        File.WriteAllLines(
            Path.Combine(RepoDir, "local.secrets.env"),
            new[] { "DNF_ACCOUNT=" + DevAccount, "DNF_PASSWORD=" + DevPassword, "DNF_MOUSE_CIPHER_HEX=" + Mouse });
    }

    /// <summary>正式分发读的合成配置文件。</summary>
    private void WriteValidConfig()
    {
        File.WriteAllLines(
            OnlineConnectionConfig.PathUnder(DataDir),
            new[] { "server_url = http://localhost", "aes_key = " + KeyHex, "mouse_cipher = " + Mouse });
    }

    /// <summary>(传进去的目录端口, 期望生效的端口):合法范围 1 到 65535 原样接受,其余回落 7001。手写字面量,不调产品代码算期望。</summary>
    public static TheoryData<int, int> PortCases => new TheoryData<int, int>
    {
        { int.MinValue, 7001 },
        { -1, 7001 },
        { 0, 7001 },
        { 1, 1 },
        { 2, 2 },
        { 18999, 18999 },
        { 65534, 65534 },
        { 65535, 65535 },
        { 65536, 7001 },
        { 65537, 7001 },
        { int.MaxValue, 7001 },
    };

    private static int EffectiveDirectoryPort(RealOnlineSessionPort port)
    {
        FieldInfo field = typeof(RealOnlineSessionPort).GetField("_directoryPort", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field.GetValue(port);
    }

    // ------------------------------------------------------------------ 层 ①:OnlineShellSetupLoader.Load

    [Theory]
    [MemberData(nameof(PortCases))]
    public void Dev_build_applies_the_directory_port_only_inside_1_to_65535(int given, int expected)
    {
        WriteDevRoot();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null, given);

        Assert.True(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.LocalDev, setup.Source);
        Assert.Equal(expected, setup.CreateSettings().ChannelDirectoryPort);
        Assert.Equal(30011, setup.CreateSettings().ChannelPort);      // 目录端口不串到频道端口上
        Assert.Equal(30011, setup.DefaultChannelPort);
    }

    [Fact]
    public void Dev_build_overload_without_a_directory_port_gets_exactly_the_default_literal()
    {
        WriteDevRoot();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);

        Assert.True(setup.Configured);
        Assert.Equal(7001, setup.CreateSettings().ChannelDirectoryPort);
    }

    [Theory]
    [MemberData(nameof(PortCases))]
    public void Non_dev_build_ignores_the_directory_port_for_every_value(int given, int expected)
    {
        _ = expected;                                                 // 非开发版没有"生效"的值,全部是默认
        WriteDevRoot();
        WriteValidConfig();

        OnlineShellSetup withRoot = OnlineShellSetupLoader.Load(false, RepoDir, 0, DataDir, null, null, given);
        OnlineShellSetup withoutRoot = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null, given);
        OnlineShellSetup devFlagButNoRoot = OnlineShellSetupLoader.Load(true, null, 0, DataDir, null, null, given);

        foreach (OnlineShellSetup setup in new[] { withRoot, withoutRoot, devFlagButNoRoot })
        {
            Assert.True(setup.Configured);
            Assert.Equal(OnlineShellConfigSource.ConfigFile, setup.Source);
            Assert.Equal(7001, setup.CreateSettings().ChannelDirectoryPort);
        }
    }

    // ------------------------------------------------------------------ 层 ②:会话薄皮的构造

    [Theory]
    [MemberData(nameof(PortCases))]
    public void Session_port_connects_to_the_settings_directory_port_only_inside_1_to_65535(int given, int expected)
    {
        using var port = new RealOnlineSessionPort(new OnlineEntrySettings { ChannelDirectoryPort = given });

        Assert.Equal(expected, EffectiveDirectoryPort(port));
    }

    [Fact]
    public void Session_port_with_untouched_settings_uses_the_default_directory_port()
    {
        using var port = new RealOnlineSessionPort(new OnlineEntrySettings());

        Assert.Equal(7001, EffectiveDirectoryPort(port));
    }

    // ------------------------------------------------------------------ 两层串起来

    [Theory]
    [MemberData(nameof(PortCases))]
    public void Dev_setup_feeds_the_session_port_and_non_dev_setup_never_does(int given, int expected)
    {
        WriteDevRoot();
        WriteValidConfig();

        OnlineEntrySettings devSettings = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null, given).CreateSettings();
        using (var devPort = new RealOnlineSessionPort(devSettings))
        {
            Assert.Equal(expected, EffectiveDirectoryPort(devPort));
        }

        OnlineEntrySettings prodSettings = OnlineShellSetupLoader.Load(false, RepoDir, 0, DataDir, null, null, given).CreateSettings();
        using (var prodPort = new RealOnlineSessionPort(prodSettings))
        {
            Assert.Equal(7001, EffectiveDirectoryPort(prodPort));
        }
    }
}
