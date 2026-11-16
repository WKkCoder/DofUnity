using Dnf.Net.Session;
using Dnf.Net.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网外壳启动设置的来源选择:正式分发读持久数据目录下的配置文件,开发版读 -dnfnetroot 的本机凭据(两者不混用),
/// 以及登录界面预填(账号来自命令行或上次记下的,口令只来自开发版本机凭据)。所有文件都是临时目录里的合成内容,从不碰真实的 local.env 或机密文件。
/// </summary>
public sealed class OnlineShellSetupTests : IDisposable
{
    private const string DevAccount = "dev_account_1";
    private const string DevPassword = "dev-pass-synthetic";
    private const string Mouse = "0102030405060708";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dnf-shell-setup-" + Guid.NewGuid().ToString("N"));

    public OnlineShellSetupTests()
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

    private void WriteConfig(params string[] lines)
    {
        File.WriteAllLines(OnlineConnectionConfig.PathUnder(DataDir), lines);
    }

    private void WriteValidConfig(params string[] extra)
    {
        var lines = new List<string>
        {
            "# synthetic test configuration",
            "server_url = http://localhost",
            "aes_key = " + KeyHex,
            "mouse_cipher = " + Mouse,
        };
        lines.AddRange(extra);
        WriteConfig(lines.ToArray());
    }

    private void WriteDev(string account = DevAccount, string password = DevPassword, bool writeSecrets = true)
    {
        File.WriteAllLines(Path.Combine(RepoDir, "local.env"), new[] { "DNF_DIR=" + GameDir });
        File.WriteAllLines(
            Path.Combine(GameDir, "Config.toml"),
            new[] { "server_url = \"http://localhost\"", "aes_key = \"" + KeyHex + "\"" });
        if (writeSecrets)
        {
            File.WriteAllLines(
                Path.Combine(RepoDir, "local.secrets.env"),
                new[] { "DNF_ACCOUNT=" + account, "DNF_PASSWORD=" + password, "DNF_MOUSE_CIPHER_HEX=" + Mouse });
        }
    }

    // ---- 配置文件(正式分发) ----

    [Fact]
    public void A_valid_config_file_gives_a_configured_setup_with_documented_defaults()
    {
        WriteValidConfig();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);

        Assert.True(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.ConfigFile, setup.Source);
        Assert.Null(setup.ConfigDetail);
        Assert.Equal(30011, setup.DefaultChannelPort);
        Assert.Null(setup.PrefillPassword);
        OnlineEntrySettings settings = setup.CreateSettings();
        Assert.NotNull(settings.Gateway);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, settings.MouseCipher);
        Assert.Equal(LocalDevEntrySettings.DefaultDataVersion, settings.DataVersion);
        Assert.Equal(30011, settings.ChannelPort);
        Assert.Equal(string.Empty, settings.Account);
        Assert.Equal(string.Empty, settings.Password);
    }

    [Fact]
    public void The_optional_keys_override_the_defaults()
    {
        WriteValidConfig("data_version = 1234", "channel_port = 31002");

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);

        Assert.True(setup.Configured);
        Assert.Equal(31002, setup.DefaultChannelPort);
        OnlineEntrySettings settings = setup.CreateSettings();
        Assert.Equal(1234u, settings.DataVersion);
        Assert.Equal(31002, settings.ChannelPort);
    }

    [Fact]
    public void A_missing_file_is_unconfigured_and_names_only_the_problem()
    {
        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);

        Assert.False(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.None, setup.Source);
        Assert.Equal(OnlineConnectionConfig.Describe(OnlineConnectionProblem.FileMissing), setup.ConfigDetail);
        Assert.Equal(30011, setup.DefaultChannelPort);
        Assert.Null(setup.CreateSettings());
    }

    [Fact]
    public void Each_bad_item_is_named_and_none_of_the_values_leak_into_the_message()
    {
        string key = KeyHex;
        var cases = new (string[] Lines, OnlineConnectionProblem Problem)[]
        {
            (new[] { "aes_key = " + key, "mouse_cipher = " + Mouse }, OnlineConnectionProblem.GatewayUrlMissing),
            (new[] { "server_url = not-a-url-value", "aes_key = " + key, "mouse_cipher = " + Mouse }, OnlineConnectionProblem.GatewayUrlInvalid),
            (new[] { "server_url = http://localhost", "mouse_cipher = " + Mouse }, OnlineConnectionProblem.AesKeyMissing),
            (new[] { "server_url = http://localhost", "aes_key = zz" + key.Substring(2), "mouse_cipher = " + Mouse }, OnlineConnectionProblem.AesKeyInvalid),
            (new[] { "server_url = http://localhost", "aes_key = " + key }, OnlineConnectionProblem.MouseCipherMissing),
            (new[] { "server_url = http://localhost", "aes_key = " + key, "mouse_cipher = " + Mouse.Substring(1) }, OnlineConnectionProblem.MouseCipherInvalid),
            (new[] { "server_url = http://localhost", "aes_key = " + key, "mouse_cipher = " + Mouse, "data_version = abc" }, OnlineConnectionProblem.DataVersionInvalid),
            (new[] { "server_url = http://localhost", "aes_key = " + key, "mouse_cipher = " + Mouse, "channel_port = 0" }, OnlineConnectionProblem.ChannelPortInvalid),
            (new[] { "server_url = http://localhost", "aes_key = " + key, "mouse_cipher = " + Mouse, "channel_port = 65536" }, OnlineConnectionProblem.ChannelPortInvalid),
        };

        foreach ((string[] lines, OnlineConnectionProblem problem) in cases)
        {
            WriteConfig(lines);

            OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);

            Assert.False(setup.Configured);
            Assert.Equal(OnlineShellConfigSource.None, setup.Source);
            Assert.Equal(OnlineConnectionConfig.Describe(problem), setup.ConfigDetail);
            Assert.DoesNotContain(key, setup.ConfigDetail);
            Assert.DoesNotContain(Mouse, setup.ConfigDetail);
            Assert.DoesNotContain("localhost", setup.ConfigDetail);
        }
    }

    [Fact]
    public void A_missing_persistent_data_path_counts_as_a_missing_file()
    {
        foreach (string path in new string[] { null, string.Empty })
        {
            OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, path, null, null);

            Assert.False(setup.Configured);
            Assert.Equal(OnlineConnectionConfig.Describe(OnlineConnectionProblem.FileMissing), setup.ConfigDetail);
        }
    }

    [Fact]
    public void A_non_dev_build_ignores_the_dev_repo_root_and_uses_the_config_file()
    {
        WriteDev();
        WriteValidConfig("channel_port = 31003");

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, RepoDir, 0, DataDir, null, null);

        Assert.True(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.ConfigFile, setup.Source);
        Assert.Equal(31003, setup.DefaultChannelPort);
        Assert.Null(setup.PrefillUser);
        Assert.Null(setup.PrefillPassword);
    }

    [Fact]
    public void A_non_dev_build_without_a_config_file_never_falls_back_to_local_credentials()
    {
        WriteDev();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, RepoDir, 0, DataDir, null, null);

        Assert.False(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.None, setup.Source);
        Assert.Null(setup.PrefillPassword);
        Assert.Null(setup.PrefillUser);
    }

    // ---- 预填的账号 ----

    [Fact]
    public void The_command_line_user_beats_the_remembered_user_and_the_remembered_user_is_the_fallback()
    {
        WriteValidConfig();

        Assert.Equal("cli_user", OnlineShellSetupLoader.Load(false, null, 0, DataDir, "cli_user", "last_user").PrefillUser);
        Assert.Equal("last_user", OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, "last_user").PrefillUser);
        Assert.Equal("last_user", OnlineShellSetupLoader.Load(false, null, 0, DataDir, string.Empty, "last_user").PrefillUser);
        Assert.Null(OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null).PrefillUser);
        Assert.Null(OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, string.Empty).PrefillUser);
    }

    [Fact]
    public void An_unconfigured_setup_still_prefills_the_user()
    {
        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, DataDir, "cli_user", null);

        Assert.False(setup.Configured);
        Assert.Equal("cli_user", setup.PrefillUser);
    }

    // ---- 开发版:本机凭据 ----

    [Fact]
    public void A_dev_build_with_a_repo_root_reads_the_local_credentials_and_prefills_them()
    {
        WriteDev();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);

        Assert.True(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.LocalDev, setup.Source);
        Assert.Null(setup.ConfigDetail);
        Assert.Equal(DevAccount, setup.PrefillUser);
        Assert.Equal(DevPassword, setup.PrefillPassword);
        Assert.Equal(30011, setup.DefaultChannelPort);
        OnlineEntrySettings settings = setup.CreateSettings();
        // 会话入参模板里不留账号与口令(登录时用界面里输入的)
        Assert.Equal(string.Empty, settings.Account);
        Assert.Equal(string.Empty, settings.Password);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, settings.MouseCipher);
        Assert.Equal(LocalDevEntrySettings.DefaultDataVersion, settings.DataVersion);
    }

    [Fact]
    public void The_dev_channel_port_argument_is_used_when_valid_and_ignored_otherwise()
    {
        WriteDev();

        OnlineShellSetup good = OnlineShellSetupLoader.Load(true, RepoDir, 31005, DataDir, null, null);
        Assert.Equal(31005, good.DefaultChannelPort);
        Assert.Equal(31005, good.CreateSettings().ChannelPort);

        foreach (int bad in new[] { 0, -1, 65536, 100000 })
        {
            OnlineShellSetup fallback = OnlineShellSetupLoader.Load(true, RepoDir, bad, DataDir, null, null);
            Assert.Equal(30011, fallback.DefaultChannelPort);
            Assert.Equal(30011, fallback.CreateSettings().ChannelPort);
        }

        Assert.Equal(1, OnlineShellSetupLoader.Load(true, RepoDir, 1, DataDir, null, null).DefaultChannelPort);
        Assert.Equal(65535, OnlineShellSetupLoader.Load(true, RepoDir, 65535, DataDir, null, null).DefaultChannelPort);
    }

    [Fact]
    public void A_different_command_line_user_keeps_the_local_password_from_being_prefilled()
    {
        WriteDev();

        OnlineShellSetup other = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, "someone_else", null);
        Assert.Equal("someone_else", other.PrefillUser);
        Assert.Null(other.PrefillPassword);

        OnlineShellSetup same = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, DevAccount, null);
        Assert.Equal(DevAccount, same.PrefillUser);
        Assert.Equal(DevPassword, same.PrefillPassword);
    }

    [Fact]
    public void A_dev_build_whose_credentials_cannot_be_read_is_unconfigured_and_does_not_fall_back_to_the_config_file()
    {
        WriteDev(writeSecrets: false);
        WriteValidConfig();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, "last_user");

        Assert.False(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.None, setup.Source);
        Assert.False(string.IsNullOrEmpty(setup.ConfigDetail));
        Assert.Equal("last_user", setup.PrefillUser);
        Assert.Null(setup.PrefillPassword);
    }

    [Fact]
    public void A_missing_dev_repo_directory_is_unconfigured()
    {
        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, Path.Combine(_root, "does-not-exist"), 0, DataDir, null, null);

        Assert.False(setup.Configured);
        Assert.False(string.IsNullOrEmpty(setup.ConfigDetail));
    }

    [Fact]
    public void A_dev_build_without_a_repo_root_reads_the_config_file_like_a_user_build()
    {
        WriteValidConfig();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, null, 0, DataDir, null, null);

        Assert.True(setup.Configured);
        Assert.Equal(OnlineShellConfigSource.ConfigFile, setup.Source);
    }

    // ---- 副本与输出 ----

    [Fact]
    public void Every_call_to_create_settings_gives_a_fresh_copy_with_its_own_mouse_cipher()
    {
        WriteValidConfig();
        OnlineShellSetup setup = OnlineShellSetupLoader.Load(false, null, 0, DataDir, null, null);

        OnlineEntrySettings first = setup.CreateSettings();
        first.MouseCipher[0] = 0xFF;
        first.ChannelPort = 1;
        OnlineEntrySettings second = setup.CreateSettings();

        Assert.NotSame(first, second);
        Assert.NotSame(first.MouseCipher, second.MouseCipher);
        Assert.Equal(1, second.MouseCipher[0]);
        Assert.Equal(30011, second.ChannelPort);
        Assert.Same(first.Gateway, second.Gateway);
    }

    [Fact]
    public void The_setup_prints_nothing_about_its_content()
    {
        WriteDev();
        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);

        string text = setup.ToString();

        Assert.Equal("OnlineShellSetup", text);
        Assert.DoesNotContain(DevAccount, text);
        Assert.DoesNotContain(DevPassword, text);
    }
}
