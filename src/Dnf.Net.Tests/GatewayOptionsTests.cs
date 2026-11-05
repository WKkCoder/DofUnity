using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>配置校验:密钥 64 个十六进制字符(大小写都收,不 trim)、基地址、超时。期望值来自规格与手算。</summary>
public sealed class GatewayOptionsTests
{
    private const string Url = "http://127.0.0.1:8080";

    private static string KeyHex(char c)
    {
        return new string(c, 64);
    }

    [Fact]
    public void KeyHexLengthBoundary()
    {
        Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, new string('a', 63)));
        Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, new string('a', 65)));
        Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, string.Empty));
        Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, null));
        GatewayOptions ok = new(Url, new string('a', 64));
        Assert.Equal(32, ok.CopyAesKey().Length);
    }

    [Fact]
    public void KeyHexDecodesToTheSameBytesInAnyCase()
    {
        byte[] expected = GatewayTestSupport.SynthKey(5);
        string lower = GatewayTestSupport.HexLower(expected);
        string upper = lower.ToUpperInvariant();
        string mixed = string.Concat(lower.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c));
        Assert.NotEqual(lower, upper);
        Assert.Equal(expected, new GatewayOptions(Url, lower).CopyAesKey());
        Assert.Equal(expected, new GatewayOptions(Url, upper).CopyAesKey());
        Assert.Equal(expected, new GatewayOptions(Url, mixed).CopyAesKey());
    }

    [Fact]
    public void KeyHexDigitsMapToTheRightNibbles()
    {
        // 手算:"0f" → 0x0F、"f0" → 0xF0、"a5" → 0xA5、"5A" → 0x5A;字符 0-9 a-f A-F 全覆盖
        string hex = "0f" + "f0" + "a5" + "5A" + "09" + "9e" + "Bd" + "cC" + new string('0', 48);
        byte[] key = new GatewayOptions(Url, hex).CopyAesKey();
        Assert.Equal(new byte[] { 0x0F, 0xF0, 0xA5, 0x5A, 0x09, 0x9E, 0xBD, 0xCC }, key.Take(8).ToArray());
        Assert.All(key.Skip(8), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData('g')]
    [InlineData('G')]
    [InlineData(' ')]
    [InlineData('-')]
    [InlineData('\n')]
    [InlineData('٣')]
    [InlineData('/')]
    [InlineData(':')]
    public void NonHexCharactersAreRejectedAtAnyPosition(char bad)
    {
        foreach (int index in new[] { 0, 31, 63 })
        {
            char[] chars = KeyHex('a').ToCharArray();
            chars[index] = bad;
            Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, new string(chars)));
        }
    }

    [Fact]
    public void SurroundingWhitespaceIsNotTrimmedForTheKey()
    {
        Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, KeyHex('a') + "\n"));
        Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, " " + KeyHex('a')));
    }

    [Fact]
    public void KeyCopyIsIsolatedFromTheConfiguration()
    {
        GatewayOptions options = new(Url, KeyHex('7'));
        byte[] copy = options.CopyAesKey();
        copy[0] = 0;
        Assert.Equal(0x77, options.CopyAesKey()[0]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    [InlineData("https://gate.example", "https://gate.example")]
    [InlineData("HTTPS://Gate.Example/", "https://gate.example")]
    [InlineData("http://127.0.0.1/prefix/", "http://127.0.0.1/prefix")]
    [InlineData("  http://127.0.0.1:8080  ", "http://127.0.0.1:8080")]
    // 全角空格与不换行空格不会被 Uri 自己忽略(它只忽略 ASCII 空白),输入框里复制出来的地址常带它们
    [InlineData("\u3000http://127.0.0.1:8080\u3000", "http://127.0.0.1:8080")]
    [InlineData("\u00a0http://127.0.0.1:8080/\u00a0", "http://127.0.0.1:8080")]
    public void BaseUrlIsNormalizedWithoutTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, new GatewayOptions(input, KeyHex('a')).BaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gate.example:8080")]
    [InlineData("/relative/path")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///tmp/x")]
    [InlineData("http://127.0.0.1:8080/?a=1")]
    [InlineData("http://127.0.0.1:8080/#frag")]
    [InlineData("http://user@127.0.0.1:8080")]
    [InlineData("http://")]
    public void UnusableBaseUrlsAreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => new GatewayOptions(input, KeyHex('a')));
    }

    [Fact]
    public void UrlForAppendsTheRouteToTheNormalizedBase()
    {
        GatewayOptions options = new("http://127.0.0.1:8080/", KeyHex('a'));
        Assert.Equal("http://127.0.0.1:8080/api/v1/auth", options.UrlFor(GatewayProtocol.AuthRoute));
        Assert.Equal("http://127.0.0.1:8080/health", options.UrlFor("/health"));
        Assert.Throws<ArgumentNullException>(() => options.UrlFor(null));
    }

    [Fact]
    public void TimeoutDefaultAndBoundaries()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), new GatewayOptions(Url, KeyHex('a')).Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1), new GatewayOptions(Url, KeyHex('a'), TimeSpan.FromMilliseconds(1)).Timeout);
        Assert.Equal(TimeSpan.FromMinutes(5), new GatewayOptions(Url, KeyHex('a'), TimeSpan.FromMinutes(5)).Timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayOptions(Url, KeyHex('a'), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayOptions(Url, KeyHex('a'), TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayOptions(Url, KeyHex('a'), TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayOptions(Url, KeyHex('a'), Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void TryCreateReportsAProblemWithoutEchoingTheInputs()
    {
        string badKey = new string('z', 64);
        Assert.False(GatewayOptions.TryCreate(Url, badKey, null, out GatewayOptions options, out string problem));
        Assert.Null(options);
        Assert.NotEmpty(problem);
        Assert.DoesNotContain(badKey, problem);
        Assert.DoesNotContain("127.0.0.1", problem);

        Assert.False(GatewayOptions.TryCreate("ftp://127.0.0.1", KeyHex('a'), null, out _, out string urlProblem));
        Assert.DoesNotContain("127.0.0.1", urlProblem);
        Assert.False(GatewayOptions.TryCreate(Url, KeyHex('a'), TimeSpan.Zero, out _, out string timeoutProblem));
        Assert.Contains("timeout", timeoutProblem);

        Assert.True(GatewayOptions.TryCreate(Url, KeyHex('a'), TimeSpan.FromSeconds(3), out options, out problem));
        Assert.Null(problem);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Timeout);
    }

    [Fact]
    public void ExceptionMessagesAndToStringNeverContainTheKeyOrTheHost()
    {
        string key = new string('c', 64);
        GatewayOptions options = new(Url, key);
        Assert.DoesNotContain(key, options.ToString());
        Assert.DoesNotContain("127.0.0.1", options.ToString());

        string shortKey = new string('d', 63);
        ArgumentException e = Assert.Throws<ArgumentException>(() => new GatewayOptions(Url, shortKey));
        Assert.DoesNotContain(shortKey, e.Message);
        ArgumentException u = Assert.Throws<ArgumentException>(() => new GatewayOptions("ftp://127.0.0.1:9", key));
        Assert.DoesNotContain("127.0.0.1", u.Message);
    }

    [Fact]
    public void ParseAesKeyHexWorksStandalone()
    {
        Assert.True(GatewayOptions.TryParseAesKeyHex(new string('1', 64), out byte[] key));
        Assert.Equal(32, key.Length);
        Assert.All(key, b => Assert.Equal(0x11, b));
        Assert.False(GatewayOptions.TryParseAesKeyHex(new string('1', 62), out byte[] none));
        Assert.Null(none);
    }
}
