using Dnf.Net.Session;
using static Dnf.Net.Tests.AntibotTestSupport;

namespace Dnf.Net.Tests;

/// <summary>
/// 561 号信封拆包(<see cref="AntibotEnvelope"/>)的测试。信封一律由测试按规格手工拼:加密借用 <see cref="AntibotCipher"/>,
/// CRC32、字节序、各字段偏移都用 <see cref="AntibotTestSupport"/> 里独立写的一份,不借用被测的拆包代码;
/// 另有两条由 Python 按规格独立算出的已知答案(算法 19 与算法 14)。每个拒绝分支都单独断言失败原因,避免"因为别的原因碰巧失败"。
/// </summary>
public class AntibotEnvelopeTests
{
    public static IEnumerable<object[]> AllAlgorithms()
    {
        for (int algo = 1; algo <= 19; algo++)
        {
            yield return new object[] { algo };
        }
    }

    private static byte[] StandardPlain(int algorithmData = 3, int innerBody = 20, int seed = 5)
    {
        return Concat(AlgorithmPacket(algorithmData), InnerPacket(1, 0x1234, innerBody, seed));
    }

    private static byte[] StandardInner(int innerBody = 20, int seed = 5)
    {
        return InnerPacket(1, 0x1234, innerBody, seed);
    }

    private static void AssertRejected(byte[] payload, AntibotEnvelopeStatus expected)
    {
        byte[] before = payload == null ? null : (byte[])payload.Clone();
        Assert.False(AntibotEnvelope.TryUnwrap(payload, out byte[] inner, out AntibotEnvelopeStatus status));
        Assert.Equal(expected, status);
        Assert.Null(inner);
        Assert.False(AntibotEnvelope.TryUnwrap(payload, out byte[] innerWithoutStatus));
        Assert.Null(innerWithoutStatus);
        if (payload != null)
        {
            Assert.Equal(before, payload);                               // 失败也不改动传入的载荷
        }
    }

    private static void AssertUnwraps(byte[] payload, byte[] expectedInner)
    {
        byte[] before = (byte[])payload.Clone();
        Assert.True(AntibotEnvelope.TryUnwrap(payload, out byte[] inner, out AntibotEnvelopeStatus status), "拆包失败:" + status);
        Assert.Equal(AntibotEnvelopeStatus.Ok, status);
        Assert.Equal(Hex(expectedInner), Hex(inner));
        Assert.Equal(before, payload);
        Assert.True(AntibotEnvelope.TryUnwrap(payload, out byte[] innerWithoutStatus));
        Assert.Equal(Hex(expectedInner), Hex(innerWithoutStatus));
        Assert.NotSame(inner, innerWithoutStatus);
    }

    /// <summary>截掉末尾 <paramref name="remove"/> 字节,并把开头的"其后字节数"改成与新长度相符,让失败只能来自后面的检查。</summary>
    private static byte[] Truncate(byte[] payload, int remove)
    {
        byte[] shorter = payload[..(payload.Length - remove)];
        if (shorter.Length >= 4)
        {
            WriteBigEndian32(shorter, 0, (uint)(shorter.Length - 4));
        }

        return shorter;
    }

    // ---- 常量 ----

    [Fact]
    public void ConstantsMatchTheSpecification()
    {
        Assert.Equal(561, AntibotEnvelope.PacketId);
        Assert.Equal(0x231, AntibotEnvelope.PacketId);
        Assert.Equal(21, AntibotEnvelope.FixedLength);
        Assert.Equal(17, AntibotEnvelope.HeaderLength);
    }

    [Fact]
    public void TestHelperCrcMatchesThePublicCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32("123456789"u8.ToArray()));
        Assert.Equal(0u, Crc32(Array.Empty<byte>()));
    }

    // ---- 成功路径 ----

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void UnwrapRecoversTheInnerPacketForEveryAlgorithm(int algo)
    {
        foreach ((int algorithmData, int innerBody) in new[] { (0, 0), (3, 8), (0, 1), (32, 40), (255, 300), (7, 1000) })
        {
            byte[] inner = InnerPacket(1, 0x1234, innerBody, algo * 7 + innerBody);
            byte[] plain = Concat(AlgorithmPacket(algorithmData), inner);
            foreach (uint seq in new[] { 0u, SeqA, SeqB })
            {
                AssertUnwraps(ValidEnvelope(algo, seq, plain), inner);
            }
        }
    }

    [Fact]
    public void ALargestPossibleEnvelopeStillUnwraps()
    {
        int innerBody = 0xFFFF - 17 - 9 - 7;                              // 总长字段恰好取到 0xFFFF
        byte[] inner = InnerPacket(0, 0x2222, innerBody, 3);
        byte[] plain = Concat(AlgorithmPacket(0), inner);
        Assert.Equal(0xFFFF - 17, plain.Length);
        byte[] payload = ValidEnvelope(19, SeqA, plain);
        Assert.Equal(0xFFFF, (payload[4] << 8) | payload[5]);
        AssertUnwraps(payload, inner);
    }

    [Fact]
    public void TheFlagByteIsNotInterpreted()
    {
        byte[] plain = StandardPlain();
        byte[] inner = StandardInner();
        for (int flag = 0; flag < 256; flag++)
        {
            byte[] payload = new Spec { CipherAlgo = 16, AlgoField = 16, Plain = plain, Flag = (byte)flag }.ToBytes();
            Assert.Equal(flag, payload[8]);
            AssertUnwraps(payload, inner);
        }
    }

    [Fact]
    public void AFourByteTailAfterTheCipherAreaIsIgnored()
    {
        byte[] plain = StandardPlain();
        byte[] inner = StandardInner();
        foreach (byte[] tail in new[] { new byte[4], new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, new byte[] { 1, 2, 3, 4 } })
        {
            byte[] payload = new Spec { CipherAlgo = 14, AlgoField = 14, Plain = plain, Tail = tail }.ToBytes();
            Assert.Equal(21 + plain.Length + 4, payload.Length);
            AssertUnwraps(payload, inner);
        }
    }

    [Fact]
    public void TheAlgorithmPacketLengthIsNinePlusItsNinthByte()
    {
        for (int algorithmData = 0; algorithmData <= 40; algorithmData += 5)
        {
            byte[] algorithmPacket = AlgorithmPacket(algorithmData);
            Assert.Equal(algorithmData, algorithmPacket[8]);
            byte[] inner = InnerPacket(0, 0x0BCD, 9, algorithmData);
            AssertUnwraps(ValidEnvelope(12, SeqB, Concat(algorithmPacket, inner)), inner);
        }
    }

    [Fact]
    public void AnInnerPacketWithJustTheSevenByteHeaderIsAccepted()
    {
        byte[] inner = InnerPacket(1, 0x0102, 0, 1);
        Assert.Equal(7, inner.Length);
        AssertUnwraps(ValidEnvelope(5, SeqA, Concat(AlgorithmPacket(2), inner)), inner);
    }

    // 按规格由 Python 独立构造:算法 19(GENE 新)、序号 3932378、标志 0
    [Fact]
    public void KnownAnswerEnvelopeWithAlgorithm19()
    {
        byte[] payload = FrameVectorFile.Hex("0000002e002e001100003c00da000000139ddb1a6744c48e953a8d667defd49ef59f1eced1f477381cb700f7ecc949071cb7");
        byte[] inner = FrameVectorFile.Hex("0134121100000030313233343536373839");
        AssertUnwraps(payload, inner);
    }

    // 按规格由 Python 独立构造:算法 14(RC4)、序号 0xA1B2C3D4、标志 0x7F、带 4 字节尾巴
    [Fact]
    public void KnownAnswerEnvelopeWithAlgorithm14AndATail()
    {
        byte[] payload = FrameVectorFile.Hex("00000055005100117fa1b2c3d40000000e5cd7533dce03493933c9db8451a79b01f4ae761748f287a6e8d7e607bb351ba44cf04e7ead61c67831bd25b46cf875304517484ea43fcf1d81f393956b5c4965735fea83deadbeef");
        byte[] inner = FrameVectorFile.Hex("00cd0b2f00000005101b26313c47525d68737e89949faab5c0cbd6e1ecf7020d18232e39444f5a65707b86919ca7b2");
        AssertUnwraps(payload, inner);
    }

    // ---- 拒绝路径:逐项 ----

    [Fact]
    public void ANullPayloadIsRejected()
    {
        AssertRejected(null, AntibotEnvelopeStatus.NullPayload);
    }

    [Fact]
    public void PayloadsShorterThanTheFixedHeaderAreRejected()
    {
        byte[] valid = ValidEnvelope(19, SeqA, StandardPlain());
        foreach (int length in new[] { 0, 1, 4, 20 })
        {
            AssertRejected(Truncate(valid, valid.Length - length), AntibotEnvelopeStatus.TooShort);
        }
    }

    // 偏移 +4(首字段恰为载荷全长)已是合法的第二种口径,改由 TheLengthPrefixMayAlsoBeTheFullPayloadLength 覆盖
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(0x01000000)]
    public void AWrongLengthPrefixIsRejected(int delta)
    {
        AssertRejected(new Spec { Plain = StandardPlain(), PrefixDelta = delta }.ToBytes(), AntibotEnvelopeStatus.LengthPrefixMismatch);
    }

    // 真实帧里首字段有两种口径:多数等于其后的字节数,个别大帧等于载荷全长且密文区之后没有尾巴。
    // 两种都接受(首字段只作参考,界定密文区的是偏移 4 的 total);紧邻全长的两侧仍然拒绝。
    [Fact]
    public void TheLengthPrefixMayAlsoBeTheFullPayloadLength()
    {
        byte[] plain = StandardPlain();
        byte[] payload = new Spec { Plain = plain, PrefixDelta = 4 }.ToBytes();
        Assert.Equal(21 + plain.Length, payload.Length);                  // 其后没有尾巴
        Assert.Equal((uint)payload.Length, ReadBigEndian32(payload, 0));  // 首字段等于全长
        AssertUnwraps(payload, StandardInner());
        foreach (int delta in new[] { 3, 5 })
        {
            AssertRejected(new Spec { Plain = plain, PrefixDelta = delta }.ToBytes(), AntibotEnvelopeStatus.LengthPrefixMismatch);
        }
    }

    [Fact]
    public void TheLengthPrefixCountsTheBytesAfterItIncludingTheTail()
    {
        byte[] plain = StandardPlain();
        byte[] withTail = new Spec { Plain = plain, Tail = new byte[4] }.ToBytes();
        Assert.Equal((uint)(withTail.Length - 4), ReadBigEndian32(withTail, 0));
        // 前缀只数到密文区末尾(忘了算尾巴)就对不上
        AssertRejected(new Spec { Plain = plain, Tail = new byte[4], PrefixDelta = -4 }.ToBytes(), AntibotEnvelopeStatus.LengthPrefixMismatch);
    }

    private static byte[] Modify(byte[] payload, int index, byte value)
    {
        byte[] copy = (byte[])payload.Clone();
        copy[index] = value;
        return copy;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(0x1100)]
    [InlineData(0x0011 + 0x100)]
    [InlineData(0xFFFF)]
    public void AHeaderLengthOtherThan17IsRejected(int headerLength)
    {
        AssertRejected(new Spec { Plain = StandardPlain(), HeaderLength = headerLength }.ToBytes(), AntibotEnvelopeStatus.BadHeaderLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public void ATotalBelow17IsRejected(int total)
    {
        AssertRejected(new Spec { Plain = StandardPlain(), Total = total }.ToBytes(), AntibotEnvelopeStatus.TotalOutOfRange);
    }

    [Fact]
    public void ATotalThatClaimsMoreCipherBytesThanThePayloadHoldsIsRejected()
    {
        byte[] plain = StandardPlain();
        int actual = 17 + plain.Length;
        foreach (int total in new[] { actual + 1, actual + 4, actual + 100, 0xFFFF })
        {
            AssertRejected(new Spec { Plain = plain, Total = total }.ToBytes(), AntibotEnvelopeStatus.Truncated);
        }
    }

    [Fact]
    public void ATruncatedPayloadIsRejected()
    {
        byte[] valid = ValidEnvelope(14, SeqA, StandardPlain());
        foreach (int remove in new[] { 1, 2, 8, valid.Length - 21 })
        {
            AssertRejected(Truncate(valid, remove), AntibotEnvelopeStatus.Truncated);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(20u)]
    [InlineData(21u)]
    [InlineData(100u)]
    [InlineData(255u)]
    [InlineData(256u)]
    [InlineData(0x01000001u)]                     // 低字节恰好是合法的 1
    [InlineData(0x00010013u)]                     // 低字节恰好是合法的 19
    [InlineData(0x80000001u)]
    [InlineData(0xFFFFFFFFu)]
    public void AnAlgorithmNumberOutsideOneToNineteenIsRejected(uint algoField)
    {
        byte[] anything = Synth(4, 40);
        AssertRejected(new Spec { AlgoField = algoField, CipherArea = anything, Plain = anything }.ToBytes(), AntibotEnvelopeStatus.BadAlgorithm);
    }

    [Fact]
    public void ACorruptedCrcFieldIsRejected()
    {
        byte[] plain = StandardPlain();
        uint good = Crc32(plain);
        foreach (uint wrong in new[] { good ^ 1u, good ^ 0x80000000u, ~good, 0u, good + 1 })
        {
            AssertRejected(new Spec { CipherAlgo = 15, AlgoField = 15, Plain = plain, Crc = wrong }.ToBytes(), AntibotEnvelopeStatus.CrcMismatch);
        }
    }

    [Fact]
    public void ACorruptedCipherByteIsRejectedByTheCrc()
    {
        byte[] valid = ValidEnvelope(15, SeqA, StandardPlain());
        for (int index = 21; index < valid.Length; index += 7)
        {
            AssertRejected(Modify(valid, index, (byte)(valid[index] ^ 0x10)), AntibotEnvelopeStatus.CrcMismatch);
        }
    }

    [Fact]
    public void TheCrcIsOverTheDecryptedAreaAndStoredBigEndian()
    {
        byte[] plain = StandardPlain();
        byte[] cipher = AntibotCipher.Encrypt(16, SeqA, plain);
        Assert.NotEqual(Crc32(plain), Crc32(cipher));
        // 对密文区算的 CRC 不行
        AssertRejected(new Spec { CipherAlgo = 16, AlgoField = 16, Plain = plain, Crc = Crc32(cipher) }.ToBytes(), AntibotEnvelopeStatus.CrcMismatch);
        // 小端存放的 CRC 不行
        uint good = Crc32(plain);
        uint swapped = (good >> 24) | ((good >> 8) & 0xFF00u) | ((good << 8) & 0xFF0000u) | (good << 24);
        Assert.NotEqual(good, swapped);
        AssertRejected(new Spec { CipherAlgo = 16, AlgoField = 16, Plain = plain, Crc = swapped }.ToBytes(), AntibotEnvelopeStatus.CrcMismatch);
        // 对的可以
        AssertUnwraps(new Spec { CipherAlgo = 16, AlgoField = 16, Plain = plain, Crc = good }.ToBytes(), StandardInner());
    }

    [Fact]
    public void ADifferentSeqInTheHeaderDecryptsToGarbageAndIsRejected()
    {
        byte[] plain = StandardPlain();
        byte[] cipher = AntibotCipher.Encrypt(17, SeqA, plain);
        AssertRejected(new Spec { Seq = SeqA + 1, AlgoField = 17, CipherArea = cipher, Plain = plain }.ToBytes(), AntibotEnvelopeStatus.CrcMismatch);
        AssertRejected(new Spec { Seq = SeqA, AlgoField = 13, CipherArea = cipher, Plain = plain }.ToBytes(), AntibotEnvelopeStatus.CrcMismatch);
    }

    [Fact]
    public void AnEmptyCipherAreaDecryptsToNothingAndIsRejected()
    {
        AssertRejected(new Spec { Plain = Array.Empty<byte>() }.ToBytes(), AntibotEnvelopeStatus.BadAlgorithmPacket);
    }

    [Fact]
    public void APlaintextShorterThanTheAlgorithmPacketHeaderIsRejected()
    {
        for (int length = 1; length <= 8; length++)
        {
            AssertRejected(ValidEnvelope(19, SeqA, Synth(length, length)), AntibotEnvelopeStatus.BadAlgorithmPacket);
        }
    }

    [Fact]
    public void AnAlgorithmPacketLongerThanThePlaintextIsRejectedAndTheBoundaryIsExact()
    {
        byte[] inner = InnerPacket(1, 0x1234, 4, 2);
        byte[] plain = Concat(AlgorithmPacket(5), inner);
        int rest = plain.Length - 9;                                      // 第 9 个字节的最大允许值:算法包恰好占满明文
        byte[] fills = (byte[])plain.Clone();
        fills[8] = (byte)rest;
        AssertRejected(ValidEnvelope(19, SeqA, fills), AntibotEnvelopeStatus.InnerTooShort);
        byte[] overflows = (byte[])plain.Clone();
        overflows[8] = (byte)(rest + 1);
        AssertRejected(ValidEnvelope(19, SeqA, overflows), AntibotEnvelopeStatus.BadAlgorithmPacket);
        byte[] wild = (byte[])plain.Clone();
        wild[8] = 0xFF;
        AssertRejected(ValidEnvelope(19, SeqA, wild), AntibotEnvelopeStatus.BadAlgorithmPacket);
    }

    [Fact]
    public void ABytesCountingPastTheAlgorithmPacketMovesTheInnerStart()
    {
        // 第 9 个字节比实际算法包长 1:内层从下一个字节开始,总长对不上
        byte[] inner = InnerPacket(1, 0x1234, 12, 2);
        byte[] plain = Concat(AlgorithmPacket(5), inner);
        plain[8] = 6;
        AssertRejected(ValidEnvelope(8, SeqA, plain), AntibotEnvelopeStatus.InnerLengthMismatch);
        plain[8] = 4;
        AssertRejected(ValidEnvelope(8, SeqA, plain), AntibotEnvelopeStatus.InnerLengthMismatch);
    }

    [Fact]
    public void AnInnerPacketShorterThanSevenBytesIsRejected()
    {
        for (int innerLength = 0; innerLength <= 6; innerLength++)
        {
            byte[] plain = Concat(AlgorithmPacket(2), Synth(innerLength, innerLength));
            AssertRejected(ValidEnvelope(19, SeqA, plain), AntibotEnvelopeStatus.InnerTooShort);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-20)]
    [InlineData(1000)]
    public void AnInnerTotalThatDisagreesWithTheRemainingLengthIsRejected(int delta)
    {
        byte[] sized = StandardInner();
        byte[] inner = InnerPacket(1, 0x1234, 20, 5, (uint)(sized.Length + delta));
        AssertRejected(ValidEnvelope(19, SeqA, Concat(AlgorithmPacket(3), inner)), AntibotEnvelopeStatus.InnerLengthMismatch);
    }

    [Fact]
    public void TheInnerTotalIsLittleEndianAndMustMatchExactly()
    {
        byte[] inner = InnerPacket(0, 0x0BCD, 256, 6);                    // 总长 263 = 0x0107:低字节 07,高字节 01
        Assert.Equal(263, inner.Length);
        Assert.Equal(0x07, inner[3]);
        Assert.Equal(0x01, inner[4]);
        AssertUnwraps(ValidEnvelope(19, SeqA, Concat(AlgorithmPacket(1), inner)), inner);
        byte[] bigEndian = (byte[])inner.Clone();
        bigEndian[3] = 0x00;
        bigEndian[4] = 0x00;
        bigEndian[5] = 0x01;
        bigEndian[6] = 0x07;
        AssertRejected(ValidEnvelope(19, SeqA, Concat(AlgorithmPacket(1), bigEndian)), AntibotEnvelopeStatus.InnerLengthMismatch);
    }

    [Fact]
    public void HeaderFieldsAreBigEndianAndTheCipherAreaStartsAtOffset21()
    {
        // 密文区长度超过 255:总长字段的高字节非零;序号的四个字节互不相同
        byte[] plain = Concat(AlgorithmPacket(2), InnerPacket(1, 0x4321, 600, 8));
        byte[] payload = new Spec { CipherAlgo = 7, AlgoField = 7, Seq = SeqB, Plain = plain }.ToBytes();
        Assert.Equal((17 + plain.Length) >> 8, payload[4]);
        Assert.Equal(0xA1, payload[9]);
        Assert.Equal(0xD4, payload[12]);
        AssertUnwraps(payload, InnerPacket(1, 0x4321, 600, 8));
    }

    [Fact]
    public void UnwrapDoesNotDependOnPreviousCalls()
    {
        byte[] good = ValidEnvelope(15, SeqA, StandardPlain());
        byte[] bad = Modify(good, good.Length - 1, (byte)(good[^1] ^ 1));
        for (int round = 0; round < 3; round++)
        {
            AssertUnwraps(good, StandardInner());
            AssertRejected(bad, AntibotEnvelopeStatus.CrcMismatch);
        }
    }
}
