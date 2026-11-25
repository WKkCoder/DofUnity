using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 客户端版本号从本机配置走到联网入参(台账 E-453):开发路径 <see cref="LocalDevEntrySettings"/> 读 local.env 的 <c>DNF_CLIENT_VERSION</c>,
/// 正式分发路径 <see cref="OnlineConnectionConfig"/> 读配置文件的 <c>client_version</c>;两条路的缺省都是 0,不合规都被点名而且不回显值。
/// 全在临时目录与内存文本里用合成假值,不碰任何真实账号、密钥或版本取值。
/// </summary>
public sealed class ClientVersionConfigTests : IDisposable
{
    private const string Url = "http://gateway.example.invalid:18080";
    private const string Cipher = "A1B2C3D4E5F60718";

    private readonly string _root;

    public ClientVersionConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dnf-client-version-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    // ---------------- 开发路径:local.env 的 DNF_CLIENT_VERSION ----------------

    private void WriteDevFiles(params string[] extraLocalEnvLines)
    {
        string dnf = Path.Combine(_root, "dnf-data");
        Directory.CreateDirectory(dnf);
        File.WriteAllLines(Path.Combine(_root, "local.env"), new[] { "DNF_DIR=" + dnf }.Concat(extraLocalEnvLines));
        File.WriteAllText(Path.Combine(dnf, "Config.toml"),
            "server_url = \"http://127.0.0.1:9\"\naes_key = \"" + new string('a', 64) + "\"\n");
        File.WriteAllText(Path.Combine(_root, "local.secrets.env"),
            "DNF_ACCOUNT=fake-user\nDNF_PASSWORD=fake-pass\nDNF_MOUSE_CIPHER_HEX=0011223344556677\n");
    }

    private OnlineEntrySettings LoadDev(params string[] extraLocalEnvLines)
    {
        WriteDevFiles(extraLocalEnvLines);
        Assert.True(LocalDevEntrySettings.TryLoad(_root, out OnlineEntrySettings settings, out string problem), problem);
        Assert.Null(problem);
        return settings;
    }

    [Fact]
    public void Dev_client_version_defaults_to_zero_when_the_key_is_absent()
    {
        Assert.Equal(0u, LoadDev().ClientVersion);
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("1", 1u)]
    [InlineData("42435", 42435u)]
    [InlineData("75002308", 75002308u)]
    [InlineData("4294967295", 4294967295u)]
    [InlineData("0000000012", 12u)]
    public void Dev_client_version_is_read_from_local_env(string text, uint expected)
    {
        Assert.Equal(expected, LoadDev("DNF_CLIENT_VERSION=" + text).ClientVersion);
    }

    [Theory]
    [InlineData("export DNF_CLIENT_VERSION=777")]
    [InlineData("DNF_CLIENT_VERSION=\"777\"")]
    [InlineData("DNF_CLIENT_VERSION='777'")]
    [InlineData("  DNF_CLIENT_VERSION = 777  ")]
    public void Dev_client_version_follows_the_env_file_syntax_the_other_keys_use(string line)
    {
        Assert.Equal(777u, LoadDev(line).ClientVersion);
    }

    [Theory]
    [InlineData("DNF_CLIENT_VERSION=")]
    [InlineData("DNF_CLIENT_VERSION=\"\"")]
    public void Dev_an_empty_value_counts_as_absent(string line)
    {
        Assert.Equal(0u, LoadDev(line).ClientVersion);
    }

    [Fact]
    public void Dev_a_repeated_key_takes_the_last_value()
    {
        Assert.Equal(22u, LoadDev("DNF_CLIENT_VERSION=11", "DNF_CLIENT_VERSION=22").ClientVersion);
    }

    [Fact]
    public void Dev_the_value_only_comes_from_local_env_not_from_the_secrets_file()
    {
        WriteDevFiles();
        File.AppendAllText(Path.Combine(_root, "local.secrets.env"), "DNF_CLIENT_VERSION=777\n");
        Assert.True(LocalDevEntrySettings.TryLoad(_root, out OnlineEntrySettings settings, out _));
        Assert.Equal(0u, settings.ClientVersion);
    }

    [Fact]
    public void Dev_the_other_settings_are_unchanged_by_the_new_key()
    {
        OnlineEntrySettings without = LoadDev();
        OnlineEntrySettings with = LoadDev("DNF_CLIENT_VERSION=777");

        Assert.Equal(without.DataVersion, with.DataVersion);
        Assert.Equal(without.ChannelPort, with.ChannelPort);
        Assert.Equal(without.MaxSlots, with.MaxSlots);
        Assert.Equal(without.ServerGroup, with.ServerGroup);
        Assert.Equal(without.Account, with.Account);
        Assert.Equal(without.MouseCipher, with.MouseCipher);
        Assert.Equal(LocalDevEntrySettings.DefaultDataVersion, with.DataVersion);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("12 34")]
    [InlineData("4294967296")]
    [InlineData("99999999999")]
    [InlineData("00000000000")]
    [InlineData("１２３")]
    public void Dev_a_bad_client_version_fails_naming_the_key_and_never_echoing_the_value(string bad)
    {
        WriteDevFiles("DNF_CLIENT_VERSION=" + bad);

        Assert.False(LocalDevEntrySettings.TryLoad(_root, out OnlineEntrySettings settings, out string problem));

        Assert.Null(settings);
        Assert.Contains("DNF_CLIENT_VERSION", problem);
        Assert.Contains("local.env", problem);
        Assert.DoesNotContain(bad, problem);
    }

    [Fact]
    public void Dev_the_key_name_constant_is_the_documented_one()
    {
        Assert.Equal("DNF_CLIENT_VERSION", LocalDevEntrySettings.KeyClientVersion);
    }

    // ---------------- 正式分发路径:配置文件的 client_version ----------------

    private static string Key => GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey(5));

    private static string[] Required() => new[] { "server_url = " + Url, "aes_key = " + Key, "mouse_cipher = " + Cipher };

    private static OnlineConnectionConfig Parse(params string[] lines)
    {
        bool ok = OnlineConnectionConfig.TryParse(string.Join("\n", lines), out OnlineConnectionConfig config, out OnlineConnectionProblem problem);
        Assert.True(ok, problem.ToString());
        Assert.Equal(OnlineConnectionProblem.None, problem);
        return config;
    }

    private static OnlineConnectionProblem Problem(params string[] lines)
    {
        bool ok = OnlineConnectionConfig.TryParse(string.Join("\n", lines), out OnlineConnectionConfig config, out OnlineConnectionProblem problem);
        Assert.False(ok);
        Assert.Null(config);
        return problem;
    }

    [Fact]
    public void Config_client_version_defaults_to_zero_and_reaches_the_settings_as_zero()
    {
        OnlineConnectionConfig config = Parse(Required());

        Assert.Equal(0u, config.ClientVersion);
        Assert.Equal(0u, config.ToSettings().ClientVersion);
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("1", 1u)]
    [InlineData("75002308", 75002308u)]
    [InlineData("4294967295", 4294967295u)]
    public void Config_client_version_is_read_and_reaches_the_settings(string text, uint expected)
    {
        OnlineConnectionConfig config = Parse(Required().Append("client_version = " + text).ToArray());

        Assert.Equal(expected, config.ClientVersion);
        OnlineEntrySettings settings = config.ToSettings();
        Assert.Equal(expected, settings.ClientVersion);
        Assert.Equal(config.DataVersion, settings.DataVersion);
        Assert.Equal(config.ChannelPort, settings.ChannelPort);
    }

    [Theory]
    [InlineData("client_version =")]
    [InlineData("client_version = \"\"")]
    public void Config_an_empty_client_version_counts_as_missing(string line)
    {
        Assert.Equal(0u, Parse(Required().Append(line).ToArray()).ClientVersion);
    }

    [Fact]
    public void Config_the_key_is_case_insensitive_quotes_are_stripped_and_the_last_repetition_wins()
    {
        Assert.Equal(31u, Parse(Required().Append("CLIENT_Version = \"31\"").ToArray()).ClientVersion);
        Assert.Equal(2u, Parse(Required().Concat(new[] { "client_version = 1", "client_version = 2" }).ToArray()).ClientVersion);
        Assert.Equal(1u, Parse(Required().Concat(new[] { "# client_version = 5", "client_version = 1" }).ToArray()).ClientVersion);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("12 34")]
    [InlineData("4294967296")]
    [InlineData("99999999999999999999")]
    [InlineData("00000000000")]
    [InlineData("１２３")]
    public void Config_a_bad_client_version_is_named(string bad)
    {
        Assert.Equal(OnlineConnectionProblem.ClientVersionInvalid, Problem(Required().Append("client_version = " + bad).ToArray()));
    }

    [Fact]
    public void Config_the_description_names_the_key_with_the_exact_requirement_and_never_echoes_the_value()
    {
        const string secretValue = "ver-secret-7";
        OnlineConnectionProblem problem = Problem(Required().Append("client_version = " + secretValue).ToArray());
        string message = OnlineConnectionConfig.Describe(problem);

        Assert.Equal("client_version 不合规(要 0 到 4294967295 的整数)", message);
        Assert.DoesNotContain(secretValue, message);
        Assert.Equal("client_version", OnlineConnectionConfig.KeyClientVersion);
    }

    [Fact]
    public void Config_the_client_version_is_checked_last_so_every_earlier_problem_still_wins()
    {
        string[] badVersion = { "client_version = nope" };
        Assert.Equal(OnlineConnectionProblem.ClientVersionInvalid, Problem(Required().Concat(badVersion).ToArray()));
        Assert.Equal(OnlineConnectionProblem.ChannelPortInvalid, Problem(Required().Concat(new[] { "channel_port = nope" }).Concat(badVersion).ToArray()));
        Assert.Equal(OnlineConnectionProblem.DataVersionInvalid, Problem(Required().Concat(new[] { "data_version = nope" }).Concat(badVersion).ToArray()));
        Assert.Equal(OnlineConnectionProblem.MouseCipherMissing, Problem(new[] { "server_url = " + Url, "aes_key = " + Key }.Concat(badVersion).ToArray()));
        Assert.Equal(OnlineConnectionProblem.GatewayUrlMissing, Problem(badVersion));
    }

    [Fact]
    public void Config_the_file_path_reads_the_key_too()
    {
        string path = OnlineConnectionConfig.PathUnder(_root);
        File.WriteAllLines(path, Required().Append("client_version = 424242"));

        Assert.True(OnlineConnectionConfig.TryLoad(path, out OnlineConnectionConfig config, out OnlineConnectionProblem problem), problem.ToString());
        Assert.Equal(424242u, config.ClientVersion);
    }
}
