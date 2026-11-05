using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>/health 的 JSON:只取顶层 status 与时间戳(键名未核实,接受 timestamp/time/ts)。期望值是手写的 JSON 与手算的结果。</summary>
public sealed class GatewayHealthParserTests
{
    private static GatewayHealth Parse(string json)
    {
        Assert.True(GatewayHealthParser.TryParse(json, out GatewayHealth health), "should parse: " + json);
        Assert.NotNull(health);
        return health;
    }

    [Fact]
    public void BasicDocument()
    {
        GatewayHealth h = Parse("{\"status\":\"ok\",\"timestamp\":1730000000}");
        Assert.Equal("ok", h.Status);
        Assert.Equal("1730000000", h.Timestamp);
    }

    [Fact]
    public void KeyOrderAndWhitespaceDoNotMatter()
    {
        GatewayHealth h = Parse("\n  {\r\n\t\"timestamp\" :  \"2026-11-05T10:00:00Z\" ,\n\t\"status\"\t:\t\"healthy\"\n}\n  ");
        Assert.Equal("healthy", h.Status);
        Assert.Equal("2026-11-05T10:00:00Z", h.Timestamp);
    }

    [Fact]
    public void ByteOrderMarkIsSkipped()
    {
        GatewayHealth h = Parse("\uFEFF{\"status\":\"ok\"}");
        Assert.Equal("ok", h.Status);
        Assert.Null(h.Timestamp);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("time")]
    [InlineData("ts")]
    [InlineData("Timestamp")]
    [InlineData("TS")]
    public void TimestampKeyNamesAreAccepted(string key)
    {
        GatewayHealth h = Parse("{\"status\":\"ok\",\"" + key + "\":12345}");
        Assert.Equal("12345", h.Timestamp);
    }

    [Theory]
    [InlineData("times")]
    [InlineData("timestamps")]
    [InlineData("t")]
    [InlineData("date")]
    [InlineData("")]
    public void OtherKeysAreNotTimestamps(string key)
    {
        GatewayHealth h = Parse("{\"status\":\"ok\",\"" + key + "\":12345}");
        Assert.Null(h.Timestamp);
    }

    [Fact]
    public void TheFirstTimestampLikeKeyWins()
    {
        GatewayHealth h = Parse("{\"status\":\"ok\",\"time\":1,\"timestamp\":2,\"ts\":3}");
        Assert.Equal("1", h.Timestamp);
    }

    [Fact]
    public void StatusKeyIsCaseInsensitive()
    {
        Assert.Equal("up", Parse("{\"Status\":\"up\"}").Status);
        Assert.Equal("up", Parse("{\"STATUS\":\"up\"}").Status);
    }

    [Theory]
    [InlineData("{\"status\":200}", "200")]
    [InlineData("{\"status\":true}", "true")]
    [InlineData("{\"status\":false}", "false")]
    [InlineData("{\"status\":-1.5e+3}", "-1.5e+3")]
    [InlineData("{\"status\":0}", "0")]
    [InlineData("{\"status\":\"\"}", "")]
    [InlineData("{\"status\" : 7 }", "7")]
    public void NonStringStatusValuesKeepTheirLiteralText(string json, string expected)
    {
        Assert.Equal(expected, Parse(json).Status);
    }

    [Fact]
    public void NullTimestampIsTreatedAsMissingAndNullStatusIsRejected()
    {
        Assert.Null(Parse("{\"status\":\"ok\",\"timestamp\":null}").Timestamp);
        Assert.False(GatewayHealthParser.TryParse("{\"status\":null}", out GatewayHealth health));
        Assert.Null(health);
    }

    [Fact]
    public void NestedStructuresAreSkippedAndTheirKeysDoNotCount()
    {
        GatewayHealth h = Parse("{\"data\":{\"status\":\"inner\",\"ts\":9},\"list\":[1,2,{\"status\":\"x\"}],\"status\":\"outer\",\"timestamp\":5}");
        Assert.Equal("outer", h.Status);
        Assert.Equal("5", h.Timestamp);
        Assert.False(GatewayHealthParser.TryParse("{\"data\":{\"status\":\"inner\"}}", out _));
        Assert.False(GatewayHealthParser.TryParse("{\"data\":[{\"status\":\"inner\"}]}", out _));
    }

    [Fact]
    public void NestedStringsMayContainBracketsAndQuotes()
    {
        GatewayHealth h = Parse("{\"note\":{\"a\":\"}]\\\"{\",\"b\":[\"]\",\"}\"]},\"status\":\"ok\"}");
        Assert.Equal("ok", h.Status);
    }

    [Fact]
    public void StringEscapesAreDecoded()
    {
        Assert.Equal("a\"b", Parse("{\"status\":\"a\\\"b\"}").Status);
        Assert.Equal("a\\b", Parse("{\"status\":\"a\\\\b\"}").Status);
        Assert.Equal("a/b", Parse("{\"status\":\"a\\/b\"}").Status);
        Assert.Equal("\b\f\n\r\t", Parse("{\"status\":\"\\b\\f\\n\\r\\t\"}").Status);
        Assert.Equal("A", Parse("{\"status\":\"\\u0041\"}").Status);
        Assert.Equal("Aé", Parse("{\"status\":\"\\u0041\\u00e9\"}").Status);
        Assert.Equal("é", Parse("{\"status\":\"\\u00E9\"}").Status);
        Assert.Equal("\uffff", Parse("{\"status\":\"\\uFFFF\"}").Status);
        Assert.Equal("\u0000", Parse("{\"status\":\"\\u0000\"}").Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("\"status\"")]
    [InlineData("status")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{ }")]
    [InlineData("{\"status\"")]
    [InlineData("{\"status\":")]
    [InlineData("{\"status\":}")]
    [InlineData("{\"status\":\"ok\"")]
    [InlineData("{\"status\":\"ok\",}")]
    [InlineData("{\"status\":\"ok\",")]
    [InlineData("{\"status\":\"ok\"} extra")]
    [InlineData("{\"status\":\"ok\"}{}")]
    [InlineData("{status:\"ok\"}")]
    [InlineData("{'status':'ok'}")]
    [InlineData("{\"status\":ok}")]
    [InlineData("{\"status\" \"ok\"}")]
    [InlineData("{\"status\":\"ok\" \"time\":1}")]
    [InlineData("{\"status\":\"ok\",,\"time\":1}")]
    [InlineData("{,\"status\":\"ok\"}")]
    [InlineData("{\"status\":\"ok\",\"time\":1x}")]
    [InlineData("{\"status\":\"ok\",\"time\":}")]
    [InlineData("{\"status\":\"\\x\"}")]
    [InlineData("{\"status\":\"\\u12\"}")]
    [InlineData("{\"status\":\"\\uZZZZ\"}")]
    [InlineData("{\"status\":\"\\u12G4\"}")]
    [InlineData("{\"status\":\"unterminated}")]
    [InlineData("{\"status\":\"ok\\")]
    [InlineData("{\"status\":\"ok\",\"a\":[1,2}")]
    [InlineData("{\"status\":\"ok\",\"a\":{\"b\":1}")]
    [InlineData("{\"status\":\"ok\",\"a\":[1,\"x]}")]
    [InlineData("{\"time\":1}")]
    public void MalformedOrIncompleteDocumentsAreRejected(string json)
    {
        Assert.False(GatewayHealthParser.TryParse(json, out GatewayHealth health));
        Assert.Null(health);
    }

    [Fact]
    public void TruncationAtEveryPositionNeverThrowsAndNeverSucceedsEarly()
    {
        string full = "{\"status\":\"ok\",\"timestamp\":1730000000,\"data\":{\"a\":[1,\"x\\\"y\",{}]}}";
        Assert.True(GatewayHealthParser.TryParse(full, out _));
        for (int i = 0; i < full.Length; i++)
        {
            Assert.False(GatewayHealthParser.TryParse(full.Substring(0, i), out GatewayHealth h), "prefix length " + i);
            Assert.Null(h);
        }
    }

    [Fact]
    public void LongValuesAreKept()
    {
        string longText = new string('z', 10000);
        Assert.Equal(longText, Parse("{\"status\":\"" + longText + "\"}").Status);
    }
}
