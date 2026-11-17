using System.Text;

namespace Dnf.Play.Tests;

/// <summary>
/// 角色名字节转文字(<see cref="CharacterNames"/>,规则取自客户端读名字的两个函数,charre 证据 03):合法的 UTF-8(1、2、3 字节序列)、
/// 长度 0 与过长、在第一个 0 字节处截断、4 字节序列与被截断的序列失败、本类比客户端更严的三处(畸形续字节、控制字符、过长名字)。
/// 名字全是合成的。
/// </summary>
public class CharacterNamesTests
{
    private static string Decode(byte[] bytes, out CharacterNameStatus status, string placeholder = "占位")
    {
        return CharacterNames.Decode(bytes, placeholder, out status);
    }

    private static string Decode(byte[] bytes, string placeholder = "占位")
    {
        return CharacterNames.Decode(bytes, placeholder, out _);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ------------------------------------------------------------------ 合法名字

    [Fact]
    public void AsciiNamesDecodeAsTheyAre()
    {
        Assert.Equal("Hero01", Decode(Utf8("Hero01"), out CharacterNameStatus status));
        Assert.Equal(CharacterNameStatus.Ok, status);
    }

    [Fact]
    public void TwoByteAndThreeByteSequencesDecode()
    {
        Assert.Equal("caf\u00e9", Decode(new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9 }));              // é:两字节
        Assert.Equal("测试角色", Decode(Utf8("测试角色"), out CharacterNameStatus status));               // 三字节
        Assert.Equal(CharacterNameStatus.Ok, status);
        Assert.Equal("A测é字B", Decode(Utf8("A测é字B")));                                           // 混排
    }

    [Fact]
    public void TheSequenceLengthBoundariesDecode()
    {
        Assert.Equal("~", Decode(new byte[] { 0x7E }));                                                  // 单字节的上界(0x7F 起是控制字符,见下面的用例)
        Assert.Equal("\u07ff", Decode(new byte[] { 0xDF, 0xBF }));                                       // 两字节的上界
        Assert.Equal("\u00a0", Decode(new byte[] { 0xC2, 0xA0 }));                                       // 两字节里第一个不是控制字符的
        Assert.Equal("\u0800", Decode(new byte[] { 0xE0, 0xA0, 0x80 }));                                  // 三字节的下界
        Assert.Equal("\uffff", Decode(new byte[] { 0xEF, 0xBF, 0xBF }));                                  // 三字节的上界
    }

    [Fact]
    public void ThirtyBytesIsTheLongestReadableNameAndThirtyOneIsNot()
    {
        string tenCjk = "一二三四五六七八九十";                                                         // 10 × 3 = 30 字节
        Assert.Equal(30, Utf8(tenCjk).Length);
        Assert.Equal(tenCjk, Decode(Utf8(tenCjk), out CharacterNameStatus ok));
        Assert.Equal(CharacterNameStatus.Ok, ok);

        byte[] thirtyOne = Utf8(tenCjk + "a");
        Assert.Equal(31, thirtyOne.Length);
        Assert.Equal("占位", Decode(thirtyOne, out CharacterNameStatus tooLong));
        Assert.Equal(CharacterNameStatus.TooLong, tooLong);
        Assert.Equal(CharacterNames.MaxBytes, 30);
    }

    // ------------------------------------------------------------------ 长度 0 与 0 字节

    [Fact]
    public void ZeroLengthAndNullUseThePlaceholder()
    {
        Assert.Equal("占位", Decode(new byte[0], out CharacterNameStatus empty));
        Assert.Equal(CharacterNameStatus.Empty, empty);
        Assert.Equal("占位", Decode(null, out CharacterNameStatus none));
        Assert.Equal(CharacterNameStatus.Empty, none);
        Assert.Equal(string.Empty, CharacterNames.Decode(new byte[0], null, out _));                    // 占位为 null 当空串
    }

    [Fact]
    public void TheNameStopsAtTheFirstZeroByteLikeACString()
    {
        Assert.Equal("AB", Decode(new byte[] { 0x41, 0x42, 0, 0x43, 0x44 }, out CharacterNameStatus status));
        Assert.Equal(CharacterNameStatus.Ok, status);
        Assert.Equal(string.Empty, Decode(new byte[] { 0, 0x41 }, out CharacterNameStatus first));     // 第一个字节就是 0:空名字,不是占位
        Assert.Equal(CharacterNameStatus.Ok, first);
        Assert.Equal("测", Decode(new byte[] { 0xE6, 0xB5, 0x8B, 0, 0xE8 }));                            // 0 字节之后的残缺序列不看
    }

    [Fact]
    public void ASequenceCutByTheZeroByteFails()
    {
        // 客户端先数 strlen 再转换,序列需要的字节必须在 0 字节之前
        Assert.Equal(string.Empty, Decode(new byte[] { 0xE6, 0xB5, 0, 0x8B }, out CharacterNameStatus status));
        Assert.Equal(CharacterNameStatus.Invalid, status);
    }

    // ------------------------------------------------------------------ 失败

    [Fact]
    public void FourByteSequencesFailTheWholeNameBecomesEmpty()
    {
        byte[] emoji = { 0x41, 0xF0, 0x9F, 0x98, 0x80, 0x42 };                                           // A + 😀 + B
        Assert.Equal(string.Empty, Decode(emoji, out CharacterNameStatus status));
        Assert.Equal(CharacterNameStatus.Invalid, status);
        Assert.Equal(string.Empty, Decode(new byte[] { 0xF8, 0x88, 0x80, 0x80, 0x80 }));                 // 更长的旧式序列也一样
        Assert.Equal(string.Empty, Decode(new byte[] { 0xFF }));
    }

    [Theory]
    [InlineData(0xA0, 0x80)]                                                                             // 只有首字节 + 两个续字节:当三字节序列能解出 U+0800
    [InlineData(0xBF, 0xBF)]                                                                             // 同理能解出 U+0FFF
    public void AFourByteLeadFailsEvenWhenOnlyThreeBytesFollowItsFirstTwo(int second, int third)
    {
        // 首字节 0xF0 以上本身就让转换失败:不管后面跟几个字节、凑起来像不像一个合法的三字节序列
        Assert.Equal(string.Empty, Decode(new byte[] { 0xF0, (byte)second, (byte)third }, out CharacterNameStatus status));
        Assert.Equal(CharacterNameStatus.Invalid, status);
        Assert.Equal(string.Empty, Decode(new byte[] { 0x41, 0xF0, (byte)second, (byte)third }, out status));
        Assert.Equal(CharacterNameStatus.Invalid, status);
    }

    [Fact]
    public void TruncatedSequencesFail()
    {
        Assert.Equal(string.Empty, Decode(new byte[] { 0xC3 }, out CharacterNameStatus two));            // 两字节序列缺续字节
        Assert.Equal(CharacterNameStatus.Invalid, two);
        Assert.Equal(string.Empty, Decode(new byte[] { 0x41, 0xE6, 0xB5 }, out CharacterNameStatus three));   // 三字节序列缺一个
        Assert.Equal(CharacterNameStatus.Invalid, three);
        Assert.Equal(string.Empty, Decode(new byte[] { 0xE6 }));
    }

    [Fact]
    public void StrayContinuationBytesAndMalformedSequencesFail()
    {
        Assert.Equal(string.Empty, Decode(new byte[] { 0x80 }));                                         // 续字节当首字节
        Assert.Equal(string.Empty, Decode(new byte[] { 0xBF, 0x41 }));
        Assert.Equal(string.Empty, Decode(new byte[] { 0xC0, 0x80 }));                                   // 过长编码的两字节
        Assert.Equal(string.Empty, Decode(new byte[] { 0xC1, 0xBF }));
        Assert.Equal(string.Empty, Decode(new byte[] { 0xE0, 0x80, 0x80 }));                             // 过长编码的三字节
        Assert.Equal(string.Empty, Decode(new byte[] { 0xE0, 0x9F, 0xBF }));
        Assert.Equal(string.Empty, Decode(new byte[] { 0xED, 0xA0, 0x80 }));                             // 代理项
        Assert.Equal(string.Empty, Decode(new byte[] { 0xED, 0xBF, 0xBF }));
        Assert.Equal(string.Empty, Decode(new byte[] { 0xE6, 0x41, 0x41 }));                             // 续字节不是 10xxxxxx
        Assert.Equal(string.Empty, Decode(new byte[] { 0xC3, 0x41 }));
        Assert.Equal(string.Empty, Decode(new byte[] { 0xE6, 0xB5, 0x41 }));
    }

    [Fact]
    public void AnInvalidNameDoesNotUseThePlaceholder()
    {
        // 客户端转换失败时名字是空的,不是默认名(默认名只给长度为 0 的)
        Assert.Equal(string.Empty, Decode(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, out CharacterNameStatus status, "占位"));
        Assert.Equal(CharacterNameStatus.Invalid, status);
    }

    // ------------------------------------------------------------------ 控制字符

    [Fact]
    public void ControlCharactersBecomeQuestionMarksSoTheyCannotBreakTheLine()
    {
        Assert.Equal("A?B", Decode(new byte[] { 0x41, 0x0A, 0x42 }));                                    // 换行
        Assert.Equal("?", Decode(new byte[] { 0x01 }));
        Assert.Equal("?", Decode(new byte[] { 0x7F }));                                                  // DEL
        Assert.Equal("?", Decode(new byte[] { 0xC2, 0x85 }));                                            // C1 的 NEL
        Assert.Equal("?", Decode(new byte[] { 0xC2, 0x9F }));
        Assert.Equal("?", Decode(new byte[] { 0xE2, 0x80, 0xA8 }));                                      // 行分隔符 U+2028
        Assert.Equal("?", Decode(new byte[] { 0xE2, 0x80, 0xA9 }));                                      // 段分隔符 U+2029
        Assert.Equal("\u00a0", Decode(new byte[] { 0xC2, 0xA0 }));                                       // 不换行空格不是控制字符
        Assert.Equal(" ", Decode(new byte[] { 0x20 }));
    }

    // ------------------------------------------------------------------ 输入类型

    [Fact]
    public void ReadOnlyListsAreAccepted()
    {
        IReadOnlyList<byte> list = new List<byte>(Utf8("名字")).AsReadOnly();
        Assert.Equal("名字", CharacterNames.Decode(list, "占位", out _));
    }

    [Fact]
    public void TheEntryNameBytesAreDecodedAsTheyAreStored()
    {
        var entry = RosterKit.EntryBytes(Utf8("勇者"));
        Assert.Equal("勇者", CharacterNames.Decode(entry.NameBytes, "占位", out _));
    }
}
