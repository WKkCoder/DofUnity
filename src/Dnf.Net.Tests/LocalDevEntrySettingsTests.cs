using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 本地开发入参读取的形状检查:全在临时目录里用合成假值,不碰任何真实账号与密钥。
/// </summary>
public class LocalDevEntrySettingsTests : IDisposable
{
    private readonly string _root;

    public LocalDevEntrySettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dnf-netprobe-dev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private string WriteDnfDir()
    {
        string dnf = Path.Combine(_root, "dnf-data");
        Directory.CreateDirectory(dnf);
        File.WriteAllText(Path.Combine(_root, "local.env"), "DNF_DIR=" + dnf + "\n");
        return dnf;
    }

    private void WriteAll(string account = "fake-user", string mouseHex = "0011223344556677", string aesKey = null)
    {
        string dnf = WriteDnfDir();
        File.WriteAllText(Path.Combine(dnf, "Config.toml"),
            "server_url = \"http://127.0.0.1:9\"\naes_key = \"" + (aesKey ?? new string('a', 64)) + "\"\n");
        File.WriteAllText(Path.Combine(_root, "local.secrets.env"),
            "DNF_ACCOUNT=" + account + "\nDNF_PASSWORD=fake-pass\nDNF_MOUSE_CIPHER_HEX=" + mouseHex + "\n");
    }

    [Fact]
    public void LoadsSettingsFromRepoFiles()
    {
        WriteAll();
        Assert.True(LocalDevEntrySettings.TryLoad(_root, out OnlineEntrySettings settings, out string problem));
        Assert.Null(problem);
        Assert.Equal("fake-user", settings.Account);
        Assert.Equal(LocalDevEntrySettings.DefaultDataVersion, settings.DataVersion);
        Assert.NotNull(settings.MouseCipher);
        Assert.Equal(8, settings.MouseCipher.Length);
        Assert.Equal(0x11, settings.MouseCipher[1]);
    }

    [Fact]
    public void MissingSecretsFailsWithoutValues()
    {
        string dnf = WriteDnfDir();
        File.WriteAllText(Path.Combine(dnf, "Config.toml"),
            "server_url = \"http://127.0.0.1:9\"\naes_key = \"" + new string('a', 64) + "\"\n");
        Assert.False(LocalDevEntrySettings.TryLoad(_root, out _, out string problem));
        Assert.Contains("local.secrets.env", problem);
    }

    [Fact]
    public void MissingRootFails()
    {
        Assert.False(LocalDevEntrySettings.TryLoad(Path.Combine(_root, "nope"), out _, out _));
    }

    [Fact]
    public void BadAesKeyShapeFails()
    {
        WriteAll(aesKey: "zz");
        Assert.False(LocalDevEntrySettings.TryLoad(_root, out _, out string problem));
        Assert.Contains("网关配置", problem);
    }

    [Fact]
    public void BadMouseHexDropsCipherButStillLoads()
    {
        WriteAll(mouseHex: "xy");
        Assert.True(LocalDevEntrySettings.TryLoad(_root, out OnlineEntrySettings settings, out _));
        Assert.Null(settings.MouseCipher);
    }
}
