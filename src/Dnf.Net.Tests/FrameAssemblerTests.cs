using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>TCP 流重组(E-202,规则照服务端收包函数的步骤):半包、粘包、逐字节、长度越界、错误粘住。帧内容是合成的,只有头里的长度有意义。</summary>
public sealed class FrameAssemblerTests
{
    // 造一个整帧长度为 length 的帧:头按布局写,头后面用可辨认的填充字节
    private static byte[] MakeFrame(FrameLayout layout, int length, ushort id, byte fill)
    {
        byte[] f = new byte[length];
        for (int i = layout.HeaderLength; i < length; i++)
        {
            f[i] = (byte)(fill + i);
        }
        PacketHeader h = new PacketHeader { ClassByte = 1, PacketId = id, TotalLength = (uint)length, Check = 0x11223344, Second = 0x55 };
        h.TryWrite(layout, f);
        return f;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        return parts.SelectMany(p => p).ToArray();
    }

    private static readonly FrameLayout C2S = FrameLayout.ClientToServer;
    private static readonly FrameLayout S2C = FrameLayout.ServerToClient;

    [Fact]
    public void SingleFrameInOneChunk()
    {
        byte[] f = MakeFrame(C2S, 29, 7, 1);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.None, a.Feed(f, 0, f.Length, outp));
        RawFrame r = Assert.Single(outp);
        Assert.Equal(f, r.Bytes);
        Assert.Equal((ushort)7, r.Header.PacketId);
        Assert.Equal(29u, r.Header.TotalLength);
        Assert.Equal(0, a.PendingBytes);
    }

    [Fact]
    public void FrameOfExactlyTheMinimumLengthHasNoBody()
    {
        byte[] f = MakeFrame(C2S, 13, 1, 0);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.None, a.Feed(f, 0, 13, outp));
        Assert.Equal(13, Assert.Single(outp).Bytes.Length);
    }

    [Fact]
    public void FrameOfExactlyTheMaximumLengthIsAccepted()
    {
        byte[] f = MakeFrame(C2S, 0x578, 9, 3);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.None, a.Feed(f, 0, f.Length, outp));
        Assert.Equal(0x578, Assert.Single(outp).Bytes.Length);
    }

    [Fact]
    public void LengthBelowTheMinimumIsRejectedAsSoonAsThirteenBytesArrive()
    {
        byte[] f = MakeFrame(C2S, 13, 1, 0);
        f[3] = 12;       // 总长写成 12
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.LengthBelowMinimum, a.Feed(f, 0, 13, outp));
        Assert.Empty(outp);
        Assert.Equal(12u, a.OffendingLength);
        Assert.Equal(FrameError.LengthBelowMinimum, a.Error);
    }

    [Fact]
    public void LengthAboveTheMaximumIsRejectedWithoutWaitingForTheBody()
    {
        byte[] f = MakeFrame(C2S, 13, 1, 0);
        f[3] = 0x79;
        f[4] = 0x05;     // 总长 0x579
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.LengthAboveMaximum, a.Feed(f, 0, 13, outp));
        Assert.Equal(0x579u, a.OffendingLength);
    }

    [Fact]
    public void HugeAndNegativeLookingLengthsAreAboveTheMaximumNotACrash()
    {
        foreach (uint huge in new uint[] { 0x80000000u, 0xFFFFFFFFu, 0x7FFFFFFFu })
        {
            byte[] f = MakeFrame(C2S, 13, 1, 0);
            f[3] = (byte)huge;
            f[4] = (byte)(huge >> 8);
            f[5] = (byte)(huge >> 16);
            f[6] = (byte)(huge >> 24);
            FrameAssembler a = new FrameAssembler(C2S);
            Assert.Equal(FrameError.LengthAboveMaximum, a.Feed(f, 0, 13, new List<RawFrame>()));
        }
    }

    [Fact]
    public void FewerThanTheMinimumBytesAreNeverParsed()
    {
        // 服务端:累计不足 13 字节只记账,哪怕头里的长度已经读得出来(这里长度字段 = 0,本应是错误)
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        byte[] twelve = new byte[12];
        Assert.Equal(FrameError.None, a.Feed(twelve, 0, 12, outp));
        Assert.Equal(12, a.PendingBytes);
        // 再来 1 个字节凑到 13,才会读头,长度 0 < 13 报错
        Assert.Equal(FrameError.LengthBelowMinimum, a.Feed(new byte[1], 0, 1, outp));
    }

    [Fact]
    public void ServerToClientLayoutRejectsLengthsShorterThanItsHeader()
    {
        byte[] f = MakeFrame(S2C, 15, 1, 0);
        f[3] = 14;
        FrameAssembler a = new FrameAssembler(S2C);
        Assert.Equal(FrameError.LengthBelowMinimum, a.Feed(f, 0, 15, new List<RawFrame>()));
        // 同样的 13 字节前缀在 15 字节头的布局里还不够读头:继续等
        FrameAssembler b = new FrameAssembler(S2C);
        Assert.Equal(FrameError.None, b.Feed(f, 0, 13, new List<RawFrame>()));
        Assert.Equal(13, b.PendingBytes);
    }

    [Fact]
    public void ThreeFramesInOneChunk()
    {
        byte[] a1 = MakeFrame(C2S, 13, 1, 1);
        byte[] a2 = MakeFrame(C2S, 40, 2, 2);
        byte[] a3 = MakeFrame(C2S, 0x100, 3, 3);
        byte[] stream = Concat(a1, a2, a3);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.None, a.Feed(stream, 0, stream.Length, outp));
        Assert.Equal(3, outp.Count);
        Assert.Equal(a1, outp[0].Bytes);
        Assert.Equal(a2, outp[1].Bytes);
        Assert.Equal(a3, outp[2].Bytes);
        Assert.Equal(0, a.PendingBytes);
    }

    [Fact]
    public void EverySplitPointGivesTheSameFrames()
    {
        byte[] stream = Concat(MakeFrame(C2S, 13, 1, 1), MakeFrame(C2S, 47, 2, 2), MakeFrame(C2S, 20, 3, 3));
        for (int cut = 0; cut <= stream.Length; cut++)
        {
            FrameAssembler a = new FrameAssembler(C2S);
            List<RawFrame> outp = new();
            Assert.Equal(FrameError.None, a.Feed(stream, 0, cut, outp));
            Assert.Equal(FrameError.None, a.Feed(stream, cut, stream.Length - cut, outp));
            Assert.Equal(3, outp.Count);
            Assert.Equal(new ushort[] { 1, 2, 3 }, outp.Select(r => r.Header.PacketId).ToArray());
            Assert.Equal(new[] { 13, 47, 20 }, outp.Select(r => r.Bytes.Length).ToArray());
            Assert.Equal(0, a.PendingBytes);
        }
    }

    [Fact]
    public void OneByteAtATimeGivesTheSameFrames()
    {
        byte[] stream = Concat(MakeFrame(S2C, 15, 5, 1), MakeFrame(S2C, 300, 6, 2), MakeFrame(S2C, 16, 7, 3));
        FrameAssembler a = new FrameAssembler(S2C);
        List<RawFrame> outp = new();
        for (int i = 0; i < stream.Length; i++)
        {
            Assert.Equal(FrameError.None, a.Feed(stream, i, 1, outp));
        }
        Assert.Equal(new ushort[] { 5, 6, 7 }, outp.Select(r => r.Header.PacketId).ToArray());
        Assert.Equal(stream, Concat(outp.Select(r => r.Bytes).ToArray()));
    }

    [Fact]
    public void PartialFrameIsHeldUntilTheLastByteArrives()
    {
        byte[] f = MakeFrame(C2S, 50, 4, 9);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        a.Feed(f, 0, 49, outp);
        Assert.Empty(outp);
        Assert.Equal(49, a.PendingBytes);
        a.Feed(f, 49, 1, outp);
        Assert.Single(outp);
        Assert.Equal(0, a.PendingBytes);
    }

    [Fact]
    public void LeftoverShorterThanTheMinimumIsKeptForTheNextFeed()
    {
        byte[] f1 = MakeFrame(C2S, 20, 1, 1);
        byte[] f2 = MakeFrame(C2S, 30, 2, 2);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        a.Feed(Concat(f1, f2.Take(12).ToArray()), 0, 20 + 12, outp);
        Assert.Single(outp);
        Assert.Equal(12, a.PendingBytes);
        a.Feed(f2, 12, f2.Length - 12, outp);
        Assert.Equal(2, outp.Count);
        Assert.Equal(f2, outp[1].Bytes);
    }

    [Fact]
    public void FramesCompletedBeforeABadFrameAreStillDelivered()
    {
        byte[] good = MakeFrame(C2S, 20, 1, 1);
        byte[] bad = MakeFrame(C2S, 13, 2, 2);
        bad[3] = 5;
        byte[] stream = Concat(good, bad, MakeFrame(C2S, 13, 3, 3));
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.LengthBelowMinimum, a.Feed(stream, 0, stream.Length, outp));
        Assert.Equal(good, Assert.Single(outp).Bytes);
        Assert.Equal(0, a.PendingBytes);
    }

    [Fact]
    public void ErrorIsStickyUntilReset()
    {
        byte[] bad = MakeFrame(C2S, 13, 2, 2);
        bad[3] = 5;
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        Assert.Equal(FrameError.LengthBelowMinimum, a.Feed(bad, 0, 13, outp));
        byte[] good = MakeFrame(C2S, 13, 1, 1);
        Assert.Equal(FrameError.LengthBelowMinimum, a.Feed(good, 0, 13, outp));
        Assert.Empty(outp);
        a.Reset();
        Assert.Equal(FrameError.None, a.Error);
        Assert.Equal(0u, a.OffendingLength);
        Assert.Equal(FrameError.None, a.Feed(good, 0, 13, outp));
        Assert.Single(outp);
    }

    [Fact]
    public void OutputFramesAreIndependentCopies()
    {
        byte[] f = MakeFrame(C2S, 20, 1, 1);
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        a.Feed(f, 0, f.Length, outp);
        Array.Clear(f, 0, f.Length);
        Assert.Equal((byte)1, outp[0].Bytes[0]);
    }

    [Fact]
    public void ArgumentErrorsAreProgrammingErrors()
    {
        Assert.Throws<ArgumentNullException>(() => new FrameAssembler(null));
        FrameAssembler a = new FrameAssembler(C2S);
        Assert.Throws<ArgumentNullException>(() => a.Feed(null, 0, 0, new List<RawFrame>()));
        Assert.Throws<ArgumentNullException>(() => a.Feed(new byte[1], 0, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Feed(new byte[1], 0, 2, new List<RawFrame>()));
        // 空喂入没有副作用
        Assert.Equal(FrameError.None, a.Feed(Array.Empty<byte>(), 0, 0, new List<RawFrame>()));
    }

    [Fact]
    public void PendingBytesStayBoundedByOneMaximumFramePlusOneChunk()
    {
        // 一个声称 0x578 的帧只有头就停住:待处理字节不会超过 0x578 - 1 + 下一次喂入
        byte[] header = MakeFrame(C2S, 13, 1, 0);
        header[3] = 0x78;
        header[4] = 0x05;
        FrameAssembler a = new FrameAssembler(C2S);
        List<RawFrame> outp = new();
        a.Feed(header, 0, 13, outp);
        byte[] body = new byte[0x578 - 14];
        a.Feed(body, 0, body.Length, outp);
        Assert.Equal(0x578 - 1, a.PendingBytes);
        Assert.Empty(outp);
        a.Feed(new byte[1], 0, 1, outp);
        Assert.Single(outp);
        Assert.Equal(0, a.PendingBytes);
    }
}
