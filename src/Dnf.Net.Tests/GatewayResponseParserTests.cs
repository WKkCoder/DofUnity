using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>认证响应明文(<c>0|令牌</c> / <c>success</c> / 错误键)的解析。令牌只是不透明文本,这里不对它的内容做任何假设。</summary>
public sealed class GatewayResponseParserTests
{
    [Fact]
    public void LoginSuccessCarriesTheTokenVerbatim()
    {
        GatewayResult r = GatewayResponseParser.Parse("0|abc123");
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.True(r.IsSuccess);
        Assert.Equal("abc123", r.Token);
        Assert.Null(r.ErrorKey);
        Assert.Equal(GatewayErrorCode.None, r.ErrorCode);
        Assert.Equal(0, r.HttpStatus);
    }

    [Theory]
    [InlineData("0|a+b/c==")]
    [InlineData("0|abc|def")]
    [InlineData("0|0|0")]
    [InlineData("0|  inner spaces  ")]
    [InlineData("0|tail\n")]
    [InlineData("0|中文令牌")]
    [InlineData("0|success")]
    [InlineData("0|wrong_credentials")]
    public void TokenIsKeptByteForByteAfterThePrefix(string plaintext)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal(plaintext.Substring(2), r.Token);
    }

    [Fact]
    public void LongTokensAreNotTruncated()
    {
        string token = new string('t', 5000);
        Assert.Equal(token, GatewayResponseParser.Parse("0|" + token).Token);
    }

    [Theory]
    [InlineData("  0|abc", "abc")]
    [InlineData("\n0|abc", "abc")]
    [InlineData("\t \r\n0|abc", "abc")]
    public void LeadingWhitespaceBeforeThePrefixIsIgnored(string plaintext, string token)
    {
        Assert.Equal(token, GatewayResponseParser.Parse(plaintext).Token);
    }

    [Theory]
    [InlineData("0|")]
    [InlineData("0| ")]
    [InlineData("0|   ")]
    [InlineData("0|\n")]
    [InlineData("0|\t\r\n")]
    public void EmptyOrBlankTokenIsAProtocolError(string plaintext)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
        Assert.Null(r.Token);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0\n")]
    [InlineData(" 0 ")]
    public void SuccessCodeWithoutATokenIsAProtocolError(string plaintext)
    {
        Assert.Equal(GatewayOutcome.ProtocolError, GatewayResponseParser.Parse(plaintext).Outcome);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("SUCCESS")]
    [InlineData("Success")]
    [InlineData("sUcCeSs")]
    [InlineData("success\n")]
    [InlineData("success \r\n")]
    [InlineData("  success")]
    [InlineData("\tsuccess\t")]
    public void SuccessKeywordIsRecognizedWithoutAToken(string plaintext)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Null(r.Token);
        Assert.Null(r.ErrorKey);
    }

    [Theory]
    [InlineData("successful")]
    [InlineData("succes")]
    [InlineData("success!")]
    [InlineData("not success")]
    public void NearMissesOfSuccessAreNotSuccess(string plaintext)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        Assert.Equal(GatewayErrorCode.Unknown, r.ErrorCode);
        Assert.Equal(plaintext, r.ErrorKey);
    }

    [Theory]
    [InlineData("wrong_credentials", GatewayErrorCode.WrongCredentials)]
    [InlineData("account_banned", GatewayErrorCode.AccountBanned)]
    [InlineData("registration_closed", GatewayErrorCode.RegistrationClosed)]
    [InlineData("user_exists", GatewayErrorCode.UserExists)]
    [InlineData("invalid_username", GatewayErrorCode.InvalidUsername)]
    [InlineData("invalid_password", GatewayErrorCode.InvalidPassword)]
    [InlineData("invalid_qq", GatewayErrorCode.InvalidQq)]
    [InlineData("fail", GatewayErrorCode.Fail)]
    public void KnownErrorKeysMapToTheirCodes(string key, GatewayErrorCode expected)
    {
        GatewayResult r = GatewayResponseParser.Parse(key);
        Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        Assert.False(r.IsSuccess);
        Assert.Equal(expected, r.ErrorCode);
        Assert.Equal(key, r.ErrorKey);
        Assert.Null(r.Token);
    }

    [Theory]
    [InlineData("WRONG_CREDENTIALS", GatewayErrorCode.WrongCredentials)]
    [InlineData("Account_Banned\n", GatewayErrorCode.AccountBanned)]
    [InlineData("  user_exists  ", GatewayErrorCode.UserExists)]
    [InlineData("FAIL\r\n", GatewayErrorCode.Fail)]
    public void ErrorKeysAreCaseInsensitiveAndTrimmed(string plaintext, GatewayErrorCode expected)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(expected, r.ErrorCode);
        Assert.Equal(plaintext.Trim(), r.ErrorKey);
    }

    [Fact]
    public void UnknownErrorKeyIsKeptAsText()
    {
        GatewayResult r = GatewayResponseParser.Parse("some_new_error_from_a_newer_server");
        Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        Assert.Equal(GatewayErrorCode.Unknown, r.ErrorCode);
        Assert.Equal("some_new_error_from_a_newer_server", r.ErrorKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\t\r\n ")]
    public void EmptyResponseIsAProtocolError(string plaintext)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
        Assert.Null(r.ErrorKey);
    }

    [Theory]
    [InlineData("1|secret_looking_text")]
    [InlineData("00|secret_looking_text")]
    [InlineData("a|b")]
    [InlineData("success|secret_looking_text")]
    [InlineData("fail|secret_looking_text")]
    [InlineData("|secret_looking_text")]
    public void ShapesWithASeparatorThatAreNotATokenAreProtocolErrorsAndNeverEchoed(string plaintext)
    {
        GatewayResult r = GatewayResponseParser.Parse(plaintext);
        Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
        Assert.Null(r.ErrorKey);
        Assert.Null(r.Token);
        Assert.DoesNotContain("secret_looking_text", r.Message);
        Assert.DoesNotContain("secret_looking_text", r.ToString());
    }

    [Fact]
    public void ParsingIsContextFreeSoACallerMustCheckForTheTokenItself()
    {
        // 解析器不知道这是登录还是注册:登录该有令牌、注册不该有,由客户端判断(见客户端测试)
        Assert.NotNull(GatewayResponseParser.Parse("0|t").Token);
        Assert.Null(GatewayResponseParser.Parse("success").Token);
    }

    [Fact]
    public void ResultDescriptionsNeverContainTheFullToken()
    {
        string token = "AbCdEfGh1234567890-secret-part";
        GatewayResult r = GatewayResponseParser.Parse("0|" + token);
        Assert.DoesNotContain(token, r.ToString());
        Assert.DoesNotContain("secret-part", r.ToString());
        Assert.DoesNotContain(token, r.Message);
        Assert.Contains("AbCd", r.ToString());
        Assert.Contains("len " + token.Length, r.ToString());
    }
}
