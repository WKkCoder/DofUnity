using System.Text;
using Dnf.Data;

namespace Dnf.Data.Tests;

public class StrBookTests
{
    private static byte[] Gbk(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936).GetBytes(text);
    }

    [Fact]
    public void ParsesKeyValueLinesWithCrlf()
    {
        var book = new StrBook(Gbk("// header\r\nname_1>测试一\r\nname_2>Plain Text\r\n"));
        Assert.Equal(2, book.Count);
        Assert.Equal("测试一", book.Get("name_1"));
        Assert.Equal("Plain Text", book.Get("name_2"));
    }

    [Fact]
    public void AcceptsLfOnlyAndMissingFinalNewline()
    {
        var book = new StrBook(Gbk("a>1\nb>2\nc>3"));
        Assert.Equal(3, book.Count);
        Assert.Equal("1", book.Get("a"));
        Assert.Equal("3", book.Get("c"));
    }

    [Fact]
    public void IgnoresCommentsBlankLinesAndLinesWithoutSeparator()
    {
        var book = new StrBook(Gbk("// c\r\n\r\n//another>not a key\r\nbroken line\r\n>no key\r\nk>v\r\n"));
        Assert.Equal(1, book.Count);
        Assert.Equal("v", book.Get("k"));
        Assert.False(book.Contains("another"));
        Assert.False(book.Contains(string.Empty));
    }

    [Fact]
    public void BackslashNBecomesNewline()
    {
        var book = new StrBook(Gbk("text>第一行\\n第二行\\n\\n第四行\r\n"));
        Assert.Equal("第一行\n第二行\n\n第四行", book.Get("text"));
    }

    [Fact]
    public void ValueMayContainTheSeparatorAndBeEmpty()
    {
        var book = new StrBook(Gbk("a>x>y>z\r\nempty>\r\n"));
        Assert.Equal("x>y>z", book.Get("a"));
        Assert.True(book.TryGet("empty", out string text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void LaterDuplicateWins()
    {
        var book = new StrBook(Gbk("k>first\r\nk>second\r\n"));
        Assert.Equal(1, book.Count);
        Assert.Equal("second", book.Get("k"));
    }

    [Fact]
    public void MissingAndNullKeysAreNotFound()
    {
        var book = new StrBook(Gbk("k>v\r\n"));
        Assert.False(book.TryGet("nope", out string text));
        Assert.Null(text);
        Assert.False(book.TryGet(null, out string none));
        Assert.Null(none);
        Assert.Null(book.Get("nope"));
        Assert.False(book.Contains(null));
        Assert.True(book.Contains("k"));
    }

    [Fact]
    public void SkipsUtf8Bom()
    {
        byte[] body = Gbk("k>v\r\n");
        var withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        Array.Copy(body, 0, withBom, 3, body.Length);
        Assert.Equal("v", new StrBook(withBom).Get("k"));
    }

    [Fact]
    public void DecodesLazilyAndCaches()
    {
        var book = new StrBook(Gbk("k>缓存\r\n"));
        string a = book.Get("k");
        string b = book.Get("k");
        Assert.Same(a, b);
    }

    [Fact]
    public void KeysAreCaseSensitive()
    {
        var book = new StrBook(Gbk("Name>x\r\n"));
        Assert.True(book.Contains("Name"));
        Assert.False(book.Contains("name"));
        Assert.Equal(new[] { "Name" }, book.Keys.ToArray());
    }

    [Fact]
    public void NullDataIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new StrBook(null));
    }

    [Fact]
    public void EmptyFileHasNoKeys()
    {
        Assert.Equal(0, new StrBook(new byte[0]).Count);
    }
}

public class StrLibraryTests
{
    private static byte[] Gbk(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936).GetBytes(text);
    }

    private sealed class FakeFiles
    {
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        public readonly List<string> Reads = new List<string>();

        public byte[] Read(string path)
        {
            Reads.Add(path);
            return Files.TryGetValue(path, out byte[] data) ? data : null;
        }
    }

    [Fact]
    public void KindsMapToTheirTextFiles()
    {
        Assert.Equal("equipment/equipment.kor.str", StrLibrary.PathOfKind(StrLibrary.KindEquipment));
        Assert.Equal("stackable/stackable.kor.str", StrLibrary.PathOfKind(StrLibrary.KindStackable));
        Assert.Equal("itemshop/itemshop.kor.str", StrLibrary.PathOfKind(StrLibrary.KindItemShop));
        Assert.Equal("monster/monster.kor.str", StrLibrary.PathOfKind(StrLibrary.KindMonster));
        Assert.Equal("dungeon/dungeon.kor.str", StrLibrary.PathOfKind(StrLibrary.KindDungeon));
        Assert.Equal("town/town.kor.str", StrLibrary.PathOfKind(StrLibrary.KindTown));
        Assert.Equal("worldmap/worldmap.kor.str", StrLibrary.PathOfKind(StrLibrary.KindWorldMap));
        Assert.Null(StrLibrary.PathOfKind(9999));
        Assert.Null(StrLibrary.PathOfKind(-1));
    }

    [Fact]
    public void ResolvesAReferenceThroughItsKind()
    {
        var files = new FakeFiles();
        files.Files["equipment/equipment.kor.str"] = Gbk("name_1>装备甲\r\n");
        files.Files["stackable/stackable.kor.str"] = Gbk("name_1>消耗乙\r\n");
        var lib = new StrLibrary(files.Read);
        Assert.Equal("装备甲", lib.Get(StrLibrary.KindEquipment, "name_1"));
        Assert.Equal("消耗乙", lib.Get(StrLibrary.KindStackable, "name_1"));
        Assert.True(lib.TryGet(StrLibrary.KindEquipment, "name_1", out string text));
        Assert.Equal("装备甲", text);
    }

    [Fact]
    public void MissingKeyUnknownKindAndMissingFileAllFailQuietly()
    {
        var files = new FakeFiles();
        files.Files["equipment/equipment.kor.str"] = Gbk("name_1>x\r\n");
        var lib = new StrLibrary(files.Read);
        Assert.False(lib.TryGet(StrLibrary.KindEquipment, "name_2", out string a));
        Assert.Null(a);
        Assert.False(lib.TryGet(9999, "name_1", out string b));
        Assert.Null(b);
        Assert.False(lib.TryGet(StrLibrary.KindMonster, "name_1", out string c));
        Assert.Null(c);
        Assert.Null(lib.Get(StrLibrary.KindMonster, "name_1"));
    }

    [Fact]
    public void EachFileIsReadOnceEvenWhenMissing()
    {
        var files = new FakeFiles();
        files.Files["equipment/equipment.kor.str"] = Gbk("name_1>x\r\n");
        var lib = new StrLibrary(files.Read);
        for (int i = 0; i < 3; i++)
        {
            lib.Get(StrLibrary.KindEquipment, "name_1");
            lib.Get(StrLibrary.KindMonster, "name_1");
        }
        Assert.Equal(2, files.Reads.Count);
    }

    [Fact]
    public void UiTextComesFromTheUiFile()
    {
        var files = new FakeFiles();
        files.Files[StrLibrary.UiPath] = Gbk("title_key>界面标题\r\n");
        var lib = new StrLibrary(files.Read);
        Assert.Equal("界面标题", lib.Ui("title_key"));
        Assert.Null(lib.Ui("other"));
    }

    [Fact]
    public void BookLookupHandlesBadPaths()
    {
        var lib = new StrLibrary(p => null);
        Assert.Null(lib.Book(null));
        Assert.Null(lib.Book(string.Empty));
        Assert.Null(lib.Ui("k"));
        Assert.Throws<ArgumentNullException>(() => new StrLibrary(null));
    }
}
