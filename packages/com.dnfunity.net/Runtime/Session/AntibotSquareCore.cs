using System;
using System.Buffers.Binary;

namespace Dnf.Net.Session
{
    /// <summary>
    /// Square 单块算法（128 位分组、8 轮）：密钥取 key32 的前 16 字节，按四个小端字使用。
    /// 一个实例对应一把密钥，建好后可反复加解密整块；实例内的调度表建成后不再改动。
    /// </summary>
    /// <remarks>
    /// 调度表各有 9 个 4 字块：
    /// 加密表第 0..7 块是各轮密钥块做过 θ 的结果，第 8 块（末轮）保持原样；
    /// 解密表第 0..7 块是各轮密钥块的原样倒序（第 0 块为最后一轮），第 8 块是第 0 个密钥块做过 θ 的结果。
    /// </remarks>
    internal sealed class AntibotSquareCore
    {
        private readonly uint[] _encryptKeys = new uint[36];
        private readonly uint[] _decryptKeys = new uint[36];

        internal AntibotSquareCore(byte[] key32)
        {
            uint[] raw = new uint[36];
            for (int i = 0; i < 4; i++)
            {
                raw[i] = BinaryPrimitives.ReadUInt32LittleEndian(key32.AsSpan(4 * i));
            }

            // 每一步由当前块推出下一块：首字取 (末字循环右移 8 位) ^ 首字 ^ 轮常数，其余字依次累异或。
            uint roundConstant = 1;
            for (int round = 0; round < 8; round++)
            {
                int cur = round * 4;
                uint first = RotateRight(raw[cur + 3], 8) ^ raw[cur] ^ roundConstant;
                raw[cur + 4] = first;
                raw[cur + 5] = first ^ raw[cur + 1];
                raw[cur + 6] = raw[cur + 5] ^ raw[cur + 2];
                raw[cur + 7] = raw[cur + 6] ^ raw[cur + 3];
                roundConstant <<= 1;
            }

            for (int i = 0; i < 32; i++)
            {
                _encryptKeys[i] = Theta(raw[i]);
            }

            for (int i = 32; i < 36; i++)
            {
                _encryptKeys[i] = raw[i];
            }

            for (int block = 0; block < 8; block++)
            {
                for (int j = 0; j < 4; j++)
                {
                    _decryptKeys[block * 4 + j] = raw[(8 - block) * 4 + j];
                }
            }

            for (int j = 0; j < 4; j++)
            {
                _decryptKeys[32 + j] = _encryptKeys[j];
            }
        }

        /// <summary>原地加密 buf 中 offset 起的 16 字节。</summary>
        internal void EncryptBlock(byte[] buf, int offset)
        {
            Crypt(buf, offset, _encryptKeys, AntibotSquareTables.Encrypt, AntibotSquareTables.SBox);
        }

        /// <summary>原地解密 buf 中 offset 起的 16 字节。</summary>
        internal void DecryptBlock(byte[] buf, int offset)
        {
            Crypt(buf, offset, _decryptKeys, AntibotSquareTables.Decrypt, AntibotSquareTables.InvSBox);
        }

        /// <summary>
        /// 加密与解密同构：先与第 0 块密钥异或，再做 7 轮“合并查表 + 轮密钥”，
        /// 末轮只做字节代换并转置（输出字的第 i 个字节取自第 i 个状态字的同序字节），再与第 8 块密钥异或。
        /// </summary>
        private static void Crypt(byte[] buf, int offset, uint[] keys, uint[][] tables, byte[] box)
        {
            uint a0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset)) ^ keys[0];
            uint a1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4)) ^ keys[1];
            uint a2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8)) ^ keys[2];
            uint a3 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12)) ^ keys[3];

            for (int round = 1; round < 8; round++)
            {
                int k = round * 4;
                uint b0 = Lookup(tables, a0, a1, a2, a3, 0) ^ keys[k];
                uint b1 = Lookup(tables, a0, a1, a2, a3, 8) ^ keys[k + 1];
                uint b2 = Lookup(tables, a0, a1, a2, a3, 16) ^ keys[k + 2];
                uint b3 = Lookup(tables, a0, a1, a2, a3, 24) ^ keys[k + 3];
                a0 = b0;
                a1 = b1;
                a2 = b2;
                a3 = b3;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), Transpose(box, a0, a1, a2, a3, 0) ^ keys[32]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), Transpose(box, a0, a1, a2, a3, 8) ^ keys[33]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), Transpose(box, a0, a1, a2, a3, 16) ^ keys[34]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), Transpose(box, a0, a1, a2, a3, 24) ^ keys[35]);
        }

        /// <summary>取四个状态字在 shift 位置上的字节，分别查四张轮表后异或。</summary>
        private static uint Lookup(uint[][] tables, uint a0, uint a1, uint a2, uint a3, int shift)
        {
            return tables[0][(a0 >> shift) & 0xFF]
                ^ tables[1][(a1 >> shift) & 0xFF]
                ^ tables[2][(a2 >> shift) & 0xFF]
                ^ tables[3][(a3 >> shift) & 0xFF];
        }

        /// <summary>末轮：四个状态字在 shift 位置上的字节各自过 S 盒，依次放进输出字的第 0..3 字节。</summary>
        private static uint Transpose(byte[] box, uint a0, uint a1, uint a2, uint a3, int shift)
        {
            return (uint)box[(a0 >> shift) & 0xFF]
                | ((uint)box[(a1 >> shift) & 0xFF] << 8)
                | ((uint)box[(a2 >> shift) & 0xFF] << 16)
                | ((uint)box[(a3 >> shift) & 0xFF] << 24);
        }

        /// <summary>整字做 θ：只用一张 θ 表，其余三个字节位置靠循环移位得到。</summary>
        private static uint Theta(uint word)
        {
            uint[] table = AntibotSquareTables.Theta;
            return table[word & 0xFF]
                ^ RotateRight(table[(word >> 8) & 0xFF], 24)
                ^ RotateRight(table[(word >> 16) & 0xFF], 16)
                ^ RotateRight(table[word >> 24], 8);
        }

        private static uint RotateRight(uint value, int bits)
        {
            return (value >> bits) | (value << (32 - bits));
        }
    }
}
