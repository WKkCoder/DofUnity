using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// 声音数据的查找(证据 E-089):audio.xml 目录 + SoundPacks 里的 NPK + Music 里的散装文件。数据目录是在临时目录里造的合成数据,
/// "Ogg" 只是几个字节;文件夹与 audio.xml 故意用不同的大小写,检查查找不分大小写。
/// </summary>
public class SoundLibraryTests
{
    private static readonly byte[] PackedOgg = Encoding.ASCII.GetBytes("OggS-packed");
    private static readonly byte[] LooseOgg = Encoding.ASCII.GetBytes("OggS-loose");

    private const string Xml = @"<AudioTagDatabase>
  <EFFECT ID=""PACKED"" FILE=""Sounds\Test\Packed.ogg""/>
  <MUSIC ID=""LOOSE"" FILE=""Music\Sub\Loose.OGG"" LOOP_DELAY=""0""/>
  <EFFECT ID=""GONE"" FILE=""sounds\test\gone.ogg""/>
  <RANDOM ID=""R_BOTH""><ITEM TAG=""PACKED"" PROB=""1""/><ITEM TAG=""GONE"" PROB=""1""/></RANDOM>
</AudioTagDatabase>";

    private sealed class DataDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dnf-sound-test-" + Guid.NewGuid().ToString("N"));

        public DataDir(bool catalog = true, bool packs = true, bool music = true)
        {
            Directory.CreateDirectory(Root);
            if (catalog)
            {
                File.WriteAllText(Path.Combine(Root, "Audio.XML"), Xml, new UTF8Encoding(false));
            }
            if (packs)
            {
                string dir = Path.Combine(Root, "soundpacks");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "sounds_test.npk"), TestData.BuildNpk(new[] { ("sounds/test/packed.ogg", PackedOgg) }));
            }
            if (music)
            {
                string dir = Path.Combine(Root, "MUSIC", "Sub");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "loose.ogg"), LooseOgg);
            }
        }

        public void Dispose()
        {
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public void FilesAreFoundInThePacksAndInTheMusicFolderRegardlessOfCaseAndSlashes()
    {
        using var dir = new DataDir();
        using SoundLibrary lib = SoundLibrary.Open(dir.Root);

        Assert.True(lib.HasCatalog);
        Assert.Equal((1, 1), (lib.PackedCount, lib.LooseCount));
        Assert.True(lib.Catalog.TryGet("PACKED", out AudioEntry packed));
        Assert.True(lib.TryRead(packed.File, out byte[] a));                               // audio.xml 里的写法:大小写混用
        Assert.Equal(PackedOgg, a);
        Assert.True(lib.TryRead("SOUNDS\\TEST\\PACKED.OGG", out byte[] b));                // 反斜杠、全大写
        Assert.Equal(PackedOgg, b);

        Assert.True(lib.Catalog.TryGet("LOOSE", out AudioEntry loose));
        Assert.True(lib.Exists(loose.File));
        Assert.True(lib.TryRead(loose.File, out byte[] c));
        Assert.Equal(LooseOgg, c);
        Assert.True(lib.TryGetLoosePath("music/sub/loose.ogg", out string path));
        Assert.EndsWith("loose.ogg", path);
        Assert.False(lib.TryGetLoosePath("sounds/test/packed.ogg", out _));               // 包里的没有磁盘路径
    }

    [Fact]
    public void MissingFilesAndBadNamesAreReportedNotThrown()
    {
        using var dir = new DataDir();
        using SoundLibrary lib = SoundLibrary.Open(dir.Root);

        Assert.False(lib.Exists("sounds/test/gone.ogg"));
        Assert.False(lib.TryRead("sounds/test/gone.ogg", out byte[] data));
        Assert.Null(data);
        Assert.False(lib.Exists(null));
        Assert.False(lib.Exists("  "));
        Assert.False(lib.TryRead(string.Empty, out _));
        Assert.Equal(string.Empty, SoundLibrary.Normalize(null));
        Assert.Equal("a/b/c.ogg", SoundLibrary.Normalize("  \\a\\b/c.ogg "));
    }

    [Fact]
    public void ACompositeTagKnowsAllItsFilesEvenWhenSomeAreMissing()
    {
        using var dir = new DataDir();
        using SoundLibrary lib = SoundLibrary.Open(dir.Root);

        List<string> files = lib.Catalog.CollectFiles("R_BOTH");
        Assert.Equal(2, files.Count);
        Assert.Equal(new[] { true, false }, files.Select(lib.Exists).ToArray());
        string[] all = lib.AllFiles().OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "music/sub/loose.ogg", "sounds/test/packed.ogg" }, all);
    }

    [Fact]
    public void WithoutAnyOfTheThreePartsTheLibraryOpensEmptyAndSilent()
    {
        using var empty = new DataDir(catalog: false, packs: false, music: false);
        using SoundLibrary lib = SoundLibrary.Open(empty.Root);
        Assert.False(lib.HasCatalog);
        Assert.Equal(0, lib.Catalog.Count);
        Assert.Equal((0, 0), (lib.PackedCount, lib.LooseCount));
        Assert.False(lib.Exists("sounds/test/packed.ogg"));
        Assert.Empty(lib.AllFiles());

        using var onlyCatalog = new DataDir(packs: false, music: false);
        using SoundLibrary lib2 = SoundLibrary.Open(onlyCatalog.Root);
        Assert.True(lib2.HasCatalog);
        Assert.Equal(4, lib2.Catalog.Count);
        Assert.False(lib2.Exists("sounds/test/packed.ogg"));
    }

    [Fact]
    public void OpeningRejectsABadDirectory()
    {
        Assert.Throws<ArgumentException>(() => SoundLibrary.Open(""));
        Assert.Throws<DirectoryNotFoundException>(() => SoundLibrary.Open(Path.Combine(Path.GetTempPath(), "dnf-no-such-" + Guid.NewGuid().ToString("N"))));
    }
}
