using Dnf.Net.Session;
using static Dnf.Net.Tests.SyntheticPeImage;

namespace Dnf.Net.Tests;

/// <summary>
/// 读版本资源的边界与"每个检查都有人盯着"(台账 E-453 的变异检查补出来的):数据目录项数与可选头大小的边界、资源目录登记值的零值、
/// 节表只认声明的节数且第一项也要看、版本块里键名每个字符的两个字节与结尾 0、语言层为空或有很多项、在语言层末尾截断、数据声明的大小越过文件末尾。
/// 输入全是 <see cref="SyntheticPeImage"/> 现场拼的合成字节。
/// </summary>
public sealed class ClientVersionFileEdgeTests
{
    private static Node Filler() => Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(new byte[8])))));

    private static void AssertVersion(byte[] image, ushort a, ushort b, ushort c, ushort d)
    {
        Assert.True(ClientVersionFile.TryReadFileVersion(image, out ClientFileVersion version, out ClientVersionFileProblem problem), problem.ToString());
        Assert.Equal(ClientVersionFileProblem.None, problem);
        Assert.Equal((a, b, c, d), (version.A, version.B, version.C, version.D));
    }

    private static void AssertProblem(byte[] image, ClientVersionFileProblem expected)
    {
        Assert.False(ClientVersionFile.TryReadFileVersion(image, out _, out ClientVersionFileProblem problem));
        Assert.Equal(expected, problem);
    }

    private static Node SinglePath(byte[] blob, int? declaredSize = null)
    {
        return Node.Dir(Id(16, Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(blob, declaredSize)))))));
    }

    // ---------------- 常量与枚举的取值本身是契约 ----------------

    [Fact]
    public void No_problem_is_the_zero_value_of_the_problem_enum()
    {
        Assert.Equal(0, (int)ClientVersionFileProblem.None);
        Assert.Equal(ClientVersionFileProblem.None, default(ClientVersionFileProblem));
    }

    [Fact]
    public void The_version_resource_size_cap_is_exactly_one_mebibyte()
    {
        Assert.Equal(1024 * 1024, ClientVersionFile.MaxVersionResourceBytes);
    }

    // ---------------- 数据目录:项数与可选头大小 ----------------

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    public void A_resource_entry_beyond_the_declared_number_of_data_directories_is_not_a_directory_entry(uint declared, bool pe32Plus)
    {
        // 第 2 项(资源)的位置上写着合规的值,但可选头只声明了 declared 项:那几个字节不属于数据目录,当没有资源目录
        var options = new Options { DirectoryCount = declared, ForceResourceDirectoryEntry = true, Pe32Plus = pe32Plus };
        AssertProblem(Build(3, 20, 105, 7, options), ClientVersionFileProblem.NoResourceDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exactly_three_declared_data_directories_make_the_resource_entry_count(bool pe32Plus)
    {
        AssertVersion(Build(3, 20, 105, 7, new Options { DirectoryCount = 3, Pe32Plus = pe32Plus }), 3, 20, 105, 7);
    }

    [Theory]
    [InlineData(false, 120)]
    [InlineData(true, 136)]
    public void The_resource_entry_must_lie_wholly_inside_the_declared_optional_header(bool pe32Plus, int exactFit)
    {
        // 资源项是数据目录第 2 项(8 字节):PE32 的数据目录从可选头第 96 字节起,PE32+ 从第 112 字节起,所以刚好装下的大小是 120 与 136
        AssertVersion(Build(3, 20, 105, 7, new Options { Pe32Plus = pe32Plus, OptionalHeaderSize = exactFit }), 3, 20, 105, 7);
        AssertVersion(Build(3, 20, 105, 7, new Options { Pe32Plus = pe32Plus, OptionalHeaderSize = exactFit + 1 }), 3, 20, 105, 7);
        AssertVersion(Build(3, 20, 105, 7, new Options { Pe32Plus = pe32Plus, OptionalHeaderSize = exactFit + 16 }), 3, 20, 105, 7);

        AssertProblem(Build(3, 20, 105, 7, new Options { Pe32Plus = pe32Plus, OptionalHeaderSize = exactFit - 1 }), ClientVersionFileProblem.NoResourceDirectory);
        AssertProblem(Build(3, 20, 105, 7, new Options { Pe32Plus = pe32Plus, OptionalHeaderSize = exactFit - 4 }), ClientVersionFileProblem.NoResourceDirectory);
        AssertProblem(Build(3, 20, 105, 7, new Options { Pe32Plus = pe32Plus, OptionalHeaderSize = exactFit - 8 }), ClientVersionFileProblem.NoResourceDirectory);
    }

    [Fact]
    public void A_declared_resource_rva_or_size_of_zero_means_there_is_no_resource_directory()
    {
        AssertProblem(Build(1, 2, 3, 4, new Options { DeclaredResourceSize = 0 }), ClientVersionFileProblem.NoResourceDirectory);
        AssertProblem(Build(1, 2, 3, 4, new Options { DeclaredResourceRva = 0 }), ClientVersionFileProblem.NoResourceDirectory);

        // 登记的大小只是"有没有"的标志:哪怕只写 1,读多少由目录自己的结构决定;
        // 只有最高字节非零的大小(0x01000000)也是非零,四个字节都要看,不能错位几个字节读到 0
        AssertVersion(Build(1, 2, 3, 4, new Options { DeclaredResourceSize = 1 }), 1, 2, 3, 4);
        AssertVersion(Build(1, 2, 3, 4, new Options { DeclaredResourceSize = 0x01000000u }), 1, 2, 3, 4);
        AssertVersion(Build(1, 2, 3, 4, new Options { DeclaredResourceSize = 0x01000000u, Pe32Plus = true }), 1, 2, 3, 4);
    }

    [Fact]
    public void The_section_raw_size_bounds_the_resource_directory_root()
    {
        // 资源目录根(16 字节头)必须整个落在节的原始数据里:节表声明只有 15 字节时就是资源目录的问题,而不是往下读到别的问题
        AssertProblem(Build(1, 2, 3, 4, new Options { ResourceRawSize = 15 }), ClientVersionFileProblem.BadResourceDirectory);
        AssertProblem(Build(1, 2, 3, 4, new Options { ResourceRawSize = 0 }), ClientVersionFileProblem.BadResourceDirectory);
    }

    [Fact]
    public void Every_byte_of_the_pe_signature_is_checked()
    {
        byte[] good = Build(1, 2, 3, 4);
        AssertVersion(good, 1, 2, 3, 4);
        for (int i = 0; i < 4; i++)
        {
            byte[] bad = (byte[])good.Clone();
            bad[0x80 + i] ^= 0x20;
            AssertProblem(bad, ClientVersionFileProblem.NotPeImage);
        }
        for (int i = 0; i < 2; i++)
        {
            byte[] bad = (byte[])good.Clone();
            bad[i] ^= 0x20;
            AssertProblem(bad, ClientVersionFileProblem.NotPeImage);
        }
    }

    // ---------------- 节表 ----------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_declared_number_of_section_headers_are_consulted(bool pe32Plus)
    {
        // 节表里实际写了两项,文件头只声明一项(代码节):资源节的表项在声明范围之外,映射不到
        AssertProblem(Build(1, 2, 3, 4, new Options { DeclaredSectionCount = 1, Pe32Plus = pe32Plus }), ClientVersionFileProblem.BadResourceDirectory);

        // 资源节排在第一项、声明一项:读得出
        AssertVersion(Build(1, 2, 3, 4, new Options { DeclaredSectionCount = 1, ResourceSectionFirst = true, Pe32Plus = pe32Plus }), 1, 2, 3, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_section_header_is_consulted_too(bool pe32Plus)
    {
        AssertVersion(Build(1, 2, 3, 4, new Options { ResourceSectionFirst = true, Pe32Plus = pe32Plus }), 1, 2, 3, 4);
    }

    // ---------------- 版本块里的键名 ----------------

    [Fact]
    public void Every_character_of_the_version_info_key_is_checked_in_both_of_its_bytes()
    {
        const string key = "VS_VERSION_INFO";
        for (int i = 0; i < key.Length; i++)
        {
            char[] wrongLow = key.ToCharArray();
            wrongLow[i] = key[i] == 'X' ? 'Y' : 'X';                    // 低字节不对
            AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, key: new string(wrongLow)))), ClientVersionFileProblem.BadVersionResource);

            char[] wrongHigh = key.ToCharArray();
            wrongHigh[i] = (char)(0x0100 | key[i]);                     // 低字节对、高字节不是 0
            AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, key: new string(wrongHigh)))), ClientVersionFileProblem.BadVersionResource);
        }
        AssertVersion(Build(TypicalTree(VersionBlob(1, 2, 3, 4, key: key))), 1, 2, 3, 4);
    }

    [Fact]
    public void The_key_must_end_with_a_zero_character_in_both_bytes()
    {
        // 结尾字符占键名之后的两个字节(块内第 36、37 字节)
        byte[] lowByte = VersionBlob(1, 2, 3, 4);
        lowByte[6 + 2 * 15] = 1;
        AssertProblem(Build(TypicalTree(lowByte)), ClientVersionFileProblem.BadVersionResource);

        byte[] highByte = VersionBlob(1, 2, 3, 4);
        highByte[6 + 2 * 15 + 1] = 1;
        AssertProblem(Build(TypicalTree(highByte)), ClientVersionFileProblem.BadVersionResource);

        // 键名比标准的长一个字符:结尾字符的位置上是字符不是 0
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, key: "VS_VERSION_INFOX"))), ClientVersionFileProblem.BadVersionResource);
    }

    // ---------------- 语言层 ----------------

    [Fact]
    public void Entries_beyond_the_declared_id_count_are_not_looked_at_in_the_type_and_name_levels()
    {
        // 类型层声明只有 1 个编号项,但紧跟着表的第二项物理上就是编号 16 的版本类型:不在声明范围内,当没有版本资源
        Node typeLevel = Node.Dir(Id(3, Filler()), Id(16, Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(VersionBlob(1, 2, 3, 4))))))));
        typeLevel.DeclaredIdEntries = 1;
        AssertProblem(Build(typeLevel), ClientVersionFileProblem.NoVersionResource);
        typeLevel.DeclaredIdEntries = null;                              // 同一棵树按真实数量声明:读得出
        AssertVersion(Build(typeLevel), 1, 2, 3, 4);

        // 编号层同理:编号 1 排在声明的范围之外
        Node nameLevel = Node.Dir(Id(2, Node.Dir(Id(0x409, Node.Leaf(new byte[8])))), Id(1, Node.Dir(Id(0x409, Node.Leaf(VersionBlob(1, 2, 3, 4))))));
        nameLevel.DeclaredIdEntries = 1;
        AssertProblem(Build(Node.Dir(Id(16, nameLevel))), ClientVersionFileProblem.NoVersionResource);
        nameLevel.DeclaredIdEntries = null;
        AssertVersion(Build(Node.Dir(Id(16, nameLevel))), 1, 2, 3, 4);
    }

    [Fact]
    public void An_image_cut_inside_the_first_directorys_entry_table_is_a_bad_directory()
    {
        // 典型的树:类型层 4 项(16 字节头 + 32 字节表)在资源节偏移 0;文件在表中间任何位置断掉,都是资源目录的问题
        byte[] full = Build(TypicalTree(VersionBlob(1, 2, 3, 4)));
        int root = 0x600;
        for (int cut = root; cut < root + 16 + 32; cut++)
        {
            AssertProblem(full.Take(cut).ToArray(), ClientVersionFileProblem.BadResourceDirectory);
        }
    }

    [Fact]
    public void An_empty_language_directory_means_there_is_no_version_resource()
    {
        AssertProblem(Build(Node.Dir(Id(16, Node.Dir(Id(1, Node.Dir()))))), ClientVersionFileProblem.NoVersionResource);

        // 排在一个有两项的目录之后、后面还跟着别的目录:读数量字段时错位几个字节读到的会是别的结构里的非零数
        Node tree = Node.Dir(
            Id(16, Node.Dir(
                Id(0, Node.Dir(Id(0x409, Node.Leaf(new byte[8])), Id(0x40A, Node.Leaf(new byte[8])))),
                Id(1, Node.Dir()))),
            Id(24, Filler()));
        AssertProblem(Build(tree), ClientVersionFileProblem.NoVersionResource);
    }

    [Fact]
    public void A_language_directory_with_many_entries_still_reads_the_first_one()
    {
        // 256 项:数量字段的高字节非零、低字节为 0,按偏一个字节读出来的数量会是 0
        var languages = new List<(bool Named, uint Id, Node Child)> { Id(0x400, Node.Leaf(VersionBlob(1, 2, 3, 4))) };
        for (uint i = 1; i < 256; i++)
        {
            languages.Add(Id(0x400 + i, Node.Leaf(VersionBlob(9, 9, 9, 9))));
        }
        AssertVersion(Build(Node.Dir(Id(16, Node.Dir(Id(1, Node.Dir(languages.ToArray())))))), 1, 2, 3, 4);
    }

    [Fact]
    public void An_image_cut_right_after_the_language_directory_entry_is_a_bad_directory_and_one_byte_earlier_has_no_first_entry()
    {
        // 最简的一条路:类型层在资源节偏移 0(16 + 8 字节),编号层在 24,语言层在 48 并在 72 结束,后面才是数据项
        byte[] full = Build(SinglePath(VersionBlob(1, 2, 3, 4)));
        AssertVersion(full, 1, 2, 3, 4);
        int cut = 0x600 + 72;
        AssertProblem(full.Take(cut).ToArray(), ClientVersionFileProblem.BadResourceDirectory);
        AssertProblem(full.Take(cut - 1).ToArray(), ClientVersionFileProblem.NoVersionResource);
    }

    // ---------------- 数据声明的大小 ----------------

    [Fact]
    public void A_data_entry_that_declares_more_bytes_than_the_section_holds_is_a_bad_version_resource()
    {
        byte[] blob = VersionBlob(1, 2, 3, 4);
        AssertVersion(Build(SinglePath(blob, blob.Length)), 1, 2, 3, 4);
        AssertProblem(Build(SinglePath(blob, blob.Length + 40000)), ClientVersionFileProblem.BadVersionResource);
    }
}
