using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 原 TenTwofish32/24/16:标准 Twofish-128/192/256(ECB),小端字;makeKey 位长 0x80/0xc0/0x100。
    /// 整块按 16 字节循环;余 <c>len%16</c> 走 <see cref="AntibotSeattos"/>(<c>CTsLocalImp</c> 尾路径)。
    /// </summary>
    internal static class AntibotTwofish
    {
        internal static void Transform(byte[] key32, int keyBytes, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotTwofishCore ctx = AntibotTwofishCore.Schedule(key32, keyBytes);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    ctx.EncryptBlock(buf, off);
                }
                else
                {
                    ctx.DecryptBlock(buf, off);
                }

                off += 16;
            }

            AntibotRc6.ApplySeattosSuffix(key32, encrypt, buf);
        }
    }

    internal sealed class AntibotTwofishCore
    {
        private const int Rounds = 16;
        private const int MdsPoly = 0x169;
        private const int RsPoly = 0x14D;

        private static readonly int[][] Q0Tables =
        {
            new[] { 0x8, 0x1, 0x7, 0xD, 0x6, 0xF, 0x3, 0x2, 0x0, 0xB, 0x5, 0x9, 0xE, 0xC, 0xA, 0x4 },
            new[] { 0xE, 0xC, 0xB, 0x8, 0x1, 0x2, 0x3, 0x5, 0xF, 0x4, 0xA, 0x6, 0x7, 0x0, 0x9, 0xD },
            new[] { 0xB, 0xA, 0x5, 0xE, 0x6, 0xD, 0x9, 0x0, 0xC, 0x8, 0xF, 0x3, 0x2, 0x4, 0x7, 0x1 },
            new[] { 0xD, 0x7, 0xF, 0x4, 0x1, 0x2, 0x6, 0xE, 0x9, 0xB, 0x3, 0x0, 0x8, 0x5, 0xC, 0xA },
        };

        private static readonly int[][] Q1Tables =
        {
            new[] { 0x2, 0x8, 0xB, 0xD, 0xF, 0x7, 0x6, 0xE, 0x3, 0x1, 0x9, 0x4, 0x0, 0xA, 0xC, 0x5 },
            new[] { 0x1, 0xE, 0x2, 0xB, 0x4, 0xC, 0x3, 0x7, 0x6, 0xD, 0xA, 0x5, 0xF, 0x9, 0x0, 0x8 },
            new[] { 0x4, 0xC, 0x7, 0x5, 0x1, 0x6, 0x9, 0xA, 0x0, 0xE, 0xD, 0x8, 0x2, 0xB, 0x3, 0xF },
            new[] { 0xB, 0x9, 0x5, 0x1, 0xC, 0x3, 0xD, 0xE, 0x6, 0x4, 0x7, 0xF, 0x2, 0x0, 0x8, 0xA },
        };

        private static readonly int[][] Mds =
        {
            new[] { 0x01, 0xEF, 0x5B, 0x5B },
            new[] { 0x5B, 0xEF, 0xEF, 0x01 },
            new[] { 0xEF, 0x5B, 0x01, 0xEF },
            new[] { 0xEF, 0x01, 0xEF, 0x5B },
        };

        private static readonly int[][] Rs =
        {
            new[] { 0x01, 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E },
            new[] { 0xA4, 0x56, 0x82, 0xF3, 0x1E, 0xC6, 0x68, 0xE5 },
            new[] { 0x02, 0xA1, 0xFC, 0xC1, 0x47, 0xAE, 0x3D, 0x19 },
            new[] { 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E, 0x03 },
        };

        private static readonly int[][] QPlan =
        {
            new[] { 1, 1, 0, 0, 1 },
            new[] { 0, 1, 1, 0, 0 },
            new[] { 0, 0, 0, 1, 1 },
            new[] { 1, 0, 1, 1, 0 },
        };

        private static readonly byte[][] Q = { BuildQ(Q0Tables), BuildQ(Q1Tables) };

        private readonly uint[] _subKeys = new uint[8 + 2 * Rounds];
        private readonly uint[][] _sbox = { new uint[256], new uint[256], new uint[256], new uint[256] };

        internal static AntibotTwofishCore Schedule(byte[] key, int keyBytes)
        {
            AntibotTwofishCore ctx = new AntibotTwofishCore();
            ctx.Init(key, keyBytes);
            return ctx;
        }

        private void Init(byte[] key, int keyBytes)
        {
            int k = keyBytes >> 3;
            uint[] m = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                if (i * 4 + 4 <= keyBytes)
                {
                    m[i] = LoadLe(key, i * 4);
                }
            }

            uint[] me = { m[0], m[2], m[4], m[6] };
            uint[] mo = { m[1], m[3], m[5], m[7] };

            uint[] s = new uint[4];
            for (int i = 0; i < 4; i++)
            {
                uint word = 0;
                for (int j = 0; j < 4; j++)
                {
                    int acc = 0;
                    for (int r = 0; r < 8; r++)
                    {
                        int idx = 8 * i + r;
                        int kb = idx < keyBytes ? key[idx] : 0;
                        acc ^= GfMul(Rs[j][r], kb, RsPoly);
                    }

                    word |= (uint)acc << (8 * j);
                }

                s[i] = word;
            }

            uint[] sList = { s[3], s[2], s[1], s[0] };

            for (int i = 0; i < 8 + 2 * Rounds; i += 2)
            {
                uint rho = 0x01010101u;
                uint a = H((uint)i * rho, me);
                uint b = RotL(H((uint)(i + 1) * rho, mo), 8);
                _subKeys[i] = a + b;
                _subKeys[i + 1] = RotL(a + 2u * b, 9);
            }

            for (int j = 0; j < 4; j++)
            {
                for (int x = 0; x < 256; x++)
                {
                    _sbox[j][x] = MdsColumn(j, ChainByte(j, x, sList));
                }
            }

        }

        internal void EncryptBlock(byte[] buf, int off)
        {
            uint r0 = LoadLe(buf, off) ^ _subKeys[0];
            uint r1 = LoadLe(buf, off + 4) ^ _subKeys[1];
            uint r2 = LoadLe(buf, off + 8) ^ _subKeys[2];
            uint r3 = LoadLe(buf, off + 12) ^ _subKeys[3];
            for (int r = 0; r < Rounds; r++)
            {
                uint t0 = G(r0);
                uint t1 = G(RotL(r1, 8));
                uint f0 = t0 + t1 + _subKeys[2 * r + 8];
                uint f1 = t0 + 2u * t1 + _subKeys[2 * r + 9];
                uint n0 = RotR(r2 ^ f0, 1);
                uint n1 = RotL(r3, 1) ^ f1;
                r2 = r0;
                r3 = r1;
                r0 = n0;
                r1 = n1;
            }

            StoreLe(buf, off, r2 ^ _subKeys[4]);
            StoreLe(buf, off + 4, r3 ^ _subKeys[5]);
            StoreLe(buf, off + 8, r0 ^ _subKeys[6]);
            StoreLe(buf, off + 12, r1 ^ _subKeys[7]);
        }

        internal void DecryptBlock(byte[] buf, int off)
        {
            uint r2 = LoadLe(buf, off) ^ _subKeys[4];
            uint r3 = LoadLe(buf, off + 4) ^ _subKeys[5];
            uint r0 = LoadLe(buf, off + 8) ^ _subKeys[6];
            uint r1 = LoadLe(buf, off + 12) ^ _subKeys[7];
            for (int r = Rounds - 1; r >= 0; r--)
            {
                uint t0 = G(r2);
                uint t1 = G(RotL(r3, 8));
                uint f0 = t0 + t1 + _subKeys[2 * r + 8];
                uint f1 = t0 + 2u * t1 + _subKeys[2 * r + 9];
                uint p2 = RotL(r0, 1) ^ f0;
                uint p3 = RotR(r1 ^ f1, 1);
                r0 = r2;
                r1 = r3;
                r2 = p2;
                r3 = p3;
            }

            StoreLe(buf, off, r0 ^ _subKeys[0]);
            StoreLe(buf, off + 4, r1 ^ _subKeys[1]);
            StoreLe(buf, off + 8, r2 ^ _subKeys[2]);
            StoreLe(buf, off + 12, r3 ^ _subKeys[3]);
        }

        private uint G(uint x)
        {
            return _sbox[0][x & 0xFF] ^ _sbox[1][(x >> 8) & 0xFF] ^ _sbox[2][(x >> 16) & 0xFF] ^ _sbox[3][x >> 24];
        }

        private static uint H(uint x, uint[] l)
        {
            uint z = 0;
            for (int j = 0; j < 4; j++)
            {
                z ^= MdsColumn(j, ChainByte(j, (int)((x >> (8 * j)) & 0xFF), l));
            }

            return z;
        }

        private static int ChainByte(int j, int x, uint[] l)
        {
            int y = x;
            for (int stage = 0; stage < 4; stage++)
            {
                uint word = l[3 - stage];
                y = Q[QPlan[j][stage]][y] ^ (int)((word >> (8 * j)) & 0xFF);
            }

            return Q[QPlan[j][4]][y];
        }

        private static uint MdsColumn(int j, int y)
        {
            uint word = 0;
            for (int i = 0; i < 4; i++)
            {
                word |= (uint)GfMul(Mds[i][j], y, MdsPoly) << (8 * i);
            }

            return word;
        }

        private static int GfMul(int a, int b, int poly)
        {
            int result = 0;
            while (b != 0)
            {
                if ((b & 1) != 0)
                {
                    result ^= a;
                }

                a <<= 1;
                if ((a & 0x100) != 0)
                {
                    a ^= poly;
                }

                b >>= 1;
            }

            return result;
        }

        private static byte[] BuildQ(int[][] t)
        {
            byte[] q = new byte[256];
            for (int x = 0; x < 256; x++)
            {
                int a0 = x >> 4;
                int b0 = x & 15;
                int a1 = a0 ^ b0;
                int b1 = (a0 ^ Ror4(b0) ^ ((8 * a0) & 15)) & 15;
                int a2 = t[0][a1];
                int b2 = t[1][b1];
                int a3 = a2 ^ b2;
                int b3 = (a2 ^ Ror4(b2) ^ ((8 * a2) & 15)) & 15;
                int a4 = t[2][a3];
                int b4 = t[3][b3];
                q[x] = (byte)((b4 << 4) | a4);
            }

            return q;
        }

        private static int Ror4(int x)
        {
            return ((x >> 1) | (x << 3)) & 15;
        }

        private static uint LoadLe(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void StoreLe(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static uint RotL(uint v, int n)
        {
            n &= 31;
            return (v << n) | (v >> (32 - n));
        }

        private static uint RotR(uint v, int n)
        {
            return RotL(v, 32 - n);
        }
    }
}
