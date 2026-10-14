using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dnf.Data
{
    /// <summary>
    /// PVF 的逐字解密。目录树与每个文件相同:按 32 位小端字,out = ROR32(word ^ crc ^ 0x81A79011, 6)。
    /// 目录树用头里的 dirTreeCrc,文件用自己目录项里的 crc(它只是密钥,不是内容校验和)。
    /// </summary>
    public static class PvfCipher
    {
        public const uint XorConstant = 0x81A79011u;

        /// <summary>原地解密。末尾不足 4 字节的部分保持原样。</summary>
        public static void DecryptInPlace(Span<byte> data, uint crc)
        {
            uint key = crc ^ XorConstant;
            int words = data.Length >> 2;
            if (BitConverter.IsLittleEndian)
            {
                Span<uint> w = MemoryMarshal.Cast<byte, uint>(data.Slice(0, words << 2));
                for (int i = 0; i < w.Length; i++)
                {
                    uint x = w[i] ^ key;
                    w[i] = (x >> 6) | (x << 26);
                }
            }
            else
            {
                for (int i = 0; i < words; i++)
                {
                    Span<byte> cell = data.Slice(i << 2, 4);
                    uint x = BinaryPrimitives.ReadUInt32LittleEndian(cell) ^ key;
                    BinaryPrimitives.WriteUInt32LittleEndian(cell, (x >> 6) | (x << 26));
                }
            }
        }

        /// <summary>解密的逆运算。库本身不需要它,测试与工具用来构造合成数据。</summary>
        public static void EncryptInPlace(Span<byte> data, uint crc)
        {
            uint key = crc ^ XorConstant;
            int words = data.Length >> 2;
            for (int i = 0; i < words; i++)
            {
                Span<byte> cell = data.Slice(i << 2, 4);
                uint p = BinaryPrimitives.ReadUInt32LittleEndian(cell);
                BinaryPrimitives.WriteUInt32LittleEndian(cell, ((p << 6) | (p >> 26)) ^ key);
            }
        }
    }
}
