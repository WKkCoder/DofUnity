using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 载荷读写原语(服务端 PacketBuf 的 put/get,E-202、E-206)。向量文件是本机模拟器直接跑原函数的结果(游标、返回值、写入/读出的字节),
/// 另有手排字节的编码测试与边界、反例。
/// </summary>
public sealed class FramePacketIoTests
{
    private static short Le16(byte[] b, int o)
    {
        return (short)(b[o] | (b[o + 1] << 8));
    }

    private static int Le32(byte[] b, int o)
    {
        return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }

    [Fact]
    public void WritePrimitivesMatchTheEmulatorVectors()
    {
        int n = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_packetbuf_vector.txt"))
        {
            if (r[0] != "put")
            {
                continue;
            }
            bool bigEndian = r[1] == "1";
            int start = int.Parse(r[2]);
            string op = r[3];
            bool expectOk = r[5] == "1";
            int expectEnd = int.Parse(r[6]);
            byte[] expectBytes = FrameVectorFile.Hex(r[7]);
            PacketWriter w = new PacketWriter(start, bigEndian);
            bool ok;
            switch (op)
            {
                case "byte":
                    ok = w.PutByte(int.Parse(r[4]));
                    break;
                case "short":
                    ok = w.PutShort(int.Parse(r[4]));
                    break;
                case "int":
                    ok = w.PutInt(int.Parse(r[4]));
                    break;
                case "str":
                    string[] parts = r[4].Split(':');
                    ok = w.PutString(FrameVectorFile.Hex(parts[0]), int.Parse(parts[1]));
                    break;
                case "bin":
                    ok = w.PutBinary(FrameVectorFile.Hex(r[4]));
                    break;
                default:
                    throw new InvalidOperationException(op);
            }
            string label = string.Join(" ", r);
            Assert.True(expectOk == ok, label);
            Assert.True(expectEnd == w.Position, label);
            Assert.True(expectBytes.AsSpan().SequenceEqual(w.ToArray()), label);
            n++;
        }
        Assert.Equal(392, n);
    }

    [Fact]
    public void ReadPrimitivesMatchTheEmulatorVectors()
    {
        int n = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_packetbuf_vector.txt"))
        {
            if (r[0] != "get")
            {
                continue;
            }
            bool bigEndian = r[1] == "1";
            int length = int.Parse(r[2]);
            int start = int.Parse(r[3]);
            byte[] data = FrameVectorFile.Hex(r[4]);
            Assert.Equal(length, data.Length);
            string op = r[5];
            bool expectOk = r[7] == "1";
            int expectEnd = int.Parse(r[8]);
            byte[] outBuf = FrameVectorFile.Hex(r[9]);
            PacketReader reader = new PacketReader(data, start, length, bigEndian);
            string label = string.Join(" ", r);
            bool ok;
            switch (op)
            {
                case "byte":
                    ok = reader.TryGetByte(out byte b);
                    Assert.True(!ok || b == outBuf[0], label);
                    break;
                case "short":
                    ok = reader.TryGetShort(out short s);
                    Assert.True(!ok || s == Le16(outBuf, 0), label);
                    break;
                case "int":
                    ok = reader.TryGetInt(out int i);
                    Assert.True(!ok || i == Le32(outBuf, 0), label);
                    break;
                case "str":
                    string[] parts = r[6].Split(':');
                    ok = reader.TryGetString(int.Parse(parts[0]), int.Parse(parts[1]), out byte[] str);
                    if (ok)
                    {
                        // 输出缓冲事先填 0xEE;写进去的字符串以 0 结尾,没写(长度 0)时一直是 0xEE
                        int end = 0;
                        while (end < outBuf.Length && outBuf[end] != 0 && outBuf[end] != 0xEE)
                        {
                            end++;
                        }
                        Assert.True(outBuf.AsSpan(0, end).SequenceEqual(str), label);
                    }
                    else
                    {
                        Assert.Null(str);
                    }
                    break;
                case "bin":
                    int count = int.Parse(r[6]);
                    ok = reader.TryGetBinary(count, out byte[] bin);
                    if (ok)
                    {
                        Assert.True(outBuf.AsSpan(0, count).SequenceEqual(bin), label);
                    }
                    break;
                default:
                    throw new InvalidOperationException(op);
            }
            Assert.True(expectOk == ok, label);
            Assert.True(expectEnd == reader.Position, label);
            n++;
        }
        Assert.Equal(210, n);
    }

    [Fact]
    public void IntegersAreLittleEndianByDefaultAndBigEndianWhenAsked()
    {
        PacketWriter le = new PacketWriter(0);
        le.PutInt(0x01020304);
        le.PutShort(0x0506);
        le.PutByte(0x07);
        Assert.Equal(new byte[] { 4, 3, 2, 1, 6, 5, 7 }, le.ToArray());
        PacketWriter be = new PacketWriter(0, bigEndian: true);
        be.PutInt(0x01020304);
        be.PutShort(0x0506);
        be.PutByte(0x07);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, be.ToArray());
    }

    [Fact]
    public void StringsHaveNoLengthPrefixAndNoTrailingNul()
    {
        PacketWriter w = new PacketWriter(0);
        Assert.True(w.PutString(new byte[] { 0x61, 0x62, 0x63 }, 3));
        Assert.Equal(new byte[] { 0x61, 0x62, 0x63 }, w.ToArray());
        // 遇到 0 停止,0 本身不写
        PacketWriter z = new PacketWriter(0);
        Assert.True(z.PutString(new byte[] { 0x61, 0, 0x62 }, 8));
        Assert.Equal(new byte[] { 0x61 }, z.ToArray());
        // 最大个数小于字符串长度:截断
        PacketWriter t = new PacketWriter(0);
        Assert.True(t.PutString(new byte[] { 1, 2, 3, 4 }, 2));
        Assert.Equal(new byte[] { 1, 2 }, t.ToArray());
        // 最大个数 0 成功但什么也不写
        PacketWriter e = new PacketWriter(0);
        Assert.True(e.PutString(new byte[] { 1 }, 0));
        Assert.Equal(0, e.Length);
        // 负数最大个数不是合法调用
        Assert.False(e.PutString(new byte[] { 1 }, -1));
    }

    [Fact]
    public void BinaryBlocksHaveNoLengthPrefixAndEmptyBlocksFail()
    {
        PacketWriter w = new PacketWriter(0);
        Assert.True(w.PutBinary(new byte[] { 9, 8, 7 }));
        Assert.Equal(new byte[] { 9, 8, 7 }, w.ToArray());
        // 反编译读码:空块或空指针记日志后返回假,什么也不写(模拟器里这条路径要进日志函数,没有跑)
        Assert.False(w.PutBinary(ReadOnlySpan<byte>.Empty));
        Assert.Equal(3, w.Length);
    }

    [Fact]
    public void WriteLimitIsInclusiveAndFailureLeavesNothingBehind()
    {
        // 上限是绝对游标 0x172b4:从头长 15 起,正好写满 0x172b4 - 15 个字节成功,再多 1 个字节失败
        PacketWriter w = PacketWriter.ForLayout(FrameLayout.ServerToClient);
        Assert.Equal(15, w.Position);
        Assert.Equal(0, w.Length);
        int room = PacketWriter.DefaultLimit - 15;
        Assert.True(w.PutBinary(new byte[room - 4]));
        Assert.True(w.PutInt(1));                 // 还剩 4 字节:整数正好放下
        Assert.Equal(PacketWriter.DefaultLimit, w.Position);
        Assert.False(w.PutByte(1));
        Assert.Equal(PacketWriter.DefaultLimit, w.Position);
        Assert.Equal(room, w.Length);
    }

    [Fact]
    public void ShortWriteJustPastTheLimitFailsWithoutPartialBytes()
    {
        PacketWriter w = new PacketWriter(PacketWriter.DefaultLimit - 3);
        Assert.False(w.PutInt(0x01020304));
        Assert.Equal(0, w.Length);
        Assert.True(w.PutShort(0x0102));
        Assert.Equal(new byte[] { 2, 1 }, w.ToArray());
        Assert.False(w.PutShort(1));
        Assert.True(w.PutByte(0xAA));
        Assert.Equal(new byte[] { 2, 1, 0xAA }, w.ToArray());
    }

    [Fact]
    public void StringSpaceCheckUsesTheMaximumNotTheActualLength()
    {
        PacketWriter w = new PacketWriter(PacketWriter.DefaultLimit - 4);
        // 实际只写 1 个字节,但最大个数 8 超出剩余空间:整个原语失败
        Assert.False(w.PutString(new byte[] { 0x61 }, 8));
        Assert.Equal(0, w.Length);
        Assert.True(w.PutString(new byte[] { 0x61 }, 4));
        Assert.Equal(1, w.Length);
    }

    [Fact]
    public void WriterRejectsAStartBeyondTheLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketWriter(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketWriter(PacketWriter.DefaultLimit + 1));
        Assert.NotNull(new PacketWriter(PacketWriter.DefaultLimit));
        Assert.Throws<ArgumentNullException>(() => PacketWriter.ForLayout(null));
        // 客户端到服务端的帧整帧上限 0x578:自定义上限也生效
        PacketWriter small = new PacketWriter(13, false, 0x578);
        Assert.True(small.PutBinary(new byte[0x578 - 13]));
        Assert.False(small.PutByte(0));
    }

    [Fact]
    public void ReaderGetStringStopsAtNulAndLeavesTheRestInTheStream()
    {
        byte[] data = { 0x61, 0x62, 0, 0x63, 0x64 };
        PacketReader r = new PacketReader(data);
        Assert.True(r.TryGetString(16, 5, out byte[] s));
        Assert.Equal(new byte[] { 0x61, 0x62 }, s);
        Assert.Equal(3, r.Position);            // 停在 0 之后,后面两个字节没被吞
        Assert.True(r.TryGetByte(out byte next));
        Assert.Equal(0x63, next);
    }

    [Fact]
    public void ReaderGetStringNeedsABufferStrictlyLargerThanTheLength()
    {
        byte[] data = { 1, 2, 3, 4, 5 };
        Assert.False(new PacketReader(data).TryGetString(5, 5, out _));
        Assert.False(new PacketReader(data).TryGetString(4, 5, out _));
        Assert.True(new PacketReader(data).TryGetString(6, 5, out byte[] full));
        Assert.Equal(data, full);
        Assert.False(new PacketReader(data).TryGetString(0, 0, out _));       // 0 < 0 不成立
        Assert.True(new PacketReader(data).TryGetString(1, 0, out byte[] empty));
        Assert.Empty(empty);
        Assert.False(new PacketReader(data).TryGetString(16, -1, out _));
        Assert.False(new PacketReader(data).TryGetString(-1, 3, out _));
        // 数据不够:失败且不动游标
        PacketReader r = new PacketReader(data);
        Assert.False(r.TryGetString(100, 6, out _));
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void ReaderIntegersAndBounds()
    {
        byte[] data = { 0xFF, 0x7F, 0x00, 0x80, 0x01, 0x02, 0x03, 0x04 };
        PacketReader r = new PacketReader(data);
        Assert.True(r.TryGetShort(out short a));
        Assert.Equal(0x7FFF, a);
        Assert.True(r.TryGetUShort(out ushort b));
        Assert.Equal((ushort)0x8000, b);
        Assert.Equal(4, r.Remaining);
        Assert.True(r.TryGetUInt(out uint c));
        Assert.Equal(0x04030201u, c);
        // 读尽之后什么都读不出,而且不抛异常
        Assert.False(r.TryGetByte(out _));
        Assert.False(r.TryGetShort(out _));
        Assert.False(r.TryGetInt(out _));
        Assert.False(r.TryGetBinary(1, out _));
        // 有符号:0x8000 读成 -32768
        Assert.True(new PacketReader(new byte[] { 0x00, 0x80 }).TryGetShort(out short neg));
        Assert.Equal(-32768, neg);
        // 一次只差一个字节
        PacketReader three = new PacketReader(new byte[] { 1, 2, 3 });
        Assert.False(three.TryGetInt(out _));
        Assert.Equal(0, three.Position);
    }

    [Fact]
    public void ReaderBinaryRejectsNonPositiveCounts()
    {
        PacketReader r = new PacketReader(new byte[] { 1, 2, 3 });
        Assert.False(r.TryGetBinary(0, out _));
        Assert.False(r.TryGetBinary(-1, out _));
        Assert.False(r.TryGetBinary(4, out _));
        Assert.True(r.TryGetBinary(3, out byte[] all));
        Assert.Equal(new byte[] { 1, 2, 3 }, all);
    }

    [Fact]
    public void ReaderStartsWhereTheCursorIsGivenAndValidatesItsArguments()
    {
        byte[] frame = new byte[20];
        frame[13] = 0x42;
        PacketReader r = new PacketReader(frame, 13, 20);       // 收到的帧:游标从 0xD 起
        Assert.True(r.TryGetByte(out byte v));
        Assert.Equal(0x42, v);
        Assert.Equal(6, r.Remaining);
        Assert.Throws<ArgumentNullException>(() => new PacketReader(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketReader(frame, 0, 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketReader(frame, 21, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketReader(frame, -1, 20));
        // 长度小于数组长度:超出长度的字节读不到
        PacketReader limited = new PacketReader(frame, 0, 2);
        Assert.False(limited.TryGetInt(out _));
    }

    [Fact]
    public void FoldHashTrailerIsCheckedAgainstTheBytesReadSoFar()
    {
        PacketWriter w = new PacketWriter(0);
        w.PutInt(0x11223344);
        w.PutByte(5);
        byte[] payload = w.ToArray();
        uint h = PacketIntegrity.FoldHash(payload);
        PacketWriter full = new PacketWriter(0);
        full.PutBinary(payload);
        full.PutUInt(h);
        byte[] wire = full.ToArray();

        PacketReader r = new PacketReader(wire);
        Assert.True(r.TryGetInt(out _));
        Assert.True(r.TryGetByte(out _));
        Assert.Equal(FoldHashStatus.Match, r.CheckFoldHash(0));
        Assert.Equal(5, r.Position);                // 核对不动游标

        // 反例:载荷里改 1 位 → 不符
        byte[] bad = (byte[])wire.Clone();
        bad[2] ^= 1;
        PacketReader rb = new PacketReader(bad, 5, bad.Length);
        Assert.Equal(FoldHashStatus.Mismatch, rb.CheckFoldHash(0));

        // 哈希字节本身改 1 位 → 不符
        bad = (byte[])wire.Clone();
        bad[wire.Length - 1] ^= 0x80;
        Assert.Equal(FoldHashStatus.Mismatch, new PacketReader(bad, 5, bad.Length).CheckFoldHash(0));

        // 缺失:游标之后不足 4 字节;已读区间为空;起点非法
        Assert.Equal(FoldHashStatus.Missing, new PacketReader(wire, 6, wire.Length).CheckFoldHash(0));
        Assert.Equal(FoldHashStatus.Missing, new PacketReader(wire, 0, wire.Length).CheckFoldHash(0));
        Assert.Equal(FoldHashStatus.Missing, new PacketReader(wire, 5, wire.Length).CheckFoldHash(-1));
        // 从中间起算:区间 [1, 5) 的哈希不等于整段的哈希
        Assert.Equal(FoldHashStatus.Mismatch, new PacketReader(wire, 5, wire.Length).CheckFoldHash(1));
    }

    [Fact]
    public void FoldHashIsOnlyCheckedForPayloadsOfOneToFourHundredBytes()
    {
        foreach (int n in new[] { 400, 401 })
        {
            byte[] payload = new byte[n];
            for (int i = 0; i < n; i++)
            {
                payload[i] = (byte)(i * 7);
            }
            PacketWriter w = new PacketWriter(0);
            w.PutBinary(payload);
            w.PutUInt(PacketIntegrity.FoldHash(payload));
            byte[] wire = w.ToArray();
            FoldHashStatus status = new PacketReader(wire, n, wire.Length).CheckFoldHash(0);
            Assert.Equal(n == 400 ? FoldHashStatus.Match : FoldHashStatus.Missing, status);
        }
    }
}
