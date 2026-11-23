using System.Text;
using Dnf.Data;

namespace Dnf.Data.Tests;

/// <summary>数字编号文字表的解析与"从可执行文件读"的测试。全部合成数据(文字用 SYNTH-… 一类的占位)。</summary>
public class ClientTextTableTests
{
    private static string Get(ClientTextTable table, int id)
    {
        Assert.True(table.TryGet(id, out string text), $"应当有编号 {id}");
        return text;
    }

    // ------------------------------------------------------------------ 解析

    [Fact]
    public void ParsesLinesWithLfCrlfAndMissingFinalNewline()
    {
        ClientTextTable table = ClientTextTable.Parse("1>SYNTH-A\r\n2>SYNTH-B\n3>SYNTH-C");
        Assert.Equal(3, table.Count);
        Assert.Equal("SYNTH-A", Get(table, 1));   // CRLF:行尾的 \r 去掉
        Assert.Equal("SYNTH-B", Get(table, 2));   // LF
        Assert.Equal("SYNTH-C", Get(table, 3));   // 最后一行没有换行
    }

    [Fact]
    public void OnlyOneTrailingCarriageReturnIsRemovedAndLoneCarriageReturnIsNotALineBreak()
    {
        ClientTextTable table = ClientTextTable.Parse("4>tail\r\r\n5>a\r6>b\n");
        Assert.Equal("tail\r", Get(table, 4));       // 只去掉紧挨在 \n 前面的那一个 \r
        Assert.Equal("a\r6>b", Get(table, 5));       // 单独的 \r 不是换行,这一行直到 \n 才结束
        Assert.False(table.TryGet(6, out _));
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        ClientTextTable table = ClientTextTable.Parse("// header\r\n\r\n1>x\n//2>not an entry\n\n   \n3>y\r\n//\n");
        Assert.Equal(2, table.Count);
        Assert.Equal("x", Get(table, 1));
        Assert.Equal("y", Get(table, 3));
        Assert.False(table.TryGet(2, out string missing));
        Assert.Null(missing);
    }

    [Fact]
    public void DoubleSlashInsideTheTextIsPartOfTheText()
    {
        ClientTextTable table = ClientTextTable.Parse("1>//still text\n2>a//b\n");
        Assert.Equal("//still text", Get(table, 1));
        Assert.Equal("a//b", Get(table, 2));
    }

    [Fact]
    public void DuplicateIdKeepsTheFirstOne()
    {
        // 实现的选择:遇到重复编号取第一个(客户端怎么处理重复没读过)。
        ClientTextTable table = ClientTextTable.Parse("5>first\n6>other\n5>second\n5>third\n");
        Assert.Equal(2, table.Count);
        Assert.Equal("first", Get(table, 5));
        Assert.Equal("other", Get(table, 6));
    }

    [Fact]
    public void DuplicateWithDifferentLeadingZerosIsTheSameId()
    {
        ClientTextTable table = ClientTextTable.Parse("007>first\n7>second\n0007>third\n");
        Assert.Equal(1, table.Count);
        Assert.Equal("first", Get(table, 7));
    }

    [Fact]
    public void TextMayContainTheSeparatorAndMayBeEmpty()
    {
        ClientTextTable table = ClientTextTable.Parse("6>a>b>c\n7>\n8>>\r\n9>\r\n");
        Assert.Equal("a>b>c", Get(table, 6));
        Assert.Equal(string.Empty, Get(table, 7));
        Assert.Equal(">", Get(table, 8));
        Assert.Equal(string.Empty, Get(table, 9));
        Assert.Equal(4, table.Count);
    }

    [Fact]
    public void BackslashNIsKeptAsTwoCharactersAndWhitespaceIsNotTrimmed()
    {
        ClientTextTable table = ClientTextTable.Parse("10>line1\\nline2\n11>  padded \\n \n12>\t\n");
        Assert.Equal("line1\\nline2", Get(table, 10));
        Assert.DoesNotContain('\n', Get(table, 10));
        Assert.Equal(12, Get(table, 10).Length);          // 12 = "line1" 5 + 反斜杠 1 + n 1 + "line2" 5
        Assert.Equal("  padded \\n ", Get(table, 11));
        Assert.Equal("\t", Get(table, 12));
    }

    [Fact]
    public void KeysThatAreNotPlainAsciiDigitsAreSkipped()
    {
        string text = string.Join("\n", new[]
        {
            "-5>negative",
            "+5>plus",
            "abc>word",
            "5a>mixed",
            ">nokey",
            " 12>leading space",
            "\t13>leading tab",
            "５>fullwidth digit",          // 全角数字 U+FF15
            "٣>arabic indic digit",        // U+0663
            "5 >space before separator",
            "5.5>decimal",
            "0x10>hex",
            "123",                         // 没有分隔符
            "15>ok",
        });
        ClientTextTable table = ClientTextTable.Parse(text);
        Assert.Equal(1, table.Count);
        Assert.Equal("ok", Get(table, 15));
    }

    [Fact]
    public void IdsBeyondTheIntRangeAreSkippedAndTheBoundariesAreKept()
    {
        // int.MaxValue = 2147483647 可以;+1 不行;很长的数字不行;前导零很多但值小的可以;0 可以。
        string text = "2147483647>max\n2147483648>over\n99999999999999999999>huge\n0>zero\n0000000000000000000012>padded\n2147483650>over2\n4294967297>wrap\n";
        ClientTextTable table = ClientTextTable.Parse(text);
        Assert.Equal(3, table.Count);
        Assert.Equal("max", Get(table, int.MaxValue));
        Assert.Equal("zero", Get(table, 0));
        Assert.Equal("padded", Get(table, 12));
        Assert.False(table.TryGet(int.MinValue, out _));
        Assert.False(table.TryGet(-1, out _));
        // 截成 32 位后会落到 int.MinValue + 2(2147483650)与 1(4294967297):这两个编号不能被"回绕"出来。
        Assert.False(table.TryGet(int.MinValue + 2, out _));
        Assert.False(table.TryGet(1, out _));
    }

    [Fact]
    public void LineOfDigitsWithoutSeparatorAtTheVeryEndIsSkipped()
    {
        // 最后一行只有数字、没有分隔符、也没有换行:跳过,而且不能越界读。
        ClientTextTable table = ClientTextTable.Parse("1>a\n123");
        Assert.Equal(1, table.Count);
        Assert.False(table.TryGet(123, out _));
        Assert.Equal(1, ClientTextTable.Parse("1>a\r\n123\r\n").Count);
        Assert.Equal(0, ClientTextTable.Parse("9").Count);
        Assert.Equal(0, ClientTextTable.Parse("9\r").Count);
    }

    [Fact]
    public void TextThatStartsWithLineBreaksKeepsEveryEntryAfterThem()
    {
        // 换行出现在下标 0:"下一行起点"是 1(紧跟在换行之后),不是文本末尾;算错的话后面所有条目都会被丢掉。
        ClientTextTable one = ClientTextTable.Parse("\n1>a\n2>b\n");
        Assert.Equal(2, one.Count);
        Assert.Equal("a", Get(one, 1));
        Assert.Equal("b", Get(one, 2));

        // 两个前导换行(下标 0 与下标 1 都是换行),后面没有换行结尾:一项。
        ClientTextTable two = ClientTextTable.Parse("\n\n1>a");
        Assert.Equal(1, two.Count);
        Assert.Equal("a", Get(two, 1));

        // 整段只有一个换行:没有条目(也不能死循环、不能抛异常)。
        Assert.Equal(0, ClientTextTable.Parse("\n").Count);

        // 同类写法:前导换行后面还有空行与 CRLF,条目照常全部读到。
        ClientTextTable mixed = ClientTextTable.Parse("\n\n\n3>c\n\n4>d\r\n\n5>e");
        Assert.Equal(3, mixed.Count);
        Assert.Equal("c", Get(mixed, 3));
        Assert.Equal("d", Get(mixed, 4));
        Assert.Equal("e", Get(mixed, 5));
    }

    [Theory]
    [InlineData("1>a\n/", 1)]
    [InlineData("1>a\n//", 1)]
    [InlineData("1>a\n/x", 1)]
    [InlineData("/", 0)]
    [InlineData("//", 0)]
    [InlineData("/x", 0)]
    public void ShortLinesStartingWithASlashAtTheVeryEndDoNotThrow(string text, int expectedCount)
    {
        // 最后一行没有换行结尾、又很短(1 到 2 个字符)且以 / 开头:判断"是不是 // 注释"时不能读到文本之外去。
        ClientTextTable table = null;
        Exception thrown = Record.Exception(() => table = ClientTextTable.Parse(text));
        Assert.Null(thrown);
        Assert.NotNull(table);
        Assert.Equal(expectedCount, table.Count);
        if (expectedCount == 1)
        {
            Assert.Equal("a", Get(table, 1));
        }
    }

    [Fact]
    public void EmptyAndNullInput()
    {
        Assert.Equal(0, ClientTextTable.Parse(string.Empty).Count);
        Assert.Equal(0, ClientTextTable.Parse("\r\n\r\n").Count);
        Assert.Throws<ArgumentNullException>(() => ClientTextTable.Parse(null));
        Assert.False(ClientTextTable.Parse("1>a").TryGet(2, out string text));
        Assert.Null(text);
    }

    [Fact]
    public void ChineseTextIsKeptVerbatim()
    {
        ClientTextTable table = ClientTextTable.Parse("7000>合成文字甲\n2000>合成文字乙\n");
        Assert.Equal("合成文字甲", Get(table, 7000));
        Assert.Equal("合成文字乙", Get(table, 2000));
    }

    [Fact]
    public void InterfaceConstantsAreTheDocumentedOnes()
    {
        Assert.Equal("FILE", ClientTextTable.ResourceTypeName);
        Assert.Equal(157, ClientTextTable.DefaultResourceId);
        Assert.Equal(0x9d, ClientTextTable.DefaultResourceId);
    }

    // ------------------------------------------------------------------ 从可执行文件读

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnf-clienttext-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] ExeWith(string typeName, int id, byte[] resource, bool pe32Plus = false, bool separate = false)
    {
        var spec = new SyntheticPe.Spec
        {
            Pe32Plus = pe32Plus,
            DataInSeparateSection = separate,
            Types = new[]
            {
                SyntheticPe.ResType.Named("OTHER", new SyntheticPe.Item(157, new SyntheticPe.Leaf(0, SyntheticPe.Ascii("not-the-table")))),
                SyntheticPe.ResType.Named(typeName, new SyntheticPe.Item(id, new SyntheticPe.Leaf(0x0804, resource))),
                SyntheticPe.ResType.Numbered(3, new SyntheticPe.Item(157, new SyntheticPe.Leaf(0, SyntheticPe.Ascii("not-the-table-either")))),
            },
        };
        return SyntheticPe.Build(spec).Bytes;
    }

    private static byte[] GoodExe(string text, bool pe32Plus = false, bool separate = false)
    {
        return ExeWith("FILE", 157, EmbeddedTextContainer.Encode(text), pe32Plus, separate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LoadsTheTableFromASyntheticExecutable(bool pe32Plus, bool separate)
    {
        using var temp = new TempDirectory();
        string path = temp.File("synthetic.exe");
        File.WriteAllBytes(path, GoodExe("// 合成\r\n1>SYNTH-A\r\n2000>SYNTH-NOTE\r\n3>a>b\\nc\r\n", pe32Plus, separate));
        Assert.True(ClientTextTable.TryLoadFromExecutable(path, out ClientTextTable table));
        Assert.NotNull(table);
        Assert.Equal(3, table.Count);
        Assert.Equal("SYNTH-A", Get(table, 1));
        Assert.Equal("SYNTH-NOTE", Get(table, 2000));
        Assert.Equal("a>b\\nc", Get(table, 3));
    }

    [Fact]
    public void LoadsATableWhoseTextStartsWithAByteOrderMark()
    {
        // 明文以 UTF-8 BOM 开头:容器解码时去掉,第一行照常算条目。
        using var temp = new TempDirectory();
        string path = temp.File("bom.exe");
        byte[] text = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("1>first\n2>second\n")).ToArray();
        File.WriteAllBytes(path, ExeWith("FILE", 157, EncodeBytes(text)));
        Assert.True(ClientTextTable.TryLoadFromExecutable(path, out ClientTextTable table));
        Assert.Equal("first", Get(table, 1));
        Assert.Equal(2, table.Count);
    }

    /// <summary>手工的"字节版加密":把任意明文字节装进 SC01 容器(Encode 只收字符串,这里要放带 BOM 的字节)。</summary>
    private static byte[] EncodeBytes(byte[] plain)
    {
        // 带 BOM 的字符串经 Encode 会把 U+FEFF 编成 EF BB BF,效果相同:先解成字符串再编。
        return EmbeddedTextContainer.Encode(new UTF8Encoding(false).GetString(plain));
    }

    [Fact]
    public void ConstantsSelectTheTypeAndIdThatAreUsed()
    {
        using var temp = new TempDirectory();
        string text = "1>SYNTH-A\n";
        byte[] container = EmbeddedTextContainer.Encode(text);

        // 同样的表放在别的资源号、别的类型名下:都读不到。
        foreach ((string type, int id) in new[] { ("FILE", 156), ("FILE", 158), ("FILE", 43), ("FILX", 157), ("FIL", 157), ("RCDATA", 157) })
        {
            string path = temp.File($"wrong-{type}-{id}.exe");
            File.WriteAllBytes(path, ExeWith(type, id, container));
            Assert.False(ClientTextTable.TryLoadFromExecutable(path, out ClientTextTable table), $"{type}/{id}");
            Assert.Null(table);
        }

        // 类型名不区分大小写。
        string lower = temp.File("lower.exe");
        File.WriteAllBytes(lower, ExeWith("file", 157, container));
        Assert.True(ClientTextTable.TryLoadFromExecutable(lower, out ClientTextTable found));
        Assert.Equal("SYNTH-A", Get(found, 1));
    }

    [Fact]
    public void FailurePathsReturnFalseWithoutThrowing()
    {
        using var temp = new TempDirectory();

        Assert.False(ClientTextTable.TryLoadFromExecutable(temp.File("missing.exe"), out ClientTextTable table));
        Assert.Null(table);
        Assert.False(ClientTextTable.TryLoadFromExecutable(null, out table));
        Assert.Null(table);
        Assert.False(ClientTextTable.TryLoadFromExecutable(string.Empty, out table));
        Assert.Null(table);
        Assert.False(ClientTextTable.TryLoadFromExecutable("bad\0name.exe", out table));
        Assert.Null(table);
        Assert.False(ClientTextTable.TryLoadFromExecutable(temp.Path, out table));   // 目录
        Assert.Null(table);

        string empty = temp.File("empty.exe");
        File.WriteAllBytes(empty, new byte[0]);
        Assert.False(ClientTextTable.TryLoadFromExecutable(empty, out table));
        Assert.Null(table);

        string notPe = temp.File("notpe.exe");
        File.WriteAllBytes(notPe, Encoding.ASCII.GetBytes("this is not an executable, just text\n1>SYNTH-A\n"));
        Assert.False(ClientTextTable.TryLoadFromExecutable(notPe, out table));
        Assert.Null(table);

        // 没有这条资源:PE 里只有别的类型。
        string noResource = temp.File("nores.exe");
        File.WriteAllBytes(noResource, ExeWith("FILX", 157, EmbeddedTextContainer.Encode("1>SYNTH-A\n")));
        Assert.False(ClientTextTable.TryLoadFromExecutable(noResource, out table));
        Assert.Null(table);

        // 资源是坏容器:魔数不对 / 太短 / P 过大。
        foreach (byte[] bad in new[]
        {
            Encoding.ASCII.GetBytes("1>SYNTH-A\n"),
            new byte[] { 0x53, 0x43, 0x30 },
            new byte[] { 0x53, 0x43, 0x30, 0x31, 0x09, 0, 0, 0, 0x56 },
        })
        {
            string badPath = temp.File("bad-" + bad.Length + ".exe");
            File.WriteAllBytes(badPath, ExeWith("FILE", 157, bad));
            Assert.False(ClientTextTable.TryLoadFromExecutable(badPath, out table), badPath);
            Assert.Null(table);
        }

        // 容器里 0 项:空文本、只有注释与空行、只有不合格的行。
        foreach (string zero in new[] { string.Empty, "// only a comment\r\n\r\n", "no number here\n-1>neg\n 2>space\nx>y\n" })
        {
            string zeroPath = temp.File("zero-" + zero.Length + ".exe");
            File.WriteAllBytes(zeroPath, GoodExe(zero));
            Assert.False(ClientTextTable.TryLoadFromExecutable(zeroPath, out table), zero);
            Assert.Null(table);
        }
    }

    [Fact]
    public void ReadsWhileAnotherHandleHasTheFileOpenForWriting()
    {
        // 以 FileShare.ReadWrite | FileShare.Delete 打开:别的句柄正拿着这个文件(客户端正在运行)也读得到。
        using var temp = new TempDirectory();
        string path = temp.File("busy.exe");
        File.WriteAllBytes(path, GoodExe("1>SYNTH-A\n"));
        using (var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.True(ClientTextTable.TryLoadFromExecutable(path, out ClientTextTable table));
            Assert.Equal("SYNTH-A", Get(table, 1));
            Assert.True(holder.CanWrite);
        }
    }
}
