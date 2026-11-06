using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// CHANNELINFO 载荷解析(E-201)。向量里的载荷是模拟器里的 PacketBuf 原函数按服务端 put 顺序拼出的合成数据;
/// 另有截断、坏长度、边界与带偏移解析的测试。
/// </summary>
public sealed class FrameChannelInfoTests
{
    private static byte[] SynthKey()
    {
        byte[] key = new byte[334];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(((i * 5) ^ (i >> 2) ^ 0x33) & 0xFF);
        }
        return key;
    }

    private static List<byte[]> Payloads()
    {
        return FrameVectorFile.Rows("frame_channelinfo_vector.txt").Select(r => FrameVectorFile.Hex(r[8])).ToList();
    }

    [Fact]
    public void ParsesTheEmulatorBuiltPayloads()
    {
        List<string[]> rows = FrameVectorFile.Rows("frame_channelinfo_vector.txt");
        Assert.Equal(4, rows.Count);
        foreach (string[] r in rows)
        {
            byte[] payload = FrameVectorFile.Hex(r[8]);
            Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(payload, 0, payload.Length, out ChannelInfo c));
            Assert.Equal(SynthKey(), c.KeyBlock);
            Assert.Equal(FrameVectorFile.Hex(r[0]), c.ChannelName);
            Assert.Equal(unchecked((int)uint.Parse(r[1])), c.DateSeed);
            Assert.Equal(unchecked((int)uint.Parse(r[2])), c.RandomValue);
            Assert.Equal(FrameVectorFile.Hex(r[3]), c.EnvironmentBytes);
            Assert.Equal(unchecked((int)uint.Parse(r[4])), c.ServerTime);
            string[] addrs = r[5] == "-" ? Array.Empty<string>() : r[5].Split(',');
            Assert.Equal(addrs.Length, c.Addresses.Count);
            for (int i = 0; i < addrs.Length; i++)
            {
                // 原函数对地址只写至多 16 个字节(长度整数写完整长度)
                byte[] expectedBytes = FrameVectorFile.Hex(addrs[i]).Take(16).ToArray();
                Assert.Equal(expectedBytes, c.Addresses[i].Bytes);
                Assert.Equal(FrameVectorFile.Hex(addrs[i]).Length, c.Addresses[i].DeclaredLength);
            }
            Assert.Equal(r[6] != "-", c.HasAddressTail);
            if (c.HasAddressTail)
            {
                Assert.Equal(unchecked((int)uint.Parse(r[6])), c.AddressTail[0]);
                Assert.Equal(unchecked((int)uint.Parse(r[7])), c.AddressTail[1]);
            }
            else
            {
                Assert.Equal(new[] { 0, 0 }, c.AddressTail);
            }
            Assert.Equal(0x25E, c.CommandTableEnd);
            Assert.Equal(0x232, c.NotiTableEnd);
            Assert.Empty(c.Trailing);
        }
    }

    [Fact]
    public void AddressLongerThanSixteenBytesKeepsTheDeclaredLengthButOnlySixteenBytes()
    {
        byte[] payload = Payloads()[2];
        Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(payload, 0, payload.Length, out ChannelInfo c));
        Assert.Equal(2, c.Addresses.Count);
        Assert.Equal("SYNTH_ADDRESS_LONGER_THAN_16_BYTES".Length, c.Addresses[0].DeclaredLength);
        Assert.Equal(16, c.Addresses[0].Bytes.Length);
        Assert.Equal(1, c.Addresses[1].DeclaredLength);
        Assert.Single(c.Addresses[1].Bytes);
    }

    [Fact]
    public void EmptyAddressListHasNoTailIntegers()
    {
        byte[] payload = Payloads()[1];
        Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(payload, 0, payload.Length, out ChannelInfo c));
        Assert.Empty(c.Addresses);
        Assert.False(c.HasAddressTail);
        // 手算载荷长度:334 密钥 + 4 + 1(名字) + 4 + 4 + 3 + 4 + 4(个数 0) + 8 = 366
        Assert.Equal(366, payload.Length);
    }

    [Fact]
    public void EveryTruncationIsReportedWithoutThrowing()
    {
        foreach (byte[] payload in Payloads())
        {
            for (int n = 0; n < payload.Length; n++)
            {
                ChannelInfoStatus s = ChannelInfo.TryParse(payload, 0, n, out ChannelInfo c);
                Assert.NotEqual(ChannelInfoStatus.Ok, s);
                Assert.Null(c);
            }
        }
    }

    [Fact]
    public void ShortPrefixesReportTruncatedAtTheKeyBlock()
    {
        byte[] payload = Payloads()[0];
        Assert.Equal(ChannelInfoStatus.Truncated, ChannelInfo.TryParse(payload, 0, 0, out _));
        Assert.Equal(ChannelInfoStatus.Truncated, ChannelInfo.TryParse(payload, 0, 333, out _));
        // 恰好 334 字节:密钥块读完,名字长度读不出
        Assert.Equal(ChannelInfoStatus.Truncated, ChannelInfo.TryParse(payload, 0, 334, out _));
        Assert.Equal(ChannelInfoStatus.Truncated, ChannelInfo.TryParse(payload, 0, 337, out _));
    }

    [Fact]
    public void TrailingBytesAreHandedBack()
    {
        byte[] payload = Payloads()[0];
        byte[] with = payload.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(with, 0, with.Length, out ChannelInfo c));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, c.Trailing);
    }

    [Fact]
    public void ParsesFromAnOffsetInsideALargerBuffer()
    {
        byte[] payload = Payloads()[0];
        byte[] big = new byte[payload.Length + 20];
        Array.Fill(big, (byte)0xEE);
        Buffer.BlockCopy(payload, 0, big, 7, payload.Length);
        Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(big, 7, payload.Length, out ChannelInfo c));
        Assert.Empty(c.Trailing);
        Assert.Equal(SynthKey(), c.KeyBlock);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelInfo.TryParse(big, 7, big.Length, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelInfo.TryParse(big, -1, 1, out _));
        Assert.Throws<ArgumentNullException>(() => ChannelInfo.TryParse(null, 0, 0, out _));
    }

    [Fact]
    public void BadNameLengthIsRejected()
    {
        byte[] payload = (byte[])Payloads()[0].Clone();
        foreach (int bad in new[] { -1, int.MinValue, int.MaxValue, payload.Length })
        {
            byte[] p = (byte[])payload.Clone();
            BitConverter.GetBytes(bad).CopyTo(p, 334);
            Assert.Equal(ChannelInfoStatus.BadNameLength, ChannelInfo.TryParse(p, 0, p.Length, out _));
        }
        // 边界:名字长度 0 合法(名字为空的向量行已覆盖);长度恰好等于剩余字节也算名字太长之后读不出后续字段
        byte[] zero = (byte[])payload.Clone();
        BitConverter.GetBytes(0).CopyTo(zero, 334);
        Assert.NotEqual(ChannelInfoStatus.BadNameLength, ChannelInfo.TryParse(zero, 0, zero.Length, out _));
    }

    [Fact]
    public void BadAddressCountAndLengthAreRejected()
    {
        // 第 0 行:名字 13 字节,地址个数字段在 334+4+13+4+4+3+4 = 366
        byte[] payload = Payloads()[0];
        int countOffset = 334 + 4 + 13 + 4 + 4 + 3 + 4;
        Assert.Equal(2, BitConverter.ToInt32(payload, countOffset));
        foreach (int bad in new[] { -1, int.MaxValue, 0x20000000 })
        {
            byte[] p = (byte[])payload.Clone();
            BitConverter.GetBytes(bad).CopyTo(p, countOffset);
            Assert.Equal(ChannelInfoStatus.BadAddressCount, ChannelInfo.TryParse(p, 0, p.Length, out _));
        }
        // 第一个地址的长度整数设成负数
        byte[] neg = (byte[])payload.Clone();
        BitConverter.GetBytes(-5).CopyTo(neg, countOffset + 4);
        Assert.Equal(ChannelInfoStatus.BadAddressLength, ChannelInfo.TryParse(neg, 0, neg.Length, out _));
        // 地址个数 1,长度整数在,但长度整数之后没有字节:截断
        byte[] cut = payload.Take(countOffset + 4 + 4).ToArray();
        BitConverter.GetBytes(1).CopyTo(cut, countOffset);
        Assert.Equal(ChannelInfoStatus.Truncated, ChannelInfo.TryParse(cut, 0, cut.Length, out _));
    }
}
