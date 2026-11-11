using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// MMB 128 位块变体(Daemen MMB 结构)；密钥 key32 前 16 字节。
    /// 6 次 γ(f)+θ 与 7 次轮密钥异或(末轮仅异或无 f)；余量 <c>len%16</c> 走 Seattos。
    /// <para>
    /// 线程安全：展开出的轮密钥是每次 <see cref="Transform"/> 调用自己的局部数组，显式传给逐块函数；
    /// 这两个类里没有任何可变的静态状态(静态字段全是只读常量表)，所以不同密钥的调用可以任意并发，结果与串行逐字节一致。
    /// (曾经把轮密钥放在一个静态可变字段里，并发时不同密钥互相覆盖，解出乱码，拆信封时表现为 CRC 对不上。)
    /// </para>
    /// </summary>
    internal static class AntibotMmb
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            uint[] roundKey = AntibotMmbCore.PrepareKey(key32);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    AntibotMmbCore.EncryptBlock(roundKey, buf, off);
                }
                else
                {
                    AntibotMmbCore.DecryptBlock(roundKey, buf, off);
                }

                off += 16;
            }

            int rest = buf.Length - off;
            if (rest > 0)
            {
                ApplySeattosSuffix(key32, encrypt, buf, off, rest);
            }
        }

        private static void ApplySeattosSuffix(byte[] key32, bool encrypt, byte[] buf, int offset, int count)
        {
            byte[] tail = new byte[count];
            System.Buffer.BlockCopy(buf, offset, tail, 0, count);
            AntibotSeattos.Transform(key32, encrypt, tail);
            System.Buffer.BlockCopy(tail, 0, buf, offset, count);
        }
    }

    internal static class AntibotMmbCore
    {
        private const uint MixConst0 = 0x33CFC738;
        private const uint MixConst1 = 0x19860719;
        private const uint ParityFix = 0x2AAAAAAA;

        private static readonly uint[] GammaF =
        {
            0x025F1CDB, 0x04BE39B6, 0x12F8E6D8, 0x2F8E6D81,
        };

        private static readonly uint[] GammaG =
        {
            0x0DAD4694, 0x06D6A34A, 0x81B5A8D2, 0x281B5A8D,
        };

        /// <summary>CMmb::Init 拷入的 64 字表(索引 ror(esi,31)&amp;0x3f)。</summary>
        private static readonly uint[] KeyMixTable =
        {
            0xFA5D7CEC, 0x734533F8, 0x484EA4B2, 0x2BF0E46B,
            0xA853884B, 0xCBE7DEE0, 0x976DF92A, 0xE2B17575,
            0xF9BC1EFD, 0xF8ADC573, 0x2E5B6EA9, 0x1C5A4FFD,
            0x3DF27762, 0x2D7BF0BD, 0x039D26D6, 0x0A937D09,
            0x01052BC2, 0x8AEEEA1C, 0xFB7D3EBE, 0x2449A182,
            0x17B71BA3, 0xC6E68955, 0x74C7A0CD, 0xE1B88745,
            0x4D71958C, 0x149CEB88, 0x5344E0B9, 0xC3F0A21F,
            0xA348429B, 0xF672C8CD, 0xD01FA55C, 0x4C6FD3DF,
            0xC05B564E, 0xE0D81861, 0xA3B3EAAC, 0x17AE4A08,
            0x9D8DF3BF, 0x1353CBFE, 0x25C0F1C8, 0xD52818EC,
            0x1BA102B0, 0xC2FDC394, 0x9AB0505E, 0xF8D5F8CB,
            0x708749DA, 0x34C5D60B, 0xD1C046B0, 0x4C8E4672,
            0x0E236A84, 0x5A36C5AB, 0xDC9B9EB5, 0x0ADB5718,
            0x57D27CC5, 0xC4ECAEFD, 0xB57C0D9E, 0x764E92EA,
            0x4CA01ED6, 0xA7314405, 0xEE5E6B4B, 0xF9560189,
            0xC50CD4D5, 0xBFE0AFD8, 0x8322F984, 0x65601B60,
        };

        private static readonly int[][] EncryptXor =
        {
            new[] { 0, 1, 2, 3 },
            new[] { 1, 2, 3, 0 },
            new[] { 2, 3, 0, 1 },
            new[] { 3, 0, 1, 2 },
            new[] { 0, 1, 2, 3 },
            new[] { 1, 2, 3, 0 },
            new[] { 2, 3, 0, 1 },
        };

        private static readonly int[][] DecryptXor =
        {
            new[] { 2, 3, 0, 1 },
            new[] { 1, 2, 3, 0 },
            new[] { 0, 1, 2, 3 },
            new[] { 3, 0, 1, 2 },
            new[] { 2, 3, 0, 1 },
            new[] { 1, 2, 3, 0 },
            new[] { 0, 1, 2, 3 },
        };

        /// <summary>由 key32 的前 16 字节展开出 4 个字的轮密钥；每次返回新数组，交给调用方持有，不存进任何静态字段。</summary>
        internal static uint[] PrepareKey(byte[] key32)
        {
            uint k0 = ReadLe32(key32, 0);
            uint k1 = ReadLe32(key32, 4);
            uint k2 = ReadLe32(key32, 8);
            uint k3 = ReadLe32(key32, 12);
            return ExpandKey(k0, k1, k2, k3);
        }

        internal static void EncryptBlock(uint[] roundKey, byte[] buf, int offset)
        {
            uint[] s = ReadBlock(buf, offset);
            for (int r = 0; r < 6; r++)
            {
                AddRoundKey(s, roundKey, EncryptXor[r]);
                OpF(s);
            }

            AddRoundKey(s, roundKey, EncryptXor[6]);
            WriteBlock(buf, offset, s);
        }

        internal static void DecryptBlock(uint[] roundKey, byte[] buf, int offset)
        {
            uint[] s = ReadBlock(buf, offset);
            for (int r = 0; r < 7; r++)
            {
                AddRoundKey(s, roundKey, DecryptXor[r]);
                if (r < 6)
                {
                    OpG(s);
                }
            }

            WriteBlock(buf, offset, s);
        }

        private static uint[] ExpandKey(uint k0, uint k1, uint k2, uint k3)
        {
            uint edx = k0 - MixConst0;
            uint edi = Ror32(edx, 0x1B) + k1;
            uint v0 = edx;
            edx = k2;
            uint ecx = Ror32(edi, 0x19) + edx;
            uint v1 = ecx;
            uint esi = Ror32(ecx, 0x15) + k3;
            int idx = (int)(Ror32(esi, 0x1F) & 0x3F);
            uint eax = Ror32(esi, 0x1E) + KeyMixTable[idx];
            eax ^= MixConst1;
            eax = Ror32(eax, 0x1F) ^ v0;
            uint edx2 = Ror32(eax, 0x1E) ^ edi;
            ecx = Ror32(edx2, 0x1D) ^ v1;
            uint w0 = eax;
            uint w1 = eax ^ edx2;
            uint w2 = w1 ^ ecx;
            uint w3 = esi ^ Ror32(ecx, 0x1C) ^ w2;
            return new[] { w0, w1, w2, w3 };
        }

        private static void OpF(uint[] s)
        {
            for (int i = 0; i < 4; i++)
            {
                s[i] = ModMult(s[i], GammaF[i]);
            }

            if ((s[0] & 1) != 0)
            {
                s[0] ^= ParityFix;
            }

            uint eax = s[3] ^ s[1];
            uint edx = s[0] ^ s[2];
            s[0] ^= eax;
            s[1] ^= edx;
            s[2] ^= eax;
            s[3] ^= edx;
        }

        private static void OpG(uint[] s)
        {
            uint s0 = s[0];
            uint s1 = s[1];
            uint s2 = s[2];
            uint s3 = s[3];
            uint edx = s3 ^ s1;
            uint ecx = s0 ^ s2;
            uint eax = s0 ^ edx;
            s2 ^= edx;
            s1 ^= ecx;
            s3 ^= ecx;
            s0 = eax;
            if ((s0 & 1) != 0)
            {
                s0 ^= ParityFix;
            }

            s[0] = s0;
            s[1] = s1;
            s[2] = s2;
            s[3] = s3;
            for (int i = 0; i < 4; i++)
            {
                s[i] = ModMult(s[i], GammaG[i]);
            }
        }

        private static void AddRoundKey(uint[] s, uint[] roundKey, int[] map)
        {
            for (int i = 0; i < 4; i++)
            {
                s[i] ^= roundKey[map[i]];
            }
        }

        private static uint ModMult(uint a, uint b)
        {
            uint ecx = a & 0xFFFF;
            uint eax = b & 0xFFFF;
            uint edi = a;
            uint edx = ecx * eax;
            edi >>= 16;
            eax = eax * edi;
            uint esi = edx;
            edx &= 0xFFFF;
            esi >>= 16;
            eax += esi;
            esi = eax;
            eax &= 0xFFFF;
            esi >>= 16;
            edx += esi;
            esi = b >> 16;
            ecx = ecx * esi;
            edi = edi * esi;
            ecx += eax;
            eax = edx;
            eax >>= 16;
            edx &= 0xFFFF;
            ecx += eax;
            eax = ecx;
            ecx &= 0xFFFF;
            eax >>= 16;
            eax = edi + eax;
            edx += eax;
            eax = edx;
            edx &= 0xFFFF;
            eax >>= 16;
            ecx += eax;
            eax = ecx;
            ecx >>= 16;
            edx += ecx;
            eax <<= 16;
            return eax + edx;
        }

        private static uint Ror32(uint x, int n)
        {
            n &= 31;
            return (x >> n) | (x << (32 - n));
        }

        private static uint[] ReadBlock(byte[] buf, int offset)
        {
            return new[]
            {
                ReadLe32(buf, offset),
                ReadLe32(buf, offset + 4),
                ReadLe32(buf, offset + 8),
                ReadLe32(buf, offset + 12),
            };
        }

        private static void WriteBlock(byte[] buf, int offset, uint[] w)
        {
            WriteLe32(buf, offset, w[0]);
            WriteLe32(buf, offset + 4, w[1]);
            WriteLe32(buf, offset + 8, w[2]);
            WriteLe32(buf, offset + 12, w[3]);
        }

        private static uint ReadLe32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        private static void WriteLe32(byte[] buf, int offset, uint v)
        {
            buf[offset] = (byte)v;
            buf[offset + 1] = (byte)(v >> 8);
            buf[offset + 2] = (byte)(v >> 16);
            buf[offset + 3] = (byte)(v >> 24);
        }
    }
}
