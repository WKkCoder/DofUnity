using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 4:标准 Twofish-256(ECB 单块,小端字),块长 16 字节,切片 32 字节全部参与(本机模拟器逐字节翻转实测;
    /// 原版 twofish_setup 0x080c4994 + twofish_ecb_encrypt 0x080c4e48,经 CNCryptoTwoFish::Initialize 0x0809d47c 取切片全长 32 字节)。
    /// 本机模拟器对 64 条合成输入、论文里的 256 位公开向量与全零密钥向量逐位一致,没有变体(E-263 只验证过槽 1)。
    /// 公开算法常量(Twofish 规格,Schneier 等 1998):
    ///  * q0、q1 置换:由 4 位小表按规格的"两层 4 位混合"合成,不贴 256 字节表;
    ///  * MDS 矩阵(GF(2^8) 模 x^8+x^6+x^5+x^3+1,即 0x169)、RS 矩阵(GF(2^8) 模 x^8+x^6+x^3+x^2+1,即 0x14D);
    ///  * 密钥调度常量 ρ = 0x01010101,PHT、白化与 16 轮结构。
    /// 每把密钥在 SetKey 时预算出 4 张"含 MDS 的密钥相关 S 表",轮函数查表即可;对象设好密钥后只读,可并发加解密。
    /// </summary>
    public sealed class TwofishCipher : BlockCipher
    {
        private const int Rounds = 16;
        private const int MdsPoly = 0x169;
        private const int RsPoly = 0x14D;

        // 公开算法常量:构造 q0 与 q1 的 4 位小表(每个 q 四张,各 16 项)。
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

        // 公开算法常量:MDS 矩阵(行 i,列 j)与 RS 矩阵。
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

        // 每个字节位置在 4 个阶段(依次用 L3、L2、L1、L0)与收尾处各用 q0 还是 q1(0 = q0,1 = q1);k = 4 时的规格图。
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
        private bool _keyed;

        public override int Slot => 4;

        public override int BlockSize => 16;

        public override int KeyLength => 32;

        public override void SetKey(byte[] key, int offset)
        {
            SlotKit0To6.CheckKey(key, offset, KeyLength);
            uint[] m = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                m[i] = SlotKit0To6.LoadLe32(key, offset + 4 * i);
            }
            uint[] me = { m[0], m[2], m[4], m[6] };
            uint[] mo = { m[1], m[3], m[5], m[7] };

            // S 向量:每 8 字节密钥乘 RS 矩阵得到一个字;g 的 h 函数里按 S3、S2、S1、S0 的顺序(规格里 S = (S3, S2, S1, S0))取用。
            uint[] s = new uint[4];
            for (int i = 0; i < 4; i++)
            {
                uint word = 0;
                for (int j = 0; j < 4; j++)
                {
                    int acc = 0;
                    for (int r = 0; r < 8; r++)
                    {
                        acc ^= GfMul(Rs[j][r], key[offset + 8 * i + r], RsPoly);
                    }
                    word |= (uint)acc << (8 * j);
                }
                s[i] = word;
            }
            uint[] sList = { s[3], s[2], s[1], s[0] };

            // 子密钥:A = h(2iρ, Me),B = ROL(h((2i+1)ρ, Mo), 8);K[2i] = A + B,K[2i+1] = ROL(A + 2B, 9)。
            for (int i = 0; i < 8 + 2 * Rounds; i += 2)
            {
                uint rho = 0x01010101u;
                uint a = H((uint)i * rho, me);
                uint b = SlotKit0To6.RotL(H((uint)(i + 1) * rho, mo), 8);
                _subKeys[i] = a + b;
                _subKeys[i + 1] = SlotKit0To6.RotL(a + 2u * b, 9);
            }

            // 含 MDS 的密钥相关 S 表:g(x) = 四张表按字节查表后异或。
            for (int j = 0; j < 4; j++)
            {
                for (int x = 0; x < 256; x++)
                {
                    _sbox[j][x] = MdsColumn(j, ChainByte(j, x, sList));
                }
            }
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint r0 = SlotKit0To6.LoadLe32(input, inOffset) ^ _subKeys[0];
            uint r1 = SlotKit0To6.LoadLe32(input, inOffset + 4) ^ _subKeys[1];
            uint r2 = SlotKit0To6.LoadLe32(input, inOffset + 8) ^ _subKeys[2];
            uint r3 = SlotKit0To6.LoadLe32(input, inOffset + 12) ^ _subKeys[3];
            for (int r = 0; r < Rounds; r++)
            {
                uint t0 = G(r0);
                uint t1 = G(SlotKit0To6.RotL(r1, 8));
                uint f0 = t0 + t1 + _subKeys[2 * r + 8];
                uint f1 = t0 + 2u * t1 + _subKeys[2 * r + 9];
                uint n0 = SlotKit0To6.RotR(r2 ^ f0, 1);
                uint n1 = SlotKit0To6.RotL(r3, 1) ^ f1;
                r2 = r0;
                r3 = r1;
                r0 = n0;
                r1 = n1;
            }
            // 撤销最后一轮的交换并做输出白化:C0 = R2 ^ K4,C1 = R3 ^ K5,C2 = R0 ^ K6,C3 = R1 ^ K7。
            SlotKit0To6.StoreLe32(output, outOffset, r2 ^ _subKeys[4]);
            SlotKit0To6.StoreLe32(output, outOffset + 4, r3 ^ _subKeys[5]);
            SlotKit0To6.StoreLe32(output, outOffset + 8, r0 ^ _subKeys[6]);
            SlotKit0To6.StoreLe32(output, outOffset + 12, r1 ^ _subKeys[7]);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            // 先撤销输出白化,得到第 16 轮之后的 (R0, R1, R2, R3)。
            uint r2 = SlotKit0To6.LoadLe32(input, inOffset) ^ _subKeys[4];
            uint r3 = SlotKit0To6.LoadLe32(input, inOffset + 4) ^ _subKeys[5];
            uint r0 = SlotKit0To6.LoadLe32(input, inOffset + 8) ^ _subKeys[6];
            uint r1 = SlotKit0To6.LoadLe32(input, inOffset + 12) ^ _subKeys[7];
            for (int r = Rounds - 1; r >= 0; r--)
            {
                // 本轮的 F 只依赖本轮输入的 (R0, R1),也就是现在的 (R2, R3)。
                uint t0 = G(r2);
                uint t1 = G(SlotKit0To6.RotL(r3, 8));
                uint f0 = t0 + t1 + _subKeys[2 * r + 8];
                uint f1 = t0 + 2u * t1 + _subKeys[2 * r + 9];
                uint p2 = SlotKit0To6.RotL(r0, 1) ^ f0;
                uint p3 = SlotKit0To6.RotR(r1 ^ f1, 1);
                r0 = r2;
                r1 = r3;
                r2 = p2;
                r3 = p3;
            }
            SlotKit0To6.StoreLe32(output, outOffset, r0 ^ _subKeys[0]);
            SlotKit0To6.StoreLe32(output, outOffset + 4, r1 ^ _subKeys[1]);
            SlotKit0To6.StoreLe32(output, outOffset + 8, r2 ^ _subKeys[2]);
            SlotKit0To6.StoreLe32(output, outOffset + 12, r3 ^ _subKeys[3]);
        }

        private uint G(uint x)
        {
            return _sbox[0][x & 0xFF] ^ _sbox[1][(x >> 8) & 0xFF] ^ _sbox[2][(x >> 16) & 0xFF] ^ _sbox[3][x >> 24];
        }

        /// <summary>规格的 h 函数(k = 4):x 的 4 个字节各过一条 q 链,再整体乘 MDS;l 依次是 L0 到 L3。</summary>
        private static uint H(uint x, uint[] l)
        {
            uint z = 0;
            for (int j = 0; j < 4; j++)
            {
                z ^= MdsColumn(j, ChainByte(j, (int)((x >> (8 * j)) & 0xFF), l));
            }
            return z;
        }

        /// <summary>第 j 个字节的 q 链:四个阶段依次是 "过 q、异或 L3 的该字节"、L2、L1、L0,最后再过一次 q。</summary>
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

        /// <summary>MDS 矩阵第 j 列乘以字节 y,得到一个 32 位字(第 i 个字节是 MDS[i][j]·y)。</summary>
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
    }
}
