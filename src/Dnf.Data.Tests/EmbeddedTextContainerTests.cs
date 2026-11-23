using System.Text;
using Dnf.Data;

namespace Dnf.Data.Tests;

/// <summary>"SC01" 容器的测试:手算向量(注释里写出每个字的异或推导)、往返、各种坏容器。全部合成数据。</summary>
public class EmbeddedTextContainerTests
{
    // 记号(下面的推导都用它):密钥初值 K0 = 0x5819AF17,按小端字节写是 17 AF 19 58。
    // 解密是链式异或:p[i] = c[i] ^ c[i-1](按小端 32 位字,c[-1] = K0);加密反过来:c[i] = p[i] ^ c[i-1]。
    // 头部:'S' 'C' '0' '1' = 53 43 30 31,后面是小端 u32 P。

    private static byte[] Hex(string hex)
    {
        string[] parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bytes[i] = Convert.ToByte(parts[i], 16);
        }
        return bytes;
    }

    private static string Decode(byte[] container)
    {
        Assert.True(EmbeddedTextContainer.TryDecode(container, out string text));
        return text;
    }

    // ------------------------------------------------------------------ 手算向量(断言密文字节)

    [Fact]
    public void EncodesAFullWordByHand()
    {
        // 文本 "ABCD":明文字节 41 42 43 44(小端字 0x44434241),P = 4,体 = 1 个字。
        //   c0 = p0 ^ K0:41^17 = 56,42^AF = ED,43^19 = 5A,44^58 = 1C  → 体 56 ED 5A 1C。
        byte[] expected = Hex("53 43 30 31  04 00 00 00  56 ED 5A 1C");
        Assert.Equal(expected, EmbeddedTextContainer.Encode("ABCD"));
        Assert.Equal("ABCD", Decode(expected));
    }

    [Fact]
    public void KeyOfTheSecondWordIsThePreviousCipherWordNotThePlainWord()
    {
        // 文本 "ABCDEFGH":P = 8,两个字。
        //   第一个字同上:c0 = 56 ED 5A 1C。
        //   第二个字明文 45 46 47 48,它的密钥是上一个<密文>字 c0 = 56 ED 5A 1C(不是上一个明文字 41 42 43 44):
        //     45^56 = 13,46^ED = AB,47^5A = 1D,48^1C = 54  → 13 AB 1D 54。
        //   (若误用上一个明文字当密钥,第二个字会是 45^41 = 04,46^42 = 04,47^43 = 04,48^44 = 0C → 04 04 04 0C,与下面的期望不同。)
        byte[] expected = Hex("53 43 30 31  08 00 00 00  56 ED 5A 1C  13 AB 1D 54");
        Assert.Equal(expected, EmbeddedTextContainer.Encode("ABCDEFGH"));
        Assert.Equal("ABCDEFGH", Decode(expected));
    }

    [Fact]
    public void PartialLastWordIsZeroPaddedAndOnlyThePrefixIsKept()
    {
        // 文本 "ABCDEF":P = 6,体 6 字节(2 个字,第二个字只有 2 个字节)。
        //   第一个字:56 ED 5A 1C(同上)。
        //   第二个字明文 45 46(补零成 45 46 00 00),密钥 56 ED 5A 1C:45^56 = 13,46^ED = AB,体里只存这两个字节 → 13 AB。
        //   解码时第二个字读成 13 AB 00 00:13^56 = 45,AB^ED = 46(后两个字节是垃圾,但输出只取前 P = 6 字节)。
        byte[] expected = Hex("53 43 30 31  06 00 00 00  56 ED 5A 1C  13 AB");
        Assert.Equal(expected, EmbeddedTextContainer.Encode("ABCDEF"));
        Assert.Equal("ABCDEF", Decode(expected));
    }

    [Fact]
    public void ShortTextsWithEveryRemainderByHand()
    {
        // P = 1:"A" = 41:41^17 = 56。
        Assert.Equal(Hex("53 43 30 31  01 00 00 00  56"), EmbeddedTextContainer.Encode("A"));
        // P = 2:"AB" = 41 42:41^17 = 56,42^AF = ED。
        Assert.Equal(Hex("53 43 30 31  02 00 00 00  56 ED"), EmbeddedTextContainer.Encode("AB"));
        // P = 3:"ABC" = 41 42 43:56,ED,43^19 = 5A。
        Assert.Equal(Hex("53 43 30 31  03 00 00 00  56 ED 5A"), EmbeddedTextContainer.Encode("ABC"));
        // P = 0:没有体。
        Assert.Equal(Hex("53 43 30 31  00 00 00 00"), EmbeddedTextContainer.Encode(string.Empty));
        Assert.Equal("A", Decode(Hex("53 43 30 31  01 00 00 00  56")));
        Assert.Equal("AB", Decode(Hex("53 43 30 31  02 00 00 00  56 ED")));
        Assert.Equal("ABC", Decode(Hex("53 43 30 31  03 00 00 00  56 ED 5A")));
        Assert.Equal(string.Empty, Decode(Hex("53 43 30 31  00 00 00 00")));
    }

    [Fact]
    public void MultiByteUtf8ByHand()
    {
        // "中" 的 UTF-8 是 E4 B8 AD,P = 3:E4^17 = F3,B8^AF = 17,AD^19 = B4。
        byte[] expected = Hex("53 43 30 31  03 00 00 00  F3 17 B4");
        Assert.Equal(expected, EmbeddedTextContainer.Encode("中"));
        Assert.Equal("中", Decode(expected));
    }

    [Fact]
    public void Utf8ByteOrderMarkAtTheStartIsRemoved()
    {
        // 明文 EF BB BF 41 42(BOM + "AB"),P = 5。
        //   第一个字 EF BB BF 41:EF^17 = F8,BB^AF = 14,BF^19 = A6,41^58 = 19  → F8 14 A6 19。
        //   第二个字 42(补零),密钥是上一个密文字 F8 14 A6 19:42^F8 = BA  → BA。
        byte[] container = Hex("53 43 30 31  05 00 00 00  F8 14 A6 19  BA");
        Assert.Equal("AB", Decode(container));
        Assert.True(EmbeddedTextContainer.TryDecodeBytes(container, out byte[] plain));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x41, 0x42 }, plain);   // 字节层面不去 BOM,文本层面才去
    }

    [Fact]
    public void ByteOrderMarkOnlyGivesEmptyTextAndMiddleMarkIsKept()
    {
        // 明文 EF BB BF,P = 3:EF^17 = F8,BB^AF = 14,BF^19 = A6。
        Assert.Equal(string.Empty, Decode(Hex("53 43 30 31  03 00 00 00  F8 14 A6")));
        // BOM 不在开头就不去掉:往返后原样。
        Assert.Equal("A\uFEFFB", Decode(EmbeddedTextContainer.Encode("A\uFEFFB")));
        // 开头的是两个字节的前缀(EF BB 41),不是 BOM:不去掉,而且是坏字节,换成替换字符。
        // 明文 EF BB 41,P = 3:EF^17 = F8,BB^AF = 14,41^19 = 58。
        Assert.Equal("\uFFFDA", Decode(Hex("53 43 30 31  03 00 00 00  F8 14 58")));
    }

    [Theory]
    [InlineData("53 43 30 31  02 00 00 00  F8 14", "EF BB")]   // P = 2:明文 EF BB:EF^17 = F8,BB^AF = 14。
    [InlineData("53 43 30 31  01 00 00 00  F8", "EF")]          // P = 1:明文 EF:EF^17 = F8。
    public void PlainTextThatIsOnlyAPrefixOfTheByteOrderMarkIsNotTakenForOneAndDoesNotThrow(string containerHex, string plainHex)
    {
        // 明文只有 BOM 的前一两个字节(P = 2 或 1):还没有第三个字节可看,不能越界去读它;也不是 BOM,不能被去掉——坏字节照常换成替换字符。
        // 不断言替换字符的个数(不同运行时对残缺序列合成几个 U+FFFD 可以不同),只断言:成功、不抛异常、不为空、里面有 U+FFFD。
        // (容器是手算的十六进制:Encode 只收字符串,而这两个明文不是合法的 UTF-8,经字符串中转会变成别的字节。)
        byte[] container = Hex(containerHex);
        Assert.True(EmbeddedTextContainer.TryDecodeBytes(container, out byte[] plain));
        Assert.Equal(Hex(plainHex), plain);                                   // 字节层面原样

        bool ok = false;
        string text = null;
        Exception thrown = Record.Exception(() => ok = EmbeddedTextContainer.TryDecode(container, out text));
        Assert.Null(thrown);
        Assert.True(ok);
        Assert.NotNull(text);
        Assert.NotEmpty(text);                                                // 没有被当作 BOM 整个去掉
        Assert.Contains('\uFFFD', text);                                      // 坏字节变成了替换字符
        Assert.DoesNotContain('\uFEFF', text);
    }

    [Fact]
    public void InvalidUtf8BytesBecomeReplacementCharactersWithoutThrowing()
    {
        // 明文 FF FE 41,P = 3:FF^17 = E8,FE^AF = 51,41^19 = 58。FF、FE 都不是合法的 UTF-8 起始字节。
        Assert.Equal("\uFFFD\uFFFDA", Decode(Hex("53 43 30 31  03 00 00 00  E8 51 58")));
    }

    // ------------------------------------------------------------------ 往返

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("abcde")]
    [InlineData("abcdef")]
    [InlineData("abcdefg")]
    [InlineData("abcdefgh")]
    [InlineData("abcdefghi")]
    [InlineData("中")]
    [InlineData("中文")]
    [InlineData("中文字")]
    [InlineData("a中")]
    [InlineData("ab中")]
    [InlineData("abc中")]
    [InlineData("abcd中")]
    [InlineData("100>SYNTH>A>B\\n第二行\\n\\n")]
    [InlineData("1>甲\r\n2>乙\r\n// 注释\r\n\r\n3>a>b\r\n")]
    [InlineData("😀 emoji 与 \\n 记号")]
    public void EncodeThenDecodeRoundTrips(string text)
    {
        byte[] container = EmbeddedTextContainer.Encode(text);
        Assert.Equal(8 + Encoding.UTF8.GetByteCount(text), container.Length);
        Assert.Equal(text, Decode(container));
    }

    [Fact]
    public void LongRandomTextsRoundTripForEveryRemainder()
    {
        var random = new Random(7001);
        const string alphabet = "abcXYZ019>\\n/ \r\n中文字符é";
        for (int length = 0; length < 64; length++)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }
            string text = new string(chars);
            Assert.Equal(text, Decode(EmbeddedTextContainer.Encode(text)));
        }
        var big = new char[20000];
        for (int i = 0; i < big.Length; i++)
        {
            big[i] = alphabet[random.Next(alphabet.Length)];
        }
        string bigText = new string(big);
        Assert.Equal(bigText, Decode(EmbeddedTextContainer.Encode(bigText)));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void LargePlainLengthsUseEveryByteOfTheDeclaredLength(int length)
    {
        // P = 100000 = 0x000186A0,小端字节 A0 86 01 00:第 2、第 3 个字节都非零,字节序或移位错了会读成别的长度。
        // P = 255、256、65535、65536 分别是各个字节进位的边界。
        string text = new string('x', length);
        byte[] container = EmbeddedTextContainer.Encode(text);
        Assert.Equal((byte)length, container[4]);
        Assert.Equal((byte)(length >> 8), container[5]);
        Assert.Equal((byte)(length >> 16), container[6]);
        Assert.Equal(0, container[7]);
        Assert.Equal(text, Decode(container));
    }

    [Fact]
    public void DeclaredLengthOfSixteenMebibytesUsesTheFourthLengthByte()
    {
        // P = 0x01000000(16 MiB),小端字节 00 00 00 01:只有第 4 个字节非零。体正好 16 MiB 全零(密文全零,明文就是链上的密钥,内容不重要,只看长度)。
        const int size = 16 * 1024 * 1024;
        var container = new byte[8 + size];
        container[0] = 0x53;
        container[1] = 0x43;
        container[2] = 0x30;
        container[3] = 0x31;
        container[7] = 0x01;
        Assert.True(EmbeddedTextContainer.TryDecodeBytes(container, out byte[] plain));
        Assert.Equal(size, plain.Length);
        // 少一个字节的体:P 超过体长,拒绝。
        var shorter = new byte[8 + size - 1];
        Array.Copy(container, shorter, shorter.Length);
        Assert.False(EmbeddedTextContainer.TryDecodeBytes(shorter, out _));
    }

    [Fact]
    public void EncodeWritesNoByteOrderMarkAndTheDeclaredLengthIsThePlainLength()
    {
        byte[] container = EmbeddedTextContainer.Encode("中a");
        // 明文 E4 B8 AD 61:P = 4,体长 4。
        Assert.Equal(12, container.Length);
        Assert.Equal(new byte[] { 0x53, 0x43, 0x30, 0x31, 4, 0, 0, 0 }, container[..8]);
        Assert.True(EmbeddedTextContainer.TryDecodeBytes(container, out byte[] plain));
        Assert.Equal(new byte[] { 0xE4, 0xB8, 0xAD, 0x61 }, plain);
    }

    // ------------------------------------------------------------------ 坏容器

    [Theory]
    [InlineData("53 43 30 32")]   // SC02
    [InlineData("58 43 30 31")]   // XC01
    [InlineData("53 44 30 31")]   // SD01
    [InlineData("53 43 4F 31")]   // SCO1(字母 O 不是数字 0)
    [InlineData("73 63 30 31")]   // sc01(魔数区分大小写)
    [InlineData("00 00 00 00")]
    public void WrongMagicIsRejected(string magic)
    {
        byte[] container = Hex(magic + "  01 00 00 00  56");
        Assert.False(EmbeddedTextContainer.TryDecode(container, out string text));
        Assert.Null(text);
        Assert.False(EmbeddedTextContainer.TryDecodeBytes(container, out byte[] plain));
        Assert.Null(plain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("53")]
    [InlineData("53 43 30 31")]
    [InlineData("53 43 30 31  01")]
    [InlineData("53 43 30 31  00 00 00")]
    public void ContainerShorterThanTheHeaderIsRejected(string hex)
    {
        Assert.False(EmbeddedTextContainer.TryDecode(Hex(hex), out string text));
        Assert.Null(text);
    }

    [Fact]
    public void HeaderOnlyContainerWithZeroLengthIsAnEmptyText()
    {
        // 刚好 8 字节、P = 0:合法的空文本(长度检查是"不足 8",8 本身可以)。
        Assert.Equal(string.Empty, Decode(Hex("53 43 30 31  00 00 00 00")));
    }

    [Fact]
    public void DeclaredLengthLargerThanTheBodyIsRejected()
    {
        // 体 4 字节:P = 4 可以,P = 5 不行(多 1);P = 0xFFFFFFFF、0x80000000、0x100 也不行(高位字节要按无符号数读)。
        Assert.Equal("ABCD", Decode(Hex("53 43 30 31  04 00 00 00  56 ED 5A 1C")));
        foreach (string length in new[] { "05 00 00 00", "FF FF FF FF", "00 00 00 80", "00 01 00 00" })
        {
            byte[] container = Hex("53 43 30 31  " + length + "  56 ED 5A 1C");
            Assert.False(EmbeddedTextContainer.TryDecode(container, out string text), length);
            Assert.Null(text);
        }
    }

    [Fact]
    public void ZeroLengthWithABodyIsAnEmptyTextAndExtraBodyBytesAreIgnored()
    {
        // P = 0 且体里有 4 个字节:合法,空文本。
        Assert.Equal(string.Empty, Decode(Hex("53 43 30 31  00 00 00 00  56 ED 5A 1C")));
        // P = 2、体 4 字节:只取前 2 个明文字节("AB"),后面的忽略。
        Assert.Equal("AB", Decode(Hex("53 43 30 31  02 00 00 00  56 ED 5A 1C")));
        // P = 6、体 8 字节:第二个字是完整的字(13 AB 99 99,不补零),只取 2 字节 45 46 → "ABCDEF"。
        Assert.Equal("ABCDEF", Decode(Hex("53 43 30 31  06 00 00 00  56 ED 5A 1C  13 AB 99 99")));
    }

    [Fact]
    public void NullContainerThrows()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddedTextContainer.TryDecode(null, out _));
        Assert.Throws<ArgumentNullException>(() => EmbeddedTextContainer.TryDecodeBytes(null, out _));
        Assert.Throws<ArgumentNullException>(() => EmbeddedTextContainer.Encode(null));
    }

    [Fact]
    public void InitialKeyConstantIsTheDocumentedValue()
    {
        Assert.Equal(0x5819AF17u, EmbeddedTextContainer.InitialKey);
        Assert.Equal(8, EmbeddedTextContainer.HeaderSize);
    }
}
