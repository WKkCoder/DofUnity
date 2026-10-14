using Dnf.Data;

namespace Dnf.Data.Tests;

public class AniFileTests
{
    private static TestData.AniFrameSpec Frame(int image, int frame, int x, int y, params (ushort Id, byte[] Payload)[] props)
    {
        var f = new TestData.AniFrameSpec { ImageIndex = image, FrameIndex = frame, X = x, Y = y };
        f.Props.AddRange(props);
        return f;
    }

    private static byte[] Sample()
    {
        var f0 = Frame(0, 7, -5, 12,
            (12, TestData.LE32(100)),                                  // 延时
            (11, new byte[] { 2, 0 }),                                 // 图形特效,类型 2
            (16, TestData.Str("HOLD")),                                // 字符串属性
            (9, new byte[] { 255, 128, 0, 255 }));
        f0.Boxes.Add((14, new[] { 1, 2, 3, 4, 5, 6 }));
        f0.Boxes.Add((15, new[] { -1, -2, -3, -4, -5, -6 }));

        var f1 = Frame(-1, 0, 0, 0,
            (11, new byte[] { 5, 0, 10, 20, 30 }),                      // 类型 5 后面多 3 字节
            (17, Array.Empty<byte>()));

        var f2 = Frame(1, 0, 1, 2);

        return TestData.BuildAni(
            new[] { "Monster/Test/Body.img", "Monster/Test/Fx%02d.img" },
            new (ushort, byte[])[] { (3, new byte[] { 1, 0 }), (18, new byte[15]) },
            new[] { f0, f1, f2 });
    }

    [Fact]
    public void Parse_ReadsAllSections()
    {
        AniFile a = AniFile.Parse(Sample());
        Assert.Equal(new[] { "Monster/Test/Body.img", "Monster/Test/Fx%02d.img" }, a.ImagePaths);
        Assert.Equal(2, a.GlobalProperties.Length);
        Assert.Equal(3, a.GlobalProperties[0].Id);
        Assert.Equal(15, a.GlobalProperties[1].Payload.Length);
        Assert.Equal(3, a.Frames.Length);

        AniFrame f0 = a.Frames[0];
        Assert.Equal(0, f0.ImageIndex);
        Assert.Equal(7, f0.FrameIndex);
        Assert.Equal(-5, f0.X);
        Assert.Equal(12, f0.Y);
        Assert.Equal(2, f0.Boxes.Length);
        Assert.Equal(14, f0.Boxes[0].Type);
        Assert.Equal(6, f0.Boxes[0].V5);
        Assert.Equal(-6, f0.Boxes[1].V5);
        Assert.Equal(4, f0.Properties.Length);
        Assert.True(f0.TryGetDelayMs(out int delay));
        Assert.Equal(100, delay);
        Assert.Equal("HOLD", System.Text.Encoding.ASCII.GetString(f0.Properties[2].Payload, 4, 4));

        AniFrame f1 = a.Frames[1];
        Assert.Equal(-1, f1.ImageIndex);
        Assert.Equal(-1, f1.FrameIndex);
        Assert.Equal(5, f1.Properties[0].Payload.Length);
        Assert.False(f1.TryGetDelayMs(out _));

        Assert.Equal(1, a.Frames[2].ImageIndex);
        Assert.Empty(a.Frames[2].Properties);
    }

    [Fact]
    public void FrameAccessors_ReadScaleRotationAndTint()
    {
        var scaled = Frame(0, 0, 0, 0,
            (7, BitConverter.GetBytes(-1.0f).Concat(BitConverter.GetBytes(1.5f)).ToArray()),
            (8, BitConverter.GetBytes(1.5707964f)),
            (9, new byte[] { 255, 150, 0, 170 }));
        var plain = Frame(0, 0, 0, 0);
        AniFile a = AniFile.Parse(TestData.BuildAni(new[] { "a.img" }, Array.Empty<(ushort, byte[])>(), new[] { scaled, plain }));

        Assert.True(a.Frames[0].TryGetScale(out float sx, out float sy));
        Assert.Equal((-1.0f, 1.5f), (sx, sy));
        Assert.True(a.Frames[0].TryGetRotationRadians(out float rot));
        Assert.Equal(1.5707964f, rot);
        Assert.True(a.Frames[0].TryGetTint(out byte r, out byte g, out byte b, out byte al));
        Assert.Equal((255, 150, 0, 170), (r, g, b, al));

        // 没有属性时返回 false,并给出"什么都不做"的默认值
        Assert.False(a.Frames[1].TryGetScale(out sx, out sy));
        Assert.Equal((1.0f, 1.0f), (sx, sy));
        Assert.False(a.Frames[1].TryGetRotationRadians(out rot));
        Assert.Equal(0f, rot);
        Assert.False(a.Frames[1].TryGetTint(out r, out g, out b, out al));
        Assert.Equal((255, 255, 255, 255), (r, g, b, al));
    }

    [Fact]
    public void Parse_RejectsTrailingBytes()
    {
        byte[] data = Sample().Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<DnfFormatException>(() => AniFile.Parse(data));
    }

    [Fact]
    public void Parse_RejectsTruncation_AtEveryCutPoint()
    {
        byte[] data = Sample();
        for (int cut = 0; cut < data.Length; cut++)
        {
            byte[] part = data.Take(cut).ToArray();
            Assert.Throws<DnfFormatException>(() => AniFile.Parse(part));
        }
    }

    [Fact]
    public void Parse_RejectsUnknownProperties()
    {
        var badFrame = Frame(0, 0, 0, 0, (99, new byte[4]));
        byte[] data = TestData.BuildAni(new[] { "a.img" }, Array.Empty<(ushort, byte[])>(), new[] { badFrame });
        Assert.Throws<DnfFormatException>(() => AniFile.Parse(data));

        byte[] badGlobal = TestData.BuildAni(new[] { "a.img" }, new (ushort, byte[])[] { (77, new byte[2]) }, new[] { Frame(0, 0, 0, 0) });
        Assert.Throws<DnfFormatException>(() => AniFile.Parse(badGlobal));
    }

    [Fact]
    public void Parse_RejectsImageIndexOutOfRange()
    {
        byte[] data = TestData.BuildAni(new[] { "a.img" }, Array.Empty<(ushort, byte[])>(), new[] { Frame(3, 0, 0, 0) });
        Assert.Throws<DnfFormatException>(() => AniFile.Parse(data));
    }

    [Fact]
    public void Parse_RejectsTooShort()
    {
        Assert.Throws<DnfFormatException>(() => AniFile.Parse(new byte[7]));
    }
}
