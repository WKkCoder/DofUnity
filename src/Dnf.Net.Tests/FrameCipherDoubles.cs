using System.Security.Cryptography;
using Dnf.Net.Crypto;
using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 帧层测试用的块算法替身:槽的真实算法由别的模块实现,这里按"块级预言机表"(服务端原函数在模拟器里对每个块的结果)回放,
/// 或者用标准库(槽 1 与 AES-128 一致)、或者用与密钥切片相关的简单异或,检验帧层的组包逻辑,不依赖任何槽的真实算法。
/// </summary>
internal static class FrameCipherDoubles
{
    private static Dictionary<string, byte[]> s_keys;
    private static Dictionary<(string KeyId, int Slot), (Dictionary<string, byte[]> Enc, Dictionary<string, byte[]> Dec)> s_oracle;

    /// <summary>合成会话密钥(334 字节),按 key_id 取。</summary>
    public static byte[] Key(string keyId)
    {
        if (s_keys == null)
        {
            Dictionary<string, byte[]> keys = new();
            foreach (string[] r in FrameVectorFile.Rows("frame_keys_vector.txt"))
            {
                keys[r[0]] = FrameVectorFile.Hex(r[1]);
            }
            s_keys = keys;
        }
        return s_keys[keyId];
    }

    /// <summary>按 key_id 回放预言机表的工厂:密钥必须是该 key_id 的密钥,且切片位置必须与槽表一致,否则抛异常。</summary>
    public static Func<int, BlockCipher> OracleFactory(string keyId)
    {
        EnsureOracle();
        byte[] key = Key(keyId);
        return slot =>
        {
            (Dictionary<string, byte[]> enc, Dictionary<string, byte[]> dec) = s_oracle[(keyId, slot)];
            return new OracleBlockCipher(slot, key, enc, dec);
        };
    }

    public static SessionCipher OracleSession(string keyId)
    {
        return new SessionCipher(Key(keyId), OracleFactory(keyId));
    }

    private static void EnsureOracle()
    {
        if (s_oracle != null)
        {
            return;
        }
        Dictionary<(string, int), (Dictionary<string, byte[]>, Dictionary<string, byte[]>)> table = new();
        foreach (string[] r in FrameVectorFile.Rows("frame_block_oracle_vector.txt"))
        {
            (string, int) k = (r[0], int.Parse(r[1]));
            if (!table.TryGetValue(k, out var pair))
            {
                pair = (new Dictionary<string, byte[]>(), new Dictionary<string, byte[]>());
                table[k] = pair;
            }
            pair.Item1[r[2]] = FrameVectorFile.Hex(r[3]);
            pair.Item2[r[3]] = FrameVectorFile.Hex(r[2]);
        }
        s_oracle = table;
    }
}

/// <summary>查表的块算法:表里没有的块抛异常(说明向量没覆盖到,不会悄悄通过)。</summary>
internal sealed class OracleBlockCipher : BlockCipher
{
    private readonly byte[] _expectedKey;
    private readonly Dictionary<string, byte[]> _enc;
    private readonly Dictionary<string, byte[]> _dec;

    public OracleBlockCipher(int slot, byte[] expectedKey, Dictionary<string, byte[]> enc, Dictionary<string, byte[]> dec)
    {
        Slot = slot;
        _expectedKey = expectedKey;
        _enc = enc;
        _dec = dec;
    }

    public override int Slot { get; }

    public override int BlockSize => CipherSlots.BlockSizes[Slot];

    public override int KeyLength => CipherSlots.KeyLengths[Slot];

    public override void SetKey(byte[] key, int offset)
    {
        // 切片位置按"各槽密钥长度依次相加"手算:槽 0..13 的起始偏移
        int[] expectedOffsets = { 0, 8, 24, 80, 140, 172, 188, 204, 220, 236, 252, 268, 278, 318 };
        Assert.Equal(expectedOffsets[Slot], offset);
        Assert.Equal(_expectedKey, key);
    }

    public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
    {
        Lookup(_enc, input, inOffset).CopyTo(output, outOffset);
    }

    public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
    {
        Lookup(_dec, input, inOffset).CopyTo(output, outOffset);
    }

    private byte[] Lookup(Dictionary<string, byte[]> table, byte[] input, int offset)
    {
        string key = Convert.ToHexString(input, offset, BlockSize).ToLowerInvariant();
        if (!table.TryGetValue(key, out byte[] value))
        {
            throw new KeyNotFoundException("槽 " + Slot + " 的预言机表里没有块 " + key);
        }
        return value;
    }
}

/// <summary>槽 1 的替身:标准库 AES-128 的 ECB,单块。</summary>
internal sealed class AesSlotCipher : BlockCipher
{
    private byte[] _key;

    public override int Slot => 1;

    public override int BlockSize => 16;

    public override int KeyLength => 16;

    public override void SetKey(byte[] key, int offset)
    {
        _key = new byte[16];
        Array.Copy(key, offset, _key, 0, 16);
    }

    public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
    {
        using Aes aes = Aes.Create();
        aes.Key = _key;
        aes.EncryptEcb(new ReadOnlySpan<byte>(input, inOffset, 16), new Span<byte>(output, outOffset, 16), PaddingMode.None);
    }

    public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
    {
        using Aes aes = Aes.Create();
        aes.Key = _key;
        aes.DecryptEcb(new ReadOnlySpan<byte>(input, inOffset, 16), new Span<byte>(output, outOffset, 16), PaddingMode.None);
    }
}

/// <summary>
/// 与密钥切片相关的替身:块的第 i 个字节异或本槽密钥切片的第 i 个字节(块长大于密钥长时循环取)。
/// 用来检查"每个槽拿到的是密钥块里属于自己的那一段"。
/// </summary>
internal sealed class SliceXorCipher : BlockCipher
{
    private byte[] _slice;

    public SliceXorCipher(int slot)
    {
        Slot = slot;
    }

    public override int Slot { get; }

    public override int BlockSize => CipherSlots.BlockSizes[Slot];

    public override int KeyLength => CipherSlots.KeyLengths[Slot];

    public byte[] Slice => _slice;

    public override void SetKey(byte[] key, int offset)
    {
        _slice = new byte[KeyLength];
        Array.Copy(key, offset, _slice, 0, KeyLength);
    }

    public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
    {
        for (int i = 0; i < BlockSize; i++)
        {
            output[outOffset + i] = (byte)(input[inOffset + i] ^ _slice[i % _slice.Length]);
        }
    }

    public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
    {
        EncryptBlock(input, inOffset, output, outOffset);
    }
}
