using Dnf.Net.Session;
using static Dnf.Net.Tests.SyntheticPeImage;

namespace Dnf.Net.Tests;

/// <summary>
/// 从客户端程序文件读文件版本(台账 E-451、E-453):输入全是 <see cref="SyntheticPeImage"/> 现场拼的合成 PE 字节,不含任何真实程序的内容。
/// 重点三类:读对(四段各来自固定文件信息里自己的那一半、读文件版本而不是产品版本、经节表映射而不是把内存地址当文件偏移、
/// 只认资源编号 1 的版本资源与第一个语言);拒绝(各种畸形输入各给一个明确的问题类别);畸形输入永远不抛异常、不按声明的大小去分配。
/// </summary>
public sealed class ClientVersionFileTests
{
    // 每次都造新的占位目录(三层:编号 1 → 语言 → 8 字节叶子),不在树里共享同一个节点
    private static Node Filler() => Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(new byte[8])))));

    private static (bool Ok, ClientFileVersion Version, ClientVersionFileProblem Problem) Read(byte[] image)
    {
        bool ok = ClientVersionFile.TryReadFileVersion(image, out ClientFileVersion version, out ClientVersionFileProblem problem);
        return (ok, version, problem);
    }

    private static void AssertVersion(byte[] image, ushort a, ushort b, ushort c, ushort d)
    {
        var (ok, version, problem) = Read(image);
        Assert.True(ok, problem.ToString());
        Assert.Equal(ClientVersionFileProblem.None, problem);
        Assert.Equal(a, version.A);
        Assert.Equal(b, version.B);
        Assert.Equal(c, version.C);
        Assert.Equal(d, version.D);
    }

    private static void AssertProblem(byte[] image, ClientVersionFileProblem expected)
    {
        var (ok, version, problem) = Read(image);
        Assert.False(ok);
        Assert.Equal(expected, problem);
        Assert.Equal(default(ClientFileVersion).ToString(), version.ToString());
    }

    // ---------------- 读对 ----------------

    [Fact]
    public void Reads_the_four_segments_from_a_PE32_image_and_folds_them()
    {
        byte[] image = Build(3, 20, 105, 7);
        AssertVersion(image, 3, 20, 105, 7);
        Assert.Equal(32010507u, Read(image).Version.Fold());
    }

    [Fact]
    public void Reads_a_PE32_plus_image_the_same_way()
    {
        AssertVersion(Build(3, 20, 105, 7, new Options { Pe32Plus = true }), 3, 20, 105, 7);
    }

    [Fact]
    public void Each_segment_comes_from_its_own_half_of_the_fixed_file_info()
    {
        // 四段都取高低字节各不相同的大数:高低字、高低字节任何一处读反都会错
        AssertVersion(Build(0x1234, 0x5678, 0x9ABC, 0xDEF0), 0x1234, 0x5678, 0x9ABC, 0xDEF0);
        AssertVersion(Build(0xFFFF, 0, 0, 0xFFFF), 0xFFFF, 0, 0, 0xFFFF);
        AssertVersion(Build(0, 0, 0, 0), 0, 0, 0, 0);
    }

    [Fact]
    public void The_file_version_is_read_and_not_the_product_version()
    {
        // 合成块里的产品版本是 9.8.11.10,与文件版本不同
        AssertVersion(Build(3, 20, 105, 7), 3, 20, 105, 7);
        var (_, version, _) = Read(Build(3, 20, 105, 7));
        Assert.NotEqual((ushort)9, version.A);
        Assert.NotEqual((ushort)11, version.C);
    }

    [Theory]
    [InlineData(0x3000u, 0x600u)]
    [InlineData(0x2000u, 0x400u)]
    [InlineData(0x10000u, 0x1000u)]
    [InlineData(0x1200u, 0x800u)]
    public void The_resource_is_located_through_the_section_table_not_by_using_the_address_as_a_file_offset(uint rva, uint rawPointer)
    {
        AssertVersion(Build(7, 300, 4095, 65000, new Options { ResourceRva = rva, ResourceRawPointer = rawPointer }), 7, 300, 4095, 65000);
        AssertVersion(Build(7, 300, 4095, 65000, new Options { ResourceRva = rva, ResourceRawPointer = rawPointer, Pe32Plus = true }), 7, 300, 4095, 65000);
    }

    [Fact]
    public void The_version_type_is_found_among_other_types_after_a_named_entry()
    {
        // TypicalTree:类型层是 [名字项, 3, 16, 24];版本在第三项,前面还有按名字的项,读不到就说明按位置乱读了
        AssertVersion(Build(TypicalTree(VersionBlob(1, 2, 3, 4))), 1, 2, 3, 4);

        // 两个按名字的项、版本类型排在最后一个编号
        Node tree = Node.Dir(Named(Filler()), Named(Filler()), Id(2, Filler()), Id(6, Filler()), Id(16, Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(VersionBlob(5, 6, 7, 8))))))));
        AssertVersion(Build(tree), 5, 6, 7, 8);
    }

    [Fact]
    public void Only_resource_id_1_counts_as_the_version_resource()
    {
        byte[] other = VersionBlob(9, 9, 9, 9);
        byte[] wanted = VersionBlob(4, 3, 2, 1);

        // 只有编号 2:没有版本资源
        AssertProblem(Build(Node.Dir(Id(16, Node.Dir(Id(2, Node.Dir(Id(0x409, Node.Leaf(other)))))))), ClientVersionFileProblem.NoVersionResource);

        // 编号 2 排在前、编号 1 排在后:取编号 1 的
        Node both = Node.Dir(Id(16, Node.Dir(
            Id(1, Node.Dir(Id(0x409, Node.Leaf(wanted)))),
            Id(2, Node.Dir(Id(0x409, Node.Leaf(other)))))));
        AssertVersion(Build(both), 4, 3, 2, 1);
        Node reversedOrder = Node.Dir(Id(16, Node.Dir(
            Id(0, Node.Dir(Id(0x409, Node.Leaf(other)))),
            Id(1, Node.Dir(Id(0x409, Node.Leaf(wanted)))),
            Id(3, Node.Dir(Id(0x409, Node.Leaf(other)))))));
        AssertVersion(Build(reversedOrder), 4, 3, 2, 1);
    }

    [Fact]
    public void The_first_language_entry_is_the_one_that_is_read()
    {
        Node tree = TypicalTree(VersionBlob(1, 1, 1, 1), VersionBlob(2, 2, 2, 2), VersionBlob(3, 3, 3, 3));
        AssertVersion(Build(tree), 1, 1, 1, 1);
    }

    [Fact]
    public void A_version_resource_of_exactly_the_size_limit_is_accepted_and_one_byte_more_is_not()
    {
        int baseBytes = 40 + 52;
        byte[] atLimit = VersionBlob(1, 2, 3, 4, trailing: ClientVersionFile.MaxVersionResourceBytes - baseBytes);
        Assert.Equal(ClientVersionFile.MaxVersionResourceBytes, atLimit.Length);
        AssertVersion(Build(TypicalTree(atLimit)), 1, 2, 3, 4);

        byte[] overLimit = VersionBlob(1, 2, 3, 4, trailing: ClientVersionFile.MaxVersionResourceBytes - baseBytes + 1);
        AssertProblem(Build(TypicalTree(overLimit)), ClientVersionFileProblem.BadVersionResource);
    }

    [Fact]
    public void A_version_resource_of_exactly_the_minimum_size_is_accepted_and_one_byte_less_is_not()
    {
        byte[] exact = VersionBlob(6, 5, 4, 3, trailing: 0);
        Assert.Equal(92, exact.Length);
        AssertVersion(Build(TypicalTree(exact)), 6, 5, 4, 3);

        AssertProblem(Build(TypicalTree(exact.Take(91).ToArray())), ClientVersionFileProblem.BadVersionResource);
    }

    // ---------------- 拒绝 ----------------

    [Fact]
    public void Inputs_that_are_not_pe_images_are_named_as_such()
    {
        AssertProblem(null, ClientVersionFileProblem.NotPeImage);
        AssertProblem(Array.Empty<byte>(), ClientVersionFileProblem.NotPeImage);
        AssertProblem(new byte[0x3F], ClientVersionFileProblem.NotPeImage);
        AssertProblem(new byte[0x200], ClientVersionFileProblem.NotPeImage);                      // 没有 MZ

        byte[] good = Build(1, 2, 3, 4);

        byte[] noMz = (byte[])good.Clone();
        noMz[1] = (byte)'X';
        AssertProblem(noMz, ClientVersionFileProblem.NotPeImage);

        byte[] noPeSignature = (byte[])good.Clone();
        noPeSignature[0x80 + 2] = 1;
        AssertProblem(noPeSignature, ClientVersionFileProblem.NotPeImage);

        byte[] wrongPe = (byte[])good.Clone();
        wrongPe[0x80] = (byte)'Q';
        AssertProblem(wrongPe, ClientVersionFileProblem.NotPeImage);

        byte[] hugeOffset = (byte[])good.Clone();
        hugeOffset[0x3C] = hugeOffset[0x3D] = hugeOffset[0x3E] = hugeOffset[0x3F] = 0xFF;          // e_lfanew = 0xFFFFFFFF
        AssertProblem(hugeOffset, ClientVersionFileProblem.NotPeImage);

        byte[] pastEnd = (byte[])good.Clone();
        pastEnd[0x3C] = (byte)((good.Length - 20) & 0xFF);                                          // PE 头放不下
        pastEnd[0x3D] = (byte)(((good.Length - 20) >> 8) & 0xFF);
        pastEnd[0x3E] = pastEnd[0x3F] = 0;
        AssertProblem(pastEnd, ClientVersionFileProblem.NotPeImage);

        byte[] unknownMagic = (byte[])good.Clone();
        unknownMagic[0x80 + 24] = 0x07;                                                             // 既不是 0x10B 也不是 0x20B
        unknownMagic[0x80 + 25] = 0x01;
        AssertProblem(unknownMagic, ClientVersionFileProblem.NotPeImage);
    }

    [Fact]
    public void A_missing_resource_directory_is_named()
    {
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4)), new Options { DeclareResourceDirectory = false }), ClientVersionFileProblem.NoResourceDirectory);
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4)), new Options { DirectoryCount = 2 }), ClientVersionFileProblem.NoResourceDirectory);
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4)), new Options { DirectoryCount = 0 }), ClientVersionFileProblem.NoResourceDirectory);
        AssertVersion(Build(TypicalTree(VersionBlob(1, 2, 3, 4)), new Options { DirectoryCount = 3 }), 1, 2, 3, 4);        // 刚好够到第 2 项(资源)就能读
    }

    [Fact]
    public void A_resource_directory_that_maps_to_no_section_is_bad()
    {
        byte[] image = Build(1, 2, 3, 4);
        int directories = 0x80 + 24 + 96;                                 // PE32 的数据目录起点
        image[directories + 16] = 0x00;                                   // 资源目录 RVA 改成 0x9000:不在任何节里
        image[directories + 17] = 0x90;
        image[directories + 18] = image[directories + 19] = 0;
        AssertProblem(image, ClientVersionFileProblem.BadResourceDirectory);

        byte[] justBefore = Build(1, 2, 3, 4);
        justBefore[directories + 16] = 0xFF;                              // RVA 0x0FFF:在 .text(0x1000 起)之前
        justBefore[directories + 17] = 0x0F;
        justBefore[directories + 18] = justBefore[directories + 19] = 0;
        AssertProblem(justBefore, ClientVersionFileProblem.BadResourceDirectory);
    }

    [Fact]
    public void A_section_table_that_does_not_fit_in_the_file_is_bad()
    {
        byte[] image = Build(1, 2, 3, 4);
        image[0x80 + 6] = 0xFF;                                           // 声称有 65535 个节
        image[0x80 + 7] = 0xFF;
        AssertProblem(image, ClientVersionFileProblem.BadResourceDirectory);
    }

    [Fact]
    public void A_version_type_without_a_version_resource_is_named()
    {
        AssertProblem(Build(Node.Dir(Id(3, Filler()), Id(24, Filler()))), ClientVersionFileProblem.NoVersionResource);
        AssertProblem(Build(Node.Dir()), ClientVersionFileProblem.NoVersionResource);                         // 空的类型层
        AssertProblem(Build(Node.Dir(Id(16, Node.Dir()))), ClientVersionFileProblem.NoVersionResource);      // 版本类型下一项也没有
        AssertProblem(Build(Node.Dir(Id(16, Node.Dir(Id(1, Node.Dir()))))), ClientVersionFileProblem.NoVersionResource);   // 没有语言项
        AssertProblem(Build(Node.Dir(Named(Filler()))), ClientVersionFileProblem.NoVersionResource);          // 只有按名字的项
    }

    [Fact]
    public void Entries_that_are_not_where_the_structure_requires_them_are_bad()
    {
        byte[] blob = VersionBlob(1, 2, 3, 4);
        Node leafLanguage = Node.Leaf(blob);

        // 类型层的版本项指向叶子而不是目录
        AssertProblem(Build(Node.Dir(Id(16, leafLanguage))), ClientVersionFileProblem.BadResourceDirectory);

        // 资源编号层的 1 号项指向叶子而不是目录
        AssertProblem(Build(Node.Dir(Id(16, Node.Dir(Id(1, leafLanguage))))), ClientVersionFileProblem.BadResourceDirectory);

        // 语言层的第一项还是目录(应该是叶子)
        AssertProblem(Build(Node.Dir(Id(16, Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Dir(Id(1, leafLanguage))))))))), ClientVersionFileProblem.BadResourceDirectory);
    }

    [Fact]
    public void A_directory_that_points_back_at_the_root_terminates_with_a_problem()
    {
        byte[] image = Build(Node.Dir(Id(16, Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(VersionBlob(1, 2, 3, 4)))))))));
        // 根目录只有一个项(编号 16),它的"子目录偏移"改成 0x80000000(也就是根自己):根里找不到编号 1,读取应当结束并说没有版本资源
        int rootEntryOffsetField = 0x600 + 16 + 4;
        image[rootEntryOffsetField] = image[rootEntryOffsetField + 1] = image[rootEntryOffsetField + 2] = 0;
        image[rootEntryOffsetField + 3] = 0x80;
        AssertProblem(image, ClientVersionFileProblem.NoVersionResource);
    }

    [Fact]
    public void Entry_counts_that_claim_more_than_the_file_holds_are_bad_without_reading_past_the_end()
    {
        byte[] image = Build(1, 2, 3, 4);
        int rootAt = 0x600;
        image[rootAt + 14] = 0xFF;                                         // 根目录声称有 65535 个编号项
        image[rootAt + 15] = 0xFF;
        // 声称的项数装不进文件:按"目录形状不对"处理,而不是读出界
        AssertProblem(image, ClientVersionFileProblem.BadResourceDirectory);
    }

    [Fact]
    public void A_data_entry_whose_data_lies_outside_every_section_is_a_bad_version_resource()
    {
        byte[] image = Build(1, 2, 3, 4);
        // 数据项的 RVA 在资源节内的某处:这里把所有数据项的 RVA 都改成 0x9000(不在任何节里)
        int count = 0;
        for (int i = 0x600; i + 16 <= image.Length; i += 4)
        {
            // 数据项:RVA(落在资源节内 0x3000..)与大小(小端 92+40 = 132 = 0x84)紧邻
            if (image[i + 4] == 0x84 && image[i + 5] == 0 && image[i + 6] == 0 && image[i + 7] == 0 && image[i + 1] >= 0x30 && image[i + 1] < 0x40 && image[i + 2] == 0)
            {
                image[i] = 0x00;
                image[i + 1] = 0x90;
                count++;
            }
        }
        Assert.True(count >= 1, "没有找到版本数据项");
        AssertProblem(image, ClientVersionFileProblem.BadVersionResource);
    }

    [Theory]
    [InlineData("VS_VERSION_INFX")]
    [InlineData("vs_version_info")]
    [InlineData("VS_VERSION_INFO2")]
    [InlineData("VS_VERSION_INF")]
    [InlineData("")]
    public void A_wrong_key_name_is_a_bad_version_resource(string key)
    {
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, key: key))), ClientVersionFileProblem.BadVersionResource);
    }

    [Fact]
    public void A_wrong_signature_or_too_short_a_value_is_a_bad_version_resource()
    {
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, signature: 0xFEEF04BCu))), ClientVersionFileProblem.BadVersionResource);
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, signature: 0))), ClientVersionFileProblem.BadVersionResource);
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, valueLength: 51))), ClientVersionFileProblem.BadVersionResource);
        AssertProblem(Build(TypicalTree(VersionBlob(1, 2, 3, 4, valueLength: 0))), ClientVersionFileProblem.BadVersionResource);
        AssertVersion(Build(TypicalTree(VersionBlob(1, 2, 3, 4, valueLength: 52))), 1, 2, 3, 4);
        AssertVersion(Build(TypicalTree(VersionBlob(1, 2, 3, 4, valueLength: 53))), 1, 2, 3, 4);                // 值长度只要求不短于 52
    }

    [Fact]
    public void The_problem_descriptions_are_distinct_non_empty_and_carry_no_values()
    {
        var seen = new HashSet<string>();
        foreach (ClientVersionFileProblem problem in Enum.GetValues<ClientVersionFileProblem>())
        {
            string text = ClientVersionFile.Describe(problem);
            if (problem == ClientVersionFileProblem.None)
            {
                Assert.Equal(string.Empty, text);
                continue;
            }
            Assert.False(string.IsNullOrWhiteSpace(text), problem.ToString());
            Assert.True(seen.Add(text), problem + " 的文案重复");
            foreach (string bad in new[] { "0x", "/", "\\" })
            {
                Assert.DoesNotContain(bad, text);
            }
        }
        Assert.Equal(5, seen.Count);
    }

    // ---------------- 畸形输入永远不抛异常 ----------------

    [Fact]
    public void Every_truncation_of_a_valid_image_is_handled_and_success_is_monotonic_in_the_length()
    {
        foreach (bool plus in new[] { false, true })
        {
            byte[] image = Build(3, 20, 105, 7, new Options { Pe32Plus = plus });
            bool seenSuccess = false;
            for (int length = 0; length <= image.Length; length++)
            {
                byte[] cut = image.Take(length).ToArray();
                (bool ok, ClientFileVersion version, ClientVersionFileProblem problem) result = default;
                Exception thrown = Record.Exception(() => result = Read(cut));
                Assert.Null(thrown);
                if (result.ok)
                {
                    seenSuccess = true;
                    Assert.Equal("3.20.105.7", result.version.ToString());
                }
                else
                {
                    Assert.False(seenSuccess, $"长度 {length} 失败,但更短的长度已经成功过");
                    Assert.NotEqual(ClientVersionFileProblem.None, result.problem);
                }
            }
            Assert.True(seenSuccess);
        }
    }

    [Fact]
    public void Random_corruption_never_throws_and_never_returns_a_success_with_a_problem_set()
    {
        byte[] original = Build(3, 20, 105, 7);
        var random = new Random(777);
        for (int trial = 0; trial < 12000; trial++)
        {
            byte[] image = (byte[])original.Clone();
            int flips = 1 + random.Next(4);
            for (int i = 0; i < flips; i++)
            {
                // 偏向头部与资源区(其余大片是零,改了也没意义)
                int at = random.Next(2) == 0 ? random.Next(0x200) : 0x600 + random.Next(image.Length - 0x600);
                image[at] = (byte)random.Next(256);
            }
            (bool ok, ClientFileVersion version, ClientVersionFileProblem problem) result = default;
            Exception thrown = Record.Exception(() => result = Read(image));
            Assert.Null(thrown);
            Assert.Equal(result.ok, result.problem == ClientVersionFileProblem.None);
        }
    }

    [Fact]
    public void All_zero_and_all_ones_buffers_of_assorted_sizes_are_handled()
    {
        foreach (int size in new[] { 0, 1, 2, 0x3F, 0x40, 0x41, 0x80, 0x100, 0x1000, 0x10000 })
        {
            foreach (byte fill in new byte[] { 0x00, 0xFF, (byte)'M' })
            {
                byte[] image = Enumerable.Repeat(fill, size).ToArray();
                Exception thrown = Record.Exception(() => Read(image));
                Assert.Null(thrown);
                Assert.False(Read(image).Ok);
            }
        }
    }
}
