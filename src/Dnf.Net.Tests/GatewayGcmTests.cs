using System.Security.Cryptography;
using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>
/// 自实现的 AES-256-GCM 的对拍:期望值只来自(a)公开测试向量(Golden/gateway_gcm_nist_vector.txt,出处见文件头),
/// (b).NET 的 <see cref="AesGcm"/>(独立实现),(c)手算的长度/布局。不把本实现的输出抄成期望值。
/// </summary>
public sealed class GatewayGcmTests
{
    private sealed record Vector(string Name, byte[] Key, byte[] Nonce, byte[] Aad, byte[] Plain, byte[] Cipher, byte[] Tag);

    private static List<Vector> LoadVectors()
    {
        List<Vector> list = new();
        foreach (string line in File.ReadAllLines(GatewayTestSupport.GoldenPath("gateway_gcm_nist_vector.txt")))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] c = line.Split('\t');
            Assert.Equal(7, c.Length);
            list.Add(new Vector(c[0], GatewayTestSupport.Hex(c[1]), GatewayTestSupport.Hex(c[2]), GatewayTestSupport.Hex(c[3]),
                GatewayTestSupport.Hex(c[4]), GatewayTestSupport.Hex(c[5]), GatewayTestSupport.Hex(c[6])));
        }
        return list;
    }

    public static IEnumerable<object[]> VectorNames()
    {
        foreach (Vector v in LoadVectors())
        {
            yield return new object[] { v.Name };
        }
    }

    private static Vector Find(string name)
    {
        return LoadVectors().Single(v => v.Name == name);
    }

    [Fact]
    public void PublicVectorFileHasTheExpectedSixCases()
    {
        // McGrew-Viega 附录 B 的用例 13–16 + RFC 7714 §16.2 的两个示例
        Assert.Equal(6, LoadVectors().Count);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void SealMatchesThePublicVector(string name)
    {
        Vector v = Find(name);
        byte[] sealedData = GcmAes.Seal(v.Key, v.Nonce, v.Plain, v.Aad);
        Assert.Equal(v.Cipher.Concat(v.Tag).ToArray(), sealedData);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void OpenRecoversThePlaintextOfThePublicVector(string name)
    {
        Vector v = Find(name);
        Assert.True(GcmAes.TryOpen(v.Key, v.Nonce, v.Cipher.Concat(v.Tag).ToArray(), out byte[] plain, v.Aad));
        Assert.Equal(v.Plain, plain);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void DotNetAesGcmAgreesWithTheTranscribedVector(string name)
    {
        // 抄录核对:公开向量文件的内容必须与 .NET 的独立实现一致,否则是抄错了而不是实现错了
        Vector v = Find(name);
        byte[] cipher = new byte[v.Plain.Length];
        byte[] tag = new byte[16];
        using AesGcm gcm = new(v.Key, 16);
        gcm.Encrypt(v.Nonce, v.Plain, cipher, tag, v.Aad);
        Assert.Equal(v.Cipher, cipher);
        Assert.Equal(v.Tag, tag);
    }

    [Fact]
    public void PublicVectorsWithoutAadRejectWhenAadIsSupplied()
    {
        // 反例:AAD 参与认证,同一份密文换一个 AAD 必须失败
        Vector v = Find("mv-tc15-four-blocks");
        byte[] sealedData = v.Cipher.Concat(v.Tag).ToArray();
        Assert.False(GcmAes.TryOpen(v.Key, v.Nonce, sealedData, out _, new byte[] { 1 }));
        Vector w = Find("mv-tc16-aad-partial");
        Assert.False(GcmAes.TryOpen(w.Key, w.Nonce, w.Cipher.Concat(w.Tag).ToArray(), out _));
    }

    public static IEnumerable<object[]> BoundaryLengths()
    {
        foreach (int n in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 4096 })
        {
            yield return new object[] { n };
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryLengths))]
    public void BoundaryLengthsMatchDotNetAndRoundTrip(int length)
    {
        byte[] key = GatewayTestSupport.SynthKey(3);
        byte[] nonce = GatewayTestSupport.Pattern(12, 40);
        byte[] plain = GatewayTestSupport.Pattern(length, 5, 13);
        byte[] aad = GatewayTestSupport.Pattern(length % 7, 100);

        byte[] sealedData = GcmAes.Seal(key, nonce, plain, aad);
        Assert.Equal(length + 16, sealedData.Length);

        byte[] cipher = new byte[length];
        byte[] tag = new byte[16];
        using (AesGcm gcm = new(key, 16))
        {
            gcm.Encrypt(nonce, plain, cipher, tag, aad);
        }
        Assert.Equal(cipher.Concat(tag).ToArray(), sealedData);

        Assert.True(GcmAes.TryOpen(key, nonce, sealedData, out byte[] opened, aad));
        Assert.Equal(plain, opened);
    }

    [Fact]
    public void RandomMessagesMatchDotNetAesGcm()
    {
        // 固定种子的随机对拍:长度 0–70 全覆盖,再加长消息与跨"一次最多 1024 块密钥流"分段边界(16384 字节)的长度
        Random rng = new(20261105);
        List<int> lengths = Enumerable.Range(0, 71).ToList();
        lengths.AddRange(new[] { 100, 255, 256, 257, 1000, 4095, 4097, 16383, 16384, 16385, 16399, 32775 });
        foreach (int length in lengths)
        {
            byte[] key = new byte[32];
            byte[] nonce = new byte[12];
            byte[] plain = new byte[length];
            byte[] aad = new byte[rng.Next(0, 41)];
            rng.NextBytes(key);
            rng.NextBytes(nonce);
            rng.NextBytes(plain);
            rng.NextBytes(aad);

            byte[] expectedCipher = new byte[length];
            byte[] expectedTag = new byte[16];
            using (AesGcm gcm = new(key, 16))
            {
                gcm.Encrypt(nonce, plain, expectedCipher, expectedTag, aad);
            }

            byte[] sealedData = GcmAes.Seal(key, nonce, plain, aad);
            Assert.True(expectedCipher.Concat(expectedTag).SequenceEqual(sealedData), "seal differs from AesGcm at length " + length);
            Assert.True(GcmAes.TryOpen(key, nonce, expectedCipher.Concat(expectedTag).ToArray(), out byte[] opened, aad), "open failed at length " + length);
            Assert.True(plain.SequenceEqual(opened), "plaintext differs at length " + length);
        }
    }

    [Fact]
    public void EveryFlippedBitInCiphertextTagNonceOrAadIsRejected()
    {
        byte[] key = GatewayTestSupport.SynthKey(9);
        byte[] nonce = GatewayTestSupport.Pattern(12, 1);
        byte[] plain = GatewayTestSupport.Pattern(40, 3);
        byte[] aad = GatewayTestSupport.Pattern(20, 50);
        byte[] sealedData = GcmAes.Seal(key, nonce, plain, aad);
        Assert.True(GcmAes.TryOpen(key, nonce, sealedData, out _, aad));

        // 密文 40 字节 + 标签 16 字节,逐个字节翻转一位(含标签的第 16 个字节)
        for (int i = 0; i < sealedData.Length; i++)
        {
            byte[] bad = (byte[])sealedData.Clone();
            bad[i] ^= 0x01;
            Assert.False(GcmAes.TryOpen(key, nonce, bad, out byte[] result, aad), "flip at sealed byte " + i);
            Assert.Null(result);
        }
        for (int i = 0; i < nonce.Length; i++)
        {
            byte[] bad = (byte[])nonce.Clone();
            bad[i] ^= 0x80;
            Assert.False(GcmAes.TryOpen(key, bad, sealedData, out _, aad), "flip at nonce byte " + i);
        }
        for (int i = 0; i < aad.Length; i++)
        {
            byte[] bad = (byte[])aad.Clone();
            bad[i] ^= 0x10;
            Assert.False(GcmAes.TryOpen(key, nonce, sealedData, out _, bad), "flip at aad byte " + i);
        }
    }

    [Fact]
    public void WrongKeyTruncationAndExtensionAreRejected()
    {
        byte[] key = GatewayTestSupport.SynthKey(2);
        byte[] nonce = GatewayTestSupport.Pattern(12, 9);
        byte[] sealedData = GcmAes.Seal(key, nonce, GatewayTestSupport.Pattern(33));
        Assert.False(GcmAes.TryOpen(GatewayTestSupport.SynthKey(3), nonce, sealedData, out _));
        Assert.False(GcmAes.TryOpen(key, nonce, sealedData.Take(sealedData.Length - 1).ToArray(), out _));
        Assert.False(GcmAes.TryOpen(key, nonce, sealedData.Concat(new byte[] { 0 }).ToArray(), out _));
        Assert.False(GcmAes.TryOpen(key, nonce, sealedData, out _, new byte[] { 0 }));
    }

    [Fact]
    public void InputShorterThanTheTagIsRejectedWithoutThrowing()
    {
        byte[] key = GatewayTestSupport.SynthKey();
        byte[] nonce = GatewayTestSupport.Pattern(12);
        for (int n = 0; n < 16; n++)
        {
            Assert.False(GcmAes.TryOpen(key, nonce, new byte[n], out byte[] plain), "length " + n);
            Assert.Null(plain);
        }
        Assert.False(GcmAes.TryOpen(key, nonce, null, out _));
    }

    [Fact]
    public void ExactlyTagLongInputIsAnEmptyMessageOnlyWhenAuthentic()
    {
        byte[] key = GatewayTestSupport.SynthKey();
        byte[] nonce = GatewayTestSupport.Pattern(12);
        byte[] empty = GcmAes.Seal(key, nonce, Array.Empty<byte>());
        Assert.Equal(16, empty.Length);
        Assert.True(GcmAes.TryOpen(key, nonce, empty, out byte[] plain));
        Assert.Empty(plain);
        Assert.False(GcmAes.TryOpen(key, nonce, new byte[16], out _));
    }

    [Fact]
    public void DifferentNonceOrKeyGivesDifferentOutput()
    {
        byte[] key = GatewayTestSupport.SynthKey(4);
        byte[] plain = GatewayTestSupport.Pattern(20);
        byte[] a = GcmAes.Seal(key, GatewayTestSupport.Pattern(12, 1), plain);
        byte[] b = GcmAes.Seal(key, GatewayTestSupport.Pattern(12, 2), plain);
        byte[] c = GcmAes.Seal(GatewayTestSupport.SynthKey(5), GatewayTestSupport.Pattern(12, 1), plain);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(16, 12)]
    [InlineData(31, 12)]
    [InlineData(33, 12)]
    [InlineData(32, 0)]
    [InlineData(32, 11)]
    [InlineData(32, 13)]
    [InlineData(32, 16)]
    public void WrongKeyOrNonceLengthIsACallerError(int keyLength, int nonceLength)
    {
        byte[] key = new byte[keyLength];
        byte[] nonce = new byte[nonceLength];
        string expected = keyLength != 32 ? "key must be 32 bytes" : "nonce must be 12 bytes";
        ArgumentException seal = Assert.Throws<ArgumentException>(() => GcmAes.Seal(key, nonce, new byte[1]));
        ArgumentException open = Assert.Throws<ArgumentException>(() => GcmAes.TryOpen(key, nonce, new byte[32], out _));
        Assert.StartsWith(expected, seal.Message);
        Assert.StartsWith(expected, open.Message);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        byte[] key = GatewayTestSupport.SynthKey();
        byte[] nonce = GatewayTestSupport.Pattern(12);
        Assert.Throws<ArgumentNullException>(() => GcmAes.Seal(null, nonce, new byte[1]));
        Assert.Throws<ArgumentNullException>(() => GcmAes.Seal(key, null, new byte[1]));
        Assert.Throws<ArgumentNullException>(() => GcmAes.Seal(key, nonce, null));
        Assert.Throws<ArgumentNullException>(() => GcmAes.TryOpen(null, nonce, new byte[16], out _));
        Assert.Throws<ArgumentNullException>(() => GcmAes.TryOpen(key, null, new byte[16], out _));
    }

    [Fact]
    public void FixedTimeEqualsComparesEveryByte()
    {
        byte[] a = GatewayTestSupport.Pattern(16);
        Assert.True(GcmAes.FixedTimeEquals(a, (byte[])a.Clone()));
        Assert.True(GcmAes.FixedTimeEquals(Array.Empty<byte>(), Array.Empty<byte>()));
        for (int i = 0; i < a.Length; i++)
        {
            byte[] b = (byte[])a.Clone();
            b[i] ^= 1;
            Assert.False(GcmAes.FixedTimeEquals(a, b), "difference at byte " + i);
        }
        Assert.False(GcmAes.FixedTimeEquals(a, a.Take(15).ToArray()));
        Assert.False(GcmAes.FixedTimeEquals(a, null));
        Assert.False(GcmAes.FixedTimeEquals(null, a));
    }

    [Fact]
    public void SealDoesNotModifyItsInputs()
    {
        byte[] key = GatewayTestSupport.SynthKey();
        byte[] nonce = GatewayTestSupport.Pattern(12);
        byte[] plain = GatewayTestSupport.Pattern(50);
        byte[] aad = GatewayTestSupport.Pattern(5);
        byte[] keyCopy = (byte[])key.Clone();
        byte[] plainCopy = (byte[])plain.Clone();
        GcmAes.Seal(key, nonce, plain, aad);
        Assert.Equal(keyCopy, key);
        Assert.Equal(plainCopy, plain);
        Assert.Equal(GatewayTestSupport.Pattern(5), aad);
    }
}
