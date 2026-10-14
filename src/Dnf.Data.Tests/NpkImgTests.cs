using Dnf.Data;

namespace Dnf.Data.Tests;

public class NpkArchiveTests
{
    [Fact]
    public void Open_DecodesNamesAndReadsData()
    {
        byte[] a = { 1, 2, 3, 4, 5 };
        byte[] b = Array.Empty<byte>();
        byte[] c = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        byte[] npk = TestData.BuildNpk(new (string, byte[])[]
        {
            ("sprite/character/swordman/effect/slash.img", a),
            ("sprite/monster/empty.img", b),
            ("sprite/interface/" + new string('x', 200) + ".img", c),
        });
        using var archive = new NpkArchive(new MemoryStream(npk));
        Assert.Equal(3, archive.Count);
        Assert.Equal("sprite/character/swordman/effect/slash.img", archive.Entries[0].Name);
        Assert.Equal("sprite/monster/empty.img", archive.Entries[1].Name);
        Assert.Equal("sprite/interface/" + new string('x', 200) + ".img", archive.Entries[2].Name);
        Assert.Equal(a, archive.Read(archive.Entries[0]));
        Assert.Equal(b, archive.Read(archive.Entries[1]));
        Assert.Equal(c, archive.Read(archive.Entries[2]));
    }

    [Fact]
    public void Open_RejectsBadMagicAndTruncation()
    {
        byte[] npk = TestData.BuildNpk(new (string, byte[])[] { ("a.img", new byte[] { 9 }) });
        byte[] bad = (byte[])npk.Clone();
        bad[0] = (byte)'X';
        Assert.Throws<DnfFormatException>(() => new NpkArchive(new MemoryStream(bad)));
        Assert.Throws<DnfFormatException>(() => new NpkArchive(new MemoryStream(npk.Take(100).ToArray())));
        Assert.Throws<DnfFormatException>(() => new NpkArchive(new MemoryStream(new byte[5])));
    }

    [Fact]
    public void Open_RejectsHugeEntryCountWithoutOverflow()
    {
        // 头里声称有 0xFFFFFFFF 个条目,但文件只有 20 字节。必须抛格式异常,而不是整数溢出。
        byte[] head = TestData.BuildNpk(Array.Empty<(string, byte[])>()).Take(20).ToArray();
        head[16] = head[17] = head[18] = head[19] = 0xFF;
        Assert.Throws<DnfFormatException>(() => new NpkArchive(new MemoryStream(head)));
    }

    [Fact]
    public void Read_ThrowsWhenEntryDataMissing()
    {
        byte[] npk = TestData.BuildNpk(new (string, byte[])[] { ("a.img", new byte[100]) });
        using var archive = new NpkArchive(new MemoryStream(npk.Take(npk.Length - 10).ToArray()));
        Assert.Throws<DnfFormatException>(() => archive.Read(archive.Entries[0]));
    }
}

public class ImgFileTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static byte[] Pixels16(params ushort[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++)
        {
            BitConverter.GetBytes(v[i]).CopyTo(b, i * 2);
        }
        return b;
    }

    // 期望值来自 tools/py/dnfdata.py 的 to_rgba(Python 参考实现)。
    [Fact]
    public void Argb4444_MatchesPythonReference()
    {
        byte[] raw = Pixels16(0x0000, 0xF123, 0x8421, 0x1234, 0xFFFF);
        Assert.Equal("00000000112233ff4422118822334411ffffffff", Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb4444, raw, 5)));
    }

    [Fact]
    public void Argb1555_MatchesPythonReference()
    {
        byte[] raw = Pixels16(0x0000, 0x8000, 0x83E0, 0x7FFF, 0xFFFF, 0x3C1F);
        Assert.Equal("00000000000000ff00ff00ffffffff00ffffffff7b00ff00", Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb1555, raw, 6)));
    }

    [Fact]
    public void Argb1555_ExpandsFiveBitChannelsByRoundingLikeTheOriginalClient()
    {
        // 5 位值 3、7、24、28 在位复制下是 24、57、198、231,而原版屏幕输出里是 25、58、197、230(证据 E-023)。
        // 像素 0x8CF8:r=3 g=7 b=24;像素 0xF303:r=28 g=24 b=3。
        byte[] raw = Pixels16(0x8CF8, 0xF303);
        Assert.Equal("193ac5ffe6c519ff", Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb1555, raw, 2)));
    }

    [Fact]
    public void Argb1555_FiveBitExpansionIsRoundedNormalization()
    {
        for (int v = 0; v < 32; v++)
        {
            // 红色通道放在 bit 10..14,A 位为 1
            byte[] raw = Pixels16((ushort)(0x8000 | (v << 10)));
            byte red = ImgPixels.ToRgba(ImgPixelFormat.Argb1555, raw, 1)[0];
            Assert.Equal((byte)Math.Round(v * 255.0 / 31.0, MidpointRounding.AwayFromZero), red);
        }
    }

    [Fact]
    public void Argb8888_SwapsBlueAndRed()
    {
        byte[] raw = { 1, 2, 3, 4, 255, 0, 128, 255, 9, 8, 7, 6 };
        Assert.Equal("030201048000ffff07080906", Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb8888, raw, 3)));
    }

    [Fact]
    public void ToRgba_RejectsShortInputAndUnknownType()
    {
        Assert.Throws<DnfFormatException>(() => ImgPixels.ToRgba(ImgPixelFormat.Argb8888, new byte[7], 2));
        Assert.Throws<DnfFormatException>(() => ImgPixels.ToRgba(0x42, new byte[8], 1));
    }

    [Fact]
    public void SixteenBitRawFrame_SizeFieldIsDoubled_ButFramesStayAligned()
    {
        // 第一帧是 1555 未压缩,索引里的 size 字段是实际的两倍(真实 IMG 的习惯)。
        // 若错用 size 推进偏移,第二帧会错位。
        byte[] p1 = Pixels16(0x83E0, 0x7FFF, 0xFFFF, 0x0000);
        byte[] p2 = { 10, 20, 30, 40, 50, 60, 70, 80 };
        byte[] img = TestData.BuildImg(new[]
        {
            TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb1555, 2, 2, p1),
            TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 2, 1, p2),
        });
        ImgFile f = ImgFile.Parse(img);
        Assert.Equal(img.Length, f.ExpectedLength);
        Assert.Equal(2 * 2 * 4, f.GetFrame(0).Size);
        Assert.Equal(2 * 2 * 2, ImgFile.StoredLength(f.GetFrame(0)));
        Assert.Equal("00ff00ffffffff00ffffffff00000000", Hex(f.DecodeRgba(0)));
        Assert.Equal("1e140a28463c3250", Hex(f.DecodeRgba(1)));
        Assert.Equal(3, f.GetFrame(0).X);
        Assert.Equal(-4, f.GetFrame(0).Y);
        Assert.Equal(12, f.GetFrame(0).FrameWidth);
    }

    [Fact]
    public void ZlibFrame_DecodesAndVerifiesChecksum()
    {
        var rng = new Random(7);
        byte[] raw = new byte[40 * 30 * 4];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(rng.Next(4) * 60); // 有重复,能压缩
        }
        byte[] img = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Zlib(ImgPixelFormat.Argb8888, 40, 30, raw) });
        ImgFile f = ImgFile.Parse(img);
        Assert.Equal(img.Length, f.ExpectedLength);
        byte[] decoded = f.DecodeRaw(0, out ImgDecodeStatus status, verifyChecksum: true);
        Assert.Equal(ImgDecodeStatus.Ok, status);
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void ZlibFrame_BadChecksumIsDetectedOnlyWhenAsked()
    {
        byte[] raw = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        byte[] img = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Zlib(ImgPixelFormat.Argb8888, 4, 4, raw) });
        img[img.Length - 1] ^= 0x55; // 破坏 Adler-32 的最后一个字节
        ImgFile f = ImgFile.Parse(img);
        Assert.Equal(raw, f.DecodeRaw(0)); // 运行时不校验
        Assert.Throws<DnfFormatException>(() => f.DecodeRaw(0, out _, verifyChecksum: true));
    }

    [Fact]
    public void LinkFrames_FollowTargets_AndRejectLoops()
    {
        byte[] p = { 1, 2, 3, 4 };
        byte[] img = TestData.BuildImg(new[]
        {
            TestData.ImgFrameSpec.Link(2),
            TestData.ImgFrameSpec.Link(0),
            TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 1, 1, p),
        });
        ImgFile f = ImgFile.Parse(img);
        Assert.Equal(2, f.ResolveLink(0));
        Assert.Equal(2, f.ResolveLink(1));
        Assert.Equal(f.DecodeRgba(2), f.DecodeRgba(1));
        Assert.Equal(3, f.FrameCount);

        byte[] loop = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Link(1), TestData.ImgFrameSpec.Link(0) });
        Assert.Throws<DnfFormatException>(() => ImgFile.Parse(loop).ResolveLink(0));
        byte[] outOfRange = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Link(9) });
        Assert.Throws<DnfFormatException>(() => ImgFile.Parse(outOfRange).ResolveLink(0));
    }

    [Fact]
    public void TruncatedOneByOneFrame_IsTransparentPlaceholder()
    {
        // 真实数据里有 97 个这样的帧:1×1、类型 0x0E、compress 6、size 4,zlib 流被截断。
        var placeholder = new TestData.ImgFrameSpec
        {
            Type = ImgPixelFormat.Argb1555, Compress = 6, Width = 1, Height = 1, SizeField = 4,
            Stored = new byte[] { 0x78, 0x9C, 0x63, 0x00 }, FrameWidth = 1, FrameHeight = 1,
        };
        ImgFile f = ImgFile.Parse(TestData.BuildImg(new[] { placeholder }));
        byte[] raw = f.DecodeRaw(0, out ImgDecodeStatus status, verifyChecksum: true);
        Assert.Equal(ImgDecodeStatus.TruncatedPlaceholder, status);
        Assert.Equal(new byte[2], raw);
        Assert.Equal("00000000", Hex(f.DecodeRgba(0)));
    }

    [Fact]
    public void TruncatedLargerFrame_Throws()
    {
        byte[] z = TestData.ZlibCompress(Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray());
        var cut = new TestData.ImgFrameSpec
        {
            Type = ImgPixelFormat.Argb8888, Compress = 6, Width = 4, Height = 4, SizeField = 10,
            Stored = z.Take(10).ToArray(), FrameWidth = 4, FrameHeight = 4,
        };
        ImgFile f = ImgFile.Parse(TestData.BuildImg(new[] { cut }));
        Assert.Throws<DnfFormatException>(() => f.DecodeRaw(0));
    }

    [Fact]
    public void EmptyFrame_DecodesToEmptyArray()
    {
        byte[] img = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb4444, 0, 5, Array.Empty<byte>()) });
        ImgFile f = ImgFile.Parse(img);
        Assert.Empty(f.DecodeRgba(0));
    }

    [Fact]
    public void Parse_RejectsBadHeaders()
    {
        byte[] good = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 1, 1, new byte[4]) });

        byte[] badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<DnfFormatException>(() => ImgFile.Parse(badMagic));

        byte[] badVersion = (byte[])good.Clone();
        badVersion[24] = 1;
        Assert.Throws<DnfFormatException>(() => ImgFile.Parse(badVersion));

        byte[] badIndexSize = (byte[])good.Clone();
        badIndexSize[16] += 4; // 头里的 indexSize 与实际索引长度不符
        Assert.Throws<DnfFormatException>(() => ImgFile.Parse(badIndexSize));

        Assert.Throws<DnfFormatException>(() => ImgFile.Parse(new byte[10]));
    }

    [Fact]
    public void Detects_V2_AndLegacyMagics()
    {
        byte[] v2 = TestData.BuildImg(new[] { TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 1, 1, new byte[4]) });
        Assert.True(ImgFile.IsV2(v2));
        Assert.False(ImgFile.IsLegacy(v2));
        byte[] legacy = System.Text.Encoding.ASCII.GetBytes("Neople Image File\0\0\0\0");
        Assert.True(ImgFile.IsLegacy(legacy));
        Assert.False(ImgFile.IsV2(legacy));
    }

    [Fact]
    public void UnknownCompress_Throws()
    {
        var weird = new TestData.ImgFrameSpec
        {
            Type = ImgPixelFormat.Argb8888, Compress = 7, Width = 1, Height = 1, SizeField = 4,
            Stored = new byte[4], FrameWidth = 1, FrameHeight = 1,
        };
        ImgFile f = ImgFile.Parse(TestData.BuildImg(new[] { weird }));
        Assert.Throws<DnfFormatException>(() => f.DecodeRaw(0));
    }
}

public class Adler32Tests
{
    [Fact]
    public void KnownValues()
    {
        Assert.Equal(1u, Adler32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x11E60398u, Adler32.Compute("Wikipedia"u8)); // RFC 1950 常见示例
    }

    [Fact]
    public void LongInput_DoesNotOverflow()
    {
        byte[] data = Enumerable.Repeat((byte)0xFF, 200_000).ToArray();
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        uint expected = (b << 16) | a;
        Assert.Equal(expected, Adler32.Compute(data));
    }
}
