using System.Security.Cryptography;
using Dnf.Net.Session;
using static Dnf.Net.Tests.AntibotTestSupport;

namespace Dnf.Net.Tests;

/// <summary>
/// 反机器人信封密文区变换(<see cref="AntibotCipher"/>:密钥派生与 19 个算法)的测试。
/// 黄金向量是外部向量文件的一个 114 条摘录(见 Golden/antibot_cipher_excerpt.txt 的文件头),期望值不是用被测实现算的;
/// 其余用例用合成数据检查往返、长度边界、尾部处理的结构性质,以及几个能按公开规格独立复算的算法(DES 大、RC4、GENE 新)。
/// </summary>
public class AntibotCipherTests
{
    private sealed record ExcerptRow(int Algo, uint Seq, string Name, byte[] Plain, byte[] Cipher);

    private static readonly List<ExcerptRow> Excerpt = LoadExcerpt();

    private static List<ExcerptRow> LoadExcerpt()
    {
        List<ExcerptRow> rows = new();
        foreach (string[] r in FrameVectorFile.Rows("antibot_cipher_excerpt.txt"))
        {
            rows.Add(new ExcerptRow(int.Parse(r[0]), uint.Parse(r[1]), r[2], FrameVectorFile.Hex(r[3]), FrameVectorFile.Hex(r[4])));
        }

        return rows;
    }

    public static IEnumerable<object[]> AllAlgorithms()
    {
        for (int algo = 1; algo <= 19; algo++)
        {
            yield return new object[] { algo };
        }
    }

    public static IEnumerable<object[]> ExcerptCases()
    {
        foreach (ExcerptRow row in Excerpt)
        {
            yield return new object[] { row.Algo, row.Seq, row.Name };
        }
    }

    private static ExcerptRow Find(int algo, uint seq, string name)
    {
        return Assert.Single(Excerpt, r => r.Algo == algo && r.Seq == seq && r.Name == name);
    }

    /// <summary>算法按整块独立处理的块长:8 字节块 = 算法 1 到 6;流式 = 14、15、16、19(逐字节);其余 16 字节块。</summary>
    private static int Granularity(int algo)
    {
        if (algo <= 6)
        {
            return 8;
        }

        return algo == 14 || algo == 15 || algo == 16 || algo == 19 ? 1 : 16;
    }

    // ---- 常量与密钥派生 ----

    [Fact]
    public void PublicConstantsMatchTheSpecification()
    {
        Assert.Equal(1, AntibotCipher.MinAlgorithm);
        Assert.Equal(19, AntibotCipher.MaxAlgorithm);
        Assert.Equal(0xFFFF, AntibotCipher.MaxDataLength);
        Assert.Equal(32, AntibotCipher.KeyLength);
        Assert.False(AntibotCipher.IsValidAlgorithm(0));
        Assert.True(AntibotCipher.IsValidAlgorithm(1));
        Assert.True(AntibotCipher.IsValidAlgorithm(19));
        Assert.False(AntibotCipher.IsValidAlgorithm(20));
    }

    // 期望值由 Python 的 hashlib.md5 与 struct.pack("<II", ...) 按规格直接算出,不经过被测实现
    [Theory]
    [InlineData(0u, 1, "da35686b165e3223d5d49c046644e68e3f977505b7b4443a10b2471177072c91")]
    [InlineData(3932378u, 7, "5fd014739fd7298ce6ef02c8554b8f6ddf04c1bdc563429cc87fb29ef5be1c7c")]
    [InlineData(1u, 19, "6a8d163f57e05800b79cff3eaa7f231c0a142e8a1c3d2b410fca0f43d656ad10")]
    [InlineData(3735928559u, 12, "1703f04be393f1516725b47ce2e749be9764f9cfc3363dacf339eb7677d538d9")]
    public void DeriveKeyMatchesThePythonMd5Construction(uint seq, int algo, string expectedHex)
    {
        Assert.Equal(expectedHex, Hex(AntibotCipher.DeriveKey(seq, algo)));
    }

    private static byte[] Md5OfSeqAndAlgo(uint seq, uint algoField)
    {
        byte[] input = new byte[8];
        WriteLittleEndian32(input, 0, seq);
        WriteLittleEndian32(input, 4, algoField);
        return MD5.HashData(input);
    }

    private static byte[] ReferenceKey(uint seq, int algo)
    {
        return Concat(Md5OfSeqAndAlgo(seq, unchecked((uint)algo)), Md5OfSeqAndAlgo(seq, unchecked((uint)algo + 1u)));
    }

    [Theory]
    [InlineData(0u, 5)]
    [InlineData(0xFFFFFFFFu, 19)]
    [InlineData(0x01020304u, 2)]
    [InlineData(3932378u, 13)]
    public void DeriveKeyIsTheTwoMd5HalvesOfSeqAndAlgoAndAlgoPlusOne(uint seq, int algo)
    {
        byte[] key = AntibotCipher.DeriveKey(seq, algo);
        Assert.Equal(32, key.Length);
        Assert.Equal(Hex(ReferenceKey(seq, algo)), Hex(key));
        // 第二个哈希用的是 algo + 1:它正好是"algo + 1 的密钥"的前半
        Assert.Equal(Hex(key[16..]), Hex(AntibotCipher.DeriveKey(seq, algo + 1)[..16]));
        Assert.NotEqual(Hex(key[..16]), Hex(key[16..]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void DeriveKeyDoesNotCheckTheAlgorithmRangeAndWrapsAroundAt32Bits(int algo)
    {
        Assert.Equal(Hex(ReferenceKey(9u, algo)), Hex(AntibotCipher.DeriveKey(9u, algo)));
    }

    [Fact]
    public void DeriveKeyDependsOnSeqAndAlgoAndReturnsAFreshArray()
    {
        byte[] a = AntibotCipher.DeriveKey(5u, 3);
        Assert.NotEqual(Hex(a), Hex(AntibotCipher.DeriveKey(6u, 3)));
        Assert.NotEqual(Hex(a), Hex(AntibotCipher.DeriveKey(5u, 4)));
        Assert.NotEqual(Hex(AntibotCipher.DeriveKey(1u, 2)), Hex(AntibotCipher.DeriveKey(2u, 1)));    // 序号与算法号不能互换
        a[0] ^= 0xFF;
        Assert.Equal(Hex(AntibotCipher.DeriveKey(5u, 3)), Hex(AntibotCipher.DeriveKey(5u, 3)));
        Assert.NotEqual(Hex(a), Hex(AntibotCipher.DeriveKey(5u, 3)));
    }

    // ---- 黄金向量摘录 ----

    [Fact]
    public void ExcerptHoldsEveryAlgorithmSeqAndPlainNameExactlyOnce()
    {
        Assert.Equal(114, Excerpt.Count);
        for (int algo = 1; algo <= 19; algo++)
        {
            foreach (uint seq in new[] { 0u, 3932378u })
            {
                foreach (string name in new[] { "inc32", "rand37", "rand180" })
                {
                    ExcerptRow row = Find(algo, seq, name);
                    int expected = name == "inc32" ? 32 : (name == "rand37" ? 37 : 180);
                    Assert.Equal(expected, row.Plain.Length);
                    Assert.Equal(expected, row.Cipher.Length);
                }
            }
        }

        byte[] increasing = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.All(Excerpt.Where(r => r.Name == "inc32"), r => Assert.Equal(increasing, r.Plain));
    }

    [Theory]
    [MemberData(nameof(ExcerptCases))]
    public void EncryptReproducesTheExcerptVector(int algo, uint seq, string name)
    {
        ExcerptRow row = Find(algo, seq, name);
        byte[] actual = AntibotCipher.Encrypt(algo, seq, row.Plain);
        Assert.NotNull(actual);
        Assert.Equal(Hex(row.Cipher), Hex(actual));
    }

    [Theory]
    [MemberData(nameof(ExcerptCases))]
    public void DecryptReproducesTheExcerptPlaintext(int algo, uint seq, string name)
    {
        ExcerptRow row = Find(algo, seq, name);
        byte[] actual = AntibotCipher.Decrypt(algo, seq, row.Cipher);
        Assert.NotNull(actual);
        Assert.Equal(Hex(row.Plain), Hex(actual));
    }

    // ---- 往返与结构性质(合成数据) ----

    private static void RoundTrip(int algo, uint seq, int length)
    {
        byte[] plain = Synth(algo * 1000 + length, length);
        byte[] snapshot = (byte[])plain.Clone();
        byte[] encrypted = AntibotCipher.Encrypt(algo, seq, plain);
        Assert.NotNull(encrypted);
        Assert.NotSame(plain, encrypted);
        Assert.Equal(snapshot, plain);                        // 传入的数组不被改动
        Assert.Equal(length, encrypted.Length);
        byte[] decrypted = AntibotCipher.Decrypt(algo, seq, encrypted);
        Assert.NotNull(decrypted);
        Assert.True(snapshot.AsSpan().SequenceEqual(decrypted), "算法 " + algo + " 序号 " + seq + " 长度 " + length + ":加密再解密没有还原");
        byte[] reversed = AntibotCipher.Encrypt(algo, seq, AntibotCipher.Decrypt(algo, seq, plain));
        Assert.True(snapshot.AsSpan().SequenceEqual(reversed), "算法 " + algo + " 序号 " + seq + " 长度 " + length + ":解密再加密没有还原");
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void DecryptRestoresEncryptedDataAtEveryLength(int algo)
    {
        foreach (uint seq in new[] { 0u, 7u, SeqA, 0xFFFFFFFFu })
        {
            for (int length = 0; length <= 80; length++)
            {
                RoundTrip(algo, seq, length);
            }
        }

        foreach (int length in new[] { 127, 128, 129, 255, 256, 257, 1000, 4095, 4096, 4097 })
        {
            RoundTrip(algo, SeqB, length);
        }
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void EncryptChangesTheDataAndDependsOnSeq(int algo)
    {
        byte[] plain = Synth(31, 64);
        byte[] first = AntibotCipher.Encrypt(algo, 1u, plain);
        Assert.NotEqual(Hex(plain), Hex(first));
        Assert.NotEqual(Hex(first), Hex(AntibotCipher.Encrypt(algo, 2u, plain)));
        Assert.Equal(Hex(first), Hex(AntibotCipher.Encrypt(algo, 1u, plain)));                 // 确定性
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void WholeBlocksDoNotDependOnTheTailAndTheTailIsTransformedToo(int algo)
    {
        int unit = Granularity(algo);
        for (int length = 1; length <= 100; length++)
        {
            byte[] data = Synth(500 + length, length);
            int whole = length / unit * unit;
            byte[] encrypted = AntibotCipher.Encrypt(algo, SeqA, data);
            byte[] head = AntibotCipher.Encrypt(algo, SeqA, data[..whole]);
            Assert.True(head.AsSpan().SequenceEqual(encrypted.AsSpan(0, whole)), "算法 " + algo + " 长度 " + length + ":整块部分受尾部影响");
            byte[] decrypted = AntibotCipher.Decrypt(algo, SeqA, data);
            byte[] decryptedHead = AntibotCipher.Decrypt(algo, SeqA, data[..whole]);
            Assert.True(decryptedHead.AsSpan().SequenceEqual(decrypted.AsSpan(0, whole)), "算法 " + algo + " 长度 " + length + ":解密的整块部分受尾部影响");
            if (whole < length && length - whole >= 3)
            {
                // 尾部(不足一块的字节)也被搅动过,不是原样放过
                Assert.False(data.AsSpan(whole).SequenceEqual(encrypted.AsSpan(whole)), "算法 " + algo + " 长度 " + length + ":尾部原样放过");
                Assert.False(data.AsSpan(whole).SequenceEqual(decrypted.AsSpan(whole)), "算法 " + algo + " 长度 " + length + ":解密时尾部原样放过");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void EmptyDataGivesAnEmptyNewArray(int algo)
    {
        byte[] empty = Array.Empty<byte>();
        byte[] encrypted = AntibotCipher.Encrypt(algo, 3u, empty);
        byte[] decrypted = AntibotCipher.Decrypt(algo, 3u, empty);
        Assert.NotNull(encrypted);
        Assert.NotNull(decrypted);
        Assert.Empty(encrypted);
        Assert.Empty(decrypted);
        Assert.True(AntibotCipher.TryEncrypt(algo, 3u, empty, out byte[] viaTry));
        Assert.Empty(viaTry);
    }

    // ---- 失败判定:不抛异常 ----

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void IllegalAlgorithmNumbersFailWithoutThrowing(int algo)
    {
        byte[] data = Synth(1, 40);
        byte[] snapshot = (byte[])data.Clone();
        Assert.Null(AntibotCipher.Encrypt(algo, 5u, data));
        Assert.Null(AntibotCipher.Decrypt(algo, 5u, data));
        Assert.False(AntibotCipher.TryEncrypt(algo, 5u, data, out byte[] encrypted));
        Assert.Null(encrypted);
        Assert.False(AntibotCipher.TryDecrypt(algo, 5u, data, out byte[] decrypted));
        Assert.Null(decrypted);
        Assert.False(AntibotCipher.IsValidAlgorithm(algo));
        Assert.Equal(snapshot, data);
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void LegalAlgorithmsSucceedThroughTheTryForms(int algo)
    {
        byte[] data = Synth(2, 24);
        Assert.True(AntibotCipher.IsValidAlgorithm(algo));
        Assert.True(AntibotCipher.TryEncrypt(algo, 8u, data, out byte[] encrypted));
        Assert.NotNull(encrypted);
        Assert.Equal(Hex(AntibotCipher.Encrypt(algo, 8u, data)), Hex(encrypted));
        Assert.True(AntibotCipher.TryDecrypt(algo, 8u, encrypted, out byte[] decrypted));
        Assert.Equal(Hex(data), Hex(decrypted));
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void NullDataFailsAndTheLengthLimitIs0xFFFF(int algo)
    {
        Assert.Null(AntibotCipher.Encrypt(algo, 3u, null));
        Assert.Null(AntibotCipher.Decrypt(algo, 3u, null));
        Assert.False(AntibotCipher.TryEncrypt(algo, 3u, null, out byte[] viaTry));
        Assert.Null(viaTry);

        byte[] longest = Synth(algo, 0xFFFF);
        byte[] encrypted = AntibotCipher.Encrypt(algo, 3u, longest);
        Assert.NotNull(encrypted);
        Assert.Equal(0xFFFF, encrypted.Length);
        byte[] decrypted = AntibotCipher.Decrypt(algo, 3u, encrypted);
        Assert.NotNull(decrypted);
        Assert.True(longest.AsSpan().SequenceEqual(decrypted));

        Assert.Null(AntibotCipher.Encrypt(algo, 3u, new byte[0x10000]));
        Assert.Null(AntibotCipher.Decrypt(algo, 3u, new byte[0x10000]));
        Assert.False(AntibotCipher.TryDecrypt(algo, 3u, new byte[0x10001], out byte[] tooLong));
        Assert.Null(tooLong);
    }

    // ---- 能按公开规格独立复算的算法 ----

    /// <summary>算法 19:out[i] = in[i] ^ (i &amp; 0xFF) ^ key[i &amp; 7],加解密相同。</summary>
    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0u, 5)]
    [InlineData(3932378u, 300)]
    [InlineData(0xA1B2C3D4u, 1000)]
    public void GeneNewIsAPositionAndKeyXor(uint seq, int length)
    {
        byte[] key = ReferenceKey(seq, 19);
        byte[] data = Synth(77, length);
        byte[] expected = new byte[length];
        for (int i = 0; i < length; i++)
        {
            expected[i] = (byte)(data[i] ^ (i & 0xFF) ^ key[i & 7]);
        }

        Assert.Equal(Hex(expected), Hex(AntibotCipher.Encrypt(19, seq, data)));
        Assert.Equal(Hex(expected), Hex(AntibotCipher.Decrypt(19, seq, data)));
    }

    private static byte[] ReferenceRc4(byte[] key, byte[] data)
    {
        byte[] s = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        byte[] output = new byte[data.Length];
        int a = 0;
        int b = 0;
        for (int n = 0; n < data.Length; n++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            output[n] = (byte)(data[n] ^ s[(s[a] + s[b]) & 0xFF]);
        }

        return output;
    }

    [Fact]
    public void ReferenceRc4MatchesThePublicVector()
    {
        Assert.Equal("bbf316e8d940af0ad3", Hex(ReferenceRc4("Key"u8.ToArray(), "Plaintext"u8.ToArray())));
    }

    /// <summary>算法 14 是标准 RC4,密钥取派生密钥的前 16 字节;加解密相同。</summary>
    [Theory]
    [InlineData(0u, 1)]
    [InlineData(0u, 40)]
    [InlineData(3932378u, 300)]
    [InlineData(0xA1B2C3D4u, 1000)]
    public void Algorithm14IsStandardRc4WithTheFirst16KeyBytes(uint seq, int length)
    {
        byte[] data = Synth(88, length);
        byte[] expected = ReferenceRc4(ReferenceKey(seq, 14)[..16], data);
        Assert.Equal(Hex(expected), Hex(AntibotCipher.Encrypt(14, seq, data)));
        Assert.Equal(Hex(expected), Hex(AntibotCipher.Decrypt(14, seq, data)));
    }

    /// <summary>算法 3 是标准单重 DES 的 ECB,密钥取派生密钥的前 8 字节;不足 8 字节的尾部另行处理,所以只比整块。</summary>
    [Theory]
    [InlineData(0u, 8)]
    [InlineData(0u, 32)]
    [InlineData(SeqA, 37)]
    [InlineData(SeqA, 180)]
    [InlineData(0xA1B2C3D4u, 64)]
    public void Algorithm3IsStandardDesEcbOnWholeBlocks(uint seq, int length)
    {
        byte[] data = Synth(99, length);
        int whole = length / 8 * 8;
        using DES des = DES.Create();
        des.Key = ReferenceKey(seq, 3)[..8];
        byte[] expected = des.EncryptEcb(data.AsSpan(0, whole), PaddingMode.None);
        Assert.Equal(Hex(expected), Hex(AntibotCipher.Encrypt(3, seq, data)[..whole]));
        byte[] encrypted = AntibotCipher.Encrypt(3, seq, data);
        Assert.Equal(Hex(data[..whole]), Hex(des.DecryptEcb(encrypted.AsSpan(0, whole), PaddingMode.None)));
        Assert.Equal(Hex(data[..whole]), Hex(AntibotCipher.Decrypt(3, seq, encrypted)[..whole]));
    }
}
