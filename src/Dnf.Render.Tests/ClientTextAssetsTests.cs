using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// <see cref="DnfAssets.ClientText"/>:从数据目录里的客户端可执行文件读出的"编号>文字"表。
/// 可执行文件全是合成的 PE 映像,文字是合成的占位词;不碰任何真实文件。
/// </summary>
public sealed class ClientTextAssetsTests
{
    // 与产品代码里的接口常量一致(客户端函数 0x010f6560 取文字时用的编号);其余编号与文字都是合成的
    private const int FixedId = 0x5362;

    private static string Table => "1>SYNTH-ONE\r\n" + FixedId + ">SYNTH-NOTE\r\n";

    private static void AssertTheSyntheticTable(ClientTextTable table)
    {
        Assert.NotNull(table);
        Assert.Equal(2, table.Count);
        Assert.True(table.TryGet(FixedId, out string note));
        Assert.Equal("SYNTH-NOTE", note);
        Assert.True(table.TryGet(1, out string one));
        Assert.Equal("SYNTH-ONE", one);
        Assert.False(table.TryGet(2, out _));
    }

    [Theory]
    [InlineData("dnf.exe")]
    [InlineData("DNF.exe")]
    [InlineData("Dnf.Exe")]
    [InlineData("dNf.eXe")]
    [InlineData("DNF.EXE")]
    public void The_executable_in_the_data_directory_is_found_whatever_the_case_of_its_name(string fileName)
    {
        using var game = new MiniGameDirectory();
        game.Write(fileName, MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Equal(game.Root, assets.DataDirectory);
        AssertTheSyntheticTable(assets.ClientText);
    }

    [Fact]
    public void A_pe32_plus_executable_is_read_too()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table, pe32Plus: true));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        AssertTheSyntheticTable(assets.ClientText);
    }

    [Fact]
    public void Without_an_executable_the_table_is_null()
    {
        using var game = new MiniGameDirectory();
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);
    }

    [Theory]
    [InlineData("dnf.exe.bak")]
    [InlineData("xdnf.exe")]
    [InlineData("dnf.exee")]
    [InlineData("dnf")]
    [InlineData("dnf.dll")]
    [InlineData("dnf.exe ")]
    [InlineData("old_dnf.exe")]
    public void Only_a_file_named_exactly_dnf_exe_counts(string otherName)
    {
        using var game = new MiniGameDirectory();
        try
        {
            game.Write(otherName, MiniGameDirectory.Executable(Table));
        }
        catch (IOException)
        {
            return;                                          // 某些文件系统不允许这个名字(如结尾空格),那就没有什么可测的
        }
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);
    }

    [Fact]
    public void An_executable_in_a_subdirectory_or_a_directory_named_like_it_is_not_picked()
    {
        using var game = new MiniGameDirectory();
        Directory.CreateDirectory(Path.Combine(game.Root, "sub"));
        File.WriteAllBytes(Path.Combine(game.Root, "sub", "dnf.exe"), MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);

        using var other = new MiniGameDirectory();
        Directory.CreateDirectory(Path.Combine(other.Root, "dnf.exe"));
        using DnfAssets otherAssets = DnfAssets.Open(other.Root);

        Assert.Null(otherAssets.ClientText);
    }

    [Fact]
    public void A_garbage_file_gives_null_without_throwing()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", Encoding.ASCII.GetBytes("this is not a PE image at all"));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);

        using var empty = new MiniGameDirectory();
        empty.Write("dnf.exe", Array.Empty<byte>());
        using DnfAssets emptyAssets = DnfAssets.Open(empty.Root);

        Assert.Null(emptyAssets.ClientText);

        using var mz = new MiniGameDirectory();
        mz.Write("dnf.exe", new byte[] { (byte)'M', (byte)'Z' });
        using DnfAssets mzAssets = DnfAssets.Open(mz.Root);

        Assert.Null(mzAssets.ClientText);
    }

    [Fact]
    public void A_truncated_image_gives_null()
    {
        using var game = new MiniGameDirectory();
        byte[] whole = MiniGameDirectory.Executable(Table);
        game.Write("dnf.exe", whole.Take(whole.Length / 2).ToArray());
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);
    }

    [Fact]
    public void An_image_whose_resource_is_not_a_text_container_or_holds_no_entries_gives_null()
    {
        // 资源是明文(没有 SC01 容器头)
        using var notAContainer = new MiniGameDirectory();
        var spec = new SyntheticPe.Spec
        {
            Types = new[]
            {
                SyntheticPe.ResType.Named(ClientTextTable.ResourceTypeName,
                    new SyntheticPe.Item(ClientTextTable.DefaultResourceId, new SyntheticPe.Leaf(0, Encoding.ASCII.GetBytes("1>SYNTH-PLAIN\n")))),
            },
        };
        notAContainer.Write("dnf.exe", SyntheticPe.Build(spec).Bytes);
        using DnfAssets plainAssets = DnfAssets.Open(notAContainer.Root);

        Assert.Null(plainAssets.ClientText);

        using var noEntries = new MiniGameDirectory();
        noEntries.Write("dnf.exe", MiniGameDirectory.Executable("// only a comment\r\n\r\nno separator here\r\n"));
        using DnfAssets noEntriesAssets = DnfAssets.Open(noEntries.Root);

        Assert.Null(noEntriesAssets.ClientText);
    }

    [Fact]
    public void An_image_without_the_resource_gives_null()
    {
        using var game = new MiniGameDirectory();
        var spec = new SyntheticPe.Spec
        {
            Types = new[]
            {
                SyntheticPe.ResType.Named("OTHER", new SyntheticPe.Item(ClientTextTable.DefaultResourceId, new SyntheticPe.Leaf(0, Encoding.ASCII.GetBytes("x")))),
            },
        };
        game.Write("dnf.exe", SyntheticPe.Build(spec).Bytes);
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);
    }

    [Fact]
    public void Without_a_data_directory_the_table_is_null_even_if_an_executable_sits_beside_the_files()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table));
        using PvfArchive pvf = PvfArchive.Open(Path.Combine(game.Root, "Script.pvf"));
        var packs = new NpkLibrary(Path.Combine(game.Root, "ImagePacks2"));
        using var assets = new DnfAssets(pvf, packs);

        Assert.Null(assets.DataDirectory);
        Assert.Null(assets.ClientText);
    }

    [Fact]
    public void The_table_is_read_once_and_the_same_object_comes_back()
    {
        using var game = new MiniGameDirectory();
        string path = game.Write("dnf.exe", MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        ClientTextTable first = assets.ClientText;
        AssertTheSyntheticTable(first);
        Assert.Same(first, assets.ClientText);

        // 文件之后被删了、换了,读到的那份不变(读一次就缓存)
        File.Delete(path);
        Assert.Same(first, assets.ClientText);
    }

    [Fact]
    public void A_missing_table_is_remembered_too()
    {
        using var game = new MiniGameDirectory();
        using DnfAssets assets = DnfAssets.Open(game.Root);

        Assert.Null(assets.ClientText);
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table));          // 之后才出现的文件不影响已经缓存的结果
        Assert.Null(assets.ClientText);
    }

    [Fact]
    public async Task Concurrent_first_reads_all_see_one_table()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);

        const int Workers = 8;
        var gate = new ManualResetEventSlim(false);
        Task<ClientTextTable>[] tasks = Enumerable.Range(0, Workers)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    gate.Wait();                              // 所有线程一起放行,争着做第一次读
                    return assets.ClientText;
                },
                TaskCreationOptions.LongRunning))
            .ToArray();
        gate.Set();
        ClientTextTable[] results = await Task.WhenAll(tasks);

        AssertTheSyntheticTable(results[0]);
        Assert.All(results, t => Assert.Same(results[0], t));
    }

    [Fact]
    public void A_data_directory_that_vanished_gives_null_without_throwing()
    {
        using var game = new MiniGameDirectory();
        game.Write("dnf.exe", MiniGameDirectory.Executable(Table));
        using DnfAssets assets = DnfAssets.Open(game.Root);
        try
        {
            Directory.Delete(game.Root, true);
        }
        catch (IOException)
        {
            return;                                          // 文件还被占着的系统上删不掉,这条测不了
        }

        Assert.Null(assets.ClientText);
    }
}
