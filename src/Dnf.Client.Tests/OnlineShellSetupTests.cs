using System.Reflection;
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
    public void A_dev_setup_keeps_the_account_and_password_out_of_the_session_template_and_out_of_the_object_it_holds()
    {
        WriteDev();

        OnlineShellSetup setup = OnlineShellSetupLoader.Load(true, RepoDir, 0, DataDir, null, null);

        // 夹具里确实有凭据可泄漏:登录界面的预填拿到了账号与口令
        Assert.False(string.IsNullOrEmpty(setup.PrefillUser));
        Assert.False(string.IsNullOrEmpty(setup.PrefillPassword));
        Assert.Equal("dev_account_1", setup.PrefillUser);
        Assert.Equal("dev-pass-synthetic", setup.PrefillPassword);

        // 会话的入参模板里却一个字也没有(登录时用界面里输入的);两条分开断言
        OnlineEntrySettings template = setup.CreateSettings();
        Assert.Equal(string.Empty, template.Account);
        Assert.Equal(string.Empty, template.Password);

        // CreateSettings 只抄一张固定的字段表(网关、鼠标口令密文、数据版本、频道端口、槽数),本来就不抄账号与口令,
        // 所以光看它的返回值,"读出来之后把账号与口令抹掉"那两行还在不在是看不出来的:
        // 这里直接看启动设置自己长期持有的那份入参对象,别让凭据在进程里多留一份(账号与口令各抹一行,各自一条断言)
        FieldInfo held = typeof(OnlineShellSetup).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(held);
        var retained = Assert.IsType<OnlineEntrySettings>(held.GetValue(setup));
        Assert.Equal(string.Empty, retained.Account);
        Assert.Equal(string.Empty, retained.Password);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, retained.MouseCipher);          // 同一个对象:其余内容没丢
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

/// <summary>
/// 没有持久数据目录就当没有配置文件:不拿相对路径去读进程当前目录里的同名文件。
/// 要在进程当前目录里放一份真的配置文件(固定文件名,而且不许改进程当前目录),所以放进专属的集合:
/// 同一集合里的测试不会并行,用完在 finally 里还原。别的测试类在产品行为正确时从不碰当前目录里的这个文件。
/// </summary>
[Collection("OnlineShellConfigFileInCurrentDirectory")]
public sealed class OnlineShellSetupCurrentDirectoryTests
{
    private static string[] ValidLines()
    {
        return new[]
        {
            "# synthetic test configuration",
            "server_url = http://gateway.example.invalid:18080",
            "aes_key = " + GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey(5)),
            "mouse_cipher = A1B2C3D4E5F60718",
        };
    }

    [Fact]
    public void Without_a_persistent_data_path_a_config_file_of_the_same_name_in_the_current_directory_is_not_read()
    {
        // 不拿相对路径去读进程当前目录里的同名文件:这里放的就是"空目录名解析出来的那个路径"(进程当前目录下的 dnf-online.conf)
        string decoy = Path.GetFullPath(OnlineConnectionConfig.PathUnder(string.Empty));
        byte[] before = File.Exists(decoy) ? File.ReadAllBytes(decoy) : null;     // 万一原来就有一份(别人留下的):用完原样还原,不删
        string data = Path.Combine(Path.GetTempPath(), "dnf-shell-cwd-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllLines(decoy, ValidLines());
            Directory.CreateDirectory(data);
            File.WriteAllLines(OnlineConnectionConfig.PathUnder(data), ValidLines());

            // 当前目录里的这份本身是合法的配置:真去读就会读成功
            Assert.True(OnlineConnectionConfig.TryLoad(decoy, out _, out OnlineConnectionProblem problem), problem.ToString());
            // 对照:同一份内容放在持久数据目录里、传该目录时读得到
            OnlineShellSetup control = OnlineShellSetupLoader.Load(false, null, 0, data, null, null);
            Assert.True(control.Configured);
            Assert.Equal(OnlineShellConfigSource.ConfigFile, control.Source);

            foreach (bool devBuild in new[] { false, true })
            {
                foreach (string noPath in new string[] { null, string.Empty })
                {
                    string which = (devBuild ? "开发版" : "用户版") + "/" + (noPath == null ? "null" : "空串");

                    OnlineShellSetup setup = OnlineShellSetupLoader.Load(devBuild, null, 0, noPath, null, null);

                    Assert.False(setup.Configured, which);
                    Assert.Equal(OnlineShellConfigSource.None, setup.Source);
                    Assert.Equal("找不到连接配置文件 dnf-online.conf", setup.ConfigDetail);
                    Assert.Null(setup.CreateSettings());
                }
            }
        }
        finally
        {
            if (before == null)
            {
                File.Delete(decoy);
            }
            else
            {
                File.WriteAllBytes(decoy, before);
            }
            if (Directory.Exists(data))
            {
                Directory.Delete(data, true);
            }
        }
    }
}
