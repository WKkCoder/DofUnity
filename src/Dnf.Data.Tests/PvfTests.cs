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
