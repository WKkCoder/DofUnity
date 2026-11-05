using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>线上格式 base64(nonce ‖ 密文 ‖ 标签) 的编解码;期望值来自规格的布局描述与 .NET 的 AesGcm(独立实现),不抄实现的输出。</summary>
public sealed class GatewayCodecTests
{
    private static readonly byte[] SynthKey = GatewayTestSupport.SynthKey(11);
    private static readonly byte[] SynthNonce = GatewayTestSupport.Pattern(12, 200, 3);

    /// <summary>独立于被测库的"服务端写法":用 AesGcm 产出 nonce ‖ 密文 ‖ 标签。</summary>
    private static byte[] ServerWire(byte[] key, byte[] nonce, byte[] plain)
    {
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using AesGcm gcm = new(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);
        return nonce.Concat(cipher).Concat(tag).ToArray();
    }

    [Fact]
    public void WireLayoutIsNonceThenCiphertextThenTagUnderStandardBase64()
    {
        string text = "login|Tester_01|900150983cd24fb0d6963f7d28e17f72|00:00:00:00:00:00";
        string encoded = GatewayCodec.Encode(SynthKey, text, new FixedNonceSource(SynthNonce));

        // 手算长度:12 + len + 16 字节,base64 每 3 字节 4 字符(向上取整)
        int binary = 12 + Encoding.UTF8.GetByteCount(text) + 16;
        Assert.Equal(4 * ((binary + 2) / 3), encoded.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9+/]+={0,2}$"), encoded);

        byte[] wire = Convert.FromBase64String(encoded);
        Assert.Equal(binary, wire.Length);
        Assert.Equal(SynthNonce, wire[..12]);

        // 用独立实现解开:密文区是 wire[12..^16],标签是最后 16 字节,AAD 为空
        byte[] plain = new byte[wire.Length - 28];
        using (AesGcm gcm = new(SynthKey, 16))
        {
            gcm.Decrypt(wire[..12], wire[12..^16], wire[^16..], plain);
        }
        Assert.Equal(text, Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void EncodingIsDeterministicForAFixedNonceAndEqualsTheIndependentConstruction()
    {
        byte[] plain = Encoding.UTF8.GetBytes("regedit|abcd|900150983cd24fb0d6963f7d28e17f72|");
        string expected = Convert.ToBase64String(ServerWire(SynthKey, SynthNonce, plain));
        Assert.Equal(expected, GatewayCodec.Encode(SynthKey, Encoding.UTF8.GetString(plain), new FixedNonceSource(SynthNonce)));
        Assert.Equal(expected, GatewayCodec.EncodeBytes(SynthKey, plain, new FixedNonceSource(SynthNonce)));
    }

    [Fact]
    public void DefaultRandomNonceMakesEveryEncodingDifferentYetDecodable()
    {
        string a = GatewayCodec.Encode(SynthKey, "same text");
        string b = GatewayCodec.Encode(SynthKey, "same text");
        Assert.NotEqual(a, b);
        Assert.True(GatewayCodec.TryDecode(SynthKey, a, out string pa));
        Assert.True(GatewayCodec.TryDecode(SynthKey, b, out string pb));
        Assert.Equal("same text", pa);
        Assert.Equal("same text", pb);
        // 随机 nonce 的前 12 字节应当不同(96 位碰撞概率可以忽略)
        Assert.NotEqual(Convert.FromBase64String(a)[..12], Convert.FromBase64String(b)[..12]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("success")]
    [InlineData("0|abc+def/ghi=")]
    [InlineData("中文与 emoji 😀 的往返")]
    public void RoundTripKeepsTheText(string text)
    {
        string encoded = GatewayCodec.Encode(SynthKey, text);
        Assert.True(GatewayCodec.TryDecode(SynthKey, encoded, out string decoded));
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void ServerStyleResponsesAreDecoded()
    {
        byte[] plain = Encoding.UTF8.GetBytes("0|sometoken+with/chars==");
        string response = Convert.ToBase64String(ServerWire(SynthKey, GatewayTestSupport.Pattern(12, 77), plain));
        Assert.True(GatewayCodec.TryDecode(SynthKey, response, out string text));
        Assert.Equal("0|sometoken+with/chars==", text);
    }

    [Fact]
    public void MinimumWireLengthIsTwentyEightBytes()
    {
        Assert.Equal(28, GatewayCodec.MinWireLength);
        Assert.Equal(12, GcmAes.NonceSize);
        Assert.Equal(16, GcmAes.TagSize);
        Assert.Equal(32, GcmAes.KeySize);
    }

    [Fact]
    public void MinimumLengthBoundaryIsNoncePlusTag()
    {
        // 恰好 28 字节 = 空明文:收;27 字节:拒(即使是合法密文被截掉一字节)
        string empty = GatewayCodec.Encode(SynthKey, string.Empty, new FixedNonceSource(SynthNonce));
        Assert.Equal(28, Convert.FromBase64String(empty).Length);
        Assert.True(GatewayCodec.TryDecode(SynthKey, empty, out string text));
        Assert.Equal(string.Empty, text);

        byte[] wire = Convert.FromBase64String(empty);
        Assert.False(GatewayCodec.TryDecode(SynthKey, Convert.ToBase64String(wire[..27]), out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, Convert.ToBase64String(new byte[12]), out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, Convert.ToBase64String(new byte[28]), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("****")]
    [InlineData("QUJDR")]
    [InlineData("QUJD=A==")]
    [InlineData("QQ===")]
    [InlineData("QU=D")]
    [InlineData("====")]
    public void MalformedBase64IsRejected(string text)
    {
        Assert.False(GatewayCodec.TryDecode(SynthKey, text, out string plain));
        Assert.Null(plain);
    }

    [Fact]
    public void MoreThanTwoPaddingCharactersAreRejectedEvenForAnAuthenticCiphertext()
    {
        // "ab" → 12+2+16=30 字节,不需要填充;"a" → 29 字节,需要一个 '='
        string unpadded = GatewayCodec.Encode(SynthKey, "ab", new FixedNonceSource(SynthNonce));
        string onePad = GatewayCodec.Encode(SynthKey, "a", new FixedNonceSource(SynthNonce));
        string twoPad = GatewayCodec.Encode(SynthKey, string.Empty, new FixedNonceSource(SynthNonce));
        Assert.DoesNotContain('=', unpadded);
        Assert.EndsWith("=", onePad);
        Assert.DoesNotContain("==", onePad);
        Assert.EndsWith("==", twoPad);

        Assert.True(GatewayCodec.TryDecode(SynthKey, unpadded, out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, unpadded + "===", out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, onePad + "==", out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, twoPad + "=", out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, twoPad + "==", out _));
    }

    [Fact]
    public void CharactersOutsideTheAlphabetAreRejectedEvenForAnAuthenticCiphertext()
    {
        string encoded = GatewayCodec.Encode(SynthKey, "ab", new FixedNonceSource(SynthNonce));
        Assert.True(GatewayCodec.TryDecode(SynthKey, encoded, out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, encoded.Insert(8, "*"), out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, encoded.Insert(8, "="), out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, encoded.Insert(8, "."), out _));
        Assert.False(GatewayCodec.TryDecode(SynthKey, encoded + "A", out _));
    }

    [Fact]
    public void AnyTamperedByteInNonceCiphertextOrTagIsRejected()
    {
        byte[] wire = Convert.FromBase64String(GatewayCodec.Encode(SynthKey, "login|Tester_01|x|y", new FixedNonceSource(SynthNonce)));
        for (int i = 0; i < wire.Length; i++)
        {
            byte[] bad = (byte[])wire.Clone();
            bad[i] ^= 0x04;
            Assert.False(GatewayCodec.TryDecode(SynthKey, Convert.ToBase64String(bad), out _), "flip at wire byte " + i);
        }
    }

    [Fact]
    public void WrongKeyIsRejected()
    {
        string encoded = GatewayCodec.Encode(SynthKey, "hello");
        Assert.False(GatewayCodec.TryDecode(GatewayTestSupport.SynthKey(12), encoded, out _));
    }

    [Fact]
    public void ParsingSideToleratesWhitespaceMissingPaddingAndTheUrlSafeAlphabet()
    {
        // 找一个编码后同时含 '+' 与 '/' 又需要填充的密文(固定序列的 nonce,确定性地搜索)
        string encoded = null;
        for (int i = 1; i < 5000 && encoded == null; i++)
        {
            byte[] nonce = new byte[12];
            BitConverter.GetBytes(i * 2654435761u).CopyTo(nonce, 0);
            BitConverter.GetBytes(i).CopyTo(nonce, 4);
            string candidate = GatewayCodec.Encode(SynthKey, "tolerance", new FixedNonceSource(nonce));
            if (candidate.Contains('+') && candidate.Contains('/') && candidate.EndsWith('='))
            {
                encoded = candidate;
            }
        }
        Assert.NotNull(encoded);

        // 尾部换行、中间被折行、前后空格
        Assert.True(GatewayCodec.TryDecode(SynthKey, encoded + "\r\n", out string a));
        Assert.True(GatewayCodec.TryDecode(SynthKey, " " + encoded.Insert(10, "\n").Insert(30, "\t") + " ", out string b));
        // 缺填充
        Assert.True(GatewayCodec.TryDecode(SynthKey, encoded.TrimEnd('='), out string c));
        // URL 安全字母表
        Assert.True(GatewayCodec.TryDecode(SynthKey, encoded.Replace('+', '-').Replace('/', '_'), out string d));
        Assert.Equal(new[] { "tolerance", "tolerance", "tolerance", "tolerance" }, new[] { a, b, c, d });
    }

    [Fact]
    public void AuthenticatedButNonUtf8PlaintextIsRefusedAsTextButAvailableAsBytes()
    {
        byte[] invalid = { 0xFF, 0xFE, 0x41 };
        string response = Convert.ToBase64String(ServerWire(SynthKey, SynthNonce, invalid));
        Assert.False(GatewayCodec.TryDecode(SynthKey, response, out string text));
        Assert.Null(text);
        Assert.True(GatewayCodec.TryDecodeBytes(SynthKey, response, out byte[] bytes));
        Assert.Equal(invalid, bytes);
    }

    [Fact]
    public void AnUnpairedSurrogateOnTheSendSideBecomesTheReplacementCharacterInsteadOfThrowing()
    {
        // 自由字段(例如 qq)里偶然混进孤立代理项,不该让登录流程抛异常:按 UTF-8 的替换规则编成 U+FFFD(EF BF BD)
        string wire = GatewayCodec.Encode(SynthKey, "a\uD800b", new FixedNonceSource(SynthNonce));
        Assert.True(GatewayCodec.TryDecodeBytes(SynthKey, wire, out byte[] bytes));
        Assert.Equal(new byte[] { 0x61, 0xEF, 0xBF, 0xBD, 0x62 }, bytes);
    }

    [Fact]
    public void NonceSourceReturningTheWrongSizeIsABug()
    {
        Assert.Throws<InvalidOperationException>(() => GatewayCodec.Encode(SynthKey, "x", new FixedNonceSource(new byte[11])));
        Assert.Throws<InvalidOperationException>(() => GatewayCodec.Encode(SynthKey, "x", new FixedNonceSource(new byte[13])));
        Assert.Throws<InvalidOperationException>(() => GatewayCodec.Encode(SynthKey, "x", new FixedNonceSource(null)));
    }

    [Fact]
    public void KeyAndPlaintextContractViolationsThrow()
    {
        Assert.Throws<ArgumentException>(() => GatewayCodec.Encode(new byte[31], "x"));
        Assert.Throws<ArgumentException>(() => GatewayCodec.TryDecode(new byte[33], "AAAA", out _));
        Assert.Throws<ArgumentException>(() => GatewayCodec.TryDecodeBytes(null, "AAAA", out _));
        Assert.Throws<ArgumentNullException>(() => GatewayCodec.Encode(SynthKey, null));
    }

    [Fact]
    public void RandomNonceSourceGivesFreshTwelveByteNonces()
    {
        HashSet<string> seen = new();
        for (int i = 0; i < 50; i++)
        {
            byte[] nonce = RandomNonceSource.Default.NextNonce();
            Assert.Equal(12, nonce.Length);
            Assert.True(seen.Add(Convert.ToHexString(nonce)));
        }
    }
}
