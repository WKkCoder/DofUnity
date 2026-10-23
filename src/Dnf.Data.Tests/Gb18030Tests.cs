using System.Text;
using Dnf.Data;

namespace Dnf.Data.Tests;

public class Gb18030Tests
{
    private static Encoding Reference()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936);
    }

    [Fact]
    public void AsciiPassesThrough()
    {
        Assert.Equal("Hello, 123!\r\n", Gb18030.Decode(Encoding.ASCII.GetBytes("Hello, 123!\r\n")));
        Assert.Equal(string.Empty, Gb18030.Decode(new byte[0]));
    }

    [Fact]
    public void TwoByteVectorsDecode()
    {
        // 公开的编码测试向量:中文、你好
        Assert.Equal("中文", Gb18030.Decode(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }));
        Assert.Equal("你好", Gb18030.Decode(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }));
        Assert.Equal("A中B", Gb18030.Decode(new byte[] { 0x41, 0xD6, 0xD0, 0x42 }));
    }

    [Fact]
    public void OffsetOverloadDecodesOnlyTheRange()
    {
        byte[] data = { 0x58, 0xD6, 0xD0, 0x59 };
        Assert.Equal("中", Gb18030.Decode(data, 1, 2));
        Assert.Equal("X", Gb18030.Decode(data, 0, 1));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Gb18030.Decode((byte[])null));
        Assert.Throws<ArgumentNullException>(() => Gb18030.Decode(null, 0, 0));
    }

    [Fact]
    public void InvalidBytesBecomeReplacementWithoutThrowing()
    {
        // 末尾孤立的首字节
        Assert.Equal("A\uFFFD", Gb18030.Decode(new byte[] { 0x41, 0xD6 }));
        // 0x80 与 0xFF 不是合法首字节
        Assert.Equal("\uFFFD\uFFFD", Gb18030.Decode(new byte[] { 0x80, 0xFF }));
        // 次字节不合法:首字节变成 U+FFFD,次字节按自己的意思再解(空格保留)
        Assert.Equal("\uFFFD ", Gb18030.Decode(new byte[] { 0xD6, 0x20 }));
        // 次字节 0x7F 不在合法范围
        Assert.Equal("\uFFFD\u007F", Gb18030.Decode(new byte[] { 0xD6, 0x7F }));
        // 表里没有定义的位置(0xA2A1 前后这一带有空位)不应该抛异常
        Assert.Equal(1, Gb18030.Decode(new byte[] { 0xFE, 0xFE }).Length);
    }

    [Fact]
    public void FourByteSequenceIsConsumedAsOneReplacement()
    {
        Assert.Equal("\uFFFD", Gb18030.Decode(new byte[] { 0x81, 0x30, 0x81, 0x30 }));
        Assert.Equal("a\uFFFDb", Gb18030.Decode(new byte[] { 0x61, 0x81, 0x30, 0x81, 0x30, 0x62 }));
        // 四字节序列被截断:不吃过头
        Assert.Equal("\uFFFD0\uFFFD", Gb18030.Decode(new byte[] { 0x81, 0x30, 0x81 }));
    }

    [Fact]
    public void TableAgreesWithTheRuntimeCodecEverywhereBothDefineASingleCharacter()
    {
        Encoding reference = Reference();
        int compared = 0;
        var mismatches = new List<string>();
        for (int lead = 0x81; lead <= 0xFE; lead++)
        {
            for (int trail = 0x40; trail <= 0xFE; trail++)
            {
                if (trail == 0x7F)
                {
                    continue;
                }
                byte[] pair = { (byte)lead, (byte)trail };
                string mine = Gb18030.Decode(pair);
                string theirs = reference.GetString(pair);
                bool mineDefined = mine.Length == 1 && mine[0] != '\uFFFD';
                bool theirsDefined = theirs.Length == 1 && theirs[0] != '\uFFFD';
                if (!mineDefined || !theirsDefined)
                {
                    continue;
                }
                // 私用区(用户自定义字符区)的映射各家实现不同,不比
                if (IsPrivateUse(mine[0]) || IsPrivateUse(theirs[0]))
                {
                    continue;
                }
                compared++;
                if (mine != theirs)
                {
                    mismatches.Add(lead.ToString("X2") + trail.ToString("X2") + ":" + ((int)mine[0]).ToString("X4") + "/" + ((int)theirs[0]).ToString("X4"));
                }
            }
        }
        Assert.True(compared > 21000, "能比较的位置太少:" + compared);
        Assert.True(mismatches.Count == 0, "不一致的位置:" + string.Join(" ", mismatches.Take(20)));
    }

    [Fact]
    public void CommonCjkCharactersAreAllDefined()
    {
        // GB2312 一级汉字区 0xB0A1 起,每个首字节 94 个字符,全部有定义
        for (int lead = 0xB0; lead <= 0xD6; lead++)
        {
            for (int trail = 0xA1; trail <= 0xFE; trail++)
            {
                string s = Gb18030.Decode(new byte[] { (byte)lead, (byte)trail });
                Assert.Equal(1, s.Length);
                Assert.NotEqual('\uFFFD', s[0]);
            }
        }
    }

    private static bool IsPrivateUse(char c)
    {
        return c >= '\uE000' && c <= '\uF8FF';
    }
}
