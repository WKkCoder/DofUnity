using Dnf.Net.Crypto;
using static Dnf.Net.Tests.CipherSlots7To13Data;

namespace Dnf.Net.Tests;

/// <summary>
/// 槽 7–13 的向量测试:已有黄金向量(E-263)、本机模拟器新测的向量、公开规格向量。
/// 向量里的密钥一律按「放进 334 字节密钥块的槽偏移处再取」的方式使用(E-262)。
/// </summary>
public sealed class CipherSlots7To13VectorTests
{
    public static IEnumerable<object[]> AllSlots => CipherSlots7To13Data.AllSlots;

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void ExistingGoldenRowIsReproduced(int slot)
    {
        ExistingRow g = LoadExistingRow(slot);
        BlockCipher probe = CipherSlots.Create(slot);
        Assert.Equal(probe.BlockSize, g.BlockSize);

        byte[] key = g.Key;
        if (key == null)
        {
            // 槽 13:这一行的密钥是服务端对象构造时内部生成的;新向量文件里有一行用同一明文复现了同一密文,密钥取自那一行。
            VectorRow match = LoadExtVectors(slot).Single(r => r.Plain.SequenceEqual(g.Plain) && r.Cipher.SequenceEqual(g.Cipher));
            key = match.Key;
        }
        else if (key.Length == probe.KeyLength - 1)
        {
            // 槽 12:黄金向量的密钥列只有 39 个字符对,服务端实际用的是尾部补一个 0x00 的 40 字节。
            Assert.Equal(12, slot);
            Array.Resize(ref key, probe.KeyLength);
        }
        Assert.Equal(probe.KeyLength, key.Length);

        BlockCipher c = Keyed(slot, key);
        Assert.Equal(Hex(g.Cipher), Hex(Encrypt(c, g.Plain)));
        Assert.Equal(Hex(g.Plain), Hex(Decrypt(c, g.Cipher)));
        Assert.Equal(Hex(g.Decrypt), Hex(Decrypt(c, g.Cipher)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void EmulatorVectorsAreReproduced(int slot)
    {
        List<VectorRow> rows = LoadExtVectors(slot).Where(r => r.Kind == "emu").ToList();
        Assert.True(rows.Count >= 8, "槽 " + slot + " 的模拟器向量不足 8 行:" + rows.Count);
        CheckRows(slot, rows);
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void PublicSpecVectorsAreReproduced(int slot)
    {
        List<VectorRow> rows = LoadExtVectors(slot).Where(r => r.Kind == "pub").ToList();
        Assert.True(rows.Count >= 1, "槽 " + slot + " 没有公开向量");
        CheckRows(slot, rows);
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void EveryRowIsEitherEmulatorOrPublic(int slot)
    {
        foreach (VectorRow r in LoadExtVectors(slot))
        {
            Assert.True(r.Kind == "emu" || r.Kind == "pub", "槽 " + slot + " 第 " + r.Line + " 行类型不明:" + r.Kind);
            BlockCipher c = CipherSlots.Create(slot);
            Assert.True(r.Key.Length == c.KeyLength && r.Plain.Length == c.BlockSize && r.Cipher.Length == c.BlockSize,
                "槽 " + slot + " 第 " + r.Line + " 行长度与槽表不符");
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void KeyComesOnlyFromItsSliceOfTheKeyBlock(int slot)
    {
        VectorRow row = LoadExtVectors(slot).First(r => r.Kind == "emu");
        int offset = CipherSlots.KeyOffset(slot);
        int length = CipherSlots.KeyLengths[slot];

        byte[] block = KeyBlockWith(slot, row.Key, 0x11);
        BlockCipher a = CipherSlots.Create(slot);
        a.SetKey(block, offset);
        Assert.Equal(Hex(row.Cipher), Hex(Encrypt(a, row.Plain)));

        // 把片之外的所有字节取反:结果不应有任何变化(相邻槽的密钥字节不参与)。
        byte[] scrambled = (byte[])block.Clone();
        for (int i = 0; i < scrambled.Length; i++)
        {
            if (i < offset || i >= offset + length)
            {
                scrambled[i] ^= 0xFF;
            }
        }
        BlockCipher b = CipherSlots.Create(slot);
        b.SetKey(scrambled, offset);
        Assert.Equal(Hex(row.Cipher), Hex(Encrypt(b, row.Plain)));

        // 贴着片的最后一个字节再改一位,结果必须变化(片的最后一个字节也参与)。
        byte[] tail = (byte[])block.Clone();
        tail[offset + length - 1] ^= 0x01;
        BlockCipher t = CipherSlots.Create(slot);
        t.SetKey(tail, offset);
        Assert.NotEqual(Hex(row.Cipher), Hex(Encrypt(t, row.Plain)));

        // 片的第一个字节同理。
        byte[] head = (byte[])block.Clone();
        head[offset] ^= 0x80;
        BlockCipher h = CipherSlots.Create(slot);
        h.SetKey(head, offset);
        Assert.NotEqual(Hex(row.Cipher), Hex(Encrypt(h, row.Plain)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void EveryKeyByteInTheSliceChangesTheCiphertext(int slot)
    {
        VectorRow row = LoadExtVectors(slot).First(r => r.Kind == "emu");
        byte[] baseline = row.Cipher;
        for (int i = 0; i < row.Key.Length; i++)
        {
            foreach (byte mask in new byte[] { 0x01, 0x80 })
            {
                byte[] key = (byte[])row.Key.Clone();
                key[i] ^= mask;
                byte[] ct = Encrypt(Keyed(slot, key), row.Plain);
                Assert.True(Hex(ct) != Hex(baseline), "槽 " + slot + " 密钥第 " + i + " 字节翻转 " + mask + " 后密文没有变化");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void KeyBytesBeyondTheSliceAreIgnored(int slot)
    {
        VectorRow row = LoadExtVectors(slot).First(r => r.Kind == "emu");
        // 传入比片更长的缓冲:多出来的字节不同,结果必须一样(服务端同样只取槽表规定的长度)。
        var longer = new byte[row.Key.Length + 24];
        Array.Copy(row.Key, longer, row.Key.Length);
        for (int i = row.Key.Length; i < longer.Length; i++)
        {
            longer[i] = (byte)(0x5A ^ (i * 13));
        }
        BlockCipher c = CipherSlots.Create(slot);
        c.SetKey(longer, 0);
        Assert.Equal(Hex(row.Cipher), Hex(Encrypt(c, row.Plain)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void ReKeyingLeavesNoResidue(int slot)
    {
        List<VectorRow> rows = LoadExtVectors(slot).Where(r => r.Kind == "emu").ToList();
        VectorRow first = rows[0];
        VectorRow other = rows.First(r => !r.Key.SequenceEqual(first.Key));
        BlockCipher c = Keyed(slot, first.Key);
        Assert.Equal(Hex(first.Cipher), Hex(Encrypt(c, first.Plain)));

        c.SetKey(KeyBlockWith(slot, other.Key, 0x22), CipherSlots.KeyOffset(slot));
        Assert.Equal(Hex(other.Cipher), Hex(Encrypt(c, other.Plain)));
        Assert.Equal(Hex(other.Plain), Hex(Decrypt(c, other.Cipher)));

        c.SetKey(KeyBlockWith(slot, first.Key, 0x33), CipherSlots.KeyOffset(slot));
        Assert.Equal(Hex(first.Cipher), Hex(Encrypt(c, first.Plain)));
        Assert.Equal(Hex(first.Plain), Hex(Decrypt(c, first.Cipher)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void InstancesOfTheSameSlotKeepTheirOwnKeys(int slot)
    {
        List<VectorRow> rows = LoadExtVectors(slot).Where(r => r.Kind == "emu").ToList();
        VectorRow a = rows[0];
        VectorRow b = rows.First(r => !r.Key.SequenceEqual(a.Key));
        BlockCipher ca = Keyed(slot, a.Key);
        BlockCipher cb = Keyed(slot, b.Key);
        // 两个连接各有一个同槽对象,交替使用时互不影响(对象之间不共享可变状态)。
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Hex(a.Cipher), Hex(Encrypt(ca, a.Plain)));
            Assert.Equal(Hex(b.Cipher), Hex(Encrypt(cb, b.Plain)));
            Assert.Equal(Hex(a.Plain), Hex(Decrypt(ca, a.Cipher)));
            Assert.Equal(Hex(b.Plain), Hex(Decrypt(cb, b.Cipher)));
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void KeyMaterialIsCopiedWhenTheKeyIsSet(int slot)
    {
        VectorRow row = LoadExtVectors(slot).First(r => r.Kind == "emu");
        byte[] block = KeyBlockWith(slot, row.Key, 0x44);
        BlockCipher c = CipherSlots.Create(slot);
        c.SetKey(block, CipherSlots.KeyOffset(slot));
        // 调用方随后清零或复用密钥缓冲,已设好的密钥不能受影响。
        Array.Clear(block, 0, block.Length);
        Assert.Equal(Hex(row.Cipher), Hex(Encrypt(c, row.Plain)));
        Assert.Equal(Hex(row.Plain), Hex(Decrypt(c, row.Cipher)));
    }

    private static void CheckRows(int slot, List<VectorRow> rows)
    {
        foreach (VectorRow r in rows)
        {
            BlockCipher c = Keyed(slot, r.Key);
            Assert.True(Hex(r.Cipher) == Hex(Encrypt(c, r.Plain)), "槽 " + slot + " 第 " + r.Line + " 行加密结果不一致");
            Assert.True(Hex(r.Plain) == Hex(Decrypt(c, r.Cipher)), "槽 " + slot + " 第 " + r.Line + " 行解密结果不一致");
        }
    }
}

/// <summary>
/// 槽 7–13 与公开标准的对照:能直接引用公开向量的地方逐位对照;服务端与公开较新版本不同的地方(槽 7、8),
/// 明确断言「不等于公开较新版本的向量」,把这个取舍钉住。
/// </summary>
public sealed class CipherSlots7To13PublicTests
{
    /// <summary>RFC 2144 B.2 的整套维护测试:一百万轮,每轮 2 次设密钥、4 次加密;终值来自 RFC。这么多轮下 8 个 S 盒的表项都会被反复用到,任何一项出错终值都对不上。</summary>
    [Fact]
    public void Cast5FullMaintenanceTestFromRfc2144()
    {
        byte[] a = Bytes("0123456712345678234567893456789a");
        byte[] b = Bytes("0123456712345678234567893456789a");
        BlockCipher c = CipherSlots.Create(10);
        for (int i = 0; i < 1000000; i++)
        {
            c.SetKey(b, 0);
            c.EncryptBlock(a, 0, a, 0);
            c.EncryptBlock(a, 8, a, 8);
            c.SetKey(a, 0);
            c.EncryptBlock(b, 0, b, 0);
            c.EncryptBlock(b, 8, b, 8);
        }
        Assert.Equal("eea9d0a249fd3ba6b3436fb89d6dca92", Hex(a));
        Assert.Equal("b2c95eb00c31ad7180ac05b8e83d696e", Hex(b));
    }

    /// <summary>
    /// 槽 7:较新的公开版本把字按大端装载,全零密钥全零明文的密文是 dee9d4d8f7131ed9;服务端按小端装载。
    /// 全零输入下两个版本的内部状态完全相同,只有输出字节序不同,所以服务端的密文应是它按 4 字节一组反转字节序的结果。
    /// </summary>
    [Fact]
    public void XteaLoadsWordsLittleEndianUnlikeTheNewerPublicVersion()
    {
        byte[] bigEndianVersion = Bytes("dee9d4d8f7131ed9");
        byte[] expected =
        {
            bigEndianVersion[3], bigEndianVersion[2], bigEndianVersion[1], bigEndianVersion[0],
            bigEndianVersion[7], bigEndianVersion[6], bigEndianVersion[5], bigEndianVersion[4],
        };
        byte[] actual = Encrypt(Keyed(7, new byte[16]), new byte[8]);
        Assert.NotEqual(Hex(bigEndianVersion), Hex(actual));
        Assert.Equal(Hex(expected), Hex(actual));
        // 再对照早期公开版本自测里的一组非对称向量(密钥、明文、密文都不是全零)。
        Assert.Equal("75d7c5bfcf58c93f", Hex(Encrypt(Keyed(7, Bytes("78563412f0cdcb9a48372615c0bfae9d")), Bytes("0102030405060708"))));
    }

    /// <summary>
    /// 槽 8:公开规格(Noekeon 直接密钥模式)的向量,规格密文是 a6ecb8a861fd62d91302fe9e47013fc3;
    /// 服务端的 π1/π2 旋转第 0 个字(规格是第 1 个字),所以结果不同。服务端的值来自模拟器(向量文件里有同一行)。
    /// </summary>
    [Fact]
    public void NoekeonDiffersFromTheSpecVectorExactlyLikeTheServer()
    {
        byte[] key = Bytes("aa3c8c86d98bf8be21e0360978fbe490");
        byte[] plain = Bytes("e4966cd313a06cafd023c9fd45322316");
        VectorRow row = LoadExtVectors(8).Single(r => r.Key.SequenceEqual(key) && r.Plain.SequenceEqual(plain));
        byte[] actual = Encrypt(Keyed(8, key), plain);
        Assert.NotEqual("a6ecb8a861fd62d91302fe9e47013fc3", Hex(actual));
        Assert.Equal(Hex(row.Cipher), Hex(actual));
        Assert.Equal(Hex(plain), Hex(Decrypt(Keyed(8, key), row.Cipher)));
    }

    /// <summary>槽 10:服务端固定传 16 字节密钥,走 128 位密钥的完整 16 轮;RFC 2144 B.1 的 128 位向量即这一路径。</summary>
    [Fact]
    public void Cast5Uses128BitKeyPathWithSixteenRounds()
    {
        BlockCipher c = Keyed(10, Bytes("0123456712345678234567893456789a"));
        Assert.Equal("238b4fe5847e44b2", Hex(Encrypt(c, Bytes("0123456789abcdef"))));
        Assert.Equal("0123456789abcdef", Hex(Decrypt(c, Bytes("238b4fe5847e44b2"))));
    }

    /// <summary>槽 11:NIST 公布的 Skipjack 规格向量(80 位密钥,16 位字大端)。</summary>
    [Fact]
    public void SkipjackMatchesTheNistSpecVector()
    {
        BlockCipher c = Keyed(11, Bytes("00998877665544332211"));
        Assert.Equal("2587cae27a12d300", Hex(Encrypt(c, Bytes("33221100ddccbbaa"))));
        Assert.Equal("33221100ddccbbaa", Hex(Decrypt(c, Bytes("2587cae27a12d300"))));
    }

    /// <summary>槽 9:Khazad 官方向量(单个明文位或单个密钥位为 1)。</summary>
    [Theory]
    [InlineData("80000000000000000000000000000000", "0000000000000000", "49a4ce32ac190e3f")]
    [InlineData("00000000000000000000000000000001", "0000000000000000", "645d773e40abdd53")]
    [InlineData("00000000000000000000000000000000", "8000000000000000", "9e399864f78eca02")]
    [InlineData("00000000000000000000000000000000", "0000000000000001", "a9df3d2c64d3ea28")]
    public void KhazadMatchesTheOfficialVectors(string key, string plain, string cipher)
    {
        BlockCipher c = Keyed(9, Bytes(key));
        Assert.Equal(cipher, Hex(Encrypt(c, Bytes(plain))));
        Assert.Equal(plain, Hex(Decrypt(c, Bytes(cipher))));
    }

    /// <summary>槽 13:Anubis 原版(非 tweak)的公开向量;tweak 版的 S 盒不同,同样输入得到不同密文。</summary>
    [Theory]
    [InlineData("80000000000000000000000000000000", "f06860fc6730e818f132c78af4132afe")]
    [InlineData("00000000000000000000000000000001", "a866848007745c89fc5eb5bad4fe326d")]
    public void AnubisMatchesTheOriginalNonTweakVectors(string key, string cipher)
    {
        BlockCipher c = Keyed(13, Bytes(key));
        byte[] zero = new byte[16];
        Assert.Equal(cipher, Hex(Encrypt(c, zero)));
        Assert.Equal(Hex(zero), Hex(Decrypt(c, Bytes(cipher))));
    }

    /// <summary>槽 12:MULTI2 的公开自测向量(系统密钥全零,数据密钥 0123456789abcdef,128 轮)。</summary>
    [Fact]
    public void Multi2MatchesThePublicSelfTestVector()
    {
        var key = new byte[40];
        Array.Copy(Bytes("0123456789abcdef"), 0, key, 32, 8);
        BlockCipher c = Keyed(12, key);
        Assert.Equal("f89440845e11cf89", Hex(Encrypt(c, Bytes("0000000000000001"))));
        Assert.Equal("0000000000000001", Hex(Decrypt(c, Bytes("f89440845e11cf89"))));
    }
}

/// <summary>
/// 槽 7–13 的性质与错误行为测试:随机往返、原地加解密、输出范围、单比特雪崩、长度/偏移/空参数。
/// 这些测试不依赖具体向量,专门抓「向量刚好没覆盖到」的偏差。
/// </summary>
public sealed class CipherSlots7To13PropertyTests
{
    private static readonly string[] ClassNames =
    {
        "XteaCipher", "NoekeonCipher", "KhazadCipher", "Cast5Cipher", "SkipjackCipher", "Multi2Cipher", "AnubisCipher",
    };

    public static IEnumerable<object[]> AllSlots => CipherSlots7To13Data.AllSlots;

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void MetadataMatchesTheSlotTable(int slot)
    {
        BlockCipher c = CipherSlots.Create(slot);
        Assert.Equal(slot, c.Slot);
        Assert.Equal(CipherSlots.BlockSizes[slot], c.BlockSize);
        Assert.Equal(CipherSlots.KeyLengths[slot], c.KeyLength);
        Assert.Equal(ClassNames[slot - 7], c.GetType().Name);
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void RandomKeysAndBlocksRoundTrip(int slot)
    {
        var rng = new Random(7000 + slot);
        BlockCipher c = CipherSlots.Create(slot);
        int blockSize = c.BlockSize;
        for (int i = 0; i < 300; i++)
        {
            byte[] key = RandomBytes(rng, c.KeyLength);
            byte[] plain = RandomBytes(rng, blockSize);
            c.SetKey(key, 0);
            byte[] ct = Encrypt(c, plain);
            Assert.NotEqual(Hex(plain), Hex(ct));
            Assert.Equal(Hex(plain), Hex(Decrypt(c, ct)));

            // 把随机块当密文解密再加密,同样必须回到原块:解密是加密的真逆,不是只对加密输出可逆。
            byte[] d = Decrypt(c, plain);
            Assert.Equal(Hex(plain), Hex(Encrypt(c, d)));
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void EncryptionIsAPermutationOfTheBlockSpaceForAFixedKey(int slot)
    {
        // 固定密钥下不同明文必须得到不同密文(抽样 400 个明文,含只差 1 位的邻居)。
        var rng = new Random(8100 + slot);
        BlockCipher c = Keyed(slot, RandomBytes(rng, CipherSlots.KeyLengths[slot]));
        var seen = new HashSet<string>();
        for (int i = 0; i < 400; i++)
        {
            byte[] plain = RandomBytes(rng, c.BlockSize);
            Assert.True(seen.Add(Hex(Encrypt(c, plain))), "不同明文出现了相同密文");
            plain[0] ^= 1;
            Assert.True(seen.Add(Hex(Encrypt(c, plain))), "只差 1 位的明文出现了相同密文");
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void InPlaceEncryptionAndDecryptionWork(int slot)
    {
        var rng = new Random(7100 + slot);
        BlockCipher c = Keyed(slot, RandomBytes(rng, CipherSlots.KeyLengths[slot]));
        int bs = c.BlockSize;
        byte[] plain = RandomBytes(rng, bs);
        byte[] expected = Encrypt(c, plain);

        var buffer = new byte[3 + bs + 5];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = 0xA5;
        }
        Array.Copy(plain, 0, buffer, 3, bs);
        c.EncryptBlock(buffer, 3, buffer, 3);
        Assert.Equal(Hex(expected), Hex(buffer.AsSpan(3, bs).ToArray()));
        // 块之外的哨兵字节不能被碰。
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0xA5, buffer[i]);
        }
        for (int i = 3 + bs; i < buffer.Length; i++)
        {
            Assert.Equal(0xA5, buffer[i]);
        }
        c.DecryptBlock(buffer, 3, buffer, 3);
        Assert.Equal(Hex(plain), Hex(buffer.AsSpan(3, bs).ToArray()));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void OutputWritesExactlyOneBlockAtTheOffset(int slot)
    {
        var rng = new Random(7200 + slot);
        BlockCipher c = Keyed(slot, RandomBytes(rng, CipherSlots.KeyLengths[slot]));
        int bs = c.BlockSize;
        // 输入夹在更长的数组中间,输出也是,偏移各不相同。
        var input = new byte[2 * bs + 9];
        rng.NextBytes(input);
        byte[] one = input.AsSpan(4, bs).ToArray();
        byte[] expectedEnc = Encrypt(c, one);

        var output = new byte[3 * bs + 7];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = 0x3C;
        }
        c.EncryptBlock(input, 4, output, 6);
        Assert.Equal(Hex(expectedEnc), Hex(output.AsSpan(6, bs).ToArray()));
        for (int i = 0; i < output.Length; i++)
        {
            if (i < 6 || i >= 6 + bs)
            {
                Assert.True(output[i] == 0x3C, "输出范围之外的第 " + i + " 字节被改动");
            }
        }
        // 输入数组不能被改动。
        byte[] inputCopy = (byte[])input.Clone();
        c.DecryptBlock(input, 4, output, 6);
        Assert.Equal(Hex(inputCopy), Hex(input));
        Assert.Equal(Hex(Decrypt(c, one)), Hex(output.AsSpan(6, bs).ToArray()));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void ConsecutiveBlocksAreEncryptedIndependently(int slot)
    {
        var rng = new Random(7300 + slot);
        BlockCipher c = Keyed(slot, RandomBytes(rng, CipherSlots.KeyLengths[slot]));
        int bs = c.BlockSize;
        byte[] data = RandomBytes(rng, 5 * bs);
        byte[] buffer = (byte[])data.Clone();
        for (int i = 0; i < 5; i++)
        {
            c.EncryptBlock(buffer, i * bs, buffer, i * bs);
        }
        for (int i = 0; i < 5; i++)
        {
            byte[] block = data.AsSpan(i * bs, bs).ToArray();
            Assert.Equal(Hex(Encrypt(c, block)), Hex(buffer.AsSpan(i * bs, bs).ToArray()));
        }
        // 相同明文块在同一密钥下得到相同密文(ECB 语义,会话层的分组方式依赖这一点)。
        byte[] twice = new byte[2 * bs];
        Array.Copy(data, 0, twice, 0, bs);
        Array.Copy(data, 0, twice, bs, bs);
        c.EncryptBlock(twice, 0, twice, 0);
        c.EncryptBlock(twice, bs, twice, bs);
        Assert.Equal(Hex(twice.AsSpan(0, bs).ToArray()), Hex(twice.AsSpan(bs, bs).ToArray()));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void SinglePlaintextBitFlipChangesAboutHalfOfTheCiphertextBits(int slot)
    {
        var rng = new Random(9000 + slot);
        int blockBits = CipherSlots.BlockSizes[slot] * 8;
        long total = 0;
        int flips = 0;
        int minDiff = int.MaxValue;
        for (int round = 0; round < 4; round++)
        {
            BlockCipher c = Keyed(slot, RandomBytes(rng, CipherSlots.KeyLengths[slot]));
            byte[] plain = RandomBytes(rng, c.BlockSize);
            byte[] baseline = Encrypt(c, plain);
            for (int bit = 0; bit < blockBits; bit++)
            {
                byte[] flipped = (byte[])plain.Clone();
                flipped[bit / 8] ^= (byte)(1 << (bit % 8));
                int diff = BitDifference(baseline, Encrypt(c, flipped));
                total += diff;
                flips++;
                minDiff = Math.Min(minDiff, diff);
            }
        }
        double mean = total / (double)((long)flips * blockBits);
        Assert.InRange(mean, 0.45, 0.55);
        Assert.True(minDiff >= blockBits / 5, "槽 " + slot + " 有一次明文翻 1 位只改变了 " + minDiff + " 个密文位");
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void SingleKeyBitFlipChangesAboutHalfOfTheCiphertextBits(int slot)
    {
        var rng = new Random(9100 + slot);
        int blockBits = CipherSlots.BlockSizes[slot] * 8;
        int keyBits = CipherSlots.KeyLengths[slot] * 8;
        long total = 0;
        int flips = 0;
        int minDiff = int.MaxValue;
        for (int round = 0; round < 2; round++)
        {
            byte[] key = RandomBytes(rng, CipherSlots.KeyLengths[slot]);
            byte[] plain = RandomBytes(rng, CipherSlots.BlockSizes[slot]);
            byte[] baseline = Encrypt(Keyed(slot, key), plain);
            for (int bit = 0; bit < keyBits; bit++)
            {
                byte[] flipped = (byte[])key.Clone();
                flipped[bit / 8] ^= (byte)(1 << (bit % 8));
                int diff = BitDifference(baseline, Encrypt(Keyed(slot, flipped), plain));
                total += diff;
                flips++;
                minDiff = Math.Min(minDiff, diff);
            }
        }
        double mean = total / (double)((long)flips * blockBits);
        Assert.InRange(mean, 0.45, 0.55);
        Assert.True(minDiff >= blockBits / 5, "槽 " + slot + " 有一次密钥翻 1 位只改变了 " + minDiff + " 个密文位");
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void SetKeyRejectsNullShortAndOutOfRangeInput(int slot)
    {
        BlockCipher c = CipherSlots.Create(slot);
        int kl = c.KeyLength;
        Assert.Throws<ArgumentNullException>(() => c.SetKey(null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[kl - 1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[kl], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[kl + 5], 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[kl], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[0], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[kl], int.MaxValue));
        // 刚好贴着数组末尾是合法的(边界)。
        c.SetKey(new byte[kl + 3], 3);
        c.SetKey(new byte[kl], 0);
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void FailedSetKeyKeepsThePreviousKey(int slot)
    {
        VectorRow row = LoadExtVectors(slot).First(r => r.Kind == "emu");
        BlockCipher c = Keyed(slot, row.Key);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[row.Key.Length - 1], 0));
        Assert.Throws<ArgumentNullException>(() => c.SetKey(null, 0));
        Assert.Equal(Hex(row.Cipher), Hex(Encrypt(c, row.Plain)));
        Assert.Equal(Hex(row.Plain), Hex(Decrypt(c, row.Cipher)));
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void BlockOperationsRejectNullShortAndOutOfRangeBuffers(int slot)
    {
        VectorRow row = LoadExtVectors(slot).First(r => r.Kind == "emu");
        BlockCipher c = Keyed(slot, row.Key);
        int bs = c.BlockSize;
        var good = new byte[bs];
        var sentinel = new byte[bs + 2];
        for (int i = 0; i < sentinel.Length; i++)
        {
            sentinel[i] = 0xEE;
        }

        foreach (bool encrypt in new[] { true, false })
        {
            void Run(byte[] input, int inOffset, byte[] output, int outOffset)
            {
                if (encrypt)
                {
                    c.EncryptBlock(input, inOffset, output, outOffset);
                }
                else
                {
                    c.DecryptBlock(input, inOffset, output, outOffset);
                }
            }

            Assert.Throws<ArgumentNullException>(() => Run(null, 0, good, 0));
            Assert.Throws<ArgumentNullException>(() => Run(good, 0, null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(new byte[bs - 1], 0, good, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, 0, new byte[bs - 1], 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, 1, good, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, 0, good, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, -1, good, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, 0, good, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, int.MaxValue, good, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(good, 0, good, int.MaxValue));

            // 出错时输出缓冲一个字节也不能写。
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(new byte[bs - 1], 0, sentinel, 1));
            foreach (byte b in sentinel)
            {
                Assert.Equal(0xEE, b);
            }

            // 输入、输出各自刚好贴着数组末尾是合法的(边界)。
            var tailIn = new byte[bs + 2];
            var tailOut = new byte[bs + 2];
            Run(tailIn, 2, tailOut, 2);
        }
    }

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void BlockOperationsRequireAKey(int slot)
    {
        BlockCipher c = CipherSlots.Create(slot);
        var block = new byte[c.BlockSize];
        Assert.Throws<InvalidOperationException>(() => c.EncryptBlock(block, 0, block, 0));
        Assert.Throws<InvalidOperationException>(() => c.DecryptBlock(block, 0, block, 0));
        // 设过密钥后即可使用;设密钥失败不算设过。
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetKey(new byte[c.KeyLength - 1], 0));
        Assert.Throws<InvalidOperationException>(() => c.EncryptBlock(block, 0, block, 0));
        c.SetKey(new byte[c.KeyLength], 0);
        c.EncryptBlock(block, 0, block, 0);
    }
}
