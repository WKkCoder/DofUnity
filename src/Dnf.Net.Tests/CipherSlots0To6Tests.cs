using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Dnf.Net.Crypto;
using static Dnf.Net.Tests.CipherSlots0To6Support;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话算法槽 0 到 6 对拍服务端原版的金标准(E-204、E-262、E-263):
/// 原有每槽 1 条(crypto_slots_vectors.txt;槽 0 另有 cryptoshift_encrypt_vector.txt 的 3 行)+ 本机模拟器新产的多组向量(crypto_ext_slot*_vector.txt)。
/// 模拟器向量的密钥列是切片全长;旧文件对大切片只写了开头的字节,其余是零,所以旧文件的密钥要右侧补零(槽 2、槽 3)。
/// 期望值全部来自这些文件,不来自本实现的输出。
/// </summary>
public sealed class CipherSlots0To6GoldenTests
{
    // 第二个参数是旧文件里该槽可复现的行数:槽 5 的那一行用的是对象构造时的内部随机密钥(SYNTH_CTOR_INTERNAL),无法复现,由新向量覆盖。
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    public void OldGoldenRowsPassForTheSlot(int slot, int expectedUsableRows)
    {
        int usable = 0;
        List<string> failures = new List<string>();
        foreach (string[] row in ReadRows("crypto_slots_vectors.txt", 7))
        {
            if (int.Parse(row[0]) != slot)
            {
                continue;
            }
            Assert.Equal(ExpectedBlockSizes[slot], int.Parse(row[2]));
            if (row[3].StartsWith("SYNTH_CTOR_INTERNAL", StringComparison.Ordinal))
            {
                continue;
            }
            usable++;
            BlockCipher cipher = Keyed(slot, PadRight(Hex(row[3]), ExpectedKeyLengths[slot]));
            string encrypted = HexOf(Encrypt(cipher, Hex(row[4])));
            string decrypted = HexOf(Decrypt(cipher, Hex(row[5])));
            if (encrypted != row[5])
            {
                failures.Add("加密:期望 " + row[5] + ",实际 " + encrypted);
            }
            if (decrypted != row[6])
            {
                failures.Add("解密:期望 " + row[6] + ",实际 " + decrypted);
            }
        }
        Assert.Empty(failures);
        Assert.Equal(expectedUsableRows, usable);
    }

    [Fact]
    public void OldShiftObjectFieldRowsPassForSlot0()
    {
        // cryptoshift_encrypt_vector.txt 的"密钥"列是直接写进槽 0 对象字段的值,不是 8 字节切片:
        // 第 1 字节是移位量(只取低 5 位),后 4 字节是异或键按 32 位数值写出的十六进制(对象里小端存放)。
        // 还原成切片:[移位量的小端 4 字节][异或键的小端 4 字节];移位量不参与结果(E-204),所以三种取值的行都应与"异或键字节"一致。
        List<string[]> rows = ReadRows("cryptoshift_encrypt_vector.txt", 3);
        Assert.Equal(3, rows.Count);
        foreach (string[] row in rows)
        {
            byte[] field = Hex(row[0]);
            Assert.Equal(5, field.Length);
            byte[] slice = new byte[8];
            slice[0] = field[0];
            slice[4] = field[4];
            slice[5] = field[3];
            slice[6] = field[2];
            slice[7] = field[1];
            BlockCipher cipher = Keyed(0, slice);
            Assert.Equal(row[2], HexOf(Encrypt(cipher, Hex(row[1]))));
            Assert.Equal(row[1], HexOf(Decrypt(cipher, Hex(row[2]))));
        }
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 16)]
    [InlineData(2, 16)]
    [InlineData(3, 16)]
    [InlineData(4, 64)]
    [InlineData(5, 24)]
    [InlineData(6, 128)]
    public void EmulatorVectorsPassForTheSlot(int slot, int expectedRows)
    {
        List<string[]> rows = ReadRows("crypto_ext_slot" + slot + "_vector.txt", 3);
        Assert.Equal(expectedRows, rows.Count);
        List<string> failures = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            string[] row = rows[i];
            byte[] synthKey = Hex(row[0]);
            Assert.Equal(ExpectedKeyLengths[slot], synthKey.Length);
            Assert.Equal(ExpectedBlockSizes[slot] * 2, row[1].Length);
            Assert.Equal(ExpectedBlockSizes[slot] * 2, row[2].Length);
            BlockCipher cipher = Keyed(slot, synthKey);
            string encrypted = HexOf(Encrypt(cipher, Hex(row[1])));
            string decrypted = HexOf(Decrypt(cipher, Hex(row[2])));
            if (encrypted != row[2])
            {
                failures.Add("第 " + (i + 1) + " 行加密:期望 " + row[2] + ",实际 " + encrypted);
            }
            if (decrypted != row[1])
            {
                failures.Add("第 " + (i + 1) + " 行解密:期望 " + row[1] + ",实际 " + decrypted);
            }
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void EmulatorVectorFilesHaveNoDuplicatesAndCoverBoundaryInputs()
    {
        // 向量文件自身的健全性:同一槽内没有重复行,且含全零与全一的边界输入(防止文件被截成一串相同的行)。
        for (int slot = 0; slot < 7; slot++)
        {
            List<string[]> rows = ReadRows("crypto_ext_slot" + slot + "_vector.txt", 3);
            Assert.Equal(rows.Count, rows.Select(r => r[0] + r[1]).Distinct().Count());
            string zeroKey = new string('0', ExpectedKeyLengths[slot] * 2);
            string zeroBlock = new string('0', ExpectedBlockSizes[slot] * 2);
            string fullKey = new string('f', ExpectedKeyLengths[slot] * 2);
            string fullBlock = new string('f', ExpectedBlockSizes[slot] * 2);
            Assert.Contains(rows, r => r[0] == zeroKey && r[1] == zeroBlock);
            Assert.Contains(rows, r => r[0] == fullKey && r[1] == fullBlock);
        }
    }
}

/// <summary>
/// 公开标准向量:对"与标准一致"的槽(1 AES-128、2 Blowfish、4 Twofish-256、6 KASUMI)逐位对拍;
/// 槽 5 是 XTEA(大端字),槽 3 是 RC6 的变体——后两者用公开向量做"一致"与"不一致"两面的断言。
/// 这些向量同时在本机用服务端二进制验证过(原版的输出与公开值一致),所以它们既是标准也是服务端行为。
/// </summary>
public sealed class CipherSlots0To6PublicVectorTests
{
    // 来源:FIPS-197 附录 C.1、附录 B,NIST SP 800-38A F.1.1(ECB-AES128)。
    [Theory]
    [InlineData("000102030405060708090a0b0c0d0e0f", "00112233445566778899aabbccddeeff", "69c4e0d86a7b0430d8cdb78070b4c55a")]
    [InlineData("2b7e151628aed2a6abf7158809cf4f3c", "3243f6a8885a308d313198a2e0370734", "3925841d02dc09fbdc118597196a0b32")]
    [InlineData("2b7e151628aed2a6abf7158809cf4f3c", "6bc1bee22e409f96e93d7e117393172a", "3ad77bb40d7a3660a89ecaf32466ef97")]
    [InlineData("2b7e151628aed2a6abf7158809cf4f3c", "ae2d8a571e03ac9c9eb76fac45af8e51", "f5d3d58503b9699de785895a96fdbaaf")]
    [InlineData("2b7e151628aed2a6abf7158809cf4f3c", "30c81c46a35ce411e5fbc1191a0a52ef", "43b1cd7f598ece23881b00e3ed030688")]
    [InlineData("2b7e151628aed2a6abf7158809cf4f3c", "f69f2445df4f9b17ad2b417be66c3710", "7b0c785e27e8ad3f8223207104725dd4")]
    public void RijndaelMatchesTheAesStandardVectors(string key, string plain, string cipher)
    {
        BlockCipher aes = Keyed(1, Hex(key));
        Assert.Equal(cipher, HexOf(Encrypt(aes, Hex(plain))));
        Assert.Equal(plain, HexOf(Decrypt(aes, Hex(cipher))));
    }

    [Fact]
    public void RijndaelMatchesTheFrameworkAesOnSyntheticInputs()
    {
        // 独立参照:.NET 自带的 AES(ECB、无填充),密钥与 4 个明文块都是固定种子的合成数据。
        for (int i = 0; i < 48; i++)
        {
            byte[] synthKey = SynthBytes(4000 + i, 16);
            byte[] synthPlain = SynthBytes(4100 + i, 64);
            using Aes reference = Aes.Create();
            reference.Key = synthKey;
            byte[] expected = reference.EncryptEcb(synthPlain, PaddingMode.None);
            BlockCipher aes = Keyed(1, synthKey);
            for (int block = 0; block < 4; block++)
            {
                byte[] plainBlock = Slice(synthPlain, block * 16, 16);
                byte[] cipherBlock = Slice(expected, block * 16, 16);
                Assert.Equal(HexOf(cipherBlock), HexOf(Encrypt(aes, plainBlock)));
                Assert.Equal(HexOf(plainBlock), HexOf(Decrypt(aes, cipherBlock)));
            }
        }
    }

    // 来源:Eric Young 的 Blowfish 参考向量(8 字节密钥)。切片 56 字节 = 8 字节密钥重复 7 次:
    // Blowfish 把密钥字节循环异或进 P 盒,56 是 8 的倍数,所以与 8 字节密钥等价(E-263 里"槽 2 与标准不一致"是旧向量文件把密钥列截断造成的误判)。
    [Theory]
    [InlineData("0000000000000000", "0000000000000000", "4ef997456198dd78")]
    [InlineData("ffffffffffffffff", "ffffffffffffffff", "51866fd5b85ecb8a")]
    [InlineData("3000000000000000", "1000000000000001", "7d856f9a613063f2")]
    [InlineData("1111111111111111", "1111111111111111", "2466dd878b963c9d")]
    [InlineData("0123456789abcdef", "1111111111111111", "61f9c3802281b096")]
    [InlineData("fedcba9876543210", "0123456789abcdef", "0aceab0fc6a0a28d")]
    public void BlowfishMatchesTheReferenceVectors(string key8, string plain, string cipher)
    {
        BlockCipher blowfish = Keyed(2, Repeat(Hex(key8), 7));
        Assert.Equal(cipher, HexOf(Encrypt(blowfish, Hex(plain))));
        Assert.Equal(plain, HexOf(Decrypt(blowfish, Hex(cipher))));
    }

    // 来源:Twofish 论文附带的 256 位密钥已知答案(密钥拆成两半写,避免在代码里内联整串)。
    [Theory]
    [InlineData("0123456789abcdeffedcba9876543210", "00112233445566778899aabbccddeeff", "00000000000000000000000000000000", "37527be0052334b89f0cfccae87cfa20")]
    [InlineData("00000000000000000000000000000000", "00000000000000000000000000000000", "00000000000000000000000000000000", "57ff739d4dc92c1bd7fc01700cc8216f")]
    public void TwofishMatchesThe256BitKnownAnswers(string keyFirstHalf, string keySecondHalf, string plain, string cipher)
    {
        BlockCipher twofish = Keyed(4, Concat(Hex(keyFirstHalf), Hex(keySecondHalf)));
        Assert.Equal(cipher, HexOf(Encrypt(twofish, Hex(plain))));
        Assert.Equal(plain, HexOf(Decrypt(twofish, Hex(cipher))));
    }

    // 来源:3GPP TS 35.203 测试集 1;其余 5 条是公共领域参考实现自带的单比特密钥向量(明文全零)。
    [Theory]
    [InlineData("9900aabbccddeeff1122334455667788", "fedcba0987654321", "514896226caa4f20")]
    [InlineData("80000000000000000000000000000000", "0000000000000000", "4b58a771afc7e5e8")]
    [InlineData("00800000000000000000000000000000", "0000000000000000", "7eef113c95bb5a77")]
    [InlineData("00008000000000000000000000000000", "0000000000000000", "5f140686d7ad5a39")]
    [InlineData("00000000000000000000000000000001", "0000000000000000", "2e1491cf70aa465d")]
    [InlineData("00000000000000000000000000000100", "0000000000000000", "b54586f4ab9ae546")]
    public void KasumiMatchesTheStandardVectors(string key, string plain, string cipher)
    {
        BlockCipher kasumi = Keyed(6, Hex(key));
        Assert.Equal(cipher, HexOf(Encrypt(kasumi, Hex(plain))));
        Assert.Equal(plain, HexOf(Decrypt(kasumi, Hex(cipher))));
    }

    // 槽 5 的 16 字节块是两个独立的 8 字节 XTEA 块(32 个循环,字节与字按大端转换),下面每条向量是两个 8 字节公开向量拼成的 16 字节。
    [Theory]
    [InlineData("00000000000000000000000000000000", "00000000000000000000000000000000", "dee9d4d8f7131ed9dee9d4d8f7131ed9")]
    [InlineData("000102030405060708090a0b0c0d0e0f", "41424344454647484141414141414141", "497df3d072612cb5e78f2d13744341d8")]
    [InlineData("000102030405060708090a0b0c0d0e0f", "5a5b6e278948d77f5a5b6e278948d77f", "41414141414141414141414141414141")]
    public void TeaSlotMatchesTheXteaVectors(string key, string plain, string cipher)
    {
        BlockCipher tea = Keyed(5, Hex(key));
        Assert.Equal(cipher, HexOf(Encrypt(tea, Hex(plain))));
        Assert.Equal(plain, HexOf(Decrypt(tea, Hex(cipher))));
    }
}

/// <summary>
/// 各槽共有的性质:往返、无状态、重设密钥、块偏移与原地、密钥切片偏移、并发、哪些密钥字节起作用、雪崩粗检。
/// 这些都是"对任何块密码都该成立"的断言,期望来自数学关系(可逆、确定、局部性),不来自本实现的输出。
/// </summary>
public sealed class CipherSlots0To6PropertyTests
{
    public static IEnumerable<object[]> AllSlots => SlotData();

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void DecryptRestoresThePlaintextAndEncryptRestoresTheCiphertext(int slot)
    {
        for (int i = 0; i < 40; i++)
        {
            byte[] synthKey = SynthBytes(100 * slot + i, ExpectedKeyLengths[slot]);
            byte[] synthPlain = SynthBytes(7000 + 100 * slot + i, ExpectedBlockSizes[slot]);
            byte[] encrypted = Encrypt(Keyed(slot, synthKey), synthPlain);
            Assert.NotEqual(HexOf(synthPlain), HexOf(encrypted));
            // 用全新的对象解密,证明解密不依赖加密时留下的状态。
            Assert.Equal(HexOf(synthPlain), HexOf(Decrypt(Keyed(slot, synthKey), encrypted)));
            // 反方向:把合成数据当密文解密,再加密,同样回到原值。
            byte[] decrypted = Decrypt(Keyed(slot, synthKey), synthPlain);
            Assert.Equal(HexOf(synthPlain), HexOf(Encrypt(Keyed(slot, synthKey), decrypted)));
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void EncryptionDoesNotDependOnEarlierCalls(int slot)
    {
        int size = ExpectedBlockSizes[slot];
        byte[] synthKey = SynthBytes(1000 + slot, ExpectedKeyLengths[slot]);
        byte[] synthPlain = SynthBytes(1010 + slot, size);
        BlockCipher cipher = Keyed(slot, synthKey);
        string first = HexOf(Encrypt(cipher, synthPlain));
        for (int i = 0; i < 6; i++)
        {
            Encrypt(cipher, SynthBytes(1020 + 10 * slot + i, size));
            Decrypt(cipher, SynthBytes(1090 + 10 * slot + i, size));
        }
        Assert.Equal(first, HexOf(Encrypt(cipher, synthPlain)));
        Assert.Equal(first, HexOf(Encrypt(Keyed(slot, synthKey), synthPlain)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void SetKeyReplacesThePreviousKeyCompletely(int slot)
    {
        byte[] synthKeyA = SynthBytes(1100 + slot, ExpectedKeyLengths[slot]);
        byte[] synthKeyB = SynthBytes(1110 + slot, ExpectedKeyLengths[slot]);
        byte[] synthPlain = SynthBytes(1120 + slot, ExpectedBlockSizes[slot]);
        BlockCipher cipher = Keyed(slot, synthKeyA);
        string withA = HexOf(Encrypt(cipher, synthPlain));
        cipher.SetKey(synthKeyB, 0);
        string withB = HexOf(Encrypt(cipher, synthPlain));
        Assert.NotEqual(withA, withB);
        Assert.Equal(HexOf(Encrypt(Keyed(slot, synthKeyB), synthPlain)), withB);
        // 换回原密钥,结果与第一次完全一致:密钥相关的表每次都从初值重建,不会残留上一把密钥的内容。
        cipher.SetKey(synthKeyA, 0);
        Assert.Equal(withA, HexOf(Encrypt(cipher, synthPlain)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void BlocksAreReadAndWrittenAtTheGivenOffsets(int slot)
    {
        int size = ExpectedBlockSizes[slot];
        byte[] synthPlain = SynthBytes(1200 + slot, size);
        BlockCipher cipher = Keyed(slot, SynthBytes(1210 + slot, ExpectedKeyLengths[slot]));
        string expectedEncrypted = HexOf(Encrypt(cipher, synthPlain));
        string expectedDecrypted = HexOf(Decrypt(cipher, synthPlain));
        for (int direction = 0; direction < 2; direction++)
        {
            byte[] input = new byte[3 + size + 4];
            Array.Fill(input, (byte)0xEE);
            Array.Copy(synthPlain, 0, input, 3, size);
            byte[] output = new byte[5 + size + 6];
            Array.Fill(output, (byte)0xDD);
            if (direction == 0)
            {
                cipher.EncryptBlock(input, 3, output, 5);
            }
            else
            {
                cipher.DecryptBlock(input, 3, output, 5);
            }
            Assert.Equal(direction == 0 ? expectedEncrypted : expectedDecrypted, HexOf(Slice(output, 5, size)));
            // 输出块之外的字节原样保留,输入数组整个没有被改写。
            for (int i = 0; i < output.Length; i++)
            {
                if (i < 5 || i >= 5 + size)
                {
                    Assert.Equal((byte)0xDD, output[i]);
                }
            }
            Assert.Equal(HexOf(synthPlain), HexOf(Slice(input, 3, size)));
            Assert.All(Slice(input, 0, 3), b => Assert.Equal((byte)0xEE, b));
            Assert.All(Slice(input, 3 + size, 4), b => Assert.Equal((byte)0xEE, b));
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void InputAndOutputMayBeTheSameBlock(int slot)
    {
        int size = ExpectedBlockSizes[slot];
        byte[] synthPlain = SynthBytes(1300 + slot, size);
        BlockCipher cipher = Keyed(slot, SynthBytes(1310 + slot, ExpectedKeyLengths[slot]));
        string expected = HexOf(Encrypt(cipher, synthPlain));
        byte[] buffer = new byte[2 + size + 2];
        Array.Fill(buffer, (byte)0xCC);
        Array.Copy(synthPlain, 0, buffer, 2, size);
        cipher.EncryptBlock(buffer, 2, buffer, 2);
        Assert.Equal(expected, HexOf(Slice(buffer, 2, size)));
        Assert.Equal((byte)0xCC, buffer[1]);
        Assert.Equal((byte)0xCC, buffer[2 + size]);
        cipher.DecryptBlock(buffer, 2, buffer, 2);
        Assert.Equal(HexOf(synthPlain), HexOf(Slice(buffer, 2, size)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void KeySliceIsTakenFromTheGivenOffsetOnly(int slot)
    {
        int keyLength = ExpectedKeyLengths[slot];
        byte[] synthKey = SynthBytes(1400 + slot, keyLength);
        byte[] synthPlain = SynthBytes(1410 + slot, ExpectedBlockSizes[slot]);
        string expected = HexOf(Encrypt(Keyed(slot, synthKey), synthPlain));

        byte[] inside = SynthBytes(1420 + slot, 11 + keyLength + 13);
        Array.Copy(synthKey, 0, inside, 11, keyLength);
        Assert.Equal(expected, HexOf(Encrypt(Keyed(slot, inside, 11), synthPlain)));
        // 切片两侧紧邻的字节随便改,结果不变。
        inside[10] ^= 0xFF;
        inside[11 + keyLength] ^= 0xFF;
        Assert.Equal(expected, HexOf(Encrypt(Keyed(slot, inside, 11), synthPlain)));

        // 切片恰好贴着数组末尾(边界),以及从数组开头取而后面还有别的字节。
        byte[] atEnd = new byte[11 + keyLength];
        Array.Copy(synthKey, 0, atEnd, 11, keyLength);
        Assert.Equal(expected, HexOf(Encrypt(Keyed(slot, atEnd, 11), synthPlain)));
        byte[] atStart = SynthBytes(1430 + slot, keyLength + 7);
        Array.Copy(synthKey, 0, atStart, 0, keyLength);
        Assert.Equal(expected, HexOf(Encrypt(Keyed(slot, atStart), synthPlain)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void ConcurrentCallsOnOneKeyedObjectGiveTheSequentialResults(int slot)
    {
        // 设好密钥后对象只读:多线程同时加解密,结果与顺序执行一致(包里的注释声明了这一点)。
        int size = ExpectedBlockSizes[slot];
        BlockCipher cipher = Keyed(slot, SynthBytes(1500 + slot, ExpectedKeyLengths[slot]));
        const int count = 256;
        byte[][] plains = new byte[count][];
        byte[][] expected = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            plains[i] = SynthBytes(2000 + i, size);
            expected[i] = Encrypt(cipher, plains[i]);
        }
        byte[][] encrypted = new byte[count][];
        byte[][] decrypted = new byte[count][];
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            encrypted[i] = Encrypt(cipher, plains[i]);
            decrypted[i] = Decrypt(cipher, expected[i]);
        });
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(HexOf(expected[i]), HexOf(encrypted[i]));
            Assert.Equal(HexOf(plains[i]), HexOf(decrypted[i]));
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void OnlyTheKeyBytesTheAlgorithmReadsAffectTheCiphertext(int slot)
    {
        // 逐字节、两种位掩码地翻转密钥:参与算法的字节必须让密文变化,不参与的(槽 0 的前 4 字节、槽 3 的第 33 到 60 字节)必须毫无影响。
        int keyLength = ExpectedKeyLengths[slot];
        byte[] synthKey = SynthBytes(1600 + slot, keyLength);
        byte[] synthPlain = SynthBytes(1610 + slot, ExpectedBlockSizes[slot]);
        string baseline = HexOf(Encrypt(Keyed(slot, synthKey), synthPlain));
        List<string> failures = new List<string>();
        for (int i = 0; i < keyLength; i++)
        {
            foreach (byte mask in new byte[] { 0x01, 0x80 })
            {
                byte[] changed = (byte[])synthKey.Clone();
                changed[i] ^= mask;
                string now = HexOf(Encrypt(Keyed(slot, changed), synthPlain));
                bool used = i >= UsedKeyStart[slot] && i < UsedKeyEnd[slot];
                if (used && now == baseline)
                {
                    failures.Add("第 " + i + " 字节掩码 " + mask + " 应改变密文却没有");
                }
                if (!used && now != baseline)
                {
                    failures.Add("第 " + i + " 字节掩码 " + mask + " 不该影响密文却改变了");
                }
            }
        }
        Assert.Empty(failures);
    }

    // 雪崩粗检(槽 0 只是异或,没有扩散,单独在下面的规则测试里断言):明文翻 1 位,同一加密单元内约一半的密文位变化,
    // 下限给宽(单次至少 1/8),均值取 40% 到 60%;槽 5 的两个 8 字节单元互不影响,另一半必须一位不变。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void FlippingOnePlaintextBitChangesAboutHalfOfTheUnitsCiphertextBits(int slot)
    {
        int block = ExpectedBlockSizes[slot];
        int unit = IndependentUnit[slot];
        int unitBits = unit * 8;
        long total = 0;
        int samples = 0;
        int minimum = int.MaxValue;
        for (int trial = 0; trial < 4; trial++)
        {
            byte[] synthKey = SynthBytes(3000 + 10 * slot + trial, ExpectedKeyLengths[slot]);
            byte[] synthPlain = SynthBytes(3100 + 10 * slot + trial, block);
            BlockCipher cipher = Keyed(slot, synthKey);
            byte[] baseline = Encrypt(cipher, synthPlain);
            for (int bit = 0; bit < block * 8; bit++)
            {
                byte[] flipped = (byte[])synthPlain.Clone();
                flipped[bit / 8] ^= (byte)(1 << (bit % 8));
                byte[] now = Encrypt(cipher, flipped);
                int flippedUnit = bit / 8 / unit;
                for (int u = 0; u < block / unit; u++)
                {
                    int changed = DifferentBits(baseline, now, u * unit, unit);
                    if (u == flippedUnit)
                    {
                        total += changed;
                        samples++;
                        minimum = Math.Min(minimum, changed);
                    }
                    else
                    {
                        Assert.Equal(0, changed);
                    }
                }
            }
        }
        double mean = (double)total / samples / unitBits;
        Assert.InRange(mean, 0.40, 0.60);
        Assert.True(minimum >= unitBits / 8, "最小变化位数 " + minimum + " 低于单元位数的 1/8");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void FlippingOneUsedKeyBitChangesAboutHalfOfTheCiphertextBits(int slot)
    {
        int blockBits = ExpectedBlockSizes[slot] * 8;
        long total = 0;
        int samples = 0;
        int minimum = int.MaxValue;
        for (int trial = 0; trial < 2; trial++)
        {
            byte[] synthKey = SynthBytes(3300 + 10 * slot + trial, ExpectedKeyLengths[slot]);
            byte[] synthPlain = SynthBytes(3400 + 10 * slot + trial, ExpectedBlockSizes[slot]);
            byte[] baseline = Encrypt(Keyed(slot, synthKey), synthPlain);
            for (int i = UsedKeyStart[slot]; i < UsedKeyEnd[slot]; i++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    byte[] changed = (byte[])synthKey.Clone();
                    changed[i] ^= (byte)(1 << bit);
                    byte[] now = Encrypt(Keyed(slot, changed), synthPlain);
                    int diff = DifferentBits(baseline, now, 0, baseline.Length);
                    total += diff;
                    samples++;
                    minimum = Math.Min(minimum, diff);
                }
            }
        }
        double mean = (double)total / samples / blockBits;
        Assert.InRange(mean, 0.40, 0.60);
        Assert.True(minimum >= blockBits / 8, "最小变化位数 " + minimum + " 低于块位数的 1/8");
    }
}

/// <summary>各槽独有的规则:槽 0 的"只剩异或"、槽 3 相对标准 RC6 的变体、槽 5 是两个独立的 XTEA 块,以及工厂映射。</summary>
public sealed class CipherSlots0To6SlotRuleTests
{
    [Theory]
    [InlineData(0, typeof(ShiftCipher))]
    [InlineData(1, typeof(RijndaelCipher))]
    [InlineData(2, typeof(BlowfishCipher))]
    [InlineData(3, typeof(Rc6Cipher))]
    [InlineData(4, typeof(TwofishCipher))]
    [InlineData(5, typeof(TeaCipher))]
    [InlineData(6, typeof(KasumiCipher))]
    public void FactoryBuildsTheClassForTheSlotWithMeasuredSizes(int slot, Type expected)
    {
        BlockCipher cipher = CipherSlots.Create(slot);
        Assert.IsType(expected, cipher);
        Assert.Equal(slot, cipher.Slot);
        Assert.Equal(ExpectedKeyLengths[slot], cipher.KeyLength);
        Assert.Equal(ExpectedBlockSizes[slot], cipher.BlockSize);
        Assert.Equal(ExpectedKeyLengths[slot], CipherSlots.KeyLengths[slot]);
        Assert.Equal(ExpectedBlockSizes[slot], CipherSlots.BlockSizes[slot]);
    }

    [Fact]
    public void ShiftXorsEveryByteWithTheLastFourKeyBytesAndDecryptsTheSameWay()
    {
        // 期望值逐字节手算:密文[i] = 明文[i] ^ 切片[4 + i];加密与解密相同(异或自反)。
        for (int i = 0; i < 24; i++)
        {
            byte[] synthKey = SynthBytes(500 + i, 8);
            byte[] synthPlain = SynthBytes(600 + i, 4);
            byte[] expected = new byte[4];
            for (int b = 0; b < 4; b++)
            {
                expected[b] = (byte)(synthPlain[b] ^ synthKey[4 + b]);
            }
            BlockCipher shift = Keyed(0, synthKey);
            Assert.Equal(HexOf(expected), HexOf(Encrypt(shift, synthPlain)));
            Assert.Equal(HexOf(expected), HexOf(Decrypt(shift, synthPlain)));
        }
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("05000000")]
    [InlineData("1f000000")]
    [InlineData("20000000")]
    [InlineData("01020304")]
    [InlineData("ffffffff")]
    public void ShiftAmountBytesAtTheStartOfTheSliceHaveNoEffect(string head)
    {
        // 原版把切片前 4 字节(低 5 位)存成移位量,但加解密里移位结果被丢弃(E-204 的"右循环移位后异或"应更正为只剩异或):
        // 不管移位量是 0、5、31 还是别的,同一个异或键给出同一个密文;00112233 ^ a1b2c3d4 = a1a3e1e7 是手算值。
        byte[] synthKey = Concat(Hex(head), Hex("a1b2c3d4"));
        BlockCipher shift = Keyed(0, synthKey);
        Assert.Equal("a1a3e1e7", HexOf(Encrypt(shift, Hex("00112233"))));
        Assert.Equal("00112233", HexOf(Decrypt(shift, Hex("a1a3e1e7"))));
    }

    [Fact]
    public void ShiftHasNoDiffusionAndFlipsExactlyTheMatchingBit()
    {
        byte[] synthPlain = SynthBytes(701, 4);
        BlockCipher shift = Keyed(0, SynthBytes(700, 8));
        byte[] baseline = Encrypt(shift, synthPlain);
        for (int bit = 0; bit < 32; bit++)
        {
            byte[] flipped = (byte[])synthPlain.Clone();
            flipped[bit / 8] ^= (byte)(1 << (bit % 8));
            byte[] now = Encrypt(shift, flipped);
            Assert.Equal(1, DifferentBits(baseline, now, 0, 4));
            Assert.Equal((byte)(1 << (bit % 8)), (byte)(baseline[bit / 8] ^ now[bit / 8]));
        }
    }

    [Fact]
    public void Rc6DiffersFromStandardRc6OnTheAesSubmissionVector()
    {
        // 标准 RC6-32/20 的 256 位密钥公开向量;切片 60 字节 = 公开密钥 32 字节 + 28 字节 0xA5 填充(后 28 字节不参与)。
        // 原版的扩展密钥表是 44 个"字节"(标准是 44 个 32 位字),所以同一把公开密钥得不到论文里的密文;
        // 原版输出是本机模拟器测量值(与 crypto_ext_slot3_vector.txt 里的对应行相同)。
        byte[] publicKey = Concat(Hex("0123456789abcdef0112233445566778"), Hex("899aabbccddeeff01032547698badcfe"));
        byte[] slice = Concat(publicKey, Repeat(new byte[] { 0xA5 }, 28));
        byte[] publicPlain = Hex("02132435465768798a9bacbdcedfe0f1");
        BlockCipher rc6 = Keyed(3, slice);
        string actual = HexOf(Encrypt(rc6, publicPlain));
        Assert.Equal("6e8ddbb862e5092b53223b04142fcd33", actual);
        Assert.NotEqual("c8241816f0d7e48920ad16a1674e5d48", actual);
        Assert.Equal(HexOf(publicPlain), HexOf(Decrypt(rc6, Hex("6e8ddbb862e5092b53223b04142fcd33"))));
    }

    [Fact]
    public void TeaSlotIsXteaAndNotClassicTea()
    {
        // 零密钥零明文:XTEA 的公开答案是 dee9d4d8f7131ed9,经典 TEA 的答案是 41ea3a0a94baa940;原版给出前者(E-204 把它记成 TEA 需更正)。
        BlockCipher tea = Keyed(5, new byte[16]);
        string actual = HexOf(Encrypt(tea, new byte[16]));
        Assert.Equal("dee9d4d8f7131ed9dee9d4d8f7131ed9", actual);
        Assert.DoesNotContain("41ea3a0a94baa940", actual);
    }

    [Fact]
    public void TeaBlockIsTwoIndependentEightByteHalvesUnderOneKey()
    {
        for (int i = 0; i < 16; i++)
        {
            byte[] synthKey = SynthBytes(800 + i, 16);
            byte[] first = SynthBytes(820 + i, 8);
            byte[] second = SynthBytes(840 + i, 8);
            BlockCipher tea = Keyed(5, synthKey);
            byte[] mixed = Encrypt(tea, Concat(first, second));
            byte[] firstTwice = Encrypt(tea, Concat(first, first));
            byte[] secondTwice = Encrypt(tea, Concat(second, second));
            // 前半只由前半明文决定,后半只由后半明文决定;两半用同一把密钥、互不链接。
            Assert.Equal(HexOf(Slice(firstTwice, 0, 8)), HexOf(Slice(mixed, 0, 8)));
            Assert.Equal(HexOf(Slice(secondTwice, 8, 8)), HexOf(Slice(mixed, 8, 8)));
            Assert.Equal(HexOf(Slice(firstTwice, 0, 8)), HexOf(Slice(firstTwice, 8, 8)));
        }
    }
}

/// <summary>参数检查:null、偏移越界、边界恰好合法、未设密钥就加解密、失败调用不留副作用。</summary>
public sealed class CipherSlots0To6ArgumentTests
{
    public static IEnumerable<object[]> AllSlots => SlotData();

    public static IEnumerable<object[]> AllSlotsBothDirections => SlotAndDirectionData();

    private static void Run(BlockCipher cipher, bool encrypt, byte[] input, int inOffset, byte[] output, int outOffset)
    {
        if (encrypt)
        {
            cipher.EncryptBlock(input, inOffset, output, outOffset);
        }
        else
        {
            cipher.DecryptBlock(input, inOffset, output, outOffset);
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void SetKeyRejectsANullKey(int slot)
    {
        BlockCipher cipher = CipherSlots.Create(slot);
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => cipher.SetKey(null, 0));
        Assert.Equal("key", error.ParamName);
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void SetKeyRejectsSlicesOutsideTheArrayAndAcceptsTheExactBoundary(int slot)
    {
        int length = ExpectedKeyLengths[slot];
        BlockCipher cipher = CipherSlots.Create(slot);
        ArgumentOutOfRangeException negative = Assert.Throws<ArgumentOutOfRangeException>(() => cipher.SetKey(new byte[length + 4], -1));
        Assert.Equal("offset", negative.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.SetKey(new byte[length - 1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.SetKey(new byte[length], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.SetKey(new byte[length + 4], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.SetKey(new byte[length], int.MaxValue));
        // 边界:切片恰好贴着数组末尾是合法的。
        cipher.SetKey(new byte[length + 4], 4);
        cipher.SetKey(new byte[length], 0);
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void AFailedSetKeyLeavesTheObjectUnusableOrUnchanged(int slot)
    {
        byte[] synthKey = SynthBytes(900 + slot, ExpectedKeyLengths[slot]);
        byte[] synthPlain = SynthBytes(910 + slot, ExpectedBlockSizes[slot]);
        BlockCipher fresh = CipherSlots.Create(slot);
        Assert.Throws<ArgumentNullException>(() => fresh.SetKey(null, 0));
        Assert.Throws<InvalidOperationException>(() => Encrypt(fresh, synthPlain));

        BlockCipher keyed = Keyed(slot, synthKey);
        string before = HexOf(Encrypt(keyed, synthPlain));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyed.SetKey(new byte[ExpectedKeyLengths[slot] - 1], 0));
        Assert.Equal(before, HexOf(Encrypt(keyed, synthPlain)));
    }

    [Theory]
    [MemberData(nameof(AllSlotsBothDirections))]
    public void BlockCallsBeforeSetKeyAreRejected(int slot, bool encrypt)
    {
        BlockCipher cipher = CipherSlots.Create(slot);
        int size = ExpectedBlockSizes[slot];
        Assert.Throws<InvalidOperationException>(() => Run(cipher, encrypt, new byte[size], 0, new byte[size], 0));
    }

    [Theory]
    [MemberData(nameof(AllSlotsBothDirections))]
    public void BlockCallsRejectNullBuffers(int slot, bool encrypt)
    {
        BlockCipher cipher = Keyed(slot, new byte[ExpectedKeyLengths[slot]]);
        byte[] block = new byte[ExpectedBlockSizes[slot]];
        ArgumentNullException noInput = Assert.Throws<ArgumentNullException>(() => Run(cipher, encrypt, null, 0, block, 0));
        Assert.Equal("input", noInput.ParamName);
        ArgumentNullException noOutput = Assert.Throws<ArgumentNullException>(() => Run(cipher, encrypt, block, 0, null, 0));
        Assert.Equal("output", noOutput.ParamName);
    }

    [Theory]
    [MemberData(nameof(AllSlotsBothDirections))]
    public void BlockCallsRejectBlocksOutsideTheArraysAndAcceptTheExactBoundary(int slot, bool encrypt)
    {
        int size = ExpectedBlockSizes[slot];
        BlockCipher cipher = Keyed(slot, SynthBytes(920 + slot, ExpectedKeyLengths[slot]));
        byte[] roomy = new byte[size + 3];
        byte[] tooSmall = new byte[size - 1];
        byte[] output = new byte[size + 3];
        Array.Fill(output, (byte)0x5A);

        Assert.Equal("inOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, -1, output, 0)).ParamName);
        Assert.Equal("inOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, 4, output, 0)).ParamName);
        Assert.Equal("inOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, tooSmall, 0, output, 0)).ParamName);
        Assert.Equal("inOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, int.MaxValue, output, 0)).ParamName);
        Assert.Equal("outOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, 0, output, -1)).ParamName);
        Assert.Equal("outOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, 0, output, 4)).ParamName);
        Assert.Equal("outOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, 0, tooSmall, 0)).ParamName);
        Assert.Equal("outOffset", Assert.Throws<ArgumentOutOfRangeException>(() => Run(cipher, encrypt, roomy, 0, output, int.MaxValue)).ParamName);
        // 被拒绝的调用没有写输出。
        Assert.All(output, b => Assert.Equal((byte)0x5A, b));
        // 边界:块恰好贴着两个数组的末尾是合法的。
        Run(cipher, encrypt, roomy, 3, output, 3);
        byte[] expected = encrypt ? Encrypt(cipher, new byte[size]) : Decrypt(cipher, new byte[size]);
        Assert.Equal(HexOf(expected), HexOf(Slice(output, 3, size)));
    }
}

/// <summary>
/// Blowfish 初值表(槽 2)的独立核对:P 盒与 S 盒就是 π 的十六进制小数位。
/// 这里用与产品代码无关的大整数 Machin 公式重算 π,再与类里的两张初值表逐字比较(表是私有静态字段,用反射读取)。
/// </summary>
public sealed class CipherSlots0To6BlowfishTableTests
{
    private const int PWords = 18;
    private const int SWords = 1024;

    [Fact]
    public void InitialTablesAreTheHexadecimalFractionDigitsOfPi()
    {
        uint[] pi = PiFractionWords(PWords + SWords);
        uint[] p = ReadTable("InitP");
        uint[] s = ReadTable("InitS");
        Assert.Equal(PWords, p.Length);
        Assert.Equal(SWords, s.Length);
        for (int i = 0; i < PWords; i++)
        {
            Assert.Equal(pi[i], p[i]);
        }
        for (int i = 0; i < SWords; i++)
        {
            Assert.Equal(pi[PWords + i], s[i]);
        }
    }

    [Fact]
    public void ThePiRecomputationStartsWithTheWellKnownDigits()
    {
        // 自检重算本身:π 的小数部分以十六进制写是 3.243F6A88 85A308D3 13198A2E 03707344(公开已知)。
        uint[] pi = PiFractionWords(4);
        Assert.Equal(new uint[] { 0x243F6A88u, 0x85A308D3u, 0x13198A2Eu, 0x03707344u }, pi);
    }

    private static uint[] ReadTable(string fieldName)
    {
        FieldInfo field = typeof(BlowfishCipher).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (uint[])field.GetValue(null);
    }

    private static uint[] PiFractionWords(int count)
    {
        int bits = 32 * count + 128;
        BigInteger one = BigInteger.One << bits;
        BigInteger pi = 16 * ArctanOfInverse(5, one) - 4 * ArctanOfInverse(239, one);
        BigInteger fraction = (pi - 3 * one) >> 128;
        uint[] words = new uint[count];
        for (int i = 0; i < count; i++)
        {
            words[i] = (uint)((fraction >> (32 * (count - 1 - i))) & uint.MaxValue);
        }
        return words;
    }

    private static BigInteger ArctanOfInverse(int x, BigInteger one)
    {
        BigInteger squared = x * x;
        BigInteger power = one / x;
        BigInteger sum = power;
        for (int k = 1; ; k++)
        {
            power /= squared;
            if (power.IsZero)
            {
                return sum;
            }
            BigInteger term = power / (2 * k + 1);
            sum += k % 2 == 1 ? -term : term;
        }
    }
}
