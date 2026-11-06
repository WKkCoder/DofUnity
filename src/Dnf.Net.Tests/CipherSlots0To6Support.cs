using Dnf.Net.Crypto;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话算法槽 0 到 6 测试的共用工具:十六进制、金标准文件读取、确定性随机数据、带密钥的对象。
/// 期望值只来自三处:原有金标准、本机模拟器向量文件、公开标准向量;这里的"期望表"是服务端对象的实测值,与产品代码里的静态表分开写一份,让两边互相校对(E-204、E-262)。
/// </summary>
internal static class CipherSlots0To6Support
{
    /// <summary>切片密钥长度(字节),下标是槽号。</summary>
    internal static readonly int[] ExpectedKeyLengths = { 8, 16, 56, 60, 32, 16, 16 };

    /// <summary>块长(字节),下标是槽号。</summary>
    internal static readonly int[] ExpectedBlockSizes = { 4, 16, 8, 16, 16, 16, 8 };

    /// <summary>真正参与加解密的切片字节区间 [起, 止):槽 0 只有后 4 字节(异或键),槽 3 只有前 32 字节,其余整片参与。</summary>
    internal static readonly int[] UsedKeyStart = { 4, 0, 0, 0, 0, 0, 0 };

    internal static readonly int[] UsedKeyEnd = { 8, 16, 56, 32, 32, 16, 16 };

    /// <summary>块内彼此独立的加密单元长度(字节):槽 5 的 16 字节块是两个互不影响的 8 字节块,其余整块是一个单元。</summary>
    internal static readonly int[] IndependentUnit = { 4, 16, 8, 16, 16, 8, 8 };

    internal static IEnumerable<object[]> SlotData()
    {
        for (int slot = 0; slot < 7; slot++)
        {
            yield return new object[] { slot };
        }
    }

    internal static IEnumerable<object[]> SlotAndDirectionData()
    {
        for (int slot = 0; slot < 7; slot++)
        {
            yield return new object[] { slot, true };
            yield return new object[] { slot, false };
        }
    }

    internal static byte[] Hex(string text)
    {
        return Convert.FromHexString(text);
    }

    internal static string HexOf(byte[] data)
    {
        return Convert.ToHexStringLower(data);
    }

    /// <summary>读 Golden 目录下以制表符分列的文本表,跳过空行与 # 开头的注释行;列数不对直接判失败。</summary>
    internal static List<string[]> ReadRows(string fileName, int columns)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", fileName);
        List<string[]> rows = new List<string[]>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] cols = line.Split('\t');
            Assert.True(cols.Length == columns, fileName + ":列数应为 " + columns + ",实际 " + cols.Length);
            rows.Add(cols);
        }
        return rows;
    }

    /// <summary>右侧补零到 length 字节(旧金标准文件的密钥列对大切片只写了开头的字节,其余是零)。</summary>
    internal static byte[] PadRight(byte[] data, int length)
    {
        Assert.True(data.Length <= length, "密钥列比切片还长");
        byte[] padded = new byte[length];
        Array.Copy(data, padded, data.Length);
        return padded;
    }

    internal static byte[] Repeat(byte[] unit, int times)
    {
        byte[] result = new byte[unit.Length * times];
        for (int i = 0; i < times; i++)
        {
            Array.Copy(unit, 0, result, i * unit.Length, unit.Length);
        }
        return result;
    }

    internal static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Array.Copy(first, 0, result, 0, first.Length);
        Array.Copy(second, 0, result, first.Length, second.Length);
        return result;
    }

    internal static byte[] Slice(byte[] data, int start, int count)
    {
        byte[] part = new byte[count];
        Array.Copy(data, start, part, 0, count);
        return part;
    }

    /// <summary>固定种子的合成数据:同一个种子永远给出同一串字节,测试因此可重现。</summary>
    internal static byte[] SynthBytes(int seed, int length)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    internal static BlockCipher Keyed(int slot, byte[] key, int offset = 0)
    {
        BlockCipher cipher = CipherSlots.Create(slot);
        cipher.SetKey(key, offset);
        return cipher;
    }

    internal static byte[] Encrypt(BlockCipher cipher, byte[] plain)
    {
        byte[] output = new byte[plain.Length];
        cipher.EncryptBlock(plain, 0, output, 0);
        return output;
    }

    internal static byte[] Decrypt(BlockCipher cipher, byte[] encrypted)
    {
        byte[] output = new byte[encrypted.Length];
        cipher.DecryptBlock(encrypted, 0, output, 0);
        return output;
    }

    internal static int DifferentBits(byte[] a, byte[] b, int start, int count)
    {
        int total = 0;
        for (int i = start; i < start + count; i++)
        {
            total += System.Numerics.BitOperations.PopCount((uint)(a[i] ^ b[i]));
        }
        return total;
    }
}
