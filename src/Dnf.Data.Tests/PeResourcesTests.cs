using Dnf.Data;

namespace Dnf.Data.Tests;

/// <summary>PE 资源读取的测试:全部用合成映像(<see cref="SyntheticPe"/>),不依赖任何原版文件。</summary>
public class PeResourcesTests
{
    private const uint HighBit = 0x80000000u;

    private static bool TryRead(byte[] image, string type, int id, out byte[] data)
    {
        using var stream = new MemoryStream(image);
        return PeResources.TryReadNamedResource(stream, type, id, out data);
    }

    private static byte[] Read(byte[] image, string type, int id)
    {
        Assert.True(TryRead(image, type, id, out byte[] data), $"应当读到 {type}/{id}");
        return data;
    }

    private static void AssertFalse(byte[] image, string type, int id)
    {
        Assert.False(TryRead(image, type, id, out byte[] data));
        Assert.Null(data);
    }

    /// <summary>在另一个线程上跑并限时:成环、过深这类输入若让实现死循环,这里会明确失败,而不是把整个测试进程挂住。</summary>
    private static bool TryReadBounded(byte[] image, string type, int id, out byte[] data)
    {
        Task<(bool, byte[])> task = Task.Run(() =>
        {
            bool ok = TryRead(image, type, id, out byte[] d);
            return (ok, d);
        });
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "读取没有在限时内返回(疑似死循环)");
        data = task.Result.Item2;
        return task.Result.Item1;
    }

    private static byte[] Ascii(string text) => SyntheticPe.Ascii(text);

    public static IEnumerable<object[]> Layouts()
    {
        yield return new object[] { false, false };
        yield return new object[] { true, false };
        yield return new object[] { false, true };
        yield return new object[] { true, true };
    }

    // ------------------------------------------------------------------ 读到

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ReadsTheNamedResourceInEveryLayout(bool pe32Plus, bool separateDataSection)
    {
        // PE32 / PE32+ × 数据与目录同节 / 数据在另一个节:取 FILE/157,第一个语言项("FILE/157/first",语言号 0x0804,排在语言号更小的 0x0409 之前)。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(pe32Plus, separateDataSection));
        Assert.Equal(Ascii("FILE/157/first"), Read(image.Bytes, "FILE", 157));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataRvaIsTranslatedThroughTheSectionTableOfItsOwnSection(bool pe32Plus)
    {
        // 数据在另一个节里:数据的 RVA(例如 0x6000 一带)与它的文件偏移毫无关系,必须按"包含这个 RVA 的那个节"换算。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(pe32Plus, separate: true));
        uint rva = image.ReadU32(image["data-entry:FILE/157/0"]);
        int fileOffset = image["data:FILE/157/0"];
        Assert.NotEqual((uint)fileOffset, rva);
        Assert.NotEqual(image.ReadU32(image["resource-directory"]) + (uint)(fileOffset - image["rsrc"]), rva);
        Assert.Equal(Ascii("FILE/157/first"), Read(image.Bytes, "FILE", 157));
    }

    [Fact]
    public void OtherIdsOfTheSameTypeReturnTheirOwnData()
    {
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard()).Bytes;
        Assert.Equal(Ascii("FILE/41/a"), Read(image, "FILE", 41));
        Assert.Equal(Ascii("FILE/43/a"), Read(image, "FILE", 43));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(40)]
    [InlineData(156)]
    [InlineData(158)]
    [InlineData(164)]
    [InlineData(42)]
    [InlineData(44)]
    [InlineData(157 + 65536)]
    [InlineData(-157)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void IdsThatTheTypeDoesNotHaveAreNotFound(int id)
    {
        AssertFalse(SyntheticPe.Build(SyntheticPe.Standard()).Bytes, "FILE", id);
    }

    [Theory]
    [InlineData("FILE")]
    [InlineData("file")]
    [InlineData("File")]
    [InlineData("fIlE")]
    public void TypeNameIsMatchedIgnoringCase(string typeName)
    {
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard()).Bytes;
        Assert.Equal(Ascii("FILE/157/first"), Read(image, typeName, 157));
    }

    [Fact]
    public void TypeNameMustMatchTheWholeName()
    {
        // FIL、FILF(与 FILE 同长度)、FILES 各有自己的 157,取到的必须是各自的,而不是 FILE 的。
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard()).Bytes;
        Assert.Equal(Ascii("decoy:FIL/157"), Read(image, "FIL", 157));
        Assert.Equal(Ascii("decoy:FILF/157"), Read(image, "FILF", 157));
        Assert.Equal(Ascii("decoy:FILES/157"), Read(image, "files", 157));
    }

    [Theory]
    [InlineData("FILEX")]
    [InlineData("FIL E")]
    [InlineData("FI")]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("#3")]
    [InlineData("10")]
    [InlineData("RCDATA")]
    public void TypeNamesThatDoNotExistAreNotFound(string typeName)
    {
        // 数字类型 3、10 下面也有编号 157,但类型是按"名字"匹配的:数字类型不会被名字取到,更不会串到别的类型。
        AssertFalse(SyntheticPe.Build(SyntheticPe.Standard()).Bytes, typeName, 157);
    }

    [Fact]
    public void FirstLanguageEntryIsTaken()
    {
        // 41 的两个语言:0x0409、0x0804(第一个是 a);157 的两个语言:0x0804、0x0409(第一个是 first,不是"语言号最小的"也不是最后一个)。
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard()).Bytes;
        Assert.Equal(Ascii("FILE/41/a"), Read(image, "FILE", 41));
        Assert.Equal(Ascii("FILE/157/first"), Read(image, "FILE", 157));
    }

    [Fact]
    public void NonAsciiTypeNamesAreComparedAsUtf16IgnoringCase()
    {
        var spec = new SyntheticPe.Spec
        {
            Types = new[]
            {
                SyntheticPe.ResType.Named("资源ÄÖÜ", new SyntheticPe.Item(7, new SyntheticPe.Leaf(0, Ascii("unicode-name")))),
            },
        };
        byte[] image = SyntheticPe.Build(spec).Bytes;
        Assert.Equal(Ascii("unicode-name"), Read(image, "资源ÄÖÜ", 7));
        Assert.Equal(Ascii("unicode-name"), Read(image, "资源äöü", 7));
        AssertFalse(image, "资源ÄÖ", 7);
    }

    [Fact]
    public void ZeroSizedResourceIsAnEmptyArray()
    {
        var spec = new SyntheticPe.Spec
        {
            Types = new[] { SyntheticPe.ResType.Named("FILE", new SyntheticPe.Item(157, new SyntheticPe.Leaf(0, new byte[0]))) },
        };
        byte[] image = SyntheticPe.Build(spec).Bytes;
        Assert.True(TryRead(image, "FILE", 157, out byte[] data));
        Assert.NotNull(data);
        Assert.Empty(data);
    }

    // ------------------------------------------------------------------ 头与数据目录

    [Fact]
    public void ImageWithoutAResourceDirectoryReturnsFalse()
    {
        SyntheticPe.Spec spec = SyntheticPe.Standard();
        spec.OmitResourceDirectory = true;
        AssertFalse(SyntheticPe.Build(spec).Bytes, "FILE", 157);
    }

    [Fact]
    public void DirectoryCountOfThreeIsTheSmallestThatHoldsTheResourceDirectory()
    {
        // 数据目录下标 2 才是资源目录:个数 3 刚好放得下(可选头大小也只留 3 项),个数 2 放不下。
        SyntheticPe.Spec three = SyntheticPe.Standard();
        three.DirectoryCount = 3;
        Assert.Equal(Ascii("FILE/157/first"), Read(SyntheticPe.Build(three).Bytes, "FILE", 157));

        SyntheticPe.Spec threePlus = SyntheticPe.Standard(pe32Plus: true);
        threePlus.DirectoryCount = 3;
        Assert.Equal(Ascii("FILE/157/first"), Read(SyntheticPe.Build(threePlus).Bytes, "FILE", 157));

        SyntheticPe.Spec two = SyntheticPe.Standard();
        two.DirectoryCount = 2;
        AssertFalse(SyntheticPe.Build(two).Bytes, "FILE", 157);
    }

    [Fact]
    public void DeclaredDirectoryCountBelowThreeReturnsFalseEvenWhenTheEntryIsThere()
    {
        // 可选头够大、资源目录项也在,只是 NumberOfRvaAndSizes 声明只有 2 个:按声明走,没有资源目录。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["directory-count"], 2);
        AssertFalse(image.Bytes, "FILE", 157);
        image.PatchU32(image["directory-count"], 3);
        Assert.Equal(Ascii("FILE/157/first"), Read(image.Bytes, "FILE", 157));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalHeaderSmallerThanTheDataDirectoryNeedsReturnsFalse(bool pe32Plus)
    {
        // 声明的可选头大小比"数据目录下标 2 的末尾"(PE32:120,PE32+:136)少 1 字节。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(pe32Plus));
        image.PatchU16(image["coff"] + 16, pe32Plus ? 135 : 119);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    [Fact]
    public void WrongMagicsAndSignaturesReturnFalse()
    {
        SyntheticPe.Image good = SyntheticPe.Build(SyntheticPe.Standard());

        byte[] zm = (byte[])good.Bytes.Clone();
        zm[0] = (byte)'Z';
        zm[1] = (byte)'M';
        AssertFalse(zm, "FILE", 157);

        byte[] noSignature = (byte[])good.Bytes.Clone();
        noSignature[good["pe"]] = (byte)'X';
        AssertFalse(noSignature, "FILE", 157);

        byte[] noSignatureTail = (byte[])good.Bytes.Clone();
        noSignatureTail[good["pe"] + 3] = 1;
        AssertFalse(noSignatureTail, "FILE", 157);

        // 可选头魔数 0x107(ROM 映像)既不是 PE32 也不是 PE32+。
        SyntheticPe.Image rom = SyntheticPe.Build(SyntheticPe.Standard());
        rom.PatchU16(rom["optional"], 0x107);
        AssertFalse(rom.Bytes, "FILE", 157);

        // PE32 的魔数配 PE32+ 的数据目录位置(反之亦然)读不到:数据目录的起点由魔数决定。
        SyntheticPe.Image wrongKind = SyntheticPe.Build(SyntheticPe.Standard());
        wrongKind.PatchU16(wrongKind["optional"], 0x20b);
        AssertFalse(wrongKind.Bytes, "FILE", 157);
        SyntheticPe.Image wrongKindPlus = SyntheticPe.Build(SyntheticPe.Standard(pe32Plus: true));
        wrongKindPlus.PatchU16(wrongKindPlus["optional"], 0x10b);
        AssertFalse(wrongKindPlus.Bytes, "FILE", 157);
    }

    [Fact]
    public void PeHeaderOffsetBeyondTheFileReturnsFalse()
    {
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(0x3C, 0xFFFFFFF0u);
        AssertFalse(image.Bytes, "FILE", 157);
        image.PatchU32(0x3C, (uint)image.Bytes.Length - 3);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    [Fact]
    public void SectionCountMustBeBetweenOneAndNinetySix()
    {
        SyntheticPe.Image zero = SyntheticPe.Build(SyntheticPe.Standard());
        zero.PatchU16(zero["coff"] + 2, 0);
        AssertFalse(zero.Bytes, "FILE", 157);

        // 刚好 96 个节(2 个有内容 + 94 个空节)可以;声明 97 个不行。
        SyntheticPe.Spec spec = SyntheticPe.Standard();
        spec.ExtraSections = PeResources.MaxSections - 2;
        SyntheticPe.Image ninetySix = SyntheticPe.Build(spec);
        Assert.Equal(Ascii("FILE/157/first"), Read(ninetySix.Bytes, "FILE", 157));

        spec.ExtraSections = PeResources.MaxSections - 1;
        SyntheticPe.Image ninetySeven = SyntheticPe.Build(spec);
        AssertFalse(ninetySeven.Bytes, "FILE", 157);
    }

    [Fact]
    public void NotPeFilesReturnFalse()
    {
        AssertFalse(new byte[0], "FILE", 157);
        AssertFalse(new byte[] { (byte)'M' }, "FILE", 157);
        AssertFalse(new byte[] { (byte)'M', (byte)'Z' }, "FILE", 157);

        var random = new Random(20260523);
        for (int round = 0; round < 20; round++)
        {
            var bytes = new byte[256 + round * 37];
            random.NextBytes(bytes);
            AssertFalse(bytes, "FILE", 157);
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            AssertFalse(bytes, "FILE", 157);
        }
    }

    [Fact]
    public void SectionTableRegionThatIsMissingReturnsFalse()
    {
        // 节表位置(可选头之后)已经超出文件末尾。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        byte[] cut = new byte[image["section-table"] + 39];
        Array.Copy(image.Bytes, cut, cut.Length);
        AssertFalse(cut, "FILE", 157);
    }

    // ------------------------------------------------------------------ 截断

    [Theory]
    [MemberData(nameof(Layouts))]
    public void TruncationAtAnyLengthReturnsFalseWithoutThrowing(bool pe32Plus, bool separateDataSection)
    {
        // 文件截到任何一个位置(DOS 头、PE 头、可选头、节表、目录、名字串、数据项、数据本身的中间)都读不到、不抛异常;
        // 目标数据的最后一个字节之前的任何长度都不够,从这一长度起(含)读得到。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(pe32Plus, separateDataSection));
        int end = image.DataEnd("FILE", 157, 0);
        Assert.True(end <= image.Bytes.Length);
        for (int length = 0; length < end; length++)
        {
            using var stream = new MemoryStream(image.Bytes, 0, length);
            Assert.False(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data), $"截到 {length} 字节不应读到");
            Assert.Null(data);
        }
        using (var exact = new MemoryStream(image.Bytes, 0, end))
        {
            Assert.True(PeResources.TryReadNamedResource(exact, "FILE", 157, out byte[] data));
            Assert.Equal(Ascii("FILE/157/first"), data);
        }
    }

    // ------------------------------------------------------------------ 成环、过深

    [Fact]
    public void TypeDirectoryPointingBackToTheRootIsACycle()
    {
        // 类型 FILE 的子目录指回根目录;再让根目录里的数字项 3 改成编号 157,并把它的目录的第一项改成直接指向目标数据项——
        // 如果实现不拒绝"指回根目录",就会顺着 根 → 根 → 编号 157 → 数据 走到数据,返回 true。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["type-entry:FILE"] + 4, HighBit | 0u);
        image.PatchU32(image["type-entry:#3"], 157u);
        image.PatchU32(image["item-entry:#3/157"] + 4, image.Relative("data-entry:FILE/157/0"));
        Assert.False(TryReadBounded(image.Bytes, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void ItemDirectoryPointingBackToTheTypeDirectoryIsACycle()
    {
        // 编号 157 的子目录就是类型目录自己,且类型目录的第一项(编号 41)被改成直接指向目标数据项:
        // 不拒绝成环的实现会把类型目录当语言目录、取第一项,读到数据。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["item-entry:FILE/157"] + 4, HighBit | image.Relative("type-dir:FILE"));
        image.PatchU32(image["item-entry:FILE/41"] + 4, image.Relative("data-entry:FILE/157/0"));
        Assert.False(TryReadBounded(image.Bytes, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void ItemDirectoryPointingBackToTheRootIsACycle()
    {
        // 编号 157 的子目录是根目录,根目录的第一项(类型 FIL)被改成直接指向目标数据项。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["item-entry:FILE/157"] + 4, HighBit | 0u);
        image.PatchU32(image["type-entry:FIL"] + 4, image.Relative("data-entry:FILE/157/0"));
        Assert.False(TryReadBounded(image.Bytes, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void LanguageEntryPointingAtASubdirectoryIsTooDeep()
    {
        // 第三层的项不是数据项而是子目录(目标是别的编号的语言目录,里面有合法数据):比三层更深,不认,也不深入。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["leaf-entry:FILE/157/0"] + 4, HighBit | image.Relative("item-dir:FILE/41"));
        Assert.False(TryReadBounded(image.Bytes, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void LanguageEntryPointingAtItsOwnDirectoryTerminates()
    {
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["leaf-entry:FILE/157/0"] + 4, HighBit | image.Relative("item-dir:FILE/157"));
        Assert.False(TryReadBounded(image.Bytes, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void TypeAndItemEntriesThatAreNotSubdirectoriesReturnFalse()
    {
        // 类型项或编号项的"数据偏移"没有高位(是数据项而不是子目录):结构不对。
        SyntheticPe.Image typeLeaf = SyntheticPe.Build(SyntheticPe.Standard());
        typeLeaf.PatchU32(typeLeaf["type-entry:FILE"] + 4, typeLeaf.Relative("data-entry:FILE/157/0"));
        AssertFalse(typeLeaf.Bytes, "FILE", 157);

        SyntheticPe.Image itemLeaf = SyntheticPe.Build(SyntheticPe.Standard());
        itemLeaf.PatchU32(itemLeaf["item-entry:FILE/157"] + 4, itemLeaf.Relative("data-entry:FILE/157/0"));
        AssertFalse(itemLeaf.Bytes, "FILE", 157);
    }

    [Fact]
    public void EmptyLanguageDirectoryReturnsFalse()
    {
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU16(image["item-dir:FILE/157"] + 14, 0);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    // ------------------------------------------------------------------ 夸张的数量与大小

    [Fact]
    public void ExaggeratedEntryCountsReturnFalse()
    {
        SyntheticPe.Image named = SyntheticPe.Build(SyntheticPe.Standard());
        named.PatchU16(named["root"] + 12, 0xFFFF);
        AssertFalse(named.Bytes, "FILE", 157);

        SyntheticPe.Image ids = SyntheticPe.Build(SyntheticPe.Standard());
        ids.PatchU16(ids["root"] + 14, 0xFFFF);
        AssertFalse(ids.Bytes, "FILE", 157);

        // 命名项与编号项各自不多、合起来超过上限:按合计算。
        SyntheticPe.Image sum = SyntheticPe.Build(SyntheticPe.Standard());
        sum.PatchU16(sum["root"] + 12, PeResources.MaxDirectoryEntries);
        sum.PatchU16(sum["root"] + 14, 1);
        AssertFalse(sum.Bytes, "FILE", 157);

        // 第二层目录(类型目录)的条目数夸张同样拒绝。
        SyntheticPe.Image second = SyntheticPe.Build(SyntheticPe.Standard());
        second.PatchU16(second["type-dir:FILE"] + 14, 0xFFFF);
        AssertFalse(second.Bytes, "FILE", 157);
    }

    [Fact]
    public void DirectoryWithExactlyTheMaximumNumberOfEntriesIsAccepted()
    {
        // 根目录里 FILE + (上限 - 1) 个数字类型 = 刚好上限条:可以;再多一个:拒绝。
        SyntheticPe.ResType[] Types(int numbered)
        {
            var list = new List<SyntheticPe.ResType>
            {
                SyntheticPe.ResType.Named("FILE", new SyntheticPe.Item(157, new SyntheticPe.Leaf(0, Ascii("at-the-limit")))),
            };
            for (int i = 0; i < numbered; i++)
            {
                list.Add(SyntheticPe.ResType.Numbered(1000 + i));
            }
            return list.ToArray();
        }

        byte[] atLimit = SyntheticPe.Build(new SyntheticPe.Spec { Types = Types(PeResources.MaxDirectoryEntries - 1) }).Bytes;
        Assert.Equal(Ascii("at-the-limit"), Read(atLimit, "FILE", 157));

        byte[] overLimit = SyntheticPe.Build(new SyntheticPe.Spec { Types = Types(PeResources.MaxDirectoryEntries) }).Bytes;
        AssertFalse(overLimit, "FILE", 157);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData((uint)PeResources.MaxResourceBytes + 1u)]
    public void ExaggeratedResourceSizeReturnsFalse(uint size)
    {
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["data-entry:FILE/157/0"] + 4, size);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    [Fact]
    public void ResourceOfExactlyTheMaximumSizeIsReadAndOneMoreByteIsNot()
    {
        // 用"虚拟长流"(前面是合成映像,后面全是零)来表示 64 MiB 的资源,不必在磁盘上放那么大的文件。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(separate: true));
        int dataFile = image["data:FILE/157/0"];
        int dataSectionRaw = image["data-section"];
        int offsetInSection = dataFile - dataSectionRaw;
        image.PatchU32(image["section:2"] + 16, (uint)(offsetInSection + PeResources.MaxResourceBytes + 64));
        image.PatchU32(image["data-entry:FILE/157/0"] + 4, (uint)PeResources.MaxResourceBytes);
        using (var stream = new ZeroPaddedStream(image.Bytes, dataFile + (long)PeResources.MaxResourceBytes + 64))
        {
            Assert.True(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data));
            Assert.Equal(PeResources.MaxResourceBytes, data.Length);
            Assert.Equal(Ascii("FILE/157/first"), data.AsSpan(0, "FILE/157/first".Length).ToArray());
        }

        image.PatchU32(image["data-entry:FILE/157/0"] + 4, (uint)PeResources.MaxResourceBytes + 1u);
        using (var stream = new ZeroPaddedStream(image.Bytes, dataFile + (long)PeResources.MaxResourceBytes + 64))
        {
            Assert.False(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data));
            Assert.Null(data);
        }
    }

    // ------------------------------------------------------------------ 数据的 RVA

    [Fact]
    public void DataThatEndsExactlyAtTheSectionEndIsReadAndOneByteLessSectionIsNot()
    {
        // 数据节的原始大小正好到目标数据的最后一个字节:读得到;少 1 字节(文件里字节都在,但已超出节):读不到。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(separate: true));
        int sizeField = image["section:2"] + 16;
        uint exact = (uint)(image.DataEnd("FILE", 157, 0) - image["data-section"]);
        image.PatchU32(sizeField, exact);
        Assert.Equal(Ascii("FILE/157/first"), Read(image.Bytes, "FILE", 157));
        image.PatchU32(sizeField, exact - 1);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    [Fact]
    public void DataRvaOutsideEverySectionReturnsFalse()
    {
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard(separate: true));
        image.PatchU32(image["data-entry:FILE/157/0"], 0x70000000u);
        AssertFalse(image.Bytes, "FILE", 157);

        // 数据的 RVA 恰好等于它的"文件偏移"(把 RVA 当文件偏移的实现会读到数据):不在任何节的虚拟范围里,读不到。
        SyntheticPe.Image asOffset = SyntheticPe.Build(SyntheticPe.Standard(separate: true));
        asOffset.PatchU32(asOffset["data-entry:FILE/157/0"], (uint)asOffset["data:FILE/157/0"]);
        AssertFalse(asOffset.Bytes, "FILE", 157);
    }

    [Fact]
    public void StructuresOutsideTheDeclaredDirectorySizeReturnFalse()
    {
        // 数据目录声明的大小小到装不下目录结构:目录、名字串、数据项都必须落在声明的大小之内。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        int sizeField = image["resource-directory"] + 4;
        uint structureEnd = image.Relative("data-entry:FILE/43/0") + 16;   // 最后一个数据项的末尾
        image.PatchU32(sizeField, structureEnd);
        Assert.Equal(Ascii("FILE/157/first"), Read(image.Bytes, "FILE", 157));
        uint need = image.Relative("data-entry:FILE/157/0") + 16;            // 目标的数据项末尾
        image.PatchU32(sizeField, need);
        Assert.Equal(Ascii("FILE/157/first"), Read(image.Bytes, "FILE", 157));
        image.PatchU32(sizeField, need - 1);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    [Fact]
    public void NameOffsetOutsideTheDirectoryReturnsFalseInsteadOfSkippingTheEntry()
    {
        // 第一个命名项(FIL)的名字偏移越界:目录已经坏了,整体返回 false(不是跳过它继续找后面的 FILE)。
        SyntheticPe.Image image = SyntheticPe.Build(SyntheticPe.Standard());
        image.PatchU32(image["type-entry:FIL"], HighBit | 0x7FFF0000u);
        AssertFalse(image.Bytes, "FILE", 157);
    }

    // ------------------------------------------------------------------ 流的行为

    [Fact]
    public void NullArgumentsThrow()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => PeResources.TryReadNamedResource(null, "FILE", 157, out _));
        Assert.Throws<ArgumentNullException>(() => PeResources.TryReadNamedResource(stream, null, 157, out _));
    }

    [Fact]
    public void StreamThatCannotSeekReturnsFalse()
    {
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard()).Bytes;
        using var stream = new FaultyStream(image, canSeek: true, failAfterBytes: -1, seekable: false);
        Assert.False(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void StreamThatFailsMidReadReturnsFalseInsteadOfThrowing()
    {
        // 先量一次成功读取的总字节数 T;最后一次读是数据本身(长度 L)。流在累计读取量达到 k 之后的下一次读抛 IOException,
        // 对 0 ≤ k ≤ T-L 的每个 k,那次抛异常的读一定发生(最后一次读开始时累计量是 T-L),所以都必须返回 false 而不是把异常抛出来。
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard()).Bytes;
        long total;
        int last;
        using (var probe = new FaultyStream(image, canSeek: true, failAfterBytes: -1, seekable: true))
        {
            Assert.True(PeResources.TryReadNamedResource(probe, "FILE", 157, out byte[] ok));
            total = probe.TotalBytesRead;
            last = ok.Length;
        }
        Assert.Equal(14, last);
        for (int k = 0; k <= total - last; k++)
        {
            using var stream = new FaultyStream(image, canSeek: true, failAfterBytes: k, seekable: true);
            Assert.False(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data), $"累计读 {k} 字节之后失败应当返回 false");
            Assert.Null(data);
        }
    }

    [Fact]
    public void StreamLengthIsTheBoundaryForEveryRead()
    {
        // 流报告的 Length 比真实可读的字节少 1(数据正好在文件末尾):按 Length 判断,最后一个字节在边界之外,读不到;
        // Length 如实报告时读得到。(截断测试里的 MemoryStream 自己会读短,这里让流"能多读"以单独检验 Length 这道边界。)
        byte[] image = SyntheticPe.Build(SyntheticPe.Standard(separate: true)).Bytes;
        int end = SyntheticPe.Build(SyntheticPe.Standard(separate: true)).DataEnd("FILE", 157, 0);
        Assert.True(end < image.Length);   // 目标不是文件里最后一段数据:截短到目标末尾再试
        byte[] cut = new byte[end];
        Array.Copy(image, cut, end);
        using (var honest = new FaultyStream(cut, canSeek: true, failAfterBytes: -1, seekable: true))
        {
            Assert.True(PeResources.TryReadNamedResource(honest, "FILE", 157, out byte[] data));
            Assert.Equal(Ascii("FILE/157/first"), data);
        }
        using (var lying = new FaultyStream(cut, canSeek: true, failAfterBytes: -1, seekable: true, lengthAdjust: -1))
        {
            Assert.False(PeResources.TryReadNamedResource(lying, "FILE", 157, out byte[] data));
            Assert.Null(data);
        }
    }

    [Fact]
    public void DisposedStreamReturnsFalseInsteadOfThrowing()
    {
        var stream = new MemoryStream(SyntheticPe.Build(SyntheticPe.Standard()).Bytes);
        stream.Dispose();
        Assert.False(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void ReadsOnlyAFewKilobytesOfAHugeImage()
    {
        // .text 节有 8 MiB 填充:只读头、节表、目录与那一小段数据,总读取量远小于文件大小(不整文件读进内存)。
        SyntheticPe.Spec spec = SyntheticPe.Standard(separate: true);
        spec.TextBytes = 8 * 1024 * 1024;
        byte[] image = SyntheticPe.Build(spec).Bytes;
        Assert.True(image.Length > 8 * 1024 * 1024);
        using var stream = new FaultyStream(image, canSeek: true, failAfterBytes: -1, seekable: true);
        Assert.True(PeResources.TryReadNamedResource(stream, "FILE", 157, out byte[] data));
        Assert.Equal(Ascii("FILE/157/first"), data);
        Assert.InRange(stream.TotalBytesRead, 1, 16 * 1024);
    }

    /// <summary>包一层的内存流:可统计总读取字节数、可在读到一定量之后抛 IOException、可假装不能 Seek。</summary>
    private sealed class FaultyStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _failAfterBytes;
        private readonly bool _seekable;

        private readonly int _lengthAdjust;

        public FaultyStream(byte[] bytes, bool canSeek, int failAfterBytes, bool seekable, int lengthAdjust = 0)
        {
            _inner = new MemoryStream(bytes);
            _failAfterBytes = failAfterBytes;
            _seekable = seekable && canSeek;
            _lengthAdjust = lengthAdjust;
        }

        public long TotalBytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => _seekable;

        public override bool CanWrite => false;

        public override long Length => _inner.Length + _lengthAdjust;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_failAfterBytes >= 0 && TotalBytesRead >= _failAfterBytes)
            {
                throw new IOException("合成的读失败");
            }
            int n = _inner.Read(buffer, offset, count);
            TotalBytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>前面是真实字节、之后一直到 <c>totalLength</c> 都读出零的只读流(用来表示很大的映像而不占磁盘)。</summary>
    private sealed class ZeroPaddedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly long _length;
        private long _position;

        public ZeroPaddedStream(byte[] prefix, long totalLength)
        {
            _prefix = prefix;
            _length = totalLength;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long available = Math.Min(count, _length - _position);
            if (available <= 0)
            {
                return 0;
            }
            for (int i = 0; i < available; i++)
            {
                long at = _position + i;
                buffer[offset + i] = at < _prefix.Length ? _prefix[at] : (byte)0;
            }
            _position += available;
            return (int)available;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: _position = offset; break;
                case SeekOrigin.Current: _position += offset; break;
                default: _position = _length + offset; break;
            }
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
