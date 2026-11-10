using System;
using System.Buffers.Binary;

namespace Dnf.Net.Session
{
    /// <summary>
    /// Square 128 位、8 轮；密钥 key32 前 16 字节。
    /// 密钥扩展与 CSquare::GenerateRoundKeys 一致；加密用 work 调度(9×16 字节)，解密用 rkeys 紧凑序。
    /// </summary>
    internal static class AntibotSquareCore
    {
        private static readonly uint[] RoundConst =
        {
            1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80,
        };

        private static uint[] _workKeys;
        private static uint[] _compactKeys;

        internal static void PrepareKey(byte[] key32)
        {
            uint k0 = BinaryPrimitives.ReadUInt32LittleEndian(key32);
            uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(key32.AsSpan(4));
            uint k2 = BinaryPrimitives.ReadUInt32LittleEndian(key32.AsSpan(8));
            uint k3 = BinaryPrimitives.ReadUInt32LittleEndian(key32.AsSpan(12));
            ExpandKeys(k0, k1, k2, k3, out _workKeys, out _compactKeys);
        }

        internal static void EncryptBlock(byte[] buf, int offset)
        {
            uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
            uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4));
            uint w2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8));
            uint w3 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12));
            AntibotSquareNative.Encrypt(ref w0, ref w1, ref w2, ref w3, _workKeys);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), w0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), w1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), w2);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), w3);
        }

        internal static void DecryptBlock(byte[] buf, int offset)
        {
            uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
            uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4));
            uint w2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8));
            uint w3 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12));
            AntibotSquareNative.Decrypt(ref w0, ref w1, ref w2, ref w3, _compactKeys);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), w0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), w1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), w2);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), w3);
        }

        private static void ExpandKeys(uint k0, uint k1, uint k2, uint k3, out uint[] work, out uint[] compact)
        {
            work = new uint[36];
            compact = new uint[36];
            work[0] = k0;
            work[1] = k1;
            work[2] = k2;
            work[3] = k3;
            int pos = 0;
            int compactSlot = 7;
            for (int i = 0; i < 8; i++)
            {
                uint w0 = work[pos];
                uint w1 = work[pos + 1];
                uint w2 = work[pos + 2];
                uint w3 = work[pos + 3];
                uint t = Ror32(w3, 8) ^ w0 ^ RoundConst[i];
                uint n0 = t;
                uint n1 = t ^ w1;
                uint n2 = n1 ^ w2;
                uint n3 = n2 ^ w3;
                compact[compactSlot * 4] = n0;
                compact[compactSlot * 4 + 1] = n1;
                compact[compactSlot * 4 + 2] = n2;
                compact[compactSlot * 4 + 3] = n3;
                compactSlot--;
                work[pos + 4] = n0;
                work[pos + 5] = n1;
                work[pos + 6] = n2;
                work[pos + 7] = n3;
                SquareThetaInPlace(work, pos);
                pos += 4;
            }

            compact[32] = work[0];
            compact[33] = work[1];
            compact[34] = work[2];
            compact[35] = work[3];
        }

        private static void SquareThetaInPlace(uint[] words, int off)
        {
            words[off] = TransformWord(words[off]);
            words[off + 1] = TransformWord(words[off + 1]);
            words[off + 2] = TransformWord(words[off + 2]);
            words[off + 3] = TransformWord(words[off + 3]);
        }

        private static uint TransformWord(uint w)
        {
            uint b0 = w & 0xFF;
            uint b1 = (w >> 8) & 0xFF;
            uint b2 = (w >> 16) & 0xFF;
            uint b3 = (w >> 24) & 0xFF;
            uint edx = AntibotSquareTables.Theta1[b1];
            edx = Ror32(edx, 24) ^ AntibotSquareTables.Theta0[b0];
            uint ecx = AntibotSquareTables.Theta2[b2];
            uint eax = AntibotSquareTables.Theta3[b3];
            ecx = Ror32(ecx, 16);
            eax = Ror32(eax, 8);
            ecx ^= eax;
            edx ^= ecx;
            return edx;
        }

        private static uint Ror32(uint x, int n)
        {
            n &= 31;
            return (x >> n) | (x << (32 - n));
        }
    }
}
