using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>结果类型的日志安全属性:令牌只露前 4 个字符加长度,服务端文本被清洗并截断。</summary>
public sealed class GatewayResultTests
{
    [Theory]
    [InlineData(null, "(no token)")]
    [InlineData("", "(len 0)")]
    [InlineData("a", "(len 1)")]
    [InlineData("abcd", "(len 4)")]
    [InlineData("abcde", "abcd...(len 5)")]
    [InlineData("abcdefghij", "abcd...(len 10)")]
    public void DescribeTokenShowsAtMostTheFirstFourCharacters(string token, string expected)
    {
        Assert.Equal(expected, GatewayResult.DescribeToken(token));
    }

    [Fact]
    public void DescribeTokenOfALongTokenGivesOnlyThePrefixAndLength()
    {
        string token = "WXYZ" + new string('q', 996);
        string described = GatewayResult.DescribeToken(token);
        Assert.Equal("WXYZ...(len 1000)", described);
        Assert.DoesNotContain("qqq", described);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("a\nb\tc\rd", "a?b?c?d")]
    [InlineData("x\u007fy", "x?y")]
    [InlineData("x\u001fy", "x?y")]
    [InlineData("x y", "x y")]
    [InlineData("é中", "é中")]
    public void SanitizeReplacesControlCharacters(string text, string expected)
    {
        Assert.Equal(expected, GatewayResult.Sanitize(text));
    }

    [Fact]
    public void SanitizeKeepsAtMostSixtyFourCharactersAndMarksTruncation()
    {
        Assert.Equal(new string('x', 63), GatewayResult.Sanitize(new string('x', 63)));
        Assert.Equal(new string('x', 64), GatewayResult.Sanitize(new string('x', 64)));
        Assert.Equal(new string('x', 64) + "...", GatewayResult.Sanitize(new string('x', 65)));
        Assert.Equal(new string('x', 64) + "...", GatewayResult.Sanitize(new string('x', 5000)));
    }

    [Fact]
    public void SanitizedErrorKeysKeepLogsOnOneLine()
    {
        GatewayResult r = GatewayResponseParser.Parse("odd\u0001key");
        Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        Assert.DoesNotContain('\u0001', r.ToString());
        Assert.Contains("odd?key", r.ToString());
    }

    [Fact]
    public void ErrorResultToStringNamesTheCodeAndKey()
    {
        string text = GatewayResponseParser.Parse("user_exists").ToString();
        Assert.Contains("ServerError", text);
        Assert.Contains("UserExists", text);
        Assert.Contains("user_exists", text);
    }

    [Fact]
    public void SuccessWithoutTokenToStringIsShortAndPlain()
    {
        string text = GatewayResponseParser.Parse("success").ToString();
        Assert.StartsWith("GatewayResult(Success", text);
        Assert.DoesNotContain("token", text);
        Assert.DoesNotContain("error", text);
    }
}
