using Dnf.Net.Crypto;

namespace Dnf.Net.Tests;

/// <summary>
/// 槽 7–13 测试共用的向量读取与小工具。向量文件按制表符分列,'#' 开头是注释;
/// 期望值全部来自本机模拟器测量、公开规格向量或手算,不是用被测实现算出来再抄回去的。
/// </summary>
public static class CipherSlots7To13Data
{
    public static readonly int[] SlotNumbers = { 7, 8, 9, 10, 11, 12, 13 };

    public static IEnumerable<object[]> AllSlots
    {
        get
        {
            foreach (int slot in SlotNumbers)
            {
                yield return new object[] { slot };
            }
        }
    }

    /// <summary>新向量文件里的一行:emu = 本机模拟器测量,pub = 公开规格向量(同样在模拟器里复核过)。</summary>
    public sealed class VectorRow
    {
        public int Line;
        public byte[] Key;
        public byte[] Plain;
        public byte[] Cipher;
        public string Kind;
    }

    /// <summary>已有黄金向量(E-263)里一个槽的一行。</summary>
    public sealed class ExistingRow
    {
        public int Slot;
        public int BlockSize;
        public string KeyColumn;

        /// <summary>密钥列不是十六进制(槽 13:服务端对象构造时内部生成)时为 null。</summary>
        public byte[] Key;
        public byte[] Plain;
        public byte[] Cipher;
        public byte[] Decrypt;
    }

    public static string GoldenPath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "Golden", name);
    }

    public static List<VectorRow> LoadExtVectors(int slot)
    {
        string path = GoldenPath("crypto_ext_slot" + slot + "_vector.txt");
        var rows = new List<VectorRow>();
        int lineNo = 0;
        foreach (string raw in File.ReadLines(path))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] f = line.Split('\t');
            Assert.True(f.Length == 4, "向量行列数不对:槽 " + slot + " 第 " + lineNo + " 行");
            rows.Add(new VectorRow
            {
                Line = lineNo,
                Key = Convert.FromHexString(f[0]),
                Plain = Convert.FromHexString(f[1]),
                Cipher = Convert.FromHexString(f[2]),
                Kind = f[3],
            });
        }
        return rows;
    }

    public static ExistingRow LoadExistingRow(int slot)
    {
        foreach (string raw in File.ReadLines(GoldenPath("crypto_slots_vectors.txt")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] f = line.Split('\t');
            if (int.Parse(f[0]) != slot)
            {
                continue;
            }
            bool isHex = f[3].Length % 2 == 0 && f[3].All(Uri.IsHexDigit);
            return new ExistingRow
            {
                Slot = slot,
                BlockSize = int.Parse(f[2]),
                KeyColumn = f[3],
                Key = isHex ? Convert.FromHexString(f[3]) : null,
                Plain = Convert.FromHexString(f[4]),
                Cipher = Convert.FromHexString(f[5]),
                Decrypt = Convert.FromHexString(f[6]),
            };
        }
        throw new InvalidOperationException("已有黄金向量里没有槽 " + slot);
    }

    public static string Hex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] Bytes(string hex)
    {
        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// 造一个 334 字节的密钥块:把 <paramref name="key"/> 放在该槽的偏移处,其余位置填与密钥无关的杂数,
    /// 这样测试走的是和会话建立时一样的「从整块里按槽偏移取密钥」路径。
    /// </summary>
    public static byte[] KeyBlockWith(int slot, byte[] key, byte filler)
    {
        var block = new byte[CipherSlots.TotalKeyLength];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = (byte)(filler + 37 * i);
        }
        Array.Copy(key, 0, block, CipherSlots.KeyOffset(slot), key.Length);
        return block;
    }

    public static BlockCipher Keyed(int slot, byte[] key)
    {
        BlockCipher cipher = CipherSlots.Create(slot);
        cipher.SetKey(KeyBlockWith(slot, key, 0xC3), CipherSlots.KeyOffset(slot));
        return cipher;
    }

    public static byte[] Encrypt(BlockCipher cipher, byte[] plain)
    {
        var output = new byte[cipher.BlockSize];
        cipher.EncryptBlock(plain, 0, output, 0);
        return output;
    }

    public static byte[] Decrypt(BlockCipher cipher, byte[] data)
    {
        var output = new byte[cipher.BlockSize];
        cipher.DecryptBlock(data, 0, output, 0);
        return output;
    }

    public static byte[] RandomBytes(Random rng, int count)
    {
        var b = new byte[count];
        rng.NextBytes(b);
        return b;
    }

    public static int BitDifference(byte[] a, byte[] b)
    {
        int bits = 0;
        for (int i = 0; i < a.Length; i++)
        {
            bits += System.Numerics.BitOperations.PopCount((uint)(a[i] ^ b[i]));
        }
        return bits;
    }
}
