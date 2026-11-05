using Dnf.Net.Crypto;
using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 帧校验函数 crc32N 与一字节折叠(E-203)。期望值来自本机模拟器跑服务端原函数得到的金标准文件
/// (crc32n_vector、checksum1byte2_vector、get_crypto_type_vector 是既有文件,frame_primitives_vector 里有 256 项表与 26 行 crc32N),
/// 加上手算的边界与"不是标准 crc32"的反例。
/// </summary>
public sealed class FrameCrcTests
{
    [Fact]
    public void Crc32NMatchesTheExistingEmulatorVectors()
    {
        List<string[]> rows = FrameVectorFile.Rows("crc32n_vector.txt");
        Assert.Equal(4, rows.Count);
        foreach (string[] r in rows)
        {
            byte[] input = FrameVectorFile.Hex(r[0]);
            uint expected = FrameVectorFile.HexU32(r[3]);
            Assert.Equal(expected, Crc32N.Compute(input));
            // 第 3 列是返回值低字节(十进制)
            Assert.Equal(byte.Parse(r[2]), (byte)Crc32N.Compute(input));
            Assert.Equal(int.Parse(r[1]), input.Length);
        }
    }

    [Fact]
    public void Crc32NMatchesThe26EmulatorRowsIncludingEmptyInput()
    {
        int n = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_primitives_vector.txt"))
        {
            if (r[0] != "crc32n")
            {
                continue;
            }
            byte[] input = FrameVectorFile.Hex(r[1]);
            Assert.Equal(FrameVectorFile.HexU32(r[2]), Crc32N.Compute(input));
            n++;
        }
        Assert.Equal(26, n);
    }

    [Fact]
    public void TableMatchesAll256EmulatorEntries()
    {
        int n = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_primitives_vector.txt"))
        {
            if (r[0] != "crctab")
            {
                continue;
            }
            int index = Convert.ToInt32(r[1], 16);
            Assert.Equal(FrameVectorFile.HexU32(r[2]), Crc32N.TableEntry(index));
            n++;
        }
        Assert.Equal(256, n);
    }

    [Fact]
    public void EmptyInputGivesTheFoldConstantOnly()
    {
        // 手算:空输入时 crc32 = ~(~0) 的结果为 0(初值取反再终值取反);折叠:0^0^0^0^0x18 = 0x18,高 3 字节为 0
        Assert.Equal(0u, Crc32N.Raw(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x00000018u, Crc32N.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FoldReplacesOnlyTheLowestByte()
    {
        // 手算:0x78^0x56 = 0x2E,^0x34 = 0x1A,^0x12 = 0x08,^0x18 = 0x10;高 3 字节 0x123456 不变
        Assert.Equal(0x12345610u, Crc32N.Fold(0x12345678u));
        // 反例:全零输入时折叠值恰好是常量 0x18,不是 0
        Assert.Equal(0x18u, Crc32N.Fold(0u));
        // 边界:全 1 时四个 0xFF 异或为 0,再异或 0x18
        Assert.Equal(0xFFFFFF18u, Crc32N.Fold(0xFFFFFFFFu));
    }

    [Fact]
    public void IsNotTheStandardCrc32()
    {
        // 标准 crc32("123456789") 是 0xCBF43926;原函数用另一个多项式,所以不会相等(反例:防止有人换成 zlib 的 crc)
        byte[] check = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.NotEqual(0xCBF43926u, Crc32N.Raw(check));
        Assert.NotEqual(0xCBF43926u, Crc32N.Compute(check));
        // 折叠只动低字节:Raw 与 Compute 的高 3 字节相同
        Assert.Equal(Crc32N.Raw(check) & 0xFFFFFF00u, Crc32N.Compute(check) & 0xFFFFFF00u);
    }

    [Fact]
    public void ArrayOverloadChecksTheRangeAndUsesOffset()
    {
        byte[] data = { 9, 1, 2, 3, 9 };
        Assert.Equal(Crc32N.Compute(new byte[] { 1, 2, 3 }), Crc32N.Compute(data, 1, 3));
        Assert.NotEqual(Crc32N.Compute(new byte[] { 1, 2, 3 }), Crc32N.Compute(data, 0, 3));
        Assert.Equal(Crc32N.Compute(ReadOnlySpan<byte>.Empty), Crc32N.Compute(data, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32N.Compute(data, 3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32N.Compute(data, -1, 1));
        Assert.Throws<ArgumentNullException>(() => Crc32N.Compute(null, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32N.TableEntry(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32N.TableEntry(-1));
    }

    [Fact]
    public void MakeChecksumTo1Byte2KeepsTheLowEightBits()
    {
        List<string[]> rows = FrameVectorFile.Rows("checksum1byte2_vector.txt");
        Assert.Equal(3, rows.Count);
        foreach (string[] r in rows)
        {
            Assert.Equal(byte.Parse(r[1]), ChecksumByte.MakeChecksumTo1Byte2(FrameVectorFile.HexU32(r[0])));
        }
        // 手算边界:只留低 8 位
        Assert.Equal(0x00, ChecksumByte.MakeChecksumTo1Byte2(0xFFFFFF00u));
        Assert.Equal(0xFF, ChecksumByte.MakeChecksumTo1Byte2(0x000000FFu));
        Assert.Equal(0xCD, ChecksumByte.MakeChecksumTo1Byte2(0x1234ABCDu));
    }

    [Fact]
    public void ChecksumByteOfDataIsTheLowByteOfCrc32N()
    {
        byte[] data = { 0, 1 };
        Assert.Equal(73, ChecksumByte.Of(data));          // crc32n_vector 里 0001 的低字节
        Assert.Equal(83, ChecksumByte.Of(new byte[] { 0 }));
    }

    [Fact]
    public void PacketIdSelectsTheSlotModulo14ForAllGoldenRows()
    {
        List<string[]> rows = FrameVectorFile.Rows("get_crypto_type_vector.txt");
        Assert.Equal(6, rows.Count);
        foreach (string[] r in rows)
        {
            int id = r[0].StartsWith("0x") ? Convert.ToInt32(r[0].Substring(2), 16) : int.Parse(r[0]);
            Assert.Equal(int.Parse(r[1]), CipherSlots.SlotForPacket(id));
        }
    }
}
