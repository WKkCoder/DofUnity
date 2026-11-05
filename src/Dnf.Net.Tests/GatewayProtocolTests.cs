using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>协议常量、错误键映射与游戏服 IP 文本的判定。期望值来自任务规格与 IPv4/IPv6 的文本规则;点分地址一律用 Dots 拼出。</summary>
public sealed class GatewayProtocolTests
{
    // ---------- 常量(规格里的字面值) ----------

    [Fact]
    public void RoutesAndCommandNamesAreTheDocumentedOnes()
    {
        Assert.Equal("/health", GatewayProtocol.HealthRoute);
        Assert.Equal("/api/v1/game-server-ip", GatewayProtocol.GameServerIpRoute);
        Assert.Equal("/api/v1/auth", GatewayProtocol.AuthRoute);
        Assert.Equal("login", GatewayProtocol.LoginCommand);
        Assert.Equal("regedit", GatewayProtocol.RegisterCommand);
        Assert.Equal("forget", GatewayProtocol.ForgotCommand);
        Assert.Equal("repasswd", GatewayProtocol.ChangePasswordCommand);
        Assert.Equal("application/octet-stream", GatewayProtocol.RequestContentType);
        Assert.Equal('|', GatewayProtocol.FieldSeparator);
        Assert.Equal("0|", GatewayProtocol.TokenPrefix);
        Assert.Equal("success", GatewayProtocol.SuccessText);
    }

    [Fact]
    public void LimitsAreTheDocumentedOnes()
    {
        Assert.Equal(4096, GatewayProtocol.MaxRequestBodyBytes);
        Assert.Equal(10, GatewayProtocol.RateLimitMaxRequests);
        Assert.Equal(TimeSpan.FromSeconds(60), GatewayProtocol.RateLimitWindow);
        Assert.Equal(4, GatewayProtocol.UsernameMinLength);
        Assert.Equal(32, GatewayProtocol.UsernameMaxLength);
        Assert.Equal(32, GatewayProtocol.Md5HexLength);
        Assert.Equal(64, GatewayProtocol.AesKeyHexLength);
        Assert.Equal("00:00:00:00:00:00", GatewayProtocol.DefaultMac);
    }

    // ---------- 错误键 ----------

    [Theory]
    [InlineData("wrong_credentials", GatewayErrorCode.WrongCredentials)]
    [InlineData("account_banned", GatewayErrorCode.AccountBanned)]
    [InlineData("registration_closed", GatewayErrorCode.RegistrationClosed)]
    [InlineData("user_exists", GatewayErrorCode.UserExists)]
    [InlineData("invalid_username", GatewayErrorCode.InvalidUsername)]
    [InlineData("invalid_password", GatewayErrorCode.InvalidPassword)]
    [InlineData("invalid_qq", GatewayErrorCode.InvalidQq)]
    [InlineData("fail", GatewayErrorCode.Fail)]
    public void EachKnownKeyMapsToItsOwnCode(string key, GatewayErrorCode expected)
    {
        Assert.Equal(expected, GatewayProtocol.ErrorCodeOf(key));
        Assert.Equal(expected, GatewayProtocol.ErrorCodeOf(key.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("success")]
    [InlineData("wrong_credential")]
    [InlineData("wrong_credentials ")]
    [InlineData(" fail")]
    [InlineData("fail2")]
    [InlineData("invalid")]
    [InlineData("some_other_error")]
    public void AnythingElseIsUnknown(string key)
    {
        Assert.Equal(GatewayErrorCode.Unknown, GatewayProtocol.ErrorCodeOf(key));
    }

    [Fact]
    public void ErrorCodesAreDistinctAndNoneIsNotAnErrorKey()
    {
        string[] keys = { "wrong_credentials", "account_banned", "registration_closed", "user_exists", "invalid_username", "invalid_password", "invalid_qq", "fail" };
        GatewayErrorCode[] codes = keys.Select(GatewayProtocol.ErrorCodeOf).ToArray();
        Assert.Equal(keys.Length, codes.Distinct().Count());
        Assert.DoesNotContain(GatewayErrorCode.Unknown, codes);
        Assert.DoesNotContain(GatewayErrorCode.None, codes);
    }

    // ---------- IP 文本 ----------

    [Fact]
    public void ValidIpv4Texts()
    {
        Assert.True(GatewayProtocol.IsIpText("127.0.0.1"));
        Assert.True(GatewayProtocol.IsIpText("0.0.0.0"));
        Assert.True(GatewayProtocol.IsIpText("255.255.255.255"));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(10, 20, 30, 40)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, 4)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(100, 200, 250, 199)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(9, 99, 9, 99)));
    }

    [Fact]
    public void EachIpv4PartIsLimitedToTwoFiftyFive()
    {
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(255, 0, 0, 0)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 255, 0, 0)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 0, 255, 0)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 0, 0, 255)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(256, 0, 0, 0)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 256, 0, 0)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 0, 256, 0)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 0, 0, 256)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(300, 1, 1, 1)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 1, 1, 999)));
    }

    [Fact]
    public void Ipv4PartsLongerThanThreeDigitsAreRejectedEvenIfTheyWrapAround()
    {
        // 4294967296 = 2^32:按 32 位整数累加会回绕成 0,必须在累加之前就按长度拦下
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "4294967296")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "1000")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots("4294967297", 2, 3, 4)));
    }

    [Fact]
    public void Ipv4MustHaveFourDecimalPartsWithoutLeadingZeros()
    {
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, 4, 5)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots("01", 2, 3, 4)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "04")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "004")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, "", 3, 4)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "")));
        Assert.False(GatewayProtocol.IsIpText("." + GatewayTestSupport.Dots(1, 2, 3)));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots("a", "b", "c", "d")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "-4")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "+4")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "0x4")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "4a")));
        Assert.False(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, "\u0664")));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(1, 2, 3, 0)));
        Assert.True(GatewayProtocol.IsIpText(GatewayTestSupport.Dots(0, 2, 3, 4)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("localhost")]
    [InlineData("gate.example")]
    [InlineData("127.0.0.1:80")]
    [InlineData("127.0.0.1/24")]
    [InlineData("http://127.0.0.1")]
    public void ShorthandsHostNamesAndDecoratedFormsAreNotIpTexts(string text)
    {
        Assert.False(GatewayProtocol.IsIpText(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 127.0.0.1")]
    [InlineData("127.0.0.1 ")]
    [InlineData("127.0.0.1\n")]
    [InlineData("\t127.0.0.1")]
    public void NoTrimmingIsDoneByTheCheckItself(string text)
    {
        Assert.False(GatewayProtocol.IsIpText(text));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:DB8::ABCD")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001")]
    [InlineData("1:2:3:4:5:6:7:8")]
    [InlineData("::ffff:255.255.255.255")]
    [InlineData("0000:0000:0000:0000:0000:ffff:255.255.255.255")]
    public void ValidIpv6Texts(string text)
    {
        Assert.True(GatewayProtocol.IsIpText(text));
    }

    [Theory]
    [InlineData("::1%eth0")]
    [InlineData("fe80::1%1")]
    [InlineData("::1%0")]
    [InlineData("fe80::abcd%ab")]
    [InlineData("[::1]")]
    [InlineData("[::1]:80")]
    [InlineData("2001:db8::1::2")]
    [InlineData("12345::1")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData(":::")]
    [InlineData("::g")]
    [InlineData("::1 ")]
    [InlineData(" ::1")]
    [InlineData("::1/64")]
    [InlineData(":")]
    [InlineData("1:2")]
    public void InvalidOrDecoratedIpv6TextsAreRejected(string text)
    {
        Assert.False(GatewayProtocol.IsIpText(text));
    }

    [Fact]
    public void VeryLongTextsAreRejectedQuickly()
    {
        Assert.False(GatewayProtocol.IsIpText(new string('1', 100000)));
        Assert.False(GatewayProtocol.IsIpText(new string(':', 100000)));
        Assert.False(GatewayProtocol.IsIpText(new string('.', 100000)));
        Assert.False(GatewayProtocol.IsIpText(string.Join(":", Enumerable.Repeat("1", 40000))));
    }
}
