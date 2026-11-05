using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>帧头布局与字段读写(E-202):两个方向的头不同,字节序与字段位置都是布局参数。期望字节都是手工排的。</summary>
public sealed class FrameHeaderTests
{
    [Fact]
    public void ServerToClientLayoutIs15BytesWithCipherFromOffset15()
    {
        FrameLayout l = FrameLayout.ServerToClient;
        Assert.Equal(15, l.HeaderLength);
        Assert.Equal(15, l.CipherOffset);
        Assert.Equal(15, l.ChecksumStart);
        Assert.Equal(0, l.ClassOffset);
        Assert.Equal(1, l.IdOffset);
        Assert.Equal(3, l.LengthOffset);
        Assert.Equal(7, l.CheckOffset);
        Assert.Equal(11, l.SecondOffset);
        Assert.Equal(4, l.SecondWidth);
        Assert.False(l.BigEndian);
        Assert.Equal(15, l.MinFrameLength);
        Assert.Equal(95000, l.MaxFrameLength);
    }

    [Fact]
    public void ClientToServerLayoutIs13BytesWithCipherFromOffset13()
    {
        FrameLayout l = FrameLayout.ClientToServer;
        Assert.Equal(13, l.HeaderLength);
        Assert.Equal(13, l.CipherOffset);
        Assert.Equal(11, l.ChecksumStart);       // 校验覆盖 [0xB, 总长):含 2 字节序号
        Assert.Equal(11, l.SecondOffset);
        Assert.Equal(2, l.SecondWidth);
        Assert.False(l.BigEndian);
        Assert.Equal(13, l.MinFrameLength);
        Assert.Equal(0x578, l.MaxFrameLength);
        Assert.Equal(0x1000, FrameLayout.ServerReceiveBufferSize);
    }

    [Fact]
    public void ReadsHandAssembledServerToClientHeader()
    {
        byte[] bytes =
        {
            0x01,                   // 类
            0x5C, 0x02,             // 包号 0x025C(小端)
            0x20, 0x00, 0x00, 0x00, // 总长 0x20
            0xD4, 0xC3, 0xB2, 0xA1, // 校验 a = 0xA1B2C3D4
            0x01, 0x02, 0x03, 0x04, // 校验 b = 0x04030201
            0xFF, 0xFF,             // 头之后的字节不属于头
        };
        Assert.True(PacketHeader.TryRead(FrameLayout.ServerToClient, bytes, out PacketHeader h));
        Assert.Equal(PacketClass.Cmd, h.Class);
        Assert.Equal((ushort)0x025C, h.PacketId);
        Assert.Equal(0x20u, h.TotalLength);
        Assert.Equal(0xA1B2C3D4u, h.Check);
        Assert.Equal(0x04030201u, h.Second);
    }

    [Fact]
    public void ReadsHandAssembledClientToServerHeaderWithTwoByteSequence()
    {
        byte[] bytes =
        {
            0x00,                   // 类 0
            0x01, 0x00,             // 包号 1
            0x1D, 0x00, 0x00, 0x00, // 总长 29
            0xDD, 0xCC, 0xBB, 0xAA, // 校验字
            0x34, 0x12,             // 序号 0x1234
        };
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, bytes, out PacketHeader h));
        Assert.Equal(PacketClass.Noti, h.Class);
        Assert.Equal((ushort)1, h.PacketId);
        Assert.Equal(29u, h.TotalLength);
        Assert.Equal(0xAABBCCDDu, h.Check);
        Assert.Equal(0x1234u, h.Second);
    }

    [Fact]
    public void BigEndianLayoutReadsTheSameBytesTheOtherWay()
    {
        byte[] bytes = { 0x01, 0x02, 0x5C, 0x00, 0x00, 0x00, 0x20, 0xA1, 0xB2, 0xC3, 0xD4, 0x12, 0x34 };
        FrameLayout be = FrameLayout.ClientToServer.WithByteOrder(true);
        Assert.True(be.BigEndian);
        Assert.True(PacketHeader.TryRead(be, bytes, out PacketHeader h));
        Assert.Equal((ushort)0x025C, h.PacketId);
        Assert.Equal(0x20u, h.TotalLength);
        Assert.Equal(0xA1B2C3D4u, h.Check);
        Assert.Equal(0x1234u, h.Second);
        // 反例:同样的字节按小端读,值完全不同
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, bytes, out PacketHeader le));
        Assert.NotEqual(h.PacketId, le.PacketId);
        Assert.NotEqual(h.TotalLength, le.TotalLength);
    }

    [Fact]
    public void WriteProducesTheHandAssembledBytesAndLeavesTheRestAlone()
    {
        PacketHeader h = new PacketHeader
        {
            Class = PacketClass.Cmd,
            PacketId = 0x025C,
            TotalLength = 0x20,
            Check = 0xA1B2C3D4,
            Second = 0x04030201,
        };
        byte[] dest = new byte[20];
        Array.Fill(dest, (byte)0xEE);
        Assert.True(h.TryWrite(FrameLayout.ServerToClient, dest));
        byte[] expected =
        {
            0x01, 0x5C, 0x02, 0x20, 0x00, 0x00, 0x00, 0xD4, 0xC3, 0xB2, 0xA1, 0x01, 0x02, 0x03, 0x04,
            0xEE, 0xEE, 0xEE, 0xEE, 0xEE,
        };
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void ClientToServerWriteTruncatesTheSecondFieldToTwoBytesAndBigEndianSwaps()
    {
        PacketHeader h = new PacketHeader { ClassByte = 1, PacketId = 0x0102, TotalLength = 0x0304, Check = 0x05060708, Second = 0x12345 };
        byte[] le = new byte[13];
        Assert.True(h.TryWrite(FrameLayout.ClientToServer, le));
        Assert.Equal(new byte[] { 1, 0x02, 0x01, 0x04, 0x03, 0, 0, 0x08, 0x07, 0x06, 0x05, 0x45, 0x23 }, le);
        byte[] be = new byte[13];
        Assert.True(h.TryWrite(FrameLayout.ClientToServer.WithByteOrder(true), be));
        Assert.Equal(new byte[] { 1, 0x01, 0x02, 0, 0, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x23, 0x45 }, be);
    }

    [Fact]
    public void RoundTripThroughBothLayoutsAndByteOrders()
    {
        PacketHeader h = new PacketHeader { ClassByte = 0xFE, PacketId = 0xFFFF, TotalLength = 0xFFFFFFFF, Check = 0x80000001, Second = 0xFFFF };
        foreach (FrameLayout l in new[] { FrameLayout.ServerToClient, FrameLayout.ClientToServer, FrameLayout.ServerToClient.WithByteOrder(true), FrameLayout.ClientToServer.WithByteOrder(true) })
        {
            byte[] buf = new byte[l.HeaderLength];
            Assert.True(h.TryWrite(l, buf));
            Assert.True(PacketHeader.TryRead(l, buf, out PacketHeader back));
            Assert.Equal(h, back);
        }
    }

    [Fact]
    public void ShortBuffersAreRejectedWithoutThrowing()
    {
        foreach (FrameLayout l in new[] { FrameLayout.ServerToClient, FrameLayout.ClientToServer })
        {
            for (int n = 0; n < l.HeaderLength; n++)
            {
                Assert.False(PacketHeader.TryRead(l, new byte[n], out PacketHeader h));
                Assert.Equal(default, h);
                Assert.False(new PacketHeader().TryWrite(l, new byte[n]));
            }
            Assert.True(PacketHeader.TryRead(l, new byte[l.HeaderLength], out _));
        }
    }

    [Fact]
    public void ZeroWidthSecondFieldReadsZeroAndWritesNothing()
    {
        FrameLayout l = new FrameLayout("SYNTH", 11, 0, 1, 3, 7, 11, 0, 11, false, 11, 100);
        byte[] bytes = { 1, 2, 0, 9, 0, 0, 0, 1, 2, 3, 4 };
        Assert.True(PacketHeader.TryRead(l, bytes, out PacketHeader h));
        Assert.Equal(0u, h.Second);
        PacketHeader w = new PacketHeader { Second = 0xFFFF };
        byte[] dest = new byte[11];
        Assert.True(w.TryWrite(l, dest));
        Assert.Equal(new byte[11], dest);
    }

    [Fact]
    public void NullLayoutIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => PacketHeader.TryRead(null, new byte[20], out _));
        Assert.Throws<ArgumentNullException>(() => new PacketHeader().TryWrite(null, new byte[20]));
    }

    [Fact]
    public void LayoutRejectsInconsistentParameters()
    {
        // 基准参数:15 字节头,各字段首尾相接
        Assert.NotNull(new FrameLayout("SYNTH", 15, 0, 1, 3, 7, 11, 4, 15, false, 15, 100));
        // 字段伸出头外
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 14, 0, 1, 3, 7, 11, 4, 14, false, 14, 100));
        // 字段重叠(包号与长度)
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 15, 0, 1, 2, 7, 11, 4, 15, false, 15, 100));
        // 负偏移
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 15, -1, 1, 3, 7, 11, 4, 15, false, 15, 100));
        // 第二字段宽度不合法
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 15, 0, 1, 3, 7, 11, 3, 15, false, 15, 100));
        // 校验起点越过头尾
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 15, 0, 1, 3, 7, 11, 4, 16, false, 15, 100));
        // 长度下限小于头长、上限小于下限
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 15, 0, 1, 3, 7, 11, 4, 15, false, 14, 100));
        Assert.Throws<ArgumentException>(() => new FrameLayout("SYNTH", 15, 0, 1, 3, 7, 11, 4, 15, false, 15, 14));
        // 边界:上限等于下限可以
        Assert.NotNull(new FrameLayout("SYNTH", 15, 0, 1, 3, 7, 11, 4, 15, false, 15, 15));
    }

    [Fact]
    public void WithLengthLimitsKeepsEverythingElse()
    {
        FrameLayout l = FrameLayout.ClientToServer.WithLengthLimits(20, 30);
        Assert.Equal(20, l.MinFrameLength);
        Assert.Equal(30, l.MaxFrameLength);
        Assert.Equal(FrameLayout.ClientToServer.HeaderLength, l.HeaderLength);
        Assert.Equal(FrameLayout.ClientToServer.SecondWidth, l.SecondWidth);
        Assert.Throws<ArgumentException>(() => FrameLayout.ClientToServer.WithLengthLimits(12, 30));
    }
}
