using System.Text;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网入口配置文件(键 = 值文本)的解析:必填项缺失与不合规逐项点名、缺省值、边界、格式容错、报错文案不带任何值、纯函数不碰网络。
/// 所有取值都是合成的:密钥由字节规律派生、地址用保留的 .invalid 域名,不含任何真实服务器配置。
/// </summary>
public sealed class OnlineConnectionConfigTests
{
    private const string Url = "http://gateway.example.invalid:18080";
    private const string Cipher = "A1B2C3D4E5F60718";

    private static string Key => GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey(5));

    private static string Text(params string[] lines) => string.Join("\n", lines);

    private static string[] Required(string url = Url, string key = null, string cipher = Cipher)
    {
        return new[] { "server_url = " + url, "aes_key = " + (key ?? Key), "mouse_cipher = " + cipher };
    }

    private static OnlineConnectionProblem Problem(params string[] lines)
    {
        bool ok = OnlineConnectionConfig.TryParse(Text(lines), out OnlineConnectionConfig config, out OnlineConnectionProblem problem);
        Assert.False(ok);
        Assert.Null(config);
        return problem;
    }

    private static OnlineConnectionConfig Parse(params string[] lines)
    {
        bool ok = OnlineConnectionConfig.TryParse(Text(lines), out OnlineConnectionConfig config, out OnlineConnectionProblem problem);
        Assert.True(ok, problem.ToString());
        Assert.Equal(OnlineConnectionProblem.None, problem);
        return config;
    }

    // ---------- 成功与缺省 ----------

    [Fact]
    public void A_complete_file_parses_into_every_field()
    {
        OnlineConnectionConfig config = Parse(
            "server_url = " + Url, "aes_key = " + Key, "mouse_cipher = " + Cipher, "data_version = 20090101", "channel_port = 31022");

        Assert.Equal(Url, config.Gateway.BaseUrl);
        Assert.Equal(GatewayTestSupport.SynthKey(5), config.Gateway.CopyAesKey());
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18 }, config.CopyMouseCipher());
        Assert.Equal(20090101u, config.DataVersion);
        Assert.Equal(31022, config.ChannelPort);
        Assert.Equal(TimeSpan.FromSeconds(15), config.Gateway.Timeout);
    }

    [Fact]
    public void Optional_fields_default_to_the_values_the_dev_path_and_the_settings_use()
    {
        OnlineConnectionConfig config = Parse(Required());

        Assert.Equal(LocalDevEntrySettings.DefaultDataVersion, config.DataVersion);
        Assert.Equal(20080418u, config.DataVersion);
        Assert.Equal(30011, config.ChannelPort);
        Assert.Equal(OnlineConnectionConfig.DefaultChannelPort, new OnlineEntrySettings().ChannelPort);
        Assert.Equal(new OnlineEntrySettings().ChannelPort, config.ChannelPort);
    }

    [Theory]
    [InlineData("data_version =")]
    [InlineData("data_version = \"\"")]
    public void An_empty_optional_value_counts_as_missing_and_takes_the_default(string line)
    {
        OnlineConnectionConfig config = Parse(Required().Append(line).ToArray());
        Assert.Equal(LocalDevEntrySettings.DefaultDataVersion, config.DataVersion);
    }

    [Fact]
    public void An_empty_channel_port_takes_the_default()
    {
        Assert.Equal(30011, Parse(Required().Append("channel_port =").ToArray()).ChannelPort);
    }

    [Fact]
    public void Settings_carry_the_gateway_cipher_version_and_port_but_no_credentials()
    {
        OnlineConnectionConfig config = Parse(Required().Append("channel_port = 31022").ToArray());

        OnlineEntrySettings settings = config.ToSettings();

        Assert.Same(config.Gateway, settings.Gateway);
        Assert.Equal(config.CopyMouseCipher(), settings.MouseCipher);
        Assert.Equal(config.DataVersion, settings.DataVersion);
        Assert.Equal(31022, settings.ChannelPort);
        Assert.Equal(string.Empty, settings.Account);
        Assert.Equal(string.Empty, settings.Password);
    }

    [Fact]
    public void Settings_and_cipher_copies_are_independent_of_the_config()
    {
        OnlineConnectionConfig config = Parse(Required());
        OnlineEntrySettings first = config.ToSettings();
        first.MouseCipher[0] ^= 0xFF;
        config.CopyMouseCipher()[1] ^= 0xFF;

        Assert.Equal(0xA1, config.ToSettings().MouseCipher[0]);
        Assert.Equal(0xB2, config.ToSettings().MouseCipher[1]);
        Assert.NotSame(config.ToSettings().MouseCipher, config.ToSettings().MouseCipher);
    }

    // ---------- 必填项缺失 ----------

    [Fact]
    public void Each_required_field_missing_names_that_field()
    {
        Assert.Equal(OnlineConnectionProblem.GatewayUrlMissing, Problem("aes_key = " + Key, "mouse_cipher = " + Cipher));
        Assert.Equal(OnlineConnectionProblem.AesKeyMissing, Problem("server_url = " + Url, "mouse_cipher = " + Cipher));
        Assert.Equal(OnlineConnectionProblem.MouseCipherMissing, Problem("server_url = " + Url, "aes_key = " + Key));
    }

    [Theory]
    [InlineData("server_url =", OnlineConnectionProblem.GatewayUrlMissing)]
    [InlineData("aes_key =", OnlineConnectionProblem.AesKeyMissing)]
    [InlineData("mouse_cipher = ''", OnlineConnectionProblem.MouseCipherMissing)]
    public void A_required_field_written_empty_is_missing(string line, OnlineConnectionProblem expected)
    {
        string key = line.Substring(0, line.IndexOf(' '));
        string[] others = Required().Where(l => !l.StartsWith(key)).Append(line).ToArray();
        Assert.Equal(expected, Problem(others));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("# only a comment")]
    [InlineData("no equals sign here")]
    public void Empty_or_meaningless_text_is_a_missing_gateway_url(string text)
    {
        Assert.False(OnlineConnectionConfig.TryParse(text, out OnlineConnectionConfig config, out OnlineConnectionProblem problem));
        Assert.Null(config);
        Assert.Equal(OnlineConnectionProblem.GatewayUrlMissing, problem);
    }

    // ---------- 不合规 ----------

    [Theory]
    [InlineData("not a url")]
    [InlineData("gateway.example.invalid:18080")]
    [InlineData("ftp://gateway.example.invalid")]
    [InlineData("http://gateway.example.invalid/?query=1")]
    [InlineData("http://gateway.example.invalid/#fragment")]
    [InlineData("http://someone@gateway.example.invalid")]
    [InlineData("file:///tmp/x")]
    public void A_bad_gateway_url_is_named(string url)
    {
        Assert.Equal(OnlineConnectionProblem.GatewayUrlInvalid, Problem(Required(url: url)));
    }

    [Theory]
    [InlineData("http://gateway.example.invalid")]
    [InlineData("https://gateway.example.invalid:8443/prefix")]
    [InlineData("  http://gateway.example.invalid  ")]
    public void Good_gateway_urls_are_accepted(string url)
    {
        Assert.NotNull(Parse(Required(url: url)));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(1)]
    public void An_aes_key_of_the_wrong_length_is_named(int length)
    {
        Assert.Equal(OnlineConnectionProblem.AesKeyInvalid, Problem(Required(key: new string('a', length))));
    }

    [Fact]
    public void An_aes_key_with_a_non_hex_character_is_named()
    {
        string bad = "g" + Key.Substring(1);
        Assert.Equal(OnlineConnectionProblem.AesKeyInvalid, Problem(Required(key: bad)));
    }

    [Fact]
    public void An_upper_case_aes_key_is_accepted_like_the_gateway_options_do()
    {
        OnlineConnectionConfig config = Parse(Required(key: Key.ToUpperInvariant()));
        Assert.Equal(GatewayTestSupport.SynthKey(5), config.Gateway.CopyAesKey());
    }

    [Theory]
    [InlineData("A1B2C3D4E5F6071")]
    [InlineData("A1B2C3D4E5F607188")]
    [InlineData("A1B2C3D4E5F6071G")]
    [InlineData("A1B2 C3D4E5F6071")]
    [InlineData("0x1B2C3D4E5F6071")]
    public void A_mouse_cipher_that_is_not_sixteen_hex_characters_is_named(string cipher)
    {
        Assert.Equal(OnlineConnectionProblem.MouseCipherInvalid, Problem(Required(cipher: cipher)));
    }

    [Fact]
    public void A_lower_case_mouse_cipher_is_the_same_eight_bytes()
    {
        Assert.Equal(Parse(Required()).CopyMouseCipher(), Parse(Required(cipher: Cipher.ToLowerInvariant())).CopyMouseCipher());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("4294967296")]
    [InlineData("99999999999999999999")]
    [InlineData("2008 0418")]
    public void A_bad_data_version_is_named(string value)
    {
        Assert.Equal(OnlineConnectionProblem.DataVersionInvalid, Problem(Required().Append("data_version = " + value).ToArray()));
    }

    [Theory]
    [InlineData("0", 0u)]
    [InlineData("1", 1u)]
    [InlineData("4294967295", 4294967295u)]
    public void Data_version_boundaries_are_accepted(string value, uint expected)
    {
        Assert.Equal(expected, Parse(Required().Append("data_version = " + value).ToArray()).DataVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+30011")]
    [InlineData("abc")]
    [InlineData("3e4")]
    [InlineData("30011.0")]
    [InlineData("99999999999")]
    public void A_channel_port_out_of_range_or_malformed_is_named(string value)
    {
        Assert.Equal(OnlineConnectionProblem.ChannelPortInvalid, Problem(Required().Append("channel_port = " + value).ToArray()));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("30011", 30011)]
    public void Channel_port_boundaries_are_accepted(string value, int expected)
    {
        Assert.Equal(expected, Parse(Required().Append("channel_port = " + value).ToArray()).ChannelPort);
    }

    [Fact]
    public void When_several_fields_are_wrong_the_first_in_the_fixed_order_is_reported()
    {
        string[] allBad = { "server_url = nope", "aes_key = nope", "mouse_cipher = nope", "data_version = nope", "channel_port = nope" };
        Assert.Equal(OnlineConnectionProblem.GatewayUrlInvalid, Problem(allBad));
        Assert.Equal(OnlineConnectionProblem.AesKeyInvalid, Problem(allBad.Skip(1).Prepend("server_url = " + Url).ToArray()));
        Assert.Equal(OnlineConnectionProblem.MouseCipherInvalid,
            Problem(allBad.Skip(2).Prepend("aes_key = " + Key).Prepend("server_url = " + Url).ToArray()));
        Assert.Equal(OnlineConnectionProblem.DataVersionInvalid, Problem(Required().Concat(allBad.Skip(3)).ToArray()));
        Assert.Equal(OnlineConnectionProblem.ChannelPortInvalid, Problem(Required().Append("channel_port = nope").ToArray()));
    }

    // ---------- 格式容错 ----------

    [Fact]
    public void Comments_blank_lines_crlf_quotes_and_key_case_are_tolerated()
    {
        string text = "# connection file\r\n\r\n"
            + "SERVER_URL = \"" + Url + "\"\r\n"
            + "  Aes_Key=" + Key + "  \r\n"
            + "mouse_cipher = '" + Cipher + "'\r\n"
            + "# channel_port = 1\r\n"
            + "unknown_key = whatever\r\n"
            + "channel_port = 31022\r\n";

        Assert.True(OnlineConnectionConfig.TryParse(text, out OnlineConnectionConfig config, out OnlineConnectionProblem problem), problem.ToString());
        Assert.Equal(Url, config.Gateway.BaseUrl);
        Assert.Equal(31022, config.ChannelPort);
    }

    [Fact]
    public void A_repeated_key_takes_the_last_value()
    {
        OnlineConnectionConfig config = Parse(Required().Concat(new[] { "channel_port = 1111", "channel_port = 2222" }).ToArray());
        Assert.Equal(2222, config.ChannelPort);
    }

    [Fact]
    public void A_value_may_contain_an_equals_sign_only_after_the_first_one()
    {
        // 键与值以第一个等号分开:网关地址后面带路径时里面若出现等号属于值(这里是查询串,所以地址不合规,而不是键错位)
        Assert.Equal(OnlineConnectionProblem.GatewayUrlInvalid, Problem(Required(url: "http://gateway.example.invalid/?a=b")));
    }

    // ---------- 报错文案不带值 ----------

    [Fact]
    public void Describe_names_the_item_and_never_echoes_a_value()
    {
        const string urlSecret = "http://secret-host.example.invalid:4711";
        string keySecret = new string('c', 63);
        const string cipherSecret = "ZZ00112233445566";
        const string versionSecret = "ver-secret-7";
        const string portSecret = "70000";

        var cases = new (string[] Lines, string Item)[]
        {
            (new[] { "server_url = ftp://secret-host.example.invalid:4711", "aes_key = " + Key, "mouse_cipher = " + Cipher }, "server_url"),
            (new[] { "server_url = " + urlSecret, "aes_key = " + keySecret, "mouse_cipher = " + Cipher }, "aes_key"),
            (new[] { "server_url = " + urlSecret, "aes_key = " + Key, "mouse_cipher = " + cipherSecret }, "mouse_cipher"),
            (Required(url: urlSecret).Append("data_version = " + versionSecret).ToArray(), "data_version"),
            (Required(url: urlSecret).Append("channel_port = " + portSecret).ToArray(), "channel_port"),
        };
        foreach ((string[] lines, string item) in cases)
        {
            OnlineConnectionProblem problem = Problem(lines);
            string message = OnlineConnectionConfig.Describe(problem);

            Assert.Contains(item, message);
            foreach (string secret in new[] { urlSecret, "secret-host", keySecret, cipherSecret, versionSecret, portSecret, Key, Cipher })
            {
                Assert.DoesNotContain(secret, message);
            }
        }
    }

    [Fact]
    public void Describe_covers_every_problem_and_is_empty_only_for_none()
    {
        foreach (OnlineConnectionProblem problem in Enum.GetValues<OnlineConnectionProblem>())
        {
            string message = OnlineConnectionConfig.Describe(problem);
            if (problem == OnlineConnectionProblem.None)
            {
                Assert.Equal(string.Empty, message);
            }
            else
            {
                Assert.NotEqual(string.Empty, message);
            }
        }
        Assert.Contains(OnlineConnectionConfig.FileName, OnlineConnectionConfig.Describe(OnlineConnectionProblem.FileMissing));
    }

    // ---------- 读文件 ----------

    private static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dnf-online-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Loading_a_missing_file_or_a_directory_is_a_file_missing_problem()
    {
        string dir = TempDirectory();
        try
        {
            Assert.False(OnlineConnectionConfig.TryLoad(OnlineConnectionConfig.PathUnder(dir), out OnlineConnectionConfig config, out OnlineConnectionProblem problem));
            Assert.Null(config);
            Assert.Equal(OnlineConnectionProblem.FileMissing, problem);

            Assert.False(OnlineConnectionConfig.TryLoad(dir, out _, out problem));
            Assert.Equal(OnlineConnectionProblem.FileMissing, problem);
            Assert.False(OnlineConnectionConfig.TryLoad(null, out _, out problem));
            Assert.Equal(OnlineConnectionProblem.FileMissing, problem);
            Assert.False(OnlineConnectionConfig.TryLoad(string.Empty, out _, out problem));
            Assert.Equal(OnlineConnectionProblem.FileMissing, problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Loading_a_file_with_a_byte_order_mark_parses_it()
    {
        string dir = TempDirectory();
        try
        {
            string path = OnlineConnectionConfig.PathUnder(dir);
            File.WriteAllText(path, Text(Required()), new UTF8Encoding(true));

            Assert.True(OnlineConnectionConfig.TryLoad(path, out OnlineConnectionConfig config, out OnlineConnectionProblem problem), problem.ToString());
            Assert.Equal(Url, config.Gateway.BaseUrl);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Loading_a_file_with_a_problem_reports_the_same_problem_as_parsing()
    {
        string dir = TempDirectory();
        try
        {
            string path = OnlineConnectionConfig.PathUnder(dir);
            File.WriteAllText(path, Text(Required().Append("channel_port = 0").ToArray()));

            Assert.False(OnlineConnectionConfig.TryLoad(path, out _, out OnlineConnectionProblem problem));
            Assert.Equal(OnlineConnectionProblem.ChannelPortInvalid, problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_a_file_unreadable_problem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string dir = TempDirectory();
        try
        {
            string path = OnlineConnectionConfig.PathUnder(dir);
            File.WriteAllText(path, Text(Required()));
            File.SetUnixFileMode(path, UnixFileMode.None);
            bool readable;
            try
            {
                File.ReadAllText(path);
                readable = true;
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
            }
            if (readable)
            {
                return; // 以特权身份运行:权限挡不住,这个场景造不出来
            }

            Assert.False(OnlineConnectionConfig.TryLoad(path, out _, out OnlineConnectionProblem problem));
            Assert.Equal(OnlineConnectionProblem.FileUnreadable, problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_path_helper_puts_the_documented_file_name_under_the_directory()
    {
        Assert.Equal("dnf-online.conf", OnlineConnectionConfig.FileName);
        Assert.Equal(Path.Combine("some", "dir", "dnf-online.conf"), OnlineConnectionConfig.PathUnder(Path.Combine("some", "dir")));
    }
}
