using Dnf.Data;

namespace Dnf.Data.Tests;

public class PvfCipherTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // 期望值来自 tools/py/dnfdata.py 的 ror6_decrypt(Python 参考实现),不是本实现自己算的。
    [Theory]
    [InlineData(0u, "44960a4654861a5664b62a6674a63a7604d64a0614c65a1624f66a2634e67a36")]
    [InlineData(0x19036113u, "c09b6e0ad08b7e1ae0bb4e2af0ab5e3a80db2e4a90cb3e5aa0fb0e6ab0eb1e7a")]
    [InlineData(0xDEADBEEFu, "bf2070f9af3060e99f0050d98f1040c9ff6030b9ef7020a9df401099cf500089")]
    public void Decrypt_MatchesPythonReference(uint crc, string expectedHex)
    {
        byte[] data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        PvfCipher.DecryptInPlace(data, crc);
        Assert.Equal(expectedHex, Hex(data));
    }

    [Fact]
    public void Decrypt_LeavesTrailingBytesUntouched()
    {
        byte[] data = Enumerable.Range(0, 7).Select(i => (byte)i).ToArray();
        PvfCipher.DecryptInPlace(data, 5);
        Assert.Equal("44960a52040506", Hex(data));
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTrips()
    {
        var rng = new Random(12345);
        for (int length = 0; length < 70; length++)
        {
            byte[] original = new byte[length];
            rng.NextBytes(original);
            byte[] work = (byte[])original.Clone();
            uint crc = (uint)rng.Next();
            PvfCipher.EncryptInPlace(work, crc);
            PvfCipher.DecryptInPlace(work, crc);
            Assert.Equal(original, work);
        }
    }
}

public class PvfArchiveTests
{
    private static byte[] Content(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static (PvfArchive Archive, List<TestData.PvfFileSpec> Files, int Trailing) Make(int extraTreeBytes = 1)
    {
        var files = new List<TestData.PvfFileSpec>
        {
            new("stringtable.bin", Content(11, 1), 0x11111111u),
            new("etc/empty.txt", Array.Empty<byte>(), 0x22222222u),
            new("character/swordman/swordman.chr", Content(4, 2), 0x33333333u),
            new("Skill/Mixed/Case.skl", Content(7, 3), 0u),
            new("big/blob.bin", Content(4097, 4), 0xFFFFFFFFu),
        };
        (byte[] pvf, int trailing) = TestData.BuildPvf(files, extraTreeBytes: extraTreeBytes);
        return (new PvfArchive(new MemoryStream(pvf)), files, trailing);
    }

    [Fact]
    public void Open_ReadsHeaderAndDirectory()
    {
        (PvfArchive a, List<TestData.PvfFileSpec> files, int trailing) = Make();
        Assert.Equal(5, a.Count);
        Assert.Equal(66282u, a.Version);
        Assert.Equal(0x19036113u, a.DirectoryCrc);
        Assert.Equal(36, a.GuidBytes.Length);
        Assert.Equal(trailing, a.DirectoryTrailingBytes);
        for (int i = 0; i < files.Count; i++)
        {
            Assert.Equal(files[i].Path, a.Entries[i].Path);
            Assert.Equal((uint)files[i].Content.Length, a.Entries[i].Length);
            Assert.Equal(files[i].Crc, a.Entries[i].Crc);
        }
    }

    [Fact]
    public void Read_ReturnsExactPlaintext_ForEveryLengthAlignment()
    {
        (PvfArchive a, List<TestData.PvfFileSpec> files, _) = Make();
        foreach (TestData.PvfFileSpec f in files)
        {
            Assert.Equal(f.Content, a.Read(f.Path));
        }
    }

    [Fact]
    public void Lookup_IgnoresCase()
    {
        (PvfArchive a, _, _) = Make();
        Assert.True(a.TryGetEntry("skill/mixed/case.SKL", out PvfEntry e));
        Assert.Equal("Skill/Mixed/Case.skl", e.Path);
        Assert.False(a.TryGetEntry("nope/missing.txt", out _));
        Assert.False(a.TryGetEntry(null, out _));
        Assert.Throws<FileNotFoundException>(() => a.Read("nope/missing.txt"));
    }

    [Fact]
    public void Open_RejectsTruncatedDirectory()
    {
        (byte[] pvf, _) = TestData.BuildPvf(new List<TestData.PvfFileSpec> { new("a.txt", Content(8, 9), 7u) });
        byte[] cut = pvf.Take(60).ToArray();
        Assert.Throws<DnfFormatException>(() => new PvfArchive(new MemoryStream(cut)));
    }

    [Fact]
    public void Read_ThrowsWhenFileDataIsTruncated()
    {
        (byte[] pvf, _) = TestData.BuildPvf(new List<TestData.PvfFileSpec> { new("a.txt", Content(64, 5), 7u) });
        byte[] cut = pvf.Take(pvf.Length - 20).ToArray();
        var a = new PvfArchive(new MemoryStream(cut));
        Assert.Throws<DnfFormatException>(() => a.Read("a.txt"));
    }

    [Fact]
    public void Open_RejectsAbsurdGuidLength()
    {
        var bytes = new byte[64];
        BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(bytes, 0);
        Assert.Throws<DnfFormatException>(() => new PvfArchive(new MemoryStream(bytes)));
    }

    [Fact]
    public void Latin1Paths_RoundTripThroughDirectory()
    {
        string odd = "etc/caf\u00e9_\u00ff.txt"; // 0xE9、0xFF 逐字节映射
        (byte[] pvf, _) = TestData.BuildPvf(new List<TestData.PvfFileSpec> { new(odd, Content(5, 6), 1u) });
        var a = new PvfArchive(new MemoryStream(pvf));
        Assert.Equal(odd, a.Entries[0].Path);
        Assert.True(a.TryGetEntry(odd, out _));
    }
}

public class Crc32Tests
{
    private static byte[] Range32() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void StandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    // 期望值来自 Python 的 zlib.crc32(data, seed),不是本实现自己算的。
    [Theory]
    [InlineData(0u, 0x91267E8Au)]
    [InlineData(0xDEADBEEFu, 0xCE7EA316u)]
    [InlineData(334998u, 0x40C677E0u)]
    [InlineData(0xFFFFFFFFu, 0x77D3D4D8u)]
    public void SeededValues_MatchZlib(uint seed, uint expected)
    {
        Assert.Equal(expected, Crc32.Compute(Range32(), seed));
    }

    [Fact]
    public void EmptyInput_ReturnsTheSeed()
    {
        Assert.Equal(12345u, Crc32.Compute(ReadOnlySpan<byte>.Empty, 12345u));
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Seed_ContinuesAPreviousCrc()
    {
        byte[] a = "hello, "u8.ToArray();
        byte[] b = "world"u8.ToArray();
        Assert.Equal(Crc32.Compute(a.Concat(b).ToArray()), Crc32.Compute(b, Crc32.Compute(a)));
    }
}

/// <summary>
/// 客户端的校验规则(Ghidra 静态分析得出,在真实数据全部 334,998 个文件上成立,见证据台账 E-011):
/// 文件的 crc = CRC-32(解密后含对齐填充的整段,种子 = fileNo),目录树的 crc = CRC-32(整段,种子 = 文件数),
/// 同一个 crc 也是解密密钥。这里用合成数据检验库对这套规则的实现,包括"改一个字节就失败"。
/// </summary>
public class PvfArchiveChecksumTests
{
    private static byte[] Content(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static List<TestData.PvfFileSpec> Specs() => new()
    {
        new("a/one.txt", Content(11, 1), 0u),    // 不是 4 的倍数,有 1 字节填充
        new("a/two.bin", Content(16, 2), 0u),    // 恰好对齐
        new("a/empty.txt", Array.Empty<byte>(), 0u),
        new("b/big.dat", Content(4099, 3), 0u),  // 末尾有 1 字节填充
    };

    [Fact]
    public void ValidArchive_PassesDirectoryAndEveryFileCheck()
    {
        (byte[] pvf, int trailing) = TestData.BuildPvf(Specs(), validChecksums: true);
        using var a = new PvfArchive(new MemoryStream(pvf));
        Assert.True(a.DirectoryChecksumValid);
        Assert.Equal(trailing, a.DirectoryTrailingBytes);
        Assert.Equal(4, a.Count);
        foreach (PvfEntry e in a.Entries)
        {
            byte[] data = a.Read(e, out bool ok);
            Assert.True(ok, e.Path);
            Assert.Equal(data, a.ReadVerified(e));
        }
    }

    [Fact]
    public void ValidArchive_FileNumbersAreNotJustTheIndex()
    {
        (byte[] pvf, _) = TestData.BuildPvf(Specs(), validChecksums: true);
        using var a = new PvfArchive(new MemoryStream(pvf));
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(TestData.FileNo(i), a.Entries[i].FileNumber);
        }
        Assert.NotEqual(0u, a.Entries[0].FileNumber);
    }

    [Fact]
    public void ChecksumSeedIsTheFileNumber()
    {
        (byte[] pvf, _) = TestData.BuildPvf(Specs(), validChecksums: true);
        using var a = new PvfArchive(new MemoryStream(pvf));
        PvfEntry e = a.Entries[0];
        a.Read(e, out bool ok);
        Assert.True(ok);
        var wrongSeed = new PvfEntry(e.Path, e.FileNumber + 1, e.Length, e.Crc, e.Offset);
        a.Read(wrongSeed, out bool okWrong);
        Assert.False(okWrong);
    }

    [Fact]
    public void CorruptedPaddingByte_FailsOnlyThatFile_AndContentStaysReadable()
    {
        List<TestData.PvfFileSpec> specs = Specs();
        (byte[] pvf, _) = TestData.BuildPvf(specs, validChecksums: true);
        // 最后一个字节属于最后一个文件(big.dat)的对齐填充。解密按字做双射,翻转的位落在填充字节上,内容本身不变。
        pvf[pvf.Length - 1] ^= 0x40;
        using var a = new PvfArchive(new MemoryStream(pvf));
        Assert.True(a.DirectoryChecksumValid);
        for (int i = 0; i < 3; i++)
        {
            a.Read(a.Entries[i], out bool ok);
            Assert.True(ok, a.Entries[i].Path);
        }
        byte[] big = a.Read(a.Entries[3], out bool bigOk);
        Assert.False(bigOk);
        Assert.Equal(specs[3].Content, big); // 不校验的读取仍然给出内容,库不替调用方做决定
        Assert.Equal(specs[3].Content, a.Read(a.Entries[3]));
        Assert.Throws<DnfFormatException>(() => a.ReadVerified(a.Entries[3]));
    }

    [Fact]
    public void CorruptedDirectory_FailsDirectoryCheck()
    {
        (byte[] pvf, _) = TestData.BuildPvf(Specs(), validChecksums: true);
        // 头长 = 4 + 36 + 16 = 56,目录树紧随其后;改目录树里的一个字节(解密后是一整个字的变化)。
        pvf[56 + 8] ^= 0x01;
        using var a = new PvfArchive(new MemoryStream(pvf));
        Assert.False(a.DirectoryChecksumValid);
    }

    [Fact]
    public void LegacySyntheticArchive_ReadsFine_ButFailsClientChecks()
    {
        var specs = new List<TestData.PvfFileSpec> { new("x.txt", Content(10, 8), 0x12345678u) };
        (byte[] pvf, _) = TestData.BuildPvf(specs); // 任意密钥,0xEE 填充:不满足客户端规则
        using var a = new PvfArchive(new MemoryStream(pvf));
        Assert.False(a.DirectoryChecksumValid);
        Assert.Equal(specs[0].Content, a.Read(a.Entries[0]));
        a.Read(a.Entries[0], out bool ok);
        Assert.False(ok);
    }
}

public class StringTableTests
{
    [Fact]
    public void Indexes_IncludingEmptyAndHighBytes()
    {
        var strings = new List<byte[]> { "[name]"u8.ToArray(), Array.Empty<byte>(), new byte[] { 0x61, 0xE9, 0xFF }, "item/icon.img"u8.ToArray() };
        var t = new PvfStringTable(TestData.BuildStringTable(strings));
        Assert.Equal(4, t.Count);
        Assert.Equal("[name]", t[0]);
        Assert.Equal(string.Empty, t[1]);
        Assert.Equal("a\u00e9\u00ff", t[2]);
        Assert.Equal("item/icon.img", t[3]);
        Assert.Same(t[3], t[3]); // 缓存
        Assert.Equal(new byte[] { 0x61, 0xE9, 0xFF }, t.GetBytes(2).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => t[4]);
    }

    [Fact]
    public void RejectsBrokenOffsets()
    {
        byte[] good = TestData.BuildStringTable(new List<byte[]> { "ab"u8.ToArray(), "cd"u8.ToArray() });
        // 把第二个偏移改小,让偏移不再单调
        byte[] bad = (byte[])good.Clone();
        BitConverter.GetBytes(0u).CopyTo(bad, 4 + 4);
        Assert.Throws<DnfFormatException>(() => new PvfStringTable(bad));
        Assert.Throws<DnfFormatException>(() => new PvfStringTable(new byte[4]));
    }
}

public class TokenScriptTests
{
    [Fact]
    public void Parse_ReadsTypesAndValues()
    {
        byte[] data = TestData.BuildTokenScript(new (byte, int)[]
        {
            (5, 17), (2, -3), (4, BitConverter.SingleToInt32Bits(1.5f)), (7, 99), (8, 0), (9, 1), (10, 2),
        });
        ScriptToken[] t = TokenScript.Parse(data);
        Assert.Equal(7, t.Length);
        Assert.Equal((byte)TokenType.Tag, t[0].Type);
        Assert.Equal(17, t[0].Value);
        Assert.Equal(-3, t[1].Value);
        Assert.Equal(1.5f, t[2].AsFloat);
        Assert.Equal((byte)TokenType.StringRefKey, t[6].Type);
    }

    [Fact]
    public void Parse_Empty_IsZeroTokens()
    {
        Assert.Empty(TokenScript.Parse(new byte[] { 0xB0, 0xD0 }));
    }

    [Fact]
    public void Parse_RejectsWrongMagicAndBadLength()
    {
        Assert.False(TokenScript.IsTokenScript(new byte[] { 0x5B, 0x6E }));
        Assert.Throws<DnfFormatException>(() => TokenScript.Parse(new byte[] { 0x5B, 0x6E, 0, 0, 0, 0, 0 }));
        Assert.Throws<DnfFormatException>(() => TokenScript.Parse(new byte[] { 0xB0, 0xD0, 5, 0, 0 }));
    }
}
